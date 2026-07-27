using System.Net.Http;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor;

/// <summary>
/// Plex Live TV client + session lifecycle, kept separate from the on-demand <see cref="PlexService"/>
/// so the live REST surface and the tuner-holding session machine don't bloat it. Responsibilities:
/// <list type="bullet">
///   <item>Discover the server's DVR(s) (<c>/livetv/dvrs</c>).</item>
///   <item>Read a DVR's live channel lineup (<c>/{epg}/lineups/dvr/channels</c>).</item>
///   <item>Enrich channels with "what's on now" from the EPG grid (<c>/{epg}/grid</c>).</item>
///   <item>Open a live playback session (tune → universal HLS manifest), keep it alive, and — most
///   importantly — <b>stop</b> it to release the physical tuner.</item>
/// </list>
/// </summary>
/// <remarks>
/// There is no host "playback stopped" callback, so teardown is self-managed: at most one live
/// session per instance is held; opening a new one first stops the prior; a keep-alive timer pings
/// the session while it plays; and <see cref="PanicCleanupAsync"/> stops strays on init/settings
/// change. The blast radius of a missed stop is one tuner until Plex's idle timeout — this design
/// keeps that from happening in normal operation.
/// </remarks>
public sealed class PlexLiveTvService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string _serverUrl = "";
    private string _token = "";

    // A stable client identity for our sessions, so PanicCleanup can recognize/stop our own strays.
    private readonly string _clientId = "phosphor-plex-livetv-" + Guid.NewGuid().ToString("N")[..8];

    private readonly object _sessionGate = new();
    private PlexLiveSession? _active;
    private System.Threading.Timer? _keepAlive;

    /// <summary>Diagnostics sink, wired by <c>PlexSource</c> (see <see cref="PlexService.Log"/>).</summary>
    public Action<LogLevel, string, string> Log { get; set; } = static (_, _, _) => { };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_serverUrl) && !string.IsNullOrWhiteSpace(_token);

    public void Configure(string serverUrl, string token)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _token = token;
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>
    /// Discovers the server's DVRs. A server may have zero (no Live TV configured), one, or several;
    /// each becomes its own Live TV tile. Best-effort — returns an empty list on any failure.
    /// </summary>
    public async Task<List<PlexDvr>> GetDvrsAsync(CancellationToken ct = default)
    {
        var dvrs = new List<PlexDvr>();
        try
        {
            using var doc = await FetchJsonAsync($"{_serverUrl}/livetv/dvrs?X-Plex-Token={_token}", ct);
            if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
                mc.TryGetProperty("Dvr", out var arr))
            {
                foreach (var d in arr.EnumerateArray())
                {
                    var key = Str(d, "key");
                    var epg = Str(d, "epgIdentifier");
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(epg)) continue;
                    dvrs.Add(new PlexDvr
                    {
                        Key = key,
                        EpgIdentifier = epg,
                        Title = Str(d, "lineupTitle") is { Length: > 0 } t ? t : "Live TV",
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, "PlexLiveTV", $"GetDvrsAsync failed: {ex.Message}");
        }
        return dvrs;
    }

    // ── Channel lineup + "what's on now" ─────────────────────────────────────

    /// <summary>
    /// Reads a DVR's channel lineup and enriches each channel with the program airing right now
    /// (best-effort grid join). Channels are returned sorted by VCN.
    /// </summary>
    public async Task<List<PlexLiveChannel>> GetChannelsAsync(PlexDvr dvr, CancellationToken ct = default)
    {
        var channels = new List<PlexLiveChannel>();
        try
        {
            var url = $"{_serverUrl}/{dvr.EpgIdentifier}/lineups/dvr/channels?X-Plex-Token={_token}";
            using var doc = await FetchJsonAsync(url, ct);
            if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
                mc.TryGetProperty("Channel", out var arr))
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var id = Str(c, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    channels.Add(new PlexLiveChannel
                    {
                        Id = id,
                        Vcn = Str(c, "vcn"),
                        Title = Str(c, "title") is { Length: > 0 } t ? t : Str(c, "callSign"),
                        CallSign = Str(c, "callSign"),
                        ThumbnailUrl = Str(c, "thumb") is { Length: > 0 } th ? th : null,
                        IsHd = c.TryGetProperty("isHd", out var hd) && hd.ValueKind == JsonValueKind.True,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, "PlexLiveTV", $"GetChannelsAsync failed: {ex.Message}");
            return channels;
        }

        // Best-effort "what's on now" enrichment — never blocks the lineup.
        try
        {
            var now = await GetNowPlayingAsync(dvr, ct);
            foreach (var ch in channels)
                if (now.TryGetValue(ch.Id, out var prog))
                    ch.CurrentProgram = prog;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "PlexLiveTV", $"grid enrichment skipped: {ex.Message}");
        }

        return channels
            .OrderBy(c => ParseVcn(c.Vcn))
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The program airing right now on each channel, keyed by channelIdentifier, from the EPG grid.
    /// Uses a "now" time window (beginsAt &lt;&lt;= now &lt;&lt; endsAt).
    /// </summary>
    private async Task<Dictionary<string, string>> GetNowPlayingAsync(PlexDvr dvr, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // ">>=" and "<<" are Plex advanced-filter operators; their glyphs must be URL-encoded.
        var url = $"{_serverUrl}/{dvr.EpgIdentifier}/grid"
                + $"?beginsAt%3C%3C={now}&endsAt%3E%3E={now}&X-Plex-Token={_token}";
        using var doc = await FetchJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc) ||
            !mc.TryGetProperty("Metadata", out var arr))
            return map;

        foreach (var m in arr.EnumerateArray())
        {
            // Prefer "Show – Episode"; fall back to whichever title is present.
            var grandparent = Str(m, "grandparentTitle");
            var title = Str(m, "title");
            var label = !string.IsNullOrEmpty(grandparent) && !string.IsNullOrEmpty(title) && grandparent != title
                ? $"{grandparent} – {title}"
                : (!string.IsNullOrEmpty(grandparent) ? grandparent : title);
            if (string.IsNullOrEmpty(label)) continue;

            if (m.TryGetProperty("Media", out var media))
                foreach (var md in media.EnumerateArray())
                {
                    var chId = Str(md, "channelIdentifier");
                    if (!string.IsNullOrEmpty(chId) && !map.ContainsKey(chId))
                        map[chId] = label;
                }
        }
        return map;
    }

    // ── Playback session lifecycle ───────────────────────────────────────────

    /// <summary>
    /// Opens a live playback session for a channel and returns a ready-to-play HLS master-manifest
    /// URL. Tears down any prior session first (one tuner at a time). Throws on failure (e.g. all
    /// tuners busy) so the caller can badge the channel unavailable.
    /// </summary>
    public async Task<PlexLiveSession> OpenSessionAsync(PlexDvr dvr, string channelId, CancellationToken ct = default)
    {
        // Release the previous channel's tuner before grabbing another.
        await StopActiveAsync().ConfigureAwait(false);

        // Two DISTINCT session ids are involved and must not be conflated:
        //  • playbackSessionId — OUR generated id. The universal transcoder spins up the playable HLS
        //    stream under this id; the manifest, keep-alive, and transcode-stop all use it.
        //  • tunerSessionId — Plex's SERVER-assigned live-session id (recovered from the tune Part
        //    key). This is the grab operation that holds the physical tuner; releasing the tuner
        //    requires stopping THIS id. (Using the server id for the manifest makes the transcoder
        //    reject playback; using our id for the tuner stop leaves the tuner held — we learned both.)
        var playbackSessionId = Guid.NewGuid().ToString("N");

        // 1) Tune: grabs a physical tuner, assigns the airing a local ratingKey, and opens a live
        //    session whose id we recover from the Part key (/livetv/sessions/{sessionId}/...).
        var tuneUrl = $"{_serverUrl}/livetv/dvrs/{dvr.Key}/channels/{channelId}/tune"
                    + $"?X-Plex-Token={_token}&X-Plex-Client-Identifier={playbackSessionId}";
        string ratingKey;
        string tunerSessionId;
        using (var req = new HttpRequestMessage(HttpMethod.Post, tuneUrl))
        {
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            ratingKey = ExtractRatingKey(doc)
                ?? throw new InvalidOperationException("Plex tune returned no ratingKey for the live channel.");
            // Best-effort: if we can't recover the tuner id, playback still works; only the explicit
            // tuner stop degrades to Plex's idle-reap. Fall back to our id so stop at least targets
            // the transcode.
            tunerSessionId = ExtractLiveSessionId(doc) ?? playbackSessionId;
        }

        // 2) Build the universal HLS manifest URL for the tuned airing, keyed by OUR playback id.
        //    The host media engine fetches this and the child playlists/segments (which require the
        //    X-Plex-Client-Identifier header, surfaced via ResolvedStream.HttpHeaders by the caller).
        var manifest = BuildManifestUrl(ratingKey, playbackSessionId);

        var session = new PlexLiveSession
        {
            ChannelId = channelId,
            PlaybackSessionId = playbackSessionId,
            TunerSessionId = tunerSessionId,
            ManifestUrl = manifest,
        };

        lock (_sessionGate)
        {
            _active = session;
            _keepAlive?.Dispose();
            // Ping the transcode session periodically so Plex doesn't idle-reap it mid-view.
            _keepAlive = new System.Threading.Timer(_ => KeepAlivePing(playbackSessionId), null,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        Log(LogLevel.Info, "PlexLiveTV",
            $"Live session opened for channel {channelId} (playback {playbackSessionId}, tuner {tunerSessionId}).");
        return session;
    }

    /// <summary>The header the host must echo when fetching the manifest/segments for a live session.</summary>
    public static IReadOnlyDictionary<string, string> ManifestHeaders(PlexLiveSession session)
        => new Dictionary<string, string> { ["X-Plex-Client-Identifier"] = session.PlaybackSessionId };

    private string BuildManifestUrl(string ratingKey, string sessionId)
    {
        var path = Uri.EscapeDataString($"/library/metadata/{ratingKey}");
        var profile = Uri.EscapeDataString(
            "add-transcode-target(type=videoProfile&context=streaming&protocol=hls&container=mpegts&videoCodec=h264&audioCodec=aac)");
        return $"{_serverUrl}/video/:/transcode/universal/start.m3u8"
             + $"?path={path}&mediaIndex=0&partIndex=0&protocol=hls"
             + $"&directPlay=0&directStream=1&fastSeek=1&copyts=1"
             + $"&videoCodec=h264&audioCodec=aac&maxAudioChannels=2"
             + $"&context=streaming&session={sessionId}"
             + $"&X-Plex-Client-Identifier={sessionId}"
             + $"&X-Plex-Product=Phosphor&X-Plex-Platform=Chrome"
             + $"&X-Plex-Client-Profile-Extra={profile}"
             + $"&X-Plex-Token={_token}";
    }

    private void KeepAlivePing(string sessionId)
    {
        try
        {
            // A lightweight decision/ping keeps the transcode session marked active.
            var url = $"{_serverUrl}/video/:/transcode/universal/ping?session={sessionId}&X-Plex-Token={_token}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Plex-Client-Identifier", sessionId);
            _http.Send(req).Dispose();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "PlexLiveTV", $"keep-alive ping failed: {ex.Message}");
        }
    }

    /// <summary>Stops the currently-active live session (if any), releasing its tuner.</summary>
    public async Task StopActiveAsync()
    {
        PlexLiveSession? s;
        lock (_sessionGate)
        {
            s = _active;
            _active = null;
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
        if (s is null) return;
        // Stop the transcode (playback id) AND the tuner grab (server id). When they're the same
        // (tuner id couldn't be recovered), the second call is a harmless no-op/404.
        await StopSessionAsync(s.PlaybackSessionId).ConfigureAwait(false);
        if (!string.Equals(s.TunerSessionId, s.PlaybackSessionId, StringComparison.Ordinal))
            await StopSessionAsync(s.TunerSessionId).ConfigureAwait(false);
    }

    private async Task StopSessionAsync(string sessionId)
    {
        try
        {
            var url = $"{_serverUrl}/video/:/transcode/universal/stop?session={sessionId}&X-Plex-Token={_token}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Plex-Client-Identifier", sessionId);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            Log(LogLevel.Info, "PlexLiveTV", $"Stopped live session {sessionId} (HTTP {(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, "PlexLiveTV", $"stop session {sessionId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Defensive cleanup: stop any lingering transcode sessions on the server. Called on init and on
    /// settings change so a prior crash never leaves a tuner stranded. Best-effort.
    /// </summary>
    public async Task PanicCleanupAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        try
        {
            using var doc = await FetchJsonAsync($"{_serverUrl}/transcode/sessions?X-Plex-Token={_token}", ct);
            if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc) ||
                !mc.TryGetProperty("TranscodeSession", out var arr))
                return;
            foreach (var tsIt in arr.EnumerateArray())
            {
                var key = Str(tsIt, "key");
                // Live TV transcode sessions use context "static"; only clean those to avoid disturbing
                // an unrelated on-demand transcode the user might be running.
                if (string.IsNullOrEmpty(key) || Str(tsIt, "context") != "static") continue;
                await StopSessionAsync(key).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "PlexLiveTV", $"panic cleanup skipped: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? ExtractRatingKey(JsonDocument tune)
    {
        // The tune response nests the tuned airing's Metadata under a MediaGrabOperation; the live
        // session's local ratingKey is what the universal transcoder plays. Fall back to scanning for
        // any ratingKey if the shape shifts across Plex versions.
        if (tune.RootElement.TryGetProperty("MediaContainer", out var mc) &&
            mc.TryGetProperty("MediaSubscription", out var subs))
        {
            foreach (var sub in subs.EnumerateArray())
                if (sub.TryGetProperty("MediaGrabOperation", out var ops))
                    foreach (var op in ops.EnumerateArray())
                        if (op.TryGetProperty("Metadata", out var meta) &&
                            meta.TryGetProperty("ratingKey", out var rk))
                            return rk.ToString();
        }
        return FindRatingKey(tune.RootElement);
    }

    private static string? FindRatingKey(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.NameEquals("ratingKey"))
                        return p.Value.ToString();
                    var nested = FindRatingKey(p.Value);
                    if (nested is not null) return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var nested = FindRatingKey(item);
                    if (nested is not null) return nested;
                }
                break;
        }
        return null;
    }

    /// <summary>
    /// Recovers Plex's server-assigned live-session id from the tune response. This is the id that
    /// actually keys the tuner + transcode session (Plex ignores any client session param we send), so
    /// it MUST be used for the manifest, keep-alive, and stop — otherwise teardown no-ops and the
    /// tuner leaks. The id appears in the Part key as <c>/livetv/sessions/{sessionId}/...</c> and, in
    /// newer builds, as a Media <c>uuid</c>.
    /// </summary>
    private static string? ExtractLiveSessionId(JsonDocument tune)
    {
        // Any Part/session "key" of the form /livetv/sessions/{id}/... carries the session id.
        var id = TryParseSessionIdFromKey(FindLiveSessionKey(tune.RootElement));
        if (!string.IsNullOrEmpty(id)) return id;
        // Fallback: a Media "uuid" is the session id on newer servers.
        return FindStringProperty(tune.RootElement, "uuid");
    }

    private static string? FindLiveSessionKey(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String &&
                        p.Value.GetString() is { } sv &&
                        sv.Contains("/livetv/sessions/", StringComparison.Ordinal))
                        return sv;
                    var nested = FindLiveSessionKey(p.Value);
                    if (nested is not null) return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var nested = FindLiveSessionKey(item);
                    if (nested is not null) return nested;
                }
                break;
        }
        return null;
    }

    private static string? TryParseSessionIdFromKey(string? key)
    {
        // key looks like: /livetv/sessions/{sessionId}/{op}/index.m3u8?offset=...
        if (string.IsNullOrEmpty(key)) return null;
        const string marker = "/livetv/sessions/";
        var i = key.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = key[(i + marker.Length)..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : (rest.Length > 0 ? rest : null);
    }

    private static string? FindStringProperty(JsonElement el, string name)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.NameEquals(name) && p.Value.ValueKind == JsonValueKind.String)
                        return p.Value.GetString();
                    var nested = FindStringProperty(p.Value, name);
                    if (nested is not null) return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var nested = FindStringProperty(item, name);
                    if (nested is not null) return nested;
                }
                break;
        }
        return null;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static (int Major, int Minor) ParseVcn(string vcn)
    {
        var parts = (vcn ?? "").Split('.', 2);
        int.TryParse(parts[0], out var major);
        var minor = 0;
        if (parts.Length > 1) int.TryParse(parts[1], out minor);
        return (major, minor);
    }

    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { StopActiveAsync().GetAwaiter().GetResult(); } catch { }
        _keepAlive?.Dispose();
        _http.Dispose();
    }
}
