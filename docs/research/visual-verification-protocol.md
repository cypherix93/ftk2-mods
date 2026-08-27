# Visual verification protocol

**A feature that exists in state and is invisible to the player does not work.**

This is not a style preference. It is the single most repeated failure in this project. Every one of
these was reported as working on the strength of a state readback, and every one was invisible on
screen:

| Feature | State said | Screen said |
|---|---|---|
| Summoned wolf | `class=WOLF_FOREST_03 hp=36 alive` + working HP tooltip | no model anywhere on the board |
| Wide combat grid | `group0=24 tiles` | tiles not drawn, not clickable |
| Diorama rotation | `battlefield GRASSLANDS -> CASTLETHRONE` in the log | Grasslands still on screen |
| Engorged | `hp=68 maxHp=68 [STATUS_VIGOR_CF_ENGORGED]` | portrait read 60, no icon — **RESOLVED**: the id had to start with a vanilla `eStatusEffectsGroups` member, and the HUD only repaints on `_refreshSelectedCharacterBar`. After a forced refresh: bar `60/68`, icon present. |
| Recipe engine | zero `proc` lines in the log | engine was fine; the LOG LEVEL was filtered |
| Tile counts | `ally=8 enemy=8` | read during the loading screen; stale |
| DEFEAT latch | `ended=False` after 30s | party dead, "ADVENTURE FAILED" on screen |

Four of those I reported as working. The owner caught them.

---

## The gate

A feature may not be marked `[x]` until **all five** steps pass. Steps 3 and 4 are the ones that
get skipped, and they are the ones that matter.

1. **State readback.** Necessary, never sufficient. A command's own success string is not evidence —
   several verbs in this harness have reported success while invoking nothing.
2. **Name the on-screen element IN ADVANCE.** Before running anything, write down the exact thing
   that must change on screen: which number, which icon, which model, which tile. Deciding
   afterwards is how you end up reading whichever number happened to move.
3. **Screenshot it** via the MCP screenshot route (`ftk2_screenshot`, or `drive._post("/screenshot")`
   — it is a dedicated route, NOT an `/exec` command; there is no `crucible_screenshot`).
4. **OPEN the screenshot and describe what is actually in it**, including whether the element from
   step 2 is present. "A screenshot was captured" is not verification. If you cannot see the element,
   the feature is NOT VERIFIED — say so, whatever the state says.
5. **Reconcile.** If state and screen disagree, the SCREEN WINS and the feature is not done.

---

## What "visible" means, per feature type

Decide this at step 2. Do not improvise it after the fact.

| Feature type | The element that must be visible | Common way it fails invisibly |
|---|---|---|
| **A summon / new combatant** | its MODEL on a tile, plus its portrait/nameplate | entity exists with no `ActorGameObject`; alive, tooltipped, drawn nowhere |
| **A status effect** | its ICON on the character's portrait AND its effect on the visible number | status applied in state, no icon authored, number not refreshed |
| **A stat change** | the number in the character's own HP/stat bar — **select that character first**, the bottom bar shows the SELECTED one | reading another character's bar, or a portrait that shows a different value than the bar |
| **A grid / tile change** | tile OUTLINES on screen, and a character actually MOVED onto a new tile | tiles counted in state but on the `DoNotRender` layer |
| **A venue / arena swap** | the ARTWORK of the new venue | the name written to state after the venue already resolved |
| **An ability firing** | the damage/heal number on screen, or the animation | ability "used" with `resultCount=0`, or the actor had `pa=0` and it never executed |
| **A recipe / trait proc** | the `proc <id>` line in the log **and** the on-screen effect | the proc line logs at **Debug**, which BepInEx filters out by default |
| **An item equipped** | the item in the slot and the stat delta in the bar | item refused at merge (`CF_LIVE_ID_COLLISION`) and silently not applied |
| **A run outcome** | the summary screen text (`ADVENTURE FAILED` / complete) | a blocking dialogue holds `_endAdventure`, so nothing latches |

---

## Settings that DESTROY a measurement

Check these before running, not after a confusing result.

- **Godmode ON** tops HP up every tick. It makes any HP-affecting test meaningless — heals, costs,
  lifesteal, overheal, damage. **Turn it off for anything touching HP.**
- **`BepInEx.cfg` `LogLevels` without `Debug`** silently drops every recipe `proc` line AND makes
  ClassForge's own `VerboseLogging = true` a no-op. Add `Debug` to both sinks.
- **`crucible_combat_restore_actions` immediately before `crucible_combat_end_turn`** refills the
  actions the end-turn must zero, so END_TURN never fires.
