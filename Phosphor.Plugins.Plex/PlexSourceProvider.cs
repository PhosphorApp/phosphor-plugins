using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Plex;

/// <summary>
/// Provider (type + factory) for the in-box Plex source. Multi-instance: a user may
/// configure more than one Plex server. Exposes the connection settings declaratively; the
/// per-library tile selection is an interactive action on the instance (<see cref="IConfigurable"/>),
/// persisted by the host into the <see cref="KeyLibraries"/> blob.
/// </summary>
public sealed class PlexSourceProvider : IPhosphorSourceProvider
{
    public const string PlexTypeId = "plex";

    public const string KeyServerUrl = "serverUrl";
    public const string KeyToken = "token";
    public const string KeyStereoAudio = "stereoAudio";
    public const string KeyLibraries = "libraries";

    public const string ActionBrowseLibraries = "browseLibraries";

    /// <summary>Synthetic library "type" marking a Live TV tile in the persisted library mapping.</summary>
    public const string LiveTvType = "livetv";

    /// <summary>True when a persisted library mapping type denotes Live TV. Tolerant of spacing/case
    /// (the host derives the type by parsing the option label, so "live tv" can occur too).</summary>
    public static bool IsLiveTvType(string? type)
        => !string.IsNullOrEmpty(type) &&
           string.Equals(type.Replace(" ", ""), LiveTvType, StringComparison.OrdinalIgnoreCase);

    public string TypeId => PlexTypeId;
    public string DisplayName => "Plex";
    public string? Description =>
        "Browse and play from a Plex Media Server. Enter your server URL and X-Plex-Token, then use " +
        "\"Browse libraries\" to pick which libraries appear as tiles. Multiple Plex servers can be added.";
    public Version ApiVersion => PluginApi.Current;

    /// <summary>Users may have access to more than one Plex server.</summary>
    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyServerUrl, "Server URL", PluginSettingType.Text,
            HelpText: "e.g. http://192.168.1.10:32400"),
        new(KeyToken, "Plex token", PluginSettingType.Secret, Secret: true,
            HelpText: "X-Plex-Token for this server."),
        new(KeyStereoAudio, "Prefer stereo audio", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Downmix/transcode surround tracks to stereo. Imperative on pinball cabs, " +
                      "whose surround channels drive mechanical/ball exciters, not music."),
        // The library-tile selection is populated via the "Browse libraries" action and
        // stored as an opaque JSON blob; not directly user-editable as a text field.
        new(KeyLibraries, "Libraries", PluginSettingType.Text,
            HelpText: "Configured via 'Browse libraries…'."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new PlexSource(instanceId, settings);
}
