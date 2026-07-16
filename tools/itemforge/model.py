"""Budget/archetype/affix model for the item forge.

All numbers come from the profile + the measured vocab-index StatCurves — nothing
tuned in code. Every random draw goes through the single random.Random instance
passed in (determinism: same (profile, vocab, seed) -> identical output).

Mechanics grounding (docs/research/game-mechanics.md):
  - budgets sit on the game's measured per-(Class,Rarity) stat curves
  - MinTier/MaxTier clamped 0..3 [O-LOOT-01]; Value >= 1 [O-LOOT-04]
  - rarity tag mirrored into Tags like live items (vocab-summary.md)
"""

from __future__ import annotations

import random

# Measured RARE -> ARTIFACT power ratio fallback for classes lacking an ARTIFACT
# curve cell (vocab-summary.md: ARTIFACT cells are sparse). Applied to the nearest
# lower-rarity cell when a cell is missing entirely.
RARITY_FALLBACK_ORDER = ["COMMON", "UNCOMMON", "RARE", "ARTIFACT"]
MISSING_CELL_STEP_RATIO = 1.35


def curve_cell(vocab: dict, cls: str, rarity: str) -> tuple[dict, float]:
    """Return (stats-curve-dict, scale) for cls|rarity, falling back down the
    rarity ladder with a compounding step ratio when the exact cell is absent."""
    curves = vocab.get("StatCurves", {})
    idx = RARITY_FALLBACK_ORDER.index(rarity) if rarity in RARITY_FALLBACK_ORDER else 0
    for back in range(idx + 1):
        key = f"{cls}|{RARITY_FALLBACK_ORDER[idx - back]}"
        cell = curves.get(key)
        if cell and cell.get("stats"):
            return cell["stats"], MISSING_CELL_STEP_RATIO ** back
    return {}, 1.0


def pick_weighted(rng: random.Random, weights: dict[str, float]) -> str:
    items = sorted((k, v) for k, v in weights.items() if v > 0)
    total = sum(v for _, v in items)
    roll = rng.random() * total
    acc = 0.0
    for key, w in items:
        acc += w
        if roll < acc:
            return key
    return items[-1][0]


def compute_budget(rng: random.Random, profile: dict, vocab: dict,
                   cls: str, rarity: str) -> tuple[float, dict]:
    """Budget = measured typical total statline for the cell x multiplier x jitter."""
    stats, scale = curve_cell(vocab, cls, rarity)
    typical_total = sum(s["p50"] for s in stats.values()) * scale
    if typical_total <= 0:
        typical_total = 10.0
    chaos = profile.get("Chaos", 0.0)
    jitter = 1.0 + (rng.random() * 2 - 1) * chaos
    return typical_total * profile.get("BudgetMultiplier", 1.0) * jitter, stats


def allocate_stats(rng: random.Random, profile: dict, budget: float,
                   arch_stats: dict[str, float], theme_stats: dict[str, float],
                   curve: dict, synergy_bias: float) -> dict[str, int]:
    """Split the budget across archetype stats (theme stats mixed in with
    probability synergy_bias), proportional to weights, capped near observed max."""
    weights = dict(arch_stats)
    if theme_stats and rng.random() < synergy_bias:
        for stat, w in sorted(theme_stats.items()):
            weights[stat] = weights.get(stat, 0) + w
    total_w = sum(weights.values()) or 1.0
    out: dict[str, int] = {}
    for stat in sorted(weights):
        share = weights[stat] / total_w
        val = round(budget * share)
        ref = curve.get(stat)
        if ref:
            # stay under the validator's hard ceiling (max*2), aim under max*1.3
            val = min(val, int(ref["max"] * 1.3))
        if val > 0:
            out[stat] = val
    return out or {min(weights, key=weights.get): 1}


def apply_curse(rng: random.Random, profile: dict, stats: dict[str, int],
                budget: float) -> tuple[dict[str, int], float]:
    """Add a negative stat and return extra budget (the rebate)."""
    curse_stats = profile.get("CurseStats", {})
    if not curse_stats:
        return stats, 0.0
    stat = pick_weighted(rng, {k: 1 for k in curse_stats})
    magnitude = curse_stats[stat]
    stats = dict(stats)
    stats[stat] = stats.get(stat, 0) - magnitude
    return stats, budget * profile.get("CurseRebate", 0.3)


def maybe_pick_passive(rng: random.Random, profile: dict, vocab: dict,
                       theme: dict, budget: float) -> tuple[str | None, float]:
    """Spend up to PassiveBudgetShare of budget on a theme skill, priced by profile."""
    share = profile.get("PassiveBudgetShare", 0.0)
    prices = profile.get("SkillPrices", {})
    candidates = [s for s in theme.get("Skills", []) if s in set(vocab.get("Skills", []))]
    if not candidates or share <= 0:
        return None, 0.0
    skill = candidates[rng.randrange(len(candidates))]
    cost = budget * prices.get(skill, share)
    if cost > budget * share:
        return None, 0.0
    return skill, cost
