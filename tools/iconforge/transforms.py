"""Deterministic, recipe-driven image transforms for FTK2 icon remixing.

Every op is a pure function of (image, params) — no randomness, no wall-clock —
so a recipe renders byte-identically on every run. Inputs are never mutated.

Transform vocabulary (the `Op` strings are the contract used by item manifests):

    HSV_SHIFT       {H, S, V}                  hue rotation (0..1 wraps), sat/val deltas
    PALETTE_MAP     {From, To, Tolerance}      remap colors near `From` (hex) to `To`
    GLOW            {Color, Radius, Strength}  soft halo behind the silhouette
    OVERLAY         {Key, Anchor, Scale, Alpha} stamp another sprite over the image
    TINT_SILHOUETTE {Color, Alpha}             recolor all opaque pixels toward Color
    COMPOSITE       {Key, Anchor, Scale}       OVERLAY with Alpha=1.0
    OUTLINE         {Color, Width}             hard edge around the silhouette
"""

from __future__ import annotations

import colorsys
from pathlib import Path

from PIL import Image, ImageFilter

ANCHORS = {"C", "N", "S", "E", "W", "NE", "NW", "SE", "SW"}


def _hex_rgb(s: str) -> tuple[int, int, int]:
    s = s.lstrip("#")
    return int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16)


def _anchor_pos(base: tuple[int, int], stamp: tuple[int, int], anchor: str) -> tuple[int, int]:
    bw, bh = base
    sw, sh = stamp
    x = {"W": 0, "E": bw - sw}.get(anchor[-1] if anchor[-1] in "WE" else "", (bw - sw) // 2)
    y = {"N": 0, "S": bh - sh}.get(anchor[0] if anchor[0] in "NS" else "", (bh - sh) // 2)
    return x, y


def _op_hsv_shift(img: Image.Image, p: dict) -> Image.Image:
    dh, ds, dv = float(p.get("H", 0)), float(p.get("S", 0)), float(p.get("V", 0))
    out = img.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            h = (h + dh) % 1.0
            s = min(1.0, max(0.0, s + ds))
            v = min(1.0, max(0.0, v + dv))
            nr, ng, nb = colorsys.hsv_to_rgb(h, s, v)
            px[x, y] = (round(nr * 255), round(ng * 255), round(nb * 255), a)
    return out


def _op_palette_map(img: Image.Image, p: dict) -> Image.Image:
    src = _hex_rgb(p["From"])
    dst = _hex_rgb(p["To"])
    tol = int(p.get("Tolerance", 0))
    out = img.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a and abs(r - src[0]) <= tol and abs(g - src[1]) <= tol and abs(b - src[2]) <= tol:
                px[x, y] = (*dst, a)
    return out


def _op_glow(img: Image.Image, p: dict) -> Image.Image:
    color = _hex_rgb(p["Color"])
    radius = int(p.get("Radius", 4))
    strength = float(p.get("Strength", 1.0))
    alpha = img.getchannel("A")
    halo_alpha = alpha.filter(ImageFilter.GaussianBlur(radius)).point(
        lambda v: min(255, round(v * strength * 1.6))
    )
    halo = Image.new("RGBA", img.size, (*color, 0))
    halo.putalpha(halo_alpha)
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    out = Image.alpha_composite(out, halo)
    return Image.alpha_composite(out, img)


def _load_sprite(p: dict, sprite_index: dict | None, sprite_root: Path | None) -> Image.Image:
    key = p["Key"]
    if not sprite_index or key not in sprite_index:
        raise ValueError(f"sprite key not in index: {key}")
    path = Path(sprite_root or ".") / sprite_index[key]["file"]
    return Image.open(path).convert("RGBA")


def _op_overlay(img: Image.Image, p: dict, sprite_index, sprite_root) -> Image.Image:
    stamp = _load_sprite(p, sprite_index, sprite_root)
    scale = float(p.get("Scale", 1.0))
    if scale != 1.0:
        stamp = stamp.resize(
            (max(1, round(stamp.width * scale)), max(1, round(stamp.height * scale))),
            Image.LANCZOS,
        )
    alpha = float(p.get("Alpha", 1.0))
    if alpha < 1.0:
        stamp.putalpha(stamp.getchannel("A").point(lambda v: round(v * alpha)))
    anchor = p.get("Anchor", "C")
    if anchor not in ANCHORS:
        raise ValueError(f"bad anchor: {anchor}")
    out = img.copy()
    out.alpha_composite(stamp, _anchor_pos(img.size, stamp.size, anchor))
    return out


def _op_tint_silhouette(img: Image.Image, p: dict) -> Image.Image:
    color = _hex_rgb(p["Color"])
    t = float(p.get("Alpha", 1.0))
    out = img.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            px[x, y] = (
                round(r + (color[0] - r) * t),
                round(g + (color[1] - g) * t),
                round(b + (color[2] - b) * t),
                a,
            )
    return out


def _op_outline(img: Image.Image, p: dict) -> Image.Image:
    color = _hex_rgb(p["Color"])
    width = int(p.get("Width", 1))
    alpha = img.getchannel("A")
    grown = alpha.filter(ImageFilter.MaxFilter(2 * width + 1))
    ring = Image.new("RGBA", img.size, (*color, 0))
    ring.putalpha(grown.point(lambda v: 255 if v > 0 else 0))
    return Image.alpha_composite(ring, img)


def apply_transforms(
    img: Image.Image,
    transforms: list[dict],
    sprite_index: dict | None = None,
    sprite_root: Path | None = None,
) -> Image.Image:
    """Apply a recipe (ordered list of op dicts) to an RGBA image, returning a new image."""
    out = img.convert("RGBA")
    for t in transforms:
        op = t.get("Op")
        if op == "HSV_SHIFT":
            out = _op_hsv_shift(out, t)
        elif op == "PALETTE_MAP":
            out = _op_palette_map(out, t)
        elif op == "GLOW":
            out = _op_glow(out, t)
        elif op == "OVERLAY":
            out = _op_overlay(out, t, sprite_index, sprite_root)
        elif op == "COMPOSITE":
            out = _op_overlay(out, {**t, "Alpha": 1.0}, sprite_index, sprite_root)
        elif op == "TINT_SILHOUETTE":
            out = _op_tint_silhouette(out, t)
        elif op == "OUTLINE":
            out = _op_outline(out, t)
        else:
            raise ValueError(f"unknown transform op: {op}")
    return out
