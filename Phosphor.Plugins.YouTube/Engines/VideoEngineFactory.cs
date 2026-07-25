namespace Phosphor.Video;

using Phosphor;
using Phosphor.Plugin.Abstractions;

/// <summary>
/// Creates the configured <see cref="IVideoEngine"/> implementation. This is the
/// single switch point between YoutubeExplode and yt-dlp for the video path.
/// </summary>
public static class VideoEngineFactory
{
    public static IVideoEngine Create(VideoEngineKind kind, PluginLog? log = null)
    {
        IVideoEngine engine = kind switch
        {
            VideoEngineKind.YtDlp => new YtDlpVideoEngine(log: log),
            _ => new YoutubeExplodeVideoEngine(log),
        };

        // Safety net: if the requested engine can't run (e.g. yt-dlp.exe missing), fall
        // back to the always-available in-process engine so playback never hard-fails.
        if (!engine.IsAvailable)
        {
            log?.Invoke(LogLevel.Warning, "VideoEngine", $"{kind} unavailable — falling back to YoutubeExplode");
            return new YoutubeExplodeVideoEngine(log);
        }

        return engine;
    }
}
