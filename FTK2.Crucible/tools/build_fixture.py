#!/usr/bin/env python3
r"""
Build a bench fixture through CHARACTER CREATION and register it in its manifest.

    python FTK2.Crucible/tools/build_fixture.py batch-4class \
        --classes CF_ORIG_VAMPIRIC,CF_ORIG_PACIFIST,CF_ORIG_TRAINER,CF_ORIG_GARY

WHAT THIS IS AND IS NOT
-----------------------
This is glue, not a new mechanism. `make-fixture.ps1` already drives a cold boot through the real
character-creation screen to a live run and captures the `.ftk2` -- that recipe was executed
against the live game and is the proven path. This wrapper does the three things around it that
the bench needs and that script does not do:

  1. Runs it through CHARACTER CREATION -- never `crucible_party_set_class`. That verb rewrites
     `ConfigName` but NOT the display name, so the class is right in state and every screenshot
     shows a stale vanilla name. Measured on the existing shared fixture 2026-08-26:

         [0] classId=CF_ORIG_VAMPIRIC name=Thief
         [1] classId=CF_ORIG_PACIFIST name=Shepherd
         [2] classId=CF_ORIG_TRAINER  name=Corsair

     Four vanilla names on a party of our classes. Every frame from that bed is unusable for
     identity, which is exactly what voided an earlier verification run.

  2. **Proves the party on screen before accepting the fixture**, with `crucible_party_list` plus
     a screenshot of the party HUD. `fixture_health` is not this check -- it only inspects file
     SIZE and once certified a content-corrupted save as healthy for a whole session.

  3. Copies the capture to `%USERPROFILE%\Backups\ftk2-fixtures\<backup>` -- OUTSIDE the game's
     own `GameRuns\` -- and flips the manifest's `fixture.status` to `built` with the real run id.
     Outside `GameRuns\` matters: a run that ENDS, win or lose, rewrites and can delete its own
     save, and a driven run autosaves over it mid-session.

The fixture is REJECTED, and the manifest left untouched, if the party gate fails. A bad bed that
gets registered costs a whole run to discover.

--------------------------------------------------------------------------------------------
KNOWN BLOCKER, 2026-08-26 -- READ BEFORE RETRYING
--------------------------------------------------------------------------------------------
Three problems were found and fixed here in one session, in this order, each hiding the next:

  1. `make-fixture.ps1` declared its RPC argument parameter as `$Args`, which is a PowerShell
     AUTOMATIC VARIABLE. `-Args @(...)` never binds to it -- verified in BOTH Windows PowerShell
     5.1 and PowerShell 7 -- so EVERY RPC went out with no arguments and the script died at stage
     0 on a 400 Bad Request. Renamed to `$CmdArgs`.
  2. A modal `PromptUIDocument` (the "Adventuring in the land of Fahrul proves fatal..." warning,
     `ok-btn` = "Understood") sat over the main menu and swallowed the `campaign-btn` click.
     Handled by `ensure_clean_main_menu()`.
  3. STILL OPEN. With 1 and 2 fixed, `campaign-btn` IS clicked successfully -- and the screen it
     opens is `LoadGameUIDocument`, **"Load a Saved Adventure"**, not the new-adventure category
     carousel. So the script's next step, a click on the category label 'Age of Rebellion',
     matches nothing and it throws.

Problem 3 is where this stops, and it stops on purpose. The screen now showing is the operator's
SAVE LIST -- the run this session was reading is right there in it, listed as The Resistance /
Apprentice / Round 19 / Thief, Shepherd, Corsair, Bladedancer. Clicking around on that surface is
exactly what the `continue-btn` / `load-btn` prohibition exists to prevent, and it is not
something to explore by trial and error.

What the next attempt needs is the ELEMENT NAME of the "new adventure" control on this build --
obtained from a `crucible_ui_dump - button` on the main menu, matched by NAME, never by the text
"Continue" and never by trying buttons to see what happens.
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import bench       # noqa: E402
import drive       # noqa: E402
import verify_pin  # noqa: E402  -- restart_game() lives there; not worth a third copy

MAKE_FIXTURE = os.path.join(HERE, "make-fixture.ps1")
REPO_FIXTURES = os.path.abspath(os.path.join(HERE, "..", "data", "Fixtures"))


def ensure_clean_main_menu(attempts=2):
    """Restart the game and leave it on a MAIN_MENU with no modal up.

    make-fixture.ps1 starts by clicking `campaign-btn`, so it assumes MAIN_MENU. Two things
    measured 2026-08-26 break that assumption, and both present as the same unhelpful failure --
    "click on 'campaign-btn' did not report invoked=True ... Visible buttons: (none)" followed by
    a 50-line UI dump:

      1. A modal PromptUIDocument sits over the main menu (the "Adventuring in the land of Fahrul
         proves fatal..." death warning, ok-btn = "Understood"). It swallows the click.
      2. The game is not on MAIN_MENU at all -- a previous attempt left it on
         ADVENTURE_SELECTION, or boot routed itself to the multiplayer lobby.

    Clearing gates on whatever screen happens to be up is NOT enough, and can itself navigate.
    The only reliable reset is a restart, then dismiss, then CONFIRM the route.
    """
    for attempt in range(attempts):
        if not verify_pin.restart_game():
            print("  restart %d failed" % (attempt + 1))
            continue
        for _ in range(10):
            gates = drive.clear_gates(max_rounds=4)
            snap = drive.snapshot()
            docs = drive.visible_docs()
            if snap.get("route") == "MAIN_MENU" and "PromptUIDocument" not in docs:
                print("  clean MAIN_MENU (gates: %s, docs: %s)" % (gates, ", ".join(docs)))
                return True
            time.sleep(4)
        print("  after restart %d the game settled on route=%s docs=%s"
              % (attempt + 1, drive.snapshot().get("route"), ", ".join(drive.visible_docs())))
    return False


def run_make_fixture(name, class_ids, timeout_seconds=1800):
    # -Command with an explicit @(...) array, NOT -File.
    #
    # `-File script.ps1 -PartyClassIds a,b,c,d` hands the comma string through as ONE argument,
    # and the [ValidateCount(4,4)] on that parameter rejects it with "the number of provided
    # arguments (1) is fewer than the minimum (4)". Under -File, arguments are plain strings and
    # no array binding happens. -Command parses PowerShell syntax, so the array literal binds.
    script = "& '%s' -PartyClassIds @(%s) -FixtureName '%s'" % (
        MAKE_FIXTURE, ",".join("'%s'" % c for c in class_ids), name)
    cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script]
    print("  $ %s" % " ".join(cmd))
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout_seconds)
    sys.stdout.write(proc.stdout or "")
    sys.stderr.write(proc.stderr or "")
    return proc.returncode == 0


def find_capture(name):
    d = os.path.join(REPO_FIXTURES, name)
    if not os.path.isdir(d):
        return None, None
    saves = [f for f in os.listdir(d) if f.endswith(".ftk2")]
    if not saves:
        return None, None
    return os.path.join(d, saves[0]), os.path.splitext(saves[0])[0]


def prove_party(expect_classes, forbid_names, label):
    """crucible_party_list + a party-HUD screenshot. BOTH, because they fail in opposite ways."""
    text = drive.run("crucible_party_list")
    print(text)
    low = text.lower()
    missing = [c for c in expect_classes if c.lower() not in low]
    vanilla = [v for v in forbid_names if v.lower() in low]

    shot = None
    try:
        import evidence
        img = evidence.grab(("party", "full"))
        shot, _size = evidence.save(img, "fixture_%s" % label, "party")
        print("  party HUD crop: %s" % shot)
        if evidence.is_blank(evidence.crop_for(img, "party")):
            print("  <-- BLANK, NOT EVIDENCE (mid-transition); re-capture before trusting it)")
    except Exception as exc:                                  # noqa: BLE001
        print("  (party screenshot failed: %s)" % exc)

    if vanilla:
        print("\n  REJECTED. Vanilla class name(s) on the party: %s" % ", ".join(vanilla))
        print("  That is the crucible_party_set_class signature -- ConfigName rewritten, display")
        print("  name left stale. Every screenshot from this bed would be worthless for identity.")
        return False, shot
    if missing:
        print("\n  REJECTED. Expected class name(s) absent from the party: %s" % ", ".join(missing))
        return False, shot
    print("\n  party gate PASSED: %s all present, no vanilla names." % ", ".join(expect_classes))
    return True, shot


def register(manifest_path, run_id, backup_name, class_ids, shot):
    with open(manifest_path, "r", encoding="utf-8") as fh:
        m = json.load(fh)
    m["fixture"]["run_id"] = run_id
    m["fixture"]["status"] = "built"
    m["fixture"]["built"] = {
        "when": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "how": "make-fixture.ps1 through CHARACTER CREATION (never crucible_party_set_class)",
        "class_ids": list(class_ids),
        "party_proof_screenshot": shot,
    }
    bench.validate(m, manifest_path)
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(m, fh, indent=2)
        fh.write("\n")
    print("  registered in %s" % manifest_path)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("scenario", help="manifest name under tools/scenarios/")
    p.add_argument("--classes", required=True,
                   help="exactly four class ids, comma-separated, in slot order")
    p.add_argument("--no-restart", action="store_true",
                   help="do not restart first; only safe when the game is already on a clean "
                        "MAIN_MENU with no modal up")
    p.add_argument("--skip-build", action="store_true",
                   help="a build already ran; just prove the party and register the capture")
    args = p.parse_args()

    class_ids = [c.strip() for c in args.classes.split(",") if c.strip()]
    if len(class_ids) != 4:
        print("make-fixture.ps1 takes exactly four class ids (got %d). For an isolation bed, put "
              "the subject in slot 1 and fill the rest -- extra party members do not invalidate "
              "an identity tell that names the subject." % len(class_ids))
        return 2

    manifest_path = os.path.join(bench.SCENARIO_DIR, args.scenario + ".json")
    with open(manifest_path, "r", encoding="utf-8") as fh:
        m = json.load(fh)
    bench.validate(m, manifest_path)
    expect = m["party"]["expect_classes"]
    forbid = m["party"].get("forbid_names", bench.VANILLA_NAMES)
    backup_name = m["fixture"]["backup"]

    print("building fixture for %s" % args.scenario)
    print("  classes : %s" % ", ".join(class_ids))
    print("  expect  : %s" % ", ".join(expect))
    print("  backup  : %s" % os.path.join(drive.FIXTURE_BACKUPS, backup_name))

    if not args.skip_build:
        if not args.no_restart and not ensure_clean_main_menu():
            print("\nCould not reach a clean MAIN_MENU. Nothing was registered.")
            return 2
        if not run_make_fixture(args.scenario, class_ids):
            print("\nmake-fixture.ps1 failed. Nothing was registered.")
            return 1

    if not drive.boot(timeout_seconds=120):
        print("the game is not reachable; cannot prove the party")
        return 2

    ok, shot = prove_party(expect, forbid, args.scenario)
    if not ok:
        print("\nFixture NOT registered. The manifest still says status=not-built, which is the "
              "honest state.")
        return 1

    capture, run_id = find_capture(args.scenario)
    if not capture:
        print("\nmake-fixture.ps1 reported success but produced no .ftk2 under %s"
              % os.path.join(REPO_FIXTURES, args.scenario))
        return 1

    os.makedirs(drive.FIXTURE_BACKUPS, exist_ok=True)
    dest = os.path.join(drive.FIXTURE_BACKUPS, backup_name)
    shutil.copy2(capture, dest)
    print("\n  captured %s -> %s (%d bytes)" % (run_id, dest, os.path.getsize(dest)))

    register(manifest_path, run_id, backup_name, class_ids, shot)
    print("\nDONE. `python bench.py run %s` will now stage this bed." % args.scenario)
    return 0


if __name__ == "__main__":
    sys.exit(main())
