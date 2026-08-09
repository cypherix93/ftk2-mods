# LiveDataHarness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A net10 console harness that loads the *real* game's `Configs` out-of-process (no Unity, no BepInEx, no running game) and runs every shipped mod pack's `.Core` logic against that live data, so id drift, dangling references, missing localization, fail-closed data gates, and non-determinism are caught by one command instead of by a 35-step manual in-game checklist.

**Architecture:** A single `net10.0` console app under `FTK2.DevKit/sandbox/LiveDataHarness/` (mirrors the documented `sandbox/<X>Sim` convention in `docs/research/build-template-notes.md` §7). It project-references the host-agnostic `.Core` assemblies (`ClassForge.Core`, `ClassForge.Recipes`, `Summoner.Core`, `Blessings.Core`, `DevKit.Core`) and reaches the game **entirely through reflection** — `Assembly.LoadFrom` on the install's `Managed/FTK2.dll` plus an `AssemblyResolve` fallback, then `ConfigsHelper.LoadConfigs(basePath)` invoked by reflection. There is **no compile-time game reference**, so the harness builds on a machine with no game installed and degrades to a clear skip message at runtime. Checks are registered functions returning findings; the runner prints a console report, writes a JSON report, and exits non-zero on any error-severity finding.

**Tech Stack:** C# / `net10.0` / `System.Text.Json` (BCL only, no NuGet) · `System.Reflection` · existing repo console-runner pattern (`FTK2.ClassForge/src/ClassForge.Recipes.Tests/Harness.cs`) · PowerShell wrapper.

## Acceptance criteria

The plan is done when every one of these is true and evidenced. They are checkable conditions, not judgment calls — an executing session evaluates itself against this list before claiming completion, and states plainly any that are unmet.

- [ ] **AC1 — Green on a pristine install.** `pwsh -File tools/run-harness.ps1` exits `0` against an install whose `Configs.Characters` reads **2095**, with every registered check reporting PASS.
- [ ] **AC2 — The provenance gate actually rejects contamination.** The same command exits `1` against an install carrying third-party content on disk, naming the offending ids. Captured as evidence *before* the install is restored, since a contaminated install cannot be manufactured afterwards.
- [ ] **AC3 — No check is vacuous.** Every check family has a negative control or a deliberately-broken fixture, and inverting it makes that check FAIL. Task 3's fixture inversion is run explicitly; the rest are asserted by their in-suite negative-control cases.
- [ ] **AC4 — Skip, not fail, without a game.** `pwsh -File tools/run-harness.ps1 -GameDir "Z:\nope"` exits `2` and prints a skip message. The project builds on a machine with no game installed (no compile-time game reference).
- [ ] **AC5 — Deterministic.** Two consecutive processes produce byte-identical JSON reports.
- [ ] **AC6 — No placeholders shipped.** Zero occurrences of `NotImplementedException`, `TODO`, or `TBD` in `FTK2.DevKit/sandbox/LiveDataHarness/`.
- [ ] **AC7 — Repo build rules honored.** No NuGet `PackageReference` in the harness csproj; no compile-time reference to `FTK2.dll`, `UnityEngine*.dll`, or `BepInEx.dll`; every other project in the repo still builds.
- [ ] **AC8 — The game folder is untouched.** `git status` in the game directory is not applicable, so instead: no harness code path writes outside the repo, verified by inspection of every `File.`/`Directory.` write call in the project.
- [ ] **AC9 — Findings surfaced, not silenced.** Any check that fires against real pack content is reported as a finding with its evidence. A check weakened to make it green is a failed acceptance, not a passed one.
- [ ] **AC10 — Documented.** `FTK2.DevKit/sandbox/LiveDataHarness/README.md` exists and states the one command, the exit codes, the check inventory, and explicitly what the harness cannot catch.

## Global Constraints

