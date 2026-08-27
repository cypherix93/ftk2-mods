#!/usr/bin/env python3
"""Close the remaining COMBAT-lifecycle items on docs/research/test-checklist.md (sections B, D, F).

Checks, in the order they run and WHY that order:

  1. CHAOS_FREEZE   -- overworld, before any combat. Cheapest way to prove chaosHistoryCount holds
                        steady while frozen: drive a few real end-turns and re-read
                        crucible_chaos_state, no fight required at all.
  2. ABILITY_AUTO    -- first thing done in fight 1. Needs a live target on the board, so it runs
                        before anything else touches the enemy group. FIXED 2026-08-25: the VERB
                        (crucible_use_ability_auto) was always correct -- it ranks and fires a real
                        ability for whoever CURRENTLY HOLDS THE TURN. The TEST was wrong: it asserted
                        on whoever merely happened to be active the instant it ran, which can
                        legitimately have nothing usable (cooldowns, a support kit with only
                        ONLY_*-shaped abilities). select_actor_with_real_ability() now checks
                        crucible_list_abilities and cycles crucible_combat_end_turn until the active
                        entity actually has a usable non-FLEE/SKIP_TURN/BASIC_MOVE/EQUIP_WEAPON/
                        BASIC_RELOAD ability before the auto-fire is asserted against it.
  3. MULTI_WAVE      -- still fight 1. Wipes the enemy group (group 1) via crucible_combat_wipe_enemies
                        and watches whether the game replenishes it (a second wave) rather than ending
                        the fight. combat.round is NOT monotonic (drive.py / battery.py's own warning:
                        it starts at -1 and resets per wave), so the pass condition is combatant count
                        CROSSING BACK UP after hitting zero, not round.
  4. END_PHASE       -- last thing done in fight 1, on purpose: crucible_end_phase("combat") forces
                        the CURRENT combat closed (CombatPhase._debugEndPhase), so it has to run after
                        everything else that needs fight 1 still open.
  5. DEFEAT LATCH    -- fight 2, and it runs LAST in the whole module because a defeat ends the run.
                        Drives the party (group 0) to zero via crucible_combat_wipe_enemies, then
                        independently re-reads crucible_combat_snapshot and asserts every group-0
                        combatant (including any summoned ally) is dead=True -- retrying the wipe once
                        to cover a Revive Ally-style effect undoing the first attempt -- BEFORE ever
                        polling crucible_endadventure_watch. Only asserts the latch once the wipe
                        precondition is independently confirmed; never on route, which lands on
                        ADVENTURE_SELECTION for both outcomes.

Every assertion reads independent state back from the game. A command's own success string/LastResult
text is never the evidence -- several verbs in this harness have reported success while invoking
nothing (see battery.py, verbs.py). Screenshots are the dedicated /screenshot route, never an /exec
command (shot() is verbatim from battery.py/verbs.py).

crucible_chaos_advance is NEVER called here (empty-arg call nulls ChaosState -- see verbs.py's SAFETY
note); this module only freezes/reads chaos, never advances its stage.

This module never clicks or names continue-btn, load-btn, or load-game-btn, and never text-matches on
"Continue" -- only exact element names, matching drive.py's DISMISSABLE convention.
"""

import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive       # noqa: E402
import to_combat    # noqa: E402

RESULTS = []
UNVERIFIABLE = []
SHOTS = []


def shot(label):
    """Capture a screenshot and remember it. Verbatim from battery.py / verbs.py.

    Screenshots are a DEDICATED HTTP ROUTE (/screenshot), not an /exec command -- calling
    `crucible_screenshot` through exec returns HTTP 400 Bad Request, because no such command is
    registered. A visual claim is not verified until someone OPENS one of these; the harness can
    only prove it was captured.
    """
    try:
        out = drive._post("/screenshot", {})
    except Exception as exc:
        print("        (screenshot failed: %s)" % exc)
        return None
    path = ""
    if isinstance(out, dict):
        path = out.get("path") or ""
    else:
        for line in str(out).splitlines():
            if ".png" in line:
                path = line.strip()
    SHOTS.append((label, path))
    print("        shot[%s] %s" % (label, path[-64:] if path else "(no path)"))
    return path


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(("  PASS " if ok else "  FAIL ") + name + (("\n        " + detail) if detail else ""))


def unverifiable(name, reason):
    """For a check whose precondition genuinely was not met right now (e.g. no active chaos, a
    single-wave spawn). Not a pass, not a fail -- reported honestly instead of inventing a pass."""
    UNVERIFIABLE.append((name, reason))
    print("  UNVERIFIABLE " + name + "\n        " + reason)


