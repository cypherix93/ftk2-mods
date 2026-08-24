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
            health = _get("/health")
            if health.get("pumpAlive"):
                return True
        except (urllib.error.URLError, OSError):
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


def clear_gate(max_presses=6):
    """
    Clears a 'Click to Continue' gate. Focus is set explicitly first: a key press acts only on a
    focused target, and focus does not persist between commands.
    """
    for _ in range(max_presses):
        dump = run("crucible_ui_dump", ["continue-label", "button"])
        if "continue-label" not in dump:
            return True
        run("crucible_ui_focus", ["continue-label"])
        run("crucible_key", ["enter"])
        time.sleep(2)
    return "continue-label" not in run("crucible_ui_dump", ["continue-label", "button"])


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

    for attempt in range(attempts):
        snap = snapshot()
        steps.append("attempt %d: route=%s" % (attempt + 1, snap.get("route")))

        # Escape whatever boot left on screen, including the multiplayer lobby it routes itself to.
        for _ in range(6):
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
        steps.append("  gate cleared: %s" % clear_gate())
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
        print("cleared: %s" % clear_gate())
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
