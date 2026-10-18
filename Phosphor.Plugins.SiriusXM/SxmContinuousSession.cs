using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// EXPERIMENTAL "continuous" stream mode. Instead of handing LibVLC an HLS playlist (whose polling cadence
/// vs. the segment cadence appears to trigger late-buffer drops in LibVLC's adaptive module), this session
/// fetches and AES-128-decrypts the HLS segments itself and serves them as ONE never-ending packed-AAC
/// (ADTS) HTTP stream. LibVLC then treats it as a plain network stream: no playlist polling, no per-segment
/// timeline handling.
/// </summary>
/// <remarks>
/// A background feeder polls the CDN media playlist, fetches/decrypts new segments into a small ring, and
/// each connected client is fed sequentially starting <c>liveDelaySegments</c> behind the live edge. Every
/// segment is also analysed (ADTS frame count, real audio duration, ID3 timestamp continuity) and logged,
/// so anomalies inside the stream itself become visible.
/// </remarks>
internal sealed class SxmContinuousSession : IDisposable
{
    private static readonly Regex GuidRx = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex IvRx = new(@"IV=0x([0-9A-Fa-f]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
    private static readonly byte[] TsOwner = Encoding.ASCII.GetBytes("com.apple.streaming.transportStreamTimestamp");

    private const int PollMs = 4000;
    private const int RingMax = 40;
    private const int MaxFailPolls = 3;

    private sealed record SegInfo(long Seq, string Url, double Dur, string? KeyGuid, byte[]? Iv, bool Discontinuity);
    private sealed record Chunk(long Seq, byte[] Data, double AudioMs);
    private readonly record struct AdtsStats(
        int Frames, int SampleRate, double AudioMs, int FirstFramePos, int MultiBlock, int LostSyncBytes, int Trailing);

    private readonly Func<Task<(string? raw, Uri? baseUri)>> _getPlaylist;
    private readonly Func<string, string, Task<(byte[]? bytes, int status)>> _fetchSegment;
    private readonly Func<string, Task<byte[]?>> _getKey;
    private readonly Func<string, Task> _onFetchFailure;
    private readonly Func<int> _getDelay;
    private readonly Action<string>? _log;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<long, Chunk> _ring = new();
    private long _ringMin = -1;
    private long _ringMax = -1;
    private long _liveEdge = -1;
    private long _lastIngested = long.MinValue;
    private TaskCompletionSource _changed = NewTcs();

    // Feeder-thread-only state.
    private long _failSeq = -1;
    private int _failPolls;
    private long _prevSeq = -1;
    private long _prevPts = -1;
    private double _prevAudioMs;
    private bool _loggedPlaylistInfo;

    // Delay (segments behind the live edge) the connected client actually started with, and the audio length
    // of one segment; the proxy uses these to tell now-playing how far behind live the heard audio is.
    private int _activeDelay = -1;
    private double _segMs = 10000;
    public int ActiveDelaySegments => Volatile.Read(ref _activeDelay);
    public double SegmentMs => Volatile.Read(ref _segMs);

    public SxmContinuousSession(
        Func<Task<(string? raw, Uri? baseUri)>> getPlaylist,
        Func<string, string, Task<(byte[]? bytes, int status)>> fetchSegment,
        Func<string, Task<byte[]?>> getKey,
        Func<string, Task> onFetchFailure,
        Func<int> getDelay,
        Action<string>? log)
    {
        _getPlaylist = getPlaylist;
        _fetchSegment = fetchSegment;
        _getKey = getKey;
        _onFetchFailure = onFetchFailure;
        _getDelay = getDelay;
        _log = log;
    }

    public void Start() => _ = Task.Run(() => FeedAsync(_cts.Token));

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        Signal();
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_lock) { old = _changed; _changed = NewTcs(); }
        old.TrySetResult();
    }

    // ── Client side ─────────────────────────────────────────────────────────────

    /// <summary>Waits (up to ~20s) until the feeder has seen the live edge, so a client can pick its start.</summary>
    public async Task<bool> WaitReadyAsync(CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        while (!linked.IsCancellationRequested)
        {
            Task changed; bool ready;
            lock (_lock) { ready = _liveEdge >= 0; changed = _changed.Task; }
            if (ready) return true;
            if (waited.Elapsed > TimeSpan.FromSeconds(20)) return false;
            try { await changed.WaitAsync(TimeSpan.FromSeconds(1), linked.Token); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>
    /// Streams segments to one client, starting <c>delaySegments</c> behind the live edge and then following
    /// the feeder. Returns when the client disconnects or the session is disposed.
    /// </summary>
    public async Task ServeAsync(Stream output, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var token = linked.Token;
        var clock = new Stopwatch();
        double sentMs = 0;
        int sent = 0;
        long bytesSent = 0;
        int delay = _getDelay();
        Volatile.Write(ref _activeDelay, delay);
        long next;
        long edge0;
        lock (_lock) { edge0 = _liveEdge; next = _liveEdge - delay; }
        _log?.Invoke($"SXM continuous: client connected startSeq={next} liveEdge={edge0} delaySegments={delay}.");
        var reason = "closed";
        try
        {
            while (!token.IsCancellationRequested)
            {
                Chunk? c; Task changed; long min, max, edge;
                lock (_lock) { _ring.TryGetValue(next, out c); changed = _changed.Task; min = _ringMin; max = _ringMax; edge = _liveEdge; }
                if (c == null)
                {
                    if (max >= 0 && next < min)
                    {
                        _log?.Invoke($"SXM continuous: client behind ring (wanted seq={next}, oldest={min}); jumping forward.");
                        next = min;
                        continue;
                    }
                    try { await changed.WaitAsync(TimeSpan.FromSeconds(30), token); }
                    catch (TimeoutException)
                    {
                        _log?.Invoke($"SXM continuous: client still waiting for seq={next} after 30s (liveEdge={edge}, newestIngested={max}).");
                    }
                    continue;
                }
                if (c.Data.Length == 0)
                {
                    _log?.Invoke($"SXM continuous: skipping gap seq={c.Seq}.");
                    next++;
                    continue;
                }

                if (!clock.IsRunning) clock.Start();
                await output.WriteAsync(c.Data, token);
                await output.FlushAsync(token);
                sent++;
                bytesSent += c.Data.Length;
                sentMs += c.AudioMs;
                next++;
                _log?.Invoke(FormattableString.Invariant(
                    $"SXM continuous: client sent seq={c.Seq} bytes={c.Data.Length} aheadMs={sentMs - clock.ElapsedMilliseconds:F0} behindEdge={edge - c.Seq}."));
            }
        }
        catch (OperationCanceledException) { reason = "cancelled"; }
        catch (Exception ex) { reason = $"{ex.GetType().Name}: {ex.Message}"; }
        _log?.Invoke($"SXM continuous: client ended after {clock.Elapsed.TotalSeconds:F0}s, sent {sent} segments / {bytesSent} bytes ({reason}).");
    }

    // ── Feeder side ─────────────────────────────────────────────────────────────

    private async Task FeedAsync(CancellationToken ct)
    {
        _log?.Invoke($"SXM continuous: session started (delaySegments={_getDelay()}, pollMs={PollMs}).");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (raw, baseUri) = await _getPlaylist();
                if (raw != null && baseUri != null)
                    await IngestAsync(Parse(raw, baseUri), ct);
                else
                    _log?.Invoke("SXM continuous: playlist unavailable this poll.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log?.Invoke($"SXM continuous: feed error {ex.GetType().Name}: {ex.Message}"); }
            try { await Task.Delay(PollMs, ct); }
            catch (OperationCanceledException) { break; }
        }
        _log?.Invoke("SXM continuous: session stopped.");
    }

    private static List<SegInfo> Parse(string raw, Uri baseUri)
    {
        var list = new List<SegInfo>();
        long mediaSeq = 0, idx = 0;
        string? keyGuid = null;
        byte[]? iv = null;
        double dur = 0;
        bool disc = false;
        foreach (var lineRaw in raw.Split('\n'))
        {
            var line = lineRaw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                    long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), out mediaSeq);
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    var encrypted = line.Contains("METHOD=AES-128", StringComparison.Ordinal);
                    var m = GuidRx.Matches(line);
                    keyGuid = encrypted && m.Count > 0 ? m[^1].Value : null;
                    iv = ParseIv(line);
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var t = line["#EXTINF:".Length..];
                    var comma = t.IndexOf(',');
                    if (comma >= 0) t = t[..comma];
                    double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
                }
                else if (line.Equals("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
                    disc = true;
                continue;
            }
            list.Add(new SegInfo(mediaSeq + idx, new Uri(baseUri, line).ToString(), dur, keyGuid, iv, disc));
            idx++;
            dur = 0;
            disc = false;
        }
        return list;
    }

    private static byte[]? ParseIv(string keyLine)
    {
        var m = IvRx.Match(keyLine);
        if (!m.Success) return null;
        var hex = m.Groups[1].Value;
        if (hex.Length > 32) hex = hex[^32..];
        if (hex.Length % 2 != 0) hex = "0" + hex;
        return Convert.FromHexString(hex.PadLeft(32, '0'));
    }

    private async Task IngestAsync(List<SegInfo> segs, CancellationToken ct)
    {
        if (segs.Count == 0) return;
        var newest = segs[^1].Seq;
        var delay = _getDelay();
        if (!_loggedPlaylistInfo)
        {
            _loggedPlaylistInfo = true;
            var last = segs[^1];
            _log?.Invoke($"SXM continuous: playlist segments={segs.Count} seq={segs[0].Seq}..{newest} encrypted={last.KeyGuid != null} iv={(last.Iv != null ? "explicit" : "sequence")}.");
        }

        long from;
        lock (_lock)
        {
            _liveEdge = newest;
            if (_lastIngested == long.MinValue) _lastIngested = newest - delay - 1;
            from = _lastIngested + 1;
            var floor = newest - RingMax + 1;
            if (from < floor)
            {
                _log?.Invoke($"SXM continuous: feeder fell behind; skipping seq {from}..{floor - 1}.");
                _ring.Clear();
                _ringMin = -1;
                _ringMax = -1;
                from = floor;
                _lastIngested = from - 1;
            }
        }
        Signal(); // live edge known: clients can pick their start sequence

        foreach (var s in segs)
        {
            if (s.Seq < from) continue;
            ct.ThrowIfCancellationRequested();
            if (!await IngestOneAsync(s)) break;
        }
    }

    // Returns true to continue with the next segment, false to stop and retry this one on the next poll.
    private async Task<bool> IngestOneAsync(SegInfo s)
    {
        bool Fail(string why)
        {
            if (_failSeq == s.Seq) _failPolls++; else { _failSeq = s.Seq; _failPolls = 1; }
            _log?.Invoke($"SXM continuous: seq={s.Seq} {why} (attempt {_failPolls}/{MaxFailPolls}).");
            if (_failPolls < MaxFailPolls) return false;
            AddChunk(new Chunk(s.Seq, Array.Empty<byte>(), 0));
            _log?.Invoke($"SXM continuous: giving up on seq={s.Seq}; the stream will skip it.");
            _failSeq = -1;
            _failPolls = 0;
            return true;
        }

        var sw = Stopwatch.StartNew();
        var (cipher, status) = await _fetchSegment(s.Url, s.Seq.ToString(CultureInfo.InvariantCulture));
        var fetchMs = sw.ElapsedMilliseconds;
        if (cipher == null)
        {
            try { await _onFetchFailure($"continuous segment CDN {status}"); } catch { }
            return Fail($"fetch failed status={status} elapsedMs={fetchMs}");
        }

        byte[] plain;
        try
        {
            var decrypted = await DecryptAsync(s, cipher);
            if (decrypted == null) return Fail("content key unavailable");
            plain = decrypted;
        }
        catch (Exception ex)
        {
            return Fail($"decrypt failed {ex.GetType().Name}: {ex.Message}");
        }
        _failSeq = -1;
        _failPolls = 0;

        var (data, id3Bytes, pts) = StripId3(plain);
        var st = Analyze(data);
        var declMs = s.Dur * 1000.0;
        var audioMs = st.AudioMs > 0 ? st.AudioMs : declMs;
        if (audioMs > 0) Volatile.Write(ref _segMs, audioMs);

        var flags = new List<string>();
        if (declMs > 0 && Math.Abs(st.AudioMs - declMs) > 5) flags.Add("durMismatch");
        if (st.MultiBlock > 0) flags.Add($"multiBlock={st.MultiBlock}");
        if (st.LostSyncBytes > 0) flags.Add($"lostSyncBytes={st.LostSyncBytes}");
        if (st.Trailing > 0) flags.Add($"trailing={st.Trailing}");
        if (st.FirstFramePos > 0) flags.Add($"firstFramePos={st.FirstFramePos}");
        if (s.Discontinuity) flags.Add("discontinuityTag");
        var dPts = "-";
        if (pts >= 0 && _prevPts >= 0 && _prevSeq == s.Seq - 1)
        {
            var d = ((pts - _prevPts) & 0x1FFFFFFFFL) / 90.0;
            dPts = d.ToString("F1", CultureInfo.InvariantCulture);
            if (Math.Abs(d - _prevAudioMs) > 3)
                flags.Add(FormattableString.Invariant($"ptsJump(d={d:F1}ms,expected={_prevAudioMs:F1}ms)"));
        }
        _prevSeq = s.Seq;
        _prevPts = pts;
        _prevAudioMs = audioMs;

        var ringCount = AddChunk(new Chunk(s.Seq, data, audioMs));
        _log?.Invoke(FormattableString.Invariant(
            $"SXM continuous: ingest seq={s.Seq} fetchMs={fetchMs} bytes={data.Length} id3={id3Bytes} frames={st.Frames} sr={st.SampleRate} audioMs={st.AudioMs:F1} declMs={declMs:F1} dPtsMs={dPts} ring={ringCount}")
            + (flags.Count > 0 ? " ANOMALY " + string.Join(' ', flags) : ""));
        return true;
    }

    private int AddChunk(Chunk c)
    {
        int count;
        lock (_lock)
        {
            _ring[c.Seq] = c;
            if (_ringMin < 0) _ringMin = c.Seq;
            _ringMax = c.Seq;
            _lastIngested = c.Seq;
            while (_ringMax - _ringMin + 1 > RingMax) { _ring.Remove(_ringMin); _ringMin++; }
            count = _ring.Count;
        }
        Signal();
        return count;
    }

    // ── Decrypt / parse helpers ─────────────────────────────────────────────────

    private async Task<byte[]?> DecryptAsync(SegInfo s, byte[] cipher)
    {
        if (s.KeyGuid == null) return cipher; // not encrypted
        var key = await _getKey(s.KeyGuid);
        if (key == null) return null;
        var iv = s.Iv;
        if (iv == null)
        {
            // HLS default IV: the segment's media sequence number as a 128-bit big-endian integer.
            iv = new byte[16];
            BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), s.Seq);
        }
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7);
    }

    // Removes leading ID3v2 tags (HLS packed-audio segments each start with one carrying the PTS) and returns
    // the transportStreamTimestamp PTS (33-bit, 90 kHz) when present, else -1.
    private static (byte[] data, int id3Bytes, long pts) StripId3(byte[] d)
    {
        int off = 0;
        long pts = -1;
        while (d.Length - off >= 10 && d[off] == (byte)'I' && d[off + 1] == (byte)'D' && d[off + 2] == (byte)'3')
        {
            int size = ((d[off + 6] & 0x7F) << 21) | ((d[off + 7] & 0x7F) << 14) | ((d[off + 8] & 0x7F) << 7) | (d[off + 9] & 0x7F);
            int total = 10 + size + ((d[off + 5] & 0x10) != 0 ? 10 : 0);
            if (off + total > d.Length) break;
            ReadOnlySpan<byte> tag = d.AsSpan(off, total);
            int at = tag.IndexOf(TsOwner);
            if (at >= 0)
            {
                int p = at + TsOwner.Length + 1;
                if (p + 8 <= tag.Length)
                    pts = (long)(BinaryPrimitives.ReadUInt64BigEndian(tag.Slice(p, 8)) & 0x1FFFFFFFFUL);
            }
            off += total;
        }
        return off == 0 ? (d, 0, pts) : (d[off..], off, pts);
    }

    // Walks the ADTS frames of one (already ID3-stripped) segment.
    private static AdtsStats Analyze(byte[] d)
    {
        int pos = 0, frames = 0, multi = 0, lost = 0, firstPos = -1, sr = 0;
        double samples = 0;
        while (pos + 7 <= d.Length)
        {
            if (d[pos] != 0xFF || (d[pos + 1] & 0xF0) != 0xF0) { lost++; pos++; continue; }
            int srIdx = (d[pos + 2] >> 2) & 0x0F;
            int frameLen = ((d[pos + 3] & 0x03) << 11) | (d[pos + 4] << 3) | (d[pos + 5] >> 5);
            int blocks = d[pos + 6] & 0x03;
            if (srIdx >= SampleRates.Length || frameLen < 7) { lost++; pos++; continue; }
            if (pos + frameLen > d.Length) break; // truncated final frame: reported via Trailing
            if (firstPos < 0) firstPos = pos;
            if (blocks != 0) multi++;
            if (sr == 0) sr = SampleRates[srIdx];
            samples += 1024.0 * (blocks + 1);
            frames++;
            pos += frameLen;
        }
        var ms = sr > 0 ? samples * 1000.0 / sr : 0;
        return new AdtsStats(frames, sr, ms, Math.Max(firstPos, 0), multi, lost, d.Length - pos);
    }
}
