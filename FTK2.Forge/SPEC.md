# FTK2.Forge — item upgrade / item-level engine

Plugin GUID: `ftk2mods.forge` · Id prefix: `FRG_` · Priority: **P1** · Contains a **zero-code M1**.

## 1. Purpose & scope

FTK2.Forge turns the game's already-native, already-extensible crafting system
(`CraftConfigs.json` + `CraftingHelper`) into an item-level/upgrade mechanic: consumable
**upgrade orbs** + a base item craft into a "+1" variant, which crafts into "+2", and so on.
It deliberately builds *on top of* `CraftingHelper` rather than replacing it — no new UI flow,
no new save state, no new network protocol. The only genuinely new piece of engineering is a
**ladder generator**: instead of a human hand-authoring hundreds of `_PLUS1..N` `ThingConfig`
variants and their recipes, a small plugin reads declarative `UpgradeRules/*.json` files and
generates the item variants + recipes at config-load time, merging them into `Configs.Things` /
`Configs.CraftConfigs` in memory (the same shape of trick EOR uses to merge its `CustomItems`
Things packs at runtime).

**In scope**: upgrade orb items, hand-authored example tier ladders, a rule-driven generator for
bulk ladders (selectors, growth curves, cost curves, name decoration), salvage/downgrade recipes,
QoL affordances (context-menu shortcut, orb drop-rate knobs), dry-run inspection tooling.

**Deliberately out of scope**:
- Sockets/gems/affixes on top of *dropped* instances of a single item — that is Runeworks'
  problem, and it needs a fundamentally different data model (per-instance state rather than
  per-config-id state; see the tradeoff discussion in §3).
- New stat-calculation logic, new `eCombatActions`, new ability verbs — Forge only ever produces
  ordinary `ThingConfig` entries with ordinary `Equippable.Stats`; it never touches combat math.
- A crafting-station UI redesign — M3's context-menu entry is a shortcut into the existing
  crafting flow, not a replacement for it.
- Visual/model changes per tier (no asset-bundle work); tiers are told apart by name decoration
  and stats only, same as the base game's material tier ladders.

## 2. Player-facing behavior

- Two new consumable materials drop in the world and appear in town/dungeon markets:
  **Whetstone Orb** (weapons) and **Plating Orb** (armor). They stack in inventory like any
  other material.
- At any crafting point, a player holding a base-tier weapon/armor piece and enough orbs sees a
  new craft choice: consume the orbs + the item, receive the same item as "+1" — visibly renamed
  (`Militia Sword +1`), with higher stats, a higher market `Value`, and a normal tooltip (it *is*
  a normal item, so `ItemCardViewHelper` renders it with no special-casing).
- Repeating the process on a "+1" item crafts "+2", and so on up to the rule's level cap (or the
  mod's global `MaxLevelCap` knob, whichever is lower). Orb cost per level scales up (more orbs
  per level, more for rarer base items).
- Right-clicking an eligible item in inventory (M3) shows an "Upgrade" shortcut in the context
  menu when a valid next-tier recipe exists, as a discovery aid — it does not bypass the
  requirement to be at a crafting point.
- Salvaging (M3): crafting a "+N" item back down to "+(N-1)" is possible (pure data, no orb
  refund — a straight downgrade recipe). A separate salvage-for-orbs recipe exists that destroys
  an upgraded item for a small, fixed orb refund (see §4.3 and Open Question #4 for why the
  refund is fixed rather than scaling).
- Orbs additionally have a small chance to drop from combat loot, tunable per loot tier.

## 3. Architecture

### 3.1 Engine vs. data split

