using System.Net.Http;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.YouTube;

/// <summary>
/// Provider (type + factory) for the in-box YouTube source. Single-instance: there is only
/// one YouTube. Exposes the YoutubeExplode-vs-yt-dlp engine choice and playback preferences
/// as declarative settings the host renders in the generic Plug-ins tab; the source itself
/// makes the actual engine determination internally.
/// </summary>
public sealed class YouTubeSourceProvider : IPhosphorSourceProvider
{
    public const string YouTubeTypeId = "youtube";

    public const string KeySearchEngine = "searchEngine";
    public const string KeyVideoEngine = "videoEngine";
    public const string KeyVideoQuality = "videoQuality";
    public const string KeyPreferStereo = "preferStereo";

    private readonly HttpClient? _http;

    public YouTubeSourceProvider(HttpClient? http = null) => _http = http;

    public string TypeId => YouTubeTypeId;
    public string DisplayName => "YouTube";
    public string? Description =>
        "Streams from YouTube. Search, playlists (playlist:), and channels (channel:) are supported. " +
        "Choose the search/video backend below — yt-dlp resolves streams more reliably but requires yt-dlp.exe.";
    public Version ApiVersion => PluginApi.Current;

    /// <summary>Only one YouTube instance makes sense.</summary>
    public bool SupportsMultipleInstances => false;

    /// <summary>
    /// yt-dlp is optional at the source level (YoutubeExplode runs in-process), but the yt-dlp
    /// engine path and ffmpeg muxing rely on the host-bundled tools. Declared for load-time
    /// visibility/diagnostics; the source degrades to YoutubeExplode when yt-dlp is absent.
    /// </summary>
    public IReadOnlyList<string> RequiredTools => ["yt-dlp", "ffmpeg"];

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeySearchEngine, "Search engine", PluginSettingType.Enum, DefaultValue: "YoutubeExplode",
            HelpText: "Backend used for search/discovery.")
        {
            EnumValues = ["YoutubeExplode", "YtDlp"],
        },
        new(KeyVideoEngine, "Video engine", PluginSettingType.Enum, DefaultValue: "YoutubeExplode",
            HelpText: "Backend used to resolve/download streams. Falls back automatically if unavailable.")
        {
            EnumValues = ["YoutubeExplode", "YtDlp"],
        },
        new(KeyVideoQuality, "Video quality", PluginSettingType.Enum, DefaultValue: "High")
        {
            EnumValues = ["Low", "Medium", "High", "Max"],
        },
        new(KeyPreferStereo, "Prefer stereo audio", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Avoid surround tracks in favor of stereo."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new YouTubeSource(instanceId, settings, _http);
}
