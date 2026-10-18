using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// Continuous stream mode. Instead of handing LibVLC an HLS playlist (whose polling cadence
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
        int Frames, int SampleRate, double AudioMs, int FirstFramePos, int MultiBlock,
        int JunkBytes, int InlineId3Tags, int InlineId3Bytes, int Trailing);

    // Position (not a copy) of one segment's lines within the raw playlist text.
    private readonly record struct SegRef(int UrlStart, int UrlLen, int DurStart, int DurLen, int KeyStart, int KeyLen, bool Disc);

    // Result of one cheap pass over the raw playlist text: the media sequence, the segment count and the positions
    // of the newest RingMax segments. The playlist holds ~1,800 segments but only the tail is ever used, so nothing
    // else is parsed or allocated.
    private sealed class PlaylistScan
    {
        public long FirstSeq;
        public int Total;
        public readonly SegRef[] Tail = new SegRef[RingMax];
        public long Newest => FirstSeq + Total - 1;
    }

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

    // Lifetime: the session stops itself when no client has been connected for _idleStop (covers a host that never
    // signals a stop), and the host can stop it explicitly through TryStopForRelease.
    private static readonly TimeSpan DefaultIdleStop = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _idleStop;
    private readonly int _pollMs;
    private readonly Action<SxmContinuousSession>? _onStopped;
    private readonly Stopwatch _age = Stopwatch.StartNew();
    private int _clients;
    private volatile bool _hadClient;
    private long _idleSinceTs = Stopwatch.GetTimestamp();

    public SxmContinuousSession(
        Func<Task<(string? raw, Uri? baseUri)>> getPlaylist,
        Func<string, string, Task<(byte[]? bytes, int status)>> fetchSegment,
        Func<string, Task<byte[]?>> getKey,
        Func<string, Task> onFetchFailure,
        Func<int> getDelay,
        Action<string>? log,
        Action<SxmContinuousSession>? onStopped = null,
        TimeSpan? idleStop = null,
        int pollMs = PollMs)
    {
        _getPlaylist = getPlaylist;
        _fetchSegment = fetchSegment;
        _getKey = getKey;
        _onFetchFailure = onFetchFailure;
        _getDelay = getDelay;
        _log = log;
        _onStopped = onStopped;
        _idleStop = idleStop ?? DefaultIdleStop;
        _pollMs = pollMs;
    }

    public void Start() => _ = Task.Run(() => FeedAsync(_cts.Token));

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        Signal();
    }

    /// <summary>
    /// Stops the session because the host reported playback stopped. Ignored (returns false) for a session that has
    /// not yet had a client or is younger than <paramref name="minAgeSeconds"/>: a stop signal for the previous play
    /// can arrive after the next play's session was created, and must not kill it.
    /// </summary>
    public bool TryStopForRelease(double minAgeSeconds)
    {
        if (!_hadClient || _age.Elapsed.TotalSeconds < minAgeSeconds) return false;
        _log?.Invoke("SXM continuous: stopping session (playback stopped by host).");
        _onStopped?.Invoke(this);
        Dispose();
        return true;
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
        Interlocked.Increment(ref _clients);
        _hadClient = true;
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
        finally
        {
            Interlocked.Decrement(ref _clients);
            Volatile.Write(ref _idleSinceTs, Stopwatch.GetTimestamp());
        }
        _log?.Invoke($"SXM continuous: client ended after {clock.Elapsed.TotalSeconds:F0}s, sent {sent} segments / {bytesSent} bytes ({reason}).");
    }

    // ── Feeder side ─────────────────────────────────────────────────────────────

    private async Task FeedAsync(CancellationToken ct)
    {
        _log?.Invoke($"SXM continuous: session started (delaySegments={_getDelay()}, pollMs={_pollMs}).");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (raw, baseUri) = await _getPlaylist();
                if (raw != null && baseUri != null)
                    await IngestAsync(Scan(raw), raw, baseUri, ct);
                else
                    _log?.Invoke("SXM continuous: playlist unavailable this poll.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log?.Invoke($"SXM continuous: feed error {ex.GetType().Name}: {ex.Message}"); }
            try { await Task.Delay(_pollMs, ct); }
            catch (OperationCanceledException) { break; }
            if (Volatile.Read(ref _clients) == 0 &&
                Stopwatch.GetElapsedTime(Volatile.Read(ref _idleSinceTs)) >= _idleStop)
            {
                _log?.Invoke($"SXM continuous: no client for {_idleStop.TotalSeconds:F0}s; stopping idle session.");
                _onStopped?.Invoke(this);
                Dispose();
                break;
            }
        }
        _log?.Invoke("SXM continuous: session stopped.");
    }

    // One pass over the raw playlist text without splitting or allocating per line: counts the segments and records
    // where the newest RingMax of them (plus their EXTINF / KEY / DISCONTINUITY context) sit.
    private static PlaylistScan Scan(string raw)
    {
        var scan = new PlaylistScan();
        var text = raw.AsSpan();
        int keyStart = -1, keyLen = 0, durStart = -1, durLen = 0, pos = 0;
        bool disc = false;
        while (pos < text.Length)
        {
            int nl = text[pos..].IndexOf('\n');
            int end = nl < 0 ? text.Length : pos + nl;
            int len = end - pos;
            while (len > 0 && text[pos + len - 1] == '\r') len--;
            if (len > 0)
            {
                var line = text.Slice(pos, len);
                if (line[0] == '#')
                {
                    if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                        long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), out scan.FirstSeq);
                    else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal)) { keyStart = pos; keyLen = len; }
                    else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal)) { durStart = pos + "#EXTINF:".Length; durLen = len - "#EXTINF:".Length; }
                    else if (line.Equals("#EXT-X-DISCONTINUITY", StringComparison.Ordinal)) disc = true;
                }
                else
                {
                    scan.Tail[scan.Total % RingMax] = new SegRef(pos, len, durStart, durLen, keyStart, keyLen, disc);
                    scan.Total++;
                    durStart = -1;
                    durLen = 0;
                    disc = false;
                }
            }
            pos = end + 1;
        }
        return scan;
    }

    // Builds SegInfo (absolute URL, duration, key, IV) only for the tail segments with Seq >= fromSeq; in steady
    // state that is zero or one segment per poll instead of the whole ~1,800-segment playlist.
    private static List<SegInfo> Materialize(PlaylistScan scan, string raw, Uri baseUri, long fromSeq)
    {
        var list = new List<SegInfo>();
        int firstIdx = Math.Max(Math.Max(0, scan.Total - RingMax), (int)Math.Clamp(fromSeq - scan.FirstSeq, 0L, (long)scan.Total));
        int cachedKeyStart = -2;
        string? keyGuid = null;
        byte[]? iv = null;
        for (int i = firstIdx; i < scan.Total; i++)
        {
            var r = scan.Tail[i % RingMax];
            if (r.KeyStart != cachedKeyStart)
            {
                cachedKeyStart = r.KeyStart;
                keyGuid = null;
                iv = null;
                if (r.KeyStart >= 0)
                {
                    var keyLine = raw.Substring(r.KeyStart, r.KeyLen);
                    var encrypted = keyLine.Contains("METHOD=AES-128", StringComparison.Ordinal);
                    var m = GuidRx.Matches(keyLine);
                    keyGuid = encrypted && m.Count > 0 ? m[^1].Value : null;
                    iv = ParseIv(keyLine);
                }
            }
            double dur = 0;
            if (r.DurStart >= 0)
            {
                var t = raw.AsSpan(r.DurStart, r.DurLen);
                int comma = t.IndexOf(',');
                if (comma >= 0) t = t[..comma];
                double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
            }
            var url = new Uri(baseUri, raw.Substring(r.UrlStart, r.UrlLen)).ToString();
            list.Add(new SegInfo(scan.FirstSeq + i, url, dur, keyGuid, iv, r.Disc));
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

    private async Task IngestAsync(PlaylistScan scan, string raw, Uri baseUri, CancellationToken ct)
    {
        if (scan.Total == 0) return;
        var newest = scan.Newest;
        var delay = _getDelay();

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

        var segs = Materialize(scan, raw, baseUri, from);
        if (!_loggedPlaylistInfo && segs.Count > 0)
        {
            _loggedPlaylistInfo = true;
            var last = segs[^1];
            _log?.Invoke($"SXM continuous: playlist segments={scan.Total} seq={scan.FirstSeq}..{newest} encrypted={last.KeyGuid != null} iv={(last.Iv != null ? "explicit" : "sequence")}.");
        }
        foreach (var s in segs)
        {
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

        var (stripped, id3Bytes, pts) = StripId3(plain);
        var st = Analyze(stripped, out var data);
        var declMs = s.Dur * 1000.0;
        var audioMs = st.AudioMs > 0 ? st.AudioMs : declMs;
        if (audioMs > 0) Volatile.Write(ref _segMs, audioMs);

        var flags = new List<string>();
        if (declMs > 0 && Math.Abs(st.AudioMs - declMs) > 5) flags.Add("durMismatch");
        if (st.MultiBlock > 0) flags.Add($"multiBlock={st.MultiBlock}");
        if (st.JunkBytes > 0) flags.Add($"junkBytes={st.JunkBytes}");
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
                        + (st.InlineId3Tags > 0 ? FormattableString.Invariant($" inlineId3Stripped={st.InlineId3Tags}/{st.InlineId3Bytes}B") : "")
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

    // Walks the ADTS frames of one (already leading-ID3-stripped) segment. When anything other than audio frames
    // sits between or after the frames (inline ID3 tags, stray bytes, a truncated final frame), 'clean' is a copy
    // holding only the frames, so LibVLC's AAC packetizer never has to resync mid-stream; otherwise 'clean' is the
    // input array itself (no copy in the common case).
    private static AdtsStats Analyze(byte[] d, out byte[] clean)
    {
        clean = d;
        int pos = 0, frames = 0, multi = 0, junk = 0, id3Tags = 0, id3Bytes = 0, firstPos = -1, sr = 0;
        int runStart = 0, lastEnd = 0;
        double samples = 0;
        List<(int Start, int Length)>? runs = null;
        while (pos + 7 <= d.Length)
        {
            int len = FrameLength(d, pos, out var srIdx, out var blocks);
            if (len > 0)
            {
                if (pos + len > d.Length) break; // truncated final frame: reported via Trailing
                if (firstPos < 0) firstPos = pos;
                if (blocks != 0) multi++;
                if (sr == 0) sr = SampleRates[srIdx];
                samples += 1024.0 * (blocks + 1);
                frames++;
                pos += len;
                lastEnd = pos;
                continue;
            }

            // Not a frame header: skip an inline ID3 tag exactly, otherwise scan on to the next confirmed frame.
            int junkStart = pos;
            int id3Len = InlineId3Length(d, pos);
            if (id3Len > 0) { pos += id3Len; id3Tags++; id3Bytes += id3Len; }
            else
            {
                pos++;
                while (pos + 7 <= d.Length && !ConfirmedFrameAt(d, pos)) pos++;
                junk += pos - junkStart;
            }
            runs ??= new();
            if (junkStart > runStart) runs.Add((runStart, junkStart - runStart));
            runStart = pos;
        }

        int trailing = d.Length - pos;
        if (frames > 0 && (runs != null || trailing > 0))
        {
            runs ??= new();
            if (lastEnd > runStart) runs.Add((runStart, lastEnd - runStart));
            int total = 0;
            foreach (var r in runs) total += r.Length;
            var buf = new byte[total];
            int o = 0;
            foreach (var r in runs) { Buffer.BlockCopy(d, r.Start, buf, o, r.Length); o += r.Length; }
            clean = buf;
        }
        var ms = sr > 0 ? samples * 1000.0 / sr : 0;
        return new AdtsStats(frames, sr, ms, Math.Max(firstPos, 0), multi, junk, id3Tags, id3Bytes, trailing);
    }

    // ADTS frame length at 'pos' (0 when there is no plausible header there).
    private static int FrameLength(byte[] d, int pos, out int srIdx, out int blocks)
    {
        srIdx = 0;
        blocks = 0;
        if (pos + 7 > d.Length || d[pos] != 0xFF || (d[pos + 1] & 0xF0) != 0xF0) return 0;
        srIdx = (d[pos + 2] >> 2) & 0x0F;
        blocks = d[pos + 6] & 0x03;
        int len = ((d[pos + 3] & 0x03) << 11) | (d[pos + 4] << 3) | (d[pos + 5] >> 5);
        return srIdx < SampleRates.Length && len >= 7 ? len : 0;
    }

    // A header found while scanning past junk must be followed by another header (or the end of the data), so
    // a 0xFFFx pair inside stray bytes is not mistaken for a frame.
    private static bool ConfirmedFrameAt(byte[] d, int pos)
    {
        int len = FrameLength(d, pos, out _, out _);
        if (len == 0) return false;
        int next = pos + len;
        if (next > d.Length) return false;
        return next + 7 > d.Length || FrameLength(d, next, out _, out _) > 0;
    }

    // Length of a complete ID3v2 tag starting at 'pos' (SiriusXM embeds metadata cue tags between frames), else 0.
    private static int InlineId3Length(byte[] d, int pos)
    {
        if (pos + 10 > d.Length || d[pos] != (byte)'I' || d[pos + 1] != (byte)'D' || d[pos + 2] != (byte)'3') return 0;
        if ((d[pos + 6] | d[pos + 7] | d[pos + 8] | d[pos + 9]) >= 0x80) return 0; // size bytes are syncsafe
        int size = (d[pos + 6] << 21) | (d[pos + 7] << 14) | (d[pos + 8] << 7) | d[pos + 9];
        int total = 10 + size + ((d[pos + 5] & 0x10) != 0 ? 10 : 0);
        return pos + total <= d.Length ? total : 0;
    }
}
