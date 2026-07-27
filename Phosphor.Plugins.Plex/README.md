# Phosphor.Plugins.Plex

The in-box Plex **source plug-in**. Browse and play from a Plex Media Server — Music, Movies, TV
libraries (with hubs, playlists, artist/album drill-down, search, favorites) — and now **Live TV**.

## Live TV

Plex Live TV appears as **just another Plex tile**, alongside your libraries. If you enable it in
**"Browse libraries…"**, you get a **📺 Live TV** tile (e.g. "Plex Live TV") next to "Plex Movies"
and "Plex TV". Behind the scenes it is a different mechanism, but to you it looks the same as a
library.

### What it does

- **Lineup** — opening the tile lists the DVR's live channels (from
  `/{epg}/lineups/dvr/channels`), sorted by channel number, with channel logos and HD flags.
- **What's on now** — each channel title is enriched with the program airing right now, joined from
  the EPG grid (`/{epg}/grid`) — e.g. `2.1 CBS – The Andy Griffith Show`. A full on-screen program
  guide is out of scope.
- **Playback** — playing a channel **tunes** it
  (`POST /livetv/dvrs/{dvr}/channels/{channel}/tune`), then plays the resulting airing through Plex's
  **universal HLS transcoder** (`/video/:/transcode/universal/start.m3u8`). Plex acts as a
  re-streaming proxy in front of the backing tuner (e.g. an HDHomeRun) and transcodes on the fly for
  compatibility. The stream is marked live (no scrub bar / auto-advance).
- **One tuner per channel** — each playing channel occupies one physical tuner, shared with your
  other Plex viewing, so concurrency is limited by your tuner count.
- **Unavailable badge (retryable)** — if a channel fails to play (all tuners busy, brief outage) it
  stays visible and playable but is badged ⊘. Just play again to retry; a successful play clears it.

### Session lifecycle (important)

A live tune holds a physical tuner until it is **explicitly stopped**. The plug-in self-manages
teardown on two levels:

- **Host-driven (primary):** the plug-in implements `IPlaybackStoppable`, so the host calls
  `ReleasePlayback(itemId)` for the outgoing item on **stop / skip / track-change / shutdown** (the
  single `CurrentlyPlaying` choke point). This deterministically releases the tuner the moment the
  user stops or switches.
- **Self-managed (belt-and-suspenders):** at most **one live session per instance** (opening a new
  channel stops the prior), a **keep-alive** timer while playing, and **panic cleanup** on
  init/settings-change that stops any stray live transcode session a prior crash may have left. The
  blast radius of a missed stop is one tuner until Plex's idle timeout — these layers avoid that.

## Settings

| Setting | Purpose |
|---|---|
| **Server URL** | e.g. `http://192.168.1.10:32400`. |
| **Plex token** | X-Plex-Token for this server. |
| **Prefer stereo audio** | Downmix/transcode surround to stereo (imperative on pinball cabs). |
| **Libraries** | Configured via **"Browse libraries…"** — pick which libraries (and Live TV) appear as tiles. |

Multiple Plex servers can be added.

## Authoring notes

Live TV integrates into the existing plug-in rather than a separate one: it is a synthetic
`"livetv"` library type that renders as a tile (`PlexMappings.LiveTvRootCategory`), a
`PlexNodeKind.LiveTv` browse case listing channels, and a `PlexLiveRef`-carrying resolve path. The
live REST surface and the tuner session machine live in `PlexLiveTvService` to keep them out of the
already-large `PlexService`.
