using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.IHeartRadio;

/// <summary>
/// A configured iHeartRadio source instance. Browses live-station genres, searches stations, and
/// resolves a station to its raw, non-DRM HLS URL the host plays via LibVLC.
/// </summary>
/// <remarks>
/// Stations are <b>live audio streams</b>: every produced item is <see cref="SourceItem.IsAudioOnly"/>
/// and <see cref="SourceItem.IsLiveStream"/>, and the resolved stream is
/// <see cref="StreamLayout.AudioOnly"/> + <see cref="ResolvedStream.IsLiveStream"/> so the host
/// suppresses seek/duration and never auto-advances. Unlike SiriusXM there is no login and no local
/// proxy — the public catalog is key-less and the HLS is unencrypted.
/// </remarks>
public sealed class IHeartRadioSource :
    IPhosphorSource, IBrowsable, IPagedBrowsable, ITextSearchCapable, IPlayableResolver, IConnectionTestable, IFavoritable, IFavoriteCapture, IDisposable
{
    private readonly object _gate = new();
    private IPluginHost? _host;
    private IHeartClient? _client;
    private IReadOnlyList<IHeartGenre>? _genres;

    // Favorited items (stations, podcast episodes, or podcast shows) keyed by id, with enough display
    // data to rebuild a playable/browsable item without a re-fetch.
    private Dictionary<string, IHeartFavorite>? _favoritesCache;
    private Dictionary<string, IHeartFavorite> _favorites => _favoritesCache ??= LoadFavorites();

    // iHeart pages episodes with an opaque cursor, but IPagedBrowsable is offset-based. We bridge by
    // caching, per podcast, the next-cursor keyed by the offset it advances TO — so sequential paging
    // (offset = items already loaded) can look up the cursor for the requested window.
    private readonly Dictionary<string, Dictionary<int, string>> _episodeCursors = new(StringComparer.Ordinal);

    public IHeartRadioSource(string instanceId)
    {
        InstanceId = instanceId;
    }

    public string InstanceId { get; }
    public string TypeId => IHeartRadioSourceProvider.IHeartRadioTypeId;
    public string DisplayName { get; set; } = "iHeartRadio";

    // Key-less public catalog — always ready.
    public bool IsConfigured => true;
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        return Task.CompletedTask;
    }

    // No settings to apply (empty schema).
    public void ApplySettings(IReadOnlyDictionary<string, string?> values) { }

    private IHeartClient Client => _client ??= new IHeartClient(_host?.HttpClient ?? new HttpClient(), s => Log(LogLevel.Debug, s));

    // ── IConnectionTestable ─────────────────────────────────────────────────────

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var genres = await Client.GetGenresAsync(ct);
            return genres.Count > 0
                ? new ConnectionTestResult(true, $"Connected — {genres.Count} genres.", sw.Elapsed)
                : new ConnectionTestResult(false, "No genres returned from iHeartRadio.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    // ── IBrowsable (root → genres + All Stations → stations) ────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A single "iHeartRadio" root tile. Drilling in reveals genres + All Stations. STATIC — no
        // network call here (the host enumerates roots at startup; a fetch would block the splash).
        await Task.CompletedTask;
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = "root",
            Title = DisplayName,
            Icon = "📻",
            HasSubCategories = true,
            SourceState = new IHeartNode(IHeartNodeKind.Root),
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var node = category.SourceState as IHeartNode ?? InferNode(category.CategoryId);

        switch (node.Kind)
        {
            case IHeartNodeKind.Root:
            {
                var genres = await EnsureGenresAsync(ct);
                var tiles = new List<SourceCategory>();

                // ⭐ Favorites first, when the user has any.
                if (_favorites.Count > 0)
                {
                    tiles.Add(new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = "favorites",
                        Title = "Favorites",
                        Icon = "⭐",
                        HasSubCategories = true,
                        SourceState = new IHeartNode(IHeartNodeKind.AllStations, "favorites"),
                    });
                }

                tiles.Add(new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = "all",
                    Title = "Popular Stations",
                    Icon = "🔥",
                    HasSubCategories = true,
                    SourceState = new IHeartNode(IHeartNodeKind.AllStations),
                });

                // 🎙 On-demand podcasts — the finite/seekable, ad-light subtree.
                tiles.Add(new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = "podcasts",
                    Title = "Podcasts",
                    Icon = "🎙",
                    HasSubCategories = true,
                    SourceState = new IHeartNode(IHeartNodeKind.Podcasts),
                });

                foreach (var g in genres)
                {
                    tiles.Add(new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = $"genre:{g.Id}",
                        Title = g.Name,
                        Icon = "🎵",
                        HasSubCategories = true,
                        SourceState = new IHeartNode(IHeartNodeKind.Genre, g.Id.ToString()),
                    });
                }
                return new BrowseResult { Categories = tiles };
            }

            case IHeartNodeKind.AllStations when node.Key == "favorites":
            {
                List<IHeartFavorite> favs;
                lock (_gate) favs = _favorites.Values.ToList();
                // Podcast shows are drill-in containers (categories); stations + episodes are leaf items.
                var categories = favs
                    .Where(f => f.Kind == IHeartFavoriteKind.Podcast)
                    .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = $"pod:{f.Id}",
                        Title = f.Title,
                        ThumbnailUrl = f.ThumbnailUrl,
                        Icon = "🎙",
                        HasSubCategories = true,
                        SourceState = new IHeartNode(IHeartNodeKind.Podcast, f.Id),
                    })
                    .ToList();
                var items = favs
                    .Where(f => f.Kind != IHeartFavoriteKind.Podcast)
                    .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(FavoriteToSourceItem)
                    .ToList();
                return new BrowseResult { Categories = categories, Items = items };
            }

            case IHeartNodeKind.AllStations:
            {
                var stations = await Client.GetStationsAsync(genreId: null, limit: 100, ct);
                return new BrowseResult { Items = stations.Select(ToSourceItem).ToList() };
            }

            case IHeartNodeKind.Genre:
            {
                var genreId = int.TryParse(node.Key, out var gid) ? gid : (int?)null;
                var stations = await Client.GetStationsAsync(genreId, limit: 200, ct);
                var items = stations
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(ToSourceItem)
                    .ToList();
                return new BrowseResult { Items = items };
            }

            case IHeartNodeKind.Podcasts:
            {
                var categories = await Client.GetPodcastCategoriesAsync(ct);
                var tiles = categories
                    .Select(c => new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = $"pcat:{c.Id}",
                        Title = c.Name,
                        Icon = "🎧",
                        HasSubCategories = true,
                        SourceState = new IHeartNode(IHeartNodeKind.PodcastCategory, c.Id.ToString()),
                    })
                    .ToList();
                return new BrowseResult { Categories = tiles };
            }

            case IHeartNodeKind.PodcastCategory:
            {
                var categoryId = int.TryParse(node.Key, out var cid) ? cid : 0;
                var podcasts = await Client.GetPodcastsInCategoryAsync(categoryId, ct);
                var tiles = podcasts
                    .Select(p => new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = $"pod:{p.Id}",
                        Title = p.Title,
                        ThumbnailUrl = p.ImageUrl,
                        Icon = "🎙",
                        HasSubCategories = true,
                        SourceState = new IHeartNode(IHeartNodeKind.Podcast, p.Id),
                    })
                    .ToList();
                return new BrowseResult { Categories = tiles };
            }

            case IHeartNodeKind.Podcast:
            {
                // Return no items here so the host drives episodes entirely through the paged path
                // (IPagedBrowsable.BrowsePageAsync) — it only engages paging when BrowseAsync yields
                // zero items. Load-more then works via cursors.
                return new BrowseResult();
            }

            default:
                return new BrowseResult();
        }
    }

    // ── IPagedBrowsable (podcast episodes — "load more") ────────────────────────

    /// <summary>
    /// Lazily pages a podcast's episodes. Only podcast nodes page; other nodes return a single window.
    /// iHeart uses cursor paging, so we look up the cursor cached for this <paramref name="offset"/>
    /// (seeded as earlier pages load) — sequential "load more" (offset = items already shown) works;
    /// arbitrary random-access offsets aren't supported by the backend.
    /// </summary>
    public async Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default)
    {
        var node = category.SourceState as IHeartNode ?? InferNode(category.CategoryId);
        if (node.Kind != IHeartNodeKind.Podcast)
            return new BrowsePage();

        // offset 0 = first page (no cursor); otherwise use the cursor cached for this offset.
        string? cursor = null;
        if (offset > 0)
        {
            lock (_gate)
            {
                if (!_episodeCursors.TryGetValue(node.Key, out var byOffset)
                    || !byOffset.TryGetValue(offset, out cursor))
                {
                    // No cursor for this window (non-sequential request) — nothing more to serve.
                    return new BrowsePage { Items = [], TotalSize = offset };
                }
            }
        }

        var (episodes, next) = await Client.GetEpisodePageAsync(node.Key, count, cursor, ct);
        SeedEpisodeCursor(node.Key, offset + episodes.Count, next);

        // The podcast tile's Title is the show name — thread it through so video-capable episodes can
        // build a "{show} {episode}" YouTube search query for the optional video-upgrade button.
        var showTitle = category.Title;
        var items = episodes.Select(e => ToEpisodeItem(e, showTitle)).ToList();
        // Report a total that keeps "load more" alive while a next cursor exists, and closes it out
        // (offset + count == total) when the podcast is exhausted.
        var total = next is null ? offset + items.Count : offset + items.Count + 1;
        return new BrowsePage { Items = items, TotalSize = total };
    }

    // Cache the cursor that advances TO 'nextOffset' for this podcast, so BrowsePageAsync can find it.
    private void SeedEpisodeCursor(string podcastId, int nextOffset, string? cursor)
    {
        if (cursor is null) return;
        lock (_gate)
        {
            if (!_episodeCursors.TryGetValue(podcastId, out var byOffset))
                _episodeCursors[podcastId] = byOffset = new Dictionary<int, string>();
            byOffset[nextOffset] = cursor;
        }
    }

    private static IHeartNode InferNode(string categoryId) => categoryId switch
    {
        "root" => new IHeartNode(IHeartNodeKind.Root),
        "favorites" => new IHeartNode(IHeartNodeKind.AllStations, "favorites"),
        "all" => new IHeartNode(IHeartNodeKind.AllStations),
        "podcasts" => new IHeartNode(IHeartNodeKind.Podcasts),
        var s when s.StartsWith("genre:", StringComparison.Ordinal) => new IHeartNode(IHeartNodeKind.Genre, s["genre:".Length..]),
        var s when s.StartsWith("pcat:", StringComparison.Ordinal) => new IHeartNode(IHeartNodeKind.PodcastCategory, s["pcat:".Length..]),
        var s when s.StartsWith("pod:", StringComparison.Ordinal) => new IHeartNode(IHeartNodeKind.Podcast, s["pod:".Length..]),
        _ => new IHeartNode(IHeartNodeKind.Root),
    };

    private async Task<IReadOnlyList<IHeartGenre>> EnsureGenresAsync(CancellationToken ct)
    {
        lock (_gate) { if (_genres != null) return _genres; }
        var genres = await Client.GetGenresAsync(ct);
        lock (_gate) _genres = genres;
        return genres;
    }

    // ── ITextSearchCapable ──────────────────────────────────────────────────────

    /// <summary>
    /// Searches iHeart by free-text query (key-less): live stations first, then on-demand podcast
    /// shows (as drill-in containers whose episodes the user can then browse/play).
    /// </summary>
    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) yield break;

        var stations = await Client.SearchStationsAsync(q, limit: 50, ct);
        foreach (var s in stations)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToSourceItem(s);
        }

        var podcasts = await Client.SearchPodcastsAsync(q, limit: 20, ct);
        foreach (var p in podcasts)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToPodcastContainerItem(p);
        }
    }

    // A podcast show is a container: drilling in browses its episodes (via the Podcast node).
    private SourceItem ToPodcastContainerItem(IHeartPodcast p) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = p.Id,
        Title = p.Title,
        Subtitle = "iHeartRadio Podcast",
        ThumbnailUrl = p.ImageUrl,
        IsContainer = true,
        SourceState = new IHeartNode(IHeartNodeKind.Podcast, p.Id),
    };

    private SourceItem ToSourceItem(IHeartStation s) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = s.Id,
        Title = s.Name,
        Subtitle = string.IsNullOrWhiteSpace(s.Description) ? "iHeartRadio" : s.Description,
        ThumbnailUrl = s.LogoUrl,
        IsAudioOnly = true,
        IsLiveStream = true,
        // Carry the station so ResolveAsync can use the inline HLS URL without a re-fetch.
        SourceState = s,
    };

    // Podcast episodes are FINITE, seekable tracks (not live) — carry a Duration and no IsLiveStream.
    private SourceItem ToEpisodeItem(IHeartEpisode e, string? showTitle = null) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = e.Id,
        Title = e.Title,
        Subtitle = "iHeartRadio Podcast",
        ThumbnailUrl = e.ImageUrl,
        IsAudioOnly = true,
        Duration = e.Duration,
        // Video podcasts serve audio inline but a video version usually lives on YouTube — flag the
        // episode and supply a best-effort search query so the host can offer an optional video upgrade.
        HasVideoAlternative = e.HasVideo,
        VideoSearchQuery = e.HasVideo ? BuildVideoQuery(showTitle, e.Title) : null,
        // Carry the episode so ResolveAsync can reuse an inline media URL when present.
        SourceState = e,
    };

    // Build the YouTube search query for a video-podcast episode: quote the show name (forces an exact
    // phrase) followed by the episode title, e.g. "Joy 101 with Hoda Kotb" Episode 12. Falls back to
    // the episode title alone when the show name is unknown.
    private static string BuildVideoQuery(string? showTitle, string episodeTitle) =>
        string.IsNullOrWhiteSpace(showTitle) ? episodeTitle : $"\"{showTitle}\" {episodeTitle}";

    // ── IFavoritable / IFavoriteCapture ─────────────────────────────────────────

    // Podcast browse tiles are categories whose CategoryId carries a "pod:" prefix, so the host
    // favorites them by that prefixed id — while the SAME podcast favorited from search (a container
    // leaf) uses the raw id. Strip the prefix everywhere so both map to one stored raw podcast id
    // (also avoids a "pod:pod:" double-prefix when rebuilding the Favorites view).
    private static string NormalizeId(string itemId) =>
        itemId.StartsWith("pod:", StringComparison.Ordinal) ? itemId["pod:".Length..] : itemId;

    public bool IsFavorite(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return false;
        var id = NormalizeId(itemId);
        lock (_gate) return _favorites.ContainsKey(id);
    }

    public void SetFavorite(string itemId, bool favorite)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        var id = NormalizeId(itemId);
        lock (_gate)
        {
            bool changed;
            if (favorite)
            {
                // Seed a minimal record; RememberFavorite (called right after by the host) enriches it
                // with the real kind/title/url. Assume Station only as a last-resort fallback.
                changed = _favorites.TryAdd(id,
                    new IHeartFavorite(id, IHeartFavoriteKind.Station, id, null, null, null, null));
            }
            else
            {
                changed = _favorites.Remove(id);
            }
            if (changed) SaveFavorites();
        }
    }

    /// <summary>
    /// Host hands us a snapshot at star-time so we persist the correct kind (station vs episode vs
    /// podcast show) and display data — the fix that makes non-station favorites actually replay.
    /// </summary>
    public void RememberFavorite(FavoriteCapture item)
    {
        if (string.IsNullOrEmpty(item.ItemId)) return;
        var id = NormalizeId(item.ItemId);
        lock (_gate)
        {
            if (!_favorites.ContainsKey(id)) return;

            // Classify: containers are podcast shows; a Duration-bearing leaf is an episode; else a
            // live station. Preserve any stream URL we already resolved for a station.
            var kind = item.IsContainer ? IHeartFavoriteKind.Podcast
                : item.Duration is not null ? IHeartFavoriteKind.Episode
                : IHeartFavoriteKind.Station;
            var existingUrl = _favorites.TryGetValue(id, out var prev) ? prev.StreamUrl : null;

            _favorites[id] = new IHeartFavorite(
                id,
                kind,
                string.IsNullOrWhiteSpace(item.Title) ? id : item.Title,
                item.Subtitle,
                item.ThumbnailUrl,
                item.Duration?.TotalSeconds,
                kind == IHeartFavoriteKind.Station ? existingUrl : null);
            SaveFavorites();
        }
    }

    public IReadOnlyCollection<string> GetFavoriteIds()
    {
        lock (_gate) return _favorites.Keys.ToArray();
    }

    /// <summary>Rebuilds a playable/browsable item from a favorite record, by kind.</summary>
    public SourceItem? GetFavorite(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        var id = NormalizeId(itemId);
        IHeartFavorite? f;
        lock (_gate) f = _favorites.TryGetValue(id, out var rec) ? rec : null;
        return f is null ? null : FavoriteToSourceItem(f);
    }

    // Rebuild a SourceItem from a persisted favorite record, honoring its kind.
    private SourceItem FavoriteToSourceItem(IHeartFavorite f)
    {
        var duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        return f.Kind switch
        {
            IHeartFavoriteKind.Podcast => new SourceItem
            {
                SourceInstanceId = InstanceId,
                ItemId = f.Id,
                Title = f.Title,
                Subtitle = f.Subtitle ?? "iHeartRadio Podcast",
                ThumbnailUrl = f.ThumbnailUrl,
                IsContainer = true,
                SourceState = new IHeartNode(IHeartNodeKind.Podcast, f.Id),
            },
            IHeartFavoriteKind.Episode => new SourceItem
            {
                SourceInstanceId = InstanceId,
                ItemId = f.Id,
                Title = f.Title,
                Subtitle = f.Subtitle ?? "iHeartRadio Podcast",
                ThumbnailUrl = f.ThumbnailUrl,
                IsAudioOnly = true,
                Duration = duration,
                // No inline media URL — ResolveAsync fetches it by episode id at play time.
                SourceState = new IHeartEpisode(f.Id, f.Title, null, f.ThumbnailUrl, duration, null),
            },
            _ => new SourceItem
            {
                SourceInstanceId = InstanceId,
                ItemId = f.Id,
                Title = f.Title,
                Subtitle = f.Subtitle ?? "iHeartRadio",
                ThumbnailUrl = f.ThumbnailUrl,
                IsAudioOnly = true,
                IsLiveStream = true,
                SourceState = new IHeartStation(f.Id, f.Title, f.Subtitle, f.ThumbnailUrl, f.StreamUrl),
            },
        };
    }

    // Remember a station's resolved stream URL when we see it (so a favorite plays without a re-fetch).
    private void RememberStation(IHeartStation s)
    {
        lock (_gate)
        {
            if (_favorites.TryGetValue(s.Id, out var f) && f.Kind == IHeartFavoriteKind.Station)
            {
                _favorites[s.Id] = f with { StreamUrl = s.StreamUrl };
                SaveFavorites();
            }
        }
    }

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private Dictionary<string, IHeartFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, IHeartFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<IHeartFavorite>>(File.ReadAllText(path)) ?? [];
            return list.Where(f => !string.IsNullOrEmpty(f.Id))
                       .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex) { Log(LogLevel.Warning, $"iHeart: favorites read failed: {ex.Message}"); return new Dictionary<string, IHeartFavorite>(StringComparer.Ordinal); }
    }

    private void SaveFavorites()
    {
        try
        {
            var path = FavoritesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_favorites.Values.ToList()));
        }
        catch (Exception ex) { Log(LogLevel.Warning, $"iHeart: favorites write failed: {ex.Message}"); }
    }

    // ── IPlayableResolver ───────────────────────────────────────────────────────

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        // Podcast episode: finite, seekable MP3 — resolve the direct mediaUrl (NOT a live stream).
        if (item.SourceState is IHeartEpisode episode)
        {
            var mediaUrl = episode.MediaUrl;
            if (string.IsNullOrWhiteSpace(mediaUrl))
                mediaUrl = await Client.GetEpisodeMediaUrlAsync(item.ItemId, ct);

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                Log(LogLevel.Warning, $"iHeart: no media URL for episode '{item.ItemId}'.");
                return null;
            }

            return new ResolvedStream(StreamTransport.Http, StreamLayout.AudioOnly, mediaUrl!);
        }

        var station = item.SourceState as IHeartStation;
        var streamUrl = station?.StreamUrl;

        // Search results (and favorites) may not carry an inline stream URL — resolve it now.
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            streamUrl = await Client.GetStreamUrlAsync(item.ItemId, ct);
            if (station != null && !string.IsNullOrWhiteSpace(streamUrl))
                RememberStation(station with { StreamUrl = streamUrl });
        }

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            Log(LogLevel.Warning, $"iHeart: no stream URL for station '{item.ItemId}'.");
            return null;
        }

        return new ResolvedStream(
            StreamTransport.Http,
            StreamLayout.AudioOnly,
            streamUrl!)
        {
            IsLiveStream = true,
        };
    }

    public Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
        // Live radio has no fixed duration/chapters — nothing to enrich.
        => Task.FromResult<SourceMetadata?>(null);

    // ── Internals ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        // Nothing owned to release (the HttpClient belongs to the host).
    }

    private void Log(LogLevel level, string message) => _host?.Log(level, message);
}
