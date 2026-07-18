# Phosphor.Plugins.Vimeo

A source plug-in that browses and plays **Vimeo** videos. Loaded dynamically from the host's
`plugins/Vimeo/` folder; references only the `Phosphor.Plugin.Abstractions` contract (compile-only).

## How it works

- **Browse: curated channels + Vimeo's categories.** Opening the Vimeo tile shows a **Favorites**
  node, curated **channels** (**Staff Picks** — Vimeo's flagship curation), and Vimeo's top-level
  **categories** (Animation, Music, Documentary, Experimental, ...). Drilling into any of them lists
  its videos, **paged** — scroll to "load more".
- **Search: the host search box** queries Vimeo's public catalog.
- **Favorites: pin videos with the star.** Star any video (via `IFavoritable`) to pin it to the
  **Favorites** node. This replaces the old "paste URLs" model — you find videos by browse/search and
  keep the ones you like. Favorites are stored per-instance in `favorites.json` with enough metadata
  (title, thumbnail, duration, URL) to render instantly/offline; a favorite pinned by id only is
  **enriched** on next Favorites open via `GET /videos/{id}`.
- **Playback: bundled yt-dlp.** Streams are resolved by shelling out to the host-bundled `yt-dlp`
  (via `IPluginHost.GetToolPath("yt-dlp")`), reusing yt-dlp's mature Vimeo extractor. The API layer
  only *finds* videos; yt-dlp *resolves* them at play time (deferred, see below).

## Capabilities

- `IBrowsable` — Favorites + curated channels (Staff Picks) + Vimeo category tree.
- `IPagedBrowsable` — lazy "load more" for category/channel video lists (Vimeo pages are 1-based).
- `ITextSearchCapable` — public Vimeo search.
- `IFavoritable` — star toggle to pin videos to Favorites (with id-only enrichment).
- `IPlayableResolver` + `IDeferredStreamResolution` — resolves a stream lazily **at play time** (one
  yt-dlp probe on play, not one per row) so browse/search stay fast.
- `IConnectionTestable` — validates the access token and yt-dlp availability.

## Settings

| Key | Type | Notes |
|---|---|---|
| `accessToken` | Secret | Vimeo app access token (public scope). **Required** for browse/search. |
| `quality` | Enum | `Low` / `Medium` / `High` / `Max` height ceiling |

Multi-instance: add several Vimeo tiles if you want more than one.

## The token is required (and why)

Unlike YouTube — which needs no credentials — Vimeo's API is **per-app**: there is **no keyless
discovery** endpoint, so browsing and searching require *your own* Vimeo app token. This is a Vimeo
limitation, not a Phosphor one. **The token is never embedded in the app**: each user registers a free
Vimeo app and supplies their own (each token carries its own rate limits and identity). Playback of a
pinned favorite still runs through yt-dlp, but *finding* videos needs the token.

### Getting a Vimeo access token

1. Sign in at **[developer.vimeo.com](https://developer.vimeo.com/)** and open **My Apps -> Create App**.
2. Fill in a name/description (any project works) and create it.
3. On the app page, find **Generate an access token** with the **`public`** scope (unauthenticated /
   public is enough — you do *not* need the OAuth "authorize" flow or the client secret).
4. Copy the token into the source's **"API access token"** field, then **Test connection** — it should
   report *"Token valid - browse & search enabled."*

## Limitations

- **A token is required** for browse/search — there is no keyless Vimeo discovery.
- **Private / password-protected / domain-locked** videos cannot be resolved (no raw stream URL).
- Vimeo is a filmmaker/creator platform, not a music catalog — categories reflect that (Animation,
  Documentary, ...), which suits an audio-reactive jukebox for visuals as much as music.

## Follow-ups (deferred)

- **User OAuth** (authorize/redirect flow) for the signed-in user's **private library** — likes,
  folders/showcases, and private uploads. The current token is public-scoped only.
- **More curated channels** — only Staff Picks is surfaced today; the curated-channel list is a small
  hardcoded set that could grow (or become configurable).
- **Paged search** — category/channel browse is paged; the ad-hoc search box still returns a single
  page (up to 100). Paging search results is a natural follow-on.
- `IDownloadable` for disk caching/prefetch.

## Build & deploy

Building this project self-deploys its output to the host's `plugins/Vimeo/` folder (see the
`SelfDeployToHost` target in the csproj), excluding the shared contract DLL. No host changes needed —
the host discovers it on next launch.
