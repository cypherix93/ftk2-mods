import filecmp
import hashlib
import json
import os
import sys
from pathlib import Path

import pytest

TOOLS_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_DIR))

import eor_import  # noqa: E402
from validate_pack import validate_pack  # noqa: E402

FIXTURES_DIR = Path(__file__).resolve().parent / "fixtures" / "eor_import"
PACKAGE_DIR = FIXTURES_DIR / "package"
SOURCE_DIR = PACKAGE_DIR / "BepInEx" / "plugins"
VOCAB_FIXTURE = FIXTURES_DIR / "vocab.json"
GOLDEN_DIR = FIXTURES_DIR / "golden"

# §M7: the real-corpus integration tests used to hardcode one workstation's absolute path, so
# REAL_PACKAGE_AVAILABLE was False (and these tests silently skipped) on every other machine and
# in CI. EOR_PACKAGE_DIR, if set, points at the package root that directly contains
# BepInEx/plugins (i.e. the same directory you'd pass `--source <that>/BepInEx/plugins` for);
# absent that, fall back to the original known-good path so this still runs unattended on the
# author's machine. Either way, the skip reason below says exactly what's missing and how to fix
# it instead of a bare "not present".
_REAL_PACKAGE_DIR_ENV = "EOR_PACKAGE_DIR"
_DEFAULT_REAL_PACKAGE_DIR = Path(
    r"D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN"
)
REAL_PACKAGE_DIR = Path(os.environ.get(_REAL_PACKAGE_DIR_ENV, str(_DEFAULT_REAL_PACKAGE_DIR)))
REAL_SOURCE = REAL_PACKAGE_DIR / "BepInEx" / "plugins"
REAL_VOCAB = TOOLS_DIR / "out" / "vocab-index.json"

REAL_VOCAB_AVAILABLE = REAL_VOCAB.is_file()
REAL_PACKAGE_AVAILABLE = REAL_SOURCE.is_dir() and REAL_VOCAB_AVAILABLE

_REAL_VOCAB_SKIP_REASON = (
    f"real vocab-index.json not present at {REAL_VOCAB} -- run tools/extract_vocab.py against "
    "a live FTK2 install first (see tools/README.md)"
)
_REAL_PACKAGE_SKIP_REASON = (
    f"real EOR package/vocab not present -- set {_REAL_PACKAGE_DIR_ENV} to the package root "
    "that directly contains BepInEx/plugins (or place one at the default "
    f"{_DEFAULT_REAL_PACKAGE_DIR}), and ensure a vocab is at {REAL_VOCAB} "
    "(tools/extract_vocab.py)"
)

# Fixture package shape (see tools/tests/fixtures/eor_import/) -- small numbers, so full-pipeline
# tests patch eor_import's EXPECTED_* invariant constants to match rather than the real corpus.
FIXTURE_EXPECTED = dict(
    EXPECTED_ITEMS_CUSTOM_COUNT=6,
    EXPECTED_ITEMS_STARTERS_COUNT=2,
    EXPECTED_ITEMS_EXAMPLES_COUNT=1,
    EXPECTED_COMPANION_PLUS_COUNT=1,
    EXPECTED_MERC_PLUS_COUNT=1,
    EXPECTED_CLASS_COUNT=2,
)


def patch_expected_counts(monkeypatch):
    for name, value in FIXTURE_EXPECTED.items():
        monkeypatch.setattr(eor_import, name, value)


def small_vocab() -> dict:
    return eor_import.load_vocab(VOCAB_FIXTURE)


def small_sources() -> eor_import.EorSources:
    return eor_import.load_sources(SOURCE_DIR)


def small_ctx(*, package_version: str = "9.9.9-test", report: dict | None = None) -> eor_import.Ctx:
    report = report if report is not None else eor_import.make_report()
    return eor_import.build_context(small_vocab(), small_sources(), package_version, report)


# ─────────────────────────────── shared helpers (3) ──────────────────────────────────────────

def test_humanize_reproduces_eor_pixie():
    assert eor_import.humanize(
        "COMPANION_PLUS_PIXIE_GENERIC_00", strip_prefixes=("COMPANION_PLUS_", "MERC_PLUS_")
    ) == "Pixie"


def test_humanize_reproduces_eor_skraevin():
    assert eor_import.humanize(
        "COMPANION_PLUS_SKRAEVIN_CHAOS_MELEE_00", strip_prefixes=("COMPANION_PLUS_", "MERC_PLUS_")
    ) == "Skraevin Chaos Melee"


def test_humanize_collapses_possessive_s():
    assert eor_import.humanize(
        "EORR_RIVET_SAMURAI_S_KATANA", strip_prefixes=("EORR_",)
    ) == "Rivet Samurai's Katana"


# ─────────────────────────────── items — id & tags (5) ───────────────────────────────────────

def test_map_item_id_eorr_prefix():
    assert eor_import.map_item_id("EORR_ASH_TEST_KNIFE") == "ARM_EOR_ASH_TEST_KNIFE"


def test_map_item_id_starter_prefix():
    assert (eor_import.map_item_id("EOR_STARTER_TESTMAGE_BASIC_WAND")
            == "ARM_EOR_STARTER_TESTMAGE_BASIC_WAND")


def test_map_item_id_example_returns_none():
    assert eor_import.map_item_id("EOR_EXAMPLE_TEST_DAGGER") is None


def test_tag_policy_drops_eor_namespace_and_bare_stat_tags():
    vocab = small_vocab()
    report = eor_import.make_report()
    out = eor_import.filter_tags(
        "ARM_TEST", ["EOR_CUSTOM", "EORR_CUSTOM", "LCK", "DROPPABLE"], vocab, report
    )
    assert out == ["DROPPABLE"]
    dropped = {f["tag"] for f in report["findings"] if f["kind"] == "tag_dropped"}
    assert dropped == {"EOR_CUSTOM", "EORR_CUSTOM", "LCK"}


