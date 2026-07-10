# FTK2.Summoner — SPEC

Plugin GUID: `ftk2mods.summoner` · Id prefix: `SMN_` · Priority: **P2**

## 1. Purpose & scope

FTK2.Summoner is a summon-and-evolution engine that delivers a trainer-style "raise a creature, fight
alongside it, watch it grow" fantasy inside FTK2's existing combat and party systems. It ships:

- A **Trainer** player class whose signature items summon elemental creatures into combat using the
  game's native `ADD_CHARACTER` action.
- An **evolution engine**: data-driven chains (`data/EvolutionChains/*.json`) that advance a creature
  through stages (new `Characters.json` config, new visuals) when kill/combat/level/item/quest triggers
  are met, persisted per run.
- A **persistent-companion knob**: the same creatures recruitable as permanent `COMPANION` followers
  (outside combat-summon mode), so the "pet that grows with you" fantasy also works between fights.
- A **bond/loyalty subsystem**: battles-together points that unlock stat bonuses or extra abilities on a
  companion, on a data-driven curve.

**Deliberately out of scope:** custom 3D models (no asset-bundle pipeline exists yet — see
`docs/feasibility.md` §7; visuals fall back to existing base-game models via a `VisualFallbacks.json`-style
map plus swappable PNG portraits/icons), a generic "capture wild creatures" loop (creatures are
class-granted starters, not overworld-caught), and any new `SKILL_*` C# logic beyond what's explicitly
called out in Milestones/Open Questions.

**IP note:** shipped example content (`SMN_PACK_STARTERS`) is original elemental creatures (a salamander,
a turtle, a sprout) — not reskins of any third-party property. Users may privately re-skin
PNG portraits/icons to taste; no such assets are shipped or distributed by this mod.

**Dependency:** the Trainer class deliverable depends on **FTK2.ClassForge** for class injection
(registering a new `PLAYER`-tagged `Characters.json` entry into the class-select flow — see §3 and §9).
Everything else (summon content, evolution engine, companion mode, bond subsystem) has no ClassForge
dependency and works with any `PLAYER`-tagged class, including a manually-added one.

## 2. Player-facing behavior

- Pick the **Trainer** class at the start of a run. It carries a starter weapon and a signature
  "satchel" item with three summon abilities — one per starter element (fire/water/plant).
- In combat, using a summon ability calls a creature onto the field (native `ADD_CHARACTER`/
  `TryCreateSummon` flow) at its **current evolution stage** for that run. The creature fights using its
  own stats/abilities/AI behaviour profile; it disappears at combat's end like any other combat summon.
- Kills scored by a summoned creature (and combats it survives) accumulate against that creature's
  **evolution chain**. When a stage's trigger is met (kill count, combats survived, Trainer level, an
  "evolution stone" consumable, or a quest flag), the *next* summon of that creature appears at the next
  stage: bigger stats, a new attack, a new portrait/model fallback. A stage-3 evolution stone item exists
  per element, consumed automatically when its threshold is also met.
- Evolution normally happens **between combats** (M1/M2): you fight with the old form, then the next
  time you summon it, it's evolved. An **in-combat transform** (mid-battle "it's evolving!" moment) is a
  stretch goal gated behind a knob (M3).
- With **Companion Mode** enabled (a knob, on by default), the same three creatures can instead be
  recruited as permanent `COMPANION` followers via the normal follower/recruitment flow — no combat
  summon needed; they travel with the party between fights. Fighting together with a companion earns
  **bond points**; crossing a bond threshold grants a small permanent stat buff or, at high bond, an
  extra ability. Evolution works identically for companions except the *actual persistent character* is
  rebuilt in place (no more "summon the new form next time" — the companion itself changes).
- Everything is tunable without recompiling: enable/disable, summon cap, notification style, bond gain
  rate, evolution trigger multipliers, and verbose logging are all BepInEx config knobs (§5).

## 3. Architecture

