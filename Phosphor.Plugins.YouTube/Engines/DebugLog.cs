using System.Diagnostics;

namespace Phosphor;

/// <summary>
/// Plug-in-internal diagnostics shim. The relocated YouTube engines log through
/// <c>DebugLog.Log</c>; across the plug-in load boundary they cannot see the host's logger, so
/// this forwards to <see cref="Trace"/> (surfaced in the debugger / host trace listeners). Kept
/// deliberately tiny — the source itself logs richer detail via <c>IPluginHost.Log</c>.
/// </summary>
public static class DebugLog
{
    public static void Log(string message) => Trace.WriteLine($"[YouTube] {message}");

    public static void Log(string category, string message) =>
        Trace.WriteLine($"[YouTube:{category}] {message}");

    public static void LogException(string context, Exception? ex) =>
        Trace.WriteLine($"[YouTube:EXC] {context}: {ex?.Message}");
}
