# Phosphor.Plugins.SiriusXM

A Phosphor source plug-in that streams **live SiriusXM channels** for a logged-in subscriber.
Loaded dynamically from the host's `plugins/SiriusXM/` folder; references only
`Phosphor.Plugin.Abstractions` (compile-only). Requires an active SiriusXM streaming subscription.

## Status

**v1.1.0** — runs entirely on the SiriusXM **edge-gateway API**
(`api.edge-gateway.siriusxm.com`) with **bearer-token (JWT)** auth. Auth, channel lineup, now-playing,
and live streaming are all off the deprecated cookie `player.siriusxm.com` path. The legacy cookie
code is retained as a compile-time fallback only (see **Legacy fallback**).

## How it works

```
SiriusXmSource (IBrowsable + IPlayableResolver + IConnectionTestable + ILiveNowPlayingProvider + …)
   │
   ├─ SxmEdgeClient    bearer/JWT session + lineup + now-playing + stream resolution (pure HttpClient)
   │                     ├─ 4-step token chain: device → anonymous → password → authenticated
   │                     ├─ GET  /relationship/v1/container/all-channels   channel lineup
   │                     ├─ POST /playback/play/v1/liveUpdate              now-playing schedule
   │                     ├─ POST /playback/play/v1/tuneSource              master playlist URL
   │                     └─ GET  /playback/key/v1/{guid}                   AES-128 content key
   └─ SxmEdgeProxy     local HLS proxy on http://127.0.0.1:8912/
					  ├─ /master.m3u8    single-variant master
					  ├─ /variant.m3u8   gateway variant, rewritten + trimmed to a live window
					  ├─ /key/{guid}     serves the gateway AES-128 key (bearer-fetched)
					  └─ /seg/<name>     fetches a pre-signed CDN segment verbatim (no auth injected)
```

`ResolveAsync` returns a `ResolvedStream(Http, AudioOnly, IsLiveStream=true)` whose `PrimaryUri` is
the local `/master.m3u8`. The host plays it through its normal
`VideoItem.StreamUrl → new Media(_libVLC, uri) → Play` path — **no ffmpeg in the playback path**.

## Authentication (headless bearer/JWT)

From the stored username/password, `SxmEdgeClient` mints a bearer session via a 4-step chain — device
grant → anonymous access token → password identity grant → authenticated user token — and injects
`Authorization: Bearer` + `x-sxm-clock` / `x-sxm-platform` / `x-sxm-tenant` headers on every gateway
request. Tokens are cached under the instance dir (`device.json` / `tokens.json` / `access.json`),
refreshed ~10 min before expiry, and re-minted on 401. No browser, no cookie scraping.

## Proxy style: **B (LibVLC decrypts)**

Gateway HLS segments are AES-128 encrypted. The proxy keeps `#EXT-X-KEY`, rewrites its `URI` to a
local `/key/{guid}` endpoint that serves the content key fetched from `/playback/key/v1/{guid}`
(bearer), and rewrites segment URIs to a local `/seg/<name>` endpoint. **LibVLC performs the AES-128
decryption itself.** Segment/playlist bytes come from the pre-signed akamai CDN URL that `tuneSource`
returns and are fetched **verbatim — no bearer or query params** (injecting auth would break the
signed URL).

Two gateway-specific proxy details worth knowing:

- **Short `/seg/` names.** Pre-signed CDN URLs carry a ~700-char session token in one path segment;
  embedding that in the local proxy path trips Windows http.sys's URL-segment length limit (→ 400).
  The proxy maps a short segment filename → full URL instead.
- **Live-window trim.** The gateway media playlist is a multi-hour DVR window (~1800 segments); the
  proxy trims to the last ~12 segments (and fixes `#EXT-X-MEDIA-SEQUENCE`) so playback starts at the
  live edge, not hours behind.

## Now-playing (current track)

Implements `ILiveNowPlayingProvider`. While a channel plays, the host polls
`GetNowPlayingAsync`, which posts `/playback/play/v1/liveUpdate` and selects the current **SONG** cut
(skipping interstitials) whose `[timestamp, timestamp+duration)` window contains the listener's audio
instant. The audio instant is anchored at `now − LiveAudioLagMs` (the HLS buffer behind live, ~30s)
so the label matches what's actually heard; talk content falls back to the current episode/show title.
Diagnostics: a `SXM np:` Debug line logs the anchoring math for tuning.

