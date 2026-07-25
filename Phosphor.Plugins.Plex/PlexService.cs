using System.IO;
using System.Net.Http;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor;

/// <summary>
/// Lightweight Plex Media Server client that uses the REST API directly.
/// Requires a server URL and an authentication token.
/// </summary>
public class PlexService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string _serverUrl = "";
    private string _token = "";
    private bool _stereoAudio;

    /// <summary>
    /// Diagnostics sink, supplied by <c>PlexSource</c> once the host is available. Formats
    /// <c>(level, category, message)</c> and routes to <see cref="IPluginHost.Log(LogLevel, string)"/>
    /// so Plex logs land in the host log file and honor the verbosity setting (Path A). Defaults to a
    /// no-op so calls made before the host is wired (or in tests) are harmless.
    /// </summary>
    public Action<LogLevel, string, string> Log { get; set; } = static (_, _, _) => { };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_serverUrl) && !string.IsNullOrWhiteSpace(_token);

    /// <summary>
    /// Builds a thumbnail URL that uses Plex's server-side photo transcoder to return a
    /// small, pre-resized image. This keeps the client from downloading and decoding the
    /// full-resolution original (Plex posters can be very large), which otherwise causes
    /// large-object-heap allocations and GC pauses that stutter the render thread.
    /// </summary>
    private string BuildThumbnailUrl(string? thumbPath, int size = 320)
    {
        if (string.IsNullOrEmpty(thumbPath))
            return "";

        // The transcoder wants the (relative) original thumb path plus a token.
        var original = $"{thumbPath}?X-Plex-Token={_token}";
        var encoded = Uri.EscapeDataString(original);
        return $"{_serverUrl}/photo/:/transcode"
            + $"?width={size}&height={size}"
            + $"&minSize=1&upscale=0"
            + $"&url={encoded}"
            + $"&X-Plex-Token={_token}";
    }

    private void DiagLog(string message)
    {
        // Per-item Plex diagnostics (audio-stream selection dump, chapter probes, compilation-album
        // search). These fire once per track resolve and historically dominated the log, so they log
        // at Trace — silent at the default verbosity, available when investigating. Genuine failures
        // are logged directly at Warning at their call sites instead of through here.
        Log(LogLevel.Trace, "Plex", message);
    }

    /// <summary>
    /// Always includes stream metadata so we can detect native audio channel counts,
    /// and chapter markers for video items.
    /// </summary>
    private string StreamParam => "&includeStreams=1&includeChapters=1";

    public void Configure(string serverUrl, string token, bool stereoAudio = false)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _token = token;
        _stereoAudio = stereoAudio;
        Log(LogLevel.Info, "Plex", $"Configured: server={_serverUrl} stereoAudio={_stereoAudio}");
    }

    /// <summary>
    /// Fetch chapter markers for a specific Plex item by rating key.
    /// Returns null if no chapters are found.
    /// </summary>
    public async Task<List<ChapterMarker>?> GetChaptersAsync(string ratingKey)
    {
        try
        {
            var url = $"{_serverUrl}/library/metadata/{ratingKey}?X-Plex-Token={_token}&includeChapters=1";
            var doc = await FetchJsonAsync(url);

            if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
                mc.TryGetProperty("Metadata", out var metadata))
            {
                foreach (var m in metadata.EnumerateArray())
                {
                    if (m.TryGetProperty("Chapter", out var chapterArr))
                    {
                        var chapters = new List<ChapterMarker>();
                        foreach (var ch in chapterArr.EnumerateArray())
                        {
                            var title = ch.TryGetProperty("tag", out var ct) ? ct.GetString() ?? "" : "";
                            var startMs = ch.TryGetProperty("startTimeOffset", out var sto) ? sto.GetInt64() : 0;
                            var endMs = ch.TryGetProperty("endTimeOffset", out var eto) ? eto.GetInt64() : 0;
                            chapters.Add(new ChapterMarker
                            {
                                Title = title,
                                StartTime = TimeSpan.FromMilliseconds(startMs),
                                EndTime = TimeSpan.FromMilliseconds(endMs)
                            });
                        }
                        if (chapters.Count > 0)
                        {
                            DiagLog($"GetChaptersAsync({ratingKey}): {chapters.Count} chapters found");
                            return chapters;
                        }
                    }
                }
            }
            DiagLog($"GetChaptersAsync({ratingKey}): no chapters found");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, "Plex", $"GetChaptersAsync({ratingKey}) error: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Browse a Plex library section and return all video items.
    /// </summary>
    public async Task<List<VideoItem>> GetLibraryVideosAsync(string sectionKey)
    {
        var url = $"{_serverUrl}/library/sections/{sectionKey}/all?X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url);
        return ParseVideos(doc);
    }

    /// <summary>
    /// Browse a Plex library section with pagination.
    /// For music libraries: pass libraryType "artist" to browse artists at the top level.
    /// </summary>
    public async Task<PlexPage> GetLibraryVideosPageAsync(string sectionKey, int start, int count, string? libraryType = null, CancellationToken ct = default)
    {
        // Music libraries: type=8 for artists at top level, type=10 for tracks
        var typeFilter = libraryType == "artist" ? "&type=8" : "";
        var url = $"{_serverUrl}/library/sections/{sectionKey}/all?X-Plex-Container-Start={start}&X-Plex-Container-Size={count}{typeFilter}&X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url, ct);

        var items = libraryType == "artist"
            ? ParsePlexItems(doc, PlexItemType.Artist)
            : ParseVideos(doc);

        int totalSize = 0;
        if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("totalSize", out var ts))
            totalSize = ts.GetInt32();

        return new PlexPage { Items = items, TotalSize = totalSize };
    }

    /// <summary>
    /// Search across the entire Plex server for videos matching a query.
    /// </summary>
    public async Task<List<VideoItem>> SearchAsync(string query)
    {
        var encoded = Uri.EscapeDataString(query);
        var url = $"{_serverUrl}/hubs/search?query={encoded}&limit=100&X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url);
        return ParseSearchResults(doc);
    }

    /// <summary>
    /// Search within a specific library section. Uses the per-section
    /// <c>/library/sections/{key}/all?title=</c> endpoint, which hard-filters to that one library
    /// (unlike <c>/hubs/search</c>, which is a global smart-search that ignores the section scope and
    /// bleeds in cross-library / actor / tag matches). For music libraries supports searching by
    /// artist (type 8), album (type 9), or track (type 10).
    /// </summary>
    public async Task<List<VideoItem>> SearchLibraryAsync(string sectionKey, string query, string? libraryType = null, PlexSearchMode? searchMode = null, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(query);
        var items = new List<VideoItem>();

        if (libraryType == "artist")
        {
            // Determine Plex API type code + our parse type based on search mode.
            var (apiType, itemType) = searchMode switch
            {
                PlexSearchMode.Artist => (8, PlexItemType.Artist),
                PlexSearchMode.Album => (9, PlexItemType.Album),
                _ => (10, PlexItemType.Track),
            };

            // Section-scoped title search — only this library, only the requested item type.
            var url = $"{_serverUrl}/library/sections/{sectionKey}/all?type={apiType}&title={encoded}&X-Plex-Token={_token}{StreamParam}";
            var doc = await FetchJsonAsync(url, ct);

            items.AddRange(ParsePlexItems(doc, itemType));
        }
        else
        {
            // Video libraries — section-scoped title search (hard-filtered to this section).
            var url = $"{_serverUrl}/library/sections/{sectionKey}/all?title={encoded}&X-Plex-Token={_token}{StreamParam}";
            var doc = await FetchJsonAsync(url, ct);
            items.AddRange(ParseVideos(doc));
        }

        return items;
    }

    /// <summary>
    /// Section-scoped search with native server-side filters (duration bounds). Uses the same
    /// <c>/library/sections/{key}/all</c> endpoint as <see cref="SearchLibraryAsync"/>, adding
    /// Plex's advanced-filter params so large libraries are filtered on the server instead of
    /// scanned client-side. For music (artist) libraries the search targets tracks (type 10) so
    /// duration bounds apply to playable items; for video libraries it is a plain title search.
    /// <paramref name="minDuration"/>/<paramref name="maxDuration"/> are optional bounds.
    /// </summary>
    public async Task<List<VideoItem>> SearchLibraryWithFiltersAsync(
        string sectionKey,
        string query,
        string? libraryType = null,
        TimeSpan? minDuration = null,
        TimeSpan? maxDuration = null,
        CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(query);

        // Plex advanced integer filters use "field>>=" (>=) and "field<<=" (<=); the ">>"/"<<"
        // operator glyphs must be URL-encoded. Duration is in milliseconds.
        var durationFilter = "";
        if (minDuration is { } min)
            durationFilter += $"&duration%3E%3E={(long)min.TotalMilliseconds}";
        if (maxDuration is { } max)
            durationFilter += $"&duration%3C%3C={(long)max.TotalMilliseconds}";

        // Music (artist) libraries: search playable tracks (type 10). A plain title= filter only
        // matches the track title, so also match the artist name (grandparentTitle) and merge — this
        // is what users expect from "rush" in a music library (all of Rush's tracks, not just tracks
        // literally titled "…rush…"). Video libraries do a plain section-scoped title search.
        if (libraryType == "artist")
        {
            var byTitle = await FetchTracksAsync(
                $"{_serverUrl}/library/sections/{sectionKey}/all"
                + $"?X-Plex-Token={_token}&type=10&title={encoded}{durationFilter}{StreamParam}", ct);

            List<VideoItem> byArtist;
            try
            {
                byArtist = await FetchTracksAsync(
                    $"{_serverUrl}/library/sections/{sectionKey}/all"
                    + $"?X-Plex-Token={_token}&type=10&artist.title={encoded}{durationFilter}{StreamParam}", ct);
            }
            catch (Exception ex)
            {
                // Some servers/library configs may not expose the artist.title filter field; the
                // title-only results still stand.
                Log(LogLevel.Warning, "Plex", $"SearchLibraryWithFiltersAsync artist.title filter failed: {ex.Message}");
                byArtist = new List<VideoItem>();
            }

            // Merge, de-duplicating by rating key (a track can match both title and artist).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<VideoItem>();
            foreach (var v in byTitle.Concat(byArtist))
            {
                var key = v.PlexRatingKey ?? v.VideoId ?? "";
                if (key.Length == 0 || seen.Add(key))
                    merged.Add(v);
            }
            return merged;
        }

        var titleFilter = string.IsNullOrWhiteSpace(query) ? "" : $"&title={encoded}";
        var url = $"{_serverUrl}/library/sections/{sectionKey}/all"
            + $"?X-Plex-Token={_token}{titleFilter}{durationFilter}{StreamParam}";
        var doc = await FetchJsonAsync(url, ct);
        return ParseVideos(doc);
    }

    /// <summary>Fetches a Plex item list URL and parses playable items (with stream URLs).</summary>
    private async Task<List<VideoItem>> FetchTracksAsync(string url, CancellationToken ct)
    {
        var doc = await FetchJsonAsync(url, ct);
        return ParseVideos(doc);
    }


    /// <summary>
    /// Get children of a Plex metadata item (e.g. artist → albums, album → tracks).
    /// For artists, also searches for albums by artist name to catch compilations
    /// that may not be directly linked in the parent-child hierarchy.
    /// </summary>
    public async Task<List<VideoItem>> GetChildrenAsync(string ratingKey, PlexItemType childType, CancellationToken ct = default)
    {
        var url = $"{_serverUrl}/library/metadata/{ratingKey}/children?X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url, ct);

        if (childType == PlexItemType.Track)
            return ParseVideos(doc);

        var items = ParsePlexItems(doc, childType);

        // For albums: also search by artist name to catch compilations that aren't direct children
        if (childType == PlexItemType.Album)
        {
            try
            {
                // Get the artist name and section ID from the parent metadata
                var parentUrl = $"{_serverUrl}/library/metadata/{ratingKey}?X-Plex-Token={_token}";
                var parentDoc = await FetchJsonAsync(parentUrl, ct);
                
                string? artistName = null;
                string? sectionKey = null;

                if (parentDoc.RootElement.TryGetProperty("MediaContainer", out var pmc))
                {
                    // Extract library section ID from the MediaContainer
                    if (pmc.TryGetProperty("librarySectionID", out var sid))
                        sectionKey = sid.GetInt32().ToString();

                    // Get artist title from the metadata
                    if (pmc.TryGetProperty("Metadata", out var metadata) && metadata.GetArrayLength() > 0)
                    {
                        var m = metadata[0];
                        artistName = m.TryGetProperty("title", out var t) ? t.GetString() : null;
                    }
                }

                if (!string.IsNullOrEmpty(artistName) && !string.IsNullOrEmpty(sectionKey))
                {
                    DiagLog($"Searching for compilation albums by '{artistName}' in section {sectionKey}");

                    // Search for albums by this artist to catch compilations
                    var searchResults = await SearchLibraryAsync(sectionKey, artistName, "artist", PlexSearchMode.Album, ct);
                    
                    // Merge search results, avoiding duplicates by ratingKey
                    var existingKeys = new HashSet<string>(items.Where(i => i.PlexRatingKey != null).Select(i => i.PlexRatingKey!));
                    
                    foreach (var album in searchResults.Where(a => a.PlexItemType == PlexItemType.Album && a.PlexRatingKey != null))
                    {
                        if (!existingKeys.Contains(album.PlexRatingKey!))
                        {
                            items.Add(album);
                            DiagLog($"  + Found compilation album: {album.Title} (key={album.PlexRatingKey})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, "Plex", $"Error searching for compilation albums: {ex.Message}");
                // Continue with just the direct children if search fails
            }
        }

        return items;
    }

    /// <summary>
    /// Get all playable tracks for a Plex item. For albums, returns the album's tracks.
    /// For artists, fetches all albums then all tracks from each album in order.
    /// </summary>
    public async Task<List<VideoItem>> GetAllTracksAsync(string ratingKey, PlexItemType itemType, CancellationToken ct = default)
    {
        if (itemType == PlexItemType.Album)
            return await GetChildrenAsync(ratingKey, PlexItemType.Track, ct);

        if (itemType == PlexItemType.Artist)
        {
            var albums = await GetChildrenAsync(ratingKey, PlexItemType.Album, ct);
            var allTracks = new List<VideoItem>();
            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                if (album.PlexRatingKey == null) continue;
                var tracks = await GetChildrenAsync(album.PlexRatingKey, PlexItemType.Track, ct);
                allTracks.AddRange(tracks);
            }
            return allTracks;
        }

        return [];
    }

    /// <summary>
    /// List available library sections (to let user pick which one to browse).
    /// </summary>
    public async Task<List<PlexLibrary>> GetLibrariesAsync()
    {
        var url = $"{_serverUrl}/library/sections?X-Plex-Token={_token}";
        var doc = await FetchJsonAsync(url);
        var libs = new List<PlexLibrary>();

        if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("Directory", out var dirs))
        {
            foreach (var dir in dirs.EnumerateArray())
            {
                var type = dir.GetProperty("type").GetString() ?? "";
                // Only show video-capable libraries (movie, show, artist with music videos, etc.)
                if (type is "movie" or "show" or "artist")
                {
                    libs.Add(new PlexLibrary
                    {
                        Key = dir.GetProperty("key").GetString() ?? "",
                        Title = dir.GetProperty("title").GetString() ?? "",
                        Type = type
                    });
                }
            }
        }

        return libs;
    }

    /// <summary>
    /// Get library hubs (smart collections like "Recently Played", "Recently Added", etc.)
    /// for a specific library section.
    /// </summary>
    public async Task<List<PlexHub>> GetLibraryHubsAsync(string sectionKey, CancellationToken ct = default)
    {
        var url = $"{_serverUrl}/hubs/sections/{sectionKey}?X-Plex-Token={_token}";
        var doc = await FetchJsonAsync(url, ct);
        var hubs = new List<PlexHub>();

        if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("Hub", out var hubArr))
        {
            foreach (var h in hubArr.EnumerateArray())
            {
                var size = h.TryGetProperty("size", out var s) ? s.GetInt32() : 0;
                if (size == 0) continue;

                hubs.Add(new PlexHub
                {
                    Title = h.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Type = h.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "",
                    HubKey = h.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
                    Size = size
                });
            }
        }

        return hubs;
    }

    /// <summary>
    /// Get all items from a hub using its key. Handles mixed item types (artists, albums, tracks).
    /// </summary>
    public async Task<List<VideoItem>> GetHubItemsAsync(string hubKey, string hubType, CancellationToken ct = default)
    {
        var separator = hubKey.Contains('?') ? "&" : "?";
        var url = $"{_serverUrl}{hubKey}{separator}X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url, ct);

        // Determine item type from hub type
        return hubType switch
        {
            "artist" => ParsePlexItems(doc, PlexItemType.Artist),
            "album" => ParsePlexItems(doc, PlexItemType.Album),
            _ => ParseMixedItems(doc)
        };
    }

    /// <summary>
    /// Get all playlists on the Plex server, optionally filtered to audio/video.
    /// </summary>
    public async Task<List<PlexPlaylist>> GetPlaylistsAsync(string? playlistType = null, CancellationToken ct = default)
    {
        var typeFilter = playlistType != null ? $"&playlistType={playlistType}" : "";
        var url = $"{_serverUrl}/playlists?X-Plex-Token={_token}{typeFilter}";
        var doc = await FetchJsonAsync(url, ct);
        var playlists = new List<PlexPlaylist>();

        if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("Metadata", out var metadata))
        {
            foreach (var m in metadata.EnumerateArray())
            {
                var thumbPath = m.TryGetProperty("composite", out var comp) ? comp.GetString()
                    : m.TryGetProperty("thumb", out var th) ? th.GetString()
                    : null;
                playlists.Add(new PlexPlaylist
                {
                    RatingKey = m.TryGetProperty("ratingKey", out var rk) ? rk.GetString() ?? "" : "",
                    Title = m.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    PlaylistType = m.TryGetProperty("playlistType", out var pt) ? pt.GetString() ?? "" : "",
                    Smart = m.TryGetProperty("smart", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True,
                    LeafCount = m.TryGetProperty("leafCount", out var lc) ? lc.GetInt32() : 0,
                    Thumb = BuildThumbnailUrl(thumbPath)
                });
            }
        }

        return playlists;
    }

    /// <summary>
    /// Get all items (tracks/videos) in a Plex playlist.
    /// </summary>
    public async Task<List<VideoItem>> GetPlaylistItemsAsync(string ratingKey, CancellationToken ct = default)
    {
        var url = $"{_serverUrl}/playlists/{ratingKey}/items?X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url, ct);
        return ParseVideos(doc);
    }

    /// <summary>
    /// Get hub items with pagination.
    /// </summary>
    public async Task<PlexPage> GetHubItemsPageAsync(string hubKey, string hubType, int start, int count, CancellationToken ct = default)
    {
        var separator = hubKey.Contains('?') ? "&" : "?";
        var url = $"{_serverUrl}{hubKey}{separator}X-Plex-Container-Start={start}&X-Plex-Container-Size={count}&X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url, ct);

        var items = hubType switch
        {
            "artist" => ParsePlexItems(doc, PlexItemType.Artist),
            "album" => ParsePlexItems(doc, PlexItemType.Album),
            _ => ParseMixedItems(doc)
        };

        int totalSize = items.Count;
        if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("totalSize", out var ts))
            totalSize = ts.GetInt32();

        return new PlexPage { Items = items, TotalSize = totalSize };
    }

    /// <summary>
    /// Get playlist items with pagination.
    /// </summary>
    public async Task<PlexPage> GetPlaylistItemsPageAsync(string ratingKey, int start, int count, CancellationToken ct = default)
    {
        var url = $"{_serverUrl}/playlists/{ratingKey}/items?X-Plex-Container-Start={start}&X-Plex-Container-Size={count}&X-Plex-Token={_token}{StreamParam}";
        var doc = await FetchJsonAsync(url, ct);
        var items = ParseMixedItems(doc);

        int totalSize = items.Count;
        if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("totalSize", out var ts))
            totalSize = ts.GetInt32();

        return new PlexPage { Items = items, TotalSize = totalSize };
    }

    /// <summary>
    /// Build a direct-play URL for a Plex media item.
    /// When stereo audio is enabled and the source has surround audio, appends the
    /// stereo stream ID if one exists, otherwise falls back to transcoding with maxAudioChannels=2.
    /// When the source only has stereo (or fewer), direct-plays without modification.
    /// </summary>
    public string GetStreamUrl(string partKey, int? stereoStreamId = null, int maxAudioChannels = 0, string? ratingKey = null)
    {
        if (_stereoAudio)
        {
            if (stereoStreamId.HasValue)
            {
                // Direct play with the stereo audio stream selected — no transcode needed
                return $"{_serverUrl}{partKey}?audioStreamID={stereoStreamId.Value}&X-Plex-Token={_token}";
            }

            if (maxAudioChannels > 2 || maxAudioChannels == 0)
            {
                // Surround or unknown channel layout — use the universal decision endpoint
                // to negotiate a transcoded stream with stereo audio
                var metadataPath = !string.IsNullOrEmpty(ratingKey)
                    ? $"/library/metadata/{ratingKey}"
                    : partKey;
                var encodedPath = Uri.EscapeDataString(metadataPath);
                var session = Guid.NewGuid().ToString("N");

                // Client profile telling Plex which output codecs/containers we accept.
                // Without this, Plex cannot find a transcode target and returns 400.
                var profileExtra = Uri.EscapeDataString(
                    "append-transcode-target-codec(type=videoProfile&context=streaming&protocol=hls&container=mpegts&videoCodec=h264&audioCodec=aac,mp3)"
                    + "+append-transcode-target-codec(type=videoProfile&context=streaming&protocol=hls&container=fmp4&videoCodec=h264&audioCodec=aac,mp3)");

                return $"{_serverUrl}/video/:/transcode/universal/start"
                    + $"?path={encodedPath}"
                    + $"&mediaIndex=0&partIndex=0"
                    + $"&offset=0"
                    + $"&protocol=hls"
                    + $"&copyts=1"
                    + $"&directPlay=0&directStream=1"
                    + $"&videoQuality=100&maxVideoBitrate=20000"
                    + $"&videoCodec=h264"
                    + $"&audioCodec=aac"
                    + $"&maxAudioChannels=2"
                    + $"&context=streaming"
                    + $"&autoAdjustQuality=0"
                    + $"&session={session}"
                    + $"&X-Plex-Product=Phosphor"
                    + $"&X-Plex-Platform=Chrome"
                    + $"&X-Plex-Client-Identifier={session}"
                    + $"&X-Plex-Device=PC"
                    + $"&X-Plex-Device-Name=Phosphor"
                    + $"&X-Plex-Version=1.0.0"
                    + $"&X-Plex-Client-Profile-Extra={profileExtra}"
                    + $"&X-Plex-Token={_token}";
            }
        }
        return $"{_serverUrl}{partKey}?X-Plex-Token={_token}";
    }

    /// <summary>
    /// Test the connection to the Plex server.
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var url = $"{_serverUrl}/identity?X-Plex-Token={_token}";
            var response = await _http.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private Task<JsonDocument> FetchJsonAsync(string url) => FetchJsonAsync(url, CancellationToken.None);

    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private List<VideoItem> ParseVideos(JsonDocument doc)
    {
        var items = new List<VideoItem>();
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc) ||
            !mc.TryGetProperty("Metadata", out var metadata))
            return items;

        foreach (var m in metadata.EnumerateArray())
        {
            var item = MapToVideoItem(m);
            if (item != null)
                items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Parse items that may be a mix of tracks, albums, and artists.
    /// Tracks are mapped as playable items; albums/artists as drill-down items.
    /// </summary>
    private List<VideoItem> ParseMixedItems(JsonDocument doc)
    {
        var items = new List<VideoItem>();
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc) ||
            !mc.TryGetProperty("Metadata", out var metadata))
            return items;

        foreach (var m in metadata.EnumerateArray())
        {
            var type = m.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            VideoItem? item = type switch
            {
                "artist" => MapToPlexItem(m, PlexItemType.Artist),
                "album" => MapToPlexItem(m, PlexItemType.Album),
                _ => MapToVideoItem(m)
            };
            if (item != null)
                items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Parse Plex items that may not have Media/Part (artists, albums).
    /// </summary>
    private List<VideoItem> ParsePlexItems(JsonDocument doc, PlexItemType itemType)
    {
        var items = new List<VideoItem>();
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc) ||
            !mc.TryGetProperty("Metadata", out var metadata))
            return items;

        foreach (var m in metadata.EnumerateArray())
        {
            var item = MapToPlexItem(m, itemType);
            if (item != null)
                items.Add(item);
        }
        return items;
    }

    private List<VideoItem> ParseSearchResults(JsonDocument doc)
    {
        var items = new List<VideoItem>();
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc) ||
            !mc.TryGetProperty("Hub", out var hubs))
            return items;

        foreach (var hub in hubs.EnumerateArray())
        {
            var type = hub.GetProperty("type").GetString() ?? "";
            if (type is not ("movie" or "episode" or "track"))
                continue;

            if (!hub.TryGetProperty("Metadata", out var metadata))
                continue;

            foreach (var m in metadata.EnumerateArray())
            {
                var item = MapToVideoItem(m);
                if (item != null)
                    items.Add(item);
            }
        }
        return items;
    }

    private VideoItem? MapToVideoItem(JsonElement m)
    {
        // Extract the part key and look for a stereo audio stream
        string? partKey = null;
        int? stereoStreamId = null;
        int audioChannels = 0;
        var diagStreams = new List<string>();
        string? diagPrimaryLang = null;
        int mediaLevelChannels = 0;
        string mediaLevelAudioCodec = "";
        if (m.TryGetProperty("Media", out var mediaArr))
        {
            foreach (var media in mediaArr.EnumerateArray())
            {
                // Capture Media-level audio info (always present even without Stream details)
                if (media.TryGetProperty("audioChannels", out var maCh))
                    mediaLevelChannels = maCh.GetInt32();
                if (media.TryGetProperty("audioCodec", out var maCodec))
                    mediaLevelAudioCodec = maCodec.GetString() ?? "";

                if (media.TryGetProperty("Part", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        partKey = part.GetProperty("key").GetString();

                        // Check if Stream data is present at all
                        bool hasStreams = part.TryGetProperty("Stream", out var streams);
                        diagStreams.Add($"[Part hasStream={hasStreams}]");

                        // Scan audio streams for channel info and stereo stream ID
                        int maxChannels = 0;
                        string? primaryLanguage = null;
                        if (hasStreams)
                        {
                            // First pass: find the primary (default) audio stream's language and max channels
                            foreach (var stream in streams.EnumerateArray())
                            {
                                // Log ALL streams (video, audio, subtitle) for full picture
                                var sType = stream.TryGetProperty("streamType", out var stv) ? stv.GetInt32() : -1;
                                var sCodec = stream.TryGetProperty("codec", out var sc) ? sc.GetString() ?? "" : "";
                                var sCh = stream.TryGetProperty("channels", out var sch) ? sch.GetInt32() : 0;
                                var sId = stream.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0;
                                var sLang = stream.TryGetProperty("languageTag", out var sl) ? sl.GetString() ?? ""
                                    : stream.TryGetProperty("language", out var sl2) ? sl2.GetString() ?? "" : "";
                                var sDef = stream.TryGetProperty("default", out var sdf) && sdf.ValueKind == JsonValueKind.True;
                                var sTitle = stream.TryGetProperty("displayTitle", out var sdt) ? sdt.GetString() ?? "" : "";
                                diagStreams.Add($"id={sId} type={sType} codec={sCodec} ch={sCh} lang={sLang} default={sDef} title=\"{sTitle}\"");

                                if (sType == 2)
                                {
                                    int ch = sCh;
                                    if (ch > maxChannels) maxChannels = ch;

                                    // The first audio stream (or one marked default) defines the primary language
                                    if (primaryLanguage == null || sDef)
                                    {
                                        primaryLanguage = !string.IsNullOrEmpty(sLang) ? sLang : null;
                                    }
                                }
                            }

                            diagPrimaryLang = primaryLanguage;

                            // Second pass: find a stereo stream that matches the primary language
                            if (_stereoAudio && maxChannels > 2)
                            {
                                foreach (var stream in streams.EnumerateArray())
                                {
                                    if (stream.TryGetProperty("streamType", out var st) && st.GetInt32() == 2)
                                    {
                                        int ch = stream.TryGetProperty("channels", out var chVal) ? chVal.GetInt32() : 0;
                                        if (ch != 2) continue;
                                        if (!stream.TryGetProperty("id", out var id)) continue;

                                        // Only accept the stereo stream if its language matches the primary
                                        var streamLang = stream.TryGetProperty("languageTag", out var lang) ? lang.GetString()
                                            : stream.TryGetProperty("language", out var lang2) ? lang2.GetString()
                                            : null;

                                        if (primaryLanguage == null || streamLang == null
                                            || string.Equals(primaryLanguage, streamLang, StringComparison.OrdinalIgnoreCase))
                                        {
                                            stereoStreamId = id.GetInt32();
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        audioChannels = maxChannels;

                        // Fall back to Media-level audioChannels if stream-level info was unavailable
                        if (audioChannels == 0 && mediaLevelChannels > 0)
                            audioChannels = mediaLevelChannels;

                        break;
                    }
                }
                if (partKey != null) break;
            }
        }

        if (partKey == null) return null;

        var title = m.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var artist = m.TryGetProperty("grandparentTitle", out var gp) ? gp.GetString() ?? ""
                   : m.TryGetProperty("parentTitle", out var pt) ? pt.GetString() ?? "" : "";

        TimeSpan? duration = null;
        if (m.TryGetProperty("duration", out var dur) && dur.TryGetInt64(out var ms))
            duration = TimeSpan.FromMilliseconds(ms);

        string thumbUrl = "";
        if (m.TryGetProperty("thumb", out var thumb))
            thumbUrl = BuildThumbnailUrl(thumb.GetString());

        var ratingKey = m.TryGetProperty("ratingKey", out var rkVal) ? rkVal.GetString() ?? "" : "";
        var streamUrl = GetStreamUrl(partKey, stereoStreamId, audioChannels, ratingKey);

        var metaType = m.TryGetProperty("type", out var mt) ? mt.GetString() ?? "" : "";
        var isAudio = metaType == "track";

        // Determine audio stream info for status display
        PlexAudioStream audioStream;
        if (audioChannels == 0)
            audioStream = _stereoAudio ? PlexAudioStream.StereoTranscode : PlexAudioStream.Unknown;
        else if (!_stereoAudio)
            audioStream = audioChannels <= 2 ? PlexAudioStream.Stereo : PlexAudioStream.Surround;
        else if (stereoStreamId.HasValue)
            audioStream = PlexAudioStream.Stereo;
        else
            audioStream = audioChannels <= 2 ? PlexAudioStream.Stereo : PlexAudioStream.StereoTranscode;

        // Diagnostic logging for audio stream analysis
        DiagLog($"--- {title} ({artist}) ---");
        DiagLog($"  partKey={partKey}  stereoAudio={_stereoAudio}");
        DiagLog($"  Media-level: audioChannels={mediaLevelChannels} audioCodec={mediaLevelAudioCodec}");
        DiagLog($"  maxChannels={audioChannels}  stereoStreamId={stereoStreamId?.ToString() ?? "(none)"}  primaryLang={diagPrimaryLang ?? "(none)"}");
        foreach (var ds in diagStreams)
            DiagLog($"  stream: {ds}");
        DiagLog($"  => PlexAudioStream={audioStream}  streamUrl={streamUrl}");

        // Parse chapter markers if present
        List<ChapterMarker>? chapters = null;
        if (m.TryGetProperty("Chapter", out var chapterArr))
        {
            chapters = [];
            foreach (var ch in chapterArr.EnumerateArray())
            {
                var chTitle = ch.TryGetProperty("tag", out var ct) ? ct.GetString() ?? "" : "";
                var startMs = ch.TryGetProperty("startTimeOffset", out var sto) ? sto.GetInt64() : 0;
                var endMs = ch.TryGetProperty("endTimeOffset", out var eto) ? eto.GetInt64() : 0;
                chapters.Add(new ChapterMarker
                {
                    Title = chTitle,
                    StartTime = TimeSpan.FromMilliseconds(startMs),
                    EndTime = TimeSpan.FromMilliseconds(endMs)
                });
            }
            if (chapters.Count == 0) chapters = null;
            else DiagLog($"  chapters: {chapters.Count} found");
        }

        return new VideoItem
        {
            Title = title,
            Author = artist,
            ThumbnailUrl = thumbUrl,
            VideoId = $"plex:{partKey}",
            Duration = duration,
            StreamUrl = streamUrl,
            PlexItemType = isAudio ? PlexItemType.Track : PlexItemType.None,
            PlexRatingKey = ratingKey,
            IsAudioOnly = isAudio,
            PlexAudioStream = audioStream,
            Chapters = chapters
        };
    }

    /// <summary>
    /// Map a Plex metadata element to a VideoItem for non-playable items (artists, albums).
    /// </summary>
    private VideoItem? MapToPlexItem(JsonElement m, PlexItemType itemType)
    {
        var title = m.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var ratingKey = m.TryGetProperty("ratingKey", out var rk) ? rk.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(ratingKey)) return null;

        var artist = m.TryGetProperty("parentTitle", out var pt) ? pt.GetString() ?? "" : "";

        string thumbUrl = "";
        if (m.TryGetProperty("thumb", out var thumb))
            thumbUrl = BuildThumbnailUrl(thumb.GetString());

        return new VideoItem
        {
            Title = title,
            Author = artist,
            ThumbnailUrl = thumbUrl,
            VideoId = $"plex:{itemType}:{ratingKey}",
            PlexItemType = itemType,
            PlexRatingKey = ratingKey
        };
    }
}

public class PlexLibrary
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
}

public class PlexPage
{
    public List<VideoItem> Items { get; set; } = [];
    public int TotalSize { get; set; }
}

public class PlexHub
{
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string HubKey { get; set; } = "";
    public int Size { get; set; }
}

public class PlexPlaylist
{
    public string RatingKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string PlaylistType { get; set; } = "";
    public bool Smart { get; set; }
    public int LeafCount { get; set; }
    public string Thumb { get; set; } = "";
}