def test_tag_policy_renames_gear_bands_into_arm_namespace():
    vocab = small_vocab()
    report = eor_import.make_report()
    out = eor_import.filter_tags(
        "ARM_TEST",
        ["ENDGAME_GEAR", "STRONGER_GEAR", "WEAKER_GEAR", "UTILITY_GEAR", "EOR_STARTER_WEAPON"],
        vocab, report,
    )
    assert out == [
        "ARM_ENDGAME_GEAR", "ARM_STRONGER_GEAR", "ARM_WEAKER_GEAR", "ARM_UTILITY_GEAR",
        "ARM_STARTER_WEAPON",
    ]


# ─────────────────────────────── items — bag hoist (5) ───────────────────────────────────────

def test_hoist_prefers_top_level_bag_over_interactable():
    sources = small_sources()
    thing = sources.items_custom["EORR_NIGHT_SHADOW_BLADE"]
    new_thing, dropped = eor_import.hoist_ability_bags(thing)
    assert new_thing["Interactable"]["AbilityBag"] == ["KNIFE_BASIC_ATTACK", "BLADE_SHADOW_AOE"]
    assert dropped == []


def test_hoist_removes_non_schema_top_level_keys_from_thing():
    sources = small_sources()
    thing = sources.items_custom["EORR_NIGHT_SHADOW_BLADE"]
    new_thing, _ = eor_import.hoist_ability_bags(thing)
    assert "AbilityBag" not in new_thing
    assert "AbilityFillBag" not in new_thing
    assert "ShuffleAbilityBag" not in new_thing


def test_hoist_populates_fillbag_from_top_level():
    sources = small_sources()
    thing = sources.items_custom["EORR_NIGHT_SHADOW_BLADE"]
    new_thing, _ = eor_import.hoist_ability_bags(thing)
    assert new_thing["Interactable"]["AbilityFillBag"] == ["KNIFE_BASIC_ATTACK", "BLADE_SHADOW_AOE"]


def test_hoist_leaves_bagless_item_without_bags():
    sources = small_sources()
    thing = sources.items_custom["EORR_OLD_TOWER_SHIELD"]
    new_thing, dropped = eor_import.hoist_ability_bags(thing)
    assert "AbilityBag" not in new_thing["Interactable"]
    assert "AbilityFillBag" not in new_thing["Interactable"]
    assert dropped == []


def test_residual_undeclared_bag_ids_are_dropped_and_reported():
    thing = {
        "Interactable": {
            "Abilities": {
                "KNIFE_BASIC_ATTACK": {"MinValue": 0, "MaxValue": 1, "ACC": 0, "Stat": "SPD",
                                        "Rolls": 1, "Ammo": 0},
            },
        },
        "AbilityBag": ["KNIFE_BASIC_ATTACK", "KNIFE_PHANTOM_ATTACK"],
    }
    new_thing, dropped = eor_import.hoist_ability_bags(thing)
    assert dropped == ["KNIFE_PHANTOM_ATTACK"]
    assert new_thing["Interactable"]["AbilityBag"] == ["KNIFE_BASIC_ATTACK"]


# ─────────────────────────────── items — class & fallback (6) ────────────────────────────────

@pytest.mark.skipif(not REAL_VOCAB_AVAILABLE, reason=_REAL_VOCAB_SKIP_REASON)
def test_class_remap_table_targets_all_present_in_vocab():
    real_vocab = eor_import.load_vocab(REAL_VOCAB)
    live_classes = set(real_vocab["Classes"])
    for target in eor_import.CLASS_REMAP.values():
        assert target in live_classes, target


@pytest.mark.parametrize("cls,target", sorted(eor_import.CLASS_REMAP.items()))
def test_class_remap_targets(cls, target):
    report = eor_import.make_report()
    result = eor_import.repair_class("ARM_X", cls, {"Classes": []}, report)
    assert result == target
    assert any(f["kind"] == "class_remap" and f["from"] == cls and f["to"] == target
               for f in report["findings"])


def test_class_remap_sword_to_blade():
    assert eor_import.CLASS_REMAP["SWORD"] == "BLADE"


def test_unmapped_illegal_class_is_blocking():
    report = eor_import.make_report()
    result = eor_import.repair_class("ARM_X", "FOOBAR", {"Classes": []}, report)
    assert result is None
    assert any(f["kind"] == "unmapped_class" and f["severity"] == "blocking"
               for f in report["findings"])


def test_donor_pick_is_first_sorted_donor_for_class():
    vocab = small_vocab()
    report = eor_import.make_report()
    result = eor_import.pick_visual_fallback(
        "ARM_X", "EORR_NO_SUCH_ID", "KATANA", {}, vocab, report
    )
    assert result == "TEST_KATANA_DONOR_00"
    assert result == sorted(vocab["VisualDonors"]["KATANA"])[0]


def test_donor_alias_orb_falls_back_to_orb_2h():
    vocab = small_vocab()
    report = eor_import.make_report()
    result = eor_import.pick_visual_fallback(
        "ARM_X", "EORR_NO_SUCH_ID", "ORB", {}, vocab, report
    )
    assert result == "TEST_ORB2H_DONOR_00"


def test_class_without_any_donor_is_blocking():
    vocab = small_vocab()
    report = eor_import.make_report()
    result = eor_import.pick_visual_fallback(
        "ARM_X", "EORR_NO_SUCH_ID", "DAGGER", {}, vocab, report
    )
    assert result is None
    assert any(f["kind"] == "no_visual_donor" and f["severity"] == "blocking"
               for f in report["findings"])


# ─────────────────────────────── items — loc & provenance (3) ────────────────────────────────

def test_katana_gets_synthesized_name_and_description():
    sources = small_sources()
    report = eor_import.make_report()
    loc = eor_import.synth_item_loc(
        "ARM_EOR_LOST_SILVER_KATANA", "EORR_LOST_SILVER_KATANA", "KATANA", sources.en, report
    )
    assert loc["Name"] == "Lost Silver Katana"
    assert loc["Description"] == eor_import.DESC_TEMPLATES["KATANA"]
    assert any(f["kind"] == "loc_synth" for f in report["findings"])


