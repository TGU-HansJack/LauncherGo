# LauncherGo AntiCheat

Server-only behavior analysis for Vintage Story 1.22.3. No client mod, file hash,
or `modinfo.json` attestation is required.

The first deployment creates `ModConfig/launchergoanticheat.json`. Defaults are
disabled and monitor-only. Enable the detector with `/anticheat enable`; keep
`MonitorOnly` enabled until thresholds have been calibrated against the server's
actual mods, latency, mounts, tools, and player skills.

Evidence is written to:

`ModData/LauncherGoAntiCheat/alerts-YYYY-MM-DD.jsonl`

When the warning score and cooldown allow an administrator notification, the
evidence is logged with the `[LauncherGoAntiCheat] ALERT` prefix so LauncherGo
can route it to the QQ groups bound to that server profile. Enforcement actions
use `[LauncherGoAntiCheat] ACTION`.

## Whitelist

Whitelist rules are detector-scoped. An empty `Detectors` list matches nothing;
use an explicit detector id, a category such as `movement.*`, or `*` only when a
full exception is intentional.

`Bypass: true` skips only the listed detectors. `Bypass: false` leaves detection
active and applies `SpeedMultiplier` / `ActionRateMultiplier`, which is preferable
for skill, airship, CarryOn, and automation-assist compatibility.

Example limited rule:

```json
{
  "Enabled": true,
  "Bypass": false,
  "Id": "airship01",
  "PlayerUid": "player-uid-here",
  "PlayerName": "",
  "Role": "",
  "Groups": [],
  "Detectors": ["movement.speed", "movement.flight"],
  "Contexts": ["mounted"],
  "ExpiresAtUtc": null,
  "SpeedMultiplier": 3.0,
  "ActionRateMultiplier": 1.0,
  "Reason": "Approved airship compatibility",
  "CreatedBy": "admin"
}
```

In-game commands:

```text
/anticheat status
/anticheat reload
/anticheat enable
/anticheat disable
/anticheat monitor on|off
/anticheat whitelist list
/anticheat whitelist add <player> <detectorsCsv> [minutes] [reason]
/anticheat whitelist limit <player> <detectorsCsv> <speedMultiplier> <actionMultiplier> [minutes] [reason]
/anticheat whitelist remove <ruleId>
```

## Evidence boundaries

Server-authoritative signals include impossible displacement, unauthorized flight
or no-clip controls, sustained solid-block collision, reach, and resistance-aware
fast breaking.

Automation cadence, no-fall, ore ratios, high healing rate, multi-target combat,
and trader interaction rate are statistical signals. They warn and record evidence
but do not contribute to automatic punishment unless
`PunishStatisticalDetections` is explicitly enabled.

Passive fullbright, GUI enhancements, admin detection, ESP, and map rendering of
data already sent to the client cannot be proven by behavior-only server analysis.
The mod does not claim to detect or auto-punish those client-only features. Xray is
limited to mining-path/ore-ratio inference.
