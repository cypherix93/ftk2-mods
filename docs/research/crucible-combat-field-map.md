# Crucible combat field map (TypeProbe-verified)

Produced by running `dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- <TypeName> [--methods]`
against the live `FTK2.dll` (5389 types loaded from
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed`).
Goal: give S1 measured member names for the combat snapshot shape
`{active, round, turn, phase, combatants[]}` where each combatant needs
`{id, name, classId, isPlayer, hp, maxHp, alive, statuses[{id,stacks,duration}], stats{}}`.

Every table below is copied verbatim from actual TypeProbe stdout. Nothing here is guessed.

---

## Types found (verbatim TypeProbe output)

### CombatState

| Kind | Type | Name |
|---|---|---|
| field | `List\`1` | `AbilityHistory` |
| field | `Dictionary\`2` | `AiAbilities` |
| field | `List\`1` | `AlternateLoseConditions` |
| field | `List\`1` | `AlternateWinConditions` |
| field | `Entity` | `AnonymousEntity` |
| field | `Boolean` | `AnonymousEventTriggered` |
| field | `BossFightState` | `BossFightState` |
| field | `Int32` | `BossLevelOverride` |
| field | `String` | `CachedActiveEntityMainHandThingId` |
| field | `Int32` | `DeadEnemiesCount` |
| field | `Boolean` | `EndCombatEarly` |
| field | `Int32` | `EnemiesPerWave` |
| field | `List\`1` | `Entities` |
| field | `Dictionary\`2` | `EntityInitiative` |
| field | `List\`1` | `GrabbedEntities` |
| field | `eVenueGrids` | `GridType` |
| field | `List\`1` | `InterruptedEntityIDs` |
| field | `Boolean` | `IsDungeon` |
| field | `List\`1` | `PropagateIgnoreEntities` |
| field | `GameRandom` | `Random` |
| field | `List\`1` | `RoundEntities` |
| field | `Dictionary\`2` | `SkillProcsCombat` |
| field | `Dictionary\`2` | `SkillProcsTurn` |
| field | `List\`1` | `StealHistory` |
| field | `Int32` | `TotalRounds` |
| field | `Entity` | `VehicleEntity` |
| field | `Int32` | `WaveIndex` |

No `Turn`, `Phase`, or `ActiveEntity` field exists on `CombatState`. `TotalRounds` is the only round-like
counter, and per `docs/research/eor-rehost-mp-review.md` N2 it is **not monotonic** — it initialises to
`-1` (`CombatState.cs:65`) and is reset to `-1` again per-wave (`CombatPhase.cs:2665`).

### Entity

| Kind | Type | Name |
|---|---|---|
| field | `String` | `<Guid>k__BackingField` |
| field | `Dictionary\`2` | `Components` |
| field | `Func\`2` | `CustomToString` |
| field | `UInt32` | `_Id` |
| field | `UInt32` | `__ID` |
| prop | `String` | `Guid` |

### CombatComponent

| Kind | Type | Name |
|---|---|---|
| field | `Boolean` | `CanSummon` |
| field | `Int32` | `DeathSaves` |
| field | `Boolean` | `DodgeCooldown` |
| field | `JuggleState` | `JuggleState` |
| field | `Int32` | `PrimaryActions` |
| field | `Int32` | `SecondaryActions` |

No HP field here — health lives on `CharacterComponent`, not `CombatComponent`.

### CharacterComponent

| Kind | Type | Name |
|---|---|---|
| field | `Dictionary\`2` | `BaseStatModifiers` |
| field | `Int32` | `CachedFocus` |
| field | `eCharacterTypes` | `CharacterType` |
| field | `String` | `ConfigName` |
| field | `Int32` | `CurrentFocus` |
| field | `Int32` | `CurrentHealth` |
| field | `Dictionary\`2` | `CustomData` |
| field | `String` | `DisplayName` |
| field | `Dictionary\`2` | `Equipped` |
| field | `Int32` | `ExtraLevel` |
| field | `Int32` | `ExtraLives` |
| field | `Int32` | `GroupIndex` |
| field | `Int32` | `NecroLives` |
| field | `String` | `Nomenclator` |
| field | `Dictionary\`2` | `OverrideStats` |
| field | `List\`1` | `Properties` |
| field | `Int32` | `RequiredFocus` |
| field | `String` | `SanctumID` |
| field | `Dictionary\`2` | `SkillBoolBags` |
| field | `Dictionary\`2` | `SkillProcCache` |
| field | `Dictionary\`2` | `SkinEquipped` |
| field | `eCharacterStates` | `State` |
| field | `List\`1` | `Things` |
| field | `String` | `TypeArgs` |
| field | `String` | `Variant` |
| field | `HashSet\`1` | `VisualProperties` |

No `MaxHealth` field — max HP is derived via `CharacterHelper.GetMaxHealth` (see below), not stored raw.
No `IsPlayer` bool — presence of `PlayerComponent` on the `Entity` is the signal (see `PlayerComponent`
below; corroborated by `docs/research/eor-rehost-mp-review.md` N5's note that friend/foe is decided by
`GroupIndex == 0`, verified at `CharacterHelper.cs:1483`).

### PlayerComponent

| Kind | Type | Name |
|---|---|---|
| field | `Int32` | `ActionPoints` |
| field | `Boolean` | `AllGoldenMovementRolls` |
| field | `String` | `AmbushEntity` |
| field | `Int32` | `ExtraMovementRolls` |
| field | `Boolean` | `HasMoved` |
| field | `ValueTuple\`2` | `RespawnHexPosition` |
| field | `Int32` | `RoundsLeftToAutoRevive` |
| field | `Int32` | `RoundsWithoutMoving` |
| field | `Boolean` | `Sneaked` |
| field | `Dictionary\`2` | `ThingsUserState` |
| field | `Int32` | `TurnsLeftToSkip` |
| field | `Int32` | `TurnsPlayed` |

Only present on player-controlled entities — that presence/absence is the practical `isPlayer` test.
`TurnsPlayed` is a **per-combatant** counter, not a global "current turn" value, so it does not resolve
the snapshot's top-level `turn` field.

### eCharacterTypes

| Kind | Type | Name |
|---|---|---|
| field | `eCharacterTypes` | `BOSS` |
| field | `eCharacterTypes` | `COMPANION` |
| field | `eCharacterTypes` | `CURSE` |
| field | `eCharacterTypes` | `FORCED_FIGHT` |
| field | `eCharacterTypes` | `INANIMATE` |
| field | `eCharacterTypes` | `MERCENARY` |
| field | `eCharacterTypes` | `NONE` |
| field | `eCharacterTypes` | `PROP` |
| field | `eCharacterTypes` | `STANDARD` |
| field | `Int32` | `value__` |

No `PLAYER` value — this enum classifies non-player combatant kinds (boss/mercenary/curse/prop/etc.), it
is not a player/enemy discriminator by itself.

### StatusEffectComponent

| Kind | Type | Name |
|---|---|---|
| field | `Dictionary\`2` | `Statuses` |

Found by grepping `FTK2.ClassForge/src/ClassForge.Plugin/Recipes/GameAdapters.cs`, which documents (and
consumes) it directly: `StatusEffectComponent.Statuses` is a `Dictionary<string, StatusEffectInfo>` keyed
by status id string. Confirmed present in the live assembly by TypeProbe.

### StatusEffectInfo

| Kind | Type | Name |
|---|---|---|
| field | `Int32` | `Duration` |
| field | `Int32` | `InitialDuration` |
| field | `String` | `OriginEntityId` |
| field | `Int32` | `TickDuration` |

No `Stacks`/`Count` field. Stacking in this engine appears to be represented by distinct dictionary keys
(observed convention elsewhere in the codebase: suffixed status ids like `STATUS_ATTACKUP_00`), not by a
numeric stack count on the instance — see UNRESOLVED note in the summary table.

### StatusEffectConfig (status *definition*, not the per-entity instance)

| Kind | Type | Name |
|---|---|---|
| field | `List\`1` | `AddProperties` |
| field | `SerializedSortedDictionary\`2` | `CustomStats` |
| field | `Int32` | `Duration` |
| field | `Boolean` | `GroupSync` |
| field | `List\`1` | `Passives` |
| field | `SerializedSortedDictionary\`2` | `Stats` |
| field | `Boolean` | `TickCombat` |
| field | `Boolean` | `TickExpire` |
| field | `Int32` | `TickFrequency` |
| field | `Boolean` | `TickOverworld` |
| field | `Boolean` | `TileSync` |
| field | `eStatusEffectTypes` | `Type` |

### CharacterConfig

| Kind | Type | Name |
|---|---|---|
| field | `String` | `BaseType` |
| field | `String` | `CampQuery` |
| field | `String` | `DefaultBodyType` |
| field | `eExpansions` | `Expansion` |
| field | `Int32` | `Level` |
| field | `String` | `LocKey` |
| field | `String` | `LootID` |
| field | `String` | `OnDeathAbility` |
| field | `List\`1` | `Passives` |
| field | `eItemRarities` | `Rarity` |
| field | `SerializedSortedDictionary\`2` | `Stats` |
| field | `String` | `SwarmQuery` |
| field | `List\`1` | `Tags` |
| field | `SerializedSortedDictionary\`2` | `Things` |
| field | `Int32` | `Threat` |

`Stats` is a `SerializedSortedDictionary<string, ...>` — stat keys are strings (there is no `eStats` enum
in the assembly at all, see below), consistent with `CharacterComponent.OverrideStats` /
`BaseStatModifiers` also being string-keyed dictionaries.

### CombatPhase (trimmed — only fields/props relevant to `active`/`phase`; full type has ~90 members,
mostly UI/rendering plumbing (canvases, HUDs, camera, music) that carry no combat-state data)

