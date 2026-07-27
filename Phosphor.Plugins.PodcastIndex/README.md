# Phosphor.Plugins.PodcastIndex

A Phosphor **source plug-in** that indexes and plays podcasts via the
[Podcast Index API](https://podcastindex.org/) (`api.podcastindex.org`).

## What it does

Podcast Index is a large, open **index** of podcast feeds. This plug-in lets you:

- **Browse** – a single "Podcast Index" tile expands to **Trending** plus the API's category
  taxonomy (Comedy, News, Technology, …). Drilling into a category lists its trending shows; drilling
  into a show lists its episodes.
- **Search** – free-text search returns matching **shows** (feeds) as drill-in containers.
- **Favorites** – star a **show** or an **episode**; favorites float to a "⭐ Favorites" tile at the
  top of the browse tree. Favorited episodes replay by re-fetching their enclosure by id, so they
  survive a restart.
- **Play** – Podcast Index is a *pure index*: each episode response carries a direct, non-DRM
  `enclosureUrl` (the `.mp3`/`.m4a`/`.mp4` the publisher hosts). The plug-in hands that URL straight
  to the host player — no scraping, no `yt-dlp`, no proxy.

Episodes are **finite, seekable** tracks (real duration), so they play with normal seek/progress and
auto-advance — they are **not** live streams. Most podcasts are audio; **video podcasts** (whose
enclosure is a `video/*` type) play as video automatically.

## Account (required, free)

The Podcast Index API is keyed **per user**. Register a **free** API key + secret at
<https://api.podcastindex.org/> and enter them in the plug-in's settings:

| Setting | Notes |
|---|---|
| **API Key** | Your Podcast Index API key (stored as a secret). |
| **API Secret** | The secret issued alongside the key (stored as a secret). |

Both are stored through the host's credential store. Use **Test connection** to verify.

> Per-user keys (not a shared embedded key) are used deliberately: Phosphor is open source, so an
> embedded secret would be public, and per-user keys keep rate limits and accountability isolated.

## Design notes

- **Auth** – every request sends the Amazon-style headers the API requires: `User-Agent`,
  `X-Auth-Key`, `X-Auth-Date` (unix seconds), and `Authorization = SHA1hex(apiKey + apiSecret +
  unixSeconds)`. See `PodcastIndexClient`.
- **Lean playback** – `ResolveAsync` returns the inline `enclosureUrl` as an `Http`-transport
  `ResolvedStream` (`AudioOnly` for audio, `Muxed` for video). The host's existing
  StreamUrl → Media → Play path does the rest.
- **Contract-only** – references only `Phosphor.Plugin.Abstractions`, compile-only; the host owns the
  single shared runtime copy. Self-deploys to `Phosphor/…/plugins/PodcastIndex/` on build.

## Capabilities

`IPhosphorSource`, `IBrowsable`, `ITextSearchCapable`, `IPlayableResolver`, `IConnectionTestable`,
`IFavoritable`, `IFavoriteCapture`.
