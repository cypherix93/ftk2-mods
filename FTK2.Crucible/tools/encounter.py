#!/usr/bin/env python3
"""
Build a reproducible combat encounter to test summons, items, abilities and classes against.

One command takes a cold game to a live fight with an exact roster on both sides:

    python FTK2.Crucible/tools/encounter.py --enemies BEE_WORKER_01:3 --allies WOLF_CHAOSHOUND_01:1

Why this exists rather than "just start a fight": a shipped encounter gives you whatever it gives
you. Testing a summon needs a KNOWN board -- a free ally tile for the creature to land on, enough
enemies to reach a kill-gated trigger, and the same layout every run so a failure means the feature
broke rather than the dice changed.

Two rules the game enforces, both learned the hard way:

* A creature's ALLEGIANCE comes from the tile it is placed on -- `TryCreateSummon` copies the
  tile's GroupIndex onto the new character. Summoning onto a slain enemy's tile produces a HOSTILE
  "ally". Group 0 tiles are the party's side, group 1 the enemy's.
* Both sides have finite tiles (typically 4 front + 4 back each). Asking for more creatures than
  there are free tiles places as many as fit and reports the shortfall rather than stacking them,
  because stacked monsters make the fight unplayable.

Usage:
    --enemies CONFIG:COUNT[,CONFIG:COUNT...]   creatures on group 1
    --allies  CONFIG:COUNT[,CONFIG:COUNT...]   creatures on group 0
    --active  CLASS_ID                         which class holds the turn when the fight starts
    --godmode                                  keep the party alive so a trait can be observed
    --list                                     print the free tiles per side and exit
"""

import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import to_combat  # noqa: E402


def parse_roster(text):
    """'BEE_WORKER_01:3,SPIDER_GENERIC_02:1' -> [('BEE_WORKER_01', 3), ('SPIDER_GENERIC_02', 1)]"""
    out = []
    for chunk in (text or "").split(","):
        chunk = chunk.strip()
        if not chunk:
            continue
        if ":" in chunk:
            name, count = chunk.rsplit(":", 1)
            out.append((name.strip(), max(1, int(count))))
        else:
            out.append((chunk, 1))
    return out


def free_tiles():
    """{group: [(x, y), ...]} of tiles with no living character on them."""
    snapshot = drive.run("crucible_combat_snapshot")
    occupied = set()
    for line in snapshot.splitlines():
        m = re.match(r"\s*[0-9a-f-]{36} name=\S+ class=\S+ group=\d+ hp=(\d+) tile=\((\d+), (\d+)\)",
                     line)
        if m and int(m.group(1)) > 0:
            occupied.add((int(m.group(2)), int(m.group(3))))

    tiles = {}
    for line in snapshot.splitlines():
        m = re.match(r"\s*\((\d+), (\d+)\) group=(-?\d+)", line)
        if not m:
            continue
        pos, group = (int(m.group(1)), int(m.group(2))), int(m.group(3))
        if group < 0 or pos in occupied:
            continue
        tiles.setdefault(group, []).append(pos)
    return tiles


def populate(roster, group):
    placed = 0
    for config, count in roster:
        result = drive.run("crucible_combat_spawn", [config, str(count), str(group)])
        got = re.search(r"placed=(\d+)", result)
        n = int(got.group(1)) if got else 0
        placed += n
        print("  group %d: %-26s requested %d, placed %d" % (group, config, count, n))
        if n < count:
            print("     (short by %d -- that side has no more free tiles)" % (count - n))
    return placed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--enemies", default="")
    parser.add_argument("--allies", default="")
    parser.add_argument("--active")
    parser.add_argument("--run", dest="run_id", default=to_combat.DEFAULT_RUN)
    parser.add_argument("--godmode", action="store_true")
    parser.add_argument("--in-combat", action="store_true",
                        help="already in a fight; just add the roster")
    parser.add_argument("--list", action="store_true", help="print free tiles and exit")
    options = parser.parse_args()

    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    if options.list:
        for group, tiles in sorted(free_tiles().items()):
            side = "party" if group == 0 else "enemy"
            print("group %d (%s): %d free -- %s" % (group, side, len(tiles), sorted(tiles)))
        return 0

    if not options.in_combat:
        argv = ["--run", options.run_id]
        if options.active:
            argv += ["--active", options.active]
        if options.godmode:
            argv += ["--godmode"]
        code = to_combat.main.__wrapped__(argv) if hasattr(to_combat.main, "__wrapped__") else None
        if code is None:
            saved = sys.argv
            sys.argv = ["to_combat.py"] + argv
            try:
                code = to_combat.main()
            finally:
                sys.argv = saved
        if code != 0:
            print("could not reach combat (exit %s)" % code)
            return code

    print("\nbuilding the roster:")
    populate(parse_roster(options.allies), 0)
    populate(parse_roster(options.enemies), 1)

    print("\nfinal board:")
    for line in drive.run("crucible_combat_snapshot").splitlines():
        if "class=" in line and "group=" in line:
            print("  " + line.strip()[:100])

    remaining = free_tiles()
    print("\nfree tiles left: " + ", ".join(
        "group %d: %d" % (g, len(t)) for g, t in sorted(remaining.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
