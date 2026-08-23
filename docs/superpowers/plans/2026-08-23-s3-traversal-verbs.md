# S3 Traversal Verbs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the harness the ability to *move between game states*. Today it can execute the game's own 21 shipped console commands and none of them change screens — so it cannot choose a campaign, cannot reach party management, cannot start a run, and is stranded wherever the game happens to boot (currently, per the owner's report, the multiplayer lobby). This plan lands eleven console commands and eleven MCP tools that turn the game's own navigation primitives into a gated, post-condition-checked verb layer, ordered so **navigation lands first** because it unblocks everything else.

**Architecture:** No new machinery. `FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs` already registers Crucible-owned commands into `CommandLineHelper` from the `RouterMono.Update` retry loop, already resolves any Director instance via three strategies (static `Instance`/`Current`, `FindObjectOfType`, private field on the live `RouterMono`), and already stashes rendered results in `ReflectionCommands.LastResult` for `RpcServer.HandleExec` to return. This plan adds a sibling file, `TraversalCommands.cs`, that registers typed verbs through the identical path, plus a `RouteGate` addition to `Crucible.Core` so the safety layer cannot lock the harness out of its own escape hatch. Every verb reports a **post-condition read**, never a dispatch receipt.

**Tech Stack:** C# · `net472` (plugin) / `netstandard2.0` (core) · `LangVersion 7.3`, `ImplicitUsings disable`, `Nullable disable` · HarmonyLib `AccessTools` reflection only, no compile-time game reference · Node `node:http` + stdio JSON-RPC for MCP, no npm deps.

**Spec:** `docs/superpowers/specs/2026-08-23-crucible-autopilot-design.md` (§2 grounding rule, §3 error posture, §5 verb layer).
**Evidence:** `docs/research/crucible-traversal-inventory.md` (this plan's direct input), `crucible-load-path.md`, `crucible-quest-progression.md`, `crucible-verb-feasibility.md`, `crucible-combat-field-map.md`.

## Global Constraints

- **The `CommandLineHelper` marshaller vocabulary is `int / float / double / bool / string / Vector2 / Vector3`. Nothing else.** Verified in-game 2026-08-23: registering a `string[]` handler throws `TargetInvocationException` while `RegisterCommand` inspects the `MethodInfo` (`ReflectionCommands.cs` XML doc). **Enums are NOT in that vocabulary** — `eRoutes` parameters must be declared `string` on the handler and parsed to the enum inside the body. Every handler in this plan takes only `string`, `int` and `bool`.
- **`ok` must never mean "dispatched" (spec §3).** `MultiplayerDemoQuickCombat` is registered, callable, returns `ok: true`, and throws `NotImplementedException` internally. Every verb here reads a post-condition after acting and puts it in `LastResult`.
- **Reflection failures degrade to a reported error, never an exception.** Matches `StateReader` / `GameBridge` / `ReflectionCommands` posture already in the repo.
- **No NuGet `PackageReference`.** BCL + `ProjectReference` only.
- **No compile-time reference to `FTK2.dll` / `UnityEngine*.dll`.** All game access reflective.
- **No npm dependency in `FTK2.Crucible/mcp/server.js`.**
- **Every check has a negative control.** A test that cannot fail is a plan failure.
- **Grounding rule (spec §2):** no game member name enters code until dumped from the retail assembly. Every name used below is quoted in `docs/research/crucible-traversal-inventory.md` from a TypeProbe run. Where behaviour rather than existence is inferred, the task marks it **ASSUMED** and the step says what to do if it is wrong.
- **`eRoutes.EXIT` is deny-listed in the route verb.** `Route(EXIT, …)` terminates the process. One mistyped argument would end an overnight run.
- **The game directory is read-only except for the plugin DLL deploy.**
- **Live-test target:** one instance, launched with Steam running, `[General] Enabled=true` and `[Rpc] Enabled=true` in `BepInEx/config`. Two instances on one machine do not work (spec §0).

## File Structure

| File | Responsibility |
|---|---|
| `FTK2.Crucible/src/Crucible.Core/RouteNames.cs` | **Pure** — the `eRoutes` name list, the `EXIT` deny-list, and `TryNormalize(string, out string, out string)`. No Unity, no game. Unit-testable. |
| `FTK2.Crucible/src/Crucible.Core/CommandRequest.cs` | Modify: extend `CommandGate.ReadOnlyCommands` with the navigation verb names so the fail-closed MP gate cannot trap the harness. |
| `FTK2.Crucible/src/Crucible.Plugin/GameOwners.cs` | Resolve the live `RouterMono`, the current `eRoutes`, and the Director/Phase that owns it. One place, so eleven verbs do not each re-derive it. |
| `FTK2.Crucible/src/Crucible.Plugin/ModalWatch.cs` | Read the four "am I wedged" oracles and the one console oracle; force-dismiss on request. |
| `FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs` | The eleven `crucible_*` handlers plus their registration. |
| `FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs` | Modify: `TryRegister()` also calls `TraversalCommands.TryRegister()`. |
| `FTK2.Crucible/mcp/server.js` | Modify: eleven new tools plus `ftk2_wait_route` (polling lives on the Node side — it must never block the game's main thread). |
| `FTK2.Crucible/src/Crucible.Core.Tests/RouteNameTests.cs` | Tests + negative controls for `RouteNames` and the widened `CommandGate`. |
| `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs` | Modify: call `RouteNameTests.Run()`. |

The split matters for the same reason it did in S0: `RouteNames` is pure, so it can be driven with deliberately wrong inputs — which is what makes the negative controls possible. `GameOwners` and `ModalWatch` are the only files that touch the game, and they are deliberately thin.

---

## Milestones

| Milestone | Tasks | Unblocks |
|---|---|---|
| **M1 — Navigation** | 1, 2, 3 | Everything. Without it the harness cannot leave the screen it booted on. |
| **M2 — Run lifecycle** | 4, 5 | Choosing a campaign, composing a party, starting and ending a run. The 35-class matrix. |
| **M3 — Phase advancement** | 6 | Walking a run forward through venue / dungeon / encounter / rest / treasure / trap / wheel / fortune. |
| **M4 — Combat** | 7 | S4 scenarios that assert on combat. |
| **M5 — MCP surface + unattended hygiene** | 8, 9 | Everything above becoming usable from an agent, and surviving overnight. |

---

## The command set

Eleven commands. Not twenty. Each row's "API" column is quoted verbatim from `docs/research/crucible-traversal-inventory.md`; **CONFIRMED** means TypeProbe printed that exact signature, **ASSUMED** means the signature is confirmed but the *behaviour* is inferred from the name or the wiring.

| # | Command | Parameters | Game API it calls | Status |
|---|---|---|---|---|
| 1 | `crucible_where` | *(none)* | `RouterHelper.GetCurrentRoute()`; `SystemDialogViewHelper.IsShowing()`; `PromptViewHelper.PromptIsShowing()`; `DialogueViewHelper.IsShowing()`; `TransitionViewHelper.IsShowing()`; `CommandLineViewHelper.IsShowing()` | **CONFIRMED** (all six) |
| 2 | `crucible_route` | `string pRoute`, `bool pForce` | `RouterMono.Route(eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData, Boolean pReload, Boolean pForceRoute)` | signature **CONFIRMED**; sufficiency for menu-weight routes **ASSUMED** |
| 3 | `crucible_dismiss` | `string pWhich` | `SystemDialogViewHelper.ForceHide(Boolean)`; `PromptViewHelper.ClosePrompt()`; `TransitionViewHelper.HideTransition()`; `DialogueViewHelper.ReleaseInput()` | **CONFIRMED** (all four) |
| 4 | `crucible_mp_detach` | `bool pHardDisconnect` | `Env.RoomAutoJoinId` (field write); `NetworkHelper.CanAutoJoinRoom` (field write); `NetworkHelper.DisconnectFromRoomAndResetNetworkData(NetworkData, UserData)` | fields + signature **CONFIRMED**; effect **ASSUMED** |
| 5 | `crucible_runs` | *(none)* | `Env.GameRuns`; `GameSaveDirector+GameSaveData.{runID, saveName, adventureType, difficulty, roundCount, dateTime}`; `UserData.LastGameRunIdPlayed` | **CONFIRMED** |
| 6 | `crucible_load_run` | `string pRunIdOrFile` | `AdventureSelectionDirector._loadGameRun(String pFileName, String pDisableLogMessage)` | signature **CONFIRMED**; that this alone carries the route to `ADVENTURE` **ASSUMED** |
| 7 | `crucible_new_run` | `string pAdventureId`, `string pDifficulty` | `Env.SelectedAdventureConfig` (field write); `Env.SelectedDifficulty` (field write); `AdventureSelectionDirector._initializeAdventureInfo(String pSelected)`; `._onNodeStartAdventureClick(NavigationSubmitEvent pEvent)` | fields + signatures **CONFIRMED**; passing `null` for the UI event **ASSUMED** |
| 8 | `crucible_party_class` | `int pSlot`, `string pClassConfigName` | `PartyManagementDirector._playerEntities` (field read, indexed); `._rebuildCharactertAsNewConfigType(Entity, String pClassConfigName, Boolean pOnlineAction, Boolean pTryPlayReadyUp, Boolean pIsPreset)` | signature **CONFIRMED**; that a bare call outside the UI flow rebuilds correctly **ASSUMED** |
| 9 | `crucible_party_begin` | *(none)* | `PartyManagementDirector._initAdventureAndRoute(Boolean pOnlineAction)` | signature **CONFIRMED**; that it is the whole "Begin Adventure" step **ASSUMED** |
| 10 | `crucible_advance` | `string pMode`, `int pArg` | dispatches on the live owner: `CombatPhase._debugEndPhase()` · `EncounterPhase._debugEndPhase()` · `FortunePhase/TreasurePhase/TrapPhase/WheelPhase._debugEndPhase()` · **`RestPhase._debugEndPhase(Int32 pOption)`** · `AdventureDirector._doEndTurn()` · `DungeonDirector._nextPhase()` · `<owner>._goToNextRoute(eRoutes)` | all seven **CONFIRMED** |
| 11 | `crucible_end_run` | `bool pIsVictory` | `<live owner>._endAdventure(Boolean pIsVictory)` (present on nine owners); fallback `<live owner>.QuitToMenu()` | **CONFIRMED** |

**Deliberately not in this plan, and why.** `use_ability` needs a `CombatDecisionData` the harness cannot build from console primitives — `CombatPhase._performAbility(Entity, Thing, CombatDecisionData, List`1, Boolean, Boolean)` is the right target (correcting `crucible-verb-feasibility.md`, which nominated the delegate-taking `CombatHelper.PerformAbility`) but it belongs to a combat-verbs plan with its own grounding pass. `force_win` / `force_lose` are **not** buildable from `CombatHelper.TryEndGame(CombatState)` — that signature carries no victory flag (see inventory §0) — so they collapse into `crucible_end_run <bool>` at run scope and are dropped at combat scope. `jump_encounter`, `set_terrain`, `advance_days` and `teleport` all require `Entity` / `Thing` / `EncounterGenData` / `ValueTuple` arguments that no marshallable parameter list can supply; they need an entity-handle scheme this plan does not invent. **Nine of eleven commands here call an API whose exact signature is quoted; the two riskiest (`crucible_new_run`, `crucible_party_begin`) are isolated in their own task with an explicit fallback.**

---

### Task 1: `RouteNames` + widen `CommandGate` (pure, no game)

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/RouteNames.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/RouteNameTests.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core/CommandRequest.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `static string[] FTK2Mods.Crucible.RouteNames.All`
  - `static bool FTK2Mods.Crucible.RouteNames.TryNormalize(string raw, out string normalized, out string error)`
  - `CommandGate.IsReadOnly(name)` returns `true` for the six existing names **plus** `crucible_where`, `crucible_route`, `crucible_dismiss`, `crucible_mp_detach`, `crucible_runs`.

**Why the gate widens.** `GameBridge.IsOnlineSession()` fails closed — it returns `true` when it cannot read `NetworkData.PlayingOnlineMultiplayer`. `CommandGate.Evaluate` then denies everything not on `ReadOnlyCommands`. A harness that boots into the multiplayer lobby would therefore have its own escape verbs refused. Navigation and observation change no game state that can desync a peer; they must be on the read-only list. **`crucible_load_run`, `crucible_new_run`, `crucible_party_class`, `crucible_party_begin`, `crucible_advance` and `crucible_end_run` deliberately stay OFF the list** — those do mutate a shared run.

- [ ] **Step 1: Write the failing tests**

`FTK2.Crucible/src/Crucible.Core.Tests/RouteNameTests.cs`:

```csharp
using System;
using FTK2Mods.Crucible;

namespace Crucible.Core.Tests
{
    internal static class RouteNameTests
    {
        internal static void Run()
        {
            TestHarness.Section("RouteNames");

            TestHarness.Run("All contains every eRoutes value dumped by TypeProbe", delegate
            {
                string[] expected = new string[]
                {
                    "ADVENTURE", "ADVENTURE_SELECTION", "CINEMATIC_CREDITS", "CINEMATIC_INTRO",
                    "CINEMATIC_OUTRO", "COMBAT", "DUNGEON", "ENCOUNTER", "EXIT", "FORTUNE",
                    "INTRO", "LORE_STORE", "MAIN_MENU", "MULTIPLAYER_LOBBY", "NONE",
                    "PARTY_MANAGEMENT", "REST", "TRAP", "TREASURE", "VENUE", "WHEEL"
                };
                TestHarness.Assert(RouteNames.All.Length == expected.Length,
                    "expected " + expected.Length + " routes, got " + RouteNames.All.Length);
                for (int i = 0; i < expected.Length; i++)
                {
                    bool found = false;
                    for (int j = 0; j < RouteNames.All.Length; j++)
                        if (RouteNames.All[j] == expected[i]) { found = true; break; }
                    TestHarness.Assert(found, "missing route: " + expected[i]);
                }
            });

            TestHarness.Run("TryNormalize accepts a lowercase name", delegate
            {
                string n, e;
                TestHarness.Assert(RouteNames.TryNormalize("main_menu", out n, out e), "should accept");
                TestHarness.Assert(n == "MAIN_MENU", "expected MAIN_MENU, got " + n);
            });

            TestHarness.Run("TryNormalize trims surrounding whitespace", delegate
            {
                string n, e;
                TestHarness.Assert(RouteNames.TryNormalize("  COMBAT  ", out n, out e), "should accept");
                TestHarness.Assert(n == "COMBAT", "expected COMBAT, got " + n);
            });

            // ---- negative controls ----

            TestHarness.Run("NEGATIVE: EXIT is refused by name", delegate
            {
                string n, e;
                TestHarness.Assert(!RouteNames.TryNormalize("EXIT", out n, out e), "EXIT must be refused");
                TestHarness.Assert(e != null && e.IndexOf("EXIT", StringComparison.Ordinal) >= 0,
                    "error must name EXIT, got: " + e);
            });

            TestHarness.Run("NEGATIVE: EXIT is refused case-insensitively too", delegate
            {
                string n, e;
                TestHarness.Assert(!RouteNames.TryNormalize("exit", out n, out e), "lowercase exit must be refused");
            });

            TestHarness.Run("NEGATIVE: an invented route is refused, not silently defaulted", delegate
            {
                string n, e;
                TestHarness.Assert(!RouteNames.TryNormalize("TOWN", out n, out e), "TOWN is not an eRoutes value");
                TestHarness.Assert(n == null, "normalized must be null on failure, got " + n);
            });

            TestHarness.Run("NEGATIVE: null and empty are refused", delegate
            {
                string n, e;
                TestHarness.Assert(!RouteNames.TryNormalize(null, out n, out e), "null must be refused");
                TestHarness.Assert(!RouteNames.TryNormalize("", out n, out e), "empty must be refused");
            });

            TestHarness.Section("CommandGate — navigation verbs survive the fail-closed MP gate");

            TestHarness.Run("navigation verbs are allowed while 'online'", delegate
            {
                string[] nav = new string[]
                { "crucible_where", "crucible_route", "crucible_dismiss", "crucible_mp_detach", "crucible_runs" };
                for (int i = 0; i < nav.Length; i++)
                {
                    GateVerdict v = CommandGate.Evaluate(nav[i], true, false, new string[0]);
                    TestHarness.Assert(v == GateVerdict.Allow, nav[i] + " must be allowed online, got " + v);
                }
            });

            TestHarness.Run("NEGATIVE: mutating verbs are still denied while 'online'", delegate
            {
                string[] mutating = new string[]
                { "crucible_load_run", "crucible_new_run", "crucible_party_class",
                  "crucible_party_begin", "crucible_advance", "crucible_end_run" };
                for (int i = 0; i < mutating.Length; i++)
                {
                    GateVerdict v = CommandGate.Evaluate(mutating[i], true, false, new string[0]);
                    TestHarness.Assert(v == GateVerdict.DeniedMultiplayer,
                        mutating[i] + " must be denied online, got " + v);
                }
            });

            TestHarness.Run("NEGATIVE: an explicit allowlist still overrides a navigation verb", delegate
            {
                GateVerdict v = CommandGate.Evaluate("crucible_route", false, false, new string[] { "EndPhase" });
                TestHarness.Assert(v == GateVerdict.DeniedNotAllowlisted,
                    "allowlist must win over the read-only list, got " + v);
            });
        }
    }
}
```

Run them; they must fail to compile (`RouteNames` does not exist). That failure is the proof the tests are wired in.

- [ ] **Step 2: Write `RouteNames`**

`FTK2.Crucible/src/Crucible.Core/RouteNames.cs`:

```csharp
using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// The <c>eRoutes</c> vocabulary, as a pure string list with no game reference.
    ///
    /// Every name here is quoted from a TypeProbe dump of the retail <c>eRoutes</c> enum recorded in
    /// docs/research/crucible-traversal-inventory.md §1 — not from memory, per spec §2.
    ///
    /// Kept in Core (netstandard2.0, no Unity) so the parse/deny logic unit-tests with no game
    /// running. The plugin converts the normalized name to the real enum with
    /// <c>Enum.Parse(AccessTools.TypeByName("eRoutes"), name)</c>; this type never touches it.
    ///
    /// EXIT is deny-listed by design: <c>RouterMono.Route(EXIT, ...)</c> terminates the process, and
    /// an unattended run must not be endable by one mistyped argument.
    /// </summary>
    public static class RouteNames
    {
        /// <summary>Routing target that shuts the game down. Never reachable through a verb.</summary>
        public const string Forbidden = "EXIT";

        public static readonly string[] All = new string[]
        {
            "NONE", "INTRO", "EXIT", "MAIN_MENU", "ADVENTURE_SELECTION", "LORE_STORE",
            "MULTIPLAYER_LOBBY", "PARTY_MANAGEMENT", "ADVENTURE", "VENUE", "DUNGEON", "COMBAT",
            "REST", "TREASURE", "TRAP", "ENCOUNTER", "WHEEL", "FORTUNE",
            "CINEMATIC_INTRO", "CINEMATIC_OUTRO", "CINEMATIC_CREDITS"
        };

        /// <summary>
        /// Uppercases and trims a caller-supplied route name, verifies it is a real eRoutes value,
        /// and refuses EXIT. Never silently defaults: an unknown name is an error, not NONE.
        /// </summary>
        public static bool TryNormalize(string raw, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            if (raw == null) { error = "route name is null"; return false; }
            string candidate = raw.Trim().ToUpperInvariant();
            if (candidate.Length == 0) { error = "route name is empty"; return false; }

            if (string.Equals(candidate, Forbidden, StringComparison.Ordinal))
            {
                error = "route EXIT is refused: it terminates the game process";
                return false;
            }

            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i], candidate, StringComparison.Ordinal))
                {
                    normalized = candidate;
                    return true;
                }
            }

            error = "not an eRoutes value: " + raw;
            return false;
        }
    }
}
```

- [ ] **Step 3: Widen `CommandGate.ReadOnlyCommands`**

In `FTK2.Crucible/src/Crucible.Core/CommandRequest.cs`, replace the `ReadOnlyCommands` initializer with:

```csharp
        /// <summary>
        /// Commands that cannot desync a peer, and therefore survive the fail-closed MP gate.
        ///
        /// The crucible_* entries matter for a specific failure: GameBridge.IsOnlineSession() returns
        /// TRUE when it cannot read NetworkData.PlayingOnlineMultiplayer, so a harness that boots into
        /// the multiplayer lobby would otherwise have its own escape verbs denied and be stranded
        /// there. Navigation and observation change no shared run state.
        ///
        /// Deliberately absent: crucible_load_run, crucible_new_run, crucible_party_class,
        /// crucible_party_begin, crucible_advance, crucible_end_run. Those DO mutate a shared run.
        /// </summary>
        private static readonly string[] ReadOnlyCommands = new string[]
        {
            "toggleui", "togglevenuegrid", "toggleplayerhuds",
            "printdungeonconfighash", "printdialogueconfighash",
            "enablegamerandomstacktracerecording",
            "crucible_where", "crucible_route", "crucible_dismiss",
            "crucible_mp_detach", "crucible_runs"
        };
