namespace Phosphor.Plugins.IHeartRadio;

/// <summary>
/// Identifies a node in the iHeartRadio browse tree, carried opaquely in
/// <c>SourceCategory.SourceState</c> so the source knows what to expand next. Two subtrees hang off
/// the root: live radio (root → genre → stations, + Popular/Favorites) and on-demand podcasts
/// (Podcasts → category → podcast → episodes).
/// </summary>
public enum IHeartNodeKind
{
    /// <summary>The single iHeartRadio root tile. Expands to the genre tiles + All Stations.</summary>
    Root,

    /// <summary>A single live-station genre (by genre id). Expands to its stations.</summary>
    Genre,

    /// <summary>A flat list of popular/featured live stations.</summary>
    AllStations,

    /// <summary>The "Podcasts" branch. Expands to podcast categories.</summary>
    Podcasts,

    /// <summary>A single podcast category (by category id). Expands to its podcasts.</summary>
    PodcastCategory,

    /// <summary>A single podcast show (by podcast id). Expands to its episodes.</summary>
    Podcast,
}

/// <summary>A browse-tree node for the iHeartRadio source. <paramref name="Key"/> is the genre id,
/// podcast category id, or podcast id (as text) depending on <paramref name="Kind"/>.</summary>
public sealed record IHeartNode(IHeartNodeKind Kind, string Key = "");
