#!/usr/bin/env python3
"""
Boot a fixture straight into a live combat, unattended.

The fast loop for trait testing. Encodes the sequence that took several wrong turns to find:

  boot -> load -> clear gates -> ANSWER REWARD PROMPTS -> spawn an enemy -> press Fight -> wait

Three things here are not obvious and each cost a restart:

* A reward prompt blocks EVERYTHING. Straight after a load the overworld can look fine while
  `interactionEnabled` is false and every move reports `IsValidMove=false`, purely because a
  "Choose Reward" panel is waiting. drive.pick_reward answers them.
* `crucible_force_combat` is NOT the way in. It routes to COMBAT without initialising the phase,
  leaving the camera under the map with no scene and no way back -- a restart is the only exit.
  The game's own debug spawn plus the encounter menu's Fight button works every time.
* Interacting with an encounter the party is STANDING ON throws KeyNotFoundException on the hex
  tile and hangs the load at ~80%. Spawning brings the fight to the party instead.

Usage:
    python FTK2.Crucible/tools/to_combat.py [--run <runId>] [--enemy BANDIT_RANGED_01] [--godmode]
"""

import argparse
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

# Clean test bed: the three original classes + an EOR control, with every quest removed and
# every non-combat encounter cleared. Both matter for speed and reliability -- a quest whose
# objectives are flagged but unresolved re-queues its reward prompt on EVERY load and holds
# interaction off, and walking anywhere used to trip the Night Merchant or a shop.
DEFAULT_RUN = "4c0f1f9f-20c8-4bd3-9662-c445f48f677e"


def press_fight(timeout_seconds=40):
    """Presses Fight as soon as the encounter menu offers it. Returns whether it landed."""
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        dump = drive.run("crucible_ui_dump", ["-", "button"])
        if "text='Fight'" in dump:
            if "invoked=True" in drive.run("crucible_ui_click", ["Fight"]):
                print("pressed Fight")
                return True
        drive.wait_ready(timeout_seconds=20)
    return False


def walk_onto_encounter():
    """
    Steps onto the nearest ROAMING encounter with the encounter menu enabled.

    Roaming encounters are the ones whose id is a bare GUID; the named ones (TAVERN, CAMP1,
    AF_TOWN_B, ...) are venues and towns, and walking into those opens a shop rather than a fight.
    The party's OWN hex is skipped on purpose: interacting with an encounter you are standing on
    throws KeyNotFoundException on the hex tile and hangs the load at ~80%.
    """
    state = drive.run("crucible_overworld_state")
    hero = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", state)
    if not hero:
        return False
    px, py = int(hero.group(1)), int(hero.group(2))

    candidates = []
    for line in drive.run("crucible_map_encounters", ["-"]).splitlines():
        m = re.match(r"\[(-?\d+),(-?\d+)\] (\S+)", line.strip())
        if not m:
            continue
        x, y, name = int(m.group(1)), int(m.group(2)), m.group(3)
        if (x, y) == (px, py):
            continue
        if not re.match(r"^[0-9a-f]{8}-", name):
            continue
        candidates.append((max(abs(x - px), abs(y - py)), x, y, name))
    candidates.sort()

    for _dist, x, y, name in candidates[:6]:
        result = drive.run("crucible_move", [str(x), str(y), "false", "false", "true"])
        if "move invoked" in result:
            print("walked onto (%d,%d) %s" % (x, y, name))
            drive.wait_ready(timeout_seconds=40)
            # Only the ACTIVE character walks; the rest stay put and would sit the fight out.
            # Teleport them onto the encounter hex so the whole party is in the combat.
            gathered = drive.run("crucible_party_set_hex", [str(x), str(y)])
            print("  gathered party onto (%d,%d): %s"
                  % (x, y, gathered.splitlines()[0] if gathered else "(no result)"))
            time.sleep(3)
            return True
    return False



def make_active(class_id, max_turns=8):
    """
    Ends overworld turns until `class_id` is the active character.

    Only the ACTIVE character walks into an encounter, and only whoever is standing on it joins the
    combat -- teleporting the rest of the party onto the hex afterwards does NOT add them, because
    the encounter has already captured its participants. So the class under test has to be the one
    holding the turn before the fight starts.

    turnOrder index 0 is the active entity (crucible_overworld_state labels it).
    """
    for _ in range(max_turns):
        state = drive.run("crucible_overworld_state")
        rows = re.findall(r"\[(\d+)\] (\S+) guid=(\S+)", state.split("turnOrder")[-1].split("party:")[0])             if "turnOrder" in state else []
        if rows and rows[0][1]:
            active_name = rows[0][1]
            party = drive.run("crucible_party_list")
            for line in party.splitlines():
                if class_id in line and active_name in line:
                    print("active character is %s (%s)" % (active_name, class_id))
                    return True
        drive.run("crucible_overworld_end_turn", [])
        drive.wait_ready(timeout_seconds=45)
    return False


