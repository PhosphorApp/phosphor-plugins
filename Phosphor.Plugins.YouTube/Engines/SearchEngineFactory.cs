using System.Net.Http;
using Phosphor;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Search;

/// <summary>
/// Creates the configured <see cref="ISearchEngine"/> implementation. This is the single
/// switch point between YoutubeExplode and (later) yt-dlp for the discovery path.
/// </summary>
public static class SearchEngineFactory
{
    public static ISearchEngine Create(SearchEngineKind kind, HttpClient? http = null, PluginLog? log = null)
    {
        ISearchEngine engine = kind switch
        {
            SearchEngineKind.YtDlp => new YtDlpSearchEngine(log: log),
            _ => new YoutubeExplodeSearchEngine(http),
        };

        // Safety net: if the requested engine can't run (e.g. yt-dlp.exe missing), fall
        // back to the always-available in-process engine so search never hard-fails.
        if (!engine.IsAvailable)
        {
            log?.Invoke(LogLevel.Warning, "SearchEngine", $"{kind} unavailable — falling back to YoutubeExplode");
            return new YoutubeExplodeSearchEngine(http);
        }

        return engine;
    }
}
