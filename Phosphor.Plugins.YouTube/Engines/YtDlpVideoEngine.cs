using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Video;

/// <summary>
/// <see cref="IVideoEngine"/> backed by the external <c>yt-dlp.exe</c>.
/// </summary>
/// <remarks>
/// Both paths are native yt-dlp: <see cref="DownloadStreamsAsync"/> (used by
/// <c>VideoCache</c> / <c>PrefetchCache</c>) downloads separate best video-only and
/// audio-only streams into the destination dir, and the caches mux them exactly as
/// before (the seam contract is unchanged). <see cref="ResolveStreamsAsync"/> (live
/// playback) resolves short-lived playable URLs via <c>-g</c>.
/// </remarks>
public sealed class YtDlpVideoEngine : IVideoEngine
{
    private readonly string _ytDlpPath;
    private readonly PluginLog? _log;

    public YtDlpVideoEngine(string? ytDlpPath = null, PluginLog? log = null)
    {
        _ytDlpPath = ytDlpPath ?? ResolveYtDlpPath();
        _log = log;
    }

    /// <summary>Available only when the yt-dlp executable is present.</summary>
    public bool IsAvailable => File.Exists(_ytDlpPath);

    /// <summary>
    /// Locates <c>yt-dlp.exe</c> next to the app (copied via csproj, like ffmpeg.exe),
    /// falling back to whatever is on PATH.
    /// </summary>
    public static string ResolveYtDlpPath()
    {
        var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
        return File.Exists(local) ? local : "yt-dlp";
    }

