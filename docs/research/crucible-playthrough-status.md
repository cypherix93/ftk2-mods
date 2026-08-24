# Playthrough status — measured live 2026-08-23

The owner's bar (AC0): drive a FULL adventure run to completion over MCP. This records
exactly how far a real run got, and what blocks the rest. Everything marked PROVEN was
executed against the running game, not inferred.

## Proven live

| Capability | How | Evidence |
|---|---|---|
| Escape boot state | `ui_click online-quit-btn` → `back-btn` | modal + lobby cleared |
| Menu navigation | `ui_click campaign-btn`, category buttons | reached adventure select |
| Adventure select | `ui_nav down` + `ui_submit` | The Resistance selected |
| Difficulty | `ui_focus difficulty-inp` + `ui_nav left` | MASTER→JOURNEYMAN→APPRENTICE |
| Party creation | `ui_click add-character-btn` + `crucible_pad a` ×4 | 4 slots filled |
| **Class choice by id** | `ui_focus class-text-selector` + `ui_nav` | `CF_EOR_THIEF`→`CF_EOR_TRICKSHOT` |
| Start run | `party-container-next-btn` → `begin-adventure-btn` → `sys-dialog-ok-btn` | route ADVENTURE |
| Clear load gate | `crucible_pad a` | LoadingUIDocument continue cleared |
| **Advance dialogue** | `ui_click dialogue-container` | whole conversation cleared |
| **Quest read** | `crucible_get RouterHelper.Env.GameRun.ActiveQuests` | 4 quest ids returned |
| **Turn advance** | `ui_click end-turn-btn` | active character cycled Thief→Shepherd |
| **Fixture capture** | `saveUser` | GameRuns 20→21, 4.95 MB |
| State observation | `/state?schema=v2` | combat block + honest warnings |

## Reached

Autumn Forest overworld, Ashhaven, Round 0, party of four ClassForge classes
(Thief / Shepherd / Corsair / Bladedancer) with HP and stats rendering.
Active quest: `STORY_1_1_VISIT_PRAN` — "Go to Pran in the Autumn Forest".

## BLOCKED: overworld movement

Movement is the gap between here and combat, and combat is the gap between here and
testing any of the 60 authored recipes.

- Hexes are 3D world objects, NOT UI elements — `crucible_ui_*` cannot reach them.
- The D-pad PANS THE CAMERA; it does not move a hex cursor. There is no cursor to drive.
- The game's overworld movement is mouse-driven: click a destination hex.
- **The harness has no mouse-input command.** That is the missing primitive.

Two candidate fixes, in preference order:
1. `AdventureDirector._doTeleportAbility` — a game API, no screen coordinates involved,
   and robust to camera position. Being built as a debug verb.
2. A synthetic mouse click at screen coordinates. Works, but requires mapping a world hex
   to a screen point, which is fragile across camera pans and resolutions.

## Not yet reached

- Combat (blocked on movement)
- Any of the 60 authored skill recipes firing (blocked on combat)
- Fixture RELOAD — `saveUser` writes, but round-tripping to this position is unverified
- Run completion

## Open bugs found in passing

- "Field Medic" appears TWICE in the loadout list. `TRAIT_CF_FIELDMEDIC` is the only one of
  20 EOR traits carrying a `CF_` infix; likely cause, unconfirmed.
- Writing `AdventureState.CurrentTimeOfDayIndex` succeeds (`old=0 new=2`) but the HUD still
  reads "Dawn" — direct field writes do NOT propagate. Debug verbs must call game methods
  and verify an observable, not poke state.
- The virtual gamepad registers as a SECOND PLAYER; party slots alternate P1/P2.


---

## Update — fixture round-trip PROVEN (2026-08-23, later)

`saveUser` writes a save that RELOADS to the same position. Verified end to end:

```
loaded run id : 1ccaf8f9-4ee6-4db2-a7dc-bab1ed1b8873
expected      : 1ccaf8f9-4ee6-4db2-a7dc-bab1ed1b8873
route ADVENTURE, run.present true
quests restored: 4, same ids
round: 0
```

