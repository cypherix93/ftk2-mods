"""EOR (Enhanced Overhaul Revamped) -> FTK2.Armory / FTK2.Summoner / FTK2.ClassForge pack converter.

Reads a read-only EOR mod package and emits:
  - two Armory manifest packs (items + starter weapons) under FTK2.Armory/packs/
  - two Summoner FollowerPacks (pets + mercs) under FTK2.Summoner/data/FollowerPacks/
  - one ClassForge class pack under FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/

Design doc (authoritative, read fully before changing this file):
    docs/superpowers/plans/2026-07-25-summoner-m0-and-converter-design.md, Part B.

NOTE on vocab provenance: tools/out/vocab-index.json may be EOR-contaminated -- the game
install it was extracted from may itself have had an EOR package installed, so its `AllIds`
can carry EOR_* class ids that are not vanilla (see design doc B7). Every id this tool emits
is ARM_/SMN_/CF_-prefixed, so that contamination cannot cause a false id-collision negative
here. Re-run this tool (and re-validate its output) after the vocab index is regenerated from
a verified-clean game install.

Usage:
    python tools/eor_import.py --source "<EOR pkg>/BepInEx/plugins" --repo-root .
        [--vocab tools/out/vocab-index.json] [--only items,followers,classes]
        [--report-dir tools/out/eor-import] [--package-version 0.7.0.60] [--dry-run]

The --source tree's ../.. also carries the package's Characters.json at
For The King II_Data/StreamingAssets/Assets/Configs/JSON~/Characters.json; that file supplies
the character-id set used to validate follower ConfigName references and is the source of the
31 EOR_* classes. If absent, the followers and classes converters abort with a blocking finding
(items conversion is unaffected -- it does not need Characters.json).
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import Path
from statistics import mean
from typing import NamedTuple, Optional

# ─────────────────────────────── 1. constants & policy tables ──────────────────────────────

TOOLS_DIR = Path(__file__).resolve().parent
REPO_ROOT_DEFAULT = TOOLS_DIR.parent
DEFAULT_VOCAB = TOOLS_DIR / "out" / "vocab-index.json"
DEFAULT_REPORT_DIR = TOOLS_DIR / "out" / "eor-import"
PACKAGE_VERSION_DEFAULT = "0.7.0.60"

# Measured corpus shape (design doc §0), used by assert_corpus_invariants. Module-level so
# tests can monkeypatch them to match small synthetic fixtures without touching the real
# defaults used against the actual EOR package.
EXPECTED_ITEMS_CUSTOM_COUNT = 386
EXPECTED_ITEMS_STARTERS_COUNT = 31
EXPECTED_ITEMS_EXAMPLES_COUNT = 4
EXPECTED_COMPANION_PLUS_COUNT = 100
EXPECTED_MERC_PLUS_COUNT = 100
EXPECTED_CLASS_COUNT = 31

# §B3.2 -- illegal starter-weapon Class -> legal one-handed target.
CLASS_REMAP: dict[str, str] = {
    "SWORD": "BLADE",
    "POLEARM": "SPEAR",
    "LANCE": "SPEAR",
    "BOW": "HANDBOW",
    "BOOK": "STAFF",
    "LUTE": "LUTE_2H",
}

# §B3.3 -- donor-lookup alias when Class has zero VisualDonors of its own.
DONOR_CLASS_ALIAS: dict[str, str] = {
    "ORB": "ORB_2H",
}

# §B3.5 -- tag policy.
TAG_DROP: frozenset[str] = frozenset({
    "EOR_CUSTOM", "EORR_CUSTOM", "EOR_EXAMPLE", "CHESTARMOR", "HELMET",
    "AWR", "SPD", "VIT", "INT", "STR", "TAL", "LCK",
})
TAG_RENAME: dict[str, str] = {
    "ENDGAME_GEAR": "ARM_ENDGAME_GEAR",
    "STRONGER_GEAR": "ARM_STRONGER_GEAR",
    "WEAKER_GEAR": "ARM_WEAKER_GEAR",
    "UTILITY_GEAR": "ARM_UTILITY_GEAR",
    "EOR_STARTER_WEAPON": "ARM_STARTER_WEAPON",
}

# §B4.3 -- ground-truth enum sets (enum-ground-truth.md §2, §6, §11), used for WARN-level
# follower-field validation only (the converter never rejects on these; PackValidator, a
# separate C# component, is the hard gate at load time).
CHARACTER_TYPES: frozenset[str] = frozenset({
    "NONE", "STANDARD", "PROP", "FORCED_FIGHT", "MERCENARY", "CURSE", "COMPANION", "BOSS",
    "INANIMATE",
})
LOOT_SCALES: frozenset[str] = frozenset({
    "NONE", "TINY", "VERY_LOW", "LOW", "AVERAGE", "HIGH", "VERY_HIGH", "HUGE", "MASSIVE",
})
AI_BEHAVIOURS: frozenset[str] = frozenset({"DEFAULT", "DPS", "SUPPORT", "TANK", "CURSE"})
ITEM_RARITIES: frozenset[str] = frozenset({
    "NONE", "COMMON", "UNCOMMON", "RARE", "ARTIFACT", "QUEST", "LORE", "SKIN", "LOCKED",
    "MERCENARY", "CHARACTER", "LOCATION", "MYSTERY", "SEASONAL",
})
CHARACTER_STATS: frozenset[str] = frozenset({
    "RND", "NONE", "STR", "VIT", "INT", "AWR", "TAL", "SPD", "LCK", "DEF", "RES", "EVD", "PA",
    "SA", "MXFOC", "FOC", "MXHP", "HP", "XP", "ACC", "ATK", "MAG", "PHY", "PRW", "CRT", "CRTD",
    "GLD", "XPM", "SKL", "HRG", "LOP", "THRN", "MOV", "LBM", "WBM", "FIND", "PSTR", "PVIT",
    "PINT", "PAWR", "PTAL", "PSPD", "PLCK", "PDEF", "PRES", "PEVD", "PFOC", "PGLD", "DAM",
    "PARTY_XP", "CURRENT_FOCUS",
})

# §B5 -- native CharacterConfig fields a class pack may carry (order-preserving allow-list).
NATIVE_CHARACTER_FIELDS: tuple[str, ...] = (
    "Stats", "Things", "Passives", "CampQuery", "SwarmQuery", "LootID", "LocKey", "Rarity",
    "Level", "Threat", "BaseType", "DefaultBodyType", "Tags", "OnDeathAbility", "Expansion",
)

# §B3.3 -- per-Class synthesized description templates; anything else falls back to the
# generic template. Only KATANA is needed for the measured corpus (the only defect-1 class).
DESC_TEMPLATES: dict[str, str] = {
    "KATANA": "A curved blade of foreign make, kept keen by long habit.",
}
DESC_FALLBACK_TEMPLATE = "A {name}, of no recorded provenance."

FOLLOWER_DESC_TEMPLATES: dict[str, str] = {
    "COMPANION": "A {name} that can be coaxed into travelling with your party.",
    "MERCENARY": "A {name} willing to fight alongside you, for coin.",
}

# Findings whose presence means "abort before writing anything" (design §B7, §B8): a corpus
# that doesn't measure up to the numbers this converter was written against, a missing
# Characters.json, a missing vocab file, or an id collision. Everything else that is
# "blocking" (an unmapped item Class, a Class with no visual donor) instead excludes just that
# one item and lets the rest of the run through -- "the tool must never silently emit an item
# the validator will reject" (§B3.2), not "one bad item poisons the whole pack".
_ABORT_KINDS = frozenset({"id_collision", "characters_json_missing", "vocab_missing"})


def _is_abort_finding(finding: dict) -> bool:
    if finding["severity"] != "blocking":
        return False
    kind = finding["kind"]
    return kind.startswith("invariant_") or kind in _ABORT_KINDS


# ─────────────────────────────────── 2. io helpers ──────────────────────────────────────────

def read_json(path: Path) -> dict:
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def _read_json_or_empty(path: Path) -> dict:
    if not path.is_file():
        return {}
    return read_json(path)


def write_json(path: Path, obj: dict) -> None:
    """Identical convention to compile_pack._merge_json: sorted keys, LF, trailing newline."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, sort_keys=True, indent=2, ensure_ascii=False)
        f.write("\n")


