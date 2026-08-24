#!/usr/bin/env python3
"""
Drive the running game over Crucible's RPC.

Encodes the sequences that are easy to get subtly wrong by hand:

  boot       wait until the plugin's main-thread pump is alive
  load       MAIN_MENU -> campaign -> load a run by id -> clear the post-load gate
  gate       clear a "Click to Continue" gate wherever one is showing
  state      print route / combat / party in one line
  exec       run any crucible_* command

Two facts this exists to encode, both measured 2026-08-23:
  * `_loadGameRun` needs ADVENTURE_SELECTION to exist. Called from MAIN_MENU it silently
    does nothing, so the campaign screen must be opened first.
  * A post-load `Click to Continue` gate (continue-label in LoadingUIDocument) blocks EVERY
    overworld interaction while it is up. Twenty-five consecutive end-turn calls reported
    "changed=False" purely because of it.

Usage:
    python FTK2.Crucible/tools/drive.py state
    python FTK2.Crucible/tools/drive.py load <runId>
    python FTK2.Crucible/tools/drive.py exec crucible_party_list
"""

import json
import subprocess
import sys
import time
import urllib.error
import urllib.request

PORT = 8787
BASE = "http://127.0.0.1:%d" % PORT


def _post(path, payload, attempts=3):
    """
    Retries transient failures. The pump answers 503 while it is busy and 400 during the
    window where a command is not yet registered, and both are normal during boot -- a single
    attempt turns an ordinary race into a spurious hard failure mid-sequence.
    """
    last = None
    for attempt in range(attempts):
        request = urllib.request.Request(
            BASE + path,
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            last = error
            time.sleep(2 + 2 * attempt)
        except (urllib.error.URLError, OSError) as error:
            last = error
            time.sleep(2 + 2 * attempt)
    raise RuntimeError("POST %s failed after %d attempts: %s" % (path, attempts, last))


def _get(path, attempts=3):
    last = None
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(BASE + path, timeout=30) as response:
                return json.loads(response.read().decode("utf-8"))
        except (urllib.error.HTTPError, urllib.error.URLError, OSError) as error:
            last = error
            time.sleep(2 + 2 * attempt)
    raise RuntimeError("GET %s failed after %d attempts: %s" % (path, attempts, last))


def run(command, args=None):
    """One console command. Returns its result string ('' when the command produced none)."""
    payload = _post("/exec", {"command": command, "args": args or []})
    return payload.get("result") or ""


def boot(timeout_seconds=180):
    """Waits for the pump, not merely for the port: the RPC answers before the game can act."""
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        try:
            health = _get("/health", attempts=1)
            if health.get("pumpAlive"):
                return True
        except (urllib.error.URLError, OSError, RuntimeError):
            # RuntimeError too: _get now raises it after exhausting retries, and during boot a
            # refused connection is the normal case rather than a failure.
            pass
        time.sleep(5)
    return False


def snapshot():
    payload = _get("/state?schema=v2")
    return payload.get("snapshot", {})


def describe():
    snap = snapshot()
    combat = snap.get("combat", {})
    combatants = combat.get("combatants")
    # null and [] are different findings: null means unreadable and always comes with a warning.
    if combatants is None:
        who = "combatants=None(unreadable)"
    else:
        who = "combatants=%d" % len(combatants)
    return "route=%s run=%s combat.active=%s %s round=%s turn=%s" % (
        snap.get("route"),
        (snap.get("run") or {}).get("present"),
        combat.get("active"),
        who,
        combat.get("round"),
        combat.get("turn"),
    )


def visible_docs():
    """
    Names of the UIDocuments with something visible on screen. Screen identity must come from
    the UI tree: route has reported MAIN_MENU while a multiplayer browser and a modal were
    actually on screen.
    """
    dump = run("crucible_ui_dump", ["-", "button"])
    found = set()
    for part in dump.split("doc='")[1:]:
        name = part.split("'")[0]
        if name and name.endswith("UIDocument"):
            found.add(name)
    return sorted(found)


# Elements that block progress and are safe to dismiss, in priority order.
#
# Every entry is an EXACT element name, never a text match. "Continue" as text appears on
# several unrelated controls including continue-btn, which resumes the owner's live co-op
# campaign - so text matching here would eventually load somebody's real save.
#
# ok-btn / royal-tutor-container were found blocking adventure selection: a tutorial modal
# ("Understood") plus the Royal Tutor overlay sit on top of the screen and silently prevent a
# fixture load, which presents as "the load did not take".
DISMISSABLE = [
    ("continue-label", "post-load gate and each intro story page"),
    # Quest resolution AWAITS the dialogue it opens. _resolveQuests is awaited by
    # _tryCompleteQuests BEFORE the AdventureEndTrigger==WIN check, so an undismissed dialogue
    # does not merely sit there -- it stops the run from ever ending. Measured 2026-08-24: the
    # WIN quest completed, and _endAdventure never fired, because this was on screen.
    ("dialogue-container", "story dialogue page; blocks _resolveQuests"),
    ("ok-btn", "tutorial/system prompt (Understood)"),
    ("royal-tutor-container", "Royal Tutor tutorial overlay"),
    ("sys-dialog-ok-btn", "system dialog confirm"),
    ("online-quit-btn", "boot-time online error modal"),
]


def visible_names(names):
    """Which of the given element names are currently on screen."""
    dump = run("crucible_ui_dump", ["-", "button"])
    return [name for name in names if ("name='%s'" % name) in dump]


def clear_gates(max_rounds=12):
    """
    Dismisses every known blocking element until none remain.

    Loops rather than pressing once: the intro is several pages, and dismissing one modal
    frequently reveals another behind it.
    """
    pressed = []
    for _ in range(max_rounds):
        present = visible_names([name for name, _reason in DISMISSABLE])
        if not present:
            return {"ok": True, "pressed": pressed}

        for name in present:
            # Focus + Enter is the path that actually retires these. It looks weaker than
            # crucible_ui_click (which reports whether the element ACTED) but it is what works:
            # continue-label ignores UIToolkitHelper.Submit entirely. Where Submit IS needed -- the
            # story dialogue and the summary's next-btn -- callers use crucible_ui_click directly.
            run("crucible_ui_focus", [name])
            run("crucible_key", ["enter"])
            pressed.append(name)
            time.sleep(1.5)

    remaining = visible_names([name for name, _reason in DISMISSABLE])
    return {"ok": not remaining, "pressed": pressed, "remaining": remaining}


def pick_reward(max_prompts=8):
    """
    Answers queued "Choose Reward" prompts by taking the first option.

    Deliberately NOT part of clear_gates. choice-menu-btn is present in AdventureUIDocument's tree
    even when no prompt is on screen, so a blanket dismiss loop clicks it forever and reports
    "remaining" every time -- which is exactly what happened when it was in DISMISSABLE. The panel
    title is the reliable signal, so this looks for that first and does nothing when it is absent.

    Answering matters: quest resolution AWAITS these, they QUEUE one per completed quest, and a
    stack of unanswered prompts presents as a hang with a healthy game thread and a fully rendered
    overworld.
    """
    answered = []
    for _ in range(max_prompts):
        dump = run("crucible_ui_dump", ["-", "button,label"])
        if "Choose Reward" not in dump:
            break
        run("crucible_ui_click", ["choice-menu-btn"])
        answered.append("choice-menu-btn")
        time.sleep(2)
        run("crucible_ui_click", ["dialogue-container"])
        time.sleep(2)
    return {"answered": len(answered), "rewardPromptStillUp": "Choose Reward" in run("crucible_ui_dump", ["-", "button,label"])}


def interaction_enabled():
    """
    The AUTHORITATIVE readiness signal: AdventureDirector._enableInteraction has run.

    Screen-shape checks are not enough on their own. LoadingUIDocument lingers as an orphaned
    overlay after a load and can report present when the game is perfectly playable, and it can be
    absent while a reward prompt still has interaction switched off. This field is what the game
    itself consults.
    """
    for line in run("crucible_overworld_state").splitlines():
        if line.startswith("interactionEnabled="):
            return line.split()[0] == "interactionEnabled=True"
    return False


def wait_until(predicate, timeout_seconds=60, interval=0.4):
    """Poll `predicate` until it returns truthy, or the deadline passes; returns its value or None.

    Replaces the fixed `time.sleep(n)` calls this harness used to pace itself with. Those were
    padding chosen to be safely LONGER than the slowest observed transition, which meant every run
    paid the worst case every time even when the game was ready immediately. Polling pays the
    ACTUAL cost instead, and a whole boot-to-combat cycle is dominated by these waits.
    """
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    return None


def _settle(timeout_seconds=8):
    """Wait for interaction to come back after dismissing a gate, rather than a flat sleep."""
    return wait_until(interaction_enabled, timeout_seconds=timeout_seconds, interval=0.3)


def wait_ready(timeout_seconds=180, verbose=False):
    """
    Drives the game to a genuinely interactive overworld, clearing whatever is in the way.

    Call this after EVERY transition -- load, spawn, combat exit -- not just after a load. The
    blockers reappear: a "Click to Continue" gate comes back after a debug spawn, the intro is
    several pages, quest resolution queues one reward prompt per completed quest, and each of those
    silently holds interaction off while the overworld looks completely normal in a screenshot.

    Returns a dict describing what it cleared and whether the game is ready, rather than raising,
    so a caller can report the real reason instead of failing later somewhere unrelated.
    """
    cleared = []
    deadline = time.time() + timeout_seconds

    while time.time() < deadline:
        if interaction_enabled():
            return {"ready": True, "cleared": cleared, "docs": visible_docs()}

        dump = run("crucible_ui_dump", ["-", "button,label"])

        if "name='continue-label'" in dump:
            # FOCUS + ENTER, not crucible_ui_click. UIToolkitHelper.Submit returns true on this
            # element and advances nothing -- measured 2026-08-24 at 48 consecutive "successful"
            # presses with the gate still up. The keyboard path clears the same gate in two.
            run("crucible_ui_focus", ["continue-label"])
            run("crucible_key", ["enter"])
            cleared.append("continue-label")
            _settle()
            continue

        if "Choose Reward" in dump:
            run("crucible_ui_click", ["choice-menu-btn"])
            cleared.append("reward")
            _settle()
            continue

        if "name='dialogue-container'" in dump:
            run("crucible_ui_click", ["dialogue-container"])
            cleared.append("dialogue")
            _settle()
            continue

        if "name='ok-btn'" in dump:
            run("crucible_ui_click", ["ok-btn"])
            cleared.append("ok-btn")
            _settle()
            continue

        # Nothing recognised is in the way but interaction is still off -- the game is mid
        # transition. Wait rather than hammering it.
        if verbose:
            print("  waiting: docs=%s" % ", ".join(visible_docs()))
        time.sleep(0.4)

    return {
        "ready": False,
        "cleared": cleared,
        "docs": visible_docs(),
        "reason": "interactionEnabled never became true within %ds" % timeout_seconds,
    }


def restart_game(timeout_seconds=240):
    """
    Kills the game by PID and relaunches it through Steam.

    The only reliable exit from the stale-overlay state: once LoadingUIDocument is orphaned, its
    continue-label has no handler wired, so pressing it succeeds and changes nothing -- measured
    2026-08-24 at 48 consecutive presses with interaction still disabled. Nothing short of a
    restart clears it.

    PID-scoped on purpose. A blanket kill on the process name would also take out unrelated
    processes, and this machine runs the agent that is driving the test.
    """
    subprocess.run([
        "powershell", "-NoProfile", "-Command",
        "Get-Process | Where-Object { $_.Path -like '*For The King II\\For The King*' } "
        "| ForEach-Object { Stop-Process -Id $_.Id -Force }; Start-Sleep -Seconds 6; "
        "Start-Process 'steam://rungameid/1676840'",
    ], capture_output=True)
    return boot(timeout_seconds=timeout_seconds)


def load_run(run_id, attempts=3):
    """
    MAIN_MENU -> campaign -> load -> clear gate, verified at each step.

    Each step is CONFIRMED rather than assumed. Boot is a race (the game reaches MAIN_MENU and
    can route itself to MULTIPLAYER_LOBBY about two seconds later), the campaign click can land
    before the menu is interactive, and _loadGameRun silently does nothing when the adventure
    selection screen is not up. An unverified sequence fails several steps later, somewhere
    that looks unrelated.
    """
    steps = []

    # Wait for the UI to exist, not merely for the RPC pump. The pump answers while the game is
    # still on the splash screen, where a dump returns no documents at all and every navigation
    # click lands on nothing -- which reads as "did not reach adventure selection".
    for _ in range(30):
        if visible_docs():
            break
        time.sleep(3)
    else:
        return "FAILED: no UI documents appeared; the game never finished loading"

    for attempt in range(attempts):
        snap = snapshot()
        steps.append("attempt %d: route=%s" % (attempt + 1, snap.get("route")))

        # A CLEAN main menu is required. Forcing RouterMono.Route(MAIN_MENU) does change the
        # route enum but does NOT tear down the previous screen's UIDocuments, so the main menu
        # ends up rendered on top of a still-live adventure selection (or venue) screen, and
        # _loadGameRun silently does nothing against that mixed state -- the file resolves, no
        # error is logged, and the load simply never happens. A restart is the only reliable
        # reset, so refuse to proceed from a dirty screen rather than fail three times over.
        docs_now = visible_docs()
        dirty = [d for d in docs_now if d not in ("MainMenuUIDocument", "PromptUIDocument", "TutorialUIDocument")]
        if dirty:
            steps.append("  screen is dirty (%s); a restart is required" % ", ".join(dirty))
            return "\n".join(steps) + "\nFAILED: dirty screen, restart the game first"

        # Escape whatever boot left on screen, including the multiplayer lobby it routes itself to.
        for _ in range(6):
            # Dismiss modals EVERY iteration, not only after loading. A tutorial prompt or the
            # Royal Tutor overlay sits on top of adventure selection and swallows the clicks that
            # would get us there, so clearing only afterwards never runs at all.
            clear_gates(max_rounds=4)
            docs = visible_docs()
            if "AdventureSelectionUIDocument" in docs:
                break
            if "MainMenuUIDocument" in docs:
                run("crucible_ui_click", ["campaign-btn"])
                time.sleep(5)
                continue
            if "MultiplayerUIDocument" in docs or "OnlineUIDocument" in docs:
                run("crucible_ui_click", ["online-quit-btn"])
                run("crucible_ui_click", ["back-btn"])
                time.sleep(4)
                continue
            time.sleep(4)

        docs = visible_docs()
        if "AdventureSelectionUIDocument" not in docs:
            steps.append("  did not reach adventure selection (docs=%s)" % ", ".join(docs))
            continue
        steps.append("  adventure selection is up")

        # Env.GameRuns is cached at startup, so a file copied in while the game runs is invisible
        # and _loadGameRun silently does nothing. Refresh first.
        run("crucible_refresh_saves", [])
        run("crucible_invoke", ["AdventureSelectionDirector", "_loadGameRun", "%s -" % run_id])

        loaded = False
        for _ in range(15):
            time.sleep(4)
            if (snapshot().get("run") or {}).get("present"):
                loaded = True
                break
        if not loaded:
            steps.append("  load did not take")
            continue

        steps.append("  loaded: %s" % describe())
        steps.append("  gates: %s" % clear_gates())
        steps.append("  final: %s" % describe())
        return "\n".join(steps)

    steps.append("FAILED to load %s after %d attempts" % (run_id, attempts))
    return "\n".join(steps)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    action = sys.argv[1]

    if action == "boot":
        print("pump alive" if boot() else "TIMED OUT waiting for pump")
        return 0
    if action == "state":
        print(describe())
        print("docs: %s" % ", ".join(visible_docs()))
        return 0
    if action == "gate":
        print(clear_gates())
        return 0
    if action == "load":
        if len(sys.argv) < 3:
            print("usage: drive.py load <runId>")
            return 2
        print(load_run(sys.argv[2]))
        return 0
    if action == "exec":
        if len(sys.argv) < 3:
            print("usage: drive.py exec <command> [args...]")
            return 2
        print(run(sys.argv[2], sys.argv[3:]))
        return 0

    print("unknown action %r" % action)
    return 2


if __name__ == "__main__":
    sys.exit(main())
