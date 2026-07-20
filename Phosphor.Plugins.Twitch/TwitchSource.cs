using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Twitch;

// Per-item state so the source never re-derives the canonical URL it resolves via yt-dlp, and knows
// whether to resolve/flag the stream as live.
internal sealed record TwState(string Url, bool IsLive);

// Node identity carried in SourceCategory.SourceState. Login is set for Channel/ChannelVods nodes;
// CategoryName is set for a Category node (the Twitch "game name" its streams are listed under).
internal sealed record TwNode(TwNodeKind Kind, string? Login = null, string? CategoryName = null);

internal enum TwNodeKind { Root, Favorites, Pinball, TopLive, ChannelVods, Categories, Category }

// A favorite persisted with enough metadata to render instantly/offline.
internal sealed record TwFavorite(
    string Id, string Title, string Url, bool IsLive, double? DurationSeconds, string? ThumbnailUrl);

/// <summary>
/// Twitch source instance. Browses curated pinball channels, the top live directory, and per-channel
/// VODs, and searches Twitch via its KEYLESS public GraphQL API, resolving playback through the
/// host-bundled yt-dlp. Live streams are flagged <see cref="SourceItem.IsLiveStream"/>; VODs are
/// finite/seekable. Users pin items with the star toggle (IFavoritable). Resolution is deferred
/// (IDeferredStreamResolution) since each yt-dlp probe is expensive.
/// </summary>
public sealed class TwitchSource :
    IPhosphorSource, IBrowsable, IPagedBrowsable, ITextSearchCapable, IPlayableResolver,
    IDeferredStreamResolution, IFavoritable, IConnectionTestable
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string IconRoot = char.ConvertFromUtf32(0x1F3AE);     // game controller
    private static readonly string IconFav = char.ConvertFromUtf32(0x2B50);       // star
    private static readonly string IconPinball = char.ConvertFromUtf32(0x26AA);   // white circle (a pinball)
    private static readonly string IconLive = char.ConvertFromUtf32(0x1F534);     // red circle
    private static readonly string IconCategories = char.ConvertFromUtf32(0x1F5C2) + char.ConvertFromUtf32(0xFE0F); // card index dividers
    private static readonly string IconCategory = char.ConvertFromUtf32(0x1F4C2); // open folder

    private readonly object _gate = new();

    private IPluginHost? _host;
    private YtDlpResolver? _resolver;
    private TwitchGqlClient? _client;

    private VideoQuality _quality = VideoQuality.High;
    private List<string> _channels = new();

    private Dictionary<string, TwFavorite> _favorites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TwitchVideo> _seen = new(StringComparer.Ordinal);

    public TwitchSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => TwitchSourceProvider.TwitchTypeId;
    public string DisplayName { get; set; } = "Twitch";

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
            Get(values, TwitchSourceProvider.KeyQuality), ignoreCase: true, out var q) ? q : VideoQuality.High;

        var raw = Get(values, TwitchSourceProvider.KeyChannels);
        var channels = (raw ?? string.Join('\n', TwitchSourceProvider.DefaultChannels))
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLogin)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_gate) _channels = channels;
    }

    // Accept either a bare login or a full twitch.tv/<login> URL and reduce to the login slug.
    private static string NormalizeLogin(string s)
    {
        s = s.Trim();
        var idx = s.LastIndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[(idx + "twitch.tv/".Length)..];
        s = s.TrimStart('@').Trim('/');
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        return s.ToLowerInvariant();
    }

    private void EnsureClient()
    {
        var http = _host?.HttpClient ?? SharedHttpClient;
        _client ??= new TwitchGqlClient(http, _host is { } h ? h.Log : null);
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
            SourceState = new TwNode(TwNodeKind.Root),
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        EnsureClient();
        var node = category.SourceState as TwNode ?? NodeFromId(category.CategoryId);

        switch (node.Kind)
        {
            case TwNodeKind.Root:
                return await BrowseRootAsync(ct);
            case TwNodeKind.Favorites:
                return await BrowseFavoritesAsync(ct);
            case TwNodeKind.Pinball:
                return await BrowsePinballAsync(ct);
            case TwNodeKind.Categories:
                return await BrowseCategoriesAsync(ct);
            // Paged nodes (TopLive, ChannelVods, Category) return NO items here on purpose: the empty
            // BrowseResult makes the host drive IPagedBrowsable.BrowsePageAsync for lazy "load more".
            case TwNodeKind.TopLive:
            case TwNodeKind.ChannelVods:
            case TwNodeKind.Category:
            default:
                return new BrowseResult();
        }
    }

    public async Task<BrowsePage> BrowsePageAsync(
        SourceCategory category, int offset, int count, CancellationToken ct = default)
    {
        EnsureClient();
        var node = category.SourceState as TwNode ?? NodeFromId(category.CategoryId);

        // GQL is cursor-based, not offset-based. We keep a per-node cursor map keyed by offset so the
        // host's offset paging maps onto Twitch's forward cursors.
        var cursor = GetCursor(category.CategoryId, offset);

        TwitchVideoPage page = node.Kind switch
        {
            TwNodeKind.TopLive => await _client!.GetTopLivePageAsync(count, cursor, ct),
            TwNodeKind.ChannelVods when node.Login is { } login =>
                await _client!.GetChannelVideosPageAsync(login, count, cursor, ct),
            TwNodeKind.Category when node.CategoryName is { } cat =>
                await _client!.GetCategoryStreamsPageAsync(cat, count, cursor, ct),
            _ => new TwitchVideoPage([], false, null),
        };

        if (page.HasMore && page.Cursor is { } next)
            SetCursor(category.CategoryId, offset + page.Items.Count, next);

        // Overstate the total while more pages remain so the host keeps requesting.
        var total = offset + page.Items.Count + (page.HasMore ? count : 0);
        return new BrowsePage
        {
            Items = page.Items.Select(ToSourceItem).ToList(),
            TotalSize = total,
        };
    }

    private async Task<BrowseResult> BrowseRootAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        bool hasChannels;
        lock (_gate) hasChannels = _channels.Count > 0;

        var cats = new List<SourceCategory>
        {
            new()
            {
                SourceInstanceId = InstanceId,
                CategoryId = "favorites",
                Title = "Favorites",
                Icon = IconFav,
                HasSubCategories = true,
                SourceState = new TwNode(TwNodeKind.Favorites),
            },
        };

        // Only surface the Pinball node when the user has at least one curated channel configured.
        if (hasChannels)
        {
            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = "pinball",
                Title = "Pinball",
                Icon = IconPinball,
                HasSubCategories = true,
                SourceState = new TwNode(TwNodeKind.Pinball),
            });
        }

        cats.Add(new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = "categories",
            Title = "Categories",
            Icon = IconCategories,
            HasSubCategories = true,
            SourceState = new TwNode(TwNodeKind.Categories),
        });
        cats.Add(new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = "toplive",
            Title = "Top Live",
            Icon = IconLive,
            HasSubCategories = true,
            SourceState = new TwNode(TwNodeKind.TopLive),
        });

        return new BrowseResult { Categories = cats };
    }

    // The Pinball node: one sub-node per curated channel (their VODs), plus any that are live NOW
    // surfaced directly as playable live items at the top.
    private async Task<BrowseResult> BrowsePinballAsync(CancellationToken ct)
    {
        List<string> channels;
        lock (_gate) channels = _channels.ToList();

        var liveItems = new List<SourceItem>();
        var cats = new List<SourceCategory>();

        foreach (var login in channels)
        {
            ct.ThrowIfCancellationRequested();
            var live = await _client!.GetLiveChannelAsync(login, ct);
            if (live is not null)
                liveItems.Add(ToSourceItem(live));

            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"vods:{login}",
                Title = live?.ChannelName ?? login,
                Icon = live is not null ? IconLive : null,
                HasSubCategories = true,
                SourceState = new TwNode(TwNodeKind.ChannelVods, login),
            });
        }

        return new BrowseResult { Categories = cats, Items = liveItems };
    }

    // The Categories node: Twitch's top categories (its "games"/directories — Just Chatting, Music,
    // Art, IRL, specific game titles, …), each drilling into that category's live streams (paged).
    // These are the same directories that back the home-page groupings (Games, IRL, Music & DJs,
    // Creative, Esports). We surface a healthy first page ordered by viewers.
    private async Task<BrowseResult> BrowseCategoriesAsync(CancellationToken ct)
    {
        var (cats, _, _) = await _client!.GetTopCategoriesPageAsync(100, null, ct);
        var nodes = cats.Select(c => new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = $"cat:{c.Name}",
            Title = c.Name,
            Icon = IconCategory,
            ThumbnailUrl = c.BoxArtUrl,
            HasSubCategories = true,
            SourceState = new TwNode(TwNodeKind.Category, CategoryName: c.Name),
        }).ToList();
        return new BrowseResult { Categories = nodes };
    }

    private async Task<BrowseResult> BrowseFavoritesAsync(CancellationToken ct)
    {
        List<TwFavorite> favs;
        lock (_gate) favs = _favorites.Values.OrderBy(f => f.Title).ToList();

        var items = new List<SourceItem>();
        foreach (var f in favs)
        {
            ct.ThrowIfCancellationRequested();

            // A channel favorite (twitch.tv/<login>, not a /videos/ VOD) has no fixed content — it's
            // "whatever that channel is broadcasting now". Re-check its live status every open so it
            // reflects the CURRENT stream: live favorites resolve to the active broadcast; offline
            // ones are surfaced but marked unplayable rather than silently pointing at a dead URL.
            if (IsChannelFavorite(f))
            {
                var live = await _client!.GetLiveChannelAsync(f.Id, ct);
                if (live is not null)
                {
                    items.Add(ToSourceItem(live));
                    continue;
                }

                items.Add(new SourceItem
                {
                    SourceInstanceId = InstanceId,
                    ItemId = f.Id,
                    Title = f.Title,
                    Subtitle = "Offline",
                    ThumbnailUrl = f.ThumbnailUrl,
                    IsLiveStream = true,
                    IsPlayable = false,
                    SourceState = new TwState(f.Url, IsLive: true),
                });
                continue;
            }

            items.Add(new SourceItem
            {
                SourceInstanceId = InstanceId,
                ItemId = f.Id,
                Title = f.Title,
                ThumbnailUrl = f.ThumbnailUrl,
                IsLiveStream = f.IsLive,
                Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
                SourceState = new TwState(f.Url, f.IsLive),
            });
        }
        return new BrowseResult { Items = items };
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
        if (item.SourceState is not TwState state) return null;

        var prefsWithQuality = prefs with
        {
            MaxQuality = prefs.MaxQuality == VideoQuality.High ? _quality : prefs.MaxQuality,
        };
        return await _resolver.ResolveAsync(state.Url, prefsWithQuality, state.IsLive, ct);
    }

    public async Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
    {
        EnsureResolver();
        if (_resolver is null) return null;
        if (item.SourceState is not TwState state) return null;
        // Live streams have no meaningful duration; skip the (slow) probe.
        if (state.IsLive) return null;
        return await _resolver.GetMetadataAsync(state.Url, ct);
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
                    ? new TwFavorite(v.Id, v.Title, v.Url, v.IsLive, v.Duration?.TotalSeconds, v.ThumbnailUrl)
                    : new TwFavorite(itemId, $"Twitch {itemId}", $"https://www.twitch.tv/videos/{itemId}", false, null, null);
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
        TwFavorite? f;
        lock (_gate) f = _favorites.TryGetValue(itemId, out var rec) ? rec : null;
        if (f is null) return null;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = f.Id,
            Title = f.Title,
            ThumbnailUrl = f.ThumbnailUrl,
            IsLiveStream = f.IsLive,
            Duration = f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            SourceState = new TwState(f.Url, f.IsLive),
        };
    }

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private Dictionary<string, TwFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, TwFavorite>(StringComparer.Ordinal);
            var list = JsonSerializer.Deserialize<List<TwFavorite>>(File.ReadAllText(path));
            return (list ?? []).Where(f => !string.IsNullOrEmpty(f.Id))
                .ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _host?.Log($"Twitch: favorites read failed: {ex.Message}");
            return new Dictionary<string, TwFavorite>(StringComparer.Ordinal);
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
            _host?.Log($"Twitch: favorites write failed: {ex.Message}");
        }
    }

    private SourceItem ToSourceItem(TwitchVideo v)
    {
        lock (_gate) _seen[v.Id] = v;
        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = v.Id,
            Title = v.Title,
            Subtitle = v.ChannelName,
            ThumbnailUrl = v.ThumbnailUrl,
            IsLiveStream = v.IsLive,
            Duration = v.Duration,
            PublishedAt = v.PublishedAt,
            SourceState = new TwState(v.Url, v.IsLive),
        };
    }

    // A channel favorite (live, keyed by login) vs. a finite VOD favorite. Channel favorites point at
    // twitch.tv/<login>; VODs point at twitch.tv/videos/<id>. We re-check channel favorites live.
    private static bool IsChannelFavorite(TwFavorite f) =>
        f.IsLive && !f.Url.Contains("/videos/", StringComparison.OrdinalIgnoreCase);

    // ── Cursor bookkeeping (map the host's offset paging onto Twitch forward cursors) ────────────
    private readonly Dictionary<string, string> _cursors = new(StringComparer.Ordinal);

    private string? GetCursor(string categoryId, int offset)
    {
        if (offset <= 0) return null;
        lock (_gate) return _cursors.TryGetValue($"{categoryId}@{offset}", out var c) ? c : null;
    }

    private void SetCursor(string categoryId, int offset, string cursor)
    {
        lock (_gate) _cursors[$"{categoryId}@{offset}"] = cursor;
    }

    private static TwNode NodeFromId(string categoryId) => categoryId switch
    {
        "root" => new TwNode(TwNodeKind.Root),
        "favorites" => new TwNode(TwNodeKind.Favorites),
        "pinball" => new TwNode(TwNodeKind.Pinball),
        "toplive" => new TwNode(TwNodeKind.TopLive),
        "categories" => new TwNode(TwNodeKind.Categories),
        _ when categoryId.StartsWith("vods:", StringComparison.Ordinal) =>
            new TwNode(TwNodeKind.ChannelVods, categoryId["vods:".Length..]),
        _ when categoryId.StartsWith("cat:", StringComparison.Ordinal) =>
            new TwNode(TwNodeKind.Category, CategoryName: categoryId["cat:".Length..]),
        _ => new TwNode(TwNodeKind.Root),
    };

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;
}
