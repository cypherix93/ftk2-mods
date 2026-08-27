#!/usr/bin/env python3
r"""
Prove that `crucible_pin_seed` actually changes DRAWS -- not merely a label.

WHY THIS IS A SEPARATE, PARANOID SCRIPT
---------------------------------------
`GameRandom` decompiles to:

    public readonly int Seed;                 // a RECORD of the seed
    private readonly System.Random random;    // the ACTUAL generator, built once from Seed

`Seed` is readonly and is never read again after the constructor. The original `crucible_pin_seed`
wrote **only** `Seed`, so it changed a label and affected **zero** future draws. Every "pinned
seed" test built on it was reproducible in name only -- the worst kind of broken instrument,
because it reports success while doing nothing. The fix replaces the private `random` field.

So this script does not trust the plugin's own success string. It measures.

TWO HALVES, AND NEITHER ALONE IS SUFFICIENT
-------------------------------------------
  A. same seed, twice -> SAME outcome.
     Alone this proves nothing: a driven sequence that consumes no randomness is trivially
     "reproducible", which is exactly how the broken implementation looked fine.
  B. a DIFFERENT seed -> DIFFERENT outcome.
     This is the half that catches the readonly-Seed bug. Without it, A is unfalsifiable.

Each pass RELOADS A PRISTINE COPY of the fixture first, so the two passes start from byte-identical
state and the only variable is the seed. (A driven run autosaves over its own save mid-session, so
reloading from the copy is not optional here -- pass 2 would otherwise start from pass 1's ending.)

    python FTK2.Crucible/tools/verify_pin.py --run-id <guid> --seed 20260826 --steps 5

Exit codes: 0 = pinning verified, 1 = NOT verified, 2 = inconclusive (say UNVERIFIED, not "works").
"""

import argparse
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

# Observables read back after the driven sequence. All come from AdventureState.MapState, never
# from the [Obsolete] GameRunData aliases -- those are never written, so asserting on them would
# have compared dead state against itself.
OBSERVABLES = ("weather", "timeOfDay", "timeOfDayIndex", "roundCount", "gameStage",
               "encounters")


GUID_RE = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-"
                     r"[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


def read_observables():
    text = drive.run("crucible_overworld_state")
    # The obsolete block is printed under its own banner; everything after it is not assertable.
    head = text.split("[obsolete")[0]
    out = {}
    for key in OBSERVABLES:
        m = re.search(re.escape(key) + r"\s*=\s*(\S+)", head)
        out[key] = m.group(1) if m else None
    out["encounters"] = read_encounters()
    return out, text


def read_encounters():
    """A fingerprint of where the roaming encounters are.

    This is the observable that actually earns the verification. Weather turned out to sit on RAIN
    across seven rounds (measured 2026-08-26), and a constant trace makes the "a different seed
    diverges" half unfalsifiable -- which is precisely the hole the broken pin_seed hid in.
    Roaming encounters move every round off the shared stream, so their positions are a dense,
    genuinely seed-sensitive signal.

    ONLY THE HEX COORDINATES ARE KEPT, and this is load-bearing.

    `crucible_map_encounters` prints `[x,y] <configName> guid=<guid>`, but most roaming encounters
    have an EMPTY configName, so a naive "second whitespace token" parse picks up the GUID
    instead. `Entity.Guid` is `Guid.NewGuid()` per entity per load, so those tokens are different
    on every single pass -- including the two passes that share a seed. Feeding them into the
    fingerprint would have made A != B *and* A != C, and this script would have printed a
    confident, completely false "PIN VERIFIED". Caught by reading the actual trace, 2026-08-26.

    So: coordinates always, and a name only when it is demonstrably not a GUID. The set is SORTED
    because enumeration order is not part of what is being claimed.
    """
    ok, text, _why = drive.run_soft("crucible_map_encounters", ["-"])
    # A crucible_* handler reports its own refusals INSIDE the result string ("error: no run
    # loaded") while the transport still says ok=true, so the ok flag alone is not the test.
    # Returning () here instead of None would quietly make every pass look identical.
    if not ok or not text or text.startswith("error:"):
        return None
    out = []
    for x, y, token in re.findall(r"\[(\d+),\s*(\d+)\]\s+(\S+)", text):
        name = "" if (GUID_RE.match(token) or token.startswith("guid=")) else token
        out.append("%s,%s@%s" % (x, y, name))
    return tuple(sorted(out))


GAME_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\For The King II"
GAME_EXE = os.path.join(GAME_DIR, "For The King II.exe")


def game_process_count():
    out = subprocess.run([
        "powershell", "-NoProfile", "-Command",
        "(Get-Process | Where-Object { $_.Path -like '*For The King II*' } | "
        "Measure-Object).Count",
    ], capture_output=True, text=True)
    try:
        return int((out.stdout or "0").strip().splitlines()[-1])
    except (ValueError, IndexError):
        return 0


