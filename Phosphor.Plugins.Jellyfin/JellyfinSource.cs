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
    IFavoritable, IFavoriteCapture
{
    private JellyfinClient? _client;
    private IPluginHost? _host;

    private string _serverUrl = "";
    private string _username = "";
    private string _password = "";
    private bool _stereoAudio;
    private List<string> _selectedLibraryIds = [];

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

        // Force a fresh client with the new config on the next use.
        _client?.Configure(_serverUrl, _username, _password, _stereoAudio);
    }

    private void EnsureClient()
    {
        // A config-time transient source (library chooser / connection test) may never get
        // InitializeAsync, so _host can be null. Fall back to a shared HttpClient + no-op log so the
        // client still works for those one-off calls; the real host client is used once initialized.
        var http = _host?.HttpClient ?? SharedHttpClient;
        var log = _host is { } h ? h.Log : (Action<string>?)null;
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

        IReadOnlyList<JellyfinItem> views;
        try
        {
            views = await _client.GetViewsAsync(ct);
        }
        catch (Exception ex)
        {
            _host?.Log($"JellyfinSource: GetViews failed — {ex.Message}");
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

        var parentId = (category.SourceState as JellyfinState)?.ItemId ?? category.CategoryId;

        JellyfinPage page;
        try
        {
            page = await _client.GetItemsAsync(parentId, ct: ct);
        }
        catch (Exception ex)
        {
            _host?.Log($"JellyfinSource: Browse '{parentId}' failed — {ex.Message}");
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
            _host?.Log($"JellyfinSource: Search '{query}' failed — {ex.Message}");
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
            _host?.Log($"JellyfinSource: resolve auth failed — {ex.Message}");
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
                _host?.Log($"JellyfinSource: GetChapters '{itemId}' failed — {ex.Message}");
            }
        }

        return new SourceMetadata(item.Duration, null, chapters);
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
            _host?.Log($"JellyfinSource: config GetViews failed — {ex.Message}");
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
            _host?.Log($"Jellyfin: favorites read failed: {ex.Message}");
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
            _host?.Log($"Jellyfin: favorites write failed: {ex.Message}");
        }
    }
}
