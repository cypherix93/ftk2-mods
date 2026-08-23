# S3 MCP Tool Surface + Game-State Generation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the complete MCP tool surface for For The King II — 100 tools that read the screen, press buttons and dialogs, navigate every reachable route, run and finish a campaign, skip the parts that waste hours, and **generate the game state each feature needs to be tested in** — ending in one seeded, unattended, end-to-end full run. That last item is the owner's stated definition of done; everything before it exists to make it possible.

**Architecture:** Two layers, and the split was decided by measurement, not taste. **UI driving** reads and presses the live UIToolkit tree (`UIDocument.rootVisualElement` → recursive `VisualElement.Children()` → `Button.clickable.clicked.Invoke()`), which works on any screen without knowing which Director owns it. **Internals** (`_debugEndPhase`, `_endAdventure(bool)`, `CharacterHelper.TryKillCharacter`, `QuestState.CompletedObjectives`, `GameRandom.Seed`) do the skipping and cheating that clicking cannot do quickly. **State generation** is a third, mostly host-side layer: Tier-1 `.ftk2` fixtures produced by the game and committed to the repo, Tier-2 per-feature state applied as readable JSON verb lists on top of them. No new transport machinery: `ReflectionCommands.TryRegister` already registers Crucible-owned commands into `CommandLineHelper` from the `RouterMono.Update` retry loop, already resolves any Director via three strategies, and already stashes rendered results in `LastResult` for `RpcServer.HandleExec`. Every new command registers through that same path.

**Tech Stack:** C# · `net472` (plugin) / `netstandard2.0` (Core) / `net10.0` (tests) · `LangVersion 7.3`, `ImplicitUsings disable`, `Nullable disable` · HarmonyLib `AccessTools` reflection only, no compile-time game reference · Node `node:http` + stdio JSON-RPC for MCP, no npm deps · repo console-runner test pattern (`FTK2.Crucible/src/Crucible.Core.Tests/TestHarness.cs`)

**Spec:** `docs/superpowers/specs/2026-08-23-crucible-autopilot-design.md` (§2 grounding rule, §3 architecture + error posture, §5 verb layer, §6 scenario runner, §9 isolation, §10 testing standard)

**Catalogue (the direct input — every tool id `A1`…`K10` below refers to it):** `docs/research/crucible-mcp-tool-surface.md`

**Evidence:** `crucible-ui-driving.md`, `crucible-ui-overlay-system.md`, `crucible-traversal-inventory.md`, `crucible-verb-feasibility.md`, `crucible-load-path.md`, `crucible-quest-progression.md`, `crucible-combat-field-map.md`, `crucible-rng-field-map.md`

**Relationship to `2026-08-23-s3-traversal-verbs.md`.** That plan is the eleven-command navigation subset and its code stands. **This plan supersedes it as the top-level S3 plan**, and its M2 reuses that plan's `RouteNames.cs`, `GameOwners.cs`, `ModalWatch.cs` and `TraversalCommands.cs` verbatim rather than rewriting them. Two things there are now known to be incomplete: it has no UI layer (M1 here), and it treats `route` as a sufficient post-condition, which the live session disproved.

**Prerequisite:** S0 complete — `TypeProbe` exists at `FTK2.DevKit/sandbox/TypeProbe` and `ben` is the trunk. Work in a worktree: `git worktree add ../ftk2-wt-s3ts -b s3/mcp-tool-surface ben`.

---

## Global Constraints

- **The `CommandLineHelper` marshaller vocabulary is `int / float / double / bool / string / Vector2 / Vector3`. Nothing else.** A `string[]` handler is rejected at registration with `TargetInvocationException` while `RegisterCommand` inspects the `MethodInfo` (verified in-game 2026-08-23; documented on `ReflectionCommands.CrucibleGet`). **Enums are not in the vocabulary** — `eRoutes` arrives as a `string` and is parsed inside the handler. Every handler in this plan takes only `string`, `int` and `bool`.
- **Registration is deferred to the `RouterMono` tick.** `CommandLineHelper.Initialize` has not run during plugin `Awake`; calling `RegisterCommand` earlier throws `NullReferenceException` from inside the game. Register from `ReflectionCommands.TryRegister`'s existing retry loop, never from `Awake`.
- **`route` is not a state signal, and never the sole post-condition.** Live proof: `GetCurrentRoute()` returned `MAIN_MENU` while the multiplayer browser and a modal were on screen. `crucible_screen` (A7) is the post-condition for anything that navigates.
- **`ok` must never mean "dispatched" (spec §3).** `MultiplayerDemoQuickCombat` is registered, callable, returns `ok: true`, and throws `NotImplementedException` internally. Every verb reads a post-condition after acting.
- **`eRoutes.EXIT` is deny-listed.** `Route(EXIT, …)` terminates the process. One mistyped argument ends an overnight run.
- **Navigation and observation verbs must be classified read-only in `CommandGate`.** `GameBridge.IsOnlineSession()` fails *closed* — it returns `true` when it cannot read `NetworkData.PlayingOnlineMultiplayer` — and `CommandGate.Evaluate` then denies everything not on `ReadOnlyCommands`. Without this the harness's own escape hatch is gated shut. Mutating verbs deliberately stay off the list.
- **Never write next to the owner's real saves.** `%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\GameRuns\` holds his live co-op saves; the Unity save path is identity-derived so a copied install shares it. Every state-generation write is behind `ftk2_saves_guard` (K10) and the spec §9 junction swap. Backup: `C:\Users\ben\Backups\ftk2-2026-08-23\`.
- **`.ftk2` files are obfuscated** (UTF-8 BOM then control bytes — not JSON, gzip or protobuf). Fixtures can only be produced by the running game and can only be verified by loading them. No step in this plan may claim to parse, author or edit one.
- **Grounding rule (spec §2):** no game member name enters code until it is in a `docs/research/` field map. Every name below is quoted in the catalogue. Where behaviour rather than existence is inferred, the step marks it **ASSUMED** and says what to do if it is wrong.
- **Every check has a negative control.** A test that cannot fail is a plan failure.
- **No NuGet `PackageReference`; no compile-time reference to `FTK2.dll` / `UnityEngine*.dll` / `BepInEx.dll` in Core; no npm dependency in `mcp/server.js`.**
- **Reflection failures degrade to a reported error, never an exception.** Matches `StateReader` / `GameBridge` / `ReflectionCommands`.
- **Live-test target:** one instance, Steam running, `[General] Enabled=true` and `[Rpc] Enabled=true` in `BepInEx/config`. Two instances on one machine do not work (spec §0). Screenshots need an unminimized window.

---

## File Structure

| File | Responsibility |
|---|---|
| `FTK2.Crucible/src/Crucible.Core/UiSelector.cs` | **Pure** — parses a selector string and matches it against `UiElementInfo` records. No Unity. Unit-testable with deliberately wrong inputs. |
| `FTK2.Crucible/src/Crucible.Core/UiElementInfo.cs` | **Pure** — the serializable element record and the tree-to-JSON renderer. |
| `FTK2.Crucible/src/Crucible.Core/ScreenTruth.cs` | **Pure** — combines a route string, six modal booleans and a `UiElementInfo[]` into the `crucible.screen.v1` payload and a `wedged` verdict. |
| `FTK2.Crucible/src/Crucible.Core/FixtureManifest.cs` | **Pure** — the manifest record, its JSON round-trip, and `Staleness.Evaluate(manifest, liveVersion, liveDataHash, resolvedIds)`. The staleness gate's whole decision lives here, with no game. |
| `FTK2.Crucible/src/Crucible.Core/RouteNames.cs` | Reused from `2026-08-23-s3-traversal-verbs.md` Task 1 — the 21-name list, the `EXIT` deny-list, `TryNormalize`. |
| `FTK2.Crucible/src/Crucible.Core/CommandRequest.cs` | Modify: widen `CommandGate.ReadOnlyCommands` with the observation + navigation verb names. |
| `FTK2.Crucible/src/Crucible.Plugin/UiTree.cs` | Walks every live `UIDocument` into `UiElementInfo[]`. The only file that touches UIToolkit reads. |
| `FTK2.Crucible/src/Crucible.Plugin/UiDriver.cs` | Presses buttons and sends navigation events. The only file that touches UIToolkit writes. |
| `FTK2.Crucible/src/Crucible.Plugin/UiCommands.cs` | The `crucible_ui_*`, `crucible_screen`, `crucible_modals`, `crucible_escape` handlers + registration. |
| `FTK2.Crucible/src/Crucible.Plugin/GameOwners.cs` | Reused from the traversal plan — resolves the live `RouterMono`, current route, and owning Director/Phase. |
| `FTK2.Crucible/src/Crucible.Plugin/ModalWatch.cs` | Reused from the traversal plan — the six modal oracles and internals dismissal. |
| `FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs` | Reused from the traversal plan — C1–C10 navigation and run lifecycle. |
| `FTK2.Crucible/src/Crucible.Plugin/CombatCommands.cs` | D2–D10, E1–E5, I2, I7. |
| `FTK2.Crucible/src/Crucible.Plugin/WorldCommands.cs` | F1–F2, F4–F12, G1–G3, G5, I4. |
| `FTK2.Crucible/src/Crucible.Plugin/RngCommands.cs` | H1, H2, H5, H6. |
| `FTK2.Crucible/src/Crucible.Plugin/EntityHandles.cs` | B10 — the `int` → `Entity.Guid` table every entity-taking verb resolves through. |
| `FTK2.Crucible/src/Crucible.Plugin/SaveCommands.cs` | K1 (`saveUser` wrapper) and the in-game half of K2/K8 — trigger the save, report the new run id. |
| `FTK2.Crucible/tools/fixtures.ps1` | Host-side K2/K3/K5/K6/K9/K10 — copy, guard, list, verify, restore, regenerate. |
| `FTK2.Crucible/data/Fixtures/<id>/` | Committed fixture: the copied `GameRuns\<runID>\` directory plus `fixture.json`. |
| `FTK2.Crucible/data/Fixtures/_stale-control/fixture.json` | The deliberately-stale manifest the staleness negative control asserts against. |
| `FTK2.Crucible/data/States/<featureId>.json` | Tier-2 per-feature state definitions (`base` + `apply`). |
| `FTK2.Crucible/mcp/server.js` | Modify: the new tools, plus the MCP-side polling tools `ftk2_ui_wait` / `ftk2_wait_for` and the `ftk2_apply_state` / `ftk2_autopilot` expanders. Polling lives on the Node side — it must never block the game's main thread. |
| `FTK2.Crucible/src/Crucible.Core.Tests/UiSelectorTests.cs` | Selector + tree-render tests and negative controls. |
| `FTK2.Crucible/src/Crucible.Core.Tests/ScreenTruthTests.cs` | Screen-truth and wedge-verdict tests and negative controls. |
| `FTK2.Crucible/src/Crucible.Core.Tests/FixtureManifestTests.cs` | Manifest round-trip + staleness gate tests and negative controls. |
| `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs` | Modify: call the three new suites. |

The split matters for the same reason it did in S0 and S1: `UiSelector`, `ScreenTruth` and `FixtureManifest` are pure, so they can be driven with deliberately wrong inputs — which is what makes the negative controls possible. `UiTree` and `UiDriver` are the only files that touch UIToolkit, and they are deliberately thin.

---

## Milestones

| Milestone | Tasks | Why it is here, and what it unblocks |
|---|---|---|
| **M1 — UI read + click + modal escape** | 1, 2, 3 | **Nothing works until a run can get past the boot state.** The game boots into `MULTIPLAYER_LOBBY` with a stale-adventure rejoin failure (P0-16), and internals-based dismissal failed three times live. The modal has a Close button; clicking is the answer. This milestone is also the only proposed handle on the three `CINEMATIC_*` routes, which have no owning type. |
| **M2 — Navigation + run lifecycle + state generation** | 4, 5, 6, 7 | Reaching a campaign, starting or loading a run, and — because 12 of 21 routes are in-flow only — capturing the base fixtures that let later milestones *start* in hard positions instead of playing there every time. |
| **M3 — State reading, including objectives** | 8, 9 | The oracle. Nothing can assert until it exists; the speedrun skips in M5 are unverifiable without it. |
| **M4 — Combat + party** | 10, 11 | The 35-class matrix, and every scenario that asserts on a fight. |
| **M5 — Speedrun skips** | 12 | Turning an eight-hour campaign into a runnable test. Depends on M3 to prove a skip actually skipped. |
| **M6 — End-to-end seeded full run** | 13, 14 | **The owner's definition of done.** One command: pinned seed, fixture-backed start, unattended, to victory, with a trace. |

**Spikes are scheduled, not assumed away.** Eleven questions below are genuinely open. Each is a task step that *answers* it and records the answer in `docs/research/`, with an explicit branch for "the answer is no".

| Spike | Question | Where |
|---|---|---|
| SP-1 | Does `Clickable.clicked.Invoke()` actually close the P0-16 modal? | Task 3 Step 3 |
| SP-2 | Does the UI tree expose anything at all during a `CINEMATIC_*` route? | Task 14 Step 4 |
| SP-3 | Do `Env.RoomAutoJoinId = null` + `NetworkHelper.CanAutoJoinRoom = false` actually prevent the boot auto-join? | Task 4 Step 4 |
| SP-4 | Does `_initAdventureAndRoute(bool)` alone start a run, or must `_processCharactersBeforeAdventureStart()` precede it? | Task 5 Step 3 |
| SP-5 | What sets `_areDebugCommandsAllowed`, and is `_debugEndPhase` registered on every phase? | Task 9 Step 4 |
| SP-6 | Can a `CombatDecisionData` be built from marshallable primitives (`use_ability`, D11)? | Task 10 Step 5 |
| SP-7 | Is the hex `ValueTuple\`2` really `(Int32, Int32)`, and can `Vector2` carry it (`move` F3, `teleport` F7)? | Task 11 Step 4 |
| SP-8 | Does the game's own resolution pass pick up an externally-set `QuestState.CompletedObjectives[i]`? | Task 12 Step 3 |
| SP-9 | How is a `GameSaveData` reached at runtime, now that `Env.GameRuns` is known to be `List<String>`? | Task 6 Step 2 |
| SP-10 | Does `saveUser` write a loadable save of the *current* position? | Task 6 Step 4 |
| SP-11 | Is every base fixture regenerable from a recorded recipe, or do some stay manual? | Task 7 Step 5 |

