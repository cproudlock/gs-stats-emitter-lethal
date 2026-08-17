# GsLethalStatsEmitter

A BepInEx plugin for **Lethal Company** that POSTs per-session stats to a self-hosted [gs](https://gs.proudtech.net) dashboard. Source: <https://github.com/cproudlock/gs-stats-emitter-lethal>.

## Sittings and playthroughs

A Lethal Company run is not one sitting. You save, quit, and come back another
evening, and that is still the same run. So the mod reports a **sitting** (one
lobby session) and the dashboard groups sittings into a **playthrough** (one save
file), using absolute in-game day numbers rather than a per-sitting counter.

Where the grouping comes from:

- **Hosting**: the mod stamps a `gsPlaythroughId` into Lethal Company's own save
  file the first time it sees it, so every later sitting on that save reports the
  same playthrough exactly. The key is cleared when the run truly ends.
- **Joining someone else's lobby**: a client cannot read the host's save, so the
  dashboard stitches sittings together server-side from the host's Steam id plus
  continuing day numbers.

While you play, the mod also posts a progress snapshot every
`EmitIntervalSeconds` (default 120). Each snapshot is a complete cumulative
document that replaces the previous one, so a crash costs one interval instead of
the entire sitting.

## What it captures

- **Session**: seed, host, days survived, quotas met, final quota/credits, peak + total scrap value, quota margin, outcome (`quota_failed` / `all_dead` / `abandoned` / `company_left`)
- **Per day**: moon, weather, scrap value collected + left behind, scrap pieces, apparatus pulled, credits in/out, deaths, returned-to-company
- **Per player**: deaths, scrap **collected** (picked up), scrap **delivered** (got it aboard the ship, the contribution that actually counts), scrap **sold** (that delivered scrap's share of the Company payout), meters traveled (bucketed by weather), items picked up + total value, terminal purchases (consumables + ship unlockables), enemies killed

  Selling is a crew action and anyone can carry scrap from the ship to the desk,
  so the payout is credited back to whoever hauled each item aboard rather than to
  whoever walked it to the counter.
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

Only **one person in the lobby** should report, otherwise the same sitting is
uploaded twice. By default that is the host (`EmitOnHostOnly = true`). A client
can report instead by setting `EmitOnHostOnly = false`, which works because every
player's pickups, deliveries and deaths are captured from the networked events
rather than from local-only ones. Playthrough grouping is exact when the host
reports and stitched heuristically when a client does.

## Getting an ingest token

The dashboard owner mints a `gsk_*` key for each friend at `/admin/api-keys` on the gs instance. Ask them for one. The key is yours, do not share it. If you ever leak it, the owner can revoke and re-mint in a few seconds.

## Privacy

Payloads include your Steam display name and the names of everyone else in the lobby. Self-host gs if that matters to you.

## License

Personal use.
