using System.Net.Http;

namespace Phosphor.Search;

/// <summary>
/// Creates the configured <see cref="ISearchEngine"/> implementation. This is the single
/// switch point between YoutubeExplode and (later) yt-dlp for the discovery path.
/// </summary>
public static class SearchEngineFactory
{
    public static ISearchEngine Create(SearchEngineKind kind, HttpClient? http = null)
    {
        ISearchEngine engine = kind switch
        {
            SearchEngineKind.YtDlp => new YtDlpSearchEngine(),
            _ => new YoutubeExplodeSearchEngine(http),
        };

        // Safety net: if the requested engine can't run (e.g. yt-dlp.exe missing), fall
        // back to the always-available in-process engine so search never hard-fails.
        if (!engine.IsAvailable)
        {
            DebugLog.Log("SearchEngine", $"{kind} unavailable — falling back to YoutubeExplode");
            return new YoutubeExplodeSearchEngine(http);
        }

        return engine;
    }
}