```

`IsReadOnly` already lowercases the incoming name and compares ordinally, so the lowercase literals are correct as written.

- [ ] **Step 4: Wire the tests in**

In `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`, add `RouteNameTests.Run();` alongside the existing suite calls, before `TestHarness.Report()`.

- [ ] **Step 5: Verify**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, and the printed test count is **36 + 10 = 46** (36 was the count recorded on 2026-08-23). Six of the ten new ones are negative controls. If the count is not 46, the new file is not being compiled — check that it sits under `src/Crucible.Core.Tests/` where the SDK globs it.

---

### Task 2: `GameOwners` + `ModalWatch` — resolve the live owner and read the wedge oracles

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/GameOwners.cs`
- Create: `FTK2.Crucible/src/Crucible.Plugin/ModalWatch.cs`

**Interfaces:**
- Consumes: `HarmonyLib.AccessTools`.
- Produces:
  - `static object GameOwners.Router()` — the live `RouterMono`, or null
  - `static string GameOwners.CurrentRoute()` — e.g. `"MAIN_MENU"`, or null
  - `static bool GameOwners.TryCurrentOwner(out object owner, out string ownerType, out string error)` — the Director/Phase that owns the current route
  - `static bool GameOwners.TryOwnerOfType(string typeName, out object owner, out string error)` — a named Director, whether or not it is current
  - `static object GameOwners.Env()` — the live `Env`
  - `static string ModalWatch.Describe()` — a compact one-line render of all five oracles
  - `static bool ModalWatch.AnyBlocking()` — true if a modal, prompt, dialogue or transition is up

**Grounding (all quoted in `docs/research/crucible-traversal-inventory.md` §1, §2, §5):**

| Need | Member |
|---|---|
| current route | `RouterHelper.GetCurrentRoute()` → `eRoutes` |
| route → owner | `RouterMono` private fields `_mainMenuDirector`, `_adventureSelectionDirector`, `_partyManagementDirector`, `_adventureDirector`, `_venueDirector`, `_dungeonDirector`, `_combatPhase`, `_restPhase`, `_treasurePhase`, `_trapPhase`, `_encounterPhase`, `_wheelPhase`, `_fortunePhase`, `_introPhase`, `_loreStoreDirector`, `_multiplayerLobbyDirector` |
| Env | `RouterHelper.Env` (property) — already read by `GameBridge.GetEnv()` |
| modal | `SystemDialogViewHelper.IsShowing()` → `Boolean` |
| prompt | `PromptViewHelper.PromptIsShowing()` → `Boolean` |
| dialogue | `DialogueViewHelper.IsShowing()` → `Boolean` |
| transition | `TransitionViewHelper.IsShowing()` → `Boolean` |
| console | `CommandLineViewHelper.IsShowing()` — already wired as `GameBridge.IsConsoleShowing()` |
| dismiss | `SystemDialogViewHelper.ForceHide(Boolean pHideNoControllerDialog)`, `PromptViewHelper.ClosePrompt()`, `TransitionViewHelper.HideTransition()`, `DialogueViewHelper.ReleaseInput()` |

- [ ] **Step 1: Write `GameOwners`**

`FTK2.Crucible/src/Crucible.Plugin/GameOwners.cs`:

