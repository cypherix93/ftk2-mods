#!/usr/bin/env python3
"""Exercise the never-yet-run harness verbs.

Every crucible_* verb below has been RESEARCHED (registered, documented) but never actually
called against a live game. This module closes that gap the same way battery.py does for the
combat classes: call each verb, then assert on state READ BACK independently -- never on the
command's own success string, because several verbs in this harness have reported success while
invoking nothing (see battery.py's docstring and ChaosCommands.cs's own history notes).

Verbs exercised, one section each:
  crucible_pin_seed        -- DebugVerbCommands.cs:62
  crucible_quest_state      -- DebugVerbCommands.cs:63
  crucible_run_status        -- RunCommands.cs:70
  crucible_chaos_state        -- ChaosCommands.cs:129
  crucible_chaos_advance        -- ChaosCommands.cs:130 (REFUSAL PATH ONLY, see SAFETY below)
  GET /trace (crucible_read_trace) -- RpcServer.cs, HandleTrace
  GET /health (crucible_list_instances / health) -- RpcServer.cs, HandleHealth
  wait-for-screen (crucible_wait_screen)  -- NOT a registered plugin command. The MCP layer's
      ftk2_wait_screen (FTK2.Crucible/mcp/server.js, waitScreenTool) is client-side polling built
      on the already-registered crucible_ui_dump (UiCommands.cs:80). This module reimplements
      that same poll-and-substring-match loop directly against the RPC, since there is no single
      crucible_wait_screen command to call.

SAFETY, non-negotiable:
  crucible_chaos_advance nulls AdventureState.MapState.ChaosState when called with an EMPTY
  config-name argument (see ChaosCommands.cs's own doc comment on CrucibleChaosAdvance), and the
  next chaos tick then dereferences ChaosState.ChaosHistory with no null guard -- a live
  NullReferenceException. This module NEVER calls crucible_chaos_advance expecting an empty arg
  to work. The only crucible_chaos_advance test here is the refusal path: call it with an empty
  arg, assert the command itself refuses (returns an "error: ... REFUSED" string before touching
  ChaosState), AND assert -- via a separate crucible_chaos_state call before and after -- that
  chaos state is byte-for-byte unchanged across the refusal. No other crucible_chaos_advance call
  is made anywhere in this module.

  This module never clicks, invokes, or names continue-btn, load-btn, or load-game-btn (those
  resume the owner's live co-op saves), and never text-matches on "Continue" -- only exact
  element names, matching drive.py's DISMISSABLE convention. It does not save over or create
  fixture saves.
"""

import json
import os
import re
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

RESULTS = []
REPORTS = []
SHOTS = []


def shot(label):
    """Capture a screenshot and remember it. Verbatim from battery.py.

    Screenshots are a DEDICATED HTTP ROUTE (/screenshot), not an /exec command -- calling
    `crucible_screenshot` through exec returns HTTP 400 Bad Request, because no such command is
    registered. A visual claim is not verified until someone OPENS one of these; the harness can
    only prove it was captured.
    """
    try:
        out = drive._post("/screenshot", {})
    except Exception as exc:
        print("        (screenshot failed: %s)" % exc)
        return None
    path = ""
    if isinstance(out, dict):
        path = out.get("path") or ""
    else:
        for line in str(out).splitlines():
            if ".png" in line:
                path = line.strip()
    SHOTS.append((label, path))
    print("        shot[%s] %s" % (label, path[-64:] if path else "(no path)"))
    return path


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(("  PASS " if ok else "  FAIL ") + name + (("\n        " + detail) if detail else ""))


def report(name, detail):
    """For a verb with no independent read path. Not a pass, not a fail -- just evidence that the
    verb was called and what it returned, so the gap is closed honestly instead of by asserting on
    the command's own success string."""
    REPORTS.append((name, detail))
    print("  REPORT " + name + "\n          " + detail)


