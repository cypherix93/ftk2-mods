# FTK2.Crucible Harness API Reference

Complete reference for the Crucible test-harness command surface: the loopback HTTP RPC
(`127.0.0.1:8787`) exposed by the BepInEx plugin, the Node MCP server (`ftk2_*` tools) that
wraps it, and the Python driving layer (`FTK2.Crucible/tools/*.py`) that uses it. Compiled by
reading every `TryRegister()` in `FTK2.Crucible/src/Crucible.Plugin/*.cs`, `FTK2.Crucible/mcp/server.js`,
and the Python drivers. Every command below is quoted from the actual C# handler signature —
file:line citations are given so you can verify against source rather than trust this doc blindly.

Registration mechanics (applies to every command): `GameBridge.RegisterCommand(name, handler, hints)`
reflectively calls the game's own `CommandLineHelper.RegisterCommand`. All `TryRegister()` methods
are polled every tick from `MainThreadPump` (not called once from `Awake`) because the game's
internal command registry does not exist yet at plugin `Awake` time — see TRAPS §1.

Handlers are `void` methods that stash their output in a static `<Class>.LastResult` string; the
RPC server's `/exec` handler reads that field after invoking the command. All `LastResult` fields
are cleared before each dispatch so a delegating command can't leak a prior command's output
(see RunEndCommands.cs:307-310 for a bug this caused).

---

## 1. QUICK REFERENCE

### Session / run
| Command | Args | Purpose |
|---|---|---|
| `crucible_run_status` | — | Read-only: map/round/stage/quests/win-loss assertion surface |
| `crucible_tutorials_suppress` | — | Mark all shipped tutorials seen + disable tutorial popups |
| `crucible_quest_activate` | `questId` | Force-add a quest, bypassing world-trigger/chaos gating |
| `crucible_quest_complete_objective` | `questId, objectiveIndex(or "all")` | Flag objective(s) complete + drive quest-completion pump |
| `crucible_quest_remove` | `questId(or "all")` | Drop quest(s) from ActiveQuests without resolving |
| `crucible_encounters_clear` | `keyword(or "all")` | Delete non-combat map encounters by substring |
| `crucible_endadventure_watch` | `action, value` | Read latched win/loss outcome of last `_endAdventure` |
| `crucible_summary_dismiss` | — | Page through the (2-page) adventure-summary UI |
| `crucible_encounter_leave` | — | Force-close a stuck encounter UI |
| `crucible_party_gain_xp` | `xpEach, slotOrAll` | Grant XP via the real progression path |
| `crucible_fixture_save` | `label` | Save current run under a **fresh** run id (never overwrites) |
| `crucible_refresh_saves` | — | Re-scan the save folder into `Env.GameRuns` |
| `crucible_fixture_list` | — | List known save/run ids |
| `crucible_load_run` | `runId` | Load a save by id, guarded to only-known ids, no implicit fallback |
| `crucible_pin_seed` | `steps` | Write `GameRandom.Seed` on all live instances |
| `crucible_time_advance` | `steps` | Call `_doEndTurn()` up to N times (async, un-awaited) |

### Overworld
| Command | Args | Purpose |
|---|---|---|
| `crucible_overworld_state` | — | Round/time/weather/turn-order/party AP+position |
| `crucible_path_preview` | `x, y` | Read-only reachability/cost check |
| `crucible_move` | `x, y, consumeActionPoints, canBeAmbushed, showEncounterMenu` | Move active character |
| `crucible_hex_info` | `x, y` | Terrain/encounter/character on a hex |
| `crucible_overworld_end_turn` | — | End active character's turn (async void) |
| `crucible_interact` | `action` | Perform encounter action (VENUE etc.) on current hex |
| `crucible_set_diorama` | `dioramaConfig` | Set battlefield visual skin (must be set before combat loads) |
| `crucible_party_set_hex` | `x, y` | Teleport whole party (writes state only, no arrival logic) |
| `crucible_reveal_map` | `Visible\|Revealed\|Hidden` | Set fog state on every hex on every map |
| `crucible_map_encounters` | `filter` | Scan hexmap for encounter entities |
| `crucible_force_encounter` | — | Legal path: spawn an encounter at party's hex |
| `crucible_engage_encounter` | — | Legal path: drive ADVENTURE→VENUE→COMBAT transition |

### Combat
| Command | Args | Purpose |
|---|---|---|
| `crucible_force_combat` | `enemyConfigName, count, payloadKind` | **Does not work** — flips route only, produces empty fight |
| `crucible_combat_snapshot` | — | Read-only combatant + tile dump |
| `crucible_list_abilities` | `entityGuid(or "-")` | List an entity's abilities + usability |
| `crucible_list_targets` | `abilityName, entityGuid(or "-")` | List legal target tiles for an ability |
| `crucible_use_ability` | `abilityName, x, y` | Fire ability as the **active** entity only (no entity param) |
| `crucible_use_ability_auto` | — | Auto-pick ability+tile for active entity, fire it |
| `crucible_combat_end_turn` | — | End active combatant's turn (async void) |
| `crucible_win_combat` | — | Guaranteed win w/ loot (removes all enemies, real victory path) |
| `crucible_combat_wipe_enemies` | `group(default 1)` | Kill everyone in a combat group with real death bookkeeping |
| `crucible_combat_restore_actions` | `group(default 0)` | Refill PA/SA to max for a combat group |
| `crucible_combat_spawn` | `characterConfig, count(default 1), group(default 1)` | Add a combatant to the **current** fight |
| `crucible_godmode` | `on\|off\|status` | Per-tick top-up of party HP (damage still lands/triggers) |

### Character / items
| Command | Args | Purpose |
|---|---|---|
| `crucible_party_list` | — | Read-only party summary |
| `crucible_party_abilities` | `slot` | Read-only: abilities, passives (entity vs config), weapon |
| `crucible_party_set_class` | `slot, classConfigName` | Swap class id + heal to new max (does NOT swap weapon) |
| `crucible_equip` | `slot, thingConfigName` | Equip an item (abilities come from weapon, not class!) |
| `crucible_give_item` | `slot, thingConfigName, quantity` | Add item(s) to inventory |
| `crucible_set_stat_value` | `slot, stat, value` | Direct stat write |
| `crucible_status_add` | `slot, statusConfigName, duration` | Apply a status effect outside combat |
| `crucible_give` | `configId, qty` | Give an item via shipped `GetSpecificThing`, verified |
| `crucible_set_level` | `slot, level` | Grant XP shortfall to reach a level (up only, never down) |
| `crucible_heal_party` | — | Heal whole party to max |
| `crucible_kill_all` | — | Kill both sides (filters to `CharacterComponent` entities) |
| `crucible_debug_spawn` | `kind(enemies\|encounters), selector(or "-" to list)` | Real spawn via game's own debug-menu buttons |

### Config / reflection
| Command | Args | Purpose |
|---|---|---|
| `crucible_loc` | `key(or prefix*)` | Look up/search localization strings |
| `crucible_class_config` | `classId` | Read a character/class config |
| `crucible_status_config` | `statusId` | Read a status-effect config |
| `crucible_thing_config` | `thingId(or prefix*)` | Read/search an item config |
| `crucible_get` | `path` | Reflective read of a static/instance dot-path |
| `crucible_set` | `path, value` | Reflective write to a dot-path — **high hazard** |
| `crucible_invoke` | `type, method, args(space-sep or "-")` | Reflective method invocation — **highest hazard** |
| `crucible_quest_state` | — | Read-only quest state dump |
| `crucible_quest_complete` | `questIndex, objectiveIndex` | Set objective complete + drive completion |

### Chaos
| Command | Args | Purpose |
|---|---|---|
| `crucible_chaos_freeze` | `on\|off` | Harmony-patch `ModifyChaosLevel` to no-op globally |
| `crucible_chaos_state` | — | Read-only chaos state (no scalar "level" exists) |
| `crucible_chaos_advance` | `chaosConfigName` | Drive the real CHAOS_ACTIVE world-trigger path |

### Fixtures
| Command | Args | Purpose |
|---|---|---|
| `crucible_fixture_save` | `label` | see Session table |
| `crucible_refresh_saves` | — | see Session table |
| `crucible_fixture_list` | — | see Session table |
| `crucible_party_list` | — | see Character table |
| `crucible_party_set_class` | `slot, classConfigName` | see Character table |

### UI / Input
| Command | Args | Purpose |
|---|---|---|
| `crucible_ui_dump` | `filter, kinds` | Reflective UIToolkit tree dump (capped 50000 elements) |
| `crucible_ui_click` | `selector` | Click first visible Button matching selector — **substring-match hazard** |
| `crucible_ui_focus` | `selector` | Focus an element, report before/after |
| `crucible_ui_press` | `selector` | Full press: exact-then-substring match, verified focus, activation ladder |
| `crucible_ui_nav` | `direction` | Send NavigationMoveEvent up/down/left/right |
| `crucible_ui_submit` | `_unused` | Send NavigationSubmitEvent to focused element |
| `crucible_ui_cancel` | `_unused` | Send NavigationCancelEvent |
| `crucible_ui_where` | `_unused` | Read-only: docs, focus, on-screen buttons |
| `crucible_dialogue_advance` | `maxPresses` | Loop-press continue prompts until no change |
| `crucible_dialogue_choose` | `selector(or "-" to list)` | List/activate dialogue options |
| `crucible_key` | `key` | Press+release a keyboard key (release scheduled a later frame) |
| `crucible_mouse_click` | `x, y` | Move+click mouse (release scheduled later) |
| `crucible_pad` | `button` | Tap a gamepad button |
| `crucible_pad_hold` | `button, milliseconds` | Hold a gamepad button (clamped max) |
| `crucible_pad_stick` | `stick, direction` | Set a stick direction — **replaces whole device state** |
| `crucible_pad_pair` | `playerIndex(or "-")` | Pair virtual pad into FTK2's own InputPlayer list |
| `crucible_input_devices` | — | Read-only input-device diagnostic |
| `crucible_input_background` | `on\|off` | Toggle background input processing |
| `crucible_input_focus_gate` | `on\|off` | Harmony-suppress `InputController.RequestDisable(LOST_FOCUS)` |
| `crucible_input_state` | — | Read-only dump of active input-disable requesters |
| `crucible_end_phase` | `phase` | Invoke the live phase's own `_debugEndPhase` |

