# FTK2 Content Generation System — design spec

Date: 2026-07-16 · Status: approved for overnight autonomous execution
Owner decisions captured: extract+remix icons · offline forge tool · data-only item vocabulary ·
~60 hand-crafted items depth-first · **game directory is read-only, no install script runs and none
is shipped — manual install instructions only**.

## 1. Purpose & scope

Build the content-generation layer for this repo: the tooling and the first large content drop that
proves FTK2 item creation can be industrialized without sacrificing quality. Three deliverables:

1. **Icon asset pipeline** (`tools/iconforge/`) — extract base-game item sprites, remix them
   deterministically (recolors, glows, overlays, composites) into new on-style icons.
2. **Item forge** (`tools/itemforge/`) — a seeded, offline random item generator with tunable knobs,
   for exploring the randomness/interestingness space and mass-producing candidate items.
3. **Hand-crafted catalog** (`FTK2.Armory/data/`) — ~60 deliberately designed, thematically coherent
   items across all rarities, each synergy grounded in verified game mechanics.

Foundation for all three: a **mechanics ground-truth doc** (`docs/research/game-mechanics.md`) and a
**machine-readable vocabulary index** (`tools/out/vocab-index.json`), both built from primary sources
(decompiled `FTK2.dll`, live game JSON) before any content is designed.

**Deliberately out of scope**: any C# plugin code (Armory M1 is zero-code static data); custom 3D
models (VisualFallbacks covers visuals; icons are the only new art); set-bonus mechanics (needs a
plugin — noted as future Armory M2); runtime/in-game item generation (offline only, by owner
decision); writing anything into the game directory (owner decision: read-only forever); loot-table
*weight* changes (items enter loot/markets via tags only).

## 2. Deliverable-facing behavior

- A designer can run `python tools/extract_vocab.py` after any game update and get a fresh
  vocabulary index + stat-curve report.
- `python tools/itemforge/forge.py --profile profiles/chaotic_epics.json --seed 42 --count 100`
  emits a complete, validated candidate pack: Things JSON + VisualFallbacks + localization + icon
  recipes + a stat-audit report. Same seed → byte-identical output.
- `python tools/iconforge/render.py --manifest <pack>/icons.json` renders every icon recipe to PNG
  and a contact-sheet HTML.
- `python tools/validate_pack.py <pack>` passes/fails any pack (hand-written or generated) against
  the vocab index and power curves. Nothing lands in `FTK2.Armory/data/` unvalidated.
- A player (the owner, manually) copies `FTK2.Armory/data/` content per the install doc and finds
  the new items dropping in-game, rendered with remixed icons and base-game models.

## 3. Architecture — one spine, three stages

Approach chosen over "three independent tools" and "forge-first maximalism": everything hangs off
one **item manifest format** and one **vocabulary index**, so the forge, the catalog, and the icon
pipeline share validation, curation, iconography, and install mechanics.

### 3.1 Stage 0 — ground truth

