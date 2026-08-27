#!/usr/bin/env python3
r"""
bench.py -- the reproducible scenario bench.

Loads a `crucible.scenario.v1` manifest and runs it end to end:

    copy the fixture -> load it -> pin the seed -> VERIFY THE PARTY -> spawn the enemies
    -> run setup -> fire each step -> capture the tells -> evaluate the asserts -> verdict

Format spec, with every field and why it exists:  scenarios/README.md
Method this encodes:                              docs/research/VERIFICATION-METHOD.md

--------------------------------------------------------------------------------------------
THE ONE RULE
--------------------------------------------------------------------------------------------
Every player-facing step reports a PAIR -- a log proof it fired AND a screen proof the player
can see it. Never one alone.

    log proc | on screen | verdict
    ---------+-----------+---------------------------------------------
    yes      | yes       | PASS
    yes      | no        | FAIL - invisible mechanic
    no       | yes       | FAIL - something else caused it
    no       | no        | NO-FIRE - check the gate/seed before calling it broken
    either   | unreviewed| PENDING-EYES   (deliberately NOT a pass)

This runner cannot look at a picture. It captures the crops, blank-checks them, records what
the manifest said should be visible, and stops at PENDING-EYES. `bench.py attest` records what
a human/vision pass actually saw; `bench.py verdict` then recomputes the pair. The runner will
not manufacture the half it cannot observe, because that specific false positive -- "the code
said it proc'd, so it works" -- has already shipped here.

--------------------------------------------------------------------------------------------
WHAT THIS FILE REFUSES TO DO, AND WHY
--------------------------------------------------------------------------------------------
* It never loads a fixture in place. A driven run autosaves over its own save MID-session, so
  the pristine copy lives outside GameRuns\ and is copied in every time.
* It never accepts `fixture_health` as proof a fixture is clean -- that only checks file SIZE
  and certified a content-corrupted save healthy for a whole session. The party gate is
  `crucible_party_list`, matched on DISPLAY NAME (which is what a screenshot can show), with the
  four vanilla names as an explicit blocklist.
* It never treats `null` as empty. A null `statuses`/`auraStatuses`/`customData` means Crucible
  could not read the member; the assert reports UNREADABLE, never PASS and never FAIL, and the
  matching `member_missing:` warning is carried into the report.
* It never calls crucible_kill_all (kills BOTH sides) or crucible_force_combat (wedges).
* It never presses continue-btn / load-btn / load-game-btn and never text-matches "Continue" --
  those resume the operator's real co-op saves. Loading goes through drive.load_run() by run id.
"""

import argparse
import copy
import glob
import hashlib
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

SCENARIO_DIR = os.path.join(HERE, "scenarios")
SCHEMA = "crucible.scenario.v1"

# Runs land next to the other harness artifacts, never in the repo.
RUN_ROOT = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
    "Temp", "claude", "C--Users-ben-repos",
    "0cfd7dc6-16ae-44ba-8cc4-2f320ee515be", "scratchpad", "bench")

PLAYER_LOG = os.path.expandvars(
    r"%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\Player.log")

# Seeing one of these on a party card means the fixture was built with crucible_party_set_class,
# which rewrites ConfigName but NOT the name -- so the class is right in state and wrong on every
# screenshot. That voided a whole verification run once. Default blocklist for `party.forbid_names`.
VANILLA_NAMES = ["Shepherd", "Thief", "Corsair", "Bladedancer"]

OUR_CLASSES = {
    "CF_ORIG_VAMPIRIC": "Vampiric",
    "CF_ORIG_PACIFIST": "Pacifist",
    "CF_ORIG_TRAINER": "Pokemon Trainer",
    "CF_ORIG_GARY": "Gary",
    "CF_ORIG_CHAOSMAGE": "Chaos Mage",
}

ASSERT_KINDS = {
    "hp", "hp_delta", "max_hp", "alive", "status_present", "status_absent",
    "status_duration", "custom_data", "thing_custom_data", "tile_aura", "count", "stat",
}
OPS = {"eq", "ne", "lt", "lte", "gt", "gte", "contains", "not_contains"}
SETUP_OPS = {"exec", "godmode", "custom_data", "wipe_enemies", "end_turn", "restore_actions"}
TELL_KINDS = {"identity", "effect", "preview"}
# Kept in step with evidence.py rather than duplicated: this list went stale when the
# `actionmenu` and `preview` regions were added there, and validate then rejected every
# manifest that used the best identity tell in the harness.
def evidence_regions():
    """The regions evidence.py actually defines, read from it rather than duplicated.

    This WAS a hardcoded literal, and it went stale the moment `actionmenu` and `preview` were
    added to evidence.py -- validate then rejected every manifest that used the best identity
    tell in the harness."""
    try:
        import evidence
        return {"full"} | set(evidence.REGIONS)
    except Exception:
        return {"full", "board", "portraits", "party", "enemy", "toolbelt", "panel", "tooltip",
                "actionmenu", "preview"}

# Fail-safety: a recipe effect that dies but lets the plan continue is a PASS for fail-safety and
# a FAIL for correctness. Reported as two separate lines, never folded together.
SOFT_FAIL_MARKER = "Recipe effect failed"
HARD_FAIL_MARKERS = (
    "NullReferenceException", "KeyNotFoundException", "IndexOutOfRangeException",
    "InvalidCastException", "Unhandled exception",
)


# =============================================================================================
# PURE LAYER -- no game, no network, no files. Everything here is unit-testable offline and is
# exercised by bench_selftest.py.
# =============================================================================================

class ValidationError(ValueError):
    pass


def validate(manifest, path="<manifest>"):
    """Structural gate. Raises ValidationError listing EVERY problem, not just the first.

    Runs offline with no game up, and runs again automatically before every scenario, because a
    manifest that is wrong in a way the runner only notices at step 4 has already cost a boot.
    """
    problems = []

    def need(cond, msg):
        if not cond:
            problems.append(msg)

    need(manifest.get("schema") == SCHEMA,
         "schema must be %r, got %r" % (SCHEMA, manifest.get("schema")))
    need(bool(manifest.get("name")), "name is required")
    need(manifest.get("kind") in ("isolation", "batch"),
         "kind must be 'isolation' or 'batch', got %r" % manifest.get("kind"))
    need(bool(manifest.get("checklist_item")),
         "checklist_item is required -- a scenario that closes nothing should not exist")

    fixture = manifest.get("fixture") or {}
    need(bool(fixture.get("run_id")), "fixture.run_id is required")
    need(bool(fixture.get("backup")), "fixture.backup is required (the pristine copy to load)")
    need("load_copy" not in fixture,
         "fixture.load_copy is not a field. The fixture is ALWAYS copied before loading; a "
         "driven run autosaves over its own save mid-session, so loading in place destroys it.")
    need(fixture.get("status") in ("built", "not-built"),
         "fixture.status must be 'built' or 'not-built'. A manifest whose bed does not exist yet "
         "is still worth having -- it carries the build recipe -- but it must SAY SO rather than "
         "failing at load time and looking like a loader bug.")

    party = manifest.get("party") or {}
    need(isinstance(party.get("expect_classes"), list) and party.get("expect_classes"),
         "party.expect_classes must be a non-empty list of DISPLAY names")
    for name in party.get("expect_classes") or []:
        if name in VANILLA_NAMES:
            problems.append(
                "party.expect_classes contains the VANILLA class %r -- a vanilla party is not "
                "evidence about our classes" % name)

    has_seed = manifest.get("seed") is not None
    search = manifest.get("seed_search")
    need(has_seed or search,
         "seed must be pinned, or seed_search must say how to find one. An unpinned, unsearched "
         "scenario is not reproducible.")
    if search:
        need(isinstance(search.get("success_assert"), str) and search.get("success_assert"),
             "seed_search.success_assert is required")
        cands = search.get("candidates") or {}
        need(("list" in cands) or ("start" in cands and "count" in cands),
             "seed_search.candidates needs either 'list' or 'start'+'count'")

    # ---- tells: >=2, >=1 identity. The rule that stops a frame being misread.
    tells = manifest.get("tells") or []
    need(len(tells) >= 2,
         "at least 2 tells are required (got %d) -- a single tell gets misread" % len(tells))
    need(any(t.get("kind") == "identity" for t in tells),
         "at least one tell must be kind 'identity' -- without it nothing proves which CLASS "
         "acted, and our classes are exactly %s" % ", ".join(sorted(OUR_CLASSES.values())))
    for i, tell in enumerate(tells):
        _validate_tell(tell, "tells[%d]" % i, problems)

    # ---- asserts
    assert_ids = set()
    for i, a in enumerate(manifest.get("asserts") or []):
        _validate_assert(a, "asserts[%d]" % i, problems, assert_ids)

    # ---- setup
    for i, op in enumerate(manifest.get("setup") or []):
        if op.get("op") not in SETUP_OPS:
            problems.append("setup[%d].op %r is not one of %s"
                            % (i, op.get("op"), ", ".join(sorted(SETUP_OPS))))

    # ---- enemies
    for i, e in enumerate(manifest.get("enemies") or []):
        if not e.get("config"):
            problems.append("enemies[%d].config is required" % i)
        if e.get("group") not in (0, 1, None):
            problems.append("enemies[%d].group must be 0 (player side) or 1 (enemy side)" % i)

    # ---- steps
    step_ids = set()
    for i, step in enumerate(manifest.get("steps") or []):
        where = "steps[%d]" % i
        sid = step.get("id")
        if not sid:
            problems.append("%s.id is required" % where)
        elif sid in step_ids:
            problems.append("%s.id %r is duplicated" % (where, sid))
        else:
            step_ids.add(sid)
        if not step.get("label"):
            problems.append("%s.label is required -- it is what the report reads" % where)
        player_facing = step.get("player_facing", True)
        log = step.get("log") or {}
        if player_facing and not log.get("proc"):
            problems.append(
                "%s.log.proc is required for a player-facing step. A step with no expected proc "
                "line can only ever be half-evidenced, and half-evidence is the false positive "
                "this bench exists to prevent. Set \"player_facing\": false if this step only "
                "advances the fight (end turns, reposition) and claims nothing." % where)
        if not player_facing and (step.get("tells") or step.get("log")):
            problems.append(
                "%s is player_facing:false but carries tells/log -- it is claiming something "
                "after all. Either drop them or make it player-facing." % where)
        for j, t in enumerate(step.get("tells") or []):
            _validate_tell(t, "%s.tells[%d]" % (where, j), problems)
        for j, a in enumerate(step.get("asserts") or []):
            _validate_assert(a, "%s.asserts[%d]" % (where, j), problems, assert_ids)

    if search and search.get("success_assert") not in assert_ids:
        problems.append(
            "seed_search.success_assert %r names no assert id (known: %s)"
            % (search.get("success_assert"), ", ".join(sorted(assert_ids)) or "none"))

    if problems:
        raise ValidationError("%s:\n  - %s" % (path, "\n  - ".join(problems)))
    return True