---

## 2. FULL COMMAND REFERENCE

Format per command: **name** — `params` (file:line of registration and handler) — description —
result contents — safety.

### 2.1 Combat — `AbilityCommands.cs`, `CombatCommands.cs`, `CombatDriveCommands.cs`

**crucible_party_abilities** — `(string slot)`
Handler: `AbilityCommands.CruciblePartyAbilities`, AbilityCommands.cs:59. Reg: AbilityCommands.cs:49-51. Hint `{"slot"}` matches.
Reads one party member's abilities (`CombatHelper.GetAbilities`, 8-arg overload), passive skills
by-entity AND by-config-name, and equipped weapon; reports classId.
Result: `slot=<n> classId=<id>`, `abilities: count=N [...]`, `passiveSkills(entity): ...`,
`passiveSkills(configName): ...`, `equippedWeapon: ...`, plus a note that entity-passives diverging
from config-passives means the entity wasn't rebuilt for its class.
Safety: read-only.

**crucible_force_combat** — `(string enemyConfigName, string count, string payloadKind)`
Handler: CombatCommands.cs:98. Reg: CombatCommands.cs:56-58. Hint `{"enemyConfigName","count","payloadKind(names|args|entities)"}` matches.
Builds one of 3 payload shapes (names/args/entities) and calls `RouterMono.Route(COMBAT, ..., force:true)`.
Result: `enemy=... count=... payloadKind=... payloadType=... routeBefore=... routeInvoked=... routeAfter=...`
Safety: **does not actually work** — see TRAPS. Use `crucible_force_encounter` + `crucible_engage_encounter` instead.

**crucible_force_encounter** — `()`
Handler: CombatCommands.cs:554. Reg: CombatCommands.cs:60-63. No params, matches.
The legal path — calls `AdventureHelper.TryNextAmbushEncounter` at the party's current hex.
Result: `mapId=... position=... hexMap=... gameRandom=... partyCount=...`, then
`result=... encounterBefore=... encounterAfter=... route=...`
Safety: overworld only; requires loaded run + party.

**crucible_engage_encounter** — `()`
Handler: CombatCommands.cs:436. Reg: CombatCommands.cs:65-68. No params, matches.
Finds the encounter on the party's hex and calls `AdventureDirector._performVenueAction`, driving
the real ADVENTURE→VENUE→COMBAT transition.
Result: `encounterSource=... encounterGuid=... routeBefore=... routeAfter=...`
Safety: async — re-read `/state?schema=v2` after a few seconds; requires an encounter already present.

**crucible_map_encounters** — `(string filter)`
Handler: CombatCommands.cs:259. Reg: CombatCommands.cs:70-73. Hint `{"filter"}` matches.
Scans `Env.HexMaps[mapId]` for entities with `EncounterComponent`, optional substring filter.
Result: `mapId=... partyHex=...`, per-hit `[x,y] configName guid=...` capped at 40, `gridSize=RxC encountersFound=N`.
Safety: read-only.

**crucible_party_set_hex** — `(string x, string y)`
Handler: CombatCommands.cs:359. Reg: CombatCommands.cs:75-78. Hint `{"x","y"}` matches.
Teleports whole party by writing `AdventureComponent.HexPosition` directly per member.
Result: per-member `[i] before -> after changed=bool`, `movedCount=M/total`.
Safety: **does not run game arrival logic** — no hex reveal, no encounter trigger, no movement cost. Combine with `crucible_reveal_map`.

**crucible_reveal_map** — `(string state)`
Handler: CombatCommands.cs:168. Reg: CombatCommands.cs:80-83. Hint `{"Visible|Revealed|Hidden"}` matches.
Sets `HexComponent.VisibilityState` across every hex on every map (not just current).
Result: per-map `map <key>: hexes=N changed=N`, then totals.
Safety: safe, write-only fog removal.

**crucible_combat_wipe_enemies** — `(string group)`
Handler: CombatDriveCommands.cs:~543. Reg: CombatDriveCommands.cs:45-46. Hint `{"group (default 1 = enemies)"}` matches.
For every combatant in `group` (default 1), zeroes `CurrentHealth` then calls `CharacterHelper.TryKillCharacter` for real death bookkeeping.
Result: `group=N inGroup=N killed=N alreadyDead=N otherGroups=N changed=bool` + killed names; async, re-read snapshot.
Safety: must be in combat. Group 0=party, default 1=enemies deliberately, to avoid party-wipe by typo.
Trap: `crucible_kill_all` does NOT work in combat (walks `GameRunData.Entities`, not `CombatState.Entities`).

**crucible_godmode** — `(string mode)`
Handler: CombatDriveCommands.cs:1573. Reg: CombatDriveCommands.cs:47. Hint `{"on|off|status"}` matches.
Per-tick top-up (`Tick()`) rewriting party (group 0) `CurrentHealth` to max while ON. Damage still
lands and still fires `ON_DAMAGE_TAKEN` — NOT an invulnerability flag.
Result: `godmode=ON|OFF topUps=<counter>` + explanatory notes.
Safety: safe, only affects group 0.

**crucible_combat_restore_actions** — `(string group)`
Handler: CombatDriveCommands.cs:~614. Reg: CombatDriveCommands.cs:48-49. Hint `{"group (default 0 = party)"}` matches.
Refills `PrimaryActions`/`SecondaryActions` to PA/SA stat (fallback 1) for every combatant in `group` (default 0=party).
Result: `group=N restored=N` + per-character before/after PA/SA.
Safety: must be in combat. An out-of-actions ability fails **silently** (see TRAPS).