| Layer | What it owns |
|---|---|
| Data (`data/Things/*.json`, `data/CraftConfigs/*.json`) | Hand-authored orb items and hand-authored example ladders. Loaded **natively by the game** — no plugin code involved. |
| Data (`data/UpgradeRules/*.json`) | Declarative bulk-ladder generation contract: selectors, growth curves, cost curves, name decoration, salvage mode, per-class overrides. |
| Engine (C# plugin, M2+) | Reads `UpgradeRules/*.json`, resolves selectors against the live `Configs.Things`, clones+mutates `ThingConfig` objects per level, generates matching `CraftRecipe` entries, decorates names via generated localization strings, and merges all of it into `Configs.Things` / `Configs.CraftConfigs` in memory. Zero hardcoded content or tuning numbers. |
| Engine (C# plugin, M3) | QoL: context-menu shortcut, loot-tier drop-chance injection. Still zero hardcoded tuning — reads the same knobs. |

### 3.2 Why M1 needs no plugin at all

`ConfigsHelper.ReadJsonConfigs` / `ProcessDirectory` / `ParseThingsConfig` already walk the
entire `StreamingAssets\Assets\Configs\JSON~\` tree natively (per `docs/research/game-code-
reference.md` §4). `CraftConfigs.json` + `CraftingHelper` are already fully data-driven — the
base game only *uses* crafting for candy, but nothing in the engine restricts it to candy
(`docs/research/data-schemas.md`, Crafting section: "Fully extensible in JSON"). That means a
hand-authored orb item + a hand-authored 3-level recipe chain is **just more entries in the same
JSON files the game already loads** — no Harmony patch, no DLL, no BepInEx plugin required to
prove the mechanic. This is M1.

Two delivery paths for those JSON entries, both zero-code:

- **Path A (used by this spec's shipped example, verified)**: ship the files under
  `data/Things/` and `data/CraftConfigs/` merged directly into the corresponding files under the
  game's own `StreamingAssets\Assets\Configs\JSON~\Things\` and `...\CraftConfigs.json`. This is
  guaranteed to work because it's the exact tree `ReadJsonConfigs`/`ProcessDirectory` already
  scan. Downside: it edits game-owned files (not toggleable, at risk from game updates, not a
  clean BepInEx-plugin install).
- **Path B (preferred if verified, see Open Question #6)**: EOR ships a generic-looking
  drop-in folder, `CustomItems\Things\*.json`, documented as "custom item packs (merged into
  Things)" — "for interop" per `docs/research/data-schemas.md` §"EOR mod data surfaces". *If*
  that loader is generic (scans any `*.json` dropped there, not just EOR's own files), Forge's
  orb `ThingConfig`s could ship there instead, which is far more portable/toggleable. There is
  no equivalent documented drop-in for `CraftConfigs.json`, so the recipe half would still need
  Path A even in that case. Not relied upon until verified.

Once M2's plugin exists, this whole question becomes moot for *generated* content: the plugin
merges directly into the in-memory `Configs.Things` / `Configs.CraftConfigs` dictionaries after
they're parsed, so nothing ever touches disk, nothing is destructive, and hot-reload works. M1's
hand-authored example intentionally keeps working the "hard way" so the zero-code claim is
actually load-bearing and not secretly dependent on the engine.

### 3.3 The ladder generator (M2)

At `ConfigsHelper.LoadConfigs`/`ReloadConfigs` time (Postfix), for each enabled rule in
`UpgradeRules/`:

1. **Resolve the selector** against every entry currently in `Configs.Things` (Class / Tags /
   Rarity / id-glob / explicit include-exclude — see §4.2). Skip any id that is not a real,
   currently-loaded `ThingConfig`, and skip (with a Debug log line) any id that already has a
   generated or hand-authored `_PLUSn` sibling (collision = "already has a ladder", never
   overwritten — fail-safe per `CONVENTIONS.md`).
2. For each selected base item, for each level `1..min(rule.Levels, MaxLevelCap)`:
   - Deep-clone the base item's `ThingConfig` object (there is no `Inherits` support documented
     for `ThingConfig` — that is an `Abilities.json`-only feature per
     `docs/research/data-schemas.md` — so the generator must clone the full object rather than
     reference it; see Open Question #5).
   - Apply each `StatGrowth` entry (or the resolved `PerClassOverrides` for that item's `Class`)
     to `Equippable.Stats`, using the level-cumulative formula for its `Mode` (§4.2).
   - Apply `ValueScaling` to `Value`.
   - Assign the new id `{BaseId}_PLUS{level}` and add tags `FRG_UPGRADED`, `FRG_TIER_{level}`.
   - Emit a `NameDecoration`-formatted pair of localization strings (`ID`, `ID_DESCRIPTION`) so
     the item's display name and tooltip flow through vanilla `Lang.__t` unmodified.
   - Compute `OrbCost` for that level (base + per-level step, times the item's rarity
     multiplier) and emit a `CraftRecipe`: `Catalyst` = the rule's orb id, `CatalystAmount` = the
     computed cost, `Ingredients` = `{ previousTierId: 1 }` (level 1's previous tier is the base
     item itself).
   - If `Salvage.Enabled`, emit the mirror-image downgrade recipe (§4.3).
3. Merge every generated `ThingConfig` into `Configs.Things` and every generated `CraftRecipe`
   into `Configs.CraftConfigs` (appending to the output id's `Recipes` list if it already has
   entries — never replacing).
4. If `DryRun` is on, instead of (or in addition to, per knob) merging, serialize the full
   generated ladder set to a file for inspection (§5, §8) and skip the merge.

Everything numeric above (`Levels`, growth `Value`s, `EveryNLevels`, orb amounts, rarity
multipliers, rounding mode) comes from the rule file — the engine hardcodes none of it, per
`CONVENTIONS.md`'s "no content and no tuning numbers" rule.

### 3.4 Why real item configs, and the tradeoff vs. instance-state

Every generated tier is a **complete, ordinary `ThingConfig` entry** merged into `Configs.Things`
— indistinguishable, from the game's point of view, from a hand-authored item. Consequences:

- **Save/MP work for free.** An owned "+2 Militia Sword" is serialized the same way any owned
  item is (id + quantity in inventory) — there is no per-instance state to persist, migrate, or
  desync. `ItemCardViewHelper.ShowItemCard*` and `CharacterHelper.GetStat` need **no patch** —
  they already read `Equippable.Stats` off whatever `ThingConfig` the id resolves to.
- **The cost is combinatorial content.** N base items × L levels = N×L real `ThingConfig`
  entries (and matching localization strings) that exist for the lifetime of the process, whether
  or not any player ever crafts one. `MaxLevelCap` and tight `Selector`s are the main lever
  against this blowing up on large item pools.
- **Contrast with Runeworks (sockets/gems on dropped instances)**: that system's whole point is
  *not* enumerating every possible (base item × gem combination) as a static config — it needs
  per-dropped-instance state (which socket has which gem) that must be patched into stat
  calculation, tooltip rendering, and save serialization explicitly, because there is no native
  "instance data on a `ThingConfig`" concept in this engine (confirmed absent per
  `docs/research/game-code-reference.md` §6: "No socket/rune/gem/affix/enchant/reforge system
  exists in the game assembly"). Forge sidesteps all of that by only ever producing more *config
  ids*, never more *instance state* — at the cost of the combinatorial blowup above. This is the
  central design bet of this mod and it is not free; it is documented here so a future reviewer
  doesn't "fix" it into an instance-state system without re-deriving why that's a different mod.

## 4. Data file formats

### 4.1 `Things/*.json` — orb items and hand-authored ladders

Standard `ThingConfig` (`docs/research/data-schemas.md` §"Things\\"). Forge uses a subset of its
documented fields for orbs (no `Equippable`/`Interactable` — orbs are inert crafting materials)
and the full weapon subset for ladder tiers:

```jsonc
// schema (annotated) — orb material
{
  "<FRG_ORB_id>": {
    "ConsumableType": "NONE",       // orbs are not usable outside crafting
    "Value": 0,                     // market sell/buy price
    "Class": "MATERIAL",            // see Open Question #1 — unverified enum value
    "Rarity": "COMMON",
    "Hidden": false,
    "Stacks": true,                 // orbs stack like any other material
    "Tags": []                      // DROPPABLE / TOWN_MARKET / DUNGEON_MARKET / rarity /
                                     // FRG_ORB / FRG_ORB_WEAPON | FRG_ORB_ARMOR
  }
}
```

```jsonc
// schema (annotated) — generated/hand-authored ladder tier
{
  "<BaseId>_PLUS<N>": {
    "ConsumableType": "NONE",
    "Value": 0,                     // base Value scaled by ValueScaling up to level N
    "Class": "<same as base item>",
    "Rarity": "<same as base item>",
    "Hidden": false,
    "Stacks": false,
    "Equippable": {
      "Slots": [],                  // cloned verbatim from base item (see Open Question #2)
      "Stats": {},                  // cloned base Stats + StatGrowth applied through level N
      "Passives": []                // cloned verbatim
    },
    "Tags": []                      // cloned base Tags + FRG_UPGRADED + FRG_TIER_<N>
  }
}
```

Shipped example: `data/Things/FRG_Orbs.json` (2 orbs), `data/Things/
FRG_Ladder_BLADE_MILITIA_LIGHT_01.json` (3 hand-authored tiers).

### 4.2 `UpgradeRules/*.json` — the generator contract (extensibility surface)

One file = one rule object (a future revision may allow an array of rules per file; v1 is one
rule per file, new file per rule, picked up automatically per `CONVENTIONS.md`'s registry rule).

```jsonc
// schema (annotated)
{
  "RuleId": "FRG_RULE_...",         // unique, used in dry-run output and log lines
  "Enabled": true,

  "Selector": {
    "Classes": [],                  // ThingConfig.Class values to include (weapon/armor Class
                                     // enum, e.g. BLADE, AXE, ARMOR_BODY — 23 values documented
                                     // in data-schemas.md §"Things\\"). Empty/omitted = all classes.
    "Rarities": [],                 // ThingConfig.Rarity values to include. Empty = all rarities.
    "Tags": {
      "AllOf": [],                  // item must have every tag listed
      "AnyOf": [],                  // item must have at least one (empty = no AnyOf constraint)
      "NoneOf": []                  // item must have none of these (used to exclude
                                     // already-upgraded items and opt-outs, see below)
    },
    "IdPatterns": [],               // glob patterns over the item id, e.g. "BLADE_MILITIA_*"
    "ExplicitIncludes": [],         // ids force-included regardless of the above
    "ExplicitExcludes": []          // ids force-excluded regardless of the above (e.g. an item
                                     // already covered by a hand-authored ladder)
  },

  "Levels": 5,                      // ladder depth for items matched by this rule

  "OrbCost": {
    "OrbItemId": "FRG_ORB_...",     // which orb this rule's ladder consumes
    "BaseAmount": 1,                // orb cost at level 1
    "AmountPerLevel": {
      "Mode": "FLAT_STEP",          // FLAT_STEP is the only mode in v1 (extensible later)
      "StepEveryLevels": 1,         // apply +StepAmount every N levels
      "StepAmount": 1
    },
    "RarityMultiplier": {}          // Rarity -> multiplier applied to the computed cost,
                                     // rounded per the rule's Rounding-less default (ceil)
  },

  "StatGrowth": [                   // ordered list, applied cumulatively per level
    {
      "Stat": "ATK",                // must be one of the 20 documented Stats keys
                                     // (ACC ATK AWR CRT DEF EVD FOC HP HRG INT LCK PA SA PRW
                                     // RES SPD STR TAL THRN VIT)
      "Mode": "PERCENT_PER_LEVEL",  // PERCENT_PER_LEVEL | FLAT_PER_LEVEL |
                                     // FLAT_EVERY_N_LEVELS | PERCENT_EVERY_N_LEVELS
      "Value": 0.08,                // meaning depends on Mode (fraction for PERCENT_*, absolute
                                     // stat points for FLAT_*)
      "EveryNLevels": 1,            // required only for the *_EVERY_N_LEVELS modes
      "Rounding": "ROUND_NEAREST"   // ROUND_NEAREST | ROUND_UP | ROUND_DOWN | NONE (float stat)
    }
  ],

  "ValueScaling": {                 // same Mode vocabulary as StatGrowth, applied to Value
    "Mode": "PERCENT_PER_LEVEL",
    "Value": 0.15,
    "Rounding": "ROUND_UP"
  },

  "NameDecoration": {
    "Style": "SUFFIX_PLUS_N",       // SUFFIX_PLUS_N | ROMAN_NUMERAL | NONE
    "Format": "{BaseName} +{Level}" // {BaseName} resolved via Lang.__t(baseId) at generation
                                     // time; {Level} is the 1-based tier number
  },

  "PerClassOverrides": {
    "<Class>": {
      "StatGrowth": []               // fully replaces StatGrowth for items of this Class only;
                                      // OrbCost/ValueScaling/NameDecoration inherit the rule's
                                      // top-level values unless also overridden here (v1 only
                                      // supports overriding StatGrowth per class; see Open
                                      // Question below on extending this)
    }
  },

  "Salvage": {
    "Enabled": true,
    "Mode": "DOWNGRADE"              // DOWNGRADE | REFUND_ORBS | BOTH (see §4.3)
  }
}
```

Shipped example: `data/UpgradeRules/FRG_common_uncommon_weapons.json` — all `COMMON`/`UNCOMMON`
weapon-class items except the hand-authored `BLADE_MILITIA_LIGHT_01` (explicitly excluded to
avoid a double-generated ladder), 5 levels, +8% ATK/level, +1 CRT every 2 levels, with a
gentler per-class override for `BOW`/`HANDBOW` (+6% ATK/level, +1 ACC every 3 levels).

### 4.3 Salvage / downgrade recipes

Expressed purely as more `CraftConfigs` entries, generated alongside the upgrade recipes when a
rule's `Salvage.Enabled` is true:

- **`DOWNGRADE`** (safe, always available): a recipe under the lower tier's output id, consuming
  1 of the higher tier, no `Catalyst` required. Reverses one level with no orb refund. This is
  the only mode used in the shipped example (`data/CraftConfigs/
  FRG_Ladder_BLADE_MILITIA_LIGHT_01.json` — each `_PLUSN` output id has both its upgrade recipe
  and, one level up, its downgrade recipe).
- **`REFUND_ORBS`** (illustrative only, not shipped — see Open Question #4): a recipe under the
  orb's own output id, consuming 1 upgraded item, refunding a *fixed* 1 orb regardless of tier.
  It is fixed rather than scaled with tier because it is unverified whether a single
  `CraftingHelper.CraftItem` call can output more than 1 unit of `OutputItemID` (§ Open Questions
  #4) — until that's confirmed, promising a scaling refund would be an engine claim the code
  can't actually keep.
- **`BOTH`**: emits both recipe families.

## 5. Knobs

All under a single BepInEx config file, `ftk2mods.forge.cfg`.

- `[General] Enabled (bool, true)` — master switch; if false, no patches apply and no rules are
  generated (M1's hand-authored data is unaffected either way — it isn't gated by the plugin).
- `[General] VerboseLogging (bool, false)` — log every selector match/skip/collision at
  `LogLevel.Debug`.
- `[General] MaxLevelCap (int, 10)` — hard ceiling applied on top of every rule's own `Levels`.
- `[General] GlobalStatGrowthMultiplier (float, 1.0)` — multiplies every resolved `StatGrowth`
  delta after per-rule/per-class computation, for one-knob balance passes.
- `[General] DryRun (bool, false)` — when true, generated ladders are written to
  `DryRunOutputPath` instead of merged into `Configs.Things`/`Configs.CraftConfigs`.
- `[General] DryRunOutputPath (string, "BepInEx/plugins/FTK2.Forge/dryrun/")` — folder for
  dry-run dumps (one file per rule, see §8).
- `[Orbs] EnableOrbDrops (bool, true)` — master switch for the M3 loot-drop injection.
- `[Orbs] DropChanceCommonLoot (float, 0.02)` / `DropChanceUncommonLoot (float, 0.03)` /
  `DropChanceRareLoot (float, 0.05)` / `DropChanceEpicLoot (float, 0.08)` — per-loot-tier chance
  (0–1) that a combat loot roll includes one bonus orb, split evenly between the two orb types.
- `[UI] EnableUpgradeContextMenuEntry (bool, true)` — master switch for the M3 context-menu
  shortcut.
- `[Naming] NameDecorationStyleOverride (string, "")` — if non-empty, overrides every rule's
  `NameDecoration.Style` mod-wide (for players who prefer roman numerals, etc.); empty = respect
  each rule's own setting.
- `[Salvage] EnableSalvage (bool, true)` — master switch; if false, no `Salvage`-derived recipes
  are generated even if a rule requests them.

## 6. Patch targets & integration points

M1 requires **no patches** — see §3.2.

- **`ConfigsHelper.LoadConfigs`** — Postfix. After vanilla config parsing completes, run the
  ladder generator (§3.3) and merge results into `Configs.Things`/`Configs.CraftConfigs`.
- **`ConfigsHelper.ReloadConfigs`** — Postfix, identical body. `ReloadConfigs` is documented as
  existing specifically to support hot-reload (`docs/research/game-code-reference.md` §4); Forge
  piggybacks on it so `UpgradeRules/*.json` edits can be iterated on via `FTK2.DevKit`'s
  hot-reload loop per `CONVENTIONS.md`'s testing section, without a full game restart.
- **`InventoryViewHelper.ShowContextMenu`** — Postfix (EOR-proven target, per
  `docs/research/game-code-reference.md` §7). Adds an "Upgrade" entry when the right-clicked
  item's id has at least one resolvable `CraftRecipe` in `Configs.CraftConfigs` where the item is
  an `Ingredient`. Exact signature/menu-entry API is unverified (Open Question #8).
- **`LootDropHelper.GetLootDropsFromEnemies`** — Postfix (EOR-proven target, same section). Rolls
  the `[Orbs] DropChance*` knobs and appends orb drops to the vanilla result. Exact
  signature/return type is unverified (Open Question #9).
- **Explicitly not patched**: `CharacterHelper.GetStat`, `UIHelper.GetBreakdownStats`,
  `ItemCardViewHelper.ShowItemCard*`, `CraftingHelper.*` — all of these already do the right
  thing for a real `ThingConfig`/`CraftRecipe` with no changes, per §3.4. Listed here explicitly
  so a future contributor doesn't add speculative patches that duplicate vanilla behavior.

## 7. Example starting dataset

```
FTK2.Forge/data/
  Things/
    FRG_Orbs.json                              # M1 — FRG_ORB_WHETSTONE, FRG_ORB_PLATING
    FRG_Ladder_BLADE_MILITIA_LIGHT_01.json      # M1 — hand-authored _PLUS1/_PLUS2/_PLUS3 tiers
  CraftConfigs/
    FRG_Ladder_BLADE_MILITIA_LIGHT_01.json      # M1 — upgrade recipes (orb+base->+1->+2->+3)
                                                 #      and mirrored DOWNGRADE recipes
  UpgradeRules/
    FRG_common_uncommon_weapons.json            # M2 — the generative rule; proves selectors,
                                                 #      per-class overrides, rarity-scaled cost,
                                                 #      and the ExplicitExcludes collision guard
                                                 #      against the M1 hand-authored ladder
  Localization/
    en.json                                     # ID / ID_DESCRIPTION for both orbs and the
                                                 #      3 hand-authored tiers (EOR style)
```

This set demonstrates, end to end: (a) the zero-code M1 claim — orbs + a full working 3-level
recipe chain with no plugin loaded at all; (b) that M2's generator and M1's hand-authored data
can coexist without collision (the rule explicitly excludes the hand-authored base item); (c)
name decoration flowing through localization rather than a `ThingConfig` field; (d) rarity- and
class-sensitive cost/growth curves via `RarityMultiplier` and `PerClassOverrides`; (e) the
`DOWNGRADE` salvage mode.

## 8. Testing plan

All steps assume `FTK2.Forge` + the base EOR install (which ships the debug toolkit referenced
below, per `docs/feasibility.md` Tier 0). Executable in under 15 minutes.

1. **M1, no plugin loaded**: merge `data/Things/*.json` and `data/CraftConfigs/*.json` into the
   game's own `StreamingAssets` tree (Path A, §3.2). Launch the game with *no* Forge DLL present.
   Confirm `BLADE_MILITIA_LIGHT_01` exists in the live `Weapons.json` first (Open Question #11) —
   if it doesn't, swap the example ladder onto the nearest real id before testing.
   - Use the EOR debug toolkit's give-item command to spawn `BLADE_MILITIA_LIGHT_01` and several
     `FRG_ORB_WHETSTONE`.
   - Go to any crafting point; confirm the "+1" craft choice appears
     (`CraftingHelper.GetCraftingChoices`/`GetCraftChoiceText`) and costs 1 orb.
   - Craft it; confirm the result is named "Militia Sword +1", has the higher `ATK`/`CRT` from
     §4.1's example numbers, and its tooltip renders normally (`ItemCardViewHelper`).
   - Repeat to +2, +3. Confirm each craft choice's cost matches the recipe (`CatalystAmount`
     1/2/3).
   - Craft the +1 recipe's second `Recipes` entry (the downgrade path) from a +2 item; confirm it
     returns to +1 with no orb cost and no orb refund.
2. **M2, plugin loaded, DryRun on**: enable Forge, set `[General] DryRun = true`, load a save.
   Inspect `DryRunOutputPath` for `FRG_RULE_COMMON_UNCOMMON_WEAPONS`'s dump; confirm every
   selected item (by `Selector`), every generated id, computed orb cost per level, and computed
   stat block per level are present and match hand-calculation from the rule file.
3. **M2, DryRun off**: confirm the same generated ids now resolve in-game (give-item debug
   command on a generated id, e.g. `<some selected weapon>_PLUS1`) and craft normally at a
   crafting point, exactly like the M1 example.
4. **Collision guard**: confirm `BLADE_MILITIA_LIGHT_01` was *not* touched by the generator (its
   hand-authored ladder is untouched) — check `VerboseLogging` output for the expected skip line.
5. **`MaxLevelCap`**: set it below a rule's `Levels`; confirm generation stops at the cap.
6. **`GlobalStatGrowthMultiplier`**: set to `2.0`; confirm generated stat deltas double relative
   to the DryRun baseline from step 2.
7. **M3 — context menu**: right-click an eligible base item in inventory; confirm the "Upgrade"
   entry appears only when a valid recipe exists, and disappears once `[UI]
   EnableUpgradeContextMenuEntry` is turned off.
8. **M3 — orb drops**: set `DropChanceCommonLoot = 1.0` for a quick deterministic check; fight a
   common-tier encounter; confirm an orb appears in the loot result.
9. **Fail-safe check**: introduce a deliberately malformed `UpgradeRules/*.json` (e.g. invalid
   JSON); confirm Forge logs loudly and leaves vanilla `Configs.Things`/`Configs.CraftConfigs`
   untouched (per `CONVENTIONS.md`'s "fails safe" rule), rather than crashing config load for
   every other mod.

## 9. Save & multiplayer considerations

- **Save**: no custom serialization anywhere. A "+2 Militia Sword" in a save file is stored
  exactly like any vanilla item (id + quantity) because it *is* a normal `ThingConfig` entry by
  the time the save system sees it (§3.4). Nothing breaks if the mod is removed later beyond the
  item id no longer resolving (same failure mode as removing any other content mod).
- **Multiplayer**: single-player-first, as with every mod in this repo, but this one has a sharp
  MP requirement: `Configs.Things`/`Configs.CraftConfigs` must be **identical** across host and
  all peers, or a generated id that resolves on one machine won't resolve on another (missing-
  item/desync risk). This means:
  - Every peer must run the same version of Forge with the same `UpgradeRules/*.json` (and the
    same hand-authored M1 data, if merged via Path A).
  - `DryRun` is local/dev-only by design — it must never be enabled differently between peers in
    the same session (a dry-run peer never merges the generated content at all).
  - Recommend piggybacking on EOR's existing mod-sync hash handshake pattern
    (`EOR_VER/EOR_CFG/EOR_DAT/EOR_SYS/EOR_DEF/EOR_SIG`, `docs/research/game-code-reference.md`
    §7) if it exposes any extension point for third-party mods; otherwise Forge needs its own
    equivalent hash-and-refuse-to-join check via a custom `AdventureDirector._handleNetworkAction`
    action, gated behind a knob that defaults to "on" per `CONVENTIONS.md`'s MP-safe-by-default
    rule. Left as an implementation detail for M2 rather than specified further here (Open
    Question #6/#7 territory — depends on what EOR's handshake actually exposes).
  - The crafting action itself (`CraftingHelper.CraftItem`) is vanilla and presumably already
    MP-safe the same way base-game candy crafting is; Forge adds no new network action for the
    craft/salvage/downgrade flows themselves, only (potentially) for the config-hash handshake
    above.

## 10. Milestones

- **M1 — zero-code proof (this spec's primary deliverable)**: `FRG_ORB_WHETSTONE`,
  `FRG_ORB_PLATING`, and a hand-authored 3-level ladder on one real base item, entirely as JSON
  merged into the game's native config tree. No plugin. Ships as `data/Things/FRG_Orbs.json`,
  `data/Things/FRG_Ladder_BLADE_MILITIA_LIGHT_01.json`,
  `data/CraftConfigs/FRG_Ladder_BLADE_MILITIA_LIGHT_01.json`, `data/Localization/en.json`.
- **M2 — the generator**: the plugin, `UpgradeRules/*.json`, the `ConfigsHelper.LoadConfigs`/
  `ReloadConfigs` postfixes, `DryRun`, `MaxLevelCap`, `GlobalStatGrowthMultiplier`. Ships
  `data/UpgradeRules/FRG_common_uncommon_weapons.json` as the proof rule. Independently shippable
  on top of M1 — M1's hand-authored ladder keeps working unchanged.
- **M3 — QoL**: context-menu shortcut (`InventoryViewHelper.ShowContextMenu`), orb drop-rate
  injection (`LootDropHelper.GetLootDropsFromEnemies`), `Salvage` recipe generation
  (`DOWNGRADE`/`REFUND_ORBS`/`BOTH`). Independently shippable on top of M2 — none of it is
  required for M1/M2's core loop to work.

## 11. Open questions

1. `ThingConfig.Class` enum has no documented "material/consumable" value — the 23 values listed
   in `docs/research/data-schemas.md` (`BLADE...ARMOR_TRINKET, TRAIT`) all describe equippable
   weapon/armor slots. `FRG_ORB_*`'s `Class: "MATERIAL"` in the shipped example is a placeholder;
   verify against a real non-equippable `Things\Items.json` entry (e.g. the base game's candy
   items, which already prove non-equip crafting inputs work) before shipping for real.
2. `Equippable.Slots[]` token format is not enumerated anywhere in the two reference docs —
   `HeroCharacterConfig`'s friendly field names (`MainHand`, `OffHand`, …) hint at semantics but
   not the literal string the `Slots[]` array expects (`"MAIN_HAND"` is this spec's guess). Needs
   confirmation before the M2 generator can safely clone `Equippable` blocks unmodified.
3. Whether `CraftRecipe.Catalyst`/`CatalystAmount` are safely omittable for a "no catalyst
   required" recipe (used by every `DOWNGRADE` salvage recipe in this spec), or whether
   `CraftingHelper` requires them and treats an absent `Catalyst` as an error/no-match.
4. Whether a single `CraftConfig` recipe can output more than 1 unit of `OutputItemID` per craft.
   This directly limits `REFUND_ORBS` salvage tuning — if output quantity is always 1, "refund N
   orbs for a +N item" cannot be expressed as a single recipe and would need either N stacked
   craft actions or an engine-side patch, which would break M3's "expressed as recipes too"
   design goal. `REFUND_ORBS` is therefore illustrative-only in this spec pending this answer.
5. Confirm `ThingConfig` truly has no `Inherits` support (only `Abilities.json` documents it) —
   if it turns out `ThingConfig` also supports `Inherits`, the M2 generator could emit much
   smaller diffs (inherited base + stat deltas) instead of full clones, which would also make
   dry-run output far more readable.
6. Whether EOR's `CustomItems\Things\*.json` loader is generic (scans any plugin's folder of that
   shape) or hardcoded to EOR's own install directory — determines whether M1 can ever move off
   Path A (direct `StreamingAssets` edits) onto a cleaner, toggleable drop-in folder. Requires
   decompiling `EnhancedOverhaulRemix.dll`, not just `FTK2.dll`.
7. Exact mutability of `Configs.Things`/`Configs.CraftConfigs` from a Harmony postfix (are they
   plain mutable `Dictionary<string,T>` fields on a static/singleton `Configs`, or something that
   needs a different injection point) — needs a dnSpyEx pass before M2 implementation starts.
8. Exact `InventoryViewHelper.ShowContextMenu` signature and how EOR adds a menu entry to it —
   needed to implement M3's "Upgrade" shortcut precisely.
9. Exact `LootDropHelper.GetLootDropsFromEnemies` signature/return type — needed to implement
   M3's orb-drop injection precisely.
10. Whether crafting is restricted to specific world "crafting point" objects or available from
    inventory anywhere (neither reference doc says) — affects how much M3's context-menu
    "Upgrade" shortcut can actually do (open a craft dialog immediately vs. just flagging
    eligibility and pointing the player at the nearest crafting point).
11. Confirm `BLADE_MILITIA_LIGHT_01` actually exists in the live `Weapons.json`, and pull its
    real baseline `ATK`/`CRT`/`Value` — the numbers used in `data/Things/
    FRG_Ladder_BLADE_MILITIA_LIGHT_01.json` are illustrative placeholders chosen for a plausible
    demonstration, not read from the live file (no direct read access to it during spec-writing).
12. Whether `ThingConfig.Expansion` is safely omittable for non-DLC content, or expects an
    explicit value (e.g. `"NONE"`/`"BASE"`) — omitted in the shipped example on the assumption
    it's optional, consistent with how the field is described for `CharacterConfig`.