| Kind | Type | Name |
|---|---|---|
| field | `Entity` | `_lastEngagedEntity` |
| field | `List\`1` | `_completedPhases` |
| prop | `Entity` | `_activeCharacterEntity` |
| prop | `CombatState` | `_combatState` |
| prop | `VenueState` | `_state` |
| prop | `BossFightState` | `_bossFightState` |

(Full un-trimmed table was produced during the probe run; omitted here since ~85 of the ~90 members are
Unity/UI plumbing — `UIDocument`, `VisualElement`, `GameObject`, `Diorama`, `CharacterSummaryController`,
etc. — with no bearing on the snapshot shape.)

### RouterHelper

| Kind | Type | Name |
|---|---|---|
| field | `Dictionary\`2` | `PhaseToRoute` |
| field | `Boolean` | `_isInitialized` |
| field | `RouterMono` | `_router` |
| prop | `Boolean` | `AllowShareData` |
| prop | `Env` | `Env` |
| prop | `UIDocument` | `LoadingUIDocument` |

Not combat-related — this is scene/route navigation (`PhaseToRoute` maps *dungeon* phases to UI scenes,
not turn phases inside a fight).

### Env

| Kind | Type | Name |
|---|---|---|
| field | `List\`1` | `ActionReplayData` |
| field | `Dictionary\`2` | `BiomeToTown` |
| field | `Dictionary\`2` | `CachedEntities` |
| field | `Configs` | `Configs` |
| field | `Boolean` | `DebugDoRecordGameRandomStackTrace` |
| field | `Boolean` | `DoRecordReplay` |
| field | `CancellationTokenSource` | `FadeOutTransitionCancelTokenSource` |
| field | `GameRunData` | `GameRun` |
| field | `List\`1` | `GameRuns` |
| field | `Dictionary\`2` | `HexMaps` |
| field | `Dictionary\`2` | `HouseRulesCreation` |
| field | `List\`1` | `LoadingTips` |
| field | `ManualSaveIdTracker` | `ManualSaveIdTracker` |
| field | `Dictionary\`2` | `MapPropPrefabNames` |
| field | `NetworkData` | `NetworkData` |
| field | `Object` | `PhaseData` |
| field | `ReloadStagingData` | `ReloadStagingData` |
| field | `String` | `RoomAutoJoinId` |
| field | `String` | `SelectedAdventureConfig` |
| field | `eGameDifficulties` | `SelectedDifficulty` |
| field | `String` | `SelectedGameRunId` |
| field | `Dictionary\`2` | `UnloadOnDeinitMemoryCollections` |
| field | `UserData` | `User` |
| field | `VenueGameObjectMaps` | `VenueGameObjectMaps` |
| field | `VenueResults` | `VenueResults` |
| prop | `List\`1[,]` | `HexMap` |