def _validate_tell(tell, where, problems):
    if tell.get("kind") not in TELL_KINDS:
        problems.append("%s.kind must be one of %s" % (where, ", ".join(sorted(TELL_KINDS))))
    if tell.get("region") not in evidence_regions():
        problems.append("%s.region %r is not an evidence.py region (%s)"
                        % (where, tell.get("region"), ", ".join(sorted(evidence_regions()))))
    if not tell.get("id"):
        problems.append("%s.id is required" % where)
    if not tell.get("expect"):
        problems.append(
            "%s.expect is required -- the on-screen element must be NAMED IN ADVANCE, so the "
            "reviewer checks a stated claim instead of narrating whatever the picture holds"
            % where)


def _validate_assert(a, where, problems, assert_ids):
    aid = a.get("id")
    if not aid:
        problems.append("%s.id is required" % where)
    elif aid in assert_ids:
        problems.append("%s.id %r is duplicated" % (where, aid))
    else:
        assert_ids.add(aid)
    kind = a.get("kind")
    if kind not in ASSERT_KINDS:
        problems.append("%s.kind %r is not one of %s" % (where, kind, ", ".join(sorted(ASSERT_KINDS))))
        return
    if a.get("op") and a["op"] not in OPS:
        problems.append("%s.op %r is not one of %s" % (where, a["op"], ", ".join(sorted(OPS))))
    if kind == "tile_aura":
        tile = a.get("tile")
        if tile != "any" and not (isinstance(tile, dict) and "x" in tile and "y" in tile):
            problems.append('%s.tile needs {x, y} (x is the ROW/DEPTH axis, y is lateral) or the '
                            'literal "any" for a RANDOM_TILE effect' % where)
        if not a.get("status"):
            problems.append('%s.status is required (a status id, or a LIST of ids when the '
                            'effect draws one of several)' % where)
    elif kind in ("status_present", "status_absent", "status_duration"):
        if not a.get("status"):
            problems.append("%s.status is required" % where)
        if not a.get("selector"):
            problems.append("%s.selector is required" % where)
    elif kind == "thing_custom_data":
        for f in ("selector", "thing_config", "key"):
            if not a.get(f):
                problems.append("%s.%s is required" % (where, f))
    elif kind == "custom_data":
        for f in ("selector", "key"):
            if not a.get(f):
                problems.append("%s.%s is required" % (where, f))
    elif kind == "stat":
        if not a.get("stat"):
            problems.append("%s.stat is required" % where)
    elif not a.get("selector"):
        problems.append("%s.selector is required" % where)


# --------------------------------------------------------------------------------------- state

NEGATIVE_OPS = {"ne", "not_contains"}


def absence_satisfies(op):
    """Does "the value is not there at all" satisfy this operator?

    For a NEGATIVE assert it does, and treating absence as a flat FAIL got a correct result
    scored as a broken one. Measured 2026-08-26, iso-gary-refusal: the whole point of that
    scenario is that Gary's ball must REFUSE an ineligible target, and its `ball_still_empty`
    assert is `CF_POKE_CONFIG ne JELLY_ACID_01`. The refusal worked, so the ball stayed empty, so
    it never appeared in the filtered things[] -- and the assert reported FAIL for exactly the
    state it was written to demand. A positive assert (`eq`, `contains`, `gte`, ...) still fails
    on absence: nothing is not something.
    """
    return (op or "eq") in NEGATIVE_OPS


def actors(snapshot):
    """Non-tile combatants. Venue TILE entities share CombatState.Entities with actors and show up
    in combatants[] with isTile=true and no CharacterComponent -- iterating without this filter
    reads a tile as a combatant."""
    combatants = ((snapshot.get("combat") or {}).get("combatants"))
    if combatants is None:
        return None            # UNREADABLE, not empty. See the null-is-not-empty rule.
    return [c for c in combatants if not c.get("isTile")]


def living(combatants):
    """The ones a manifest means when it says `enemy:0`.

    A driven fixture walks into an encounter that ALREADY has monsters, and the harness's own
    spawns pile on top; every corpse stays in CombatState.Entities with hp 0 for the rest of the
    fight. Indexing the raw list therefore aimed `enemy:0` at whatever died FIRST -- measured in
    iso-ash-commands, where the ability fired at a corpse and `hp_delta` read `0 -> 0` and failed
    a step whose skill was fine. `alive` is only trusted when it is an actual bool; an UNREADABLE
    (null) alive is never used to hide a combatant.
    """
    return [c for c in combatants if c.get("alive") is not False]


class Unreadable(Exception):
    """The oracle is blind for this field. Never a PASS and never a FAIL."""


def resolve(snapshot, selector):
    """Selector -> one combatant dict. Raises LookupError if nothing matches, Unreadable if the
    roster itself could not be read.

    GUIDs are deliberately not a selector form: Entity.Guid is Guid.NewGuid() per peer, so it is
    never a valid key for anything that has to mean the same thing twice. `ordinal` is.
    """
    live = actors(snapshot)
    if live is None:
        raise Unreadable("combat.combatants is null -- Crucible could not read the roster")
    if not isinstance(selector, str) or ":" not in selector:
        if selector == "active":
            active = (snapshot.get("combat") or {}).get("activeId")
            for c in live:
                if c.get("id") == active:
                    return c
            raise LookupError("no combatant matches combat.activeId=%r" % active)
        raise LookupError("malformed selector %r" % selector)

    kind, _, arg = selector.partition(":")
    if kind == "class":
        want = arg.strip().lower()
        for c in live:
            if (c.get("classId") or "") == arg:
                return c
        for c in live:
            if want and want in (c.get("name") or "").lower():
                return c
        raise LookupError("no combatant with classId or name matching %r" % arg)
    if kind == "name":
        want = arg.strip().lower()
        for c in live:
            if want in (c.get("name") or "").lower():
                return c
        raise LookupError("no combatant named like %r" % arg)
    if kind in ("ally", "enemy"):
        group = 0 if kind == "ally" else 1
        side = living([c for c in live if c.get("groupIndex") == group])
        idx = int(arg)
        if idx >= len(side):
            raise LookupError("%s:%d -- only %d combatant(s) on group %d"
                              % (kind, idx, len(side), group))
        return side[idx]
    if kind == "ordinal":
        want = int(arg)
        for c in live:
            if c.get("ordinal") == want:
                return c
        raise LookupError("no combatant with roster ordinal %d" % want)
    if kind == "summon":
        # ALLY-side summons only. crucible_combat_spawn marks a spawned ENEMY isSummon=True as
        # well, so an unfiltered `isSummon` made `summon:0` ambiguous the moment a scenario both
        # spawned an enemy and expected a partner: iso-ash-commands resolved summon:0 to the
        # ENEMY jelly and tried to fire the partner's ability on it, and `count(summon:*)` read 2
        # so `partner_present` passed for the wrong reason. Every manifest use of this selector
        # means "the party's summon/partner"; enemy summons are reachable as enemy:N.
        summons = living([c for c in live if c.get("isSummon") and c.get("groupIndex") == 0])
        idx = int(arg)
        if idx >= len(summons):
            raise LookupError("summon:%d -- only %d ally summon(s) present" % (idx, len(summons)))
        return summons[idx]
    raise LookupError("unknown selector kind %r" % kind)


def resolve_count(snapshot, selector):
    """How many combatants match a `count` assert's selector. Accepts the same prefixes, but as
    FILTERS rather than indexes: `enemy:*`, `summon:*`, `class:CF_ORIG_TRAINER`, `name:Sparky`."""
    live = actors(snapshot)
    if live is None:
        raise Unreadable("combat.combatants is null")
    kind, _, arg = (selector or "").partition(":")
    # LIVING only, everywhere. "party_intact >= 4" and "both spawns are still there" are both
    # claims about combatants that are still standing; counting corpses makes them pass by
    # accident. See living().
    if kind == "enemy":
        return len(living([c for c in live if c.get("groupIndex") == 1]))
    if kind == "ally":
        return len(living([c for c in live if c.get("groupIndex") == 0]))
    if kind == "summon":
        # ally-side only -- see resolve_one's note; an enemy spawn is isSummon=True too.
        return len(living([c for c in live if c.get("isSummon") and c.get("groupIndex") == 0]))
    if kind == "class":
        return len(living([c for c in live
                    if c.get("classId") == arg or arg.lower() in (c.get("name") or "").lower()]))
    if kind == "name":
        return len(living([c for c in live if arg.lower() in (c.get("name") or "").lower()]))
    raise LookupError("count selector %r is not a filter form" % selector)


def compare(op, actual, expected):
    if op in (None, "eq"):
        return actual == expected
    if op == "ne":
        return actual != expected
    if op == "contains":
        return expected in (actual or [])
    if op == "not_contains":
        return expected not in (actual or [])
    # Numeric. A None actual is UNREADABLE, handled by the caller before it gets here.
    a, b = float(actual), float(expected)
    return {"lt": a < b, "lte": a <= b, "gt": a > b, "gte": a >= b}[op]


