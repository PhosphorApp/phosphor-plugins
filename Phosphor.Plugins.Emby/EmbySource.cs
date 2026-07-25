using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Emby;

/// <summary>
/// Per-node/-item state carried in <see cref="SourceCategory.SourceState"/> /
/// <see cref="SourceItem.SourceState"/> so the source never re-derives ids. <see cref="IsAudioOnly"/>
/// is meaningful for leaf items (drives audio-vs-video stream resolution). <see cref="CollectionType"/>
/// is the owning library's type (music, movies, musicvideos, …), carried down the tree so browse can
/// flatten per-title folders in video libraries. <see cref="MusicLevel"/> tracks position in the
/// music entity graph (library → artists → albums → tracks) so music libraries browse by entity type
/// like the web UI (giving artists/albums their own artwork) instead of by raw folder.
/// </summary>
internal sealed record EmbyState(
    string ItemId,
    bool IsAudioOnly,
    string? CollectionType = null,
    EmbyMusicLevel MusicLevel = EmbyMusicLevel.None);

/// <summary>Position within the music entity graph, used to drive entity-typed browse queries.</summary>
internal enum EmbyMusicLevel
{
    None = 0,   // not a music library (fall back to folder/leaf browse)
    Library,    // the music library root → list artists
    Artist,     // an artist → list albums
    Album,      // an album → list tracks
}

