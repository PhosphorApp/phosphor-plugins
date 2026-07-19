# Phosphor.Plugins.IHeartRadio

A Phosphor **source** plug-in that streams **iHeartRadio** live radio stations **and on-demand
podcasts**. Loaded dynamically by the host from its `plugins/` folder — there is deliberately **no**
project reference from the host back to this plug-in; it references only the
`Phosphor.Plugin.Abstractions` contract (compile-only).

## What it does

- **Browse (live radio)** — a single **iHeartRadio** root tile expands to **Popular Stations** plus the
  live-station **genres** (Classic Rock, Country, Pop, …). Drilling into a genre lists its stations.
- **Browse (podcasts)** — a **Podcasts** branch expands to categories → podcasts → episodes. Unlike the
  live streams these are **finite, seekable** tracks with real durations (and far less ad-laden).
- **Search** — free-text search across iHeart's catalog: live stations **plus** podcast shows (the
  latter as drill-in containers whose episodes you can browse/play).
- **Favorites** — star a station to float it to the top (persisted to the instance dir).
- **Play** — live stations resolve to a raw stream URL played as `IsLiveStream`; podcast episodes
  resolve to a direct `mediaUrl` MP3 played as an ordinary seekable track.

## Why it's the lean cousin of SiriusXM

Both are **live radio** sources that ride the host's `IsLiveStream` path (no seek/progress, no
duration, no playlist auto-advance — "next" is a station change). But iHeart is dramatically simpler:

| | SiriusXM | **iHeartRadio** |
|---|---|---|
| Auth | subscriber login (cookies) | **none — key-less public catalog** |
| Discovery | authenticated lineup fetch | `liveStationGenres` + `liveStations` (key-less) |
| Playback | AES-128 HLS → **local decrypt proxy** | **plain non-DRM HLS → play directly** |
| yt-dlp | not supported (custom) | not needed (raw URL from the API) |
| Config | username/password/region/port | **nothing to configure** |

So there is **no login, no proxy, and no external tools** — `ResolveAsync` just returns the station's
best stream URL with `IsLiveStream = true`.

### Stream format selection

Not every station offers HLS — many are **Shoutcast-only** (or PLS). The client picks the best
playable URL from the station's `streams` object, preferring the secure (https) variant of each
format, in order **HLS → Shoutcast → PLS** (all of which LibVLC plays directly). Selecting only
`secure_hls_stream` was the cause of "stream unavailable" on Shoutcast-only stations.

## Endpoints (all `api.iheart.com`, unauthenticated)

**Live radio**
- `GET /api/v2/content/liveStationGenres` — genre taxonomy (id + name + station count).
- `GET /api/v2/content/liveStations?genreId={id}&limit=N` — stations in a genre (streams embedded).
- `GET /api/v2/content/liveStations?limit=N` — popular/featured stations.
- `GET /api/v1/catalog/searchAll?keywords=…` — free-text station search (station ids/names).
- `GET /api/v2/content/liveStations/{id}` — single station (used to resolve a stream URL for search
  results / favorites that don't carry one inline).

**Podcasts (on-demand)**
- `GET /api/v3/podcast/categories` — podcast category taxonomy (id + name).
- `GET /api/v3/podcast/categories/{id}` — podcasts within a category (listed inline).
- `GET /api/v3/search/all?keywords=…&podcast=true` — podcast search (the `podcast=true` flag is
  **required** — the `results.podcasts` array is empty without it).
- `GET /api/v3/podcast/podcasts/{id}/episodes` — a podcast's episodes (finite, with durations).
- `GET /api/v3/podcast/episodes/{id}` — a single episode's direct `mediaUrl` MP3 (for playback).

Feasibility was proven first in `tools/IHeartRadioSpike` (live) and `tools/IHeartPodcastSpike`
(podcasts) — see `SOURCE_PLUGIN_CANDIDATES.md`.

## Capabilities

`IBrowsable` + `IPagedBrowsable` (podcast episodes) + `ITextSearchCapable` + `IPlayableResolver` +
`IConnectionTestable` + `IFavoritable` + `IFavoriteCapture`. Single-instance
(`SupportsMultipleInstances = false`).

### Favorites (stations, episodes, and podcast shows)

Favorites are typed (`IHeartFavorite` with a `Station` / `Episode` / `Podcast` kind), captured at
star-time via `IFavoriteCapture.RememberFavorite`, and rebuilt correctly by kind:

- **Station** → replays as a live stream (reusing a resolved URL when known).
- **Episode** → replays from its `mediaUrl` as a seekable track.
- **Podcast show** → appears in the Favorites view as a **drill-in container** (browse its episodes).

(This fixes the earlier bug where every favorite was stored as a station, so a favorited episode
couldn't play.)

### Paged episodes ("load more")

iHeart pages episodes with an opaque **cursor** (`links.next`), not an offset. The plug-in bridges
that onto the host's offset-based `IPagedBrowsable` by caching, per podcast, the next-cursor keyed by
the offset it advances to — so sequential "load more" works as the user scrolls.

## Build & deploy

Building this project self-deploys its output to
`Phosphor/bin/<Config>/net8.0-windows/plugins/IHeartRadio/`. The host discovers it on next launch.

## Limitations / deferred

- **Live-radio UX** — stations are continuous; no seek/duration/auto-advance (by design). Podcast
  episodes are the seekable, finite counterpart.
- **Live-stream ads** are server-side-inserted (baked into the audio timeline) and unavoidable
  client-side; on-demand podcasts are the ad-light path.
- Unofficial API (no formal dev program) so endpoints could shift.
- Genre / podcast-category lists are fetched once per session (no on-disk cache — they're small and
  cheap). Episode paging is cursor-based and sequential (no random-access offsets).
