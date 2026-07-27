using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.HdHomeRun;

/// <summary>
/// Provider for the HDHomeRun source: surfaces the live TV lineup of a SiliconDust HDHomeRun network
/// tuner on the local network. Each configured instance points at one tuner's HTTP address (e.g.
/// <c>192.168.14.31</c>); multiple instances are allowed so a user with several tuners (or a
/// multi-device setup) can add each one. Phase 1 reads the tuner's local <c>/discover.json</c> and
/// <c>/lineup.json</c> for device details and the channel lineup. Phase 2 layers channel icons and
/// guide data from the SiliconDust cloud API on top (see <see cref="HdhrGuideClient"/>).
/// </summary>
public sealed class HdHomeRunSourceProvider : IPhosphorSourceProvider
{
    public const string HdHomeRunTypeId = "hdhomerun";

    /// <summary>Settings key: the tuner's HTTP address / host (e.g. "192.168.14.31" or "http://hdhr.local").</summary>
    public const string KeyTunerAddress = "tunerAddress";

    /// <summary>Settings key: whether to fetch channel icons/guide data from the SiliconDust cloud API (Phase 2).</summary>
    public const string KeyEnableGuideData = "enableGuideData";

    /// <summary>Settings key: how many minutes the cached lineup stays fresh before an auto-refresh.</summary>
    public const string KeyCacheMaxAgeMinutes = "cacheMaxAgeMinutes";

    public string TypeId => HdHomeRunTypeId;
    public string DisplayName => "HDHomeRun";

    public string? Description =>
        "Live over-the-air (and cable) TV from a SiliconDust HDHomeRun network tuner on your local " +
        "network (https://www.silicondust.com/). Enter the tuner's IP address or hostname (e.g. " +
        "192.168.14.31); the plug-in reads its lineup directly and plays each channel's live MPEG-TS " +
        "stream. Enable \"Fetch guide data\" to pull channel icons (and, later, program info) from the " +
        "SiliconDust guide service. Note: each channel occupies one tuner while playing, so you can " +
        "watch as many channels at once as your device has tuners. DRM-protected channels cannot be played.";

    public Version ApiVersion => PluginApi.Current;

    // A user may own more than one HDHomeRun device, so allow several configured instances.
    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyTunerAddress, "Tuner address", PluginSettingType.Text,
            HelpText: "The HDHomeRun tuner's IP address or hostname on your local network, e.g. " +
                      "\"192.168.14.31\". A scheme (http://) is optional and assumed."),
        new(KeyEnableGuideData, "Fetch guide data", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Pull channel icons (and, in a later phase, program guide data) from the SiliconDust " +
                      "guide service using the tuner's DeviceAuth token. Off keeps everything local."),
        new(KeyCacheMaxAgeMinutes, "Lineup freshness (minutes)", PluginSettingType.Number, DefaultValue: "60",
            HelpText: "How long the downloaded lineup stays fresh before it is refreshed on first use. " +
                      "0 keeps the cache until you manually run \"Rescan library\"."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new HdHomeRunSource(instanceId, settings);
}
