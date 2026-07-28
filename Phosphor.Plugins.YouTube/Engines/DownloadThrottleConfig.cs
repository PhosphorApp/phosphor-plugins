using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Video;

/// <summary>
/// Loads the code-tunable yt-dlp DOWNLOAD-path throttle knobs from the bundled
/// <c>download_throttle.json</c> (shipped next to the plug-in DLL, like
/// <c>default_categories.json</c>). This lets testers tune anti-throttle behavior WITHOUT
/// recompiling, while keeping the values out of the UI (only the on/off master switch is a
/// user setting). Values populate the static properties on <see cref="YtDlpVideoEngine"/>.
/// </summary>
/// <remarks>
/// The on/off master switch (<see cref="YtDlpVideoEngine.ThrottleDownloads"/>) is NOT read from
/// this file — it is a user setting owned by the host. Missing file / parse failure leaves the
/// engine's built-in defaults intact (safe fallback).
/// </remarks>
internal static class DownloadThrottleConfig
{
    private const string FileName = "download_throttle.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static bool _loaded;

    /// <summary>
    /// Loads the throttle file once and applies it to <see cref="YtDlpVideoEngine"/>'s static
    /// knobs. Idempotent: subsequent calls are no-ops. Never throws — on any failure the engine
    /// keeps its built-in defaults.
    /// </summary>
    public static void EnsureLoaded(PluginLog? log = null)
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var dir = Path.GetDirectoryName(typeof(DownloadThrottleConfig).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return;
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path))
            {
                log?.Invoke(LogLevel.Debug, "YtDlpVideoEngine", $"throttle config not found ({path}) — using built-in defaults");
                return;
            }

            var dto = JsonSerializer.Deserialize<ThrottleJson>(File.ReadAllText(path), JsonOptions);
            if (dto == null) return;

            YtDlpVideoEngine.DownloadLimitRate = NullIfBlank(dto.LimitRate);
            YtDlpVideoEngine.DownloadThrottledRate = NullIfBlank(dto.ThrottledRate);
            YtDlpVideoEngine.DownloadSleepIntervalSeconds = dto.SleepIntervalSeconds;
            YtDlpVideoEngine.DownloadMaxSleepIntervalSeconds = dto.MaxSleepIntervalSeconds;
            YtDlpVideoEngine.Http403BackoffThreshold = dto.Http403BackoffThreshold;
            if (dto.Http403BackoffCooldownMinutes is double mins && mins > 0)
                YtDlpVideoEngine.Http403BackoffCooldown = TimeSpan.FromMinutes(mins);

            log?.Invoke(LogLevel.Debug, "YtDlpVideoEngine",
                $"throttle config loaded from {path}: limitRate={dto.LimitRate ?? "(none)"} throttledRate={dto.ThrottledRate ?? "(none)"} " +
                $"sleep={dto.SleepIntervalSeconds}-{dto.MaxSleepIntervalSeconds}s 403(threshold={dto.Http403BackoffThreshold}, cooldown={dto.Http403BackoffCooldownMinutes}m)");
        }
        catch (Exception ex)
        {
            log?.Invoke(LogLevel.Warning, "YtDlpVideoEngine", $"throttle config read failed — using built-in defaults: {ex.Message}");
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed class ThrottleJson
    {
        [JsonPropertyName("limitRate")] public string? LimitRate { get; set; }
        [JsonPropertyName("throttledRate")] public string? ThrottledRate { get; set; }
        [JsonPropertyName("sleepIntervalSeconds")] public int? SleepIntervalSeconds { get; set; }
        [JsonPropertyName("maxSleepIntervalSeconds")] public int? MaxSleepIntervalSeconds { get; set; }
        [JsonPropertyName("http403BackoffThreshold")] public int? Http403BackoffThreshold { get; set; }
        [JsonPropertyName("http403BackoffCooldownMinutes")] public double? Http403BackoffCooldownMinutes { get; set; }
    }
}
