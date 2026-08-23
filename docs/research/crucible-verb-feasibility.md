# Crucible S3 verb feasibility (TypeProbe-verified)

Produced by running `dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- <TypeName> --methods`
against the live `FTK2.dll` (5389 types loaded from
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed`).

Goal: for each of the 11 unverified S3 verbs listed in
`docs/superpowers/specs/2026-08-23-crucible-autopilot-design.md` §5, determine whether a real,
non-fabricated game API exists behind it, so infeasible verbs get **dropped** rather than stubbed.

Every method/field name quoted below appears verbatim in TypeProbe stdout captured during this pass.
Nothing here is guessed. Where a name is `private` (leading underscore), that mirrors the precedent
already relied on elsewhere in this repo — `PartyManagementDirector._pOnChangeClassDelay` and
`._rebuildCharactertAsNewConfigType` are already documented as the runtime class-swap path in
`docs/research/game-code-reference.md` §5 and are reflection-callable exactly like the shipped console
commands (`SetPlayerHealth`, `SetStat`, etc., which are themselves thin wrappers over similar helper
calls).

Types probed this pass: `CombatState`, `CombatPhase`, `CombatHelper`, `AdventureDirector`, `PartyHelper`
(not found), `PartyManagementDirector`, `CharacterHelper`, `EncounterPhase`, `VenueHelper`, `GameRandom`,
`GameRun` (not found), `RouterHelper`, `MainMenuDirector`, `AdventureConfig`, `GameCalendar` (not found),
`CalendarHelper` (not found), `WorldState` (not found), `eVenueGrids`, `VenueGrids` (not found),
`AdventureHelper`, `MapHelper` (not found), `TerrainHelper` (not found), `WeatherHelper`,
`AdventureState`, `PartyManagementSyncData`, `CharacterCreationDirector` (not found), `HexHelper`,
`eHexTypes`, `MapZoneData`, `HexTemplateData`, `CommandLineHelper`, `AbilityHelper` (not found),
`eTimesOfDay`.

---

## kill_all

**Verdict: LIKELY-FEASIBLE**

`CharacterHelper` (verbatim from TypeProbe):

```
| method | `Void`    | `KillCharacter` |
| method | `Boolean` | `TryKillCharacter` |
```

`CombatState.Entities` (`field | List\`1 | Entities`) gives the full combatant list to iterate. A
`kill_all` verb can loop `CombatState.Entities` and call `CharacterHelper.TryKillCharacter` (or
`KillCharacter`) per entity — same shape as the already-shipped `SetPlayerHealth`/`SetStat` commands,
which also drive `CharacterHelper` directly.

---

## force_win / force_lose

**Verdict: UNCLEAR**

`CombatHelper` (verbatim):

```
| method | `Boolean` | `TryEndGame` |
```

Supporting state on `CombatState` (verbatim, also documented in
`docs/research/crucible-combat-field-map.md`):

