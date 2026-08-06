using Phosphor.Plugin.Abstractions;
using PluginChapterMarker = Phosphor.Plugin.Abstractions.ChapterMarker;

namespace Phosphor.Plugins.Plex;

/// <summary>
/// Adapts the host's Plex types (<see cref="VideoItem"/>, <see cref="PlexLibrary"/>,
/// <see cref="PlexHub"/>, <see cref="PlexPlaylist"/>) to the plug-in abstraction types.
/// Pure, behavior-preserving translation — the Plex REST logic stays in
/// <see cref="PlexService"/>; this only shapes data for the plug-in contract.
/// </summary>
internal static class PlexMappings
{
    // ── Items ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a Plex <see cref="VideoItem"/> to a <see cref="SourceItem"/>. Container items
    /// (artist/album) become <see cref="SourceItem.IsContainer"/> so the host drills in via
    /// <see cref="IBrowsable"/>; playable items carry their pre-built stream through
    /// <see cref="SourceItem.SourceState"/>.
    /// </summary>
    public static SourceItem ToSourceItem(VideoItem v, string instanceId)
    {
        bool isContainer = v.PlexItemType is PlexItemType.Artist or PlexItemType.Album
            or PlexItemType.Show or PlexItemType.Season;
        // Containers (artist/album/show/season) drill in via IBrowsable, so they must carry a PlexNode
        // the host hands back to BrowseAsync — NOT the VideoItem (which is for playback/resolve of
        // leaves). Artists/albums live in music ("artist") libraries; shows/seasons in TV ("show").
        object? state = v.PlexItemType switch
        {
            PlexItemType.Artist => new PlexNode(PlexNodeKind.Artist, v.PlexRatingKey ?? "", "artist"),
            PlexItemType.Album => new PlexNode(PlexNodeKind.Album, v.PlexRatingKey ?? "", "artist"),
            PlexItemType.Show => new PlexNode(PlexNodeKind.Show, v.PlexRatingKey ?? "", "show"),
            PlexItemType.Season => new PlexNode(PlexNodeKind.Season, v.PlexRatingKey ?? "", "show"),
            _ => v,
        };
        return new SourceItem
        {
            SourceInstanceId = instanceId,
            ItemId = v.VideoId,
            Title = v.Title,
            Subtitle = string.IsNullOrEmpty(v.Author) ? null : v.Author,
            ThumbnailUrl = v.ThumbnailUrl,
            IsAudioOnly = v.IsAudioOnly,
            IsContainer = isContainer,
            Duration = v.Duration,
            Chapters = v.Chapters?.Select(ToPluginChapter).ToList(),
            // Leaves keep the whole source VideoItem so resolve/metadata need no re-fetch; containers
            // carry a PlexNode (above) so the host can drill into them.
            SourceState = state,
            // Durable identity the host persists (queue.json) and hands back on later round-trips — the
            // rating key is all GetMetadataAsync needs to fetch on-demand chapters after a restart, when
            // the live SourceState object is gone. Leaves only (containers rebuild from their PlexNode).
            SourceStateToken = isContainer ? null : v.PlexRatingKey,
        };
    }

    /// <summary>Recovers the source <see cref="VideoItem"/> from a <see cref="SourceItem"/>.</summary>
    public static VideoItem? VideoItemOf(SourceItem item) => item.SourceState as VideoItem;

    // ── Categories ─────────────────────────────────────────────────────────────

