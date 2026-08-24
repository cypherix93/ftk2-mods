#!/usr/bin/env python3
"""
Per-class sweep: build a party from authored classes and check each one in the LIVE game.

Four characters fit in one save, so 31 classes take 8 loads rather than 31. Each batch clones
the pristine template, swaps the four slots to the batch's classes, equips each class's starter
weapon, then runs the checklist and saves the result as a reusable fixture.

Checklist per class (all against the running game, not against JSON):
  config_present    the class id resolves in the live Configs.Characters map
  passives_declared every passive the pack declares is present in the LIVE merged config
  class_applied     the swap actually took on the party slot
  weapon_equipped   the class's starter weapon is the equipped weapon afterwards
  abilities_present the character offers at least one ability (they come from the WEAPON)
  loc_key           the config carries a LocKey

Why the weapon check is not optional: abilities come from the equipped weapon, not the class.
A class swapped without its weapon offers the PREVIOUS class's abilities, which looks like a
working class and is not.

Usage:
    python FTK2.Crucible/tools/class_sweep.py --template <runId> [--batches N] [--json out.json]
"""

import argparse
import json
import os
import subprocess
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PACK = os.path.join(REPO_ROOT, "FTK2.ClassForge", "data", "ClassPacks", "CF_PACK_EOR_CLASSES")
STARTER_MAP = os.path.join(REPO_ROOT, "FTK2.Crucible", "data", "class-starter-weapons.json")
TEMPLATE_FILE = os.path.join(
    REPO_ROOT, "FTK2.Crucible", "data", "Fixtures", "overworld-four-classes", "run.ftk2")
GAME_RUNS = os.path.expandvars(
    r"%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\GameRuns")


