# EOR-rehost engine build — full verification sweep

*Written 2026-07-25. Scope: fix the two known test-data issues blocking a clean sweep, then run
the full build/test/pack-validation/compile/determinism matrix for the EOR-rehost work
(ClassForge, Summoner, WarBrain, DevKit, Armory) and capture real evidence. No commits made —
every change described below is a working-tree diff only.*

## 0. Fixes applied first

### Fix 1 — ClassForge.Core.Tests hard-coded "exactly 1 enabled pack"

`FTK2.ClassForge/src/ClassForge.Core.Tests/Program.cs` scanned the real `data/ClassPacks` dir and
asserted `EnabledOrderedPacks.Count == 1` / `[0].Id == "CF_PACK_BALDURS"`. That broke the moment
`CF_PACK_EOR_CLASSES` (also `enabled: true`) landed in the repo, because the loader then found two
enabled packs, not one.

**Fix**: pass the `isPackEnabled` predicate `PackLoader.Load` already supports (the same mechanism
`ClassForge.PackCheck` uses — `id => string.Equals(id, expectedId, StringComparison.Ordinal)`), so
the test scans the real fixture directory (still true end-to-end coverage of the shipped BALDURS
pack) but treats only `CF_PACK_BALDURS` as enabled regardless of how many other packs are enabled
repo-wide. The assertion `EnabledOrderedPacks.Count == 1` is now true by construction, not by
accident of repo state.

```csharp
baldursResult = loader.Load(fs, new[] { classPacksDir },
    id => string.Equals(id, "CF_PACK_BALDURS", StringComparison.Ordinal));
```

### Fix 2 — CF_PACK_BALDURS trait ids renamed to the native `TRAIT_` prefix

`CF_PACK_BALDURS/traits.json` used `CF_TRAIT_*` ids. The native trait substrate
(`CharacterHelper.GiveTrait`/`InventoryHelper.GetTraits`) keys purely on `Thing.ConfigName`
starting with the literal `TRAIT_` prefix — a `CF_TRAIT_*` id can never be granted through it.
`ClassForge.Core`'s `PackContentParser` already flagged this with a `CF_TRAIT_PREFIX` warning per
non-conforming id; the ClassForge.Core.Tests suite asserted exactly 6 of those warnings, i.e. it
was asserting the bug.

**Convention chosen**: `TRAIT_CF_BALDURS_<original-suffix>` — keeps the native-required `TRAIT_`
prefix while keeping the pack-scoping `CF_BALDURS` segment to avoid id collisions with other packs'
traits. Renamed all 6 ids consistently across every place they appear inside the pack:

| Old id | New id |
|---|---|
| `CF_TRAIT_HEXBLADE_CURSE` | `TRAIT_CF_BALDURS_HEXBLADE_CURSE` |
| `CF_TRAIT_PACT_BOON` | `TRAIT_CF_BALDURS_PACT_BOON` |
| `CF_TRAIT_COMBAT_SUPERIORITY` | `TRAIT_CF_BALDURS_COMBAT_SUPERIORITY` |
| `CF_TRAIT_TACTICAL_MIND` | `TRAIT_CF_BALDURS_TACTICAL_MIND` |
| `CF_TRAIT_UNDEAD_LEGION` | `TRAIT_CF_BALDURS_UNDEAD_LEGION` |
| `CF_TRAIT_DEATHS_DESIGN` | `TRAIT_CF_BALDURS_DEATHS_DESIGN` |

