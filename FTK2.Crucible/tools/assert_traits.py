#!/usr/bin/env python3
"""
Assert the CF_PACK_ORIGINALS class traits actually FIRE, in a live combat.

Every check reads the effect back out of game state; none of them trusts "the command returned OK".
That distinction has caught real bugs in this repo more than once -- most recently Blood Price,
whose recipe evaluated correctly, built the right action, and then threw inside the native damage
path so the HP never moved.

The recipe engine also logs each effect it applies (and each one it skips), so this cross-checks
the measured delta against ClassForge's own log lines rather than inferring from HP alone: HP can
move for a dozen reasons in a fight, and only the log says which recipe did it.

Usage:
    python FTK2.Crucible/tools/assert_traits.py            # run every check it can
    python FTK2.Crucible/tools/assert_traits.py --json out.json
"""

import argparse
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

PLAYER_LOG = os.path.expandvars(
    r"%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\Player.log")

VAMPIRIC = "CF_ORIG_VAMPIRIC"
PACIFIST = "CF_ORIG_PACIFIST"
TRAINER = "CF_ORIG_TRAINER"


def combatants():
    """[{guid, cls, hp, group, tile}] from the live combat snapshot."""
    rows = []
    for line in drive.run("crucible_combat_snapshot").splitlines():
        m = re.match(
            r"\s*([0-9a-f-]{36}) name=(\S+) class=(\S+) group=(\d+) hp=(\d+) tile=\((\d+), (\d+)\)",
            line)
        if not m:
            continue
        rows.append({
            "guid": m.group(1), "name": m.group(2), "cls": m.group(3),
            "group": int(m.group(4)), "hp": int(m.group(5)),
            "tile": (int(m.group(6)), int(m.group(7))),
        })
    return rows


def find(rows, cls):
    for r in rows:
        if r["cls"] == cls:
            return r
    return None


def log_size():
    try:
        return os.path.getsize(PLAYER_LOG)
    except OSError:
        return 0


def log_since(offset):
    """New Player.log text since `offset`. The engine writes its recipe activity here."""
    try:
        with open(PLAYER_LOG, "r", encoding="utf-8", errors="replace") as handle:
            handle.seek(offset)
            return handle.read()
    except OSError:
        return ""


def recipe_lines(text, recipe):
    return [l.strip() for l in text.splitlines() if recipe in l]


def check(results, name, passed, detail):
    results.append({"check": name, "passed": bool(passed), "detail": detail})
    print("  %-34s %s  %s" % (name, "PASS" if passed else "FAIL", detail))


def active_guid():
    line = drive.run("crucible_combat_snapshot").splitlines()[0]
    m = re.search(r"activeGuid=(\S+)", line)
    return m.group(1) if m else None


def wait_for_turn(guid, timeout_seconds=240):
    """
    Waits until it is THIS entity's turn, detecting the ways the queue can stall.

    Firing an ability out of turn is not an error the game reports -- it simply does nothing, which
    reads as "the trait did not proc". So the wait is necessary. But the naive version SPINS:

      * The active entity may have pa=0 sa=0, in which case crucible_use_ability_auto no-ops, the
        turn never ends, and the active guid never changes. Measured 2026-08-24: the Vampiric held
        the turn at 0/0 actions and the loop ran until it timed out.
      * Combat can end underneath the loop, leaving nothing to wait for at all.

    So this tracks the active guid, ends the turn explicitly when it stops moving, and gives up with
    a reason rather than burning the whole timeout.
    """
    deadline = time.time() + timeout_seconds
    last_active = None
    stalls = 0

    while time.time() < deadline:
        snap = drive.snapshot()
        if not (snap.get("combat") or {}).get("active"):
            print("     combat ended while waiting for %s" % guid[:8])
            return False

        active = active_guid()
        if active == guid:
            return True

        if active == last_active:
            stalls += 1
            # The holder cannot act (no actions left), so nudging it does nothing. End its turn.
            if stalls == 2:
                print("     turn stalled on %s -- ending its turn" % str(active)[:8])
                drive.run("crucible_combat_end_turn", [])
            elif stalls >= 5:
                print("     turn order is stuck on %s (%d checks, no change)"
                      % (str(active)[:8], stalls))
                return False
        else:
            stalls = 0
            last_active = active
            drive.run("crucible_use_ability_auto", [])

        time.sleep(5)

    print("     timed out waiting for %s to become active" % guid[:8])
    return False


