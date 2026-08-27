#!/usr/bin/env python3
"""Close the last two OVERWORLD checklist items: non-combat venues and dungeon entry.

docs/research/test-checklist.md section C, unticked:
  - crucible_interact on non-combat venues (town, market, quest board)
  - Dungeon entry / DungeonState.LoopState

THE HAZARD this module exists to respect: five eEncounterActions branches --
MARKET, QUEST_BOARD, TOWN_SERVICES, MERC_GUILD, PET_SHOP -- never call
_closeEncounterMenuAsync (see RunEndCommands.cs's own docstring on that gap), so an encounter
opened through one of them stays open. rotate_check.py already measured the fix live
2026-08-24 on an "Abandoned Village" search node: crucible_encounter_leave ALONE left the panel
up; clicking the panel's own close-btn (element NAME, never a text match) first, THEN
crucible_encounter_leave, THEN drive.clear_gates(), is what actually clears it.

So every branch here is: open -> screenshot -> close -> VERIFY closed by reading state back,
one branch at a time, before the next branch is even attempted. If a close fails, this module
stops testing further branches, reports that branch WEDGED, and says plainly that the game
needs a restart -- it does not keep going and produce garbage results from a half-broken UI.

Discovering which hex offers which action is itself made safe the same way: crucible_interact
validates the requested action against the encounter's own ActionList BEFORE doing anything,
and refuses (no side effect) with the real list when the action offered isn't there
(OverworldCommands.cs's CrucibleInteract). Probing with "NONE" -- eEncounterActions' own zero
value, never a real venue offering -- reads that list for free without performing anything.

Dungeon entry uses the DUNGEON action the same way, then the safe exit path verified by
grepping the plugin (see module docstring further down at DUNGEON_EXIT) rather than the
run-abandoning QuitToMenu.

THE FIXTURE HAS NOTHING TO FIND: to_combat.DEFAULT_RUN is built "with every quest removed and
every non-combat encounter cleared" (to_combat.py's own comment) -- measured live, its map holds
40 encounters, 39 bare-GUID roaming monsters and exactly one named venue, TAVERN. No MARKET,
QUEST_BOARD, TOWN_SERVICES, MERC_GUILD, PET_SHOP or DUNGEON exists anywhere on it. TAVERN is
tested as-is (test_tavern). The other five are tested by first looking for one already on the
map (find_venue_offering, unchanged) and, only if that finds nothing, reaching for
crucible_debug_spawn to place one (find_or_spawn_venue) -- see SPAWN_VENUE below for what that
command actually does and why it's safe to call blind.

Everything here asserts on state READ BACK, never on a command's own success string, matching
drive.py / battery.py / verbs.py.

Usage:
    python FTK2.Crucible/tools/overworld.py
"""

import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive
import to_combat  # noqa: E402

RESULTS = []
REPORTS = []
SHOTS = []
WEDGED = []

# The five branches that never close themselves (RunEndCommands.cs), tested one at a time.
LEAK_BRANCHES = ["MARKET", "QUEST_BOARD", "TOWN_SERVICES", "MERC_GUILD", "PET_SHOP"]

# The close sequence measured to actually work on a stuck panel (rotate_check.py, 2026-08-24):
# close-btn by NAME, then crucible_encounter_leave, then drive.clear_gates(). crucible_encounter_leave
# alone was measured to leave the panel up.
CLOSE_BTN = "close-btn"


def shot(label):
    """Capture a screenshot and remember it. Verbatim from battery.py / verbs.py.

    Screenshots are a DEDICATED HTTP ROUTE (/screenshot), not an /exec command -- there is no
    crucible_screenshot command registered. A visual claim is not verified until someone OPENS
    one of these; the harness can only prove it was captured.
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
    """For evidence that isn't a strict pass/fail -- e.g. "no venue offering this action was
    found nearby". Not silently skipped: printed and counted separately from PASS/FAIL."""
    REPORTS.append((name, detail))
    print("  REPORT " + name + "\n          " + detail)


def close_btn_visible():
    return "name='%s'" % CLOSE_BTN in drive.run("crucible_ui_dump", ["-", "button"])


