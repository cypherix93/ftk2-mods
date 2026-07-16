"""The item forge: seeded offline random item generator.

forge_items(profile, vocab, seed, count) -> list of manifest entries (see
tools/manifest_schema.md). Pure function of its inputs: a single
random.Random(seed) drives every draw, iteration orders are sorted, no
wall-clock anywhere.

CLI:
    python tools/itemforge/forge.py --profile tools/itemforge/profiles/baseline.json
        --seed 42 --count 100 --out tools/out/forge_runs/run.pack.json
        [--audit tools/out/forge_runs/run-audit.md] [--sprites tools/out]
"""

from __future__ import annotations

import argparse
import json
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from itemforge import model  # noqa: E402
from itemforge.naming import make_name  # noqa: E402

WEAPON_SLOT_CLASSES_TAG = "WEAPON"
ARMOR_CLASS_PREFIX = "ARMOR_"


def _base_tags(vocab: dict, cls: str, rarity: str) -> list[str]:
    """Live-vocabulary tags that make the item reachable by loot and markets
    (game-mechanics.md O-LOOT-04/05, vocab-summary.md market tag reality)."""
    tags = ["DROPPABLE", "TOWN_MARKET", "DUNGEON_MARKET"]
    live = set(vocab.get("Tags", {}))
    if rarity in live:
        tags.append(rarity)
    if cls.startswith(ARMOR_CLASS_PREFIX):
        tags.append("ARMOR")
        if cls in live:
            tags.append(cls)
    else:
        tags.extend(t for t in ("WEAPON", "HAND_EQUIP") if t in live)
        base_cls = cls.replace("_2H", "")
        if base_cls in live:
            tags.append(base_cls)
        two_hand = "WEAPON_TWO_HAND" if cls.endswith("_2H") else "WEAPON_ONE_HAND"
        if two_hand in live:
            tags.append(two_hand)
    return [t for t in tags if t in live]


def _theme_tags(vocab: dict, theme: dict) -> list[str]:
    live = set(vocab.get("Tags", {}))
    return [t for t in theme.get("Tags", []) if t in live]


def _value_for(rng: random.Random, vocab: dict, cls: str, rarity: str,
               budget_ratio: float) -> int:
    cell = vocab.get("StatCurves", {}).get(f"{cls}|{rarity}", {})
    v = cell.get("value", {})
    base = v.get("p50", 25)
    return max(1, round(base * max(0.5, budget_ratio)))


def _icon_for(rng: random.Random, sprite_index: dict | None, donor: str,
              theme_style: dict) -> dict | None:
    if not sprite_index:
        return None
    base = f"PRERENDER_{donor}"
    if base not in sprite_index:
        return None
    transforms = []
    if "Hue" in theme_style:
        transforms.append({"Op": "HSV_SHIFT", "H": theme_style["Hue"],
                           "S": theme_style.get("Sat", 0.1), "V": 0.0})
    if "Glow" in theme_style:
        transforms.append({"Op": "GLOW", "Color": theme_style["Glow"],
                           "Radius": 5, "Strength": 0.8})
    return {"Base": base, "Transforms": transforms}


