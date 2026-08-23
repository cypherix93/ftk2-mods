# Crucible State Construction: building any game state without character creation

**Objective.** Determine exactly how to construct an arbitrary FTK2 game state — any party of any
classes, any items, any levels, any traits, in combat or on the overworld — *programmatically*,
without driving character creation.

**The strategic question this pass answers.** Rather than generating one save per party combination
by driving the party-creation UI (slow, and blocked on the input-focus bug), can the harness load
**one** base save and **mutate** the live run into any configuration, then persist it?

**Verdict: YES for the run contents, with two hard corrections to the assumed plan.**

1. **Class swap is NOT available on a loaded run.** `_rebuildCharactertAsNewConfigType` exists on
   exactly one type — `PartyManagementDirector` — and nowhere else in the assembly. See §1.
2. **`saveUser` does NOT persist the run.** It resolves to `SaveGameHelper.SaveUserAsync(UserData)`,
   which writes `UserData` (settings, `PartyCharacters`, `LastRunCharacters`) — not `GameRunData`.
   The run-persisting call is `SaveGameHelper.WriteSaveData(String, GameRunData, UserData)`. See §7.

Everything else — items, equipment, traits, levels, stats, health, focus, and combat entry — has a
confirmed, non-UI, string/int-argument API.

---

## Method and provenance

All member signatures below are quoted **verbatim** from TypeProbe run against the retail assembly:

```
dotnet run --project C:\Users\ben\repos\ftk2-wt-probe2\FTK2.DevKit\sandbox\TypeProbe -c Release -- <Type> --methods
```

Game dir `C:\Program Files (x86)\Steam\steamapps\common\For The King II`, managed dir
`For The King II_Data\Managed`, **5389 types loaded** on every run this pass.

Two supplementary reflection passes were run out-of-process against the same assembly set (scratch
scripts, not committed):