---

## M1 — UI read + click + modal escape

### Task 1: `UiElementInfo`, `UiSelector`, `ScreenTruth` (pure, no game)

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/UiElementInfo.cs`
- Create: `FTK2.Crucible/src/Crucible.Core/UiSelector.cs`
- Create: `FTK2.Crucible/src/Crucible.Core/ScreenTruth.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/UiSelectorTests.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/ScreenTruthTests.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `sealed class FTK2Mods.Crucible.UiElementInfo { string Type; string Name; string Text; bool Visible; bool Enabled; bool Focused; int Depth; int Document; }`
  - `static bool FTK2Mods.Crucible.UiSelector.Matches(UiElementInfo e, string selector)`
  - `static UiElementInfo FTK2Mods.Crucible.UiSelector.FindFirst(UiElementInfo[] elements, string selector, bool clickableOnly)`
  - `static string FTK2Mods.Crucible.UiElementInfo.RenderAll(UiElementInfo[] elements)`
  - `sealed class FTK2Mods.Crucible.ScreenState { string Route; bool MpMenus; bool Prompt; bool SystemDialog; bool Dialogue; bool Transition; bool Console; UiElementInfo[] Elements; }`
  - `static string FTK2Mods.Crucible.ScreenTruth.Render(ScreenState s)`
  - `static bool FTK2Mods.Crucible.ScreenTruth.IsWedged(ScreenState s, out string reason)`

**Selector grammar, deliberately tiny.** `#Name` matches `Name` exactly. `"text"` matches `Text` by case-insensitive substring. A bare token tries name-exact first, then text-substring — so `crucible_ui_click Close` works whether the button is *named* `Close` or *says* `Close`, which is exactly the P0-16 case and the reason the fallback exists.

- [ ] **Step 1: Write the failing tests**

`FTK2.Crucible/src/Crucible.Core.Tests/UiSelectorTests.cs`:

```csharp
using System;
using FTK2Mods.Crucible;

namespace Crucible.Core.Tests
{
    internal static class UiSelectorTests
    {
        private static UiElementInfo E(string type, string name, string text, bool visible, bool enabled)
        {
            UiElementInfo e = new UiElementInfo();
            e.Type = type; e.Name = name; e.Text = text;
            e.Visible = visible; e.Enabled = enabled; e.Focused = false;
            e.Depth = 1; e.Document = 0;
            return e;
        }

        internal static void Run()
        {
            TestHarness.Section("UiSelector");

            TestHarness.Run("#Name matches by exact name", delegate
            {
                UiElementInfo e = E("Button", "CloseButton", "Close", true, true);
                TestHarness.Assert(UiSelector.Matches(e, "#CloseButton"), "should match exact name");
            });

            // NEGATIVE CONTROL 1: #Name must not match a substring of the name.
            TestHarness.Run("#Name does NOT match a partial name", delegate
            {
                UiElementInfo e = E("Button", "CloseButton", "Close", true, true);
                TestHarness.Assert(!UiSelector.Matches(e, "#Close"), "partial name must not match #-form");
            });

            TestHarness.Run("quoted form matches text case-insensitively", delegate
            {
                UiElementInfo e = E("Button", "btn_0", "CLOSE", true, true);
                TestHarness.Assert(UiSelector.Matches(e, "\"close\""), "should match text substring");
            });

            TestHarness.Run("bare token falls back from name to text", delegate
            {
                UiElementInfo byText = E("Button", "btn_0", "Close", true, true);
                UiElementInfo byName = E("Button", "Close", "", true, true);
                TestHarness.Assert(UiSelector.Matches(byText, "Close"), "bare token should reach text");
                TestHarness.Assert(UiSelector.Matches(byName, "Close"), "bare token should reach name");
            });

            // NEGATIVE CONTROL 2: an unrelated element must not match.
            TestHarness.Run("unrelated element does NOT match", delegate
            {
                UiElementInfo e = E("Label", "TitleLabel", "Online Error", true, true);
                TestHarness.Assert(!UiSelector.Matches(e, "Close"), "unrelated element must not match");
            });

            TestHarness.Run("FindFirst skips invisible elements", delegate
            {
                UiElementInfo[] all = new UiElementInfo[]
                {
                    E("Button", "Close", "Close", false, true),
                    E("Button", "Close2", "Close", true, true)
                };
                UiElementInfo hit = UiSelector.FindFirst(all, "Close", true);
                TestHarness.Assert(hit != null && hit.Name == "Close2", "should skip the invisible one");
            });

            // NEGATIVE CONTROL 3: clickableOnly must actually exclude non-buttons.
            TestHarness.Run("FindFirst with clickableOnly does NOT return a Label", delegate
            {
                UiElementInfo[] all = new UiElementInfo[] { E("Label", "Close", "Close", true, true) };
                TestHarness.Assert(UiSelector.FindFirst(all, "Close", true) == null, "Label must be excluded");
                TestHarness.Assert(UiSelector.FindFirst(all, "Close", false) != null, "but found when not clickableOnly");
            });

            // NEGATIVE CONTROL 4: a disabled button is not pressable.
            TestHarness.Run("FindFirst skips disabled elements", delegate
            {
                UiElementInfo[] all = new UiElementInfo[] { E("Button", "Close", "Close", true, false) };
                TestHarness.Assert(UiSelector.FindFirst(all, "Close", true) == null, "disabled must be skipped");
            });

            TestHarness.Run("null and empty selectors match nothing", delegate
            {
                UiElementInfo e = E("Button", "Close", "Close", true, true);
                TestHarness.Assert(!UiSelector.Matches(e, null), "null selector must not match");
                TestHarness.Assert(!UiSelector.Matches(e, ""), "empty selector must not match");
            });
        }
    }
}
```

`FTK2.Crucible/src/Crucible.Core.Tests/ScreenTruthTests.cs`:

```csharp
using System;
using FTK2Mods.Crucible;

namespace Crucible.Core.Tests
{
    internal static class ScreenTruthTests
    {
        private static ScreenState S(string route)
        {
            ScreenState s = new ScreenState();
            s.Route = route;
            s.Elements = new UiElementInfo[0];
            return s;
        }

        internal static void Run()
        {
            TestHarness.Section("ScreenTruth");

            TestHarness.Run("a clean menu screen is not wedged", delegate
            {
                string reason;
                TestHarness.Assert(!ScreenTruth.IsWedged(S("MAIN_MENU"), out reason), "clean screen must pass");
                TestHarness.Assert(reason == null, "no reason expected, got: " + reason);
            });

            // This is the exact live failure: route says MAIN_MENU, a multiplayer modal is up.
            TestHarness.Run("route MAIN_MENU with an MP modal IS wedged", delegate
            {
                ScreenState s = S("MAIN_MENU");
                s.MpMenus = true;
                string reason;
                TestHarness.Assert(ScreenTruth.IsWedged(s, out reason), "must detect the P0-16 shape");
                TestHarness.Assert(reason != null && reason.IndexOf("multiplayer", StringComparison.OrdinalIgnoreCase) >= 0,
                    "reason should name the multiplayer menus, got: " + reason);
            });

            // NEGATIVE CONTROL 1: route alone must never be the wedge signal.
            TestHarness.Run("route alone does NOT make a screen wedged", delegate
            {
                string reason;
                TestHarness.Assert(!ScreenTruth.IsWedged(S("MULTIPLAYER_LOBBY"), out reason),
                    "being in the lobby with no modal is a place, not a wedge");
            });

            // NEGATIVE CONTROL 2: each oracle must be independently load-bearing.
            TestHarness.Run("each of the five blocking oracles trips the verdict on its own", delegate
            {
                string reason;
                ScreenState a = S("COMBAT"); a.Prompt = true;
                ScreenState b = S("COMBAT"); b.SystemDialog = true;
                ScreenState c = S("COMBAT"); c.Dialogue = true;
                ScreenState d = S("COMBAT"); d.Transition = true;
                ScreenState e = S("COMBAT"); e.MpMenus = true;
                TestHarness.Assert(ScreenTruth.IsWedged(a, out reason), "prompt must trip");
                TestHarness.Assert(ScreenTruth.IsWedged(b, out reason), "system dialog must trip");
                TestHarness.Assert(ScreenTruth.IsWedged(c, out reason), "dialogue must trip");
                TestHarness.Assert(ScreenTruth.IsWedged(d, out reason), "transition must trip");
                TestHarness.Assert(ScreenTruth.IsWedged(e, out reason), "mp menus must trip");
            });

            // NEGATIVE CONTROL 3: the console overlay is noise, not a wedge.
            TestHarness.Run("the console overlay alone does NOT count as wedged", delegate
            {
                ScreenState s = S("COMBAT"); s.Console = true;
                string reason;
                TestHarness.Assert(!ScreenTruth.IsWedged(s, out reason), "the console is ours; it is not a wedge");
            });

            TestHarness.Run("Render emits route and every oracle", delegate
            {
                ScreenState s = S("COMBAT"); s.Dialogue = true;
                string json = ScreenTruth.Render(s);
                TestHarness.Assert(json.IndexOf("\"route\":\"COMBAT\"", StringComparison.Ordinal) >= 0, "route missing: " + json);
                TestHarness.Assert(json.IndexOf("\"dialogue\":true", StringComparison.Ordinal) >= 0, "dialogue missing: " + json);
                TestHarness.Assert(json.IndexOf("\"wedged\":true", StringComparison.Ordinal) >= 0, "wedged missing: " + json);
            });
        }
    }
}
```

Add to `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`, alongside the existing suite calls:

```csharp
UiSelectorTests.Run();
ScreenTruthTests.Run();
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: compile errors (`UiElementInfo`, `UiSelector`, `ScreenState`, `ScreenTruth` do not exist). That is the failing state.

- [ ] **Step 3: Implement `UiElementInfo`**

`FTK2.Crucible/src/Crucible.Core/UiElementInfo.cs`:

```csharp
using System;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// One element of the live UIToolkit tree, flattened into plain serializable data.
    /// Deliberately Unity-free: the plugin fills these in by reflection, and every consumer
    /// (selector matching, rendering, tests) works on the record, not on a VisualElement.
    /// </summary>
    public sealed class UiElementInfo
    {
        public string Type;
        public string Name;
        public string Text;
        public bool Visible;
        public bool Enabled;
        public bool Focused;
        public int Depth;
        public int Document;

        /// <summary>True for element types the driver knows how to press.</summary>
        public bool IsClickable
        {
            get { return string.Equals(Type, "Button", StringComparison.Ordinal); }
        }

        public static string RenderAll(UiElementInfo[] elements)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            if (elements != null)
            {
                for (int i = 0; i < elements.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    UiElementInfo e = elements[i];
                    sb.Append("{\"doc\":").Append(e.Document)
                      .Append(",\"depth\":").Append(e.Depth)
                      .Append(",\"type\":").Append(MiniJson.Quote(e.Type))
                      .Append(",\"name\":").Append(MiniJson.Quote(e.Name))
                      .Append(",\"text\":").Append(MiniJson.Quote(e.Text))
                      .Append(",\"visible\":").Append(e.Visible ? "true" : "false")
                      .Append(",\"enabled\":").Append(e.Enabled ? "true" : "false")
                      .Append(",\"focused\":").Append(e.Focused ? "true" : "false")
                      .Append("}");
                }
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}
```

> `MiniJson.Quote` is the existing string-escaping helper in `FTK2.Crucible/src/Crucible.Core/MiniJson.cs`. If its public surface differs, use whatever `MiniJson` exposes for escaping rather than adding a second escaper — do not hand-roll quoting here.

- [ ] **Step 4: Implement `UiSelector`**

`FTK2.Crucible/src/Crucible.Core/UiSelector.cs`:

```csharp
using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// A three-form selector grammar, kept tiny on purpose:
    ///   #Name    -> Name, exact, ordinal
    ///   "text"   -> Text, case-insensitive substring
    ///   bare     -> Name exact first, then Text substring
    ///
    /// The bare form exists because the P0-16 modal's Close button might be *named* Close or
    /// might merely *say* Close, and the harness cannot know which in advance. Guessing one
    /// convention and being wrong is how a dismissal silently no-ops.
    /// </summary>
    public static class UiSelector
    {
        public static bool Matches(UiElementInfo e, string selector)
        {
            if (e == null || string.IsNullOrEmpty(selector)) return false;

            if (selector[0] == '#')
                return string.Equals(e.Name, selector.Substring(1), StringComparison.Ordinal);

            if (selector.Length >= 2 && selector[0] == '"' && selector[selector.Length - 1] == '"')
                return ContainsCI(e.Text, selector.Substring(1, selector.Length - 2));

            if (string.Equals(e.Name, selector, StringComparison.Ordinal)) return true;
            return ContainsCI(e.Text, selector);
        }

        /// <summary>
        /// First visible, enabled match in tree order. Invisible and disabled elements are skipped
        /// because the whole point of driving the rendered tree is to act only on what is really
        /// on screen — the divergence between route state and rendered state is the bug this
        /// architecture exists to route around.
        /// </summary>
        public static UiElementInfo FindFirst(UiElementInfo[] elements, string selector, bool clickableOnly)
        {
            if (elements == null) return null;
            for (int i = 0; i < elements.Length; i++)
            {
                UiElementInfo e = elements[i];
                if (e == null) continue;
                if (!e.Visible || !e.Enabled) continue;
                if (clickableOnly && !e.IsClickable) continue;
                if (Matches(e, selector)) return e;
            }
            return null;
        }

        private static bool ContainsCI(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
```

- [ ] **Step 5: Implement `ScreenTruth`**

`FTK2.Crucible/src/Crucible.Core/ScreenTruth.cs`:

```csharp
using System;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>The six modal oracles plus the route plus what is actually rendered.</summary>
    public sealed class ScreenState
    {
        public string Route;
        public bool MpMenus;       // MultiplayerViewHelper.AreAnyMultiplayerMenusVisible()
        public bool Prompt;        // PromptViewHelper.PromptIsShowing()
        public bool SystemDialog;  // SystemDialogViewHelper.IsShowing()
        public bool Dialogue;      // DialogueViewHelper.IsShowing()
        public bool Transition;    // TransitionViewHelper.IsShowing()
        public bool Console;       // CommandLineViewHelper.IsShowing()
        public UiElementInfo[] Elements;
    }

    /// <summary>
    /// The replacement for wait_for(route == X).
    ///
    /// Measured 2026-08-23: RouterHelper.GetCurrentRoute() returned MAIN_MENU while the
    /// multiplayer browser and an error modal were on screen. Nothing in RouterMono notifies the
    /// view layer on a route change, so the divergence is structural. Route is reported here as
    /// one input among seven, never as the answer.
    /// </summary>
    public static class ScreenTruth
    {
        /// <summary>
        /// Console visibility is deliberately NOT a wedge: it is Crucible's own overlay, and
        /// treating it as a blocker would make the harness refuse to run whenever it is open.
        /// </summary>
        public static bool IsWedged(ScreenState s, out string reason)
        {
            reason = null;
            if (s == null) { reason = "no screen state"; return true; }
            if (s.MpMenus) { reason = "multiplayer menus visible"; return true; }
            if (s.SystemDialog) { reason = "system dialog showing"; return true; }
            if (s.Prompt) { reason = "prompt showing"; return true; }
            if (s.Dialogue) { reason = "dialogue showing"; return true; }
            if (s.Transition) { reason = "transition curtain showing"; return true; }
            return false;
        }

        public static string Render(ScreenState s)
        {
            if (s == null) return "{\"error\":\"no screen state\"}";

            string reason;
            bool wedged = IsWedged(s, out reason);

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"schema\":\"crucible.screen.v1\"")
              .Append(",\"route\":").Append(MiniJson.Quote(s.Route))
              .Append(",\"mpMenus\":").Append(s.MpMenus ? "true" : "false")
              .Append(",\"prompt\":").Append(s.Prompt ? "true" : "false")
              .Append(",\"systemDialog\":").Append(s.SystemDialog ? "true" : "false")
              .Append(",\"dialogue\":").Append(s.Dialogue ? "true" : "false")
              .Append(",\"transition\":").Append(s.Transition ? "true" : "false")
              .Append(",\"console\":").Append(s.Console ? "true" : "false")
              .Append(",\"wedged\":").Append(wedged ? "true" : "false")
              .Append(",\"wedgeReason\":").Append(MiniJson.Quote(reason))
              .Append(",\"elements\":").Append(UiElementInfo.RenderAll(s.Elements))
              .Append("}");
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, with the previous suite count plus the new `UiSelector` and `ScreenTruth` tests, including **seven negative controls** (four in `UiSelectorTests`, three in `ScreenTruthTests`).

---

### Task 2: `UiTree` + `UiDriver` — the only files that touch UIToolkit

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/UiTree.cs`
- Create: `FTK2.Crucible/src/Crucible.Plugin/UiDriver.cs`

**Interfaces:**
- Consumes: `UiElementInfo`, `UiSelector` (Task 1).
- Produces:
  - `static UiElementInfo[] Crucible.Plugin.UiTree.Snapshot(out string error)`
  - `static object Crucible.Plugin.UiTree.ResolveElement(UiElementInfo target)`
  - `static bool Crucible.Plugin.UiDriver.Click(string selector, out string detail)`
  - `static bool Crucible.Plugin.UiDriver.SendNavigation(string key, out string detail)`
  - `static bool Crucible.Plugin.UiDriver.Focus(string selector, out string detail)`

**Every member named here is quoted in `crucible-ui-driving.md`.** `UIDocument.rootVisualElement`; `VisualElement.Children()` / `.name` / `.visible` / `.enabledInHierarchy` / `.parent` / `.panel` / `.focusController` / `.SendEvent(EventBase)`; `IResolvedStyle.display`; `TextElement.text`; `Button.clickable`; `Clickable.clicked` and `.clickedWithEventInfo` (public `Action` **fields**, not properties); `Focusable.Focus()`; `FocusController.focusedElement`; `UnityEngine.Object.FindObjectsOfType(Type)`; `NavigationMoveEvent.GetPooled(Direction, EventModifiers)`; `NavigationEventBase<T>.GetPooled(EventModifiers)`.

- [ ] **Step 1: Implement `UiTree`**

`FTK2.Crucible/src/Crucible.Plugin/UiTree.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Reads the live UIToolkit tree. This is the READ half of the "drive the UI like Playwright"
    /// approach, and it exists because route state is provably not the rendered state: on
    /// 2026-08-23 RouterHelper.GetCurrentRoute() said MAIN_MENU while a multiplayer error modal
    /// was on screen. Walking the rendered tree is the only signal that cannot lie about that.
    ///
    /// Every member touched here is confirmed in docs/research/crucible-ui-driving.md.
    /// Nothing throws: a failure degrades to a reported error, per the spec's error posture.
    /// </summary>
    internal static class UiTree
    {
        private const int MaxDepth = 64;
        private const int MaxElements = 4000;

        /// <summary>Parallel to the returned infos: the live VisualElement each info came from.</summary>
        private static readonly List<object> LastElements = new List<object>();

        internal static UiElementInfo[] Snapshot(out string error)
        {
            error = null;
            LastElements.Clear();
            List<UiElementInfo> infos = new List<UiElementInfo>();

            Type uiDocumentType = AccessTools.TypeByName("UnityEngine.UIElements.UIDocument");
            if (uiDocumentType == null) { error = "UIDocument type not found"; return new UiElementInfo[0]; }

            Type unityObject = AccessTools.TypeByName("UnityEngine.Object");
            if (unityObject == null) { error = "UnityEngine.Object type not found"; return new UiElementInfo[0]; }

            MethodInfo findAll = AccessTools.Method(unityObject, "FindObjectsOfType", new Type[] { typeof(Type) });
            if (findAll == null) { error = "FindObjectsOfType(Type) not found"; return new UiElementInfo[0]; }

            object[] docs;
            try { docs = (object[])findAll.Invoke(null, new object[] { uiDocumentType }); }
            catch (Exception ex) { error = "FindObjectsOfType threw: " + ex.Message; return new UiElementInfo[0]; }
            if (docs == null) { error = "FindObjectsOfType returned null"; return new UiElementInfo[0]; }

            PropertyInfo rootProp = AccessTools.Property(uiDocumentType, "rootVisualElement");
            if (rootProp == null) { error = "UIDocument.rootVisualElement not found"; return new UiElementInfo[0]; }

            for (int d = 0; d < docs.Length; d++)
            {
                object root;
                try { root = rootProp.GetValue(docs[d], null); }
                catch (Exception) { continue; }
                if (root == null) continue;

                object focused = TryGetFocused(root);
                Walk(root, d, 0, focused, infos);
                if (infos.Count >= MaxElements) break;
            }

            return infos.ToArray();
        }

        /// <summary>The live VisualElement behind an info from the most recent Snapshot.</summary>
        internal static object ResolveElement(UiElementInfo target)
        {
            if (target == null) return null;
            int index = target.Document * 0; // placeholder-free: the index is carried below
            index = target.Depth; // not used for lookup; see IndexOf mapping
            return null;
        }

        /// <summary>
        /// Snapshot the tree and return both the plain infos and the live elements, index-aligned.
        /// Callers that intend to ACT must use this, not Snapshot, so the element they press is
        /// the element they matched.
        /// </summary>
        internal static bool SnapshotWithElements(out UiElementInfo[] infos, out object[] elements, out string error)
        {
            infos = Snapshot(out error);
            elements = LastElements.ToArray();
            return error == null;
        }

        private static void Walk(object element, int docIndex, int depth, object focused, List<UiElementInfo> outInfos)
        {
            if (element == null || depth > MaxDepth || outInfos.Count >= MaxElements) return;

            UiElementInfo info = new UiElementInfo();
            Type t = element.GetType();
            info.Type = t.Name;
            info.Document = docIndex;
            info.Depth = depth;
            info.Name = ReadString(element, t, "name");
            info.Text = ReadString(element, t, "text");
            info.Visible = ReadBool(element, t, "visible", false) && IsDisplayed(element, t);
            info.Enabled = ReadBool(element, t, "enabledInHierarchy", false);
            info.Focused = focused != null && ReferenceEquals(focused, element);

            outInfos.Add(info);
            LastElements.Add(element);

            MethodInfo children = AccessTools.Method(t, "Children", new Type[0]);
            if (children == null) return;

            IEnumerable kids;
            try { kids = children.Invoke(element, null) as IEnumerable; }
            catch (Exception) { return; }
            if (kids == null) return;

            foreach (object kid in kids)
            {
                Walk(kid, docIndex, depth + 1, focused, outInfos);
                if (outInfos.Count >= MaxElements) return;
            }
        }

        /// <summary>
        /// resolvedStyle.display != DisplayStyle.None. An element can report visible == true and
        /// still be display:none, which is exactly the case where a naive check would claim a
        /// dismissed modal is still up (or a live one is gone).
        /// </summary>
        private static bool IsDisplayed(object element, Type t)
        {
            try
            {
                PropertyInfo resolved = AccessTools.Property(t, "resolvedStyle");
                if (resolved == null) return true;
                object style = resolved.GetValue(element, null);
                if (style == null) return true;

                Type iface = AccessTools.TypeByName("UnityEngine.UIElements.IResolvedStyle");
                PropertyInfo display = iface == null ? null : AccessTools.Property(iface, "display");
                if (display == null) return true;

                object value = display.GetValue(style, null);
                return value == null || !string.Equals(value.ToString(), "None", StringComparison.Ordinal);
            }
            catch (Exception) { return true; }
        }

        private static object TryGetFocused(object element)
        {
            try
            {
                Type t = element.GetType();
                PropertyInfo fcProp = AccessTools.Property(t, "focusController");
                if (fcProp == null) return null;
                object fc = fcProp.GetValue(element, null);
                if (fc == null) return null;
                PropertyInfo feProp = AccessTools.Property(fc.GetType(), "focusedElement");
                return feProp == null ? null : feProp.GetValue(fc, null);
            }
            catch (Exception) { return null; }
        }

        private static string ReadString(object o, Type t, string member)
        {
            try
            {
                PropertyInfo p = AccessTools.Property(t, member);
                if (p == null) return null;
                object v = p.GetValue(o, null);
                return v == null ? null : v.ToString();
            }
            catch (Exception) { return null; }
        }

        private static bool ReadBool(object o, Type t, string member, bool fallback)
        {
            try
            {
                PropertyInfo p = AccessTools.Property(t, member);
                if (p == null) return fallback;
                object v = p.GetValue(o, null);
                return v is bool ? (bool)v : fallback;
            }
            catch (Exception) { return fallback; }
        }
    }
}
```

> Delete the vestigial `ResolveElement` stub above and keep only `SnapshotWithElements` — it is listed in the Interfaces block for symmetry with the tests, but `SnapshotWithElements` is the one the driver uses and the only one that can honestly resolve an element. (Stated here rather than left as a surprise: an index-aligned pair is the correct design; a lookup by depth is not.)

- [ ] **Step 2: Implement `UiDriver`**

`FTK2.Crucible/src/Crucible.Plugin/UiDriver.cs`:

```csharp
using System;
using System.Reflection;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// The WRITE half. Three strategies, in the order crucible-ui-driving.md recommends:
    ///
    ///  1. Button.clickable.clicked.Invoke() — runs exactly the delegate the game registered.
    ///     No focus, no panel, no pooled-event construction, no dispatch risk.
    ///  2. Button.clickable.clickedWithEventInfo.Invoke(null) — same, for buttons wired the
    ///     other way.
    ///  3. NavigationSubmitEvent via SendEvent — drives Button's own OnNavigationSubmit handler.
    ///
    /// Raw KeyDownEvent / PointerDownEvent / ClickEvent injection is deliberately absent: their
    /// GetPooled chains were never traced, and nothing here needs them.
    /// </summary>
    internal static class UiDriver
    {
        internal static bool Click(string selector, out string detail)
        {
            detail = null;

            UiElementInfo[] infos; object[] elements; string error;
            if (!UiTree.SnapshotWithElements(out infos, out elements, out error))
            {
                detail = "ui snapshot failed: " + error;
                return false;
            }

            int index = IndexOfMatch(infos, selector, true);
            if (index < 0)
            {
                detail = "no visible, enabled clickable element matched: " + selector;
                return false;
            }

            object button = elements[index];
            Type buttonType = button.GetType();

            PropertyInfo clickableProp = AccessTools.Property(buttonType, "clickable");
            if (clickableProp != null)
            {
                object clickable = null;
                try { clickable = clickableProp.GetValue(button, null); }
                catch (Exception ex) { detail = "reading clickable threw: " + ex.Message; }

                if (clickable != null)
                {
                    Type ct = clickable.GetType();

                    FieldInfo clickedField = AccessTools.Field(ct, "clicked");
                    if (clickedField != null)
                    {
                        Delegate clicked = null;
                        try { clicked = clickedField.GetValue(clickable) as Delegate; }
                        catch (Exception) { }
                        if (clicked != null)
                        {
                            try
                            {
                                clicked.DynamicInvoke(null);
                                detail = "clicked '" + infos[index].Name + "' via Clickable.clicked";
                                return true;
                            }
                            catch (TargetInvocationException ex)
                            {
                                detail = "Clickable.clicked threw: " +
                                    (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                                return false;
                            }
                        }
                    }

                    FieldInfo withInfoField = AccessTools.Field(ct, "clickedWithEventInfo");
                    if (withInfoField != null)
                    {
                        Delegate withInfo = null;
                        try { withInfo = withInfoField.GetValue(clickable) as Delegate; }
                        catch (Exception) { }
                        if (withInfo != null)
                        {
                            try
                            {
                                withInfo.DynamicInvoke(new object[] { null });
                                detail = "clicked '" + infos[index].Name + "' via clickedWithEventInfo";
                                return true;
                            }
                            catch (TargetInvocationException ex)
                            {
                                detail = "clickedWithEventInfo threw: " +
                                    (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                                return false;
                            }
                        }
                    }
                }
            }

            string navDetail;
            if (SendNavigationTo(button, "submit", out navDetail))
            {
                detail = "clicked '" + infos[index].Name + "' via NavigationSubmitEvent";
                return true;
            }

            detail = "no click path worked for '" + infos[index].Name + "': " + navDetail;
            return false;
        }

        internal static bool Focus(string selector, out string detail)
        {
            detail = null;

            UiElementInfo[] infos; object[] elements; string error;
            if (!UiTree.SnapshotWithElements(out infos, out elements, out error))
            {
                detail = "ui snapshot failed: " + error;
                return false;
            }

            int index = IndexOfMatch(infos, selector, false);
            if (index < 0) { detail = "no visible element matched: " + selector; return false; }

            try
            {
                MethodInfo focus = AccessTools.Method(elements[index].GetType(), "Focus", new Type[0]);
                if (focus == null) { detail = "Focus() not found on " + infos[index].Type; return false; }
                focus.Invoke(elements[index], null);
                detail = "focused '" + infos[index].Name + "'";
                return true;
            }
            catch (Exception ex) { detail = "Focus threw: " + ex.Message; return false; }
        }

        /// <summary>Sends a navigation event to the focused element, or to the first root if none.</summary>
        internal static bool SendNavigation(string key, out string detail)
        {
            detail = null;

            UiElementInfo[] infos; object[] elements; string error;
            if (!UiTree.SnapshotWithElements(out infos, out elements, out error))
            {
                detail = "ui snapshot failed: " + error;
                return false;
            }
            if (elements.Length == 0) { detail = "no UI elements on screen"; return false; }

            object target = elements[0];
            for (int i = 0; i < infos.Length; i++)
                if (infos[i].Focused) { target = elements[i]; break; }

            return SendNavigationTo(target, key, out detail);
        }

        private static bool SendNavigationTo(object target, string key, out string detail)
        {
            detail = null;
            object evt = BuildNavigationEvent(key, out detail);
            if (evt == null) return false;

            try
            {
                MethodInfo send = AccessTools.Method(target.GetType(), "SendEvent",
                    new Type[] { AccessTools.TypeByName("UnityEngine.UIElements.EventBase") });
                if (send == null) { detail = "SendEvent(EventBase) not found"; return false; }
                send.Invoke(target, new object[] { evt });
                detail = "sent " + key;
                return true;
            }
            catch (Exception ex) { detail = "SendEvent threw: " + ex.Message; return false; }
        }

        private static object BuildNavigationEvent(string key, out string error)
        {
            error = null;
            string k = (key ?? string.Empty).Trim().ToLowerInvariant();

            Type modifiers = AccessTools.TypeByName("UnityEngine.UIElements.EventModifiers");
            if (modifiers == null) { error = "EventModifiers type not found"; return null; }
            object none = Enum.ToObject(modifiers, 0);

            if (k == "submit" || k == "enter")
                return GetPooledNoDirection("UnityEngine.UIElements.NavigationSubmitEvent", modifiers, none, out error);
            if (k == "cancel" || k == "escape" || k == "back")
                return GetPooledNoDirection("UnityEngine.UIElements.NavigationCancelEvent", modifiers, none, out error);

            if (k == "up" || k == "down" || k == "left" || k == "right")
            {
                Type moveType = AccessTools.TypeByName("UnityEngine.UIElements.NavigationMoveEvent");
                if (moveType == null) { error = "NavigationMoveEvent not found"; return null; }
                Type dirType = moveType.GetNestedType("Direction", BindingFlags.Public | BindingFlags.NonPublic);
                if (dirType == null) { error = "NavigationMoveEvent.Direction not found"; return null; }

                object dir;
                try { dir = Enum.Parse(dirType, k, true); }
                catch (Exception) { error = "no Direction value named '" + k + "'"; return null; }

                MethodInfo pooled = AccessTools.Method(moveType, "GetPooled", new Type[] { dirType, modifiers });
                if (pooled == null) { error = "NavigationMoveEvent.GetPooled(Direction, EventModifiers) not found"; return null; }
                try { return pooled.Invoke(null, new object[] { dir, none }); }
                catch (Exception ex) { error = "GetPooled threw: " + ex.Message; return null; }
            }

            error = "unknown key '" + key + "' (use up/down/left/right/submit/cancel)";
            return null;
        }

        /// <summary>
        /// NavigationSubmitEvent / NavigationCancelEvent do not declare GetPooled themselves — it
        /// is inherited from the shared generic base NavigationEventBase&lt;T&gt;, so the lookup
        /// needs FlattenHierarchy. Getting this wrong reports "no such method" on a method that
        /// is definitely there.
        /// </summary>
        private static object GetPooledNoDirection(string typeName, Type modifiers, object none, out string error)
        {
            error = null;
            Type t = AccessTools.TypeByName(typeName);
            if (t == null) { error = typeName + " not found"; return null; }

            MethodInfo pooled = t.GetMethod("GetPooled",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                null, new Type[] { modifiers }, null);
            if (pooled == null) { error = typeName + ".GetPooled(EventModifiers) not found"; return null; }

            try { return pooled.Invoke(null, new object[] { none }); }
            catch (Exception ex) { error = "GetPooled threw: " + ex.Message; return null; }
        }

        private static int IndexOfMatch(UiElementInfo[] infos, string selector, bool clickableOnly)
        {
            for (int i = 0; i < infos.Length; i++)
            {
                UiElementInfo e = infos[i];
                if (e == null || !e.Visible || !e.Enabled) continue;
                if (clickableOnly && !e.IsClickable) continue;
                if (UiSelector.Matches(e, selector)) return i;
            }
            return -1;
        }
    }
}
```

- [ ] **Step 3: Build the plugin**

```bash
dotnet build FTK2.Crucible/src/Crucible.Plugin -c Release
```

Expected: builds clean. There is no unit test for these two files by design — they exist only to touch Unity, and every decision they make that *can* be tested without Unity (selector matching, wedge verdicts, rendering) already lives in `Crucible.Core` and is tested in Task 1.

---

### Task 3: `UiCommands` — register the tools, then prove the escape live

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/UiCommands.cs`
- Modify: `FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs` (`TryRegister()` also calls `UiCommands.TryRegister()`)
- Modify: `FTK2.Crucible/src/Crucible.Core/CommandRequest.cs` (widen `CommandGate.ReadOnlyCommands`)
- Modify: `FTK2.Crucible/mcp/server.js` (A1–A10)

**Interfaces:**
- Consumes: `UiTree`, `UiDriver`, `ScreenTruth`, `ModalWatch`.
- Produces console commands `crucible_ui_dump`, `crucible_ui_click`, `crucible_ui_key`, `crucible_ui_focus`, `crucible_ui_text`, `crucible_screen`, `crucible_modals`, `crucible_escape`; MCP tools A1–A10.

**Why the gate widens.** `crucible_ui_dump`, `crucible_ui_click`, `crucible_ui_key`, `crucible_ui_focus`, `crucible_ui_text`, `crucible_screen`, `crucible_modals` and `crucible_escape` go on `CommandGate.ReadOnlyCommands`. Reading the screen changes nothing. Pressing a Close button and navigating menus change no replicated run state — and if they are *not* on the list, a harness that boots into the MP lobby has its own escape refused, because `IsOnlineSession()` fails closed. **The mutating verbs from M2 onward deliberately stay off the list.**

- [ ] **Step 1: Write `UiCommands`**

`FTK2.Crucible/src/Crucible.Plugin/UiCommands.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;

namespace Crucible.Plugin
{
    /// <summary>
    /// The UI-driving console commands.
    ///
    /// Every handler takes only string/int/bool: CommandLineHelper marshals the raw arg array
    /// into discrete typed parameters and its vocabulary is int/float/double/bool/string/
    /// Vector2/Vector3. A string[] handler is rejected at registration (verified in-game
    /// 2026-08-23). Registration is driven from the RouterMono tick by ReflectionCommands,
    /// because CommandLineHelper.Initialize has not run during plugin Awake.
    /// </summary>
    internal static class UiCommands
    {
        private static ManualLogSource _log;
        private static bool _registered;

        internal static void Initialize(ManualLogSource log) { _log = log; }

        internal static bool TryRegister()
        {
            if (_registered) return true;

            string[] existing = GameBridge.ListCommands();
            if (existing == null || existing.Length == 0) return false;

            Type t = typeof(UiCommands);
            BindingFlags pub = BindingFlags.Public | BindingFlags.Static;

            bool ok = true;
            ok &= GameBridge.RegisterCommand("crucible_ui_dump", t.GetMethod("UiDump", pub),
                new List<string> { "filter (or - for all)" });
            ok &= GameBridge.RegisterCommand("crucible_ui_click", t.GetMethod("UiClick", pub),
                new List<string> { "selector (#Name, \"text\", or bare)" });
            ok &= GameBridge.RegisterCommand("crucible_ui_key", t.GetMethod("UiKey", pub),
                new List<string> { "up|down|left|right|submit|cancel" });
            ok &= GameBridge.RegisterCommand("crucible_ui_focus", t.GetMethod("UiFocus", pub),
                new List<string> { "selector" });
            ok &= GameBridge.RegisterCommand("crucible_ui_text", t.GetMethod("UiText", pub),
                new List<string>());
            ok &= GameBridge.RegisterCommand("crucible_screen", t.GetMethod("Screen", pub),
                new List<string>());
            ok &= GameBridge.RegisterCommand("crucible_modals", t.GetMethod("Modals", pub),
                new List<string>());
            ok &= GameBridge.RegisterCommand("crucible_escape", t.GetMethod("Escape", pub),
                new List<string>());

            _registered = ok;
            if (_log != null) _log.LogInfo("UiCommands registration: " + (ok ? "ok" : "failed"));
            return _registered;
        }

        // ---------------------------------------------------------------- read

        public static void UiDump(string pFilter)
        {
            string error;
            UiElementInfo[] all = UiTree.Snapshot(out error);
            if (error != null) { ReflectionCommands.LastResult = "error: " + error; return; }

            string filter = (pFilter == null || pFilter == "-") ? null : pFilter;
            List<UiElementInfo> kept = new List<UiElementInfo>();
            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].Visible) continue;
                if (filter != null && !UiSelector.Matches(all[i], filter)) continue;
                kept.Add(all[i]);
            }
            ReflectionCommands.LastResult = UiElementInfo.RenderAll(kept.ToArray());
        }

        public static void UiText()
        {
            string error;
            UiElementInfo[] all = UiTree.Snapshot(out error);
            if (error != null) { ReflectionCommands.LastResult = "error: " + error; return; }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].Visible) continue;
                if (string.IsNullOrEmpty(all[i].Text)) continue;
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(all[i].Text);
            }
            ReflectionCommands.LastResult = sb.Length == 0 ? "(no visible text)" : sb.ToString();
        }

        public static void Screen()
        {
            ReflectionCommands.LastResult = ScreenTruth.Render(BuildScreenState());
        }

        public static void Modals()
        {
            ScreenState s = BuildScreenState();
            s.Elements = new UiElementInfo[0];   // oracles only; the cheap read
            ReflectionCommands.LastResult = ScreenTruth.Render(s);
        }

        // ---------------------------------------------------------------- write

        public static void UiClick(string pSelector)
        {
            if (string.IsNullOrEmpty(pSelector))
            {
                ReflectionCommands.LastResult = "error: usage: crucible_ui_click <selector>";
                return;
            }

            string detail;
            bool clicked = UiDriver.Click(pSelector, out detail);

            // Post-condition, never a dispatch receipt (spec §3): report the screen AFTER acting.
            ReflectionCommands.LastResult =
                "{\"clicked\":" + (clicked ? "true" : "false") +
                ",\"detail\":" + MiniJson.Quote(detail) +
                ",\"after\":" + ScreenTruth.Render(BuildScreenState()) + "}";
        }

        public static void UiKey(string pKey)
        {
            string detail;
            bool sent = UiDriver.SendNavigation(pKey, out detail);
            ReflectionCommands.LastResult =
                "{\"sent\":" + (sent ? "true" : "false") +
                ",\"detail\":" + MiniJson.Quote(detail) +
                ",\"after\":" + ScreenTruth.Render(BuildScreenState()) + "}";
        }

        public static void UiFocus(string pSelector)
        {
            string detail;
            bool focused = UiDriver.Focus(pSelector, out detail);
            ReflectionCommands.LastResult =
                "{\"focused\":" + (focused ? "true" : "false") +
                ",\"detail\":" + MiniJson.Quote(detail) + "}";
        }

        /// <summary>
        /// The boot-escape macro. Clicking comes FIRST and internals dismissal is the fallback,
        /// because on 2026-08-23 MultiplayerViewHelper.HideAllMenus() ran against the P0-16 modal
        /// and left it on screen, while the modal itself has a Close button.
        /// </summary>
        public static void Escape()
        {
            StringBuilder log = new StringBuilder();
            string[] labels = new string[] { "Close", "Back", "OK", "Cancel", "Continue" };

            for (int attempt = 0; attempt < 5; attempt++)
            {
                ScreenState before = BuildScreenState();
                string reason;
                if (!ScreenTruth.IsWedged(before, out reason))
                {
                    log.Append("clear after ").Append(attempt).Append(" attempt(s)");
                    ReflectionCommands.LastResult =
                        "{\"escaped\":true,\"log\":" + MiniJson.Quote(log.ToString()) +
                        ",\"after\":" + ScreenTruth.Render(before) + "}";
                    return;
                }

                log.Append("[").Append(attempt).Append("] wedged: ").Append(reason).Append("; ");

                bool acted = false;
                for (int i = 0; i < labels.Length && !acted; i++)
                {
                    string detail;
                    if (UiDriver.Click(labels[i], out detail))
                    {
                        log.Append("clicked ").Append(labels[i]).Append("; ");
                        acted = true;
                    }
                }

                if (!acted)
                {
                    string detail;
                    if (UiDriver.SendNavigation("cancel", out detail)) { log.Append("sent cancel; "); acted = true; }
                }

                if (!acted)
                {
                    // Last resort: the internals path that is known to have failed once.
                    string detail;
                    bool hid = ModalWatch.TryDismissAll(out detail);
                    log.Append("fallback dismiss=").Append(hid).Append(" (").Append(detail).Append("); ");
                }
            }

            ScreenState after = BuildScreenState();
            string finalReason;
            bool stillWedged = ScreenTruth.IsWedged(after, out finalReason);
            ReflectionCommands.LastResult =
                "{\"escaped\":" + (stillWedged ? "false" : "true") +
                ",\"log\":" + MiniJson.Quote(log.ToString()) +
                ",\"after\":" + ScreenTruth.Render(after) + "}";
        }

        private static ScreenState BuildScreenState()
        {
            ScreenState s = new ScreenState();
            s.Route = GameOwners.CurrentRouteName();
            ModalWatch.ReadOracles(out s.MpMenus, out s.Prompt, out s.SystemDialog,
                                   out s.Dialogue, out s.Transition, out s.Console);
            string error;
            s.Elements = UiTree.Snapshot(out error);
            return s;
        }
    }
}
```

> `GameOwners.CurrentRouteName()`, `ModalWatch.ReadOracles(out ×6)` and `ModalWatch.TryDismissAll(out string)` come from `2026-08-23-s3-traversal-verbs.md` Tasks 2–3. If that plan has not landed in this worktree, implement those three members first from that plan verbatim; do not re-derive them here, and do not inline a second copy.

- [ ] **Step 2: Widen the gate and wire registration**

In `FTK2.Crucible/src/Crucible.Core/CommandRequest.cs`, extend `CommandGate.ReadOnlyCommands`:

```csharp
private static readonly string[] ReadOnlyCommands = new string[]
{
    "toggleui", "togglevenuegrid", "toggleplayerhuds",
    "printdungeonconfighash", "printdialogueconfighash",
    "enablegamerandomstacktracerecording",
    // Crucible observation + escape. These must be read-only or the harness locks itself out:
    // GameBridge.IsOnlineSession() fails CLOSED, and CommandGate.Evaluate then denies everything
    // not listed here — including the verbs that get us out of the multiplayer lobby.
    "crucible_get", "crucible_ui_dump", "crucible_ui_click", "crucible_ui_key",
    "crucible_ui_focus", "crucible_ui_text", "crucible_screen", "crucible_modals",
    "crucible_escape"
};
```

Add a test to `FTK2.Crucible/src/Crucible.Core.Tests/CommandTests.cs`:

```csharp
TestHarness.Run("observation and escape verbs are read-only", delegate
{
    string[] shouldBeReadOnly = new string[]
    {
        "crucible_ui_dump", "crucible_ui_click", "crucible_screen",
        "crucible_modals", "crucible_escape"
    };
    for (int i = 0; i < shouldBeReadOnly.Length; i++)
        TestHarness.Assert(CommandGate.IsReadOnly(shouldBeReadOnly[i]),
            shouldBeReadOnly[i] + " must be read-only or the MP fail-closed gate traps the harness");
});

// NEGATIVE CONTROL: the gate must still deny mutating verbs in an online session.
TestHarness.Run("mutating verbs are NOT read-only", delegate
{
    string[] mustMutate = new string[]
    {
        "crucible_new_run", "crucible_party_class", "crucible_kill_all",
        "crucible_end_run", "crucible_objective_set"
    };
    for (int i = 0; i < mustMutate.Length; i++)
    {
        TestHarness.Assert(!CommandGate.IsReadOnly(mustMutate[i]), mustMutate[i] + " must not be read-only");
        TestHarness.Assert(CommandGate.Evaluate(mustMutate[i], true, false, null) == GateVerdict.DeniedMultiplayer,
            mustMutate[i] + " must be denied in an online session");
    }
});
```

In `ReflectionCommands.TryRegister()`, after the existing two registrations succeed, add:

```csharp
UiCommands.TryRegister();
```

and call `UiCommands.Initialize(log)` wherever `ReflectionCommands.Initialize(log)` is called in `CruciblePlugin`.

- [ ] **Step 3: SPIKE SP-1 — does clicking actually close the P0-16 modal?**

Deploy and launch with Steam running, then, from the state the game boots into:

```bash
# 1. What does the harness think, and what is actually on screen?
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_screen"}'

# 2. Every visible button, by name and text.
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_ui_dump","args":["Button"]}'

# 3. Press the Close button.
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_ui_click","args":["Close"]}'
```

**Expected:** step 1 returns `wedged:true` with `mpMenus:true` — and very likely a `route` that disagrees with what is on screen, which is the point. Step 2 lists a `Button` whose `name` or `text` contains `Close`. Step 3 returns `clicked:true` and an `after` block with `wedged:false`.

**Record the outcome in `docs/research/crucible-ui-driving.md` either way.** This is the spike that decides the architecture:

- **If step 3 works** — UI driving is CONFIRMED-LIVE and becomes the primary path for every dialog in this plan.
- **If `Clickable.clicked` is null** — the `clickedWithEventInfo` and `NavigationSubmitEvent` fallbacks are already in `UiDriver.Click`; report which one carried it.
- **If step 2 shows no matching button** — the modal is not a `Button`-based UIToolkit widget. Dump every element type on screen (`crucible_ui_dump -`) and record what it *is*. Do not widen the selector blindly; the answer changes what `UiDriver` must support.
- **If step 1 shows no elements at all** — `FindObjectsOfType(UIDocument)` is not reaching the modal's document. That is a bigger finding than this task and must be reported before M2 starts, because every later milestone assumes the tree is readable.

- [ ] **Step 4: Add the MCP tools**

In `FTK2.Crucible/mcp/server.js`, add to `TOOLS` (matching the existing entries' shape, `INSTANCE_ARG` spread into each):

```js
{ name: 'ftk2_ui_dump',
  description: 'Snapshot the visible UI tree: every element with type, name, text, visible, enabled, focused.',
  inputSchema: { type: 'object', properties: {
    filter: { type: 'string', description: 'Selector to filter by (#Name, "text", or bare). Omit for all.' },
    ...INSTANCE_ARG } } },
{ name: 'ftk2_ui_click',
  description: 'Press a button on screen by name or by its label text. Returns the screen state AFTER the click.',
  inputSchema: { type: 'object', required: ['selector'], properties: {
    selector: { type: 'string', description: '#Name (exact), "text" (substring), or a bare token trying both' },
    ...INSTANCE_ARG } } },
{ name: 'ftk2_ui_key',
  description: 'Send a navigation key: up, down, left, right, submit, cancel.',
  inputSchema: { type: 'object', required: ['key'], properties: {
    key: { type: 'string' }, ...INSTANCE_ARG } } },
{ name: 'ftk2_ui_focus',
  description: 'Move UI focus to an element, so ftk2_ui_key lands where intended.',
  inputSchema: { type: 'object', required: ['selector'], properties: {
    selector: { type: 'string' }, ...INSTANCE_ARG } } },
{ name: 'ftk2_ui_text',
  description: 'All visible text on screen, in tree order. Use this to read objectives and dialog bodies.',
  inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
{ name: 'ftk2_screen',
  description: 'Screen truth: route PLUS the six modal oracles PLUS the visible tree, and a wedged verdict. Use this instead of trusting route alone — route is provably not the rendered state.',
  inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
{ name: 'ftk2_modals',
  description: 'The six modal oracles only — the cheap wedge check.',
  inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
{ name: 'ftk2_escape',
  description: 'Escape whatever modal or lobby the game is stuck in: click Close/Back/OK, then cancel, then the internals fallback. Reports the screen afterwards.',
  inputSchema: { type: 'object', properties: { ...INSTANCE_ARG } } },
{ name: 'ftk2_ui_wait',
  description: 'Poll until a selector appears or disappears. Polling happens here, on the Node side — never on the game main thread.',
  inputSchema: { type: 'object', required: ['selector'], properties: {
    selector: { type: 'string' },
    gone: { type: 'boolean', description: 'Wait for absence instead of presence' },
    timeoutMs: { type: 'number', description: 'Default 30000' },
    ...INSTANCE_ARG } } }
```

`ftk2_ui_wait` is implemented in `server.js` as a loop over `ftk2_ui_dump` with a 250 ms interval and a hard timeout; it must return a timeout result, never hang.

- [ ] **Step 5: M1 acceptance**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release   # exit 0
node FTK2.Crucible/mcp/server.js --once ftk2_screen '{}'                 # wedged:false
```

**M1 is done when the game, launched cold, can be driven to a clean `MAIN_MENU` by `ftk2_escape` alone, and `ftk2_screen` reports `wedged:false` with `route:"MAIN_MENU"` and a non-empty element list.** Not before. If SP-1 failed, M1 is not done and M2 must not start — record the finding and stop.

---

## M2 — Navigation, run lifecycle, and state generation

### Task 4: Port the traversal verbs and prove the MP detach

**Files:**
- Create (port verbatim from `2026-08-23-s3-traversal-verbs.md` Tasks 1–4): `RouteNames.cs`, `GameOwners.cs`, `ModalWatch.cs`, `TraversalCommands.cs`, `RouteNameTests.cs`
- Modify: `mcp/server.js` (C1–C5, C7–C10)

**Interfaces:**
- Produces C1 `crucible_route`, C2 `crucible_mp_detach`, C3 `crucible_load_run`, C5 `crucible_campaigns`, C7 `crucible_save`, C8 `crucible_end_run`, C9 `crucible_quit_menu`, C10 `crucible_next_route`.

- [ ] **Step 1:** Port `RouteNames.cs` and `RouteNameTests.cs` from the traversal plan Task 1, unchanged. The 21-name list and the `EXIT` deny-list are already correct there.
- [ ] **Step 2:** Port `GameOwners.cs` and `ModalWatch.cs` from the traversal plan Tasks 2–3, adding `ModalWatch.ReadOracles(out ×6)` if that plan exposes the oracles individually rather than as a set — `UiCommands.BuildScreenState` depends on that exact shape.
- [ ] **Step 3:** Port `TraversalCommands.cs` from the traversal plan Task 4, with **one change**: every verb's post-condition becomes `ScreenTruth.Render(BuildScreenState())` rather than a bare route read. That plan predates the live finding that route diverges from the screen.
- [ ] **Step 4: SPIKE SP-3 — does clearing the auto-join actually prevent the boot lobby?**

```bash
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["Env.RoomAutoJoinId"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["NetworkHelper.CanAutoJoinRoom"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_mp_detach","args":["false"]}'
# then restart the game and check where it lands:
node FTK2.Crucible/mcp/server.js --once ftk2_screen '{}'
```

**Expected:** after detach and restart, `route` is `MAIN_MENU` and `mpMenus` is `false`. **If it still boots into the lobby**, the id is being re-derived at startup — `RouterMono._registerDebugCommands(String& autoJoinRoomId)` produces it by reference during startup, and `UserData.OnlineMutliplayerEnabled` (the game's own typo) persists in `User.ftk2`. Record which, and fall back to `ftk2_escape` at every boot rather than trying to prevent it. **Do not** reach for `DisconnectFromRoomAndResetNetworkData` first — it mutates `UserData` and is the heaviest of the three levers.

- [ ] **Step 5:** Add C1–C5 and C7–C10 to `mcp/server.js`, each documenting that `EXIT` is refused and that the post-condition is a screen read.

---

### Task 5: Start a campaign — `crucible_new_run` + `crucible_party_begin`

**Files:**
- Modify: `FTK2.Crucible/src/Crucible.Plugin/TraversalCommands.cs`
- Modify: `mcp/server.js` (C4, C6)

**Interfaces:**
- Produces C4 `crucible_new_run(string pAdventureId, string pDifficulty)`, C6 `crucible_party_begin()`.

These are the two riskiest verbs in M2 and are isolated here on purpose. Both have CONFIRMED signatures and ASSUMED behaviour.

- [ ] **Step 1:** Implement `crucible_new_run`: write `Env.SelectedAdventureConfig` and `Env.SelectedDifficulty` (parsing the difficulty string against `eGameDifficulties.{NONE, APPRENTICE, JOURNEYMAN, MASTER, GAUNTLET}`), then invoke `AdventureSelectionDirector._initializeAdventureInfo(String pSelected)`, then `._onNodeStartAdventureClick(NavigationSubmitEvent pEvent)` with `null`. Post-condition: `ftk2_screen` shows `route == PARTY_MANAGEMENT`.
- [ ] **Step 2:** Implement `crucible_party_begin`: invoke `PartyManagementDirector._initAdventureAndRoute(Boolean pOnlineAction)` with `false`. Post-condition: `ftk2_screen` shows `route == ADVENTURE` (allow up to 120 s — this is the heaviest `Initialize` in the game).
- [ ] **Step 3: SPIKE SP-4 — is `_initAdventureAndRoute` sufficient?**

```bash
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_new_run","args":["<id from ftk2_campaigns>","JOURNEYMAN"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_screen '{}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_party_begin","args":[]}'
node FTK2.Crucible/mcp/server.js --once ftk2_wait_for '{"path":"route","equals":"ADVENTURE","timeoutMs":120000}'
```

**Branches, all of which must be recorded in `crucible-traversal-inventory.md`:**
- Works → mark C4/C6 CONFIRMED-LIVE in the catalogue.
- Route does not advance → try `._processCharactersBeforeAdventureStart()` first, then `_initAdventureAndRoute` again.
- Still nothing → **fall back to the UI layer**: `ftk2_ui_dump` on the party screen, find the Begin button, `ftk2_ui_click` it. This fallback is the whole reason M1 comes first, and using it is a success, not a failure.
- `_onNodeStartAdventureClick(null)` throws → the UI event argument is not ignorable; use `ftk2_ui_click` for that step too and record that passing `null` for a `NavigationSubmitEvent` does not work, so no later task repeats the guess.

---

### Task 6: Saves, `saveUser`, and how a `GameSaveData` is reached

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/SaveCommands.cs`
- Modify: `mcp/server.js` (B7, K1)

**Interfaces:**
- Produces `crucible_runs()` (B7) and `crucible_save_user()` (K1), plus a recorded answer to SP-9.

- [ ] **Step 1:** Implement `crucible_runs`: read `Env.GameRuns` and `UserData.LastGameRunIdPlayed` and `UserData.LastPlayedVersionString`. **`Env.GameRuns` is a `List<String>` of run ids — VERIFIED LIVE 2026-08-23.** `crucible-load-path.md` §2's assumption that it was `List<GameSaveData>` was wrong; do not write code against that assumption.
- [ ] **Step 2: SPIKE SP-9 — how is a `GameSaveData` reached at runtime?**

The manifest in Task 7 wants `saveName`, `manualId`, `roundCount`, `difficulty`, `characters`, `chaosState`, `expansions`, `mapGenSeed` — all CONFIRMED fields on `GameSaveDirector+GameSaveData`, none currently reachable, because the list that was assumed to hold them holds strings instead.

```bash
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["Env.GameRuns"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["Env.User.LastGameRunIdPlayed"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["Env.ManualSaveIdTracker"]}'
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- GameSaveDirector --methods
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- Env --methods
```

**Record the answer in `docs/research/crucible-load-path.md` as a correction to §2.** If no runtime path to a `GameSaveData` exists, say so — and then the manifest is written from what the harness itself knows (route, round count, the verbs it applied, the live version and dataHash) with the `GameSaveData` fields **omitted rather than faked**. A manifest with honest gaps is usable; one with invented values is the `PlayerCount` bug again.

- [ ] **Step 3:** Implement `crucible_save_user`: `GameBridge.Exec("saveUser", …)`, then read `Env.GameRuns` before and after and report the new run id. **The post-condition is the new id, not the exec's return.**
- [ ] **Step 4: SPIKE SP-10 — does `saveUser` write a loadable save of the current position?**

```bash
# From a known position (e.g. ADVENTURE, round 3):
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_progress","args":[]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_save_user","args":[]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_runs","args":[]}'
# then quit to menu and load the id it reported:
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_quit_menu","args":[]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_load_run","args":["<new run id>","-"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_wait_for '{"path":"route","equals":"ADVENTURE","timeoutMs":180000}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_progress","args":[]}'
```

**Expected: the second `crucible_progress` matches the first.** Because `.ftk2` is obfuscated — UTF-8 BOM then control bytes, not JSON, gzip or protobuf — **a load-and-assert round trip is the only possible proof.** There is no offline validator and there never will be. If the round trip does not reproduce the position, the whole Tier-1 fixture design is unsound and Task 7 must stop and report rather than build on it.

---

### Task 7: Tier-1 fixtures — manifest, staleness gate, capture, regeneration

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/FixtureManifest.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/FixtureManifestTests.cs`
- Create: `FTK2.Crucible/tools/fixtures.ps1`
- Create: `FTK2.Crucible/data/Fixtures/_stale-control/fixture.json`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`
- Modify: `mcp/server.js` (K2–K6, K9, K10)

**Interfaces:**
- Consumes: K1 (Task 6), C3, A7, B8.
- Produces:
  - `sealed class FTK2Mods.Crucible.FixtureManifest { string Id; string CreatedUtc; string GameVersion; string DataHash; string RunId; string Position; string[] DependsOnIds; string Base; bool Regenerable; }`
  - `static FixtureManifest FixtureManifest.Parse(string json, out string error)`
  - `static string FixtureManifest.Render(FixtureManifest m)`
  - `static bool FTK2Mods.Crucible.Staleness.IsFresh(FixtureManifest m, string liveVersion, string liveDataHash, string[] resolvedIds, out string[] failures)`
  - MCP tools K2 `ftk2_fixture_capture`, K3 `ftk2_fixture_list`, K4 `ftk2_fixture_verify`, K5 `ftk2_fixture_load`, K6 `ftk2_fixture_restore`, K9 `ftk2_fixture_regen`, K10 `ftk2_saves_guard`

**Why the gate refuses instead of warning.** A fixture references content ids. Rename a trait, retune a recipe, redeploy a pack, and the fixture is silently *wrong* — producing a green test against content that no longer exists. That is worse than a red test. The obfuscated save format removes every cheaper way to notice, so the manifest gate is the only defence there is, and a defence that can be ignored is not one.

- [ ] **Step 1: Write the failing tests**

`FTK2.Crucible/src/Crucible.Core.Tests/FixtureManifestTests.cs`:

```csharp
using System;
using FTK2Mods.Crucible;

namespace Crucible.Core.Tests
{
    internal static class FixtureManifestTests
    {
        private static FixtureManifest Fresh()
        {
            FixtureManifest m = new FixtureManifest();
            m.Id = "combat-round-1";
            m.CreatedUtc = "2026-08-23T18:04:11Z";
            m.GameVersion = "1.14.6";
            m.DataHash = "sha256:b7e5e871";
            m.RunId = "run-0001";
            m.Position = "COMBAT";
            m.DependsOnIds = new string[] { "CF_EOR_BARD", "CF_EOR_WARDEN" };
            m.Regenerable = true;
            return m;
        }

        internal static void Run()
        {
            TestHarness.Section("FixtureManifest");

            TestHarness.Run("render then parse round-trips every field", delegate
            {
                string error;
                FixtureManifest back = FixtureManifest.Parse(FixtureManifest.Render(Fresh()), out error);
                TestHarness.Assert(error == null, "parse error: " + error);
                TestHarness.Assert(back.Id == "combat-round-1", "Id lost");
                TestHarness.Assert(back.GameVersion == "1.14.6", "GameVersion lost");
                TestHarness.Assert(back.DataHash == "sha256:b7e5e871", "DataHash lost");
                TestHarness.Assert(back.DependsOnIds.Length == 2, "DependsOnIds lost");
            });

            // The half that matters as much as the refusals: a matching fixture must be ACCEPTED.
            TestHarness.Run("a matching fixture is fresh", delegate
            {
                string[] failures;
                bool fresh = Staleness.IsFresh(Fresh(), "1.14.6", "sha256:b7e5e871",
                    new string[] { "CF_EOR_BARD", "CF_EOR_WARDEN", "CF_EOR_OTHER" }, out failures);
                TestHarness.Assert(fresh, "should be fresh; failures: " + string.Join("; ", failures));
                TestHarness.Assert(failures.Length == 0, "expected no failures");
            });

            // NEGATIVE CONTROL 1: a game update must invalidate the fixture.
            TestHarness.Run("a game version mismatch is stale", delegate
            {
                string[] failures;
                bool fresh = Staleness.IsFresh(Fresh(), "1.15.0", "sha256:b7e5e871",
                    new string[] { "CF_EOR_BARD", "CF_EOR_WARDEN" }, out failures);
                TestHarness.Assert(!fresh, "version mismatch must be stale");
                TestHarness.Assert(failures.Length == 1 &&
                    failures[0].IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0,
                    "failure should name the version, got: " + string.Join("; ", failures));
            });

            // NEGATIVE CONTROL 2: a content retune must invalidate the fixture.
            TestHarness.Run("a dataHash mismatch is stale", delegate
            {
                string[] failures;
                TestHarness.Assert(!Staleness.IsFresh(Fresh(), "1.14.6", "sha256:deadbeef",
                    new string[] { "CF_EOR_BARD", "CF_EOR_WARDEN" }, out failures), "dataHash mismatch must be stale");
            });

            // NEGATIVE CONTROL 3: a renamed class must invalidate the fixture.
            TestHarness.Run("an unresolvable content id is stale", delegate
            {
                string[] failures;
                bool fresh = Staleness.IsFresh(Fresh(), "1.14.6", "sha256:b7e5e871",
                    new string[] { "CF_EOR_BARD" }, out failures);   // WARDEN was renamed away
                TestHarness.Assert(!fresh, "missing id must be stale");
                TestHarness.Assert(failures.Length == 1 &&
                    failures[0].IndexOf("CF_EOR_WARDEN", StringComparison.Ordinal) >= 0,
                    "failure should name the missing id, got: " + string.Join("; ", failures));
            });

            // NEGATIVE CONTROL 4: all three failing at once must all be reported, not just the first.
            TestHarness.Run("the deliberately-stale control fails all three checks", delegate
            {
                FixtureManifest m = Fresh();
                m.Id = "_stale-control";
                m.GameVersion = "0.0.0-stale";
                m.DataHash = "sha256:0000";
                m.DependsOnIds = new string[] { "CF_DOES_NOT_EXIST" };

                string[] failures;
                TestHarness.Assert(!Staleness.IsFresh(m, "1.14.6", "sha256:b7e5e871",
                    new string[] { "CF_EOR_BARD" }, out failures), "control must be stale");
                TestHarness.Assert(failures.Length == 3,
                    "expected 3 failures (version, dataHash, id), got " + failures.Length + ": "
                    + string.Join("; ", failures));
            });

            // NEGATIVE CONTROL 5: an unparseable manifest is stale, never silently fresh.
            TestHarness.Run("a manifest that will not parse is stale", delegate
            {
                string error;
                FixtureManifest bad = FixtureManifest.Parse("{ not json", out error);
                TestHarness.Assert(bad == null && error != null, "bad JSON must report an error");

                string[] failures;
                TestHarness.Assert(!Staleness.IsFresh(null, "1.14.6", "sha256:b7e5e871",
                    new string[0], out failures), "a null manifest must be stale");
            });

            TestHarness.Run("a null live version is stale, not a pass", delegate
            {
                string[] failures;
                TestHarness.Assert(!Staleness.IsFresh(Fresh(), null, "sha256:b7e5e871",
                    new string[] { "CF_EOR_BARD", "CF_EOR_WARDEN" }, out failures),
                    "unknown live version must fail closed");
            });
        }
    }
}
```

Add `FixtureManifestTests.Run();` to `Program.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: compile errors — `FixtureManifest` and `Staleness` do not exist.

- [ ] **Step 3: Implement `FixtureManifest` and `Staleness`**

`FTK2.Crucible/src/Crucible.Core/FixtureManifest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// The metadata committed beside a Tier-1 fixture.
    ///
    /// This exists because .ftk2 saves are obfuscated on disk (UTF-8 BOM then control bytes —
    /// not JSON, not gzip, not protobuf). A fixture's contents cannot be inspected, validated or
    /// diffed by any tool we can write; the ONLY thing that can be checked cheaply is this
    /// manifest. That is why it must be complete and why the gate below refuses rather than warns.
    /// </summary>
    public sealed class FixtureManifest
    {
        public string Id;
        public string CreatedUtc;
        public string GameVersion;      // UserData.LastPlayedVersionString at capture
        public string DataHash;         // ClassForge pack dataHash at capture
        public string RunId;            // the GameRuns\<runID> directory this fixture came from
        public string Position;         // the route asserted on load
        public string[] DependsOnIds;   // every content id the fixture references
        public string Base;             // Tier-2 snapshots name their base fixture; null otherwise
        public bool Regenerable;        // false when the recipe cannot be scripted (see the plan §SP-11)

        public static string Render(FixtureManifest m)
        {
            if (m == null) return "{}";
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"id\":").Append(MiniJson.Quote(m.Id))
              .Append(",\"createdUtc\":").Append(MiniJson.Quote(m.CreatedUtc))
              .Append(",\"gameVersion\":").Append(MiniJson.Quote(m.GameVersion))
              .Append(",\"dataHash\":").Append(MiniJson.Quote(m.DataHash))
              .Append(",\"runId\":").Append(MiniJson.Quote(m.RunId))
              .Append(",\"position\":").Append(MiniJson.Quote(m.Position))
              .Append(",\"base\":").Append(MiniJson.Quote(m.Base))
              .Append(",\"regenerable\":").Append(m.Regenerable ? "true" : "false")
              .Append(",\"dependsOnIds\":[");
            if (m.DependsOnIds != null)
                for (int i = 0; i < m.DependsOnIds.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(MiniJson.Quote(m.DependsOnIds[i]));
                }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Returns null plus an error rather than throwing — a manifest we cannot read is stale.</summary>
        public static FixtureManifest Parse(string json, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "empty manifest"; return null; }

            object parsed;
            if (!MiniJson.TryParse(json, out parsed, out error)) return null;

            Dictionary<string, object> map = parsed as Dictionary<string, object>;
            if (map == null) { error = "manifest is not a JSON object"; return null; }

            FixtureManifest m = new FixtureManifest();
            m.Id = Str(map, "id");
            m.CreatedUtc = Str(map, "createdUtc");
            m.GameVersion = Str(map, "gameVersion");
            m.DataHash = Str(map, "dataHash");
            m.RunId = Str(map, "runId");
            m.Position = Str(map, "position");
            m.Base = Str(map, "base");

            object reg;
            m.Regenerable = map.TryGetValue("regenerable", out reg) && reg is bool && (bool)reg;

            List<string> ids = new List<string>();
            object raw;
            if (map.TryGetValue("dependsOnIds", out raw))
            {
                List<object> list = raw as List<object>;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null) ids.Add(list[i].ToString());
            }
            m.DependsOnIds = ids.ToArray();

            if (string.IsNullOrEmpty(m.Id)) { error = "manifest has no id"; return null; }
            return m;
        }

        private static string Str(Dictionary<string, object> map, string key)
        {
            object v;
            if (!map.TryGetValue(key, out v) || v == null) return null;
            return v.ToString();
        }
    }

    /// <summary>
    /// The staleness gate. It REFUSES; it does not warn.
    ///
    /// The failure it prevents: a fixture references content ids, someone renames a trait or
    /// retunes a recipe, and the fixture is silently wrong — a green test against content that no
    /// longer exists. Same shape as StateReader reading NetworkData.PlayerCount for months: a
    /// plausible value, confidently served, wrong.
    ///
    /// Every check reports independently, so a caller sees all the reasons at once instead of
    /// fixing one and rediscovering the next.
    /// </summary>
    public static class Staleness
    {
        public static bool IsFresh(FixtureManifest m, string liveVersion, string liveDataHash,
                                   string[] resolvedIds, out string[] failures)
        {
            List<string> f = new List<string>();

            if (m == null)
            {
                failures = new string[] { "no manifest (unreadable or missing) — refusing" };
                return false;
            }

            if (string.IsNullOrEmpty(liveVersion))
                f.Add("live game version is unknown — refusing (fail closed)");
            else if (!string.Equals(m.GameVersion, liveVersion, StringComparison.Ordinal))
                f.Add("game version mismatch: fixture " + m.GameVersion + " vs live " + liveVersion);

            if (string.IsNullOrEmpty(liveDataHash))
                f.Add("live dataHash is unknown — refusing (fail closed)");
            else if (!string.Equals(m.DataHash, liveDataHash, StringComparison.Ordinal))
                f.Add("dataHash mismatch: fixture " + m.DataHash + " vs live " + liveDataHash);

            if (m.DependsOnIds != null)
            {
                for (int i = 0; i < m.DependsOnIds.Length; i++)
                {
                    string need = m.DependsOnIds[i];
                    bool found = false;
                    if (resolvedIds != null)
                        for (int j = 0; j < resolvedIds.Length; j++)
                            if (string.Equals(resolvedIds[j], need, StringComparison.Ordinal)) { found = true; break; }
                    if (!found) f.Add("content id no longer resolves: " + need);
                }
            }

            failures = f.ToArray();
            return f.Count == 0;
        }
    }
}
```

> `MiniJson.TryParse(string, out object, out string)` and `MiniJson.Quote(string)` are the existing helpers in `FTK2.Crucible/src/Crucible.Core/MiniJson.cs`. Use whatever surface it exposes; do not add a second JSON implementation to this repo.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, with **five negative controls** in `FixtureManifestTests` plus the positive "a matching fixture is fresh" case — the one that proves the gate is not simply refusing everything.

- [ ] **Step 5: Write the fixture tooling and capture the base set**

`FTK2.Crucible/tools/fixtures.ps1` implements, in this order:

1. **`Guard`** (K10) — assert that `%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\GameRuns\` is the per-run scratch junction and not the owner's real folder, using the spec §9 marker file. **Every other function calls `Guard` first and stops if it fails.** The owner's real co-op saves live in that exact folder; the Unity save path is identity-derived so a copied install does not isolate it. Backup at `C:\Users\ben\Backups\ftk2-2026-08-23\`.
2. **`Capture -Id <fixtureId>`** (K2) — read `Env.GameRuns` before, call `crucible_save_user`, read after, diff to get the new run id, copy `GameRuns\<runID>\` to `FTK2.Crucible/data/Fixtures/<id>/`, and write `fixture.json` from the live `UserData.LastPlayedVersionString`, the live ClassForge `dataHash`, the current `crucible_screen` route, and the ids the recipe named.
3. **`List`** (K3) and **`Verify -Id`** (K4) — the latter shelling the freshness decision to `Staleness.IsFresh`, never re-implementing it in PowerShell.
4. **`Load -Id`** (K5) — `Guard` → `Verify` → copy in → `crucible_load_run` → assert `crucible_screen` matches `position`.
5. **`Restore`** (K6) — idempotent teardown.
6. **`Regen -Id`** (K9) — replay the manifest recipe, then `Capture`.

Then capture the eight base fixtures named in the catalogue §5.1: `main-menu-clean`, `party-fresh`, `overworld-early`, `overworld-mid`, `combat-round-1`, `combat-multi-wave`, `rest-site`, `dungeon-floor-1`.

**SPIKE SP-11 — is each one regenerable?** For each fixture, after capturing it manually, run `Regen` and check the replay reaches the same `position`. Set `regenerable: true` in its manifest only if it does. **A fixture whose recipe would need `move` (F3) or `use_ability` (D11) cannot be scripted today — mark it `regenerable: false` and say so in the manifest rather than pretending.** Eight fixtures must not mean eight manual play sessions per content change; record honestly how many actually avoid that.

- [ ] **Step 6: Commit the stale control**

`FTK2.Crucible/data/Fixtures/_stale-control/fixture.json`:

```json
{
  "id": "_stale-control",
  "createdUtc": "2026-08-23T00:00:00Z",
  "gameVersion": "0.0.0-stale",
  "dataHash": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
  "runId": "does-not-exist",
  "position": "MAIN_MENU",
  "base": null,
  "regenerable": false,
  "dependsOnIds": ["CF_DOES_NOT_EXIST"]
}
```

There is deliberately **no save directory beside it** — it is a manifest only. `ftk2_fixture_verify _stale-control` must refuse it and name all three failing checks, and `ftk2_fixture_load _stale-control` must refuse to load anything. A gate that has never been seen to fire is not a gate.

---

## M3 — State reading, including objectives

### Task 8: Progress, quests, objectives, entity handles

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/EntityHandles.cs`
- Modify: `FTK2.Crucible/src/Crucible.Plugin/WorldCommands.cs`
- Modify: `mcp/server.js` (B8, B9, B10, G1)

- [ ] **Step 1:** `crucible_progress` (B8) — `GameRunData.RoundCount`, `.GameStageIndex`, `.GameStageRoundStart`, `.AdventureState`; `AdventureState.CurrentStoryQuestTitle`, `.CurrentOptionalQuestTitle`, `.TotalRoundCount`.
- [ ] **Step 2:** `crucible_quests` (B9) and `crucible_objectives` (G1) — `GameRunData.ActiveQuests` / `.CompletedQuests` / `.FailedQuests` / `.FutureQuests`; per quest, `QuestState.Data` → `QuestData.ID` / `.Objectives` / `.ObjectiveCustomTexts` / `.CompleteObjectives` / `.AdventureEndTrigger`, and `QuestState.CompletedObjectives` / `.ObjectivesProgress` / `.RoundsLeft`.
- [ ] **Step 3:** `crucible_entities` (B10) — the handle table. Emit `index`, `Entity.Guid`, `CharacterComponent.DisplayName`, `.ConfigName`, and `isPlayer` derived from `PlayerComponent` presence (there is **no** `isPlayer` field anywhere; `eCharacterTypes` has no `PLAYER` value). Scope `combat` reads `CombatState.Entities`; scope `party` reads `PartyManagementDirector._playerEntities`.
- [ ] **Step 4:** Cross-check objectives two ways — `crucible_objectives` (the data) against `ftk2_ui_text` (what the player sees). **Where they disagree, the UI is right about what is displayed and the data is right about what the engine believes**; record any systematic divergence, because a scenario asserting on one while a human reads the other is a whole class of confusing failures.

### Task 9: `crucible.state.v2` and the phase-advance dispatcher

**Files:**
- Modify: `FTK2.Crucible/src/Crucible.Plugin/CombatCommands.cs`, `RpcServer.cs`
- Modify: `mcp/server.js` (B3, D2, D3, J7, J8)

- [ ] **Step 1:** Implement `crucible.state.v2` per `2026-08-23-s1-observation-surface.md` — that plan's schema decisions stand and must not be relitigated here: `stacks` is omitted, `stats` is string-keyed, `turn` and `phase` are synthesized and labelled as such, `round` is documented non-monotonic.
- [ ] **Step 2:** `crucible_combat` (D3) — the raw `CombatState` read including `EndCombatEarly`, `AlternateWinConditions`, `AlternateLoseConditions`, `WaveIndex`.
- [ ] **Step 3:** `crucible_advance` (D2) — the dispatcher. **`RestPhase._debugEndPhase` takes an `Int32` and has no zero-arg overload**; a dispatcher that assumes uniform arity fails on REST.
- [ ] **Step 4: SPIKE SP-5 — what gates `_areDebugCommandsAllowed`?**

```bash
# On every route the harness can reach, in turn:
node FTK2.Crucible/mcp/server.js --once ftk2_list_commands '{}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["CombatPhase._areDebugCommandsAllowed"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["AppConfigManager.AppConfig.BlockNetworkUnsafeCheats"]}'
```

Record a per-route table of the command count and the flag in `crucible-traversal-inventory.md`. **If the flag is false on some route, `crucible_advance` and half of M5 do not work there**, and the plan must say which routes rather than discovering it during a soak. Never cache the command list — it is a function of route, because each phase registers on entry and calls `_unregisterCommonConsoleCommands()` on exit.
- [ ] **Step 5:** `ftk2_wedge_report` (J7) and `ftk2_owner` (J8).

---

## M4 — Combat and party

### Task 10: Combat verbs

**Files:** Modify `CombatCommands.cs`, `mcp/server.js` (D4–D10, I2)

- [ ] **Step 1:** `crucible_next_turn` (D4), `crucible_end_turn` (D5) — `CombatHelper.NextTurn(Env, GameRandom)` / `.EndTurn(Boolean, Env, GameRandom)`, resolving `Env` and the phase's `GameRandom` through `GameOwners`.
- [ ] **Step 2:** `crucible_kill_all` (D6) — loop `CombatState.Entities`, filter with `CharacterHelper.IsOpponent` / `.IsFriendly`, call `CharacterHelper.TryKillCharacter`. Post-condition: re-read `crucible.state.v2` and assert the alive count dropped by exactly the number targeted.
- [ ] **Step 3:** `crucible_end_combat` (D7), `crucible_combat_early` (D8), `crucible_next_wave` (D9). **`CombatHelper.TryEndGame(CombatState)` carries no victory flag** — do not build `force_win` / `force_lose` here; they are `crucible_end_run <bool>` at run scope.
- [ ] **Step 4:** `crucible_god_mode` (I2) — `CombatPhase._debugSetPlayersTo9999HP()` / `._debugSetAllCharactersTo9999HP()`.
- [ ] **Step 5: SPIKE SP-6 — can `use_ability` (D11) be built at all?**

```bash
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- CombatDecisionData
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- Thing
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_abilities","args":["0"]}'
```

The target is `CombatPhase._performAbility(Entity, Thing, CombatDecisionData, List\`1, Boolean pTryProceed, Boolean pIsScripted)` — **not** `CombatHelper.PerformAbility`, which needs a `Func\`4`, a `Func\`3` and a `SkillContext` the harness cannot synthesise. The `pIsScripted` flag reads like it exists precisely for non-UI callers (ASSUMED). **If `CombatDecisionData` cannot be constructed from primitives plus a handle, report `use_ability` as still UNKNOWN and do not stub it.** A verb whose API does not exist is dropped, not faked (spec §5). Combat remains skippable-but-not-playable, and the catalogue §7 item 2 stands.

### Task 11: Party and world verbs

**Files:** Modify `CombatCommands.cs`, `WorldCommands.cs`, `mcp/server.js` (E1–E5, F1–F2, F4–F12)

- [ ] **Step 1:** `crucible_party` (E1), `crucible_party_class` (E2), `crucible_party_randomize` (E3), `crucible_party_remove` (E4), `crucible_set_level` (E5).
- [ ] **Step 2:** **Serialize class swaps.** `PartyManagementDirector._classChangeDelayToken` (a `CancellationTokenSource`) and `._customizationLock` (a `SemaphoreSlim`) mean two swaps in one frame cancel rather than queue. Each `crucible_party_class` must re-read the slot's `CharacterComponent.ConfigName` as its post-condition, and the 35-class matrix must issue swaps one at a time, each confirmed.
- [ ] **Step 3:** `crucible_overworld_end_turn` (F1), `crucible_overworld_proceed` (F2), `crucible_stop_pick_hex` (F4), `crucible_hexmap` (F5), `crucible_time_of_day` (F6), `crucible_dungeon` (F8), `crucible_dungeon_next_phase` (F9), `crucible_dungeon_complete` (F10), `crucible_phase_action` (F11), `crucible_goto_next_route` (F12).
- [ ] **Step 4: SPIKE SP-7 — the hex tuple.**

```bash
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_get","args":["Env.HexMap"]}'
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- AdventureMoveData
```

The `ValueTuple\`2` element types are ASSUMED `(Int32, Int32)` from ~10 uniform sibling call sites and confirmed by none of them. `Vector2` **is** in the marshaller vocabulary and may carry a hex. **If the tuple can be constructed and `_move` accepts it, `move` (F3) and possibly `teleport` (F7) leave UNKNOWN — record it. If not, they stay UNKNOWN and the overworld remains turnable but not steerable**, which the catalogue §7 item 3 already says out loud.

---

## M5 — Speedrun skips

### Task 12: The skip layer, and proving a skip skipped

**Files:** Modify `WorldCommands.cs`, `RngCommands.cs`, `mcp/server.js` (G2, G3, G5, H1, H2, H5, H6, I1, I3–I8)

- [ ] **Step 1:** `crucible_pin_seed` (H1) — write `GameRandom.Seed` on **every** live instance (`CombatState.Random`, `CombatPhase._gameRandom`, `AdventureDirector._gameRandom`, `EncounterPhase._gameRandom`, `MainMenuDirector._gameRandom`); `crucible_map_seed` (H6); `crucible_rig_wheel` (H5).
- [ ] **Step 2:** `crucible_rng` (H2) — Harmony counters on **Tier A primitives only**. Patching the Tier B selection helpers (`GetRandomElementFrom*`, `GetRandomKeyFromDictionary`, `GetRandomValueFromDictionary`, `ShuffleList`) as well would double-count every loot and shuffle roll if they are composites over Tier A. Verify with the call-count check `crucible-rng-field-map.md` §3 specifies: call `GetRandomElementFromList` once with counting patches on both `NextInt` and the helper; if the counter increments by 2, Tier B is composite and stays unpatched.
- [ ] **Step 3: SPIKE SP-8 — does an externally-set objective flag take?**

```bash
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_objectives","args":[]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_objective_set","args":["0","0","true"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_try_complete_quests","args":[]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_quests","args":[]}'
```

`QuestState.CompletedObjectives` is a public, settable `Boolean[]`; that the game's own `_tryCompleteQuests` / `_resolveQuests` pass picks up an externally-set flag has **never been observed**. **Expected:** the quest moves from `ActiveQuests` to `CompletedQuests`. **If it does not**, `objective_set` and `complete_all_objectives` (I5) are dead, quest-level skipping falls back to `crucible_end_run <bool>` at run scope only, and that must be recorded in `crucible-quest-progression.md` as an answer to its own open verdict.
- [ ] **Step 4:** `crucible_dungeon_force_end` (I4) — write `DungeonState.ForceEndDungeon`, then `DungeonDirector._nextPhase()`. This is the escape for the one route where `QuitToMenu()` can be refused (`_canQuitToMenu`).
- [ ] **Step 5:** `crucible_skip_outro` (I6) — write `AppConfigManager+AppConfig.SkipOutroCinematic`. Whether it is read at route time is ASSUMED; if a run still stalls on the outro, the only remaining lever is `ftk2_ui_click` on whatever the cinematic renders, which is SP-2.
- [ ] **Step 6:** **Every skip verb's post-condition is an M3 read, not its own return.** `kill_all` proves itself with the alive count; `advance` with the route change; `objective_set` with the quest list; `dungeon_force_end` with `DungeonState`. A skip that reports success without a state change is the `MultiplayerDemoQuickCombat` bug wearing a new hat.

---

## M6 — The end-to-end seeded full run

### Task 13: Tier-2 state application and the scenario surface

**Files:** Create `FTK2.Crucible/data/States/*.json`; modify `mcp/server.js` (K7, K8), `Crucible.Core/ScenarioRunner` per spec §6

- [ ] **Step 1:** Implement `ftk2_apply_state` (K7) in `ScenarioRunner`: read `data/States/<featureId>.json`, load its `base` fixture via K5, then execute each `apply` step as an ordinary verb call **with its own post-condition**. A failed setup step fails the scenario — it must never proceed with a differently-configured test. This is not a console command; the marshaller cannot take a JSON blob.
- [ ] **Step 2:** Implement `ftk2_state_snapshot` (K8): run K1 + K2 against the current state and write a manifest carrying `base` and the exact `apply` list. This is what gives the owner per-feature *saves*, not just per-feature verb lists.
- [ ] **Step 3:** Author one real Tier-2 state end to end — the spec §6 `bard-crescendo` example on top of `combat-round-1` — and run it. It exercises `party_class`, `set_level`, `give`, `pin_seed`, `force_roll` and an `expect` on `combat.combatants[?isPlayer].statuses[*].id`.

### Task 14: The full run

**Files:** Modify `mcp/server.js` (I8 `ftk2_autopilot`), `FTK2.Crucible/tools/` soak entry point

- [ ] **Step 1:** Implement `ftk2_autopilot` (I8) as the loop: `ftk2_screen` → if wedged, `ftk2_escape` → else `crucible_next_route` for the game's own opinion of where to go → act with `crucible_advance` / `crucible_dungeon_next_phase` / `crucible_goto_next_route` → `crucible_save` at every REST → repeat until the goal or the wall-clock budget. Checkpoint after each phase; resumable; a dead RPC port triggers relaunch-and-resume (spec §9).
- [ ] **Step 2: The acceptance run.**

```bash
node FTK2.Crucible/mcp/server.js --once ftk2_saves_guard '{}'
node FTK2.Crucible/mcp/server.js --once ftk2_fixture_load '{"fixtureId":"main-menu-clean"}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_pin_seed","args":["12345","all"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_new_run","args":["<adventureId>","APPRENTICE"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_party_class","args":["0","CF_EOR_BARD"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_party_begin","args":[]}'
node FTK2.Crucible/mcp/server.js --once ftk2_exec '{"command":"crucible_god_mode","args":["false"]}'
node FTK2.Crucible/mcp/server.js --once ftk2_autopilot '{"goal":"victory","budgetMs":14400000}'
node FTK2.Crucible/mcp/server.js --once ftk2_read_trace '{"n":200}'
node FTK2.Crucible/mcp/server.js --once ftk2_fixture_restore '{}'
```

**Acceptance — all of these, or M6 is not done:**
1. The run reaches victory, or `ftk2_autopilot` reports precisely where and why it stopped. **A stall reported honestly is a pass for the harness and a finding for the plan; a stall reported as success is a failure.**
2. Every phase transition appears in the trace with a post-condition read, not a dispatch receipt.
3. No write occurred outside the scratch junction — `ftk2_saves_guard` passed at the start and `ftk2_fixture_restore` succeeded at the end.
4. Re-running with the same seed and the same fixture produces the same `ftk2_compare_state` digest at the same checkpoints. If it does not, `pin_seed` (H1) is not being honoured and that is a finding, recorded, not smoothed over.

- [ ] **Step 3:** Record every spike's answer back into the catalogue's status column and into the relevant `docs/research/` file. **The catalogue is only useful if its statuses stay true** — a tool that moved from ASSUMED to CONFIRMED-LIVE, or from ASSUMED to UNKNOWN, must be updated the day it moves.
- [ ] **Step 4: SPIKE SP-2 — the cinematic routes.**

If the acceptance run reaches a `CINEMATIC_*` route, immediately:

```bash
node FTK2.Crucible/mcp/server.js --once ftk2_ui_dump '{}'
node FTK2.Crucible/mcp/server.js --once ftk2_ui_text '{}'
node FTK2.Crucible/mcp/server.js --once ftk2_screenshot '{"label":"cinematic"}'
```

**This is the only genuine gap in the whole surface.** `CinematicDirector`, `CinematicPhase`, `CinematicHelper` and `VideoDirector` are all NOT FOUND among the 5389 loaded types; `RouterMono` has no cinematic field; there is no known entry, exit, or arrival proof for any of the three routes. The UI layer is the only proposed handle, precisely because it needs no owning type. **Record what the tree contains — even "nothing" is the answer that tells the next person to stop looking for a Director.** Then check whether `crucible_skip_outro` (I6) removed the outro at all.

---

## Self-Review

**Spec coverage.** §2 grounding rule → every name in this plan is quoted in `docs/research/crucible-mcp-tool-surface.md`, which cites the eight field maps; SP-9 and SP-10 exist because two assumptions in those docs were caught wrong and must be corrected in place rather than coded around. §3 architecture and error posture → `Crucible.Core` stays Unity-free (`UiSelector`, `ScreenTruth`, `FixtureManifest` are pure and tested with no game); every verb reports a post-condition, and Task 3's `UiClick` embeds the `after` screen in its own result so "dispatched" cannot be mistaken for "worked". §5 verb layer → M2–M5, with the honest split preserved: verbs whose API does not exist are reported infeasible and dropped, not stubbed (SP-6, SP-7). §6 scenario runner → M6 Task 13. §9 isolation → K10 `saves_guard` gates every state-generation write, and M6's acceptance requires guard-pass plus restore-success. §10 testing standard → thirteen negative controls across three pure suites.

**The catalogue-to-plan mapping is complete.** A1–A10 → M1. B1–B11 → M1 (A7/B6) and M3 (B3, B8–B10). C1–C10 → M2 Tasks 4–5. D1–D10 → M4 Task 10; D11 → SP-6, explicitly not built. E1–E9 → M4 Task 11. F1–F2, F4–F12 → M4 Task 11; F3 and F7 → SP-7, explicitly not built. G1–G3, G5 → M3/M5; G4 → not built, and the plan says why (six arguments, two of them lists; G2+G3 is cheaper). H1–H6 → M5. I1–I8 → M5/M6. J1–J6 already exist; J7–J8 → M3 Task 9. K1–K10 → M2 Tasks 6–7 and M6 Task 13.

**Deliberately out of scope for this plan:** S6 LiveDataHarness, S5's static scan and build-parity mechanisms, S2 mod-internals feed, the two-machine MP acceptance gate (impossible on one machine, spec §0). Each has or gets its own plan.

**Deliberately not built, with reasons stated at the point of decision rather than buried:** `use_ability` (D11), `move` (F3), `teleport` (F7), `close_quest` (G4). Three of the four get a spike that could change the answer; G4 does not, because G2+G3 does the same job with two arguments instead of six.

**Placeholder scan.** No `TBD` / `TODO` / "implement later" / "add error handling" strings. Every code step contains complete, runnable content. The two forward references are called out at the point of use rather than left to surprise the implementer: `MiniJson`'s exact helper surface (Tasks 1 and 7 both say to use whatever it exposes and not to add a second JSON implementation), and `GameOwners` / `ModalWatch` coming from the traversal plan (Task 3 says to port them verbatim first if they are absent). One vestigial method (`UiTree.ResolveElement`) is flagged in the step that introduces it, with the instruction to delete it and keep `SnapshotWithElements` — noted rather than silently shipped.

**Type consistency.** `UiElementInfo` fields `Type/Name/Text/Visible/Enabled/Focused/Depth/Document` are identical in `UiElementInfo.cs`, `UiSelector.cs`, `UiTree.cs`, `UiDriver.cs` and `UiSelectorTests.cs`. `ScreenState`'s seven inputs match `ModalWatch.ReadOracles`'s six out-parameters plus the route. `Staleness.IsFresh(FixtureManifest, string, string, string[], out string[])` matches its five call sites in `FixtureManifestTests` and its description in `fixtures.ps1`. Every console handler takes only `string` / `int` / `bool`, per the marshaller constraint.

**Negative controls, counted.** `UiSelectorTests`: 4 (`#Name` must not match a partial; an unrelated element must not match; `clickableOnly` must exclude a `Label`; a disabled button must be skipped). `ScreenTruthTests`: 3 (route alone is not a wedge; each of the five oracles trips independently; the console overlay is not a wedge). `FixtureManifestTests`: 5 (version mismatch; dataHash mismatch; unresolvable id; all three at once reported together; unparseable and null manifests fail closed) **plus** the positive control that a matching fixture is accepted — because a gate that refuses everything is as useless as one that refuses nothing. Total 13, and one live negative control: the committed `_stale-control` fixture that `ftk2_fixture_verify` must refuse.

**Known risks, in the order I would worry about them.**

1. **SP-1 is load-bearing for the entire plan.** If clicking cannot close the P0-16 modal, M1 does not complete and nothing downstream is reachable. Step 3 lists four distinct failure branches with a different action for each, and the one that matters most — the UI tree being empty — is called out as a stop-and-report, not a workaround.
2. **The staleness gate is the difference between a test suite and a lie.** `.ftk2` is obfuscated, so the manifest is the only checkable metadata that will ever exist. If SP-9 finds no runtime path to a `GameSaveData`, the manifest must omit those fields rather than invent them.
3. **SP-10 could invalidate Tier 1 entirely.** If `saveUser` does not round-trip the current position, base fixtures are not possible and M2 Task 7 stops. There is no fallback: nothing but the game can write a `.ftk2`.
4. **`_areDebugCommandsAllowed` (SP-5) could silently remove half of M5 on some routes**, and the only way to find out is per-route, live.
5. **Three routes have no plan at all.** The `CINEMATIC_*` gap is real, named in the catalogue §3 and §7, and scheduled as SP-2 rather than assumed away — an unattended run can plausibly sit in one forever.
