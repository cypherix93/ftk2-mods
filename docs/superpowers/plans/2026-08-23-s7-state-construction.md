# S7 State Construction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make it possible to construct **any** FTK2 game state — any party of any classes, any items, any levels, any traits, standing in combat — by loading **one** base fixture and mutating the live run, with no character creation and no UI driving. The gating deliverable is **reaching combat**, because all 60 recipe tests are blocked on it; everything else is sequenced behind that.

**Architecture:** Three layers, each testable without the other two.

1. **`GameArgResolver` (new, `Crucible.Plugin`)** — turns short strings (`p2`, `p0/SWORD_IRON_01`, `12,7`, `-`) into live game objects (`Entity`, `Thing`, `ValueTuple<int,int>`, `GameRandom`, `Env`, `GameRunData`, `List<>`). This is the unlock: `docs/research/crucible-state-construction.md` §6 shows that *every* mutation verb is blocked on the same seven parameter types, not on seventy different ones.
2. **`ArgCoercion` resolver seam (modify, `Crucible.Core`)** — `ArgCoercion` stays **pure** (no game reference, unit-testable with no game running); it gains an *optional* resolver delegate it consults before failing. The game-aware implementation lives in the Plugin.
3. **Mutation verbs (new, `Crucible.Plugin`)** — thin `string`/`int`/`bool` console handlers, each with a mandatory post-condition read-back, registered through the existing `ReflectionCommands.TryRegister` retry loop.

**Tech Stack:** C# · `net10.0` (consoles/tests) + the plugin's existing TFM · `System.Reflection` + Harmony `AccessTools` · BCL only, no NuGet · repo console-runner test pattern (`FTK2.Crucible/src/Crucible.Core.Tests/TestHarness.cs`)

**Research:** `docs/research/crucible-state-construction.md` — every member named in this plan is quoted verbatim there, marked CONFIRMED or ASSUMED. **Do not add a game member name to this plan or to code without a verbatim TypeProbe line for it.**

## Global Constraints

- **No NuGet `PackageReference` anywhere.** BCL + `ProjectReference` only (`FTK2.DevKit/src/DevKit.Plugin/DevKit.Plugin.csproj:31`).
- **No NuGet test framework.** Tests are console runners returning exit code 0 (`Crucible.Core.Tests`, `DevKit.Core.Tests`).
- **`Crucible.Core` must stay game-free.** `ArgCoercion.cs` currently has zero game references and that is *why* it is unit-testable with no game installed. The resolver seam must be a delegate, never a game type.
- **The `CommandLineHelper` marshaller vocabulary is `int / float / double / bool / string / Vector2 / Vector3`. Nothing else. Enums are NOT in it.** Verified in-game 2026-08-23 (`docs/superpowers/plans/2026-08-23-s3-mcp-tool-surface.md` line 25). **Every handler in this plan declares only `string`, `int`, `bool`** and parses inside the body.
- **Registration is deferred to the `RouterMono` tick.** `CommandLineHelper.Initialize` has not run during plugin `Awake`; register from `ReflectionCommands.TryRegister`'s existing retry loop, never from `Awake`.
- **Every verb has a post-condition.** A verb that mutates and does not read back is a plan failure. The read-backs are listed per-verb in the research doc's §0 summary table.
- **Every check has a negative control.** A test that cannot fail is a plan failure. Bar set by `FTK2.DevKit/sandbox/DeterminismHarness.Tests` (6 negative controls).
- **Never silently default.** `ArgCoercion`'s own doc-comment: *"a non-numeric string targeting `int` is a failure, not a 0."* `p9` on a four-person party is an error, not slot 0. A `Thing` the character does not hold is an error, not `null`.
- **Resolution runs on the main thread at invoke time**, inside the same `MainThreadPump` step as the invocation. Resolvers read live game state.
- **Two corrections from the research pass are load-bearing. Ignoring either wastes a day:**
  - `CharacterHelper.TryProgressCharacterEntityToLevel` takes **three** arguments — `(Entity, Int32, GameRandom)`. There is no two-argument overload.
  - **`saveUser` does NOT persist the run.** It is `SaveGameHelper.SaveUserAsync(UserData)`. The run-persisting call is `SaveGameHelper.WriteSaveData(String pGameRunId, GameRunData pGameRun, UserData pUser)`.
- **`_rebuildCharactertAsNewConfigType` exists only on `PartyManagementDirector`.** There is no in-run class-swap API. Task 5 spikes it before anything is built on it.

## File Structure

| File | Responsibility |
|---|---|
| `FTK2.Crucible/src/Crucible.Core/ArgCoercion.cs` | **Modify.** Add `Decimal` support and an optional resolver seam. Stays pure. |
| `FTK2.Crucible/src/Crucible.Core/ArgResolverSeam.cs` | **Create.** The delegate type + a `TryResolve` result struct. Pure; no game reference. |
| `FTK2.Crucible/src/Crucible.Plugin/GameArgResolver.cs` | **Create.** Game-aware resolution of `Entity`, `Thing`, `ValueTuple`2`, `GameRandom`, `Env`, `GameRunData`, `UserData`, `CharacterComponent`, `List<>`. All reflective via `AccessTools`. |
| `FTK2.Crucible/src/Crucible.Plugin/CombatEntryCommands.cs` | **Create.** M1 verbs: spawn enemy, find encounter, enter combat, read combat state. |
| `FTK2.Crucible/src/Crucible.Plugin/CharacterMutationCommands.cs` | **Create.** M3 verbs: level, stat, health, focus, trait. |
| `FTK2.Crucible/src/Crucible.Plugin/InventoryMutationCommands.cs` | **Create.** M4 verbs: give, take, equip, unequip, list. |
| `FTK2.Crucible/src/Crucible.Plugin/PartyMutationCommands.cs` | **Create.** M5 verbs: list party, replace slot's class. |
| `FTK2.Crucible/src/Crucible.Plugin/PersistenceCommands.cs` | **Create.** M6 verbs: write run save, list runs, saving-allowed gate. |
| `FTK2.Crucible/src/Crucible.Core.Tests/ArgCoercionTests.cs` | **Modify/Create.** Coercion + seam tests with negative controls. No game. |
| `FTK2.Crucible/src/Crucible.Core.Tests/ArgAddressSyntaxTests.cs` | **Create.** Pure parse-level tests of the address syntax (`p2`, `p0/ID`, `p0#id`, `p0:slot:X`, `enc:under:p1`, `hex:under:p0`, `seed:42`) against a fake resolver. |

