using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Vimeo;

internal sealed record VimeoState(string Url);

// Kind + the API collection path (e.g. "/categories/music" or "/channels/staffpicks") for paged nodes.
internal sealed record VimeoNode(VimeoNodeKind Kind, string? CollectionUri = null);

internal enum VimeoNodeKind { Root, Favorites, Category, Channel }

// A curated Vimeo channel we surface as a top-level tile (e.g. Staff Picks).
internal sealed record VimeoCuratedChannel(string Key, string Name, string Uri);

internal sealed record VimeoFavorite(string Id, string Title, string Url, double? DurationSeconds, string? ThumbnailUrl);

/// <summary>
/// Vimeo source instance. Browses Vimeo''s curated categories (via the API) and searches Vimeo, and
/// resolves playback through the host-bundled yt-dlp. Users pin videos with the star toggle
/// (IFavoritable) instead of pasting URLs. Finite, seekable video. Resolution is deferred
/// (IDeferredStreamResolution) because each yt-dlp probe is expensive; the host resolves at play time.
/// </summary>
public sealed class VimeoSource :
    IPhosphorSource, IBrowsable, IPagedBrowsable, ITextSearchCapable, IPlayableResolver,
    IDeferredStreamResolution, IFavoritable, IConnectionTestable
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string IconRoot = char.ConvertFromUtf32(0x1F3AC);     // clapper
    private static readonly string IconFav = char.ConvertFromUtf32(0x2B50);       // star
    private static readonly string IconCat = char.ConvertFromUtf32(0x1F39E) + char.ConvertFromUtf32(0xFE0F); // film frames
    private static readonly string IconChannel = char.ConvertFromUtf32(0x1F31F);  // glowing star (curated)

    // Curated Vimeo channels surfaced as top-level tiles. Staff Picks is Vimeo''s flagship curation.
    private static readonly VimeoCuratedChannel[] CuratedChannels =
    [
        new("staffpicks", "Staff Picks", "/channels/staffpicks"),
    ];

    private readonly object _gate = new();

    private IPluginHost? _host;
    private YtDlpResolver? _resolver;
    private VimeoClient? _client;

    private string _accessToken = "";
    private VideoQuality _quality = VideoQuality.High;

    private Dictionary<string, VimeoFavorite> _favorites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VimeoVideo> _seen = new(StringComparer.Ordinal);

    public VimeoSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => VimeoSourceProvider.VimeoTypeId;
    public string DisplayName { get; set; } = "Vimeo";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_accessToken);

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
        _accessToken = (Get(values, VimeoSourceProvider.KeyAccessToken) ?? "").Trim();
        _quality = Enum.TryParse<VideoQuality>(
            Get(values, VimeoSourceProvider.KeyQuality), ignoreCase: true, out var q) ? q : VideoQuality.High;
        _client = null;
    }

    private void EnsureClient()
    {
        if (string.IsNullOrWhiteSpace(_accessToken)) { _client = null; return; }
        var http = _host?.HttpClient ?? SharedHttpClient;
        _client ??= new VimeoClient(http, _accessToken, _host is { } h ? h.Log : null);
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
        if (_client is null)
            return new ConnectionTestResult(false, "Add a Vimeo API access token to browse/search.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, message) = await _client.TestAsync(ct);
        sw.Stop();
        return new ConnectionTestResult(ok, ok ? "Token valid - browse & search enabled." : message,
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
            SourceState = new VimeoNode(VimeoNodeKind.Root),
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        EnsureClient();
        var node = category.SourceState as VimeoNode ?? new VimeoNode(VimeoNodeKind.Root);

        switch (node.Kind)
        {
            case VimeoNodeKind.Root:
                return await BrowseRootAsync(ct);
            case VimeoNodeKind.Favorites:
                return await BrowseFavoritesAsync(ct);
            // Category/Channel nodes yield NO items here on purpose: returning an empty BrowseResult
            // makes the host drive IPagedBrowsable.BrowsePageAsync for lazy "load more".
            case VimeoNodeKind.Category:
            case VimeoNodeKind.Channel:
            default:
                return new BrowseResult();
        }
    }

    // ── IPagedBrowsable ──────────────────────────────────────────────────────────

    public async Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null || category.SourceState is not VimeoNode { CollectionUri: { } uri })
            return new BrowsePage();

        var page = (offset / Math.Max(1, count)) + 1; // Vimeo pages are 1-based.
        var result = await _client.GetVideosPageAsync(uri, page, count, ct);
        return new BrowsePage
        {
            Items = result.Items.Select(ToSourceItem).ToList(),
            TotalSize = result.Total,
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
                SourceState = new VimeoNode(VimeoNodeKind.Favorites),
            },
        };

        // Curated channels (Staff Picks) next — these are the strongest jukebox picks.
        foreach (var ch in CuratedChannels)
        {
            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"chan:{ch.Key}",
                Title = ch.Name,
                Icon = IconChannel,
                HasSubCategories = true,
                SourceState = new VimeoNode(VimeoNodeKind.Channel, ch.Uri),
            });
        }

        if (_client is not null)
        {
            foreach (var c in await _client.GetCategoriesAsync(ct))
            {
                cats.Add(new SourceCategory
                {
                    SourceInstanceId = InstanceId,
                    CategoryId = $"cat:{c.Key}",
                    Title = c.Name,
                    Icon = IconCat,
                    HasSubCategories = true,
                    SourceState = new VimeoNode(VimeoNodeKind.Category, c.Uri),
                });
            }
        }

        return new BrowseResult { Categories = cats };
    }

    private async Task<BrowseResult> BrowseFavoritesAsync(CancellationToken ct)
    {
        await EnrichFavoritesAsync(ct);
        List<VimeoFavorite> favs;
        lock (_gate) favs = _favorites.Values.OrderBy(f => f.Title).ToList();
        var items = favs.Select(f => new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            ThumbnailUrl = f.ThumbnailUrl,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            SourceState = new VimeoState(f.Url),
        }).ToList();
        return new BrowseResult { Items = items };
    }

    // Backfills favorites that were pinned by id only (never seen this session, so no title/thumbnail)
    // via GET /videos/{id}. Only runs for records missing a thumbnail; persists once if anything changed.
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
                _favorites[id] = new VimeoFavorite(v.Id, v.Title, v.Url, v.Duration?.TotalSeconds, v.ThumbnailUrl);
                changed = true;
            }
        }
        if (changed) { lock (_gate) SaveFavorites(); }
    }

    public async IAsyncEnumerable<SourceItem> SearchAsync(
        string query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureClient();
        if (_client is null) yield break;

        await foreach (var v in _client.SearchAsync(query, ct: ct).WithCancellation(ct))
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
                    ? new VimeoFavorite(v.Id, v.Title, v.Url, v.Duration?.TotalSeconds, v.ThumbnailUrl)
                    : new VimeoFavorite(itemId, $"Vimeo {itemId}", $"https://vimeo.com/{itemId}", null, null);
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

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private Dictionary<string, VimeoFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, VimeoFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<VimeoFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log($"Vimeo: favorites read failed: {ex.Message}");
            return new Dictionary<string, VimeoFavorite>(StringComparer.Ordinal);
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
            _host?.Log($"Vimeo: favorites write failed: {ex.Message}");
        }
    }

    private static string? UrlOf(SourceItem item) =>
        item.SourceState is VimeoState s ? s.Url : null;

    private SourceItem ToSourceItem(VimeoVideo v)
    {
        lock (_gate) _seen[v.Id] = v;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = v.Id,
            Title = v.Title,
            ThumbnailUrl = v.ThumbnailUrl,
            Duration = v.Duration,
            SourceState = new VimeoState(v.Url),
        };
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;
}
