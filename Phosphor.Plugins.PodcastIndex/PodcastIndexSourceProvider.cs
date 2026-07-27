using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.PodcastIndex;

/// <summary>
/// Provider for the Podcast Index source: indexes and plays podcasts via the Podcast Index API
/// (podcastindex.org). Keyed per-user — each user registers a free API key + secret (Option B) so no
/// shared credential ships in this open-source repo. Single-instance. Loaded dynamically from the
/// host's <c>plugins/</c> folder.
/// </summary>
public sealed class PodcastIndexSourceProvider : IPhosphorSourceProvider
{
    public const string PodcastIndexTypeId = "podcastindex";

    /// <summary>Settings key: Podcast Index API key (secret).</summary>
    public const string KeyApiKey = "apiKey";

    /// <summary>Settings key: Podcast Index API secret (secret).</summary>
    public const string KeyApiSecret = "apiSecret";

    public string TypeId => PodcastIndexTypeId;
    public string DisplayName => "Podcast Index";

    public string? Description =>
        "Indexes and plays podcasts from the Podcast Index (podcastindex.org). Browse trending shows " +
        "and categories or search, then drill into a show to play its episodes. Episodes are finite, " +
        "seekable tracks. Requires a free Podcast Index API key + secret (register once, no cost).";

    public Version ApiVersion => PluginApi.Current;

    /// <summary>Podcast Index requires a free, per-user API key (registered at api.podcastindex.org).</summary>
    public AccountRequirement? Account => new(
        Summary: "a free Podcast Index API key",
        SignupUrl: "https://api.podcastindex.org/",
        IsPaid: false);

    // One key per configured instance — a second Podcast Index key is an unusual case.
    public bool SupportsMultipleInstances => false;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyApiKey, "API Key", PluginSettingType.Secret, Secret: true,
            HelpText: "Your Podcast Index API key. Register free at https://api.podcastindex.org/. " +
                      "Stored per the host's secret settings."),
        new(KeyApiSecret, "API Secret", PluginSettingType.Secret, Secret: true,
            HelpText: "Your Podcast Index API secret (issued alongside the key). " +
                      "Stored per the host's secret settings."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new PodcastIndexSource(instanceId, settings);
}