The split matters: address **parsing** is pure and testable with no game; address **resolution** touches the game and can only be spiked live. Keeping them in different assemblies is what makes the parsing half provable offline.

---

## Milestone map

| Milestone | Delivers | Why this order |
|---|---|---|
| **M0** — Task 1 | Live-truth spike: what `SpawnSpecificEnemy` takes, what `_performVenueAction` does | Combat entry is the blocker; three APIs "looked right" and were not. Nothing gets built before this returns. |
| **M1** — Tasks 2–4 | Resolver seam + `Entity`/hex resolution + **combat entry verbs** | **Unblocks all 60 recipe tests.** |
| **M2** — Task 5 | Class-swap spike (`_rebuildCharactertAsNewConfigType` off-route) | Decides M5's shape. Cheap, must happen before M5 is designed. |
| **M3** — Task 6 | Level, stat, health, focus, trait verbs | Pure `Entity` + string/int. Falls out of M1 for free. |
| **M4** — Task 7 | `Thing` resolution + item/equipment verbs | Needs the `Thing` addressing scheme, which needs M1's `Entity` scheme. |
| **M5** — Task 8 | Party composition verbs | Riskiest; depends on M2's verdict. |
| **M6** — Task 9 | Run persistence | Last, because it is only useful once there is something worth persisting. |
| **M7** — Task 10 | End-to-end recipe smoke test | Proves the whole chain. |

---

### Task 1 (M0): Live spike — resolve the three unknowns that gate everything

**No code is written in this task.** It answers questions that decide the shape of Tasks 2–4. Every question below is one the static pass explicitly could not answer (`crucible-state-construction.md` §8).

**Files:** none. Output is a findings block appended to `docs/research/crucible-state-construction.md` §8, replacing each open question with an answer.

**Interfaces:**
- Consumes: a running game with a loaded save on the ADVENTURE route; `ftk2_exec`, `ftk2_list_commands`, `crucible_get`, `crucible_invoke`.
- Produces: answers to SPIKE-1..SPIKE-5 below. **Task 2 must not start until SPIKE-1 and SPIKE-2 are answered.**

- [ ] **Step 1 (SPIKE-1): Dump the live command registry on each route.**

Run `ftk2_list_commands` on `MAIN_MENU`, then on `ADVENTURE`, then (once reached) on `COMBAT`.

Expected, per `crucible-state-construction.md` §5.0: `GetSpecificThing`, `SpawnSpecificEnemy`, `KillPlayer`, `FillLifePool`, `SetPlayerHealth`, `ToggleUI` appear only on ADVENTURE; `EquipSpecificThing`, `ToggleOutline`, `ToggleVenueGrid` only on COMBAT; `EndPhase`, `SetPlayersTo9999HP`, `saveUser`, `SetSeed`, `SetStat` broadly.

**This confirms or refutes the string-locality mapping, which is currently ASSUMED.** Record the actual per-route lists. If `EquipSpecificThing` is in fact available on ADVENTURE, say so — it changes M4's priority.

- [ ] **Step 2 (SPIKE-2): Get `SpawnSpecificEnemy`'s real signature.**

```
crucible_invoke CommandLineHelper TryGetCommand "SpawnSpecificEnemy" -
```

`TryGetCommand(String pName, ValueTuple`5& pCommand)` is CONFIRMED. The `ValueTuple`5` carries the registration record; dump it. Record the parameter count and types.

Then execute it with a `CharacterHelper.DEBUG_*` id and observe:

```
ftk2_exec SpawnSpecificEnemy DEBUG_GOBLIN
```

**Post-condition to check:** `Env.GameRun.Entities` count increases; `AdventureState.IsDevEnemyReady` flips. If the command *arms* a flag rather than spawning immediately (the `IsDevEnemyReady` name strongly suggests it does — ASSUMED), record what then triggers the spawn: a turn end, a move, or `TwitchChatController.ForceTriggerDevEnemy()`.

- [ ] **Step 3 (SPIKE-3): Does `_performVenueAction` enter combat?**

With an enemy on the map:

```
crucible_invoke AdventureHelper GetPlayerEncounter <party0> <env>       // note what it returns
crucible_invoke AdventureDirector _performVenueAction <party0> <enc> -
```

Both arguments are `Entity` — **which is exactly the coercion gap Task 3 closes**, so this step may need a throwaway hard-coded probe rather than the general resolver. That is acceptable and expected: this spike exists to justify building the resolver.

**Post-condition:** `RouterHelper.GetCurrentRoute()` becomes `VENUE`, then `COMBAT`; `Env.GameRun.CombatState.Entities` is non-empty.

If `_performVenueAction` does **not** route: record what it did instead, and fall to §5.1 rank 2 (the `DebugHelper` display-class closure). Do not guess — a "no, here is what happened" is the valuable answer.

- [ ] **Step 4 (SPIKE-4): Confirm `CoreHelper.GetParty` order.**

```
crucible_invoke CoreHelper GetParty <Env.GameRun.Entities>
```

Compare element order against each element's `CharacterComponent.GroupIndex` and `.DisplayName`. **If order is not stable/creation order, the `p<N>` addressing scheme must key on `GroupIndex` instead of list index** — that changes `GameArgResolver` before it is written.

- [ ] **Step 5 (SPIKE-5): Confirm `saveUser` does not persist the run.**

Mutate something trivially observable and cheap: `ftk2_exec SetPlayerHealth <n>`. Then `ftk2_exec saveUser`. Then reload the save and re-read health.

**Expected per the research doc: the mutation is NOT persisted.** If it *is* persisted, that contradicts `SaveGameHelper.SaveUserAsync(UserData)`'s signature and must be explained before M6 is designed.

- [ ] **Step 6: Write the findings back.**

Replace items 1, 3, 4 and 10 in `crucible-state-construction.md` §8 with answers. Mark each newly-answered item CONFIRMED-LIVE with the date. Leave anything still open as open — do not upgrade a guess.

---

