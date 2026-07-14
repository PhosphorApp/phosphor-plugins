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
    ];

    public IPhosphorSource CreateInstance(string instanceId, IReadOnlyDictionary<string, string?> settings)
        => new LocalFolderSource(instanceId, settings);
}
