# S6 LiveDataHarness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An offline console that loads the *real* game's `Configs` out-of-process — no Unity, no BepInEx, no running game — and validates every authored class, trait, skill recipe, blessing, status, and encounter modifier in this repo against it. One command, ~2 seconds, catching id drift, dangling references, missing localization, fail-closed data gates, undeployed packs, and non-determinism that today cost a 35-step manual in-game checklist.

**Architecture:** A single `net10.0` console under `FTK2.DevKit/sandbox/LiveDataHarness/`, mirroring the working `FTK2.DevKit/sandbox/TypeProbe/` shape from S0. It `ProjectReference`s the host-agnostic `.Core` assemblies (`ClassForge.Core`, `ClassForge.Recipes`, `Summoner.Core`, `Blessings.Core`, `DevKit.Core` — all `netstandard2.0`) and reaches the game **entirely by reflection**: `Assembly.LoadFrom` on the install's `Managed/FTK2.dll` plus an `AssemblyResolve` fallback, then `ConfigsHelper.LoadConfigs(basePath)` invoked reflectively. There is **no compile-time game reference**, so the project builds on a machine with no game installed and degrades to a clear skip at runtime. Checks are registered cases on a console `CheckRunner`; the run prints a report, writes a deterministic JSON report, and exits non-zero on any error-severity finding.

**Tech Stack:** C# · `net10.0` · `System.Reflection` · `System.Text.Json` (BCL only, no NuGet) · repo console-runner pattern (`FTK2.ClassForge/src/ClassForge.Recipes.Tests/Harness.cs`, `FTK2.DevKit/sandbox/TypeProbe.Tests/TestHarness.cs`) · PowerShell wrapper.

**Spec:** `docs/superpowers/specs/2026-08-23-crucible-autopilot-design.md` (§2 grounding rule, §8 S6, §10 testing standard, §11 build order). **Inventory:** `docs/research/class-test-matrix.md`. **Superseded predecessor:** `docs/superpowers/plans/2026-08-08-live-data-harness.md` — never implemented, zero code exists; its verified ground truth is carried forward below, its AC1/AC2 are replaced.

---

## Acceptance criteria

Checkable conditions, not judgment calls. An executing session evaluates itself against this list before claiming completion and states plainly any that are unmet.

- [ ] **AC1 — Green on *Ben's* install, not on a fantasy pristine one.** `pwsh -File tools/run-harness.ps1` exits `0` against the live install at `C:\Program Files (x86)\Steam\steamapps\common\For The King II`, which is **deliberately contaminated**: `Configs.Characters` reads **2126** (2095 vanilla + 31 `EOR_*` written to disk by *Enhanced Overhaul Revamped* `0.7.0.62`) and `Configs.Things` reads **2380** (1847 vanilla + 533 `ARM_*` written to disk by this repo's own FTK2.Armory deploy). The harness asserts the **expected pack set is present** and that **every authored id resolves**; it never demands vanilla purity. A Steam *Verify integrity of game files* would delete content the co-op saves depend on and is **never** an instruction this harness gives.
- [ ] **AC2 — The provenance gate fires on the things that actually go wrong.** Three independently-proven behaviours, each with a pure-function negative control (Task 3), because the real install must not be mutated to demonstrate them:
  1. **Expected content missing** → Error. Feed a synthetic snapshot with zero `EOR_*` Characters; the check must name the missing pack. (This is the Steam-verify-wiped-the-co-op-content failure.)
  2. **Unaccounted-for content present** → Error. Feed a synthetic snapshot carrying an id from neither vanilla nor a recorded pack; the check must name the dictionary and the surplus. Detection is by arithmetic (`live count == vanilla baseline + recorded pack contributions`), so it catches an unknown third mod without needing to know its prefix.
  3. **A repo pack baked onto disk** → Error. Feed a synthetic snapshot containing `CF_EOR_BARD` in `Characters`; the check must name it. Repo packs are applied at runtime and must never appear in `StreamingAssets` — if one does, "adds-only against live ids" is silently measuring itself.
  Legitimate pack-version drift (`EOR_*` count moving off 31, `ARM_*` off 533) is a **Warning**, never a failure — a harness that goes permanently red when a dependency updates is worthless.
- [ ] **AC3 — No check is vacuous.** Every check family has a negative control or a deliberately-broken fixture, and inverting it makes that check FAIL. Task 4 Step 6 and Task 5 Step 5 run the fixture inversions explicitly; the rest are asserted by in-suite negative-control cases.
- [ ] **AC4 — Skip, not fail, without a game.** `pwsh -File tools/run-harness.ps1 -GameDir "Z:\nope"` exits `2` and prints a skip message. `dotnet build` succeeds on a machine with no game installed (no compile-time game reference).
- [ ] **AC5 — Deterministic.** Two consecutive processes produce byte-identical JSON reports. The report carries no timings, no absolute repo paths, and no enumeration-order-dependent lists.
- [ ] **AC6 — No placeholders shipped.** Zero occurrences of `NotImplementedException`, `TODO`, or `TBD` under `FTK2.DevKit/sandbox/LiveDataHarness/`.
- [ ] **AC7 — Repo build rules honored.** No NuGet `PackageReference` in the harness csproj; no compile-time reference to `FTK2.dll`, `UnityEngine*.dll`, or `BepInEx.dll`; every other project in the repo still builds.
- [ ] **AC8 — The game folder is untouched.** Every `File.`/`Directory.` call in the project is a read or an `Exists` probe, verified by inspection; the only write is the JSON report under `tools/out/`. The harness is safe to run while the game is open.
- [ ] **AC9 — Findings surfaced, not silenced.** Any check that fires against real pack content is reported with its evidence. **One first-run failure is already known and expected** (Task 10 Step 4: `SKILL_CF_ENCMOD_CURSE_ON_HIT` applies status `"CURSE"`, which is an `eStatusEffectTypes` *type* name, not a `Configs.StatusEffects` id — the nearest real id is `STATUS_CURSE_00`). It is reported to the owner for a decision; it is **not** allowlisted away. A check weakened to make it green is a failed acceptance, not a passed one.
- [ ] **AC10 — Documented.** `FTK2.DevKit/sandbox/LiveDataHarness/README.md` states the one command, the exit codes, the check inventory, the recorded install baseline, and explicitly what the harness cannot catch.

---

## Global Constraints

- **No NuGet packages.** BCL + `ProjectReference` only. Repo build rule stated at `FTK2.DevKit/src/DevKit.Plugin/DevKit.Plugin.csproj:31`; `ClassForge.PackCheck.csproj` and every `*.Core.Tests` project comply.
- **No compile-time reference to `FTK2.dll`, `UnityEngine*.dll`, or `BepInEx.dll`.** All game access is reflective. This is what keeps the harness buildable and reviewable with no game installed, and immune to game-update binary drift.
- **No Harmony, no Unity shim.** Verified 2026-08-08 and unchanged: `ConfigsHelper.LoadConfigs` completes in ~1.6–1.9 s in a plain net10 process with zero patches applied. If a future path needs Harmony, that is a separate plan.
- **`net10.0`, `LangVersion 7.3`, `ImplicitUsings disable`, `Nullable disable`** — matching `ClassForge.PackCheck.csproj` and `TypeProbe.csproj` exactly. `LangVersion 7.3` means: no `switch` expressions, no target-typed `new`, no `using` declarations, no nullable annotations, no default interface members.
- **The game directory is read-only.** Read `For The King II_Data\Managed` and `For The King II_Data\StreamingAssets\Assets`; write nothing, ever. The harness also never requires the mods to be deployed — pack content is read from the repo's `data/` folders.
- **The baseline is the *contaminated* install, and that is correct.** Third-party and first-party content is written directly into `StreamingAssets\Assets\Configs\JSON~` and is load-bearing for the owner's co-op saves. Task 3 records the expected content in a checked-in table and asserts against it. **Never instruct a Steam integrity verify.**
- **Every check has a negative control or a deliberately-broken fixture.** The bar set by `FTK2.DevKit/sandbox/DeterminismHarness.Tests` (6 negative controls) and S0's TypeProbe (6). A lint that cannot fail reports green forever. Fixtures live in `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/`.
- **Exit codes:** `0` = all checks passed · `1` = at least one Error finding · `2` = game install not found or `Configs` unloadable (skipped, not failed).
- **`Warning` findings never fail the run** — same posture as `ClassForge.PackCheck`, which tolerates `CF_PACK_BALDURS`'s known trait-prefix warning.
- **`ok` must never mean "dispatched" (spec §3).** A check that cannot evaluate its subject reports FAIL, never PASS. Zero offenders out of zero inspected subjects is a vacuous pass, so every collection-scanning check also asserts a non-zero subject count.
- **Namespace:** `LiveDataHarness` (root namespace matches assembly name, per `docs/research/build-template-notes.md` §7).

---

## Verified ground truth

Everything here was **measured on 2026-08-23** against the live install and the repo's `.Core` sources, unless marked as carried forward from the 2026-08-08 measurement (which was re-confirmed this session where noted). Do not re-derive it.

### Game install

| Fact | Value |
|---|---|
| Install root | `C:\Program Files (x86)\Steam\steamapps\common\For The King II` (`EOR_PACKAGE.json`, `For The King II.exe`, `BepInEx/` all present) |
| Managed dir | `<root>\For The King II_Data\Managed` |
| StreamingAssets base | `<root>\For The King II_Data\StreamingAssets\Assets` — this is `LoadConfigs`'s `basePath` |
| Configs JSON dir | `<basePath>\Configs\JSON~` |
| Entry point | `public static Configs ConfigsHelper.LoadConfigs(string basePath)` (2026-08-08) |
| `Configs` dictionaries | `SerializedSortedDictionary<string, T>` with working `.Keys`, `.ContainsKey`, indexer (2026-08-08) |
| Deployed plugins | `ftk2mods.blessings`, `ftk2mods.classforge`, `ftk2mods.crucible`, `ftk2mods.devkit`, `ftk2mods.summoner`, `ftk2mods.wardrobe` |

### Live dictionary counts — the recorded baseline

| Dictionary | Live count | Vanilla baseline | Recorded pack contribution |
|---|---|---|---|
| `Characters` | **2126** | 2095 | `EOR_*` × **31** |
| `Things` | **2380** | 1847 | `ARM_*` × **533** |
| `Abilities` | **992** | 992 | — |
| `SkillConfigs` | **68** | 68 | — |
| `StatusEffects` | **189** | 189 | — |
| `Followers` | **48** | 48 | — |
| `Langs["en"]` | **9707** keys | 9707 | — |

`2095 + 31 = 2126` and `1847 + 533 = 2380` exactly. That arithmetic **is** the provenance gate.

`ARM_*` content is **this repo's own FTK2.Armory**, deployed as seven files into `Configs\JSON~\Things\`: `ARM_CATALOG_S13` (15), `ARM_CATALOG_S46` (15), `ARM_CATALOG_S79` (13), `ARM_CATALOG_UNIQ` (13), `ARM_EOR_ITEMS` (386), `ARM_EOR_STARTERS` (31), `ARM_FORGE_CURATED` (60) = 533. `EOR_*` content is third-party (`package_id: sirpepperpot.enhanced-overhaul-revamped`, `package_version: 0.7.0.62`) and is confined to `Characters.json` — measured **zero** `EOR_` ids in `Abilities`, `SkillConfigs`, `StatusEffects`, `Followers`, `LootDrops`, `Things`, and `Langs/en.json`.

**Zero `CF_`, `BLSS_`, or `SMN_` ids appear anywhere on disk.** Repo packs are applied at runtime by their plugins, never baked into `StreamingAssets`, and their localization is injected at runtime too (on-disk `en.json` has 9707 keys and none of those prefixes). That is an invariant the harness enforces.

### Vocabulary, learned from live data

| Vocabulary | Source | Measured |
|---|---|---|
| `BaseType` | `CharacterConfig.BaseType` over live `Characters` | **32** distinct values (`HUMAN`, `SKELETON`, `GOLEM`, …). A closed learned vocabulary, **not** a dictionary key — resolving it against `Configs.Characters` produced **31 false findings** in 2026-08-08. |
| `DefaultBodyType` | same | exactly `{ "F", "M" }` |
| Character tags | `CharacterConfig.Tags` over live `Characters` | **483** distinct, includes `PLAYER` |
| Thing tags | `ThingConfig.Tags` over live `Things` | **496** distinct |
| Enum members | enum types declared in the loaded `FTK2.dll` | 395 enum types / 4182 distinct member names (2026-08-08) |

### The reference-resolution corrections that stop false findings

1. **`Passives` resolve against `Configs.SkillConfigs` (68 entries), *not* `Configs.Abilities`.** Carried forward: a checker using `Abilities` produced **100 false dangling findings**. `SKILL_BLACKHOLE` ∈ `SkillConfigs`.
2. **`Passives` must ALSO resolve against the packs' own `skillrecipes.json` ids — and those are NOT in `MergePlan`.** *New correction, measured this session.* `ClassForge.Core.ParsedPack` carries `Classes/Traits/Abilities/Items/Statuses/Localization/Icons/Portraits/ModifierTable` and **no recipe collection**; `MergePlan` likewise. Resolving `Passives` against only `SkillConfigs ∪ MergePlan.Abilities ∪ MergePlan.StatusEffects` — the 2026-08-08 plan's rule — produces **56 false dangling findings** (40 from class `Passives`, 16 from trait `Equippable.Passives`), because the 60 authored `SKILL_CF_*`/`SKILL_BLSS_*` recipes live only in `skillrecipes.json`. The harness must build an authored-recipe id set by running `RecipeParser` over each pack's `skillrecipes.json`, plus the two ids `ModifierRecipeGenerator` synthesizes per modifier table.
3. **`SKILL_CF_ENCMOD_SELECT` / `SKILL_CF_ENCMOD_APPLY` are engine-synthesized, not authored.** `modifiers.json` declares `Selection.Recipe: "SKILL_CF_ENCMOD_SELECT"`; both it and its derived sibling come from `ModifierRecipeGenerator.Generate` at load time and appear in no `skillrecipes.json`. Derive them with the public `ModifierRecipeGenerator.DeriveApplyId(selectionRecipeId)` rather than hard-coding.
4. **`CF_SUMMON`, `TRAIT`, and `LOADOUT_0` are authored/engine tags absent from the learned live tag vocabularies.** Measured: pack tags not present in live vocabularies are exactly `{CF_PACK_EOR_CLASSES, CF_PACK_BALDURS, BLSS_PACK_EOR_BLESSINGS, CF_SUMMON, TRAIT, LOADOUT_0}`. The first three are loaded pack ids (already allowed); the last three need an explicit, documented allowlist or the tag rule yields 3 false findings. `TRAIT` and `LOADOUT_0` are probably `FTK2.dll` enum members and would be covered by the enum vocabulary anyway — Task 4 prints any allowlist entry that turned out to be redundant, so the list can be trimmed on evidence rather than guessed at.

### Pre-flight simulation of every check (run against real data, 2026-08-23)

| Check | Result today |
|---|---|
| Class/trait/item/status/ability ids colliding with live ids | **0** |
| Class `Passives` + `Things` references unresolved | **0** (with correction 2 applied) |
| Trait `Equippable.Passives` + status `Passives` unresolved | **0** (with correction 2 applied) |
| `BaseType`/`DefaultBodyType` outside the learned vocab | **0** (packs use `HUMAN`, `SKELETON`, `M`, `F`) |
| `Rarity`/`Class`/`Material`/`ConsumableType`/status `Type` outside learned vocab | **0** |
| Localization: merged class name + tooltip keys, trait/item name keys | **0 missing**, **0 shadowing vanilla**; merged pack localization = 410 keys (237 + 60 + 21 + 92) |
| Blessings roster `TraitId` unresolved | **0** (15/15 resolve into merged Things) |
| Summoner follower id collisions vs live `Followers` | **0** (200 adds across 2 packs; live `Followers` has zero `SMN_` ids) |
| Recipe-referenced `StatusEffect` ids unresolved | **1 — `CURSE`**, from `SKILL_CF_ENCMOD_CURSE_ON_HIT`. See AC9. |
| Authored recipes defined but unconsumed | **2** — `SKILL_CF_BEASTMASTER_PACK_TACTICS`, `SKILL_CF_AFFIX_OF_SCAVENGING_LOOT`. Both documented in `class-test-matrix.md` §1.3; reported as **Warnings**. |

So: the suite is expected to come up green except for the single `CURSE` Error, which is a genuine authoring bug this harness exists to find.

### Verified `.Core` API surface (read from source this session — do not re-derive)