def write_text(path: Path, text: str) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def load_vocab(path: Path) -> dict:
    return read_json(Path(path))


class EorSources(NamedTuple):
    items_custom: dict
    items_starters: dict
    items_examples: dict
    vf_map: dict
    en: dict
    followers_pets: dict
    followers_mercs: dict
    classes_all: dict
    classes: dict
    characters_json_found: bool
    characters_json_path: Path


def load_sources(source_dir: Path) -> EorSources:
    """Reads every EOR input document. --source is read-only and never written to."""
    source_dir = Path(source_dir)
    eor_root = source_dir / "EnhancedOverhaulRevamped"
    things_dir = eor_root / "CustomItems" / "Things"

    items_custom = _read_json_or_empty(things_dir / "EORR_CustomItems.json")
    items_starters = _read_json_or_empty(things_dir / "StarterWeapons.json")
    items_examples = _read_json_or_empty(things_dir / "Examples.json")
    vf_map = _read_json_or_empty(eor_root / "CustomItems" / "VisualFallbacks.json")
    en = _read_json_or_empty(eor_root / "Localization" / "en.json")
    followers_pets = _read_json_or_empty(source_dir / "FTK2_EnhancedPets" / "Followers.json")
    followers_mercs = _read_json_or_empty(source_dir / "FTK2_EnhancedMercenaries" / "Followers.json")

    characters_json_path = (
        source_dir.parent.parent / "For The King II_Data" / "StreamingAssets" / "Assets"
        / "Configs" / "JSON~" / "Characters.json"
    )
    characters_json_found = characters_json_path.is_file()
    classes_all = read_json(characters_json_path) if characters_json_found else {}
    classes = {k: v for k, v in classes_all.items() if k.startswith("EOR_")}

    return EorSources(
        items_custom=items_custom, items_starters=items_starters, items_examples=items_examples,
        vf_map=vf_map, en=en, followers_pets=followers_pets, followers_mercs=followers_mercs,
        classes_all=classes_all, classes=classes,
        characters_json_found=characters_json_found, characters_json_path=characters_json_path,
    )


# ───────────────────────────── 3. shared pure helpers ───────────────────────────────────────

def humanize(raw_id: str, *, strip_prefixes: tuple[str, ...] = (),
             drop_tokens: tuple[str, ...] = ("GENERIC",)) -> str:
    """Turns an EOR SCREAMING_SNAKE id into a display name.

    Validated against two real EOR outputs (design §B4.4):
      COMPANION_PLUS_PIXIE_GENERIC_00      -> "Pixie"
      COMPANION_PLUS_SKRAEVIN_CHAOS_MELEE_00 -> "Skraevin Chaos Melee"
    and one raw-id shape (design §B3.3, the possessive-collapse rule):
      EORR_RIVET_SAMURAI_S_KATANA -> "Rivet Samurai's Katana"
    """
    s = raw_id
    for prefix in strip_prefixes:
        if s.startswith(prefix):
            s = s[len(prefix):]
            break

    tokens = [t for t in s.split("_") if t]
    while tokens and tokens[-1].isdigit():
        tokens.pop()
    tokens = [t for t in tokens if t not in drop_tokens]

    words: list[str] = []
    for tok in tokens:
        if tok == "S" and words:
            words[-1] = words[-1] + "'s"
        else:
            words.append(tok.capitalize())
    return " ".join(words)


def provenance(eor_id: str, package_version: str) -> dict:
    return {"Source": "eor-import", "EorId": eor_id, "PackageVersion": package_version}


def make_report() -> dict:
    return {"findings": []}


def record(report: dict, kind: str, *, severity: str = "info", id: Optional[str] = None,
           **fields) -> dict:
    finding = {"kind": kind, "severity": severity, "id": id, **fields}
    report["findings"].append(finding)
    return finding


def _sorted_findings(report: dict) -> list[dict]:
    return sorted(
        report["findings"],
        key=lambda f: (f["kind"], f["id"] if f["id"] is not None else ""),
    )


# ────────────────────────────── 4. items converter ──────────────────────────────────────────

def map_item_id(eor_id: str) -> Optional[str]:
    if eor_id.startswith("EORR_"):
        return "ARM_EOR_" + eor_id[len("EORR_"):]
    if eor_id.startswith("EOR_STARTER_"):
        return "ARM_EOR_STARTER_" + eor_id[len("EOR_STARTER_"):]
    if eor_id.startswith("EOR_EXAMPLE_"):
        return None
    return None


