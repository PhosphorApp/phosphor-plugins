using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Phosphor.Plugins.Vimeo;

/// <summary>
/// A lightweight Vimeo video as returned by the API. Only the fields Phosphor needs.
/// <see cref="Url"/> is the canonical vimeo.com link the yt-dlp resolver plays.
/// </summary>
public sealed record VimeoVideo(
    string Id,
    string Title,
    string Url,
    TimeSpan? Duration,
    string? ThumbnailUrl);

/// <summary>
/// A Vimeo top-level category (Vimeo's own curated buckets — Animation, Music, Documentary, …).
/// <see cref="Uri"/> is the API path (e.g. <c>/categories/animation</c>) used to fetch its videos.
/// </summary>
public sealed record VimeoCategory(
    string Key,
    string Name,
    string Uri);

/// <summary>One page of a video listing: the items plus the total count for paging.</summary>
public sealed record VimeoVideoPage(IReadOnlyList<VimeoVideo> Items, int Total);

/// <summary>
/// Pure-<see cref="HttpClient"/> Vimeo REST client (public scope). Uses an unauthenticated
/// app access token — <c>Authorization: bearer &lt;token&gt;</c> per request — so no OAuth
/// redirect flow is needed. Implements public search plus Vimeo's curated category browse; the
/// user's private library (likes/folders/uploads) would need user-OAuth and is deliberately out of
/// scope. No UI, no threading assumptions — mirrors the shape of the Jellyfin client.
/// </summary>
public sealed class VimeoClient(HttpClient http, string accessToken, Action<string>? log = null)
{
    private const string ApiBase = "https://api.vimeo.com";
    // Pin the API version + ask only for the fields we map, to keep responses small.
    private const string ApiVersion = "application/vnd.vimeo.*+json;version=3.4";
    private const string VideoFields = "uri,name,link,duration,pictures.sizes";
    private const string CategoryFields = "uri,name";

