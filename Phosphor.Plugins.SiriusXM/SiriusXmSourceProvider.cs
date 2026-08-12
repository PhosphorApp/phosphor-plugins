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

    /// <summary>Settings key: use the legacy cookie streaming path instead of the edge gateway.</summary>
    public const string KeyUseLegacyStreaming = "useLegacyStreaming";

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
        new(KeyUseLegacyStreaming, "Use legacy streaming (fallback)", PluginSettingType.Bool, DefaultValue: "false",
            HelpText: "Advanced/diagnostic: stream via the older SiriusXM cookie path instead of the " +
                      "edge-gateway API. Leave OFF unless edge-gateway playback fails and you need a " +
                      "temporary workaround. Now-playing and the channel lineup always use the gateway."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new SiriusXmSource(instanceId, settings);
}