def hoist_ability_bags(thing: dict) -> tuple[dict, list[str]]:
    """§B3.4: hoist the stray top-level AbilityBag/AbilityFillBag/ShuffleAbilityBag (not real
    ThingConfig fields -- the game silently discards them) into Interactable, preferring the
    top-level copy on conflict. Returns (new_thing, dropped_undeclared_ability_ids).
    """
    thing = dict(thing)
    top_bag = thing.pop("AbilityBag", None)
    top_fill = thing.pop("AbilityFillBag", None)
    top_shuffle = thing.pop("ShuffleAbilityBag", None)

    interactable = thing.get("Interactable")
    if interactable is None:
        return thing, []
    interactable = dict(interactable)

    def resolve_list(top_val, key: str) -> list:
        if top_val:
            return list(top_val)
        existing = interactable.get(key)
        return list(existing) if existing else []

    resolved_bag = resolve_list(top_bag, "AbilityBag")
    resolved_fill = resolve_list(top_fill, "AbilityFillBag")
    resolved_shuffle = top_shuffle if top_shuffle is not None else interactable.get("ShuffleAbilityBag")

    declared = set((interactable.get("Abilities") or {}).keys())
    dropped: list[str] = []

    def filter_undeclared(bag: list) -> list:
        kept = []
        for ab in bag:
            if ab in declared:
                kept.append(ab)
            else:
                dropped.append(ab)
        return kept

    resolved_bag = filter_undeclared(resolved_bag)
    resolved_fill = filter_undeclared(resolved_fill)

    if resolved_bag:
        interactable["AbilityBag"] = resolved_bag
    else:
        interactable.pop("AbilityBag", None)
    if resolved_fill:
        interactable["AbilityFillBag"] = resolved_fill
    else:
        interactable.pop("AbilityFillBag", None)
    if resolved_shuffle is not None:
        interactable["ShuffleAbilityBag"] = resolved_shuffle
    else:
        interactable.pop("ShuffleAbilityBag", None)

    thing["Interactable"] = interactable
    return thing, dropped


def repair_class(item_id: str, cls: Optional[str], vocab: dict, report: dict) -> Optional[str]:
    """§B3.2. Returns the (possibly remapped) legal Class, or None (+ blocking finding) if the
    Class is neither legal nor in CLASS_REMAP."""
    classes = set(vocab.get("Classes", []))
    if cls in classes:
        return cls
    if cls in CLASS_REMAP:
        new_cls = CLASS_REMAP[cls]
        record(report, "class_remap", severity="info", id=item_id, **{"from": cls, "to": new_cls})
        return new_cls
    record(report, "unmapped_class", severity="blocking", id=item_id, cls=cls,
           message=f"Class {cls!r} is not live and has no CLASS_REMAP entry; item dropped")
    return None


def pick_visual_fallback(item_id: str, eor_id: str, cls: str, vf_map: dict, vocab: dict,
                          report: dict) -> Optional[str]:
    """§B3.3. Existing (valid) EOR VisualFallbacks.json entry wins; otherwise a deterministic
    donor pick from vocab["VisualDonors"][cls] (sorted first), walking the
    Class -> alias -> Class_2H -> Class-without-_2H fallback chain. None (+ blocking finding)
    if nothing in the chain has a donor."""
    item_ids = set(vocab.get("ItemIds", []))
    existing = vf_map.get(eor_id)
    if existing and existing in item_ids:
        return existing

    donors = vocab.get("VisualDonors", {})
    chain = [cls]
    if cls in DONOR_CLASS_ALIAS:
        chain.append(DONOR_CLASS_ALIAS[cls])
    chain.append(cls + "_2H")
    if cls.endswith("_2H"):
        chain.append(cls[:-len("_2H")])

    for candidate in chain:
        donor_list = donors.get(candidate)
        if donor_list:
            return sorted(donor_list)[0]

    record(report, "no_visual_donor", severity="blocking", id=item_id, cls=cls,
           message=f"no VisualDonors entry for Class {cls!r} or its alias/2H fallback chain; "
                   "item dropped")
    return None


def synth_item_loc(item_id: str, eor_id: str, cls: str, en: dict, report: dict) -> dict:
    """§B3.3/B3.6. Copies the existing en.json entry verbatim (bugs and all -- this tool never
    repairs loc content, only fills true gaps); otherwise humanizes the id and synthesizes a
    per-Class description."""
    if eor_id in en:
        record(report, "loc_copied", severity="info", id=item_id)
        return {
            "Name": en[eor_id],
            "Description": en.get(eor_id + "_DESCRIPTION", ""),
        }
    name = humanize(eor_id, strip_prefixes=("EORR_", "EOR_STARTER_", "EOR_EXAMPLE_"))
    template = DESC_TEMPLATES.get(cls, DESC_FALLBACK_TEMPLATE)
    record(report, "loc_synth", severity="info", id=item_id, name=name)
    return {"Name": name, "Description": template.format(name=name)}


def filter_tags(item_id: str, tags: list[str], vocab: dict, report: dict) -> list[str]:
    """§B3.5. Drop TAG_DROP, rename TAG_RENAME into the ARM_ namespace, drop+report anything
    else unknown, keep all known/ARM_-namespace tags verbatim, dedupe keeping first occurrence,
    original order preserved."""
    known = set(vocab.get("Tags", {}))
    seen: set[str] = set()
    out: list[str] = []
    for tag in tags:
        if tag in TAG_DROP:
            record(report, "tag_dropped", severity="info", id=item_id, tag=tag, reason="policy")
            continue
        if tag in TAG_RENAME:
            new_tag = TAG_RENAME[tag]
        elif tag in known or tag.startswith("ARM_"):
            new_tag = tag
        else:
            record(report, "tag_dropped", severity="info", id=item_id, tag=tag, reason="unknown")
            continue
        if new_tag not in seen:
            seen.add(new_tag)
            out.append(new_tag)
    return out