def hero_hex():
    m = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", drive.run("crucible_overworld_state"))
    return (int(m.group(1)), int(m.group(2))) if m else None


def named_encounters(max_candidates=25):
    """Named (non-roaming) encounters -- towns/venues/dungeons -- nearest first.

    Mirrors to_combat.walk_onto_encounter's split: a bare-GUID name is a roaming monster/search
    node, anything else (TAVERN, AF_TOWN_B, ...) is a venue. Only the latter can offer the
    non-combat actions this module tests.
    """
    hero = hero_hex()
    if not hero:
        return []
    px, py = hero
    candidates = []
    for line in drive.run("crucible_map_encounters", ["-"]).splitlines():
        m = re.match(r"\[(-?\d+),(-?\d+)\] (\S+)", line.strip())
        if not m:
            continue
        x, y, name = int(m.group(1)), int(m.group(2)), m.group(3)
        if (x, y) == (px, py):
            continue
        if re.match(r"^[0-9a-f]{8}-", name):
            continue  # roaming, not a named venue
        candidates.append((max(abs(x - px), abs(y - py)), x, y, name))
    candidates.sort()
    return [(x, y, name) for _dist, x, y, name in candidates[:max_candidates]]


def move_onto(x, y):
    result = drive.run("crucible_move", [str(x), str(y), "false", "false", "true"])
    if "move invoked" not in result:
        return False
    drive.wait_ready(timeout_seconds=40)
    # Only the active character walks; gather the rest so a later encounter captures the whole
    # party (to_combat.py's convention).
    drive.run("crucible_party_set_hex", [str(x), str(y)])
    time.sleep(2)
    return True


def offered_actions(x, y, name):
    """crucible_interact's own ActionList refusal, read as a safe probe.

    "NONE" is eEncounterActions' zero/sentinel value -- never a real venue offering -- so this
    call is expected to be REFUSED every time, and the refusal text carries the encounter's real
    ActionList (OverworldCommands.cs CrucibleInteract) with no side effect performed.
    """
    result = drive.run("crucible_interact", ["NONE"])
    m = re.search(r"actions:\s*(.+)", result)
    if not m:
        return None, result

    # The refusal prints the list as `COUNT=2 [LEAVE, VENUE]`, so a naive split on "," yields
    # 'COUNT=2 [LEAVE' and 'VENUE]' and every membership test misses. Measured live on the
    # fixture's TAVERN: it really did offer VENUE and the test reported that it did not.
    # Take what is INSIDE the brackets when they are present, and strip any leftover bracket.
    raw = m.group(1).strip()
    inner = re.search(r"\[(.*?)\]", raw)
    if inner:
        raw = inner.group(1)
    actions = {a.strip().strip("[]").upper() for a in raw.split(",") if a.strip().strip("[]")}
    actions.discard("")
    return actions, result


def find_venue_offering(action, cache, max_candidates=25):
    """Walks named encounters until one's ActionList offers `action`. Tries the last venue that
    worked first -- towns commonly bundle several of these actions on one hex."""
    if cache.get("last"):
        x, y, name = cache["last"]
        if move_onto(x, y):
            actions, raw = offered_actions(x, y, name)
            if actions and action in actions:
                return x, y, name, actions

    for x, y, name in named_encounters(max_candidates):
        if not move_onto(x, y):
            continue
        actions, raw = offered_actions(x, y, name)
        if actions is None:
            continue
        if action in actions:
            cache["last"] = (x, y, name)
            return x, y, name, actions
    return None


def find_encounter_by_name(target_name):
    """Nearest map encounter with this EXACT name -- used for TAVERN, the one named venue the
    fixture already has, instead of find_venue_offering's "any venue that offers X" search."""
    hero = hero_hex()
    if not hero:
        return None
    px, py = hero
    candidates = []
    for line in drive.run("crucible_map_encounters", ["-"]).splitlines():
        m = re.match(r"\[(-?\d+),(-?\d+)\] (\S+)", line.strip())
        if not m:
            continue
        x, y, name = int(m.group(1)), int(m.group(2)), m.group(3)
        if name != target_name:
            continue
        candidates.append((max(abs(x - px), abs(y - py)), x, y, name))
    candidates.sort()
    return (candidates[0][1], candidates[0][2], candidates[0][3]) if candidates else None


