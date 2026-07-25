# Build template notes — reference assemblies + WarBrain project conventions

*Written 2026-07-25. Status: **PARTIALLY BLOCKED** — see §0. Everything in §1–§5 that depends on
the live game install is documented from the WarBrain `.csproj` files and from an actual
`dotnet build` failure run against the (currently empty) game directory; it has **not** been
verified end-to-end against real assemblies. Re-run §6 "Unblock checklist" once the game install
finishes, then update this file.*

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

## 2. What still needs copying into `tools\bin\refs\` (once the install completes)

Minimum set (confirmed load-bearing by WarBrain's csproj + build warnings):
`FTK2.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `System.Text.Json.dll`,
`SerializedSortedDictionary.dll`, `BepInEx.dll`, `0Harmony.dll`.

Task brief additionally asks to copy liberally for future ClassForge/Summoner/DevKit plugins,
specifically UI Toolkit modules (the game uses UIElements/VisualElement) — e.g.
`UnityEngine.UIElementsModule.dll`, `UnityEngine.UIElementsNativeModule.dll`,
`UnityEngine.IMGUIModule.dll`, `UnityEngine.TextRenderingModule.dll`, plus any
`netstandard.dll`/`mscorlib.dll` facades the Managed folder ships. **These names are typical
Unity 2019+/2020+ module names, not verified against this game's actual Managed folder** — do
not treat this list as final. The correct procedure once unblocked:

```powershell
$managed = "E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed"
$bepcore = "E:\Games\Steam\steamapps\common\For The King II\BepInEx\core"
New-Item -ItemType Directory -Force "D:\src\mods\ftk2-mods\tools\bin\refs" | Out-Null
# Minimum set used today:
Copy-Item "$managed\FTK2.dll","$managed\UnityEngine.dll","$managed\UnityEngine.CoreModule.dll","$managed\System.Text.Json.dll","$managed\SerializedSortedDictionary.dll" "D:\src\mods\ftk2-mods\tools\bin\refs\"
Copy-Item "$bepcore\BepInEx.dll","$bepcore\0Harmony.dll" "D:\src\mods\ftk2-mods\tools\bin\refs\"
# Liberal set for future engines — copy every UnityEngine.*Module.dll + UI Toolkit + facades:
Copy-Item "$managed\UnityEngine.*Module.dll" "D:\src\mods\ftk2-mods\tools\bin\refs\"
Copy-Item "$managed\netstandard.dll" "D:\src\mods\ftk2-mods\tools\bin\refs\" -ErrorAction SilentlyContinue
Copy-Item "$managed\Newtonsoft.Json.dll" "D:\src\mods\ftk2-mods\tools\bin\refs\" -ErrorAction SilentlyContinue
# Then verify:
Get-ChildItem "D:\src\mods\ftk2-mods\tools\bin\refs" | Measure-Object | Select Count
```

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

## 4. Build commands (exact, as run this session)

```
dotnet --version                                          # 10.0.109
dotnet build FTK2.WarBrain/src/WarBrain.Core -c Release    # PASSED — no external refs
dotnet build FTK2.WarBrain/src/WarBrain.Plugin -c Release  # FAILED today — game dir empty (see §0)
```

`WarBrain.Core` build tail (real output, this session):
```
Restored D:\src\mods\ftk2-mods\FTK2.WarBrain\src\WarBrain.Core\WarBrain.Core.csproj (in 4.8 sec).
WarBrain.Core -> D:\src\mods\ftk2-mods\FTK2.WarBrain\src\WarBrain.Core\bin\Release\netstandard2.0\WarBrain.Core.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:07.04
```

`WarBrain.Plugin` build tail (real output, this session — expected to pass once §0 unblocks and
`-p:GameDir=...` or the live install resolves the 7 HintPaths):
```
...GameStateAdapter.cs(304,41): error CS0246: The type or namespace name 'Entity' could not be found...
...DamagePredictor.cs(27,43): error CS0246: The type or namespace name 'Entity' could not be found...
    7 Warning(s)
    82 Error(s)
```
The 7 warnings are one `MSB3245: Could not resolve this reference` per missing dll (§1 table); the
82 errors are downstream `CS0246`s in code that uses game types (`Entity`, `CombatState`,
`CombatAbilityConfig`, `AbilityAction`, `CharacterComponent`, ...) — expected cascading failures,
not a WarBrain code problem.

**When the game install completes**, re-run `dotnet build FTK2.WarBrain/src/WarBrain.Plugin -c
Release` unmodified (uses the default `$(GameDir)`) to get the "actually passes" evidence the
acceptance check requires, or `-p:GameDir=...` against a restored/verified install if the default
path ever changes.

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

## 6. Unblock checklist (run this once the Steam download/install finishes)

1. `Test-Path "E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed\FTK2.dll"` → confirm true.
2. Run the copy commands in §2 into `tools\bin\refs\`.
3. `Get-ChildItem "D:\src\mods\ftk2-mods\tools\bin\refs" | Measure-Object` → record count in this file.
4. `dotnet build FTK2.WarBrain/src/WarBrain.Plugin -c Release` (unmodified — still points at
   `$(GameDir)`, not the snapshot) → confirm it passes against the fresh install; paste the tail
   here, replacing §4's failing tail.
5. Spot-check the redirect mechanism in §3 by scaffolding one throwaway `.csproj` (not committed)
   with `FtkRefsDir` pointed at `tools\bin\refs` and confirming `dotnet build` resolves all 7
   references from the snapshot with **no game install present** (rename/hide the game folder
   temporarily, or just trust the HintPath resolution — don't actually rename the live install).
6. Update the reference table in §2 with the real Unity module DLL list once the Managed folder
   is inspectable (`Get-ChildItem $managed -Filter *.dll | Sort Name`).

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
