using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Dailymotion;

/// <summary>
/// Provider (type + factory) for the Dailymotion source. Browses Dailymotion's editorial categories
/// and searches Dailymotion via its <em>keyless</em> public API (no OAuth, no token), and resolves
/// playback through the host-bundled yt-dlp. Users pin videos with the star toggle (IFavoritable).
/// Multi-instance.
/// </summary>
public sealed class DailymotionSourceProvider : IPhosphorSourceProvider
{
    public const string DailymotionTypeId = "dailymotion";

    /// <summary>Settings key: coarse video quality ceiling.</summary>
    public const string KeyQuality = "quality";

    public string TypeId => DailymotionTypeId;
    public string DisplayName => "Dailymotion";

    public string? Description =>
        "Browses Dailymotion's categories (Music, Movies, Gaming, …) and searches Dailymotion. " +
        "No account, token, or setup needed — Dailymotion's public API is keyless. Playback is " +
        "resolved through the bundled yt-dlp. Star a video to pin it to your Favorites. Private or " +
        "geo-restricted videos cannot be resolved.";

    public Version ApiVersion => PluginApi.Current;

    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyQuality, "Video quality", PluginSettingType.Enum, DefaultValue: "High",
            HelpText: "Quality ceiling yt-dlp should not exceed when picking a stream.")
        {
            EnumValues = ["Low", "Medium", "High", "Max"],
        },
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new DailymotionSource(instanceId, settings);
}