def restart_game(timeout_seconds=300):
    """PID-scoped kill, relaunch, then wait for the PUMP and for the UI to exist.

    Why a restart between passes at all: `drive.load_run` REFUSES to load over a live run, and it
    is right to. Forcing `RouterMono.Route(MAIN_MENU)` does not tear down the previous screen's
    UIDocuments, so the main menu ends up rendered on top of a still-live adventure screen and
    `_loadGameRun` silently does nothing against that mixed state -- the file resolves, no error
    is logged, and the load simply never happens. A restart is the only reliable reset, and each
    pass has to start from byte-identical state or the seed is not the only variable.

    Why NOT `steam://rungameid/1676840`, which is what `drive.restart_game` uses: measured
    2026-08-26, that URL did not start the game on this machine at all. Steam was running and
    logged in, the call returned cleanly, and no process ever appeared -- twice, with a 45s wait.
    Launching the exe directly does start it, and Steam still attaches.

    But the direct launch is not reliable either -- it also silently produced no process on some
    attempts, always straight after a Stop-Process. Neither launcher is trustworthy fired and
    forgotten, which is why the loop below CONFIRMS the process appeared and re-issues if not.
    That confirmation, not the choice of launcher, is what made this stop failing.

    The kill is PID-scoped on purpose. A blanket kill on the process name would also take out
    unrelated processes, and this machine is running the agent that drives the test.
    """
    subprocess.run([
        "powershell", "-NoProfile", "-Command",
        "Get-Process | Where-Object { $_.Path -like '*For The King II*' } "
        "| ForEach-Object { Stop-Process -Id $_.Id -Force }",
    ], capture_output=True)
    time.sleep(10)

    # THE LAUNCH IS CONFIRMED, NOT ASSUMED, AND RETRIED.
    #
    # Measured 2026-08-26: `Start-Process` returns success and the game sometimes never appears --
    # observed on 2 of 3 relaunches in one session, always straight after a Stop-Process. Fired
    # and forgotten, that turns into a 5-minute wait inside drive.boot() and then a "the game did
    # not come back up" that looks like a game problem rather than a launcher one. So: poll for
    # the PROCESS first, and re-issue the launch if it did not take.
    launched = False
    for attempt in range(4):
        subprocess.run([
            "powershell", "-NoProfile", "-Command",
            "Start-Process -FilePath '%s' -WorkingDirectory '%s'" % (GAME_EXE, GAME_DIR),
        ], capture_output=True)
        for _ in range(20):
            time.sleep(3)
            if game_process_count() > 0:
                launched = True
                break
        if launched:
            break
        print("  launch attempt %d produced no process; re-issuing" % (attempt + 1))
    if not launched:
        print("  the game executable would not start after 4 attempts")
        return False

    if not drive.boot(timeout_seconds=timeout_seconds):
        return False
    drive.run_soft("crucible_input_focus_gate", ["on"])
    drive.run_soft("crucible_tutorials_suppress", ["on"])
    # The pump answers while the game is still on the splash screen, where a UI dump returns no
    # documents at all and every navigation click lands on nothing.
    for _ in range(40):
        if drive.visible_docs():
            return True
        time.sleep(3)
    return False


