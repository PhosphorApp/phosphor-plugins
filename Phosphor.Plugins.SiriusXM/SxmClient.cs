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
                    ExtractThumb(ch)));
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

    /// <summary>Query string of auth params required on every akamai playlist/segment request.</summary>
    public string TokenParams =>
        $"token={Uri.EscapeDataString(SxmToken() ?? "")}&consumer=k2&gupId={Uri.EscapeDataString(GupId() ?? "")}";

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
}

/// <summary>A SiriusXM channel from the lineup.</summary>
/// <param name="Id">The channelId slug (e.g. "octane"), used for tuning.</param>
/// <param name="Name">Display name (e.g. "Octane").</param>
/// <param name="Number">Channel number as a string (e.g. "37").</param>
/// <param name="Guid">Channel GUID, required by now-playing-live.</param>
/// <param name="ThumbnailUrl">Optional channel logo URL.</param>
public sealed record SxmChannel(string Id, string Name, string Number, string Guid, string? ThumbnailUrl)
{
    public int SortNumber => int.TryParse(Number, out var n) ? n : int.MaxValue;
}
