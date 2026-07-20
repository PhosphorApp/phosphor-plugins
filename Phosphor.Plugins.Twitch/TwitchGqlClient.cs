using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Phosphor.Plugins.Twitch;

/// <summary>
/// A Twitch stream/VOD/channel as surfaced to the source. <see cref="Url"/> is the canonical
/// twitch.tv link the yt-dlp resolver plays. <see cref="IsLive"/> distinguishes an endless live
/// broadcast (no duration, flagged live at resolve) from a finite, seekable VOD.
/// </summary>
public sealed record TwitchVideo(
    string Id,
    string Title,
    string Url,
    bool IsLive,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    string? ChannelName,
    DateTimeOffset? PublishedAt = null,
    string? ChannelLogin = null);

/// <summary>One page of a listing: items plus whether more pages remain (GQL is cursor-based).</summary>
public sealed record TwitchVideoPage(IReadOnlyList<TwitchVideo> Items, bool HasMore, string? Cursor);

/// <summary>
/// A Twitch category (what the API still calls a "game" — e.g. "Just Chatting", "Music", "Art", a
/// specific game title). <see cref="Name"/> is the key both the directory query and yt-dlp use.
/// </summary>
public sealed record TwitchCategory(string Id, string Name, string? BoxArtUrl);

/// <summary>
/// Pure-<see cref="HttpClient"/> Twitch client. Discovery rides Twitch's public GraphQL endpoint
/// (<c>gql.twitch.tv/gql</c>) with the well-known anonymous web <c>Client-ID</c> — no OAuth, no
/// account, no token (the same keyless surface yt-dlp itself uses for extraction). This is an
/// unofficial endpoint (Twitch's own web frontend uses it); it can change without notice, so callers
/// must treat failures as "discovery unavailable", not crash. No UI, no threading assumptions;
/// mirrors the Dailymotion client shape.
/// </summary>
public sealed class TwitchGqlClient(HttpClient http, Action<string>? log = null)
{
    private const string GqlEndpoint = "https://gql.twitch.tv/gql";
    // The public, well-known anonymous web Client-ID Twitch's own site ships. Not a secret/credential.
    private const string AnonClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    private readonly HttpClient _http = http;
    private readonly Action<string>? _log = log;

