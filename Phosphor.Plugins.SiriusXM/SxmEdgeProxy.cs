using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// A local HLS proxy for the EDGE-GATEWAY streaming path — the bearer/JWT replacement for the
/// cookie-based <see cref="SxmProxy"/>. It resolves a channel's master playlist via
/// <c>tuneSource</c> (whose URLs are PRE-SIGNED akamai — fetched verbatim, no auth), rewrites each
/// <c>#EXT-X-KEY</c> URI to a local <c>/key/{guid}</c> endpoint that serves the AES-128 key fetched
/// from the gateway <c>/playback/key/v1/{guid}</c> (bearer), and rewrites segment URIs to a local
/// <c>/seg/</c> endpoint. LibVLC performs the AES-128 decryption itself (proxy style B).
/// </summary>
/// <remarks>
/// This proxy talks ONLY to the edge gateway + pre-signed CDN — no legacy <c>player.siriusxm.com</c>.
/// One proxy serves one channel at a time (swap via <see cref="SetChannelAsync"/>). The host plays
/// <c>http://127.0.0.1:{port}/master.m3u8</c>.
/// </remarks>
public sealed class SxmEdgeProxy : IDisposable
{
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private readonly SxmEdgeClient _edge;
    private readonly Action<string>? _log;
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    // Current channel context.
    private SxmChannel? _channel;
    private string _variantUrl = "";
    private Uri? _variantBase;

    // Map a SHORT segment key (the .aac filename) → its full pre-signed CDN URL. The gateway URLs
    // carry a ~700-char session token in ONE path segment; embedding that (even base64url'd) in the
    // local proxy path trips http.sys's UrlSegmentMaxLength (~260) and it 400s BEFORE our handler
    // runs. Serving a short filename and looking the URL up here avoids that entirely.
    private readonly Dictionary<string, string> _segUrlByName = new(StringComparer.Ordinal);

    // Cache of fetched AES-128 keys by GUID (served from the local /key/{guid} endpoint).
    private readonly Dictionary<string, byte[]> _keyCache = new(StringComparer.OrdinalIgnoreCase);

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    private readonly int _basePort;

