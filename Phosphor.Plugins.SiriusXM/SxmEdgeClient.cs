using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// EXPERIMENTAL SiriusXM edge-gateway client (<c>api.edge-gateway.siriusxm.com</c>), used ONLY for
/// now-playing. Unlike the cookie-based <see cref="SxmClient"/>, this authenticates with a headless
/// 4-step bearer-token (JWT) chain minted transparently from the stored username/password, then
/// posts <c>/playback/play/v1/liveUpdate</c> — the schedule feed the SiriusXM web player uses, which
/// publishes ahead of the broadcast so the selected cut matches the audio (the old cookie feed trails
/// by ~90s). Plain <see cref="HttpClient"/> only — no NSwag/generated clients. Flow mirrors
/// <c>yob15662/sxm-player</c> (<c>APISession</c> / <c>ClientExtensions</c> / <c>MetadataService</c>).
/// </summary>
/// <remarks>
/// Auth chain (each request carries <c>Authorization: Bearer</c> + <c>x-sxm-*</c> headers):
/// <list type="number">
/// <item><c>POST /device/v1/devices</c> → device grant (cache <c>device.json</c>).</item>
/// <item><c>POST /session/v1/sessions/anonymous</c> → anonymous access token.</item>
/// <item><c>POST /identity/v1/identities/authenticate/password</c> → identity grant (cache <c>tokens.json</c>).</item>
/// <item><c>POST /session/v1/sessions/authenticated</c> → user JWT access token (cache <c>access.json</c>).</item>
/// </list>
/// Tokens refresh ~10 min before expiry; a 401 clears caches and retries.
/// </remarks>
public sealed class SxmEdgeClient : IDisposable
{
    private const string BaseAddress = "https://api.edge-gateway.siriusxm.com";

    // Curated "all channels" container the web player uses to enumerate the lineup (from the
    // reference's GetChannelsAsync). Stable ids captured from the web-player HAR.
    private const string AllChannelsContainerId = "3JoBfOCIwo6FmTpzM1S2H7";
    private const string AllChannelsEntityId = "403ab6a5-d3c9-4c2a-a722-a94a6a5fd056";

    // Listener buffer behind the broadcast live edge, in ms. The now-playing selection anchors to
    // (UtcNow - this), i.e. the instant actually being heard, NOT the live edge. LibVLC buffers a few
    // ~10s HLS segments behind live, so the audio trails real time by ~25-30s; without this offset the
    // label leads the audio and the "next" track pops early (~27s early was measured on ch.18). The
    // lag is roughly constant for a given config but can vary by ~one segment depending on where in a
    // segment playback joined. Tune against the "SXM np:" diagnostics: aim for a small positive
    // audio-songStart and end-audio near the true remaining time.
    // Measured on ch.18: 27000 left the label ~3s early, so 30000 lands within the ~one-segment
    // (~10s) variance floor. A truly dynamic lag would need LibVLC's live-buffer depth, which isn't
    // observable here — see docs\SIRIUSXM_NOWPLAYING.md ("Dynamic lag") for why a constant is used.
    private const long LiveAudioLagMs = 30000;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _username;
    private readonly string _password;
    private readonly string _region;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    // Plain client for pre-signed CDN playlist/segment fetches — NO bearer/x-sxm headers, no base
    // address. tuneSource returns pre-signed akamai URLs that must be fetched verbatim.
    private readonly HttpClient _cdn;

    private readonly string _deviceFile;
    private readonly string _tokensFile;
    private readonly string _accessFile;

    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly object _gate = new();
    // Monotonic request index for the x-sxm-clock header ([0,<idx>]).
    private int _requestIndex;

    // A stable per-session GUID; the base64 of its string form is the container "key" param.
    private readonly Guid _guid = Guid.NewGuid();

    // Cached edge channelId lookups (channelNumber → edge id, name → edge id), from all-channels.
    private Dictionary<string, string>? _edgeIdByNumber;
    private Dictionary<string, string>? _edgeIdByName;

    private DeviceGrant? _device;
    private AnonTokens? _tokens;
    private UserAccessToken? _access;

