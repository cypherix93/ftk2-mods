# Crucible MCP Tool Surface — the complete FTK2 drive / read / skip catalogue

**Produced 2026-08-23.** Objective: enumerate *every* MCP tool the Crucible harness needs in order to
drive, read, and speedrun the whole of For The King II unattended — menus, dialogs, campaign start,
overworld, dungeon, every phase, combat, quests, and run completion — **plus the two-tier game-state
generation system (§5) that gives every feature under test a state to be tested in** — and to state
honestly which of those tools rest on evidence and which do not.

This is the catalogue. The build order is
[`docs/superpowers/plans/2026-08-23-s3-mcp-tool-surface.md`](../superpowers/plans/2026-08-23-s3-mcp-tool-surface.md).

**Evidence base — nothing here is named unless it appears in one of these:**

| Document | What it supplies |
|---|---|
| [`crucible-ui-driving.md`](./crucible-ui-driving.md) | UIToolkit read + click APIs. The new primary architecture. |
| [`crucible-ui-overlay-system.md`](./crucible-ui-overlay-system.md) | Modal detect / dismiss helpers; the P0-16 mechanism. |
| [`crucible-traversal-inventory.md`](./crucible-traversal-inventory.md) | Per-route owners, act/leave surface, wedge table. |
| [`crucible-verb-feasibility.md`](./crucible-verb-feasibility.md) | The 11 spec verbs triaged against the assembly. |
| [`crucible-load-path.md`](./crucible-load-path.md) | `RouterMono.Route`, save enumeration, `_loadGameRun`. |
| [`crucible-quest-progression.md`](./crucible-quest-progression.md) | Quest/objective state, run-completion path. |
| [`crucible-combat-field-map.md`](./crucible-combat-field-map.md) | `CombatState`, `Entity`, components, `CharacterHelper`. |
| [`crucible-rng-field-map.md`](./crucible-rng-field-map.md) | Gameplay vs visual RNG; patch targets. |
| [`../superpowers/specs/2026-08-23-crucible-autopilot-design.md`](../superpowers/specs/2026-08-23-crucible-autopilot-design.md) | The spec this extends. |

---

## 0. Status legend, and what it costs to get it wrong

| Status | Meaning |
|---|---|
| **CONFIRMED-LIVE** | Exercised against the running retail game and observed to do what the row says. |
| **CONFIRMED-API-ONLY** | The exact member/signature was dumped from the retail assembly by TypeProbe (or, for pure-Unity types, the equivalent probe described in `crucible-ui-driving.md` §Methodology). Never called live. |
| **ASSUMED** | The member exists, but the *semantics*, the call chain, or the argument shape is inference from a name or from wiring. TypeProbe prints signatures, never bodies. |
| **UNKNOWN** | No evidence path to a working call exists yet. Needs a spike to decide whether the tool is even buildable. |

The distinction is not pedantry. `MultiplayerDemoQuickCombat` is registered, callable, returns `ok:true`,
and throws `NotImplementedException` internally (spec §0). `StateReader` read
`NetworkData.PlayerCount` — a plausible, wrong name — for months. **A name that looks right is not
evidence, and a dispatch receipt is not a post-condition.**

---

## 1. The architecture: UI drives, internals skip

Today's live session settled an architectural question the earlier plans left open.

**Three measured facts force the split.**

1. **`RouterMono.Route(eRoutes, Int32, Object, Boolean, Boolean)` dispatches.** CONFIRMED-LIVE.
   Navigation through the router works.
2. **`route` is not a state signal.** CONFIRMED-LIVE: `RouterHelper.GetCurrentRoute()` returned
   `MAIN_MENU` while the screen showed the multiplayer browser *plus* an "Online Error: Adventure Not
   Found (P0-16)" modal. `crucible-ui-overlay-system.md` §2 explains why this is structural, not a
   harness bug: dismissal lives entirely on the `*ViewHelper` layer, and **nothing in `RouterMono`
   calls back into `MultiplayerViewHelper`**. Any design whose only readiness check is
   `wait_for(route == X)` is unsound on its own.
3. **Internals-based dismissal of that modal failed three times live** — `Route(MAIN_MENU, …)`;
   `Route` with force + reload; and `MultiplayerViewHelper.HideAllMenus()`, which *ran* and left the
   modal on screen. The modal has a **Close button**. Clicking is what closes it.

So:

```
   READ THE SCREEN + PRESS THINGS                 SKIP / CHEAT / SET STATE
   ------------------------------                 ------------------------
   UIDocument.rootVisualElement                   <phase>._debugEndPhase()
   VisualElement.Children() walk                  AdventureDirector._endAdventure(bool)
   Button.clickable.clicked.Invoke()              CharacterHelper.TryKillCharacter
   NavigationSubmitEvent / Cancel / Move          QuestState.CompletedObjectives[i] = true
                                                  GameRandom.Seed
   -> navigation, dialogs, modals, cinematics     -> what clicking is too slow or too blind for
```

**The UI layer is authoritative for "where am I and what can I press".** It works on any screen
without knowing which Director owns it — including the three `CINEMATIC_*` routes, which have **no
owning type at all** (`crucible-traversal-inventory.md` §3: `CinematicDirector`, `CinematicPhase`,
`CinematicHelper`, `VideoDirector` all NOT FOUND). Clicking is the only handle anyone has proposed
for those routes.

**The internals layer is authoritative for skipping.** No amount of clicking ends a 40-round dungeon
in one call; `DungeonState.ForceEndDungeon` might. No click sets a seed.

**Both layers must be read-only-classified in `CommandGate`** where they only observe or navigate.
`GameBridge.IsOnlineSession()` fails *closed* — it returns `true` when it cannot read
`NetworkData.PlayingOnlineMultiplayer` — and `CommandGate.Evaluate` then denies everything not on the
six-name `ReadOnlyCommands` list. A harness that boots into the MP lobby can find its own escape verbs
refused (`crucible-traversal-inventory.md` §4). This is the single most likely self-inflicted wedge in
the whole design.