**`ClassForge.Core`** (`netstandard2.0`):
- `PackLoadResult PackLoader.Load(IFileSource fs, IEnumerable<string> roots, Func<string,bool> isPackEnabled = null, LiveIdSets liveIds = null)` — an **instance** method on `new PackLoader()`.
- `LiveIdSets(IEnumerable<string> characters, IEnumerable<string> things, IEnumerable<string> abilities, IEnumerable<string> statusEffects = null)`.
- `PackLoadResult { List<PackManifest> DiscoveredPacks, EnabledOrderedPacks, SkippedPacks; MergePlan MergePlan; string DataHash; List<Finding> Findings; }`.
- `PackManifest { string Id, Name, Version, Author, Description; int LoadOrder; List<string> Dependencies; bool Enabled; string RootDir; }` — `RootDir` **is** populated (`ManifestParser.cs:53`).
- `MergePlan { List<MergeOp> Characters, Things, Abilities, StatusEffects; List<ModifierTable> ModifierTables; Dictionary<string,string> Localization, Icons, Portraits; List<string> TraitIds; List<Finding> Findings; }`. `plan.Findings` is the **same list instance** as `result.Findings`.
- `MergeOp { string Id { get; } JsonValue Value { get; } string SourcePackId { get; } }`.
- `Finding { FindingSeverity Severity { get; } string Code, Message, PackId { get; } }`; `ClassForge.Core.FindingSeverity` has `Info`/`Warning`/`Error` members (only `Error` is ever compared).
- `ModifierTable { string PackId, SchemaVersion, SelectionRecipe; List<ModifierEntry> Modifiers; }`; `ModifierEntry { string Id; int Weight; string Status; int? MaxHpPercent; ModifierRewards Rewards; }`.
- Live-collision behaviour: `MergePlanner.MergeCategory` emits `Finding.Error("CF_LIVE_ID_COLLISION", …)` and **skips** the id when `liveIds.Contains(id)`. Traits and items both merge into `plan.Things`; `plan.TraitIds` is the subset of `plan.Things` whose `Class == "TRAIT"`.
- `ClassForge.Core.IO.IFileSource` / `FileSystemFileSource` (real-disk implementation, already public).
- `ClassForge.Core.Json.JsonValue`: `JsonKind Kind`, `bool IsNull`, `JsonValue Get(string key)` (returns `JsonValue.Null`, never C# null), `string GetString(string key, string defaultValue = null)`, `List<string> GetStringArray(string key)`, `IReadOnlyList<KeyValuePair<string,JsonValue>> AsObjectMembers`, `IReadOnlyList<JsonValue> AsArray`, `string AsString`.
- `ClassForge.Core.DataHasher.HashPrefix == "sha256:"`; `PackLoadResult.DataHash` is `"sha256:" + 64 lowercase hex`.

**`ClassForge.Recipes`** (`netstandard2.0`):
- `RecipeSet ClassForge.Recipes.Parsing.RecipeParser.Parse(string json)`; `void ClassForge.Recipes.Parsing.RecipeValidator.Validate(RecipeSet set)`.
- `RecipeSet { IReadOnlyList<SkillRecipe> Ordered; IReadOnlyList<Finding> Findings; bool HasErrors; void Add(SkillRecipe); void AddFinding(Finding); SkillRecipe Find(string id); }`.
- `SkillRecipe { string Id; List<RecipeEffect> Effects; bool Enabled; bool DisabledByValidator; bool IsLive; … }`.
- `RecipeEffect { string Status; string FallbackStatus; List<string> StatusOneOf; string StatusFromSelection; List<SelectionStatusEntry> StatusFromSelectionTable; string CharacterConfig; … }`; `SelectionStatusEntry { string Value; string StatusId; }`.
- `ClassForge.Recipes.Model.Vocabulary.TriggerStatusToken == "TRIGGER_STATUS"` — a sentinel, **not** a content id; excluding it is mandatory or every recipe using it is a false positive.
- `ClassForge.Recipes.Model.FindingSeverity` is a **separate enum** from `ClassForge.Core.FindingSeverity`; both are named `FindingSeverity`, so any file touching both must fully qualify at least one.
- `ClassForge.Recipes.Generation.ModifierRecipeGenerator`: `GeneratedModifierRecipes Generate(ModifierTableInput table)`, `string DeriveApplyId(string selectionRecipeId)`, `string DeriveSelectionName(string selectionRecipeId)`. `ModifierTableInput { string SelectionRecipeId; List<ModifierRow> Modifiers; }`, `ModifierRow { string Id; int Weight; string Status; int? MaxHpPercent; }`, `GeneratedModifierRecipes { SkillRecipe Select; SkillRecipe Apply; string SelectionName; }`.

**`Blessings.Core`** (`netstandard2.0`):
- `ParseResult Blessings.Core.Parsing.BlessingsRegistryParser.Parse(string json)`.
- `ParseResult { BlessingsRegistry Registry; List<Blessings.Core.Diagnostics.Finding> Findings; bool Success; bool HasErrors; }`.
- `BlessingsRegistry { string SchemaVersion; IReadOnlyList<BlessingEntry> Blessings; BlessingEntry FindById(string); }`.
- `BlessingEntry { string Id; string TraitId; int Weight; bool Enabled; IReadOnlyList<string> Tags; }` (all get-only).

**`Summoner.Core`** (`netstandard2.0`):
- `LoadResult Summoner.Core.Packs.PackLoader.Load(IPackFileSource source, IJsonCodec codec, string root)` (**static**); `LoadResult { List<FollowerPack> Packs; List<Summoner.Core.Diagnostics.Finding> Findings; }`.
- `IPackFileSource { IEnumerable<string> ListPackDirectories(string root); bool Exists(string path); byte[] ReadAllBytes(string path); IEnumerable<string> ListFilesRecursive(string packDir); }` — `ListPackDirectories` returns **opaque identifiers**; the real adapter returns absolute directory paths and every other method joins sub-paths onto them, so returning `Directory.GetDirectories(root)` is correct.
- `IJsonCodec { T Deserialize<T>(string json); }`. `Summoner.Core.Packs.PackManifest` uses PascalCase properties over lowercase-camel JSON, so the codec **must** set `PropertyNameCaseInsensitive = true` or every pack is skipped as `manifest_invalid`.
- `MergePlan Summoner.Core.Merge.MergePlanner.Plan(IReadOnlyList<FollowerPack> packs, ISet<string> existingFollowerIds, ISet<string> existingCharacterIds)`; `MergePlan { List<MergeAdd> FollowerAdds, CharacterAdds; List<MergeSkip> Skips; List<Finding> Rejects; }`; `MergeSkip { string PackId, Id, Reason; }`; `MergeAdd { string PackId, Id; object Entry; }`.
- Neither shipped follower pack has a `characters.json`, so `CharacterAdds` is legitimately **0** — do not assert a floor on it.

**`DevKit.Core`** (`netstandard2.0`): the namespace is **`FTK2Mods.DevKit`**, which matches neither the folder nor the assembly name — `using DevKit.Core;` will not compile. `bool FTK2Mods.DevKit.DataHasher.IsWellFormedHash(string hash)` accepts a bare or `sha256:`-prefixed 64-hex digest.

### Repo pack inventory (authoritative — `docs/research/class-test-matrix.md`)

| Category | EOR_CLASSES | BALDURS | ENCOUNTER_MODIFIERS | BLESSINGS | Total |
|---|---|---|---|---|---|
| Classes | 31 | 4 | — | — | **35** (34 player-selectable; `CF_SKELETON_WARRIOR` is a `CF_SUMMON`) |
| Traits | 20 | 6 | — | 15 | **41** |
| Skill recipes | 52 | 5 | 2 | 1 | **60** |
| Blessings | — | — | — | 15 | **15** |
| Statuses | — | — | 10 | — | **10** |
| Encounter modifiers | — | — | 10 | — | **10** |
| Item-granted abilities | — | 13 | — | — | 13 |
| Items | — | 4 | — | — | 4 |

Follower packs (Summoner): `SMN_PACK_EOR_MERCS` (100 followers) + `SMN_PACK_EOR_PETS` (100 followers) = **200**. Merged `Things` = 41 traits + 4 items = **45**.

**Zero cross-pack references.** Every pack declares `"dependencies": []` and no pack's id prefixes appear in any other pack's JSON, so the harness only needs the base-game id universe plus each pack's own ids.

---

## File structure

| File | Responsibility |
|---|---|
| `FTK2.DevKit/sandbox/LiveDataHarness/LiveDataHarness.csproj` | `net10.0` exe; `ProjectReference`s only; no game/BepInEx/NuGet refs |
| `.../Harness.cs` | `Check` assertions + `CheckRunner` (console reporting, warning channel) |
| `.../GameInstall.cs` | Locate the install; expose `Managed` + `StreamingAssets` paths |
| `.../GameData.cs` | Reflective `LoadConfigs`; id-set snapshots; boxed values; `Langs` access |
| `.../InstallProvenance.cs` | **Pure** provenance arithmetic + the recorded install baseline. AC1/AC2 live here. |
| `.../GameVocabulary.cs` | Enum member names + learned `BaseType`/`BodyType`/tag/class/rarity vocabularies |
| `.../PackRoots.cs` | Repo pack directories, resolved by walking up from `AppContext.BaseDirectory` |
| `.../AuthoredContent.cs` | One `PackLoader.Load` + one `RecipeParser` pass per pack → the authored id universe every later check shares |
| `.../Io/HarnessPackSource.cs` | `Summoner.Core` `IPackFileSource` + `IJsonCodec` over the real filesystem |
| `.../Checks/AddsOnlyChecks.cs` | Pack load produces zero Errors; no pack id collides with a live id |
| `.../Checks/ReferenceChecks.cs` | Every id a pack points at resolves (the correction-2 checker) |
| `.../Checks/LocalizationChecks.cs` | Name + tooltip coverage; no vanilla shadowing |
| `.../Checks/BlessingsChecks.cs` | Offline replica of the fail-closed roster gate |
| `.../Checks/SummonerChecks.cs` | Follower packs adds-only vs live `Configs` |
| `.../Checks/RecipeChecks.cs` | Recipes parse/validate; status references resolve; modifier tables generate |
| `.../Checks/InventoryChecks.cs` | The class-test-matrix counts, so an undeployed or dropped pack cannot hide |
| `.../Checks/DeterminismChecks.cs` | Two loads agree on `dataHash` and merge ordering |
| `.../Report.cs` | Deterministic JSON report writer |
| `.../README.md` | One command, exit codes, check inventory, recorded baseline, limits |
| `.../fixtures/collide/CF_HARNESS_COLLIDE/{pack,classes}.json` | Ships a live vanilla id — proves adds-only bites |
| `.../fixtures/dangling/CF_HARNESS_DANGLING/{pack,classes}.json` | Ships a dangling `Passive` — proves the reference checker bites |
| `tools/run-harness.ps1` | One-command wrapper: build, run twice, prove cross-process determinism |

**Why this split.** One file per *check family*, because a check family is what a reviewer accepts or rejects as a unit and what a task delivers. `GameInstall`/`GameData`/`GameVocabulary` are separated because they are the only reflection-bearing code — keeping the game-coupling surface in three small files makes it auditable and marks exactly where a game update will break. `InstallProvenance` is deliberately **pure** (it takes id sets, not a `GameData`) so its three failure modes can be proven with synthetic snapshots, which is the only honest way to test them without damaging the real install.

---

### Task 1: Project skeleton, install resolution, reflective config load, smoke check

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/LiveDataHarness.csproj`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Harness.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/GameInstall.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/GameData.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - `sealed class CheckFailed : Exception` with `CheckFailed(string message)`.
  - `sealed class CheckFailure { public string Name; public string Message; }`
  - `static class Check` — `void True(bool value, string what)`, `void AtLeast(int floor, int actual, string what)`, `void Exactly(int expected, int actual, string what)`, `void Eq(string expected, string actual, string what)`, `void Empty(IEnumerable<string> offenders, string what)`.
  - `sealed class CheckRunner` — `void Section(string title)`, `void Case(string name, Action body)`, `void Warn(string name, IEnumerable<string> notes)`, `int Report()`, `IReadOnlyList<CheckFailure> Failures { get; }`, `IReadOnlyList<string> Warnings { get; }`, `int Passed { get; }`, `int Failed { get; }`.
  - `sealed class GameInstall` — `static GameInstall Resolve(string requested)` (null when not found), `string Root { get; }`, `string ManagedDir { get; }`, `string StreamingAssetsDir { get; }`.
  - `sealed class GameData` — `static GameData Load(GameInstall install)`, `object Configs { get; }`, `string ManagedDir { get; }`, `ISet<string> Ids(string dictName)`, `int Count(string dictName)`, `IEnumerable<object> Values(string dictName)`, `IDictionary<string,string> Lang(string code)`, `static object Field(object cfgObj, string fieldName)`.
  - `static class Program` — `int Main(string[] args)`, `static string ArgValue(string[] args, string name)`.

- [ ] **Step 1: Write `LiveDataHarness.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <RootNamespace>LiveDataHarness</RootNamespace>
    <AssemblyName>LiveDataHarness</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <!-- Runs the repo's host-agnostic .Core logic against the REAL game's Configs, loaded
         out-of-process by reflection. No game/BepInEx/Unity compile-time references and no
         NuGet packages: the game is reached only via Assembly.LoadFrom at runtime, so this
         project builds on a machine with no game installed. -->
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\FTK2.ClassForge\src\ClassForge.Core\ClassForge.Core.csproj" />
    <ProjectReference Include="..\..\..\FTK2.ClassForge\src\ClassForge.Recipes\ClassForge.Recipes.csproj" />
    <ProjectReference Include="..\..\..\FTK2.Summoner\src\Summoner.Core\Summoner.Core.csproj" />
    <ProjectReference Include="..\..\..\FTK2.Blessings\src\Blessings.Core\Blessings.Core.csproj" />
    <ProjectReference Include="..\..\src\DevKit.Core\DevKit.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write `Harness.cs`**

Deliberately mirrors `FTK2.ClassForge/src/ClassForge.Recipes.Tests/Harness.cs` and `FTK2.DevKit/sandbox/TypeProbe.Tests/TestHarness.cs` (same `Case`/`Section`/`Report` shape) so anyone who has read those can read this. Unlike `TestHarness` it holds **instance** state only — the harness runs one suite per process but the report writer needs the counts back, and static mutable state is what makes a runner untestable.

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveDataHarness
{
    /// <summary>Thrown by <see cref="Check"/> when an assertion fails. Distinct from a genuine
    /// exception so <see cref="CheckRunner"/> can report FAIL vs ERROR differently.</summary>
    public sealed class CheckFailed : Exception
    {
        public CheckFailed(string message) : base(message) { }
    }

    /// <summary>One failed check, carried into the JSON report.</summary>
    public sealed class CheckFailure
    {
        public string Name;
        public string Message;
    }

    /// <summary>Assertions. Static methods, no static fields.</summary>
    public static class Check
    {
        public static void True(bool value, string what)
        {
            if (!value) throw new CheckFailed("expected true: " + what);
        }

        public static void AtLeast(int floor, int actual, string what)
        {
            if (actual < floor)
                throw new CheckFailed(what + ": expected at least " + floor.ToString(CultureInfo.InvariantCulture) +
                                      ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }

        public static void Exactly(int expected, int actual, string what)
        {
            if (actual != expected)
                throw new CheckFailed(what + ": expected exactly " + expected.ToString(CultureInfo.InvariantCulture) +
                                      ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }

        public static void Eq(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new CheckFailed(what + ":\n  expected: " + (expected ?? "<null>") +
                                      "\n  actual:   " + (actual ?? "<null>"));
        }

        /// <summary>The workhorse: a check collects offender strings and asserts the list is empty.
        /// Offenders are ordinal-sorted so two runs report identically (AC5), and at most 15 are
        /// printed plus a count, so a wholesale break stays readable.</summary>
        public static void Empty(IEnumerable<string> offenders, string what)
        {
            List<string> list = offenders == null ? new List<string>() : offenders.ToList();
            if (list.Count == 0) return;
            list.Sort(StringComparer.Ordinal);
            string shown = string.Join("\n    ", list.Take(15));
            string more = list.Count > 15
                ? "\n    ... and " + (list.Count - 15).ToString(CultureInfo.InvariantCulture) + " more"
                : "";
            throw new CheckFailed(what + ": " + list.Count.ToString(CultureInfo.InvariantCulture) +
                                  " offender(s)\n    " + shown + more);
        }
    }

    /// <summary>Console check runner. Instance state only.</summary>
    public sealed class CheckRunner
    {
        private int _passed;
        private int _failed;
        private readonly List<CheckFailure> _failures = new List<CheckFailure>();
        private readonly List<string> _warnings = new List<string>();

        public IReadOnlyList<CheckFailure> Failures { get { return _failures; } }
        public IReadOnlyList<string> Warnings { get { return _warnings; } }
        public int Passed { get { return _passed; } }
        public int Failed { get { return _failed; } }

        public void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        public void Case(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (CheckFailed ex)
            {
                _failed++;
                _failures.Add(new CheckFailure { Name = name, Message = ex.Message });
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.Message.Replace("\n", "\n        "));
            }
            catch (Exception ex)
            {
                _failed++;
                _failures.Add(new CheckFailure { Name = name, Message = "unexpected " + ex.GetType().Name + ": " + ex.Message });
                Console.WriteLine("  ERROR " + name);
                Console.WriteLine("        " + ex);
            }
        }

        /// <summary>Records notes that must be visible but must never fail the run (repo rule:
        /// Warning findings never fail). Sorted for report determinism (AC5).</summary>
        public void Warn(string name, IEnumerable<string> notes)
        {
            List<string> list = notes == null ? new List<string>() : notes.ToList();
            if (list.Count == 0) return;
            list.Sort(StringComparer.Ordinal);
            Console.WriteLine("  WARN  " + name + " (" + list.Count.ToString(CultureInfo.InvariantCulture) + ")");
            for (int i = 0; i < list.Count; i++)
            {
                _warnings.Add(name + ": " + list[i]);
                Console.WriteLine("        " + list[i]);
            }
        }

        public int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("checks: " + (_passed + _failed).ToString(CultureInfo.InvariantCulture) +
                              "  passed: " + _passed.ToString(CultureInfo.InvariantCulture) +
                              "  failed: " + _failed.ToString(CultureInfo.InvariantCulture) +
                              "  warnings: " + _warnings.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _failures.Count; i++)
                Console.WriteLine("FAILED: " + _failures[i].Name);
            Console.WriteLine("---------------------------------------------");
            return _failed == 0 ? 0 : 1;
        }
    }
}
```

- [ ] **Step 3: Write `GameInstall.cs`**

The candidate list matches `tools/deploy.ps1`'s `Resolve-GameDir` so the harness and the deployer always agree on which install is "the" install. The verified path on this machine is the `Program Files (x86)` one; the `E:\` entry is kept because `deploy.ps1` carries it and a divergence between the two tools is worse than a dead probe.

```csharp
using System.IO;

namespace LiveDataHarness
{
    /// <summary>Locates a For The King II install. Read-only: every member is a path accessor or an
    /// Exists probe. Nothing in this class or anything downstream ever writes to the game folder.</summary>
    public sealed class GameInstall
    {
        public string Root { get; private set; }

        public string ManagedDir
        {
            get { return Path.Combine(Root, "For The King II_Data", "Managed"); }
        }

        public string StreamingAssetsDir
        {
            get { return Path.Combine(Root, "For The King II_Data", "StreamingAssets", "Assets"); }
        }

        private GameInstall(string root) { Root = root; }

        private static readonly string[] Candidates =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\For The King II",
            @"C:\Program Files\Steam\steamapps\common\For The King II",
            @"E:\Games\Steam\steamapps\common\For The King II",
        };

        /// <summary>Returns null when no install is found; the caller exits 2 (skipped, not failed).</summary>
        public static GameInstall Resolve(string requested)
        {
            if (!string.IsNullOrEmpty(requested)) return Probe(requested);
            for (int i = 0; i < Candidates.Length; i++)
            {
                GameInstall hit = Probe(Candidates[i]);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GameInstall Probe(string root)
        {
            string marker = Path.Combine(root, "For The King II_Data", "Managed", "FTK2.dll");
            return File.Exists(marker) ? new GameInstall(root) : null;
        }
    }
}
```

- [ ] **Step 4: Write `GameData.cs`**

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LiveDataHarness
{
    /// <summary>
    /// The only place the harness touches the game. Loads FTK2.dll out of the install's Managed folder and
    /// invokes <c>ConfigsHelper.LoadConfigs(basePath)</c> by reflection — no compile-time game reference, no
    /// Harmony, no Unity shim (verified 2026-08-08: LoadConfigs completes in ~1.6-1.9s in a plain net10
    /// process with zero patches applied). Everything downstream sees plain string sets and boxed config
    /// objects, so a game update can only break this file plus GameVocabulary.
    /// </summary>
    public sealed class GameData
    {
        public object Configs { get; private set; }
        public string ManagedDir { get; private set; }

        private readonly Dictionary<string, ISet<string>> _idCache =
            new Dictionary<string, ISet<string>>(StringComparer.Ordinal);

        public static GameData Load(GameInstall install)
        {
            string managed = install.ManagedDir;

            // Sibling assemblies (UnityEngine, Photon, ...) resolve on demand out of the live Managed folder.
            // tools/bin/refs is deliberately NOT used: it is an 80-file snapshot and the live folder has 205,
            // so resolving against the snapshot fails on e.g. Unity.InputSystem.dll.
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object sender, ResolveEventArgs e)
            {
                string name = new AssemblyName(e.Name).Name;
                string probe = Path.Combine(managed, name + ".dll");
                return File.Exists(probe) ? Assembly.LoadFrom(probe) : null;
            };

            Assembly ftk2 = Assembly.LoadFrom(Path.Combine(managed, "FTK2.dll"));

            Type helper = ftk2.GetType("ConfigsHelper", false);
            if (helper == null)
                throw new InvalidOperationException("Type 'ConfigsHelper' not found in FTK2.dll — a game update broke the harness. Re-ground with TypeProbe.");

            MethodInfo load = helper.GetMethod("LoadConfigs", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string) }, null);
            if (load == null)
                throw new InvalidOperationException("ConfigsHelper.LoadConfigs(string) not found — a game update broke the harness. Re-ground with TypeProbe.");

            object configs = load.Invoke(null, new object[] { install.StreamingAssetsDir });
            if (configs == null)
                throw new InvalidOperationException("ConfigsHelper.LoadConfigs returned null.");

            GameData data = new GameData();
            data.Configs = configs;
            data.ManagedDir = managed;
            return data;
        }

        private object Dict(string dictName)
        {
            FieldInfo field = Configs.GetType().GetField(dictName);
            if (field == null)
                throw new InvalidOperationException("Configs." + dictName + " does not exist — a game update broke the harness. Re-ground with TypeProbe.");
            return field.GetValue(Configs);
        }

        /// <summary>Ordinal key set of one Configs.* dictionary. Cached: LoadConfigs is ~1.6s and key walks
        /// over a 2000-entry SerializedSortedDictionary are not free either.</summary>
        public ISet<string> Ids(string dictName)
        {
            ISet<string> cached;
            if (_idCache.TryGetValue(dictName, out cached)) return cached;

            object dict = Dict(dictName);
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            if (dict != null)
            {
                IEnumerable keys = dict.GetType().GetProperty("Keys").GetValue(dict, null) as IEnumerable;
                if (keys != null)
                {
                    foreach (object k in keys)
                    {
                        string s = k as string;
                        if (s != null) set.Add(s);
                    }
                }
            }
            _idCache[dictName] = set;
            return set;
        }

        public int Count(string dictName) { return Ids(dictName).Count; }

        /// <summary>Boxed values of one Configs.* dictionary, for field-level reflection
        /// (e.g. CharacterConfig.BaseType).</summary>
        public IEnumerable<object> Values(string dictName)
        {
            object dict = Dict(dictName);
            if (dict == null) yield break;
            foreach (object kv in (IEnumerable)dict)
                yield return kv.GetType().GetProperty("Value").GetValue(kv, null);
        }

        /// <summary>Reads a public field off a boxed game config object. Returns null when the field is
        /// absent, so a game update degrades to a null rather than an exception (spec §3 error posture).</summary>
        public static object Field(object cfgObj, string fieldName)
        {
            if (cfgObj == null) return null;
            FieldInfo f = cfgObj.GetType().GetField(fieldName);
            return f == null ? null : f.GetValue(cfgObj);
        }

        /// <summary>Configs.Langs[code] as a plain string dictionary. Returns null when absent.</summary>
        public IDictionary<string, string> Lang(string code)
        {
            object langs = Dict("Langs");
            if (langs == null) return null;
            MethodInfo contains = langs.GetType().GetMethod("ContainsKey");
            if (contains == null) return null;
            if (!(bool)contains.Invoke(langs, new object[] { code })) return null;
            return langs.GetType().GetProperty("Item").GetValue(langs, new object[] { code }) as IDictionary<string, string>;
        }
    }
}
```

- [ ] **Step 5: Write `Program.cs` with the smoke check only**

```csharp
using System;

namespace LiveDataHarness
{
    /// <summary>
    /// Runs the repo's .Core logic against the real game's Configs, with no game running.
    ///
    ///   dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release -- --json out.json
    ///
    /// Exit codes: 0 = every check passed · 1 = at least one check failed · 2 = no loadable game
    /// install (skipped, not failed).
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string gameDir = ArgValue(args, "--game-dir");

            GameInstall install = GameInstall.Resolve(gameDir);
            if (install == null)
            {
                Console.WriteLine("LiveDataHarness: SKIPPED — no For The King II install found.");
                Console.WriteLine("  Pass --game-dir \"X:\\...\\steamapps\\common\\For The King II\".");
                return 2;
            }

            Console.WriteLine("LiveDataHarness");
            Console.WriteLine("  game: " + install.Root);

            GameData data;
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                data = GameData.Load(install);
                sw.Stop();
                Console.WriteLine("  ConfigsHelper.LoadConfigs OK in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine("LiveDataHarness: SKIPPED — could not load Configs: " + ex.Message);
                return 2;
            }

            CheckRunner runner = new CheckRunner();

            runner.Section("Live config sanity");
            runner.Case("live Configs are populated", delegate
            {
                Check.AtLeast(1800, data.Count("Things"), "Configs.Things");
                Check.AtLeast(2000, data.Count("Characters"), "Configs.Characters");
                Check.AtLeast(900, data.Count("Abilities"), "Configs.Abilities");
                Check.AtLeast(50, data.Count("SkillConfigs"), "Configs.SkillConfigs");
                Check.AtLeast(150, data.Count("StatusEffects"), "Configs.StatusEffects");
                Check.AtLeast(40, data.Count("Followers"), "Configs.Followers");
                Check.True(data.Lang("en") != null, "Configs.Langs contains 'en'");
                Check.AtLeast(9000, data.Lang("en").Count, "Configs.Langs['en'] key count");
            });

            runner.Case("negative control: an invented dictionary key is absent", delegate
            {
                // If Ids() ever returned a set that answered true to everything, every adds-only and
                // reference check downstream would pass vacuously. Prove it does not.
                Check.True(!data.Ids("Characters").Contains("LDH_NEVER_SHIPPED_CHARACTER"),
                    "Configs.Characters must not contain an invented id");
                Check.True(!data.Ids("Things").Contains("LDH_NEVER_SHIPPED_THING"),
                    "Configs.Things must not contain an invented id");
            });

            return runner.Report();
        }

        /// <summary>Returns the value following <paramref name="name"/>, or null.</summary>
        public static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }
    }
}
```

- [ ] **Step 6: Verify it builds and runs green**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: prints `game: C:\Program Files (x86)\Steam\steamapps\common\For The King II`, `ConfigsHelper.LoadConfigs OK in <1500-2500> ms`, both cases PASS, `checks: 2  passed: 2  failed: 0  warnings: 0`, exit 0.

If `Configs.Things` reads far below 2380 or `Configs.Characters` below 2126, **stop and report** — content the co-op saves depend on is missing from the install. Do **not** run Steam's *Verify integrity of game files*; that is what removes it.

- [ ] **Step 7: Verify the no-install path (AC4)**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release -- --game-dir "Z:\nope"
```

Expected: `LiveDataHarness: SKIPPED — no For The King II install found.` and exit code 2. Confirm with `echo $LASTEXITCODE` in PowerShell.

- [ ] **Step 8: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T1: reflective live-Configs loader and check runner skeleton"
```

---

### Task 2: Game vocabulary (enum names + learned value sets)

Without this, every enum-valued and closed-vocabulary JSON field (`Rarity: "COMMON"`, `BaseType: "HUMAN"`, `Tags: ["RANGED"]`) looks like a dangling content id. Measured 2026-08-08: a naive checker produced 131 false findings; this task is what removes them.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/GameVocabulary.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData.Configs`, `GameData.Values`, `GameData.Field` (Task 1).
- Produces: `sealed class GameVocabulary` with `static GameVocabulary Build(GameData data)` and get-only members `ISet<string> EnumMembers`, `BaseTypes`, `BodyTypes`, `CharacterTags`, `ThingTags`, `ThingClasses`, `Rarities`, `Materials`, `ConsumableTypes`, `StatusTypes`, `Expansions`.

- [ ] **Step 1: Write `GameVocabulary.cs`**

`EnumMembers` is read from the already-loaded `FTK2.dll` (no `MetadataLoadContext` — the assembly is live in this process). Everything else is *learned* from live config values, because those fields are `string`/`List<string>` with a closed de-facto vocabulary rather than declared enums.

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiveDataHarness
{
    /// <summary>
    /// The allowlist that separates "this string is a content id that must resolve" from "this string is an
    /// enum member or a closed-vocabulary value". Measured against the live install 2026-08-23:
    /// CharacterConfig.BaseType has 32 distinct values, DefaultBodyType exactly two ("F","M"), character
    /// Tags 483 distinct, Thing Tags 496 distinct; FTK2.dll declares 395 enum types / 4182 member names.
    /// Checking BaseType against Configs.Characters instead produced 31 false findings (2026-08-08).
    /// </summary>
    public sealed class GameVocabulary
    {
        public ISet<string> EnumMembers { get; private set; }
        public ISet<string> BaseTypes { get; private set; }
        public ISet<string> BodyTypes { get; private set; }
        public ISet<string> CharacterTags { get; private set; }
        public ISet<string> ThingTags { get; private set; }
        public ISet<string> ThingClasses { get; private set; }
        public ISet<string> Rarities { get; private set; }
        public ISet<string> Materials { get; private set; }
        public ISet<string> ConsumableTypes { get; private set; }
        public ISet<string> StatusTypes { get; private set; }
        public ISet<string> Expansions { get; private set; }

        public static GameVocabulary Build(GameData data)
        {
            HashSet<string> enums = new HashSet<string>(StringComparer.Ordinal);
            Assembly ftk2 = data.Configs.GetType().Assembly;
            foreach (Type t in SafeGetTypes(ftk2))
            {
                if (t == null || !t.IsEnum) continue;
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    enums.Add(f.Name);
            }

            HashSet<string> baseTypes = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> bodyTypes = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> charTags = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> expansions = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> charRarities = new HashSet<string>(StringComparer.Ordinal);

            foreach (object ch in data.Values("Characters"))
            {
                AddString(baseTypes, GameData.Field(ch, "BaseType"));
                AddString(bodyTypes, GameData.Field(ch, "DefaultBodyType"));
                AddAll(charTags, GameData.Field(ch, "Tags"));
                // Rarity and Expansion are enum-typed on CharacterConfig (eItemRarities / eExpansions);
                // their boxed values stringify to the member name, so learning them here gives one
                // vocabulary per field family without needing the enum type itself.
                AddString(expansions, GameData.Field(ch, "Expansion"));
                AddString(charRarities, GameData.Field(ch, "Rarity"));
            }

            HashSet<string> thingTags = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> thingClasses = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> rarities = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> materials = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> consumables = new HashSet<string>(StringComparer.Ordinal);

            foreach (object th in data.Values("Things"))
            {
                AddAll(thingTags, GameData.Field(th, "Tags"));
                AddString(thingClasses, GameData.Field(th, "Class"));
                AddString(rarities, GameData.Field(th, "Rarity"));
                AddString(materials, GameData.Field(th, "Material"));
                AddString(consumables, GameData.Field(th, "ConsumableType"));
            }

            HashSet<string> statusTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (object st in data.Values("StatusEffects"))
                AddString(statusTypes, GameData.Field(st, "Type"));

            rarities.UnionWith(charRarities);

            GameVocabulary v = new GameVocabulary();
            v.EnumMembers = enums;
            v.BaseTypes = baseTypes;
            v.BodyTypes = bodyTypes;
            v.CharacterTags = charTags;
            v.ThingTags = thingTags;
            v.ThingClasses = thingClasses;
            v.Rarities = rarities;
            v.Materials = materials;
            v.ConsumableTypes = consumables;
            v.StatusTypes = statusTypes;
            v.Expansions = expansions;
            return v;
        }

        private static void AddString(HashSet<string> target, object value)
        {
            if (value == null) return;
            string s = value as string;
            if (s == null) s = value.ToString();
            if (!string.IsNullOrEmpty(s)) target.Add(s);
        }

        private static void AddAll(HashSet<string> target, object listValue)
        {
            IEnumerable list = listValue as IEnumerable;
            if (list == null) return;
            foreach (object item in list) AddString(target, item);
        }

        /// <summary>FTK2.dll references assemblies that may not resolve outside the player (editor-only
        /// modules); a partial type list is fine for enum harvesting, a thrown
        /// ReflectionTypeLoadException is not. Same posture as TypeProbe.GameAssembly.</summary>
        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(delegate (Type t) { return t != null; }); }
        }
    }
}
```

- [ ] **Step 2: Register the vocabulary section in `Program.cs`**

Insert directly after the "Live config sanity" section, hoisting `vocab` so later tasks can use it:

```csharp
            GameVocabulary vocab = GameVocabulary.Build(data);

            runner.Section("Game vocabulary");
            runner.Case("enum + learned vocabularies are populated", delegate
            {
                Check.AtLeast(2000, vocab.EnumMembers.Count, "distinct enum member names in FTK2.dll");
                Check.True(vocab.EnumMembers.Contains("COMMON"), "enum vocabulary contains COMMON");
                Check.True(vocab.EnumMembers.Contains("MELEE"), "enum vocabulary contains MELEE");
                Check.AtLeast(25, vocab.BaseTypes.Count, "learned CharacterConfig.BaseType values (32 measured)");
                Check.True(vocab.BaseTypes.Contains("HUMAN"), "BaseType vocabulary contains HUMAN");
                Check.True(vocab.BaseTypes.Contains("SKELETON"), "BaseType vocabulary contains SKELETON");
                Check.True(vocab.BodyTypes.Contains("M") && vocab.BodyTypes.Contains("F"), "BodyType vocabulary is {F,M}");
                Check.AtLeast(400, vocab.CharacterTags.Count, "learned character tags (483 measured)");
                Check.True(vocab.CharacterTags.Contains("PLAYER"), "character tag vocabulary contains PLAYER");
                Check.AtLeast(400, vocab.ThingTags.Count, "learned Thing tags (496 measured)");
                Check.AtLeast(1, vocab.StatusTypes.Count, "learned StatusEffect types");
                Check.AtLeast(1, vocab.Expansions.Count, "learned CharacterConfig.Expansion values");
                Check.True(vocab.Rarities.Contains("COMMON"), "Rarity vocabulary contains COMMON");
            });

            runner.Case("negative control: vocabularies reject an invented value", delegate
            {
                // A vocabulary built from an empty or always-true source would wave every value through,
                // making the whole reference checker vacuous.
                Check.True(!vocab.BaseTypes.Contains("LDH_NOT_A_BASETYPE"), "BaseTypes must reject an invented value");
                Check.True(!vocab.BodyTypes.Contains("Q"), "BodyTypes must reject an invented value");
                Check.True(!vocab.EnumMembers.Contains("LDH_NOT_AN_ENUM_MEMBER"), "EnumMembers must reject an invented value");
                Check.True(!vocab.CharacterTags.Contains("LDH_NOT_A_TAG"), "CharacterTags must reject an invented value");
            });
```

- [ ] **Step 3: Run and verify**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: `checks: 4  passed: 4  failed: 0`, exit 0. If `BaseTypes.Count` is far from 32 or `BodyTypes` is not `{F,M}`, the `CharacterConfig` field names have drifted — re-ground with `dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- CharacterConfig` before touching this file.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T2: enum and learned-value vocabularies from the live game assembly"
```

---

### Task 3: Install provenance gate — AC1 and AC2, rewritten for the contaminated baseline

**This is the task the 2026-08-08 plan got backwards.** That plan's AC1 demanded a pristine install reading `Characters` 2095, and its AC2 manufactured contamination to prove a purity gate fires. Both are wrong here: the owner's install is deliberately EOR-contaminated, his friends' co-op saves depend on that content, and this repo's own FTK2.Armory writes 533 more ids onto disk. A gate that goes red on a correct setup — and whose remediation advice is "Steam → Verify integrity of game files" — would destroy the thing it was meant to protect.

The gate is therefore **inverted**: assert the expected pack set is *present*, assert every live id is *accounted for*, and flag the *unexpected*. Version drift in an expected pack is a Warning, not a failure.

`InstallProvenance` is a **pure** function of `(dictionary name → id set)`. That is deliberate: the three failure modes cannot be demonstrated on the real install without damaging it, so each gets a synthetic-snapshot negative control instead. Purity is what makes those controls possible.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/InstallProvenance.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData.Ids` (Task 1), `CheckRunner`, `Check` (Task 1).
- Produces:
  - `sealed class DictBaseline { public string Dictionary; public int VanillaCount; }`
  - `sealed class ExpectedPack { public string Label; public string Prefix; public string Dictionary; public int RecordedCount; }`
  - `static class InstallProvenance` with
    - `static readonly DictBaseline[] Baselines`
    - `static readonly ExpectedPack[] Expected`
    - `static readonly string[] RepoAuthoredPrefixes`
    - `static IDictionary<string, ISet<string>> Snapshot(GameData data)`
    - `static List<string> MissingExpected(IDictionary<string, ISet<string>> snapshot)`
    - `static List<string> Unaccounted(IDictionary<string, ISet<string>> snapshot)`
    - `static List<string> RepoIdsOnDisk(IDictionary<string, ISet<string>> snapshot)`
    - `static List<string> VersionDrift(IDictionary<string, ISet<string>> snapshot)`
    - `static void Register(CheckRunner runner, GameData data)`

- [ ] **Step 1: Write `InstallProvenance.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveDataHarness
{
    /// <summary>One live Configs dictionary and the count vanilla alone contributes to it.</summary>
    public sealed class DictBaseline
    {
        public string Dictionary;
        public int VanillaCount;
    }

    /// <summary>One content pack this install is EXPECTED to carry on disk, identified by the id prefix it
    /// writes into a named Configs dictionary.</summary>
    public sealed class ExpectedPack
    {
        public string Label;
        public string Prefix;
        public string Dictionary;
        public int RecordedCount;
    }

    /// <summary>
    /// The provenance gate, inverted for this install (spec §8).
    ///
    /// The live install is deliberately contaminated and that is CORRECT: Enhanced Overhaul Revamped
    /// (sirpepperpot.enhanced-overhaul-revamped 0.7.0.62) writes 31 EOR_* classes into
    /// StreamingAssets\...\Configs\JSON~\Characters.json, and this repo's own FTK2.Armory writes 533 ARM_*
    /// Things across seven files under Configs\JSON~\Things\. The owner's co-op saves depend on that
    /// content. Steam's "Verify integrity of game files" DELETES it, so this class never recommends it.
    ///
    /// What is asserted instead, measured 2026-08-23:
    ///   1. every expected pack is PRESENT      (Error when absent  — the Steam-verify-wiped-it failure)
    ///   2. every live id is ACCOUNTED FOR      (Error on a surplus — an unrecorded third mod)
    ///   3. no repo-authored id is ON DISK      (Error when found   — a repo pack baked into StreamingAssets
    ///                                           would make every adds-only check measure itself)
    ///   4. expected-pack counts match record   (Warning on drift   — a legitimate dependency update)
    ///
    /// Rule 2 is arithmetic (live == vanilla baseline + recorded pack contributions), so it catches an
    /// unknown modder without needing to know their id prefix. It stays green under rule-4 drift because it
    /// counts the prefix hits actually present, not the recorded number.
    ///
    /// Every method below is PURE over a (dictionary name -> id set) snapshot. That is what lets the three
    /// Error paths be proven with synthetic snapshots instead of by damaging the real install.
    /// </summary>
    public static class InstallProvenance
    {
        /// <summary>Vanilla-only counts. Measured by subtracting each dictionary's recorded pack
        /// contribution from the live count on 2026-08-23: Characters 2126-31=2095, Things 2380-533=1847;
        /// the rest carry no recorded pack content and were read directly.</summary>
        public static readonly DictBaseline[] Baselines =
        {
            new DictBaseline { Dictionary = "Characters",    VanillaCount = 2095 },
            new DictBaseline { Dictionary = "Things",        VanillaCount = 1847 },
            new DictBaseline { Dictionary = "Abilities",     VanillaCount = 992  },
            new DictBaseline { Dictionary = "SkillConfigs",  VanillaCount = 68   },
            new DictBaseline { Dictionary = "StatusEffects", VanillaCount = 189  },
            new DictBaseline { Dictionary = "Followers",     VanillaCount = 48   },
        };

        /// <summary>Content this install is expected to carry on disk. Absence is an Error, not a pass.</summary>
        public static readonly ExpectedPack[] Expected =
        {
            new ExpectedPack {
                Label = "Enhanced Overhaul Revamped (third-party, sirpepperpot.enhanced-overhaul-revamped; the co-op saves depend on it)",
                Prefix = "EOR_", Dictionary = "Characters", RecordedCount = 31 },
            new ExpectedPack {
                Label = "FTK2.Armory (this repo, deployed as seven ARM_*.json files under Configs\\JSON~\\Things)",
                Prefix = "ARM_", Dictionary = "Things", RecordedCount = 533 },
        };

        /// <summary>Prefixes owned by this repo's runtime-applied packs. These are merged into Configs by
        /// their plugins at load time and must NEVER be found written into StreamingAssets — if one is, the
        /// adds-only checks are comparing pack content against itself.</summary>
        public static readonly string[] RepoAuthoredPrefixes = { "CF_", "BLSS_", "SMN_" };

        /// <summary>Adapter from the live game to the pure functions below.</summary>
        public static IDictionary<string, ISet<string>> Snapshot(GameData data)
        {
            Dictionary<string, ISet<string>> snapshot =
                new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < Baselines.Length; i++)
                snapshot[Baselines[i].Dictionary] = data.Ids(Baselines[i].Dictionary);
            return snapshot;
        }

        /// <summary>Rule 1 — an expected pack contributing zero ids has been wiped off the disk.</summary>
        public static List<string> MissingExpected(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> offenders = new List<string>();
            for (int i = 0; i < Expected.Length; i++)
            {
                ExpectedPack pack = Expected[i];
                int found = CountWithPrefix(snapshot, pack.Dictionary, pack.Prefix);
                if (found == 0)
                    offenders.Add("EXPECTED CONTENT MISSING: no '" + pack.Prefix + "' ids in Configs." +
                                  pack.Dictionary + " — " + pack.Label +
                                  ". Recorded contribution was " + pack.RecordedCount.ToString(CultureInfo.InvariantCulture) +
                                  ". Restore the mod's files; do NOT run Steam's 'Verify integrity of game files'.");
            }
            return offenders;
        }

        /// <summary>Rule 2 — live count must equal vanilla baseline plus the prefix hits actually present.
        /// Any residual is content nobody recorded.</summary>
        public static List<string> Unaccounted(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> offenders = new List<string>();
            for (int i = 0; i < Baselines.Length; i++)
            {
                DictBaseline baseline = Baselines[i];
                ISet<string> ids;
                if (!snapshot.TryGetValue(baseline.Dictionary, out ids) || ids == null) continue;

                int accounted = baseline.VanillaCount;
                for (int p = 0; p < Expected.Length; p++)
                    if (string.Equals(Expected[p].Dictionary, baseline.Dictionary, StringComparison.Ordinal))
                        accounted += CountWithPrefix(snapshot, baseline.Dictionary, Expected[p].Prefix);

                int delta = ids.Count - accounted;
                if (delta > 0)
                    offenders.Add("UNACCOUNTED CONTENT: Configs." + baseline.Dictionary + " holds " +
                                  ids.Count.ToString(CultureInfo.InvariantCulture) + " ids but only " +
                                  accounted.ToString(CultureInfo.InvariantCulture) +
                                  " are accounted for (vanilla " + baseline.VanillaCount.ToString(CultureInfo.InvariantCulture) +
                                  " + recorded packs) — " + delta.ToString(CultureInfo.InvariantCulture) +
                                  " surplus id(s) from an unrecorded source. Identify it and add it to " +
                                  "InstallProvenance.Expected, or remove it.");
                else if (delta < 0)
                    offenders.Add("CONTENT SHORTFALL: Configs." + baseline.Dictionary + " holds " +
                                  ids.Count.ToString(CultureInfo.InvariantCulture) + " ids, " +
                                  (-delta).ToString(CultureInfo.InvariantCulture) +
                                  " fewer than the recorded baseline of " + accounted.ToString(CultureInfo.InvariantCulture) +
                                  ". Vanilla content is missing — the install has been damaged.");
            }
            return offenders;
        }

        /// <summary>Rule 3 — a repo-authored id found in the live Configs means a pack was baked onto disk
        /// instead of applied at runtime.</summary>
        public static List<string> RepoIdsOnDisk(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> offenders = new List<string>();
            foreach (KeyValuePair<string, ISet<string>> entry in snapshot)
            {
                if (entry.Value == null) continue;
                foreach (string id in entry.Value)
                {
                    for (int p = 0; p < RepoAuthoredPrefixes.Length; p++)
                    {
                        if (!id.StartsWith(RepoAuthoredPrefixes[p], StringComparison.Ordinal)) continue;
                        offenders.Add("REPO CONTENT ON DISK: Configs." + entry.Key + "." + id +
                                      " carries repo prefix '" + RepoAuthoredPrefixes[p] +
                                      "'. Repo packs are applied at runtime by their plugins and must never " +
                                      "be written into StreamingAssets — while one is, every adds-only check " +
                                      "is comparing the pack against itself.");
                        break;
                    }
                }
            }
            return offenders;
        }

        /// <summary>Rule 4 — a recorded pack whose id count moved. A dependency update is legitimate, so
        /// this is a Warning; it exists so the recorded numbers get refreshed on evidence.</summary>
        public static List<string> VersionDrift(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> notes = new List<string>();
            for (int i = 0; i < Expected.Length; i++)
            {
                ExpectedPack pack = Expected[i];
                int found = CountWithPrefix(snapshot, pack.Dictionary, pack.Prefix);
                if (found != 0 && found != pack.RecordedCount)
                    notes.Add(pack.Prefix + " in Configs." + pack.Dictionary + ": " +
                              found.ToString(CultureInfo.InvariantCulture) + " ids, recorded " +
                              pack.RecordedCount.ToString(CultureInfo.InvariantCulture) +
                              " — likely a pack update (" + pack.Label +
                              "). Update ExpectedPack.RecordedCount and the README baseline table.");
            }
            return notes;
        }

        private static int CountWithPrefix(IDictionary<string, ISet<string>> snapshot, string dictName, string prefix)
        {
            ISet<string> ids;
            if (!snapshot.TryGetValue(dictName, out ids) || ids == null) return 0;
            int n = 0;
            foreach (string id in ids)
                if (id.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        public static void Register(CheckRunner runner, GameData data)
        {
            IDictionary<string, ISet<string>> snapshot = Snapshot(data);

            runner.Section("Install provenance (expected-content gate)");

            runner.Case("every expected content pack is present on disk", delegate
            {
                Check.Empty(MissingExpected(snapshot), "expected content packs missing from the install");
            });

            runner.Case("every live id is accounted for by vanilla or a recorded pack", delegate
            {
                Check.Empty(Unaccounted(snapshot), "unaccounted-for content in the live Configs");
            });

            runner.Case("no repo-authored id is written into the game's StreamingAssets", delegate
            {
                Check.Empty(RepoIdsOnDisk(snapshot), "repo pack content found baked into the game folder");
            });

            runner.Warn("expected-pack version drift", VersionDrift(snapshot));

            // ---- negative controls -------------------------------------------------------------
            // The real install must not be mutated to prove these fire, so each runs the same pure
            // function over a synthetic snapshot. Without them all three checks above could be
            // permanently, silently green.

            runner.Case("NEGATIVE: a wiped expected pack IS reported", delegate
            {
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                RemovePrefix(fake, "Characters", "EOR_");
                List<string> hits = MissingExpected(fake);
                Check.AtLeast(1, hits.Count, "MissingExpected must fire when EOR_ content is gone");
                Check.True(hits[0].Contains("EOR_"), "the finding must name the missing prefix; got: " + hits[0]);
            });

            runner.Case("NEGATIVE: an unrecorded third mod IS reported", delegate
            {
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                fake["Characters"].Add("XMOD_INTRUDER_00");
                List<string> hits = Unaccounted(fake);
                Check.AtLeast(1, hits.Count, "Unaccounted must fire on a surplus id");
                Check.True(hits[0].Contains("Characters"), "the finding must name the dictionary; got: " + hits[0]);
            });

            runner.Case("NEGATIVE: a repo pack baked onto disk IS reported", delegate
            {
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                fake["Characters"].Add("CF_EOR_BARD");
                List<string> hits = RepoIdsOnDisk(fake);
                Check.AtLeast(1, hits.Count, "RepoIdsOnDisk must fire on a CF_ id in the live Configs");
                Check.True(hits[0].Contains("CF_EOR_BARD"), "the finding must name the id; got: " + hits[0]);
            });

            runner.Case("NEGATIVE: a clean synthetic baseline produces no findings", delegate
            {
                // Without this, all three controls above would still pass if the functions returned a
                // finding for literally every input.
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                Check.Empty(MissingExpected(fake), "MissingExpected on a clean synthetic baseline");
                Check.Empty(Unaccounted(fake), "Unaccounted on a clean synthetic baseline");
                Check.Empty(RepoIdsOnDisk(fake), "RepoIdsOnDisk on a clean synthetic baseline");
                Check.Empty(VersionDrift(fake), "VersionDrift on a clean synthetic baseline");
            });
        }

        /// <summary>A synthetic snapshot matching the recorded baseline exactly: for each dictionary,
        /// VanillaCount placeholder ids plus RecordedCount prefixed ids for each expected pack. Used only by
        /// the negative controls — it never touches the game.</summary>
        private static IDictionary<string, ISet<string>> SyntheticBaseline()
        {
            Dictionary<string, ISet<string>> fake = new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < Baselines.Length; i++)
            {
                DictBaseline b = Baselines[i];
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                for (int n = 0; n < b.VanillaCount; n++)
                    ids.Add("VANILLA_" + b.Dictionary + "_" + n.ToString(CultureInfo.InvariantCulture));
                fake[b.Dictionary] = ids;
            }
            for (int p = 0; p < Expected.Length; p++)
            {
                ExpectedPack pack = Expected[p];
                ISet<string> ids = fake[pack.Dictionary];
                for (int n = 0; n < pack.RecordedCount; n++)
                    ids.Add(pack.Prefix + "SYNTH_" + n.ToString(CultureInfo.InvariantCulture));
            }
            return fake;
        }

        private static void RemovePrefix(IDictionary<string, ISet<string>> snapshot, string dictName, string prefix)
        {
            ISet<string> ids;
            if (!snapshot.TryGetValue(dictName, out ids) || ids == null) return;
            List<string> doomed = new List<string>();
            foreach (string id in ids)
                if (id.StartsWith(prefix, StringComparison.Ordinal)) doomed.Add(id);
            for (int i = 0; i < doomed.Count; i++) ids.Remove(doomed[i]);
        }
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

Immediately after the "Game vocabulary" section:

```csharp
            InstallProvenance.Register(runner, data);
```

- [ ] **Step 3: Run and verify**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: `checks: 11  passed: 11  failed: 0`, exit 0 (2 from Task 1, 2 from Task 2, 7 here: 3 real + 4 negative controls). No warnings.

Read the outcomes honestly:

- **"every expected content pack is present" FAILS** → the co-op content has been removed from the install. Do **not** re-run a Steam verify; that is the likeliest cause. Report to the owner and restore from `C:\Users\ben\Backups\ftk2-2026-08-23\`.
- **"every live id is accounted for" FAILS with a surplus** → a mod nobody recorded is writing into `StreamingAssets`. Identify it (`git`-free: list `Configs\JSON~\Things\*.json` and `Characters.json` prefixes), then add it to `InstallProvenance.Expected` with its measured count and record it in the README baseline table. Adding it is the *correct* fix; deleting the check is not.
- **"every live id is accounted for" FAILS with a shortfall** → vanilla content is missing. That is install damage, and it is the one case where a Steam repair is genuinely indicated — but only after the owner has been told, because the mod content must be reinstalled afterwards.
- **"no repo-authored id is written into StreamingAssets" FAILS** → a deploy step baked a runtime pack onto disk. Every adds-only result below is void until it is removed.

- [ ] **Step 4: Prove the controls actually control**

Temporarily change `SyntheticBaseline` to return an empty dictionary, re-run, and confirm the three NEGATIVE cases now **FAIL** (they can no longer find the offenders they inject). Restore it and re-run to green before committing. This is the cheapest available demonstration that the negative controls are not themselves vacuous.

- [ ] **Step 5: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T3: expected-content provenance gate for the EOR/Armory install baseline"
```

---

### Task 4: Pack roots, the authored id universe, and adds-only enforcement against live ids

`ClassForge.Core.PackLoader.Load` already accepts a `LiveIdSets` argument, and its own XML doc names `ClassForge.PackCheck` as the caller that has to pass `null` "because it has no live `Configs` to snapshot". This task is that missing caller.

It also builds `AuthoredContent`, the shared id universe every later check reads. That exists because of **correction 2**: `MergePlan` carries no recipe ids, so `Passives` cannot be resolved without a separate `RecipeParser` pass. Building it once, here, is what stops Tasks 5 and 10 from disagreeing about what "authored" means.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/PackRoots.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/AuthoredContent.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/AddsOnlyChecks.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/collide/CF_PACK_HARNESS_COLLIDE/pack.json`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/collide/CF_PACK_HARNESS_COLLIDE/classes.json`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData.Ids` (Task 1); `ClassForge.Core.PackLoader/LiveIdSets/PackLoadResult/MergeOp/Finding`; `ClassForge.Core.IO.FileSystemFileSource`; `ClassForge.Recipes.Parsing.RecipeParser/RecipeValidator`; `ClassForge.Recipes.Generation.ModifierRecipeGenerator`.
- Produces:
  - `static class PackRoots` — `static string RepoRoot()`, `static string[] ClassPackRoots()`, `static string FollowerPackRoot()`, `static string FixturesDir()`.
  - `sealed class AuthoredContent` — `static AuthoredContent Load(GameData data)`, `static AuthoredContent LoadFrom(GameData data, string[] roots)`, `static IEnumerable<string> ErrorFindings(AuthoredContent content)`; get-only members `PackLoadResult Packs`, `IDictionary<string, ClassForge.Recipes.Model.RecipeSet> RecipeSetsByPackId`, `ISet<string> AuthoredRecipeIds`, `ISet<string> GeneratedRecipeIds`, `ISet<string> AllRecipeIds`, `ISet<string> PackIds`.
  - `static class Checks.AddsOnlyChecks` — `static void Register(CheckRunner runner, GameData data, AuthoredContent content)`.

- [ ] **Step 1: Write `PackRoots.cs`**

```csharp
using System.Collections.Generic;
using System.IO;

namespace LiveDataHarness
{
    /// <summary>Resolves the repo's shipped pack directories by walking up from the executable — the same
    /// trick ClassForge.PackCheck's FindDefaultPack uses. Read-only; nothing here writes.</summary>
    public static class PackRoots
    {
        /// <summary>Repo root, or null when the harness has been copied out of the repo.</summary>
        public static string RepoRoot()
        {
            string dir = System.AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "FTK2.ClassForge", "data", "ClassPacks")))
                    return dir;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            return null;
        }

        /// <summary>Every root a ClassForge PackLoader pass should scan. Ordinal-sorted for AC5.</summary>
        public static string[] ClassPackRoots()
        {
            string repo = RepoRoot();
            if (repo == null) return new string[0];

            List<string> roots = new List<string>();
            string cf = Path.Combine(repo, "FTK2.ClassForge", "data", "ClassPacks");
            string bl = Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks");
            if (Directory.Exists(cf)) roots.Add(cf);
            if (Directory.Exists(bl)) roots.Add(bl);
            roots.Sort(System.StringComparer.Ordinal);
            return roots.ToArray();
        }

        public static string FollowerPackRoot()
        {
            string repo = RepoRoot();
            if (repo == null) return null;
            string p = Path.Combine(repo, "FTK2.Summoner", "data", "FollowerPacks");
            return Directory.Exists(p) ? p : null;
        }

        /// <summary>Deliberately-broken packs, copied next to the executable by the csproj.</summary>
        public static string FixturesDir()
        {
            return Path.Combine(System.AppContext.BaseDirectory, "fixtures");
        }
    }
}
```

- [ ] **Step 2: Write `AuthoredContent.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Parsing;