```csharp
using System;
using System.Reflection;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// One place that answers "what route are we on and who owns it".
    ///
    /// RouterMono holds every scene owner as a private field (docs/research/
    /// crucible-traversal-inventory.md §1), so owner resolution is a field read on the live
    /// RouterMono rather than a per-Director singleton hunt. ReflectionCommands.TryResolveInstance
    /// already implements the generic three-strategy hunt; this type is the route-aware version the
    /// traversal verbs need, and it never throws.
    /// </summary>
    internal static class GameOwners
    {
        /// <summary>Route name -> the RouterMono private field holding that route's owner.</summary>
        private static readonly string[,] OwnerFieldByRoute = new string[,]
        {
            { "INTRO",               "_introPhase" },
            { "MAIN_MENU",           "_mainMenuDirector" },
            { "ADVENTURE_SELECTION", "_adventureSelectionDirector" },
            { "LORE_STORE",          "_loreStoreDirector" },
            { "MULTIPLAYER_LOBBY",   "_multiplayerLobbyDirector" },
            { "PARTY_MANAGEMENT",    "_partyManagementDirector" },
            { "ADVENTURE",           "_adventureDirector" },
            { "VENUE",               "_venueDirector" },
            { "DUNGEON",             "_dungeonDirector" },
            { "COMBAT",              "_combatPhase" },
            { "REST",                "_restPhase" },
            { "TREASURE",            "_treasurePhase" },
            { "TRAP",                "_trapPhase" },
            { "ENCOUNTER",           "_encounterPhase" },
            { "WHEEL",               "_wheelPhase" },
            { "FORTUNE",             "_fortunePhase" }
        };

        internal static object Router()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                if (routerHelper != null)
                {
                    FieldInfo f = AccessTools.Field(routerHelper, "_router");
                    if (f != null)
                    {
                        object v = f.GetValue(null);
                        if (v != null) return v;
                    }
                }

                Type routerMono = AccessTools.TypeByName("RouterMono");
                Type unityObject = AccessTools.TypeByName("UnityEngine.Object");
                if (routerMono == null || unityObject == null) return null;
                MethodInfo find = AccessTools.Method(unityObject, "FindObjectOfType", new Type[] { typeof(Type) });
                return find == null ? null : find.Invoke(null, new object[] { routerMono });
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static object Env()
        {
            return GameBridge.GetEnv();
        }

        /// <summary>Current route as an uppercase string, or null when it cannot be read.</summary>
        internal static string CurrentRoute()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                if (routerHelper == null) return null;
                MethodInfo m = AccessTools.Method(routerHelper, "GetCurrentRoute");
                if (m == null || m.GetParameters().Length != 0) return null;
                object v = m.Invoke(null, null);
                return v == null ? null : v.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool TryCurrentOwner(out object owner, out string ownerType, out string error)
        {
            owner = null;
            ownerType = null;
            error = null;

            string route = CurrentRoute();
            if (route == null) { error = "current route unreadable (RouterHelper.GetCurrentRoute)"; return false; }

            string fieldName = null;
            for (int i = 0; i < OwnerFieldByRoute.GetLength(0); i++)
                if (string.Equals(OwnerFieldByRoute[i, 0], route, StringComparison.Ordinal))
                { fieldName = OwnerFieldByRoute[i, 1]; break; }

            if (fieldName == null)
            {
                error = "no owner is known for route " + route
                    + " (NONE, EXIT and the three CINEMATIC_* routes have no RouterMono director field)";
                return false;
            }

            if (!TryOwnerByField(fieldName, out owner, out error)) return false;
            ownerType = owner.GetType().Name;
            return true;
        }

        /// <summary>Resolves a Director/Phase by its type name whether or not its route is current.</summary>
        internal static bool TryOwnerOfType(string typeName, out object owner, out string error)
        {
            owner = null;
            error = null;

            Type target;
            try { target = AccessTools.TypeByName(typeName); }
            catch (Exception ex) { error = "type lookup threw for " + typeName + ": " + ex.Message; return false; }
            if (target == null) { error = "type not found: " + typeName; return false; }

            object router = Router();
            if (router == null) { error = "RouterMono instance not resolvable"; return false; }

            try
            {
                FieldInfo[] fields = router.GetType().GetFields(AccessTools.all);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].IsStatic) continue;
                    if (!target.IsAssignableFrom(fields[i].FieldType)) continue;
                    object v = fields[i].GetValue(router);
                    if (v != null) { owner = v; return true; }
                }
            }
            catch (Exception ex)
            {
                error = "RouterMono field scan threw: " + ex.Message;
                return false;
            }

            error = typeName + " is not live right now (its RouterMono field is null)";
            return false;
        }

        private static bool TryOwnerByField(string fieldName, out object owner, out string error)
        {
            owner = null;
            error = null;

            object router = Router();
            if (router == null) { error = "RouterMono instance not resolvable"; return false; }

            try
            {
                FieldInfo f = AccessTools.Field(router.GetType(), fieldName);
                if (f == null) { error = "RouterMono has no field " + fieldName; return false; }
                object v = f.GetValue(router);
                if (v == null) { error = "RouterMono." + fieldName + " is null (owner not initialized yet)"; return false; }
                owner = v;
                return true;
            }
            catch (Exception ex)
            {
                error = "reading RouterMono." + fieldName + " threw: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Invokes a zero-or-more-arg method on an already-resolved owner. Returns the rendered
        /// result; a Task is reported as its status, never awaited (the pump owns the frame).
        /// </summary>
        internal static bool TryInvokeOn(object owner, string methodName, object[] args, out string rendered, out string error)
        {
            rendered = null;
            error = null;
            if (owner == null) { error = "owner is null"; return false; }

            int argCount = args == null ? 0 : args.Length;
            MethodInfo chosen = null;
            MethodInfo[] candidates = owner.GetType().GetMethods(AccessTools.all);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.Equals(candidates[i].Name, methodName, StringComparison.Ordinal)) continue;
                if (candidates[i].GetParameters().Length != argCount) continue;
                chosen = candidates[i];
                break;
            }
            if (chosen == null)
            {
                error = owner.GetType().Name + " has no " + methodName + " with " + argCount + " arg(s)";
                return false;
            }

            try
            {
                object result = chosen.Invoke(owner, args);
                rendered = ReflectionCommands.Render(result);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = owner.GetType().Name + "." + methodName + " threw: "
                    + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "invoke failed: " + ex.Message;
                return false;
            }
        }
    }
}
```

Note `ReflectionCommands.Render` is already `internal static` on the existing type, so no visibility change is needed.

- [ ] **Step 2: Write `ModalWatch`**

`FTK2.Crucible/src/Crucible.Plugin/ModalWatch.cs`:

```csharp
using System;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// The five "am I wedged" oracles, and the force-dismiss for four of them.
    ///
    /// Every member is quoted in docs/research/crucible-traversal-inventory.md §5. All are static
    /// methods on static view helpers, so there is no instance to resolve. A missing helper reports
    /// null rather than throwing: an oracle that takes down the harness is worse than one that
    /// says "unknown".
    /// </summary>
    internal static class ModalWatch
    {
        private static bool? ReadFlag(string typeName, string methodName)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) return null;
                MethodInfo m = AccessTools.Method(t, methodName);
                if (m == null || m.GetParameters().Length != 0) return null;
                object v = m.Invoke(null, null);
                return v is bool ? (bool?)(bool)v : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool? SystemDialog() { return ReadFlag("SystemDialogViewHelper", "IsShowing"); }
        internal static bool? Prompt()       { return ReadFlag("PromptViewHelper", "PromptIsShowing"); }
        internal static bool? Dialogue()     { return ReadFlag("DialogueViewHelper", "IsShowing"); }
        internal static bool? Transition()   { return ReadFlag("TransitionViewHelper", "IsShowing"); }

        /// <summary>True only when an oracle positively reports a blocker. Unknown is not blocking.</summary>
        internal static bool AnyBlocking()
        {
            return SystemDialog() == true || Prompt() == true || Dialogue() == true || Transition() == true;
        }

        internal static string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("modal=").Append(Show(SystemDialog()));
            sb.Append(" prompt=").Append(Show(Prompt()));
            sb.Append(" dialogue=").Append(Show(Dialogue()));
            sb.Append(" transition=").Append(Show(Transition()));
            sb.Append(" console=").Append(GameBridge.IsConsoleShowing() ? "true" : "false");
            return sb.ToString();
        }

        private static string Show(bool? v) { return v == null ? "unknown" : (v.Value ? "true" : "false"); }

        /// <summary>
        /// Dismisses one class of blocker. Returns a human-readable outcome; never throws.
        /// which: "modal" | "prompt" | "transition" | "dialogue" | "all"
        /// </summary>
        internal static string Dismiss(string which)
        {
            string w = which == null ? "all" : which.Trim().ToLowerInvariant();
            StringBuilder sb = new StringBuilder();

            if (w == "all" || w == "modal")
                sb.Append("modal:").Append(Call("SystemDialogViewHelper", "ForceHide", new object[] { false })).Append(' ');
            if (w == "all" || w == "prompt")
                sb.Append("prompt:").Append(Call("PromptViewHelper", "ClosePrompt", new object[0])).Append(' ');
            if (w == "all" || w == "transition")
                sb.Append("transition:").Append(Call("TransitionViewHelper", "HideTransition", new object[0])).Append(' ');
            if (w == "all" || w == "dialogue")
                sb.Append("dialogue:").Append(Call("DialogueViewHelper", "ReleaseInput", new object[0])).Append(' ');

            if (sb.Length == 0) return "error: unknown target '" + which + "' (use modal|prompt|transition|dialogue|all)";
            return sb.ToString().TrimEnd() + " | after: " + Describe();
        }

        private static string Call(string typeName, string methodName, object[] args)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) return "no-type";
                MethodInfo m = AccessTools.Method(t, methodName);
                if (m == null || m.GetParameters().Length != args.Length) return "no-method";
                m.Invoke(null, args);
                return "ok";
            }
            catch (TargetInvocationException ex)
            {
                return "threw(" + (ex.InnerException != null ? ex.InnerException.Message : ex.Message) + ")";
            }
            catch (Exception ex)
            {
                return "failed(" + ex.Message + ")";
            }
        }
    }
}
```

- [ ] **Step 3: Verify it compiles**

```bash
dotnet build FTK2.Crucible/src/Crucible.Plugin -c Release
```

Expected: build succeeds. Nothing is registered yet — Task 3 does that.

---

