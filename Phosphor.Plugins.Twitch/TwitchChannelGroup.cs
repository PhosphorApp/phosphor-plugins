using System.Text.Json;
using Phosphor.Plugin.Abstractions;

namespace Phosphor.Plugins.Twitch;

/// <summary>
/// A user-defined Twitch channel group ("Pinball", "Concerts", …): a display <see cref="Name"/>, an
/// optional <see cref="Icon"/> glyph, and the list of channel <see cref="Channels"/> (logins)
/// surfaced when the group node is opened inside the Twitch tile. Groups are edited directly in the
/// plug-in's own settings as an add/remove list of rows (one row per group), so no root-tile editor
/// or host-side category contract is involved.
/// </summary>
public sealed record TwitchChannelGroup(string Name, IReadOnlyList<string> Channels, string? Icon = null);

/// <summary>
/// Parses/formats the plug-in's channel-group settings rows. Each row is one group in the form
/// <c>[glyph] Name = login1, login2, login3</c> — an optional leading emoji/glyph, then the name
/// before the first <c>=</c>, then a comma/space/newline-delimited list of channel logins. A row with
/// no <c>=</c> is treated as an unnamed group whose whole text is the login list (its name defaults to
/// the first login). The host renders the rows via a <c>Text</c> + <c>AllowMultiple</c> setting
/// (newline-separated), so this only deals with a single row's shape.
/// </summary>
public static class TwitchChannelGroups
{
    /// <summary>Default glyph used for a group whose row omits a leading icon (a "pinball" white circle).</summary>
    public static readonly string DefaultIcon = char.ConvertFromUtf32(0x26AA);

    /// <summary>Bundled template defining the authoritative default channel groups, shipped next to the DLL.</summary>
    private const string DefaultsFileName = "default_channel_groups.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Built-in fallback seed, used only if the bundled <c>default_channel_groups.json</c> cannot be
    /// found or read. Mirrors the template file so the plug-in still ships sensible defaults even
    /// from a stripped deployment.
    /// </summary>
    private static readonly IReadOnlyList<TwitchChannelGroup> Seed =
    [
        new("Pinball",
        [
            "deadflip",
            "buffalopinball",
            "sdtmpinball",
            "foxcitiespinball",
            "mpt3k",
        ], DefaultIcon),
    ];

    /// <summary>
    /// The default groups shipped on first run: the bundled <c>default_channel_groups.json</c> next
    /// to the plug-in DLL if present and valid, otherwise the built-in <see cref="Seed"/>. So the
    /// defaults can be managed (rename/add/remove) by editing the template file, no recompile.
    /// </summary>
    public static IReadOnlyList<TwitchChannelGroup> LoadDefaults(Action<LogLevel, string>? log = null)
        => TryLoadDefaultsFile(log) ?? Seed;

    /// <summary>The default rows, in the settings-string form, used to seed the settings default value.</summary>
    public static string DefaultRows => string.Join('\n', LoadDefaults().Select(FormatRow));

    /// <summary>DTO mirroring the bundled <c>default_channel_groups.json</c> shape.</summary>
    private sealed record DefaultGroupDto(string? Name, string? Icon, List<string>? Channels, int SortOrder = 0);