namespace LiveDataHarness
{
    /// <summary>
    /// One pass over every shipped pack, producing the id universe the rest of the harness shares.
    ///
    /// Why the separate recipe pass: ClassForge.Core's ParsedPack/MergePlan carry Classes, Traits, Items,
    /// Abilities, Statuses, Localization, Icons, Portraits and ModifierTables — and NO recipe collection.
    /// The 60 authored SKILL_CF_*/SKILL_BLSS_* recipes exist only in each pack's skillrecipes.json.
    /// Measured 2026-08-23: resolving class/trait Passives against SkillConfigs + MergePlan alone produces
    /// 56 false "dangling" findings (40 class, 16 trait). Building AuthoredRecipeIds here is what removes them.
    ///
    /// GeneratedRecipeIds covers the pair ModifierRecipeGenerator synthesizes per modifiers.json table
    /// (SKILL_CF_ENCMOD_SELECT and its derived SKILL_CF_ENCMOD_APPLY sibling for the shipped table). They are
    /// authored nowhere, so a checker that did not know about them would report the selection id as dangling.
    /// They are DERIVED via the generator's own public helper, never hard-coded.
    /// </summary>
    public sealed class AuthoredContent
    {
        public PackLoadResult Packs { get; private set; }
        public IDictionary<string, ClassForge.Recipes.Model.RecipeSet> RecipeSetsByPackId { get; private set; }
        public ISet<string> AuthoredRecipeIds { get; private set; }
        public ISet<string> GeneratedRecipeIds { get; private set; }
        public ISet<string> AllRecipeIds { get; private set; }
        public ISet<string> PackIds { get; private set; }

