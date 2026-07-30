using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Jellyfin;

/// <summary>
/// Per-node/-item state carried in <see cref="SourceCategory.SourceState"/> /
/// <see cref="SourceItem.SourceState"/> so the source never re-derives ids. <see cref="IsAudioOnly"/>
/// is meaningful for leaf items (drives audio-vs-video stream resolution).
/// </summary>
internal sealed record JellyfinState(string ItemId, bool IsAudioOnly);

/// <summary>Carried in a live-channel <see cref="SourceItem.SourceState"/> so the resolver can open a
/// live stream for the channel without re-browsing. Distinct from <see cref="JellyfinState"/> so the
/// resolver can tell a live channel from an ordinary item.</summary>
internal sealed record JellyfinLiveRef(string ChannelId);

/// <summary>
/// Jellyfin source instance. Browses the server's libraries as a folder tree (containers become
/// drill-in tiles, leaves become playable items), searches across the library, and resolves items
/// to direct HTTP stream URLs — finite, seekable content (no proxy, no live-stream handling).
///
/// STEREO: honors the instance's "Stereo audio" setting (and the host's
/// <see cref="PlaybackPreferences.PreferStereo"/>) so surround sources are downmixed to 2 channels —
/// essential on pinball cabs where surround channels drive mechanical/ball exciters.
/// </summary>
public sealed class JellyfinSource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IPlayableResolver, IConnectionTestable, IConfigurable,
    IFavoritable, IFavoriteCapture, IPlaybackStoppable, IPlaybackReportable, IPlaybackSuccessReportable
{
    private JellyfinClient? _client;
    private IPluginHost? _host;

    private string _serverUrl = "";
    private string _username = "";
    private string _password = "";
    private bool _stereoAudio;
    private bool _singleTile;
    private List<string> _selectedLibraryIds = [];

    // Ids of the server's Live TV view(s), captured during GetRootCategoriesAsync so BrowseAsync can
    // route them to the channel lineup instead of a generic folder listing.
    private readonly HashSet<string> _liveTvViewIds = new(StringComparer.Ordinal);

    // The single active live-TV playback session (one tuner at a time), tracked so it can be torn
    // down on stop/skip/shutdown via IPlaybackStoppable.
    private readonly object _liveGate = new();
    private JellyfinLiveSession? _activeLive;

    // Channels the host reported as failed to play (tuner busy, brief outage). Not hidden — the
    // channel stays visible and playable, badged ⊘, self-healing on a successful play.
    private readonly object _deadGate = new();
    private readonly HashSet<string> _dead = new(StringComparer.Ordinal);

    public JellyfinSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => JellyfinSourceProvider.JellyfinTypeId;
    public string DisplayName { get; set; } = "Jellyfin";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_serverUrl) && !string.IsNullOrWhiteSpace(_username);

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        EnsureClient();
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values)
    {
        ApplySettingsInternal(values);
        EnsureClient();
    }

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _serverUrl = Get(values, JellyfinSourceProvider.KeyServerUrl) ?? "";
        _username = Get(values, JellyfinSourceProvider.KeyUsername) ?? "";
        _password = Get(values, JellyfinSourceProvider.KeyPassword) ?? "";
        _stereoAudio = !bool.TryParse(Get(values, JellyfinSourceProvider.KeyStereoAudio), out var s) || s;
        // Default to stereo when unset/invalid — safest for cabs.

        _selectedLibraryIds = ParseLibraryIds(Get(values, JellyfinSourceProvider.KeyLibraries));

        // Tile mode: default (unset/unknown) is the historical "Per Library" behavior; only an
        // explicit "Single Tile" collapses the libraries under one root tile.
        _singleTile = string.Equals(
            Get(values, JellyfinSourceProvider.KeyTileMode),
            JellyfinSourceProvider.TileModeSingleTile,
            StringComparison.OrdinalIgnoreCase);

        // Force a fresh client with the new config on the next use.
        _client?.Configure(_serverUrl, _username, _password, _stereoAudio);
    }

    private void EnsureClient()
    {
        // A config-time transient source (library chooser / connection test) may never get
        // InitializeAsync, so _host can be null. Fall back to a shared HttpClient + no-op log so the
        // client still works for those one-off calls; the real host client is used once initialized.
        var http = _host?.HttpClient ?? SharedHttpClient;
        Action<string>? log = _host is { } h ? (s => h.Log(LogLevel.Debug, s)) : null;
        _client ??= new JellyfinClient(http, StableDeviceId(), log);
        _client.Configure(_serverUrl, _username, _password, _stereoAudio);
    }

    // Shared fallback for transient (config-time) sources built without a host.
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    // A stable-per-instance device id so the server tracks a single session.
    private string StableDeviceId() => $"phosphor-{InstanceId}";

    // ── IConnectionTestable ──────────────────────────────────────────────────────

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null)
            return new ConnectionTestResult(false, "Plug-in not initialized.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, message) = await _client.TestConnectionAsync(ct);
        sw.Stop();
        return new ConnectionTestResult(ok, message, ok ? sw.Elapsed : null);
    }

    // ── IBrowsable ───────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null) yield break;

        // Single Tile mode: one root tile for the whole server; the libraries become its children
        // (produced by browsing the ServerRoot sentinel). Default (Per Library) yields one root per
        // library. Note we still enumerate libraries in Single Tile mode's BrowseAsync so the Live TV
        // view ids get captured there.
        if (_singleTile)
        {
            yield return new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = ServerRootId,
                Title = DisplayName,
                Icon = "📚",
                HasSubCategories = true,
                SourceState = new JellyfinState(ServerRootId, IsAudioOnly: false),
            };
            yield break;
        }

        await foreach (var cat in FetchLibraryRootCategoriesAsync(ct))
            yield return cat;
    }

    /// <summary>Sentinel category id / item id marking the Single Tile server-root node.</summary>
    private const string ServerRootId = "__server_root__";

    /// <summary>
    /// Fetches the server's libraries (honoring the selected-library filter) as root
    /// <see cref="SourceCategory"/> tiles, capturing Live TV view ids as a side effect. Shared by both
    /// tile modes: emitted directly as home-screen tiles in Per Library mode, or nested under the
    /// single server tile in Single Tile mode.
    /// </summary>
    private async IAsyncEnumerable<SourceCategory> FetchLibraryRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client is null) yield break;

        IReadOnlyList<JellyfinItem> views;
        try
        {
            views = await _client.GetViewsAsync(ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"JellyfinSource: GetViews failed — {ex.Message}");
            yield break;
        }

        foreach (var v in views)
        {
            ct.ThrowIfCancellationRequested();

            // When the user has chosen specific libraries, show only those; empty = show all.
            if (_selectedLibraryIds.Count > 0 && !_selectedLibraryIds.Contains(v.Id))
                continue;

            var isLiveTv = string.Equals(v.CollectionType, "livetv", StringComparison.OrdinalIgnoreCase);
            if (isLiveTv) _liveTvViewIds.Add(v.Id);

            yield return new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = v.Id,
                // Prefix the instance name (e.g. "Jellyfin Movies") so libraries don't collide with
                // same-named tiles from other servers/sources (a Plex "Concerts" vs a Jellyfin one).
                Title = $"{DisplayName} {v.Name}",
                Icon = IconFor(v.CollectionType),
                ThumbnailUrl = _client.GetImageUrl(v.Id, v.ImageTag),
                HasSubCategories = true,
                SourceState = new JellyfinState(v.Id, IsAudioOnly: false),
            };
        }
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null) return new BrowseResult();

        // Single Tile server root: expand to the libraries (the same tiles Per Library mode surfaces
        // at the top level).
        if (category.CategoryId == ServerRootId ||
            (category.SourceState as JellyfinState)?.ItemId == ServerRootId)
        {
            var libs = new List<SourceCategory>();
            await foreach (var cat in FetchLibraryRootCategoriesAsync(ct))
                libs.Add(cat);
            return new BrowseResult { Categories = libs };
        }

        var parentId = (category.SourceState as JellyfinState)?.ItemId ?? category.CategoryId;

        // Live TV view → list channels as playable live leaves (not generic folder items).
        if (await IsLiveTvViewAsync(parentId, ct))
            return await BrowseLiveTvAsync(ct);

        JellyfinPage page;
        try
        {
            page = await _client.GetItemsAsync(parentId, ct: ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"JellyfinSource: Browse '{parentId}' failed — {ex.Message}");
            return new BrowseResult();
        }

        var categories = new List<SourceCategory>();
        var items = new List<SourceItem>();

        foreach (var it in page.Items)
        {
            if (it.IsFolder)
                categories.Add(ToCategory(it));
            else
                items.Add(ToItem(it));
        }

        return new BrowseResult { Categories = categories, Items = items };
    }

    /// <summary>True when a browse-node id is (one of) the server's Live TV view(s). Uses the set
    /// captured during root enumeration, falling back to a live lookup for durable navigation where
    /// the roots weren't enumerated first.</summary>
    private async Task<bool> IsLiveTvViewAsync(string id, CancellationToken ct)
    {
        lock (_liveGate) { }
        if (_liveTvViewIds.Contains(id)) return true;
        if (_client is null) return false;
        try
        {
            var views = await _client.GetViewsAsync(ct);
            foreach (var v in views)
                if (string.Equals(v.CollectionType, "livetv", StringComparison.OrdinalIgnoreCase))
                    _liveTvViewIds.Add(v.Id);
        }
        catch { /* best-effort */ }
        return _liveTvViewIds.Contains(id);
    }

    /// <summary>Lists the server's live channels as playable live leaves (⊘ badge when a channel was
    /// previously reported unavailable).</summary>
    private async Task<BrowseResult> BrowseLiveTvAsync(CancellationToken ct)
    {
        if (_client is null) return new BrowseResult();
        IReadOnlyList<JellyfinLiveChannel> channels;
        try
        {
            channels = await _client.GetLiveChannelsAsync(ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"JellyfinSource: GetLiveChannels failed — {ex.Message}");
            return new BrowseResult();
        }

        var items = channels.Select(ToLiveItem).ToList();
        return new BrowseResult { Items = items };
    }

    // ── ITextSearchCapable ───────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null || string.IsNullOrWhiteSpace(query)) yield break;

        IReadOnlyList<JellyfinItem> results;
        try
        {
            results = await _client.SearchAsync(query, ct: ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"JellyfinSource: Search '{query}' failed — {ex.Message}");
            yield break;
        }

        foreach (var it in results)
        {
            ct.ThrowIfCancellationRequested();
            if (!it.IsFolder)
                yield return ToItem(it);
        }
    }

    // ── IPlayableResolver ────────────────────────────────────────────────────────

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null) return null;

        // Live TV: open a live stream (server tunes + transcodes UDP→HLS) and return its URL. One tuner
        // at a time — opening a new channel closes the prior session first.
        if (item.SourceState is JellyfinLiveRef live)
        {
            try
            {
                await ReleaseActiveLiveAsync(ct);
                var session = await _client.OpenLiveStreamAsync(live.ChannelId, ct);
                lock (_liveGate) _activeLive = session;
                return new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, session.StreamUrl)
                {
                    IsLiveStream = true,
                    StartupTimeout = TimeSpan.FromSeconds(30),
                };
            }
            catch (Exception ex)
            {
                _host?.Log(LogLevel.Warning, $"JellyfinSource: open live channel '{live.ChannelId}' failed — {ex.Message}");
                throw; // let the host report the failure so the ⊘ badge is applied
            }
        }

        var state = item.SourceState as JellyfinState;
        var itemId = state?.ItemId ?? item.ItemId;
        var audioOnly = item.IsAudioOnly || (state?.IsAudioOnly ?? false);

        // Ensure we have a live access token: EnsureClient() → Configure() clears cached auth, so the
        // token must be (re)acquired here before building the stream URL — otherwise api_key comes out
        // empty and the server rejects the stream.
        try
        {
            await _client.AuthenticateAsync(ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"JellyfinSource: resolve auth failed — {ex.Message}");
            return null;
        }

        // Note: the stereo downmix is driven by the instance's Stereo audio setting inside the client;
        // prefs.PreferStereo is an additional host hint (both point the same direction on a cab).
        var url = _client.GetStreamUrl(itemId, audioOnly);

        var layout = audioOnly ? StreamLayout.AudioOnly : StreamLayout.Muxed;
        return new ResolvedStream(StreamTransport.Http, layout, url);
    }

    public async Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        EnsureClient();
        var itemId = (item.SourceState as JellyfinState)?.ItemId ?? item.ItemId;

        IReadOnlyList<ChapterMarker> chapters = item.Chapters ?? [];
        if (_client != null)
        {
            try
            {
                var raw = await _client.GetChaptersAsync(itemId, ct);
                if (raw.Count > 0)
                    chapters = raw.Select(c => new ChapterMarker(c.Name, c.Start)).ToList();
            }
            catch (Exception ex)
            {
                _host?.Log(LogLevel.Warning, $"JellyfinSource: GetChapters '{itemId}' failed — {ex.Message}");
            }
        }

        return new SourceMetadata(item.Duration, null, chapters);
    }

    // ── IPlaybackStoppable / retryable ⊘ badge ────────────────────────────────────

    /// <summary>Playback of an item stopped. For Live TV, close the active session to release the
    /// tuner/transcode. Non-live items are stateless and need nothing. Best-effort; never throws.</summary>
    public void ReleasePlayback(string itemId)
    {
        if (string.IsNullOrEmpty(itemId) || !itemId.StartsWith("livetv:", StringComparison.Ordinal))
            return;
        _ = Task.Run(async () =>
        {
            try { await ReleaseActiveLiveAsync(CancellationToken.None); }
            catch (Exception ex) { _host?.Log(LogLevel.Debug, $"JellyfinSource: ReleasePlayback failed — {ex.Message}"); }
        });
    }

    /// <summary>Closes the currently-tracked live session (if any).</summary>
    private async Task ReleaseActiveLiveAsync(CancellationToken ct)
    {
        JellyfinLiveSession? s;
        lock (_liveGate) { s = _activeLive; _activeLive = null; }
        if (s is not null && _client is not null)
            await _client.CloseLiveStreamAsync(s, ct);
    }

    public bool ReportPlaybackFailure(string itemId, PlaybackFailureKind kind)
    {
        if (string.IsNullOrEmpty(itemId) || !itemId.StartsWith("livetv:", StringComparison.Ordinal))
            return false;
        lock (_deadGate) _dead.Add(itemId);
        _host?.Log(LogLevel.Info, $"Jellyfin Live TV: '{itemId}' play failed — badged unavailable (retryable).");
        return false; // stays playable; the ⊘ badge conveys the state
    }

    public bool ReportPlaybackSuccess(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return false;
        lock (_deadGate)
        {
            if (_dead.Remove(itemId))
            {
                _host?.Log(LogLevel.Debug, $"Jellyfin Live TV: '{itemId}' played — cleared unavailable badge.");
                return true;
            }
        }
        return false;
    }

    private bool IsDead(string itemId)
    {
        lock (_deadGate) return _dead.Contains(itemId);
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────

    private SourceCategory ToCategory(JellyfinItem it) => new()
    {
        SourceInstanceId = InstanceId,
        CategoryId = it.Id,
        Title = it.Name,
        ThumbnailUrl = _client?.GetBestImageUrl(it),
        HasSubCategories = true,
        SourceState = new JellyfinState(it.Id, IsAudioOnly: false),
    };

    private SourceItem ToItem(JellyfinItem it)
    {
        var audioOnly = string.Equals(it.Type, "Audio", StringComparison.OrdinalIgnoreCase);
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = it.Id,
            Title = it.Name,
            Subtitle = it.AlbumArtist ?? it.Album,
            ThumbnailUrl = _client?.GetBestImageUrl(it),
            IsAudioOnly = audioOnly,
            Duration = it.Duration,
            SourceState = new JellyfinState(it.Id, audioOnly),
        };
    }

    /// <summary>Maps a live channel to a playable live leaf. The title is enriched with the current
    /// program when guide data is present (e.g. "2.1 WFMY-HD – Evening News"). Resolution is deferred
    /// to play time (a live ref in SourceState) so browsing never opens a tuner.</summary>
    private SourceItem ToLiveItem(JellyfinLiveChannel ch)
    {
        var name = string.IsNullOrEmpty(ch.ChannelNumber) ? ch.Name : $"{ch.ChannelNumber} {ch.Name}";
        var title = string.IsNullOrEmpty(ch.CurrentProgram) ? name : $"{name} – {ch.CurrentProgram}";
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = $"livetv:{ch.Id}",
            Title = title,
            ThumbnailUrl = _client?.GetImageUrl(ch.Id, ch.ImageTag),
            IsLiveStream = true,
            ShowUnavailableBadge = IsDead($"livetv:{ch.Id}"),
            SourceState = new JellyfinLiveRef(ch.Id),
        };
    }

    private static string? IconFor(string? collectionType) => collectionType?.ToLowerInvariant() switch
    {
        "music" => "🎵",
        "movies" => "🎬",
        "tvshows" => "📺",
        "musicvideos" => "🎤",
        "homevideos" => "📹",
        _ => null,
    };

    // ── IConfigurable ────────────────────────────────────────────────────────────

    public IReadOnlyList<ConfigAction> GetConfigActions() =>
    [
        new(JellyfinSourceProvider.ActionBrowseLibraries, "Browse libraries…",
            "List the server's libraries and choose which become tiles."),
    ];

    public async Task<ConfigSelection> InvokeConfigActionAsync(string actionId, CancellationToken ct = default)
    {
        EnsureClient();
        if (actionId != JellyfinSourceProvider.ActionBrowseLibraries || _client is null)
            return new ConfigSelection([]);

        IReadOnlyList<JellyfinItem> views;
        try
        {
            views = await _client.GetViewsAsync(ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"JellyfinSource: config GetViews failed — {ex.Message}");
            return new ConfigSelection([]);
        }

        // Pre-check the currently-selected libraries; when none are selected yet, default all on
        // (matches the "empty = show all" browse behavior).
        var selectAll = _selectedLibraryIds.Count == 0;
        var options = views
            .Select(v => new ConfigOption(
                v.Id,
                string.IsNullOrEmpty(v.CollectionType) ? v.Name : $"{v.Name} ({v.CollectionType})",
                selectAll || _selectedLibraryIds.Contains(v.Id)))
            .ToList();

        return new ConfigSelection(options, AllowMultiple: true, Title: "Jellyfin libraries");
    }

    public async Task<IReadOnlyDictionary<string, string?>> ApplyConfigActionAsync(
        string actionId,
        IReadOnlyList<ConfigOptionResult> results,
        IReadOnlyDictionary<string, string?> currentSettings,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var result = new Dictionary<string, string?>(currentSettings);
        if (actionId != JellyfinSourceProvider.ActionBrowseLibraries)
            return result;

        var selected = results.Where(r => r.IsSelected).Select(r => r.OptionId).ToList();
        result[JellyfinSourceProvider.KeyLibraries] = JsonSerializer.Serialize(selected);
        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the persisted library selection. The host's inline library editor stores an array of
    /// objects with a <c>Key</c> field (shared with Plex's editor), while the plug-in's own config
    /// action stores a bare string array — accept either so both paths round-trip.
    /// </summary>
    private static List<string> ParseLibraryIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var ids = new List<string>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    if (e.GetString() is { } s) ids.Add(s);
                }
                else if (e.ValueKind == JsonValueKind.Object)
                {
                    if ((e.TryGetProperty("Key", out var k) || e.TryGetProperty("key", out k)) &&
                        k.ValueKind == JsonValueKind.String && k.GetString() is { } id)
                        ids.Add(id);
                }
            }
            return ids;
        }
        catch
        {
            return [];
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;

    // ── IFavoritable / IFavoriteCapture ──────────────────────────────────────────
    // Jellyfin resolves streams fresh by item id, so favorites persist a small record (id, display,
    // audio flag, container flag). Leaves and containers (artist/album) both rebuild from the id via
    // a JellyfinState; containers return IsContainer=true so the host drills in / play-alls.

    private sealed class JfFavorite
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? ThumbnailUrl { get; set; }
        public double? DurationSeconds { get; set; }
        public bool IsAudioOnly { get; set; }
        public bool IsContainer { get; set; }
    }

    private readonly object _favGate = new();
    private Dictionary<string, JfFavorite>? _favoritesCache;
    private Dictionary<string, JfFavorite> FavStore => _favoritesCache ??= LoadFavorites();

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
                if (!FavStore.ContainsKey(itemId))
                    FavStore[itemId] = new JfFavorite { Id = itemId, Title = itemId };
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
            FavStore[item.ItemId] = new JfFavorite
            {
                Id = item.ItemId,
                Title = item.Title,
                Subtitle = item.Subtitle,
                ThumbnailUrl = item.ThumbnailUrl,
                DurationSeconds = item.Duration?.TotalSeconds,
                IsAudioOnly = item.IsAudioOnly,
                IsContainer = item.IsContainer,
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
        JfFavorite? f;
        lock (_favGate) f = FavStore.TryGetValue(itemId, out var rec) ? rec : null;
        if (f is null) return null;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            Subtitle = f.Subtitle,
            ThumbnailUrl = f.ThumbnailUrl,
            IsAudioOnly = f.IsAudioOnly,
            IsContainer = f.IsContainer,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            SourceState = new JellyfinState(f.Id, f.IsAudioOnly),
        };
    }

    private Dictionary<string, JfFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, JfFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<JfFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"Jellyfin: favorites read failed: {ex.Message}");
            return new Dictionary<string, JfFavorite>(StringComparer.Ordinal);
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
            _host?.Log(LogLevel.Warning, $"Jellyfin: favorites write failed: {ex.Message}");
        }
    }
}
