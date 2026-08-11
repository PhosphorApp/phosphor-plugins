using System.Net;
using System.Text;
using System.Text.Json;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// Minimal SiriusXM REST client: authenticates a subscriber, enumerates the channel lineup, and
/// resolves a channel's live HLS master-playlist URL. Pure <see cref="HttpClient"/> +
/// <see cref="CookieContainer"/> — no browser, no external tools. Flow reverse-engineered from
/// <c>AngellusMortis/sxm-client</c>.
/// </summary>
/// <remarks>
/// Session is cookie-based: <c>SXMAUTHNEW</c> = logged in; <c>AWSALB</c>+<c>JSESSIONID</c> =
/// authenticated. Authenticated akamai (segment/playlist) requests additionally need the query
/// params <c>token</c> (from <c>SXMAKTOKEN</c>), <c>consumer=k2</c>, and <c>gupId</c> (from
/// <c>SXMDATA</c>), exposed via <see cref="TokenParams"/>.
/// </remarks>
public sealed class SxmClient
{
    private const string RestV2 = "https://player.siriusxm.com/rest/v2/experience/modules/{0}";
    private const string RestV4 = "https://player.siriusxm.com/rest/v4/experience/modules/{0}";
    private const string AppVersion = "5.36.514";
    private const string DeviceModel = "EverestWebClient";

