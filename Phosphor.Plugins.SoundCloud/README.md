# Phosphor.Plugins.SoundCloud

> **⚠️ EXPERIMENTAL.** Much SoundCloud content (major-label catalog) is **DRM-protected** — served
> only via `cbc-encrypted-hls` / `ctr-encrypted-hls`, which yt-dlp cannot decrypt (they resolve to
> zero formats). Rather than hide the source, this plug-in advertises itself as **experimental**
> (`IExperimental`, shown as a badge in the Plug-ins settings tab) and does **lazy discovery**: it
> remembers tracks that fail to resolve and surfaces them as **unplayable** on future searches instead
> of letting you hit dead play buttons. Indie/creator uploads and Go+ HQ transcodings generally play
> fine. See "Lazy discovery & unplayable tracks" below.

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
- `IExperimental` (on the provider) — flags the source as experimental so the host shows a badge.
- `IPlaybackReportable` — lets the host report a play-time failure back so the plug-in can remember
  definitively-unplayable (DRM/no-formats) tracks. Transient failures are ignored for persistence.

## Settings

| Key | Type | Notes |
|---|---|---|
| `resultLimit` | Enum | `25` / `50` / `75` / `100` tracks fetched per feed or search |

Multi-instance: add several SoundCloud tiles if you want more than one.

## Lazy discovery & unplayable tracks

Much SoundCloud content is DRM-protected: a large share of mainstream/major-label catalog (RCA,
Atlantic, …) is served **only** via `cbc-encrypted-hls` / `ctr-encrypted-hls` (Widevine). yt-dlp
filters those transcodings out and returns **zero formats**, so the track can't be resolved.

yt-dlp's fast search (`scsearch … --flat-playlist`) does **not** expose DRM/availability, so the only
cheap signal is a failed resolve. The plug-in turns that into **lazy discovery**:

- **On a play-time failure** the host calls `ReportPlaybackFailure(id, kind)`. A **definitive**
  failure (DRM / no formats — recognized from yt-dlp's stderr) adds the id to a persisted
  **unplayable set** (`unplayable.json` in the instance cache); a **transient** failure (network /
  timeout) is counted but never marks the track.
- **On future search/browse**, any result whose id is in the unplayable set is surfaced with
  `SourceItem.IsPlayable = false`, so the host renders it as an **unplayable row** (action buttons
  removed, a 🚫 indicator shown) rather than hiding it — you still see it exists, you just can't play it.
- **When a track fails while playing from the queue**, the current row flips to unplayable live and
  the host auto-skips to the next queued track.

### Diagnostic stats (dev-only)

`unplayable.json` also carries lightweight counters — `Attempts`, `Successes`, `Failures`,
`DefinitiveFailures`, `TransientFailures` — purely to gauge how much SoundCloud content actually
resolves versus fails. Not surfaced in the UI.

### Future: active discovery (not implemented)

Lazy discovery only learns after a failed play. A more thorough (but slower, more invasive) approach
would **pre-filter at search time** by doing a full yt-dlp extraction per result and dropping
preview/DRM formats, e.g.:

```sh
# Fast but too thin to filter on availability/format:
yt-dlp "scsearch50:query" --flat-playlist -j > results.jsonl

# Slower but gives the real fields to filter/inspect:
yt-dlp "scsearch50:query" -j --no-download > results.jsonl
jq -c 'select(.format_id | test("preview") | not)' results.jsonl
```

This runs one full extraction per row (defeating the deferred design) so it isn't used; it's noted
as a possible opt-in "active discovery" mode. The cleanest fix remains the official SoundCloud API's
`access=playable` filter (excludes DRM/paywalled/geo-blocked **server-side**), but that needs a paid
Artist Pro account + `client_id`/`client_secret` + OAuth — a much higher bar than the keyless default.

## Limitations

- **Audio-only** (SoundCloud has no video).
- **Preview-only / DRM-protected** tracks cannot be resolved — surfaced as unplayable (see above).
- Genre browse is search-backed (SoundCloud exposes no keyless charts/catalog API), so a feed is
  "top search results for the genre", not an editorial chart.
- Lazy discovery only flags a track **after** it has failed once; the very first attempt on a
  DRM track still fails (and is then remembered).

## Follow-ups (deferred)

- **Optional-credentials hybrid** — official-API discovery with `access=playable` (DRM-free results)
  when a user supplies a `client_id`/`client_secret`, falling back to keyless yt-dlp otherwise.
- **Active discovery** — opt-in full-extraction pre-filter at search time (see above).
- **Paged browse** — genre feeds and search return a single window (up to 100); lazy "load more"
  (`IPagedBrowsable`) is a natural follow-on.
- `IGaplessCapable` — SoundCloud URLs are short-lived HLS, so gapless priming is deferred.
- `IDownloadable` for disk caching/prefetch.

## Build & deploy

Building this project self-deploys its output to the host's `plugins/SoundCloud/` folder (see the
`SelfDeployToHost` target in the csproj), excluding the shared contract DLL. No host changes needed.
