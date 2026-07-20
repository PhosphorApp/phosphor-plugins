namespace Phosphor.Video;

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

    public YtDlpUpdater(string? ytDlpPath = null)
    {
        _ytDlpPath = ytDlpPath ?? YtDlpVideoEngine.ResolveYtDlpPath();
    }

    /// <summary>Returns the current yt-dlp version string (e.g. "2026.07.04"), or null on failure.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var (code, stdout, _) = await YtDlpVideoEngine.RunYtDlpAsync(
                _ytDlpPath, new[] { "--version" }, ct);
            if (code != 0) return null;
            var v = stdout.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch (Exception ex)
        {
            DebugLog.Log("YtDlpUpdater", $"version check failed: {ex.Message}");
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
            (code, _, stderr) = await YtDlpVideoEngine.RunYtDlpAsync(
                _ytDlpPath, new[] { "--update-to", "stable" }, ct);
        }
        catch (Exception ex)
        {
            DebugLog.Log("YtDlpUpdater", $"update failed: {ex.Message}");
            return new YtDlpUpdateResult(YtDlpUpdateStatus.Failed, before, before, ex.Message);
        }

        if (code != 0)
        {
            DebugLog.Log("YtDlpUpdater", $"update exited {code}: {stderr.Trim()}");
            return new YtDlpUpdateResult(YtDlpUpdateStatus.Failed, before, before, stderr.Trim());
        }

        var after = await GetVersionAsync(ct);
        var status = (before != null && after != null && before != after)
            ? YtDlpUpdateStatus.Updated
            : YtDlpUpdateStatus.AlreadyCurrent;

        DebugLog.Log("YtDlpUpdater", $"update {status}: {before} -> {after}");
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
