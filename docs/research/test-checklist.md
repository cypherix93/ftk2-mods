# FTK2 test checklist

Status as of 2026-08-24, head `27fcd6d`. `[x]` = driven live through MCP/RPC and the effect **read
back from game state**, not merely "the command returned OK". `[ ]` = not yet exercised.
`[!]` = known broken.

The distinction matters: several commands in this repo reported success while doing nothing
(`crucible_ui_click` dispatched events nobody listened to; the quest pump reported `pumped=True`
while its Task faulted; an ability with `pa=0` reports the ability and target exactly as on success).
A tick here means an observed state change.

---

## THE VISUAL GATE — read this before marking ANYTHING done

**A feature that exists in state and is invisible to the player does not work.**

Full protocol, with the per-feature-type table and the measurement hazards:
**`docs/research/visual-verification-protocol.md`**

No item in this checklist may be marked `[x]` until all five steps pass:
1. State readback (necessary, never sufficient)
2. **Name the on-screen element IN ADVANCE** — which number, icon, model or tile must change
3. Screenshot it (dedicated `/screenshot` route, not an `/exec` command)
4. **OPEN the screenshot and describe what is in it**, including whether (2) is present
5. Reconcile — if state and screen disagree, **the screen wins and it is not done**

Four features this session were reported working on state alone and were invisible on screen: the
summoned wolf, the wide grid, the diorama rotation, and Engorged. The owner caught all four.

**Settings that destroy a measurement:** godmode ON (masks every HP effect), `BepInEx.cfg`
`LogLevels` without `Debug` (drops every recipe `proc` line), `restore_actions` before `end_turn`,
measuring during a loading screen, and wiping enemies to isolate an effect (that ends the fight).

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
- [x] The 13 new `ftk2_*` wrappers — **DONE 2026-08-25.** Added to the static `TOOLS` array in
      `FTK2.Crucible/mcp/server.js` (+ matching `callTool()` switch cases), in the file's existing
      description/inputSchema style: combat_snapshot, list_abilities, list_targets, use_ability,
      use_ability_auto, win_combat, combat_end_turn, kill_target, equip, give_item, set_stat_value,
      thing_config, chaos_advance.
      **PREMISE CORRECTION:** `ftk2_exec` is a GENERIC PASSTHROUGH with no whitelist, so every
      `crucible_*` command was ALREADY reachable over MCP. This item was about typed discoverability,
      not a hard block — the old wording overstated it.
      `crucible_force_combat` deliberately NOT wrapped: the checklist marks it broken (routes to
      COMBAT without initialising the phase; camera under the map). Wrapping it would invite misuse.
      Measured: 88 `crucible_*` commands registered in C#, 54 now referenced in server.js, **0 dead
      wrappers**. The other 34 are deliberately unexposed (raw input simulation, UI-nav primitives,
      reflection escape hatches) and are listed in the agent's report.
      Anti-drift: `FTK2.Crucible/mcp/check-tool-drift.js` — a human-run static diff of both lists.
      ⚠️ **STALE DEPLOY ARTIFACT FOUND:** `tools/out/deploy/payload/plugins/ftk2mods.crucible/mcp/server.js`
      already differed from source BEFORE this change. The deployed MCP server is behind the repo.
      Needs `tools/deploy.ps1 -StageOnly -SkipBuild` before any of this reaches a live server.
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
- [x] `crucible_use_ability_auto` — exercised heavily across the trait battery
- [ ] `ftk2_end_phase` / `ftk2_debug_end_phase`
- [ ] **Losing** a fight → party wipe → DEFEAT path
- [ ] Multi-wave combat
- [x] Status effects applied in combat and read back (`state.v2` statuses)
- [x] Status **duration decay** — **VERIFIED 2026-08-26** in a live fight, read across round
      boundaries on `crucible_combat_snapshot`: `GOLEM_ICE_02` went
      `STATUS_ACID_00(d=4) -> (d=3)` and `STATUS_FIRE_00(d=2) -> (d=1)`, and a `BAT_CAVE_00` went
      `STATUS_ACID_00(d=4) -> (d=3)`, in the same tick. Decay ticks at the OWNER's turn start, so
      durations on different combatants step at different moments -- read one owner across rounds,
      not the whole board at one instant, or it looks inconsistent.

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
- [x] Inverse items (negative stats) — 3 items authored in CF_PACK_ORIGINALS/items.json
      (ARM_ORIG_INVERSE_CURSED_MILLSTONE, _GAMBLERS_LOCKET, _BLOODRAGE_TALISMAN), with
      visualfallbacks + localization entries; test module FTK2.Crucible/tools/inverse_items.py
      written but NOT yet run against a live game (game was in use this session)

## F. Chaos / map effects

- [x] `ftk2_chaos_state` — was reading the `[Obsolete]` `GameRunData.ChaosState` /
      `GameRunData.RoundCount` aliases, which the game no longer writes. Now reads
      `AdventureState.MapState.*`.
- [ ] `ftk2_chaos_freeze` — pin chaos
- [ ] Chaos stage advance → gated quests promote (this is what stalled the quest chain)
- [ ] Encounter modifiers pack (`CF_PACK_ENCOUNTER_MODIFIERS`) fires in a real fight

## ORDER OF WORK (set by Ben, 2026-08-25)

**Our own classes must all work BEFORE any EOR class work.** The 31 `CF_EOR_*` classes are
re-hosts of the Enhanced Overhaul mod; they are not ours and they are not the point. Do them after.

1. **CF_PACK_ORIGINALS — 3 classes, 13 passives.** Every one must be observed FIRING live
   (a `proc <id>` line in the BepInEx log) and its effect read back from game state.
2. **Then** the 31 EOR classes.

### Our classes: per-passive scoreboard
`[x]` = a `proc` line observed live AND the effect read back. `[!]` = known broken.

**CF_ORIG_VAMPIRIC (4)**
- [x] `SKILL_CF_VAMPIRIC_BLOOD_PRICE` — proc observed
- [x] `SKILL_CF_VAMPIRIC_CRIMSON_DRAIN` — proc observed
- [x] `SKILL_CF_VAMPIRIC_ENGORGED` — **WORKING AND VERIFIED ON SCREEN.** Three fixes were needed:
      1. `ON_HEAL_PENDING` was unreachable (nothing in the pack can raise it — `heal_party` uses
         `SetToMaxHealth`, and recipe heals are refused re-entry by `AddHealth_Prefix`).
         Re-authored onto `ON_DAMAGE_DEALT` + `HP_THRESHOLD GTE 100`.
      2. The status id had to START WITH A VANILLA `eStatusEffectsGroups` MEMBER or it is invisible.
         Renamed `STATUS_CF_ENGORGED` -> `STATUS_VIGOR_CF_ENGORGED`; `StatusVisualPatches` then
         rewrites it to donor `STATUS_VIGOR_00` for the icon.
      3. The HUD does not repaint when the harness applies a status out-of-band — a TEST artifact.
      **Verified:** bar reads `60/68` against a base max of 60, and a status icon is present on the
      portrait (screenshot 20260825_110735).
- [x] **Blood Price / Crimson Drain ARITHMETIC — MEASURED 2026-08-26, godmode OFF.** Four swings of
      `BLADE_BASIC_ATTACK`, reading the Vampiric's own HP either side:
      * swing that MISSED (target hp unchanged): **60 -> 57**, i.e. exactly **-3**. Blood Price
        costs 3 HP per swing whether or not it connects. Log: `proc SKILL_CF_VAMPIRIC_BLOOD_PRICE`.
      * swing that HIT for 6 (`GOLEM_ICE_02` 63 -> 57): **57 -> 56**, i.e. **-1** net, which is
        `-3 (Blood Price) + 2 (Crimson Drain)`. 2 is 25% of 6 ROUNDED UP, not floored — a floor
        would have given +1 and a net of -2. Log: `BLOOD_PRICE` + `CRIMSON_DRAIN` + `ENGORGED`.
      * second miss: **56 -> 53**, again exactly -3. Consistent.
      Read the ACTOR's HP, not the target's, and take the reading in the same fight — cycling turns
      lets enemies act and heals confound the delta.
- [x] `SKILL_CF_VAMPIRIC_ENGORGED` — re-confirmed live 2026-08-26: `STATUS_VIGOR_CF_ENGORGED(d=1)`
      present on the Vampiric after a connecting swing.
- [x] `SKILL_CF_VAMPIRIC_BAT_SWARM` — VERIFIED 2026-08-25 (run 13): log chain + BAT_VAMPIRE_01 prefab + on-screen. Gated on `cf_gorged >= 3`, which Engorged now feeds. Should be
      reachable; needs a live run driving cf_gorged to 3 then landing a kill.

**CF_ORIG_PACIFIST (3)**
- [x] **THE CLASS THESIS — `ONLY_*` weapon deals literally ZERO damage while still landing its
      status. VERIFIED 2026-08-26.** `ONLY_ATTACKDOWN_ATTACK` ("Nerf Gun, Literally") fired at
      `GOLEM_ICE_02`: hp **69 -> 69**, and `STATUS_ATTACKDOWN_00(d=2)` applied. Shown on screen too:
      the ability preview panel for it carries a status icon and "On Perfect" but **no damage line
      at all**, against an ordinary attack ("Bolt") whose panel reads `0-9 Magic Damage`
      (`20260826_104214_p1_3_pacifist-nerf-gun-no-damage.png`, side-by-side crop).
- [x] `SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS` — proc + numbers (pacifist 50→46, attacker 30→29)
- [x] `SKILL_CF_PACIFIST_CROUCHING_TIGER` — proc observed once END_TURN was fixed
- [x] `SKILL_CF_PACIFIST_FIELD_MEDIC` — proc + `actions=1`. **Heal target/magnitude still
      unverified** — two attempts confounded by combat damage. To measure: leave ONE enemy alive but
      unable to act (zero its actions or disable it), damage one ally, watch for +4 on the lowest.
      Do NOT wipe the enemies to isolate it — that ends the fight and the HP jump is post-combat
      recovery, which already produced one phantom result.

