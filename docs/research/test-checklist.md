# FTK2 test checklist

Status as of 2026-08-24, head `27fcd6d`. `[x]` = driven live through MCP/RPC and the effect **read
back from game state**, not merely "the command returned OK". `[ ]` = not yet exercised.
`[!]` = known broken.

The distinction matters: several commands in this repo reported success while doing nothing
(`crucible_ui_click` dispatched events nobody listened to; the quest pump reported `pumped=True`
while its Task faulted; an ability with `pa=0` reports the ability and target exactly as on success).
A tick here means an observed state change.

---

## Method notes (learned the hard way)

- A visual feature is not verified until a screenshot shows it, and the screenshot must be opened
  and described. State readback and screenshots fail in different directions.
- Never change the measuring instrument and the feature in the same deploy. The snapshot's
  tile-list cap was 30 — below the tile count of even a standard venue — so the list truncated at
  exactly the cap and an enlarged grid was indistinguishable from a normal one.
- A reflection miss inside a per-tick loop is not a silent no-op: godmode looked up a non-existent
  `"MaxHealth"` field every tick and produced 45,432 of 49,219 lines in Player.log, right before the
  game died on a d3d11 out-of-memory.

---

## RESOLVED: combat UI was broken by an unguarded tile-render lookup

    KeyNotFoundException: '(WOLF_CHAOSHOUND_01) - (3, 1) e85cf401…' was not present in the dictionary
      at CombatPhase._clearTileRenderState ()
      at CombatPhase.Initialize (...)

The root cause was **not** a leaked summon persisting into the next fight. `_clearTileRenderState`
indexes `_gameObjectMaps.FromCharacter[occupant]` with **no membership check** — ANY combatant
lacking a 3D model throws `KeyNotFoundException` there and aborts the rest of `Initialize`. That is
why the symptom was a missing UI (no tile grid, no action menu, nothing targetable) rather than a
missing creature: one absent model took the whole combat venue down with it. **Fixed** by building
the missing model first and only removing the combatant if that genuinely fails.

Two contributing bugs found and fixed alongside it:

- `_canvas3D` was cast `as Component`, but it is a `GameObject` (`Initialize` takes
  `GameObject pCanvas3D`), and `GameObject` does not derive from `Component` in Unity — the cast
  yielded null and every actor draw silently reported "canvas not up yet".
- `_gameObjectMaps` is a **property** over `_env.VenueGameObjectMaps`, so reflective **field**
  lookups for it returned null.

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
- [x] `drive.wait_ready()` — polls `interactionEnabled`, the field the game itself consults
- [ ] `ftk2_new_game` — start a run from scratch (never exercised)
- [ ] `ftk2_pin_seed` — determinism control
- [ ] `ftk2_compare_state` — two-instance digest compare (**blocked**: game is single-instance)
- [ ] `ftk2_read_trace`
- [ ] `ftk2_wait_screen` / `ftk2_screen`
- [ ] `ftk2_list_instances`

## B. Harness — run lifecycle

- [x] `crucible_tutorials_suppress` — 85 ids, `TutorialEnabled` off
- [x] `crucible_run_status` — quests + the pump's fault AND stack trace
- [x] `crucible_quest_complete_objective` — by id, flagging **all duplicates**
- [x] `crucible_quest_activate` — inject any quest with the ACTIVE map id
- [x] `crucible_quest_remove` — drop quests so they stop re-queuing rewards
- [x] `crucible_encounters_clear` — 60 interrupting encounters removed
- [x] `crucible_endadventure_watch` — VICTORY latched + save snapshotted
- [x] `crucible_summary_dismiss` — pages ADVENTURE COMPLETE → PLAYER SUMMARY
- [x] `crucible_party_gain_xp` — level 0→3 read back
- [x] `crucible_encounter_leave` — invoked clean
- [x] `crucible_get` indexer — `ActiveQuests[0].Data.ID` + out-of-range negative control
- [x] **Full run to victory through the game's own quest chain**
- [ ] Verify a **DEFEAT** latches too (only VICTORY proven)
- [!] Loading a fixture saved mid-combat does **not** resume combat: `CoreHelper.LoadIntoGameRun`
      routes on `DungeonState` only and never inspects `CombatState`, so the combat blob rides along
      in the save as orphaned data.
- [ ] The 13 new `ftk2_*` wrappers — the MCP server reads its tool list from a static array at
      startup, so new `ftk2_*` tools need a server reload before they're callable.

## C. Overworld

