using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.HdHomeRun;

/// <summary>
/// Phase 2 companion to <see cref="HdhrApiClient"/>: pulls channel artwork <em>and</em> program guide
/// data from the SiliconDust cloud guide service, keyed by the tuner's rotating <c>DeviceAuth</c>
/// token (read fresh from <c>/discover.json</c> each time — it is only valid for 16–24 hours).
/// </summary>
/// <remarks>
/// The guide service returns, per channel, an <c>ImageURL</c> (the channel icon) and a <c>Guide</c>
/// array of upcoming programs (start/end unix time + title). SiliconDust gives every owner ~2 days of
/// program data — more than we need. We fetch it best-effort, cache it (icons + programs) for ~24h,
/// and compute the <em>current</em> program at display time (see <see cref="HdhrGuide.CurrentProgram"/>).
/// The exact JSON shape is only lightly documented, so parsing is deliberately tolerant: any missing
/// field simply yields less enrichment, never an error.
/// </remarks>
internal sealed class HdhrGuideClient
{
    // The device-guide endpoint returns each channel plus its ImageURL and a Guide[] of programs.
    private const string GuideApiBase = "https://api.hdhomerun.com/api/guide.php";

    private readonly HttpClient _http;
    private readonly Action<LogLevel, string> _log;

    public HdhrGuideClient(HttpClient http, Action<LogLevel, string> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Fetches the full guide (channel icons + program schedule) for the given
    /// <paramref name="deviceAuth"/> token. Returns a map of guide number (e.g. "5.1") →
    /// <see cref="HdhrGuide"/>. Best-effort: returns an empty map on any failure so the
    /// (already-usable) local lineup is never blocked by the cloud call.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, HdhrGuide>> GetGuideAsync(
        string deviceAuth, CancellationToken ct)
    {
        var result = new Dictionary<string, HdhrGuide>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(deviceAuth)) return result;

        var url = $"{GuideApiBase}?DeviceAuth={Uri.EscapeDataString(deviceAuth)}";
        try
        {
            await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            var channels = await JsonSerializer.DeserializeAsync<List<GuideChannelDto>>(stream, JsonOpts, ct)
                .ConfigureAwait(false) ?? [];

            var programCount = 0;
            foreach (var ch in channels)
            {
                if (string.IsNullOrWhiteSpace(ch.GuideNumber)) continue;

                var programs = (ch.Guide ?? [])
                    .Where(p => p.StartTime > 0 && !string.IsNullOrWhiteSpace(p.Title))
                    .Select(p => new HdhrProgram(
                        Title: p.Title!.Trim(),
                        EpisodeTitle: string.IsNullOrWhiteSpace(p.EpisodeTitle) ? null : p.EpisodeTitle!.Trim(),
                        StartUtc: DateTimeOffset.FromUnixTimeSeconds(p.StartTime),
                        // Some feeds omit EndTime; leave it null and infer the boundary from the next
                        // program at lookup time.
                        EndUtc: p.EndTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(p.EndTime) : null))
                    .OrderBy(p => p.StartUtc)
                    .ToList();

                programCount += programs.Count;
                result[ch.GuideNumber!] = new HdhrGuide(
                    GuideNumber: ch.GuideNumber!,
                    IconUrl: string.IsNullOrWhiteSpace(ch.ImageURL) ? null : ch.ImageURL,
                    Programs: programs);
            }

            _log(LogLevel.Info,
                $"HDHomeRun: fetched guide for {result.Count} channels ({programCount} programs) from the guide service.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(LogLevel.Warning, $"HDHomeRun: guide fetch failed: {ex.Message}");
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class GuideChannelDto
    {
        [JsonPropertyName("GuideNumber")] public string? GuideNumber { get; set; }
        [JsonPropertyName("GuideName")] public string? GuideName { get; set; }
        [JsonPropertyName("ImageURL")] public string? ImageURL { get; set; }
        [JsonPropertyName("Guide")] public List<GuideProgramDto>? Guide { get; set; }
    }

    private sealed class GuideProgramDto
    {
        [JsonPropertyName("Title")] public string? Title { get; set; }
        [JsonPropertyName("EpisodeTitle")] public string? EpisodeTitle { get; set; }
        // Unix epoch seconds (UTC). Some feeds omit EndTime.
        [JsonPropertyName("StartTime")] public long StartTime { get; set; }
        [JsonPropertyName("EndTime")] public long EndTime { get; set; }
    }
}

/// <summary>One scheduled program from the guide (times are UTC).</summary>
internal sealed record HdhrProgram(
    string Title,
    string? EpisodeTitle,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc);

/// <summary>
/// The cloud-guide data for one channel: its icon plus an ordered (by start time) list of programs.
/// Programs are cached for ~24h; the <em>current</em> one is computed on demand against the clock.
/// </summary>
internal sealed record HdhrGuide(
    string GuideNumber,
    string? IconUrl,
    IReadOnlyList<HdhrProgram> Programs)
{
    /// <summary>
    /// The program airing at <paramref name="nowUtc"/>, or <c>null</c> when nothing matches (empty
    /// schedule, or the cached window no longer covers "now"). When a program has no explicit end
    /// time, its boundary is inferred from the next program's start.
    /// </summary>
    public HdhrProgram? CurrentProgram(DateTimeOffset nowUtc)
    {
        for (var i = 0; i < Programs.Count; i++)
        {
            var p = Programs[i];
            if (nowUtc < p.StartUtc) continue;

            var end = p.EndUtc
                ?? (i + 1 < Programs.Count ? Programs[i + 1].StartUtc : (DateTimeOffset?)null);
            if (end is null || nowUtc < end.Value)
                return p;
        }
        return null;
    }
}
