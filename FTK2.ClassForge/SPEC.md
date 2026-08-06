# FTK2.ClassForge — SPEC

**Plugin GUID:** `ftk2mods.classforge` · **Id prefix:** `CF_` · **Priority:** P1

> **Grounding notice.** Every class/method/field name below is taken verbatim from
> `docs/research/game-code-reference.md` and `docs/research/data-schemas.md`. A handful of exact *string enum
> values* (not names) are not enumerated in those two docs — e.g. the precise `Rarity`/`Material` string set, the
> `Equippable.Slots[]` token vocabulary, and the internal field mapping of `ADD_CHARACTER`'s `{Type, Value}` pair.
> Every place this spec needs one of those, it uses an inferred placeholder and calls it out inline and in
> §11 Open Questions. Nothing here invents a class/method/field name that isn't in the two reference docs.

## Status (2026-07-25)

**M1 (pack loader) + M2 (trait injection) + M3 (skill-recipe engine) are implemented** on branch
`engine/eor-rehost` — `ClassForge.Core` (21 tests) + `ClassForge.Recipes` (149 tests), all green;
**offline-verified only, in-game smoke test pending** (no restored game install at implementation time —
see the operator handoff, `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md`). This spec
now describes the vocabulary and MP posture **as shipped**, folding in `SPEC-DELTA-v1.1.md` (skill-recipe
vocabulary v1.1 — kept as a historical document, header-noted "merged into SPEC.md on 2026-07-25") and the
corrections from the adversarial MP-correctness review, `docs/research/eor-rehost-mp-review.md`. Where this
spec and that review disagree about what shipped, the review (and the code it audited) wins; the fixes it
recommended for ClassForge (M0 adds-only enforcement, B3/B4/M1/M2 parity-registration and Block-semantics
fixes) are already applied in the current code and are described below as implemented, not as open findings.

**Update (2026-08-06) — recipe vocabulary bumped to v1.2.** Two Wave-2 capabilities landed on
`engine/eor-rehost`, offline-verified (`ClassForge.Recipes` 287 tests, `ClassForge.Core` 32 tests,
`DevKit.Core` 91 tests — all green; in-game smoke still pending, same caveat as M1–M3 above):

- **The loot-grant sync verb** (`ON_COMBAT_LOOT` trigger + `GOLD_GRANT`/`ITEM_TAG_GRANT`/`LOOT_SCALE` effects
  + `PickOneEffect`, `CF_SYNC_LOOT_GRANT_V1` host mirror+audit) — commits `fc17c53` (M-LG1 offline core),
  `f7b14b9` (M-LG2 hooks + SP + consumer recipes, dark), `22131ce` (M-LG3 MP wire-up). Ships **dark** behind
  `[Skills] EnableLootGrants = false`, flip gated on operator smoke item V-1. Full design:
  `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md`.
- **The encounter-modifier engine capability** (`Scope: COMBAT` ownerless recipes, `ProcChanceFormula`,
  `SELECTION_SET`/`StatusFromSelection`/`EVENT_BANNER`, 9 new conditions incl. `IS_ENEMY`, `STAT_CHANGE`
  `FlatValueFrom: "TARGET_MXHP_PCT"`, the `statuses.json`/`modifiers.json` pack file types and their
  `Configs.StatusEffects` merge category) — commits `f8c4eaf` (M-EM1 pack surface), `a92a0e3` (M-EM2 engine
  capability), `d293536` (M-EM3/4 generated recipes + reward halves). Ships live via
  `CF_PACK_ENCOUNTER_MODIFIERS`; reward halves ride the loot-grant verb (above) in Mode M. Full design:
  `docs/superpowers/plans/2026-08-05-encounter-modifiers-spec.md`.

Both are described below as implemented, folded into the §4.6 vocabulary tables rather than kept as a
separate delta document.

## 1. Purpose & scope

ClassForge is a **content-pack engine** for player classes. It lets a designer drop a self-contained folder
under `ClassPacks/<PackName>/` — classes, traits, abilities, starting gear, localization, icons/portraits — and
have the engine merge it into the game's own `Configs` registry at load time, the same way EOR runtime-merges its
Pets/Mercs/CustomItems packs. On top of the loader, ClassForge ships a small **skill-recipe framework**: a
data-driven registry that lets a class's signature mechanic (an HP-for-focus bargain, a taunt-on-hit maneuver, a
raise-the-fallen proc) be authored as JSON — trigger + condition + effect — instead of a bespoke Harmony patch
per class.

