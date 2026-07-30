using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.PodcastIndex;

/// <summary>
/// Provider for the Podcast Index source: indexes and plays podcasts via the Podcast Index API
/// (podcastindex.org). Works out of the box with Phosphor's built-in shared credentials; users who
/// prefer their own key can switch the "Authorization" setting to BYOK and enter an API key + secret.
/// Single-instance. Loaded dynamically from the host's <c>plugins/</c> folder.
/// </summary>
public sealed class PodcastIndexSourceProvider : IPhosphorSourceProvider
{
    public const string PodcastIndexTypeId = "podcastindex";

    /// <summary>Settings key: how the source authenticates ("Phosphor" built-in key vs BYOK).</summary>
    public const string KeyAuthMode = "authMode";

    /// <summary>Auth mode: use Phosphor's built-in, shared API credentials (default).</summary>
    public const string AuthModePhosphor = "Phosphor - Built in";

    /// <summary>Auth mode: bring your own key — the user supplies their own API key + secret.</summary>
    public const string AuthModeByok = "Bring your own key (BYOK)";

    /// <summary>Settings key: Podcast Index API key (secret).</summary>
    public const string KeyApiKey = "apiKey";

    /// <summary>Settings key: Podcast Index API secret (secret).</summary>
    public const string KeyApiSecret = "apiSecret";

    public string TypeId => PodcastIndexTypeId;
    public string DisplayName => "Podcast Index";

    public string? Description =>
        "Indexes and plays podcasts from the Podcast Index (podcastindex.org). Browse trending shows " +
        "and categories or search, then drill into a show to play its episodes. Episodes are finite, " +
        "seekable tracks. Works out of the box with Phosphor's built-in access, or bring your own free " +
        "Podcast Index API key + secret.";

    public Version ApiVersion => PluginApi.Current;

    // Works out of the box with the built-in "Phosphor" credentials, so no account is required. Users
    // who prefer their own key can switch to BYOK in settings.
    public AccountRequirement? Account => null;

    // One key per configured instance — a second Podcast Index key is an unusual case.
    public bool SupportsMultipleInstances => false;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyAuthMode, "Authorization", PluginSettingType.Enum, DefaultValue: AuthModePhosphor,
            HelpText: "How to authenticate with Podcast Index. \"Phosphor - Built in\" uses built-in " +
                      "shared access — nothing to configure. \"Bring your own key (BYOK)\" uses the API " +
                      "key and secret you enter below (register free at https://api.podcastindex.org/).")
        {
            EnumValues = [AuthModePhosphor, AuthModeByok],
        },
        new(KeyApiKey, "API Key", PluginSettingType.Secret, Secret: true,
            HelpText: "Your Podcast Index API key (BYOK only). Register free at " +
                      "https://api.podcastindex.org/. Ignored when Authorization is \"Phosphor - Built in\".")
        {
            EnabledWhen = new SettingDependency(KeyAuthMode, [AuthModeByok]),
        },
        new(KeyApiSecret, "API Secret", PluginSettingType.Secret, Secret: true,
            HelpText: "Your Podcast Index API secret (BYOK only, issued alongside the key). " +
                      "Ignored when Authorization is \"Phosphor - Built in\".")
        {
            EnabledWhen = new SettingDependency(KeyAuthMode, [AuthModeByok]),
        },
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new PodcastIndexSource(instanceId, settings);
}