### Task 2 (M1): The resolver seam — `Crucible.Core` stays pure

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/ArgResolverSeam.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core/ArgCoercion.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/ArgAddressSyntaxTests.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/ArgCoercionTests.cs` (or create if absent)

**Interfaces:**
- Consumes: nothing (pure).
- Produces:
  - `public delegate bool GameArgResolverDelegate(string raw, Type targetType, out object value, out string error)`
  - `ArgCoercion.Resolver` — a settable static of that delegate type, `null` by default.
  - `ArgCoercion.TryCoerce` unchanged in signature.

- [ ] **Step 1: Add `Decimal` to `ArgCoercion`.**

`_trickleEnemies(GameRandom, Decimal pMinNumber, Decimal pMaxNumber)` and `_spawnHazardInZone(MapZoneData, Decimal pAmount, ..)` are blocked on this and nothing else. `Decimal` is a BCL type, so purity is preserved.

Mirror the existing `int`/`float`/`double` blocks exactly:

```csharp
if (targetType == typeof(decimal))
{
    decimal m;
    if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out m))
    {
        error = "not a valid decimal: " + raw;
        return false;
    }
    value = m;
    return true;
}
```

- [ ] **Step 2: Add the resolver seam.**

`ArgCoercion.TryCoerce` currently falls through to a failure for unknown types. Insert **one** consultation point immediately before that final failure, and nowhere else:

```csharp
// Game object parameters (Entity, Thing, ValueTuple<int,int>, GameRandom, Env, GameRunData, ...)
// cannot be built from a string by anything in this assembly -- Crucible.Core deliberately has no
// game reference, which is what lets it unit-test with no game installed. The Plugin installs a
// resolver that knows how to turn "p2" into a live Entity. If none is installed, the failure below
// is the correct answer, not a bug.
GameArgResolverDelegate resolver = Resolver;
if (resolver != null)
{
    object resolved; string resolveError;
    if (resolver(raw, targetType, out resolved, out resolveError))
    {
        value = resolved;
        return true;
    }
    if (resolveError != null) { error = resolveError; return false; }
}
```

**Do not** consult the resolver before the `string`/`int`/`bool`/enum branches — a `string` parameter must never be intercepted.

**Preserve the `object` refusal exactly as it is.** Its doc-comment names `RouterMono.Route`'s `pCustomData` as the motivating case, and widening it is explicitly out of scope (`crucible-state-construction.md` §6.2 last row).

- [ ] **Step 3: Tests, with negative controls.**

In `ArgCoercionTests.cs`:

- `decimal` parses `"1.5"`, `"0"`, `"-2.25"` (invariant culture).
- **negative control:** `decimal` rejects `"abc"`, `""`, `"1,5"` (comma is a group separator under invariant, not a decimal point — assert the *rejection*, and if it parses, assert what it parsed to and fix the `NumberStyles`).
- with `Resolver == null`, an unknown type still fails with the pre-existing message. **This is the regression guard**: the seam must be invisible when unused.
- with a fake resolver returning `true`, an unknown type succeeds and the fake's value comes back.
- **negative control:** with a fake resolver returning `false` with an error, `TryCoerce` returns `false` and surfaces *the resolver's* error, not a generic one.
- **negative control:** with a fake resolver that would happily claim `typeof(string)`, coercing a `string` parameter must **not** call it. Assert the fake was never invoked.

- [ ] **Step 4: Address-syntax parse tests (`ArgAddressSyntaxTests.cs`).**

Extract address *parsing* into a pure helper in `Crucible.Core` (`ArgAddress.TryParse(string raw, out ArgAddress addr)`) so the syntax is provable with no game. Cover, per `crucible-state-construction.md` §6.2:

| Input | Parses as |
|---|---|
| `p2` | party slot 2 |
| `@<guid>` | entity by guid |
| `name:Aldric` | entity by display name |
| `c3` | combat entity index 3 |
| `e0` | enemy index 0 |
| `p0/SWORD_IRON_01` | thing by config on slot 0 |
| `p0#abc123` | thing by id on slot 0 |
| `p0:slot:MAIN_HAND` | equipped thing in a slot |
| `enc:under:p1` | encounter under slot 1 |
| `hex:under:p0` | hex under slot 0 |
| `12,7` | raw hex coord (handed to `HexHelper.StringToCoord` at resolve time) |
| `seed:42` | seeded `GameRandom` |
| `-` | the well-known singleton / empty sink for this parameter type |
| `inv:p0` | slot 0's inventory list |
| `party`, `entities` | the party / all-entities lists |

**Negative controls:** `p` alone, `p-1`, `p0/` (empty config), `p0:slot:` (empty slot), `enc:under:` , `hex:under:zzz`, `seed:` , `seed:abc` — every one must fail to parse with a specific message. A parser that accepts `p-1` and lets resolution deal with it is a plan failure; the error must name the offending token.

- [ ] **Step 5: Verify.**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, test count up from the 36 recorded on 2026-08-23. Record the new count.

---