/// <summary>
/// Emby source instance. Browses the server's libraries as a folder tree (containers become
/// drill-in tiles, leaves become playable items), searches across the library, and resolves items
/// to direct HTTP stream URLs — finite, seekable content (no proxy, no live-stream handling).
///
/// STEREO: honors the instance's "Stereo audio" setting (and the host's
/// <see cref="PlaybackPreferences.PreferStereo"/>) so surround sources are downmixed to 2 channels —
/// essential on pinball cabs where surround channels drive mechanical/ball exciters.
/// </summary>
public sealed class EmbySource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IPlayableResolver, IConnectionTestable, IConfigurable,
    IFavoritable, IFavoriteCapture
{
    private EmbyClient? _client;
    private IPluginHost? _host;

    private string _serverUrl = "";
    private string _username = "";
    private string _password = "";
    private bool _stereoAudio;
    private List<string> _selectedLibraryIds = [];

    public EmbySource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => EmbySourceProvider.EmbyTypeId;
    public string DisplayName { get; set; } = "Emby";

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
        _serverUrl = Get(values, EmbySourceProvider.KeyServerUrl) ?? "";
        _username = Get(values, EmbySourceProvider.KeyUsername) ?? "";
        _password = Get(values, EmbySourceProvider.KeyPassword) ?? "";
        _stereoAudio = !bool.TryParse(Get(values, EmbySourceProvider.KeyStereoAudio), out var s) || s;
        // Default to stereo when unset/invalid — safest for cabs.

        _selectedLibraryIds = ParseLibraryIds(Get(values, EmbySourceProvider.KeyLibraries));

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
        _client ??= new EmbyClient(http, StableDeviceId(), log);
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

        IReadOnlyList<EmbyItem> views;
        try
        {
            views = await _client.GetViewsAsync(ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"EmbySource: GetViews failed — {ex.Message}");
            yield break;
        }

        foreach (var v in views)
        {
            ct.ThrowIfCancellationRequested();

            // When the user has chosen specific libraries, show only those; empty = show all.
            if (_selectedLibraryIds.Count > 0 && !_selectedLibraryIds.Contains(v.Id))
                continue;

            yield return new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = v.Id,
                // Prefix the instance name (e.g. "Emby Movies") so libraries don't collide with
                // same-named tiles from other servers/sources (a Plex "Concerts" vs an Emby one).
                Title = $"{DisplayName} {v.Name}",
                Icon = IconFor(v.CollectionType),
                ThumbnailUrl = _client.GetImageUrl(v.Id, v.ImageTag),
                HasSubCategories = true,
                SourceState = new EmbyState(
                    v.Id, IsAudioOnly: false, v.CollectionType,
                    // Music libraries browse via the entity graph (artists → albums → tracks) so
                    // artists/albums get their own artwork, matching the web UI.
                    IsMusic(v.CollectionType) ? EmbyMusicLevel.Library : EmbyMusicLevel.None),
            };
        }
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null) return new BrowseResult();

        var state = category.SourceState as EmbyState;
        var parentId = state?.ItemId ?? category.CategoryId;
        var collectionType = state?.CollectionType;

        // Music libraries: browse the entity graph (library → artists → albums → tracks) so artist
        // and album tiles carry their own artwork (the web-UI shape), rather than raw folders.
        if (state?.MusicLevel is EmbyMusicLevel.Library or EmbyMusicLevel.Artist or EmbyMusicLevel.Album)
            return await BrowseMusicAsync(state, ct);

        // Movie / music-video libraries store each title in its own folder. Listing children flatly
        // would surface those folders as drill-in tiles wrapping a single item. For these libraries we
        // recurse and filter to the leaf video type so folders collapse into playable items (matching
        // the Emby/Jellyfin web clients). TV keeps its natural hierarchy.
        var (recursive, includeTypes) = collectionType?.ToLowerInvariant() switch
        {
            "movies" => (true, "Movie"),
            "musicvideos" => (true, "MusicVideo"),
            "homevideos" => (true, "Video"),
            _ => (false, (string?)null),
        };

        EmbyPage page;
        try
        {
            page = await _client.GetItemsAsync(
                parentId, includeItemTypes: includeTypes, recursive: recursive, ct: ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"EmbySource: Browse '{parentId}' failed — {ex.Message}");
            return new BrowseResult();
        }

        var categories = new List<SourceCategory>();
        var items = new List<SourceItem>();

        foreach (var it in page.Items)
        {
            if (it.IsFolder)
                categories.Add(ToCategory(it, collectionType));
            else
                items.Add(ToItem(it));
        }

        return new BrowseResult { Categories = categories, Items = items };
    }

    /// <summary>
    /// Browses a music library by entity type, matching the web UI: library → artists → albums →
    /// tracks. Artist and album entities carry their own Primary artwork (unique artist images, album
    /// covers), unlike the raw folder tree which returns art-less <c>Folder</c> items.
    /// </summary>
    private async Task<BrowseResult> BrowseMusicAsync(EmbyState state, CancellationToken ct)
    {
        try
        {
            switch (state.MusicLevel)
            {
                case EmbyMusicLevel.Library:
                {
                    var artists = await _client!.GetArtistsAsync(state.ItemId, ct);
                    return new BrowseResult
                    {
                        Categories = artists.Select(a => ToMusicCategory(a, EmbyMusicLevel.Artist)).ToList(),
                    };
                }
                case EmbyMusicLevel.Artist:
                {
                    var albums = await _client!.GetAlbumsAsync(state.ItemId, ct);
                    // Albums often have no Primary image of their own — borrow a child track's cover
                    // (the web UI does the same). One query builds an albumId → image URL map.
                    var imageMap = await _client.GetAlbumImageMapAsync(state.ItemId, ct);
                    return new BrowseResult
                    {
                        Categories = albums
                            .Select(a => ToMusicCategory(
                                a, EmbyMusicLevel.Album,
                                imageMap.TryGetValue(a.Id, out var url) ? url : null))
                            .ToList(),
                    };
                }
                case EmbyMusicLevel.Album:
                {
                    var tracks = await _client!.GetAlbumTracksAsync(state.ItemId, ct);
                    return new BrowseResult { Items = tracks.Select(ToItem).ToList() };
                }
                default:
                    return new BrowseResult();
            }
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"EmbySource: music browse '{state.ItemId}' (level {state.MusicLevel}) failed — {ex.Message}");
            return new BrowseResult();
        }
    }

    // ── ITextSearchCapable ───────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null || string.IsNullOrWhiteSpace(query)) yield break;

        IReadOnlyList<EmbyItem> results;
        try
        {
            results = await _client.SearchAsync(query, ct: ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"EmbySource: Search '{query}' failed — {ex.Message}");
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

        var state = item.SourceState as EmbyState;
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
            _host?.Log(LogLevel.Warning, $"EmbySource: resolve auth failed — {ex.Message}");
            return null;
        }

        // Note: the stereo downmix is driven by the instance's Stereo audio setting inside the client;
        // prefs.PreferStereo is an additional host hint (both point the same direction on a cab).
        var url = _client.GetStreamUrl(itemId, audioOnly);
        _host?.Log(LogLevel.Debug, $"EmbySource: resolved {(audioOnly ? "audio" : "video")} stream → {url}");

        var layout = audioOnly ? StreamLayout.AudioOnly : StreamLayout.Muxed;
        return new ResolvedStream(StreamTransport.Http, layout, url);
    }

    public async Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        EnsureClient();
        var itemId = (item.SourceState as EmbyState)?.ItemId ?? item.ItemId;

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
                _host?.Log(LogLevel.Warning, $"EmbySource: GetChapters '{itemId}' failed — {ex.Message}");
            }
        }

        return new SourceMetadata(item.Duration, null, chapters);
    }

    // ── Mapping ──────────────────────────────────────────────────────────────────

    private SourceCategory ToCategory(EmbyItem it, string? collectionType = null) => new()
    {
        SourceInstanceId = InstanceId,
        CategoryId = it.Id,
        Title = it.Name,
        ThumbnailUrl = _client?.GetBestImageUrl(it),
        HasSubCategories = true,
        SourceState = new EmbyState(it.Id, IsAudioOnly: false, collectionType),
    };

    /// <summary>
    /// Maps a music entity (artist or album) into a drill-in tile carrying its own artwork and the
    /// next <see cref="EmbyMusicLevel"/> so the child browse queries the right entity type. Albums
    /// frequently have no image of their own (their cover is derived from track art), so callers may
    /// pass a <paramref name="fallbackImageUrl"/> borrowed from a child track.
    /// </summary>
    private SourceCategory ToMusicCategory(
        EmbyItem it, EmbyMusicLevel childLevel, string? fallbackImageUrl = null) => new()
    {
        SourceInstanceId = InstanceId,
        CategoryId = it.Id,
        Title = it.Name,
        ThumbnailUrl = _client?.GetBestImageUrl(it) ?? fallbackImageUrl,
        HasSubCategories = true,
        SourceState = new EmbyState(it.Id, IsAudioOnly: false, CollectionType: "music", childLevel),
    };

    private SourceItem ToItem(EmbyItem it)
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
            SourceState = new EmbyState(it.Id, audioOnly),
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

    private static bool IsMusic(string? collectionType) =>
        string.Equals(collectionType, "music", StringComparison.OrdinalIgnoreCase);

    // ── IConfigurable ────────────────────────────────────────────────────────────

    public IReadOnlyList<ConfigAction> GetConfigActions() =>
    [
        new(EmbySourceProvider.ActionBrowseLibraries, "Browse libraries…",
            "List the server's libraries and choose which become tiles."),
    ];

    public async Task<ConfigSelection> InvokeConfigActionAsync(string actionId, CancellationToken ct = default)
    {
        EnsureClient();
        if (actionId != EmbySourceProvider.ActionBrowseLibraries || _client is null)
            return new ConfigSelection([]);

        IReadOnlyList<EmbyItem> views;
        try
        {
            views = await _client.GetViewsAsync(ct);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"EmbySource: config GetViews failed — {ex.Message}");
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

        return new ConfigSelection(options, AllowMultiple: true, Title: "Emby libraries");
    }

    public async Task<IReadOnlyDictionary<string, string?>> ApplyConfigActionAsync(
        string actionId,
        IReadOnlyList<ConfigOptionResult> results,
        IReadOnlyDictionary<string, string?> currentSettings,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var result = new Dictionary<string, string?>(currentSettings);
        if (actionId != EmbySourceProvider.ActionBrowseLibraries)
            return result;

        var selected = results.Where(r => r.IsSelected).Select(r => r.OptionId).ToList();
        result[EmbySourceProvider.KeyLibraries] = JsonSerializer.Serialize(selected);
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
    // Emby resolves streams fresh by id, so favorites persist a small record. Containers (artist/album)
    // must keep the full EmbyState (CollectionType + MusicLevel) so entity-typed browse expands them
    // correctly; leaves need only the id + audio flag.

    private sealed class EmbyFavorite
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public string? ThumbnailUrl { get; set; }
        public double? DurationSeconds { get; set; }
        public bool IsAudioOnly { get; set; }
        public bool IsContainer { get; set; }
        public string? CollectionType { get; set; }
        public EmbyMusicLevel MusicLevel { get; set; } = EmbyMusicLevel.None;
    }

    private readonly object _favGate = new();
    private Dictionary<string, EmbyFavorite>? _favoritesCache;
    private Dictionary<string, EmbyFavorite> FavStore => _favoritesCache ??= LoadFavorites();

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
                    FavStore[itemId] = new EmbyFavorite { Id = itemId, Title = itemId };
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
        var node = item.ContainerState as EmbyState;
        lock (_favGate)
        {
            if (!FavStore.ContainsKey(item.ItemId)) return;
            FavStore[item.ItemId] = new EmbyFavorite
            {
                Id = item.ItemId,
                Title = item.Title,
                Subtitle = item.Subtitle,
                ThumbnailUrl = item.ThumbnailUrl,
                DurationSeconds = item.Duration?.TotalSeconds,
                IsAudioOnly = item.IsAudioOnly,
                IsContainer = item.IsContainer,
                CollectionType = node?.CollectionType,
                MusicLevel = node?.MusicLevel ?? EmbyMusicLevel.None,
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
        EmbyFavorite? f;
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
            SourceState = new EmbyState(f.Id, f.IsAudioOnly, f.CollectionType, f.MusicLevel),
        };
    }

    private Dictionary<string, EmbyFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, EmbyFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<EmbyFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"Emby: favorites read failed: {ex.Message}");
            return new Dictionary<string, EmbyFavorite>(StringComparer.Ordinal);
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
            _host?.Log(LogLevel.Warning, $"Emby: favorites write failed: {ex.Message}");
        }
    }
}
