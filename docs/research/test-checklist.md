# FTK2 test checklist

Status as of 2026-08-24. `[x]` = driven live through MCP/RPC and the effect **read back from game
state**, not merely "the command returned OK". `[ ]` = not yet exercised. `[!]` = known broken.

The distinction matters: several commands in this repo reported success while doing nothing
(`crucible_ui_click` dispatched events nobody listened to; the quest pump reported `pumped=True`
while its Task faulted). A tick here means an observed state change.

---

## A. Harness — session control

- [x] `ftk2_health` / pump alive
- [x] `ftk2_boot_to_run` — cold boot to a loaded overworld
- [x] `ftk2_saves` / `ftk2_refresh_saves` — startup cache re-scanned
- [x] `ftk2_dialogs` — enumerate blockers
- [x] `ftk2_clear_gates` — post-load gate, story pages, reward prompts
- [x] `ftk2_screenshot` — returns image
- [x] `ftk2_state` (v2) — route/run/combat snapshot
- [x] `ftk2_exec` — arbitrary command
- [ ] `ftk2_new_game` — start a run from scratch (never exercised)
- [ ] `ftk2_pin_seed` — determinism control
- [ ] `ftk2_compare_state` — two-instance digest compare (**blocked**: game is single-instance)
- [ ] `ftk2_read_trace`
- [ ] `ftk2_wait_screen` / `ftk2_screen`
- [ ] `ftk2_list_instances`

## B. Harness — run lifecycle *(built today)*

- [x] `crucible_tutorials_suppress` — 85 ids, `TutorialEnabled` off
- [x] `crucible_run_status` — quests + pump fault/stack
- [x] `crucible_quest_complete_objective` — by id, **all duplicates**
- [x] `crucible_quest_activate` — WIN quest injected with correct MapID
- [x] `crucible_endadventure_watch` — VICTORY latched + save snapshotted
- [x] `crucible_summary_dismiss` — pages ADVENTURE COMPLETE → PLAYER SUMMARY
- [x] `crucible_party_gain_xp` — level 0→3 read back
- [x] `crucible_encounter_leave` — invoked clean
- [x] `crucible_get` indexer — `ActiveQuests[0].Data.ID` + out-of-range control
- [x] **Full run to victory through the game's own quest chain**
- [ ] Verify a **DEFEAT** latches too (only VICTORY proven)
- [ ] `ftk2_*` wrappers for the 8 new commands — written, **need MCP server reload**

## C. Overworld

- [x] `ftk2_reveal_map` — 5850 hexes
- [x] `crucible_move` + traversal defaults (no ambush, no encounter menu)
- [x] `crucible_path_preview` / `crucible_hex_info`
- [x] `ftk2_set_hex` — teleport
- [x] `crucible_interact VENUE` → combat
- [x] `crucible_overworld_end_turn`
- [x] `ftk2_map_encounters`
- [ ] `ftk2_time_advance` — day/night
- [ ] `crucible_interact` on non-combat venues (town, market, quest board)
- [ ] Dungeon entry / `DungeonState.LoopState`

## D. Combat — "does fighting work"

- [x] `crucible_force_combat` / `ftk2_spawn` (2332 enemies, 170 encounters)
- [x] `crucible_combat_snapshot` — combatants, hp
- [x] `crucible_list_abilities` / `crucible_list_targets`
- [x] `crucible_use_ability` — **measured 7→6 damage**
- [x] `crucible_win_combat` (EndPhase) — 4 combatants → 1
- [ ] `crucible_use_ability_auto`
- [ ] `crucible_combat_end_turn`
- [ ] `ftk2_kill_all` — the godmode "delete the enemies" verb
- [ ] `ftk2_heal_party` / `ftk2_health` — the godmode "don't die" verb
- [ ] `ftk2_end_phase` / `ftk2_debug_end_phase`
- [ ] **Losing** a fight → party wipe → DEFEAT path
- [ ] Multi-wave combat
- [ ] Status effects applied in combat and read back (`state.v2` statuses)

## E. Items, gear, stats

- [ ] `ftk2_give` / `crucible_give_item` — item lands in inventory
- [x] `crucible_equip` — starter weapons, 30/31 classes
- [ ] Equipping changes **abilities** (abilities come from the WEAPON, not the class)
- [ ] `crucible_set_stat_value` — stat write read back
- [ ] `crucible_status_add` — status applied, duration ticks down
- [ ] `ftk2_set_level`
- [ ] Armory items resolve in-game (533 items, config-folder install)
- [ ] Inverse items (negative stats) — designed, not built