        /// <summary>Every shipped ClassPack root, with the LIVE id sets handed to PackLoader — the argument
        /// ClassForge.PackCheck is documented as unable to supply.</summary>
        public static AuthoredContent Load(GameData data)
        {
            return LoadFrom(data, PackRoots.ClassPackRoots());
        }

        /// <summary>Same call shape pointed at an arbitrary root list — used by the fixtures.</summary>
        public static AuthoredContent LoadFrom(GameData data, string[] roots)
        {
            LiveIdSets liveIds = new LiveIdSets(
                data.Ids("Characters"),
                data.Ids("Things"),
                data.Ids("Abilities"),
                data.Ids("StatusEffects"));

            PackLoadResult result = new PackLoader().Load(new FileSystemFileSource(), roots, null, liveIds);

            Dictionary<string, ClassForge.Recipes.Model.RecipeSet> sets =
                new Dictionary<string, ClassForge.Recipes.Model.RecipeSet>(StringComparer.Ordinal);
            HashSet<string> authored = new HashSet<string>(StringComparer.Ordinal);

            foreach (PackManifest manifest in result.EnabledOrderedPacks)
            {
                string path = Path.Combine(manifest.RootDir, "skillrecipes.json");
                if (!File.Exists(path)) continue;

                ClassForge.Recipes.Model.RecipeSet set = RecipeParser.Parse(File.ReadAllText(path));
                RecipeValidator.Validate(set);
                sets[manifest.Id] = set;

                // Every parsed id counts as authored, including one the validator disabled: a Passives entry
                // pointing at a disabled recipe is a validator finding, not a dangling reference, and
                // reporting it twice under two different names helps nobody.
                foreach (ClassForge.Recipes.Model.SkillRecipe recipe in set.Ordered)
                    authored.Add(recipe.Id);
            }

            HashSet<string> generated = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModifierTable table in result.MergePlan.ModifierTables)
            {
                if (string.IsNullOrEmpty(table.SelectionRecipe)) continue;
                generated.Add(table.SelectionRecipe);
                string applyId = ModifierRecipeGenerator.DeriveApplyId(table.SelectionRecipe);
                if (!string.IsNullOrEmpty(applyId)) generated.Add(applyId);
            }

            HashSet<string> all = new HashSet<string>(authored, StringComparer.Ordinal);
            all.UnionWith(generated);

            AuthoredContent content = new AuthoredContent();
            content.Packs = result;
            content.RecipeSetsByPackId = sets;
            content.AuthoredRecipeIds = authored;
            content.GeneratedRecipeIds = generated;
            content.AllRecipeIds = all;
            content.PackIds = new HashSet<string>(
                result.EnabledOrderedPacks.Select(delegate (PackManifest m) { return m.Id; }),
                StringComparer.Ordinal);
            return content;
        }

