using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Iptv;

/// <summary>
/// Provider for the IPTV source: surfaces the community-maintained <c>iptv-org</c> catalog of
/// publicly-listed free live TV/radio streams, browsable by country and by category/genre. The
/// catalog and stream URLs come entirely from the public iptv-org API — this plug-in only stores
/// links, never media (see the Description and README for the legal caveats). Multiple instances are
/// allowed so a user could point a second instance at a custom playlist later.
/// </summary>
public sealed class IptvSourceProvider : IPhosphorSourceProvider
{
    public const string IptvTypeId = "iptv";

    /// <summary>Settings key: which browse organizations to expose ("Both", "Country", or "Category").</summary>
    public const string KeyOrganizeBy = "organizeBy";

    /// <summary>Value of <see cref="KeyOrganizeBy"/> that groups channels by broadcast country.</summary>
    public const string OrganizeByCountry = "Country";

    /// <summary>Value of <see cref="KeyOrganizeBy"/> that groups channels by category/genre.</summary>
    public const string OrganizeByCategory = "Category";

    /// <summary>Value of <see cref="KeyOrganizeBy"/> that exposes both organizations as sibling tiles.</summary>
    public const string OrganizeByBoth = "Both";

    /// <summary>Settings key: whether to include channels flagged as adult content ("true"/"false").</summary>
    public const string KeyIncludeNsfw = "includeNsfw";

    /// <summary>Settings key: how many hours the cached catalog stays fresh before an auto-refresh.</summary>
    public const string KeyCacheMaxAgeHours = "cacheMaxAgeHours";

    public string TypeId => IptvTypeId;
    public string DisplayName => "IPTV (iptv-org)";

    public string? Description =>
        "Free live TV & radio from the community-maintained iptv-org project " +
        "(https://github.com/iptv-org/iptv), browsable by country and by category. " +
        "No account needed. Note: this plug-in only lists publicly-submitted stream links — it stores " +
        "no media. Streams are third-party, may be geo-restricted, and can go offline at any time; dead " +
        "channels are expected. Some content may be subject to local broadcast rights — use responsibly. " +
        "Enable \"Rescan library\" to refresh the catalog from the iptv-org API.";

    public Version ApiVersion => PluginApi.Current;

    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyOrganizeBy, "Organize by", PluginSettingType.Enum, DefaultValue: OrganizeByBoth,
            HelpText: "Which browse views to show: a \"By Country\" tile, a \"By Category\" tile, or both.")
        {
            EnumValues = [OrganizeByBoth, OrganizeByCountry, OrganizeByCategory],
        },
        new(KeyIncludeNsfw, "Include adult channels", PluginSettingType.Bool, DefaultValue: "false",
            HelpText: "Include channels the iptv-org catalog flags as adult content. Off by default."),
        new(KeyCacheMaxAgeHours, "Cache freshness (hours)", PluginSettingType.Number, DefaultValue: "24",
            HelpText: "How long the downloaded catalog stays fresh before it is refreshed on first use. " +
                      "0 keeps the cache until you manually run \"Rescan library\"."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new IptvSource(instanceId, settings);
}
