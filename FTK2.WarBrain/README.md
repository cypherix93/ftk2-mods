# FTK2.WarBrain

Enemy battle-AI engine for For The King II: replaces the vanilla random-pick enemy AI with a
data-driven utility scorer (JSON brain profiles, doctrines, tunable difficulty knobs) plus an
optional enemy stat-scaling layer. See [SPEC.md](SPEC.md) and
[REPORT.md](REPORT.md) (discovery + simulation evidence).

## Layout

```
src/WarBrain.Core/      # host-agnostic scoring engine (netstandard2.0, decimal math)
src/WarBrain.Plugin/    # BepInEx 5 plugin (net472) — Harmony patches + live-game adapter
sandbox/WarBrainSim/    # combat simulator: vanilla-AI replica vs WarBrain, experiment runner
data/                   # brain profiles, doctrines, assignments, memory reactions (JSON)
```

## Build

```
dotnet build src/WarBrain.Plugin -c Release          # plugin + core
dotnet run --project sandbox/WarBrainSim -c Release  # re-run the experiment matrix
```

Game path defaults to `E:\Games\Steam\steamapps\common\For The King II`; override with
`-p:GameDir="..."`.

## Install (manual, until a packaging step exists)

Copy to `<game>\BepInEx\plugins\FTK2.WarBrain\`:
- `src/WarBrain.Plugin/bin/Release/net472/FTK2.WarBrain.dll`
- `src/WarBrain.Plugin/bin/Release/net472/WarBrain.Core.dll`
- the whole `data/` folder (must sit next to the dll: `...\FTK2.WarBrain\data\Profiles\...`)

Config appears at `BepInEx\config\ftk2mods.warbrain.cfg` after first launch.

## Key knobs

| Knob | Default | Effect |
|---|---|---|
| `[General] Enabled` | true | master switch — false restores vanilla AI exactly |
| `[General] VerboseLogging` + `LogDecisionBreakdown` | false | per-turn score breakdowns (primary tuning tool) |
| `[Difficulty] GlobalIntelligenceScalar` | 1.0 | scales all consideration weights |
| `[Difficulty] GlobalTemperatureMultiplier` | 1.0 | higher = more varied/suboptimal picks |
| `[Difficulty] GlobalMistakeChanceAdd` | 0.0 | chance of a uniformly random legal move |
| `[Assignments] DefaultFocusPolicy` | (per-profile) | `SMART` / `MAX` / `NONE` focus spending |
| `[Scaling] EnableEnemyScaling` | **false** | tunable enemy ATK/HP/ACC/FOC layer (sim-validated defaults ×1.25/×1.25/+8/+1) |

## Multiplayer (read this)

Enemy AI in FTK2 is **deterministic lockstep on every peer** (verified by decompilation —
`docs/research/battle-ai-deep-dive.md` §3). Every peer in a co-op session must run the same
WarBrain version, identical `data/`, and identical `[Difficulty]`/`[Scaling]` config values,
or combat desyncs on the first enemy turn. The plugin logs a `dataHash` at startup for manual
comparison; automated parity enforcement lands with the repo-wide ParityService.