### Task 3 (M1): `GameArgResolver` — `Entity`, hex, and the singletons

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/GameArgResolver.cs`
- Modify: `FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs` (install the resolver during `TryRegister`)

**Interfaces:**
- Consumes: `ArgAddress` (Task 2), `ArgCoercion.Resolver` (Task 2), Harmony `AccessTools`.
- Produces: `GameArgResolver.TryResolve(string, Type, out object, out string)` matching `GameArgResolverDelegate`; installed once into `ArgCoercion.Resolver`.

**Grounding — every member this task uses, verbatim from `crucible-state-construction.md`:**

| Purpose | Member |
|---|---|
| the run | `Env.GameRun` (field, `GameRunData`) |
| the user | `Env.User` (field, `UserData`) |
| all entities | `GameRunData.Entities` (prop, `List`1`) |
| the party | `CoreHelper.GetParty(List`1 pEntities)` -> `List`1` |
| entity identity | `Entity.Guid` (prop, `String`) |
| entity components | `Entity.Components` (field, `Dictionary`2`) |
| display name | `CharacterComponent.DisplayName` (field, `String`) |
| slot cross-check | `CharacterComponent.GroupIndex` (field, `Int32`) |
| hex parse | `HexHelper.StringToCoord(String pCoord)` -> `ValueTuple`2` |
| hex under a character | `AdventureComponent.HexPosition` (field, `ValueTuple`2`) via `Entity.TryGetAdventureComponent(GameRunData pGameRun, AdventureComponent& pComponent)` |
| env | `RouterHelper.Env` (prop, `Env`) |
| hex map | `Env.HexMap` (prop, `List`1[,]`) |
| combat entities | `CombatState.Entities` (field, `List`1`); reached via `GameRunData.CombatState` |
| enemy filter | `CharacterHelper.IsEnemy(Entity pEntity)` -> `Boolean` |
| combat RNG | `CombatState.Random` (field, `GameRandom`) |
| overworld RNG | `AdventureDirector._gameRandom` (field) |
| seeded RNG | `GameRandom.Seed` (field, `Int32`) |
| encounter under a character | `AdventureHelper.GetPlayerEncounter(Entity pCharacter, Env pEnv)` -> `Entity` |

- [ ] **Step 1: Resolve the well-known singletons (`-`).**

For `Env` -> `RouterHelper.Env`. For `GameRunData` -> `Env.GameRun`. For `UserData` -> `Env.User`. For `List`1[,]` -> `Env.HexMap`.

**If any is null, that is an error with a specific message** ("no loaded run: Env.GameRun is null"), never a null argument passed through.

- [ ] **Step 2: Resolve `Entity`.**

`p<N>` -> `CoreHelper.GetParty(Env.GameRun.Entities)[N]`, **with the index scheme SPIKE-4 confirmed** (list index or `GroupIndex`). Out-of-range must error and list the resolved party (index, `DisplayName`, `ConfigName`) so the caller can see what was available.

`@<guid>` -> scan `Env.GameRun.Entities` for `Entity.Guid` equality (ordinal).
`name:<X>` -> match `CharacterComponent.DisplayName` (ordinal, case-sensitive; ambiguity is an error naming all matches).
`c<i>` -> `Env.GameRun.CombatState.Entities[i]`.
`e<i>` -> the `CharacterHelper.IsEnemy`-filtered view of the same list.
`enc:under:p<N>` -> `AdventureHelper.GetPlayerEncounter(party[N], RouterHelper.Env)`; null is an error ("no encounter under party slot N").

- [ ] **Step 3: Resolve `ValueTuple`2` (hex).**

A raw coordinate string -> **`HexHelper.StringToCoord(String)`**. This is the game's own parser; **do not write another one.** If it throws or returns something the caller cannot use, surface that.

`hex:under:p<N>` -> `Entity.TryGetAdventureComponent(Env.GameRun, out ac)` then `ac.HexPosition`.

Note `AdventureComponent` has exactly two fields — `HexPosition` (`ValueTuple`2`) and `MapID` (`String`) — so there is nothing else to read.

- [ ] **Step 4: Resolve `GameRandom`.**

`-` -> `Env.GameRun.CombatState.Random` when `RouterHelper.GetCurrentRoute() == eRoutes.COMBAT`, else the live `AdventureDirector._gameRandom` via the existing `ReflectionCommands.TryResolveInstance` RouterMono-field strategy.

`seed:<n>` -> construct a `GameRandom` and set its `Seed` field.

**Never silently construct a fresh unseeded `GameRandom`.** `docs/research/crucible-rng-field-map.md` treats determinism as load-bearing; an unseeded RNG smuggled into `TryProgressCharacterEntityToLevel` makes a "deterministic" recipe non-reproducible. If neither source is available, **error**.

- [ ] **Step 5: Resolve `List<>` parameters.**

`entities` -> `Env.GameRun.Entities`. `party` -> `CoreHelper.GetParty(..)`. `inv:p<N>` -> slot N's `CharacterComponent.Things`.

`-` on a `List<>` parameter -> a **fresh empty list of the parameter's element type**, via `Activator.CreateInstance(parameterType)`. This is the `pResults` diagnostic-sink case that blocks `FullRestoreCharacter`, `ReviveCharacter`, `KillCharacter` and friends. Contents are discarded.

- [ ] **Step 6: Resolve `CharacterComponent`.**

`p<N>` -> `party[N]`'s `CharacterComponent`, read out of `Entity.Components`. Same slot syntax as `Entity`, disambiguated by the parameter type.

- [ ] **Step 7: Refuse everything else, loudly.**

`CombatDecisionData`, `SkillContext`, `RollResultData`, `EncounterActionContext`, `Func`4`, `Func`3` and any unrecognised game type must return `false` with a message naming the type and saying it is unsupported by design. **Do not fabricate a default instance.** The one narrow exception: a nullable `Action`/`Action`1`/`Action`2` may accept `-` -> `null`, and every such site must be commented as ASSUMED.

- [ ] **Step 8: Install the resolver.**

Set `ArgCoercion.Resolver = GameArgResolver.TryResolve` from inside `ReflectionCommands.TryRegister`'s existing retry loop — **never from `Awake`**, for the same reason command registration is deferred.

- [ ] **Step 9: Log every resolution.**

A recipe that fails must show *what `p2` resolved to*. Log `raw -> targetType -> resolved description` (guid + `DisplayName` + `ConfigName` for an `Entity`; `ConfigName` + `Id` for a `Thing`). Without this, debugging 60 recipes is hopeless.

- [ ] **Step 10: Verify.**

Live, with a loaded run:

```
crucible_invoke CharacterHelper GetHealth p0
crucible_invoke CharacterHelper GetHealth p9      # expect a clear out-of-range error naming the party
crucible_invoke CharacterHelper GetHealth @<realguid>
crucible_invoke HexHelper DistanceBetweenHexes hex:under:p0 hex:under:p1
```

Expected: the first returns an int matching `CharacterComponent.CurrentHealth`; the second errors and **lists the party**; the fourth returns a plausible distance. **The `p9` case is the negative control — if it returns slot 0's health, stop and fix it before going further.**

---

### Task 4 (M1): Combat entry verbs — the gating deliverable

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/CombatEntryCommands.cs`
- Modify: `FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs` (register)

**Interfaces:**
- Consumes: `GameArgResolver` (Task 3), SPIKE-2/SPIKE-3 answers (Task 1).
- Produces: console commands `crucible_spawn_enemy`, `crucible_enter_combat`, `crucible_combat_state`, `crucible_route`.

**Ranked implementation order — build rank 1 first, and only fall through on a live failure** (`crucible-state-construction.md` §5.1):

- [ ] **Step 1: `crucible_spawn_enemy(string pEnemyConfig)` — rank 1.**

Wrap the shipped `SpawnSpecificEnemy` using the signature SPIKE-2 returned. Valid ids are `CharacterHelper`'s 33 `DEBUG_*` string constants (read them off the type at runtime and expose them in the error when the id is unknown — do not hard-code the list).

**Post-condition:** `Env.GameRun.Entities` count increases, and at least one new entity satisfies `CharacterHelper.IsEnemy`. Report the new entity's guid.

**If SPIKE-2 showed the command only *arms* `AdventureState.IsDevEnemyReady`,** the post-condition is the flag flip plus whatever trigger SPIKE-2 identified, and the verb must document that it is two-phase.

- [ ] **Step 2: Rank-2 fallback — the `DebugHelper` closure.** *(Only if Step 1 fails live.)*

Construct `DebugHelper+<>c__DisplayClass43_0` via `Activator.CreateInstance`, set its four fields, and invoke `<GenerateSpawnEnemiesDebugMenuButtons>g___spawnEnemy|0(String pEnemy)`:

| Field | Type | Value |
|---|---|---|
| `pHexMap` | `List`1[,]` | `Env.HexMap` |
| `pPlayerCharacterEntities` | `List`1` | `CoreHelper.GetParty(Env.GameRun.Entities)` |
| `pEnv` | `Env` | `RouterHelper.Env` |
| `pVisualCallback` | `Action`2` | `null` — **RISK: may be dereferenced.** If it NREs, the fallback is dead; record that and go to Step 3. |

- [ ] **Step 3: Rank-3 fallback — `_trickleEnemies`.** *(Only if Steps 1 and 2 both fail.)*

`AdventureDirector._trickleEnemies(GameRandom pRandom, Decimal pMinNumber, Decimal pMaxNumber)` -> `Task`1`. Needs Task 2's `Decimal` support and Task 3's `GameRandom` resolution — both already landed. Non-deterministic in enemy choice, so it is a floor, not a target.

- [ ] **Step 4: `crucible_enter_combat(string pCharacter, string pEncounter)`.**

```
enc  = AdventureHelper.GetPlayerEncounter(<pCharacter>, RouterHelper.Env)   // when pEncounter is "-"
       AdventureDirector._performVenueAction(Entity pActiveCharacter, Entity pEncounterEntity, String pLootTableArg)
```

Default `pCharacter` to `p0`, `pEncounter` to `enc:under:p0`, `pLootTableArg` to `null`.

**Post-condition, polled:** `RouterHelper.GetCurrentRoute()` becomes `eRoutes.VENUE`, then `eRoutes.COMBAT`; `RouterHelper.IsInVenue()` is true; `Env.GameRun.CombatState.Entities` is non-empty. Report the route at each poll so a stall is visible.

**Stuck risks to detect and report, not to silently retry** (`crucible-traversal-inventory.md`): `AdventureDirector._userPickHex` non-null means the game is waiting on a hex click; `DialogueViewHelper.IsShowing()` means a dialogue is blocking; `VenueDirector._visualStack` is the callback-driven visual queue that stalls unattended runs.

- [ ] **Step 5: `crucible_combat_state()`.**

Read-only, and the verifier for everything above. From `Env.GameRun.CombatState`: `Entities` count, `RoundEntities`, `TotalRounds`, `WaveIndex`, `EnemiesPerWave`, `DeadEnemiesCount`, `EndCombatEarly`, `IsDungeon`, `GridType`. Per combatant: guid, `CharacterComponent.DisplayName`, `.ConfigName`, `.CurrentHealth`, `CharacterHelper.GetMaxHealth`, `CharacterHelper.IsEnemy`, `CharacterHelper.IsDead`.

- [ ] **Step 6: `crucible_route()`.**

`RouterHelper.GetCurrentRoute()` -> the `eRoutes` name as a string. Trivial, and every other verb's gate. If S1/S3 already ship this, reuse rather than duplicating.

- [ ] **Step 7: Verify end-to-end.**

From a loaded ADVENTURE save:

```
crucible_route                       # ADVENTURE
crucible_spawn_enemy DEBUG_GOBLIN
crucible_enter_combat p0 -
crucible_route                       # VENUE, then COMBAT
crucible_combat_state                # non-empty, contains a DEBUG_GOBLIN
```

**Negative control:** `crucible_enter_combat p0 -` with **no** enemy spawned must fail with "no encounter under party slot 0" — not silently succeed, and not enter an empty combat.

**M1 exit criterion: this sequence reaches `eRoutes.COMBAT` from a cold loaded save, unattended.** The 60 recipe tests unblock here.

---

### Task 5 (M2): Class-swap spike — decide M5's shape before designing it

**Files:** none (spike). Output goes to `crucible-state-construction.md` §1.3.

**Interfaces:**
- Consumes: `GameArgResolver`.
- Produces: a verdict on fallbacks (a), (c) and (d) from `crucible-state-construction.md` §1.3.

- [ ] **Step 1: Try fallback (a) — call the persisted director off-route.**

With a loaded ADVENTURE run:

```
crucible_invoke PartyManagementDirector _rebuildCharactertAsNewConfigType p0 CF_EOR_HUNTER false false false
```

`TryResolveInstance`'s RouterMono-field strategy should find `RouterMono._partyManagementDirector`. **Expect a null-deref** on `_characterHuds` / `_actorGameObjects` / `_playerSpots` / `_canvas2D` — those are CONFIRMED fields and the director is deinitialized off-route. Record the exact exception and the field it died on.

It returns `Task`. **Await it.** A fire-and-forget call behind `_customizationLock` is precisely how "four swaps yield one" happens.

- [ ] **Step 2: If (a) fails, try fallback (d) — the blunt write.**

Set `CharacterComponent.ConfigName` directly, then `CharacterHelper.ReconsiderVitals(Entity)`. Read back: `GetMaxHealth`, `GetBaseStats`, `GetPassiveSkills(Entity)`, `GetAbilityBag`. **Record precisely which derived values did and did not change.** This tells us what fallback (c) has to reconstruct.

- [ ] **Step 3: Try fallback (c) — entity replacement.**

```
CharacterHelper.CreatePlayableCharacterEntity("CF_EOR_HUNTER", name, <gameRandom>, uniqueId, true)
Entity.OverwriteGuid(oldGuid)
// re-attach AdventureComponent + PlayerComponent via Entity.Add(Object pComponent)
GameRunData.SetEntities(newList)
```

**Then probe for stale references** — the suspects from §8 item 5: `Env.CachedEntities`, `GameRunData.PlayerFollowers`, `AdventureState.RoundPlayerNames`, and (if in combat) `CombatState.Entities` / `.EntityInitiative`. A swap that reads correctly but breaks the next turn is worse than one that fails loudly.

- [ ] **Step 4: Record the verdict.**

Update `crucible-state-construction.md` §1.3 with which fallback works, what it costs, and what it breaks. **If none works, say so** — that is a legitimate outcome and it means the fixture strategy changes to "one base save per class layout, generated once", which is still far better than per-combination character creation.

---

### Task 6 (M3): Character mutation verbs — level, stat, health, focus, trait

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/CharacterMutationCommands.cs`
- Modify: `ReflectionCommands.cs` (register)

**Interfaces:**
- Consumes: `GameArgResolver`.
- Produces: `crucible_set_level`, `crucible_set_stat`, `crucible_set_health`, `crucible_set_focus`, `crucible_give_trait`, `crucible_remove_traits`, `crucible_character`.

Every verb here takes `(string pCharacter, ...)` where `pCharacter` is an address, and every one ends with a read-back.

- [ ] **Step 1: `crucible_set_level(string pCharacter, int pLevel)`.**

```
CharacterHelper.TryProgressCharacterEntityToLevel(Entity pCharacterEntity, Int32 pLevel, GameRandom pGameRandom)
```

**Three arguments.** The `GameRandom` comes from the `-` resolution in Task 3 Step 4. Clamp/validate against `CharacterHelper.GetPlayerMaxLevel()`; a level above it is an error, not a clamp.

**Post-condition:** `CharacterHelper.GetConfigNameLevel(CharacterComponent.ConfigName)` equals `pLevel`, or `CharacterComponent.ExtraLevel` accounts for the difference. Report both. Also report the `Boolean` the method returned — a `false` return with an unchanged level is a silent failure that must surface.

- [ ] **Step 2: `crucible_set_stat(string pCharacter, string pStat, int pValue)`.**

`CharacterHelper.SetStat(Entity, String, Int32)` — the **String** overload, so no enum marshalling. Validate `pStat` against `eCharacterStats` member names at runtime (`Enum.GetNames`) and list them in the error on a miss.

**Post-condition:** `CharacterHelper.GetStat(Entity, String, false)`.

Add `crucible_override_base_stat` wrapping `CharacterHelper.OverrideBaseStat(Entity, String, Int32)`, verified by `CharacterHelper.GetCharacterBaseStat(Entity, String)` — the two are different things and a recipe author needs both.

- [ ] **Step 3: `crucible_set_health(string pCharacter, int pValue)`.**

`pValue == -1` -> `CharacterHelper.SetToMaxHealth(Entity)`. Otherwise compute the delta from `CharacterHelper.GetHealth(Entity)` and apply `CharacterHelper.AddHealth(Entity, Int32 pValue, Boolean pNonLethal)` — **the three-arg sink-free overload**, so no `List`1 pResults` is needed.

**Post-condition:** `GetHealth` equals `pValue`; `GetMaxHealth(Entity, true)` for context; `IsDead(Entity)`.

Also expose `crucible_kill(string pCharacter)` -> `CharacterHelper.KillCharacter(Entity, List`1 pResults, Boolean pPlaySoundFX)` and `crucible_revive(string pCharacter)` -> `CharacterHelper.ReviveCharacter(Entity, List`1 pResults)`, both using the `-` empty-sink resolution from Task 3 Step 5.

- [ ] **Step 4: `crucible_set_focus(string pCharacter, int pValue)`.**

`pValue == -1` -> `CharacterHelper.SetToMaxFocus(Entity)`; otherwise `CharacterHelper.AddFocus(Entity, Int32)` by delta from `GetFocus`.

**Post-condition:** `GetFocus` / `GetMaxFocus`.

**Name the doc-comment carefully:** this is the *game-mechanic* focus resource, unrelated to the UI keyboard-focus input bug. Somebody will confuse them.

- [ ] **Step 5: `crucible_give_trait(string pCharacter, string pTraitId)`.**

`CharacterHelper.GiveTrait(Entity pCharacter, String pTraitName)`.

**Reject any id that does not literally start with `TRAIT_`, before calling.** Per `FTK2.ClassForge/src/ClassForge.Core/PackContentParser.cs:52-63` and `ClassForge.Plugin/ClassForgePlugin.cs:121-124`, that prefix *is* the native trait substrate. A non-prefixed id is not a trait, and passing one through will look like it worked. Use `InventoryHelper.TRAIT_CONFIG_PREFIX` (CONFIRMED field) as the source of the literal rather than hard-coding `"TRAIT_"`.

**Post-condition:** `InventoryHelper.GetTraits(Entity pCharacter)` -> `List`1` contains the id.

`crucible_remove_traits(string pCharacter)` -> `CharacterHelper.RemoveAllTraits(Entity)`; post-condition: `GetTraits` is empty.

- [ ] **Step 6: `crucible_character(string pCharacter)` — the read-back verb.**

One call returning everything a recipe assertion needs: guid, `DisplayName`, `ConfigName`, derived level, `ExtraLevel`, `CurrentHealth`/max, `CurrentFocus`/max/`RequiredFocus`, `IsDead`, `GetBaseStats`, `GetTraits`, `GetEquippedThings(entity, false, **false**)`, `Things` count, `GetCharacterGold`.

**Pass `pIncludeTraits: false` to `GetEquippedThings`.** Per `ClassForge.Plugin/Recipes/GameAdapters.cs:179-180`, `true` pulls in `TRAIT_`-prefixed things regardless of slot, which would over-count equipment in every recipe assertion.

- [ ] **Step 7: Verify.**

```
crucible_set_level p0 5      && crucible_character p0    # level 5
crucible_set_stat p0 STR 20  && crucible_character p0    # STR 20
crucible_set_health p0 1     && crucible_character p0    # health 1, IsDead false
crucible_give_trait p0 TRAIT_CATNAP && crucible_character p0
```

**Negative controls:** `crucible_set_stat p0 NOTASTAT 5` errors and lists valid stats; `crucible_give_trait p0 CATNAP` (no prefix) errors before touching the game; `crucible_set_level p0 999` errors against `GetPlayerMaxLevel`.

---

### Task 7 (M4): `Thing` resolution and inventory/equipment verbs

**Files:**
- Modify: `GameArgResolver.cs` (add `Thing`)
- Create: `FTK2.Crucible/src/Crucible.Plugin/InventoryMutationCommands.cs`
- Modify: `ReflectionCommands.cs` (register)

**Interfaces:**
- Consumes: Task 3's `Entity` resolution.
- Produces: `crucible_give`, `crucible_take`, `crucible_equip`, `crucible_unequip`, `crucible_inventory`.

- [ ] **Step 1: `Thing` resolution in `GameArgResolver`.**

| Address | Resolution |
|---|---|
| `p<N>/<CONFIG_ID>` | `InventoryHelper.GetCharacterThing(Entity pEntity, String pThingName, Int32 pAmount)` with amount 1 |
| `p<N>#<thingId>` | `InventoryHelper.GetCharacterThingByID(Entity pCharacter, String pThingID)` |
| `p<N>:slot:<SLOT>` | `EquipmentHelper.GetEquippedThingBySlot(eEquipmentSlots pSlot, Entity pCharacter)` |

**A null result is an error, never a passed-through null.** A `null` `Thing` into `EquipmentHelper.Equip(Thing, Entity)` is a silent no-op that reads as success. The error must list what the character actually holds.

- [ ] **Step 2: `crucible_give(string pCharacter, string pItemId, int pQuantity)`.**

```
InventoryHelper.GiveByName(String pThingConfigName, Int32 pQuantity, List`1 pInventory, String pParentId, Int32 pMinMaterialTier, Int32 pMaxMaterialTier, GameRandom pGameRandom)
```

**Mind the asymmetry:** `GiveByName` takes the **inventory list** (`inv:p<N>` -> `CharacterComponent.Things`), while `TakeByName` takes the **entity**. Getting this backwards is a compile-time-invisible, runtime-silent bug.

`pParentId` -> `null`; tiers default to something the recipe can override.

**Post-condition:** `InventoryHelper.GetCharacterThingCount(Entity, String)` increased by `pQuantity`.

This is strictly better than the shipped `GetSpecificThing`, which takes `List`1 pPlayers` and so appears to grant party-wide (ASSUMED — SPIKE it if a recipe depends on it).

