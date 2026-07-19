using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.IHeartRadio;

/// <summary>
/// Provider for the iHeartRadio source: streams live iHeartRadio radio stations. Key-less (no
/// account, no configuration) and single-instance. Loaded dynamically from the host's
/// <c>plugins/</c> folder.
/// </summary>
public sealed class IHeartRadioSourceProvider : IPhosphorSourceProvider
{
    public const string IHeartRadioTypeId = "iheartradio";

    public string TypeId => IHeartRadioTypeId;
    public string DisplayName => "iHeartRadio";

    public string? Description =>
        "Streams live iHeartRadio radio stations. No account or configuration required — browse by " +
        "genre or search for a station, then play. Stations are live radio streams (no seek or " +
        "track boundaries).";

    public Version ApiVersion => PluginApi.Current;

    // One instance is plenty — iHeart is a single public catalog with no per-user login.
    public bool SupportsMultipleInstances => false;

    // Nothing to configure: the public catalog endpoints are all key-less.
    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() => [];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new IHeartRadioSource(instanceId);
}