`Env.GameRun` (`GameRunData`) is the root the run/combat state hangs off; `CombatState` itself is reached
via `GameRunData.CombatState` per `docs/research/eor-rehost-mp-review.md` ("`Env.GameRun.CombatState`").

### VenueState

| Kind | Type | Name |
|---|---|---|
| field | `eCombatMusics` | `CombatMusic` |
| field | `List\`1` | `FirstInitiativeEntities` |
| field | `String` | `LootTableArg` |
| field | `Int32` | `PhaseIndex` |
| field | `Boolean` | `PhaseSkip` |
| field | `String` | `VehicleID` |
| field | `VenueData` | `Venue` |
| field | `List\`1` | `VenuePlayerNames` |
| field | `WeatherData` | `WeatherData` |

`PhaseIndex` here is the **dungeon room/phase** index (which `PhaseConfig` in the current room sequence
is active — e.g. COMBAT vs REST vs TREASURE), not a turn-phase inside an active fight. See `ePhases` below.

### PhaseConfig / ePhases / ePhaseSubType

`PhaseConfig.PhaseType` is `ePhases`:

| Kind | Type | Name |
|---|---|---|
| field | `ePhases` | `BOSS` |
| field | `ePhases` | `BOSS_LOOP` |
| field | `ePhases` | `CHOICE` |
| field | `ePhases` | `CHOICE_POOL` |
| field | `ePhases` | `COMBAT` |
| field | `ePhases` | `COMBAT_PREVIEW` |
| field | `ePhases` | `CUTSCENE` |
| field | `ePhases` | `ENCOUNTER` |
| field | `ePhases` | `EXIT` |
| field | `ePhases` | `FORTUNE` |
| field | `ePhases` | `GRAB_BAG` |
| field | `ePhases` | `HALLWAY` |
| field | `ePhases` | `INSERT_DUNGEON` |
| field | `ePhases` | `INTRO` |
| field | `ePhases` | `NONE` |
| field | `ePhases` | `PHASE_LOOP` |
| field | `ePhases` | `PRESET` |
| field | `ePhases` | `REST` |
| field | `ePhases` | `SCHEDULED_EVENT` |
| field | `ePhases` | `STAIRS` |
| field | `ePhases` | `TRAP` |
| field | `ePhases` | `TREASURE` |
| field | `ePhases` | `WHEEL` |
| field | `Int32` | `value__` |