- [ ] **Step 3: `crucible_take(string pCharacter, string pItemId, int pQuantity)`.**

`InventoryHelper.TakeByName(String pThingConfigName, Int32 pQuantity, Entity pEntity)` -> `Int32` (the amount actually taken — report it; a returned 0 is a failure the caller must see).

**Post-condition:** `GetCharacterThingCount` decreased.

- [ ] **Step 4: `crucible_equip(string pCharacter, string pItem)`.**

If `pItem` is a bare config id: `EquipmentHelper.Equip(String pThingName, Entity pCharacterEntity, Int32 pMinMaterialTier, Int32 pMaxMaterialTier, GameRandom pGameRandom)`.
If `pItem` is a `Thing` address: `EquipmentHelper.Equip(Thing pThing, Entity pCharacterEntity)`.

Gate on `EquipmentHelper.CanEquip(String pThingName, Entity pCharacterEntity)` first and report a refusal rather than a silent no-op.

**Post-condition:** `EquipmentHelper.GetEquippedThingBySlot(slot, entity)` returns a `Thing` whose `ConfigName` matches, or `EquipmentHelper.IsEquipped(Thing, CharacterComponent)`.

Add `crucible_equip_all(string pCharacter)` -> `EquipmentHelper.TryEquipEverySlot(Entity)` — one call for "fully-geared character", which most recipes want.

