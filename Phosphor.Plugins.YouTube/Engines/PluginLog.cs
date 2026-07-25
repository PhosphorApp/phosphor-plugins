using Phosphor.Plugin.Abstractions;

namespace Phosphor;

/// <summary>
/// Diagnostics sink threaded from <see cref="Phosphor.Plugins.YouTube.YouTubeSource"/> down into the
/// engines/factories. Formats <c>(level, category, message)</c> and routes to
/// <see cref="IPluginHost.Log(LogLevel, string)"/> so YouTube engine logs land in the host log file
/// and honor the verbosity setting (Path A). A <c>null</c> sink is a no-op — safe for the brief window
/// before <c>InitializeAsync(host)</c> wires the host, and for unit construction.
/// </summary>
public delegate void PluginLog(LogLevel level, string category, string message);
