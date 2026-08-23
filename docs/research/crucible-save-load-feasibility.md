# Save/Load Feasibility for Test Fixtures

**Objective:** determine whether FTK2 `.ftk2` save files can be loaded programmatically, to use
pre-built "party synergy" saves as test fixtures instead of building parties through a verb API.

**Verdict: UNCLEAR — leaning NOT-FEASIBLE for the "bypass the UI" goal.**

A concrete load entry point exists (`AdventureDirector._loadSave`), but it is a **private**
method on a MonoBehaviour Director whose `Initialize` requires a fully-constructed adventure
scene (2D/3D canvases, camera controller, input controller). There is no evidence of a
load path that skips scene/UI setup. This mirrors the `set_party` finding: the only save/load
surface sits on a UI-lifecycle Director, not a headless service class.

All findings below are quoted verbatim from TypeProbe output (`--methods` flag), run against
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed`
(5389 types loaded) — first from the `ftk2-mods-crucible` copy of the tool (no signatures
emitted there), then from `C:\Users\ben\repos\ftk2-wt-probe2` (signatures emitted, per the
task note that this feature lives in that worktree).

---

## 1. What type owns save/load?

**`AdventureDirector`** owns the load calls. **`GameSaveDirector`** owns the save metadata
model (as a nested type) but exposes no members of its own beyond what every type inherits
from `Object`:

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

This is unusual for a "Director" — TypeProbe's `Flags` cover
`Public | NonPublic | Instance | Static`, so this is a complete member list, not a filtered
one. Whatever code populates/persists a save either lives outside this type (extension/static
helper elsewhere) or `GameSaveDirector` is essentially a namespace holder for the nested
`GameSaveData` struct. **UNRESOLVED**: could not find where save files are actually written to
disk.

Candidates checked and confirmed **NOT FOUND** in the assembly: `SaveHelper`, `SaveManager`,
`GameRunHelper`, `SaveData`, `PersistenceHelper`, `SerializationHelper`, `LoadGameDirector`,
`GameRunState`.

`GameRunData` **exists** and is the in-memory run-state model (fields like `ConfigName`,
`GameStageIndex`, `RoundCount`, `AdventureState`, `CombatState`, `DungeonState`, `VenueState`,
`Version`), but it has no save/load methods of its own — it is a plain data container with only
`Create`, `SetDefaultExpansions`, `SetEntities` as non-inherited methods.

`Env` (the global game-state singleton) holds save-adjacent state but no save/load methods:

```
| field | `GameRunData` | `GameRun` | `` |
| field | `List`1` | `GameRuns` | `` |
| field | `ManualSaveIdTracker` | `ManualSaveIdTracker` | `` |
| field | `NetworkData` | `NetworkData` | `` |
| field | `ReloadStagingData` | `ReloadStagingData` | `` |
| field | `String` | `SelectedGameRunId` | `` |
```

## 2. Load-a-specific-save entry point