## F. Chaos / map effects

- [ ] `ftk2_chaos_state` — read chaos level
- [ ] `ftk2_chaos_freeze` — pin chaos
- [ ] Chaos stage advance → gated quests promote (this is what stalled the quest chain)
- [ ] Encounter modifiers pack (`CF_PACK_ENCOUNTER_MODIFIERS`) fires in a real fight

## G. Classes

### CF_PACK_ORIGINALS — the first classes that are actually ours

- [x] **Vampiric / Pacifist / Beast Trainer** authored, PackCheck zero errors
- [x] Balanced into the MEASURED EOR band (432-474): 470 / 466 / 462
- [x] All three resolve live: config, passives, starter weapon, abilities
- [x] Localization: 22 keys resolve in the game's own `Lang.__translations`
- [x] Party Summary renders every skill by name (no raw-key fallbacks)
- [x] `DAMAGE_DEALT_PCT` value source + 11 tests (330 total green)
- [x] Pacifist's weapon confirmed to carry NO damaging ability
- [ ] **Traits asserted firing in combat** -- see the blocker below
- [ ] `visualfallbacks.json` added but not yet visually confirmed

### RESOLVED: recipe effects were dying inside the native pipeline

Three separate bugs, all fixed, all found by driving the game rather than by reading code:

1. **Every recipe `STAT_CHANGE` threw.** `InteractableHelper.ApplyStatChange` ends with an
   unconditional `abilityConfig.Actions.Where(...)`, and recipe effects were attributed to a
   synthetic `CF_RECIPE_<id>` ability name that exists in no config, so `GetAbilityConfig` returned
   null. Nothing caught it because every shipped `STAT_CHANGE` in `CF_PACK_EOR_CLASSES` targets
   `FOC`, and FOC reaches the same line -- it had simply never been exercised in a live fight.
   **Fix:** register one real `CF_RECIPE_EFFECT` config with empty `Actions` and route every verb
   through it.
2. **HP sign is inverted.** `statDeltaValue > 0` is DAMAGE, `< 0` is HEAL. All four HP recipes were
   authored backwards. **Fixed in data.**
3. **`SUMMON` needs a TILE entity, not a character.** The native path reads
   `pTarget.Get<VenueTileComponent>().GroupIndex` unconditionally; a character carries a
   `VenueComponent` instead, so it threw before `TryCreateSummon` was reached. `ExecSummon` was
   computing `UseTargetPosition` and then ignoring it. **Fix:** resolve the tile entity standing at
   the target's position.

### Verified FIRING in live combat

    proc SKILL_CF_VAMPIRIC_BLOOD_PRICE   (BloodPrice)   owner=bb212c3e actions=1
    proc SKILL_CF_VAMPIRIC_CRIMSON_DRAIN (CrimsonDrain) owner=bb212c3e actions=1
    proc SKILL_CF_TRAINER_FIRST_PARTNER  (FirstPartner) owner=333e7dad actions=1

- [x] **Blood Price** -- procs, no exception, HP delta exactly **-3**
- [x] **Crimson Drain** -- procs, no exception; net delta **-1 = -3 cost + 2 drained** (25% of 7)
- [x] **First Partner** -- procs and a **Chaos Wolf appears on the felled enemy's tile**
- [x] **Pacifist kit** -- no damaging ability; all four `ONLY_*` abilities present
- [ ] Engorged / Bat Swarm / Field Medic / Why Can't We Be Friends -- not yet isolated

Evidence: `artifacts/screenshots/originals-classes/`, `artifacts/trait-assertions.json`.

### Caught OFFLINE now, so it never costs a play session again

New validator rules (**333 tests green**):

- `E_SUMMON_TRIGGER_SCOPE` -- `SUMMON` only places under `ON_KILL`, because ADD_CHARACTER writes to
  the target's exact tile and bails when a living entity is on it. The freed tile of something that
  just died is the only reliable spot.
- `E_SUMMON_TARGET` -- must be `TRIGGER_TARGET_POSITION`; `SELF` is the caster's own (occupied) tile.
- `E_VALUE_SOURCE_SCOPE` / `E_VALUE_SOURCE_NO_PERCENT` for `DAMAGE_DEALT_PCT`.

**The lesson, in Ben's words: reuse what is already there.** `SKILL_CF_NECRO_RAISE_ON_KILL` was
sitting in the repo using exactly `ON_KILL` + `TRIGGER_TARGET_POSITION`. Copying that shape would
have skipped the entire investigation. The validator rules now encode it.

