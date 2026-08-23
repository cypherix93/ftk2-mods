# Load Path: Main Menu → Loaded Save (for a live-game Harmony harness)

**Objective:** find the call path FTK2 uses to go from the main menu into a loaded save, so
FTK2.Crucible (a BepInEx plugin running inside the live game, main-thread pump, Harmony,
`AccessTools` reflection) can trigger "load this specific save" from code.

**Reframe from the prior probe pass (`crucible-save-load-feasibility.md`):** that pass treated
`AdventureDirector._loadSave` being private, and `AdventureDirector.Initialize` needing a live
scene, as blockers to a "headless bypass". They are not blockers here — the harness runs inside
the live game and can call private members via `AccessTools`, and can drive the same
Director/route lifecycle the real UI drives. The open question was purely: **what sequence does
the real UI execute?** This pass resolves most of that sequence.

All findings below are quoted verbatim from TypeProbe `--methods` output, run against
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed`
(5389 types loaded), via
`dotnet run --project C:\Users\ben\repos\ftk2-wt-probe2\FTK2.DevKit\sandbox\TypeProbe -c Release`.
TypeProbe is reflection-only — it prints member signatures, never method bodies — so anything
about *what a method's body actually does* is marked ASSUMED, not CONFIRMED.

---

## 1. Routing API

**`RouterMono.Route`** is the navigation entry point, and it is public:

```
| method | `Void` | `Route` | `(eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData, Boolean pReload, Boolean pForceRoute)` |
```

`RouterMono` is the live singleton MonoBehaviour that owns every scene Director as a private
field, confirming it is the thing that switches between them:

```
| field | `AdventureDirector` | `_adventureDirector` | `` |
| field | `AdventureSelectionDirector` | `_adventureSelectionDirector` | `` |
| field | `MainMenuDirector` | `_mainMenuDirector` | `` |
| field | `eRoutes` | `_currentRoute` | `` |
| prop | `eRoutes` | `CurrentRoute` | `` |
| method | `Boolean` | `IsGameplayRoute` | `(eRoutes route)` |
```

`RouterHelper` (the singleton facade — established in prior probes) exposes `GetCurrentRoute()`
and holds `_router` (a `RouterMono`), so a harness reaching `RouterHelper` can get to
`RouterMono.Route(...)` through that private `_router` field (reflection) or by finding the
live `RouterMono` instance directly in the scene.

`RouterMono` also carries a `UIDocument LoadGameUIDocument` field, distinct from
`AdventureSummaryUIDocument`/`LoadingUIDocument`/etc — confirming a dedicated "load game" UI
surface exists in the route/scene system (its owning Director is `AdventureSelectionDirector`,
see §3).

**routing_api: FOUND** — `RouterMono.Route(eRoutes pTargetRoute, Int32 pRandomSeed, Object
pCustomData, Boolean pReload, Boolean pForceRoute)`, public instance method.

---

## 2. Save enumeration

**`GameSaveDirector` has no members beyond `Object`** — confirmed again in this pass (same
result as the prior probe):

```
## GameSaveDirector

| Kind | Type | Name | Signature |
|---|---|---|---|
| method | `Boolean` | `Equals` | `(Object obj)` |
| method | `Void` | `Finalize` | `()` |
| method | `Int32` | `GetHashCode` | `()` |
| method | `Type` | `GetType` | `()` |
| method | `Object` | `MemberwiseClone` | `()` |
| method | `String` | `ToString` | `()` |
```

No enumeration *method* was found anywhere in the probed types. The save list lives as **data**,
not behind an accessor: `Env.GameRuns` is a `List`1` field directly on the global `Env`
singleton:

```
| field | `List`1` | `GameRuns` | `` |
```

(Generic element type not resolvable via this reflection probe pass, but given `GameSaveData`'s
shape and `AdventureSelectionDirector._loadGameRun(GameSaveData pGameSaveData, ...)` taking that
exact type, `Env.GameRuns` is almost certainly `List<GameSaveDirector.GameSaveData>` — ASSUMED,
not confirmed by TypeProbe since it can't print closed generic args.)

`UserData` (on `Env.User`) also carries `LastGameRunIdPlayed` (String) — a single most-recent-run
id, separate from the full list:

```
| field | `String` | `LastGameRunIdPlayed` | `` |
```

**save_enumeration:** no enumeration method; read the `Env.GameRuns` list field directly
(private-field reflection, fine for a Harmony/AccessTools harness). Each element is presumably a
`GameSaveDirector.GameSaveData` (ASSUMED — element type not printable by this probe).

---

## 3. Who calls `_loadSave` — the load caller

**Found.** `AdventureSelectionDirector` (the Director for `eRoutes.ADVENTURE_SELECTION`) has its
own `_loadSave` overloads plus a public-facing wrapper chain not seen in the prior pass, which
only probed `AdventureDirector`:

```
## AdventureSelectionDirector

