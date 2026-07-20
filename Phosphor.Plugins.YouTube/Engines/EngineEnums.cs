namespace Phosphor;

/// <summary>
/// Plug-in-internal copy of the host's video quality ceiling. The plug-in never references the
/// host's <c>AppSettings</c> enum (it lives across the load boundary); <c>YouTubeMappings</c>
/// maps the contract's <c>VideoQuality</c> onto this.
/// </summary>
public enum VideoQualityPreference
{
    Low,    // up to 480p
    Medium, // up to 720p
    High,   // up to 1080p
    Max     // best available
}

/// <summary>Plug-in-internal copy of the host's video-engine selector.</summary>
public enum VideoEngineKind
{
    YoutubeExplode, // in-process YoutubeExplode (default)
    YtDlp           // external yt-dlp.exe
}

/// <summary>Plug-in-internal copy of the host's search-engine selector.</summary>
public enum SearchEngineKind
{
    YoutubeExplode, // in-process YoutubeExplode (default)
    YtDlp           // external yt-dlp.exe
}
