# FTK2.ClassForge — SPEC

**Plugin GUID:** `ftk2mods.classforge` · **Id prefix:** `CF_` · **Priority:** P1

> **Grounding notice.** Every class/method/field name below is taken verbatim from
> `docs/research/game-code-reference.md` and `docs/research/data-schemas.md`. A handful of exact *string enum
> values* (not names) are not enumerated in those two docs — e.g. the precise `Rarity`/`Material` string set, the
> `Equippable.Slots[]` token vocabulary, and the internal field mapping of `ADD_CHARACTER`'s `{Type, Value}` pair.
> Every place this spec needs one of those, it uses an inferred placeholder and calls it out inline and in
> §11 Open Questions. Nothing here invents a class/method/field name that isn't in the two reference docs.

## 1. Purpose & scope

ClassForge is a **content-pack engine** for player classes. It lets a designer drop a self-contained folder
under `ClassPacks/<PackName>/` — classes, traits, abilities, starting gear, localization, icons/portraits — and
have the engine merge it into the game's own `Configs` registry at load time, the same way EOR runtime-merges its
Pets/Mercs/CustomItems packs. On top of the loader, ClassForge ships a small **skill-recipe framework**: a
data-driven registry that lets a class's signature mechanic (an HP-for-focus bargain, a taunt-on-hit maneuver, a
raise-the-fallen proc) be authored as JSON — trigger + condition + effect — instead of a bespoke Harmony patch
per class.

**In scope:** pack discovery/merge into `Configs.Characters/Things/Abilities`; the `eTraits` enum bridge for
custom trait ids; class-select UI injection; a generic `SKILL_*` proc handler driven by data ("skill recipes");
one complete example pack (`CF_PACK_BALDURS`, three classes).

