# FTK2.Crucible — SPEC

Plugin GUID: `ftk2mods.crucible` · Content id prefix: `CRU_` · Priority: **P0-dev**

Testing + automation harness. Exposes the game to an external agent (Claude via MCP) the way Playwright
exposes a browser: drive it, read its state, screenshot it, assert on it.

---

## 0. Ground truth (verified 2026-08-15, decompile of retail `FTK2.dll`)

Crucible is a **bridge, not a reimplementation**. The game already ships IronOak's dev-command system, and
it is live in the retail build. Everything below was read out of the decompiled retail assembly, not assumed.

| Verified fact | Where |
|---|---|
| `CommandLineHelper` is `public static` with `ExecuteCommand(string pName, string[] pArgs, bool pPrintSuccess = false, bool pPrintResult = false)`, `RegisterCommand(...)` (3 overloads), `UnregisterCommand`, `TryGetCommand`, `GetCommands()` | `CommandLineHelper` |
| Arg marshalling is built in: `int`, `float`, `double`, `bool`, `string`, `Vector2`, `Vector3` | `CommandLineHelper.ExecuteCommand` |
| Command registration is **not** gated behind `Debug.isDebugBuild` — it runs on normal init paths | `RouterMono`, `CombatPhase`, `MainMenuDirector`, … |
| The console UI **is initialized in the retail build**: `CommandLineHelper.Initialize()` then `CommandLineViewHelper.Initialize(CommandLineUIDocument.rootVisualElement)` | `RouterMono.cs:256-257` |
| `CommandLineViewHelper.ToggleShow()` / `IsShowing()` are `public static` | `CommandLineViewHelper` |
| Only the *keybind* was stripped — `CommandLineHelper._onToggleConsoleInput` has an empty body | `CommandLineHelper` |
| Multiplayer transport is **Photon** (`Photon3Unity3D.dll`, `PhotonRealtime.dll`, `websocket-sharp.dll`), joined by **room id** — not Steam P2P | `…_Data/Managed/` |
| Game install path is `C:\Program Files (x86)\Steam\steamapps\common\For The King II` (README's `E:\…` is stale) | Steam `libraryfolders.vdf`, app `1676840` |

### Shipped commands Crucible builds on

| Command | Args | Use |
|---|---|---|
| `EndPhase` | — | **Next/skip turn.** Registered by `CombatPhase`, `EncounterPhase`, `FortunePhase`, `RestPhase` |
| `SetStat` / `SetStatLocal` / `RemoveStat` / `ClearStat` | stat, value | State mutation |
| `SetPlayerHealth` | playerIndex, hp | State mutation |
| `SetPlayersTo9999HP` | — | Survive-the-scenario helper |
| `GetSpecificThing` | configId, qty | Give item |
| `EquipSpecificThing` | configId | Equip item |
| `SetTarot` / `SetSlotRollResult` | — | **RNG determinism** — makes scenarios repeatable |
| `ReceiveGameAction` | json | Inject an inbound network action |
| `ToggleUI` / `ToggleVenueGrid` / `TogglePlayerHuds` | — | Screenshot hygiene |
| `MultiplayerDemoQuickCombat` | totalPlayers, controlledPlayers, roomId | **One instance drives multiple player slots** |
| `MultiplayerDemoQuickDungeon` / `…QuickAdventure` | + config name | Same, other modes |
| `JoinOnlineRoom` | — | Join by room |
| `EnableGameRandomStacktraceRecording` | bool | Records RNG-call stacktraces "for multiplayer debugging" |
| `SendMultiplayerDesyncNotification` | — | Manual trigger; the game already auto-detects desync |
| `PrintDungeonConfigHash` / `PrintDialogueConfigHash` | — | Config-divergence check |

**Unverified (do not build on until tested):** the `MultiplayerDemoQuick*` bodies decompile as
`throw new NotImplementedException()`. This is most likely an ILSpy 8.2 async-lambda artifact, not stripped
code — but it is the single load-bearing assumption of §9/M4 and is explicitly deferred to an M3 experiment,
which M3's own tooling makes a one-call test.

### Useful launch args

`-SKIPSPLASH` (faster cycles), `-WINDOWED`, `-GRAPHICS_LOW`, `-THREADING_LOW`, `-OFFLINE`, `-REGION_DEFAULT`
— verified in `AppConfigManager.SetConfigFromLaunchParameters`.

---

## 1. Purpose & scope

Crucible makes the FTK2 mod stack testable two ways: **headfully**, by an agent driving the real running
game, and **headlessly**, by a scenario runner over the `*.Core` assemblies. It exposes one command
vocabulary — the game's own — through three front-ends: an in-game console/hotkeys (for Ben), a loopback
RPC (for machines), and an MCP server (for Claude).

**Deliberately out of scope:** no gameplay changes, no content, no balance. Crucible never ships enabled by
default in a distributed package. No outbound network beyond loopback (it does not phone home). It does not
replace `FTK2.DevKit` — it consumes `DevKitLog`/`PatchRegistry`/`ParityService` through the same reflection
soft-dependency convention every sibling uses, and does not take a compile-time reference.

**Headless honesty.** FTK2's combat sim is Unity-coupled (`RouterMono`, `MonoBehaviour`, UniTask
coroutines). Crucible does **not** attempt a headless combat sim. Headless covers what already builds as
`netstandard2.0` — parity, codec, loot-grant, scoring logic — plus simulated peers over a fake transport.
Anything touching a real encounter is headful. This boundary is a design decision, not a limitation to fix
later.

