# Phosphor.Plugins.HdHomeRun

A Phosphor **source plug-in** that surfaces the live TV lineup of a
[**SiliconDust HDHomeRun**](https://www.silicondust.com/) network tuner on your local network, with
per-channel **live playback**, **favorites**, **hide/unhide** management, and **guide data** (channel
icons + the current program title) from the SiliconDust guide service.

## What it does

- **Discovery** — reads the tuner's local HTTP API directly:
  - `GET http://<tuner>/discover.json` → device specifics (model, id, firmware, tuner count, base
	URL, and the rotating `DeviceAuth` token used by the guide service).
  - `GET http://<tuner>/lineup.json` → the scanned channel lineup (guide number, name, stream URL,
	HD/DRM flags).

  These are joined into a single flat catalog. The HDHomeRun occupies a **single tile** on the home
  screen; opening it lists the tuner's live channels (sorted by channel number). A free-text
  **search** box filters across channel name and number.
- **Playback** — every channel is a continuous **live MPEG-TS stream** served by the tuner over HTTP.
  The plug-in resolves each item to its lineup URL and marks it live, so Phosphor shows a `M:SS / *`
  timer + "● LIVE" badge, hides the scrub bar, and never auto-advances. **DRM-protected** channels are
  skipped (they cannot be played outside SiliconDust's own apps). Each playing channel occupies one
  physical tuner, so the number of simultaneous streams is limited by your device's tuner count.
- **Guide data (channel icons + current program)** — when "Fetch guide data" is enabled, the plug-in
  pulls per-channel artwork **and** the upcoming program schedule from the SiliconDust guide service
  (`api.hdhomerun.com`) using the tuner's `DeviceAuth` token. Icons are overlaid onto the lineup, and
  the program airing **right now** is appended to the channel title — e.g. `2.1 WFMY-HD` becomes
  `2.1 WFMY-HD - 6:00 News`. Because the title flows through unchanged, the current program shows in
  **both** the channel list and the **now-playing** display. The guide (icons + ~24h of programs) is
  treated as a long-lived cache and refreshed at most once a day (or on a manual rescan); the current
  program is recomputed against the clock each time an item is shown. Best-effort: if the cloud call
  fails, the local lineup is still fully usable. A full on-screen EPG grid is out of scope — see the
  [XMLTV Guide Data](https://github.com/Silicondust/documentation/wiki/XMLTV-Guide-Data) feed for the
  growth path.
- **Favorites** — star any channel to pin it; a **Favorites** node appears at the top of the
  HDHomeRun tile listing your starred channels (with their live program titles).
- **Hide / unhide channels** — a "Manage hidden…" affordance (surfaced in settings) lets you suppress
  channels you never watch from the browse and search lists. Hidden channels are grouped under
  *Channels* in the editor and persist per instance. (A channel you favorited then hid still shows
  under Favorites, by design.)
- **Unavailable badge (retryable)** — if a channel fails to play (all tuners busy, brief signal loss),
  it stays visible and fully playable but is marked with a small ⊘ badge. Just click Play to retry; a
  successful play clears the badge automatically. A **Rescan** also clears all badges.
- **Refresh** — "Rescan library" force-refreshes the lineup from the tuner (and the guide). The catalog
  is cached to the instance directory and auto-refreshes after the configured freshness window.

Multiple instances are supported, so you can add more than one HDHomeRun device.

## Settings

| Setting | Purpose |
|---|---|
| **Tuner address** | The HDHomeRun tuner's IP address or hostname, e.g. `192.168.14.31`. A scheme (`http://`) is optional. |
| **Fetch guide data** | Pull channel icons and program info from the SiliconDust guide service (used for icons + the current-program title). Off keeps everything local. |
| **Lineup freshness (minutes)** | How long the downloaded lineup stays fresh before an auto-refresh. `0` = until you manually rescan. |

No account or external tool is required for the basics. The Phase 2 guide data uses SiliconDust's
public guide service, which is free for HDHomeRun owners (2 days of data without a DVR subscription).

## Authoring notes

Built against `Phosphor.Plugin.Abstractions` (compile-only) per the
[Plug-in Authoring Guide](../docs/PLUGIN_AUTHORING_GUIDE.md). It implements `IBrowsable`,
`ITextSearchCapable`, `IPlayableResolver`, `IRefreshable`, `IReplayableById`, `IFavoritable` +
`IFavoriteCapture` (channel favorites), `IHideable` (hidden channels), and `IPlaybackReportable` +
`IPlaybackSuccessReportable` (retryable ⊘ unavailable badge that self-heals on a successful play), and
targets plain `net8.0` with no UI dependencies.

### Phase roadmap

- **Phase 1 (done)** — scaffold the plug-in, vet discovery (`/discover.json`), read the lineup
  (`/lineup.json`), and play channels.
- **Phase 2 (done)** — incorporate SiliconDust guide data: **channel icons**, plus the **current
  program title** appended to each channel (shown in the channel list and now-playing), cached ~24h
  (see `HdhrGuideClient`). Added **favorites** and **hide/unhide** channel management.
- **Future** — a richer on-screen EPG (upcoming programs / grid) via the guide schedule already
  cached, and/or the officially-documented
  [XMLTV feed](https://github.com/Silicondust/documentation/wiki/XMLTV-Guide-Data).
