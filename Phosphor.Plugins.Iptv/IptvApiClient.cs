using System.Text.Json;
using System.Text.Json.Serialization;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Iptv;

/// <summary>
/// Fetches and joins the iptv-org public JSON API (https://iptv-org.github.io/api/) into a flat,
/// in-memory catalog of playable live channels. The catalog is built by joining:
/// <list type="bullet">
///   <item><c>streams.json</c> — the actual stream URLs (+ optional referrer / user-agent), keyed by channel id.</item>
///   <item><c>channels.json</c> — channel metadata (name, categories, country, is_nsfw).</item>
///   <item><c>logos.json</c> — channel artwork.</item>
///   <item><c>countries.json</c> / <c>categories.json</c> — display names (and country flag emoji).</item>
/// </list>
/// A stream with no matching channel record still becomes a catalog entry using its own <c>title</c>,
/// so the catalog is never smaller than the set of usable streams.
/// </summary>
internal sealed class IptvApiClient
{
    private const string ApiBase = "https://iptv-org.github.io/api/";

    private readonly HttpClient _http;
    private readonly Action<LogLevel, string> _log;

    public IptvApiClient(HttpClient http, Action<LogLevel, string> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Downloads all the API endpoints and joins them into the catalog. Streams without a URL, and
    /// (optionally) NSFW channels, are excluded.
    /// </summary>
    public async Task<IptvCatalog> BuildCatalogAsync(bool includeNsfw, CancellationToken ct)
    {
        var streamsTask = GetJsonAsync<List<StreamDto>>("streams.json", ct);
        var channelsTask = GetJsonAsync<List<ChannelDto>>("channels.json", ct);
        var logosTask = GetJsonAsync<List<LogoDto>>("logos.json", ct);
        var countriesTask = GetJsonAsync<List<CountryDto>>("countries.json", ct);
        var categoriesTask = GetJsonAsync<List<CategoryDto>>("categories.json", ct);

        await Task.WhenAll(streamsTask, channelsTask, logosTask, countriesTask, categoriesTask)
            .ConfigureAwait(false);

        var streams = streamsTask.Result ?? [];
        var channels = (channelsTask.Result ?? []).ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        var logos = logosTask.Result ?? [];
        var countries = (countriesTask.Result ?? []).ToDictionary(c => c.Code, c => c, StringComparer.OrdinalIgnoreCase);
        var categories = (categoriesTask.Result ?? []).ToDictionary(c => c.Id, c => c, StringComparer.OrdinalIgnoreCase);

        // First logo per channel (in_use preferred) — the API lists many variants per channel.
        var logoByChannel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var logo in logos)
        {
            if (string.IsNullOrEmpty(logo.Channel) || string.IsNullOrEmpty(logo.Url)) continue;
            if (logo.InUse || !logoByChannel.ContainsKey(logo.Channel))
                logoByChannel[logo.Channel] = logo.Url!;
        }

        var entries = new List<IptvChannel>(streams.Count);
        foreach (var s in streams)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(s.Url)) continue;

            ChannelDto? channel = null;
            if (!string.IsNullOrEmpty(s.Channel))
                channels.TryGetValue(s.Channel!, out channel);

            if (channel is { IsNsfw: true } && !includeNsfw) continue;

            var id = !string.IsNullOrEmpty(s.Channel)
                ? s.Channel!
                : (s.Title ?? s.Url!);

            var title = channel?.Name ?? s.Title ?? id;

            string? thumb = null;
            if (!string.IsNullOrEmpty(s.Channel))
                logoByChannel.TryGetValue(s.Channel!, out thumb);

            var countryCode = channel?.Country;
            var countryName = "Unknown";
            string? flag = null;
            if (!string.IsNullOrEmpty(countryCode) && countries.TryGetValue(countryCode!, out var cc))
            {
                countryName = cc.Name;
                flag = cc.Flag;
            }

            var cats = (channel?.Categories ?? [])
                .Select(cid => categories.TryGetValue(cid, out var cat) ? cat.Name : cid)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cats.Count == 0) cats.Add("Uncategorized");

            entries.Add(new IptvChannel(
                Id: id,
                Title: title,
                Url: s.Url!,
                ThumbnailUrl: thumb,
                CountryCode: countryCode,
                CountryName: countryName,
                CountryFlag: flag,
                Categories: cats,
                Quality: s.Quality,
                Referrer: s.Referrer,
                UserAgent: s.UserAgent,
                IsNsfw: channel?.IsNsfw ?? false));
        }

        // Collapse duplicate ids (multiple stream URLs per channel) — keep the first (best) one.
        var deduped = entries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log(LogLevel.Info, $"IPTV: built catalog of {deduped.Count} channels from {streams.Count} streams.");
        return new IptvCatalog(deduped);
    }

    private async Task<T?> GetJsonAsync<T>(string endpoint, CancellationToken ct)
    {
        var url = ApiBase + endpoint;
        try
        {
            await using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(LogLevel.Warning, $"IPTV: failed to fetch {endpoint}: {ex.Message}");
            return default;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // ── API DTOs (only the fields we use) ────────────────────────────────────────

    private sealed class StreamDto
    {
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("referrer")] public string? Referrer { get; set; }
        [JsonPropertyName("user_agent")] public string? UserAgent { get; set; }
        [JsonPropertyName("quality")] public string? Quality { get; set; }
    }

    private sealed class ChannelDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
        [JsonPropertyName("is_nsfw")] public bool IsNsfw { get; set; }
    }

    private sealed class LogoDto
    {
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("in_use")] public bool InUse { get; set; }
    }

    private sealed class CountryDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("flag")] public string? Flag { get; set; }
    }

    private sealed class CategoryDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }
}

/// <summary>One playable live channel in the joined catalog.</summary>
internal sealed record IptvChannel(
    string Id,
    string Title,
    string Url,
    string? ThumbnailUrl,
    string? CountryCode,
    string CountryName,
    string? CountryFlag,
    IReadOnlyList<string> Categories,
    string? Quality,
    string? Referrer,
    string? UserAgent,
    bool IsNsfw);

/// <summary>The whole joined catalog plus lazily-materialized browse groupings.</summary>
internal sealed class IptvCatalog
{
    public IptvCatalog(IReadOnlyList<IptvChannel> channels)
    {
        Channels = channels;
        ById = channels.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
    }

    public IReadOnlyList<IptvChannel> Channels { get; }
    public IReadOnlyDictionary<string, IptvChannel> ById { get; }

    /// <summary>Country name → channels, ordered by country name.</summary>
    public IEnumerable<IGrouping<string, IptvChannel>> ByCountry() =>
        Channels.GroupBy(c => c.CountryName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Category name → channels (a channel may appear under several), ordered by category name.</summary>
    public IEnumerable<IGrouping<string, IptvChannel>> ByCategory() =>
        Channels.SelectMany(c => c.Categories.Select(cat => (cat, c)))
                .GroupBy(x => x.cat, x => x.c, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
}
