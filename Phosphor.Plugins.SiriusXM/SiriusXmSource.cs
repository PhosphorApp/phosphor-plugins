using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// A configured SiriusXM source instance. Authenticates a subscriber, browses the live channel
/// lineup, and resolves a channel to a locally-proxied HLS URL the host plays via LibVLC.
/// </summary>
/// <remarks>
/// Channels are <b>live audio streams</b>: every produced item is <see cref="SourceItem.IsAudioOnly"/>
/// and <see cref="SourceItem.IsLiveStream"/>, and the resolved stream is
/// <see cref="StreamLayout.AudioOnly"/> + <see cref="ResolvedStream.IsLiveStream"/> so the host
/// suppresses seek/duration and never auto-advances.
/// </remarks>
public sealed class SiriusXmSource :
    IPhosphorSource, IBrowsable, IPlayableResolver, IConnectionTestable
{
    // Fixed local port for the proxy. High/uncommon to avoid clashes for the prototype.
    private const int ProxyPort = 8912;

    private readonly object _gate = new();
    private IPluginHost? _host;
    private string _username = "";
    private string _password = "";
    private string _region = SiriusXmSourceProvider.RegionUs;

    private SxmClient? _client;
    private SxmProxy? _proxy;
    private IReadOnlyList<SxmChannel>? _channels;

    public SiriusXmSource(string instanceId, IReadOnlyDictionary<string, string?> settings)
    {
        InstanceId = instanceId;
        ApplySettingsInternal(settings);
    }

    public string InstanceId { get; }
    public string TypeId => SiriusXmSourceProvider.SiriusXmTypeId;
    public string DisplayName { get; set; } = "SiriusXM";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password);
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(IPluginHost host, CancellationToken ct = default)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public void ApplySettings(IReadOnlyDictionary<string, string?> values) => ApplySettingsInternal(values);

    private void ApplySettingsInternal(IReadOnlyDictionary<string, string?> values)
    {
        _username = Get(values, SiriusXmSourceProvider.KeyUsername) ?? "";
        _password = Get(values, SiriusXmSourceProvider.KeyPassword) ?? "";
        _region = Get(values, SiriusXmSourceProvider.KeyRegion) is { Length: > 0 } r ? r : SiriusXmSourceProvider.RegionUs;

        // Credentials changed — drop any live client/lineup so the next use re-authenticates.
        lock (_gate)
        {
            _client = null;
            _channels = null;
        }
    }

    // ── IConnectionTestable ─────────────────────────────────────────────────────

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ConnectionTestResult(false, "Enter a username and password first.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var client = new SxmClient(_username, _password, _region, Log);
            if (!await client.AuthenticateAsync(ct))
                return new ConnectionTestResult(false, "Login failed — check username/password.", sw.Elapsed);
            var channels = await client.GetChannelsAsync(ct);
            return new ConnectionTestResult(true, $"Connected — {channels.Count} channels.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    // ── IBrowsable (flat channel lineup) ────────────────────────────────────────

    public async IAsyncEnumerable<SourceCategory> GetRootCategoriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Flat first cut: a single "All Channels" category. Grouping/hiding comes later.
        await Task.CompletedTask;
        yield return new SourceCategory
        {
            SourceInstanceId = InstanceId,
            CategoryId = "all",
            Title = "All Channels",
            Icon = "📻",
            HasSubCategories = false,
        };
    }

    public async Task<BrowseResult> BrowseAsync(SourceCategory category, CancellationToken ct = default)
    {
        var channels = await EnsureChannelsAsync(ct);
        var items = channels
            .OrderBy(c => c.SortNumber)
            .Select(ToSourceItem)
            .ToList();
        return new BrowseResult { Items = items };
    }

    private SourceItem ToSourceItem(SxmChannel c) => new()
    {
        SourceInstanceId = InstanceId,
        ItemId = c.Id,
        Title = string.IsNullOrEmpty(c.Number) ? c.Name : $"{c.Number} · {c.Name}",
        Subtitle = "SiriusXM",
        ThumbnailUrl = c.ThumbnailUrl,
        IsAudioOnly = true,
        IsLiveStream = true,
        // Carry the channel so ResolveAsync needs no re-fetch.
        SourceState = c,
    };

    // ── IPlayableResolver ───────────────────────────────────────────────────────

    public async Task<ResolvedStream?> ResolveAsync(
        SourceItem item, PlaybackPreferences prefs, CancellationToken ct = default)
    {
        var channel = item.SourceState as SxmChannel
            ?? (await EnsureChannelsAsync(ct)).FirstOrDefault(c => c.Id == item.ItemId);
        if (channel == null) { Log($"SXM: channel '{item.ItemId}' not found."); return null; }

        var client = await EnsureClientAsync(ct);
        if (client == null) return null;

        var proxy = EnsureProxy(client);
        var localUrl = await proxy.SetChannelAsync(channel, ct);
        if (localUrl == null) { Log($"SXM: failed to resolve stream for '{channel.Id}'."); return null; }

        return new ResolvedStream(
            StreamTransport.Http,
            StreamLayout.AudioOnly,
            localUrl)
        {
            IsLiveStream = true,
        };
    }

    public Task<SourceMetadata?> GetMetadataAsync(SourceItem item, CancellationToken ct = default)
        // Live radio has no fixed duration/chapters — nothing to enrich.
        => Task.FromResult<SourceMetadata?>(null);

    // ── Internals ───────────────────────────────────────────────────────────────

    private async Task<SxmClient?> EnsureClientAsync(CancellationToken ct)
    {
        SxmClient? client;
        lock (_gate) client = _client;
        if (client is { IsAuthenticated: true }) return client;

        if (!IsConfigured) return null;
        client = new SxmClient(_username, _password, _region, Log);
        if (!await client.AuthenticateAsync(ct)) { Log("SXM: authentication failed."); return null; }
        lock (_gate) _client = client;
        return client;
    }

    private async Task<IReadOnlyList<SxmChannel>> EnsureChannelsAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channels != null) return _channels; }

        // Try the on-disk lineup cache first (the lineup seldom changes). Fresh cache avoids the
        // authenticated fetch entirely, making browse instant and offline-tolerant.
        var cached = LoadLineupCache();
        if (cached != null)
        {
            lock (_gate) _channels = cached;
            return cached;
        }

        var client = await EnsureClientAsync(ct);
        if (client == null) return [];
        var channels = await client.GetChannelsAsync(ct);
        if (channels.Count > 0)
        {
            lock (_gate) _channels = channels;
            SaveLineupCache(channels);
        }
        return channels;
    }

    // ── Lineup cache (per-instance, timestamped) ────────────────────────────────

    private const int LineupCacheDays = 7;
    private string LineupCachePath =>
        Path.Combine(_host?.InstanceCacheDirectory ?? Path.GetTempPath(), "lineup.json");

    private IReadOnlyList<SxmChannel>? LoadLineupCache()
    {
        try
        {
            var path = LineupCachePath;
            if (!File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(LineupCacheDays))
                return null;
            var cache = JsonSerializer.Deserialize<LineupCache>(File.ReadAllText(path));
            return cache?.Channels is { Count: > 0 } ? cache.Channels : null;
        }
        catch (Exception ex) { Log($"SXM: lineup cache read failed: {ex.Message}"); return null; }
    }

    private void SaveLineupCache(IReadOnlyList<SxmChannel> channels)
    {
        try
        {
            var path = LineupCachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new LineupCache(DateTimeOffset.UtcNow, channels)));
        }
        catch (Exception ex) { Log($"SXM: lineup cache write failed: {ex.Message}"); }
    }

    private sealed record LineupCache(DateTimeOffset FetchedUtc, IReadOnlyList<SxmChannel> Channels);

    private SxmProxy EnsureProxy(SxmClient client)
    {
        lock (_gate)
        {
            if (_proxy is { IsRunning: true }) return _proxy;
            _proxy = new SxmProxy(client, ProxyPort, Log);
            _proxy.Start();
            return _proxy;
        }
    }

    private void Log(string message) => _host?.Log(message);

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;
}
