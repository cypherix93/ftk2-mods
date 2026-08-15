"""Emit CF_PACK_ARMORY_VISUALS/visualfallbacks.json from the Armory's compiled fallback map.

The compile pipeline (compile_pack.py / eor_import.py) already records one visual donor per
generated item in FTK2.Armory/data/VisualFallbacks.json — but that file is tool output the game
never reads. This script filters it to the items the Armory actually ships today and re-emits it
as a ClassForge fallback-only pack, which is the surface the runtime consumes
(MergePlan.VisualFallbacks -> EquipmentVisualHelper.GetITMEquipmentPrefab prefix + item-card art).

Rules:
  - keys: every id present in FTK2.Armory/data/Things/*.json (the shipped catalog). Entries in
    the source map for items no longer shipped there (e.g. the 31 ARM_EOR_STARTER_* weapons that
    moved into CF_PACK_EOR_CLASSES, which carries their fallbacks itself) are dropped.
  - a shipped item with no source entry is a hard error: the compile pipeline guarantees one per
    item, so a gap means the catalog and the map have drifted — regenerate, don't guess.
  - donors are validated against the vanilla game configs when --game-dir (or the default) exists;
    every donor must be a live vanilla Things id. Validation is read-only.
  - output is sorted and stable; run it again any time the Armory catalog changes.

Usage:
    python tools/gen_visual_fallbacks.py [--game-dir "...\\For The King II"] [--check]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ARMORY_THINGS = REPO / "FTK2.Armory" / "data" / "Things"
SOURCE_MAP = REPO / "FTK2.Armory" / "data" / "VisualFallbacks.json"
PACK_DIR = REPO / "FTK2.ClassForge" / "data" / "ClassPacks" / "CF_PACK_ARMORY_VISUALS"
DEFAULT_GAME_DIR = Path(r"E:\Games\Steam\steamapps\common\For The King II")

PACK_JSON = {
    "id": "CF_PACK_ARMORY_VISUALS",
    "name": "Armory visual fallbacks",
    "version": "1.0.0",
    "loadOrder": 10,
    "dependencies": [],
    "enabled": True,
}


def load_json(path: Path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def shipped_armory_ids() -> set[str]:
    ids: set[str] = set()
    for f in sorted(ARMORY_THINGS.glob("*.json")):
        doc = load_json(f)
        if isinstance(doc, dict):
            ids.update(doc.keys())
    return ids


def vanilla_thing_ids(game_dir: Path) -> set[str] | None:
    cfg = game_dir / "For The King II_Data" / "StreamingAssets" / "Assets" / "Configs" / "JSON~" / "Things"
    if not cfg.is_dir():
        return None
    ids: set[str] = set()
    for f in sorted(cfg.glob("*.json")):
        doc = load_json(f)
        if isinstance(doc, dict):
            ids.update(doc.keys())
    return ids


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game-dir", type=Path, default=DEFAULT_GAME_DIR)
    ap.add_argument("--check", action="store_true",
                    help="verify the emitted file is up to date instead of writing it")
    args = ap.parse_args()

    source = load_json(SOURCE_MAP)
    shipped = shipped_armory_ids()

    missing = sorted(i for i in shipped if i not in source)
    if missing:
        print(f"ERROR: {len(missing)} shipped Armory item(s) have no VisualFallbacks.json entry "
              f"(catalog/map drift — recompile the pack): {missing[:5]}...")
        return 1

    emitted = {k: source[k] for k in sorted(shipped)}
    dropped = sorted(k for k in source if k not in shipped)

    vanilla = vanilla_thing_ids(args.game_dir)
    if vanilla is None:
        print(f"NOTE: game configs not found under '{args.game_dir}' — donor validation skipped.")
    else:
        bad = sorted((k, v) for k, v in emitted.items() if v not in vanilla)
        if bad:
            print(f"ERROR: {len(bad)} donor id(s) are not vanilla Things ids: {bad[:5]}...")
            return 1
        print(f"donors: all {len(set(emitted.values()))} unique donors resolve against "
              f"{len(vanilla)} vanilla Things ids.")

    out_fallbacks = PACK_DIR / "visualfallbacks.json"
    out_pack = PACK_DIR / "pack.json"
    fallbacks_text = json.dumps(emitted, sort_keys=True, indent=2, ensure_ascii=False) + "\n"
    pack_text = json.dumps(PACK_JSON, indent=2, ensure_ascii=False) + "\n"

    if args.check:
        current = out_fallbacks.read_text(encoding="utf-8-sig") if out_fallbacks.is_file() else ""
        if current != fallbacks_text:
            print("ERROR: emitted map is stale — rerun tools/gen_visual_fallbacks.py")
            return 1
        print("check: up to date.")
        return 0

    PACK_DIR.mkdir(parents=True, exist_ok=True)
    out_fallbacks.write_text(fallbacks_text, encoding="utf-8", newline="\n")
    if not out_pack.is_file():
        out_pack.write_text(pack_text, encoding="utf-8", newline="\n")

    print(f"wrote {out_fallbacks} ({len(emitted)} entries; dropped {len(dropped)} "
          f"non-shipped source keys, e.g. starters now owned by CF_PACK_EOR_CLASSES)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
