"""
Screenshot ZOOM helper — look at big legible crops instead of squinting at a full frame.

WHY THIS EXISTS
---------------
Repeated wrong visual calls in this project all share one cause: judging a ~30px model at the
back of a dim room from a downscaled full-frame screenshot, then reporting the guess as an
observation. A partner was called "a glowing teal jelly" when it was a human adventurer.

The fix is not to look harder. It is to look at a REGION, UPSCALED.

Highest-signal regions, in order:
  1. `portraits`  - the summon/ally portrait cards (top-left). These carry the character's actual
                    portrait ART, large and high-contrast, with no camera angle or occlusion.
                    A human face vs a jelly blob is unmistakable here. USE THIS FIRST.
  2. `party`      - the party HUD cards along the bottom.
  3. `board`      - the centre of the play area (models, tiles, decals).
  4. `full`       - the whole frame, for orientation only. Never for identifying art.

USAGE
    python shot_zoom.py                      # portraits + board, 3x
    python shot_zoom.py portraits 4          # one region at 4x
    python shot_zoom.py crop 120 90 420 260  # arbitrary rect (x y w h), 3x

Writes PNGs next to the scratchpad and prints their paths, one per line, for the caller to open.
Opening the image is the point -- a saved crop nobody looks at verifies nothing.
"""

import base64
import io
import os
import sys

import drive

try:
    from PIL import Image
except ImportError:  # pragma: no cover
    print("PIL missing: pip install pillow")
    raise

OUT_DIR = (r"C:\Users\ben\AppData\Local\Temp\claude\C--Users-ben-repos"
           r"\0cfd7dc6-16ae-44ba-8cc4-2f320ee515be\scratchpad")

# Fractions of (width, height) so these hold at any resolution.
REGIONS = {
    "portraits": (0.00, 0.07, 0.20, 0.20),   # summon/ally cards, upper-left
    "party":     (0.00, 0.83, 0.62, 0.17),   # party HUD cards along the bottom
    "board":     (0.10, 0.30, 0.75, 0.45),   # play area
    "enemy":     (0.78, 0.08, 0.22, 0.12),   # enemy nameplate card, upper-right
    "toolbelt":  (0.72, 0.90, 0.28, 0.10),   # toolbelt slots
}


def grab():
    payload = drive._post("/screenshot", {})
    raw = base64.b64decode(payload.get("base64") or "")
    return Image.open(io.BytesIO(raw)).convert("RGB")


def save_crop(img, name, box_frac, scale):
    w, h = img.size
    fx, fy, fw, fh = box_frac
    box = (int(fx * w), int(fy * h), int((fx + fw) * w), int((fy + fh) * h))
    crop = img.crop(box)
    crop = crop.resize((crop.width * scale, crop.height * scale), Image.LANCZOS)
    path = os.path.join(OUT_DIR, "zoom_%s.png" % name)
    crop.save(path)
    return path, crop.size


def main():
    args = sys.argv[1:]
    scale = 3
    img = grab()

    if args and args[0] == "crop":
        x, y, cw, ch = (int(v) for v in args[1:5])
        if len(args) > 5:
            scale = int(args[5])
        w, h = img.size
        frac = (x / w, y / h, cw / w, ch / h)
        path, size = save_crop(img, "custom", frac, scale)
        print("%s  (%dx%d)" % (path, size[0], size[1]))
        return

    wanted = ["portraits", "board"]
    if args:
        wanted = [args[0]]
        if len(args) > 1:
            scale = int(args[1])
    if wanted == ["full"]:
        path = os.path.join(OUT_DIR, "zoom_full.png")
        img.save(path)
        print("%s  (%dx%d)  -- orientation only, do NOT identify art from this"
              % (path, img.width, img.height))
        return

    for name in wanted:
        if name not in REGIONS:
            print("unknown region %r; known: %s" % (name, ", ".join(sorted(REGIONS))))
            continue
        path, size = save_crop(img, name, REGIONS[name], scale)
        print("%s  (%dx%d)" % (path, size[0], size[1]))


if __name__ == "__main__":
    main()
