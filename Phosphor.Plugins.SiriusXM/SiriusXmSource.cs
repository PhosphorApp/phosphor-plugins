using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// A configured SiriusXM source instance. Authenticates a subscriber, browses the live channel
/// lineup, and resolves a channel to a locally-proxied HLS URL the host plays via LibVLC.
/// </summary>
/// <remarks>
/// Channels are <b>live audio streams</b>: every produced item is <see cref="SourceItem.IsAudioOnly"/>
/// and <see cref="SourceItem.IsLiveStream"/>, and the resolved stream is
/// <see cref="StreamLayout.AudioOnly"/> + <see cref="ResolvedStream.IsLiveStream"/> so the host
/// suppresses seek/duration and never auto-advances.
/// </remarks>
public sealed class SiriusXmSource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IPlayableResolver, IConnectionTestable, IFavoritable, IHideable
{
    private readonly object _gate = new();
    private IPluginHost? _host;
    private string _username = "";
    private string _password = "";
    private string _region = SiriusXmSourceProvider.RegionUs;
    // Local HLS proxy port (configurable; defaults to SiriusXmSourceProvider.DefaultProxyPort).
    private int _proxyPort = SiriusXmSourceProvider.DefaultProxyPort;

    private SxmClient? _client;
    private SxmProxy? _proxy;
    private IReadOnlyList<SxmChannel>? _channels;

    // Favorited channel ids (loaded lazily from the instance dir).
    private HashSet<string>? _favoritesCache;
    private HashSet<string> _favorites => _favoritesCache ??= LoadFavorites();

    // Hidden channel ids (loaded lazily from the instance dir).
    private HashSet<string>? _hiddenCache;
    private HashSet<string> _hidden => _hiddenCache ??= LoadHidden();