**Out of scope (deliberately):** new `eCombatActions` verbs, new `StatusEffectConfig.Type` values, new 3D
models/animations (proxy to existing base types + custom 2D icons/portraits only), and any *genuinely novel*
mechanic that can't be expressed as a composition of existing actions/statuses — those need a one-off C# patch
authored outside this framework (ClassForge's job is to make that the rare case, not the common one).

## 2. Player-facing behavior

- A player installs a class pack (a folder under ClassPacks). On next launch, the pack's classes appear as
  selectable options in the class-select screen alongside vanilla classes (HUNTER, etc.), with their own
  portrait, name, and stat/gear preview.
- Picking a pack class starts the run with that class's `Stats`, starting weapon/gear (`Things`), and any
  innate signature passive baked into the class (e.g. the Hexblade Warlock immediately has "Eldritch Bargain"
  available — no trait pick required).
- At character creation / level-up trait picks, the pack's custom traits (e.g. "Cursed Strikes", "Relentless")
  appear in the trait list alongside vanilla traits (`TRAIT_TACTICIAN` etc.), each showing its own icon and
  description, and can be assigned like any other trait.
- In combat, the pack's abilities behave like any vanilla ability (targeting, focus cost, action cost) and its
  skill recipes proc silently in the background per their trigger — e.g. the Necromancer has a chance to raise
  a slain (non-skeleton) enemy as an allied skeleton immediately after the kill lands.
- With `VerboseLogging`/`SkillRecipeVerboseLogging` on, the BepInEx log records every pack load decision and
  every recipe proc/skip with its evaluated condition, for debugging without touching gameplay.
- Nothing changes for players who don't install any pack — the engine loads zero packs and is a no-op.

## 3. Architecture

### Engine (C#) vs data split

The engine hardcodes zero content. It provides four pieces of *mechanism*, each configured entirely from
`ClassPacks/*` data:

1. **Pack loader** — discovers `ClassPacks/<PackName>/pack.json` manifests, resolves load order/dependencies,
   parses `classes.json`/`traits.json`/`abilities.json`/`items.json`/`skillrecipes.json`/`localization/en.json`,
   and merges each into the matching `Configs` dictionary (`Configs.Characters`, `Configs.Things`,
   `Configs.Abilities`) by id. Icons/portraits are indexed by filename = content id.
2. **Trait bridge** — lets `Things` with `Class:"TRAIT"` whose id is *not* one of the 17 native `eTraits` values
   still be granted/removed/queried through the same `CharacterHelper.GiveTrait/RemoveTrait/RemoveAllTraits/
   GetFirstTrait` surface the game already uses. Exact mechanism is an open question (§11.1) — EOR already ships
   22 custom traits, so this is a solved problem; we need to decompile EOR's approach before implementing.
3. **Class-select UI injection** — patches `CharacterCustomizationViewHelper.RenderClassList` (and, for full
   polish, `RenderCustomizationContainer`/`RenderStatsContainer`) so pack classes appear in the roster with
   their stat preview and gear list rendered the same way vanilla classes are.
4. **Skill-recipe engine** — a small dispatcher that fires on a fixed set of trigger points, evaluates a
   recipe's `Conditions` against live combat state, and — if they pass — applies its `Effects` by constructing
   the equivalent `eCombatActions` entries and routing them through `CombatHelper.ApplyAction` /
   `InteractableHelper.ApplyStatus` / `InteractableHelper.ApplyStatChange`, exactly as if a vanilla ability had
   produced them. One generic handler serves every recipe; no per-class C# is needed unless a recipe's desired
   effect isn't expressible in the effect vocabulary (§4.6).

### Runtime flow

```
BepInEx Awake
  └─ ClassForgePlugin.Load()
       ├─ scan <plugin folder>\ClassPacks\*\ + any [Packs] AdditionalRoots
       ├─ for each folder with a pack.json: parse manifest
       ├─ topologically sort by loadOrder + dependencies (cycle/missing-dep → log loudly, skip pack)
       ├─ for each pack, in resolved order:
       │    ├─ merge classes.json  → Configs.Characters   (id collision: last pack wins, both pack ids logged)
       │    ├─ merge traits.json   → Configs.Things        (Class:"TRAIT" entries also register with the trait bridge)
       │    ├─ merge abilities.json → Configs.Abilities
       │    ├─ merge items.json    → Configs.Things
       │    ├─ merge skillrecipes.json → ClassForge's own SkillRecipe registry (NOT a Configs.* field — SKILL_*
       │    │    ids referenced from Passives[]/trait Things still need a SkillConfigs.json tuning entry only if
       │    │    they want native ProcChance/EVENT_PROC bookkeeping; ClassForge's dispatcher works without one)
       │    ├─ merge localization/en.json → Lang backing dictionary (before first Lang.__t call, or via a
       │    │    Lang.__t/SetLanguage patch per the EOR precedent)
       │    └─ index icons/*.png, portraits/*.png by filename for the AssetLoader.GetImage/GetRender patch
       ├─ patch CharacterCustomizationViewHelper.RenderClassList (if [UI] EnableClassSelectInjection)
       └─ register the generic skill-recipe dispatcher against its hook points (§6)
DevKit / ConfigsHelper.ReloadConfigs hot-reload (future) → re-run the merge idempotently (open question §11.8)
```

### State lifecycle

- **Persistent content (classes/traits/abilities/items/localization/icons):** loaded once per game session into
  the same `Configs` dictionaries vanilla content lives in. No new save data — a character created with a pack
  class is saved exactly like a vanilla-class character (its `Things`/`Passives`/stats are already how the game
  persists class identity). If a save is loaded with a pack *disabled*, that character's config id resolves to
  nothing — same failure mode as removing any mod's Things pack; call out in Testing (§8) and MP posture (§9).
- **Skill-recipe proc state (per-recipe cooldown counters):** per-battle, lives on the ClassForge plugin, reset
  on combat end — the CONVENTIONS.md default, and consistent with the native `PROC_COOLDOWN`/`SKILL_COOLDOWN`
  vocabulary already being battle-scoped tuning knobs in `SkillConfigs.json`.
- **Pack registry itself (manifest, load order, id→pack-origin map):** rebuilt on every load; not persisted.

## 4. Data file formats

Every file below lives inside one pack folder: `data/ClassPacks/<PackName>/`. All are UTF-8, no comments (the
game's own JSON parser doesn't tolerate them) — the annotated fences below are illustrative only; the actual
files under `data/` are strict JSON.

### 4.1 `pack.json` — the manifest

```jsonc
{
  "id": "CF_PACK_BALDURS",          // unique pack id; convention CF_PACK_<NAME>, enforced by loader (warn, not hard-fail)
  "name": "Baldur's Gate Companions", // display name (falls back to id if localization key missing)
  "version": "1.0.0",                // semver string, informational + used in load logs
  "author": "ftk2mods",              // optional
  "description": "...",              // optional, shown in a future pack-manager UI
  "loadOrder": 100,                  // int; lower loads first; ties broken alphabetically by id
  "dependencies": [],                // array of other pack ids that MUST load before this one;
                                      // missing/unresolved dependency → this pack is skipped, logged loudly
  "enabled": true                    // optional per-pack default; the [Packs] knob (§5) can override at runtime
}
```

### 4.2 `classes.json` — Characters.json-shaped

Same shape as the game's own `Characters.json` (verified fields: `Stats{}`, `Things{}`, `Passives[]`, `LootID`,
`LocKey`, `Rarity`, `Level`, `Threat`, `BaseType`, `Tags[]`, `Expansion`, optional `OnDeathAbility`,
`CampQuery`/`SwarmQuery`, `DefaultBodyType`). A pack's `classes.json` may contain both:

- **Player class entries** — `Tags` includes `"PLAYER"` — these are what the class-select UI injects.
- **Supporting non-player entries** — e.g. a summon character a class's abilities reference via `ADD_CHARACTER`
  (no `"PLAYER"` tag). These merge into `Configs.Characters` exactly the same way; the loader doesn't care.

```jsonc
{
  "CF_WARLOCK_HEXBLADE": {
    "Stats": { "ACC":4, "ATK":5, "AWR":3, "CRT":3, "DEF":2, "EVD":3, "FOC":6, "HP":28, "HRG":1, "INT":6,
               "LCK":2, "PA":1, "SA":1, "PRW":2, "RES":4, "SPD":3, "STR":3, "TAL":4, "THRN":1, "VIT":3 },
    "Things": { "CF_ITEM_PACT_BLADE": 1 },       // starting gear; abilities come from equipped Things
    "Passives": ["SKILL_CF_HEXBLADE_BARGAIN"],   // innate class passive — baked in, NOT trait-gated (see §3)
    "LootID": "",
    "LocKey": "CF_WARLOCK_HEXBLADE",
    "Rarity": "COMMON",         // placeholder string — exact Rarity enum not enumerated in the ground-truth docs (§11.5)
    "Level": -1,                // -1 = "not a fixed-level entity", matches the game's own HUNTER example
    "Threat": 0,
    "BaseType": "HUMAN",
    "Tags": ["PLAYER", "CF_PACK_BALDURS"],
    "Expansion": ""
  }
}
```