    private static IReadOnlyList<TwitchChannelGroup>? TryLoadDefaultsFile(Action<LogLevel, string>? log)
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(TwitchChannelGroups).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) return null;
            var path = Path.Combine(dir, DefaultsFileName);
            if (!File.Exists(path)) return null;

            var parsed = JsonSerializer.Deserialize<List<DefaultGroupDto>>(File.ReadAllText(path), JsonOptions);
            if (parsed is not { Count: > 0 }) return null;

            var groups = parsed
                .OrderBy(g => g.SortOrder)
                .Select(g => new TwitchChannelGroup(
                    string.IsNullOrWhiteSpace(g.Name) ? (g.Channels?.FirstOrDefault() ?? "") : g.Name!,
                    (g.Channels ?? []).Select(NormalizeLogin).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    string.IsNullOrEmpty(g.Icon) ? DefaultIcon : g.Icon))
                .Where(g => g.Channels.Count > 0)
                .ToList();

            if (groups.Count == 0) return null;
            log?.Invoke(LogLevel.Debug, $"Twitch: loaded default channel groups from {path}");
            return groups;
        }
        catch (Exception ex)
        {
            log?.Invoke(LogLevel.Warning, $"Twitch: default channel groups read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses the multi-row settings value (newline-separated) into groups, dropping empties.</summary>
    public static List<TwitchChannelGroup> Parse(string? rows)
    {
        var result = new List<TwitchChannelGroup>();
        if (string.IsNullOrWhiteSpace(rows)) return result;

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in rows.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var group = ParseRow(line);
            if (group is null || group.Channels.Count == 0) continue;
            // De-duplicate by name so two rows named the same don't produce ambiguous node ids.
            if (!seenNames.Add(group.Name)) continue;
            result.Add(group);
        }
        return result;
    }

    /// <summary>Parses a single <c>[glyph] Name = login1, login2</c> row into a group (or null when empty).</summary>
    public static TwitchChannelGroup? ParseRow(string? row)
    {
        if (string.IsNullOrWhiteSpace(row)) return null;

        // Optional leading glyph: a single non-alphanumeric grapheme before the name (e.g. "🎪 Concerts
        // = …"). If the row doesn't start with one, there is no icon and the whole prefix is the name.
        var (icon, rest) = ExtractLeadingIcon(row.Trim());

        string name;
        string channelsPart;
        var eq = rest.IndexOf('=');
        if (eq >= 0)
        {
            name = rest[..eq].Trim();
            channelsPart = rest[(eq + 1)..];
        }
        else
        {
            name = "";
            channelsPart = rest;
        }

        var channels = SplitLogins(channelsPart);
        if (channels.Count == 0) return null;
        if (string.IsNullOrEmpty(name)) name = channels[0];
        return new TwitchChannelGroup(name, channels, icon);
    }

    /// <summary>Formats a group back into its <c>[glyph] Name = login1, login2</c> settings row.</summary>
    public static string FormatRow(TwitchChannelGroup group)
    {
        var prefix = string.IsNullOrEmpty(group.Icon) ? "" : group.Icon + " ";
        return $"{prefix}{group.Name} = {string.Join(", ", group.Channels)}";
    }

    /// <summary>
    /// Splits an optional leading icon glyph off the front of a row. Recognizes a single leading
    /// grapheme that is NOT a letter/digit (an emoji or symbol), optionally followed by whitespace,
    /// as the icon; anything else is treated as having no icon (the whole string is the name/body).
    /// </summary>
    private static (string? icon, string rest) ExtractLeadingIcon(string row)
    {
        if (row.Length == 0) return (null, row);

        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(row);
        if (!elements.MoveNext()) return (null, row);
        var first = (string)elements.Current;

        // A leading icon must be a symbol/emoji, not a normal name character. Guard against eating a
        // real name that happens to start with a letter or digit.
        if (first.Length == 1 && (char.IsLetterOrDigit(first[0]) || first[0] == '_'))
            return (null, row);

        var rest = row[first.Length..].TrimStart();
        // If dropping the "icon" leaves nothing meaningful before the '=', keep it as the name instead
        // (e.g. a group literally named "="): only treat as icon when a body remains.
        if (rest.Length == 0) return (null, row);
        return (first, rest);
    }

    /// <summary>Splits a comma/space/newline-delimited login string into normalized, de-duplicated logins.</summary>
    public static List<string> SplitLogins(string? raw) =>
        (raw ?? string.Empty)
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLogin)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Accepts a bare login or a full twitch.tv/&lt;login&gt; URL and reduces to the login slug.</summary>
    public static string NormalizeLogin(string s)
    {
        s = s.Trim();
        var idx = s.LastIndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[(idx + "twitch.tv/".Length)..];
        s = s.TrimStart('@').Trim('/');
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        return s.ToLowerInvariant();
    }

    /// <summary>A URL-safe, stable node-id slug derived from a group name (for the <c>group:</c> node id).</summary>
    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "group";
    }
}
