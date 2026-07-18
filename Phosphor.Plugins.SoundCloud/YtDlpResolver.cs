using System.Diagnostics;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.SoundCloud;

/// <summary>
/// Thin wrapper around the host-bundled <c>yt-dlp</c> executable. Out-of-tree plug-ins can't reach
/// the host's internal engine, so this replicates the small slice we need. SoundCloud is
/// <em>audio-only</em>, and — unlike Dailymotion — has no keyless REST API, so yt-dlp does double
/// duty here: it both <em>discovers</em> tracks (via its keyless <c>scsearch</c> extractor, which
/// auto-derives a client_id) and <em>resolves</em> them to a short-lived HLS/progressive URL.
/// </summary>
internal sealed class YtDlpResolver(string ytDlpPath, Action<string>? log = null)
{
    private readonly string _ytDlpPath = ytDlpPath;
    private readonly Action<string>? _log = log;

    // A field separator unlikely to appear in titles/uploaders, used with --print.
    private const string Sep = "\u001f";

    // Fields printed per flat-playlist row: id, title, duration, canonical URL, thumbnail, uploader.
    private const string PrintTemplate =
        "%(id)s\u001f%(title)s\u001f%(duration)s\u001f%(webpage_url)s\u001f%(thumbnails.0.url)s\u001f%(uploader)s";

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_ytDlpPath) &&
        (File.Exists(_ytDlpPath) || !_ytDlpPath.Contains(Path.DirectorySeparatorChar));

    /// <summary>
    /// Runs a keyless SoundCloud search (<c>scsearchN:query</c>) and yields lightweight track rows,
    /// resolving each track's stream lazily later (deferred). No client_id/token required.
    /// </summary>
    public async Task<IReadOnlyList<ScTrack>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var target = $"scsearch{Math.Clamp(limit, 1, 100)}:{query}";
        return await FlatListAsync(target, limit, ct);
    }

    /// <summary>
    /// Fetches a track by its canonical SoundCloud URL, for reconstructing a favorite not seen this
    /// session (uses <c>--dump-single-json</c>, flat).
    /// </summary>
    public async Task<ScTrack?> GetTrackAsync(string url, CancellationToken ct = default)
    {
        var (code, stdout, _) = await RunAsync(
            ["--no-warnings", "--flat-playlist", "--print", PrintTemplate, url], ct);
        if (code != 0) return null;
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return line is null ? null : ParseRow(line);
    }

    private async Task<IReadOnlyList<ScTrack>> FlatListAsync(
        string target, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (code, stdout, stderr) = await RunAsync(
            ["--no-warnings", "--flat-playlist", "--playlist-end", Math.Clamp(limit, 1, 100).ToString(),
             "--print", PrintTemplate, target], ct);
        if (code != 0)
        {
            _log?.Invoke($"SoundCloud list '{target}' failed ({code}) in {sw.ElapsedMilliseconds}ms: {Trim(stderr, 200)}");
            return [];
        }
        var items = new List<ScTrack>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = ParseRow(line);
            if (t is not null) items.Add(t);
        }
        _log?.Invoke($"SoundCloud list '{target}': {items.Count} in {sw.ElapsedMilliseconds}ms.");
        return items;
    }

    private static ScTrack? ParseRow(string line)
    {
        var parts = line.Split(Sep);
        if (parts.Length < 4) return null;
        var id = parts[0];
        if (string.IsNullOrEmpty(id) || id == "NA") return null;

        var title = string.IsNullOrEmpty(parts[1]) || parts[1] == "NA" ? $"SoundCloud {id}" : parts[1];
        TimeSpan? duration = double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var secs)
            && secs > 0 ? TimeSpan.FromSeconds(secs) : null;
        var url = !string.IsNullOrEmpty(parts[3]) && parts[3] != "NA"
            ? parts[3]
            : $"https://soundcloud.com/tracks/{id}";
        var thumb = parts.Length > 4 && !string.IsNullOrEmpty(parts[4]) && parts[4] != "NA" ? parts[4] : null;
        var uploader = parts.Length > 5 && !string.IsNullOrEmpty(parts[5]) && parts[5] != "NA" ? parts[5] : null;

        return new ScTrack(id, title, url, duration, thumb, uploader);
    }

    public async Task<ResolvedStream?> ResolveAsync(
        string url, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // SoundCloud is audio-only. Prefer a stereo (2.1) track when requested — pinball cabs route
        // surround channels to mechanical/exciter hardware — else best audio.
        var audioSel = prefs.PreferStereo ? "ba[audio_channels<=2]/ba" : "ba";
        var (code, stdout, stderr) = await RunAsync(
            ["--no-warnings", "-f", audioSel, "-g", url], ct);
        var audioUrl = FirstNonEmptyLine(stdout);
        if (code != 0 || audioUrl is null)
        {
            _log?.Invoke($"SoundCloud resolve failed ({code}) in {sw.ElapsedMilliseconds}ms: {Trim(stderr, 200)}");
            return null;
        }
        _log?.Invoke($"SoundCloud resolved audio in {sw.ElapsedMilliseconds}ms: {url}");
        return new ResolvedStream(StreamTransport.Http, StreamLayout.AudioOnly, audioUrl);
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
            _log?.Invoke($"SoundCloud metadata parse failed: {ex.Message}");
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
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
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

    private static string? FirstNonEmptyLine(string s) => s
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
