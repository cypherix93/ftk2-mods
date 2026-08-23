# Quest / Objective Progression & Adventure Completion — TypeProbe Findings

Probed with:
`dotnet run --project C:/Users/ben/repos/ftk2-wt-probe2/FTK2.DevKit/sandbox/TypeProbe -c Release -- <Type> --methods`

All members below are copied verbatim from TypeProbe output. Anything not printed by TypeProbe is
explicitly marked NOT FOUND or NOT PROBED — nothing here is inferred from FTK2.Questsmith/SPEC.md
except where labeled ASSUMED (that doc is treated as unverified vocabulary, not evidence).

## 1. What models a quest/objective

Probed and FOUND:

- **`QuestData`** — the static/config definition of a quest (fields only, no methods beyond object
  boilerplate):
  `ID`, `QuestType (eQuests)`, `Objectives (String[])`, `ObjectiveTags (String[])`,
  `ObjectiveCustomTexts (String[])`, `ObjectiveCameraActions (String[])`,
  `ObjectiveEngageWorldTriggers (String[])`, `ObjectiveCompleteWorldTriggers (String[])`,
  `CompleteObjectives (eCompleteObjectives)`, `ShuffleObjectives (Boolean)`,
  `AdventureEndTrigger (eAdventureEndTriggers)`, `NextQuests (String[])`, `Conflicts (String[])`,
  `Requirements (String[])`, `Rewards (String[])`, `RoundsToExpire (Int32)`,
  `QuestStartWorldTriggers/QuestEndWorldTriggers (String[])`, `Tags (String[])`, `GroupID`,
  `SideQuestType (eSideQuestTypes)`, plus dialogue/reward-display flags.
- **`QuestState`** — the live, per-run instance of a quest:
  `Data (QuestData)`, `CompletedObjectives (Boolean[])`, `ObjectivesProgress (List\`1)`,
  `ObjectiveDisplayOrder (Int32[])`, `RewardsDistributed (Boolean[])`, `RoundsLeft (Int32)`,
  `Initialized (Boolean)`, `Hidden (Boolean)`, `MapID (String)`, `PreviousQuest (QuestState)`,
  `PreviousQuestID (String)`. No methods of its own (pure data + object boilerplate).