    private readonly HttpClient _http = http;
    private readonly string _accessToken = accessToken;
    private readonly Action<string>? _log = log;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_accessToken);

    /// <summary>
    /// Verifies the token by fetching the authenticated app/user context (<c>/me</c> works for a
    /// user token; public tokens resolve app context). Returns (ok, message).
    /// </summary>
    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        // A minimal public call that requires a valid token: a 1-result search.
        try
        {
            using var req = Build(HttpMethod.Get, "/videos?per_page=1&query=test");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
                return (true, "Token valid.");
            if ((int)resp.StatusCode == 401)
                return (false, "401 Unauthorized — check the access token.");
            return (false, $"Unexpected response: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Searches public videos for <paramref name="query"/>, yielding up to <paramref name="perPage"/>
    /// results (Vimeo caps at 100). Paging beyond the first page is deferred.
    /// </summary>
    public async IAsyncEnumerable<VimeoVideo> SearchAsync(
        string query, int perPage = 50, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var path = $"/videos?query={Uri.EscapeDataString(query)}&per_page={Math.Clamp(perPage, 1, 100)}" +
                   $"&fields={Uri.EscapeDataString(VideoFields)}";
        JsonDocument? doc = null;
        try
        {
            using var req = Build(HttpMethod.Get, path);
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"Vimeo search failed {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms: {Trim(text, 200)}");
                yield break;
            }
            doc = JsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Vimeo search threw after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                _log?.Invoke($"Vimeo search '{query}' returned no data array in {sw.ElapsedMilliseconds}ms.");
                yield break;
            }

            var total = data.GetArrayLength();
            _log?.Invoke($"Vimeo search '{query}' returned {total} result(s) in {sw.ElapsedMilliseconds}ms (single API call).");
            foreach (var v in data.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var video = MapVideo(v);
                if (video is not null) yield return video;
            }
        }
    }

    // ── Public API: categories + single video ────────────────────────────────────

    /// <summary>Fetches Vimeo's top-level curated categories (Animation, Music, …).</summary>
    public async Task<IReadOnlyList<VimeoCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var path = $"/categories?per_page=100&fields={Uri.EscapeDataString(CategoryFields)}";
        try
        {
            using var req = Build(HttpMethod.Get, path);
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"Vimeo categories failed {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms: {Trim(text, 200)}");
                return [];
            }
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<VimeoCategory>();
            foreach (var c in data.EnumerateArray())
            {
                var uri = c.TryGetProperty("uri", out var u) ? u.GetString() : null;
                var key = uri?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(uri))
                    list.Add(new VimeoCategory(key, name ?? key, uri));
            }
            _log?.Invoke($"Vimeo categories: {list.Count} in {sw.ElapsedMilliseconds}ms.");
            return list;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Vimeo categories threw after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return [];
        }
    }

    /// <summary>Fetches a single video by id, for reconstructing a favorite not seen this session.</summary>
    public async Task<VimeoVideo?> GetVideoAsync(string id, CancellationToken ct = default)
    {
        var path = $"/videos/{Uri.EscapeDataString(id)}?fields={Uri.EscapeDataString(VideoFields)}";
        try
        {
            using var req = Build(HttpMethod.Get, path);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return MapVideo(doc.RootElement);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Vimeo get video '{id}' threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches one page of videos under an API collection path (e.g. <c>/categories/music</c> or
    /// <c>/channels/staffpicks</c>), returning the items plus Vimeo's reported total for paging.
    /// Vimeo pages are 1-based.
    /// </summary>
    public async Task<VimeoVideoPage> GetVideosPageAsync(
        string collectionUri, int page, int perPage, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sep = collectionUri.Contains('?') ? "&" : "?";
        var path = $"{collectionUri}/videos{sep}page={Math.Max(1, page)}&per_page={Math.Clamp(perPage, 1, 100)}" +
                   $"&fields={Uri.EscapeDataString(VideoFields)},total";
        try
        {
            using var req = Build(HttpMethod.Get, path);
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"Vimeo page '{collectionUri}' failed {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms: {Trim(text, 200)}");
                return new VimeoVideoPage([], 0);
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
            var items = new List<VimeoVideo>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in data.EnumerateArray())
                {
                    var video = MapVideo(v);
                    if (video is not null) items.Add(video);
                }
            }
            _log?.Invoke($"Vimeo page '{collectionUri}' p{page}: {items.Count}/{total} in {sw.ElapsedMilliseconds}ms.");
            return new VimeoVideoPage(items, total);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Vimeo page '{collectionUri}' threw after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return new VimeoVideoPage([], 0);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private HttpRequestMessage Build(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", _accessToken);
        req.Headers.Accept.ParseAdd(ApiVersion);
        return req;
    }

    private static VimeoVideo? MapVideo(JsonElement v)
    {
        // uri is "/videos/76979871" — the trailing segment is the id.
        var uri = v.TryGetProperty("uri", out var u) ? u.GetString() : null;
        var id = uri?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(id)) return null;

        var name = v.TryGetProperty("name", out var n) ? n.GetString() ?? $"Vimeo {id}" : $"Vimeo {id}";
        var link = v.TryGetProperty("link", out var l) ? l.GetString() : null;
        var url = string.IsNullOrEmpty(link) ? $"https://vimeo.com/{id}" : link;

        TimeSpan? duration = v.TryGetProperty("duration", out var d) && d.TryGetInt32(out var secs)
            ? TimeSpan.FromSeconds(secs) : null;

        return new VimeoVideo(id, name, url, duration, PickThumbnail(v));
    }

    // pictures.sizes is an ascending-by-width array; pick a mid/large size for the tile.
    private static string? PickThumbnail(JsonElement v)
    {
        if (!v.TryGetProperty("pictures", out var pics) ||
            !pics.TryGetProperty("sizes", out var sizes) ||
            sizes.ValueKind != JsonValueKind.Array || sizes.GetArrayLength() == 0)
            return null;
        // Prefer a ~640px-wide size, else the largest available (last element).
        JsonElement chosen = default;
        bool found = false;
        foreach (var s in sizes.EnumerateArray())
        {
            chosen = s;
            found = true;
            if (s.TryGetProperty("width", out var w) && w.TryGetInt32(out var width) && width >= 640)
                break;
        }
        return found && chosen.TryGetProperty("link", out var link) ? link.GetString() : null;
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
