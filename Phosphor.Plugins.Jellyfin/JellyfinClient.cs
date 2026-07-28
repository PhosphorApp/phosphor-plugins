using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Phosphor.Plugins.Jellyfin;

/// <summary>
/// A lightweight Jellyfin item as returned by the REST API. Only the fields Phosphor needs.
/// </summary>
public sealed record JellyfinItem(
    string Id,
    string Name,
    string Type,
    bool IsFolder,
    TimeSpan? Duration,
    string? ImageTag,
    string? AlbumArtist,
    string? Album,
    string? CollectionType,
    string? AlbumId = null,
    string? AlbumImageTag = null);

/// <summary>One page of a browse/search query.</summary>
public sealed record JellyfinPage(IReadOnlyList<JellyfinItem> Items, int TotalCount);

/// <summary>A live TV channel from <c>/LiveTv/Channels</c>: a normal item plus its channel number and
/// the program airing right now (when guide data is present).</summary>
public sealed record JellyfinLiveChannel(
    string Id,
    string Name,
    string? ChannelNumber,
    string? ImageTag,
    string? CurrentProgram);

/// <summary>
/// An opened live-TV playback session: the ready-to-play (server-absolute) HLS URL plus the ids
/// needed to tear it down. <see cref="PlaySessionId"/> reports playback stopped (releases the tuner);
/// <see cref="LiveStreamId"/> is the opened media source. Owned by the source so it can close the
/// session on stop/skip/shutdown.
/// </summary>
public sealed record JellyfinLiveSession(
    string ChannelId,
    string StreamUrl,
    string PlaySessionId,
    string? LiveStreamId);

/// <summary>A single chapter marker on a Jellyfin item.</summary>
public sealed record JellyfinChapter(string Name, TimeSpan Start);

/// <summary>
/// Pure-<see cref="HttpClient"/> Jellyfin REST client: authenticates a user, browses the media
/// tree (views → artists/albums/tracks, movies, etc.), searches, and builds direct stream URLs.
/// No UI, no threading assumptions — mirrors the shape of the in-box Plex client.
/// </summary>
public sealed class JellyfinClient
{
    // Ticks per 100ns unit → seconds. Jellyfin reports RunTimeTicks in 100-nanosecond units.
    private const long TicksPerSecond = 10_000_000L;

    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    private string _serverUrl = "";
    private string _username = "";
    private string _password = "";
    private bool _stereoAudio;

    // Populated by AuthenticateAsync.
    private string? _accessToken;
    private string? _userId;

    private readonly string _deviceId;

    public JellyfinClient(HttpClient http, string deviceId, Action<string>? log = null)
    {
        _http = http;
        _deviceId = deviceId;
        _log = log;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_serverUrl) &&
        !string.IsNullOrWhiteSpace(_username);

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && !string.IsNullOrEmpty(_userId);

    public string ServerUrl => _serverUrl;

    /// <summary>Applies configuration. Clears cached auth ONLY when the credentials actually change,
    /// so repeated Configure calls with identical settings don't force a re-authentication.</summary>
    public void Configure(string serverUrl, string username, string password, bool stereoAudio)
    {
        var newServerUrl = (serverUrl ?? "").Trim().TrimEnd('/');
        var newUsername = username ?? "";
        var newPassword = password ?? "";

        // Only invalidate cached auth when the server/credentials changed. Configure() is called on
        // every EnsureClient() (i.e. once per ResolveAsync), so unconditionally wiping the token forced
        // a fresh authentication per item — e.g. 30 serial auth round-trips to open a 30-track album.
        var credsChanged =
            newServerUrl != _serverUrl || newUsername != _username || newPassword != _password;

        _serverUrl = newServerUrl;
        _username = newUsername;
        _password = newPassword;
        _stereoAudio = stereoAudio;

        if (credsChanged)
        {
            _accessToken = null;
            _userId = null;
        }
    }

    // ── Auth ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Jellyfin auth header. Must be exact or the server rejects the request. Version/device
    /// are cosmetic; DeviceId should be stable per install so the server tracks one session.
    /// </summary>
    private string AuthHeaderValue =>
        $"MediaBrowser Client=\"Phosphor\", Device=\"Phosphor\", DeviceId=\"{_deviceId}\", Version=\"1.0.0\"" +
        (string.IsNullOrEmpty(_accessToken) ? "" : $", Token=\"{_accessToken}\"");