| method | `Void` | `Initialize` | `(UIDocument pCanvas2D, UserData pUserData, Camera pDefaultCamera, Boolean pReleaseDisabledInput)` |
| method | `Void` | `_bindNextButtonToLoadSave` | `()` |
| method | `Void` | `_loadGameRun` | `(GameSaveData pGameSaveData, String pDisableLogMessage)` |
| method | `Void` | `_loadGameRun` | `(String pFileName, String pDisableLogMessage)` |
| method | `Task` | `_loadGameRunImpl` | `(GameSaveData pGameSaveData)` |
| method | `Task`1` | `_loadSave` | `(GameSaveData pSaveData, CancellationToken pCancellationToken)` |
| method | `Task`1` | `_loadSave` | `(String pGameRunId, String pFilename, CancellationToken pCancellationToken)` |
| method | `Void` | `_refreshLoadGameBrowser` | `(Boolean pShowList, GameSaveData pGameSaveData)` |
| method | `Void` | `_setNavigationLoadGame` | `()` |
| method | `Void` | `_onlineMultiplayerOnContinueSaveGameAsHost` | `(GameSaveData pGameSaveData)` |
```

(Full dump also shows `_bindNextButtonToLoadSave`'s closures, e.g.
`<_bindNextButtonToLoadSave>b__31_0(NavigationSubmitEvent e)`, confirming this method is wired
directly to a UI button click.)

All of these are private, but that is fine for a Harmony/`AccessTools` harness. The important
structural finding: `_loadGameRun(GameSaveData pGameSaveData, String pDisableLogMessage)` is the
narrowest, most save-oriented method that takes the exact `GameSaveData` type read from
`Env.GameRuns` — a more direct target than calling `_loadSave` itself, since it is presumably the
thin wrapper the UI button invokes (name symmetry with `_bindNextButtonToLoadSave` supports this
— ASSUMED, body not inspected).

`AdventureSelectionDirector.Initialize` is dramatically lighter than
`AdventureDirector.Initialize`:

```
AdventureSelectionDirector.Initialize(UIDocument pCanvas2D, UserData pUserData, Camera pDefaultCamera, Boolean pReleaseDisabledInput)
AdventureDirector.Initialize(UIDocument pCanvas2D, UIDocument pOverheadCanvas2D, Int32 pRandomSeed, GameObject pCanvas3D, AdventureCameraController pCamera, GameObject pOverlayParent, InputController pInputControls)
```

This matches the route model: `ADVENTURE_SELECTION` is a menu-weight route (2D canvas, camera,
user data only), while `ADVENTURE` is the full 3D gameplay route. This is consistent with
`RouterMono.Route(eRoutes, ...)` being the thing that swaps `_currentDirector` and calls each
Director's own lighter/heavier `Initialize` — i.e. a harness does not need to hand-assemble
`AdventureDirector.Initialize`'s heavy argument list itself; that is `RouterMono`'s job when
routing to `eRoutes.ADVENTURE` (ASSUMED wiring, based on `RouterMono` holding both directors as
private fields and having the single `Route` entry point — bodies not inspected).

**Other candidate types probed and confirmed NOT FOUND in the assembly:** `LoadGameOverlay`,
`LoadGameOverlayController`, `LoadGameController`, `LoadGamePanel`, `LoadGameView`,
`LoadGameMenu`, `GameSaveOverlay`, `GameSaveOverlayController`, `GameSaveListController`,
`GameSavePanel`, `GameSaveView`, `SaveSlotController`, `SaveSlotView`, `SaveSlot`,
`ContinueGameController`, `AdventureSelectionController`.

`MainMenuDirector` (fully probed) has **no** load/continue method of its own — its only
save-adjacent surface is a button-callback closure taking a `GameSaveData pDeletedGame` param
(a delete-confirmation callback, not a load call):

```
| method | `Void` | `<_onClickMultiplayer>b__42_3` | `(GameSaveData pDeletedGame)` |
```

`MainMenuDirector._openAdventure(Env pEnv, String pAdventureName)` opens a *new* adventure by
name — not a saved-run loader.

**load_save_caller: FOUND** — `AdventureSelectionDirector._loadGameRun(GameSaveData
pGameSaveData, String pDisableLogMessage)` (private), backed by
`AdventureSelectionDirector._loadGameRunImpl(GameSaveData pGameSaveData)` and
`AdventureSelectionDirector._loadSave(...)` (same two overloads as `AdventureDirector`, private).
The route that owns this Director is `eRoutes.ADVENTURE_SELECTION`.

---

## 4. Continue-last-run path

No `Continue`/`Resume`-named public method was found. The closest things:

- `UserData.LastGameRunIdPlayed` (String field) — the most-recently-played run id, readable
  directly.
- `AdventureSelectionDirector._onlineMultiplayerOnContinueSaveGameAsHost(GameSaveData
  pGameSaveData)` — multiplayer-host-specific, not a single-player "continue" shortcut.
- `AdventureDirector` field `_continueTurnOnInitiaze` (Boolean) and method
  `_continueTurn(Boolean pIsFirstTurn, GameRandom pGameRandom)` — these are turn-resumption
  inside an already-loaded adventure, not a save-loading shortcut.

**continue_path: does not exist as a distinct API.** "Continue" is achieved the same way as
loading any other save: read `UserData.LastGameRunIdPlayed`, find the matching entry in
`Env.GameRuns`, and drive it through the same `_loadGameRun` path as §3. No dedicated
one-call shortcut was found.

---

## 5. Most plausible concrete call sequence

Given a running game with the harness attached at the main menu:

1. **Resolve the live `RouterMono` instance** (e.g. via `UnityEngine.Object.FindObjectOfType`
   or by reading `RouterHelper`'s private `_router` field). — ASSUMED (mechanism for finding the
   singleton instance; `RouterMono`'s existence and its being the route owner is CONFIRMED).
2. **Read `Env.GameRuns`** (private field on the live `Env` instance) to enumerate saves, and
   pick the target entry by matching `GameSaveData.runID` or `.saveName`. — CONFIRMED field
   exists (`Env.GameRuns`); CONFIRMED `GameSaveData` shape (`runID`, `saveName`, etc., from
   prior probe pass). The list's *element type* being `GameSaveData` is ASSUMED (generic arg not
   printable by TypeProbe).
3. **Call `RouterMono.Route(eRoutes.ADVENTURE_SELECTION, pRandomSeed, pCustomData, pReload,
   pForceRoute)`** to route into the Director that owns the load-game UI. — CONFIRMED method
   signature exists; ASSUMED that this is the correct route to reach `AdventureSelectionDirector`
   (route↔Director mapping inferred from `RouterMono` holding `_adventureSelectionDirector` as a
   field and `eRoutes.ADVENTURE_SELECTION` being a known enum value — bodies not inspected to
   confirm the dispatch table).
4. **Wait for `AdventureSelectionDirector.Initialize(UIDocument pCanvas2D, UserData pUserData,
   Camera pDefaultCamera, Boolean pReleaseDisabledInput)` to complete** (or poll until the
   Director instance is non-null / initialized), since `Route` presumably triggers this
   asynchronously. — CONFIRMED signature; ASSUMED that `Route` is what invokes it.
5. **Call `AdventureSelectionDirector._loadGameRun(GameSaveData pGameSaveData, String
   pDisableLogMessage)` reflectively** (`AccessTools.Method` + `Invoke` on the live Director
   instance from step 3/4), passing the selected save entry from step 2. — CONFIRMED method
   signature; ASSUMED this is the correct/sufficient entry point rather than needing to call
   `_loadSave` or `_loadGameRunImpl` directly (name/wiring inference from
   `_bindNextButtonToLoadSave`, not from an inspected method body).
6. **Let the game's own async chain carry the route to `eRoutes.ADVENTURE`** (gameplay), driven
   by whatever `_loadGameRunImpl`/`_tryFinishLoadingAndInit` do internally — no explicit second
   `Route(...)` call is expected to be necessary from the harness. — ASSUMED (based on
   `_tryFinishLoadingAndInit(Action pOnQuit, Boolean pSkipTransition)` and
   `_showFinishLoadingConfimPrompt(Action pOnQuit)` existing on both `AdventureSelectionDirector`
   and `AdventureDirector`, suggesting a load-finish handoff between the two Directors that
   TypeProbe cannot confirm the trigger for).

Steps 3–6 have not been exercised against a live game in this pass — they are TypeProbe-level
static evidence only, not runtime-verified.

---

## Verdict

**NEEDS-LIVE-SPIKE.**

This pass materially improves on the prior probe: it identifies a public, non-Director-specific
routing entry point (`RouterMono.Route(eRoutes, ...)`), and — critically — finds the actual
*caller* of the save-loading machinery (`AdventureSelectionDirector._loadGameRun` /
`_loadGameRunImpl` / `_loadSave`), which the prior pass explicitly flagged as unresolved. The
"private methods are a blocker" and "`AdventureDirector.Initialize` requires a full scene"
objections from the prior pass are moot for a live in-process Harmony harness — private access
is not an obstacle, and `AdventureSelectionDirector.Initialize` is lighter-weight than
`AdventureDirector.Initialize` in any case.

What remains unresolved and requires a live-game spike rather than more reflection:
- Whether `RouterMono.Route(eRoutes.ADVENTURE_SELECTION, ...)` is in fact the right route to
  reach a state where `AdventureSelectionDirector._loadGameRun` can be called (method bodies are
  invisible to TypeProbe).
- Whether `_loadGameRun` alone is sufficient, or whether the harness must additionally drive
  `_loadSave`/`_loadGameRunImpl`/`_tryFinishLoadingAndInit` itself.
- Timing/async sequencing — when the Director instance and its `UIDocument`/`Camera` dependencies
  are actually available for the harness to act on after issuing a `Route(...)` call.
- Whether `Env.GameRuns`'s element type is really `GameSaveDirector.GameSaveData` (near-certain
  but not reflection-confirmed).

None of these are blockers in the sense the prior pass found (private access, heavy `Initialize`
signature) — they are just facts that only show up by running the sequence against the live game
with logging/breakpoints, which TypeProbe (static reflection only) cannot supply.
