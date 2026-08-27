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
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

# Clean test bed: the three original classes + an EOR control, with every quest removed and
# every non-combat encounter cleared. Both matter for speed and reliability -- a quest whose
# objectives are flagged but unresolved re-queues its reward prompt on EVERY load and holds
# interaction off, and walking anywhere used to trip the Night Merchant or a shop.
# REBUILT 2026-08-25. The previous fixture (4c0f1f9f-...) was DESTROYED by this session's own
# DEFEAT testing: _endAdventure rewrote it to a post-defeat state and it shrank from 5.34 MB to
# 552 KB, after which it would no longer load into a playable run. It was not recoverable from the
# 2026-08-23 backup because the fixture post-dates it.
#
# THE HAZARD, worth understanding before driving another run to an ending: a run that ENDS -- win
# OR lose -- rewrites and can delete its own save (SaveGameHelper.DeleteSave). Any suite that
# drives a run to completion can destroy the fixture every other suite depends on. Save a fresh
# fixture BEFORE such a run, not after.
#
# This one carries the party the trait tests need: Vampiric / Pacifist / Pokemon Trainer, plus an
# EOR Runemage as a control.
DEFAULT_RUN = "bdb1596d-86ee-4783-9d8f-fb49934a0770"


def restart_game(timeout_seconds=240):
    """
    Kills the game by PID and relaunches it by exe path, NOT via drive.restart_game().

    drive.restart_game() launches through 'steam://rungameid/1676840', a fire-and-forget URI
    handoff to Steam: if Steam is not already up (or ignores the request) the game process never
    appears, boot() burns its full timeout, and this script reports "game did not come back after
    restart" -- which then went on to KILL a game that was still running, mid-verification, in a
    session this same bug was hit live in. class_sweep.py's restart_game() hit the identical
    problem and fixed it by launching the exe directly instead of the steam: URI; this mirrors
    that fix. Same narrow PID-scoped kill filter as drive.restart_game() -- deliberately not
    broadened, and it never matches node.
    """
    subprocess.run([
        "powershell", "-NoProfile", "-Command",
        "Get-Process | Where-Object { $_.Path -like '*For The King II\\For The King*' } "
        "| ForEach-Object { Stop-Process -Id $_.Id -Force }; Start-Sleep -Seconds 6; "
        # Direct exe launch, NOT steam://rungameid/1676840 -- see docstring above.
        "Start-Process 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\For The King II\\For The King II.exe'",
    ], capture_output=True)
    return drive.boot(timeout_seconds=timeout_seconds)


def press_fight(timeout_seconds=40):
    """Presses Fight as soon as the encounter menu offers it. Returns whether it landed.

    Only the FAST path -- the debug spawn's own menu, which really does put a literal "Fight"
    button on screen. Everything else (Attempt, Ambush, a bespoke SkillEncounterConfig label,
    no button at all) belongs to drive.enter_combat(); do not grow this into a second copy of it.
    """
    if drive.in_combat():
        return True
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        dump = drive.run("crucible_ui_dump", ["-", "button"])
        if "text='Fight'" in dump:
            if "invoked=True" in drive.run("crucible_ui_click", ["Fight"]):
                print("pressed Fight")
                return True
        if drive.in_combat():
            return True
        drive.wait_ready(timeout_seconds=20)
    return False


def walk_onto_encounter():
    """
    Steps onto the nearest ROAMING encounter with the encounter menu enabled.

    THE PROVEN PATH. spawn an enemy -> walk onto the roaming encounter it creates -> press Fight
    reached combat ~8 times in one session from the base fixture. It is the PRIMARY route and is
    tried before drive.enter_combat(), which is the fallback for when it yields no menu.

    Restored 2026-08-25 after being briefly replaced by a call to drive.enter_combat(). That swap
    was a regression: a measured-working path was traded for an unexercised one.

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
    parser.add_argument("run_id_arg", nargs="?", default=None,
                        help="run id to drive into combat; falls back to the CRUCIBLE_RUN_ID env "
                             "var, then the hardcoded default fixture")
    parser.add_argument("--run", dest="run_id", default=None,
                        help="same as the positional run id argument")
    parser.add_argument("--enemy", default="BANDIT_RANGED_01")
    parser.add_argument("--godmode", action="store_true")
    parser.add_argument("--active",
                        help="end overworld turns until this class is the ACTIVE character, so it "
                             "is the one that walks into the fight (e.g. CF_ORIG_TRAINER)")
    parser.add_argument("--skip-load", action="store_true",
                        help="already in a run; just spawn and fight")
    options = parser.parse_args()

    # Run id precedence: positional arg > --run flag > CRUCIBLE_RUN_ID env var > hardcoded
    # default. A bare `python to_combat.py` hits none of the first three and falls through to
    # DEFAULT_RUN exactly as before this option was added.
    if options.run_id_arg:
        options.run_id, source = options.run_id_arg, "arg"
    elif options.run_id:
        source = "--run flag"
    elif os.environ.get("CRUCIBLE_RUN_ID"):
        options.run_id, source = os.environ["CRUCIBLE_RUN_ID"], "env CRUCIBLE_RUN_ID"
    else:
        options.run_id, source = DEFAULT_RUN, "default"
    print("run id: %s (source: %s)" % (options.run_id, source))

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
            if not restart_game():
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
        if not restart_game():
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
        # PRIMARY: the path that worked ~8 times in one session -- walk onto the roaming encounter
        # the spawn created and press Fight. Tried FIRST, always.
        print("no menu from the spawn; walking onto a roaming encounter instead")
        reached = walk_onto_encounter() and press_fight(timeout_seconds=60)

        # FALLBACK, only once the proven path has actually failed: drive.enter_combat() rescans,
        # skips nodes whose ActionList cannot start a fight, retries across several of them, and
        # recovers interactionEnabled between attempts.
        if not reached:
            print("walk + Fight did not reach combat; falling back to drive.enter_combat()")
            outcome = drive.enter_combat()
            print("enter_combat ok=%s attempts=%d reason=%s"
                  % (outcome["ok"], outcome["attempts"], outcome["reason"]))
            if not outcome["ok"]:
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