**`docs/research/game-mechanics.md`** — documents *meaning*, not schema (schema already lives in
`data-schemas.md`). Combat: the 20 stats' actual roles in hit/damage/crit/dodge formulas; skill-roll
mechanics (slots, FOC re-rolls, ACC's effect); damage types vs DEF/RES/EVD; status semantics
(stacking, tick order, numeric effects of BLEED/POISON/FIRE/…); `SKILL_*` proc mechanics
(PROC_CHANCE/cooldown/event hooks); ability-bag draw semantics; focus economy; `Tendency`/`Threat`
target selection (exploitable by item design); charges; summons. Overworld: skill-encounter stat
gates; traps; loot resolution (LootID/tags/tier bands → what drops where); market tag gates; chaos;
XP/gold flow. **Every claim tagged [verified: decompile] / [verified: data] / [inferred]. Synergy
designs may only build on verified claims.**

**`tools/out/vocab-index.json`** (gitignored; the extractor + a committed summary report are the
durable artifacts) — every live item id, tag, `SKILL_*`, `STATUS_*`, slot token, class, rarity,
material, tier band; plus measured stat distributions per (class, tier, rarity) so generated items
sit on the game's real power curve.

**`docs/research/eor-loader-notes.md`** — decompile findings from `EnhancedOverhaulRemix.dll`: how
`CustomItems\Things\*.json`, `VisualFallbacks.json`, `ItemIcons\*.png`, and `Localization\*.json`
are loaded (generic drop-in vs hardcoded); this decides the recommended manual install path and
resolves FTK2.Forge SPEC Open Question #6 as a side effect.

### 3.2 Item manifest format (the shared contract)

One JSON file per pack: `pack.json` — an ordered list of item entries. Each entry:

```jsonc
{
  "Id": "ARM_...",                  // uppercase-underscore, ARM_ prefix
  "Thing": { /* full ThingConfig exactly as data-schemas.md documents */ },
  "VisualFallback": "BLADE_...",    // required: base-game id for model/icon fallback
  "Loc": { "Name": "...", "Description": "..." },
  "Icon": {                         // optional; omit = fallback icon only
    "Base": "<sprite key in sprite index>",
    "Transforms": [ { "Op": "HSV_SHIFT", "...": "..." }, ... ]
  },
  "DesignNote": "...",              // hand-crafted items: which verified mechanic the synergy uses
  "Provenance": { "Source": "handcrafted|forge", "Profile": "...", "Seed": 0 }
}
```

Compilers emit the ship-shape files from manifests: `Things/*.json`, `VisualFallbacks.json`,
`Localization/en.json`, `icons/*.png`. Hand-crafted and forge items are the same kind of thing.

### 3.3 iconforge

- `extract_assets` step: download AssetRipper (pinned release, into `tools/bin/`, gitignored) and
  extract sprites from the game's bundles **read-only** into `tools/out/sprites/`, then build
  `sprite-index.json` (key → file, size, class guess from name).
- Transform vocabulary (deterministic Pillow ops): `HSV_SHIFT`, `PALETTE_MAP` (material remaps),
  `GLOW` (rarity auras), `OVERLAY` (rune/crack/flame stamps from a small authored overlay set),
  `TINT_SILHOUETTE`, `COMPOSITE` (second sprite at anchor), `OUTLINE`. Recipes render identically
  on every run.
- Outputs PNGs named per the EOR ItemIcons convention (verified in Stage 0) + contact-sheet HTML.

### 3.4 itemforge

Seeded PRNG, pure function of (profile, seed, vocab-index). Generation model:

- **Power budget**: stat-point budget per (tier, rarity) derived from measured base-game curves ×
  profile multiplier.
- **Archetypes**: weighted per-class templates skewing budget allocation (duelist/brute/warden/…).
- **Affix pools**: stat affixes, passive affixes (`SKILL_*` priced per-skill), status-proc tags,
  ability-bag grants — priced against the budget.
- **Tension knobs**: `chaos` (variance), `synergy_bias` (theme-clustered vs i.i.d. affix draws),
  `curse_chance` (upside+drawback items), `name_grammar` (themed naming).
- Ships ≥3 example profiles demonstrating knob ranges. Every run emits a stat-audit report
  (budget adherence, per-stat histograms vs base game).
- A curated selection of the best forge output (validated, hand-reviewed) ships in the Armory pack,
  clearly marked by provenance.

### 3.5 Hand-crafted catalog (~60 items)

8–10 themed sets (3–6 items each; complementary-mechanics synergy, not hidden set bonuses) +
10–15 standalone legendaries/uniques. All rarities COMMON→LEGENDARY, all slot families including
trinkets, overworld-stat design space included (TAL/AWR/LCK gating items). Each item: name, lore
description, deliberate stats/passives/bags, tier/market/loot tags, icon recipe, and a DesignNote
citing the verified mechanic it exploits. Authored as manifests, compiled and validated like any
forge pack.

### 3.6 validate_pack (the gate)

Checks: JSON shape per data-schemas.md; every referenced tag/skill/status/slot/class/rarity/
material/VisualFallback id exists in vocab-index; ARM_ prefix + id uniqueness (incl. vs live game);
power-budget sanity vs curves (warn threshold, hard-fail threshold); Loc completeness; icon recipe
references resolve; localization key collisions. Exit code gates CI-style use.

## 4. Data formats

§3.2 manifest is the primary new contract. Profiles (`tools/itemforge/profiles/*.json`) and icon
recipes (inline in manifests) are documented by JSON-schema files next to the tools plus ≥1
commented example each. Ship-shape outputs follow the game's own schemas verbatim.

## 5. Knobs

No BepInEx knobs (no plugin). Tool knobs = forge profile fields (§3.4), CLI flags (`--seed`,
`--count`, `--profile`, per-knob overrides), iconforge transform params, validator thresholds
(`--strict`). All defaults live in committed profile/config files, not code.

## 6. Patch targets & integration points

None at runtime (zero-code). Integration = file conventions verified in Stage 0: the game-native
`Things` JSON tree, EOR `CustomItems\Things`, `VisualFallbacks.json`, `ItemIcons`,
`Localization`. The install doc (`FTK2.Armory/INSTALL.md`) describes both paths (EOR drop-in if
verified generic, else StreamingAssets merge) as **manual steps with owner-made backups** — no
script in this repo writes to the game directory.

## 7. Example starting dataset

`FTK2.Armory/data/` ships: the ~60-item hand-crafted pack, one curated forge pack (~40–80 items),
`VisualFallbacks.json`, `Localization/en.json`, `icons/`. `tools/itemforge/profiles/` ships ≥3
profiles. `tools/` ships the sprite/vocab extractors and validator.

## 8. Testing plan

Autonomous (tonight): validator green on every shipped pack; forge determinism test (same seed
twice → byte-identical); icon renders for 100% of shipped items; vocab-index spot-checks against
raw game JSON; stat-audit reports within thresholds; mechanics-doc claims cross-checked decompile
vs data where both exist.
Human (morning, <15 min in-game): manual install per INSTALL.md; spawn 3–5 items via EOR debug
give-item; confirm tooltip/name/icon/stats render; confirm market/loot appearance of one COMMON
item; confirm a bag-based on-use item fires its ability. A "needs in-game verification" list ships
in the review page.

## 9. Save & multiplayer

Static config-shaped data only — the safest MP shape in this repo. Parity class `ALL_PEERS` by
file identity (same pack files on all peers, same as base-game data edits; EOR's config hash
handshake covers them when installed via EOR paths). No runtime randomness: forge randomness is
authoring-time. No per-instance state, no sync surface, no SafeMode semantics. Armory M2 (future,
plugin: set bonuses, ParityService registration) inherits repo MP rules when it happens.

## 10. Milestones (tonight's execution order)

1. **M0 ground truth**: decompiles (FTK2.dll, EOR dll) → `game-mechanics.md`,
   `eor-loader-notes.md`; `extract_vocab.py` → vocab-index + curve report.
2. **M1 iconforge**: AssetRipper extraction → sprite index → transform engine → contact sheet.
3. **M2 itemforge**: generator + profiles + audit reports + determinism test.
4. **M3 catalog**: ~60 hand-crafted manifests, icons, design notes.
5. **M4 ship**: validator green, compile packs to `FTK2.Armory/data/`, INSTALL.md, review page
   (local HTML, not published), REVIEW-GUIDE update, incremental commits throughout.

Each milestone is independently reviewable; commits land per-milestone at minimum.

## 11. Open questions (deferred to morning review)

1. EOR `CustomItems`/`ItemIcons` loader generality — resolved tonight by decompile if possible;
   if inconclusive, INSTALL.md documents the StreamingAssets path only.
2. Icon key convention (display-name-based per EOR observation vs id-based) — Stage 0 decompile
   decides; recipes are keyed abstractly so renaming output files is trivial either way.
3. Whether curated forge items should ship enabled by default or as a separate optional pack —
   shipping as **separate pack file** so the owner can adopt selectively.
4. Non-EOR-installed players: packs assume nothing from EOR at runtime, but the recommended
   install path may; the StreamingAssets fallback keeps packs EOR-independent.
