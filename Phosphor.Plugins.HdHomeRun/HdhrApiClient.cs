using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.HdHomeRun;

/// <summary>
/// Talks to a single SiliconDust HDHomeRun network tuner over its local HTTP API and joins the
/// results into a flat, in-memory <see cref="HdhrCatalog"/> of playable live channels. The tuner
/// exposes two endpoints we need for the basics:
/// <list type="bullet">
///   <item><c>/discover.json</c> — device specifics (model, id, firmware, tuner count, base URL,
///   and — Phase 2 — the <c>DeviceAuth</c> token used by the SiliconDust guide API).</item>
///   <item><c>/lineup.json</c> — the scanned channel lineup (number, name, stream URL, HD flag).</item>
/// </list>
/// Phase 1 only reads these two local endpoints. Phase 2 will additionally pull channel icons and
/// guide data from the SiliconDust cloud API using the <see cref="HdhrDevice.DeviceAuth"/> token
/// surfaced here (see <see cref="HdhrGuideClient"/>).
/// </summary>
internal sealed class HdhrApiClient
{
    private readonly HttpClient _http;
    private readonly Action<LogLevel, string> _log;

    public HdhrApiClient(HttpClient http, Action<LogLevel, string> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Fetches <c>/discover.json</c> from the tuner at <paramref name="baseUrl"/> (e.g.
    /// "http://192.168.14.31"). Returns the device details, or <c>null</c> when the tuner is
    /// unreachable / does not respond with valid JSON.
    /// </summary>
    public async Task<HdhrDevice?> DiscoverAsync(string baseUrl, CancellationToken ct)
    {
        var url = Combine(baseUrl, "discover.json");
        try
        {
            await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<DiscoverDto>(stream, JsonOpts, ct).ConfigureAwait(false);
            if (dto is null)
            {
                _log(LogLevel.Warning, $"HDHomeRun: {url} returned no device data.");
                return null;
            }

            _log(LogLevel.Info,
                $"HDHomeRun: discovered '{dto.FriendlyName}' ({dto.ModelNumber}, id {dto.DeviceID}, {dto.TunerCount} tuners).");

            return new HdhrDevice(
                DeviceId: dto.DeviceID,
                FriendlyName: string.IsNullOrWhiteSpace(dto.FriendlyName) ? "HDHomeRun" : dto.FriendlyName!,
                ModelNumber: dto.ModelNumber,
                FirmwareVersion: dto.FirmwareVersion,
                TunerCount: dto.TunerCount,
                DeviceAuth: dto.DeviceAuth,
                // Prefer the BaseURL the device reports (authoritative), fall back to what we were given.
                BaseUrl: string.IsNullOrWhiteSpace(dto.BaseURL) ? Normalize(baseUrl) : dto.BaseURL!,
                LineupUrl: dto.LineupURL);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(LogLevel.Warning, $"HDHomeRun: discover failed for {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches the channel lineup and joins it with the <paramref name="device"/> details into a
    /// catalog of playable channels. Uses the device's reported <c>LineupURL</c> when present,
    /// otherwise <c>{BaseUrl}/lineup.json</c>.
    /// </summary>
    public async Task<HdhrCatalog> BuildCatalogAsync(HdhrDevice device, CancellationToken ct)
    {
        var url = !string.IsNullOrWhiteSpace(device.LineupUrl)
            ? device.LineupUrl!
            : Combine(device.BaseUrl, "lineup.json");

        List<LineupDto> lineup;
        try
        {
            await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            lineup = await JsonSerializer.DeserializeAsync<List<LineupDto>>(stream, JsonOpts, ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(LogLevel.Warning, $"HDHomeRun: lineup fetch failed for {url}: {ex.Message}");
            lineup = [];
        }

        var channels = new List<HdhrChannel>(lineup.Count);
        foreach (var c in lineup)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(c.URL) || string.IsNullOrWhiteSpace(c.GuideNumber)) continue;

            channels.Add(new HdhrChannel(
                // The guide number (e.g. "5.1") is stable and unique per lineup — a good id.
                Id: c.GuideNumber!,
                GuideNumber: c.GuideNumber!,
                Name: string.IsNullOrWhiteSpace(c.GuideName) ? c.GuideNumber! : c.GuideName!,
                Url: c.URL!,
                IsHd: c.HD == 1,
                IsDrm: c.DRM == 1,
                IsFavorite: c.Favorite == 1,
                // Phase 2 fills this from the SiliconDust guide API; the lineup has no icon.
                ThumbnailUrl: null));
        }

        var ordered = channels
            .OrderBy(c => ParseGuideNumber(c.GuideNumber))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log(LogLevel.Info, $"HDHomeRun: built catalog of {ordered.Count} channels from {url}.");
        return new HdhrCatalog(device, ordered);
    }

    // Guide numbers look like "5.1", "12.3", "704" — sort them numerically (major, then minor).
    private static (int Major, int Minor) ParseGuideNumber(string guideNumber)
    {
        var parts = guideNumber.Split('.', 2);
        int.TryParse(parts[0], out var major);
        var minor = 0;
        if (parts.Length > 1) int.TryParse(parts[1], out minor);
        return (major, minor);
    }

    private static string Combine(string baseUrl, string endpoint)
        => Normalize(baseUrl) + "/" + endpoint;

    /// <summary>Trims trailing slashes and prepends "http://" when the user omitted a scheme.</summary>
    private static string Normalize(string baseUrl)
    {
        var s = baseUrl.Trim().TrimEnd('/');
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s;
        return s;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // ── API DTOs (only the fields we use) ────────────────────────────────────────

    private sealed class DiscoverDto
    {
        [JsonPropertyName("FriendlyName")] public string? FriendlyName { get; set; }
        [JsonPropertyName("ModelNumber")] public string? ModelNumber { get; set; }
        [JsonPropertyName("FirmwareName")] public string? FirmwareName { get; set; }
        [JsonPropertyName("FirmwareVersion")] public string? FirmwareVersion { get; set; }
        [JsonPropertyName("DeviceID")] public string? DeviceID { get; set; }
        [JsonPropertyName("DeviceAuth")] public string? DeviceAuth { get; set; }
        [JsonPropertyName("BaseURL")] public string? BaseURL { get; set; }
        [JsonPropertyName("LineupURL")] public string? LineupURL { get; set; }
        [JsonPropertyName("TunerCount")] public int TunerCount { get; set; }
    }

    private sealed class LineupDto
    {
        [JsonPropertyName("GuideNumber")] public string? GuideNumber { get; set; }
        [JsonPropertyName("GuideName")] public string? GuideName { get; set; }
        [JsonPropertyName("URL")] public string? URL { get; set; }
        [JsonPropertyName("HD")] public int HD { get; set; }
        [JsonPropertyName("DRM")] public int DRM { get; set; }
        [JsonPropertyName("Favorite")] public int Favorite { get; set; }
    }
}

/// <summary>Device specifics read from <c>/discover.json</c>.</summary>
internal sealed record HdhrDevice(
    string? DeviceId,
    string FriendlyName,
    string? ModelNumber,
    string? FirmwareVersion,
    int TunerCount,
    string? DeviceAuth,
    string BaseUrl,
    string? LineupUrl);

/// <summary>One playable live channel from the tuner lineup.</summary>
internal sealed record HdhrChannel(
    string Id,
    string GuideNumber,
    string Name,
    string Url,
    bool IsHd,
    bool IsDrm,
    bool IsFavorite,
    string? ThumbnailUrl);

/// <summary>The joined catalog: the device plus its channels, indexed by id.</summary>
internal sealed class HdhrCatalog
{
    public HdhrCatalog(HdhrDevice device, IReadOnlyList<HdhrChannel> channels)
    {
        Device = device;
        Channels = channels;
        ById = channels.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
    }

    public HdhrDevice Device { get; }
    public IReadOnlyList<HdhrChannel> Channels { get; }
    public IReadOnlyDictionary<string, HdhrChannel> ById { get; }
}