def load_json(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def restart_game(timeout=240):
    """
    Hard restart, used when an in-place reload will not do.

    Returning to the main menu is much faster and works from a clean overworld, but after a
    combat the VenueUIDocument stays on screen and the router will not open adventure selection
    again -- campaign-btn is visible and clickable and simply does nothing. Rather than leave a
    batch stuck, fall back to a restart, which always works.
    """
    subprocess.run([
        "powershell", "-NoProfile", "-Command",
        r"Get-Process | Where-Object { $_.Path -like '*For The King II\For The King*' } "
        "| ForEach-Object { Stop-Process -Id $_.Id -Force }; Start-Sleep -Seconds 4; "
        "Start-Process 'steam://rungameid/1676840'",
    ], capture_output=True)
    return drive.boot(timeout_seconds=timeout)


# A base save the GAME wrote, not a file copy.
#
# Copying a .ftk2 to a new GUID filename produces a save the game silently refuses to load: the
# run id is recorded inside the file, so a copy under a different name no longer matches. The
# file resolves on disk, no error is logged, and _loadGameRun simply does nothing. Measured
# 2026-08-24: a game-written save loaded while a byte-identical copy under a new name did not.
#
# So batches all start from one game-written base and persist their result through the game's own
# WriteSaveData (crucible_fixture_save), which produces a genuinely loadable fixture.
BASE_RUN_ID = "ef0bac05-db36-4eb5-9fe8-1f10faf8d857"


def check_class(slot, class_id, starter, declared_passives):
    """Runs the checklist for one slot and returns a result dict."""
    result = {"class": class_id, "slot": slot, "starter": starter, "checks": {}, "notes": []}

    config = drive.run("crucible_class_config", [class_id])
    result["checks"]["config_present"] = "PRESENT=True" in config

    missing = [p for p in declared_passives if p not in config]
    result["checks"]["passives_declared"] = not missing
    if missing:
        result["notes"].append("passives missing from live config: %s" % ", ".join(missing))

    result["checks"]["loc_key"] = "locKey=" in config and "locKey=(null)" not in config

    party = drive.run("crucible_party_list", [])
    line = ""
    for row in party.splitlines():
        if row.strip().startswith("[%d]" % slot):
            line = row
            break
    result["checks"]["class_applied"] = ("classId=%s" % class_id) in line
    if not result["checks"]["class_applied"]:
        result["notes"].append("party slot reads: %s" % line.strip())

    abilities = drive.run("crucible_party_abilities", [str(slot)])
    result["checks"]["weapon_equipped"] = ("equippedWeapon: %s" % starter) in abilities
    if not result["checks"]["weapon_equipped"]:
        for row in abilities.splitlines():
            if row.startswith("equippedWeapon:"):
                result["notes"].append("equipped %s (wanted %s)" % (row.split(":", 1)[1].strip(), starter))
                break

    # "abilities: count=0 []" is a real failure; "(null)" means unreadable, which is a different
    # finding and must not be scored as a pass.
    has_abilities = False
    for row in abilities.splitlines():
        if row.startswith("abilities:"):
            body = row.split(":", 1)[1].strip()
            has_abilities = body.startswith("count=") and not body.startswith("count=0")
            if body == "(null)":
                result["notes"].append("abilities unreadable")
            break
    result["checks"]["abilities_present"] = has_abilities

    result["passed"] = all(result["checks"].values())
    return result


def run_batch(batch, classes, starters, index, total):
    print("\n=== batch %d/%d: %s ===" % (index, total, ", ".join(batch)))
    run_id = BASE_RUN_ID
    print("  loading base save %s" % run_id)

    transcript = drive.load_run(run_id)
    if "FAILED" in transcript:
        print("  in-place load failed; restarting the game and retrying")
        if not restart_game():
            return [{"class": c, "slot": i, "passed": False, "checks": {},
                     "notes": ["game did not come back after restart"]} for i, c in enumerate(batch)]
        transcript = drive.load_run(run_id)
        if "FAILED" in transcript:
            print(transcript)
            return [{"class": c, "slot": i, "passed": False, "checks": {},
                     "notes": ["batch load failed after restart"]} for i, c in enumerate(batch)]

    drive.clear_gates()

    # All class swaps FIRST, then all equips. Interleaving them lets a later swap undo an
    # earlier equip: swapping a class rebuilds the character's kit from its class config, so an
    # equip performed before the last swap is silently overwritten.
    for slot, class_id in enumerate(batch):
        swap = drive.run("crucible_party_set_class", [str(slot), class_id])
        if "changed=True" not in swap:
            print("  slot %d swap did not take: %s" % (slot, swap.splitlines()[0] if swap else "(no result)"))

    for slot, class_id in enumerate(batch):
        equip = drive.run("crucible_equip", [str(slot), starters[class_id]])
        if "changed=True" not in equip:
            print("  equip[%d] FAILED: %s" % (slot, equip.splitlines()[0] if equip else "(no result)"))

    results = []
    for slot, class_id in enumerate(batch):
        declared = classes[class_id].get("Passives") or []
        outcome = check_class(slot, class_id, starters[class_id], declared)
        results.append(outcome)
        flags = "".join("." if ok else "X" for ok in outcome["checks"].values())
        print("  %-24s %s %s" % (class_id, flags, "PASS" if outcome["passed"] else "FAIL"))
        for note in outcome["notes"]:
            print("      %s" % note)

    saved = drive.run("crucible_fixture_save", ["class-sweep-batch-%d" % index])
    fixture_id = None
    for token in saved.split():
        if token.startswith("newRunId="):
            fixture_id = token.split("=", 1)[1]
    print("  fixture saved: %s" % fixture_id)
    for outcome in results:
        outcome["fixture"] = fixture_id
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--batches", type=int, default=0, help="limit batches (0 = all)")
    parser.add_argument("--json", dest="json_out")
    options = parser.parse_args()

    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    classes = load_json(os.path.join(PACK, "classes.json"))
    starters = load_json(STARTER_MAP)
    class_ids = sorted(classes.keys())

    batches = [class_ids[i:i + 4] for i in range(0, len(class_ids), 4)]
    if options.batches:
        batches = batches[:options.batches]

    print("sweeping %d classes in %d batches of up to 4" % (
        sum(len(b) for b in batches), len(batches)))

    results = []
    for index, batch in enumerate(batches, start=1):
        results.extend(run_batch(batch, classes, starters, index, len(batches)))

    passed = [r for r in results if r.get("passed")]
    failed = [r for r in results if not r.get("passed")]

    print("\n=== summary ===")
    print("%d passed, %d failed, of %d classes" % (len(passed), len(failed), len(results)))
    for outcome in failed:
        bad = [name for name, ok in outcome.get("checks", {}).items() if not ok]
        print("  FAIL %-24s %s" % (outcome["class"], ", ".join(bad) or "load failed"))
        for note in outcome.get("notes", []):
            print("         %s" % note)

    if options.json_out:
        with open(options.json_out, "w", encoding="utf-8") as handle:
            json.dump(results, handle, indent=1, sort_keys=True)
        print("wrote %s" % options.json_out)

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
