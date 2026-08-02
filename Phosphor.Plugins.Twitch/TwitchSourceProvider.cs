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

    /// <summary>
    /// Settings key holding the user's channel groups, one per row (the host renders an add/remove
    /// list editor via <c>AllowMultiple</c>). Each row is <c>Name = login1, login2, …</c>; groups
    /// surface as browse nodes inside the Twitch tile. Owned by the plug-in; seeded with a "Pinball"
    /// row on first run.
    /// </summary>
    public const string KeyChannelGroups = "channelGroups";

    /// <summary>Settings key: coarse video quality ceiling.</summary>
    public const string KeyQuality = "quality";

    /// <summary>Settings key: decorate the now-live feed's thumbnail with a red corner dot.</summary>
    public const string KeyLiveIndicator = "liveIndicator";

    public string TypeId => TwitchTypeId;
    public string DisplayName => "Twitch";

    public string? Description =>
        "Browses curated pinball channels, the top live directory, and channel VODs, and searches " +
        "Twitch — all keyless (no account, token, or setup; Twitch's public GraphQL is anonymous). " +   
        "Subscriber-only or geo-restricted content will not work.";

    public Version ApiVersion => PluginApi.Current;

    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyChannelGroups, "Channel groups", PluginSettingType.Text,
            DefaultValue: TwitchChannelGroups.DefaultRows,
            HelpText: "Named channel groups shown as tiles inside Twitch (one group per row). " +
                      "Format: \"[icon] Name = login1, login2\" — an optional leading emoji sets the " +
                      "tile glyph (default ⚪), e.g. \"🎪 Concerts = channel_a, channel_b\". " +
                      "A full twitch.tv/<login> URL also works.")
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
                      "thumbnail, and a red ● LIVE tag on live channel tiles (in Favorites and groups)."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new TwitchSource(instanceId, settings);
}
