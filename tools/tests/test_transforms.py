import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from iconforge.transforms import apply_transforms  # noqa: E402


def _red_square(size=8):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    for x in range(2, 6):
        for y in range(2, 6):
            img.putpixel((x, y), (255, 0, 0, 255))
    return img


def test_hsv_shift_red_to_cyan():
    out = apply_transforms(_red_square(), [{"Op": "HSV_SHIFT", "H": 0.5, "S": 0.0, "V": 0.0}])
    r, g, b, a = out.getpixel((3, 3))
    assert a == 255
    assert b > 200 and g > 200 and r < 60  # cyan
    assert out.getpixel((0, 0))[3] == 0  # transparent corner untouched


def test_palette_map_exact_color():
    out = apply_transforms(
        _red_square(),
        [{"Op": "PALETTE_MAP", "From": "#ff0000", "To": "#123456", "Tolerance": 8}],
    )
    assert out.getpixel((3, 3))[:3] == (0x12, 0x34, 0x56)


def test_glow_preserves_center_adds_halo():
    img = _red_square()
    out = apply_transforms(img, [{"Op": "GLOW", "Color": "#00ff00", "Radius": 2, "Strength": 1.0}])
    assert out.getpixel((3, 3)) == (255, 0, 0, 255)  # center pixel unchanged
    assert out.getpixel((1, 1))[3] > 0  # halo alpha where it was transparent
    assert img.getpixel((1, 1))[3] == 0  # input not mutated


def test_tint_silhouette():
    out = apply_transforms(
        _red_square(), [{"Op": "TINT_SILHOUETTE", "Color": "#0000ff", "Alpha": 1.0}]
    )
    r, g, b, a = out.getpixel((3, 3))
    assert (r, g, b) == (0, 0, 255) and a == 255


def test_outline():
    out = apply_transforms(_red_square(), [{"Op": "OUTLINE", "Color": "#ffffff", "Width": 1}])
    assert out.getpixel((1, 3))[:3] == (255, 255, 255)  # outline pixel next to shape
    assert out.getpixel((3, 3))[:3] == (255, 0, 0)  # interior intact


def test_overlay_and_composite(tmp_path):
    stamp = Image.new("RGBA", (4, 4), (0, 255, 0, 255))
    stamp_path = tmp_path / "STAMP.png"
    stamp.save(stamp_path)
    sprites = {"STAMP": {"file": "STAMP.png", "w": 4, "h": 4, "source": "t"}}
    out = apply_transforms(
        _red_square(),
        [{"Op": "OVERLAY", "Key": "STAMP", "Anchor": "NW", "Scale": 0.5, "Alpha": 1.0}],
        sprite_index=sprites,
        sprite_root=tmp_path,
    )
    assert out.getpixel((0, 0))[:3] == (0, 255, 0)  # stamp landed at NW
    assert out.getpixel((5, 5))[:3] == (255, 0, 0)  # shape interior intact


def test_determinism():
    recipe = [
        {"Op": "HSV_SHIFT", "H": 0.23, "S": 0.1, "V": -0.05},
        {"Op": "GLOW", "Color": "#ffcc00", "Radius": 2, "Strength": 0.8},
        {"Op": "OUTLINE", "Color": "#222222", "Width": 1},
    ]
    a = apply_transforms(_red_square(), recipe).tobytes()
    b = apply_transforms(_red_square(), recipe).tobytes()
    assert a == b


def test_unknown_op_raises():
    import pytest

    with pytest.raises(ValueError, match="NOT_AN_OP"):
        apply_transforms(_red_square(), [{"Op": "NOT_AN_OP"}])
