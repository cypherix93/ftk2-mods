# FTK2 Content Generation System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the ground-truth docs, icon remix pipeline, offline item forge, and ~60-item hand-crafted catalog defined in `docs/superpowers/specs/2026-07-16-content-generation-system-design.md`, shipping validated static packs in a new `FTK2.Armory` mod.

**Architecture:** One spine: a machine-readable vocabulary index extracted from the live game grounds a shared item-manifest format; iconforge (Pillow) and itemforge (seeded PRNG) both emit/consume manifests; `validate_pack.py` gates everything before compilation into ship-shape game JSON under `FTK2.Armory/data/`.

**Tech Stack:** Python 3.14 (Pillow 12.2 present; pytest + UnityPy installed in Task 1), ilspycmd (dotnet tool) for C# decompilation, git.

## Global Constraints

- **The game directory is READ-ONLY. No task writes, moves, or creates anything under `E:\Games\Steam\steamapps\common\For The King II`. No install script is shipped or run — INSTALL.md documents manual steps only.** (spec header + §6)
- Content id prefix: `ARM_`, uppercase-underscore (spec §3.2; CONVENTIONS.md naming).
- Item mechanics: data-only vocabulary — only stats/skills/statuses/tags/slots/abilities that exist in the live game files (spec §1); no new Abilities.json entries, no C#.
- All generation deterministic: seeded PRNG only (`random.Random(seed)`), no wall-clock in content, invariant formatting (spec §3.4, §9).
- Mechanics claims used by designs must be tagged `[verified: decompile]` or `[verified: data]` in `docs/research/game-mechanics.md`; `[inferred]` claims may not anchor a synergy (spec §3.1).
- Large extracted artifacts (decompiles, sprites, vocab-index.json, downloaded tools, venv) are gitignored; committed artifacts are source code, docs, manifests, profiles, shipped packs, icons, reports (spec §3.1, §7).
- Game paths (read-only): configs `E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\StreamingAssets\Assets\Configs\JSON~\`, assembly `...\For The King II_Data\Managed\FTK2.dll`, EOR plugin `...\BepInEx\plugins\EnhancedOverhaulRevamped\`.
- Python tests live in `tools/tests/`, run with `python -m pytest tools/tests -v` from repo root.
- Commit at every task's commit step at minimum; message style follows repo history (short imperative subject), each ends with the Claude co-author trailer.

---

### Task 1: Tooling bootstrap

**Files:**
- Create: `tools/README.md`, `tools/requirements.txt`, `tools/tests/__init__.py` (empty)
- Modify: `.gitignore` (create if absent)

**Interfaces:**
- Produces: a working `python -m pytest tools/tests -v` invocation; `ilspycmd` on PATH (or via `dotnet tool run`); UnityPy importable. Directory contract used by all later tasks: `tools/out/` (gitignored) for machine artifacts, `tools/bin/` (gitignored) for downloaded binaries.

- [ ] **Step 1: Write `.gitignore` additions and `tools/requirements.txt`**

Append to `.gitignore` (create with exactly this content if absent):

```gitignore
tools/out/
tools/bin/
tools/.venv/
__pycache__/
*.pyc
```

`tools/requirements.txt`:

```text
pillow>=12
pytest>=8
UnityPy>=1.20
```

- [ ] **Step 2: Install Python deps and ilspycmd**

Run: `python -m pip install --user -r tools/requirements.txt`
Expected: success (Pillow already satisfied). If pip refuses (externally-managed), create `python -m venv tools/.venv` and use `tools/.venv/Scripts/python.exe` everywhere `python` appears in this plan.

Run: `dotnet tool install --global ilspycmd`
Expected: "You can invoke the tool using the following command: ilspycmd". If already installed, `dotnet tool update --global ilspycmd`.

- [ ] **Step 3: Smoke-check the three tools**

Run: `python -c "import PIL, UnityPy; print(PIL.__version__, UnityPy.__version__)"` and `ilspycmd --version` and `python -m pytest tools/tests -v`
Expected: version strings print; pytest reports "no tests ran" (exit 5 is fine at this point).

- [ ] **Step 4: Write `tools/README.md`**

Content: one paragraph per tool directory (`extract_vocab.py`, `iconforge/`, `itemforge/`, `validate_pack.py`, `tests/`), the `tools/out/` + `tools/bin/` gitignore contract, and the read-only-game-dir rule stated verbatim.

- [ ] **Step 5: Commit**

```bash
git add .gitignore tools/README.md tools/requirements.txt tools/tests/__init__.py
git commit -m "Tooling bootstrap for content-generation pipelines"
```

---

### Task 2: Decompile FTK2.dll + EOR; write eor-loader-notes.md

**Files:**
- Create: `docs/research/eor-loader-notes.md`
- Create (gitignored): `tools/out/decompile/FTK2/`, `tools/out/decompile/EOR/`

**Interfaces:**
- Produces: full C# source trees for grep by Tasks 3–5; `eor-loader-notes.md` answering: (a) is `CustomItems\Things\*.json` a generic drop-in loader; (b) exact `ItemIcons` PNG key convention (display-name vs id); (c) how `VisualFallbacks.json` is applied (which method, what it keys); (d) EOR `Localization\*.json` merge behavior. Each answer tagged `[verified: decompile]` with class/method names cited.

- [ ] **Step 1: Decompile both assemblies (read-only inputs, gitignored outputs)**

```powershell
ilspycmd -p -o tools/out/decompile/FTK2 'E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed\FTK2.dll'
ilspycmd -p -o tools/out/decompile/EOR 'E:\Games\Steam\steamapps\common\For The King II\BepInEx\plugins\EnhancedOverhaulRevamped\EnhancedOverhaulRemix.dll'
```

Expected: project trees with .cs files. If EOR dll name differs, locate it: `Get-ChildItem '...\BepInEx\plugins\EnhancedOverhaulRevamped' -Filter *.dll` and decompile every dll found there.

- [ ] **Step 2: Trace the four loader questions in the EOR source**

Grep starting points: `CustomItems`, `VisualFallbacks`, `ItemIcons`, `Localization`, `GetImage`, `LoadImage`, `Sprite`, `Texture2D`. For each, follow to the Harmony patch or load call and record: trigger point (which game method patched / lifecycle event), input path pattern, key derivation (e.g. filename → display name via `Lang.__t`? → id?), merge target (`Configs.Things` etc.).

- [ ] **Step 3: Write `docs/research/eor-loader-notes.md`**

Sections: Findings (the four answers, each with code citation + tag), Consequences for Armory (recommended manual install path; icon file-naming convention iconforge must emit), Forge spec OQ#6 resolution note. If a question is genuinely unresolvable from the decompile, record the default the spec mandates: StreamingAssets-merge path documented in INSTALL.md, id-keyed icon filenames.

- [ ] **Step 4: Commit**

```bash
git add docs/research/eor-loader-notes.md
git commit -m "Research: EOR loader mechanics (CustomItems, ItemIcons, VisualFallbacks)"
```

---

### Task 3: game-mechanics.md — combat ground truth

**Files:**
- Create: `docs/research/game-mechanics.md` (combat sections)

**Interfaces:**
- Consumes: `tools/out/decompile/FTK2/` from Task 2.
- Produces: the verified-claims catalog Tasks 10 and 12 cite. Claim ids like `[C-STAT-ATK]`, `[C-PROC-1]` so item DesignNotes can reference them precisely.

- [ ] **Step 1: Trace combat formulas in decompiled FTK2 source**

Entry points (from `docs/research/game-code-reference.md`): `CharacterHelper.GetStat`, combat damage application (search `eCombatActions`, `CHANGE_STAT` handling, `IsBlockable`), skill roll (search `Rolls`, `SkillRoll`, `Focus`), status ticking (search `TickCombat`, `TickFrequency`), passives (`SkillConfigs`, `PROC_CHANCE`), targeting (`Tendency`, `Threat`), ability bags (`AbilityBag`, `Shuffle`), charges (`ChargeConfigs`).

- [ ] **Step 2: Write the combat half of `docs/research/game-mechanics.md`**

Required sections (from spec §3.1): the 20 stats' actual roles; skill-roll mechanics incl. FOC re-rolls and ACC; damage types vs DEF/RES/EVD; status semantics incl. stacking + numeric effects of at least BLEED/POISON/FIRE/REGEN/GUARD/TAUNT/DAZE/STUN; SKILL_* proc mechanics; ability-bag draw semantics; focus economy; Tendency/Threat target selection; charges; summons (ADD_CHARACTER). Every claim gets an id + `[verified: decompile]` / `[verified: data]` / `[inferred]` tag, with class.method citations for decompile claims.

- [ ] **Step 3: Cross-check 5 claims against game JSON**

For five claims that are checkable both ways (e.g. a status's `Stats{}`, a skill's `PROC_CHANCE`), verify the data agrees; note the cross-check inline.

- [ ] **Step 4: Commit**

```bash
git add docs/research/game-mechanics.md
git commit -m "Research: combat mechanics ground truth (verified against decompile)"
```

---

### Task 4: game-mechanics.md — overworld ground truth

**Files:**
- Modify: `docs/research/game-mechanics.md` (append overworld sections)

**Interfaces:**
- Consumes: decompile tree; `docs/research/data-schemas.md` (SkillEncounters/Traps/Wheels/QuestTemplates schemas).
- Produces: overworld claim ids (`[O-*]`) for Task 12's explorer-kit designs.

- [ ] **Step 1: Trace overworld systems**

Entry points: skill-encounter resolution (search `SkillEncounter`, `TestStat`, `SuccessThreshold`, `RollModifier`), traps (`RunTestStat`, `DisarmTestStat`), loot resolution (`LootDropHelper`, `LootID`, `RewardsTagQuery`, tier filtering), market stocking (search `TOWN_MARKET`, `DUNGEON_MARKET`, `NIGHT_MARKET` consumers), chaos, XP/gold award paths.

- [ ] **Step 2: Append overworld sections with the same claim-id + tag discipline**

Must answer concretely: which stats gate which overworld check types; how an item's stats influence overworld rolls (equipped-stat contribution [verify!]); how tags + MinTier/MaxTier + Rarity decide drop/market eligibility (this is what makes Armory items actually appear in-game — flag it as the #1 morning in-game check).

- [ ] **Step 3: Commit**

```bash
git add docs/research/game-mechanics.md
git commit -m "Research: overworld mechanics ground truth"
```

---

### Task 5: extract_vocab.py — vocabulary index + stat curves

**Files:**
- Create: `tools/extract_vocab.py`, `tools/tests/test_extract_vocab.py`
- Create: `docs/research/vocab-summary.md` (committed summary report)
- Create (gitignored): `tools/out/vocab-index.json`

**Interfaces:**
- Consumes: live game JSON tree (read-only).
- Produces: `tools/out/vocab-index.json` with EXACTLY these top-level keys, consumed by Tasks 8–12:

```python
{
  "ItemIds": [...],            # every id across Things\*.json
  "AllIds": [...],             # ItemIds + Characters + Abilities + Statuses + Skills ids
  "Tags": {tag: count},        # tag -> occurrence count across Things
  "Skills": [...],             # SKILL_* ids appearing in SkillConfigs.json
  "Statuses": [...],           # StatusEffects.json ids
  "Slots": [...],              # distinct Equippable.Slots tokens observed
  "Classes": [...], "Rarities": [...], "Materials": [...],
  "StatKeys": [...],           # distinct Equippable.Stats keys observed
  "TierBands": {id: {"MinTier": int, "MaxTier": int}},
  "StatCurves": {"<Class>|<Rarity>": {"count": n,
      "stats": {statKey: {"min": x, "max": x, "mean": x, "p50": x}},
      "value": {"min": x, "max": x, "mean": x, "p50": x}}},
  "VisualDonors": {Class: [ids sorted]},   # equippable ids per class, for VisualFallback picks
}
```

Public function: `build_index(configs_dir: Path) -> dict`. CLI: `python tools/extract_vocab.py [--configs <dir>] [--out tools/out/vocab-index.json]`.

- [ ] **Step 1: Write failing tests against a fixture mini-config tree**

`tools/tests/test_extract_vocab.py` builds a temp dir mimicking `JSON~\Things\Weapons.json` (2 items, one with BOM), `StatusEffects.json`, `SkillConfigs.json`, `Characters.json`, then:

```python
def test_build_index_shapes(tmp_path):
    write_fixtures(tmp_path)          # helper in the test file, explicit JSON literals
    idx = build_index(tmp_path)
    assert "BLADE_TEST_01" in idx["ItemIds"]
    assert idx["Tags"]["DROPPABLE"] == 2
    assert "SKILL_TESTPROC" in idx["Skills"]
    assert "BLADE|COMMON" in idx["StatCurves"]
    assert idx["StatCurves"]["BLADE|COMMON"]["stats"]["ATK"]["max"] >= 5
    assert idx["TierBands"]["BLADE_TEST_01"] == {"MinTier": 0, "MaxTier": 2}