# ------------------------------------------------------------------ combat snapshot parsing
# Same shape as battery.py's combatants(); extended with the header's totalRounds/waveIndex, which
# battery.py never needed and this module's multi-wave check depends on.

def snapshot_text():
    return drive.run("crucible_combat_snapshot")


def combatants(text=None):
    """guid -> parsed row, for every actual combatant (not the header, not a tile)."""
    out = {}
    for line in (text or snapshot_text()).splitlines():
        if " name=" not in line or " hp=" not in line:
            continue
        guid = line.strip().split()[0]
        row = {"line": line.strip()}
        for key in ("class", "group", "hp", "pa", "sa", "dead"):
            m = re.search(key + r"=(\S+)", line)
            if m:
                row[key] = m.group(1)
        out[guid] = row
    return out


def wave_header(text=None):
    text = text or snapshot_text()
    m = re.search(r"totalRounds=(\S+) waveIndex=(\S+) activeGuid=(\S+)", text)
    if not m:
        return {"totalRounds": None, "waveIndex": None, "activeGuid": None}
    return {"totalRounds": m.group(1), "waveIndex": m.group(2), "activeGuid": m.group(3)}


def group_count(group, text=None):
    rows = combatants(text)
    return len([r for r in rows.values() if r.get("group") == str(group)])


def usable_real_ability(entity_guid):
    """True if entity_guid currently has at least one usable ability other than the five built-in
    non-action verbs -- FLEE/SKIP_TURN/BASIC_MOVE/EQUIP_WEAPON/BASIC_RELOAD, exactly the exclusion
    list crucible_use_ability_auto itself applies (CombatDriveCommands.cs's own doc comment on
    CrucibleUseAbilityAuto). Read from crucible_list_abilities, never assumed."""
    excluded = {"FLEE", "SKIP_TURN", "BASIC_MOVE", "EQUIP_WEAPON", "BASIC_RELOAD"}
    listing = drive.run("crucible_list_abilities", [entity_guid])
    for line in listing.splitlines():
        line = line.strip()
        if not line or " usable=" not in line:
            continue
        name = line.split()[0]
        if name in excluded:
            continue
        if "usable=True" in line:
            return True
    return False


def select_actor_with_real_ability(max_turns=12, settle=1.5):
    """Ends turns (crucible_combat_end_turn) until the ACTIVE entity has a usable ability other than
    the built-in non-action verbs.

    crucible_use_ability_auto always acts on whoever CURRENTLY HOLDS THE TURN -- it never picks who
    to act as. The verb itself is correct (it ranks and fires a real ability for that entity); the
    bug was in this test asserting on whoever happened to be active the instant it ran, which can
    legitimately have nothing usable (every ability on cooldown, a support kit with only
    ONLY_*-shaped abilities, etc.). This finds a turn where the active entity DOES have something
    real first. Returns the qualifying entity's guid, or None if no turn within max_turns qualified.
    """
    for _ in range(max_turns):
        guid = wave_header()["activeGuid"]
        if guid and usable_real_ability(guid):
            return guid
        drive.run("crucible_combat_end_turn", [])
        time.sleep(settle)
    return None


# ------------------------------------------------------------------ getting into a fight
# Mirrors to_combat.py's own sequence (boot -> load -> clear gates -> answer rewards -> spawn ->
# press Fight -> wait) rather than re-deriving it; to_combat.py already paid for finding this order.

def load_fixture():
    transcript = drive.load_run(to_combat.DEFAULT_RUN)
    if "FAILED" in transcript:
        print("load refused (%s); restarting once" % transcript.splitlines()[-1])
        if not drive.restart_game():
            return False
        transcript = drive.load_run(to_combat.DEFAULT_RUN)
        if "FAILED" in transcript:
            print(transcript)
            return False
    drive.run("crucible_tutorials_suppress", [])
    drive.clear_gates()
    drive.pick_reward()
    ready = drive.wait_ready()
    if not ready["ready"]:
        print("not interactive (%s); restarting once" % ready.get("reason"))
        if not drive.restart_game():
            return False
        if "FAILED" in drive.load_run(to_combat.DEFAULT_RUN):
            return False
        drive.run("crucible_tutorials_suppress", [])
        ready = drive.wait_ready()
        if not ready["ready"]:
            return False
    return True


