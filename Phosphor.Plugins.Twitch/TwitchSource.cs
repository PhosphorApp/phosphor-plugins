using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Twitch;

// Per-item state so the source never re-derives the canonical URL it resolves via yt-dlp, and knows
// whether to resolve/flag the stream as live. ChannelLogin ties every item (live stream OR VOD) back
// to its owning channel, which is what favoriting operates on (see below).
internal sealed record TwState(string Url, bool IsLive, string? ChannelLogin = null);

// Node identity carried in SourceCategory.SourceState. Login is set for Channel/ChannelVods nodes;
// CategoryName is set for a Category node (the Twitch "game name" its streams are listed under).
internal sealed record TwNode(TwNodeKind Kind, string? Login = null, string? CategoryName = null);

internal enum TwNodeKind { Root, Favorites, Pinball, TopLive, ChannelVods, Categories, Category }

// A favorite is always a CHANNEL (keyed by its stable login), never a specific video. Twitch VODs
// expire quickly (days–weeks), so pinning a video id would go stale; a channel login is permanent.
// Starring any item — live stream or VOD — favorites its owning channel. Persisted with enough to
// render the channel row instantly/offline.
internal sealed record TwFavorite(string Login, string Title, string? ThumbnailUrl);

/// <summary>
/// Twitch source instance. Browses curated pinball channels, the top live directory, and per-channel
/// VODs, and searches Twitch via its KEYLESS public GraphQL API, resolving playback through the
/// host-bundled yt-dlp. Live streams are flagged <see cref="SourceItem.IsLiveStream"/>; VODs are
/// finite/seekable. Users pin items with the star toggle (IFavoritable). Resolution is deferred
/// (IDeferredStreamResolution) since each yt-dlp probe is expensive.
/// </summary>
public sealed class TwitchSource :
    IPhosphorSource, IBrowsable, IPagedBrowsable, ITextSearchCapable, IPlayableResolver,
    IDeferredStreamResolution, IFavoritable, IConnectionTestable, IResultCachePolicy,
    IReplayableById, IContainerPlayPolicy
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string IconRoot = char.ConvertFromUtf32(0x1F3AE);     // game controller
    private static readonly string IconFav = char.ConvertFromUtf32(0x2B50);       // star
    private static readonly string IconPinball = char.ConvertFromUtf32(0x26AA);   // white circle (a pinball)
    private static readonly string IconLive = char.ConvertFromUtf32(0x1F534);     // red circle
    private static readonly string IconCategories = char.ConvertFromUtf32(0x1F5C2) + char.ConvertFromUtf32(0xFE0F); // card index dividers
    private static readonly string IconCategory = char.ConvertFromUtf32(0x1F4C2); // open folder
    private static readonly string IconChannel = char.ConvertFromUtf32(0x1F4FA);  // television

    private readonly object _gate = new();

    private IPluginHost? _host;
    private YtDlpResolver? _resolver;
    private TwitchGqlClient? _client;

    private VideoQuality _quality = VideoQuality.High;
    private List<string> _channels = new();
    private bool _liveIndicator = true;

    private Dictionary<string, TwFavorite> _favorites = new(StringComparer.OrdinalIgnoreCase);
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

        _liveIndicator = !bool.TryParse(Get(values, TwitchSourceProvider.KeyLiveIndicator), out var li) || li;

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
        _client ??= new TwitchGqlClient(http, _host is { } h ? (s => h.Log(LogLevel.Debug, s)) : (Action<string>?)null);
    }

    private void EnsureResolver()
    {
        var path = _host?.GetToolPath("yt-dlp");
        if (string.IsNullOrWhiteSpace(path))
        {
            var local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
            path = File.Exists(local) ? local : "yt-dlp";
        }
        _resolver = new YtDlpResolver(path, _host is { } h ? (s => h.Log(LogLevel.Debug, s)) : (Action<string>?)null);
    }

    // ── IResultCachePolicy ─────────────────────────────────────────────────────

    /// <summary>Twitch results are ephemeral live streams — never cache their result pages.</summary>
    public ResultCachePolicy GetResultCachePolicy() => new(Cache: false);

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

        var items = page.Items.Select(ToSourceItem).ToList();

        // For a channel's collection, inject the CURRENT live broadcast as the first item on the first
        // page when the channel is live. The host can theme it (red border / LIVE badge) via
        // SourceItem.IsLiveStream. This is why a favorited channel opens its videos rather than playing.
        if (node.Kind == TwNodeKind.ChannelVods && node.Login is { } chLogin && offset == 0)
        {
            var live = await _client!.GetLiveChannelAsync(chLogin, ct).ConfigureAwait(false);
            if (live is not null)
                items.Insert(0, ToSourceItem(live, showLiveBadge: _liveIndicator));
        }

        // Overstate the total while more pages remain so the host keeps requesting.
        var total = offset + items.Count + (page.HasMore ? count : 0);
        return new BrowsePage
        {
            Items = items,
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
            var live = await _client!.GetLiveChannelAsync(login, ct).ConfigureAwait(false);
            if (live is not null)
                liveItems.Add(ToSourceItem(live, showLiveBadge: _liveIndicator));

            var showLive = live is not null && _liveIndicator;
            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"vods:{login}",
                Title = live?.ChannelName ?? login,
                Icon = showLive ? IconLive : IconChannel,
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

        var cats = new List<SourceCategory>();
        foreach (var f in favs)
        {
            ct.ThrowIfCancellationRequested();

            // Every favorite is a channel: a CONTAINER you drill into (its videos), not something that
            // plays on click. Re-check live each open only to theme the tile (red LIVE glyph when the
            // channel is broadcasting); the actual live stream is injected as the first item inside the
            // channel's collection (see BrowsePageAsync). The live decoration honors the same
            // "Show live indicator" setting as the in-collection badge, so the two stay consistent.
            var live = await _client!.GetLiveChannelAsync(f.Login, ct).ConfigureAwait(false);
            var showLive = live is not null && _liveIndicator;
            var thumb = await ResolveChannelThumbAsync(f.Login, live, f.ThumbnailUrl, ct).ConfigureAwait(false);
            cats.Add(new SourceCategory
            {
                SourceInstanceId = InstanceId,
                CategoryId = $"vods:{f.Login}",
                Title = showLive ? $"{live!.ChannelName ?? f.Title} — ● LIVE" : (live?.ChannelName ?? f.Title),
                Icon = showLive ? IconLive : IconChannel,
                ThumbnailUrl = thumb,
                HasSubCategories = true,
                SourceState = new TwNode(TwNodeKind.ChannelVods, f.Login),
            });
        }
        return new BrowseResult { Categories = cats };
    }

    /// <summary>
    /// Resolves the best thumbnail for a channel container: the LIVE preview frame when broadcasting,
    /// else the most-recent VOD's thumbnail ("first thumbnail wins"), else a stored/avatar fallback.
    /// The live case is dynamic (reflects the current show); the VOD fallback keeps offline channels
    /// from rendering blank.
    /// </summary>
    private async Task<string?> ResolveChannelThumbAsync(
        string login, TwitchVideo? live, string? fallback, CancellationToken ct)
    {
        if (live?.ThumbnailUrl is { Length: > 0 } livePreview)
            return livePreview;
        var vodThumb = await _client!.GetMostRecentVodThumbnailAsync(login, ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(vodThumb) ? vodThumb : fallback;
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

    // Favoriting always operates on the CHANNEL, keyed by login. A row's ItemId may be a login (live
    // stream) or a video id (VOD); either way we resolve it to the owning channel. Consequence: star
    // one Dead Flip video and every Dead Flip row shows starred — the whole view is consistent.

    public bool IsFavorite(string itemId)
    {
        var login = LoginOf(itemId);
        if (login is null) return false;
        lock (_gate) return _favorites.ContainsKey(login);
    }

    public void SetFavorite(string itemId, bool favorite)
    {
        var login = LoginOf(itemId);
        if (login is null) return;

        lock (_gate)
        {
            bool changed;
            if (favorite)
            {
                // Build a display record from whatever we last saw for this channel.
                var v = _seen.TryGetValue(itemId, out var seen) ? seen
                      : _seen.Values.FirstOrDefault(x =>
                            string.Equals(x.ChannelLogin, login, StringComparison.OrdinalIgnoreCase));
                var title = v?.ChannelName ?? login;
                var thumb = v is { IsLive: true } ? null : v?.ThumbnailUrl; // prefer stable profile art at open
                changed = !_favorites.ContainsKey(login);
                _favorites[login] = new TwFavorite(login, title, thumb);
            }
            else
            {
                changed = _favorites.Remove(login);
            }
            if (changed) SaveFavorites();
        }
    }

    public IReadOnlyCollection<string> GetFavoriteIds()
    {
        // Report every favorited channel's login PLUS any currently-seen video ids belonging to those
        // channels, so the host's per-row star reflects "starred" across the whole channel view.
        lock (_gate)
        {
            var ids = new HashSet<string>(_favorites.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var v in _seen.Values)
                if (v.ChannelLogin is { } cl && _favorites.ContainsKey(cl))
                    ids.Add(v.Id);
            return ids.ToArray();
        }
    }

    /// <summary>Rebuilds a favorited channel as a browsable CONTAINER (its VODs, with the live feed
    /// injected on open) — matching how <see cref="BrowseFavoritesAsync"/> surfaces it. The host
    /// drills into it (or expands it to play), so it must carry the <c>vods:{login}</c> node identity,
    /// not a live leaf.</summary>
    public SourceItem? GetFavorite(string itemId)
    {
        var login = LoginOf(itemId);
        if (login is null) return null;
        lock (_gate) if (!_favorites.ContainsKey(login)) return null;

        EnsureClient();
        // Theme the tile by current live state (red LIVE glyph), but the item is always a container.
        var live = _client is not null
            ? _client.GetLiveChannelAsync(login).GetAwaiter().GetResult()
            : null;
        var showLive = live is not null && _liveIndicator;
        TwFavorite? f;
        lock (_gate) f = _favorites.TryGetValue(login, out var rec) ? rec : null;
        // Live preview when broadcasting, else most-recent VOD thumbnail, else stored avatar.
        var thumb = _client is not null
            ? ResolveChannelThumbAsync(login, live, f?.ThumbnailUrl, default).GetAwaiter().GetResult()
            : f?.ThumbnailUrl;

        return new SourceItem
        {
            SourceInstanceId = InstanceId,
            ItemId = $"vods:{login}",
            Title = showLive
                ? $"{live!.ChannelName ?? f?.Title ?? login} — ● LIVE"
                : (live?.ChannelName ?? f?.Title ?? login),
            ThumbnailUrl = thumb,
            IsContainer = true,
            SourceState = new TwNode(TwNodeKind.ChannelVods, login),
        };
    }

    /// <summary>
    /// Rebuilds a playable item from any persisted id (typically a live row's channel login), with no
    /// prior browse state — used to re-resolve a live queue entry after a restart. Reflects the
    /// channel's <em>current</em> live feed (not whatever was live when it was queued), or <c>null</c>
    /// if the channel isn't live now.
    /// </summary>
    public SourceItem? RebuildPlayable(string itemId)
    {
        var login = LoginOf(itemId);
        if (login is null) return null;

        EnsureClient();
        var live = _client!.GetLiveChannelAsync(login).GetAwaiter().GetResult();
        return live is not null ? ToSourceItem(live) : null;
    }

    // A Twitch CHANNEL is a recency feed: "Play all" plays what's live now (or the most recent VOD),
    // never the entire back-catalog. A game/category tile (or the Categories list) is a pure grouping
    // whose children are themselves channels — it has NO meaningful "Play all", so it's browse-only
    // and the host hides the play affordance rather than playing one arbitrary stream from within it.
    public ContainerPlayAll GetPlayAllBehavior(SourceItem container) =>
        IsGroupingNode(container) ? ContainerPlayAll.None : ContainerPlayAll.PlayLatestOnly;

    public string? PlayAllLabel(SourceItem container) =>
        IsGroupingNode(container) ? null : "Play latest";

    // A grouping node lists other containers (games → channels), not playable leaves: the Categories
    // list tile and any single game/category tile. Resolved from the carried node when present, else
    // the id shape (durable across a reconstructed item whose SourceState is null).
    private static bool IsGroupingNode(SourceItem container)
    {
        if (container.SourceState is TwNode n)
            return n.Kind is TwNodeKind.Categories or TwNodeKind.Category;
        var id = container.ItemId ?? "";
        return id == "categories" || id.StartsWith("cat:", StringComparison.Ordinal);
    }

    // Maps a row's ItemId to its owning channel login: a live-stream row is already the login; a VOD
    // row is resolved via the channel we saw it under; a channel-VODs container id is "vods:{login}"
    // (its stable form, which survives a restart when _seen is empty). Falls back to the id as a login.
    private string? LoginOf(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        // Channel container id carries the login directly (e.g. a favorited channel: "vods:foxcity…").
        if (itemId.StartsWith("vods:", StringComparison.Ordinal))
            return itemId["vods:".Length..];
        return ChannelLoginFor(itemId) ?? itemId;
    }

    private string FavoritesPath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "favorites.json");

    private Dictionary<string, TwFavorite> LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) return new Dictionary<string, TwFavorite>(StringComparer.OrdinalIgnoreCase);
            var list = JsonSerializer.Deserialize<List<TwFavorite>>(File.ReadAllText(path));
            // Normalize the key to the bare channel login: a channel container was previously stored
            // with its "vods:{login}" node id (an unreachable key that neither IsFavorite nor
            // GetFavorite could match). Strip the prefix so the entry is canonical and self-heals on
            // the next save. De-duplicate to the first record if both forms somehow exist.
            var normalized = new Dictionary<string, TwFavorite>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in list ?? [])
            {
                if (string.IsNullOrEmpty(f.Login)) continue;
                var login = f.Login.StartsWith("vods:", StringComparison.Ordinal)
                    ? f.Login["vods:".Length..]
                    : f.Login;
                if (string.IsNullOrEmpty(login) || normalized.ContainsKey(login)) continue;
                var title = f.Title.StartsWith("vods:", StringComparison.Ordinal) ? login : f.Title;
                normalized[login] = f with { Login = login, Title = title };
            }
            return normalized;
        }
        catch (Exception ex)
        {
            _host?.Log(LogLevel.Warning, $"Twitch: favorites read failed: {ex.Message}");
            return new Dictionary<string, TwFavorite>(StringComparer.OrdinalIgnoreCase);
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
            _host?.Log(LogLevel.Warning, $"Twitch: favorites write failed: {ex.Message}");
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
            ShowLiveBadge = false,
            Duration = v.Duration,
            PublishedAt = v.PublishedAt,
            SourceState = new TwState(v.Url, v.IsLive, v.ChannelLogin),
        };
    }

    // Overload that lets the caller opt into the red "live now" thumbnail badge — used for the live
    // feed injected atop a channel's collection (gated by the "Show live indicator" setting).
    private SourceItem ToSourceItem(TwitchVideo v, bool showLiveBadge)
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
            ShowLiveBadge = showLiveBadge && v.IsLive,
            Duration = v.Duration,
            PublishedAt = v.PublishedAt,
            SourceState = new TwState(v.Url, v.IsLive, v.ChannelLogin),
        };
    }

    // Resolves the owning channel login for a row's ItemId. Live-stream rows are already keyed by
    // login; VOD rows are keyed by video id, so we look up the channel we saw them under.
    private string? ChannelLoginFor(string itemId)
    {
        lock (_gate)
            return _seen.TryGetValue(itemId, out var v) && !string.IsNullOrEmpty(v.ChannelLogin)
                ? v.ChannelLogin
                : null;
    }

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