- **No NuGet packages anywhere in this project.** BCL + `ProjectReference` only. (`FTK2.DevKit/src/DevKit.Plugin/DevKit.Plugin.csproj:31` states the repo build rule; `ClassForge.PackCheck.csproj` and every `*.Core.Tests` project comply.)
- **No compile-time reference to `FTK2.dll`, `UnityEngine*.dll`, or `BepInEx.dll`.** All game access is reflective. This is what keeps the harness buildable and reviewable without a game install and immune to game-update binary drift.
- **No Harmony, no Unity shim.** Empirically verified 2026-08-08: `ConfigsHelper.LoadConfigs` completes in ~1.6–1.9 s in a plain net10 process with **zero** Harmony patches applied. Do not add a Harmony dependency; if a future code path needs one, that is a separate plan.
- **Target framework `net10.0`**, `LangVersion 7.3`, `ImplicitUsings disable`, `Nullable disable` — matching `ClassForge.PackCheck.csproj` exactly.
- **The harness never writes to the game directory.** Read-only access to `For The King II_Data\Managed` and `For The King II_Data\StreamingAssets\Assets`. It is safe to run while the game is open. **It also never requires the mods to be deployed** — pack content is read from the repo's `data/ClassPacks` folders, so a clean game install is the correct and preferred setup.
- **The baseline must be a pristine install.** Third-party mods can write content directly into `StreamingAssets\Assets\Configs\JSON~` (verified 2026-08-08: EOR injects 31 `EOR_*` classes into `Characters.json`), which silently changes what "live ids" means and can mask or fabricate collisions. Task 1 gates every other check on a provenance assertion; restore with Steam's *Verify integrity of game files* before trusting a run.
- **Exit codes:** `0` = all checks passed · `1` = at least one `Error` finding · `2` = game install not found / configs unloadable (skipped, not failed).
- **`Warning` findings never fail the run** (same posture as `ClassForge.PackCheck`, which tolerates `CF_PACK_BALDURS`'s known `CF_TRAIT_` prefix warning).
- **Every check must be self-testing:** it must be demonstrated failing against a deliberately-broken fixture before it is accepted as passing against real packs. Fixtures live in `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/`.
- **Namespace:** `LiveDataHarness` (root namespace matches assembly name, per `build-template-notes.md` §7).

## Verified ground truth (do not re-derive; these were measured against the live install on 2026-08-08)

| Fact | Value | Why it matters |
|---|---|---|
| `ConfigsHelper.LoadConfigs` | `public static Configs LoadConfigs(string basePath)` | The one entry point. `basePath` = `<game>\For The King II_Data\StreamingAssets\Assets` |
| Live counts (**pristine vanilla**) | `Things` 1847 · `Characters` 2095 · `Abilities` 992 · `SkillConfigs` 68 · `StatusEffects` 189 · `Followers` 48 · `Langs` 15 | Sanity floors for the smoke check |
| Contaminated-install counts | `Characters` **2126** when third-party EOR is installed | EOR writes 31 `EOR_*` classes directly into `StreamingAssets\...\JSON~\Characters.json` on disk — it is **not** a pure runtime patcher. The 2095↔2126 gap is contamination, not game-update drift. Every other dictionary was clean of `EOR_` ids in the contaminated install measured 2026-08-08 |
| `Configs` dictionaries | `SerializedSortedDictionary<string, T>` with a working `.Keys` property | How id sets are snapshotted |
| Value types | `Characters`→`CharacterConfig` · `Things`→`ThingConfig` · `Abilities`→`CombatAbilityConfig` · `SkillConfigs`→`SkillConfig` · `StatusEffects`→`StatusEffectConfig` · `Langs`→`Dictionary<string,string>` | Reflection targets |
| `CharacterConfig` fields | `Stats`, `Things`, `Passives`, `CampQuery`, `SwarmQuery`, `LootID`, `LocKey`, `Rarity` (`eItemRarities`), `Level`, `Threat`, `BaseType` (String), `DefaultBodyType` (String), `Tags`, `OnDeathAbility`, `Expansion` (`eExpansions`) | The reference-checker's field map |
| **`Passives` resolve against `Configs.SkillConfigs`, NOT `Configs.Abilities`** | `SKILL_BLACKHOLE` ∈ `SkillConfigs` (68 entries) | A naive checker that used `Abilities` produced **100 false dangling findings**. This is the single most important correction in this plan |
| `BaseType` is a closed learned vocabulary, not a dictionary key | 32 distinct values across vanilla characters: `BAT, BEAR, BEE, BIRD, BISONTAUR, BOSS, CHAOS, CRAB, DEMON, FLY, GHOST, GHOUL, GOLEM, HELLHOUND, HUMAN, JELLY, KRAKEN, MIMIC, MINDBENDER, PIXIE, PLANT, RAT, SKELETON, SNAKE, SPECIAL, …` | Checking it against `Configs.Characters` produced **31 false findings** |
| `DefaultBodyType` vocabulary | `{ "F", "M" }` | Learned the same way |
| Localization convention | `Langs["en"]` has 9707 keys; a class named `ALCHEMIST` has `en["ALCHEMIST"] = "Alchemist"` and `en["UI_TOOLTIP_ALCHEMIST_DESCRIPTION"]`. Vanilla `CharacterConfig.LocKey` is **empty string** for all 54 `PLAYER`-tagged classes — the id itself is the key | The loc check keys off the config id, not `LocKey` |
| Enum vocabulary | 395 enum types / 4182 distinct member names in `FTK2.dll` | The allowlist that stops enum values (`COMMON`, `MELEE`, `RANGED`) being reported as dangling ids |
| `tools/bin/refs` is **insufficient** for reflective loading | 80 files; missing e.g. `Unity.InputSystem.dll` | The harness must resolve against the live `Managed/` folder (205 DLLs), not the snapshot |
| Game dir auto-detect | Same candidate list as `tools/deploy.ps1:66` `Resolve-GameDir` | Reuse, don't invent |

## File structure

```
FTK2.DevKit/sandbox/LiveDataHarness/
  LiveDataHarness.csproj      net10.0 exe; ProjectReferences only; no game/BepInEx refs
  Program.cs                  arg parsing, check registration, exit code
  Harness.cs                  Check assertions + CheckRunner (console reporting)
  GameInstall.cs              locate game dir + StreamingAssets/Managed paths
  GameData.cs                 reflective LoadConfigs + id-set snapshots + Lang access
  GameVocabulary.cs           enum member names + learned BaseType/BodyType/Tag sets
  Io/HarnessPackSource.cs     Summoner IPackFileSource + IJsonCodec over the real filesystem
  PackRoots.cs                the repo's shipped pack directories, resolved from AppContext.BaseDirectory
  Checks/ClassForgeChecks.cs  adds-only (live ids), reference integrity, localization
  Checks/BlessingsChecks.cs   roster fail-closed gate replica
  Checks/SummonerChecks.cs    follower adds-only vs live Configs
  Checks/RecipeChecks.cs      skillrecipes parse/validate + status-id references
  Checks/DeterminismChecks.cs double-load hash equality
  Report.cs                   JSON report writer
  fixtures/                   deliberately-broken packs proving each check bites
tools/run-harness.ps1         one-command wrapper (build + run + surface exit code)
```

Rationale for the split: one file per *check family*, because a check family is what a reviewer accepts or rejects as a unit and what a task delivers. `GameData`/`GameVocabulary`/`GameInstall` are separated from checks because they are the only reflection-bearing code — keeping reflection in three small files makes the game-coupling surface auditable and is where a future game update will break.

---

### Task 1: Project skeleton, game-install resolution, reflective config load

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/LiveDataHarness.csproj`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/GameInstall.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/GameData.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Harness.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - `GameInstall.Resolve(string requested)` → `GameInstall` or `null`; instance members `string Root`, `string ManagedDir`, `string StreamingAssetsDir`.
  - `GameData.Load(GameInstall install)` → `GameData`; instance members `object Configs`, `ISet<string> Ids(string dictName)`, `int Count(string dictName)`, `IDictionary<string,string> Lang(string code)`, `IEnumerable<object> Values(string dictName)`; **static** member `object GameData.Field(object cfgObj, string fieldName)`.
  - `Check` static assertion class with `True`, `Eq`, `Empty(IEnumerable<string> offenders, string what)`.
  - `CheckRunner` with `void Case(string name, Action body)`, `void Section(string title)`, `int Report()`, `IReadOnlyList<CheckFailure> Failures`.
  - `CheckFailure` with `string Name`, `string Message`.

- [ ] **Step 1: Write the csproj**

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

- [ ] **Step 2: Write `GameInstall.cs`**

The candidate list is copied verbatim from `tools/deploy.ps1:66` `Resolve-GameDir` so the two tools agree on which install they mean.

```csharp
using System.IO;

namespace LiveDataHarness
{
    /// <summary>Locates a For The King II install. Mirrors tools/deploy.ps1's Resolve-GameDir candidate
    /// list so the harness and the deployer always agree on which install is "the" install.</summary>
    public sealed class GameInstall
    {
        public string Root { get; private set; }
        public string ManagedDir { get { return Path.Combine(Root, "For The King II_Data", "Managed"); } }
        public string StreamingAssetsDir { get { return Path.Combine(Root, "For The King II_Data", "StreamingAssets", "Assets"); } }

        private GameInstall(string root) { Root = root; }

        private static readonly string[] Candidates =
        {
            @"E:\Games\Steam\steamapps\common\For The King II",
            @"C:\Program Files (x86)\Steam\steamapps\common\For The King II",
            @"C:\Program Files\Steam\steamapps\common\For The King II",
        };

        /// <summary>Returns null when no install is found (caller exits 2 = skipped, not failed).</summary>
        public static GameInstall Resolve(string requested)
        {
            if (!string.IsNullOrEmpty(requested))
                return Probe(requested);
            for (int i = 0; i < Candidates.Length; i++)
            {
                var hit = Probe(Candidates[i]);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GameInstall Probe(string root)
        {
            var marker = Path.Combine(root, "For The King II_Data", "Managed", "FTK2.dll");
            return File.Exists(marker) ? new GameInstall(root) : null;
        }
    }
}
```

- [ ] **Step 3: Write `GameData.cs`**

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
    /// Harmony, no Unity shim (verified 2026-08-08: LoadConfigs completes in a plain net10 process with zero
    /// patches applied). Everything downstream sees plain string sets and boxed config objects.
    /// </summary>
    public sealed class GameData
    {
        public object Configs { get; private set; }
        private readonly Dictionary<string, ISet<string>> _idCache = new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
        public string ManagedDir { get; private set; }

        public static GameData Load(GameInstall install)
        {
            var managed = install.ManagedDir;
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var probe = Path.Combine(managed, name + ".dll");
                return File.Exists(probe) ? Assembly.LoadFrom(probe) : null;
            };

            var ftk2 = Assembly.LoadFrom(Path.Combine(managed, "FTK2.dll"));
            var helper = ftk2.GetType("ConfigsHelper", true);
            var load = helper.GetMethod("LoadConfigs", BindingFlags.Public | BindingFlags.Static);
            if (load == null) throw new InvalidOperationException("ConfigsHelper.LoadConfigs(string) not found — game update broke the harness.");

            var configs = load.Invoke(null, new object[] { install.StreamingAssetsDir });
            if (configs == null) throw new InvalidOperationException("ConfigsHelper.LoadConfigs returned null.");

            return new GameData { Configs = configs, ManagedDir = managed };
        }

        private object Dict(string dictName)
        {
            var field = Configs.GetType().GetField(dictName);
            if (field == null) throw new InvalidOperationException("Configs." + dictName + " does not exist — game update broke the harness.");
            return field.GetValue(Configs);
        }

        /// <summary>Ordinal key set of one Configs.* dictionary. Cached — LoadConfigs is ~1.6s, key walks are not free either.</summary>
        public ISet<string> Ids(string dictName)
        {
            ISet<string> cached;
            if (_idCache.TryGetValue(dictName, out cached)) return cached;

            var dict = Dict(dictName);
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (dict != null)
            {
                var keys = dict.GetType().GetProperty("Keys").GetValue(dict, null) as IEnumerable;
                foreach (var k in keys) { var s = k as string; if (s != null) set.Add(s); }
            }
            _idCache[dictName] = set;
            return set;
        }

        public int Count(string dictName) { return Ids(dictName).Count; }

        /// <summary>Boxed values of one Configs.* dictionary, for field-level reflection (e.g. CharacterConfig.BaseType).</summary>
        public IEnumerable<object> Values(string dictName)
        {
            var dict = Dict(dictName);
            if (dict == null) yield break;
            foreach (var kv in (IEnumerable)dict)
                yield return kv.GetType().GetProperty("Value").GetValue(kv, null);
        }

        /// <summary>Reads a public field off a boxed game config object. Returns null when the field is absent.</summary>
        public static object Field(object cfgObj, string fieldName)
        {
            if (cfgObj == null) return null;
            var f = cfgObj.GetType().GetField(fieldName);
            return f == null ? null : f.GetValue(cfgObj);
        }

        /// <summary>Configs.Langs[code] as a plain string dictionary. Returns null when the language is absent.</summary>
        public IDictionary<string, string> Lang(string code)
        {
            var langs = Dict("Langs");
            if (langs == null) return null;
            var contains = langs.GetType().GetMethod("ContainsKey");
            if (!(bool)contains.Invoke(langs, new object[] { code })) return null;
            return langs.GetType().GetProperty("Item").GetValue(langs, new object[] { code }) as IDictionary<string, string>;
        }
    }
}
```

- [ ] **Step 4: Write `Harness.cs`**

Deliberately mirrors `FTK2.ClassForge/src/ClassForge.Recipes.Tests/Harness.cs` (same `Case`/`Section`/`Report` shape, same "no static mutable state" posture) so anyone who has read that file can read this one.

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveDataHarness
{
    public sealed class CheckFailed : Exception
    {
        public CheckFailed(string message) : base(message) { }
    }

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

        public static void Eq(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new CheckFailed(what + ":\n  expected: " + (expected ?? "<null>") + "\n  actual:   " + (actual ?? "<null>"));
        }

        /// <summary>The workhorse: a check collects offender strings and asserts the list is empty.
        /// Prints at most 15 offenders plus a count, so a wholesale break stays readable.</summary>
        public static void Empty(IEnumerable<string> offenders, string what)
        {
            var list = offenders.ToList();
            if (list.Count == 0) return;
            var shown = string.Join("\n    ", list.Take(15));
            var more = list.Count > 15 ? "\n    ... and " + (list.Count - 15).ToString(CultureInfo.InvariantCulture) + " more" : "";
            throw new CheckFailed(what + ": " + list.Count.ToString(CultureInfo.InvariantCulture) + " offender(s)\n    " + shown + more);
        }
    }

    /// <summary>Console check runner. Instance state only.</summary>
    public sealed class CheckRunner
    {
        private int _passed;
        private int _failed;
        private readonly List<CheckFailure> _failures = new List<CheckFailure>();

        public IReadOnlyList<CheckFailure> Failures { get { return _failures; } }

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

        public int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("checks: " + (_passed + _failed).ToString(CultureInfo.InvariantCulture) +
                              "  passed: " + _passed.ToString(CultureInfo.InvariantCulture) +
                              "  failed: " + _failed.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _failures.Count; i++)
                Console.WriteLine("FAILED: " + _failures[i].Name);
            Console.WriteLine("---------------------------------------------");
            return _failed == 0 ? 0 : 1;
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
    /// Runs the repo's .Core logic against the real game's Configs. Exit 0 = green, 1 = a check failed,
    /// 2 = no game install found (skipped — not a failure).
    /// Usage: LiveDataHarness [--game-dir &lt;path&gt;] [--json &lt;out.json&gt;]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string gameDir = ArgValue(args, "--game-dir");

            var install = GameInstall.Resolve(gameDir);
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                data = GameData.Load(install);
                sw.Stop();
                Console.WriteLine("  ConfigsHelper.LoadConfigs OK in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine("LiveDataHarness: SKIPPED — could not load Configs: " + ex.Message);
                return 2;
            }

            var runner = new CheckRunner();

            runner.Section("Live config sanity");
            runner.Case("live Configs are populated", () =>
            {
                Check.AtLeast(1500, data.Count("Things"), "Configs.Things");
                Check.AtLeast(1800, data.Count("Characters"), "Configs.Characters");
                Check.AtLeast(900, data.Count("Abilities"), "Configs.Abilities");
                Check.AtLeast(50, data.Count("SkillConfigs"), "Configs.SkillConfigs");
                Check.AtLeast(150, data.Count("StatusEffects"), "Configs.StatusEffects");
                Check.True(data.Lang("en") != null, "Configs.Langs contains 'en'");
            });

            runner.Case("install is a pristine baseline (no third-party content on disk)", () =>
            {
                // Third-party mods can write straight into StreamingAssets\...\JSON~. Verified 2026-08-08:
                // EOR injects 31 EOR_* classes into Characters.json, pushing Configs.Characters from 2095 to
                // 2126. That silently redefines "live ids" for every downstream adds-only and reference check,
                // so it is a hard gate, not a warning. Fix: Steam -> Verify integrity of game files.
                var foreign = new List<string>();
                foreach (var dict in new[] { "Characters", "Things", "Abilities", "SkillConfigs", "StatusEffects", "Followers" })
                    foreach (var id in data.Ids(dict))
                        foreach (var prefix in ForeignIdPrefixes)
                            if (id.StartsWith(prefix, StringComparison.Ordinal))
                                foreign.Add(dict + "." + id + " (matches third-party prefix '" + prefix + "')");

                Check.Empty(foreign,
                    "third-party content found on disk in StreamingAssets — restore with Steam's " +
                    "'Verify integrity of game files' before trusting any result below");
            });

            return runner.Report();
        }

        /// <summary>
        /// Id prefixes that must never appear in a pristine install's Configs. "EOR_" is the verified
        /// offender (Enhanced Overhaul writes 31 classes into Characters.json on disk). Our own packs use
        /// CF_EOR_/SMN_/BLSS_/ARM_ prefixes and are read from the repo, never from the game folder, so they
        /// are deliberately NOT listed here — if one shows up in the game's Configs, that is also
        /// contamination and this check should catch it.
        /// </summary>
        internal static readonly string[] ForeignIdPrefixes =
        {
            "EOR_", "CF_", "SMN_", "BLSS_", "ARM_", "WB_",
        };

        internal static string ArgValue(string[] args, string name)
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

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: prints the game path, `ConfigsHelper.LoadConfigs OK in <~1500-2500> ms`, both cases PASS, `checks: 2  passed: 2  failed: 0`, exit code 0.

If the provenance case FAILS listing `EOR_*` ids, the install still has third-party content on disk: run Steam → *Verify integrity of game files*, re-run, and confirm `Configs.Characters` drops to **2095**. Do not proceed to Task 2 against a contaminated baseline — every downstream check would be measuring the wrong thing. Add `using System;` and `using System.Collections.Generic;` to `Program.cs` for this case.

- [ ] **Step 7: Verify the no-install path**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release -- --game-dir "Z:\nope"`
Expected: `LiveDataHarness: SKIPPED — no For The King II install found.`, exit code 2. Confirm with `echo $LASTEXITCODE` (PowerShell).

- [ ] **Step 8: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T1: reflective live-Configs loader + check runner skeleton"
```

---

### Task 2: Game vocabulary (enum names + learned value sets)

Without this, every enum-valued JSON field (`Rarity: "COMMON"`, `Tags: ["RANGED"]`) looks like a dangling content id. Measured on the live install: a naive checker produced 131 false findings; this task is what removes them.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/GameVocabulary.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs` (register the section)

**Interfaces:**
- Consumes: `GameData` (Task 1).
- Produces: `GameVocabulary.Build(GameData data)` → `GameVocabulary` with `ISet<string> EnumMembers`, `ISet<string> BaseTypes`, `ISet<string> BodyTypes`, `ISet<string> CharacterTags`.

- [ ] **Step 1: Write `GameVocabulary.cs`**

`EnumMembers` is read from `FTK2.dll`'s own enum types via the already-loaded assembly (no MetadataLoadContext — the assembly is live in this process). `BaseTypes`/`BodyTypes`/`CharacterTags` are *learned* from vanilla `CharacterConfig` values, because they are `String`/`List<string>` fields with a closed de-facto vocabulary rather than enums.

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
    /// enum member or a closed-vocabulary value". Verified 2026-08-08: FTK2.dll declares 395 enum types with
    /// 4182 distinct member names; vanilla CharacterConfig.BaseType has 32 distinct values (HUMAN, SKELETON,
    /// GOLEM, …) and DefaultBodyType exactly two ("F", "M").
    /// </summary>
    public sealed class GameVocabulary
    {
        public ISet<string> EnumMembers { get; private set; }
        public ISet<string> BaseTypes { get; private set; }
        public ISet<string> BodyTypes { get; private set; }
        public ISet<string> CharacterTags { get; private set; }

        public static GameVocabulary Build(GameData data)
        {
            var enums = new HashSet<string>(StringComparer.Ordinal);
            var ftk2 = data.Configs.GetType().Assembly;
            foreach (var t in SafeGetTypes(ftk2))
            {
                if (t == null || !t.IsEnum) continue;
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    enums.Add(f.Name);
            }

            var baseTypes = new HashSet<string>(StringComparer.Ordinal);
            var bodyTypes = new HashSet<string>(StringComparer.Ordinal);
            var tags = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ch in data.Values("Characters"))
            {
                var bt = GameData.Field(ch, "BaseType") as string;
                if (!string.IsNullOrEmpty(bt)) baseTypes.Add(bt);

                var dbt = GameData.Field(ch, "DefaultBodyType") as string;
                if (!string.IsNullOrEmpty(dbt)) bodyTypes.Add(dbt);

                var tagList = GameData.Field(ch, "Tags") as IEnumerable;
                if (tagList != null)
                    foreach (var tg in tagList) { if (tg != null) tags.Add(tg.ToString()); }
            }

            return new GameVocabulary
            {
                EnumMembers = enums,
                BaseTypes = baseTypes,
                BodyTypes = bodyTypes,
                CharacterTags = tags,
            };
        }

        /// <summary>FTK2.dll references assemblies that may not resolve (e.g. editor-only modules); a partial
        /// type list is fine for enum harvesting, a thrown ReflectionTypeLoadException is not.</summary>
        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }
    }
}
```

- [ ] **Step 2: Register the vocabulary section in `Program.cs`**

Insert directly after the "Live config sanity" section, and hoist `vocab` so later tasks can use it:

```csharp
            var vocab = GameVocabulary.Build(data);

            runner.Section("Game vocabulary");
            runner.Case("enum + learned vocabularies are populated", () =>
            {
                Check.AtLeast(2000, vocab.EnumMembers.Count, "distinct enum member names in FTK2.dll");
                Check.True(vocab.EnumMembers.Contains("COMMON"), "enum vocabulary contains COMMON");
                Check.True(vocab.EnumMembers.Contains("MELEE"), "enum vocabulary contains MELEE");
                Check.AtLeast(20, vocab.BaseTypes.Count, "learned CharacterConfig.BaseType values");
                Check.True(vocab.BaseTypes.Contains("HUMAN"), "BaseType vocabulary contains HUMAN");
                Check.True(vocab.BodyTypes.Contains("M") && vocab.BodyTypes.Contains("F"), "BodyType vocabulary is {F,M}");
                Check.True(vocab.CharacterTags.Contains("PLAYER"), "tag vocabulary contains PLAYER");
            });
```

- [ ] **Step 3: Run and verify**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: `PASS  enum + learned vocabularies are populated`, `checks: 2  passed: 2  failed: 0`, exit 0.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T2: enum + learned-value vocabulary from the live game assembly"
```

---

### Task 3: Pack roots + ClassForge adds-only enforcement against live ids

`ClassForge.Core.PackLoader.Load` already accepts a `LiveIdSets` argument, and its own XML doc names `ClassForge.PackCheck` as the caller that has to pass `null` "because it has no live `Configs` to snapshot". This task is that missing caller.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/PackRoots.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/ClassForgeChecks.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/collide/KNIGHT_COLLIDER/pack.json`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/collide/KNIGHT_COLLIDER/classes.json`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData.Ids` (T1).
- Produces:
  - `PackRoots.RepoRoot()` → `string` (repo root found by walking up from `AppContext.BaseDirectory`, or null).
  - `PackRoots.ClassPackRoots()` → `string[]` (`FTK2.ClassForge/data/ClassPacks`, `FTK2.Blessings/data/ClassPacks`).
  - `PackRoots.FollowerPackRoot()` → `string` (`FTK2.Summoner/data/FollowerPacks`).
  - `ClassForgeChecks.LoadAll(GameData data)` → `ClassForge.Core.PackLoadResult` (memoised by the caller; loaded once, reused by Tasks 4/5/8/9).
  - `ClassForgeChecks.LoadFixture(GameData data, string fixtureRoot)` → `PackLoadResult` (used by Task 4's negative fixture too).
  - `ClassForgeChecks.ErrorFindings(PackLoadResult result)` → `IEnumerable<string>`.
  - `ClassForgeChecks.RegisterAddsOnly(CheckRunner runner, GameData data, ClassForge.Core.PackLoadResult result)`.
  - `PackRoots.FixturesDir()` → `string` (`<output>/fixtures`, populated by the csproj's `CopyToOutputDirectory`).

- [ ] **Step 1: Write `PackRoots.cs`**

```csharp
using System.Collections.Generic;
using System.IO;

namespace LiveDataHarness
{
    /// <summary>Resolves the repo's shipped pack directories by walking up from the executable, the same
    /// trick ClassForge.PackCheck's FindDefaultPack uses.</summary>
    public static class PackRoots
    {
        public static string RepoRoot()
        {
            var dir = System.AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "FTK2.ClassForge", "data", "ClassPacks")))
                    return dir;
                var parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            return null;
        }

        public static string[] ClassPackRoots()
        {
            var repo = RepoRoot();
            if (repo == null) return new string[0];
            var roots = new List<string>();
            var cf = Path.Combine(repo, "FTK2.ClassForge", "data", "ClassPacks");
            var bl = Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks");
            if (Directory.Exists(cf)) roots.Add(cf);
            if (Directory.Exists(bl)) roots.Add(bl);
            return roots.ToArray();
        }

        public static string FollowerPackRoot()
        {
            var repo = RepoRoot();
            if (repo == null) return null;
            var p = Path.Combine(repo, "FTK2.Summoner", "data", "FollowerPacks");
            return Directory.Exists(p) ? p : null;
        }

        public static string FixturesDir()
        {
            return Path.Combine(System.AppContext.BaseDirectory, "fixtures");
        }
    }
}
```

- [ ] **Step 2: Write the negative fixture**

`fixtures/collide/KNIGHT_COLLIDER/pack.json` — the manifest keys are **lowercase-camel**, verified against the real `FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/pack.json` (`ClassForge.Core.ManifestParser` is the authority; getting the casing wrong yields a pack that silently fails to discover, which would make this fixture vacuous):

```json
{
  "author": "ftk2mods",
  "dependencies": [],
  "description": "Harness negative fixture: deliberately ships a class id that already exists in vanilla Configs.Characters, to prove the adds-only live-id check bites. Never deployed.",
  "enabled": true,
  "id": "KNIGHT_COLLIDER",
  "loadOrder": 100,
  "name": "Harness collide fixture",
  "version": "1.0.0"
}
```

`fixtures/collide/KNIGHT_COLLIDER/classes.json` — pick any id that is genuinely present in live `Configs.Characters`; `ALCHEMIST` is verified present (it is one of the 54 `PLAYER`-tagged vanilla classes):

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

- [ ] **Step 3: Write `Checks/ClassForgeChecks.cs` with the adds-only check**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;

namespace LiveDataHarness.Checks
{
    public static class ClassForgeChecks
    {
        /// <summary>Runs the real PackLoader over every shipped ClassPack root, handing it the LIVE id sets —
        /// the argument ClassForge.PackCheck is documented as unable to supply.</summary>
        public static PackLoadResult LoadAll(GameData data)
        {
            var liveIds = new LiveIdSets(
                data.Ids("Characters"),
                data.Ids("Things"),
                data.Ids("Abilities"),
                data.Ids("StatusEffects"));

            return new PackLoader().Load(new FileSystemFileSource(), PackRoots.ClassPackRoots(), null, liveIds);
        }

        /// <summary>Same call shape, pointed at one fixture root — used to prove a check bites.</summary>
        public static PackLoadResult LoadFixture(GameData data, string fixtureRoot)
        {
            var liveIds = new LiveIdSets(
                data.Ids("Characters"),
                data.Ids("Things"),
                data.Ids("Abilities"),
                data.Ids("StatusEffects"));

            return new PackLoader().Load(new FileSystemFileSource(), new[] { fixtureRoot }, null, liveIds);
        }

        public static IEnumerable<string> ErrorFindings(PackLoadResult result)
        {
            return result.Findings
                .Where(f => f.Severity == FindingSeverity.Error)
                .Select(f => f.Code + ": " + f.Message);
        }

        public static void RegisterAddsOnly(CheckRunner runner, GameData data, PackLoadResult result)
        {
            runner.Section("ClassForge — pack load against live Configs");

            runner.Case("shipped packs load with zero Error findings", () =>
            {
                // All four shipped manifests carry "enabled": true as of 2026-08-08 (CF_PACK_EOR_CLASSES,
                // CF_PACK_BALDURS, CF_PACK_ENCOUNTER_MODIFIERS, BLSS_PACK_EOR_BLESSINGS). isPackEnabled is
                // null here, so each manifest's own flag is authoritative — parking a pack lowers this count
                // legitimately, which is why the floor is 3 rather than 4.
                Check.AtLeast(3, result.EnabledOrderedPacks.Count, "packs merged");
                Check.Empty(ErrorFindings(result), "ClassForge PackLoader Error findings");
            });

            runner.Case("no pack id collides with a live vanilla id", () =>
            {
                var live = new[]
                {
                    Tuple.Create("Characters", (IEnumerable<string>)result.MergePlan.Characters.Select(o => o.Id)),
                    Tuple.Create("Things",     (IEnumerable<string>)result.MergePlan.Things.Select(o => o.Id)),
                    Tuple.Create("Abilities",  (IEnumerable<string>)result.MergePlan.Abilities.Select(o => o.Id)),
                    Tuple.Create("StatusEffects", (IEnumerable<string>)result.MergePlan.StatusEffects.Select(o => o.Id)),
                };

                var offenders = new List<string>();
                foreach (var pair in live)
                {
                    var liveSet = data.Ids(pair.Item1);
                    foreach (var id in pair.Item2)
                        if (liveSet.Contains(id))
                            offenders.Add(pair.Item1 + "." + id + " already exists in vanilla Configs");
                }
                Check.Empty(offenders, "pack ids colliding with live Configs ids");
            });

            runner.Case("negative fixture: a vanilla-colliding pack IS refused", () =>
            {
                var fixtureRoot = System.IO.Path.Combine(PackRoots.FixturesDir(), "collide");
                var bad = LoadFixture(data, fixtureRoot);
                Check.True(ErrorFindings(bad).Any(),
                    "fixture pack KNIGHT_COLLIDER (ships vanilla id ALCHEMIST) must produce an Error finding — " +
                    "if this passes silently the adds-only live-id check is not actually running");
            });
        }
    }
}
```

- [ ] **Step 4: Wire into `Program.cs`**

After the vocabulary section:

```csharp
            var cfResult = Checks.ClassForgeChecks.LoadAll(data);
            Checks.ClassForgeChecks.RegisterAddsOnly(runner, data, cfResult);
```

- [ ] **Step 5: Prove the negative fixture bites before trusting the positive result**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: all three cases PASS. The third case is the meaningful one — it passes only because the fixture *did* produce an Error.

Now invert it to confirm the check is real: temporarily change the fixture's `classes.json` key from `ALCHEMIST` to `CF_HARNESS_NOT_A_REAL_CLASS`, re-run, and confirm the third case now **FAILS**. Restore `ALCHEMIST` and re-run to green before committing.

- [ ] **Step 6: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T3: ClassForge adds-only enforcement against live Configs ids"
```

---

### Task 4: ClassForge reference integrity

The check that would have caught the 2026-08-04 EOR upstream delta silently breaking an ability id. **Use the verified field map — `Passives` resolve against `SkillConfigs`, not `Abilities`.**

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/ReferenceChecks.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/dangling/CF_HARNESS_DANGLING/pack.json`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/fixtures/dangling/CF_HARNESS_DANGLING/classes.json`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData`, `GameVocabulary`, `PackLoadResult` (T1–T3). Reads `MergeOp.Value`, a `ClassForge.Core.Json.JsonValue` — a hand-rolled tree, **not** `System.Text.Json`. Verified accessors: `JsonKind Kind`, `JsonValue Get(string key)` (returns `JsonValue.Null`, never C# null), `string GetString(string key, string defaultValue = null)`, `List<string> GetStringArray(string key)`, `IReadOnlyList<KeyValuePair<string, JsonValue>> AsObjectMembers`, `IReadOnlyList<JsonValue> AsArray`, `bool IsNull`.
- Produces: `ReferenceChecks.Register(CheckRunner runner, GameData data, GameVocabulary vocab, PackLoadResult result)`.

- [ ] **Step 1: Write `Checks/ReferenceChecks.cs`**

The field map below is the measured ground truth. Each entry says: *for this `CharacterConfig` field, a value is valid if it is in this set.*

| Field | Kind | Valid when the value is in… |
|---|---|---|
| `Passives` (list) | content id | `Configs.SkillConfigs` ∪ merged `Abilities` ids ∪ merged `StatusEffects` ids |
| `Things` (dict keys) | content id | `Configs.Things` ∪ merged `Things` ids |
| `BaseType` | closed vocab | `vocab.BaseTypes` |
| `DefaultBodyType` | closed vocab | `vocab.BodyTypes` |
| `Rarity`, `Expansion` | enum | `vocab.EnumMembers` |
| `Tags` (list) | mixed | `vocab.CharacterTags` ∪ `vocab.EnumMembers` ∪ the set of loaded pack ids (packs tag their own content with the pack id — `CF_PACK_EOR_CLASSES` appears in `Tags`) |
| `OnDeathAbility`, `LootID`, `CampQuery`, `SwarmQuery` | optional id | empty/absent, or `Configs.Abilities` / `Configs.LootDrops` respectively — **skip these fields in this task**; they are unexercised by the shipped packs (verify with a grep before deciding) and adding them speculatively invites false positives |

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Cross-references every id a shipped pack's classes.json points at against the LIVE game data plus the
    /// pack's own merged ids. Field map verified against the live install 2026-08-08 — in particular
    /// CharacterConfig.Passives resolve against Configs.SkillConfigs (68 entries), NOT Configs.Abilities;
    /// checking Abilities produced 100 false "dangling" findings.
    /// </summary>
    public static class ReferenceChecks
    {
        public static void Register(CheckRunner runner, GameData data, GameVocabulary vocab, PackLoadResult result)
        {
            runner.Section("ClassForge — reference integrity vs live Configs");

            runner.Case("every class reference resolves", () =>
                Check.Empty(Scan(data, vocab, result), "dangling references in shipped pack classes.json"));

            runner.Case("negative fixture: a dangling Passive IS caught", () =>
            {
                var fixtureRoot = System.IO.Path.Combine(PackRoots.FixturesDir(), "dangling");
                var bad = ClassForgeChecks.LoadFixture(data, fixtureRoot);
                var offenders = Scan(data, vocab, bad).ToList();
                Check.True(offenders.Any(o => o.Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST")),
                    "fixture CF_HARNESS_DANGLING must be reported; got: " + string.Join(" | ", offenders));
            });
        }

        /// <summary>Returns one human-readable offender line per unresolved reference.</summary>
        public static IEnumerable<string> Scan(GameData data, GameVocabulary vocab, PackLoadResult result)
        {
            var mergedThings = new HashSet<string>(result.MergePlan.Things.Select(o => o.Id), StringComparer.Ordinal);
            var mergedAbilities = new HashSet<string>(result.MergePlan.Abilities.Select(o => o.Id), StringComparer.Ordinal);
            var mergedStatuses = new HashSet<string>(result.MergePlan.StatusEffects.Select(o => o.Id), StringComparer.Ordinal);
            var packIds = new HashSet<string>(result.EnabledOrderedPacks.Select(p => p.Id), StringComparer.Ordinal);

            var liveSkills = data.Ids("SkillConfigs");
            var liveThings = data.Ids("Things");

            var offenders = new List<string>();

            foreach (var op in result.MergePlan.Characters)
            {
                foreach (var passive in op.Value.GetStringArray("Passives"))
                    if (!liveSkills.Contains(passive) && !mergedAbilities.Contains(passive) && !mergedStatuses.Contains(passive))
                        offenders.Add(op.Id + ".Passives -> " + passive + " (not in Configs.SkillConfigs nor merged Abilities/StatusEffects)");

                foreach (var member in op.Value.Get("Things").AsObjectMembers)
                {
                    var thing = member.Key;
                    if (!liveThings.Contains(thing) && !mergedThings.Contains(thing))
                        offenders.Add(op.Id + ".Things -> " + thing + " (not in Configs.Things nor merged Things)");
                }

                var baseType = op.Value.GetString("BaseType");
                if (!string.IsNullOrEmpty(baseType) && !vocab.BaseTypes.Contains(baseType))
                    offenders.Add(op.Id + ".BaseType -> " + baseType + " (not one of the " + vocab.BaseTypes.Count + " BaseType values vanilla uses)");

                var bodyType = op.Value.GetString("DefaultBodyType");
                if (!string.IsNullOrEmpty(bodyType) && !vocab.BodyTypes.Contains(bodyType))
                    offenders.Add(op.Id + ".DefaultBodyType -> " + bodyType + " (expected one of " + string.Join("/", vocab.BodyTypes) + ")");

                foreach (var fieldName in new[] { "Rarity", "Expansion" })
                {
                    var v = op.Value.GetString(fieldName);
                    if (!string.IsNullOrEmpty(v) && !vocab.EnumMembers.Contains(v))
                        offenders.Add(op.Id + "." + fieldName + " -> " + v + " (not an FTK2 enum member)");
                }

                foreach (var tag in op.Value.GetStringArray("Tags"))
                    if (!vocab.CharacterTags.Contains(tag) && !vocab.EnumMembers.Contains(tag) && !packIds.Contains(tag))
                        offenders.Add(op.Id + ".Tags -> " + tag + " (not a vanilla tag, enum member, or loaded pack id)");
            }

            return offenders;
        }
    }
}
```

Add `using ClassForge.Core.Json;` if the compiler needs it for `JsonValue`; `MergeOp.Value` is already strongly typed, so the accessors above resolve without an explicit cast.

- [ ] **Step 2: Write the negative fixture**

`fixtures/dangling/CF_HARNESS_DANGLING/pack.json` — same lowercase-camel manifest shape as Task 3's fixture, with `"id": "CF_HARNESS_DANGLING"` and a description naming it a harness fixture.

`fixtures/dangling/CF_HARNESS_DANGLING/classes.json`:

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
    "Tags": [ "PLAYER", "CF_HARNESS_DANGLING" ],
    "Things": {},
    "Threat": 0
  }
}
```

- [ ] **Step 3: Wire into `Program.cs`**

```csharp
            Checks.ReferenceChecks.Register(runner, data, vocab, cfResult);
```

- [ ] **Step 4: Run and interpret honestly**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`

Expected: the negative-fixture case PASSes. The real-packs case may **fail on first run** — that is a genuine result, not a bug in the harness. If it reports offenders:
1. Pick one offender and verify it by hand against the live data (grep the id in `<game>\For The King II_Data\StreamingAssets\Assets\Configs\JSON~`).
2. If the reference is genuinely broken → **do not weaken the check.** Record the finding in the commit message and leave the check failing; fixing pack content is out of this plan's scope and belongs to a follow-up.
3. If the check's rule is wrong (a field resolves against a dictionary not in the map) → fix the map, note the correction in the file's XML doc, and re-run.

- [ ] **Step 5: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T4: reference-integrity check for pack classes vs live Configs"
```

---

### Task 5: Localization coverage

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/LocalizationChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `GameData.Lang("en")`, `PackLoadResult.MergePlan.Localization`, `MergePlan.Characters`, `MergePlan.TraitIds`.
- Produces: `LocalizationChecks.Register(CheckRunner runner, GameData data, PackLoadResult result)`.

Verified convention: a class with config id `ALCHEMIST` needs `en["ALCHEMIST"]` (display name) and `en["UI_TOOLTIP_ALCHEMIST_DESCRIPTION"]` (tooltip body). Vanilla `LocKey` is empty on all 54 `PLAYER` classes — the config id *is* the key. `CF_PACK_EOR_CLASSES/localization/en.json` ships 237 keys including exactly 31 `UI_TOOLTIP_*_DESCRIPTION` entries, one per class, and **zero** collisions with vanilla `en`.

- [ ] **Step 1: Write `Checks/LocalizationChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Localization coverage against the live Lang table. Convention verified 2026-08-08 against vanilla:
    /// display name is keyed by the config id itself (en["ALCHEMIST"] == "Alchemist") and the tooltip by
    /// "UI_TOOLTIP_&lt;id&gt;_DESCRIPTION"; CharacterConfig.LocKey is empty for every vanilla PLAYER class,
    /// so the id — not LocKey — is what must resolve.
    /// </summary>
    public static class LocalizationChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult result)
        {
            var en = data.Lang("en");
            var merged = result.MergePlan.Localization;

            Func<string, bool> has = key => merged.ContainsKey(key) || (en != null && en.ContainsKey(key));

            runner.Section("Localization coverage");

            runner.Case("every merged class has a name and a tooltip key", () =>
            {
                var offenders = new List<string>();
                foreach (var op in result.MergePlan.Characters)
                {
                    if (!has(op.Id)) offenders.Add("class " + op.Id + ": missing display-name key '" + op.Id + "'");
                    var tip = "UI_TOOLTIP_" + op.Id + "_DESCRIPTION";
                    if (!has(tip)) offenders.Add("class " + op.Id + ": missing tooltip key '" + tip + "'");
                }
                Check.Empty(offenders, "classes with missing localization");
            });

            runner.Case("every merged trait has a name key", () =>
            {
                var offenders = result.MergePlan.TraitIds
                    .Where(id => !has(id))
                    .Select(id => "trait " + id + ": missing display-name key '" + id + "'");
                Check.Empty(offenders, "traits with missing localization");
            });

            runner.Case("no pack localization key overwrites a vanilla key", () =>
            {
                if (en == null) { Check.True(false, "Configs.Langs['en'] is unavailable"); return; }
                var offenders = merged.Keys
                    .Where(k => en.ContainsKey(k))
                    .Select(k => "pack key '" + k + "' shadows vanilla en['" + k + "'] = \"" + en[k] + "\"");
                Check.Empty(offenders, "pack localization keys shadowing vanilla");
            });

            runner.Case("negative control: a key we never ship is reported missing", () =>
                Check.True(!has("CF_HARNESS_NEVER_SHIPPED_KEY"),
                    "the localization lookup must return false for an unshipped key — otherwise 'has' is always true and the checks above are vacuous"));
        }
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.LocalizationChecks.Register(runner, data, cfResult);
```

- [ ] **Step 3: Run and verify**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: 4 PASS. Baseline for the shipped EOR pack is known-good (31/31 classes have both key families, 0 vanilla collisions), so a failure here means either a real regression or a wrong rule — apply the same honest-interpretation procedure as Task 4 Step 5.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T5: localization coverage + vanilla-shadowing check"
```

---

### Task 6: Blessings fail-closed roster gate

Reproduces, offline, the exact gate at `FTK2.Blessings/src/Blessings.Plugin/GrantAnchorPatches.cs` `EnsureRosterResolvesAgainstConfigs`, which disables the whole plugin for a session when a roster `TraitId` does not resolve in `Env.Configs.Things`. Today the only way to learn that Blessings self-disabled is to launch the game and read a log line.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/BlessingsChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `Blessings.Core.Parsing.BlessingsRegistryParser.Parse(string json)` → `ParseResult { BlessingsRegistry Registry; List<Finding> Findings; bool Success => Registry != null; bool HasErrors; }`; `BlessingsRegistry.Blessings` → `IReadOnlyList<BlessingEntry>`; `BlessingEntry.TraitId`, `.Id`, `.Enabled`. `PackLoadResult.MergePlan.Things` (the BLSS pack's `traits.json` is merged by ClassForge because `FTK2.Blessings/data/ClassPacks` is one of the roots from Task 3).
- Produces: `BlessingsChecks.Register(CheckRunner runner, GameData data, PackLoadResult result)`.

- [ ] **Step 1: Write `Checks/BlessingsChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blessings.Core.Parsing;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Offline replica of Blessings' fail-closed data gate (GrantAnchorPatches.EnsureRosterResolvesAgainstConfigs):
    /// every blessing's TraitId must resolve in Configs.Things, else the plugin disables itself for the whole
    /// session and logs one error. In-game that costs a full launch to discover; here it costs ~2 seconds.
    /// </summary>
    public static class BlessingsChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult result)
        {
            runner.Section("Blessings — fail-closed roster gate");

            var repo = PackRoots.RepoRoot();
            var blessingsJson = repo == null ? null
                : Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks", "BLSS_PACK_EOR_BLESSINGS", "blessings.json");

            runner.Case("blessings.json parses without errors", () =>
            {
                Check.True(blessingsJson != null && File.Exists(blessingsJson), "blessings.json exists at " + blessingsJson);
                var parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "BlessingsRegistryParser succeeded (Registry != null)");
                Check.True(!parsed.HasErrors,
                    "blessings.json has no Error findings; got: " + string.Join(" | ", parsed.Findings.Select(f => f.ToString())));
                Check.AtLeast(1, parsed.Registry.Blessings.Count, "blessings in the roster");
            });

            runner.Case("every roster TraitId resolves (plugin will NOT self-disable)", () =>
            {
                var parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "registry parsed");
                var registry = parsed.Registry;

                var resolvable = new HashSet<string>(data.Ids("Things"), StringComparer.Ordinal);
                foreach (var op in result.MergePlan.Things) resolvable.Add(op.Id);

                var missing = registry.Blessings
                    .Select(b => b.TraitId)
                    .Where(t => !resolvable.Contains(t))
                    .Select(t => "TraitId '" + t + "' unresolved — Blessings would log " +
                                 "'roster TraitId(s) do not resolve in Env.Configs.Things -- this plugin is DISABLED for the session'");

                Check.Empty(missing, "unresolvable Blessings roster TraitIds");
            });

            runner.Case("negative control: an invented TraitId would be caught", () =>
            {
                var resolvable = new HashSet<string>(data.Ids("Things"), StringComparer.Ordinal);
                foreach (var op in result.MergePlan.Things) resolvable.Add(op.Id);
                Check.True(!resolvable.Contains("TRAIT_CF_HARNESS_NEVER_SHIPPED"),
                    "the resolvable-Things set must not contain an invented id — otherwise the gate check is vacuous");
            });
        }
    }
}
```

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.BlessingsChecks.Register(runner, data, cfResult);
```

- [ ] **Step 3: Run and verify**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: 3 PASS. Per the Wave-2 handoff this gate is *live-untested*; a failure here is a genuine, valuable discovery — report it in the commit message rather than relaxing the check.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T6: offline replica of the Blessings fail-closed roster gate"
```

---

### Task 7: Summoner follower packs vs live Configs

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Io/HarnessPackSource.cs`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/SummonerChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `Summoner.Core.Packs.PackLoader.Load(IPackFileSource source, IJsonCodec codec, string root)` → `LoadResult { List<FollowerPack> Packs; List<Finding> Findings; }`; `Summoner.Core.Merge.MergePlanner.Plan(IReadOnlyList<FollowerPack> packs, ISet<string> existingFollowerIds, ISet<string> existingCharacterIds)` → `MergePlan { List<MergeAdd> FollowerAdds; List<MergeAdd> CharacterAdds; List<MergeSkip> Skips; List<Finding> Rejects; }`, where `MergeSkip { string PackId; string Id; string Reason; }`. `IPackFileSource` members: `IEnumerable<string> ListPackDirectories(string root)`, `bool Exists(string path)`, `byte[] ReadAllBytes(string path)`, `IEnumerable<string> ListFilesRecursive(string packDir)`. `IJsonCodec` member: `T Deserialize<T>(string json)`.
- Produces: `HarnessPackSource : IPackFileSource`, `HarnessJsonCodec : IJsonCodec`, `SummonerChecks.Register(CheckRunner runner, GameData data)`.

Note: `Summoner.Plugin/Adapters/FileSystemPackSource.cs` and `GameJsonCodec.cs` exist but are net472/BepInEx-coupled, and `Summoner.Core.Tests`' equivalents live in an `Exe` project. The harness needs its own two small implementations — that is why they are in this task rather than reused.

- [ ] **Step 1: Read the existing implementations before writing new ones**

Run: `cat FTK2.Summoner/src/Summoner.Plugin/Adapters/FileSystemPackSource.cs FTK2.Summoner/src/Summoner.Core.Tests/TestFileSystemPackSource.cs FTK2.Summoner/src/Summoner.Core.Tests/TestJsonCodec.cs`
Mirror their semantics exactly (especially what `ListPackDirectories` returns — full paths vs names — and any path normalisation). Differences here silently produce "0 packs loaded", which would make the whole task vacuously green.

- [ ] **Step 2: Write `Io/HarnessPackSource.cs`**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Summoner.Core.Packs;

namespace LiveDataHarness.Io
{
    /// <summary>IPackFileSource over the real filesystem. Mirrors Summoner.Plugin/Adapters/FileSystemPackSource
    /// without the BepInEx coupling. Adjust to match that file's exact path semantics (Step 1).</summary>
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

    /// <summary>IJsonCodec over System.Text.Json (BCL, no NuGet) — same shape as Summoner.Core.Tests' TestJsonCodec.</summary>
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
    /// skipped, never silently overwritten. Live baseline 2026-08-08: Configs.Followers = 48,
    /// Configs.Characters = 2126.
    /// </summary>
    public static class SummonerChecks
    {
        public static void Register(CheckRunner runner, GameData data)
        {
            runner.Section("Summoner — follower packs vs live Configs");

            var root = PackRoots.FollowerPackRoot();

            runner.Case("follower packs load", () =>
            {
                Check.True(root != null, "FTK2.Summoner/data/FollowerPacks exists");
                var loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                Check.AtLeast(2, loaded.Packs.Count, "follower packs loaded (SMN_PACK_EOR_MERCS + SMN_PACK_EOR_PETS)");
            });

            runner.Case("no follower or character id collides with live Configs", () =>
            {
                var loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                var plan = MergePlanner.Plan(loaded.Packs, data.Ids("Followers"), data.Ids("Characters"));

                // MergePlan.Skips is exactly "entries not added because the id already exists in live Configs"
                // (design §A2.3) — a non-empty Skips list is a pack shipping content the game already has.
                var offenders = plan.Skips
                    .Select(s => s.PackId + "/" + s.Id + ": " + s.Reason)
                    .Concat(plan.Rejects.Select(f => "reject: " + f));
                Check.Empty(offenders, "Summoner entries refused against live Configs");

                Check.AtLeast(1, plan.FollowerAdds.Count,
                    "follower adds planned — zero adds would mean the check above is vacuous");
            });

            runner.Case("negative control: live Followers set is real", () =>
            {
                Check.AtLeast(10, data.Ids("Followers").Count, "Configs.Followers");
                Check.True(!data.Ids("Followers").Contains("SMN_HARNESS_NEVER_SHIPPED"),
                    "live Followers must not contain an invented id — otherwise the collision check is vacuous");
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

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: 3 PASS, with "follower packs loaded" reporting at least 2. **If it reports 0 packs, the check is vacuous — fix `ListPackDirectories` semantics (Step 1) before proceeding.**

- [ ] **Step 6: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T7: Summoner follower-pack adds-only check vs live Configs"
```

---

### Task 8: Recipes — parse, validate, and resolve status references

`ClassForge.PackCheck` already parses and validates `skillrecipes.json`. What it cannot do is confirm that a recipe's referenced `StatusEffect` ids exist in the live game. That is this task.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/RecipeChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `ClassForge.Recipes.Parsing.RecipeParser.Parse(string json)` → `RecipeSet` with `IReadOnlyList<SkillRecipe> Ordered`, `.Findings`, `.HasErrors`, `.Find(string)`, `.Add(SkillRecipe)`; `ClassForge.Recipes.Parsing.RecipeValidator.Validate(RecipeSet)`; `ClassForge.Recipes.Generation.ModifierRecipeGenerator.Generate(ModifierTableInput)`; `ModifierTableInput { SelectionRecipeId, Modifiers }`, `ModifierRow { Id, Weight, Status, MaxHpPercent }`. All confirmed in use at `FTK2.ClassForge/src/ClassForge.PackCheck/Program.cs`.
- Status ids live on the parsed effect model, verified in `FTK2.ClassForge/src/ClassForge.Recipes/Model/SkillRecipe.cs`: `SkillRecipe { string Id; List<RecipeEffect> Effects; bool IsLive; }` and `RecipeEffect { string Status; string FallbackStatus; List<string> StatusOneOf; string StatusFromSelection; List<SelectionStatusEntry> StatusFromSelectionTable; }` where `SelectionStatusEntry { string StatusId; }`. `ClassForge.Recipes.Model.Vocabulary.TriggerStatusToken` is the literal `"TRIGGER_STATUS"` sentinel meaning "the status carried by the trigger" — it is **not** a content id and must be excluded, or the check will report a false positive on every recipe that uses it.
- Produces: `RecipeChecks.Register(CheckRunner runner, GameData data, PackLoadResult result)`.

- [ ] **Step 1: Write `Checks/RecipeChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Every shipped skillrecipes.json parses and validates clean, and every StatusEffect id a recipe applies
    /// resolves against live Configs.StatusEffects (189 entries) plus the packs' own merged statuses. Also
    /// re-runs the encounter-modifier generator over each pack's real modifiers.json, the same way
    /// ClassForge.PackCheck does — but with live status ids available to cross-check.
    /// </summary>
    public static class RecipeChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult result)
        {
            runner.Section("ClassForge — recipes vs live Configs");

            var files = ShippedRecipeFiles().ToList();

            runner.Case("every shipped skillrecipes.json parses and validates clean", () =>
            {
                Check.AtLeast(3, files.Count, "shipped skillrecipes.json files found");
                var offenders = new List<string>();
                foreach (var path in files)
                {
                    var set = RecipeParser.Parse(File.ReadAllText(path));
                    RecipeValidator.Validate(set);
                    if (set.HasErrors)
                        foreach (var f in set.Findings.Where(x => x.Severity == ClassForge.Recipes.Model.FindingSeverity.Error))
                            offenders.Add(Path.GetFileName(Path.GetDirectoryName(path)) + ": " + f);
                }
                Check.Empty(offenders, "recipe validation errors");
            });

            runner.Case("every referenced StatusEffect id resolves", () =>
            {
                var resolvable = new HashSet<string>(data.Ids("StatusEffects"), StringComparer.Ordinal);
                foreach (var op in result.MergePlan.StatusEffects) resolvable.Add(op.Id);

                var offenders = new List<string>();
                foreach (var path in files)
                {
                    var set = RecipeParser.Parse(File.ReadAllText(path));
                    RecipeValidator.Validate(set);
                    var packName = Path.GetFileName(Path.GetDirectoryName(path));
                    foreach (var pair in ReferencedStatusIds(set))
                        if (!resolvable.Contains(pair.Value))
                            offenders.Add(packName + " / " + pair.Key + " -> status '" + pair.Value + "' not in Configs.StatusEffects nor merged statuses");
                }
                Check.Empty(offenders, "recipes referencing unknown StatusEffect ids");
            });

            runner.Case("modifier tables generate valid recipes", () =>
            {
                var offenders = new List<string>();
                foreach (var table in result.MergePlan.ModifierTables)
                {
                    var input = new ModifierTableInput { SelectionRecipeId = table.SelectionRecipe };
                    foreach (var m in table.Modifiers)
                        input.Modifiers.Add(new ModifierRow { Id = m.Id, Weight = m.Weight, Status = m.Status, MaxHpPercent = m.MaxHpPercent });

                    var generated = ModifierRecipeGenerator.Generate(input);
                    if (generated == null) { offenders.Add(table.PackId + "/" + table.SelectionRecipe + ": generator returned null"); continue; }

                    var genSet = new RecipeSet();
                    genSet.Add(generated.Select);
                    genSet.Add(generated.Apply);
                    RecipeValidator.Validate(genSet);
                    if (genSet.HasErrors)
                        foreach (var f in genSet.Findings.Where(x => x.Severity == ClassForge.Recipes.Model.FindingSeverity.Error))
                            offenders.Add(table.PackId + ": " + f);
                }
                Check.Empty(offenders, "generated encounter-modifier recipe errors");
            });
        }

        private static IEnumerable<string> ShippedRecipeFiles()
        {
            foreach (var root in PackRoots.ClassPackRoots())
                foreach (var packDir in Directory.GetDirectories(root))
                {
                    var p = Path.Combine(packDir, "skillrecipes.json");
                    if (File.Exists(p)) yield return p;
                }
        }

        /// <summary>
        /// recipeId -> status id, walked from the parsed effect model (never from raw JSON — that would
        /// re-implement the parser and drift from it). Only live recipes are walked: a recipe the validator
        /// already disabled cannot reference anything at runtime. TRIGGER_STATUS is a sentinel, not an id.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> ReferencedStatusIds(RecipeSet set)
        {
            foreach (var recipe in set.Ordered)
            {
                if (!recipe.IsLive) continue;
                foreach (var effect in recipe.Effects)
                {
                    foreach (var s in DirectStatuses(effect))
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
                foreach (var s in effect.StatusOneOf) yield return s;

            // StatusFromSelection names a CombatRuntime selection slot, not a status id — skip it; the
            // resolvable status ids it can produce are exactly StatusFromSelectionTable's StatusId column.
            if (effect.StatusFromSelectionTable != null)
                foreach (var row in effect.StatusFromSelectionTable) yield return row.StatusId;
        }
    }
}
```

Add `using ClassForge.Recipes.Model;` for `Vocabulary`, `RecipeEffect`, and `RecipeSet`.

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.RecipeChecks.Register(runner, data, cfResult);
```

- [ ] **Step 3: Run and verify**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: 3 PASS. If "every referenced StatusEffect id resolves" reports offenders, apply Task 4 Step 4's honest-interpretation procedure — a genuinely unresolvable status is a real bug worth surfacing.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T8: recipe validation + StatusEffect reference resolution vs live Configs"
```

---

### Task 9: Determinism

MP parity depends on every peer computing the same `dataHash` from the same pack files. This check catches ordering/hash non-determinism in one process, which is cheap and strictly weaker than the cross-process check the wrapper script adds in Task 10.

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Checks/DeterminismChecks.cs`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`

**Interfaces:**
- Consumes: `ClassForgeChecks.LoadAll(GameData)` (T3), `PackLoadResult.DataHash`, `MergePlan.Characters/Things/Abilities/StatusEffects` ordering, `DevKit.Core`'s `DataHasher.IsWellFormedHash(string)`.
- Produces: `DeterminismChecks.Register(CheckRunner runner, GameData data, PackLoadResult first)`.

- [ ] **Step 1: Write `Checks/DeterminismChecks.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// A second full PackLoader.Load in the same process must produce a byte-identical dataHash and an
    /// identical merge-op ordering. Peers that disagree on dataHash fail the MP parity handshake, so this is
    /// the cheapest possible proxy for "would two players' installs agree".
    /// </summary>
    public static class DeterminismChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult first)
        {
            runner.Section("Determinism");

            runner.Case("dataHash is well-formed and stable across two loads", () =>
            {
                Check.True(!string.IsNullOrEmpty(first.DataHash), "first load produced a dataHash");
                Check.True(FTK2Mods.DevKit.DataHasher.IsWellFormedHash(first.DataHash),
                    "dataHash is well-formed per DevKit.Core: " + first.DataHash);

                var second = ClassForgeChecks.LoadAll(data);
                Check.Eq(first.DataHash, second.DataHash, "dataHash across two loads");
            });

            runner.Case("merge-op ordering is stable across two loads", () =>
            {
                var second = ClassForgeChecks.LoadAll(data);
                var offenders = new List<string>();
                Compare(offenders, "Characters", first.MergePlan.Characters, second.MergePlan.Characters);
                Compare(offenders, "Things", first.MergePlan.Things, second.MergePlan.Things);
                Compare(offenders, "Abilities", first.MergePlan.Abilities, second.MergePlan.Abilities);
                Compare(offenders, "StatusEffects", first.MergePlan.StatusEffects, second.MergePlan.StatusEffects);
                Check.Empty(offenders, "merge-op ordering differences between two loads");
            });

            runner.Case("pack load order is stable across two loads", () =>
            {
                var second = ClassForgeChecks.LoadAll(data);
                Check.Eq(string.Join(",", first.EnabledOrderedPacks.Select(p => p.Id)),
                         string.Join(",", second.EnabledOrderedPacks.Select(p => p.Id)),
                         "resolved pack load order");
            });
        }

        private static void Compare(List<string> offenders, string label, List<MergeOp> a, List<MergeOp> b)
        {
            if (a.Count != b.Count) { offenders.Add(label + ": count " + a.Count + " vs " + b.Count); return; }
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal))
                    offenders.Add(label + "[" + i + "]: '" + a[i].Id + "' vs '" + b[i].Id + "'");
        }
    }
}
```

`FTK2Mods.DevKit` is the verified namespace declared at `FTK2.DevKit/src/DevKit.Core/DataHasher.cs:8` — note it does **not** match the folder or the assembly name (`ftk2mods.devkit`), so a `using DevKit.Core;` will not compile.

- [ ] **Step 2: Wire into `Program.cs`**

```csharp
            Checks.DeterminismChecks.Register(runner, data, cfResult);
```

- [ ] **Step 3: Run and verify**

Run: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
Expected: 3 PASS. Note the run is now doing 3 extra `PackLoader.Load` passes; if total runtime exceeds ~15 s, memoise the second load once and share it between the two cases.

- [ ] **Step 4: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness
git commit -m "LiveDataHarness T9: in-process determinism checks for dataHash and merge ordering"
```

---

### Task 10: JSON report, wrapper script, cross-process determinism, docs

**Files:**
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/Report.cs`
- Create: `tools/run-harness.ps1`
- Create: `FTK2.DevKit/sandbox/LiveDataHarness/README.md`
- Modify: `FTK2.DevKit/sandbox/LiveDataHarness/Program.cs`
- Modify: `README.md` (repo root — the "verification" bullet list)
- Modify: `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md` (mark automated checks)

**Interfaces:**
- Consumes: `CheckRunner.Failures` (T1).
- Produces: `Report.Write(string path, string gameRoot, int passed, int failed, IReadOnlyList<CheckFailure> failures)`.

- [ ] **Step 1: Write `Report.cs`**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LiveDataHarness
{
    /// <summary>Machine-readable run summary, so the harness can gate a future CI step or be diffed
    /// between game updates.</summary>
    public static class Report
    {
        public static void Write(string path, string gameRoot, int passed, int failed, IReadOnlyList<CheckFailure> failures)
        {
            var payload = new Dictionary<string, object>
            {
                { "gameRoot", gameRoot },
                { "passed", passed },
                { "failed", failed },
                { "failures", failures },
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }
}
```

- [ ] **Step 2: Expose pass/fail counts on `CheckRunner` and call `Report` from `Program`**

Add to `CheckRunner`: `public int Passed { get { return _passed; } }` and `public int Failed { get { return _failed; } }`.

At the end of `Program.Main`, replace `return runner.Report();` with:

```csharp
            var exit = runner.Report();

            var jsonOut = ArgValue(args, "--json");
            if (!string.IsNullOrEmpty(jsonOut))
            {
                Report.Write(jsonOut, install.Root, runner.Passed, runner.Failed, runner.Failures);
                Console.WriteLine("report: " + System.IO.Path.GetFullPath(jsonOut));
            }

            return exit;
```

- [ ] **Step 3: Write `tools/run-harness.ps1`**

Must start with a UTF-8 BOM — commit `08db46c` fixed exactly this for `deploy.ps1` (Windows PowerShell 5.1 `-File` misparses otherwise).

```powershell
<#
.SYNOPSIS
  Builds and runs LiveDataHarness against a real game install, then re-runs it in a second process
  to prove cross-process determinism of the pack dataHash.
.PARAMETER GameDir
  Optional path to the For The King II install. Omit to auto-detect.
#>
param(
    [string]$GameDir = '',
    [string]$JsonOut = 'tools/out/harness-report.json'
)

$ErrorActionPreference = 'Stop'
$proj = 'FTK2.DevKit/sandbox/LiveDataHarness'

dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$argsList = @('run', '--project', $proj, '-c', 'Release', '--no-build', '--')
if ($GameDir) { $argsList += @('--game-dir', $GameDir) }

# Pass 1 — full check suite, writes the JSON report.
& dotnet @($argsList + @('--json', $JsonOut))
$exit1 = $LASTEXITCODE

# Pass 2 — a fresh process; its report must match pass 1's on everything but timing.
$second = [System.IO.Path]::ChangeExtension($JsonOut, '.pass2.json')
& dotnet @($argsList + @('--json', $second))
$exit2 = $LASTEXITCODE

if ($exit1 -eq 2 -or $exit2 -eq 2) {
    Write-Host "LiveDataHarness SKIPPED (no game install found)."
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

Run: `pwsh -File tools/run-harness.ps1`
Expected: two passes, `reports byte-identical`, exit 0. If the reports differ, inspect the diff — a timing field or absolute path leaking into the report is a report bug (remove the field); a differing failure list is a real determinism finding.

Also run: `pwsh -File tools/run-harness.ps1 -GameDir "Z:\nope"` → expect `SKIPPED`, exit 2.

- [ ] **Step 5: Write `FTK2.DevKit/sandbox/LiveDataHarness/README.md`**

Cover, in this order: what it is (one paragraph); the one command (`pwsh -File tools/run-harness.ps1`); exit codes 0/1/2; the check inventory with one line each; **what it cannot catch** — Harmony patch application, UI injection (class-select and trait-pick lists), combat behaviour, multiplayer handshake, anything requiring a running game — with a pointer to the operator smoke script for those; and the field-map table from Task 4 with the note that `Passives` resolve against `SkillConfigs`, since that is the trap a future maintainer will otherwise re-fall into.

- [ ] **Step 6: Wire into the repo README and the operator handoff**

In root `README.md`, add to the verification-docs bullet list:

```markdown
- [FTK2.DevKit/sandbox/LiveDataHarness](FTK2.DevKit/sandbox/LiveDataHarness/README.md) — runs every mod's `.Core` logic against the real game's `Configs`, loaded out-of-process with no Unity/BepInEx/running game (`pwsh -File tools/run-harness.ps1`)
```

In `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md`, add a short subsection immediately before the numbered smoke checks stating which of them the harness now covers offline (adds-only merge, reference integrity, localization coverage, Blessings roster gate, Summoner adds-only, recipe validation, determinism) and which still require a launch. Do **not** delete any manual check — the harness reduces what must be checked by hand, it does not replace the in-game evidence for UI/combat/MP.

- [ ] **Step 7: Full green run before committing**

Run: `pwsh -File tools/run-harness.ps1`
Expected: every check PASS, exit 0. Paste the final summary line into the commit message.

- [ ] **Step 8: Commit**

```bash
git add FTK2.DevKit/sandbox/LiveDataHarness tools/run-harness.ps1 README.md docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md
git commit -m "LiveDataHarness T10: JSON report, run-harness.ps1 wrapper, cross-process determinism, docs"
```

---

## Notes for the executing session

**Parallelism.** Tasks 1 and 2 are strictly sequential (2 needs `GameData`). Task 3 needs 1. After Task 3 lands, **Tasks 4, 5, 6, 7, 8 are mutually independent** — each creates its own file under `Checks/` and appends one line to `Program.cs`. They can run concurrently in separate worktrees; the only merge conflict surface is the block of `Checks.*.Register(...)` lines in `Program.cs`, which is a trivial textual resolution. Task 9 needs 3. Task 10 needs all of them.

**Do not weaken a check to make it green.** Every check in this plan has a negative control or fixture precisely so that "it passes" means something. If a check fires against real packs, the default action is to report it, not to relax the rule. The only legitimate reason to change a rule is discovering that the rule's *mapping* is wrong (as `Passives → Abilities` was), and that discovery must be recorded in the file's XML doc.

**What this harness deliberately does not do.** It does not apply the merge to `Configs` (that is `ClassForge.Plugin.ConfigMergePatches.ApplyPlan`, a `private static` method in a net472 BepInEx-coupled assembly), does not exercise Harmony patch application, does not touch UI, combat, or multiplayer. Those need the headless-boot smoke script and the in-game TestPilot plugin — separate, larger pieces of work.
