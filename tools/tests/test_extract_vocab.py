import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from extract_vocab import build_index  # noqa: E402


def write_fixtures(root: Path) -> None:
    things = root / "Things"
    things.mkdir(parents=True)
    weapons = {
        "BLADE_TEST_01": {
            "ConsumableType": "NONE",
            "Value": 25,
            "MinTier": 0,
            "MaxTier": 2,
            "Class": "BLADE",
            "Rarity": "COMMON",
            "Material": "METAL",
            "Hidden": False,
            "Stacks": False,
            "Equippable": {
                "Slots": ["MAIN_HAND"],
                "Stats": {"ATK": 6, "CRT": 2},
                "Passives": ["SKILL_TESTPROC"],
            },
            "Interactable": {
                "Abilities": {
                    "SWORD_BASIC_ATTACK": {
                        "MinValue": 0, "MaxValue": 1, "ACC": 0,
                        "Stat": "STR", "Rolls": 3, "Ammo": 0,
                    }
                },
                "AbilityBag": ["SWORD_BASIC_ATTACK"],
            },
            "Tags": ["DROPPABLE", "WEAPON", "BLADE", "MELEE", "COMMON"],
        },
        "BLADE_TEST_02": {
            "Value": 90,
            "MinTier": 1,
            "MaxTier": 3,
            "Class": "BLADE",
            "Rarity": "COMMON",
            "Material": "METAL",
            "Equippable": {"Slots": ["MAIN_HAND"], "Stats": {"ATK": 10}},
            "Tags": ["DROPPABLE", "WEAPON", "BLADE", "TOWN_MARKET"],
        },
    }
    # Weapons.json gets a UTF-8 BOM on purpose: the live Characters.json has one,
    # and the extractor must tolerate it anywhere.
    (things / "Weapons.json").write_bytes(
        b"\xef\xbb\xbf" + json.dumps(weapons).encode("utf-8")
    )
    (root / "StatusEffects.json").write_text(
        json.dumps({"STATUS_TEST_BUFF": {"Type": "BUFF", "Duration": 2, "Stats": {"ATK": 2}}}),
        encoding="utf-8",
    )
    (root / "SkillConfigs.json").write_text(
        json.dumps({"SKILL_TESTPROC": {"Properties": [{"PROC_CHANCE": 0.25}]}}),
        encoding="utf-8",
    )
    (root / "Abilities.json").write_text(
        json.dumps({"SWORD_BASIC_ATTACK": {"TargetArea": "SINGLE", "Target": "ENEMY"}}),
        encoding="utf-8",
    )
    (root / "Characters.json").write_text(
        json.dumps({"CHAR_TEST_GOBLIN": {"Stats": {"HP": 20}, "Tags": []}}),
        encoding="utf-8",
    )


def test_build_index_shapes(tmp_path):
    write_fixtures(tmp_path)
    idx = build_index(tmp_path)
    assert "BLADE_TEST_01" in idx["ItemIds"]
    assert "CHAR_TEST_GOBLIN" in idx["AllIds"]
    assert "SWORD_BASIC_ATTACK" in idx["AllIds"]
    assert idx["Tags"]["DROPPABLE"] == 2
    assert "SKILL_TESTPROC" in idx["Skills"]
    assert "STATUS_TEST_BUFF" in idx["Statuses"]
    assert idx["Slots"] == ["MAIN_HAND"]
    assert idx["Classes"] == ["BLADE"]
    assert idx["Rarities"] == ["COMMON"]
    assert idx["Materials"] == ["METAL"]
    assert set(idx["StatKeys"]) == {"ATK", "CRT"}
    assert idx["TierBands"]["BLADE_TEST_01"] == {"MinTier": 0, "MaxTier": 2}


def test_stat_curves(tmp_path):
    write_fixtures(tmp_path)
    idx = build_index(tmp_path)
    curve = idx["StatCurves"]["BLADE|COMMON"]
    assert curve["count"] == 2
    assert curve["stats"]["ATK"]["min"] == 6
    assert curve["stats"]["ATK"]["max"] == 10
    assert curve["stats"]["ATK"]["mean"] == 8
    assert curve["value"]["min"] == 25
    assert curve["value"]["max"] == 90


def test_visual_donors(tmp_path):
    write_fixtures(tmp_path)
    idx = build_index(tmp_path)
    assert idx["VisualDonors"]["BLADE"] == ["BLADE_TEST_01", "BLADE_TEST_02"]


def test_handles_utf8_bom(tmp_path):
    write_fixtures(tmp_path)
    idx = build_index(tmp_path)  # Weapons.json carries a BOM; must not raise
    assert len(idx["ItemIds"]) == 2


def test_class_slots_and_ability_templates(tmp_path):
    write_fixtures(tmp_path)
    idx = build_index(tmp_path)
    assert idx["ClassSlots"]["BLADE"] == ["MAIN_HAND"]
    tpl = idx["ClassAbilityTemplates"]["BLADE"]
    assert "SWORD_BASIC_ATTACK" in tpl["Abilities"]
    assert tpl["AbilityBag"] == ["SWORD_BASIC_ATTACK"]


def test_deterministic_output(tmp_path):
    write_fixtures(tmp_path)
    a = json.dumps(build_index(tmp_path), sort_keys=True)
    b = json.dumps(build_index(tmp_path), sort_keys=True)
    assert a == b
