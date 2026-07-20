using System.Diagnostics;

namespace Phosphor;

/// <summary>
/// Plug-in-internal diagnostics shim. The relocated Plex code logs through <c>DebugLog.Log</c>;
/// across the plug-in load boundary it cannot see the host's logger, so this forwards to
/// <see cref="Trace"/> (surfaced in the debugger / host trace listeners). The source itself logs
/// richer detail via <c>IPluginHost.Log</c>.
/// </summary>
public static class DebugLog
{
    public static void Log(string message) => Trace.WriteLine($"[Plex] {message}");

    public static void Log(string category, string message) =>
        Trace.WriteLine($"[Plex:{category}] {message}");

    public static void LogException(string context, Exception? ex) =>
        Trace.WriteLine($"[Plex:EXC] {context}: {ex?.Message}");
}
