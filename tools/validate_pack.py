"""The shipping gate: validate an item-manifest pack against the live-game vocab index.

Checks (see tools/manifest_schema.md for the manifest contract):
  - ARM_ prefix, id uniqueness within the pack AND against every live game id
  - every Tag / Passive / Stat key / Slot / Class / Rarity / Material exists in vocab
  - every ability reference (Interactable.Abilities keys, AbilityBag, AbilityFillBag)
    exists in the live Abilities.json — EOR's loader hard-skips items that fail this
  - Equippable.Slots non-empty (EOR loader rejects empty)
  - VisualFallback present and resolving to a live item id (EOR quarantines custom
    items without safe visuals: strips DROPPABLE/market tags — see eor-loader-notes.md)
  - Loc completeness (Name + Description)
  - Icon.Base resolves in the sprite index (when one is provided)
  - power budget sanity vs measured StatCurves (WARN > p50*warn_mult, ERROR > max*fail_mult)

Usage:
    python tools/validate_pack.py <pack.json> [--strict] [--vocab tools/out/vocab-index.json]
                                  [--sprites tools/out/sprite-index.json]

Exit code 1 on any ERROR (also on WARN with --strict).
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
DEFAULT_VOCAB = TOOLS_DIR / "out" / "vocab-index.json"
DEFAULT_SPRITES = TOOLS_DIR / "out" / "sprite-index.json"
CONFIG_PATH = TOOLS_DIR / "validator-config.json"

ID_PREFIX = "ARM_"


def _finding(level: str, item_id: str, check: str, msg: str) -> dict:
    return {"level": level, "id": item_id, "check": check, "msg": msg}


def _load_config() -> dict:
    if CONFIG_PATH.is_file():
        with open(CONFIG_PATH, encoding="utf-8") as f:
            return json.load(f)
    return {"warn_mult": 1.5, "fail_mult": 2.0}


def validate_pack(pack: dict, vocab: dict, sprite_index: dict | None = None) -> list[dict]:
    cfg = _load_config()
    warn_mult = cfg.get("warn_mult", 1.5)
    fail_mult = cfg.get("fail_mult", 2.0)

    findings: list[dict] = []
    all_ids = set(vocab.get("AllIds", []))
    abilities = set(vocab.get("Abilities", []))
    tags = set(vocab.get("Tags", {}))
    skills = set(vocab.get("Skills", []))
    slots = set(vocab.get("Slots", []))
    classes = set(vocab.get("Classes", []))
    rarities = set(vocab.get("Rarities", []))
    materials = set(vocab.get("Materials", []))
    stat_keys = set(vocab.get("StatKeys", []))
    curves = vocab.get("StatCurves", {})

    seen: set[str] = set()
    for entry in pack.get("Items", []):
        item_id = entry.get("Id", "<missing>")
        err = lambda check, msg: findings.append(_finding("ERROR", item_id, check, msg))  # noqa: E731
        warn = lambda check, msg: findings.append(_finding("WARN", item_id, check, msg))  # noqa: E731

        if not item_id.startswith(ID_PREFIX) or item_id != item_id.upper():
            err("bad_prefix", f"id must be UPPERCASE and start with {ID_PREFIX}")
        if item_id in seen or item_id in all_ids:
            err("dup_id", "id already exists (in this pack or in the live game)")
        seen.add(item_id)

        thing = entry.get("Thing") or {}
        cls = thing.get("Class")
        rarity = thing.get("Rarity")
        if cls and cls not in classes:
            err("unknown_class", f"Class {cls} not observed in live data")
        if rarity and rarity not in rarities:
            err("unknown_rarity", f"Rarity {rarity} not observed in live data")
        material = thing.get("Material")
        if material and material not in materials:
            err("unknown_material", f"Material {material} not observed in live data")
        for tag in thing.get("Tags") or []:
            if tag.startswith(ID_PREFIX):
                continue  # our own namespace tags are allowed
            if tag not in tags:
                err("unknown_tag", f"tag {tag} not observed in live data")

        eq = thing.get("Equippable")
        if eq is not None:
            slot_list = eq.get("Slots")
            if slot_list is not None and len(slot_list) == 0:
                err("empty_slots", "EOR loader rejects Equippable with zero slots")
            for s in slot_list or []:
                if s not in slots:
                    err("unknown_slot", f"slot {s} not observed in live data")
            for stat in (eq.get("Stats") or {}):
                if stat not in stat_keys:
                    err("unknown_stat_key", f"stat {stat} not observed on live equipment")
            for passive in eq.get("Passives") or []:
                if passive not in skills and passive not in all_ids:
                    err("unknown_passive", f"passive {passive} not found in live data")

            if rarity == "NONE":
                warn("rarity_none", "equippable with Rarity NONE is unusual (legal but check)")

            # power budget vs measured curve for this (Class, Rarity)
            curve = curves.get(f"{cls}|{rarity}", {}).get("stats", {})
            for stat, val in (eq.get("Stats") or {}).items():
                ref = curve.get(stat)
                if ref is None or not isinstance(val, (int, float)):
                    continue
                if val > ref["max"] * fail_mult:
                    err("budget_error",
                        f"{stat}={val} exceeds {fail_mult}x observed max "
                        f"({ref['max']}) for {cls}|{rarity}")
                elif val > ref["p50"] * warn_mult and val > ref["max"]:
                    warn("budget_warn",
                         f"{stat}={val} above observed range (p50 {ref['p50']}, "
                         f"max {ref['max']}) for {cls}|{rarity}")

        inter = thing.get("Interactable")
        if inter is not None:
            declared = set((inter.get("Abilities") or {}).keys())
            for ab in declared:
                if ab not in abilities:
                    err("unknown_ability", f"ability {ab} not in live Abilities.json "
                        "(EOR loader skips the whole item)")
            for bag_name in ("AbilityBag", "AbilityFillBag"):
                for ab in inter.get(bag_name) or []:
                    if ab not in abilities and ab not in declared:
                        err("unknown_ability", f"{bag_name} entry {ab} unknown")

        vf = entry.get("VisualFallback")
        if not vf:
            err("missing_visual_fallback",
                "every ARM_ item needs a VisualFallback (quarantine risk, see eor-loader-notes)")
        elif vf not in set(vocab.get("ItemIds", [])):
            err("unknown_visual_fallback", f"VisualFallback {vf} is not a live item id")

        loc = entry.get("Loc") or {}
        if not loc.get("Name") or not loc.get("Description"):
            err("missing_loc", "Loc.Name and Loc.Description are required")

        icon = entry.get("Icon")
        if icon and sprite_index is not None:
            if icon.get("Base") not in sprite_index:
                err("unknown_icon_base", f"Icon.Base {icon.get('Base')} not in sprite index")

    return findings


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("pack", type=Path)
    ap.add_argument("--vocab", type=Path, default=DEFAULT_VOCAB)
    ap.add_argument("--sprites", type=Path, default=DEFAULT_SPRITES)
    ap.add_argument("--strict", action="store_true")
    args = ap.parse_args()

    with open(args.pack, encoding="utf-8") as f:
        pack = json.load(f)
    with open(args.vocab, encoding="utf-8") as f:
        vocab = json.load(f)
    sprites = None
    if args.sprites and Path(args.sprites).is_file():
        with open(args.sprites, encoding="utf-8") as f:
            sprites = json.load(f)

    findings = validate_pack(pack, vocab, sprites)
    for f_ in findings:
        print(f"[{f_['level']}] {f_['id']} {f_['check']}: {f_['msg']}")
    n_err = sum(1 for f_ in findings if f_["level"] == "ERROR")
    n_warn = len(findings) - n_err
    n_items = len(pack.get("Items", []))
    print(f"{args.pack.name}: {n_items} items, {n_err} errors, {n_warn} warnings")
    if n_err or (args.strict and n_warn):
        sys.exit(1)


if __name__ == "__main__":
    main()
