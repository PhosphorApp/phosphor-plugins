using System.Diagnostics;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Dailymotion;

/// <summary>
/// Thin wrapper around the host-bundled <c>yt-dlp</c> executable. Out-of-tree plug-ins can't reach
/// the host's internal engine, so this replicates the small slice we need: resolve short-lived
/// playable URLs via <c>-g</c>, and fetch metadata via <c>--dump-single-json</c>. yt-dlp ships a
/// mature Dailymotion extractor, so the format-selector shape works unchanged.
/// </summary>
internal sealed class YtDlpResolver(string ytDlpPath, Action<string>? log = null)
{
    private readonly string _ytDlpPath = ytDlpPath;
    private readonly Action<string>? _log = log;

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_ytDlpPath) &&
        (File.Exists(_ytDlpPath) || !_ytDlpPath.Contains(Path.DirectorySeparatorChar));

    public async Task<ResolvedStream?> ResolveAsync(
        string url, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var cap = HeightCap(prefs.MaxQuality);

        if (prefs.AudioOnly)
        {
            var audioSel = prefs.PreferStereo ? "ba[audio_channels<=2]/ba" : "ba";
            var (aCode, aOut, aErr) = await RunAsync(["--no-warnings", "-f", audioSel, "-g", url], ct);
            var audioUrl = FirstNonEmptyLine(aOut);
            if (aCode != 0 || audioUrl is null)
            {
                _log?.Invoke($"Dailymotion audio-only resolve failed ({aCode}) in {sw.ElapsedMilliseconds}ms: {Trim(aErr, 200)}");
                return null;
            }
            _log?.Invoke($"Dailymotion audio-only resolved in {sw.ElapsedMilliseconds}ms: {url}");
            return new ResolvedStream(StreamTransport.Http, StreamLayout.AudioOnly, audioUrl);
        }

        // Prefer H.264 (avc) video — VLC decodes it reliably, whereas some sources (Dailymotion)
        // offer an AV1 track at the same resolution that VLC can't decode (black video, audio-only).
        // Fall back to any codec, then a muxed stream. Stereo audio preferred first when requested.
        var videoAudioSel = prefs.PreferStereo
            ? $"bv*[vcodec^=avc]{cap}+ba[audio_channels<=2]/bv*{cap}+ba[audio_channels<=2]/bv*{cap}+ba/b{cap}"
            : $"bv*[vcodec^=avc]{cap}+ba/bv*{cap}+ba/b{cap}";

        // HLS-first: many sources (Dailymotion) are HLS-only with SEPARATE video/audio tracks. Feeding
        // VLC two independent .m3u8 streams via AddSlave renders unreliably (black video). yt-dlp
        // exposes the master HLS playlist via manifest_url — it carries BOTH avc video + aac audio, so
        // VLC demuxes it as a single adaptive Media (no slave, no AV1). Prefer it when present.
        // NOTE: manifest_url only exists on a SINGLE format (a "+ba" merge has none), so probe with a
        // single video-format selector, preferring avc.
        var manifestSel = $"bv*[vcodec^=avc]{cap}/bv*{cap}/b{cap}/b";
        var (mCode, mOut, _) = await RunAsync(
            ["--no-warnings", "-f", manifestSel, "--print", "%(manifest_url)s", url], ct);
        var manifestUrl = FirstNonEmptyLine(mOut);
        if (mCode == 0 && manifestUrl is { Length: > 0 } && manifestUrl != "NA" &&
            manifestUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"Dailymotion resolved via master manifest in {sw.ElapsedMilliseconds}ms: {url}");
            return new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, manifestUrl);
        }

        // Fallback: non-HLS/progressive sources — resolve direct URL(s) via -g.
        var (code, stdout, stderr) = await RunAsync(
            ["--no-warnings", "-f", videoAudioSel, "-g", "--print", "%(width)sx%(height)s", url], ct);

        var lines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (code != 0 || lines.Count < 2)
        {
            _log?.Invoke($"Dailymotion resolve failed ({code}) in {sw.ElapsedMilliseconds}ms, lines={lines.Count}: {Trim(stderr, 200)}");
            return null;
        }

        var resolution = lines[0];
        _log?.Invoke($"Dailymotion resolved in {sw.ElapsedMilliseconds}ms ({(lines.Count >= 3 ? "separate" : "muxed")}, {resolution}): {url}");
        return lines.Count >= 3
            ? new ResolvedStream(StreamTransport.Http, StreamLayout.SeparateVideoAudio, lines[1], lines[2], resolution)
            : new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, lines[1], Resolution: resolution);
    }

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
            _log?.Invoke($"Dailymotion metadata parse failed: {ex.Message}");
            return null;
        }
    }

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

    private static string HeightCap(VideoQuality q) => q switch
    {
        VideoQuality.Low => "[height<=480]",
        VideoQuality.Medium => "[height<=720]",
        VideoQuality.High => "[height<=1080]",
        _ => "",
    };

    private static string? FirstNonEmptyLine(string s) => s
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