    /// <summary>
    /// Resolves short-lived playable stream URLs natively via yt-dlp <c>-g</c>. A single
    /// invocation yields the "WxH" resolution (first line, non-audio) followed by the
    /// playable URL(s): two lines for separate video+audio, one for a muxed fallback.
    /// URLs are time-limited and IP-bound, so this is resolved fresh for each play.
    /// </summary>
    public async Task<VideoStreams?> ResolveStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        bool audioOnly,
        CancellationToken ct = default)
    {
        var url = ToWatchUrl(videoId);
        var cap = HeightCap(quality);

        // Audio-only: a single URL, no resolution needed.
        if (audioOnly)
        {
            var audioSel = preferStereo ? "ba[audio_channels<=2]/ba" : "ba";
            var (aCode, aOut, aErr) = await RunAsync(new[]
            {
                "--no-warnings", "-f", audioSel, "-g", url,
            }, ct);

            var audioUrl = FirstNonEmptyLine(aOut);
            if (aCode != 0 || audioUrl == null)
            {
                _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine", $"audio-only resolve failed ({aCode}): {Trim(aErr)}");
                return null;
            }
            return new VideoStreams(VideoStreamKind.AudioOnly, audioUrl, null, "");
        }

        // Non-audio: prefer separate video+audio (stereo audio first if requested), fall
        // back to a muxed stream — all in one invocation. The fallback chain is built with
        // explicit "video+audio" tiers so the muxed tier stays last; reusing the audio-only
        // selector here would inject an unintended bare-audio tier before muxed.
        // Output: [resolution, videoUrl, audioUrl] (separate, 3 lines) or
        //         [resolution, muxedUrl] (muxed fallback, 2 lines).
        var videoAudioSel = preferStereo
            ? $"bv*{cap}+ba[audio_channels<=2]/bv*{cap}+ba/b{cap}"
            : $"bv*{cap}+ba/b{cap}";
        var (code, stdout, stderr) = await RunAsync(new[]
        {
            "--no-warnings", "-f", videoAudioSel, "-g",
            "--print", "%(width)sx%(height)s", url,
        }, ct);

        var lines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (code != 0 || lines.Count < 2)
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine", $"live resolve failed ({code}), lines={lines.Count}: {Trim(stderr)}");
            return null;
        }

        var resolution = lines[0];

        if (lines.Count >= 3)
        {
            // Separate video-only + audio-only streams.
            return new VideoStreams(VideoStreamKind.SeparateVideoAudio, lines[1], lines[2], resolution);
        }

        // Muxed fallback (resolution + single URL).
        return new VideoStreams(VideoStreamKind.Muxed, lines[1], null, resolution);
    }

    public async Task<VideoDownload?> DownloadStreamsAsync(
        string videoId,
        VideoQualityPreference quality,
        bool preferStereo,
        string destinationDir,
        CancellationToken ct = default)
    {
        var url = ToWatchUrl(videoId);

        // Download best video-only and best audio-only streams separately, mirroring the
        // YoutubeExplode engine's output shape so the caches mux exactly as before.
        var videoFormat = $"bv*{HeightCap(quality)}";
        var audioFormat = preferStereo ? "ba[audio_channels<=2]/ba" : "ba";

        var videoPath = await DownloadOneAsync(url, videoFormat,
            Path.Combine(destinationDir, "%(id)s_video.%(ext)s"), ct);
        if (videoPath == null) return null;

        var audioPath = await DownloadOneAsync(url, audioFormat,
            Path.Combine(destinationDir, "%(id)s_audio.%(ext)s"), ct);
        if (audioPath == null)
        {
            TryDelete(videoPath);
            return null;
        }

        var resolution = await GetResolutionAsync(url, videoFormat, ct);

        return new VideoDownload(
            videoPath,
            audioPath,
            GetExtension(videoPath),
            GetExtension(audioPath),
            resolution);
    }

    /// <summary>
    /// Fetches metadata via <c>--dump-single-json</c>: duration, description, and yt-dlp's
    /// <em>native</em> structured chapter markers. When a video has no native chapters, the
    /// list is empty and the caller falls back to parsing the description.
    /// </summary>
    public async Task<VideoMetadata?> GetMetadataAsync(string videoId, CancellationToken ct = default)
    {
        var url = ToWatchUrl(videoId);
        var (code, stdout, stderr) = await RunAsync(new[]
        {
            "--no-warnings", "--dump-single-json", url,
        }, ct);

        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine", $"metadata failed ({code}): {Trim(stderr)}");
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<YtDlpMetaJson>(stdout);
            if (dto == null) return null;

            var duration = dto.Duration is > 0 ? TimeSpan.FromSeconds(dto.Duration.Value) : (TimeSpan?)null;

            var chapters = (dto.Chapters ?? new List<YtDlpChapterJson>())
                .Where(c => c != null)
                .Select(c => new ChapterMarker
                {
                    Title = c.Title ?? "",
                    StartTime = TimeSpan.FromSeconds(c.StartTime ?? 0),
                    EndTime = TimeSpan.FromSeconds(c.EndTime ?? 0),
                })
                .ToList();

            return new VideoMetadata(duration, dto.Description, chapters, ParseUploadDate(dto.UploadDate));
        }
        catch (Exception ex)
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine", $"metadata parse failed: {ex.Message}");
            return null;
        }
    }

    // ── yt-dlp invocations ──

    /// <summary>
    /// Downloads a single selected format and returns the exact final file path
    /// (<c>--print after_move:filepath</c>), or null on failure.
    /// </summary>
    private async Task<string?> DownloadOneAsync(string url, string format, string outputTemplate, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync(new[]
        {
            "--no-warnings",
            "-f", format,
            "-o", outputTemplate,
            "--print", "after_move:filepath",
            "--no-simulate",
            url,
        }, ct);

        if (exitCode != 0)
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine", $"download failed ({exitCode}) fmt={format}: {Trim(stderr)}");
            return null;
        }

        var path = stdout.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine", $"download produced no file for fmt={format}");
            return null;
        }

        return path;
    }

    /// <summary>Resolves the "WxH" resolution of the selected video format (no download).</summary>
    private async Task<string> GetResolutionAsync(string url, string videoFormat, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunAsync(new[]
        {
            "--no-warnings",
            "-f", videoFormat,
            "--print", "%(width)sx%(height)s",
            url,
        }, ct);

        var res = stdout.Trim();
        return exitCode == 0 && res.Contains('x') ? res : "";
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct)
        => await RunYtDlpAsync(_ytDlpPath, args, ct, _log);

    /// <summary>
    /// Serializes all <c>yt-dlp.exe</c> invocations (resolve / download / metadata /
    /// self-update) through a single process gate. This prevents the updater from
    /// replacing the exe while a resolve or download is mid-flight against it, and
    /// vice-versa. Shared by <see cref="YtDlpUpdater"/>.
    /// </summary>
    internal static readonly SemaphoreSlim ProcessGate = new(1, 1);

    /// <summary>Runs <c>yt-dlp.exe</c> with the given args under <see cref="ProcessGate"/>.</summary>
    internal static async Task<(int exitCode, string stdout, string stderr)> RunYtDlpAsync(
        string ytDlpPath, IReadOnlyList<string> args, CancellationToken ct, PluginLog? log = null)
    {
        await ProcessGate.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            log?.Invoke(LogLevel.Trace, "yt-dlp", FormatCommand(ytDlpPath, args));

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    /// <summary>
    /// Runs <c>yt-dlp.exe</c> and yields each stdout line as it arrives (for streaming
    /// search results). The <see cref="ProcessGate"/> is held only long enough to launch
    /// the process — once started, the exe is loaded into memory, so a concurrent updater
    /// replacing the on-disk file is harmless, and other reads aren't blocked for the
    /// (multi-second) lifetime of the stream. The process is killed if the enumeration is
    /// cancelled or disposed early.
    /// </summary>
    internal static async IAsyncEnumerable<string> RunYtDlpStreamingAsync(
        string ytDlpPath, IReadOnlyList<string> args,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        PluginLog? log = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };

        log?.Invoke(LogLevel.Trace, "yt-dlp", FormatCommand(ytDlpPath, args));

        await ProcessGate.WaitAsync(ct);
        try
        {
            proc.Start();
        }
        finally
        {
            ProcessGate.Release();
        }
        try
        {
            // Loop purely on the async ReadLineAsync (null = end of stream). Do NOT gate on
            // StreamReader.EndOfStream — that property is synchronous and BLOCKS the calling
            // thread while it waits for the underlying stream, which freezes the UI during
            // yt-dlp's initial spawn/resolve before any output arrives.
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (line.Length > 0) yield return line;
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    // ── helpers ──

    /// <summary>
    /// Formats a yt-dlp invocation as a copy-pasteable command line for the debug
    /// log. Arguments containing whitespace are quoted so the logged line can be
    /// re-run verbatim in a shell.
    /// </summary>
    private static string FormatCommand(string ytDlpPath, IReadOnlyList<string> args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Quote(ytDlpPath));
        foreach (var a in args)
        {
            sb.Append(' ');
            sb.Append(Quote(a));
        }
        return sb.ToString();

        static string Quote(string s) =>
            s.Length == 0 || s.IndexOfAny([' ', '\t', '"']) >= 0
                ? "\"" + s.Replace("\"", "\\\"") + "\""
                : s;
    }

    /// <summary>Maps the quality ceiling onto a yt-dlp height filter (mirrors StreamSelector).</summary>
    private static string HeightCap(VideoQualityPreference pref) => pref switch
    {
        VideoQualityPreference.Low => "[height<=480]",
        VideoQualityPreference.Medium => "[height<=720]",
        VideoQualityPreference.High => "[height<=1080]",
        _ => "", // Max — no cap
    };

    private static string GetExtension(string path)
        => Path.GetExtension(path).TrimStart('.');

    private static string ToWatchUrl(string videoId)
        => videoId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? videoId
            : $"https://www.youtube.com/watch?v={videoId}";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static string? FirstNonEmptyLine(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string Trim(string s)
        => s.Length <= 400 ? s : s[..400];

    /// <summary>Parses yt-dlp's <c>upload_date</c> ("YYYYMMDD") as a UTC-midnight offset.</summary>
    private static DateTimeOffset? ParseUploadDate(string? uploadDate)
    {
        // Parse with DateTimeStyles.None so the result is Kind=Unspecified. AssumeUniversal
        // would yield Kind=Local (converting to local time), which then makes the
        // DateTimeOffset(dt, TimeSpan.Zero) constructor throw when the local offset != 0.
        if (string.IsNullOrWhiteSpace(uploadDate)
            || !DateTime.TryParseExact(uploadDate, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return null;
        return new DateTimeOffset(dt, TimeSpan.Zero);
    }

    // ── JSON shapes (subset of yt-dlp --dump-single-json) ──

    private sealed class YtDlpMetaJson
    {
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("chapters")] public List<YtDlpChapterJson>? Chapters { get; set; }
        [JsonPropertyName("upload_date")] public string? UploadDate { get; set; }
    }

    private sealed class YtDlpChapterJson
    {
        [JsonPropertyName("start_time")] public double? StartTime { get; set; }
        [JsonPropertyName("end_time")] public double? EndTime { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }
}
