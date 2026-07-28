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

    // ── Download throttling (mitigation 1) ──
    // YouTube 403-throttles concurrent stream + full-media download of the same item. These
    // knobs keep the cache/prefetch DOWNLOAD path polite so it is less likely to trip anti-abuse
    // heuristics. The VALUES below are defaults; they are overridden at startup from the bundled
    // download_throttle.json (via DownloadThrottleConfig) so testers can tune without recompiling.
    // The on/off master switch (ThrottleDownloads) is a USER setting, applied by YouTubeSource —
    // it is NOT read from the JSON. Set a value to null/false to omit the corresponding arg.

    /// <summary>Master switch for the download-path throttle args. When false, downloads run
    /// unthrottled (original behavior).</summary>
    public static bool ThrottleDownloads { get; set; } = true;

    /// <summary>Caps download speed (<c>--limit-rate</c>) so the pull isn't a burst. null = omit.</summary>
    public static string? DownloadLimitRate { get; set; } = "5M";

    /// <summary>Re-extracts if speed drops below this (<c>--throttled-rate</c>), bypassing some
    /// ISP/site throttling. null = omit.</summary>
    public static string? DownloadThrottledRate { get; set; } = "100K";

    /// <summary>Lower bound (seconds) for the random pre-download sleep (<c>--sleep-interval</c>).
    /// null = omit both sleep args.</summary>
    public static int? DownloadSleepIntervalSeconds { get; set; } = 2;

    /// <summary>Upper bound (seconds) for the random pre-download sleep (<c>--max-sleep-interval</c>).</summary>
    public static int? DownloadMaxSleepIntervalSeconds { get; set; } = 10;

    // ── 403 back-off (mitigation 3) ──
    // On repeated 403s for an item, stop re-attempting its download for a cooldown so we don't
    // worsen the throttle. Keyed by videoId; shared across engine instances (settings changes
    // rebuild the engine, but the throttle state should persist).

    /// <summary>Number of consecutive 403 failures for an item before its download is put on
    /// cooldown. null/less-than-1 disables the back-off.</summary>
    public static int? Http403BackoffThreshold { get; set; } = 2;

    /// <summary>How long an item's download stays on cooldown after tripping the 403 threshold.</summary>
    public static TimeSpan Http403BackoffCooldown { get; set; } = TimeSpan.FromMinutes(10);

    private static readonly object _backoffGate = new();
    private static readonly Dictionary<string, int> _http403Counts = new();
    private static readonly Dictionary<string, DateTimeOffset> _http403CooldownUntil = new();

    public YtDlpVideoEngine(string? ytDlpPath = null, PluginLog? log = null)
    {
        _ytDlpPath = ytDlpPath ?? ResolveYtDlpPath();
        _log = log;
        // Populate the tunable throttle knobs from the bundled download_throttle.json (once).
        DownloadThrottleConfig.EnsureLoaded(log);
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

        // 403 back-off: if this item recently tripped the 403 threshold, skip the download entirely
        // for the cooldown window rather than hammering YouTube and worsening the throttle.
        if (IsInHttp403Cooldown(videoId, out var remaining))
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine",
                $"skip download {videoId}: YouTube throttled (403 back-off, {remaining.TotalSeconds:F0}s remaining)");
            return null;
        }

        // Download best video-only and best audio-only streams separately, mirroring the
        // YoutubeExplode engine's output shape so the caches mux exactly as before.
        var videoFormat = $"bv*{HeightCap(quality)}";
        var audioFormat = preferStereo ? "ba[audio_channels<=2]/ba" : "ba";

        var videoPath = await DownloadOneAsync(videoId, url, videoFormat,
            Path.Combine(destinationDir, "%(id)s_video.%(ext)s"), ct);
        if (videoPath == null) return null;

        var audioPath = await DownloadOneAsync(videoId, url, audioFormat,
            Path.Combine(destinationDir, "%(id)s_audio.%(ext)s"), ct);
        if (audioPath == null)
        {
            TryDelete(videoPath);
            return null;
        }

        // A successful download clears any accumulated 403 state for this item.
        ClearHttp403(videoId);

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

    // ── download throttling / 403 back-off ──

    /// <summary>
    /// Appends the code-tunable throttle args (<c>--limit-rate</c> / <c>--throttled-rate</c> /
    /// <c>--sleep-interval</c> / <c>--max-sleep-interval</c>) to a DOWNLOAD invocation when
    /// <see cref="ThrottleDownloads"/> is on. Never used on the resolve path.
    /// </summary>
    private static void AddThrottleArgs(List<string> args)
    {
        if (!ThrottleDownloads) return;

        if (!string.IsNullOrWhiteSpace(DownloadLimitRate))
        {
            args.Add("--limit-rate");
            args.Add(DownloadLimitRate);
        }
        if (!string.IsNullOrWhiteSpace(DownloadThrottledRate))
        {
            args.Add("--throttled-rate");
            args.Add(DownloadThrottledRate);
        }
        if (DownloadSleepIntervalSeconds is int sleep && sleep > 0)
        {
            args.Add("--sleep-interval");
            args.Add(sleep.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (DownloadMaxSleepIntervalSeconds is int maxSleep && maxSleep >= sleep)
            {
                args.Add("--max-sleep-interval");
                args.Add(maxSleep.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Heuristic: does this yt-dlp stderr indicate an HTTP 403 (throttling)?</summary>
    private static bool LooksLikeHttp403(string? stderr)
        => stderr != null
            && (stderr.Contains("403", StringComparison.Ordinal)
                || stderr.Contains("Forbidden", StringComparison.OrdinalIgnoreCase));

    /// <summary>Records a 403 for an item; once the threshold is reached, starts the cooldown.</summary>
    private void RecordHttp403(string videoId)
    {
        if (Http403BackoffThreshold is not int threshold || threshold < 1) return;

        lock (_backoffGate)
        {
            var count = _http403Counts.TryGetValue(videoId, out var c) ? c + 1 : 1;
            _http403Counts[videoId] = count;

            if (count >= threshold)
            {
                _http403CooldownUntil[videoId] = DateTimeOffset.UtcNow + Http403BackoffCooldown;
                _http403Counts.Remove(videoId);
                _log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine",
                    $"YouTube throttled {videoId}: {threshold} consecutive 403s — backing off downloads for {Http403BackoffCooldown.TotalMinutes:F0}m");
            }
        }
    }

    /// <summary>True while an item is in its 403 cooldown window; yields the remaining time.</summary>
    private static bool IsInHttp403Cooldown(string videoId, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        lock (_backoffGate)
        {
            if (!_http403CooldownUntil.TryGetValue(videoId, out var until)) return false;

            var now = DateTimeOffset.UtcNow;
            if (now >= until)
            {
                _http403CooldownUntil.Remove(videoId);
                return false;
            }
            remaining = until - now;
            return true;
        }
    }

    /// <summary>Clears any accumulated 403 count / cooldown for an item (on a successful download).</summary>
    private static void ClearHttp403(string videoId)
    {
        lock (_backoffGate)
        {
            _http403Counts.Remove(videoId);
            _http403CooldownUntil.Remove(videoId);
        }
    }

    // ── yt-dlp invocations ──

    /// <summary>
    /// Downloads a single selected format and returns the exact final file path
    /// (<c>--print after_move:filepath</c>), or null on failure.
    /// </summary>
    private async Task<string?> DownloadOneAsync(string videoId, string url, string format, string outputTemplate, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-warnings",
            "-f", format,
            "-o", outputTemplate,
            "--print", "after_move:filepath",
            "--no-simulate",
        };
        AddThrottleArgs(args);
        args.Add(url);

        var (exitCode, stdout, stderr) = await RunDownloadAsync(args, ct);

        if (exitCode != 0)
        {
            // Track 403s per item so repeated throttling puts the download on cooldown (mitigation 3).
            if (LooksLikeHttp403(stderr))
                RecordHttp403(videoId);

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
        // Called only from the download path (DownloadStreamsAsync), so run it on the DownloadGate
        // to keep the whole download sequence off the interactive ProcessGate.
        var (exitCode, stdout, _) = await RunDownloadAsync(new[]
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
    /// Runs <c>yt-dlp.exe</c> on the background <see cref="DownloadGate"/> (for cache/prefetch
    /// downloads), so a long download never blocks an interactive resolve on <see cref="ProcessGate"/>.
    /// </summary>
    private async Task<(int exitCode, string stdout, string stderr)> RunDownloadAsync(
        IReadOnlyList<string> args, CancellationToken ct)
        => await RunYtDlpOnGateAsync(DownloadGate, _ytDlpPath, args, ct, _log);

    /// <summary>
    /// Serializes interactive <c>yt-dlp.exe</c> invocations (resolve / metadata / self-update /
    /// version) through a single process gate. This prevents the updater from replacing the exe
    /// while a resolve is mid-flight against it, and vice-versa. Shared by <see cref="YtDlpUpdater"/>.
    ///
    /// Downloads use the SEPARATE <see cref="DownloadGate"/> instead, so a long cache download (a
    /// full concert can run for minutes) does NOT block interactive playback stream resolution —
    /// which would otherwise queue behind the download and trip the player's first-frame watchdog.
    /// </summary>
    internal static readonly SemaphoreSlim ProcessGate = new(1, 1);

    /// <summary>
    /// Serializes background cache/prefetch DOWNLOADS separately from interactive resolves (see
    /// <see cref="ProcessGate"/>). Downloads stay serialized among themselves (one at a time, to
    /// avoid doubling network/CPU), but never block a resolve the play path needs. The updater
    /// acquires BOTH gates so its exe-swap protection still holds against an in-flight download.
    /// </summary>
    internal static readonly SemaphoreSlim DownloadGate = new(1, 1);

    /// <summary>Runs <c>yt-dlp.exe</c> with the given args under <see cref="ProcessGate"/>.</summary>
    internal static Task<(int exitCode, string stdout, string stderr)> RunYtDlpAsync(
        string ytDlpPath, IReadOnlyList<string> args, CancellationToken ct, PluginLog? log = null)
        => RunYtDlpOnGateAsync(ProcessGate, ytDlpPath, args, ct, log);

    /// <summary>Runs <c>yt-dlp.exe</c> under the given gate (see <see cref="ProcessGate"/> / <see cref="DownloadGate"/>).</summary>
    internal static async Task<(int exitCode, string stdout, string stderr)> RunYtDlpOnGateAsync(
        SemaphoreSlim gate, string ytDlpPath, IReadOnlyList<string> args, CancellationToken ct, PluginLog? log = null)
    {
        await gate.WaitAsync(ct);
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
            gate.Release();
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