This settles the spike the whole fixture strategy depended on. Restarting the game is now
cheap: reload the fixture instead of re-driving twenty steps.

### CORRECTION to an earlier finding

Earlier this doc said writing `AdventureState.CurrentTimeOfDayIndex` "does NOT propagate",
because the HUD kept reading Dawn after the write. That was wrong. The value was set to 2,
and after a save/load cycle it read back as **2** — the write took effect in game state and
PERSISTED INTO THE SAVE. Only the UI failed to refresh.

That matters strategically: **mutate-then-save works.** State can be set directly and
captured into a fixture, which is what makes "generate any state without character creation"
achievable. A debug verb should still call the game's own method where one exists, so the UI
stays consistent — but a direct write is not lost.

### NEW BLOCKER: the shipped command registry is ROUTE-SCOPED

At `ADVENTURE` the registry holds 41 commands and does NOT include `GetSpecificThing`,
`SetStat` or `SetPlayerHealth`. Those are documented as shipped and they are, but they are
registered by a phase we have not reached — almost certainly `CombatPhase`.

Consequence: **items cannot be granted from the overworld via the console.** Combat gates the
item commands, movement gates combat. The way out is `crucible_invoke` against the underlying
APIs rather than the console.

`ToggleCheat` exists and runs, but registers no additional commands.

### Current blocker chain

```
grant items  ->  needs combat-phase commands  ->  needs combat
reach combat ->  needs movement               ->  needs hex teleport
hex teleport ->  AdventureDirector._onSelectHexPositionTeleportScroll(...)
             ->  needs ArgCoercion for Entity / Thing / ValueTuple`2
