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

/// <summary>
/// Diagnostic (dev-only) play/fail counters, persisted with the unplayable set so we can gauge how
/// much SoundCloud content actually resolves versus fails (mostly DRM). Not surfaced in the UI.
/// </summary>
public sealed class ScStats
{
    public int Attempts { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    /// <summary>Failures that are intrinsic to the track (DRM/no-formats) — these mark it unplayable.</summary>
    public int DefinitiveFailures { get; set; }
    /// <summary>Failures that are transient (network/timeout) — counted but never mark a track bad.</summary>
    public int TransientFailures { get; set; }
}

/// <summary>On-disk shape for the plug-in's lazy-discovery state: the unplayable id set + stats.</summary>
public sealed class UnplayableDoc
{
    public List<string>? Ids { get; set; }
    public ScStats? Stats { get; set; }
}