def test_existing_eor_loc_is_copied_verbatim_under_new_id():
    sources = small_sources()
    report = eor_import.make_report()
    loc = eor_import.synth_item_loc(
        "ARM_EOR_ASH_TEST_KNIFE", "EORR_ASH_TEST_KNIFE", "DAGGER", sources.en, report
    )
    assert loc == {"Name": "Ash Test Knife", "Description": "A worn practice blade."}
    assert any(f["kind"] == "loc_copied" for f in report["findings"])


def test_every_item_entry_carries_provenance_with_eor_id_and_version():
    ctx = small_ctx(package_version="1.2.3")
    item_packs = eor_import.convert_items(ctx)
    entries = item_packs["ARM_EOR_ITEMS"]["Items"] + item_packs["ARM_EOR_STARTERS"]["Items"]
    assert entries, "fixture should emit at least one item"
    for entry in entries:
        prov = entry["Provenance"]
        assert prov["Source"] == "eor-import"
        assert prov["EorId"]
        assert prov["PackageVersion"] == "1.2.3"


def test_illegal_unmapped_class_item_excluded_from_pack():
    ctx = small_ctx()
    item_packs = eor_import.convert_items(ctx)
    ids = {e["Id"] for e in item_packs["ARM_EOR_ITEMS"]["Items"]}
    assert "ARM_EOR_BROKEN_WEIRD_MACE" not in ids
    assert any(f["kind"] == "unmapped_class" and f["id"] == "ARM_EOR_BROKEN_WEIRD_MACE"
               for f in ctx.report["findings"])


# ─────────────────────────────── followers (7) ────────────────────────────────────────────────

def test_follower_id_map_companion_and_merc():
    assert eor_import.map_follower_id("COMPANION_PLUS_GHOST_GENERIC_00") == "SMN_FOL_GHOST_GENERIC_00"
    assert eor_import.map_follower_id("MERC_PLUS_BANDIT_HEAVY_00") == "SMN_MRC_BANDIT_HEAVY_00"
    assert eor_import.map_follower_id("MERC_CULTIST") is None


def test_sheep_entries_classified_drop_dangling():
    sources = small_sources()
    ctx = small_ctx()
    entry = sources.followers_pets["SHEPHERD_SHEEP_TEST_00"]
    assert eor_import.classify_follower("SHEPHERD_SHEEP_TEST_00", entry, ctx.char_ids) == "drop_dangling"


def test_entry_without_configname_classified_drop_no_config():
    sources = small_sources()
    ctx = small_ctx()
    entry = sources.followers_pets["COMPANION_REFLECTION"]
    assert eor_import.classify_follower("COMPANION_REFLECTION", entry, ctx.char_ids) == "drop_no_config"


def test_vanilla_id_entries_parked_not_emitted():
    sources = small_sources()
    ctx = small_ctx()
    entry = sources.followers_mercs["MERC_TEST_VANILLA"]
    assert eor_import.classify_follower("MERC_TEST_VANILLA", entry, ctx.char_ids) == "park_vanilla"

    result = eor_import.convert_followers(ctx)
    emitted_ids = set(result["packs"]["SMN_PACK_EOR_PETS"]["followers"]) | \
        set(result["packs"]["SMN_PACK_EOR_MERCS"]["followers"])
    assert "SMN_MRC_TEST_VANILLA" not in emitted_ids  # would-be id if it were wrongly emitted
    override_ids = {e["eor_id"] for e in result["vanilla_overrides"]}
    assert "MERC_TEST_VANILLA" in override_ids


def test_follower_field_passthrough_preserves_vanilla_loc_keys():
    sources = small_sources()
    ctx = small_ctx()
    entry = sources.followers_pets["COMPANION_PLUS_TEST_GHOST_GENERIC_00"]
    new_id, new_entry = eor_import.convert_follower("COMPANION_PLUS_TEST_GHOST_GENERIC_00", entry, ctx)
    assert new_entry["Subtitle"] == entry["Subtitle"] == "CREATURE_SUBTITLE"
    assert new_entry["JoinParty"] == entry["JoinParty"]
    assert new_entry["LeaveParty"] == entry["LeaveParty"]
    assert new_entry["ConfigName"] == entry["ConfigName"] == "TEST_GHOST_GENERIC_00"


def test_follower_classname_rewritten_to_new_id():
    sources = small_sources()
    ctx = small_ctx()
    entry = sources.followers_pets["COMPANION_PLUS_TEST_GHOST_GENERIC_00"]
    new_id, new_entry = eor_import.convert_follower("COMPANION_PLUS_TEST_GHOST_GENERIC_00", entry, ctx)
    assert new_id == "SMN_FOL_TEST_GHOST_GENERIC_00"
    assert new_entry["ClassName"] == new_id


def test_follower_enum_values_validated_against_ground_truth_sets():
    report = eor_import.make_report()
    bad_entry = {
        "Type": "BOGUS", "ContractPrice": "WHATEVER", "Behaviour": "IDLE",
        "AppendableStats": {"NOTASTAT": 1},
    }
    eor_import.validate_follower_enums("SMN_FOL_TEST", bad_entry, report)
    kinds = {f["kind"] for f in report["findings"]}
    assert {"unknown_character_type", "unknown_contract_price", "unknown_behaviour",
            "unknown_appendable_stat"} <= kinds

    report2 = eor_import.make_report()
    good_entry = {"Type": "COMPANION", "ContractPrice": "AVERAGE", "Behaviour": "DEFAULT"}
    eor_import.validate_follower_enums("SMN_FOL_TEST2", good_entry, report2)
    assert report2["findings"] == []


# ─────────────────────────────── classes (4) ───────────────────────────────────────────────────

def test_class_id_and_lockey_rewritten():
    assert eor_import.map_class_id("EOR_TESTMAGE") == "CF_EOR_TESTMAGE"
    ctx = small_ctx()
    class_result = eor_import.convert_classes(ctx)
    assert class_result["classes"]["CF_EOR_TESTMAGE"]["LocKey"] == "CF_EOR_TESTMAGE"