```

So the single highest-value piece of work is argument coercion for those three types.
Everything downstream unblocks at once.

---

## BREAKTHROUGH — unfocused input solved (2026-08-23, late)

### Root cause: THREE layers of focus gating, not one

| Layer | Fix | Status |
|---|---|---|
| `Application.runInBackground` | set true | applied |
| Unity `InputSystem.settings.backgroundBehavior` | `ResetAndDisableNonBackgroundDevices` -> `IgnoreFocus` | applied |
| **FTK2's own `InputController`** | Harmony prefix skipping only the LOST_FOCUS reason | **this was the real one** |

The game gates ALL input itself, independently of Unity:
`InputController.RequestDisable(eDisableRequest pRequester, string pLogMessage)`, with
`_disableRequesters` a `HashSet<eDisableRequest>` — input is off while it is non-empty.
The enum has 29 members. Only LOST_FOCUS is suppressed; ROUTE_CHANGE and
SYSTEM_DIALOG_TRANSITION still work, because suppressing those would fire input during
transitions.

Verified live with the window unfocused:
```
gate: on | disableReasons before: [LOST_FOCUS] | after: []
inputDisabled=False reasons=[] gateHeld=True
```

### Now working with the window UNFOCUSED

- **UI clicks** — menu navigation, escaping the boot lobby
- **Mouse injection** — `crucible_mouse_click <x> <y>` cleared the post-load "Click to
  Continue" gate that NOTHING else could clear, and produces hex path previews on the
  overworld. Coordinates are Unity convention: **bottom-left origin**, so screen-top y
  must be flipped (`clientHeight - y`). Client rect matched the screenshot exactly (1158x900).
- **Loading a save by API** — `AdventureSelectionDirector._loadGameRun(String pFileName,
  String pDisableLogMessage)`. Both params are strings, so it is directly callable. Loaded
  in ~10s, correct run id, no UI and no focus.

### Corrections to earlier claims in this document

- `AdventureDirector._loadSave(runId, filename, ct)` fails two ways: with a `.ftk2`
  extension it throws `File format  not supported`, and without one it stalls forever in
  `WaitingForActivation` because that director is not initialised on ADVENTURE_SELECTION.
  Use the AdventureSelectionDirector overload instead.
- **`saveUser` does NOT save the run.** It is `SaveGameHelper.SaveUserAsync(UserData)` —
  the user profile. The save file that appeared was the game's own autosave at run start.
  Persisting a fixture needs `SaveGameHelper.WriteSaveData(runId, GameRunData, UserData)`.

### Still open

- **Confirming a move.** Clicking a hex shows a numbered path preview (2 -> 1 -> 0), but a
  second click on either the clicked hex or the path destination does not execute the move.
  The confirm gesture is unknown — candidates: double-click, click-and-hold, a keyboard/pad
  confirm while the path is shown, or clicking the exact hex centre.
- `SpawnSpecificEnemy` is NOT in the registry even in-run and after `ToggleCheat`; it is
  behind the debug-commands gate. It was ranked the most likely route into combat.
- The focus gate does NOT auto-apply reliably at startup — it needed an explicit
  `crucible_input_focus_gate on`. Fix before any unattended run.

## 2026-08-23 — Device input is blocked while unfocused; the pairing theory is DISPROVEN

The handoff's top item was "get the virtual gamepad honoured; the hypothesis is that it was never
paired to an InputUser." That hypothesis was implemented, tested live, and is **wrong**.

What was measured, in order, all against the live game with the window UNFOCUSED:

1. `crucible_input_devices` (new) reports the real devices and their FTK2 player assignment:
   `Keyboard id=1` and `Mouse id=2`, both `activated=True`, both on `InputPlayer(AssignmentIndex=-1)`.
   **The primary player's AssignmentIndex is -1, not 0** — that was an open question.
   Unity's `InputUser.all` holds exactly one user with `pairedDeviceCount=2`.
2. `crucible_pad_pair -` (new) folded the virtual pad into that same `InputPlayer`:
   `activated[nav]=True activated[nonNav]=True`, and **no phantom P2 was created**
   (`clearedNonPrimaryPlayers=True`). Pairing itself works exactly as designed.
3. A `DpadDown` press with the pad paired **did not move menu focus** off `campaign-btn`.
   So pairing was never the blocker.
4. `crucible_key` (new) drives the REAL, already-paired `Keyboard.current` — the same class of
   device as the mouse, so it sidesteps pairing entirely by construction. `DownArrow` **also did
   not move focus**, and `Enter` did not activate the focused button.
5. `crucible_input_state` showed why the first attempts were doomed regardless:
   `inputDisabled=True reasons=[LOST_FOCUS] gateHeld=False` — **the focus gate does not auto-apply
   at startup**, confirming the handoff's warning. After `crucible_input_focus_gate on`:
   `inputDisabled=False reasons=[] gateHeld=True`.
6. With the gate held and `runInBackground` on, `crucible_key down` and `crucible_key enter`
   **still did nothing**.
7. In the same conditions, `crucible_ui_click campaign-btn` **worked**, advancing
   MainMenuUIDocument → AdventureSelectionUIDocument.

**Conclusion.** The dividing line is not gamepad-vs-keyboard and not paired-vs-unpaired. It is
**synthetic UI events vs. low-level device injection**. UI events (`crucible_ui_click/focus/nav/
submit`) work with the window unfocused. `InputSystem.QueueStateEvent` injection does not, on any
device — even a real, paired, activated one — and clearing FTK2's own `LOST_FOCUS` gate does not
change that. Something above `InputController` (the Input System's own background/focus handling,
or `InputSystemUIInputModule`) is discarding the injected events.

This also reframes "mouse injection works unfocused": that is worth re-testing against this
distinction rather than assumed, since it is the one device claim that predates this measurement.

**Consequence for the plan.** Ben's "drive the entire game through controller mode" is not
available while unfocused. The options are, in order of cost:
- Drive menus through the UI-event path, which already works unfocused and is proven through the
  whole cold-boot recipe. Only the two known UI-event-immune spots (party-slot fill, the
  "Click to Continue" gate) needed a real device press.
- Find the layer that drops injected events while unfocused and suppress it, the same way the
  FTK2 `LOST_FOCUS` gate was suppressed. This is the real fix and keeps controller mode.
- Accept a focused window for device input. Cheapest, but gives up unattended running.

**Shipped this session:** `crucible_input_devices`, `crucible_pad_pair`, `crucible_key`,
plus `KeyboardKeyName` in Core. 326 tests green. Also fixed: `GamepadCommands.LastResult` was
never in `RpcServer`'s result chain, so every `crucible_pad*` result was invisible over RPC —
`KeyboardCommands` was wired in at the same time. **`InputBackgroundCommands.LastResult` is still
missing from that chain**, which is why `crucible_input_background on` returns a blank result.

## 2026-08-23 (later) — Device input SOLVED for keyboard: two independent bugs, neither was focus

The earlier section concluded that device injection is "blocked while unfocused" and that the
dividing line is UI events vs. device injection. **Both halves of that were wrong.** Measured:

**Bug 1 — the fake-null tick guard (root cause of several "mysteries").**
`CruciblePlugin.Instance` is a MonoBehaviour, and this plugin's host GameObject is explicitly
destroyed at the end of chainloader startup. Unity overloads `operator==` so a destroyed component
compares EQUAL TO NULL while the managed reference is alive. Every per-tick guard written as
`if (CruciblePlugin.Instance == null || !Instance.CfgForceSinglePlayer.Value) return;` therefore
returned early forever, a second after boot. Three separate features were dead the whole time:
- `InputFocusGateCommands.AutoApplyTick` — the focus gate never auto-applied (the known symptom).
- `InputBackgroundCommands.AutoApplyTick` — **the Input System stayed on
  `backgroundBehavior = ResetAndDisableNonBackgroundDevices`**, which disables devices on focus loss.
- `SinglePlayerGuard.Tick` — so the boot auto-rejoin was never suppressed, which is the
  `MULTIPLAYER_LOBBY` race the recipes describe as "the game's own behaviour".

Diagnosis: reflection reads `_patchInstalled=True`, `CfgForceSinglePlayer=True`, `Instance` non-null,
yet `_autoApplied=False` — because reflection bypasses Unity's `==` override while the tick does not.
Fix: a plain `static bool CruciblePlugin.ForceSinglePlayerEnabled` captured in `Awake`. A plain
static has no Unity lifetime semantics and cannot fake-null.

**Bug 2 — the press and the release collapsed into one frame.**
Command handlers run ON the game thread, inside the `RouterMono.Update` postfix that drives
`MainThreadPump`. `crucible_key`/`crucible_pad` did press → `Thread.Sleep(40)` → release *inside that
one call*, so no frame ever rendered with the key held and the game's Input Actions never saw a
press→release transition. Sleeping on the main thread cannot produce a frame — it prevents one.
Fix: queue the press, then schedule the release on a later tick (`ReleaseDelayFrames = 2`).

**Result, measured live with the window FOCUSED and again after the fixes:**

| Input | Before | After |
|---|---|---|
| `crucible_key down` | focus unchanged | focus `campaign-btn` → `multiplayer-btn` |
| `crucible_key enter` | nothing | activated the button, advanced to `AdventureSelectionUIDocument` |
| `crucible_pad down` | focus unchanged | **still unchanged** |
| `crucible_ui_nav down` | worked | worked |

**Keyboard driving now works.** The critical discriminator was that the earlier test never checked
whether device input worked *with* focus — it did not, which means focus was never the variable.

**The gamepad has a third, separate problem and is being set aside.** It is the only device that must
be CREATED (`InputSystem.AddDevice<Gamepad>()`) because no physical pad is attached, and it is never
paired at Unity's `InputUser` level — `crucible_input_devices` shows `inputUsers pairedDeviceCount=2`
(keyboard + mouse) even after `crucible_pad_pair` folds it into FTK2's own `InputPlayer`. The
remaining untried lever is `InputUser.PerformPairingWithDevice`. Not worth it: the keyboard is a real,
already-paired device and FTK2 is fully keyboard-navigable, so **keyboard is the driving path**.

**Still unverified:** whether keyboard input works while the window is genuinely unfocused. The game
runs fullscreen and reclaims foreground, so an unfocused test could not be forced from here. With
`backgroundBehavior=IgnoreFocus` now applied at boot (Bug 1's fix) and the `LOST_FOCUS` gate held from
`Initialize`, the mechanism is in place; it needs one observation while the user is on another window.

## 2026-08-23 (later still) — Fixture factory works; content sweep clean; combat still gated

**Fixture generation is PROVEN end to end.** From the base fixture: swapped all four party
members to classes that had never been created by hand (Arcanist, Oracle, Chronomancer,
Runemage), saved under a fresh run id, reloaded, and read all four class ids back.
Character creation is no longer needed for any party combination.

- `crucible_party_list` / `crucible_party_set_class` / `crucible_fixture_save` / `crucible_fixture_list`
- The generated save is real: `ef0bac05-…ftk2`, 5.4 MB, 22 files on disk (21 before).
- `Env.GameRuns` is an in-memory cache and does NOT reflect a new file — check the folder.
- **Load requires being on ADVENTURE_SELECTION first**; `_loadGameRun` needs that director to
  exist. From MAIN_MENU it silently does nothing. `crucible_load_run` still uses the known-bad
  `AdventureDirector._loadSave` path and stalls in `WaitingForActivation` — prefer
  `crucible_invoke AdventureSelectionDirector _loadGameRun "<runId> -"`.
- A class swap changes only `CharacterComponent.ConfigName`. Max health is COMPUTED from the
  class config while `CurrentHealth` is stored, so a swap produced `34 / 21` in the HUD. The
  swap now heals to max. Display name, equipment and things still do NOT follow the swap —
  abilities come from the equipped weapon, so a class test must equip that class's weapon too
  (`ARM_EOR_STARTERS` holds 31 starter weapons, one per class).

**Custom skills are invisible to the game's own skill API — by design, not a bug.**
`CharacterHelper.GetPassiveSkills` maps to the `eSkills` enum, which cannot gain members at
runtime, so an authored `SKILL_CF_*` id will never appear there. ClassForge reads the character
config's `Passives` as raw strings instead. Verified: `CF_EOR_ARCANIST` in the LIVE merged config
carries all three passives including `SKILL_CF_ARCANIST_OVERFLOW`, while `GetPassiveSkills`
returns only the two vanilla ones. **Assert custom skills at the config level for presence and by
their combat effect for behaviour** — asserting via `GetPassiveSkills` yields a confident false
negative.

**Live content sweep added** (`FTK2.Crucible/tools/content_sweep.py`), asserting authored ids
against the running game rather than against JSON. Baseline: **31 deployed classes and 31 status
ids all resolve.** The four `CF_PACK_BALDURS` classes are absent by design — `tools/deploy.ps1`
has excluded that pack since 2026-08-05 behind `-IncludeBaldurs` because its 5 skill recipes have
no localization entries. Negative control confirmed: `STATUS_CURSE_00` resolves, bare `CURSE`
does not ("anything applying it silently does nothing") — the exact check that would have caught
the shipped bug.

**Combat is still not reachable, and the blockers are now precisely known:**
- The post-load `Click to Continue` gate (`continue-label` in `LoadingUIDocument`) must be cleared
  or NOTHING on the overworld responds. This is what made 25 consecutive `crucible_time_advance`
  calls report `changed=False`. Keyboard Enter clears it.
- Even cleared, `AdventureDirector._doEndTurn()` does not advance time (`timeOfDayIndex` stays 2,
  `roundCount` 0), and pressing `end-turn-btn` changes the screen without advancing either — the
  turn is presumably gated on each character acting first.
- Overworld movement is NOT keyboard-driven: arrow keys leave action points at 5 and change
  nothing. It remains mouse/hex based, so the movement-confirm gesture is still unsolved.
- `MultiplayerDemoQuickCombat` is registered on this route and dispatches without error but has
  no observable effect (route unchanged, still offline).
- `SpawnSpecificEnemy` is NOT in the registry on the ADVENTURE route (25 shipped commands there).

Remaining untried levers for combat entry, in order of promise: `EncounterPhase._debugForceCombat`
(a shipped Boolean latch, but requires already being in an encounter phase), writing a
`CombatState` directly into a fixture so the save loads already in a fight, and
`RouterMono.Route(COMBAT, …)` with a `CombatEncounterArgs` payload (the docs warn that traversal
is a graph walk through legal transitions, not teleportation).

## 2026-08-23 (final) — THE LOOP CLOSES: movement, combat entry, ability firing, victory

Every blocker in the handoff is resolved. Measured live, end to end, in one unattended sequence:

```
boot -> load fixture -> clear gates -> move -> interact VENUE -> COMBAT
     -> fire ability (damage observed) -> win -> back to ADVENTURE