# ------------------------------------------------------------------------------------ SPAWN_VENUE
#
# CAN_WE_PLACE_VENUES / CAN_WE_PLACE_A_DUNGEON: yes, with existing verbs -- crucible_debug_spawn
# (DebugSpawnCommands.cs, registered) drives the game's OWN F6 debug menu builder,
# DebugHelper.GenerateSpawnEncountersDebugMenuButtons (verified in
# .decompile-scratch/proj/DebugHelper.cs:717-798, kind="encounters"). That method buckets every
# SkillEncounterConfig by tag into six categories -- STANDARD, AMBUSH, DUNGEON, MARKET,
# DARK CARNIVAL, QUEST(by rarity) -- and, only when a category has MORE THAN ONE config
# (DebugHelper.cs:752 `if (category.Value.Count > 1)`), adds a "Spawn All <Category>" button that
# places one of every config in that category on a hex within ring 1-10 of the party
# (HexHelper.GetHexPositionsWithinRingAroundParty, DebugHelper.cs:776).
#
# crucible_debug_spawn's selector match is a case-insensitive substring search over
# "{container}title" dictionary keys (DebugSpawnCommands.cs CrucibleDebugSpawn). The per-category
# NAV button ("Market", "Dungeon", ...) that just opens a debug submenu lives in container
# "F6.ENCOUNTERS"; the actual "Spawn All <Category>" button lives in container
# "F6.ENCOUNTERS.<TAG>" (DebugHelper.cs:748 vs :754) -- so passing "Spawn All Market" /
# "Spawn All Dungeon" as the selector can only land on the real spawn button, never the nav-only
# header, because the substring "Spawn All " only appears in the spawn button's own key.
#
# There is no dedicated category for QUEST_BOARD / TOWN_SERVICES / MERC_GUILD / PET_SHOP -- the
# debug menu buckets by encounter TAG, not by eEncounterActions -- so those four are only reached
# if a spawned MARKET/QUEST/STANDARD config happens to bundle that action on its own ActionList,
# same bundling this module already assumes for hand-placed towns (find_venue_offering's own
# comment: "towns commonly bundle several of these actions on one hex"). DUNGEON has its own
# category and is spawned directly. If a category has 0 or 1 configs, no "Spawn All" button
# exists, crucible_debug_spawn reports "no button matches", and this is treated as a REPORT, not
# faked as a pass.
SPAWN_CATEGORIES_FOR_ACTION = {
    "MARKET": ["MARKET", "QUEST", "STANDARD"],
    "QUEST_BOARD": ["QUEST", "MARKET", "STANDARD"],
    "TOWN_SERVICES": ["MARKET", "QUEST", "STANDARD"],
    "MERC_GUILD": ["MARKET", "QUEST", "STANDARD"],
    "PET_SHOP": ["MARKET", "QUEST", "STANDARD"],
    "DUNGEON": ["DUNGEON"],
}


def spawn_encounter_category(category):
    """Invokes the "Spawn All <Category>" debug-spawn button via crucible_debug_spawn. Returns
    (invoked, raw_result) -- invoked is read from the command's own "invoked=True/False" field,
    which DebugSpawnCommands.cs sets from the actual delegate invocation, not merely from the
    selector having matched."""
    title = "Spawn All %s" % category.title()
    result = drive.run("crucible_debug_spawn", ["encounters", title])
    invoked = "invoked=True" in result
    return invoked, result


def find_or_spawn_venue(action, cache, spawn_log):
    """find_venue_offering first -- uses whatever the fixture already has. Only if nothing on the
    map already offers `action` does this reach for crucible_debug_spawn, trying each category in
    SPAWN_CATEGORIES_FOR_ACTION until one both invokes AND a rescan of the map turns up a venue
    that actually offers `action` -- a spawn that reports invoked=True but doesn't produce the
    wanted action is not treated as success, the search just continues to the next category."""
    found = find_venue_offering(action, cache)
    if found:
        return found

    for category in SPAWN_CATEGORIES_FOR_ACTION.get(action, []):
        invoked, raw = spawn_encounter_category(category)
        spawn_log.append("%s <- category %s: %s" % (
            action, category, raw.splitlines()[0][:160] if raw else "(no result)"))
        if not invoked:
            continue
        # crucible_debug_spawn's own note: spawning is asynchronous -- poll, don't trust immediately.
        time.sleep(3)
        cache.pop("last", None)  # force a full rescan; the just-spawned encounter is what we want
        found = find_venue_offering(action, cache)
        if found:
            spawn_log.append("%s: found after spawning category %s" % (action, category))
            return found

    return None


