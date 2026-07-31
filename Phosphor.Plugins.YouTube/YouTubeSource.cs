using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;
using Phosphor.Search;
using Phosphor.Video;

namespace Phosphor.Plugins.YouTube;

/// <summary>
/// In-box YouTube source. Composes the existing discovery (<see cref="ISearchEngine"/>)
/// and video (<see cref="IVideoEngine"/>) seams and presents them through the plug-in
/// contract. The YoutubeExplode-vs-yt-dlp engine choice is an internal detail driven by
/// settings — the host sees a single source. Engines are created via the existing
/// factories, which keep the "fall back to an available engine" safety net.
/// </summary>
/// <remarks>
/// This is statically referenced (in-box), not scanned, so it may use the host's
/// YoutubeExplode package and existing engine code directly. It is a pure data producer:
/// it never touches UI or assumes a thread.
/// </remarks>
public sealed class YouTubeSource : IPhosphorSource, ITextSearchCapable, IPlaylistChannelDiscovery, IPlayableResolver, IDownloadable, IUpdatable, IConnectionTestable, IFavoritable, IFavoriteCapture, IBrowsable, IPagedBrowsable, IContainerPlayPolicy, ISearchHintProvider, ISavedSearchCategories, IEditableSavedSearchCategories, IResultCachePolicy
{
    private HttpClient? _http;
    private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private IPluginHost? _host;

    private SearchEngineKind _searchKind = SearchEngineKind.YoutubeExplode;
    private VideoEngineKind _videoKind = VideoEngineKind.YoutubeExplode;
    private VideoQualityPreference _quality = VideoQualityPreference.High;
    private bool _preferStereo = true;

    // User-defined category tiles (Rock/Pop/…). Plug-in-owned: seeded from the bundled
    // default_categories.json on first run, then persisted in the instance settings blob.
    private readonly object _categoriesGate = new();
    private List<YouTubeCategory> _categories = [];

    private ISearchEngine _search;
    private IVideoEngine _video;

    /// <summary>
    /// Diagnostics sink threaded into the engines/factories. Reads <see cref="_host"/> at call time so
    /// it is a safe no-op during the constructor (before <see cref="InitializeAsync"/> supplies the
    /// host) and routes through <see cref="IPluginHost.Log(LogLevel, string)"/> (Path A) once wired —
    /// so YouTube engine logs land in the host log file and honor the verbosity setting. Category is
    /// folded into the message since the contract carries only a level + message.
    /// </summary>
    private void EngineLog(LogLevel level, string category, string message) =>
        _host?.Log(level, $"{category}: {message}");

