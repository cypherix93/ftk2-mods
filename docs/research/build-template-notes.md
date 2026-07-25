# Build template notes — reference assemblies + WarBrain project conventions

*Written 2026-07-25. Status: **UNBLOCKED** — Steam download completed same day, §6 "Unblock
checklist" run end-to-end against the real install. §2 and §4 below now hold real results (real
DLL list, real build tails for all four plugin projects — WarBrain/DevKit/Summoner pass, ClassForge
fails on a genuine source gap unrelated to refs). §0's narrative is left as-is (historical record
of the blocked state); it is no longer current.*

## 0. Blocker: game install is not present on disk right now

The task brief assumed the game directory was mid Steam-*verify* (files present, maybe locked).
That is not the actual state. Checked directly:

```
E:\Games\Steam\steamapps\common\For The King II\   → exists, but is an EMPTY directory (0 files, 2 dirs: . and ..)
E:\Games\Steam\steamapps\appmanifest_1676840.acf   → "InstalledDepots" {}  (nothing installed yet)
                                                       "BytesToDownload"  8619735280
                                                       "BytesDownloaded"  4332754864  (~50%)
                                                       "StateFlags"       1026
```

Steam (`steam.exe`, running) is mid a fresh **download**, not a verify of existing files — the acf's
`InstalledDepots` block is empty, so nothing has been staged into the game folder yet. Polled the
byte counter twice ~90s apart (PowerShell `Select-String` on the `.acf`) and once more via a
background poll (7 checks over ~140s): `BytesDownloaded` did not move from `4332754864` the entire
time — the download appears **stalled/paused**, not actively progressing. `For The King II_Data\Managed\`
and `BepInEx\core\` do not exist yet, so **no DLL could be copied and `tools\bin\refs\` is empty.**

Confirmed empirically by actually attempting the plugin build (see §4) — it fails with
`MSB3245: Could not resolve this reference` for all 7 external references, which is exactly the
expected failure mode when the HintPath targets don't exist.

**Nothing was written to the game directory** (only reads/listings were attempted; all failed
because the paths don't exist).

## 1. Reference assemblies WarBrain.Plugin needs (from the .csproj, confirmed by build warnings)

`FTK2.WarBrain/src/WarBrain.Plugin/WarBrain.Plugin.csproj` resolves every game/BepInEx reference
via `<HintPath>`, none are NuGet packages (`Private=false` on all of them — never copied to output).
Building it right now against the empty game dir reproduces exactly this warning list
(`dotnet build FTK2.WarBrain/src/WarBrain.Plugin -c Release`, `MSB3245` for each):

| Reference | Source dir property | Expected file |
|---|---|---|
| BepInEx | `$(BepInExDir)` = `$(GameDir)\BepInEx\core` | `BepInEx.dll` |
| 0Harmony | `$(BepInExDir)` | `0Harmony.dll` |
| FTK2 | `$(ManagedDir)` = `$(GameDir)\For The King II_Data\Managed` | `FTK2.dll` |
| UnityEngine | `$(ManagedDir)` | `UnityEngine.dll` |
| UnityEngine.CoreModule | `$(ManagedDir)` | `UnityEngine.CoreModule.dll` |
| System.Text.Json | `$(ManagedDir)` | `System.Text.Json.dll` |
| SerializedSortedDictionary | `$(ManagedDir)` | `SerializedSortedDictionary.dll` |

`$(GameDir)` defaults to `E:\Games\Steam\steamapps\common\For The King II` (hardcoded in
`WarBrain.Plugin.csproj`), overridable with `-p:GameDir="..."`. There is no `Directory.Build.props`
anywhere in the repo today, no `nuget.config`, no `global.json`, no `.sln` — each project is built
standalone with `dotnet build <project-dir>`.

WarBrain.Core has **zero** external references (host-agnostic by design, SPEC-mandated: "no
BepInEx, no Unity, no game refs" — see its `.csproj` comment) and builds clean with only the
`Microsoft.NETFramework.ReferenceAssemblies` NuGet package (net472-only, pulled automatically).

**Versions** (from repo docs, not independently re-verified this session since the install is
gone): BepInEx **5.4.23** + **0Harmony** (`docs/feasibility.md`, `tools/bin` README). Game build:
unknown right now — the currently-downloading build is `TargetBuildID 23771537`; whatever ships
in `Managed\FTK2.dll` after this install completes is the version to snapshot. (Prior research
docs — `docs/research/game-code-reference.md`, `battle-ai-deep-dive.md` — were extracted
2026-07-09/16 from a now-superseded install; expect drift.)

## 2. What still needs copying into `tools\bin\refs\` (RESULT — run 2026-07-25 against the completed install)

Minimum set (confirmed load-bearing by WarBrain's csproj + build warnings):
`FTK2.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `System.Text.Json.dll`,
`SerializedSortedDictionary.dll`, `BepInEx.dll`, `0Harmony.dll`.