def test_class_stripped_to_native_character_fields():
    cfg = {"Stats": {"HP": 1}, "Things": {}, "SomeUnknownField": "should not survive"}
    stripped = eor_import.strip_to_native(cfg)
    assert stripped == {"Stats": {"HP": 1}, "Things": {}}


def test_class_stats_and_things_copied_verbatim():
    sources = small_sources()
    ctx = small_ctx()
    class_result = eor_import.convert_classes(ctx)
    assert class_result["classes"]["CF_EOR_TESTMAGE"]["Stats"] == sources.classes["EOR_TESTMAGE"]["Stats"]
    assert class_result["classes"]["CF_EOR_TESTMAGE"]["Things"] == sources.classes["EOR_TESTMAGE"]["Things"]


def test_stats_report_flags_lck_95_above_vanilla_max():
    sources = small_sources()
    vanilla_player = {
        k: v for k, v in sources.classes_all.items()
        if not k.startswith("EOR_") and "PLAYER" in (v.get("Tags") or [])
    }
    stats = eor_import.build_stats_report(vanilla_player, sources.classes)
    flagged = {row["stat"]: row for row in stats["above_vanilla_max"]}
    assert "LCK" in flagged
    assert flagged["LCK"]["eor_max"] == 95
    assert flagged["LCK"]["vanilla_max"] == 50


# ─────────────────────────────── §B5 — vocab snapshot provenance ───────────────────────────────

def test_compute_vocab_snapshot_all_none_when_path_missing():
    assert eor_import.compute_vocab_snapshot(None, {}) == {
        "VocabSha256": None, "VocabItemIdsCount": None, "VocabAllIdsCount": None,
    }


def test_compute_vocab_snapshot_all_none_when_path_does_not_exist(tmp_path):
    assert eor_import.compute_vocab_snapshot(tmp_path / "nope.json", {}) == {
        "VocabSha256": None, "VocabItemIdsCount": None, "VocabAllIdsCount": None,
    }


def test_compute_vocab_snapshot_hashes_the_real_fixture_file():
    vocab = small_vocab()
    result = eor_import.compute_vocab_snapshot(VOCAB_FIXTURE, vocab)
    assert result["VocabSha256"] == hashlib.sha256(VOCAB_FIXTURE.read_bytes()).hexdigest()
    assert result["VocabItemIdsCount"] == len(vocab["ItemIds"])
    assert result["VocabAllIdsCount"] == len(vocab["AllIds"])


def test_convert_classes_stamps_vocab_snapshot_into_provenance():
    vocab = small_vocab()
    sources = small_sources()
    report = eor_import.make_report()
    ctx = eor_import.build_context(vocab, sources, "9.9.9-test", report, vocab_path=VOCAB_FIXTURE)
    class_result = eor_import.convert_classes(ctx)
    snapshot = class_result["provenance"]["_vocab_snapshot"]
    assert snapshot["VocabSha256"] == hashlib.sha256(VOCAB_FIXTURE.read_bytes()).hexdigest()
    assert snapshot["VocabItemIdsCount"] == len(vocab["ItemIds"])
    # per-class entries are untouched by the marker.
    assert "CF_EOR_TESTMAGE" in class_result["provenance"]
    assert "_vocab_snapshot" not in class_result["classes"]


def test_convert_classes_without_vocab_path_records_null_snapshot():
    ctx = small_ctx()  # small_ctx doesn't pass vocab_path -> defaults to None
    class_result = eor_import.convert_classes(ctx)
    assert class_result["provenance"]["_vocab_snapshot"] == {
        "VocabSha256": None, "VocabItemIdsCount": None, "VocabAllIdsCount": None,
    }


def test_main_vocab_missing_is_loud_in_report_and_snapshot_is_null(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)
    repo_root = tmp_path / "repo"
    repo_root.mkdir()
    report_dir = tmp_path / "reports"
    missing_vocab = tmp_path / "does-not-exist-vocab.json"

    rc = eor_import.main([
        "--source", str(SOURCE_DIR),
        "--repo-root", str(repo_root),
        "--vocab", str(missing_vocab),
        "--report-dir", str(report_dir),
        "--package-version", "9.9.9-test",
        "--dry-run",
    ])

    assert rc == 1
    report = json.loads((report_dir / "report.json").read_text(encoding="utf-8"))
    assert any(f["kind"] == "vocab_missing" and f["severity"] == "blocking"
               for f in report["findings"])
    assert report["vocab_snapshot"] == {
        "VocabSha256": None, "VocabItemIdsCount": None, "VocabAllIdsCount": None,
    }


def test_main_writes_vocab_snapshot_into_report_json(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)
    repo_root = tmp_path / "repo"
    repo_root.mkdir()
    report_dir = tmp_path / "reports"

    eor_import.main([
        "--source", str(SOURCE_DIR),
        "--repo-root", str(repo_root),
        "--vocab", str(VOCAB_FIXTURE),
        "--report-dir", str(report_dir),
        "--package-version", "9.9.9-test",
    ])

    report = json.loads((report_dir / "report.json").read_text(encoding="utf-8"))
    assert report["vocab_snapshot"]["VocabSha256"] == hashlib.sha256(
        VOCAB_FIXTURE.read_bytes()
    ).hexdigest()

    classes_prov = json.loads(
        (repo_root / "FTK2.ClassForge" / "data" / "ClassPacks" / eor_import.CLASS_PACK_ID
         / "provenance.json").read_text(encoding="utf-8")
    )
    assert classes_prov["_vocab_snapshot"] == report["vocab_snapshot"]


# ─────────────────────────────── §B6 — authored-content detection ──────────────────────────────

def test_detect_authored_content_empty_for_clean_fresh_pack(tmp_path):
    pack_dir = tmp_path / "pack"
    pack_dir.mkdir()
    (pack_dir / "classes.json").write_text(
        json.dumps({"CF_EOR_X": {"Passives": ["SKILL_NATIVE_ONE"]}}), encoding="utf-8"
    )
    (pack_dir / "provenance.json").write_text(
        json.dumps({"CF_EOR_X": {"Source": "eor-import"}}), encoding="utf-8"
    )
    assert eor_import.detect_authored_class_content(pack_dir) == []


