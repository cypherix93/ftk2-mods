"""Deterministic themed name generation from the profile's NameGrammar."""

from __future__ import annotations

import random


def make_name(rng: random.Random, grammar: dict, cls: str, theme_name: str) -> str:
    patterns = grammar.get("Patterns", ["{adj} {noun}"])
    nouns = grammar.get("Nouns", {}).get(cls) or [cls.replace("_2H", "").title()]
    adjs = grammar.get("Adj", ["Forged"])
    themes = grammar.get("Theme", [theme_name.title()])
    pattern = patterns[rng.randrange(len(patterns))]
    return (
        pattern.replace("{adj}", adjs[rng.randrange(len(adjs))])
        .replace("{noun}", nouns[rng.randrange(len(nouns))])
        .replace("{theme}", themes[rng.randrange(len(themes))])
    )
