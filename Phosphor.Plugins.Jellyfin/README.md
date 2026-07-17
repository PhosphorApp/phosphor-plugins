# Phosphor.Plugins.Jellyfin

A Phosphor source plug-in that browses and plays **music and video** from a self-hosted
**Jellyfin** server for a logged-in user. Loaded dynamically from the host's `plugins/Jellyfin/`
folder; references only `Phosphor.Plugin.Abstractions` (compile-only). Requires a reachable Jellyfin
server and account.

## Status

v1. On-demand (finite, seekable) content — the Plex-shaped counterpart to the SiriusXM plug-in.
No local proxy and no live-stream handling: Jellyfin returns direct, long-lived HTTP stream URLs
that the host plays through its normal `VideoItem.StreamUrl → new Media → Play` path.

## How it works

```
JellyfinSource (IBrowsable + ITextSearchCapable + IPlayableResolver + IConnectionTestable + IConfigurable)
   │
   └─ JellyfinClient   auth + browse + search + chapters + stream-URL building   (pure HttpClient)
		├─ POST /Users/AuthenticateByName   (X-Emby-Authorization header → AccessToken + UserId)
		├─ GET  /Users/{userId}/Views        (top-level libraries → root tiles)
		├─ GET  /Users/{userId}/Items?ParentId=…   (drill-down: folders → tiles, leaves → items)
		├─ GET  /Users/{userId}/Items?SearchTerm=…&Recursive=true   (search)
		├─ GET  /Users/{userId}/Items/{id}?Fields=Chapters   (chapter markers)
		├─ GET  /Audio/{id}/universal         (audio stream)
		└─ GET  /Videos/{id}/stream           (video stream)
```

- **Auth.** `POST /Users/AuthenticateByName` with the exact `X-Emby-Authorization` header
  (`MediaBrowser Client="Phosphor", Device="Phosphor", DeviceId="phosphor-<instanceId>", Version="1.0.0"`).
  The returned `AccessToken` is cached and sent as `api_key` on stream URLs. A stale token (HTTP 401)
  triggers a one-shot re-auth + retry.
- **Browse.** Each Jellyfin **library/view** becomes a root tile (with a type glyph — 🎵 music,
  🎬 movies, 🎤 music videos, 📺 TV), prefixed with the instance name (e.g. "Jellyfin Movies") so it
  won't collide with a same-named Plex tile. Use **Browse libraries…** to pick which libraries appear
  (empty selection = show all). Drilling a tile lists its children: containers (artists, albums,
  folders) become sub-tiles, leaves (tracks, movies, videos) become playable items. Item identity
  rides in the opaque `SourceState`.
- **Chapters.** `GetMetadataAsync` fetches chapter markers on play (`Fields=Chapters`), so movies/
  concerts with chapters get scrub-bar ticks and snap-to-chapter seeking.
- **Playback.** `ResolveAsync` returns a `ResolvedStream(Http, AudioOnly|Muxed, directUrl)`. Audio
  uses `/Audio/{id}/universal`; video uses `/Videos/{id}/stream`.

## Stereo (2.1) — imperative for pinball cabs

Pinball cabinets usually have 5.1/7.1 outputs, **but the surround channels drive exciters for
mechanical/ball cabinet sounds, not music.** Feeding them a surround music/movie mix is wrong, so
this plug-in must emit **2 channels only**.

The **"Stereo audio (2.1 cabs)"** setting (default **ON**) makes the client ask Jellyfin for a
2-channel downmix via `MaxAudioChannels=2` on the stream request — mirroring the in-box Plex source's
stereo option. With it off, content direct-plays at its native channel layout. **Keep it on for a
cabinet.**

## Configuration

Plug-ins tab → add **Jellyfin** → set **Server URL** (e.g. `http://192.168.1.10:8096`) /
**Username** / **Password** (Secret) / **Stereo audio** → **Test connection**. Use **Browse
libraries…** to choose which libraries become tiles (leave all unselected to show every library).
The password is a `Secret` setting; enable the app's DPAPI "encrypt secrets" option to protect it at
rest. Multiple servers are supported — add another Jellyfin instance for each.

## Deferred (not in v1)

- **Favorites** (`IFavoritable`) — Jellyfin has server-side favorites (like Plex), so no client-side
  workaround is needed; wiring the star to the server is a future addition.
- Paged browse (`IPagedBrowsable`) for very large libraries, scoped/in-node search
  (`IScopedSearchable`), and gapless audio (`IGaplessCapable`).
- Now-playing / richer metadata and per-item transcode tuning.