This is a **dungeon-room phase** enum (one value, `COMBAT`, just means "we are in a combat room" —
it does not subdivide what is happening *within* that combat: initiative, player-turn, enemy-turn,
resolution, etc.). No such intra-combat phase enum exists anywhere in the assembly (see UNRESOLVED below).

### CharacterHelper (trimmed to the members relevant to hp/maxHp/alive/stats; full type has ~150+ methods,
mostly character-creation/UI helpers with no bearing on the snapshot)

| Kind | Type | Name |
|---|---|---|
| method | `Int32` | `GetHealth` |
| method | `Int32` | `GetHealthMissing` |
| method | `Int32` | `GetMaxHealth` |
| method | `Void` | `SetToMaxHealth` |
| method | `Boolean` | `IsDead` |
| method | `Boolean` | `IsDeadAdventurer` |
| method | `Boolean` | `IsFriendly` |
| method | `Boolean` | `IsOpponent` |
| method | `Int32` | `GetStat` (8 overloads) |
| method | `Int32` | `GetCombatStat` |
| method | `Int32` | `GetAttackStat` (3 overloads) |
| method | `Dictionary\`1` | `GetBaseStats` |

---

## Types NOT found (candidates that do not exist under those names)

| Candidate | Result |
|---|---|
| `StatusEffect` | NOT FOUND (the real per-entity carrier is `StatusEffectComponent` + `StatusEffectInfo`; `StatusEffectConfig` is the *definition*, found and listed above) |
| `eStats` | NOT FOUND — stats are string-keyed dictionaries (`CharacterConfig.Stats`, `CharacterComponent.OverrideStats`/`BaseStatModifiers`), not an enum |
| `StatModifier` | NOT FOUND |
| `StatusComponent` | NOT FOUND (real name: `StatusEffectComponent`) |
| `ActiveStatusEffect` | NOT FOUND (real name: `StatusEffectInfo`) |
| `eStatuses` | NOT FOUND |
| `ClassConfig` | NOT FOUND — no dedicated class-config type; the closest analog is `CharacterConfig` (found) plus `CharacterComponent.ConfigName` as the class/character key string |
| `eClasses` | NOT FOUND |
| `eVenuePhases` | NOT FOUND |
| `eCombatPhases` | NOT FOUND — no intra-combat phase enum exists in the assembly at all |
| `eGameStates` | NOT FOUND |

---

## Summary: required snapshot fields → resolved member