**Engine (C# plugin, `ftk2mods.summoner`)** owns three responsibilities, all mechanics, no content:

1. **Evolution-stage resolution** — because a summon ability's `ADD_CHARACTER` action bakes a single,
   static character-config id into its JSON (`Actions[].Item2.Value`, per
   `docs/research/data-schemas.md` §Abilities.json), the plugin must *substitute* the correct stage's
   config id at summon time. This is the one indispensable patch: a Harmony prefix on
   `CombatHelper.TryCreateSummon` (IL-verified entry point for `ADD_CHARACTER`/`ADD_CHARACTER_SMOKE`/
   `ADD_CHARACTER_INSTANT`, per `docs/research/game-code-reference.md` §2) that looks up the summoning
   character's persisted `EvolutionState` for the chain tied to that ability id, and rewrites the config
   id argument to the current stage's `CharacterConfigId` before vanilla creates the entity.
2. **Progress tracking** — counters (`KillsBySummon`, `CombatsSurvived`, bond points) live in plugin-owned
   per-run state piggybacked on `GameRunData` (the EOR Nemesis-system pattern explicitly called out in
   `docs/research/game-code-reference.md` §7 and CONVENTIONS.md). Kills/combats are attributed at
   well-defined choke points (combat-end cleanup, `ApplyAction` postfix — see §6) and checked against
   each chain's `AdvanceConditions` after every combat.
3. **Transform execution** — between-combat evolution for **combat-summon mode** requires no game call at
   all (the next `ADD_CHARACTER` just uses the new stage's config id, per point 1). Between-combat
   evolution for **companion/follower mode** calls
   `PartyManagementDirector._rebuildCharactertAsNewConfigType` directly against the follower's live
   character instance (the same "config-name rebuild" primitive the game uses for player class
   switching — CONVENTIONS.md §Naming/Architecture and `game-code-reference.md` §5). In-combat evolution
   (M3) is a stretch goal modeled on the `EncounterPhase._mimicTransformAndStartCombat` precedent, whose
   generality is unverified (see Open Questions).

**Data surface (`data/`)** owns everything a designer tunes: creature `Characters.json` entries, their
`Things`/`Abilities`/`StatusEffects`, `Behaviours.json` AI profiles, `Followers.json` companion entries,
and three new engine-defined registries:

- `data/EvolutionChains/*.json` — one file per chain; stages, triggers, visuals, evolution moment.
- `data/BondCurves/*.json` — bond-point gain rates and threshold rewards per companion.
- `data/VisualFallbacks.json` — EOR-pattern custom-id → base-game-id map for model/icon fallback
  (`docs/research/data-schemas.md` §EOR mod data surfaces), plus a portrait PNG path per stage.

**State lifecycle:**

| State | Scope | Lifetime | Storage |
|---|---|---|---|
| Active combat summon entity | per-battle | created on `ADD_CHARACTER`, torn down at combat end (`CombatHelper.TryEndTurnSummons`, `CombatPhase._endCombatAsync`'s summon cleanup) | native `CombatComponent`/venue entity — ephemeral, not ours |
| `EvolutionState` (per chain: `CurrentStage`, `KillsBySummon`, `CombatsSurvived`, `EvolutionStoneConsumed`) | per-run, per-owned-chain | survives save/load for the run | piggybacked on `GameRunData` |
| `BondState` (per companion: `BondPoints`, `ThresholdsGranted[]`) | per-run, per-companion | survives save/load for the run | piggybacked on `GameRunData` |
| Config/knobs | global | until changed | BepInEx config file |

Because combat summons are ephemeral (spawned fresh from `ADD_CHARACTER` every fight) but the game's own
turn/summon plumbing (`CombatComponent.CanSummon`, `TryCheckForSummonAvailability`) is not itself
evolution-aware, **the plugin — not the game — is the source of truth for "which stage does this
Trainer's fire starter currently summon."** The companion/follower character, by contrast, is a real
persistent entity in the roster; its config is the source of truth once rebuilt.

Master `Enabled` knob: if disabled, or if any `data/` file fails to parse, the plugin logs loudly (per
CONVENTIONS.md) and leaves the Trainer class, evolution chains, and follower entries completely inert —
vanilla `ADD_CHARACTER`/`Followers.json` behavior is unaffected because Summoner never registers content
that wasn't explicitly authored to use it.

## 4. Data file formats

### 4.1 `data/Characters/*.json` — creature & Trainer entries (native `Characters.json` shape)

Standard `CharacterConfig` shape (`docs/research/data-schemas.md` §Characters.json): `Stats{}` (subset of
the 20-key table), `Things{}` (itemId→qty — **abilities come from equipped Things**, not from the
character entry directly), `Passives[]`, `LootID`, `LocKey`, `Rarity`, `Level`, `Threat`, `BaseType`,
`Tags[]`, `Expansion`, optional `OnDeathAbility`/`CampQuery`/`SwarmQuery`/`DefaultBodyType`.

Trainer example — `data/Characters/SMN_Trainer.json`:

```json
{
  "SMN_TRAINER": {
    "Stats": { "HP": 30, "PA": 1, "SA": 1, "ACC": 75, "EVD": 8, "DEF": 3, "RES": 3, "SPD": 6, "FOC": 3, "LCK": 5, "CRT": 5 },
    "Things": { "SMN_ITM_STARTER_BLADE": 1, "SMN_ITM_TRAINERS_SATCHEL": 1 },
    "Passives": [],
    "LootID": "",
    "LocKey": "SMN_TRAINER",
    "Rarity": "COMMON",
    "Level": -1,
    "Threat": 0,
    "BaseType": "HUMAN",
    "Tags": ["PLAYER"],
    "Expansion": "BASE"
  }
}
```

Creature stage entry, one of nine — `data/Characters/SMN_Starters.json` (see §7 for the full pack):

```json
{
  "SMN_SALAMANDER_1": {
    "Stats": { "HP": 14, "ATK": 5, "DEF": 1, "RES": 1, "SPD": 7, "ACC": 72, "EVD": 8, "CRT": 8, "PA": 1, "SA": 1 },
    "Things": { "SMN_ITM_EMBER_CLAWS_T1": 1 },
    "Passives": [],
    "LootID": "",
    "LocKey": "SMN_SALAMANDER_1",
    "Rarity": "COMMON",
    "Level": 1,
    "Threat": 1,
    "BaseType": "IMP",
    "Tags": ["SMN_SUMMON", "SMN_CHAIN_SALAMANDER"],
    "Expansion": "BASE"
  }
}
```

### 4.2 `data/Abilities/*.json` — summon + attack abilities (native `Abilities.json` shape)

Per `docs/research/data-schemas.md` §Abilities.json: `TargetArea`, `Target`, `TileOccupancy`,
`OriginRowPosition`/`TargetRowPosition`, `IsMajorAction`, `RequiresFocus`, `Chargeable`, `Ammo`,
`Tendency`, `Actions[]` (`{Item1: eCombatActions, Item2: params}`). Summon abilities use
`ADD_CHARACTER`; attack abilities use `CHANGE_STAT` (± status via `ADD_STATUS`).

```json
{
  "SMN_ABILITY_SUMMON_SALAMANDER": {
    "TargetArea": "SELF",
    "Target": "SELF",
    "TileOccupancy": "ANY",
    "IsMajorAction": true,
    "RequiresFocus": 0,
    "Chargeable": false,
    "Tendency": "NONE",
    "Actions": [
      { "Item1": "ADD_CHARACTER", "Item2": { "Type": "ALLY", "Value": "SMN_SALAMANDER_1" } }
    ],
    "Tags": ["SMN_SUMMON_ABILITY"]
  },
  "SMN_ABILITY_EMBER_BITE_T1": {
    "TargetArea": "SINGLE",
    "Target": "ENEMY",
    "TileOccupancy": "ANY",
    "IsMajorAction": true,
    "RequiresFocus": 0,
    "Chargeable": false,
    "Tendency": "LEASTHEALTH",
    "Actions": [
      { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "FIRE", "IsBlockable": true, "FlatValue": -4 } }
    ],
    "Tags": []
  }
}
```

The `Value` in `ADD_CHARACTER` (`SMN_SALAMANDER_1` above) is always the **stage-1** id — the
`TryCreateSummon` prefix (§3, §6) is what makes a higher-stage creature actually appear once evolved.
`ADD_CHARACTER`'s `Type` field meaning is unconfirmed (see Open Questions); `"ALLY"` is a best-effort
placeholder.

### 4.3 `data/Things/*.json` — natural weapons, signature items, evolution stones (native shape)

Per §Things: `ConsumableType`, `Value`, `MinTier/MaxTier`, `Class`, `Rarity`, `Equippable{Slots[],
Stats{}, Passives[]}`, `Interactable{Abilities{}, AbilityBag[]}`, `Tags[]`. Item-side ability params are
`{MinValue, MaxValue, ACC, Stat, Rolls, Ammo}` per ability id.

```json
{
  "SMN_ITM_EMBER_CLAWS_T1": {
    "ConsumableType": "NONE",
    "Class": "BLADE",
    "Rarity": "COMMON",
    "Equippable": { "Slots": ["MAIN_HAND"], "Stats": {}, "Passives": [] },
    "Interactable": {
      "Abilities": { "SMN_ABILITY_EMBER_BITE_T1": { "MinValue": 3, "MaxValue": 5, "ACC": 72, "Stat": "STR", "Rolls": 1, "Ammo": -1 } },
      "AbilityBag": ["SMN_ABILITY_EMBER_BITE_T1"]
    },
    "Tags": ["SMN_NATURAL_WEAPON", "MELEE", "FIRE"]
  },
  "SMN_STONE_FIRE": {
    "ConsumableType": "NONE",
    "Class": null,
    "Rarity": "RARE",
    "Stacks": true,
    "Tags": ["SMN_EVOLUTION_STONE", "DUNGEON_MARKET"]
  }
}
```

Evolution stones are checked/consumed via `InventoryHelper.Consume`/`HasInteractable` (EOR-proven patch
targets, `game-code-reference.md` §7) when a chain's `ITEM_CATALYST` condition is evaluated — not via
native combat-consumable use. `Class: null` on the stone is a placeholder; see Open Questions.

### 4.4 `data/StatusEffects/*.json` (native shape)

Per §StatusEffects.json: `{Type, Duration, TickFrequency, TickCombat, TickExpire, Stats{}, Passives[]?}`.
New status ids reusing an existing `Type` are pure JSON.

```json
{
  "SMN_STATUS_SMOLDER": { "Type": "FIRE", "Duration": 2, "TickFrequency": 1, "TickCombat": true, "TickExpire": true, "Stats": { "HP": -2 } },
  "SMN_STATUS_SOAKED":  { "Type": "WATER", "Duration": 2, "TickFrequency": 1, "TickCombat": true, "TickExpire": true, "Stats": { "SPD": -1 } },
  "SMN_STATUS_THORNED": { "Type": "POISON", "Duration": 3, "TickFrequency": 1, "TickCombat": true, "TickExpire": true, "Stats": { "HP": -2 } },
  "SMN_STATUS_BOND_MINOR": { "Type": "BUFF", "Duration": -1, "Stats": { "DEF": 1, "HP": 5 } },
  "SMN_STATUS_BOND_MAJOR": { "Type": "BUFF", "Duration": -1, "Stats": { "STR": 2, "HP": 10 } }
}
```

**Design note (grounded correction):** the prompt's "FIRE/WATER-typed abilities via `CHANGE_STAT` Type"
only holds for FIRE — `WATER` is a confirmed `StatusEffectConfig.Type` value but is **not** in the
confirmed `CHANGE_STAT.Type` enum (`PHYSICAL MAGICAL BLEED FIRE INFINITE_FIRE POISON RATTLED REGEN
STRENGTH`, per `data-schemas.md` §Abilities.json). The water starter's damage therefore uses
`CHANGE_STAT` `Type: MAGICAL` and layers the "watery" flavor via `ADD_STATUS: SMN_STATUS_SOAKED`
(`Type: WATER`) instead of a nonexistent water damage type.

### 4.5 `data/Behaviours/*.json` (native shape)

`ProfileName → [{Category, Weight}]` over `eAbilityCategories` (14 confirmed values incl. `SUMMON`,
`TAUNT`, `DEBUFF`).

```json
{
  "SMN_BEHAVIOR_SALAMANDER": [ { "Category": "ATTACK", "Weight": 8 }, { "Category": "SUPPORT_SELF", "Weight": 1 } ],
  "SMN_BEHAVIOR_TURTLE":     [ { "Category": "ATTACK", "Weight": 4 }, { "Category": "TAUNT", "Weight": 5 }, { "Category": "SUPPORT_ALLY", "Weight": 1 } ],
  "SMN_BEHAVIOR_SPROUT":     [ { "Category": "ATTACK", "Weight": 3 }, { "Category": "DEBUFF", "Weight": 6 }, { "Category": "SUPPORT_ALLY", "Weight": 1 } ]
}
```

### 4.6 `data/Followers/*.json` (native shape, Companion Mode)

Per §Followers.json: `{Type, ConfigName, ClassName, ContractRounds, ContractPrice, MinTier, MaxTier,
Rarity, Behaviour, Subtitle, JoinParty, LeaveParty, Tags[], CaravanStatID, CaravanStatValue,
AppendableStats{}, GiveDeed, Rescued, Expansion}`.

```json
{
  "SMN_FOL_SALAMANDER": {
    "Type": "COMPANION",
    "ConfigName": "SMN_SALAMANDER_1",
    "ClassName": "",
    "ContractRounds": 0,
    "ContractPrice": "TINY",
    "MinTier": 1,
    "MaxTier": 3,
    "Rarity": "COMMON",
    "Behaviour": "SMN_BEHAVIOR_SALAMANDER",
    "Subtitle": "SMN_FOL_SALAMANDER_SUBTITLE",
    "JoinParty": "SMN_FOL_SALAMANDER_JOIN",
    "LeaveParty": "SMN_FOL_SALAMANDER_LEAVE",
    "Tags": ["COMPANION"],
    "AppendableStats": {},
    "GiveDeed": false,
    "Rescued": false,
    "Expansion": "BASE"
  }
}
```

`ConfigName` always points at the chain's **stage-1** config; evolving a companion calls
`_rebuildCharactertAsNewConfigType` to swap the live character's config to the current stage — the
`Followers.json` entry itself is only ever consulted at first recruitment.

Bond bonuses are **not** applied by mutating `AppendableStats` at runtime (its consumption code path is
unconfirmed — see Open Questions). Instead the engine applies a permanent (`Duration: -1`) `BUFF` status
via `InteractableHelper.ApplyStatus` (a fully-verified method) carrying the threshold's `Stats{}` delta —
see `SMN_STATUS_BOND_MINOR`/`_MAJOR` above.

### 4.7 `data/EvolutionChains/*.json` (engine-defined format)

```jsonc
{
  "<CHAIN_ID>": {
    "Element": "FIRE",                 // free-text flavor tag
    "DisplayName": "SMN_CHAIN_SALAMANDER_NAME",   // localization key
    "FollowerId": "SMN_FOL_SALAMANDER",           // optional — links Companion Mode entry
    "EvolutionMoment": "BETWEEN_COMBAT",          // BETWEEN_COMBAT (M1/M2) | IN_COMBAT (M3, gated)
    "Stages": [
      {
        "Stage": 1,
        "CharacterConfigId": "SMN_SALAMANDER_1",
        "VisualFallbackId": "SMN_SALAMANDER_1",   // key into data/VisualFallbacks.json
        "PortraitPath": "Portraits/smn_salamander_1.png",
        "AdvanceConditions": []                    // stage 1 is the entry point, nothing to satisfy
      },
      {
        "Stage": 2,
        "CharacterConfigId": "SMN_SALAMANDER_2",
        "VisualFallbackId": "SMN_SALAMANDER_2",
        "PortraitPath": "Portraits/smn_salamander_2.png",
        "AdvanceConditions": [
          { "Type": "KILLS_BY_SUMMON", "Count": 5 }
        ]
      },
      {
        "Stage": 3,
        "CharacterConfigId": "SMN_SALAMANDER_3",
        "VisualFallbackId": "SMN_SALAMANDER_3",
        "PortraitPath": "Portraits/smn_salamander_3.png",
        "AdvanceConditions": [
          { "Type": "KILLS_BY_SUMMON", "Count": 12 },
          { "Type": "ITEM_CATALYST", "ItemId": "SMN_STONE_FIRE" }
        ]
      }
    ]
  }
}
```

All entries inside `AdvanceConditions[]` are AND'd. `Type` vocabulary (engine-owned, extensible):

| Type | Params | Meaning |
|---|---|---|
| `KILLS_BY_SUMMON` | `Count` | cumulative kills scored by summons of this chain, this run |
| `COMBATS_SURVIVED` | `Count` | combats the summon was present at combat-end without dying |
| `SUMMONER_LEVEL` | `Level` | Trainer character `Level` stat ≥ value |
| `ITEM_CATALYST` | `ItemId` | Trainer's inventory contains and consumes this item (`InventoryHelper.Consume`) |
| `QUEST_FLAG` | `FlagId` | a quest-set flag/global (via the quest `GIVE_THING`/world-trigger surface, `data-schemas.md` §Quest system) is set |

### 4.8 `data/BondCurves/*.json` (engine-defined format)

```json
{
  "SMN_BOND_SALAMANDER": {
    "FollowerId": "SMN_FOL_SALAMANDER",
    "PointsPerCombatSurvived": 2,
    "PointsPerKill": 1,
    "Thresholds": [
      { "BondPoints": 10, "GrantStatus": "SMN_STATUS_BOND_MINOR" },
      { "BondPoints": 30, "GrantStatus": "SMN_STATUS_BOND_MAJOR" }
    ]
  }
}
```

An optional `GrantTrait` threshold field (instead of/alongside `GrantStatus`) grants a hidden `Class:
"TRAIT"` Thing via `CharacterHelper.GiveTrait` — used by the plant chain's top bond tier (§7) to grant
the existing `SKILL_SUMMONREVENGE` passive. Brand-new trait ids require `eTraits` enum injection (EOR
precedent proves it's possible; it is a C# patch, scheduled M2/M3 — see Milestones).

### 4.9 `data/VisualFallbacks.json` (EOR-pattern, engine-defined format)

```json
{
  "SMN_SALAMANDER_1": "TBD_VERIFY_EXISTING_IMP_MODEL",
  "SMN_SALAMANDER_3": "TBD_VERIFY_EXISTING_ELEMENTAL_MODEL",
  "SMN_TURTLE_1": "TBD_VERIFY_EXISTING_ELEMENTAL_MODEL",
  "SMN_SPROUT_1": "TBD_VERIFY_EXISTING_PLANT_MODEL"
}
```

Values prefixed `TBD_VERIFY_*` are explicit placeholders, not real config ids — see Open Questions. This
mirrors EOR's own `VisualFallbacks.json` (custom id → base-game id for model/icon,
`data-schemas.md` §EOR mod data surfaces).

## 5. Knobs

```
[General]
Enabled (bool, true) — master switch; if false, no patches apply and no Summoner content registers.
VerboseLogging (bool, false) — log evolution/bond decisions at LogLevel.Debug.

[Summons]
MaxSimultaneousSummons (int, 1) — cap on concurrently active combat summons per Trainer; enforced by
  the plugin (see §6) since vanilla per-character summon gating semantics are unconfirmed.
AllowInCombatEvolution (bool, false) — gates the M3 mid-battle transform moment; MP-unsafe by default.

[Evolution]
EvolutionTriggerMultiplier (float, 1.0) — multiplies every chain's Count/Level thresholds for balancing
  (e.g. 0.5 = evolve twice as fast).
EvolutionNotificationStyle (string enum: NONE | LOG_ONLY | DIALOGUE, default DIALOGUE) — how an
  evolution moment is surfaced to the player.

[Companion]
EnableCompanionMode (bool, true) — registers the SMN_FOL_* Followers.json entries; if false, starters
  are combat-summon-only.

[Bond]
BondGainRate (float, 1.0) — global multiplier on PointsPerCombatSurvived/PointsPerKill from BondCurves.

[Multiplayer]
OnParityMismatch (string enum: WarnAndSafeMode | WarnOnly | Block, default Block) — overrides
  ParityService's session-wide default (see MULTIPLAYER.md R1) for this mod specifically. Block is the
  Summoner-recommended choice (§9.5): a desynced creature config breaks entity resolution outright rather
  than merely going stale, so the softer WarnAndSafeMode is not safe enough here.
```

## 6. Patch targets & integration points

All names verbatim from `docs/research/game-code-reference.md`.

| Target | Kind | Why |
|---|---|---|
| `CombatHelper.TryCreateSummon` | Prefix | Resolve the summoning character's `EvolutionState` for the ability's chain and rewrite the `ADD_CHARACTER` config-id argument to the current stage's `CharacterConfigId` before vanilla creates the entity. Central mechanism of §3.1. |
| `CombatHelper.TryCreateSummon` (same patch, postfix half) | Postfix | Register the newly created ephemeral entity → `(OwnerId, ChainId)` in a per-battle dictionary so later kill/damage events can be attributed to a chain, and to count against `MaxSimultaneousSummons`. |
| `CombatHelper.TryCheckForSummonAvailability` | Postfix | Fold `MaxSimultaneousSummons` into the availability check (block another summon once the cap is reached), alongside whatever vanilla grid-space gating already exists. |
| `CombatHelper.ApplyAction` | Postfix | Detect `CHANGE_STAT` actions that bring an entity's HP to ≤0; if the acting entity is a registered chain summon, increment `KillsBySummon` for its chain. (No verified dedicated on-kill/on-death hook exists — see Open Questions.) |
| `CombatHelper.TryEndTurnSummons` | Postfix | Observation point for summons expiring/ticking at turn end; used to detect a summon surviving to combat's end for `COMBATS_SURVIVED`. |
| `CombatPhase._endCombatAsync` (summon cleanup local function) | Postfix (target method TBD — see Open Questions) | Finalize the battle: persist `KillsBySummon`/`CombatsSurvived` deltas to `GameRunData`, then evaluate every owned chain's `AdvanceConditions` and advance `CurrentStage` where met (between-combat evolution). |
| `PartyManagementDirector._rebuildCharactertAsNewConfigType` | Direct call (not patched) | Execute a companion/follower's evolution: rebuild the live character to the new stage's config. |
| `InventoryHelper.Consume` / `HasInteractable` | Direct call (not patched) | Check for and consume an evolution stone when evaluating an `ITEM_CATALYST` condition. |
| `EncounterPhase._mimicTransformAndStartCombat` | Reference precedent only (M3) | Proves combat-time transforms are technically possible in this engine; exact reuse is unverified (may be mimic-specific) — decompile before building the M3 in-combat evolution moment. |
| `GameRunData.Create` | Extend (EOR pattern) | Attach `SummonerRunState` (per-chain `EvolutionState[]`, per-companion `BondState[]`) so progress survives save/load, mirroring the Nemesis persistence pattern. |
| `CombatComponent.CanSummon` | Read only | Understand vanilla per-character summon gating before finalizing whether `MaxSimultaneousSummons` duplicates or complements it (semantics unconfirmed — see Open Questions). |

## 7. Example starting dataset — `SMN_PACK_STARTERS`

Three elemental chains, three stages each, fully wired end-to-end:

| Chain | Element | BaseType (verified enum value used) | Stage 2 trigger | Stage 3 trigger |
|---|---|---|---|---|
| `SMN_CHAIN_SALAMANDER` | Fire | `IMP` (stages 1–2) → `ELEMENTAL` (stage 3) | 5 kills | 12 kills + `SMN_STONE_FIRE` |
| `SMN_CHAIN_TURTLE` | Water | `ELEMENTAL` (all stages — see note below) | 5 kills | 12 kills + `SMN_STONE_WATER` |
| `SMN_CHAIN_SPROUT` | Plant | `PLANT` (all stages) | 5 kills | 12 kills + `SMN_STONE_PLANT` |

**Water starter BaseType note:** the prompt asked for a "shelled/aquatic base" akin to a crab/watcher.
`data-schemas.md` confirms only 8 of the game's 32 `BaseType` species (`HUMAN, SKELETON, DEMON, BIRD,
VOID, IMP, ELEMENTAL, PLANT`) — no aquatic/shelled type is enumerated anywhere in our reference docs.
Rather than invent an unverified enum value, the turtle chain uses the confirmed `ELEMENTAL` BaseType
(read as "water elemental") and defers the actual model choice entirely to `VisualFallbacks.json`
placeholders. **This needs verification**: browse `Characters.json` for an existing
crab/turtle/aquatic-shaped enemy to use as the real fallback model before shipping (see Open Questions).

Files delivered:

- `data/Characters/SMN_Trainer.json` — `SMN_TRAINER` (§4.1).
- `data/Characters/SMN_Starters.json` — 9 stage entries (3 chains × 3 stages), stats scaling up per
  element flavor (fire = high SPD/CRT low DEF; water = high DEF/HP low SPD; plant = high RES/THRN
  balanced), each equipped with a tiered natural-weapon Thing.
- `data/Abilities/SMN_Abilities.json` — 3 summon abilities (`ADD_CHARACTER`) + 9 tiered attack abilities
  (`CHANGE_STAT` with `FIRE`/`MAGICAL`/`POISON` types respectively, `ADD_STATUS` layered in at tiers 2–3)
  + 1 basic Trainer attack.
- `data/Things/SMN_Things.json` — Trainer's starter blade + signature satchel (3 summon abilities in one
  `Interactable.Abilities` dict), 9 tiered natural weapons, 3 evolution stones
  (`SMN_STONE_FIRE/_WATER/_PLANT`).
- `data/StatusEffects/SMN_StatusEffects.json` — `SMN_STATUS_SMOLDER` (FIRE), `SMN_STATUS_SOAKED`
  (WATER), `SMN_STATUS_THORNED` (POISON), `SMN_STATUS_BOND_MINOR`/`_MAJOR` (BUFF, for bond rewards).
- `data/Behaviours/SMN_Behaviours.json` — one AI profile per chain (§4.5).
- `data/Followers/SMN_Followers.json` — `SMN_FOL_SALAMANDER/_TURTLE/_SPROUT`, all `Type: COMPANION`,
  `ContractRounds: 0` (permanent).
- `data/EvolutionChains/SMN_CHAIN_SALAMANDER.json`, `SMN_CHAIN_TURTLE.json`, `SMN_CHAIN_SPROUT.json` —
  full 3-stage chains with the trigger table above.
- `data/BondCurves/SMN_BondCurves.json` — one curve per companion; the plant chain's top tier grants the
  existing `SKILL_SUMMONREVENGE` skill (via a new hidden trait, needing `eTraits` enum injection — see
  §4.8) as the "extra ability" bond reward, demonstrating that path alongside the pure-stat-buff path
  used by the other two.
- `data/VisualFallbacks.json` — placeholder `TBD_VERIFY_*` mappings for all 9 stage ids (§4.9).
- `data/Localization/en.json` — `ID`/`ID_DESCRIPTION` pairs for the Trainer, all 9 creature stages, and
  the 3 followers' subtitle/join/leave keys, per CONVENTIONS.md's localization rule.

Portrait PNGs referenced by `PortraitPath` are **not** included — this is a text/data-only example pack;
placeholder paths are declared so the evolution-chain schema is demonstrated end-to-end, but actual art
is a separate pass (Tier-2 asset-swap work per `docs/feasibility.md`).

## 8. Testing plan

Executable in under 15 minutes with the shipped example data, per CONVENTIONS.md:

1. **Load check.** Start the game with the mod installed; confirm the log shows `Enabled` = true and
   every `data/` file parsed without error (per-file "loaded N entries" log lines).
2. **Class select.** Confirm `SMN_TRAINER` appears as a selectable class (via FTK2.ClassForge injection,
   or the manual fallback — see §9/Open Questions if ClassForge isn't ready yet).
3. **Summon each starter.** In a fight, use each of the 3 signature abilities; confirm a stage-1 creature
   appears on the grid, acts on its own turn per its `Behaviours.json` profile, and disappears cleanly at
   combat end (no orphaned entities, no `CanSummon`/availability soft-lock on the next fight).
4. **Farm kills to stage 2.** Repeatedly summon one starter and land the killing blow with it (not the
   Trainer) across fights; confirm `KillsBySummon` increments only on summon-attributed kills (kills by
   the Trainer or other party members must NOT count), and that at 5 kills the *next* summon of that
   starter is stage 2 (bigger stats, new attack, `SMN_STATUS_SMOLDER`/`_SOAKED`/`_THORNED` now applying).
5. **Stage 3 with catalyst.** Reach 12 cumulative kills; confirm evolution does **not** fire without the
   matching `SMN_STONE_*` in inventory, then confirm it fires (and the stone is consumed) once acquired.
6. **Save/load persistence.** Mid-way through farming kills, save and reload; confirm `KillsBySummon`/
   `CombatsSurvived`/`CurrentStage` survived the round-trip (validates the `GameRunData` piggyback).
7. **MaxSimultaneousSummons.** Set the knob to 1 (default); confirm a second summon attempt is blocked
   while one is active. Raise to 2; confirm two can coexist.
8. **Companion Mode.** With `EnableCompanionMode` on, recruit a starter as a `COMPANION` follower;
   confirm it travels with the party outside combat, fights using its `Behaviours.json` profile, and that
   its bond points rise per combat survived/kill per `BondCurves`. Cross the first threshold; confirm the
   `SMN_STATUS_BOND_MINOR` buff (or the plant chain's `SKILL_SUMMONREVENGE` trait at its top tier) is
   applied and persists across save/load.
9. **Companion evolution.** Farm the companion's chain to stage 2/3; confirm
   `_rebuildCharactertAsNewConfigType` actually swaps the *same persistent character* (not a new roster
   slot) to the new config, preserving its bond state.
10. **Fail-safe.** Intentionally corrupt one `data/EvolutionChains/*.json` file (bad JSON); confirm the
    plugin logs loudly, disables only that chain (or the whole engine, per implementation choice — decide
    and document at implementation time), and vanilla `ADD_CHARACTER`/follower behavior elsewhere is
    unaffected.
11. **(M3) In-combat evolution.** With `AllowInCombatEvolution` on, trigger a mid-battle evolution moment;
    confirm the transformed creature keeps acting in the same battle without desyncing its turn/action
    state.
12. **MP smoke test — client-as-Trainer.** Host a session; the client picks the Trainer (ClassForge
    present on both peers, or the manual fallback per §11). The client summons a starter and fights two
    combats, farming it to at least stage 2. Confirm: (a) the summoned creature renders at the correct
    evolved stage on both the client's and the host's screens; (b) the host's UI — which never summoned
    anything itself — shows the correct `CurrentStage`/`KillsBySummon`/`CombatsSurvived`/bond display for
    the client's Trainer's chain, delivered via `SMN_SYNC_EVOLUTION_V1` (§9.4); (c) no log errors about
    unresolved config ids on either peer.
13. **MP late-join test.** With the session from step 12 still in progress (or a fresh one), have a third
    peer join mid-run. Confirm it sends `SMN_SYNC_EVOLUTION_REQUEST_V1` on join, receives a full
    `SMN_SYNC_EVOLUTION_V1` snapshot covering every chain/companion already in play, and that its UI
    matches the host's without having simulated any prior combat.
14. **Parity mismatch test.** Join with a client missing `ftk2mods.summoner` (or on a mismatched
    version/data). Confirm ParityService flags it by name and — per the §9.5/§5 `Block` default — the
    session is blocked rather than silently allowed to drift. Repeat with `OnParityMismatch` overridden to
    `WarnAndSafeMode` and confirm the SafeMode behavior described in §9.5 (existing creatures keep
    working, no new summons/evolutions).

Edge cases to explicitly hit: a summon that dies before its combat ends (should still count
`KillsBySummon` earned before death, should NOT count `CombatsSurvived`); a companion recruited mid-run
that already has kills attributed to that chain from earlier combat-summon use (decide whether progress
is shared across summon-mode and companion-mode for the same chain — flagged in Open Questions);
multiple starters of the same chain summoned simultaneously if `MaxSimultaneousSummons` > 1 (kill
attribution must be per-entity, not per-chain-aggregate, until they're reconciled at combat end).

## 9. Multiplayer

Co-op is a hard requirement (project decision, 2026-07-10); this section applies `docs/MULTIPLAYER.md`'s
repo-wide rules R1–R5 to Summoner specifically and is authoritative over any older single-player-first
framing in this document.

### 9.1 Parity class: `ALL_PEERS`

Creature `Characters.json`/`Abilities.json`/`Things.json`/`StatusEffects.json`/`Behaviours.json` entries,
plus the engine-defined `EvolutionChains/*.json`, `BondCurves/*.json`, and `VisualFallbacks.json`, all
merge into the shared `Configs` the game simulates from (MULTIPLAYER.md R1: "the game simulates from
`Configs`"). A peer without the same Summoner version and `data/` files cannot resolve `SMN_SALAMANDER_2`,
`SMN_STATUS_BOND_MAJOR`, a rebuilt companion's config id, etc., once another peer's session references
them. Every peer **must** run `ftk2mods.summoner` at a matching version with matching data — there is no
`HOST_ONLY` or `LOCAL` reduction available for this mod.

Note: the Trainer class itself is only reachable via FTK2.ClassForge's injection (§1, §3); that's a
parity concern one layer up the dependency chain, not Summoner's own data — see §11 for how ClassForge's
recommended mismatch policy should align with §9.5 below.

### 9.2 Feature table

| Feature | Label | Authority | Mechanism |
|---|---|---|---|
| Creature/Trainer/status/behaviour static data | `[SYNCED]` | all peers (config-shaped, no runtime sync) | merges into vanilla `Configs`; identical on every peer once ParityService confirms a match — "syncs for free" (MULTIPLAYER.md Practical guidance) |
| Evolution counters (`KillsBySummon`, `CombatsSurvived`) | `[SYNCED]` | host | tallied host-side from host-simulated combat events (`ApplyAction`/`TryEndTurnSummons` postfixes, §6); pushed to clients in `SMN_SYNC_EVOLUTION_V1` |
| Evolution stage decision (threshold crossed → advance `CurrentStage`) | `[SYNCED]` | host | evaluated host-side at combat end against `AdvanceConditions`; included in the same snapshot |
| Stage substitution at summon time (`TryCreateSummon` prefix) | `[SYNCED]` | host | the config-id rewrite runs wherever `TryCreateSummon` actually executes the entity-creation side of the vanilla combat-action pipeline — host, under the same host-authoritative-action assumption MULTIPLAYER.md open question #1 makes for AI actions (unverified until the shared decompile pass, not re-litigated per mod). The resulting spawn is a vanilla `ADD_CHARACTER` effect that replicates to clients as-is; clients never independently re-resolve the stage during combat |
| Out-of-combat stage/progress display, for any peer's UI (not just the owner) | `[SYNCED]` | host | no live entity exists between combats for combat-summon mode, so display state must come from a pushed snapshot rather than local re-derivation: `SMN_SYNC_EVOLUTION_V1` |
| Companion rebuild (`_rebuildCharactertAsNewConfigType`) | `[SYNCED]` | host call site, client mirroring branch-dependent | see §9.4 — gated by MULTIPLAYER.md open question #4 |
| Bond points / threshold grants | `[SYNCED]` | host | tallied host-side (`PointsPerCombatSurvived`/`PointsPerKill` × `BondGainRate`); status/trait grant applied host-side (`InteractableHelper.ApplyStatus`/`CharacterHelper.GiveTrait`); synced in the same `SMN_SYNC_EVOLUTION_V1` envelope |
| `MaxSimultaneousSummons` cap enforcement | `[SYNCED]` | host | folds into `TryCheckForSummonAvailability`, gating the same host-executed action pipeline as stage substitution — blocked actions are never broadcast, so no separate sync is needed |
| Evolution-stone catalyst check/consume | `[SYNCED]` | host | `InventoryHelper.Consume`/`HasInteractable` against the Trainer's inventory, itself host-authoritative party state |
| `VerboseLogging`, `EvolutionNotificationStyle` (toast/dialogue text) | `[LOCAL]` | per-peer | R4 — cosmetic; one peer may run `LOG_ONLY` while another runs `DIALOGUE` with no desync |

### 9.3 Determinism inventory (R2)

- Evolution thresholds (`AdvanceConditions[].Count`/`Level`), bond curves
  (`PointsPerCombatSurvived`/`PointsPerKill`/`Thresholds[].BondPoints`), and the
  `EvolutionTriggerMultiplier`/`BondGainRate` knobs are all **data or config**, never runtime rolls —
  every peer with matching data/knobs evaluates the identical threshold the identical way. `data/` falls
  under R1's `dataHash`; the two knobs above affect gameplay outcome and should be included in
  ParityService's `enabledFeatures`/config comparison, not just treated as cosmetic knobs.
- Kill/combat/level/quest-flag/item-catalyst conditions are all *counted* from host-simulated events
  (§9.2) — there is no dice roll anywhere in the evolution or bond path as specified; it is pure counting
  against data-defined thresholds.
- **Audit result: no RNG exists in this spec's mechanics today.** Nothing to remove or reroute. Flagging
  the rule now so it isn't missed on a future revision: if anything probabilistic is ever added (a
  "chance to evolve early," a bond-point crit, a randomized stage-3 stat roll), it must go through the
  game's deterministic `GameRandom` (EOR `EOR_SHARED_RNG` pattern), evaluated host-side and synced —
  never a local, unseeded `System.Random`.

### 9.4 Sync surface

**ParityService registration (R1):** `ftk2mods.summoner` registers `(guid, version, dataHash,
enabledFeatures)` at plugin load, where `enabledFeatures` includes at minimum
`EnableCompanionMode`/`AllowInCombatEvolution`/`EvolutionTriggerMultiplier`/`BondGainRate` (§9.3) and
`dataHash` covers every file under `data/` per R1's sorted/normalized hashing rule.

Vanilla mechanisms already carrying Summoner's effects, no bespoke sync needed:
- Creature/Trainer/status/behaviour **data** — merges into `Configs`, identical on all peers (R1), zero
  runtime sync.
- Combat-summon entity creation/actions/death — vanilla `ADD_CHARACTER`/combat-action replication
  (`CombatComponent` serialization, per MULTIPLAYER.md's verified/precedented list).
- Evolution-stone consumption — vanilla inventory state.
- Bond-reward stat buffs/traits — vanilla `StatusEffectConfig`/trait application on a real character,
  replicates like any other status/trait.

Custom `_SYNC_` action, host → clients:

**`SMN_SYNC_EVOLUTION_V1`** — versioned, idempotent (clients overwrite local `EvolutionState`/`BondState`
wholesale per key; re-applying the same snapshot twice is a no-op, EOR town-snapshot precedent).
- Payload: array of entries, one per `(OwnerId, ChainId)`: `{ OwnerId, ChainId, Mode:
  COMBAT_SUMMON|COMPANION, CurrentStage, CharacterConfigId, KillsBySummon, CombatsSurvived,
  EvolutionStoneConsumed[], BondPoints, BondThresholdsGranted[] }`.
- Fired: after combat-end evolution evaluation, after a companion rebuild, after a bond-threshold grant,
  and whenever the host's `EvolutionState`/`BondState` changes for any tracked chain/companion.
- Purpose: lets **every** peer's UI show accurate stage/progress/bond state for **any** Trainer's chains,
  without each peer independently owning or re-deriving the counters (the "out-of-combat display" row in
  §9.2).

**`SMN_SYNC_EVOLUTION_REQUEST_V1`** — empty/peer-id payload, client → host, sent on session join/rejoin.
Host responds with a full `SMN_SYNC_EVOLUTION_V1` covering every chain/companion in the current run (EOR
town-snapshot request pattern, MULTIPLAYER.md Practical guidance — "every `_SYNC_` snapshot must be
requestable").

**Companion rebuild propagation — both branches specced (MULTIPLAYER.md open question #4):**
- **Branch A — native propagation confirmed.** If `_rebuildCharactertAsNewConfigType`'s config-swap on a
  roster character replicates the same way ordinary character-config changes do, the host's single call
  is sufficient; vanilla character-state replication carries the new config to every peer's view of that
  companion for free. `SMN_SYNC_EVOLUTION_V1` is still sent (peers still need the counters for
  progress-bar/next-threshold display), but its `CharacterConfigId` field is redundant with what clients
  already have.
- **Branch B — native propagation not confirmed / disproven.** If the rebuild is a local-roster-only
  mutation that doesn't marshal across the network (plausible — it's described in `game-code-reference.md`
  §5 as a "config-name rebuild primitive," not a networked action), the host's call only updates the
  host's own view. Here `SMN_SYNC_EVOLUTION_V1`'s `CharacterConfigId`/`CurrentStage` field is load-bearing,
  and each client must call `_rebuildCharactertAsNewConfigType` **locally** against its own copy of that
  companion instance on receipt — "host-triggers + `SMN_SYNC` action," not "host mutates, clients watch."
- **Implementation default until the decompile pass resolves open question #4: build Branch B.** Always
  include `CharacterConfigId`/`CurrentStage` in the snapshot and always issue the local rebuild call on
  every peer (host included, as a harmless same-value no-op). This degrades gracefully into Branch A's
  behavior for free if native propagation turns out to already cover it, and never leaves a client peer
  showing a stale companion.

### 9.5 SafeMode definition & recommended default

If `ftk2mods.summoner` enters SafeMode (parity mismatch): no new combat summons (summon abilities become
inert/log-only), no evolution advancement, no companion recruitment, no bond gain. Creatures/companions
that were **already resolved before the mismatch was detected** keep working — their `Configs` entries
were already merged at load time, so an existing stage-2 companion doesn't disappear, it just stops
progressing for the rest of the session.

That guarantee is narrower than it sounds, so **Summoner recommends overriding the repo-wide
`[Multiplayer] OnParityMismatch` default (`WarnAndSafeMode`) to `Block`** for this mod specifically.
Rationale: a companion's persisted `CharacterConfigId` (which can already be at stage 2/3 from a prior
session — creature configs live in party state, not in a Summoner-owned sidecar) is only resolvable on a
peer that has the matching `data/Characters/*.json` entries. A peer joining or reconnecting *without* that
match cannot resolve the companion at all — not staleness, a broken/null character reference — and a live
combat-summon entity mid-fight on a divergent peer fails the same hard way. WarnAndSafeMode's "existing
state keeps working" guarantee only covers state resolved *before* divergence was detected; it does not
protect a late-joining or newly-mismatched peer. `Block` is the safer default here.

### 9.6 MP test plan

Summary — full steps in §8 items 12–14: host + one client, the client picks the Trainer and summons +
evolves a starter across two combats while the host (who never summoned anything) verifies correct stage
substitution on-screen and correct `CurrentStage`/counters/bond display via `SMN_SYNC_EVOLUTION_V1`; then
a late-joining third peer sends `SMN_SYNC_EVOLUTION_REQUEST_V1` and must display matching state without
having simulated any prior combat; then a parity-mismatch run confirms the `Block` default (§9.5) actually
blocks the session rather than silently drifting.

## 10. Milestones

- **M1 — Summon content + between-combat evolution (MVP), MP sync included.** Trainer class data (pending
  ClassForge injection, or a documented manual-slot fallback for testing), all 9 creature stages, summon +
  attack abilities, `TryCreateSummon` config-id substitution, `GameRunData`-backed
  `KillsBySummon`/`CombatsSurvived` counters, between-combat stage advancement, evolution-stone
  catalyst check, `MaxSimultaneousSummons` enforcement, `VerboseLogging`. **Plus, not deferred:**
  ParityService registration (§9.4) at plugin load, and the `SMN_SYNC_EVOLUTION_V1`/`_REQUEST_V1`
  snapshot (host → clients, with late-join request support, §9.4/§9.6) — MP sync ships with the evolution
  counters it describes, per the project's MP-first requirement (`docs/MULTIPLAYER.md`). Independently
  shippable: summon-only trainer gameplay with growing creatures, correctly synced in co-op, no companion
  mode yet.
- **M2 — Companion Mode + bond.** `Followers.json` registration, `Behaviours.json` profiles wired to
  companions, `BondCurves` engine (points, thresholds, `SMN_STATUS_BOND_*` application), companion
  evolution via `_rebuildCharactertAsNewConfigType`, the `SKILL_SUMMONREVENGE` bond-trait path (requires
  `eTraits` enum injection patch), `EnableCompanionMode`/`BondGainRate` knobs. **Plus:** the companion-
  rebuild sync branch (§9.4) — `SMN_SYNC_EVOLUTION_V1` entries for companions carry
  `CharacterConfigId`/`CurrentStage`, and each peer applies `_rebuildCharactertAsNewConfigType` locally
  (Branch B default) until MULTIPLAYER.md open question #4 is resolved. Independently shippable:
  companions can be recruited, bonded with, and evolved entirely outside combat-summon mode, in sync
  across peers.
- **M3 — In-combat evolution + polish.** Mid-battle transform (`AllowInCombatEvolution`), full
  `EvolutionNotificationStyle` options (toast/dialogue/silent), `EvolutionTriggerMultiplier` balancing
  knob, real portrait/model art pass replacing `TBD_VERIFY_*` placeholders. `AllowInCombatEvolution`
  remains off by default even with MP sync in place from M1 — a mid-battle transform is still the
  highest-desync-risk feature and needs its own §9-style analysis once `_mimicTransformAndStartCombat`'s
  generality is confirmed (Open Questions #5).

## 11. Open questions

1. **`CombatComponent.CanSummon` semantics are unverified** — unclear whether it's a per-character bool
   gate or an availability counter. Needs decompile before finalizing whether `MaxSimultaneousSummons`
   duplicates existing vanilla capacity logic or is purely additive.
2. **`TryCheckForSummonAvailability` / `TryEndTurnSummons` exact behavior is unverified** — does
   `TryEndTurnSummons` force summoned entities to act, expire them, or just tidy bookkeeping? This affects
   how `COMBATS_SURVIVED` should be measured and whether our postfix hooks fire at the right time.
3. **No verified on-kill/on-death C# event exists** in the reference docs. The kill-attribution plan
   (postfix `CombatHelper.ApplyAction`, check resulting HP ≤ 0, attribute via the registered
   entity→chain map) is inferred, not confirmed — `ApplyAction`'s exact parameter signature needs
   decompile verification.
4. **Exact Harmony target for the `CombatPhase._endCombatAsync` summon-cleanup local function
   (`g__cleanUpSummon`) is unknown** — compiler-generated local functions get mangled names; the real
   IL-visible method name needs decompiling before this patch can be written.
5. **`EncounterPhase._mimicTransformAndStartCombat` generality is unconfirmed** — it may be
   mimic-encounter-specific rather than a general combat-time transform primitive. Needed only for M3.
6. **No aquatic/shelled `BaseType` or specific existing base-game character model is confirmed** for the
   water starter (see §7 note) — needs a `Characters.json` browse/decompile pass to pick a real
   `VisualFallbacks.json` target; all `TBD_VERIFY_*` placeholders in the shipped `VisualFallbacks.json`
   need resolving before real art/model fallback ships, including the fire/plant lines' specific
   IMP/ELEMENTAL/PLANT-family model ids (only the BaseTypes themselves are confirmed to exist, not
   specific config ids within them).
7. **The `Things\Items` sub-schema (as opposed to Weapons/Attires/Traits) is not fully enumerated** —
   only 23 weapon/armor `Class` values are documented. The evolution-stone items model `Class: null`,
   which is a placeholder pending verification against a real Items JSON file.
8. **`Followers.json`'s `AppendableStats{}` consumption code path is unconfirmed** — the spec sidesteps
   this by applying bond bonuses via a verified mechanism (`InteractableHelper.ApplyStatus`) instead, but
   if `AppendableStats` turns out to be the "correct"/intended native path for permanent follower stat
   growth, the bond engine should likely be revised to use it.
9. **`Followers.json`'s `CaravanStatID`/`CaravanStatValue` purpose is unconfirmed** — left unset in the
   example dataset.
10. **Several field value formats are unconfirmed beyond field name** (`Rarity`'s full enum, `Expansion`'s
    type/format, the accepted "no loot table" sentinel for `LootID`, the full `ContractPrice` tier scale
    beyond the confirmed `TINY`/`HIGH` endpoints, `Equippable.Slots[]` enum values, `ADD_CHARACTER`'s
    `Type` field meaning). Placeholders used throughout the example dataset are best-effort guesses.
11. **FTK2.ClassForge's injection contract is undefined** — its own SPEC doesn't exist yet. This spec
    assumes ClassForge will expose something like "drop a `PLAYER`-tagged `Characters.json` entry and
    it's registered + shown in class-select," mirroring EOR's proven `UseExternalJsonCustomClasses`
    pattern. Reconcile once FTK2.ClassForge's SPEC is written; until then, M1 testing can use a manual
    fallback (temporarily overwriting an existing class slot, or a dev-console-driven class swap) to
    exercise the Trainer without waiting on ClassForge. **MP alignment:** per `docs/MULTIPLAYER.md` R1, a
    class that silently fails to appear for one peer is a worse desync than a warning — ClassForge's own
    `[Multiplayer] OnParityMismatch` should default to `Block`, not the repo-wide `WarnAndSafeMode`, the
    same reasoning as Summoner's own §9.5/§5 `Block` recommendation. Reconcile the two SPECs on this once
    ClassForge's own MP §9 is written, so a Trainer-class mismatch and a creature-config mismatch fail the
    session the same way.
12. **Ammo reset cadence for the signature satchel's summon abilities is unconfirmed** (per-combat vs.
    per-rest) — affects whether `MaxSimultaneousSummons` alone is sufficient to prevent summon-spam
    within a single fight, or whether `Ammo` needs an explicit per-combat reset patch too.
13. **Shared vs. separate progress between combat-summon mode and companion mode for the same chain** is
    an undecided design choice (see §8 edge cases) — needs a decision before M2 implementation: does
    farming kills with the *combat-summoned* salamander also advance the *companion* salamander's
    evolution (and vice versa), or are `EvolutionState` entries scoped per mode?
14. **MP open questions inherited from `docs/MULTIPLAYER.md`** (tracked centrally there, not re-litigated
    per mod, but §9's design depends on their resolution): open question #1 (is combat-action/AI execution
    host-only? confirms the `TryCreateSummon` prefix's assumed host location, §9.2); open question #4
    (does `_rebuildCharactertAsNewConfigType` propagate natively? decides whether §9.4's Branch A or
    Branch B is actually load-bearing — Branch B is implemented defensively regardless); open question #5
    (payload size limits on `_handleNetworkAction`? relevant if `SMN_SYNC_EVOLUTION_V1` grows large with
    many chains/companions across a long run — may require chunking or per-chain delta payloads instead
    of one array).
