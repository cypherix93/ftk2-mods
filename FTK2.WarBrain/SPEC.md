# FTK2.WarBrain — enemy battle-AI engine

Plugin GUID: `ftk2mods.warbrain` · Id prefix: `WB_` · Priority: **P0** (flagship mod)

## 1. Purpose & scope

WarBrain replaces the vanilla enemy combat AI (`AIHelper.BehaviourAiDecision` /
`AIHelper.StandardAiDecision`) with a **data-driven utility-scoring brain**: every legal
(ability × target × position × focus-use) candidate is scored as a weighted sum of named
*considerations* (expected damage, kill-secure, threat, squishiness, ally-in-danger, etc.),
the weights/curves/filters/biases for that sum live entirely in JSON "brain profiles," and a
temperature knob turns the argmax pick into a softmax sample so the AI reads as opinionated
rather than robotic. A companion **memory subsystem** lets enemies remember what the player's
party did (abilities sighted, damage-by-type taken, healers identified) and react to it via
data-defined rules. **Tactical doctrines** bundle a profile + reactions into a named package
(e.g. "swarm and surround") assignable by character tag.

Deliberately out of scope for this spec:
- Overworld/non-combat AI (town NPCs, world encounters) — combat only.
- Player-party AI-controlled companions (`PlayerPartyHasAI`) — WarBrain targets enemy-side
  decisions; player-side AI is not repatched, though nothing prevents assigning it a profile
  later (open question, §11).
- Pathing/animation — WarBrain only produces the `CombatDecisionData` contract; execution is
  100% vanilla (`CombatPhase._performAiDecision`, `CombatHelper.PerformAbility`, etc.).
- New abilities/statuses/skills — WarBrain only *chooses among* whatever `Abilities.json` /
  `SkillConfigs.json` already define for an entity.

## 2. Player-facing behavior

- Enemies stop feeling like they roll a die among "attack the frontliner" and "use a random
  debuff." A brute-type enemy dog-piles the weakest target and finishes it off; a tactician-type
  enemy focuses whoever the party already hurt this fight and goes for squishy backline casters;
  a coward flees or turtles once badly hurt instead of suiciding; a support-type enemy babies its
  low-HP allies and prioritizes heals/buffs over attacking.
- Enemies that see the party spam an AoE start spreading out on the grid instead of clustering.
  Enemies that see a healer heal an ally start focusing that healer on sight in later turns (and,
  if memory scope is per-run, in later battles of the same run).
- Named enemy squads ("pack hunters," "swarms," "guardians") behave identifiably differently from
  each other even when built from the same base profile, because a doctrine is layered on top.
- With `VerboseLogging` + `LogDecisionBreakdown` on, every AI turn prints a full score breakdown
  to the BepInEx console/log — this is the primary tuning and debugging tool, not a player-facing
  feature.
- Nothing changes if the mod is disabled or its data fails to load: vanilla `AIHelper` behavior
  runs untouched (fail-safe knob, per repo convention).

## 3. Architecture

