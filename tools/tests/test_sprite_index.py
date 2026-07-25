import json
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from extract_assets import write_index  # noqa: E402


def test_index_entry_shape(tmp_path):
    (tmp_path / "sprites").mkdir()
    img = Image.new("RGBA", (64, 64), (200, 30, 30, 255))
    img.save(tmp_path / "sprites" / "BLADE_TEST_01.png")
    entries = {
        "BLADE_TEST_01": {
            "file": "sprites/BLADE_TEST_01.png",
            "w": 64,
            "h": 64,
            "source": "archive/bundle_x",
        }
    }
    write_index(entries, tmp_path)
    idx = json.loads((tmp_path / "sprite-index.json").read_text(encoding="utf-8"))
    e = idx["BLADE_TEST_01"]
    assert set(e) == {"file", "w", "h", "source"}
    assert e["w"] > 0 and e["h"] > 0
    assert (tmp_path / e["file"]).is_file()


def test_index_sorted_and_stable(tmp_path):
    (tmp_path / "sprites").mkdir()
    entries = {
        "B": {"file": "sprites/B.png", "w": 1, "h": 1, "source": "s"},
        "A": {"file": "sprites/A.png", "w": 1, "h": 1, "source": "s"},
    }
    write_index(entries, tmp_path)
    text = (tmp_path / "sprite-index.json").read_text(encoding="utf-8")
    assert text.index('"A"') < text.index('"B"')