def convert_item(eor_id: str, thing: dict, ctx: "Ctx") -> Optional[dict]:
    new_id = map_item_id(eor_id)
    if new_id is None:
        return None

    hoisted, dropped_bag_ids = hoist_ability_bags(thing)
    for ab in sorted(set(dropped_bag_ids)):
        record(ctx.report, "bag_undeclared_dropped", severity="warn", id=new_id, ability=ab)

    new_cls = repair_class(new_id, hoisted.get("Class"), ctx.vocab, ctx.report)
    if new_cls is None:
        return None
    hoisted["Class"] = new_cls

    vf = pick_visual_fallback(new_id, eor_id, new_cls, ctx.vf_map, ctx.vocab, ctx.report)
    if vf is None:
        return None

    hoisted["Tags"] = filter_tags(new_id, hoisted.get("Tags") or [], ctx.vocab, ctx.report)
    loc = synth_item_loc(new_id, eor_id, new_cls, ctx.en, ctx.report)

    return {
        "Id": new_id,
        "Thing": hoisted,
        "VisualFallback": vf,
        "Loc": loc,
        "Provenance": provenance(eor_id, ctx.package_version),
    }


def convert_items(ctx: "Ctx") -> dict[str, dict]:
    items_doc = {"PackId": "ARM_EOR_ITEMS", "Items": []}
    starters_doc = {"PackId": "ARM_EOR_STARTERS", "Items": []}

    for eor_id in sorted(ctx.sources.items_custom):
        entry = convert_item(eor_id, ctx.sources.items_custom[eor_id], ctx)
        if entry is not None:
            items_doc["Items"].append(entry)
    for eor_id in sorted(ctx.sources.items_starters):
        entry = convert_item(eor_id, ctx.sources.items_starters[eor_id], ctx)
        if entry is not None:
            starters_doc["Items"].append(entry)

    items_doc["Items"].sort(key=lambda e: e["Id"])
    starters_doc["Items"].sort(key=lambda e: e["Id"])
    return {"ARM_EOR_ITEMS": items_doc, "ARM_EOR_STARTERS": starters_doc}


ITEM_PACK_FILENAMES = {
    "ARM_EOR_ITEMS": "eor_items.pack.json",
    "ARM_EOR_STARTERS": "eor_starters.pack.json",
}


# ──────────────────────────── 5. followers converter ────────────────────────────────────────

def map_follower_id(eor_id: str) -> Optional[str]:
    if eor_id.startswith("COMPANION_PLUS_"):
        return "SMN_FOL_" + eor_id[len("COMPANION_PLUS_"):]
    if eor_id.startswith("MERC_PLUS_"):
        return "SMN_MRC_" + eor_id[len("MERC_PLUS_"):]
    return None


def classify_follower(eor_id: str, entry: dict, char_ids: frozenset) -> str:
    """§B4.1/§B4.2, total & deterministic. Priority: missing ConfigName first (catches
    COMPANION_REFLECTION), then unresolved ConfigName (catches the 8 SHEPHERD_SHEEP_* whether
    or not they happen to carry a *_PLUS_ prefix), then the *_PLUS_ prefix test, else park."""
    config_name = entry.get("ConfigName")
    if not config_name:
        return "drop_no_config"
    if config_name not in char_ids:
        return "drop_dangling"
    if eor_id.startswith("COMPANION_PLUS_") or eor_id.startswith("MERC_PLUS_"):
        return "emit"
    return "park_vanilla"


def convert_follower(eor_id: str, entry: dict, ctx: "Ctx") -> tuple[str, dict]:
    new_id = map_follower_id(eor_id)
    new_entry = dict(entry)
    new_entry["ClassName"] = new_id
    return new_id, new_entry


def synth_follower_loc(new_id: str, eor_id: str, entry: dict, en: dict) -> dict[str, str]:
    """§B4.4. Copy the EOR name verbatim if present; otherwise humanize. Description is always
    synthesized (the 9 that exist in EOR's en.json are branded text we don't ship)."""
    if eor_id in en:
        name = en[eor_id]
    else:
        name = humanize(eor_id, strip_prefixes=("COMPANION_PLUS_", "MERC_PLUS_"))
    follower_type = entry.get("Type", "")
    template = FOLLOWER_DESC_TEMPLATES.get(
        follower_type, "A {name} that has joined your party."
    )
    return {"Name": name, "Description": template.format(name=name)}


def validate_follower_enums(new_id: str, entry: dict, report: dict) -> None:
    """§A4.1 ground truth, WARN-level here (the C# PackValidator is the hard gate)."""
    t = entry.get("Type")
    if t not in CHARACTER_TYPES:
        record(report, "unknown_character_type", severity="warn", id=new_id, value=t)
    cp = entry.get("ContractPrice")
    if cp not in LOOT_SCALES:
        record(report, "unknown_contract_price", severity="warn", id=new_id, value=cp)
    behaviour = entry.get("Behaviour")
    if behaviour not in AI_BEHAVIOURS:
        record(report, "unknown_behaviour", severity="warn", id=new_id, value=behaviour)
    rarity = entry.get("Rarity")
    if rarity is not None and rarity not in ITEM_RARITIES:
        record(report, "unknown_rarity", severity="warn", id=new_id, value=rarity)
    for stat in (entry.get("AppendableStats") or {}):
        if stat not in CHARACTER_STATS:
            record(report, "unknown_appendable_stat", severity="warn", id=new_id, stat=stat)


def _loc_entries(new_id: str, loc: dict) -> dict:
    return {new_id: loc["Name"], new_id + "_DESCRIPTION": loc["Description"]}


def convert_followers(ctx: "Ctx") -> dict:
    pets = ctx.sources.followers_pets
    mercs = ctx.sources.followers_mercs
    merged = dict(pets)
    merged.update(mercs)  # §B4.1: mercs copy wins on the 21 divergent entries.

    pets_pack = {"followers": {}, "localization": {}, "provenance": {}}
    mercs_pack = {"followers": {}, "localization": {}, "provenance": {}}
    dropped: list[dict] = []
    vanilla_overrides: list[dict] = []

    for eor_id in sorted(merged):
        entry = merged[eor_id]
        classification = classify_follower(eor_id, entry, ctx.char_ids)

        if classification == "drop_dangling":
            dropped.append({
                "eor_id": eor_id, "reason": "dangling_config_name",
                "config_name": entry.get("ConfigName"),
            })
            continue
        if classification == "drop_no_config":
            dropped.append({"eor_id": eor_id, "reason": "no_config_name", "config_name": None})
            continue
        if classification == "park_vanilla":
            pets_entry = pets.get(eor_id)
            mercs_entry = mercs.get(eor_id)
            diff = {}
            if pets_entry is not None and mercs_entry is not None:
                for key in sorted(set(pets_entry) | set(mercs_entry)):
                    if pets_entry.get(key) != mercs_entry.get(key):
                        diff[key] = {"pets": pets_entry.get(key), "mercs": mercs_entry.get(key)}
            vanilla_overrides.append({
                "eor_id": eor_id, "type": entry.get("Type"), "config_name": entry.get("ConfigName"),
                "pets": pets_entry, "mercs": mercs_entry, "diff": diff,
            })
            continue

        # emit
        new_id, new_entry = convert_follower(eor_id, entry, ctx)
        validate_follower_enums(new_id, new_entry, ctx.report)
        loc = synth_follower_loc(new_id, eor_id, entry, ctx.en)
        prov = provenance(eor_id, ctx.package_version)
        target = pets_pack if eor_id.startswith("COMPANION_PLUS_") else mercs_pack
        target["followers"][new_id] = new_entry
        target["localization"].update(_loc_entries(new_id, loc))
        target["provenance"][new_id] = prov
        if "AppendableStats" in entry:
            record(ctx.report, "appendable_stats_unconfirmed", severity="warn", id=new_id,
                   message="AppendableStats consumption path unconfirmed (SPEC §11.8)")

    return {
        "packs": {"SMN_PACK_EOR_PETS": pets_pack, "SMN_PACK_EOR_MERCS": mercs_pack},
        "dropped": dropped,
        "vanilla_overrides": vanilla_overrides,
    }


