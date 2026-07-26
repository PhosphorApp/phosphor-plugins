# Phosphor.Plugins.Iptv

A Phosphor **source plug-in** that surfaces the community-maintained
[**iptv-org**](https://github.com/iptv-org/iptv) catalog of publicly-listed **free live TV & radio
streams**, browsable by **country** and by **category/genre**, with **favorites** support.

## What it does

- **Discovery** — downloads and joins the public [iptv-org JSON API](https://github.com/iptv-org/api)
  (`streams`, `channels`, `logos`, `countries`, `categories`) into a single catalog. IPTV occupies a
  **single tile** on the home screen; opening it reveals:
  - **By Country** → a tile per broadcast country (with flag emoji) → its live channels.
  - **By Category** → a tile per genre (News, Music, Movies, Sports, …) → its live channels.
  - **Favorites** → channels you have starred (shown only when you have any).

  When "Organize by" is set to a single axis, that axis is **flattened** — the IPTV tile expands
  straight to the country (or category) tiles, with no redundant intermediate folder.
  A free-text **search** box filters across channel name, country, and category.
- **Playback** — every channel is a continuous **live stream**. The plug-in resolves each item to its
  HLS/HTTP URL (attaching the stream's `Referer` / `User-Agent` headers when the catalog supplies
  them) and marks it live, so Phosphor shows a `M:SS / *` timer + "● LIVE" badge, hides the scrub bar,
  and never auto-advances.
- **Unavailable badge (retryable)** — many iptv-org streams are geo-blocked or temporarily offline.
  When a channel fails to play, it stays visible and fully playable but is marked with a small ⊘
  badge so you know it failed last time. Just click Play to retry; a successful play clears the badge
  automatically. A **Rescan** also clears all badges (fresh stream URLs may work again).
- **Manage countries & categories** — a "Manage hidden…" affordance lets you suppress whole countries
  or categories from the browse tree (they are grouped under *Countries* / *Categories* in the editor).
- **Refresh** — "Rescan library" force-refreshes the catalog from the iptv-org API (works even with no
  prior catalog). The catalog is cached to the instance directory and auto-refreshes after the
  configured freshness window.

## Settings

| Setting | Purpose |
|---|---|
| **Organize by** | Show a *By Country* tile, a *By Category* tile, or both. |
| **Include adult channels** | Include channels iptv-org flags as adult content. Off by default. |
| **Cache freshness (hours)** | How long the downloaded catalog stays fresh before an auto-refresh. `0` = until you manually rescan. |

No account, API key, or external tool is required.

## Legal & caveats

This plug-in **stores no media**. It only reads publicly-submitted **links** from the iptv-org
project, which to the best of that project's knowledge were made public by the copyright holders.

- Streams are **third-party** and outside this plug-in's (or Phosphor's) control.
- Many streams are **geo-restricted** or **go offline** frequently — **dead channels are expected**.
- Some content may be subject to **local broadcast rights**. Use responsibly and in accordance with
  the laws in your jurisdiction.
- See the iptv-org [README](https://github.com/iptv-org/iptv#readme) and
  [Legal notice](https://github.com/iptv-org/iptv#legal) for details and takedown procedures.

## Authoring notes

Built against `Phosphor.Plugin.Abstractions` (compile-only) per the
[Plug-in Authoring Guide](../docs/PLUGIN_AUTHORING_GUIDE.md). It implements `IBrowsable`,
`ITextSearchCapable`, `IPlayableResolver`, `IRefreshable`, `IFavoritable`, `IFavoriteCapture`,
`IReplayableById`, `IHideable` (hidden countries/categories), `IPlaybackReportable` +
`IPlaybackSuccessReportable` (retryable ⊘ unavailable badge that self-heals on a successful play),
and targets plain `net8.0` with no UI dependencies.