    public YouTubeSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        // Engines are built here with no host HttpClient yet; InitializeAsync adopts the host's
        // shared client and rebuilds the search engine so the configured network timeout applies.
        ApplySettingsInternal(settings);
        _search ??= SearchEngineFactory.Create(_searchKind, _http, EngineLog);
        _video ??= VideoEngineFactory.Create(_videoKind, EngineLog);
    }

    public string InstanceId { get; }
    public string TypeId => YouTubeSourceProvider.YouTubeTypeId;
    public string DisplayName { get; set; } = "YouTube";

    /// <summary>YouTube needs no credentials — it is always considered configured.</summary>
    public bool IsConfigured => true;

    /// <summary>Search-box hint advertising YouTube's query grammar (see <see cref="ISearchHintProvider"/>).</summary>
    public string? SearchHint => "...try channel:<name>, playlist:<name>, channels:<name>, playlists:<name>, min:5m, max:30m";

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        // Adopt the host's shared HttpClient so its connection pooling and configured network
        // timeout apply to YouTube discovery. The search engine is the http consumer — rebuild it
        // with the host client (the video engine's yt-dlp/YoutubeExplode paths don't take it).
        _http = host.HttpClient;
        _search = SearchEngineFactory.Create(_searchKind, _http, EngineLog);
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    /// <summary>
    /// Lightweight reachability check — YouTube needs no credentials, so this just confirms the
    /// network can reach youtube.com within the HttpClient timeout.
    /// </summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var http = _http ?? _sharedHttp;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://www.youtube.com/");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            return resp.IsSuccessStatusCode
                ? new ConnectionTestResult(true, "Reachable.", sw.Elapsed)
                : new ConnectionTestResult(false, $"Unexpected response: {(int)resp.StatusCode} {resp.ReasonPhrase}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionTestResult(false, $"Not reachable: {ex.Message}", sw.Elapsed);
        }
    }

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _searchKind = ParseEnum(values, YouTubeSourceProvider.KeySearchEngine, SearchEngineKind.YoutubeExplode);
        _videoKind = ParseEnum(values, YouTubeSourceProvider.KeyVideoEngine, VideoEngineKind.YoutubeExplode);
        _quality = ParseEnum(values, YouTubeSourceProvider.KeyVideoQuality, VideoQualityPreference.High);
        _preferStereo = ParseBool(values, YouTubeSourceProvider.KeyPreferStereo, true);

        // Master on/off switch for the yt-dlp download-path throttle (anti-403). The detailed knobs
        // live in download_throttle.json; this just toggles whether they're applied. Static, so it
        // spans engine rebuilds triggered by settings changes.
        YtDlpVideoEngine.ThrottleDownloads = ParseBool(values, YouTubeSourceProvider.KeyThrottleDownloads, true);

        // Load the user's categories from the persisted blob; seed from the plug-in defaults on
        // first run (empty/absent blob) so a fresh instance still ships the baked-in tiles.
        LoadCategories(values.TryGetValue(YouTubeSourceProvider.KeyCategories, out var catJson) ? catJson : null);

        // Re-create engines through the existing factories (which keep the availability
        // fallback), so a settings change takes effect immediately.
        _search = SearchEngineFactory.Create(_searchKind, _http, EngineLog);
        _video = VideoEngineFactory.Create(_videoKind, EngineLog);
        _host?.Log(LogLevel.Debug, $"YouTubeSource: search={_searchKind} video={_videoKind} quality={_quality} stereo={_preferStereo}");
    }

    // ── Categories (plug-in-owned tiles) ───────────────────────────────────────

    /// <summary>Loads the category list from the persisted JSON blob, seeding from the bundled
    /// default_categories.json when the blob is empty/absent (first run).</summary>
    private void LoadCategories(string? json)
    {
        var list = YouTubeCategoryStore.Deserialize(json, (lvl, s) => _host?.Log(lvl, s));
        if (list.Count == 0)
            list = YouTubeCategoryStore.LoadDefaults((lvl, s) => _host?.Log(lvl, s));
        lock (_categoriesGate)
            _categories = list;
    }

    /// <summary>A snapshot of the current user categories, ordered by <see cref="YouTubeCategory.SortOrder"/>.</summary>
    public IReadOnlyList<YouTubeCategory> Categories
    {
        get { lock (_categoriesGate) return _categories.OrderBy(c => c.SortOrder).ToList(); }
    }

    // ── ISavedSearchCategories ─────────────────────────────────────────────────

    /// <summary>Surfaces the user's YouTube categories to the host as source-bound saved-search
    /// tiles. The host runs each <see cref="SavedSearchCategory.SearchTerm"/> through its own query
    /// grammar bound to this source.</summary>
    public IReadOnlyList<SavedSearchCategory> GetSavedSearchCategories()
    {
        lock (_categoriesGate)
            return _categories
                .OrderBy(c => c.SortOrder)
                .Select(c => new SavedSearchCategory(c.Id, c.Name, c.Icon, c.SearchTerm))
                .ToList();
    }

    // ── IEditableSavedSearchCategories ─────────────────────────────────────────

    /// <summary>Translates an edited category list into an updated settings blob (JSON under
    /// <see cref="YouTubeSourceProvider.KeyCategories"/>). Assigns ids to new rows, drops empty
    /// rows (no name AND no search term), and renumbers <c>SortOrder</c> by the incoming order.</summary>
    public IReadOnlyDictionary<string, string?> ApplySavedSearchCategories(
        IReadOnlyList<SavedSearchCategory> categories,
        IReadOnlyDictionary<string, string?> currentSettings)
    {
        var list = new List<YouTubeCategory>();
        int order = 0;
        foreach (var c in categories)
        {
            if (string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.SearchTerm))
                continue;
            list.Add(new YouTubeCategory
            {
                Id = string.IsNullOrEmpty(c.Id) ? Guid.NewGuid().ToString("N") : c.Id,
                Name = c.Name?.Trim() ?? "",
                Icon = c.Icon?.Trim() ?? "",
                SearchTerm = c.SearchTerm?.Trim() ?? "",
                SortOrder = order++,
            });
        }

        // Reflect the edit immediately in this live instance too.
        lock (_categoriesGate)
            _categories = list;

        var result = new Dictionary<string, string?>(currentSettings)
        {
            [YouTubeSourceProvider.KeyCategories] = YouTubeCategoryStore.Serialize(list),
        };
        return result;
    }

    /// <summary>The plug-in's built-in default categories, for a "restore defaults" affordance.</summary>
    public IReadOnlyList<SavedSearchCategory> GetDefaultSavedSearchCategories() =>
        YouTubeCategoryStore.LoadDefaults((lvl, s) => _host?.Log(lvl, s))
            .OrderBy(c => c.SortOrder)
            .Select(c => new SavedSearchCategory(c.Id, c.Name, c.Icon, c.SearchTerm))
            .ToList();

    // ── IResultCachePolicy ─────────────────────────────────────────────────────

    /// <summary>YouTube result pages (category/playlist searches) are stable enough to cache; use
    /// the host's default max age.</summary>
    public ResultCachePolicy GetResultCachePolicy() => new(Cache: true);

    // ── ITextSearchCapable ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var v in _search.SearchVideosAsync(query, ct).WithCancellation(ct))
            yield return YouTubeMappings.ToSourceItem(v, InstanceId);
    }

    // ── IPlaylistChannelDiscovery ──────────────────────────────────────────────

    public Task<string?> ResolvePlaylistIdAsync(
        string nameIdOrUrl, Action<string>? onFoundByName = null, CancellationToken ct = default)
        => _search.ResolvePlaylistIdAsync(nameIdOrUrl, onFoundByName, ct);

    public async IAsyncEnumerable<SourceItem> GetPlaylistItemsAsync(
        string playlistId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var v in _search.GetPlaylistVideosAsync(playlistId, ct).WithCancellation(ct))
            yield return YouTubeMappings.ToSourceItem(v, InstanceId);
    }

    public async IAsyncEnumerable<SourceItem> GetChannelUploadsAsync(
        string handleOrUser, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var v in _search.GetChannelUploadsAsync(handleOrUser, ct).WithCancellation(ct))
            yield return YouTubeMappings.ToSourceItem(v, InstanceId);
    }

    public async IAsyncEnumerable<SourceItem> SearchChannelsAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var c in _search.SearchChannelsAsync(query, ct).WithCancellation(ct))
            yield return YouTubeMappings.ToContainerSourceItem(c, InstanceId);
    }

    public async IAsyncEnumerable<SourceItem> SearchPlaylistsAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var p in _search.SearchPlaylistsAsync(query, ct).WithCancellation(ct))
            yield return YouTubeMappings.ToContainerSourceItem(p, InstanceId);
    }

    // ── IBrowsable ─────────────────────────────────────────────────────────────
    // YouTube has no static browse tree of its own (no root categories → no stray "YouTube" browse
    // tile). It implements IBrowsable ONLY so the host's generic drill-in machinery can expand a
    // favorited/searched channel or playlist container node into its videos, reusing the same path
    // every other container source uses.

    public IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(CancellationToken ct = default)
        => EmptyCategories();

    private static async IAsyncEnumerable<SourceCategory> EmptyCategories()
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        // Channel/playlist videos are a large, flat list — return NO leaves here so the host drives
        // lazy paging through IPagedBrowsable.BrowsePageAsync (below), instead of loading a 1000-video
        // channel in one shot. A non-container node yields nothing.
        return Task.FromResult(new BrowseResult());
    }

    // ── IPagedBrowsable ────────────────────────────────────────────────────────
    // YouTube's engines enumerate uploads/playlists as a forward-only cursor stream, not by offset.
    // To serve the host's offset-based paging we keep one live enumerator per browse node and pull
    // the next `count` items each call, tracking how far we've advanced. A new node (or a rewind to
    // offset 0) restarts the stream.

    private sealed class PagedStream
    {
        public required string CategoryId { get; init; }
        public required IAsyncEnumerator<VideoItem> Enumerator { get; init; }
        public int Served { get; set; }
        public bool Exhausted { get; set; }
    }

    private readonly object _pageGate = new();
    private PagedStream? _pagedStream;

    public async Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default)
    {
        if (!YouTubeMappings.TryParseContainerId(category.CategoryId, out var kind, out var rawId))
            return new BrowsePage();

        PagedStream stream;
        lock (_pageGate)
        {
            // Start (or restart) the cursor when the node changes or the host rewinds to the top.
            if (_pagedStream is null || _pagedStream.CategoryId != category.CategoryId || offset == 0)
            {
                if (_pagedStream is not null)
                    _ = _pagedStream.Enumerator.DisposeAsync().AsTask();

                var playlistId = kind == ChannelPlaylistKind.Channel
                    ? YouTubeMappings.ChannelUploadsPlaylistId(rawId)
                    : rawId;
                _pagedStream = new PagedStream
                {
                    CategoryId = category.CategoryId,
                    Enumerator = _search.GetPlaylistVideosAsync(playlistId, ct).GetAsyncEnumerator(ct),
                };
            }
            stream = _pagedStream;
        }

        var items = new List<SourceItem>();
        // Skip any already-served items if the host asks for a window past where we are (defensive —
        // the host pages sequentially, so normally Served == offset already).
        while (stream.Served < offset && !stream.Exhausted)
        {
            if (!await stream.Enumerator.MoveNextAsync()) { stream.Exhausted = true; break; }
            stream.Served++;
        }

        while (items.Count < count && !stream.Exhausted)
        {
            if (!await stream.Enumerator.MoveNextAsync()) { stream.Exhausted = true; break; }
            items.Add(YouTubeMappings.ToSourceItem(stream.Enumerator.Current, InstanceId));
            stream.Served++;
        }

        // TotalSize is unknown for a forward-only cursor: report "at least what we've served, plus one
        // more page" while items keep coming, and the exact served count once exhausted. This keeps the
        // host's "load more" affordance alive until the stream truly ends.
        var total = stream.Exhausted ? stream.Served : stream.Served + count;
        return new BrowsePage { Items = items, TotalSize = total };
    }

    // ── IContainerPlayPolicy ───────────────────────────────────────────────────
    // A channel/playlist container is a drill-in shortcut, not a "queue all N uploads" action — the
    // host hides the play-all affordance and offers browse only.
    public ContainerPlayAll GetPlayAllBehavior(SourceItem container)
        => YouTubeMappings.TryParseContainerId(container.ItemId, out _, out _)
            ? ContainerPlayAll.None
            : ContainerPlayAll.QueueAll;

    // ── IPlayableResolver ──────────────────────────────────────────────────────

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        // Identify the active video engine on the playback path — otherwise the log is silent about
        // whether a resolve/stream used yt-dlp or YoutubeExplode (they are configured independently of
        // the search engine), which makes throttling/403 issues hard to attribute.
        _host?.Log(LogLevel.Debug, $"YouTubeSource: resolve via {_videoKind} (audioOnly={prefs.AudioOnly})");
        var streams = await _video.ResolveStreamsAsync(
            YouTubeMappings.VideoIdOf(item),
            MapQuality(prefs.MaxQuality),
            prefs.PreferStereo || _preferStereo,
            prefs.AudioOnly,
            ct);

        return streams == null ? null : YouTubeMappings.ToResolvedStream(streams);
    }

    public async Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        var meta = await _video.GetMetadataAsync(YouTubeMappings.VideoIdOf(item), ct);
        return meta == null ? null : YouTubeMappings.ToSourceMetadata(meta);
    }

    // ── IDownloadable ──────────────────────────────────────────────────────────

    public async Task<SourceDownload?> DownloadAsync(
        SourceItem item,
        PlaybackPreferences prefs,
        string destinationDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // The existing engine does not surface incremental progress; report start/finish
        // so callers relying on the hook still see terminal states.
        progress?.Report(0);
        // Identify the active video engine on the download (cache/prefetch) path. YouTube throttles
        // downloads (403) far more aggressively than resolves, so attributing the engine here makes
        // those failures diagnosable at a glance.
        _host?.Log(LogLevel.Debug, $"YouTubeSource: download via {_videoKind} (dir={destinationDir})");
        var download = await _video.DownloadStreamsAsync(
            YouTubeMappings.VideoIdOf(item),
            MapQuality(prefs.MaxQuality),
            prefs.PreferStereo || _preferStereo,
            destinationDir,
            ct);
        progress?.Report(1);

        return download == null ? null : YouTubeMappings.ToSourceDownload(download);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // The plug-in's quality preference wins when the caller supplied a real ceiling;
    // otherwise fall back to the configured default.
    private VideoQualityPreference MapQuality(VideoQuality q) => YouTubeMappings.ToQualityPreference(q);

    // ── IUpdatable (yt-dlp self-update) ────────────────────────────────────────

    /// <summary>Only the external yt-dlp tool is updatable; YoutubeExplode is compiled-in.</summary>
    public bool SupportsUpdate =>
        _searchKind == SearchEngineKind.YtDlp || _videoKind == VideoEngineKind.YtDlp;

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        if (!SupportsUpdate) return null;
        return await new YtDlpUpdater(log: EngineLog).GetVersionAsync(ct);
    }

    public async Task<UpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        if (!SupportsUpdate)
            return new UpdateResult(UpdateStatus.NotSupported, null, null, null);

        var r = await new YtDlpUpdater(log: EngineLog).UpdateAsync(ct);
        var status = r.Status switch
        {
            YtDlpUpdateStatus.Updated => UpdateStatus.Updated,
            YtDlpUpdateStatus.AlreadyCurrent => UpdateStatus.AlreadyCurrent,
            _ => UpdateStatus.Failed,
        };
        return new UpdateResult(status, r.OldVersion, r.NewVersion, r.Error);
    }

    private static T ParseEnum<T>(IReadOnlyDictionary<string, string?> values, string key, T fallback)
        where T : struct, Enum
        => values.TryGetValue(key, out var raw) && Enum.TryParse<T>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static bool ParseBool(IReadOnlyDictionary<string, string?> values, string key, bool fallback)
        => values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    // ── IFavoritable / IFavoriteCapture ────────────────────────────────────────
    // YouTube favorites are video ids OR namespaced channel/playlist container ids, plus a light
    // display record (so the aggregated tile and GetFavorite work without a network probe). Videos
    // surface in the host-level global Favorites tile; a favorited channel/playlist rebuilds as a
    // browsable container that drills in via IBrowsable. YouTube has no stable per-source browse tree
    // to host its own Favorites node.

    private sealed record YtFavorite(
        string Id, string Title, string Author, string? ThumbnailUrl, double? DurationSeconds, bool IsContainer);

    private readonly object _favGate = new();
    private Dictionary<string, YtFavorite>? _favoritesCache;
    private Dictionary<string, YtFavorite> Favorites => _favoritesCache ??= LoadFavorites();

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    public bool IsFavorite(string itemId)
    {
        lock (_favGate) return Favorites.ContainsKey(itemId);
    }

    public void SetFavorite(string itemId, bool favorite)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        lock (_favGate)
        {
            bool changed;
            if (favorite)
            {
                changed = !Favorites.ContainsKey(itemId);
                if (changed)
                {
                    // A light placeholder; RememberFavorite refines it with the rich display data the
                    // host captured at star-time. A container id (channel:/playlist:) marks a drill-in.
                    var isContainer = YouTubeMappings.TryParseContainerId(itemId, out _, out _);
                    Favorites[itemId] = new YtFavorite(
                        itemId, DefaultTitle(itemId), "", null, null, isContainer);
                }
            }
            else
            {
                changed = Favorites.Remove(itemId);
            }
            if (changed) SaveFavorites();
        }
    }

    public void RememberFavorite(FavoriteCapture item)
    {
        if (string.IsNullOrEmpty(item.ItemId)) return;
        lock (_favGate)
        {
            if (!Favorites.ContainsKey(item.ItemId)) return;
            Favorites[item.ItemId] = new YtFavorite(
                item.ItemId,
                string.IsNullOrEmpty(item.Title) ? DefaultTitle(item.ItemId) : item.Title,
                item.Subtitle ?? "",
                item.ThumbnailUrl,
                item.Duration?.TotalSeconds,
                item.IsContainer || YouTubeMappings.TryParseContainerId(item.ItemId, out _, out _));
            SaveFavorites();
        }
    }

    public IReadOnlyCollection<string> GetFavoriteIds()
    {
        lock (_favGate) return Favorites.Keys.ToArray();
    }

    /// <summary>
    /// Rebuilds a favorited item: a browsable container for a channel/playlist id (drills in via
    /// <see cref="BrowseAsync"/>), or a playable video (resolved via yt-dlp at play time) otherwise.
    /// Works from the durable id alone, so it survives a restart.
    /// </summary>
    public SourceItem? GetFavorite(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        YtFavorite? f;
        lock (_favGate) f = Favorites.TryGetValue(itemId, out var rec) ? rec : null;
        if (f is null) return null;

        if (YouTubeMappings.TryParseContainerId(itemId, out var kind, out var rawId))
            return new SourceItem
            {
                SourceInstanceId = InstanceId,
                ItemId = itemId,
                Title = f.Title,
                Subtitle = string.IsNullOrEmpty(f.Author) ? null : f.Author,
                ThumbnailUrl = f.ThumbnailUrl,
                IsContainer = true,
                SourceState = new YtContainerState(kind, rawId),
            };

        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = itemId,
            Title = f.Title,
            Subtitle = string.IsNullOrEmpty(f.Author) ? null : f.Author,
            ThumbnailUrl = f.ThumbnailUrl,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            SourceState = itemId,
        };
    }

    /// <summary>Best-effort placeholder title until the host's rich star-time capture arrives.</summary>
    private static string DefaultTitle(string itemId) =>
        YouTubeMappings.TryParseContainerId(itemId, out var kind, out var rawId)
            ? $"YouTube {(kind == ChannelPlaylistKind.Channel ? "channel" : "playlist")} {rawId}"
            : $"YouTube {itemId}";

    private Dictionary<string, YtFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, YtFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<YtFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"YouTube: favorites read failed: {ex.Message}");
            return new Dictionary<string, YtFavorite>(StringComparer.Ordinal);
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var path = FavoritesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Favorites.Values.ToList()));
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"YouTube: favorites write failed: {ex.Message}");
        }
    }
}
