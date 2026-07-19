using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.SoundCloud;

// Per-item state so the source never re-derives the canonical URL it resolves via yt-dlp.
internal sealed record ScState(string Url);

// Node identity carried in SourceCategory.SourceState. Query is the scsearch term for a genre feed.
internal sealed record ScNode(ScNodeKind Kind, string? Query = null);

internal enum ScNodeKind { Root, Favorites, Genre }

// A favorite persisted with enough metadata to render instantly/offline.
internal sealed record ScFavorite(
    string Id, string Title, string Url, double? DurationSeconds, string? ThumbnailUrl, string? Uploader);

/// <summary>
/// SoundCloud source instance. Audio-only. Browses curated genre feeds and searches SoundCloud via
/// yt-dlp's KEYLESS <c>scsearch</c> extractor, resolving playback through the same host-bundled
/// yt-dlp. Users pin tracks with the star toggle (IFavoritable). Finite, seekable audio; resolution
/// is deferred (IDeferredStreamResolution) since each yt-dlp probe is expensive.
/// </summary>
public sealed class SoundCloudSource :
    IPhosphorSource, IBrowsable, ITextSearchCapable, IPlayableResolver,
    IDeferredStreamResolution, IFavoritable, IConnectionTestable, IPlaybackReportable
{
    // Curated genre feeds. SoundCloud has no keyless catalog API, so each is a canned scsearch term.
    private static readonly (string Title, string Query)[] Genres =
    [
        ("Electronic", "electronic"),
        ("House", "house"),
        ("Techno", "techno"),
        ("Hip-Hop", "hip hop"),
        ("Drum & Bass", "drum and bass"),
        ("Chill", "chillout"),
        ("Ambient", "ambient"),
        ("Rock", "rock"),
        ("Pop", "pop"),
        ("Jazz", "jazz"),
        ("Classical", "classical"),
        ("Soundtrack", "soundtrack"),
    ];

    private static readonly string IconRoot = char.ConvertFromUtf32(0x2601) + char.ConvertFromUtf32(0xFE0F); // cloud
    private static readonly string IconFav = char.ConvertFromUtf32(0x2B50);       // star
    private static readonly string IconGenre = char.ConvertFromUtf32(0x1F3B5);    // musical note

    private readonly object _gate = new();

    private IPluginHost? _host;
    private YtDlpResolver? _resolver;

    private int _resultLimit = 50;

    private Dictionary<string, ScFavorite> _favorites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScTrack> _seen = new(StringComparer.Ordinal);

    // Lazy discovery: ids known to be unplayable (DRM/no-formats), learned from play-time failures.
    private HashSet<string> _unplayable = new(StringComparer.Ordinal);
    // Diagnostic play/fail stats (dev-only), persisted alongside the unplayable set.
    private ScStats _stats = new();

    public SoundCloudSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => SoundCloudSourceProvider.SoundCloudTypeId;
    public string DisplayName { get; set; } = "SoundCloud";

    // Keyless discovery via yt-dlp: always ready.
    public bool IsConfigured => true;

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        EnsureResolver();
        _favorites = LoadFavorites();
        LoadUnplayable();
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _resultLimit = int.TryParse(Get(values, SoundCloudSourceProvider.KeyResultLimit), out var n)
            ? Math.Clamp(n, 1, 100) : 50;
    }

    private void EnsureResolver()
    {
        var path = _host?.GetToolPath("yt-dlp");
        if (string.IsNullOrWhiteSpace(path))
        {
            var local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
            path = File.Exists(local) ? local : "yt-dlp";
        }
        _resolver = new YtDlpResolver(path, _host is { } h ? h.Log : null);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureResolver();
        if (_resolver is null || !_resolver.IsAvailable)
            return new ConnectionTestResult(false, "yt-dlp is not available to the host.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tracks = await _resolver.SearchAsync("music", 1, ct);
        sw.Stop();
        return tracks.Count > 0
            ? new ConnectionTestResult(true, "Reachable — browse & search enabled.", sw.Elapsed)
            : new ConnectionTestResult(false, "yt-dlp reached, but SoundCloud returned no results.");
    }

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = "root",
            Title = DisplayName,
            Icon = IconRoot,
            HasSubCategories = true,
            SourceState = new ScNode(ScNodeKind.Root),
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        EnsureResolver();
        var node = category.SourceState as ScNode ?? new ScNode(ScNodeKind.Root);
        return node.Kind switch
        {
            ScNodeKind.Root => BrowseRoot(),
            ScNodeKind.Favorites => await BrowseFavoritesAsync(ct),
            ScNodeKind.Genre => await BrowseGenreAsync(node.Query, ct),
            _ => new BrowseResult(),
        };
    }

    private BrowseResult BrowseRoot()
    {
        var cats = new List<SourceCategory>
        {
            new()
            {
                SourceInstanceId = InstanceId,
                CategoryId = "favorites",
                Title = "Favorites",
                Icon = IconFav,
                HasSubCategories = true,
                SourceState = new ScNode(ScNodeKind.Favorites),
            },
        };

        foreach (var (title, query) in Genres)
        {
            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"genre:{query}",
                Title = title,
                Icon = IconGenre,
                HasSubCategories = false,
                SourceState = new ScNode(ScNodeKind.Genre, query),
            });
        }

        return new BrowseResult { Categories = cats };
    }

    private async Task<BrowseResult> BrowseGenreAsync(string? query, CancellationToken ct)
    {
        if (_resolver is null || string.IsNullOrEmpty(query)) return new BrowseResult();
        var tracks = await _resolver.SearchAsync(query, _resultLimit, ct);
        return new BrowseResult { Items = tracks.Select(ToSourceItem).ToList() };
    }

    private async Task<BrowseResult> BrowseFavoritesAsync(CancellationToken ct)
    {
        await EnrichFavoritesAsync(ct);
        List<ScFavorite> favs;
        lock (_gate) favs = _favorites.Values.OrderBy(f => f.Title).ToList();
        var items = favs.Select(f => new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            Subtitle = f.Uploader,
            ThumbnailUrl = f.ThumbnailUrl,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            IsAudioOnly = true,
            IsPlayable = IsPlayableId(f.Id),
            SourceState = new ScState(f.Url),
        }).ToList();
        return new BrowseResult { Items = items };
    }

    private async Task EnrichFavoritesAsync(CancellationToken ct)
    {
        if (_resolver is null) return;
        List<ScFavorite> stale;
        lock (_gate)
            stale = _favorites.Values.Where(f => string.IsNullOrEmpty(f.ThumbnailUrl)).ToList();
        if (stale.Count == 0) return;

        bool changed = false;
        foreach (var f in stale)
        {
            ct.ThrowIfCancellationRequested();
            var t = await _resolver.GetTrackAsync(f.Url, ct);
            if (t is null) continue;
            lock (_gate)
            {
                _favorites[f.Id] = new ScFavorite(
                    f.Id, t.Title, t.Url, t.Duration?.TotalSeconds, t.ThumbnailUrl, t.Uploader);
                changed = true;
            }
        }
        if (changed) { lock (_gate) SaveFavorites(); }
    }

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureResolver();
        if (_resolver is null) yield break;
        var tracks = await _resolver.SearchAsync(query, _resultLimit, ct);
        foreach (var t in tracks)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToSourceItem(t);
        }
    }

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        EnsureResolver();
        if (_resolver is null) return null;
        var url = UrlOf(item);
        if (url is null) return null;

        var (stream, definitive) = await _resolver.ResolveWithDiagnosisAsync(url, prefs, ct);
        if (stream is not null)
        {
            RecordOutcome(item.ItemId, success: true, definitiveFailure: false);
            return stream;
        }

        // Failed. Record it; a definitive failure (DRM/no-formats) also marks the id unplayable so it
        // surfaces as such on future searches (lazy discovery). Transient failures are counted only.
        RecordOutcome(item.ItemId, success: false, definitiveFailure: definitive);
        return null;
    }

    public async Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        EnsureResolver();
        if (_resolver is null) return null;
        var url = UrlOf(item);
        return url is null ? null : await _resolver.GetMetadataAsync(url, ct);
    }

    public bool IsFavorite(string itemId)
    {
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
                var rec = _seen.TryGetValue(itemId, out var t)
                    ? new ScFavorite(t.Id, t.Title, t.Url, t.Duration?.TotalSeconds, t.ThumbnailUrl, t.Uploader)
                    : new ScFavorite(itemId, $"SoundCloud {itemId}",
                        $"https://soundcloud.com/tracks/{itemId}", null, null, null);
                changed = !_favorites.ContainsKey(itemId);
                _favorites[itemId] = rec;
            }
            else
            {
                changed = _favorites.Remove(itemId);
            }
            if (changed) SaveFavorites();
        }
    }

    public IReadOnlyCollection<string> GetFavoriteIds()
    {
        lock (_gate) return _favorites.Keys.ToArray();
    }

    /// <summary>Rebuilds a playable item from a favorited id, using the stored rich record.</summary>
    public SourceItem? GetFavorite(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        ScFavorite? f;
        lock (_gate) f = _favorites.TryGetValue(itemId, out var rec) ? rec : null;
        if (f is null) return null;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            Subtitle = f.Uploader,
            ThumbnailUrl = f.ThumbnailUrl,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            IsAudioOnly = true,
            SourceState = new ScState(f.Url),
        };
    }

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private Dictionary<string, ScFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, ScFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<ScFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log($"SoundCloud: favorites read failed: {ex.Message}");
            return new Dictionary<string, ScFavorite>(StringComparer.Ordinal);
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
            _host?.Log($"SoundCloud: favorites write failed: {ex.Message}");
        }
    }

    // ── Lazy unplayable discovery + diagnostic stats ──────────────────────────────

    /// <summary>
    /// Host callback (IPlaybackReportable): a track failed to play. We persist only definitive
    /// (Unresolvable) failures as known-unplayable — transient failures (network/timeout) are counted
    /// but never mark a track permanently bad. Returns whether the id is now known-unplayable.
    /// </summary>
    public bool ReportPlaybackFailure(string itemId, PlaybackFailureKind kind)
    {
        if (string.IsNullOrEmpty(itemId)) return false;
        bool definitive = kind == PlaybackFailureKind.Unresolvable;
        RecordOutcome(itemId, success: false, definitiveFailure: definitive);
        lock (_gate) return _unplayable.Contains(itemId);
    }

    // Central place all resolve/play outcomes flow through: updates diagnostic stats and, for a
    // definitive failure, adds the id to the persisted unplayable set. Persists every outcome (stats
    // always change).
    private void RecordOutcome(string itemId, bool success, bool definitiveFailure)
    {
        lock (_gate)
        {
            _stats.Attempts++;
            if (success)
            {
                _stats.Successes++;
            }
            else
            {
                _stats.Failures++;
                if (definitiveFailure)
                {
                    _stats.DefinitiveFailures++;
                    if (!string.IsNullOrEmpty(itemId)) _unplayable.Add(itemId);
                }
                else
                {
                    _stats.TransientFailures++;
                }
            }
            SaveUnplayable();
        }
    }

    private string UnplayablePath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "unplayable.json");

    private void LoadUnplayable()
    {
        try
        {
            var path = UnplayablePath;
            if (!File.Exists(path)) return;
            var doc = JsonSerializer.Deserialize<UnplayableDoc>(File.ReadAllText(path));
            if (doc is null) return;
            lock (_gate)
            {
                _unplayable = new HashSet<string>(doc.Ids ?? [], StringComparer.Ordinal);
                _stats = doc.Stats ?? new ScStats();
            }
        }
        catch (Exception ex)
        {
            _host?.Log($"SoundCloud: unplayable read failed: {ex.Message}");
        }
    }

    // Caller holds _gate.
    private void SaveUnplayable()
    {
        try
        {
            var path = UnplayablePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var doc = new UnplayableDoc { Ids = _unplayable.ToList(), Stats = _stats };
            File.WriteAllText(path, JsonSerializer.Serialize(doc));
        }
        catch (Exception ex)
        {
            _host?.Log($"SoundCloud: unplayable write failed: {ex.Message}");
        }
    }

    private static string? UrlOf(SourceItem item) =>
        item.SourceState is ScState s ? s.Url : null;

    private SourceItem ToSourceItem(ScTrack t)
    {
        lock (_gate) _seen[t.Id] = t;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = t.Id,
            Title = t.Title,
            Subtitle = t.Uploader,
            ThumbnailUrl = t.ThumbnailUrl,
            Duration = t.Duration,
            IsAudioOnly = true,
            IsPlayable = IsPlayableId(t.Id),
            SourceState = new ScState(t.Url),
        };
    }

    private bool IsPlayableId(string id)
    {
        lock (_gate) return !_unplayable.Contains(id);
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;
}
