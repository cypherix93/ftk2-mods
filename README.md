# FTK2 Mods

A collection of modular, data-driven mods for **For The King II** (Unity Mono, BepInEx 5 + Harmony).

Each mod is an **extensible engine**: the C# plugin implements mechanics; everything tunable lives in
JSON data files and BepInEx config knobs so behavior can be changed without recompiling.

## Mods

| Mod | What it is | Priority | Status |
|---|---|---|---|
| [FTK2.WarBrain](FTK2.WarBrain/SPEC.md) | Enemy battle-AI engine: utility scoring, tactical profiles, enemy memory of seen player abilities | P0 | **M1 implemented** |
| [FTK2.ClassForge](FTK2.ClassForge/SPEC.md) | Class/trait content engine: data-driven class packs, trait injection, custom skill hooks | P1 | **M1–M3 implemented (offline-verified)** |
| [FTK2.Forge](FTK2.Forge/SPEC.md) | Item upgrade engine: item levels, upgrade orbs, generated tier ladders on top of native crafting | P1 | Spec draft |
| [FTK2.Runeworks](FTK2.Runeworks/SPEC.md) | Socket/rune/gem engine: slot gems into dropped gear for stats, passives, and abilities | P2 | Spec draft |
| [FTK2.Summoner](FTK2.Summoner/SPEC.md) | Summon & evolution engine: trainer-style classes, persistent summons, evolution chains | P2 | **M0 implemented** |
| [FTK2.Questsmith](FTK2.Questsmith/SPEC.md) | Quest engine extensions: new objective/trigger verbs + example scripted quest pack | P2 | Spec draft |
| [FTK2.Venue](FTK2.Venue/SPEC.md) | Battle-grid control: data-driven grid selection (Extended grid in normal fights) | P3 | Spec draft |
| [FTK2.ActionPoints](FTK2.ActionPoints/SPEC.md) | Pooled action-point economy (Divinity/BG3 style) with carryover | P3 | Spec draft |
| [FTK2.DevKit](FTK2.DevKit/SPEC.md) | Modder QoL: JSON hot-reload, config dumps, AI decision logging | P0 (dev) | **ParityService implemented** |
| [FTK2.Armory](FTK2.Armory/SPEC.md) | Item content packs: hand-crafted catalog + curated forge output (zero-code M1) | P1 | **M1 shipped + EOR import packs** |

## Docs

- [docs/feasibility.md](docs/feasibility.md) — the original feasibility study (start here)
- [docs/CONVENTIONS.md](docs/CONVENTIONS.md) — repo + engine design conventions all mods follow
- [docs/research/game-code-reference.md](docs/research/game-code-reference.md) — verified FTK2.dll patch targets
- [docs/research/data-schemas.md](docs/research/data-schemas.md) — game JSON config schemas
- [docs/research/game-mechanics.md](docs/research/game-mechanics.md) — decompile-verified combat + overworld mechanics (claim-id ground truth)
- [docs/research/vocab-summary.md](docs/research/vocab-summary.md) — live-data vocabulary (real enums, stat curves; corrects data-schemas.md)
- [docs/research/eor-loader-notes.md](docs/research/eor-loader-notes.md) — EOR CustomItems/ItemIcons/VisualFallbacks loader mechanics
- [docs/research/eor-0760-content-audit.md](docs/research/eor-0760-content-audit.md) — EOR v0.7.0.60 content audit: what's portable, and why EOR is buggy in MP
- [docs/research/eor-rehost-coverage-matrix.md](docs/research/eor-rehost-coverage-matrix.md) — every EOR mechanic dispositioned PORT/PORT-MODIFIED/PARK against the recipe vocabulary
- [docs/research/eor-rehost-mp-review.md](docs/research/eor-rehost-mp-review.md) — adversarial MP-correctness review of the ClassForge/Summoner/DevKit parity engine code
- [docs/research/eor-rehost-verification.md](docs/research/eor-rehost-verification.md) — full build/test/pack-validation/determinism verification sweep for the EOR-rehost work
- [docs/research/enum-ground-truth.md](docs/research/enum-ground-truth.md) — decompile-verified true enum member lists (Rarity, Material, slots, damage types, roll status, …)
- [docs/research/game-patch-surface-notes.md](docs/research/game-patch-surface-notes.md) — exact Harmony patch-target signatures and parameters for the recipe engine's hooks
- [docs/research/eor-behavior-matrix.md](docs/research/eor-behavior-matrix.md) — exact gameplay semantics of every EOR class skill, trait, and affix, line-cited
- [docs/research/eor-trait-mechanism.md](docs/research/eor-trait-mechanism.md) — how EOR grants custom traits without the `eTraits` enum (ClassForge's no-bridge design source)
- [docs/research/build-template-notes.md](docs/research/build-template-notes.md) — reference-assembly snapshot, build commands, and project-layout conventions for new engines
- [tools/README.md](tools/README.md) — content-generation pipelines (vocab extractor, iconforge, itemforge, validator)
- [FTK2.DevKit/sandbox/LiveDataHarness](FTK2.DevKit/sandbox/LiveDataHarness/README.md) — runs every mod's `.Core` logic against the real game's `Configs`, loaded out-of-process with no Unity/BepInEx/running game (`pwsh -File tools/run-harness.ps1`)

## Environment

- Game: `E:\Games\Steam\steamapps\common\For The King II` (Mono; logic in `For The King II_Data\Managed\FTK2.dll`)
- Game data: `For The King II_Data\StreamingAssets\Assets\Configs\JSON~\`
- Loader: BepInEx 5.4.23 (as shipped with Enhanced Overhaul Revamped v0.7.0.51)
- Reference mod to decompile: `EnhancedOverhaulRemix.dll` (proves class injection, affixes, save persistence, MP sync)