Files touched: `traits.json` (6 keys), `localization/en.json` (12 keys — name + `_DESCRIPTION` per
trait), `icons/*.png` (6 files renamed via `git mv` to match, per SPEC.md's "filename = content id"
icon convention). `classes.json` and `skillrecipes.json` were checked and confirmed to have **no**
references to the old ids (classes reference items, not traits directly; skillrecipes is keyed by
`SKILL_CF_*` ids and referenced *from* `traits.json`'s `Passives`, not the other way around) — no
changes needed there. `FTK2.ClassForge/SPEC.md` also references the old ids in prose/examples but
is outside this unit's owned paths (`data/ClassPacks/CF_PACK_BALDURS/**` only) — left untouched,
flagged as a documentation-drift follow-up.

`ClassForge.Core.Tests`'s `CF_TRAIT_PREFIX` assertion was flipped from "expect 6 warnings" to
"expect 0 warnings", and confirmed against both the unit test run and a live `ClassForge.PackCheck`
run (§3 below) — the loader's `CF_TRAIT_PREFIX` warning for BALDURS is gone in both.

Both fixes verified: `ClassForge.Core.Tests` → **14 passed, 0 failed**.

## 1. Build matrix

All from `D:\src\mods\ftk2-mods`, `-c Release`. Plugins needing game/BepInEx refs used
`-p:ManagedDir="D:\src\mods\ftk2-mods\tools\bin\refs" -p:BepInExDir="D:\src\mods\ftk2-mods\tools\bin\refs"`
per `docs/research/build-template-notes.md` §4 (Summoner.Plugin's `.csproj` already defaults
`FtkRefsDir` to that same snapshot, so it built with a plain `dotnet build`).

| Component | Command | Result | Tail |
|---|---|---|---|
| DevKit.Core | `dotnet build FTK2.DevKit/src/DevKit.Core -c Release` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| DevKit.Plugin | `dotnet build FTK2.DevKit/src/DevKit.Plugin -c Release -p:ManagedDir=... -p:BepInExDir=...` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| ClassForge.Core | `dotnet build FTK2.ClassForge/src/ClassForge.Core -c Release` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| ClassForge.Recipes | `dotnet build FTK2.ClassForge/src/ClassForge.Recipes -c Release` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| ClassForge.Plugin | `dotnet build FTK2.ClassForge/src/ClassForge.Plugin -c Release -p:ManagedDir=... -p:BepInExDir=...` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| ClassForge.PackCheck | `dotnet build FTK2.ClassForge/src/ClassForge.PackCheck -c Release` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| Summoner.Core | `dotnet build FTK2.Summoner/src/Summoner.Core -c Release` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| Summoner.Plugin | `dotnet build FTK2.Summoner/src/Summoner.Plugin -c Release` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| WarBrain.Core | `dotnet build FTK2.WarBrain/src/WarBrain.Core -c Release` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| WarBrain.Plugin | `dotnet build FTK2.WarBrain/src/WarBrain.Plugin -c Release -p:ManagedDir=... -p:BepInExDir=...` | **PASS** | `Build succeeded. 0 Warning(s) 0 Error(s)` |

All 10/10 builds: **0 Errors, 0 Warnings**. Note: `docs/research/build-template-notes.md` §4
recorded a prior `ClassForge.Plugin` build failure (`CS0012`, missing `System.Memory` reference for
`ReadOnlySpan<char>` in `ConfigMergePatches.cs`) — that source gap has since been fixed
(a `<Reference Include="System.Memory">` now resolves cleanly); ClassForge.Plugin builds clean in
this sweep.

## 2. Test suites

| Suite | Command | Result | Tail |
|---|---|---|---|
| DevKit.Core.Tests | `dotnet run --project FTK2.DevKit/src/DevKit.Core.Tests -c Release` | **69/69** | `Tests run: 69   passed: 69   failed: 0` / `ALL TESTS PASSED` |
| ClassForge.Core.Tests | `dotnet run --project FTK2.ClassForge/src/ClassForge.Core.Tests -c Release` | **14/14** | `14 passed, 0 failed.` |
| ClassForge.Recipes.Tests | `dotnet run --project FTK2.ClassForge/src/ClassForge.Recipes.Tests -c Release` | **149/149** | `tests: 149  passed: 149  failed: 0` |
| Summoner.Core.Tests | `dotnet run --project FTK2.Summoner/src/Summoner.Core.Tests -c Release` | **14/14** | `14 tests, 14 passed, 0 failed. (design §C.2 plans 14 for Summoner.Core.Tests.)` |
| WarBrain sim | `dotnet run --project FTK2.WarBrain/sandbox/WarBrainSim -c Release -- 50` (run from `FTK2.WarBrain/sandbox/WarBrainSim` to dodge the CWD-output gotcha) | **ran clean** | `Loaded 4 profiles, 3 doctrines from D:\src\mods\ftk2-mods\FTK2.WarBrain\data` / `Wrote results.md` |

Full ClassForge.Core.Tests run (post-fix), for the record:

```
PASS: Discovery determinism: shuffled directory order yields identical sorted result
PASS: Ordering: dependencies force order regardless of discovery order
PASS: Ordering: loadOrder ties break alphabetically by id
PASS: Ordering: missing dependency skips the dependent pack and logs a Finding
PASS: Ordering: dependency cycle skips every pack in the cycle and logs Findings
Fixture root: D:\src\mods\ftk2-mods\FTK2.ClassForge\data\ClassPacks\CF_PACK_BALDURS
PASS: CF_PACK_BALDURS: loads cleanly with zero Error findings
  classes=4 traits=6 abilities=13 items=4 localization=56
  BALDURS merge plan: classes=4 things=10 abilities=13 traitIds=6 loc=56 icons=6 portraits=3
PASS: CF_PACK_BALDURS: merge plan counts match independently re-parsed raw JSON
PASS: CF_PACK_BALDURS: TRAIT_-prefixed trait ids produce zero CF_TRAIT_PREFIX warnings
PASS: CF_PACK_BALDURS: dataHash is a stable 64-char hex SHA-256
PASS: Merge: id collision across packs resolves last-pack-wins and logs both pack ids
PASS: DataHasher: CRLF vs LF line endings produce the same hash
PASS: DataHasher: localization/** is excluded from the hash
PASS: DataHasher: a real (non-localization) content change does change the hash
PASS: ParityRegistrationBuilder: payload shape matches (guid, version, dataHash, enabledFeatures)

14 passed, 0 failed.
```

**Note on `results.md`**: the WarBrain sim writes `results.md` relative to the process CWD (a
documented gotcha, `build-template-notes.md` §5). Running from
`FTK2.WarBrain/sandbox/WarBrainSim` overwrote the *committed* `results.md` (a 200-battles/cell run)
with this session's 50-battles/cell run. Since that file isn't part of this unit's scope, it was
restored via `git checkout -- FTK2.WarBrain/sandbox/WarBrainSim/results.md` after capturing the
console tail above — no working-tree diff left behind from the sim run.

## 3. PackCheck — CF_PACK_EOR_CLASSES + CF_PACK_BALDURS

Command: `dotnet run --project FTK2.ClassForge/src/ClassForge.PackCheck -c Release` (no args —
the tool's built-in default resolves both packs under the repo's `data/ClassPacks` layout).

| Pack | Result | Tail |
|---|---|---|
| CF_PACK_EOR_CLASSES | **exit 0** | `Classes: 31  Traits: 20  Things: 20  Abilities: 0  Localization: 102  Icons: 51  Portraits: 31` / `Recipes: 48` (all 48 findings are `W_UNKNOWN_FIELD` on `_source` — a harmless authoring/provenance annotation field) / `RESULT: CF_PACK_EOR_CLASSES -- OK (zero Errors)` |
| CF_PACK_BALDURS | **exit 0** | `Classes: 4  Traits: 6  Things: 10  Abilities: 13  Localization: 56  Icons: 6  Portraits: 3` / `Recipes: 5` (6 findings: 4× `W_SCHEMA_DEFAULT`, 1× `W_COND_DEPRECATED`; **zero `CF_TRAIT_PREFIX` warnings — confirms the rename fixed it**) / `RESULT: CF_PACK_BALDURS -- OK (zero Errors)` |

Combined: `PackCheck: ALL PACKS OK (zero Errors)`. Confirms fix 2 end-to-end, not just via the unit
test — the loader's `CF_TRAIT_` warning is gone from a real PackCheck run against BALDURS.

## 4. Python test suite

Command: `python -m pytest tools/tests -v`

Result: **92 passed** in 2.02s (0 failed). Covers `test_eor_import.py`, `test_extract_vocab.py`,
`test_forge.py`, `test_sprite_index.py`, `test_transforms.py`, `test_validate_pack.py`.

```
============================= 92 passed in 2.02s ==============================
```

## 5. Pack validation vs the CLEAN vocab

Vocab: `tools/out/vocab-index.json` (present, regenerated post-install — 574,474 bytes, dated
2026-07-25). Command per `tools/validate_pack.py --help`:
`python tools/validate_pack.py <pack> --vocab tools/out/vocab-index.json`

| Pack | ERRORs | WARNs | Result |
|---|---|---|---|
| `FTK2.Armory/packs/eor_items.pack.json` | **0** | 54 | `eor_items.pack.json: 386 items, 0 errors, 54 warnings` (exit 0) — all 54 are `budget_warn` (item stat above the observed p50/max range for its slot+rarity bucket; informational power-budget flags, not blocking) |
| `FTK2.Armory/packs/eor_starters.pack.json` | **0** | 0 | `eor_starters.pack.json: 31 items, 0 errors, 0 warnings` (exit 0) |
| **Control**: `FTK2.Armory/packs/forge_curated.pack.json` (pre-existing shipped pack) | **0** | 11 | `forge_curated.pack.json: 60 items, 0 errors, 11 warnings` (exit 0) — same `budget_warn` shape as before, unchanged-green |

Both ERRORs targets met (0/0). Control pack still validates clean, confirming the vocab regen
didn't regress an already-shipped pack.

## 6. Armory compile

Commands (per `tools/compile_pack.py --help`; default `--out` is already `FTK2.Armory/data`,
matching every existing compiled pack's location; `--icons` omitted — both packs' items have zero
`Icon` fields, confirmed via `python -c "..."` inspection, so the icon-copy step correctly no-ops
at 0 copied rather than being skipped by omission of real work):

```
python tools/compile_pack.py FTK2.Armory/packs/eor_items.pack.json
python tools/compile_pack.py FTK2.Armory/packs/eor_starters.pack.json
```

Output:
```
compiled ARM_EOR_ITEMS: 386 items -> D:\src\mods\ftk2-mods\FTK2.Armory\data (0 icons)
compiled ARM_EOR_STARTERS: 31 items -> D:\src\mods\ftk2-mods\FTK2.Armory\data (0 icons)
```

Resulting paths (same layout as the 5 pre-existing compiled packs — `Things/<PackId>.json` new
files, `VisualFallbacks.json` / `Localization/en.json` merge-appended in place, JSON shape/sort/
indent verified byte-for-byte consistent with `ARM_FORGE_CURATED.json` etc.):

- `FTK2.Armory/data/Things/ARM_EOR_ITEMS.json` (new)
- `FTK2.Armory/data/Things/ARM_EOR_STARTERS.json` (new)
- `FTK2.Armory/data/VisualFallbacks.json` (modified — merge-appended)
- `FTK2.Armory/data/Localization/en.json` (modified — merge-appended)
- `FTK2.Armory/data/icons/` — untouched (0 icons to copy, both packs have no `Icon` fields)

## 7. Determinism spot-check — `tools/eor_import.py` re-run

Command (identical args to the original run):
```
python tools/eor_import.py --source "D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN\BepInEx\plugins" --repo-root . --report-dir tools/out/eor-import --package-version 0.7.0.60
```

Output:
```
eor_import: 1396 findings (0 blocking, 100 warn, 1296 info)
  ARM_EOR_ITEMS: 386 items
  ARM_EOR_STARTERS: 31 items
  SMN_PACK_EOR_MERCS: 100 followers
  SMN_PACK_EOR_PETS: 100 followers
  dropped: 9, vanilla_overrides: 40
  CF_PACK_EOR_CLASSES: 31 classes
reports written to D:\src\mods\ftk2-mods\tools\out\eor-import
```

**Result: PARTIAL — anomaly found and reverted, not clean.** `git status` after the re-run showed:

- `FTK2.Armory/packs/eor_items.pack.json`, `FTK2.Armory/packs/eor_starters.pack.json`,
  `FTK2.Summoner/**` (SMN_PACK_EOR_MERCS/PETS): **zero diff** — deterministic, as expected.
- `FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/{classes.json, localization/en.json,
  pack.json, provenance.json}`: **diffed**. Root cause: a later hand-authoring pass (see
  `provenance.json`'s `_authored_content` block, and commit `ba6ccbd`/prior ClassForge M1 work)
  added 40 `SKILL_CF_*` signature-skill passives to `classes.json`, 20 `TRAIT_*` trait localization
  entries to `en.json`, a richer `description`/bumped `version` (`1.0.0` → `1.1.0`) to `pack.json`,
  and an `_authored_content` provenance block — none of which the base `eor_import.py` converter
  knows how to regenerate, because that content doesn't come from the EOR source package at all.
  A bare re-run silently reverts all of it back to the raw-import baseline.

This is a real gap between "the importer is deterministic" (true — confirmed for every file it
alone owns) and "the repo state is reproducible from the importer alone" (false for
`CF_PACK_EOR_CLASSES`, which has manually-curated content layered on top). **Reverted** via
`git checkout -- FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/{classes.json,localization/en.json,pack.json,provenance.json}`
immediately after capturing this evidence, so no working-tree damage was left behind. Flagged as
an anomaly below — recommend the coverage-matrix hand-authoring step become part of the pipeline
(or get a documented "re-apply after re-import" companion script) so this doesn't silently regress
if `eor_import.py` is ever re-run for a real reason (e.g. a new EOR package version).

## 8. Known parked items

- **In-game smoke test pending operator.** `FTK2.ClassForge/SPEC.md` §8 step 8 (and
  `SPEC-DELTA-v1.1.md`'s OQ#10 note) call for a two-peer MP smoke test — both peers install
  `ftk2mods.classforge` + a class pack at matching versions, fight one combat together, verify
  identical HP/status/summon state and BepInEx logs. Nothing in this sweep launches the actual
  game (no BepInEx/Unity runtime involved in any build or test here — everything above is
  build/unit-test/CLI-tool verification only), so this remains pending a human operator with the
  game installed and running.
- **`SKILL_CF_PRIEST_BENEDICTION` ON_HEAL roll-tier gap.** The coverage matrix's original sketch
  (`docs/research/eor-rehost-coverage-matrix.md` §1 row 20) gates PRIEST's `_01`-tier status pair
  on `ROLL_TIER(EQ PERFECT)` under `ON_HEAL`. The shipped `ClassForge.Recipes` engine's `HealEvent`
  (`Runtime/TriggerEvents.cs`) carries no roll-tier field for `ON_HEAL` — only
  Healer/Healed/AbilityId/Amount — so a `ROLL_TIER` condition there would always read the
  `RollTier.SUCCESS` default and could never distinguish PERFECT, silently downgrading to dead
  code. The shipped recipe instead honestly always applies the `_00` tier; the tier split is
  recorded as unported in `provenance.json`'s `expressiveness_gaps`, pending an engine change that
  threads `pRollData` through `ON_HEAL`.
- **Parked primitives** (from `eor-rehost-coverage-matrix.md`, carried through to
  `CF_PACK_EOR_CLASSES/provenance.json`'s `balance_flags_carried_forward`): `GOLD_GRANT` /
  `ITEM_TAG_GRANT` (post-combat loot hooks — `TRAIT_TREASURE_SENSE`, `TRAIT_SCHOLARS_HABIT`,
  `TRAIT_SCAVENGER`, `OF_SCAVENGING` lose their loot halves), `SUPPRESS_CONSUME`
  (`TRAIT_ARCANE_MEMORY`, `OF_SPELLKEEPING` — needs an out-of-combat shared deterministic RNG),
  `CONDITIONAL_STAT_MODIFIER` (`TRAIT_PACK_TACTICS`, `TRAIT_ARCANE_FOCUS` — became unconditional
  half-strength flat stats), `DAMAGE_TAKEN_MULT` (`TRAIT_SHIELDBEARER` — became a flat `DEF +1`,
  flagged ~2× stronger than EOR's expected average), `CROSS_ENTITY_COORDINATION` (BEASTMASTER's
  pet-side quarry link — became a real dispellable status instead of a private GUID read).
- **BEASTMASTER's pet-side recipe is authored but unreachable.** `SKILL_CF_BEASTMASTER_PACK_TACTICS`
  is valid and PackCheck-clean but isn't attached to any `Passives[]` in this pack — pets/mercs are
  Summoner's domain, not ClassForge's, per the coverage matrix's note.

## 9. Anomalies

1. `docs/research/build-template-notes.md` §4 recorded `ClassForge.Plugin` failing to build
   (`CS0012`, missing `System.Memory`). That gap is closed — it built clean in this sweep (§1).
   Worth a follow-up note in that doc since it's now stale on that point.
2. The `tools/eor_import.py` determinism re-run (§7) is **not** a clean zero-diff for
   `CF_PACK_EOR_CLASSES` — see §7 for the full root-cause and the revert performed to leave the
   working tree clean.
3. `FTK2.WarBrain/sandbox/WarBrainSim/results.md` gets overwritten by any sim run from its own
   directory (by design — that's where it's supposed to write); this session's 50-battle run was
   reverted after capturing the console tail so the committed 200-battle results file is intact.

## Acceptance summary

- Builds: **10/10 pass, 0 errors each.**
- Test suites: DevKit.Core.Tests **69/69**, ClassForge.Core.Tests **14/14** (post-fix),
  ClassForge.Recipes.Tests **149/149**, Summoner.Core.Tests **14/14**, WarBrain sim **ran clean**.
- PackCheck: CF_PACK_EOR_CLASSES **exit 0**, CF_PACK_BALDURS **exit 0** (zero `CF_TRAIT_PREFIX`
  warnings post-rename).
- Python: **92/92 passed.**
- Pack validation: `eor_items` **0 errors / 54 warns**, `eor_starters` **0 errors / 0 warns**,
  control `forge_curated` **0 errors / 11 warns, unchanged-green.**
- Compile: `FTK2.Armory/data/Things/ARM_EOR_ITEMS.json` + `ARM_EOR_STARTERS.json` produced,
  `VisualFallbacks.json`/`Localization/en.json` merge-appended, layout matches the 5 pre-existing
  compiled packs exactly.
- Determinism re-run: **not clean** — `CF_PACK_EOR_CLASSES` drifted due to post-import hand
  authoring the base importer can't reproduce; reverted, documented as an anomaly (§7, §9).
