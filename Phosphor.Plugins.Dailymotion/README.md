# Phosphor.Plugins.Dailymotion

A source plug-in that browses and plays **Dailymotion** videos. Loaded dynamically from the host's
`plugins/Dailymotion/` folder; references only the `Phosphor.Plugin.Abstractions` contract
(compile-only).

## How it works

- **Browse: Dailymotion's editorial categories.** Opening the tile shows a **Favorites** node plus
  Dailymotion's categories (Music, Movies, Gaming, Comedy, News, ...), fetched from its public API.
  Drilling into a category lists its videos, **paged** — scroll to "load more".
- **Search: the host search box** queries Dailymotion's public catalog.
- **Favorites: pin videos with the star** (`IFavoritable`). Stored per-instance in `favorites.json`
  with title/thumbnail/duration/URL so the Favorites view renders instantly/offline; a favorite pinned
  by id only is **enriched** on next open via `GET /video/{id}`.
- **Playback: bundled yt-dlp.** Streams are resolved by shelling out to the host-bundled `yt-dlp`
  (via `IPluginHost.GetToolPath("yt-dlp")`), reusing yt-dlp's mature Dailymotion extractor. The API
  layer only *finds* videos; yt-dlp *resolves* them lazily at play time (deferred).

## No account or key needed

Unlike Vimeo (which needs a per-user access token), **Dailymotion's public API is keyless** — search,
categories, and paging all work unauthenticated (confirmed by `tools/DailymotionSpike/`). There is no
token setting and no setup: install the plug-in and it works.

## Capabilities

- `IBrowsable` — Favorites + Dailymotion category tree.
- `IPagedBrowsable` — lazy "load more" for category video lists (Dailymotion pages are 1-based).
- `ITextSearchCapable` — public Dailymotion search.
- `IFavoritable` — star toggle to pin videos to Favorites (with id-only enrichment).
- `IPlayableResolver` + `IDeferredStreamResolution` — resolves a stream lazily **at play time** (one
  yt-dlp probe on play, not one per row) so browse/search stay fast.
- `IConnectionTestable` — checks Dailymotion reachability and yt-dlp availability.

## Settings

| Key | Type | Notes |
|---|---|---|
| `quality` | Enum | `Low` / `Medium` / `High` / `Max` height ceiling |

Multi-instance: add several Dailymotion tiles if you want more than one.

## Limitations

- **Private / geo-restricted** videos cannot be resolved (no raw stream URL).
- Ad-supported content; stream quality varies by upload.
- Search returns the first page (up to 100); category browse is fully paged.

## Follow-ups (deferred)

- **Paged search** — category/channel browse is paged; the ad-hoc search box still returns a single
  page. Paging search results is a natural follow-on.
- `IDownloadable` for disk caching/prefetch.

## Build & deploy

Building this project self-deploys its output to the host's `plugins/Dailymotion/` folder (see the
`SelfDeployToHost` target in the csproj), excluding the shared contract DLL. No host changes needed.
