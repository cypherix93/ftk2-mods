import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from itemforge.forge import forge_items  # noqa: E402
from validate_pack import validate_pack  # noqa: E402
from tests.test_validate_pack import VOCAB as BASE_VOCAB  # noqa: E402

VOCAB = json.loads(json.dumps(BASE_VOCAB))  # deep copy
VOCAB["ClassSlots"] = {"BLADE": ["MAIN_HAND"]}
VOCAB["ClassAbilityTemplates"] = {
    "BLADE": {
        "Abilities": {
            "SWORD_BASIC_ATTACK": {"MinValue": 0, "MaxValue": 1, "ACC": 0,
                                   "Stat": "STR", "Rolls": 3, "Ammo": 0}
        },
        "AbilityBag": ["SWORD_BASIC_ATTACK"],
    }
}
VOCAB["StatCurves"]["BLADE|COMMON"]["stats"]["CRT"] = {
    "min": 2, "max": 8, "mean": 5, "p50": 5
}

PROFILE = {
    "Name": "test",
    "TierRange": [0, 2],
    "Rarities": {"COMMON": 100},
    "Classes": {"BLADE": 1},
    "BudgetMultiplier": 1.0,
    "Chaos": 0.10,
    "SynergyBias": 0.5,
    "CurseChance": 0.0,
    "PassiveBudgetShare": 0.35,
    "SkillPrices": {"SKILL_TESTPROC": 0.3},
    "Archetypes": {
        "BLADE": {
            "duelist": {"W": 1, "Stats": {"ATK": 2, "CRT": 3}},
            "brute": {"W": 1, "Stats": {"ATK": 5}},
        }
    },
    "AffixThemes": {
        "keen": {"Stats": {"CRT": 2}, "Skills": ["SKILL_TESTPROC"], "Tags": []},
    },
    "CurseStats": {"LCK": 5},
    "CurseRebate": 0.3,
    "NameGrammar": {
        "Patterns": ["{adj} {noun}", "{noun} of {theme}"],
        "Adj": ["Ashen", "Gleaming"],
        "Theme": ["Embers", "Sorrow"],
        "Nouns": {"BLADE": ["Blade", "Sword"]},
    },
}


def test_determinism():
    a = forge_items(PROFILE, VOCAB, seed=42, count=20)
    b = forge_items(PROFILE, VOCAB, seed=42, count=20)
    assert json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True)


def test_different_seeds_differ():
    a = forge_items(PROFILE, VOCAB, seed=1, count=20)
    b = forge_items(PROFILE, VOCAB, seed=2, count=20)
    assert json.dumps(a, sort_keys=True) != json.dumps(b, sort_keys=True)


def test_manifest_valid():
    items = forge_items(PROFILE, VOCAB, seed=7, count=30)
    pack = {"PackId": "ARM_FORGE_TEST", "Items": items}
    findings = validate_pack(pack, VOCAB, sprite_index=None)
    assert [f for f in findings if f["level"] == "ERROR"] == []


def test_budget_adherence():
    items = forge_items(PROFILE, VOCAB, seed=7, count=30)
    curve = VOCAB["StatCurves"]["BLADE|COMMON"]["stats"]
    for it in items:
        for stat, val in it["Thing"]["Equippable"]["Stats"].items():
            if stat in curve and val > 0:
                assert val <= curve[stat]["max"] * 2, (it["Id"], stat, val)


def test_vocab_closure():
    items = forge_items(PROFILE, VOCAB, seed=7, count=30)
    for it in items:
        for tag in it["Thing"]["Tags"]:
            assert tag.startswith("ARM_") or tag in VOCAB["Tags"], tag
        for p in it["Thing"]["Equippable"].get("Passives", []):
            assert p in VOCAB["Skills"]


def test_curse_rebate():
    cursed_profile = dict(PROFILE, CurseChance=1.0)
    cursed = forge_items(cursed_profile, VOCAB, seed=7, count=10)
    plain = forge_items(dict(PROFILE, CurseChance=0.0), VOCAB, seed=7, count=10)
    def positive_total(item):
        return sum(v for v in item["Thing"]["Equippable"]["Stats"].values() if v > 0)
    assert all(any(v < 0 for v in c["Thing"]["Equippable"]["Stats"].values()) for c in cursed)
    assert sum(map(positive_total, cursed)) > sum(map(positive_total, plain))


def test_provenance_and_ids():
    items = forge_items(PROFILE, VOCAB, seed=9, count=5)
    ids = [it["Id"] for it in items]
    assert len(set(ids)) == 5
    for it in items:
        assert it["Id"].startswith("ARM_FRG_")
        assert it["Provenance"] == {"Source": "forge", "Profile": "test", "Seed": 9}
        assert it["Thing"]["Value"] >= 1  # Value 0 never drops (O-LOOT-04)
        assert 0 <= it["Thing"]["MinTier"] <= it["Thing"]["MaxTier"] <= 3  # O-LOOT-01