    // Player buffer behind the broadcast live edge, in ms. The now-playing selection anchors to
    // (liveEdge - this). Keep this in sync with SxmProxy.SegmentDelayCount (segments × ~10s): the proxy
    // plays that far behind live via a DVR offset, so the audio aligns with SXM's late-published cut
    // metadata. 6 segments ≈ 60s. Set both to 0 to play at the live edge again (label then trails).
    private const long LiveAudioLagMs = 0;
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:89.0) Gecko/20100101 Firefox/89.0";

    private readonly string _username;
    private readonly string _password;
    private readonly string _region;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public SxmClient(string username, string password, string region = "US", Action<string>? log = null)
    {
        _username = username;
        _password = password;
        _region = string.IsNullOrWhiteSpace(region) ? "US" : region;
        _log = log;
        var handler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public bool IsLoggedIn => HasCookie("SXMAUTHNEW");
    public bool IsAuthenticated => HasCookie("AWSALB") && HasCookie("JSESSIONID");

    /// <summary>Logs in and resumes a session. Returns true when authenticated.</summary>
    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        var loginBody = Device();
        loginBody["standardAuth"] = new Dictionary<string, object>
        {
            ["username"] = _username,
            ["password"] = _password,
        };
        await PostAsync("modify/authentication", loginBody, RestV2, ct: ct);
        if (!IsLoggedIn) { _log?.Invoke("SXM login failed (no SXMAUTHNEW)."); return false; }

        await PostAsync("resume?OAtrial=false", Device(), RestV2, ct: ct);
        if (!IsAuthenticated) { _log?.Invoke("SXM resume failed (no session cookies)."); return false; }
        return true;
    }

    /// <summary>Fetches the account's channel lineup.</summary>
    public async Task<IReadOnlyList<SxmChannel>> GetChannelsAsync(CancellationToken ct = default)
    {
        var req = new Dictionary<string, object>
        {
            ["consumeRequests"] = new List<object>(),
            ["resultTemplate"] = "responsive",
            ["alerts"] = new List<object>(),
            ["profileInfos"] = new List<object>(),
        };
        var resp = await PostAsync("get?type=2", req, RestV4, channelList: true, ct: ct);
        var list = new List<SxmChannel>();
        if (resp is not { } r) return list;
        try
        {
            var channels = r.GetProperty("moduleList").GetProperty("modules")[0]
                .GetProperty("moduleResponse").GetProperty("contentData")
                .GetProperty("channelListing").GetProperty("channels");
            foreach (var ch in channels.EnumerateArray())
            {
                var id = Str(ch, "channelId") ?? Str(ch, "id") ?? "";
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new SxmChannel(
                    id,
                    Str(ch, "name") ?? id,
                    Str(ch, "channelNumber") ?? "",
                    Str(ch, "channelGuid") ?? Str(ch, "guid") ?? "",
                    ExtractThumb(ch),
                    ExtractCategories(ch)));
            }
        }
        catch (Exception ex) { _log?.Invoke($"SXM channel parse failed: {ex.Message}"); }
        return list;
    }

    /// <summary>
    /// Resolves the live master-playlist URL for a channel (HLS roots substituted). Returns null if
    /// the channel isn't live or resolution failed.
    /// </summary>
    public async Task<string?> ResolveMasterPlaylistAsync(SxmChannel channel, CancellationToken ct = default)
    {
        var (primary, secondary) = await GetHlsRootsAsync(ct);
        if (primary == null) { _log?.Invoke("SXM: no Live_Primary_HLS root."); return null; }

        var now = DateTimeOffset.UtcNow;
        var npParams = new Dictionary<string, string>
        {
            ["assetGUID"] = channel.Guid,
            ["ccRequestType"] = "AUDIO_VIDEO",
            ["channelId"] = channel.Id,
            ["hls_output_mode"] = "custom",
            ["marker_mode"] = "all_separate_cue_points",
            ["result-template"] = "web",
            ["time"] = now.ToUnixTimeMilliseconds().ToString(),
            ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z",
        };
        var np = await GetAsync("tune/now-playing-live", npParams, RestV2, ct);
        var template = ExtractMasterUrl(np);
        if (template == null) { _log?.Invoke("SXM: no HLS URL in now-playing."); return null; }

        return template
            .Replace("%Live_Primary_HLS%", primary)
            .Replace("%Live_Secondary_HLS%", secondary ?? primary);
    }

    /// <summary>
    /// Resolves the currently-airing track for a channel from <c>tune/now-playing-live</c> (the same
    /// endpoint used to resolve the stream URL). Returns the latest "Song" cut whose start time is at
    /// or before the live edge, plus the next expected change time. Returns null when nothing usable
    /// is available (e.g. a talk break) — the caller may then fall back to the show/episode title.
    /// </summary>
    public async Task<SxmNowPlaying?> GetNowPlayingAsync(SxmChannel channel, TimeSpan? playbackPosition = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var npParams = new Dictionary<string, string>
        {
            ["assetGUID"] = channel.Guid,
            ["ccRequestType"] = "AUDIO_VIDEO",
            ["channelId"] = channel.Id,
            ["hls_output_mode"] = "custom",
            ["marker_mode"] = "all_separate_cue_points",
            ["result-template"] = "web",
            ["time"] = now.ToUnixTimeMilliseconds().ToString(),
            ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z",
        };
        var np = await GetAsync("tune/now-playing-live", npParams, RestV2, ct);
        return ExtractNowPlaying(np, playbackPosition, _log);
    }

    /// <summary>
    /// Parses the now-playing-live response into the current track. The "cut" marker layer mixes real
    /// songs with station-ID stingers (cutContentType "Link") and DJ talk breaks ("Exp"); only "Song"
    /// cuts are surfaced. Selection is anchored to the <em>audio instant</em> the listener actually
    /// hears — the stream's <c>TUNE_START</c> wall-clock plus <paramref name="playbackPosition"/> —
    /// not the live edge, because live playback runs behind the broadcast by a variable amount. The
    /// current song is the cut whose [start, start+duration) window contains that instant. Falls back
    /// to the live edge when the position/tune-start aren't available, and to the show title for talk.
    /// </summary>
    private static SxmNowPlaying? ExtractNowPlaying(JsonElement? np, TimeSpan? playbackPosition, Action<string>? log = null)
    {
        if (np is not { } r) return null;
        try
        {
            var live = r.GetProperty("moduleList").GetProperty("modules")[0]
                .GetProperty("moduleResponse").GetProperty("liveChannelData");

            long liveEdge = FindLiveEdge(live);

            // The instant the listener is actually hearing. SXM's TUNE_START drifts (it's refreshed on
            // each poll, not a fixed stream origin), so anchoring to TUNE_START+elapsed lands too far in
            // the past (observed ~1-2 min behind the audio). The player's real buffer behind the live
            // edge is small and roughly constant, so anchor to (liveEdge - a fixed lag) instead. Tune
            // LiveAudioLagMs against the diagnostics if the label still leads/trails the audio.
            long audioInstant = liveEdge - LiveAudioLagMs;
            long tuneStart = FindTuneStart(live);
            _ = playbackPosition; // retained in the API for sources that can use a true session origin.
            JsonElement? bestSong = null;
            long bestTime = long.MinValue;
            long nextChange = 0;

            if (live.TryGetProperty("markerLists", out var markerLists))
            {
                foreach (var list in markerLists.EnumerateArray())
                {
                    if (Str(list, "layer") != "cut") continue;
                    if (!list.TryGetProperty("markers", out var markers)) continue;
                    foreach (var m in markers.EnumerateArray())
                    {
                        if (!m.TryGetProperty("cut", out var cut)) continue;
                        if (Str(cut, "cutContentType") != "Song") continue;
                        if (!m.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                        var time = t.GetInt64();
                        // The song whose window contains the audio instant: latest start at/before it.
                        if (time <= audioInstant && time > bestTime)
                        {
                            bestTime = time;
                            bestSong = cut;
                            // Next change = this cut's start + its duration (seconds).
                            nextChange = m.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                                ? time + (long)(d.GetDouble() * 1000) : 0;
                        }
                    }
                }
            }

            if (bestSong is { } song)
            {
                var title = Str(song, "title");
                string? artist = null;
                if (song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                    && artists.GetArrayLength() > 0)
                {
                    artist = Str(artists[0], "name");
                }
                var album = song.TryGetProperty("album", out var al) ? Str(al, "title") : null;
                // NextChangeUtc is a wall-clock at the AUDIO timeline: shift the cut-end (broadcast
                // time) back by the listener's lag so the poll fires when the change is actually heard.
                DateTimeOffset? next = null;
                if (nextChange > 0)
                {
                    long lag = audioInstant > 0 ? liveEdge - audioInstant : 0;
                    next = DateTimeOffset.FromUnixTimeMilliseconds(nextChange - Math.Max(0, lag));
                }
                // Diagnostics: log the anchor math + chosen song so mismatches can be solved from
                // real numbers rather than guessed. All times epoch-ms UTC; deltas in seconds.
                if (log != null)
                {
                    string F(long ms) => ms <= 0 ? "-" : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("HH:mm:ss");
                    double edgeToAudio = (liveEdge - audioInstant) / 1000.0;
                    double songToAudio = (audioInstant - bestTime) / 1000.0;
                    log($"SXM np: pos={playbackPosition?.TotalSeconds:F0}s tuneStart={F(tuneStart)} " +
                        $"liveEdge={F(liveEdge)} audioInstant={F(audioInstant)} " +
                        $"(edge-audio={edgeToAudio:F0}s) songStart={F(bestTime)} (audio-songStart={songToAudio:F0}s) " +
                        $"=> '{artist} - {title}'");
                }
                if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist))
                    return new SxmNowPlaying(title, artist, album, next);
            }

            // No current song (e.g. a talk break) — fall back to the current episode/show title.
            var showTitle = FindCurrentEpisodeTitle(live, audioInstant);
            return showTitle is null ? null : new SxmNowPlaying(showTitle, null, null, null);
        }
        catch
        {
            return null;
        }
    }

    // The stream-start wall-clock (epoch ms): customAudioInfos[].position where position=="TUNE_START".
    private static long FindTuneStart(JsonElement live)
    {
        try
        {
            if (!live.TryGetProperty("customAudioInfos", out var infos)) return 0;
            foreach (var info in infos.EnumerateArray())
            {
                if (!info.TryGetProperty("position", out var pos)) continue;
                if (Str(pos, "position") != "TUNE_START") continue;
                var ts = Str(pos, "timestamp");
                if (ts != null && DateTimeOffset.TryParse(ts, out var dto))
                    return dto.ToUnixTimeMilliseconds();
            }
        }
        catch { }
        return 0;
    }

    // The live edge (epoch ms): the "livepoint" cue point marker. Falls back to "now" when absent.
    private static long FindLiveEdge(JsonElement live)
    {
        try
        {
            if (live.TryGetProperty("cuePointList", out var cpl) &&
                cpl.TryGetProperty("cuePoints", out var cps))
            {
                foreach (var cp in cps.EnumerateArray())
                {
                    if (Str(cp, "layer") == "livepoint" &&
                        cp.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                        return t.GetInt64();
                }
            }
        }
        catch { }
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // The current show/episode title for talk content: latest "episode" marker at or before the edge.
    private static string? FindCurrentEpisodeTitle(JsonElement live, long liveEdge)
    {
        try
        {
            if (!live.TryGetProperty("markerLists", out var markerLists)) return null;
            string? best = null;
            long bestTime = long.MinValue;
            foreach (var list in markerLists.EnumerateArray())
            {
                if (Str(list, "layer") != "episode") continue;
                if (!list.TryGetProperty("markers", out var markers)) continue;
                foreach (var m in markers.EnumerateArray())
                {
                    if (!m.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                    var time = t.GetInt64();
                    if (time > liveEdge || time <= bestTime) continue;
                    if (!m.TryGetProperty("episode", out var ep)) continue;
                    var title = Str(ep, "longTitle") ?? Str(ep, "mediumTitle");
                    if (ep.TryGetProperty("show", out var show))
                        title = Str(show, "longTitle") ?? Str(show, "mediumTitle") ?? title;
                    if (!string.IsNullOrWhiteSpace(title)) { best = title; bestTime = time; }
                }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>Query string of auth params required on every akamai playlist/segment request.</summary>
    public string TokenParams =>        $"token={Uri.EscapeDataString(SxmToken() ?? "")}&consumer=k2&gupId={Uri.EscapeDataString(GupId() ?? "")}";

    /// <summary>The shared HTTP client (carries the session cookies) for the proxy to reuse.</summary>
    public HttpClient Http => _http;

    // ── HLS roots (from get/configuration) ──────────────────────────────────────

    private async Task<(string? primary, string? secondary)> GetHlsRootsAsync(CancellationToken ct)
    {
        var cfg = await GetAsync("get/configuration", new Dictionary<string, string>
        {
            ["result-template"] = "html5",
            ["app-region"] = _region,
            ["cacheBuster"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        }, RestV2, ct);
        if (cfg is not { } r) return (null, null);
        try
        {
            var components = r.GetProperty("moduleList").GetProperty("modules")[0]
                .GetProperty("moduleResponse").GetProperty("configuration").GetProperty("components");
            string? primary = null, secondary = null;
            foreach (var comp in components.EnumerateArray())
            {
                if (!comp.TryGetProperty("settings", out var settings)) continue;
                foreach (var s in settings.EnumerateArray())
                {
                    if (!s.TryGetProperty("relativeUrls", out var rels)) continue;
                    foreach (var u in rels.EnumerateArray())
                    {
                        var name = Str(u, "name");
                        var url = Str(u, "url");
                        if (url == null) continue;
                        if (name == "Live_Primary_HLS") primary = url;
                        else if (name == "Live_Secondary_HLS") secondary = url;
                    }
                }
            }
            return (primary, secondary);
        }
        catch (Exception ex) { _log?.Invoke($"SXM HLS-root extract failed: {ex.Message}"); return (null, null); }
    }

    private static string? ExtractMasterUrl(JsonElement? np)
    {
        if (np is not { } r) return null;
        try
        {
            var live = r.GetProperty("moduleList").GetProperty("modules")[0]
                .GetProperty("moduleResponse").GetProperty("liveChannelData");
            foreach (var key in new[] { "hlsAudioInfos", "customAudioInfos" })
            {
                if (!live.TryGetProperty(key, out var arr)) continue;
                foreach (var info in arr.EnumerateArray())
                {
                    var url = Str(info, "url");
                    if (url != null && url.Contains("%Live_")) return url;
                }
            }
        }
        catch { }
        return null;
    }

    // ── Request plumbing ────────────────────────────────────────────────────────

    private Dictionary<string, object> Device() => new()
    {
        ["resultTemplate"] = "web",
        ["deviceInfo"] = new Dictionary<string, object>
        {
            ["osVersion"] = "Windows",
            ["platform"] = "Web",
            ["sxmAppVersion"] = AppVersion,
            ["browser"] = "Firefox",
            ["browserVersion"] = "89.0",
            ["appRegion"] = _region,
            ["deviceModel"] = DeviceModel,
            ["clientDeviceId"] = "null",
            ["player"] = "html5",
            ["clientDeviceType"] = "web",
        },
    };

    private async Task<JsonElement?> PostAsync(
        string path, Dictionary<string, object> moduleRequest, string urlFormat,
        bool channelList = false, CancellationToken ct = default)
    {
        var module = new Dictionary<string, object> { ["moduleRequest"] = moduleRequest };
        if (channelList)
        {
            module["moduleArea"] = "Discovery";
            module["moduleType"] = "ChannelListing";
            module["moduleRequest"] = new Dictionary<string, object> { ["resultTemplate"] = "responsive" };
        }
        var envelope = new Dictionary<string, object>
        {
            ["moduleList"] = new Dictionary<string, object> { ["modules"] = new List<object> { module } },
        };
        var url = string.Format(urlFormat, path);
        using var content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.PostAsync(url, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var root = JsonDocument.Parse(text).RootElement;
            return root.TryGetProperty("ModuleListResponse", out var mlr) ? mlr.Clone() : root.Clone();
        }
        catch (Exception ex) { _log?.Invoke($"SXM POST '{path}' failed: {ex.Message}"); return null; }
    }

    private async Task<JsonElement?> GetAsync(
        string path, Dictionary<string, string> queryParams, string urlFormat, CancellationToken ct)
    {
        var qs = string.Join("&", queryParams.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = string.Format(urlFormat, path) + "?" + qs;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var root = JsonDocument.Parse(text).RootElement;
            return root.TryGetProperty("ModuleListResponse", out var mlr) ? mlr.Clone() : root.Clone();
        }
        catch (Exception ex) { _log?.Invoke($"SXM GET '{path}' failed: {ex.Message}"); return null; }
    }

    // ── Cookie/token helpers ────────────────────────────────────────────────────

    private bool HasCookie(string name) =>
        _cookies.GetAllCookies().Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private string? SxmToken()
    {
        var c = _cookies.GetAllCookies().FirstOrDefault(x =>
            string.Equals(x.Name, "SXMAKTOKEN", StringComparison.OrdinalIgnoreCase));
        if (c == null) return null;
        var eq = c.Value.IndexOf('=');
        var after = eq < 0 ? c.Value : c.Value[(eq + 1)..];
        var comma = after.IndexOf(',');
        return comma < 0 ? after : after[..comma];
    }

    private string? GupId()
    {
        var c = _cookies.GetAllCookies().FirstOrDefault(x =>
            string.Equals(x.Name, "SXMDATA", StringComparison.OrdinalIgnoreCase));
        if (c == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(Uri.UnescapeDataString(c.Value));
            return doc.RootElement.TryGetProperty("gupId", out var g) ? g.ToString() : null;
        }
        catch { return null; }
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ExtractThumb(JsonElement ch)
    {
        // Channel art lives under images.images[].url; grab the first usable one (best-effort).
        try
        {
            if (ch.TryGetProperty("images", out var images) &&
                images.TryGetProperty("images", out var arr))
            {
                foreach (var img in arr.EnumerateArray())
                {
                    var url = Str(img, "url");
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            }
        }
        catch { }
        return null;
    }

    private static IReadOnlyList<SxmCategoryRef> ExtractCategories(JsonElement ch)
    {
        // Channels carry categories.categories[] with a stable key (e.g. "rock", "nflplay") and a
        // human name. We keep both: the key drives grouping (survives display-name changes), the
        // name labels the tile.
        var cats = new List<SxmCategoryRef>();
        try
        {
            if (ch.TryGetProperty("categories", out var categories) &&
                categories.TryGetProperty("categories", out var arr))
            {
                foreach (var cat in arr.EnumerateArray())
                {
                    var key = Str(cat, "key");
                    if (string.IsNullOrWhiteSpace(key) || cats.Any(c => c.Key == key)) continue;
                    cats.Add(new SxmCategoryRef(key, Str(cat, "name") ?? key));
                }
            }
        }
        catch { }
        return cats;
    }
}

/// <summary>A SiriusXM channel from the lineup.</summary>
/// <param name="Id">The channelId slug (e.g. "octane"), used for tuning.</param>
/// <param name="Name">Display name (e.g. "Octane").</param>
/// <param name="Number">Channel number as a string (e.g. "37").</param>
/// <param name="Guid">Channel GUID, required by now-playing-live.</param>
/// <param name="ThumbnailUrl">Optional channel logo URL.</param>
/// <param name="Categories">Categories the channel belongs to (key + name), from the lineup.</param>
public sealed record SxmChannel(
    string Id, string Name, string Number, string Guid, string? ThumbnailUrl, IReadOnlyList<SxmCategoryRef> Categories)
{
    public int SortNumber => int.TryParse(Number, out var n) ? n : int.MaxValue;
}

/// <summary>A category a channel belongs to: a stable <paramref name="Key"/> (e.g. "nflplay") and a
/// display <paramref name="Name"/> (e.g. "NFL Play-by-Play").</summary>
public sealed record SxmCategoryRef(string Key, string Name);

/// <summary>The currently-airing track on a channel, parsed from now-playing-live cut markers.</summary>
/// <param name="Title">Song title (or show/episode title for talk content).</param>
/// <param name="Artist">Performing artist(s), or null for talk content.</param>
/// <param name="Album">Album title, when known.</param>
/// <param name="NextChangeUtc">When the current cut is expected to end, for poll scheduling; null if unknown.</param>
public sealed record SxmNowPlaying(string? Title, string? Artist, string? Album, DateTimeOffset? NextChangeUtc);
