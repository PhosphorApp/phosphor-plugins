using System.Text.Json;

namespace Phosphor.Plugins.IHeartRadio;

/// <summary>
/// Minimal iHeartRadio REST client: enumerates live-station genres, lists/searches stations, and
/// surfaces each station's raw, NON-DRM HLS master URL. Pure <see cref="HttpClient"/> — no auth, no
/// browser, no external tools. Endpoints from <c>api.iheart.com</c> (see tools/IHeartRadioSpike and
/// https://github.com/api-evangelist/iheart-radio); all public catalog endpoints are key-less.
/// </summary>
public sealed class IHeartClient
{
    private const string ApiBase = "https://api.iheart.com";

    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public IHeartClient(HttpClient http, Action<string>? log = null)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Fetches the live-station genre taxonomy (id + name + station count).</summary>
    public async Task<IReadOnlyList<IHeartGenre>> GetGenresAsync(CancellationToken ct = default)
    {
        var root = await GetAsync("/api/v2/content/liveStationGenres", ct);
        var genres = new List<IHeartGenre>();
        if (root is { } r && r.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in hits.EnumerateArray())
            {
                var id = g.TryGetProperty("id", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
                var name = g.TryGetProperty("name", out var n) ? n.GetString() : null;
                var count = g.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                if (id > 0 && !string.IsNullOrWhiteSpace(name))
                    genres.Add(new IHeartGenre(id, name!, count));
            }
        }
        return genres;
    }

    /// <summary>Lists live stations, optionally filtered by <paramref name="genreId"/>.</summary>
    public async Task<IReadOnlyList<IHeartStation>> GetStationsAsync(
        int? genreId = null, int limit = 100, CancellationToken ct = default)
    {
        var path = genreId is > 0
            ? $"/api/v2/content/liveStations?genreId={genreId}&limit={limit}"
            : $"/api/v2/content/liveStations?limit={limit}";
        var root = await GetAsync(path, ct);
        return ParseStations(root);
    }

