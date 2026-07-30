using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.PodcastIndex;

/// <summary>
/// A configured Podcast Index source instance. Browses trending shows and categories, searches feeds,
/// and resolves an episode to its direct <c>enclosureUrl</c> the host plays via LibVLC.
/// </summary>
/// <remarks>
/// Podcast Index is a pure <b>index</b>: episode responses carry the direct, non-DRM
/// <c>enclosureUrl</c> inline, so <see cref="ResolveAsync"/> just hands that URL to the host — no
/// scraping, no external tools. Episodes are <b>finite, seekable</b> tracks (real
/// <see cref="SourceItem.Duration"/>), so items are NOT marked <see cref="SourceItem.IsLiveStream"/>.
/// Audio-first; a video-enclosure episode plays as video (its <c>IsAudioOnly</c> is left unset).
/// Requires a per-user, free API key + secret, so every request is SHA-1 signed by the client.
/// </remarks>
public sealed class PodcastIndexSource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IScopedSearchable, IContainerPlayPolicy, IPlayableResolver, IConnectionTestable, IFavoritable, IFavoriteCapture, IDisposable
{
    private readonly object _gate = new();
    private IPluginHost? _host;
    private string _apiKey = "";
    private string _apiSecret = "";

    private PodcastIndexClient? _client;
    private IReadOnlyList<PiCategory>? _categories;

    // Favorited shows/episodes keyed by SourceItem.ItemId ("feed:{id}" for shows, the numeric
    // episode id for episodes — the two id-spaces never collide), each carrying enough display data
    // to rebuild a playable/browsable item without a re-fetch.
    private Dictionary<string, PiFavorite>? _favoritesCache;
    private Dictionary<string, PiFavorite> _favorites => _favoritesCache ??= LoadFavorites();

