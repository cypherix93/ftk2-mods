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


def _post(path, payload):
    request = urllib.request.Request(
        BASE + path,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def _get(path):
    with urllib.request.urlopen(BASE + path, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


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
    dump = run("crucible_ui_dump", ["-", "button"])
    return sorted({part.split("'")[1] for part in dump.split("doc='")[1:] if "'" in part})


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


def load_run(run_id):
    """MAIN_MENU -> campaign -> load -> clear gate. Returns a short transcript."""
    steps = []
    snap = snapshot()
    steps.append("start: route=%s" % snap.get("route"))

    if snap.get("route") == "MAIN_MENU":
        for _ in range(8):
            if "campaign-btn" in run("crucible_ui_dump", ["campaign-btn", "button"]):
                break
            time.sleep(3)
        run("crucible_ui_click", ["campaign-btn"])
        steps.append("opened campaign screen")
        time.sleep(5)

    run("crucible_invoke", ["AdventureSelectionDirector", "_loadGameRun", "%s -" % run_id])
    steps.append("requested load %s" % run_id)

    for _ in range(15):
        time.sleep(4)
        if (snapshot().get("run") or {}).get("present"):
            break
    steps.append("after load: %s" % describe())

    steps.append("gate cleared: %s" % clear_gate())
    steps.append("final: %s" % describe())
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
