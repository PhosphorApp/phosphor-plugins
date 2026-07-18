using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Dailymotion;

// Per-item state so the source never re-derives the canonical URL it resolves via yt-dlp.
internal sealed record DmState(string Url);

// Node identity carried in SourceCategory.SourceState. CategoryId is the Dailymotion channel id.
internal sealed record DmNode(DmNodeKind Kind, string? CategoryId = null);

internal enum DmNodeKind { Root, Favorites, Category }

// A favorite persisted with enough metadata to render instantly/offline (no bounded catalog to
// reconstruct against).
internal sealed record DmFavorite(string Id, string Title, string Url, double? DurationSeconds, string? ThumbnailUrl);

/// <summary>
/// Dailymotion source instance. Browses Dailymotion editorial categories and searches Dailymotion via
/// its KEYLESS public API, resolving playback through the host-bundled yt-dlp. Users pin videos with
/// the star toggle (IFavoritable). Finite, seekable video; resolution is deferred
/// (IDeferredStreamResolution) since each yt-dlp probe is expensive.
/// </summary>
public sealed class DailymotionSource :
    IPhosphorSource, IBrowsable, IPagedBrowsable, ITextSearchCapable, IPlayableResolver,
    IDeferredStreamResolution, IFavoritable, IConnectionTestable
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string IconRoot = char.ConvertFromUtf32(0x1F3AC);     // clapper
    private static readonly string IconFav = char.ConvertFromUtf32(0x2B50);       // star
    private static readonly string IconCat = char.ConvertFromUtf32(0x1F39E) + char.ConvertFromUtf32(0xFE0F); // film frames

    private readonly object _gate = new();

    private IPluginHost? _host;
    private YtDlpResolver? _resolver;
    private DailymotionClient? _client;

    private VideoQuality _quality = VideoQuality.High;

    private Dictionary<string, DmFavorite> _favorites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DmVideo> _seen = new(StringComparer.Ordinal);

    public DailymotionSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => DailymotionSourceProvider.DailymotionTypeId;
    public string DisplayName { get; set; } = "Dailymotion";

    // Keyless discovery: always ready.
    public bool IsConfigured => true;

    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        EnsureResolver();
        EnsureClient();
        _favorites = LoadFavorites();
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _quality = Enum.TryParse<VideoQuality>(
            Get(values, DailymotionSourceProvider.KeyQuality), ignoreCase: true, out var q) ? q : VideoQuality.High;
    }

    private void EnsureClient()
    {
        var http = _host?.HttpClient ?? SharedHttpClient;
        _client ??= new DailymotionClient(http, _host is { } h ? h.Log : null);
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

        EnsureClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, message) = await _client!.TestAsync(ct);
        sw.Stop();
        return new ConnectionTestResult(ok, ok ? "Reachable - browse & search enabled." : message,
            ok ? sw.Elapsed : null);
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
            SourceState = new DmNode(DmNodeKind.Root),
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        EnsureClient();
        var node = category.SourceState as DmNode ?? new DmNode(DmNodeKind.Root);

        switch (node.Kind)
        {
            case DmNodeKind.Root:
                return await BrowseRootAsync(ct);
            case DmNodeKind.Favorites:
                return await BrowseFavoritesAsync(ct);
            // Category nodes return NO items here on purpose: the empty BrowseResult makes the host
            // drive IPagedBrowsable.BrowsePageAsync for lazy "load more".
            case DmNodeKind.Category:
            default:
                return new BrowseResult();
        }
    }

    public async Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default)
    {
        EnsureClient();
        if (category.SourceState is not DmNode { Kind: DmNodeKind.Category, CategoryId: { } id })
            return new BrowsePage();

        var page = (offset / Math.Max(1, count)) + 1; // Dailymotion pages are 1-based.
        var result = await _client!.GetCategoryVideosPageAsync(id, page, count, ct);
        // Prefer the reported total; when absent (0) but more pages exist, keep paging by overstating.
        var total = result.Total > 0 ? result.Total : offset + result.Items.Count + (result.HasMore ? count : 0);
        return new BrowsePage
        {
            Items = result.Items.Select(ToSourceItem).ToList(),
            TotalSize = total,
        };
    }

    private async Task<BrowseResult> BrowseRootAsync(CancellationToken ct)
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
                SourceState = new DmNode(DmNodeKind.Favorites),
            },
        };

        foreach (var c in await _client!.GetCategoriesAsync(ct))
        {
            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"cat:{c.Id}",
                Title = c.Name,
                Icon = IconCat,
                HasSubCategories = true,
                SourceState = new DmNode(DmNodeKind.Category, c.Id),
            });
        }

        return new BrowseResult { Categories = cats };
    }

    private async Task<BrowseResult> BrowseFavoritesAsync(CancellationToken ct)
    {
        await EnrichFavoritesAsync(ct);
        List<DmFavorite> favs;
        lock (_gate) favs = _favorites.Values.OrderBy(f => f.Title).ToList();
        var items = favs.Select(f => new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            ThumbnailUrl = f.ThumbnailUrl,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            SourceState = new DmState(f.Url),
        }).ToList();
        return new BrowseResult { Items = items };
    }

    private async Task EnrichFavoritesAsync(CancellationToken ct)
    {
        if (_client is null) return;
        List<string> stale;
        lock (_gate)
            stale = _favorites.Values.Where(f => string.IsNullOrEmpty(f.ThumbnailUrl)).Select(f => f.Id).ToList();
        if (stale.Count == 0) return;

        bool changed = false;
        foreach (var id in stale)
        {
            ct.ThrowIfCancellationRequested();
            var v = await _client.GetVideoAsync(id, ct);
            if (v is null) continue;
            lock (_gate)
            {
                _favorites[id] = new DmFavorite(v.Id, v.Title, v.Url, v.Duration?.TotalSeconds, v.ThumbnailUrl);
                changed = true;
            }
        }
        if (changed) { lock (_gate) SaveFavorites(); }
    }

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureClient();
        await foreach (var v in _client!.SearchAsync(query, ct: ct).WithCancellation(ct))
            yield return ToSourceItem(v);
    }

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        EnsureResolver();
        if (_resolver is null) return null;
        var url = UrlOf(item);
        if (url is null) return null;

        var prefsWithQuality = prefs with
        {
            MaxQuality = prefs.MaxQuality == VideoQuality.High ? _quality : prefs.MaxQuality,
        };
        return await _resolver.ResolveAsync(url, prefsWithQuality, ct);
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
                var rec = _seen.TryGetValue(itemId, out var v)
                    ? new DmFavorite(v.Id, v.Title, v.Url, v.Duration?.TotalSeconds, v.ThumbnailUrl)
                    : new DmFavorite(itemId, $"Dailymotion {itemId}", $"https://www.dailymotion.com/video/{itemId}", null, null);
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
        DmFavorite? f;
        lock (_gate) f = _favorites.TryGetValue(itemId, out var rec) ? rec : null;
        if (f is null) return null;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            ThumbnailUrl = f.ThumbnailUrl,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            SourceState = new DmState(f.Url),
        };
    }

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private Dictionary<string, DmFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, DmFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<DmFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log($"Dailymotion: favorites read failed: {ex.Message}");
            return new Dictionary<string, DmFavorite>(StringComparer.Ordinal);
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
            _host?.Log($"Dailymotion: favorites write failed: {ex.Message}");
        }
    }

    private static string? UrlOf(SourceItem item) =>
        item.SourceState is DmState s ? s.Url : null;

    private SourceItem ToSourceItem(DmVideo v)
    {
        lock (_gate) _seen[v.Id] = v;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = v.Id,
            Title = v.Title,
            ThumbnailUrl = v.ThumbnailUrl,
            Duration = v.Duration,
            SourceState = new DmState(v.Url),
        };
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;
}
