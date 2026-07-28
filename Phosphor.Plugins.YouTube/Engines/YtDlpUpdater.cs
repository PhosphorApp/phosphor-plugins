namespace Phosphor.Video;

using Phosphor;
using Phosphor.Plugin.Abstractions;

/// <summary>
/// Handles keeping the bundled <c>yt-dlp.exe</c> current between (infrequent) app
/// releases, using yt-dlp's own self-updater. Runs through
/// <see cref="YtDlpVideoEngine.ProcessGate"/> so an update never collides with an
/// in-flight resolve / download.
/// </summary>
/// <remarks>
/// Only yt-dlp is updatable at runtime. YoutubeExplode is a compiled-in dependency and
/// cannot be updated without rebuilding the app, so it is intentionally out of scope here.
/// </remarks>
public sealed class YtDlpUpdater
{
    private readonly string _ytDlpPath;
    private readonly PluginLog? _log;

    public YtDlpUpdater(string? ytDlpPath = null, PluginLog? log = null)
    {
        _ytDlpPath = ytDlpPath ?? YtDlpVideoEngine.ResolveYtDlpPath();
        _log = log;
    }

    /// <summary>Returns the current yt-dlp version string (e.g. "2026.07.04"), or null on failure.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, stdout, _) = await YtDlpVideoEngine.RunYtDlpAsync(
                _ytDlpPath, new[] { "--version" }, ct, _log);
            if (code != 0) return null;
            var v = stdout.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch (Exception ex)
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpUpdater", $"version check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Updates yt-dlp to the latest stable build via <c>--update-to stable</c>. No-ops
    /// (reports <see cref="YtDlpUpdateStatus.AlreadyCurrent"/>) when already up to date.
    /// </summary>
    public async Task<YtDlpUpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        var before = await GetVersionAsync(ct);

        int code;
        string stderr;
        try
        {
            // The self-update replaces yt-dlp.exe on disk, so it must not run while a background
            // cache/prefetch DOWNLOAD is mid-flight against the same exe. Downloads use a separate
            // gate (YtDlpVideoEngine.DownloadGate) from interactive resolves, so hold BOTH here:
            // RunYtDlpAsync already serializes against resolves via ProcessGate; wrapping it in
            // DownloadGate additionally blocks (and is blocked by) in-flight downloads.
            await YtDlpVideoEngine.DownloadGate.WaitAsync(ct);
            try
            {
                (code, _, stderr) = await YtDlpVideoEngine.RunYtDlpAsync(
                    _ytDlpPath, new[] { "--update-to", "stable" }, ct, _log);
            }
            finally
            {
                YtDlpVideoEngine.DownloadGate.Release();
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpUpdater", $"update failed: {ex.Message}");
            return new YtDlpUpdateResult(YtDlpUpdateStatus.Failed, before, before, ex.Message);
        }

        if (code != 0)
        {
            _log?.Invoke(LogLevel.Warning, "YtDlpUpdater", $"update exited {code}: {stderr.Trim()}");
            return new YtDlpUpdateResult(YtDlpUpdateStatus.Failed, before, before, stderr.Trim());
        }

        var after = await GetVersionAsync(ct);
        var status = (before != null && after != null && before != after)
            ? YtDlpUpdateStatus.Updated
            : YtDlpUpdateStatus.AlreadyCurrent;

        _log?.Invoke(LogLevel.Info, "YtDlpUpdater", $"update {status}: {before} -> {after}");
        return new YtDlpUpdateResult(status, before, after, null);
    }
}

public enum YtDlpUpdateStatus
{
    AlreadyCurrent,
    Updated,
    Failed,
}

/// <summary>Outcome of a yt-dlp update attempt.</summary>
public sealed record YtDlpUpdateResult(
    YtDlpUpdateStatus Status,
    string? OldVersion,
    string? NewVersion,
    string? Error)
{
    /// <summary>A concise, user-facing status line for the Settings UI.</summary>
    public string ToDisplayString() => Status switch
    {
        YtDlpUpdateStatus.Updated => $"Updated {OldVersion} → {NewVersion}",
        YtDlpUpdateStatus.AlreadyCurrent => $"Already current ({NewVersion ?? OldVersion ?? "unknown"})",
        _ => $"Update failed{(string.IsNullOrEmpty(Error) ? "" : $": {Error}")}",
    };
}
