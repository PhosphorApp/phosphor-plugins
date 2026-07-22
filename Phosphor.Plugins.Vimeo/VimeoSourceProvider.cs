using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Vimeo;

/// <summary>
/// Provider (type + factory) for the Vimeo source. Browses Vimeo's curated categories and searches
/// Vimeo (both via an access token), and resolves playback through the host-bundled yt-dlp. Users pin
/// specific videos with the star toggle (IFavoritable) rather than pasting URLs. Multi-instance.
/// </summary>
public sealed class VimeoSourceProvider : IPhosphorSourceProvider
{
    public const string VimeoTypeId = "vimeo";

    /// <summary>
    /// Settings key: a Vimeo API access token (unauthenticated / public scope). Required for browse
    /// and search (there is no keyless Vimeo discovery). Stored as a secret. No client secret / OAuth
    /// flow is needed for a public-scoped token.
    /// </summary>
    public const string KeyAccessToken = "accessToken";

    /// <summary>Settings key: coarse video quality ceiling.</summary>
    public const string KeyQuality = "quality";

    public string TypeId => VimeoTypeId;
    public string DisplayName => "Vimeo";

    public string? Description =>
        "Browses Vimeo's categories and searches Vimeo. Requires a Vimeo API access token " +
        "(unauthenticated / public scope) — Vimeo has no keyless discovery. " +
        "Star a video to pin it to your Favorites. Private, " +
        "password-protected, or domain-locked videos will not work.";

    public Version ApiVersion => PluginApi.Current;

    /// <summary>Vimeo browse/search needs a developer access token tied to a (free) Vimeo account.</summary>
    public AccountRequirement? Account => new(
        Summary: "a free Vimeo account (for an API access token)",
        SignupUrl: "https://developer.vimeo.com",
        IsPaid: false);

    // A user may want several Vimeo tiles.
    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyAccessToken, "API access token", PluginSettingType.Secret, Secret: true,
            HelpText: "Required for browsing/searching Vimeo (there is no keyless Vimeo discovery). " +
                      "Add a Vimeo app access token (unauthenticated / public scope). Each user " +
                      "supplies their own — get one at developer.vimeo.com (see the plug-in README). " +
                      "Never embedded in the app. Playback of pinned favorites still uses yt-dlp."),
        new(KeyQuality, "Video quality", PluginSettingType.Enum, DefaultValue: "High",
            HelpText: "Quality ceiling yt-dlp should not exceed when picking a stream.")
        {
            EnumValues = ["Low", "Medium", "High", "Max"],
        },
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new VimeoSource(instanceId, settings);
}
