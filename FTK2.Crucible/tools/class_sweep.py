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
import urllib.error
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CLASS_PACKS_DIR = os.path.join(REPO_ROOT, "FTK2.ClassForge", "data", "ClassPacks")
# Default kept as the EOR pack so an argument-free run behaves exactly as before. The
# ORIGINALS pack (Vampiric / Pacifist / Pokemon Trainer / Gary / Chaos Mage) was NOT reachable
# from this tool at all until --pack existed, so "the class sweep is green" had never included
# a single one of Ben's own classes.
DEFAULT_PACK = "CF_PACK_EOR_CLASSES"
PACK = os.path.join(CLASS_PACKS_DIR, DEFAULT_PACK)
STARTER_MAP = os.path.join(REPO_ROOT, "FTK2.Crucible", "data", "class-starter-weapons.json")
TEMPLATE_FILE = os.path.join(
    REPO_ROOT, "FTK2.Crucible", "data", "Fixtures", "overworld-four-classes", "run.ftk2")
GAME_RUNS = os.path.expandvars(
    r"%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\GameRuns")


def resolve_starters(classes, starter_map):
    """Starter weapon per class: the curated map first, the pack's own Things as the fallback.

    class-starter-weapons.json was written for CF_PACK_EOR_CLASSES and has NO entry for any
    CF_ORIG_* class, so before this fallback existed `--pack CF_PACK_ORIGINALS` could only ever
    KeyError. The fallback reads the class's own `Things` and takes the one matching the pack's
    ARM_*_STARTER_* naming convention -- verified 2026-08-26 to resolve all five originals
    (e.g. CF_ORIG_VAMPIRIC -> ARM_ORIG_STARTER_VAMPIRIC_BLOODLETTER).

    The convention filter is NOT cosmetic: Gary and the Trainer each carry a capture ball in Things
    alongside the weapon (ARM_ORIG_TRAINER_BALL_*), and equipping the ball instead of the weapon
    would silently swap the class's whole ability set -- exactly the failure the weapon_equipped
    check exists to catch.
    """
    resolved, unresolved = {}, []
    for class_id, config in classes.items():
        if class_id in starter_map:
            resolved[class_id] = starter_map[class_id]
            continue
        things = (config or {}).get("Things") or {}
        arms = [t for t in things if t.startswith("ARM_")]
        starter = next((t for t in arms if "_STARTER_" in t), None)
        if starter is None and len(arms) == 1:
            starter = arms[0]
        if starter is None:
            unresolved.append(class_id)
        else:
            resolved[class_id] = starter
    return resolved, unresolved


def player_classes(classes):
    """Only configs tagged PLAYER are player classes.

    CF_PACK_ORIGINALS also ships creature configs (JELLY_*, DEMON_*, BOSS_*, PLANT_*, ELEMENTAL_*)
    -- 12 of its 17 entries. Sweeping those as if they were playable would report a dozen bogus
    failures and bury the five that matter.
    """
    return {cid: cfg for cid, cfg in classes.items()
            if "PLAYER" in ((cfg or {}).get("Tags") or [])}


