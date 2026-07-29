using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.HdHomeRun;

/// <summary>
/// A configured HDHomeRun source instance. Discovers one tuner (<c>/discover.json</c>), reads its
/// channel lineup (<c>/lineup.json</c>), caches the joined catalog to the instance directory, and
/// exposes it for browse, free-text search, and live playback. When "Fetch guide data" is enabled it
/// overlays channel icons from the SiliconDust guide service (Phase 2).
/// </summary>
/// <remarks>
/// Pure data producer — no UI, no host internals. Every channel is a continuous live MPEG-TS stream,
/// so items and resolved streams are marked <see cref="ResolvedStream.IsLiveStream"/>. The catalog is
/// built lazily on first use (or by an explicit "Rescan"), cached on disk, and guarded by a lock.
/// </remarks>
public sealed class HdHomeRunSource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IPlayableResolver, IRefreshable,
    IReplayableById, IFavoritable, IFavoriteCapture, IHideable,
    IPlaybackReportable, IPlaybackSuccessReportable
{
    // Durable category-id scheme (see SourceCategory.CategoryId): a node must be actionable from its
    // id alone. The lineup is flat, so a single root expands straight to the channel list (plus a
    // Favorites node when the user has starred any channels).
    private const string Root = "root";
    private const string RootFavorites = "root:favorites";

    private const string CacheFileName = "catalog.json";
    private const int CacheSchemaVersion = 1;

    private const string GuideCacheFileName = "guide.json";
    private const int GuideCacheSchemaVersion = 1;
    // Guide/program data cache. Kept fairly short (~4h) to stay well inside the source's program
    // window: if the feed only supplies a few hours of schedule, a longer cache would leave us
    // showing (or falling back from) stale/empty program data.
    private static readonly TimeSpan GuideMaxAge = TimeSpan.FromHours(4);

    private readonly object _gate = new();
    private HdhrCatalog? _catalog;
    private DateTimeOffset? _catalogSavedUtc;

    // The cloud guide (channel icons + program schedule) keyed by guide number. Cached separately from
    // the lineup with its own (~4h) freshness window: a lineup rescan should not discard the guide,
    // and a shorter window keeps program data from going stale. Used to enrich channel titles with the
    // current program (see ToItem) and to overlay icons onto the lineup.
    private readonly object _guideGate = new();
    private IReadOnlyDictionary<string, HdhrGuide> _guideByNumber =
        new Dictionary<string, HdhrGuide>(StringComparer.Ordinal);
    private DateTimeOffset? _guideSavedUtc;

    private IPluginHost? _host;
    private HdhrApiClient? _api;
    private HdhrGuideClient? _guide;

    private string _tunerAddress = "";
    private bool _enableGuideData = true;
    private int _cacheMaxAgeMinutes = 60;

    // Ids the host reported as having failed playback (all tuners busy, temporary signal loss, DRM).
    // NOT hidden — the channel stays visible and playable, badged with ⊘ so the user can retry; a
    // successful play clears its id. Persisted to the instance dir.
    private readonly object _deadGate = new();
    private HashSet<string>? _deadCache;

    public HdHomeRunSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => HdHomeRunSourceProvider.HdHomeRunTypeId;
    public string DisplayName { get; set; } = "HDHomeRun";

    /// <summary>Ready to operate once the user has supplied a tuner address.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_tunerAddress);

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        _api = new HdhrApiClient(host.HttpClient, (lvl, msg) => host.Log(lvl, msg));
        _guide = new HdhrGuideClient(host.HttpClient, (lvl, msg) => host.Log(lvl, msg));
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _tunerAddress = Get(values, HdHomeRunSourceProvider.KeyTunerAddress)?.Trim() ?? "";
        _enableGuideData = !bool.TryParse(Get(values, HdHomeRunSourceProvider.KeyEnableGuideData), out var g) || g;
        _cacheMaxAgeMinutes = int.TryParse(Get(values, HdHomeRunSourceProvider.KeyCacheMaxAgeMinutes), out var m) && m >= 0
            ? m : 60;

        // Settings changed — the in-memory catalog is stale (address or guide flag may differ).
        lock (_gate)
        {
            _catalog = null;
            _catalogSavedUtc = null;
        }
        // Drop the in-memory guide too so a toggled/re-pointed instance re-evaluates it on next use
        // (the on-disk guide cache is still consulted and reused when the address is unchanged).
        lock (_guideGate)
        {
            _guideByNumber = new Dictionary<string, HdhrGuide>(StringComparer.Ordinal);
            _guideSavedUtc = null;
        }
        _host?.Log(LogLevel.Debug,
            $"HdHomeRunSource: tunerAddress={_tunerAddress}, enableGuideData={_enableGuideData}, cacheMaxAgeMinutes={_cacheMaxAgeMinutes}");
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    // ── Catalog acquisition (lazy, cached) ───────────────────────────────────────

    private async Task<HdhrCatalog> EnsureCatalogAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_catalog is not null)
                return _catalog;
        }

        // Try the on-disk cache first (unless it is stale).
        if (TryLoadCache(out var cached, out var savedUtc) && cached is not null && !IsCacheStale(savedUtc))
        {
            lock (_gate)
            {
                _catalog = cached;
                _catalogSavedUtc = savedUtc;
                return cached;
            }
        }

        return await FetchAndCacheAsync(forceRefreshGuide: false, ct).ConfigureAwait(false);
    }

    private async Task<HdhrCatalog> FetchAndCacheAsync(bool forceRefreshGuide, CancellationToken ct)
    {
        if (_api is null)
            throw new InvalidOperationException("HdHomeRunSource used before InitializeAsync.");
        if (string.IsNullOrWhiteSpace(_tunerAddress))
            throw new InvalidOperationException("HDHomeRun tuner address is not configured.");

        var device = await _api.DiscoverAsync(_tunerAddress, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"HDHomeRun tuner at '{_tunerAddress}' did not respond to discovery.");

        var catalog = await _api.BuildCatalogAsync(device, ct).ConfigureAwait(false);

        // Phase 2: ensure the cloud guide (icons + programs) is loaded/refreshed, then overlay icons.
        var guide = await EnsureGuideAsync(device.DeviceAuth, forceRefreshGuide, ct).ConfigureAwait(false);
        catalog = ApplyGuideIcons(catalog, guide);

        SaveCache(catalog);
        lock (_gate)
        {
            _catalog = catalog;
            _catalogSavedUtc = DateTimeOffset.UtcNow;
        }
        return catalog;
    }

    /// <summary>
    /// Overlays channel icons from the loaded guide onto the locally-built catalog by guide number.
    /// Returns the catalog unchanged when there is no guide data or none of it carries an icon.
    /// </summary>
    private static HdhrCatalog ApplyGuideIcons(HdhrCatalog catalog, IReadOnlyDictionary<string, HdhrGuide> guide)
    {
        if (guide.Count == 0) return catalog;

        var merged = catalog.Channels
            .Select(c => guide.TryGetValue(c.GuideNumber, out var g) && !string.IsNullOrEmpty(g.IconUrl)
                ? c with { ThumbnailUrl = g.IconUrl }
                : c)
            .ToList();
        return new HdhrCatalog(catalog.Device, merged);
    }

    // ── Guide (icons + programs) acquisition (lazy, ~4h cache) ────────────────────

    /// <summary>
    /// Ensures the cloud guide is available: returns the in-memory copy if present, else loads a fresh
    /// on-disk cache, else (when enabled and a DeviceAuth token is available) fetches it from the
    /// SiliconDust guide service and caches it for ~4h. Best-effort — any failure yields whatever we
    /// already have (possibly empty), never an exception, so the local lineup is never blocked.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, HdhrGuide>> EnsureGuideAsync(
        string? deviceAuth, bool forceRefresh, CancellationToken ct)
    {
        if (!_enableGuideData || _guide is null)
            return new Dictionary<string, HdhrGuide>(StringComparer.Ordinal);

        if (!forceRefresh)
        {
            lock (_guideGate)
            {
                if (_guideByNumber.Count > 0 && _guideSavedUtc is { } saved &&
                    DateTimeOffset.UtcNow - saved <= GuideMaxAge)
                    return _guideByNumber;
            }

            if (TryLoadGuideCache(out var cachedGuide, out var cachedSaved) &&
                DateTimeOffset.UtcNow - cachedSaved <= GuideMaxAge)
            {
                lock (_guideGate)
                {
                    _guideByNumber = cachedGuide;
                    _guideSavedUtc = cachedSaved;
                    return _guideByNumber;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(deviceAuth))
        {
            lock (_guideGate) return _guideByNumber;
        }

        var fetched = await _guide.GetGuideAsync(deviceAuth!, ct).ConfigureAwait(false);
        if (fetched.Count == 0)
        {
            // Keep whatever we already had rather than clobbering a good cache with an empty fetch.
            lock (_guideGate) return _guideByNumber;
        }

        // Dynamic eviction: when the source's program window is short, a fetch can return channels
        // with (almost) no program data. Rather than caching that emptiness for the full freshness
        // window, invalidate the cache so we re-fetch soon and pick up data as it becomes available.
        var channelsWithPrograms = fetched.Values.Count(g => g.Programs.Count > 0);
        if (channelsWithPrograms == 0)
        {
            _host?.Log(LogLevel.Info,
                $"HDHomeRun: guide cache invalidated — fetch returned no program data for any of {fetched.Count} channels " +
                "(source program window may be short); will re-fetch on next request.");
            InvalidateGuideCache();
            lock (_guideGate) return _guideByNumber;
        }

        SaveGuideCache(fetched);
        lock (_guideGate)
        {
            _guideByNumber = fetched;
            _guideSavedUtc = DateTimeOffset.UtcNow;
            return _guideByNumber;
        }
    }

    /// <summary>The current program for a channel from the loaded guide, or <c>null</c>.</summary>
    private HdhrProgram? CurrentProgramFor(string guideNumber)
    {
        HdhrGuide? g = null;
        lock (_guideGate) _guideByNumber.TryGetValue(guideNumber, out g);

        // Cold path (e.g. RebuildPlayable after a restart): the guide isn't in memory yet, so fall
        // back to the on-disk cache when it is still fresh, without blocking on a cloud fetch.
        if (g is null && _enableGuideData)
        {
            lock (_guideGate)
            {
                if (_guideByNumber.Count == 0 &&
                    TryLoadGuideCache(out var cached, out var saved) &&
                    DateTimeOffset.UtcNow - saved <= GuideMaxAge)
                {
                    _guideByNumber = cached;
                    _guideSavedUtc = saved;
                }
                _guideByNumber.TryGetValue(guideNumber, out g);
            }
        }

        return g?.CurrentProgram(DateTimeOffset.UtcNow);
    }

    private bool IsCacheStale(DateTimeOffset savedUtc)
        => _cacheMaxAgeMinutes > 0 && DateTimeOffset.UtcNow - savedUtc > TimeSpan.FromMinutes(_cacheMaxAgeMinutes);

    private string CachePath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), CacheFileName);

    private sealed class CacheFile
    {
        public int Schema { get; set; }
        public string TunerAddress { get; set; } = "";
        public DateTimeOffset SavedUtc { get; set; }
        public HdhrDevice? Device { get; set; }
        public List<HdhrChannel> Channels { get; set; } = [];
    }

    private bool TryLoadCache(out HdhrCatalog? catalog, out DateTimeOffset savedUtc)
    {
        catalog = null;
        savedUtc = default;
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return false;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (file is null || file.Schema != CacheSchemaVersion || file.Device is null ||
                !string.Equals(file.TunerAddress, _tunerAddress, StringComparison.OrdinalIgnoreCase))
                return false;
            if (file.Channels.Count == 0) return false;
            catalog = new HdhrCatalog(file.Device, file.Channels);
            savedUtc = file.SavedUtc;
            return true;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: catalog cache read failed: {ex.Message}");
            return false;
        }
    }

    private void SaveCache(HdhrCatalog catalog)
    {
        try
        {
            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new CacheFile
            {
                Schema = CacheSchemaVersion,
                TunerAddress = _tunerAddress,
                SavedUtc = DateTimeOffset.UtcNow,
                Device = catalog.Device,
                Channels = catalog.Channels.ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(file));
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: catalog cache write failed: {ex.Message}");
        }
    }

    // ── Guide cache (icons + programs) persistence ───────────────────────────────

    private string GuideCachePath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), GuideCacheFileName);

    private sealed class GuideCacheFile
    {
        public int Schema { get; set; }
        public DateTimeOffset SavedUtc { get; set; }
        public List<HdhrGuide> Channels { get; set; } = [];
    }

    private bool TryLoadGuideCache(out IReadOnlyDictionary<string, HdhrGuide> guide, out DateTimeOffset savedUtc)
    {
        guide = new Dictionary<string, HdhrGuide>(StringComparer.Ordinal);
        savedUtc = default;
        try
        {
            var path = GuideCachePath;
            if (!File.Exists(path)) return false;
            var file = JsonSerializer.Deserialize<GuideCacheFile>(File.ReadAllText(path));
            if (file is null || file.Schema != GuideCacheSchemaVersion || file.Channels.Count == 0)
                return false;
            guide = file.Channels
                .Where(g => !string.IsNullOrWhiteSpace(g.GuideNumber))
                .ToDictionary(g => g.GuideNumber, g => g, StringComparer.Ordinal);
            savedUtc = file.SavedUtc;
            return guide.Count > 0;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: guide cache read failed: {ex.Message}");
            return false;
        }
    }

    private void SaveGuideCache(IReadOnlyDictionary<string, HdhrGuide> guide)
    {
        try
        {
            var path = GuideCachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new GuideCacheFile
            {
                Schema = GuideCacheSchemaVersion,
                SavedUtc = DateTimeOffset.UtcNow,
                Channels = guide.Values.ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(file));
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: guide cache write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops the guide cache (in-memory + on-disk) so the next request re-fetches. Used when a fetch
    /// comes back with no usable program data, to keep the cache dynamic rather than caching emptiness.
    /// </summary>
    private void InvalidateGuideCache()
    {
        lock (_guideGate)
        {
            _guideByNumber = new Dictionary<string, HdhrGuide>(StringComparer.Ordinal);
            _guideSavedUtc = null;
        }

        try
        {
            var path = GuideCachePath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: guide cache delete failed: {ex.Message}");
        }
    }

    // ── IBrowsable ───────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Warm the catalog so drilling in is instant, but don't fail enumeration if the tuner is down.
        try { await EnsureCatalogAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: could not preload lineup: {ex.Message}");
        }

        // A single top-level tile; its children are the tuner's live channels.
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = Root,
            Title = DisplayName,
            Icon = "📡",
            HasSubCategories = true,
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var catalog = await EnsureCatalogAsync(ct).ConfigureAwait(false);

        if (category.CategoryId == RootFavorites)
        {
            var favIds = GetFavoriteIds();
            var favItems = favIds
                .Select(fid => catalog.ById.TryGetValue(fid, out var ch) ? ToItem(ch) : null)
                .Where(i => i is not null)
                .Select(i => i!)
                .ToList();
            return new BrowseResult { Items = favItems };
        }

        if (category.CategoryId != Root)
            return new BrowseResult();

        // The channel list, minus any the user has hidden. A Favorites node leads the list when the
        // user has starred any channels.
        var cats = new List<SourceCategory>();
        if (GetFavoriteIds().Count > 0)
            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = RootFavorites,
                Title = "Favorites",
                Icon = "⭐",
            });

        var items = catalog.Channels
            .Where(c => !IsHidden(c.Id))
            .Select(ToItem)
            .ToList();
        return new BrowseResult { Categories = cats, Items = items };
    }

    // ── ITextSearchCapable ───────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;
        var catalog = await EnsureCatalogAsync(ct).ConfigureAwait(false);

        foreach (var ch in catalog.Channels)
        {
            ct.ThrowIfCancellationRequested();
            if (IsHidden(ch.Id)) continue;
            if (ch.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ch.GuideNumber.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                yield return ToItem(ch);
            }
        }
    }

    // ── IPlayableResolver ────────────────────────────────────────────────────────

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var channel = item.SourceState as HdhrChannel ?? await FindChannelAsync(item.ItemId, ct).ConfigureAwait(false);
        if (channel is null)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: could not resolve '{item.ItemId}' — not in lineup.");
            return null;
        }

        if (channel.IsDrm)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: '{channel.Name}' ({channel.GuideNumber}) is DRM-protected — cannot play.");
            return null;
        }

        // The lineup URL is a direct MPEG-TS stream served by the tuner over HTTP.
        return new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, channel.Url)
        {
            IsLiveStream = true,
            Resolution = channel.IsHd ? "HD" : null,
        };
    }

    public Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
        => Task.FromResult<SourceMetadata?>(null);

    private async Task<HdhrChannel?> FindChannelAsync(string id, CancellationToken ct)
    {
        var catalog = await EnsureCatalogAsync(ct).ConfigureAwait(false);
        return catalog.ById.TryGetValue(id, out var ch) ? ch : null;
    }

    // ── IRefreshable ─────────────────────────────────────────────────────────────

    // Refreshable whenever a tuner address is configured: "Rescan" is a force-refresh of the lineup.
    // Must NOT depend on _api (the host checks CanRefresh on a transient source before InitializeAsync).
    public bool CanRefresh => !string.IsNullOrWhiteSpace(_tunerAddress);

    public async Task<RefreshResult> RefreshAsync(
        IProgress<RefreshProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new RefreshProgress(-1, "Contacting HDHomeRun tuner…"));
        try
        {
            _ = _api ?? throw new InvalidOperationException("HdHomeRunSource used before InitializeAsync.");
            var catalog = await FetchAndCacheAsync(forceRefreshGuide: true, ct).ConfigureAwait(false);

            // A force-refresh re-reads the lineup, so give previously-failed channels another chance.
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
                $"Loaded {catalog.Channels.Count:N0} channels from {catalog.Device.FriendlyName}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RefreshResult(false, 0, $"Failed to refresh lineup: {ex.Message}");
        }
    }

    // ── IReplayableById ──────────────────────────────────────────────────────────

    public SourceItem? RebuildPlayable(string itemId)
    {
        HdhrCatalog? catalog;
        lock (_gate) catalog = _catalog;
        if (catalog is null && TryLoadCache(out var cached, out _)) catalog = cached;
        if (catalog is not null && catalog.ById.TryGetValue(itemId, out var ch))
            return ToItem(ch);
        return null;
    }

    // ── SourceItem mapping ───────────────────────────────────────────────────────

    private SourceItem ToItem(HdhrChannel ch) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = ch.Id,
        Title = BuildTitle(ch),
        Subtitle = ch.IsHd ? "HD" : null,
        ThumbnailUrl = ch.ThumbnailUrl,
        IsLiveStream = true,
        ShowUnavailableBadge = IsDead(ch.Id),
        SourceState = ch,
    };

    /// <summary>
    /// The channel's display title, supplemented with the program airing right now when the guide
    /// knows it — e.g. "2.1 WFMY-HD" becomes "2.1 WFMY-HD - 6:00 News". The program is computed against
    /// the clock at call time, so a title rebuilt later reflects whatever is on <em>then</em>. Falls
    /// back to the plain channel title when guide data is disabled/unavailable. Because both the channel
    /// listview and the now-playing display derive their text from this title, the enrichment surfaces
    /// in both places automatically.
    /// </summary>
    private string BuildTitle(HdhrChannel ch)
    {
        var baseTitle = $"{ch.GuideNumber} {ch.Name}";
        var program = CurrentProgramFor(ch.GuideNumber);
        return program is null ? baseTitle : $"{baseTitle} - {program.Title}";
    }

    // ── IPlaybackReportable / IPlaybackSuccessReportable (soft, retryable ⊘ badge) ───

    public bool ReportPlaybackFailure(string itemId, PlaybackFailureKind kind)
    {
        // HDHomeRun failures are usually transient (all tuners busy, brief signal loss), so we never
        // mark a channel permanently unplayable — we remember it as "last play failed" and badge it
        // with ⊘ while keeping it fully playable. The user can retry; a success clears it (see
        // ReportPlaybackSuccess). A "Rescan" also clears the whole set.
        if (string.IsNullOrEmpty(itemId)) return false;
        lock (_deadGate)
        {
            if (DeadStore.Add(itemId))
            {
                SaveDead();
                _host?.Log(LogLevel.Info, $"HDHomeRun: '{itemId}' play failed — badged unavailable (retryable).");
            }
        }
        return false; // stays playable; the ⊘ badge conveys the state
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
                _host?.Log(LogLevel.Debug, $"HDHomeRun: '{itemId}' played — cleared unavailable badge.");
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
            _host?.Log(LogLevel.Warning, $"HDHomeRun: unplayable read failed: {ex.Message}");
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
            _host?.Log(LogLevel.Warning, $"HDHomeRun: unplayable write failed: {ex.Message}");
        }
    }

    // ── IFavoritable / IFavoriteCapture ──────────────────────────────────────────

    private sealed class HdhrFavorite
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? ThumbnailUrl { get; set; }
    }

    private readonly object _favGate = new();
    private Dictionary<string, HdhrFavorite>? _favoritesCache;
    private Dictionary<string, HdhrFavorite> FavStore => _favoritesCache ??= LoadFavorites();

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
                if (changed) FavStore[itemId] = new HdhrFavorite { Id = itemId, Title = itemId };
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
            FavStore[item.ItemId] = new HdhrFavorite
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

        // Prefer a live catalog entry (has the current stream URL + fresh program title); fall back to
        // the stored display record when the lineup isn't loaded.
        HdhrCatalog? catalog;
        lock (_gate) catalog = _catalog;
        if (catalog is null && TryLoadCache(out var cached, out _)) catalog = cached;
        if (catalog is not null && catalog.ById.TryGetValue(itemId, out var ch))
            return ToItem(ch);

        HdhrFavorite? f;
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

    private Dictionary<string, HdhrFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, HdhrFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<HdhrFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"HDHomeRun: favorites read failed: {ex.Message}");
            return new Dictionary<string, HdhrFavorite>(StringComparer.Ordinal);
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
            _host?.Log(LogLevel.Warning, $"HDHomeRun: favorites write failed: {ex.Message}");
        }
    }

    // ── IHideable (hidden channels) ──────────────────────────────────────────────
    // Lets the user suppress whole channels from the browse/search lists via the host's generic
    // hide-management UI (surfaced in settings). Each hideable "item" id is a channel id (its guide
    // number). Hidden channels are persisted to the instance dir and excluded from BrowseAsync /
    // SearchAsync (but a favorited-then-hidden channel still shows under Favorites, by design).

    private readonly object _hideGate = new();
    private HashSet<string>? _hiddenCache;
    private HashSet<string> HiddenStore => _hiddenCache ??= LoadHidden();

    private string HiddenPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "hidden.json");

    public IReadOnlyList<HideableItem> GetHideableItems()
    {
        HdhrCatalog? catalog;
        lock (_gate) catalog = _catalog;
        if (catalog is null && TryLoadCache(out var cached, out _)) catalog = cached;
        if (catalog is null) return [];

        return catalog.Channels
            .Select(c => new HideableItem(c.Id, $"{c.GuideNumber} {c.Name}"))
            .ToList();
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

    private bool IsHidden(string id)
    {
        lock (_hideGate) return HiddenStore.Contains(id);
    }

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
            _host?.Log(LogLevel.Warning, $"HDHomeRun: hidden read failed: {ex.Message}");
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
            _host?.Log(LogLevel.Warning, $"HDHomeRun: hidden write failed: {ex.Message}");
        }
    }
}