### Engine vs data split
- **Engine (C#)**: candidate enumeration, the fixed catalogue of consideration *evaluators*
  (the code that turns "this candidate, this battle state" into a raw 0..1 number for
  `WB_CONSIDER_EXPECTED_DAMAGE` etc.), the curve math, the softmax sampler, the memory
  fact-recorder, the reaction-application pipeline, the assignment resolver, and the Harmony
  patches. The engine hardcodes **no weights, no curve constants, no filters, no ids**.
- **Data surface (JSON)**: which considerations apply to which brain, their weights/curves,
  target filters, ability-category biases, temperature, difficulty-scaling multipliers
  (`data/Profiles/*.brain.json`); doctrine deltas (`data/Doctrines/*.doctrine.json`); memory
  fact→reaction rules (`data/MemoryReactions/*.reactions.json`); character→profile/doctrine
  bindings (`data/Assignments/*.assignment.json`). New considerations still require code (they
  are a fixed catalogue documented in §4); everything about how they're combined is data.

### Runtime flow (per AI turn)
1. `CombatPhase._engageActiveEntity` calls `AIHelper.BehaviourAiDecision` (and internally may
   fall back to `AIHelper.StandardAiDecision`) — both carry a WarBrain Harmony **prefix**.
2. Prefix calls `WarBrainDecisionEngine.TryDecide(entity, combatState, out result)`:
   a. **Resolve** the active brain profile + optional doctrine for `entity` via the assignment
      resolver (§4, priority/specificity order: exact character id → tag → `BaseType` →
      vanilla `eAiBehaviours` profile name → global default).
   b. **Enumerate candidates**: legal abilities for `entity` this turn (reading
      `CombatState.AiAbilities` / `AIComponent.BehaviourShuffle`, respecting
      `CombatAbilityConfig.IsMajorAction`/`RequiresFocus`/`Ammo` and remaining
      `PrimaryActions`/`SecondaryActions`) × legal targets/positions for each ability (via
      `VenueHelper.GetTargetableTiles`/`GetAreaTiles` filtered by the ability's `TargetArea`,
      `Target`, `OriginRowPosition`/`TargetRowPosition`) × focus-use on/off where
      `IsFocusable`. Candidate generation reuses vanilla read-only helpers
      (`AIHelper.CategorizeCombatOptions`, `GetAbilityCategory`, `GetPreferredTarget`) so
      WarBrain never re-derives what's *legal* — only what's *good*.
   c. **Filter** candidates against the resolved profile's `TargetFilters`.
   d. **Score** each surviving candidate: `Σ (considerationWeight_i * curve_i(rawInput_i))`,
      then `× AbilityCategoryBias[category]`, then `+ memory reaction deltas` (if memory
      enabled and facts match), then `× difficulty multipliers` (profile `DifficultyScaling` ×
      global knobs).
   e. **Pick**: softmax over final scores with temperature `T` (`P(c) ∝ exp(score_c / T)`);
      `T → 0` degenerates to deterministic argmax (used by high-difficulty/low-randomness
      profiles); higher `T` broadens the distribution. A separate `MistakeChance` can instead
      substitute a uniformly-random *legal* candidate outright (a distinct "AI misplays" knob
      from "AI has personality").
   f. Build `CombatDecisionData { Ability, Position, FocusUsed }` from the chosen candidate.
   g. Prefix returns `false` (skip vanilla) with the built result. **Any exception anywhere in
      a–f is caught at the prefix boundary**, logged at `Warning`, and the prefix returns `true`
      (fail-safe: vanilla `AIHelper` logic runs for that entity's turn as if WarBrain weren't
      installed).
3. `CombatHelper.PerformAbility` carries a WarBrain **postfix**: if the actor is player-tagged
   (`Tags` contains `PLAYER`) and memory is enabled, it derives `WB_FACT_*` memory facts from the
   executed ability/action and records them into the current battle's memory store, scoped to
   the opposing enemy faction.
4. `ConfigsHelper.ReloadConfigs` carries a WarBrain postfix that reloads all four data
   registries in place (supports the edit-JSON→hot-reload loop from `docs/CONVENTIONS.md`).

### State lifecycle
- **Per-battle** (plugin static field, cleared on battle end/start — see open question in §11
  for the exact reset hook): the live memory-fact store for the current combat, and the
  resolved-profile cache for entities currently in combat.
- **Per-run**: when `MemoryScope = PerRun`, per-battle memory facts are folded into a per-run
  memory store at battle end. Piggybacks `GameRunData` the same way EOR's Nemesis system does
  (`GameRunData.Create` patched to attach/initialize a side-store keyed to the run); cleared
  when a new run is created.
- **Persistent (data)**: brain profiles, doctrines, reactions, and assignments are pure
  content — loaded at startup and on `ReloadConfigs`, never mutated by gameplay.

## 4. Data file formats

All files are JSON, UTF-8, no comments (per game convention) — human-readable `Description`
fields are used instead where useful. Engine scans folders and merges *all* files found in
each; new files are picked up automatically (no registration step).

```
data/
  Profiles/*.brain.json           # WB_BRAIN_*
  Doctrines/*.doctrine.json       # WB_DOCTRINE_*
  MemoryReactions/*.reactions.json
  Assignments/*.assignment.json
```

### 4.1 Consideration catalogue (fixed set of engine-known evaluators)

Each consideration is a **named evaluator implemented in code** that reduces
`(candidate, entity, battleState, memory)` to a raw value normalized to `[0,1]` before any
curve/weight is applied (unless noted). Profiles/doctrines only ever supply the **weight** and
**curve** for a consideration — never new evaluator logic. This is the extension seam: a new
consideration id requires a code change (documented here so a profile author knows the full
vocabulary); everything about how strongly it matters is data.

| Id | Raw input (0..1 unless noted) | Data source used |
|---|---|---|
| `WB_CONSIDER_EXPECTED_DAMAGE` | predicted damage as a fraction of target's current HP | ability `Actions[]` `CHANGE_STAT` rolls, target `Stats.HP` |
| `WB_CONSIDER_KILL_SECURE` | 1.0 if predicted damage ≥ target's current HP, else 0 | as above |
| `WB_CONSIDER_FOCUS_FIRE` | fraction of target's max HP already missing this battle | target `Stats.HP` vs. tracked max |
| `WB_CONSIDER_THREAT` | target's `CharacterConfig.Threat` (0–9) normalized /9 | `Characters.json` `Threat` |
| `WB_CONSIDER_TARGET_SQUISHINESS` | inverse of target's effective `DEF`/`RES`/`EVD` relative to the ability's damage type | target `Stats` |
| `WB_CONSIDER_STATUS_VALUE` | value of the status the ability would apply/remove (buff-to-self-or-ally vs. debuff-to-enemy), sign-aware | ability `Actions[]` `ADD_STATUS`/`REMOVE_STATUS`, `StatusEffects.json` `Type` |
| `WB_CONSIDER_ALLY_IN_DANGER` | 1 − (lowest-HP-fraction ally reachable by this ability) | ally `Stats.HP` |
| `WB_CONSIDER_ACTION_ECONOMY` | reward for using a `SecondaryAction`-costing ability when `PrimaryActions` remain scarce, or for not spending a major action on a low-value play | `CombatComponent.PrimaryActions/SecondaryActions`, `CombatAbilityConfig.IsMajorAction` |
| `WB_CONSIDER_ROW_POSITION_VALUE` | tactical value of the resulting/target tile row (front vs. back) | `VenueHelper` row queries, ability `OriginRowPosition`/`TargetRowPosition` |
| `WB_CONSIDER_SELF_PRESERVATION` | 1 − caster's own HP fraction (high when caster is badly hurt) | caster `Stats.HP` |
| `WB_CONSIDER_RANGE_SAFETY` | 1.0 if ability is `IsRanged` and caster is not adjacent to any enemy, scaled down otherwise | ability `IsRanged`, grid adjacency |
| `WB_CONSIDER_TENDENCY_MATCH` | 1.0 if the target best matches the ability's own `Tendency` hint among legal targets, else partial credit by rank | ability `CombatAbilityConfig.Tendency`, vanilla `eAiTendencies` ordering (reuses `AIHelper._orderTargetsByTendency` output) |

`WB_CONSIDER_ABILITY_CATEGORY_BIAS` is **not** a weighted consideration — it is a flat
multiplier applied after the weighted sum (see `AbilityCategoryBias` below), because a category
bias should scale the whole candidate's appeal, not compete additively with per-candidate
considerations.

Memory reactions (§4.4) apply as **additive deltas** to consideration weights or
`AbilityCategoryBias` values, or as a separate additive `TargetPriorityDelta` added directly to
a specific candidate's final score — they are not their own consideration id.

### 4.2 Curve schema (shared by every consideration weight)

```jsonc
"Curve": {
  "Type": "LINEAR",       // LINEAR | QUADRATIC | LOGISTIC | STEP
  // LINEAR:    output = raw
  // QUADRATIC: output = raw ^ Exponent
  "Exponent": 2.0,
  // LOGISTIC:  output = 1 / (1 + e^(-Steepness * (raw - Midpoint)))
  "Midpoint": 0.5,
  "Steepness": 8.0,
  // STEP:      output = raw >= Threshold ? 1.0 : 0.0
  "Threshold": 0.5
}
```
Only the fields relevant to `Type` are read; others are ignored. Output is clamped to `[0,1]`
before multiplication by the consideration's `Weight` (which itself may be negative, to make a
consideration repel rather than attract — e.g. a coward's `WB_CONSIDER_SELF_PRESERVATION` on the
*attack* candidates uses a negative weight so being hurt suppresses aggression).

