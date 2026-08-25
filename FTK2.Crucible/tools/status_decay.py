#!/usr/bin/env python3
"""
Proves (or disproves), from LIVE game state, whether a status effect's DURATION decays in
combat -- and does it for both a TickCombat=true status and a TickCombat=false one, so the
"false" case is a real control rather than an assumption.

Statuses chosen, from StatusEffects.json (shipped at
"...For The King II_Data\\StreamingAssets\\Assets\\Configs\\JSON~\\StatusEffects.json"):

  STATUS_FIRE_00  -- TickCombat=true, Duration=3, TickFrequency=1, TickOverworld=false.
    {"Type": "FIRE", "Duration": 3, "TickFrequency": 1, "TickOverworld": false,
     "TickCombat": true, "TickExpire": false, "TileSync": false, "GroupSync": false,
     "AddProperties": [], "Stats": {}}
    Chosen because TickFrequency=1 means every start-of-turn tick both fires the burn ability
    AND decrements Duration (see TICK_TIMING below), giving a clean 1-decrement-per-active-turn
    signal with no every-other-turn noise (STATUS_BLEED_00 has TickFrequency=2 and would still
    decrement Duration every active turn, but its tick-ability cadence would be a confusing
    thing to reconcile against -- FIRE_00 keeps the two numbers in lockstep).

  STATUS_POISON_00 -- TickCombat=false, Duration=3, TickOverworld=true.
    {"Type": "POISON", "Duration": 3, "TickFrequency": 1, "TickOverworld": true,
     "TickCombat": false, "TickExpire": false, "TileSync": false, "GroupSync": false,
     "AddProperties": [], "Stats": {"STR": -10, "VIT": -10, "INT": -10, "SPD": -10,
     "TAL": -10, "AWR": -10}}
    Chosen as the negative control specifically because TickCombat=false: per
    CombatHelper.TickActiveEntityCharacterStatus (see below), the active entity's status list is
    first filtered to `where Env.Configs.StatusEffects[s].TickCombat` before anything touches
    Duration, so STATUS_POISON_00 is never even visited by the combat tick path -- it ticks on
    the overworld (TickOverworld=true) and drains stats instead.

TICK_TIMING -- how many end-turns until the status owner is active again, derived from code
(.decompile-scratch/proj/CombatHelper.cs):

  CombatHelper.NextTurn (called by CrucibleCombatEndTurn via CombatPhase._nextTurn) does:
      if RoundEntities is null or has <=1 entries: regenerate a FULL new round via
          GetCombatOrder(...) (one entry per living combatant) and TotalRounds++
      else: RoundEntities.RemoveAt(0)   -- dequeue the entity whose turn just ended
  So each end-turn call dequeues exactly one entity from the front of RoundEntities, and a
  brand-new round is generated only once the queue empties to <=1. That means every living
  combatant gets exactly one turn per round, and an entity becomes RoundEntities[0] (the ACTIVE
  entity -- the only one CombatHelper.TickActiveEntityCharacterStatus ever looks at) again after
  AT MOST `len(RoundEntities)` end-turn calls, i.e. at most the number of living combatants in
  the fight. Deaths only shrink that bound. This script therefore does NOT hardcode a turn
  count: it polls crucible_combat_snapshot's activeGuid after every crucible_combat_end_turn and
  recognises "owner is active again" directly, bounding the total number of end-turns it will
  spend per status at (living combatants + margin) * (initial duration + margin) so it can never
  hang even if the fight's roster changes mid-test.

  Duration itself only ever moves inside TickActiveEntityCharacterStatus(pIsStartTurn: true),
  called once per NextTurn right after the new active entity is chosen (CombatHelper.cs:826-827),
  and only for TickCombat=true statuses on THAT entity (CombatHelper.cs:1017-1019). The actual
  decrement is CoreHelper.DecrementStatus(..., pDecrementTick: false) (CoreHelper.cs:757-807):
  duration=status.Duration-1, written back via `value.Duration = pDuration` when duration>0, or
  the status is removed outright (eCombatActions.REMOVE_STATUS) when it hits 0. So one active
  turn == exactly one Duration decrement (for a TickFrequency=1 status like FIRE_00), never more,
  never less -- which is what "duration decreased" and "eventually reaches 0/disappears" assert
  against below.

Harness commands used (verified 2026-08-24 against the C# source, arg order as registered):
  crucible_combat_snapshot        () -- CombatDriveCommands.cs:56
  crucible_status_add <slot> <statusConfigName> <duration> -- CharacterCommands.cs:240
      slot indexes PartyAccess.TryGetParty(), NOT a combat group; see CharacterCommands.cs:308-319.
  crucible_combat_end_turn        () -- CombatDriveCommands.cs:62, invokes CombatPhase._nextTurn(false).
      LastResult itself warns the Task it starts is not awaited, so this script always re-polls
      crucible_combat_snapshot afterward rather than trusting the call to have already settled.

This never drives the game to get INTO combat and never calls crucible_new_game / crucible_load
/ crucible_spawn -- it only observes and ends turns in whatever fight is already running, exactly
like tools/assert_traits.py and tools/battery.py do.

Usage:
    python FTK2.Crucible/tools/status_decay.py
"""

