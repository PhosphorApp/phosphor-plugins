using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Plex;

/// <summary>
/// In-box Plex source. Wraps the existing <see cref="PlexService"/> REST client and presents
/// its search + drill-down + playback surface through the plug-in contract. Implements
/// <see cref="IBrowsable"/> (the hierarchical shape that stress-tests
/// <see cref="SourceCategory"/>/<see cref="BrowseResult"/>) and <see cref="IConfigurable"/>
/// (the "browse libraries" setup action). Multiple instances (two Plex servers) are supported
/// via the provider.
/// </summary>
/// <remarks>
/// In-box, so it uses <see cref="PlexService"/>, <see cref="VideoItem"/>, and the Plex enums
/// directly. Pure data producer: no UI, no thread assumptions.
/// </remarks>
public sealed class PlexSource : IPhosphorSource, ITextSearchCapable, IFilterableSearch, IBrowsable, IPagedBrowsable, IScopedSearchable, IPlayableResolver, IConfigurable, IGaplessCapable, IConnectionTestable, IFavoritable, IFavoriteCapture
{
    private readonly PlexService _plex = new();
    private IPluginHost? _host;

    private string _serverUrl = "";
    private string _token = "";
    private bool _stereoAudio;
    private List<PlexLibraryMapping> _libraries = [];

    public PlexSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => PlexSourceProvider.PlexTypeId;
    public string DisplayName { get; set; } = "Plex";

    public bool IsConfigured => _plex.IsConfigured;
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _serverUrl = Get(values, PlexSourceProvider.KeyServerUrl) ?? "";
        _token = Get(values, PlexSourceProvider.KeyToken) ?? "";
        // Default to stereo when unset/invalid — safest for cabs (surround channels drive
        // mechanical/ball exciters, not music). Matches the Emby/Jellyfin sources.
        _stereoAudio = !bool.TryParse(Get(values, PlexSourceProvider.KeyStereoAudio), out var s) || s;
        _libraries = ParseLibraries(Get(values, PlexSourceProvider.KeyLibraries));

