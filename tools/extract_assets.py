"""Extract 2D sprites/textures from the FTK2 Unity assets (READ-ONLY on the game dir).

Scans `data.unity3d` plus every Addressables `*.bundle` under `StreamingAssets\\aa`,
exports Sprite objects (fallback: Texture2D) sized within --min/--max px to
`tools/out/sprites/<NAME>.png`, deduping by object name (largest wins), and writes
`tools/out/sprite-index.json`:

    { key: {"file": "sprites/<key>.png", "w": int, "h": int, "source": "<container>"} }

Keys are the Unity object names uppercased (they already match game id style for icons).

Usage:
    python tools/extract_assets.py [--game <For The King II_Data dir>] [--out tools/out]
                                   [--limit-bundles N]   # probe mode
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

import UnityPy

DEFAULT_GAME_DATA = Path(
    r"E:\Games\Steam\steamapps\common\For The King II\For The King II_Data"
)
DEFAULT_OUT = Path(__file__).resolve().parent / "out"

SAFE_NAME = re.compile(r"[^A-Za-z0-9_\-]")


def write_index(entries: dict, out_dir: Path) -> None:
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    with open(out_dir / "sprite-index.json", "w", encoding="utf-8", newline="\n") as f:
        json.dump(dict(sorted(entries.items())), f, sort_keys=True, indent=2)
        f.write("\n")


def _iter_asset_files(game_data: Path, limit_bundles: int | None) -> list[Path]:
    files: list[Path] = []
    main = game_data / "data.unity3d"
    if main.is_file():
        files.append(main)
    aa = game_data / "StreamingAssets" / "aa"
    bundles = sorted(aa.rglob("*.bundle")) if aa.is_dir() else []
    if limit_bundles is not None:
        bundles = bundles[:limit_bundles]
    files.extend(bundles)
    return files


def _export_image(obj, kind: str):
    try:
        data = obj.read()
        img = data.image  # PIL image for Sprite and Texture2D alike
    except Exception:
        return None, None
    if img is None:
        return None, None
    name = getattr(data, "m_Name", "") or ""
    return name, img


def extract(game_data: Path, out_dir: Path, min_px: int, max_px: int,
            limit_bundles: int | None = None) -> dict:
    sprites_dir = Path(out_dir) / "sprites"
    sprites_dir.mkdir(parents=True, exist_ok=True)
    entries: dict[str, dict] = {}
    sizes: dict[str, int] = {}  # key -> pixel area of kept image
    files = _iter_asset_files(game_data, limit_bundles)
    n_scanned = 0
    for path in files:
        try:
            env = UnityPy.load(str(path))
        except Exception:
            continue
        for obj in env.objects:
            kind = obj.type.name
            if kind not in ("Sprite", "Texture2D"):
                continue
            name, img = _export_image(obj, kind)
            if not name or img is None:
                continue
            w, h = img.size
            if not (min_px <= w <= max_px and min_px <= h <= max_px):
                continue
            key = SAFE_NAME.sub("_", name).upper()
            area = w * h
            # Sprites are preferred over raw Texture2D atlases of the same name;
            # among the same kind, keep the largest.
            prev = sizes.get(key)
            prefer = prev is None or (
                area > prev if entries[key]["source"].endswith(kind)
                else kind == "Sprite"
            )
            if not prefer:
                continue
            try:
                img.convert("RGBA").save(sprites_dir / f"{key}.png")
            except Exception:
                continue
            sizes[key] = area
            entries[key] = {
                "file": f"sprites/{key}.png",
                "w": w,
                "h": h,
                "source": f"{path.name}:{kind}",
            }
        n_scanned += 1
        if n_scanned % 500 == 0:
            print(f"  scanned {n_scanned}/{len(files)} files, {len(entries)} sprites kept")
    write_index(entries, out_dir)
    print(f"done: scanned {n_scanned} asset files, kept {len(entries)} sprites -> "
          f"{Path(out_dir) / 'sprite-index.json'}")
    return entries


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", type=Path, default=DEFAULT_GAME_DATA)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--min", type=int, default=32, dest="min_px")
    ap.add_argument("--max", type=int, default=1024, dest="max_px")
    ap.add_argument("--limit-bundles", type=int, default=None)
    args = ap.parse_args()
    extract(args.game, args.out, args.min_px, args.max_px, args.limit_bundles)


if __name__ == "__main__":
    main()