    public SxmEdgeClient(string username, string password, string region, string cacheDir, Action<string>? log = null)
    {
        _username = username;
        _password = password;
        _region = string.IsNullOrWhiteSpace(region) ? "US" : region;
        _log = log;
        _http = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(60) };
        _cdn = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        _deviceFile = Path.Combine(cacheDir, "device.json");
        _tokensFile = Path.Combine(cacheDir, "tokens.json");
        _accessFile = Path.Combine(cacheDir, "access.json");
    }

    /// <summary>True once a (non-expired) user access token is held.</summary>
    public bool IsAuthenticated
    {
        get { lock (_gate) return _access != null; }
    }

    // ── Public: authenticate + now-playing ──────────────────────────────────────

    /// <summary>Ensures a valid bearer session (minting/refreshing as needed). Returns true on success.</summary>
    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        try { return await LoginIfNecessaryAsync(false, 0, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log?.Invoke($"SXM login failed: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Resolves the currently-airing track for a channel from the edge <c>liveUpdate</c> feed. Selects
    /// the SONG cut (skipping interstitials) whose <c>[timestamp, timestamp+duration)</c> window
    /// contains the listener's audio instant. Falls back to the current episode/show title for talk.
    /// Returns null when nothing usable is available.
    /// </summary>
    public async Task<SxmNowPlaying?> GetNowPlayingAsync(SxmChannel channel, TimeSpan? playbackPosition = null, CancellationToken ct = default)
    {
        if (!await LoginIfNecessaryAsync(false, 0, ct)) return null;

        var channelId = await ResolveEdgeChannelIdAsync(channel, ct);
        if (channelId == null) { _log?.Invoke($"SXM: no edge channelId for '{channel.Id}' (#{channel.Number})."); return null; }

        var now = DateTimeOffset.UtcNow;
        // Window: a few hours back (schedule) to a minute ahead, mirroring the reference.
        var body = new Dictionary<string, string>
        {
            ["channelId"] = channelId,
            ["startTimestamp"] = Iso(now.AddHours(-3).AddMinutes(-10)),
            ["endTimestamp"] = Iso(now.AddMinutes(1)),
        };
        var resp = await SendJsonAsync(HttpMethod.Post, "/playback/play/v1/liveUpdate", body, ct);
        if (resp is not { } r) return null;
        return ExtractNowPlaying(r, playbackPosition, _log);
    }

    // ── Public: live stream resolution (tuneSource + key + pre-signed CDN) ───────

    /// <summary>The resolved live stream: the pre-signed CDN master-playlist URL for a channel.</summary>
    /// <param name="MasterUrl">Pre-signed akamai master playlist (fetch verbatim, no auth).</param>
    public sealed record EdgeStream(string MasterUrl);

    /// <summary>
    /// Resolves a channel's live HLS master playlist via <c>POST /playback/play/v1/tuneSource</c>
    /// (bearer auth). Returns the primary (<c>isPrimary</c>) stream URL — a PRE-SIGNED CDN URL that
    /// must be fetched verbatim (no bearer, no cookie params). Returns null on any failure; the caller
    /// force-fails rather than falling back to the legacy path.
    /// </summary>
    public async Task<EdgeStream?> TuneSourceAsync(SxmChannel channel, CancellationToken ct = default)
    {
        if (!await LoginIfNecessaryAsync(false, 0, ct)) return null;

        var channelId = await ResolveEdgeChannelIdAsync(channel, ct);
        if (channelId == null) { _log?.Invoke($"SXM: tuneSource — no UUID channelId for '{channel.Name}' (#{channel.Number})."); return null; }

        // Live radio is a linear channel; the web player requests the WEB manifest variant for it.
        var body = new Dictionary<string, string>
        {
            ["id"] = channelId,
            ["type"] = "channel-linear",
            ["hlsVersion"] = "V3",
            ["manifestVariant"] = "WEB",
            ["mtcVersion"] = "V2",
        };
        var resp = await SendJsonAsync(HttpMethod.Post, "/playback/play/v1/tuneSource", body, ct);
        if (resp is not { } r) return null;

        try
        {
            if (!r.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
            {
                _log?.Invoke("SXM: tuneSource returned no streams[].");
                return null;
            }

            string? primary = null, firstUrl = null;
            foreach (var s in streams.EnumerateArray())
            {
                if (!s.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array) continue;
                foreach (var u in urls.EnumerateArray())
                {
                    var url = Str(u, "url");
                    if (string.IsNullOrEmpty(url)) continue;
                    firstUrl ??= url;
                    if (u.TryGetProperty("isPrimary", out var ip) && ip.ValueKind == JsonValueKind.True)
                    {
                        primary = url;
                        break;
                    }
                }
                if (primary != null) break;
            }

            var master = primary ?? firstUrl;
            if (master == null) { _log?.Invoke("SXM: tuneSource had streams but no usable url."); return null; }
            _log?.Invoke($"SXM: tuneSource master = {master}");
            return new EdgeStream(master);
        }
        catch (Exception ex) { _log?.Invoke($"SXM: tuneSource parse failed: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Fetches the AES-128 content key for an HLS <c>EXT-X-KEY</c> GUID via
    /// <c>GET /playback/key/v1/{guid}</c> (bearer auth). Returns the raw 16-byte key, or null on failure.
    /// </summary>
    public async Task<byte[]?> GetKeyAsync(string keyGuid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyGuid)) return null;
        if (!await LoginIfNecessaryAsync(false, 0, ct)) return null;

        var resp = await SendJsonAsync(HttpMethod.Get, $"/playback/key/v1/{Uri.EscapeDataString(keyGuid)}", null, ct);
        if (resp is not { } r) return null;
        try
        {
            var b64 = Str(r, "key");
            if (string.IsNullOrEmpty(b64)) { _log?.Invoke($"SXM: key response had no 'key' for {keyGuid}."); return null; }
            return Convert.FromBase64String(b64);
        }
        catch (Exception ex) { _log?.Invoke($"SXM: key decode failed for {keyGuid}: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Fetches a PRE-SIGNED CDN resource (playlist or segment) verbatim — no bearer, no cookie params.
    /// tuneSource URLs are akamai-signed, so any injected auth would break the signature.
    /// </summary>
    public async Task<HttpResponseMessage?> GetCdnAsync(string url, CancellationToken ct = default)
    {
        try { return await _cdn.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log?.Invoke($"SXM: CDN GET failed: {ex.Message}"); return null; }
    }


    // ── liveUpdate parsing (SONG-cut selection + diagnostics) ───────────────────

    private static SxmNowPlaying? ExtractNowPlaying(JsonElement root, TimeSpan? playbackPosition, Action<string>? log)
    {
        try
        {
            // The instant the listener is actually hearing. The edge feed runs AHEAD of broadcast, so
            // anchoring at (now - a small buffer) lands on the airing cut. Tune LiveAudioLagMs from the
            // diagnostics if the label leads/trails the audio.
            long audioInstant = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LiveAudioLagMs;
            _ = playbackPosition; // retained in the API for sources that can use a true session origin.

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                log?.Invoke("SXM np: liveUpdate returned no items[] — falling back to episode/show title.");
                return EpisodeFallback(root, audioInstant, log, playbackPosition);
            }

            JsonElement? bestSong = null;
            long bestStart = long.MinValue, bestEnd = 0;
            int songCount = 0;
            foreach (var it in items.EnumerateArray())
            {
                // Skip interstitials (station-ID / DJ chatter) — songs only.
                if (it.TryGetProperty("isInterstitial", out var isi) &&
                    isi.ValueKind == JsonValueKind.True) continue;
                songCount++;

                if (!it.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String) continue;
                if (!DateTimeOffset.TryParse(tsEl.GetString(), out var tsDto)) continue;
                long start = tsDto.ToUnixTimeMilliseconds();
                long dur = it.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? (long)d.GetDouble() : 0;
                long end = dur > 0 ? start + dur : start;

                // The cut whose window contains the audio instant: latest start at/before it.
                if (start <= audioInstant && start > bestStart)
                {
                    bestStart = start;
                    bestEnd = end;
                    bestSong = it;
                }
            }

            if (songCount == 0)
                log?.Invoke($"SXM np: liveUpdate returned {items.GetArrayLength()} items but 0 SONG cuts (all interstitial) — using episode/show title.");

            if (bestSong is { } song)
            {
                var title = Str(song, "name");
                var artist = Str(song, "artistName");
                var album = Str(song, "albumName");

                // NextChangeUtc is a wall-clock on the AUDIO timeline: bestEnd is a broadcast-schedule
                // timestamp, but the listener hears that boundary LiveAudioLagMs later, so shift it
                // forward so the host's next poll (and the label flip) fires when the change is heard.
                DateTimeOffset? next = bestEnd > audioInstant
                    ? DateTimeOffset.FromUnixTimeMilliseconds(bestEnd + LiveAudioLagMs) : null;

                if (log != null)
                {
                    string F(long ms) => ms <= 0 ? "-" : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("HH:mm:ss");
                    double songToAudio = (audioInstant - bestStart) / 1000.0;
                    double audioToEnd = (bestEnd - audioInstant) / 1000.0;
                    log($"SXM np: pos={playbackPosition?.TotalSeconds:F0}s audioInstant={F(audioInstant)} " +
                        $"songStart={F(bestStart)} (audio-songStart={songToAudio:F0}s) songEnd={F(bestEnd)} " +
                        $"(end-audio={audioToEnd:F0}s) => '{artist} - {title}'");
                }

                if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist))
                    return new SxmNowPlaying(title, artist, album, next);
            }

            return EpisodeFallback(root, audioInstant, log, playbackPosition);
        }
        catch { return null; }
    }

    // Talk fallback: the current episode/show title from live.episodes[] at/before the audio instant.
    private static SxmNowPlaying? EpisodeFallback(JsonElement root, long audioInstant, Action<string>? log, TimeSpan? playbackPosition)
    {
        try
        {
            if (!root.TryGetProperty("live", out var live) ||
                !live.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array)
                return null;

            string? best = null;
            long bestStart = long.MinValue;
            foreach (var ep in eps.EnumerateArray())
            {
                if (!ep.TryGetProperty("startTimestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String) continue;
                if (!DateTimeOffset.TryParse(tsEl.GetString(), out var tsDto)) continue;
                long start = tsDto.ToUnixTimeMilliseconds();
                if (start > audioInstant || start <= bestStart) continue;
                var name = Str(ep, "showName") ?? Str(ep, "name");
                if (!string.IsNullOrWhiteSpace(name)) { best = name; bestStart = start; }
            }

            if (best != null && log != null)
                log($"SXM np: pos={playbackPosition?.TotalSeconds:F0}s (talk) => '{best}'");

            return best is null ? null : new SxmNowPlaying(best, null, null, null);
        }
        catch { return null; }
    }

    // ── Edge channelId resolution (map cookie lineup → edge ids) ─────────────────

    /// <summary>
    /// Maps a cookie-lineup <see cref="SxmChannel"/> to the edge-gateway channelId. The gateway's
    /// <c>liveUpdate</c> requires a UUID channelId (not the slug), and the cookie lineup already
    /// carries exactly that as <see cref="SxmChannel.Guid"/> (the <c>channelGuid</c>) — so prefer it.
    /// Only when the GUID is missing do we consult the edge all-channels container (matched by channel
    /// number, then name) as a fallback.
    /// </summary>
    private async Task<string?> ResolveEdgeChannelIdAsync(SxmChannel channel, CancellationToken ct)
    {
        // Preferred: the channel GUID from the cookie lineup is already the UUID the gateway wants.
        if (LooksLikeUuid(channel.Guid))
        {
            _log?.Invoke($"SXM np: channelId from lineup GUID (#{channel.Number} '{channel.Name}') => {channel.Guid}");
            return channel.Guid;
        }

        Dictionary<string, string>? byNumber, byName;
        lock (_gate) { byNumber = _edgeIdByNumber; byName = _edgeIdByName; }
        if (byNumber == null)
        {
            (byNumber, byName) = await FetchEdgeChannelMapAsync(ct);
            lock (_gate) { _edgeIdByNumber = byNumber; _edgeIdByName = byName; }
        }

        if (!string.IsNullOrEmpty(channel.Number) && byNumber.TryGetValue(channel.Number, out var idByNum))
        {
            _log?.Invoke($"SXM np: channelId resolved by number (#{channel.Number}) => {idByNum}");
            return idByNum;
        }
        if (!string.IsNullOrEmpty(channel.Name) && byName!.TryGetValue(channel.Name, out var idByName))
        {
            _log?.Invoke($"SXM np: channelId resolved by name ('{channel.Name}') => {idByName}");
            return idByName;
        }
        // No UUID available — the gateway rejects the slug, so don't call liveUpdate with it.
        _log?.Invoke($"SXM np: no UUID channelId for '{channel.Name}' (#{channel.Number}); skipping liveUpdate.");
        return null;
    }

    private static bool LooksLikeUuid(string? s) => Guid.TryParse(s, out _);

    // Fetches the raw all-channels container JSON (bearer). Shared by the now-playing channelId map
    // and the full lineup. offset/size support paging.
    private async Task<JsonElement?> FetchAllChannelsRawAsync(int offset, int size, CancellationToken ct)
    {
        if (!await LoginIfNecessaryAsync(false, 0, ct)) return null;
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(_guid.ToString()));
        var q = new Dictionary<string, string>
        {
            ["containerId"] = AllChannelsContainerId,
            ["useCuratedContext"] = "false",
            ["entityType"] = "curated-grouping",
            ["entityId"] = AllChannelsEntityId,
            ["offset"] = offset.ToString(),
            ["size"] = size.ToString(),
            ["setStyle"] = "small_list",
            ["key"] = key,
        };
        var path = "/relationship/v1/container/all-channels?" + string.Join("&",
            q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return await SendJsonAsync(HttpMethod.Get, path, null, ct);
    }

    // SiriusXM image server: relative image paths (e.g. "if/03/..png") are served by a resize proxy
    // that takes a base64-encoded JSON param. Mirrors yob15662/sxm-player's image URL builder.
    private const string ImageServerBase = "https://imgsrv-sxm-prod-device.streaming.siriusxm.com/";

    private static string? BuildImageUrl(string? relativeKey)
    {
        if (string.IsNullOrEmpty(relativeKey)) return null;
        var json = $"{{\"key\":\"{relativeKey}\",\"edits\":[{{\"format\":{{\"type\":\"jpeg\"}}}},{{\"resize\":{{\"width\":600,\"height\":600}}}}]}}";
        return ImageServerBase + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Fetches the account's channel lineup from the edge-gateway <c>all-channels</c> container
    /// (bearer auth) and maps it to <see cref="SxmChannel"/>. The channel UUID (<c>entity.id</c>)
    /// serves as both <c>Id</c> and <c>Guid</c>; name is <c>entity.texts.title.default</c>; number is
    /// <c>decorations.channelNumber</c>; <c>decorations.genre</c> becomes the (single) category; the
    /// tile/logo image is resolved through the SXM image server. Returns an empty list on failure.
    /// </summary>
    public async Task<IReadOnlyList<SxmChannel>> GetChannelsAsync(CancellationToken ct = default)
    {
        var list = new List<SxmChannel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var root = await FetchAllChannelsRawAsync(0, 1000, ct);
        if (root is not { } r) return list;
        try
        {
            if (!r.TryGetProperty("container", out var container) ||
                !container.TryGetProperty("sets", out var sets) || sets.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var set in sets.EnumerateArray())
            {
                if (!set.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("entity", out var entity)) continue;
                    var id = Str(entity, "id");
                    if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;

                    // Name: entity.texts.title.default
                    string? name = null;
                    if (entity.TryGetProperty("texts", out var texts) &&
                        texts.TryGetProperty("title", out var title))
                        name = Str(title, "default") ?? Str(title, "medium") ?? Str(title, "long");
                    name ??= id;

                    // Number + genre: decorations.channelNumber / decorations.genre
                    string number = "";
                    var cats = new List<SxmCategoryRef>();
                    if (item.TryGetProperty("decorations", out var deco))
                    {
                        number = NumStr(deco, "channelNumber") ?? NumStr(deco, "channelNumberCanonical") ?? "";
                        var genre = Str(deco, "genre");
                        if (!string.IsNullOrWhiteSpace(genre))
                            cats.Add(new SxmCategoryRef(genre!.ToLowerInvariant().Replace(" & ", "").Replace(" ", ""), genre!));
                    }

                    var thumb = BuildImageUrl(ExtractImageKey(entity));

                    // The gateway UUID is both the tune id and the now-playing GUID.
                    list.Add(new SxmChannel(id, name!, number, id, thumb, cats));
                }
            }
        }
        catch (Exception ex) { _log?.Invoke($"SXM: lineup parse failed: {ex.Message}"); }
        _log?.Invoke($"SXM: edge lineup — {list.Count} channels.");
        return list;
    }

    // Prefer a square tile/logo image key from entity.images (tile 1x1 → logo 1x1 → any first url).
    private static string? ExtractImageKey(JsonElement entity)
    {
        if (!entity.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var group in new[] { "tile", "logo" })
        {
            if (images.TryGetProperty(group, out var g) &&
                g.TryGetProperty("aspect_1x1", out var a) &&
                a.TryGetProperty("default", out var d))
            {
                var url = Str(d, "url");
                if (!string.IsNullOrEmpty(url)) return url;
            }
        }
        // Fallback: first url found anywhere under images.
        foreach (var group in images.EnumerateObject())
            foreach (var aspect in group.Value.EnumerateObject())
                if (aspect.Value.ValueKind == JsonValueKind.Object &&
                    aspect.Value.TryGetProperty("default", out var d2))
                {
                    var url = Str(d2, "url");
                    if (!string.IsNullOrEmpty(url)) return url;
                }
        return null;
    }

    private async Task<(Dictionary<string, string> byNumber, Dictionary<string, string> byName)> FetchEdgeChannelMapAsync(CancellationToken ct)
    {
        var byNumber = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(_guid.ToString()));
            var q = new Dictionary<string, string>
            {
                ["containerId"] = AllChannelsContainerId,
                ["useCuratedContext"] = "false",
                ["entityType"] = "curated-grouping",
                ["entityId"] = AllChannelsEntityId,
                ["offset"] = "0",
                ["size"] = "1000",
                ["setStyle"] = "small_list",
                ["key"] = key,
            };
            var path = "/relationship/v1/container/all-channels?" + string.Join("&",
                q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            var resp = await SendJsonAsync(HttpMethod.Get, path, null, ct);
            if (resp is not { } r) return (byNumber, byName);

            // container.sets[].items[]: the UUID is entity.id; the channel number lives under
            // decorations.channelNumber; the name under decorations.contentTypeLabel/entity.texts.
            if (r.TryGetProperty("container", out var container) &&
                container.TryGetProperty("sets", out var sets) && sets.ValueKind == JsonValueKind.Array)
            {
                foreach (var set in sets.EnumerateArray())
                {
                    if (!set.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in itemsEl.EnumerateArray())
                    {
                        var id = item.TryGetProperty("entity", out var entity) ? Str(entity, "id") : null;
                        if (string.IsNullOrEmpty(id)) continue;

                        string? num = null, name = null;
                        if (item.TryGetProperty("decorations", out var deco))
                        {
                            num = NumStr(deco, "channelNumber");
                            name = Str(deco, "contentTypeLabel");
                        }
                        // Name best-effort from entity.texts.title.default when present.
                        if (name == null && entity.TryGetProperty("texts", out var texts) &&
                            texts.TryGetProperty("title", out var titleObj))
                            name = Str(titleObj, "default");

                        if (!string.IsNullOrEmpty(num)) byNumber[num!] = id;
                        if (!string.IsNullOrEmpty(name)) byName[name!] = id;
                    }
                }
            }
            _log?.Invoke($"SXM: edge channel map — {byNumber.Count} by number, {byName.Count} by name.");
        }
        catch (Exception ex) { _log?.Invoke($"SXM: edge channel map fetch failed: {ex.Message}"); }
        return (byNumber, byName);
    }

    // ── Auth chain ──────────────────────────────────────────────────────────────

    private async Task<bool> LoginIfNecessaryAsync(bool clearDevice, int retryCount, CancellationToken ct)
    {
        await _loginGate.WaitAsync(ct);
        try { return await LoginInternalAsync(clearDevice, retryCount, ct); }
        finally { _loginGate.Release(); }
    }

    private async Task<bool> LoginInternalAsync(bool clearDevice, int retryCount, CancellationToken ct)
    {
        if (retryCount >= 5) { _log?.Invoke("SXM: too many login retries."); return false; }
        try
        {
            LoadCachesIfNeeded();
            DropExpiredTokens();

            // 1) Device grant (unauthenticated).
            if (_device == null)
            {
                _device = await MintDeviceAsync(ct);
                if (_device == null) { _log?.Invoke("SXM: device grant failed."); return false; }
                WriteJson(_deviceFile, _device);
            }

            // Short-circuit: a still-valid user access token is all we need.
            if (_access != null) return true;

            // 2) + 3) Anonymous token, then password identity grant.
            if (_tokens == null)
            {
                var anon = await MintAnonymousAsync(ct);
                if (anon == null) { _log?.Invoke("SXM: anonymous session failed."); return false; }

                var grant = await MintIdentityGrantAsync(ct, anon.AccessToken);
                if (grant == null) { _log?.Invoke("SXM: password identity grant failed."); return false; }

                _tokens = anon with { IdentityGrant = grant };
                WriteJson(_tokensFile, _tokens);
            }

            // 4) Authenticated user access token.
            var access = await MintAuthenticatedAsync(ct);
            if (access == null) { _log?.Invoke("SXM: authenticated session failed."); return false; }
            _access = access;
            WriteJson(_accessFile, _access);

            _log?.Invoke("SXM: logged in (edge-gateway).");
            return true;
        }
        catch (SxmEdgeStatusException ex) when (ex.StatusCode == 401)
        {
            ClearCaches(clearDevice);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return await LoginInternalAsync(retryCount > 0, retryCount + 1, ct);
        }
        catch (SxmEdgeStatusException ex) when (ex.StatusCode == 500)
        {
            ClearCaches(clearDevice);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return await LoginInternalAsync(retryCount > 0, retryCount + 1, ct);
        }
    }

    private async Task<DeviceGrant?> MintDeviceAsync(CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            // Values mirror yob15662/sxm-player's DevicesAsync body exactly — the gateway 400s on
            // unexpected devicePlatform/app values (e.g. "browser"/"sxm").
            ["devicePlatform"] = "web-desktop",
            ["deviceAttributes"] = new Dictionary<string, object?>
            {
                ["browser"] = new Dictionary<string, object?>
                {
                    ["browser"] = "Edge",
                    ["browserVersion"] = "121.0.0.0",
                    ["userAgent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
                    ["sdk"] = "web",
                    ["app"] = "web",
                    ["sdkVersion"] = "121.0.0.0",
                    ["appVersion"] = "121.0.0.0",
                },
            },
        };
        var r = await SendJsonAsync(HttpMethod.Post, "/device/v1/devices", body, ct);
        if (r is not { } el) return null;
        var id = Str(el, "deviceId");
        var grant = Str(el, "grant");
        return grant == null ? null : new DeviceGrant(id ?? "", grant);
    }

    private async Task<AnonTokens?> MintAnonymousAsync(CancellationToken ct)
    {
        var r = await SendJsonAsync(HttpMethod.Post, "/session/v1/sessions/anonymous", "", ct);
        if (r is not { } el) return null;
        var token = Str(el, "accessToken");
        if (token == null) return null;
        var exp = ParseExpiry(el, "accessTokenExpiresAt");
        return new AnonTokens(token, exp, null);
    }

    private async Task<string?> MintIdentityGrantAsync(CancellationToken ct, string anonToken)
    {
        // This request must carry the anon bearer; set it explicitly since _tokens isn't stored yet.
        var body = new Dictionary<string, object?> { ["handle"] = _username, ["password"] = _password };
        var r = await SendJsonAsync(HttpMethod.Post, "/identity/v1/identities/authenticate/password", body, ct, overrideBearer: anonToken);
        if (r is not { } el) return null;
        return Str(el, "grant");
    }

    private async Task<UserAccessToken?> MintAuthenticatedAsync(CancellationToken ct)
    {
        var r = await SendJsonAsync(HttpMethod.Post, "/session/v1/sessions/authenticated", "", ct);
        if (r is not { } el) return null;
        var token = Str(el, "accessToken");
        if (token == null) return null;
        return new UserAccessToken(
            token,
            Str(el, "accessTokenId") ?? "",
            ParseExpiry(el, "accessTokenExpiresAt"),
            ParseExpiry(el, "refreshTokenExpiresAt"),
            Str(el, "sessionType") ?? "");
    }

    // Drop tokens ~10 min before expiry so a fresh one is minted proactively.
    private void DropExpiredTokens()
    {
        var soon = DateTimeOffset.UtcNow.AddMinutes(10);
        if (_access != null && _access.AccessTokenExpiresAt <= soon) _access = null;
        if (_tokens != null && _tokens.AnonExpiry <= soon) { _tokens = null; _access = null; }
    }

    // ── Request plumbing (header injection + bearer precedence) ─────────────────

    private async Task<JsonElement?> SendJsonAsync(
        HttpMethod method, string pathOrUrl, object? jsonBody, CancellationToken ct, string? overrideBearer = null)
    {
        using var req = new HttpRequestMessage(method, pathOrUrl);

        // x-sxm-* headers on every request.
        int idx;
        lock (_gate) idx = _requestIndex++;
        req.Headers.TryAddWithoutValidation("x-sxm-clock", $"[0,{idx}]");
        req.Headers.TryAddWithoutValidation("x-sxm-platform", "browser");
        req.Headers.TryAddWithoutValidation("x-sxm-tenant", "sxm");
        req.Headers.Accept.ParseAdd("application/json");

        // Authorization: Bearer with precedence access → identity grant → anon → device.
        var bearer = overrideBearer ?? CurrentBearer();
        if (bearer != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        if (jsonBody != null || method == HttpMethod.Post)
        {
            var json = jsonBody is string s ? s : JsonSerializer.Serialize(jsonBody, JsonOpts);
            req.Content = new StringContent(json, Encoding.UTF8);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)resp.StatusCode;
        if (status is 401 or 500)
            throw new SxmEdgeStatusException(status, $"{method} {pathOrUrl} -> {status}");
        if (!resp.IsSuccessStatusCode)
        {
            // Include a snippet of the error body — gateway 4xx responses carry a JSON message that
            // pinpoints the bad field, which is essential for diagnosing device/session rejections.
            string detail = "";
            try
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(err)) detail = " " + (err.Length > 300 ? err[..300] : err);
            }
            catch { /* best-effort */ }
            _log?.Invoke($"SXM: {method} {pathOrUrl} -> {status}.{detail}");
            return null;
        }

        var text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private string? CurrentBearer()
    {
        lock (_gate)
        {
            if (_access != null) return _access.AccessToken;
            if (_tokens?.IdentityGrant != null) return _tokens.IdentityGrant;
            if (_tokens?.AnonAccessToken != null) return _tokens.AnonAccessToken;
            if (_device != null) return _device.Grant;
            return null;
        }
    }

    // ── Token caches (JSON under the instance dir) ──────────────────────────────

    private bool _cachesLoaded;

    private void LoadCachesIfNeeded()
    {
        if (_cachesLoaded) return;
        _cachesLoaded = true;
        _device ??= ReadJson<DeviceGrant>(_deviceFile);
        _tokens ??= ReadJson<AnonTokens>(_tokensFile);
        _access ??= ReadJson<UserAccessToken>(_accessFile);
    }

    private void ClearCaches(bool clearDevice)
    {
        lock (_gate)
        {
            _access = null;
            TryDelete(_accessFile);
            if (clearDevice)
            {
                _device = null;
                _tokens = null;
                TryDelete(_deviceFile);
                TryDelete(_tokensFile);
            }
        }
    }

    private T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex) { _log?.Invoke($"SXM: cache read '{Path.GetFileName(path)}' failed: {ex.Message}"); return null; }
    }

    private void WriteJson(string path, object value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
        }
        catch (Exception ex) { _log?.Invoke($"SXM: cache write '{Path.GetFileName(path)}' failed: {ex.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    // ── Small helpers ────────────────────────────────────────────────────────────

    private static string Iso(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static DateTimeOffset ParseExpiry(JsonElement el, string prop) =>
        Str(el, prop) is { } s && DateTimeOffset.TryParse(s, out var dto)
            ? dto : DateTimeOffset.UtcNow.AddMinutes(30);

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NumStr(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n.ToString() : v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { /* best-effort */ }
        try { _cdn.Dispose(); } catch { /* best-effort */ }
        try { _loginGate.Dispose(); } catch { /* best-effort */ }
    }

    // ── Cached token records ─────────────────────────────────────────────────────

    private sealed record DeviceGrant(string DeviceId, string Grant);

    private sealed record AnonTokens(
        string AnonAccessToken,
        DateTimeOffset AnonExpiry,
        string? IdentityGrant)
    {
        [JsonIgnore] public string AccessToken => AnonAccessToken;
    }

    private sealed record UserAccessToken(
        string AccessToken,
        string AccessTokenId,
        DateTimeOffset AccessTokenExpiresAt,
        DateTimeOffset RefreshTokenExpiresAt,
        string SessionType);

    private sealed class SxmEdgeStatusException : Exception
    {
        public int StatusCode { get; }
        public SxmEdgeStatusException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    }
}