- **`QuestHelper`** — the static logic surface (full method list captured; key ones in §2/§4 below).
- **`eQuestObjective`** — the objective-verb vocabulary as an enum (not string-driven at the engine
  level; Questsmith's SPEC.md described a string-verb registry layered on top of this, unverified here):
  `ACTIVATE_ENTITY, ASSASSINATE_ENTITY, AUTO_COMPLETE, CHAOS_HISTORY_CONTAINS, COMBAT_HAS_ENEMY,
  COMBAT_START, COMBAT_WAVE_START, DELIVER, DELIVER_TO_TYPE, DESTROY_ENCOUNTER, DISABLE_MAP_SPAWNER,
  DUMMY, ENCOUNTER_HAS_HP, ENTITY_REACH_HEX, ENTITY_REACH_RADIUS_*, ENTITY_REACH_ZONE, GAME_MODIFIER,
  HAS_PROPERTY, HAS_THING, MAP_SPAWNER_KILLS, MAP_SPAWNER_TURNS, NOT_HAS_PROPERTY, QUEST_COMPLETE,
  REACH_HEX, REACH_HEX_IGNORE_NULL, REACH_PARENT_ZONE, REACH_RADIUS_*, REACH_ZONE, REMOVE_ENCOUNTER,
  REMOVE_ENCOUNTER_IGNORE_NULL, REMOVE_ENTITY, REVEAL_ENTITY, ROUNDS_COMPLETED, SCOURGE_MODIFIER,
  SURVIVE_ENTITY, VISIT_DUNGEON_ENCOUNTER`.
- **`eCompleteObjectives`**: `ALL, ALL_EXCEPT_DUMMY, ANY, FIRST, REPEAT` — controls how a quest's
  multiple objectives combine into quest completion.
- **`eQuests`**: `COMBAT, GENERIC_ADVENTURE, MODIFIER, PERMA_MODIFIER, SCOURGE, SIDE_MISSION, STORY,
  STORY_OPTIONAL, SYSTEM` — quest category enum.
- **`eAdventureEndTriggers`**: `NONE, WIN, LOSE` — this is the field on `QuestData` that ties a specific
  quest to ending the run (see §3).

NOT FOUND (probed by exact name, no match in the 5389 loaded types): `QuestConfig`, `ObjectiveConfig`,
`Objective`, `AdventureObjective`, `LoreHelper`, `ChapterConfig`, `ChapterState`.
`AdventureConfig` and `AdventureState` DO exist but model the map/campaign, not quests specifically
(see §4).

## 2. Completing/advancing an objective from code

No public "complete objective" API was found. What exists:

- `QuestHelper.CheckObjectiveCompletion(eQuestObjective pObjectiveType, QuestState pQuest,
  Int32 pObjectiveIndex, GameRunData pGameRun, List\`1[,] pHexMap, AdventureConfig pAdventureConfig,
  List\`1 pCompletedObjectives, List\`1 pPlayerEntities, List\`1 pAbilityResults, Entity pActivePlayer)`
  → `Boolean` — a **check**, not a mutator. It evaluates whether an objective's real-world condition is
  currently true; it does not flip any state itself.
- `QuestHelper.GetCompletedQuests(GameRunData pGameRun, List\`1[,] pHexMap, AdventureConfig
  pAdventureConfig, List\`1 pFilters, List\`1 pCompletedObjectives, List\`1 pPlayerEntities,
  List\`1 pAbilityResults, Entity pActivePlayer)` → `List\`1` — read-only query, not a mutator either.
- `QuestHelper.CloseQuest(QuestState pQuest, List\`1 pPlayers, GameRunData pGameRun,
  List\`1 pEntitiesToRefresh, Boolean pAddToFailed, String pMapID)` → `Void` — the actual quest-closing
  call (success or, per `pAddToFailed`, failure). Public, static-looking helper signature; takes an
  already-built `QuestState`.
- `AdventureDirector._resolveQuests(List\`1 pCompletedQuests, List\`1 pCompletedObjectives)` → `Task`
  (private) and `AdventureDirector._tryCompleteQuests()` → `Task\`1` (private) — the director-level
  orchestration that presumably calls `CheckObjectiveCompletion`/`CloseQuest` and drives UI/rewards.
  Both are private instance methods on the live `AdventureDirector`, not free functions — calling them
  from a harness requires either reflection/Harmony or driving the real game loop.
- `AdventureDirector._processQuestCompleteObjectiveActions(QuestState pQuest, Int32 pObjectiveIndex)`
  (found on `CombatPhase`, `Task`) — fires world triggers/dialogue/camera actions tied to a specific
  objective completing; again private, phase-scoped.
- **`QuestState.CompletedObjectives` is a public `Boolean[]` field.** This is the one clean lever: an
  automation harness with access to a live `QuestState` (e.g. via `GameRunData.ActiveQuests`, §4) could
  directly set `CompletedObjectives[i] = true` to force an objective flagged complete, then rely on the
  game's normal per-turn/per-event quest-resolution pass (`_tryCompleteQuests`/`_resolveQuests`) to notice
  and close the quest out. This is ASSUMED to work as a shortcut — it was not observed executing, only
  the field's existence and public settability were confirmed.

No `QuestHelper` method takes a `QuestState` + objective index and marks it done as its primary purpose;
the closest thing to a "complete now" verb is `AUTO_COMPLETE` in `eQuestObjective`, which is a value of
the enum (i.e., a quest authored with an `AUTO_COMPLETE` objective type) rather than a method to call —
NOT FOUND as an invokable API, only as a data value.

## 3. How an adventure ends

`AdventureDirector` (full `--methods` probed) contains, verbatim:

- `Task\`1 _endAdventure(Boolean pIsVictory)` — private. This is the actual "end the run" entry point:
  takes a boolean victory/loss flag. Strongly suggests `CINEMATIC_OUTRO` (an `eRoutes` value from prior
  probes) is routed to as a consequence of this call, though no direct call to `RouterMono.Route` with
  `CINEMATIC_OUTRO` was observed inside `AdventureDirector`'s printed signatures — TypeProbe shows
  signatures only, not bodies, so the actual route target is ASSUMED, not confirmed.
- `Task\`1 _tryVictoryLoss()` — private, no-arg. Reads as the periodic check that decides whether a
  victory/loss condition has been met and, if so, calls `_endAdventure`.
- `Boolean _tryEndAdventureFromDungeon()` — also present on `CombatPhase` and `EncounterPhase` — a
  dungeon-specific end-condition check.
- `QuestData.AdventureEndTrigger (eAdventureEndTriggers)` — `NONE | WIN | LOSE` — a quest can be
  authored to end the adventure on completion; this is the config-side link between "this quest closed"
  and "the run is over."

All of these are **private instance methods** on the live `AdventureDirector` object — none are public,
none are static. There is no free-standing "EndGame(bool victory)" API. `CombatHelper.TryEndGame`
(named in a prior probe, semantics still unconfirmed here) was not re-probed in this pass; it remains a
separate, unconfirmed candidate and was out of scope for this task's method list.

## 4. What can be read to know run progress

Confirmed public/field-level state (verbatim from TypeProbe):

- **`GameRunData.ActiveQuests`** (`List\`1`) — the live quest instances currently in play (element type
  not printed by `--methods`, but per `QuestHelper.AddQuestToGameRun(QuestData, GameRunData, Boolean)`
  and `CloseQuest(QuestState, ...)` signatures, this is a `List<QuestState>`).
- **`GameRunData.CompletedQuests`** (`List\`1`) — quests closed out successfully.
- **`GameRunData.FailedQuests`** (`List\`1`) — quests closed out as failures.
- **`GameRunData.FutureQuests`** (`List\`1`) — queued/not-yet-started quests.
- **`GameRunData.RoundCount`** (`Int32`) — global round counter for the run.
- **`GameRunData.GameStageIndex`** / **`GameRunData.GameStageRoundStart`** (`Int32`) — campaign-stage
  progress markers.
- **`GameRunData.Phase`** (`Nullable\`1`) and **`GameRunData.PhaseData`** (`Nullable\`1`) — current phase
  state (element type not printed).
- **`AdventureState.CurrentStoryQuestTitle`** / **`AdventureState.CurrentOptionalQuestTitle`** (`String`)
  — human-readable "what quest is currently active" — good for a coarse "did the run advance" signal.
  Reachable via `GameRunData.AdventureState`.
- **`AdventureState.TotalRoundCount`** (`Int32`) — adventure-scoped round counter (distinct from
  `GameRunData.RoundCount`).
- **`QuestState.CompletedObjectives`** (`Boolean[]`), **`QuestState.ObjectivesProgress`** (`List\`1`),
  **`QuestState.RoundsLeft`** (`Int32`) — per-quest granular progress, for any `QuestState` pulled out of
  `GameRunData.ActiveQuests`/`CompletedQuests`/`FailedQuests`.

Exact `Type.Member` list for a harness to poll:
`GameRunData.ActiveQuests`, `GameRunData.CompletedQuests`, `GameRunData.FailedQuests`,
`GameRunData.FutureQuests`, `GameRunData.RoundCount`, `GameRunData.GameStageIndex`,
`GameRunData.AdventureState`, `AdventureState.CurrentStoryQuestTitle`,
`AdventureState.CurrentOptionalQuestTitle`, `AdventureState.TotalRoundCount`,
`QuestState.CompletedObjectives`, `QuestState.ObjectivesProgress`, `QuestState.RoundsLeft`.

Reaching a live `GameRunData` at all was established in a prior probe via `Env.GameRuns` /
`UserData.LastGameRunIdPlayed` / `AdventureSelectionDirector._loadGameRun` — not re-derived here.

## 5. Shipped console commands relevant to quests/run completion

Probed `CombatPhase`, `EncounterPhase`, `FortunePhase`, `RestPhase` fully with `--methods`. None of
their printed method signatures reveal the actual command *name strings* — TypeProbe's `--methods`
output is signatures only, not method bodies, so the string literals passed to whatever
registration API these use are NOT VISIBLE with this tool. What is visible:

- All four phase types have a `Task _registerConsoleCommands()` method (private), except
  `EncounterPhase`, which instead has `Task _registerSendDebugCommand()` (private). Each also has
  `Void _unregisterCommonConsoleCommands()` and a field `Boolean _areDebugCommandsAllowed`.
- Each phase type has its own `_debugEndPhase` method — a skip-shaped command:
  - `CombatPhase._debugEndPhase()` → `Void`
  - `EncounterPhase._debugEndPhase()` → `Task`
  - `FortunePhase._debugEndPhase()` → `Void`
  - `RestPhase._debugEndPhase()` → `Void`, plus an overload `RestPhase._debugEndPhase(Int32 pOption)`
    → `Void`.
  These read as "force this phase to end" debug hooks, gated by `_areDebugCommandsAllowed`. All are
  private instance methods, not confirmed to be wired to a registered console command string (that
  wiring lives in the unseen body of `_registerConsoleCommands`/`_registerSendDebugCommand`).
- Quest/completion-adjacent methods found per phase (private, instance):
  - `CombatPhase`: `Task _tryCompleteCombatQuestObjectives()`, `Task _tryCompleteQuests()`,
    `Task _processQuestCompleteObjectiveActions(QuestState pQuest, Int32 pObjectiveIndex)`,
    `Boolean _tryEndAdventureFromDungeon()`.
  - `EncounterPhase`: `Boolean _tryCompleteQuests()` (note: returns `Boolean` here, not `Task` as in
    `CombatPhase`/`AdventureDirector`), `Task _endEncounter()`, `Boolean _tryEndAdventureFromDungeon()`.
  - `RestPhase`: `Void _endRestPhase()`, `Void _endRestPhase(eRoutes pNextRoute)`,
    `Boolean _tryEndRestPhase()`.
  - `FortunePhase`: no quest-completion-specific methods beyond the shared `_debugEndPhase()`/console
    registration found.

None of these are confirmed as registered console-command names — that requires decompiling method
bodies (out of scope for TypeProbe `--methods`), so this is reported as "found candidate hook methods,"
not "found a console command."

## Verdicts

- **Complete an objective from code**: **NEEDS-LIVE-SPIKE**. No public mutator API exists.
  `QuestState.CompletedObjectives[i] = true` is a plausible direct-field-write shortcut (public field,
  confirmed to exist) but was never observed to actually drive quest closure — that requires confirming
  the game's own tick (`_tryCompleteQuests`/`_resolveQuests`) picks up an externally-set flag, which can
  only be confirmed by running the game and observing behavior, not by static probing.

- **Detect run progress**: **FEASIBLE**. `GameRunData.ActiveQuests/CompletedQuests/FailedQuests`,
  `GameRunData.RoundCount`, `AdventureState.CurrentStoryQuestTitle`/`CurrentOptionalQuestTitle`, and
  `QuestState.CompletedObjectives`/`ObjectivesProgress` are all public, readable fields reachable from a
  live `GameRunData` (itself reachable via the already-established `Env.GameRuns` path). A harness can
  poll these directly with no reflection tricks beyond normal field access.

- **Detect run completion**: **NEEDS-LIVE-SPIKE**. `AdventureDirector._endAdventure(Boolean pIsVictory)`
  and `._tryVictoryLoss()` are the clear code path, and `QuestData.AdventureEndTrigger (WIN/LOSE)` is the
  config-side trigger — but both director methods are private instance methods with no confirmed link
  (from signatures alone) to `eRoutes.CINEMATIC_OUTRO` or to any publicly observable "run over" flag on
  `GameRunData`/`AdventureState`. A harness would need to either hook `_endAdventure` via
  reflection/Harmony, or watch for the route change to `CINEMATIC_OUTRO` (established in a prior probe)
  as an indirect completion signal — both require running the live game to confirm, not just static
  probing.

## Notes

- TypeProbe reports signatures only; no method bodies were inspected, so any claim about *what a method
  actually does internally* (e.g., whether `_endAdventure` calls `RouterMono.Route(CINEMATIC_OUTRO,...)`)
  is inference from naming, explicitly flagged ASSUMED above, not confirmed.
- `PartyHelper`, `MapHelper`, `TerrainHelper`, `GameRunHelper`, `QuestConfig`, `ObjectiveConfig`,
  `Objective`, `AdventureObjective`, `LoreHelper`, `ChapterConfig`, `ChapterState` were all confirmed
  NOT FOUND in the loaded assembly by direct TypeProbe lookup — do not write code against these names.
- `CombatHelper.TryEndGame` (named in a prior probe) was not re-probed here; still unconfirmed semantics.
