using YoutubeExplode.Videos.Streams;

namespace Phosphor;

/// <summary>
/// Selects video and audio streams from a manifest based on the user's quality preference.
/// Internal detail of <see cref="Phosphor.Video.YoutubeExplodeVideoEngine"/>.
/// </summary>
internal static class StreamSelector
{
    private static int MaxHeight(VideoQualityPreference pref) => pref switch
    {
        VideoQualityPreference.Low => 480,
        VideoQualityPreference.Medium => 720,
        VideoQualityPreference.High => 1080,
        _ => int.MaxValue
    };

    private static void LogVideoStream(string label, IVideoStreamInfo stream)
    {
        DebugLog.Log("StreamSelector",
            $"{label}: {stream.VideoQuality.Label} {stream.VideoResolution.Width}x{stream.VideoResolution.Height} " +
            $"codec={stream.VideoCodec} bitrate={stream.Bitrate} size={stream.Size} container={stream.Container}");
    }

    private static void LogAudioStream(string label, IAudioStreamInfo stream)
    {
        DebugLog.Log("StreamSelector",
            $"{label}: codec={stream.AudioCodec} bitrate={stream.Bitrate} size={stream.Size} container={stream.Container}");
    }

    /// <summary>
    /// Picks the best video-only stream at or below the preferred quality ceiling.
    /// Falls back to the lowest available if nothing is at or below the cap.
    /// </summary>
    public static VideoOnlyStreamInfo? SelectVideo(StreamManifest manifest, VideoQualityPreference pref)
    {
        var streams = manifest.GetVideoOnlyStreams();
        int cap = MaxHeight(pref);

        // Best stream at or below the cap
        var pick = streams
            .Where(s => s.VideoQuality.MaxHeight <= cap)
            .OrderByDescending(s => s.VideoQuality.MaxHeight)
            .ThenByDescending(s => s.Bitrate.BitsPerSecond)
            .FirstOrDefault();

        // If nothing fits (e.g. only 1080p+ available), take the lowest available
        var result = pick ?? streams
            .OrderBy(s => s.VideoQuality.MaxHeight)
            .FirstOrDefault();

        if (result != null)
            LogVideoStream($"Selected video-only (pref={pref}, cap={cap}p)", result);

        return result;
    }

    /// <summary>
    /// Bitrate ceiling used to skip surround (5.1) audio streams.
    /// YouTube surround streams are typically 256 kbps+; stereo tops out around 160 kbps (opus)
    /// or 128 kbps (aac). A 192 kbps threshold safely excludes surround while keeping the
    /// best stereo option.
    /// </summary>
    private const long StereoBitrateCeiling = 192_000;

    /// <summary>
    /// Picks the best audio-only stream. When <paramref name="preferStereo"/> is true,
    /// selects the highest-bitrate stream at or below <see cref="StereoBitrateCeiling"/>
    /// (which excludes YouTube's surround/5.1 tracks), falling back to the overall
    /// highest bitrate if no stream fits under the ceiling.
    /// </summary>
    public static AudioOnlyStreamInfo? SelectAudio(StreamManifest manifest, bool preferStereo = false)
    {
        AudioOnlyStreamInfo? result;

        if (preferStereo)
        {
            var stereo = manifest.GetAudioOnlyStreams()
                .OfType<AudioOnlyStreamInfo>()
                .Where(s => s.Bitrate.BitsPerSecond <= StereoBitrateCeiling)
                .OrderByDescending(s => s.Bitrate.BitsPerSecond)
                .FirstOrDefault();

            result = stereo ?? (AudioOnlyStreamInfo?)manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        }
        else
        {
            result = (AudioOnlyStreamInfo?)manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        }

        if (result != null)
            LogAudioStream($"Selected audio-only (stereo={preferStereo})", result);

        return result;
    }

    /// <summary>
    /// Picks the best muxed stream at or below the preferred quality ceiling.
    /// </summary>
    public static MuxedStreamInfo? SelectMuxed(StreamManifest manifest, VideoQualityPreference pref)
    {
        var streams = manifest.GetMuxedStreams();
        int cap = MaxHeight(pref);

        var pick = streams
            .Where(s => s.VideoQuality.MaxHeight <= cap)
            .OrderByDescending(s => s.VideoQuality.MaxHeight)
            .ThenByDescending(s => s.Bitrate.BitsPerSecond)
            .FirstOrDefault();

        var result = pick ?? streams
            .OrderBy(s => s.VideoQuality.MaxHeight)
            .FirstOrDefault();

        if (result != null)
        {
            DebugLog.Log("StreamSelector",
                $"Selected muxed (pref={pref}, cap={cap}p): {result.VideoQuality.Label} {result.VideoResolution.Width}x{result.VideoResolution.Height} " +
                $"videoCodec={result.VideoCodec} audioCodec={result.AudioCodec} bitrate={result.Bitrate} size={result.Size} container={result.Container}");
        }

        return result;
    }
}
