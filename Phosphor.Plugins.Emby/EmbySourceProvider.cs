using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Emby;

/// <summary>
/// Provider for the Emby source: browses and plays music/video from a self-hosted Emby
/// server for a logged-in user. Multi-instance (a user may add several servers). Loaded dynamically
/// from the host's <c>plugins/</c> folder. Emby and Jellyfin share nearly identical REST APIs
/// (Jellyfin began as a fork of Emby), so this mirrors the Jellyfin plug-in.
/// </summary>
public sealed class EmbySourceProvider : IPhosphorSourceProvider
{
    public const string EmbyTypeId = "emby";

    /// <summary>Settings key: Emby server base URL (e.g. http://192.168.1.10:8096).</summary>
    public const string KeyServerUrl = "serverUrl";

    /// <summary>Settings key: Emby account username.</summary>
    public const string KeyUsername = "username";

    /// <summary>Settings key: Emby account password (secret).</summary>
    public const string KeyPassword = "password";

    /// <summary>
    /// Settings key: when true, request a 2-channel (stereo) downmix from the server. Imperative for
    /// pinball cabs whose surround channels drive mechanical/ball exciters, not music.
    /// </summary>
    public const string KeyStereoAudio = "stereoAudio";

    /// <summary>
    /// Settings key: JSON array of selected library ids to show as tiles. Empty/unset = show all
    /// libraries. Populated via the "Browse libraries" config action.
    /// </summary>
    public const string KeyLibraries = "libraries";

    /// <summary>Config action id for the interactive library chooser. Deliberately NOT the same
    /// string the Plex source uses ("browseLibraries") — the host suppresses source-specific ids in
    /// the generic config-action UI because those sources render an inline library editor.</summary>
    public const string ActionBrowseLibraries = "embyBrowseLibraries";

    public string TypeId => EmbyTypeId;
    public string DisplayName => "Emby";

    public string? Description =>
        "Browses and plays music and video from a self-hosted Emby server. Enter your server " +
        "URL, username and password, then use \"Test connection\" to verify. Enable \"Stereo audio\" " +
        "on a pinball cabinet so surround-channel exciters aren't fed music (forces a 2-channel mix). " +
        "Requires a reachable Emby server and account.";

    public Version ApiVersion => PluginApi.Current;

    // Multiple servers are a normal case (home + friend's server), like Plex.
    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyServerUrl, "Server URL", PluginSettingType.Text,
            HelpText: "Base URL of your Emby server, e.g. http://192.168.1.10:8096"),
        new(KeyUsername, "Username", PluginSettingType.Text,
            HelpText: "Your Emby account username."),
        new(KeyPassword, "Password", PluginSettingType.Secret, Secret: true,
            HelpText: "Your Emby account password. Stored per the host's secret settings."),
        new(KeyStereoAudio, "Prefer stereo audio", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Ask the server for a 2-channel downmix. Keep ON for pinball cabinets whose " +
                      "surround channels drive mechanical/ball exciters."),
        // The library-tile selection is populated via the "Browse libraries" action and stored as an
        // opaque JSON blob; not directly user-editable as a text field.
        new(KeyLibraries, "Libraries", PluginSettingType.Text,
            HelpText: "Configured via 'Browse libraries…'."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new EmbySource(instanceId, settings);
}
