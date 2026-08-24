#!/usr/bin/env python3
"""
Drive a loaded adventure to its WIN quest, unattended.

The strategy is the one the decompilation settled: an adventure ends when a COMPLETED quest carries
AdventureEndTrigger == WIN, and an objective can be completed by flagging it, because
QuestHelper.CheckObjectiveCompletion short-circuits on
`if (pQuest.CompletedObjectives[i/2]) return true;` before evaluating any world condition.

Two hard-won rules shape the loop:

1. COMPLETE ONE QUEST PER PASS. _tryCompleteQuests ends with
   `if (completedQuests.Count(x => x.Data.QuestType != GENERIC_ADVENTURE) > 0) { ... await
   _tryCompleteQuests(); }` -- it RECURSES. A quest that is flagged complete but does not leave
   ActiveQuests is therefore returned by GetCompletedQuests on every recursion, and the game hangs
   outright. Measured 2026-08-24: flagging two quests in one pass froze the process (Responding =
   False, pump dead, only a kill recovered it).

2. NEVER RE-FLAG A STUCK QUEST. The same recursion makes a quest that refuses to resolve actively
   dangerous rather than merely useless, so any quest still active after being flagged is put on a
   blocklist and left alone. STORY_1_1's save carries two identical
   GENERIC_ADVENTURE_PRISMATIC_FISH_00 entries, and one of them never resolves.

The chain also cannot be walked by flags alone: promotion into ActiveQuests is gated on
QuestStartWorldTriggers (STORY_1_1_COMPLETE_TASKS needs chaos stage 3), so when progress stalls the
WIN quest is activated directly with crucible_quest_activate.

Victory is asserted from crucible_endadventure_watch, NOT from the route: _endAdventure sends a win
and a loss to the same screen. The watch is a Harmony prefix on GameplayDirectorBase._endAdventure
that latches pIsVictory and snapshots the save, which matters because a victorious run DELETES its
own save on the way out.

Usage:
    python FTK2.Crucible/tools/run_to_victory.py [--rounds N] [--run <runId>]
"""

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

COMPLETABLE = ("STORY", "STORY_OPTIONAL", "SIDE_MISSION", "SYSTEM", "GENERIC_ADVENTURE")

# The WIN quest per adventure: _tryCompleteQuests calls _endAdventure(true) for any completed quest
# whose AdventureEndTrigger is WIN.
WIN_QUESTS = {"STORY_1_1": "STORY_1_1_CLEAR_BANDIT_KING"}


def parse_status(text):
    status = {"active": [], "completed": [], "won": False, "map": None, "raw": text}
    section = None
    for line in text.splitlines():
        stripped = line.strip()
        if line.startswith("map="):
            status["map"] = line.split()[0][len("map="):]
            section = None
            continue
        if line.startswith("activeQuests:"):
            section = "active"
            continue
        if line.startswith("completedQuests:"):
            status["completed"] = _ids_in_brackets(line)
            section = None
            continue
        if line.startswith("WIN quest completed:"):
            status["won"] = "YES" in line
            section = None
            continue
        if line.startswith(("failedQuests:", "futureQuests:", "inDungeon=", "lastPump=")):
            section = None
            continue
        if section == "active" and stripped and not stripped.startswith("("):
            parts = stripped.split()
            quest_type = ""
            flags = ""
            for part in parts[1:]:
                if part.startswith("type="):
                    quest_type = part[len("type="):]
                elif part.startswith("objectives="):
                    flags = part[len("objectives="):]
            status["active"].append((parts[0], quest_type, flags))
    return status


def _ids_in_brackets(line):
    if "[" not in line:
        return []
    inner = line[line.index("[") + 1:line.rindex("]")]
    return [x.strip() for x in inner.split(",") if x.strip()]


def alive():
    """True while the game thread still answers. A dead pump means a hang, not a slow frame."""
    try:
        return bool(drive._get("/health", attempts=1).get("pumpAlive"))
    except Exception:
        return False


def wait_until_alive(seconds=60):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if alive():
            return True
        time.sleep(3)
    return False


def ended():
    text = drive.run("crucible_endadventure_watch", ["", ""])
    return ("ended=True" in text, "victory=True" in text, text)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rounds", type=int, default=40)
    parser.add_argument("--run", dest="run_id")
    options = parser.parse_args()

    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    if options.run_id:
        print(drive.load_run(options.run_id))

    print("tutorials: %s" % drive.run("crucible_tutorials_suppress", []).splitlines()[0])
    drive.clear_gates()

    blocked = set()        # quests flagged complete that refused to leave ActiveQuests
    attempted = {}         # quest id -> passes since it was flagged
    forced_win = False
    stalls = 0

    for round_index in range(1, options.rounds + 1):
        if not alive():
            print("\nGAME HUNG on pass %d (pump dead). Last actions above are the cause." % round_index)
            return 5

        was_ended, victory, detail = ended()
        if was_ended:
            print("\n=== run ended on pass %d ===" % round_index)
            print(detail)
            return 0 if victory else 1

        status = parse_status(drive.run("crucible_run_status", []))
        active_ids = [q[0] for q in status["active"]]

        # Anything flagged in an earlier pass that is STILL active has not resolved. Re-flagging it
        # feeds the _tryCompleteQuests recursion, so retire it instead.
        for quest_id in list(attempted):
            if quest_id in active_ids:
                attempted[quest_id] += 1
                if attempted[quest_id] >= 2 and quest_id not in blocked:
                    blocked.add(quest_id)
                    print("   blocked (will not resolve): %s" % quest_id)
            else:
                attempted.pop(quest_id, None)

        todo = [q for q in status["active"]
                if q[1] in COMPLETABLE and "0" in q[2] and q[0] not in blocked]

        print("pass %2d: %d active, %d actionable, %d blocked%s" % (
            round_index, len(status["active"]), len(todo), len(blocked),
            "  [WIN quest completed]" if status["won"] else ""))

        if not todo:
            stalls += 1
            if stalls == 2 and not forced_win:
                win_quest = WIN_QUESTS.get(status["map"])
                if win_quest and win_quest not in active_ids:
                    print("   chain stalled -- activating the WIN quest directly: %s" % win_quest)
                    print("   %s" % drive.run("crucible_quest_activate", [win_quest]).splitlines()[0])
                    forced_win = True
                    stalls = 0
                    continue
            if stalls >= 4:
                print("\nno actionable objectives for 4 passes -- stopping.")
                print(status["raw"])
                return 3
            time.sleep(4)
            continue
        stalls = 0

        # ONE per pass. See rule 1 in the module docstring -- this is what stops the hang.
        quest_id = todo[0][0]
        result = drive.run("crucible_quest_complete_objective", [quest_id, "all"])
        print("   %-40s %s" % (quest_id, result.splitlines()[0] if result else "(no result)"))
        attempted.setdefault(quest_id, 0)

        # Resolution plays dialogue and rewards on the game thread; give it real time, then confirm
        # the game is still answering before doing anything else.
        time.sleep(6)
        if not wait_until_alive(60):
            print("\nGAME HUNG resolving %s." % quest_id)
            return 5
        drive.clear_gates()

    print("\nran out of passes (%d) without an end-of-adventure." % options.rounds)
    print(drive.run("crucible_run_status", []))
    return 4


if __name__ == "__main__":
    sys.exit(main())