    /// <summary>Maps a configured library mapping to a root <see cref="SourceCategory"/>. The
    /// instance <paramref name="displayNamePrefix"/> is prepended to the tile title (e.g.
    /// "Plex Movies") so libraries don't collide with same-named tiles from other servers/sources.</summary>
    public static SourceCategory ToRootCategory(PlexLibraryMapping lib, string instanceId, string? displayNamePrefix = null) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = $"library:{lib.Key}",
        Title = string.IsNullOrWhiteSpace(displayNamePrefix) ? lib.Title : $"{displayNamePrefix} {lib.Title}",
        Icon = lib.Type == "artist" ? "🎵" : "🎬",
        HasSubCategories = true,
        SourceState = new PlexNode(PlexNodeKind.Library, lib.Key, lib.Type),
    };

    /// <summary>Maps a container <see cref="VideoItem"/> (artist/album/hub/playlist) to a browse node.</summary>
    public static SourceCategory ToCategory(VideoItem v, string instanceId, PlexNode node) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = v.VideoId,
        Title = v.Title,
        ThumbnailUrl = v.ThumbnailUrl,
        Icon = node.Kind switch
        {
            PlexNodeKind.Artist => "🎤",
            PlexNodeKind.Album => "💿",
            PlexNodeKind.Show => "📺",
            PlexNodeKind.Season => "🗂️",
            _ => null,
        },
        HasSubCategories = node.Kind is not (PlexNodeKind.Album or PlexNodeKind.Season), // albums/seasons expand straight to leaves
        SourceState = node,
    };

    /// <summary>Maps a Plex hub to a browse node.</summary>
    public static SourceCategory ToCategory(PlexHub hub, string instanceId) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = $"hub:{hub.HubKey}",
        Title = hub.Title,
        Icon = "⭐",
        HasSubCategories = false,
        SourceState = new PlexNode(PlexNodeKind.Hub, hub.HubKey, hub.Type),
    };

    /// <summary>Maps a Plex playlist to a browse node.</summary>
    public static SourceCategory ToCategory(PlexPlaylist pl, string instanceId) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = $"playlist:{pl.RatingKey}",
        Title = pl.Title,
        ThumbnailUrl = pl.Thumb,
        Icon = "🎶",
        HasSubCategories = false,
        SourceState = new PlexNode(PlexNodeKind.Playlist, pl.RatingKey),
    };

    // ── Live TV ────────────────────────────────────────────────────────────────

    /// <summary>Maps a Live TV DVR to a root <see cref="SourceCategory"/> tile (presented like a
    /// library). The <paramref name="displayNamePrefix"/> is prepended so it reads e.g. "Plex Live TV".</summary>
    public static SourceCategory LiveTvRootCategory(PlexDvr dvr, string instanceId, string? displayNamePrefix = null) => new()
    {
        SourceInstanceId = instanceId,
        CategoryId = $"livetv:{dvr.Key}",
        Title = string.IsNullOrWhiteSpace(displayNamePrefix) ? "Live TV" : $"{displayNamePrefix} Live TV",
        Icon = "📺",
        HasSubCategories = true,
        SourceState = new PlexNode(PlexNodeKind.LiveTv, dvr.Key, PlexSourceProvider.LiveTvType),
    };

    /// <summary>Maps a live channel to a playable leaf <see cref="SourceItem"/>. The channel id is
    /// carried in <see cref="SourceItem.SourceState"/> so <c>ResolveAsync</c> can tune it. The title
    /// is enriched with the current program when the grid provided one (e.g. "2.1 CBS – News").</summary>
    public static SourceItem LiveChannelToSourceItem(PlexLiveChannel ch, string dvrKey, string instanceId, bool unavailable = false)
    {
        var name = string.IsNullOrEmpty(ch.Vcn) ? ch.Title : $"{ch.Vcn} {ch.Title}";
        var title = string.IsNullOrEmpty(ch.CurrentProgram) ? name : $"{name} – {ch.CurrentProgram}";
        return new SourceItem
        {
            SourceInstanceId = instanceId,
            ItemId = $"livetv:{dvrKey}:{ch.Id}",
            Title = title,
            Subtitle = string.IsNullOrEmpty(ch.CallSign) ? null : ch.CallSign,
            ThumbnailUrl = ch.ThumbnailUrl,
            IsContainer = false,
            IsLiveStream = true,
            ShowLiveBadge = false, // all channels are live; don't badge every row
            ShowUnavailableBadge = unavailable,
            // Carry the DVR key + channel id so the resolver can open a live session.
            SourceState = new PlexLiveRef(dvrKey, ch.Id),
        };
    }

    /// <summary>
    /// Maps a resolved Plex <see cref="VideoItem"/> to a <see cref="ResolvedStream"/>. Plex
    /// items carry a ready-to-play HTTP <see cref="VideoItem.StreamUrl"/>, so there is no
    /// separate resolution step — audio tracks are audio-only, everything else muxed.
    /// </summary>
    public static ResolvedStream? ToResolvedStream(VideoItem v)
    {
        if (string.IsNullOrEmpty(v.StreamUrl)) return null;
        var layout = v.IsAudioOnly ? StreamLayout.AudioOnly : StreamLayout.Muxed;
        return new ResolvedStream(StreamTransport.Http, layout, v.StreamUrl, null, null)
        {
            // Surface the stereo/surround selection so the host status bar can show it.
            AudioTag = string.IsNullOrEmpty(v.AudioTag) ? null : v.AudioTag,
        };
    }

    /// <summary>Builds <see cref="SourceMetadata"/> from a Plex item's known duration + chapters.</summary>
    public static SourceMetadata ToSourceMetadata(VideoItem v) => new(
        v.Duration,
        null,
        v.Chapters?.Select(ToPluginChapter).ToList() ?? [],
        v.UploadDate);

    private static PluginChapterMarker ToPluginChapter(Phosphor.ChapterMarker c) =>
        new(c.Title, c.StartTime, c.EndTime);
}