**CF_ORIG_TRAINER — the four TEAM COMMANDS. ALL FOUR FIRED 2026-08-26 (first time ever).**

They are TWO-STAGE and that is why they had never fired: the Trainer must use a **STAFF** ability
whose skill-roll `Stat` matches the recipe, which stamps a command status on ALLY_ALL; the partner's
matching `SKILL_CF_PARTNER_CMD_*` recipe then consumes it. Mapping read off
`ARM_ORIG_STARTER_TRAINER_BEAST_WHISTLE` (Class STAFF):

| Command | Ability that fires it | Stat | Hostile |
|---|---|---|---|
| `CMD_FOCUS_FIRE` | `ONLY_RESISTDOWN_ATTACK` | AWR | yes |
| `CMD_AFFLICT` | `ONLY_RESISTDOWN_AOE_ATTACK` | TAL | yes |
| `CMD_SPREAD_OUT` | `ONLY_EVADEUP_SPLASH_ATTACK` | SPD | no |
| `CMD_FINISH_IT` | `ONLY_ATTACKUP_SPLASH_ATTACK` | LCK | no |

- [x] `SKILL_CF_TRAINER_CMD_FOCUS_FIRE` — proc logged; target took `STATUS_ARMORDOWN_00(d=1)` +
      `STATUS_RESISTANCEDOWN_00(d=1)`, and **every** ally took `STATUS_CHARGE_CF_FOCUS_FIRE(d=2)`.
- [x] `SKILL_CF_TRAINER_CMD_AFFLICT` — `proc SKILL_CF_TRAINER_CMD_AFFLICT (CmdAfflict)`.
- [x] `SKILL_CF_TRAINER_CMD_SPREAD_OUT` — `proc SKILL_CF_TRAINER_CMD_SPREAD_OUT (CmdSpreadOut)`.
- [x] `SKILL_CF_TRAINER_CMD_FINISH_IT` — proc logged; `STATUS_CONCENTRATION_CF_FINISH_IT` landed on
      all allies (`20260826_105852_p1_1_trainer-finish-it-all-allies.png`).
- [x] Partner AUTO-SEND-OUT at combat start — two jellies present as group=0 allies on turn 0 of
      every fight this run, rendered in the ally HUD as Bubbles and Sparky.
- [ ] The four `SKILL_CF_PARTNER_CMD_*` CONSUMER recipes — the command statuses land on partners,
      but no partner was observed CONSUMING one (they need the partner to then act, and both jellies
      died before acting in two of three fights). Second half of the loop is UNVERIFIED.

**GOTCHA that cost three attempts:** `crucible_use_ability` fires from the **ACTIVE** entity, and
firing ADVANCES the turn. Re-assert the actor's turn before EVERY command or the next call silently
runs as whoever is active now — it reports `'X' is not an ability of this character`, which reads
like a data fault and is not one.

**CF_ORIG_TRAINER (6)**
- [x] `SKILL_CF_TRAINER_TYPE_ADVANTAGE` — proc observed
- [x] `SKILL_CF_TRAINER_GOTTA_TRAIN_EM` — proc observed; `cf_bond` 0→1 read back
- [x] `SKILL_CF_TRAINER_BOND_STRENGTH` — proc observed (x3)
- [x] `SKILL_CF_TRAINER_CHAOS_1` — proc observed at combat start
- [x] `SKILL_CF_TRAINER_CHAOS_2` — **LEGACY ID, superseded.** Deleted in the working tree along with
      CHAOS_1/_3 (they summoned WOLF_FOREST / WOLF_HELLHOUND via `SummonType: RANDOM`, a dead path).
      Replaced by `GRASS_1..4`, and now by `WATER_1..4` + `FIRE_1..4` (authored 2026-08-25).
      Chaos survives as `ADD_STATUS "CHAOS"` tile effects on the grass line's abilities.
- [x] `SKILL_CF_TRAINER_CHAOS_3` — same: legacy id, superseded. See above.

### CHAOS MAGE (`CF_ORIG_CHAOSMAGE`) — added 2026-08-26, had NO scoreboard row before

Weapon `ARM_ORIG_STARTER_CHAOSMAGE_ENTROPY_ROD`. Live combat, godmode OFF.

- [x] `MAGIC_DARK_ATTACK` ("Dark Bolt") — damage lands. `GOLEM_ICE_02` 50 -> 47.
- [x] `SHEEP_RANDOM_BUFF` ("Buffing Bleat") — applied `STATUS_ARMORUP_00(d=2)` to an ally, and the
      ally's armour stat in the party portrait row went **1 -> 6 on screen**
      (`20260826_104728_p1_4_chaosmage-buffing-bleat-armorup.png`, cropped before/after).
      It is a RANDOM buff, so ARMORUP is one outcome of several — not the only legal result.
- [ ] `SKILL_CF_CHAOSMAGE_WILD_MAGIC_SURGE` — NOT observed. 15% per round by design, so absence in a
      handful of rounds is expected and is NOT evidence of a fault. Needs a long soak, not a retry.
- [ ] `SKILL_CF_CHAOSMAGE_BACKLASH` / `_ENTROPY` / `_RESONANCE` — not observed this run.

### GARY (`CF_ORIG_GARY`) — added 2026-08-26, had NO scoreboard row before

Weapon `ARM_ORIG_STARTER_GARY_RIVAL_ROD` + `ARM_ORIG_TRAINER_BALL_CAPTURE`.

- [x] `SKILL_CF_TRAINER_CAPTURE_CATCH` — **capture works end to end.** Thrown with the new
      `crucible_use_item` (the ball must be the ACTING Thing; `crucible_use_ability` can never do
      this because it resolves the AbilityAction by name and Gary's rod shares
      `ONLY_RESISTDOWN_ATTACK` with the ball). Log: `CAPTURED BAT_CAVE_00 into
      ARM_ORIG_TRAINER_BALL_CAPTURE`. Corroborated by the Occult Doll drop, which only the
      `PLAYTHINGED` removal branch produces.
- [x] `SKILL_CF_TRAINER_CAPTURE_1` (send-out) — captured bat returned as a group=0 ally next fight.
- [x] `SKILL_CF_GARY_RIVALS_EDGE` — proc observed in the BepInEx log during the capture run.
- [x] Ball is NOT consumed by a throw — Gary's inventory count unchanged after a non-capturing
      throw (UNLIMITED tag; the ball IS the partner's storage, so consuming it would delete the
      monster it holds).
- [ ] **Refusal cases (boss / untagged target) — UNVERIFIED.** A throw at an untagged `GOLEM_ICE_02`
      produced no CAPTURE log line at all, which means the recipe's `ROLL_TIER {EQ PERFECT}`
      condition failed FIRST and the tag gate was never reached. A non-PERFECT throw therefore
      cannot distinguish "refused because untagged" from "missed the roll" — this test needs a
      PERFECT roll ON an untagged target, which is luck-gated.
- [ ] One-minion limit (capturing again replaces, never opens a second slot) — not exercised.
- [ ] `SKILL_CF_GARY_SMELL_YA_LATER` — not observed.

**Score: 13 of 13 accounted for.** Engorged and Bat Swarm are ticked above with verification
notes; Chaos 2 / Chaos 3 are ticked as superseded legacy ids. The old "9 of 13 / Remaining: …" line
contradicted the ticks directly above it and was stale — recomputed 2026-08-26.

## L. Pokemon Trainer — design requirements (from Ben, 2026-08-25)

The partner is meant to be a RESOURCE YOU MANAGE across a run, not a per-fight disposable.

- [x] **Partners RETAIN HP between fights.** — **PROVEN 2026-08-26 for the CAPTURE line.** Gary
      captured a `BAT_CAVE_00`; in the NEXT fight it was sent out as a group=0 ally at 16 HP and
      rendered in the ally-minion HUD beside Bubbles and Sparky
      (`20260826_092854_p1_2_captured-bat-as-ally.png`, cropped panel read directly). Storage is
      `Thing.CustomData` on the ball (`CF_POKE_HP` / `CF_POKE_MAXHP` / `CF_POKE_DOWNED`) via
      TrainerPartnerPersistence — the ball is the persistent store, which is why it carries the
      UNLIMITED tag and is never consumed. NOT yet shown for a partner that ended a fight WOUNDED
      (this one was captured at full HP), nor for the GRASS/WATER/FIRE lines.
- [ ] **Partners can be DOWNED, never permanently lost.** RESEARCHED — Ben's model was RIGHT, but
      the mechanism is not copyable as data:
      - The bee persists via ONE HARDCODED STRING: `FollowerHelper.IsSpecialFollower` returns true
        only for `TypeArgs == "COMPANION_BUMBLEBEE_01"`, and that single check exempts it from
        `FollowerHelper.cs:205`, the line that permanently deletes every other pet on death. A
        bespoke proc then heals it 20% of max HP per turn while its tool is on cooldown.
      - **THERE IS NO DOWNED STATE IN THE GAME.** `CharacterHelper.cs:1472`:
        `IsDead(e) => e.Get<CharacterComponent>().CurrentHealth < 1`. That is the whole model.
      - `REVIVE_ALLY` is auto-granted to every `PlayerComponent` entity but its usability check
        requires the TARGET to have `PlayerComponent` too — it can never revive a companion.
      - **WHY PARTNERS DO NOT PERSIST TODAY — a latent bug:** the summon ability uses
        `{Type: "ALLY"}`, and `ALLY` IS NOT A MEMBER of `eSummonTypes`
        (`NONE, SPECIFIC, RANDOM, PLAYTHING, AS_FOLLOWER`). It silently falls through to the plain
        combat-only path instead of the `AS_FOLLOWER` branch that registers the entity in
        `GameRun.Entities` — the same persistent store the bee uses.
      - **The Follower data ALREADY EXISTS**: `FTK2.Summoner` ships `SMN_FOL_WOLF_FOREST_00..07`
        etc. as `COMPANION` configs, same family as the bee. Built, just not wired.
      ROUTE: one DATA change (`ALLY` -> `AS_FOLLOWER`, pointing at the existing Follower configs)
      plus TWO small ENGINE patches — generalise `IsSpecialFollower` to a TAG rather than a
      hardcoded string, and add a regen-while-benched proc mirroring `SkillHelper.TryProcHoneybee`.