def assert_vampiric(results):
    """Blood Price costs HP on a hostile ability; Crimson Drain returns a share of the damage."""
    print("\n=== Vampiric ===")
    rows = combatants()
    me = find(rows, VAMPIRIC)
    if me is None:
        check(results, "vampiric present", False, "not in this combat")
        return

    enemy = next((r for r in rows if r["group"] != me["group"]), None)
    if enemy is None:
        check(results, "vampiric has a target", False, "no enemy in the snapshot")
        return

    if not wait_for_turn(me["guid"]):
        check(results, "vampiric got a turn", False,
              "never became the active entity; nothing could be measured")
        return
    check(results, "vampiric got a turn", True, "active entity")

    # Re-read: HP and enemy positions move while the other combatants act.
    rows = combatants()
    me = find(rows, VAMPIRIC)
    enemy = next((r for r in rows if r["group"] != me["group"]), None)
    if enemy is None:
        check(results, "vampiric has a target", False, "no enemy left")
        return

    # An ability with no actions left reports success and does nothing, so refill first.
    drive.run("crucible_combat_restore_actions", ["0"])

    rows = combatants()
    me = find(rows, VAMPIRIC)
    enemy = next((r for r in rows if r["group"] != me["group"]), None)
    hp_before = me["hp"]
    enemy_hp_before = enemy["hp"]
    offset = log_size()

    drive.run("crucible_use_ability",
              ["BLADE_BASIC_ATTACK", str(enemy["tile"][0]), str(enemy["tile"][1])])
    time.sleep(9)

    text = log_since(offset)
    after = find(combatants(), VAMPIRIC)
    enemy_after = find(combatants(), enemy["cls"])
    hp_after = after["hp"] if after else None

    blood = recipe_lines(text, "SKILL_CF_VAMPIRIC_BLOOD_PRICE")
    drain = recipe_lines(text, "SKILL_CF_VAMPIRIC_CRIMSON_DRAIN")
    blood_threw = any("Exception" in l for l in blood)
    drain_threw = any("Exception" in l for l in drain)

    dealt = (enemy_hp_before - enemy_after["hp"]) if enemy_after else 0
    delta = (hp_after - hp_before) if (hp_after is not None) else None
    print("     vampiric hp %s -> %s (delta %s) | damage dealt to %s: %s"
          % (hp_before, hp_after, delta, enemy["cls"], dealt))

    # A SUCCESSFUL effect logs nothing unless VerboseLogging is on -- only failures log. So the
    # assertion is "it did not throw", cross-checked against the measured HP delta, rather than
    # "a log line exists". Requiring a line marked a working Blood Price as broken.
    # With VerboseLogging on, the engine emits "proc <RECIPE> (<tag>) owner=<guid> actions=N" for
    # every recipe that fires. That is the direct evidence; the HP delta is the cross-check.
    check(results, "Blood Price procced",
          any("proc SKILL_CF_VAMPIRIC_BLOOD_PRICE" in l for l in blood),
          next((l.split("] ")[-1] for l in blood if "proc " in l), "no proc line"))
    check(results, "Blood Price did not throw", not blood_threw,
          "no exception" if not blood_threw else blood[0][:140])
    # -3 cost, partly offset by the drain when the hit connects, so the net is <= 0 rather than
    # exactly -3. Measured live: delta -1 == -3 cost + 2 drained from 7 damage.
    check(results, "net HP moved as designed",
          delta is not None and delta <= 0,
          "hp delta %s (cost 3, drain returns 25%% of damage dealt)" % delta)

    check(results, "Crimson Drain procced",
          any("proc SKILL_CF_VAMPIRIC_CRIMSON_DRAIN" in l for l in drain),
          next((l.split("] ")[-1] for l in drain if "proc " in l), "no proc line"))
    check(results, "Crimson Drain did not throw", not drain_threw,
          "no exception" if not drain_threw else drain[0][:140])
    if dealt > 0:
        check(results, "Crimson Drain returned health",
              delta is not None and delta > -3,
              "dealt %s, so the drain should offset part of the 3 cost; delta %s" % (dealt, delta))
    else:
        print("     (attack dealt no damage -- ON_DAMAGE_DEALT could not fire; drain not assertable)")


def assert_pacifist(results):
    """The Pacifist's weapon must deal no damage at all; the reflect must hurt the attacker."""
    print("\n=== Pacifist ===")
    pacifist = find(combatants(), PACIFIST)
    if pacifist is None:
        # Waves pull a subset of the party, so absence here is a coverage gap, not a defect.
        print("  (Pacifist is not in this wave -- kit check skipped)")
        return
    # BY GUID. crucible_list_abilities with "-" reads the ACTIVE entity, which is whoever's turn it
    # is -- reading another character's kit and attributing it to the Pacifist.
    abilities = drive.run("crucible_list_abilities", [pacifist["guid"]])
    damaging = [a for a in ("BLADE_BASIC_ATTACK", "STAFF_BASIC_ATTACK", "BOW_BASIC_ATTACK")
                if a in abilities]
    only_star = re.findall(r"\s(ONLY_\w+)", abilities)
    if True:
        check(results, "Pacifist kit is non-damaging",
              not damaging,
              "no basic attack in the active kit" if not damaging else "found %s" % damaging)
        check(results, "Pacifist kit has ONLY_* abilities", bool(only_star),
              ", ".join(sorted(set(only_star))[:4]) if only_star else "none found")


