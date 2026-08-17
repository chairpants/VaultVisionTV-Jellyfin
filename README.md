# VaultVisionTV for Jellyfin

A Jellyfin plugin that brings [VaultVisionTV](https://github.com/chairpants/VaultVisionTV)'s
simulated 90s cable channels into Jellyfin's native Live TV system — 64
channels streamed from archive.org, each running its own deterministic,
wall-clock-driven schedule, so tuning in joins a show already in progress,
the same way for every viewer at once. Works on every Jellyfin client (web,
Android TV, tvOS, Roku, Samsung/LG apps), because it plugs into Jellyfin's
Live TV data model rather than shipping its own UI.

This is a real installed C# plugin, but it integrates with Live TV the way an
M3U tuner does — it self-hosts an M3U playlist, an XMLTV guide, and live
MPEG-TS stream endpoints, and you point Jellyfin's built-in "M3U Tuner" Live
TV source at those URLs.

## Status: Phase 1

- [x] Channel lineup + deterministic scheduler, ported from VaultVisionTV's
      `channels.js` / `scheduler.js`
- [x] Catalog fetch/cache from the published `catalog.json`
- [x] M3U + XMLTV endpoints
- [x] Live tune-in (ffmpeg seeks to the schedule's current offset)
- [ ] Seamless chaining across slot boundaries (a channel's stream currently
      ends when the episode does, rather than continuing to the next
      scheduled item)
- [ ] Commercial-break padding / idle ffmpeg process teardown
- [ ] VOD browsing, channel logos (not planned for this phase)

## Building

Requires the **.NET 9 SDK** (Jellyfin 10.11.x targets `net9.0`, not the older
`net8.0` some plugin templates still default to).

```bash
dotnet build Jellyfin.Plugin.VaultVisionTV/Jellyfin.Plugin.VaultVisionTV.csproj -c Release
```

Output DLL: `Jellyfin.Plugin.VaultVisionTV/bin/Release/net9.0/Jellyfin.Plugin.VaultVisionTV.dll`

## Installing

### Option A — plugin repository (recommended, no NAS filesystem access needed)

1. Dashboard → Plugins → **Repositories** → Add:
   `https://raw.githubusercontent.com/chairpants/VaultVisionTV-Jellyfin/main/manifest.json`
2. Dashboard → Plugins → **Catalog** → find **VaultVisionTV** (category "Live
   TV") → Install.
3. Restart the Jellyfin server (Dashboard prompts for this).
4. Dashboard → Plugins should show **VaultVisionTV** as active.

Each new release gets a new entry appended to `manifest.json`'s `versions`
array — Jellyfin's catalog then offers it as an update, same as any other
plugin.

### Option B — manual copy

Jellyfin loads plugins from a folder inside its config volume, one
subdirectory per plugin:

```
<jellyfin config volume>/plugins/VaultVisionTV_0.1.0.0/Jellyfin.Plugin.VaultVisionTV.dll
```

Build in Release mode (above), copy the `.dll` into that folder (over
SSH/rsync/your NAS's file manager), restart the container.

### Either way

Check `docker logs` for the container if the plugin doesn't show up in
Dashboard → Plugins — a load error there usually means an ABI/SDK mismatch.

## Wiring up Live TV

The plugin's config page (Dashboard → Plugins → VaultVisionTV) shows the
exact URLs for your server. In short, under Dashboard → Live TV:

1. **Add a Tuner Device** → M3U Tuner → URL: `http://<server>:8096/VaultVisionTV/iptv/channels.m3u`
2. **Add a TV Guide Data Provider** → XMLTV → URL: `http://<server>:8096/VaultVisionTV/iptv/epg.xml`

Channels should appear with a live guide grid. Tuning one should join
whatever's currently scheduled, mid-program.

## Releasing a new version

`jprm` (Jellyfin Plugin Repository Manager) is the standard tool for this but
was unreliable in testing (silently no-op'd rather than erroring). Until
that's sorted out, cut a release by hand:

```bash
VERSION=0.1.0.0   # bump this — also update it in build.yaml

dotnet publish Jellyfin.Plugin.VaultVisionTV/Jellyfin.Plugin.VaultVisionTV.csproj \
  -c Release -f net9.0 -o /tmp/vvtv-publish

cd /tmp
zip -j vvtv-plugin.zip vvtv-publish/Jellyfin.Plugin.VaultVisionTV.dll vvtv-publish/Jellyfin.Plugin.VaultVisionTV.pdb
CHECKSUM=$(md5 -q vvtv-plugin.zip | tr 'a-f' 'A-F')   # Jellyfin expects hex MD5 of the zip

gh release create v$VERSION vvtv-plugin.zip --title "v$VERSION" --notes "..."
```

Then append a new entry to `manifest.json`'s `versions` array (highest
version first) with the new `version`, `sourceUrl` (the release asset URL
`gh release create` prints), `checksum` ($CHECKSUM above), and a fresh
`timestamp`, and push. Jellyfin's plugin catalog picks up the new version on
its next repository check.

## Layout

| Path | Role |
|---|---|
| `Plugin.cs` | Plugin entry point, config page registration |
| `PluginServiceRegistrator.cs` | DI wiring |
| `Configuration/` | `PluginConfiguration.cs` + `configPage.html` (Dashboard settings page) |
| `Domain/` | Data models — `Channel`/`DaypartWindow` (from `channels.js`), `CatalogData`/`Show`/`Episode` (matches VaultVisionTV's `catalog.json` shape), `SchedulePosition` |
| `Data/channels.json` | The channel lineup, generated from VaultVisionTV's `channels.js` (its "guide" and "vod" kind entries are excluded — Jellyfin renders its own guide grid, and VOD browsing is out of scope for this phase) |
| `Services/SchedulerService.cs` | Port of `scheduler.js` — deterministic scheduling math |
| `Services/CatalogService.cs` | Fetches/caches the published `catalog.json` |
| `Services/ArchiveOrgResolver.cs` | Port of `player.js`'s `resolveEpisodeUrl` |
| `Services/EpgService.cs` | M3U + XMLTV generation |
| `Services/StreamService.cs` | ffmpeg live-join pipeline |
| `Api/IptvController.cs` | `channels.m3u` / `epg.xml` / `stream/{channel}` endpoints |
| `Api/AdminController.cs` | Config-page-only "refresh catalog now" action |