### Task 3 (M1): The four navigation commands

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs`
- Modify: `FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs`

**Interfaces:**
- Consumes: `GameOwners`, `ModalWatch`, `RouteNames`, `GameBridge.RegisterCommand`.
- Produces: registered commands `crucible_where`, `crucible_route`, `crucible_dismiss`, `crucible_mp_detach`; results surface through `ReflectionCommands.LastResult`, which `RpcServer.HandleExec` already reads back into the `result` field of the `/exec` response.

**Handler shape rules, non-negotiable:**
- `public static`, so `GetMethod(..., BindingFlags.Public | BindingFlags.Static)` finds it.
- Parameters restricted to `string` / `int` / `bool`. **No enums** — the marshaller vocabulary is `int/float/double/bool/string/Vector2/Vector3` and `eRoutes` is not in it.
- A zero-parameter handler is registered through the same `RegisterCommand(string, MethodInfo, List<string>, object)` overload `GameBridge` already binds; the arg-hint list is simply empty.
- The first statement of every handler is `ReflectionCommands.LastResult = null;` so a throw before assignment cannot leave a stale result from the previous call.

- [ ] **Step 1: Write `TraversalCommands` (M1 subset)**

`FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// The traversal verb layer: the commands that move the game between eRoutes states.
    ///
    /// Design rules, both learned the hard way and both load-bearing:
    ///
    /// 1. CommandLineHelper marshals the raw string[] into a handler's individual typed parameters,
    ///    and its vocabulary is int/float/double/bool/string/Vector2/Vector3 ONLY. A string[]
    ///    handler is REJECTED at registration (verified in-game 2026-08-23,
    ///    TargetInvocationException). eRoutes is not in the vocabulary either, so every route
    ///    argument is declared `string` and parsed here.
    ///
    /// 2. `ok` must never mean "dispatched" (spec section 3). MultiplayerDemoQuickCombat is
    ///    registered, returns ok, and throws NotImplementedException internally. Every handler below
    ///    therefore reads a POST-CONDITION after acting and reports it.
    ///
    /// Member names come from docs/research/crucible-traversal-inventory.md, which quotes TypeProbe
    /// output verbatim. Nothing here is written from memory (spec section 2).
    /// </summary>
    internal static class TraversalCommands
    {
        private static ManualLogSource _log;
        private static bool _registered;

        internal static void Initialize(ManualLogSource log) { _log = log; }

        internal static bool TryRegister()
        {
            if (_registered) return true;

            MethodInfo where    = typeof(TraversalCommands).GetMethod("CrucibleWhere",     BindingFlags.Public | BindingFlags.Static);
            MethodInfo route    = typeof(TraversalCommands).GetMethod("CrucibleRoute",     BindingFlags.Public | BindingFlags.Static);
            MethodInfo dismiss  = typeof(TraversalCommands).GetMethod("CrucibleDismiss",   BindingFlags.Public | BindingFlags.Static);
            MethodInfo mpDetach = typeof(TraversalCommands).GetMethod("CrucibleMpDetach",  BindingFlags.Public | BindingFlags.Static);

            bool a = GameBridge.RegisterCommand("crucible_where", where, new List<string>());
            bool b = GameBridge.RegisterCommand("crucible_route", route,
                new List<string> { "route name (e.g. MAIN_MENU)", "force (true/false)" });
            bool c = GameBridge.RegisterCommand("crucible_dismiss", dismiss,
                new List<string> { "modal|prompt|transition|dialogue|all" });
            bool d = GameBridge.RegisterCommand("crucible_mp_detach", mpDetach,
                new List<string> { "hard disconnect (true/false)" });

            _registered = a && b && c && d;
            if (_registered && _log != null) _log.LogInfo("Traversal verbs registered (M1).");
            return _registered;
        }

        // ------------------------------------------------------------------ crucible_where

        /// <summary>
        /// crucible_where — the single most important read in the harness: where am I, who owns it,
        /// and is anything blocking me. Zero parameters.
        /// </summary>
        public static void CrucibleWhere()
        {
            ReflectionCommands.LastResult = null;

            string route = GameOwners.CurrentRoute();
            object owner; string ownerType; string ownerError;
            bool haveOwner = GameOwners.TryCurrentOwner(out owner, out ownerType, out ownerError);

            object env = GameOwners.Env();
            object gameRun = env == null ? null : ReadMember(env, "GameRun");

            StringBuilder sb = new StringBuilder();
            sb.Append("route=").Append(route == null ? "unknown" : route);
            sb.Append(" owner=").Append(haveOwner ? ownerType : "(" + ownerError + ")");
            sb.Append(" run=").Append(gameRun != null ? "present" : "none");
            sb.Append(' ').Append(ModalWatch.Describe());
            sb.Append(" blocking=").Append(ModalWatch.AnyBlocking() ? "true" : "false");

            ReflectionCommands.LastResult = sb.ToString();
        }

        // ------------------------------------------------------------------ crucible_route

        /// <summary>
        /// crucible_route &lt;route&gt; &lt;force&gt; — calls the navigation primitive
        /// RouterMono.Route(eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData,
        /// Boolean pReload, Boolean pForceRoute) with seed 0, custom data null, reload false.
        ///
        /// The route is a string because eRoutes is not in the marshaller vocabulary. EXIT is
        /// refused by RouteNames: it terminates the process.
        ///
        /// Post-condition: the route AFTER the call is reported, and whether it changed. Route is
        /// almost certainly asynchronous (RouterMono has _startTransition/_onLoaded/TaskCompletionSource
        /// wiring), so an unchanged route in the same frame is NOT proof of failure — poll with
        /// crucible_where, or use the MCP ftk2_wait_route tool.
        /// </summary>
        public static void CrucibleRoute(string pRoute, bool pForce)
        {
            ReflectionCommands.LastResult = null;

            string normalized, nameError;
            if (!RouteNames.TryNormalize(pRoute, out normalized, out nameError))
            {
                ReflectionCommands.LastResult = "error: " + nameError;
                return;
            }

            Type routesType;
            try { routesType = AccessTools.TypeByName("eRoutes"); }
            catch (Exception ex) { ReflectionCommands.LastResult = "error: eRoutes lookup threw: " + ex.Message; return; }
            if (routesType == null) { ReflectionCommands.LastResult = "error: type not found: eRoutes"; return; }

            object routeValue;
            try { routeValue = Enum.Parse(routesType, normalized, false); }
            catch (Exception ex) { ReflectionCommands.LastResult = "error: eRoutes.Parse('" + normalized + "') threw: " + ex.Message; return; }

            object router = GameOwners.Router();
            if (router == null) { ReflectionCommands.LastResult = "error: RouterMono instance not resolvable"; return; }

            MethodInfo routeMethod = AccessTools.Method(router.GetType(), "Route");
            if (routeMethod == null || routeMethod.GetParameters().Length != 5)
            {
                ReflectionCommands.LastResult = "error: RouterMono.Route(eRoutes,Int32,Object,Boolean,Boolean) not found";
                return;
            }

            string before = GameOwners.CurrentRoute();
            try
            {
                routeMethod.Invoke(router, new object[] { routeValue, 0, null, false, pForce });
            }
            catch (TargetInvocationException ex)
            {
                ReflectionCommands.LastResult = "error: Route threw: "
                    + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return;
            }
            catch (Exception ex)
            {
                ReflectionCommands.LastResult = "error: Route invoke failed: " + ex.Message;
                return;
            }

            string after = GameOwners.CurrentRoute();
            ReflectionCommands.LastResult =
                "requested=" + normalized + " force=" + pForce
                + " before=" + (before ?? "unknown") + " after=" + (after ?? "unknown")
                + " changed=" + (before != after)
                + " (Route is async; poll crucible_where) " + ModalWatch.Describe();
        }

        // ------------------------------------------------------------------ crucible_dismiss

        /// <summary>
        /// crucible_dismiss &lt;modal|prompt|transition|dialogue|all&gt; — force-closes whatever is
        /// blocking, and reports the oracle state afterwards so the caller can see it worked.
        /// </summary>
        public static void CrucibleDismiss(string pWhich)
        {
            ReflectionCommands.LastResult = null;
            ReflectionCommands.LastResult = ModalWatch.Dismiss(pWhich);
        }

        // ------------------------------------------------------------------ crucible_mp_detach

        /// <summary>
        /// crucible_mp_detach &lt;hard&gt; — stops the game auto-joining a multiplayer room, and
        /// optionally tears down an existing session.
        ///
        /// Chain (docs/research/crucible-traversal-inventory.md section 4): RouterMono
        /// ._registerDebugCommands(String&amp; autoJoinRoomId) produces a room id at startup; it
        /// reaches Env.RoomAutoJoinId; MainMenuDirector.TryAutoJoinRoom() consumes it and routes to
        /// MULTIPLAYER_LOBBY; MultiplayerLobbyDirector.Initialize(String pAutoJoinRoomId, ...) takes
        /// it as its first argument. Clearing Env.RoomAutoJoinId and NetworkHelper.CanAutoJoinRoom
        /// breaks the chain at the source. Both are plain fields (CONFIRMED); that clearing them
        /// prevents the auto-join is ASSUMED.
        ///
        /// pHardDisconnect=true additionally calls
        /// NetworkHelper.DisconnectFromRoomAndResetNetworkData(NetworkData, UserData). That mutates
        /// UserData, so it is opt-in rather than the default.
        /// </summary>
        public static void CrucibleMpDetach(bool pHardDisconnect)
        {
            ReflectionCommands.LastResult = null;
            StringBuilder sb = new StringBuilder();

            object env = GameOwners.Env();
            if (env == null) { ReflectionCommands.LastResult = "error: Env not resolvable"; return; }

            sb.Append("RoomAutoJoinId:").Append(WriteField(env, "RoomAutoJoinId", null)).Append(' ');

            Type networkHelper = AccessTools.TypeByName("NetworkHelper");
            if (networkHelper == null) sb.Append("CanAutoJoinRoom:no-type ");
            else
            {
                FieldInfo f = AccessTools.Field(networkHelper, "CanAutoJoinRoom");
                if (f == null) sb.Append("CanAutoJoinRoom:no-field ");
                else
                {
                    try { f.SetValue(null, false); sb.Append("CanAutoJoinRoom:false "); }
                    catch (Exception ex) { sb.Append("CanAutoJoinRoom:failed(").Append(ex.Message).Append(") "); }
                }
            }

            if (pHardDisconnect)
            {
                object networkData = GameBridge.GetNetworkData();
                object user = ReadMember(env, "User");
                if (networkHelper == null || networkData == null || user == null)
                {
                    sb.Append("disconnect:skipped(no NetworkHelper/NetworkData/User) ");
                }
                else
                {
                    MethodInfo m = AccessTools.Method(networkHelper, "DisconnectFromRoomAndResetNetworkData");
                    if (m == null || m.GetParameters().Length != 2) sb.Append("disconnect:no-method ");
                    else
                    {
                        try { m.Invoke(null, new object[] { networkData, user }); sb.Append("disconnect:dispatched "); }
                        catch (TargetInvocationException ex)
                        {
                            sb.Append("disconnect:threw(")
                              .Append(ex.InnerException != null ? ex.InnerException.Message : ex.Message)
                              .Append(") ");
                        }
                        catch (Exception ex) { sb.Append("disconnect:failed(").Append(ex.Message).Append(") "); }
                    }
                }
            }

            object after = ReadMember(env, "RoomAutoJoinId");
            sb.Append("| RoomAutoJoinId now=").Append(after == null ? "null" : after.ToString());
            sb.Append(" route=").Append(GameOwners.CurrentRoute() ?? "unknown");
            sb.Append(" online=").Append(GameBridge.IsOnlineSession() ? "true" : "false");

            ReflectionCommands.LastResult = sb.ToString();
        }

        // ------------------------------------------------------------------ shared helpers

        internal static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                FieldInfo f = AccessTools.Field(instance.GetType(), name);
                if (f != null) return f.GetValue(instance);
                PropertyInfo p = AccessTools.Property(instance.GetType(), name);
                return p == null ? null : p.GetValue(instance, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string WriteField(object instance, string name, object value)
        {
            if (instance == null) return "no-instance";
            try
            {
                FieldInfo f = AccessTools.Field(instance.GetType(), name);
                if (f == null) return "no-field";
                f.SetValue(instance, value);
                return "cleared";
            }
            catch (Exception ex)
            {
                return "failed(" + ex.Message + ")";
            }
        }

        internal static int CountOf(object collection)
        {
            ICollection c = collection as ICollection;
            return c == null ? -1 : c.Count;
        }
    }
}
```

- [ ] **Step 2: Register from the existing retry loop**

In `FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs`, at the end of `TryRegister()`, replace:

```csharp
            _registered = a && b;
            return _registered;
```

with:

```csharp
            _registered = a && b;
            // Traversal verbs ride the same retry loop: the game's command registry does not exist
            // until RouterMono has run CommandLineHelper.Initialize, which is why registration is
            // driven from the tick rather than from Awake.
            if (_registered) TraversalCommands.TryRegister();
            return _registered;
```

and in `Initialize(ManualLogSource log)` add `TraversalCommands.Initialize(log);` after `_log = log;`.

- [ ] **Step 3: Build and deploy**

```bash
dotnet build FTK2.Crucible/src/Crucible.Plugin -c Release
```
Then deploy the plugin DLL by the repo's existing `tools/deploy.ps1` route. Do not hand-copy.

- [ ] **Step 4: Live test — M1 acceptance**

Launch one instance with Steam running:

```bash
pwsh -File FTK2.Crucible/tools/launch-peers.ps1 -Count 1
```

Then, from the MCP side or with curl:

```bash
curl -s -X POST http://127.0.0.1:8787/exec -H 'Content-Type: application/json' \
  -d '{"command":"crucible_where","args":[]}'
```
Expected: `ok: true` and a `result` string beginning `route=` with a real route name, an `owner=` naming a Director type, and five oracle flags. **If `owner=` reports an error while `route=` is a gameplay route, `GameOwners.OwnerFieldByRoute` is wrong — stop and re-probe `RouterMono`'s fields before continuing.**

```bash
curl -s -X POST http://127.0.0.1:8787/exec -H 'Content-Type: application/json' \
  -d '{"command":"crucible_route","args":["MAIN_MENU","true"]}'
```
Expected: `ok: true`; `result` shows `requested=MAIN_MENU`. Then re-run `crucible_where` after ~2s and confirm `route=MAIN_MENU`. **This is the acceptance criterion for the entire milestone: the harness has changed screens for the first time.**

- [ ] **Step 5: Live test — the multiplayer-landing fix**

If the game boots to `MULTIPLAYER_LOBBY`:

```bash
curl -s -X POST http://127.0.0.1:8787/exec -H 'Content-Type: application/json' \
  -d '{"command":"crucible_mp_detach","args":["false"]}'
curl -s -X POST http://127.0.0.1:8787/exec -H 'Content-Type: application/json' \
  -d '{"command":"crucible_route","args":["MAIN_MENU","true"]}'
```
Expected: `RoomAutoJoinId now=null`, then `route=MAIN_MENU` on the next `crucible_where`. Then **restart the game and confirm it now boots to `MAIN_MENU` on its own** — that is what distinguishes "we escaped" from "we fixed the cause". If it still boots to the lobby, the id is arriving from a source other than `Env.RoomAutoJoinId` (Steam launch argument is the next suspect) and that finding belongs back in `docs/research/crucible-traversal-inventory.md` §4.

- [ ] **Step 6: Negative controls, live**

```bash
# must be refused by name, not executed
curl -s -X POST http://127.0.0.1:8787/exec -d '{"command":"crucible_route","args":["EXIT","true"]}' -H 'Content-Type: application/json'
```
Expected: `ok: true` (the command ran) with `result` = `error: route EXIT is refused: it terminates the game process`, **and the game is still running.** A dead process here is a plan failure.

```bash
curl -s -X POST http://127.0.0.1:8787/exec -d '{"command":"crucible_route","args":["TOWN","true"]}' -H 'Content-Type: application/json'
```
Expected: `result` = `error: not an eRoutes value: TOWN`, route unchanged.

```bash
curl -s -X POST http://127.0.0.1:8787/exec -d '{"command":"crucible_dismiss","args":["nonsense"]}' -H 'Content-Type: application/json'
```
Expected: `result` begins `error: unknown target 'nonsense'`.

- [ ] **Step 7: Record what the live run taught you**

Append a short "M1 live results" section to `docs/research/crucible-traversal-inventory.md` recording, for each route you actually reached: whether a cold `Route` worked, how long it took, and what the oracles read. That table is what M2 and M3 are planned against.

---

### Task 4 (M2): Run lifecycle — `crucible_runs`, `crucible_load_run`, `crucible_end_run`

**Files:**
- Modify: `FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs`

**Interfaces:**
- Consumes: `GameOwners`, `TraversalCommands.ReadMember`.
- Produces: registered `crucible_runs`, `crucible_load_run`, `crucible_end_run`.

**Grounding.** `Env.GameRuns` is `| field | List`1 | GameRuns |`; elements are `GameSaveDirector+GameSaveData` with confirmed fields `runID`, `saveName`, `runPath`, `adventureType`, `difficulty`, `roundCount`, `dateTime`, and method `String GetFileName()`. `UserData.LastGameRunIdPlayed` is `| field | String |`. The loader is `AdventureSelectionDirector._loadGameRun(String pFileName, String pDisableLogMessage)` — **the two-string overload, deliberately chosen over the `GameSaveData` one because it is directly marshallable.** `_endAdventure(Boolean pIsVictory)` exists on nine owners.

