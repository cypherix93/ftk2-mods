#!/usr/bin/env python3
"""
Content sweep: assert every authored class and every status id our recipes reference
actually resolves in the LIVE, running game.

Why this exists
---------------
Content can be perfect on disk and inert in the game. Two authored recipes shipped applying
"CURSE" -- an eStatusEffectTypes member rather than a Configs.StatusEffects key -- so the
Warlock's signature ability silently did nothing, and no test in the repo could have caught
it. The offline validators check JSON against JSON. This checks JSON against the game.

It drives the running game through Crucible's RPC (the same surface the MCP tools use), so a
"pass" here means the id was resolved by the live merged config, not by a parser.

Usage
-----
    python FTK2.Crucible/tools/content_sweep.py [--port 8787] [--json out.json]

Exit codes: 0 = every id resolved · 1 = at least one did not · 2 = game unreachable.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Packs that declare classes and/or reference statuses from recipes.
#
# A pack may be PARKED: authored in the repo but deliberately not deployed, so its ids are
# absent from the live game by design. Parked packs are still swept, and reported separately,
# because "missing because nobody shipped it" and "missing because it is broken" are different
# findings and collapsing them either hides a real defect or cries wolf about a known decision.
PACK_DIRS = [
    ("FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES", None),
    ("FTK2.ClassForge/data/ClassPacks/CF_PACK_ENCOUNTER_MODIFIERS", None),
    ("FTK2.Blessings/data/ClassPacks/BLSS_PACK_EOR_BLESSINGS", None),
    ("FTK2.ClassForge/data/ClassPacks/CF_PACK_BALDURS",
     "parked by tools/deploy.ps1 since 2026-08-05 (needs -IncludeBaldurs); "
     "its 5 skill recipes have no localization entries"),
]


def rpc_exec(port, command, args):
    """One console command over the loopback RPC. Returns the result string, or raises."""
    body = json.dumps({"command": command, "args": args}).encode("utf-8")
    req = urllib.request.Request(
        "http://127.0.0.1:%d/exec" % port,
        data=body,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=30) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if not payload.get("ok"):
        raise RuntimeError("rpc not ok: %s" % payload)
    return payload.get("result") or ""


def load_json(path):
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def collect_class_ids():
    """Every authored class id, with the pack that declares it."""
    found = {}
    for pack, parked in PACK_DIRS:
        data = load_json(os.path.join(REPO_ROOT, pack, "classes.json"))
        if not data:
            continue
        ids = data.keys() if isinstance(data, dict) else [entry.get("Id") for entry in data]
        for class_id in ids:
            if class_id:
                found[class_id] = (pack, parked)
    return found


def walk_status_ids(node, sink):
    """
    Recursively harvest status ids from a recipe tree.

    Both spellings matter: "Status" carries a single id and "StatusOneOf" a list, and a sweep
    that only knew about one would report a clean run while half the references went unchecked.
    The literal TRIGGER_STATUS token is a runtime placeholder resolved from the triggering
    effect, not a config key, so it is deliberately not treated as an id.
    """
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "Status" and isinstance(value, str):
                sink.add(value)
            elif key == "StatusOneOf" and isinstance(value, list):
                for item in value:
                    if isinstance(item, str):
                        sink.add(item)
            else:
                walk_status_ids(value, sink)
    elif isinstance(node, list):
        for item in node:
            walk_status_ids(item, sink)


def collect_status_ids():
    """Status ids referenced by recipes, plus ids the packs declare themselves."""
    referenced = set()
    declared = set()
    for pack, _parked in PACK_DIRS:
        recipes = load_json(os.path.join(REPO_ROOT, pack, "skillrecipes.json"))
        if recipes:
            walk_status_ids(recipes, referenced)
        statuses = load_json(os.path.join(REPO_ROOT, pack, "statuses.json"))
        if isinstance(statuses, dict):
            declared.update(statuses.keys())
    referenced.discard("TRIGGER_STATUS")
    return referenced, declared


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8787)
    parser.add_argument("--json", dest="json_out")
    options = parser.parse_args()

    try:
        urllib.request.urlopen("http://127.0.0.1:%d/health" % options.port, timeout=15).read()
    except (urllib.error.URLError, OSError) as error:
        print("SKIP: game not reachable on port %d (%s)" % (options.port, error))
        return 2

    classes = collect_class_ids()
    referenced, declared = collect_status_ids()
    statuses = sorted(referenced | declared)

    results = {"classes": {}, "statuses": {}}
    failures = []

    parked_absent = []
    print("== classes (%d) ==" % len(classes))
    for class_id in sorted(classes):
        pack, parked = classes[class_id]
        result = rpc_exec(options.port, "crucible_class_config", [class_id])
        present = "PRESENT=True" in result
        results["classes"][class_id] = {
            "present": present, "pack": pack, "parked": parked, "raw": result,
        }
        if present:
            continue
        if parked:
            parked_absent.append("%s (%s)" % (class_id, parked))
            print("  PARKED   %s" % class_id)
        else:
            failures.append("class %s (declared in %s) is NOT in the live config" % (class_id, pack))
            print("  MISSING  %s" % class_id)
    print("  %d present, %d missing, %d absent-but-parked" % (
        sum(1 for v in results["classes"].values() if v["present"]),
        sum(1 for v in results["classes"].values() if not v["present"] and not v["parked"]),
        sum(1 for v in results["classes"].values() if not v["present"] and v["parked"]),
    ))

    print("== statuses (%d referenced/declared) ==" % len(statuses))
    for status_id in statuses:
        result = rpc_exec(options.port, "crucible_status_config", [status_id])
        present = "PRESENT=True" in result
        results["statuses"][status_id] = {
            "present": present,
            "referenced": status_id in referenced,
            "declared": status_id in declared,
            "raw": result,
        }
        if not present:
            how = "referenced by a recipe" if status_id in referenced else "declared by a pack"
            failures.append("status %s (%s) does NOT resolve -- anything applying it does nothing" % (status_id, how))
            print("  MISSING  %s  (%s)" % (status_id, how))
    print("  %d present, %d missing" % (
        sum(1 for v in results["statuses"].values() if v["present"]),
        sum(1 for v in results["statuses"].values() if not v["present"]),
    ))

    if options.json_out:
        with open(options.json_out, "w", encoding="utf-8") as handle:
            json.dump(results, handle, indent=1, sort_keys=True)
        print("wrote %s" % options.json_out)

    print("\n== summary ==")
    if failures:
        for failure in failures:
            print("  FAIL  %s" % failure)
        print("%d failure(s)" % len(failures))
        return 1

    live_classes = sum(1 for v in results["classes"].values() if v["present"])
    print("all %d deployed classes and %d statuses resolve in the live game%s"
          % (live_classes, len(statuses),
             "" if not parked_absent else " (%d parked class(es) absent by design)" % len(parked_absent)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