def load_json(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def _game_process_running():
    """True if a 'For The King II\\For The King*' process currently exists.

    Same narrow path filter as the Stop-Process call below, kept read-only -- used only to
    tell "Steam never launched it" apart from "it launched but the RPC pump is not up yet"
    when boot() times out.
    """
    check = subprocess.run([
        "powershell", "-NoProfile", "-Command",
        r"if (Get-Process | Where-Object { $_.Path -like '*For The King II\For The King*' }) "
        "{ 'yes' } else { 'no' }",
    ], capture_output=True, text=True)
    return "yes" in (check.stdout or "")


def restart_game(timeout=240):
    """
    Hard restart, used when an in-place reload will not do.

    Returning to the main menu is much faster and works from a clean overworld, but after a
    combat the VenueUIDocument stays on screen and the router will not open adventure selection
    again -- campaign-btn is visible and clickable and simply does nothing. Rather than leave a
    batch stuck, fall back to a restart, which always works.

    Launches via steam://rungameid/1676840, which is a fire-and-forget URI handoff to Steam --
    if Steam is slow to react or swallows the request, the game process may never appear and
    drive.boot() will simply time out with no other signal. So boot() failing gets one full
    relaunch retry (kill/relaunch/wait again) before this gives up, and the process-existence
    check is used only to make the failure message legible, never to change control flow.
    """
    def _relaunch():
        subprocess.run([
            "powershell", "-NoProfile", "-Command",
            r"Get-Process | Where-Object { $_.Path -like '*For The King II\For The King*' } "
            "| ForEach-Object { Stop-Process -Id $_.Id -Force }; Start-Sleep -Seconds 4; "
            # Direct exe launch, NOT steam://rungameid/1676840. The steam: URI is a
            # fire-and-forget handoff: if Steam is not already up (or ignores the request) the
            # process never appears and boot() burns its full 240s timeout. Measured 2026-08-25:
            # every batch needing a restart failed with "process running=False" until this changed.
            r"Start-Process 'C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II.exe'",
        ], capture_output=True)

    _relaunch()
    if drive.boot(timeout_seconds=timeout):
        return True

    print("  restart: boot did not come up within %ds (process running=%s); "
          "retrying the relaunch once" % (timeout, _game_process_running()))
    _relaunch()
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
# The sweep's template save. It MUST be a live `<guid>.ftk2` in
# %USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\GameRuns.
#
# The previous value (ef0bac05-...) is now `_ef0bac05-....json` -- underscore-prefixed and a
# different file type, i.e. SOFT-DELETED. That is why every batch failed to load with
# "run.present never became true within 60s" while to_combat's fixture loaded first try.
# The cause is documented but easy to walk into: _endAdventure calls SaveGameHelper.DeleteSave
# when a run ENDS, so any suite that drives a run to victory OR defeat can destroy a fixture.
# Check the file still exists as .ftk2 before blaming the loader.
BASE_RUN_ID = "bdb1596d-86ee-4783-9d8f-fb49934a0770"


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


def _batch_failure(batch, reason):
    return [{"class": c, "slot": i, "passed": False, "checks": {},
             "notes": [reason]} for i, c in enumerate(batch)]


def _safe_load_run(run_id):
    """drive.load_run(run_id), but treated as a normal FAILED result instead of an exception.

    drive.load_run makes several RPC calls under the hood (drive.run/_post et al.), and
    drive._post RAISES RuntimeError -- not a falsy/"FAILED" string -- once it exhausts its
    retries against an unreachable RPC (e.g. the game process has exited). Confirmed live:
    batch 3/8 of the 31-class sweep died exactly this way ("POST /exec failed after 3
    attempts: ... WinError 10061 ... actively refused") with the game process already gone,
    which propagated straight out of run_batch and killed the remaining 5 batches even though
    the restart-and-retry path two lines below exists to handle precisely this case. Catching
    here lets that existing path run instead of the whole sweep dying.
    """
    try:
        return drive.load_run(run_id)
    except (RuntimeError, urllib.error.URLError, OSError) as exc:
        print("  load_run raised %s: %s" % (type(exc).__name__, exc))
        return "FAILED: load_run raised %s: %s" % (type(exc).__name__, exc)


def run_batch(batch, classes, starters, index, total):
    print("\n=== batch %d/%d: %s ===" % (index, total, ", ".join(batch)))
    # RESTORE THE BASE FIXTURE BEFORE EVERY BATCH.
    #
    # This sweep swaps the party's classes on the LOADED run, and the game autosaves — which writes
    # the swapped party straight back over the base fixture. Measured 2026-08-25: after batch 1 the
    # base held Arcanist/Assassin/Bard/Beastmaster instead of the originals party, and every
    # subsequent suite that depended on it failed with "class not present on the board".
    # A pristine copy lives outside GameRuns for exactly this reason; put it back each time.
    #
    # ensure_fixture/restore_fixture are pure filesystem work (os.path checks + shutil.copy2,
    # with OSError already caught internally and returned as (False, detail)) -- neither makes
    # an RPC call, so neither can raise the RuntimeError an unreachable game process produces.
    # No try/except needed here.
    ok, detail = drive.ensure_fixture(BASE_RUN_ID)
    if not ok:
        print("  base fixture unusable: %s" % detail)
    else:
        restored, rdetail = drive.restore_fixture(BASE_RUN_ID)
        if restored:
            print("  base fixture restored from backup (%s)" % rdetail)

    run_id = BASE_RUN_ID
    print("  loading base save %s" % run_id)

    transcript = _safe_load_run(run_id)
    if "FAILED" in transcript:
        print("  in-place load failed; restarting the game and retrying")
        if not restart_game():
            return _batch_failure(batch, "game did not come back after restart")
        transcript = _safe_load_run(run_id)
        if "FAILED" in transcript:
            print(transcript)
            return _batch_failure(batch, "batch load failed after restart")

    # From here on every step is another RPC call (clear_gates, the swap/equip loops, the
    # checklist, the fixture save), any of which can raise the same RuntimeError if the game
    # process dies mid-batch. One dead batch must not take the rest of the sweep down with it,
    # so the whole per-batch body is guarded and a batch that blows up here is recorded as
    # failed exactly like a load failure, and the sweep moves on to the next batch.
    try:
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
    except (RuntimeError, urllib.error.URLError, OSError) as exc:
        print("  batch %d/%d aborted mid-way by %s: %s" % (index, total, type(exc).__name__, exc))
        return _batch_failure(batch, "batch aborted mid-way: %s: %s" % (type(exc).__name__, exc))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--batches", type=int, default=0, help="limit batches (0 = all)")
    parser.add_argument("--json", dest="json_out")
    parser.add_argument("--pack", default=DEFAULT_PACK,
                        help="class pack folder name under FTK2.ClassForge/data/ClassPacks "
                             "(default %s; e.g. CF_PACK_ORIGINALS)" % DEFAULT_PACK)
    options = parser.parse_args()

    pack_dir = os.path.join(CLASS_PACKS_DIR, options.pack)
    classes_path = os.path.join(pack_dir, "classes.json")
    if not os.path.isfile(classes_path):
        available = sorted(d for d in os.listdir(CLASS_PACKS_DIR)
                           if os.path.isfile(os.path.join(CLASS_PACKS_DIR, d, "classes.json")))
        print("no classes.json for pack %r at %s -- available packs: %s"
              % (options.pack, classes_path, ", ".join(available)))
        return 2
    print("pack: %s" % options.pack)

    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    all_classes = load_json(classes_path)
    classes = player_classes(all_classes)
    skipped = sorted(set(all_classes) - set(classes))
    if skipped:
        print("skipping %d non-PLAYER config(s) in this pack: %s"
              % (len(skipped), ", ".join(skipped)))
    if not classes:
        print("pack %r declares no PLAYER-tagged classes -- nothing to sweep" % options.pack)
        return 2

    starters, unresolved = resolve_starters(classes, load_json(STARTER_MAP))
    if unresolved:
        print("no starter weapon resolvable for: %s -- these will be skipped"
              % ", ".join(sorted(unresolved)))
        for cid in unresolved:
            classes.pop(cid, None)
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
