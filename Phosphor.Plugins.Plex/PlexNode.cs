namespace Phosphor.Plugins.Plex;

/// <summary>
/// Kinds of node in the Plex browse tree. Each maps to a distinct <see cref="PlexService"/>
/// call in <c>PlexSource.BrowseAsync</c>. This mirrors the drill-down the ViewModel does
/// today (library → artists/albums/tracks, plus hubs and playlists), but expressed as
/// generic <see cref="Phosphor.Plugin.Abstractions.SourceCategory"/> nodes.
/// </summary>
internal enum PlexNodeKind
{
    /// <summary>The whole server as a single root tile (Single Tile mode) — expands to the
    /// configured libraries (and Live TV) as sub-categories. <see cref="PlexNode.Key"/> is unused.</summary>
    ServerRoot,
    /// <summary>A library section (music "artist" type, or video). Root-level tile.</summary>
    Library,
    /// <summary>An artist — expands to its albums.</summary>
    Artist,
    /// <summary>An album — expands to its tracks.</summary>
    Album,
    /// <summary>A TV show — expands to its seasons.</summary>
    Show,
    /// <summary>A TV season — expands to its episodes (playable leaves).</summary>
    Season,
    /// <summary>The "Hubs" grouping under a library — expands to the library's hubs.</summary>
    HubList,
    /// <summary>A single hub — expands to its items.</summary>
    Hub,
    /// <summary>The "Playlists" grouping under a library — expands to the library's playlists.</summary>
    PlaylistList,
    /// <summary>A single playlist — expands to its items.</summary>
    Playlist,
    /// <summary>A Live TV tile — expands to the DVR's live channel lineup. <see cref="PlexNode.Key"/>
    /// carries the DVR key. Root-level tile, presented like a library.</summary>
    LiveTv,
}

/// <summary>
/// Internal descriptor carried in <see cref="Phosphor.Plugin.Abstractions.SourceCategory.SourceState"/>
/// so a browse node knows exactly which Plex call to make when expanded. Opaque to the host.
/// </summary>
/// <param name="Kind">Which kind of Plex node this is.</param>
/// <param name="Key">The Plex key/ratingKey/hubKey the node addresses (meaning depends on <paramref name="Kind"/>).</param>
/// <param name="LibraryType">The owning library's type ("artist" for music, else video); drives child shapes.</param>
internal sealed record PlexNode(PlexNodeKind Kind, string Key, string? LibraryType = null);

/// <summary>
/// Identifies a live channel to play: the DVR key plus the Plex channelIdentifier. Carried in a live
/// channel <see cref="Phosphor.Plugin.Abstractions.SourceItem.SourceState"/> so the resolver can open
/// a tuner session without re-browsing. Opaque to the host.
/// </summary>
internal sealed record PlexLiveRef(string DvrKey, string ChannelId);