def gather_party():
    """Teleport the whole party onto the active character's hex, exactly as to_combat.main() does,
    so a spawn pulls in every class rather than whoever is standing where they last stopped."""
    state = drive.run("crucible_overworld_state")
    lead = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", state)
    if lead:
        drive.run("crucible_party_set_hex", [lead.group(1), lead.group(2)])
        drive.wait_until(drive.interaction_enabled, timeout_seconds=6, interval=0.3)


def venue_on_screen(timeout_seconds=90):
    """Waits until the BATTLEFIELD is actually rendered, not merely until combat reports active.

    wait_for_combat returns as soon as CombatState says active, which happens while the
    "Travelling to a new area..." parchment is still up. Anything measured there reads the
    PREVIOUS fight's state. That produced a bogus DEFEAT pass: the wipe reported
    `killed=0 alreadyDead=0 changed=False` while the snapshot claimed both party members were
    already dead -- because the snapshot was stale and the screenshot was a loading screen.
    """
    def ready():
        docs = drive.visible_docs()
        if "TransitionUIDocument" in docs or "LoadingScreenUIDocument" in docs:
            return False
        return "CombatUIDocument" in docs or "VenueUIDocument" in docs

    ok = drive.wait_until(ready, timeout_seconds=timeout_seconds)
    time.sleep(2.0)   # one settle beat; entities land a frame or two after the doc appears
    return ok


def enter_fight(enemy="BANDIT_RANGED_01", timeout_seconds=120):
    """Spawn + press Fight + wait for combat.active. Same shape as to_combat.main()'s spawn block."""
    spawn = drive.run("crucible_debug_spawn", ["enemies", enemy])
    if "invoked=True" not in spawn:
        return None
    drive.wait_until(lambda: "fight" in drive.run("crucible_ui_dump", ["-", "button"]).lower(),
                      timeout_seconds=8, interval=0.3)
    if not to_combat.press_fight(timeout_seconds=40):
        # PRIMARY: to_combat.py's measured-working walk + Fight, unchanged.
        reached = to_combat.walk_onto_encounter() and to_combat.press_fight(timeout_seconds=60)
        # FALLBACK only after that has actually failed.
        if not reached:
            outcome = drive.enter_combat()
            print("  enter_combat: ok=%s attempts=%d reason=%s"
                  % (outcome["ok"], outcome["attempts"], outcome["reason"]))
            if not outcome["ok"]:
                return None
    return to_combat.wait_for_combat(timeout_seconds=timeout_seconds)


