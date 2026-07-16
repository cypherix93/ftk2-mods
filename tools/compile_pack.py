"""Compile a validated item-manifest pack into ship-shape game files.

Outputs under --out (default FTK2.Armory/data):
    Things/<PackId>.json      id -> ThingConfig dict (the game/EOR-loadable shape)
    VisualFallbacks.json      merge-appended {customId: donorId}
    Localization/en.json      merge-appended {<Id>: Name, <Id>_DESCRIPTION: Description}
    icons/<Id>.png            copied from --icons (rendered by iconforge), if present

Refuses to compile if validate_pack reports any ERROR.

Usage:
    python tools/compile_pack.py <pack.json> [--icons <rendered-dir>]
                                 [--out FTK2.Armory/data]
                                 [--vocab tools/out/vocab-index.json]
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

from validate_pack import validate_pack, DEFAULT_VOCAB


def _merge_json(path: Path, new_entries: dict) -> None:
    existing = {}
    if path.is_file():
        with open(path, encoding="utf-8-sig") as f:
            existing = json.load(f)
    existing.update(new_entries)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(existing, f, sort_keys=True, indent=2, ensure_ascii=False)
        f.write("\n")


def compile_pack(pack: dict, out_dir: Path, vocab: dict,
                 icons_dir: Path | None = None) -> None:
    findings = validate_pack(pack, vocab, sprite_index=None)
    errors = [f for f in findings if f["level"] == "ERROR"]
    if errors:
        for f_ in errors:
            print(f"[ERROR] {f_['id']} {f_['check']}: {f_['msg']}")
        print(f"refusing to compile: {len(errors)} validation errors")
        raise SystemExit(1)

    out_dir = Path(out_dir)
    pack_id = pack["PackId"]
    items = pack.get("Items", [])

    things = {e["Id"]: e["Thing"] for e in items}
    things_path = out_dir / "Things" / f"{pack_id}.json"
    things_path.parent.mkdir(parents=True, exist_ok=True)
    with open(things_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(things, f, sort_keys=True, indent=2, ensure_ascii=False)
        f.write("\n")

    _merge_json(out_dir / "VisualFallbacks.json",
                {e["Id"]: e["VisualFallback"] for e in items})

    loc: dict[str, str] = {}
    for e in items:
        loc[e["Id"]] = e["Loc"]["Name"]
        loc[e["Id"] + "_DESCRIPTION"] = e["Loc"]["Description"]
    _merge_json(out_dir / "Localization" / "en.json", loc)

    copied = 0
    if icons_dir is not None:
        icons_out = out_dir / "icons"
        for e in items:
            if not e.get("Icon"):
                continue
            src = Path(icons_dir) / f"{e['Id']}.png"
            if src.is_file():
                icons_out.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(src, icons_out / src.name)
                copied += 1
            else:
                print(f"[WARN] {e['Id']}: icon recipe present but {src} not rendered yet")

    print(f"compiled {pack_id}: {len(items)} items -> {out_dir} ({copied} icons)")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("pack", type=Path)
    ap.add_argument("--icons", type=Path, default=None)
    ap.add_argument("--out", type=Path,
                    default=Path(__file__).resolve().parents[1] / "FTK2.Armory" / "data")
    ap.add_argument("--vocab", type=Path, default=DEFAULT_VOCAB)
    args = ap.parse_args()
    with open(args.pack, encoding="utf-8") as f:
        pack = json.load(f)
    with open(args.vocab, encoding="utf-8") as f:
        vocab = json.load(f)
    compile_pack(pack, args.out, vocab, args.icons)


if __name__ == "__main__":
    main()
