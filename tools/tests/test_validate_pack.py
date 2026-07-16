import copy
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from validate_pack import validate_pack  # noqa: E402

VOCAB = {
    "ItemIds": ["BLADE_MILITIA_LIGHT_01", "ARMOR_DONOR_01"],
    "AllIds": ["BLADE_MILITIA_LIGHT_01", "ARMOR_DONOR_01", "SWORD_BASIC_ATTACK",
               "SKILL_TESTPROC", "STATUS_TEST_BUFF", "CHAR_TEST_GOBLIN"],
    "Abilities": ["SWORD_BASIC_ATTACK"],
    "Tags": {"DROPPABLE": 2, "WEAPON": 2, "BLADE": 1, "MELEE": 1, "COMMON": 1,
             "TOWN_MARKET": 1},
    "Skills": ["SKILL_TESTPROC"],
    "Statuses": ["STATUS_TEST_BUFF"],
    "Slots": ["MAIN_HAND", "ARMOR", "TRINKET"],
    "Classes": ["BLADE", "ARMOR_BODY", "ARMOR_TRINKET"],
    "Rarities": ["COMMON", "UNCOMMON", "RARE", "ARTIFACT", "NONE"],
    "Materials": ["METAL", "CLOTH", "NONE"],
    "StatKeys": ["ATK", "CRT", "DEF", "LCK"],
    "StatCurves": {
        "BLADE|COMMON": {
            "count": 5,
            "stats": {"ATK": {"min": 6, "max": 26, "mean": 15, "p50": 14}},
            "value": {"min": 5, "max": 227, "mean": 72, "p50": 40},
        }
    },
    "TierBands": {},
    "VisualDonors": {"BLADE": ["BLADE_MILITIA_LIGHT_01"]},
}

SPRITES = {"PRERENDER_X": {"file": "sprites/PRERENDER_X.png", "w": 64, "h": 64, "source": "t"}}


def good_entry():
    return {
        "Id": "ARM_TEST_BLADE_01",
        "Thing": {
            "ConsumableType": "NONE",
            "Value": 40,
            "MinTier": 0,
            "MaxTier": 2,
            "Class": "BLADE",
            "Rarity": "COMMON",
            "Material": "METAL",
            "Hidden": False,
            "Stacks": False,
            "Equippable": {
                "Slots": ["MAIN_HAND"],
                "Stats": {"ATK": 12, "CRT": 5},
                "Passives": ["SKILL_TESTPROC"],
            },
            "Interactable": {
                "Abilities": {
                    "SWORD_BASIC_ATTACK": {"MinValue": 0, "MaxValue": 1, "ACC": 0,
                                           "Stat": "STR", "Rolls": 3, "Ammo": 0}
                },
                "AbilityBag": ["SWORD_BASIC_ATTACK"],
            },
            "Tags": ["DROPPABLE", "WEAPON", "BLADE", "MELEE", "COMMON"],
            "Expansion": "BASE",
        },
        "VisualFallback": "BLADE_MILITIA_LIGHT_01",
        "Loc": {"Name": "Test Blade", "Description": "A test."},
        "Icon": {"Base": "PRERENDER_X", "Transforms": []},
        "DesignNote": "test",
        "Provenance": {"Source": "handcrafted"},
    }


def pack_of(*entries):
    return {"PackId": "ARM_TEST_PACK", "Items": list(entries)}


def errors(findings):
    return [f for f in findings if f["level"] == "ERROR"]


def checks(findings):
    return {f["check"] for f in findings}


def test_valid_entry_passes():
    findings = validate_pack(pack_of(good_entry()), VOCAB, SPRITES)
    assert errors(findings) == []


def test_bad_prefix():
    e = good_entry()
    e["Id"] = "XX_TEST_BLADE"
    assert "bad_prefix" in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES)))


def test_dup_id_within_pack_and_vs_game():
    e1, e2 = good_entry(), good_entry()
    assert "dup_id" in checks(errors(validate_pack(pack_of(e1, e2), VOCAB, SPRITES)))
    e3 = good_entry()
    e3["Id"] = "BLADE_MILITIA_LIGHT_01"  # collides with live game id (and bad prefix)
    assert "dup_id" in checks(errors(validate_pack(pack_of(e3), VOCAB, SPRITES)))


def test_unknown_tag():
    e = good_entry()
    e["Thing"]["Tags"].append("TOTALLY_MADE_UP_TAG")
    assert "unknown_tag" in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES)))


def test_unknown_skill_status_stat_slot_class_rarity_material():
    for mutate, check in [
        (lambda t: t["Equippable"]["Passives"].append("SKILL_NOPE"), "unknown_passive"),
        (lambda t: t["Equippable"]["Stats"].update({"NOT_A_STAT": 1}), "unknown_stat_key"),
        (lambda t: t["Equippable"].update({"Slots": ["NOT_A_SLOT"]}), "unknown_slot"),
        (lambda t: t.update({"Class": "NOT_A_CLASS"}), "unknown_class"),
        (lambda t: t.update({"Rarity": "EPIC"}), "unknown_rarity"),
        (lambda t: t.update({"Material": "PLASTIC"}), "unknown_material"),
    ]:
        e = good_entry()
        mutate(e["Thing"])
        assert check in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES))), check


def test_unknown_ability_reference():
    e = good_entry()
    e["Thing"]["Interactable"]["AbilityBag"].append("ABILITY_NOPE")
    assert "unknown_ability" in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES)))


def test_empty_slots_rejected():
    e = good_entry()
    e["Thing"]["Equippable"]["Slots"] = []
    assert "empty_slots" in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES)))


def test_missing_visual_fallback():
    e = good_entry()
    e["VisualFallback"] = "NOT_AN_ITEM"
    assert "unknown_visual_fallback" in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES)))
    e2 = good_entry()
    del e2["VisualFallback"]
    assert "missing_visual_fallback" in checks(errors(validate_pack(pack_of(e2), VOCAB, SPRITES)))


def test_missing_loc():
    e = good_entry()
    e["Loc"] = {"Name": "X"}
    assert "missing_loc" in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES)))


def test_budget_warn_and_error():
    e = good_entry()
    e["Thing"]["Equippable"]["Stats"]["ATK"] = 30  # > p50*1.5 = 21, < max*2 = 52
    fw = validate_pack(pack_of(e), VOCAB, SPRITES)
    assert "budget_warn" in checks(fw)
    assert errors(fw) == []
    e2 = good_entry()
    e2["Thing"]["Equippable"]["Stats"]["ATK"] = 60  # > max*2 = 52
    assert "budget_error" in checks(errors(validate_pack(pack_of(e2), VOCAB, SPRITES)))


def test_icon_base_must_resolve_when_sprites_given():
    e = good_entry()
    e["Icon"]["Base"] = "NOT_A_SPRITE"
    assert "unknown_icon_base" in checks(errors(validate_pack(pack_of(e), VOCAB, SPRITES)))
    # without a sprite index, icon checks are skipped
    assert errors(validate_pack(pack_of(e), VOCAB, None)) == []


def test_rarity_none_equippable_warns():
    e = good_entry()
    e["Thing"]["Rarity"] = "NONE"
    fw = validate_pack(pack_of(e), VOCAB, SPRITES)
    assert "rarity_none" in checks(fw)
    assert errors(fw) == []
