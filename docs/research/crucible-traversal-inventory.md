# Crucible Traversal Inventory — every `eRoutes` state, how to reach it, how to leave it

**Produced 2026-08-23** by running
`dotnet run --project C:\Users\ben\repos\ftk2-wt-probe2\FTK2.DevKit\sandbox\TypeProbe -c Release -- --methods <Type>`
against the retail assembly (`5389 types loaded from C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed`),
cross-read with `docs/research/crucible-load-path.md`, `crucible-quest-progression.md`,
`crucible-verb-feasibility.md`, `crucible-combat-field-map.md`, and the live
`BepInEx/LogOutput.log` from Ben's 2026-08-23 session.

**Every member name below is quoted verbatim from TypeProbe output or from one of those docs.**
Where behaviour is inferred from a name or from wiring rather than from a signature, it is marked
**ASSUMED** — TypeProbe prints signatures, never method bodies.

**Purpose.** The harness can execute the game's own console commands but none of the 21 shipped ones
change screens, so it cannot choose a campaign, let alone finish one. This document answers, for
every `eRoutes` value: *how do you get there, what can you do there, what proves you arrived, and
what can wedge you.* The plan built on it is
`docs/superpowers/plans/2026-08-23-s3-traversal-verbs.md`.

---

## 0. Corrections to the record

Two things in earlier docs are now known to be wrong or stale:

| Earlier claim | Correction (this pass) |
|---|---|
| `crucible-quest-progression.md` §5: "`RestPhase._debugEndPhase()` → `Void`, plus an overload `RestPhase._debugEndPhase(Int32 pOption)`" | **`RestPhase` has exactly ONE `_debugEndPhase`, and it takes an `Int32`.** Verbatim: `| method | Void | _debugEndPhase | (Int32 pOption) |`. There is no zero-arg overload. `CombatPhase`, `EncounterPhase`, `FortunePhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase` are the zero-arg ones. A dispatcher assuming a uniform zero-arg `_debugEndPhase` fails on REST. |
| `crucible-verb-feasibility.md`: "`CombatHelper.TryEndGame` … whether it takes a 'did the player win' bool … is unverified" | **Resolved.** Verbatim: `| method | Boolean | TryEndGame | (CombatState pCombatState) |`. It takes only the combat state — there is **no** win/lose flag. `force_win`/`force_lose` cannot be built from `TryEndGame` alone; they need `CombatState.EndCombatEarly` / `AlternateWinConditions` (combat field map) or `<director>._endAdventure(Boolean pIsVictory)`. |

Also worth stating plainly, from the live log: **the reflective escape hatch already works.**
`BepInEx/LogOutput.log` shows `Registered command: crucible_get` after nine
`ListCommands failed: Exception has been thrown by the target of an invocation` retries — i.e.
`CommandLineHelper.GetCommands()` throws until `RouterMono` has run `CommandLineHelper.Initialize`,
and the retry loop in `ReflectionCommands.TryRegister` rides it out. So `crucible_invoke <Type>
<Method> <args>` is live today, and `FTK2.Crucible/src/Crucible.Core/ArgCoercion.cs` is currently
being extended with a `System.Object` case whose own comment names `RouterMono.Route`'s
`pCustomData` as the motivating call. Navigation is therefore not blocked on *capability* — it is
blocked on there being no ergonomic, gated, post-condition-checked verb for it, and on nobody having
mapped which transitions are legal.

---

## 1. The route ↔ director map (CONFIRMED)

`RouterMono` holds every scene owner as a private field. This is the authoritative mapping, read
directly off the field dump rather than inferred from names:

```
| field | AdventureDirector           | _adventureDirector           |
| field | AdventureSelectionDirector  | _adventureSelectionDirector  |
| field | CombatPhase                 | _combatPhase                 |
| field | DungeonDirector             | _dungeonDirector             |
| field | EncounterPhase              | _encounterPhase              |
| field | FortunePhase                | _fortunePhase                |
| field | IntroPhase                  | _introPhase                  |
| field | LoreStoreDirector           | _loreStoreDirector           |
| field | MainMenuDirector            | _mainMenuDirector            |
| field | MultiplayerLobbyDirector    | _multiplayerLobbyDirector    |
| field | PartyManagementDirector     | _partyManagementDirector     |
| field | RestPhase                   | _restPhase                   |
| field | TrapPhase                   | _trapPhase                   |
| field | TreasurePhase               | _treasurePhase               |
| field | VenueDirector               | _venueDirector               |
| field | WheelPhase                  | _wheelPhase                  |
| field | IContainer                  | _currentDirector             |
| field | eRoutes                     | _currentRoute                |
| prop  | eRoutes                     | CurrentRoute                 |
```

**There is no director field for any `CINEMATIC_*` route.** `CinematicDirector`, `CinematicPhase`,
`CinematicHelper` and `VideoDirector` were probed by exact name and are all **NOT FOUND** among the
5389 loaded types. Whatever owns the three cinematic routes is not a Director in this family. This
is the largest hole in the inventory (§6).

The navigation primitive, public, verbatim:

```
| method | Void | Route | (eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData, Boolean pReload, Boolean pForceRoute) |
```

Reached from the harness via `RouterHelper._router` (`| field | RouterMono | _router |`) or
`UnityEngine.Object.FindObjectOfType(RouterMono)`. `ReflectionCommands.TryResolveInstance` already
implements both, plus the RouterMono-private-field strategy that resolves every Director listed
above — so instance resolution for all sixteen owners is solved code that exists today.

Four route-choosing helpers exist and should be preferred over a hand-written route graph:

```
RouterHelper | method | eRoutes       | GetNextRoute           | (GameRunData pGameRun) |
RouterHelper | method | eRoutes       | GetEndOfAdventureRoute | (GameRunData pGameRun) |
RouterHelper | field  | Dictionary`2  | PhaseToRoute           |                        |
RouterHelper | method | Boolean       | IsInVenue              | ()                     |
RouterMono   | method | Boolean       | IsGameplayRoute        | (eRoutes route)        |
```

`RouterHelper.GetNextRoute(GameRunData)` is the game's own answer to "where should this run go
next", which makes it the correct oracle for an autopilot. (ASSUMED side-effect-free — signature
only.)

---

## 2. The uniform per-route control surface (CONFIRMED)

Nine of the phase/venue types share an identical shape. This is what makes one *generic* dispatcher
possible instead of nine bespoke verbs.

| Member | Present on |
|---|---|
| `Void _goToNextRoute(eRoutes pNextRoute)` | `CombatPhase` (param named `pRoute`), `EncounterPhase`, `RestPhase`, `FortunePhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase`, `IntroPhase`, `VenueDirector` |
| `_debugEndPhase` | `CombatPhase` `()`→`Void` · `EncounterPhase` `()`→`Task` · `FortunePhase` `()`→`Void` · `TreasurePhase` `()`→`Void` · `TrapPhase` `()`→`Void` · `WheelPhase` `()`→`Void` · **`RestPhase` `(Int32 pOption)`→`Void`** |
| `Boolean _areDebugCommandsAllowed` (field) | `CombatPhase`, `EncounterPhase`, `RestPhase`, `FortunePhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase` |
| `Void _initDebug()` / `Void _debugUpdate()` | `CombatPhase`, `RestPhase`, `FortunePhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase` (`EncounterPhase`: `_initDebug` only) |
| `Task _registerConsoleCommands()` | `CombatPhase`, `RestPhase`, `FortunePhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase` |
| `Task`1 _endAdventure(Boolean pIsVictory)` | `AdventureDirector`, `AdventureSelectionDirector`, `PartyManagementDirector`, `VenueDirector`, `DungeonDirector`, `CombatPhase`, `EncounterPhase`, `RestPhase`, `FortunePhase` |
| `Task QuitToMenu()` — **public** | every Director and Phase probed |
| `Boolean _tryEndAdventureFromDungeon()` | `CombatPhase`, `EncounterPhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase`, `IntroPhase`, `VenueDirector` |
| `Void _unregisterCommonConsoleCommands()` | every Director and Phase probed |

`_areDebugCommandsAllowed` is a plain `Boolean` field on each phase and is ASSUMED to be what
`_registerConsoleCommands` gates on. That explains the spec's measured "2 commands at `route=NONE`,
21 at `MAIN_MENU`": **the command registry's contents are a function of the current route**, because
each phase registers its own on entry and calls `_unregisterCommonConsoleCommands()` on exit. An
autopilot must re-read `/commands` after every route change and must never cache the vocabulary.

`AppConfigManager+AppConfig` carries `| field | Boolean | BlockNetworkUnsafeCheats |` — ASSUMED to be
the global gate behind `_areDebugCommandsAllowed`. No `appconfig.json` exists in the install
(`AppConfigManager.FILE_NAME` and `GetConfigPath()` exist; the file does not), so defaults apply.

---

## 3. Route-by-route inventory

Legend — **REACHABLE**: a named, confirmed call gets you there. **REACHABLE-IN-FLOW**: the game
routes there itself during a run and the harness can act once there, but a cold `Route(<x>, …)` from
an arbitrary state is not supported by any evidence. **UNCLEAR**: no confirmed path found.

### `NONE` — pre-boot, not a destination
What `RouterHelper.GetCurrentRoute()` returns before `RouterMono.StartInternalAsync` finishes. The
spec records a live measurement: with Steam closed the process runs, `RouterMono.Update` ticks, and
`route=NONE` persists forever behind a black window.
**Arrival proof:** `route != "NONE"`.
**Stuck risk:** indistinguishable from a harness bug. Assert Steam is up before blaming Crucible.

