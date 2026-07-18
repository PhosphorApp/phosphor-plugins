namespace Phosphor.Plugins.SoundCloud;

/// <summary>
/// A lightweight SoundCloud track as surfaced by yt-dlp's keyless <c>scsearch</c> extractor.
/// <see cref="Url"/> is the canonical soundcloud.com link the yt-dlp resolver plays.
/// </summary>
public sealed record ScTrack(
    string Id,
    string Title,
    string Url,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    string? Uploader);
