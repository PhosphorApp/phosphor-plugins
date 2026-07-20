using System.Diagnostics;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Twitch;

/// <summary>
/// Thin wrapper around the host-bundled <c>yt-dlp</c> executable. Out-of-tree plug-ins can't reach
/// the host's internal engine, so this replicates the small slice we need: resolve short-lived
/// playable URLs via <c>-g</c>, and fetch metadata via <c>--dump-single-json</c>. yt-dlp ships
/// mature Twitch extractors (<c>twitch:stream</c>, <c>twitch:vod</c>, <c>twitch:clips</c>), so the
/// format-selector shape works unchanged.
///
/// Twitch is live-first: a live channel resolves to an endless HLS manifest (no duration, no seek),
/// which we flag <see cref="ResolvedStream.IsLiveStream"/> = <c>true</c>; VODs and clips are finite
/// and seekable like ordinary video.
/// </summary>
internal sealed class YtDlpResolver(string ytDlpPath, Action<string>? log = null)
{
    private readonly string _ytDlpPath = ytDlpPath;
    private readonly Action<string>? _log = log;

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_ytDlpPath) &&
        (File.Exists(_ytDlpPath) || !_ytDlpPath.Contains(Path.DirectorySeparatorChar));

    public async Task<ResolvedStream?> ResolveAsync(
        string url, PlaybackPreferences prefs, bool isLive, CancellationToken ct = default)
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
                _log?.Invoke($"Twitch audio-only resolve failed ({aCode}) in {sw.ElapsedMilliseconds}ms: {Trim(aErr, 200)}");
                return null;
            }
            _log?.Invoke($"Twitch audio-only resolved in {sw.ElapsedMilliseconds}ms: {url}");
            return new ResolvedStream(StreamTransport.Http, StreamLayout.AudioOnly, audioUrl)
            {
                IsLiveStream = isLive,
            };
        }

        // Twitch serves adaptive HLS: a single master manifest carries muxed avc video + aac audio,
        // so VLC demuxes it as one Media (no slave track). Prefer the master manifest_url when present.
        var manifestSel = $"b{cap}/b";
        var (mCode, mOut, _) = await RunAsync(
            ["--no-warnings", "-f", manifestSel, "--print", "%(manifest_url)s", url], ct);
        var manifestUrl = FirstNonEmptyLine(mOut);
        if (mCode == 0 && manifestUrl is { Length: > 0 } && manifestUrl != "NA" &&
            manifestUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"Twitch resolved via master manifest in {sw.ElapsedMilliseconds}ms (live={isLive}): {url}");
            return new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, manifestUrl)
            {
                IsLiveStream = isLive,
            };
        }

        // Fallback: resolve a direct playable URL via -g.
        var (code, stdout, stderr) = await RunAsync(
            ["--no-warnings", "-f", $"b{cap}/b", "-g", url], ct);
        var direct = FirstNonEmptyLine(stdout);
        if (code != 0 || direct is null)
        {
            _log?.Invoke($"Twitch resolve failed ({code}) in {sw.ElapsedMilliseconds}ms: {Trim(stderr, 200)}");
            return null;
        }

        _log?.Invoke($"Twitch resolved (muxed) in {sw.ElapsedMilliseconds}ms (live={isLive}): {url}");
        return new ResolvedStream(StreamTransport.Http, StreamLayout.Muxed, direct)
        {
            IsLiveStream = isLive,
        };
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
            _log?.Invoke($"Twitch metadata parse failed: {ex.Message}");
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