### `INTRO` — REACHABLE (ASSUMED)
Owner `IntroPhase`, `Void Initialize(Int32 pRandomSeed, UIDocument pCanvas2D, GameObject pCanvas3D)`
— the lightest `Initialize` in the game, so `Route(INTRO, seed, null, false, true)` plausibly stands
alone (ASSUMED; never exercised).
**Act:** `Void _goToNextRoute(eRoutes pNextRoute)`, `Task _proceedVisualStack()`.
**Proof:** `RouterHelper.GetCurrentRoute() == INTRO`.
**Stuck risk:** `Queue`1 _visualStack` plus `Void _playVisualNode(VisualNode pVisualNode, Action`1 pCallback)`
— a visual node whose callback never fires leaves the phase waiting. Escape is `_goToNextRoute`.

### `EXIT` — REACHABLE, and **never to be issued unattended**
`RouterHelper.Quit()`, `IEnumerator RouterHelper.QuitSafely()`, `RouterMono.OnApplicationQuit()`.
`Route(EXIT, …)` terminates the process.
**This must be excluded from any generic route verb by an explicit deny-list**, or one mistyped
argument ends an overnight run.

### `MAIN_MENU` — REACHABLE
Owner `MainMenuDirector`, `Void Initialize(UIDocument pCanvas2D, Camera pCameraDefault, GameObject pLight)`.
Two ways in:
- `RouterMono.Route(MAIN_MENU, 0, null, false, true)` — signature CONFIRMED, sufficiency ASSUMED.
- `<any director>.QuitToMenu()` → `Task`, **public**, present on every Director and Phase. The
  reliable universal "get me out of here", and the harness's panic button.

**Act:**
```
| method | Void    | _openAdventure       | (Env pEnv, String pAdventureName) |
| method | Boolean | TryAutoJoinRoom      | () |
| method | Void    | BindMultiplayerButton| () |
| method | Void    | _onClickMultiplayer  | (NavigationSubmitEvent pEvent) |
| method | Void    | _showExitGameDialog  | () |
| method | Void    | _onRerouteFromNews   | (eRoutes pRoute) |
| method | Void    | _createKrakenDebug   | (Int32 pBoatType) |
| method | Void    | _createBossCombat    | (dBossFightSchedule pSchedule) |
| method | Task    | QuitToMenu           | () |
```
`_openAdventure(Env, String pAdventureName)` opens a *new* adventure by name — it is not a save
loader (`crucible-load-path.md` §3 established this).

**Proof:** `route == MAIN_MENU` **and** the `/commands` count rises to ~21 (measured in the spec).
**Stuck risks:** `_showExitGameDialog()` raises a `SystemDialogViewHelper` modal;
`PromptViewHelper.ShowIntroPrompt(Env pEnv, Action pAction)` / `ShowDemoCompletionPrompt(Action)` /
`ShowExperimentalPrompt(Action)` are first-run prompts (fields `_isFirstStartup`,
`_enableExperimentalPrompts`) that will block a fresh profile; `TryAutoJoinRoom()` — see §4.

### `ADVENTURE_SELECTION` — REACHABLE
Owner `AdventureSelectionDirector`,
`Void Initialize(UIDocument pCanvas2D, UserData pUserData, Camera pDefaultCamera, Boolean pReleaseDisabledInput)`.
`Route(ADVENTURE_SELECTION, …)` per `crucible-load-path.md` §5 step 3.

**Act — load an existing run:**
```
| method | Void   | _loadGameRun            | (GameSaveData pGameSaveData, String pDisableLogMessage) |
| method | Void   | _loadGameRun            | (String pFileName, String pDisableLogMessage) |
| method | Task   | _loadGameRunImpl        | (GameSaveData pGameSaveData) |
| method | Task`1 | _loadSave               | (GameSaveData pSaveData, CancellationToken pCancellationToken) |
| method | Task`1 | _loadSave               | (String pGameRunId, String pFilename, CancellationToken pCancellationToken) |
| method | Void   | _refreshLoadGameBrowser | (Boolean pShowList, GameSaveData pGameSaveData) |
| method | Void   | _setNavigationLoadGame  | () |
| method | Void   | _bindNextButtonToLoadSave| () |
```
**The `_loadGameRun(String pFileName, String pDisableLogMessage)` overload takes two strings** — it
is the one overload directly marshallable from a console command with no `GameSaveData` reflection
at all. That is a materially better target than the `GameSaveData` overload
`crucible-load-path.md` §5 nominated.

Save enumeration is `Env.GameRuns` (`| field | List`1 | GameRuns |`). Each element is a
`GameSaveDirector+GameSaveData` with confirmed fields
`runID`, `saveName`, `runPath`, `manualId`, `adventureType`, `difficulty (eGameDifficulties)`,
`mapGenSeed`, `roundCount`, `roomCount`, `loopCount`, `currentLifePool`, `maxLifePool`, `dateTime`,
`version`, `characters`, `chaosState`, `expansions`, plus `String GetFileName()` and
`Boolean IsPS4TransferRun()`. `UserData.LastGameRunIdPlayed` (`String`) names the most recent run.

**Act — start a new campaign:**
```
| method | Void | _initializeAdventureInfo   | (String pSelected) |
| method | Void | _selectChapter             | () |
| method | Void | _onSelectDifficulty        | (Boolean pIsOnlineAction) |
| method | Void | _onNodeStartAdventureClick | (NavigationSubmitEvent pEvent) |
| method | Void | CycleDirection             | (Boolean pIsRight) |
| method | Void | CycleHeader                | (Boolean pIsRight) |
| method | Void | TabDirection               | (Boolean pIsRight, Int32 pIndex) |
| field  | String       | _selectedCampaignName          | |
| field  | List`1       | _campaignOptions               | |
| field  | Dictionary`2 | _campaignDataToAdventureConfigs| |
| field  | List`1       | GATED_ADVENTURE_CONFIGS        | |
| field  | List`1       | PERMITTED_PLAYTEST_ADVENTURES  | |
```
`CycleDirection`, `CycleHeader` and `TabDirection` are **public** — the carousel can be driven
without touching a private member.

Campaign identity lives on `Env` as plain data:
`| field | String | SelectedAdventureConfig |`, `| field | eGameDifficulties | SelectedDifficulty |`,
`| field | String | SelectedGameRunId |`. `eGameDifficulties` = `NONE, APPRENTICE, JOURNEYMAN,
MASTER, GAUNTLET`.
`NetworkHelper.StartNewCampaignAsHost(String pAdventureId, eGameDifficulties pDifficulty, …)`
independently confirms the shape: **a campaign is a string id plus a difficulty enum.**
`AdventureConfig` itself carries no id field — it is keyed by name in
`_campaignDataToAdventureConfigs`. (`CampaignData` was probed by name: **NOT FOUND**.)

**Proof:** `route == ADVENTURE_SELECTION`; after a start or load, `Env.GameRun != null` and the route
advances.
**Stuck risks:** `Void _showFinishLoadingConfimPrompt(Action pOnQuit)` — a confirmation on the load
path; `Task _showErrorPrompt(VisualElement, NetworkError, CancellationToken)`; the field
`Boolean _decommissioned` marking a torn-down director; the whole `_onlineMultiplayer*` family
firing if a session is live.

### `LORE_STORE` — REACHABLE, with a caveat
Owner `LoreStoreDirector`,
`Task Initialize(UIDocument pCanvas2D, eRoutes pPreviousRoute, UserData pUserData, Camera pCameraDefault, InputController pInputControls)`
plus `| field | eRoutes | _previousRoute |`. It needs to know where you came from, which is plausibly
what `Route`'s `Object pCustomData` carries (ASSUMED — the binding is not visible). Not on a campaign
path; listed for completeness.
**Act:** `Void _bindBackButton()`, `Void _showRedeemDialog(NavigationSubmitEvent pEvent)`.
**Proof:** `route == LORE_STORE`.

### `MULTIPLAYER_LOBBY` — REACHABLE, and the thing to steer *away* from
Owner `MultiplayerLobbyDirector`,
`Void Initialize(String pAutoJoinRoomId, UIDocument pCanvas2D, UserData pUserData, Camera pCameraDefault)`.
Full treatment in §4.
**Escape:** `QuitToMenu()`; `Void _bindBackButton(Boolean pIsEnabled)`; `Route(MAIN_MENU, …)`.
**Stuck risks:** `Task _checkIfOnline(CancellationTokenSource pCancelToken)` and
`Task`1 _findAndSetBestRegion()` are network round-trips with their own cancellation tokens — a
lobby that cannot reach Photon sits spinning; `Task _onNetworkError(NetworkError pError)` →
`_showErrorPrompt` raises a modal; `Boolean _joinOrCreateCancelled`,
`Boolean _lastOnlineCheckSuccess`, `Dictionary`2 _rooms` are the readable state.

### `PARTY_MANAGEMENT` — REACHABLE-IN-FLOW
Owner `PartyManagementDirector`,
`Task Initialize(UIDocument pCanvas2D, Int32 pRandomSeed, UserData pUserData, Camera pCamera, CinemachineVirtualCamera pVirtualCamera, PartyManagementSyncData pSyncData)`
— it needs a `PartyManagementSyncData`, so a cold `Route(PARTY_MANAGEMENT, 0, null, …)` is **not**
supported by the evidence. Natural entry is from `ADVENTURE_SELECTION` after a campaign is chosen
(ASSUMED, from `_onNodeStartAdventureClick`). The director carries its own route helpers:
`Void _routeToPartyManagement(Boolean pIsFromLoadout)` and `Void _routeToAdventureSelection()`.

**This is the richest and most important route for the 35-class test matrix.** Confirmed surface:
```
| method | Task | _rebuildCharactertAsNewConfigType | (Entity pCharacter, String pClassConfigName, Boolean pOnlineAction, Boolean pTryPlayReadyUp, Boolean pIsPreset) |
| method | Void | _pOnChangeClassDelay              | (Entity pCharacter, String pClassConfigName) |
| method | Void | _rebuildCharacterEntity           | (Entity pCharacter, String pLastClassName) |
| method | Void | _createCharacter                  | (InputPlayer pPlayer, Boolean pOnlineAction, String pConnectionId, String pUsername, String pPlatformId, String pOverrideClass, Int32 pAssignmentIndex, Boolean pAssignEntity, Entity pOverrideEntity) |
| method | Task | _randomizeCharacterEntity         | (Entity pCharacter, Boolean pOnlineAction) |
| method | Void | _removeCharacter                  | (Int32 pIndex) |
| method | Void | _onSelectCharacter                | (Int32 pCurrentIndex) |
| method | Void | _onChangeCharacter                | (Boolean pSelectNext) |
| method | Void | _processCharactersBeforeAdventureStart | () |
| method | Void | _tryBeginAdventure                | (NavigationSubmitEvent pEvent) |
| method | Task | _initAdventureAndRoute            | (Boolean pOnlineAction) |
| method | Void | ClearPlayerObjects                | () |
| field  | List`1  | _playerEntities      | |
| field  | List`1  | _playableCharacters  | |
| field  | Int32   | _selectedPartyIndex  | |
| field  | Int32   | _activePlayerIndex   | |
| field  | Boolean | _inLoadout           | |
| field  | Boolean | _inCustomization     | |
| prop   | List`1  | _activeAssignedEntities | |
| prop   | Entity  | _activeCharacterEntity  | |
```

**`_initAdventureAndRoute(Boolean pOnlineAction)` is the find of this pass.** It is a one-`Boolean`
method whose name says it both starts the adventure and performs the route — the entire "press Begin
Adventure" step reduces to a single marshallable argument. Signature CONFIRMED, semantics ASSUMED.
`_tryBeginAdventure(NavigationSubmitEvent pEvent)` is the button handler above it; the event argument
is very likely ignorable (`null`), but that too is ASSUMED.

This resolves `crucible-verb-feasibility.md`'s `set_party` **UNCLEAR** verdict in the harness's
favour: the class-swap method is Director-scoped, and this route is exactly where that Director is
alive. `set_party` is not "not headless", it is "PARTY_MANAGEMENT-scoped" — which is fine now that
the harness can navigate there.

**Proof:** `route == PARTY_MANAGEMENT`; after a swap, re-read the slot entity's class.
**Stuck risks:** `Void _showPartyManagementMenu(Boolean pCanQuit, Action pCancelAction)` modal;
`_showRenameCharacterDialog`; `Boolean _checkOnlineReady()` and
`_togglePartyCreationOnlineReady(VisualElement, Int32, NetComponent)` gating Begin behind a ready-up
in online mode; and critically `CancellationTokenSource _classChangeDelayToken` plus
`SemaphoreSlim _customizationLock` — **class swaps are async and serialized.** Fire two in one frame
and the second is cancelled, not queued.

### `ADVENTURE` — REACHABLE-IN-FLOW (the overworld)
Owner `AdventureDirector`,
`Task Initialize(UIDocument pCanvas2D, UIDocument pOverheadCanvas2D, Int32 pRandomSeed, GameObject pCanvas3D, AdventureCameraController pCamera, GameObject pOverlayParent, InputController pInputControls)`
— the heaviest `Initialize` in the game. Entered by `_initAdventureAndRoute` (new run) or
`_loadGameRun` (existing run), not by a cold `Route`.

**Act:**
```
| method | Task   | _doEndTurn           | () |
| method | Void   | _tryProceed          | (Boolean pForceEndTurn) |
| method | Task   | _nextTurn            | (GameRandom pGameRandom, Boolean pForceNextRound, Boolean pIsFirstTurn) |
| method | Void   | _continueTurn        | (Boolean pIsFirstTurn, GameRandom pGameRandom) |
| method | Task   | _move                | (ValueTuple`2 pGoal, Boolean pConsumeActionPoints, Boolean pCanBeAmbushed, Boolean pShowEncounterMenu) |
| method | Task   | _move                | (AdventureMoveData pMoveData, Boolean pConsumeActionPoints, Boolean pCanBeAmbushed, Boolean pShowEncounterMenu) |
| method | AdventureMoveData | _getAdventureMoveData | (Entity pCharacter, ValueTuple`2 pGoal, Boolean pConsumeActionPoints) |
| method | Task   | _doTeleportAbility   | (Entity pCharacter, Thing pThingItem, Boolean pIsVoidwalk) |
| method | Void   | _createEncounterEntity | (ValueTuple`2 pPosition, List`1 pEncounters, EncounterGenData pData, GameRandom pRandom, Int32 pTurnsToDecay) |
| method | Task`1 | _tryCompleteQuests   | () |
| method | Task   | _resolveQuests       | (List`1 pCompletedQuests, List`1 pCompletedObjectives) |
| method | Task`1 | _tryVictoryLoss      | () |
| method | Task`1 | _endAdventure        | (Boolean pIsVictory) |
| method | Task   | _startQuest          | (QuestData pQuestData, QuestState pParentQuest) |
| method | Task`1 | _triggerChaos        | (Boolean pCheckChaosType, Int32 pChaosIncreaseValue, String pChaosEvent, Boolean pIsRemoveNextEvent) |
| method | Void   | _onStopPickHex       | () |
| field  | Boolean | _continueTurnOnInitiaze     | |
| field  | Int32   | _debugAdventureTemplateID   | |
| field  | Boolean | _debugOptionShowDebugBox    | |
```
`_move(ValueTuple`2 pGoal, …)` is the overworld movement primitive. The tuple's element types are
**ASSUMED** `(Int32, Int32)` hex coordinates — TypeProbe cannot print closed generic args, but every
sibling uses the same shape for a hex: `_placeMarker(ValueTuple`2 pHexTarget, …)`,
`_refreshHexState(ValueTuple`2 pPosition, …)`, `_hexMapPosition(ValueTuple`2 pPos)`,
`_onConfirmLeftClickMapHex(ValueTuple`2 pMapHex)`, `_renderPathPreview(ValueTuple`2 pGoalHex)`.
`Env.HexMap` is exposed as `| prop | List`1[,] | HexMap |`.

**Proof:** `route == ADVENTURE`, `Env.GameRun != null`; progress via the poll set already published
in `crucible-quest-progression.md` §4 (`GameRunData.RoundCount`, `.ActiveQuests`, `.CompletedQuests`,
`.FailedQuests`, `.GameStageIndex`, `AdventureState.CurrentStoryQuestTitle`, `.TotalRoundCount`).
**Stuck risks:** `_onStopPickHex()` / `_renderPickHexCursor(ValueTuple`2 pCursorHex)` /
`_setPickHexVisuals(Texture2D pIcon, String pText)` — the game can enter a **hex-pick mode** waiting
for a click that will never come from an unattended harness;
`_showPartyManagementMenu(Boolean pCanQuit, Action pCloseAction, Boolean pBroadcastOpenAction)`;
`_showFinishLoadingConfimPrompt(Action pOnQuit)`; `_showMapHint(Entity, Entity, ValueTuple`2)`.
Dialogue is the other big one — every Director carries four `StartDialogueAsync` overloads and
`DialogueViewHelper.IsShowing()` is the detector.

### `VENUE` — REACHABLE-IN-FLOW
Owner `VenueDirector`, `Void Initialize(Scene pScene, Int32 pRandomSeed, UIDocument pCanvas2D, GameObject pCanvas3D)`
— needs a loaded `Scene`, so no cold route.
**Act:** `Void _goToNextRoute(eRoutes pRoute)`, `Task _proceedVisualStack()`,
`Void _prepareVenuePartyForCombat()`, `Void _setPartyVisibility(Boolean pShow, Boolean pShowAppearFX)`,
`Void _removeCharacterFromVenue(Entity pCharacter)`, `Boolean _tryEndAdventureFromDungeon()`.
**Proof:** `route == VENUE`, and `RouterHelper.IsInVenue()` returns true. Venue identity via
`| prop | VenueState | _state |` →
`VenueState { Venue (VenueData), PhaseIndex (Int32), PhaseSkip (Boolean), CombatMusic (eCombatMusics),
WeatherData, LootTableArg (String), VehicleID (String), FirstInitiativeEntities, VenuePlayerNames }`.
**Stuck risk:** `Queue`1 _visualStack` + `_playVisualNode(VisualNode, Action`1 pCallback)` — the
callback-driven visual queue is the classic place an unattended run stalls.

### `DUNGEON` — REACHABLE-IN-FLOW
Owner `DungeonDirector`,
`Task Initialize(List`1 pDungeonConfigNames, Int32 pRandomSeed, Scene pScene, UIDocument pCanvas2D, GameObject pCanvas3D)`.
**Act:**
```
| method | Void | _completeDungeon        | () |
| method | Task | _nextPhase              | () |
| method | Void | _tryProceed             | () |
| method | Void | _nextFloorSection       | () |
| method | Task | _loadNextPhase          | (List`1 pPhaseList) |
| method | Task | _loadNextDioramas       | (eJunctionPoints pLastDioExitJunction, Boolean pAppendDioramas) |
| method | Void | _resetDungeonLoop       | () |
| method | Void | _bindConsoleDebugCommands | () |
| method | Task | _onSave                 | (Boolean pIsRestPhase) |
| method | Void | ForceDeinitialize       | () |
| method | Void | _loadUpcomingDirectionChoices | () |
```
`DungeonDirector` is the one gameplay owner **without** `_goToNextRoute` — it drives phases through
`_nextPhase`/`_loadNextPhase` instead.
**Proof:** `route == DUNGEON`; `| prop | DungeonState | _state |` gives `FloorIndex`, `VenueIndex`,
`CompletedRoomAmount`, `ConfigIndex`, `ConfigNames`, `ChosenDirection (eJunctionPoints)`,
`DifficultyLevel`, `RoomHistory`, `UpcomingDirectionChoices`, and four explicit latch flags:
`ForceEndDungeon`, `IsLoadingIntoDungeon`, `IsLoadingIntoRestPhase`, `HasInitializedPhase`.
`| field | ePhases | _currentPhase |` — `ePhases` = `NONE, INTRO, HALLWAY, COMBAT, COMBAT_PREVIEW,
ENCOUNTER, TRAP, TREASURE, WHEEL, REST, FORTUNE, CHOICE, CHOICE_POOL, GRAB_BAG, PRESET, BOSS,
BOSS_LOOP, PHASE_LOOP, STAIRS, CUTSCENE, SCHEDULED_EVENT, INSERT_DUNGEON, EXIT` — and
`RouterHelper.PhaseToRoute` is the dictionary that maps it to a route.
**Stuck risks:** `| field | Boolean | _canQuitToMenu |` and
`| field | CancellationTokenSource | _quitToMenuQueued |` — **the dungeon can refuse a quit**, so
`QuitToMenu()` is not a universal panic button; `_hasLoadedNextPhase`; a direction choice waiting on
`UpcomingDirectionChoices`.

### `COMBAT` — REACHABLE-IN-FLOW
Owner `CombatPhase`.
```
| method | Void | _debugEndPhase              | () |
| method | Void | _debugSetPlayersTo9999HP    | () |
| method | Void | _debugSetAllCharactersTo9999HP | () |
| method | Task | _endCombatAsync             | (Boolean pIsImmediate) |
| method | Task | _nextTurn                   | (Boolean pIsFirstTurn) |
| method | Task | _initializeNextWave         | (Boolean pIsEndTurn) |
| method | Task | _performAbility             | (Entity pEntity, Thing pThing, CombatDecisionData pCombatDecision, List`1 pResults, Boolean pTryProceed, Boolean pIsScripted) |
| method | Void | _goToNextRoute              | (eRoutes pRoute) |
| method | Task | _tryCompleteQuests          | () |
| method | Task | _startCombatQuest           | (QuestState pQuest) |
```
plus the static helpers:
```
CombatHelper | method | Boolean | TryEndGame        | (CombatState pCombatState) |
CombatHelper | method | List`1  | NextTurn          | (Env pEnv, GameRandom pGameRandom) |
CombatHelper | method | List`1  | EndTurn           | (Boolean pIsEndRound, Env pEnv, GameRandom pGameRandom) |
CombatHelper | method | List`1  | GetAbilities      | (Entity pCharacter, Boolean pMainHandOnly, Boolean pIncludeDefaultAbilities, Boolean pIncludeConsumables, Boolean pIgnoreConfuseAbilites, Boolean pIgnoreReviveAbility, Boolean pGetChargeAbilities, Boolean pGetHookAbilities) |
CombatHelper | method | Boolean | IsUsableAbility   | (GameRunData pGameRun, Entity pCharacterEntity, String pAbility, Boolean pConsiderRemainingActions) |
CombatHelper | method | List`1  | GetCombatOrder    | (CombatState pCombatPhaseData, Boolean pIsMidcombat) |
CombatHelper | method | Void    | ResetCharacterActions | (Entity pEntity) |
CombatHelper | method | List`1  | PerformAbility    | (Entity pOrigin, Entity pTarget, List`1 pParty, Thing pThing, CombatDecisionData pCombatDecision, RollResultData pRollData, Func`4 pGetStat, Func`3 pGetTileStat, SkillContext pOriginSkillContext, Boolean pConsumeAction, Env pEnv, GameRandom pGameRandom) |
```
**Compare the two ability paths.** `CombatHelper.PerformAbility` needs two delegates (`Func`4`,
`Func`3`) and a `SkillContext` the harness cannot synthesise. `CombatPhase._performAbility` needs a
`CombatDecisionData` and carries a `Boolean pIsScripted` flag that reads like it exists precisely for
non-UI callers (ASSUMED). **`use_ability` should target `CombatPhase._performAbility`, not
`CombatHelper.PerformAbility`** — the opposite of what `crucible-verb-feasibility.md` recommended
before parameter lists were available.

**Proof:** `route == COMBAT`; combatants and round via `CombatState` (`crucible-combat-field-map.md`).
**Stuck risks:** `CombatState.EndCombatEarly` / `AlternateWinConditions` / `AlternateLoseConditions`
never satisfied; an ability animation queue; `_areDebugCommandsAllowed` false, meaning the shipped
`EndPhase` is not even registered on this route.

### `ENCOUNTER` — REACHABLE-IN-FLOW
Owner `EncounterPhase`.
**Act:** `Task _debugEndPhase()`, `Task _endEncounter()`, `Void _goToNextRoute(eRoutes pNextRoute)`,
`Boolean _tryCompleteQuests()`, `Boolean _tryEndAdventureFromDungeon()`,
`EncounterActionData _createEncounterAction(Entity pEntity, eEncounterActions pAction, eEncounterActions pSelectedAction, Int32 pFocusUsed, Thing pThing)`,
`| field | Boolean | _debugForceCombat |`.
Registration on this phase is `Task _registerSendDebugCommand()`, not the common
`_registerConsoleCommands` (`crucible-quest-progression.md` §5).
**Proof:** `route == ENCOUNTER`.
**Stuck risk:** an encounter waiting on a per-player action choice with no player to choose.

### `REST` — REACHABLE-IN-FLOW
Owner `RestPhase`.
**Act:** **`Void _debugEndPhase(Int32 pOption)`** — the only overload, see §0 —
`Void _endRestPhase()`, `Void _endRestPhase(eRoutes pNextRoute)`, `Boolean _tryEndRestPhase()`,
`Task _onSave()`, `Task _tryVictoryLoss()`, `Void _debugGiveAllMercDeeds()`,
`Void _goToNextRoute(eRoutes pNextRoute)`.
**Proof:** `route == REST`. `RestPhase._onSave()` is the natural checkpoint for a long unattended run.

### `TREASURE` — REACHABLE-IN-FLOW
Owner `TreasurePhase`.
**Act:** `Void _debugEndPhase()`, `Void _startTreasurePhase()`,
`Void _openTreasure(Entity pCharacter, Int32 pPlayerIndex, Boolean pUseLockPicks, Boolean pIsAmbush, Boolean pIsOnlineAction)`,
`Void _passTreasure(List`1 pAllFilterPlayers, Boolean pTakeTurns, Int32 pCurrentActivePlayerIndex, Int32 pTimesPassed, Boolean pIsOnlineAction)`,
`Void _distributeTreasureLoot()`, `Void _goToNextRoute(eRoutes pNextRoute)`.
**Proof:** `route == TREASURE`; fields `_isLocked`, `_isMimic`, `_hasIdentified`, `_timesPassed`,
`_treasureConfig (TreasureConfig)`.
**Stuck risks:** `_isMimic` + `_isLoadingMimicCombat` +
`_mimicTransformAndStartCombat(Boolean pIsPlayerAmbush, Entity pCharacter)` — a treasure can become a
combat mid-phase; `Task _showLootActionsForPlayers(…)` waits on per-player loot choices.

### `TRAP` — REACHABLE-IN-FLOW
Owner `TrapPhase`.
**Act:** `Void _debugEndPhase()`, `Void _startTrapPhase(Boolean pIsRefresh)`,
`Void _passTrap(Boolean pOnlineAction)`, `Void _disarmTrap(Entity pCharacter, Boolean pOnlineAction)`,
`Void _bashTrap(Entity pCharacter, Boolean pOnlineAction)`,
`Void _lockpickTrap(Entity pCharacter, Boolean pOnlineAction)`,
`Void _runTrap(Entity pCharacter, Boolean pOnlineAction)`,
`Void _selectAction(Entity pEntity, eTrapActions pSelectedAction, Boolean pIsOnlineAction)`,
`Void _addFocus(Entity pCharacter, Boolean pIsOnlineAction)`,
`Void _goToNextRoute(eRoutes pNextRoute)`.
**Proof:** `route == TRAP`; `| field | Boolean | _isTrapCompleted |`,
`| field | eTrapActions | _selectedTrapAction |`, `| field | String | _trapName |`.
**Stuck risk:** the phase sits on a per-character action selection. Unattended escape is
`_passTrap(false)` then `_debugEndPhase()`.

### `WHEEL` — REACHABLE-IN-FLOW
Owner `WheelPhase`.
**Act:** `Void _debugEndPhase()`, `Void _spinWheel(Boolean pOnlineAction)`,
`Void _passWheel(Boolean pOnlineAction)`, `Void _continue(Boolean pOnlineAction)`,
`Void _navigateWheelToIndex(Int32 pWedgeIndexTarget, Boolean pOnlineAction)`,
`Void _replaceWheel(Int32 pCharacterIndex, Boolean pOnlineAction)`, `Void _restartWheelPhase()`,
`Void _goToNextRoute(eRoutes pNextRoute)`.
**Proof:** `route == WHEEL`; `_isWheelSpun`, `_remainingSpins`, `_spinIndex`,
`_currentWheelSpinIndex`, `_wheelConfigName`.
**Determinism bonus:** `| field | Boolean | _debugSetWheelSelection |` and
`| field | List`1 | _spinRigging |` — a first-class rigging hook for scenarios.
**Stuck risks:** `_isShowingReplaceMenu`, `_isGivingOutChoiceRewards`,
`_onlineWaitingForWheelDetailContinue`, and `| field | Sequence | _wheelRotationSequence |` (a DOTween
sequence — a spin animation that never resolves).

### `FORTUNE` — REACHABLE-IN-FLOW
Owner `FortunePhase`.
**Act:** `Void _debugEndPhase()`, `Void _goToNextRoute(eRoutes pNextRoute)`,
`Task _registerConsoleCommands()`, `Void _initDebug()`, `Void _debugUpdate()`. Fewest distinct
methods of any phase; per `crucible-quest-progression.md` §5 it has no quest-completion surface
beyond the shared hooks.
**Proof:** `route == FORTUNE`.

### `CINEMATIC_INTRO` / `CINEMATIC_OUTRO` / `CINEMATIC_CREDITS` — **UNCLEAR**
**No owning type found.** `CinematicDirector`, `CinematicPhase`, `CinematicHelper` and
`VideoDirector` were each probed by exact name and are all **NOT FOUND**. `RouterMono` has no
cinematic field, and none of its `UIDocument` fields is cinematic-named. These three routes have a
name in `eRoutes` and nothing else this pass could attach to them.

What *is* known:
- `| field | Boolean | SkipOutroCinematic |` on `AppConfigManager+AppConfig` — a global switch that,
  if honoured, removes the outro from an unattended run entirely. Whether it is read at route time is
  ASSUMED.
- `crucible-quest-progression.md` §3 already flagged that `_endAdventure(Boolean pIsVictory)` is
  ASSUMED — not confirmed — to route to `CINEMATIC_OUTRO`.
- `RouterHelper.GetEndOfAdventureRoute(GameRunData pGameRun)` returns the route an ending run goes to.
  **This is the way to learn what actually happens at the end of a campaign without guessing**, and
  it is a cheap read-only call. It should be the first live probe of S3.

**Honest statement: I could not work out how to reach, exit, or observe any of the three cinematic
routes from code.** They are the one genuine gap in this inventory, and also a plausible place for an
unattended run to sit forever, since a cinematic is by nature a timed non-interactive state.

---

## 4. The multiplayer-browser-at-startup problem

**What Ben is seeing has a named mechanism, and it is auto-join — not a menu default.** Five
CONFIRMED members line up into one chain:

```
Env                      | field  | String  | RoomAutoJoinId        |                                  |
NetworkHelper            | field  | Boolean | CanAutoJoinRoom       |                                  |
MainMenuDirector         | method | Boolean | TryAutoJoinRoom       | ()                               |
MultiplayerLobbyDirector | method | Void    | Initialize            | (String pAutoJoinRoomId, UIDocument pCanvas2D, UserData pUserData, Camera pCameraDefault) |
RouterMono               | method | Void    | _registerDebugCommands| (String& autoJoinRoomId)         |
```

Read together (members CONFIRMED, mechanism ASSUMED):
`RouterMono._registerDebugCommands` takes a **by-reference `String& autoJoinRoomId`** — it *produces*
a room id as a by-product of registering debug commands, during startup. That id reaches
`Env.RoomAutoJoinId`. `MainMenuDirector.TryAutoJoinRoom()` returns `Boolean` — the classic shape of
"if an auto-join id is set, consume it and route to `MULTIPLAYER_LOBBY`; otherwise return false and
stay put". `MultiplayerLobbyDirector.Initialize` then takes that same id as its **first** parameter,
and `Void _continue(VisualElement pLayout, String pAutoJoinRoomId)` plus
`Task _connectToRoomById(String pRoomId, VisualElement pLayout)` complete the path. The lobby
*browser* specifically is `MainMenuDirector.<_onClickMultiplayer>g___toMultiplayerLobbyBrowser|42_2`.

Three contributing factors specific to this install:

1. **The launcher is a multiplayer launcher.** `FTK2.Crucible/tools/launch-peers.ps1` exists to start
   N peers and defaults to `-Count 2`. It passes only `-SKIPSPLASH -WINDOWED`, so it is not itself
   injecting a room id — but any Steam-side invite/join argument, or an id left over in `User.ftk2`,
   arrives through the same `Env.RoomAutoJoinId` door.
2. **`UserData.OnlineMutliplayerEnabled`** (`Boolean` — the game's own typo) is persisted in
   `%LOCALAPPDATA%Low\IronOak Games\For The King II\User.ftk2` (354 KB, last written 2026-08-23).
   Whatever the user last did online is remembered across launches.
3. **The main menu does network work before it settles.** `RouterMono.ReCheckMultiplayerPermissions()`,
   `OnConnectionChanged(Boolean pInIsConnected)`, `IEnumerator RefreshPlatformBlocklistCo()` and
   `_tryInitPlayfab(Env pEnv)` all run at startup, and `MainMenuDirector` carries
   `_readyToConnectToBestRegion`, `_readyToPlayNetworkAction`, `_lastPlayfabInitRetry`,
   `_playfabInitRetryDelayMs`. `AppConfig.GenerateMockLobbies` and `AppConfig.HideUnjoinableLobbies`
   are live switches on the same surface.

**How to navigate away — three independent levers, all CONFIRMED members:**

| Lever | Call | Confidence |
|---|---|---|
| Prevent it before it fires | write `Env.RoomAutoJoinId = null` and `NetworkHelper.CanAutoJoinRoom = false` at the first tick after boot (both plain fields) | members CONFIRMED, effect ASSUMED |
| Leave the lobby | `MultiplayerLobbyDirector.QuitToMenu()` (public `Task`), or `_bindBackButton(Boolean pIsEnabled)`, or `RouterMono.Route(MAIN_MENU, 0, null, false, true)` | CONFIRMED |
| Tear the session down | `NetworkHelper.DisconnectFromRoomAndResetNetworkData(NetworkData pNetworkData, UserData pUser)` → `Task`1`, or `HardDisconnectAndResetNetworkData(NetworkData, UserData)` | CONFIRMED signatures |

**Recommended order for the harness:** clear `Env.RoomAutoJoinId` and `NetworkHelper.CanAutoJoinRoom`
at the first tick after `RouterHelper.GetCurrentRoute() != NONE`; then, if the route is already
`MULTIPLAYER_LOBBY`, call `Route(MAIN_MENU, …)` and assert the route actually changed. Do **not**
start with `DisconnectFromRoomAndResetNetworkData` — it mutates `UserData` and is the heaviest of the
three.

**One caution, and it is the most likely self-inflicted wedge in the whole design.**
`GameBridge.IsOnlineSession()` fails *closed*: if it cannot read
`NetworkData.PlayingOnlineMultiplayer` it returns `true`
(`FTK2.Crucible/src/Crucible.Plugin/GameBridge.cs`), and `CommandGate.Evaluate`
(`FTK2.Crucible/src/Crucible.Core/CommandRequest.cs`) then denies every command that is not on the
six-name `ReadOnlyCommands` list. So a harness that lands in the MP lobby can find its own escape
verbs refused. **The navigation verbs must be classified read-only in `CommandGate`, or the escape
hatch is gated shut by the safety system.**

---

## 5. What can wedge an unattended run

Every detector and escape below is a CONFIRMED member; the "it will happen" judgement is mine.

| Wedge | Detector (CONFIRMED) | Escape (CONFIRMED) |
|---|---|---|
| System modal (exit dialog, network error, controller disconnect) | `SystemDialogViewHelper.IsShowing()` → `Boolean` | `SystemDialogViewHelper.ForceHide(Boolean pHideNoControllerDialog)`; or invoke the stored `Action` fields `_onStandardOk` / `_onStandardCancel` |
| First-run / experimental / demo prompt | `PromptViewHelper.PromptIsShowing()` → `Boolean` | `PromptViewHelper.ClosePrompt()` |
| Dialogue box waiting on Continue | `DialogueViewHelper.IsShowing()` → `Boolean`; `prop Boolean ReadyToPlayNetworkDialogueAction` | `DialogueViewHelper.ReleaseInput()`; `<director>._dialogue_Complete()` |
| Loading / transition curtain never lifts | `TransitionViewHelper.IsShowing()` → `Boolean`, `IsTextVisible()` → `Boolean` | `TransitionViewHelper.HideTransition()`, `ImmediateBlackTransition()` |
| Load-finish confirmation prompt | `<director>._showFinishLoadingConfimPrompt(Action pOnQuit)` was called | `<director>._tryFinishLoadingAndInit(Action pOnQuit, Boolean pSkipTransition)` |
| Console overlay eating input | `CommandLineViewHelper.IsShowing()` (already wired in `GameBridge`) | `CommandLineViewHelper.ToggleShow()` |
| Hex-pick mode on the overworld | `AdventureDirector._setPickHexVisuals` / `_renderPickHexCursor` were called | `AdventureDirector._onStopPickHex()` |
| Dungeon refuses to quit | `DungeonDirector._canQuitToMenu` (`Boolean`), `_quitToMenuQueued` (`CancellationTokenSource`) | **none found** — the dungeon must be played out via `_nextPhase()` / `_completeDungeon()` |
| Visual-node queue stalls | `Queue`1 _visualStack` non-empty on Venue / Intro / Treasure / Trap / Wheel | `_proceedVisualStack()` → `Task`, then `_goToNextRoute(eRoutes)` |
| Wheel spin animation | `_isWheelSpun`, `Sequence _wheelRotationSequence` | `_passWheel(false)` then `_debugEndPhase()` |
| Class swap silently cancelled | `PartyManagementDirector._classChangeDelayToken`, `_customizationLock` (`SemaphoreSlim`) | serialize swaps; re-read the slot's class after each |
| Debug commands not registered on this route | `/commands` count (2 at `NONE`, ~21 at `MAIN_MENU` — measured) | re-read `/commands` after every route change; never cache |
| `IsOnlineSession()` fails closed → everything denied | `/health` reports `online: true` with no real session | classify navigation verbs read-only in `CommandGate` — see §4 |
| Cinematic route with no owner | **none found** | **none found** — see §3 |
| Steam not running | `route == NONE` forever, black window (measured, spec §0) | restart with Steam up |

---

## 6. What I could not work out

1. **All three `CINEMATIC_*` routes.** No owning type exists under any name probed. No entry, no exit,
   no arrival proof. `RouterHelper.GetEndOfAdventureRoute(GameRunData)` is the cheapest way to learn
   whether a finishing campaign even passes through one.
2. **Whether a cold `Route(<gameplay route>, …)` works at all.** Every gameplay `Initialize` needs a
   `Scene`, `Diorama`, `VenueState` or `PartyManagementSyncData` that only the previous route
   produces. `Object pCustomData` is presumably how `Route` supplies it (ASSUMED), but nothing
   confirms the shape per route, and `ArgCoercion` deliberately refuses anything but `null` for
   `Object` parameters. **Practical consequence: traversal is a graph walk through legal transitions,
   not teleportation.** Any plan that assumes "route straight into COMBAT" is wrong.
3. **What sets `_areDebugCommandsAllowed`.** `AppConfig.BlockNetworkUnsafeCheats` is the obvious
   candidate; unconfirmed.
4. **The exact `ValueTuple`2` element types** for hex positions. Uniform across ~10 call sites,
   ASSUMED `(Int32, Int32)`.
5. **Whether `_initAdventureAndRoute(Boolean)` alone starts a run**, or whether
   `_processCharactersBeforeAdventureStart()` must precede it. Signature-only evidence.
6. **The registered *names* of the shipped console commands per phase.** `_registerConsoleCommands()`
   bodies are invisible to TypeProbe; the only way to enumerate them is `GET /commands` on the live
   game, per route.
