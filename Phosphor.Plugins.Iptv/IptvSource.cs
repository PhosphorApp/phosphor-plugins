using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Iptv;

/// <summary>
/// A configured IPTV source instance. Downloads the iptv-org catalog (cached to the instance dir),
/// exposes it for browse (by country and/or category), free-text search, and live playback, and
/// supports per-channel favorites.
/// </summary>
/// <remarks>
/// Pure data producer — no UI, no host internals. Every channel is a continuous live stream, so
/// items and resolved streams are marked <see cref="SourceItem.IsLiveStream"/>. The catalog is built
/// lazily on first use (or by an explicit "Rescan"), cached on disk, and guarded by a lock.
/// </remarks>
public sealed class IptvSource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IPlayableResolver, IRefreshable,
    IFavoritable, IFavoriteCapture, IReplayableById, IHideable, IPlaybackReportable, IPlaybackSuccessReportable
{
    // Durable category-id scheme (see SourceCategory.CategoryId): a node must be actionable from its
    // id alone, so we encode the grouping axis + key into the id.
    private const string Root = "root";                 // the single top-level IPTV tile
    private const string RootCountry = "root:country";  // the "By Country" sub-node (only when both axes shown)
    private const string RootCategory = "root:category"; // the "By Category" sub-node (only when both axes shown)
    private const string RootFavorites = "root:favorites";
    private const string CountryPrefix = "country:";
    private const string CategoryPrefix = "category:";

    private const string CacheFileName = "catalog.json";
    private const int CacheSchemaVersion = 1;

    private readonly object _gate = new();
    private IptvCatalog? _catalog;
    private DateTimeOffset? _catalogSavedUtc;

    private IPluginHost? _host;
    private IptvApiClient? _api;

    private string _organizeBy = IptvSourceProvider.OrganizeByBoth;
    private bool _includeNsfw;
    private int _cacheMaxAgeHours = 24;

    // Hidden groups (whole countries / categories the user suppressed) keyed by durable node id
    // (e.g. "country:France", "category:News"). Persisted to the instance dir.
    private readonly object _hideGate = new();
    private HashSet<string>? _hiddenCache;

    // Ids the host reported as having failed playback (dead/geo-blocked/temporarily-offline streams).
    // NOT hidden — the channel stays visible and playable, badged with ⊘ so the user can retry; a
    // successful play clears its id. Persisted to the instance dir.
    private readonly object _deadGate = new();
    private HashSet<string>? _deadCache;

    public IptvSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => IptvSourceProvider.IptvTypeId;
    public string DisplayName { get; set; } = "IPTV";

    /// <summary>The catalog is fetched from a public URL, so the source is always ready to operate.</summary>
    public bool IsConfigured => true;

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        _api = new IptvApiClient(host.HttpClient, (lvl, msg) => host.Log(lvl, msg));
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _organizeBy = Get(values, IptvSourceProvider.KeyOrganizeBy) switch
        {
            var v when string.Equals(v, IptvSourceProvider.OrganizeByCountry, StringComparison.OrdinalIgnoreCase)
                => IptvSourceProvider.OrganizeByCountry,
            var v when string.Equals(v, IptvSourceProvider.OrganizeByCategory, StringComparison.OrdinalIgnoreCase)
                => IptvSourceProvider.OrganizeByCategory,
            _ => IptvSourceProvider.OrganizeByBoth,
        };
        _includeNsfw = bool.TryParse(Get(values, IptvSourceProvider.KeyIncludeNsfw), out var n) && n;
        _cacheMaxAgeHours = int.TryParse(Get(values, IptvSourceProvider.KeyCacheMaxAgeHours), out var h) && h >= 0
            ? h : 24;

        // Settings changed — the in-memory catalog is stale (NSFW filter may differ).
        lock (_gate)
        {
            _catalog = null;
            _catalogSavedUtc = null;
        }
        _host?.Log(LogLevel.Debug, $"IptvSource: organizeBy={_organizeBy}, includeNsfw={_includeNsfw}, cacheMaxAgeHours={_cacheMaxAgeHours}");
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    // ── Catalog acquisition (lazy, cached) ───────────────────────────────────────

    private async Task<IptvCatalog> EnsureCatalogAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_catalog is not null)
                return _catalog;
        }

        // Try the on-disk cache first (unless it is stale).
        if (TryLoadCache(out var cached, out var savedUtc) &&
            !IsCacheStale(savedUtc))
        {
            lock (_gate)
            {
                _catalog = cached;
                _catalogSavedUtc = savedUtc;
                return _catalog;
            }
        }

        var catalog = await FetchAndCacheAsync(ct).ConfigureAwait(false);
        return catalog;
    }

    private async Task<IptvCatalog> FetchAndCacheAsync(CancellationToken ct)
    {
        if (_api is null)
            throw new InvalidOperationException("IptvSource used before InitializeAsync.");

        var catalog = await _api.BuildCatalogAsync(_includeNsfw, ct).ConfigureAwait(false);
        SaveCache(catalog);
        lock (_gate)
        {
            _catalog = catalog;
            _catalogSavedUtc = DateTimeOffset.UtcNow;
        }
        return catalog;
    }

    private bool IsCacheStale(DateTimeOffset savedUtc)
        => _cacheMaxAgeHours > 0 && DateTimeOffset.UtcNow - savedUtc > TimeSpan.FromHours(_cacheMaxAgeHours);

    private string CachePath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), CacheFileName);

    private sealed class CacheFile
    {
        public int Schema { get; set; }
        public bool IncludeNsfw { get; set; }
        public DateTimeOffset SavedUtc { get; set; }
        public List<IptvChannel> Channels { get; set; } = [];
    }

    private bool TryLoadCache(out IptvCatalog catalog, out DateTimeOffset savedUtc)
    {
        catalog = new IptvCatalog([]);
        savedUtc = default;
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return false;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (file is null || file.Schema != CacheSchemaVersion || file.IncludeNsfw != _includeNsfw)
                return false;
            catalog = new IptvCatalog(file.Channels);
            savedUtc = file.SavedUtc;
            return file.Channels.Count > 0;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: catalog cache read failed: {ex.Message}");
            return false;
        }
    }

    private void SaveCache(IptvCatalog catalog)
    {
        try
        {
            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new CacheFile
            {
                Schema = CacheSchemaVersion,
                IncludeNsfw = _includeNsfw,
                SavedUtc = DateTimeOffset.UtcNow,
                Channels = catalog.Channels.ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(file));
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: catalog cache write failed: {ex.Message}");
        }
    }

    // ── IBrowsable ───────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Ensure the catalog is available so drilling in is instant, but don't block enumeration on it.
        await EnsureCatalogAsync(ct).ConfigureAwait(false);

        // A single top-level IPTV tile on the home screen. Everything (country/category axes and
        // favorites) lives underneath it, so IPTV occupies one DMD tile rather than several.
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = Root,
            Title = "IPTV",
            Icon = "📺",
            HasSubCategories = true,
        };
    }

    /// <summary>
    /// Builds the child nodes of the single IPTV root: the country and/or category axes (per the
    /// "Organize by" setting) plus a Favorites node when any favorites exist. When only one axis is
    /// selected we <em>flatten</em> it — the root expands straight to the country (or category) tiles
    /// instead of forcing the user through a redundant single "By Country" sub-folder.
    /// </summary>
    private BrowseResult BuildRootChildren(IptvCatalog catalog)
    {
        bool showCountry = _organizeBy is IptvSourceProvider.OrganizeByCountry or IptvSourceProvider.OrganizeByBoth;
        bool showCategory = _organizeBy is IptvSourceProvider.OrganizeByCategory or IptvSourceProvider.OrganizeByBoth;
        bool both = showCountry && showCategory;

        var cats = new List<SourceCategory>();

        if (both)
        {
            // Two axes → offer them as sub-folders so the root stays tidy.
            cats.Add(Node(RootCountry, "By Country", "🌍", hasSub: true));
            cats.Add(Node(RootCategory, "By Category", "🎬", hasSub: true));
        }
        else if (showCountry)
        {
            cats.AddRange(CountryNodes(catalog));
        }
        else if (showCategory)
        {
            cats.AddRange(CategoryNodes(catalog));
        }

        if (GetFavoriteIds().Count > 0)
            cats.Add(Node(RootFavorites, "Favorites", "⭐", hasSub: false));

        return new BrowseResult { Categories = cats };
    }

    private IEnumerable<SourceCategory> CountryNodes(IptvCatalog catalog) =>
        catalog.ByCountry()
            .Where(g => !IsGroupHidden(CountryPrefix + g.Key))
            .Select(g => Group(CountryPrefix + g.Key, FlagFor(g) + g.Key, g.Count(), "📡"));

    private IEnumerable<SourceCategory> CategoryNodes(IptvCatalog catalog) =>
        catalog.ByCategory()
            .Where(g => !IsGroupHidden(CategoryPrefix + g.Key))
            .Select(g => Group(CategoryPrefix + g.Key, g.Key, g.Count(), "🎬"));

    private SourceCategory Node(string id, string title, string icon, bool hasSub) => new()
    {
        SourceInstanceId = InstanceId,
        CategoryId = id,
        Title = title,
        Icon = icon,
        HasSubCategories = hasSub,
    };

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var catalog = await EnsureCatalogAsync(ct).ConfigureAwait(false);
        var id = category.CategoryId;

        if (id == Root)
            return BuildRootChildren(catalog);

        if (id == RootCountry)
            return new BrowseResult { Categories = CountryNodes(catalog).ToList() };

        if (id == RootCategory)
            return new BrowseResult { Categories = CategoryNodes(catalog).ToList() };

        if (id == RootFavorites)
        {
            var favIds = GetFavoriteIds();
            var items = favIds
                .Select(fid => catalog.ById.TryGetValue(fid, out var ch) ? ToItem(ch) : null)
                .Where(i => i is not null)
                .Select(i => i!)
                .ToList();
            return new BrowseResult { Items = items };
        }

        if (id.StartsWith(CountryPrefix, StringComparison.Ordinal))
        {
            var name = id[CountryPrefix.Length..];
            var items = catalog.Channels
                .Where(c => string.Equals(c.CountryName, name, StringComparison.OrdinalIgnoreCase))
                .Select(ToItem).ToList();
            return new BrowseResult { Items = items };
        }

        if (id.StartsWith(CategoryPrefix, StringComparison.Ordinal))
        {
            var name = id[CategoryPrefix.Length..];
            var items = catalog.Channels
                .Where(c => c.Categories.Any(cat => string.Equals(cat, name, StringComparison.OrdinalIgnoreCase)))
                .Select(ToItem).ToList();
            return new BrowseResult { Items = items };
        }

        return new BrowseResult();
    }

    private static string FlagFor(IGrouping<string, IptvChannel> g)
    {
        var flag = g.Select(c => c.CountryFlag).FirstOrDefault(f => !string.IsNullOrEmpty(f));
        return string.IsNullOrEmpty(flag) ? "" : flag + " ";
    }

    private SourceCategory Group(string id, string title, int count, string icon) => new()
    {
        SourceInstanceId = InstanceId,
        CategoryId = id,
        Title = $"{title} ({count})",
        Icon = icon,
    };

    // ── ITextSearchCapable ───────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var catalog = await EnsureCatalogAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query)) yield break;

        foreach (var ch in catalog.Channels)
        {
            ct.ThrowIfCancellationRequested();
            if (ch.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ch.CountryName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ch.Categories.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                yield return ToItem(ch);
            }
        }
    }

    // ── IPlayableResolver ────────────────────────────────────────────────────────

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var channel = item.SourceState as IptvChannel ?? await FindChannelAsync(item.ItemId, ct).ConfigureAwait(false);
        if (channel is null)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: could not resolve '{item.ItemId}' — not in catalog.");
            return null;
        }

        IReadOnlyDictionary<string, string>? headers = null;
        if (!string.IsNullOrEmpty(channel.Referrer) || !string.IsNullOrEmpty(channel.UserAgent))
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(channel.Referrer)) h["Referer"] = channel.Referrer!;
            if (!string.IsNullOrEmpty(channel.UserAgent)) h["User-Agent"] = channel.UserAgent!;
            headers = h;
        }

        return new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, channel.Url)
        {
            IsLiveStream = true,
            HttpHeaders = headers,
            Resolution = channel.Quality,
        };
    }

    public Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
        => Task.FromResult<SourceMetadata?>(null);

    private async Task<IptvChannel?> FindChannelAsync(string id, CancellationToken ct)
    {
        var catalog = await EnsureCatalogAsync(ct).ConfigureAwait(false);
        return catalog.ById.TryGetValue(id, out var ch) ? ch : null;
    }

    // ── IRefreshable ─────────────────────────────────────────────────────────────

    // Always refreshable: the catalog is fetched from a public URL, so "Rescan" is really a
    // force-refresh. This must NOT depend on _api, because the host checks CanRefresh on a freshly
    // built transient source BEFORE calling InitializeAsync — gating on _api would wrongly report
    // "Nothing to rescan".
    public bool CanRefresh => true;

    public async Task<RefreshResult> RefreshAsync(
        IProgress<RefreshProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new RefreshProgress(-1, "Downloading iptv-org catalog…"));
        try
        {
            _ = _api ?? throw new InvalidOperationException("IptvSource used before InitializeAsync.");
            var catalog = await FetchAndCacheAsync(ct).ConfigureAwait(false);

            // A force-refresh pulls fresh stream URLs, so give previously-dead channels another chance.
            lock (_deadGate)
            {
                if (DeadStore.Count > 0)
                {
                    DeadStore.Clear();
                    SaveDead();
                }
            }

            progress?.Report(new RefreshProgress(1, "Done"));
            return new RefreshResult(true, catalog.Channels.Count,
                $"Loaded {catalog.Channels.Count:N0} channels from iptv-org.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RefreshResult(false, 0, $"Failed to refresh catalog: {ex.Message}");
        }
    }

    // ── IReplayableById ──────────────────────────────────────────────────────────

    public SourceItem? RebuildPlayable(string itemId)
    {
        IptvCatalog? catalog;
        lock (_gate) catalog = _catalog;
        if (catalog is null && TryLoadCache(out var cached, out _)) catalog = cached;
        if (catalog is not null && catalog.ById.TryGetValue(itemId, out var ch))
            return ToItem(ch);
        return null;
    }

    // ── SourceItem mapping ───────────────────────────────────────────────────────

    private SourceItem ToItem(IptvChannel ch) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = ch.Id,
        Title = ch.Title,
        Subtitle = ch.CountryName,
        ThumbnailUrl = ch.ThumbnailUrl,
        IsLiveStream = true,
        ShowUnavailableBadge = IsDead(ch.Id),
        SourceState = ch,
    };

    // ── IHideable (hidden countries / categories) ────────────────────────────────
    // We reuse the host's generic hide-management UI to let the user suppress whole COUNTRIES and
    // CATEGORIES (not individual channels — the lineup is huge and auto-pruned). Each hideable "item"
    // is a group node whose id matches the durable browse-node id (e.g. "country:France").

    public IReadOnlyList<HideableItem> GetHideableItems()
    {
        IptvCatalog? catalog;
        lock (_gate) catalog = _catalog;
        if (catalog is null && TryLoadCache(out var cached, out _)) catalog = cached;
        if (catalog is null) return [];

        var items = new List<HideableItem>();
        foreach (var g in catalog.ByCountry())
            items.Add(new HideableItem(CountryPrefix + g.Key, $"{FlagFor(g)}{g.Key} ({g.Count()})", "Countries"));
        foreach (var g in catalog.ByCategory())
            items.Add(new HideableItem(CategoryPrefix + g.Key, $"{g.Key} ({g.Count()})", "Categories"));
        return items;
    }

    public IReadOnlyCollection<string> GetHiddenIds()
    {
        lock (_hideGate) return HiddenStore.ToArray();
    }

    public void SetHidden(IReadOnlyCollection<string> itemIds, bool hidden)
    {
        if (itemIds is not { Count: > 0 }) return;
        lock (_hideGate)
        {
            bool changed = false;
            foreach (var id in itemIds)
                changed |= hidden ? HiddenStore.Add(id) : HiddenStore.Remove(id);
            if (changed) SaveHidden();
        }
    }

    private bool IsGroupHidden(string groupId)
    {
        lock (_hideGate) return HiddenStore.Contains(groupId);
    }

    private HashSet<string> HiddenStore => _hiddenCache ??= LoadHidden();

    private string HiddenPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "hidden.json");

    private HashSet<string> LoadHidden()
    {
        try
        {
            var path = HiddenPath;
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: hidden read failed: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void SaveHidden()
    {
        try
        {
            var path = HiddenPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_hiddenCache!.ToList()));
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: hidden write failed: {ex.Message}");
        }
    }

    // ── IPlaybackReportable / IPlaybackSuccessReportable (soft, retryable ⊘ badge) ───

    public bool ReportPlaybackFailure(string itemId, PlaybackFailureKind kind)
    {
        // IPTV failures are usually transient (geo-block, temporary outage, dead-for-now URL), so we
        // never mark a channel permanently unplayable — we remember it as "last play failed" and badge
        // it with ⊘ while keeping it fully playable. The user can retry; a success clears it (see
        // ReportPlaybackSuccess). Both Transient and Unresolvable are treated the same here. Returning
        // false keeps the row playable (IsPlayable stays true) — the badge, not button removal, is the
        // signal. A "Rescan" also clears the whole set (fresh URLs may work again).
        if (string.IsNullOrEmpty(itemId)) return false;
        lock (_deadGate)
        {
            if (DeadStore.Add(itemId))
            {
                SaveDead();
                _host?.Log(LogLevel.Info, $"IPTV: '{itemId}' play failed — badged unavailable (retryable).");
            }
        }
        return false; // stays playable; the ⊘ badge (via ShowUnavailableBadge) conveys the state
    }

    public bool ReportPlaybackSuccess(string itemId)
    {
        // Self-healing: a channel that plays again is no longer "unavailable" — drop its id and tell
        // the host the row's display state changed so the ⊘ badge is cleared live.
        if (string.IsNullOrEmpty(itemId)) return false;
        lock (_deadGate)
        {
            if (DeadStore.Remove(itemId))
            {
                SaveDead();
                _host?.Log(LogLevel.Debug, $"IPTV: '{itemId}' played — cleared unavailable badge.");
                return true;
            }
        }
        return false;
    }

    private bool IsDead(string id)
    {
        lock (_deadGate) return DeadStore.Contains(id);
    }

    private HashSet<string> DeadStore => _deadCache ??= LoadDead();

    private string DeadPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "unplayable.json");

    private HashSet<string> LoadDead()
    {
        try
        {
            var path = DeadPath;
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: unplayable read failed: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void SaveDead()
    {
        try
        {
            var path = DeadPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_deadCache!.ToList()));
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: unplayable write failed: {ex.Message}");
        }
    }

    // ── IFavoritable / IFavoriteCapture ──────────────────────────────────────────

    private sealed class IptvFavorite
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? ThumbnailUrl { get; set; }
    }

    private readonly object _favGate = new();
    private Dictionary<string, IptvFavorite>? _favoritesCache;
    private Dictionary<string, IptvFavorite> FavStore => _favoritesCache ??= LoadFavorites();

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    public bool IsFavorite(string itemId)
    {
        lock (_favGate) return FavStore.ContainsKey(itemId);
    }

    public void SetFavorite(string itemId, bool favorite)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        lock (_favGate)
        {
            bool changed;
            if (favorite)
            {
                changed = !FavStore.ContainsKey(itemId);
                if (changed) FavStore[itemId] = new IptvFavorite { Id = itemId, Title = itemId };
            }
            else
            {
                changed = FavStore.Remove(itemId);
            }
            if (changed) SaveFavorites();
        }
    }

    public void RememberFavorite(FavoriteCapture item)
    {
        if (string.IsNullOrEmpty(item.ItemId)) return;
        lock (_favGate)
        {
            if (!FavStore.ContainsKey(item.ItemId)) return;
            FavStore[item.ItemId] = new IptvFavorite
            {
                Id = item.ItemId,
                Title = item.Title,
                Subtitle = item.Subtitle,
                ThumbnailUrl = item.ThumbnailUrl,
            };
            SaveFavorites();
        }
    }

    public IReadOnlyCollection<string> GetFavoriteIds()
    {
        lock (_favGate) return FavStore.Keys.ToArray();
    }

    public SourceItem? GetFavorite(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;

        // Prefer a live catalog entry (has the current stream URL); fall back to the stored display record.
        IptvCatalog? catalog;
        lock (_gate) catalog = _catalog;
        if (catalog is not null && catalog.ById.TryGetValue(itemId, out var ch))
            return ToItem(ch);

        IptvFavorite? f;
        lock (_favGate) f = FavStore.TryGetValue(itemId, out var rec) ? rec : null;
        if (f is null) return null;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            Subtitle = f.Subtitle,
            ThumbnailUrl = f.ThumbnailUrl,
            IsLiveStream = true,
        };
    }

    private Dictionary<string, IptvFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, IptvFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<IptvFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: favorites read failed: {ex.Message}");
            return new Dictionary<string, IptvFavorite>(StringComparer.Ordinal);
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var path = FavoritesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(FavStore.Values.ToList()));
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"IPTV: favorites write failed: {ex.Message}");
        }
    }
}