    public SiriusXmSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => SiriusXmSourceProvider.SiriusXmTypeId;
    public string DisplayName { get; set; } = "SiriusXM";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password);
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _username = Get(values, SiriusXmSourceProvider.KeyUsername) ?? "";
        _password = Get(values, SiriusXmSourceProvider.KeyPassword) ?? "";
        _region = Get(values, SiriusXmSourceProvider.KeyRegion) is { Length: > 0 } r ? r : SiriusXmSourceProvider.RegionUs;

        var newPort = int.TryParse(Get(values, SiriusXmSourceProvider.KeyProxyPort), out var p) && p is > 0 and <= 65535
            ? p : SiriusXmSourceProvider.DefaultProxyPort;

        // Credentials changed — drop any live client/lineup so the next use re-authenticates. If the
        // port changed, tear down the running proxy so it rebinds on the new port next resolve.
        lock (_gate)
        {
            _client = null;
            _channels = null;
            if (newPort != _proxyPort)
            {
                _proxyPort = newPort;
                _proxy?.Dispose();
                _proxy = null;
            }
        }
    }

    // ── IConnectionTestable ─────────────────────────────────────────────────────

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ConnectionTestResult(false, "Enter a username and password first.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var client = new SxmClient(_username, _password, _region, Log);
            if (!await client.AuthenticateAsync(ct))
                return new ConnectionTestResult(false, "Login failed — check username/password.", sw.Elapsed);
            var channels = await client.GetChannelsAsync(ct);
            return new ConnectionTestResult(true, $"Connected — {channels.Count} channels.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    // ── IBrowsable (Music/Talk/Sports super-groups → categories → channels) ─────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A single "SiriusXM" root tile (keeps the home screen tidy alongside playlists/other
        // sources). Drilling in reveals the super-groups + All Channels. STATIC — no lineup fetch
        // here (the host enumerates roots at startup; a network call would block the splash).
        await Task.CompletedTask;
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = "root",
            Title = DisplayName,
            Icon = "📡",
            HasSubCategories = true,
            SourceState = new SxmNode(SxmNodeKind.Root),
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var node = category.SourceState as SxmNode ?? InferNode(category.CategoryId);

        // Root expands to the super-group tiles + All Channels — static, no lineup needed.
        if (node.Kind == SxmNodeKind.Root)
        {
            var groups = new List<SourceCategory>();
            // ⭐ Favorites first, when the user has any.
            if (_favorites.Count > 0)
            {
                groups.Add(new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = "favorites",
                    Title = "Favorites",
                    Icon = "⭐",
                    HasSubCategories = true,
                    SourceState = new SxmNode(SxmNodeKind.Favorites),
                });
            }
            foreach (var super in new[] { SxmCategoryMap.SuperMusic, SxmCategoryMap.SuperTalk, SxmCategoryMap.SuperSports })
            {
                groups.Add(new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = $"super:{super}",
                    Title = super,
                    Icon = SuperGroupIcon(super),
                    HasSubCategories = true,
                    SourceState = new SxmNode(SxmNodeKind.SuperGroup, super),
                });
            }
            groups.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = "all",
                Title = "All Channels",
                Icon = "📻",
                HasSubCategories = true,
                SourceState = new SxmNode(SxmNodeKind.AllChannels),
            });
            return new BrowseResult { Categories = groups };
        }

        var channels = await EnsureChannelsAsync(ct);
        // Exclude hidden channels from every browse view (categories, All Channels, and Favorites).
        // Re-read from disk so edits made in the Settings "Manage hidden channels" dialog (a separate
        // transient source instance) take effect on the next browse without an app restart.
        ReloadHiddenIfChanged();
        HashSet<string> hidden;
        lock (_gate) hidden = new HashSet<string>(_hidden, StringComparer.Ordinal);
        if (hidden.Count > 0)
            channels = channels.Where(c => !hidden.Contains(c.Id)).ToList();

        switch (node.Kind)
        {
            case SxmNodeKind.Favorites:
            {
                var favs = _favorites;
                var items = channels
                    .Where(c => favs.Contains(c.Id))
                    .OrderBy(c => c.SortNumber)
                    .Select(ToSourceItem)
                    .ToList();
                return new BrowseResult { Items = items };
            }

            case SxmNodeKind.SuperGroup:
            {
                // List the distinct categories in this super-group (that have channels), as sub-tiles.
                var map = CategoryMap;
                var cats = channels
                    .SelectMany(c => c.Categories)
                    .Where(cat => map.SuperGroupFor(cat.Key) == node.Key)
                    .GroupBy(cat => cat.Key)
                    .Select(g => (Key: g.Key, Name: g.First().Name, Count: g.Count()))
                    .OrderByDescending(g => g.Count)
                    .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = $"cat:{g.Key}",
                        Title = g.Name,
                        Icon = "🎶",
                        HasSubCategories = true,
                        SourceState = new SxmNode(SxmNodeKind.Category, g.Key),
                    })
                    .ToList();
                return new BrowseResult { Categories = cats };
            }

            case SxmNodeKind.Category:
            {
                var items = channels
                    .Where(c => c.Categories.Any(cat => string.Equals(cat.Key, node.Key, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(c => c.SortNumber)
                    .Select(ToSourceItem)
                    .ToList();
                return new BrowseResult { Items = items };
            }

            case SxmNodeKind.AllChannels:
            default:
            {
                var items = channels
                    .OrderBy(c => c.SortNumber)
                    .Select(ToSourceItem)
                    .ToList();
                return new BrowseResult { Items = items };
            }
        }
    }

    private static SxmNode InferNode(string categoryId) => categoryId switch
    {
        "root" => new SxmNode(SxmNodeKind.Root),
        "favorites" => new SxmNode(SxmNodeKind.Favorites),
        "all" => new SxmNode(SxmNodeKind.AllChannels),
        var s when s.StartsWith("super:", StringComparison.Ordinal) => new SxmNode(SxmNodeKind.SuperGroup, s["super:".Length..]),
        var s when s.StartsWith("cat:", StringComparison.Ordinal) => new SxmNode(SxmNodeKind.Category, s["cat:".Length..]),
        _ => new SxmNode(SxmNodeKind.Root),
    };

    private static string SuperGroupIcon(string super) => super switch
    {
        SxmCategoryMap.SuperMusic => "🎵",
        SxmCategoryMap.SuperTalk => "🗣",
        SxmCategoryMap.SuperSports => "🏈",
        _ => "📻",
    };

    private SxmCategoryMap? _categoryMap;
    private SxmCategoryMap CategoryMap =>
        _categoryMap ??= SxmCategoryMap.Load(_host?.InstanceCacheDirectory, Log);

    // ── ITextSearchCapable ──────────────────────────────────────────────────────

    /// <summary>
    /// Filters the channel lineup by a free-text <paramref name="query"/> — matches channel name,
    /// number, or any of its category names (e.g. "NHL" surfaces "NHL Radio" buried under Sports).
    /// Not a fuzzy/relevance search; a simple case-insensitive substring filter over the cached
    /// lineup. Hidden channels are excluded, mirroring browse.
    /// </summary>
    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) yield break;

        var channels = await EnsureChannelsAsync(ct);

        // Exclude hidden channels, like the browse views do.
        ReloadHiddenIfChanged();
        HashSet<string> hidden;
        lock (_gate) hidden = new HashSet<string>(_hidden, StringComparer.Ordinal);

        foreach (var c in channels
            .Where(c => !hidden.Contains(c.Id) && MatchesQuery(c, q))
            .OrderBy(c => c.SortNumber))
        {
            ct.ThrowIfCancellationRequested();
            yield return ToSourceItem(c);
        }
    }

    // Case-insensitive substring match over name, number, and category names.
    private static bool MatchesQuery(SxmChannel c, string q)
    {
        if (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(c.Number) && c.Number.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var cat in c.Categories)
            if (cat.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private SourceItem ToSourceItem(SxmChannel c) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = c.Id,
        Title = string.IsNullOrEmpty(c.Number) ? c.Name : $"{c.Number} · {c.Name}",
        Subtitle = "SiriusXM",
        ThumbnailUrl = c.ThumbnailUrl,
        IsAudioOnly = true,
        IsLiveStream = true,
        // Carry the channel so ResolveAsync needs no re-fetch.
        SourceState = c,
    };

    // ── IFavoritable ────────────────────────────────────────────────────────────

    public bool IsFavorite(string itemId)
    {
        lock (_gate) return _favorites.Contains(itemId);
    }

    public void SetFavorite(string itemId, bool favorite)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        lock (_gate)
        {
            bool changed = favorite ? _favorites.Add(itemId) : _favorites.Remove(itemId);
            if (changed) SaveFavorites();
        }
    }

    public IReadOnlyCollection<string> GetFavoriteIds()
    {
        lock (_gate) return _favorites.ToArray();
    }

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private HashSet<string> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) { Log($"SXM: favorites read failed: {ex.Message}"); return new HashSet<string>(StringComparer.Ordinal); }
    }

    private void SaveFavorites()
    {
        try
        {
            var path = FavoritesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_favorites.ToList()));
        }
        catch (Exception ex) { Log($"SXM: favorites write failed: {ex.Message}"); }
    }

    // ── IHideable ────────────────────────────────────────────────────────────────

    // Category keys that mark a channel as a sports team / play-by-play channel (the ~200 the user
    // most often wants gone). Used by the "hide sports teams" quick action.
    private static readonly HashSet<string> SportsTeamKeys =
        new(StringComparer.OrdinalIgnoreCase) { "nflplay", "mlbpbp", "NHL_PBP", "NBA_PBP", "sportsplay", "college" };

    public IReadOnlyList<HideableItem> GetHideableItems()
    {
        // Best-effort from the cached lineup; empty if not yet loaded (the manage-UI opens after browse).
        var channels = _channels ?? LoadLineupCache() ?? [];
        var map = CategoryMap;
        return channels
            .OrderBy(c => c.SortNumber)
            .Select(c =>
            {
                var cat = c.Categories.Count > 0 ? c.Categories[0] : null;
                var super = cat != null ? map.SuperGroupFor(cat.Key) : SxmCategoryMap.SuperOther;
                return new HideableItem(
                    c.Id,
                    string.IsNullOrEmpty(c.Number) ? c.Name : $"{c.Number} · {c.Name}",
                    super,           // Group: Music/Talk/Sports/Other
                    cat?.Name);      // SubGroup: category (e.g. "Country", "NFL Play-by-Play")
            })
            .ToList();
    }

    public IReadOnlyCollection<string> GetHiddenIds()
    {
        lock (_gate) return _hidden.ToArray();
    }

    public void SetHidden(IReadOnlyCollection<string> itemIds, bool hidden)
    {
        if (itemIds is not { Count: > 0 }) return;
        lock (_gate)
        {
            bool changed = false;
            foreach (var id in itemIds)
                changed |= hidden ? _hidden.Add(id) : _hidden.Remove(id);
            if (changed) SaveHidden();
        }
    }

    /// <summary>Convenience id set for the "hide all sports team channels" quick action.</summary>
    public IReadOnlyCollection<string> SportsTeamChannelIds()
    {
        var channels = _channels ?? LoadLineupCache() ?? [];
        return channels
            .Where(c => c.Categories.Count > 0 && c.Categories.All(cat => SportsTeamKeys.Contains(cat.Key)))
            .Select(c => c.Id)
            .ToList();
    }

    private bool IsHidden(string id)
    {
        lock (_gate) return _hidden.Contains(id);
    }

    private string HiddenPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "hidden.json");

    private DateTime _hiddenLoadedUtc = DateTime.MinValue;

    private void ReloadHiddenIfChanged()
    {
        try
        {
            var path = HiddenPath;
            if (!File.Exists(path)) return;
            var mtime = File.GetLastWriteTimeUtc(path);
            if (mtime <= _hiddenLoadedUtc) return;
            lock (_gate)
            {
                _hiddenCache = LoadHidden();
                _hiddenLoadedUtc = mtime;
            }
        }
        catch { /* best-effort refresh */ }
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
        catch (Exception ex) { Log($"SXM: hidden read failed: {ex.Message}"); return new HashSet<string>(StringComparer.Ordinal); }
    }

    private void SaveHidden()
    {
        try
        {
            var path = HiddenPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_hidden.ToList()));
        }
        catch (Exception ex) { Log($"SXM: hidden write failed: {ex.Message}"); }
    }

    // ── IPlayableResolver ───────────────────────────────────────────────────────

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var channel = item.SourceState as SxmChannel
            ?? (await EnsureChannelsAsync(ct)).FirstOrDefault(c => c.Id == item.ItemId);
        if (channel == null) { Log($"SXM: channel '{item.ItemId}' not found."); return null; }

        var client = await EnsureClientAsync(ct);
        if (client == null) return null;

        var proxy = EnsureProxy(client);
        var localUrl = await proxy.SetChannelAsync(channel, ct);
        if (localUrl == null) { Log($"SXM: failed to resolve stream for '{channel.Id}'."); return null; }

        return new ResolvedStream(
            StreamTransport.Http,
            StreamLayout.AudioOnly,
            localUrl)
        {
            IsLiveStream = true,
        };
    }

    public Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
        // Live radio has no fixed duration/chapters — nothing to enrich.
        => Task.FromResult<SourceMetadata?>(null);

    // ── Internals ───────────────────────────────────────────────────────────────

    private async Task<SxmClient?> EnsureClientAsync(CancellationToken ct)
    {
        SxmClient? client;
        lock (_gate) client = _client;
        if (client is { IsAuthenticated: true }) return client;

        if (!IsConfigured) return null;
        client = new SxmClient(_username, _password, _region, Log);
        if (!await client.AuthenticateAsync(ct)) { Log("SXM: authentication failed."); return null; }
        lock (_gate) _client = client;
        return client;
    }

    private async Task<IReadOnlyList<SxmChannel>> EnsureChannelsAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channels != null) return _channels; }

        // Try the on-disk lineup cache first (the lineup seldom changes). Fresh cache avoids the
        // authenticated fetch entirely, making browse instant and offline-tolerant.
        var cached = LoadLineupCache();
        if (cached != null)
        {
            lock (_gate) _channels = cached;
            return cached;
        }

        var client = await EnsureClientAsync(ct);
        if (client == null) return [];
        var channels = await client.GetChannelsAsync(ct);
        if (channels.Count > 0)
        {
            lock (_gate) _channels = channels;
            SaveLineupCache(channels);
        }
        return channels;
    }

    // ── Lineup cache (per-instance, timestamped) ────────────────────────────────

    private const int LineupCacheDays = 7;
    private string LineupCachePath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "lineup.json");

    private IReadOnlyList<SxmChannel>? LoadLineupCache()
    {
        try
        {
            var path = LineupCachePath;
            if (!File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(LineupCacheDays))
                return null;
            var cache = JsonSerializer.Deserialize<LineupCache>(File.ReadAllText(path));
            if (cache is null || cache.Version != LineupCacheVersion) return null;
            return cache.Channels is { Count: > 0 } ? cache.Channels : null;
        }
        catch (Exception ex) { Log($"SXM: lineup cache read failed: {ex.Message}"); return null; }
    }

    private void SaveLineupCache(IReadOnlyList<SxmChannel> channels)
    {
        try
        {
            var path = LineupCachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new LineupCache(LineupCacheVersion, DateTimeOffset.UtcNow, channels)));
        }
        catch (Exception ex) { Log($"SXM: lineup cache write failed: {ex.Message}"); }
    }

    // Bump when the SxmChannel shape changes so old caches are rejected (tester-only: no migration).
    private const int LineupCacheVersion = 2;
    private sealed record LineupCache(int Version, DateTimeOffset FetchedUtc, IReadOnlyList<SxmChannel> Channels);

    private SxmProxy EnsureProxy(SxmClient client)
    {
        lock (_gate)
        {
            if (_proxy is { IsRunning: true }) return _proxy;
            _proxy = new SxmProxy(client, _proxyPort, Log);
            _proxy.Start();
            return _proxy;
        }
    }

    private void Log(string message) => _host?.Log(message);

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;
}
