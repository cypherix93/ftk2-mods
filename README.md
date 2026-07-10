# FTK2 Mods

A collection of modular, data-driven mods for **For The King II** (Unity Mono, BepInEx 5 + Harmony).

Each mod is an **extensible engine**: the C# plugin implements mechanics; everything tunable lives in
JSON data files and BepInEx config knobs so behavior can be changed without recompiling.

## Mods

| Mod | What it is | Priority | Status |
|---|---|---|---|
| [FTK2.WarBrain](FTK2.WarBrain/SPEC.md) | Enemy battle-AI engine: utility scoring, tactical profiles, enemy memory of seen player abilities | P0 | Spec draft |
| [FTK2.ClassForge](FTK2.ClassForge/SPEC.md) | Class/trait content engine: data-driven class packs, trait injection, custom skill hooks | P1 | Spec draft |
| [FTK2.Forge](FTK2.Forge/SPEC.md) | Item upgrade engine: item levels, upgrade orbs, generated tier ladders on top of native crafting | P1 | Spec draft |
| [FTK2.Runeworks](FTK2.Runeworks/SPEC.md) | Socket/rune/gem engine: slot gems into dropped gear for stats, passives, and abilities | P2 | Spec draft |
| [FTK2.Summoner](FTK2.Summoner/SPEC.md) | Summon & evolution engine: trainer-style classes, persistent summons, evolution chains | P2 | Spec draft |
| [FTK2.Questsmith](FTK2.Questsmith/SPEC.md) | Quest engine extensions: new objective/trigger verbs + example scripted quest pack | P2 | Spec draft |
| [FTK2.Venue](FTK2.Venue/SPEC.md) | Battle-grid control: data-driven grid selection (Extended grid in normal fights) | P3 | Spec draft |
| [FTK2.ActionPoints](FTK2.ActionPoints/SPEC.md) | Pooled action-point economy (Divinity/BG3 style) with carryover | P3 | Spec draft |
| [FTK2.DevKit](FTK2.DevKit/SPEC.md) | Modder QoL: JSON hot-reload, config dumps, AI decision logging | P0 (dev) | Spec draft |

## Docs

- [docs/feasibility.md](docs/feasibility.md) — the original feasibility study (start here)
- [docs/CONVENTIONS.md](docs/CONVENTIONS.md) — repo + engine design conventions all mods follow
- [docs/research/game-code-reference.md](docs/research/game-code-reference.md) — verified FTK2.dll patch targets
- [docs/research/data-schemas.md](docs/research/data-schemas.md) — game JSON config schemas

## Environment

- Game: `E:\Games\Steam\steamapps\common\For The King II` (Mono; logic in `For The King II_Data\Managed\FTK2.dll`)
- Game data: `For The King II_Data\StreamingAssets\Assets\Configs\JSON~\`
- Loader: BepInEx 5.4.23 (as shipped with Enhanced Overhaul Revamped v0.7.0.51)
- Reference mod to decompile: `EnhancedOverhaulRemix.dll` (proves class injection, affixes, save persistence, MP sync)