- **Attribute scan.** Every method in all 205 assemblies in `Managed\` (28 730 types scanned, 0
  skipped) was checked for `RegisterCommandAttribute`. **Zero hits.** See §5.0.
- **Global signature search.** Methods filtered by name and by parameter type across all types
  including compiler-generated nested display classes. Used to prove the *absence* of
  `_rebuildChar*` outside `PartyManagementDirector`, and to find the enemy-spawn closure in §5.

**TypeProbe is reflection-only.** It prints signatures, never method bodies. Anything about what a
method *does* is marked ASSUMED. Marks used below:

| Mark | Meaning |
|---|---|
| **CONFIRMED** | The member exists with this exact signature in the retail assembly, quoted verbatim. |
| **ASSUMED** | Behaviour inferred from name, shape or siblings. Body not inspected, not run. |
| **REFUTED** | Probed and found not to exist, or found to do something other than its name implies. |

Nothing in this document names a member that was not read out of TypeProbe output or out of an
existing doc under `docs/research/`.

---

## 0. Summary table — capability, API, status, verifier

| # | Capability | API (verbatim) | Status | What verifies it |
|---|---|---|---|---|
| 1 | Enumerate the party of a loaded run | `CoreHelper.GetParty(List`1 pEntities)` -> `List`1`, fed `Env.GameRun.Entities` | CONFIRMED (sig) / ASSUMED (slot order) | Count equals `GameRunData.PlayerAmount`; each element yields a `CharacterComponent` |
| 2 | Read a character's class | `CharacterComponent.ConfigName` (field, `String`) | CONFIRMED | String equality against `CF_EOR_*` |
| 3 | **Swap a slot's class in a loaded run** | **no in-run API found** — `_rebuildCharactertAsNewConfigType` exists only on `PartyManagementDirector` | **REFUTED for the ADVENTURE route**; ASSUMED-possible via the persisted director instance | see §1.3 for the fallbacks and their risks |
| 4 | Create a playable character from a class id | `CharacterHelper.CreatePlayableCharacterEntity(String pCharacterConfigName, String pDisplayName, GameRandom pGameRandom, String pUniqueId, Boolean pAddPlayerComponent)` -> `Entity` | CONFIRMED (sig) | returned `Entity`'s `CharacterComponent.ConfigName` |
| 5 | Replace the party wholesale | `GameRunData.SetEntities(List`1 pEntities)`; `GameRunData.Entities` (prop) | CONFIRMED (sig) | re-read `CoreHelper.GetParty` |
| 6 | Add an item to a specific character | `InventoryHelper.GiveByName(String pThingConfigName, Int32 pQuantity, List`1 pInventory, String pParentId, Int32 pMinMaterialTier, Int32 pMaxMaterialTier, GameRandom pGameRandom)` -> `List`1`, with `pInventory` = `CharacterComponent.Things` | CONFIRMED (sig) | `InventoryHelper.GetCharacterThingCount(Entity, String)` |
| 7 | Add an item (shipped path) | console `GetSpecificThing <configId> <qty>` -> `DebugHelper.DebugGetSpecificThing(List`1 pPlayers, GameRandom pGameRandom, String pThingId, Int32 pQuantity, Action`1 pRefresh)` | CONFIRMED (shipped, in use) | same |
| 8 | Remove an item | `InventoryHelper.TakeByName(String pThingConfigName, Int32 pQuantity, Entity pEntity)` -> `Int32` | CONFIRMED (sig) | count goes to 0 |
| 9 | Set a stack quantity | `Thing.StackCount` (prop), `Thing.TryIncreaseStack(Int32)`, `Thing.TryDecreaseStack(Int32)` | CONFIRMED | re-read `StackCount` |
| 10 | Equip by config name | `EquipmentHelper.Equip(String pThingName, Entity pCharacterEntity, Int32 pMinMaterialTier, Int32 pMaxMaterialTier, GameRandom pGameRandom)` | CONFIRMED (sig) | `EquipmentHelper.GetEquippedThingBySlot(eEquipmentSlots, Entity)` |
| 11 | Equip an already-held `Thing` | `EquipmentHelper.Equip(Thing pThing, Entity pCharacterEntity)` | CONFIRMED (sig) | `EquipmentHelper.IsEquipped(Thing, CharacterComponent)` |
| 12 | Unequip | `EquipmentHelper.TryUnequip(Thing pThing, Entity pCharacterEntity)` -> `Boolean`; `EquipmentHelper.Unequip(Thing, Entity, Boolean pReconsiderVitals)` | CONFIRMED (sig) | slot read-back returns null or unarmed |
| 13 | Fill every slot at once | `EquipmentHelper.TryEquipEverySlot(Entity pCharacterEntity)`; `EquipmentHelper.EquipRandomThingForSlotDebug(Entity pEntity, eEquipmentSlots pSlot, Int32 pLevel, GameRandom pGameRandom)` | CONFIRMED (sig) | `EquipmentHelper.GetEquippedThings(Entity, Boolean, Boolean)` |
| 14 | Enumerate what a character holds | `CharacterComponent.Things` (field, `List`1`); `InventoryHelper.GetVisibleItem(Entity)`; `EquipmentHelper.GetEquippedThings(Entity pCharacter, Boolean pVisualOnly, Boolean pIncludeTraits)` -> `HashSet`1` | CONFIRMED | direct read |
| 15 | Enumerate equipped by slot | `CharacterComponent.Equipped` (field, `Dictionary`2`); `EquipmentHelper.GetEquippedThing(CharacterComponent, eEquipmentSlots)` | CONFIRMED | direct read |
| 16 | **Grant a trait** | `CharacterHelper.GiveTrait(Entity pCharacter, String pTraitName)` | **CONFIRMED (sig)**; mechanism corroborated by ClassForge | `InventoryHelper.GetTraits(Entity pCharacter)` -> `List`1` |
| 17 | Remove a trait | `CharacterHelper.RemoveTrait(Entity pCharacter, Thing pTrait)`; `CharacterHelper.RemoveAllTraits(Entity pCharacter)` | CONFIRMED (sig) | `GetTraits` shrinks |
| 18 | Read first trait | `CharacterHelper.GetFirstTrait(CharacterComponent pCharacterComponent)` -> `String` | CONFIRMED | direct |
| 19 | **Set level** | `CharacterHelper.TryProgressCharacterEntityToLevel(Entity pCharacterEntity, Int32 pLevel, GameRandom pGameRandom)` -> `Boolean` — **three args, not two** | CONFIRMED (sig) | `CharacterHelper.GetConfigNameLevel(String pConfigName)` on `CharacterComponent.ConfigName`; `CharacterComponent.ExtraLevel` |
| 20 | Level cap | `CharacterHelper.GetPlayerMaxLevel()` -> `Int32`; `CharacterHelper.MAX_PLAYER_CONFIG_LEVEL` (field) | CONFIRMED | — |
| 21 | Set a stat | `CharacterHelper.SetStat(Entity pCharacterEntity, String pStat, Int32 pValue)`; console `SetStat` | CONFIRMED | `CharacterHelper.GetStat(Entity, String, Boolean pIgnoreEquipped)` |
| 22 | Override a base stat | `CharacterHelper.OverrideBaseStat(Entity pCharacterEntity, String pStat, Int32 pValue)`; `CharacterHelper.AppendStat(Entity, String, Int32)`; `CharacterHelper.AppendBaseStats(Entity, Int32 pAdjustBy)` | CONFIRMED (sig) | `CharacterHelper.GetCharacterBaseStat(Entity, String)`; `CharacterComponent.OverrideStats` / `.BaseStatModifiers` |
| 23 | Health | `CharacterHelper.SetToMaxHealth(Entity)`, `.AddHealth(Entity, Int32, Boolean pNonLethal)` -> `Int32`, `.FullRestoreCharacter(Entity, List`1 pResults)`, `.KillCharacter(Entity, List`1, Boolean)`, `.ReviveCharacter(Entity, List`1)` | CONFIRMED (sig) | `CharacterHelper.GetHealth(Entity)` / `.GetMaxHealth(Entity, Boolean)` / `.IsDead(Entity)`; `CharacterComponent.CurrentHealth` |
| 24 | Health (shipped) | console `SetPlayerHealth`, `FillLifePool`, `KillPlayer`, `SetPlayersTo9999HP` | CONFIRMED (registered strings) | as above; life pool via `GameRunData.CurrentLifePool` |
| 25 | Focus | `CharacterHelper.SetToMaxFocus(Entity)`, `.AddFocus(Entity, Int32)`, `.CommitFocus(Entity, Action pUIRefresh)` | CONFIRMED (sig) | `CharacterHelper.GetFocus(Entity)` / `.GetMaxFocus(Entity)`; `CharacterComponent.CurrentFocus` / `.RequiredFocus` |
| 26 | Party-wide passives and stats | `CharacterHelper.AddPartyPassive(String)`, `.AddPartyStat(String, Int32)`, `.InitializePartyStats(List`1 pParty)`, `.ClearPartyStats()` | CONFIRMED (sig) | `CharacterHelper.HasPartyPassive(String)`, `.GetPartyStat(String)` |
| 27 | Gold | `InventoryHelper.IncreaseCurrency(Entity, Int32)` / `.DecreaseCurrency(Entity, Int32)`; `CharacterHelper.GainGold(Entity, Int32, Boolean, List`1)` | CONFIRMED (sig) | `InventoryHelper.GetCharacterGold(Entity)` |
| 28 | **Spawn an enemy on the overworld** | console `SpawnSpecificEnemy` (registered by `AdventureDirector`) | CONFIRMED (string registered); handler ASSUMED | new hostile `Entity` in `GameRunData.Entities`; `AdventureState.IsDevEnemyReady` |
| 29 | **Enter combat from the overworld** | `AdventureDirector._performVenueAction(Entity pActiveCharacter, Entity pEncounterEntity, String pLootTableArg)` | CONFIRMED (sig) / ASSUMED (behaviour) | `RouterHelper.GetCurrentRoute() == eRoutes.VENUE`, then `COMBAT`; `RouterHelper.IsInVenue()` |
| 30 | Find the encounter under a character | `AdventureHelper.GetPlayerEncounter(Entity pCharacter, Env pEnv)` -> `Entity` | CONFIRMED (sig) | non-null; carries an `EncounterComponent` |
| 31 | Move onto a hex (can be ambushed) | `AdventureDirector._move(ValueTuple`2 pGoal, Boolean pConsumeActionPoints, Boolean pCanBeAmbushed, Boolean pShowEncounterMenu)` | CONFIRMED (sig) | `AdventureComponent.HexPosition` changes |
| 32 | Hex coordinate from a string | `HexHelper.StringToCoord(String pCoord)` -> `ValueTuple`2` | **CONFIRMED** | round-trip against `AdventureComponent.HexPosition` |
| 33 | Character's hex position | `AdventureComponent.HexPosition` (field, `ValueTuple`2`), `.MapID` (field, `String`); reached via `Entity.TryGetAdventureComponent(GameRunData pGameRun, AdventureComponent& pComponent)` | CONFIRMED | direct read |
| 34 | End the current phase | per-phase `_debugEndPhase()` (`CombatPhase`, `EncounterPhase`, `FortunePhase`, `TreasurePhase`, `TrapPhase`, `WheelPhase`) and `_debugEndPhase(Int32 pOption)` (`RestPhase`); console `EndPhase` | CONFIRMED (prior pass, `crucible-traversal-inventory.md` §0) | `RouterHelper.GetCurrentRoute()` changes |
| 35 | **Persist a mutated run** | `SaveGameHelper.WriteSaveData(String pGameRunId, GameRunData pGameRun, UserData pUser)` -> `Task`1`; or `SaveGameHelper.WriteManualSave(String pGameRunId, GameRunData pGameRun, String pSaveName, String pOverwriteFileName)` -> `Task`1` | CONFIRMED (sig) | new or updated `<guid>.ftk2` under `GameRuns\`; reload and re-read the mutation |
| 36 | `saveUser` | `SaveGameHelper.SaveUserAsync(UserData pUser)` | **CONFIRMED — and it does NOT save the run** | writes `UserDirectoryPath`, not `GameRuns\` |

---

## 1. Party composition

### 1.1 Where the party lives outside character creation — CONFIRMED

`GameRunData` (verbatim):

```
| field | `List`1` | `_entities` | `` |
| method | `Void` | `SetEntities` | `(List`1 pEntities)` |
| prop | `List`1` | `Entities` | `` |
| field | `Int32` | `PlayerAmount` | `` |
| field | `Dictionary`2` | `PlayerFollowers` | `` |
```

`Env` (verbatim):

```
| field | `GameRunData` | `GameRun` | `` |
| field | `UserData` | `User` | `` |
| field | `List`1` | `GameRuns` | `` |
| field | `String` | `SelectedGameRunId` | `` |
| field | `Dictionary`2` | `CachedEntities` | `` |
| prop | `List`1[,]` | `HexMap` | `` |
```

**The party of a loaded run is a filtered view of `Env.GameRun.Entities`.** The canonical filter is
`CoreHelper` (verbatim):

```
| method | `List`1` | `GetParty` | `(List`1 pEntities)` |
| method | `List`1` | `GetPlayersAndFollowers` | `(List`1 pPlayers, Dictionary`2 pPlayerFollowers, List`1 pEntities)` |
| method | `Entity` | `GetPlayerFollower` | `(String pPlayerID, Dictionary`2 pPlayerFollowers, List`1 pEntities)` |
| method | `Entity` | `GetFollowerOwner` | `(String pFollowerID, Dictionary`2 pPlayerFollowers, List`1 pEntities)` |
```

So **`CoreHelper.GetParty(Env.GameRun.Entities)` is the party-slot addressing primitive.** That it
returns players in stable slot order is ASSUMED — the name and the single `List` parameter are all
TypeProbe gives. `CharacterComponent.GroupIndex` (`Int32`, CONFIRMED field) is the cross-check.

**`PartyHelper` does not exist** — probed, NOT FOUND. Do not write code against it.

### 1.2 What `UserData.PartyCharacters` / `LastRunCharacters` actually are — CONFIRMED

`UserData` (verbatim):

```
| field | `List`1` | `_lastRunCharacters` | `` |
| field | `List`1` | `_partyCharacters` | `` |
| method | `Void` | `SetLastRunCharacters` | `(List`1 pEntities)` |
| method | `Void` | `SetPartyCharacters` | `(List`1 pEntities)` |
| prop | `List`1` | `LastRunCharacters` | `` |
| prop | `List`1` | `PartyCharacters` | `` |
| field | `String` | `LastGameRunIdPlayed` | `` |
```

These live on `UserData`, which is the **profile**, not the run — the very object
`SaveGameHelper.SaveUserAsync` writes. They are the party-creation-screen roster and the
most-recent-run roster. **Mutating them does not change a loaded run.** Do not use them as the party
handle for a live run; use `Env.GameRun.Entities`.

### 1.3 Swapping a slot's class — the hard finding

**The class-swap path is `PartyManagementDirector`-only.** A global search across all 5389 types,
including nested compiler-generated types, for methods matching `^_rebuildChar` returned exactly:

```
PartyManagementDirector._rebuildCharactertAsNewConfigType(Entity pCharacter, String pClassConfigName, Boolean pOnlineAction, Boolean pTryPlayReadyUp, Boolean pIsPreset) -> Task
PartyManagementDirector._rebuildCharacterEntity(Entity pCharacter, String pLastClassName) -> Void
```

and `_pOnChangeClassDelay(Entity pCharacter, String pClassConfigName)` likewise only there. Neither
`AdventureDirector`, `GameplayDirectorBase`, nor `DirectorBase` carries any of them.
`PartyManagementDirector` also owns the serialisation machinery the brief already documented
(verbatim):

```
| field | `SemaphoreSlim` | `_customizationLock` | `` |
| field | `CancellationTokenSource` | `_classChangeDelayToken` | `` |
```

So **"make party slot N class X on a loaded overworld run" has no first-class API.** Four fallbacks,
ranked:

**(a) Call the persisted director anyway — ASSUMED, medium risk, cheapest to try.**

`RouterMono` holds every director as a private field for the process lifetime (verbatim):

```
| field | `PartyManagementDirector` | `_partyManagementDirector` | `` |
| field | `AdventureDirector` | `_adventureDirector` | `` |
| field | `VenueDirector` | `_venueDirector` | `` |
| field | `DungeonDirector` | `_dungeonDirector` | `` |
| field | `eRoutes` | `_currentRoute` | `` |
| method | `Void` | `Route` | `(eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData, Boolean pReload, Boolean pForceRoute)` |
```

and `ReflectionCommands.TryResolveInstance` already resolves instances via *"a private field on the
live RouterMono"* (`FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs:719`, `:764-778`), so the
instance is reachable off-route. **The risk is that `_rebuildCharactertAsNewConfigType` touches UI
state that is deinitialized** — `_characterHuds` (`List`1`), `_actorGameObjects` (prop
`Dictionary`2`), `_playerSpots` (`GameObject[]`), `_inventoryController`, `_canvas2D`,
`_decommissioned` (`Boolean`) are all CONFIRMED fields on the director. A null-deref there is the
likely failure. This must be spiked live; do not build on it until it is.

Also note: it returns `Task`, and the brief's own measurement says swaps are async and serialised
behind `_customizationLock`. A loop of four swaps must await each one; `_pOnChangeClassDelay` is
`Void` and fire-and-forget, which is exactly how "four swaps silently yield one" happens.

**(b) Route to `PARTY_MANAGEMENT`, swap, route back — ASSUMED, high blast radius, probably wrong.**

`RouterMono.Route(eRoutes.PARTY_MANAGEMENT, seed, customData, reload, force)`. But
`PartyManagementDirector.Initialize` is verbatim
`(UIDocument pCanvas2D, Int32 pRandomSeed, UserData pUserData, Camera pCamera, CinemachineVirtualCamera pVirtualCamera, PartyManagementSyncData pSyncData)`
— it re-initialises from **`UserData`**, not from `Env.GameRun`, and `_processCharactersBeforeAdventureStart()`
plus `_initAdventureAndRoute(Boolean pOnlineAction)` sit on the exit path (both CONFIRMED
signatures). **This reads as starting a fresh run rather than editing the loaded one.** Treat as
REFUTED-in-spirit until proven otherwise.

**(c) Replace the entity — CONFIRMED signatures, ASSUMED sufficiency. The recommended path.**

```
CharacterHelper | method | `Entity` | `CreatePlayableCharacterEntity` | `(String pCharacterConfigName, String pDisplayName, GameRandom pGameRandom, String pUniqueId, Boolean pAddPlayerComponent)` |
CharacterHelper | method | `Entity` | `CreateCharacterEntity` | `(String pCharacterConfigName, Boolean pIsNpc, GameRandom pGameRandom, String pUniqueID, Boolean pAddPlayerComponent, eSummonTypes pSummonType, Int32 pLevel)` |
CharacterHelper | method | `CharacterComponent` | `CreateCharacterComponent` | `(String pCharacterConfigName, eSummonTypes pSummonType, Int32 pLevel)` |
Entity          | method | `Entity` | `Add`    | `(Object pComponent)` |
Entity          | method | `Entity` | `Remove` | `(Object pComponent)` |
Entity          | method | `Void`   | `OverwriteGuid` | `(String pNewGuid)` |
Entity          | prop   | `String` | `Guid`   | `` |
Entity          | field  | `Dictionary`2` | `Components` | `` |
GameRunData     | method | `Void`   | `SetEntities` | `(List`1 pEntities)` |
```