        /// <summary>Error-severity findings from the ClassForge load, as one line each.</summary>
        public static IEnumerable<string> ErrorFindings(AuthoredContent content)
        {
            return content.Packs.Findings
                .Where(delegate (Finding f) { return f.Severity == FindingSeverity.Error; })
                .Select(delegate (Finding f) { return f.Code + " [" + (f.PackId ?? "-") + "]: " + f.Message; });
        }
    }
}
```

- [ ] **Step 3: Write the collide fixture**

Manifest keys are **lowercase-camel** — verified against `ClassForge.Core.ManifestParser`, which reads `id`, `name`, `version`, `author`, `description`, `loadOrder`, `dependencies`, `enabled`. Getting the casing wrong yields a pack that silently fails to discover, which would make the fixture vacuous. The id starts with `CF_PACK_` so it does not trip `ManifestParser`'s `CF_PACK_ID_CONVENTION` warning and add noise.

`fixtures/collide/CF_PACK_HARNESS_COLLIDE/pack.json`:

```json
{
  "author": "ftk2mods",
  "dependencies": [],
  "description": "Harness negative fixture: deliberately ships a class id that already exists in live Configs.Characters, to prove the adds-only live-id check bites. Never deployed, never referenced by any pack root.",
  "enabled": true,
  "id": "CF_PACK_HARNESS_COLLIDE",
  "loadOrder": 900,
  "name": "Harness collide fixture",
  "version": "1.0.0"
}
```

`fixtures/collide/CF_PACK_HARNESS_COLLIDE/classes.json` — `ALCHEMIST` is verified present in live `Configs.Characters` (its `en` display name is `"Alchemist"`, measured 2026-08-23):

```json
{
  "ALCHEMIST": {
    "BaseType": "HUMAN",
    "DefaultBodyType": "M",
    "Expansion": "BASE",
    "Level": -1,
    "LocKey": "ALCHEMIST",
    "Passives": [],
    "Rarity": "COMMON",
    "Stats": { "HP": 30 },
    "Tags": [ "PLAYER" ],
    "Things": {},
    "Threat": 0
  }
}
```

- [ ] **Step 4: Write `Checks/AddsOnlyChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Packs are adds-only: no pack id may collide with an id the live game already defines. MergePlanner
    /// enforces this itself when handed a LiveIdSets (emitting CF_LIVE_ID_COLLISION and dropping the id);
    /// this check both asserts that no such finding fired AND re-derives the collision set directly, so a
    /// regression in MergePlanner cannot silently make the check pass.
    /// </summary>
    public static class AddsOnlyChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            runner.Section("ClassForge — pack load against live Configs");

            runner.Case("all four shipped packs load with zero Error findings", delegate
            {
                // Measured 2026-08-23: CF_PACK_EOR_CLASSES, CF_PACK_BALDURS, CF_PACK_ENCOUNTER_MODIFIERS and
                // BLSS_PACK_EOR_BLESSINGS all carry "enabled": true. isPackEnabled is null here, so each
                // manifest's own flag is authoritative. Parking a pack is a legitimate change — update this
                // number and the README when it happens, do not soften it to a floor.
                Check.Exactly(4, content.Packs.EnabledOrderedPacks.Count, "packs merged");
                Check.Empty(AuthoredContent.ErrorFindings(content), "ClassForge PackLoader Error findings");
            });

            runner.Case("the merge plan is non-empty (the checks below are not vacuous)", delegate
            {
                // class-test-matrix.md: 35 classes (31 EOR + 4 Baldur's) and 45 Things (41 traits + 4 items).
                Check.Exactly(35, content.Packs.MergePlan.Characters.Count, "merged Characters");
                Check.Exactly(45, content.Packs.MergePlan.Things.Count, "merged Things");
                Check.Exactly(10, content.Packs.MergePlan.StatusEffects.Count, "merged StatusEffects");
                Check.Exactly(13, content.Packs.MergePlan.Abilities.Count, "merged Abilities");
            });

            runner.Case("no pack id collides with a live vanilla id", delegate
            {
                List<string> offenders = new List<string>();
                AddCollisions(offenders, data, "Characters", content.Packs.MergePlan.Characters);
                AddCollisions(offenders, data, "Things", content.Packs.MergePlan.Things);
                AddCollisions(offenders, data, "Abilities", content.Packs.MergePlan.Abilities);
                AddCollisions(offenders, data, "StatusEffects", content.Packs.MergePlan.StatusEffects);
                Check.Empty(offenders, "pack ids colliding with live Configs ids");
            });

            runner.Case("NEGATIVE fixture: a vanilla-colliding pack IS refused", delegate
            {
                string fixtureRoot = Path.Combine(PackRoots.FixturesDir(), "collide");
                Check.True(Directory.Exists(fixtureRoot),
                    "fixture root must exist at " + fixtureRoot + " — check the csproj's fixtures\\**\\* copy rule");

                AuthoredContent bad = AuthoredContent.LoadFrom(data, new string[] { fixtureRoot });
                List<string> findings = AuthoredContent.ErrorFindings(bad).ToList();

                Check.True(findings.Any(delegate (string f) { return f.Contains("CF_LIVE_ID_COLLISION"); }),
                    "fixture CF_PACK_HARNESS_COLLIDE (ships live id ALCHEMIST) must produce a " +
                    "CF_LIVE_ID_COLLISION Error — if this passes silently, adds-only enforcement is not " +
                    "actually running. Got: " + (findings.Count == 0 ? "<no errors>" : string.Join(" | ", findings)));

                Check.Exactly(0, bad.Packs.MergePlan.Characters.Count,
                    "the colliding id must be DROPPED from the merge plan, not merely reported");
            });

            runner.Case("NEGATIVE control: the fixture loader is not simply always-failing", delegate
            {
                // Without this, the case above would still pass if LoadFrom errored on every input.
                AuthoredContent good = AuthoredContent.LoadFrom(data, PackRoots.ClassPackRoots());
                Check.Empty(AuthoredContent.ErrorFindings(good),
                    "the same LoadFrom call over the real pack roots must produce zero Errors");
            });
        }

        private static void AddCollisions(List<string> offenders, GameData data, string dictName, List<MergeOp> ops)
        {
            ISet<string> live = data.Ids(dictName);
            for (int i = 0; i < ops.Count; i++)
                if (live.Contains(ops[i].Id))
                    offenders.Add(dictName + "." + ops[i].Id + " (from pack " + ops[i].SourcePackId +
                                  ") already exists in the live Configs");
        }
    }
}
```

- [ ] **Step 5: Wire into `Program.cs`**

After `InstallProvenance.Register(runner, data);`:

```csharp
            AuthoredContent authored = AuthoredContent.Load(data);
            Checks.AddsOnlyChecks.Register(runner, data, authored);
```

- [ ] **Step 6: Prove the fixture bites before trusting the positive result**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: all five new cases PASS (`checks: 16  passed: 16  failed: 0`). The fourth is the meaningful one — it passes only because the fixture *did* produce a `CF_LIVE_ID_COLLISION`.

Now invert it. Change the fixture's `classes.json` key from `ALCHEMIST` to `CF_HARNESS_NOT_A_REAL_CLASS`, re-run, and confirm the **NEGATIVE fixture** case now **FAILS** (no collision finding, and the merge plan has 1 character rather than 0). Restore `ALCHEMIST`, re-run to green, and only then commit. A fixture that has never been observed failing is decoration.

- [ ] **Step 7: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T4: authored id universe and adds-only enforcement against live Configs"
```

---

### Task 5: Reference integrity

The check that would have caught the 2026-08-04 EOR upstream delta silently breaking an ability id. It carries both measured corrections: `Passives` resolve against `Configs.SkillConfigs` (not `Abilities`) **and** against the packs' own authored recipe ids (which are not in `MergePlan`).

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/ReferenceChecks.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/dangling/CF_PACK_HARNESS_DANGLING/pack.json`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/dangling/CF_PACK_HARNESS_DANGLING/classes.json`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData` (T1), `GameVocabulary` (T2), `AuthoredContent` (T4), `ClassForge.Core.Json.JsonValue`.
- Produces: `static class Checks.ReferenceChecks` — `static IEnumerable<string> Scan(GameData data, GameVocabulary vocab, AuthoredContent content)`, `static void Register(CheckRunner runner, GameData data, GameVocabulary vocab, AuthoredContent content)`, `static readonly string[] AuthoredTags`.

The field map below is the measured ground truth. Each row says: *for this field, a value is valid when it is in this set.*

| Merge category | Field | Kind | Valid when in… |
|---|---|---|---|
| `Characters` | `Passives` (array) | content id | `Configs.SkillConfigs` ∪ `AllRecipeIds` ∪ merged `Abilities` ∪ merged `StatusEffects` |
| `Characters` | `Things` (object keys) | content id | `Configs.Things` ∪ merged `Things` |
| `Characters` | `BaseType` | closed vocab | `vocab.BaseTypes` |
| `Characters` | `DefaultBodyType` | closed vocab | `vocab.BodyTypes` |
| `Characters` | `Rarity` | enum | `vocab.Rarities` ∪ `vocab.EnumMembers` |
| `Characters` | `Expansion` | enum | `vocab.Expansions` ∪ `vocab.EnumMembers` |
| `Characters` | `Tags` (array) | mixed | `vocab.CharacterTags` ∪ `vocab.EnumMembers` ∪ `content.PackIds` ∪ `AuthoredTags` |
| `Things` | `Equippable.Passives` (array) | content id | same as character `Passives` |
| `Things` | `Interactable.Abilities` (object keys) | content id | `Configs.Abilities` ∪ merged `Abilities` |
| `Things` | `Class`, `Rarity`, `Material`, `ConsumableType` | closed vocab | the matching learned vocab ∪ `vocab.EnumMembers` |
| `Things` | `Tags` (array) | mixed | `vocab.ThingTags` ∪ `vocab.EnumMembers` ∪ `content.PackIds` ∪ `AuthoredTags` |
| `StatusEffects` | `Passives` (array) | content id | same as character `Passives` |
| `StatusEffects` | `Type` | closed vocab | `vocab.StatusTypes` ∪ `vocab.EnumMembers` |
| *(any)* | `LootID`, `CampQuery`, `SwarmQuery`, `OnDeathAbility` | optional id | **out of scope** — measured empty/absent across all shipped packs, so a rule for them would be speculative and could only produce false positives. Add them when a pack actually uses one. |

- [ ] **Step 1: Write `Checks/ReferenceChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.Json;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Cross-references every id a shipped pack points at, against the LIVE game data plus the packs' own
    /// merged and authored ids. Two corrections are baked in and must not be undone:
    ///
    ///   (1) CharacterConfig.Passives resolve against Configs.SkillConfigs (68 entries), NOT Configs.Abilities.
    ///       Checking Abilities produced 100 false "dangling" findings (2026-08-08).
    ///   (2) Passives must ALSO resolve against the packs' own skillrecipes.json ids, which are absent from
    ///       ClassForge's MergePlan entirely. Omitting them produced 56 false findings (2026-08-23).
    /// </summary>
    public static class ReferenceChecks
    {
        /// <summary>Tags authored by this repo's packs (or owned by the engine) that are absent from every
        /// learned live tag vocabulary. Measured 2026-08-23: pack tags not in the live vocabularies are
        /// exactly the three loaded pack ids plus these three. TRAIT and LOADOUT_0 are very likely FTK2.dll
        /// enum members and therefore already covered — the "redundant allowlist entries" warning below
        /// reports any entry that turns out to be, so this list shrinks on evidence rather than guesswork.</summary>
        public static readonly string[] AuthoredTags = { "CF_SUMMON", "TRAIT", "LOADOUT_0" };

        public static void Register(CheckRunner runner, GameData data, GameVocabulary vocab, AuthoredContent content)
        {
            runner.Section("ClassForge — reference integrity vs live Configs");

            runner.Case("every pack reference resolves", delegate
            {
                Check.AtLeast(35, content.Packs.MergePlan.Characters.Count,
                    "merged Characters inspected (a zero-subject scan is a vacuous pass)");
                Check.Empty(Scan(data, vocab, content), "dangling references in shipped pack content");
            });

            runner.Case("NEGATIVE fixture: a dangling Passive IS caught", delegate
            {
                string fixtureRoot = Path.Combine(PackRoots.FixturesDir(), "dangling");
                Check.True(Directory.Exists(fixtureRoot), "fixture root must exist at " + fixtureRoot);

                AuthoredContent bad = AuthoredContent.LoadFrom(data, new string[] { fixtureRoot });
                List<string> offenders = Scan(data, vocab, bad).ToList();
                Check.True(offenders.Any(delegate (string o) { return o.Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST"); }),
                    "fixture CF_PACK_HARNESS_DANGLING must be reported as dangling; got: " +
                    (offenders.Count == 0 ? "<nothing>" : string.Join(" | ", offenders)));
            });

            runner.Case("NEGATIVE control: the resolvable sets reject an invented id", delegate
            {
                Check.True(!data.Ids("SkillConfigs").Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST"),
                    "live SkillConfigs must not contain the fixture's invented id");
                Check.True(!content.AllRecipeIds.Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST"),
                    "the authored recipe universe must not contain the fixture's invented id");
                Check.AtLeast(60, content.AllRecipeIds.Count,
                    "authored + generated recipe ids (60 authored + 2 generated measured) — a near-empty set " +
                    "would make correction 2 silently ineffective and flood the run with false findings");
            });

            runner.Warn("redundant AuthoredTags entries (already covered by the enum vocabulary)",
                AuthoredTags.Where(delegate (string t) { return vocab.EnumMembers.Contains(t); })
                            .Select(delegate (string t) { return t + " is an FTK2.dll enum member — remove it from ReferenceChecks.AuthoredTags"; }));
        }

        /// <summary>One human-readable offender line per unresolved reference. Pure over its inputs.</summary>
        public static IEnumerable<string> Scan(GameData data, GameVocabulary vocab, AuthoredContent content)
        {
            MergePlan plan = content.Packs.MergePlan;

            HashSet<string> mergedThings = IdSet(plan.Things);
            HashSet<string> mergedAbilities = IdSet(plan.Abilities);
            HashSet<string> mergedStatuses = IdSet(plan.StatusEffects);

            ISet<string> liveSkills = data.Ids("SkillConfigs");
            ISet<string> liveThings = data.Ids("Things");
            ISet<string> liveAbilities = data.Ids("Abilities");

            // The one set both corrections live in.
            HashSet<string> resolvableSkills = new HashSet<string>(liveSkills, StringComparer.Ordinal);
            resolvableSkills.UnionWith(content.AllRecipeIds);
            resolvableSkills.UnionWith(mergedAbilities);
            resolvableSkills.UnionWith(mergedStatuses);

            HashSet<string> resolvableThings = new HashSet<string>(liveThings, StringComparer.Ordinal);
            resolvableThings.UnionWith(mergedThings);

            HashSet<string> resolvableAbilities = new HashSet<string>(liveAbilities, StringComparer.Ordinal);
            resolvableAbilities.UnionWith(mergedAbilities);

            HashSet<string> authoredTags = new HashSet<string>(AuthoredTags, StringComparer.Ordinal);

            List<string> offenders = new List<string>();

            foreach (MergeOp op in plan.Characters)
            {
                foreach (string passive in op.Value.GetStringArray("Passives"))
                    if (!resolvableSkills.Contains(passive))
                        offenders.Add(op.Id + ".Passives -> " + passive +
                                      " (not in Configs.SkillConfigs, the packs' authored/generated recipes, " +
                                      "or merged Abilities/StatusEffects)");

                foreach (KeyValuePair<string, JsonValue> member in op.Value.Get("Things").AsObjectMembers)
                    if (!resolvableThings.Contains(member.Key))
                        offenders.Add(op.Id + ".Things -> " + member.Key + " (not in Configs.Things nor merged Things)");

                CheckVocab(offenders, op.Id, "BaseType", op.Value.GetString("BaseType"), vocab.BaseTypes, null);
                CheckVocab(offenders, op.Id, "DefaultBodyType", op.Value.GetString("DefaultBodyType"), vocab.BodyTypes, null);
                CheckVocab(offenders, op.Id, "Rarity", op.Value.GetString("Rarity"), vocab.Rarities, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "Expansion", op.Value.GetString("Expansion"), vocab.Expansions, vocab.EnumMembers);
                CheckTags(offenders, op.Id, op.Value.GetStringArray("Tags"), vocab.CharacterTags, vocab.EnumMembers, content.PackIds, authoredTags);
            }

            foreach (MergeOp op in plan.Things)
            {
                JsonValue equippable = op.Value.Get("Equippable");
                if (!equippable.IsNull)
                    foreach (string passive in equippable.GetStringArray("Passives"))
                        if (!resolvableSkills.Contains(passive))
                            offenders.Add(op.Id + ".Equippable.Passives -> " + passive +
                                          " (not in Configs.SkillConfigs, the packs' authored/generated recipes, " +
                                          "or merged Abilities/StatusEffects)");

                JsonValue interactable = op.Value.Get("Interactable");
                if (!interactable.IsNull)
                    foreach (KeyValuePair<string, JsonValue> ability in interactable.Get("Abilities").AsObjectMembers)
                        if (!resolvableAbilities.Contains(ability.Key))
                            offenders.Add(op.Id + ".Interactable.Abilities -> " + ability.Key +
                                          " (not in Configs.Abilities nor merged Abilities)");

                CheckVocab(offenders, op.Id, "Class", op.Value.GetString("Class"), vocab.ThingClasses, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "Rarity", op.Value.GetString("Rarity"), vocab.Rarities, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "Material", op.Value.GetString("Material"), vocab.Materials, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "ConsumableType", op.Value.GetString("ConsumableType"), vocab.ConsumableTypes, vocab.EnumMembers);
                CheckTags(offenders, op.Id, op.Value.GetStringArray("Tags"), vocab.ThingTags, vocab.EnumMembers, content.PackIds, authoredTags);
            }

            foreach (MergeOp op in plan.StatusEffects)
            {
                foreach (string passive in op.Value.GetStringArray("Passives"))
                    if (!resolvableSkills.Contains(passive))
                        offenders.Add(op.Id + ".Passives -> " + passive +
                                      " (not in Configs.SkillConfigs, the packs' authored/generated recipes, " +
                                      "or merged Abilities/StatusEffects)");

                CheckVocab(offenders, op.Id, "Type", op.Value.GetString("Type"), vocab.StatusTypes, vocab.EnumMembers);
            }

            offenders.Sort(StringComparer.Ordinal);
            return offenders;
        }

        private static HashSet<string> IdSet(List<MergeOp> ops)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ops.Count; i++) set.Add(ops[i].Id);
            return set;
        }

        /// <summary>An absent or empty value is always fine — these fields are optional in the schema and
        /// vanilla itself leaves several of them blank.</summary>
        private static void CheckVocab(List<string> offenders, string id, string field, string value,
                                       ISet<string> primary, ISet<string> fallback)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (primary != null && primary.Contains(value)) return;
            if (fallback != null && fallback.Contains(value)) return;
            offenders.Add(id + "." + field + " -> " + value + " (not a value the live game uses for this field)");
        }

        private static void CheckTags(List<string> offenders, string id, List<string> tags,
                                      ISet<string> liveTags, ISet<string> enums, ISet<string> packIds, ISet<string> authoredTags)
        {
            if (tags == null) return;
            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                if (string.IsNullOrEmpty(tag)) continue;
                if (liveTags.Contains(tag) || enums.Contains(tag) || packIds.Contains(tag) || authoredTags.Contains(tag)) continue;
                offenders.Add(id + ".Tags -> " + tag +
                              " (not a live tag, an FTK2 enum member, a loaded pack id, or a ReferenceChecks.AuthoredTags entry)");
            }
        }
    }
}
```

- [ ] **Step 2: Write the dangling fixture**

`fixtures/dangling/CF_PACK_HARNESS_DANGLING/pack.json`:

```json
{
  "author": "ftk2mods",
  "dependencies": [],
  "description": "Harness negative fixture: deliberately ships a class whose Passives point at a recipe id that exists nowhere, to prove the reference checker bites. Never deployed, never referenced by any pack root.",
  "enabled": true,
  "id": "CF_PACK_HARNESS_DANGLING",
  "loadOrder": 901,
  "name": "Harness dangling fixture",
  "version": "1.0.0"
}
```

`fixtures/dangling/CF_PACK_HARNESS_DANGLING/classes.json` — note there is deliberately **no** `skillrecipes.json` in this fixture, so `AuthoredRecipeIds` cannot accidentally satisfy the reference:

```json
{
  "CF_HARNESS_DANGLING_CLASS": {
    "BaseType": "HUMAN",
    "DefaultBodyType": "M",
    "Expansion": "BASE",
    "Level": -1,
    "LocKey": "CF_HARNESS_DANGLING_CLASS",
    "Passives": [ "SKILL_CF_HARNESS_DOES_NOT_EXIST" ],
    "Rarity": "COMMON",
    "Stats": { "HP": 30 },
    "Tags": [ "PLAYER", "CF_PACK_HARNESS_DANGLING" ],
    "Things": {},
    "Threat": 0
  }
}
```

- [ ] **Step 3: Wire into `Program.cs`**

```csharp
            Checks.ReferenceChecks.Register(runner, data, vocab, authored);