FOLLOWER_PACK_META = {
    "SMN_PACK_EOR_PETS": {
        "id": "SMN_PACK_EOR_PETS",
        "name": "Enhanced Pets (re-hosted)",
        "description": "EOR's COMPANION_PLUS_* followers, re-hosted with SMN_FOL_ ids.",
        "loadOrder": 100,
    },
    "SMN_PACK_EOR_MERCS": {
        "id": "SMN_PACK_EOR_MERCS",
        "name": "Enhanced Mercenaries (re-hosted)",
        "description": "EOR's MERC_PLUS_* followers, re-hosted with SMN_MRC_ ids.",
        "loadOrder": 110,
    },
}


# ───────────────────────────── 6. classes converter ──────────────────────────────────────────

def map_class_id(eor_id: str) -> Optional[str]:
    if eor_id.startswith("EOR_"):
        return "CF_EOR_" + eor_id[len("EOR_"):]
    return None


def strip_to_native(cfg: dict) -> dict:
    return {k: v for k, v in cfg.items() if k in NATIVE_CHARACTER_FIELDS}


def _filter_class_tags(new_id: str, tags: list[str], vocab: dict, report: dict) -> list[str]:
    known = set(vocab.get("Tags", {}))
    out: list[str] = []
    seen: set[str] = set()
    for tag in tags:
        if tag not in known and not tag.startswith("CF_"):
            record(report, "class_tag_dropped", severity="info", id=new_id, tag=tag)
            continue
        if tag not in seen:
            seen.add(tag)
            out.append(tag)
    if "CF_PACK_EOR_CLASSES" not in seen:
        out.append("CF_PACK_EOR_CLASSES")
    return out


def convert_classes(ctx: "Ctx") -> dict:
    classes_doc: dict[str, dict] = {}
    localization: dict[str, str] = {}
    provenance_doc: dict[str, dict] = {}
    item_ids = set(ctx.vocab.get("ItemIds", [])) | set(ctx.vocab.get("AllIds", []))
    skill_ids = set(ctx.vocab.get("Skills", [])) | set(ctx.vocab.get("AllIds", []))

    for eor_id in sorted(ctx.sources.classes):
        cfg = ctx.sources.classes[eor_id]
        new_id = map_class_id(eor_id)
        if new_id is None:
            continue

        native = strip_to_native(cfg)
        native["LocKey"] = new_id
        native["Tags"] = _filter_class_tags(new_id, list(native.get("Tags") or []), ctx.vocab,
                                             ctx.report)

        for thing_id in (native.get("Things") or {}):
            if thing_id not in item_ids:
                record(ctx.report, "unknown_class_thing", severity="warn", id=new_id,
                       thing=thing_id)
        for passive in (native.get("Passives") or []):
            if passive not in skill_ids:
                record(ctx.report, "unknown_class_passive", severity="warn", id=new_id,
                       passive=passive)

        classes_doc[new_id] = native

        name = ctx.en.get(eor_id) or humanize(eor_id, strip_prefixes=("EOR_",))
        desc = ctx.en.get(eor_id + "_DESCRIPTION", "")
        localization[new_id] = name
        localization[new_id + "_DESCRIPTION"] = desc
        provenance_doc[new_id] = provenance(eor_id, ctx.package_version)

        record(ctx.report, "parked_class_content", severity="info", id=new_id,
               message="signature skill, mastery, and starting-kit code are DLL-only and not "
                       "ported; ClassForge M3 recipe territory")

    return {"classes": classes_doc, "localization": localization, "provenance": provenance_doc}


