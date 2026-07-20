# Phosphor.Plugins.Twitch

A source plug-in that browses and plays **Twitch** content. Loaded dynamically from the host's
`plugins/Twitch/` folder; references only the `Phosphor.Plugin.Abstractions` contract (compile-only).

> ⚠️ **Experimental.** Discovery rides Twitch's *unofficial* public GraphQL endpoint (the same one
> Twitch's own web frontend uses). It's keyless and works today, but Twitch can change it without
> notice — treat browse/search failures as "discovery temporarily unavailable".

## How it works

- **Browse: Favorites + Pinball + Categories + Top Live.** Opening the Twitch tile shows four nodes:
  - **Favorites** — items you've starred.
  - **Pinball** — the curated pinball channels (see Settings). Any that are **live right now** appear
	as playable live items at the top; each channel also gets a sub-node listing its recent **VODs**.
  - **Categories** — Twitch's top categories/directories ordered by viewers (Just Chatting, Music,
	Art, IRL, specific game titles, …) — the same directories that back the home-page groupings
	(Games, IRL, Music & DJs, Creative, Esports). Drilling into a category lists its **live streams**,
	paged.
  - **Top Live** — Twitch's current top live streams, **paged** (scroll to "load more").
- **Search: the host search box** queries Twitch's live channels.
- **Favorites: pin the *channel* with the star.** Twitch VODs expire quickly (days–weeks), so the
  star always favorites the **channel**, never a specific video id. Star any row — a live stream OR a
  VOD — and its owning channel is pinned to the **Favorites** node (and every other row from that
  channel shows starred too, for a consistent view). Favorites are stored per-instance in
  `favorites.json` keyed by channel login (permanent). A favorited channel appears as a **collection
  you drill into** (its videos), tagged **● LIVE** when it's currently broadcasting; opening it lists
  the channel's VODs with the **current live stream injected as the first item** (flagged
  `IsLiveStream` so the host can badge it). Clicking the channel tile browses — it never tries to play
  an offline channel.
- **Playback: bundled yt-dlp.** Streams are resolved by shelling out to the host-bundled `yt-dlp`
  (via `IPluginHost.GetToolPath("yt-dlp")`), reusing yt-dlp's mature Twitch extractors
  (`twitch:stream`, `twitch:vod`). The GQL layer only *finds* content; yt-dlp *resolves* it at play
  time (deferred, see below).
- **Live vs. VOD.** Live channels resolve to an endless HLS manifest flagged
  `ResolvedStream.IsLiveStream = true`, so the host renders them "tuner"-style (`M:SS / *`, no scrub
  bar, no auto-advance) — the same path SiriusXM/iHeartRadio use. VODs are finite and seekable like
  ordinary video.

## Capabilities

- `IBrowsable` — Favorites + Pinball (curated channels, live + VODs) + Categories + Top Live.
- `IPagedBrowsable` — lazy "load more" for Top Live, per-category live streams, and per-channel VODs
  (Twitch GQL is cursor-based; the source maps the host's offset paging onto forward cursors).
- `ITextSearchCapable` — Twitch live-channel search.
- `IFavoritable` — star toggle that pins the owning **channel** (not the video), keyed by login so it
  never goes stale as VODs expire; re-checks live/offline on open.
- `IPlayableResolver` + `IDeferredStreamResolution` — resolves a stream lazily **at play time** (one
  yt-dlp probe on play, not one per row) so browse/search stay fast.
- `IConnectionTestable` — checks GQL reachability and yt-dlp availability.
- `IExperimental` (on the provider) — surfaces the experimental badge in the Plug-ins settings tab.

## Settings

| Key | Type | Notes |
|---|---|---|
| `channels` | Text (multiple) | Curated pinball channel logins, one per line (e.g. `deadflip`). A full `twitch.tv/<login>` URL is also accepted and reduced to the login. |
| `quality` | Enum | `Low` / `Medium` / `High` / `Max` height ceiling. |
| `liveIndicator` | Bool | Decorate a currently-broadcasting channel's live feed with a red corner dot on its thumbnail (via the source-agnostic `SourceItem.ShowLiveBadge` hint). Default on. |

Ships with a few pinball channels seeded by default (`deadflip`, `buffalopinball`,
`straightdownthemiddle`, `foxcitiespinball`, `mpt3k`) — edit the list freely. Multi-instance: add
several Twitch tiles if you want more than one.

## Limitations

- Subscriber-only, geo-restricted, or deleted content cannot be resolved.
- Live streams carry Twitch's server-side ads (unavoidable client-side).
- The keyless GQL endpoint is unofficial and may break; no account/credentials are required or used.
