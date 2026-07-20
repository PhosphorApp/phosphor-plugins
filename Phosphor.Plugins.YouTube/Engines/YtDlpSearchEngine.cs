using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Video;

namespace Phosphor.Search;

/// <summary>
/// <see cref="ISearchEngine"/> backed by the external <c>yt-dlp.exe</c>. Search, playlist,
/// and channel enumeration all run <c>--flat-playlist --dump-json</c> and stream the JSONL
/// output line-by-line, mapping each entry to a <see cref="VideoItem"/> so results appear
/// incrementally (preserving the app's live pagination UX).
/// </summary>
/// <remarks>
/// Flat-playlist mode exposes <c>id</c>, <c>title</c>, <c>uploader</c>, <c>duration</c>, and
/// <c>thumbnails</c> but not <c>upload_date</c>/<c>description</c> (those need a full
/// per-video resolve, done lazily via the video engine's metadata call on play).
/// </remarks>
public sealed class YtDlpSearchEngine : ISearchEngine
{
    private readonly string _ytDlpPath;

    public YtDlpSearchEngine(string? ytDlpPath = null)
    {
        _ytDlpPath = ytDlpPath ?? YtDlpVideoEngine.ResolveYtDlpPath();
    }

    /// <summary>Available only when the yt-dlp executable is present.</summary>
    public bool IsAvailable => File.Exists(_ytDlpPath);

    public IAsyncEnumerable<VideoItem> SearchVideosAsync(string query, CancellationToken ct = default)
        // ytsearchN: returns up to N results; a large N lets the VM page via its own take-count.
        => EnumerateAsync($"ytsearch{MaxSearchResults}:{query}", ct);

    public IAsyncEnumerable<VideoItem> GetPlaylistVideosAsync(string playlistId, CancellationToken ct = default)
        => EnumerateAsync($"https://www.youtube.com/playlist?list={playlistId}", ct);

    public IAsyncEnumerable<VideoItem> GetChannelUploadsAsync(string handleOrUser, CancellationToken ct = default)
        => EnumerateAsync(ToChannelVideosUrl(handleOrUser), ct);

    public async Task<string?> ResolvePlaylistIdAsync(
        string nameIdOrUrl,
        Action<string>? onFoundByName = null,
        CancellationToken ct = default)
    {
        // A raw playlist id or URL — use as-is (yt-dlp resolves the URL form).
        if (LooksLikePlaylistId(nameIdOrUrl))
            return ExtractPlaylistId(nameIdOrUrl);

        // Otherwise search for the playlist by name. yt-dlp has no first-class
        // playlist search (its ytsearch: prefix only returns videos), so query
        // YouTube's results page with the "playlists" filter (sp=EgIQAw) and take
        // the first playlist entry. Flat-playlist entries for this URL are of
        // _type=url with ie_key=YoutubeTab and a playlist id in the id field.
        var searchUrl =
            $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(nameIdOrUrl)}&sp=EgIQAw";
        var (code, stdout, _) = await YtDlpVideoEngine.RunYtDlpAsync(
            _ytDlpPath,
            new[] { "--no-warnings", "--flat-playlist", "--dump-json", "--playlist-items", "1",
                    searchUrl },
            ct);

        if (code != 0) return null;
        var line = FirstNonEmptyLine(stdout);
        if (line == null) return null;

        try
        {
            var dto = JsonSerializer.Deserialize<YtDlpEntryJson>(line);
            // Only accept a genuine playlist entry (YoutubeTab), and only when the
            // id looks like a playlist id (not a stray video result).
            if (dto?.Id != null
                && string.Equals(dto.IeKey, "YoutubeTab", StringComparison.OrdinalIgnoreCase)
                && LooksLikePlaylistId(dto.Id))
            {
                onFoundByName?.Invoke(dto.Title ?? nameIdOrUrl);
                return dto.Id;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ── enumeration ──

    private const int MaxSearchResults = 200;

    private async IAsyncEnumerable<VideoItem> EnumerateAsync(
        string target, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var args = new[] { "--no-warnings", "--flat-playlist", "--lazy-playlist", "--dump-json", target };

        await foreach (var line in YtDlpVideoEngine.RunYtDlpStreamingAsync(_ytDlpPath, args, ct))
        {
            VideoItem? item = null;
            try
            {
                var dto = JsonSerializer.Deserialize<YtDlpEntryJson>(line);
                if (dto?.Id != null)
                    item = MapEntry(dto);
            }
            catch
            {
                // Skip malformed lines (progress noise, partial writes).
            }

            if (item != null)
                yield return item;
        }
    }

    private static VideoItem MapEntry(YtDlpEntryJson e) => new()
    {
        Title = e.Title ?? "",
        Author = e.Uploader ?? e.Channel ?? "",
        ThumbnailUrl = BestThumbnail(e.Thumbnails, e.Id!),
        VideoId = e.Id!,
        Duration = e.Duration is > 0 ? TimeSpan.FromSeconds(e.Duration.Value) : null,
    };

    private static string BestThumbnail(List<YtDlpThumbJson>? thumbs, string videoId)
    {
        // Thumbnails come sorted ascending by size; take the largest, else a standard fallback.
        var url = thumbs is { Count: > 0 } ? thumbs[^1].Url : null;
        return !string.IsNullOrEmpty(url)
            ? url!
            : $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
    }

    // ── helpers ──

    private static string ToChannelVideosUrl(string handleOrUser)
    {
        if (handleOrUser.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return handleOrUser;
        var handle = handleOrUser.StartsWith('@') ? handleOrUser : "@" + handleOrUser;
        return $"https://www.youtube.com/{handle}/videos";
    }

    private static bool LooksLikePlaylistId(string s)
        => s.StartsWith("PL", StringComparison.Ordinal)
           || s.StartsWith("UU", StringComparison.Ordinal)
           || s.StartsWith("OL", StringComparison.Ordinal)
           || s.Contains("list=", StringComparison.OrdinalIgnoreCase);

    private static string ExtractPlaylistId(string idOrUrl)
    {
        var idx = idOrUrl.IndexOf("list=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return idOrUrl;
        var rest = idOrUrl[(idx + 5)..];
        var amp = rest.IndexOf('&');
        return amp < 0 ? rest : rest[..amp];
    }

    private static string? FirstNonEmptyLine(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    // ── JSON shapes (subset of yt-dlp --flat-playlist --dump-json entries) ──

    private sealed class YtDlpEntryJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("uploader")] public string? Uploader { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("thumbnails")] public List<YtDlpThumbJson>? Thumbnails { get; set; }
        [JsonPropertyName("ie_key")] public string? IeKey { get; set; }
    }

    private sealed class YtDlpThumbJson
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
    }
}