### 4.3 `traits.json` — Things-shaped `TRAIT` entries

Same shape as the game's `Things\Traits.json`: `Class:"TRAIT"`, `Hidden:true`, empty `Equippable.Slots`, power
from `Equippable.Stats` + `Equippable.Passives`.

```jsonc
{
  "CF_TRAIT_HEXBLADE_CURSE": {
    "ConsumableType": "NONE",
    "Value": 0,
    "MinTier": 1,
    "MaxTier": 1,
    "Class": "TRAIT",
    "Rarity": "COMMON",
    "Hidden": true,
    "Equippable": { "Slots": [], "Stats": {}, "Passives": ["SKILL_CF_CURSED_STRIKES"], "MaxCharges": 0 },
    "Tags": ["TRAIT", "CF_PACK_BALDURS"],
    "Expansion": ""
  }
}
```

Trait ids not in the native 17-value `eTraits` enum need the trait bridge (§3.2, §11.1) to actually be
grantable/removable in-game — until that lands (M2), a pack's traits still merge into `Configs.Things` and are
inert data, safe to ship ahead of the bridge.

### 4.4 `abilities.json` — Abilities.json-shaped

Same shape as the game's `Abilities.json`. Use `"Inherits": "DEFAULT_ABILITY"` and only override the fields that
differ — exactly the game's own convention — so unspecified fields (whose exact enum values aren't verified,
e.g. `DamageAgainstRow`) fall back to the base ability instead of being guessed.

```jsonc
{
  "CF_ABL_ELDRITCH_BLAST": {
    "Inherits": "DEFAULT_ABILITY",
    "TargetArea": "SINGLE",
    "Target": "ENEMY",
    "IsRanged": true,
    "IsMajorAction": true,
    "RequiresFocus": 0,
    "Tendency": "MOSTDAMAGE",
    "Actions": [
      { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "MAGICAL", "IsBlockable": true, "FlatValue": -6 } }
    ],
    "Tags": ["CF_HEX_MAGIC"]
  }
}
```

`Actions[].Item2` shapes are verified per `eCombatActions` entry:
- `CHANGE_STAT` → `{Stat, Type, IsBlockable, FlatValue?, FlatPercent?}`. `Stat` ∈ the ability-Actions vocabulary
  `{HP FOC MXHP MXFOC STR VIT MAG INT AWR TAL LCK SPD MOV PA SA DEF RES EVD PHY XP}` — **note this is a
  different, overlapping vocabulary from the character-sheet `Stats{}` keys** (e.g. `ACC`/`ATK`/`CRT`/`HRG`/
  `PRW`/`THRN` appear on the character sheet but not in `CHANGE_STAT`; `MAG`/`MOV`/`PHY`/`XP`/`MXHP`/`MXFOC`
  appear in `CHANGE_STAT` but not on the sheet). `Type` ∈ `{PHYSICAL MAGICAL BLEED FIRE INFINITE_FIRE POISON
  RATTLED REGEN STRENGTH}` — used `REGEN` for genuine heals, `MAGICAL`/`PHYSICAL` for damage/backlash by class
  flavor. Runtime semantics of `Type` on a *non-HP* stat (e.g. granting `PA`/`SA`/`FOC`) aren't verified — see §11.5.
- `ADD_STATUS`/`REMOVE_STATUS` → a bare status id string (or the category string `"DEBUFF"`).
- `ADD_CHARACTER` → `{Type, Value}` — verified as "summon by character config" but the two ground-truth docs
  don't specify which field carries the character-config id vs. the count. This spec's example data assumes
  `Type` = character config id string, `Value` = count int; **flagged as an open question (§11.4)** to confirm
  by IL inspection before M1 ships the Necromancer's summon ability.

### 4.5 `items.json` — starting-gear ThingConfigs