### 4.3 Brain profile — `data/Profiles/*.brain.json`

```jsonc
{
  "Id": "WB_BRAIN_EXAMPLE",
  "Description": "Human-readable summary of the personality.",
  "Considerations": [
    {
      "Id": "WB_CONSIDER_EXPECTED_DAMAGE",
      "Weight": 1.0,
      "Curve": { "Type": "LINEAR" }
    },
    {
      "Id": "WB_CONSIDER_KILL_SECURE",
      "Weight": 2.5,
      "Curve": { "Type": "STEP", "Threshold": 0.999 }
    }
  ],
  // Optional. AND within one filter object; OR across the list — a candidate target passes
  // if it satisfies at least one TargetFilters entry (empty list = no filtering).
  "TargetFilters": [
    {
      "Tags": { "AnyOf": [], "NoneOf": [] },
      "BaseType": { "AnyOf": [], "NoneOf": [] },
      "HpPercentBelow": 1.0,
      "HpPercentAbove": 0.0,
      "RowPosition": "ANY"        // FRONT | BACK | ANY
    }
  ],
  // Multiplier per eAbilityCategories value; absent categories default to 1.0.
  "AbilityCategoryBias": {
    "ATTACK": 1.0,
    "DEBUFF": 1.0,
    "SUPPORT_SELF": 1.0,
    "SUPPORT_ALLY": 1.0,
    "SUPPORT_GROUP": 1.0,
    "TAUNT": 1.0,
    "USE_ITEM": 1.0,
    "MOVE": 1.0,
    "MOVE_ENEMY": 1.0,
    "SUMMON": 1.0,
    "FLEE": 1.0,
    "SKIP_TURN": 1.0,
    "REVIVE_ALLY": 1.0,
    "REPAIR_VEHICLE": 1.0
  },
  // Softmax temperature. 0 = always pick the top-scoring candidate. Higher = more varied.
  "Temperature": 0.2,
  "DifficultyScaling": {
    "IntelligenceMultiplier": 1.0,   // scales every consideration Weight
    "TemperatureMultiplier": 1.0,    // scales Temperature
    "MistakeChance": 0.0             // chance [0,1] to instead pick a uniformly random legal candidate
  }
}
```