        _plex.Configure(_serverUrl, _token, _stereoAudio);
        _host?.Log($"PlexSource: server={_serverUrl} stereo={_stereoAudio} libraries={_libraries.Count}");
    }

    // ── IConnectionTestable ────────────────────────────────────────────────────

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!_plex.IsConfigured)
            return new ConnectionTestResult(false, "Server URL and token are required.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // A successful library fetch confirms both reachability and a valid token, and gives a
            // friendly count for the result line.
            var libs = await _plex.GetLibrariesAsync();
            sw.Stop();
            return new ConnectionTestResult(
                true,
                $"Connected — {libs.Count} librar{(libs.Count == 1 ? "y" : "ies")} found.",
                sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionTestResult(false, $"Connection failed: {ex.Message}", sw.Elapsed);
        }
    }

    // ── ITextSearchCapable ─────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var results = await _plex.SearchAsync(query);
        foreach (var v in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return PlexMappings.ToSourceItem(v, InstanceId);
        }
    }

    // ── IFilterableSearch ──────────────────────────────────────────────────────

    /// <summary>
    /// Server-side filtered search. Pushes duration bounds (and an optional <c>library:</c> scope)
    /// down to Plex's section-scoped endpoint so large servers filter server-side instead of the
    /// host scanning results. When a library name is given it resolves to a single configured
    /// section; otherwise it fans out across every configured library and merges. Reports the
    /// duration + library filters as applied so the host doesn't re-filter client-side.
    /// </summary>
    public FilteredSearchResult SearchFiltered(string query, SearchFilters filters, CancellationToken ct = default)
    {
        // Resolve the target section(s): a named library (case-insensitive exact, then contains),
        // or all configured libraries when no name was given.
        var targets = _libraries.AsEnumerable();
        var appliedLibrary = filters.Library;
        if (!string.IsNullOrWhiteSpace(filters.Library))
        {
            var name = filters.Library.Trim();
            var match = _libraries.FirstOrDefault(l =>
                            string.Equals(l.Title, name, StringComparison.OrdinalIgnoreCase))
                        ?? _libraries.FirstOrDefault(l =>
                            l.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                targets = new[] { match };
                appliedLibrary = match.Title;
            }
            else
            {
                // Unknown library name — nothing scoped, so don't claim the filter was applied.
                targets = _libraries;
                appliedLibrary = null;
            }
        }

        // Duration bounds are always applied server-side.
        var applied = filters with { Library = appliedLibrary };
        return new FilteredSearchResult(SearchFilteredCore(query, filters, targets.ToList(), ct), applied);
    }

    private async IAsyncEnumerable<SourceItem> SearchFilteredCore(
        string query,
        SearchFilters filters,
        List<PlexLibraryMapping> targets,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var lib in targets)
        {
            ct.ThrowIfCancellationRequested();

            List<VideoItem> results;
            try
            {
                results = await _plex.SearchLibraryWithFiltersAsync(
                    lib.Key, query, lib.Type, filters.MinDuration, filters.MaxDuration, ct);
            }
            catch (Exception ex)
            {
                _host?.Log($"PlexSource: filtered search failed for '{lib.Title}': {ex.Message}");
                continue;
            }

            foreach (var v in results)
            {
                ct.ThrowIfCancellationRequested();
                yield return PlexMappings.ToSourceItem(v, InstanceId);
            }
        }
    }

    // ── IBrowsable ─────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var lib in _libraries)
            yield return PlexMappings.ToRootCategory(lib, InstanceId, DisplayName);
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        if (category.SourceState is not PlexNode node)
            return new BrowseResult();

        return node.Kind switch
        {
            PlexNodeKind.Library => await BrowseLibraryAsync(node, ct),
            PlexNodeKind.Artist => await BrowseChildrenAsync(node, PlexItemType.Album, PlexNodeKind.Album, ct),
            PlexNodeKind.Album => await BrowseTracksAsync(node, ct),
            PlexNodeKind.HubList => await BrowseHubListAsync(node, ct),
            PlexNodeKind.Hub => await BrowseHubAsync(node, ct),
            PlexNodeKind.PlaylistList => await BrowsePlaylistListAsync(node, ct),
            PlexNodeKind.Playlist => await BrowsePlaylistAsync(node, ct),
            _ => new BrowseResult(),
        };
    }

    private async Task<BrowseResult> BrowseLibraryAsync(PlexNode node, CancellationToken ct)
    {
        await Task.CompletedTask;
        // A library expands to its "Hubs" and "Playlists" grouping nodes only. Its actual children
        // (artists for music, videos otherwise) are served through the paged path (BrowsePageAsync)
        // so large libraries lazy-load and aren't rendered twice (the host runs both BrowseAsync and,
        // because the library is IPagedBrowsable, the paged path). Container children (artists/albums)
        // come back as leaf SourceItems flagged IsContainer, which the host drills into.
        var categories = new List<SourceCategory>
        {
            new()
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"hublist:{node.Key}",
                Title = "Hubs",
                // No own icon → inherits the parent library's icon (music note / clapperboard).
                HasSubCategories = true,
                SourceState = new PlexNode(PlexNodeKind.HubList, node.Key, node.LibraryType),
            },
            new()
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"playlistlist:{node.Key}",
                Title = "Playlists",
                // No own icon → inherits the parent library's icon.
                HasSubCategories = true,
                SourceState = new PlexNode(PlexNodeKind.PlaylistList, node.Key, node.LibraryType),
            },
        };

        return new BrowseResult { Categories = categories };
    }

    private async Task<BrowseResult> BrowseChildrenAsync(
        PlexNode node, PlexItemType childType, PlexNodeKind childKind, CancellationToken ct)
    {
        var children = await _plex.GetChildrenAsync(node.Key, childType, ct);
        var categories = children
            .Select(v => PlexMappings.ToCategory(v, InstanceId,
                new PlexNode(childKind, v.PlexRatingKey ?? "", node.LibraryType)))
            .ToList();
        return new BrowseResult { Categories = categories };
    }

    private async Task<BrowseResult> BrowseTracksAsync(PlexNode node, CancellationToken ct)
    {
        var tracks = await _plex.GetChildrenAsync(node.Key, PlexItemType.Track, ct);
        return new BrowseResult { Items = tracks.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowseHubListAsync(PlexNode node, CancellationToken ct)
    {
        var hubs = await _plex.GetLibraryHubsAsync(node.Key, ct);
        return new BrowseResult { Categories = hubs.Select(h => PlexMappings.ToCategory(h, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowseHubAsync(PlexNode node, CancellationToken ct)
    {
        var items = await _plex.GetHubItemsAsync(node.Key, node.LibraryType ?? "", ct);
        return new BrowseResult { Items = items.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowsePlaylistListAsync(PlexNode node, CancellationToken ct)
    {
        var playlistType = node.LibraryType == "artist" ? "audio" : "video";
        var playlists = await _plex.GetPlaylistsAsync(playlistType, ct);
        return new BrowseResult { Categories = playlists.Select(p => PlexMappings.ToCategory(p, InstanceId)).ToList() };
    }

    private async Task<BrowseResult> BrowsePlaylistAsync(PlexNode node, CancellationToken ct)
    {
        var items = await _plex.GetPlaylistItemsAsync(node.Key, ct);
        return new BrowseResult { Items = items.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList() };
    }

    // ── IPagedBrowsable ────────────────────────────────────────────────────────

    public async Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default)
    {
        if (category.SourceState is not PlexNode node)
            return new BrowsePage();

        // Route to the paginated Plex endpoint matching the node kind. Hubs, libraries, and
        // playlists all page by offset/count and report a total size.
        PlexPage page = node.Kind switch
        {
            PlexNodeKind.Hub => await _plex.GetHubItemsPageAsync(node.Key, node.LibraryType ?? "", offset, count, ct),
            PlexNodeKind.Library => await _plex.GetLibraryVideosPageAsync(node.Key, offset, count, node.LibraryType, ct),
            PlexNodeKind.Playlist => await _plex.GetPlaylistItemsPageAsync(node.Key, offset, count, ct),
            _ => new PlexPage(),
        };

        return new BrowsePage
        {
            Items = page.Items.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList(),
            TotalSize = page.TotalSize,
        };
    }

    // ── IScopedSearchable ──────────────────────────────────────────────────────

    public async Task<BrowseResult> SearchInCategoryAsync(
        SourceCategory node, string query, CancellationToken ct = default)
    {
        // Only library-scoped search is supported; other nodes return nothing.
        if (node.SourceState is not PlexNode plexNode || plexNode.Kind != PlexNodeKind.Library)
            return new BrowseResult();

        if (plexNode.LibraryType == "artist")
        {
            // Music: fan out across artist (8), album (9), and track (10) and merge. Matching
            // artists/albums become drill-in containers; matching tracks are playable leaves.
            var artists = await _plex.SearchLibraryAsync(
                plexNode.Key, query, plexNode.LibraryType, PlexSearchMode.Artist, ct);
            var albums = await _plex.SearchLibraryAsync(
                plexNode.Key, query, plexNode.LibraryType, PlexSearchMode.Album, ct);
            var tracks = await _plex.SearchLibraryAsync(
                plexNode.Key, query, plexNode.LibraryType, PlexSearchMode.Track, ct);

            var categories = new List<SourceCategory>();
            foreach (var a in artists)
                categories.Add(PlexMappings.ToCategory(a, InstanceId,
                    new PlexNode(PlexNodeKind.Artist, a.PlexRatingKey ?? "", plexNode.LibraryType)));
            foreach (var al in albums)
                categories.Add(PlexMappings.ToCategory(al, InstanceId,
                    new PlexNode(PlexNodeKind.Album, al.PlexRatingKey ?? "", plexNode.LibraryType)));

            return new BrowseResult
            {
                Categories = categories,
                Items = tracks.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList(),
            };
        }

        // Video: plain section-scoped title search — all leaves.
        var videos = await _plex.SearchLibraryAsync(plexNode.Key, query, plexNode.LibraryType, null, ct);
        return new BrowseResult
        {
            Items = videos.Select(v => PlexMappings.ToSourceItem(v, InstanceId)).ToList(),
        };
    }

    // ── IPlayableResolver ──────────────────────────────────────────────────────

    public Task<ResolvedStream?> ResolveAsync(SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        // Plex items already carry a ready-to-play StreamUrl (built at browse time).
        var v = PlexMappings.VideoItemOf(item);
        return Task.FromResult(v == null ? null : PlexMappings.ToResolvedStream(v));
    }

    public async Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        var v = PlexMappings.VideoItemOf(item);
        if (v == null) return null;

        // Fetch chapters on demand when the item didn't already carry them.
        if ((v.Chapters == null || v.Chapters.Count == 0) && !string.IsNullOrEmpty(v.PlexRatingKey))
        {
            var chapters = await _plex.GetChaptersAsync(v.PlexRatingKey);
            if (chapters != null) v.Chapters = chapters;
        }

        return PlexMappings.ToSourceMetadata(v);
    }

    // ── IGaplessCapable ────────────────────────────────────────────────────────

    /// <summary>
    /// Plex audio tracks carry a stable, direct audio stream URL built at browse time, which can be
    /// pre-loaded on the idle decoder for gapless transitions. The host passes the pre-built URL via
    /// <see cref="SourceItem.SourceState"/> (a string) plus the audio-only flag; returns it for
    /// audio-only items, null otherwise. Kept source-agnostic across the plug-in boundary — the host
    /// never hands back a plug-in type.
    /// </summary>
    public string? GetGaplessStreamUrl(SourceItem item)
    {
        if (!item.IsAudioOnly) return null;
        return item.SourceState as string is { Length: > 0 } url ? url : null;
    }

    // ── IConfigurable ──────────────────────────────────────────────────────────

    public IReadOnlyList<ConfigAction> GetConfigActions() =>
    [
        new(PlexSourceProvider.ActionBrowseLibraries, "Browse libraries…",
            "List the server's libraries and choose which become tiles."),
    ];

    public async Task<ConfigSelection> InvokeConfigActionAsync(string actionId, CancellationToken ct = default)
    {
        if (actionId != PlexSourceProvider.ActionBrowseLibraries)
            return new ConfigSelection([]);

        var enabled = _libraries.ToDictionary(l => l.Key, l => l);
        var libs = await _plex.GetLibrariesAsync();
        var options = libs
            .Select(l =>
            {
                enabled.TryGetValue(l.Key, out var prev);
                return new ConfigOption(l.Key, $"{l.Title} ({l.Type})", prev != null,
                    new[]
                    {
                        new ConfigSubOption("hubs", "Hubs", prev?.HubsEnabled ?? false),
                        new ConfigSubOption("playlists", "Playlists", prev?.PlaylistsEnabled ?? false),
                    });
            })
            .ToList();

        return new ConfigSelection(options, AllowMultiple: true, Title: "Plex libraries");
    }

    public async Task<IReadOnlyDictionary<string, string?>> ApplyConfigActionAsync(
        string actionId,
        IReadOnlyList<ConfigOptionResult> results,
        IReadOnlyDictionary<string, string?> currentSettings,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string?>(currentSettings);
        if (actionId != PlexSourceProvider.ActionBrowseLibraries)
            return result;

        // Turn selected libraries + their sub-flags into the rich mapping, taking Title/Type from
        // the server and Hubs/Playlists from the user's per-library sub-option choices.
        var libs = (await _plex.GetLibrariesAsync()).ToDictionary(l => l.Key, l => l);
        var mapped = new List<PlexLibraryMapping>();
        foreach (var r in results)
        {
            if (!r.IsSelected || !libs.TryGetValue(r.OptionId, out var lib)) continue;
            mapped.Add(new PlexLibraryMapping
            {
                Key = lib.Key,
                Title = lib.Title,
                Type = lib.Type,
                HubsEnabled = r.SelectedSubOptionIds.Contains("hubs"),
                PlaylistsEnabled = r.SelectedSubOptionIds.Contains("playlists"),
            });
        }

        result[PlexSourceProvider.KeyLibraries] = JsonSerializer.Serialize(mapped);
        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    private static List<PlexLibraryMapping> ParseLibraries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PlexLibraryMapping>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // ── IFavoritable ───────────────────────────────────────────────────────────
    // Plex favorites persist a serializable record per rating key. LEAF favorites keep the full,
    // token-bound StreamUrl so GetFavorite replays without a server round-trip. CONTAINER favorites
    // (artist/album) keep the browse-node identity (kind + key + libraryType) so GetFavorite returns
    // a container SourceItem the host expands to tracks on play. Feeds the aggregated Favorites tile.

    /// <summary>A persisted Plex favorite — either a playable leaf or a container (artist/album).</summary>
    private sealed class PlexFavorite
    {
        public string Id { get; set; } = "";        // rating-key based id (VideoId)
        public string Title { get; set; } = "";
        public string? Author { get; set; }
        public string? ThumbnailUrl { get; set; }
        public bool IsContainer { get; set; }
        public VideoItem? Leaf { get; set; }         // leaf: full playable item (has StreamUrl)
        public string? NodeKind { get; set; }        // container: PlexNodeKind name
        public string? NodeKey { get; set; }
        public string? NodeLibraryType { get; set; }
    }

    private readonly object _favGate = new();
    private Dictionary<string, PlexFavorite>? _favoritesCache;
    private Dictionary<string, PlexFavorite> Favorites => _favoritesCache ??= LoadFavorites();

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
                // Placeholder; the rich record is captured via RememberFavorite (host holds the item).
                if (!Favorites.ContainsKey(itemId))
                    Favorites[itemId] = new PlexFavorite { Id = itemId, Title = itemId };
            }
            else
            {
                changed = Favorites.Remove(itemId);
            }
            if (changed) SaveFavorites();
        }
    }

    /// <summary>
    /// Captures the full favorite (leaf item or container node) so <see cref="GetFavorite"/> can
    /// rebuild it. <see cref="FavoriteCapture.ContainerState"/> carries the opaque browse state:
    /// a <see cref="PlexNode"/> for containers, or the leaf's <see cref="VideoItem"/> for leaves.
    /// </summary>
    public void RememberFavorite(FavoriteCapture item)
    {
        if (string.IsNullOrEmpty(item.ItemId)) return;
        lock (_favGate)
        {
            if (!Favorites.ContainsKey(item.ItemId)) return; // only enrich known favorites

            PlexFavorite rec;
            if (item.IsContainer)
            {
                var node = item.ContainerState as PlexNode;
                rec = new PlexFavorite
                {
                    Id = item.ItemId,
                    Title = item.Title,
                    Author = item.Subtitle,
                    ThumbnailUrl = item.ThumbnailUrl,
                    IsContainer = true,
                    NodeKind = node?.Kind.ToString(),
                    NodeKey = node?.Key ?? item.ItemId,
                    NodeLibraryType = node?.LibraryType,
                };
            }
            else
            {
                // Leaves carry their playable VideoItem (token-bound StreamUrl) via ContainerState,
                // so a favorited track replays without a server round-trip.
                rec = new PlexFavorite
                {
                    Id = item.ItemId,
                    Title = item.Title,
                    Author = item.Subtitle,
                    ThumbnailUrl = item.ThumbnailUrl,
                    IsContainer = false,
                    Leaf = item.ContainerState as VideoItem,
                };
            }
            Favorites[item.ItemId] = rec;
            SaveFavorites();
        }
    }

    public IReadOnlyCollection<string> GetFavoriteIds()
    {
        lock (_favGate) return Favorites.Keys.ToArray();
    }

    /// <summary>
    /// Rebuilds a favorite: a leaf → its stored playable item; a container → a container SourceItem
    /// (IsContainer=true, SourceState=PlexNode) the host expands to tracks on play.
    /// </summary>
    public SourceItem? GetFavorite(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        PlexFavorite? f;
        lock (_favGate) f = Favorites.TryGetValue(itemId, out var stored) ? stored : null;
        if (f is null) return null;

        if (f.IsContainer)
        {
            var kind = Enum.TryParse<PlexNodeKind>(f.NodeKind, out var k) ? k : PlexNodeKind.Album;
            return new SourceItem
            {
                SourceInstanceId = InstanceId,
                ItemId = f.Id,
                Title = f.Title,
                Subtitle = f.Author,
                ThumbnailUrl = f.ThumbnailUrl,
                IsContainer = true,
                SourceState = new PlexNode(kind, f.NodeKey ?? f.Id, f.NodeLibraryType ?? "artist"),
            };
        }

        return f.Leaf is null ? null : PlexMappings.ToSourceItem(f.Leaf, InstanceId);
    }

    private Dictionary<string, PlexFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, PlexFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<PlexFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(v => !string.IsNullOrEmpty(v.Id))
                .ToDictionary(v => v.Id, v => v, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log($"Plex: favorites read failed: {ex.Message}");
            return new Dictionary<string, PlexFavorite>(StringComparer.Ordinal);
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
            _host?.Log($"Plex: favorites write failed: {ex.Message}");
        }
    }
}
