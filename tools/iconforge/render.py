"""Render item-manifest icon recipes to PNGs + a contact-sheet HTML.

Usage:
    python tools/iconforge/render.py --manifest <pack.json> [--sprites tools/out]
                                     [--out <dir>] [--sheet <file.html>]

Reads each manifest entry's `Icon` block ({Base, Transforms}), resolves `Base`
against <sprites>/sprite-index.json, applies the transforms, and writes
`<out>/<Id>.png` (exact-id filenames — the convention EOR's ItemIcons loader
resolves first; see docs/research/eor-loader-notes.md §2).
"""

from __future__ import annotations

import argparse
import base64
import html
import io
import json
from pathlib import Path

from PIL import Image

import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from iconforge.transforms import apply_transforms  # noqa: E402

DEFAULT_SPRITES = Path(__file__).resolve().parents[1] / "out"


def render_manifest(
    pack: dict,
    sprite_root: Path,
    out_dir: Path,
    sheet_path: Path | None = None,
) -> list[str]:
    """Render every entry with an Icon block; returns the list of rendered ids."""
    with open(Path(sprite_root) / "sprite-index.json", encoding="utf-8") as f:
        sprite_index = json.load(f)
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    rendered: list[tuple[str, str, Image.Image, Image.Image]] = []
    for entry in pack.get("Items", []):
        icon = entry.get("Icon")
        if not icon:
            continue
        base_key = icon["Base"]
        if base_key not in sprite_index:
            raise ValueError(f"{entry['Id']}: Icon.Base not in sprite index: {base_key}")
        base_img = Image.open(
            Path(sprite_root) / sprite_index[base_key]["file"]
        ).convert("RGBA")
        img = apply_transforms(
            base_img, icon.get("Transforms", []), sprite_index, sprite_root
        )
        img.save(out_dir / f"{entry['Id']}.png")
        name = (entry.get("Loc") or {}).get("Name", entry["Id"])
        rendered.append((entry["Id"], name, base_img, img))

    if sheet_path is not None:
        _write_sheet(rendered, Path(sheet_path))
    return [r[0] for r in rendered]


def _b64(img: Image.Image, max_px: int = 128) -> str:
    thumb = img.copy()
    thumb.thumbnail((max_px, max_px), Image.LANCZOS)
    buf = io.BytesIO()
    thumb.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def _write_sheet(rendered, sheet_path: Path) -> None:
    cells = []
    for item_id, name, base_img, img in rendered:
        cells.append(
            f'<div class="cell"><div class="imgs">'
            f'<img src="data:image/png;base64,{_b64(base_img)}" title="base">'
            f'<span class="arrow">&rarr;</span>'
            f'<img src="data:image/png;base64,{_b64(img)}" title="remixed">'
            f'</div><div class="name">{html.escape(name)}</div>'
            f'<div class="id">{html.escape(item_id)}</div></div>'
        )
    sheet_path.parent.mkdir(parents=True, exist_ok=True)
    sheet_path.write_text(
        "<!doctype html><meta charset='utf-8'><title>iconforge contact sheet</title>"
        "<style>body{background:#1b1b22;color:#ddd;font-family:sans-serif;margin:16px}"
        ".grid{display:flex;flex-wrap:wrap;gap:12px}"
        ".cell{background:#26262f;border-radius:8px;padding:10px;width:300px;text-align:center}"
        ".imgs{display:flex;align-items:center;justify-content:center;gap:8px}"
        ".imgs img{image-rendering:pixelated;max-width:128px;max-height:128px;background:#111;border-radius:4px}"
        ".arrow{color:#888;font-size:20px}"
        ".name{margin-top:6px;font-weight:bold}.id{color:#8a8a99;font-size:11px}</style>"
        f"<h1>iconforge contact sheet ({len(rendered)} icons)</h1>"
        f"<div class='grid'>{''.join(cells)}</div>\n",
        encoding="utf-8",
    )


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--manifest", type=Path, required=True)
    ap.add_argument("--sprites", type=Path, default=DEFAULT_SPRITES)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--sheet", type=Path, default=None)
    args = ap.parse_args()
    with open(args.manifest, encoding="utf-8") as f:
        pack = json.load(f)
    ids = render_manifest(pack, args.sprites, args.out, args.sheet)
    print(f"rendered {len(ids)} icons -> {args.out}" + (f" (sheet: {args.sheet})" if args.sheet else ""))


if __name__ == "__main__":
    main()