    public SxmEdgeProxy(SxmEdgeClient edge, int port, Action<string>? log = null)
    {
        _edge = edge;
        Port = port;
        _basePort = port;
        _log = log;
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
                if (p != _basePort) _log?.Invoke($"SXM edge proxy: port {_basePort} busy, bound {p} instead.");
                _cts = new CancellationTokenSource();
                IsRunning = true;
                _ = Task.Run(() => LoopAsync(_cts.Token));
                _log?.Invoke($"SXM edge proxy listening on http://127.0.0.1:{Port}/");
                return;
            }
            catch (HttpListenerException ex) { last = ex; }
        }
        _log?.Invoke($"SXM edge proxy: could not bind any port in {_basePort}..{_basePort + 10}: {last?.Message}");
        if (last != null) throw last;
        throw new InvalidOperationException("SXM edge proxy failed to bind a port.");
    }

    /// <summary>
    /// Points the proxy at a channel: resolves its master → variant playlist via tuneSource. Returns
    /// the local master URL the host should play, or null on failure (no legacy fallback).
    /// </summary>
    public async Task<string?> SetChannelAsync(SxmChannel channel, CancellationToken ct = default)
    {
        lock (_gate) _channel = channel;
        if (!await ResolveVariantAsync(channel, ct)) return null;

        // Unique per-channel URL so LibVLC re-opens the stream on a channel switch instead of keeping
        // the previous channel's buffered audio (the local master URL is otherwise identical).
        return $"http://127.0.0.1:{Port}/master.m3u8?ch={Uri.EscapeDataString(channel.Id)}&t={DateTimeOffset.UtcNow.Ticks}";
    }

    /// <summary>
    /// Resolves the channel's master → variant playlist (highest-bitrate) via tuneSource and stores it
    /// as the current window. Re-run when the served window goes stale (pre-signed URLs expire).
    /// </summary>
    private async Task<bool> ResolveVariantAsync(SxmChannel channel, CancellationToken ct)
    {
        var stream = await _edge.TuneSourceAsync(channel, ct);
        if (stream == null) { _log?.Invoke("SXM edge proxy: tuneSource returned null (no legacy fallback)."); return false; }

        // The tuneSource URL may be EITHER a master (lists variant playlists via #EXT-X-STREAM-INF) OR
        // already a media playlist (segments via #EXTINF) — the name "..._variant_web_v3.m3u8" is
        // ambiguous. Fetch it (pre-signed CDN, verbatim) and branch on what it actually is.
        var masterText = await FetchCdnStringAsync(stream.MasterUrl);
        if (masterText == null) { _log?.Invoke("SXM edge proxy: playlist fetch failed."); return false; }

        var isMaster = masterText.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
        var isMedia = masterText.Contains("#EXTINF", StringComparison.Ordinal);
        _log?.Invoke($"SXM edge proxy: playlist isMaster={isMaster} isMedia={isMedia} len={masterText.Length}");

        string variantAbs;
        if (isMaster)
        {
            var masterUri = new Uri(stream.MasterUrl);
            var variantRel = masterText.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (variantRel == null)
            {
                _log?.Invoke("SXM edge proxy: master had no variant line. Body: "
                    + (masterText.Length > 300 ? masterText[..300] : masterText));
                return false;
            }
            variantAbs = new Uri(masterUri, variantRel).ToString();
        }
        else if (isMedia)
        {
            // The tuneSource URL is already the media playlist — use it directly as the variant.
            variantAbs = stream.MasterUrl;
        }
        else
        {
            _log?.Invoke("SXM edge proxy: playlist is neither master nor media. Body: "
                + (masterText.Length > 300 ? masterText[..300] : masterText));
            return false;
        }

        lock (_gate)
        {
            _variantUrl = variantAbs;
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
                await WriteTextAsync(ctx, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=256000\nvariant.m3u8\n",
                    "application/vnd.apple.mpegurl");
            }
            else if (path.EndsWith("variant.m3u8"))
            {
                await WriteTextAsync(ctx, await GetVariantBodyAsync(), "application/vnd.apple.mpegurl");
            }
            else if (path.StartsWith("/key/"))
            {
                // Style B: LibVLC fetches the key here and decrypts segments itself. The GUID in the
                // path identifies the gateway content key.
                var guid = path["/key/".Length..];
                var key = await GetKeyAsync(guid);
                await WriteBytesAsync(ctx, key ?? Array.Empty<byte>(),
                    "application/octet-stream", key == null ? 502 : 200);
            }
            else if (path.StartsWith("/seg/"))
            {
                var name = Uri.UnescapeDataString(path["/seg/".Length..]);
                string? segUrl;
                lock (_gate) _segUrlByName.TryGetValue(name, out segUrl);
                if (segUrl == null)
                {
                    _log?.Invoke($"SXM edge proxy: unknown segment '{name}'.");
                    await WriteBytesAsync(ctx, Array.Empty<byte>(), "audio/aac", 404);
                }
                else
                {
                    var bytes = await FetchCdnBytesAsync(segUrl);
                    await WriteBytesAsync(ctx, bytes ?? Array.Empty<byte>(),
                        "audio/aac", bytes == null ? 502 : 200);
                }
            }
            else ctx.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"SXM edge proxy error for {path}: {ex.Message}");
            try { ctx.Response.StatusCode = 500; } catch { }
        }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    // Serve the current variant playlist; re-resolve a fresh window when missing or stale (pre-signed
    // URLs expire, and SXM's live variant snapshot stops advancing after a couple of minutes).
    private async Task<string> GetVariantBodyAsync()
    {
        string variantUrl;
        SxmChannel? channel;
        lock (_gate) { variantUrl = _variantUrl; channel = _channel; }

        if (string.IsNullOrEmpty(variantUrl) && channel != null)
        {
            if (await ResolveVariantAsync(channel, CancellationToken.None))
                lock (_gate) variantUrl = _variantUrl;
        }
        if (string.IsNullOrEmpty(variantUrl)) return "#EXTM3U\n";

        var raw = await FetchCdnStringAsync(variantUrl);
        if ((raw == null || !HasSegments(raw)) && channel != null &&
            await ResolveVariantAsync(channel, CancellationToken.None))
        {
            lock (_gate) variantUrl = _variantUrl;
            raw = await FetchCdnStringAsync(variantUrl);
        }
        var rewritten = RewriteVariant(raw ?? "");
        _log?.Invoke($"SXM edge proxy: served variant rawLen={raw?.Length ?? 0} segs={(raw != null ? CountSegments(raw) : 0)} outLen={rewritten.Length}");
        return rewritten;
    }

    private static int CountSegments(string text) => text.Split('\n')
        .Count(l => { var t = l.TrimEnd('\r'); return t.Length > 0 && !t.StartsWith('#'); });

    private static bool HasSegments(string text) => text.Split('\n')
        .Any(l => { var t = l.TrimEnd('\r'); return t.Length > 0 && !t.StartsWith('#'); });

    // Number of trailing segments to serve as the live window. The gateway media playlist is a huge
    // multi-hour DVR window (~1800 segments); serving all of it makes VLC start hours behind live and
    // bloats the playlist. Keep a small live tail (~LiveWindowSegments × ~10s).
    private const int LiveWindowSegments = 12;

    // Rewrite the variant playlist: redirect EXT-X-KEY to our local /key, map each segment to a short
    // /seg/ name (http.sys rejects long paths), strip ENDLIST/PLAYLIST-TYPE so VLC keeps it live, and
    // TRIM to the last LiveWindowSegments (adjusting EXT-X-MEDIA-SEQUENCE so VLC counts correctly).
    private string RewriteVariant(string text)
    {
        var baseUri = _variantBase;

        // Split into header (tags before the first segment) and per-segment blocks (each segment plus
        // the tag lines that immediately precede it, e.g. #EXTINF / #EXT-X-KEY / #EXT-X-PROGRAM-DATE-TIME).
        var headerLines = new List<string>();
        var blocks = new List<string>();
        var pending = new StringBuilder();
        bool seenFirstSegment = false;
        long mediaSequence = 0;
        bool haveMediaSequence = false;

        foreach (var lineRaw in text.Split('\n'))
        {
            var line = lineRaw.TrimEnd('\r');
            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-PLAYLIST-TYPE", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                haveMediaSequence = long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), out mediaSequence);
                continue; // re-emitted (adjusted) below
            }

            string outLine;
            bool isSegment = false;
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                var matches = GuidPattern.Matches(line);
                if (matches.Count > 0)
                    outLine = Regex.Replace(line, "URI=\"[^\"]*\"", $"URI=\"http://127.0.0.1:{Port}/key/{matches[^1].Value}\"");
                else { _log?.Invoke($"SXM edge proxy: EXT-X-KEY without a GUID: {line}"); outLine = line; }
            }
            else if (line.Length > 0 && !line.StartsWith('#') && baseUri != null)
            {
                var abs = new Uri(baseUri, line).ToString();
                var name = SegKeyFor(abs);
                lock (_gate) _segUrlByName[name] = abs;
                outLine = "seg/" + name;
                isSegment = true;
            }
            else outLine = line;

            if (!seenFirstSegment && !isSegment)
            {
                headerLines.Add(outLine);
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

        // Keep only the last LiveWindowSegments blocks; bump the media sequence past the dropped ones.
        int drop = Math.Max(0, blocks.Count - LiveWindowSegments);
        long outSeq = haveMediaSequence ? mediaSequence + drop : 0;

        var sb = new StringBuilder();
        foreach (var h in headerLines) sb.Append(h).Append('\n');
        if (haveMediaSequence || drop > 0) sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(outSeq).Append('\n');
        for (int i = drop; i < blocks.Count; i++) sb.Append(blocks[i]);
        if (pending.Length > 0) sb.Append(pending);
        return sb.ToString();
    }

    // Short, unique, path-safe key for a segment URL: its filename (e.g. 9446_256k_..._v3.aac). If two
    // segments ever share a filename, disambiguate with a short hash suffix.
    private static string SegKeyFor(string url)
    {
        var name = url;
        var q = name.IndexOf('?');
        if (q >= 0) name = name[..q];
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return string.IsNullOrEmpty(name) ? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16] : name;
    }

    private async Task<byte[]?> GetKeyAsync(string guid)
    {
        lock (_gate) { if (_keyCache.TryGetValue(guid, out var cached)) return cached; }
        var key = await _edge.GetKeyAsync(guid);
        if (key != null) lock (_gate) _keyCache[guid] = key;
        return key;
    }

    // ── Pre-signed CDN fetch helpers (verbatim; no auth injection) ──────────────

    private async Task<string?> FetchCdnStringAsync(string url)
    {
        using var r = await _edge.GetCdnAsync(url);
        if (r == null || !r.IsSuccessStatusCode)
        {
            if (r != null) _log?.Invoke($"SXM edge proxy: CDN {(int)r.StatusCode} for playlist.");
            return null;
        }
        return await r.Content.ReadAsStringAsync();
    }

    private async Task<byte[]?> FetchCdnBytesAsync(string url)
    {
        using var r = await _edge.GetCdnAsync(url);
        if (r == null || !r.IsSuccessStatusCode)
        {
            if (r != null) _log?.Invoke($"SXM edge proxy: CDN {(int)r.StatusCode} for segment.");
            return null;
        }
        return await r.Content.ReadAsByteArrayAsync();
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
