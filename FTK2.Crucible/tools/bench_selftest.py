#!/usr/bin/env python3
r"""
Offline self-test for bench.py's pure layer. No game, no network, no fixtures.

WHY THIS EXISTS
---------------
bench.py is a MEASURING INSTRUMENT. This project has already been burned twice by an instrument
that was wrong in a way nobody could see: `crucible_pin_seed` wrote a readonly label and changed
zero draws, and `fixture_health` certified a content-corrupted save as healthy for a whole
session. Both reported success while doing nothing.

So the parts of bench.py that decide PASS or FAIL are tested here against hand-built snapshots
before they are ever pointed at the game. In particular:

  * the PAIRED verdict table, including that a proc line alone is never a pass
  * UNREADABLE vs FAIL -- a null statuses/auraStatuses list must NOT read as "empty"
  * the >=2-tells / >=1-identity manifest rule
  * the vanilla-name party blocklist
  * digest redaction, so the determinism check does not fire on local GUIDs

    python FTK2.Crucible/tools/bench_selftest.py
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench  # noqa: E402

FAILURES = []


def check(name, cond, detail=""):
    if cond:
        print("  ok    %s" % name)
    else:
        FAILURES.append(name)
        print("  FAIL  %s   %s" % (name, detail))


def eq(name, got, want):
    check(name, got == want, "got %r want %r" % (got, want))


# ------------------------------------------------------------------- fixtures for the pure layer

def snap(**over):
    s = {
        "route": "COMBAT",
        "warnings": [],
        "combat": {
            "active": True, "round": 2, "wave": 0, "turn": 5, "phase": "PLAYER",
            "activeId": "guid-vamp", "episode": "abc123",
            "combatants": [
                {"id": "guid-vamp", "name": "Vampiric", "classId": "CF_ORIG_VAMPIRIC",
                 "isPlayer": True, "hp": 30, "maxHp": 46, "alive": True,
                 "tile": {"x": 1, "y": 0}, "groupIndex": 0, "ordinal": 3,
                 "isSummon": False, "isTile": False,
                 "statuses": [{"id": "STATUS_VIGOR_CF_ENGORGED", "duration": 3,
                               "initialDuration": 3, "originEntityId": "guid-vamp"}],
                 "stats": {"STR": 74}, "customData": {"CF_COUNTER_cf_bond": "2"},
                 "things": [{"id": "thing-1", "configName": "ARM_ORIG_TRAINER_BALL_CAPTURE",
                             "customData": {"CF_POKE_CONFIG": "BAT_CAVE_00",
                                            "CF_POKE_HP": "8"}}]},
                {"id": "guid-jelly", "name": "Acid Jelly", "classId": "JELLY_ACID_01",
                 "isPlayer": False, "hp": 16, "maxHp": 16, "alive": True,
                 "tile": {"x": 4, "y": 1}, "groupIndex": 1, "ordinal": 7,
                 "isSummon": False, "isTile": False,
                 "statuses": [], "stats": {}, "customData": {}, "things": []},
                {"id": "guid-bat", "name": "Vampire Bat", "classId": "BAT_VAMPIRE_01",
                 "isPlayer": False, "hp": 10, "maxHp": 10, "alive": True,
                 "tile": {"x": 2, "y": 0}, "groupIndex": 0, "ordinal": 9,
                 "isSummon": True, "isTile": False,
                 "statuses": [], "stats": {}, "customData": {"SUMMONED_BY": "guid-vamp"},
                 "things": []},
                {"id": "guid-tile", "name": None, "classId": None, "isTile": True,
                 "tile": {"x": 4, "y": 1}, "groupIndex": 1, "ordinal": 11},
            ],
            "tiles": [
                {"x": 1, "y": 0, "groupIndex": 0, "rowPositionsType": "FRONT",
                 "auraStatuses": [], "ordinal": 2, "occupantId": "guid-vamp"},
                {"x": 4, "y": 1, "groupIndex": 1, "rowPositionsType": "FRONT",
                 "auraStatuses": ["STATUS_FIRE_00"], "ordinal": 11,
                 "occupantId": "guid-jelly"},
            ],
        },
    }
    s["combat"].update(over)
    return s


def manifest(**over):
    m = {
        "schema": "crucible.scenario.v1", "name": "t", "kind": "isolation",
        "checklist_item": "X",
        "fixture": {"run_id": "r", "backup": "b.ftk2", "status": "built"},
        "party": {"expect_classes": ["Vampiric"]},
        "seed": 1,
        "steps": [{"id": "s1", "label": "l", "log": {"proc": ["SKILL_X"]}}],
        "tells": [
            {"id": "i", "kind": "identity", "region": "party", "expect": "reads Vampiric"},
            {"id": "e", "kind": "effect", "region": "board", "expect": "hp drops"},
        ],
        "asserts": [],
    }
    m.update(over)
    return m


def invalid(m):
    try:
        bench.validate(m)
        return None
    except bench.ValidationError as exc:
        return str(exc)


# =============================================================================================
print("\nTHE PAIRED VERDICT TABLE -- the whole point of the bench")
eq("log yes + screen yes  -> PASS", bench.pair_verdict(True, "yes")[0], "PASS")
eq("log yes + screen no   -> FAIL (invisible mechanic)", bench.pair_verdict(True, "no")[0], "FAIL")
check("  ...and says why", "INVISIBLE MECHANIC" in bench.pair_verdict(True, "no")[1])
eq("log no  + screen yes  -> FAIL (something else)", bench.pair_verdict(False, "yes")[0], "FAIL")
check("  ...and refuses to credit the skill",
      "do not credit the skill" in bench.pair_verdict(False, "yes")[1].lower())
eq("log no  + screen no   -> NO-FIRE", bench.pair_verdict(False, "no")[0], "NO-FIRE")
eq("log yes + UNREVIEWED  -> PENDING-EYES, NOT a pass",
   bench.pair_verdict(True, "unreviewed")[0], "PENDING-EYES")
check("  ...and says a proc line alone is not a pass",
      "ALONE is not a pass" in bench.pair_verdict(True, "unreviewed")[1])

print("\nSTEP VERDICT FOLDING")
step = {"id": "s1", "tells": [{"id": "i"}, {"id": "e"}],
        "log": {"proc_seen": {"SKILL_X": ["line"]}, "proc_missing": [],
                "soft_failures": [], "hard_failures": []}}
eq("no attestation           -> PENDING-EYES", bench.step_verdict(step, {})["verdict"],
   "PENDING-EYES")
eq("one tell attested only   -> PENDING-EYES",
   bench.step_verdict(step, {"i": "yes"})["verdict"], "PENDING-EYES")
eq("all tells yes            -> PASS",
   bench.step_verdict(step, {"i": "yes", "e": "yes"})["verdict"], "PASS")
eq("ANY tell no              -> FAIL",
   bench.step_verdict(step, {"i": "yes", "e": "no"})["verdict"], "FAIL")

missing = dict(step, log=dict(step["log"], proc_seen={}, proc_missing=["SKILL_X"]))
eq("proc missing + screen yes -> FAIL",
   bench.step_verdict(missing, {"i": "yes", "e": "yes"})["verdict"], "FAIL")

partial = dict(step, log=dict(step["log"], proc_seen={"A": ["l"]}, proc_missing=["B"]))
eq("SOME expected procs missing -> not counted as fired",
   bench.step_verdict(partial, {"i": "yes", "e": "yes"})["log_fired"], False)

adv = {"id": "r1", "player_facing": False}
eq("advance-only step        -> ADVANCE (claims nothing)",
   bench.step_verdict(adv)["verdict"], "ADVANCE")

print("\nFAIL-SAFETY vs CORRECTNESS -- reported separately, never folded")
hard = dict(step, log=dict(step["log"], hard_failures=["NullReferenceException at X"]))
eq("unhandled exception -> fail-safety FAIL", bench.step_verdict(hard, {})["fail_safety"], "FAIL")
soft = dict(step, log=dict(step["log"],
                           soft_failures=["[ClassForge] Recipe effect failed (skipped...)"]))
v = bench.step_verdict(soft, {"i": "yes", "e": "yes"})
eq("recipe effect failed -> fail-safety still PASS", v["fail_safety"], "PASS")
check("  ...and correctness is reported anyway", len(v["correctness_soft_failures"]) == 1)

print("\nLOG SCANNING")
LOG = ("[Info   :ClassForge] [ClassForge] proc SKILL_CF_VAMPIRIC_ENGORGED (Engorged) "
       "owner=abc actions=2\n"
       "[Warning:ClassForge] [ClassForge] Recipe effect failed (skipped, rest of plan continues)\n"
       "[Info   : Unity] something ordinary\n")
scanned = bench.scan_log(LOG, ["SKILL_CF_VAMPIRIC_ENGORGED", "SKILL_CF_VAMPIRIC_BAT_SWARM"])
eq("found the proc that is there", list(scanned["proc_seen"]), ["SKILL_CF_VAMPIRIC_ENGORGED"])
eq("reports the one that is not", scanned["proc_missing"], ["SKILL_CF_VAMPIRIC_BAT_SWARM"])
eq("soft failure seen", len(scanned["soft_failures"]), 1)
eq("no hard failure invented", scanned["hard_failures"], [])
eq("empty log -> nothing fired", bench.scan_log("", ["X"])["proc_missing"], ["X"])

print("\nSELECTORS -- GUIDs are never a selector form; ordinal is the peer-stable key")
s = snap()
eq("class: by configId", bench.resolve(s, "class:CF_ORIG_VAMPIRIC")["name"], "Vampiric")
eq("class: by display name", bench.resolve(s, "class:Vampiric")["ordinal"], 3)
eq("enemy:0 skips tiles and player side", bench.resolve(s, "enemy:0")["name"], "Acid Jelly")
eq("ally:0", bench.resolve(s, "ally:0")["name"], "Vampiric")
eq("summon:0", bench.resolve(s, "summon:0")["name"], "Vampire Bat")
eq("ordinal:", bench.resolve(s, "ordinal:7")["name"], "Acid Jelly")
eq("active", bench.resolve(s, "active")["name"], "Vampiric")
check("tile entities are excluded from actors()",
      all(not c.get("isTile") for c in bench.actors(s)))
eq("count enemy:*", bench.resolve_count(s, "enemy:*"), 1)
eq("count summon:*", bench.resolve_count(s, "summon:*"), 1)
try:
    bench.resolve(s, "enemy:9")
    check("out-of-range selector raises", False)
except LookupError:
    check("out-of-range selector raises", True)

print("\nNULL IS NOT EMPTY -- the rule that stops a blind oracle reporting PASS")
blind = snap(combatants=None)
eq("null combatants -> UNREADABLE",
   bench.evaluate_assert({"kind": "alive", "selector": "ally:0", "value": True}, blind)[0],
   "UNREADABLE")

no_statuses = snap()
no_statuses["combat"]["combatants"][1]["statuses"] = None
eq("null statuses -> UNREADABLE, not 'has no statuses'",
   bench.evaluate_assert({"kind": "status_absent", "selector": "enemy:0",
                          "status": "STATUS_FIRE_00"}, no_statuses)[0], "UNREADABLE")
eq("EMPTY statuses -> a real PASS for status_absent",
   bench.evaluate_assert({"kind": "status_absent", "selector": "enemy:0",
                          "status": "STATUS_FIRE_00"}, snap())[0], "PASS")

null_tiles = snap(tiles=None)
eq("null tiles -> UNREADABLE",
   bench.evaluate_assert({"kind": "tile_aura", "tile": "any", "status": "STATUS_FIRE_00"},
                         null_tiles)[0], "UNREADABLE")
null_aura = snap()
null_aura["combat"]["tiles"][0]["auraStatuses"] = None
eq("a null auraStatuses MEMBER -> UNREADABLE, never 'clean tile'",
   bench.evaluate_assert({"kind": "tile_aura", "tile": "any", "status": "STATUS_FIRE_00"},
                         null_aura)[0], "UNREADABLE")

print("\nASSERTS")
A = lambda a, sn=None: bench.evaluate_assert(a, sn or snap())
eq("hp", A({"kind": "hp", "selector": "ally:0", "op": "eq", "value": 30})[0], "PASS")
eq("hp fails honestly", A({"kind": "hp", "selector": "ally:0", "op": "eq", "value": 99})[0], "FAIL")
eq("alive", A({"kind": "alive", "selector": "enemy:0", "value": True})[0], "PASS")
eq("status_present", A({"kind": "status_present", "selector": "ally:0",
                        "status": "STATUS_VIGOR_CF_ENGORGED"})[0], "PASS")
eq("status_duration", A({"kind": "status_duration", "selector": "ally:0",
                         "status": "STATUS_VIGOR_CF_ENGORGED", "op": "eq", "value": 3})[0], "PASS")
eq("customData string coerces to a number",
   A({"kind": "custom_data", "selector": "ally:0", "key": "CF_COUNTER_cf_bond",
      "op": "lt", "value": 3})[0], "PASS")
eq("thing_custom_data", A({"kind": "thing_custom_data", "selector": "ally:0",
                           "thing_config": "ARM_ORIG_TRAINER_BALL_CAPTURE",
                           "key": "CF_POKE_CONFIG", "op": "eq", "value": "BAT_CAVE_00"})[0], "PASS")
eq("tile_aura at an explicit tile",
   A({"kind": "tile_aura", "tile": {"x": 4, "y": 1}, "status": "STATUS_FIRE_00"})[0], "PASS")
eq("tile_aura 'any' finds the RANDOM_TILE hazard",
   A({"kind": "tile_aura", "tile": "any", "status": "STATUS_FIRE_00"})[0], "PASS")
eq("tile_aura 'any' reports a genuine absence as FAIL",
   A({"kind": "tile_aura", "tile": "any", "status": "STATUS_ACID_00"})[0], "FAIL")
eq("count", A({"kind": "count", "selector": "summon:*", "op": "gte", "value": 1})[0], "PASS")
eq("unknown selector -> ERROR, never PASS",
   A({"kind": "hp", "selector": "nonsense:0", "op": "eq", "value": 1})[0], "ERROR")

before = snap()
after = snap()
after["combat"]["combatants"][1]["hp"] = 4
eq("hp_delta measures against the baseline",
   bench.evaluate_assert({"kind": "hp_delta", "selector": "enemy:0", "op": "lte", "value": -6},
                         after, before)[0], "PASS")
eq("hp_delta with no baseline -> ERROR, not a silent pass",
   bench.evaluate_assert({"kind": "hp_delta", "selector": "enemy:0", "op": "lt", "value": 0},
                         after)[0], "ERROR")

print("\nMANIFEST VALIDATION")
check("a good manifest validates", bench.validate(manifest()) is True)
check("one tell is rejected",
      "at least 2 tells" in (invalid(manifest(tells=[{"id": "e", "kind": "effect",
                                                      "region": "board", "expect": "x"}])) or ""))
check("two EFFECT tells and no identity tell is rejected",
      "identity" in (invalid(manifest(tells=[
          {"id": "a", "kind": "effect", "region": "board", "expect": "x"},
          {"id": "b", "kind": "effect", "region": "party", "expect": "y"}])) or ""))
check("a tell with no `expect` is rejected (name the element IN ADVANCE)",
      "NAMED IN ADVANCE" in (invalid(manifest(tells=[
          {"id": "a", "kind": "identity", "region": "party"},
          {"id": "b", "kind": "effect", "region": "board", "expect": "y"}])) or ""))
check("expecting a VANILLA class is rejected",
      "VANILLA" in (invalid(manifest(party={"expect_classes": ["Shepherd"]})) or ""))
check("no seed and no seed_search is rejected",
      "not reproducible" in (invalid(manifest(seed=None)) or ""))
check("seed_search naming an unknown assert is rejected",
      "names no assert id" in (invalid(manifest(
          seed=None, seed_search={"success_assert": "nope",
                                  "candidates": {"start": 1, "count": 2}})) or ""))
check("fixture.load_copy is refused outright",
      "ALWAYS copied" in (invalid(manifest(
          fixture={"run_id": "r", "backup": "b", "status": "built",
                   "load_copy": False})) or ""))
check("fixture.status is mandatory",
      "fixture.status" in (invalid(manifest(fixture={"run_id": "r", "backup": "b"})) or ""))
check("a player-facing step with no expected proc is rejected",
      "log.proc is required" in (invalid(manifest(
          steps=[{"id": "s", "label": "l"}])) or ""))
check("an advance-only step needs no proc",
      bench.validate(manifest(steps=[{"id": "s", "label": "l", "player_facing": False,
                                      "advance": {"end_turn": 1}}])) is True)
check("an unknown assert kind is rejected",
      "is not one of" in (invalid(manifest(
          asserts=[{"id": "a", "kind": "vibes", "selector": "ally:0"}])) or ""))
check("duplicate step ids are rejected",
      "duplicated" in (invalid(manifest(steps=[
          {"id": "s", "label": "a", "log": {"proc": ["X"]}},
          {"id": "s", "label": "b", "log": {"proc": ["X"]}}])) or ""))

print("\nDIGEST REDACTION -- local GUIDs must not make the determinism check cry wolf")
a = snap()
b = snap()
b["combat"]["episode"] = "different-episode"
b["combat"]["activeId"] = "guid-vamp"
for c in b["combat"]["combatants"]:
    c["id"] = "regenerated-" + str(c.get("ordinal"))
    for st in (c.get("statuses") or []):
        st["originEntityId"] = "regenerated"
    if c.get("customData", {}).get("SUMMONED_BY"):
        c["customData"]["SUMMONED_BY"] = "regenerated"
    for t in (c.get("things") or []):
        t["id"] = "regenerated"
for t in b["combat"]["tiles"]:
    t["occupantId"] = "regenerated"
eq("per-peer GUIDs / episode are redacted out of the digest",
   bench.digest_of({"snapshot": a}), bench.digest_of({"snapshot": b}))

c = snap()
c["combat"]["combatants"][1]["hp"] = 4
check("a REAL state change still moves the digest",
      bench.digest_of({"snapshot": a}) != bench.digest_of({"snapshot": c}))
check("the plugin's own digest is preferred when present",
      bench.digest_of({"snapshot": a, "digest": "PLUGIN"}) == "PLUGIN")

print("\nDETERMINISM SEQUENCE DIFF")
eq("identical sequences -> no divergence", bench.diff_sequences(["a", "b"], ["a", "b"], "d"), None)
check("divergence is located exactly",
      "index 1" in bench.diff_sequences(["a", "b"], ["a", "c"], "d"))
check("a short run is a divergence, not a match",
      "<ended>" in bench.diff_sequences(["a", "b"], ["a"], "d"))

print("\nEVERY SHIPPED MANIFEST VALIDATES")
import glob  # noqa: E402
import json  # noqa: E402
for path in sorted(glob.glob(os.path.join(bench.SCENARIO_DIR, "*.json"))):
    name = os.path.basename(path)
    try:
        bench.validate(json.load(open(path, "r", encoding="utf-8")), path)
        check(name, True)
    except bench.ValidationError as exc:
        check(name, False, str(exc)[:300])

print("\n" + "=" * 78)
if FAILURES:
    print("%d FAILURE(S): %s" % (len(FAILURES), ", ".join(FAILURES)))
    sys.exit(1)
print("all bench.py self-tests pass")