def close_open_panel():
    """The measured-working close sequence: close-btn by NAME, then crucible_encounter_leave,
    then drive.clear_gates(). Returns (closed, detail)."""
    drive.run("crucible_ui_click", [CLOSE_BTN])
    time.sleep(1.0)
    drive.run("crucible_encounter_leave", [])
    time.sleep(1.0)
    gates = drive.clear_gates()
    time.sleep(1.0)
    closed = (not close_btn_visible()) and drive.interaction_enabled()
    return closed, "close-btn gone=%s interactionEnabled=%s gates=%s" % (
        not close_btn_visible(), drive.interaction_enabled(), gates)


def test_tavern():
    """Tests the ONE named venue the fixture actually has -- see the module docstring's
    "THE FIXTURE HAS NOTHING TO FIND" note. TAVERN itself is not an eEncounterActions member; the
    action AdventureDirector.cs routes any venue (tavern included) through is VENUE
    (.decompile-scratch/proj/AdventureDirector.cs:9944 `case eEncounterActions.VENUE:` --
    self-closes via _closeEncounterMenuAsync before performing the venue action). This still
    drives the panel through close_open_panel()'s manual close-and-verify sequence rather than
    trusting that self-close, matching every other branch here."""
    print("\n=== TAVERN (already on the fixture map) ===")
    found = find_encounter_by_name("TAVERN")
    if not found:
        report("TAVERN", "no encounter named TAVERN found on the map -- fixture may have changed")
        return True  # not wedged -- just nothing to test

    x, y, name = found
    if not move_onto(x, y):
        report("TAVERN", "crucible_move to (%d,%d) did not report success" % (x, y))
        return True

    actions, raw = offered_actions(x, y, name)
    if actions is None:
        report("TAVERN", "crucible_interact NONE did not return a readable ActionList: %s" % raw[:200])
        return True
    print("  standing on (%d,%d) TAVERN  ActionList=%s" % (x, y, sorted(actions)))

    if "VENUE" not in actions:
        report("TAVERN", "ActionList does not offer VENUE (the action TAVERN venues route "
               "through per AdventureDirector.cs); actual offering=%s" % sorted(actions))
        return True

    result = drive.run("crucible_interact", ["VENUE"])
    opened_evidence = close_btn_visible()
    check("TAVERN: crucible_interact VENUE opens the panel (close-btn present, read back)",
          opened_evidence, result.splitlines()[0][:170] if result else "(no result)")
    shot("venue-TAVERN-open")

    if not opened_evidence:
        return True  # nothing opened -- nothing to close either, not a wedge

    closed, detail = close_open_panel()
    check("TAVERN: panel closes via close-btn + crucible_encounter_leave + clear_gates "
          "(close-btn gone AND interaction re-enabled, read back)", closed, detail)
    shot("venue-TAVERN-after-close")

    if not closed:
        WEDGED.append("TAVERN")
        print("\n  *** WEDGED: TAVERN did not close. STOPPING further branch tests. ***")
        print("  *** The game needs a restart before any further overworld testing. ***")
        return False

    return True


