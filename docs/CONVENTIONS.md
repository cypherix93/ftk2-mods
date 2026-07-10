# Conventions — how every mod in this repo is built

## The engine philosophy (non-negotiable design rule)

Every mod is split into two layers:

1. **Engine (C# plugin)** — implements the *mechanics*: Harmony patches, runtime state, algorithms.
   The engine hardcodes **no content and no tuning numbers**.
2. **Data surface (JSON + BepInEx config)** — everything a designer would ever want to change:
   content definitions, weights, formulas' coefficients, feature toggles. Changing behavior must not
   require recompiling.

Rules of thumb:
- If a value could plausibly be tuned during balancing, it is a **knob** (BepInEx config entry or data field).
- If a concept could plausibly have more instances later (a gem, an AI profile, a quest verb, an evolution
  chain), it is a **data-driven registry** loaded from a JSON folder, with new files/entries picked up
  automatically.
- Engines expose **extension points** documented in their SPEC (e.g. "drop another `*.brain.json` in
  `Profiles/` to add an AI personality").
- Follow the game's own patterns where they exist (e.g. `Inherits` for ability configs, id → config
  dictionaries, `Tags` arrays, `SKILL_*`/`STATUS_*` id namespaces). Mod ids are prefixed per-mod
  (see Naming below).

## Repo layout (per mod)

```
FTK2.<ModName>/
  SPEC.md            # the design spec (see template below)
  data/              # example/starting dataset, shipped with the mod
    ...
  src/               # C# plugin source (created at implementation time)
  README.md          # short: what it does, install, key knobs (optional at spec stage)
```

## SPEC.md template (all specs follow this structure)

1. **Purpose & scope** — one paragraph; what it does, what it deliberately does not do.
2. **Player-facing behavior** — what changes in-game, described as a player would see it.
3. **Architecture** — engine vs data split; runtime flow; state lifecycle (per-battle / per-run / persistent).
4. **Data file formats** — full schemas of every JSON the engine loads, with a commented example of each.
   This is the extensibility contract.
5. **Knobs** — BepInEx config entries: `[Section] Key (type, default) — description`.
6. **Patch targets & integration points** — exact FTK2.dll classes/methods (verbatim from
   `docs/research/game-code-reference.md`), what kind of patch (prefix/postfix), and why.
7. **Example starting dataset** — describes the files in `data/` and what each demonstrates/tests.
8. **Testing plan** — concrete in-game verification steps; what to log; edge cases.
9. **Save & multiplayer considerations** — persistence strategy, host-authority, desync risk.
10. **Milestones** — M1 (MVP) → M2 → M3, each independently shippable.
11. **Open questions** — decisions deferred to review.

## Naming

- Plugin GUIDs: `ftk2mods.<modname>` (e.g. `ftk2mods.warbrain`).
- Mod content ids: uppercase-underscore with per-mod prefix: `WB_*` (WarBrain), `CF_*` (ClassForge),
  `FRG_*` (Forge), `RW_*` (Runeworks), `SMN_*` (Summoner), `QS_*` (Questsmith), `VNU_*` (Venue),
  `AP_*` (ActionPoints), `DK_*` (DevKit).
- Data files: `<thing>.json` inside per-purpose subfolders of `data/`.
- Localization: mods that add player-visible strings ship `Localization/en.json` in the EOR style
  (`ID` and `ID_DESCRIPTION` keys).

## Technical baseline

- Target: BepInEx 5.4.23, .NET Framework (Mono), HarmonyLib 2.x, game assembly `FTK2.dll`
  (reference `E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed\`).
- JSON parsing: prefer the game's own `JsonHelper`/`System.Text.Json` (Newtonsoft also present in Managed).
- Manual Harmony patching (`AccessTools` + `Harmony.Patch`) with a "Target found: X" log line per target,
  EOR-style, so breakage after game updates is diagnosable from logs.
- Every engine has a master `Enabled` knob and fails safe: if its data fails to parse, log loudly and
  leave vanilla behavior untouched.
- Per-run persistent state piggybacks `GameRunData` (EOR's Nemesis system is the proven pattern);
  per-battle state lives on the plugin and resets on combat end.

## Save & multiplayer defaults

- **Co-op multiplayer is a hard requirement** (owner decision 2026-07-10). Every mod is designed
  MP-first per `docs/MULTIPLAYER.md` — parity handshake (R1), determinism of generated content (R2),
  host authority for decisions (R3), presentation exemption (R4), dev-mutation lockout (R5).
- Every SPEC §9 declares: parity class (`ALL_PEERS`/`HOST_ONLY`/`LOCAL`), a `[SYNCED]`/`[LOCAL]`
  feature table, determinism inventory, sync surface, SafeMode definition, and an MP smoke test.

## Testing

- Primary loop: edit data → `FTK2.DevKit` hot-reload → observe in-game (until DevKit exists: restart).
- Each spec's testing plan must be executable by a human in < 15 minutes with the shipped example data.
- Engines log decisions at `LogLevel.Debug` behind a `VerboseLogging` knob.