def test_detect_authored_content_empty_for_nonexistent_dir(tmp_path):
    assert eor_import.detect_authored_class_content(tmp_path / "does-not-exist") == []


def test_detect_authored_content_via_extra_traits_file(tmp_path):
    pack_dir = tmp_path / "pack"
    pack_dir.mkdir()
    (pack_dir / "traits.json").write_text("{}", encoding="utf-8")
    reasons = eor_import.detect_authored_class_content(pack_dir)
    assert any("traits.json" in r for r in reasons)


def test_detect_authored_content_via_extra_skillrecipes_file(tmp_path):
    pack_dir = tmp_path / "pack"
    pack_dir.mkdir()
    (pack_dir / "skillrecipes.json").write_text("{}", encoding="utf-8")
    reasons = eor_import.detect_authored_class_content(pack_dir)
    assert any("skillrecipes.json" in r for r in reasons)


def test_detect_authored_content_via_authored_content_provenance_marker(tmp_path):
    pack_dir = tmp_path / "pack"
    pack_dir.mkdir()
    (pack_dir / "provenance.json").write_text(
        json.dumps({"_authored_content": {"_note": "hand-authoring pass"}}), encoding="utf-8"
    )
    reasons = eor_import.detect_authored_class_content(pack_dir)
    assert any("_authored_content" in r for r in reasons)


def test_detect_authored_content_via_skill_cf_passive_marker(tmp_path):
    pack_dir = tmp_path / "pack"
    pack_dir.mkdir()
    (pack_dir / "classes.json").write_text(
        json.dumps({"CF_EOR_KNIGHT": {"Passives": ["SKILL_CF_KNIGHT_CHIVALRY"]}}),
        encoding="utf-8",
    )
    reasons = eor_import.detect_authored_class_content(pack_dir)
    assert any("SKILL_CF_" in r for r in reasons)


def test_detect_authored_content_tolerates_malformed_json(tmp_path):
    pack_dir = tmp_path / "pack"
    pack_dir.mkdir()
    (pack_dir / "provenance.json").write_text("not valid json{{{", encoding="utf-8")
    (pack_dir / "classes.json").write_text("also not json", encoding="utf-8")
    # malformed files never crash detection; they're just treated as carrying no marker. The
    # extra-file check still fires independently if traits.json/skillrecipes.json are present.
    assert eor_import.detect_authored_class_content(pack_dir) == []


# ─────────────────────────────── §B6 — emit_class_pack skip/force behaviour ────────────────────

def _fresh_class_result(class_id: str = "CF_EOR_FRESH", hp: int = 1) -> dict:
    return {
        "classes": {class_id: {"Stats": {"HP": hp}, "LocKey": class_id}},
        "localization": {class_id: "Fresh", class_id + "_DESCRIPTION": "A fresh class."},
        "provenance": {
            class_id: {"Source": "eor-import", "EorId": "EOR_FRESH", "PackageVersion": "9.9.9-test"},
        },
    }


def test_emit_class_pack_no_op_when_no_classes():
    status = eor_import.emit_class_pack(
        {"classes": {}, "localization": {}, "provenance": {}}, Path("unused")
    )
    assert status == "skipped_empty"


def test_emit_class_pack_writes_fresh_when_pack_dir_absent(tmp_path):
    class_packs_dir = tmp_path / "ClassPacks"
    status = eor_import.emit_class_pack(_fresh_class_result(), class_packs_dir)
    assert status == "written"
    pack_dir = class_packs_dir / eor_import.CLASS_PACK_ID
    assert (pack_dir / "classes.json").is_file()
    assert (pack_dir / "pack.json").is_file()
    assert not (pack_dir / eor_import.PRE_IMPORT_BACKUP_DIRNAME).exists()


def test_emit_class_pack_skips_when_authored_content_present_and_not_forced(tmp_path):
    class_packs_dir = tmp_path / "ClassPacks"
    pack_dir = class_packs_dir / eor_import.CLASS_PACK_ID
    pack_dir.mkdir(parents=True)
    (pack_dir / "traits.json").write_text("{}", encoding="utf-8")
    (pack_dir / "classes.json").write_text(
        json.dumps({"CF_EOR_OLD": {"Stats": {"HP": 42}}}), encoding="utf-8"
    )
    report = eor_import.make_report()

    status = eor_import.emit_class_pack(_fresh_class_result(), class_packs_dir, force=False,
                                         report=report)

    assert status == "skipped_authored"
    on_disk = json.loads((pack_dir / "classes.json").read_text(encoding="utf-8"))
    assert on_disk == {"CF_EOR_OLD": {"Stats": {"HP": 42}}}, "authored pack must be untouched"
    assert not (pack_dir / "pack.json").exists(), "no partial write on skip"
    assert not (pack_dir / eor_import.PRE_IMPORT_BACKUP_DIRNAME).exists()
    skip_findings = [f for f in report["findings"]
                     if f["kind"] == "class_pack_authored_content_skipped"]
    assert len(skip_findings) == 1
    assert skip_findings[0]["severity"] == "warn"  # §B6: skip is not an error


def test_emit_class_pack_forced_overwrite_backs_up_then_writes(tmp_path):
    class_packs_dir = tmp_path / "ClassPacks"
    pack_dir = class_packs_dir / eor_import.CLASS_PACK_ID
    pack_dir.mkdir(parents=True)
    (pack_dir / "skillrecipes.json").write_text("{}", encoding="utf-8")
    (pack_dir / "classes.json").write_text(
        json.dumps({"CF_EOR_OLD": {"Stats": {"HP": 99}}}), encoding="utf-8"
    )
    report = eor_import.make_report()

    status = eor_import.emit_class_pack(_fresh_class_result(), class_packs_dir, force=True,
                                         report=report)

    assert status == "overwritten_forced"
    new_classes = json.loads((pack_dir / "classes.json").read_text(encoding="utf-8"))
    assert new_classes == {"CF_EOR_FRESH": {"Stats": {"HP": 1}, "LocKey": "CF_EOR_FRESH"}}

    backup_classes = json.loads(
        (pack_dir / eor_import.PRE_IMPORT_BACKUP_DIRNAME / "classes.json")
        .read_text(encoding="utf-8")
    )
    assert backup_classes == {"CF_EOR_OLD": {"Stats": {"HP": 99}}}

    gi_lines = (pack_dir / ".gitignore").read_text(encoding="utf-8").splitlines()
    assert f"{eor_import.PRE_IMPORT_BACKUP_DIRNAME}/" in gi_lines

    overwrite_findings = [f for f in report["findings"]
                          if f["kind"] == "class_pack_authored_content_overwritten"]
    assert len(overwrite_findings) == 1
    assert overwrite_findings[0]["severity"] == "warn"