def wait_screen(expect, timeout_ms=10000, poll_interval=0.5):
    """Poll crucible_ui_dump until its output contains `expect` (case-insensitive substring), or
    the deadline passes. Mirrors FTK2.Crucible/mcp/server.js's waitScreenTool exactly -- same
    poll loop, same substring match -- since crucible_wait_screen is not itself a registered
    plugin command."""
    start = time.time()
    deadline = start + (timeout_ms / 1000.0)
    polls = 0
    last_dump = None
    while True:
        polls += 1
        last_dump = drive.run("crucible_ui_dump", ["-", "-"])
        if expect.lower() in last_dump.lower():
            return {"matched": True, "elapsedMs": int((time.time() - start) * 1000), "polls": polls}
        if time.time() >= deadline:
            return {"matched": False, "elapsedMs": int((time.time() - start) * 1000), "polls": polls,
                    "lastDump": last_dump}
        time.sleep(min(poll_interval, max(0, deadline - time.time())))


def main():
    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    shot("start")

    # ---- crucible_list_instances / health --------------------------------
    print("\n=== LIST_INSTANCES / HEALTH ===")
    # FTK2.Crucible/mcp/server.js's ftk2_list_instances probes CRUCIBLE_INSTANCES (default: a
    # single instance "p1" on CRUCIBLE_PORT, matching drive.py's single BASE) via GET /health for
    # each declared peer. This harness runs single-instance, so that enumeration degenerates to
    # exactly the probe below -- reported honestly rather than faked as multi-instance.
    instances = [{"name": "p1", "port": drive.PORT}]
    rows = []
    for inst in instances:
        try:
            health = drive._get("/health")
            rows.append({"instance": inst["name"], "port": inst["port"], "reachable": True,
                         "pumpAlive": health.get("pumpAlive"), "online": health.get("online"),
                         "bridgeAvailable": health.get("bridgeAvailable")})
        except Exception as exc:
            rows.append({"instance": inst["name"], "port": inst["port"], "reachable": False,
                         "error": str(exc)})
    check("list_instances: single declared instance is reachable via its own GET /health",
          len(rows) == 1 and rows[0]["reachable"],
          json.dumps(rows))
    check("health: pumpAlive reads back true from the live RPC response body",
          rows[0].get("pumpAlive") is True if rows[0]["reachable"] else False,
          json.dumps(rows[0]))

    # ---- crucible_pin_seed -------------------------------------------------
    print("\n=== PIN_SEED ===")
    seed = 424242
    pin_result = drive.run("crucible_pin_seed", [str(seed)])
    # Independent read: crucible_get on the SAME field the pin wrote, via a wholly separate
    # command/code path (ReflectionCommands.CrucibleGet, not DebugVerbCommands.CruciblePinSeed).
    get_result = drive.run("crucible_get", ["AdventureDirector._gameRandom.Seed"])
    if get_result.strip().startswith("error"):
        report("pin_seed: independent readback via crucible_get AdventureDirector._gameRandom.Seed",
               "crucible_get could not reach the field (%s) -- likely no active run/no live "
               "GameRandom instance right now. pin command's own embedded before/after reads: %s"
               % (get_result.strip(), pin_result.strip()))
    else:
        readback_matches = get_result.strip() == str(seed)
        check("pin_seed: crucible_get independently reads back the seed just written",
              readback_matches,
              "crucible_get AdventureDirector._gameRandom.Seed -> %r (wrote %d)"
              % (get_result.strip(), seed))

    # ---- crucible_quest_state / crucible_run_status ------------------------
    print("\n=== QUEST_STATE / RUN_STATUS ===")
    quest_result = drive.run("crucible_quest_state")
    run_result = drive.run("crucible_run_status")

    if quest_result.strip().startswith("error"):
        report("quest_state: active/completed/failed counts read back from GameRun",
               quest_result.strip())
    else:
        m = re.match(r"active=(\d+) completed=(\d+) failed=(\d+)", quest_result.strip())
        check("quest_state: response parses to non-negative active/completed/failed counts",
              m is not None and all(int(g) >= 0 for g in m.groups()),
              quest_result.strip()[:170])

    if run_result.strip().startswith("error"):
        report("run_status: structured map/round/stage state read back from AdventureState",
               run_result.strip())
    else:
        rm = re.search(r"round=(\S+)", run_result)
        check("run_status: response carries a round value read from AdventureState.MapState",
              rm is not None,
              run_result.splitlines()[0][:170] if run_result.splitlines() else run_result)

    # Cross-check: quest_state (DebugVerbCommands, reads GameRunData.ActiveQuests via RunAccess)
    # and run_status (RunCommands, reads the same list via PartyAccess) are two independently
    # registered commands touching overlapping state -- a real independent-read comparison, not
    # just "no exception".
    qm = re.match(r"active=(\d+)", quest_result.strip()) if not quest_result.strip().startswith("error") else None
    if qm and "activeQuests:" in run_result:
        check("quest_state & run_status agree on whether any quests are active",
              (int(qm.group(1)) > 0) == ("id=" in run_result),
              "quest_state active=%s, run_status activeQuests section=%r"
              % (qm.group(1), run_result[run_result.find("activeQuests:"):run_result.find("activeQuests:") + 120]))
    else:
        report("quest_state & run_status cross-check",
               "one or both verbs reported no active run; nothing to cross-check right now")

    # ---- crucible_chaos_state ----------------------------------------------
    print("\n=== CHAOS_STATE ===")
    chaos_before = drive.run("crucible_chaos_state")
    check("chaos_state: response is one of the two known shapes "
          "('chaosHistoryCount=' when a run has chaos, 'no active chaos' note otherwise)",
          ("chaosHistoryCount=" in chaos_before) or ("no active chaos" in chaos_before),
          chaos_before.strip()[:200])

    # ---- crucible_chaos_advance: REFUSAL PATH ONLY (see module docstring SAFETY) -------------
    print("\n=== CHAOS_ADVANCE (refusal path only -- see SAFETY in module docstring) ===")
    advance_result = drive.run("crucible_chaos_advance", [""])
    check("chaos_advance: an empty config-name arg is REFUSED before touching ChaosState",
          advance_result.strip().startswith("error:") and "REFUSED" in advance_result,
          advance_result.strip()[:220])

    chaos_after = drive.run("crucible_chaos_state")
    check("chaos_advance: chaos state is UNCHANGED across the refused call "
          "(independently re-read via crucible_chaos_state, not inferred from the refusal message)",
          chaos_after.strip() == chaos_before.strip(),
          "before=%r\n        after=%r" % (chaos_before.strip()[:170], chaos_after.strip()[:170]))

    # ---- crucible_read_trace -------------------------------------------------
    print("\n=== READ_TRACE ===")
    correlation_id = uuid.uuid4().hex
    probe_command = "crucible_chaos_state"
    drive._post("/exec", {"command": probe_command, "args": [], "correlationId": correlation_id})
    trace = drive._get("/trace?n=30")
    lines = trace.get("lines") or []
    found = None
    for line in lines:
        try:
            entry = json.loads(line) if isinstance(line, str) else line
        except (ValueError, TypeError):
            continue
        if isinstance(entry, dict) and entry.get("correlationId") == correlation_id:
            found = entry
            break
    check("read_trace: the trace buffer independently records the exec call just made "
          "(matched by a correlationId this test generated, not by the exec response)",
          found is not None and found.get("command") == probe_command,
          ("matched entry: %s" % json.dumps(found)) if found else
          ("correlationId %s not found among %d tail lines" % (correlation_id, len(lines))))

    # ---- crucible_wait_screen (reimplemented poll loop; see module docstring) ----------------
    print("\n=== WAIT_SCREEN ===")
    docs_now = drive.visible_docs()
    if docs_now:
        target = docs_now[0]
        result = wait_screen(target, timeout_ms=5000)
        check("wait_screen: matches a UIDocument that crucible_ui_dump independently reports as visible",
              result["matched"],
              "expect=%r -> %s" % (target, json.dumps(result)))
    else:
        report("wait_screen", "crucible_ui_dump reported no visible UIDocuments right now; "
               "nothing on screen to wait for")

    shot("end")

    print("\n" + "=" * 62)
    bad = [n for n, ok in RESULTS if not ok]
    print("%d/%d passed" % (len(RESULTS) - len(bad), len(RESULTS)))
    for n in bad:
        print("  FAILED: " + n)
    if REPORTS:
        print("\nreported-only (no independent read path available right now):")
        for n, detail in REPORTS:
            print("  %s: %s" % (n, detail[:170]))
    if SHOTS:
        print("\nscreenshots to OPEN (a visual claim is not verified until one is looked at):")
        for label, path in SHOTS:
            print("  %-12s %s" % (label, path))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