def evaluate_assert(a, snapshot, baseline=None):
    """(status, detail) where status is 'PASS' | 'FAIL' | 'UNREADABLE' | 'ERROR'.

    UNREADABLE is its own outcome on purpose. A null statuses/customData/auraStatuses list means
    the reflective read MISSED, not that the thing is absent -- an assert that folded null into
    "empty" would report PASS while the instrument was blind, which is the same class of bug as
    the log-only false positive this bench exists to stop.
    """
    kind, op, want = a.get("kind"), a.get("op"), a.get("value")
    try:
        if kind == "tile_aura":
            tiles = (snapshot.get("combat") or {}).get("tiles")
            if tiles is None:
                raise Unreadable("combat.tiles is null -- the board could not be read")
            # "any" is not laziness: SKILL_CF_CHAOSMAGE_WILD_MAGIC_SURGE targets RANDOM_TILE, so
            # no coordinate is knowable in advance. The assert is "the hazard landed SOMEWHERE",
            # and the screenshot tell is what pins it to the right decal on the right tile.
            if a["tile"] == "any":
                unreadable = [t for t in tiles if t.get("auraStatuses") is None]
                if unreadable:
                    raise Unreadable(
                        "%d tile(s) report auraStatuses=null. A null VALUE is emitted as [] (a "
                        "clean tile); null here means the MEMBER is absent -- look for "
                        "member_missing: VenueTileComponent.AuraStatuses in warnings. Scanning "
                        "'any' tile while some are blind could report a false absence."
                        % len(unreadable))
                # `status` may be a LIST. SKILL_CF_CHAOSMAGE_WILD_MAGIC_SURGE draws ONE of
                # FIRE/WATER/SHOCK/ACID per proc, so naming a single id would fail a perfectly
                # good surge three times out of four -- and "the hazard landed" is the claim,
                # not "this particular element landed".
                wanted = a["status"] if isinstance(a["status"], list) else [a["status"]]
                hits = [(t["x"], t["y"], sid)
                        for t in tiles for sid in (t.get("auraStatuses") or []) if sid in wanted]
                want_present = (op or "contains") != "not_contains"
                ok = bool(hits) if want_present else not hits
                return ("PASS" if ok else "FAIL"), "%s on tile(s) %s (scanned %d tiles)" % (
                    "/".join(wanted), hits or "none", len(tiles))
            tx, ty = a["tile"]["x"], a["tile"]["y"]
            match = [t for t in tiles if t.get("x") == tx and t.get("y") == ty]
            if not match:
                return "FAIL", "no tile at (x=%s, y=%s)" % (tx, ty)
            auras = match[0].get("auraStatuses")
            if auras is None:
                raise Unreadable(
                    "tiles[(%s,%s)].auraStatuses is null. A null VALUE would have been emitted "
                    "as [] (a clean tile); null here means the MEMBER is absent -- look for "
                    "member_missing: VenueTileComponent.AuraStatuses in warnings" % (tx, ty))
            ok = compare(op or "contains", auras, a["status"])
            return ("PASS" if ok else "FAIL"), "auraStatuses=%s want %s %s" % (
                auras, op or "contains", a["status"])

        if kind == "count":
            n = resolve_count(snapshot, a["selector"])
            return ("PASS" if compare(op or "eq", n, want) else "FAIL"), \
                   "count(%s)=%d want %s %s" % (a["selector"], n, op or "eq", want)

        if kind == "hp_delta":
            # Resolved from the BASELINE and then followed into the after-snapshot BY ID -- never
            # re-resolved against `snapshot`. An index selector is not stable across a step:
            # `enemy:0` means the first LIVING enemy, and the step under test can kill it. Measured
            # 2026-08-26, both halves of that: re-resolving gave `hp 6 -> 18 (delta +12)` off a
            # DIFFERENT rat, and once the fight was thinned to one monster the kill made the
            # top-level resolve raise "enemy:0 -- only 0 combatant(s) on group 1" and the assert
            # ERRORed on a step whose skill had fired perfectly. Guid used only as a same-peer
            # round trip, which AGENT-BRIEF section 8 allows.
            if not baseline:
                return "ERROR", "hp_delta needs a baseline snapshot and none was captured"
            was = resolve(baseline, a["selector"])
            before = was.get("hp")
            same = [x for x in (actors(snapshot) or []) if x.get("id") == was.get("id")]
            if not same:
                return "ERROR", ("%s (%s) is no longer in the roster after the step, so no delta "
                                 "can be computed" % (a["selector"], was.get("id")))
            now = same[0].get("hp")
            if before is None or now is None:
                raise Unreadable("hp is null on one side of the delta")
            delta = now - before
            return ("PASS" if compare(op or "eq", delta, want) else "FAIL"),                    "%s hp %s -> %s (delta %+d) want %s %s" % (
                       was.get("name") or was.get("classId"), before, now, delta, op or "eq", want)

        c = resolve(snapshot, a["selector"])

        if kind in ("hp", "max_hp"):
            key = "hp" if kind == "hp" else "maxHp"
            v = c.get(key)
            if v is None:
                raise Unreadable("%s.%s is null" % (a["selector"], key))
            return ("PASS" if compare(op or "eq", v, want) else "FAIL"), \
                   "%s %s=%s want %s %s" % (c.get("name"), key, v, op or "eq", want)

        if kind == "alive":
            v = c.get("alive")
            if v is None:
                raise Unreadable("%s.alive is null" % a["selector"])
            return ("PASS" if compare("eq", v, want) else "FAIL"), \
                   "%s alive=%s want %s" % (c.get("name"), v, want)

        if kind in ("status_present", "status_absent", "status_duration"):
            statuses = c.get("statuses")
            if statuses is None:
                raise Unreadable("%s.statuses is null -- Crucible could not read them (this is "
                                 "NOT 'has no statuses', which serialises as [])" % a["selector"])
            hits = [s for s in statuses if s.get("id") == a["status"]]
            ids = [s.get("id") for s in statuses]
            if kind == "status_present":
                return ("PASS" if hits else "FAIL"), "%s statuses=%s want %s present" % (
                    c.get("name"), ids, a["status"])
            if kind == "status_absent":
                return ("FAIL" if hits else "PASS"), "%s statuses=%s want %s absent" % (
                    c.get("name"), ids, a["status"])
            if not hits:
                return "FAIL", "%s has no %s to read a duration from (statuses=%s)" % (
                    c.get("name"), a["status"], ids)
            d = hits[0].get("duration")
            if d is None:
                raise Unreadable("%s duration is null" % a["status"])
            return ("PASS" if compare(op or "eq", d, want) else "FAIL"), \
                   "%s %s duration=%s want %s %s" % (c.get("name"), a["status"], d, op or "eq", want)

        if kind == "custom_data":
            data = c.get("customData")
            if data is None:
                raise Unreadable("%s.customData is null" % a["selector"])
            raw = data.get(a["key"])
            if raw is None:
                if absence_satisfies(op):
                    return "PASS", "%s has no customData key %s at all, which satisfies %s %r" % (
                        c.get("name"), a["key"], op, want)
                return "FAIL", "%s has no customData key %s (keys=%s)" % (
                    c.get("name"), a["key"], sorted(data))
            value = _coerce_like(raw, want)
            return ("PASS" if compare(op or "eq", value, want) else "FAIL"), \
                   "%s %s=%r want %s %r" % (c.get("name"), a["key"], raw, op or "eq", want)

        if kind == "thing_custom_data":
            things = c.get("things")
            if things is None:
                raise Unreadable("%s.things is null" % a["selector"])
            match = [t for t in things if t.get("configName") == a["thing_config"]]
            if not match:
                if absence_satisfies(op):
                    return "PASS", (
                        "%s has no custom data on %s at all -- the item is empty, which "
                        "satisfies %s.%s %s %r" % (c.get("name"), a["thing_config"],
                                                  a["thing_config"], a["key"], op, want))
                # things[] is FILTERED to items that carry custom data (CombatReader.ReadThings:
                # "Dumping every item would bury those few under an inventory that never
                # changes"). So an absent entry means "this item has no custom data yet",
                # NOT "the character does not have it" -- Gary really does hold
                # ARM_ORIG_TRAINER_BALL_CAPTURE from his class Things block; the ball is
                # simply empty until a capture writes CF_POKE_CONFIG into it. The old wording
                # ("carries no thing X") sent a reader hunting a fixture bug that was not
                # there, when the real reading is "the PERFECT-gated capture did not land".
                return "FAIL", (
                    "%s has no CUSTOM DATA on %s. things[] only lists items that carry custom "
                    "data, so this means the item is still empty -- not that it is missing from "
                    "the inventory. Items with data: %s" % (
                        c.get("name"), a["thing_config"],
                        [t.get("configName") for t in things]))
            data = match[0].get("customData")
            if data is None:
                raise Unreadable("things[%s].customData is null" % a["thing_config"])
            raw = data.get(a["key"])
            if raw is None:
                if absence_satisfies(op):
                    return "PASS", "thing %s has no key %s at all, which satisfies %s %r" % (
                        a["thing_config"], a["key"], op, want)
                return "FAIL", "thing %s has no key %s (keys=%s)" % (
                    a["thing_config"], a["key"], sorted(data))
            value = _coerce_like(raw, want)
            return ("PASS" if compare(op or "eq", value, want) else "FAIL"), \
                   "%s.%s=%r want %s %r" % (a["thing_config"], a["key"], raw, op or "eq", want)

        if kind == "stat":
            stats = c.get("stats")
            if stats is None:
                raise Unreadable("%s.stats is null" % a["selector"])
            v = stats.get(a["stat"])
            if v is None:
                return "FAIL", "%s has no stat %s (has %s)" % (c.get("name"), a["stat"], sorted(stats))
            return ("PASS" if compare(op or "eq", v, want) else "FAIL"), \
                   ("%s %s=%s want %s %s  [NOTE: stats{} is BASE stats from GetBaseStats -- a "
                    "buff does NOT move these]" % (c.get("name"), a["stat"], v, op or "eq", want))

        return "ERROR", "unhandled assert kind %r" % kind
    except Unreadable as exc:
        return "UNREADABLE", str(exc)
    except (LookupError, KeyError, TypeError, ValueError) as exc:
        return "ERROR", "%s: %s" % (type(exc).__name__, exc)


def _coerce_like(raw, want):
    """customData is Dictionary<string,string>, so "3" has to compare against 3."""
    if isinstance(want, bool):
        return str(raw).strip().lower() in ("1", "true", "yes")
    if isinstance(want, (int, float)):
        try:
            return float(raw)
        except (TypeError, ValueError):
            return raw
    return raw


# ----------------------------------------------------------------------------------------- log

def scan_log(text, expect_proc, forbid=()):
    """Classify the Player.log text written during one step.

    Returns a dict:
      proc_seen      {recipe_id: [matching lines]}   -- the LOG half of the pair
      proc_missing   [recipe_id, ...]
      soft_failures  ["[ClassForge] Recipe effect failed ...", ...]
      hard_failures  [lines carrying an unhandled exception marker]

    A `Recipe effect failed (skipped, rest of plan continues)` line is a PASS for FAIL-SAFETY and
    a FAIL for CORRECTNESS. The two are kept apart here so the report can say both, rather than
    collapsing to one misleading verdict.
    """
    lines = (text or "").splitlines()
    seen, missing = {}, []
    for rid in expect_proc or []:
        hits = [ln for ln in lines if "proc" in ln and rid in ln]
        if hits:
            seen[rid] = hits
        else:
            missing.append(rid)
    soft = [ln for ln in lines if SOFT_FAIL_MARKER in ln]
    hard = [ln for ln in lines if any(m in ln for m in HARD_FAIL_MARKERS)]
    forbidden = [ln for ln in lines if any(f in ln for f in forbid or [])]
    return {
        "proc_seen": seen,
        "proc_missing": missing,
        "soft_failures": soft,
        "hard_failures": hard,
        "forbidden": forbidden,
    }



# ------------------------------------------------------------------------------------- verdicts

VERDICTS = {
    ("yes", "yes"): ("PASS", "log proc + visible on screen"),
    ("yes", "no"): ("FAIL", "INVISIBLE MECHANIC -- it fired in state and the player cannot see it"),
    ("no", "yes"): ("FAIL", "SOMETHING ELSE CAUSED IT -- no proc line; do not credit the skill"),
    ("no", "no"): ("NO-FIRE", "did not fire -- check the gate/seed before calling it broken"),
}