### 4.4 Doctrine — `data/Doctrines/*.doctrine.json`

A doctrine is a **delta layer** applied on top of whatever profile the assignment resolver
picked (doctrines never stand alone). Deltas are additive; `Overrides` (if present for a given
id) fully replace rather than add.

```jsonc
{
  "Id": "WB_DOCTRINE_EXAMPLE",
  "Description": "Named behavior package, layered on top of the resolved brain profile.",
  "ConsiderationWeightDeltas": { "WB_CONSIDER_FOCUS_FIRE": 0.5 },
  "ConsiderationOverrides": [ ],      // same shape as a profile's Considerations entries
  "AbilityCategoryBiasDeltas": { "ATTACK": 0.2 },
  "TemperatureDelta": 0.0,
  "ReactionRefs": [ "WB_REACT_FOCUS_SAME_TARGET" ]  // reactions always considered "triggered" while this doctrine is active, independent of memory facts
}
```

### 4.5 Memory facts & reactions — `data/MemoryReactions/*.reactions.json`

**Facts recorded** by the `CombatHelper.PerformAbility` postfix, one battle-scoped log per
enemy faction, whenever a `PLAYER`-tagged actor completes an ability:

| Fact | Fields |
|---|---|
| `WB_FACT_ABILITY_SEEN` | `AbilityId`, `Category` (`eAbilityCategories`), `TargetArea` (`Abilities.json` `TargetArea`), `Tendency`, `ActorEntityId`, `Turn` |
| `WB_FACT_DAMAGE_TAKEN_BY_TYPE` | `DamageType` (the `CHANGE_STAT` action's `Type`, e.g. `PHYSICAL`/`FIRE`/`POISON`), `Amount`, `VictimEntityId`, `ActorEntityId`, `Turn` |
| `WB_FACT_HEALER_IDENTIFIED` | `ActorEntityId`, `AbilityId`, `Turn` — recorded when an executed ability contains a `CHANGE_STAT{Stat:"HP"}` action with a positive effective value targeting an `ALLY`, or an `ADD_STATUS` referencing a `REGEN`-type status |
| `WB_FACT_STATUS_INFLICTED` | `StatusId`, `ActorEntityId`, `VictimEntityId`, `Turn` |

```jsonc
{
  "Reactions": [
    {
      "Id": "WB_REACT_SAW_AOE",
      "Description": "Party used an AoE — start spreading out.",
      "Trigger": {
        "Fact": "WB_FACT_ABILITY_SEEN",
        "TargetAreaAnyOf": ["AOE", "ROW", "COLUMN", "SPLASH", "SPLASH_WAVE", "ALL_GROUP"]
      },
      "Scope": "FACTION",
      "Effect": { "ConsiderationWeightDelta": { "WB_CONSIDER_ROW_POSITION_VALUE": 0.4 } },
      "Decay": { "Mode": "PER_BATTLE" }
    },
    {
      "Id": "WB_REACT_HEALER_IDENTIFIED",
      "Description": "Identified party healer — bias targeting toward them.",
      "Trigger": { "Fact": "WB_FACT_HEALER_IDENTIFIED" },
      "Scope": "FACTION",
      "Effect": { "TargetPriorityDelta": 6.0 },
      "Decay": { "Mode": "PER_RUN", "DecayBattles": 3 }
    }
  ]
}
```
- `Trigger.Fact` selects which fact type arms the reaction; the remaining `Trigger` keys are
  fact-specific filters (`TargetAreaAnyOf`, `DamageTypeAnyOf`, `MinOccurrences`, …).
- `Scope: "FACTION"` means the reaction, once triggered, applies to every AI decision made by
  the enemy faction that witnessed it (not just the entity that got hit).
- `Effect.ConsiderationWeightDelta` / `AbilityCategoryBiasDelta` add to the resolved
  profile+doctrine values for the duration the reaction is active. `Effect.TargetPriorityDelta`
  instead adds a flat bonus directly to any candidate scoring against the *specific remembered
  entity* (e.g. the identified healer), independent of consideration weights.
- `Decay.Mode`: `PER_BATTLE` (cleared at battle end), `PER_RUN` (survives battle end while
  `MemoryScope=PerRun`; `DecayBattles` optionally expires it after N battles), `NONE` (never
  decays while memory is enabled).

### 4.6 Assignment — `data/Assignments/*.assignment.json`

```jsonc
{
  "Rules": [
    {
      "Priority": 1000,
      "Match": { "CharacterId": "BOSS_NECROMANCER_00" },
      "ProfileId": "WB_BRAIN_TACTICIAN",
      "DoctrineId": "WB_DOCTRINE_GUARDIAN"
    },
    {
      "Priority": 500,
      "Match": { "Tags": { "AnyOf": ["GOBLIN"] } },
      "ProfileId": "WB_BRAIN_BRUTE",
      "DoctrineId": "WB_DOCTRINE_SWARM"
    },
    {
      "Priority": 300,
      "Match": { "BaseType": { "AnyOf": ["SKELETON"] } },
      "ProfileId": "WB_BRAIN_BRUTE"
    },
    {
      "Priority": 100,
      "Match": { "AiBehaviour": "SUPPORT" },
      "ProfileId": "WB_BRAIN_SUPPORT"
    },
    {
      "Priority": 0,
      "Match": { "Default": true },
      "ProfileId": "WB_BRAIN_BRUTE"
    }
  ]
}
```
**Resolution order** (a rule "applies" if every key present in `Match` matches the entity):
1. Collect all applying rules across every loaded assignment file.
2. Pick the highest `Priority`. Ties are broken by match-kind specificity, most → least
   specific: `CharacterId` > `Tags` > `BaseType` > `AiBehaviour` > `Default`.
3. If no rule applies at all (should not happen — `Default` always matches), fall back to the
   `Assignments.DefaultProfileId` BepInEx knob.
`AiBehaviour` matches the vanilla `eAiBehaviours` value assigned to the entity (bridges
`Followers.json`/`Behaviours.json`-driven vanilla profiles to WarBrain profiles for anything not
explicitly tagged).

## 5. Knobs

`[General]`
- `Enabled` (bool, `true`) — master switch; `false` fully restores vanilla `AIHelper` behavior.
- `VerboseLogging` (bool, `false`) — enables `LogLevel.Debug` decision logs (per repo convention).
- `LogDecisionBreakdown` (bool, `false`) — when combined with `VerboseLogging`, logs every
  candidate's full per-consideration score breakdown, not just the chosen pick.

`[Subsystems]`
- `EnableScoringEngine` (bool, `true`) — if `false`, the takeover prefixes still install (for
  logging/telemetry) but always return `true` (defer to vanilla).
- `EnableMemory` (bool, `true`) — master switch for the `CombatHelper.PerformAbility` postfix
  and reaction application.
- `EnableDoctrines` (bool, `true`) — if `false`, only base profiles apply; doctrine deltas are
  skipped.

`[Difficulty]`
- `GlobalIntelligenceScalar` (float, `1.0`) — multiplies every profile's
  `DifficultyScaling.IntelligenceMultiplier`.
- `GlobalTemperatureMultiplier` (float, `1.0`) — multiplies every profile's effective
  temperature (after its own `DifficultyScaling.TemperatureMultiplier`).
- `GlobalMistakeChanceAdd` (float, `0.0`) — added to every profile's `MistakeChance`, clamped
  to `[0,1]`.

`[Memory]`
- `MemoryScope` (enum: `Off`, `PerBattle`, `PerRun`; default `PerBattle`).
- `MemoryDecayEnabled` (bool, `true`) — if `false`, reactions never expire while memory is on
  (overrides individual `Decay.Mode` for the session — a "nightmare mode" knob).

`[Assignments]`
- `DefaultProfileId` (string, `"WB_BRAIN_BRUTE"`) — used if no assignment rule applies.

`[Safety]`
- `FailSafeOnError` (bool, `true`) — always caught regardless, but exposed so a tester can flip
  it `false` in a debug build to make scoring exceptions loud/crashing instead of silently
  deferring to vanilla.

## 6. Patch targets & integration points

All from `docs/research/game-code-reference.md` verbatim class/method names. Manual
`AccessTools` + `Harmony.Patch` per repo convention (no `[HarmonyPatch]` attributes), each
logging `"Target found: X"` on successful patch.

| Target | Kind | Purpose |
|---|---|---|
| `AIHelper.BehaviourAiDecision` | Prefix | Primary enemy decision entry point (called from `CombatPhase._engageActiveEntity`). Computes and returns a `CombatDecisionData`; returns `false` to skip vanilla, or `true` on any internal failure. |
| `AIHelper.StandardAiDecision` | Prefix | Secondary/fallback decision entry point the vanilla flow uses when `BehaviourAiDecision` defers. Same prefix contract as above — WarBrain intercepts both so it never partially controls a turn. |
| `CombatHelper.PerformAbility` | Postfix | After a `PLAYER`-tagged ability executes, derive and record `WB_FACT_*` memory facts against the acting player's opposing enemy faction. No-op if `EnableMemory=false` or `MemoryScope=Off`. |
| `ConfigsHelper.ReloadConfigs` | Postfix | Re-run the WarBrain data loader (Profiles/Doctrines/Assignments/MemoryReactions) so edited JSON takes effect without a restart, matching the repo's hot-reload testing loop. |
| `GameRunData.Create` | Postfix | Attach/initialize the per-run memory side-store (only touched when `MemoryScope=PerRun`); mirrors EOR's Nemesis pattern. |

Not patched, only **called (read-only)** by the decision engine to build candidates: `AIHelper.
CategorizeCombatOptions`, `AIHelper.GetAbilityCategory`, `AIHelper.GetPreferredTarget`,
`AIHelper._orderTargetsByTendency` (via its public effect, for `WB_CONSIDER_TENDENCY_MATCH`),
`VenueHelper.GetTargetableTiles`/`GetAreaTiles`, `CharacterHelper.GetStat`/`GetActorsByTags`.
WarBrain must never call anything that mutates combat/action-economy state during scoring — only
during the final `CombatDecisionData` handoff, which is vanilla's job once WarBrain returns it.

## 7. Example starting dataset

Shipped under `FTK2.WarBrain/data/`:

**Profiles** (`Profiles/*.brain.json`)
- `WB_BRAIN_BRUTE.brain.json` — melee aggression: heavy `WB_CONSIDER_EXPECTED_DAMAGE` +
  `WB_CONSIDER_KILL_SECURE`, `ATTACK` bias 1.4, near-zero `WB_CONSIDER_ALLY_IN_DANGER`/
  `WB_CONSIDER_SELF_PRESERVATION`, low temperature (0.15) — picks the hardest hit almost every
  time.
- `WB_BRAIN_TACTICIAN.brain.json` — `WB_CONSIDER_FOCUS_FIRE` and `WB_CONSIDER_TARGET_SQUISHINESS`
  dominate, `WB_CONSIDER_ROW_POSITION_VALUE` biases toward reaching backline casters, moderate
  temperature (0.3) for readable but not perfectly optimal play.
- `WB_BRAIN_COWARD.brain.json` — `WB_CONSIDER_SELF_PRESERVATION` weight is large and paired with
  a `FLEE`/`SKIP_TURN` category bias > 1, `TargetFilters` avoid the toughest target, negative
  weight on `WB_CONSIDER_EXPECTED_DAMAGE` once combined with self-preservation logic below ~40%
  HP.
- `WB_BRAIN_SUPPORT.brain.json` — `WB_CONSIDER_ALLY_IN_DANGER` and `WB_CONSIDER_STATUS_VALUE`
  dominate, `SUPPORT_ALLY`/`SUPPORT_GROUP`/`REVIVE_ALLY` category bias 1.6+, `ATTACK` bias 0.5.

**Doctrines** (`Doctrines/*.doctrine.json`)
- `WB_DOCTRINE_PACK_HUNTER.doctrine.json` — `WB_CONSIDER_FOCUS_FIRE` delta +0.6, `ATTACK` bias
  delta +0.2. Layer this on `WB_BRAIN_BRUTE` or `WB_BRAIN_TACTICIAN` for a squad that visibly
  dog-piles one target per fight.
- `WB_DOCTRINE_SWARM.doctrine.json` — `WB_CONSIDER_ROW_POSITION_VALUE` delta +0.5 tuned to
  reward *not* stacking columns, references `WB_REACT_SAW_AOE` so it gets extra spread-out
  pressure once the party shows an AoE. Meant for `GOBLIN`-tagged trash mobs.
- `WB_DOCTRINE_GUARDIAN.doctrine.json` — `TAUNT` bias delta +0.8, `WB_CONSIDER_ALLY_IN_DANGER`
  delta +0.4 restricted (via a `TargetFilters`-style tag check documented inline) to a
  designated "leader" ally — protects the boss/caster of the squad.

**Memory reactions** (`MemoryReactions/WB_default.reactions.json`) — `WB_REACT_SAW_AOE` (spread
out), `WB_REACT_HEALER_IDENTIFIED` (focus the healer), `WB_REACT_HEAVY_ELEMENTAL_DAMAGE` (party
leaning on one damage type nudges `SUPPORT_SELF` bias up, simulating "getting cautious").

**Assignments** (`Assignments/WB_default.assignment.json`) — binds:
- `BOSS_NECROMANCER_00` (exact id) → `WB_BRAIN_TACTICIAN` + `WB_DOCTRINE_GUARDIAN`. **This exact
  id is illustrative, not verified against a shipped `Characters.json` id list** — the reference
  docs confirm ~393 `BOSS_*` entries exist but do not enumerate them (see §11).
- Tag `GOBLIN` → `WB_BRAIN_BRUTE` + `WB_DOCTRINE_SWARM`.
- Tag `CULTIST` → `WB_BRAIN_TACTICIAN` (casters that pick their shots).
- `BaseType` `SKELETON` → `WB_BRAIN_BRUTE` (mindless, no self-preservation, demonstrates
  base-type-level assignment distinct from tag-level).
- `BaseType` `BIRD` → `WB_BRAIN_COWARD` (skittish, flees readily; exercises the coward profile
  without needing a temporary rule during testing).
- `AiBehaviour` `SUPPORT` → `WB_BRAIN_SUPPORT` (bridges any vanilla `Behaviours.json`-tagged
  healer-type enemy not otherwise covered).
- `AiBehaviour` `TANK` → `WB_BRAIN_BRUTE` + `WB_DOCTRINE_GUARDIAN` (holds the line aggressively
  while protecting squishier allies — demonstrates a doctrine layered on a behaviour-tier match).
- `Default` → `WB_BRAIN_BRUTE`.

This set exercises every resolution tier (exact id, tag, base type, vanilla behaviour, default),
every shipped profile, all three doctrines, and every reaction.

## 8. Testing plan

Executable in under 15 minutes with the shipped data. Turn on `VerboseLogging=true` and
`LogDecisionBreakdown=true` first.

1. **Log format sanity.** Start any fight. Confirm each enemy turn prints a line resembling:
   `[WarBrain] <EntityId> profile=<ProfileId> doctrine=<DoctrineId|none> candidates=<k>
   chosen=<AbilityId>@<Position> focus=<bool> score=<final>` followed by one breakdown line per
   consideration (`id weight curve(raw)=contribution`) and the softmax probability of the pick.
2. **Brute.** Fight `SKELETON`-`BaseType` enemies (assigned `WB_BRAIN_BRUTE` by default). They
   should consistently go for the highest-damage/kill-securing target, essentially ignoring any
   allies' HP. Log should show `WB_CONSIDER_EXPECTED_DAMAGE`/`WB_CONSIDER_KILL_SECURE`
   dominating the winning candidate's breakdown.
3. **Tactician + focus fire.** Fight a `CULTIST`-tagged enemy alongside another enemy; land a
   hit on one party member, then watch subsequent tactician turns preferentially target the
   already-hurt member — `WB_CONSIDER_FOCUS_FIRE`'s contribution should visibly rise turn over
   turn in the log for that target.
4. **Coward.** Fight a `BIRD`-`BaseType` enemy (assigned `WB_BRAIN_COWARD` by default) and get it
   below ~40% HP. Confirm it stops attacking and instead picks `FLEE`/`MOVE`/`SKIP_TURN`
   candidates, with `WB_CONSIDER_SELF_PRESERVATION` dominating the log.
5. **Support.** Fight alongside (or simulate) a `WB_BRAIN_SUPPORT`-assigned enemy with a
   damaged ally nearby; confirm it prefers `REVIVE_ALLY`/heal-category abilities over attacking,
   with `WB_CONSIDER_ALLY_IN_DANGER` dominating.
6. **Swarm doctrine + memory reaction.** Fight `GOBLIN`-tagged enemies (Brute + `WB_DOCTRINE_
   SWARM`). Use an AoE/row/column ability as the player twice. Confirm the log shows
   `WB_REACT_SAW_AOE` firing (a `WB_CONSIDER_ROW_POSITION_VALUE` weight delta appears in the
   breakdown) and that goblins visibly stop clustering on the grid in later turns.
7. **Healer-identified reaction.** If the current adventure has a healer-type enemy, have the
   player heal an ally (or otherwise trigger a comparable `WB_FACT_HEALER_IDENTIFIED`-eligible
   action) — actually, this reaction is about the *enemy* remembering the *player's* healer,
   so: have a player character heal another player character; confirm subsequent enemy turns'
   logs show a `TargetPriorityDelta` applied against that character and enemies visibly directing
   abilities at them.
8. **Guardian doctrine.** Fight the `BOSS_NECROMANCER_00`-style assignment (or whatever exact
   boss id is substituted per §11) with `WB_DOCTRINE_GUARDIAN`; confirm `TAUNT`-category
   candidates score higher than baseline and the guardian visibly intercepts/protects.
9. **Fail-safe.** Deliberately corrupt one profile's JSON (stray comma). Confirm: (a) startup/
   reload log loudly reports the parse failure for that file only; (b) entities assigned to the
   broken profile fall back to vanilla `AIHelper` decisions (no crash); (c) all other
   profiles/entities are unaffected.
10. **Master toggle regression check.** Set `Enabled=false`; confirm logs stop entirely and
    combat feels identical to unmodded vanilla — this is the "did I actually change anything"
    control test for every other step above.
11. **Memory scope.** Repeat step 6/7 with `MemoryScope=Off`: confirm no `WB_FACT_*`/reaction
    log lines appear and behavior reverts to profile+doctrine only. Repeat with `PerRun` across
    two consecutive battles in the same run: confirm a reaction triggered in battle 1 is still
    active at the start of battle 2 (until its `DecayBattles` limit, if any, expires).

## 9. Save & multiplayer considerations

- **MP posture: host-authoritative.** Per repo convention, AI-side mods run their decision
  logic only where the host computes it; WarBrain's prefixes are only meaningful on the
  instance that actually executes `AIHelper.BehaviourAiDecision`/`StandardAiDecision` for a
  given entity's turn. If the game's netcode already restricts these calls to the host (as is
  typical for authoritative turn resolution), no extra gating is needed — but this must be
  confirmed before shipping (§11). If it is *not* host-restricted, WarBrain needs an
  `IsHost`-style guard so non-host peers don't independently compute divergent decisions.
- **Data files are not per-save.** Profiles/doctrines/reactions/assignments are static mod
  content; every peer in an MP session needs the same files loaded for AI behavior to look
  consistent to all observers (screen-only divergence risk if peers run different WarBrain
  data — no state corruption, since only the host's decision is authoritative either way).
- **Per-battle memory** is ephemeral runtime state, not saved.
- **Per-run memory** (`MemoryScope=PerRun`) piggybacks `GameRunData`, so it saves/loads with the
  run exactly like the EOR Nemesis pattern it's modeled on — verify the exact `GameRunData`
  field used as the piggyback key does not collide with EOR's own usage (§11).
- No new network messages are introduced; WarBrain produces the same `CombatDecisionData`
  contract vanilla AI would have produced, so it rides existing combat-result networking as-is.

## 10. Milestones

- **M1 — Takeover + hardcoded-free scoring.** Harmony prefixes on both decision methods wired
  with fail-safe fallback; full candidate enumeration + consideration catalogue + curves +
  target filters + ability-category bias + temperature/softmax + difficulty scalars, all driven
  by shipped brain profiles via the assignment resolver. Verbose decision logging. No memory, no
  doctrines (doctrine fields may be parsed but are no-ops).
- **M2 — Memory.** `CombatHelper.PerformAbility` postfix, `WB_FACT_*` recording, decay rules,
  reaction application (weight/bias/target-priority deltas), `MemoryScope` knob including
  `PerRun` `GameRunData` piggyback.
- **M3 — Doctrines + difficulty scaling polish.** Doctrine delta layer fully wired
  (`ConsiderationWeightDeltas`/`Overrides`, `AbilityCategoryBiasDeltas`, `TemperatureDelta`,
  `ReactionRefs`), tag-based doctrine assignment, `GlobalIntelligenceScalar`/
  `GlobalTemperatureMultiplier`/`GlobalMistakeChanceAdd` fully combined with per-profile
  `DifficultyScaling`.

Each milestone is independently shippable: M1 alone is already "smarter AI," M2 adds memory on
top without touching M1's scoring, M3 adds squad personality and difficulty tuning on top of
both.

## 11. Open questions

- **Exact method signatures.** `AIHelper.BehaviourAiDecision`/`StandardAiDecision`,
  `CombatHelper.PerformAbility`, `ConfigsHelper.ReloadConfigs`, and `GameRunData.Create` are
  verified to exist (game-code-reference.md) but their parameter lists/return types are not
  enumerated there. Must confirm via dnSpyEx before writing the Harmony patches (the reference
  doc itself flags this as the standard caveat for anything not IL-verified in detail).
- **Combat-start reset hook.** No single documented method clearly fires exactly once at
  combat *start* (candidates from the doc: `CombatPhase._addEntityToCombat`,
  `_initializeNextWave`, or combat/`CombatState` construction itself). Need to confirm which
  hook cleanly resets per-battle memory without double-clearing on multi-wave fights.
- **`GameRunData` stable key field.** No `GameRunData` fields are enumerated in the docs. Need a
  stable per-run identifier (seed/guid) to key the per-run memory side-store, and to confirm it
  doesn't collide with EOR's own `GameRunData.Create` patch if EOR is also installed.
- **Host/client gating.** Need to confirm whether `AIHelper.BehaviourAiDecision`/
  `StandardAiDecision` are already only invoked on the host in MP, or whether WarBrain needs an
  explicit host check before engaging the takeover prefix.
- **Illustrative ids.** `BOSS_NECROMANCER_00` (used in the example assignment file) and the
  `CULTIST`/`GOBLIN` tag values are not literally enumerated in `docs/research/data-schemas.md`
  (that doc confirms `BOSS_*` as a 393-entry prefix and `GOBLIN`/`CULTIST` as faction tag values
  seen on `Things.json` items, but does not dump `Characters.json`'s actual id/tag list). Verify
  against the shipped `Characters.json` before treating the example assignment file as anything
  more than a template.
- **Player-side AI.** `AIHelper.PlayerPartyHasAI` implies player-controlled AI party members
  exist; whether WarBrain should also offer to drive them (same profiles, different default) is
  deferred — out of scope for M1–M3 above.
- **`WB_CONSIDER_TENDENCY_MATCH` fidelity.** Reusing `AIHelper._orderTargetsByTendency`'s
  *effect* (via its public callers) rather than reimplementing `eAiTendencies` ordering assumes
  that method's ranking is cheaply observable from a patch-adjacent call; if it's only reachable
  as a private implementation detail with no safe call surface, this consideration may need a
  from-scratch reimplementation of a subset of the 25 `eAiTendencies` values instead.
- **Damage prediction accuracy.** `WB_CONSIDER_EXPECTED_DAMAGE`/`KILL_SECURE` need a damage
  estimate before the ability actually resolves; whether to reuse `InteractableHelper.
  CalculateFinalDamage` directly (if its signature allows a dry-run) or approximate from raw
  stats/ability rolls is an implementation-time call, not a data-schema question.