```

- [ ] **Step 4: Run and interpret honestly**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: 3 PASS, `checks: 19  passed: 19  failed: 0`. The pre-flight simulation (2026-08-23) resolved every reference in all four shipped packs with these exact rules, so a failure here is new information.

If "every pack reference resolves" reports offenders:
1. Pick one and verify it by hand against live data (`grep` the id under `<game>\For The King II_Data\StreamingAssets\Assets\Configs\JSON~`).
2. If the reference is genuinely broken → **do not weaken the check.** Record the finding in the commit message and leave the check failing; fixing pack content is a follow-up, not this plan's scope.
3. If the *rule* is wrong (a field resolves against a set not in the map above) → fix the map, record the correction in the file's XML doc and in this plan's ground-truth table, and re-run. That is the only legitimate reason to change a rule.

If the `AuthoredTags` warning fires, delete the named entries from the array and re-run — the enum vocabulary already covers them.

- [ ] **Step 5: Prove the fixture bites**

Change the fixture's `Passives` entry from `SKILL_CF_HARNESS_DOES_NOT_EXIST` to `SKILL_BLACKHOLE` (a real `Configs.SkillConfigs` id, verified present), re-run, and confirm the **NEGATIVE fixture** case now **FAILS**. Restore the invented id, re-run to green, then commit.

- [ ] **Step 6: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T5: reference integrity for pack content vs live Configs"
```

---

### Task 6: Localization coverage

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/LocalizationChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData.Lang("en")` (T1), `AuthoredContent.Packs.MergePlan.Localization/Characters/Things/TraitIds` (T4).
- Produces: `static class Checks.LocalizationChecks` — `static void Register(CheckRunner runner, GameData data, AuthoredContent content)`.

**Verified convention (2026-08-23).** A class with config id `ALCHEMIST` needs `en["ALCHEMIST"]` (display name — measured value `"Alchemist"`) and `en["UI_TOOLTIP_ALCHEMIST_DESCRIPTION"]` (tooltip body). Vanilla `CharacterConfig.LocKey` is empty for every `PLAYER`-tagged class, so the **config id itself is the key** — not `LocKey`. The four packs ship 237 + 60 + 21 + 92 = **410** localization keys between them, cover 35/35 classes and 45/45 traits+items, and shadow **zero** vanilla keys. Live `en` has 9707 keys and contains **no** `CF_`/`BLSS_`/`SMN_`/`EOR_`/`ARM_` keys — mod localization is injected at runtime, so `MergePlan.Localization` is the only place pack keys can come from.

- [ ] **Step 1: Write `Checks/LocalizationChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Localization coverage against the live Lang table plus the packs' merged localization. Convention
    /// verified 2026-08-23 against vanilla: the display name is keyed by the config id itself
    /// (en["ALCHEMIST"] == "Alchemist") and the tooltip by "UI_TOOLTIP_&lt;id&gt;_DESCRIPTION".
    /// CharacterConfig.LocKey is empty for every vanilla PLAYER class, so the id — not LocKey — is what
    /// must resolve. Live en.json carries zero mod keys (mod localization is injected at runtime), so a
    /// missing key here means the string will render as its raw id in game.
    /// </summary>
    public static class LocalizationChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            IDictionary<string, string> en = data.Lang("en");
            Dictionary<string, string> merged = content.Packs.MergePlan.Localization;

            runner.Section("Localization coverage");

            runner.Case("the live 'en' table and the merged pack table are both present", delegate
            {
                Check.True(en != null, "Configs.Langs['en'] is available");
                Check.AtLeast(9000, en.Count, "live en key count (9707 measured)");
                Check.AtLeast(400, merged.Count, "merged pack localization keys (410 measured) — a near-empty " +
                                                 "table would make every coverage check below fail loudly, and " +
                                                 "a suspiciously full one would make them pass vacuously");
            });

            runner.Case("every merged class has a display-name and a tooltip key", delegate
            {
                Check.AtLeast(35, content.Packs.MergePlan.Characters.Count, "classes inspected");
                List<string> offenders = new List<string>();
                foreach (MergeOp op in content.Packs.MergePlan.Characters)
                {
                    if (!Has(merged, en, op.Id))
                        offenders.Add("class " + op.Id + ": missing display-name key '" + op.Id + "'");
                    string tip = "UI_TOOLTIP_" + op.Id + "_DESCRIPTION";
                    if (!Has(merged, en, tip))
                        offenders.Add("class " + op.Id + ": missing tooltip key '" + tip + "'");
                }
                Check.Empty(offenders, "classes with missing localization");
            });

            runner.Case("every merged trait and item has a display-name key", delegate
            {
                Check.AtLeast(45, content.Packs.MergePlan.Things.Count, "traits + items inspected");
                List<string> offenders = new List<string>();
                foreach (MergeOp op in content.Packs.MergePlan.Things)
                    if (!Has(merged, en, op.Id))
                        offenders.Add("thing " + op.Id + ": missing display-name key '" + op.Id + "'");
                Check.Empty(offenders, "traits/items with missing localization");
            });

            runner.Case("no pack localization key overwrites a vanilla key", delegate
            {
                List<string> offenders = new List<string>();
                foreach (KeyValuePair<string, string> kv in merged)
                    if (en.ContainsKey(kv.Key))
                        offenders.Add("pack key '" + kv.Key + "' shadows vanilla en['" + kv.Key + "'] = \"" + en[kv.Key] + "\"");
                Check.Empty(offenders, "pack localization keys shadowing vanilla");
            });

            runner.Case("NEGATIVE control: an unshipped key is reported missing", delegate
            {
                // If Has() were ever always-true, all three coverage checks above would pass forever.
                Check.True(!Has(merged, en, "LDH_NEVER_SHIPPED_LOCALIZATION_KEY"),
                    "the localization lookup must return false for a key nobody ships");
                Check.True(Has(merged, en, "ALCHEMIST"),
                    "...and true for a key that does exist (ALCHEMIST is a verified vanilla en key) — " +
                    "an always-false lookup would be equally vacuous in the other direction");
            });
        }

        private static bool Has(IDictionary<string, string> merged, IDictionary<string, string> en, string key)
        {
            if (merged != null && merged.ContainsKey(key)) return true;
            return en != null && en.ContainsKey(key);
        }
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.LocalizationChecks.Register(runner, data, authored);
```

- [ ] **Step 3: Run and verify**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: 5 PASS, `checks: 24  passed: 24  failed: 0`. The pre-flight simulation found 0 missing keys and 0 vanilla collisions across all four packs, so a failure is new information — apply Task 5 Step 4's honest-interpretation procedure.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T6: localization coverage and vanilla-shadowing check"
```

---

### Task 7: Blessings fail-closed roster gate

Reproduces, offline, the gate at `FTK2.Blessings/src/Blessings.Plugin/GrantAnchorPatches.cs` `EnsureRosterResolvesAgainstConfigs`, which disables the whole plugin for a session when a roster `TraitId` does not resolve in `Env.Configs.Things`. Today the only way to learn Blessings self-disabled is to launch the game and read a log line.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/BlessingsChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `Blessings.Core.Parsing.BlessingsRegistryParser.Parse(string)` → `ParseResult`; `GameData.Ids("Things")` (T1); `AuthoredContent.Packs.MergePlan.Things` (T4); `PackRoots.RepoRoot()` (T4).
- Produces: `static class Checks.BlessingsChecks` — `static void Register(CheckRunner runner, GameData data, AuthoredContent content)`.

Note the pack root: `BLSS_PACK_EOR_BLESSINGS` lives under `FTK2.Blessings/data/ClassPacks/`, which **is** one of `PackRoots.ClassPackRoots()`, so its `traits.json` is already merged into `MergePlan.Things` by Task 4. `blessings.json` is not a ClassForge concept and is read directly here.

- [ ] **Step 1: Write `Checks/BlessingsChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blessings.Core.Model;
using Blessings.Core.Parsing;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Offline replica of Blessings' fail-closed data gate
    /// (GrantAnchorPatches.EnsureRosterResolvesAgainstConfigs): every blessing's TraitId must resolve in
    /// Configs.Things, else the plugin disables itself for the whole session and logs one error. In-game
    /// that costs a full launch to discover; here it costs about two seconds.
    ///
    /// The resolvable set is live Configs.Things PLUS the merged pack Things, because the BLSS traits are
    /// added at runtime by ClassForge from the same pack — checking live ids alone would report all 15 as
    /// unresolved, which is precisely the false-negative-shaped false positive this harness exists to avoid.
    /// </summary>
    public static class BlessingsChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            runner.Section("Blessings — fail-closed roster gate");

            string repo = PackRoots.RepoRoot();
            string blessingsJson = repo == null
                ? null
                : Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks", "BLSS_PACK_EOR_BLESSINGS", "blessings.json");

            runner.Case("blessings.json parses without Error findings", delegate
            {
                Check.True(blessingsJson != null && File.Exists(blessingsJson),
                    "blessings.json exists at " + (blessingsJson ?? "<repo root not found>"));

                ParseResult parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "BlessingsRegistryParser succeeded (Registry != null)");
                Check.True(!parsed.HasErrors,
                    "blessings.json has no Error findings; got: " +
                    string.Join(" | ", parsed.Findings.Select(delegate (Blessings.Core.Diagnostics.Finding f) { return f.ToString(); })));
                Check.Exactly(15, parsed.Registry.Blessings.Count, "blessings in the roster (class-test-matrix.md §4.1)");
            });

            runner.Case("every roster TraitId resolves (the plugin will NOT self-disable)", delegate
            {
                ParseResult parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "registry parsed");

                HashSet<string> resolvable = Resolvable(data, content);
                List<string> missing = new List<string>();
                foreach (BlessingEntry b in parsed.Registry.Blessings)
                    if (!resolvable.Contains(b.TraitId))
                        missing.Add("blessing '" + b.Id + "' TraitId '" + b.TraitId + "' unresolved — Blessings would log " +
                                    "'roster TraitId(s) do not resolve in Env.Configs.Things -- this plugin is DISABLED for the session'");

                Check.Empty(missing, "unresolvable Blessings roster TraitIds");
            });

            runner.Case("NEGATIVE control: an invented TraitId would be caught", delegate
            {
                HashSet<string> resolvable = Resolvable(data, content);
                Check.AtLeast(2380, resolvable.Count,
                    "resolvable Things (2380 live + 45 merged measured) — a small set here would make the " +
                    "gate check above fail loudly rather than silently, but a set containing everything " +
                    "would make it vacuous");
                Check.True(!resolvable.Contains("TRAIT_BLSS_HARNESS_NEVER_SHIPPED"),
                    "the resolvable-Things set must not contain an invented id");
            });
        }

        private static HashSet<string> Resolvable(GameData data, AuthoredContent content)
        {
            HashSet<string> resolvable = new HashSet<string>(data.Ids("Things"), StringComparer.Ordinal);
            foreach (MergeOp op in content.Packs.MergePlan.Things) resolvable.Add(op.Id);
            return resolvable;
        }
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.BlessingsChecks.Register(runner, data, authored);
```

- [ ] **Step 3: Run and verify**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: 3 PASS, `checks: 27  passed: 27  failed: 0`. The pre-flight simulation resolved 15/15 roster `TraitId`s. Per the Wave-2 handoff this gate is *live-untested*, so a failure here is a genuine and valuable discovery — report it in the commit message rather than relaxing the check.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T7: offline replica of the Blessings fail-closed roster gate"
```

---

### Task 8: Summoner follower packs vs live Configs

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Io/HarnessPackSource.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/SummonerChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `Summoner.Core.Packs.IPackFileSource`, `IJsonCodec`, `PackLoader.Load`; `Summoner.Core.Merge.MergePlanner.Plan`; `GameData.Ids` (T1); `PackRoots.FollowerPackRoot()` (T4).
- Produces: `sealed class Io.HarnessPackSource : IPackFileSource`, `sealed class Io.HarnessJsonCodec : IJsonCodec`, `static class Checks.SummonerChecks` — `static void Register(CheckRunner runner, GameData data)`.

`Summoner.Plugin/Adapters/FileSystemPackSource.cs` and `GameJsonCodec.cs` exist but are net472/BepInEx-coupled, and `Summoner.Core.Tests`' equivalents live in an `Exe` project. The harness needs its own two small implementations — that is why they are in this task rather than reused.

- [ ] **Step 1: Read the existing implementations before writing new ones**

```bash
cat FTK2.Summoner/src/Summoner.Plugin/Adapters/FileSystemPackSource.cs \
    FTK2.Summoner/src/Summoner.Core.Tests/TestFileSystemPackSource.cs \
    FTK2.Summoner/src/Summoner.Core.Tests/TestJsonCodec.cs 2>/dev/null
```

Mirror their semantics exactly. `IPackFileSource`'s own XML doc settles the contract: `ListPackDirectories` returns **opaque identifiers**, the real adapter returns absolute directory paths, and every other method joins sub-paths onto them and treats the result as opaque. So returning `Directory.GetDirectories(root)` is correct. A mismatch here silently produces "0 packs loaded", which would make this whole task vacuously green — Step 4 asserts against exactly that.

- [ ] **Step 2: Write `Io/HarnessPackSource.cs`**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Summoner.Core.Packs;

namespace LiveDataHarness.Io
{
    /// <summary>IPackFileSource over the real filesystem, mirroring
    /// Summoner.Plugin/Adapters/FileSystemPackSource without the BepInEx coupling. Read-only.</summary>
    public sealed class HarnessPackSource : IPackFileSource
    {
        public IEnumerable<string> ListPackDirectories(string root)
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0];
        }

        public bool Exists(string path) { return File.Exists(path) || Directory.Exists(path); }

        public byte[] ReadAllBytes(string path) { return File.ReadAllBytes(path); }

        public IEnumerable<string> ListFilesRecursive(string packDir)
        {
            return Directory.Exists(packDir)
                ? Directory.GetFiles(packDir, "*", SearchOption.AllDirectories)
                : new string[0];
        }
    }

    /// <summary>
    /// IJsonCodec over System.Text.Json (BCL, no NuGet). PropertyNameCaseInsensitive is MANDATORY:
    /// Summoner.Core.Packs.PackManifest declares PascalCase properties (Id, LoadOrder, Dependencies) while
    /// every shipped pack.json uses lowercase-camel keys. Without it every manifest deserializes with a
    /// null Id and PackLoader skips the pack as "manifest_invalid" — 0 packs loaded, all checks vacuous.
    /// </summary>
    public sealed class HarnessJsonCodec : IJsonCodec
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public T Deserialize<T>(string json) { return JsonSerializer.Deserialize<T>(json, Options); }
    }
}
```

- [ ] **Step 3: Write `Checks/SummonerChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using LiveDataHarness.Io;
using Summoner.Core.Merge;
using Summoner.Core.Packs;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Summoner is adds-only: a follower pack entry whose id already exists in the live Configs must be
    /// skipped, never silently overwritten. Measured 2026-08-23: Configs.Followers = 48 with zero SMN_ ids,
    /// Configs.Characters = 2126; the two shipped packs contribute 100 + 100 = 200 followers and zero
    /// characters (neither pack ships a characters.json), so CharacterAdds is legitimately 0.
    /// </summary>
    public static class SummonerChecks
    {
        public static void Register(CheckRunner runner, GameData data)
        {
            runner.Section("Summoner — follower packs vs live Configs");

            string root = PackRoots.FollowerPackRoot();

            runner.Case("both follower packs load", delegate
            {
                Check.True(root != null, "FTK2.Summoner/data/FollowerPacks exists");
                LoadResult loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                Check.Exactly(2, loaded.Packs.Count,
                    "follower packs loaded (SMN_PACK_EOR_MERCS + SMN_PACK_EOR_PETS). Zero here means the " +
                    "IJsonCodec dropped the camelCase manifests — see HarnessJsonCodec's remarks.");
                Check.Empty(loaded.Findings
                        .Where(delegate (Summoner.Core.Diagnostics.Finding f) { return f.Severity == Summoner.Core.Diagnostics.FindingSeverity.Error; })
                        .Select(delegate (Summoner.Core.Diagnostics.Finding f) { return f.ToString(); }),
                    "Summoner PackLoader Error findings");
            });

            runner.Case("no follower or character id collides with live Configs", delegate
            {
                LoadResult loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                MergePlan plan = MergePlanner.Plan(loaded.Packs, data.Ids("Followers"), data.Ids("Characters"));

                Check.Exactly(200, plan.FollowerAdds.Count,
                    "planned follower adds (100 mercs + 100 pets) — zero adds would make the collision " +
                    "assertion below vacuous");

                // MergePlan.Skips is exactly "entries not added because the id already exists in live
                // Configs" (Summoner design §A2.3), so a non-empty Skips list is a pack shipping content
                // the game already has.
                IEnumerable<string> offenders = plan.Skips
                    .Select(delegate (MergeSkip s) { return "skip " + s.PackId + "/" + s.Id + ": " + s.Reason; })
                    .Concat(plan.Rejects.Select(delegate (Summoner.Core.Diagnostics.Finding f) { return "reject: " + f.ToString(); }));
                Check.Empty(offenders, "Summoner entries refused against live Configs");
            });

            runner.Case("NEGATIVE control: the live Followers set is real and finite", delegate
            {
                Check.AtLeast(40, data.Ids("Followers").Count, "Configs.Followers (48 measured)");
                Check.True(!data.Ids("Followers").Contains("SMN_HARNESS_NEVER_SHIPPED"),
                    "live Followers must not contain an invented id — otherwise the collision check is vacuous");
            });

            runner.Case("NEGATIVE control: a synthetic pre-existing id IS skipped", delegate
            {
                // Prove MergePlanner's skip path actually runs, by telling it one of the packs' own ids is
                // already live. Without this, "0 skips" could mean "adds-only enforcement never fired".
                LoadResult loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                Check.AtLeast(1, loaded.Packs.Count, "at least one pack to draw an id from");

                string victim = loaded.Packs[0].Followers.Keys.OrderBy(delegate (string k) { return k; }, StringComparer.Ordinal).First();
                HashSet<string> pretendLive = new HashSet<string>(data.Ids("Followers"), StringComparer.Ordinal);
                pretendLive.Add(victim);

                MergePlan plan = MergePlanner.Plan(loaded.Packs, pretendLive, data.Ids("Characters"));
                Check.True(plan.Skips.Any(delegate (MergeSkip s) { return string.Equals(s.Id, victim, StringComparison.Ordinal); }),
                    "MergePlanner must skip '" + victim + "' once it is present in the live follower ids");
                Check.Exactly(199, plan.FollowerAdds.Count, "adds after one id is skipped");
            });
        }
    }
}
```