- [x] `ftk2_reveal_map` — 5850 hexes
- [x] `crucible_move` + traversal defaults (no ambush, no encounter menu)
- [x] `crucible_path_preview` / `crucible_hex_info`
- [x] `ftk2_set_hex` — teleport, and party gather
- [x] `crucible_interact VENUE` → combat
- [x] `crucible_overworld_end_turn`
- [x] `ftk2_map_encounters`
- [x] `ftk2_time_advance` — was reading the `[Obsolete]` `AdventureState.CurrentTimeOfDayIndex`
      alias, which the game no longer writes. Now reads `AdventureState.MapState.*`.
- [ ] `crucible_interact` on non-combat venues (town, market, quest board)
- [ ] Dungeon entry / `DungeonState.LoopState`

## D. Combat

- [x] `crucible_combat_snapshot` — combatants, hp, tiles, turn order; now also prints each
      combatant's statuses
- [x] `crucible_list_abilities` (by guid)
- [x] `crucible_list_targets` — was throwing "Number of parameters specified does not match the
      expected number" on every call, because `InteractableHelper.GetAbilityConfig` takes
      `(string, bool)` and was invoked with one argument. Fixed.
- [x] `crucible_use_ability` — **measured 7→6 damage**
- [x] `crucible_win_combat` (EndPhase) — 4 combatants → 1
- [x] `ftk2_heal_party` — **0→60 and 0→48; it also REVIVES the dead**
- [x] `ftk2_spawn` / `crucible_debug_spawn` — 2332 enemies, 170 encounters
- [x] `crucible_combat_restore_actions` — refills `pa`/`sa`
- [x] `crucible_combat_wipe_enemies`
- [x] `crucible_godmode`
- [x] `crucible_combat_end_turn` — works; the active character walks the initiative order. It
      appeared broken only because the whole board was dead at the time.
- [x] `ftk2_kill_all` — was invoking **nothing**: it looked for one-argument
      `TryKillCharacter`/`KillCharacter` overloads that do not exist (they take six and three
      parameters). Now binds `KillCharacter` properly and skips tile entities.
      Measured `attempted=8 deadAfter=8`.
- [!] `crucible_force_combat` — routes to COMBAT without initialising the phase; camera ends up under
      the map. Only a restart recovers. **Do not use** — spawn + the Fight button instead.
- [ ] `crucible_use_ability_auto`
- [ ] `ftk2_end_phase` / `ftk2_debug_end_phase`
- [ ] **Losing** a fight → party wipe → DEFEAT path
- [ ] Multi-wave combat
- [x] Status effects applied in combat and read back (`state.v2` statuses)
- [ ] Status **duration decay** — unverified. The status applies and reads back, but decay ticks at
      the OWNER's turn start and only when the status config's `TickCombat` is true; a clean
      measurement has not been taken.

## E. Items, gear, stats

- [x] `crucible_equip` — starter weapons, 30/31 classes
- [x] `ftk2_give` / `crucible_give_item` — item lands in inventory, measured things `9→10`
- [x] Equipping changes **abilities** (abilities come from the WEAPON, not the class) — swapping to
      `ARM_ASHEN_CINDERROD` gained `WAND_FIRE_ATTACK` / `MAGIC_PULL_ATTACK` / `MAGIC_STUN_ATTACK` /
      `MAGIC_CONFUSE_ONLY_SPLASH_ATTACK` and lost both `BLADE_*` abilities
- [x] `crucible_set_stat_value` — stat write read back, measured `79→95`
- [x] `crucible_thing_config` — accepts a trailing `*` to list matching ids
- [x] `ftk2_set_level` — was calling `CharacterHelper.TryProgressCharacterEntityToLevel`, which swaps
      an ENEMY's config tier and does nothing for a player. Now grants XP via
      `ProgressionHelper.EntityGainXP`. Measured xp `300→455`, level `3→4`.
- [x] Armory items resolve in-game (533 items, config-folder install)
- [ ] Inverse items (negative stats) — designed, not built

## F. Chaos / map effects

- [x] `ftk2_chaos_state` — was reading the `[Obsolete]` `GameRunData.ChaosState` /
      `GameRunData.RoundCount` aliases, which the game no longer writes. Now reads
      `AdventureState.MapState.*`.
- [ ] `ftk2_chaos_freeze` — pin chaos
- [ ] Chaos stage advance → gated quests promote (this is what stalled the quest chain)
- [ ] Encounter modifiers pack (`CF_PACK_ENCOUNTER_MODIFIERS`) fires in a real fight

## G. Classes

### CF_PACK_ORIGINALS — the first classes that are actually ours