    public PodcastIndexSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => PodcastIndexSourceProvider.PodcastIndexTypeId;
    public string DisplayName { get; set; } = "Podcast Index";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret);
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _apiKey = Get(values, PodcastIndexSourceProvider.KeyApiKey) ?? "";
        _apiSecret = Get(values, PodcastIndexSourceProvider.KeyApiSecret) ?? "";

        // Credentials may have changed — drop any cached client/taxonomy so the next call rebuilds.
        lock (_gate)
        {
            _client = null;
            _categories = null;
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var v) ? v : null;

    private PodcastIndexClient Client
    {
        get
        {
            lock (_gate)
            {
                return _client ??= new PodcastIndexClient(
                    _host?.HttpClient ?? new HttpClient(),
                    _apiKey, _apiSecret,
                    s => Log(LogLevel.Debug, s));
            }
        }
    }

    // ── IConnectionTestable ─────────────────────────────────────────────────────

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ConnectionTestResult(false, "Enter your Podcast Index API key and secret first.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var categories = await Client.GetCategoriesAsync(ct);
            return categories.Count > 0
                ? new ConnectionTestResult(true, $"Connected — {categories.Count} categories.", sw.Elapsed)
                : new ConnectionTestResult(false, "No data returned — check your API key and secret.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    // ── IBrowsable (root → Trending + categories → feeds → episodes) ────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A single "Podcast Index" root tile. Drilling in reveals Trending + category tiles. STATIC —
        // no network call here (the host enumerates roots at startup; a fetch would block the splash).
        await Task.CompletedTask;
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = "root",
            Title = DisplayName,
            Icon = "🎙",
            HasSubCategories = true,
            SourceState = new PiNode(PiNodeKind.Root),
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var node = category.SourceState as PiNode ?? InferNode(category.CategoryId);

        switch (node.Kind)
        {
            case PiNodeKind.Root:
            {
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
                        SourceState = new PiNode(PiNodeKind.Favorites),
                    });
                }

                tiles.Add(new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = "trending",
                    Title = "Trending",
                    Icon = "🔥",
                    HasSubCategories = true,
                    SourceState = new PiNode(PiNodeKind.Trending),
                });

                var categories = await EnsureCategoriesAsync(ct);
                foreach (var c in categories)
                {
                    tiles.Add(new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = $"cat:{c.Id}",
                        Title = c.Name,
                        Icon = "🎧",
                        HasSubCategories = true,
                        SourceState = new PiNode(PiNodeKind.Category, c.Id.ToString()),
                    });
                }
                return new BrowseResult { Categories = tiles };
            }

            case PiNodeKind.Favorites:
            {
                List<PiFavorite> favs;
                lock (_gate) favs = _favorites.Values.ToList();
                // Shows are drill-in containers (categories); episodes are leaf items.
                var categories = favs
                    .Where(f => f.Kind == PiFavoriteKind.Feed)
                    .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new SourceCategory
                    {
                        SourceInstanceId = InstanceId,
                        CategoryId = f.Id, // already "feed:{id}"
                        Title = f.Title,
                        ThumbnailUrl = f.ThumbnailUrl,
                        Icon = "🎙",
                        HasSubCategories = true,
                        SourceState = new PiNode(PiNodeKind.Feed, FeedIdOf(f.Id)),
                    })
                    .ToList();
                var items = favs
                    .Where(f => f.Kind == PiFavoriteKind.Episode)
                    .OrderByDescending(f => f.PublishedUnix ?? 0)
                    .Select(FavoriteToSourceItem)
                    .ToList();
                return new BrowseResult { Categories = categories, Items = items };
            }

            case PiNodeKind.Trending:
            {
                var feeds = await Client.GetTrendingFeedsAsync(categoryId: null, max: 50, ct);
                return new BrowseResult { Categories = feeds.Select(ToFeedCategory).ToList() };
            }

            case PiNodeKind.Category:
            {
                var categoryId = int.TryParse(node.Key, out var cid) ? cid : (int?)null;
                var feeds = await Client.GetTrendingFeedsAsync(categoryId, max: 50, ct);
                return new BrowseResult { Categories = feeds.Select(ToFeedCategory).ToList() };
            }

            case PiNodeKind.Feed:
            {
                var feedId = long.TryParse(node.Key, out var fid) ? fid : 0;
                if (feedId <= 0) return new BrowseResult();
                var episodes = await Client.GetEpisodesByFeedAsync(feedId, max: 100, ct);
                return new BrowseResult { Items = episodes.Select(ToEpisodeItem).ToList() };
            }

            default:
                return new BrowseResult();
        }
    }

    private static PiNode InferNode(string categoryId) => categoryId switch
    {
        "root" => new PiNode(PiNodeKind.Root),
        "favorites" => new PiNode(PiNodeKind.Favorites),
        "trending" => new PiNode(PiNodeKind.Trending),
        var s when s.StartsWith("cat:", StringComparison.Ordinal) => new PiNode(PiNodeKind.Category, s["cat:".Length..]),
        var s when s.StartsWith("feed:", StringComparison.Ordinal) => new PiNode(PiNodeKind.Feed, s["feed:".Length..]),
        _ => new PiNode(PiNodeKind.Root),
    };

    // Extracts the numeric feed id from a "feed:{id}" item/category id.
    private static string FeedIdOf(string feedItemId) =>
        feedItemId.StartsWith("feed:", StringComparison.Ordinal) ? feedItemId["feed:".Length..] : feedItemId;

    private async Task<IReadOnlyList<PiCategory>> EnsureCategoriesAsync(CancellationToken ct)
    {
        lock (_gate) { if (_categories != null) return _categories; }
        var categories = await Client.GetCategoriesAsync(ct);
        lock (_gate) _categories = categories;
        return categories;
    }

    // ── ITextSearchCapable ──────────────────────────────────────────────────────

    /// <summary>
    /// Searches Podcast Index by free-text query: matching shows (feeds) as drill-in containers whose
    /// episodes the user can then browse/play.
    /// </summary>
    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) yield break;

        var feeds = await Client.SearchFeedsAsync(q, max: 50, ct);
        foreach (var f in feeds)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToFeedContainerItem(f);
        }
    }

    // ── IScopedSearchable ────────────────────────────────────────────────────────

    /// <summary>
    /// Re-runs a feed search so the host can push a durable "Search: …" frame: Back from a drilled-in
    /// show returns to the search RESULTS (not the tile's default content), and the breadcrumb reads
    /// honestly. The scope is source-wide (Podcast Index has no per-node search), so the node is
    /// ignored and only the query matters — which makes it replayable from CategoryId alone.
    /// </summary>
    public async Task<BrowseResult> SearchInCategoryAsync(
        SourceCategory node, string query, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return new BrowseResult();

        var feeds = await Client.SearchFeedsAsync(q, max: 50, ct);
        return new BrowseResult { Categories = feeds.Select(ToFeedCategory).ToList() };
    }

    // ── IContainerPlayPolicy ─────────────────────────────────────────────────────

    // A podcast feed is a recency feed, not a curated set: "Play all" should play only the most
    // recent episode (episodes come back newest-first), never queue the entire back-catalog.
    public ContainerPlayAll GetPlayAllBehavior(SourceItem container) => ContainerPlayAll.PlayLatestOnly;

    public string? PlayAllLabel(SourceItem container) => "Play latest";

    // ── Mapping ──────────────────────────────────────────────────────────────────

    // A feed browse tile: drilling in browses its episodes (via the Feed node).
    private SourceCategory ToFeedCategory(PiFeed f) => new()
    {
        SourceInstanceId = InstanceId,
        CategoryId = $"feed:{f.Id}",
        Title = f.Title,
        ThumbnailUrl = f.ImageUrl,
        Icon = "🎙",
        HasSubCategories = true,
        SourceState = new PiNode(PiNodeKind.Feed, f.Id.ToString()),
    };

    // A feed search result: a container leaf whose drill-in browses its episodes.
    private SourceItem ToFeedContainerItem(PiFeed f) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = $"feed:{f.Id}",
        Title = f.Title,
        Subtitle = string.IsNullOrWhiteSpace(f.Author) ? "Podcast" : f.Author,
        ThumbnailUrl = f.ImageUrl,
        IsContainer = true,
        SourceState = new PiNode(PiNodeKind.Feed, f.Id.ToString()),
    };

    // A podcast episode is a FINITE, seekable track (not live) — carry a Duration and no IsLiveStream.
    // Audio-first: a video-enclosure episode plays as video (IsAudioOnly left false).
    private SourceItem ToEpisodeItem(PiEpisode e) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = e.Id.ToString(),
        Title = e.Title,
        Subtitle = "Podcast",
        ThumbnailUrl = e.ImageUrl,
        IsAudioOnly = !e.IsVideo,
        Duration = e.Duration,
        PublishedAt = e.Published,
        // Carry the episode so ResolveAsync can use the inline enclosureUrl without a re-fetch.
        SourceState = e,
    };

    // ── IFavoritable / IFavoriteCapture ─────────────────────────────────────────

    public bool IsFavorite(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return false;
        lock (_gate) return _favorites.ContainsKey(itemId);
    }

    public void SetFavorite(string itemId, bool favorite)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        lock (_gate)
        {
            bool changed;
            if (favorite)
            {
                // Seed a minimal record; RememberFavorite (called right after by the host) enriches it
                // with the real kind/title/url. Classify by id shape as a last resort: "feed:*" is a
                // show, anything else an episode.
                var kind = itemId.StartsWith("feed:", StringComparison.Ordinal)
                    ? PiFavoriteKind.Feed : PiFavoriteKind.Episode;
                changed = _favorites.TryAdd(itemId, new PiFavorite(itemId, kind, itemId, null, null));
            }
            else
            {
                changed = _favorites.Remove(itemId);
            }
            if (changed) SaveFavorites();
        }
    }

    /// <summary>
    /// Host hands us a snapshot at star-time so we persist the correct kind (show vs episode) plus the
    /// display data. Episodes store no enclosure here (the host's capture carries only container state,
    /// which is null for leaves) — <see cref="ResolveAsync"/> re-fetches the enclosure by episode id
    /// on play, so a favorited episode still replays.
    /// </summary>
    public void RememberFavorite(FavoriteCapture item)
    {
        if (string.IsNullOrEmpty(item.ItemId)) return;
        lock (_gate)
        {
            if (!_favorites.ContainsKey(item.ItemId)) return;

            var kind = item.IsContainer ? PiFavoriteKind.Feed : PiFavoriteKind.Episode;
            _favorites[item.ItemId] = new PiFavorite(
                item.ItemId,
                kind,
                string.IsNullOrWhiteSpace(item.Title) ? item.ItemId : item.Title,
                item.Subtitle,
                item.ThumbnailUrl,
                item.Duration?.TotalSeconds);
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
        PiFavorite? f;
        lock (_gate) f = _favorites.TryGetValue(itemId, out var rec) ? rec : null;
        return f is null ? null : FavoriteToSourceItem(f);
    }

    // Rebuild a SourceItem from a persisted favorite record, honoring its kind.
    private SourceItem FavoriteToSourceItem(PiFavorite f)
    {
        if (f.Kind == PiFavoriteKind.Feed)
        {
            return new SourceItem
            {
                SourceInstanceId = InstanceId,
                ItemId = f.Id, // "feed:{id}"
                Title = f.Title,
                Subtitle = f.Subtitle ?? "Podcast",
                ThumbnailUrl = f.ThumbnailUrl,
                IsContainer = true,
                SourceState = new PiNode(PiNodeKind.Feed, FeedIdOf(f.Id)),
            };
        }

        var duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        var published = f.PublishedUnix is { } u ? DateTimeOffset.FromUnixTimeSeconds(u) : (DateTimeOffset?)null;
        var isVideo = f.EnclosureType is { } t && t.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        long.TryParse(f.Id, out var epId);
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            Subtitle = f.Subtitle ?? "Podcast",
            ThumbnailUrl = f.ThumbnailUrl,
            IsAudioOnly = !isVideo,
            Duration = duration,
            PublishedAt = published,
            // Carry a PiEpisode so ResolveAsync can play from the stored enclosure with no re-fetch.
            SourceState = new PiEpisode(epId, f.Title, null, f.ThumbnailUrl, duration,
                f.EnclosureUrl ?? "", f.EnclosureType, published),
        };
    }

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private Dictionary<string, PiFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, PiFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<PiFavorite>>(File.ReadAllText(path)) ?? [];
            return list.Where(f => !string.IsNullOrEmpty(f.Id))
                       .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"PodcastIndex: favorites read failed: {ex.Message}");
            return new Dictionary<string, PiFavorite>(StringComparer.Ordinal);
        }
    }

    private void SaveFavorites()
    {
        try
        {
            var path = FavoritesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_favorites.Values.ToList()));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"PodcastIndex: favorites write failed: {ex.Message}");
        }
    }

    // ── IPlayableResolver ───────────────────────────────────────────────────────
    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var episode = item.SourceState as PiEpisode;
        var enclosureUrl = episode?.EnclosureUrl;
        var isVideo = episode?.IsVideo ?? false;

        // Favorite replay: the persisted item may lack an inline enclosure — re-fetch by episode id.
        if (string.IsNullOrWhiteSpace(enclosureUrl) && long.TryParse(item.ItemId, out var epId) && epId > 0)
        {
            var fetched = await Client.GetEpisodeByIdAsync(epId, ct);
            if (fetched is not null && !string.IsNullOrWhiteSpace(fetched.EnclosureUrl))
            {
                enclosureUrl = fetched.EnclosureUrl;
                isVideo = fetched.IsVideo;
            }
        }

        if (string.IsNullOrWhiteSpace(enclosureUrl))
        {
            Log(LogLevel.Warning, $"PodcastIndex: no enclosure URL for item '{item.ItemId}'.");
            return null;
        }

        // Podcast Index hands us the direct media file — audio (.mp3/.m4a) or video (.mp4). The host's
        // player fetches and plays it directly; finite/seekable, so no IsLiveStream.
        var layout = isVideo ? StreamLayout.Muxed : StreamLayout.AudioOnly;
        return new ResolvedStream(StreamTransport.Http, layout, enclosureUrl!);
    }

    public Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
        // The episode item already carries duration; nothing extra to enrich.
        => Task.FromResult<SourceMetadata?>(null);

    // ── Internals ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        // Nothing owned to release (the HttpClient belongs to the host).
    }

    private void Log(LogLevel level, string message) => _host?.Log(level, message);
}