import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

FIRE = "STATUS_FIRE_00"
POISON = "STATUS_POISON_00"
SLOT = "0"  # party slot passed to crucible_status_add; owner guid is discovered by diffing
            # the snapshot, not assumed from the slot, so this does not depend on slot->combat
            # ordering being anything in particular.

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(("  PASS " if ok else "  FAIL ") + name + (("\n        " + detail) if detail else ""))


def read_state():
    """(activeGuid, {guid: row}) from ONE crucible_combat_snapshot call, so both are consistent."""
    text = drive.run("crucible_combat_snapshot")
    lines = text.splitlines()
    header = lines[0] if lines else ""
    m = re.search(r"activeGuid=(\S+)", header)
    active = m.group(1) if m else None
    return active, combatants(text)


def combatants(text):
    """guid -> parsed row, for every actual combatant line (not the header, not a tile row).

    Deliberately requires BOTH ' name=' and ' hp=' before treating a line as a combatant row --
    the header line also contains 'activeGuid=<guid or ->', and a naive substring search for a
    status name against the WHOLE snapshot text would false-match there whenever a combatant's
    real guid happens to be the active one. Parsing row-by-row like this avoids that entirely.
    """
    out = {}
    for line in text.splitlines():
        if " name=" not in line or " hp=" not in line:
            continue
        guid = line.strip().split()[0]
        row = {"line": line.strip()}
        for key in ("class", "group", "hp", "dead"):
            m = re.search(key + r"=(\S+)", line)
            if m:
                row[key] = m.group(1)
        m = re.search(r"statuses=count=\d+ \[([^\]]*)\]", line)
        row["statuses"] = m.group(1) if m else ""
        out[guid] = row
    return out


def status_duration(rows, guid, status_name):
    """Duration of `status_name` on `guid`, or None if that guid has no such status right now."""
    row = rows.get(guid)
    if row is None:
        return None
    m = re.search(re.escape(status_name) + r"\(d=(-?\d+)\)", row.get("statuses", ""))
    return int(m.group(1)) if m else None


def find_owner_guid(rows_before, rows_after, status_name):
    """The guid that gained `status_name` between two snapshots -- i.e. who crucible_status_add
    actually applied it to, discovered from state rather than assumed from the slot argument."""
    for guid, row in rows_after.items():
        before_statuses = rows_before.get(guid, {}).get("statuses", "")
        if status_name in row.get("statuses", "") and status_name not in before_statuses:
            return guid
    return None


def combat_is_active():
    return bool((drive.snapshot().get("combat") or {}).get("active"))


def end_turn_and_settle(prior_active, settle_timeout=8):
    """One crucible_combat_end_turn, then poll (bounded) until the snapshot reflects it.

    crucible_combat_end_turn's own result string warns its Task is not awaited, so a caller that
    reads the snapshot immediately can see stale state. This polls for the activeGuid to change
    away from `prior_active`, bounded by `settle_timeout` -- never a flat unconditional sleep, and
    never unbounded.
    """
    drive.run("crucible_combat_end_turn", [])

    def changed():
        active, rows = read_state()
        return (active, rows) if active != prior_active else None

    result = drive.wait_until(changed, timeout_seconds=settle_timeout, interval=0.3)
    if result is not None:
        return result
    return read_state()