| Snapshot field | Resolution | Status |
|---|---|---|
| `combat.active` | `CombatPhase._activeCharacterEntity` (`prop \| Entity \| _activeCharacterEntity`) | RESOLVED |
| `combat.round` | `CombatState.TotalRounds` (`field \| Int32 \| TotalRounds`) — **caveat**: not monotonic, resets to `-1` per wave (see `docs/research/eor-rehost-mp-review.md` N2) | RESOLVED (with caveat) |
| `combat.turn` | No global per-turn counter exists anywhere in the probed types. `PlayerComponent.TurnsPlayed` is per-combatant, not global; `CombatState` has no `Turn` field. | UNRESOLVED |
| `combat.phase` | No intra-combat phase enum/field exists. `ePhases`/`VenueState.PhaseIndex` are dungeon-room-level (COMBAT vs REST vs TREASURE), not turn-phase-within-combat. Nearest available proxy is deriving player-turn/enemy-turn from whether `CombatPhase._activeCharacterEntity` has a `PlayerComponent`, but that's a derived value, not a direct field. | UNRESOLVED |
| `combat.combatants[]` | `CombatState.Entities` (`field \| List\`1 \| Entities`) — list of `Entity` in the fight | RESOLVED |
| `combatant.id` | `Entity.Guid` (`prop \| String \| Guid`) | RESOLVED |
| `combatant.name` | `CharacterComponent.DisplayName` (`field \| String \| DisplayName`) | RESOLVED |
| `combatant.classId` | `CharacterComponent.ConfigName` (`field \| String \| ConfigName`) — the character/class config key into `Configs.Characters`; no dedicated `ClassConfig`/`eClasses` type exists | RESOLVED |
| `combatant.isPlayer` | Presence of `PlayerComponent` on the `Entity` (component-presence check via `Entity.Components`/`TryGet<PlayerComponent>`) — no boolean field exists on `CharacterComponent` itself; `eCharacterTypes` (found) has no `PLAYER` value | RESOLVED (derived, not a direct field) |
| `combatant.hp` | `CharacterComponent.CurrentHealth` (`field \| Int32 \| CurrentHealth`) | RESOLVED |
| `combatant.maxHp` | `CharacterHelper.GetMaxHealth` (`method \| Int32 \| GetMaxHealth`) — computed, not a stored field | RESOLVED |
| `combatant.alive` | `CharacterHelper.IsDead` (`method \| Boolean \| IsDead`), negated | RESOLVED |
| `combatant.statuses[].id` | `StatusEffectComponent.Statuses` dictionary key (string) — `field \| Dictionary\`2 \| Statuses` on `StatusEffectComponent`, `Dictionary<string, StatusEffectInfo>` | RESOLVED |
| `combatant.statuses[].stacks` | No `Stacks`/count field on `StatusEffectInfo` (`Duration`, `InitialDuration`, `OriginEntityId`, `TickDuration` only). Stacking appears to be represented via distinct suffixed status ids as separate dict keys, not a numeric count. | UNRESOLVED |
| `combatant.statuses[].duration` | `StatusEffectInfo.Duration` (`field \| Int32 \| Duration`) | RESOLVED |
| `combatant.stats{}` | `CharacterHelper.GetStat` (`method \| Int32 \| GetStat`, 8 overloads) reading string-keyed dictionaries (`CharacterComponent.OverrideStats`/`BaseStatModifiers`, `CharacterConfig.Stats`) — no `eStats` enum exists, stat keys are plain strings | RESOLVED |

---

## Notes / surprises

- **`eStats` does not exist.** Every stats dictionary in the assembly (`CharacterConfig.Stats`,
  `CharacterComponent.OverrideStats`/`BaseStatModifiers`, `StatusEffectConfig.Stats`/`CustomStats`) is
  string-keyed, not enum-keyed. S1's `stats{}` should be written as a string-keyed map, not an enum-keyed
  one.
- **`isPlayer` has no boolean field anywhere.** It must be derived from component presence
  (`PlayerComponent`) or from `CharacterHelper.IsFriendly`/`IsOpponent`, both of which key off
  `CharacterComponent.GroupIndex` (per `docs/research/eor-rehost-mp-review.md`, `GroupIndex == 0` is the
  verified friendly check, `CharacterHelper.cs:1483`).
- **`round` is real but not monotonic** — a multi-wave fight rewinds `TotalRounds` back to `-1` between
  waves, so any downstream S1 consumer treating `round` as strictly increasing will be wrong on multi-wave
  encounters.
- **`turn` and intra-combat `phase` are genuinely absent** from the assembly, not just misnamed. This
  engine does not appear to track a discrete "whose turn / what phase" state as data — it's driven
  procedurally through `CombatPhase._engageActiveEntity` / `_nextTurn` async control flow instead. If S1
  needs these fields, they'll have to be synthesized at hook time (e.g. incrementing a counter on every
  `CombatHelper.NextTurn` call, and tagging phase from whether the active entity has `PlayerComponent`)
  rather than read off game state.
- **Stacks are likely encoded as separate status ids**, not a count field — e.g. the codebase elsewhere
  references tiered ids like `STATUS_ATTACKUP_00`. Confirming this needs looking at `Configs.StatusEffects`
  JSON data for a stacking status, which is out of scope for this type-level probe.