```

**Movement.** A move is not a click gesture, which is why clicking a hex only ever produced a
path preview. The executor is `AdventureDirector._move(AdventureMoveData, bool consumeAP,
bool canBeAmbushed, bool showEncounterMenu)`, with the path from
`_getAdventureMoveData(entity, goal, consumeAP)` which also reports `IsValidMove`. Verified: a
character moved hex to hex and spent exactly the path cost in action points. Passing
`consumeAP=false` gives the pathfinder unlimited range — the game's own free-move path, ideal
for a harness.

**Two long-standing wrong beliefs, both corrected.**
- `GameRunData.RoundCount` and `AdventureState.CurrentTimeOfDayIndex` are `[Obsolete]` in the
  assembly; the game only writes `AdventureState.MapState.*`. The 25 end-turn calls that
  reported `changed=False` were compared against dead fields, so "turn advance does not work"
  was never actually established. Observed live in one snapshot: obsolete index = 2 while the
  live MapState index = 1.
- `_performVenueAction` dereferences `CombatEncounterComponent` on its twelfth line, so calling
  it on a town throws — and being `async void`, it unwinds silently. That is precisely what
  happened when it was aimed at the town the party was standing in.

**Combat entry.** `RouterMono.Route(COMBAT, …)` can never work: the payload is a list of entity
GUIDs, and `CombatPhase.Initialize` needs a Diorama and camera that only the Venue scene builds.
The legal path is `_tryShowEncounterMenu` (which resolves PROXY encounters and sets
`_encounterEntity`) followed by `_performEncounterAction(ctx, pAllowBroadcast:false)` with
`Action = VENUE`. Result: `route=COMBAT combat.active=True combatants=70`, Shepherd versus three
`CROW_FOREST_00`.

**Firing an ability.** `CombatPhase._performAiDecision(entity, CombatDecisionData, results)` is
the entry point the game's own AI uses; it does selection, highlight, look-at, slot roll and
execution in the right order. **Targeting is by TILE COORDINATE, not by entity.** Verified: the
targeted crow went hp 7 -> 6 and the attacker's secondary actions 1 -> 0.

**Winning.** `_debugEndPhase()` (console `EndPhase`) removes every living enemy and then ends
combat, so the victory test passes and loot drops. Verified: combatants 4 -> 1, route returned
to ADVENTURE. This is NOT interchangeable with `CombatState.EndCombatEarly`, which stops the
fight with enemies alive and therefore evaluates as a LOSS.

**Fog of war is a harness concern, not cosmetic.** An unexplored hex has no clickable target, so
a party teleported into unrevealed map is surrounded by cloud and every click silently does
nothing — indistinguishable from broken input. `crucible_reveal_map` sets all 5850 hexes visible.

**Deployment gap found, and it is large.** The live install is missing the `ftk2mods.armory`
plugin entirely — all 386 Armory items including every one of the 31 class starter weapons —
and `ftk2mods.warbrain`. Both are targeted by `tools/deploy.ps1`, so the install predates the
current script. This matters because ABILITIES COME FROM THE EQUIPPED WEAPON: a class fixture
cannot be given its intended kit until Armory is deployed. `EquipmentHelper.Equip` throws a
NullReferenceException on an absent id, which reads as a broken command rather than as missing
content — `crucible_thing_config` now reports it honestly instead.
