using System.Net.Http;
using System.Runtime.CompilerServices;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos;

namespace Phosphor.Search;

/// <summary>
/// <see cref="ISearchEngine"/> backed by YoutubeExplode. Wraps the exact search, playlist,
/// and channel logic that previously lived inline in <c>JukeboxViewModel</c>, including the
/// <c>IVideo</c> → <see cref="VideoItem"/> mapping and the playlist-id / channel-handle
/// resolution fallbacks, so routing the ViewModel through the engine is behavior-identical.
/// </summary>
public sealed class YoutubeExplodeSearchEngine : ISearchEngine
{
    private readonly YoutubeClient _youtube;

    public YoutubeExplodeSearchEngine(HttpClient? http = null)
    {
        _youtube = http != null ? new YoutubeClient(http) : new YoutubeClient();
    }

    /// <summary>Always available — runs in-process.</summary>
    public bool IsAvailable => true;

    public IAsyncEnumerable<VideoItem> SearchVideosAsync(string query, CancellationToken ct = default)
        => MapVideos(_youtube.Search.GetVideosAsync(query), ct);

    public IAsyncEnumerable<VideoItem> GetPlaylistVideosAsync(string playlistId, CancellationToken ct = default)
        => MapVideos(_youtube.Playlists.GetVideosAsync(playlistId), ct);

    public async IAsyncEnumerable<VideoItem> GetChannelUploadsAsync(
        string handleOrUser, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Try handle first (e.g. "@vpinworkshop"), then fall back to a legacy user name.
        YoutubeExplode.Channels.Channel channel;
        try
        {
            channel = await _youtube.Channels.GetByHandleAsync(handleOrUser, ct);
        }
        catch
        {
            channel = await _youtube.Channels.GetByUserAsync(handleOrUser, ct);
        }

        await foreach (var item in MapVideos(_youtube.Channels.GetUploadsAsync(channel.Id), ct))
            yield return item;
    }

    public async Task<string?> ResolvePlaylistIdAsync(
        string nameIdOrUrl,
        Action<string>? onFoundByName = null,
        CancellationToken ct = default)
    {
        // Try as a direct playlist id / URL first.
        try
        {
            return YoutubeExplode.Playlists.PlaylistId.Parse(nameIdOrUrl).Value;
        }
        catch
        {
            // Not an id — search for the playlist by name and take the first match.
            await foreach (var result in _youtube.Search.GetPlaylistsAsync(nameIdOrUrl).WithCancellation(ct))
            {
                onFoundByName?.Invoke(result.Title);
                return result.Id.Value;
            }
            return null;
        }
    }

    /// <summary>Maps YoutubeExplode <see cref="IVideo"/> results to <see cref="VideoItem"/>.</summary>
    private static async IAsyncEnumerable<VideoItem> MapVideos<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken ct = default) where T : IVideo
    {
        await foreach (var video in source.WithCancellation(ct))
        {
            yield return new VideoItem
            {
                Title = video.Title ?? "",
                Author = video.Author?.ChannelTitle ?? "",
                ThumbnailUrl = video.Thumbnails?.GetWithHighestResolution()?.Url ?? "",
                VideoId = video.Id,
                Duration = video.Duration,
            };
        }
    }
}
