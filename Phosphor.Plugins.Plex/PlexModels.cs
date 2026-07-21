namespace Phosphor;

/// <summary>
/// Plug-in-internal Plex item model, produced by <c>PlexService</c> and mapped to the plug-in
/// contract's <c>SourceItem</c>/<c>ResolvedStream</c> by <c>PlexMappings</c>. The plug-in never
/// references the host's rich MVVM <c>VideoItem</c> (which lives across the load boundary); this
/// carries only the fields the Plex REST layer populates and the mappings read back.
/// </summary>
public sealed class VideoItem
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string? StreamUrl { get; set; }
    public TimeSpan? Duration { get; set; }
    public DateTimeOffset? UploadDate { get; set; }
    public bool IsAudioOnly { get; set; }

    // ── Plex drill-down / audio metadata (plug-in-private) ──
    public PlexItemType PlexItemType { get; set; }
    public string? PlexRatingKey { get; set; }
    public PlexAudioStream PlexAudioStream { get; set; }
    public List<ChapterMarker>? Chapters { get; set; }

    /// <summary>Audio-selection tag surfaced to the host status bar (e.g. " (Stereo)").</summary>
    public string AudioTag => PlexAudioStream switch
    {
        PlexAudioStream.Stereo => " (Stereo)",
        PlexAudioStream.StereoTranscode => " (Stereo Transcode)",
        PlexAudioStream.Surround => " (Surround)",
        _ => ""
    };
}

/// <summary>Plug-in-internal chapter marker, mapped to the contract's <c>ChapterMarker</c>.</summary>
public sealed class ChapterMarker
{
    public string Title { get; set; } = "";
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
}

/// <summary>Plug-in-internal library selection (mirrors the host's persisted mapping shape).</summary>
public sealed class PlexLibraryMapping
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";

    /// <summary>
    /// Per-library sub-toggles keyed by sub-option id, shared with the host's source-agnostic
    /// <c>SourceLibraryMapping.SubFlags</c> JSON shape. Plex's ids are "hubs" and "playlists".
    /// </summary>
    public Dictionary<string, bool> SubFlags { get; set; } = new();

    /// <summary>Whether the "Hubs" grouping tile is shown (computed over <see cref="SubFlags"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HubsEnabled
    {
        get => SubFlags.TryGetValue("hubs", out var v) && v;
        set => SubFlags["hubs"] = value;
    }

    /// <summary>Whether the "Playlists" grouping tile is shown (computed over <see cref="SubFlags"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PlaylistsEnabled
    {
        get => SubFlags.TryGetValue("playlists", out var v) && v;
        set => SubFlags["playlists"] = value;
    }
}