- **Measuring while `TransitionUIDocument` / `LoadingScreenUIDocument` is up** returns the PREVIOUS
  fight's state. Wait for the venue (`venue_on_screen()` in `rotate_check.py` / `lifecycle.py`).
- **MATCH THE INSTRUMENT TO THE EFFECT.** `crucible_kill_target` FORCES the target to 1 hp before
  firing, by design — so it can prove a KILL-gated trait and can NEVER prove damage mitigation,
  because the victim dies whatever the shield does. Use natural AI damage for mitigation, real
  killing blows for `ON_KILL`, and turn godmode OFF for anything touching hp.
- **When a model is too small or occluded to identify by eye, ask the game what it INSTANTIATED.**
  `crucible_get RouterHelper.Env.VenueGameObjectMaps.FromCharacter` names the actual Unity
  GameObject per entity (e.g. `JELLY_ACID_016777 (CharacterGameObject)`). That is stronger evidence
  than any stat readback — it is the real prefab — and it settles "did the right art load".
- **REDIRECT STDERR, NOT JUST STDOUT.** A battery run terminated with no tally and no traceback
  because only stdout was captured. The crash was invisible for a full cycle. Always run the driver
  with stderr merged into the same log (`2>&1` / `-RedirectStandardError`), or a crash looks
  identical to a hang.
- **`crucible_combat_snapshot` rows carry NO `maxHp`.** Only `/state?schema=v2` exposes it. Reading
  HP from the snapshot and max HP from nowhere yields `hp=68/None (?%)`, and every percentage gate
  built on it silently evaluates false — which then reports a WORKING feature as unverifiable. If a
  percentage gate never fires, check that the denominator was ever read at all.
- **ASK THE ENGINE FOR THE PAIRED MEASUREMENT BEFORE BUILDING A NOISY ONE.** Testing SHIELDED by
  comparing one mitigated hit against one unmitigated hit is confounded: the two samples came from
  different abilities (`GOLEM_ICE_SINGLE_ATTACK` 16 vs `BOW_BASIC_ATTACK` 19), so the ordering
  holding proved almost nothing. ClassForge already logs the SAME hit before and after:
  `DAMAGE_TAKEN_MULT adjusted incoming damage 14 -> 9 for <guid>`. That is a paired measurement with
  zero variance between the two numbers, and six of them matched -35% exactly. When a feature
  transforms a value, look for a log line carrying BOTH sides of the transform before constructing a
  statistical comparison out of separate events.
- **A counter a recipe RESETS cannot be polled.** `cf_gorged` was asserted by sampling once per round
  for `>= 3`; the Bat Swarm recipe consumes it to 0 in the same proc that the gate opens, so the
  value 3 never survives to a sample. It reported FAIL for a working feature. Assert consumed
  counters from the log (`CounterAdd ... value=3` -> `proc <recipe>` -> `CounterSet ... value=0`).
- **Wiping the enemies to isolate an effect** ENDS the fight; the resulting HP jump is post-combat
  recovery, not your feature. Leave one enemy alive and neutralise its turn instead.
- **The HUD does not repaint when the HARNESS changes state out-of-band.** A status applied via
  `crucible_status_add` is real in state but the bar and icon strip keep their old paint until
  something calls `_refreshSelectedCharacterBar` (`CombatPhase.cs:3279` — selecting an ability does
  it). Force a refresh before screenshotting, or you will photograph a stale HUD and conclude a
  working feature is broken. This is a TEST artifact: in normal play the native ability flow
  refreshes afterwards.

---

## Two tells that you are about to be wrong

- **Two numbers in the SAME reply disagree.** `killed=0 alreadyDead=0 changed=False` next to
  `dead=True` meant the read was stale. Stop and open the screenshot.
- **A fix you applied, deployed and verified changes nothing.** That is evidence against the
  diagnosis, not a reason to retry. The Runemage `CLOTH -> WOOD` edit was correct and irrelevant —
  the pack's copy of the item was being REFUSED at merge, so the edited file was never loaded.

---

## Per-feature template

Copy this into the checklist entry for every new feature.

```
### <feature>
- [ ] 1. State readback:      <command> -> <expected field/value>
- [ ] 2. On-screen element:   <the exact number / icon / model / tile that must change>
- [ ] 3. Screenshot:          <path>
- [ ] 4. Opened and saw:      <what is actually in the frame, including whether (2) is present>
- [ ] 5. Reconciled:          state and screen agree / DISAGREE -> not done
- Measurement hazards checked: godmode off? Debug logging on? venue loaded? correct character selected?
```