def pair_verdict(log_fired, screen):
    """The whole point of this file. `screen` is 'yes' | 'no' | 'unreviewed'."""
    log = "yes" if log_fired else "no"
    if screen not in ("yes", "no"):
        return "PENDING-EYES", (
            "log proc=%s; the screen half has not been reviewed. A proc line ALONE is not a pass. "
            "Run `bench.py attest <run-dir>`." % log)
    return VERDICTS[(log, screen)]


def step_verdict(step_result, attestation=None):
    """Fold one step's log scan + tell attestations into the PAIR verdict."""
    if not step_result.get("player_facing", True):
        return {"verdict": "ADVANCE", "why": "advance-only step; claims nothing",
                "log_fired": False, "screen": "n/a", "proc_missing": [],
                "fail_safety": "PASS", "correctness_soft_failures": []}
    if step_result.get("setup_failed"):
        # A step whose PRECONDITION failed never fired an ability, so it has neither half of the
        # pair. Folding it into PENDING-EYES made the summary claim "N step(s) have a log proc and
        # no reviewed screen half" about four steps that had no proc and nothing to review -- an
        # instrument overstating its own evidence, which is the one failure mode this file exists
        # to prevent. It is its own verdict, and it is never a pass.
        return {"verdict": "SETUP-FAILED", "why": step_result["setup_failed"],
                "log_fired": False, "screen": "n/a", "proc_missing": [],
                "fail_safety": "PASS", "correctness_soft_failures": []}
    scan = step_result.get("log") or {}
    log_fired = bool(scan.get("proc_seen")) and not scan.get("proc_missing")
    tell_ids = [t["id"] for t in step_result.get("tells", [])]
    verdicts = [(attestation or {}).get(t) for t in tell_ids]
    if not tell_ids:
        screen = "unreviewed"
    elif any(v == "no" for v in verdicts):
        screen = "no"
    elif verdicts and all(v == "yes" for v in verdicts):
        screen = "yes"
    else:
        screen = "unreviewed"
    verdict, why = pair_verdict(log_fired, screen)
    return {
        "verdict": verdict,
        "why": why,
        "log_fired": log_fired,
        "screen": screen,
        "proc_missing": scan.get("proc_missing") or [],
        "fail_safety": "FAIL" if scan.get("hard_failures") else "PASS",
        "correctness_soft_failures": scan.get("soft_failures") or [],
    }