- [ ] **Step 5: `crucible_unequip(string pCharacter, string pThing)`.**

`EquipmentHelper.TryUnequip(Thing pThing, Entity pCharacterEntity)` -> `Boolean`. Report the boolean.

**Post-condition:** the slot read-back no longer returns that `Thing`.

- [ ] **Step 6: `crucible_inventory(string pCharacter)` — the read-back verb.**

Per `Thing` in `CharacterComponent.Things`: `ConfigName`, `Id`, `StackCount`, `Type`, and whether `EquipmentHelper.IsEquipped(Thing, CharacterComponent)`. Plus `CharacterComponent.Equipped` as a slot map, and `InventoryHelper.GetCharacterGold(Entity)`.

This is the "enumerate what a character currently holds so a test can assert on it" requirement, answered directly.

- [ ] **Step 7: Verify.**

```
crucible_give p0 SCROLL_TELEPORT_01 3   && crucible_inventory p0   # count 3
crucible_take p0 SCROLL_TELEPORT_01 1   && crucible_inventory p0   # count 2
crucible_equip p0 <a real weapon id>    && crucible_inventory p0   # MAIN_HAND set
crucible_unequip p0 p0:slot:MAIN_HAND   && crucible_inventory p0   # MAIN_HAND cleared
```

**Negative controls:** `crucible_equip p0 p0/NOT_HELD` errors and lists held items; `crucible_take p0 SCROLL_TELEPORT_01 99` reports the actual amount taken rather than claiming success.