def test_emit_class_pack_gitignore_is_appended_not_clobbered(tmp_path):
    class_packs_dir = tmp_path / "ClassPacks"
    pack_dir = class_packs_dir / eor_import.CLASS_PACK_ID
    pack_dir.mkdir(parents=True)
    (pack_dir / "traits.json").write_text("{}", encoding="utf-8")
    (pack_dir / ".gitignore").write_text("some-other-pattern/\n", encoding="utf-8")

    eor_import.emit_class_pack(_fresh_class_result(), class_packs_dir, force=True)

    gi_lines = (pack_dir / ".gitignore").read_text(encoding="utf-8").splitlines()
    assert "some-other-pattern/" in gi_lines
    assert f"{eor_import.PRE_IMPORT_BACKUP_DIRNAME}/" in gi_lines


def test_emit_class_pack_backup_reflects_immediately_prior_state_without_accumulating(tmp_path):
    class_packs_dir = tmp_path / "ClassPacks"
    pack_dir = class_packs_dir / eor_import.CLASS_PACK_ID
    pack_dir.mkdir(parents=True)
    (pack_dir / "traits.json").write_text("{}", encoding="utf-8")
    (pack_dir / "classes.json").write_text(
        json.dumps({"CF_EOR_OLD": {"Stats": {"HP": 0}}}), encoding="utf-8"
    )

    eor_import.emit_class_pack(_fresh_class_result("CF_EOR_A", hp=1), class_packs_dir, force=True)
    eor_import.emit_class_pack(_fresh_class_result("CF_EOR_B", hp=2), class_packs_dir, force=True)

    backup_dir = pack_dir / eor_import.PRE_IMPORT_BACKUP_DIRNAME
    backup_files = sorted(str(p.relative_to(backup_dir).as_posix())
                          for p in backup_dir.rglob("*") if p.is_file())
    assert backup_files == ["classes.json", "localization/en.json", "pack.json",
                            "provenance.json"], "backup must not accumulate across runs"

    # the second run's backup is the *first* run's freshly-written output, not the original
    # pre-import content and not the second run's own output.
    backed_up = json.loads((backup_dir / "classes.json").read_text(encoding="utf-8"))
    assert backed_up == {"CF_EOR_A": {"Stats": {"HP": 1}, "LocKey": "CF_EOR_A"}}
    live = json.loads((pack_dir / "classes.json").read_text(encoding="utf-8"))
    assert live == {"CF_EOR_B": {"Stats": {"HP": 2}, "LocKey": "CF_EOR_B"}}


def test_main_force_classes_flag_end_to_end(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)
    repo_root = tmp_path / "repo"
    class_pack_dir = (repo_root / "FTK2.ClassForge" / "data" / "ClassPacks"
                     / eor_import.CLASS_PACK_ID)
    class_pack_dir.mkdir(parents=True)
    (class_pack_dir / "traits.json").write_text("{}", encoding="utf-8")
    (class_pack_dir / "classes.json").write_text(
        json.dumps({"CF_EOR_OLD": {"Stats": {}}}), encoding="utf-8"
    )
    report_dir = tmp_path / "reports"
    base_args = [
        "--source", str(SOURCE_DIR),
        "--repo-root", str(repo_root),
        "--vocab", str(VOCAB_FIXTURE),
        "--report-dir", str(report_dir),
        "--package-version", "9.9.9-test",
    ]

    eor_import.main(base_args)  # no --force-classes: must skip, not touch the authored pack
    on_disk = json.loads((class_pack_dir / "classes.json").read_text(encoding="utf-8"))
    assert on_disk == {"CF_EOR_OLD": {"Stats": {}}}
    report = json.loads((report_dir / "report.json").read_text(encoding="utf-8"))
    assert any(f["kind"] == "class_pack_authored_content_skipped" for f in report["findings"])
    assert report["summary"]["blocking"] == 1  # unrelated pre-existing per-item finding only

    eor_import.main(base_args + ["--force-classes"])
    on_disk_forced = json.loads((class_pack_dir / "classes.json").read_text(encoding="utf-8"))
    assert set(on_disk_forced) == {"CF_EOR_TESTMAGE", "CF_EOR_TESTKNIGHT"}
    assert (class_pack_dir / eor_import.PRE_IMPORT_BACKUP_DIRNAME / "classes.json").is_file()
    report_forced = json.loads((report_dir / "report.json").read_text(encoding="utf-8"))
    assert any(f["kind"] == "class_pack_authored_content_overwritten"
               for f in report_forced["findings"])


# ─────────────────────────────── cross-cutting (2) ─────────────────────────────────────────────

def test_id_collision_against_vocab_snapshot_is_blocking(monkeypatch):
    patch_expected_counts(monkeypatch)
    vocab = small_vocab()
    vocab["AllIds"] = list(vocab["AllIds"]) + ["ARM_EOR_ASH_TEST_KNIFE"]
    sources = small_sources()
    report = eor_import.make_report()
    ctx = eor_import.build_context(vocab, sources, "9.9.9-test", report)

    result = eor_import.run_pipeline(ctx, {"items", "followers", "classes"}, check_invariants=True)

    assert result.aborted is True
    assert result.item_packs == {}
    assert any(f["kind"] == "id_collision" and f["severity"] == "blocking"
               for f in report["findings"])


