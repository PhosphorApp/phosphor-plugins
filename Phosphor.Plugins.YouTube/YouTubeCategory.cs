using System.Text.Json;

namespace Phosphor.Plugins.YouTube;

/// <summary>
/// A user-defined YouTube category ("tile"): a display <see cref="Name"/>, a recommended
/// <see cref="Icon"/> glyph, and the <see cref="SearchTerm"/> executed when the tile is opened
/// (e.g. <c>playlist:modern rock</c>). YouTube was the original baked-in provider, so these were
/// historically host-owned genre entries; they now belong to the plug-in. The list is fully
/// user-generated (add/edit/delete), seeded on first run from the bundled
/// <c>default-categories.json</c> template next to the plug-in DLL.
/// </summary>
public sealed class YouTubeCategory
{
    /// <summary>Stable id for this category, used to key the host's per-source tile.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name of the tile (e.g. "Rock").</summary>
    public string Name { get; set; } = "";

    /// <summary>Recommended glyph for the tile. The host persists it and lets the user override it.</summary>
    public string Icon { get; set; } = "";

    /// <summary>The search term run when the tile is opened (e.g. <c>playlist:modern rock</c>).</summary>
    public string SearchTerm { get; set; } = "";

    /// <summary>Relative ordering hint within the plug-in's own list (host ordering still wins).</summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// Loads and persists the user's YouTube category list. The authoritative <em>initial</em> state is
/// defined by the plug-in (the bundled <c>default-categories.json</c> template shipped next to the
/// DLL); the live/customized state is owned by whoever holds the persisted settings blob (the host,
/// via the YouTube instance settings). This keeps the boundary honest: the plug-in defines what the
/// default YouTube categories are; the host arranges/overrides them.
/// </summary>
public static class YouTubeCategoryStore
{
    private const string DefaultsFileName = "default-categories.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Built-in fallback seed, used only if the bundled <c>default-categories.json</c> cannot be
    /// found or read. Mirrors the template file so the plug-in still ships sensible defaults even
    /// from a stripped deployment.
    /// </summary>
    private static readonly YouTubeCategory[] Seed =
    [
        new() { Name = "Top Hits", Icon = "🎶", SearchTerm = "playlist:top hits", SortOrder = 0 },
        new() { Name = "Rock", Icon = "🎸", SearchTerm = "playlist:modern rock", SortOrder = 1 },
        new() { Name = "Pop", Icon = "🎤", SearchTerm = "playlist:pop hits", SortOrder = 2 },
        new() { Name = "Hip Hop", Icon = "🎧", SearchTerm = "playlist:hip hop hits", SortOrder = 3 },
        new() { Name = "Country", Icon = "🤠", SearchTerm = "playlist:country music videos", SortOrder = 4 },
        new() { Name = "Metal", Icon = "🤘", SearchTerm = "playlist:heavy metal music videos", SortOrder = 5 },
        new() { Name = "Electronic", Icon = "🎹", SearchTerm = "playlist:EDM music videos", SortOrder = 6 },
        new() { Name = "Concerts", Icon = "🎪", SearchTerm = "full concert live performances min:30m", SortOrder = 7 },
        new() { Name = "'80s", Icon = "📼", SearchTerm = "playlist:music hits from the 80s", SortOrder = 8 },
        new() { Name = "'90s", Icon = "💿", SearchTerm = "playlist:music hits from the 90s", SortOrder = 9 },
        new() { Name = "2000s", Icon = "🔥", SearchTerm = "playlist:music hits from the 2000s", SortOrder = 10 },
        new() { Name = "Classic Rock", Icon = "🎵", SearchTerm = "playlist:classic rock hits", SortOrder = 11 },
        new() { Name = "Jazz", Icon = "🎺", SearchTerm = "playlist:jazz music videos", SortOrder = 12 },
        new() { Name = "Reggae", Icon = "🌴", SearchTerm = "playlist:reggae music videos", SortOrder = 13 },
        new() { Name = "Punk", Icon = "⚡", SearchTerm = "playlist:punk rock music videos", SortOrder = 14 },
        new() { Name = "Ambience", Icon = "🕯️", SearchTerm = "relaxing ambience fireplace cozy holiday background video", SortOrder = 15 },
        new() { Name = "Tutorials", Icon = "🕹️", SearchTerm = "pinball tutorial how to play", SortOrder = 16 },
        new() { Name = "Table Guides", Icon = "📖", SearchTerm = "kongedam pinball tutorials", SortOrder = 17 },
    ];

    /// <summary>
    /// Returns the plug-in's default category list: the bundled <c>default-categories.json</c> next
    /// to the plug-in DLL if present and valid, otherwise the built-in <see cref="Seed"/>. Every
    /// returned category is guaranteed a non-empty <see cref="YouTubeCategory.Id"/>. Used to seed a
    /// fresh instance and to power a future "restore defaults" action.
    /// </summary>
    public static List<YouTubeCategory> LoadDefaults(Action<string>? log = null)
    {
        var fromFile = TryLoadDefaultsFile(log);
        var list = fromFile ?? Seed.Select(Clone).ToList();
        foreach (var c in list)
            if (string.IsNullOrEmpty(c.Id))
                c.Id = Guid.NewGuid().ToString("N");
        return list;
    }

    /// <summary>Deserializes the user's category list from its persisted JSON blob.</summary>
    public static List<YouTubeCategory> Deserialize(string? json, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<YouTubeCategory>>(json, JsonOptions);
            if (list == null) return [];
            foreach (var c in list)
                if (string.IsNullOrEmpty(c.Id))
                    c.Id = Guid.NewGuid().ToString("N");
            return list;
        }
        catch (Exception ex)
        {
            log?.Invoke($"YouTube: category list parse failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Serializes a category list to its persisted JSON blob shape.</summary>
    public static string Serialize(IEnumerable<YouTubeCategory> categories) =>
        JsonSerializer.Serialize(categories, JsonOptions);

    private static List<YouTubeCategory>? TryLoadDefaultsFile(Action<string>? log)
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(YouTubeCategoryStore).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return null;
            var path = Path.Combine(dir, DefaultsFileName);
            if (!File.Exists(path)) return null;
            var parsed = JsonSerializer.Deserialize<List<YouTubeCategory>>(File.ReadAllText(path), JsonOptions);
            if (parsed is { Count: > 0 })
            {
                log?.Invoke($"YouTube: loaded default categories from {path}");
                return parsed;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"YouTube: default categories read failed: {ex.Message}");
        }
        return null;
    }

    private static YouTubeCategory Clone(YouTubeCategory c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Icon = c.Icon,
        SearchTerm = c.SearchTerm,
        SortOrder = c.SortOrder,
    };
}