def one_pass(run_id, seed, steps, label, restart=True):
    print("\n--- pass %s: restart, reload a pristine copy, pin %d, advance %d ---"
          % (label, seed, steps))
    if restart and not restart_game():
        print("  the game did not come back up")
        return None
    restored, detail = drive.restore_fixture(run_id)
    if not restored:
        print("  cannot restore the pristine copy: %s" % detail)
        return None
    print("  fixture: %s" % detail)

    load_text = drive.load_run(run_id)
    if "FAILED" in load_text:
        print("  load failed:\n%s" % load_text)
        return None

    # The pin must be RETRIED until a live GameRandom is actually reachable.
    #
    # Measured 2026-08-26: called immediately after load_run returns, pin_seed reports
    # `scopesFound=0 ... note=no live GameRandom instance reachable`. The run IS present by then --
    # load_run waits on run.present -- but AdventureDirector is not yet resolvable, so the pin
    # writes nothing and reports ok. That is a silent no-op of exactly the kind this whole script
    # exists to catch, and it would have produced two identical traces and a confident, wrong
    # "PIN VERIFIED".
    pin = ""
    for attempt in range(20):
        pin = drive.run("crucible_pin_seed", [str(seed)])
        if "scopesFound=0" not in pin:
            break
        if attempt == 0:
            print("  waiting for a live GameRandom to become reachable "
                  "(scopesFound=0 right after load is normal)")
        time.sleep(3)
    print("  pin: %s" % pin.strip())

    if "scopesFound=0" in pin:
        print("  !! no live GameRandom was ever reachable. The pin wrote NOTHING, so this pass "
              "measures an unpinned run. Aborting rather than comparing meaningless traces.")
        return None

    generator_replaced = "generator=True" in pin
    if not generator_replaced:
        print("  !! the plugin did NOT report a replaced generator. This is the pre-fix build, "
              "which writes the readonly Seed label and changes zero draws.")

    # WHAT IS BEING MEASURED, and why it is weather.
    #
    # `crucible_time_advance 1` calls AdventureDirector._doEndTurn() ONCE, which ends ONE
    # character's turn -- not one round. With a four-character party the round counter only moves
    # every ~4-6 calls (measured: 6 calls advanced roundCount 19 -> 20). Weather is re-rolled from
    # the shared stream on the round boundary, so the number of steps has to be large enough to
    # cross several boundaries or the trace is constant for reasons that have nothing to do with
    # the seed -- and a constant trace makes the "different seed diverges" half unfalsifiable.
    trace = []
    for i in range(steps):
        drive.run("crucible_time_advance", ["1"])
        time.sleep(1.5)          # _doEndTurn returns a Task and is NOT awaited
        obs, _raw = read_observables()
        trace.append(tuple(obs[k] for k in OBSERVABLES))
        print("    step %2d  %s" % (i + 1, dict(zip(OBSERVABLES, trace[-1]))))

    distinct = len(set(trace))
    if distinct < 3:
        print("  !! only %d distinct observation(s) across %d steps -- this trace is nearly "
              "constant and cannot distinguish two seeds." % (distinct, steps))
    rounds = len(set(t[OBSERVABLES.index("roundCount")] for t in trace))
    if rounds < 3:
        print("  !! only %d distinct roundCount value(s) across %d steps. Too few round "
              "boundaries were crossed for weather to have been re-rolled more than once, so "
              "this trace cannot distinguish two seeds. Raise --steps." % (rounds, steps))
    return {"pin": pin, "generator_replaced": generator_replaced, "trace": trace,
            "rounds_crossed": rounds}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--run-id", required=True)
    p.add_argument("--seed", type=int, default=20260826)
    p.add_argument("--other-seed", type=int, default=None)
    p.add_argument("--steps", type=int, default=24)
    args = p.parse_args()
    other = args.other_seed if args.other_seed is not None else args.seed + 7919

    if not drive.boot(timeout_seconds=120):
        print("the game is not up")
        return 2
    drive.run_soft("crucible_input_focus_gate", ["on"])
    drive.run_soft("crucible_tutorials_suppress", ["on"])

    a = one_pass(args.run_id, args.seed, args.steps, "A (seed %d)" % args.seed)
    if a is None:
        return 2
    b = one_pass(args.run_id, args.seed, args.steps, "B (seed %d, same)" % args.seed)
    if b is None:
        return 2
    c = one_pass(args.run_id, other, args.steps, "C (seed %d, different)" % other)
    if c is None:
        return 2

    print("\n" + "=" * 84)
    print("PIN SEED VERIFICATION")
    print("=" * 84)
    print("plugin reported a replaced generator on all three passes: %s"
          % (a["generator_replaced"] and b["generator_replaced"] and c["generator_replaced"]))
    for tag, r, sd in (("A", a, args.seed), ("B", b, args.seed), ("C", c, other)):
        print("  %s (seed %-9d) rounds crossed=%d distinct observations=%d"
              % (tag, sd, r["rounds_crossed"], len(set(r["trace"]))))
        for i, row in enumerate(r["trace"]):
            d = dict(zip(OBSERVABLES, row))
            enc = d.pop("encounters")
            print("       %2d %s" % (i + 1, d))
            print("          encounters=%s" % (list(enc) if enc else enc))

    same = a["trace"] == b["trace"]
    differ = a["trace"] != c["trace"]
    print()
    print("  A == B (same seed reproduces)      : %s" % ("YES" if same else "NO"))
    print("  A != C (a different seed diverges) : %s" % ("YES" if differ else "NO"))

    if same and differ:
        print("\nPIN VERIFIED. The seed genuinely drives the outcome, so a pinned scenario is "
              "reproducible in substance and not only in name.")
        return 0
    if same and not differ:
        print("\nINCONCLUSIVE -- and lean UNVERIFIED. Both seeds produced the same trace, which "
              "means either the pin still changes nothing (the readonly-Seed bug) OR this driven "
              "sequence consumes no randomness and cannot tell the two apart. Do NOT report "
              "pinning as working on this evidence; find an observable that is genuinely "
              "RNG-driven and re-run.")
        return 2
    print("\nPIN NOT VERIFIED. The same seed did not reproduce. Nothing seeded should be "
          "trusted until this passes.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