`CreatePlayableCharacterEntity` takes the class config name **as a plain string** plus a
`pUniqueId`, and `Entity.OverwriteGuid(String pNewGuid)` exists specifically so an entity can adopt
another's identity. The recipe (ASSUMED end-to-end; every step CONFIRMED individually):

1. `old = CoreHelper.GetParty(Env.GameRun.Entities)[N]`
2. read `old.Guid`, its `AdventureComponent.HexPosition` / `.MapID`, and its `PlayerComponent`
3. `new = CharacterHelper.CreatePlayableCharacterEntity("CF_EOR_HUNTER", displayName, gameRandom, uniqueId, true)`
4. `new.OverwriteGuid(old.Guid)` — so `GameRunData.PlayerFollowers`, quest references and
   `AdventureState.RoundPlayerNames` keep resolving
5. re-attach position and player state: `new.Add(adventureComponent)`, `new.Add(playerComponent)`
6. splice into the list and `Env.GameRun.SetEntities(newList)`

**Risk to spike:** whether anything caches `Entity` references by object identity rather than by
guid. `Env.CachedEntities` (`Dictionary`2`, CONFIRMED) is the obvious suspect;
`CombatState.Entities`, `.RoundEntities`, `.EntityInitiative` are the in-combat ones.

**(d) The blunt option — CONFIRMED field, ASSUMED sufficiency, fallback only.**

`CharacterComponent.ConfigName` is a plain writable `String` field. Writing it re-points the
character at a different class config. What it does *not* do is rebuild derived state — stats,
abilities, actor prefab. `CharacterHelper.AppendBaseStats(Entity, Int32)`,
`.OverrideBaseStat(Entity, String, Int32)`, `.ReconsiderVitals(Entity)` and
`.TryProgressCharacterEntityToLevel(...)` are the tools for re-deriving after such a write. Cheapest
to try, least likely to be *correct*.

### 1.4 Add, remove, reorder — status

| Operation | API | Status |
|---|---|---|
| Add a character to a run | `CharacterHelper.CreatePlayableCharacterEntity(...)` + `GameRunData.SetEntities(...)` | ASSUMED (`GameRunData.PlayerAmount` must be updated too — CONFIRMED field) |
| Remove a character | rebuild the list without it, then `SetEntities` | ASSUMED |
| Remove (party-screen only) | `PartyManagementDirector._removeCharacter(Int32 pIndex)` | CONFIRMED sig, PARTY_MANAGEMENT route only |
| Reorder | order of `GameRunData.Entities` plus `CharacterComponent.GroupIndex` | ASSUMED |
| Add a follower or pet | `CharacterHelper.CreateFollowerCharacter(Entity pOwner, String pFollowerConfig, GameRandom pGameRandom, List`1 pProperties, Int32 pLevel)`; `GameRunData.PlayerFollowers` | CONFIRMED sig |
| Randomise a character's class | `CharacterHelper.RandomizePlayer(Entity pCharacter, Boolean pRandomizeClass, Boolean pRandomizeCosmetic, GameRandom pGameRandom)` | CONFIRMED sig; ASSUMED it has the same director-context dependency as (a) |

**"Make party slot N class X" — the exact call, ASSUMED:**

```
party  = CoreHelper.GetParty(Env.GameRun.Entities)            // CONFIRMED sig, ASSUMED order
target = party[N]
fresh  = CharacterHelper.CreatePlayableCharacterEntity(X, displayName, gameRandom, uniqueId, true)
fresh.OverwriteGuid(target.Guid)
// re-attach AdventureComponent + PlayerComponent from target
Env.GameRun.SetEntities(listWithTargetReplacedByFresh)
```

There is **no confirmed one-call form.** Anyone asserting `_rebuildCharactertAsNewConfigType` works
on a loaded run is claiming something this pass could not confirm and has reason to doubt.

---

## 2. Items and equipment

### 2.1 What models an inventory — CONFIRMED

`CharacterComponent` (verbatim):

```
| field | `List`1`       | `Things`   | `` |
| field | `Dictionary`2` | `Equipped` | `` |
| field | `Dictionary`2` | `SkinEquipped` | `` |
| field | `Dictionary`2` | `CustomData` | `` |
```

`Thing` (verbatim):

```
| field | `String`        | `ConfigName` | `` |
| field | `String`        | `Id`         | `` |
| field | `String`        | `ParentId`   | `` |
| field | `eThingTypes`   | `Type`       | `` |
| field | `Dictionary`2`  | `CustomData` | `` |
| field | `Dictionary`2`  | `BaseStatModifiers` | `` |
| field | `List`1`        | `PassiveModifiers`  | `` |
| field | `Int32`         | `_stackCount` | `` |
| prop  | `Int32`         | `StackCount`  | `` |
| method| `Int32` | `TryIncreaseStack` | `(Int32 pQuantity)` |
| method| `Int32` | `TryDecreaseStack` | `(Int32 pQuantity)` |
| method| `Void`  | `AddBaseStatModifier` | `(String pStat, Int32 pAmount)` |
| method| `Void`  | `AddBasePassiveModifier` | `(String pPassive)` |
```

So an inventory is `List<Thing>` on the character's `CharacterComponent`, `Equipped` is a
slot-to-thing dictionary, and a `Thing` carries a guid-ish `Id` alongside its `ConfigName`. Traits
live in the same `Things` list, distinguished by a `TRAIT_` `ConfigName` prefix — see §3.

**`ThingHelper` does not exist** — probed, NOT FOUND. The item helper is `InventoryHelper`.
`ItemHelper`, `LootHelper`, `ThingsHelper` and `ConfigHelper` were also probed and are all NOT FOUND.

### 2.2 Grant, remove, quantity — CONFIRMED signatures

```
InventoryHelper | method | `Thing`  | `CreateThing`  | `(String pConfigName, Int32 pQuantity)` |
InventoryHelper | method | `Thing`  | `CreateThing`  | `(String pConfigName)` |
InventoryHelper | method | `Int32`  | `Give`         | `(Thing pThingToGive, List`1 pInventory)` |
InventoryHelper | method | `Void`   | `GiveBulk`     | `(List`1 pThingsToGive, List`1 pInventory)` |
InventoryHelper | method | `List`1` | `GiveByName`   | `(String pThingConfigName, Int32 pQuantity, List`1 pInventory, String pParentId, Int32 pMinMaterialTier, Int32 pMaxMaterialTier, GameRandom pGameRandom)` |
InventoryHelper | method | `Int32`  | `TakeByName`   | `(String pThingConfigName, Int32 pQuantity, Entity pEntity)` |
InventoryHelper | method | `Void`   | `Take`         | `(Thing pThing, Entity pCharacter, Int32 pQuantity)` |
InventoryHelper | method | `Void`   | `TransferItem` | `(Thing pThing, Entity pSource, Entity pDestination, Int32 pQuantity)` |
InventoryHelper | method | `Void`   | `Consume`      | `(Thing pConsumable, Entity pEntity)` |
InventoryHelper | method | `List`1` | `RemoveThingFromPartyByName` | `(String pThingName, Int32 pAmount, Entity pActivePlayer, List`1 pPlayers, Boolean pSilent)` |
InventoryHelper | method | `List`1` | `CreateEquipment` | `(String pEquipmentName, String pMaterialLevel, GameRandom pRandom)` |
InventoryHelper | method | `Boolean`| `HasItemByName`| `(String pThingConfigName, List`1 pInventory, String pParentId)` |
```

Note the asymmetry: **`GiveByName` takes the inventory list; `TakeByName` takes the entity.** The
call site is `GiveByName(id, qty, characterComponent.Things, null, minTier, maxTier, gameRandom)`.
This is real and is a coercion requirement — see §6.

The shipped console path is `GetSpecificThing <configId> <qty>`, backed by (verbatim):

```
DebugHelper | method | `Void` | `DebugGetSpecificThing` | `(List`1 pPlayers, GameRandom pGameRandom, String pThingId, Int32 pQuantity, Action`1 pRefresh)` |
```

