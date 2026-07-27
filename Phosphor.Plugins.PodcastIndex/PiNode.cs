namespace Phosphor.Plugins.PodcastIndex;

/// <summary>
/// Identifies a node in the Podcast Index browse tree, carried opaquely in
/// <c>SourceCategory.SourceState</c> so the source knows what to expand next. The root fans out to
/// Trending and per-category tiles; a Category expands to its trending feeds; a Feed expands to its
/// episodes.
/// </summary>
public enum PiNodeKind
{
    /// <summary>The single PodcastIndex root tile. Expands to Trending + category tiles.</summary>
    Root,

    /// <summary>The "Trending" branch — the globally trending feeds (no category filter).</summary>
    Trending,

    /// <summary>The "Favorites" branch — the user's favorited shows and episodes.</summary>
    Favorites,

    /// <summary>A single category (by category id). Expands to its trending feeds.</summary>
    Category,

    /// <summary>A single feed/show (by feed id). Expands to its episodes.</summary>
    Feed,
}

/// <summary>A browse-tree node for the PodcastIndex source. <paramref name="Key"/> is the category id
/// or feed id (as text) depending on <paramref name="Kind"/>.</summary>
public sealed record PiNode(PiNodeKind Kind, string Key = "");