    /// <summary>
    /// Authenticates with username/password via <c>POST /Users/AuthenticateByName</c>, caching the
    /// access token + user id. Idempotent: returns immediately if already authenticated.
    /// </summary>
    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated) return;
        if (!IsConfigured) throw new InvalidOperationException("Jellyfin is not configured.");

        var body = JsonSerializer.Serialize(new { Username = _username, Pw = _password });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverUrl}/Users/AuthenticateByName")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Emby-Authorization", AuthHeaderValue);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        _accessToken = root.TryGetProperty("AccessToken", out var t) ? t.GetString() : null;
        _userId = root.TryGetProperty("User", out var u) && u.TryGetProperty("Id", out var id)
            ? id.GetString()
            : null;

        if (!IsAuthenticated)
            throw new InvalidOperationException("Jellyfin authentication returned no token.");

        _log?.Invoke($"JellyfinClient: authenticated as '{_username}' (userId={_userId}).");
    }

    // ── Browse ──────────────────────────────────────────────────────────────────

    /// <summary>The user's top-level views/libraries (Music, Movies, …) via <c>GET /Users/{id}/Views</c>.</summary>
    public async Task<IReadOnlyList<JellyfinItem>> GetViewsAsync(CancellationToken ct = default)
    {
        await AuthenticateAsync(ct);
        using var doc = await GetJsonAsync($"{_serverUrl}/Users/{_userId}/Views", ct);
        return ParseItems(doc.RootElement);
    }

    /// <summary>
    /// Lists the children of a parent container (a view, artist, or album). Supports optional item-type
    /// filtering and paging. When <paramref name="includeItemTypes"/> is null the server returns the
    /// natural children of the parent.
    /// </summary>
    public async Task<JellyfinPage> GetItemsAsync(
        string parentId,
        string? includeItemTypes = null,
        int startIndex = 0,
        int limit = 0,
        CancellationToken ct = default)
    {
        await AuthenticateAsync(ct);

        var url = new StringBuilder($"{_serverUrl}/Users/{_userId}/Items?ParentId={Uri.EscapeDataString(parentId)}");
        url.Append("&SortBy=SortName&SortOrder=Ascending&Recursive=false");
        url.Append("&Fields=RunTimeTicks,AlbumArtist,Album");
        // Jellyfin omits ImageTags from list queries unless images are explicitly requested; without
        // this, folder items (albums/artists) can come back with no Primary tag and show no art.
        url.Append("&EnableImageTypes=Primary&ImageTypeLimit=1");
        if (!string.IsNullOrEmpty(includeItemTypes))
            url.Append($"&IncludeItemTypes={Uri.EscapeDataString(includeItemTypes)}");
        if (startIndex > 0) url.Append($"&StartIndex={startIndex}");
        if (limit > 0) url.Append($"&Limit={limit}");

        using var doc = await GetJsonAsync(url.ToString(), ct);
        return ParsePage(doc.RootElement);
    }

    /// <summary>
    /// Free-text search across the whole library for playable content (audio + video). Uses the
    /// recursive Items query with a <c>SearchTerm</c>.
    /// </summary>
    public async Task<IReadOnlyList<JellyfinItem>> SearchAsync(
        string query, int limit = 100, CancellationToken ct = default)
    {
        await AuthenticateAsync(ct);

        var url = $"{_serverUrl}/Users/{_userId}/Items"
            + $"?SearchTerm={Uri.EscapeDataString(query)}"
            + "&Recursive=true"
            + "&IncludeItemTypes=Audio,MusicVideo,Movie,Video,Episode"
            + "&Fields=RunTimeTicks,AlbumArtist,Album"
            + "&EnableImageTypes=Primary&ImageTypeLimit=1"
            + "&SortBy=SortName&SortOrder=Ascending"
            + $"&Limit={limit}";

        using var doc = await GetJsonAsync(url, ct);
        return ParseItems(doc.RootElement.TryGetProperty("Items", out _) ? doc.RootElement : doc.RootElement);
    }

    // ── Stream URLs ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a direct playback URL for an item.
    ///
    /// STEREO (2.1) is imperative for pinball cabs: their surround channels drive mechanical/ball
    /// exciters, so we must never emit &gt;2 channels. When <c>stereoAudio</c> is on we ask Jellyfin
    /// for a 2-channel downmix via <c>MaxStreamingBitrate</c> + <c>AudioChannels=2</c> on the
    /// universal audio endpoint / the video stream endpoint. Otherwise we direct-play.
    /// </summary>
    public string GetStreamUrl(string itemId, bool isAudioOnly)
    {
        var apiKey = _accessToken ?? "";

        if (isAudioOnly)
        {
            // Direct audio stream endpoint. LibVLC plays these reliably; the /universal endpoint's
            // HLS output frequently never reaches the player's "Playing" state.
            // - No downmix needed → static=true = direct-play/remux the original file untouched.
            // - Stereo (2.1 cab) needed → transcode to a plain 2-channel AAC container (NOT HLS).
            if (_stereoAudio)
            {
                return $"{_serverUrl}/Audio/{itemId}/stream"
                    + "?static=false"
                    + $"&UserId={_userId}"
                    + $"&DeviceId={Uri.EscapeDataString(_deviceId)}"
                    + "&Container=ts"
                    + "&AudioCodec=aac"
                    + "&MaxAudioChannels=2"
                    + $"&api_key={apiKey}";
            }

            return $"{_serverUrl}/Audio/{itemId}/stream"
                + "?static=true"
                + $"&DeviceId={Uri.EscapeDataString(_deviceId)}"
                + $"&api_key={apiKey}";
        }

        // Video: direct stream. static=true = remux/direct-play container without full transcode.
        // When stereo is required we drop static and constrain audio channels so the server
        // downmixes surround to 2.0.
        if (_stereoAudio)
        {
            return $"{_serverUrl}/Videos/{itemId}/stream"
                + $"?static=false"
                + $"&DeviceId={Uri.EscapeDataString(_deviceId)}"
                + "&VideoCodec=h264&AudioCodec=aac"
                + "&MaxAudioChannels=2"
                + "&TranscodingContainer=ts&TranscodingProtocol=hls"
                + $"&api_key={apiKey}";
        }

        return $"{_serverUrl}/Videos/{itemId}/stream"
            + $"?static=true"
            + $"&DeviceId={Uri.EscapeDataString(_deviceId)}"
            + $"&api_key={apiKey}";
    }

    /// <summary>Builds a primary-image URL for an item, or null when it has no image tag.</summary>
    public string? GetImageUrl(string itemId, string? imageTag)
    {
        if (string.IsNullOrEmpty(imageTag)) return null;
        return $"{_serverUrl}/Items/{itemId}/Images/Primary?tag={imageTag}&quality=90";
    }

    /// <summary>
    /// Best thumbnail for an item: its own Primary image when present, otherwise the album's Primary
    /// image (tracks without their own art inherit the album cover, matching the web UI).
    /// </summary>
    public string? GetBestImageUrl(JellyfinItem it)
    {
        if (!string.IsNullOrEmpty(it.ImageTag))
            return GetImageUrl(it.Id, it.ImageTag);
        if (!string.IsNullOrEmpty(it.AlbumId) && !string.IsNullOrEmpty(it.AlbumImageTag))
            return GetImageUrl(it.AlbumId!, it.AlbumImageTag);
        return null;
    }

    // ── Metadata ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches an item's chapter markers via <c>GET /Users/{userId}/Items/{id}?Fields=Chapters</c>.
    /// Jellyfin reports each chapter's <c>StartPositionTicks</c> (100ns units) and a name. Returns an
    /// empty list when the item has none.
    /// </summary>
    public async Task<IReadOnlyList<JellyfinChapter>> GetChaptersAsync(string itemId, CancellationToken ct = default)
    {
        await AuthenticateAsync(ct);
        using var doc = await GetJsonAsync(
            $"{_serverUrl}/Users/{_userId}/Items/{Uri.EscapeDataString(itemId)}?Fields=Chapters", ct);

        var list = new List<JellyfinChapter>();
        if (!doc.RootElement.TryGetProperty("Chapters", out var chapters) ||
            chapters.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var c in chapters.EnumerateArray())
        {
            var name = c.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";
            long ticks = c.TryGetProperty("StartPositionTicks", out var t) && t.TryGetInt64(out var v) ? v : 0;
            list.Add(new JellyfinChapter(name, TimeSpan.FromSeconds(ticks / (double)TicksPerSecond)));
        }
        return list;
    }

    // ── Live TV ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the server's Live TV channels via <c>GET /LiveTv/Channels</c>, requesting the current
    /// program so channel titles can show "what's on now" when guide data exists. Channel order is by
    /// the server's channel number.
    /// </summary>
    public async Task<IReadOnlyList<JellyfinLiveChannel>> GetLiveChannelsAsync(CancellationToken ct = default)
    {
        await AuthenticateAsync(ct);
        var url = $"{_serverUrl}/LiveTv/Channels?userId={_userId}"
                + "&Fields=CurrentProgram&EnableImageTypes=Primary&ImageTypeLimit=1&SortBy=SortName";
        using var doc = await GetJsonAsync(url, ct);

        var list = new List<JellyfinLiveChannel>();
        if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var c in items.EnumerateArray())
        {
            var id = Str(c, "Id");
            if (string.IsNullOrEmpty(id)) continue;
            string? imageTag = null;
            if (c.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
                tags.TryGetProperty("Primary", out var pt) && pt.ValueKind == JsonValueKind.String)
                imageTag = pt.GetString();

            string? current = null;
            if (c.TryGetProperty("CurrentProgram", out var cp) && cp.ValueKind == JsonValueKind.Object)
                current = Str(cp, "Name") is { Length: > 0 } n ? n : null;

            list.Add(new JellyfinLiveChannel(
                Id: id,
                Name: Str(c, "Name") is { Length: > 0 } nm ? nm : id,
                ChannelNumber: Str(c, "ChannelNumber") is { Length: > 0 } num ? num : null,
                ImageTag: imageTag,
                CurrentProgram: current));
        }

        return list.OrderBy(c => ParseChannelNumber(c.ChannelNumber)).ToList();
    }

    /// <summary>
    /// Opens a live playback session for a channel. Live TV can't direct-play here (UDP tuner feed), so
    /// this asks the server (<c>POST /Items/{id}/PlaybackInfo?AutoOpenLiveStream=true</c>) to open the
    /// stream and hand back a transcoded HLS URL. Returns the ready-to-play (server-absolute) URL plus
    /// the ids needed to tear the session down. Throws on failure (e.g. all tuners busy).
    /// </summary>
    public async Task<JellyfinLiveSession> OpenLiveStreamAsync(string channelId, CancellationToken ct = default)
    {
        await AuthenticateAsync(ct);

        // Empty DirectPlayProfiles + an HLS transcoding profile → the server returns a TranscodingUrl.
        var profile = new
        {
            UserId = _userId,
            AutoOpenLiveStream = true,
            DeviceProfile = new
            {
                MaxStreamingBitrate = 120_000_000,
                DirectPlayProfiles = Array.Empty<object>(),
                TranscodingProfiles = new[]
                {
                    new
                    {
                        Container = "ts",
                        Type = "Video",
                        VideoCodec = "h264",
                        AudioCodec = _stereoAudio ? "aac" : "aac,ac3,mp3",
                        Protocol = "hls",
                        Context = "Streaming",
                        MaxAudioChannels = _stereoAudio ? "2" : "6",
                    },
                },
            },
        };

        var body = JsonSerializer.Serialize(profile);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_serverUrl}/Items/{Uri.EscapeDataString(channelId)}/PlaybackInfo?userId={_userId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Emby-Authorization", AuthHeaderValue);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        var playSessionId = Str(root, "PlaySessionId");
        if (!root.TryGetProperty("MediaSources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Jellyfin PlaybackInfo returned no media sources for the channel.");

        // Prefer the source Jellyfin opened (it carries a LiveStreamId + TranscodingUrl).
        JsonElement chosen = default;
        var haveChosen = false;
        foreach (var s in sources.EnumerateArray())
        {
            if (Str(s, "TranscodingUrl") is { Length: > 0 })
            {
                chosen = s;
                haveChosen = true;
                break;
            }
        }
        if (!haveChosen)
            throw new InvalidOperationException("Jellyfin did not provide a playable Live TV stream (no TranscodingUrl).");

        var transcodingUrl = Str(chosen, "TranscodingUrl") ?? "";
        var liveStreamId = Str(chosen, "LiveStreamId");
        if (string.IsNullOrEmpty(transcodingUrl))
            throw new InvalidOperationException("Jellyfin did not provide a playable Live TV stream (no TranscodingUrl).");
        // TranscodingUrl is server-relative; make it absolute.
        var streamUrl = transcodingUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? transcodingUrl
            : _serverUrl + transcodingUrl;

        _log?.Invoke($"JellyfinClient: opened Live TV stream for channel {channelId} (playSession={playSessionId}).");
        return new JellyfinLiveSession(channelId, streamUrl,
            string.IsNullOrEmpty(playSessionId) ? "" : playSessionId,
            string.IsNullOrEmpty(liveStreamId) ? null : liveStreamId);
    }

    /// <summary>
    /// Closes a live playback session, releasing the transcode (and prompting the server to release
    /// the tuner). Reports playback stopped (<c>POST /Sessions/Playing/Stopped</c>) — the reliable
    /// teardown — and best-effort closes the live stream. Never throws.
    /// </summary>
    public async Task CloseLiveStreamAsync(JellyfinLiveSession session, CancellationToken ct = default)
    {
        try
        {
            await AuthenticateAsync(ct);
            var stopped = JsonSerializer.Serialize(new { PlaySessionId = session.PlaySessionId, ItemId = session.ChannelId });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverUrl}/Sessions/Playing/Stopped")
            {
                Content = new StringContent(stopped, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Emby-Authorization", AuthHeaderValue);
            using var resp = await _http.SendAsync(req, ct);
            _log?.Invoke($"JellyfinClient: reported Live TV stopped for {session.ChannelId} (HTTP {(int)resp.StatusCode}).");

            if (!string.IsNullOrEmpty(session.LiveStreamId))
            {
                var closeBody = JsonSerializer.Serialize(new { LiveStreamId = session.LiveStreamId });
                using var creq = new HttpRequestMessage(HttpMethod.Post, $"{_serverUrl}/LiveStreams/Close")
                {
                    Content = new StringContent(closeBody, Encoding.UTF8, "application/json"),
                };
                creq.Headers.TryAddWithoutValidation("X-Emby-Authorization", AuthHeaderValue);
                using var cresp = await _http.SendAsync(creq, ct);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"JellyfinClient: CloseLiveStream failed for {session.ChannelId}: {ex.Message}");
        }
    }

    private static (int Major, int Minor) ParseChannelNumber(string? number)
    {
        var parts = (number ?? "").Split('.', 2);
        int.TryParse(parts[0], out var major);
        var minor = 0;
        if (parts.Length > 1) int.TryParse(parts[1], out minor);
        return (major, minor);
    }

    // ── Connection test ─────────────────────────────────────────────────────────

    /// <summary>Authenticates and counts the user's views. Never throws for expected failures.</summary>
    public async Task<(bool ok, string message)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return (false, "Server URL and username are required.");
        try
        {
            var views = await GetViewsAsync(ct);
            return (true, $"Connected — {views.Count} librar{(views.Count == 1 ? "y" : "ies")}.");
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.StatusCode is { } sc ? $"{(int)sc} {sc}" : ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-Emby-Authorization", AuthHeaderValue);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await _http.SendAsync(req, ct);

        // A stale token yields 401 — re-auth once and retry.
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            _accessToken = null;
            _userId = null;
            await AuthenticateAsync(ct);

            using var retry = new HttpRequestMessage(HttpMethod.Get, url);
            retry.Headers.TryAddWithoutValidation("X-Emby-Authorization", AuthHeaderValue);
            retry.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            resp = await _http.SendAsync(retry, ct);
        }

        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        resp.Dispose();
        return doc;
    }

    private JellyfinPage ParsePage(JsonElement root)
    {
        var items = ParseItems(root);
        var total = root.TryGetProperty("TotalRecordCount", out var t) && t.TryGetInt32(out var n)
            ? n
            : items.Count;
        return new JellyfinPage(items, total);
    }

    /// <summary>Parses an <c>Items</c>-wrapped or bare array of Jellyfin items.</summary>
    private List<JellyfinItem> ParseItems(JsonElement root)
    {
        var list = new List<JellyfinItem>();

        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var itemsProp))
            arr = itemsProp;
        else if (root.ValueKind == JsonValueKind.Array)
            arr = root;
        else
            return list;

        foreach (var e in arr.EnumerateArray())
        {
            var mapped = MapItem(e);
            if (mapped != null) list.Add(mapped);
        }
        return list;
    }

    private static JellyfinItem? MapItem(JsonElement e)
    {
        var id = Str(e, "Id");
        if (string.IsNullOrEmpty(id)) return null;

        var name = Str(e, "Name") ?? "";
        var type = Str(e, "Type") ?? "";
        var isFolder = e.TryGetProperty("IsFolder", out var f) && f.ValueKind == JsonValueKind.True;

        TimeSpan? duration = null;
        if (e.TryGetProperty("RunTimeTicks", out var rt) && rt.TryGetInt64(out var ticks) && ticks > 0)
            duration = TimeSpan.FromSeconds(ticks / (double)TicksPerSecond);

        string? imageTag = null;
        if (e.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
            tags.TryGetProperty("Primary", out var primary))
            imageTag = primary.GetString();

        return new JellyfinItem(
            Id: id,
            Name: name,
            Type: type,
            IsFolder: isFolder,
            Duration: duration,
            ImageTag: imageTag,
            AlbumArtist: Str(e, "AlbumArtist"),
            Album: Str(e, "Album"),
            CollectionType: Str(e, "CollectionType"),
            // Tracks without their own Primary image inherit the album's art (the web UI does this),
            // so carry the album id + tag as a fallback for the thumbnail URL.
            AlbumId: Str(e, "AlbumId"),
            AlbumImageTag: Str(e, "AlbumPrimaryImageTag"));
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