def test_leak_branch(action, cache):
    print("\n=== VENUE: %s ===" % action)
    spawn_log = []
    found = find_or_spawn_venue(action, cache, spawn_log)
    for line in spawn_log:
        print("  spawn: %s" % line)
    if not found:
        detail = "no named encounter within the searched radius offered %s" % action
        if spawn_log:
            detail += "; spawn attempts: " + " | ".join(spawn_log)
        report("%s: a venue offering this action" % action, detail)
        return True  # not wedged -- just nothing to test

    x, y, name, actions = found
    print("  standing on (%d,%d) %s  ActionList=%s" % (x, y, name, sorted(actions)))

    result = drive.run("crucible_interact", [action])
    opened_evidence = close_btn_visible()
    check("%s: crucible_interact opens the panel (close-btn present, read back)" % action,
          opened_evidence, result.splitlines()[0][:170] if result else "(no result)")
    shot("venue-%s-open" % action)

    if not opened_evidence:
        # Nothing opened -- nothing to close either. Not a wedge, just didn't take.
        return True

    closed, detail = close_open_panel()
    check("%s: panel closes via close-btn + crucible_encounter_leave + clear_gates" % action,
          closed, detail)
    shot("venue-%s-after-close" % action)

    if not closed:
        WEDGED.append(action)
        print("\n  *** WEDGED: %s did not close. STOPPING further branch tests. ***" % action)
        print("  *** The game needs a restart before any further overworld testing. ***")
        return False

    return True


# ---------------------------------------------------------------------------------- DUNGEON_EXIT
#
# The safe exit, verified by grepping the plugin and the decompiled game source (not guessed):
#   1. DungeonState.ForceEndDungeon is a FIELD (DungeonState.cs: `public bool ForceEndDungeon;`),
#      not a command -- there is no crucible_dungeon_force_end verb registered. Written via the
#      already-registered crucible_set, path "RouterHelper.Env.GameRun.DungeonState.ForceEndDungeon"
#      (RouterHelper.cs: `public static Env Env { get; }`; Env.cs: `public GameRunData GameRun;`
#      GameRunData -> DungeonState.ForceEndDungeon; the same Env/GameRun path RunCommands.cs's own
#      crucible_run_status walks).
#   2. DungeonDirector._nextPhase() (DungeonDirector.cs:1151, `private async Task _nextPhase()`)
#      is the pump that consumes the flag: `if (_state.ForceEndDungeon) { ... }` sits directly in
#      that method (DungeonDirector.cs:575). Written via the already-registered crucible_invoke.
#   docs/research/crucible-mcp-tool-surface.md row I4 documents this exact pair as the intended
#   ftk2_dungeon_force_end implementation.
#
# QuitToMenu() (DungeonDirector.cs:2293) is the OTHER exit and is NEVER called here -- it abandons
# the run, per the task's own warning.
FORCE_END_PATH = "RouterHelper.Env.GameRun.DungeonState.ForceEndDungeon"


def run_status_in_dungeon():
    """True/False/None (unreadable) parsed from crucible_run_status's own inDungeon= field,
    which is DungeonState.LoopState != null (RunCommands.cs CrucibleRunStatus)."""
    result = drive.run("crucible_run_status")
    m = re.search(r"inDungeon=(True|False)", result)
    return (m.group(1) == "True") if m else None, result