def apply_and_find_owner(status_name, duration):
    rows_before = combatants(drive.run("crucible_combat_snapshot"))
    add_result = drive.run("crucible_status_add", [SLOT, status_name, str(duration)])
    if add_result.lower().startswith("error"):
        return None, None, "crucible_status_add failed: %s" % add_result[:200]

    _, rows_after = read_state()
    owner = find_owner_guid(rows_before, rows_after, status_name)
    if owner is None:
        return None, None, "no combatant gained %s after crucible_status_add (result: %s)" % (
            status_name, add_result[:200])

    initial = status_duration(rows_after, owner, status_name)
    return owner, initial, add_result


def run_test(status_name, expect_decay, initial_duration_requested):
    """Applies `status_name` to SLOT, then drives end-turns until its owner has been the active
    entity several times, tracking Duration each time. Bounded per TICK_TIMING above."""
    print("\n=== %s (TickCombat expected to %s decay) ===" % (
        status_name, "" if expect_decay else "NOT"))

    if not combat_is_active():
        check("%s: still in combat" % status_name, False, "combat ended before this test started")
        return

    living = len(combatants(drive.run("crucible_combat_snapshot")))
    owner, initial, detail = apply_and_find_owner(status_name, initial_duration_requested)
    if owner is None:
        check("%s: applied and owner identified" % status_name, False, detail)
        return
    check("%s: applied and read back (duration=%s)" % (status_name, initial), initial is not None,
          detail[:200])
    if initial is None:
        return

    # Bound: at most (living combatants + margin) end-turns per full round, times (duration +
    # margin) rounds before the status should be fully spent -- generous, but always finite.
    max_end_turns = (living + 3) * (initial + 3) + 10

    readings = [initial]
    prior_active, _ = read_state()
    disappeared = False
    combat_ended_early = False

    for turn in range(max_end_turns):
        if not combat_is_active():
            combat_ended_early = True
            break
        active, rows = end_turn_and_settle(prior_active)
        prior_active = active
        if active != owner:
            continue
        cur = status_duration(rows, owner, status_name)
        if cur is None:
            disappeared = True
            print("     turn %d: %s no longer present on owner (removed)" % (turn + 1, status_name))
            break
        readings.append(cur)
        print("     turn %d: owner active, %s duration=%d" % (turn + 1, status_name, cur))

    if combat_ended_early:
        check("%s: combat stayed active for the whole test" % status_name, False,
              "combat ended after %d readings: %s" % (len(readings) - 1, readings))
        return

    ever_decreased = any(b < a for a, b in zip(readings, readings[1:]))
    ever_increased = any(b > a for a, b in zip(readings, readings[1:]))
    never_changed = not ever_decreased and not ever_increased and not disappeared

    if expect_decay:
        check("%s: duration decreased across active turns" % status_name, ever_decreased,
              "readings=%s" % readings)
        check("%s: duration eventually reaches 0 / status disappears" % status_name, disappeared,
              "readings=%s disappeared=%s (bounded at %d end-turns)" % (
                  readings, disappeared, max_end_turns))
    else:
        check("%s: duration did NOT decay in combat" % status_name, never_changed,
              "readings=%s (any change here would disprove the TickCombat=false claim)" % readings)
        check("%s: status still present at the end of the test" % status_name, not disappeared,
              "readings=%s disappeared=%s" % (readings, disappeared))


def main():
    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    if not combat_is_active():
        print("SKIP: not in combat. Get the game into a fight first (this script does not "
              "drive the game into one).")
        return 2

    run_test(FIRE, expect_decay=True, initial_duration_requested=3)
    run_test(POISON, expect_decay=False, initial_duration_requested=3)

    print("\n" + "=" * 62)
    bad = [n for n, ok in RESULTS if not ok]
    print("%d/%d passed" % (len(RESULTS) - len(bad), len(RESULTS)))
    for n in bad:
        print("  FAILED: " + n)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