It takes `List`1 pPlayers` — i.e. **the shipped command appears to grant to the whole party, not to
a specific character** (ASSUMED, from the plural parameter). Per-character granting needs
`GiveByName`.

### 2.3 Equipping — CONFIRMED signatures

```
EquipmentHelper | method | `Boolean` | `CanEquip` | `(String pThingName, Entity pCharacterEntity)` |
EquipmentHelper | method | `Void`    | `Equip`    | `(String pThingName, Entity pCharacterEntity, Int32 pMinMaterialTier, Int32 pMaxMaterialTier, GameRandom pGameRandom)` |
EquipmentHelper | method | `Void`    | `Equip`    | `(Thing pThing, Entity pCharacterEntity)` |
EquipmentHelper | method | `Boolean` | `TryUnequip` | `(Thing pThing, Entity pCharacterEntity)` |
EquipmentHelper | method | `Void`    | `Unequip`  | `(Thing pThing, Entity pCharacterEntity, Boolean pReconsiderVitals)` |
EquipmentHelper | method | `Void`    | `UnequipMainHandWeapon` | `(Entity pCharacterEntity)` |
EquipmentHelper | method | `Void`    | `TryEquipEverySlot` | `(Entity pCharacterEntity)` |
EquipmentHelper | method | `Void`    | `EquipRandomThingForSlotDebug` | `(Entity pEntity, eEquipmentSlots pSlot, Int32 pLevel, GameRandom pGameRandom)` |
EquipmentHelper | method | `Void`    | `EquipRandomThingForSlot` | `(Entity pEntity, eEquipmentSlots pSlot, Int32 pLevel, GameRandom pGameRandom, Env pEnv, Boolean pRespectCharacterClass, eItemRarities pRarity, eItemMaterialFamilies pMaterial, List`1 pCustomTags)` |
EquipmentHelper | method | `Void`    | `TryEmptyAllEntityWeapons` | `(List`1 pEntities)` |
```

`Equip(String, Entity, Int32, Int32, GameRandom)` is the money signature: **item id is a string;
only `Entity` and `GameRandom` need resolving.**

`eEquipmentSlots` — all 10 values, verbatim: `ARMOR`, `BACKPACK`, `BOOTS`, `GLOVES`, `HELMET`,
`MAIN_HAND`, `NONE`, `OFF_HAND`, `PIPE`, `TRINKET`.

**Route caveat.** The shipped `EquipSpecificThing <configId>` is registered by **`CombatPhase`** (see
§5.0) — it is a COMBAT-route command. `GetSpecificThing` is registered by `AdventureDirector` — an
ADVENTURE-route command. **They are not both available at the same time.** Any recipe that does
"give then equip" through shipped commands alone will fail on one route or the other. This is
exactly why the reflective `EquipmentHelper` path matters.

### 2.4 Reading back so a test can assert — CONFIRMED

```
InventoryHelper | method | `Thing`        | `GetCharacterThing`      | `(Entity pEntity, String pThingName, Int32 pAmount)` |
InventoryHelper | method | `Thing`        | `GetCharacterThingByID`  | `(Entity pCharacter, String pThingID)` |
InventoryHelper | method | `Int32`        | `GetCharacterThingCount` | `(Entity pCharacter, String pConfigName)` |
InventoryHelper | method | `List`1`       | `GetCharacterThings`     | `(Entity pEntity, String pTag)` |
InventoryHelper | method | `List`1`       | `GetVisibleItem`         | `(Entity pEntity)` |
InventoryHelper | method | `List`1`       | `GetTradableItem`        | `(Entity pEntity)` |
InventoryHelper | method | `Dictionary`2` | `GetEntitiesWhoHaveThing`| `(String pThingName, Int32 pAmount, List`1 pEntities)` |
InventoryHelper | method | `Int32`        | `GetThingStat`           | `(Thing pThing, String pStat)` |
InventoryHelper | method | `ThingConfig`  | `GetThingConfig`         | `(String pThingName)` |
InventoryHelper | method | `List`1`       | `GetItemTags`            | `(Thing pThing)` |
InventoryHelper | method | `Int32`        | `GetCharacterGold`       | `(Entity pCharacter)` |
EquipmentHelper | method | `Thing`        | `GetEquippedThingBySlot` | `(eEquipmentSlots pSlot, Entity pCharacter)` |
EquipmentHelper | method | `Thing`        | `GetEquippedThing`       | `(CharacterComponent pCharacterComponent, eEquipmentSlots pSlot)` |
EquipmentHelper | method | `HashSet`1`    | `GetEquippedThings`      | `(Entity pCharacter, Boolean pVisualOnly, Boolean pIncludeTraits)` |
EquipmentHelper | method | `Thing`        | `GetFirstEquippedThing`  | `(Entity pCharacter, String pThingConfig)` |
EquipmentHelper | method | `Thing`        | `GetFirstNonEquippedThing`| `(Entity pCharacter, String pThingConfig)` |
EquipmentHelper | method | `Boolean`      | `IsEquipped`             | `(Thing pThing, CharacterComponent pCharacterComponent)` |
```

**`GetCharacterThingCount(Entity, String)` -> `Int32` is the assertion primitive** for "does slot N
hold K of item X". **`GetCharacterThing(Entity, String, Int32)` -> `Thing` is the addressing
primitive** for every API that wants a `Thing` — see §6.

---

## 3. Traits

41 authored traits live in `.../Configs/JSON~/Things/Traits.json`. The runtime mechanism is
CONFIRMED and there is a working in-repo precedent.

**Grant and remove (verbatim):**