- [ ] **Step 1: Add the three handlers**

Append to `TraversalCommands`:

```csharp
        // ------------------------------------------------------------------ crucible_runs

        /// <summary>
        /// crucible_runs — lists the saved runs from Env.GameRuns, plus which one
        /// UserData.LastGameRunIdPlayed names. Zero parameters. Read-only.
        /// </summary>
        public static void CrucibleRuns()
        {
            ReflectionCommands.LastResult = null;

            object env = GameOwners.Env();
            if (env == null) { ReflectionCommands.LastResult = "error: Env not resolvable"; return; }

            object runs = ReadMember(env, "GameRuns");
            IEnumerable list = runs as IEnumerable;
            if (list == null) { ReflectionCommands.LastResult = "error: Env.GameRuns is not enumerable (got " + (runs == null ? "null" : runs.GetType().Name) + ")"; return; }

            object user = ReadMember(env, "User");
            object last = user == null ? null : ReadMember(user, "LastGameRunIdPlayed");

            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (object save in list)
            {
                if (save == null) continue;
                n++;
                sb.Append('[').Append(n - 1).Append("] ");
                sb.Append("runID=").Append(Str(ReadMember(save, "runID")));
                sb.Append(" saveName=").Append(Str(ReadMember(save, "saveName")));
                sb.Append(" adventureType=").Append(Str(ReadMember(save, "adventureType")));
                sb.Append(" difficulty=").Append(Str(ReadMember(save, "difficulty")));
                sb.Append(" roundCount=").Append(Str(ReadMember(save, "roundCount")));
                sb.Append(" file=").Append(CallStringMethod(save, "GetFileName"));
                sb.Append(" | ");
            }

            ReflectionCommands.LastResult = "count=" + n
                + " lastPlayed=" + Str(last) + " || " + (n == 0 ? "(no saved runs)" : sb.ToString().TrimEnd(' ', '|'));
        }

        // ------------------------------------------------------------------ crucible_load_run

        /// <summary>
        /// crucible_load_run &lt;runIdOrFile&gt; — loads a saved run.
        ///
        /// Calls AdventureSelectionDirector._loadGameRun(String pFileName, String pDisableLogMessage)
        /// (CONFIRMED signature). The argument is matched against each Env.GameRuns entry's runID,
        /// saveName and GetFileName(), and the entry's GetFileName() is what is passed through —
        /// so callers can name a run any of the three ways crucible_runs prints it.
        ///
        /// ASSUMED: that _loadGameRun alone carries the route through to ADVENTURE.
        /// crucible-load-path.md section 5 step 6 records the same assumption and the fallbacks
        /// (_loadGameRunImpl, _tryFinishLoadingAndInit) if it turns out to be wrong.
        ///
        /// Requires the AdventureSelectionDirector to be live. If it is not, this reports that
        /// rather than guessing — route to ADVENTURE_SELECTION first with crucible_route.
        /// </summary>
        public static void CrucibleLoadRun(string pRunIdOrFile)
        {
            ReflectionCommands.LastResult = null;

            if (string.IsNullOrEmpty(pRunIdOrFile))
            {
                ReflectionCommands.LastResult = "error: usage: crucible_load_run <runID|saveName|fileName>";
                return;
            }

            object env = GameOwners.Env();
            if (env == null) { ReflectionCommands.LastResult = "error: Env not resolvable"; return; }

            IEnumerable list = ReadMember(env, "GameRuns") as IEnumerable;
            if (list == null) { ReflectionCommands.LastResult = "error: Env.GameRuns is not enumerable"; return; }

            string fileName = null;
            string matchedOn = null;
            foreach (object save in list)
            {
                if (save == null) continue;
                string id = Str(ReadMember(save, "runID"));
                string name = Str(ReadMember(save, "saveName"));
                string file = CallStringMethod(save, "GetFileName");
                if (Same(id, pRunIdOrFile))   { fileName = file; matchedOn = "runID"; break; }
                if (Same(name, pRunIdOrFile)) { fileName = file; matchedOn = "saveName"; break; }
                if (Same(file, pRunIdOrFile)) { fileName = file; matchedOn = "fileName"; break; }
            }

            if (fileName == null)
            {
                ReflectionCommands.LastResult = "error: no run matched '" + pRunIdOrFile
                    + "' (run crucible_runs to list them)";
                return;
            }

            object director; string ownerError;
            if (!GameOwners.TryOwnerOfType("AdventureSelectionDirector", out director, out ownerError))
            {
                ReflectionCommands.LastResult = "error: " + ownerError
                    + " — route to ADVENTURE_SELECTION first (crucible_route ADVENTURE_SELECTION true)";
                return;
            }

            string rendered, invokeError;
            if (!GameOwners.TryInvokeOn(director, "_loadGameRun", new object[] { fileName, null }, out rendered, out invokeError))
            {
                ReflectionCommands.LastResult = "error: " + invokeError;
                return;
            }

            object gameRun = ReadMember(env, "GameRun");
            ReflectionCommands.LastResult =
                "matched=" + matchedOn + " file=" + fileName
                + " route=" + (GameOwners.CurrentRoute() ?? "unknown")
                + " run=" + (gameRun != null ? "present" : "none")
                + " (load is async; poll crucible_where until route=ADVENTURE) " + ModalWatch.Describe();
        }

        // ------------------------------------------------------------------ crucible_end_run

        /// <summary>
        /// crucible_end_run &lt;isVictory&gt; — ends the active run.
        ///
        /// Calls _endAdventure(Boolean pIsVictory) on whichever Director/Phase currently owns the
        /// route. That method exists on AdventureDirector, AdventureSelectionDirector,
        /// PartyManagementDirector, VenueDirector, DungeonDirector, CombatPhase, EncounterPhase,
        /// RestPhase and FortunePhase (all CONFIRMED), which covers every route a live run can be on.
        ///
        /// Note what this ISN'T: CombatHelper.TryEndGame(CombatState) carries no victory flag, so
        /// combat-scoped force_win/force_lose cannot be built from it. Run-scoped victory/loss is
        /// this verb; combat-scoped belongs to a later combat plan.
        ///
        /// ASSUMED (crucible-quest-progression.md section 3): that _endAdventure routes onward to
        /// CINEMATIC_OUTRO. That route has no known owner, so DO NOT assume the harness can act
        /// after this call — treat crucible_end_run as terminal for the run and verify with
        /// crucible_where.
        /// </summary>
        public static void CrucibleEndRun(bool pIsVictory)
        {
            ReflectionCommands.LastResult = null;

            object owner; string ownerType; string ownerError;
            if (!GameOwners.TryCurrentOwner(out owner, out ownerType, out ownerError))
            {
                ReflectionCommands.LastResult = "error: " + ownerError;
                return;
            }

            string before = GameOwners.CurrentRoute();
            string rendered, invokeError;
            if (!GameOwners.TryInvokeOn(owner, "_endAdventure", new object[] { pIsVictory }, out rendered, out invokeError))
            {
                ReflectionCommands.LastResult = "error: " + invokeError
                    + " — fall back to QuitToMenu via crucible_invoke " + ownerType + " QuitToMenu -";
                return;
            }

            ReflectionCommands.LastResult =
                "owner=" + ownerType + " isVictory=" + pIsVictory + " returned=" + rendered
                + " before=" + (before ?? "unknown") + " after=" + (GameOwners.CurrentRoute() ?? "unknown")
                + " " + ModalWatch.Describe();
        }

        private static string Str(object v) { return v == null ? "null" : v.ToString(); }

        private static bool Same(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string CallStringMethod(object instance, string methodName)
        {
            if (instance == null) return "null";
            try
            {
                MethodInfo m = AccessTools.Method(instance.GetType(), methodName);
                if (m == null || m.GetParameters().Length != 0) return "no-method";
                object v = m.Invoke(instance, null);
                return v == null ? "null" : v.ToString();
            }
            catch (Exception ex)
            {
                return "threw(" + ex.Message + ")";
            }
        }
```

- [ ] **Step 2: Register them**

In `TraversalCommands.TryRegister()`, add before the `_registered =` line:

```csharp
            MethodInfo runs    = typeof(TraversalCommands).GetMethod("CrucibleRuns",    BindingFlags.Public | BindingFlags.Static);
            MethodInfo loadRun = typeof(TraversalCommands).GetMethod("CrucibleLoadRun", BindingFlags.Public | BindingFlags.Static);
            MethodInfo endRun  = typeof(TraversalCommands).GetMethod("CrucibleEndRun",  BindingFlags.Public | BindingFlags.Static);

            bool e = GameBridge.RegisterCommand("crucible_runs", runs, new List<string>());
            bool f = GameBridge.RegisterCommand("crucible_load_run", loadRun,
                new List<string> { "runID | saveName | fileName" });
            bool g = GameBridge.RegisterCommand("crucible_end_run", endRun,
                new List<string> { "isVictory (true/false)" });
```

and change the assignment to `_registered = a && b && c && d && e && f && g;`.

- [ ] **Step 3: Live test**

```bash
curl -s -X POST http://127.0.0.1:8787/exec -d '{"command":"crucible_runs","args":[]}' -H 'Content-Type: application/json'
```
Expected: `count=N` matching the file count under
`%LOCALAPPDATA%Low\IronOak Games\For The King II\GameRuns`, with a `runID` and `saveName` per entry.

Then route to `ADVENTURE_SELECTION`, load the first run by its `runID`, and poll `crucible_where`
until `route=ADVENTURE` and `run=present`. **`run=present` with `route=ADVENTURE` is the M2 load
acceptance criterion.**

- [ ] **Step 4: Negative controls, live**

- `crucible_load_run` with a bogus id → `error: no run matched 'zzz'`, route unchanged.
- `crucible_load_run` while on `MAIN_MENU` → `error: AdventureSelectionDirector is not live right now …` with the routing hint. **Not a crash, and not a silent no-op.**
- `crucible_end_run` while on `MAIN_MENU` → `error: MainMenuDirector has no _endAdventure with 1 arg(s)`. This is the correct answer: `MainMenuDirector` genuinely does not have it.

---

### Task 5 (M2): New campaign + party composition — `crucible_new_run`, `crucible_party_class`, `crucible_party_begin`

**Files:**
- Modify: `FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs`

**This is the highest-risk task in the plan and it is isolated deliberately.** All three verbs call CONFIRMED signatures, but all three depend on ASSUMED *behaviour*: that setting `Env.SelectedAdventureConfig` + `Env.SelectedDifficulty` and clicking the start node is equivalent to the UI flow; that `_rebuildCharactertAsNewConfigType` works outside its usual call site; and that `_initAdventureAndRoute(Boolean)` is the whole Begin-Adventure step. **If any of the three fails live, the fallback is `crucible_invoke` against the same members with different arguments — the plan does not need reworking, only the argument choice.**