**Important wrinkle found this run**: `BepInEx.dll`/`0Harmony.dll` do **not** exist anywhere under
the game directory — this game install has no BepInEx layer installed into it at all (confirmed:
`Test-Path "$GameDir\BepInEx\core"` → `False`). They were copied instead from the mod package
(read-only, per task brief): `` D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN\BepInEx\core ``
(also grabbed `BepInEx.Harmony.dll` from there since it's referenced by name in some project
conventions, even though no current `.csproj` HintPaths it). Version stamps: `0Harmony.dll` in that
package is 204800 bytes, `BepInEx.dll` 128512 bytes — matches the BepInEx 5.4.23 expectation from §1
(not independently re-verified via file version resource this session, just size sanity).

The game's actual `Managed\` folder was inspected (`Get-ChildItem $managed -Filter *.dll`, 205
files total). The UI Toolkit module naming guess in the previous version of this section was
**close but not exact**: the game ships `UnityEngine.UIElementsModule.dll` but there is **no**
`UnityEngine.UIElementsNativeModule.dll` (that name doesn't exist in this Unity version — don't
copy it, it'll just no-op-skip). Confirmed present and copied: `UnityEngine.IMGUIModule.dll`,
`UnityEngine.TextRenderingModule.dll`, `netstandard.dll`, `mscorlib.dll`, `Newtonsoft.Json.dll`
(the game ships Newtonsoft alongside System.Text.Json — no plugin references it via HintPath today
but it's cheap to snapshot for future engines).

Real procedure run this session (BepInEx core from the mod package, not the game dir):

```powershell
$managed = "E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed"
$bepcore = "D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN\BepInEx\core"
$refs = "D:\src\mods\ftk2-mods\tools\bin\refs"
New-Item -ItemType Directory -Force $refs | Out-Null
Copy-Item "$managed\FTK2.dll","$managed\UnityEngine.dll","$managed\UnityEngine.CoreModule.dll","$managed\System.Text.Json.dll","$managed\SerializedSortedDictionary.dll" $refs -Force
Copy-Item "$bepcore\BepInEx.dll","$bepcore\0Harmony.dll","$bepcore\BepInEx.Harmony.dll" $refs -Force
Copy-Item "$managed\UnityEngine.*Module.dll" $refs -Force
Copy-Item "$managed\UnityEngine.UI.dll" $refs -Force
Copy-Item "$managed\netstandard.dll","$managed\mscorlib.dll","$managed\Newtonsoft.Json.dll","$managed\System.Core.dll","$managed\System.dll" $refs -Force -ErrorAction SilentlyContinue
```

**Result: 80 files in `tools\bin\refs\`** (`Get-ChildItem ... | Measure-Object` → `Count: 80`),
covering the minimum set for all 4 plugin projects plus every `UnityEngine.*Module.dll` (62 of
them), `UnityEngine.UI.dll`, and the BCL/JSON facades above. Full listing not reproduced here
(gitignored, rebuildable via the block above) — spot-checked sizes match the live `Managed\`
folder exactly (e.g. `FTK2.dll` 6,572,544 bytes both places).

`tools\bin\` is already gitignored (`tools/bin/` in root `.gitignore`, documented in
`tools/README.md`'s "Machine-artifact contract": *"tools/bin/ — downloaded third-party binaries.
Gitignored."*) — no repo config changes needed for the destination.

## 3. Redirect mechanism for NEW projects to use `tools\bin\refs\` instead of the live game dir

WarBrain's own `.csproj` was **not modified** (per instructions) — it still points `$(ManagedDir)`/
`$(BepInExDir)` at `$(GameDir)` (default `E:\Games\Steam\steamapps\common\For The King II`,
override with `-p:GameDir=...`). That mechanism keeps working unchanged once the game reinstalls.

For **new** engines that want to build against the repo snapshot instead of the live game
install (so later implementation agents never need the game running/present), the documented
pattern — mirroring WarBrain's existing `HintPath` + MSBuild-property style exactly, just pointed
at the snapshot — is:

```xml
<PropertyGroup>
  <!-- tools/bin/refs is a flat folder: game Managed dlls + BepInEx core dlls together.
       Override with -p:FtkRefsDir=... to point at a different snapshot or the live game. -->
  <FtkRefsDir Condition="'$(FtkRefsDir)' == ''">$(MSBuildThisFileDirectory)..\..\..\tools\bin\refs</FtkRefsDir>
</PropertyGroup>

<ItemGroup>
  <Reference Include="FTK2">
    <HintPath>$(FtkRefsDir)\FTK2.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="UnityEngine">
    <HintPath>$(FtkRefsDir)\UnityEngine.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <!-- ...one <Reference> per dll, Private=false on all (matches WarBrain: never copy game/BepInEx
       dlls into plugin output — the game/BepInEx already provide them at runtime). -->
</ItemGroup>
```

Adjust the relative `..\..\..\` to however many levels the new project sits below repo root
(WarBrain.Plugin is 3 levels down: `FTK2.<Mod>/src/<Mod>.Plugin/`). No `Directory.Build.props` is
required (the repo has none today and this keeps the new project self-contained like WarBrain's),
but a repo-root `Directory.Build.props` defining `FtkRefsDir` once would be the natural next step
if/when a second engine needs this — **not created here**, since the task scope is
snapshot+documentation only and touching build config for other mods is out of scope.

## 4. Build commands (RESULT — real runs, 2026-07-25, against the completed install + `tools\bin\refs`)

```
dotnet --version   # 10.0.109
```

**Mechanism used for the passing builds**: none of the four projects were built against the live
game dir. All four resolved refs from the `tools\bin\refs` snapshot (§2), so future game updates
don't reflow the build:

- **WarBrain, DevKit** (both use `$(GameDir)` → `$(ManagedDir)`/`$(BepInExDir)`, no `FtkRefsDir`
  knob in their `.csproj`): built with `-p:ManagedDir="D:\src\mods\ftk2-mods\tools\bin\refs"
  -p:BepInExDir="D:\src\mods\ftk2-mods\tools\bin\refs"`. MSBuild global properties (`-p:`) override
  the project's own unconditioned `<ManagedDir>`/`<BepInExDir>` assignments, so this works without
  touching either `.csproj` and without needing a synthetic `<dir>\For The King II_Data\Managed\`
  folder tree — `tools\bin\refs` is a flat folder and both properties can point at the same flat
  folder since every `HintPath` is just `$(ManagedDir)\X.dll` / `$(BepInExDir)\Y.dll`.
- **ClassForge**: same `$(GameDir)`-style csproj, same `-p:ManagedDir=...` / `-p:BepInExDir=...`
  override — build reached the snapshot fine, then failed on an unrelated source issue (see below).
- **Summoner**: its `.csproj` already defines `FtkRefsDir` defaulting to
  `$(MSBuildThisFileDirectory)..\..\..\tools\bin\refs` (the §3 redirect pattern, pre-applied by
  whoever wrote Summoner.Plugin.csproj) — built with a **plain** `dotnet build`, no `-p:` needed.

Why not `-p:GameDir=<real game dir>` for all four: the real game dir has `Managed\` but has **no**
`BepInEx\core\` (BepInEx isn't installed into this game install — see §2), so `GameDir` alone
can't satisfy the BepInEx refs without also overriding `BepInExDir` to point elsewhere; overriding
`ManagedDir`/`BepInExDir` directly and pointing both at the single `tools\bin\refs` snapshot was
simpler than constructing a synthetic `GameDir` folder tree, and doubles as the "survives future
game updates" snapshot the task asked for.

### WarBrain.Plugin — PASS
```
dotnet build FTK2.WarBrain/src/WarBrain.Plugin -c Release -p:ManagedDir="D:\src\mods\ftk2-mods\tools\bin\refs" -p:BepInExDir="D:\src\mods\ftk2-mods\tools\bin\refs"
```
```
  Determining projects to restore...
  All projects are up-to-date for restore.
  WarBrain.Core -> D:\src\mods\ftk2-mods\FTK2.WarBrain\src\WarBrain.Core\bin\Release\netstandard2.0\WarBrain.Core.dll
  WarBrain.Plugin -> D:\src\mods\ftk2-mods\FTK2.WarBrain\src\WarBrain.Plugin\bin\Release\net472\FTK2.WarBrain.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.77
```
The 82 `CS0246` errors from the previous (blocked) run are gone — those were purely a symptom of
the 7 unresolved HintPaths cascading into "game types don't exist," not a WarBrain code defect, as
predicted in the prior version of this section.

### DevKit.Plugin — PASS
```
dotnet build FTK2.DevKit/src/DevKit.Plugin -c Release -p:ManagedDir="D:\src\mods\ftk2-mods\tools\bin\refs" -p:BepInExDir="D:\src\mods\ftk2-mods\tools\bin\refs"
```
```
  Determining projects to restore...
  All projects are up-to-date for restore.
  DevKit.Core -> D:\src\mods\ftk2-mods\FTK2.DevKit\src\DevKit.Core\bin\Release\netstandard2.0\ftk2mods.devkit.dll
  DevKit.Plugin -> D:\src\mods\ftk2-mods\FTK2.DevKit\src\DevKit.Plugin\bin\Release\net472\FTK2.DevKit.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:01.02
```

### Summoner.Plugin — PASS
```
dotnet build FTK2.Summoner/src/Summoner.Plugin -c Release
```
```
  Determining projects to restore...
  All projects are up-to-date for restore.
  Summoner.Core -> D:\src\mods\ftk2-mods\FTK2.Summoner\src\Summoner.Core\bin\Release\netstandard2.0\Summoner.Core.dll
  Summoner.Plugin -> D:\src\mods\ftk2-mods\FTK2.Summoner\src\Summoner.Plugin\bin\Release\net472\FTK2.Summoner.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:00.94
```

### ClassForge.Plugin — FAIL (genuine source gap, NOT a ref-path issue — not fixed, per task scope)
```
dotnet build FTK2.ClassForge/src/ClassForge.Plugin -c Release -p:ManagedDir="D:\src\mods\ftk2-mods\tools\bin\refs" -p:BepInExDir="D:\src\mods\ftk2-mods\tools\bin\refs"
```
```
  Determining projects to restore...
  All projects are up-to-date for restore.
  ClassForge.Core -> D:\src\mods\ftk2-mods\FTK2.ClassForge\src\ClassForge.Core\bin\Release\netstandard2.0\ClassForge.Core.dll
D:\src\mods\ftk2-mods\FTK2.ClassForge\src\ClassForge.Plugin\ConfigMergePatches.cs(99,49): error CS0012: The type 'ReadOnlySpan<>' is defined in an assembly that is not referenced. You must add a reference to assembly 'System.Memory, Version=4.0.1.2, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51'. [D:\src\mods\ftk2-mods\FTK2.ClassForge\src\ClassForge.Plugin\ClassForge.Plugin.csproj]
D:\src\mods\ftk2-mods\FTK2.ClassForge\src\ClassForge.Plugin\ConfigMergePatches.cs(109,45): error CS0012: The type 'ReadOnlySpan<>' is defined in an assembly that is not referenced. You must add a reference to assembly 'System.Memory, Version=4.0.1.2, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51'. [D:\src\mods\ftk2-mods\FTK2.ClassForge\src\ClassForge.Plugin\ClassForge.Plugin.csproj]
D:\src\mods\ftk2-mods\FTK2.ClassForge\src\ClassForge.Plugin\ConfigMergePatches.cs(119,48): error CS0012: The type 'ReadOnlySpan<>' is defined in an assembly that is not referenced. You must add a reference to assembly 'System.Memory, Version=4.0.1.2, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51'. [D:\src\mods\ftk2-mods\FTK2.ClassForge\src\ClassForge.Plugin\ClassForge.Plugin.csproj]

Build FAILED.
    0 Warning(s)
    3 Error(s)
Time Elapsed 00:00:00.94
```
Root cause: `ConfigMergePatches.cs` uses `ReadOnlySpan<char>` directly (net472 has no built-in
`ReadOnlySpan`; it needs `System.Memory.dll`, which the live `Managed\` folder does ship), but
`ClassForge.Plugin.csproj` has **no** `<Reference Include="System.Memory">` entry at all — not a
missing-file problem (the DLL exists both in the game's `Managed\` and could be copied to
`tools\bin\refs`), and not a HintPath-resolution problem (`ClassForge.Core` restored and built
fine as a dependency). This is a missing `<Reference>` item in the `.csproj` — fixing it means
editing `.csproj` source, which is out of scope for this unit (task constraint: don't touch `.cs`/
`.csproj`, capture and report instead). **Not fixed here** — flagged for the orchestrator.

## 5. Test / sim harness — exact commands (both ran clean this session, no game deps)

WarBrain has no `dotnet test` project (no xunit/nunit test project exists yet). Its verification
harness is the **combat simulator**, a plain console app (net10.0, no game/BepInEx references —
only depends on `WarBrain.Core`):

```
dotnet run --project FTK2.WarBrain/sandbox/WarBrainSim -c Release -- 50
```
Output (real, this session):
```
Loaded 4 profiles, 3 doctrines from D:\src\mods\ftk2-mods\FTK2.WarBrain\data
Wrote results.md
```
Note: `results.md` is written **relative to the process's current working directory**, not the
project directory — running from repo root produces `D:\src\mods\ftk2-mods\results.md` (a stray
file, deleted after this check; the committed `FTK2.WarBrain/sandbox/WarBrainSim/results.md` is
the 200-battles/cell run committed separately and was untouched by this run). Future agents:
either `cd` into `FTK2.WarBrain/sandbox/WarBrainSim` first, or pass an absolute path as the second
arg, e.g. `dotnet run --project FTK2.WarBrain/sandbox/WarBrainSim -c Release -- 2000 FTK2.WarBrain/sandbox/WarBrainSim/results.md`.

Args: `dotnet run -c Release -- [battles-per-cell] [outfile]` (defaults 2000 / `results.md`).

`tools/tests` (pytest) is unrelated — that's the Python content-generation tooling
(`python -m pytest tools/tests -v`), not part of the C# build template.

## 6. Unblock checklist — RESULT (run 2026-07-25 against the completed install)

1. **DONE.** `Test-Path .../Managed/FTK2.dll` → `True`. Additionally verified install completeness
   beyond the checklist's ask: `appmanifest_1676840.acf` → `StateFlags 4`, `BytesDownloaded ==
   BytesToDownload` (8,619,735,280 both), `InstalledDepots` populated (depot `1676841`, non-empty
   manifest/size). `FTK2.dll` size (6,572,544 bytes) re-checked 30s apart, unchanged — install is
   complete and stable, not still-writing.
2. **DONE.** Copy commands run — see §2 for the exact commands and the BepInEx-source wrinkle
   (core dlls came from the mod package, not the game dir, since the game dir has no BepInEx layer
   installed).
3. **DONE.** `Get-ChildItem tools\bin\refs | Measure-Object` → **Count: 80**.
4. **DONE, with a mechanism change from the plan.** A plain `dotnet build
   FTK2.WarBrain/src/WarBrain.Plugin -c Release` (unmodified, default `$(GameDir)`) was tried for
   comparison and **fails** — 27 `CS0246`s rooted in unresolved `BepInEx`/`Harmony` types, because
   the live game dir has `Managed\` but no `BepInEx\core\` (see §2). The passing build instead used
   `-p:ManagedDir=...\tools\bin\refs -p:BepInExDir=...\tools\bin\refs` (both pointed at the same
   snapshot folder) — see §4 for the full tail and rationale. This is arguably a *better* result
   than the checklist's literal ask, since it's the snapshot-based build the task brief wanted
   documented ("document which path you used for the passing builds").
5. **Effectively done, via the real projects rather than a throwaway csproj.** Summoner.Plugin's
   `.csproj` already implements the exact `FtkRefsDir` redirect pattern from §3 and built clean
   with a plain `dotnet build` (no `-p:` flags, no game dir involved at all) — see §4. That's a
   stronger proof than a scaffolded throwaway project would have been, since it's real shipped
   source using the pattern end-to-end.
6. **DONE.** §2 now has the real 205-file `Managed\` inventory summary and the corrected UI
   Toolkit module name (`UnityEngine.UIElementsModule.dll` exists; the previously-guessed
   `UnityEngine.UIElementsNativeModule.dll` does not).

## 7. WarBrain project-layout conventions (verified, no blocker — new engines should copy these)

**Folder layout** (`FTK2.WarBrain/`, mirrors `docs/CONVENTIONS.md` "Repo layout (per mod)"):
```
FTK2.<ModName>/
  SPEC.md                    # design spec
  README.md                  # build/install/knobs summary
  REPORT.md                  # optional: discovery/validation writeup (WarBrain-specific, not a required convention)
  data/                      # JSON registries, ships with the mod
    Profiles/*.brain.json
    Doctrines/*.doctrine.json
    Assignments/*.assignment.json
    MemoryReactions/*.reactions.json
  src/
    <ModName>.Core/           # netstandard2.0, host-agnostic engine (no BepInEx/Unity/game refs)
    <ModName>.Plugin/         # net472, BepInEx 5 plugin — Harmony patches + game adapter
  sandbox/<ModName>Sim/        # optional: net10.0 console harness, references only *.Core
```

**Namespaces**: `<ModName>.Core`, `<ModName>.Plugin` (root namespace matches project name, set
via `<RootNamespace>` in the csproj).

**Plugin GUID pattern**: `ftk2mods.<modname>` (lowercase), e.g. `ftk2mods.warbrain` — declared as
`public const string Guid = "ftk2mods.warbrain";` and used both in `[BepInPlugin(Guid, Name,
Version)]` and `new Harmony(Guid)`. Matches `docs/CONVENTIONS.md` §Naming exactly.

**Data id prefix pattern**: uppercase-underscore, per-mod prefix — `WB_*` for WarBrain (e.g.
`WB_BRAIN_BRUTE`, `WB_DOCTRINE_SWARM`). Full prefix table lives in `docs/CONVENTIONS.md`.

**Logging pattern** (`WarBrainPlugin.cs`, `AiDecisionPatches.cs`):
- `internal static ManualLogSource Log = Logger;` set in `Awake()`, referenced everywhere as
  `WarBrainPlugin.Log`.
- Harmony patch install logs one line per target: `Log.LogInfo($"Target found: {type.Name}.{method}")`
  or `Log.LogError($"Target NOT found: ...")` if `AccessTools.Method` returns null — EOR-style, so
  breakage after a game update is diagnosable from the BepInEx console without a debugger.
- Startup summary line: `Log.LogInfo($"{Name} {Version} loaded. Enabled={...}, ..., dataHash={DataHash}")`.
- Fail-safe try/catch around every patched decision path: catch `Exception`, `Log.LogWarning` with
  the entity/context and exception, then `return true` (defer to vanilla) unless a `FailSafeOnError`
  knob is off. Every data-load call site (`TryLoad` in `WarBrainPlugin.LoadData`) is similarly
  wrapped: one bad JSON file logs and is skipped, everything else still loads.
- `VerboseLogging`/`LogDecisionBreakdown` config knobs gate per-turn debug logs (matches
  `docs/CONVENTIONS.md` §Testing: "Engines log decisions at `LogLevel.Debug` behind a
  `VerboseLogging` knob").

**Config knob pattern**: `Config.Bind("<Section>", "<Key>", <default>, "<description>")` in
`Awake()`, sections grouped by concern (`General`, `Subsystems`, `Difficulty`, `Assignments`,
`Safety`, `Scaling` in WarBrain) — every engine has a master `[General] Enabled` bool knob (SPEC-
mandated master switch, see `docs/CONVENTIONS.md`).

**Harmony patch pattern**: manual `AccessTools.Method(type, method[, argumentTypes])` +
`harmony.Patch(target, prefix:, postfix:)`, wrapped in a local `Patch(...)` helper that logs
found/not-found (see `ApplyPatches()` in `WarBrainPlugin.cs`).

**Data hot-reload + hash pattern**: `LoadData()` is re-entrant (called from `Awake` and from a
`ConfigsHelper.ReloadConfigs` Harmony postfix), builds a `SHA256` hash over every loaded file's
name+contents for MP-parity logging (`DataHash`), and is fully rebuild-from-scratch each call
(no incremental merge) so hot-reload can't leave stale entries.