**crucible_combat_spawn** — `(string characterConfig, string countArg, string groupArg)`
Handler: CombatDriveCommands.cs:~698. Reg: CombatDriveCommands.cs:50-51. Hint `{"characterConfig","count (default 1)","group (default 1 = enemies)"}` matches.
Adds a combatant to the CURRENT fight via `CombatHelper.ApplyAction(ADD_CHARACTER, ...)` (the
game's full 9-step join) plus manual `EnsureActorVisual` wiring. Count capped at 12. Tile's
`GroupIndex` decides allegiance.
Result: `config=... group=... requested=N placed=N` + per-spawn placement/visual status, timeline-refresh note.
Safety: must be in combat. **Assert on `crucible_combat_snapshot`, not on screenshot** — the 3D model comes from a separate hand-off (see TRAPS).

**crucible_combat_snapshot** — `()`
Handler: CombatDriveCommands.cs:76. Reg: CombatDriveCommands.cs:52. No params, matches.
Enumerates `CombatState.Entities`, splits into combatants (`CombatComponent`+`CharacterComponent`) and tiles.
Result: `totalRounds=... waveIndex=... activeGuid=<guid>`, then `combatants=N` with per-row
`<guid> name=... class=... group=... hp=... tile=... pa=... sa=... dead=bool statuses=...`; then
`tiles=N` (**cap actually 120** in code, though the printed message says "showing first 30" — a
real discrepancy between the code's cap and its own status text).
Safety: read-only, must be in combat. See TRAPS for the `activeGuid=` header false-match risk.

**crucible_list_abilities** — `(string entityGuid)`
Handler: CombatDriveCommands.cs:~168. Reg: CombatDriveCommands.cs:53. Hint `{"entityGuid or -"}` matches.
Lists `CombatHelper.GetAbilities` output with `IsUsableAbility` + `GetCharacterAbilityThing` per ability.
Result: `entity=<guid> abilities=N`, per-ability `name thingId=... thingConfig=... usable=bool/(unknown) thingResolves=bool`.
Safety: must be in combat. `""`/`"-"` resolves to active entity.

**crucible_list_targets** — `(string abilityName, string entityGuid)`
Handler: CombatDriveCommands.cs:~217. Reg: CombatDriveCommands.cs:54. Hint `{"abilityName","entityGuid or -"}` matches.
Resolves ability config, calls `VenueHelper.GetTargetableTiles` (4-arg overload, selected by param count).
Result: `ability=... entity=...`, per-tile `tile=(x,y) group=... occupant=...`, `targetableTiles=N`.
Safety: must be in combat.

**crucible_use_ability** — `(string abilityName, string x, string y)`
Handler: CombatDriveCommands.cs:302. Reg: CombatDriveCommands.cs:55. Hint `{"abilityName","x","y"}` matches — **NO entity/guid parameter exists**.
Parses x/y and calls shared `FireAbility(abilityName, tileX, tileY, null)`, which internally calls
`ResolveEntity(null, ...)` — hardcoded null, always resolving to `CombatPhase._activeCharacterEntity`.
Result: `ability=... target=(x,y) origin=<guid> resultCount=N healthBefore: name=hp,...` + async note.
Safety: must be in combat. See TRAPS — this is the "always the active entity" trap.

**crucible_use_ability_auto** — `()`
Handler: CombatDriveCommands.cs:335. Reg: CombatDriveCommands.cs:56. No params, matches.
`CombatHelper.GetFirstEntityDecision` picks ability+tile for the active entity, fires via same `FireAbility` path.
Result: `auto-chose ability=... target=(x,y)` prefixed onto the ability-fire result text.
Safety: same async caveat as `crucible_use_ability`.

**crucible_win_combat** — `()`
Handler: CombatDriveCommands.cs:502. Reg: CombatDriveCommands.cs:57. No params, matches.
Invokes the game's own debug `EndPhase` command via `GameBridge.Exec`, removing every living enemy
then calling `_endCombatAsync` (victory test: `aliveEnemies==0 && alivePlayers>0`).
Result: `EndPhase invoked=bool [error=...] before: route=... combatants=N after: route=... combatants=N` + async note.
Safety: must be in combat. **`CombatState.EndCombatEarly = true` is the OPPOSITE** — it ends the fight with enemies alive and evaluates as a LOSS. Do not confuse the two.

**crucible_combat_end_turn** — `()`
Handler: CombatDriveCommands.cs:456. Reg: CombatDriveCommands.cs:58. No params, matches.
Calls `CombatPhase._nextTurn(false)` — NOT `CombatHelper.NextTurn` (that mutates turn order without the wrapping timeline/engage/camera work).
Result: `activeBefore=<guid> activeAfter=<guid>` + async note.
Safety: must be in combat.

Support code (no commands): `CombatReader.cs` builds the `crucible.state.v2` snapshot schema
(distinct from `crucible_combat_snapshot`'s text dump). Falls back `CombatPhase._combatState` →
`GameRunData.CombatState`; combat identity is `RuntimeHelpers.GetHashCode(combatState)`;
`IsPlayer` is component-presence-based; `MaxHp` computed via `CharacterHelperBridge.InvokeMaxHealth`
(not stored); turn/phase from `TurnHooks`, null/`PhaseUnknown` if hooks aren't installed rather
than faking a zero.

### 2.2 Session / Run / Overworld — `OverworldCommands.cs`, `RunCommands.cs`, `RunEndCommands.cs`

All three files register via a private `Register(command, methodName, hints)` helper that
resolves the handler by name via reflection and caches success per-command in a `Registered` dict
(failure retries, success does not re-register).

**crucible_overworld_state** — `()`
Handler: OverworldCommands.cs:144. Reg: OverworldCommands.cs:49. No params, matches.
Reads correct overworld observables from `AdventureState.MapState` (not the obsolete `GameRunData` fields).
Result: `roundCount`, `timeOfDayIndex`, `timeOfDay`, `weather`, `gameStage`, `totalRoundCount`,
`activeMapId`; obsolete fields printed under a `[obsolete, do not assert on these]` banner;
`interactionEnabled`, `userPickHexPending`; turn order (index 0=active); party w/ hex/ap/hasMoved/turnsPlayed.
Safety: read-only.

**crucible_path_preview** — `(string x, string y)`
Handler: OverworldCommands.cs:228. Reg: OverworldCommands.cs:50. Hint `{"x","y"}` matches.
Read-only reachability/cost check for a goal hex.
Result: `goal`, `start`, `isValidMove`, `actionPoints`, `pathSteps`, `pathCost`.
Safety: read-only.

**crucible_move** — `(string x, string y, string consumeActionPoints, string canBeAmbushedArg, string showEncounterMenuArg)`
Handler: OverworldCommands.cs:267-268. Reg: OverworldCommands.cs:51-53. Hint `{"x","y","consumeActionPoints(true|false)","canBeAmbushed(true|false|auto)","showEncounterMenu(true|false)"}` — 5 params, matches (cosmetic `Arg` suffix only).
Moves active character via `AdventureDirector._move(...)`, refuses if `IsValidMove` is false.
Result: path-preview block + `move invoked (...)`, or `REFUSED: IsValidMove is false, so the move was not attempted.`
Safety: **hazardous** — `_move` returns an unawaited Task; `canBeAmbushed=true|auto` can trigger unwanted combat; `showEncounterMenu=true` can open a UI that market/town/quest-board branches never close. Both default `false`.

**crucible_hex_info** — `(string x, string y)`
Handler: OverworldCommands.cs:350. Reg: OverworldCommands.cs:54. Hint `{"x","y"}` matches.
Inspects terrain/encounter/character on a hex.
Result: biome/zone/visibility; encounter guid/type/actions/properties/isCombat; character name/class.
Safety: read-only.

**crucible_overworld_end_turn** — `()`
Handler: OverworldCommands.cs:423. Reg: OverworldCommands.cs:55. No params, matches.
Ends active character's turn via `AdventureDirector._tryProceed(true)`.
Result: `active=<guid>`, `turnsPlayedBefore`, `roundCountBefore` — **before-only, no after** (async).
Safety: **hazardous** — `_tryProceed` is `async void`; failures appear only in the BepInEx log. Confirm via `TurnsPlayed`, not `RoundCount` (one end-turn ≠ one round; a round advances only once every party member's turn ends).

**crucible_interact** — `(string action)`
Handler: OverworldCommands.cs:494. Reg: OverworldCommands.cs:56. Hint `{"action (e.g. VENUE)"}` matches.
Reproduces the game's own two-step encounter-action sequence (`_tryShowEncounterMenu` then
`_performEncounterAction`), validating against `EncounterComponent.ActionList` first.
Result: `hex=`, `encounter=<guid>`, `action=`, `afterShowMenu: ...`, `routeBefore=... routeAfter=...`.
Safety: **hazardous** — `_performEncounterAction` is `async void`; venue transition lands ~0.75s later, so `routeAfter` read synchronously is often stale. Deliberately does not call `_performVenueAction` directly (combat-only, throws silently elsewhere).

**crucible_set_diorama** — `(string dioramaConfig)`
Handler: OverworldCommands.cs:80. Reg: OverworldCommands.cs:57. Hint `{"dioramaConfig"}` matches.
Sets `GameRunData.VenueState.Venue.DioramaName` (battlefield visual skin).
Result: `dioramaName <before> -> <after> changed=<bool>`.
Safety: must be set before combat begins — read at venue load, no effect on an in-progress fight; unknown name silently fails to resolve.

**crucible_tutorials_suppress** — `()`
Handler: RunCommands.cs:96. Reg: RunCommands.cs:67. No params, matches.
Harvests every tutorial `Id` from shipped `Tutorials.json`, adds to `Env.User.SeenTutorialIds`, clears `UserData.TutorialEnabled`.
Result: `seenTutorialIds {before} -> {after} (added {n})`, `harvested=`, `tutorialEnabled={was} -> {now}`.
Safety: safe; degrades gracefully if JSON missing.

**crucible_quest_complete_objective** — `(string questId, string objectiveIndex)`
Handler: RunCommands.cs:197. Reg: RunCommands.cs:68-69. Hint `{"questId","objectiveIndex or all"}` matches (2nd entry documents value domain, not an extra param).
Flags one or all objectives complete on every active-quest copy matching id, then invokes `AdventureDirector._tryCompleteQuests()` directly (NOT `_tryProceed`, which does not call it — see TRAPS).
Result: `quest=`, `copies=`, per-copy `objectives=N before=[flags] after=[flags]`, `changed=N`, `pumped=true/false [+pumpError=]`.
Safety: **hazardous** — the pump Task is not awaited; a fault inside is invisible except via `crucible_run_status`'s `lastPump=`.

**crucible_run_status** — `()`
Handler: RunCommands.cs:536. Reg: RunCommands.cs:70. No params, matches.
Read-only full-run assertion surface.
Result: `map=`, `round=`, `totalRounds=`, `stage=`, `timeOfDay=`, `lastPump=` (incl. fault+stack),
`inDungeon=`, `summaryShowing=`, active/completed/failed/future quests w/ objective flags + `roundsLeft=`,
a `<-- WARNING: STORY quest at 0 rounds left ends the run in DEFEAT` marker, `WIN quest completed: YES/no`.
Safety: read-only, safe even with no run loaded. **The only trustworthy victory signal** — route is useless (both win and loss land on ADVENTURE_SELECTION).

**crucible_quest_activate** — `(string questId)`
Handler: RunCommands.cs:328. Reg: RunCommands.cs:71. Hint `{"questId"}` matches.
Force-adds via `QuestHelper.CreateQuestState`, bypassing world-trigger/chaos gating; removes matching `FutureQuests` entry.
Result: `activated {id} type= endTrigger= objectives=[flags] mapId=`, FutureQuests removal status, no-op case: `"already active: {id} (no change)"`.
Safety: bogus id → caught error `CreateQuestState threw: ... (is '{id}' a real quest id?)`.

**crucible_quest_remove** — `(string questId)`
Handler: RunCommands.cs:424. Reg: RunCommands.cs:72. Hint `{"questId or all"}` matches.
Drops quests from ActiveQuests WITHOUT resolving — no rewards/dialogue/follow-ons.
Result: `activeQuests {before} -> {after} (removed {n})` + per-id lines.
Safety: safe, pure list mutation.

**crucible_encounters_clear** — `(string keyword)`
Handler: RunCommands.cs:475. Reg: RunCommands.cs:73. Hint `{"keyword or all"}` matches.
Deletes non-combat map encounter entities by substring match on ConfigName/Guid, or all.
Result: `encountersInspected=N removed=N` + removed names.
Safety: safe — explicitly skips anything carrying `CombatEncounterComponent`.

**crucible_endadventure_watch** — `(string action, string value)`
Handler: RunEndCommands.cs:207 — **2 params**. Reg: RunEndCommands.cs:149-150. Hint list `{"reset|snapshot on|snapshot off (optional)"}` is **1 entry** — ⚠️ **SOFT MISMATCH**: hint collapses two positional params into one usage string; arity of hint (1) ≠ handler arity (2).
Reads the latched outcome of the most recent `_endAdventure` call (via Harmony prefix); optionally resets the latch or toggles auto-snapshot.
Result: `installed=`, `autoSnapshot=`, `cameraFaultsSuppressed=`, `ended=`, and when latched `victory=`, `detail=`, `snapshotRunId=`.
Safety: read-only.

**crucible_summary_dismiss** — `()`
Handler: RunEndCommands.cs:281. Reg: RunEndCommands.cs:151. No params, matches.
Pages through the multi-page adventure-summary UI by clicking `next-btn` until the UIDocument is gone (max 6 presses).
Result: `summaryShowingBefore=`, `pressesMade=`, `summaryStillPresent=`, per-page click log.
Safety: only ever presses `next-btn` — **never `load-game-btn`**, which loads a save. Summary has 2 pages sharing the same button name (see TRAPS).

**crucible_encounter_leave** — `()`
Handler: RunEndCommands.cs:373. Reg: RunEndCommands.cs:152. No params, matches.
Force-closes a stuck encounter UI: `_closeEncounterMenuAsync` then `_stopEncounterAsync`.
Result: `_closeEncounterMenuAsync=<closed|not attempted|threw:...>`, `_encounterEntity <null|set> -> <null|STILL SET>`.
Safety: **hazardous** — fires un-awaited Task; effect not complete on return. Never clears market/town-services/quest-board/merc-guild panels (see TRAPS).

**crucible_party_gain_xp** — `(string xpEach, string slotOrAll)`
Handler: RunEndCommands.cs:446. Reg: RunEndCommands.cs:153-154. Hint `{"xpEach","slot or 'all' (optional)"}` matches.
Grants XP through real `ProgressionHelper.EntitiesGainXP` path (preserves heal, focus grant, LEVELED_UP event).
Result: `granted <xp> xp to <N> character(s)`, per-slot `before:`/`after:` levels, `abilityResults=<count>`.
Safety: `pConsiderMultiplier` deliberately `false` for determinism.

Support: `RunAccess.cs` — party-slot resolution is by enumeration order over live `PlayerComponent`
entities, **not a guaranteed persistent slot id**; every verb using it also reports the resolved
entity's DisplayName so identity can be confirmed. `TurnHooks.cs` — synthesizes `combat.turn`/`combat.phase`
which the engine holds as control flow, not data; degrades to `null`/`"UNKNOWN"` + warning if hooks fail to install rather than faking a zero.

### 2.3 Character / Items — `CharacterCommands.cs`, `DebugSpawnCommands.cs`, `DebugVerbCommands.cs`

**crucible_equip** — `(string slot, string thingConfigName)`
Handler: CharacterCommands.cs:79. Reg: CharacterCommands.cs:56-58. Hint `{"slot","thingConfigName"}` matches.
Resolves party slot, probes `EquipmentHelper.CanEquip`, then `EquipmentHelper.Equip(name, entity, -1, -1, GameRandom)`. Tiers -1/-1 = ask for item exactly as authored.
Result: `slot=`, `item=`, `canEquip=`, `weaponBefore=`, `weaponAfter=`, `changed=`.
Safety: unknown `thingConfigName` can NullReferenceException (EquipmentHelper.Equip throws on absent id).

**crucible_give_item** — `(string slot, string thingConfigName, string quantity)`
Handler: CharacterCommands.cs:140. Reg: CharacterCommands.cs:61-63. Hint matches.
`InventoryHelper.GiveByName(name, qty, Things, null, -1, -1, GameRandom)`.
Result: `slot=`, `item=`, `qty=`, `thingsBefore=`/`thingsAfter=`, `changed=`.
Safety: safe; invalid quantity defaults to 1.

**crucible_set_stat_value** — `(string slot, string stat, string value)`
Handler: CharacterCommands.cs:192. Reg: CharacterCommands.cs:66-68. Hint matches.
`CharacterHelper.SetStat(entity, stat, amount)` (stat name upper-invariant).
Result: `slot=`, `stat=`, `requested=`, `before=`/`after=`, `changed=`.
Safety: safe, no save-file interaction.

**crucible_status_add** — `(string slot, string statusConfigName, string duration)`
Handler: CharacterCommands.cs:240. Reg: CharacterCommands.cs:71-73. Hint matches.
Calls the 10-arg `InteractableHelper.ApplyStatus(entity, entity, null, "crucible_status_add", statusConfigName, GameRandom, results, true, false, durationArg)` — works outside combat.
Result: `slot=`, `status=`, `duration=` (or "(config default)"), `statusesBefore:`/`statusesAfter:`, `resultCount=`, `changed=`.
Safety: an unresolvable `statusConfigName` likely silently no-ops.

**crucible_debug_spawn** — `(string kind, string selector)`
Handler: DebugSpawnCommands.cs:64. Reg: DebugSpawnCommands.cs:51-53. Hint `{"kind(enemies|encounters)","selector or - to list"}` — positionally matches (labels are more descriptive than bare names, not a mismatch).
Invokes the game's own `DebugHelper.GenerateSpawnEnemiesDebugMenuButtons`/`GenerateSpawnEncountersDebugMenuButtons` to build the real debug-menu button set, then either lists names (`-`/empty) or invokes the matched button.
Result: `kind=`, `buttonCount=`, `valueType=`; listing: full sorted names "(list only; nothing was spawned)"; invoking: `chosen=`, `invoked=`, `invokeError=` if failed, `routeAfter=`, async note.
Safety: listing is inert. Invoking performs a real spawn — mutates live run, not reversible except by leaving/reloading. Requires overworld loaded.

**crucible_give** — `(string pConfigId, string pQty)`
Handler: DebugVerbCommands.cs:369. Reg: DebugVerbCommands.cs:60. Hint matches.
Wraps shipped `GetSpecificThing`; verifies party `Things.Count` sum before/after.
Safety: verified, safe.

**crucible_set_level** — `(string pSlot, string pLevel)`
Handler: DebugVerbCommands.cs:270. Reg: DebugVerbCommands.cs:59. Hint matches.
Grants XP shortfall via `ProgressionHelper.EntityGainXP`. **Levels only go UP** — a request ≤ current level is refused, not silently ignored.
Safety: safe; refuses downgrade requests explicitly.

**crucible_heal_party** — `()`
Handler: DebugVerbCommands.cs:172. Reg: DebugVerbCommands.cs:57. No params, matches.
`SetToMaxHealth` per party member, verified.
Safety: safe.

**crucible_kill_all** — `()`
Handler: DebugVerbCommands.cs:76. Reg: DebugVerbCommands.cs:56. No params, matches.
Kills **both sides** (filters to `CharacterComponent` entities — tiles would NPE otherwise).
Safety: **does not work while in combat** — walks `GameRunData.Entities`, a different set from `CombatState.Entities` (see TRAPS). Use `crucible_combat_wipe_enemies` in combat instead. Also: turn `crucible_godmode` off first or the read-back races the heal.

**crucible_end_phase** — `(string pPhase)`
Handler: DebugVerbCommands.cs:215. Reg: DebugVerbCommands.cs:58. Hint matches.
Invokes the live phase's own `_debugEndPhase`; verifies route change.
Safety: safe, verified.

**crucible_pin_seed** — `(string pSeed)`
Handler: DebugVerbCommands.cs:477. Reg: DebugVerbCommands.cs:62. Hint matches.
Direct `GameRandom.Seed` write on all live instances.
Safety: safe here (no cached derived state).

**crucible_time_advance** — `(string pSteps)`
Handler: DebugVerbCommands.cs:415. Reg: DebugVerbCommands.cs:61. Hint matches.
Calls `AdventureDirector._doEndTurn()` up to 20×.
Safety: **`_doEndTurn` returns a Task and is NOT awaited** — immediate read races the continuation; re-read before concluding failure.

**crucible_quest_state** — `()`
Handler: DebugVerbCommands.cs:534. Reg: DebugVerbCommands.cs:63. No params, matches. Read-only.

**crucible_quest_complete** — `(string pQuestIndex, string pObjectiveIndex)`
Handler: DebugVerbCommands.cs:589. Reg: DebugVerbCommands.cs:64. Hint matches.
Sets `CompletedObjectives[i]` then drives `_tryCompleteQuests()`.
Safety: async pump, same caveats as `crucible_quest_complete_objective`.

### 2.4 Config / Reflection — `ConfigCommands.cs`, `ReflectionCommands.cs`, `FixtureCommands.cs`

**crucible_loc** — `(string key)`
Handler: ConfigCommands.cs:358. Reg: ConfigCommands.cs:52-54. Hint `{"key or prefix"}` matches.
Reads `Lang.__translations`; exact key or `*`-prefix search.
Result: `totalKeys=`, per-match `key = value` lines with `matched=N`, or single `key=`/`PRESENT=`/`value:`.
Safety: read-only.

**crucible_class_config** — `(string classId)`
Handler: ConfigCommands.cs:77. Reg: ConfigCommands.cs:57-59. Hint matches.
Looks up `Env.Configs.Characters[classId]` (live merged config).
Result: `classId=`, `PRESENT=`, `passives:`, `things:`, `stats:`, `baseType=`, `rarity=`, `locKey=`.
Safety: read-only.

**crucible_status_config** — `(string statusId)`
Handler: ConfigCommands.cs:123. Reg: ConfigCommands.cs:62-64. Hint matches.
Looks up `Env.Configs.StatusEffects[statusId]`.
Result: `statusId=`, `PRESENT=`, `type=`, `duration=`, `tickCombat=`, `tickOverworld=`, `passives:`, `stats:`.
Safety: read-only. Trap: `"CURSE"` is an `eStatusEffectTypes` member, not a `Configs.StatusEffects` id — shipped recipes that applied it silently did nothing (see TRAPS).

**crucible_thing_config** — `(string thingId)`
Handler: ConfigCommands.cs:173. Reg: ConfigCommands.cs:66-68. Hint matches; trailing `*` also supported for prefix search.
Result: `thingId=`, `PRESENT=`, `type=`, `rarity=`, `stats:`; or `*` search: `prefix=...* matches=N` + list (capped 60).
Safety: read-only. Existence-check before `crucible_equip`/`crucible_give_item` — an absent id crashes those with NullReferenceException.

**crucible_get** — `(string pPath)`
Handler: ReflectionCommands.cs:90. Reg: ReflectionCommands.cs:72. Hint `{"path"}` matches.
Reflectively walks a dot-path (`Type.Member.Member[n]...`), reads static/instance fields/properties, supports `[n]` list indexing.
Result: rendered value — primitives/strings/enums as-is; collections as `Count=N [sample...]`; objects as `TypeName { field=val, ... }`.
Safety: read-only reflection, safe.

**crucible_invoke** — `(string pType, string pMethod, string pArgs)`
Handler: ReflectionCommands.cs:113. Reg: ReflectionCommands.cs:73. Hint `{"type","method","args (space-separated, or - for none)"}` matches.
Finds `Type.Method` by name+arg-count (incl. private/instance), resolves an instance via 3-strategy
fallback (`Instance`/`Current` static member → `FindObjectOfType` → private field on live `RouterMono`),
coerces string args, invokes. Un-awaited `Task` results report status only.
Result: `[instance: <strategy>] <rendered result>`.
Safety: **highest-hazard command in the entire set** — arbitrary reflective invocation of ANY method
(public/private, static/instance) on ANY loaded type, no allow-list. Can trigger arbitrary state mutation, UI transitions, or crashes.

**crucible_set** — `(string pPath, string pValue)`
Handler: ReflectionCommands.cs:150. Reg: ReflectionCommands.cs:74. Hint matches.
Walks a dot-path to the final segment, resolves it as a settable field/property, coerces + assigns.
Result: `old=<rendered> new=<rendered>`.
Safety: **high hazard** — arbitrary reflective field/property write, no allow-list, can corrupt engine/game state.

**crucible_load_run** — `(string pRunId)`
Handler: ReflectionCommands.cs:270. Reg: ReflectionCommands.cs:75. Hint matches.
Loads a save by id, bypassing the Load Game UI. **Safety-gated**: (1) `LoadRunGuard.TryValidateRunId`
requires non-empty explicit id; (2) `LoadRunGuard.IsKnownRunId` requires the id already exist in
`RouterHelper.Env.GameRuns` — **never falls back to "last played"/"first"/"newest"**. Invokes
`AdventureDirector._loadSave(runId, runId+".ftk2", CancellationToken)` via reflection (falls back
to `DungeonDirector._loadSave`). No pre-route to ADVENTURE_SELECTION (see TRAPS — that used to break it).
Result: `loaded=` (best-effort within a bounded 500ms settle-retry), evidence string, `invoke=[strategy] rendered-result`, `route=`, async re-check note.
Safety: guarded against ever touching an unknown/implicit run id. Loads whatever known id is passed, replacing live in-memory session state.

**crucible_party_list** — `()`
Handler: FixtureCommands.cs:78. Reg: FixtureCommands.cs:50-52. No params, matches.
Result: `party count=`, per slot `classId=`, `name=`, `hp=`, `level=`, `guid=`.
Safety: read-only.

**crucible_party_set_class** — `(string slot, string classConfigName)`
Handler: FixtureCommands.cs:117. Reg: FixtureCommands.cs:55-57. Hint matches.
Directly overwrites `CharacterComponent.ConfigName` via reflection (class id only, not equipment/things/stat modifiers), then `CharacterHelper.SetToMaxHealth`.
Result: `slot=`, `classBefore=`/`classAfter=`, `changed=`, `hpBefore=`/`hpAfter=`, `healedToMax=`.
Safety: does NOT touch weapon/abilities — old-class abilities persist until `crucible_equip` is also called (see TRAPS).

**crucible_fixture_save** — `(string label)`
Handler: FixtureCommands.cs:193. Reg: FixtureCommands.cs:60-62. Hint matches.
Persists live `GameRunData` via `SaveGameHelper.WriteSaveData(newRunId, gameRun, user)` under a **freshly generated GUID run id** — never an existing one.
Result: `wrote fixture label=`, `newRunId=`, `runsBefore=`, `taskStarted=` (async, not awaited).
Safety: **explicitly architecturally incapable of overwriting** — no "overwrite" parameter exists. Still writes new files each call.

**crucible_refresh_saves** — `()`
Handler: FixtureCommands.cs:250. Reg: FixtureCommands.cs:65-67. No params, matches.
`SaveGameHelper.ListGameRunsSync()` → rewrites `Env.GameRuns`, projecting ids out of the returned `List<GameRunFileInfo>`.
Result: `gameRunsBefore=`, `gameRunsAfter=`, `changed=`.
Safety: safe, only rewrites in-memory list.

**crucible_fixture_list** — `()`
Handler: FixtureCommands.cs:309. Reg: FixtureCommands.cs:70-72. No params, matches. Read-only enumeration of `Env.GameRuns`.

### 2.5 Chaos — `ChaosCommands.cs`

**crucible_chaos_freeze** — `(string pOnOff)`
Handler: ChaosCommands.cs:141. Reg: ChaosCommands.cs:128. Hint `{"on|off"}` matches.
Toggles a static `_frozen` flag. A Harmony Prefix on every overload of `AdventureHelper.ModifyChaosLevel`
returns `false` (skip original) while frozen, suppressing all chaos-level increases **globally for the whole game session**.
Result: `api=...`, `frozenBefore=`, `frozenAfter=`, `changed=`, `chaosHistoryCountNow=`.
Safety: **global mutation**, not scoped to one run. Reversible by toggling off. If `ModifyChaosLevel` isn't found, reports "chaos freeze unavailable" rather than silently no-opping.

**crucible_chaos_state** — `()`
Handler: ChaosCommands.cs:169. Reg: ChaosCommands.cs:129. No params, matches. Read-only.
Reads `AdventureState.MapState.ChaosState` — **NOT** the obsolete `GameRunData.ChaosState` (see TRAPS).
Result: `frozen=`, `patched=`, `chaosHistoryCount=`, `maxChaos=`, `lastChaosRoundAdded=`, `startedAtRound=`, `roundCount=`. No scalar "current chaos level" exists.

**crucible_chaos_advance** — `(string pConfigName)`
Handler: ChaosCommands.cs:234. Reg: ChaosCommands.cs:130. Hint matches.
Invokes `AdventureDirector._processWorldTrigger(CHAOS_ACTIVE, pConfigName, ...)` via reflection —
the same path the game's own CHAOS_ACTIVE world trigger uses. **Refuses empty/whitespace config
name up front** (empty arg is a live NullReferenceException downstream, not a harmless no-op).
Result: `configArg=`, `configNameBefore=`/`After=`, `chaosHistoryCountBefore=`/`After=`, `changed=`, `taskState=` (not awaited), `availableChaosConfigs(...)`.
Safety: mutates live run's chaos stage/state only (not global). Unknown config name → caught, reported error.

### 2.6 UI / Input — `UiCommands.cs`, `KeyboardCommands.cs`, `MouseCommands.cs`, `GamepadCommands.cs`, `InputBackgroundCommands.cs`, `InputFocusGateCommands.cs`

All UiCommands.cs registrations at UiCommands.cs:75-101; params match hints for all 10.

**crucible_ui_dump** — `(string filter, string kinds)` — UiCommands.cs:112 / reg:80. Reflective UIToolkit
tree dump, `"-"`=all. Hard cap `MaxElementBudget=50000` (line 1207) to protect the 10s RPC budget. Safe, read-only.

**crucible_ui_click** — `(string selector)` — UiCommands.cs:166 / reg:81. Clicks first visible Button
via a 4-strategy activation ladder. **Substring match once hit `continue-btn` and resumed the
owner's live co-op campaign** — a forbidden-element check was added at UiCommands.cs:210-215
afterward. Note: `ftk2_pick`'s Node-side guard does NOT cover `ftk2_exec`→this path.

**crucible_ui_focus** — `(string selector)` — UiCommands.cs:351 / reg:82. Focus() + before/after report. No forbidden-element check (focus-only, no activation).

**crucible_ui_nav** — `(string direction)` — UiCommands.cs:1069 / reg:83. NavigationMoveEvent up/down/left/right.

**crucible_ui_submit** — `(string _unused)` — UiCommands.cs:1114 / reg:84. NavigationSubmitEvent to focused element. Dummy param is deliberate.

**crucible_ui_cancel** — `(string _unused)` — UiCommands.cs:1129 / reg:85. NavigationCancelEvent.

**crucible_ui_press** — `(string selector)` — UiCommands.cs:457 / reg:86. Full press flow: exact-beats-substring
match, verified focus, 4-strategy ladder, screen-delta check. Refuses forbidden elements before focus/activation.

**crucible_ui_where** — `(string _unused)` — UiCommands.cs:586 / reg:87. Read-only: docs, focus, on-screen buttons.

**crucible_dialogue_advance** — `(string maxPressesRaw)` — UiCommands.cs:652 / reg:88. Loop-presses
continue prompts, stops on no-change, using an independent full-element-set diff (not the ladder's own verdict).

**crucible_dialogue_choose** — `(string selector)` — UiCommands.cs:798 / reg:89. Lists/activates dialogue
options; `"-"` lists only. Refuses forbidden elements.

**crucible_key** — `(string key)` — KeyboardCommands.cs:81 / reg:76. Presses/releases a key on the real
`Keyboard.current` (never `AddDevice` — an unpaired keyboard is silently ignored). **Release scheduled on
a later frame**, never in the same call (see TRAPS — same-frame press+release is invisible to the game).
Result: device, key, `pressed=`, `releaseScheduledInFrames=`.

**crucible_mouse_click** — `(string x, string y)` — MouseCommands.cs:53 / reg:46. Queues a move-only
state first, then press; release scheduled later. Needed for 3D overworld hexes UI/pad paths can't
reach. Result deliberately reports `released=false` ("scheduled", not "sent").

**crucible_pad** — `(string button)` — GamepadCommands.cs:102 / reg:82. Tap; release scheduled across frames.

**crucible_pad_hold** — `(string button, string milliseconds)` — GamepadCommands.cs:144 / reg:83. Hold, clamped to `PadHoldDuration.MaxMs`.

**crucible_pad_stick** — `(string stick, string direction)` — GamepadCommands.cs:195 / reg:84.
`BuildStickState` **replaces the full device state** (GamepadCommands.cs:841-847) — does not preserve buttons or the other stick.

**crucible_pad_pair** — `(string playerIndex)` — GamepadCommands.cs:254 / reg:85. Pairs virtual pad
into FTK2's own `InputPlayer` list (FTK2 does not use Unity's `InputUser` system at all); `"-"`=primary.
An unpaired device is silently ignored or spawns a spurious P2.

**crucible_input_devices** — `()` — GamepadCommands.cs:439 / reg:86. Read-only diagnostic.

**crucible_input_background** — `(string pOnOff)` — InputBackgroundCommands.cs:117 / reg:63. Sets
`Application.runInBackground` + `InputSystem.settings.backgroundBehavior` (enum member picked
reflectively); captures originals so "off" restores exactly. Also auto-applied per tick when ForceSinglePlayer is on.

**crucible_input_focus_gate** — `(string pOnOff)` — InputFocusGateCommands.cs:265 / reg:162. Harmony
prefix on `InputController.RequestDisable` suppressing only `LOST_FOCUS`; releases an already-held
entry. Force-enabled directly in `Initialize()` because the tick path was measured to leave it down.

**crucible_input_state** — `()` — InputFocusGateCommands.cs:298 / reg:163. Read-only dump of `_disableRequesters`.

Support: `CaptureService.cs` — see SCREENSHOTS section. `RpcServer.cs` — HTTP route table (see
below). `CruciblePlugin.cs` — plugin entry; hosts the per-tick registration poll and the
`Instance == null` Unity fake-null gotcha (see TRAPS).

**No true hint/handler-signature MISMATCHES were found anywhere except `crucible_endadventure_watch`**
(hint arity 1 vs. handler arity 2 — a soft/labeling mismatch, not a wrong-order-of-args bug).

---

## 3. THE MCP LAYER

`FTK2.Crucible/mcp/server.js` exposes tools as **a static array built once at module load**:

```js
const TOOLS = [ { name: 'ftk2_list_instances', ... }, ... ];   // server.js:85
if (msg.method === 'tools/list') {
  send({ jsonrpc: '2.0', id: msg.id, result: { tools: TOOLS } });   // server.js:1144-1146
}
```

This array is **not** re-derived from the game's live command registry at request time — a new
C# command does not become an `ftk2_*` tool until `server.js` is edited and the MCP server process
is reloaded. `ftk2_list_commands` queries `GET /commands` (the game's live registry) but that's a
separate read-only listing tool, not the MCP schema itself.

Shared `/exec` wrapper: `execText(inst, command, args)` (server.js:895-898) →
`rpc(inst,'POST','/exec',{command,args})` → returns `res.result || ''`.

### Route-based tools (not `/exec`)
- `ftk2_health` → `GET /health`
- `ftk2_list_commands` → `GET /commands`
- `ftk2_state` → `GET /state?schema=v2`
- `ftk2_read_trace` → `GET /trace?n=N`
- `ftk2_screenshot` → `POST /screenshot` (see §5)
- `ftk2_compare_state` → multi-instance `/state` + digest
- `ftk2_list_instances` → local, pings `/health` per known instance

### Direct 1:1 `ftk2_*` → `crucible_*` mappings (via `/exec`)
`ftk2_kill_all`→`crucible_kill_all`, `ftk2_heal_party`→`crucible_heal_party`,
`ftk2_debug_end_phase`→`crucible_end_phase`, `ftk2_set_level`→`crucible_set_level`,
`ftk2_give`→`crucible_give`, `ftk2_time_advance`→`crucible_time_advance`,
`ftk2_pin_seed`→`crucible_pin_seed`, `ftk2_quest_state`→`crucible_quest_state`,
`ftk2_quest_complete`→`crucible_quest_complete`, `ftk2_party`→`crucible_party_list`,
`ftk2_set_class`→`crucible_party_set_class`, `ftk2_set_hex`→`crucible_party_set_hex`,
`ftk2_abilities`→`crucible_party_abilities`, `ftk2_party_gain_xp`→`crucible_party_gain_xp`,
`ftk2_fixture_save`→`crucible_fixture_save`, `ftk2_reveal_map`→`crucible_reveal_map`,
`ftk2_map_encounters`→`crucible_map_encounters`, `ftk2_class_config`→`crucible_class_config`,
`ftk2_status_config`→`crucible_status_config`, `ftk2_spawn`→`crucible_debug_spawn`,
`ftk2_refresh_saves`→`crucible_refresh_saves`, `ftk2_godmode`→`crucible_godmode`,
`ftk2_combat_spawn`→`crucible_combat_spawn`, `ftk2_combat_wipe_enemies`→`crucible_combat_wipe_enemies`,
`ftk2_combat_restore_actions`→`crucible_combat_restore_actions`, `ftk2_loc`→`crucible_loc`,
`ftk2_summary_dismiss`→`crucible_summary_dismiss`, `ftk2_endadventure_watch`→`crucible_endadventure_watch`,
`ftk2_run_status`→`crucible_run_status`, `ftk2_quest_activate`→`crucible_quest_activate`,
`ftk2_quest_complete_objective`→`crucible_quest_complete_objective`,
`ftk2_tutorials_suppress`→`crucible_tutorials_suppress`, `ftk2_encounter_leave`→`crucible_encounter_leave`,
`ftk2_chaos_freeze`→`crucible_chaos_freeze`, `ftk2_chaos_state`→`crucible_chaos_state`.
`ftk2_end_phase` calls the **shipped game command** `EndPhase` directly, not a `crucible_*` one.

### Composite helpers (multi-command sequences, not 1:1 wraps)
`ftk2_clear_gates`, `ftk2_boot_to_run`, `ftk2_dialogs`, `ftk2_dismiss`, `ftk2_screen`, `ftk2_saves`,
`ftk2_new_game`, `ftk2_pick`, `ftk2_wait_screen`.

### crucible_* commands with NO ftk2_* wrapper — reachable only via `ftk2_exec`
The following have **no dedicated tool** and must go through `ftk2_exec {command: "...", args: [...]}`:
`crucible_force_combat`, `crucible_force_encounter`, `crucible_engage_encounter`, `crucible_use_ability`,
`crucible_use_ability_auto`, `crucible_win_combat`, `crucible_combat_end_turn`, `crucible_combat_snapshot`,
`crucible_list_abilities`, `crucible_list_targets`, `crucible_overworld_state`, `crucible_path_preview`,
`crucible_move`, `crucible_hex_info`, `crucible_overworld_end_turn`, `crucible_interact`,
`crucible_set_diorama`, `crucible_quest_remove`, `crucible_encounters_clear`, `crucible_chaos_advance`,
`crucible_equip`, `crucible_give_item`, `crucible_set_stat_value`, `crucible_status_add`,
`crucible_thing_config`, `crucible_get`, `crucible_set`, `crucible_invoke`, `crucible_load_run`,
`crucible_fixture_list`, `crucible_ui_dump`, `crucible_ui_click`, `crucible_ui_focus`, `crucible_ui_nav`,
`crucible_ui_submit`, `crucible_ui_cancel`, `crucible_ui_press`, `crucible_ui_where`,
`crucible_dialogue_advance`, `crucible_dialogue_choose`, `crucible_key`, `crucible_mouse_click`,
`crucible_pad`, `crucible_pad_hold`, `crucible_pad_stick`, `crucible_pad_pair`, `crucible_input_devices`,
`crucible_input_background`, `crucible_input_focus_gate`, `crucible_input_state`.

MCP coverage summary: roughly one-third of `crucible_*` commands have a dedicated `ftk2_*` tool;
the majority — including nearly all of combat driving, overworld movement, UI/input, and reflection —
are exec-only.

---

## 4. TRAPS

These are the gotchas that were paid for in real debugging time, mined from source comments.
Quotes are verbatim from the cited file:line.

### 4.1 `crucible_use_ability` has no entity parameter
`(abilityName, x, y)` only. `FireAbility` internally calls `ResolveEntity(null, ...)` with a
hardcoded `null` — CombatDriveCommands.cs. It always acts as **whoever's turn it currently is**
(`CombatPhase._activeCharacterEntity`, falling back to the first `RoundEntities` entry). There is
no way to target a non-active entity with this command, even though the underlying `ResolveEntity`
helper does support guid lookup (used elsewhere by `crucible_list_abilities`/`crucible_list_targets`).
To act as a specific entity, it must first become that entity's turn.

### 4.2 Ability/move results resolve asynchronously — reading immediately shows stale state
Confirmed across many commands. The pattern: the C# handler invokes a game method that returns an
un-awaited `Task` (deliberately — blocking the main game thread on it would deadlock the pump), and
the handler's `LastResult` is written before that Task completes. Examples with an explicit "NOTE"
in source:
- `crucible_use_ability`/`crucible_use_ability_auto`: "`_performAiDecision` returns a Task that is
  NOT awaited (blocking the game thread would deadlock the pump). Re-read `crucible_combat_snapshot`
  after a moment." — CombatDriveCommands.cs (FireAbility result block).
- `crucible_combat_end_turn`: "`_nextTurn` returns a Task that is not awaited; re-read the snapshot."
- `crucible_move`: "`_move` returns a Task that is NOT awaited — it plays a visual stack and then
  chains into the encounter menu or end-of-turn. Re-read `crucible_overworld_state` after a few seconds." — OverworldCommands.cs:332-334.
- `crucible_overworld_end_turn`: "`_tryProceed` is async void — it returns immediately and any
  failure appears in the BepInEx log." — OverworldCommands.cs:455-457.
- `crucible_interact`: "`_performEncounterAction` is async void and the venue transition waits ~0.75s
  before raising the route change. Re-read state." — OverworldCommands.cs:592-593.
- `crucible_time_advance`: `_doEndTurn` returns Task, not awaited; "an immediate read can race the
  async continuation." — DebugVerbCommands.cs:432-436.
- `crucible_quest_complete_objective`/`crucible_quest_complete`: the completion pump Task is not
  awaited; a fault inside it is invisible except via `crucible_run_status`'s `lastPump=`.

**Rule of thumb: after any command whose result text mentions a Task/async note, wait (poll or sleep
briefly) and re-read the relevant snapshot/state command before asserting on the outcome.**

### 4.3 The combat snapshot header's `activeGuid=<guid>` causes false matches
`crucible_combat_snapshot`'s output begins with a header line containing `activeGuid=<guid>`
(CombatDriveCommands.cs, `ActiveEntityGuid()`). If you search/grep the **whole snapshot text** for
a target guid, this header line will false-positive-match even when that guid is not present as an
actual combatant row. Real per-combatant rows have the reliable shape
`<guid> name=... class=... group=... hp=... tile=... pa=... sa=... dead=bool statuses=...` — so
**require both `" name="` and `" hp="` in a line** before treating it as a combatant match, never
match on the guid substring alone. (Note: no source comment documents this specific trap explicitly —
it is an inferable risk from the snapshot's format, not a measured/recorded incident like the others.)

### 4.4 The snapshot's tile list is capped — and the cap can silently hide an oversized grid
The code caps the printed tile list at **120 entries** (`if (tiles <= 120)` in CombatDriveCommands.cs),
but the status text printed alongside it still says `"(showing first 30)"` — a real discrepancy
between the actual cap and the message describing it. Source comment on the ORIGINAL bug this
guards against: "30 was too low to see a whole board: a standard venue already has 30 tiles counting
the neutral ones, so the list truncated exactly at the cap and made an ENLARGED grid look identical
to a standard one." (CombatDriveCommands.cs, near line 125-129). **Do not trust the cap message text
— always compare `tiles=N` (the reported total count) against however many rows you actually parsed,
not against the printed "(showing first ...)" number.**

### 4.5 Commands fail to register for the first several ticks of game boot
`CommandLineHelper._commandRegistry` is null until the game's own `CommandLineHelper.Initialize()`
runs (which happens when `RouterMono` starts, not at plugin `Awake`). Registering before that throws
`NullReferenceException` from inside the game's own code. Every `TryRegister()` across every file
is polled every tick from `MainThreadPump`, does NOT cache failure, and self-heals the moment the
game's `Initialize()` has run:
> "CommandLineHelper._commandRegistry is still null on the first several MainThreadPump ticks ...
> so GameBridge.RegisterCommand throws/logs a NullReferenceException per attempt during that window.
> TryRegister is polled every tick and does NOT cache failure, so it keeps retrying and self-heals
> the moment the game's Initialize() runs." — InputBackgroundCommands.cs:35-40 (near-identical text
> at UiCommands.cs:23-29, ChaosCommands.cs:69-71, InputFocusGateCommands.cs:79-86, CruciblePlugin.cs:170-172, ReflectionCommands.cs:50-54.

**"Command not found" seen only in the first second or two of a session is this boot race self-healing,
not a missing/broken command. If it persists after boot has clearly settled (health-check shows
`pumpAlive`), that's a real problem.**

### 4.6 Other measured traps worth knowing before you hit them

- **`crucible_force_combat` does not work.** `RouterMono.Route(COMBAT, force:true)` flips the route
  enum but `CombatPhase.Initialize` needs a Scene/Diorama/VenueCameraController/VenueState that only
  a real transition builds — the fight never initializes (no CombatState, no combatants, no UI), and
  repeating it wedges the main thread. Use `crucible_force_encounter` + `crucible_engage_encounter`
  instead (CombatCommands.cs:538-546, 12-35).

- **`crucible_kill_all` does not work in combat.** It walks `GameRunData.Entities`; the fight runs on
  `CombatState.Entities`, a different set of objects. Measured against a 74-combatant fight:
  `entities=74 deadBefore=0 deadAfter=0 changed=False` — "a verb that looks like it works and does
  nothing." Use `crucible_combat_wipe_enemies` in combat.

- **`crucible_win_combat`'s `EndPhase` vs. `CombatState.EndCombatEarly` are opposites.** `EndPhase`
  removes all enemies then evaluates a real win. `EndCombatEarly = true` ends the fight with enemies
  still alive, which evaluates as a **loss**. "The two are easy to confuse and behave oppositely."

- **An out-of-actions ability fails completely silently.** `crucible_use_ability` reports the ability
  name and target exactly the same whether or not it actually resolved. The only tell is `pa=0` in
  the snapshot or `usable=False` in `crucible_list_abilities`. Measured: an attack with `pa=0` returned
  `resultCount=0` and logged nothing. Call `crucible_combat_restore_actions` first if actions might be exhausted.

- **`crucible_party_set_class` does not swap the weapon.** Abilities come from the EQUIPPED WEAPON,
  not the class. A character swapped to a new class via this command keeps offering the OLD class's
  abilities until `crucible_equip` is also called with the new class's weapon. Measured: an entity
  swapped to `CF_EOR_ARCANIST` still offered `HOOK_LEFT/HOOK_PULL/HOOK_RIGHT` because it was still
  holding the Thief's whip. "A class fixture is not valid until the matching weapon is equipped."

- **Class-swap HP/max mismatch.** Max HP is computed from the class config; CurrentHealth is stored
  on the component — so a class swap alone leaves old HP against new max. Observed: a Corsair (34)
  swapped to Chronomancer rendered as "34 / 21" in the HUD. `crucible_party_set_class` heals to max
  afterward specifically because of this.

- **`crucible_chaos_state` must read `AdventureState.MapState.ChaosState`, not `GameRunData.ChaosState`.**
  The latter is `[Obsolete]` and nothing writes it anymore. Reading the alias reports a chaos level
  that can never move no matter what the game does. `GameRunData.RoundCount` is obsolete the same way
  (`AdventureState.MapState.RoundCount` is live).

- **`crucible_chaos_advance` refuses an empty/whitespace config name on purpose** — that specific
  input sets `ChaosState` to null, and `AdventureHelper.ModifyChaosLevel` dereferences
  `ChaosState.ChaosHistory` with no null guard on the next chaos tick: a live NullReferenceException,
  not a harmless no-op.

- **`crucible_chaos_freeze` is a permanent, global Harmony patch**, not scoped to the current run —
  it affects chaos escalation for the entire game session once installed.

- **`crucible_fixture_save` can never overwrite a save.** It always writes under a freshly generated
  GUID run id; there is deliberately no "overwrite" parameter. This exists specifically so the
  harness cannot destroy the owner's real co-op campaign save.

- **`crucible_load_run` never falls back to an implicit run id.** It requires the id be explicit,
  non-empty, and already present in `RouterHelper.Env.GameRuns` — never "last played"/"first"/"newest".
  Also: **do not pre-route to ADVENTURE_SELECTION before calling it** — the game's own Load action
  never does that, and routing there first tears down the live `AdventureDirector`, causing `_loadSave`
  to run against a director being destroyed. Measured: `loaded=False` with the previous run id still
  selected, despite a dispatched Task.

- **A save file added/copied in while the game runs is invisible until `crucible_refresh_saves` runs**
  — `Env.GameRuns` is populated once at startup. This presents as "the load did not take" with no
  error anywhere.

- **`crucible_invoke`/`crucible_get`/`crucible_set` — parameter type vocabulary is limited.**
  `CommandLineHelper` marshals raw args into typed handler parameters using a fixed vocabulary
  (int/float/double/bool/string/Vector2/Vector3). Registering a handler with e.g. a `string[]`
  parameter throws `TargetInvocationException` — not in the vocabulary.

- **`crucible_ui_click`/`crucible_ui_press` substring matching is dangerous.** A selector like
  `"Continue"` once substring-matched `continue-btn` and **resumed the owner's live co-op campaign.**
  A forbidden-element check now guards `crucible_ui_click`/`crucible_ui_press`/`crucible_dialogue_choose`,
  but this guard does **not** extend to arbitrary reflective paths (`crucible_invoke`, `crucible_set`)
  reaching the same buttons another way. The MCP-layer `ftk2_pick` guard also does not cover
  `ftk2_exec` calling `crucible_ui_click` directly.

- **`crucible_summary_dismiss` — the adventure summary is two pages sharing the same button name.**
  "ADVENTURE COMPLETE" then "PLAYER SUMMARY" both use `next-btn` (only its label text differs,
  "Continue" then "Finish"). A single press looks successful and leaves the run stuck on page two,
  still not unwound. The command loops up to 6 presses for this reason. Never press `load-game-btn`
  on this screen — it loads a save.

- **A winning adventure deletes its own save.** `GameplayDirectorBase._endAdventure` ends with
  `if (summaryTask.Result && canLoadSave && pIsVictory) _removeSave();` — so on a normal-difficulty
  win, the run's save file is gone by the time anything can read it. `crucible_endadventure_watch`'s
  Harmony prefix snapshots the outcome BEFORE this happens, because it's the last moment the save
  still exists. MASTER/GAUNTLET/DARK_CARNIVAL delete it even earlier.

- **Route is not a reliable win/loss signal.** Both victory and defeat land on ADVENTURE_SELECTION
  (only `OUTRO_ADVENTURES` diverts to `CINEMATIC_CREDITS`). `crucible_run_status`'s `WIN quest
  completed:` line and `crucible_endadventure_watch`'s latched `victory=` are the only trustworthy signals.

- **`crucible_quest_complete_objective` must call `_tryCompleteQuests()` directly, not `_tryProceed`.**
  `_tryProceed(true)` was measured to write the completion flag, report success, and never actually
  move the quest out of `ActiveQuests` — `_tryProceed` doesn't call `_tryCompleteQuests` on any branch;
  it just ends the turn and returns.

- **Quest `Objectives` is a flat pair array** — `[verb, arg, verb, arg, ...]` — so objective count is
  `Length / 2` and `CompletedObjectives` is indexed by `i / 2`. Indexing the pair array directly by
  objective index is the obvious mistake.

- **Input timing: press+release in the same call is invisible to the game.** Keyboard/gamepad/mouse
  releases are deliberately scheduled on a **later frame**, never in the same call that presses. A
  same-frame press-then-release completes within one Unity frame, so the game never gets an `Update`
  where the input reads as held — its Input Actions never see a press→release transition and nothing
  happens. Measured live: `crucible_key` (frame-spanning) moved menu focus and activated buttons;
  `crucible_pad` (same-call release, before the fix) did neither.

- **OS-level input injection does not work against this game.** `SendKeys`/`keybd_event` verified not
  to work; raw UIToolkit `NavigationMoveEvent` sent to a document root also does nothing (the game
  drives focus itself). `SetForegroundWindow`/`ShowWindow`/`BringWindowToTop`/`AttachThreadInput` all
  verified not to steal focus for a background process either.

- **`Application.runInBackground = true` alone is not enough for background input.** Unity's Input
  System has its own separate focus policy (`InputSystem.settings.backgroundBehavior`), independently
  toggled by `crucible_input_background`.

- **FTK2's `InputController` gates input independently of Unity**, via a reference-counted
  disable-reason set (`RequestDisable`/`ReleaseDisable`). A stray outstanding `LOST_FOCUS` disable
  can leave input off after the window loses and regains focus; `crucible_input_focus_gate` patches
  this and is force-enabled directly in `Initialize()` because relying on the per-tick auto-apply
  path was measured to leave the gate down at startup.

- **Unity's fake-null (`operator==`) broke auto-apply logic.** A destroyed component compares equal
  to `null` while the managed reference is still alive, so `Instance == null` checks silently became
  true a moment after boot and every guarded `AutoApplyTick` returned early forever — the focus gate
  and Input System background behavior were both measured to never auto-apply for this reason.
  (CruciblePlugin.cs:30-41)

- **`crucible_pad_stick` replaces the entire virtual gamepad device state**, not just the stick axis —
  it does not preserve currently-held buttons or the other stick's position.

- **`crucible_pad_pair` matters because FTK2 does not use Unity's `InputUser` pairing system.** A
  newly-added device not already in an `InputPlayer.PlayerDevices` list is either silently ignored
  or auto-joins as a **new** InputPlayer (observed as a spurious P2 on the party screen) — never
  routed to the existing P1.

- **`crucible_combat_spawn` needs a redraw trigger.** `CombatPhase._refreshTimeline` is only called
  on turn transitions, so a freshly spawned creature has no turn-order banner icon until something
  else triggers a redraw. Similarly, `ADD_CHARACTER` alone does not create the actor GameObject the
  renderer needs — that comes from a separate `CHARACTER_ADDED` ability-result hand-off. **Assert a
  spawn on `crucible_combat_snapshot`, not on a screenshot** — the combatant can be real (takes turns)
  before it's ever visually drawn.

- **`GameRandom` from `AdventureDirector._gameRandom` is null for a while after a load.** This made
  `EquipmentHelper.Equip` throw a NullReferenceException in a sweep immediately after loading, while
  the identical call worked fine by hand a few minutes later — "a timing-dependent failure that reads
  as a broken command." A non-null fallback (`ResolveGameRandom`) is used everywhere that matters.

- **Custom skill (`SKILL_CF_*`) visibility split.** A custom skill is never reachable through
  `CharacterHelper.GetPassiveSkills` (maps to the fixed `eSkills` enum, which cannot gain runtime
  members) even when it's working correctly. Its **presence** must be asserted via config
  (`crucible_class_config`/`crucible_status_config`); its **effect** must be asserted in combat.
  Asserting the wrong one produces a confident false negative.

- **Item ids are not guessable** — 2418 of them, no naming convention that survives contact with
  shipped data. Always search with `crucible_thing_config <prefix>*` rather than guessing a key.

---

## 5. RECIPES

All examples assume the harness is up (`GET /health` returns `pumpAlive=true`) and a run is loaded.
Prefer the MCP tool where one exists; fall back to `ftk2_exec {"command": "...", "args": [...]}` for
exec-only commands (see §3's uncovered list).

### Get into a fight (the working path — NOT `crucible_force_combat`)
```
crucible_force_encounter                 # spawn an encounter at the party's current hex
crucible_engage_encounter                # drive ADVENTURE -> VENUE -> COMBAT
# wait ~1-2s, transition is async
crucible_combat_snapshot                 # confirm combat.active / combatants present
```
If the party is standing in unrevealed fog, `crucible_force_encounter` may have nothing to target —
call `crucible_reveal_map Visible` first.

### Spawn enemies into an in-progress fight
```
crucible_combat_spawn <characterConfig> <count> <group>   # group 1 = enemies, default
# wait briefly (visual hand-off is a separate async step)
crucible_combat_snapshot                 # assert on THIS, not a screenshot
```
To spawn a NEW overworld encounter instead (not into a running fight), use `crucible_debug_spawn
encounters -` to list buttons, then `crucible_debug_spawn encounters <button>`.

### Move a character and confirm it
```
crucible_path_preview <x> <y>            # check isValidMove=true first
crucible_move <x> <y> true false false   # consume AP, no ambush, no menu
# wait a few seconds -- _move's Task is not awaited
crucible_overworld_state                 # confirm party hex + turnsPlayed advanced
```
For a teleport with no game-side arrival logic (fixture setup, not a "real" move):
```
crucible_party_set_hex <x> <y>
crucible_reveal_map Visible              # otherwise the party lands in fog
```

### Apply a status and watch it decay
```
crucible_status_add <slot> <statusConfigName> <duration>   # outside combat
# or, in combat, use crucible_use_ability targeting a status-applying ability
crucible_combat_snapshot                 # confirm status appears in the combatant row's statuses=
crucible_combat_end_turn                 # advance a turn
crucible_combat_snapshot                 # confirm duration ticked down
```

### Summon a creature (mid-combat add, as a player-side unit)
Same as "spawn enemies" above but target a tile in the party's group (group 0) rather than the
enemy group -- allegiance is decided by the destination tile's `GroupIndex`, not by an explicit flag:
```
crucible_combat_spawn <characterConfig> 1 0
crucible_combat_snapshot
```

### Assert a class trait fired
```
crucible_class_config <classId>          # confirm the passive/skill is authored (existence)
crucible_party_set_class <slot> <classId>
crucible_equip <slot> <weaponConfigForClass>   # required -- class swap alone keeps old abilities
crucible_party_abilities <slot>          # confirm passives(entity) now matches passives(configName)
# drive combat, trigger the condition (e.g. crucible_use_ability against this character)
crucible_combat_snapshot                 # confirm the EFFECT (hp/status/stat change), not just presence
```
Presence in config and effect in combat are two different assertions -- don't conflate them (see TRAPS).

### Take a screenshot
See §6 below -- do not call `crucible_screenshot` via `/exec`; there's no such command.

---

## 6. SCREENSHOTS

**There is no `crucible_screenshot` RPC command.** Screenshots are a **dedicated HTTP route**,
`POST /screenshot`, separate from the generic `/exec` command-dispatch endpoint. Calling
`crucible_screenshot` through `/exec` returns `HTTP 400 Bad Request` because `/exec` rejects/fails
on a command name that was never registered — there simply is no such registered command.

### Route table (RpcServer.cs:144-154)
`/`, `/health`, `/commands`, `/exec`, `/state` (+`?schema=v2`), `/screenshot`, `/trace`. Anything
else → 404 `unknown_endpoint`.

### The C# side
- `RpcServer.cs:151`: `case "/screenshot": HandleScreenshot(ctx, body); return;`
- `RpcServer.cs:366-394` `HandleScreenshot`: accepts optional JSON body `{"label": "..."}`, calls
  `CaptureService.CaptureBlocking(label, 10000, out path, out error)`, responds
  `{"ok":true,"instance":…,"path":…,"base64":<PNG bytes, base64>}` on success, 503 on failure.
- `CaptureService.cs:45-99`: queues `ScreenCapture.CaptureScreenshot(path, superSize)` onto the main
  thread; `CaptureBlocking` **polls until the file exists AND its size is stable across two
  consecutive reads** — Unity writes the PNG asynchronously at end-of-frame, so returning as soon as
  the file merely exists can hand back a truncated image.
- If `[Rpc] Token` is configured, every request (including `/screenshot`) needs header
  `X-Crucible-Token`.

### The correct call
```
POST http://127.0.0.1:8787/screenshot
Content-Type: application/json

{"label": "optional-name"}
```
(An empty `{}` body is also accepted.)

### How the MCP tool does it (`FTK2.Crucible/mcp/server.js:1120-1126`)
```js
case 'ftk2_screenshot': {
  const res = await rpc(inst, 'POST', '/screenshot', { label: args.label || null });
  ...
  if (res.base64) content.push({ type: 'image', data: res.base64, mimeType: 'image/png' });
```
It calls the `/screenshot` route directly — never `/exec` — and returns the image as inline
base64 PNG content.

### How the Python layer does it
Only `FTK2.Crucible/tools/battery.py` takes screenshots, via `drive._post("/screenshot", {})`
(battery.py:25-31, which carries this exact verbatim comment):
> "Screenshots are a DEDICATED HTTP ROUTE (/screenshot), not an /exec command -- calling
> `crucible_screenshot` through exec returns HTTP 400 Bad Request, because no such command is
> registered."
`battery.py`'s `shot(label)` helper reads only `out["path"]` from the response (deliberately not
printing the base64 payload). None of `drive.py`, `to_combat.py`, `encounter.py`, `class_sweep.py`,
or `status_decay.py` take screenshots at all.

**Conclusion: whoever hit `HTTP Error 400: Bad Request` was calling `crucible_screenshot` through
`/exec`. The fix is to call `POST /screenshot` directly (or use the `ftk2_screenshot` MCP tool,
which already does this correctly).**
