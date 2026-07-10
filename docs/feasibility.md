# For The King II — Modding Feasibility & Design Doc

> Compiled 2026-07-09/10 from a deep teardown of the **Enhanced Overhaul Revamped** mod folder (this directory)
> and the installed game at `E:\Games\Steam\steamapps\common\For The King II`.
> All class/method/field names below were extracted from the actual assemblies (metadata + IL scans), not guessed.
> Raw dumps live in the session scratchpad (`ftk2-typedefs.txt`, `ftk2-members.txt`, `ftk2-userstrings.txt`, etc.).

---

## 1. Architecture — how FTK2 modding works

- **Unity Mono game** (not IL2CPP) → assemblies decompile to near-readable C#. Best-case modding scenario.
- **Game logic** = one assembly: `For The King II_Data\Managed\FTK2.dll` (~6.4MB, 5,375 types).
- **Mod stack** (this folder): `winhttp.dll` (UnityDoorstop) → **BepInEx 5.4.23** → plugins in `BepInEx\plugins\`.
  The compiled plugin `EnhancedOverhaulRemix.dll` (v0.7.0.51, GUID `lastg.ftk2.banditkingplayable`) uses **Harmony**
  to patch 73 game methods and runtime-merges the JSON packs in `FTK2_EnhancedPets` / `FTK2_EnhancedMercenaries`.
- **The game natively loads its entire gameplay database from loose JSON**:
  `For The King II_Data\StreamingAssets\Assets\Configs\JSON~\` — characters, abilities, items, statuses, quests,
  adventures, dungeons, map-gen, traps, crafting, AI behaviours, NPCs, dialogues. No mod framework needed to edit these.
- **Config loader**: `ConfigsHelper` (`LoadConfigs`, `ReadJsonConfigs`, `ParseThingsConfig`…) fills the `Configs`
  registry (~57 fields, ~1:1 with the JSON files). **`ConfigsHelper.ReloadConfigs` exists → hot-reload is possible**
  (a tiny hotkey plugin would make JSON iteration instant).
- **No official modding API** (no Workshop/ModIO/AddOns anywhere in the assembly). BepInEx/Harmony + JSON is the way.

### Core design fact (drives every estimate below)

FTK2 is a **data-composition engine over a fixed, code-enumerated verb vocabulary**.
- *Recombining existing verbs* (stats, statuses, skills-by-reference, items, recipes, quest objectives, AI weights) = **pure JSON**.
- *New verbs* (new skill logic, new status Type, new quest objective/trigger types, new AI scoring) = **C# Harmony patch**.

---

## 2. Moddability tiers (from the original teardown)

### Tier 0 — INI toggles (zero effort)
~60 BepInEx config keys from the EOR mod (`BepInEx\config\` after first run): affix system + chances, free combat
camera, intro skip, debug toolkit (give gold/spawn items/heal), diagnostics upload, per-subsystem master switches.

### Tier 1 — Pure JSON text editing (the big surface)
**Game side (`StreamingAssets\Assets\Configs\JSON~\`):**

| File | Controls |
|---|---|
| `Characters.json` (2,126 entries) | Everything with a stat block: player classes (tagged `PLAYER`), 393 bosses, 160 mercs, 178 companions, all enemies/NPCs. Fields: `Stats` (20 keys incl. `PA`/`SA` actions per turn), `Things` (equipment → grants abilities), `Passives` (`SKILL_*`/`STATUS_IMMUNITY_*`), `Threat` (aggro 0–9), `OnDeathAbility`, `CampQuery`/`SwarmQuery` (encounter composition/waves) |
| `Abilities.json` (972) | Ability composition: targeting shapes (SINGLE/AOE/ROW/COLUMN/SPLASH/SWIPE…), action economy (`IsMajorAction`, `RequiresFocus`, `Ammo`), **`Tendency`** AI targeting hint, `Actions` effect list (`CHANGE_STAT`, `ADD_STATUS`, `ADD_CHARACTER` summon, `MOVE`, `FLEE`, `REVIVE_ALLY`, `VEHICLE_*`). Inheritance via `"Inherits": "DEFAULT_ABILITY"` |
| `Behaviours.json` | **AI personality profiles**: name → `[{Category, Weight}]` over ability categories. Shipped: DEFAULT/DPS/SUPPORT/CURSE using ATTACK/SUPPORT_ALLY/SUPPORT_SELF/DEBUFF. Enum supports 14 categories (also TAUNT, SUMMON, FLEE, MOVE_ENEMY, USE_ITEM…) |
| `StatusEffects.json` (~40 Types) | Buffs/debuffs/DOTs: `Type` (code-recognized: BLEED, STUN, TAUNT, REFLECT, DECOY, DEATHMARK, IMBUE_*, AURA…), duration, tick cadence, stat deltas, attached `Passives`. New effects reusing an existing Type = free |
| `SkillConfigs.json` | `SKILL_*` passive **tuning only** (PROC_CHANCE, EVENT_PROC, cooldowns…). Logic is C# — this is the hard wall |
| `Things\{Weapons,Attires,Items,Traits}.json` | 1,633+ equipables. Traits = hidden items granting `Stats` + `Passives` |
| `CraftConfigs.json` | **Crafting recipes exist**: `Output → {Catalyst, CatalystAmount, Ingredients{}}`. Base game only uses it for candy — fully extensible |
| `Materials.json` / `ItemMaterials.json` | Material/reforge tier ladders (bronze→emerald stat progressions per gear family) |
| `ChargeConfigs.json` | Multi-turn wind-up attacks: charging status, area shape, `DAMAGE_PERCENT_*` modifiers |
| `QuestTemplates.json` / `QuestBoards.json` / `Quests\*` | Full quest scripting (see §7 below) |
| `Adventures\*` (per-campaign) | Campaign scripts: map zones, MegaHex placement, encounters, start quests, world modifiers, game stages |
| `MapSpawners.json` / `TowerDefenseUnits.json` | Wave spawners with quest hooks; escort/defend moving units with collide/reach triggers |
| `Traps.json` / `SkillEncounters.json` / `RewardEncounters.json` | Roll-table → outcome-verb encounter systems |
| `Wheels.json` / `WheelWedges.json` | Weighted random-effect tables (Dark Carnival wheel) — a reusable effect-verb library |
| `NPCs.json`, `Followers.json`, `Dungeons\*`, `MegaHexTemplates\*`, `Dialogues\*` | Quest givers, companions, dungeon layouts, map-gen, dialogue trees |

**Mod side (EOR frameworks):** `CustomItems\Things\*.json` (417 items shipped; items grant actives via
`Interactable.Abilities` + weighted `AbilityBag`, passives via `Equippable.Passives`), `VisualFallbacks.json`
(custom id → base-game model), `Localization\*.json`, Pets/Mercs JSON packs (runtime-merged).

### Tier 2 — Asset swaps (image editor)
Loose PNGs, 1:1 replaceable: 34 class portraits + icons, 22 trait icons, service/item icons.
**No custom 3D model pipeline yet** — visuals must fall back to base-game models (Unity asset-bundle work to go further).

### Tier 3 — C# plugins (full scripting)
BepInEx plugin + Harmony = patch any game method. EOR proves the ceiling: affix system, Nemesis enemies,
campaign mutators, world events, town specialists, custom sanctums, class masteries, custom MP sync protocol.

### Caveats
1. **Multiplayer**: EOR fingerprints config+data files (`EOR_CFG/EOR_DAT/EOR_SIG` handshake) — JSON edits are
   single-player or same-files-for-everyone. AI runs host-side.
2. **Phone-home**: `BepInEx\EnhancedOverhaulReports\github_issue_token.txt` is a **live GitHub PAT** used to
   auto-file diagnostic issues on `SirPepperPot/EnhancedOverhaulRemixVersion`; startup version check hits GitHub.
   Delete the file + set `UploadMode = Disabled` if unwanted.

---

## 3. Ask #1 — Battle AI takeover + enemy memory ✅ HIGHLY FEASIBLE (easiest code mod; build first)

**The entire enemy brain is one static class: `AIHelper`.**

- Call chain (IL-verified): `CombatPhase._engageActiveEntity` → `AIHelper.BehaviourAiDecision`
  (falls back to `StandardAiDecision`) → result executed by `CombatPhase._performAiDecision`.
- Decision output = **`CombatDecisionData` with 3 fields: `Ability`, `Position`, `FocusUsed`**.
- **Takeover = Harmony prefix on `BehaviourAiDecision` + `StandardAiDecision`** (optionally `ConfusedAiDecision`),
  write your own decision, return false. Two methods.
- Full `AIHelper` surface: `TryGetAiParameter`, `CategorizeCombatOptions`, `GetAbilityCategory`,
  `StandardAiDecision`, `TryConsiderSecondaryAction`, `BehaviourAiDecision`, `ConfusedAiDecision`,
  `ForceAiDecision`, `GetPreferredTarget`, `_orderTargetsByTendency`, `_canUseAbilityByTendency`,
  `PlayerPartyHasAI`, `_initializeAbilityBag`, `_intializeBehaviourShuffle`, `_getDecisionData`.
- Per-entity AI state: `AIComponent` (`Parameters`, `PriorityTargets`, `Properties`, `BehaviourShuffle`).
- Data knobs: `eAiBehaviours` (DEFAULT/DPS/SUPPORT/TANK/CURSE ← `Behaviours.json`),
  `eAiTendencies` (25 values, per-ability via `CombatAbilityConfig.Tendency`), `Threat` per character.

**Build plan:**
1. *JSON pass (tonight-level)*: give bosses/elites DPS/CURSE profiles, combo tendencies (`MUSTHAVEPOISON` chains),
   raise healer `Threat`, weight ability bags.
2. *Custom brain (plugin)*: score every (ability × target) pair — expected damage via
   `InteractableHelper.CalculateFinalDamage`, kill-secure, focus-fire low `DEF`, hold defensive abilities until
   allies hurt, punish back-row casters. All state readable from `CombatComponent` / `StatusEffectComponent` /
   `VenueComponent`.
3. *Memory system*: postfix `CombatHelper.PerformAbility` → record `(enemy saw player Y use ability Z)` in a dict.
   Brain reads it: spread vs known AoE, pre-guard vs known burst, target the healer they've seen heal.
   Per-run persistence: hang state on `GameRunData` (EOR's Nemesis system proves the save-piggyback pattern).

**Effort: small→medium plugin.**

## 4. Ask #2 — New classes / traits / class abilities ✅ mostly JSON

- **Class = `Characters.json` entry tagged `PLAYER`** (stats, starting `Things`, `Passives`). EOR runs ~20 custom
  classes this way (`UseExternalJsonCustomClasses`) incl. class-select UI injection — proven tech.
- **Traits** = hidden Things granting Stats+Passives. Gotcha: base traits parse into the `eTraits` enum
  (17 values; used by `CharacterHelper.GiveTrait/RemoveTrait`) → brand-new trait ids need enum injection —
  which EOR already does (22 custom traits). Solved problem.
- **Abilities**: composition is JSON (damage/status/summon/move actions, shapes, focus/charge costs).
  A genuinely novel mechanic = new `SKILL_*` handler = one small patch each
  (hook `CombatHelper._onCombatSkillProc` / `ApplyAction`). Budget one patch per signature mechanic.
- Class switching at runtime already exists: `PartyManagementDirector._pOnChangeClassDelay` /
  `_rebuildCharactertAsNewConfigType`.

## 5. Ask #3 — Bigger battle grid ⚠️ MEDIUM with a shortcut

- Grid = "**Venue**". `eVenueGrids` already has **three layouts: `Standard`, `Extended`, `BossKraken`**
  (`DEFAULT/EXTENDED/KRAKEN_VENUE_TILE_AMOUNT` constants in `VenueHelper`; `CombatState.GridType` selects per fight).
- **Shortcut (small patch)**: force `Extended` for normal fights — bigger battlefield, zero visual work
  (dioramas already load it).
- **True custom 12/16-slot grid (deep)**: slot maps are hardcoded static maps in `VenueHelper`
  (`VenueMap1`, `ExtendedVenueMap1`, `KrakenMap1`; `getSlotRow`, `GetTargetableTiles`, `GetAreaTiles`…) AND each
  3D diorama has physical tile anchors (`Diorama.LoadVenueGrid`, `dDiorama.VenueGrid`). Logic patchable;
  visuals are the hard half.
- **Recommendation**: ship Extended-everywhere first, evaluate feel, then decide.

## 6. Ask #4 — Action-point economy (Divinity/BG3) ⚠️ MEDIUM, highest blast radius — do last

All choke points IL-verified:
- State: `CombatComponent.PrimaryActions` / `.SecondaryActions` (plain ints, serialized). Cost flag:
  `CombatAbilityConfig.IsMajorAction` (data-driven).
- **Grant/reset**: `CombatHelper.ResetCharacterActions` (2 overloads) ← called from `NextTurn`, `ApplyAction`,
  `CombatPhase._addEntityToCombat`, `_processCombatResults`, `_nextTurn`, `_initializeNextWave`, `OnTakeTarotCard`.
- **Gate**: `CombatHelper.IsUsableAbility`, `CombatHelper.IsTurnOver`, `CombatPhase._characterCanUseItem`,
  `InventoryController._isValidInventoryOption`.
- **Spend**: `CombatHelper.PerformAbility`, `CombatPhase._performAbility` (async body `<_performAbility>d__106`).
- **UI**: `CombatAbilitiesTemplateHelper.Show`, end-turn button (`EndTurnButtonViewHelper`), `eSpriteIcons.PA/SA`.

Pooled AP + carryover = ~6–10 patch sites + UI cosmetics. Carryover survives saves (serialized component).
Risks: MP sync (everyone patched), and balance ripple (haste/slow statuses, tactics-style passives, AI assumptions).
**Do after the AI brain — the brain must understand AP anyway.**

## 7. Ask #5 — Pokémon trainer ✅ surprisingly well-supported

- **Summoning is native**: `eCombatActions.ADD_CHARACTER` → `CombatHelper.TryCreateSummon`
  (+ `TryCheckForSummonAvailability`, `TryEndTurnSummons`, `CombatComponent.CanSummon`, combat-end cleanup).
  Summon abilities are JSON-composable today.
- **Build**: Trainer class (JSON) + 3 signature summon abilities → Charmander/Squirtle/Bulbasaur as
  `Characters.json` entries (FIRE/WATER damage types + statuses exist natively; own `Behaviours.json` profiles).
- **Evolution**: trigger `_rebuildCharactertAsNewConfigType` (or the mimic-transform pattern,
  `EncounterPhase._mimicTransformAndStartCombat`) on kill/level thresholds — small patch + counters in plugin state.
- **Assets (honest part)**: no custom-model pipeline. Near-term: proxy models (`IMP`/`ELEMENTAL`/`PLANT` base types)
  + custom PNG icons/portraits. Real models = Unity asset bundles matched to game Unity version, patch prefab
  loading (EOR's `EquipmentVisualHelper.GetITMEquipmentPrefab` patch is the template).
  **Keep Nintendo-asset builds private — not distributable.**

## 8. Class ideas — FTK × Baldur's Gate (all grounded in existing verbs)

| Class | Fantasy | Built from |
|---|---|---|
| Warlock (Hexblade Pact) | Power at a price | `CURSE`/`DEATHMARK`, self-debuff + big `CHANGE_STAT`; "Eldritch Bargain" HP→focus *(small patch)* |
| Paladin Oathkeeper | Smites + auras | Native `AURA` status; `ChargeConfigs` wind-up smite `DAMAGE_PERCENT_200`; oath-break via trait swap |
| Druid Shapeshifter | Wildshape | `_rebuildCharactertAsNewConfigType` into bear/wolf forms, revert on combat end |
| Necromancer | Skeleton horde | `ADD_CHARACTER` (SKELLY_* configs exist), `OnDeathAbility` chains, `SKILL_SUMMONREVENGE` precedent |
| Battle Master | Maneuvers | `MOVE_ENEMY` category + row targeting; Trip/Push/Goad with `TAUNT`/`ENTANGLE` |
| Wild Magic Sorcerer | Chaos surges | Wheel/WheelWedges = ready-made random-effect engine — roll a wedge per cast |
| Monk (Open Hand) | Flurry + stun | Multi-action synergy (pairs with AP economy), `STATUS_STUN`, high SPD/EVD |
| Bard (College of Valor) | Party inspiration | `SUPPORT_GROUP` + `ALLY_ALL`, `PROWESS`/`VIGOR`; saved "verses" as charge stacks |
| Ranger Beastmaster+ | Pet + traps | Followers + `Traps.json` outcome tables repurposed as placeable combat traps *(patch)* |
| Cleric (Life/War domains) | Choice at creation | Two trait packages via variant selection (EOR's Mastery system is this exact pattern — study it) |

## 9. Ask #6 — Quests: the sleeper hit (all JSON)

Quest scripting vocabulary (verified from `Quests\*`, `QuestTemplates.json`, `QuestBoards.json`, `Adventures\*`):
- **Objectives**: `ASSASSINATE_ENTITY, DELIVER, REMOVE_ENCOUNTER, ACTIVATE_ENTITY, REACH_HEX, REACH_RADIUS_LARGE, DUMMY`;
  `CompleteObjectives: ALL|ANY`.
- **World triggers** (on start/engage/complete/end): `DIALOGUE, GIVE_THING, REMOVE_THING_SILENT, CHAOS_ACTIVE,
  NEXT_GAME_STAGE, TOWN_REFRESH, SET_NPC_HOST, REMOVE_ENCOUNTER_PROPERTY`.
- **Chaining/gating**: `NextQuests`, `CombatQuests`, `Requirements`, `Conflicts`, `RoundsToExpire`.
- **Branching choices**: `ChoiceRewards` + `[CHOICE_CANCEL]` / `[ON_VALID_CHOICE_TRIGGER]` tokens firing
  `RESTART_QUEST`/`CLOSE_QUEST` on siblings — reference impl: `STORY_1_6_OFFERING_CHOICE`.
- **World integration**: `MapSpawners.json` (waves, `QUEST_PARENT` hook), `TowerDefenseUnits.json`
  (escort/defend units), dialogue files, camera actions, custom objective texts with `[HIDE_UNTIL:n]` directives.

**Composable quest ideas (no code):** branching investigations (expose vs blackmail → `Conflicts` kills the other
path), escalating sieges (spawner strengthens per ignored day + expiry), traveling escort with scripted ambushes
(success AND failure follow-up quests), rival adventuring party racing your objectives (`RewardEncounters`
spawn conditions + expiry), moral-economy choices (gold-but-Chaos vs refuse-and-Life-Pool).
**With 2–3 new objective/trigger verbs patched in** (`SURVIVE_ROUNDS`, `PROTECT_ENTITY_HP`, `STEALTH_REACH`),
the space roughly doubles.

## 10. Ask #7 — Item crafting / upgrades / rune sockets ✅ with a golden shortcut

1. **Crafting exists** (`CraftConfigs.json` + `CraftingHelper`: `GetCatalystRecipes, IsCraftableRecipe, CraftItem,
   GetCraftingChoices`). Base game uses it only for candy. **Item-level mechanic = zero-code**: author
   `SWORD_X_TIER1/2/3` variants + orb-catalyst recipes between them. Weekend job.
2. **Material tier ladders exist** (`Materials.json`/`ItemMaterials.json`) — retunable/extensible upgrade paths.
3. **Sockets/runes/gems**: no native system, BUT **EOR's affix system is the reference implementation** of
   "crafting on top of dropped items" (20 `OF_*` affixes; `AffixDefinition`, `AffixedItemVariant`). Same
   architecture for sockets: per-item-instance socket state, socket-a-gem interaction
   (patch `InventoryViewHelper.ShowContextMenu` — EOR already does), stats/passives applied via stat-calc
   postfixes (`CharacterHelper.GetStat`, `UIHelper.GetBreakdownStats` — both already EOR patch targets).
   Gems granting abilities works: passives are `SKILL_*` refs; actives via `AbilityBag`.
   **Effort: medium plugin; decompile EOR's affix code as the textbook.**

---

## 11. Tooling

| Tool | Purpose |
|---|---|
| **dnSpyEx** or **ILSpy** | Decompile `FTK2.dll` (find/verify patch targets) and `EnhancedOverhaulRemix.dll` (the best textbook: class/trait injection, save-state persistence, MP sync, affixes) |
| **.NET SDK + Visual Studio/Rider** | Build BepInEx 5 plugins (template: `BepInEx.Templates`), reference `Managed\*.dll` |
| **UnityExplorer** (BepInEx plugin) | Live in-game scene/object inspection |
| **VS Code** | JSON editing with validation (one syntax error in a 4.5MB file is painful) |
| **AssetRipper / AssetStudio** | Only when extracting base-game art/models as reference |
| *(later)* Unity Editor matching game version | Asset bundles for custom models |

Dev QoL first-build: **hot-reload hotkey plugin** calling `ConfigsHelper.ReloadConfigs` — makes all JSON iteration
instant instead of restart-per-tweak.

## 12. Recommended order of attack

1. **JSON-only AI pass** — tune `Behaviours.json` profiles, ability `Tendency`s, `Threat`, ability bags.
   Learn the data by doing.
2. **AI-takeover plugin + memory system** — highest impact, cleanest patch surface (2-method prefix on `AIHelper`).
   Spec first: scoring function, memory schema, difficulty scaling.
3. **Item-level crafting via recipes** — zero code, instant gratification.
4. **Custom class pack + Pokémon trainer** — JSON classes + summon abilities + evolution patch; proxy models + PNG art.
5. **Quest pack** — branching/multi-stage quests in JSON; consider 2–3 new verb patches.
6. **Extended-grid patch** — force `eVenueGrids.Extended` in normal fights; evaluate before attempting custom sizes.
7. **AP economy** — last; touches everything (UI, AI, statuses, MP), and by then the AI brain must understand it anyway.

## 13. Open questions for next session

- Single-player only, or must everything stay MP-safe? (Constrains AI memory + AP economy patch strategy.)
- Build inside/alongside EOR (it already ships class/trait injection + affix plumbing) or as a standalone plugin pack?
- Enemy memory scope: per-battle, per-run, or persistent-across-runs (meta-progression flavor)?
- First concrete deliverable to spec: the AI brain (recommended) or the crafting quick win?
