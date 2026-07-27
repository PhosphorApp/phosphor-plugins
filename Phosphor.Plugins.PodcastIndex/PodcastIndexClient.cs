using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Phosphor.Plugins.PodcastIndex;

/// <summary>
/// Minimal Podcast Index REST client (api.podcastindex.org). Signs every request with the
/// Amazon-style auth headers the API requires and surfaces feeds/episodes/categories. Pure
/// <see cref="HttpClient"/> — no browser, no external tools. Podcast Index is a pure INDEX, so
/// episode responses carry the direct <c>enclosureUrl</c> inline; there is no separate resolve call.
/// </summary>
/// <remarks>
/// Auth (per the docs): each request must send <c>User-Agent</c>, <c>X-Auth-Key</c>,
/// <c>X-Auth-Date</c> (unix seconds), and <c>Authorization</c> where
/// <c>Authorization = SHA1hex(apiKey + apiSecret + unixSeconds)</c>. The <c>X-Auth-Date</c> value
/// MUST be the same timestamp folded into the hash.
/// </remarks>
public sealed class PodcastIndexClient
{
    private const string ApiBase = "https://api.podcastindex.org/api/1.0";
    private const string UserAgent = "Phosphor/1.0";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly Action<string>? _log;

    public PodcastIndexClient(HttpClient http, string apiKey, string apiSecret, Action<string>? log = null)
    {
        _http = http;
        _apiKey = apiKey;
        _apiSecret = apiSecret;
        _log = log;
    }

    /// <summary>Free-text search across podcast feeds (shows).</summary>
    public async Task<IReadOnlyList<PiFeed>> SearchFeedsAsync(string query, int max = 50, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return [];
        var root = await GetAsync($"/search/byterm?q={Uri.EscapeDataString(q)}&max={max}", ct);
        return ParseFeeds(root);
    }

    /// <summary>Lists trending feeds, optionally filtered to a <paramref name="categoryId"/>.</summary>
    public async Task<IReadOnlyList<PiFeed>> GetTrendingFeedsAsync(int? categoryId = null, int max = 50, CancellationToken ct = default)
    {
        var path = categoryId is > 0
            ? $"/podcasts/trending?max={max}&cat={categoryId}"
            : $"/podcasts/trending?max={max}";
        var root = await GetAsync(path, ct);
        return ParseFeeds(root);
    }

    /// <summary>Fetches the category taxonomy (id + name).</summary>
    public async Task<IReadOnlyList<PiCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var root = await GetAsync("/categories/list", ct);
        var categories = new List<PiCategory>();
        if (root is { } r && r.TryGetProperty("feeds", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
            {
                var id = c.TryGetProperty("id", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (id > 0 && !string.IsNullOrWhiteSpace(name))
                    categories.Add(new PiCategory(id, name!));
            }
        }
        return categories;
    }

    /// <summary>Lists the episodes of a feed (show), newest first.</summary>
    public async Task<IReadOnlyList<PiEpisode>> GetEpisodesByFeedAsync(long feedId, int max = 100, CancellationToken ct = default)
    {
        var root = await GetAsync($"/episodes/byfeedid?id={feedId}&max={max}", ct);
        var episodes = new List<PiEpisode>();
        if (root is { } r && r.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var ep = ParseEpisode(e);
                if (ep is not null) episodes.Add(ep);
            }
        }
        return episodes;
    }

    /// <summary>Fetches a single episode by its Podcast Index id (used to re-resolve a favorite
    /// whose inline enclosure wasn't retained across persistence). Returns <c>null</c> if not found.</summary>
    public async Task<PiEpisode?> GetEpisodeByIdAsync(long episodeId, CancellationToken ct = default)
    {
        var root = await GetAsync($"/episodes/byid?id={episodeId}", ct);
        // /episodes/byid returns the episode under a single "episode" object.
        if (root is { } r && r.TryGetProperty("episode", out var e) && e.ValueKind == JsonValueKind.Object)
            return ParseEpisode(e);
        return null;
    }

    // ── Parsing ──────────────────────────────────────────────────────────────────

    private static IReadOnlyList<PiFeed> ParseFeeds(JsonElement? root)
    {
        var feeds = new List<PiFeed>();
        if (root is { } r && r.TryGetProperty("feeds", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var id = f.TryGetProperty("id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
                var title = f.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (id <= 0 || string.IsNullOrWhiteSpace(title)) continue;
                var author = f.TryGetProperty("author", out var a) ? a.GetString() : null;
                var desc = f.TryGetProperty("description", out var d) ? d.GetString() : null;
                var image = f.TryGetProperty("image", out var im) ? im.GetString()
                    : f.TryGetProperty("artwork", out var aw) ? aw.GetString() : null;
                feeds.Add(new PiFeed(id, title!, author, desc, image));
            }
        }
        return feeds;
    }

    private static PiEpisode? ParseEpisode(JsonElement e)
    {
        var id = e.TryGetProperty("id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
        var title = e.TryGetProperty("title", out var t) ? t.GetString() : null;
        var enclosure = e.TryGetProperty("enclosureUrl", out var eu) ? eu.GetString() : null;
        if (id <= 0 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(enclosure))
            return null;
        var desc = e.TryGetProperty("description", out var d) ? d.GetString() : null;
        var image = e.TryGetProperty("image", out var im) && !string.IsNullOrWhiteSpace(im.GetString()) ? im.GetString()
            : e.TryGetProperty("feedImage", out var fi) ? fi.GetString() : null;
        var enclosureType = e.TryGetProperty("enclosureType", out var et) ? et.GetString() : null;
        TimeSpan? duration = e.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number
            && du.TryGetInt32(out var secs) && secs > 0
            ? TimeSpan.FromSeconds(secs) : null;
        DateTimeOffset? published = e.TryGetProperty("datePublished", out var dp)
            && dp.ValueKind == JsonValueKind.Number && dp.TryGetInt64(out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        return new PiEpisode(id, title!, desc, image, duration, enclosure!, enclosureType, published);
    }

    // ── Signed GET ───────────────────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
            AddAuthHeaders(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"PodcastIndex: HTTP {(int)resp.StatusCode} for {path}");
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"PodcastIndex: request '{path}' failed: {ex.Message}");
            return null;
        }
    }

    // Builds the Amazon-style auth headers: X-Auth-Date is unix seconds, Authorization is the
    // lowercase-hex SHA-1 of (apiKey + apiSecret + that same timestamp).
    private void AddAuthHeaders(HttpRequestMessage req)
    {
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var authHash = Sha1Hex(_apiKey + _apiSecret + unixSeconds);

        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("X-Auth-Key", _apiKey);
        req.Headers.TryAddWithoutValidation("X-Auth-Date", unixSeconds);
        req.Headers.TryAddWithoutValidation("Authorization", authHash);
        req.Headers.Accept.ParseAdd("application/json");
    }

    private static string Sha1Hex(string input)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