**One hard constraint shapes every row below.** `CommandLineHelper` marshals the raw arg array into a
handler's *discrete typed parameters*, and its vocabulary is **`int / float / double / bool / string /
Vector2 / Vector3` only**. A `string[]` handler is rejected at registration
(`TargetInvocationException`, verified in-game 2026-08-23). **Enums are not in the vocabulary** —
`eRoutes` arrives as a `string` and is parsed inside the handler. And registration must be deferred to
the `RouterMono` tick: `CommandLineHelper.Initialize` has not run during plugin `Awake`, so the
registry does not exist yet (`ReflectionCommands.TryRegister` already implements the retry loop).

---

## 2. The catalogue

100 tools in eleven categories. MCP tool names are `ftk2_*` (the thin Node pipe); the console command that
backs each is `crucible_*` unless the row says it wraps a shipped IronOak command. Where a tool is
composite, the "underlying API" column lists every member it touches.

### A — UI driving, screen truth, and modal escape (10 tools)

Nothing else in this document works until the harness can escape the state the game boots into.

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| A1 | `ftk2_ui_dump` | `crucible_ui_dump(string pFilter)` | Walks every live `UIDocument` and emits `{type, name, text, visible, enabled, focused}` per element. The Playwright-style snapshot. | `UnityEngine.Object.FindObjectsOfType(Type)`; `UIDocument.rootVisualElement`; `VisualElement.Children()` / `.name` / `.visible` / `.enabledInHierarchy`; `IResolvedStyle.display`; `TextElement.text`; `FocusController.focusedElement` | CONFIRMED-API-ONLY |
| A2 | `ftk2_ui_click` | `crucible_ui_click(string pSelector)` | Presses the first visible `Button` matching by `name` (exact) then `text` (case-insensitive substring). | `Button.clickable` → `Clickable.clicked` (public `Action` **field**) `.Invoke()`; fallback `Clickable.clickedWithEventInfo`; fallback `NavigationEventBase<NavigationSubmitEvent>.GetPooled(EventModifiers)` + `VisualElement.SendEvent(EventBase)` | CONFIRMED-API-ONLY |
| A3 | `ftk2_ui_key` | `crucible_ui_key(string pKey)` | Sends a navigation event to the focused element, or to the root if nothing is focused. `up/down/left/right`, `submit`, `cancel`. | `NavigationMoveEvent.GetPooled(Direction, EventModifiers)`; `NavigationEventBase<NavigationSubmitEvent \| NavigationCancelEvent>.GetPooled(EventModifiers)`; `VisualElement.SendEvent` | CONFIRMED-API-ONLY |
| A4 | `ftk2_ui_focus` | `crucible_ui_focus(string pSelector)` | Moves focus to a named element, so A3 lands where intended. | `Focusable.Focus()`; `Focusable.focusable` / `canGrabFocus` / `excludeFromFocusRing` | CONFIRMED-API-ONLY |
| A5 | `ftk2_ui_text` | `crucible_ui_text()` | All visible text on screen, in tree order. **This is how objectives, quest titles, and dialog bodies get read** without knowing which Director rendered them. | `TextElement.text` / `renderedText` / `originalText` over the A1 walk | CONFIRMED-API-ONLY |
| A6 | `ftk2_ui_wait` | *(MCP-side; polls `ftk2_ui_dump`)* | Blocks until a selector appears or disappears, or a timeout expires. Polling lives on the Node side — it must never block the game's main thread. | Repeated `ftk2_exec` over the RPC bridge | CONFIRMED-LIVE (polling over the bridge is proven; the predicate is A1's) |
| A7 | `ftk2_screen` | `crucible_screen()` | **The screen-truth composite, and the replacement for `wait_for(route == X)`.** Returns route *and* the modal oracles *and* a digest of visible buttons/text, so divergence is visible instead of fatal. | `RouterHelper.GetCurrentRoute()` + A8 + A1 | CONFIRMED-API-ONLY |
| A8 | `ftk2_modals` | `crucible_modals()` | The six "am I wedged" oracles in one read. | `MultiplayerViewHelper.AreAnyMultiplayerMenusVisible()`; `PromptViewHelper.PromptIsShowing()`; `SystemDialogViewHelper.IsShowing()`; `DialogueViewHelper.IsShowing()`; `TransitionViewHelper.IsShowing()`; `CommandLineViewHelper.IsShowing()` | CONFIRMED-API-ONLY |
| A9 | `ftk2_dismiss` | `crucible_dismiss(string pWhich)` | Internals-based dismissal. **Kept as a fallback, not the primary.** | `MultiplayerViewHelper.HideAllMenus()` / `HideMultiplayerJoinModal()` / `HidePromptMenu()` / `HideMultiplayerNotificationMenu()` / `HideMultiplayerWaitingNotificationMenu()` / `HideMultiplayerMenu(Boolean)`; `PromptViewHelper.ClosePrompt()`; `SystemDialogViewHelper.ForceHide(Boolean)`; `TransitionViewHelper.HideTransition()`; `DialogueViewHelper.ReleaseInput()` | CONFIRMED-API-ONLY signatures — **but `HideAllMenus()` ran live against the P0-16 modal and did not close it.** Treat every `Hide*` as unproven. |
| A10 | `ftk2_escape` | `crucible_escape()` | The boot-escape macro: read A7; if a modal is up, click its Close/Back/OK button (A2); if the route is `MULTIPLAYER_LOBBY`, detach (C2) then `QuitToMenu()`; re-read A7 as the post-condition. | Composition of A1/A2/A7/A8, C2, C9 | ASSUMED (each part is confirmed; the sequence has never been run) |

**What is deliberately *not* in this category.** `crucible-ui-driving.md` §4 flags raw `KeyDownEvent` /
`PointerDownEvent` / `ClickEvent` injection as NEEDS-LIVE-SPIKE — their `GetPooled` chains were never
traced the way `NavigationEventBase<T>`'s was. **No tool above needs them.** The `Clickable.clicked`
invocation path plus the three Navigation events cover "press this button" and "send this key" without
constructing a pointer or keyboard event at all. `InputController` is also excluded: it is a gate
(`RequestDisable` / `ReleaseDisable` / `EnableGlobalInput`), not an input source, and its
`_submitPerformed` / `_selectPerformed` / `_backPerformed` handlers take an Input System
`CallbackContext` that wraps native event pointers and cannot be fabricated reflectively.

### B — State reading (11 tools)

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| B1 | `ftk2_health` | *(RPC)* | Is the instance alive and is the bridge serving. | Crucible `RpcServer` | CONFIRMED-LIVE |
| B2 | `ftk2_state` | *(RPC)* | `crucible.state.v1` — route, run seed/day/gold/chapter, network, digest. | `StateReader` | CONFIRMED-LIVE |
| B3 | `ftk2_state?schema=v2` | *(RPC)* | Combat-level snapshot: combatants, hp, statuses, stats, synthesized `turn` / `phase`. | `CombatState.Entities` / `.TotalRounds` / `.WaveIndex`; `Entity.Guid`; `CharacterComponent.DisplayName` / `.ConfigName` / `.CurrentHealth`; `PlayerComponent` presence; `CharacterHelper.GetMaxHealth` / `.IsDead` / `.GetStat`; `StatusEffectComponent.Statuses` → `StatusEffectInfo.Duration` | CONFIRMED-API-ONLY (field map complete; S1 unbuilt) |
| B4 | `ftk2_get` | `crucible_get(string pPath)` | Reflective dot-path read, including private members. | `AccessTools.TypeByName` + field/property walk | **CONFIRMED-LIVE** |
| B5 | `ftk2_invoke` | `crucible_invoke(string pType, string pMethod, string pArgs)` | Reflective call, including private methods; resolves instances via static `Instance`/`Current`, `FindObjectOfType`, then a private field on the live `RouterMono`. | `ReflectionCommands.TryInvoke` | **CONFIRMED-LIVE** |
| B6 | `ftk2_where` | `crucible_where()` | Route + the wedge oracles, no UI walk. The cheap sibling of A7. | `RouterHelper.GetCurrentRoute()`; the A8 set | CONFIRMED-API-ONLY |
| B7 | `ftk2_runs` | `crucible_runs()` | Every save on disk. **`Env.GameRuns` is a `List<String>` of run-id strings — VERIFIED LIVE 2026-08-23.** `crucible-load-path.md` §2 assumed it was `List<GameSaveData>`; that assumption was wrong. `GameSaveData` must be reached some other way (spike SP-9). | `Env.GameRuns` (`List<String>`, run ids); `UserData.LastGameRunIdPlayed`; `UserData.LastPlayedVersionString`; `GameSaveDirector+GameSaveData.{runID, saveName, manualId, dateTime, roundCount, difficulty, characters, chaosState, expansions, mapGenSeed, runPath, adventureType}` and `GetFileName()` | run-id list **CONFIRMED-LIVE**; the `GameSaveData` projection CONFIRMED-API-ONLY and not yet reachable |
| B8 | `ftk2_progress` | `crucible_progress()` | "Did the run advance." | `GameRunData.RoundCount` / `.GameStageIndex` / `.GameStageRoundStart` / `.AdventureState`; `AdventureState.CurrentStoryQuestTitle` / `.CurrentOptionalQuestTitle` / `.TotalRoundCount` | CONFIRMED-API-ONLY |
| B9 | `ftk2_quests` | `crucible_quests()` | Active / completed / failed / future quests. | `GameRunData.ActiveQuests` / `.CompletedQuests` / `.FailedQuests` / `.FutureQuests`; `QuestState.Data` / `.MapID` / `.RoundsLeft`; `QuestData.ID` / `.QuestType` / `.Objectives` / `.ObjectiveCustomTexts` / `.AdventureEndTrigger` | CONFIRMED-API-ONLY |
| B10 | `ftk2_entities` | `crucible_entities(string pScope)` | **The handle table.** Emits `index → Entity.Guid → DisplayName / ConfigName` for the current combat or party, so every later verb can take an `int` instead of an `Entity`. | `CombatState.Entities`; `PartyManagementDirector._playerEntities`; `Entity.Guid`; `CharacterComponent.DisplayName` / `.ConfigName`; `PlayerComponent` presence | ASSUMED (members CONFIRMED; the handle scheme is this catalogue's own design — see §4) |
| B11 | `ftk2_wait_for` | *(MCP-side; polls B2/B3/A7)* | Blocks until a JSON path satisfies a predicate. The `wait_for` primitive spec §6 requires. | `PathParser` + repeated RPC reads | CONFIRMED-LIVE (mechanism) |

### C — Run lifecycle: navigate, choose, load, start, save, end (10 tools)

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| C1 | `ftk2_route` | `crucible_route(string pRoute, bool pForce)` | Navigate. **`EXIT` is deny-listed** — `Route(EXIT, …)` terminates the process and one mistyped argument ends an overnight run. | `RouterMono.Route(eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData, Boolean pReload, Boolean pForceRoute)`; reached via `RouterHelper._router` or `FindObjectOfType` | **CONFIRMED-LIVE** (it dispatches). Sufficiency for menu-weight routes ASSUMED; **it does not repaint a stale screen** — always follow with A7. |
| C2 | `ftk2_mp_detach` | `crucible_mp_detach(bool pHardDisconnect)` | Stops the boot-time auto-rejoin that strands the harness in `MULTIPLAYER_LOBBY`. | `Env.RoomAutoJoinId` (field write); `NetworkHelper.CanAutoJoinRoom` (field write); `NetworkHelper.DisconnectFromRoomAndResetNetworkData(NetworkData, UserData)` / `HardDisconnectAndResetNetworkData(NetworkData, UserData)` | CONFIRMED-API-ONLY (fields + signatures); effect ASSUMED |
| C3 | `ftk2_load_run` | `crucible_load_run(string pRunIdOrFile)` | Load a save. Uses the **two-string overload**, which needs no `GameSaveData` marshalling — which matters more now that `Env.GameRuns` turns out to hold only run-id strings (B7). Private; reflection is fine, Crucible already invokes private members. | `AdventureSelectionDirector._loadGameRun(String, String pDisableLogMessage)` — two probes recorded the first parameter as `pFileName` and as `pGameSaveData`; the *shape* (two strings) is what both agree on and what the tool depends on. Supporting `._loadGameRunImpl(GameSaveData)`, `._loadSave(String, String, CancellationToken)` | CONFIRMED-API-ONLY; that it alone carries the route to `ADVENTURE` is ASSUMED |
| C4 | `ftk2_new_run` | `crucible_new_run(string pAdventureId, string pDifficulty)` | Start a new campaign. A campaign is *a string id plus a difficulty enum* — confirmed independently by `NetworkHelper.StartNewCampaignAsHost(String pAdventureId, eGameDifficulties pDifficulty, …)`. | `Env.SelectedAdventureConfig` / `Env.SelectedDifficulty` (field writes); `AdventureSelectionDirector._initializeAdventureInfo(String pSelected)`; `._onNodeStartAdventureClick(NavigationSubmitEvent pEvent)`; `eGameDifficulties.{NONE, APPRENTICE, JOURNEYMAN, MASTER, GAUNTLET}` | CONFIRMED-API-ONLY; passing `null` for the UI event is ASSUMED. **A2 is the fallback: click the Start button instead.** |
| C5 | `ftk2_campaigns` | `crucible_campaigns()` | Which campaigns exist and which are gated. | `AdventureSelectionDirector._campaignOptions`, `._campaignDataToAdventureConfigs`, `.GATED_ADVENTURE_CONFIGS`, `.PERMITTED_PLAYTEST_ADVENTURES`, `._selectedCampaignName`; public carousel `CycleDirection(Boolean)` / `CycleHeader(Boolean)` / `TabDirection(Boolean, Int32)` | CONFIRMED-API-ONLY |
| C6 | `ftk2_party_begin` | `crucible_party_begin()` | Press "Begin Adventure". One bool — the whole step reduces to a marshallable argument. | `PartyManagementDirector._initAdventureAndRoute(Boolean pOnlineAction)`; possible prerequisite `._processCharactersBeforeAdventureStart()`; button handler `._tryBeginAdventure(NavigationSubmitEvent)` | ASSUMED (signature CONFIRMED; that it alone starts a run is inference — spike SP-4) |
| C7 | `ftk2_save` | `crucible_save()` | Checkpoint a long run. | `RestPhase._onSave()`; `DungeonDirector._onSave(Boolean pIsRestPhase)`; `Env.ManualSaveIdTracker` | CONFIRMED-API-ONLY |
| C8 | `ftk2_end_run` | `crucible_end_run(bool pIsVictory)` | End the adventure with an explicit victory flag. Present on nine owners. | `<live owner>._endAdventure(Boolean pIsVictory)` on `AdventureDirector`, `AdventureSelectionDirector`, `PartyManagementDirector`, `VenueDirector`, `DungeonDirector`, `CombatPhase`, `EncounterPhase`, `RestPhase`, `FortunePhase` | CONFIRMED-API-ONLY |
| C9 | `ftk2_quit_menu` | `crucible_quit_menu()` | The panic button. **Public** on every Director and Phase. | `<live owner>.QuitToMenu()` → `Task` | CONFIRMED-API-ONLY. **Not universal:** `DungeonDirector._canQuitToMenu` / `._quitToMenuQueued` mean the dungeon can refuse. |
| C10 | `ftk2_next_route` | `crucible_next_route()` | Ask the game where the run *should* go next, instead of hand-writing a route graph. | `RouterHelper.GetNextRoute(GameRunData)`; `RouterHelper.GetEndOfAdventureRoute(GameRunData)`; `RouterHelper.PhaseToRoute`; `RouterHelper.IsInVenue()`; `RouterMono.IsGameplayRoute(eRoutes)` | CONFIRMED-API-ONLY; side-effect-freedom ASSUMED |

### D — Combat (11 tools)

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| D1 | `ftk2_end_phase` | *(wraps shipped `EndPhase`)* | Advance one phase via the game's own registered command. | `CommandLineHelper` shipped command | CONFIRMED-LIVE |
| D2 | `ftk2_advance` | `crucible_advance(string pMode, int pArg)` | The generic phase-skip dispatcher; picks the right member for the live owner. **`RestPhase` has exactly one `_debugEndPhase` and it takes an `Int32`** — a dispatcher assuming a uniform zero-arg overload fails on REST. | `CombatPhase._debugEndPhase()`; `EncounterPhase._debugEndPhase()`; `FortunePhase` / `TreasurePhase` / `TrapPhase` / `WheelPhase._debugEndPhase()`; `RestPhase._debugEndPhase(Int32 pOption)`; `AdventureDirector._doEndTurn()`; `DungeonDirector._nextPhase()`; `<owner>._goToNextRoute(eRoutes)` | CONFIRMED-API-ONLY. **Gated by `_areDebugCommandsAllowed`** (field on seven phases) — what sets it is unknown. |
| D3 | `ftk2_combat` | `crucible_combat()` | Raw combat state, including the end-condition levers. | `CombatState.{Entities, TotalRounds, WaveIndex, EnemiesPerWave, DeadEnemiesCount, EndCombatEarly, AlternateWinConditions, AlternateLoseConditions, IsDungeon, GridType, Random, EntityInitiative, BossFightState}`; `CombatPhase._activeCharacterEntity` / `._combatState` | CONFIRMED-API-ONLY |
| D4 | `ftk2_next_turn` | `crucible_next_turn()` | Advance to the next combatant. | `CombatHelper.NextTurn(Env pEnv, GameRandom pGameRandom)`; `CombatPhase._nextTurn(Boolean pIsFirstTurn)` | CONFIRMED-API-ONLY |
| D5 | `ftk2_end_turn` | `crucible_end_turn(bool pIsEndRound)` | End the active combatant's turn. | `CombatHelper.EndTurn(Boolean pIsEndRound, Env pEnv, GameRandom pGameRandom)`; `CombatHelper.ResetCharacterActions(Entity)` | CONFIRMED-API-ONLY |
| D6 | `ftk2_kill_all` | `crucible_kill_all(bool pEnemiesOnly)` | Wipe the field. The fastest combat skip that still runs the game's own death and loot resolution. | Loop `CombatState.Entities` → `CharacterHelper.TryKillCharacter` / `.KillCharacter`; filter with `CharacterHelper.IsOpponent` / `.IsFriendly` (`CharacterComponent.GroupIndex == 0` is the verified friendly test) | ASSUMED (both members CONFIRMED; the loop's effect on wave and quest resolution is not) |
| D7 | `ftk2_end_combat` | `crucible_end_combat(bool pImmediate)` | Close the fight outright. | `CombatPhase._endCombatAsync(Boolean pIsImmediate)`; `CombatHelper.TryEndGame(CombatState pCombatState)` | CONFIRMED-API-ONLY. **`TryEndGame` carries no victory flag** — it evaluates existing conditions. `force_win` / `force_lose` are *not* buildable here; they collapse into C8 at run scope. |
| D8 | `ftk2_combat_early` | `crucible_combat_early(bool pValue)` | Set the engine's own early-exit latch and let the game resolve normally. | `CombatState.EndCombatEarly` (field write); read-back via D3 | ASSUMED |
| D9 | `ftk2_next_wave` | `crucible_next_wave(bool pIsEndTurn)` | Skip to the next wave of a multi-wave fight. | `CombatPhase._initializeNextWave(Boolean pIsEndTurn)`; read-back via `CombatState.WaveIndex` | CONFIRMED-API-ONLY |
| D10 | `ftk2_abilities` | `crucible_abilities(int pEntity)` | List what a combatant can do — the prerequisite for D11. | `CombatHelper.GetAbilities(Entity, Boolean ×7)`; `CombatHelper.IsUsableAbility(GameRunData, Entity, String pAbility, Boolean)`; `CombatHelper.GetCombatOrder(CombatState, Boolean)` | ASSUMED (signatures CONFIRMED; needs the B10 handle scheme) |
| D11 | `ftk2_use_ability` | `crucible_use_ability(int pEntity, int pTarget, string pAbility)` | Perform a specific ability. **The single biggest hole in the combat surface.** | `CombatPhase._performAbility(Entity, Thing, CombatDecisionData, List\`1, Boolean pTryProceed, Boolean pIsScripted)` — the right target, correcting `crucible-verb-feasibility.md`, which nominated `CombatHelper.PerformAbility` before parameter lists were available (that one needs a `Func\`4`, a `Func\`3` and a `SkillContext` the harness cannot synthesise) | **UNKNOWN** — nobody has shown how to construct a `CombatDecisionData` or a `Thing` from marshallable primitives. Spike SP-6. |

### E — Party (9 tools)

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| E1 | `ftk2_party` | `crucible_party()` | Who is in the party and what class each slot is. | `PartyManagementDirector._playerEntities`, `._playableCharacters`, `._selectedPartyIndex`, `._activePlayerIndex`, `._inLoadout`, `._activeAssignedEntities`, `._activeCharacterEntity`; `PartyManagementSyncData.{Players, AssignmentIndices, LoadoutIndicesForPlayers, InLoadout}` | CONFIRMED-API-ONLY |
| E2 | `ftk2_party_class` | `crucible_party_class(int pSlot, string pClassConfigName)` | **The 35-class test matrix's load-bearing verb.** | `PartyManagementDirector._rebuildCharactertAsNewConfigType(Entity, String pClassConfigName, Boolean pOnlineAction, Boolean pTryPlayReadyUp, Boolean pIsPreset)`; `._pOnChangeClassDelay(Entity, String)`; `._rebuildCharacterEntity(Entity, String)` | ASSUMED. **Serialize calls** — `._classChangeDelayToken` (`CancellationTokenSource`) + `._customizationLock` (`SemaphoreSlim`) mean two swaps in one frame cancel, not queue. Re-read the slot after each. |
| E3 | `ftk2_party_randomize` | `crucible_party_randomize(int pSlot)` | Randomize a slot. | `PartyManagementDirector._randomizeCharacterEntity(Entity, Boolean pOnlineAction)` | ASSUMED |
| E4 | `ftk2_party_remove` | `crucible_party_remove(int pIndex)` | Remove a slot. Already takes an `Int32`. | `PartyManagementDirector._removeCharacter(Int32 pIndex)`; `._onSelectCharacter(Int32)`; `._onChangeCharacter(Boolean)` | CONFIRMED-API-ONLY |
| E5 | `ftk2_set_level` | `crucible_set_level(int pEntity, int pLevel)` | Level a character to a target. | `CharacterHelper.TryProgressCharacterEntityToLevel`; `.ProgressCompanionEntityToLevel`; `.GetPlayerMaxLevel` | ASSUMED (needs B10 handle) |
| E6 | `ftk2_set_hp` | *(wraps shipped `SetPlayerHealth`)* | Set a player's HP. | shipped command; read-back `CharacterComponent.CurrentHealth`, `CharacterHelper.GetMaxHealth` / `.SetToMaxHealth` | CONFIRMED-API-ONLY (among the 21 measured at `MAIN_MENU`; never executed live) |
| E7 | `ftk2_give` | *(wraps shipped `GetSpecificThing`)* | Grant an item. | shipped command; read-back `CharacterComponent.Things` | CONFIRMED-API-ONLY |
| E8 | `ftk2_equip` | *(wraps shipped `EquipSpecificThing`)* | Equip an item. | shipped command; read-back `CharacterComponent.Equipped` | CONFIRMED-API-ONLY |
| E9 | `ftk2_set_stat` | *(wraps shipped `SetStat`)* | Set a stat. **Stat keys are strings — `eStats` does not exist in the assembly.** | shipped command; read-back `CharacterHelper.GetStat` over `CharacterComponent.OverrideStats` / `.BaseStatModifiers` | CONFIRMED-API-ONLY |

### F — World: overworld, dungeon, and the per-phase actions (12 tools)

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| F1 | `ftk2_overworld_end_turn` | `crucible_overworld_end_turn()` | End the overworld turn. | `AdventureDirector._doEndTurn()`; `._nextTurn(GameRandom, Boolean, Boolean)`; `._continueTurn(Boolean, GameRandom)` | CONFIRMED-API-ONLY |
| F2 | `ftk2_overworld_proceed` | `crucible_overworld_proceed(bool pForceEndTurn)` | Nudge the overworld forward when it is waiting. | `AdventureDirector._tryProceed(Boolean pForceEndTurn)` | CONFIRMED-API-ONLY |
| F3 | `ftk2_move` | `crucible_move(int pQ, int pR)` | Move the party to a hex. | `AdventureDirector._move(ValueTuple\`2 pGoal, Boolean, Boolean, Boolean)`; `._getAdventureMoveData(Entity, ValueTuple\`2, Boolean)` | **UNKNOWN** — the tuple's element types are ASSUMED `(Int32, Int32)` (uniform across ~10 sibling call sites) and `ValueTuple\`2` is not in the marshaller vocabulary. Spike SP-7. |
| F4 | `ftk2_stop_pick_hex` | `crucible_stop_pick_hex()` | Escape hex-pick mode — the overworld can sit waiting for a click that will never come from an unattended harness. | `AdventureDirector._onStopPickHex()` | CONFIRMED-API-ONLY |
| F5 | `ftk2_hexmap` | `crucible_hexmap()` | Read the map. | `Env.HexMap` (`prop List\`1[,]`); `Env.HexMaps`; `eHexTypes.{BOG, CLEARING, FOREST, RIVER, ROAD, WATER, BLOCKER, FEATURE, NONE, RAIL, …}`; `HexTemplateData.BaseTypeGroup`; `MapZoneData` | ASSUMED (members CONFIRMED; the 2-D list shape has never been walked) |
| F6 | `ftk2_time_of_day` | `crucible_time_of_day(int pIndex)` | Read/set time of day. **There is no "day" in this engine** — `GameCalendar`, `CalendarHelper` and `WorldState` are all NOT FOUND; time is the 4-stage `eTimesOfDay` cycle `DAWN → DAY → DUSK → NIGHT`. | `AdventureState.CurrentTimeOfDayIndex` / `.TimeOfDayTimeline` / `.TotalRoundCount`; `AdventureHelper.GetNextTimeOfDay` / `.GetIndexOfTimeOfDay` / `.GetTurnsToTimeOfDay` / `.GetRoundsToTimeOfDay` | ASSUMED — whether a direct index write fires the same side effects as natural passage is unverified |
| F7 | `ftk2_teleport` | `crucible_teleport(int pEntity, int pQ, int pR)` | Teleport the party. | `AdventureDirector._doTeleportAbility(Entity pCharacter, Thing pThingItem, Boolean pIsVoidwalk)` | **UNKNOWN** — takes an `Entity` *and* a `Thing` (the teleport-scroll item) and carries no destination argument; the destination presumably arrives from the UI hex-picker `._onSelectHexPositionTeleportScroll`. Spike SP-7. |
| F8 | `ftk2_dungeon` | `crucible_dungeon()` | Dungeon state. | `DungeonState.{FloorIndex, VenueIndex, CompletedRoomAmount, ConfigIndex, ConfigNames, ChosenDirection, DifficultyLevel, RoomHistory, UpcomingDirectionChoices, ForceEndDungeon, IsLoadingIntoDungeon, IsLoadingIntoRestPhase, HasInitializedPhase}`; `DungeonDirector._currentPhase` (`ePhases`) | CONFIRMED-API-ONLY |
| F9 | `ftk2_dungeon_next_phase` | `crucible_dungeon_next_phase()` | Walk the dungeon forward. `DungeonDirector` is the one gameplay owner **without** `_goToNextRoute`. | `DungeonDirector._nextPhase()`; `._tryProceed()`; `._nextFloorSection()`; `._loadNextPhase(List\`1)` | CONFIRMED-API-ONLY |
| F10 | `ftk2_dungeon_complete` | `crucible_dungeon_complete()` | Finish the dungeon. | `DungeonDirector._completeDungeon()`; `._resetDungeonLoop()`; `.ForceDeinitialize()` | CONFIRMED-API-ONLY |
| F11 | `ftk2_phase_action` | `crucible_phase_action(string pAction, int pArg)` | The per-phase action dispatcher, so seven phases do not need seven tools. | TRAP: `_passTrap(Boolean)`, `_disarmTrap`, `_bashTrap`, `_lockpickTrap`, `_runTrap`, `_selectAction(Entity, eTrapActions, Boolean)`, `_addFocus`. WHEEL: `_spinWheel(Boolean)`, `_passWheel(Boolean)`, `_continue(Boolean)`, `_navigateWheelToIndex(Int32, Boolean)`, `_replaceWheel(Int32, Boolean)`, `_restartWheelPhase()`. TREASURE: `_openTreasure(Entity, Int32, Boolean, Boolean, Boolean)`, `_passTreasure(List\`1, Boolean, Int32, Int32, Boolean)`, `_distributeTreasureLoot()`, `_startTreasurePhase()`. REST: `_endRestPhase()`, `_endRestPhase(eRoutes)`, `_tryEndRestPhase()`. ENCOUNTER: `_endEncounter()`, `_createEncounterAction(Entity, eEncounterActions, eEncounterActions, Int32, Thing)`. VENUE/INTRO: `_proceedVisualStack()` | CONFIRMED-API-ONLY for the `Boolean` / `Int32` / no-arg members; the `Entity` / `Thing` / `List\`1` members need B10 or are out of reach |
| F12 | `ftk2_goto_next_route` | `crucible_goto_next_route(string pRoute)` | Force the current owner to hand off. The universal in-flow "leave". | `<owner>._goToNextRoute(eRoutes)` on `CombatPhase` (param `pRoute`), `EncounterPhase`, `RestPhase`, `FortunePhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase`, `IntroPhase`, `VenueDirector` | CONFIRMED-API-ONLY |

### G — Quest and objective (5 tools)

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| G1 | `ftk2_objectives` | `crucible_objectives()` | Per-quest objective progress — **the "read the objectives" surface**, paired with A5 for the on-screen text. | `QuestState.CompletedObjectives` (`Boolean[]`), `.ObjectivesProgress`, `.ObjectiveDisplayOrder`, `.RewardsDistributed`, `.RoundsLeft`; `QuestData.Objectives` / `.ObjectiveTags` / `.ObjectiveCustomTexts` / `.CompleteObjectives`; `eQuestObjective` (the 30+ objective-verb enum); `eCompleteObjectives.{ALL, ALL_EXCEPT_DUMMY, ANY, FIRST, REPEAT}` | CONFIRMED-API-ONLY |
| G2 | `ftk2_objective_set` | `crucible_objective_set(int pQuest, int pObjective, bool pDone)` | **The objective-skip lever.** Write the public `Boolean[]` and let the game's own resolution pass notice. | `QuestState.CompletedObjectives[i] = true` (public field) | ASSUMED — the field is confirmed public and settable; that `_tryCompleteQuests` / `_resolveQuests` pick up an externally-set flag has never been observed. Spike SP-8. |
| G3 | `ftk2_try_complete_quests` | `crucible_try_complete_quests()` | Force the resolution pass, so G2 takes effect now rather than next tick. | `AdventureDirector._tryCompleteQuests()`; `._resolveQuests(List\`1, List\`1)`; `CombatPhase._tryCompleteQuests()`; `EncounterPhase._tryCompleteQuests()`; `QuestHelper.GetCompletedQuests(…)` for read-back | CONFIRMED-API-ONLY |
| G4 | `ftk2_close_quest` | `crucible_close_quest(int pQuest, bool pFailed)` | Close a quest outright. | `QuestHelper.CloseQuest(QuestState, List\`1 pPlayers, GameRunData, List\`1 pEntitiesToRefresh, Boolean pAddToFailed, String pMapID)` | **UNKNOWN** — six arguments including two `List\`1` the harness would have to assemble. G2 + G3 is the cheaper path. |
| G5 | `ftk2_try_victory_loss` | `crucible_try_victory_loss()` | Force the win/lose check. | `AdventureDirector._tryVictoryLoss()`; `RestPhase._tryVictoryLoss()`; `<owner>._tryEndAdventureFromDungeon()`; config-side `QuestData.AdventureEndTrigger` (`eAdventureEndTriggers.{NONE, WIN, LOSE}`) | CONFIRMED-API-ONLY |

### H — RNG and determinism (6 tools)

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| H1 | `ftk2_pin_seed` | `crucible_pin_seed(int pSeed, string pScope)` | Pin the RNG. **Every phase holds its own `GameRandom`**, so "pin" means writing `.Seed` on each live instance. | `GameRandom.Seed` (`Int32` field); `.GetNewRandomSeed`; instances at `CombatState.Random`, `CombatPhase._gameRandom`, `AdventureDirector._gameRandom`, `EncounterPhase._gameRandom`, `MainMenuDirector._gameRandom` | ASSUMED (field CONFIRMED; whether a mid-stream reseed is honoured is not) |
| H2 | `ftk2_rng` | `crucible_rng()` | Gameplay vs visual draw counters — the S5 signal and the scenario determinism check. | Harmony counters on **Tier A primitives only**: `NextBool`, `NextChance` ×2, `NextDecimal` ×2, `NextInt` ×2, `NextIntAscendingWeight`, `NextIntDescendingWeight`, `NextNormalizedDecimal`, `NextNormalizedFloatDistributed`, `NextOffset3`, `NextPointInSphere`, `NextPointInUnitSphere` ×2, `NextVector3`; visual: `NextChanceVisual`, `NextFloatVisual` ×2, `NextNormalizedFloatVisual`. Plus `GameRandom.NextCount`, `.RecordDebugInfo`, `.LogCalls` | CONFIRMED-API-ONLY. **Do not patch Tier B** (`GetRandomElementFromArray` / `FromList` / `FromWeightedList`, `GetRandomKeyFromDictionary`, `GetRandomValueFromDictionary`, `ShuffleList`) — they are likely composites over Tier A and would double-count. |
| H3 | `ftk2_force_roll` | *(wraps shipped `SetSlotRollResult`)* | Pin a roll tier. | shipped command | CONFIRMED-API-ONLY |
| H4 | `ftk2_set_tarot` | *(wraps shipped `SetTarot`)* | Pin a tarot draw. | shipped command | CONFIRMED-API-ONLY |
| H5 | `ftk2_rig_wheel` | `crucible_rig_wheel(int pWedgeIndex)` | Rig the wheel — the engine ships a first-class hook for it. | `WheelPhase._debugSetWheelSelection` (`Boolean` field); `._spinRigging` (`List\`1` field); `._navigateWheelToIndex(Int32, Boolean)` | ASSUMED |
| H6 | `ftk2_map_seed` | `crucible_map_seed(int pSeed)` | Pin map generation, separately from combat RNG. | `PartyManagementSyncData.CustomMapGenSeed` (`Int32`); `NetworkData.MapGenSeed`; `NetworkData.RandomSeedOnLastSave`; `GameSaveData.mapGenSeed` | ASSUMED |

### I — Speedrun skips (8 tools)

These exist because clicking is too slow or too blind. Each one buys a whole class of game time.

| # | MCP tool | Console cmd + params | What it skips | Underlying API | Status |
|---|---|---|---|---|---|
| I1 | `ftk2_skip_phase` | *(alias of D2 in skip mode)* | Any single phase. | see D2 | CONFIRMED-API-ONLY |
| I2 | `ftk2_god_mode` | `crucible_god_mode(bool pAllCharacters)` | Losing. Makes every fight survivable so a run can be walked to the end. | `CombatPhase._debugSetPlayersTo9999HP()`; `._debugSetAllCharactersTo9999HP()` | CONFIRMED-API-ONLY |
| I3 | `ftk2_win_now` | *(alias of C8)* | The rest of the campaign. | `<owner>._endAdventure(Boolean pIsVictory)` | CONFIRMED-API-ONLY |
| I4 | `ftk2_dungeon_force_end` | `crucible_dungeon_force_end()` | A whole dungeon — the longest wedge risk in the game, and the one place `QuitToMenu()` can be refused. | `DungeonState.ForceEndDungeon` (field write), then `DungeonDirector._nextPhase()` | ASSUMED (field CONFIRMED; that setting it is honoured is not) |
| I5 | `ftk2_complete_all_objectives` | `crucible_complete_all_objectives()` | Every objective of every active quest, then forces the resolution pass. | Loop `GameRunData.ActiveQuests` → `QuestState.CompletedObjectives[*] = true` → `AdventureDirector._tryCompleteQuests()` | ASSUMED (composition of G2 + G3) |
| I6 | `ftk2_skip_outro` | `crucible_skip_outro(bool pValue)` | The outro cinematic — a route with **no owning type**, and therefore a plausible place for an unattended run to sit forever. | `AppConfigManager+AppConfig.SkipOutroCinematic` (`Boolean` field) | ASSUMED — whether it is read at route time is inference. **A2 (click) is the only other candidate.** |
| I7 | `ftk2_merc_deeds` | `crucible_merc_deeds()` | Grinding for mercenary deeds. | `RestPhase._debugGiveAllMercDeeds()` | CONFIRMED-API-ONLY |
| I8 | `ftk2_autopilot` | `crucible_autopilot(string pGoal, int pBudgetMs)` | **The owner's definition of done.** Loop: read A7 → if wedged, A10 → else ask C10 where to go → act with D2 / F9 / F12 → checkpoint C7 → until `pGoal` (`victory`, `route:<X>`, `quest:<id>`) or budget. | Composition only; every leaf is a row above | ASSUMED |

### J — Diagnostics (8 tools)

| # | MCP tool | What it does | Status |
|---|---|---|
| J1 | `ftk2_list_instances` | List configured peers and reachability. | CONFIRMED-LIVE |
| J2 | `ftk2_list_commands` | Enumerate the live registry. **Re-read after every route change — never cache.** The registry's contents are a function of route (2 commands at `NONE`, ~21 at `MAIN_MENU`, measured) because each phase registers on entry and calls `_unregisterCommonConsoleCommands()` on exit. | CONFIRMED-LIVE |
| J3 | `ftk2_exec` | Execute any registered command by name. | CONFIRMED-LIVE |
| J4 | `ftk2_read_trace` | Read the session JSONL trace. | CONFIRMED-LIVE |
| J5 | `ftk2_screenshot` | Capture the window. **Requires an unminimized rendering window**, or the images are black. | CONFIRMED-LIVE |
| J6 | `ftk2_compare_state` | Desync oracle across instances. | CONFIRMED-LIVE |
| J7 | `ftk2_wedge_report` | The full wedge table from `crucible-traversal-inventory.md` §5 as one read: the A8 oracles plus `DungeonDirector._canQuitToMenu`, `WheelPhase._isWheelSpun`, `TreasurePhase._isMimic`, `TrapPhase._isTrapCompleted`, `AdventureSelectionDirector._decommissioned`, `_areDebugCommandsAllowed`, and `GameBridge.IsOnlineSession()`. | CONFIRMED-API-ONLY |
| J8 | `ftk2_owner` | Which Director/Phase owns the live route, resolved from the `RouterMono` private fields (`_adventureDirector`, `_combatPhase`, … `_currentDirector`) — so every other tool can say *why* it dispatched where it did. | CONFIRMED-API-ONLY |

### K — Game-state generation: fixtures and per-feature state (10 tools)

Design rationale in §5. Rows marked *host-side* touch no game member at all — they read and copy files
next to the harness — so the only risk they carry is our own code, not a wrong game name.

| # | MCP tool | Console cmd + params | What it does | Underlying API | Status |
|---|---|---|---|---|---|
| K1 | `ftk2_save_user` | *(wraps shipped `saveUser`)* | **The save trigger.** A shipped console command, observed present in the live registry 2026-08-23. | `CommandLineHelper` shipped command `saveUser`; post-condition read via B7 (`Env.GameRuns` gains a run id) and a new directory under `GameRuns\` | **CONFIRMED-LIVE** (present in the registry). The post-condition — that it writes a loadable `.ftk2` for the *current* position — is ASSUMED and is spike SP-10. |
| K2 | `ftk2_fixture_capture` | *(host-side + K1)* | Tier-1 capture: with the game already driven to the target position, call K1, wait for the new run id, copy `GameRuns\<runID>\` into `FTK2.Crucible/data/Fixtures/<fixtureId>/`, and write the manifest (§5.3). | K1; B7; B8; A7; BCL file copy | ASSUMED (composition) |
| K3 | `ftk2_fixture_list` | *(host-side)* | Every fixture in the repo with its manifest and a fresh/stale verdict. | none — reads `data/Fixtures/*/fixture.json` | CONFIRMED-API-ONLY *(host-side)* |
| K4 | `ftk2_fixture_verify` | `ftk2_fixture_verify(fixtureId)` | **The staleness gate, and it refuses loudly.** Compares the manifest's `gameVersion`, `dataHash` and declared content ids against the live game; any mismatch is a hard refusal, never a warning. | `UserData.LastPlayedVersionString` (`1.14.6` observed live 2026-08-23); the ClassForge pack `dataHash` (`sha256:b7e5e871…` observed in today's log); id resolution through `Env.Configs` | ASSUMED (each input is real; the comparison is new code — negative control mandatory, §5.4) |
| K5 | `ftk2_fixture_load` | `ftk2_fixture_load(fixtureId)` | K10 → K4 → copy the fixture into the **scratch** `GameRuns\` → C3 → assert with A7 + B8. Refuses if K4 or K10 refuses. | K10, K4, C3, A7, B8 | ASSUMED |
| K6 | `ftk2_fixture_restore` | *(host-side)* | Teardown: remove copied fixture dirs and restore the real save folder. Idempotent, safe to run twice. | none — file ops + the marker-file protocol (spec §9) | CONFIRMED-API-ONLY *(host-side)* |
| K7 | `ftk2_apply_state` | *(MCP-side; expands to verb calls)* | Tier-2: apply a named per-feature state — a readable JSON list of verb invocations (`party_class`, `set_stat`, `set_level`, `give`, `equip`, `pin_seed`, `force_roll`, `objective_set`, `god_mode`) — on top of a loaded fixture. **Not a console command**: the marshaller cannot take a JSON blob, so expansion happens in `ScenarioRunner` and each step is an ordinary verb call with its own post-condition. | E2, E5, E7, E8, E9, H1, H3, H5, G2, I2 | ASSUMED |
| K8 | `ftk2_state_snapshot` | `ftk2_state_snapshot(featureId)` | **Persist a Tier-2 state as its own save**, so a per-feature fixture exists on disk when the owner wants one. K1 + K2 with a manifest that additionally records the base fixture id and the exact applied verb list. | K1, K2, K7's recorded step list | ASSUMED |
| K9 | `ftk2_fixture_regen` | `ftk2_fixture_regen(fixtureId)` | **Rebuild a stale fixture without a manual play session.** Replays the manifest's `recipe` (the recorded traversal script that produced it) from `MAIN_MENU`, then re-captures via K2 and rewrites the manifest. | The `recipe` replayed through C2, C4, C6, C1, D2, F9, F12, A6/B11 waits; then K2 | ASSUMED |
| K10 | `ftk2_saves_guard` | *(host-side)* | **Refuses to proceed unless the live save folder is the per-run scratch junction, not the owner's real `GameRuns\`.** Called before every write in this category. | none — path identity + marker-file check | CONFIRMED-API-ONLY *(host-side)* |

---

## 3. Coverage matrix over all 21 `eRoutes` values

Columns: **Enter** = can the harness get there. **Detect** = can it prove it arrived. **Act** = can it
do the route's work. **Leave** = can it get out.

`in-flow` means the game routes there itself during a run and the harness can act once there, but a
cold `Route(<x>, …)` from an arbitrary state is not supported by any evidence — every gameplay
`Initialize` needs a `Scene`, `Diorama`, `VenueState` or `PartyManagementSyncData` that only the
previous route produces, and `Route`'s `Object pCustomData` is refused by anything but `null` in the
current `ArgCoercion`. **Traversal is a graph walk, not teleportation.**

| Route | Enter | Detect | Act | Leave | Verdict |
|---|---|---|---|---|---|
| `NONE` | n/a — pre-boot | A7 / B6 (`route != NONE`) | — | boot completes | **Detect-only by design.** Indistinguishable from a harness bug; assert Steam is running before blaming Crucible. |
| `INTRO` | C1 (ASSUMED — lightest `Initialize` in the game) | A7 | F11 (`_proceedVisualStack`), F12 | F12 | in-flow, never exercised |
| `EXIT` | **deny-listed** | — | — | — | **Excluded by design.** `Route(EXIT, …)` terminates the process. |
| `MAIN_MENU` | C1, C9 | A7, J2 (~21 commands) | C4, C5, A2 | C1 | **FULL** |
| `ADVENTURE_SELECTION` | C1 | A7 | C3, C4, C5, A2 | C1, C9 | **FULL** |
| `LORE_STORE` | C1 | A7 | A2 (`_bindBackButton`, `_showRedeemDialog` are UI-shaped) | C1, A2 | **FULL** (via the UI layer) |
| `MULTIPLAYER_LOBBY` | C1 | A7, A8 | C2, A2 | C2 → C9 → C1, A10 | **FULL** — and the route to steer *away* from |
| `PARTY_MANAGEMENT` | in-flow (from `ADVENTURE_SELECTION`; `Initialize` needs a `PartyManagementSyncData`) | A7, E1 | E1–E4, C6 | C6, C9 | in-flow |
| `ADVENTURE` | in-flow (C3 or C6) | A7, B8 | F1, F2, F4, G1–G3, G5 | C8, C9 | in-flow. **F3 `move` and F7 `teleport` are UNKNOWN** — the overworld can be turned but not steered. |
| `VENUE` | in-flow | A7, `RouterHelper.IsInVenue()` | F11 (`_proceedVisualStack`), F12 | F12 | in-flow |
| `DUNGEON` | in-flow | A7, F8 | F9, F10 | F10, I4 — **C9 can be refused** (`_canQuitToMenu`) | in-flow, **weakest leave** |
| `COMBAT` | in-flow | A7, B3, D3 | D2, D4–D9, E6, I2 | D7, F12 | in-flow. **D11 `use_ability` UNKNOWN** — combat can be skipped but not *played*. |
| `ENCOUNTER` | in-flow | A7 | D2, F11 (`_endEncounter`) | F12 | in-flow |
| `REST` | in-flow | A7 | D2 (**`_debugEndPhase(Int32)`**), C7, F11, I7 | F11, F12 | in-flow |
| `TREASURE` | in-flow | A7 | D2, F11 | F12 | in-flow; `_isMimic` can turn it into combat mid-phase |
| `TRAP` | in-flow | A7 | D2, F11 (`_passTrap(false)`) | F12 | in-flow |
| `WHEEL` | in-flow | A7 | D2, F11, H5 | F12 | in-flow |
| `FORTUNE` | in-flow | A7 | D2 | F12 | in-flow; the thinnest phase surface |
| `CINEMATIC_INTRO` | **none found** | A1 / A5 only (ASSUMED) | A2 / A3 only (ASSUMED) | **none found** | **NOT COVERED** |
| `CINEMATIC_OUTRO` | reached by `_endAdventure` (ASSUMED) | A1 / A5 only | A2 / A3, I6 | **none found** | **NOT COVERED** |
| `CINEMATIC_CREDITS` | **none found** | A1 / A5 only | A2 / A3 only | **none found** | **NOT COVERED** |

**Tally: 4 routes fully covered** (`MAIN_MENU`, `ADVENTURE_SELECTION`, `LORE_STORE`,
`MULTIPLAYER_LOBBY` — cold-enterable, detectable, actionable, leavable). **12 covered in-flow**
(`INTRO`, `PARTY_MANAGEMENT`, `ADVENTURE`, `VENUE`, `DUNGEON`, `COMBAT`, `ENCOUNTER`, `REST`,
`TREASURE`, `TRAP`, `WHEEL`, `FORTUNE`) — reachable only by walking the run forward, which is exactly
what an autopilot does, so this is a limitation on *scenario setup*, not on *playing*. **5 not
covered**: `NONE` and `EXIT` deliberately, and the three `CINEMATIC_*` routes because **no owning type
exists in the assembly under any name probed**.

**The cinematic gap, stated plainly.** `CinematicDirector`, `CinematicPhase`, `CinematicHelper` and
`VideoDirector` are all NOT FOUND among the 5389 loaded types. `RouterMono` has no cinematic field.
I cannot name an entry, an exit, or an arrival proof for those three routes. Two things reduce the
risk without closing it: `AppConfig.SkipOutroCinematic` might remove the outro entirely (I6, ASSUMED),
and the UI layer (A1 / A2 / A5) needs no owning type at all — if a cinematic renders a Skip button
into a `UIDocument`, A1 will see it and A2 can press it. **Both are guesses until someone runs a
campaign to its end and dumps the tree.** `RouterHelper.GetEndOfAdventureRoute(GameRunData)` (C10) is
the cheap read-only way to learn whether a finishing campaign even passes through one, and it should
be among the first things the harness calls.

---

## 4. The cross-cutting blocker: entity handles

Ten tools (B10, D6, D10, D11, E2, E3, E5, F3, F7, and parts of F11) want to name a specific `Entity`,
`Thing`, or hex. The marshaller accepts `int / float / double / bool / string / Vector2 / Vector3` and
nothing else. `Entity`, `Thing`, `CombatDecisionData`, `EncounterGenData` and `ValueTuple\`2` are all
outside it.

**The proposed scheme (ASSUMED — this catalogue's own design, no evidence backs it yet).**
`ftk2_entities` (B10) publishes a stable table for the current scope, keyed by a small `int`:

```
0  6f2a...  "Rosalind"  CF_EOR_BARD    player
1  91b4...  "Bandit"    CHR_BANDIT_01  enemy
```

Every entity-taking verb then declares `int pEntity` and resolves it through that table by
`Entity.Guid` (`prop | String | Guid` — CONFIRMED). `Vector2` *is* in the vocabulary and is the
natural carrier for a hex `(q, r)`, which may rescue F3 — but only if the `ValueTuple\`2` really is
`(Int32, Int32)`, which is ASSUMED from ~10 uniform sibling call sites and confirmed by none of them.

**This is the single highest-leverage unknown in the catalogue.** Resolve it and D6 / D10 / E2 / E3 /
E5 firm up together; leave it and combat is skippable but not playable.

---

## 5. Game-state generation — the two-tier system

Every feature under test needs a game state to test it *in*. Two tiers, because the two problems are
different: getting to a hard-to-reach **position** is slow and can only be done by playing; setting up
the **specifics** of a test is fast and should be readable in the scenario file.

```
  Tier 1 — BASE FIXTURE                     Tier 2 — PER-FEATURE STATE
  a .ftk2 save, produced by the game        a JSON verb list, applied on load
  answers "where am I in the run"           answers "what exactly is set up"
  ~8 of them, change only on game update    one per feature, changes constantly
  K1 K2 K3 K4 K5 K6 K9 K10                  K7, and K8 to persist one as a save
```

### 5.1 Tier 1 — base fixtures

A small set of saves capturing positions the route graph makes expensive to reach. The catalogue's
§3 matrix is the reason this tier must exist at all: **12 of 21 routes are `in-flow` only** — there is
no cold `Route(COMBAT, …)`, so the only way to be *in* combat is to have played there. A fixture is a
recording of having played there.

The proposed base set — eight, one per hard position:

| Fixture id | Position it captures | Why it is expensive to reach live |
|---|---|---|
| `main-menu-clean` | `MAIN_MENU`, no auto-join pending | Proves the boot escape (A10) worked; the cheapest possible baseline |
| `party-fresh` | `PARTY_MANAGEMENT`, campaign chosen, difficulty set | Gate for the 35-class matrix (E2) |
| `overworld-early` | `ADVENTURE`, round ~3, party alive | Every overworld and quest test starts here |
| `overworld-mid` | `ADVENTURE`, mid-campaign, chaos advanced | Late-game content is otherwise hours away |
| `combat-round-1` | `COMBAT`, round 1, both sides at full HP | The single most-used fixture — every combat, ability and status test |
| `combat-multi-wave` | `COMBAT`, `WaveIndex > 0` | The `TotalRounds`-resets-to-`-1` case that breaks naive round assertions |
| `rest-site` | `REST` | The natural save/checkpoint phase (C7), and `_debugEndPhase(Int32)`'s odd-one-out signature |
| `dungeon-floor-1` | `DUNGEON`, one room completed | The weakest-leave route; also where `QuitToMenu()` can be refused |

**Creation (K2).** Drive the game to the position with the tools in A/C/D/F, then `saveUser` (K1),
then copy the resulting `GameRuns\<runID>\` directory into `FTK2.Crucible/data/Fixtures/<id>/` and
write the manifest. Fixtures are **committed to the repo**, not left in `LocalLow`.

**Storage and isolation.** Saves live under
`%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\GameRuns\` — **which is also where the
owner's real co-op saves live.** Copying the game install does not isolate this: the Unity save path
is identity-derived (`app.info` = `IronOak Games` / `For The King II`), so both copies write to the
same folder (spec §0). Fixtures therefore live in the repo and are copied *in* per run, behind the
junction swap from spec §9, and **K10 refuses every write unless the live folder is the scratch
junction.** A soak must never write next to his real saves. Backup:
`C:\Users\ben\Backups\ftk2-2026-08-23\`.

**Restore (K5).** Guard (K10) → verify freshness (K4) → copy the fixture dir into the scratch
`GameRuns\` → `crucible_load_run` (C3) → assert arrival with A7 + B8. Teardown is K6, idempotent.

**The constraint that shapes all of this: `.ftk2` files are obfuscated on disk** — a UTF-8 BOM
followed by control bytes; not JSON, not gzip, not protobuf. Consequences, and they are not small:

- Fixtures **cannot be authored or edited programmatically.** Only the running game can produce one.
- A fixture's contents **cannot be verified except by loading it.** There is no offline validator.
- So the manifest (§5.3) is the *only* metadata anyone can check cheaply, which is exactly why it
  must be complete and why the staleness gate must be a refusal rather than a warning.
- And the acceptance test for a fixture is "load it and assert the position", never "parse it".

### 5.2 Tier 2 — per-feature state

Generated at test time by loading a base fixture and applying verbs. Expressed as readable JSON in the
scenario, so a reviewer can see what the test set up without opening a binary:

```jsonc
{
  "state": "bard-crescendo-ready",
  "base":  "combat-round-1",
  "apply": [
    { "do": "party_class",   "args": { "slot": 0, "classConfigName": "CF_EOR_BARD" } },
    { "do": "set_level",     "args": { "entity": 0, "level": 5 } },
    { "do": "give",          "args": { "entity": 0, "thingId": "<a TAL-stat weapon>" } },
    { "do": "pin_seed",      "args": { "seed": 12345, "scope": "all" } },
    { "do": "force_roll",    "args": { "tier": "SUCCESS" } },
    { "do": "objective_set", "args": { "quest": 0, "objective": 0, "done": false } }
  ]
}
```

`ftk2_apply_state` (K7) expands this in `ScenarioRunner` — **it is not a console command**, because
the marshaller takes discrete typed parameters and cannot accept a JSON blob. Each step is an ordinary
verb call and reports its own post-condition, so a failed setup step fails the scenario rather than
silently producing a differently-configured test.

**Persisting a Tier-2 state (K8).** The owner explicitly wants per-feature saves available, not only
verb-built state. `ftk2_state_snapshot <featureId>` runs K1 + K2 against the *current* state and
writes a manifest that additionally records `base` and the exact `apply` list that produced it. The
result is a Tier-1-shaped fixture with a Tier-2 provenance record — which is what makes K9
regeneration possible for it too.

### 5.3 The fixture manifest, and the staleness rule

`FTK2.Crucible/data/Fixtures/<id>/fixture.json`, committed beside the save directory:

```jsonc
{
  "id": "combat-round-1",
  "createdUtc": "2026-08-23T18:04:11Z",
  "gameVersion": "1.14.6",                    // UserData.LastPlayedVersionString, read live at capture
  "dataHash": "sha256:b7e5e871…",             // the ClassForge pack dataHash at capture time
  "runId": "<GameSaveData.runID>",
  "saveName": "<GameSaveData.saveName>",
  "manualId": "<GameSaveData.manualId>",
  "roundCount": 1,
  "difficulty": "JOURNEYMAN",                 // GameSaveData.difficulty (eGameDifficulties)
  "mapGenSeed": 12345,                        // GameSaveData.mapGenSeed
  "chaosState": "<GameSaveData.chaosState>",
  "expansions": ["<GameSaveData.expansions>"],
  "characters": ["<GameSaveData.characters>"],
  "position": { "route": "COMBAT", "wave": 0 },   // asserted on load via A7 + D3
  "dependsOn": {                                  // every content id the fixture references
    "classes":  ["CF_EOR_BARD", "CF_EOR_WARDEN"],
    "things":   ["…"],
    "statuses": ["…"],
    "quests":   ["…"]
  },
  "base":  null,                              // Tier-2 snapshots name their base fixture here
  "apply": [],                                // and the verb list that produced them
  "recipe": [                                 // how to rebuild it — see §5.5
    { "do": "escape" },
    { "do": "new_run", "args": { "adventureId": "…", "difficulty": "JOURNEYMAN" } },
    { "do": "party_class", "args": { "slot": 0, "classConfigName": "CF_EOR_BARD" } },
    { "do": "party_begin" },
    { "wait_for": { "path": "screen.route", "equals": "ADVENTURE", "timeoutMs": 120000 } },
    { "do": "advance", "args": { "mode": "auto" }, "until": { "path": "screen.route", "equals": "COMBAT" } }
  ]
}
```

**The rule: `ftk2_fixture_verify` (K4) REFUSES, it does not warn.** A fixture is stale — and the
harness must stop, loudly, naming which check failed — if any of:

1. `gameVersion` != `UserData.LastPlayedVersionString` live.
2. `dataHash` != the live ClassForge pack `dataHash`.
3. Any id in `dependsOn` fails to resolve in the live configs.
4. `position.route` does not match `A7` after loading.

**Why this is the central design risk.** A fixture references content ids. Rename a trait, retune a
recipe, redeploy a pack — and the fixture is silently *wrong*, producing a green test against content
that no longer exists. That is worse than a red test: it is the `NetworkData.PlayerCount` failure
again, in a new place, with the same shape (a plausible-looking value, confidently served, wrong).
The obfuscated save format removes every cheaper way to notice, so the manifest gate is the only
defence there is.

### 5.4 Negative control for the staleness gate

A lint that cannot fail reports green forever. The gate ships with a fixture that deliberately
violates it: `data/Fixtures/_stale-control/fixture.json` carries `gameVersion: "0.0.0-stale"`, a
`dataHash` of `sha256:0000…`, and a `dependsOn.classes` entry of `CF_DOES_NOT_EXIST`. The test asserts
that K4 **refuses it and names all three failing checks**, and — the half that actually matters —
that K4 **accepts** a fixture whose manifest matches the live values. A gate that refuses everything
is as useless as one that refuses nothing.

### 5.5 Regeneration — eight fixtures must not mean eight play sessions

Every manifest carries a `recipe`: the traversal script that produced the fixture, recorded at capture
time as the ordered list of verb calls the harness actually issued. `ftk2_fixture_regen` (K9) replays
it from `MAIN_MENU` and re-captures. Regenerating the whole base set after a content change is then
one command in a loop, not a day of manual play.

**Honest limits on this.** The recipe is only as replayable as the verbs it contains, and three
things can break it: the run is seeded (`mapGenSeed` is pinned in the manifest and re-applied via H6,
which is ASSUMED to be honoured); `advance`-until steps depend on `_areDebugCommandsAllowed`, which
nothing has confirmed the setter for; and any recipe step that would need `move` (F3) or `use_ability`
(D11) cannot be recorded at all, because those tools are UNKNOWN. **So `combat-round-1` is
regenerable only if walking into a fight can be done with `advance` alone** — which is the point of
spike SP-11. If it cannot, that fixture stays manual, and the manifest should say so with a
`regenerable: false` flag rather than pretend.

---

## 6. Counts

| Status | Count | Where |
|---|---|---|
| **CONFIRMED-LIVE** | 15 | A6, B1, B2, B4, B5, B7 (the run-id list), B11, C1, D1, J1–J6, K1 |
| **CONFIRMED-API-ONLY** | 56 | the bulk of A, B, C, D, E, F, G, H, I, J, plus host-side K3 / K6 / K10 |
| **ASSUMED** | 25 | A10, B10, C6, D6, D8, D10, E2, E3, E5, F5, F6, G2, H1, H5, H6, I4, I5, I6, I8, K2, K4, K5, K7, K8, K9 |
| **UNKNOWN** | 4 | D11 `use_ability`, F3 `move`, F7 `teleport`, G4 `close_quest` |
| **Total** | **100** | |

Two rows carry an extra warning that no status label captures:

- **A9 `ftk2_dismiss`** — every signature is CONFIRMED, and `MultiplayerViewHelper.HideAllMenus()`
  **was called live against the P0-16 modal and did not close it.** The row stays as a fallback,
  demoted below A2 (click the Close button).
- **C1 `ftk2_route`** — CONFIRMED-LIVE that it *dispatches*, and CONFIRMED-LIVE that dispatching
  **does not repaint the screen**. It is a real tool with a false post-condition; A7 is its
  post-condition.

---

## 7. What this catalogue does not cover — named, not papered over

1. **The three `CINEMATIC_*` routes.** No owning type exists. §3 states the two guesses and marks both
   ASSUMED.
2. **Playing combat, as opposed to skipping it.** `use_ability` (D11) is UNKNOWN. The harness can end
   a fight five different ways and cannot cast a spell.
3. **Steering the overworld.** `move` (F3) and `teleport` (F7) are UNKNOWN. The party can end turns; it
   cannot be sent anywhere.
4. **Cold entry into any gameplay route.** Nothing confirms what `Route`'s `Object pCustomData` must
   carry per route, and `ArgCoercion` refuses anything but `null` for `Object`. Scenario setup must
   walk the graph.
5. **What sets `_areDebugCommandsAllowed`.** `AppConfig.BlockNetworkUnsafeCheats` is the obvious
   candidate and is unconfirmed. If it is false on some route, D2 and half of I stop working there —
   and `ftk2_list_commands` (J2) is the only way to find out, per route, live.
6. **The registered *names* of the shipped per-phase console commands.** `_registerConsoleCommands()`
   bodies are invisible to TypeProbe. Only the ~21 present at `MAIN_MENU` have been counted, and only
   six of them (`ToggleUI`, `ToggleVenueGrid`, `TogglePlayerHUDs`, `PrintDungeonConfigHash`,
   `PrintDialogueConfigHash`, `EnableGameRandomStackTraceRecording`) are named anywhere in this repo's
   code. Every wrapper row in E and H is therefore CONFIRMED-API-ONLY at best.
7. **Status stacking.** `StatusEffectInfo` has no stacks field; stacking *appears* to be encoded as
   suffixed ids. B3 deliberately omits a derived `stacks` rather than invent one.
8. **`combat.turn` and intra-combat `phase`.** Genuinely absent from the assembly, not misnamed. B3
   synthesizes both from Harmony hooks and must label them as synthesized.
9. **Two-peer anything.** A second instance cannot run on one machine (spec §0). J6
   `ftk2_compare_state` is real, and proving it requires two machines.
10. **Reaching a `GameSaveData` at all.** `Env.GameRuns` is a `List<String>` of run ids, VERIFIED LIVE
    — `crucible-load-path.md`'s assumption that it was `List<GameSaveData>` was wrong. Every
    `GameSaveData` field in the K5 manifest (`saveName`, `characters`, `chaosState`, `mapGenSeed`, …)
    is CONFIRMED to exist on the type and **not yet confirmed reachable at runtime**. Spike SP-9.
    Until it lands, a manifest can be written from what the harness itself knows (route, round, the
    verbs it applied) but not from the save's own metadata.
11. **What `saveUser` actually writes.** It is present in the live registry; that it saves the
    *current* position in a loadable form is ASSUMED. Spike SP-10. And because `.ftk2` is obfuscated,
    the only possible proof is a load-and-assert round trip.
12. **Whether every base fixture is regenerable.** §5.5 — a fixture whose recipe would need `move`
    (F3) or `use_ability` (D11) cannot be scripted at all today.