Same shape as the game's `Things\Weapons.json`/`Attires.json`. Fields: `ConsumableType, Value, MinTier/MaxTier,
Class, Rarity, Material, Hidden, Stacks, Ammo, Equippable{Slots[], Stats{}, Passives[], MaxCharges},
Interactable{Abilities{}, AbilityBag[], AbilityFillBag[], ShuffleAbilityBag}, Tags[], Expansion`.

```jsonc
{
  "CF_ITEM_PACT_BLADE": {
    "ConsumableType": "NONE",
    "Value": 30,
    "MinTier": 1,
    "MaxTier": 1,
    "Class": "BLADE",                 // verified weapon-class enum value
    "Rarity": "COMMON",               // placeholder, see §11.5
    "Material": "IRON",               // placeholder, see §11.5
    "Hidden": false,
    "Stacks": 1,
    "Ammo": 0,
    "Equippable": { "Slots": ["MAIN_HAND"], "Stats": { "ATK": 2, "STR": 1 }, "Passives": [], "MaxCharges": 0 },
    "Interactable": {
      "Abilities": {
        "CF_ABL_ELDRITCH_BLAST": { "MinValue": 4, "MaxValue": 8, "ACC": 70, "Stat": "INT", "Rolls": 1, "Ammo": 0 }
      },
      "AbilityBag": ["CF_ABL_ELDRITCH_BLAST", "CF_ABL_ELDRITCH_BLAST"],
      "AbilityFillBag": ["CF_ABL_ELDRITCH_BLAST"],
      "ShuffleAbilityBag": true
    },
    "Tags": ["DROPPABLE", "MELEE", "MAGICAL", "CF_PACK_BALDURS"],
    "Expansion": ""
  }
}
```

`Equippable.Slots[]` token values (`"MAIN_HAND"` above) are **not enumerated** in the two ground-truth docs —
the closest evidence is `HeroCharacterConfig`'s per-slot property names (`MainHand/OffHand/Helmet/Armor/Gloves/
Boots/Trinket/Pipe/BackPack`), which is a different config shape. Treated as a placeholder; verify against a
real `Weapons.json` entry before ship (§11.5).

### 4.6 `skillrecipes.json` — the skill-recipe schema (ClassForge's own file, not a native game file)

This is the extensibility contract for the custom skill-handler framework (task scope #3). One generic engine
handler serves every entry; a `SKILL_*` id here does not need a matching `SkillConfigs.json` entry unless the
designer also wants the native `PROC_CHANCE`/`EVENT_PROC` bookkeeping visible in vanilla UI tooltips.

```jsonc
{
  "SKILL_CF_HEXBLADE_BARGAIN": {
    "DisplayName": "Eldritch Bargain",
    "Trigger": "ON_ABILITY_USED",
    "Conditions": [
      { "Type": "ABILITY_TAG", "Value": "CF_BARGAIN" }
    ],
    "Effects": [
      { "Type": "STAT_CHANGE", "Target": "CASTER", "Stat": "HP", "StatChangeType": "MAGICAL", "FlatValue": -4, "Blockable": false },
      { "Type": "STAT_CHANGE", "Target": "CASTER", "Stat": "FOC", "StatChangeType": "MAGICAL", "FlatValue": 3, "Blockable": false }
    ],
    "ProcChance": 100,
    "AiProcChance": 100,
    "Cooldown": 0,
    "VerboseLogTag": "Hexblade Bargain"
  }
}
```

**Trigger** (enum, this mod's own — dispatch mechanism per trigger in §6):
`ON_CRIT, ON_KILL, ON_TURN_START, ON_TURN_END, ON_DAMAGED, ON_HEAL, ON_ABILITY_USED`. `ON_TURN_START`/
`ON_TURN_END` map onto the native `SkillConfigs.json` `EVENT_PROC` vocabulary (`START_TURN`/`END_TURN`) and
dispatch through the existing `CombatHelper._onCombatSkillProc` pipeline. The other five have no native
EVENT_PROC equivalent and need new Harmony hooks (§6); `ON_CRIT` specifically needs an unverified crit signal
(§11.2) — ship it in the schema, gate the actual dispatch behind a knob until confirmed.

Illustrative (not shipped in the example pack, but valid per the schema) examples of the remaining triggers:

```jsonc
// ON_TURN_START — "burns 1 HP per turn while cursed" style condition/effect pairing
{ "Trigger": "ON_TURN_START", "Conditions": [{ "Type": "HAS_STATUS", "Value": "CURSE" }],
  "Effects": [{ "Type": "STAT_CHANGE", "Target": "SELF", "Stat": "FOC", "StatChangeType": "MAGICAL", "FlatValue": 1, "Blockable": false }] }

// ON_DAMAGED — "gain a shield stack whenever hit below half HP"
{ "Trigger": "ON_DAMAGED", "Conditions": [{ "Type": "HP_THRESHOLD", "Comparator": "LTE", "Percent": 50 }],
  "Effects": [{ "Type": "ADD_STATUS", "Target": "SELF", "Status": "GUARD" }] }

// ON_HEAL — "healing a rowmate in the front row also cleanses one debuff"
{ "Trigger": "ON_HEAL", "Conditions": [{ "Type": "ROW", "Value": "FRONT" }],
  "Effects": [{ "Type": "REMOVE_STATUS", "Target": "TRIGGER_TARGET", "Status": "DEBUFF" }] }
