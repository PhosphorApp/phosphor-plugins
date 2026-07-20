namespace Phosphor.Video;

/// <summary>
/// Abstraction over a YouTube <em>video</em> backend: resolving playable stream URLs
/// for live playback, and downloading raw streams for the disk caches. This is the
/// switch point that lets the app use either YoutubeExplode or yt-dlp for the video
/// path, independent of the search/metadata path.
/// </summary>
/// <remarks>
/// Search/discovery is a separate seam (<c>ISearchEngine</c>, added in a later phase).
/// Plex is orthogonal — it plays via <c>VideoItem.StreamUrl</c> and does not use this.
/// </remarks>
public interface IVideoEngine
{
    /// <summary>
    /// Whether this engine can actually run in the current environment. In-process engines
    /// (YoutubeExplode) are always available; external ones (yt-dlp) report false when their
    /// executable is missing, so the factory can fall back to an available engine. This is a
    /// general capability hook — future engines report their own readiness however they need.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Resolves short-lived playable stream URLs for live playback into LibVLC.
    /// URLs are typically time-limited and IP-bound, so callers must resolve fresh
    /// for each play and must not persist the result.
    /// </summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="quality">The user's quality ceiling.</param>
    /// <param name="preferStereo">When true, avoid surround audio tracks.</param>
    /// <param name="audioOnly">When true, resolve an audio-only stream (no video).</param>
    /// <returns>The resolved streams, or <c>null</c> if nothing suitable was found.</returns>
    Task<VideoStreams?> ResolveStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        bool audioOnly,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads the raw video and audio streams for a video into
    /// <paramref name="destinationDir"/>. The caller is responsible for muxing /
    /// indexing / eviction — this method only produces the raw files and reports
    /// their paths, containers, and resolution.
    /// </summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="quality">The user's quality ceiling.</param>
    /// <param name="preferStereo">When true, avoid surround audio tracks.</param>
    /// <param name="destinationDir">Directory the raw files are written into.</param>
    /// <returns>The downloaded file descriptor, or <c>null</c> if streams were unavailable.</returns>
    Task<VideoDownload?> DownloadStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        string destinationDir,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches video metadata: duration, description, and any <em>native</em> chapter
    /// markers the source exposes. Engines that lack native chapters return an empty
    /// chapter list (and a non-null <see cref="VideoMetadata.Description"/> so the
    /// caller can fall back to description parsing).
    /// </summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <returns>The metadata, or <c>null</c> if the lookup failed.</returns>
    Task<VideoMetadata?> GetMetadataAsync(string videoId, CancellationToken ct = default);
}

/// <summary>Shape of a resolved live stream set.</summary>
public enum VideoStreamKind
{
    /// <summary>Separate video-only + audio-only streams (audio added as a VLC slave).</summary>
    SeparateVideoAudio,
    /// <summary>A single muxed stream carrying both video and audio.</summary>
    Muxed,
    /// <summary>Audio-only stream (no video).</summary>
    AudioOnly,
}

/// <summary>
/// Resolved playable stream URLs for live playback. For
/// <see cref="VideoStreamKind.SeparateVideoAudio"/>, <see cref="PrimaryUrl"/> is the
/// video-only URL and <see cref="AudioSlaveUrl"/> is the audio-only URL to attach as
/// a VLC slave. For <see cref="VideoStreamKind.Muxed"/> and
/// <see cref="VideoStreamKind.AudioOnly"/>, <see cref="AudioSlaveUrl"/> is <c>null</c>.
/// </summary>
public sealed record VideoStreams(
    VideoStreamKind Kind,
    string PrimaryUrl,
    string? AudioSlaveUrl,
    string Resolution);

/// <summary>
/// Raw downloaded stream files for the disk caches. When the engine produced separate
/// streams, <see cref="VideoFilePath"/> and <see cref="AudioFilePath"/> are both set;
/// the caller muxes them. Containers are reported so callers can name/mux correctly.
/// </summary>
public sealed record VideoDownload(
    string VideoFilePath,
    string AudioFilePath,
    string VideoContainer,
    string AudioContainer,
    string Resolution);

/// <summary>
/// Video metadata for chapter/duration enrichment. <see cref="Chapters"/> holds
/// <em>native</em> markers when the engine exposes them; when empty, the caller falls
/// back to parsing <see cref="Description"/>. <see cref="Duration"/> and
/// <see cref="UploadDate"/> may be null if the source did not report them.
/// </summary>
public sealed record VideoMetadata(
    TimeSpan? Duration,
    string? Description,
    List<ChapterMarker> Chapters,
    DateTimeOffset? UploadDate = null);