### Also broken / discovered

- [!] `crucible_kill_all` does nothing in combat -- it walks `GameRunData.Entities` while the fight
      runs on `CombatState.Entities`. Replaced by `crucible_combat_wipe_enemies`.
- [!] `crucible_force_combat` routes to COMBAT without initialising the phase: camera ends up under
      the map with no scene. Only a restart recovers. **Do not use** -- spawn + Fight instead.
- [!] Interacting with an encounter the party is STANDING ON throws `KeyNotFoundException`.
- [!] An ability with **no actions left** reports success and does nothing (`pa=0`).
      `crucible_combat_restore_actions` refills.
- [!] Only the ACTIVE character walks into an encounter, and only whoever stands on it joins the
      fight. Teleporting the party afterwards does NOT add them -- use `to_combat.py --active`.
- [x] `crucible_godmode`, `crucible_combat_wipe_enemies`, `crucible_combat_restore_actions`,
      `crucible_loc`, `drive.wait_ready()`, `to_combat.py`, `assert_traits.py`.
- [!] **My own regression, caught by Ben:** I switched `clear_gates` from focus+Enter to
      `crucible_ui_click`. `UIToolkitHelper.Submit` returns true on `continue-label` and advances
      nothing -- 48 "successful" presses left the gate up. Focus+Enter clears it in two. Reverted.
- [!] **Loop detection:** `wait_for_turn` used to spin when the active character had `pa=0`
      (auto-play no-ops, turn never ends). It now tracks the active guid, forces an end-turn after
      2 stalls, and gives up with a reason after 5.

- [x] 31 `CF_EOR_*` classes: config present, passives, class applied, weapon, abilities, loc — **30/31**
- [ ] Re-run the sweep post-merge for the Runemage weapon fix (31/31)
- [ ] Per-trait in-combat assertion (fire the trigger, read the effect back)

## H. Offline suites

- [x] Crucible.Core 329 · DevKit 117 · ClassForge Core 44 · Recipes 319
- [x] PackCheck zero errors
- [ ] LiveDataHarness (offline config validation) — exists in docs, not run this session

---

## Ordering for autopilot

1. **Reload the MCP server** so the 8 new `ftk2_*` tools are callable as tools rather than through
   `ftk2_exec`.
2. **D + E in one combat fixture** — the godmode verbs and the item/stat/status verbs are the
   largest untested block and they share a setup: force a fight, then exercise kill/heal/give/
   equip/stat/status and read each one back.
3. **F** — chaos, since chaos staging is what gates the quest chain; proving it may remove the need
   for `crucible_quest_activate` in normal runs.
4. **G — Vampiric first**, as the first class that is actually ours: HP-cost attacks, flat drain
   (percentage needs the new primitive), Engorged temp-max-HP, bat summon on gorge.
5. Re-run the class sweep for 31/31.
6. The remaining A/C items, which are cheap.

## Standing constraints