```
CharacterHelper | method | `Void`   | `GiveTrait`       | `(Entity pCharacter, String pTraitName)` |
CharacterHelper | method | `Void`   | `GiveTrait`       | `(Entity pCharacter, Thing pTrait)` |
CharacterHelper | method | `Void`   | `RemoveTrait`     | `(Entity pCharacter, Thing pTrait)` |
CharacterHelper | method | `Void`   | `RemoveAllTraits` | `(Entity pCharacter)` |
CharacterHelper | method | `String` | `GetFirstTrait`   | `(CharacterComponent pCharacterComponent)` |
InventoryHelper | method | `List`1` | `GetTraits`       | `(Entity pCharacter)` |
InventoryHelper | method | `List`1` | `GetTraits`       | `(List`1 pThings)` |
InventoryHelper | field  | `String` | `TRAIT_CONFIG_PREFIX` | `` |
```

**`CharacterHelper.GiveTrait(Entity, String)` is the grant verb. `InventoryHelper.GetTraits(Entity)`
is the verifier.** Both take only an `Entity` plus a string.

**`TraitHelper` does not exist** — probed, NOT FOUND.

**The ClassForge precedent — a working, shipped mechanism, not a guess.** From
`FTK2.ClassForge/src/ClassForge.Core/PackContentParser.cs:52-63`:

> `// "TRAIT_" (CharacterHelper.GiveTrait / InventoryHelper.GetTraits) — there is no eTraits bridge.`
> `"The native trait substrate (CharacterHelper.GiveTrait/RemoveTrait, InventoryHelper.GetTraits) keys on that literal "` (prefix)

`FTK2.ClassForge/src/ClassForge.Core/Model.cs:178`:

> `/// (SPEC-DELTA-v1.1 §1 OQ#1: grant/remove is native via the TRAIT_ ConfigName prefix, no bridge needed).</summary>`

`FTK2.ClassForge/src/ClassForge.Plugin/ClassForgePlugin.cs:121-124`:

> `EnableTraitLoadoutInjection` ... `"Only ids that literally start with 'TRAIT_' are injected — that prefix IS the native trait "`

**Three consequences the trait verb must honour:**

1. Trait ids **must literally start with `TRAIT_`**. A non-prefixed id is not a trait to this engine.
   ClassForge emits a `CF_TRAIT_PREFIX` warning for exactly this.
2. Traits are `Thing`s in `CharacterComponent.Things` — which is why `InventoryHelper.GetTraits` can
   read them out of a plain `List<Thing>` overload.
3. `EquipmentHelper.GetEquippedThings(Entity, pVisualOnly: false, pIncludeTraits: true)` includes
   `TRAIT_`-prefixed things *regardless of slot* — noted verbatim at
   `FTK2.ClassForge/src/ClassForge.Plugin/Recipes/GameAdapters.cs:179-180`. **An equipment assertion
   that counts equipped things must pass `pIncludeTraits: false` or it will over-count.**

---

## 4. Levels, stats, health, focus

### 4.1 Level — CONFIRMED, and the brief's signature was one argument short

```
CharacterHelper | method | `Boolean` | `TryProgressCharacterEntityToLevel` | `(Entity pCharacterEntity, Int32 pLevel, GameRandom pGameRandom)` |
CharacterHelper | method | `Boolean` | `TryProgressCompanionEntityToLevel` | `(Entity pCharacterEntity, Int32 pLevel, Thing pXPThing, GameRandom pGameRandom)` |
CharacterHelper | method | `Void`    | `ProgressCompanionEntityToLevel`   | `(Entity pCharacterEntity, Int32 pLevel)` |
CharacterHelper | method | `Int32`   | `GetPlayerMaxLevel` | `()` |
CharacterHelper | method | `Int32`   | `GetEnemyMaxLevel`  | `(GameRunData pGameRun)` |
CharacterHelper | method | `Int32`   | `GetConfigNameLevel` | `(String pConfigName)` |
CharacterHelper | method | `String`  | `GetConfigNameForLevel` | `(String pConfigName, Int32 pLevel)` |
CharacterHelper | method | `String`  | `GetCharacterConfigAtLevel` | `(String pConfigName, Int32 pLevel)` |
CharacterHelper | method | `Int32`   | `GetExtraLevelStats` | `(String pStat, Int32 pStatValue, Int32 pExtraLevel, eCharacterTypes pType)` |
CharacterHelper | field  | `Int32`   | `MAX_PLAYER_CONFIG_LEVEL` | `` |
CharacterHelper | field  | `Int32`   | `MAX_ENEMY_CONFIG_LEVEL`  | `` |
```

**Correction to the brief:** it lists `TryProgressCharacterEntityToLevel(Entity, Int32)`. The
assembly has **only** the three-argument form with a trailing `GameRandom`. Code written to the
two-argument signature will fail method lookup at runtime.

**Verify with:** `CharacterHelper.GetConfigNameLevel(characterComponent.ConfigName)` -> `Int32` —
level is encoded in the config name, which is what `GetConfigNameForLevel` implies (ASSUMED) — plus
`CharacterComponent.ExtraLevel` (`Int32`, CONFIRMED field) for levels past the config ladder.

### 4.2 Stats — CONFIRMED

```
CharacterHelper | method | `Void`         | `SetStat`              | `(Entity pCharacterEntity, String pStat, Int32 pValue)` |
CharacterHelper | method | `Void`         | `OverrideBaseStat`     | `(Entity pCharacterEntity, String pStat, Int32 pValue)` |
CharacterHelper | method | `Void`         | `AppendStat`           | `(Entity pCharacterEntity, String pStat, Int32 pValue)` |
CharacterHelper | method | `Void`         | `AppendBaseStats`      | `(Entity pCharacterEntity, Int32 pAdjustBy)` |
CharacterHelper | method | `Dictionary`2` | `GetBaseStats`         | `(Entity pCharacterEntity)` |
CharacterHelper | method | `Int32`        | `GetCharacterBaseStat` | `(Entity pCharacterEntity, String pStat)` |
CharacterHelper | method | `Int32`        | `GetStat`              | `(Entity pCharacterEntity, String pStat, Boolean pIgnoreEquipped)` |
CharacterHelper | method | `Dictionary`2` | `GetStatModifierValues`| `(Entity pCharacterEntity, String pStat)` |
CharacterHelper | method | `Boolean`      | `TryGetMaxStatValue`   | `(String pCharacterStat, Int32& pMaxStat)` |
CharacterHelper | method | `Boolean`      | `HasStat`              | `(CharacterComponent pCharacterComponent, String pStat)` |
CharacterHelper | field  | `Int32`        | `MAX_CHARACTER_STAT`   | `` |
CharacterHelper | field  | `Int32`        | `MIN_CHARACTER_STAT`   | `` |
CharacterHelper | field  | `Dictionary`2` | `ValidPartyStats`      | `` |
CharacterHelper | field  | `Dictionary`2` | `ValidPartyPassives`   | `` |
```

Every one takes the stat as a **`String`**, with `eCharacterStats` overloads alongside — so the
string form sidesteps enum coercion entirely. 62 `eCharacterStats` members exist; the
gameplay-relevant ones, verbatim: `STR`, `VIT`, `INT`, `AWR`, `TAL`, `SPD`, `LCK`, `PHY`, `RES`,
`SKL`, `ATK`, `DEF`, `DAM`, `EVD`, `ACC`, `CRT`, `CRTD`, `MOV`, `HP`, `MXHP`, `FOC`, `MXFOC`,
`CURRENT_FOCUS`, `GLD`, `XP`, `XPM`, `PARTY_XP`.

Backing fields on `CharacterComponent`, both CONFIRMED: `OverrideStats` (`Dictionary`2`),
`BaseStatModifiers` (`Dictionary`2`).

### 4.3 Health — CONFIRMED

```
CharacterHelper | method | `Void`   | `SetToMaxHealth`       | `(Entity pEntity)` |
CharacterHelper | method | `Int32`  | `AddHealth`            | `(Entity pEntity, Int32 pValue, Boolean pNonLethal)` |
CharacterHelper | method | `Int32`  | `GetHealth`            | `(Entity pEntity)` |
CharacterHelper | method | `Int32`  | `GetMaxHealth`         | `(Entity pEntity, Boolean pCappedStat)` |
CharacterHelper | method | `Int32`  | `GetHealthMissing`     | `(Entity pEntity)` |
CharacterHelper | method | `Boolean`| `IsDead`               | `(Entity pEntity)` |
CharacterHelper | method | `Void`   | `KillCharacter`        | `(Entity pTargetEntity, List`1 pResults, Boolean pPlaySoundFX)` |
CharacterHelper | method | `Void`   | `ReviveCharacter`      | `(Entity pTargetEntity, List`1 pResults)` |
CharacterHelper | method | `Boolean`| `TryRevivePlayer`      | `(Entity pTargetEntity, List`1 pResults)` |
CharacterHelper | method | `Void`   | `FullRestoreCharacter` | `(Entity pCharacter, List`1 pResults)` |
CharacterHelper | method | `Void`   | `ReconsiderVitals`     | `(Entity pEntity)` |
CharacterHelper | method | `eCharacterTargetState` | `GetHealthState` | `(Entity pCharacter)` |
```

Several take a `List`1 pResults` diagnostic sink. Constructing one is not expressible today — see
§6. Prefer the sink-free forms (`SetToMaxHealth`, `AddHealth(Entity,Int32,Boolean)`,
`ReconsiderVitals`) first. Backing field `CharacterComponent.CurrentHealth` (`Int32`).

Shipped console alternatives: `SetPlayerHealth`, `FillLifePool`, `KillPlayer` — all registered by
`AdventureDirector` — and `SetPlayersTo9999HP` on `DirectorBase`/`GameplayDirectorBase`. In combat,
`CombatPhase._debugSetPlayersTo9999HP()` and `._debugSetAllCharactersTo9999HP()`
(`crucible-traversal-inventory.md`). Life pool reads back on `GameRunData.CurrentLifePool` (`Int32`).

### 4.4 Focus — CONFIRMED

```
CharacterHelper | method | `Void`  | `SetToMaxFocus`  | `(Entity pEntity)` |
CharacterHelper | method | `Void`  | `AddFocus`       | `(Entity pEntity, Int32 pValue)` |
CharacterHelper | method | `Void`  | `CommitFocus`    | `(Entity pCharacter, Action pUIRefresh)` |
CharacterHelper | method | `Int32` | `GetFocus`       | `(Entity pEntity)` |
CharacterHelper | method | `Int32` | `GetMaxFocus`    | `(Entity pEntity)` |
CharacterHelper | method | `Int32` | `GetFocusMissing`| `(Entity pEntity)` |
CharacterHelper | method | `Int32` | `GetFocusedStatValue` | `(Int32 pRawValue, Int32 pFocusedAmount, Int32 pModifiedAmount)` |
CharacterHelper | field  | `Int32` | `STAT_FOCUS_INCREASE_BASE` | `` |
CharacterHelper | field  | `Decimal` | `STAT_MULTIPLIER_PER_FOCUS` | `` |
```

Backing fields `CharacterComponent.CurrentFocus`, `.CachedFocus`, `.RequiredFocus` — all `Int32`.

**Note the name collision.** "Focus" in the brief also means *UI keyboard focus* — the input bug.
These APIs are the *game-mechanic* focus resource. They are unrelated, and the input bug does not
gate them.

---

## 5. Reaching combat — the ranked routes

### 5.0 First, the shipped-command ground truth, and one negative result

**Negative result — do not repeat this work.** `CommandLineHelper` exposes
`FetchRegisterCommandAttributes()`, and the assembly does define `RegisterCommandAttribute`
(`ctor (String commandName, String[] tooltips)`; `AttributeUsage targets=Method`; properties
`CommandName`, `Tooltips`, `order`). **But not one method in any of the 205 assemblies under
`Managed\` carries it** — 28 730 types scanned, 0 skipped, 0 hits. **The shipped commands are
registered imperatively**, via:

```
CommandLineHelper | method | `Void`   | `RegisterCommand` | `(String pName, MethodInfo pMethod, List`1 pTooltips, Object pContext)` |
CommandLineHelper | method | `Void`   | `RegisterCommand` | `(String pName, MethodInfo pMethod, List`1 pTooltips, List`1 pSuggestions, Object pContext)` |
CommandLineHelper | method | `Void`   | `RegisterCommand` | `(String pName, Action pAction, List`1 pTooltips)` |
CommandLineHelper | field  | `Dictionary`2` | `_commandRegistry` | `` |
CommandLineHelper | method | `List`1` | `GetCommands`     | `()` |
CommandLineHelper | method | `Boolean`| `TryGetCommand`   | `(String pName, ValueTuple`5& pCommand)` |
CommandLineHelper | method | `Void`   | `ExecuteCommand`  | `(String pName, String[] pArgs, Boolean pPrintSuccess, Boolean pPrintResult)` |
CommandLineHelper | method | `Void`   | `UnregisterCommand` | `(String pName)` |
```

**Therefore the only complete command list is the live one** — `CommandLineHelper.GetCommands()`,
which `ftk2_list_commands` already surfaces. Do not attempt static enumeration.

What *can* be recovered statically is the command **name strings**, and from their locality in the
`#US` literal heap, which type registers them. Extracted from `FTK2.dll` by UTF-16 literal scan:

| Registering type (by string locality) | Command names, verbatim |
|---|---|
| `AdventureDirector` | `KillPlayer`, `SendCommand`, `GetSpecificThing`, `ToggleUI`, `ToggleOverlay`, `ToggleUIAndOverlay`, `FillLifePool`, **`SpawnSpecificEnemy`**, `SetPlayerHealth` |
| `DirectorBase` / `GameplayDirectorBase` | `EndPhase`, `SetPlayersTo9999HP` |
| `CombatPhase` | `EquipSpecificThing`, `ToggleOutline`, `ToggleVenueGrid` |
| `MainMenuDirector` and global | `saveUser`, `SetSeed`, `ResetSeed`, `ForceNextRollResult`, `SetTarot`, `SetSlotRollResult`, `SetStat`, `SetStatLocal`, `RemoveStat`, `ClearStat`, `ToggleCheat`, `SetVoidTrinketXP`, `SetDungeonCrawlSeed`, `SetDebugTimeoutMs`, `ToggleDevLabels`, `LoadDObject`, `EnableDLC`, `DisableDLC`, `ToggleAllDLC`, `MultiplayerDemoQuickCombat`, `MultiplayerDemoQuickDungeon`, `MultiplayerDemoQuickAdventure`, `CompareVersion` |
| **NOT commands** | `EndTurn`, `ToggleInventory`, `CycleLeft`, `CycleRight`, `TogglePlayerSummary`, `Toolbelt`, `TabLeft`, `TabRight` — these are `InputController` action names and sit in a different string cluster. Do not mistake them for console commands. |

Which type registers a command decides **which route it is available on**. `GetSpecificThing` is
ADVENTURE-only; `EquipSpecificThing` is COMBAT-only. The string-locality-to-owner mapping is ASSUMED
(the `#US` heap groups literals by declaring method, which is a compiler convention, not a
guarantee) — confirm against `ftk2_list_commands` on each route.

`MultiplayerDemoQuickCombat` is documented in `FTK2.DevKit/sandbox/TypeProbe/README.md` as
*"registered and callable and still throws `NotImplementedException`"*. **Do not use it.**

### 5.1 The routes, ranked

There is no single "start a combat" API. Combat is the `eRoutes.COMBAT` phase inside the
`eRoutes.VENUE` route, and a venue is entered by acting on an **encounter entity** that exists on the
map. So every route decomposes into *(a) make an encounter exist* and *(b) act on it*.

`eRoutes` — all 21 values, verbatim: `ADVENTURE`, `ADVENTURE_SELECTION`, `CINEMATIC_CREDITS`,
`CINEMATIC_INTRO`, `CINEMATIC_OUTRO`, **`COMBAT`**, `DUNGEON`, `ENCOUNTER`, `EXIT`, `FORTUNE`,
`INTRO`, `LORE_STORE`, `MAIN_MENU`, `MULTIPLAYER_LOBBY`, `NONE`, `PARTY_MANAGEMENT`, `REST`, `TRAP`,
`TREASURE`, **`VENUE`**, `WHEEL`.

---

#### Rank 1 — `SpawnSpecificEnemy` (console) then `_performVenueAction`. Highest likelihood.

