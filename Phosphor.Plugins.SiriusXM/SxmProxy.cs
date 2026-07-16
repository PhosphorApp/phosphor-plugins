using System.Net;
using System.Text;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// A tiny local HLS proxy that makes a SiriusXM channel playable by a plain HLS player (LibVLC).
/// <b>Proxy style B (LibVLC decrypts):</b> the proxy passes the SXM variant playlist through, but
/// rewrites the <c>#EXT-X-KEY</c> URI to a local <c>/key</c> endpoint that serves the static AES-128
/// key, and rewrites segment URIs to a local <c>/seg</c> endpoint that fetches them from SXM with the
/// auth token params injected. LibVLC then performs the AES-128 decryption itself. (Style A —
/// decrypt-in-transit here — is the proven fallback but keeps decryption in our process.)
/// </summary>
/// <remarks>
/// One proxy serves one channel at a time (swap via <see cref="SetChannel"/>). The host plays
/// <c>http://127.0.0.1:{port}/master.m3u8</c>. Segment/playlist requests that come back 403 trigger a
/// re-auth + token refresh (lean session handling).
/// </remarks>
public sealed class SxmProxy : IDisposable
{
    // Static, publicly-known SXM HLS segment AES-128 key.
    private static readonly byte[] StaticKey = Convert.FromBase64String("0Nsco7MAgxowGvkUT8aYag==");

    private readonly SxmClient _client;
    private readonly Action<string>? _log;
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    // Current channel context.
    private SxmChannel? _channel;
    private string _variantUrl = "";
    private Uri? _variantBase;

    public int Port { get; }
    public bool IsRunning { get; private set; }

    public SxmProxy(SxmClient client, int port, Action<string>? log = null)
    {
        _client = client;
        Port = port;
        _log = log;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        if (IsRunning) return;
        _listener.Start();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _ = Task.Run(() => LoopAsync(_cts.Token));
        _log?.Invoke($"SXM proxy listening on http://127.0.0.1:{Port}/");
    }

    /// <summary>
    /// Points the proxy at a channel: resolves its master → variant playlist. Returns the local
    /// master URL the host should play, or null on failure.
    /// </summary>
    public async Task<string?> SetChannelAsync(SxmChannel channel, CancellationToken ct = default)
    {
        var master = await _client.ResolveMasterPlaylistAsync(channel, ct);
        if (master == null) { _log?.Invoke("SXM proxy: master URL was null."); return null; }
        _log?.Invoke($"SXM proxy: master = {master}");

        // The master lists variant playlists; pick the first (highest-bitrate) one. The master
        // itself is an authenticated akamai request — it needs the SXM token params like every
        // other playlist/segment fetch.
        var masterText = await FetchStringWithAuthAsync(master);
        if (masterText == null) { _log?.Invoke("SXM proxy: master playlist fetch failed."); return null; }
        var masterUri = new Uri(master);
        var variantRel = masterText.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (variantRel == null)
        {
            _log?.Invoke("SXM proxy: no variant line in master playlist. Body: "
                + (masterText.Length > 200 ? masterText[..200] : masterText));
            return null;
        }

        lock (_gate)
        {
            _channel = channel;
            _variantUrl = new Uri(masterUri, variantRel).ToString();
            _variantBase = new Uri(_variantUrl);
        }
        return $"http://127.0.0.1:{Port}/master.m3u8";
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        try
        {
            if (path.EndsWith("master.m3u8"))
            {
                // Single-variant master pointing at our own variant playlist.
                await WriteTextAsync(ctx, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=256000\nvariant.m3u8\n",
                    "application/vnd.apple.mpegurl");
            }
            else if (path.EndsWith("variant.m3u8"))
            {
                var raw = await FetchStringWithAuthAsync(_variantUrl);
                await WriteTextAsync(ctx, RewriteVariant(raw ?? ""), "application/vnd.apple.mpegurl");
            }
            else if (path.EndsWith("/key"))
            {
                // Style B: LibVLC fetches the key here and decrypts segments itself.
                await WriteBytesAsync(ctx, StaticKey, "application/octet-stream");
            }
            else if (path.StartsWith("/seg/"))
            {
                var b64 = path["/seg/".Length..];
                var segUrl = DecodeSeg(b64);
                var bytes = await FetchBytesWithAuthAsync(segUrl);
                await WriteBytesAsync(ctx, bytes ?? Array.Empty<byte>(),
                    "audio/aac", bytes == null ? 502 : 200);
            }
            else ctx.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"SXM proxy error for {path}: {ex.Message}");
            try { ctx.Response.StatusCode = 500; } catch { }
        }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    // Rewrite the SXM variant playlist: point EXT-X-KEY at our local key, and each segment at /seg/.
    private string RewriteVariant(string text)
    {
        var baseUri = _variantBase;
        var sb = new StringBuilder();
        foreach (var lineRaw in text.Split('\n'))
        {
            var line = lineRaw.TrimEnd('\r');
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                // Keep METHOD/IV but swap the URI for our local key endpoint (style B).
                sb.Append(System.Text.RegularExpressions.Regex.Replace(
                    line, "URI=\"[^\"]*\"", $"URI=\"http://127.0.0.1:{Port}/key\""));
                sb.Append('\n');
            }
            else if (line.Length > 0 && !line.StartsWith('#') && baseUri != null)
            {
                var abs = new Uri(baseUri, line).ToString();
                sb.Append("seg/").Append(EncodeSeg(abs)).Append('\n');
            }
            else sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static string EncodeSeg(string url) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string DecodeSeg(string b64)
    {
        var s = b64.Replace('-', '+').Replace('_', '/');
        s = s.PadRight((s.Length + 3) / 4 * 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    // ── SXM fetch helpers (inject token params; retry once on 403 via re-auth) ──

    private async Task<string?> FetchStringWithAuthAsync(string url)
    {
        var (ok, body, _) = await GetWithAuthAsync(url, wantBytes: false);
        return ok ? body as string : null;
    }

    private async Task<byte[]?> FetchBytesWithAuthAsync(string url)
    {
        var (ok, _, bytes) = await GetWithAuthAsync(url, wantBytes: true);
        return ok ? bytes : null;
    }

    private async Task<(bool ok, object? text, byte[]? bytes)> GetWithAuthAsync(string url, bool wantBytes)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var tp = _client.TokenParams;
            var full = url + (url.Contains('?') ? "&" : "?") + tp;
            using var r = await _client.Http.GetAsync(full);
            if (r.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                _log?.Invoke("SXM 403 — re-authenticating session.");
                await _client.AuthenticateAsync();
                continue;
            }
            if (!r.IsSuccessStatusCode)
            {
                _log?.Invoke($"SXM fetch {(int)r.StatusCode} for {url} (tokenParams len={tp.Length})");
                return (false, null, null);
            }
            if (wantBytes) return (true, null, await r.Content.ReadAsByteArrayAsync());
            return (true, await r.Content.ReadAsStringAsync(), null);
        }
        return (false, null, null);
    }

    // ── HTTP write helpers ──────────────────────────────────────────────────────

    private static async Task WriteTextAsync(HttpListenerContext ctx, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task WriteBytesAsync(HttpListenerContext ctx, byte[] bytes, string contentType, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0) await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        IsRunning = false;
    }
}
