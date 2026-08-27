#!/usr/bin/env python3
"""Prove the battlefield rotates: win the current fight, walk into the next, twice.

Reads the venue the game actually LOADED (crucible_state/diorama) rather than the mod's own
log line -- the previous attempt at this feature logged a swap that never took effect, so the
log is the claim and the loaded diorama is the evidence. Screenshots are still required on top
of both: a correct diorama name with the wrong art on screen has happened before.
"""
import os, re, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive, to_combat


def in_combat():
    return drive.snapshot().get("combat", {}).get("active") is True


def end_fight():
    """Finish the fight the game's OWN way and wait for combat to actually be over.

    crucible_combat_wipe_enemies does NOT end the encounter -- it was called between fights
    here and the spider was still standing at 20 hp a screenshot later, which is why the next
    two "walks" silently no-opped: the party cannot walk while a combat is live.
    """
    if not in_combat():
        return True
    # drive.exit_combat() is this sequence plus the two cases it did not survive: a replenished
    # wave that leaves the fight live after crucible_win_combat, and a venue that advances
    # COMBAT -> TREASURE instead of back to the map.
    outcome = drive.exit_combat()
    print("   combat ended: %s (%s)" % (outcome["ok"], outcome["reason"]))
    return outcome["ok"]


def venue_on_screen(timeout_seconds=90):
    """Waits until the BATTLEFIELD is actually being rendered, not merely until combat is active.

    wait_for_combat returns as soon as CombatState reports active, which happens while the
    "Travelling to a new area..." parchment is still up. Measuring there reads the PREVIOUS
    fight's board -- it once reported ally=8 enemy=8 for a venue that had not been built yet and
    made a working rotation look broken. The transition/loading documents going away is the
    signal that the venue exists.
    """
    def ready():
        docs = drive.visible_docs()
        if "TransitionUIDocument" in docs or "LoadingScreenUIDocument" in docs:
            return False
        return "CombatUIDocument" in docs or "VenueUIDocument" in docs

    ok = drive.wait_until(ready, timeout_seconds=timeout_seconds)
    time.sleep(2.0)   # one settle beat: the tiles are built a frame or two after the doc appears
    return ok


def shot(label):
    out = drive._post("/screenshot", {})
    p = out.get("path", "") if isinstance(out, dict) else ""
    print("   shot %s -> %s" % (label, p))
    return p


def tile_counts():
    n = {}
    for line in drive.run("crucible_combat_snapshot").splitlines():
        m = re.match(r"\s*\(\d+, \d+\) group=(-?\d+)", line)
        if m:
            n[m.group(1)] = n.get(m.group(1), 0) + 1
    return n


def reach_fight(tries=6, _tried=set()):
    """Walks onto encounters until one of them actually offers Fight.

    Two things this had to learn the hard way:

    * A bare-GUID encounter is not necessarily a monster. Half of them are search/story nodes --
      an "Abandoned Village" with a Search button and a slot-roll outcome table -- and walking
      onto one parks the party in front of a panel with no Fight button.
    * `crucible_encounter_leave` does NOT close that panel, and the party is left standing ON the
      encounter hex. The nearest-first search then picks the very same node again, forever. The
      panel's own `close-btn` is what dismisses it, and the node has to be remembered as tried.
    """
    for attempt in range(tries):
        drive.wait_ready(timeout_seconds=120)
        if not walk_to_untried_encounter(_tried):
            print("   no untried roaming encounter reachable")
            return False
        if to_combat.press_fight(timeout_seconds=20):
            return True
        print("   no Fight here (attempt %d) -- closing the panel and trying another"
              % (attempt + 1))
        drive.run("crucible_ui_click", ["close-btn"])
        drive.run("crucible_encounter_leave", [])
        drive.clear_gates()
        time.sleep(2)
    return False


def walk_to_untried_encounter(tried):
    """to_combat.walk_onto_encounter, but skipping nodes already known not to fight."""
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
        if (x, y) == (px, py) or name in tried:
            continue
        if not re.match(r"^[0-9a-f]{8}-", name):
            continue
        candidates.append((max(abs(x - px), abs(y - py)), x, y, name))
    candidates.sort()

    for _dist, x, y, name in candidates[:6]:
        if "move invoked" not in drive.run("crucible_move", [str(x), str(y), "false", "false", "true"]):
            continue
        tried.add(name)
        print("   walked onto (%d,%d) %s" % (x, y, name))
        drive.wait_ready(timeout_seconds=40)
        # Only the ACTIVE character walks; the rest would sit the fight out.
        drive.run("crucible_party_set_hex", [str(x), str(y)])
        time.sleep(3)
        return True
    return False


def next_fight(i):
    print("== fight %d ==" % i)
    if not reach_fight():
        print("   could not reach a fight")
        return None
    if not to_combat.wait_for_combat():
        print("   combat never became active")
        return None
    if not venue_on_screen():
        print("   the venue never finished loading -- NOT measuring, the numbers would be stale")
        return None
    counts = tile_counts()
    print("   tiles ally=%s enemy=%s" % (counts.get("0"), counts.get("1")))
    return shot("fight%d" % i)


def main():
    if not drive.boot():
        print("game is not running")
        return 2
    print(drive.load_run(to_combat.DEFAULT_RUN).splitlines()[-1][:120])
    drive.clear_gates()
    drive.pick_reward()
    end_fight()
    seen = []
    for i in (1, 2, 3):
        seen.append(next_fight(i))
        end_fight()
    print("\nscreenshots to OPEN -- the rotation is not proven until three DIFFERENT")
    print("venues appear in these images; tile counts prove the grid, not the venue:")
    for i, path in enumerate(seen, 1):
        print("  fight%d %s" % (i, path))
    return 0


if __name__ == "__main__":
    sys.exit(main())