**Grounding.**
```
Env                          | field  | String            | SelectedAdventureConfig |
Env                          | field  | eGameDifficulties | SelectedDifficulty      |
eGameDifficulties            = NONE, APPRENTICE, JOURNEYMAN, MASTER, GAUNTLET
AdventureSelectionDirector   | field  | String            | _selectedCampaignName   |
AdventureSelectionDirector   | field  | List`1            | GATED_ADVENTURE_CONFIGS |
AdventureSelectionDirector   | field  | List`1            | PERMITTED_PLAYTEST_ADVENTURES |
AdventureSelectionDirector   | field  | Dictionary`2      | _campaignDataToAdventureConfigs |
AdventureSelectionDirector   | method | Void | _initializeAdventureInfo   | (String pSelected) |
AdventureSelectionDirector   | method | Void | _onNodeStartAdventureClick | (NavigationSubmitEvent pEvent) |
PartyManagementDirector      | field  | List`1 | _playerEntities |
PartyManagementDirector      | method | Task | _rebuildCharactertAsNewConfigType | (Entity pCharacter, String pClassConfigName, Boolean pOnlineAction, Boolean pTryPlayReadyUp, Boolean pIsPreset) |
PartyManagementDirector      | method | Task | _initAdventureAndRoute | (Boolean pOnlineAction) |
PartyManagementDirector      | field  | CancellationTokenSource | _classChangeDelayToken |
PartyManagementDirector      | field  | SemaphoreSlim           | _customizationLock     |
NetworkHelper                | method | Task | StartNewCampaignAsHost | (String pAdventureId, eGameDifficulties pDifficulty, ...) |
```
The `NetworkHelper` signature is the independent corroboration that **a campaign is a string id plus a difficulty enum** — the shape `crucible_new_run` takes.

- [ ] **Step 1: Add the three handlers**

Append to `TraversalCommands`:

```csharp
        // ------------------------------------------------------------------ crucible_new_run

        /// <summary>
        /// crucible_new_run &lt;adventureId&gt; &lt;difficulty&gt; — starts a new campaign.
        ///
        /// Pass "?" as the adventureId to LIST the ids this build accepts (read from
        /// AdventureSelectionDirector's static GATED_ADVENTURE_CONFIGS and
        /// PERMITTED_PLAYTEST_ADVENTURES plus _campaignDataToAdventureConfigs keys) instead of
        /// starting anything. Nobody should be guessing campaign names.
        ///
        /// Sequence: write Env.SelectedAdventureConfig and Env.SelectedDifficulty (both CONFIRMED
        /// plain fields), call _initializeAdventureInfo(String pSelected), then
        /// _onNodeStartAdventureClick(null). ASSUMED: that the UI event argument is ignorable, and
        /// that this trio is equivalent to a human clicking Start.
        ///
        /// Fallback if it is not: MainMenuDirector._openAdventure(Env pEnv, String pAdventureName)
        /// (CONFIRMED signature) from MAIN_MENU, reachable today via crucible_invoke.
        /// </summary>
        public static void CrucibleNewRun(string pAdventureId, string pDifficulty)
        {
            ReflectionCommands.LastResult = null;

            object director; string ownerError;
            if (!GameOwners.TryOwnerOfType("AdventureSelectionDirector", out director, out ownerError))
            {
                ReflectionCommands.LastResult = "error: " + ownerError
                    + " — route to ADVENTURE_SELECTION first (crucible_route ADVENTURE_SELECTION true)";
                return;
            }

            if (pAdventureId == "?")
            {
                ReflectionCommands.LastResult = DescribeAdventureIds(director);
                return;
            }

            if (string.IsNullOrEmpty(pAdventureId))
            {
                ReflectionCommands.LastResult = "error: usage: crucible_new_run <adventureId|?> <difficulty>";
                return;
            }

            Type diffType = AccessTools.TypeByName("eGameDifficulties");
            if (diffType == null) { ReflectionCommands.LastResult = "error: type not found: eGameDifficulties"; return; }

            object difficulty;
            try { difficulty = Enum.Parse(diffType, (pDifficulty ?? "JOURNEYMAN").Trim(), true); }
            catch (Exception)
            {
                ReflectionCommands.LastResult = "error: not an eGameDifficulties value: '" + pDifficulty
                    + "' (NONE|APPRENTICE|JOURNEYMAN|MASTER|GAUNTLET)";
                return;
            }

            object env = GameOwners.Env();
            if (env == null) { ReflectionCommands.LastResult = "error: Env not resolvable"; return; }

            StringBuilder sb = new StringBuilder();
            sb.Append("SelectedAdventureConfig:").Append(SetField(env, "SelectedAdventureConfig", pAdventureId)).Append(' ');
            sb.Append("SelectedDifficulty:").Append(SetField(env, "SelectedDifficulty", difficulty)).Append(' ');
            sb.Append("_selectedCampaignName:").Append(SetField(director, "_selectedCampaignName", pAdventureId)).Append(' ');

            string rendered, invokeError;
            if (!GameOwners.TryInvokeOn(director, "_initializeAdventureInfo", new object[] { pAdventureId }, out rendered, out invokeError))
            {
                ReflectionCommands.LastResult = "error at _initializeAdventureInfo: " + invokeError + " | " + sb;
                return;
            }
            sb.Append("_initializeAdventureInfo:ok ");

            if (!GameOwners.TryInvokeOn(director, "_onNodeStartAdventureClick", new object[] { null }, out rendered, out invokeError))
            {
                ReflectionCommands.LastResult = "error at _onNodeStartAdventureClick: " + invokeError
                    + " | " + sb
                    + " | fallback: crucible_invoke MainMenuDirector _openAdventure <env> " + pAdventureId;
                return;
            }
            sb.Append("_onNodeStartAdventureClick:ok ");

            sb.Append("| route=").Append(GameOwners.CurrentRoute() ?? "unknown");
            sb.Append(" SelectedAdventureConfig now=").Append(Str(ReadMember(env, "SelectedAdventureConfig")));
            sb.Append(" run=").Append(ReadMember(env, "GameRun") != null ? "present" : "none");
            sb.Append(' ').Append(ModalWatch.Describe());
            ReflectionCommands.LastResult = sb.ToString();
        }

        // ------------------------------------------------------------------ crucible_party_class

        /// <summary>
        /// crucible_party_class &lt;slot&gt; &lt;classConfigName&gt; — swaps one party slot's class.
        ///
        /// Pass slot -1 to LIST the current party (index, and each entity's ToString) instead of
        /// changing anything.
        ///
        /// Calls PartyManagementDirector._rebuildCharactertAsNewConfigType(Entity pCharacter,
        /// String pClassConfigName, Boolean pOnlineAction, Boolean pTryPlayReadyUp, Boolean
        /// pIsPreset) with (entity, name, false, false, false) — CONFIRMED signature.
        ///
        /// The entity comes from indexing PartyManagementDirector._playerEntities, which is how the
        /// harness supplies an Entity argument without an entity-handle scheme.
        ///
        /// IMPORTANT, from the field dump: _classChangeDelayToken (CancellationTokenSource) and
        /// _customizationLock (SemaphoreSlim) mean class swaps are ASYNC AND SERIALIZED. Two swaps
        /// in one frame will cancel the first. Callers must issue one, then poll this verb's list
        /// mode (slot -1) until the change is visible, before issuing the next.
        /// </summary>
        public static void CrucibleClass(int pSlot, string pClassConfigName)
        {
            ReflectionCommands.LastResult = null;

            object director; string ownerError;
            if (!GameOwners.TryOwnerOfType("PartyManagementDirector", out director, out ownerError))
            {
                ReflectionCommands.LastResult = "error: " + ownerError
                    + " — PARTY_MANAGEMENT must be the live route (start a run first)";
                return;
            }

            IList party = ReadMember(director, "_playerEntities") as IList;
            if (party == null)
            {
                ReflectionCommands.LastResult = "error: PartyManagementDirector._playerEntities is not an IList";
                return;
            }

            if (pSlot < 0)
            {
                StringBuilder list = new StringBuilder();
                list.Append("slots=").Append(party.Count).Append(" || ");
                for (int i = 0; i < party.Count; i++)
                    list.Append('[').Append(i).Append("] ").Append(party[i] == null ? "null" : party[i].ToString()).Append(" | ");
                ReflectionCommands.LastResult = list.ToString().TrimEnd(' ', '|');
                return;
            }

            if (pSlot >= party.Count)
            {
                ReflectionCommands.LastResult = "error: slot " + pSlot + " out of range (party has "
                    + party.Count + " slot(s); use -1 to list)";
                return;
            }
            if (string.IsNullOrEmpty(pClassConfigName))
            {
                ReflectionCommands.LastResult = "error: class config name is required";
                return;
            }

            object entity = party[pSlot];
            if (entity == null) { ReflectionCommands.LastResult = "error: slot " + pSlot + " is empty"; return; }

            string before = entity.ToString();
            string rendered, invokeError;
            if (!GameOwners.TryInvokeOn(director, "_rebuildCharactertAsNewConfigType",
                    new object[] { entity, pClassConfigName, false, false, false }, out rendered, out invokeError))
            {
                ReflectionCommands.LastResult = "error: " + invokeError;
                return;
            }

            ReflectionCommands.LastResult =
                "slot=" + pSlot + " class=" + pClassConfigName + " task=" + rendered
                + " before=" + before + " after=" + (party[pSlot] == null ? "null" : party[pSlot].ToString())
                + " (async + serialized: poll `crucible_party_class -1 -` before the next swap)";
        }

        // ------------------------------------------------------------------ crucible_party_begin

        /// <summary>
        /// crucible_party_begin — presses Begin Adventure.
        ///
        /// Calls PartyManagementDirector._initAdventureAndRoute(Boolean pOnlineAction) with false
        /// (CONFIRMED signature). ASSUMED: that this single call both starts the adventure and
        /// performs the route, as its name says, and that
        /// _processCharactersBeforeAdventureStart() does not have to be called first.
        ///
        /// If the run does not start, the two things to try in order, both via crucible_invoke:
        ///   crucible_invoke PartyManagementDirector _processCharactersBeforeAdventureStart -
        ///   crucible_invoke PartyManagementDirector _tryBeginAdventure null
        /// Both are CONFIRMED signatures on the same Director.
        /// </summary>
        public static void CrucibleBegin()
        {
            ReflectionCommands.LastResult = null;

            object director; string ownerError;
            if (!GameOwners.TryOwnerOfType("PartyManagementDirector", out director, out ownerError))
            {
                ReflectionCommands.LastResult = "error: " + ownerError;
                return;
            }

            string before = GameOwners.CurrentRoute();
            string rendered, invokeError;
            if (!GameOwners.TryInvokeOn(director, "_initAdventureAndRoute", new object[] { false }, out rendered, out invokeError))
            {
                ReflectionCommands.LastResult = "error: " + invokeError
                    + " — try crucible_invoke PartyManagementDirector _tryBeginAdventure null";
                return;
            }

            object env = GameOwners.Env();
            ReflectionCommands.LastResult =
                "task=" + rendered
                + " before=" + (before ?? "unknown") + " after=" + (GameOwners.CurrentRoute() ?? "unknown")
                + " run=" + (env != null && ReadMember(env, "GameRun") != null ? "present" : "none")
                + " (async; poll crucible_where until route=ADVENTURE) " + ModalWatch.Describe();
        }

        private static string DescribeAdventureIds(object director)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("GATED_ADVENTURE_CONFIGS=").Append(RenderList(ReadStatic("AdventureSelectionDirector", "GATED_ADVENTURE_CONFIGS")));
            sb.Append(" || PERMITTED_PLAYTEST_ADVENTURES=").Append(RenderList(ReadStatic("AdventureSelectionDirector", "PERMITTED_PLAYTEST_ADVENTURES")));
            object map = ReadMember(director, "_campaignDataToAdventureConfigs");
            IDictionary dict = map as IDictionary;
            sb.Append(" || _campaignDataToAdventureConfigs keys=");
            if (dict == null) sb.Append("(not a dictionary)");
            else
            {
                foreach (object k in dict.Keys) sb.Append(k == null ? "null" : k.ToString()).Append(',');
            }
            object options = ReadMember(director, "_campaignOptions");
            sb.Append(" || _campaignOptions=").Append(RenderList(options));
            return sb.ToString();
        }

        private static object ReadStatic(string typeName, string fieldName)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) return null;
                FieldInfo f = AccessTools.Field(t, fieldName);
                return f == null ? null : f.GetValue(null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string RenderList(object value)
        {
            IEnumerable e = value as IEnumerable;
            if (e == null) return value == null ? "null" : value.ToString();
            StringBuilder sb = new StringBuilder();
            foreach (object item in e) sb.Append(item == null ? "null" : item.ToString()).Append(',');
            return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd(',');
        }

        private static string SetField(object instance, string name, object value)
        {
            if (instance == null) return "no-instance";
            try
            {
                FieldInfo f = AccessTools.Field(instance.GetType(), name);
                if (f == null) return "no-field";
                f.SetValue(instance, value);
                return "set";
            }
            catch (Exception ex)
            {
                return "failed(" + ex.Message + ")";
            }
        }
```

Add `using System.Collections;` at the top if it is not already there (it is, from `CountOf`).

- [ ] **Step 2: Register them**

Add to `TryRegister()`:

```csharp
            MethodInfo newRun = typeof(TraversalCommands).GetMethod("CrucibleNewRun", BindingFlags.Public | BindingFlags.Static);
            MethodInfo cls    = typeof(TraversalCommands).GetMethod("CrucibleClass",  BindingFlags.Public | BindingFlags.Static);
            MethodInfo begin  = typeof(TraversalCommands).GetMethod("CrucibleBegin",  BindingFlags.Public | BindingFlags.Static);

            bool h = GameBridge.RegisterCommand("crucible_new_run", newRun,
                new List<string> { "adventureId (or ? to list)", "NONE|APPRENTICE|JOURNEYMAN|MASTER|GAUNTLET" });
            bool i = GameBridge.RegisterCommand("crucible_party_class", cls,
                new List<string> { "slot index (-1 to list)", "class config name" });
            bool j = GameBridge.RegisterCommand("crucible_party_begin", begin, new List<string>());
```

and extend the `_registered` conjunction with `&& h && i && j`.

- [ ] **Step 3: Live test — the discovery call first**

Route to `ADVENTURE_SELECTION`, then:

```bash
curl -s -X POST http://127.0.0.1:8787/exec -H 'Content-Type: application/json' \
  -d '{"command":"crucible_new_run","args":["?","JOURNEYMAN"]}'
```
Expected: a list of real adventure ids. **Record them in `docs/research/crucible-traversal-inventory.md` §3 under `ADVENTURE_SELECTION` — that list is what every later scenario names, and until it exists no scenario should hardcode a campaign.**

Then start one with a real id, poll `crucible_where` until `route=PARTY_MANAGEMENT`, run
`crucible_party_class` with slot `-1` to see the party, swap slot 0 to a known class id from
`FTK2.ClassForge`'s pack, poll the list again to confirm, then `crucible_party_begin` and poll until
`route=ADVENTURE` with `run=present`. **That full sequence is the M2 acceptance criterion, and it is
the first time the harness has driven a campaign from menu to overworld.**

- [ ] **Step 4: Negative controls, live**

- `crucible_new_run` with a bogus id → the game must not start a run; expect an error from
  `_initializeAdventureInfo` or an unchanged `route`. **If it starts a run with a garbage id, that is
  a finding: the id is not validated and scenarios must validate it themselves.**
- `crucible_new_run <valid> BANANA` → `error: not an eGameDifficulties value: 'BANANA'`, nothing set.
- `crucible_party_class 99 CF_EOR_BARD` → `error: slot 99 out of range (party has N slot(s); use -1 to list)`.
- `crucible_party_class 0 NOT_A_CLASS` → must report the invoke error, and slot 0's class must be
  unchanged when listed afterwards.
- `crucible_party_begin` from `MAIN_MENU` → `error: PartyManagementDirector is not live right now …`.

---

### Task 6 (M3): `crucible_advance` — the phase dispatcher

**Files:**
- Modify: `FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs`

**Interfaces:** registered `crucible_advance` with `string pMode, int pArg`.

**Modes.** `pMode` is a string because the target varies by owner and because `eRoutes` is not
marshallable:

| `pMode` | Behaviour | API (all CONFIRMED) |
|---|---|---|
| `phase` | End the current phase | `CombatPhase/EncounterPhase/FortunePhase/TreasurePhase/TrapPhase/WheelPhase._debugEndPhase()`; **`RestPhase._debugEndPhase(Int32 pOption)`** uses `pArg` |
| `turn` | End the overworld turn | `AdventureDirector._doEndTurn()` |
| `dungeon` | Advance one dungeon phase | `DungeonDirector._nextPhase()` |
| `route:<NAME>` | Leave via the owner's own next-route hook | `<owner>._goToNextRoute(eRoutes pNextRoute)` |

`RestPhase` is the reason `pArg` exists at all. A dispatcher that assumes a uniform zero-arg
`_debugEndPhase` fails on REST — see `docs/research/crucible-traversal-inventory.md` §0.

- [ ] **Step 1: Add the handler**

```csharp
        // ------------------------------------------------------------------ crucible_advance

        /// <summary>
        /// crucible_advance &lt;mode&gt; &lt;arg&gt; — moves the run forward one step.
        ///
        ///   phase          -> <owner>._debugEndPhase()   [RestPhase: _debugEndPhase(arg)]
        ///   turn           -> AdventureDirector._doEndTurn()
        ///   dungeon        -> DungeonDirector._nextPhase()
        ///   route:MAIN_MENU-> <owner>._goToNextRoute(eRoutes.MAIN_MENU)
        ///
        /// RestPhase is why `arg` exists: it is the ONE phase whose _debugEndPhase takes an Int32
        /// (docs/research/crucible-traversal-inventory.md section 0). Pass 0 elsewhere; it is ignored.
        ///
        /// _debugEndPhase is gated by each phase's _areDebugCommandsAllowed flag (a plain Boolean
        /// field). If a call reports success but nothing moves, read that flag before assuming the
        /// verb is broken.
        /// </summary>
        public static void CrucibleAdvance(string pMode, int pArg)
        {
            ReflectionCommands.LastResult = null;

            string mode = (pMode ?? "phase").Trim().ToLowerInvariant();
            string routeBefore = GameOwners.CurrentRoute();

            object owner; string ownerType; string ownerError;
            if (!GameOwners.TryCurrentOwner(out owner, out ownerType, out ownerError))
            {
                ReflectionCommands.LastResult = "error: " + ownerError;
                return;
            }

            string rendered, invokeError;

            if (mode == "phase")
            {
                // RestPhase takes an Int32; every other phase takes none.
                object[] args = string.Equals(ownerType, "RestPhase", StringComparison.Ordinal)
                    ? new object[] { pArg }
                    : new object[0];
                if (!GameOwners.TryInvokeOn(owner, "_debugEndPhase", args, out rendered, out invokeError))
                {
                    ReflectionCommands.LastResult = "error: " + invokeError
                        + " — check " + ownerType + "._areDebugCommandsAllowed";
                    return;
                }
            }
            else if (mode == "turn")
            {
                object adventure; string e2;
                if (!GameOwners.TryOwnerOfType("AdventureDirector", out adventure, out e2))
                { ReflectionCommands.LastResult = "error: " + e2; return; }
                if (!GameOwners.TryInvokeOn(adventure, "_doEndTurn", new object[0], out rendered, out invokeError))
                { ReflectionCommands.LastResult = "error: " + invokeError; return; }
            }
            else if (mode == "dungeon")
            {
                object dungeon; string e3;
                if (!GameOwners.TryOwnerOfType("DungeonDirector", out dungeon, out e3))
                { ReflectionCommands.LastResult = "error: " + e3; return; }
                if (!GameOwners.TryInvokeOn(dungeon, "_nextPhase", new object[0], out rendered, out invokeError))
                { ReflectionCommands.LastResult = "error: " + invokeError; return; }
            }
            else if (mode.StartsWith("route:", StringComparison.Ordinal))
            {
                string requested = mode.Substring("route:".Length);
                string normalized, nameError;
                if (!RouteNames.TryNormalize(requested, out normalized, out nameError))
                { ReflectionCommands.LastResult = "error: " + nameError; return; }

                Type routesType = AccessTools.TypeByName("eRoutes");
                if (routesType == null) { ReflectionCommands.LastResult = "error: type not found: eRoutes"; return; }

                object routeValue;
                try { routeValue = Enum.Parse(routesType, normalized, false); }
                catch (Exception ex) { ReflectionCommands.LastResult = "error: eRoutes.Parse threw: " + ex.Message; return; }

                if (!GameOwners.TryInvokeOn(owner, "_goToNextRoute", new object[] { routeValue }, out rendered, out invokeError))
                {
                    ReflectionCommands.LastResult = "error: " + invokeError
                        + (string.Equals(ownerType, "DungeonDirector", StringComparison.Ordinal)
                            ? " — DungeonDirector has no _goToNextRoute; use mode 'dungeon' instead"
                            : "");
                    return;
                }
            }
            else
            {
                ReflectionCommands.LastResult = "error: unknown mode '" + pMode
                    + "' (phase | turn | dungeon | route:<NAME>)";
                return;
            }

            ReflectionCommands.LastResult =
                "mode=" + mode + " arg=" + pArg + " owner=" + ownerType + " returned=" + rendered
                + " before=" + (routeBefore ?? "unknown") + " after=" + (GameOwners.CurrentRoute() ?? "unknown")
                + " " + ModalWatch.Describe();
        }
```

- [ ] **Step 2: Register it**

```csharp
            MethodInfo advance = typeof(TraversalCommands).GetMethod("CrucibleAdvance", BindingFlags.Public | BindingFlags.Static);
            bool k = GameBridge.RegisterCommand("crucible_advance", advance,
                new List<string> { "phase|turn|dungeon|route:<NAME>", "int arg (RestPhase option; 0 elsewhere)" });
```
extend `_registered` with `&& k`.

- [ ] **Step 3: Live test**

With a run loaded and `route=ADVENTURE`:
```bash
curl -s -X POST http://127.0.0.1:8787/exec -d '{"command":"crucible_advance","args":["turn","0"]}' -H 'Content-Type: application/json'
```
Expected: `ok: true`, and `GameRunData.RoundCount` (readable via `crucible_get Env.GameRun.RoundCount`) increases within a few seconds. **A round-count increase is the M3 acceptance criterion — not the `ok` flag.**

Then walk the run until it enters a phase route and try `phase` mode on each of COMBAT, ENCOUNTER,
FORTUNE, TREASURE, TRAP, WHEEL, and REST. **REST is the one that must be tested explicitly**, because
it is the only `Int32` overload and the only place a uniform-arity assumption breaks.

- [ ] **Step 4: Negative controls, live**

- `crucible_advance banana 0` → `error: unknown mode 'banana' (phase | turn | dungeon | route:<NAME>)`.
- `crucible_advance route:EXIT 0` → refused by `RouteNames`, game still running.
- `crucible_advance turn 0` from `MAIN_MENU` → `error: AdventureDirector is not live right now …`.
- `crucible_advance route:MAIN_MENU 0` while `route=DUNGEON` → the `DungeonDirector has no
  _goToNextRoute` hint fires. That hint existing *is* the test: it proves the dispatcher knows the
  one owner that breaks the pattern.

---

### Task 7 (M4): Combat — deferred, with a written reason

**No code in this task.** M4 is named here so the milestone list is honest about what this plan does
and does not deliver.

`crucible_advance phase` already ends a combat via `CombatPhase._debugEndPhase()`, and
`crucible_end_run <bool>` already ends a run from inside combat via `CombatPhase._endAdventure(Boolean)`.
That is enough for a campaign to be *traversed*. What is **not** here, and why:

