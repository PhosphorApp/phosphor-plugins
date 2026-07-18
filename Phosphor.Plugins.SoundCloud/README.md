# Phosphor.Plugins.SoundCloud

> **⚠️ DISABLED — not deployed.** This plug-in is intentionally **not built into the host's
> `plugins/` folder** (`DisableSoundCloudPlugin` is `true` in the csproj), so the host never
> discovers it and it does **not** appear in the source list. The source code is kept for a possible
> future revisit. **Why:** too much SoundCloud content is DRM-protected — major-label catalog tracks
> are served only via `cbc-encrypted-hls` / `ctr-encrypted-hls`, which yt-dlp cannot decrypt (they
> resolve to zero formats). In practice tracks would be skipped constantly, a frustrating jukebox
> experience. See "Why it's disabled" below and `SOURCE_PLUGIN_CANDIDATES.md`.
>
> **To re-enable:** set `<DisableSoundCloudPlugin>false</DisableSoundCloudPlugin>` in
> `Phosphor.Plugins.SoundCloud.csproj` and rebuild — the self-deploy target restores it.

A source plug-in that browses and plays **SoundCloud** audio tracks. Loaded dynamically from the
host's `plugins/SoundCloud/` folder; references only the `Phosphor.Plugin.Abstractions` contract
(compile-only).

## How it works

- **Browse: curated genre feeds.** Opening the tile shows a **Favorites** node plus a set of genre
  feeds (Electronic, House, Techno, Hip-Hop, Rock, Jazz, ...). Each feed is a curated SoundCloud
  search — SoundCloud has no keyless catalog/charts API, so feeds are backed by yt-dlp's keyless
  `scsearch` extractor.
- **Search: the host search box** queries SoundCloud's catalog via `scsearch`.
- **Favorites: pin tracks with the star** (`IFavoritable`). Stored per-instance in `favorites.json`
  with title/thumbnail/duration/uploader/URL so the Favorites view renders instantly/offline; a
  favorite pinned by id only is **enriched** on next open.
- **Playback: bundled yt-dlp.** Streams are resolved by shelling out to the host-bundled `yt-dlp`
  (via `IPluginHost.GetToolPath("yt-dlp")`), reusing yt-dlp's mature SoundCloud extractor. SoundCloud
  is **audio-only**, so resolution returns an `AudioOnly` stream, played fine under the audio-reactive
  visuals. yt-dlp *resolves* lazily at play time (deferred).

## No account or key needed

Unlike Vimeo (which needs a per-user access token), **SoundCloud needs no credentials** — yt-dlp's
SoundCloud extractor auto-derives a `client_id`, so search, genre browse, and playback all work
unauthenticated. There is no token setting and no setup: install the plug-in and it works. Unlike
Dailymotion (which has a keyless REST API), SoundCloud has **no keyless public API at all**, so
*both* discovery and playback ride yt-dlp.

## Capabilities

- `IBrowsable` — Favorites + curated genre feeds.
- `ITextSearchCapable` — public SoundCloud search (via `scsearch`).
- `IFavoritable` — star toggle to pin tracks to Favorites (with id-only enrichment).
- `IPlayableResolver` + `IDeferredStreamResolution` — resolves an audio stream lazily **at play
  time** (one yt-dlp probe on play, not one per row) so browse/search stay fast.
- `IConnectionTestable` — checks yt-dlp availability and SoundCloud reachability.

## Settings

| Key | Type | Notes |
|---|---|---|
| `resultLimit` | Enum | `25` / `50` / `75` / `100` tracks fetched per feed or search |

Multi-instance: add several SoundCloud tiles if you want more than one.

## Limitations

- **Audio-only** (SoundCloud has no video).
- **Preview-only / DRM-protected** tracks cannot be resolved (no raw stream URL) — yt-dlp reports
  these and the item is skipped at play time.
- Genre browse is search-backed (SoundCloud exposes no keyless charts/catalog API), so a feed is
  "top search results for the genre", not an editorial chart.

## Why it's disabled

The DRM limitation above turned out to dominate real-world use, not be an edge case. A large share of
mainstream/major-label catalog (RCA, Atlantic, etc.) is served **only** via SoundCloud's
`cbc-encrypted-hls` / `ctr-encrypted-hls` (Widevine) protocols. yt-dlp filters those transcodings out
and returns **zero formats**, so the track can't be resolved and the host auto-skips it. Across
typical searches enough results are DRM-protected that the jukebox would skip tracks constantly —
frustrating enough that we disabled the plug-in rather than ship that experience.

The clean fix would be the **official SoundCloud API's `access=playable`** search filter, which
excludes DRM/paywalled/geo-blocked tracks **server-side** so they never appear. But that API is not
keyless: it requires a **paid Artist Pro account**, a registered app with a `client_id` +
`client_secret`, and an OAuth 2.1 token lifecycle — a much higher onboarding bar than any other
source. That's the natural shape of a future revisit (an **optional-credentials hybrid**: keyless
yt-dlp by default, official-API discovery with `access=playable` when a user supplies credentials),
tracked as a follow-up rather than built now.

## Follow-ups (deferred)

- **Optional-credentials hybrid** — official-API discovery with `access=playable` (DRM-free results)
  when a user supplies a `client_id`/`client_secret`, falling back to keyless yt-dlp otherwise. This
  is the path to re-enabling the plug-in with a good experience.
- **Paged browse** — genre feeds and search return a single window (up to 100); lazy "load more"
  (`IPagedBrowsable`) is a natural follow-on.
- `IGaplessCapable` — SoundCloud URLs are short-lived HLS, so gapless priming is deferred.
- `IDownloadable` for disk caching/prefetch.

## Build & deploy

Building this project self-deploys its output to the host's `plugins/SoundCloud/` folder (see the
`SelfDeployToHost` target in the csproj), excluding the shared contract DLL. No host changes needed.
