using System.Diagnostics;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Vimeo;

/// <summary>
/// Thin wrapper around the host-bundled <c>yt-dlp</c> executable. Out-of-tree plug-ins can't reach
/// the host's internal <c>YtDlpVideoEngine</c>, so this replicates the small slice we need:
/// resolve short-lived playable URLs via <c>-g</c>, and fetch metadata via <c>--dump-single-json</c>.
/// The format-selector shape mirrors the in-box YouTube engine (separate video+audio preferred,
/// muxed fallback), which works identically for yt-dlp's Vimeo extractor.
/// </summary>
internal sealed class YtDlpResolver(string ytDlpPath, Action<string>? log = null)
{
    private readonly string _ytDlpPath = ytDlpPath;
    private readonly Action<string>? _log = log;

    /// <summary>Whether the resolved yt-dlp path actually exists (or is a bare PATH command).</summary>
    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_ytDlpPath) &&
        (File.Exists(_ytDlpPath) || !_ytDlpPath.Contains(Path.DirectorySeparatorChar));

    /// <summary>
    /// Resolves a playable stream for a Vimeo URL. Prefers separate best video + best audio (stereo
    /// first when requested), falling back to a muxed stream — a single yt-dlp invocation.
    /// Output: [resolution, videoUrl, audioUrl] (separate) or [resolution, muxedUrl] (muxed).
    /// </summary>
    public async Task<ResolvedStream?> ResolveAsync(
        string url, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cap = HeightCap(prefs.MaxQuality);

        if (prefs.AudioOnly)
        {
            var audioSel = prefs.PreferStereo ? "ba[audio_channels<=2]/ba" : "ba";
            var (aCode, aOut, aErr) = await RunAsync(["--no-warnings", "-f", audioSel, "-g", url], ct);
            var audioUrl = FirstNonEmptyLine(aOut);
            if (aCode != 0 || audioUrl is null)
            {
                _log?.Invoke($"Vimeo audio-only resolve failed ({aCode}) in {sw.ElapsedMilliseconds}ms: {Trim(aErr, 200)}");
                return null;
            }
            _log?.Invoke($"Vimeo audio-only resolved in {sw.ElapsedMilliseconds}ms: {url}");
            return new ResolvedStream(StreamTransport.Http, StreamLayout.AudioOnly, audioUrl);
        }

        var videoAudioSel = prefs.PreferStereo
            ? $"bv*{cap}+ba[audio_channels<=2]/bv*{cap}+ba/b{cap}"
            : $"bv*{cap}+ba/b{cap}";
        var (code, stdout, stderr) = await RunAsync(
            ["--no-warnings", "-f", videoAudioSel, "-g", "--print", "%(width)sx%(height)s", url], ct);

        var lines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (code != 0 || lines.Count < 2)
        {
            _log?.Invoke($"Vimeo resolve failed ({code}) in {sw.ElapsedMilliseconds}ms, lines={lines.Count}: {Trim(stderr, 200)}");
            return null;
        }

        var resolution = lines[0];
        _log?.Invoke($"Vimeo resolved in {sw.ElapsedMilliseconds}ms ({(lines.Count >= 3 ? "separate" : "muxed")}, {resolution}): {url}");
        return lines.Count >= 3
            ? new ResolvedStream(StreamTransport.Http, StreamLayout.SeparateVideoAudio, lines[1], lines[2], resolution)
            : new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, lines[1], Resolution: resolution);
    }

    /// <summary>Fetches duration + description via <c>--dump-single-json</c>.</summary>
    public async Task<SourceMetadata?> GetMetadataAsync(string url, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunAsync(["--no-warnings", "--dump-single-json", url], ct);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            var root = JsonDocument.Parse(stdout).RootElement;
            TimeSpan? duration = root.TryGetProperty("duration", out var d) && d.TryGetDouble(out var secs)
                ? TimeSpan.FromSeconds(secs) : null;
            var description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null;
            return new SourceMetadata(duration, description, []);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Vimeo metadata parse failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetches just the title (best-effort) for browse enrichment.</summary>
    public async Task<string?> GetTitleAsync(string url, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunAsync(["--no-warnings", "--print", "%(title)s", url], ct);
        return code == 0 ? FirstNonEmptyLine(stdout) : null;
    }

    // ── process plumbing ─────────────────────────────────────────────────────

    private async Task<(int code, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, await outTask, await errTask);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    // yt-dlp height cap for the coarse quality ceiling.
    private static string HeightCap(VideoQuality q) => q switch
    {
        VideoQuality.Low => "[height<=480]",
        VideoQuality.Medium => "[height<=720]",
        VideoQuality.High => "[height<=1080]",
        _ => "", // Max: no cap
    };

    private static string? FirstNonEmptyLine(string s) => s
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
