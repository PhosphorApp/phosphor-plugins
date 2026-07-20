using Phosphor.Plugin.Abstractions;
using Phosphor.Video;
using PluginChapterMarker = Phosphor.Plugin.Abstractions.ChapterMarker;

namespace Phosphor.Plugins.YouTube;

/// <summary>
/// Adapts the host's existing YouTube engine types (<see cref="VideoItem"/>,
/// <see cref="VideoStreams"/>, <see cref="VideoDownload"/>, <see cref="VideoMetadata"/>)
/// to the plug-in abstraction types. This is pure, behavior-preserving translation — no
/// YouTube logic lives here, it only relocates/shapes data that already flows through the
/// app so the in-box source presents the same information via the plug-in contract.
/// </summary>
internal static class YouTubeMappings
{
    // ── Discovery ──────────────────────────────────────────────────────────────

    /// <summary>Maps a host <see cref="VideoItem"/> to a plug-in <see cref="SourceItem"/>.</summary>
    public static SourceItem ToSourceItem(VideoItem v, string instanceId) => new()
    {
        SourceInstanceId = instanceId,
        ItemId = v.VideoId,
        Title = v.Title,
        Subtitle = string.IsNullOrEmpty(v.Author) ? null : v.Author,
        ThumbnailUrl = v.ThumbnailUrl,
        Duration = v.Duration,
        PublishedAt = v.UploadDate,
        // Carry the raw YouTube id so resolve/download/metadata never re-parse.
        SourceState = v.VideoId,
    };

    /// <summary>
    /// Extracts the YouTube video id from a <see cref="SourceItem"/>, preferring the
    /// <see cref="SourceItem.SourceState"/> payload and falling back to the item id.
    /// </summary>
    public static string VideoIdOf(SourceItem item) =>
        item.SourceState as string ?? item.ItemId;

    // ── Playback / streams ─────────────────────────────────────────────────────

    /// <summary>Maps a resolved <see cref="VideoStreams"/> to a plug-in <see cref="ResolvedStream"/>.</summary>
    public static ResolvedStream ToResolvedStream(VideoStreams s)
    {
        var layout = s.Kind switch
        {
            VideoStreamKind.SeparateVideoAudio => StreamLayout.SeparateVideoAudio,
            VideoStreamKind.Muxed => StreamLayout.Muxed,
            _ => StreamLayout.AudioOnly,
        };

        // YouTube always resolves to short-lived HTTP(S) URLs.
        return new ResolvedStream(
            StreamTransport.Http,
            layout,
            s.PrimaryUrl,
            s.AudioSlaveUrl,
            string.IsNullOrEmpty(s.Resolution) ? null : s.Resolution);
    }

    // ── Download ───────────────────────────────────────────────────────────────

    /// <summary>Maps a host <see cref="VideoDownload"/> to a plug-in <see cref="SourceDownload"/>.</summary>
    public static SourceDownload ToSourceDownload(VideoDownload d) => new()
    {
        VideoFilePath = d.VideoFilePath,
        AudioFilePath = d.AudioFilePath,
        VideoContainer = d.VideoContainer,
        AudioContainer = d.AudioContainer,
        Resolution = d.Resolution,
    };

    // ── Metadata ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps host <see cref="VideoMetadata"/> to plug-in <see cref="SourceMetadata"/>. When the
    /// engine reported no native chapters, YouTube-style chapters are parsed from the description
    /// here so the host receives ready-to-use markers (the host no longer parses descriptions).
    /// </summary>
    public static SourceMetadata ToSourceMetadata(VideoMetadata m)
    {
        var chapters = m.Chapters.Count > 0
            ? m.Chapters.Select(ToPluginChapter).ToList()
            : ParseChaptersFromDescription(m.Description ?? "", m.Duration);
        return new SourceMetadata(m.Duration, m.Description, chapters, m.UploadDate);
    }

    private static PluginChapterMarker ToPluginChapter(Phosphor.ChapterMarker c) =>
        new(c.Title, c.StartTime, c.EndTime);

    /// <summary>
    /// Parses chapter markers from a YouTube video description. Looks for lines starting with
    /// timestamps like "0:00", "1:23:45", etc. Relocated from the host so YouTube-specific parsing
    /// lives with the YouTube source.
    /// </summary>
    private static List<PluginChapterMarker> ParseChaptersFromDescription(string description, TimeSpan? totalDuration)
    {
        var chapters = new List<PluginChapterMarker>();
        if (string.IsNullOrWhiteSpace(description)) return chapters;

        var regex = new System.Text.RegularExpressions.Regex(
            @"(?:^|\()\s*(\d{1,2}:\d{2}(?::\d{2})?)\s*(?:\)?\s*[-–—]?\s*)(.+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var starts = new List<TimeSpan>();
        var titles = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in regex.Matches(description))
        {
            var timeParts = match.Groups[1].Value.Split(':');
            TimeSpan ts = timeParts.Length == 3
                ? new TimeSpan(int.Parse(timeParts[0]), int.Parse(timeParts[1]), int.Parse(timeParts[2]))
                : new TimeSpan(0, int.Parse(timeParts[0]), int.Parse(timeParts[1]));
            starts.Add(ts);
            titles.Add(match.Groups[2].Value.Trim());
        }

        for (int i = 0; i < starts.Count; i++)
        {
            var end = i < starts.Count - 1 ? starts[i + 1]
                : (totalDuration ?? TimeSpan.Zero);
            chapters.Add(new PluginChapterMarker(titles[i], starts[i], end));
        }

        return chapters;
    }

    // ── Preferences ────────────────────────────────────────────────────────────

    /// <summary>Maps the plug-in quality ceiling to the host's <see cref="VideoQualityPreference"/>.</summary>
    public static VideoQualityPreference ToQualityPreference(VideoQuality q) => q switch
    {
        VideoQuality.Low => VideoQualityPreference.Low,
        VideoQuality.Medium => VideoQualityPreference.Medium,
        VideoQuality.High => VideoQualityPreference.High,
        _ => VideoQualityPreference.Max,
    };
}