`SCROLL_TELEPORT_01` and `SCROLL_PORTAL_01` are CONFIRMED-present item ids (they appear in `AdventureDirector`'s literal set). Use them for smoke tests; read real weapon ids off a live character.

---

### Task 8 (M5): Party composition verbs — shaped by Task 5's verdict

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/PartyMutationCommands.cs`
- Modify: `ReflectionCommands.cs` (register)

**Interfaces:**
- Consumes: Task 5's verdict, `GameArgResolver`.
- Produces: `crucible_party`, `crucible_set_class`.

- [ ] **Step 1: `crucible_party()` — read-back first.**

`CoreHelper.GetParty(Env.GameRun.Entities)`, and per slot: index, `Entity.Guid`, `CharacterComponent.DisplayName`, `.ConfigName`, `.GroupIndex`, derived level, `CurrentHealth`. Plus `GameRunData.PlayerAmount` and `GameRunData.PlayerFollowers`.

**Build this before any mutation verb** — it is the verifier for all of them, and it also settles SPIKE-4's ordering question permanently.

- [ ] **Step 2: `crucible_set_class(string pSlot, string pClassId)` — implement whichever fallback Task 5 proved.**

Validate `pClassId` against the 31 `CF_EOR_*` ids (plus 4 `CF_PACK_BALDURS`), sourced at runtime, not hard-coded.

If Task 5 proved **(a)**: call `PartyManagementDirector._rebuildCharactertAsNewConfigType(Entity, String, false, false, false)` and **await the returned `Task`** before the post-condition. For a multi-slot recipe, **serialise: await slot N fully before starting slot N+1.** `_customizationLock` is a `SemaphoreSlim` and `_pOnChangeClassDelay` is fire-and-forget `Void` — a parallel loop is how four swaps silently become one.

If Task 5 proved **(c)**: implement the replacement recipe from `crucible-state-construction.md` §1.3(c) — `CreatePlayableCharacterEntity` -> `OverwriteGuid` -> re-attach `AdventureComponent` and `PlayerComponent` -> `SetEntities`.

If Task 5 proved **none**: **do not ship a `crucible_set_class` that pretends to work.** Ship `crucible_party` alone, and record in the plan that per-class-layout base fixtures are required. Say so plainly in the milestone report.

**Post-condition in every case:** `crucible_party` shows slot N's `ConfigName` equal to `pClassId`, **and** slot N's `AdventureComponent.HexPosition` is unchanged, **and** the party count is unchanged. A class swap that teleports or duplicates a character has failed.

- [ ] **Step 3: Verify.**

```
crucible_party
crucible_set_class 1 CF_EOR_HUNTER
crucible_party            # slot 1 is CF_EOR_HUNTER, position unchanged, count unchanged
```

**Negative control:** four swaps in immediate succession must all take. Set all four slots to four distinct classes and assert all four in one `crucible_party` read. **This is the specific failure the brief warned about; a test that only swaps one slot cannot catch it.**

---

### Task 9 (M6): Run persistence

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/PersistenceCommands.cs`
- Modify: `ReflectionCommands.cs` (register)

**Interfaces:**
- Consumes: `GameArgResolver`'s `GameRunData` / `UserData` singleton resolution.
- Produces: `crucible_save_run`, `crucible_save_named`, `crucible_list_runs`, `crucible_can_save`.

- [ ] **Step 1: `crucible_can_save()`.**

`SaveGameHelper.IsManualSavingAllowed(Env pEnv)` -> `Boolean`. **Call this first in every save verb.** Combat and some phases refuse manual saving; a failed save that is actually a policy refusal must not read as a bug.

- [ ] **Step 2: `crucible_save_run()`.**

```
SaveGameHelper.WriteSaveData(String pGameRunId, GameRunData pGameRun, UserData pUser) -> Task`1
```

Arguments: `Env.SelectedGameRunId`, `Env.GameRun`, `Env.User` — all resolved via `-`. Await the `Task`; report its result.

**Do not use `saveUser`.** `SaveGameHelper.SaveUserAsync(UserData)` writes `UserData` to `UserDirectoryPath` and does not touch `GameRunData`. SPIKE-5 confirms this live.

- [ ] **Step 3: `crucible_save_named(string pSaveName)`.**

```
SaveGameHelper.WriteManualSave(String pGameRunId, GameRunData pGameRun, String pSaveName, String pOverwriteFileName) -> Task`1
```

**This is the fixture-generator verb.** `pSaveName` is a plain string, so a 60-recipe suite can mint stable, readable, per-recipe base states — which is the whole point of the mutate-then-save strategy. Respect `SaveGameHelper.MAX_SAVE_NAME_LIMIT` and `MANUAL_SAVE_ID_DELIMITER` (both CONFIRMED fields); a name that violates either is an error before the call.

- [ ] **Step 4: `crucible_list_runs()`.**

`SaveGameHelper.ListGameRunsSync()` -> `List`1`. The post-condition read for both save verbs.

- [ ] **Step 5: Verify persistence properly — reload is mandatory.**

```
crucible_set_stat p0 STR 42
crucible_save_named s7-persistence-probe
crucible_list_runs                     # the new save appears
# route to MAIN_MENU, reload via AdventureSelectionDirector._loadGameRun (crucible-load-path.md §3)
crucible_character p0                  # STR is still 42
```

**Anything short of a reload does not prove persistence.** A read-back before reload only proves the in-memory value, which we already knew.

**Negative control:** run the same sequence with `saveUser` in place of `crucible_save_named` and confirm STR reverts. **That is the evidence that closes out the `saveUser` question for good** — record it in `crucible-state-construction.md` §7.

---

### Task 10 (M7): End-to-end recipe smoke test

**Files:**
- Create: `FTK2.Crucible/data/States/s7-smoke.json` (or wherever `ftk2_apply_state` reads from — follow `docs/superpowers/plans/2026-08-23-s3-mcp-tool-surface.md` K7)

**Interfaces:**
- Consumes: every verb from M1–M6.
- Produces: one runnable recipe proving the whole strategy.

- [ ] **Step 1: Write one recipe exercising every layer.**

From one base fixture: set slot 0's class (if M5 shipped it), set its level, set two stats, grant a trait, give and equip an item, spawn an enemy, enter combat, assert combat state, then save the result as a named fixture.

- [ ] **Step 2: Every step carries its own post-condition, and a failed setup step fails the recipe.**

Per `2026-08-23-s3-mcp-tool-surface.md` Step 1 of the `ftk2_apply_state` task: *"A failed setup step fails the scenario — it must never proceed with a differently-configured test."* A recipe that silently runs with the wrong party is worse than one that fails.

- [ ] **Step 3: Run it twice from the same base fixture and diff the results.**

With RNG pinned (`seed:<n>` on every `GameRandom` argument, plus the existing `SetSeed` command), two runs must produce identical state. **This is the determinism negative control** — if they diverge, an unseeded `GameRandom` leaked in somewhere, which Task 3 Step 4 was written to prevent.

- [ ] **Step 4: Report.**

Milestone report to the reviewer: which of the seven ranked combat-entry routes actually worked; which class-swap fallback shipped, if any; the final `Crucible.Core.Tests` count; and the list of §8 open questions now closed.

---

## Out of scope for S7

Stated explicitly so nobody builds them by accident:

- **Ability execution in combat.** `CombatPhase._performAbility(Entity, Thing, CombatDecisionData, List`1, Boolean, Boolean)` needs a `CombatDecisionData` the resolver deliberately refuses. `crucible-traversal-inventory.md` already settled that `CombatPhase._performAbility` beats `CombatHelper.PerformAbility`; building either is a separate plan.
- **Constructing `CombatEncounterArgs`** to enter combat declaratively (`crucible-state-construction.md` §5.1 rank 5). Powerful, unproven, and it would let a recipe name its enemies — worth its own plan once M1 lands.
- **Widening `ArgCoercion`'s `object` handling.** Its refusal is deliberate and documented at the call site.
- **Offline `.ftk2` editing.** LZ4 plus positional XOR; the round-trip pair exists (`GetCompressedBytesForRun` / `GetRunFromCompressedBytes`) but in-memory mutation plus `WriteSaveData` is strictly cheaper.
- **Fixing the gamepad-input background-focus bug.** A separate agent owns it, and **this plan is designed so nothing here depends on it.** That independence is the point: no verb in M1–M6 sends a synthetic input event.
