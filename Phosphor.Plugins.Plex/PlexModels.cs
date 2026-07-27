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

// ── Live TV ─────────────────────────────────────────────────────────────────

/// <summary>
/// A Plex Live TV DVR reference. A Plex server hosts zero or more DVRs (each backed by a physical
/// tuner such as an HDHomeRun), exposing an EPG provider and a live channel lineup. The
/// <see cref="Key"/> (e.g. "13") is used to tune channels; the <see cref="EpgIdentifier"/> (e.g.
/// "tv.plex.providers.epg.cloud:13") addresses the lineup/grid endpoints.
/// </summary>
public sealed class PlexDvr
{
    public string Key { get; set; } = "";
    public string EpgIdentifier { get; set; } = "";
    public string Title { get; set; } = "Live TV";
}

/// <summary>
/// One live channel in a DVR's lineup (from <c>/{epg}/lineups/dvr/channels</c>), optionally enriched
/// with the program airing right now (from the EPG <c>/grid</c>). <see cref="Id"/> is the Plex
/// channelIdentifier used to tune.
/// </summary>
public sealed class PlexLiveChannel
{
    public string Id { get; set; } = "";
    public string Vcn { get; set; } = "";
    public string Title { get; set; } = "";
    public string CallSign { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public bool IsHd { get; set; }

    /// <summary>The program on this channel right now, or <c>null</c> when the grid had nothing.</summary>
    public string? CurrentProgram { get; set; }
}

/// <summary>
/// A live-playback session opened for a channel: the tuner-holding transcode session id and the
/// resolved HLS master-manifest URL. Owned by <c>PlexLiveTvService</c> so it can keep-alive and, most
/// importantly, stop the session (releasing the physical tuner). A missed stop pins a tuner until
/// Plex's idle timeout, so the service tears down the prior session before opening a new one.
/// </summary>
public sealed class PlexLiveSession
{
    public string ChannelId { get; set; } = "";

    /// <summary>Our client-generated id used for the universal transcode (manifest + keep-alive +
    /// transcode stop). Plex spins up the playable HLS transcode under this id.</summary>
    public string PlaybackSessionId { get; set; } = "";

    /// <summary>Plex's server-assigned live-session id (from the tune Part key) — the grab operation
    /// that holds the physical tuner. Must be stopped to release the tuner.</summary>
    public string TunerSessionId { get; set; } = "";

    public string ManifestUrl { get; set; } = "";
}
