using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Phosphor.Plugins.Dailymotion;

/// <summary>
/// A lightweight Dailymotion video as returned by the API. <see cref="Url"/> is the canonical
/// dailymotion.com link the yt-dlp resolver plays.
/// </summary>
public sealed record DmVideo(
    string Id,
    string Title,
    string Url,
    TimeSpan? Duration,
    string? ThumbnailUrl);

/// <summary>A Dailymotion editorial category (its <c>/channels</c> — Music, Movies, Gaming, …).</summary>
public sealed record DmCategory(string Id, string Name);

/// <summary>One page of a video listing: items plus whether more pages remain (Dailymotion is page-based).</summary>
public sealed record DmVideoPage(IReadOnlyList<DmVideo> Items, bool HasMore, int Total);

/// <summary>
/// Pure-<see cref="HttpClient"/> Dailymotion REST client. The public API
/// (<c>api.dailymotion.com</c>) allows search, editorial categories, and paged listings
/// <em>unauthenticated</em> (spike-proven) — no OAuth, no key, no token. So there is no credential
/// state here at all. No UI, no threading assumptions; mirrors the Vimeo client shape.
/// </summary>
public sealed class DailymotionClient(HttpClient http, Action<string>? log = null)
{
    private const string ApiBase = "https://api.dailymotion.com";
    // Ask only for the fields we map, to keep responses small. thumbnail_360_url is a good tile size.
    private const string VideoFields = "id,title,duration,thumbnail_360_url,url";

    private readonly HttpClient _http = http;
    private readonly Action<string>? _log = log;

    /// <summary>Lightweight reachability + shape check (a 1-result search).</summary>
    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ApiBase}/videos?limit=1&fields=id", ct);
            if (resp.IsSuccessStatusCode) return (true, "Reachable.");
            return (false, $"Unexpected response: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Fetches Dailymotion's editorial categories (its <c>/channels</c>).</summary>
    public async Task<IReadOnlyList<DmCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync($"{ApiBase}/channels?fields=id,name&limit=100", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"Dailymotion categories failed {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms: {Trim(text, 200)}");
                return [];
            }
            using var doc = JsonDocument.Parse(text);
            var list = new List<DmCategory>();
            if (doc.RootElement.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var id = c.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrEmpty(id))
                        list.Add(new DmCategory(id, name ?? id));
                }
            }
            _log?.Invoke($"Dailymotion categories: {list.Count} in {sw.ElapsedMilliseconds}ms.");
            return list;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Dailymotion categories threw after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return [];
        }
    }

    /// <summary>Searches public videos for <paramref name="query"/> (first page).</summary>
    public async IAsyncEnumerable<DmVideo> SearchAsync(
        string query, int limit = 50, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var page = await GetVideosPageAsync(
            $"/videos?search={Uri.EscapeDataString(query)}", 1, limit, ct);
        foreach (var v in page.Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return v;
        }
    }

    /// <summary>Fetches one page of videos in a category (channel), with paging info.</summary>
    public Task<DmVideoPage> GetCategoryVideosPageAsync(
        string categoryId, int page, int limit, CancellationToken ct = default)
        => GetVideosPageAsync($"/channel/{Uri.EscapeDataString(categoryId)}/videos", page, limit, ct);

    /// <summary>Fetches a single video by id, for reconstructing a favorite not seen this session.</summary>
    public async Task<DmVideo?> GetVideoAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"{ApiBase}/video/{Uri.EscapeDataString(id)}?fields={VideoFields}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return MapVideo(doc.RootElement);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Dailymotion get video '{id}' threw: {ex.Message}");
            return null;
        }
    }

    // ── Core paged fetch ─────────────────────────────────────────────────────────

    private async Task<DmVideoPage> GetVideosPageAsync(
        string basePath, int page, int limit, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sep = basePath.Contains('?') ? "&" : "?";
        var path = $"{basePath}{sep}page={Math.Max(1, page)}&limit={Math.Clamp(limit, 1, 100)}" +
                   $"&fields={VideoFields}";
        try
        {
            using var resp = await _http.GetAsync(ApiBase + path, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"Dailymotion page '{basePath}' failed {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms: {Trim(text, 200)}");
                return new DmVideoPage([], false, 0);
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var hasMore = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
            var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
            var items = new List<DmVideo>();
            if (root.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arr.EnumerateArray())
                {
                    var video = MapVideo(v);
                    if (video is not null) items.Add(video);
                }
            }
            _log?.Invoke($"Dailymotion page '{basePath}' p{page}: {items.Count} (has_more={hasMore}, total={total}) in {sw.ElapsedMilliseconds}ms.");
            return new DmVideoPage(items, hasMore, total);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Dailymotion page '{basePath}' threw after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return new DmVideoPage([], false, 0);
        }
    }

    private static DmVideo? MapVideo(JsonElement v)
    {
        var id = v.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;

        var title = v.TryGetProperty("title", out var t) ? t.GetString() ?? $"Dailymotion {id}" : $"Dailymotion {id}";
        var url = v.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } link
            ? link
            : $"https://www.dailymotion.com/video/{id}";
        TimeSpan? duration = v.TryGetProperty("duration", out var d) && d.TryGetInt32(out var secs) && secs > 0
            ? TimeSpan.FromSeconds(secs) : null;
        var thumb = v.TryGetProperty("thumbnail_360_url", out var th) ? th.GetString() : null;

        return new DmVideo(id, title, url, duration, thumb);
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
