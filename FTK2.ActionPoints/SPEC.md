# FTK2.ActionPoints — SPEC

Plugin GUID: `ftk2mods.actionpoints` · Id prefix: `AP_` · Priority: **P3** (highest blast radius of any mod in
this repo — touches shared combat state, UI, save data, and both AI paths). Depends on nothing; is a soft
dependency for `FTK2.WarBrain` (§6, interop).

> Everything below is grounded in `docs/research/game-code-reference.md` §2 ("Combat core" / "Action economy
> choke points") and `docs/research/data-schemas.md`. Where a decision depends on IL detail not covered by
> those documents, this spec says so explicitly and lists it in §11 rather than inventing behavior.

## 1. Purpose & scope

Replaces the vanilla fixed Primary-Action/Secondary-Action (PA/SA) slot economy with a single, data-driven
Action Point (AP) pool per combatant — the Divinity/BG3-style economy where cheap abilities cost few points,
big abilities cost more, and a bounded amount of unspent AP carries into the next round, rewarding
build-up turns. State is kept **inside the game's own existing serialized fields**
(`CombatComponent.PrimaryActions` / `.SecondaryActions`) rather than new plugin state, so save persistence
and netcode replication of the pool value come for free from the base game's own component sync.

**In scope**: the AP pool itself (grant, cap, carryover), a data-driven cost table (major/minor defaults,
per-category, per-ability, item-use, movement, focus-use modifier), gating every known choke point that
reads or writes PA/SA, a minimal cosmetic UI (repurposed pip counter + cost-in-tooltip), a public interop
surface for AI (`FTK2.WarBrain` and vanilla `AIHelper`), and a per-save opt-in switch with a documented
mid-run-toggle story.

**Out of scope**: no new abilities, statuses, or classes; no ability roster rebalancing beyond the cost
table; no new venue/targeting mechanics; a detect → resync-from-host → session-warning netcode
reconciliation protocol (§9.4) ships in M1/M2, not deferred past it; no bespoke pip artwork in M1
(cosmetic-first: reuse `eSpriteIcons.PA` before commissioning anything new).

## 2. Player-facing behavior

- Each combatant's action-economy readout becomes **one pip row** ("AP: 5/8") instead of separate
  primary/secondary icons. The vanilla secondary-action pip row is hidden (M1) rather than duplicated,
  since both fields now carry the same pool value (§3).
- Ability buttons show an **AP cost badge** and grey out when unaffordable, same as vanilla greys out
  actions with no PA/SA left — just checked against the pool instead of the two counters.
- Ability tooltips gain a cost line, e.g. "Cost: 3 AP" (plus "+1 AP if focused" when the ability is
  focusable and the focus-use modifier applies).
- **Carryover is the headline feature**: unspent AP up to a per-preset cap (default 2, Divinity preset 3)
  rolls into next round's grant instead of evaporating at turn end. Skipping a turn to bank AP for a
  bigger following turn becomes a real tactical choice.
- Enemies use the same pool by default (Symmetric knob); a server/host can flip to Asymmetric so bosses/
  elites get their own (typically flatter, no-carryover) pool tuned independently of the player-facing curve
  — demonstrated by the shipped `CostModel.divinity.json` preset.
- Nothing changes about targeting, movement rules, or which abilities exist — only what they cost to use.
- The mod is an explicit **per-save opt-in**: enabling the BepInEx knob does not retroactively change a
  save already in progress; the choice is locked in at run creation and a warning is logged/shown so the
  player understands mid-run toggling is unsupported (§9).

## 3. Architecture

### 3.1 Field-mapping options (the core design decision)

All PA/SA choke points operate on two plain serialized ints on `CombatComponent`:
`PrimaryActions`, `SecondaryActions`. **This is now the load-bearing MP design decision for the whole mod,
not a save-persistence convenience** — see §9.1 for the full argument. A fourth option, keeping the AP pool
in mod-side state (a `Dictionary<EntityId, int>` or equivalent side-table in plugin memory, decoupled from
any vanilla-serialized field), was considered and is **formally rejected**: it is invisible to the base
game's replication, so multiplayer consistency would require this mod to reinvent network transport for its
single highest-frequency value (every ability spend, every turn grant) — exactly the failure mode
`docs/MULTIPLAYER.md` R3 warns against ("prefer host-decides + vanilla-replicates over custom sync").
Three ways to host the AP pool *inside the vanilla fields* were considered:

**Option A — Mirrored single pool (recommended, M1).**
`PrimaryActions` is the live AP pool; every write path also writes the identical value into
`SecondaryActions` through one shared setter. Any not-yet-patched vanilla code that still reads either
field alone (a stray UI binding, a save-inspection tool, another mod) sees a **consistent** number, not a
half-migrated one. Cost: every gate/spend choke point must be fully replaced (not arithmetically
corrected), because vanilla's own PA-vs-SA split logic no longer means anything — but the spec's scope
already requires patching every one of those choke points anyway (§6), so this is not additional work.
This is also the only option compatible with MP at `ALL_PEERS` parity without a bespoke sync protocol
(§9.1) — the mirrored value is exactly what the base game's `CombatComponent` replication already carries
for every peer, for free.

**Option B — Split pool + spent-counter.** `PrimaryActions` = pool, `SecondaryActions` = "AP spent this
turn" for UI/analytics. Rejected: doubles the number of fields that must stay coherent across save/load and
netcode sync, and any vanilla or third-party code that still writes `SecondaryActions` directly (haste/slow
`Stats{SA}` deltas, `TryClearEntityActions`) corrupts the counter unless *also* patched — no benefit over
just logging spend events through the existing `VerboseLogging` knob.

**Option C — Split pool + carryover bank.** `PrimaryActions` = spendable pool this turn, `SecondaryActions`
= banked/carryover AP not yet pulled in. Elegant in principle (gives the vanilla secondary pip a real,
distinct meaning: "AP banked for next round") but risky: any unpatched or partially-patched gate that reads
`SecondaryActions > 0` as "you have an action available" (e.g. `_isValidInventoryOption` if its exact
predicate turns out to be `PA>0 || SA>0`, unverified — see §11) would let players spend banked AP for free.
Given this mod's P3 blast-radius rating, that risk is deferred: **Option C is a candidate M3+ UI
enhancement** (show the carryover bank as a visually distinct secondary pip) implemented only after every
gate in §6 is confirmed to route through the shared `ActionPointsService.CanAfford` check, never through a
raw field read.

**Decision: ship Option A for M1–M3 — non-negotiable for MP, not merely a recommendation.** Options B, C,
and the rejected mod-side-dictionary alternative above all either desync-risk a raw field read or require a
custom sync protocol this mod does not implement; Option A is the only one where `ALL_PEERS` parity
requires nothing beyond R1's ordinary data-hash enforcement. Revisit Option C only as a cosmetic layer once
the gate/spend audit is field-verified, and only if it does not compromise the mirrored-field MP guarantee.

### 3.2 Runtime flow

1. **Grant** — at every one of the 7 verified `ResetCharacterActions` call sites (§6), a postfix recomputes
   the pool from `CostModel.APPerTurn` (base + SPD bonus + carryover-in, capped at `MaxPool`) and writes it
   through the Option-A mirrored setter. A lightweight context tag (set by a lightweight prefix on each of
   the 7 callers, cleared after the postfix reads it) tells the postfix *which* call site triggered the
   reset, because callers are not homogeneous — a true turn boundary (`NextTurn`, `_nextTurn`) should apply
   carryover; a fresh combat entrant (`_addEntityToCombat`) must start at 0 carryover (nothing to carry
   from yet); a skill-proc-triggered reset (`ApplyAction`) or a tarot draw (`OnTakeTarotCard`) may represent
   "gain an extra action" rather than "your turn started over" and should not clobber an already-larger
   pool down to the base grant. See §6 for the per-caller policy table and §11 for what remains unverified
   about the two `ResetCharacterActions` overload signatures.
2. **Gate** — `IsUsableAbility`, `IsTurnOver`, `_characterCanUseItem`, `_isValidInventoryOption`, and
   `CombatAbilitiesTemplateHelper.Show` are all replaced (not arithmetically patched) to ask
   `ActionPointsService.CanAfford(...)` against the pool and the resolved cost (§4 precedence rules),
   instead of running vanilla's PA/SA-split arithmetic.
3. **Spend** — `PerformAbility` / `_performAbility` deduct the resolved cost (including the focus-use
   modifier when `CombatDecisionData.FocusUsed` is true) from the pool through the mirrored setter.
4. **Misc touchers** (`TickActiveEntityCharacterStatus`, `ApplyStatChange`, `_startPhaseEvent`,
   `_tryStartBossPhase`, `TryClearEntityActions`) directly manipulate the raw fields in vanilla, *without*
   going through `ResetCharacterActions` (per game-code-reference.md's own grouping — these are listed
   separately from the reset callers). Each gets its own postfix: `ApplyStatChange` reinterprets a raw
   `Stat=PA`/`Stat=SA` delta via `LegacyStatConversion` (§4) instead of letting the literal int land in the
   pool; the other four get a cheap **resync-and-log safety net** — after vanilla runs, force
   `SecondaryActions = PrimaryActions` and, if `VerboseLogging`, log the observed raw delta so a human can
   decide in a later milestone whether that specific site also needs `LegacyStatConversion` scaling. This
   defense-in-depth is deliberate: several of these methods' exact internal semantics are unverified
   (§11), and a P3 mod should degrade to "numbers stay consistent, logged for review" rather than silently
   drifting.

### 3.3 State lifecycle

- **Per-battle**: the pool value lives in `CombatComponent.PrimaryActions`/`.SecondaryActions` like vanilla
  PA/SA — no new plugin-side per-entity state. Resets naturally at combat end because the component itself
  is per-combat-entity.
- **Per-run (persistent)**: one bool, `AP_ModeEnabled`, piggybacked onto `GameRunData` (the proven
  Nemesis-style save-piggyback pattern, per CONVENTIONS.md and the EOR precedent) — captured once at run
  creation from the live `Enabled` knob and never re-read from the knob afterward. This is what makes the
  opt-in "per-save" rather than "per-launch" (§9).
- **Not persisted across runs**: nothing. Each new run re-asks via the knob's current value at creation
  time.
- **Plugin runtime cache**: `CostModel` (and its `EnemyAP.Overrides`, if Asymmetric) is parsed once at
  startup from the file named by `[General] CostModelFile`, and re-parsed on `ConfigsHelper.ReloadConfigs`
  hot-reload if `FTK2.DevKit` is present (soft dependency, not required).

## 4. Data file formats

### 4.1 `CostModel.json` (and any alternate preset file selected by the `CostModelFile` knob)

This file is the extensibility contract — every number a designer would want to retune during balancing
lives here, not in code.

```jsonc
{
  "SchemaVersion": 1,
  "Id": "AP_COSTMODEL_DEFAULT",
  "Description": "free-text, shown in logs when a preset loads",

  "APPerTurn": {
    "Base": 4,                          // flat AP granted at a full turn-boundary reset
    "SpdBonus": {
      "SpdPerPoint": 5,                 // bonus = min(MaxBonus, floor(SPD / SpdPerPoint))
      "MaxBonus": 2
    },
    "MaxPool": 8,                       // hard cap on pool size after any grant/carryover/conversion
    "CarryoverCap": 2                   // max AP retained from previous round into next grant
  },

  "Costs": {
    "DefaultMajorActionCost": 3,        // used when CombatAbilityConfig.IsMajorAction == true
    "DefaultMinorActionCost": 1,        // used when IsMajorAction == false
    "DefaultItemUseCost": 1,            // gates CombatPhase._characterCanUseItem / _isValidInventoryOption
    "DefaultMovementCost": 1,           // see open question in §11 re: whether MOVE is ability-gated

    "FocusUsedModifier": {
      "Mode": "Add",                    // "Add" | "Multiply"
      "Value": 1
    },

    "PerCategoryOverrides": {           // keys: eAbilityCategories (14-value verified enum)
      "MOVE": 1,
      "SUMMON": 4,
      "SKIP_TURN": 0
    },

    "PerAbilityOverrides": {            // keys: real Abilities.json ability ids (highest precedence)
      "SOME_ABILITY_ID": 2
    }
  },

  "LegacyStatConversion": {
    "PAPointValue": 3,                  // 1 point of legacy CHANGE_STAT{Stat:PA} == this many AP
    "SAPointValue": 1                   // 1 point of legacy CHANGE_STAT{Stat:SA} == this many AP
  },

  "EnemyAP": {
    "Mode": "Symmetric",                // "Symmetric" | "Asymmetric"
    "Overrides": null                   // when Asymmetric: same shape as APPerTurn/Costs, sparse-mergeable
  }
}
```

**Cost resolution precedence** (highest wins): `PerAbilityOverrides[id]` → `PerCategoryOverrides[category]`
→ `IsMajorAction ? DefaultMajorActionCost : DefaultMinorActionCost`. The result is then passed through
`FocusUsedModifier` if `CombatDecisionData.FocusUsed` is true (`Add`: `cost + Value`; `Multiply`:
`ceil(cost * Value)` — always rounds costs up to whole AP, never down, so focus is never free rounding
error). Item use and movement resolve through `DefaultItemUseCost`/`DefaultMovementCost` directly (they are
not `CombatAbilityConfig` entries in every call site — see §11).

**Grant formula**: `grantedThisReset = min(MaxPool, Base + SpdBonus(SPD) + carriedIn)`, where `carriedIn`
is `min(CarryoverCap, poolBeforeReset)` for a true turn-boundary reset and `0` for a fresh combat entrant
(§3.2, §6).

**`EnemyAP.Overrides`** is sparse — any field omitted falls back to the corresponding player-side value.
This lets an Asymmetric preset override just, say, `CarryoverCap` for enemies while inheriting everything
else.

### 4.2 Shipped example dataset — see §7.

## 5. Knobs

All under BepInEx config, section names as shown.

- `[General] Enabled` (bool, default `true`) — master switch, read once at plugin load; see §9 for the
  per-save interaction.
- `[General] CostModelFile` (string, default `"CostModel.json"`) — filename inside this mod's `data/`
  folder to load. Point it at `CostModel.divinity.json` to swap the whole preset with no recompile.
- `[General] VerboseLogging` (bool, default `false`) — logs every grant/spend/carryover/conversion decision
  at `LogLevel.Debug`, per CONVENTIONS.md testing guidance.
- `[Overrides] APPerTurnOverride` (int, default `-1`) — quick-tune escape hatch; any value `>= 0` overrides
  `APPerTurn.Base` from the loaded `CostModel` without editing JSON. `-1` = defer to the data file.
- `[Overrides] CarryoverCapOverride` (int, default `-1`) — same pattern for `APPerTurn.CarryoverCap`.
- `[EnemyAP] Mode` (enum `Symmetric` | `Asymmetric`, default `Symmetric`) — mirrors/overrides
  `CostModel.EnemyAP.Mode` so a host can flip enemy-side asymmetry without editing JSON; JSON value is the
  fallback if this knob is left at its own default. **Host-effective (§9.2, §9.6 item 5)**: enemy decisions
  and enemy cost resolution run host-side (R3), so only the host's value of this knob has any effect on a
  live fight; a client's differing local value is inert, not a parity/desync risk, and is not sent through
  the R1 handshake.
- `[Multiplayer] OnParityMismatch` (enum `WarnAndSafeMode` | `WarnOnly` | `Block`, default `Block`) —
  overrides the repo-wide default (`WarnAndSafeMode`, `docs/MULTIPLAYER.md` R1) for this mod specifically.
  **Recommended and shipped default: `Block`** — see §9.5 for why AP mode has no safe partial-SafeMode
  state and must refuse a mismatched join outright rather than downgrade a subset of peers.

Knob precedence is always: **knob override (if not the sentinel default) > CostModel.json value**. This
keeps JSON as the designer-authoritative extensibility contract (per CONVENTIONS.md) while still giving
players a no-JSON quick-tune layer for the handful of numbers most likely to be tweaked live.

## 6. Patch targets & integration points

All targets are verbatim from `docs/research/game-code-reference.md` §2. Grouped by role; "Ctx" = the
lightweight call-site context tag described in §3.2.

| Class.Method | Kind | Purpose |
|---|---|---|
| **Grant** | | |
| `CombatHelper.ResetCharacterActions` (both overloads) | Postfix | Recompute pool from `CostModel.APPerTurn` per the Ctx-tagged policy (§3.2 item 1); write via mirrored setter. |
| `CombatHelper.NextTurn` | Prefix (tag only) | Set Ctx = `TurnBoundary` (apply carryover). |
| `CombatHelper.ApplyAction` | Prefix (tag only) | Set Ctx = `SkillProc` (additive grant, don't clobber a larger existing pool — see policy table below). |
| `CombatPhase._addEntityToCombat` | Prefix (tag only) | Set Ctx = `FreshEntity` (force carryover-in = 0). |
| `CombatPhase._processCombatResults` | Prefix (tag only) | Set Ctx = `CombatEnd` (grant is moot; combat is ending — mostly a no-op safety tag). |
| `CombatPhase._nextTurn` | Prefix (tag only) | Set Ctx = `TurnBoundary`. |
| `CombatPhase._initializeNextWave` | Prefix (tag only) | Set Ctx = `NewWave` (treat like `FreshEntity` for the incoming wave; carryover-in = 0). |
| `CombatPhase.OnTakeTarotCard` | Prefix (tag only) | Set Ctx = `TarotDraw` (additive grant policy, same caution as `SkillProc`). |
| **Gate** | | |
| `CombatHelper.IsUsableAbility` | Prefix (replace) | `ActionPointsService.CanAfford(component, ability, focusUsed)`. |
| `CombatHelper.IsTurnOver` | Prefix (replace) | Turn over when pool `<= 0` (M1); M2 stretch adds "no affordable ability remains." |
| `CombatPhase._characterCanUseItem` | Prefix (replace) | Gate against `Costs.DefaultItemUseCost`. |
| `InventoryController._isValidInventoryOption` | Prefix (replace) | Same, for the inventory UI filter. |
| `CombatAbilitiesTemplateHelper.Show` | Postfix | Grey out unaffordable buttons; inject cost text into the rendered tooltip (exact tooltip string builder unverified — see §11). |
| **Spend** | | |
| `CombatHelper.PerformAbility` | Postfix (or Prefix — TBD after dnSpy read, §11) | Deduct resolved cost (incl. focus modifier) from pool via mirrored setter. |
| `CombatPhase._performAbility` (async `<_performAbility>d__106`) | Postfix | Belt-and-suspenders spend, in case the async state machine has its own decrement path distinct from `PerformAbility`. |
| **Misc touchers (direct field writes, bypass `ResetCharacterActions`)** | | |
| `InteractableHelper.ApplyStatChange` | Postfix | Reinterpret `Stat=PA`/`Stat=SA` `CHANGE_STAT` deltas via `LegacyStatConversion` instead of letting the raw int land in the pool. |
| `CombatHelper.TickActiveEntityCharacterStatus` | Postfix | Guard against re-applying the same status's PA/SA delta on every tick (§9 edge case); resync-and-log safety net. |
| `CombatPhase._startPhaseEvent` | Postfix | Resync-and-log safety net (exact scripted-event semantics unverified). |
| `CombatPhase._tryStartBossPhase` | Postfix | Resync-and-log safety net; boss-phase transitions get a fresh grant if they also call `ResetCharacterActions` (already covered above), plus this safety net for any direct field write. |
| `CombatHelper.TryClearEntityActions` | Postfix | Resync-and-log safety net (this one is expected to zero both fields already — mirror should already hold). |
| **AI** | | |
| *(none required directly)* | — | If `AIHelper`'s decision pipeline filters legal options through `CombatHelper.IsUsableAbility` (unverified — §11), patching that one gate is sufficient to make vanilla AI AP-aware for free. `ActionPointsService` (below) is the fallback/explicit path either way. |
| **UI** | | |
| `EndTurnButtonViewHelper` | Postfix (method TBD after dnSpy read) | End-turn indicator shows pool instead of PA/SA split; M1 keeps this minimal (just don't show a broken split number). |

**Mid-turn grant policy (Ctx = `SkillProc` / `TarotDraw`)**: `newPool = max(poolBeforeReset, freshGrantFromFormula)` — i.e. a skill-proc or tarot "extra action" reset never *reduces* an already-larger pool, it only tops it up if the fresh-grant formula would exceed the current value. This is the safest default absent verified overload semantics (§11); if dnSpy shows the two `ResetCharacterActions` overloads cleanly distinguish "full reset" from "grant N more," switch to a pure additive `newPool = min(MaxPool, poolBeforeReset + N)` for the latter.

### AI interop surface

A new public static class, **`ActionPointsService`**, ships with this mod for other plugins to reference
(soft dependency; `FTK2.WarBrain` is the intended consumer, per its own SPEC when written):

```csharp
public static class ActionPointsService
{
    public static bool Enabled { get; }                 // AP_ModeEnabled for the current run, AND-ed with the knob
    public static int  MaxPool { get; }                 // from the active CostModel (player-side, or enemy-side overload below)
    public static int  APRemaining(CombatComponent c);
    public static int  GetCost(CombatAbilityConfig ability, bool focusUsed);
    public static bool CanAfford(CombatComponent c, CombatAbilityConfig ability, bool focusUsed);
}
```

Signatures use only verified types (`CombatComponent`, `CombatAbilityConfig`) — the exact combat-entity/
actor wrapper type that owns a `CombatComponent` is not named in `game-code-reference.md`, so the API takes
the component directly rather than guessing an entity type name.

## 7. Example starting dataset

- `data/CostModel.json` — default preset. 4 AP/turn base, +1 per 5 SPD up to +2, pool cap 8, carryover cap
  2. Major actions cost 3, minor 1, item use 1, movement 1 — deliberately close to vanilla's "1 big + 1
  small" turn shape so the conversion reads as a faithful re-expression of the existing economy, not a
  fresh rebalance. Demonstrates a `PerCategoryOverrides` sample (`MOVE`, `SUMMON`, `SKIP_TURN`) and a
  placeholder `PerAbilityOverrides` entry (real ability ids must be sourced from `Abilities.json` at
  authoring time — see §11 item 8).
- `data/CostModel.divinity.json` — alternate preset demonstrating swappability via the `CostModelFile`
  knob alone. 6 AP/turn base, bigger max pool (12) and carryover cap (3), wider major/minor spread (4 vs
  2), a `Multiply`-mode focus modifier (vs. the default's `Add`), and an `Asymmetric` `EnemyAP` block giving
  enemies a flatter 8-AP pool with zero carryover and no SPD scaling — the knob demonstration for
  "enemy-side AP (symmetric or asymmetric)."

Both files exercise every field in the §4 schema so a designer can diff them side by side as a worked
example of the full knob surface.

## 8. Testing plan

Executable by a human in under 15 minutes per CONVENTIONS.md, using a single scripted fight with
`VerboseLogging` on so every grant/spend/carryover/conversion decision prints to the BepInEx console log.

1. **Boot check**: confirm the "Target found: X" log line for every patch target in §6 — a missing line
   means that method no longer exists at that signature after a game update (fail-safe: mod should log
   loudly and fall back to vanilla PA/SA for that specific choke point, not crash).
2. **Basic spend**: enter combat, confirm starting pool matches `Base + SpdBonus(SPD)` for a known-SPD
   character, use one major and one minor ability, confirm pool decremented by the resolved costs and the
   UI pip count matches the log.
3. **Multi-attack turn**: use abilities until the pool can't afford anything else; confirm
   `IsTurnOver`/`IsUsableAbility` correctly end the turn / grey out buttons at exactly `pool < cheapest
   affordable cost`, not at `pool == 0` if a 0-cost ability (e.g. `SKIP_TURN`) is present.
4. **Carryover across rounds**: deliberately underspend a turn, confirm next turn's grant equals
   `Base + SpdBonus + min(CarryoverCap, leftover)`, and that leftover beyond the cap is discarded, not
   banked indefinitely.
5. **Haste/slow status**: apply a status with a `Stats{PA}` or `Stats{SA}` component (or a native `HASTE`-
   type effect if one carries such a delta), confirm the pool changes by `delta * LegacyStatConversion.*
   PointValue`, not by the raw literal delta — and confirm it does **not** re-apply every tick while the
   status is active (§11 item 6 — this is the one to watch most closely).
6. **Skill proc granting an extra action**: trigger a skill known to call `ApplyAction` in a way that
   reaches `ResetCharacterActions` mid-turn; confirm the mid-turn grant policy (§6) tops up rather than
   resets the pool downward.
7. **Tarot draw**: draw a tarot card that triggers `OnTakeTarotCard`'s reset path; same check as #6.
8. **Summon entry**: summon a creature mid-combat (`_addEntityToCombat`); confirm the new entity's initial
   pool uses `carriedIn = 0` regardless of `CarryoverCap`.
9. **Boss phase transition**: fight a boss with multiple phases (`_tryStartBossPhase`); confirm pools for
   all combatants remain sane (no negative, no silently-doubled) across the phase-change frame, and that
   the resync-and-log safety net doesn't fire spuriously (a firing log line here that wasn't expected is a
   signal to go verify that method's IL before shipping M2).
10. **Enemy-side symmetry/asymmetry**: run the same fight once with `[EnemyAP] Mode=Symmetric` and once
    `Asymmetric` (using the Divinity preset's `Overrides` block), confirm enemy pools track the expected
    preset in each case.
11. **Mid-run toggle**: start a run with the mod disabled, play a turn, enable it via the knob mid-run,
    confirm the per-save flag does *not* flip (still disabled for this run) and the warning about opt-in
    being locked at run creation appears in the log.
12. **MP smoke test (host + client)**: full scripted fight run live with two peers, `VerboseLogging` on
    both, DevKit dump-compare pulling both peers' AP pools after each event — multi-action turn, carryover
    round boundary, haste status application, and summon entry. See §9.6 for the full MP test plan
    (registration check, resync path, late join, join-blocking, enemy-AP host-effectiveness).

## 9. Save & multiplayer considerations

Per `docs/MULTIPLAYER.md` (repo-wide MP architecture, rules R1–R5). AP is the highest-desync-risk mod in
this repo — it replaces the vanilla action economy inside the exact fields
(`CombatComponent.PrimaryActions`/`.SecondaryActions`) that every combat choke point, and the base game's
own netcode, already touch. Every peer's combat sim must agree on affordability at every gate, every turn;
this section is written to that strict standard.

**Persistence** (unchanged from the base mechanism, restated for context): the pool value is a plain int
inside `CombatComponent`, which the base game already serializes — no new save schema for the pool itself.
The one new save field is `GameRunData.AP_ModeEnabled` (bool), captured once at run creation from the
`[General] Enabled` knob, per the Nemesis-style save-piggyback pattern (CONVENTIONS.md, EOR precedent).
**Mid-run toggling analysis**: because the pool lives in the same int fields vanilla already uses for PA/SA,
toggling the mod off mid-run does not crash or corrupt the save — vanilla will simply read whatever
pool-shaped number is sitting in `PrimaryActions` as a literal PA count, self-correcting at the next full
`ResetCharacterActions` turn boundary. This single-peer analysis is unchanged by MP; §9.5 below covers why
toggling can never be *asymmetric* across peers in the first place.

### 9.1 Parity class: `ALL_PEERS`

AP mode requires the identical mod (version + `dataHash`) and the identical active `CostModel` file on
**every peer**, for the entire time it is enabled for a run. There is no reduced-footprint install that is
safe:

- **The MP backbone is the field-mapping decision in §3.1, not a convenience.** `CombatComponent.PrimaryActions`/
  `.SecondaryActions` are vanilla-serialized, vanilla-replicated fields — every peer's game engine already
  keeps them in sync as part of ordinary combat-state sync, for free, with zero new wire protocol. That is
  the *only* reason AP mode can exist in MP without inventing a bespoke state-sync system for a value that
  changes every single ability use. Any design that instead kept the pool in a mod-side dictionary
  (`Dictionary<EntityId, int>` in plugin memory, or an equivalent side-table) is **formally rejected** (§3.1):
  such a structure is invisible to the base game's replication entirely, so keeping it consistent across
  peers would require the mod to reimplement network transport at exactly the frequency this mod's core loop
  needs it most. Option A (mirrored single pool inside the vanilla fields) is not merely "recommended," it
  is the only option that keeps this mod inside R3's "vanilla pipeline for effects" guidance.
- **Cost resolution is pure data (R1).** `CostModel.json`'s `APPerTurn`/`Costs`/`LegacyStatConversion`/
  `EnemyAP` blocks are parity-hashed like every other mod's data files (§9.3). Because cost resolution is a
  pure function of `(ability id or category, IsMajorAction, FocusUsed, SPD)` against that data, identical
  data + identical patched gates on every peer is sufficient for every peer to compute an identical cost —
  no peer-to-peer cost negotiation is needed, only parity enforcement.
- **UI affordability is evaluated client-side, which is why this can't be `HOST_ONLY`.** Per R3's authority
  split (§9.2), the *outcome* of combat (host-simulated, assumed per `docs/MULTIPLAYER.md` open question 1)
  only strictly needs the patched gates to agree on the host. But `CombatAbilitiesTemplateHelper.Show`'s
  grey-out/cost-badge rendering runs on whichever peer is looking at their own ability bar — a client
  without this mod's patches would render vanilla PA/SA-shaped affordability against a pool value that
  means something else entirely, showing wrong grey-outs, wrong tooltips, and wrong end-turn state well
  before any host-simulated result comes back. That makes the mod + `CostModel` data a client-side
  requirement independent of who simulates the fight — hence `ALL_PEERS`, not `HOST_ONLY`.

### 9.2 Feature table & authority analysis (R3)

| Feature | Sync class | Authority |
|---|---|---|
| AP pool value (grant/spend/carryover) | `[SYNCED]` — rides vanilla `CombatComponent` replication | Host-simulated combat writes the authoritative value; every peer's local Harmony patches must independently compute the *same* value from the same gates so the pre-sync local read (used for UI) already matches what the host will confirm. |
| Cost resolution (`ActionPointsService.GetCost`/`CanAfford`) | `[SYNCED]` (data-parity, not network-synced) | All peers, symmetric. Pure function of parity-hashed data; nothing to sync because nothing diverges if data matches. |
| Gates (`IsUsableAbility`, `IsTurnOver`, `_characterCanUseItem`, `_isValidInventoryOption`, `CombatAbilitiesTemplateHelper.Show`) | `[SYNCED]` | Evaluated **wherever the game evaluates them**: if vanilla combat resolution is host-simulated (per `docs/MULTIPLAYER.md` open question 1), the result that decides fight outcome only needs host/host agreement, so patches only need consistency on the host for that path. But `.Show` and the UI-adjacent gates run locally on every client's own screen to paint their own ability bar (§9.1's third bullet) — all peers must have the patch and the data regardless of who resolves the actual fight. |
| Grant/reset Ctx-tagging (§3.2/§6) | `[LOCAL]` mechanism, `[SYNCED]` outcome | The Ctx tag is a same-frame, single-peer implementation detail (R4-exempt); the pool value it produces is the synced thing. |
| Enemy-AP Symmetric/Asymmetric mode | `[SYNCED]` outcome, **host-effective** knob | The `[EnemyAP] Mode` BepInEx knob is read locally per peer, but because enemy decision-making and enemy cost resolution are host-side (R3: AI runs host-only per `docs/MULTIPLAYER.md` open question 1), only the **host's** knob value has any effect on the actual fight. A client's differing local knob value is inert, not a desync source (§9.6 item 5). |
| Resync snapshot (`AP_SYNC_RESYNC_V1`) | `[SYNCED]` | Host → clients, on-demand + late-join. See §9.4. |
| `VerboseLogging` diagnostic output | `[LOCAL]` | Presentation-only per R4; no parity requirement, any subset of peers may enable it. |
| DevKit dump-compare hooks (§9.6) | `[LOCAL]` | Debug tooling; R5 applies (disabled/host-only-with-warning in live MP unless `ForceAllowInMP`). |

### 9.3 Determinism inventory (R2)

Every number this mod produces is integer arithmetic over parity-hashed data and verified-integer game
state (`SPD`, `IsMajorAction`, `FocusUsed`). **There is no randomness anywhere in this mod's design** — no
roll, no `System.Random`, no wall-clock, no floating-point value crosses a peer boundary — so there is
nothing to host-gate or route through `GameRandom`; R2's "never local `System.Random`" rule is satisfied by
construction, not by a runtime guard. Enumerated:

1. **Grant formula** — `grantedThisReset = min(MaxPool, Base + SpdBonus(SPD) + carriedIn)`, where
   `SpdBonus(SPD) = min(MaxBonus, floor(SPD / SpdPerPoint))` and `carriedIn = min(CarryoverCap,
   poolBeforeReset)` (turn boundary) or `0` (fresh entity/wave). All operands are ints; `floor` is integer
   division, not a float truncation. Two peers with the same `SPD` and the same `CostModel` always compute
   the same grant.
2. **Carryover cap arithmetic** — `min(CarryoverCap, poolBeforeReset)`, pure integer comparison.
3. **Cost resolution** — `PerAbilityOverrides[id] → PerCategoryOverrides[category] → IsMajorAction ?
   DefaultMajorActionCost : DefaultMinorActionCost`, then `FocusUsedModifier` (`Add`: integer add; `Multiply`:
   `ceil(cost * Value)`, always rounding up to whole AP per §4.1). All lookups key off data
   (`CostModel.json`, `Abilities.json`'s `IsMajorAction`/category), never off runtime-generated ids.
4. **Mid-turn grant policy** — `max(poolBeforeReset, freshGrantFromFormula)`, integer max of two integer
   values already covered by items 1 and 2.
5. **`LegacyStatConversion`** — `delta * PAPointValue` / `delta * SAPointValue`, integer multiply against an
   integer stat delta already sourced from parity-hashed `StatusEffectConfig`/`CharacterConfig` data.

Because every one of these is a pure function of parity-hashed `CostModel.json` + already-deterministic
game state, R1 (parity enforcement on that data) is what actually carries R2 here — there is no separate
"agree on a roll" problem to solve.

### 9.4 Sync surface

- **Primary surface: vanilla `CombatComponent` replication** (§9.1) — no `_SYNC_` action is needed for the
  steady-state pool value; it rides the same wire path `PrimaryActions`/`SecondaryActions` already use for
  vanilla PA/SA.
- **`AP_SYNC_RESYNC_V1`** (host → clients, piggybacked on `AdventureDirector._handleNetworkAction` per the
  EOR/`docs/MULTIPLAYER.md` transport precedent). Fired when a desync is *detected* and cannot be resolved
  by waiting for the next ordinary `CombatComponent` sync tick, or when a client requests it via
  `AP_SYNC_RESYNC_REQUEST_V1` (late join/rejoin, or a manual DevKit debug command, R5-gated). Payload
  sketch (per-entity AP pools):
  ```jsonc
  {
    "SchemaVersion": 1,
    "RunId": "...",
    "Entities": [
      { "EntityId": "...", "PrimaryActions": 5, "SecondaryActions": 5, "CarriedInLastReset": 2 }
    ]
  }
  ```
  Idempotent by construction: applying the same snapshot twice is just writing the same two ints twice
  through the mirrored setter (§3.1 Option A) — no accumulation, no "apply once" bookkeeping required.
- **`AP_SYNC_RESYNC_REQUEST_V1`** (client → host): `{ "SchemaVersion": 1, "RequestingPeerId": "...",
  "Scope": "AllActive" | ["EntityId", ...] }`. Fired on late join/rejoin (mirrors the EOR town-snapshot
  `_REQUEST_V1` pattern per `docs/MULTIPLAYER.md` "Practical guidance") and on-demand from a DevKit debug
  command for manual dump-compare testing (§9.6).
- **Desync posture — detect → resync-from-host → session warning** (upgraded from a prior detect-and-log-
  only posture):
  1. *Detect*: a non-host peer's locally-Harmony-computed pool value disagrees with the value the next
     ordinary `CombatComponent` network sync delivers for the same entity (`VerboseLogging`: "AP mismatch
     observed for entity X: local=N, synced=M").
  2. *Resync-from-host*: on a mismatch that persists past one sync tick (not just normal same-frame
     flicker), the client fires `AP_SYNC_RESYNC_REQUEST_V1` for the affected entity/entities; the host
     answers with `AP_SYNC_RESYNC_V1` and the client applies it, overwriting its local value.
  3. *If resync is impossible* (transport failure, or a second consecutive resync attempt still disagrees,
     signalling a parity problem deeper than a transient race): a **session-visible warning** is shown to
     all peers naming the affected entity, recommending the session accept host-authoritative values for
     the remainder of combat (safe — host state is never wrong, only a client's transient local read might
     have been) or restart if repeated resync failures suggest a genuine mod/data mismatch that should have
     been caught by the R1 handshake at join time.

### 9.5 SafeMode definition

AP mode has no partial/degraded state that is safe in MP — unlike features with a graceful
presentation-only fallback, "half the peers on AP economy, half on vanilla PA/SA" is not a SafeMode, it is
a guaranteed desync (one side's combat math produces different affordability than the other's for the same
fight). **AP mode cannot be toggled mid-combat, and cannot be enabled asymmetrically across peers.**
Accordingly:

- **SafeMode for this mod = refuse to enable AP mode for the session at all (fall back to vanilla PA/SA).**
  On a `ParityFailed` callback (R1) concerning `ftk2mods.actionpoints` (version, `dataHash`, or
  `enabledFeatures` mismatch), the mod does not attempt partial feature disablement — it refuses to enable
  AP mode for the session, every peer falls back to vanilla PA/SA for that session, and a clear message is
  shown naming the mod and the divergence ("ActionPoints disabled for this session — <peer> is running a
  different CostModel/version; playing with vanilla PA/SA instead"). This is a deliberate, mod-specific
  override of the repo-wide default `[Multiplayer] OnParityMismatch = WarnAndSafeMode`.
- **Recommended (and shipped default) knob: `[Multiplayer] OnParityMismatch = Block` for
  `ftk2mods.actionpoints`** (§5). Rationale: the repo-default `WarnAndSafeMode` assumes a mod can keep its
  state-mutating features off while leaving presentation-only features on. AP mode has no such split — its
  only state-mutating feature *is* the thing that must never run asymmetrically. `Block` is the honest
  policy: refuse a mismatched peer outright rather than silently downgrade a subset of peers to a
  `SafeMode` this mod cannot actually offer without risking the asymmetric-economy failure mode itself.
- **Per-save opt-in interaction with the parity handshake at join.** `GameRunData.AP_ModeEnabled` (§3.3) is
  captured once at run creation and must be **host-authoritative and identical for all peers for the
  lifetime of that run** — save state that replicates with the run, not a per-peer knob read. Join-time
  interaction:
  1. Joining peer's `ParityService` registration is exchanged with the host (R1, `FTK2MODS_PARITY_V1`).
  2. If the host's `GameRunData.AP_ModeEnabled == true` and the joining peer does not have
     `ftk2mods.actionpoints` installed at all (not merely a version/data mismatch, but entirely absent), that
     is itself an R1 mismatch — the mod's registration is simply missing from the joining peer's handshake
     payload — and per the `Block` recommendation, the join is refused outright: "This run requires
     FTK2.ActionPoints — install the mod to join." No `SafeMode` fallback is offered at join time, because a
     joining client running vanilla PA/SA logic against a save whose `CombatComponent` fields already hold
     AP-pool-shaped numbers (not vanilla PA/SA-shaped numbers) would misread that state as literal PA counts
     — the "mixed mod sets across a save" risk, now hardened into a hard join gate rather than a README
     caveat.
  3. If the joining peer *has* the mod but with a mismatched version/`dataHash`, same outcome under `Block`:
     refused, with the specific divergence named (per R1's mismatch reporting).
  4. Mid-combat or asymmetric toggling is not just discouraged, it is **structurally prevented**: the flag
     is locked at run creation, is part of the host-owned `GameRunData`, and the join gate above ensures no
     peer can be present in a session with a different effective value of it. No code path lets one peer be
     "in AP mode" while another peer of the same live session is not.

### 9.6 MP test plan

Registration: `ftk2mods.actionpoints` registers `(guid, version, dataHash-over-active-CostModel-file,
enabledFeatures={ApMode, EnemyAPMode})` with `FTK2.DevKit`'s `ParityService` at plugin load (R1). Smoke test
(host + one client, `VerboseLogging` on both, DevKit dump-compare available on both):

1. **Full scripted fight, host + client** — same fight script as §8, run with two live peers. After every
   listed event below, use DevKit's dump-compare command to pull both peers' full AP pool state
   (`PrimaryActions`/`SecondaryActions` per combat entity) and diff them; any non-host-simulated divergence
   beyond one sync tick is a bug.
   - **Multi-action turn**: one character spends down to zero affordable actions in a single turn; compare
     pools after each individual ability use, not just at turn end, since this is the highest-frequency
     sync point in the mod.
   - **Carryover round boundary**: deliberately underspend a turn on both a host-controlled and a
     client-controlled character; compare next turn's granted pool against the formula in §9.3 item 1 on
     both peers.
   - **Haste status**: apply a `Stats{PA}`/`Stats{SA}` status; compare `LegacyStatConversion`-adjusted pools
     on both peers, and confirm no double-application on tick (§8 item 5 / §11 item 6).
   - **Summon entry**: summon mid-combat on both a host-side and client-side trigger; compare the new
     entity's initial pool (`carriedIn = 0` expected on both peers per §9.3 item 1) immediately after
     `_addEntityToCombat`.
2. **Resync path**: force a transient mismatch (e.g. via DevKit dev-mutation on a non-live test session per
   R5) and confirm the detect → `AP_SYNC_RESYNC_REQUEST_V1` → `AP_SYNC_RESYNC_V1` → applied sequence in
   §9.4 fires and resolves within one resync round-trip; confirm the session warning appears only if a
   second consecutive resync still disagrees.
3. **Late join**: host starts an AP-mode run, plays several turns, then a second client joins; confirm the
   `AP_SYNC_RESYNC_V1`/`_REQUEST_V1` snapshot exchange on join reproduces the exact current pool state for
   every combat entity mid-fight (or, if joining between fights, that the next fight starts identically for
   both peers).
4. **Join-blocking**: attempt to join an AP-mode run (a) with the mod entirely absent, (b) with a mismatched
   `CostModel` file, (c) with a mismatched mod version; confirm all three are refused per `Block` (§9.5),
   not silently downgraded.
5. **Enemy-AP host-effectiveness**: set `[EnemyAP] Mode=Asymmetric` on the client only, leaving the host at
   `Symmetric` (or vice versa); confirm the fight behaves per the **host's** setting on both peers' screens
   — confirming the knob is host-effective and a client-side difference is inert, not a desync (§9.2).

## 10. Milestones

- **M1 — Pool + costs + carryover, minimal UI, MP parity foundation.** Grant/gate/spend patches from §6
  (excluding the misc resync-and-log safety nets, which can ship alongside since they're cheap); `CostModel`
  loader with the shipped default preset; `ActionPointsService` exists but is untested by any consumer;
  repurposed PA pip display (SA row hidden) + cost text appended to ability tooltips (best-effort against
  the verified `CombatAbilitiesTemplateHelper.Show` hook); per-save `AP_ModeEnabled` flag and mid-run-toggle
  warning; Symmetric enemy AP only; **`ParityService` registration at plugin load (R1)**; **`[Multiplayer]
  OnParityMismatch=Block` shipped as this mod's default**; **join-blocking (§9.5) live** for missing-mod,
  version, or `dataHash` mismatch whenever `GameRunData.AP_ModeEnabled=true`. **Exit criteria**: scripted
  test §8 items 1–4, 8, 11 pass; §9.6 items 1 (multi-action turn + carryover subset) and 4 (join-blocking)
  pass.
- **M2 — Status/skill interaction correctness + MP resync.** `LegacyStatConversion` wired into
  `ApplyStatChange`; mid-turn grant policy validated (or corrected, per §11 item 2) against real
  `ResetCharacterActions` overload semantics; all five misc-toucher safety nets in place and confirmed
  non-spurious; Asymmetric enemy AP knob (host-effective, §9.2) + Divinity preset validated; boss-phase and
  summon-entry behavior confirmed correct; **`AP_SYNC_RESYNC_V1`/`AP_SYNC_RESYNC_REQUEST_V1` snapshot
  implemented** (detect → resync-from-host → session-warning posture, §9.4), covering both on-demand resync
  and late-join snapshot exchange. **Exit criteria**: scripted test §8 items 5–7, 9–10 pass; no unexpected
  safety-net log lines during a full boss fight; §9.6 items 2 (resync path), 3 (late join), and 5 (enemy-AP
  host-effectiveness) pass.
- **M3 — Full UI + WarBrain interop.** Distinct AP pip art (replacing the repurposed PA icon), animated
  fill/drain, cost shown as an in-pip highlight rather than text-only; `ActionPointsService` consumed by
  `FTK2.WarBrain`'s scoring function (damage-per-AP efficiency term). MP posture (parity registration,
  join-blocking, resync snapshot) is already complete by end of M2 (§9) — M3 introduces no new MP
  mechanism; its only MP-relevant task is re-verifying §9's authority assumptions against real IL once §11
  item 7 is resolved, and tightening the resync-vs-corrective-auto-fix boundary in §9.4 if that
  verification allows it.

## 11. Open questions

1. Exact predicate inside `IsUsableAbility` / `IsTurnOver` / `PerformAbility` / `_performAbility` — does
   each independently test/decrement `PrimaryActions` vs `SecondaryActions` per `IsMajorAction`, or is
   there shared logic? Needed to confirm the Prefix-replace strategy in §6 doesn't miss a code path.
   Requires a dnSpy read before implementation.
2. The two `ResetCharacterActions` overloads' exact signatures/semantics — is one a full-reset-to-class-
   defaults and the other an incremental "+N actions" grant (assumed in §6's mid-turn policy), or something
   else entirely? Determines whether the `max(current, freshGrant)` fallback policy can be replaced with a
   cleaner additive one.
3. Whether `_addEntityToCombat`'s reset call is distinguishable from a normal turn-boundary call via any
   parameter, or whether the Ctx-tagging device in §3.2/§6 (a static field set by a same-frame prefix on
   the caller) is required because the callee itself carries no context. Assumed the latter; needs
   confirmation the call sites don't reenter or race in a way that breaks single-threaded tag assumptions.
4. Does `AIHelper.StandardAiDecision`/`CategorizeCombatOptions` call `CombatHelper.IsUsableAbility` for
   legality filtering? If yes, vanilla AI (and `FTK2.WarBrain`, if it delegates legality checks the same
   way) becomes AP-aware for free from the single gate patch; if no, an additional `AIHelper` patch is
   needed to strip unaffordable options before scoring.
5. Exact tooltip-text-building call site for ability tooltips is not named in
   `docs/research/game-code-reference.md` beyond `CombatAbilitiesTemplateHelper.Show` controlling
   show/hide. Needs a dnSpy pass to find the precise string-builder for the M1 "cost in tooltip" feature;
   if none is reachable from `Show` itself, a second small patch target will be needed.
6. `TickActiveEntityCharacterStatus` re-application cadence for `Stats{PA,SA}` deltas — applied once when
   a status is added, or reapplied every combat tick for the status's duration? Directly affects whether
   `LegacyStatConversion` double-counts a haste effect every tick. Highest-priority item to verify before
   M2, per §8 test #5.
7. **[MP-CRITICAL, highest priority]** FTK2's netcode authority model for `CombatComponent` sync — which
   peer's patched value wins after replication, and specifically whether vanilla combat resolution (and
   therefore enemy AI decisions) is host-only. This is `docs/MULTIPLAYER.md`'s repo-wide open question 1,
   restated here because §9's entire authority analysis (9.1–9.6) is built on the working assumption that
   combat is host-simulated and `CombatComponent` replicates host→client — if that assumption is wrong, the
   `ALL_PEERS` parity class, the resync-from-host posture, and the "client UI reads local patched gates"
   argument in §9.1 all need re-derivation, not just tuning. Requires live MP testing and/or a decompile
   pass before M1 ships to a real multi-peer session; do not treat §9 as verified until this is resolved.
8. No verified concrete `Abilities.json` ability id was available in `docs/research` at spec time; the
   shipped `PerAbilityOverrides` example uses a placeholder key (`REPLACE_WITH_REAL_ABILITY_ID`) that must
   be swapped for real ids sourced directly from `Abilities.json` before this feature is demonstrated
   in-game.
9. Is player/enemy movement itself gated through the same ability-cost pipeline (`eAbilityCategories.MOVE`
   as a `CombatAbilityConfig` entry) or is repositioning a separate, non-ability-costed action entirely?
   `DefaultMovementCost` and the `PerCategoryOverrides.MOVE` field are shipped on the assumption movement
   is ability-gated; if it turns out to be a free/separate action outside `IsUsableAbility`'s reach, a
   distinct patch target (unidentified in current docs) will be needed to actually enforce the movement
   cost, and this should be resolved before M1 exit.
10. **Other MP open questions inherited from `docs/MULTIPLAYER.md`** (repo-wide numbered list, tracked
    centrally, not duplicated in full here) — two are directly load-bearing for this spec: repo open
    question 2 (does `GameRunData` custom state replicate to clients, or live host-side only?) directly
    affects whether §9.5's "`AP_ModeEnabled` is host-authoritative and identical for all peers" claim is
    automatic or requires this mod to push it explicitly via a `_SYNC_` action of its own; repo open
    question 5 (exact `_handleNetworkAction` payload size limits) is relevant to confirming
    `AP_SYNC_RESYNC_V1`'s per-entity two-int payload (§9.4) stays trivially within any limit for large
    fights (many summons/enemies).
