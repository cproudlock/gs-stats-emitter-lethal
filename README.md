# GsLethalStatsEmitter

A BepInEx plugin for **Lethal Company** that POSTs per-session stats to a self-hosted [gs](https://gs.proudtech.net) dashboard. Source: <https://github.com/cproudlock/gs-stats-emitter-lethal>.

## What it captures

- **Session**: seed, host, days survived, quotas met, final quota/credits, peak + total scrap value, quota margin, outcome (`quota_failed` / `all_dead` / `abandoned` / `company_left`)
- **Per day**: moon, weather, scrap value collected + left behind, scrap pieces, apparatus pulled, credits in/out, deaths, returned-to-company
- **Per player**: deaths, scrap value collected, meters traveled (bucketed by weather), items picked up + total value, terminal purchases (consumables + ship unlockables), enemies killed
- **Per death**: cause of death, killer, moon, position, timestamp
- **Loaded mods**: name, author, version, folder (so the dashboard can link to each on Thunderstore)

## Setup

1. Install via r2modman (Lethal Company community), OR drop `GsLethalStatsEmitter.dll` into `BepInEx/plugins/cproudlock-GsLethalStatsEmitter/`
2. Launch once. The mod creates `BepInEx/config/net.cproudlock.gslethalstatsemitter.cfg`
3. Edit it:

```ini
[Ingest]
Url = https://gs.proudtech.net/api/lethal/ingest
Token = gsk_xxxxxxxxxxxxxxxxxxxxxxxx
```

4. Restart Lethal Company

Only the **host** of a multiplayer session needs to run the mod. Co-op clients can have it installed and disabled (or leave their `Token` blank) without breaking anything.

## Getting an ingest token

The dashboard owner mints a `gsk_*` key for each friend at `/admin/api-keys` on the gs instance. Ask them for one. The key is yours, do not share it. If you ever leak it, the owner can revoke and re-mint in a few seconds.

## Privacy

Payloads include your Steam display name and the names of everyone else in the lobby. Self-host gs if that matters to you.

## License

Personal use.
