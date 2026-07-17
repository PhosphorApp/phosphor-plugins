namespace Phosphor.Plugins.SiriusXM;

/// <summary>
/// Identifies a node in the SiriusXM browse tree, carried opaquely in
/// <c>SourceCategory.SourceState</c> so the source knows what to expand next. The tree is:
/// root (single "SiriusXM" tile) → super-group (Music/Talk/Sports) → category (Rock, Comedy, NFL…)
/// → channels. "All Channels" is a flat sibling of the super-groups under the root.
/// </summary>
public enum SxmNodeKind
{
    /// <summary>The single SiriusXM root tile. Expands to the super-groups + All Channels.</summary>
    Root,

    /// <summary>A super-group: Music, Talk, Sports, or Other. Expands to its categories.</summary>
    SuperGroup,

    /// <summary>A single SXM category (by category key). Expands to its channels.</summary>
    Category,

    /// <summary>The flat "All Channels" list.</summary>
    AllChannels,
}

/// <summary>A browse-tree node for the SiriusXM source. <paramref name="Key"/> is the super-group
/// name or category key depending on <paramref name="Kind"/>.</summary>
public sealed record SxmNode(SxmNodeKind Kind, string Key = "");