def _stat_summary(entries: list[dict]) -> dict[str, dict]:
    buckets: dict[str, list[float]] = {}
    for cfg in entries:
        for stat, val in (cfg.get("Stats") or {}).items():
            buckets.setdefault(stat, []).append(val)
    out: dict[str, dict] = {}
    for stat, vals in buckets.items():
        vals_sorted = sorted(vals)
        n = len(vals_sorted)
        if n % 2:
            p50 = vals_sorted[n // 2]
        else:
            p50 = (vals_sorted[n // 2 - 1] + vals_sorted[n // 2]) / 2
        out[stat] = {"min": min(vals), "max": max(vals), "mean": mean(vals), "p50": p50}
    return out


def build_stats_report(vanilla_classes: dict[str, dict], eor_classes: dict[str, dict]) -> dict:
    """§B5.1. Report only -- flags stats whose EOR max exceeds the vanilla max. Never mutates,
    never suggests replacement values."""
    vanilla_summary = _stat_summary(list(vanilla_classes.values()))
    eor_summary = _stat_summary(list(eor_classes.values()))
    above_vanilla_max = []
    for stat in sorted(eor_summary):
        van = vanilla_summary.get(stat)
        if van is not None and eor_summary[stat]["max"] > van["max"]:
            above_vanilla_max.append({
                "stat": stat, "eor_max": eor_summary[stat]["max"], "vanilla_max": van["max"],
            })
    return {"vanilla": vanilla_summary, "eor": eor_summary, "above_vanilla_max": above_vanilla_max}


def render_stats_report_md(report: dict) -> str:
    lines = [
        "# EOR class stat envelope vs. vanilla player classes",
        "",
        "Measured, informational only. This converter makes no balance change whatsoever;",
        "rebalancing (if any) is a later human/design pass.",
        "",
        "| Stat | Vanilla min/max/mean | EOR min/max/mean | Flag |",
        "|---|---|---|---|",
    ]
    flagged = {row["stat"] for row in report["above_vanilla_max"]}
    for stat in sorted(set(report["vanilla"]) | set(report["eor"])):
        van = report["vanilla"].get(stat)
        eor = report["eor"].get(stat)
        van_txt = f"{van['min']}/{van['max']}/{van['mean']:.1f}" if van else "-"
        eor_txt = f"{eor['min']}/{eor['max']}/{eor['mean']:.1f}" if eor else "-"
        flag = "ABOVE VANILLA MAX" if stat in flagged else ""
        lines.append(f"| {stat} | {van_txt} | {eor_txt} | {flag} |")
    lines.append("")
    return "\n".join(lines) + "\n"


CLASS_PACK_ID = "CF_PACK_EOR_CLASSES"


# ─────────────────────────── 7. corpus invariants ────────────────────────────────────────────

def assert_corpus_invariants(sources: EorSources, vocab: dict, report: dict) -> bool:
    """§B8. Each check is a measured fact about the specific EOR build this converter was
    written against; a mismatch is a blocking finding naming expected vs actual, so pointing
    the tool at a different EOR build fails loudly instead of emitting subtly wrong packs.
    Returns True iff every check passed (no blocking finding was recorded)."""
    ok = True

    def fail(kind: str, message: str, **extra) -> None:
        nonlocal ok
        record(report, kind, severity="blocking", message=message, **extra)
        ok = False

    # 1. file counts.
    if len(sources.items_custom) != EXPECTED_ITEMS_CUSTOM_COUNT:
        fail("invariant_items_custom_count",
             f"expected {EXPECTED_ITEMS_CUSTOM_COUNT} EORR_CustomItems.json entries, "
             f"got {len(sources.items_custom)}")
    if len(sources.items_starters) != EXPECTED_ITEMS_STARTERS_COUNT:
        fail("invariant_items_starters_count",
             f"expected {EXPECTED_ITEMS_STARTERS_COUNT} StarterWeapons.json entries, "
             f"got {len(sources.items_starters)}")
    if len(sources.items_examples) != EXPECTED_ITEMS_EXAMPLES_COUNT:
        fail("invariant_items_examples_count",
             f"expected {EXPECTED_ITEMS_EXAMPLES_COUNT} Examples.json entries, "
             f"got {len(sources.items_examples)}")

    all_items = {**sources.items_custom, **sources.items_starters, **sources.items_examples}

    # 2. id shape.
    bad_prefix = sorted(
        k for k in all_items
        if not (k.startswith("EORR_") or k.startswith("EOR_STARTER_") or k.startswith("EOR_EXAMPLE_"))
    )
    if bad_prefix:
        fail("invariant_item_id_shape",
             f"{len(bad_prefix)} item ids don't match ^(EORR_|EOR_STARTER_|EOR_EXAMPLE_)",
             ids=bad_prefix[:5])

    # 3. hoist leaves zero residual undeclared bag ids.
    residual = 0
    for thing in all_items.values():
        _, dropped = hoist_ability_bags(thing)
        residual += len(dropped)
    if residual:
        fail("invariant_bag_residual",
             f"expected 0 residual undeclared bag ids after hoist_ability_bags, got {residual}")

    # 4. every declared ability resolves against vocab.
    abilities = set(vocab.get("Abilities", []))
    unknown_ability_refs = 0
    for thing in all_items.values():
        declared = ((thing.get("Interactable") or {}).get("Abilities") or {})
        unknown_ability_refs += sum(1 for ab in declared if ab not in abilities)
    if unknown_ability_refs:
        fail("invariant_unknown_ability",
             f"{unknown_ability_refs} ability refs not present in vocab Abilities")

    # 5. pets Followers.json keys subset of mercs.
    pets, mercs = sources.followers_pets, sources.followers_mercs
    if not set(pets) <= set(mercs):
        fail("invariant_pets_subset_mercs",
             "pets Followers.json key set is not a subset of the mercs key set")

    # 6. exact COMPANION_PLUS_/MERC_PLUS_ counts.
    merged_followers = {**pets, **mercs}
    comp_plus = sum(1 for k in merged_followers if k.startswith("COMPANION_PLUS_"))
    merc_plus = sum(1 for k in merged_followers if k.startswith("MERC_PLUS_"))
    if comp_plus != EXPECTED_COMPANION_PLUS_COUNT or merc_plus != EXPECTED_MERC_PLUS_COUNT:
        fail("invariant_plus_counts",
             f"expected {EXPECTED_COMPANION_PLUS_COUNT}/{EXPECTED_MERC_PLUS_COUNT} "
             f"COMPANION_PLUS_/MERC_PLUS_ entries, got {comp_plus}/{merc_plus}")

    # 7. ClassName == key for every follower.
    mismatched = sorted(k for k, v in merged_followers.items() if v.get("ClassName") != k)
    if mismatched:
        fail("invariant_classname_mismatch",
             f"{len(mismatched)} followers have ClassName != key", ids=mismatched[:5])

    # 8. exact EOR_* class count.
    if len(sources.classes) != EXPECTED_CLASS_COUNT:
        fail("invariant_class_count",
             f"expected {EXPECTED_CLASS_COUNT} EOR_* classes in Characters.json, "
             f"got {len(sources.classes)}")

    # 9. class gear/passives resolve against vocab.
    item_ids = set(vocab.get("ItemIds", [])) | set(vocab.get("AllIds", []))
    skill_ids = set(vocab.get("Skills", [])) | set(vocab.get("AllIds", []))
    bad_things = bad_passives = 0
    for cfg in sources.classes.values():
        bad_things += sum(1 for t in (cfg.get("Things") or {}) if t not in item_ids)
        bad_passives += sum(1 for p in (cfg.get("Passives") or []) if p not in skill_ids)
    if bad_things or bad_passives:
        fail("invariant_class_gear_or_passives",
             f"{bad_things} unknown Things ids, {bad_passives} unknown Passives ids across "
             "EOR classes")

    return ok


# ────────────────────────────────── ctx ──────────────────────────────────────────────────────

@dataclass(frozen=True)
class Ctx:
    vocab: dict
    en: dict
    vf_map: dict
    char_ids: frozenset
    package_version: str
    report: dict
    sources: EorSources


def build_context(vocab: dict, sources: EorSources, package_version: str, report: dict) -> Ctx:
    char_ids = frozenset(sources.classes_all.keys()) if sources.characters_json_found else frozenset()
    return Ctx(
        vocab=vocab, en=sources.en, vf_map=sources.vf_map, char_ids=char_ids,
        package_version=package_version, report=report, sources=sources,
    )


# ────────────────────────────────── 8. emit ──────────────────────────────────────────────────

def emit_item_packs(item_packs: dict[str, dict], packs_dir: Path) -> None:
    for pack_id, doc in sorted(item_packs.items()):
        filename = ITEM_PACK_FILENAMES.get(pack_id, f"{pack_id.lower()}.pack.json")
        write_json(Path(packs_dir) / filename, doc)


def _summoner_pack_json(pack_id: str, package_version_semver: str = "1.0.0") -> dict:
    meta = FOLLOWER_PACK_META[pack_id]
    return {
        "id": meta["id"],
        "name": meta["name"],
        "version": package_version_semver,
        "author": "ftk2mods",
        "description": meta["description"],
        "loadOrder": meta["loadOrder"],
        "dependencies": [],
        "enabled": True,
    }


def emit_follower_packs(follower_result: dict, follower_packs_dir: Path) -> None:
    for pack_id, pack in sorted(follower_result["packs"].items()):
        if not pack["followers"]:
            continue
        pack_dir = Path(follower_packs_dir) / pack_id
        write_json(pack_dir / "pack.json", _summoner_pack_json(pack_id))
        write_json(pack_dir / "followers.json", pack["followers"])
        write_json(pack_dir / "localization" / "en.json", pack["localization"])
        write_json(pack_dir / "provenance.json", pack["provenance"])


def _classforge_pack_json(package_version_semver: str = "1.0.0") -> dict:
    return {
        "id": CLASS_PACK_ID,
        "name": "Enhanced Overhaul Classes (re-hosted)",
        "version": package_version_semver,
        "author": "ftk2mods",
        "description": "EOR's 31 EOR_* player classes, re-hosted with CF_EOR_ ids.",
        "loadOrder": 100,
        "dependencies": [],
        "enabled": True,
    }


def emit_class_pack(class_result: dict, class_packs_dir: Path) -> None:
    if not class_result["classes"]:
        return
    pack_dir = Path(class_packs_dir) / CLASS_PACK_ID
    write_json(pack_dir / "pack.json", _classforge_pack_json())
    write_json(pack_dir / "classes.json", class_result["classes"])
    write_json(pack_dir / "localization" / "en.json", class_result["localization"])
    write_json(pack_dir / "provenance.json", class_result["provenance"])


def emit_reports(report: dict, follower_result: dict, stats_report: Optional[dict],
                  report_dir: Path, source: Path, package_version: str, *,
                  dry_run: bool, wrote_packs: bool) -> None:
    report_dir = Path(report_dir)
    findings = _sorted_findings(report)
    n_blocking = sum(1 for f in findings if f["severity"] == "blocking")
    n_warn = sum(1 for f in findings if f["severity"] == "warn")
    n_info = len(findings) - n_blocking - n_warn

    write_json(report_dir / "report.json", {
        "source": str(source),
        "package_version": package_version,
        "dry_run": dry_run,
        "wrote_packs": wrote_packs,
        "vocab_provenance_note": (
            "tools/out/vocab-index.json may be EOR-contaminated (the game install it was "
            "extracted from may have had EOR installed); AllIds may carry EOR_* class ids "
            "that are not vanilla. All ids this tool emits are ARM_/SMN_/CF_-prefixed, so "
            "that cannot cause a false collision negative here -- re-run after a verified-"
            "clean vocab extraction. See design doc Part B, §B7."
        ),
        "summary": {"blocking": n_blocking, "warn": n_warn, "info": n_info,
                    "total": len(findings)},
        "findings": findings,
    })

    if follower_result["dropped"] or follower_result["packs"]:
        write_json(report_dir / "dropped.json",
                   sorted(follower_result["dropped"], key=lambda e: e["eor_id"]))
    if follower_result["vanilla_overrides"] or follower_result["packs"]:
        write_json(report_dir / "vanilla-overrides.json",
                   sorted(follower_result["vanilla_overrides"], key=lambda e: e["eor_id"]))
    if stats_report is not None:
        write_text(report_dir / "eor-class-stats-report.md", render_stats_report_md(stats_report))


# ─────────────────────────────────── pipeline ────────────────────────────────────────────────

class RunResult(NamedTuple):
    item_packs: dict
    follower_result: dict
    class_result: dict
    stats_report: Optional[dict]
    aborted: bool


def check_id_collisions(emitted_ids: list[str], vocab: dict, report: dict) -> bool:
    """§B7. Every emitted id checked against vocab AllIds/ItemIds and against ids emitted so
    far in this run. Returns True iff at least one collision was found (+ recorded)."""
    known = set(vocab.get("AllIds", [])) | set(vocab.get("ItemIds", []))
    seen: set[str] = set()
    collided = False
    for eid in sorted(emitted_ids):
        if eid in known:
            record(report, "id_collision", severity="blocking", id=eid,
                   message="id collides with a live id in the vocab snapshot (AllIds/ItemIds)")
            collided = True
        if eid in seen:
            record(report, "id_collision", severity="blocking", id=eid,
                   message="id emitted more than once within this run")
            collided = True
        seen.add(eid)
    return collided


def run_pipeline(ctx: Ctx, selected: set[str], *, check_invariants: bool = True) -> RunResult:
    """The whole converter minus argument parsing and disk writes -- everything is computed
    in memory first (§B7: "before writing anything"), so a global id-collision (or, when
    check_invariants=True, a corpus-shape) blocking finding can abort with nothing written.

    check_invariants=False is used by tests that run small synthetic fixtures through the
    full pipeline without also having to fake the real package's exact measured sizes; the
    CLI entry point (main()) always leaves it True.
    """
    report = ctx.report

    if check_invariants:
        if not assert_corpus_invariants(ctx.sources, ctx.vocab, report):
            return RunResult({}, {"packs": {}, "dropped": [], "vanilla_overrides": []}, {}, None,
                              aborted=True)

    if any(_is_abort_finding(f) for f in report["findings"]):
        return RunResult({}, {"packs": {}, "dropped": [], "vanilla_overrides": []}, {}, None,
                          aborted=True)

    item_packs: dict = {}
    follower_result: dict = {"packs": {}, "dropped": [], "vanilla_overrides": []}
    class_result: dict = {"classes": {}, "localization": {}, "provenance": {}}
    stats_report = None

    if "items" in selected:
        item_packs = convert_items(ctx)

    have_characters = ctx.sources.characters_json_found
    if ("followers" in selected or "classes" in selected) and not have_characters:
        record(report, "characters_json_missing", severity="blocking",
               message=f"Characters.json not found at {ctx.sources.characters_json_path}; "
                       "followers/classes converters need it for ConfigName/class resolution")

    if any(_is_abort_finding(f) for f in report["findings"]):
        return RunResult({}, {"packs": {}, "dropped": [], "vanilla_overrides": []}, {}, None,
                          aborted=True)

    if "followers" in selected and have_characters:
        follower_result = convert_followers(ctx)
    if "classes" in selected and have_characters:
        class_result = convert_classes(ctx)
        vanilla_player_classes = {
            k: v for k, v in ctx.sources.classes_all.items()
            if not k.startswith("EOR_") and "PLAYER" in (v.get("Tags") or [])
        }
        stats_report = build_stats_report(vanilla_player_classes, ctx.sources.classes)

    emitted_ids: list[str] = []
    for doc in item_packs.values():
        emitted_ids.extend(e["Id"] for e in doc["Items"])
    for pack in follower_result["packs"].values():
        emitted_ids.extend(pack["followers"].keys())
    emitted_ids.extend(class_result["classes"].keys())

    if check_id_collisions(emitted_ids, ctx.vocab, report):
        return RunResult({}, {"packs": {}, "dropped": [], "vanilla_overrides": []}, {}, None,
                          aborted=True)

    return RunResult(item_packs, follower_result, class_result, stats_report, aborted=False)


def emit_all(result: RunResult, repo_root: Path) -> None:
    if result.item_packs:
        emit_item_packs(result.item_packs, Path(repo_root) / "FTK2.Armory" / "packs")
    if result.follower_result["packs"]:
        emit_follower_packs(result.follower_result,
                             Path(repo_root) / "FTK2.Summoner" / "data" / "FollowerPacks")
    if result.class_result["classes"]:
        emit_class_pack(result.class_result,
                         Path(repo_root) / "FTK2.ClassForge" / "data" / "ClassPacks")


# ───────────────────────────────────── 9. cli ────────────────────────────────────────────────

_HELP_EPILOG = (
    "NOTE: tools/out/vocab-index.json may be EOR-contaminated -- the game install it was "
    "extracted from may itself have had an EOR package installed, so its AllIds can carry "
    "EOR_* class ids that are not vanilla (design doc Part B, §B7). Every id this tool emits "
    "is ARM_/SMN_/CF_-prefixed, so that contamination cannot cause a false id-collision "
    "negative; re-run after the vocab index is regenerated from a verified-clean install."
)


def main(argv: Optional[list[str]] = None) -> int:
    ap = argparse.ArgumentParser(
        prog="eor_import.py",
        description="Convert an EOR package into FTK2.Armory/Summoner/ClassForge packs.",
        epilog=_HELP_EPILOG,
    )
    ap.add_argument("--source", required=True, type=Path,
                     help="EOR package's BepInEx/plugins directory (read-only)")
    ap.add_argument("--repo-root", type=Path, default=None,
                     help="repo root outputs are written under (default: inferred from this file)")
    ap.add_argument("--vocab", type=Path, default=DEFAULT_VOCAB)
    ap.add_argument("--only", default="items,followers,classes",
                     help="comma-separated subset of items,followers,classes")
    ap.add_argument("--report-dir", type=Path, default=None)
    ap.add_argument("--package-version", default=PACKAGE_VERSION_DEFAULT)
    ap.add_argument("--dry-run", action="store_true", help="compute and report, write nothing")
    args = ap.parse_args(argv)

    repo_root = (args.repo_root or REPO_ROOT_DEFAULT).resolve()
    report_dir = (args.report_dir or (repo_root / "tools" / "out" / "eor-import")).resolve()
    selected = {s.strip() for s in args.only.split(",") if s.strip()}
    unknown = selected - {"items", "followers", "classes"}
    if unknown:
        ap.error(f"--only: unknown converter(s) {sorted(unknown)}")

    report = make_report()
    source = args.source.resolve()

    vocab: dict = {}
    if args.vocab and Path(args.vocab).is_file():
        vocab = load_vocab(args.vocab)
    else:
        record(report, "vocab_missing", severity="blocking",
               message=f"--vocab file not found: {args.vocab}")

    sources = load_sources(source)
    ctx = build_context(vocab, sources, args.package_version, report)

    result = run_pipeline(ctx, selected, check_invariants=True)

    wrote_packs = False
    if not result.aborted and not args.dry_run:
        emit_all(result, repo_root)
        wrote_packs = True

    emit_reports(report, result.follower_result, result.stats_report, report_dir, source,
                 args.package_version, dry_run=args.dry_run, wrote_packs=wrote_packs)

    findings = report["findings"]
    n_blocking = sum(1 for f in findings if f["severity"] == "blocking")
    n_warn = sum(1 for f in findings if f["severity"] == "warn")
    n_info = len(findings) - n_blocking - n_warn
    print(f"eor_import: {len(findings)} findings ({n_blocking} blocking, {n_warn} warn, "
          f"{n_info} info)")
    for pack_id, doc in sorted(result.item_packs.items()):
        print(f"  {pack_id}: {len(doc['Items'])} items")
    for pack_id, pack in sorted(result.follower_result["packs"].items()):
        print(f"  {pack_id}: {len(pack['followers'])} followers")
    if result.follower_result["packs"]:
        print(f"  dropped: {len(result.follower_result['dropped'])}, "
              f"vanilla_overrides: {len(result.follower_result['vanilla_overrides'])}")
    if result.class_result["classes"]:
        print(f"  {CLASS_PACK_ID}: {len(result.class_result['classes'])} classes")
    if result.aborted:
        print("eor_import: ABORTED before writing anything -- see report.json for blocking "
              "findings")
    elif args.dry_run:
        print("eor_import: dry-run, nothing written")
    print(f"reports written to {report_dir}")

    return 1 if n_blocking else 0


if __name__ == "__main__":
    sys.exit(main())
