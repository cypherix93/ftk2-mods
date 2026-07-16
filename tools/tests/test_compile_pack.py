import json
import sys
from pathlib import Path

import pytest
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from compile_pack import compile_pack  # noqa: E402
from tests.test_validate_pack import VOCAB, good_entry, pack_of  # noqa: E402


def _icons_dir(tmp_path):
    d = tmp_path / "rendered"
    d.mkdir()
    Image.new("RGBA", (32, 32), (255, 0, 0, 255)).save(d / "ARM_TEST_BLADE_01.png")
    Image.new("RGBA", (32, 32), (0, 255, 0, 255)).save(d / "ARM_TEST_BLADE_02.png")
    return d


def two_item_pack():
    e1 = good_entry()
    e2 = good_entry()
    e2["Id"] = "ARM_TEST_BLADE_02"
    e2["Loc"] = {"Name": "Second Blade", "Description": "Another test."}
    return pack_of(e1, e2)


def test_compile_writes_ship_shape(tmp_path):
    out = tmp_path / "data"
    compile_pack(two_item_pack(), out, vocab=VOCAB, icons_dir=_icons_dir(tmp_path))

    things = json.loads((out / "Things" / "ARM_TEST_PACK.json").read_text(encoding="utf-8"))
    assert set(things) == {"ARM_TEST_BLADE_01", "ARM_TEST_BLADE_02"}
    assert things["ARM_TEST_BLADE_01"]["Class"] == "BLADE"
    assert "VisualFallback" not in things["ARM_TEST_BLADE_01"]  # manifest-only key stripped

    vf = json.loads((out / "VisualFallbacks.json").read_text(encoding="utf-8"))
    assert vf["ARM_TEST_BLADE_01"] == "BLADE_MILITIA_LIGHT_01"

    loc = json.loads((out / "Localization" / "en.json").read_text(encoding="utf-8"))
    assert loc["ARM_TEST_BLADE_01"] == "Test Blade"
    assert loc["ARM_TEST_BLADE_01_DESCRIPTION"] == "A test."

    assert (out / "icons" / "ARM_TEST_BLADE_01.png").is_file()
    assert (out / "icons" / "ARM_TEST_BLADE_02.png").is_file()


def test_merge_append_preserves_existing(tmp_path):
    out = tmp_path / "data"
    out.mkdir()
    (out / "VisualFallbacks.json").write_text(
        json.dumps({"ARM_OLD_ITEM": "ARMOR_DONOR_01"}), encoding="utf-8"
    )
    (out / "Localization").mkdir()
    (out / "Localization" / "en.json").write_text(
        json.dumps({"ARM_OLD_ITEM": "Old"}), encoding="utf-8"
    )
    compile_pack(two_item_pack(), out, vocab=VOCAB, icons_dir=_icons_dir(tmp_path))
    vf = json.loads((out / "VisualFallbacks.json").read_text(encoding="utf-8"))
    assert vf["ARM_OLD_ITEM"] == "ARMOR_DONOR_01"
    assert vf["ARM_TEST_BLADE_01"] == "BLADE_MILITIA_LIGHT_01"
    loc = json.loads((out / "Localization" / "en.json").read_text(encoding="utf-8"))
    assert loc["ARM_OLD_ITEM"] == "Old"
    assert loc["ARM_TEST_BLADE_02"] == "Second Blade"


def test_refuses_invalid_pack(tmp_path):
    pack = two_item_pack()
    pack["Items"][0]["VisualFallback"] = "NOT_AN_ITEM"
    with pytest.raises(SystemExit):
        compile_pack(pack, tmp_path / "data", vocab=VOCAB, icons_dir=_icons_dir(tmp_path))


def test_missing_icon_file_is_tolerated(tmp_path):
    # Icon block present but PNG not rendered yet -> warn, still compiles JSON.
    out = tmp_path / "data"
    d = tmp_path / "empty"
    d.mkdir()
    compile_pack(pack_of(good_entry()), out, vocab=VOCAB, icons_dir=d)
    assert (out / "Things" / "ARM_TEST_PACK.json").is_file()
    assert not (out / "icons" / "ARM_TEST_BLADE_01.png").exists()