```
| field | `List\`1`  | `AlternateLoseConditions` |
| field | `List\`1`  | `AlternateWinConditions` |
| field | `Boolean`  | `EndCombatEarly` |
```

A callable end-game method (`CombatHelper.TryEndGame`) and combat-state fields that clearly gate
win/lose resolution both exist. What TypeProbe cannot tell us is `TryEndGame`'s parameter list (no
`--methods` signature output — see "Tool limitation" below), so whether it takes a "did the player win"
bool, or only evaluates existing conditions, is unverified. Route to a live-game grounding probe
(call it with the game running, observe effect) before committing to it as the win/lose implementation.
Do not stub a fabricated `ForceWin(bool)` signature.

---

## use_ability

**Verdict: LIKELY-FEASIBLE**

`CombatHelper` (verbatim):

```
| method | `List\`1` | `PerformAbility` |
```

`CombatPhase` (verbatim):

```
| method | `Task` | `_performAbility` |
```

`PerformAbility` is a public, non-UI combat helper method (same class as the already-verified
`ApplyAction`/`EndTurn`/`NextTurn` combat primitives). This is a strong candidate for the actual ability
execution path, with `CombatPhase._performAbility` as the UI-driven caller for cross-reference.

---

## set_party

**Verdict: UNCLEAR — highest priority, most effort spent here**

**Specific finding:** there is a non-UI-named code path for changing a character's class, but it lives
on `PartyManagementDirector`, a `MonoBehaviour`-style Director class instantiated only while the
party-management *screen* is open — so "non-UI" here means "not a button click handler," not "callable
with the game running headless in an arbitrary state."

`PartyManagementDirector` (verbatim from TypeProbe, confirming what
`docs/research/game-code-reference.md` §5 already recorded):

```
| method | `Void` | `_pOnChangeClassDelay` |
| method | `Task` | `_rebuildCharactertAsNewConfigType` |
```

(`docs/research/game-code-reference.md` line 101 additionally records the call shape:
`PartyManagementDirector._pOnChangeClassDelay(pCharacter, pClassConfigName)` — this is prior research,
not re-derived by TypeProbe in this pass, since TypeProbe does not expose parameter lists.)

Supporting data shape — `PartyManagementSyncData` (verbatim):

```
| field | `List\`1`  | `AssignmentIndices` |
| field | `Dictionary\`2` | `LoadoutIndicesForPlayers` |
| field | `List\`1`  | `Players` |
| field | `Boolean`  | `InLoadout` |
```

This confirms party composition is represented as ordinary serializable data (not something baked into
UI widgets), which is what makes programmatic construction plausible in principle.

`PartyHelper` and `CharacterCreationDirector` — **NOT FOUND** (probed, not present in the loaded
assembly under those names). There is no dedicated "party helper" or "character creation director" type;
the class-swap logic is inlined in `PartyManagementDirector`.

**Conclusion:** a real class-swap method exists and is named, but it is a member of a UI-lifecycle
Director class (same family as `CombatPhase`, `AdventureDirector`, `EncounterPhase` — all UI+state
combined). Calling it programmatically means either (a) reflection-invoking it on a live
`PartyManagementDirector` instance while the party-management screen is open, which is not "headless,"
or (b) reconstructing the underlying state (`PartyManagementSyncData.Players` /
`LoadoutIndicesForPlayers`) directly and bypassing the Director method entirely — unverified whether the
rest of the game (character entity stat rebuild, config lookup) tolerates that without going through
`_rebuildCharactertAsNewConfigType`. This is not a flat "UI-only, drop it" result, but it is also not a
clean headless API. Recommend a live-game spike invoking `_pOnChangeClassDelay` /
`_rebuildCharactertAsNewConfigType` via reflection during an active party-management session as the next
concrete step, before assuming the 35-class matrix strategy works unattended.

---

## set_level

**Verdict: LIKELY-FEASIBLE**

`CharacterHelper` (verbatim):

```
| method | `Boolean` | `TryProgressCharacterEntityToLevel` |
| method | `Void`    | `ProgressCompanionEntityToLevel` |
| method | `Int32`   | `GetPlayerMaxLevel` |
```

Direct, non-UI helper methods for progressing a character entity to a target level, in the same
`CharacterHelper` class as the already-shipped `SetStat`/`GetSpecificThing`/`EquipSpecificThing`
commands.

---

## jump_encounter

**Verdict: UNCLEAR**

`AdventureDirector` (verbatim):

```
| method | `Void` | `_createEncounterEntity` |
```

`AdventureHelper` (verbatim):

```
| method | `EncounterGenData` | `GetAdventureEncounter` |
| method | `ValueTuple\`2`     | `GetAdventureEncounterWithZone` |
| method | `List\`1`           | `GetAdventureEncounters` |
```

There is a method that creates an encounter entity (`AdventureDirector._createEncounterEntity`) and
helper lookups to fetch encounter definitions by id/zone. This suggests a path — construct/locate an
encounter and place it — but nothing here confirms a single call that "jumps" the active party straight
into a running encounter/combat the way the spec's scenario example implies
(`{ "do": "jump_encounter", "args": { "id": "..." } }`). Needs a live-game probe of
`_createEncounterEntity`'s actual effect before treating this as confirmed.

---

## set_terrain

**Verdict: UNCLEAR**

`HexHelper` (verbatim):

```
| method | `Void` | `AddHexType` |
```

`eHexTypes` (verbatim — confirms terrain is a real, finite enum, not something requiring string/config
lookups):

```
| field | `eHexTypes` | `BOG` |
| field | `eHexTypes` | `CLEARING` |
| field | `eHexTypes` | `FOREST` |
| field | `eHexTypes` | `RIVER` |
| field | `eHexTypes` | `ROAD` |
| field | `eHexTypes` | `WATER` |
(+ BLOCKER, FEATURE, NONE, RAIL, RIVERBEND/FORK/POINT, ROADBEND/END/FORK*/POINT)
```

`MapHelper` and `TerrainHelper` — **NOT FOUND** (probed, not present under those names).

A method exists (`HexHelper.AddHexType`) on the hex/terrain enum type, but the name is "Add," not "Set" —
unverified whether it replaces a hex's terrain or layers an additional type onto one that already has a
primary type (`HexTemplateData.BaseTypeGroup` suggests hexes carry a base type plus possible secondary
properties, which would make "Add" and "Set" meaningfully different operations). Needs live-game
verification of `AddHexType`'s actual semantics before this is treated as `set_terrain`.

---

## advance_days

**Verdict: UNCLEAR**

FTK2 has no literal "day" concept in the loaded assembly — `GameCalendar`, `CalendarHelper`, and
`WorldState` all **NOT FOUND** (probed, not present). Time progresses through a 4-stage
`eTimesOfDay` cycle instead (verbatim):

```
| field | `eTimesOfDay` | `DAWN` |
| field | `eTimesOfDay` | `DAY` |
| field | `eTimesOfDay` | `DUSK` |
| field | `eTimesOfDay` | `NIGHT` |
```

`AdventureState` carries this as plain settable data (verbatim):

```
| field | `eTimesOfDay` | `CurrentWeather` |
| field | `Int32`       | `CurrentTimeOfDayIndex` |
| field | `List\`1`     | `TimeOfDayTimeline` |
| field | `Int32`       | `TotalRoundCount` |
```

(Note: `TimeOfDay` itself is a field on `AdventureState` per the field dump; `CurrentTimeOfDayIndex` and
`TimeOfDayTimeline` are the indexing/schedule fields around it.)

`AdventureHelper` (verbatim):

```
| method | `eTimesOfDay` | `GetNextTimeOfDay` |
| method | `Int32`       | `GetIndexOfTimeOfDay` |
| method | `Int32`       | `GetTurnsToTimeOfDay` |
| method | `Int32`       | `GetRoundsToTimeOfDay` |
```

There is real, plain-data state (`AdventureState.CurrentTimeOfDayIndex`) that could be written directly
(same pattern as `SetPlayerHealth`), plus helper methods that compute time-of-day transitions. What's
unverified is the semantic mapping from the spec's "advance N days" to this engine's DAWN→DAY→DUSK→NIGHT
cycle (is a "day" one full 4-stage loop? does writing `CurrentTimeOfDayIndex` directly trigger the same
side effects — town refresh, encounter decay — as natural time passage via `AdventureHelper`
methods?). Needs a live-game probe before treating as confirmed; the underlying state is real, the verb
semantics are not yet nailed down.

---

## teleport

**Verdict: LIKELY-FEASIBLE**

`AdventureDirector` (verbatim):

```
| method | `Task` | `_doTeleportAbility` |
```

This is the actual teleport-ability execution method (backing the in-game Teleport Scroll item), as
distinct from the UI hex-picker handler `_onSelectHexPositionTeleportScroll` (also present, verbatim,
but that one is explicitly UI-input-driven — it resolves a player's mouse/controller hex pick, not a
programmatic destination). `_doTeleportAbility` is the better candidate to drive directly.

---

## pin_seed

**Verdict: LIKELY-FEASIBLE**

`GameRandom` (verbatim):

```
| field  | `Int32` | `Seed` |
| method | `Int32` | `GetNewRandomSeed` |
```

`Seed` is a plain, directly-settable `Int32` field on the RNG object itself — the most direct kind of
state mutation available (same tier as writing `CombatState.EndCombatEarly` or a stat field). Every
phase (`CombatPhase._gameRandom`, `AdventureDirector._gameRandom`, `EncounterPhase._gameRandom`,
`MainMenuDirector._gameRandom`, `CombatState.Random`) holds its own `GameRandom` instance, so "pin seed"
means writing `.Seed` on the relevant instance(s) — consistent with the already-shipped `SetSlotRollResult`
verb's approach of overriding RNG-adjacent state directly.

Also relevant: `PartyManagementSyncData.CustomMapGenSeed` (`Int32` field, verbatim) — a seed specifically
for map generation at party-creation time, a second concrete seed-pinning point if map layout (not just
combat RNG) needs to be pinned.

---

## Summary table

| Verb | Verdict | API named |
|---|---|---|
| `kill_all` | LIKELY-FEASIBLE | `CharacterHelper.TryKillCharacter` / `.KillCharacter` |
| `force_win` | UNCLEAR | `CombatHelper.TryEndGame` (+ `CombatState.EndCombatEarly`/`AlternateWinConditions`) |
| `force_lose` | UNCLEAR | `CombatHelper.TryEndGame` (+ `CombatState.EndCombatEarly`/`AlternateLoseConditions`) |
| `use_ability` | LIKELY-FEASIBLE | `CombatHelper.PerformAbility` |
| `set_party` | UNCLEAR | `PartyManagementDirector._pOnChangeClassDelay` / `._rebuildCharactertAsNewConfigType` — real but UI-Director-scoped, not headless |
| `set_level` | LIKELY-FEASIBLE | `CharacterHelper.TryProgressCharacterEntityToLevel` / `.ProgressCompanionEntityToLevel` |
| `jump_encounter` | UNCLEAR | `AdventureDirector._createEncounterEntity` + `AdventureHelper.GetAdventureEncounter` |
| `set_terrain` | UNCLEAR | `HexHelper.AddHexType` — "Add" semantics unverified vs "Set" |
| `advance_days` | UNCLEAR | `AdventureState.CurrentTimeOfDayIndex` + `AdventureHelper.GetNextTimeOfDay` — no literal "day" concept exists, only 4-stage time-of-day |
| `teleport` | LIKELY-FEASIBLE | `AdventureDirector._doTeleportAbility` |
| `pin_seed` | LIKELY-FEASIBLE | `GameRandom.Seed` (+ `PartyManagementSyncData.CustomMapGenSeed`) |

**None of the 11 verbs are NO-API-FOUND.** Every one has at least one real, TypeProbe-verified method or
field behind it — the honest split from the spec ("11 depend on game APIs nobody has confirmed") turns
out to be 5 confirmed-and-named (`kill_all`, `use_ability`, `set_level`, `teleport`, `pin_seed`) and 6
where an API exists but its exact semantics/reachability need a live-game grounding probe before being
planned (`force_win`, `force_lose`, `set_party`, `jump_encounter`, `set_terrain`, `advance_days`). None
should be dropped outright on this evidence, but none of the UNCLEAR six should be planned as if their
signatures/behavior were confirmed — they need the live-game spike step the design doc already calls
for.

## Tool limitation note

`TypeProbe --methods` reports only member kind, return type, and name — it does not expose parameter
lists (see `FTK2.DevKit/sandbox/TypeProbe/Program.cs`, `Probe.cs`, `Report.cs`). This is why several
verdicts above are UNCLEAR rather than LIKELY-FEASIBLE: the method exists, but its call signature (and
therefore whether it does what the verb needs) cannot be confirmed by this tool alone. A live-game
probe (calling the method via reflection against a running game instance and observing state/output) is
the next step for every UNCLEAR verb.