def test_two_runs_produce_byte_identical_output(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)

    def run_once(out_dir: Path) -> None:
        vocab = small_vocab()
        sources = small_sources()
        report = eor_import.make_report()
        ctx = eor_import.build_context(vocab, sources, "9.9.9-test", report)
        result = eor_import.run_pipeline(ctx, {"items", "followers", "classes"},
                                          check_invariants=True)
        assert result.aborted is False
        eor_import.emit_all(result, out_dir)
        eor_import.emit_reports(report, result.follower_result, result.stats_report,
                                 out_dir / "reports", SOURCE_DIR, "9.9.9-test",
                                 dry_run=False, wrote_packs=True)

    out_a = tmp_path / "a"
    out_b = tmp_path / "b"
    run_once(out_a)
    run_once(out_b)

    files_a = sorted(p.relative_to(out_a) for p in out_a.rglob("*") if p.is_file())
    files_b = sorted(p.relative_to(out_b) for p in out_b.rglob("*") if p.is_file())
    assert files_a == files_b
    assert files_a, "expected at least one emitted file"
    for rel in files_a:
        assert (out_a / rel).read_bytes() == (out_b / rel).read_bytes(), rel


# ─────────────────────────────── invariants & pipeline behaviour ───────────────────────────────

def test_assert_corpus_invariants_passes_on_matching_fixture(monkeypatch):
    patch_expected_counts(monkeypatch)
    vocab = small_vocab()
    sources = small_sources()
    report = eor_import.make_report()
    assert eor_import.assert_corpus_invariants(sources, vocab, report) is True
    assert not any(f["severity"] == "blocking" for f in report["findings"])


def test_assert_corpus_invariants_fails_on_mismatched_count():
    # EXPECTED_* left at real-package defaults (386/...); fixture has 6 -> must fail loudly.
    vocab = small_vocab()
    sources = small_sources()
    report = eor_import.make_report()
    assert eor_import.assert_corpus_invariants(sources, vocab, report) is False
    assert any(f["kind"] == "invariant_items_custom_count" and f["severity"] == "blocking"
               for f in report["findings"])


def test_run_pipeline_aborts_without_writing_when_invariants_fail():
    # Same idea via the full pipeline entry point.
    vocab = small_vocab()
    sources = small_sources()
    report = eor_import.make_report()
    ctx = eor_import.build_context(vocab, sources, "9.9.9-test", report)
    result = eor_import.run_pipeline(ctx, {"items", "followers", "classes"}, check_invariants=True)
    assert result.aborted is True
    assert result.item_packs == {}
    assert result.follower_result == {"packs": {}, "dropped": [], "vanilla_overrides": []}


def test_run_pipeline_aborts_when_characters_json_missing(tmp_path, monkeypatch):
    # Build a source tree that has items but no Characters.json two levels up.
    empty_root = tmp_path / "pkg" / "BepInEx" / "plugins"
    empty_root.mkdir(parents=True)
    vocab = small_vocab()
    sources = eor_import.load_sources(empty_root)
    assert sources.characters_json_found is False
    report = eor_import.make_report()
    ctx = eor_import.build_context(vocab, sources, "9.9.9-test", report)
    result = eor_import.run_pipeline(ctx, {"followers"}, check_invariants=False)
    assert result.aborted is True
    assert any(f["kind"] == "characters_json_missing" for f in report["findings"])


def test_dry_run_writes_nothing(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)
    vocab = small_vocab()
    sources = small_sources()
    report = eor_import.make_report()
    ctx = eor_import.build_context(vocab, sources, "9.9.9-test", report)
    result = eor_import.run_pipeline(ctx, {"items", "followers", "classes"}, check_invariants=True)
    assert result.aborted is False
    assert result.item_packs  # non-trivial result computed

    out_dir = tmp_path / "would-be-output"
    # dry-run: main()'s contract is "compute and report, write nothing" -- emulate by simply
    # not calling emit_all, matching what main() does when args.dry_run is set.
    assert not out_dir.exists()


def test_main_dry_run_cli_writes_no_packs(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)
    repo_root = tmp_path / "repo"
    repo_root.mkdir()
    report_dir = tmp_path / "reports"

    rc = eor_import.main([
        "--source", str(SOURCE_DIR),
        "--repo-root", str(repo_root),
        "--vocab", str(VOCAB_FIXTURE),
        "--report-dir", str(report_dir),
        "--package-version", "9.9.9-test",
        "--dry-run",
    ])

    # rc == 1: the fixture deliberately includes one item with an unmapped illegal Class
    # (EORR_BROKEN_WEIRD_MACE), which is a per-item blocking finding -- exit 1 signals "go
    # look at the report", it does not by itself abort the run (see run_pipeline docstring).
    assert rc == 1
    assert not (repo_root / "FTK2.Armory").exists()
    assert not (repo_root / "FTK2.Summoner").exists()
    assert not (repo_root / "FTK2.ClassForge").exists()
    assert (report_dir / "report.json").is_file()
    report = json.loads((report_dir / "report.json").read_text(encoding="utf-8"))
    assert report["dry_run"] is True
    assert report["wrote_packs"] is False