## Live-stream handling

Channels are continuous live audio with no duration/seek. Items are `IsAudioOnly` + `IsLiveStream`;
the host (`JukeboxViewModel` / `BackglassWindow`) responds by:
- showing elapsed time as `M:SS / *` (no total),
- disabling seek, and
- not auto-advancing the queue when the stream drops.

## Browsing & grouping

A single **SiriusXM** root tile drills into **Music / Talk / Sports** super-groups → categories →
channels, plus a flat **All Channels** view. Each channel carries a single **genre** from the gateway
lineup (e.g. Pop, Hip-Hop, Dance & Electronic, News & Politics), and a bundled `categories.json` maps
those genres → super-groups. Drop an edited `categories.json` in the instance cache dir to re-bucket
without a rebuild. The lineup is cached (`lineup.json`, 7-day freshness) so browse is instant/offline
after the first fetch.

> **Note:** the gateway `all-channels` container exposes one broad genre per channel, so decade
> stations (50s/60s/70s…) group under Pop/Rock rather than as separate decade tiles like the web UI
> (which sources those from separate curated containers). Broad-genre grouping is what this plug-in
> currently reproduces.

## Favorites

Implements the generic `IFavoritable` capability, so the host shows a **star** on each channel row
(only because this source opts in). Starred channels appear under a **⭐ Favorites** tile floated to
the top of the root, and persist to `favorites.json` in the instance cache dir. Favorites are just
channel ids, so they survive lineup refreshes.

## Search

Implements the generic `ITextSearchCapable` capability — a simple case-insensitive **substring filter**
over the cached lineup, matching each channel's **name, number, or category names**. So "NHL" surfaces
**NHL Radio** even though it's buried under Sports, and "howard" finds the Howard Stern channels. It's a
filter, not a fuzzy/relevance search. Hidden channels are excluded, mirroring browse. Because the source
opts into search, the host binds the search box to SiriusXM (and greys out the global search-source
selector) while it's the active source.

## Hiding channels

Implements the generic `IHideable` capability. The Plug-ins tab shows a **"Manage hidden channels…"**
button (only for `IHideable` sources) that opens a single themed modal with two side-by-side
**multi-select** lists (Visible ⇄ Hidden) + move buttons — block/range select works for fast bulk
hides. Below them a **category tree** (super-group → category, e.g. Music → Country) lets you hide or
show a whole **super-group** or **category** in one click. Hidden ids persist to `hidden.json` and are
filtered from every browse view (categories, All Channels, Favorites); the live source re-reads the
file so changes apply on the next drill-in without an app restart.

## Configuration

Plug-ins tab → add **SiriusXM** → set **Username** / **Password** (Secret) / **Region** (US/CA) →
**Test connection**. Credentials are `Secret` settings; enable the app's DPAPI "encrypt secrets"
option to protect them at rest. **Proxy port** (default `8912`) sets the local HLS proxy's port —
change it only if another app already uses that port; the running proxy rebinds when you save a new value.

## Legacy fallback

The original cookie `player.siriusxm.com` implementation (`SxmClient` / `SxmProxy`) is retained but
gated OFF behind the compile-time `SiriusXmSource.UseLegacyStreaming` (default `false`). It exists
purely for manual rollback; when the edge path is fully trusted it (and the flag) can be deleted.

## Upgrade note (v1.0.x → v1.1.0)

Channel ids changed from the old cookie **slugs** (e.g. `9446`) to gateway **UUIDs**, so saved
**favorites and hidden channels reset** on upgrade (they stored slugs that no longer match). The
lineup cache auto-invalidates. Re-favoriting/hiding once after upgrade is expected.

## Deferred / future

- **Decade & sub-genre tiles** matching the web UI (needs the curated-grouping containers).
- Retiring the **legacy cookie code** once gateway streaming is fully trusted.
- Live-stream **UI polish** (tuner-style navigation, hiding the scrub bar entirely).