- [ ] **Step 4: Wire into `Program.cs`**

```csharp
            Checks.SummonerChecks.Register(runner, data);
```

- [ ] **Step 5: Run and verify**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: 4 PASS, `checks: 31  passed: 31  failed: 0`. **If "both follower packs load" reports 0, the check family is vacuous** — fix the codec/path semantics (Step 1) before proceeding; do not lower the assertion to a floor of zero.

`FollowerPack.Followers` (used above to pick a victim id) is `Dictionary<string, FollowerEntry>` — verified 2026-08-23 at `FTK2.Summoner/src/Summoner.Core/Packs/FollowerPack.cs:12`, alongside `Manifest`, `Characters`, `Localization`, `Provenance`, `SourcePath`, `Files`.

- [ ] **Step 6: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T8: Summoner follower-pack adds-only check vs live Configs"
```

---

### Task 9: Inventory coverage — the class-test-matrix, enforced

Spec §8 calls S6 "the only component that would have caught the undeployed Baldur's pack". This is that check. It asserts the authoritative counts from `docs/research/class-test-matrix.md`, so a pack silently dropping out of the load — parked, renamed, mis-cased, or failing to discover — turns red instead of quietly shrinking the merge plan.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/InventoryChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `AuthoredContent` (T4), `ClassForge.Core.MergeOp/MergePlan/ModifierTable`.
- Produces: `static class Checks.InventoryChecks` — `static void Register(CheckRunner runner, AuthoredContent content)`.

- [ ] **Step 1: Write `Checks/InventoryChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// The authored inventory, enforced against docs/research/class-test-matrix.md (generated by enumerating
    /// each pack's JSON keys, not eyeballed). These are exact equalities on purpose: a floor would let a pack
    /// vanish from the load without a sound, which is exactly how CF_PACK_BALDURS stayed undeployed and
    /// unnoticed. When content is legitimately added or removed, update the matrix, this file, and the
    /// plan's ground-truth table together — in that order.
    /// </summary>
    public static class InventoryChecks
    {
        public static void Register(CheckRunner runner, AuthoredContent content)
        {
            MergePlan plan = content.Packs.MergePlan;

            runner.Section("Authored inventory (class-test-matrix.md)");

            runner.Case("every expected pack is loaded, by id", delegate
            {
                string[] expected =
                {
                    "BLSS_PACK_EOR_BLESSINGS",
                    "CF_PACK_BALDURS",
                    "CF_PACK_ENCOUNTER_MODIFIERS",
                    "CF_PACK_EOR_CLASSES",
                };
                List<string> loaded = content.PackIds.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal).ToList();
                Check.Eq(string.Join(",", expected), string.Join(",", loaded), "loaded pack ids");
            });

            runner.Case("class, trait, item and status counts match the matrix", delegate
            {
                Check.Exactly(35, plan.Characters.Count, "authored classes (31 EOR + 4 Baldur's)");
                Check.Exactly(41, plan.TraitIds.Count, "authored traits (20 EOR + 6 Baldur's + 15 Blessings)");
                Check.Exactly(45, plan.Things.Count, "authored traits + items (41 + 4)");
                Check.Exactly(10, plan.StatusEffects.Count, "authored statuses (all in CF_PACK_ENCOUNTER_MODIFIERS)");
                Check.Exactly(13, plan.Abilities.Count, "authored item-granted abilities (all in CF_PACK_BALDURS)");
            });

            runner.Case("exactly 34 of the 35 classes are player-selectable", delegate
            {
                // CF_SKELETON_WARRIOR is a summon: Tags ["CF_PACK_BALDURS","CF_SUMMON"], no PLAYER tag.
                // The pack's own description says "Three ... crossover classes", which is right for player
                // classes and wrong for classes.json entries — this check pins which reading the harness uses.
                List<string> players = new List<string>();
                List<string> nonPlayers = new List<string>();
                foreach (MergeOp op in plan.Characters)
                {
                    if (op.Value.GetStringArray("Tags").Contains("PLAYER")) players.Add(op.Id);
                    else nonPlayers.Add(op.Id);
                }
                Check.Exactly(34, players.Count, "PLAYER-tagged authored classes");
                Check.Eq("CF_SKELETON_WARRIOR", string.Join(",", nonPlayers.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal)),
                    "non-PLAYER authored classes");
            });

            runner.Case("recipe and modifier counts match the matrix", delegate
            {
                Check.Exactly(60, content.AuthoredRecipeIds.Count,
                    "authored skill recipes (52 EOR + 5 Baldur's + 2 EncMod + 1 Blessings)");
                Check.Exactly(2, content.GeneratedRecipeIds.Count,
                    "engine-synthesized recipes (SKILL_CF_ENCMOD_SELECT + its derived APPLY sibling)");
                Check.Exactly(1, plan.ModifierTables.Count, "modifier tables");
                Check.Exactly(10, plan.ModifierTables[0].Modifiers.Count, "encounter modifiers");
            });

            runner.Warn("authored recipes defined but consumed by nothing", Unconsumed(content));

            runner.Case("NEGATIVE control: the inventory is derived, not hard-coded", delegate
            {
                // Every count above would also pass if the check compared a constant against itself. Prove
                // the numbers come from the loaded data by asserting a relationship no constant satisfies.
                Check.Exactly(plan.Things.Count, plan.TraitIds.Count + 4,
                    "merged Things must equal traits plus the four CF_PACK_BALDURS items");
                Check.True(content.AllRecipeIds.Count == content.AuthoredRecipeIds.Count + content.GeneratedRecipeIds.Count,
                    "the combined recipe universe must be the union of the authored and generated sets " +
                    "(a non-empty intersection would mean a pack hand-authored an engine-owned id)");
                Check.True(!content.PackIds.Contains("CF_PACK_HARNESS_COLLIDE"),
                    "the fixtures must never be discovered by the real pack roots");
            });
        }

        /// <summary>Authored recipes no class, trait, item or status Passives array points at. Known and
        /// expected: SKILL_CF_BEASTMASTER_PACK_TACTICS and SKILL_CF_AFFIX_OF_SCAVENGING_LOOT
        /// (class-test-matrix.md §1.3). Reported as a Warning, never a failure — an unwired recipe is dead
        /// weight, not a break.</summary>
        private static IEnumerable<string> Unconsumed(AuthoredContent content)
        {
            MergePlan plan = content.Packs.MergePlan;
            HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);

            foreach (MergeOp op in plan.Characters)
                consumed.UnionWith(op.Value.GetStringArray("Passives"));

            foreach (MergeOp op in plan.Things)
            {
                consumed.UnionWith(op.Value.GetStringArray("Passives"));
                ClassForge.Core.Json.JsonValue eq = op.Value.Get("Equippable");
                if (!eq.IsNull) consumed.UnionWith(eq.GetStringArray("Passives"));
            }

            foreach (MergeOp op in plan.StatusEffects)
                consumed.UnionWith(op.Value.GetStringArray("Passives"));

            return content.AuthoredRecipeIds
                .Where(delegate (string id) { return !consumed.Contains(id); })
                .Select(delegate (string id) { return id + " is authored but no Passives array references it"; });
        }
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.InventoryChecks.Register(runner, authored);
```

- [ ] **Step 3: Run and verify**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: 5 PASS, `checks: 36  passed: 36  failed: 0`, plus exactly two warnings naming `SKILL_CF_BEASTMASTER_PACK_TACTICS` and `SKILL_CF_AFFIX_OF_SCAVENGING_LOOT`.

If a count is off by exactly one pack's worth, a pack stopped loading — check its `pack.json` `enabled` flag and that its directory still sits under a `PackRoots.ClassPackRoots()` root. That is the failure mode this task exists for; **update the numbers only after confirming the content change was intentional.**

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T9: authored inventory enforced against the class-test matrix"
```

---

### Task 10: Recipes — validation, status references, and modifier generation

`ClassForge.PackCheck` already parses and validates `skillrecipes.json`. What it cannot do is confirm that a recipe's referenced `StatusEffect` ids exist in the live game. That is this task — and it is the one expected to fail on first run (AC9).

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/RecipeChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `AuthoredContent.RecipeSetsByPackId` (already parsed **and** validated in T4), `AuthoredContent.Packs.MergePlan.ModifierTables`, `GameData.Ids("StatusEffects")` (T1), `ClassForge.Recipes.Model.Vocabulary.TriggerStatusToken`, `ClassForge.Recipes.Generation.ModifierRecipeGenerator`.
- Produces: `static class Checks.RecipeChecks` — `static void Register(CheckRunner runner, GameData data, AuthoredContent content)`, `static IEnumerable<KeyValuePair<string,string>> ReferencedStatusIds(ClassForge.Recipes.Model.RecipeSet set)`.

- [ ] **Step 1: Write `Checks/RecipeChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Model;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Every shipped skillrecipes.json validates clean, every StatusEffect id a recipe applies resolves
    /// against live Configs.StatusEffects (189 entries) plus the packs' own merged statuses (10), and every
    /// modifiers.json table still round-trips through the engine-owned generator.
    ///
    /// The recipe sets are the ones AuthoredContent already parsed and validated, so this file cannot drift
    /// from the id universe the reference checker used.
    /// </summary>
    public static class RecipeChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            runner.Section("ClassForge — recipes vs live Configs");

            runner.Case("every shipped skillrecipes.json validates clean", delegate
            {
                Check.Exactly(4, content.RecipeSetsByPackId.Count,
                    "packs with a skillrecipes.json (all four ship one)");

                List<string> offenders = new List<string>();
                foreach (KeyValuePair<string, RecipeSet> entry in content.RecipeSetsByPackId)
                    foreach (Finding f in entry.Value.Findings)
                        if (f.Severity == FindingSeverity.Error)
                            offenders.Add(entry.Key + ": " + f.ToString());
                Check.Empty(offenders, "recipe validation errors");
            });

            runner.Case("every referenced StatusEffect id resolves", delegate
            {
                HashSet<string> resolvable = new HashSet<string>(data.Ids("StatusEffects"), StringComparer.Ordinal);
                foreach (MergeOp op in content.Packs.MergePlan.StatusEffects) resolvable.Add(op.Id);

                int inspected = 0;
                List<string> offenders = new List<string>();
                foreach (KeyValuePair<string, RecipeSet> entry in content.RecipeSetsByPackId)
                {
                    foreach (KeyValuePair<string, string> pair in ReferencedStatusIds(entry.Value))
                    {
                        inspected++;
                        if (!resolvable.Contains(pair.Value))
                            offenders.Add(entry.Key + " / " + pair.Key + " -> status '" + pair.Value +
                                          "' is neither in Configs.StatusEffects nor a merged pack status");
                    }
                }

                Check.AtLeast(20, inspected,
                    "status references inspected (32 measured) — zero would make this check vacuous");
                Check.Empty(offenders, "recipes referencing unknown StatusEffect ids");
            });

            runner.Case("every modifier table generates a valid recipe pair", delegate
            {
                Check.Exactly(1, content.Packs.MergePlan.ModifierTables.Count, "modifier tables");

                List<string> offenders = new List<string>();
                foreach (ModifierTable table in content.Packs.MergePlan.ModifierTables)
                {
                    ModifierTableInput input = new ModifierTableInput();
                    input.SelectionRecipeId = table.SelectionRecipe;
                    foreach (ModifierEntry m in table.Modifiers)
                    {
                        ModifierRow row = new ModifierRow();
                        row.Id = m.Id;
                        row.Weight = m.Weight;
                        row.Status = m.Status;
                        row.MaxHpPercent = m.MaxHpPercent;
                        input.Modifiers.Add(row);
                    }

                    GeneratedModifierRecipes generated = ModifierRecipeGenerator.Generate(input);
                    if (generated == null || generated.Select == null || generated.Apply == null)
                    {
                        offenders.Add(table.PackId + "/" + table.SelectionRecipe + ": generator returned nothing usable");
                        continue;
                    }

                    RecipeSet genSet = new RecipeSet();
                    genSet.Add(generated.Select);
                    genSet.Add(generated.Apply);
                    ClassForge.Recipes.Parsing.RecipeValidator.Validate(genSet);
                    foreach (Finding f in genSet.Findings)
                        if (f.Severity == FindingSeverity.Error)
                            offenders.Add(table.PackId + ": " + f.ToString());
                }
                Check.Empty(offenders, "generated encounter-modifier recipe errors");
            });

            runner.Case("NEGATIVE control: TRIGGER_STATUS is excluded, an invented id is not", delegate
            {
                // Two ways this check family could go silently wrong: treating the TRIGGER_STATUS sentinel
                // as a content id (false positives on every recipe that uses it), or resolving anything at
                // all (false negatives everywhere).
                HashSet<string> resolvable = new HashSet<string>(data.Ids("StatusEffects"), StringComparer.Ordinal);
                Check.True(!resolvable.Contains(Vocabulary.TriggerStatusToken),
                    "TRIGGER_STATUS must not be a live status id — it is a sentinel, and ReferencedStatusIds skips it");
                Check.True(!resolvable.Contains("STATUS_LDH_NEVER_SHIPPED"),
                    "the resolvable status set must reject an invented id");
                Check.True(resolvable.Contains("STATUS_CURSE_00"),
                    "...and accept a verified real one (STATUS_CURSE_00 measured present)");

                RecipeSet probe = new RecipeSet();
                SkillRecipe fake = new SkillRecipe();
                fake.Id = "SKILL_LDH_PROBE";
                RecipeEffect effect = new RecipeEffect();
                effect.Status = "STATUS_LDH_NEVER_SHIPPED";
                fake.Effects.Add(effect);
                probe.Add(fake);
                Check.True(ReferencedStatusIds(probe).Any(delegate (KeyValuePair<string, string> p)
                        { return p.Value == "STATUS_LDH_NEVER_SHIPPED"; }),
                    "ReferencedStatusIds must surface a plain Status field — if it returns nothing, the " +
                    "resolution check above inspects nothing and passes vacuously");
            });
        }

        /// <summary>
        /// recipeId -&gt; status id, walked from the parsed effect model, never from raw JSON (that would
        /// re-implement the parser and drift from it). Only live recipes are walked: one the validator
        /// already disabled cannot reference anything at runtime. TRIGGER_STATUS is a sentinel meaning
        /// "whatever status the trigger carried" and is skipped — treating it as a content id yields a false
        /// positive on every recipe that uses it. StatusFromSelection names a CombatRuntime selection slot,
        /// not a status, so it is skipped too; the status ids it can produce are exactly
        /// StatusFromSelectionTable's StatusId column, which IS walked.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> ReferencedStatusIds(RecipeSet set)
        {
            foreach (SkillRecipe recipe in set.Ordered)
            {
                if (!recipe.IsLive) continue;
                foreach (RecipeEffect effect in recipe.Effects)
                {
                    foreach (string s in DirectStatuses(effect))
                    {
                        if (string.IsNullOrEmpty(s)) continue;
                        if (string.Equals(s, Vocabulary.TriggerStatusToken, StringComparison.Ordinal)) continue;
                        yield return new KeyValuePair<string, string>(recipe.Id, s);
                    }
                }
            }
        }

        private static IEnumerable<string> DirectStatuses(RecipeEffect effect)
        {
            yield return effect.Status;
            yield return effect.FallbackStatus;

            if (effect.StatusOneOf != null)
                foreach (string s in effect.StatusOneOf) yield return s;

            if (effect.StatusFromSelectionTable != null)
                foreach (SelectionStatusEntry row in effect.StatusFromSelectionTable) yield return row.StatusId;
        }
    }
}
```

Note on `Finding`/`FindingSeverity`: inside this file they are `ClassForge.Recipes.Model.Finding` and `ClassForge.Recipes.Model.FindingSeverity`, resolved by the `using ClassForge.Recipes.Model;`. `ClassForge.Core` is also imported for `MergeOp`/`ModifierTable`/`ModifierEntry`, and it declares same-named types — if the compiler reports CS0104 (ambiguous reference), fully qualify the recipe ones as `ClassForge.Recipes.Model.Finding` / `...FindingSeverity` rather than dropping either `using`.

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.RecipeChecks.Register(runner, data, authored);
```

- [ ] **Step 3: Run**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: 3 PASS and **1 FAIL** — `every referenced StatusEffect id resolves`, naming exactly one offender:

```
CF_PACK_ENCOUNTER_MODIFIERS / SKILL_CF_ENCMOD_CURSE_ON_HIT -> status 'CURSE' is neither in
Configs.StatusEffects nor a merged pack status
```

Exit code 1. **This is the harness working, not the harness broken.**

- [ ] **Step 4: Report the `CURSE` finding — do not silence it (AC9)**

The evidence, gathered 2026-08-23:

- `CF_PACK_ENCOUNTER_MODIFIERS/skillrecipes.json` line 10 declares `{ "Type": "ADD_STATUS", "Target": "TRIGGER_TARGET", "Status": "CURSE" }`.
- `"CURSE"` is a member of `ClassForge.Recipes.Model.Vocabulary.HarmfulStatusTypes` — i.e. an `eStatusEffectTypes` **type** name, not a `Configs.StatusEffects` **id**.
- Live `Configs.StatusEffects` contains no `CURSE`; the nearest real ids are `STATUS_CURSE_00`, `STATUS_CURSE_LETHARGIC`, `STATUS_CURSE_UNWELL`, `STATUS_CURSE_FEEBLE`, `STATUS_CURSE_UNLUCKY`, `STATUS_CURSE_BLIND`, `STATUS_CURSE_CLUMSY`, `STATUS_CURSE_FOOLISH`.
- The recipe's own `_source` note says it is **inert** until the M-EM2 status-passives extension is wired, so nothing observable is broken today.

Three permitted responses, in order of preference. **Weakening or allowlisting the check is not among them.**

1. **Report and stop.** Record the finding in the commit message and leave the harness exiting 1. Correct if the owner has not been consulted. This is the default.
2. **Fix the data**, once the owner agrees: change `"Status": "CURSE"` to `"Status": "STATUS_CURSE_00"` in `CF_PACK_ENCOUNTER_MODIFIERS/skillrecipes.json`. One token, in an inert recipe. Note that this changes the pack `dataHash` and therefore the MP parity handshake, so every peer must take the same zip.
3. **Correct the rule**, if and only if evidence shows `ADD_STATUS.Status` legitimately accepts a type name (check `ClassForge.Recipes.Runtime.RecipeDispatcher`'s ADD_STATUS path and `ClassForge.Plugin`'s status resolution). If so, extend the resolvable set with `Vocabulary.HarmfulStatusTypes`, record the correction in this plan's ground-truth table and the file's XML doc, and say plainly that the rule — not the check — was wrong.

Whichever is chosen, say which one and why. Do not proceed to Task 11 having quietly deleted the case.

- [ ] **Step 5: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T10: recipe validation and StatusEffect reference resolution vs live Configs"
```

---

### Task 11: Determinism

MP parity depends on every peer computing the same `dataHash` from the same pack files. This catches ordering/hash non-determinism inside one process, which is cheap and strictly weaker than the cross-process check Task 12's wrapper adds.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/DeterminismChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `AuthoredContent.Load(GameData)` (T4), `PackLoadResult.DataHash`, `MergePlan` orderings, `FTK2Mods.DevKit.DataHasher.IsWellFormedHash(string)`.
- Produces: `static class Checks.DeterminismChecks` — `static void Register(CheckRunner runner, GameData data, AuthoredContent first)`.