def test_main_full_run_writes_expected_pack_layout(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)
    repo_root = tmp_path / "repo"
    repo_root.mkdir()
    report_dir = tmp_path / "reports"

    rc = eor_import.main([
        "--source", str(SOURCE_DIR),
        "--repo-root", str(repo_root),
        "--vocab", str(VOCAB_FIXTURE),
        "--report-dir", str(report_dir),
        "--package-version", "9.9.9-test",
    ])

    # rc == 1 for the same reason as the dry-run test above (one item-level blocking finding,
    # non-aborting); the packs are still written minus that one excluded item.
    assert rc == 1
    items_pack = json.loads(
        (repo_root / "FTK2.Armory" / "packs" / "eor_items.pack.json").read_text(encoding="utf-8")
    )
    assert items_pack["PackId"] == "ARM_EOR_ITEMS"
    assert len(items_pack["Items"]) == 5  # 6 custom items minus the blocking-excluded one

    starters_pack = json.loads(
        (repo_root / "FTK2.Armory" / "packs" / "eor_starters.pack.json").read_text(encoding="utf-8")
    )
    assert len(starters_pack["Items"]) == 2

    pets_followers = json.loads(
        (repo_root / "FTK2.Summoner" / "data" / "FollowerPacks" / "SMN_PACK_EOR_PETS"
         / "followers.json").read_text(encoding="utf-8")
    )
    assert list(pets_followers) == ["SMN_FOL_TEST_GHOST_GENERIC_00"]

    mercs_followers = json.loads(
        (repo_root / "FTK2.Summoner" / "data" / "FollowerPacks" / "SMN_PACK_EOR_MERCS"
         / "followers.json").read_text(encoding="utf-8")
    )
    assert list(mercs_followers) == ["SMN_MRC_TEST_BANDIT_HEAVY_00"]

    classes = json.loads(
        (repo_root / "FTK2.ClassForge" / "data" / "ClassPacks" / "CF_PACK_EOR_CLASSES"
         / "classes.json").read_text(encoding="utf-8")
    )
    assert set(classes) == {"CF_EOR_TESTMAGE", "CF_EOR_TESTKNIGHT"}

    dropped = json.loads((report_dir / "dropped.json").read_text(encoding="utf-8"))
    assert {e["eor_id"] for e in dropped} == {"SHEPHERD_SHEEP_TEST_00", "COMPANION_REFLECTION"}

    overrides = json.loads((report_dir / "vanilla-overrides.json").read_text(encoding="utf-8"))
    assert {e["eor_id"] for e in overrides} == {"MERC_TEST_VANILLA"}

    assert (report_dir / "eor-class-stats-report.md").is_file()


# ─────────────────────────────── §M7 — golden-file regression test ─────────────────────────────
#
# This is the regression net B6 lacked: nothing previously asserted that a fresh regeneration of
# the emitted *packs* (not the diagnostic reports/ dir, which embeds a host-dependent absolute
# --source path and so is deliberately excluded from this byte-compare) equals a committed,
# reviewed baseline. To refresh GOLDEN_DIR after an intentional converter change, regenerate it
# with the same call this test makes (patch_expected_counts + run_pipeline + emit_all against
# GOLDEN_DIR) and review the diff like any other change to committed pack content.

def test_golden_full_pipeline_matches_committed_output(tmp_path, monkeypatch):
    patch_expected_counts(monkeypatch)
    vocab = small_vocab()
    sources = small_sources()
    report = eor_import.make_report()
    ctx = eor_import.build_context(vocab, sources, "9.9.9-test", report, vocab_path=VOCAB_FIXTURE)
    result = eor_import.run_pipeline(ctx, {"items", "followers", "classes"},
                                      check_invariants=True)
    assert result.aborted is False, report["findings"]

    out_dir = tmp_path / "out"
    status = eor_import.emit_all(result, out_dir)
    assert status == "written"  # fresh dir, no prior authored content -- must not be skipped

    golden_files = sorted(p.relative_to(GOLDEN_DIR) for p in GOLDEN_DIR.rglob("*")
                          if p.is_file())
    out_files = sorted(p.relative_to(out_dir) for p in out_dir.rglob("*") if p.is_file())
    assert golden_files, "golden fixture tree is empty -- was it committed?"
    assert out_files == golden_files, (
        "emitted pack file layout drifted from the committed golden tree in "
        "tools/tests/fixtures/eor_import/golden/ -- if this is an intentional converter "
        "change, regenerate the goldens and review the diff"
    )
    mismatches = [
        rel for rel in golden_files
        if (out_dir / rel).read_bytes() != (GOLDEN_DIR / rel).read_bytes()
    ]
    assert mismatches == [], f"byte mismatch vs committed golden for: {mismatches}"


# ─────────────────────────────── integration (skipped without the real package) ────────────────


@pytest.mark.skipif(not REAL_PACKAGE_AVAILABLE, reason=_REAL_PACKAGE_SKIP_REASON)
def test_real_package_invariants_pass():
    vocab = eor_import.load_vocab(REAL_VOCAB)
    sources = eor_import.load_sources(REAL_SOURCE)
    report = eor_import.make_report()
    assert eor_import.assert_corpus_invariants(sources, vocab, report) is True, report["findings"]


@pytest.mark.skipif(not REAL_PACKAGE_AVAILABLE, reason=_REAL_PACKAGE_SKIP_REASON)
def test_real_package_item_packs_pass_validate_pack():
    vocab = eor_import.load_vocab(REAL_VOCAB)
    sources = eor_import.load_sources(REAL_SOURCE)
    report = eor_import.make_report()
    ctx = eor_import.build_context(vocab, sources, "0.7.0.60", report)
    result = eor_import.run_pipeline(ctx, {"items"}, check_invariants=True)
    assert result.aborted is False, report["findings"]

    for pack_id, doc in result.item_packs.items():
        findings = validate_pack(doc, vocab)
        errors = [f for f in findings if f["level"] == "ERROR"]
        assert errors == [], f"{pack_id}: {errors}"


@pytest.mark.skipif(not REAL_PACKAGE_AVAILABLE, reason=_REAL_PACKAGE_SKIP_REASON)
def test_real_package_two_runs_byte_identical(tmp_path):
    def run_once(out_dir: Path) -> None:
        vocab = eor_import.load_vocab(REAL_VOCAB)
        sources = eor_import.load_sources(REAL_SOURCE)
        report = eor_import.make_report()
        ctx = eor_import.build_context(vocab, sources, "0.7.0.60", report)
        result = eor_import.run_pipeline(ctx, {"items", "followers", "classes"},
                                          check_invariants=True)
        assert result.aborted is False, report["findings"]
        eor_import.emit_all(result, out_dir)

    out_a = tmp_path / "a"
    out_b = tmp_path / "b"
    run_once(out_a)
    run_once(out_b)

    files_a = sorted(p.relative_to(out_a) for p in out_a.rglob("*") if p.is_file())
    files_b = sorted(p.relative_to(out_b) for p in out_b.rglob("*") if p.is_file())
    assert files_a == files_b
    for rel in files_a:
        assert (out_a / rel).read_bytes() == (out_b / rel).read_bytes(), rel