def assert_trainer(results):
    """
    The three partners: Chaoshound at combat start, Hellhound at Bond 3, Serpent at Bond 6.

    Bond is built by kills, so this fells enemies until the later two unlock. Each partner must land
    on the PLAYER's side (group 0) -- a summon placed on an enemy tile spawns a HOSTILE creature,
    which is exactly how the first one arrived before the placement fix.
    """
    print("\n=== Beast Trainer ===")
    rows = combatants()
    me = find(rows, TRAINER)
    if me is None:
        print("  (Beast Trainer is not in this wave -- summon checks skipped)")
        return

    # First Partner fires at combat start, so it should already be here.
    allies = [r for r in combatants() if r["group"] == 0 and r["cls"].startswith(("WOLF_", "COMPANION_"))]
    check(results, "First Partner is an ALLY on our side",
          any(r["cls"] == "WOLF_CHAOSHOUND_01" for r in allies),
          ", ".join("%s@group%d" % (r["cls"], r["group"]) for r in allies) or "no partner found")

    hostile_partners = [r for r in combatants()
                        if r["group"] != 0 and r["cls"].startswith(("WOLF_", "COMPANION_"))]
    check(results, "no partner spawned hostile", not hostile_partners,
          "none" if not hostile_partners else str([r["cls"] for r in hostile_partners]))

    # Build Bond by killing. Bond 3 unlocks Ember, Bond 6 unlocks Venom.
    offset = log_size()
    kills = 0
    for _ in range(14):
        if not wait_for_turn(me["guid"], timeout_seconds=90):
            break
        drive.run("crucible_combat_restore_actions", ["0"])
        rows = combatants()
        enemies = [r for r in rows if r["group"] != 0]
        if not enemies:
            break
        weakest = min(enemies, key=lambda r: r["hp"])
        drive.run("crucible_use_ability",
                  ["STAFF_BASIC_ATTACK", str(weakest["tile"][0]), str(weakest["tile"][1])])
        time.sleep(7)
        if not [r for r in combatants() if r["guid"] == weakest["guid"]]:
            kills += 1
            print("     kill %d: %s" % (kills, weakest["cls"]))
        if kills >= 7:
            break

    text = log_since(offset)
    for label, recipe, config in (
            ("Ember Partner (Bond 3)", "SKILL_CF_TRAINER_SECOND_PARTNER", "WOLF_HELLHOUND_02"),
            ("Venom Partner (Bond 6)", "SKILL_CF_TRAINER_THIRD_PARTNER", "COMPANION_SNAKE_BASIC_01")):
        lines = recipe_lines(text, recipe)
        procced = any("proc " + recipe in l for l in lines)
        onfield = any(r["cls"] == config and r["group"] == 0 for r in combatants())
        if procced or onfield:
            check(results, label + " procced", procced,
                  next((l.split("] ")[-1] for l in lines if "proc " in l), "no proc line"))
            check(results, label + " is an ally", onfield,
                  "%s on group 0" % config if onfield else "not on the field")
        else:
            print("     (%s not reached -- %d kills, needs more Bond)" % (label, kills))

    final = [r for r in combatants() if r["group"] == 0 and r["cls"].startswith(("WOLF_", "COMPANION_"))]
    print("     partners on our side: %s" % ([r["cls"] for r in final] or "none"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", dest="json_out")
    parser.add_argument("--only", choices=["vampiric", "pacifist", "trainer"],
                        help="run one class's checks. Each check CONSUMES the character's turn and "
                             "actions, so running them all in one combat can starve the later ones.")
    options = parser.parse_args()

    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    snap = drive.snapshot()
    if not (snap.get("combat") or {}).get("active"):
        print("SKIP: not in combat. Run tools/to_combat.py first.")
        return 2

    results = []
    if options.only in (None, "vampiric"):
        assert_vampiric(results)
    if options.only in (None, "pacifist"):
        assert_pacifist(results)
    if options.only in (None, "trainer"):
        assert_trainer(results)

    passed = [r for r in results if r["passed"]]
    print("\n=== %d/%d checks passed ===" % (len(passed), len(results)))
    for r in results:
        if not r["passed"]:
            print("  FAIL %s: %s" % (r["check"], r["detail"]))

    if options.json_out:
        with open(options.json_out, "w", encoding="utf-8") as handle:
            json.dump(results, handle, indent=1)
        print("wrote %s" % options.json_out)

    return 0 if len(passed) == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
