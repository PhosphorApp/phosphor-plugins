# Phosphor.Plugins.SiriusXM

A Phosphor source plug-in that streams **live SiriusXM channels** for a logged-in subscriber.
Loaded dynamically from the host's `plugins/SiriusXM/` folder; references only
`Phosphor.Plugin.Abstractions` (compile-only). Requires an active SiriusXM streaming subscription.

## Status

Prototype (lean v1). Proves end-to-end playback in the app. Deliberately minimal — see
**Deferred** below.

## How it works

```
SiriusXmSource (IBrowsable + IPlayableResolver + IConnectionTestable)
   │
   ├─ SxmClient        auth (login → resume) + lineup + resolve master playlist  (pure HttpClient)
   └─ SxmProxy         local HLS proxy on http://127.0.0.1:8912/
						  ├─ /master.m3u8   single-variant master
						  ├─ /variant.m3u8  SXM variant, rewritten (see proxy style below)
						  ├─ /key           serves the static AES-128 key
						  └─ /seg/<b64url>  fetches a segment from SXM with auth tokens injected
```

`ResolveAsync` returns a `ResolvedStream(Http, AudioOnly, IsLiveStream=true)` whose `PrimaryUri` is
the local `/master.m3u8`. The host plays it through its normal
`VideoItem.StreamUrl → new Media(_libVLC, uri) → Play` path — **no ffmpeg in the playback path**.

## Proxy style: **B (LibVLC decrypts)** — chosen

SXM HLS segments are AES-128 encrypted with a **static, publicly-known key**. Two proxy styles were
possible:

- **A — we decrypt in-transit:** the proxy strips `EXT-X-KEY` and serves already-decrypted AAC.
  (Proven in the `tools/SiriusXmSpike` harness; kept as a fallback.)
- **B — LibVLC decrypts (this plug-in):** the proxy keeps `EXT-X-KEY`, rewrites its `URI` to a local
  `/key` endpoint that serves the static key, and injects SXM auth tokens onto segment requests.
  LibVLC performs the AES-128 decryption itself.

**We chose B**: less work in our hot path (LibVLC does the crypto), and it keeps the proxy a thin
auth/token shim closer to the reference `sxm` design. Style A remains a proven fallback if a player
mishandles the local key URI.

## Live-stream handling

Channels are continuous live audio with no duration/seek. Items are `IsAudioOnly` + `IsLiveStream`;
the host (`JukeboxViewModel` / `BackglassWindow`) responds by:
- showing elapsed time as `M:SS / *` (no total),
- disabling seek, and
- not auto-advancing the queue when the stream drops.

## Browsing & grouping

A single **SiriusXM** root tile drills into **Music / Talk / Sports** super-groups → categories →
channels, plus a flat **All Channels** view. Categories are discovered from the lineup JSON
(`categories.categories[]`), and a bundled `categories.json` maps SXM category keys → super-groups.
Drop an edited `categories.json` in the instance cache dir to re-bucket categories without a rebuild.
The lineup is cached (`lineup.json`, 7-day freshness) so browse is instant/offline after the first fetch.

## Favorites

Implements the generic `IFavoritable` capability, so the host shows a **star** on each channel row
(only because this source opts in). Starred channels appear under a **⭐ Favorites** tile floated to
the top of the root, and persist to `favorites.json` in the instance cache dir. Favorites are just
channel ids, so they survive lineup refreshes.

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

## Deferred (not in v1)

- Robust **session/token refresh** (v1 does a one-shot re-auth on HTTP 403).
- **Now-playing metadata** (track/show titles) and channel logos beyond the lineup thumbnail.
- Live-stream **UI polish** (tuner-style navigation, hiding the scrub bar entirely).