- [x] **Vampiric / Pacifist / Beast Trainer** authored, PackCheck zero errors
- [x] Balanced into the MEASURED EOR band (432–474): **470 / 466 / 462**
- [x] All three resolve live: config, passives, starter weapon, abilities
- [x] Localization: 22 keys resolve in the game's own `Lang.__translations`
- [x] Party Summary renders every skill by name (no raw-key fallbacks)
- [x] `DAMAGE_DEALT_PCT` value source + tests
- [x] Pacifist's weapon confirmed to carry NO damaging ability
- [x] **Traits asserted FIRING in live combat** (below)
- [!] Beast Trainer's three Send Out abilities exist on the weapon and resolve, but never appear in
      the in-combat action menu and report `usable=False`. Tile capacity was **ruled out** (7 free
      ally tiles). Cause not yet known.
- [ ] Engorged / Bat Swarm / Field Medic / Why Can't We Be Friends — not yet isolated
- [ ] Three elemental partners (Chaoshound / Hellhound at Bond 3 / Serpent at Bond 6) — authored and
      validated; only the first is verified firing
- [ ] `visualfallbacks.json` added but not visually confirmed
- [x] 31 `CF_EOR_*` classes: config, passives, class applied, weapon, abilities, loc — **30/31**
- [ ] Re-run the sweep post-merge for the Runemage weapon fix (31/31)

### Verified FIRING in live combat

    proc SKILL_CF_VAMPIRIC_BLOOD_PRICE   (BloodPrice)   owner=bb212c3e actions=1
    proc SKILL_CF_VAMPIRIC_CRIMSON_DRAIN (CrimsonDrain) owner=bb212c3e actions=1
    proc SKILL_CF_TRAINER_FIRST_PARTNER  (FirstPartner) owner=333e7dad actions=1

- [x] **Blood Price** — HP delta exactly **−3**
- [x] **Crimson Drain** — net **−1 = −3 cost + 2 drained** (25% of 7 damage)
- [x] **First Partner** — a Chaos Wolf appears as an **ALLY on our side**, facing the enemy, animating
- [x] **Pacifist kit** — no damaging ability; all four `ONLY_*` abilities present

A successful effect logs NOTHING unless `VerboseLogging` is on — only failures log. Requiring a log
line marked a working Blood Price as broken. `ftk2mods.classforge.cfg` now has it enabled.

Evidence: `artifacts/screenshots/originals-classes/`, `artifacts/trait-assertions.json`.

### RESOLVED: recipe effects were dying inside the native pipeline

1. **Every recipe `STAT_CHANGE` threw.** `InteractableHelper.ApplyStatChange` ends with an
   unconditional `abilityConfig.Actions.Where(...)`, and effects were attributed to a synthetic
   `CF_RECIPE_<id>` name that exists in no config. Nothing caught it because every shipped
   `STAT_CHANGE` targets `FOC`, which reaches the same line — the path had never run in a real fight.
   **Fix:** register one real `CF_RECIPE_EFFECT` config with empty `Actions`.
2. **HP sign is INVERTED** — positive is DAMAGE, negative is HEAL. All four HP recipes were backwards.
3. **`SUMMON` needs a TILE entity, not a character** — the native path reads
   `pTarget.Get<VenueTileComponent>()` unconditionally. `ExecSummon` was computing
   `UseTargetPosition` and ignoring it.

## H. Summoning — what it actually takes

Adding a combatant is **nine calls across two layers**, with no enforced contract. Each omission
fails differently and silently; every one below was hit and fixed in turn:

| Omission | Symptom |
|---|---|
| `SetInitiative` | turn order fills with entities that can never act — **WEDGES the fight** |
| `ResetCharacterActions` | joins with `pa=0`, so it can never act |
| `CreateActorGameObject` | real, takes turns, **invisible** — `FromCharacter` has no entry |
| `positionActorGameObject` | model built at the prefab default spot, not its tile |
| `_allyEntities` / `_enemyEntities` | **cannot be clicked, hovered or targeted** — the phase keeps its own rosters and targeting reads those, not `CombatState` |
| `CharacterLookAtTarget` | faces the wrong way |
| idle animation | stands frozen |

Hard-won specifics:

- **Allegiance comes from the TILE.** `TryCreateSummon` copies the tile's `GroupIndex` onto the new
  character, so summoning onto a slain enemy's tile produces a **hostile "ally"**.
- **`ApplyAction(ADD_CHARACTER)` ignores your tile** — it recomputes from the target and does
  row-based placement, which **stacks** creatures. `TryCreateSummon` honours `pPos` exactly
  (`TilePosition = pPos`, `OccupiedTiles.Add(pPos)`, `SetTilePosition`). Use it directly.
