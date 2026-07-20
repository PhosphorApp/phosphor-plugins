using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Phosphor.Video;

/// <summary>
/// <see cref="IVideoEngine"/> backed by YoutubeExplode. This wraps the exact
/// manifest + <see cref="StreamSelector"/> + download logic that previously lived
/// inline in <c>BackglassWindow</c>, <c>VideoCache</c>, and <c>PrefetchCache</c>,
/// so routing those call sites through the engine is behavior-identical.
/// </summary>
public sealed class YoutubeExplodeVideoEngine : IVideoEngine
{
    private readonly YoutubeClient _youtube = new();

    /// <summary>Always available — runs in-process.</summary>
    public bool IsAvailable => true;

    public async Task<VideoStreams?> ResolveStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        bool audioOnly,
        CancellationToken ct = default)
    {
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);

        var audioStream = StreamSelector.SelectAudio(manifest, preferStereo);

        // Audio-only mode: stream just audio. If no audio stream is available, fall
        // through to the video path (mirrors the original BackglassWindow behavior).
        if (audioOnly && audioStream != null)
        {
            return new VideoStreams(VideoStreamKind.AudioOnly, audioStream.Url, null, "");
        }

        var videoStream = StreamSelector.SelectVideo(manifest, quality);

        if (videoStream != null && audioStream != null)
        {
            var resolution = $"{videoStream.VideoResolution.Width}x{videoStream.VideoResolution.Height}";
            return new VideoStreams(
                VideoStreamKind.SeparateVideoAudio,
                videoStream.Url,
                audioStream.Url,
                resolution);
        }

        // Fallback to muxed if separate streams aren't available.
        var muxed = StreamSelector.SelectMuxed(manifest, quality);
        if (muxed == null) return null;

        var muxedResolution = $"{muxed.VideoResolution.Width}x{muxed.VideoResolution.Height}";
        return new VideoStreams(VideoStreamKind.Muxed, muxed.Url, null, muxedResolution);
    }

    public async Task<VideoDownload?> DownloadStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        string destinationDir,
        CancellationToken ct = default)
    {
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
        var videoStream = StreamSelector.SelectVideo(manifest, quality);
        var audioStream = StreamSelector.SelectAudio(manifest, preferStereo);

        if (videoStream == null || audioStream == null) return null;

        var videoFile = $"{videoId}_video.{videoStream.Container.Name}";
        var audioFile = $"{videoId}_audio.{audioStream.Container.Name}";
        var videoPath = Path.Combine(destinationDir, videoFile);
        var audioPath = Path.Combine(destinationDir, audioFile);

        await _youtube.Videos.Streams.DownloadAsync(videoStream, videoPath, cancellationToken: ct);
        await _youtube.Videos.Streams.DownloadAsync(audioStream, audioPath, cancellationToken: ct);

        var resolution = $"{videoStream.VideoResolution.Width}x{videoStream.VideoResolution.Height}";
        return new VideoDownload(
            videoPath,
            audioPath,
            videoStream.Container.Name,
            audioStream.Container.Name,
            resolution);
    }

    public async Task<VideoMetadata?> GetMetadataAsync(string videoId, CancellationToken ct = default)
    {
        var video = await _youtube.Videos.GetAsync(videoId, ct);

        // YoutubeExplode exposes no native chapter markers — return an empty list plus
        // the description so the caller parses chapters from it (as it always has).
        return new VideoMetadata(video.Duration, video.Description, new List<ChapterMarker>(), video.UploadDate);
    }
}