## 2. Player-facing behavior

"Player" = Ben running a dev build, or Claude driving over MCP.

- Press the console hotkey (default `F1`) → IronOak's own console overlay appears, with its built-in
  argument autocomplete. Type `EndPhase`, `SetStat`, `crucible_*`, anything in the registry.
- Press `F2` → screenshot to the artifact folder; a toast names the file.
- With `[Rpc] Enabled = true`, an HTTP server on `127.0.0.1:<port>` accepts command execution, state
  snapshots, and screenshot requests.
- From Claude: `ftk2_exec`, `ftk2_end_phase`, `ftk2_screenshot`, `ftk2_snapshot`, `ftk2_list_commands`,
  `ftk2_read_trace`, `ftk2_run_scenario` — the agent drives the game and sees it.
- Every command executed, every phase transition, and every desync notification lands in a JSONL session
  trace.
- In multiplayer, mutating commands are refused by default (§9).

## 3. Architecture

**Engine vs data split.** Mechanics (dispatch, RPC, capture, trace) are C#. Data surface: `data/
Scenarios/*.json` (scenario scripts) and `data/Redactions.json` (snapshot field policy). No gameplay content.

**Four assemblies:**

```
FTK2.Crucible/
  src/Crucible.Core/          netstandard2.0 — no Unity, no game refs. Unit-testable.
      CommandRequest.cs       parse/validate a command + args
      ScenarioModel.cs        scenario + step + assertion model
      ScenarioRunner.cs       executes steps against an ICommandSink
      TraceWriter.cs          JSONL trace, correlation ids
      StateDigest.cs          deterministic hash of a state snapshot
  src/Crucible.Core.Tests/    net10.0 — mirrors DevKit.Core.Tests harness style
  src/Crucible.Plugin/        net472 — BepInEx plugin
      CruciblePlugin.cs       Awake, knobs, Harmony
      GameBridge.cs           reflection wrapper over CommandLineHelper/CommandLineViewHelper
      MainThreadPump.cs       queue drained on RouterMono.Update
      RpcServer.cs            HttpListener on loopback
      CaptureService.cs       screenshots
      StateReader.cs          snapshot of run/combat state
  mcp/                        Node MCP server (stdio) → RPC client
```

**Runtime flow**
1. `Awake()` — read knobs, install Harmony patches, register `crucible_*` commands into
   `CommandLineHelper`, register with `DevKitLog`/`PatchRegistry` if DevKit is present.
2. `RouterMono.Update` postfix — poll hotkeys; drain the main-thread queue (one work item per frame by
   default, `[Rpc] MaxWorkPerFrame`).
3. RPC request arrives on an `HttpListener` thread → enqueued with a completion source → drained on the
   main thread → result marshalled back. **No Unity API is ever touched off the main thread.**
4. Every execution writes a trace entry with a correlation id supplied by the caller.

**GameBridge (the whole trick).** All game access goes through reflection resolved once at `Awake`, in the
`AccessTools` + "Target found: X" style `docs/CONVENTIONS.md` mandates, so a game update degrades to a
diagnosable log line rather than a crash:

- `CommandLineHelper.ExecuteCommand(string, string[], bool, bool)` → `Exec`
- `CommandLineHelper.GetCommands()` → `ListCommands`
- `CommandLineHelper.TryGetCommand(...)` → arity/tooltip lookup for validation before dispatch
- `CommandLineHelper.RegisterCommand(string, MethodInfo, List<string>, object)` → registering our own verbs
- `CommandLineViewHelper.ToggleShow()` / `IsShowing()` → console revival

Because `ExecuteCommand` silently returns on an unknown name, `GameBridge.Exec` calls `TryGetCommand`
first and reports `unknown_command` rather than a false success. This matters: the RPC's honesty depends
on it.

**Console revival.** No patch needed — `RouterMono` already initialized the view. The hotkey calls
`CommandLineViewHelper.ToggleShow()`. If `IsShowing()` is unavailable (game update), the hotkey logs and
no-ops rather than throwing.

**Screenshots.** `ScreenCapture.CaptureScreenshot(path)` writes at end of frame; Crucible waits one frame,
polls for the file, and returns the path plus bytes. The MCP layer returns it inline as an image so Claude
can look at the game. `[Capture] HideUiForShots` optionally runs `ToggleUI` around the capture.

**State snapshot.** `StateReader` walks `RouterHelper.Env` (`GameRun`, `NetworkData`, combat state) by
reflection into a plain dictionary. It is best-effort and **explicitly versioned** (`schema` field): a null
or renamed field yields a `null` entry plus a note, never an exception. `StateDigest` hashes the snapshot
with `ParityService.ComputeDataHash`'s discipline — sorted keys, ordinal comparison, invariant culture,
floats quantized to a fixed decimal count — so two peers' digests are comparable.

**Failure posture.** Everything is behind a master `Enabled` knob defaulting to **false**. RPC binds
loopback only. If any bridge target fails to resolve, that feature disables itself and logs; the game runs
untouched.

## 4. Data file formats

### `data/Scenarios/<name>.json`
```jsonc
{
  "Id": "CRU_SCN_BASIC_COMBAT",
  "Description": "Start a fight, end 3 phases, assert party alive",
  "Setup": [
    { "Cmd": "EnableGameRandomStacktraceRecording", "Args": ["true"] },
    { "Cmd": "SetPlayersTo9999HP", "Args": [] }
  ],
  "Steps": [
    { "Cmd": "EndPhase", "Args": [], "Repeat": 3, "WaitFrames": 30, "Screenshot": true }
  ],
  "Assertions": [
    { "Path": "combat.partyAlive", "Op": "eq", "Value": true },
    { "Path": "combat.round",      "Op": "gte", "Value": 3 }
  ]
}
```
`Op`: `eq` | `neq` | `gt` | `gte` | `lt` | `lte` | `contains` | `exists`. `Path` is a dotted path into the
snapshot. An assertion against a missing path **fails** (it does not pass vacuously).

### `data/Redactions.json`
Field-path globs excluded from snapshots/digests (volatile things — frame counters, timestamps, object ids)
so digests are stable across peers.

## 5. Knobs

- `[General] Enabled` (bool, **false**) — master switch.
- `[General] VerboseLogging` (bool, false)
- `[Console] Enabled` (bool, true) — revive IronOak's console
- `[Console] ToggleKey` (KeyboardShortcut, `F1`)
- `[Capture] ScreenshotKey` (KeyboardShortcut, `F2`)
- `[Capture] ArtifactFolder` (string, `BepInEx/crucible-artifacts`)
- `[Capture] HideUiForShots` (bool, false)
- `[Capture] SuperSize` (int, 1)
- `[Rpc] Enabled` (bool, **false**)
- `[Rpc] Port` (int, 8787)
- `[Rpc] Token` (string, "") — if set, required as `X-Crucible-Token`
- `[Rpc] MaxWorkPerFrame` (int, 4)
- `[Trace] Enabled` (bool, true)
- `[Trace] Folder` (string, `BepInEx/crucible-artifacts/traces`)
- `[Safety] AllowMutationsInMP` (bool, false)
- `[Safety] AllowedCommands` (string, "") — empty = all; else comma-separated allowlist

## 6. Patch targets & integration points

| Target | Patch | Why |
|---|---|---|
| `RouterMono.Update()` | Postfix | Hotkey polling + main-thread pump. Single per-frame entry point. |
| `CombatPhase._addEntityToCombat` | Postfix | Set `InCombat` (mutation gating, trace context) |
| `CombatPhase._endCombatAsync` | Postfix | Clear `InCombat`, flush trace |

Everything else is **reflection calls, not patches** — Crucible calls the game's public statics directly.
That is deliberate: fewer patches, less breakage surface across game updates.

Soft dependencies (reflection, no-op if absent): `FTK2Mods.DevKit.DevKitLog`,
`FTK2Mods.DevKit.PatchRegistry`, `FTK2Mods.DevKit.ParityService`.

## 7. Example starting dataset

- `data/Scenarios/CRU_SCN_SMOKE.json` — console + exec + screenshot, no combat. Proves the bridge.
- `data/Scenarios/CRU_SCN_BASIC_COMBAT.json` — the §4 example.
- `data/Redactions.json` — starter volatile-field list.

## 8. Testing plan

Offline (`Crucible.Core.Tests`, runs in CI, no game):
1. Command parse/validate: arity, type coercion, unknown command, allowlist enforcement.
2. `ScenarioRunner` against a fake `ICommandSink`: setup/steps/repeat ordering, assertion pass+fail,
   missing-path assertion **fails**, first-failure short-circuit.
3. `TraceWriter`: valid JSONL, correlation ids preserved, rotation.
4. `StateDigest`: identical snapshots → identical digest; key order and float noise below the quantum do
   not change it; a redacted field does not affect it.

In-game (headful, < 15 min):
5. `F1` opens IronOak's console; `EndPhase` typed by hand advances the phase.
6. `F2` writes a PNG.
7. With RPC on: `curl` → `/exec` runs `EndPhase`; `/screenshot` returns a PNG; `/state` returns a snapshot;
   `/commands` lists the registry. Unknown command returns `unknown_command`, not a false success.
8. Claude drives it over MCP: screenshot → read state → `EndPhase` → screenshot, and reports what changed.
9. Kill the game mid-RPC — the MCP server reports a clean connection error, no hang.
10. Trace file for the session is valid JSONL and contains every command with correlation ids.

Deferred to M4: the `MultiplayerDemoQuickCombat` experiment and two-instance desync runs.

## 9. Save & multiplayer considerations

- **Parity class: `LOCAL`.** Crucible has no save footprint and syncs nothing. It must never appear in a
  `ParityService` gameplay registration as content.
- **Dev-mutation lockout (R5).** When `NetworkData` reports an online session, every command not on a
  read-only list (`ToggleUI`, `ToggleVenueGrid`, `TogglePlayerHuds`, `Print*Hash`, `GetCommands`) is
  refused unless `[Safety] AllowMutationsInMP = true`, which logs a loud banner. `EndPhase` counts as a
  mutation.
- **SafeMode:** RPC stops accepting mutating requests; console and read-only endpoints stay up.
- **Photon, not Steam.** Peers join by room id, so multi-instance testing does not need multiple Steam
  accounts. Whether two *processes* can run concurrently is an M4 experiment.
- **Desync oracle (M4):** per-turn `StateDigest` from each peer compared at phase boundaries; the harness
  reports the first divergent turn and the diverging fields, corroborated by the game's own
  `SendMultiplayerDesyncNotification` and `EnableGameRandomStacktraceRecording`.

## 10. Milestones

- **M1 — Bridge + console.** `Crucible.Core` (parse/trace/digest) + tests; plugin with knobs, `GameBridge`,
  main-thread pump, console revival on `F1`, screenshot on `F2`. Ben gets the fast manual loop.
- **M2 — RPC + state.** `HttpListener` on loopback: `/health`, `/commands`, `/exec`, `/state`,
  `/screenshot`, `/trace`. Safety gating + trace wired through.
- **M3 — MCP.** Node stdio MCP server exposing the tools in §2, screenshots returned inline as images.
  Scenario runner + the two shipped scenarios. **Claude can now drive the game.** Ends by using that
  tooling to run the `MultiplayerDemoQuickCombat` experiment and resolve §0's open question.
- **M4 — MP harness.** Two-instance launcher, simulated peers over the fake transport, per-turn digest
  comparison, desync oracle. Scope confirmed by M3's experiment.

Each milestone is independently useful; M1 alone already beats the current workflow.

## 11. Open questions

1. Do the `MultiplayerDemoQuick*` commands actually work, or were the bodies stripped? (M3 experiment.)
2. Can two FTK2 processes run concurrently on one machine (Steam/game single-instance enforcement)?
3. Does `RouterMono.Update` run on every route, or are there scenes where the pump stalls? If so, a second
   pump host is needed.
4. Is `ScreenCapture.CaptureScreenshot` reliable with this render pipeline, or is a
   `RenderTexture`/`AsyncGPUReadback` path needed for reliable off-screen capture?
5. Should the MCP server launch the game itself (Playwright-style `browser.launch()`) or only attach to a
   running instance? M3 assumes attach-only; launching is an M4 concern tied to the two-instance question.
6. Does `ReceiveGameAction` accept arbitrary JSON safely enough to be a fault-injection tool, or does it
   need an allowlist of action types?
