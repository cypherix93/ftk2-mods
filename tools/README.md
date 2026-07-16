# tools/ — content-generation pipelines

Repo-level tooling shared across mods (currently feeding `FTK2.Armory`; iconforge will also serve
ClassForge icon packs later). Everything here is offline authoring tooling — nothing in this
directory runs inside the game process.

**The game directory is READ-ONLY. No script in this repo writes, moves, or creates anything under
`E:\Games\Steam\steamapps\common\For The King II`. Installation of shipped packs is a manual,
human-performed step documented in `FTK2.Armory/INSTALL.md`.**

## Layout

| Path | What it is |
|---|---|
| `extract_vocab.py` | Reads the live game's `Configs\JSON~` tree (read-only) and emits `tools/out/vocab-index.json`: every id, tag, skill, status, slot token, class, rarity, material, plus measured stat curves per (class, rarity). Ground truth for the validator and the item forge. Re-run after game updates. |
| `extract_assets.py` | UnityPy-based sprite extractor: exports item icon sprites from the game's asset files (read-only) to `tools/out/sprites/` and builds `tools/out/sprite-index.json`. |
| `iconforge/` | Deterministic icon remix engine (Pillow): recipe-driven transforms (recolor, glow, overlay, composite) over extracted sprites; renders pack icons + contact-sheet HTML. |
| `itemforge/` | Seeded offline random item generator. Profiles in `itemforge/profiles/*.json` hold every knob; same profile + seed → byte-identical output. Emits manifest packs + stat-audit reports. |
| `validate_pack.py` | The shipping gate: validates any manifest pack against `vocab-index.json` (schema shape, id existence, power budget vs measured curves, localization/icon completeness). Nothing lands in `FTK2.Armory/data/` unvalidated. |
| `compile_pack.py` | Compiles a validated manifest pack into ship-shape game JSON (`Things/`, `VisualFallbacks.json`, `Localization/en.json`, icons). |
| `manifest_schema.md` | The shared item-manifest contract all of the above consume/emit. |
| `tests/` | pytest suite: `python -m pytest tools/tests -v` from repo root. |

## Machine-artifact contract

- `tools/out/` — extracted/generated machine artifacts (decompiles, sprites, vocab index, forge
  runs). **Gitignored**; rebuildable from the game install + this tooling.
- `tools/bin/` — downloaded third-party binaries. **Gitignored.**
- Committed artifacts are: tool source, tests, profiles, docs, manifests, and shipped pack output
  under `FTK2.Armory/`.

## Setup

```
python -m pip install --user -r tools/requirements.txt
dotnet tool install --global ilspycmd    # for the research decompiles only
```