    /// <summary>Free-text station search via the key-less catalog endpoint.</summary>
    public async Task<IReadOnlyList<IHeartStation>> SearchStationsAsync(
        string query, int limit = 50, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return [];
        var path = $"/api/v1/catalog/searchAll?keywords={Uri.EscapeDataString(q)}" +
                   $"&maxRows={limit}&bundle=false&startIndex=0";
        var root = await GetAsync(path, ct);
        var stations = new List<IHeartStation>();
        if (root is { } r && r.TryGetProperty("stations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            // searchAll stations carry only ids/names — fetch the stream URL lazily at resolve time.
            foreach (var s in arr.EnumerateArray())
            {
                var id = IdOf(s);
                var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
                var desc = s.TryGetProperty("description", out var d) ? d.GetString() : null;
                var logo = s.TryGetProperty("logo", out var l) ? l.GetString()
                    : s.TryGetProperty("newlogo", out var nl) ? nl.GetString() : null;
                if (id is not null && !string.IsNullOrWhiteSpace(name))
                    stations.Add(new IHeartStation(id, name!, desc, logo, StreamFrom(s)));
            }
        }
        return stations;
    }

    /// <summary>
    /// Resolves a single station to its raw HLS URL. Used when an item's inline URL is missing
    /// (e.g. search results) or stale.
    /// </summary>
    public async Task<string?> GetStreamUrlAsync(string stationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stationId)) return null;
        var root = await GetAsync($"/api/v2/content/liveStations/{Uri.EscapeDataString(stationId)}", ct);
        var stations = ParseStations(root);
        return stations.Count > 0 ? stations[0].StreamUrl : null;
    }

    // ── Podcasts (on-demand) ─────────────────────────────────────────────────────

    /// <summary>Fetches the podcast category taxonomy (id + name).</summary>
    public async Task<IReadOnlyList<IHeartPodcastCategory>> GetPodcastCategoriesAsync(CancellationToken ct = default)
    {
        var root = await GetAsync("/api/v3/podcast/categories", ct);
        var categories = new List<IHeartPodcastCategory>();
        if (root is { } r && r.TryGetProperty("categories", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
            {
                var id = c.TryGetProperty("id", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
                var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (id > 0 && !string.IsNullOrWhiteSpace(name))
                    categories.Add(new IHeartPodcastCategory(id, name!));
            }
        }
        return categories;
    }

    /// <summary>Lists the podcasts within a category (returned inline by the category detail).</summary>
    public async Task<IReadOnlyList<IHeartPodcast>> GetPodcastsInCategoryAsync(
        int categoryId, CancellationToken ct = default)
    {
        var root = await GetAsync($"/api/v3/podcast/categories/{categoryId}", ct);
        return root is { } r ? ParsePodcasts(r, "podcasts") : [];
    }

    /// <summary>Free-text podcast search. Requires the <c>podcast=true</c> flag on searchAll.</summary>
    public async Task<IReadOnlyList<IHeartPodcast>> SearchPodcastsAsync(
        string query, int limit = 20, CancellationToken ct = default)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return [];
        var root = await GetAsync(
            $"/api/v3/search/all?keywords={Uri.EscapeDataString(q)}&maxRows={limit}&podcast=true", ct);
        if (root is { } r && r.TryGetProperty("results", out var results))
            return ParsePodcasts(results, "podcasts");
        return [];
    }

    /// <summary>
    /// Fetches one page of a podcast's episodes. iHeart pages episodes with an opaque <b>cursor</b>
    /// (<c>links.next</c>), not an offset — pass the previously returned <paramref name="cursor"/> to
    /// get the next page. Returns the episodes plus the next cursor (<c>null</c> when no more remain).
    /// Media URLs are resolved lazily per episode via <see cref="GetEpisodeMediaUrlAsync"/>.
    /// </summary>
    public async Task<(IReadOnlyList<IHeartEpisode> Episodes, string? NextCursor)> GetEpisodePageAsync(
        string podcastId, int limit, string? cursor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(podcastId)) return ([], null);
        var path = $"/api/v3/podcast/podcasts/{Uri.EscapeDataString(podcastId)}/episodes?limit={limit}";
        if (!string.IsNullOrWhiteSpace(cursor))
            path += $"&pageKey={Uri.EscapeDataString(cursor)}";
        var root = await GetAsync(path, ct);
        var episodes = new List<IHeartEpisode>();
        string? next = null;
        if (root is { } r)
        {
            if (r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var e in data.EnumerateArray())
                    if (ParseEpisode(e, mediaUrl: null) is { } ep)
                        episodes.Add(ep);
            if (r.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
                && links.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String)
                next = n.GetString();
        }
        return (episodes, string.IsNullOrWhiteSpace(next) ? null : next);
    }

    /// <summary>Resolves an episode's direct, non-DRM <c>mediaUrl</c> MP3 for playback.</summary>
    public async Task<string?> GetEpisodeMediaUrlAsync(string episodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(episodeId)) return null;
        var root = await GetAsync($"/api/v3/podcast/episodes/{Uri.EscapeDataString(episodeId)}", ct);
        if (root is { } r && r.TryGetProperty("episode", out var episode)
            && episode.TryGetProperty("mediaUrl", out var mu) && mu.ValueKind == JsonValueKind.String)
        {
            var url = mu.GetString();
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        return null;
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────

    private static IReadOnlyList<IHeartStation> ParseStations(JsonElement? root)
    {
        var stations = new List<IHeartStation>();
        if (root is not { } r) return stations;
        if (!r.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return stations;
        foreach (var s in hits.EnumerateArray())
        {
            var id = IdOf(s);
            var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (id is null || string.IsNullOrWhiteSpace(name)) continue;
            var desc = s.TryGetProperty("description", out var d) ? d.GetString() : null;
            var logo = s.TryGetProperty("logo", out var l) ? l.GetString() : null;
            stations.Add(new IHeartStation(id, name!, desc, logo, StreamFrom(s)));
        }
        return stations;
    }

    // Pick the best playable stream URL from a station's "streams" object. Not every station offers
    // HLS — many are Shoutcast-only (or PLS), so we fall back through the formats LibVLC can play.
    // Within each format we prefer the secure (https) variant. Order: HLS → Shoutcast → PLS.
    private static readonly string[] StreamKeysInPreference =
    [
        "secure_hls_stream", "hls_stream",
        "secure_shoutcast_stream", "shoutcast_stream",
        "secure_pls_stream", "pls_stream",
    ];

    private static string? StreamFrom(JsonElement station)
    {
        if (!station.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in StreamKeysInPreference)
        {
            if (streams.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var url = v.GetString();
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }
        }
        return null;
    }

    private static string? IdOf(JsonElement el)
    {
        if (!el.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    // Parse a podcast array (a category detail's "podcasts", or search results' "podcasts").
    private static IReadOnlyList<IHeartPodcast> ParsePodcasts(JsonElement parent, string arrayName)
    {
        var podcasts = new List<IHeartPodcast>();
        if (!parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return podcasts;
        foreach (var p in arr.EnumerateArray())
        {
            var id = IdOf(p);
            var title = p.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (id is null || string.IsNullOrWhiteSpace(title)) continue;
            var desc = p.TryGetProperty("description", out var d) ? d.GetString() : null;
            var image = p.TryGetProperty("imageUrl", out var im) ? im.GetString()
                : p.TryGetProperty("image", out var im2) ? im2.GetString() : null;
            podcasts.Add(new IHeartPodcast(id, title!, desc, image));
        }
        return podcasts;
    }

    // Parse a single episode element; mediaUrl is supplied separately (the list endpoint omits it).
    private static IHeartEpisode? ParseEpisode(JsonElement e, string? mediaUrl)
    {
        var id = IdOf(e);
        var title = e.TryGetProperty("title", out var t) ? t.GetString() : null;
        if (id is null || string.IsNullOrWhiteSpace(title)) return null;
        var desc = e.TryGetProperty("description", out var d) ? d.GetString() : null;
        var image = e.TryGetProperty("imageUrl", out var im) ? im.GetString() : null;
        TimeSpan? duration = e.TryGetProperty("duration", out var du) && du.TryGetInt32(out var secs) && secs > 0
            ? TimeSpan.FromSeconds(secs) : null;
        return new IHeartEpisode(id, title!, desc, image, duration, mediaUrl);
    }

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"iHeart: HTTP {(int)resp.StatusCode} for {path}");
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"iHeart: request '{path}' failed: {ex.Message}");
            return null;
        }
    }
}