def digest_of(payload):
    """The state digest for the determinism self-check.

    Prefers the digest Crucible computes itself (Redactions.V2 already strips the local GUIDs,
    the episode key and SUMMONED_BY). Falls back to hashing the snapshot with the same members
    removed, so a build that does not return one still yields a comparable sequence.
    """
    if isinstance(payload, dict) and payload.get("digest"):
        return payload["digest"]
    snap = copy.deepcopy((payload or {}).get("snapshot") or payload or {})
    _redact(snap)
    return hashlib.sha256(
        json.dumps(snap, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def _redact(snap):
    """Strip the members that legitimately differ between two peers / two runs.

    Entity GUIDs are Guid.NewGuid() per peer and `episode` is an object-identity key, so leaving
    either in would make the determinism check fail every single time and teach everyone to
    ignore it.
    """
    snap.pop("instance", None)
    snap.pop("warnings", None)
    snap.pop("console", None)
    combat = snap.get("combat") or {}
    combat.pop("episode", None)
    combat.pop("activeId", None)
    for c in (combat.get("combatants") or []):
        c.pop("id", None)
        for s in (c.get("statuses") or []):
            s.pop("originEntityId", None)
        (c.get("customData") or {}).pop("SUMMONED_BY", None)
        for t in (c.get("things") or []):
            t.pop("id", None)
            (t.get("customData") or {}).pop("SUMMONED_BY", None)
    for t in (combat.get("tiles") or []):
        t.pop("occupantId", None)
    return snap


def diff_sequences(a, b, label):
    """First divergence between two recorded sequences, or None."""
    for i in range(max(len(a), len(b))):
        x = a[i] if i < len(a) else "<ended>"
        y = b[i] if i < len(b) else "<ended>"
        if x != y:
            return "%s diverged at index %d: run1=%s run2=%s" % (label, i, x, y)
    return None


# =============================================================================================
# DRIVER LAYER -- talks to the running game through drive.py.
# =============================================================================================

def _drive():
    import drive
    return drive


def _evidence():
    import evidence
    return evidence


def log_offset():
    try:
        return os.path.getsize(PLAYER_LOG)
    except OSError:
        return None


def log_since(offset):
    """New Player.log text since `offset`. None (not '') when the file could not be read at all --
    an empty read and an unreadable log are different findings and only one of them is benign."""
    if offset is None:
        return None
    try:
        with open(PLAYER_LOG, "r", encoding="utf-8", errors="replace") as fh:
            fh.seek(offset)
            return fh.read()
    except OSError:
        return None


def state_payload():
    """The full /state?schema=v2 body -- snapshot AND digest. drive.snapshot() drops the digest."""
    return _drive()._get("/state?schema=v2")


class Bench(object):
    def __init__(self, manifest, run_dir, verbose=True):
        self.m = manifest
        self.run_dir = run_dir
        self.verbose = verbose
        self._combat_entry_offset = None
        self._combat_start_offset = None
        self.report = {
            "schema": "crucible.bench-run.v1",
            "scenario": manifest.get("name"),
            "checklist_item": manifest.get("checklist_item"),
            "started": time.strftime("%Y-%m-%dT%H:%M:%S"),
            "seed": manifest.get("seed"),
            "aborted": None,
            "party_gate": None,
            "pin": None,
            "steps": [],
            "scenario_asserts": [],
            "scenario_tells": [],
            "digests": [],
            "warnings": [],
        }
        os.makedirs(run_dir, exist_ok=True)

    def say(self, msg):
        if self.verbose:
            print(msg)

    # ------------------------------------------------------------------ fixture + party gate

    def stage_fixture(self):
        """Copy the pristine backup over GameRuns\\<run_id>.ftk2. ALWAYS a copy, never in place."""
        drive = _drive()
        fx = self.m["fixture"]
        if fx.get("status") == "not-built":
            return False, (
                "fixture %r is marked NOT BUILT. The manifest carries the recipe in "
                "fixture.notes -- build the bed through CHARACTER CREATION (never "
                "crucible_party_set_class, which leaves stale vanilla names and makes every "
                "screenshot useless for identity), save it to %s, then set status to 'built'."
                % (fx.get("backup"), drive.FIXTURE_BACKUPS))
        src = os.path.join(drive.FIXTURE_BACKUPS, fx["backup"])
        if not os.path.exists(src):
            return False, "fixture backup missing: %s" % src
        dst = drive.fixture_path(fx["run_id"])
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        # Size is a cheap smoke test only. It is NOT the party gate -- see verify_party.
        return True, "copied %s -> %s (%d bytes)" % (src, dst, os.path.getsize(dst))

    def verify_party(self):
        """THE gate. crucible_party_list, matched on DISPLAY NAME.

        fixture_health only checks SIZE and has certified a content-corrupted save healthy for a
        whole session, so nothing downstream may run until the party has been read back and the
        expected class names are actually in it.
        """
        drive = _drive()
        text = drive.run("crucible_party_list")
        party = self.m.get("party") or {}
        expect = party.get("expect_classes") or []
        forbid = party.get("forbid_names", VANILLA_NAMES)
        low = text.lower()
        missing = [n for n in expect if n.lower() not in low]
        present_vanilla = [n for n in forbid if n.lower() in low]
        ok = not missing and not present_vanilla
        detail = {
            "ok": ok,
            "raw": text,
            "missing": missing,
            "vanilla_present": present_vanilla,
        }
        if present_vanilla:
            detail["note"] = (
                "VANILLA class name(s) %s in the party. That is the crucible_party_set_class "
                "signature -- ConfigName rewritten, display name left stale -- and every "
                "screenshot from this fixture would be worthless for identity. Rebuild the "
                "fixture through CHARACTER CREATION." % ", ".join(present_vanilla))
        self.report["party_gate"] = detail
        return ok, detail

    def pin_seed(self):
        seed = self.m.get("seed")
        if seed is None:
            self.report["pin"] = {"pinned": False, "note": "no seed in manifest"}
            return True
        drive = _drive()

        # RETRY until a live GameRandom is actually reachable. Measured 2026-08-26: called
        # straight after load_run returns, pin_seed answers `scopesFound=0 ... no live GameRandom
        # instance reachable`. The run IS present -- load_run waits on run.present -- but
        # AdventureDirector is not resolvable yet, so the pin writes NOTHING and still reports ok.
        # Every "seeded" scenario after that would have been running unpinned.
        text = ""
        for _ in range(20):
            text = drive.run("crucible_pin_seed", [str(seed)])
            if "scopesFound=0" not in text:
                break
            time.sleep(3)

        reachable = "scopesFound=0" not in text
        # `generator=` is emitted only by the FIXED implementation, which replaces the private
        # System.Random. The original wrote the readonly `Seed` label -- a mere RECORD of the seed
        # that the generator never reads again -- and changed zero draws, so a result that does
        # not report a replaced generator is a pin in name only.
        genuinely = reachable and "generator=True" in text
        self.report["pin"] = {"pinned": True, "seed": seed, "raw": text,
                              "reachable": reachable, "generator_replaced": genuinely}
        if not reachable:
            self.report["warnings"].append(
                "crucible_pin_seed never found a live GameRandom (%r). NOTHING was pinned, so "
                "every luck-gated result below is from an unpinned run and will not reproduce."
                % text.strip())
        elif not genuinely:
            self.report["warnings"].append(
                "crucible_pin_seed did not report a replaced generator (%r). The pre-fix build "
                "wrote GameRandom.Seed only -- a readonly RECORD that the RNG never reads again "
                "-- so every 'pinned' result from it is reproducible in name only. Run "
                "`bench.py verify-pin --run-id <guid>` before trusting any seeded verdict here."
                % text.strip())
        return True

    # ------------------------------------------------------------------------------ execution

    def run_setup(self, ops):
        drive = _drive()
        done = []
        for op in ops or []:
            kind = op.get("op")
            if kind == "exec":
                done.append((kind, drive.run(op["command"], op.get("args") or [])))
            elif kind == "godmode":
                done.append((kind, drive.run("crucible_godmode", [op.get("value", "off")])))
            elif kind == "wipe_enemies":
                # `keep_alive` leaves N of the group's living members standing. An isolation
                # scenario that needs a status-capable target must keep a REAL encounter monster:
                # a crucible_combat_spawn body has no StatusEffectComponent and reads statuses=[]
                # forever, which looks exactly like a status that failed to land.
                done.append((kind, drive.run("crucible_combat_wipe_enemies",
                                             [str(op.get("group", 1)),
                                              str(op.get("keep_alive", 0))])))
            elif kind == "end_turn":
                for _ in range(int(op.get("times", 1))):
                    done.append((kind, drive.run("crucible_combat_end_turn")))
            elif kind == "restore_actions":
                done.append((kind, drive.run("crucible_combat_restore_actions")))
            elif kind == "custom_data":
                done.append((kind, self._set_custom_data(op)))
            self.say("    setup %-16s %s" % (kind, str(done[-1][1])[:120] if done else ""))
        return done

    def _set_custom_data(self, op):
        """Put a counter-gated skill 'near' its threshold without waiting for a run to get there.

        There is no dedicated verb for this, so it goes through crucible_set on the resolved
        entity. If the deployed build has no such path this returns the refusal text verbatim
        rather than pretending it worked -- a silent no-op here would make the scenario test a
        gate that was never actually armed.
        """
        drive = _drive()
        snap = (state_payload() or {}).get("snapshot") or {}
        try:
            target = resolve(snap, op["selector"])
        except (LookupError, Unreadable) as exc:
            return "SETUP-FAILED: %s" % exc
        ok, text, why = drive.run_soft(
            "crucible_set_custom_data", [target.get("id"), op["key"], str(op["value"])])
        return text if ok else "SETUP-FAILED: %s" % why

    def spawn_enemies(self):
        drive = _drive()
        out = []
        for e in self.m.get("enemies") or []:
            # run_soft, NOT run. A spawn instantiates a prefab on the main thread and regularly
            # blows the RPC deadline: measured 2026-08-26, `crucible_combat_spawn(JELLY_ACID_01,
            # 2, 1)` came back `main_thread_timeout` while the spawn ITSELF landed. Under strict
            # run() that raised out of execute() as an unhandled CommandError -- a full Python
            # traceback, no report written, no verdict, and a completed fight thrown away. A slow
            # reply is not a failed command; the spawn is asserted from the snapshot below, which
            # is the only assertion the README allows for it anyway.
            ok, text, why = drive.run_soft(
                "crucible_combat_spawn",
                [e["config"], str(e.get("count", 1)), str(e.get("group", 1))])
            if not ok:
                text = "(no reply: %s -- verify from the snapshot below, not from this line)" % why
                time.sleep(5)
            out.append({"config": e["config"], "role": e.get("role"), "result": text})
            self.say("    spawn %-28s %s" % (e["config"], text[:100]))
        # Assert the spawn from STATE, never from a screenshot: the 3D model comes through a
        # separate hand-off, so a spawn can be real in state and missing on the board.
        return out

    def capture_tells(self, tells, prefix, when="after"):
        """Capture each tell's crop under a STABLE name and record what the manifest said should
        be in it. This function does NOT decide whether the tell is satisfied -- it cannot see."""
        ev = _evidence()
        out = []
        tells = [t for t in (tells or []) if t.get("when", "after") == when]
        if not tells:
            return out
        regions = [t["region"] for t in tells]
        img = ev.grab(tuple(set(regions)))
        for t in tells:
            path, size = ev.save(img, "%s_%s" % (prefix, t["id"]), t["region"])
            blank = ev.is_blank(img if t["region"] == "full" else ev.crop_for(img, t["region"]))
            local = os.path.join(self.run_dir, os.path.basename(path))
            try:
                shutil.copy2(path, local)
            except OSError:
                local = path
            out.append({
                "id": t["id"], "kind": t["kind"], "region": t["region"],
                "expect": t["expect"],
                "invalidated_by": t.get("invalidated_by", VANILLA_NAMES if t["kind"] == "identity" else []),
                "path": local, "size": list(size),
                "blank": blank,
                "attested": None,   # filled in by `bench.py attest`
            })
            self.say("      tell[%s] %s%s" % (t["id"], local,
                                              "   <-- BLANK, NOT EVIDENCE" if blank else ""))
        return out

    def fire_step(self, step):
        drive = _drive()
        before_payload = state_payload()
        before = (before_payload or {}).get("snapshot") or {}
        offset = (self._combat_start_offset
                  if step.get("log_since") == "combat_start" else log_offset())
        result = {"id": step["id"], "label": step["label"], "setup_failed": None,
                  "player_facing": step.get("player_facing", True)}

        # An advance-only step moves the fight on (end turns to reach the next round, so a
        # ONCE_PER_ROUND budget re-arms) and claims nothing. It gets no verdict, because a step
        # that asserts nothing must never contribute a PASS to the summary.
        if step.get("advance"):
            for _ in range(int(step["advance"].get("end_turn", 0))):
                drive.run_soft("crucible_combat_end_turn")
                time.sleep(1.2)
            result["advanced"] = step["advance"]

        # `crucible_use_ability` has NO entity parameter -- it always fires as
        # CombatPhase._activeCharacterEntity. So `actor` is an ASSERTION, not a selection.
        if step.get("actor"):
            # ADVANCE TO THE ACTOR FIRST, THEN ASSERT. `crucible_use_ability` has no entity
            # parameter, so a step CANNOT choose who acts -- but the manifest's `advance` steps
            # were guessing how many end-turns it takes to reach the next class, and they are
            # always wrong: initiative interleaves the enemies (and the Trainer's partner summon)
            # between the party, so one end_turn lands on a jelly, not on the Pacifist. Measured
            # 2026-08-26: all four steps reported SETUP-FAILED with activeId set to a DIFFERENT
            # party member each time -- the classes were fine, the turn arithmetic was not.
            # to_combat.make_active() already encodes the fix for the overworld; this is the
            # combat equivalent: end turns until the named actor holds the turn, bounded, and only
            # then apply the assertion (which still refuses to fire as the wrong character).
            # The RETURN VALUE MATTERS. It was discarded, so when the named actor could not be
            # reached inside max_turns the step went on to fire the manifest's ability as
            # WHOEVER held the turn. Measured 2026-08-26 on iso-vampiric-batswarm s3/s4:
            # `error: 'BLADE_BASIC_ATTACK' is not an ability of this character` -- and the step
            # was then scored as a no-proc, i.e. a staging failure reported as a dead skill,
            # which is precisely the confusion SETUP-FAILED exists to prevent.
            if not self.advance_to_actor(step["actor"]):
                result["setup_failed"] = (
                    "actor %s never got the turn within the bound -- nothing was fired, so this "
                    "step is not evidence either way" % step["actor"])
            # A DOWNED ACTOR FIRES NOTHING AND SAYS NOTHING. Measured 2026-08-26: step v1 read
            # `healthBefore: ... Vampiric=0` and `resultCount=0` -- the Vampiric had been beaten
            # unconscious by the enemy AI during the turns spent reaching its initiative slot, so
            # `crucible_use_ability` was a no-op and the step reported "no proc" as if the SKILL
            # were broken. Reaching a fight through a real encounter means real enemies act, and
            # over 4-8 end-turns they will drop a level-3 character. Heal the party back up before
            # measuring: hp_delta baselines are taken per step, immediately after this, so a heal
            # here cannot flatter a damage assert. godmode stays OFF -- this is a one-shot heal,
            # not the per-tick top-up that masks every HP effect.
            if not self._actor_alive(step["actor"]):
                drive.run_soft("crucible_heal_party")
                time.sleep(1.5)
                self.say("      actor was downed; healed the party before measuring")
            before_payload = state_payload()
            before = (before_payload or {}).get("snapshot") or {}
            try:
                want = resolve(before, step["actor"])
                active = (before.get("combat") or {}).get("activeId")
                if want.get("id") != active:
                    result["setup_failed"] = (
                        "actor %s (%s, ordinal %s) is not the ACTIVE entity (activeId=%s). "
                        "crucible_use_ability has no entity parameter and would have fired as "
                        "whoever is up instead -- refusing." % (
                            step["actor"], want.get("name"), want.get("ordinal"), active))
            except (LookupError, Unreadable) as exc:
                result["setup_failed"] = "actor %s: %s" % (step["actor"], exc)

        # ON_TURN_END / ON_TURN_START recipes are not fired by an ability -- ending the turn IS
        # the player action. Modelled explicitly so the step still carries a proc expectation and
        # a pair of tells, rather than being smuggled in as an advance step that claims nothing.
        if not result["setup_failed"] and step.get("action") == "end_turn":
            result["fire"] = drive.run("crucible_combat_end_turn")
            self.say("    end turn                         %s" % str(result["fire"])[:80])

        # CAPTURE THE IDENTITY TELL WHILE THE ACTOR STILL HOLDS THE TURN. The tells used to be
        # taken only AFTER the fire plus a 3s settle, by which time the turn has passed to the
        # next combatant -- so a step that genuinely fired as the Pacifist filed an "identity"
        # crop whose active-character banner read "Vampiric" (measured 2026-08-26). The party
        # cards are all still in frame, so the crop was not WRONG, but the one element that says
        # WHO ACTED had already moved on. A tell with "when": "before" is taken here instead.
        pre_tells = self.capture_tells(step.get("tells"), "%s_%s_pre" % (self.m["name"], step["id"]),
                                       when="before") if not result["setup_failed"] else []

        if not result["setup_failed"] and step.get("ability"):
            tx, ty = self._resolve_target(before, step.get("target"))
            if tx is None:
                result["setup_failed"] = ty      # ty carries the reason in the failure case
            else:
                result["fire"] = drive.run("crucible_use_ability", [step["ability"], str(tx), str(ty)])
                result["target_tile"] = {"x": tx, "y": ty}
                self.say("    fire  %-30s -> (%s,%s)  %s"
                         % (step["ability"], tx, ty, str(result.get("fire"))[:90]))

        # _performAiDecision returns a Task that is NOT awaited, so an immediate read races the
        # continuation. Settle before measuring, or a real effect reads as "did not fire".
        time.sleep(float(step.get("settle_seconds", 3.0)))

        # ...and 3s is not always enough. MEASURED 2026-08-26 on iso-ash-commands s2: the partner's
        # JELLY_ACID_ATTACK landed and `proc SKILL_CF_PARTNER_CMD_FOCUS_FIRE (PartnerFocusFire)`
        # was written to Player.log -- but AFTER the settle, so the scan reported the proc MISSING
        # and the state read caught the target's HP mid-flight. A slow attack animation was being
        # reported as a dead skill. So: if an expected proc has not appeared yet, keep polling the
        # SAME log offset until it does or the deadline passes, and only then read state. This can
        # only convert a false negative into a true reading -- a proc that never appears still
        # reads as missing, and nothing here invents one.
        want_procs = (step.get("log") or {}).get("proc") or []
        if want_procs:
            deadline = time.time() + float(step.get("proc_wait_seconds", 12.0))
            while time.time() < deadline:
                probe = log_since(offset)
                if probe is None:
                    break
                if not scan_log(probe, want_procs, None)["proc_missing"]:
                    time.sleep(1.0)      # let the effect that followed the proc land too
                    break
                time.sleep(1.0)

        after_payload = state_payload()
        after = (after_payload or {}).get("snapshot") or {}
        text = log_since(offset)
        result["log_readable"] = text is not None
        result["log"] = scan_log(text or "", (step.get("log") or {}).get("proc"),
                                 (step.get("log") or {}).get("forbid"))
        result["asserts"] = [
            dict(a, **dict(zip(("status", "detail"), evaluate_assert(a, after, before))))
            for a in (step.get("asserts") or [])
        ]
        result["tells"] = pre_tells + self.capture_tells(
            step.get("tells"), "%s_%s" % (self.m["name"], step["id"]))
        result["digest"] = digest_of(after_payload)
        result["warnings"] = after.get("warnings") or []
        # PLAYABILITY GATE. Scored separately from the skill's own verdict: a step can proc
        # correctly and still wedge the game, and that is the failure that actually matters.
        result["ui_alive"] = self.check_ui_alive()
        if not result["ui_alive"]["ok"]:
            self.say("      !! %s" % result["ui_alive"]["reason"])
        return result

    # --------------------------------------------------------------- UI liveness / freeze gate

    def check_ui_alive(self, sample_seconds=2.0, storm_bytes=50000):
        """Is the game still PLAYABLE? Returns {"ok", "reason", "log_bytes_per_s", "buttons"}.

        WHY THIS IS A FIRST-CLASS CRITERION, not a nicety. The governing acceptance criterion for
        this mod is "classes playable, game does not break". A skill can proc, land its status in
        state, satisfy every assert -- and still leave the player staring at a combat that never
        gives anyone a turn. Measured 2026-08-26 on batch-4class: STATUS_CHARGE_CF_FOCUS_FIRE
        landed on all four party members, every state assert PASSED, and the fight then FROZE.
        Player.log was growing at ~94 KB/s, 60 NullReferenceExceptions per second, all of them
        CombatTimelineViewHelper2._refreshPortraitVisuals -> g___addStatusIcon, which dereferences
        `statusRecord.IconTexture` UNGUARDED on a status that has no dStatusEffect record. The
        throw escapes the scheduled _progressRound callback, so the round never finishes
        progressing, the active character never gets an action menu, and combat is unplayable.
        Nothing in the assert/proc/screenshot surface caught that on its own.

        Two independent tells, because either alone misreads:
          * EXCEPTION STORM -- Player.log growth rate. A wedged UI callback re-throws every frame,
            which is enormous and unmistakable; normal play writes a trickle.
          * NO DRIVEABLE UI -- route is COMBAT with an active entity, and yet the UI has no
            buttons at all. That is the visible half of the same freeze.
        """
        drive = _drive()
        out = {"ok": True, "reason": "combat UI is responsive", "log_bytes_per_s": None,
               "buttons": None, "storm_frames": None}
        before = log_offset()
        time.sleep(sample_seconds)
        after = log_offset()
        if before is not None and after is not None:
            grew = max(0, after - before)
            out["log_bytes_per_s"] = int(grew / sample_seconds)
            if grew > storm_bytes:
                tail = log_since(before) or ""
                out["storm_frames"] = tail.count("NullReferenceException")
                if out["storm_frames"] > 10:
                    top = ""
                    for line in tail.splitlines():
                        if line.strip().startswith("at ") and "UnityEngine" not in line:
                            top = line.strip()[:200]
                            break
                    out["ok"] = False
                    out["reason"] = (
                        "UI-FROZEN: exception storm -- Player.log grew %d bytes in %.0fs with %d "
                        "NullReferenceException frames. Top non-Unity frame: %s"
                        % (grew, sample_seconds, out["storm_frames"], top or "(none captured)"))
                    return out

        snap = (state_payload() or {}).get("snapshot") or {}
        combat = snap.get("combat") or {}
        if combat.get("active") and combat.get("activeId"):
            dump = drive.run("crucible_ui_dump", ["-", "button"], strict=False) or ""
            count = dump.count("name='")
            out["buttons"] = count
            if count == 0:
                out["ok"] = False
                out["reason"] = ("UI-FROZEN: combat is live with an active entity (%s) and the UI "
                                 "has NO buttons -- nothing is driveable and no character can be "
                                 "given an order." % combat.get("activeId"))
        return out

    def _resolve_target(self, snap, target):
        if not target:
            return None, "step has an ability but no target"
        if "tile" in target:
            return target["tile"]["x"], target["tile"]["y"]
        try:
            c = resolve(snap, target["select"])
        except (LookupError, Unreadable) as exc:
            return None, "target %s: %s" % (target.get("select"), exc)
        tile = c.get("tile")
        if not tile:
            return None, ("target %s (%s) has no tile -- it is not on the board"
                          % (target["select"], c.get("name")))
        return tile["x"], tile["y"]

    # ---------------------------------------------------------------------------------- run it




    def _actor_alive(self, selector):
        snap = (state_payload() or {}).get("snapshot") or {}
        try:
            who = resolve(snap, selector)
        except (LookupError, Unreadable):
            return True          # unresolvable is the actor gate's problem, not this one
        hp = who.get("hp")
        return bool(who.get("alive")) and (hp is None or hp > 0)

    def advance_to_actor(self, selector, max_turns=16, settle=1.5):
        """End combat turns until `selector` holds the turn. Returns whether it got there.

        `activeId` is also None for a beat right after the fight opens (the phase has not chosen a
        starter yet), so this waits that out before it starts spending turns -- otherwise step 1
        fails on a null active entity that would have resolved on its own in two seconds.
        """
        drive = _drive()
        for attempt in range(max_turns):
            snap = (state_payload() or {}).get("snapshot") or {}
            active = (snap.get("combat") or {}).get("activeId")
            if active is None:
                time.sleep(2.0)
                snap = (state_payload() or {}).get("snapshot") or {}
                active = (snap.get("combat") or {}).get("activeId")
            try:
                want = resolve(snap, selector)
            except (LookupError, Unreadable):
                return False
            if want.get("id") == active:
                if attempt:
                    self.say("      advanced %d turn(s) to reach %s" % (attempt, selector))
                return True
            drive.run_soft("crucible_combat_end_turn")
            time.sleep(settle)
        self.say("      could not reach %s in %d turns" % (selector, max_turns))
        return False

    def enter_combat_with_whole_party(self):
        """Gather the party on its OWN hex, spawn a fight onto it, press Fight.

        WHY NOT drive.enter_combat() FIRST. That verb WALKS to a roaming encounter, and only the
        ACTIVE character walks. Measured 2026-08-26 on the batch-4class bed: the party started
        split (lead at (59,30), the other three at (38,37)), the lead was ambushed alone, and the
        fight had ONE ally in it. Every four-class step then reported
        `SETUP-FAILED: no combatant with classId CF_ORIG_PACIFIST` -- which reads exactly like a
        class bug and is a staging bug. A batch scenario whose whole point is four classes in one
        fight must never enter combat by a route that can leave three of them on the map.

        Teleporting the party onto the DESTINATION before walking does fix the roster and breaks
        the entry instead: with nobody left to ambush, six consecutive encounters reported "Fight
        pressed and accepted, no combat within 90s".

        THE ROUTE USED NOW is drive.enter_combat_whole_party(): stage the whole party ONE hex
        from a roaming encounter, then make each character active in turn with
        crucible_overworld_end_turn and walk EACH of them onto the encounter hex with a real
        crucible_move, so the encounter captures four genuine arrivals. Then press Fight.
        Measured 2026-08-26: 4/4 group=0 combatants, CF_ORIG_VAMPIRIC / _PACIFIST / _TRAINER /
        _GARY. drive.enter_combat() is NOT used -- it walks the active character alone.
        """
        drive = _drive()
        # STRAIGHT TO THE WHOLE-PARTY WALK-IN. The old preamble gathered the party with
        # crucible_party_set_hex and then `crucible_debug_spawn enemies <config>` to raise the
        # spawn's own Fight menu. Two measured problems on this bed, 2026-08-26:
        #   * the spawn lands wherever it lands, and when it lands out of reach NO menu appears --
        #     invoked=True, then 120s with no Fight button anywhere and the nearest roaming
        #     encounter 22 hexes away;
        #   * the spawned body joined GROUP 0, so the fight had a jelly ALLY and every
        #     `count(ally:*)` assert read 5 instead of 4.
        # The walk-in below needs neither, and the manifest's own `enemies` block spawns the real
        # opposition once the fight is live.
        self.say("  walking the WHOLE party onto an encounter")
        outcome = drive.enter_combat_whole_party()
        self.say("  enter_combat_whole_party ok=%s: %s" % (outcome["ok"], outcome["reason"]))
        if drive.in_combat() and outcome.get("missing"):
            self.report["party_incomplete_in_fight"] = outcome["missing"]
        return drive.in_combat()

    def execute(self):
        drive = _drive()
        m = self.m

        ok, detail = self.stage_fixture()
        self.say("  fixture: %s" % detail)
        if not ok:
            return self._abort("fixture could not be staged: %s" % detail)

        if not drive.boot(timeout_seconds=240):
            return self._abort("the game's RPC pump never came alive -- is the game running?")
        drive.run_soft("crucible_input_focus_gate", ["on"])
        drive.run_soft("crucible_tutorials_suppress", ["on"])

        self.say("  loading run %s" % m["fixture"]["run_id"])
        load_text = drive.load_run(m["fixture"]["run_id"])

        # A live venue from the PREVIOUS scenario is a dirty screen, and load_run refuses it --
        # correctly, because _loadGameRun silently does nothing against a stale overlay. That
        # made every multi-scenario invocation (`run --all`, and every seed-search past trial 1)
        # abort on its second scenario, which is exactly the two workflows this bench exists
        # for. The documented cure for a dirty screen IS a restart, so take it here instead of
        # making the caller do it by hand.
        if "dirty screen" in load_text:
            self.say("  dirty screen from the previous scenario -- restarting the game")
            if not drive.restart_game(timeout_seconds=300):
                return self._abort("restart after a dirty screen never brought the game back")
            drive.run_soft("crucible_input_focus_gate", ["on"])
            drive.run_soft("crucible_tutorials_suppress", ["on"])
            load_text = drive.load_run(m["fixture"]["run_id"])

        self.report["load"] = load_text
        if "FAILED" in load_text:
            return self._abort("load failed:\n%s" % load_text)

        ok, detail = self.verify_party()
        self.say("  party gate: %s" % ("OK" if ok else "FAILED -- " + json.dumps(detail)[:400]))
        if not ok:
            return self._abort(
                "PARTY GATE FAILED. Nothing downstream is evidence about our classes, so the "
                "run stops here rather than filing screenshots nobody can use. %s"
                % detail.get("note", "missing=%s" % detail.get("missing")))

        self.pin_seed()

        self.run_setup([o for o in (m.get("setup") or []) if o.get("phase") == "overworld"])

        self._combat_entry_offset = log_offset()
        if not drive.in_combat():
            self.say("  entering combat")
            self.enter_combat_with_whole_party()
        if not drive.in_combat():
            return self._abort("could not reach a live fight")
        # WHO IS ACTUALLY IN THE FIGHT. A batch scenario that entered combat with three of its
        # four classes left on the map reports SETUP-FAILED per step with a different member
        # active each time, which reads exactly like three class bugs. Record the roster here so
        # that failure can never again be scored against the classes.
        missing = drive._party_missing_from_fight()
        self.report["party_in_fight"] = {"missing": missing}
        self.say("  party in fight: %s" % ("ALL PRESENT" if not missing
                                           else "MISSING " + ", ".join(missing)))
        # Top the party up ONCE on entry. Combat is reached through a real encounter, so the party
        # arrives however the overworld left it -- and the run that produced this fixture had
        # already lost the Vampiric to a solo ambush. A party that starts a scenario at low HP
        # gets wiped a few turns in and the fight ENDS mid-battery, which surfaces as
        # `combat.combatants is null` on every later step: an oracle going blind, reported as
        # three class failures. One heal, godmode still OFF, before any measurement is taken.
        drive.run_soft("crucible_heal_party")
        time.sleep(1.5)

        # Anything that fires at ON_COMBAT_START has already been written to the log by the time
        # the first step begins, so a step that OBSERVES the combat opening needs the offset from
        # before the fight, not from its own start. Without this a partner summon is invisible to
        # the log half and would read as "did not fire".
        self._combat_start_offset = self._combat_entry_offset
        self.run_setup([o for o in (m.get("setup") or []) if o.get("phase") != "overworld"])
        self.report["spawns"] = self.spawn_enemies()

        for step in m.get("steps") or []:
            self.say("    step %s: %s" % (step["id"], step["label"]))
            self.report["steps"].append(self.fire_step(step))
            self.report["digests"].append(self.report["steps"][-1]["digest"])

        payload = state_payload()
        final = (payload or {}).get("snapshot") or {}
        self.report["scenario_asserts"] = [
            dict(a, **dict(zip(("status", "detail"), evaluate_assert(a, final))))
            for a in (m.get("asserts") or [])
        ]
        self.report["scenario_tells"] = self.capture_tells(m.get("tells"), m["name"] + "_final")
        self.report["final_warnings"] = final.get("warnings") or []
        self.report["ui_alive_final"] = self.check_ui_alive()
        self.say("  playable at the end: %s" % self.report["ui_alive_final"]["reason"])
        self.report["finished"] = time.strftime("%Y-%m-%dT%H:%M:%S")
        return self.report

    def _abort(self, why):
        self.report["aborted"] = why
        self.report["finished"] = time.strftime("%Y-%m-%dT%H:%M:%S")
        return self.report


# =============================================================================================
# REPORTING
# =============================================================================================

def render(report, attestations=None):
    lines = []
    add = lines.append
    add("=" * 92)
    add("SCENARIO  %s      closes: %s" % (report.get("scenario"), report.get("checklist_item")))
    add("seed      %s" % report.get("seed"))
    pin = report.get("pin") or {}
    if pin.get("pinned"):
        add("pin       generator_replaced=%s  %s" % (
            pin.get("generator_replaced"), (pin.get("raw") or "").strip()[:150]))
    add("=" * 92)

    if report.get("aborted"):
        add("ABORTED: %s" % report["aborted"])
        gate = report.get("party_gate") or {}
        if gate.get("raw"):
            add("  crucible_party_list said:")
            for ln in gate["raw"].splitlines()[:12]:
                add("    " + ln)
        add("")
        add("VERDICT: ABORTED -- nothing here is evidence.")
        return "\n".join(lines)

    counts = {}
    for step in report.get("steps") or []:
        v = step_verdict(step, (attestations or {}).get(step["id"]))
        if v["verdict"] != "ADVANCE":
            counts[v["verdict"]] = counts.get(v["verdict"], 0) + 1
        add("")
        add("STEP %-8s %s" % (step["id"], step["label"]))
        if v["verdict"] == "ADVANCE":
            add("   (advance only: %s)" % step.get("advanced"))
            continue
        if step.get("setup_failed"):
            add("   SETUP-FAILED: %s" % step["setup_failed"])
            continue
        add("   log proc   : %s   %s" % (
            "YES" if v["log_fired"] else "NO",
            ("missing %s" % ", ".join(v["proc_missing"])) if v["proc_missing"] else
            ", ".join((step.get("log") or {}).get("proc_seen", {}))))
        add("   on screen  : %s" % v["screen"].upper())
        add("   >> VERDICT : %-13s %s" % (v["verdict"], v["why"]))
        add("   fail-safety: %s%s" % (
            v["fail_safety"],
            "  (unhandled exception in the log -- this is the criterion that actually matters)"
            if v["fail_safety"] == "FAIL" else ""))
        for ln in v["correctness_soft_failures"]:
            add("   correctness: FAIL (fail-safety still PASS) -- %s" % ln.strip()[:140])
        for ln in (step.get("log") or {}).get("forbidden", []):
            add("   FORBIDDEN LINE (the manifest said this must not appear): %s" % ln.strip()[:140])
        for ln in (step.get("log") or {}).get("hard_failures", []):
            add("   UNHANDLED EXCEPTION: %s" % ln.strip()[:160])
        for a in step.get("asserts") or []:
            add("   assert %-9s %-11s %s" % (a.get("id"), a.get("status"), a.get("detail")))
        for t in step.get("tells") or []:
            mark = "  <-- BLANK, NOT EVIDENCE" if t.get("blank") else ""
            add("   tell   %-9s [%s] expect: %s" % (t["id"], t["kind"], t["expect"]))
            add("            %s%s" % (t["path"], mark))
        if not step.get("log_readable"):
            add("   NOTE: Player.log could not be read -- the log half is UNKNOWN, not 'no'.")

    if report.get("scenario_asserts"):
        add("")
        add("SCENARIO ASSERTS")
        for a in report["scenario_asserts"]:
            add("   %-22s %-11s %s" % (a.get("id"), a.get("status"), a.get("detail")))
    if report.get("scenario_tells"):
        add("")
        add("SCENARIO TELLS (>=2, >=1 identity)")
        for t in report["scenario_tells"]:
            add("   %-16s [%s] expect: %s" % (t["id"], t["kind"], t["expect"]))
            add("        %s%s" % (t["path"], "  <-- BLANK, NOT EVIDENCE" if t.get("blank") else ""))

    warn = [w for w in (report.get("final_warnings") or []) if "GameRunData." not in str(w)]
    if warn:
        add("")
        add("STATE WARNINGS (a member_missing here means an assert on that field is blind)")
        for w in warn[:20]:
            add("   %s" % w)
    for w in report.get("warnings") or []:
        add("")
        add("!! %s" % w)

    add("")
    add("-" * 92)
    add("VERDICT SUMMARY  " + ("  ".join("%s=%d" % kv for kv in sorted(counts.items())) or "no steps"))
    if counts.get("PENDING-EYES"):
        add("PENDING-EYES is NOT a pass. %d step(s) have a log proc and no reviewed screen half."
            % counts["PENDING-EYES"])
        add("Open the crops above, then: python bench.py attest %s" % report.get("run_dir", "<run-dir>"))
    return "\n".join(lines)


# =============================================================================================
# CLI
# =============================================================================================

def load_manifest(name):
    path = name if os.path.exists(name) else os.path.join(SCENARIO_DIR, name + ".json")
    with open(path, "r", encoding="utf-8") as fh:
        m = json.load(fh)
    validate(m, path)
    return m, path


def all_manifests():
    return sorted(glob.glob(os.path.join(SCENARIO_DIR, "*.json")))


def new_run_dir(name):
    d = os.path.join(RUN_ROOT, "%s_%s" % (name, time.strftime("%Y%m%d_%H%M%S")))
    os.makedirs(d, exist_ok=True)
    return d


def cmd_list(_args):
    for path in all_manifests():
        try:
            m = json.load(open(path, "r", encoding="utf-8"))
            validate(m, path)
            flag = "ok  "
        except ValidationError:
            flag = "BAD "
        except (ValueError, OSError):
            flag = "ERR "
        fx = (m.get("fixture") or {}).get("status", "?")
        print("%s %-26s %-10s fixture=%-10s seed=%-11s %s"
              % (flag, m.get("name", "?"), m.get("kind", "?"), fx, m.get("seed"),
                 m.get("checklist_item", "")))
    return 0


def cmd_validate(args):
    bad = 0
    paths = [os.path.join(SCENARIO_DIR, n + ".json") if not os.path.exists(n) else n
             for n in (args.names or [])] or all_manifests()
    for path in paths:
        try:
            validate(json.load(open(path, "r", encoding="utf-8")), path)
            print("ok   %s" % os.path.basename(path))
        except (ValidationError, ValueError, OSError) as exc:
            bad += 1
            print("BAD  %s" % exc)
    print("\n%d/%d valid" % (len(paths) - bad, len(paths)))
    return 1 if bad else 0


def cmd_run(args):
    names = args.names
    if args.all:
        names = [os.path.splitext(os.path.basename(p))[0] for p in all_manifests()]
    if not names:
        print("nothing to run: give a scenario name or --all")
        return 2

    worst = 0
    for name in names:
        try:
            m, path = load_manifest(name)
        except (ValidationError, ValueError, OSError) as exc:
            print("BAD MANIFEST %s" % exc)
            worst = max(worst, 2)
            continue

        if args.twice:
            worst = max(worst, run_twice(m, args))
            continue

        run_dir = new_run_dir(m["name"])
        # A crash in a driven step must still produce a report. Before this, one
        # `main_thread_timeout` out of drive.run() ended the process with a traceback and the
        # whole fight -- load, party gate, combat entry, several minutes of it -- was discarded
        # with no artifact at all.
        bench_obj = Bench(m, run_dir)
        try:
            report = bench_obj.execute()
        except Exception as exc:                                        # noqa: BLE001
            import traceback as _tb
            report = bench_obj.report
            report["aborted"] = ("the runner raised %s: %s" % (type(exc).__name__, exc))
            report["traceback"] = _tb.format_exc()
            print(report["traceback"])
        report["run_dir"] = run_dir
        write_report(report, run_dir)
        print(render(report, load_attestations(run_dir)))
        print("\nrun dir: %s" % run_dir)
        if report.get("aborted"):
            worst = max(worst, 2)
        else:
            vs = [step_verdict(s, {}).get("verdict") for s in report.get("steps") or []]
            if "FAIL" in vs:
                worst = max(worst, 1)
    return worst


def run_twice(m, args):
    """The determinism self-check. Same fixture, same PINNED seed, twice; diff the digest
    sequence. Divergence between two runs of identical input is precisely the class of bug that
    desyncs peers, and it is catchable on ONE machine.

    NOTE: v2 digests MOVED when the board fields (tile/ordinal/isSummon/customData/tiles[])
    landed, so any stored baseline from before that change is stale. This compares two FRESH
    runs against each other, never against a recorded baseline, for exactly that reason.
    """
    if m.get("seed") is None:
        print("--twice needs a pinned seed; %s has none" % m["name"])
        return 2
    reports, dirs = [], []
    for i in (1, 2):
        d = new_run_dir("%s_twice%d" % (m["name"], i))
        print("\n===== determinism pass %d/2 =====" % i)
        r = Bench(m, d).execute()
        r["run_dir"] = d
        write_report(r, d)
        reports.append(r)
        dirs.append(d)
        if r.get("aborted"):
            print(render(r))
            return 2

    a, b = reports
    print(render(b, load_attestations(dirs[1])))
    print("\n" + "=" * 92)
    print("DETERMINISM SELF-CHECK -- same fixture, seed=%s, two runs" % m["seed"])
    pin_ok = (a.get("pin") or {}).get("generator_replaced")
    if not pin_ok:
        print("!! The pin did not report a replaced generator. Two identical digest sequences")
        print("!! here would prove NOTHING about seeding -- they would only show the scenario is")
        print("!! insensitive to the RNG. Fix pinning first (`bench.py verify-pin`).")
    div = diff_sequences(a["digests"], b["digests"], "state digest sequence")
    if div:
        print("DIVERGED: %s" % div)
        print("Two runs of identical input produced different state. That is the same class of")
        print("bug that desyncs peers -- process-local state, dictionary ordering, unstable")
        print("sorts, per-peer branches. CombatState is [JsonIgnore] on GameRunData, so the")
        print("game's own desync MD5 would never have seen it.")
        return 1
    print("IDENTICAL: %d digest(s) matched across both runs." % len(a["digests"]))
    print("  run1: %s" % (dirs[0]))
    print("  run2: %s" % (dirs[1]))
    return 0


def cmd_seed_search(args):
    """Search once, pin forever.

    A luck-gated outcome -- Gary's PERFECT-roll refusal, the Chaos Mage's 15% surge -- must not
    be waited for and must not be reached by a test-only bypass that changes the code path.
    Instead: run the real scenario across candidate seeds until the outcome happens, then write
    that seed into the manifest, where it reproduces on demand forever.
    """
    m, path = load_manifest(args.name)
    search = m.get("seed_search")
    if not search:
        print("%s has no seed_search block" % m["name"])
        return 2
    want = search["success_assert"]
    cands = search["candidates"]
    seeds = cands.get("list") or list(range(int(cands["start"]),
                                            int(cands["start"]) + int(cands["count"])))
    if args.limit:
        seeds = seeds[:args.limit]

    print("seed-searching %s for assert %r across %d candidate seed(s)" % (m["name"], want, len(seeds)))
    for n, seed in enumerate(seeds, 1):
        trial = copy.deepcopy(m)
        trial["seed"] = seed
        d = new_run_dir("%s_seed%d" % (m["name"], seed))
        report = Bench(trial, d, verbose=False).execute()
        report["run_dir"] = d
        write_report(report, d)
        if report.get("aborted"):
            print("  seed %-8d ABORTED: %s" % (seed, report["aborted"][:120]))
            return 2

        # REFUSE TO SEARCH ON A PIN THAT DOES NOTHING.
        #
        # A seed search whose seed does not actually drive the RNG is worse than no search: it
        # eventually "finds" a seed by pure chance, that seed gets written into the manifest as a
        # pinned reproduction, and the scenario then passes or fails at random forever while
        # claiming to be deterministic. Checked on the FIRST trial so this costs one load, not
        # four hundred.
        pin = report.get("pin") or {}
        if n == 1 and not pin.get("generator_replaced"):
            print("\n  REFUSING TO SEARCH. The pin did not replace the live generator "
                  "(reachable=%s):\n    %s"
                  % (pin.get("reachable"), (pin.get("raw") or "").strip()))
            print("  Any seed found this way would be a coincidence recorded as a reproduction.")
            print("  Fix pinning first: python verify_pin.py --run-id %s"
                  % (m.get("fixture") or {}).get("run_id"))
            return 2
        hits = [a for s in report["steps"] for a in s["asserts"] if a.get("id") == want]
        hits += [a for a in report["scenario_asserts"] if a.get("id") == want]
        status = hits[0]["status"] if hits else "MISSING"
        print("  [%3d/%3d] seed %-8d %s  %s" % (n, len(seeds), seed, status,
                                                (hits[0]["detail"] if hits else "")[:90]))
        if status == "PASS":
            print("\nFOUND. Write this into %s:\n    \"seed\": %d" % (path, seed))
            print("run dir: %s" % d)
            print("\nThe outcome now reproduces on demand. Re-run `bench.py run %s` and it will"
                  % m["name"])
            print("hit every time -- no waiting on the roll, and no test-only bypass that would")
            print("have changed the code path under test.")
            return 0
        if status == "UNREADABLE":
            print("  the oracle is blind for this assert; searching further would be meaningless")
            return 2
    print("\nNOT FOUND in %d seeds. Widen seed_search.candidates." % len(seeds))
    return 1


def cmd_verify_pin(args):
    """Delegate to verify_pin.py -- the single implementation of the seed check.

    There is deliberately only one, because the whole point of this check is that it is the thing
    standing between "the plugin said generator=True" and a trustworthy seeded verdict. Two
    half-implementations of a trust boundary is how you end up trusting the weaker one.

    Why it needs its own script rather than a few lines here: it must RESTART the game and reload
    a pristine copy between passes. drive.load_run refuses to load over a live run (forcing
    Route(MAIN_MENU) leaves the old screen's UIDocuments up and _loadGameRun silently does
    nothing against that mixed state), and unless both passes start from byte-identical state the
    seed is not the only variable and the comparison means nothing.
    """
    import subprocess
    cmd = [sys.executable, os.path.join(HERE, "verify_pin.py"),
           "--seed", str(args.seed), "--steps", str(args.steps)]
    if args.run_id:
        cmd += ["--run-id", args.run_id]
    else:
        print("verify-pin needs --run-id: the fixture whose pristine copy each pass reloads.")
        print("It must be a fixture you are willing to have overwritten in GameRuns\\ -- the")
        print("backup outside GameRuns\\ is what survives.")
        return 2
    return subprocess.call(cmd)


def load_attestations(run_dir):
    path = os.path.join(run_dir, "attestation.json")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    return {}


def cmd_attest(args):
    """Record what a human (or a vision pass) ACTUALLY saw in each captured crop.

    This is the missing half of the pair. The runner captures the picture; only a reviewer can
    say whether the thing the manifest named in advance is in it. Answers are stored with the
    run, so re-running `verdict` on that run is repeatable and the judgement is auditable rather
    than living in a chat message.
    """
    run_dir = args.run_dir
    report = json.load(open(os.path.join(run_dir, "report.json"), "r", encoding="utf-8"))
    att = load_attestations(run_dir)

    if args.set:
        for entry in args.set:
            step_id, _, rest = entry.partition(":")
            tell_id, _, verdict = rest.partition("=")
            if verdict not in ("yes", "no"):
                print("--set %r: verdict must be yes or no" % entry)
                return 2
            att.setdefault(step_id, {})[tell_id] = verdict
        with open(os.path.join(run_dir, "attestation.json"), "w", encoding="utf-8") as fh:
            json.dump(att, fh, indent=2)
        print(render(report, att))
        return 0

    print("Open each crop, then record what it showed:\n")
    for step in report.get("steps") or []:
        for t in step.get("tells") or []:
            have = att.get(step["id"], {}).get(t["id"])
            print("  %s:%s" % (step["id"], t["id"]))
            print("      expect      : %s" % t["expect"])
            if t.get("invalidated_by"):
                print("      invalidated by seeing: %s" % ", ".join(t["invalidated_by"]))
            print("      crop        : %s%s" % (t["path"], "   [BLANK]" if t.get("blank") else ""))
            print("      recorded    : %s" % (have or "-- not yet --"))
            print("      to record   : bench.py attest %s --set %s:%s=yes|no"
                  % (run_dir, step["id"], t["id"]))
            print()
    return 0


def cmd_verdict(args):
    report = json.load(open(os.path.join(args.run_dir, "report.json"), "r", encoding="utf-8"))
    print(render(report, load_attestations(args.run_dir)))
    return 0


def write_report(report, run_dir):
    with open(os.path.join(run_dir, "report.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2, default=str)
    with open(os.path.join(run_dir, "report.txt"), "w", encoding="utf-8") as fh:
        fh.write(render(report, load_attestations(run_dir)))


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("---")[0],
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd")

    sub.add_parser("list", help="every manifest, with its validity and pinned seed")

    v = sub.add_parser("validate", help="offline structural check; no game needed")
    v.add_argument("names", nargs="*")

    r = sub.add_parser("run", help="run one scenario, or the whole battery")
    r.add_argument("names", nargs="*")
    r.add_argument("--all", action="store_true", help="the whole battery")
    r.add_argument("--twice", action="store_true",
                   help="determinism self-check: same seed twice, diff the digest sequence")

    s = sub.add_parser("seed-search", help="find a seed that produces a luck-gated outcome, once")
    s.add_argument("name")
    s.add_argument("--limit", type=int, default=0)

    vp = sub.add_parser("verify-pin", help="prove pin_seed changes DRAWS, not just a label")
    vp.add_argument("--run-id", help="fixture whose pristine copy each pass reloads")
    vp.add_argument("--seed", type=int, default=20260826)
    vp.add_argument("--steps", type=int, default=6)

    a = sub.add_parser("attest", help="record what each captured crop actually showed")
    a.add_argument("run_dir")
    a.add_argument("--set", action="append", metavar="STEP:TELL=yes|no")

    d = sub.add_parser("verdict", help="recompute the PAIRED verdict for a finished run")
    d.add_argument("run_dir")

    args = p.parse_args(argv)
    if not args.cmd:
        p.print_help()
        return 0
    return {
        "list": cmd_list, "validate": cmd_validate, "run": cmd_run,
        "seed-search": cmd_seed_search, "verify-pin": cmd_verify_pin,
        "attest": cmd_attest, "verdict": cmd_verdict,
    }[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