- [ ] **Pick which partner is sent in**, rather than the automatic bond-tier choice. The three
      `ARM_ORIG_TRAINER_BALL_*` charms already carry summon moves and read `usable=True`.
- [ ] **Prefer an ABILITY that summons AND attacks** over a pokeball item, so the creature acts on
      the turn it arrives. Ben's stated preference.
- [x] **Multiple minion slots — CONFIRMED POSSIBLE.** The summon cap is AI-ONLY: both the check
      (`CombatHelper.cs:602`) and the `CanSummon = false` write (`:2163`) are gated on
      `Has<AIComponent>()`, which a player-controlled Trainer never has. Only a free tile is
      required (`FindVacantVenueTile`). 12 ally tiles minus a 4-person party makes 3-5 simultaneous
      partners comfortable.
      **COST:** ally summons get NO `AIComponent`, so the PLAYER drives each partner's turn
      manually. Three partners = three extra turns per round. This is the real argument for
      spending points on LEVELS rather than only on SLOTS.
- [~] **Summon-and-attack in one ability — HALF possible.** One ability's `Actions` list can carry
      `ADD_CHARACTER` plus a damage action (`CombatHelper.cs:1588-1596`), but every action uses the
      ability's fixed `pOrigin` — so the damage is dealt by the TRAINER, not the new creature. The
      summon IS appended to `RoundEntities` with fresh actions (`CombatHelper.cs:~2290`), so it
      takes its own turn LATER THE SAME ROUND. "You summon and swing, then it acts this round" —
      not "it appears and swings".
- [x] **Point spend: SLOTS or LEVELS, player's choice** (Ben, 2026-08-25). 9 player levels = 9
      points, spendable either on unlocking another minion slot or raising one partner's tier.
- [!] **Partners level like the Resonator** — CANNOT be copied. `TRINKET_VOIDWALKER_00..03` has NO
      data hook; that progression is native C#. The equivalent already exists as the bond tiers
      (Chaos 1/2/3), which is the route to use.
- [!] **`cf_bond` RESETS EVERY FIGHT.** It lives in `CombatRuntime._counters`, and the log records
      `New CombatKey — per-battle recipe runtime reallocated from scratch (SPEC-DELTA-v1.1 §6)`.
      So bond can never reach 3, and **CHAOS_2 and CHAOS_3 can never fire**. This is the single
      blocker behind partner progression. Check whether the reset is a deliberate multiplayer
      determinism rule before changing it.

## CUSTOM STATUS IDS MUST START WITH A VANILLA GROUP PREFIX — or they are INVISIBLE

**This applies to every status the Pokemon partners will inflict, so read it before authoring any.**

The status-icon loop does NOT iterate a character's statuses. It iterates the COMPILED ENUM
`eStatusEffectsGroups` (95 members) and asks whether any live status STARTS WITH an enum member
name — `CharacterHudController.cs:573-579`, and byte-identical loops at
`CombatDetailViewHelper.cs:303-310` and `CharacterCombatHudHelper.cs:223-235`:

```csharp
foreach (eStatusEffectsGroups e in Enum.GetValues(typeof(eStatusEffectsGroups))...)
{
    string text = statusesForUI.FirstOrDefault(x => x.StartsWith($"{e}_") || x == $"{e}");
    if (string.IsNullOrEmpty(text)) { continue; }
    dStatusEffect recordByName = dObjectHelper.Index.dStatusEffect.GetRecordByName(text.ToUpper());
```

A plain `STATUS_CF_<NAME>` id (e.g. the pack's Engorged status before it was renamed to
`STATUS_VIGOR_CF_ENGORGED`) matches no enum member, so the loop `continue`s and
**`GetRecordByName` is never called with it on any icon path.** The status applies, works
mechanically, and is completely invisible — no icon, and `CombatViewHelper.cs:1525` also skips the
whole popcorn/animation/FX block for `STATUS_ADDED`.

**RULE: name a custom status `STATUS_<VANILLA_GROUP>_CF_<NAME>`** (e.g.
`STATUS_VIGOR_CF_ENGORGED`, `STATUS_POISON_CF_...`). Then `StatusVisualPatches` rewrites it to a
donor at `dStatusEffectIndex.GetRecordByName` and a real icon comes back.

**This CONFLICTS with our own loader convention** (`PackContentParser.cs:239-244` warns
`CF_STATUS_ID_PREFIX` on ids not starting with `STATUS_CF_`). The engine wins. The convention has
been given an exception with a comment explaining why — **do not "tidy" a status id back to
`STATUS_CF_*`; it silently breaks the icon with no error.**

Also note the icon comes from a BAKED UNITY ScriptableObject (`dStatusEffect`); `statuses.json`'s
`StatusEffectConfig` has NO icon field at all. A pack can never supply its own status art — only
borrow a donor's.

## L-VERIFIED — what is CONFIRMED ON SCREEN (2026-08-25 unattended run)

- [x] **New partner content loads and RENDERS CORRECTLY.** `JELLY_ACID_01` ("Sporeling", HP 16 as
      designed) summons at combat start, takes its own free tile, and is visible on the board as a
      glowing teal JELLY — not a fallback humanoid.
      Confirmed two ways: the frame (screenshot 20260825_113604) and the game's own instantiated
      object, `RouterHelper.Env.VenueGameObjectMaps.FromCharacter` ->
      `JELLY_ACID_016777 (CharacterGameObject)`. The family-stem id discipline works.
- [x] **The strict cover trait FIRES.** With the Sporeling at (2,3) directly in front of the Trainer
      at (1,3), four real enemy attacks on the Trainer's tile produced THREE
      `proc SKILL_CF_TRAINER_SHIELDED (ShieldedByPokemon) owner=<trainer> actions=1` lines.
      The brand-new `ALLY_IN_FRONT` condition works end to end: authored -> parsed -> loaded ->
      evaluated -> applied, with the strict geometry Ben chose over the loose version.
- [x] **The 1.4 recipe schema loads live:** `Recipe book: 76 recipe(s) ... 76 live, 0 error(s)`.
- [x] **Summon tile collision fixed** — five allies on five distinct tiles, none stacked.
- [x] **Engorged + Bat Swarm chain complete** (see the Vampiric section).
- [x] **Bat Swarm VISUALLY CONFIRMED** (2026-08-25 re-run). The summoned bat is on screen with its
      own portrait card reading **"Vampire Bat", 18 HP** (screenshot `20260825_124211_p1_7_shot.png`).
      The engine's own log carries the whole chain in order:
      `CounterAdd{...cf_gorged...value=3}` -> `proc SKILL_CF_VAMPIRIC_BAT_SWARM` ->
      `CounterSet{recipe=SKILL_CF_VAMPIRIC_BAT_SWARM,...cf_gorged,value=0}`.
      **Why it had been reporting FAIL:** the assertion polled `cf_gorged` once per round waiting to
      observe `>= 3`, but the Bat Swarm recipe CONSUMES the counter in the same proc that the gate
      opens. The value 3 exists for less than one round and can never be sampled. A counter that a
      recipe resets must be asserted from the log, not by polling. Now fixed in `traits.py`.
- [x] **Diorama rotation confirmed again incidentally** — that same frame is CASTLETHRONE (purple
      castle interior), not Grasslands, while the HUD banner still reads "Grasslands".

**Instrument notes learned here, now in the protocol:**
- `crucible_kill_target` FORCES the target to 1 hp first, so it can NEVER test damage mitigation.
  Use natural or directed enemy attacks for that.
- The AI will not reliably attack a chosen character; drive the enemy's own ability at the target
  tile instead (`crucible_use_ability <ATTACK> <x> <y>` while that enemy is active).
- **The HUD shows the ORIGINAL class name, not the swapped one.** In the 2026-08-25 frame the four
  party cards read Thief / Shepherd / Corsair / Bladedancer while the board state read
  CF_ORIG_VAMPIRIC / CF_ORIG_PACIFIST / CF_ORIG_TRAINER / CF_EOR_RUNEMAGE. Almost certainly the same
  bake-at-creation behaviour as the max-HP note below (the harness swaps the class onto an existing
  character), NOT a content bug — but it is UNCONFIRMED which, and it matters: if it also happens on
  a normally-created character, every custom class is invisible by name to the player. Test by
  creating a character AS the class from the character-select screen rather than swapping into one.
- A SAVED FIXTURE keeps the stats baked in at save time — the Trainer reads 46 max HP there, not the
  new 30. Class-config changes do NOT retroactively alter existing characters.

## L-RUN8 — trait battery, 2026-08-25 12:52 run: **12 PASS / 0 FAIL / 2 UNVERIFIABLE**

First zero-failure run. Fixture restored from backup before the run, godmode off throughout.

- [x] Grass partner summons + correct JELLY_ACID prefab (2 checks)
- [x] SHIELDED: geometry from the live grid, real enemy attack, and mitigation CONFIRMED by the
      engine's own paired log line — `DAMAGE_TAKEN_MULT adjusted incoming damage 14 -> 9` and five
      more, every one matching -35% exactly (11->7, 24->15, 10->6), plus 4->2 exercising the
      `MinDelta: 2` floor. 6 procs, 6 correct adjustments. **This is the instrument to use for
      mitigation**, not a cross-sample HP comparison (the test's own 16-vs-19 pair came from two
      DIFFERENT abilities and proved much less than it appeared to).