- Never press `continue-btn` or `load-btn` / `load-game-btn` — those resume or load Ben's saves.
- Never Steam-verify the install (wipes EOR content the co-op saves need).
- Fixture writes always use a fresh run id; the 20 originals in `C:\Users\ben\Backups\ftk2-2026-08-23\`
  stay byte-identical.
- Local commits only, and only when Ben asks. `origin` is not Ben's fork.

## I. Combat spawning + godmode (built this session)

- [x] `ftk2_godmode` -- per-tick party top-up. Damage still LANDS so `ON_DAMAGE_TAKEN` still fires
      and reflect traits stay observable. Not invulnerability, deliberately.
- [x] `ftk2_combat_wipe_enemies` -- replaces `ftk2_kill_all`, which does nothing in combat.
- [x] `ftk2_combat_restore_actions` -- an ability with `pa=0` reports success and does nothing.
- [x] `ftk2_combat_spawn` -- adds combatants to the CURRENT fight (needed to farm Bond; the shipped
      encounter had one spider).
- [x] `ftk2_loc` -- assert localization against the game's own `Lang.__translations`.
- **53 MCP tools total.** The 13 added this session need an MCP server reload to appear as tools;
  until then they are reachable through `ftk2_exec`.

### Why building an encounter is hard (and the right way to do it)

The SIMULATION is easy and is already correct: `crucible_combat_spawn` produces combatants with the
right tile, group, initiative, actions and turn order, and the snapshot proves it every time.

What is hard is the PRESENTATION, because FTK2 splits one logical act -- "a creature joins the
fight" -- across two layers with no enforced contract between them, and the presentation half is
reachable only from inside `CombatPhase`'s own awaited ability-resolution flow. Tried in order, all
measured 2026-08-24:

1. `CombatState.Entities.Add` alone -> invisible combatants that WEDGE the turn order.
2. Add `SetInitiative` + `ResetCharacterActions` -> fight works, still invisible.
3. Add `CharacterVisualHelper.CreateActorGameObject` -> a model appears, at the prefab default spot.
4. Re-apply `VenueHelper.SetTilePosition` after the model exists -> some models move, not all.
5. Route the whole thing through the game's own `ApplyAction(ADD_CHARACTER)` verb -> still not drawn.
6. Hand the emitted results to `CharacterVisualHelper.RenderAbilityResults` -> still not drawn.

The remaining piece is that the phase interleaves camera work, animation and awaits around the
render call; reproducing the call without the surrounding sequence does not reproduce the effect.

**So do not inject into a running fight.** Mid-combat insertion is something the game itself only
does through tightly-scripted boss phases. The supported path is to build the encounter BEFORE the
fight and let the game construct it, which already works perfectly through this harness:

    crucible_debug_spawn enemies <ENEMY_ID>   -> opens the encounter menu
    crucible_ui_click Fight                   -> the game builds the combat

That route produced a 74-combatant fight with every model drawn correctly, because the game did the
construction. 2332 enemies and 170 encounters are reachable that way, and `MapSpawners.json` is the
data surface for authoring new ones.

`crucible_combat_spawn` therefore stays as a SIMULATION-ONLY test verb: correct for asserting
mechanics against `crucible_combat_snapshot`, not for anything judged on screen.

### RESOLVED: spawned combatants now render

The answer was a single public method the game already exposes:

    VenueViewHelper.LoadCharacterEntitiesToVenueGrid(
        parent, characters, gameObjectMaps, allTiles, diorama, random, ...)

It creates the actor GameObject, registers it in `gameObjectMaps.FromCharacter`, computes the
tile-average world position including the diorama's PlayerOffset, sets the transform and starts the
idle animation. It is what the venue uses to populate the grid at the start of a fight -- which is
exactly why everything present at the start renders and nothing added afterwards did.

**Confirmed live:** `FromCharacter` went 5 -> 8 and the bee models appear in the enemy zone.

Everything tried before it failed for an instructive reason, and each cost a cycle:

| Attempt | Result |
|---|---|
| `CombatState.Entities.Add` alone | invisible, and WEDGES the turn order |
| `+ SetInitiative` / `ResetCharacterActions` | fight works, still invisible |
| `+ CreateActorGameObject` | model appears at the prefab default spot |
| `+ SetTilePosition` after creation | some models move, not all |
| Route through `ApplyAction(ADD_CHARACTER)` | still invisible -- see below |
| `+ RenderAbilityResults` | still invisible |
| `LoadCharacterEntitiesToVenueGrid` | **works** |

The decisive clue came from reading the renderer rather than guessing:

    case eAbilityResults.CHARACTER_ADDED_SMOKE:
        ActorGameObjectBase originActor = pActorGameObjects[entity];   // LOOKS UP, does not create
        originActor.gameObject.SetActive(true);

`CHARACTER_ADDED_SMOKE` **activates** an actor that must already exist. Nothing in the ADD_CHARACTER
path creates one, so the "route it through the game's own verb" theory was wrong on its own.

Reproducing the placement by hand also failed three ways, all worth remembering: the positioning
logic lives in a LOCAL FUNCTION reflection cannot reach; it reads `VenueComponent.OccupiedTiles`,
which only the game's summon path fills (an empty list averages ZERO tiles and drops the model at
the diorama origin); and its two helpers are overloaded, so a name-only lookup throws
`AmbiguousMatchException`.

**The lesson, again:** the entry point already existed. Finding the public method that owns the
whole job beat reassembling its internals every time.

### [ ] Remaining: spawn placement stacks

Spawned creatures land on one tile rather than the requested one -- `TryCreateSummon` does its own
row-based placement from the target tile's `RowPositionsType` and ignores the exact position. They
render, act and can be attacked; they just cluster. Cosmetic, and separate from the rendering fix.

The combat tile GRID not being drawn is NOT a defect: 66 tile GameObjects stay registered, and FTK2
only draws the grid during targeting.