Found on **`AdventureDirector`**, both overloads private (underscore-prefixed, per this
codebase's convention for non-public members):

```
| method | `Task`1` | `_loadSave` | `(GameSaveData pSaveData, CancellationToken pCancellationToken)` |
| method | `Task`1` | `_loadSave` | `(String pGameRunId, String pFilename, CancellationToken pCancellationToken)` |
```

The second overload takes exactly `(String pGameRunId, String pFilename, CancellationToken)` —
this is the closest thing to "load by id/path" in the assembly. But it is **private** on
`AdventureDirector`, and `AdventureDirector.Initialize` (the only public setup entry point)
requires the full scene:

```
| method | `Task` | `Initialize` | `(UIDocument pCanvas2D, UIDocument pOverheadCanvas2D, Int32 pRandomSeed, GameObject pCanvas3D, AdventureCameraController pCamera, GameObject pOverlayParent, InputController pInputControls)` |
```

No public wrapper calling `_loadSave` was found on `AdventureDirector`, `RouterHelper`,
`MainMenuDirector`, or `Env` — grepping the full `--methods` dump for `save`/`load`/`reload`
turned up nothing else. **UNRESOLVED**: the caller of `_loadSave` (presumably a
`LoadGameOverlayController`/menu-list controller) was not located — that type name was not
guessed/probed and would need a follow-up probe pass.

## 3. Save-now entry point

No explicit "save now" trigger method was found on `AdventureDirector`. Only a gate check
exists:

```
| method | `Boolean` | `IsSavingAllowed` | `(Boolean pConsiderDialog)` |
| method | `Boolean` | `_isJipSavingAllowed` | `()` |
```

The task's known facts already list a shipped console command `saveUser` registered at
MAIN_MENU as the save trigger; TypeProbe (reflection-only, can't inspect console command
registration bodies) does not add anything beyond that. **UNRESOLVED** for a code-level
save API distinct from the console command.

## 4. Save identity and metadata model

Save files on disk are named by GUID (confirmed by directory listing — see §6). The metadata
type is `GameSaveDirector.GameSaveData` (a nested type; probed by simple name `GameSaveData`,
TypeProbe resolved it to `GameSaveDirector+GameSaveData`):

```
## GameSaveDirector+GameSaveData

| Kind | Type | Name | Signature |
|---|---|---|---|
| field | `String` | `adventureType` | `` |
| field | `ChaosState` | `chaosState` | `` |
| field | `List`1` | `characters` | `` |
| field | `Int32` | `currentLifePool` | `` |
| field | `DateTime` | `dateTime` | `` |
| field | `eGameDifficulties` | `difficulty` | `` |
| field | `List`1` | `expansions` | `` |
| field | `Int32` | `loopCount` | `` |
| field | `String` | `manualId` | `` |
| field | `Int32` | `mapGenSeed` | `` |
| field | `Int32` | `maxLifePool` | `` |
| field | `Int32` | `roomCount` | `` |
| field | `Int32` | `roundCount` | `` |
| field | `String` | `runID` | `` |
| field | `String` | `runPath` | `` |
| field | `String` | `saveName` | `` |
| field | `String` | `version` | `` |
| method | `String` | `GetFileName` | `()` |
| method | `Boolean` | `IsPS4TransferRun` | `()` |
```

`runID` (String) plus `GetFileName()` strongly indicates the on-disk GUID filename is derived
from `runID` — matching the observed `<guid>.ftk2` filenames in `GameRuns/`. `characters`
(a `List<T>`, element type not resolvable via this probe pass) is the party-composition field —
this is the type that would need to be inspected/edited to build a "party synergy" fixture, if
edits to save data turn out to be necessary.

Related helper types found:

```
## ManualSaveIdTracker
| method | `Int32` | `GetNextId` | `(String pGameRunId)` |

## ReloadStagingData
| field | `GameRunData` | `GameRun` | `` |
| field | `Dictionary`2` | `HexMaps` | `` |
| field | `List`1` | `PartyCharacters` | `` |
```

`ReloadStagingData` (held on `Env.ReloadStagingData`) looks like the staging buffer for an
in-progress reload — further evidence that loading is a stateful, multi-step Director process,
not a single call.

## 5. UI coupling

**Loading routes through a Director's scene lifecycle; it is not a headless call.**

Evidence: `_loadSave` is a private instance method on `AdventureDirector`, and the only way to
get a live `AdventureDirector` instance in a runnable state is through its public `Initialize`,
which demands real Unity scene objects:

```
| method | `Task` | `Initialize` | `(UIDocument pCanvas2D, UIDocument pOverheadCanvas2D, Int32 pRandomSeed, GameObject pCanvas3D, AdventureCameraController pCamera, GameObject pOverlayParent, InputController pInputControls)` |
```

Additional supporting methods on `AdventureDirector` reinforce this is a full scene-lifecycle
object, not a data service: `Deinitialize`, `DeinitializeCommon`, `DeleteMapDeinitialize`,
`LateUpdate(Single pFixedTime)`, `_showFinishLoadingConfimPrompt(Action pOnQuit)`,
`_tryFinishLoadingAndInit(Action pOnQuit, Boolean pSkipTransition)`.

This is the same shape of blocker as the `set_party` finding: the only real entry point sits on
a UI-lifecycle Director (`PartyManagementDirector` there, `AdventureDirector` here), not on a
plain-data or headless service type.

## 6. File format inspection

Read the first 64 bytes of a real save (`0778a3d1-5bf7-449a-acae-263102cf2235.ftk2`,
5,847,090 bytes) from
`C:\Users\ben\AppData\LocalLow\IronOak Games\For The King II\GameRuns\`. **Read-only** — no
write/move/delete performed.

```
EF BB BF 1D 1E 19 13 43 5A 13 47 5C 78 77 1B 02 5A 51 05 05 09 52 0A 5C 49 4C 07 50 57 04 14 0C
4C 58 53 1F 50 50 58 5D 55 53 04 01 00 03 0B 5B 1E 53 00 01 04 11 15 1A 1C 00 46 57 65 5A 54 5D
```

- Bytes `EF BB BF` are a UTF-8 BOM.
- Everything after the BOM is low-value control-range bytes (0x00–0x5D range dominated),
  not printable JSON/text and not a recognizable compression magic (no `1F 8B` gzip header, no
  `50 4B` zip header, no protobuf-typical varint/tag pattern at the start).

**Conclusion: the format is not plain-text-inspectable.** It reads as XOR'd/obfuscated or
encrypted binary with a stray leading UTF-8 BOM (possibly a leftover artifact of a
pre-encryption text encoding step). This confirms the identity signal is the **filename** (GUID
matching `runID`), not something skimmable from the file's own bytes without reversing the
encoding first.

Filenames observed in the folder are GUIDs (e.g. `0778a3d1-5bf7-449a-acae-263102cf2235.ftk2`),
consistent with `GameSaveData.runID` (String) driving `GetFileName()`.

---

## Answers to the return-shape questions

- **verdict**: UNCLEAR, leaning NOT-FEASIBLE for "bypass the UI entirely." A load API with the
  right shape exists (`AdventureDirector._loadSave(String pGameRunId, String pFilename,
  CancellationToken pCancellationToken)`), but it is private and gated behind
  `AdventureDirector.Initialize`, which needs a live adventure scene (canvases, camera,
  input controller). No public, headless load path was found.
- **load_api**: `AdventureDirector._loadSave(GameSaveData pSaveData, CancellationToken
  pCancellationToken)` and `AdventureDirector._loadSave(String pGameRunId, String pFilename,
  CancellationToken pCancellationToken)` — both private, both require a scene-initialized
  `AdventureDirector` instance.
- **save_api**: none found as a direct code call. Only `AdventureDirector.IsSavingAllowed(Boolean
  pConsiderDialog)` (a gate check, not a trigger) was found. The known shipped console command
  `saveUser` (from the task's prior facts) remains the only confirmed save trigger.
- **save_identity**: GUID filename (`<guid>.ftk2`) in `GameRuns/`, matching the `runID` (String)
  field on `GameSaveDirector.GameSaveData`, which also carries `saveName`, `manualId`,
  `dateTime`, `roundCount`, `difficulty`, `characters` (party composition), `chaosState`,
  `expansions`, `mapGenSeed`.
- **ui_coupling**: Yes — loading requires `AdventureDirector.Initialize(UIDocument, UIDocument,
  Int32, GameObject, AdventureCameraController, GameObject, InputController)` before
  `_loadSave` can run. Same class of blocker as the `set_party` / `PartyManagementDirector`
  finding.
- **file_format**: Not plain JSON or a recognizable compressed container. First bytes are a
  UTF-8 BOM (`EF BB BF`) followed by low control-range bytes — consistent with an
  XOR/obfuscated or encrypted binary blob, not directly parseable without reversing the
  encoding.
- **types_probed**: `SaveHelper` (not found), `SaveManager` (not found), `GameRunHelper` (not
  found), `SaveData` (not found), `GameRunData` (found), `PersistenceHelper` (not found),
  `SerializationHelper` (not found), `AdventureDirector` (found), `RouterHelper` (found),
  `Env` (found), `MainMenuDirector` (found), `LoadGameDirector` (not found), `GameRunState`
  (not found), `GameSaveData` (found, resolves to `GameSaveDirector+GameSaveData`),
  `GameSaveDirector` (found, empty besides inherited members and the nested type),
  `ManualSaveIdTracker` (found), `ReloadStagingData` (found), `UserData` (found, no save/load
  members), `NetworkData` (found, fields only — no save/load methods on it directly).
- **notes**: The caller of `AdventureDirector._loadSave` (likely a menu/list controller such as
  a "load game overlay") was not located — it wasn't in the probed set and would need a
  follow-up TypeProbe pass on guessed controller names (e.g. `LoadGameOverlayController`,
  `SaveSlotController`) to fully resolve whether *any* path exists to invoke `_loadSave`
  without going through `MainMenuDirector`'s UI flow. Given the private access modifier and the
  `Initialize` scene-object requirement already found, it is unlikely that follow-up would
  change the verdict from "requires UI/Director scene setup," but it could reveal whether the
  invocation itself is human-click-only or reachable from a scripted console command.