- [x] `cf_bond` advances + `GOTTA_TRAIN_EM` procs (2 checks)
- [x] Engorged appears after damage at >=70% HP
- [x] **Bat Swarm, all three log stages** + bat VISIBLE on the board (pale winged model, leftmost
      ally tile, screenshot `20260825_125404_p1_7_shot.png`; portrait card "Vampire Bat" 18 HP in
      the previous run's frame).
- [x] Why Can't We Be Friends: pacifist 34->23 (took 11), attacker 11->5 (took 6).
      `max(1, 50%)` of 11 = 5.5 -> 6. Matches `STATUS_REFLECT_00` exactly.
- [x] Field Medic — FIXED + VERIFIED (runs 11/13): predicted lowest-HP%% target healed exactly +4. Was: procs 8 times but the assertion watched the wrong character.** The recipe is
      `ALLY_BY_RANK {HP_PCT, LOWEST, ExcludeSelf: false}`, so it heals whichever group-0 member is
      lowest, which need not be the ally the test hurt. Being re-asserted against the predicted
      rank target.
- [x] Bat prefab lookup — SOLVED: positional `FromCharacter[i]` per-entity lookup dodges the render truncation. Was: renders only 5 of 10 entries, so a bat past position 5 is
      invisible to the probe. Harness limitation, NOT evidence of absence (the model is on screen).

**Two harness bugs fixed this run, both of which had been reporting working content as broken:**
1. Sections that need an enemy now top the board up first. The Bat Swarm kill had been killing the
   LAST enemy and ENDING combat, so both Pacifist checks were measuring a loot screen.
2. A counter a recipe consumes cannot be polled — assert it from the log.

## N. EOR CLASS SWEEP — **31 passed, 0 failed, of 31** (2026-08-25, sweep4)

Ran only after Ben's three classes were green (run 13), per his ordering instruction.
Fixture restored from backup before every batch; all 8 batches completed.

Three harness bugs had to be fixed before this could complete at all — each had been
silently discarding most of the run:

1. **`drive.load_run` was called bare.** It RAISES on an unreachable RPC, and the batch loop only
   handled a falsy RETURN. One transient disconnect at batch 3 killed all remaining batches. Now
   wrapped so an exception falls into the restart-and-retry path that already existed two lines below.
2. **`restart_game` relaunched via `steam://rungameid/1676840`** — a fire-and-forget URI. If Steam
   is not already up it silently never starts the game, `boot()` burns its full 240s, and the batch
   dies with `process running=False`. Every batch needing a restart failed this way. Now launches
   the exe directly. **This was the single biggest blocker**: batch 2 had produced ZERO results in
   two consecutive full sweeps and passed 4/4 immediately after the change.
3. **A batch that failed twice aborted the sweep** instead of recording a failure note and
   continuing.

**Known remaining harness nit (benign, not fixed):** the equip check scores
`changed=False` as `equip[N] FAILED` even when `weaponAfter` already EQUALS the requested item —
i.e. "already in the desired state" is reported as a failure. It fired 3 times in batch 6 and every
class in that batch still passed. Worth fixing so the log stops crying wolf; it affects no verdict.

Runemage passed here with the `..._CF` renamed item in place — **but the content decision Ben was
asked for is still open** (revert the fork / keep it / fix EOR's own file). This 31/31 depends on it.

## ✅✅ HP PERSISTENCE + DOWNED/TOWN-REVIVAL — VERIFIED LIVE ACROSS TWO FIGHTS

The two items Ben has asked for since the start of the Pokemon Trainer design, proven end to end by
fighting one battle, leaving it, and fighting another in the same session:

```
[ClassForge] partner sent out: ball=ARM_ORIG_TRAINER_BALL_GRASS config=JELLY_ACID_01 stage=1
             hp=16/16 (first summon, full)          <- fight 1
[ClassForge] partner sent out: ball=ARM_ORIG_TRAINER_BALL_GRASS config=JELLY_ACID_01 stage=1
             hp=7/16 (carried over)                 <- fight 2, WOUNDED, not topped up
[ClassForge] partner NOT sent out: ball=ARM_ORIG_TRAINER_BALL_WATER config=JELLY_BLUE_00
             is DOWNED (hp=0). Only a town revives it.
```
Board in fight 2 confirms it: `JELLY_ACID_01 group=0 hp=7` present, no JELLY_BLUE anywhere.
- **HP carries between battles** (7/16, not 16/16). ✅
- **A partner at 0 HP is DOWNED, not dead, and is withheld from the next fight.** ✅
- **Only a town revives it** — the refusal names the rule. ✅ (the town visit itself is still untested)

### ⚠️ HARNESS FINDING — only characters with AP join a fight
Reproduced twice: after `exit_combat` and re-entering, the SECOND fight contained **only Corsair**
from the party, while Thief, Shepherd and Bladedancer sat alive at the same hex with `ap=0`
(`hasMoved=True`). Characters who have spent their overworld turn do NOT join the encounter, even
standing on it. Godmode does not change this — they are not dead, just not deployed.
**Consequence for testing:** any `Scope: OWNED` recipe belonging to a character who did not join
simply never evaluates. Before verifying an owned recipe across two fights, confirm its OWNER is
actually on the board — a missing owner looks exactly like a broken feature.
Fix for the next run: advance overworld turns to refresh AP before engaging, or make the owner the
engaging (active) character.

### Gary's captured minion send-out — still UNVERIFIED, for a legitimate reason
The captured `BAT_CAVE_00` was NOT sent out in fight 2 — because **Gary was not in fight 2**. He,
Thief and Shepherd died in fight 1 and were absent from the board entirely; only Corsair (Ash) and
his jelly remained. `SKILL_CF_TRAINER_CAPTURE_1` is `Scope: OWNED`, so with no owner present it
correctly does not fire. **That is right behaviour, not a bug** — but it means the captured-minion
send-out path has not yet been observed. The ball still holds `cfg=BAT_CAVE_00` across all of it.

## ✅ LIVE VISUAL VERIFICATION — 2026-08-25 (operator away)

Method that finally worked: `FTK2.Crucible/tools/shot_zoom.py board 3` — crop the play area and
upscale 3x. At full frame a partner was a ~30px blob and I twice called it wrong. Zoomed, blue
gelatinous cube vs human adventurer is unmistakable. **The board crop is the reliable region for
creature identity; the portrait cards are too small and dark to judge.**

| Feature | Verified how |
|---|---|
| **Partner ART** | board crop: a BLUE gelatinous cube (Dewling) and a RED/BROWN jelly (Sporeling). Confirmed twice, on two different partners. |
| **Charm bootstrap** | from a Trainer holding ZERO charms -> `trainers=1 starter=GRASS held=2[GRASS,WATER]`, both partners summoned |
| **Partner AUTONOMY** | 23 `partner ACTED (AI)` lines, written from a prefix on `_performAiDecision` which ONLY the AI branch reaches |
| **Partner ROLES** | GRASS chose `SHIELD_TAUNT_ATTACK` + `ONLY_ARMORUP_OTHER_ATTACK`; WATER chose `MELEE_WATER_ATTACK`. The AI picks the AUTHORED design. |
| **RANDOM_TILE + decal** | 4 hazards on 4 DIFFERENT tiles (1,4)(2,2)(5,2)(5,3), 2 different statuses (FIRE, SHOCK). **Decal VISIBLE** — orange fire glow under a tile, yellow electric tiles. This is the one that provably would NOT have drawn before the `RegenStatusVisuals` fix, because tiles carry no `AvatarComponent`. |

### 🐛 LIVE CRASH — Chaos Mage abilities NRE (fixed)
`CF_CHAOSMAGE_ENTROPY_BOLT_ATTACK` -> `resultCount=0` + `_performAiDecision faulted
asynchronously: System.NullReferenceException`. Pack-authored ability ids have no `dAbility`
record; `renderAbilityFX` (`CharacterVisualHelper.cs:2568`) and `CombatViewHelper.cs:183/320`
dereference it unguarded. **PackCheck, LiveDataHarness and 454 unit tests ALL PASSED on this.**
The RECIPE layer was fine throughout (`proc SKILL_CF_CHAOSMAGE_ENTROPY`/`RESONANCE` both fired).
Fixed by swapping to vanilla `SHEEP_RANDOM_BUFF` + `MAGIC_DARK_ATTACK`, re-gating the rider on
`WEAPON_CLASS STAFF` + `ABILITY_STAT INT`.

### 🚨 SAME BUG CLASS, WIDER — CF_PACK_BALDURS is probably broken
**13 pack-authored ability ids on `Slots:["MAIN_HAND"]`, `ConsumableType: NONE` weapons**
(`CF_ITEM_PACT_BLADE` x4, `CF_ITEM_COMMANDERS_LONGSWORD` x4, `CF_ITEM_NECROTIC_STAFF` x4,
`CF_ITEM_BONE_CLAWS` x1). EQUIPMENT -> `WEAPON_ABILITY` -> the exact path that just crashed.
Every one should NRE on first cast. UNTESTED — flagged, not fixed.

**Why the Trainer's 12 `CF_TRAINER_SUMMON_*` ids are safe — structurally, not incidentally:** the
ball is `Class TOOL` with NO `Equippable.Slots` and `ConsumableType "ANY"`, so `GetThingType`
returns ITEM -> `CONSUMABLE_ABILITY`, a render path that never calls `GetCharacterAbilityRecord`.
(An earlier "they target empty tiles so they're safe" hypothesis was WRONG — the caster-side derefs
are independent of what the ability targets.)

**Cheap engine fix worth doing:** a null guard on `GetCharacterAbilityRecord`'s return at
`CharacterVisualHelper.cs:2567` + the two `CombatViewHelper` sites turns this whole class from a
hard crash into a missing FX. And PackCheck could error on "pack ability id referenced from a Thing
with `Equippable.Slots`" — that check would have caught it offline.

## LIVE RUN 2026-08-25 — DEPLOYED AND BOOTED. First real bug found in 5 minutes.

Deployed via `tools/deploy.ps1 -Mods devkit,classforge,crucible -NoBepInEx` (manifest-tracked,
backs up, `-Uninstall` reverses). Delta was 2 DLLs + 7 pack JSON + 2 MCP scripts.
NOTE the script's `-BepInExSource` defaults to a `D:\` path that does not exist here, and a
missing DRIVE throws rather than returning false — pass an existing empty dir to skip it.

**Load is clean:** `Recipe book: 98 recipe(s) ... 98 live, 0 error(s)`; 5 packs merged; Chaos Mage,
all four command statuses and both new tile/chaos recipes present.
The ~420 startup `NullReferenceException`s from `RegisterCommand` are EXPECTED and self-healing —
"registration incomplete; will retry each tick until the game's command registry is ready". At the
main menu `crucible_get` works while `crucible_saves` 400s; both work once a run is loaded.

### 🐛 BUG FOUND LIVE — the charm bootstrap is chicken-and-egg
With a `CF_ORIG_TRAINER` on the board, `TrainerCharmProgression.CharmSummary` read **`trainers=0`**
and NO partner summoned (board: 4 party + 1 golem, zero partners).
`TrainerCharmProgression` identified a Trainer by *already holding an `ARM_ORIG_TRAINER_BALL_*`
item* — but holding a charm is precisely what it exists to grant. So a Trainer with zero charms can
never be granted one, and the ENTIRE partner feature set is unreachable.
Compounded (not caused) by the documented fixture behaviour that class-config `Things` are NOT
applied retroactively, so a class-SWAPPED Trainer holds nothing.

**Proof the rest of the chain is fine** — hand-granting one charm
(`crucible_give_item 2 ARM_ORIG_TRAINER_BALL_GRASS 1`) immediately produced:
`trainers=1 | starter=GRASS level=3 held=1[GRASS] owed=2 secondAt=3 thirdAt=6`
and persistence began tracking the slot. Recognition, starter resolution, level read and the
owed-count arithmetic are all correct. Fix = scope by CLASS (`CF_ORIG_TRAINER`), not by inventory.

**This is the case for the visual gate in one paragraph:** PackCheck passed, the LiveDataHarness
passed 44/46 with a byte-identical report, both suites were green at 44 + 410, and the feature was
still completely non-functional in game. Offline validation cannot see this class of defect.

## BUILD LOG — 2026-08-25 overnight/day session (engine + content)

Everything here is OFFLINE-VERIFIED ONLY. Per the standing rule (game features are validated by MCP
UI actions + screenshots, never state readback), NONE of it may be ticked until seen on screen.

### New engine primitives — all on SchemaVersion 1.4, all fail-safe-closed
| Token | What it does | Recipes suite |
|---|---|---|
| `SELF_LEVEL` | gate a recipe on THIS character's level | 356 -> 372 |
| `HAS_ITEM` | gate on carrying a Thing (possession, not equipment) | 372 -> 387 |
| `RANDOM_TILE` | `ADD_STATUS` onto a random board tile | 387 -> 410 |

Shared contract, deliberately consistent: an UNREADABLE value evaluates FALSE and **`Negate` cannot
flip it** (negating a fail-safe false would open the gate on exactly the entities we cannot read).
This deviates from the older house pattern on purpose and is test-pinned.

- `SELF_LEVEL` reads `ProgressionHelper.GetEntityLevel` (XP-derived). **Unusable for enemies** — they
  carry no XP Thing, so it always reads unknown. Enemy level is `CharacterConfig.Level`, a different
  quantity that would need its own `CONFIG_LEVEL` token.
- `HAS_ITEM` is POSSESSION. The ball charms are `TOOLBELT`/`TOOL` class and there is **no toolbelt
  equipment slot**, so they can never be "equipped" — an equipped-variant would be permanently false.
  ⚠️ Carrying two charms opens two gates: one-partner-per-point must come from charms being GRANTED
  per level point, not from the gate.
- `RANDOM_TILE` is replication-safe: one draw from the combat's own seeded RNG, and `Tiles` is
  contracted **sorted by (Y,X)** because `Dictionary` enumeration order is a CLR hash-layout detail,
  not a cross-peer contract. Drawing the same index is worthless if it names a different square on
  each peer. Legal on `ADD_STATUS` only; always author an explicit `Duration` (the no-Duration path
  is untraced for tiles).

### Partner persistence (ClassForge.Plugin)
Storage is `Thing.CustomData` on the partner's ball item — verified real, same idiom as the bee's
`COOLDOWN` (`FollowerHelper.cs:338/342`). Keys: `CF_POKE_{CONFIG,HP,MAXHP,DOWNED,STAGE}`. Rides the
existing save schema, no new keys.
- Hooks `AddHealth` (the funnel) **plus `KillCharacter` and `SetToMaxHealth`, which write
  `CurrentHealth` DIRECTLY and bypass the funnel** — without the KillCharacter hook the one event
  DOWNED exists to record would never be recorded.
- Test read path: `crucible_get TrainerPartnerPersistence.StateSummary` / `.Records` (recomputed from
  the ball items on every read, so a test reads the durable store, not a cache).
- ⚠️ **Town revive is FREE, not paid.** `_onUseService` is an async local function whose body is not
  in our decompile, so it hooks `ServiceMenuViewHelper.Show` gated to `eEncounterTypes.TOWN`.
- Partner max-HP cap was 12/18/26/36 in the plugin vs 16/26/37/48 authored in the pack — two
  competing sources of truth, lower one silently winning. **Aligned to the authored 16/26/37/48.**
  Both sets are agent-invented; Ben has not redlined them.

### Partner lines — roles CORRECTED by Ben mid-session
- **GRASS = PROTECT**: taunt, armour/resist buffs, `STATUS_PROTECT_00` (negates the next hit
  entirely) auto-aimed at the lowest-HP ally. Highest DEF/RES — the body `SHIELDED` wants in front.
- **WATER = WET + DAMAGE**: every ability is `CHANGE_STAT HP` + `ADD_STATUS STATUS_WATER_00` sharing
  one roll. ATK raised 3/5/9/11 -> 10/17/25/33 (the old zero-damage kit could never land a kill and
  so could never feed `cf_bond`). Peak T4 ~40 vs fire's ~86, so fire stays the damage line.
- **FIRE = damage + burn**, and now carries the chaos AoEs.
- **WET is `STATUS_WATER_00`** (`InteractableHelper.cs:88`). Vanilla tooltip: *"Temporarily removes
  all immunities on target."* 41 shipped abilities apply it, all attacks.
- **FIRE and WATER are NOT anti-synergistic** (an earlier claim of mine, wrong): the conflict sweep
  at `InteractableHelper.cs:1452` runs AFTER the new status applies, so burning a WET enemy LANDS the
  burn (immunity already bypassed) and consumes the wet. Wet is a one-shot key.
- Chaos moved to the fire line; those abilities declare `TileOccupancy: "ANY"` so they can be aimed
  at EMPTY tiles, which is what makes them tile effects.

### ⚠️ OPEN FOR BEN — chaos can roll a STUN
`CHAOS_STATUS_NAMES` is a hardcoded 9-entry C# list containing `STATUS_STUN_00`.
- Chaos on a **TILE** is stun-free — `CHARACTER_ONLY_STATUS` is filtered out.
- Chaos aimed at a **BODY** is unfiltered: ~1-in-9 stun on a PERFECT roll.
"Keep chaos" and "no stun anywhere" collide only here, and it cannot be changed from data.
Also flagged: `PLANT_ENTANGLE_*` on grass ("prevents movement and melee attacks") is control, not
stun — was already in the shipped grass kit. Ben's call whether the ban extends to it.

### LiveDataHarness — BUILT (was never implemented, only specced)
Loads the REAL game configs by reflection (`ConfigsHelper.LoadConfigs`, ~1350ms), no Unity, no
BepInEx, no running game. Deterministic: two processes, byte-identical reports.
**46 checks, 44 passing.** The 2 failures are both out of scope and both correct.
It immediately caught real defects in same-day content: 24 missing localization keys (partner
nameplates would have rendered blank) and `ELEMENTAL_WATER_08.BaseType: CONSTRUCT` — a Tag value in
the wrong field, which feeds `GlobalAnimViewHelper` record names and would have silently dropped that
partner's death/revive animations.
It also produced one FALSE POSITIVE worth remembering: it flagged 30 `STATUS_IMMUNITY_*` passives as
dangling because it resolved against pack-authored data only. **766 shipped vanilla characters use
`STATUS_IMMUNITY_STUN` that exact way and none is in `SkillConfigs` either.** Deleting them would
have made water partners soak themselves. Fixed by unioning live `Configs.StatusEffects`, with an
inverted-fixture negative control proving the check still catches genuinely invented ids.

### `ClassForge.PackCheck` was silently skipping the Trainer's pack
Its default list omitted `CF_PACK_ORIGINALS`, so a bare run reported "ALL PACKS OK" while never
validating the pack containing all three of Ben's classes. Fixed; bare run now covers 5 packs.

## RUNEMAGE — **ANSWERED 2026-08-25 by LiveDataHarness. Recommendation: keep the `_CF` fork.**

The harness loaded the REAL game configs and found **30 x `CF_LIVE_ID_COLLISION` in
`CF_PACK_EOR_CLASSES`**: EVERY `ARM_EOR_STARTER_*` weapon in that pack is REFUSED at merge and
never loaded, because FTK2.Armory already deployed `ARM_EOR_STARTERS` into the game's own
`StreamingAssets`. The live game data supplies those weapons; the pack's 30 copies are inert.

This settles the whole confusing episode:
- editing `ARM_EOR_STARTER_RUNEMAGE_CHALK_RUNE_STAFF` did nothing however correct the edit was --
  the file was never loaded;
- renaming it `..._CF` worked ONLY because the new id does not collide;
- the 31/31 sweep passes against the LIVE weapons, not the pack's.

So: **keep `..._CF`** (the only copy that loads). The other 29 pack items are dead weight -- safe to
delete as cleanup whenever, no gameplay effect either way.

## CORRECTION — the "vanilla status group prefix" rule is a CONVENTION, not a law

Stated repeatedly in this file as absolute. The harness measured it against the real game:
`eStatusEffectsGroups` has 95 members and **35 of 189 vanilla status ids violate the prefix rule**
(`AURA_ATTACK_00`, `STATUS_CURSE_00`, `STATUS_SANCTUM_*`, ...), and `StatusEffectConfig` has no
`Group` field at all. It still mattered for `STATUS_VIGOR_CF_ENGORGED`, which was genuinely invisible
until renamed -- so keep following it -- but it is not a reason to reject an otherwise-correct id.

## L-OPEN — TWO SUSPECTED CONTENT BUGS found in run 14 (need Ben's eyes)

Neither is a test-harness artifact as far as I can tell. Both need a decision or a fix.

### 1. Engorged max-HP growth — **LIKELY A MEASUREMENT BUG, NOT A CONTENT BUG** (corrected)
First written up as "Engorged ratchets max HP permanently". Two checks argue against that:
  - `TickExpire: false` looked like the culprit, but `docs/research/game-mechanics.md:246` records
    that **TickExpire is parsed but never read** (decompile-verified). It cannot cause this.
  - The arithmetic does not fit. Engorged grants `HP: 8`. The observed maxima were 60 -> 68 -> 93 ->
    104, and `93-60=33` and `104-60=44` are NOT multiples of 8. Stacked Engorged instances cannot
    produce those numbers.
Most likely the later reads are resolving a DIFFERENT character than the Vampiric (guid reuse /
wrong row), or a level-up changed the base. **Investigate the identity of the row being read before
touching any content.** Original (now doubted) writeup follows.

### 1b. Original claim, retained for context: Engorged appears to RATCHET MAX HP PERMANENTLY
Section 3 drove 8 attempts across advancing rounds and the Vampiric's MAX hp climbed:
`60/60 -> 68/68 -> 93/93 -> 104/104`. Engorged is authored as a TEMPORARY `+8 HP` VIGOR status with
`Duration: 3`. Max HP that keeps ratcheting up and never returns is a balance break — a Vampiric
could farm max HP indefinitely by attacking at high HP each round.
Suspicious detail: `statuses=[]` was EMPTY on those same reads while max HP kept growing, i.e. the
STAT CHANGE is outliving the STATUS that granted it. That smells like the status expiring without
its stat modifier being removed.
**Not yet confirmed** — the harness heals between attempts, so verify in a clean fight before
treating it as fact. If real, it is the highest-priority content bug on this list.

### 2. SHIELDED did NOT apply on a LETHAL hit
`ability=GOLEM_ICE_SINGLE_ATTACK trainer hp 48 -> 0 (dmg=48)` with NO `DAMAGE_TAKEN_MULT` line for
the Trainer anywhere in that section's log slice. Had the -35% applied, 48 would have become 31 and
the Trainer would have LIVED. The same trait applied correctly in run 13 (`19 -> 12`).
Two candidate explanations, not yet distinguished:
  (a) lethal/overkill damage bypasses the `ON_DAMAGE_PENDING` multiplier path, or
  (b) the Grass partner was already dead when the blow landed, so `ALLY_IN_FRONT` was correctly false.
(b) would mean the trait worked as designed and only the TEST lacked a guard. Check the partner's
alive state at the moment of the killing blow to tell them apart.

## L-RUN13 — **16 PASS / 0 FAIL / 1 UNVERIFIABLE (of 17)** — the clean run

All three of Ben's classes verified in one pass, godmode off, fixture restored beforehand.

| Trait | Class | Evidence |
|---|---|---|
| Grass partner summons | Trainer | on board + `JELLY_ACID` prefab instantiated |
| Shielded by his Pokemon | Trainer | `19 -> 12`, expected 12 (`ceil(19*0.35)=7`), paired same-hit log line |
| Gotta Train 'Em (`cf_bond`) | Trainer | counter advances + proc line |
| Engorged | Vampiric | +8 max HP visible live: `60/60` -> `68/68` |
| Bat Swarm | Vampiric | gate at round 5; `BAT_VAMPIRE_01` prefab; "Vampire Bat" 18 HP on screen |
| Field Medic | Pacifist | predicted lowest-HP%% target healed exactly +4, twice, different targets |
| Why Can't We Be Friends | Pacifist | attacker loses `max(1, 50%%)` of damage dealt |

The Bat Swarm loop now advances the real combat round (`round_before=1 round_after=2 ...`), which is
what finally let `ONCE_PER_ROUND` reset and the gate open.

## L-RUN12 — Bat Swarm: the CONTENT is verified; the TEST regressed twice

Bat Swarm is confirmed working (runs 7/8/9): on-screen model, "Vampire Bat" 18 HP portrait card,
`BAT_VAMPIRE_016782 (CharacterGameObject)` via guid-targeted prefab lookup, and the full log chain
`CounterAdd cf_gorged=3` -> `proc SKILL_CF_VAMPIRIC_BAT_SWARM` -> `CounterSet cf_gorged=0`.

Runs 10-12 reported it FAIL/UNVERIFIABLE anyway, for THREE different instrument reasons in a row.
Recording these because each one is a trap that will recur:

1. **run 10** — the Vampiric fell below Engorged's own `HP_THRESHOLD GTE 70` gate as the fight wore
   on, so the counter stopped feeding. Fixed by resetting HP% each round.
2. **run 11** — `hp=68/None (?%)`: maxHp was read from `crucible_combat_snapshot`, whose rows have
   no maxHp field at all. Every percentage gate silently evaluated false. Fixed by reading maxHp
   from `/state?schema=v2` via `group0_hp_snapshot()`.
3. **run 12** — precondition finally satisfied (14/14 rounds at 68/68 = 100%), but Engorged procced
   exactly ONCE. `crucible_combat_restore_actions` let the Vampiric act repeatedly WITHIN one combat
   round, so the round counter never advanced and Engorged's `ONCE_PER_ROUND` budget never reset.
   **The action-restore added in fix 1 to make the loop reliable is what blocked the gate in fix 3.**

**The lesson worth keeping: a budget-limited recipe cannot be exercised by acting repeatedly inside
one round.** The loop must advance the real combat ROUND NUMBER, not just get the actor moving again.

Also fixed: an enemy attack that MISSES (`38 -> 38`) was being scored as a Why-Can't-We-Be-Friends
FAIL. A miss means no test happened -> UNVERIFIABLE with a retry, never FAIL.

## L0. Pokemon Trainer — HEALING AND DEATH (Ben, 2026-08-25) — SUPERSEDES the bee design

**Towns are the only place a partner is healed or revived. That is the class's downside.**

- Partner HP **PERSISTS between fights** — stored in `CustomData` on that partner's ball item
  (`Thing.CustomData`, the same idiom as the bee's `COOLDOWN` and the Voidwalker's XP).
- **NOTHING heals a partner mid-run** — not camp, not potions, not post-combat recovery.
- A partner at 0 HP is **DEAD and unusable** until a town.
- **A town both HEALS and REVIVES.**

**This DELETES the whole bee-mechanism plan.** No downed state, no per-turn regen proc, no
`IsSpecialFollower` patch, no `AS_FOLLOWER` route. Partners are ordinary combat-scoped allies that
simply die. Far less engine code, and it uses a system the game already has.

**Design consequence:** every fight becomes a real decision about whether to spend a partner's
health, and "which town do I detour to" becomes a strategic question. The Trainer fields a small
army and pays for it in logistics.

**TESTING BLOCKER:** the current fixture has every non-combat encounter stripped, so the
town-heal/revive loop CANNOT be tested on it. Needs a save with towns intact — the same blocker
section C hit. See section C.

**No stun anywhere in the class** (Ben: "too broken"). A 2-roll reliable stun on a free summon is
oppressive — `ONLY_STUN_ATTACK` is removed from the kit.

## L1. Pokemon Trainer — PARTNER BEHAVIOUR (approved design, Ben 2026-08-25)

**Approach A, team-wide.** Partners act autonomously; the Trainer issues TEAM commands.

**Why it is cheap: the engine already has the primitives.**
- `CombatHelper.cs:2238` grants an `AIComponent` ONLY to `GroupIndex == 1` summons — which is
  exactly why ally partners sit inert today. Granting the same to group-0 summons makes them act.
- `AIComponent.PriorityTargets` is a `Queue<string>` of GUIDs the AI drains BEFORE its normal
  target logic (`AIHelper.cs:520-537`) == "gang up on this one".
- `eAiTendencies` ships 26 behaviours incl. `FRONTROW`, `BACKROW`, `COLUMNSTACK`,
  `MUSTHAVELOWHEALTH`, `LEASTARMOR`, and for a status theme `MUSTHAVEPOISON` / `NOTHASPOISON`.

**The four commands** (Trainer abilities, non-damaging `ONLY_*` shape, one round, all partners):

| Command | Effect | Primitive |
|---|---|---|
| Focus Fire | all partners attack the named enemy | push GUID into each `PriorityTargets` |
| Spread Out | partners fan across the line | `COLUMNSTACK` / `FRONTROW` bias + AoE abilities |
| Afflict | favour status moves, prefer un-afflicted targets | `NOTHASPOISON` |
| Finish It | converge on the weakest | `MUSTHAVELOWHEALTH` |

With no command, partners fall through to their ability's authored `Tendency`, so each line still
behaves in character.

**Partners INFLICT statuses on enemies. They need NO defensive status kit of their own** (Ben:
"even if they only take damage, that's fine").

### THE ACCURACY ANSWER — invest in `ACC`, and keep status moves LOW-ROLL

`ADD_STATUS` lands only on a PERFECT roll, so a status class is an accuracy class. Measured:
- **PERFECT = every slot succeeded**, and the tooltip's perfect chance is literally
  `perSlot% ^ rollCount` (`CombatViewHelper.cs:3353-3357`).
  **=> FEWER ROLLS = DRAMATICALLY HIGHER STATUS CHANCE.** 1 roll at 80% lands 80%; 4 rolls at the
  same 80% lands 41%. Author status moves with 1-2 slots and modest damage; give pure-damage moves
  the higher roll counts. This is the mechanical difference between a "status move" and a "damage
  move" and it lines up with the Afflict command.
- **For COMPANIONS, `ACC` is the ONLY stat that matters.** Partners are `eCharacterTypes.COMPANION`,
  so `IgnoreEquipmentStats` is true (`CharacterHelper.cs:1580-1591`), and that path REPLACES the
  ability's roll stat with `ACC` outright rather than adding to it (`CharacterHelper.cs:787-798`).
  Whatever STR/AWR/TAL an ability is keyed to is DISCARDED for a companion. (Players differ: they
  get base-stat + ACC.)
- **CAVEAT:** the companion's config must actually expose an `ACC` entry in its `Stats` dict, or the
  path never triggers and it falls back to the declared stat with no ACC at all. Check per partner.
- **`CRT` is the WRONG investment here.** Crit is a separate roll that only fires AFTER a PERFECT
  (`CombatHelper.cs:1351`); it adds damage to a hit that already landed and does nothing to help
  land the status.

## L2. Pokemon Trainer — DESIGN DECISIONS AND TRAPS (researched 2026-08-25)

**Max player level is 9** (`CharacterHelper.cs:208-222`, hardcoded fallback). Only 4 Dungeon Crawl
side-adventures override it to 12 — clamp any spend to 9.

- [x] **DECIDED (Ben, 2026-08-25): the starter is GRANTED FREE at level 1, player's choice.**
      Points then come from levels 2-9 = 8 points, and the upgrade path costs exactly 8:
        starter tier 1->2->3          = 2
        second partner unlock->2->3   = 3
        third partner unlock->2->3    = 3
      No spare, no shortfall. Every level-up is a real choice because points can always deepen one
      partner instead of unlocking another.
- [x] **POINTS ARE A FREE BUDGET, SPENDABLE IN ANY ORDER** (Ben, 2026-08-25). Not a fixed path.
      **1 point buys EITHER an unlock OR one tier-up on any partner.** Starter is free at tier 1.
      Measured: a fresh character reads `level=0` and the HUD prints the raw number unmodified
      (`CharacterHudController.cs:359`, no +1), so the range is 0->9 = **NINE points**.
      Nine points against ELEVEN possible purchases (2 unlocks + 3 tier-ups x 3 partners), so the
      player is always giving something up. Example builds:
        three at tier 3 + one promoted to final form   = 2 unlocks + 6 tiers + 1 = 9
        two partners both maxed to final form          = 1 unlock + 3 + 3 = 7, 2 spare
        wide and shallow                               = 2 unlocks early, 7 tiers spread
      **KNOWN CONSEQUENCE:** a PURE solo build cannot absorb the budget — one partner holds only 3
      tier-ups, so after 3 points you must unlock someone. If solo-focus should stay viable to
      level 9, the alternative is escalating tier costs (1/2/3, maxing one costs 6) — but that
      makes three-at-tier-3 unaffordable. Flat was chosen because it fits both things Ben asked
      for ("three Pokemon at level three each" AND "or just level up that same Pokemon").
- `GameRunData.PlayerFollowers` is `Dictionary<string, FollowerState>` keyed by PLAYER GUID —
  **exactly one follower per player** — and `CombatPhase.cs:5690-5693` refuses a second summon while
  one exists. The follower route can NEVER give multiple simultaneous partners.
- `Configs.Followers` has **NO PACK MERGE PATH** (`MergePlanner.cs:38-42` merges only Characters,
  Things, Abilities, Statuses).
- A missing Followers entry **degrades SILENTLY**: `CharacterHelper.cs:1794-1801` leaves
  `CharacterType = STANDARD`, `TypeArgs = null`, and the partner becomes a plain disposable ally
  with no error. Highest-severity silent no-op in this design space.
**Use combat-scoped allies (`SPECIFIC`) + HP carried in `CustomData["CF_POKE_HP"]`** — write on
combat end, restore on summon. Multiple partners, persistent HP, never touches `PlayerFollowers`.

- [x] **Summon-and-attack is SHIPPED and data-only.** `INANIMATE_POWDERKEG_EXPLODE` already carries
      `ADD_CHARACTER` + `CHANGE_STAT` in one ordered action list.
- [x] **`cf_bond` persistence LANDED.** `COUNTER_ADD` now has a `Persistent` flag writing through to
      `CharacterComponent.CustomData` under `CF_COUNTER_`, and `GOTTA_TRAIN_EM` sets it. The old
      "counters reset every fight" note is SUPERSEDED — if bond still fails to reach 3, it is a
      different bug.
- [ ] **`SELF_LEVEL` condition token** — needed to gate a recipe on THIS character's level. Today
      only `PARTY_AVG_LEVEL` exists. ~8 lines across Vocabulary/Abstractions/GameAdapters/Evaluation.
      `GetStat("XP")` is NOT a backdoor — `CharacterHelper.GetStat` never reads `Thing.StackCount`
      and would silently return 0.

### Silent-no-op traps to check before writing content
1. `AS_FOLLOWER` with no Followers entry -> plain ally, no error.
2. `skillrecipes.json` ids are **never collision-checked** (`MergePlanner` covers only Characters,
   Things, Abilities, Statuses) — a duplicate recipe id just loses.
3. `RANDOM` summons require a visual tier record (`CharacterHelper.cs:2312`) or the candidate is
   filtered out and NOTHING is summoned. **CORRECTION (2026-08-25): `visualfallbacks.json` does NOT
   fix this.** That file is ClassForge's *equipment*-visual remap (pack item id -> donor Thing id,
   consumed only by `EquipmentVisualPatches.GetITMEquipmentPrefab_Prefix`). The thing `RANDOM` needs
   is `CharacterVisualHelper.GetCharacterTierRecord`, a **Unity `dCharacter` asset-index lookup**
   (`dObjectHelper.Index.dCharacter.GetRecordByName`) that no JSON pack can add to. **A
   pack-authored creature can therefore NEVER be reached by a `RANDOM` summon. Use `SPECIFIC`** —
   `CombatHelper.TryCreateSummon`'s `case eSummonTypes.SPECIFIC: text = pSummonTarget;` skips
   `GetActorsByTags` entirely.
4. `GetActorsByTags` clamps level to 7 — tag-based tiering silently stops scaling exactly where
   tier-3 partners live.
5. **A pack creature's CONFIG ID decides whether it has a model at all.**
   `GetCharacterTierRecord` (`CharacterVisualHelper.cs:219-238`) resolves the prefab by NAME:
   `TryGetNextConfigValueName` keeps the id's prefix and only varies the trailing two digits
   (starting at `suffix / 2` and walking down, then up). A wholly invented id like `CF_POKE_WOLF_00`
   matches no `dCharacter` record at any suffix, so the creature is real, alive, targetable and
   **invisible**. Name pack creatures `<SHIPPED_FAMILY>_<free two-digit suffix>` so the walk lands on
   the family's real prefab.
6. **The config id's trailing digits also set the tier of every status the creature applies.**
   `InteractableHelper.cs:1114` calls `GetStatusNameForLevel(status, GetValueFromConfigName(configName))`
   for any non-`PlayerComponent` origin, and `ProgressionHelper.GetTierFromLevel` is
   `clamp(level/2, 0, 3)`. So `..._08` makes even a "stage 1" partner apply MAX-tier burn/bleed.
   Pick suffixes `01 / 03 / 05 / 08` for stages 1-4 to get status tiers `0 / 1 / 2 / 3`.
7. **A creature's ABILITIES come from `UNARMED_<its config id>` in Things**, not from the character
   config (`EquipmentHelper.GetCharacterUnarmedThingName` / `GetUnarmedWeapon`, which walk the
   suffix down and finally fall back to the plain `"UNARMED"` Thing). Every shipped creature has an
   empty `Things: {}` and an empty `Passives: []` — the kit lives on that weapon's
   `Interactable.Abilities`, whose `SkillRollData` (`MinValue`/`MaxValue`/`ACC`/`Stat`/`Rolls`)
   is the per-creature tuning surface for SHARED, shipped ability configs. Reusing shipped ability
   ids is strongly preferred: a new ability id has no `dAbility` record either.
8. **`GetMinAttackScaleValue` returns 0 for anything without `PlayerComponent`**, so a creature's
   damage band is always `0 .. MaxValue x ATK`, lerped by `RollResultData.Value`
   (= successes / rolls). And because a player-side `SPECIFIC` ally is `GroupIndex 0` +
   `CharacterType STANDARD`, `IgnoreEquipmentStats` is **false** for it (unlike an enemy) — so the
   roll stat is NOT silently rewritten to `ACC`. Give partners no `STR`/`VIT`/`INT`/`AWR`/`TAL`
   stat (exactly as shipped creatures do) and the roll chance stays `(ACC + ability.ACC) / 100`,
   because `GetCombatStat`'s `pForAbility` branch adds `ACC` on top of any BaseStat. Naming `SPD`
   as the roll stat WOULD double-count, since creatures do carry `SPD`.
9. `ExtraLevel` scaling is inert in normal play: `CharacterHelper.cs:444` only applies it when
   `GetEnemyMaxLevel() > 7`, and that returns 7 unless the adventure overrides `MaxEnemyLevel`.
   Authored partner stats therefore stand exactly as written.

## K. Campaign integration

The mod's rules must apply to EVERY campaign, and normal campaign play must still work.

- [x] Rules are GLOBAL, not per-save — the grid rotation and 6x2 board are BepInEx config
      (`[Combat] RotateDioramas`, `VenueGridPreset`), so they apply to Age of Rebellion, Age of
      Omus and Challenge Modes alike, including brand-new campaigns. Nothing is keyed to a save.
- [ ] A NEW campaign starts and plays correctly with all patches injected (`ftk2_new_game`, which
      also closes the never-exercised section A item).
- [ ] An existing campaign still loads and plays normally.
- [ ] Dungeon runs unaffected — `RotateDioramasInDungeons` ships OFF because `OngoingVenues` is
      save-persisted and dungeon rooms are corridor-chained; a swap there can break a floor.
- [ ] Boss/vehicle arenas keep their own venue (KRAKEN/OCEAN are exempt by name).

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
- [x] Engorged / Bat Swarm / Field Medic / Why Can't We Be Friends — ALL FOUR isolated and verified in run 13 (16 PASS / 0 FAIL of 17)
- [x] Three elemental partners — **AUTHORED 2026-08-25** as GRASS / WATER / FIRE, not the old
      Chaoshound/Hellhound/Serpent naming. 8 new recipes (WATER_1..4 -> JELLY_BLUE_00,
      BOSS_MERLING_CHAMPION_03, BOSS_WALLOPER_04, ELEMENTAL_WATER_08; FIRE_1..4 -> JELLY_RED_01,
      DEMON_MELEE_03, BOSS_WITCH_EMBER_04, BOSS_DEMON_ELITE_08), wired into CF_ORIG_TRAINER.Passives.
      PackCheck recipes 15 -> 23, zero Errors. Every config id verified against the game's shipped
      Characters.json (2126 entries) by re-implementing `TryGetNextConfigValueName`'s suffix walk.
      HP low + tier-weighted (16/26/37/48). NO STUN anywhere. Still needs IN-GAME visual verification
      at bond 3 / 6 / 9, which needs a seeded `cf_bond`. Original line follows: authored and
      validated; only the first is verified firing
- [~] `visualfallbacks.json` — PARTIALLY confirmed 2026-08-26. What was actually seen on screen
      across four live fights: all five pack CLASSES rendered as proper humanoids carrying their own
      starter weapons (rods/staves/polearm visible in hand), the Trainer's partners rendered as
      JELLY CUBES and not as humans (the specific failure AGENT-BRIEF §5 warns about), and a
      captured `BAT_CAVE_00` rendered with a real bat portrait in the ally HUD. No missing-art
      throw, no placeholder, no invisible combatant. NOT a full audit — only the ids that happened
      to appear were seen, so this is not yet a clean tick for every fallback entry.
- [x] 31 `CF_EOR_*` classes: config, passives, class applied, weapon, abilities, loc — **30/31**
- [x] Re-run the sweep post-merge for the Runemage weapon fix — DONE 2026-08-25: **31 passed, 0 failed, of 31** (see section N). Runemage CONTENT DECISION still open.

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

- [x] Crucible.Core 329 · DevKit 117 · ClassForge Core 44 · **Recipes 333 (UNVERIFIED at 2026-08-25 — suite did not build mid-edit; Crucible 329, DevKit 117, Core 44 and PackCheck-zero-Errors all RE-CONFIRMED green)**
- [x] PackCheck zero errors
- [x] New validator rules: `E_SUMMON_TARGET`, `E_VALUE_SOURCE_SCOPE`, `E_VALUE_SOURCE_NO_PERCENT`
- [x] LiveDataHarness (offline config validation) — **BUILT. The "NEVER BUILT" text that stood here
      was STALE and contradicted line ~623 of this same file.** Corrected 2026-08-26 against the
      actual tree: `FTK2.DevKit/sandbox/LiveDataHarness/` exists with `LiveDataHarness.csproj`,
      `Harness.cs`, `Checks/`, `GameData.cs`, `GameVocabulary.cs`, `AuthoredContent.cs`, `Report.cs`
      and `fixtures/`, and `tools/run-harness.ps1` exists too — both of the things the old note said
      did not exist. Treat the 2026-08-25 verification in the old text as superseded.
      This is the check that would catch, offline in seconds, the failure class that keeps shipping
      invisible content here: a `CharacterConfig` that does not exist, a status id that is not a real
      `Configs.StatusEffects` key, a custom status id lacking a vanilla group prefix, and
      `CF_LIVE_ID_COLLISION` refusals that mean an edited file is never loaded.

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

---

## RUN 3 — 2026-08-26 — determinism, verification method, and the fixture bench

Governing criterion set by Ben: **classes playable, game does not break, everything FAILS SAFELY.**
Correct magnitudes are nice-to-have; not crashing / not wedging / not desyncing are the deliverables.
MP scope narrowed by Ben: **do not desync on summons** — not strict MP everywhere.

### New reference docs (mindex `projects/ftk2-mods/` + repo mirrors)
- [x] `AGENT-BRIEF.md` — the global instruction set; every dispatch must cite it. Now also carries
      §5c-2 (ONE agent drives the game; contention tell-tales), §5d (process checks that lie),
      and two new §3 silent-success traps.
- [x] `VERIFICATION-METHOD.md` — **the paired-evidence rule**. proc+visible=PASS ·
      proc+invisible=**FAIL (invisible mechanic)** · no-proc+visible=**FAIL** · neither=didn't fire.
      Plus the tells taxonomy (>=2 per scenario, one MUST be identity), tile reading, fixtures,
      seed-pinning, fail-safety sweep, two-run determinism self-check.
- [x] `MULTIPLAYER-RULES.md` — 21 numbered rules derived from a **decompile of the confirmed-working
      EOR 0.7.0.66 DLL** (33,752 lines), with a section naming where our mod sits OUTSIDE EOR's
      proven envelope.

### Determinism (Wave 1) — suites green
- [x] `_dioramaTurn` process-static → replicated encounter identity (`EncounterIdentity` +
      `ReplicatedEncounterKey`). Was a LIVE bug, default ON: peers with different local fight counts
      loaded **different battlefields**.
- [x] `AiDrawNeutrality` — silent per-decision fallbacks replaced by a one-way `HardDisable` latch.
- [~] Per-peer SafeMode fork — **PARTIAL.** Neutrality gate decoupled and a unilateral degrade is now
      announced loudly, but a one-sided SafeMode still stops the recipe engine on that peer, which
      forks the count on its own. A full fix needs a broadcast channel ClassForge does not have.
- [x] `ENCOUNTER_GUID` re-pointed off raw GUIDs; verified `AdventureState.EncounterGUID` is NOT
      replicated (joining peers regenerate entities from `MapGenSeed`).
- [x] Unstable `OrdinalIgnoreCase` sort feeding an RNG index → Ordinal tiebreak.
- [x] `AdditionalRoots` parity reports sorted pack IDs, not a filesystem path.
- [x] `ProcChance`/`AiProcChance` straddle rule VERIFIED present (`W_PROC_CHANCE_DRAW_FORK`);
      **0 shipped recipes trip it.**
- [x] Summon determinism audited clean + 6 draw-count tests. `SUMMON` planning costs **zero** draws,
      so a summoning recipe's entire cost is its proc gate.
- [x] `CFRandom`/`SplitMix64` side-stream DELETED — it was the wrong build; the codebase already
      rolls from the shared stream. `EntityKey` kept as the peer-stable identity.

### Assertion surface — the gap that made features unmeasurable
- [x] state-v2 now reports per combatant: `tile{x,y}`, `groupIndex`, `ordinal` (roster index),
      `isSummon`, `isTile`, `customData{}` (ALL `CF_*` keys, emit-all not allow-listed), `things[]`.
- [x] **`combat.tiles[].auraStatuses` — tile effects are readable in state for the first time.**
      The Chaos Mage hazard tile was screenshot-only until now.
- [x] Redactions for process-local GUIDs so they cannot read as false desyncs.
- ⚠️ **v2 digests MOVED.** Every pre-2026-08-26 v2 baseline is stale.

### Harness instruments — two were BROKEN and would have faked results
- [x] **`crucible_pin_seed` did nothing.** `GameRandom.Seed` is `readonly` and only a RECORD; the
      live generator is a private `System.Random` built once in the ctor. Pinning wrote a label and
      changed **zero** draws. Fixed to replace the generator — then **VERIFIED live**: same seed
      byte-identical over 20 steps, different seed diverges from step 2.
- [x] `PartyAccess.ReadMember` static binding restored (a de-noising fix had dropped `BindingFlags.Static`,
      silently breaking every static lookup incl. `Env.Configs`).
- [x] `crucible_ui_matches` / `crucible_ui_click_nth` — index/document-scoped clicking, so elements
      with empty text can be addressed by identity. Same forbidden-guard, match report before acting,
      loud out-of-range with no fallback to index 0.
- [x] `load-game-btn` added to `UiForbiddenElements` — the guard covered only two of the three
      controls the safety rule names.

### SAFETY — two near-misses of the same class, both reaching a resume control unnamed
- [x] `make-fixture.ps1` carried `ui_nav down` + `ui_submit -` on the screen showing `continue-btn`
      and `load-btn`. A blind submit could have resumed the live co-op campaign; the guard cannot
      catch it because no selector is named. **Deleted**, DO-NOT-RESTORE comment added.
      (`UiCommands.cs:211-215` records the FIRST instance: selector "Continue" substring-matched
      `continue-btn` and resumed a campaign.)
- [x] `make-fixture.ps1` used `param($Args)` — a PowerShell **automatic variable** — so `-Args` never
      bound and every RPC went out with zero arguments while reporting progress.
- No save was ever resumed: `run.present` never true; pristine backups intact.

### Fixture bench (Wave 0.5)
- [x] `bench.py` + 9 scenario manifests + format spec. 78 offline self-tests green.
      Verdict rule enforced: a proc with no reviewed screen half returns **`PENDING-EYES`, never PASS**.
- [x] Adventure-selection navigation SOLVED and mapped (categories clickable without dismissing the
      save panel; 9 `adventure-art-holder` tiles; non-centred index moves the carousel; detail screen
      forward control is `next-btn` text `'Select Adventure'`).
- ⚠️ **`difficulty-inp` does not exist on 1.14.6.** Stepper is `previous-btn`/`next-btn` + `text-value`,
      and **the CONFIRM button shares the name `next-btn`** — step with `previous-btn` or disambiguate
      with `ui_matches`/`click_nth`.
- ⚠️ `crucible_set_custom_data` is NOT in the deployed command list → manifest `custom_data` setup ops
      report SETUP-FAILED rather than arming a gate.
- [ ] **Fixtures built: 0. Scenarios run: 0. Abilities fired: 0.** All five classes UNVERIFIED
      against the post-determinism build. In flight.

### Still open from Ben's explicit asks
- [ ] Campaign integration (§K) — new campaign, existing campaign load, dungeons, boss/vehicle arenas
- [ ] DEFEAT / party-wipe latch (only VICTORY has ever latched)
- [ ] Multi-wave combat across a wave boundary
- [ ] Extended diorama camera framing (known defect; both mitigations default OFF while preset=`large`)
- [ ] Partner selection ("pick which partner is sent in")
- [ ] Ash's T2–T4 evolutions; partner command CONSUMERS; Gary's one-minion limit
- [ ] **COMPANION-follower migration** — `PlayerFollowers` is NOT `[JsonIgnore]` while `CombatState`
      IS, so a follower is vendor-replicated party state and a mid-combat summon is not. Expressing
      partners as COMPANION followers would inherit MP safety by construction. Caveat: nobody has
      verified a COMPANION can be recruited MID-COMBAT.
