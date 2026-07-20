using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Twitch;

/// <summary>
/// Provider (type + factory) for the Twitch source. Browses curated pinball channels, the top live
/// directory, and per-channel VODs, and searches live channels — all via Twitch's <em>keyless</em>
/// public GraphQL endpoint (no OAuth, no account, no token). Playback resolves through the
/// host-bundled yt-dlp. Live streams flow through the host's IsLiveStream path; VODs are finite and
/// seekable. Users pin items with the star toggle (IFavoritable). Multi-instance.
///
/// Marked <see cref="IExperimental"/>: discovery rides Twitch's unofficial web GQL endpoint, which
/// can change without notice.
/// </summary>
public sealed class TwitchSourceProvider : IPhosphorSourceProvider, IExperimental
{
    public const string TwitchTypeId = "twitch";

    /// <summary>Settings key: curated channel logins (one per line), shown under a "Pinball" node.</summary>
    public const string KeyChannels = "channels";

    /// <summary>Settings key: coarse video quality ceiling.</summary>
    public const string KeyQuality = "quality";

    /// <summary>Settings key: decorate the now-live feed's thumbnail with a red corner dot.</summary>
    public const string KeyLiveIndicator = "liveIndicator";

    /// <summary>
    /// Seed channels the plug-in ships with. Pinball-cabinet-relevant streamers/creators; users edit
    /// the list freely in settings. Kept as logins (the twitch.tv/&lt;login&gt; slug).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultChannels =
    [
        "deadflip",         // Dead Flip — long-running pinball streams
        "buffalopinball",   // Buffalo Pinball
        "straightdownthemiddle",
        "foxcitiespinball", // Fox Cities Pinball
        "mpt3k",            // MPT3K
    ];

    public string TypeId => TwitchTypeId;
    public string DisplayName => "Twitch";

    public string? Description =>
        "Browses curated pinball channels, the top live directory, and channel VODs, and searches " +
        "Twitch — all keyless (no account, token, or setup; Twitch's public GraphQL is anonymous). " +
        "Playback is resolved through the bundled yt-dlp; live streams play as continuous 'live' " +
        "with no seek, VODs are finite and seekable. Star an item to pin it to your Favorites. " +
        "Subscriber-only or geo-restricted content cannot be resolved.";

    public Version ApiVersion => PluginApi.Current;

    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyChannels, "Pinball channels", PluginSettingType.Text,
            DefaultValue: string.Join('\n', DefaultChannels),
            HelpText: "Twitch channel logins to surface under the Pinball tile (one per line). " +
                      "Use the name from the channel URL, e.g. twitch.tv/deadflip → deadflip.")
        {
            AllowMultiple = true,
        },
        new(KeyQuality, "Video quality", PluginSettingType.Enum, DefaultValue: "High",
            HelpText: "Quality ceiling yt-dlp should not exceed when picking a stream.")
        {
            EnumValues = ["Low", "Medium", "High", "Max"],
        },
        new(KeyLiveIndicator, "Show live indicator", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Mark currently-broadcasting channels as live — a red dot on the live feed's " +
                      "thumbnail, and a red ● LIVE tag on live channel tiles (in Favorites and Pinball)."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new TwitchSource(instanceId, settings);
}