| Wanted verb | Blocker | What would unblock it |
|---|---|---|
| `use_ability` | `CombatPhase._performAbility(Entity, Thing, CombatDecisionData, List`1, Boolean, Boolean)` needs a `CombatDecisionData` instance. No marshallable parameter list produces one. | A grounding pass on `CombatDecisionData`'s constructor/fields, plus an entity-handle scheme (index into `CombatState.Entities`). |
| `force_win` / `force_lose` at combat scope | `CombatHelper.TryEndGame(CombatState pCombatState)` carries **no** victory flag — see inventory §0. | Live probing of `CombatState.EndCombatEarly` / `AlternateWinConditions` / `AlternateLoseConditions` (all in `crucible-combat-field-map.md`) to learn which combination resolves as a win. |
| `kill_all` | `CharacterHelper.TryKillCharacter` needs an `Entity` per combatant. | Same entity-handle scheme. `CombatPhase._debugSetAllCharactersTo9999HP()` / `_debugSetPlayersTo9999HP()` (both CONFIRMED, both zero-arg) are the immediately shippable half and could be added as `crucible_combat_god <bool pPlayersOnly>` if a run needs to survive unattended. |

- [ ] **Step 1: Decide whether `crucible_combat_god` is worth adding now**

If overnight runs are dying in combat, add one more verb following the Task 6 pattern:
`CrucibleCombatGod(bool pPlayersOnly)` resolving `CombatPhase` via `GameOwners.TryOwnerOfType` and
invoking `_debugSetPlayersTo9999HP()` or `_debugSetAllCharactersTo9999HP()` — both zero-arg,
both CONFIRMED. If they are not dying in combat, skip it. Do not add it speculatively.

---

### Task 8 (M5): MCP tool surface

**Files:**
- Modify: `FTK2.Crucible/mcp/server.js`

**Interfaces:** eleven pass-through tools plus one Node-side polling tool. **`ftk2_wait_route` polls
from Node, never from the plugin** — `MainThreadPump.Run` blocks the calling thread waiting on the
game's frame, so a wait implemented plugin-side would deadlock the very frame it is waiting for.

- [ ] **Step 1: Add the tool definitions**

In the `TOOLS` array in `FTK2.Crucible/mcp/server.js`:

```javascript
  { name: 'ftk2_where',
    description: 'Where is the game right now: route, owning director, whether a run is loaded, and the five modal/loading oracles.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_route',
    description: 'Navigate to an eRoutes state. EXIT is refused. Route is async — follow with ftk2_wait_route.',
    inputSchema: { type: 'object', required: ['route'], properties: {
      route: { type: 'string', description: 'MAIN_MENU, ADVENTURE_SELECTION, PARTY_MANAGEMENT, ...' },
      force: { type: 'boolean', description: 'pForceRoute (default true)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_wait_route',
    description: 'Poll until the game reaches a route (or a modal blocks). Use after any navigation call.',
    inputSchema: { type: 'object', required: ['route'], properties: {
      route: { type: 'string' },
      timeoutMs: { type: 'number', description: 'default 60000' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_dismiss',
    description: 'Force-close whatever is blocking: modal | prompt | transition | dialogue | all.',
    inputSchema: { type: 'object', properties: {
      which: { type: 'string', description: 'default "all"' }, ...INSTANCE_ARG } } },
  { name: 'ftk2_mp_detach',
    description: 'Stop the game auto-joining a multiplayer room (clears Env.RoomAutoJoinId and NetworkHelper.CanAutoJoinRoom).',
    inputSchema: { type: 'object', properties: {
      hard: { type: 'boolean', description: 'also disconnect an existing session (mutates UserData)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_runs',
    description: 'List saved runs (runID, saveName, adventureType, difficulty, roundCount) and which was played last.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_load_run',
    description: 'Load a saved run by runID, saveName or fileName. Requires route ADVENTURE_SELECTION.',
    inputSchema: { type: 'object', required: ['run'], properties: {
      run: { type: 'string' }, ...INSTANCE_ARG } } },
  { name: 'ftk2_new_run',
    description: 'Start a new campaign. Pass adventureId "?" to list the ids this build accepts.',
    inputSchema: { type: 'object', required: ['adventureId'], properties: {
      adventureId: { type: 'string' },
      difficulty: { type: 'string', description: 'NONE|APPRENTICE|JOURNEYMAN|MASTER|GAUNTLET (default JOURNEYMAN)' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_party_class',
    description: 'Set one party slot’s class. Pass slot -1 to list the party. Swaps are async and serialized — poll between them.',
    inputSchema: { type: 'object', required: ['slot'], properties: {
      slot: { type: 'number' },
      classConfigName: { type: 'string' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_party_begin',
    description: 'Press Begin Adventure from party management.',
    inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
  { name: 'ftk2_advance',
    description: 'Move the run forward: phase | turn | dungeon | route:<NAME>. RestPhase uses the numeric arg.',
    inputSchema: { type: 'object', properties: {
      mode: { type: 'string', description: 'default "phase"' },
      arg: { type: 'number', description: 'RestPhase option; 0 elsewhere' },
      ...INSTANCE_ARG } } },
  { name: 'ftk2_end_run',
    description: 'End the active run as a victory or a loss. Terminal — verify with ftk2_where afterwards.',
    inputSchema: { type: 'object', required: ['isVictory'], properties: {
      isVictory: { type: 'boolean' }, ...INSTANCE_ARG } } },
```

- [ ] **Step 2: Add the dispatch cases**

In `callTool`'s `switch`, before `default:`:

```javascript
    case 'ftk2_where':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_where', args: [] }));
    case 'ftk2_route':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_route',
        args: [String(args.route), String(args.force === undefined ? true : !!args.force)] }));
    case 'ftk2_wait_route':
      return waitForRoute(inst, String(args.route), args.timeoutMs || 60000);
    case 'ftk2_dismiss':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_dismiss',
        args: [String(args.which || 'all')] }));
    case 'ftk2_mp_detach':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_mp_detach',
        args: [String(!!args.hard)] }));
    case 'ftk2_runs':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_runs', args: [] }));
    case 'ftk2_load_run':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_load_run',
        args: [String(args.run)] }));
    case 'ftk2_new_run':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_new_run',
        args: [String(args.adventureId), String(args.difficulty || 'JOURNEYMAN')] }));
    case 'ftk2_party_class':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_party_class',
        args: [String(args.slot), String(args.classConfigName || '-')] }));
    case 'ftk2_party_begin':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_party_begin', args: [] }));
    case 'ftk2_advance':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_advance',
        args: [String(args.mode || 'phase'), String(args.arg === undefined ? 0 : args.arg)] }));
    case 'ftk2_end_run':
      return textResult(await rpc(inst, 'POST', '/exec', { command: 'crucible_end_run',
        args: [String(!!args.isVictory)] }));
```

- [ ] **Step 3: Add the Node-side poller**

Above `callTool`:

```javascript
/**
 * Polls crucible_where until the target route is reached.
 *
 * Deliberately on the Node side. MainThreadPump.Run blocks the calling thread until the game's next
 * frame executes the work item; a wait loop inside the plugin would occupy the frame it is waiting
 * for. Polling over HTTP costs one frame per sample and cannot deadlock.
 *
 * Reports a blocking modal rather than timing out silently: a wedged run should say what wedged it.
 */
async function waitForRoute(instance, targetRoute, timeoutMs) {
  const target = String(targetRoute).trim().toUpperCase();
  const deadline = Date.now() + timeoutMs;
  const samples = [];

  while (Date.now() < deadline) {
    let res;
    try {
      res = await rpc(instance, 'POST', '/exec', { command: 'crucible_where', args: [] });
    } catch (e) {
      samples.push('unreachable: ' + e.message);
      await new Promise((r) => setTimeout(r, 1000));
      continue;
    }

    const line = (res && res.result) || '';
    samples.push(line);

    const m = /route=([A-Z_]+)/.exec(line);
    const route = m ? m[1] : null;
    if (route === target) {
      return textResult({ ok: true, route, waitedMs: timeoutMs - (deadline - Date.now()), last: line });
    }
    if (/blocking=true/.test(line)) {
      return textResult({
        ok: false, reason: 'blocked', route, last: line,
        hint: 'a modal, prompt, dialogue or transition is up — call ftk2_dismiss then retry'
      });
    }
    await new Promise((r) => setTimeout(r, 1000));
  }

  return textResult({
    ok: false, reason: 'timeout', target, timeoutMs,
    lastSamples: samples.slice(-5)
  });
}
```

- [ ] **Step 4: Verify the MCP server still speaks the protocol**

```bash
printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' | node FTK2.Crucible/mcp/server.js
```
Expected: two JSON lines; the second lists **21 tools** (9 existing + 12 new). If it lists 9, the
`TOOLS` edit did not land. If `node` reports a syntax error, the file is broken and no tool works —
this check is not optional.

- [ ] **Step 5: Negative control — the poller must actually fail**

```bash
# with the game on MAIN_MENU, wait for a route it will never reach
```
Call `ftk2_wait_route` with `route: "DUNGEON"` and `timeoutMs: 5000`. Expected: `ok:false,
reason:"timeout"` with up to five `lastSamples`, in roughly five seconds. **A poller that returns
success here, or hangs past its timeout, is a plan failure.**

---

### Task 9 (M5): Unattended hygiene — document, do not automate

**Files:**
- Modify: `docs/research/crucible-traversal-inventory.md` (append an "M1–M3 live results" section)

No new verbs. There is a temptation to add a `crucible_user_flag` verb that writes
`UserData.TutorialEnabled`, `AutoFastForwardEnabled`, `FastForwardEnabled`, `ShouldAutoEndTurn`,
`OnlineMutliplayerEnabled` and `AppConfig.SkipOutroCinematic` / `SkipSplashScreens` (all CONFIRMED
fields). **Resist it until a live run proves one of them is actually blocking**, for two reasons:
these write to persisted user state in `User.ftk2`, and every one of them is a guess about behaviour
rather than a confirmed mechanism. `crucible_invoke` can already set them ad hoc for an experiment.

- [ ] **Step 1: Run one campaign as far as it will go, unattended**

Drive `ftk2_route` → `ftk2_new_run` → `ftk2_party_class` → `ftk2_party_begin` → repeated
`ftk2_advance turn` with `ftk2_where` between each, for as long as it survives. Record:
- every route the run actually passed through, in order
- every place it stalled, and which oracle in `ModalWatch.Describe()` caught it (or that none did)
- whether it ever reached a `CINEMATIC_*` route, and what happened if it did
- what `RouterHelper.GetEndOfAdventureRoute(Env.GameRun)` returns near the end
  (`crucible_get` cannot call methods — use `crucible_invoke RouterHelper GetEndOfAdventureRoute
  <arg>`, or add it to `crucible_where` if the argument proves awkward)

- [ ] **Step 2: Write it up**

Append the results to `docs/research/crucible-traversal-inventory.md` as "§7 — M1–M3 live results",
and **update §6 "What I could not work out"**: the cinematic question is either answered by that run
or it is confirmed as still open. Either outcome is progress; leaving §6 stale is not.

---

## Self-Review

**Spec coverage.** §2 grounding rule → every game member in every task is quoted from
`docs/research/crucible-traversal-inventory.md`, which quotes TypeProbe verbatim; the two riskiest
verbs are labelled ASSUMED at the call site in code comments, not just in prose. §3 error posture →
every handler starts by clearing `LastResult` and ends by reading a post-condition; no handler
throws; Task 3 Step 6, Task 4 Step 4, Task 5 Step 4, Task 6 Step 4 and Task 8 Step 5 are the
negative controls that prove `ok` is not being confused with "worked". §5 verb layer → this plan
delivers the navigation and lifecycle half honestly and states in Task 7 exactly which of the spec's
eighteen verbs it is *not* delivering and what would unblock each.

**Honest count.** **Eleven commands promised, eleven implemented.** Nine call an API whose exact
signature is quoted; two (`crucible_new_run`, `crucible_party_begin`) call confirmed signatures with
ASSUMED semantics and carry an explicit fallback in their own doc comment. Zero speculative verbs.
Seven of the spec's §5 verbs (`use_ability`, `force_win`, `force_lose`, `kill_all`,
`jump_encounter`, `set_terrain`, `advance_days`, `teleport`) are deliberately absent with a written
blocker each.

**Deliberately out of scope:** the S1 observation surface (`crucible.state.v2`), the S4 scenario
runner, S5 MP-safety, S6 LiveDataHarness, S8 overnight runner. Each has its own plan. This plan's
deliverable is a harness that can walk a campaign from the main menu to the overworld and forward
through phases — nothing more, and nothing less.

**Type consistency.** `RouteNames.TryNormalize(string, out string, out string)` has the same
signature in Task 1's tests, Task 1's implementation, Task 3's `CrucibleRoute` and Task 6's
`CrucibleAdvance`. `GameOwners.TryInvokeOn(object, string, object[], out string, out string)` is
called identically in Tasks 4, 5 and 6. `GameOwners.TryOwnerOfType(string, out object, out string)`
and `TryCurrentOwner(out object, out string, out string)` keep distinct shapes throughout —
`TryCurrentOwner` has the extra `ownerType` out-param and the two are never swapped. Every handler
is `public static` with only `string`/`int`/`bool` parameters, which is what
`CommandLineHelper.RegisterCommand` requires. Command names match one-to-one between
`TraversalCommands.TryRegister`, `CommandGate.ReadOnlyCommands`, the MCP `TOOLS` array and the MCP
`switch`.

**Placeholder scan.** No `TBD`, `TODO`, "implement later" or "add error handling" strings. Every code
step contains complete, runnable content. The one forward reference — Task 3's `TryRegister` growing
new booleans in Tasks 4, 5 and 6 — is called out at each point of use with the exact `_registered`
conjunction to write.

**Known risks.**
1. **`Route` is asynchronous.** Every verb that navigates reports `before`/`after` in the same frame
   and says so in its result string; the actual assertion is `ftk2_wait_route`. If a live test shows
   `Route` is synchronous, the polling is harmless overhead.
2. **A cold `Route` into a gameplay route probably does not work** (inventory §6 item 2). The plan
   only ever cold-routes to menu-weight routes (`MAIN_MENU`, `ADVENTURE_SELECTION`) and reaches
   gameplay routes through the game's own flow. If Task 3 Step 4 shows even `MAIN_MENU` fails from
   `MULTIPLAYER_LOBBY`, fall back to `QuitToMenu()` via `crucible_invoke` and re-plan Task 3.
3. **`CommandGate` widening is a real safety decision, not a formality.** Five verbs become callable
   during an online session. All five are reads or client-local navigation; none touches
   `GameRunData`. If that judgement is wrong, the fix is to remove them from `ReadOnlyCommands` and
   accept that MP-lobby recovery requires `AllowMutationsInMP`.
4. **`crucible_party_class` swaps are serialized behind a `SemaphoreSlim`.** The verb documents this
   and provides list mode for polling, but nothing *enforces* the caller waiting. A scenario runner
   that fires four swaps in a loop will silently get one. This is the most likely source of a
   confusing false negative in the 35-class matrix.
