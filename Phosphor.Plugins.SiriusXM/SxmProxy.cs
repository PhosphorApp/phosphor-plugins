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

    // ── "DVR offset": play a fixed distance BEHIND the live edge so SXM's (late-published) now-playing
    // metadata lines up with the audio — matching the SiriusXM web player, which is accurate precisely
    // because it plays ~1 min behind live. We achieve it by trimming the last N segments from every
    // rewritten variant playlist, so LibVLC treats an earlier segment as the live edge. Set to 0 to
    // disable (play at the live edge again); SxmClient.LiveAudioLagMs should track this value so the
    // label anchor matches the audio. SXM AAC segments are ~10s, so 6 ≈ 60s behind.
    private const int SegmentDelayCount = 0;

    private readonly SxmClient _client;
    private readonly Action<string>? _log;
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    // Current channel context.
    private SxmChannel? _channel;
    private string _variantUrl = "";
    private Uri? _variantBase;

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    private readonly int _basePort;

    public SxmProxy(SxmClient client, int port, Action<string>? log = null)
    {
        _client = client;
        Port = port;
        _basePort = port;
        _log = log;
        // Prefix is added in Start() (after the bound port is finalized), so fallback can re-point it.
    }

    public void Start()
    {
        if (IsRunning) return;

        // Bind resiliently: the configured port can still be held by a not-yet-released HttpListener
        // from a prior source instance (settings rebuild). Try the base port, then a small range.
        HttpListenerException? last = null;
        for (var p = _basePort; p <= _basePort + 10; p++)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{p}/");
                _listener.Start();
                Port = p;
                if (p != _basePort) _log?.Invoke($"SXM proxy: port {_basePort} busy, bound {p} instead.");
                _cts = new CancellationTokenSource();
                IsRunning = true;
                _ = Task.Run(() => LoopAsync(_cts.Token));
                _log?.Invoke($"SXM proxy listening on http://127.0.0.1:{Port}/");
                return;
            }
            catch (HttpListenerException ex)
            {
                last = ex;
            }
        }
        _log?.Invoke($"SXM proxy: could not bind any port in {_basePort}..{_basePort + 10}: {last?.Message}");
        if (last != null) throw last;
        throw new InvalidOperationException("SXM proxy failed to bind a port.");
    }

    /// <summary>
    /// Points the proxy at a channel: resolves its master → variant playlist. Returns the local
    /// master URL the host should play, or null on failure.
    /// </summary>
    public async Task<string?> SetChannelAsync(SxmChannel channel, CancellationToken ct = default)
    {
        lock (_gate) _channel = channel;
        if (!await ResolveVariantAsync(channel, ct)) return null;

        // Unique per-channel URL: the proxy routes on the path suffix (master.m3u8) and ignores the
        // query, but a distinct URL forces LibVLC to re-open the stream on a channel switch instead of
        // keeping the previous channel's buffered audio (the local master URL is otherwise identical).
        return $"http://127.0.0.1:{Port}/master.m3u8?ch={Uri.EscapeDataString(channel.Id)}&t={DateTimeOffset.UtcNow.Ticks}";
    }

    /// <summary>
    /// Resolves the channel's master → variant playlist and stores it as the current window. SXM's
    /// live variant is a snapshot that stops advancing after a couple of minutes; re-resolving gets a
    /// fresh, advancing window (which is why we re-run this when the served playlist goes stale).
    /// </summary>
    private async Task<bool> ResolveVariantAsync(SxmChannel channel, CancellationToken ct)
    {
        var master = await _client.ResolveMasterPlaylistAsync(channel, ct);
        if (master == null) { _log?.Invoke("SXM proxy: master URL was null."); return false; }
        _log?.Invoke($"SXM proxy: master = {master}");

        // The master lists variant playlists; pick the first (highest-bitrate) one. The master
        // itself is an authenticated akamai request — it needs the SXM token params like every
        // other playlist/segment fetch.
        var masterText = await FetchStringWithAuthAsync(master);
        if (masterText == null) { _log?.Invoke("SXM proxy: master playlist fetch failed."); return false; }
        var masterUri = new Uri(master);
        var variantRel = masterText.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (variantRel == null)
        {
            _log?.Invoke("SXM proxy: no variant line in master playlist. Body: "
                + (masterText.Length > 200 ? masterText[..200] : masterText));
            return false;
        }

        lock (_gate)
        {
            _variantUrl = new Uri(masterUri, variantRel).ToString();
            _variantBase = new Uri(_variantUrl);
        }
        return true;
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
                await WriteTextAsync(ctx, await GetVariantBodyAsync(), "application/vnd.apple.mpegurl");
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

    // Serve the current variant playlist. Re-resolves a fresh SXM window when the current one is
    // missing (first-hit race) or has gone stale (no segment lines) — SXM's live variant snapshot
    // stops advancing after a couple of minutes, which otherwise makes LibVLC fire EndReached and
    // drop the "live" stream. Keeping the window advancing is what keeps playback continuous.
    private async Task<string> GetVariantBodyAsync()
    {
        string variantUrl;
        SxmChannel? channel;
        lock (_gate) { variantUrl = _variantUrl; channel = _channel; }

        // First hit before SetChannel finished, or a torn-down window: resolve now.
        if (string.IsNullOrEmpty(variantUrl) && channel != null)
        {
            if (await ResolveVariantAsync(channel, CancellationToken.None))
                lock (_gate) variantUrl = _variantUrl;
        }
        if (string.IsNullOrEmpty(variantUrl)) return "#EXTM3U\n";

        var raw = await FetchStringWithAuthAsync(variantUrl);
        // Stale window: the fetch failed or the snapshot no longer lists any segments. Re-resolve a
        // fresh, advancing window from now-playing-live and serve that instead.
        if ((raw == null || !HasSegments(raw)) && channel != null &&
            await ResolveVariantAsync(channel, CancellationToken.None))
        {
            lock (_gate) variantUrl = _variantUrl;
            raw = await FetchStringWithAuthAsync(variantUrl);
        }
        return RewriteVariant(raw ?? "");
    }

    // True when the playlist body contains at least one media segment (a non-comment, non-blank line).
    private static bool HasSegments(string text) => text.Split('\n')
        .Any(l => { var t = l.TrimEnd('\r'); return t.Length > 0 && !t.StartsWith('#'); });

    // Rewrite the SXM variant playlist: point EXT-X-KEY at our local key, and each segment at /seg/.
    // Also applies the DVR offset (see SegmentDelayCount): trims the last N media segments so LibVLC
    // plays behind the live edge, aligning the audio with SXM's late-published now-playing metadata.
    private string RewriteVariant(string text)
    {
        var baseUri = _variantBase;

        // Split the playlist into a header (tags before the first segment) and per-segment blocks
        // (each media segment plus the tag lines that immediately precede it, e.g. #EXTINF/#EXT-X-KEY),
        // so trimming a trailing segment also drops its own tags — keeping the playlist well-formed.
        var header = new StringBuilder();
        var blocks = new List<string>();
        var pending = new StringBuilder();
        bool seenFirstSegment = false;

        foreach (var lineRaw in text.Split('\n'))
        {
            var line = lineRaw.TrimEnd('\r');
            // Never let LibVLC treat the live window as finite (VOD). If SXM's snapshot carries an
            // EXT-X-ENDLIST or a VOD playlist-type, VLC stops at the window's end and fires EndReached
            // (the "live stream dropped" symptom). Drop these so the stream stays open-ended.
            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-PLAYLIST-TYPE", StringComparison.Ordinal))
            {
                continue;
            }

            string outLine;
            bool isSegment = false;
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                // Keep METHOD/IV but swap the URI for our local key endpoint (style B).
                outLine = System.Text.RegularExpressions.Regex.Replace(
                    line, "URI=\"[^\"]*\"", $"URI=\"http://127.0.0.1:{Port}/key\"");
            }
            else if (line.Length > 0 && !line.StartsWith('#') && baseUri != null)
            {
                var abs = new Uri(baseUri, line).ToString();
                outLine = "seg/" + EncodeSeg(abs);
                isSegment = true;
            }
            else outLine = line;

            if (!seenFirstSegment && !isSegment)
            {
                // Playlist header (before any segment): emit verbatim.
                header.Append(outLine).Append('\n');
                continue;
            }

            pending.Append(outLine).Append('\n');
            if (isSegment)
            {
                seenFirstSegment = true;
                blocks.Add(pending.ToString());
                pending.Clear();
            }
        }

        // Trim the last N segment blocks (the DVR offset), but always keep at least one so the stream
        // never goes empty on a short window.
        int keep = blocks.Count;
        if (SegmentDelayCount > 0 && blocks.Count > SegmentDelayCount)
            keep = blocks.Count - SegmentDelayCount;
        else if (blocks.Count > 1)
            keep = Math.Max(1, blocks.Count); // window too short for full offset — keep what we have.

        var sb = new StringBuilder();
        sb.Append(header);
        for (int i = 0; i < keep; i++) sb.Append(blocks[i]);
        // Any trailing non-segment lines (rare) that came after the last segment.
        if (pending.Length > 0) sb.Append(pending);
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
