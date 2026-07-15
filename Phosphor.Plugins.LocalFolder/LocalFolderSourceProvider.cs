using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.LocalFolder;

/// <summary>
/// Provider for the local-folder source: plays media files the user has on disk. A reference
/// third-party plug-in — it references only <c>Phosphor.Plugin.Abstractions</c> and is discovered
/// dynamically from the host's <c>plugins/</c> folder. Multiple instances are allowed so a user can
/// group different folder sets (e.g. "Music" vs. "Concerts").
/// </summary>
public sealed class LocalFolderSourceProvider : IPhosphorSourceProvider
{
    public const string LocalFolderTypeId = "localfolder";

    /// <summary>Settings key: newline-separated list of absolute folder paths to scan.</summary>
    public const string KeyFolders = "folders";

    /// <summary>Settings key: whether to recurse into subdirectories ("true"/"false").</summary>
    public const string KeyRecursive = "recursive";

    /// <summary>
    /// Settings key: how many hours the on-disk catalog cache stays fresh before an automatic
    /// rescan is triggered on first use. "0" disables auto-expiry (cache is used until the user
    /// manually runs "Rescan library").
    /// </summary>
    public const string KeyCacheMaxAgeHours = "cacheMaxAgeHours";

    /// <summary>Settings key: which browse organizations to expose ("Both", "Folder", or "Metadata").</summary>
    public const string KeyOrganizeBy = "organizeBy";

    /// <summary>Value of <see cref="KeyOrganizeBy"/> that exposes only the on-disk folder structure.</summary>
    public const string OrganizeByFolder = "Folder";

    /// <summary>Value of <see cref="KeyOrganizeBy"/> that exposes only the tag metadata tree (Artist → Album).</summary>
    public const string OrganizeByMetadata = "Metadata";

    /// <summary>Value of <see cref="KeyOrganizeBy"/> that exposes both organizations as sibling tiles.</summary>
    public const string OrganizeByBoth = "Both";

    /// <summary>Settings key: whether to extract per-file thumbnails during a rescan ("true"/"false").</summary>
    public const string KeyExtractThumbnails = "extractThumbnails";

    public string TypeId => LocalFolderTypeId;
    public string DisplayName => "Local Folders";
    public string? Description =>
        "Plays audio and video files from folders on this machine — ideal for offline cabinets. " +
        "Add one or more folders, then use \"Rescan library\" to build the catalog.";

    public Version ApiVersion => PluginApi.Current;

    public bool SupportsMultipleInstances => true;

    public IReadOnlyList<PluginSettingDescriptor> GetSettingsSchema() =>
    [
        new(KeyFolders, "Folders", PluginSettingType.FolderPath,
            HelpText: "Folders to scan for media. Add one or more; files in them become playable.")
        {
            AllowMultiple = true,
        },
        new(KeyRecursive, "Include subfolders", PluginSettingType.Bool, DefaultValue: "true",
            HelpText: "Also scan folders nested inside the ones above."),
        new(KeyCacheMaxAgeHours, "Cache freshness (hours)", PluginSettingType.Number, DefaultValue: "0",
            HelpText: "How long the saved catalog stays fresh before an automatic rescan on startup. " +
                      "0 keeps the cache until you manually run \"Rescan library\"."),
        new(KeyOrganizeBy, "Organize by", PluginSettingType.Enum, DefaultValue: OrganizeByBoth,
            HelpText: "Which browse views to show: both a \"By Folder\" tile and a \"By Artist\" tile " +
                      "(Artist → Album → Track), or force just one. The metadata view requires a " +
                      "rescan to read tags.")
        {
            EnumValues = [OrganizeByBoth, OrganizeByFolder, OrganizeByMetadata],
        },
        new(KeyExtractThumbnails, "Extract thumbnails", PluginSettingType.Bool, DefaultValue: "false",
            HelpText: "During a rescan, generate a thumbnail per file — embedded cover art for audio, " +
                      "and a video frame (requires the host's ffmpeg). Cached on disk; adds scan time."),
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new LocalFolderSource(instanceId, settings);
}
