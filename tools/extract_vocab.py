"""Extract a machine-readable vocabulary index from the live FTK2 game configs.

Reads the game's Configs\\JSON~ tree (READ-ONLY) and emits vocab-index.json:
every id, tag, skill, status, slot token, class, rarity, material, tier band,
plus measured stat distributions per (Class, Rarity). This is the ground truth
that validate_pack.py and itemforge build against.

Usage:
    python tools/extract_vocab.py [--configs <dir>] [--out tools/out/vocab-index.json]
"""

from __future__ import annotations

import argparse
import json
import statistics
from collections import Counter, defaultdict
from pathlib import Path

DEFAULT_CONFIGS = Path(
    r"E:\Games\Steam\steamapps\common\For The King II"
    r"\For The King II_Data\StreamingAssets\Assets\Configs\JSON~"
)
DEFAULT_OUT = Path(__file__).resolve().parent / "out" / "vocab-index.json"


def _read_json(path: Path) -> dict:
    # utf-8-sig: the live Characters.json ships a BOM; harmless elsewhere.
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def _summary(values: list[float]) -> dict:
    return {
        "min": min(values),
        "max": max(values),
        "mean": sum(values) / len(values),
        "p50": statistics.median(values),
    }


def build_index(configs_dir: Path) -> dict:
    configs_dir = Path(configs_dir)

    things: dict[str, dict] = {}
    things_dir = configs_dir / "Things"
    if things_dir.is_dir():
        for f in sorted(things_dir.glob("*.json")):
            things.update(_read_json(f))

    statuses = _read_json(p) if (p := configs_dir / "StatusEffects.json").is_file() else {}
    skills = _read_json(p) if (p := configs_dir / "SkillConfigs.json").is_file() else {}
    abilities = _read_json(p) if (p := configs_dir / "Abilities.json").is_file() else {}
    characters = _read_json(p) if (p := configs_dir / "Characters.json").is_file() else {}

    tags: Counter[str] = Counter()
    slots: set[str] = set()
    classes: set[str] = set()
    rarities: set[str] = set()
    materials: set[str] = set()
    stat_keys: set[str] = set()
    tier_bands: dict[str, dict] = {}
    donors: defaultdict[str, list[str]] = defaultdict(list)
    # (class, rarity) -> {stat -> [values]}, plus item Value samples
    curve_stats: defaultdict[str, defaultdict[str, list[float]]] = defaultdict(
        lambda: defaultdict(list)
    )
    curve_values: defaultdict[str, list[float]] = defaultdict(list)

    for item_id in sorted(things):
        cfg = things[item_id] or {}
        for t in cfg.get("Tags") or []:
            tags[t] += 1
        cls = cfg.get("Class")
        if cls:
            classes.add(cls)
        rarity = cfg.get("Rarity")
        if rarity:
            rarities.add(rarity)
        material = cfg.get("Material")
        if material:
            materials.add(material)
        if "MinTier" in cfg or "MaxTier" in cfg:
            tier_bands[item_id] = {
                "MinTier": cfg.get("MinTier", 0),
                "MaxTier": cfg.get("MaxTier", 0),
            }
        eq = cfg.get("Equippable")
        if eq:
            for s in eq.get("Slots") or []:
                slots.add(s)
            if cls:
                donors[cls].append(item_id)
            stats = eq.get("Stats") or {}
            stat_keys.update(stats)
            if cls and rarity:
                key = f"{cls}|{rarity}"
                for stat, val in stats.items():
                    if isinstance(val, (int, float)):
                        curve_stats[key][stat].append(val)
                value = cfg.get("Value")
                if isinstance(value, (int, float)):
                    curve_values[key].append(value)

    # Per-class modal Slots and a representative Interactable template (weapons need
    # working ability blocks; the template comes from the class's most common
    # ability-key-set, copied verbatim from one representative live item).
    slot_votes: defaultdict[str, Counter] = defaultdict(Counter)
    ability_sets: defaultdict[str, Counter] = defaultdict(Counter)
    ability_examples: dict[tuple[str, tuple], dict] = {}
    for item_id in sorted(things):
        cfg = things[item_id] or {}
        cls = cfg.get("Class")
        eq = cfg.get("Equippable")
        if not cls or not eq:
            continue
        slot_key = tuple(eq.get("Slots") or [])
        if slot_key:
            slot_votes[cls][slot_key] += 1
        inter = cfg.get("Interactable")
        if inter and inter.get("Abilities"):
            key = tuple(sorted(inter["Abilities"].keys()))
            ability_sets[cls][key] += 1
            ability_examples.setdefault((cls, key), inter)

    class_slots = {
        cls: list(votes.most_common(1)[0][0]) for cls, votes in sorted(slot_votes.items())
    }
    class_ability_templates = {}
    for cls, votes in sorted(ability_sets.items()):
        modal_key = votes.most_common(1)[0][0]
        class_ability_templates[cls] = ability_examples[(cls, modal_key)]

    stat_curves = {}
    for key in sorted(set(curve_stats) | set(curve_values)):
        per_stat = {
            stat: _summary(vals)
            for stat, vals in sorted(curve_stats[key].items())
            if vals
        }
        entry = {
            # count = items of this (class, rarity) that contributed a Value sample
            "count": len(curve_values[key]),
            "stats": per_stat,
        }
        if curve_values[key]:
            entry["value"] = _summary(curve_values[key])
        stat_curves[key] = entry

    item_ids = sorted(things)
    all_ids = sorted(
        set(item_ids)
        | set(characters)
        | set(abilities)
        | set(statuses)
        | set(skills)
    )

    return {
        "ItemIds": item_ids,
        "AllIds": all_ids,
        "Tags": dict(sorted(tags.items())),
        "Skills": sorted(skills),
        "Statuses": sorted(statuses),
        "Slots": sorted(slots),
        "Classes": sorted(classes),
        "Rarities": sorted(rarities),
        "Materials": sorted(materials),
        "StatKeys": sorted(stat_keys),
        "TierBands": tier_bands,
        "StatCurves": stat_curves,
        "VisualDonors": {k: sorted(v) for k, v in sorted(donors.items())},
        "Abilities": sorted(abilities),
        "ClassSlots": class_slots,
        "ClassAbilityTemplates": class_ability_templates,
    }


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--configs", type=Path, default=DEFAULT_CONFIGS)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = ap.parse_args()

    idx = build_index(args.configs)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(idx, f, sort_keys=True, indent=2)
        f.write("\n")
    print(
        f"vocab-index written to {args.out}: "
        f"{len(idx['ItemIds'])} items, {len(idx['Tags'])} tags, "
        f"{len(idx['Skills'])} skills, {len(idx['Statuses'])} statuses, "
        f"{len(idx['Abilities'])} abilities, {len(idx['StatCurves'])} curve cells"
    )


if __name__ == "__main__":
    main()