Why first: `SpawnSpecificEnemy` is a **shipped, registered console command on the ADVENTURE route**,
executable today through `ftk2_exec` with zero new marshalling.

Its handler could not be located statically — no method named `*SpawnSpecific*` exists on
`AdventureDirector` or any of its nested display classes (searched). Read the argument shape live
from `CommandLineHelper.TryGetCommand("SpawnSpecificEnemy", out ...)`.

Enemy config ids are the `CharacterHelper.DEBUG_*` string constants — 33 of them, CONFIRMED fields:
`DEBUG_ALCHEMIST`, `DEBUG_BAT`, `DEBUG_BEASTMAN`, `DEBUG_BLACKSMITH`, `DEBUG_BUSKER`, `DEBUG_FARMER`,
`DEBUG_FRIAR`, `DEBUG_GHOST`, `DEBUG_GOBLIN`, `DEBUG_GUARD`, `DEBUG_GUARD_ARCHER`, `DEBUG_HERBALIST`,
`DEBUG_HOBO`, `DEBUG_HUNTER`, `DEBUG_JELLY`, `DEBUG_LEPRECHAUN`, `DEBUG_MIMIC`, `DEBUG_MINSTREL`,
`DEBUG_PATHFINDER`, `DEBUG_SCHOLAR`, `DEBUG_SHEPHERD`, `DEBUG_SNAKE`, `DEBUG_SPIRIT`,
`DEBUG_STABLEBOY`, `DEBUG_TROLL`, `DEBUG_WILDMAGE`, `DEBUG_WITCH`, `DEBUG_WOLF`, `DEBUG_WOODCUTTER`.

Then act on it:

```
AdventureHelper   | method | `Entity` | `GetPlayerEncounter`  | `(Entity pCharacter, Env pEnv)` |
AdventureDirector | method | `Void`   | `_performVenueAction` | `(Entity pActiveCharacter, Entity pEncounterEntity, String pLootTableArg)` |
```

**Why `_performVenueAction` is the combat-entry method.** The only two producers/consumers of
`CombatEncounterArgs` in the entire assembly are `MapGenHelper._createDefaultCombatArg(Boolean
pIsCamp, Boolean pIsBoat, List`1 pProperties)` and
`EncounterHelper.UpdateCombatArgsVenueInclusionRules(List`1 pProperties, CombatEncounterArgs
pCombatArgs, Boolean pIsBoat)`, and `_performVenueAction` carries a closure over that type:
`AdventureDirector+<>c.<_performVenueAction>b__226_11(CombatEncounterArgs x) -> Boolean`. The
signature and the closure are CONFIRMED; that the method routes to VENUE is **ASSUMED**.

`eEncounterActions.VENUE` (CONFIRMED enum member) is the corresponding menu action, reachable via
`AdventureDirector._performEncounterAction(EncounterActionContext pMenuContext, Boolean
pAllowBroadcast)` — but `EncounterMenuViewHelper2+EncounterActionContext` has 15 fields including
`VisualElement Layout` and `OnActionButtonUse OnButtonUse`, so building one is strictly harder than
calling `_performVenueAction` directly.

**Proof of arrival:** `RouterHelper.GetCurrentRoute() == eRoutes.VENUE`, then
`RouterHelper.IsInVenue()`, then route `COMBAT` with `Env.GameRun.CombatState.Entities` populated.

---

#### Rank 2 — the `DebugHelper` spawn-enemy closure. Fully reflective, no console dependency.

```
DebugHelper | method | `Void` | `GenerateSpawnEnemiesDebugMenuButtons` | `(Dictionary`2 pDebugButtons, List`1[,] pHexMap, Env pEnv, List`1 pPlayerCharacterEntities, Action`2 pVisualCallback)` |
```

The button bodies are a captured local function on a display class with exactly four fields, **all
of them obtainable at runtime**:

```
DebugHelper+<>c__DisplayClass43_0.<GenerateSpawnEnemiesDebugMenuButtons>g___spawnEnemy|0(String pEnemy) -> Void
FIELD DebugHelper+<>c__DisplayClass43_0.pHexMap                  : List`1[,]   <- Env.HexMap (prop)
FIELD DebugHelper+<>c__DisplayClass43_0.pPlayerCharacterEntities : List`1      <- CoreHelper.GetParty(Env.GameRun.Entities)
FIELD DebugHelper+<>c__DisplayClass43_0.pEnv                     : Env         <- RouterHelper.Env (prop)
FIELD DebugHelper+<>c__DisplayClass43_0.pVisualCallback          : Action`2    <- null (RISK: may be dereferenced)
```

Instantiate the display class, set the four fields, call `g___spawnEnemy|0("DEBUG_TROLL")`. This is
the same machinery the shipped debug menu uses, reached without any UI. **Risk:** `pVisualCallback`
being null. Ranked 2 rather than 1 only because it is new plumbing while the console command already
exists.

Sibling generators with the same shape, all CONFIRMED, worth the same treatment:
`GenerateSpawnEncountersDebugMenuButtons`, `GenerateSpawnItemsDebugMenuButtons`,
`GenerateSpawnStatusesDebugMenuButtons`, `GenerateSpawnFollowersDebugMenuButtons`.

---

#### Rank 3 — `_trickleEnemies`. Random enemies onto the current map.

```
AdventureDirector | method | `Task`1` | `_trickleEnemies` | `(GameRandom pRandom, Decimal pMinNumber, Decimal pMaxNumber)` |
```

Two `Decimal` parameters — **`Decimal` is not in `ArgCoercion`'s vocabulary** (§6), though it is
trivial to add. Produces enemies at engine-chosen positions; rank 1's `GetPlayerEncounter` plus
`_performVenueAction` then finish the job. Less deterministic than ranks 1 and 2, so lower for a test
harness that wants a named enemy.

---

#### Rank 4 — move onto an enemy hex.

```
AdventureDirector | method | `Task` | `_move` | `(ValueTuple`2 pGoal, Boolean pConsumeActionPoints, Boolean pCanBeAmbushed, Boolean pShowEncounterMenu)` |
AdventureDirector | method | `Task` | `_move` | `(AdventureMoveData pMoveData, Boolean pConsumeActionPoints, Boolean pCanBeAmbushed, Boolean pShowEncounterMenu)` |
AdventureDirector | method | `AdventureMoveData` | `_getAdventureMoveData` | `(Entity pCharacter, ValueTuple`2 pGoal, Boolean pConsumeActionPoints)` |
AdventureDirector | method | `Void` | `_onConfirmLeftClickMapHex` | `(ValueTuple`2 pMapHex)` |
```

`pCanBeAmbushed: true` with `pShowEncounterMenu: true` is the natural-flow path. It requires an enemy
already on the map, so it composes with ranks 1-3, and it burns action points and turns — which a
combinatorial suite would rather not do. `HexHelper.StringToCoord(String)` supplies the tuple.

**Stuck risk, carried over from `crucible-traversal-inventory.md`:** `_move` can leave the game in a
hex-pick mode (`_onStopPickHex()`, `_renderPickHexCursor(ValueTuple`2)`, `_userPickHex`,
`_userPickHexValidHexes` — all CONFIRMED members) waiting on a click that never comes.

---

#### Rank 5 — `RouterMono.Route(eRoutes.VENUE, ...)` with a hand-built `CombatEncounterArgs`.

```
RouterMono    | method | `Void`   | `Route` | `(eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData, Boolean pReload, Boolean pForceRoute)` |
Env           | field  | `Object` | `PhaseData` | `` |
VenueDirector | method | `Void`   | `Initialize` | `(Scene pScene, Int32 pRandomSeed, UIDocument pCanvas2D, GameObject pCanvas3D)` |
VenueDirector | method | `Void`   | `_prepareVenuePartyForCombat` | `()` |
VenueDirector | method | `Void`   | `_prepareVenuePartyForCombat` | `(List`1 pParty)` |
VenueDirector | method | `Void`   | `_goToNextRoute` | `(eRoutes pRoute)` |
```

`CombatEncounterArgs` (verbatim) is the declarative combat description a test would most want:

```
| field | `List`1` | `EnemyNames` | `` |
| field | `List`1` | `EnemyGuids` | `` |
| field | `Int32`  | `WaveAmount` | `` |
| field | `String` | `BossID`     | `` |
| field | `Boolean`| `IsCamp`     | `` |
| field | `String` | `LootDropID` | `` |
| field | `String` | `VenueDioConfigName` | `` |
| field | `eCombatMusics` | `MusicType` | `` |
| field | `eCharacterTypes` | `CharacterType` | `` |
| field | `List`1` | `AlternateWinConditions` | `` |
| field | `List`1` | `AlternateLoseConditions` | `` |
| field | `List`1` | `FirstInitiativeEntities` | `` |
| field | `eVenueInclusionRules` | `AllyVenueInclusion` | `` |
| field | `eVenueInclusionRules` | `EnemyVenueInclusion` | `` |
```

`Route`'s `pCustomData` is `Object` and `Env.PhaseData` is `Object`, so a `CombatEncounterArgs`
almost certainly travels one of those (ASSUMED). **This is the most powerful route and the least
proven.** `ArgCoercion` refuses `Object` by deliberate design, and `VenueDirector.Initialize` wants a
loaded `Scene`. Rank it 5 now; promote it if a `CombatEncounterArgs` builder proves out, because it
would let a recipe name its enemies directly instead of spawning and walking.

---

#### Rank 6 — dungeon phases.

`DungeonDirector._nextPhase()` / `._loadNextPhase(List`1 pPhaseList)` with `ePhases.COMBAT`
(`crucible-traversal-inventory.md`). Reaches combat, but inside a dungeon context with its own
quit-refusal latches (`_canQuitToMenu`, `_quitToMenuQueued`). Only if the overworld routes all fail.

#### Rank 7 — mimic transform.

`TreasurePhase._mimicTransformAndStartCombat(Boolean pIsPlayerAmbush, Entity pCharacter)` and
`FortunePhase._mimicTransformAndStartCombat(GameObject pHoverTile)` — CONFIRMED signatures; the
`FortunePhase` one wants a `GameObject`. Route of last resort. Related arming flags on
`AdventureState`: `IsDevMimicReady`, `IsDevEnemyReady` (both `Boolean`, CONFIRMED), and
`TwitchChatController.ForceTriggerMimic()` / `.ForceTriggerDevEnemy()` (both `Void`, zero-arg,
CONFIRMED — `TwitchChatController.Instance` is a static field, so `TryResolveInstance` resolves it
trivially).

#### REFUTED — do not use

- `MultiplayerDemoQuickCombat` — throws `NotImplementedException`.
- **`CombatDirector` — probed, NOT FOUND.** There is no such type; the combat owner is `CombatPhase`.
- `_debugEndPhase` — it *ends* a phase. It does not start combat.

### 5.2 Once in combat

```
CombatState | field  | `List`1`       | `Entities`         | `` |
CombatState | field  | `List`1`       | `RoundEntities`    | `` |
CombatState | field  | `Dictionary`2` | `EntityInitiative` | `` |
CombatState | field  | `Int32`        | `TotalRounds`      | `` |
CombatState | field  | `Int32`        | `WaveIndex`        | `` |
CombatState | field  | `Int32`        | `EnemiesPerWave`   | `` |
CombatState | field  | `Int32`        | `DeadEnemiesCount` | `` |
CombatState | field  | `Boolean`      | `EndCombatEarly`   | `` |
CombatState | field  | `Boolean`      | `IsDungeon`        | `` |
CombatState | field  | `GameRandom`   | `Random`           | `` |
CombatState | field  | `eVenueGrids`  | `GridType`         | `` |
CombatState | method | `CombatState`  | `Create`           | `()` |
CombatHelper| method | `Boolean`      | `TryEndGame`       | `(CombatState pCombatState)` |
CombatHelper| method | `Entity`       | `GetEmptyTile`     | `(CombatState pCombatState, Int32 pGroupIndex, GameRandom pGameRandom)` |
CombatHelper| method | `List`1`       | `GetCombatWaves`   | `(List`1 pEnemies, Int32 pTileAmount, Int32 pEnemyAmountOverride)` |
CombatHelper| method | `List`1`       | `GetCombatOrder`   | `(CombatState pCombatPhaseData, Boolean pIsMidcombat)` |
CombatHelper| method | `Void`         | `SetInitiative`    | `(Entity pEntity, List`1 pAllies, CombatState pCombatState, GameRunData pGameRun, List`1 pResults, Boolean pTrySkillProc)` |
CharacterHelper | method | `List`1`   | `GetEnemiesForCombat` | `(EnemySet pEnemySet, GameRandom pGameRandom, Int32 pLevel, Int32 pThreat, GameRunData pGameRunData, eGetEnemyRules pGetEnemyRule, BiomeDefaultsConfig pBiomeConfig)` |
CharacterHelper | method | `List`1`   | `GetEnemiesForAmbush` | `(Entity pActivePlayer, String pAmbushEnemy, Int32 pRequiredEnemies, List`1 pPlayerEntities, Int32 pLevel, Env pEnv, GameRandom pGameRandom)` |
```