- [ ] **Step 1: Write `Checks/DeterminismChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// A second full load in the same process must produce a byte-identical dataHash, an identical
    /// merge-op ordering, and an identical resolved pack order. Peers that disagree on dataHash fail the
    /// ParityService handshake and drop into SafeMode, so this is the cheapest possible proxy for "would
    /// two players' installs agree" — and it runs with no game and no second machine.
    ///
    /// One extra load is performed, shared by all three cases: three separate loads would triple the run
    /// time for no additional signal.
    /// </summary>
    public static class DeterminismChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent first)
        {
            runner.Section("Determinism");

            AuthoredContent second = AuthoredContent.Load(data);

            runner.Case("dataHash is well-formed and stable across two loads", delegate
            {
                Check.True(!string.IsNullOrEmpty(first.Packs.DataHash), "first load produced a dataHash");
                Check.True(FTK2Mods.DevKit.DataHasher.IsWellFormedHash(first.Packs.DataHash),
                    "dataHash is well-formed per DevKit.Core: " + first.Packs.DataHash);
                Check.True(first.Packs.DataHash.StartsWith(DataHasher.HashPrefix, StringComparison.Ordinal),
                    "dataHash carries the ClassForge.Core.DataHasher.HashPrefix: " + first.Packs.DataHash);
                Check.Eq(first.Packs.DataHash, second.Packs.DataHash, "dataHash across two loads");
            });

            runner.Case("merge-op ordering is stable across two loads", delegate
            {
                List<string> offenders = new List<string>();
                Compare(offenders, "Characters", first.Packs.MergePlan.Characters, second.Packs.MergePlan.Characters);
                Compare(offenders, "Things", first.Packs.MergePlan.Things, second.Packs.MergePlan.Things);
                Compare(offenders, "Abilities", first.Packs.MergePlan.Abilities, second.Packs.MergePlan.Abilities);
                Compare(offenders, "StatusEffects", first.Packs.MergePlan.StatusEffects, second.Packs.MergePlan.StatusEffects);
                Check.AtLeast(35, first.Packs.MergePlan.Characters.Count, "ops compared (a zero-op compare is vacuous)");
                Check.Empty(offenders, "merge-op ordering differences between two loads");
            });

            runner.Case("pack load order and recipe universe are stable across two loads", delegate
            {
                Check.Eq(Join(first.Packs.EnabledOrderedPacks), Join(second.Packs.EnabledOrderedPacks),
                    "resolved pack load order");
                Check.Eq(string.Join(",", first.AllRecipeIds.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal)),
                         string.Join(",", second.AllRecipeIds.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal)),
                         "authored + generated recipe id universe");
            });

            runner.Case("NEGATIVE control: the comparator detects a difference", delegate
            {
                // Compare() returning nothing for genuinely different inputs would make all three cases
                // above permanently green.
                List<MergeOp> a = new List<MergeOp>();
                a.Add(new MergeOp("A", ClassForge.Core.Json.JsonValue.Null, "P"));
                a.Add(new MergeOp("B", ClassForge.Core.Json.JsonValue.Null, "P"));
                List<MergeOp> b = new List<MergeOp>();
                b.Add(new MergeOp("B", ClassForge.Core.Json.JsonValue.Null, "P"));
                b.Add(new MergeOp("A", ClassForge.Core.Json.JsonValue.Null, "P"));

                List<string> reordered = new List<string>();
                Compare(reordered, "Synthetic", a, b);
                Check.AtLeast(1, reordered.Count, "Compare must report a reordering");

                List<string> shortened = new List<string>();
                Compare(shortened, "Synthetic", a, new List<MergeOp>());
                Check.AtLeast(1, shortened.Count, "Compare must report a count difference");

                List<string> identical = new List<string>();
                Compare(identical, "Synthetic", a, a);
                Check.Empty(identical, "Compare must report nothing for identical lists");
            });
        }

        private static string Join(List<PackManifest> packs)
        {
            return string.Join(",", packs.Select(delegate (PackManifest p) { return p.Id; }));
        }

        private static void Compare(List<string> offenders, string label, List<MergeOp> a, List<MergeOp> b)
        {
            if (a.Count != b.Count)
            {
                offenders.Add(label + ": count " + a.Count + " vs " + b.Count);
                return;
            }
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal))
                    offenders.Add(label + "[" + i + "]: '" + a[i].Id + "' vs '" + b[i].Id + "'");
        }
    }
}
```

`FTK2Mods.DevKit` is the namespace declared at `FTK2.DevKit/src/DevKit.Core/DataHasher.cs:8`; it matches neither the folder nor the assembly name (`ftk2mods.devkit`), so `using DevKit.Core;` will not compile — hence the fully-qualified call. `DataHasher.HashPrefix` unqualified resolves to `ClassForge.Core.DataHasher` via the `using ClassForge.Core;`, which is the intended one (both declare the identical `"sha256:"` value).

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.DeterminismChecks.Register(runner, data, authored);
```

- [ ] **Step 3: Run and verify**

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release
```

Expected: 4 PASS. The run now performs two full `PackLoader.Load` passes plus two `RecipeParser` sweeps; if total wall time exceeds ~15 s, report it — the target is a command someone runs reflexively, and a slow one stops being run.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T11: in-process determinism checks for dataHash and merge ordering"
```

---

### Task 12: JSON report, wrapper script, cross-process determinism, docs

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Report.cs`
- Create: `tools/run-harness.ps1`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/README.md`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`
- Modify: `README.md` (repo root — the verification-docs bullet list)
- Modify: `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md` (mark which manual checks the harness now covers)

**Interfaces:**
- Consumes: `CheckRunner.Passed/Failed/Failures/Warnings` (T1).
- Produces: `static class Report` — `static void Write(string path, int passed, int failed, IReadOnlyList<CheckFailure> failures, IReadOnlyList<string> warnings)`.

- [ ] **Step 1: Write `Report.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LiveDataHarness
{
    /// <summary>
    /// Machine-readable run summary, so the harness can gate a future CI step or be diffed between game
    /// updates.
    ///
    /// Deliberately absent (AC5): elapsed times, the resolved game path, and the repo path. All three vary
    /// between two runs of an unchanged setup, and a report that cannot be byte-compared cannot prove
    /// cross-process determinism. Everything present is sorted ordinally for the same reason.
    /// </summary>
    public static class Report
    {
        public static void Write(string path, int passed, int failed,
                                 IReadOnlyList<CheckFailure> failures, IReadOnlyList<string> warnings)
        {
            List<Dictionary<string, string>> failureRows = failures
                .OrderBy(delegate (CheckFailure f) { return f.Name; }, StringComparer.Ordinal)
                .Select(delegate (CheckFailure f)
                {
                    Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.Ordinal);
                    row["name"] = f.Name;
                    row["message"] = f.Message;
                    return row;
                })
                .ToList();

            List<string> warningRows = warnings.OrderBy(delegate (string w) { return w; }, StringComparer.Ordinal).ToList();

            Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.Ordinal);
            payload["schema"] = "livedataharness.report.v1";
            payload["passed"] = passed;
            payload["failed"] = failed;
            payload["failures"] = failureRows;
            payload["warnings"] = warningRows;

            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            string json = JsonSerializer.Serialize(payload, options);

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }
}
```

- [ ] **Step 2: Call `Report` from `Program.Main`**

Replace the final `return runner.Report();` with:

```csharp
            int exit = runner.Report();

            string jsonOut = ArgValue(args, "--json");
            if (!string.IsNullOrEmpty(jsonOut))
            {
                Report.Write(jsonOut, runner.Passed, runner.Failed, runner.Failures, runner.Warnings);
                Console.WriteLine("report: " + System.IO.Path.GetFullPath(jsonOut));
            }

            return exit;
```

- [ ] **Step 3: Write `tools/run-harness.ps1`**

Save it **with a UTF-8 BOM** — commit `08db46c` fixed exactly this for `deploy.ps1` (Windows PowerShell 5.1 `-File` misparses without one).

```powershell
<#
.SYNOPSIS
  Builds and runs LiveDataHarness against a real game install, then re-runs it in a second process to
  prove cross-process determinism of the report (and therefore of the pack dataHash).
.PARAMETER GameDir
  Optional path to the For The King II install. Omit to auto-detect.
.PARAMETER JsonOut
  Where to write the first pass's JSON report.
.NOTES
  The game directory is only ever read. This script never writes to it, and it is safe to run while the
  game is open.
#>
param(
    [string]$GameDir = '',
    [string]$JsonOut = 'tools/out/harness-report.json'
)

$ErrorActionPreference = 'Stop'
$proj = 'FTK2.DevKit/sandbox/LiveDataHarness'

dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'LiveDataHarness build failed' }

$common = @('run', '--project', $proj, '-c', 'Release', '--no-build', '--')
if ($GameDir) { $common += @('--game-dir', $GameDir) }

# Pass 1 - full check suite, writes the JSON report.
& dotnet @($common + @('--json', $JsonOut))
$exit1 = $LASTEXITCODE

# Pass 2 - a fresh process; its report must be byte-identical to pass 1's.
$second = [System.IO.Path]::ChangeExtension($JsonOut, '.pass2.json')
& dotnet @($common + @('--json', $second))
$exit2 = $LASTEXITCODE

if ($exit1 -eq 2 -or $exit2 -eq 2) {
    Write-Host 'LiveDataHarness SKIPPED (no loadable For The King II install found).'
    exit 2
}

$h1 = (Get-FileHash $JsonOut -Algorithm SHA256).Hash
$h2 = (Get-FileHash $second  -Algorithm SHA256).Hash
if ($h1 -ne $h2) {
    Write-Host "CROSS-PROCESS DETERMINISM FAILURE: $JsonOut and $second differ."
    exit 1
}

Write-Host "LiveDataHarness: pass1=$exit1 pass2=$exit2, reports byte-identical."
exit ([Math]::Max($exit1, $exit2))
```

- [ ] **Step 4: Verify the wrapper end to end**

```bash
pwsh -File tools/run-harness.ps1
```

Expected: two passes, `reports byte-identical`, and the exit code matching the harness's own (0 once the Task 10 `CURSE` question is resolved; 1 while it stands). If the reports differ, inspect the diff: a timing field or an absolute path leaking into the report is a **report bug** (remove the field); a differing failure list is a genuine determinism finding and belongs in the commit message.

```bash
pwsh -File tools/run-harness.ps1 -GameDir "Z:\nope"
```

Expected: `LiveDataHarness SKIPPED`, exit 2 (AC4).

- [ ] **Step 5: Verify AC7 and AC8 by inspection**

```bash
grep -rn "PackageReference" FTK2.DevKit/sandbox/LiveDataHarness/
grep -rn "FTK2.dll\|UnityEngine\|BepInEx" FTK2.DevKit/sandbox/LiveDataHarness/*.csproj
grep -rn "File\.Write\|File\.Create\|File\.Delete\|File\.Move\|File\.Copy\|Directory\.Create\|Directory\.Delete" FTK2.DevKit/sandbox/LiveDataHarness/
grep -rn "TODO\|TBD\|NotImplementedException" FTK2.DevKit/sandbox/LiveDataHarness/
```

Expected: no `PackageReference`; no game/Unity/BepInEx reference in the csproj (the string `FTK2.dll` appears only inside `GameData.cs` as a runtime path, which is correct); the only write calls are `Report.Write`'s `Directory.CreateDirectory` + `File.WriteAllText` against the `--json` path; zero placeholder strings.

- [ ] **Step 6: Write `FTK2.DevKit/sandbox/LiveDataHarness/README.md`**

Cover, in this order:

1. **What it is** — one paragraph: runs every mod's `.Core` logic against the real game's `Configs`, loaded out-of-process with no Unity, no BepInEx, and no running game.
2. **The one command** — `pwsh -File tools/run-harness.ps1`.
3. **Exit codes** — 0 pass · 1 Error findings · 2 no loadable install (skipped).
4. **The recorded install baseline** — reproduce the "Live dictionary counts" table from this plan verbatim, with the sentence: *this install is deliberately contaminated and that is correct; the co-op saves depend on the EOR content, and Steam's "Verify integrity of game files" deletes it. Never run it to make this harness green.*
5. **Check inventory** — one line per check family: provenance, vocabulary, adds-only, reference integrity, localization, Blessings roster gate, Summoner adds-only, inventory coverage, recipes, determinism.
6. **The two field-map traps** — `Passives` resolve against `SkillConfigs` (not `Abilities`), and `Passives` must also resolve against the packs' own `skillrecipes.json` ids because `MergePlan` has none. State the false-finding counts (100 and 56) so a future maintainer does not re-fall into either.
7. **What it cannot catch** — Harmony patch application, UI injection (class-select and trait-pick lists), combat behaviour, the multiplayer handshake, and anything else needing a running game. Point at the operator smoke script and, once they exist, the S1/S3/S4 Crucible components for those.

- [ ] **Step 7: Wire into the repo README and the operator handoff**

In the root `README.md` verification-docs bullet list:

```markdown
- [FTK2.DevKit/sandbox/LiveDataHarness](FTK2.DevKit/sandbox/LiveDataHarness/README.md) — runs every mod's `.Core` logic against the real game's `Configs`, loaded out-of-process with no Unity/BepInEx/running game (`pwsh -File tools/run-harness.ps1`)
```

In `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md`, add a short subsection immediately before the numbered smoke checks, stating which of them the harness now covers offline (adds-only merge, reference integrity, localization coverage, the Blessings roster gate, Summoner adds-only, recipe validation, inventory coverage, determinism) and which still require a launch. **Do not delete any manual check** — the harness reduces what must be checked by hand; it does not replace the in-game evidence for UI, combat, or MP.

- [ ] **Step 8: Full run before committing**

```bash
pwsh -File tools/run-harness.ps1
```

Paste the final summary line into the commit message, and state explicitly which of the three Task 10 Step 4 options was taken for the `CURSE` finding.

- [ ] **Step 9: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness tools/run-harness.ps1 README.md \
        docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md
git commit -m "LiveDataHarness T12: JSON report, run-harness wrapper, cross-process determinism, docs"
```

---

## Notes for the executing session

**Parallelism.** Tasks 1 → 2 → 3 → 4 are strictly sequential (each consumes the previous one's types). After Task 4 lands, **Tasks 5, 6, 7, 8, 9, 10 are mutually independent** — each creates its own file under `Checks/` and appends one line to `Program.cs`. They can run concurrently in separate worktrees; the only conflict surface is the block of `Checks.*.Register(...)` lines in `Program.cs`, which is a trivial textual resolution. Task 11 needs Task 4. Task 12 needs all of them.

**Do not weaken a check to make it green.** Every check here has a negative control or a fixture precisely so that "it passes" means something. If a check fires against real content, the default action is to report it. The only legitimate reason to change a rule is discovering the rule's *mapping* is wrong — as `Passives → Abilities` was, and as `Passives → MergePlan only` was — and that discovery must be recorded in the file's XML doc **and** in this plan's ground-truth table.

**Never recommend a Steam integrity verify.** It deletes the EOR content the owner's co-op saves depend on and the Armory content this repo deploys. If the provenance gate fires, the fix is to identify and record the new content, or to restore from `C:\Users\ben\Backups\ftk2-2026-08-23\` — never to "repair" the install.

**What this harness deliberately does not do.** It does not apply the merge to `Configs` (that is `ClassForge.Plugin.ConfigMergePatches.ApplyPlan`, a `private static` method in a net472 BepInEx-coupled assembly). It does not exercise Harmony patch application, UI, combat, or multiplayer. Those need the S1 observation surface, the S3 verb layer, and the S4 scenario runner — separate, larger plans in the spec's build order.

**Instrument of record for re-grounding.** When a member name in `GameData.cs` or `GameVocabulary.cs` stops resolving, the answer comes from `dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- <TypeName>`, not from memory. That is the whole point of spec §2.

---

## Self-Review

**Spec coverage.** §8 (S6, corrected AC1/AC2) → Tasks 3–11, with the correction itself carried in Task 3's preamble and AC1/AC2. §2 grounding rule → the ground-truth section, every measured claim cited to its measurement, and the TypeProbe re-grounding instruction in Tasks 2 and 10. §10 testing standard: all logic runs with no game beyond the reflective `Configs` load; **every check family carries a negative control** (17 explicit negative-control cases across Tasks 1-11 — 15 named `NEGATIVE ...` plus the two lower-cased ones in Tasks 1 and 2 — including two deliberately-broken fixtures with prescribed inversion runs); no component reports success it has not verified — every collection-scanning check asserts a non-zero subject count so a vacuous pass is impossible. §11 build order: S6 sits after S0, needs nothing from S1/S3/S4, and delivers standalone.

**AC1/AC2 rewrite, stated plainly.** The predecessor plan demanded a pristine 2095-`Characters` install and manufactured contamination to prove a purity gate. Both are inverted here. AC1 is now green *on the contaminated install*, asserting the expected pack set (EOR 31 in `Characters`, Armory 533 in `Things`) is present and every authored id resolves. AC2 is now three Error rules — expected-content-missing, unaccounted-content-present, repo-content-on-disk — plus a Warning for legitimate version drift, each proven by a pure-function negative control over a synthetic snapshot rather than by damaging the live install. Steam's integrity verify is named as a hazard in three places rather than prescribed as a remedy.

**Placeholder scan.** No `TBD`, `TODO`, `NotImplementedException`, "similar to Task N", or "add validation here". Every code step is complete and runnable. Two forward references exist and are both explicit at the point of use: Task 1's `Program.cs` is extended (not replaced) by every later task's one-line registration, and Task 5's `AuthoredTags` warning tells the implementer to delete any entry the enum vocabulary already covers.

**Type consistency audit.** `CheckRunner.Case/Section/Warn/Report/Failures/Warnings/Passed/Failed` defined in Task 1, used unchanged in Tasks 2–12. `Check.True/AtLeast/Exactly/Eq/Empty` defined in Task 1, every later call matches an overload. `GameData.Ids/Count/Values/Lang/Field` defined in Task 1; `GameVocabulary.Build` + its ten sets defined in Task 2 and consumed in Task 5; `InstallProvenance`'s four pure scanners defined and consumed in Task 3 only. `PackRoots.RepoRoot/ClassPackRoots/FollowerPackRoot/FixturesDir` defined in Task 4 and used in Tasks 4, 5, 7, 8. `AuthoredContent.Load/LoadFrom/ErrorFindings` and its six members are defined in Task 4 and consumed in Tasks 4, 5, 6, 7, 9, 10, 11 with identical signatures. `Io.HarnessPackSource`/`HarnessJsonCodec` are defined and used in Task 8 only. `Report.Write(string,int,int,IReadOnlyList<CheckFailure>,IReadOnlyList<string>)` is defined in Task 12 Step 1 and called in Step 2 with matching arguments. Exit codes 0/1/2 are stated identically in the Global Constraints, `Program.cs`, the wrapper, the README outline, and AC4.

**Known ambiguity flagged rather than hidden.** Two `FindingSeverity` enums and two `Finding` classes exist across `ClassForge.Core` and `ClassForge.Recipes.Model`; Task 10 names the collision and prescribes qualification rather than leaving a CS0104 for the implementer to discover. Three `MergePlan` types exist (`ClassForge.Core`, `Summoner.Core.Merge`, and this plan's prose); the Summoner one is only ever referenced inside `Checks/SummonerChecks.cs`, which does not import `ClassForge.Core`.

**Expected non-green outcome, declared up front.** Task 10 will fail on the first run with exactly one finding (`SKILL_CF_ENCMOD_CURSE_ON_HIT -> CURSE`). It is documented in AC9, in the pre-flight table, and in Task 10 Step 4 with three permitted responses, none of which is silencing the check. A plan that predicted an all-green first run here would have been wrong.