def test_dungeon_entry():
    print("\n=== DUNGEON ENTRY ===")

    # Read crucible_run_status FIRST to confirm it actually reports inDungeon=, before trusting
    # it as the assertion surface (per task instructions).
    in_dungeon_before, raw_before = run_status_in_dungeon()
    check("run_status: reports an inDungeon= field before entry",
          in_dungeon_before is not None, raw_before.splitlines()[1] if "\n" in raw_before else raw_before[:170])
    if in_dungeon_before is None:
        report("dungeon entry", "crucible_run_status did not report inDungeon=; cannot proceed safely "
               "(%s)" % raw_before[:200])
        return
    check("run_status: not already in a dungeon before this test", not in_dungeon_before, raw_before[:170])

    cache = {}
    spawn_log = []
    found = find_or_spawn_venue("DUNGEON", cache, spawn_log)
    for line in spawn_log:
        print("  spawn: %s" % line)
    if not found:
        detail = "no named encounter within the searched radius offered DUNGEON"
        if spawn_log:
            detail += "; spawn attempts: " + " | ".join(spawn_log)
        report("dungeon entry", detail)
        return
    x, y, name, actions = found
    print("  standing on (%d,%d) %s  ActionList=%s" % (x, y, name, sorted(actions)))

    drive.run("crucible_interact", ["DUNGEON"])
    # The transition is async (crucible_interact's own note: _performEncounterAction is async void
    # and the venue transition waits ~0.75s before raising the route change) -- poll, don't sleep once.
    entered = drive.wait_until(lambda: run_status_in_dungeon()[0] is True, timeout_seconds=45, interval=1)
    in_dungeon_after, raw_after = run_status_in_dungeon()
    check("run_status: inDungeon=True after DUNGEON interact (LoopState non-null, read back)",
          entered is True and in_dungeon_after is True, raw_after[:220])

    route = (drive.snapshot() or {}).get("route")
    check("state snapshot: route reads DUNGEON", route == "DUNGEON", "route=%s" % route)

    if not (entered is True):
        report("dungeon entry", "did not confirm entry; skipping the exit sequence to avoid acting "
               "on an unknown state")
        return

    shot("dungeon-interior")

    # ---- safe exit: ForceEndDungeon field write, then the _nextPhase() pump. See DUNGEON_EXIT.
    set_result = drive.run("crucible_set", [FORCE_END_PATH, "true"])
    check("crucible_set writes DungeonState.ForceEndDungeon=true",
          set_result.startswith("old=") and "new=True" in set_result, set_result[:200])

    invoke_result = drive.run("crucible_invoke", ["DungeonDirector", "_nextPhase", "-"])
    print("  _nextPhase(): %s" % invoke_result[:200])

    exited = drive.wait_until(lambda: run_status_in_dungeon()[0] is False, timeout_seconds=45, interval=1)
    in_dungeon_final, raw_final = run_status_in_dungeon()
    check("run_status: inDungeon=False after ForceEndDungeon + _nextPhase() (read back, never QuitToMenu)",
          exited is True and in_dungeon_final is False, raw_final[:220])

    drive.wait_ready(timeout_seconds=30)
    shot("after-dungeon-exit")


def main():
    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    # Load the fixture FIRST. Without this every check reports "error: no run loaded" and the
    # module produces a full page of confident-looking results that tested nothing at all --
    # which is exactly what happened on its first run.
    if "run=True" not in drive.describe():
        print("no run loaded; loading the fixture %s" % to_combat.DEFAULT_RUN)
        transcript = drive.load_run(to_combat.DEFAULT_RUN)
        print("  %s" % (transcript.splitlines()[-1][:140] if transcript else "(no transcript)"))
        drive.clear_gates()
        drive.pick_reward()
    if "run=True" not in drive.describe():
        print("FAILED: could not load a run; refusing to report results that would all be "
              "'no run loaded'. Restart the game and re-run.")
        return 2

    ready = drive.wait_ready(timeout_seconds=30)
    print("start: %s docs=%s" % (drive.describe(), ", ".join(ready.get("docs") or [])))
    shot("start")

    tavern_ok = test_tavern()

    cache = {}
    if tavern_ok:
        for action in LEAK_BRANCHES:
            ok = test_leak_branch(action, cache)
            if not ok:
                break  # WEDGED -- do not touch anything else
    else:
        report("MARKET/QUEST_BOARD/TOWN_SERVICES/MERC_GUILD/PET_SHOP",
               "SKIPPED -- TAVERN is WEDGED; the game needs a restart before any further "
               "overworld testing can be trusted")

    if not WEDGED:
        test_dungeon_entry()
    else:
        report("dungeon entry", "SKIPPED -- a venue branch above is WEDGED; the game needs a "
               "restart before any further overworld testing can be trusted")

    shot("end")

    print("\n" + "=" * 62)
    bad = [n for n, ok in RESULTS if not ok]
    print("%d/%d passed" % (len(RESULTS) - len(bad), len(RESULTS)))
    for n in bad:
        print("  FAILED: " + n)
    if WEDGED:
        print("\nWEDGED branches (did not close -- game needs a restart):")
        for name in WEDGED:
            print("  " + name)
    if REPORTS:
        print("\nreported-only (no venue found / test skipped):")
        for n, detail in REPORTS:
            print("  %s: %s" % (n, detail[:200]))
    if SHOTS:
        print("\nscreenshots to OPEN (a visual claim is not verified until one is looked at):")
        for label, path in SHOTS:
            print("  %-24s %s" % (label, path))
    return 1 if (bad or WEDGED) else 0


if __name__ == "__main__":
    sys.exit(main())
