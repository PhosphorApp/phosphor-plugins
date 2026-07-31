namespace Phosphor.Search;

/// <summary>
/// Abstraction over a YouTube <em>discovery</em> backend: free-text search, playlist and
/// channel enumeration, and playlist-id resolution. This is the switch point that lets the
/// app use either YoutubeExplode or (later) yt-dlp for search, independent of the video
/// (resolve/download) path.
/// </summary>
/// <remarks>
/// Results are yielded incrementally as <see cref="VideoItem"/> to preserve the app's
/// live pagination UX. The engine owns the mapping from its native result type to
/// <see cref="VideoItem"/>. Plex is orthogonal and does not use this seam.
/// </remarks>
public interface ISearchEngine
{
    /// <summary>
    /// Whether this engine can actually run in the current environment. In-process engines
    /// (YoutubeExplode) are always available; external ones (yt-dlp) report false when their
    /// executable is missing, so the factory can fall back to an available engine. This is a
    /// general capability hook — future engines report their own readiness however they need.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Incrementally yields videos matching a free-text query.</summary>
    IAsyncEnumerable<VideoItem> SearchVideosAsync(string query, CancellationToken ct = default);

    /// <summary>Incrementally yields the videos of a playlist (by resolved playlist id).</summary>
    IAsyncEnumerable<VideoItem> GetPlaylistVideosAsync(string playlistId, CancellationToken ct = default);

    /// <summary>Incrementally yields a channel's uploads (by handle or user name).</summary>
    IAsyncEnumerable<VideoItem> GetChannelUploadsAsync(string handleOrUser, CancellationToken ct = default);

    /// <summary>
    /// Incrementally yields <em>channels</em> matching a free-text query (browsable containers, not
    /// videos). Backs the <c>channels:</c> search prefix so the user can find and favorite a channel.
    /// </summary>
    IAsyncEnumerable<ChannelOrPlaylistItem> SearchChannelsAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Incrementally yields <em>playlists</em> matching a free-text query (browsable containers, not
    /// videos). Backs playlist-row discovery so the user can find and favorite a playlist.
    /// </summary>
    IAsyncEnumerable<ChannelOrPlaylistItem> SearchPlaylistsAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Resolves a playlist id from a raw id, URL, or a name to search for. Returns the
    /// canonical playlist id, or <c>null</c> if a name search found nothing.
    /// </summary>
    /// <param name="nameIdOrUrl">A playlist id/URL, or a name to search by.</param>
    /// <param name="onFoundByName">
    /// Invoked with the matched playlist's title when resolution happened via name search
    /// (lets the caller surface "Found playlist: X" status). Not called for direct ids.
    /// </param>
    Task<string?> ResolvePlaylistIdAsync(
        string nameIdOrUrl,
        Action<string>? onFoundByName = null,
        CancellationToken ct = default);
}
