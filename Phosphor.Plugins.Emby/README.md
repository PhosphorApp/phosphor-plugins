# Phosphor.Plugins.Emby

A Phosphor source plug-in that browses and plays **music and video** from a self-hosted
**Emby** server for a logged-in user. Loaded dynamically from the host's `plugins/Emby/`
folder; references only `Phosphor.Plugin.Abstractions` (compile-only). Requires a reachable Emby
server and account.

## Status

v1. On-demand (finite, seekable) content — mirrors the Jellyfin plug-in. Emby and Jellyfin share
nearly identical REST APIs (Jellyfin began as a fork of Emby), so the implementation is intentionally
a near-clone. No local proxy and no live-stream handling: Emby returns direct, long-lived HTTP stream
URLs that the host plays through its normal `VideoItem.StreamUrl → new Media → Play` path.

## How it works

```
EmbySource (IBrowsable + ITextSearchCapable + IPlayableResolver + IConnectionTestable + IConfigurable)
   │
   └─ EmbyClient   auth + browse + search + chapters + stream-URL building   (pure HttpClient)
		├─ POST /Users/AuthenticateByName   (X-Emby-Authorization header → AccessToken + UserId)
		├─ GET  /Users/{userId}/Views        (top-level libraries → root tiles)
		├─ GET  /Artists?ParentId=…          (music: artist entities → tiles, with portraits)
		├─ GET  /Users/{userId}/Items?IncludeItemTypes=MusicAlbum&AlbumArtistIds=…   (music: albums)
		├─ GET  /Users/{userId}/Items?IncludeItemTypes=Audio&ParentId=…   (music: album tracks)
		├─ GET  /Users/{userId}/Items?ParentId=…&Recursive=…   (video/other: drill-down / flatten)
		├─ GET  /Users/{userId}/Items?SearchTerm=…&Recursive=true   (search)
		├─ GET  /Users/{userId}/Items/{id}?Fields=Chapters   (chapter markers)
		├─ GET  /Audio/{id}/stream            (audio stream)
		└─ GET  /Videos/{id}/stream           (video stream)
```

- **Auth.** `POST /Users/AuthenticateByName` with the exact `X-Emby-Authorization` header
  (`MediaBrowser Client="Phosphor", Device="Phosphor", DeviceId="phosphor-<instanceId>", Version="1.0.0"`).
  The returned `AccessToken` is cached and sent as `api_key` on stream URLs. The token is retained
  across calls (only re-fetched when the server/credentials change or on a 401), so browsing a large
  album resolves against a single authentication rather than one per track.
- **Browse.** Each Emby **library/view** becomes a root tile (with a type glyph — 🎵 music,
  🎬 movies, 🎤 music videos, 📺 TV), prefixed with the instance name (e.g. "Emby Movies") so it
  won't collide with a same-named Plex tile. Use **Browse libraries…** to pick which libraries appear
  (empty selection = show all). Browse behavior is library-type-aware (see **Browsing by library
  type** below). Item identity rides in the opaque `SourceState`.
- **Chapters.** `GetMetadataAsync` fetches chapter markers on play (`Fields=Chapters`), so movies/
  concerts with chapters get scrub-bar ticks and snap-to-chapter seeking.
- **Playback.** `ResolveAsync` returns a `ResolvedStream(Http, AudioOnly|Muxed, directUrl)`. Audio
  uses `/Audio/{id}/stream`; video uses `/Videos/{id}/stream`. It (re)authenticates before building
  the URL so the `api_key` is always populated.

## Browsing by library type

Emby exposes libraries differently depending on their kind, so the plug-in browses each the way the
Emby web client does (tracked via `EmbyMusicLevel`/`CollectionType` on the node `SourceState`):

- **Music** → the **entity graph**, not the raw folder tree: library → **artists** (`/Artists`, with
  their own portraits) → **albums** (`MusicAlbum` entities) → **tracks** (`Audio`). This is what gives
  artists and albums proper artwork. Album entities frequently have **no Primary image of their own**
  (their cover is derived from embedded track art), so each album tile **borrows a child track's
  cover** via a per-artist `albumId → track-image` map — reproducing the web UI.
