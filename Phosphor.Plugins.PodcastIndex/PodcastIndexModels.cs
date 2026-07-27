namespace Phosphor.Plugins.PodcastIndex;

/// <summary>
/// A podcast feed (show) from the Podcast Index, e.g. from <c>/search/byterm</c>,
/// <c>/podcasts/trending</c>, or <c>/podcasts/bytag</c>. <see cref="Id"/> is the feed id used to
/// list its episodes via <c>/episodes/byfeedid?id={Id}</c>. A feed is a browse container, not a
/// directly playable item.
/// </summary>
public sealed record PiFeed(
    long Id,
    string Title,
    string? Author,
    string? Description,
    string? ImageUrl);

/// <summary>
/// A single podcast episode. Unlike live radio these are <b>finite, seekable</b> tracks with a real
/// <see cref="Duration"/>. <see cref="EnclosureUrl"/> is the direct, non-DRM media file the publisher
/// hosts (an .mp3/.m4a/.mp4) that the host's player plays directly — Podcast Index is a pure index,
/// so no extra resolve fetch is needed.
/// <para>
/// <see cref="EnclosureType"/> is the media MIME (e.g. <c>audio/mpeg</c>, <c>video/mp4</c>). Video
/// podcasts advertise a <c>video/*</c> type; those play as video, everything else is audio-only.
/// </para>
/// </summary>
public sealed record PiEpisode(
    long Id,
    string Title,
    string? Description,
    string? ImageUrl,
    TimeSpan? Duration,
    string EnclosureUrl,
    string? EnclosureType,
    DateTimeOffset? Published)
{
    /// <summary>True when the enclosure is a video rendition (<c>video/*</c> MIME).</summary>
    public bool IsVideo =>
        EnclosureType is { } t && t.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A Podcast Index category (e.g. "Comedy", "News", "Technology") from <c>/categories/list</c>.
/// <see cref="Id"/> lists its trending feeds via <c>/podcasts/trending?cat={Id}</c>.
/// </summary>
public sealed record PiCategory(int Id, string Name);

/// <summary>What kind of thing a favorite is, so it rebuilds/plays correctly.</summary>
public enum PiFavoriteKind
{
    /// <summary>A show/feed — a drill-in container of episodes (not directly playable).</summary>
    Feed,

    /// <summary>A finite, seekable episode (played from its <c>enclosureUrl</c>).</summary>
    Episode,
}

/// <summary>
/// A persisted favorite. PodcastIndex favorites span two shapes (shows/feeds and episodes) so a
/// single record carries the <see cref="Kind"/> plus enough display data to rebuild a
/// playable/browsable <c>SourceItem</c> without a re-fetch. For episodes the resolved
/// <see cref="EnclosureUrl"/> (and its MIME) is stored so playback needs no re-index.
/// </summary>
public sealed record PiFavorite(
    string Id,
    PiFavoriteKind Kind,
    string Title,
    string? Subtitle,
    string? ThumbnailUrl,
    double? DurationSeconds = null,
    string? EnclosureUrl = null,
    string? EnclosureType = null,
    long? PublishedUnix = null);
