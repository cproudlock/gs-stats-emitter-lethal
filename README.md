# gs Lethal Company Stats Emitter

A BepInEx plugin that captures Lethal Company session data and POSTs it to
`gs.proudtech.net/api/lethal/ingest`. Designed for personal stats; not for
public servers.

## What it captures

- Session: seed, host, days survived, quotas met, final credits, outcome
- Per day: moon, weather, scrap collected, deaths, credits in/out
- Per player: deaths, scrap value collected, distance traveled, items picked up
- Per death: cause, killer, position, day

## Setup

1. Install via r2modman (Lethal Company community) once published, OR drop
   `GsLethalStatsEmitter.dll` into `BepInEx/plugins/cproudlock-GsLethalStatsEmitter/`
2. On first launch the mod creates `BepInEx/config/net.cproudlock.gslethalstatsemitter.cfg`
3. Edit it to set `[Ingest].Url` and `[Ingest].Token`
4. Restart the game

Only the host should run this mod with valid credentials — co-op clients can
have it installed but their copies POST to their own gs (or stay disabled).

## License

Personal use.