```

**Conditions** (array, ANDed; empty array = always true). `Type` ∈:
- `HP_THRESHOLD` — `{Comparator: LTE|GTE, Percent: int}` against current/max HP (read via `CharacterHelper.GetStat`).
- `HAS_STATUS` / `LACKS_STATUS` — `{Value: <status id>}` against `StatusEffectComponent.Statuses`.
- `ROW` — `{Value: FRONT|BACK}` against `VenueComponent.TilePosition`/`VenueTileComponent.RowPositionsType`.
- `WEAPON_CLASS` — `{Value: <ThingConfig.Class>}` against the caster's equipped weapon (e.g. `BLADE`, `WAND`).
- `ABILITY_TAG` — `{Value: <tag>}` against the triggering ability's `Tags[]` (only meaningful for
  `ON_ABILITY_USED`).
- `TARGET_BASE_TYPE` / `TARGET_BASE_TYPE_NOT` — `{Value: <BaseType>}` against the trigger target's `BaseType`.

**Effects** (array, applied in order). `Type` ∈, each mapping onto an existing `eCombatActions`/status verb:
- `ADD_STATUS` / `REMOVE_STATUS` — `{Target, Status}` → native `ADD_STATUS`/`REMOVE_STATUS`.
- `STAT_CHANGE` — `{Target, Stat, StatChangeType, FlatValue|FlatPercent, Blockable}` → native `CHANGE_STAT`.
  Used for stat buffs/debuffs, "gain focus" (`Stat:"FOC"`), and "extra action" (`Stat:"PA"`/`"SA"`, `FlatValue:1`).
- `SUMMON` — `{Target, CharacterConfig, Count}` → native `ADD_CHARACTER` via `CombatHelper.TryCreateSummon`.
- `Target` ∈ `SELF|CASTER|TRIGGER_TARGET|TRIGGER_TARGET_POSITION|ALLY_ALL` depending on effect type.

**Not included in v1** — "gain gold" (explicitly requested in scope #3) has **no verified combat-time verb**:
the confirmed `CHANGE_STAT` `Stat` vocabulary has no `GOLD` entry, and gold grants only appear in overworld
reward verbs (`SkillEncounters.json`/`QuestTemplates.json` `GOLD` reward type), not in combat. Until a gold-grant
verb is found or patched, `GRANT_GOLD` is out of the effect catalog — see §11.6.

`ProcChance`/`AiProcChance` (0–100) mirror the native `PROC_CHANCE`/`AI_PROC_CHANCE` vocabulary. `Cooldown` is in
turns, `0` = unlimited, tracked per-battle only (state lifecycle, §3).

### 4.7 `localization/en.json`

EOR-style flat dictionary, `ID` and `ID_DESCRIPTION` keys, covering every class/trait/ability/item id the pack
introduces plus its own `LocKey`s:

```jsonc
{
  "CF_WARLOCK_HEXBLADE": "Hexblade Warlock",
  "CF_WARLOCK_HEXBLADE_DESCRIPTION": "A duelist bound to a pact that trades vitality for arcane power.",
  "CF_TRAIT_HEXBLADE_CURSE": "Cursed Strikes",
  "CF_TRAIT_HEXBLADE_CURSE_DESCRIPTION": "Critical hits curse the target."
}
```

### 4.8 `icons/` and `portraits/`

Loose PNGs, filename (without extension) = the content id they represent: `icons/CF_TRAIT_HEXBLADE_CURSE.png`,
`portraits/CF_WARLOCK_HEXBLADE.png`. The loader builds an id→file map per pack and the `AssetLoader.GetImage`/
`GetRender` patch (§6) consults it before falling back to the vanilla lookup — ClassForge's own equivalent of
EOR's `VisualFallbacks.json` convention (that file itself is an EOR-authored convention, not a native game
feature; ClassForge doesn't share EOR's file, it builds its own map from each pack's `icons`/`portraits`
folders).

## 5. Knobs

- `[General] Enabled` (bool, `true`) — master switch; if false, no pack is scanned and vanilla behavior is
  untouched.
- `[General] VerboseLogging` (bool, `false`) — pack discovery/merge decisions at `LogLevel.Debug`.
- `[Skills] SkillRecipeVerboseLogging` (bool, `false`) — logs every recipe evaluation (trigger fired, conditions
  checked pass/fail, effects applied) at `LogLevel.Debug`. Separate from the general knob because it's by far
  the highest-volume log source (fires every combat action).
- `[Packs] AdditionalRoots` (string, `""`) — comma-separated absolute paths to additional directories to scan
  for `ClassPacks/<PackName>/` folders, so other mods/authors can ship packs without touching ClassForge's own
  plugin folder.
- `[Packs] <PackId>.Enabled` (bool, `true`) — one dynamically-registered BepInEx entry per discovered pack
  (generated after the first scan pass), letting a player disable an individual pack without deleting it.
- `[UI] EnableClassSelectInjection` (bool, `true`) — the class-select UI integration toggle. If false, pack
  classes still exist in `Configs.Characters` (usable via console/dev tools or `LoadOuts.json`) but don't appear
  in the normal class-select screen.
- `[UI] EnableIconFallback` (bool, `true`) — toggles the `AssetLoader.GetImage`/`GetRender` icon/portrait
  fallback patch; off = packs render with the game's default missing-icon placeholder.

## 6. Patch targets & integration points

All verbatim from `docs/research/game-code-reference.md`.

| Target | Kind | Why |
|---|---|---|
| `ConfigsHelper.LoadConfigs` | Postfix | After vanilla config load, run the pack merge pass into `Configs.Characters/Things/Abilities`. |
| `ConfigsHelper.ReloadConfigs` | Postfix | Re-run the merge on hot-reload (DevKit loop); idempotency unverified — §11.8. |
| `CharacterCustomizationViewHelper.RenderClassList` | Postfix (or transpiler if the list is built before the postfix can append) | Inject pack `PLAYER`-tagged classes into the selectable roster. EOR already patches this exact method — proven hook. |
| `CharacterCustomizationViewHelper.RenderCustomizationContainer` / `RenderStatsContainer` | Postfix | M2 UI polish — full parity for pack classes' stat/gear preview rendering. |
| `CharacterHelper.GiveTrait` / `RemoveTrait` / `RemoveAllTraits` / `GetFirstTrait` | Prefix or Postfix (exact shape TBD) | Trait bridge — let a non-`eTraits` trait id flow through the same API surface. §11.1. |
| `CombatHelper._onCombatSkillProc` | Postfix | Native proc dispatch point for `ON_TURN_START`/`ON_TURN_END` recipes (reuses the existing `EVENT_PROC` pipeline). |
| `CombatHelper.ApplyAction` | Postfix | Observes every resolved `eCombatActions` entry — source signal for `ON_DAMAGED`/`ON_HEAL`/`ON_KILL` (HP hits ≤0 after a `CHANGE_STAT`). |
| `CombatHelper.PerformAbility` | Prefix or Postfix | Entry point for `ON_ABILITY_USED` — fires once per ability cast, before/after its `Actions[]` resolve. |
| `InteractableHelper.ApplyStatChange` | Postfix | Lower-level stat-delta resolution; likely where actual damage/heal magnitude is finalized — candidate source for `ON_DAMAGED`/`ON_HEAL` if `ApplyAction` proves too coarse. |
| `InteractableHelper.CalculateFinalDamage` | Postfix (conditional on §11.2) | Candidate source for an `ON_CRIT` signal, if one is IL-verified to exist on the return value. |
| `AssetLoader.GetImage` / `GetRender` | Prefix (return true/false to skip vanilla lookup) | Serve pack icons/portraits by id before falling back to vanilla. EOR already patches these — proven hook. |
| `Lang.__t` / `Lang.SetLanguage` | Postfix, or pre-populate the backing dictionary before first call | Merge pack localization strings. EOR already patches these — proven hook. |

## 7. Example starting dataset

`data/ClassPacks/CF_PACK_BALDURS/` — one complete pack, three fully-authored FTK×BG3 classes chosen as the most
data-only-feasible from the idea list (row control + native statuses + native `ADD_CHARACTER` summons cover all
three without needing a bespoke C# mechanic per class):

- **`CF_WARLOCK_HEXBLADE`** — Hexblade Warlock. Innate passive `SKILL_CF_HEXBLADE_BARGAIN` (HP-for-focus bargain,
  entirely skill-recipe-driven: the ability `CF_ABL_ELDRITCH_BARGAIN` carries no `Actions` of its own — it exists
  only to be the recipe's `ON_ABILITY_USED` trigger surface, demonstrating ability+recipe composition). Weapon
  `CF_ITEM_PACT_BLADE` grants `CF_ABL_ELDRITCH_BLAST` (damage), `CF_ABL_HEX` (`ADD_STATUS CURSE`),
  `CF_ABL_HELLISH_REBUKE` (damage + `ADD_STATUS DEATHMARK`), and the bargain ability. Two optional traits:
  `CF_TRAIT_HEXBLADE_CURSE` (grants recipe `SKILL_CF_CURSED_STRIKES`, `ON_CRIT` → curse the target) and
  `CF_TRAIT_PACT_BOON` (flat `FOC`/`INT` bonus, no recipe — demonstrates a pure-stat trait).
- **`CF_BATTLEMASTER`** — Battle Master. **No innate passive at all** — deliberately, to demonstrate that most
  class identity is pure JSON: its four abilities (`CF_ABL_TRIP_ATTACK` → damage + `ADD_STATUS ENTANGLE`,
  `CF_ABL_GOADING_STRIKE` → damage + `ADD_STATUS TAUNT`, `CF_ABL_MENACING_STRIKE` → damage + `ADD_STATUS SCARE`,
  `CF_ABL_COMMANDERS_STRIKE` → ally-targeted `CHANGE_STAT SA +1`) carry the whole class on their own. One
  optional trait, `CF_TRAIT_COMBAT_SUPERIORITY`, grants recipe `SKILL_CF_BM_RELENTLESS` (`ON_KILL` → regain a
  secondary action). A second trait, `CF_TRAIT_TACTICAL_MIND`, is pure-stat only.
- **`CF_NECROMANCER`** — Necromancer. Innate passive `SKILL_CF_NECRO_RAISE_ON_KILL` (`ON_KILL`, condition target
  isn't already a skeleton → `SUMMON CF_SKELETON_WARRIOR` at the kill's tile). Staff `CF_ITEM_NECROTIC_STAFF`
  grants `CF_ABL_RAISE_SKELETON` (direct `ADD_CHARACTER` summon), `CF_ABL_BONE_ARMOR` (`CHANGE_STAT DEF` +
  `ADD_STATUS GUARD`), `CF_ABL_DRAIN_LIFE` (damage, tagged `CF_DRAIN` for the Soul Siphon recipe to key off),
  `CF_ABL_DEATH_MARK` (`ADD_STATUS DEATHMARK`). Optional trait `CF_TRAIT_DEATHS_DESIGN` grants recipe
  `SKILL_CF_NECRO_SOUL_SIPHON` (`ON_ABILITY_USED`, condition ability tag `CF_DRAIN` → self-heal). A second trait,
  `CF_TRAIT_UNDEAD_LEGION`, is pure-stat only.
- **`CF_SKELETON_WARRIOR`** — supporting non-player summon entry (`BaseType:"SKELETON"`), armed with
  `CF_ITEM_BONE_CLAWS` granting `CF_ABL_BONE_STRIKE`, so the Necromancer's summons can actually fight.

Files: `pack.json`, `classes.json` (4 entries: 3 classes + 1 summon), `traits.json` (6 entries), `abilities.json`
(13 entries), `items.json` (4 entries), `skillrecipes.json` (5 recipes — covering `ON_ABILITY_USED` ×2, `ON_CRIT`
×1, `ON_KILL` ×2), `localization/en.json`, `icons/*.png` (6, one per trait), `portraits/*.png` (3, one per player
class).

**Placeholder values used in this example** (flagged, not silently asserted as fact — see §11.5): `Rarity:
"COMMON"` throughout; `Material: "IRON"` on the two metal weapons; `Equippable.Slots: ["MAIN_HAND"]` on all
weapons; `ADD_CHARACTER`'s `{Type: <config id>, Value: <count>}` field mapping.

## 8. Testing plan

Executable by a human in well under 15 minutes with the shipped `CF_PACK_BALDURS`.

1. **Pack load.** Start the game with `ftk2mods.classforge` installed and `CF_PACK_BALDURS` in `ClassPacks/`.
   Confirm the BepInEx log shows one "pack found: CF_PACK_BALDURS" line and one merge-count line per file
   (classes/traits/abilities/items/recipes/localization). Turn on `VerboseLogging` and confirm per-id merge
   entries.
2. **Class-select UI.** Open character customization. Confirm all three classes (Hexblade Warlock, Battle
   Master, Necromancer) appear with correct portrait, name, and stat preview. Toggle
   `EnableClassSelectInjection` off, confirm they disappear from the list (but still exist in `Configs`).
3. **Character creation — stats/gear/abilities.** Create one character per class. Verify: `Stats{}` matches
   `classes.json` on the character sheet; starting weapon is equipped and its 3–4 abilities are usable in a test
   fight; `CF_WARLOCK_HEXBLADE`/`CF_NECROMANCER` show their innate passive in the passive list;
   `CF_BATTLEMASTER` shows none (expected — no innate passive).
4. **Trait pick (M2-dependent).** At the trait-pick step, confirm the pack's 6 traits appear with correct
   icon/name/description and can be assigned. Assign one skill-recipe trait per class.
5. **Skill recipes — trigger each one, once per battle, with `SkillRecipeVerboseLogging` on:**
   - Warlock: cast `CF_ABL_ELDRITCH_BARGAIN` → log shows `SKILL_CF_HEXBLADE_BARGAIN` fired, `-4 HP`/`+3 FOC`
     applied to caster.
   - Warlock (with `CF_TRAIT_HEXBLADE_CURSE` equipped): land a critical hit → log shows
     `SKILL_CF_CURSED_STRIKES` fired (or explicitly "ON_CRIT signal not available" if §11.2 is still open at
     test time — acceptable degraded state, not a failure) and `CURSE` applied to the target.
   - Battle Master (with `CF_TRAIT_COMBAT_SUPERIORITY`): land a killing blow → log shows
     `SKILL_CF_BM_RELENTLESS` fired, `SA +1` applied.
   - Necromancer: land a killing blow on a non-skeleton enemy → log shows `SKILL_CF_NECRO_RAISE_ON_KILL`
     evaluated (respecting `ProcChance:35`, so may legitimately skip — re-test until it procs), on success a
     `CF_SKELETON_WARRIOR` appears at the kill's tile, allied.
   - Necromancer (with `CF_TRAIT_DEATHS_DESIGN`): cast `CF_ABL_DRAIN_LIFE` → log shows
     `SKILL_CF_NECRO_SOUL_SIPHON` fired, `+3 HP` (`REGEN`) applied to caster.
6. **Summon combat.** Confirm the raised/summoned `CF_SKELETON_WARRIOR` can take its turn and use
   `CF_ABL_BONE_STRIKE`, and is cleaned up at combat end (`CombatPhase._endCombatAsync` / `TryEndTurnSummons`).
7. **Edge cases to log and verify explicitly:** two packs defining the same content id (collision log line, last
   loader-order pack wins); a pack with an unresolved `dependencies` entry (pack skipped, logged, other packs
   still load); `Enabled = false` master knob (zero packs scanned, vanilla untouched); disabling one pack via its
   per-pack knob while others remain enabled.

## 9. Save & multiplayer considerations

- **Persistence:** no new save schema. A pack character's class/trait/gear identity persists exactly how vanilla
  already persists any character's config id, `Things`, and `Passives`. Skill-recipe cooldown counters are
  per-battle plugin state only, never saved (consistent with CONVENTIONS.md's per-battle default).
- **Single-player-first.** This spec assumes single-player as the primary target, per CONVENTIONS.md default.
- **Multiplayer posture:** every peer must have the same set of packs enabled, at the same versions, or a
  pack-class character will desync/resolve to nothing on a peer missing that pack's data — the same failure mode
  EOR's config+data hash handshake (`EOR_CFG`/`EOR_DAT`/`EOR_SIG`) exists to catch. ClassForge does not ship a
  sync handshake in M1–M3; this spec explicitly defers "verify all peers have matching packs" to a future
  milestone and, until then, treats mismatched packs across peers as **unsupported** (gate behind a knob-visible
  warning at session start, don't silently allow it).
- **Host authority:** enemy AI use of a pack class's abilities (if ever put on an enemy) runs host-side, per
  CONVENTIONS.md's AI-side default — not exercised by this spec's example pack (all `CF_PACK_BALDURS` content is
  player-facing), but noted for any future pack that reuses these classes as bosses/mercs.

## 10. Milestones

- **M1 — Pack loader; classes appear and play.** Manifest parsing, load-order/dependency resolution, `Configs.
  Characters/Things/Abilities` merge, localization merge, per-pack enable knob, minimal class-select injection
  (enough to pick and play a pack class in a real fight). Traits merge into `Configs.Things` as inert data (not
  yet grantable in-game — that's M2). No skill recipes execute yet; innate class `Passives[]` referencing
  `SKILL_*` ids that don't have a recipe registered are simply no-ops (fail-safe, not a crash).
- **M2 — Trait injection + UI polish.** Solve the `eTraits` enum bridge (§11.1) so pack traits are actually
  grantable/removable via the normal trait-pick flow; full `RenderCustomizationContainer`/`RenderStatsContainer`
  parity for pack classes; icon/portrait fallback patch live.
  Note that despite skill‑recipe execution technically being M3, this milestone also finishes bridging trait ids
  so trait‑granted recipes (§7) can be assigned in-game — they still won't *fire* until M3.
- **M3 — Skill-recipe framework.** The generic dispatcher and all six hook points in §6; every recipe in the
  example pack fires and is verifiable per the Testing plan (§8, steps 5–6); `SkillRecipeVerboseLogging` knob
  live.

Each milestone is independently shippable: M1 alone gives players three new playable classes with full stats/
gear/abilities; M2 adds trait depth; M3 adds the signature procs.

## 11. Open questions

1. **`eTraits` enum bridge mechanism (blocks M2).** The two ground-truth docs confirm `eTraits` has 17 values
   and is consumed by `CharacterHelper.GiveTrait/RemoveTrait/RemoveAllTraits/GetFirstTrait` and
   `CombatHelper.RecalculateLoneWolf`, and that EOR already ships 22 custom trait ids successfully — but not
   *how*. Decompile `EnhancedOverhaulRemix.dll`'s trait-injection code before implementing M2: does it patch the
   four `CharacterHelper` methods to consult a shadow string-keyed registry when the id isn't a valid enum
   value, or does it runtime-extend the enum itself, or something else?
2. **`ON_CRIT` signal availability.** No confirmed field/return value exposes "was this hit a critical" to a
   Harmony patch on `ApplyAction`/`ApplyStatChange`/`CalculateFinalDamage`. Verify via IL/dnSpyEx before M3 ships
   `ON_CRIT` support; ship without it (recipes using it simply never fire, logged once) if unconfirmed.
3. **`ConfigsHelper.ProcessDirectory`/`CONFIGS_JSON_SOURCES` signature.** Would let packs be picked up by the
   *native* config loader instead of a bespoke postfix merge, if it accepts additional directories. Needs
   inspection before deciding the exact merge implementation in §3/§6.
4. **`ADD_CHARACTER`'s exact `{Type, Value}` field mapping.** This spec assumes `Type` = character-config id
   string, `Value` = count. Confirm by IL inspection before the Necromancer's summon ability (`CF_ABL_
   RAISE_SKELETON`) and the `SUMMON` recipe effect ship.
5. **Unverified exact string-enum values** used as placeholders in the example data: `Rarity` (used `"COMMON"`),
   `Material` (used `"IRON"`), `Equippable.Slots[]` tokens (used `"MAIN_HAND"`), and the runtime meaning of
   `CHANGE_STAT.Type` when `Stat` isn't `HP` (used `MAGICAL`/`PHYSICAL`/`REGEN` by flavor, semantics unconfirmed).
   Verify each against a real vanilla `Weapons.json`/`Materials.json` entry before ship.
6. **"Gain gold" effect has no verified combat-time verb.** Requested in scope #3's effect catalog but not
   backed by anything in `CHANGE_STAT`'s `Stat` vocabulary (gold grants only appear in overworld reward verbs).
   Decide: drop it from v1's effect catalog (current plan), or scope a small dedicated C# effect for it.
7. **Collision/dependency policy.** Current plan is "last pack in load order wins, log both pack ids" for id
   collisions and "skip pack, log loudly" for unresolved dependencies. Confirm this is the desired UX before
   other mod packs start shipping against ClassForge (semantic-version dependency ranges vs. simple presence
   checks is also unresolved).
8. **Hot-reload idempotency.** Whether `ConfigsHelper.ReloadConfigs` cleanly re-triggers the pack-merge postfix
   without duplicating/leaking dictionary entries needs verification before wiring into `FTK2.DevKit`'s hot-reload
   loop.
9. **Skeleton reuse.** Whether the base game ships its own skeleton enemy character configs (a `SKELLY_*`-style
   id under `BaseType:"SKELETON"`) that the Necromancer could summon instead of (or in addition to) the pack's
   own `CF_SKELETON_WARRIOR` is unverified in the two ground-truth docs — grep a real `Characters.json` for
   `BaseType:"SKELETON"` entries before deciding; not a blocker since `CF_SKELETON_WARRIOR` is fully
   self-contained.