- **Movies / Music Videos / Home Videos** → each title lives in its own folder, so the browse
  **recurses and filters to the leaf type** (`Movie` / `MusicVideo` / `Video`), collapsing per-title
  folders into directly-playable items instead of drill-in tiles wrapping a single file.
- **Other (TV, etc.)** → natural hierarchy: containers become drill-in tiles, leaves become items.

## Stereo (2.1) — imperative for pinball cabs

Pinball cabinets usually have 5.1/7.1 outputs, **but the surround channels drive exciters for
mechanical/ball cabinet sounds, not music.** Feeding them a surround music/movie mix is wrong, so
this plug-in must emit **2 channels only**.


The **"Stereo audio (2.1 cabs)"** setting (default **ON**) makes the client ask Emby for a
2-channel downmix via `MaxAudioChannels=2` on the stream request — mirroring the in-box Plex source's
stereo option. With it off, content direct-plays at its native channel layout. **Keep it on for a
cabinet.**

## Configuration

Plug-ins tab → add **Emby** → set **Server URL** (e.g. `http://192.168.1.10:8096`) /
**Username** / **Password** (Secret) / **Stereo audio** → **Test connection**. Use **Browse
libraries…** to choose which libraries become tiles (leave all unselected to show every library).
The password is a `Secret` setting; enable the app's DPAPI "encrypt secrets" option to protect it at
rest. Multiple servers are supported — add another Emby instance for each.

## Notes / known differences

Emby and Jellyfin share nearly the same REST API (Jellyfin began as a fork of Emby), but the two
servers behave differently in a few areas the plug-ins handle. The bug-class fixes below were
**ported to the Jellyfin plug-in** (auth caching, `EnableImageTypes`, `ResolveAsync` re-auth); the
**browse-shape** changes were **not** ported because the test Jellyfin server already renders
correctly with the flat-folder browse — porting them risks regressing a working setup. If a Jellyfin
library ever shows the wrapped-folder or missing-music-art behavior, port the relevant piece from
`EmbySource`.

- **Music entity browsing.** Music libraries browse the **entity graph** (`/Artists` → `MusicAlbum` →
  `Audio`), not the raw folder tree, so artists/albums get their own artwork. Emby-only; Jellyfin
  still uses the flat browse.
- **Album art from track.** Emby `MusicAlbum` entities often have **no Primary image** of their own
  (a tag-less `…/Images/Primary` request 404s), so album tiles borrow a child track's cover via a
  per-artist `albumId → track-image` map. Emby-only.
- **Per-title folders in video libraries.** Emby stores each movie / music-video title in its own
  folder, so `movies` / `musicvideos` / `homevideos` libraries recurse and filter to the leaf item
  type (`Movie` / `MusicVideo` / `Video`), collapsing folders into directly-playable items. Emby-only.
- **Image tags on list queries.** Both servers omit `ImageTags` from list queries unless images are
  explicitly requested, so browse/search add `EnableImageTypes=Primary&ImageTypeLimit=1`. **Ported to
  Jellyfin.**
- **Auth caching.** `Configure()` only clears the cached access token when the server/credentials
  actually change (not on every `ResolveAsync`), so opening a large album authenticates **once**
  instead of per-track. **Ported to Jellyfin.**
- **Stream `api_key`.** `ResolveAsync` (re)authenticates before building the stream URL so `api_key`
  is always populated. **Ported to Jellyfin.**

## Deferred (not in v1)

- **Favorites** (`IFavoritable`) — Emby has server-side favorites (like Plex), so no client-side
  workaround is needed; wiring the star to the server is a future addition.
- Paged browse (`IPagedBrowsable`) for very large libraries, scoped/in-node search
  (`IScopedSearchable`), and gapless audio (`IGaplessCapable`).
- Now-playing / richer metadata and per-item transcode tuning.