def main():
    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    if not load_fixture():
        print("FATAL: could not reach a loaded, interactive overworld")
        return 3
    gather_party()
    shot("overworld")

    # =====================================================================================
    print("\n=== CHAOS_FREEZE (crucible_chaos_freeze / crucible_chaos_state) ===")
    chaos_before = drive.run("crucible_chaos_state")
    m_before = re.search(r"chaosHistoryCount=(\S+)", chaos_before)
    if not m_before:
        unverifiable("chaos_freeze: chaosHistoryCount holds steady while frozen",
                      "AdventureState.MapState.ChaosState is null in this fixture right now "
                      "(crucible_chaos_state: %r) -- nothing to freeze." % chaos_before.strip()[:160])
    else:
        freeze_on = drive.run("crucible_chaos_freeze", ["on"])
        check("chaos_freeze: 'on' reads back frozen=True from its own response",
              "frozenAfter=True" in freeze_on, freeze_on.strip()[:200])

        # Drive several REAL end-turns while frozen -- each one is documented (ChaosCommands.cs) to
        # be the thing that raises chaos, so this is a genuine attempt to move it, not a no-op.
        for _ in range(4):
            drive.run("crucible_overworld_end_turn", [])
            drive.wait_ready(timeout_seconds=20)

        chaos_after = drive.run("crucible_chaos_state")
        m_after = re.search(r"chaosHistoryCount=(\S+)", chaos_after)
        check("chaos_freeze: chaosHistoryCount is UNCHANGED across turns while frozen "
              "(independently re-read via crucible_chaos_state, not inferred from the freeze reply)",
              m_after is not None and m_after.group(1) == m_before.group(1),
              "before=%r after=%r" % (chaos_before.strip()[:150], chaos_after.strip()[:150]))

        drive.run("crucible_chaos_freeze", ["off"])

    gather_party()

    # =====================================================================================
    print("\n=== FIGHT 1: ABILITY_AUTO, MULTI_WAVE, END_PHASE ===")
    combat = enter_fight()
    if combat is None:
        check("fight 1: combat became active", False,
              "enter_fight() never reached combat.active; last state: %s" % drive.describe())
        print("\nCannot run ABILITY_AUTO / MULTI_WAVE / END_PHASE without a live fight -- skipping "
              "to the DEFEAT test.")
    else:
        check("fight 1: combat became active", True, "combatants=%d" % len(combat.get("combatants") or []))
        shot("fight1-start")
        drive.run("crucible_godmode", ["on"])

        # Reinforce group 1 so ability_auto and the wave wipe both have room to work without the
        # fight ending underneath them prematurely. crucible_combat_spawn is an already-verified
        # checklist item (section D); using it here is setup, not the thing under test.
        reinforce = drive.run("crucible_combat_spawn", ["BANDIT_RANGED_01", "3", "1"])
        print("  reinforced group 1: %s" % reinforce.splitlines()[0] if reinforce else "(no result)")

        # ---- crucible_use_ability_auto ---------------------------------------------------
        print("\n--- USE_ABILITY_AUTO ---")
        # Pre-select a turn whose active entity actually HAS a usable non-FLEE/SKIP/MOVE/EQUIP
        # ability (see select_actor_with_real_ability's docstring for why the old version of this
        # test -- asserting on whoever merely held the turn -- was wrong).
        active_before = select_actor_with_real_ability()
        if active_before is None:
            unverifiable("use_ability_auto: the acting entity's actions (pa+sa) dropped after firing",
                         "no entity's turn within the search budget had a usable ability other than "
                         "FLEE/SKIP_TURN/BASIC_MOVE/EQUIP_WEAPON/BASIC_RELOAD (checked live via "
                         "crucible_list_abilities before every candidate turn) -- cannot exercise "
                         "crucible_use_ability_auto meaningfully right now")
        else:
            rows_before = combatants()
            actor_before = rows_before.get(active_before, {})
            auto_result = drive.run("crucible_use_ability_auto", [])
            time.sleep(2.5)  # abilities resolve asynchronously -- battery.py's own warning
            rows_after = combatants()
            actor_after = rows_after.get(active_before, {})

            def actions(row):
                try:
                    return int(row.get("pa", "0")) + int(row.get("sa", "0"))
                except ValueError:
                    return None

            spent = (actions(actor_before) is not None and actions(actor_after) is not None
                      and actions(actor_after) < actions(actor_before))
            check("use_ability_auto: the acting entity's actions (pa+sa) dropped after firing "
                  "(read back from crucible_combat_snapshot, not from the command's own reply; the "
                  "actor was pre-selected via crucible_list_abilities to actually have a usable "
                  "non-FLEE/SKIP/MOVE/EQUIP ability, not just whoever happened to hold the turn)",
                  spent,
                  "actor=%s pa+sa before=%s after=%s; command reply(for context only)=%r"
                  % (active_before[:8] if active_before else "?", actions(actor_before), actions(actor_after),
                     auto_result.strip().splitlines()[0][:160] if auto_result.strip() else "(empty)"))
        shot("after-ability-auto")

        # ---- MULTI-WAVE: wipe group 1, watch for a replenish ------------------------------
        print("\n--- MULTI_WAVE (crucible_combat_wipe_enemies) ---")
        header_before = wave_header()
        enemies_before = group_count(1)
        wipe = drive.run("crucible_combat_wipe_enemies", ["1"])
        print("  wipe: %s" % (wipe.splitlines()[0] if wipe else "(no result)"))

        # Poll: death bookkeeping and any wave transition are asynchronous (CombatDriveCommands.cs's
        # own note on crucible_combat_wipe_enemies).
        replenished = None
        for _ in range(20):
            time.sleep(1)
            snap = drive.snapshot()
            if not (snap.get("combat") or {}).get("active"):
                break
            count_now = group_count(1)
            if count_now > 0:
                replenished = count_now
                break

        header_after = wave_header()
        still_active = bool((drive.snapshot().get("combat") or {}).get("active"))
        if replenished:
            shot("wave2-start")
            check("multi_wave: enemy group replenished after being wiped to zero "
                  "(combatants crossing back up, not asserted on combat.round -- it resets per wave)",
                  True,
                  "group1 before=%d -> after wipe -> replenished=%d; totalRounds %s->%s waveIndex %s->%s"
                  % (enemies_before, replenished, header_before["totalRounds"], header_after["totalRounds"],
                     header_before["waveIndex"], header_after["waveIndex"]))
        elif not still_active:
            unverifiable("multi_wave: enemy group replenished after being wiped to zero",
                         "combat ended after the wipe (group1 before=%d, combat.active=False) -- this "
                         "spawn (BANDIT_RANGED_01) only had one wave; would need an encounter with a "
                         "scripted WaveAmount > 1 to observe a real second wave." % enemies_before)
        else:
            check("multi_wave: enemy group replenished after being wiped to zero", False,
                  "group1 before=%d, still 0 after ~20s and combat is still active (stuck?)" % enemies_before)

        # ---- crucible_end_phase("combat") -- closes fight 1 on purpose, so it runs last here ----
        print("\n--- END_PHASE (crucible_end_phase) ---")
        route_before = drive.snapshot().get("route")
        active_before_ep = bool((drive.snapshot().get("combat") or {}).get("active"))
        end_phase_result = drive.run("crucible_end_phase", ["combat"])

        # Assert on the ROUTE LEAVING COMBAT, not on combat.active.
        #
        # combat.active is derived from CombatState.Entities being non-empty (CombatReader.cs:45),
        # NOT from the phase. When a venue advances to its NEXT phase -- a treasure room, say --
        # the combat phase really has ended but the entities are still there, so combat.active
        # stays true. Measured: routeBefore=COMBAT routeAfter=TREASURE with activeAfter=True, and
        # the verb had done exactly what it was asked to do. Two earlier runs "passed" only
        # because those venues happened to land on ADVENTURE.
        drive.wait_until(lambda: drive.snapshot().get("route") != "COMBAT",
                          timeout_seconds=25, interval=0.5)
        route_after = drive.snapshot().get("route")
        active_after_ep = bool((drive.snapshot().get("combat") or {}).get("active"))
        check("end_phase: the route LEAVES COMBAT after ending the combat phase "
              "(read via /state?schema=v2, not the command's own reply -- and not via "
              "combat.active, which stays true across an in-venue phase change)",
              route_before == "COMBAT" and route_after != "COMBAT",
              "routeBefore=%s routeAfter=%s (combat.active %s->%s, reported for context only); "
              "command reply(context only)=%r"
              % (route_before, route_after, active_before_ep, active_after_ep,
                 end_phase_result.strip().splitlines()[0][:160] if end_phase_result.strip() else "(empty)"))

        drive.wait_ready()
        shot("fight1-end")

    # =====================================================================================
    # DEFEAT LATCH -- LAST, because it ends the run. Fight 1 above must be fully measured first.
    print("\n=== FIGHT 2: DEFEAT LATCH (crucible_endadventure_watch) ===")
    # BACK THE FIXTURE UP BEFORE DRIVING A DEFEAT.
    # A run that ends rewrites and can delete its own save. This exact section destroyed the main
    # fixture once already -- 5.34 MB down to 552 KB and no longer loadable -- so the backup happens
    # HERE, before the damage, not as a cleanup afterwards.
    _ok, _detail = drive.backup_fixture(to_combat.DEFAULT_RUN)
    print("  fixture backup: %s (%s)" % ("ok" if _ok else "FAILED", _detail))

    # Get back to the OVERWORLD before trying to start another fight.
    #
    # Ending fight 1's combat phase does not necessarily return to the map: the venue may advance
    # to its OWN next phase (a treasure room). Measured: route sat at TREASURE with combat.active
    # still true, enter_fight() never saw a new fight, and the whole defeat section was skipped
    # behind a misleading "fight 2: combat became active -> False".
    # This loop is now drive.exit_combat(), which additionally handles a fight that is still LIVE
    # (crucible_win_combat can leave a replenished wave standing) rather than assuming the combat
    # phase has already ended.
    back = drive.exit_combat()
    print("  back on the map: ok=%s route=%s (%s)" % (back["ok"], back["route"], back["reason"]))

    gather_party()
    combat2 = enter_fight()
    if combat2 is None:
        check("fight 2: combat became active", False,
              "enter_fight() never reached combat.active; last state: %s" % drive.describe())
        print("\nCannot drive a defeat without a live fight.")
    else:
        check("fight 2: combat became active", True, "combatants=%d" % len(combat2.get("combatants") or []))
        # godmode must be OFF, or the party's HP is topped up every tick and the wipe never sticks.
        drive.run("crucible_godmode", ["off"])
        watch_reset = drive.run("crucible_endadventure_watch", ["reset"])
        print("  watch reset: %s" % (watch_reset.splitlines()[0] if watch_reset else "(no result)"))

        # Do NOT wipe until the venue is really on screen. Wiping during the load makes every
        # subsequent read stale, and a stale read once made this very check PASS while the
        # screenshot showed the loading parchment.
        if not venue_on_screen():
            print("  the venue never finished loading -- NOT wiping, the reads would be stale")
            report("defeat", "the fight's venue never finished loading, so the wipe and every "
                             "assertion after it would have read stale state")
            return
        shot("pre-defeat")

        # ---- WIPE, then VERIFY every group-0 combatant is actually dead ------------------
        # crucible_combat_wipe_enemies("0") can leave the wipe incomplete: a party member can be
        # brought back by a Revive Ally-style effect from a still-living ally before death fully
        # sticks, and any SUMMONED ally (e.g. a Pokemon Trainer's partner) is a group-0 combatant
        # too and must die along with the rest, or the fight -- and the ally that could revive
        # someone -- stays alive. So this kills, reads real state back via crucible_combat_snapshot
        # (never the wipe command's own reply), and retries once if anyone came back, rather than
        # trusting a single wipe call and blaming the latch when nothing actually died.
        def group0_rows():
            return {g: r for g, r in combatants().items() if r.get("group") == "0"}

        def group0_all_dead(rows):
            return len(rows) > 0 and all(r.get("dead") == "True" for r in rows.values())

        wiped_ok = False
        wipe_detail = ""
        for attempt in range(2):
            wipe = drive.run("crucible_combat_wipe_enemies", ["0"])
            print("  party wipe (attempt %d): %s" % (attempt + 1, wipe.splitlines()[0] if wipe else "(no result)"))
            drive.wait_until(lambda: group0_all_dead(group0_rows()), timeout_seconds=10, interval=0.5)
            rows = group0_rows()
            wipe_detail = "attempt %d: %s" % (attempt + 1,
                ", ".join("%s(grp0)=%s dead=%s" % (r.get("class", g), g[:8], r.get("dead")) for g, r in rows.items()))
            if group0_all_dead(rows):
                wiped_ok = True
                break

        shot("post-wipe")
        check("defeat: every group-0 combatant (including any summoned ally) reads dead=True "
              "after the wipe (read back from crucible_combat_snapshot, not the wipe command's own "
              "reply, with one retry to cover a revive undoing the first attempt)",
              wiped_ok, wipe_detail)

        if not wiped_ok:
            print("\nParty could not be fully wiped -- the DEFEAT LATCH check below is meaningless "
                  "without a real party-wipe precondition, so it is skipped rather than blamed on "
                  "crucible_endadventure_watch.")
            shot("defeat")
        else:
            # CLEAR GATES WHILE WAITING. A defeat opens narration ("I had such high hopes for
            # this lot, I did." -- Hildegard, Tavern Keeper) behind a Click to Continue, and
            # _endAdventure AWAITS that dialogue. Leave it up and the run never ends, so the
            # watch never latches and the failure looks like a broken latch.
            # Measured: the party was confirmed dead on screen, all four portraits at 0 with
            # grave markers, while the watch sat at ended=False purely because of this dialogue.
            latched = None
            for _ in range(30):
                time.sleep(1)
                drive.clear_gates(max_rounds=2)
                watch = drive.run("crucible_endadventure_watch", [])
                if "ended=True" in watch:
                    latched = watch
                    break
            shot("defeat")

            if latched is None:
                check("defeat: crucible_endadventure_watch latches an outcome after the party is wiped",
                      False, "ended=True never appeared within 30s after every group-0 combatant "
                      "independently verified dead=True")
            else:
                check("defeat: crucible_endadventure_watch latches victory=False "
                      "(NEVER asserted on route -- both outcomes land on ADVENTURE_SELECTION)",
                      "victory=False" in latched,
                      latched.strip()[:300])

    print("\n" + "=" * 62)
    bad = [n for n, ok in RESULTS if not ok]
    print("%d/%d passed" % (len(RESULTS) - len(bad), len(RESULTS)))
    for n in bad:
        print("  FAILED: " + n)
    if UNVERIFIABLE:
        print("\nUNVERIFIABLE (precondition not met this run, not a pass or a fail):")
        for n, reason in UNVERIFIABLE:
            print("  %s: %s" % (n, reason[:200]))
    if SHOTS:
        print("\nscreenshots to OPEN (a visual claim is not verified until one is looked at):")
        for label, path in SHOTS:
            print("  %-14s %s" % (label, path))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