`Env.GameRun.CombatState` is a plain field chain, so combat-state assertions need no instance
resolution at all. Ability execution stays as `crucible-traversal-inventory.md` concluded:
`CombatPhase._performAbility(Entity, Thing, CombatDecisionData, List`1, Boolean pTryProceed, Boolean
pIsScripted)`, **not** `CombatHelper.PerformAbility` (which needs two delegates and a `SkillContext`
the harness cannot synthesise).

---

## 6. Argument marshalling gaps, and the addressing scheme to close them

### 6.1 What exists today

`FTK2.Crucible/src/Crucible.Core/ArgCoercion.cs` (139 lines) exposes exactly:

```
public static bool TryCoerce(string raw, Type targetType, out object value, out string error)
```

and handles `string`, `int`, `float`, `double`, `bool`, enums, `object` (null token only, by
deliberate design — see its own comment naming `RouterMono.Route`'s `pCustomData` as the motivating
case), and `System.Threading.CancellationToken` (`"-"` -> `CancellationToken.None`).

It is **pure and has no game reference**, which is what makes it unit-testable with no game running —
and also what makes it structurally incapable of resolving a game object.
`ReflectionCommands.cs:663` is the call site; `ReflectionCommands.TryResolveInstance` (`:719`) already
resolves *receiver* instances via Instance/Current, then `FindObjectOfType`, then a private field on
the live `RouterMono`.

**Design conclusion: do not teach `ArgCoercion` about game types.** Add a **resolver seam** — an
optional resolver delegate that `ArgCoercion` consults before failing — and implement the game-aware
resolver in `Crucible.Plugin`, where reflection into the game is already the norm. `ArgCoercion`
stays pure and stays testable; the resolver gets its own tests against a fake resolver.

Separately, remember the *shipped* marshaller's limit, already established at
`docs/superpowers/plans/2026-08-23-s3-mcp-tool-surface.md` line 25: **`CommandLineHelper`'s vocabulary
is `int / float / double / bool / string / Vector2 / Vector3` — enums are NOT in it.** So every
Crucible *handler* must still declare `string`/`int`/`bool` parameters and parse inside the body.
The addressing schemes below are therefore all **string** syntaxes.

### 6.2 The gaps, per mutation

| Needed by | Parameter type | Expressible today? | Proposed addressing scheme (string syntax) |
|---|---|---|---|
| every character verb | `Entity` (character) | **No** | `p0`..`p3` -> `CoreHelper.GetParty(Env.GameRun.Entities)[N]`. Also `@<guid>` -> scan `Env.GameRun.Entities` for `Entity.Guid` equality; `name:<DisplayName>` -> match `CharacterComponent.DisplayName`. Out-of-range must be an error, never a silent slot 0. |
| `_performVenueAction`, encounter verbs | `Entity` (encounter) | **No** | `enc:under:p<N>` -> `AdventureHelper.GetPlayerEncounter(party[N], RouterHelper.Env)`; `enc:@<guid>` -> guid scan of `Env.GameRun.Entities`. |
| combat verbs | `Entity` (combatant) | **No** | `c<i>` -> `Env.GameRun.CombatState.Entities[i]`; `e<i>` -> the `CharacterHelper.IsEnemy(Entity)`-filtered view of the same list. |
| `Equip(Thing,..)`, `Unequip`, `RemoveTrait`, `Consume`, `TryEmptyEquippedWeapon` | `Thing` | **No** | `p<N>/<CONFIG_ID>` -> `InventoryHelper.GetCharacterThing(party[N], CONFIG_ID, 1)`; `p<N>#<thingId>` -> `InventoryHelper.GetCharacterThingByID(party[N], thingId)`; `p<N>:slot:<SLOT>` -> `EquipmentHelper.GetEquippedThingBySlot(slot, party[N])`. **Refuse on null rather than passing null** — a null `Thing` into `Equip` is a silent no-op that looks like success. |
| `_move`, `_onSelectHexPositionTeleportScroll`, `_placeMarker`, `_onConfirmLeftClickMapHex`, every `HexHelper` predicate | `ValueTuple`2` | **No** | **`HexHelper.StringToCoord(String pCoord)` — CONFIRMED. This is the game's own parser; use it, do not write another.** Also accept `hex:under:p<N>` -> `AdventureComponent.HexPosition` via `Entity.TryGetAdventureComponent(Env.GameRun, out ..)`. |
| `GiveByName`, `Give`, `GiveBulk` | `List`1` (inventory) | **No** | `inv:p<N>` -> `party[N].Get<CharacterComponent>().Things`. Remember the asymmetry from §2.2: `GiveByName` wants the list, `TakeByName` wants the entity. |
| `CoreHelper.GetParty`, `GetPlayersAndFollowers`, `InitializePartyStats`, `DebugGetSpecificThing`, `TryReloadAllPlayerWeapons` | `List`1` (entities/players) | **No** | `entities` -> `Env.GameRun.Entities`; `party` -> `CoreHelper.GetParty(Env.GameRun.Entities)`. |
| most `CharacterHelper` mutators | `List`1 pResults` (diagnostic sink) | **No** | `-` -> a fresh empty `List<>` of the parameter's element type via `Activator.CreateInstance(paramType)`. Contents are diagnostics; discard them. Prefer sink-free overloads where one exists. |
| `TryProgressCharacterEntityToLevel`, `Equip(String,..)`, `GiveByName`, `_trickleEnemies`, `EquipRandomThingForSlotDebug`, `CreatePlayableCharacterEntity` | `GameRandom` | **No** | `-` -> `Env.GameRun.CombatState.Random` when in combat, else the owning director's `_gameRandom` (`AdventureDirector._gameRandom`, `PartyManagementDirector._gameRandom` / `._visualGameRandom` — all CONFIRMED fields). `seed:<n>` -> construct a `GameRandom` and set its `Seed` field (`Int32`, CONFIRMED). **Never silently invent a fresh unseeded one** — that breaks determinism, which `crucible-rng-field-map.md` treats as load-bearing. |
| `_trickleEnemies`, `_spawnHazardInZone`, `GetChanceValue` | `Decimal` | **No** | Trivial: `decimal.TryParse` with `NumberStyles.Number` and `CultureInfo.InvariantCulture`. Add to `ArgCoercion` proper — it is a BCL type, so purity is preserved. |
| `SaveGameHelper.WriteSaveData`, `WriteManualSave`, `EncounterHelper.*`, `CharacterHelper.GetEnemyMaxLevel` | `GameRunData` | **No** | `-` -> `Env.GameRun`. One well-known instance, no ambiguity. |
| `EncounterHelper.*`, `EquipRandomThingForSlot`, `AdventureHelper.GetPlayerEncounter`, `SaveGameHelper.IsManualSavingAllowed` | `Env` | **No** | `-` -> `RouterHelper.Env` (prop, CONFIRMED). |
| `SaveGameHelper.WriteSaveData` | `UserData` | **No** | `-` -> `Env.User`. |
| `CharacterHelper.GetDisplayName`, `HasStat`, `EquipmentHelper.GetEquippedThing`, `IsEquipped` | `CharacterComponent` | **No** | `p<N>` -> `party[N].Get<CharacterComponent>()`. Same slot syntax as `Entity`, disambiguated by the parameter type. |
| `DebugHelper` display-class fields | `List`1[,]` (hex map) | **No** | `-` -> `Env.HexMap` (prop, `List`1[,]`, CONFIRMED). |
| `_performAbility`, `PerformAbility`, `_performEncounterAction` | `CombatDecisionData`, `SkillContext`, `RollResultData`, `EncounterActionContext`, `Func`4`, `Func`3`, `Action`1`, `Action`2` | **No** | **Out of scope. Declare unsupported and say so in the error message.** Do not fabricate. The one exception: a nullable `Action`/`Action`1`/`Action`2` parameter may accept `-` -> `null`, but only where a null callback is plausible (`DebugGetSpecificThing`'s `pRefresh`), and it must be flagged as ASSUMED in the verb's own doc. |
| `RouterMono.Route`, `Env.PhaseData` | `Object` (`CombatEncounterArgs`) | **No, deliberately** | Leave `ArgCoercion`'s refusal in place. If rank 5 (§5.1) is promoted, build a dedicated `crucible_combat_args` verb that constructs the object from named string/int fields — do not widen `object` coercion. |
| enum params (`eEquipmentSlots`, `eCharacterStats`, `eRoutes`, `eSummonTypes`) | enum | **Yes** | `ArgCoercion` already handles enums. Prefer the `String` overload where the game offers one (every `CharacterHelper` stat method does). |

### 6.3 Three rules the resolver must follow

1. **Never silently default.** `ArgCoercion`'s existing doc-comment is explicit: *"a non-numeric
   string targeting `int` is a failure, not a 0. A silent 0 would make a mis-typed argument invoke
   the right method with the wrong data instead of refusing outright."* The same must hold for `p9`
   on a four-person party and for a `Thing` the character does not hold. Refuse, and put the resolved
   candidates in the error text.
2. **Resolution happens on the main thread, at invoke time.** Every resolver above reads live game
   state, so it must run inside the same `MainThreadPump` step as the invocation, not at parse time.
3. **Every resolver is a pure function of a string plus live state, and is logged.** A recipe that
   fails must show *what `p2` resolved to*, or debugging a 60-recipe suite is hopeless.

---

## 7. Persistence — the second hard correction

**`saveUser` does not save the run.** Verbatim:

```
SaveGameHelper | method | `Task`   | `SaveUserAsync`      | `(UserData pUser)` |
SaveGameHelper | method | `Task`   | `_saveUserAsync`     | `(UserData pUser)` |
SaveGameHelper | method | `Task`   | `WriteUserDataAsync` | `(String pFilePath, UserData pUser)` |
SaveGameHelper | method | `Task`   | `BackUpUser`         | `(String pSourceFilePath)` |
SaveGameHelper | field  | `String` | `UserDirectoryPath`  | `` |
```

`saveUser` -> `SaveUserAsync(UserData)` -> `WriteUserDataAsync` -> `UserDirectoryPath`. It writes
**`UserData`**: settings, `LocalStats`, `SeenTutorialIds`, `LastGameRunIdPlayed`, and the
`_partyCharacters` / `_lastRunCharacters` rosters. **`GameRunData` is not a parameter anywhere on
that path.** Any mutation to `Env.GameRun` — party, items, traits, levels, health — is **not**
captured by `saveUser`.

The observation that "`saveUser` wrote a new save tonight, GameRuns 20 -> 21" is therefore **not
evidence that it persists a run.** `Env.GameRuns` is the run *list*, and the run-write path is
separate. Re-attribute that observation before relying on it. This is precisely the failure mode the
brief warned about: an API that does not do what its name implies.

**The run-persisting calls, all CONFIRMED signatures:**

```
SaveGameHelper | method | `Task`1` | `WriteSaveData`     | `(String pGameRunId, GameRunData pGameRun, UserData pUser)` |
SaveGameHelper | method | `Task`1` | `WriteManualSave`   | `(String pGameRunId, GameRunData pGameRun, String pSaveName, String pOverwriteFileName)` |
SaveGameHelper | method | `Task`   | `WriteRunDataAsync` | `(String pFilePath, GameSaveData pRunSummary, GameRunData pRunData)` |
SaveGameHelper | method | `GameSaveData` | `CreateGameSaveData` | `(String pRunId, GameRunData pGameRun)` |
SaveGameHelper | method | `Boolean`| `IsManualSavingAllowed` | `(Env pEnv)` |
SaveGameHelper | method | `Task`1` | `ListGameRuns`      | `()` |
SaveGameHelper | method | `List`1` | `ListGameRunsSync`  | `()` |
SaveGameHelper | method | `Void`   | `DeleteSave`        | `(String pFileId)` |
SaveGameHelper | method | `Task`1` | `ConvertManualSaveToAutoSave` | `(GameSaveData pGameSaveData)` |
```

**Recommended persist verb:** `WriteSaveData(Env.SelectedGameRunId, Env.GameRun, Env.User)` — three
arguments, two resolving to well-known singletons (§6.2), the third a `String` already on `Env`
(`SelectedGameRunId`, CONFIRMED field).

**Recommended fixture-generator verb:** `WriteManualSave(gameRunId, Env.GameRun, pSaveName,
pOverwriteFileName)` — `pSaveName` and `pOverwriteFileName` are plain strings, which makes stable,
readable, per-recipe fixture names possible. That is the better tool for a 60-recipe suite that wants
its own named base states.

**Gate:** `IsManualSavingAllowed(Env)` -> `Boolean`. Combat and some phases refuse manual saving —
check it before treating a failed save as a bug. `PartyManagementDirector._isJipSavingAllowed()` is
the multiplayer sibling. `Env.ManualSaveIdTracker` and `SaveGameHelper.MANUAL_SAVE_ID_DELIMITER` /
`MAX_SAVE_NAME_LIMIT` are the naming constraints.

**Verification of persistence** — ASSUMED procedure, every step a CONFIRMED API:

1. mutate `Env.GameRun`
2. `WriteSaveData(..)` / `WriteManualSave(..)`
3. `SaveGameHelper.ListGameRunsSync()` shows the run id
4. route to `MAIN_MENU`, reload via `AdventureSelectionDirector._loadGameRun(GameSaveData
   pGameSaveData, String pDisableLogMessage)` (`crucible-load-path.md` §3)
5. re-read the mutated field

**Anything short of a reload does not prove persistence.**

**Why offline save editing is not the answer.** `crucible-save-load-feasibility.md` §6 established
the `.ftk2` format is not plain text. This pass explains why:

```
SaveGameHelper | method | `Byte[]` | `LZ4Compress`   | `(String pData)` |
SaveGameHelper | method | `String` | `LZ4Decompress` | `(Byte[] pData)` |
SaveGameHelper | method | `String` | `_encryptOrDecrypt` | `(String pData)` |
SaveGameHelper | method | `Void`   | `_encryptOrDecrypt` | `(Byte[] pUnicodeBytes)` |
SaveGameHelper | method | `Char`   | `_encryptOrDecryptChar` | `(Char pChar, Int32 pIndex)` |
SaveGameHelper | field  | `String` | `encryptString` | `` |
SaveGameHelper | method | `Task`1` | `GetCompressedBytesForRun` | `(GameRunData pRunData)` |
SaveGameHelper | method | `Task`1` | `GetRunFromCompressedBytes` | `(Byte[] pBytes, Int32 pOffset, Int32 pLength)` |
```

LZ4 under a positional XOR. Reproducible in principle — `GetCompressedBytesForRun` /
`GetRunFromCompressedBytes` are the round-trip pair — but it is a whole serialisation project.
**In-memory mutation plus `WriteSaveData` is strictly cheaper.** The brief's strategic insight is
**validated**, with the save call corrected.

---

## 8. What could not be resolved statically

Stated plainly, because a clear "no API found, here is what I probed" is worth more than a plausible
guess.

1. **The handler behind `SpawnSpecificEnemy`.** The command string is registered by
   `AdventureDirector`; no method of that name exists on the type or on any of its nested display
   classes. Read the signature live from `CommandLineHelper.TryGetCommand("SpawnSpecificEnemy", ..)`.
2. **Whether `_rebuildCharactertAsNewConfigType` survives being called off-route.** Signature
   CONFIRMED, UI-field dependencies CONFIRMED, outcome unknowable without running it.
3. **Whether `CoreHelper.GetParty` returns slots in stable, creation order.** Cross-check live
   against `CharacterComponent.GroupIndex`.
4. **Whether `_performVenueAction` actually routes to VENUE.** Signature and the
   `CombatEncounterArgs` closure are CONFIRMED; the body was not inspected.
5. **Whether replacing an `Entity` (§1.3c) leaves stale references.** Suspects: `Env.CachedEntities`,
   `CombatState.Entities`, `GameRunData.PlayerFollowers`, `AdventureState.RoundPlayerNames`.
6. **Closed generic element types.** TypeProbe prints `List`1` without arguments. Every `List`1`
   quoted above has an ASSUMED element type, inferred from sibling signatures.
7. **`DebugHelper+<>c__DisplayClass43_0.pVisualCallback` null-tolerance.**
8. **Whether `DebugGetSpecificThing` grants to one character or to all.** The parameter is
   `List`1 pPlayers`; "all" is ASSUMED.
9. **Which route each shipped command is actually live on.** The string-locality mapping in §5.0 is
   ASSUMED; `ftk2_list_commands` on each route is the confirmation.
10. **Whether `Env.PhaseData` or `RouterMono.Route`'s `pCustomData` is where `CombatEncounterArgs`
    travels.** Both are typed `Object`; neither was traced.

---

## Related documents

- `docs/research/crucible-traversal-inventory.md` — per-route act/proof/stuck-risk inventory; the
  authority on `_debugEndPhase` shapes and phase ownership.
- `docs/research/crucible-load-path.md` — main menu to loaded save; `AdventureSelectionDirector._loadGameRun`.
- `docs/research/crucible-save-load-feasibility.md` — `.ftk2` file-format inspection.
- `docs/research/crucible-combat-field-map.md` — `CombatState` field semantics.
- `docs/research/crucible-rng-field-map.md` — every `GameRandom` instance and why seeding matters.
- `docs/research/eor-trait-mechanism.md` — trait content authoring.
- `docs/superpowers/plans/2026-08-23-s3-mcp-tool-surface.md` §"Global Constraints" — the
  `CommandLineHelper` marshaller vocabulary.
- `docs/superpowers/plans/2026-08-23-s7-state-construction.md` — the implementation plan derived from
  this document.