def wait_for_combat(timeout_seconds=120):
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        snap = drive.snapshot()
        combat = snap.get("combat") or {}
        if combat.get("active"):
            return combat
        time.sleep(0.5)
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run", dest="run_id", default=DEFAULT_RUN)
    parser.add_argument("--enemy", default="BANDIT_RANGED_01")
    parser.add_argument("--godmode", action="store_true")
    parser.add_argument("--active",
                        help="end overworld turns until this class is the ACTIVE character, so it "
                             "is the one that walks into the fight (e.g. CF_ORIG_TRAINER)")
    parser.add_argument("--skip-load", action="store_true",
                        help="already in a run; just spawn and fight")
    options = parser.parse_args()

    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    if not options.skip_load:
        transcript = drive.load_run(options.run_id)
        if "FAILED" in transcript:
            # A dirty screen (a stale LoadingUIDocument over a live one) makes _loadGameRun a silent
            # no-op, and it cannot be cleared from inside the game. Restart once and try again
            # rather than reporting a failure the caller cannot act on.
            print("load refused (%s); restarting once" % transcript.splitlines()[-1])
            if not drive.restart_game():
                print("game did not come back after restart")
                return 1
            transcript = drive.load_run(options.run_id)
            if "FAILED" in transcript:
                print(transcript)
                return 1
        print("loaded %s" % options.run_id)
        drive.run("crucible_tutorials_suppress", [])

    drive.clear_gates()
    print("rewards: %s" % drive.pick_reward())

    ready = drive.wait_ready()
    if not ready["ready"]:
        # The stale-overlay state cannot be cleared from inside the game, so restart once and
        # start over rather than reporting a failure the caller cannot act on.
        print("not interactive (%s); restarting once" % ready.get("reason"))
        if not drive.restart_game():
            print("game did not come back after restart")
            return 6
        transcript = drive.load_run(options.run_id)
        if "FAILED" in transcript:
            print(transcript)
            return 6
        drive.run("crucible_tutorials_suppress", [])
        ready = drive.wait_ready()
        if not ready["ready"]:
            print("still not interactive after restart: %s" % ready.get("reason"))
            return 6
    print("ready (cleared: %s)" % (ready["cleared"] or "nothing"))

    if options.active:
        if not make_active(options.active):
            print("could not make %s the active character" % options.active)
            return 7

    # GATHER THE PARTY FIRST. Characters occupy their own hexes on the overworld, and an encounter
    # only pulls in whoever is standing on it -- a fight started without this had one class in it
    # and skipped the checks for the other two. Teleporting everyone onto the active character's
    # hex is what makes a whole-party combat reproducible.
    state = drive.run("crucible_overworld_state")
    lead = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", state)
    if lead:
        gathered = drive.run("crucible_party_set_hex", [lead.group(1), lead.group(2)])
        print("gathered party at (%s,%s): %s" % (
            lead.group(1), lead.group(2), gathered.splitlines()[0] if gathered else "(no result)"))
        drive.wait_until(drive.interaction_enabled, timeout_seconds=6, interval=0.3)

    # Bring the fight to the party rather than walking into one.
    spawn = drive.run("crucible_debug_spawn", ["enemies", options.enemy])
    if "invoked=True" not in spawn:
        print("spawn failed:\n%s" % spawn)
        return 3
    print("spawned %s" % options.enemy)
    # The encounter menu appears when it appears; press_fight already polls for it, so sleeping a
    # flat 6s here just added 6s to every single run.
    drive.wait_until(lambda: "fight" in drive.run("crucible_ui_dump", ["-", "button"]).lower(),
                     timeout_seconds=8, interval=0.3)

    # The spawn USUALLY opens the encounter menu on the party's own hex, but where it places the
    # enemy varies, and when it lands out of reach no menu appears at all. So: try the menu, and if
    # it never shows, walk onto a roaming encounter instead. Both routes end at the same Fight
    # button; only the way the menu is raised differs.
    if not press_fight(timeout_seconds=40):
        print("no menu from the spawn; walking onto a roaming encounter instead")
        if not walk_onto_encounter():
            print("could not reach any roaming encounter")
            return 4
        if not press_fight(timeout_seconds=60):
            print("reached an encounter but the Fight button never appeared")
            return 4

    combat = wait_for_combat()
    if combat is None:
        print("combat never became active; last state: %s" % drive.describe())
        return 5

    print("COMBAT ACTIVE: combatants=%s round=%s turn=%s" % (
        len(combat.get("combatants") or []), combat.get("round"), combat.get("turn")))

    if options.godmode:
        print("godmode: %s" % drive.run("crucible_godmode", ["on"]).splitlines()[0])
    return 0


if __name__ == "__main__":
    sys.exit(main())