def forge_items(profile: dict, vocab: dict, seed: int, count: int,
                sprite_index: dict | None = None) -> list[dict]:
    rng = random.Random(seed)
    themes = profile.get("AffixThemes", {})
    theme_names = sorted(themes) or ["plain"]
    grammar = profile.get("NameGrammar", {})
    tier_lo, tier_hi = profile.get("TierRange", [0, 3])
    tier_lo, tier_hi = max(0, tier_lo), min(3, tier_hi)
    prefix = f"ARM_FRG_{profile['Name'].upper()}_{seed}"

    items: list[dict] = []
    for i in range(count):
        cls = model.pick_weighted(rng, profile["Classes"])
        rarity = model.pick_weighted(rng, profile["Rarities"])
        archetypes = profile.get("Archetypes", {}).get(cls) or {
            "generic": {"W": 1, "Stats": {"ATK" if not cls.startswith("ARMOR_") else "DEF": 1}}
        }
        arch_name = model.pick_weighted(rng, {k: v["W"] for k, v in archetypes.items()})
        arch = archetypes[arch_name]
        theme_name = theme_names[rng.randrange(len(theme_names))]
        theme = themes.get(theme_name, {})

        budget, curve = model.compute_budget(rng, profile, vocab, cls, rarity)
        base_budget = budget

        cursed = rng.random() < profile.get("CurseChance", 0.0)
        passive, passive_cost = model.maybe_pick_passive(rng, profile, vocab, theme, budget)
        if passive:
            budget -= passive_cost

        stats = model.allocate_stats(
            rng, profile, budget, arch.get("Stats", {}),
            theme.get("Stats", {}), curve, profile.get("SynergyBias", 0.5),
        )
        if cursed:
            stats, rebate = model.apply_curse(rng, profile, stats, base_budget)
            if rebate > 0:
                boost_stat = max((s for s in stats if stats[s] > 0),
                                 key=lambda s: stats[s], default=None)
                if boost_stat:
                    ref = curve.get(boost_stat)
                    boosted = stats[boost_stat] + max(1, round(rebate * 0.5))
                    if ref:
                        boosted = min(boosted, int(ref["max"] * 1.5))
                    stats[boost_stat] = boosted

        slots = vocab.get("ClassSlots", {}).get(cls)
        if not slots:
            continue  # class not equippable in live data; skip deterministically
        donors = vocab.get("VisualDonors", {}).get(cls, [])
        if not donors:
            continue
        donor = donors[rng.randrange(len(donors))]

        thing: dict = {
            "ConsumableType": "NONE",
            "Value": _value_for(rng, vocab, cls, rarity, budget / max(1.0, base_budget)),
            "MinTier": tier_lo,
            "MaxTier": tier_hi,
            "Class": cls,
            "Rarity": rarity,
            "Material": "NONE",
            "Hidden": False,
            "Stacks": False,
            "Ammo": 0,
            "Equippable": {
                "Slots": list(slots),
                "Stats": {k: v for k, v in sorted(stats.items()) if v != 0},
                "MaxCharges": -1,
            },
            "Tags": _base_tags(vocab, cls, rarity) + _theme_tags(vocab, theme),
            "Expansion": "BASE",
        }
        if passive:
            thing["Equippable"]["Passives"] = [passive]
        template = vocab.get("ClassAbilityTemplates", {}).get(cls)
        if template and not cls.startswith(ARMOR_CLASS_PREFIX):
            thing["Interactable"] = json.loads(json.dumps(template))

        name = make_name(rng, grammar, cls, theme_name)
        item_id = f"{prefix}_{i:03d}"
        items.append({
            "Id": item_id,
            "Thing": thing,
            "VisualFallback": donor,
            "Loc": {
                "Name": name,
                "Description": profile.get("DescriptionTemplates", {}).get(
                    theme_name, f"A {rarity.lower()} {theme_name} piece from the forge."
                ),
            },
            **({"Icon": icon} if (icon := _icon_for(
                rng, sprite_index, donor, theme.get("IconStyle", {}))) else {}),
            "DesignNote": f"forge: archetype={arch_name}, theme={theme_name}"
                          + (", cursed" if cursed else ""),
            "Provenance": {"Source": "forge", "Profile": profile["Name"], "Seed": seed},
        })
    return items


def write_audit(items: list[dict], profile: dict, vocab: dict, path: Path) -> None:
    from collections import Counter, defaultdict
    by_stat: dict[str, list[int]] = defaultdict(list)
    rarities: Counter = Counter()
    classes: Counter = Counter()
    themes: Counter = Counter()
    for it in items:
        rarities[it["Thing"]["Rarity"]] += 1
        classes[it["Thing"]["Class"]] += 1
        themes[it["DesignNote"].split("theme=")[1].split(",")[0]] += 1
        for s, v in it["Thing"]["Equippable"]["Stats"].items():
            by_stat[s].append(v)
    lines = [f"# Forge audit — profile {profile['Name']}, {len(items)} items", ""]
    lines.append(f"Rarities: {dict(rarities)}")
    lines.append(f"Classes: {dict(classes)}")
    lines.append(f"Themes: {dict(themes)}")
    lines.append("")
    lines.append("| Stat | n | min | mean | max | live p50 range |")
    lines.append("|---|---|---|---|---|---|")
    curves = vocab.get("StatCurves", {})
    for s in sorted(by_stat):
        vals = by_stat[s]
        p50s = [c["stats"][s]["p50"] for c in curves.values() if s in c.get("stats", {})]
        live = f"{min(p50s):.0f}–{max(p50s):.0f}" if p50s else "n/a"
        lines.append(f"| {s} | {len(vals)} | {min(vals)} | "
                     f"{sum(vals)/len(vals):.1f} | {max(vals)} | {live} |")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--profile", type=Path, required=True)
    ap.add_argument("--seed", type=int, required=True)
    ap.add_argument("--count", type=int, default=50)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--audit", type=Path, default=None)
    ap.add_argument("--vocab", type=Path,
                    default=Path(__file__).resolve().parents[1] / "out" / "vocab-index.json")
    ap.add_argument("--sprites", type=Path, default=None)
    args = ap.parse_args()

    with open(args.profile, encoding="utf-8") as f:
        profile = json.load(f)
    with open(args.vocab, encoding="utf-8") as f:
        vocab = json.load(f)
    sprite_index = None
    if args.sprites:
        with open(Path(args.sprites) / "sprite-index.json", encoding="utf-8") as f:
            sprite_index = json.load(f)

    items = forge_items(profile, vocab, args.seed, args.count, sprite_index)
    pack = {"PackId": f"ARM_FRG_{profile['Name'].upper()}_{args.seed}", "Items": items}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(pack, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"forged {len(items)} items -> {args.out}")
    if args.audit:
        write_audit(items, profile, vocab, args.audit)
        print(f"audit -> {args.audit}")


if __name__ == "__main__":
    main()