    /// <summary>Lightweight reachability + shape check (a tiny directory query).</summary>
    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var body = BuildDirectoryQuery("Just Chatting", 1, null);
            using var resp = await PostAsync(body, ct);
            if (resp.IsSuccessStatusCode) return (true, "Reachable.");
            return (false, $"Unexpected response: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Searches live channels matching <paramref name="query"/> (first page).</summary>
    public async IAsyncEnumerable<TwitchVideo> SearchAsync(
        string query, int limit = 30, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildChannelSearchQuery(query, limit);
        using var resp = await PostAsync(body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.Invoke($"Twitch search '{query}' failed {(int)resp.StatusCode}: {Trim(text, 200)}");
            yield break;
        }

        using var doc = JsonDocument.Parse(text);
        if (!TryGetPath(doc.RootElement, out var edges, "data", "searchStreams", "edges"))
            yield break;

        foreach (var edge in edges.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (!edge.TryGetProperty("node", out var node)) continue;
            var v = MapStream(node);
            if (v is not null) yield return v;
        }
    }

    /// <summary>Fetches the current top live streams (the front-page directory), paged by cursor.</summary>
    public async Task<TwitchVideoPage> GetTopLivePageAsync(
        int limit, string? cursor, CancellationToken ct = default)
    {
        var body = BuildTopLiveQuery(limit, cursor);
        using var resp = await PostAsync(body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.Invoke($"Twitch top-live failed {(int)resp.StatusCode}: {Trim(text, 200)}");
            return new TwitchVideoPage([], false, null);
        }
        return ParseStreamsConnection(text, "data", "streams");
    }

    /// <summary>
    /// Fetches Twitch's top categories (the "games"/directories that back the home-page groupings),
    /// ordered by current viewers. Keyless, cursor-paged.
    /// </summary>
    public async Task<(IReadOnlyList<TwitchCategory> Items, bool HasMore, string? Cursor)> GetTopCategoriesPageAsync(
        int limit, string? cursor, CancellationToken ct = default)
    {
        var body = BuildTopCategoriesQuery(limit, cursor);
        using var resp = await PostAsync(body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.Invoke($"Twitch top-categories failed {(int)resp.StatusCode}: {Trim(text, 200)}");
            return ([], false, null);
        }

        using var doc = JsonDocument.Parse(text);
        if (!TryGetPath(doc.RootElement, out var conn, "data", "games"))
            return ([], false, null);

        var items = new List<TwitchCategory>();
        string? lastCursor = null;
        if (conn.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String)
                    lastCursor = c.GetString();
                if (!edge.TryGetProperty("node", out var node)) continue;
                var cat = MapCategory(node);
                if (cat is not null) items.Add(cat);
            }
        }
        var hasMore = TryGetPath(conn, out var pi, "pageInfo", "hasNextPage") &&
                      pi.ValueKind == JsonValueKind.True;
        return (items, hasMore, hasMore ? lastCursor : null);
    }

    /// <summary>
    /// Fetches the live streams within a category (by its <paramref name="categoryName"/>), paged by
    /// cursor. The category name is the API's "game name" (e.g. "Just Chatting", "Music").
    /// </summary>
    public async Task<TwitchVideoPage> GetCategoryStreamsPageAsync(
        string categoryName, int limit, string? cursor, CancellationToken ct = default)
    {
        var body = BuildDirectoryQuery(categoryName, limit, cursor);
        using var resp = await PostAsync(body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.Invoke($"Twitch category '{categoryName}' failed {(int)resp.StatusCode}: {Trim(text, 200)}");
            return new TwitchVideoPage([], false, null);
        }

        using var doc = JsonDocument.Parse(text);
        if (!TryGetPath(doc.RootElement, out var conn, "data", "game", "streams"))
            return new TwitchVideoPage([], false, null);

        return ParseStreamsConnection(conn);
    }
    public async Task<TwitchVideo?> GetLiveChannelAsync(string login, CancellationToken ct = default)
    {
        var body = BuildUserStreamQuery(login);
        using var resp = await PostAsync(body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.Invoke($"Twitch channel '{login}' failed {(int)resp.StatusCode}: {Trim(text, 200)}");
            return null;
        }

        using var doc = JsonDocument.Parse(text);
        if (!TryGetPath(doc.RootElement, out var user, "data", "user")) return null;
        if (user.ValueKind != JsonValueKind.Object) return null;

        var displayName = user.TryGetProperty("displayName", out var dn) ? dn.GetString() : login;
        if (!user.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object)
            return null; // offline

        var title = TryGetPath(user, out var bt, "broadcastSettings", "title") ? bt.GetString() : null;
        var thumb = user.TryGetProperty("profileImageURL", out var pi) ? pi.GetString() : null;
        var started = ParseDate(stream, "createdAt");

        // Key by the STABLE login (not the ephemeral per-broadcast stream id) so a favorited channel
        // survives across broadcasts.
        return new TwitchVideo(
            login,
            string.IsNullOrWhiteSpace(title) ? (displayName ?? login) : title!,
            $"https://www.twitch.tv/{login}",
            IsLive: true,
            Duration: null,
            ThumbnailUrl: thumb,
            ChannelName: displayName ?? login,
            PublishedAt: started,
            ChannelLogin: login);
    }
    public async Task<TwitchVideoPage> GetChannelVideosPageAsync(
        string login, int limit, string? cursor, CancellationToken ct = default)
    {
        var body = BuildChannelVideosQuery(login, limit, cursor);
        using var resp = await PostAsync(body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.Invoke($"Twitch VODs '{login}' failed {(int)resp.StatusCode}: {Trim(text, 200)}");
            return new TwitchVideoPage([], false, null);
        }

        using var doc = JsonDocument.Parse(text);
        if (!TryGetPath(doc.RootElement, out var conn, "data", "user", "videos"))
            return new TwitchVideoPage([], false, null);

        return ParseVideosConnection(conn, login);
    }

    // ── Request plumbing ─────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostAsync(string json, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, GqlEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Client-ID", AnonClientId);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return _http.SendAsync(req, ct);
    }

    // ── GraphQL query builders (plain persisted-free queries the anon endpoint accepts) ──────────

    private static string BuildChannelSearchQuery(string query, int limit) =>
        Gql($$"""
        query {
          searchStreams(userQuery: {{JsonStr(query)}}, first: {{limit}}) {
            edges { node {
              id title createdAt
              broadcaster { login displayName profileImageURL(width: 150) }
              previewImageURL(width: 320, height: 180)
            } }
          }
        }
        """);

    private static string BuildTopLiveQuery(int limit, string? cursor) =>
        Gql($$"""
        query {
          streams(first: {{limit}}{{After(cursor)}}) {
            edges { cursor node {
              id title createdAt
              broadcaster { login displayName profileImageURL(width: 150) }
              previewImageURL(width: 320, height: 180)
            } }
            pageInfo { hasNextPage }
          }
        }
        """);

    private static string BuildDirectoryQuery(string game, int limit, string? cursor) =>
        Gql($$"""
        query {
          game(name: {{JsonStr(game)}}) {
            streams(first: {{limit}}{{After(cursor)}}) {
              edges { cursor node {
                id title createdAt
                broadcaster { login displayName profileImageURL(width: 150) }
                previewImageURL(width: 320, height: 180)
              } }
              pageInfo { hasNextPage }
            }
          }
        }
        """);

    private static string BuildTopCategoriesQuery(int limit, string? cursor) =>
        Gql($$"""
        query {
          games(first: {{limit}}{{After(cursor)}}, options: { sort: VIEWER_COUNT }) {
            edges { cursor node {
              id name boxArtURL(width: 144, height: 192)
            } }
            pageInfo { hasNextPage }
          }
        }
        """);

    private static string BuildUserStreamQuery(string login) =>
        Gql($$"""
        query {
          user(login: {{JsonStr(login)}}) {
            displayName
            profileImageURL(width: 150)
            broadcastSettings { title }
            stream { id type viewersCount createdAt }
          }
        }
        """);

    private static string BuildChannelVideosQuery(string login, int limit, string? cursor) =>
        Gql($$"""
        query {
          user(login: {{JsonStr(login)}}) {
            videos(first: {{limit}}, sort: TIME{{After(cursor)}}) {
              edges { cursor node {
                id title lengthSeconds publishedAt previewThumbnailURL(width: 320, height: 180)
                owner { login displayName }
              } }
              pageInfo { hasNextPage }
            }
          }
        }
        """);

    // ── Parsing ──────────────────────────────────────────────────────────────────

    private TwitchVideoPage ParseStreamsConnection(string text, params string[] path)
    {
        using var doc = JsonDocument.Parse(text);
        if (!TryGetPath(doc.RootElement, out var conn, path))
            return new TwitchVideoPage([], false, null);
        return ParseStreamsConnection(conn);
    }

    private TwitchVideoPage ParseStreamsConnection(JsonElement conn)
    {
        var items = new List<TwitchVideo>();
        string? lastCursor = null;
        if (conn.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String)
                    lastCursor = c.GetString();
                if (!edge.TryGetProperty("node", out var node)) continue;
                var v = MapStream(node);
                if (v is not null) items.Add(v);
            }
        }
        var hasMore = TryGetPath(conn, out var pi, "pageInfo", "hasNextPage") &&
                      pi.ValueKind == JsonValueKind.True;
        return new TwitchVideoPage(items, hasMore, hasMore ? lastCursor : null);
    }

    private TwitchVideoPage ParseVideosConnection(JsonElement conn, string login)
    {
        var items = new List<TwitchVideo>();
        string? lastCursor = null;
        if (conn.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String)
                    lastCursor = c.GetString();
                if (!edge.TryGetProperty("node", out var node)) continue;
                var v = MapVod(node, login);
                if (v is not null) items.Add(v);
            }
        }
        var hasMore = TryGetPath(conn, out var pi, "pageInfo", "hasNextPage") &&
                      pi.ValueKind == JsonValueKind.True;
        return new TwitchVideoPage(items, hasMore, hasMore ? lastCursor : null);
    }

    private static TwitchCategory? MapCategory(JsonElement node)
    {
        var id = node.TryGetProperty("id", out var i) ? i.GetString() : null;
        var name = node.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(name)) return null;
        var boxArt = node.TryGetProperty("boxArtURL", out var b) ? b.GetString() : null;
        return new TwitchCategory(id ?? name!, name!, boxArt);
    }

    private static TwitchVideo? MapStream(JsonElement node)
    {
        var login = TryGetPath(node, out var lg, "broadcaster", "login") ? lg.GetString() : null;
        if (string.IsNullOrEmpty(login)) return null;

        var display = TryGetPath(node, out var dn, "broadcaster", "displayName") ? dn.GetString() : login;
        var title = node.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } tt
            ? tt : (display ?? login);
        var thumb = node.TryGetProperty("previewImageURL", out var pv) ? pv.GetString() : null;
        var started = ParseDate(node, "createdAt");

        // Key a live stream by its STABLE login, not the ephemeral per-broadcast stream id, so a
        // favorited channel keeps working across broadcasts (each new stream gets a fresh id).
        return new TwitchVideo(
            login!,
            title!,
            $"https://www.twitch.tv/{login}",
            IsLive: true,
            Duration: null,
            ThumbnailUrl: thumb,
            ChannelName: display ?? login,
            PublishedAt: started,
            ChannelLogin: login);
    }

    private static TwitchVideo? MapVod(JsonElement node, string login)
    {
        var id = node.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;

        var title = node.TryGetProperty("title", out var t) ? t.GetString() ?? $"VOD {id}" : $"VOD {id}";
        var ownerLogin = TryGetPath(node, out var ol, "owner", "login") ? ol.GetString() : login;
        var display = TryGetPath(node, out var dn, "owner", "displayName") ? dn.GetString() : login;
        TimeSpan? duration = node.TryGetProperty("lengthSeconds", out var ls) && ls.TryGetInt32(out var secs) && secs > 0
            ? TimeSpan.FromSeconds(secs) : null;
        var thumb = node.TryGetProperty("previewThumbnailURL", out var pt) ? pt.GetString() : null;
        var published = ParseDate(node, "publishedAt");

        return new TwitchVideo(
            id!,
            title,
            $"https://www.twitch.tv/videos/{id}",
            IsLive: false,
            Duration: duration,
            ThumbnailUrl: thumb,
            ChannelName: display ?? login,
            PublishedAt: published,
            ChannelLogin: ownerLogin ?? login);
    }

    // ── Small helpers ────────────────────────────────────────────────────────────

    private static string Gql(string query) =>
        JsonSerializer.Serialize(new { query });

    private static string JsonStr(string s) => JsonSerializer.Serialize(s);

    private static DateTimeOffset? ParseDate(JsonElement node, string prop) =>
        node.TryGetProperty(prop, out var d) && d.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(d.GetString(), out var dt)
            ? dt : null;

    private static string After(string? cursor) =>
        string.IsNullOrEmpty(cursor) ? "" : $", after: {JsonStr(cursor)}";

    private static bool TryGetPath(JsonElement root, out JsonElement result, params string[] path)
    {
        var cur = root;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur))
            {
                result = default;
                return false;
            }
        }
        result = cur;
        return true;
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