def test_handles_utf8_bom(tmp_path): ...
def test_deterministic_output(tmp_path):
    assert json.dumps(build_index(tmp_path), sort_keys=True) == json.dumps(build_index(tmp_path), sort_keys=True)
```

- [ ] **Step 2: Run tests, verify they fail** — `python -m pytest tools/tests/test_extract_vocab.py -v` → FAIL (module missing).

- [ ] **Step 3: Implement `tools/extract_vocab.py`**

Read every `Things\*.json` (use `encoding="utf-8-sig"` everywhere), plus `StatusEffects.json`, `SkillConfigs.json`, `Abilities.json`, `Characters.json`. Aggregate per the interface dict. Percentile via `statistics.median`; sort every list; `json.dump(..., sort_keys=True, indent=2)`.

- [ ] **Step 4: Tests pass** — `python -m pytest tools/tests/test_extract_vocab.py -v` → PASS.

- [ ] **Step 5: Run against the live game; write summary**

Run: `python tools/extract_vocab.py`
Then hand-spot-check 5 facts against raw files (e.g. count of Weapons ids ≈ 1,020 per data-schemas.md). Write `docs/research/vocab-summary.md`: counts per category, the observed `Slots` token list (resolves Forge OQ#2 as a bonus), 3 example StatCurves rows, anomalies.

- [ ] **Step 6: Commit**

```bash
git add tools/extract_vocab.py tools/tests/test_extract_vocab.py docs/research/vocab-summary.md
git commit -m "Vocab extractor: machine-readable game vocabulary + stat curves"
```

---

### Task 6: Sprite extraction (UnityPy) + sprite index

**Files:**
- Create: `tools/extract_assets.py`, `tools/tests/test_sprite_index.py`
- Create (gitignored): `tools/out/sprites/*.png`, `tools/out/sprite-index.json`

**Interfaces:**
- Produces: `tools/out/sprite-index.json`: `{key: {"file": "sprites/<key>.png", "w": int, "h": int, "source": "<container>"}}` where `key` is the sprite/texture object name uppercased. Function `build_sprite_index(game_data_dir, out_dir) -> dict`. Icon recipes in Tasks 7/12 reference these keys.

- [ ] **Step 1: Recon the asset layout (read-only)**

List candidate files: `For The King II_Data\*.assets`, `sharedassets*.assets`, `resources.assets`, `StreamingAssets\**\*.bundle`/`*.unity3d`. Load one in UnityPy and print object types to find where item icon `Sprite`s live (item ids like `BLADE_*` or display names in object names are the tell).

- [ ] **Step 2: Write failing test for the indexer contract**

```python
def test_index_entry_shape(tmp_path):
    # fake: write two PNGs via PIL + call write_index(entries, tmp_path)
    idx = json.loads((tmp_path/"sprite-index.json").read_text())
    e = idx["BLADE_TEST_01"]
    assert set(e) == {"file", "w", "h", "source"} and e["w"] > 0
```

(The UnityPy walk itself is verified by running it — the test pins the index format only.)

- [ ] **Step 3: Implement + run extraction**

`extract_assets.py`: walk candidate asset files, export every `Sprite` (fallback `Texture2D`) whose image is between 32 and 1024 px square, dedupe by name (keep largest), write PNGs + index. Run it; report count. Expected: hundreds+ of sprites including recognizable item icons. If item icons turn out to live in Addressables bundles with opaque names, index them all anyway — recipe authors pick by visual survey (Task 7's contact sheet).

- [ ] **Step 4: Tests pass; commit**

```bash
git add tools/extract_assets.py tools/tests/test_sprite_index.py
git commit -m "Sprite extraction pipeline (UnityPy) + sprite index"
```

---

### Task 7: iconforge — transform engine, renderer, contact sheet

**Files:**
- Create: `tools/iconforge/__init__.py`, `tools/iconforge/transforms.py`, `tools/iconforge/render.py`, `tools/tests/test_transforms.py`
- Create (gitignored): `tools/out/icons_preview/`

**Interfaces:**
- Consumes: `sprite-index.json` (Task 6).
- Produces:
  - `apply_transforms(img: PIL.Image.Image, transforms: list[dict]) -> PIL.Image.Image` — pure, deterministic.
  - Transform op vocabulary (exact `Op` strings): `HSV_SHIFT {H:float,S:float,V:float}`, `PALETTE_MAP {From:"#rrggbb",To:"#rrggbb",Tolerance:int}`, `GLOW {Color:"#rrggbb",Radius:int,Strength:float}`, `OVERLAY {Key:sprite-or-overlay-key,Anchor:"C|N|S|E|W|NE|NW|SE|SW",Scale:float,Alpha:float}`, `TINT_SILHOUETTE {Color,Alpha}`, `COMPOSITE {Key,Anchor,Scale}`, `OUTLINE {Color,Width}`.
  - CLI: `python tools/iconforge/render.py --manifest <pack.json> --sprites tools/out --out <dir>` renders every entry with an `Icon` block and writes `contact-sheet.html` (grid of name+icon+base).
- Task 12 authors recipes in exactly this vocabulary.

- [ ] **Step 1: Failing tests** — for each op: known 8×8 input → assert exact pixel expectations (e.g. `HSV_SHIFT` H:0.5 turns pure red pixel cyan; `GLOW` leaves center pixel unchanged, adds alpha in a previously transparent corner; determinism: same input+recipe twice → identical bytes).

- [ ] **Step 2: Run tests** → FAIL (module missing).

- [ ] **Step 3: Implement `transforms.py` + `render.py`**

Pillow only; RGBA throughout; no randomness. `render.py` loads manifest entries, resolves `Icon.Base` via sprite-index, applies transforms, saves `<Id>.png` at source resolution, emits contact-sheet HTML (plain file, base64-embedded thumbs).

- [ ] **Step 4: Tests pass** → `python -m pytest tools/tests/test_transforms.py -v` PASS.

- [ ] **Step 5: Visual smoke run** — hand-write a 6-entry demo manifest in `tools/out/` using real extracted sprites (2 recolors, glow, overlay, composite, outline); render; verify contact sheet renders plausible on-style icons (view the PNGs).

- [ ] **Step 6: Commit**

```bash
git add tools/iconforge tools/tests/test_transforms.py
git commit -m "iconforge: deterministic sprite remix engine + contact sheets"
```

---

### Task 8: Manifest schema + validate_pack.py (the gate)

**Files:**
- Create: `tools/manifest_schema.md`, `tools/validate_pack.py`, `tools/tests/test_validate_pack.py`

**Interfaces:**
- Consumes: `vocab-index.json`.
- Produces: `validate_pack(pack: dict, vocab: dict, sprite_index: dict|None) -> list[Finding]` where `Finding = {"level": "ERROR"|"WARN", "id": str, "check": str, "msg": str}`. CLI `python tools/validate_pack.py <pack.json> [--strict]` exits 1 on any ERROR (WARN too under `--strict`). Checks (spec §3.6): ThingConfig shape per data-schemas.md; every Tag/Skill/Status/Slot/Class/Rarity/Material in vocab; `VisualFallback` id ∈ ItemIds; `ARM_` prefix + uniqueness incl. vs `AllIds`; budget sanity vs StatCurves (WARN > p50×`warn_mult`, ERROR > max×`fail_mult`; defaults 1.5 / 2.0 in a `validator-config.json` next to the script); Loc completeness; Icon.Base resolves when sprite index provided.

- [ ] **Step 1: Failing tests** — table-driven: a minimal valid entry passes; then one test per check with a single broken field asserting the exact `check` name in the finding (`unknown_tag`, `bad_prefix`, `dup_id`, `unknown_visual_fallback`, `budget_error`, `missing_loc`, `unknown_stat_key`, ...). Fixture vocab is a small literal dict, not the real index.

- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.

- [ ] **Step 5: Write `tools/manifest_schema.md`** — the §3.2 manifest contract verbatim with one fully-worked commented example entry (a real planned catalog item), transform vocabulary from Task 7, provenance rules.

- [ ] **Step 6: Commit**

```bash
git add tools/manifest_schema.md tools/validate_pack.py tools/tests/test_validate_pack.py tools/validator-config.json
git commit -m "Pack validator + manifest contract (the shipping gate)"
```

---

### Task 9: Pack compiler (manifest → ship-shape files)

**Files:**
- Create: `tools/compile_pack.py`, `tools/tests/test_compile_pack.py`

**Interfaces:**
- Consumes: validated `pack.json` manifests.
- Produces: `compile_pack(pack: dict, out_dir: Path) -> None` writing `Things/<PackName>.json` (id→ThingConfig dict), `VisualFallbacks.json` (merge-append if file exists), `Localization/en.json` (keys `<Id>` and `<Id>_DESCRIPTION`, EOR style, merge-append), and copying rendered icons per the Task 2 naming convention. CLI: `python tools/compile_pack.py <pack.json> --icons <rendered-dir> --out FTK2.Armory/data`. Refuses to run if `validate_pack` reports ERRORs (imports and calls it first).

- [ ] **Step 1: Failing tests** — compile a 2-item fixture manifest to tmp dir; assert Things dict keys, VisualFallbacks content, loc keys, merge-append behavior (pre-seed a VisualFallbacks.json, assert both old+new keys), and validator-refusal (broken manifest → SystemExit).

- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.

- [ ] **Step 5: Commit**

```bash
git add tools/compile_pack.py tools/tests/test_compile_pack.py
git commit -m "Pack compiler: manifests to ship-shape game JSON"
```

---

### Task 10: itemforge — generator core + profiles

**Files:**
- Create: `tools/itemforge/__init__.py`, `tools/itemforge/forge.py`, `tools/itemforge/model.py`, `tools/itemforge/naming.py`, `tools/itemforge/profiles/baseline.json`, `tools/itemforge/profiles/chaotic_epics.json`, `tools/itemforge/profiles/cursed_bargains.json`, `tools/tests/test_forge.py`

**Interfaces:**
- Consumes: `vocab-index.json`; mechanics claims (affix pool contents cite `game-mechanics.md` claim ids in comments).
- Produces: `forge_items(profile: dict, vocab: dict, seed: int, count: int) -> list[dict]` returning manifest entries (§3.2 shape, `Provenance.Source="forge"`). CLI: `python tools/itemforge/forge.py --profile <p> --seed N --count N --out <pack.json> [--audit <report.md>]`.
- Profile schema (all fields required; documented in each profile as `_comment` keys):

```jsonc
{
  "Name": "baseline",
  "TierRange": [0, 4], "Rarities": {"COMMON": 40, "UNCOMMON": 30, "RARE": 18, "EPIC": 9, "LEGENDARY": 3},
  "Classes": {"BLADE": 10, "AXE": 8, "...": 0},          // weighted; 0/absent = excluded
  "BudgetMultiplier": 1.0,                               // × measured p50 curve for (class,rarity)
  "Chaos": 0.15,                                         // relative stddev of budget jitter
  "SynergyBias": 0.6,                                    // P(next affix drawn from same theme cluster)
  "CurseChance": 0.1,                                    // P(item takes a negative stat + budget rebate)
  "PassiveBudgetShare": 0.35,                            // max budget fraction spendable on SKILL_* affixes
  "Archetypes": {"BLADE": {"duelist": {"W": 3, "Stats": {"ACC": 3, "CRT": 3, "SPD": 1}},
                            "brute":   {"W": 2, "Stats": {"ATK": 5, "VIT": 1}}}, "...": {}},
  "AffixThemes": {"venom": {"Tags": ["POISON"], "Skills": ["SKILL_..."], "Stats": {"CRT": 2}}, "...": {}},
  "NameGrammar": {"Patterns": ["{adj} {noun}", "{noun} of {theme}"], "Adj": ["Ashen", "..."], "...": []}
}
```

- [ ] **Step 1: Failing tests**

```python
def test_determinism(): assert forge_items(p, v, 42, 20) == forge_items(p, v, 42, 20)
def test_different_seeds_differ(): assert forge_items(p, v, 1, 20) != forge_items(p, v, 2, 20)
def test_budget_adherence():   # every item within Chaos-implied bounds of its (class,rarity) budget
def test_vocab_closure():      # every tag/skill/stat emitted exists in fixture vocab
def test_curse_rebate():       # cursed item's positive-stat total exceeds non-cursed same-budget item
def test_manifest_valid():     # forge output passes validate_pack with zero ERRORs
```

- [ ] **Step 2: Run** → FAIL. **Step 3: Implement `model.py` (budget/archetype/affix pricing), `forge.py` (orchestration + audit report), `naming.py` (grammar)** — single `random.Random(seed)` instance threaded through; affix prices: flat stat point = 1 budget unit scaled by that stat's p50 presence in curves; SKILL_* priced in `model.py` via a per-skill price table derived from vocab (skills appearing on higher-rarity items cost more) with profile override map. Audit report: per-stat histograms (text bars) vs base-game curve, budget adherence %, theme distribution.

- [ ] **Step 4: Run** → PASS (`python -m pytest tools/tests/test_forge.py -v`).

- [ ] **Step 5: Author the 3 profiles** with genuinely distinct knob settings; run each at `--seed 7 --count 60`; skim outputs for sanity; keep the three audit reports.

- [ ] **Step 6: Commit**

```bash
git add tools/itemforge tools/tests/test_forge.py
git commit -m "itemforge: seeded offline item generator with tunable profiles"
```

---

### Task 11: Forge tuning runs + curated forge pack

**Files:**
- Create: `FTK2.Armory/packs/forge_curated.pack.json` (manifest, committed), `docs/research/forge-tuning-notes.md`
- Create (gitignored): `tools/out/forge_runs/`

**Interfaces:**
- Consumes: Task 10 CLI. Produces: a curated manifest of 40–80 forge items (ids `ARM_FRG_*` — note: distinct from FTK2.Forge's `FRG_`; use `ARM_` + role infix), validator-green, provenance intact.

- [ ] **Step 1: Sweep runs** — 3 profiles × seeds {7, 42, 1337} × count 100 → `tools/out/forge_runs/`. Record per-run one-line verdicts in `forge-tuning-notes.md` (what each knob visibly did — this is the "test the randomness" deliverable).
- [ ] **Step 2: Curate** — select 40–80 items across runs (interesting, spread over tiers/classes/rarities); merge into `forge_curated.pack.json`; assign icon recipes (theme-driven defaults: rarity glow + theme palette map).
- [ ] **Step 3: Validate + render** — `validate_pack` zero ERRORs; iconforge renders all icons; contact sheet saved to `FTK2.Armory/packs/forge_curated-contact-sheet.html`.
- [ ] **Step 4: Commit**

```bash
git add FTK2.Armory/packs/forge_curated.pack.json FTK2.Armory/packs/forge_curated-contact-sheet.html docs/research/forge-tuning-notes.md
git commit -m "Curated forge pack + randomness tuning notes"
```

---

### Task 12: Hand-crafted catalog (~60 items)

**Files:**
- Create: `FTK2.Armory/packs/catalog.pack.json` (or one file per set: `catalog_<set>.pack.json` — executor's choice, record it), `FTK2.Armory/CATALOG.md` (design doc: sets, loops, item tables)

**Interfaces:**
- Consumes: `game-mechanics.md` claim ids (every DesignNote cites ≥1 verified claim), vocab-index, manifest schema, transform vocabulary.
- Produces: ~60 validator-green manifest entries with icon recipes.

- [ ] **Step 1: Write `CATALOG.md` set plan** — 8–10 sets + 10–15 standalones. Planned sets (adjust against verified mechanics; each set 3–6 items, COMMON→EPIC spread; standalones mostly LEGENDARY):
  1. **Bramblelord** — THRN/DEF/taunt retaliation tank (THRN stat + TAUNT/GUARD statuses)
  2. **Tidebinder** — focus-economy caster (FOC gen/discount + water/ice imbues)
  3. **Gravedigger's Due** — LCK/gold gambler (high-variance ability bags, MOSTGOLD-adjacent procs)
  4. **Red Season** — bleed/crit hunter (BLEED procs, CRT/ACC scaling)
  5. **Waywatcher** — overworld explorer (TAL/AWR/LCK for encounters/traps/loot)
  6. **Ashen Covenant** — cursed bargains (large upside + negative stat, CURSE flavor)
  7. **Hollow Court** — summoner regalia (ADD_CHARACTER bags)
  8. **Stormcaller** — elemental imbue kit (IMBUE_FIRE/ICE/SHOCK + SHOCK procs)
  9. **Vanguard's Oath** — SPD/EVD skirmisher (row/move manipulation, evasion tank)
  plus standalones (build-around legendaries, e.g. a Kamikaze-flavored blade, a DEATHMARK scythe, a REFLECT mirror shield, a PA/SA economy relic — all contingent on verified claims).
- [ ] **Step 2: Author manifests set-by-set** — for each item: Thing (stats/passives/bags per design), VisualFallback (nearest-silhouette donor from `VisualDonors`), Loc (name + lore description), Icon recipe, DesignNote citing claim ids. Validate after each set (`validate_pack`, fix immediately). Sets are independent — parallelize across subagents if executing with subagent-driven-development, one set per agent, each given: CATALOG.md's set brief, manifest_schema.md, the vocab summary, and the mechanics doc.
- [ ] **Step 3: Render all icons + per-set contact sheets** (`FTK2.Armory/packs/*.html`).
- [ ] **Step 4: Full-catalog review pass** — one pass over all ~60 for: duplicate niches, budget outliers vs curves (validator WARNs), name collisions with live game loc strings (grep extracted loc if available), tier coverage gaps.
- [ ] **Step 5: Commit** (per set or at end of authoring — executor's judgment, at least one commit per 3 sets)

```bash
git add FTK2.Armory/packs FTK2.Armory/CATALOG.md
git commit -m "Hand-crafted catalog: <sets> (~60 items, mechanics-grounded)"
```

---

### Task 13: Ship — compile packs, Armory SPEC, INSTALL, review page

**Files:**
- Create: `FTK2.Armory/SPEC.md`, `FTK2.Armory/INSTALL.md`, `FTK2.Armory/data/**` (compiled output), `FTK2.Armory/REVIEW.html`, modify `README.md` (mod table row), modify `REVIEW-GUIDE.md`

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Compile both packs** — `compile_pack.py` for catalog + curated forge pack into `FTK2.Armory/data/` (separate Things files; shared VisualFallbacks.json/Localization). Re-run `validate_pack --strict` on manifests; run full test suite `python -m pytest tools/tests -v` → all PASS.
- [ ] **Step 2: Write `FTK2.Armory/SPEC.md`** per CONVENTIONS.md template (§1–§11; §9 = static-data ALL_PEERS parity per design spec §9; M2 future = set-bonus plugin).
- [ ] **Step 3: Write `INSTALL.md`** — manual steps only, both paths as resolved by Task 2 (recommended path first), explicit "back up these files yourself first" list, uninstall steps, MP note (identical files on all peers).
- [ ] **Step 4: Build `REVIEW.html`** (local file, not published): all contact sheets, 60 item cards (name, lore, stats, DesignNote + claim links), forge audit charts, mechanics-doc highlights, "needs in-game verification" checklist (from spec §8 human tests), open questions status.
- [ ] **Step 5: Update `README.md` (Armory row) + `REVIEW-GUIDE.md` (morning review path: REVIEW.html → CATALOG.md → INSTALL.md).**
- [ ] **Step 6: Final commit + verification**

```bash
python -m pytest tools/tests -v   # expect: all pass
git add FTK2.Armory README.md REVIEW-GUIDE.md
git commit -m "FTK2.Armory: compiled packs, SPEC, install + review guides"
git log --oneline master          # expect: one+ commit per task
```

---

## Self-review notes (done at authoring time)

- Spec coverage: §3.1→Tasks 2–5; §3.3→6–7; §3.2/§3.6→8; compile→9; §3.4→10–11; §3.5→12; §7/§8/§6→13; §9→13 (SPEC §9). Morning human tests remain human (spec §8) — listed in REVIEW.html.
- Stage-0-dependent details (icon key naming, install path, exact affix skill lists) are pinned to Task 2/3 outputs with mandated defaults — no TBDs.
- Type consistency: `Finding`, manifest entry shape, vocab-index keys, transform `Op` strings, and CLI names are used identically across Tasks 5–13.
