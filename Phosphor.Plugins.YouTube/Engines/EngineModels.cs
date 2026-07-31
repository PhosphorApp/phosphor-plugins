namespace Phosphor;

/// <summary>
/// Lightweight plug-in-internal video result produced by the YouTube search/discovery engines
/// and mapped straight to the plug-in contract's <c>SourceItem</c> by <c>YouTubeMappings</c>.
/// This is a trimmed copy of the fields the engines actually populate — the plug-in never
/// references the host's rich MVVM <c>VideoItem</c> (which lives across the load boundary).
/// </summary>
public sealed class VideoItem
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string VideoId { get; set; } = "";
    public TimeSpan? Duration { get; set; }
    public DateTimeOffset? UploadDate { get; set; }
}

/// <summary>Whether a <see cref="ChannelOrPlaylistItem"/> describes a channel or a playlist.</summary>
public enum ChannelPlaylistKind
{
    Channel,
    Playlist,
}

/// <summary>
/// Lightweight plug-in-internal discovery result describing a YouTube <em>channel</em> or
/// <em>playlist</em> (a browsable container), as opposed to a playable <see cref="VideoItem"/>.
/// Produced by the search engines' channel/playlist search and mapped to a container
/// <c>SourceItem</c> by <c>YouTubeMappings</c>.
/// </summary>
public sealed class ChannelOrPlaylistItem
{
    /// <summary>The channel id (e.g. <c>UC…</c>) or playlist id (e.g. <c>PL…</c>).</summary>
    public string Id { get; set; } = "";
    public ChannelPlaylistKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Channel/playlist owner or author, when available.</summary>
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    /// <summary>Video/subscriber count when the backend reports one, else null.</summary>
    public long? ItemCount { get; set; }
}

/// <summary>
/// Plug-in-internal chapter marker used by the video engines' metadata results. Mapped to the
/// contract's <c>ChapterMarker</c> by <c>YouTubeMappings</c>.
/// </summary>
public sealed class ChapterMarker
{
    public string Title { get; set; } = "";
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
}
