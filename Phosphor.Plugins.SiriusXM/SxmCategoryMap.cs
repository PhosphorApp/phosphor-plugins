using System.Text.Json;

namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// Maps SiriusXM category keys into top-level super-groups (Music / Talk / Sports), mirroring the
/// grouping on SiriusXM's own apps. Seeded from a bundled <c>categories.json</c> next to the plug-in
/// DLL, but a user copy in the instance cache directory (same file name) overrides it — so end users
/// can re-bucket categories without a code change. Category keys not listed fall into "Other".
/// </summary>
/// <remarks>
/// Keying on the category <em>key</em> (e.g. "rock", "nflplay") rather than its display name means the
/// map survives SiriusXM renaming a category's visible text.
/// </remarks>
public sealed class SxmCategoryMap
{
    public const string SuperMusic = "Music";
    public const string SuperTalk = "Talk";
    public const string SuperSports = "Sports";
    public const string SuperOther = "Other";

    private const string MapFileName = "categories.json";

    // Fallback seed (used if no categories.json is found), from the real 37-category taxonomy.
    private static readonly Dictionary<string, string[]> Seed = new()
    {
        [SuperMusic] =
        [
            "rock", "pop", "country", "hiphop", "world", "canadianmusic", "discovery", "dance",
            "jazz", "chill", "00s", "70s", "90s", "hits", "50s60s", "80s", "10s", "global",
            "christian", "party", "workout",
        ],
        [SuperTalk] =
        [
            "entertainment", "publicradio", "comedy", "moretalk", "political", "canadiantalk",
            "religion", "howardstern", "kids",
        ],
        [SuperSports] =
        [
            "sportsplay", "nflplay", "mlbpbp", "NHL_PBP", "NBA_PBP", "sportstalk", "college",
        ],
    };

    // category key -> super-group name
    private readonly Dictionary<string, string> _keyToSuper = new(StringComparer.OrdinalIgnoreCase);

    private SxmCategoryMap(Dictionary<string, string[]> groups)
    {
        foreach (var (super, keys) in groups)
            foreach (var key in keys)
                _keyToSuper[key] = super;
    }

    /// <summary>Loads the map, preferring a user override in <paramref name="userDir"/>, then the
    /// bundled file next to the plug-in, then the built-in seed.</summary>
    public static SxmCategoryMap Load(string? userDir, Action<string>? log = null)
    {
        // 1) user override in the instance cache dir
        var groups = TryLoadFile(userDir, log) ?? TryLoadFile(AppContext.BaseDirectory, log);
        if (groups != null) return new SxmCategoryMap(groups);
        return new SxmCategoryMap(Seed);
    }

    private static Dictionary<string, string[]>? TryLoadFile(string? dir, Action<string>? log)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            var path = Path.Combine(dir, MapFileName);
            if (!File.Exists(path)) return null;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
            if (parsed is { Count: > 0 })
            {
                log?.Invoke($"SXM: loaded category map from {path}");
                return parsed;
            }
        }
        catch (Exception ex) { log?.Invoke($"SXM: category map read failed: {ex.Message}"); }
        return null;
    }

    /// <summary>The super-group for a category key, or "Other" if unmapped.</summary>
    public string SuperGroupFor(string categoryKey) =>
        _keyToSuper.TryGetValue(categoryKey, out var s) ? s : SuperOther;

    /// <summary>The super-groups to show at the root, in display order, limited to those that
    /// actually contain at least one of the given category keys (plus Other if any unmapped).</summary>
    public IReadOnlyList<string> SuperGroupsPresent(IEnumerable<string> presentCategoryKeys)
    {
        var supers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in presentCategoryKeys)
            supers.Add(SuperGroupFor(key));
        var ordered = new[] { SuperMusic, SuperTalk, SuperSports, SuperOther };
        return ordered.Where(supers.Contains).ToList();
    }
}
