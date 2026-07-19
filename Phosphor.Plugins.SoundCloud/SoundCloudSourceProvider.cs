using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.SoundCloud;

/// <summary>
/// Provider (type + factory) for the SoundCloud source. Browses curated genre feeds and searches
/// SoundCloud via yt-dlp's <em>keyless</em> <c>scsearch</c> extractor (no OAuth, no token — yt-dlp
/// auto-derives a client_id), and resolves audio playback through the same host-bundled yt-dlp.
/// Users pin tracks with the star toggle (IFavoritable). Multi-instance.
/// </summary>
public sealed class SoundCloudSourceProvider : IPhosphorSourceProvider, IExperimental
{
    public const string SoundCloudTypeId = "soundcloud";

    /// <summary>Settings key: how many results per genre feed / search to request from yt-dlp.</summary>
    public const string KeyResultLimit = "resultLimit";

    public string TypeId => SoundCloudTypeId;
    public string DisplayName => "SoundCloud";

    public string? Description =>
        "Browses curated SoundCloud genre feeds and searches SoundCloud's catalog. " +
        "No account, token, or setup needed — discovery and playback both ride the bundled yt-dlp, " +
        "whose SoundCloud extractor is keyless. Audio-only. Star a track to pin it to your " +
        "Favorites. Some tracks are preview-only or DRM-protected and cannot be resolved.";

    public Version ApiVersion => PluginApi.Current;

    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyResultLimit, "Results per feed", PluginSettingType.Enum, DefaultValue: "50",
            HelpText: "How many tracks to fetch per genre feed or search.")
        {
            EnumValues = ["25", "50", "75", "100"],
        },
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new SoundCloudSource(instanceId, settings);
}
