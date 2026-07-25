using System.Diagnostics;

namespace Phosphor;

/// <summary>
/// Verbosity level for the plug-in diagnostics shim. Mirrors the host's <c>LogLevel</c> so the
/// relocated YouTube code can tag its logs consistently; entries below <see cref="DebugLog.MinimumLevel"/>
/// are dropped here.
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>
/// Plug-in-internal diagnostics shim. The relocated YouTube engines log through
/// <c>DebugLog.Log</c>; across the plug-in load boundary they cannot see the host's logger, so
/// this forwards to <see cref="Trace"/> (surfaced in the debugger / host trace listeners). Kept
/// deliberately tiny — the source itself logs richer detail via <c>IPluginHost.Log</c>.
/// </summary>
public static class DebugLog
{
    /// <summary>Minimum level written; verbose per-item Trace diagnostics are dropped by default.</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public static void Log(string message) => Trace.WriteLine($"[GENERIC] [YouTube] {message}");

    public static void Log(string category, string message) =>
        Trace.WriteLine($"[GENERIC] [YouTube:{category}] {message}");

    public static void Log(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;
        Trace.WriteLine($"[{level}] [YouTube] {message}");
    }

    public static void Log(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel) return;
        Trace.WriteLine($"[{level}] [YouTube:{category}] {message}");
    }

    public static void LogException(string context, Exception? ex) =>
        Trace.WriteLine($"[Error] [YouTube:EXC] {context}: {ex?.Message}");
}
