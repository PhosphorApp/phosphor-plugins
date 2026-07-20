namespace Phosphor.Plugins.IHeartRadio;

/// <summary>
/// A live iHeartRadio genre (e.g. "Classic Rock", "Country") from
/// <c>/api/v2/content/liveStationGenres</c>. <paramref name="Id"/> is the genre id used to filter
/// stations via <c>/api/v2/content/liveStations?genreId={Id}</c>.
/// </summary>
public sealed record IHeartGenre(int Id, string Name, int Count);

/// <summary>
/// A live iHeartRadio station. Carries the resolved, non-DRM stream URL inline (the listing
/// endpoints embed the <c>streams</c> object), so no extra resolve fetch is needed. The URL is the
/// best playable format the station offers — HLS when available, otherwise Shoutcast/PLS (all of
/// which LibVLC plays directly).
/// </summary>
public sealed record IHeartStation(
    string Id,
    string Name,
    string? Description,
    string? LogoUrl,
    string? StreamUrl);

/// <summary>
/// An iHeartRadio podcast category (e.g. "Comedy", "Crime") from <c>/api/v3/podcast/categories</c>.
/// <paramref name="Id"/> lists its podcasts via <c>/api/v3/podcast/categories/{Id}</c>.
/// </summary>
public sealed record IHeartPodcastCategory(int Id, string Name);

/// <summary>An iHeartRadio podcast (show) — a container of episodes.</summary>
public sealed record IHeartPodcast(
    string Id,
    string Title,
    string? Description,
    string? ImageUrl);

/// <summary>
/// A single podcast episode. Unlike live stations these are <b>finite, seekable</b> tracks with a
/// real <see cref="Duration"/>. <see cref="MediaUrl"/> is a direct, non-DRM MP3 (resolved from
/// <c>/api/v3/podcast/episodes/{Id}</c>) that LibVLC plays directly.
/// <para>
/// <see cref="HasVideo"/> reflects the episode's <c>mimeTypes</c> array: a show flagged as a "video
/// podcast" advertises <c>video/mp4</c> alongside <c>audio/mpeg</c>. This is a <b>per-episode</b>
/// signal — even within a video show some episodes are audio-only. <b>Note:</b> the public
/// key-less API still only hands back the audio <c>mediaUrl</c> on resolve, so this flag is currently
/// informational (grounding for future video-playback work), not yet a playable video source.
/// </para>
/// </summary>
public sealed record IHeartEpisode(
    string Id,
    string Title,
    string? Description,
    string? ImageUrl,
    TimeSpan? Duration,
    string? MediaUrl,
    bool HasVideo = false);

/// <summary>What kind of thing a favorite is, so it rebuilds/plays correctly.</summary>
public enum IHeartFavoriteKind
{
    /// <summary>A live radio station (played as an <c>IsLiveStream</c>).</summary>
    Station,

    /// <summary>A finite, seekable podcast episode (played from its <c>mediaUrl</c>).</summary>
    Episode,

    /// <summary>A podcast show — a drill-in container of episodes (not directly playable).</summary>
    Podcast,
}

/// <summary>
/// A persisted favorite. iHeart favorites span three shapes (live stations, podcast episodes, podcast
/// shows) so a single record carries the <see cref="Kind"/> plus enough display data to rebuild a
/// playable/browsable <c>SourceItem</c> without a re-fetch.
/// </summary>
public sealed record IHeartFavorite(
    string Id,
    IHeartFavoriteKind Kind,
    string Title,
    string? Subtitle,
    string? ThumbnailUrl,
    double? DurationSeconds,
    string? StreamUrl);
