"""
Evidence capture — labelled screenshots + zoom crops for the verification artifact.

WHY THIS EXISTS
---------------
Verification claims in this project are only as good as the picture behind them, and a picture
nobody labelled is nearly as useless as no picture. This writes every capture under a STABLE NAME so
the artifact builder can reference it, and it always writes BOTH the full frame (for orientation)
and the zoom crops (for actually identifying anything).

The full frame is orientation ONLY. At 1577x981 a partner model is ~30px and jelly-vs-human is a
coin flip -- that mistake was made twice in this project. Identify art from a crop.

USAGE
    python evidence.py <label>              # full + board + portraits
    python evidence.py <label> board party  # named regions only

Writes to  .../scratchpad/evidence/<label>_<region>.png  and prints each path.
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
           r"\0cfd7dc6-16ae-44ba-8cc4-2f320ee515be\scratchpad\evidence")

# Fractions of (width, height) so these survive a resolution change.
REGIONS = {
    # MEASURED against a real 1577x981 combat frame (evidence/cmb11_pacifist_full.png), not guessed.
    # An earlier battery captured "portraits" and got stonework and "party" and got bare floor --
    # and is_blank did NOT flag either, because scenery is textured. A wrong rectangle is worse than
    # a blank one: it produces confident evidence of nothing. Re-measure if the HUD ever moves.

    # THE BEST IDENTITY TELL. Class name in the menu header AND our uniquely-named abilities
    # ("Here's a Shield" / "Nerf Gun, Literally"), both legible in one crop -- so identity is
    # satisfied two independent ways. Prefer this over any other identity capture.
    # Widened UP from y=0.61 -> 0.55 on 2026-08-26: the menu grows DOWNWARD from a header that is
    # anchored to the BOTTOM of the screen, so a longer ability list pushes the header off the top
    # of the old rectangle. The Pokemon Trainer (9 rows) lost its "Pokemon Trainer" header entirely
    # while Gary and the Pacifist (8 rows) kept theirs -- i.e. the crop silently stopped being an
    # identity tell for exactly the class with the most abilities.
    "actionmenu": (0.79, 0.55, 0.21, 0.32),

    # Identity + effect together: all four class cards with names, HP, stat pips, portraits (status
    # icons render ON the portrait), plus the active-character banner and its full stat row.
    # Widened from y=0.83 -> 0.75; the old rectangle clipped the card tops, cutting off exactly the
    # status icons a status-skill effect tell depends on.
    "party":      (0.00, 0.75, 0.62, 0.25),

    # Ability preview panel: name, target scope, "On Perfect", per-slot/perfect %. The cheapest
    # proof the UI explains a skill, and the zero-damage tell (a normal ability shows "0-9 Magic
    # Damage" here; the Pacifist's shows no damage line at all).
    "preview":    (0.66, 0.70, 0.14, 0.16),

    # The ally-MINION card, upper-left. This is the SUMMON/CAPTURE tell -- a captured or summoned
    # creature appears here with its portrait and HP. NOTE it legitimately shows scenery when no
    # minion is out; that is an absent minion, NOT a broken capture. Do not read it as evidence
    # unless a minion is expected.
    "portraits":  (0.00, 0.07, 0.20, 0.20),

    "board":      (0.10, 0.30, 0.75, 0.45),   # play area -- creature identity, tile decals
    "enemy":      (0.78, 0.08, 0.22, 0.12),   # enemy nameplate, upper-right
    "toolbelt":   (0.72, 0.90, 0.28, 0.10),   # consumable slots (NOT the ability list -- see actionmenu)
    "panel":      (0.20, 0.10, 0.60, 0.80),   # a centred UI panel (class info tab, encyclopedia)
    "tooltip":    (0.25, 0.20, 0.50, 0.60),   # hover tooltip region
}


def is_blank(img, threshold=6.0):
    """A transition/loading frame is near-black. Capturing one produces 'evidence' of nothing.

    Measured hazard: a capture taken while TransitionUIDocument/LoadingScreenUIDocument is up
    returns an all-black frame. Filed unopened it looks like a real screenshot.
    """
    small = img.resize((64, 40), Image.BILINEAR)
    px = list(small.getdata())
    mean = sum(sum(p) for p in px) / (len(px) * 3.0)
    return mean < threshold


def _shot():
    payload = drive._post("/screenshot", {})
    raw = base64.b64decode(payload.get("base64") or "")
    return Image.open(io.BytesIO(raw)).convert("RGB")


def crop_for(img, region):
    fx, fy, fw, fh = REGIONS[region]
    w, h = img.size
    return img.crop((int(fx * w), int(fy * h), int((fx + fw) * w), int((fy + fh) * h)))


def grab(regions=("full",), wait_seconds=45):
    """Screenshot, but never one whose REQUESTED CROPS are blank.

    The check must run on the crop, not the full frame. A loading screen with any bright element
    -- a logo, a progress bar -- lifts the full-frame mean above threshold while the board region
    is still pure black, so a full-frame check passes and files a black crop as evidence. That
    happened: soak_passB_board.png is 100% black and the guard did not catch it.
    """
    import time
    deadline = time.time() + wait_seconds
    while True:
        img = _shot()
        bad = [r for r in regions
               if is_blank(img if r == "full" else crop_for(img, r))]
        if not bad:
            return img
        if time.time() > deadline:
            print("WARNING: %s still blank after %ds -- the game is probably mid-transition. "
                  "DO NOT treat this as evidence." % (", ".join(bad), wait_seconds))
            return img
        time.sleep(2)


def save(img, label, region, scale=3):
    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)
    if region == "full":
        path = os.path.join(OUT_DIR, "%s_full.png" % label)
        img.save(path)
        return path, img.size
    crop = crop_for(img, region)
    crop = crop.resize((crop.width * scale, crop.height * scale), Image.LANCZOS)
    path = os.path.join(OUT_DIR, "%s_%s.png" % (label, region))
    crop.save(path)
    return path, crop.size


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    label = sys.argv[1]
    regions = sys.argv[2:] or ["full", "board", "portraits"]
    img = grab(regions)
    for region in regions:
        if region != "full" and region not in REGIONS:
            print("unknown region %r; known: full, %s" % (region, ", ".join(sorted(REGIONS))))
            continue
        blank = is_blank(img if region == "full" else crop_for(img, region))
        path, size = save(img, label, region)
        print("%s  (%dx%d)%s" % (path, size[0], size[1],
                                 "   <-- BLANK, NOT EVIDENCE" if blank else ""))


if __name__ == "__main__":
    main()