- **`CHARACTER_ADDED_SMOKE` does not create an actor** — it looks one up and activates it.
- **`CharacterLookAtTarget`'s parameter is `pTileTarget`** — it wants a tile, not a character.
- **`VenueViewHelper.LoadCharacterEntitiesToVenueGrid` draws correctly but RE-LAYS-OUT the venue**
  when handed the full tile list. Using it mid-fight broke the combat grid. **Reverted** — build the
  single actor instead.
- `positionActorGameObject` is a **local function** reflection cannot reach; its maths reads
  `VenueComponent.OccupiedTiles`, and an empty list averages ZERO tiles and drops the model at the
  diorama origin. Its two helpers are overloaded, so name-only lookup throws `AmbiguousMatchException`.

**The canonical pattern**, from the skeleton book — `MAGIC_SUMMON_UNDEAD_ATTACK` declares
`Target: ALLY_ALL` + `TileOccupancy: EMPTY`, letting the game find a free ally tile itself. And
`SKILL_CF_NECRO_RAISE_ON_KILL` was already in the repo using `ON_KILL` + `TRIGGER_TARGET_POSITION`.
**Both were sitting there before any of this was reverse-engineered.**

FTK2.Summoner was checked: **data-only**, 350 lines of config merging, no runtime summon code.

## I. Offline suites

- [x] Crucible.Core 329 · DevKit 117 · ClassForge Core 44 · **Recipes 333**
- [x] PackCheck zero errors
- [x] New validator rules: `E_SUMMON_TARGET`, `E_VALUE_SOURCE_SCOPE`, `E_VALUE_SOURCE_NO_PERCENT`
- [ ] LiveDataHarness (offline config validation) — exists in docs, not run

## J. Combat grid — arena sizing

- [x] Arena size is moddable. A tile map is a rectangle of characters: `CharacterHelper.GetGroupIndex(c)`
      is `c - ('a' or 'A')`, so `A`/`a` is group 0 and `B`/`b` is group 1; uppercase is the BACK row,
      lowercase the FRONT row; any non-letter is a neutral tile with group `-1`.
- [x] The stock row `|..Aa.bB..|` gives each side exactly one back and one front column, so a
      TWO_BY_TWO creature has no 2x2 block of same-group tiles anywhere on a standard board.
- [x] Presets ship behind `[Combat] VenueGridPreset` (off / extended / large / huge). Measured:
      preset `large` produced 120 tiles and 24 tiles per side against the stock 8.
- [x] The step that made it actually work: new tiles must be given the render pass `Initialize`
      already ran on the old ones (`RenderVenueTile(..., TileRender.Hidden)`), otherwise they exist
      and are active but are neither drawn nor interactive.
- [ ] Camera framing for the larger arena — not verified.
- [ ] Enemy AI placement across a wider board — not verified.

---

## Ordering for autopilot

1. **Status duration decay** — needs a clean measurement: apply a status with `TickCombat: true`,
   end the OWNER's turn, and confirm the duration counts down in the snapshot.
2. **Beast Trainer Send Out abilities** — resolve why a usable, tile-eligible ability reports
   `usable=False` and never reaches the action menu.
3. **Fixture mid-combat resume** — teach `CoreHelper.LoadIntoGameRun` to route on `CombatState` when
   present, not just `DungeonState`.
4. **Remaining G traits** — Engorged, Bat Swarm, Field Medic, Why Can't We Be Friends, and the
   Bond-3 / Bond-6 partners. Each needs a specific trigger condition the harness can now reach.
5. **F — chaos**, since chaos staging is what gates the quest chain.
6. **J — camera framing and enemy AI placement** on enlarged arenas.
7. Reload the MCP server so the 13 new `ftk2_*` tools are callable directly, then re-run the class
   sweep for 31/31 and the cheap A/C leftovers.

## Fast test bed

    python FTK2.Crucible/tools/encounter.py --active CF_ORIG_TRAINER --godmode \
      --allies WOLF_CHAOSHOUND_01:2 --enemies BEE_WORKER_01:3

Fixture `4c0f1f9f-20c8-4bd3-9662-c445f48f677e` — **0 quests, 60 interrupting encounters removed, map
revealed**. `to_combat.py` defaults to it. This matters for speed as well as noise: a quest flagged
complete but never resolved re-queues its reward prompt on EVERY load and holds interaction off
while the overworld looks completely normal.

## Standing constraints

- Never press `continue-btn` / `load-btn` / `load-game-btn` — those resume or load Ben's saves.
- Never Steam-verify the install (wipes EOR content the co-op saves need).
- Fixture writes always use a fresh run id; the 20 originals in
  `C:\Users\ben\Backups\ftk2-2026-08-23\` stay byte-identical.
- Local commits only, and only when Ben asks. `origin` is not Ben's fork.