**In scope:** pack discovery/merge into `Configs.Characters/Things/Abilities`; `TRAIT_`-prefix trait injection
via the vanilla loadout pool (no `eTraits` bridge needed — §3 point 2); class-select UI injection; a generic
`SKILL_*` proc handler driven by data ("skill recipes"); one complete example pack (`CF_PACK_BALDURS`, three
classes) plus a second, EOR-derived pack (`CF_PACK_EOR_CLASSES`, 31 classes / 20 traits / 48 recipes).

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
2. **Trait mechanism — resolved: there is no bridge, because there is no enum gate.** `eTraits` is dead code
   (zero qualified `eTraits.<Member>` usages outside its own declaration in the decompile) and additionally
   stale (lists `TRAIT_LONGLEGS`, no live data; omits `TRAIT_LUCKY`/`TRAIT_SUPPORTIVE`, both live). The entire
   runtime trait substrate is keyed on the string prefix `"TRAIT_"` on `Thing.ConfigName`:
   `CharacterHelper.GiveTrait(Entity, string)` does an unvalidated `Things.Add`; `InventoryHelper.GetTraits`
   is `FindAll(t => t.ConfigName.StartsWith("TRAIT_"))`; `EquipmentHelper.GetEquippedThingsNonAlloc(...,
   pIncludeTraits: true)` treats any `TRAIT_`-prefixed `Thing` as equipped regardless of slot, which is why a
   `Equippable.Slots: []` trait already contributes its `Equippable.Stats` through the native `GetStat` path
   with zero patches. So: **a ClassForge trait is a `ThingConfig` matching `^TRAIT_[A-Z0-9_]+$`, nothing
   more** — grant/remove/query all flow through the untouched native API, and the pack loader hard-rejects a
   `traits.json` entry whose id doesn't start with `TRAIT_` (the prefix is not cosmetic, it *is* the
   mechanism). **Selection** is a Postfix on `LootDropHelper.GetAdventureLoadOut(string, GameRandom)` that
   appends one `InventoryHelper.CreateThing(traitId)` per pack trait not already in the vanilla loadout pool,
   tagged `"LOADOUT_0"` so it prices at 0 — the vanilla `PartyManagementDirector` take/untake action performs
   the actual `Things.Add` and rides the already-networked loadout-pick pipeline. Injected `Thing.Id` is
   deterministic (`SHA256("CF_THING_ID|" + packId + "|" + traitId)`, first 12 hex chars), never a runtime
   GUID, so the pool resolves to the same identity on every peer. Full detail, citations and the MP-ordering
   caveat: §4.6, §9.
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
       ├─ sort discovered pack ids alphabetically (deterministic discovery — never trust filesystem/OS
       │    directory-listing order, which is not guaranteed identical across peers)
       ├─ topologically sort by loadOrder + dependencies (cycle/missing-dep → log loudly, skip pack;
       │    ties broken alphabetically by id, §4.1 — this is what makes merge order, and therefore the
       │    ParityService dataHash below, a pure function of which packs are enabled, per MP R2)
       ├─ snapshot the live (pre-merge) Configs.Characters/Things/Abilities id sets (adds-only enforcement,
       │    below)
       ├─ for each pack, in resolved order:
       │    ├─ merge classes.json  → Configs.Characters   (a candidate id already present in the LIVE
       │    │    (pre-pack) id set is refused outright — Error finding, entry dropped, never reaches the
       │    │    pack-vs-pack check; among packs, id collision: last pack wins, both pack ids logged)
       │    ├─ merge traits.json   → Configs.Things        (id must match `^TRAIT_[A-Z0-9_]+$` or the entry is
       │    │    hard-rejected — §3 point 2, §4.3; no separate "trait bridge" registration step exists)
       │    ├─ merge abilities.json → Configs.Abilities
       │    ├─ merge items.json    → Configs.Things
       │    ├─ merge statuses.json → Configs.StatusEffects (v1.2, new merge category — same adds-only /
       │    │    last-pack-wins-among-packs / live-id-refused semantics as the three above; every `Passives`
       │    │    entry must resolve to a recipe id in the same pack or an `eSkills` member — §4.6)
       │    ├─ merge modifiers.json → ClassForge's own EncounterModifier registry (v1.2, NOT a Configs.* field,
       │    │    mirrors skillrecipes.json below — feeds the generated `Scope:COMBAT` select/apply recipe pair)
       │    ├─ merge skillrecipes.json → ClassForge's own SkillRecipe registry (NOT a Configs.* field — SKILL_*
       │    │    ids referenced from Passives[]/trait Things still need a SkillConfigs.json tuning entry only if
       │    │    they want native ProcChance/EVENT_PROC bookkeeping; ClassForge's dispatcher works without one)
       │    ├─ merge localization/en.json → Lang backing dictionary (before first Lang.__t call, or via a
       │    │    Lang.__t/SetLanguage patch per the EOR precedent)
       │    └─ index icons/*.png, portraits/*.png by filename for the AssetLoader.GetImage/GetRender patch
       ├─ patch CharacterCustomizationViewHelper.RenderClassList (if [UI] EnableClassSelectInjection)
       ├─ register the generic skill-recipe dispatcher against its hook points (§6)
       └─ register (guid, version, dataHash, enabledFeatures) with FTK2Mods.DevKit's ParityService (§9.6)
DevKit / ConfigsHelper.ReloadConfigs hot-reload → re-run the merge idempotently (resolved, §11 #8: both entry
  points rebuild Configs from scratch, so re-merging cannot double-insert) and re-register with ParityService
  so a hot-reloaded dataHash is re-checked
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

### Multiplayer parity registration

Per `docs/MULTIPLAYER.md` R1, `ClassForgePlugin.Load()` registers with `FTK2Mods.DevKit.ParityService`
(reflection-based call, no hard build dependency — `Type.GetType("FTK2Mods.DevKit.ParityService,
ftk2mods.devkit")`, `RegisterWithCallback` with an `Action<string[]>` callback, resolving DevKit SPEC §11.12)
once the pack-merge pass completes:

- `guid` = `ftk2mods.classforge`; `version` = the plugin's assembly version.
- `dataHash` = SHA-256 over every file under each *enabled* pack's `ClassPacks/<PackName>/` tree, **excluding
  `localization/**`** (R1: localization files are parity-exempt — text-only, no gameplay-state effect), computed
  over sorted pack ids then sorted file paths within each pack, normalized line endings, invariant culture — the
  same determinism discipline the merge pipeline itself uses (below). Prefixed `sha256:` + 64 hex chars, matching
  DevKit's canonical `IsWellFormedHash` shape (an early build shipped a bare-hex hash that failed that check and
  forced every comparison to `DataMismatch`; fixed).
- `enabledFeatures` = the sorted list of active (post-per-pack `[Packs] <PackId>.Enabled`-filtered) pack ids —
  e.g. `["CF_PACK_BALDURS"]` — a *pack*, not an individual content id, is the parity-relevant unit — **plus** one
  `"feature:<KnobName>=<true|false>"` entry per gameplay-relevant feature knob currently on:
  `feature:EnableRecipeEngine=true`, `feature:EnableTraitLoadoutInjection=true`. Only these two qualify: each
  changes what actually executes against replicated combat/party state (RNG draw counts, loadout-pool length) on
  divergence. `EnableClassSelectInjection`/`EnableIconFallback` are deliberately excluded — both are local,
  presentation-only UI/art filters over already-parity-hashed `Configs` data, so including them would trip the
  `Block` policy for a purely cosmetic preference. The master `Enabled` switch isn't listed either: when it's
  false the merge (and this registration call) never runs at all, so there's no payload for it to appear in.

This feeds the `FTK2MODS_PARITY_V1` handshake on session join/host; see §9.5/§9.6 for what happens on mismatch.

**Why the merge pipeline is deterministic (R2), and therefore why `dataHash` is stable across peers:** pack
*discovery* sorts discovered pack ids alphabetically before any further step (never trusts filesystem/OS
directory-listing order), and the topological sort by `loadOrder` + `dependencies` breaks ties alphabetically by
pack id (§4.1). Given the same set of enabled packs at the same versions, every peer computes the same merge
order and the same `dataHash` — no network coordination is needed to *agree* on the hash, only to *compare* it.

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

`loadOrder` ties are broken **alphabetically by pack id**, never by insertion/filesystem-discovery order. This
is not a style preference — it is what keeps the merge (and the ParityService `dataHash`, §3) a deterministic
function of *which packs are enabled*, satisfying MP rule R2, so that every peer with the same pack set computes
the same merge result without any network coordination.

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
    "Rarity": "COMMON",         // confirmed legal eItemRarities member (enum-ground-truth.md §2; was a placeholder, §11 #5)
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
  "TRAIT_CF_BALDURS_HEXBLADE_CURSE": {
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

**Id shape is load-bearing, not cosmetic (§3 point 2, resolved).** The pack loader hard-rejects any
`traits.json` entry whose id does not match `^TRAIT_[A-Z0-9_]+$` (logged, entry skipped) — this is what
makes the id itself the trait mechanism, since the game's own `InventoryHelper.GetTraits`/
`EquipmentHelper.GetEquippedThingsNonAlloc` match on the literal `"TRAIT_"` prefix. The shipped example pack
uses `TRAIT_CF_BALDURS_<NAME>` (per-pack sub-namespace inside the required `TRAIT_` prefix) rather than a
bare `CF_TRAIT_<NAME>` for exactly this reason. Once merged into `Configs.Things`, a pack trait is grantable via
vanilla loadout-pool injection immediately (§4.6, §9.1) — there is no separate "bridge" milestone gating it.

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
- `ADD_CHARACTER` → **resolved (was OQ#4): `{eSummonTypes Type, string Value}`, no count field.**
  `eSummonTypes = {NONE=-1, SPECIFIC, RANDOM, PLAYTHING, AS_FOLLOWER}`. `Type` selects *how* `Value` is
  resolved, not "id vs count": `SPECIFIC` → `Value` is the verbatim `Characters.json` config id (what this
  spec's example data uses); `RANDOM`/`AS_FOLLOWER` → `Value` is a tag fed to a weighted pool; `PLAYTHING` →
  a doll-type pool key. `CombatHelper.TryCreateSummon` returns a **singular** `out Entity` and has no count
  parameter anywhere — an ability wanting N summons authors N separate `ADD_CHARACTER` entries in its
  `Actions[]`. (The recipe-level `SUMMON` effect's `Count` field, §4.6, is authoring sugar the *emitter*
  expands into N calls — it is never passed to the game.)

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
    "Rarity": "COMMON",               // confirmed legal eItemRarities member (was a placeholder, §11 #5)
    "Material": "METAL",              // confirmed eItemMaterialFamilies = {NONE, WOOD, METAL, GLASS, CLOTH, LEATHER} — "IRON"/"BONE" are ILLEGAL values (was a placeholder, §11 #5). NOTE: the shipped CF_PACK_BALDURS/items.json still has a few "IRON"/"BONE" entries predating this resolution — a known data-fix-up item, not a spec ambiguity; see the coverage/operator-handoff docs.
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

`Equippable.Slots[]` token values (`"MAIN_HAND"` above) are **confirmed legal** members of the true enum
`eEquipmentSlots` (`docs/research/enum-ground-truth.md` §3) — resolved, no longer a placeholder (was §11 #5).

### 4.6 `skillrecipes.json` — the skill-recipe vocabulary **v1.1** (ClassForge's own file, not a native game file)

**Merged 2026-07-25 from `SPEC-DELTA-v1.1.md`**, which is now a historical document (its header says so) —
this is the vocabulary as implemented (`ClassForge.Recipes`, 149 tests). This is the extensibility contract
for the skill-recipe framework (task scope #3): one generic engine handler serves every entry; a `SKILL_*` id
here does not need a matching `SkillConfigs.json` entry unless the designer also wants native
`PROC_CHANCE`/`EVENT_PROC` bookkeeping in vanilla UI tooltips. Every primitive below names the exact game
method it hooks, prefix/postfix, and the parameter carrying its data — verified against
`docs/research/game-patch-surface-notes.md` (PSN) and `docs/research/enum-ground-truth.md` (EGT). Coverage
this vocabulary produces across all 74 EOR mechanics slated for re-hosting: **47 PORT / 20 PORT-MODIFIED / 7
PARK** — full disposition in `docs/research/eor-rehost-coverage-matrix.md`.

```jsonc
{
  "SKILL_CF_HEXBLADE_BARGAIN": {
    "SchemaVersion": "1.1",
    "DisplayName": "Eldritch Bargain",
    "Enabled": true,
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
    "Budget": { "Scope": "NONE" },
    "Cooldown": 0,
    "Priority": 0,
    "VerboseLogTag": "Hexblade Bargain"
  }
}
```

#### Triggers (15)

`ON_ABILITY_USED`, `ON_CRIT`, `ON_KILL`, `ON_HEAL`, `ON_TURN_START`/`ON_TURN_END` are the 7 v1 triggers,
re-anchored to exact hooks (one v1 trigger, `ON_DAMAGED`, is a deprecated alias — see below); 8 more were
added in v1.1. Universal MP posture: `[SYNCED]` — every trigger is a read of parameters on a native combat
method, nothing is transmitted, and no trigger itself consumes RNG.

| Token | Hook (method · kind) | Owner | Datum source |
|---|---|---|---|
| `ON_ABILITY_USED` | `CombatHelper.PerformAbility` **Postfix** (PSN §1 L1155) | `pOrigin` | ability id, `pRollData`, `pCombatDecision`, `pTarget`, `pParty`, `pGameRandom` |
| `ON_CRIT` | `InteractableHelper.ApplyStatChange` **Postfix** (PSN §2 L626) | `pOriginEntity` | `pIsCrit` — an explicit `bool` parameter (resolves the old "unverified crit signal" open question; `CalculateFinalDamage` was ruled out — it carries no crit info) |
| `ON_KILL` | same hook, **Prefix (capture) + Postfix** | `pOriginEntity` | prefix captures `pTargetEntity` HP into `__state`; postfix fires when `HpBefore > 0 && HpAfter <= 0` — origin-attributed (fixes GLADIATOR/MARSHAL-style "fires on any death" bugs) |
| `ON_HEAL` | same hook, **Postfix** | `pOriginEntity` | `pStatAction.Stat == "HP"` and delta is a heal; healed entity binds to `TRIGGER_TARGET` |
| `ON_DAMAGED` | — | — | **deprecated alias** of `ON_DAMAGE_TAKEN` below; loader accepts it, logs a one-time rename warning |
| `ON_TURN_START` / `ON_TURN_END` | `CombatPhase._performSkillAbilityProcs` **Postfix** (called from `CombatHelper._onCombatSkillProc`'s `START_TURN`/`END_TURN` `EVENT_PROC`s) | `pCharacter` | native `EVENT_PROC` vocabulary; `_performSkillAbilityProcs` is `private`, so a game-update rename fails safe (§6) |
| `ON_COMBAT_START` | `CombatHelper.SetInitiative` **Postfix** (PSN §1 L43) | `pEntity` | combat identity + effect sink |
| `ON_ABILITY_DECLARED` | `CombatHelper.PerformAbility` **Prefix** (PSN §1 L1155) | `pOrigin` | roll tier already resolved, `pCombatDecision.FocusUsed`, `pTarget`, `pThing`, the `pGetStat`/`pGetTileStat` delegate params (the `ROLL_STAT_BONUS` insertion point) |
| `ON_DAMAGE_DEALT` | `InteractableHelper.ApplyStatChange` **Prefix (capture) + Postfix** | `pOriginEntity` | HP before/after on the target; target ∉ `pParty` |
| `ON_DAMAGE_TAKEN` | same hook | **`pTargetEntity`** | attacker = `pOriginEntity` → `TRIGGER_SOURCE` |
| `ON_STATUS_APPLIED` | `InteractableHelper.ApplyStatus` (single-target overload) **Postfix** (PSN §2 L1219) | `pTargetEntity` | `pStatusConfigName` → `TRIGGER_STATUS`, `pOriginEntity`, `pGameRandom` |
| `ON_CONSUMABLE_USED` | `InteractableHelper.PerformConsumableAbility` **Postfix** (PSN §2 L538) | `pOriginEntity` | `pThing` → `ITEM_CLASS`/`ITEM_CONSUMABLE` |
| `ON_ENEMY_ABILITY_RESOLVED` | `CombatHelper.PerformAbility` **Postfix** | each living recipe-holder opposed to `pOrigin`, iterated in ascending ordinal `Entity.Guid` order | acting enemy = `pOrigin` → `TRIGGER_SOURCE`; `pRollData.Status` → `ROLL_TIER` |
| `ON_HEAL_PENDING` | `CharacterHelper.AddHealth(Entity, ref int pValue, ...)` **Prefix** (PSN §3 L1342/L1357) | `pEntity` | `ref int pValue` — the only `HEAL_MODIFIER` insertion point |
| `ON_COMBAT_LOOT` (v1.2, adopted 2026-08-06) | `LootDropHelper.GetLootDropsFromEnemies` **Postfix** (PSN §11) | each entity in `pParty` (alive players), ascending `Entity.Guid` ordinal | defeated enemies (`pEnemies`); the pending loot list (not exposed to conditions). Fires once per won enemy-loot combat (Branch B only — scripted venue loot bypasses the hook, loot-grant-verb-spec.md §9 OQ-4). Restricted vocabulary: only the §4.6 grant effects below may appear in `Effects[]`; `Budget`/`Cooldown` are rejected (intrinsically once-per-combat); MP posture `[SYNCED]` via Mode M mirror+audit, `CF_SYNC_LOOT_GRANT_V1` (§9) — full semantics in `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md` §6 |

#### Conditions (23 + 9)

ANDed; empty array = always true. Two universal extensions apply to every condition: `"Negate": true`
(inverts the result; subsumes v1's `TARGET_BASE_TYPE_NOT`, now a deprecated alias) and `"Of":
"SELF"|"TRIGGER_TARGET"|"TRIGGER_SOURCE"` (which entity the condition reads; default `SELF`). All
conditions are pure reads of replicated state or hook parameters — **no condition consumes RNG**.

v1 retained: `HP_THRESHOLD` (+`Of`, `{Percent}` or `{Flat}`), `HAS_STATUS`/`LACKS_STATUS` (+`Of`), `ROW`
(+`Of`), `WEAPON_CLASS`, `ABILITY_TAG`, `TARGET_BASE_TYPE` (+`Negate`).

v1.1 added: `ROLL_TIER {Comparator, Value: PERFECT|SUCCESS|FAIL|CRIT_FAIL}` (reads `pRollData.Status`,
`eRollStatus`), `HOSTILE_ACTION`, `ABILITY_STAT`, `ABILITY_RANGED`, `ABILITY_REPEATED`, `CHARACTER_TYPE`
(+`Of`, reads `CharacterComponent.CharacterType` — replaces ad-hoc "IsBoss" string checks), `FOCUS_SPENT`,
`FOCUS_CURRENT` (+`Of`), `STATUS_COUNT` (+`Of`, `Category: HARMFUL|BENEFICIAL|ANY`), `STATUS_TYPE`
(`ON_STATUS_APPLIED` only), `COUNTER`, `MOVED_THIS_ROUND` (+`Of`), `ALL_ALLIES_ACTED`, `ITEM_CLASS`,
`ITEM_CONSUMABLE`.

v1.2 added (9, encounter-modifiers spec — adopted 2026-08-06, full semantics
`docs/superpowers/plans/2026-08-05-encounter-modifiers-spec.md` §5/§7): `PARTY_AVG_LEVEL {Comparator, Value}`
(`ProgressionHelper.GetAveragePartyLevel` over `PlayerComponent` entities), `IS_DUNGEON {Value: bool}`
(`CombatState.IsDungeon`), `BOSS_FIGHT {Value: bool}` (`CombatState.BossFightState != null`),
`ENCOUNTER_PROPERTY {Value: <eEncounterProperties>, Negate}` (encounter entity via
`GameRun.AdventureState.EncounterGUID` → `EncounterComponent.HasProperty`/`HasAnyProperty`; no encounter
entity resolved ⇒ false), `ENTITY_TAG {Of, Value: <eConfigTags>, Negate}` (`CharacterHelper.ActorHasTag`),
`CONFIG_NAME_CONTAINS {Of, Value, Negate}` (ordinal-ignore-case `CharacterComponent.ConfigName` substring),
`COMBAT_START_REAL {Value: bool}` (`SetInitiative`'s `pTrySkillProc` parameter — `ON_COMBAT_START` only),
`SELECTION_PRESENT {Name, Value: bool}` (`CombatRuntime.Selections[Name]` non-empty — engine state, no
hook), and `IS_ENEMY {Of}` (wraps `CharacterHelper.IsEnemy`, i.e. `GroupIndex == 1` — the native predicate
used for every enemy-side gate; `CHARACTER_TYPE` cannot express "enemy" since `eCharacterTypes` and
`GroupIndex` are disjoint vocabularies). All pure reads, no condition consumes RNG (same universal rule as
v1/v1.1 above).

#### Effects (8 + new)

Every effect is emitted by constructing the equivalent `(eCombatActions, object)` pair and routing it
through `CombatHelper.ApplyAction` (PSN §1 L1871) — nothing bypasses the native action pipeline, so there is
no bespoke `_SYNC_` action for any adopted effect. Every effect entry may also carry its own optional
`"Conditions": [...]`, evaluated the same way as recipe-level conditions — this single extension expresses
roll-tiered/conditional branching (PRIEST, ASSASSIN's boss split, BARD's escalating tier) with zero new hooks.

- `ADD_STATUS` / `REMOVE_STATUS` — `{Target, Status}` → native `ADD_STATUS`/`REMOVE_STATUS`. v1.1 adds
  `FallbackStatus` (apply a substitute if the primary result never appears in `pResults` — result-driven, no
  RNG, no immunity-table introspection), `StatusOneOf: [ids]` (exactly one draw via
  `CombatState.Random.GetRandomElementFromList<T>`, only when the effect actually executes), `Duration: int?`,
  and the `"TRIGGER_STATUS"` target token under `ON_STATUS_APPLIED`. **v1.2 adds `StatusFromSelection:
  <Name>`** (mutually exclusive with `Status`/`StatusOneOf`) — resolves the status id from
  `CombatRuntime.Selections[Name]` (written by `SELECTION_SET` below); no selection present ⇒ no-op, zero
  RNG. Adopted 2026-08-06, encounter-modifiers-spec.md §5.
- `STAT_CHANGE` — `{Target, Stat, StatChangeType, FlatValue|FlatPercent, Blockable}` → native `CHANGE_STAT`.
  v1.1 adds dynamic value sources `FlatValueFrom`/`PercentFrom` ∈ `"FOCUS_SPENT"`, `"COUNTER:<name>"`,
  `"STATUS_COUNT:HARMFUL"`, `"TARGET_HP_PCT"` (with optional `PerUnit`/`Min`/`Max`), and `IsSilent`.
  `FOCUS_CHANGE` is deliberately not a separate effect — it's `STAT_CHANGE{Stat:"FOC"}`. **v1.2 adds
  `FlatValueFrom: "TARGET_MXHP_PCT"`** (adopted 2026-08-06) — engine computes the flat value from the
  target's max HP at emission time (`max(1, round(|maxhp × pct| / 100))`, EOR's rounding), because native
  `STAT_CHANGE{Stat:"MXHP", FlatPercent}` throws (`GetStatChangePercentValue` only handles `HP`/`XP`,
  `InteractableHelper.cs:1741-53` — encounter-modifiers-spec.md Gate C/§13.1); also adds
  `PercentFromSelection: <Name>` sugar (the selected modifier's `MaxHpPercent`, 0 ⇒ omitted).
- `SUMMON` — `{Target, SummonType, CharacterConfig, Count}` → native `ADD_CHARACTER`. `SummonType` is
  `eSummonTypes` (default `SPECIFIC`, see §4.4). `Count` is **authoring sugar only** — expanded by the
  emitter into N sequential `ApplyAction` calls (never passed to the game), capped at 4, default 1.
- `ROLL_STAT_BONUS {Stat, Percent?, Flat?, PercentFrom?, PerUnit?, Max?}` (new) — `CombatHelper.PerformAbility`
  Prefix; replaces the `pGetStat`/`pGetTileStat` delegate parameters with a wrapper for this call only.
  `ON_ABILITY_DECLARED` only. No RNG; safe under either MP combat-authority design (§9.3b).
- `HEAL_MODIFIER {Percent?, Flat?, MinDelta?, Scope: RECEIVED|GIVEN}` (new) — `CharacterHelper.AddHealth`
  Prefix, mutates `ref int pValue`. `ON_HEAL_PENDING` only. **No RNG permitted** — the validator rejects a
  chance-gated `HEAL_MODIFIER`.
- `COUNTER_ADD {Name, Delta, Max?}` / `COUNTER_SET {Name, Value}` (new) — pure per-battle state writes (§6
  below); `[LOCAL]` state, `[SYNCED]` cause, no RNG.
- **`GOLD_GRANT {MinGold, MaxGold}` / `ITEM_TAG_GRANT {Tag, Rarity?, Stack?}` / `LOOT_SCALE {ConfigName,
  Percent}`** (v1.2, adopted 2026-08-06, `ON_COMBAT_LOOT` only) — emit `ADD_GOLD`/`ADD_ITEM`/`SCALE_STACK`
  delta ops into the pending loot list, computed against a private per-combat grant stream (never the shared
  `CombatState.Random`) and pushed as `CF_SYNC_LOOT_GRANT_V1` (§9). `MinGold==MaxGold` on `GOLD_GRANT` is a
  zero-draw flat grant; `LOOT_SCALE` is always zero-draw. `ITEM_TAG_GRANT`'s candidate pool is
  native-stricter than EOR's tag+rarity-only pool (a recorded deliberate deviation — Gate B, coverage
  matrix §2). A `REPLACE_ITEM`-emitting `AFFIX_ROLL {ChancePct, Table}` effect exists in the wire schema but
  is **validator-rejected in v1** (reserved for M-LG4). Recipe-level field **`PickOneEffect: true`** (this
  trigger only) selects exactly one entry of `Effects[]` uniformly after the proc roll passes (one
  grant-stream draw), mirroring `StatusOneOf`. Full schema and semantics:
  `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md` §6.
- **`SELECTION_SET {Name, OneOfWeighted: [{Value, Weight}]}`** (v1.2, adopted 2026-08-06) — the weighted-pick
  counterpart to `StatusOneOf`: exactly one draw (`NextInt(1, Σweights, pMaxInclusive: true)`, EOR's walk
  algorithm verbatim), stores the winning `Value` in `CombatRuntime.Selections[Name]` (new
  `Dictionary<string,string>` on `CombatRuntime`, reset with the runtime like `Counters`). `[LOCAL]` state,
  `[SYNCED]` cause. Consumed by `StatusFromSelection`/`PercentFromSelection` above and `SELECTION_PRESENT`
  (§ Conditions). Only shipped consumer today: the generated encounter-modifier select recipe
  (encounter-modifiers-spec.md §5/§6).
- **`EVENT_BANNER {LocKey, FallbackText, DurationMs, TextFromSelection?}`** (v1.2, adopted 2026-08-06) —
  `[LOCAL]` presentation (R4) via `GameplayDialogViewHelper.ShowEventTitle` (min enforced duration 3000ms).
  Renders on every peer running the engine; never gates or feeds gameplay state, excluded from SafeMode
  considerations like all presentation.

`Target` ∈ `SELF|CASTER|TRIGGER_TARGET|TRIGGER_TARGET_POSITION|ALLY_ALL` (v1) **+** `TRIGGER_SOURCE` (the
entity that caused the trigger), `ALLY_ALL_OTHERS`, `ENEMY_ALL`, `ALLY_BY_RANK{Rank:{Stat, Order, Where,
ExcludeSelf}}` (deterministic — sorted by stat then ordinal `Entity.Guid` tiebreak, zero RNG, mirrors EOR's
own `.OrderBy(SPD).ThenBy(Guid)`).

**"Gain gold" is ADOPTED (v1.2, 2026-08-06)** — was PARKED per the original v1 finding (no verified
combat-time gold verb existed; gold grants are overworld-only reward verbs). `LootDropHelper.
GetLootDropsFromEnemies`'s signature is now verified (PSN §11) and the host-decided, version-synced
`CF_SYNC_LOOT_GRANT_V1` action ships: `ON_COMBAT_LOOT` + `GOLD_GRANT`/`ITEM_TAG_GRANT`/`LOOT_SCALE` (§ above)
compute a deterministic post-combat loot delta at a single verified point, mirrored identically on every
peer (Mode M), with the host's push serving as the authoritative audit digest rather than an apply-from-wire
channel — see §9 and `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md` §2. Ships **dark** behind
`[Skills] EnableLootGrants = false` pending operator smoke item V-1 (coverage matrix §1/§2/§3, §11 below).

#### Recipe-level fields

`ProcChance`/`AiProcChance` (0–100, mirror native `PROC_CHANCE`/`AI_PROC_CHANCE`; `100` = no roll taken at
all, `0` = never fires). `Budget {Scope: NONE|ONCE_PER_ROUND|ONCE_PER_COMBAT|ONCE_PER_TARGET_PER_ROUND|
ONCE_PER_TARGET_PER_COMBAT, ConsumeOn: PROC|EVALUATION|EFFECT_APPLIED, Key}` (new) covers what v1's bare
`Cooldown` couldn't: `ConsumeOn: PROC` (default) reproduces EOR's real "a failed roll leaves the budget open"
behavior; `Key` lets a declare/resolve recipe pair (CORSAIR-style) share one budget. `Cooldown` stays in
rounds, `0` = none. `Priority` (new) breaks evaluation-order ties (ascending, then ordinal recipe id).
`SchemaVersion`/`Enabled` (new) — a per-recipe kill switch and version gate.

**`Scope: OWNED|COMBAT`** (v1.2, adopted 2026-08-06; `OWNED` is the default — exactly today's semantics,
field omitted everywhere in pre-v1.2 packs). A `COMBAT` recipe is registered for every combat while its
pack is enabled — `Holds()` is bypassed, there is no owner iteration, `Owner = null`. Evaluated once per
trigger event, after all owned recipes for that event, ordered ascending `Priority`/ordinal id (an
extension of the fixed-iteration-order invariant below). The validator rejects, in a `COMBAT` recipe: any
condition whose `Of` resolves to `SELF`; any effect targeting `SELF`/`CASTER`/`ALLY_*`/`ENEMY_ALL`;
`AiProcChance` — all meaningless without an owner (load-time rejection, not runtime skipping). Budget/
cooldown/counter state for a `COMBAT` recipe keys on the sentinel owner guid `""` (empty string, ordinal-
sorts before every real guid) in the same single-slot `(CombatKey, CombatRuntime)` cache as owned-recipe
state — no new state container. **`ProcChanceFormula: {Base: [{Conditions, Value}], Adjustments:
[{Conditions, Value}], Min, Max}`** (v1.2, mutually exclusive with `ProcChance`; `COMBAT` and `OWNED`
recipes alike) — first `Base` row whose `Conditions` pass wins (last row = default), every passing
`Adjustments` row adds its `Value`, clamped to `[Min, Max]`: a pure function of replicated state, so exactly
one `NextChance` draw (or zero when it clamps to ≤0 — a symmetric, replicated-state-determined skip, same
argument as `ProcChance:100`). Both fields are the encounter-modifiers spec's contribution; full semantics
and the shipped selection/application recipe pair: `docs/superpowers/plans/2026-08-05-encounter-modifiers-spec.md`
§4/§5/§6.

#### Determinism invariants (binding)

1. **One RNG source, always**: every roll draws from `Env.GameRun.CombatState.Random` (`GameRandom`, backed by
   a single seeded `System.Random`, seeded from `NetworkDebuggingHelper.MultiplayerSeed` in online MP).
   `System.Random`, `UnityEngine.Random`, and constructing a fresh `GameRandom` are all forbidden.
2. **Null stream ⇒ no fire, ever.** If no active combat, the recipe does not fire (logs a one-time skip) — it
   never falls back to an ad-hoc seeded `GameRandom` (the explicit anti-pattern behind EOR's non-lockstep
   ARCANE_MEMORY/OF_SPELLKEEPING bugs).
3. **Evaluation order**: `Enabled` → `Trigger` → `Conditions` → `Cooldown` → `Budget` → **roll** → `Effects`.
   The roll is always last, so no draw is taken on a path a peer could skip for a state-dependent reason.
4. **Fixed iteration order everywhere**: ascending `Priority` then ordinal recipe id; multi-owner/multi-target
   iteration by ascending ordinal `Entity.Guid`; never `Dictionary`/`HashSet` enumeration order.
5. **All effects ride native verbs** — nothing writes entity state directly.
6. **No per-client suppression** — no primitive returns `false` from a prefix or otherwise decides locally
   that an authoritative change did not happen.

#### Per-battle state model

*No gameplay-affecting static state survives a combat* (the direct fix for EOR's `SteadyAimUsedThisCombat`
leak). Combat identity = `(CombatState` reference identity`, CombatState.Random.Seed)`; the engine holds
exactly one `(CombatKey, CombatRuntime)` slot and drops/reallocates on any change, so stale state can never be
read even if an end-of-combat hook is missed. `CombatRuntime` holds `Round` (mirrored off
`CombatHelper.NextTurn`'s `pIsNewRound` — not the raw `CombatState.TotalRounds` field, which resets to `-1`
mid-fight on multi-wave encounters and is therefore not monotonic; either source rewinds identically on every
peer, so this is a correctness nicety, not an MP-safety difference), `Budgets`, `Cooldowns`, `Counters`, and
per-owner `TurnState` (`ActedRound`/`MovedRound`/`LastAbilityId`). No per-run state; no new `GameRunData` keys.

#### Parked primitives (7)

**`GOLD_GRANT`/`ITEM_TAG_GRANT` ADOPTED (v1.2, 2026-08-06)** — see the "Gain gold" paragraph above; removed
from this list. `SUPPRESS_CONSUME` (needs
out-of-combat shared RNG + a replicated inventory-grant verb — its EOR form and its host-decided redesign
both fail, since scroll use happens outside combat where `CombatState.Random` doesn't exist),
`DAMAGE_TAKEN_MULT` (`CalculateFinalDamage` has no RNG parameter — a chance gate there would be an asymmetric
draw), `STEAL_STATUS`/`CLEANSE_RANDOM_STATUS` (need a cross-entity `StatusSelector` sub-schema), `ON_DODGE`
(needs a confirmed dodge value in `eAbilityResults` — none found), `CONDITIONAL_STAT_MODIFIER` (a
hot-path `GetStat` postfix with 8 overloads; not worth it for two flat `+2`s — ported as unconditional flat
stats instead). `CROSS_ENTITY_COORDINATION` (BEASTMASTER's literal cross-entity read) is parked as a
*primitive* but its mechanic still ports, re-expressed with a real `STATUS_MARKED_00` status instead. One
resolved via redesign rather than parked: `STATUS_APPLY_RESIST`'s EOR form (prefix `ApplyStatus`, `return
false`) is per-client suppression and refused; the behavior ships instead as `ON_STATUS_APPLIED` (Postfix) +
`STATUS_TYPE` + `ProcChance` + `REMOVE_STATUS{"TRIGGER_STATUS"}` — the status is briefly applied and then
removed, a documented semantic delta from "never applied".

#### New pack file types (v1.2, adopted 2026-08-06): `statuses.json` / `modifiers.json`

Two new pack files, shipped by `CF_PACK_ENCOUNTER_MODIFIERS` (§3 runtime flow above lists their merge
steps): `statuses.json` is `StatusEffectConfig`-shaped verbatim (`Type, Duration, TickFrequency,
TickOverworld, TickCombat, TickExpire, TileSync, GroupSync, Passives[], AddProperties[], Stats, CustomStats`),
merges adds-only into `Configs.StatusEffects` under the same M0 live-id enforcement as `Configs.Characters/
Things/Abilities` (§9.1); `modifiers.json` is ClassForge's own weighted modifier table (`{Id, Weight,
Status, MaxHpPercent?, Rewards?}[]`, authored array order = pick-walk order), parsed into ClassForge's own
registry like `skillrecipes.json` (not a `Configs.*` field) and used to **generate** the `Scope:COMBAT`
selection/application recipe pair (§ recipe-level fields above) at pack-load time — authors write the
table, the engine emits the recipes, so the weighted list/status ids/`MaxHpPercent` values can never drift
across hand-authored copies. Both files are new inputs to the existing `dataHash` automatically (§3 "Multiplayer
parity registration" — it hashes every non-localization file in the pack tree), so R1 parity covers them
with zero new mechanism. Full schema: `docs/superpowers/plans/2026-08-05-encounter-modifiers-spec.md` §3.

### 4.7 `localization/en.json`

EOR-style flat dictionary, `ID` and `ID_DESCRIPTION` keys, covering every class/trait/ability/item id the pack
introduces plus its own `LocKey`s:

```jsonc
{
  "CF_WARLOCK_HEXBLADE": "Hexblade Warlock",
  "CF_WARLOCK_HEXBLADE_DESCRIPTION": "A duelist bound to a pact that trades vitality for arcane power.",
  "TRAIT_CF_HEXBLADE_CURSE": "Cursed Strikes",
  "TRAIT_CF_HEXBLADE_CURSE_DESCRIPTION": "Critical hits curse the target."
}
```

### 4.8 `icons/` and `portraits/`

Loose PNGs, filename (without extension) = the content id they represent: `icons/TRAIT_CF_HEXBLADE_CURSE.png`,
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
- `[Multiplayer] OnParityMismatch` (enum: `Block` / `WarnAndSafeMode` / `WarnOnly`, default **`Block`**) —
  **overrides** `docs/MULTIPLAYER.md` R1's repo-wide default (`WarnAndSafeMode`) specifically for this mod.
  Rationale (full reasoning in §9.5): ClassForge cannot hot-disable a pack class mid-run the way a
  SafeMode-compatible mod sheds a feature — a mismatched pack-class character has no valid degraded state, it
  either resolves against `Configs` or it doesn't. Defaulting to `Block` refuses a mismatched peer's session
  join *before* any pack-class character can be created, instead of letting the party discover the failure
  mid-combat. Only set this to `WarnAndSafeMode`/`WarnOnly` if you understand and accept that a mismatched peer
  may desync or crash the moment a pack-class character takes its first action.

## 6. Patch targets & integration points

All verbatim from `docs/research/game-code-reference.md` / `docs/research/game-patch-surface-notes.md`
(PSN). The four `CharacterHelper.GiveTrait/RemoveTrait/RemoveAllTraits/GetFirstTrait` rows a pre-v1.1 draft
of this table carried for a "trait bridge" are **deleted** — there is no bridge, §3 point 2 — as is the
`InteractableHelper.CalculateFinalDamage` row once tried for `ON_CRIT` (ruled out; carries no crit info).

| Target | Kind | Why |
|---|---|---|
| `ConfigsHelper.LoadConfigs` | Postfix | After vanilla config load, run the pack merge pass into `Configs.Characters/Things/Abilities`. |
| `ConfigsHelper.ReloadConfigs` | Postfix | Re-run the merge on hot-reload; resolved as idempotent by construction (was §11.8) — both entry points rebuild `Configs` from scratch, so a re-running merge can never see leftover pack data. |
| `CharacterCustomizationViewHelper.RenderClassList` | Postfix | Inject pack `PLAYER`-tagged classes into the selectable roster. EOR already patches this exact method — proven hook. |
| `CharacterCustomizationViewHelper.RenderCustomizationContainer` / `RenderStatsContainer` | Postfix | M2 UI polish — full parity for pack classes' stat/gear preview rendering. |
| `LootDropHelper.GetAdventureLoadOut` | Postfix | Trait selection (§3 point 2, §4.6 OQ#1 resolution) — appends one `Thing` per eligible pack trait not already in the vanilla loadout pool. Cited from the decompile directly, not PSN — re-verify its signature on any game update. |
| `CombatHelper.SetInitiative` | Postfix | `ON_COMBAT_START` (PSN §1 L43). |
| `CombatHelper.PerformAbility` | Prefix (declared) + Postfix (used) | `ON_ABILITY_DECLARED`/`ROLL_STAT_BONUS` (prefix, replaces a delegate param) and `ON_ABILITY_USED`/`ON_ENEMY_ABILITY_RESOLVED` (postfix) (PSN §1 L1155). |
| `InteractableHelper.ApplyStatChange` | Prefix (capture) + Postfix | `ON_CRIT`, `ON_KILL`, `ON_HEAL`, `ON_DAMAGE_DEALT`, `ON_DAMAGE_TAKEN` — the single verified hook backing all five (PSN §2 L626). |
| `InteractableHelper.ApplyStatus` (single-target overload) | Postfix | `ON_STATUS_APPLIED` (PSN §2 L1219). |
| `InteractableHelper.PerformConsumableAbility` | Postfix | `ON_CONSUMABLE_USED` (PSN §2 L538). |
| `CharacterHelper.AddHealth` | Prefix, mutates `ref int pValue` | `ON_HEAL_PENDING`/`HEAL_MODIFIER` (PSN §3 L1342/L1357). |
| `CombatHelper.NextTurn` | Postfix | Per-battle `Round` counter, incremented when `pIsNewRound == true` (PSN §1 L793) — the recipe engine's round source of truth. |
| `CombatPhase._performSkillAbilityProcs` | Postfix (private method — `AccessTools`-mediated; a game-update rename disables `ON_TURN_START`/`ON_TURN_END` fail-safe, per Wave-3 correction and the MP review's N6/risk-3 notes) | `ON_TURN_START`/`ON_TURN_END`, called with `START_TURN`/`END_TURN` from `CombatHelper._onCombatSkillProc`'s dispatch — this method, not `_onCombatSkillProc` itself, is the actual turn-phase hook (a Wave-3 implementation correction over an earlier draft of this spec). |
| `AssetLoader.GetImage` / `GetRender` | Prefix (return true/false to skip vanilla lookup) | Serve pack icons/portraits by id before falling back to vanilla. EOR already patches these — proven hook. |
| `Lang.__t` / `Lang.SetLanguage` | Postfix, or pre-populate the backing dictionary before first call | Merge pack localization strings. EOR already patches these — proven hook. |
| `CombatHelper.ApplyAction` | Effect emission only (no observation patch) | Every recipe effect is constructed as an `(eCombatActions, object)` pair and routed through this method — see §4.6. Verified payload shapes: `ADD_STATUS`/`REMOVE_STATUS` are a bare string with a `PERFECT` roll-tier gate; `CHANGE_STAT`/`ADD_CHARACTER` are an unconditional `JsonElement` cast to `ChangeStatAction`/`AddCharacterAction` (field names `IsBlockable` not `Blockable`; `ADD_CHARACTER` also carries a `PERFECT` gate the executor supplies unconditionally). |
| `FTK2Mods.DevKit.ParityService.RegisterWithCallback` | Reflection call (not a Harmony patch; no compile-time dependency) | Called at the end of `Load()` (and again after a hot-reload merge) with `(guid, version, dataHash, enabledFeatures, Action<string[]> onParityFailed)` — see §3 "Multiplayer parity registration". Backs the `OnParityMismatch = Block` behavior in §9.5. |

## 7. Example starting dataset

`data/ClassPacks/CF_PACK_BALDURS/` — one complete pack, three fully-authored FTK×BG3 classes chosen as the most
data-only-feasible from the idea list (row control + native statuses + native `ADD_CHARACTER` summons cover all
three without needing a bespoke C# mechanic per class):

- **`CF_WARLOCK_HEXBLADE`** — Hexblade Warlock. Innate passive `SKILL_CF_HEXBLADE_BARGAIN` (HP-for-focus bargain,
  entirely skill-recipe-driven: the ability `CF_ABL_ELDRITCH_BARGAIN` carries no `Actions` of its own — it exists
  only to be the recipe's `ON_ABILITY_USED` trigger surface, demonstrating ability+recipe composition). Weapon
  `CF_ITEM_PACT_BLADE` grants `CF_ABL_ELDRITCH_BLAST` (damage), `CF_ABL_HEX` (`ADD_STATUS CURSE`),
  `CF_ABL_HELLISH_REBUKE` (damage + `ADD_STATUS DEATHMARK`), and the bargain ability. Two optional traits:
  `TRAIT_CF_HEXBLADE_CURSE` (grants recipe `SKILL_CF_CURSED_STRIKES`, `ON_CRIT` → curse the target) and
  `TRAIT_CF_PACT_BOON` (flat `FOC`/`INT` bonus, no recipe — demonstrates a pure-stat trait).
- **`CF_BATTLEMASTER`** — Battle Master. **No innate passive at all** — deliberately, to demonstrate that most
  class identity is pure JSON: its four abilities (`CF_ABL_TRIP_ATTACK` → damage + `ADD_STATUS ENTANGLE`,
  `CF_ABL_GOADING_STRIKE` → damage + `ADD_STATUS TAUNT`, `CF_ABL_MENACING_STRIKE` → damage + `ADD_STATUS SCARE`,
  `CF_ABL_COMMANDERS_STRIKE` → ally-targeted `CHANGE_STAT SA +1`) carry the whole class on their own. One
  optional trait, `TRAIT_CF_COMBAT_SUPERIORITY`, grants recipe `SKILL_CF_BM_RELENTLESS` (`ON_KILL` → regain a
  secondary action). A second trait, `TRAIT_CF_TACTICAL_MIND`, is pure-stat only.
- **`CF_NECROMANCER`** — Necromancer. Innate passive `SKILL_CF_NECRO_RAISE_ON_KILL` (`ON_KILL`, condition target
  isn't already a skeleton → `SUMMON CF_SKELETON_WARRIOR` at the kill's tile). Staff `CF_ITEM_NECROTIC_STAFF`
  grants `CF_ABL_RAISE_SKELETON` (direct `ADD_CHARACTER` summon), `CF_ABL_BONE_ARMOR` (`CHANGE_STAT DEF` +
  `ADD_STATUS GUARD`), `CF_ABL_DRAIN_LIFE` (damage, tagged `CF_DRAIN` for the Soul Siphon recipe to key off),
  `CF_ABL_DEATH_MARK` (`ADD_STATUS DEATHMARK`). Optional trait `TRAIT_CF_DEATHS_DESIGN` grants recipe
  `SKILL_CF_NECRO_SOUL_SIPHON` (`ON_ABILITY_USED`, condition ability tag `CF_DRAIN` → self-heal). A second trait,
  `TRAIT_CF_UNDEAD_LEGION`, is pure-stat only.
- **`CF_SKELETON_WARRIOR`** — supporting non-player summon entry (`BaseType:"SKELETON"`), armed with
  `CF_ITEM_BONE_CLAWS` granting `CF_ABL_BONE_STRIKE`, so the Necromancer's summons can actually fight.

Files: `pack.json`, `classes.json` (4 entries: 3 classes + 1 summon), `traits.json` (6 entries), `abilities.json`
(13 entries), `items.json` (4 entries), `skillrecipes.json` (5 recipes — covering `ON_ABILITY_USED` ×2, `ON_CRIT`
×1, `ON_KILL` ×2), `localization/en.json`, `icons/*.png` (6, one per trait), `portraits/*.png` (3, one per player
class).

**Placeholder values, now resolved against `docs/research/enum-ground-truth.md` (was §11.5):**
`Rarity: "COMMON"` and `Equippable.Slots: ["MAIN_HAND"]` are **confirmed legal** (`eItemRarities`,
`eEquipmentSlots`) — no longer placeholders. `Material: "IRON"` is **illegal**
(`eItemMaterialFamilies = {NONE, WOOD, METAL, GLASS, CLOTH, LEATHER}` has no `IRON`) and the shipped data
uses `"METAL"` instead. `ADD_CHARACTER`'s field mapping is resolved (§4.4): `{Type: "SPECIFIC", Value:
"CF_SKELETON_WARRIOR"}`, not `{Type: <config id>, Value: <count>}`.

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
   - Warlock (with `TRAIT_CF_HEXBLADE_CURSE` equipped): land a critical hit → log shows
     `SKILL_CF_CURSED_STRIKES` fired (the `ON_CRIT` signal is confirmed and ships ungated — §4.6, §11 #2) and
     `CURSE` applied to the target.
   - Battle Master (with `TRAIT_CF_COMBAT_SUPERIORITY`): land a killing blow → log shows
     `SKILL_CF_BM_RELENTLESS` fired, `SA +1` applied.
   - Necromancer: land a killing blow on a non-skeleton enemy → log shows `SKILL_CF_NECRO_RAISE_ON_KILL`
     evaluated (respecting `ProcChance:35`, so may legitimately skip — re-test until it procs), on success a
     `CF_SKELETON_WARRIOR` appears at the kill's tile, allied.
   - Necromancer (with `TRAIT_CF_DEATHS_DESIGN`): cast `CF_ABL_DRAIN_LIFE` → log shows
     `SKILL_CF_NECRO_SOUL_SIPHON` fired, `+3 HP` (`REGEN`) applied to caster.
6. **Summon combat.** Confirm the raised/summoned `CF_SKELETON_WARRIOR` can take its turn and use
   `CF_ABL_BONE_STRIKE`, and is cleaned up at combat end (`CombatPhase._endCombatAsync` / `TryEndTurnSummons`).
7. **Edge cases to log and verify explicitly:** two packs defining the same content id (collision log line, last
   loader-order pack wins); a pack with an unresolved `dependencies` entry (pack skipped, logged, other packs
   still load); `Enabled = false` master knob (zero packs scanned, vanilla untouched); disabling one pack via its
   per-pack knob while others remain enabled.
8. **MP smoke test — matching packs (see §9.6).** Both peers install `ftk2mods.classforge` +
   `CF_PACK_BALDURS` at the same version. Host creates a `CF_WARLOCK_HEXBLADE` character, client creates a
   `CF_NECROMANCER` character; both join the same run and fight one combat together, each casting at least one
   ability and triggering one skill recipe. Verify: both peers show identical HP/status/summon state after each
   action (no visible divergence between screens); the Necromancer's raised skeleton appears and acts
   identically on both peers; with `SkillRecipeVerboseLogging` on, both peers' logs show the same recipe
   fire/skip decisions for the same triggers.
9. **MP mismatch test — blocked join (see §9.5).** Host has `CF_PACK_BALDURS` enabled; client either lacks the
   pack, has it at a different version, or has a different `[Packs] <PackId>.Enabled` set. Client attempts to
   join the host's session. Verify: the join is **refused**, not merely warned, before the client reaches
   character selection, and the displayed message names `ftk2mods.classforge` and the specific mismatch kind
   (missing pack / version / feature-set difference).

## 9. Multiplayer

ClassForge is **MP-first**, per `docs/MULTIPLAYER.md` (the repo-wide MP architecture) — this section follows
that doc's mandated §9 structure. *(Persistence note, unaffected by MP: no new save schema. A pack character's
class/trait/gear identity persists exactly how vanilla already persists any character's config id, `Things`, and
`Passives`. Skill-recipe cooldown counters are per-battle plugin state only, never saved.)*

### 9.1 Parity class: `ALL_PEERS` — no exceptions

ClassForge is `ALL_PEERS`. Every peer in a session must have the same packs enabled at the same versions with
matching data. There is no safe subset install (unlike a `HOST_ONLY` mod such as an AI-only brain): classes,
traits, abilities, and items merge directly into `Configs.Characters/Things/Abilities` (§3) *before* any
character exists, and the game simulates combat from those `Configs` entries on every peer that must resolve
that character. A peer missing the pack resolves the class's config id to nothing (§3 State lifecycle) — not a
cosmetic gap, but an unresolvable character the instant it needs to render or act. A party member playing a
class the other peer doesn't have is a **guaranteed desync or crash**, not a degraded experience. This is also
why ClassForge's SafeMode posture (§9.5) departs from the repo default. **M0 adds-only enforcement** closes
the companion risk: a candidate pack id already present in the *live* (pre-pack) `Configs` is refused outright
(a `CF_LIVE_ID_COLLISION` Error finding, entry dropped) — a pack can never silently overwrite vanilla content
(e.g. a pack shipping the id `KNIGHT`), which would otherwise make two peers simulate from different vanilla
configs with no parity signal at all, since the pack's own id would still hash identically either way.

**Known handshake-ordering limitation (fail-closed, not fail-open).** Trait selection (§3 point 2, §4.6) is
injected from a Postfix on `LootDropHelper.GetAdventureLoadOut`, whose only callers are party-setup screen
build and join-in-progress rebuild — both of which can run **before** DevKit's parity handshake resolves (the
handshake currently runs from an `AdventureDirector.Initialize` postfix). Because the wire format for a trait
pick is a raw pool **index**, not an id, two peers with differently-sized pools are not a cosmetic mismatch —
it is an out-of-range index crash on one peer, or a silently-wrong-item equip if pool lengths happen to
coincide. The implemented fix is **fail-closed**: read the online/offline flag (fail-closed = treat unknown as
online); offline, inject unconditionally exactly as before; online, injection additionally requires a
*positive* verified parity match (not merely "not yet blocked") before it runs. Net effect: **day-one MP pack
traits may legitimately be absent from the loadout pool for the first screen build of an online session** —
correct and safe, not a bug, but a real gap for M2 to close. The follow-up (not yet implemented, owned by
DevKit, tracked here because ClassForge's fix depends on it): move the handshake anchor to
`AdventureSelectionDirector`, which already branches on `PlayingOnlineMultiplayer && IsHost` and runs earlier
in the boot sequence than `PartyManagementDirector`.

### 9.2 Feature table

| Feature | Class | Authority |
|---|---|---|
| Pack-merged `Configs.Characters/Things/Abilities` entries | `[SYNCED]` | All peers — correctness comes from parity (R1), not runtime replication; there is nothing to transmit because the data is loaded identically before any character exists. M0 adds-only enforcement runs at merge time on every peer identically. |
| Class-select UI injection | `[LOCAL]` | Local peer; renders shared `Configs` data, doesn't transmit the render |
| Trait `Thing` grant/removal | `[SYNCED]` | Native `Things` replication — no ClassForge mechanism; a `TRAIT_`-prefixed `Thing` grant rides the same pipeline as any vanilla trait pick (§3 point 2). The **selection/offer** step (loadout-pool injection) is `[LOCAL]`-gated online per the fail-closed posture in §9.1 above. |
| Icon/portrait fallback | `[LOCAL]` | Local peer, presentation only (R4) |
| Localization merge | `[LOCAL]` | Local peer, parity-exempt (R4); also excluded from the `dataHash` (§9.6) |
| Skill-recipe proc evaluation (`ProcChance`/`AiProcChance` roll) | `[SYNCED]` | Whichever peer executes the native hook method; safe under either combat-authority design (§9.3b) by construction — every roll draws from `CombatState.Random` at a point reached identically on every peer, and the roll is always evaluated last (§4.6 determinism invariants) |
| Skill-recipe effect application (`CHANGE_STAT`/`ADD_STATUS`/`ADD_CHARACTER`) | `[SYNCED]` | Rides the vanilla action-pipeline replication used by any ability's `Actions[]` |
| Skill-recipe cooldown/budget/counter state | `[LOCAL]` | Per-battle, single-slot cache keyed by `(CombatState` identity`, seed)` — drops and reallocates on any change, so it cannot leak past the combat that produced it; derived from `[SYNCED]` triggers, never itself transmitted |
| `ON_COMBAT_LOOT` grant computation + application (v1.2, 2026-08-06) | `[SYNCED]` | Every peer computes+applies the identical delta (Mode M mirrored-deterministic); RNG = a private per-combat grant stream derived from replicated state, never `CombatState.Random` — loot-grant-verb-spec.md §2/§4 |
| `CF_SYNC_LOOT_GRANT_V1` push (host → all, per won combat) | `[SYNCED]` | Host only sends (`NetworkData.IsHost`); all peers receive/verify. Not an apply-from-wire channel — the push is the authoritative audit digest (`{GrantKey, OpsHash}`); dropped/late/duplicated payloads never affect gameplay (loot-grant-verb-spec.md §7) |
| `Scope: COMBAT` recipe evaluation (encounter modifiers, v1.2, 2026-08-06) | `[SYNCED]` | Ownerless, registered (not held); evaluated once per trigger event at every executing peer, same posture as owned recipes above |
| `statuses.json`/`modifiers.json` merge → `Configs.StatusEffects` / EncounterModifier registry (v1.2, 2026-08-06) | `[SYNCED]` | All peers identically at load (R1 property, nothing transmitted) |
| `EnableRecipeEngine` / `EnableTraitLoadoutInjection` / `EnableLootGrants` knobs | parity-covered | Included in `ParityService`'s `enabledFeatures` as `feature:<Name>=<value>` (§3) — a knob mismatch between otherwise-identical peers is now a real, reported divergence, not a silent "Match". `EnableLootGrants` defaults `false` (dark, §11) |

### 9.3 Determinism inventory

**a) Pack merge order (R2).** The merge pipeline (§3 Runtime flow) is a pure function of which packs are
enabled: discovery sorts pack ids alphabetically (never trusts filesystem/OS enumeration order), and the
`loadOrder` topological sort breaks ties alphabetically by pack id (§4.1). Given the same enabled pack set at the
same versions, every peer computes the same merge order — hence the same "last pack wins" collision resolution
(§8 edge cases) and the same ParityService `dataHash` (§9.6) — with zero network coordination required to reach
agreement, only to detect disagreement.

**b) Skill-recipe `ProcChance`/`AiProcChance` rolls (R2 + R3) — no longer blocking; both designs sketched, and
the shipped vocabulary is safe under either.** Every recipe fire (§4.6) includes a percentage roll whose
outcome peers must agree on. Which peer(s) evaluate it (host-only vs. per-peer) is still a repo-wide open
question (`docs/MULTIPLAYER.md` open question #1) — but the v1.1 vocabulary's determinism invariants (§4.6)
make the choice non-load-bearing:

- **Design A — host-simulated, vanilla-replicated.** Consistent with R3 and `docs/MULTIPLAYER.md`'s strong
  prior that combat resolution is host-authoritative. The dispatcher's hook points (§6: `PerformAbility`,
  `ApplyStatChange`, `ApplyStatus`, `PerformConsumableAbility`, `AddHealth`, `SetInitiative`, `NextTurn`,
  `_performSkillAbilityProcs`) only *decide* outcomes host-side, drawing from the game's deterministic
  `GameRandom`. The resulting effect is applied through the same native `eCombatActions`/status verbs a
  vanilla ability would use, so it replicates to clients for free via the vanilla action pipeline — no
  bespoke `_SYNC_` action needed.
- **Design B — evaluated per-peer, must converge independently.** If each peer's client instead re-executes
  the same hooks locally (driven by synced inputs rather than synced results), the dispatcher's Harmony hooks
  fire independently on every peer, and each peer's roll must independently land on the *same* outcome. Only
  possible if every roll consumes the shared deterministic `GameRandom` advanced identically (same call count,
  same order, for the same triggers) on every peer — a local `System.Random` per peer desyncs the instant two
  peers' rolls diverge. This design additionally needs every peer to evaluate the same set of triggers, which
  already presumes R1 parity holds (§9.1).

**Verified as implemented:** every `ProcChance`/`AiProcChance`/`StatusOneOf` roll draws from
`Env.GameRun.CombatState.Random` — a `GameRandomSource` wrapper is the *only* RNG entry point in
`ClassForge.Recipes`, and a grep of the engine assembly finds zero `System.Random`/`UnityEngine.Random`/
constructed-`GameRandom` uses anywhere (per the Wave-4 MP review's N5 finding). The roll is always the last
step in the evaluation order (§4.6 invariant 3), so no draw is ever taken on a path a peer could skip for a
state-dependent reason — the only choice safe under both designs, and the one shipped.
Tracked as an open question in §11 alongside `docs/MULTIPLAYER.md`'s combat-authority unknowns.

### 9.4 Sync surface

- **Config-shaped content — no bespoke sync.** Per `docs/MULTIPLAYER.md`'s "prefer config-shaped content"
  guidance, ClassForge's entire persistent surface (classes/traits/abilities/items) merges into `Configs` before
  any character exists; there is nothing to transmit at runtime. Correctness is a parity property (R1, §9.6),
  not a replication property.
- **Skill-recipe effects ride the vanilla action pipeline.** `CHANGE_STAT`/`ADD_STATUS`/`REMOVE_STATUS`/
  `ADD_CHARACTER` (via `SUMMON`, §4.6) are native `eCombatActions`/status verbs; whatever mechanism already
  replicates a vanilla ability's actions replicates a recipe's effects identically under Design A (§9.3b).
- **No custom `_SYNC_` action defined for M1–M3.** Everything ClassForge needs rides either the vanilla
  `Configs` load (content) or the vanilla action pipeline (effects). If Design B (§9.3b) turns out to be reality
  and per-peer convergence via shared `GameRandom` alone proves insufficient, the designed-for fallback is
  `CF_SYNC_RECIPE_PROC_V1` (host → clients, `{recipeId, casterId, targetId, rngDraw}`, idempotent to apply) — a
  last resort per `docs/MULTIPLAYER.md`'s guidance, named here so it isn't a scope surprise later.
- **`CF_SYNC_LOOT_GRANT_V1` (v1.2, adopted 2026-08-06) — the first shipped custom `_SYNC_` action, and a
  third posture beyond "no sync needed" and the `CF_SYNC_RECIPE_PROC_V1` fallback above.** Host → all peers,
  once per won enemy-loot combat, idempotent by `GrantKey`; rides `FTK2.DevKit`'s `TransportService`
  (`FTK2.DevKit/SPEC.md` §3). Not an apply-from-wire channel: every peer computes and applies the identical
  loot delta locally (Mode M, `ON_COMBAT_LOOT` above), and the pushed `{GrantKey, OpsHash}` is the
  authoritative audit digest — a silent-drift-to-loud-failure conversion (per `docs/MULTIPLAYER.md`'s new
  "mirror + audit" posture), not a replication mechanism. Ships dark behind `[Skills] EnableLootGrants =
  false` pending operator smoke item V-1. Reserved Mode H (host-authoritative verbatim-apply push, for
  host-private inputs like a future Nemesis system) is M-LG4, gated on verification item V-2. Full design:
  `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md` §2/§3/§7.

### 9.5 SafeMode definition: `OnParityMismatch = Block` (ClassForge's override of the repo default)

`docs/MULTIPLAYER.md` R1's repo-wide default is `WarnAndSafeMode`, where SafeMode means "disable state-mutating
features, keep presentation-only features." **ClassForge overrides this to `Block`** (§5), because SafeMode's
premise doesn't hold for a class-pack mod:

- SafeMode assumes a mod can hot-disable its mutating features mid-session while the party keeps playing.
  ClassForge cannot: a pack class is not a feature layered onto a character that can be toggled off, it *is* the
  character. Once a run starts with a `CF_WARLOCK_HEXBLADE` party member, there is no "disable ClassForge's
  mutations" state that leaves that character valid on a peer missing the pack — its `Configs.Characters` entry,
  `Things`, and `Passives` simply don't resolve there (§3 State lifecycle), mid-run, after the party has already
  committed to that roster.
- `WarnOnly` is worse than useless here: the failure isn't a visual glitch, it's an unresolvable character
  reference the first time the mismatched peer's client needs to render or simulate that character — exactly the
  "guaranteed desync/crash" of §9.1, just delayed until it's expensive to unwind.
- **Therefore: on session join, if ParityService reports a `ftk2mods.classforge` mismatch (version, `dataHash`,
  or `enabledFeatures` difference — §9.6), ClassForge's on-mismatch behavior engages**, with a message naming
  the mod, the mismatch kind, and — if `enabledFeatures` differ — which pack ids/knobs are missing/extra on
  which side (§8 step 9). This is a stronger-than-default posture applied because ClassForge's failure mode is
  uniquely unrecoverable mid-run; it is not proposed as a new repo-wide default.

  **Honest semantics of what `Block` actually does (implemented reality, corrected from an earlier draft of
  this spec):** `Block` does **not** unmerge or prevent the pack merge — the merge runs from
  `ConfigsHelper.LoadConfigs` at boot, before any handshake could possibly have resolved, so "prevent the
  merge" was never reachable in practice. What `Block` actually does: **content stays merged as inert data**
  (a mismatched peer's `Configs.Characters/Things/Abilities` still contain every pack entry — a previously
  saved character using a pack class still loads), while **every runtime feature gated behind
  `ClassForgePlugin.FeaturesActive` switches off** — the recipe engine, class-select injection, trait-loadout
  injection, and icon fallback. This is the honest, weaker claim: "runtime features off, content stays
  merged," not "the mismatch is undone." The block latch is **session-scoped, not process-scoped**: it resets
  at the same session-start boundary DevKit's own `ParityService.ResetSession()` uses, so a stale block from a
  previous session in the same process can't silently leave one peer feature-off while a fresh handshake
  reports `Match` for everyone (an earlier build had a process-latching bug here; fixed).

### 9.6 MP test plan

See §8 steps 8–9 for the executable checklist. Summary: (1) both peers install the same pack at the same
version, each plays a pack class, fight one combat together, verify identical HP/status/summon state and
identical recipe fire/skip decisions on both peers' logs; (2) a peer with a mismatched pack set/version/knob
attempts to join and ClassForge's `Block` behavior engages (§9.5) — runtime features off on the diverged peer,
with a message naming the mod and the mismatch kind. The registration that backs this is
`ClassForgePlugin.Load()` calling `FTK2Mods.DevKit.ParityService.RegisterWithCallback(guid, version, dataHash,
enabledFeatures, onParityFailed)` — `dataHash` over all enabled packs' files excluding `localization/**`,
`enabledFeatures` = sorted active pack ids **plus** `feature:<Name>=<value>` knob entries — detailed in §3
"Multiplayer parity registration." Real two-peer verification against a live game session is still pending
(§Status; see the operator handoff doc for the smoke-test script).

## 10. Milestones

- **M1 — Pack loader; classes appear and play. Implemented.** Manifest parsing, load-order/dependency
  resolution, `Configs.Characters/Things/Abilities` merge (with M0 live-id adds-only enforcement), localization
  merge, per-pack enable knob, class-select injection. Traits merge into `Configs.Things` immediately
  grantable via the vanilla loadout pool — there was never a separate "inert until M2" state once the no-bridge
  resolution (§3 point 2) landed. `FTK2Mods.DevKit.ParityService` registration
  (guid/version/dataHash/enabledFeatures via `feature:` tuples, §3, §9.6) and the
  `[Multiplayer] OnParityMismatch = Block` default (§5, §9.5) shipped from the first milestone as planned.
- **M2 — Trait injection + UI polish. Implemented.** The no-bridge resolution (§3 point 2, §4.6) made this
  milestone's actual work "mint `TRAIT_`-prefixed configs + wire the loadout-pool injection postfix," not
  "solve an enum bridge" — the `eTraits` enum plays no role. Full `RenderCustomizationContainer`/
  `RenderStatsContainer` parity for pack classes and the icon/portrait fallback patch are both live.
- **M3 — Skill-recipe framework. Implemented.** The generic dispatcher and all 15 v1.1 trigger hook points
  (§4.6, §6); every recipe in both shipped packs (`CF_PACK_BALDURS`, `CF_PACK_EOR_CLASSES`) fires and is
  verifiable per the Testing plan; `SkillRecipeVerboseLogging` knob live. 149 `ClassForge.Recipes` tests pass.

All three milestones are **offline-verified only** (`ClassForge.Core` 21 tests + `ClassForge.Recipes` 149
tests, all green against a snapshotted reference-assembly build) — **in-game smoke testing has not yet run**
(no restored game install at implementation time). See the operator handoff doc for the smoke-test script:
`docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md`.

## 11. Open questions

Nine of the original ten open questions are resolved (below, with their resolution). Three remain open,
tracked here rather than closed silently:

**Resolved:**

1. ~~`eTraits` enum bridge mechanism~~ — **resolved: there is no bridge.** §3 point 2, §4.6.
2. ~~`ON_CRIT` signal availability~~ — **resolved: `pIsCrit` is an explicit `bool` parameter** on
   `InteractableHelper.ApplyStatChange` (PSN §2 L626). Ships ungated; the `CalculateFinalDamage` candidate is
   ruled out (no crit info on its return value). §4.6, §6.
3. ~~`ConfigsHelper.ProcessDirectory`/`CONFIGS_JSON_SOURCES` signature~~ — **resolved: `CONFIGS_JSON_SOURCES`
   is a hardcoded `private const string`, not data-driven; neither `LoadConfigs` nor `ReloadConfigs` accepts
   additional source directories.** ClassForge merges via Postfix on both, writing directly into the returned/
   `ref Configs` object. §3, §6.
4. ~~`ADD_CHARACTER`'s exact `{Type, Value}` field mapping~~ — **resolved: `{eSummonTypes Type, string
   Value}`, no count field.** §4.4.
5. ~~Unverified exact string-enum values~~ — **resolved by reference to `docs/research/enum-ground-truth.md`.**
   `Rarity`/`Slots: ["MAIN_HAND"]` confirmed legal; `Material: "IRON"` was illegal, corrected to `"METAL"`. §4.5,
   §7. **One residual sub-item stays genuinely unverified**: the runtime semantics of `CHANGE_STAT.Type` on a
   *non-`HP`* stat (e.g. granting `PA`/`SA`/`FOC`) — authors use `MAGICAL`/`PHYSICAL`/`REGEN` by flavor and the
   validator only warns (doesn't error) on other `eDamageType` members for non-`HP` stats. §4.4.
6. ~~"Gain gold" effect has no verified combat-time verb~~ — **resolved: parked, not shipped**, then
   **superseded and ADOPTED (v1.2, 2026-08-06):** `LootDropHelper.GetLootDropsFromEnemies`'s signature is
   verified (PSN §11); `ON_COMBAT_LOOT` + `GOLD_GRANT`/`ITEM_TAG_GRANT`/`LOOT_SCALE` + `CF_SYNC_LOOT_GRANT_V1`
   (Mode M host mirror+audit) ship, dark behind `[Skills] EnableLootGrants = false` pending operator smoke
   item V-1 (`docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md` §10, milestone M-LG3). §4.6, §9,
   coverage matrix §1/§2/§3.
8. ~~Hot-reload idempotency~~ — **resolved as a corollary of #3: both `LoadConfigs` and `ReloadConfigs` rebuild
   `Configs` from scratch, so a re-running merge postfix never sees leftover pack data and cannot
   double-insert.** The residual risk (other systems holding a stale `Configs` reference across a reload) is a
   DevKit hot-reload concern, not a ClassForge merge concern.

**Open — deferred by design (not blockers for M1–M3):**

7. **Collision/dependency policy.** Current, implemented behavior is "last pack in load order wins, log both
   pack ids" for pack-vs-pack id collisions (refused outright, not merged, if the id collides with a *live*
   pre-pack id — M0) and "skip pack, log loudly" for unresolved dependencies. Whether other mod packs need
   semantic-version dependency ranges (vs. simple presence checks) is still unresolved.
9. **Skeleton reuse.** Whether the base game ships its own skeleton enemy character configs (a `SKELLY_*`-style
   id under `BaseType:"SKELETON"`) that the Necromancer could summon instead of (or in addition to) the pack's
   own `CF_SKELETON_WARRIOR` remains unverified — not a blocker since `CF_SKELETON_WARRIOR` is fully
   self-contained.
10. **Multiplayer combat-authority model for skill-recipe procs — still open upstream, no longer blocking.**
    Whether `CombatHelper`/`InteractableHelper` combat resolution runs host-simulated-and-replicated (§9.3b
    Design A) or independently per-peer (§9.3b Design B) is unresolved, but the shipped v1.1 vocabulary is
    written so **both** designs are safe (§4.6 determinism invariants: every roll draws from
    `CombatState.Random` at a point reached identically on every peer, and every effect is emitted through a
    native `eCombatActions` verb) — so this no longer blocks shipping. All other repo-wide MP unknowns are
    tracked centrally in `docs/MULTIPLAYER.md`'s numbered open-questions list, not duplicated here.

**New, found by the Wave-4 adversarial MP-correctness review** (`docs/research/eor-rehost-mp-review.md`),
fixed in the current code and described as implemented above, not repeated as open items here: `dataHash`
prefix format, transport send/receive wiring, trait-injection session-ordering gate (fail-closed, with the
`AdventureSelectionDirector` follow-up noted in §9.1), `enabledFeatures` knob coverage, session-scoped Block
latch, honest Block semantics, host-detection wiring, and M0 adds-only enforcement. One item from that review
remains a genuine open follow-up, not yet implemented: moving DevKit's parity handshake anchor earlier than
`PartyManagementDirector` (§9.1) so day-one MP trait injection doesn't have a legitimate empty window.
