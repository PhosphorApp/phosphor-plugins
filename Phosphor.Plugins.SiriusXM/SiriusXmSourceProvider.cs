using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// Provider for the SiriusXM source: streams live SiriusXM channels for a logged-in subscriber.
/// Single-instance (one account). Loaded dynamically from the host's <c>plugins/</c> folder.
/// </summary>
public sealed class SiriusXmSourceProvider : IPhosphorSourceProvider
{
    public const string SiriusXmTypeId = "siriusxm";

    /// <summary>Settings key: SiriusXM account username/email.</summary>
    public const string KeyUsername = "username";

    /// <summary>Settings key: SiriusXM account password (secret).</summary>
    public const string KeyPassword = "password";

    /// <summary>Settings key: account region ("US" or "CA").</summary>
    public const string KeyRegion = "region";

    /// <summary>Settings key: local HLS proxy port.</summary>
    public const string KeyProxyPort = "proxyPort";

    /// <summary>Settings key: legacy cookie streaming path. Not exposed in the settings schema and ignored when read;
    /// the key remains only so the legacy code path can be revived.</summary>
    public const string KeyUseLegacyStreaming = "useLegacyStreaming";

    /// <summary>Settings key: how many ~10s segments to stay behind live (network-blip cushion).</summary>
    public const string KeyLiveDelaySegments = "liveDelaySegments";

    /// <summary>Settings key: experimental override of the served HLS target duration (seconds; 0 = off).</summary>
    public const string KeyTargetDurationOverride = "targetDurationOverride";

    /// <summary>Settings key: continuous (non-HLS) stream mode toggle.</summary>
    public const string KeyContinuousStream = "continuousStream";

    /// <summary>Continuous stream mode is on unless the user turns it off.</summary>
    public const bool DefaultContinuousStream = true;
    public const string RegionUs = "US";
    public const string RegionCa = "CA";

    /// <summary>Default local HLS proxy port when the setting is unset/invalid.</summary>
    public const int DefaultProxyPort = 8912;

    public string TypeId => SiriusXmTypeId;
    public string DisplayName => "SiriusXM";

    public string? Description =>
        "Streams live SiriusXM channels for a logged-in subscriber. Enter your SiriusXM account " +
        "username and password, then use \"Test connection\" to verify. Channels are live radio " +
        "streams (no seek or track boundaries). Requires an active SiriusXM streaming subscription.";

    public Version ApiVersion => PluginApi.Current;

    /// <summary>SiriusXM streaming requires an active paid subscription.</summary>
    public AccountRequirement? Account => new(
        Summary: "an active SiriusXM streaming subscription",
        SignupUrl: "https://www.siriusxm.com/plans",
        IsPaid: true);

    // One account per configured instance — a second SiriusXM login is an unusual case.
    public bool SupportsMultipleInstances => false;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyUsername, "Username", PluginSettingType.Text,
            HelpText: "Your SiriusXM account email/username."),
        new(KeyPassword, "Password", PluginSettingType.Secret, Secret: true,
            HelpText: "Your SiriusXM account password. Stored per the host's secret settings."),
        new(KeyRegion, "Region", PluginSettingType.Enum, DefaultValue: RegionUs,
            HelpText: "Your account region.")
        {
            EnumValues = [RegionUs, RegionCa],
        },
        new(KeyProxyPort, "Proxy port", PluginSettingType.Number, DefaultValue: DefaultProxyPort.ToString(),
            HelpText: "Local port for the built-in HLS proxy that feeds the player. " +
                      "Change only if another app already uses this port."),
        new(KeyLiveDelaySegments, "Live delay (segments)", PluginSettingType.Number,
            DefaultValue: SxmEdgeProxy.DefaultLiveDelaySegments.ToString(),
            HelpText: "How far behind live to play, in ~10 second segments (0-8). Higher values give a " +
                      "buffer that rides out network hiccups and dropouts; lower values reduce latency. " +
                      "Default 3 (~30s) suits music. Use 0 for live sports/news."),
        new(KeyTargetDurationOverride, "Playlist target duration override (seconds)", PluginSettingType.Number,
            DefaultValue: "0",
            HelpText: "Experimental. Makes the player poll the playlist faster, which may avoid a brief audio " +
                      "glitch about every 3 minutes. 0 = off (default). Try 9. Applies on the next playlist refresh."),
        new(KeyContinuousStream, "Continuous stream mode", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Feeds the player one continuous audio stream instead of an HLS playlist, which avoids " +
                      "brief audio glitches caused by the player's playlist handling. Takes effect on the " +
                      "next play. Turn OFF to use standard HLS playback if audio misbehaves."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new SiriusXmSource(instanceId, settings);
}
