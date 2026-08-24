# Combat subsystem coverage map

Produced by decompiling `CombatPhase`, `CombatHelper`, `VenueHelper`, `InteractableHelper`, `CoreHelper`,
`CombatState`, `StatusEffectComponent`, `StatusEffectInfo` out of `FTK2.dll` with:

```
DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/tools/ilspycmd.exe --disable-updatecheck -t <Type> \
  -r "C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed" \
  "C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed\FTK2.dll"
```

Every code block below is verbatim from that decompile, cited `<Type>.cs:<line>` against the
per-type file the command above produces (line numbers are relative to that single-type dump, not
the original source tree — ilspycmd's `-t` mode emits one file per type starting at line 1). Facts
given as pre-verified in the task prompt are re-cited here, not re-derived, per instructions.
Background docs consulted (not re-quoted where this file supersedes them):
`docs/research/crucible-combat-field-map.md`, `docs/research/battle-ai-deep-dive.md`.

---

## TURN_FLOW

Ordered call sequence, from combat start to combat end:

1. **`CombatPhase.Initialize(...)`** — combat-start entry point (`Task`, awaited by its caller;
   signature at `CombatPhase.cs:177`):
   ```csharp
   public async Task Initialize(object pEnemyEntityNames, int pRandomSeed, VenueState pVenueState,
       VisualElement pCanvas2D, GameObject pCanvas3D, VenueCameraController pCamera, Diorama pDio,
       Scene pScene, InputController pInputControls, int pWaveAmount = 0)
   ```
   Near the end of `Initialize` (`CombatPhase.cs:574-575`):
   ```csharp
   await _initializeNextWave(pIsEndTurn: false);
   await _nextTurn(pIsFirstTurn: true);
   ```
   `CombatState.Create()` (`CombatState.cs:60-78`) is the state factory that backs this — it sets
   `TotalRounds = -1` (`CombatState.cs:65`) among other fresh-fight defaults.

2. **`CombatPhase._nextTurn(bool pIsFirstTurn)`** (`Task`, `CombatPhase.cs:1957`) — the per-turn
   pump. Order of operations inside it:
   ```csharp
   private async Task _nextTurn(bool pIsFirstTurn)
   {
       bool isNextRound = false;
       _resetHoverTile();
       _resetOffTurnHoverTile();
       if (_bossFightState != null && _combatState.Entities.Contains(_combatState.BossFightState.BossEntity) && _combatState.Entities.Count > 0)
       {
           await _tryStartBossPhase();
       }
       if (CombatHelper.TryEndGame(_combatState))
       {
           GlobalHeaderViewHelper.Hide(null);
           await _endCombatAsync();
           return;
       }
       List<(eAbilityResults result, object args)> nextTurnResults = (List<(eAbilityResults result, object args)>)(pIsFirstTurn ? ((IList)new List<(eAbilityResults, object)>()) : ((IList)CombatHelper.NextTurn(_env, _gameRandom, out isNextRound)));
       ...
   ```
   (`CombatPhase.cs:1957-1972`) — i.e. every non-first turn calls `CombatHelper.NextTurn` right
   here to advance the round/turn state, before doing any visual/UI work.

3. **`CombatHelper.NextTurn(Env, GameRandom, out bool pIsNewRound)`** (`CombatHelper.cs:795-840`) —
   the actual round/turn advance:
   ```csharp
   public static List<(eAbilityResults result, object args)> NextTurn(Env pEnv, GameRandom pGameRandom, out bool pIsNewRound)
   {
       CombatState combatState = pEnv.GameRun.CombatState;
       combatState.SkillProcsTurn.Clear();
       if (combatState.RoundEntities != null && combatState.RoundEntities.Count > 0 && combatState.RoundEntities[0] != null)
       {
           ResetCharacterActions(combatState.RoundEntities[0]);
       }
       pIsNewRound = false;
       List<(eAbilityResults, object)> list = new List<(eAbilityResults, object)>();
       if (combatState.RoundEntities == null || combatState.RoundEntities.Count <= 1)
       {
           __log("## NEW ROUND: " + combatState.TotalRounds + " ##");
           pIsNewRound = true;
           combatState.RoundEntities = GetCombatOrder(combatState, pIsMidcombat: false);
           combatState.InterruptedEntityIDs.Clear();
           combatState.TotalRounds++;
       }
       else
       {
           combatState.RoundEntities.RemoveAt(0);
       }
       ...
       list.AddRange(TickActiveEntityCharacterStatus(pEnv, pGameRandom, pIsStartTurn: true));
       ...
   }
   ```
   (`CombatHelper.cs:795-826`). **`TotalRounds` only increments when `RoundEntities` has drained to
   ≤1** (confirms the pre-verified fact) — this is a per-*round* counter (increments once per full
   pass through initiative order), not a per-turn counter. `TickActiveEntityCharacterStatus(...,
   pIsStartTurn: true)` is called from inside `NextTurn` itself, i.e. status ticking is folded into
   the turn-advance step, not a separate call site.

4. **`CombatPhase._tryProceedAsync(...)`** (`Task`, `CombatPhase.cs:4770`) — the player-input-driven
   dispatcher; called after every action a character takes. Its tail decides one of three things:
   end the fight, end the round/turn (call `_nextTurn`), or re-engage the same active entity
   (`CombatPhase.cs:4818-4912`):
   ```csharp
   await _tryStartBossPhase();
   await _tryCompleteQuests();
   _clearTileRenderState();
   if (CombatHelper.TryEndGame(_combatState))
   {
       ...
       if (!flag || _combatState.EnemiesPerWave * _combatState.WaveIndex > _enemyEntities.Count)
       {
           if (_bossFightState != null && _combatState.Entities.Count > 0)
           {
               await _tryStartBossPhase();
           }
           await _endCombatAsync();
           return;
       }
       ...
       if (_combatState.WaveIndex < _enemyWaves.Count)
       {
           await _initializeNextWave(pIsEndTurn: true);
           CombatHelper.NextTurn(_env, _gameRandom, out var _);
           await _nextTurn(pIsFirstTurn: true);
           ...
       }
       else
       {
           await _endCombatAsync();
       }
   }
   else if (_combatState.RoundEntities.Count < 1 || CombatHelper.IsTurnOver(_activeCharacterEntity))
   {
       if (_combatState.RoundEntities.Count > 0)
       {
           ...
           List<(eAbilityResults result, object args)> endTurnResults = CombatHelper.EndTurn(_combatState.RoundEntities.Count == 1, _env, _gameRandom);
           ...
       }
       _resetGlobalLookAt(_activeCharacterEntity);
       await _nextTurn(pIsFirstTurn: false);
   }
   else
   {
       await Task.Delay(300);
       await _setCameraAsExpected(eVenueEvent.TURN_START, ...);
       _engageActiveEntity();
   }
   ```
   So: **"is the fight over" is checked first** (`CombatHelper.TryEndGame`), **then "is the current
   character's turn over"** (`CombatHelper.IsTurnOver`, which drives `CombatHelper.EndTurn` before
   `_nextTurn` fires), **and only if neither**, the same active entity is re-engaged via
   `_engageActiveEntity()` (fired-and-forgotten, see below) for its next action within the same
   turn.

5. **`CombatHelper.EndTurn(bool pIsEndRound, Env, GameRandom)`** (`CombatHelper.cs:865-893`) — runs
   tile-status decay (round-end only) then character-status decay for the round's current active
   entity, always:
   ```csharp
   public static List<(eAbilityResults result, object args)> EndTurn(bool pIsEndRound, Env pEnv, GameRandom pGameRandom)
   {
       List<(eAbilityResults, object)> list = new List<(eAbilityResults, object)>();
       if (pIsEndRound)
       {
           foreach (Entity statusTileEntity in pEnv.GameRun.CombatState.Entities.FindAll((Entity e) => e.Has<StatusEffectComponent>() && e.Has<VenueTileComponent>()))
           {
               ...
               CoreHelper.DecrementStatus(statusTileEntity, item, pEnv, pGameRandom, list);
               ...
           }
       }
       list.AddRange(TickActiveEntityTileStatus(pEnv, pGameRandom));
       list.AddRange(TickActiveEntityCharacterStatus(pEnv, pGameRandom, pIsStartTurn: false));
       ...
       return list;
   }
   ```
   (`CombatHelper.cs:865-888`).

6. **`CombatPhase._engageActiveEntity()`** (`async void`, `CombatPhase.cs:1619`) — engages the newly
   active entity: resolves status gates (STUN/ENTANGLE/SCARE/CONFUSE/GRAB/thief/charge — matches the
   pre-verified `battle-ai-deep-dive.md` §1.1 summary) and, for AI entities, dispatches
   `AIHelper.BehaviourAiDecision`. **This method returns `void`, not `Task`** — it cannot be awaited
   by any caller, including `_tryProceedAsync`, which just calls `_engageActiveEntity();` and moves
   on (`CombatPhase.cs:4911`). A Harmony patch on it fires once per engagement but a postfix cannot
   observe when the async body actually finishes.

7. **`CombatPhase._endCombatAsync(bool pIsImmediate = false)`** (`Task`, `CombatPhase.cs:2352`) —
   fight teardown. `pIsImmediate` skips the summon-cleanup animation delay:
   ```csharp
   private async Task _endCombatAsync(bool pIsImmediate = false)
   {
       ...
       List<Entity> list = _combatState.Entities.FindAll((Entity e) => e.Has<CharacterComponent>() && CharacterHelper.ActorHasProperty(eActorProperties.SUMMON, e) && !CharacterHelper.IsDead(e));
       foreach (Entity item2 in _combatState.Entities.FindAll((Entity e) => e.Has<VenueTileComponent>()))
       {
           VenueViewHelper.RenderVenueTile(base._gameObjectMaps.FromTile[item2], null, TileRender.Hidden);
       }
       if (!pIsImmediate)
       {
           list.ForEach(delegate(Entity e)
           {
               cleanUpSummon(e);
           });
           if (list.Count > 0)
           {
               await Task.Delay(1500);
           }
       }
       ...
   ```
   (`CombatPhase.cs:2352-2399`). **`pIsImmediate: true` skips `cleanUpSummon(e)` for every
   still-alive summon and the following `await Task.Delay(1500)`** — everything after that block
   (win/lose determination, loot, follower fate, status clearing via `CoreHelper.ClearEntityStatuses`)
   still runs identically regardless of `pIsImmediate`. It is called with `pIsImmediate: true` at
   `CombatPhase.cs:7094` (a scripted/forced end path) and with the default `false` everywhere else
   (e.g. `CombatPhase.cs:1969`, `4837`, `4860`).

8. **`CombatHelper.TryEndGame(CombatState)`** (`bool`, `CombatHelper.cs:679-`) — the single win/loss
   check every step above consults:
   ```csharp
   public static bool TryEndGame(CombatState pCombatState)
   {
       if (pCombatState.EndCombatEarly || (pCombatState.VehicleEntity != null && pCombatState.VehicleEntity.Get<VehicleComponent>().CurrentHealth == 0))
       {
           return true;
       }
       ...
   ```
   (`CombatHelper.cs:679-685`, truncated — remainder counts alive entities per side/group).

**Ordering summary** (method names only):

```
CombatPhase.Initialize
  -> _initializeNextWave(pIsEndTurn:false)
  -> _nextTurn(pIsFirstTurn:true)
       -> CombatHelper.TryEndGame            [early-out check]
       -> CombatHelper.NextTurn              [only if !pIsFirstTurn; increments TotalRounds when RoundEntities drains]
            -> TickActiveEntityCharacterStatus(pIsStartTurn:true)
       -> ... visuals/UI ...
-- per action a character takes --
CombatPhase._tryProceedAsync
  -> CombatHelper.TryEndGame                  [fight-over branch -> _endCombatAsync]
  -> CombatHelper.IsTurnOver(activeEntity)     [turn-over branch]
       -> CombatHelper.EndTurn(pIsEndRound)
            -> TickActiveEntityTileStatus (round-end only for tile statuses)
            -> TickActiveEntityCharacterStatus(pIsStartTurn:false)
       -> _nextTurn(pIsFirstTurn:false)        [-> CombatHelper.NextTurn as above]
  -> else: _engageActiveEntity()               [same entity, next action within its turn; async void]
-- fight ends --
CombatPhase._endCombatAsync(pIsImmediate)
```

**Async caveats worth flagging explicitly** (per task instructions — this has misled the project
before):
- `_nextTurn` and `_tryProceedAsync` both return `Task` and are always `await`-ed at their call
  sites shown above, so ordering between them is reliable.
- `_engageActiveEntity` returns `void`, not `Task` — it is invoked fire-and-forget
  (`CombatPhase.cs:4911`: `_engageActiveEntity();` with no `await`). A Harmony **prefix** on it is
  safe (fires synchronously before the method body runs its first `await`), but a **postfix**
  fires when the synchronous prologue returns control (typically at the method's first `await`
  point), not when the whole async body completes. Do not build sequencing logic that assumes a
  postfix here means "engagement finished."
- `CombatHelper.NextTurn`, `EndTurn`, `TickActiveEntityCharacterStatus`, `PerformAbility`,
  `ApplyAction`, `TryCreateSummon` are all synchronous (`List<...>` / `bool` / `void` returns, not
  `Task`) — safe to patch without any async caveat.

---

## TARGETING

### Enums (verbatim, `eTargets.cs`, `eTileOccupancies.cs`, `eTileRowPositions.cs`, `eTileTargetAreas.cs`)

```csharp
public enum eTargets
{
    ANY, SELF, SELF_PICK, ALLY, ALLY_ALL, ALLY_NEARBY, ALLY_NEARBY_ALL, ENEMY
}
public enum eTileOccupancies
{
    ANY, FULL, EMPTY
}
public enum eTileRowPositions
{
    NONE, ANY, FRONT, BACK
}
public enum eTileTargetAreas
{
    NONE, SELF, SINGLE, AOE, SPLASH, ROW, COLUMN, SWIPE, ALL_GROUP, ALL_TOTAL, SPLASH_WAVE,
    BEHIND, CHECKERED, DEFAULT, EXPAND
}
```

### `VenueHelper.GetTargetableTiles` — the filter

Two overloads; the ability-config-driven one just unpacks fields into the full signature
(`VenueHelper.cs:297-300`):
```csharp
public static List<Entity> GetTargetableTiles(Entity pActiveEntity, List<Entity> pAllEntities, CombatAbilityConfig pAbility, string pThingName = null)
{
    return GetTargetableTiles(pActiveEntity, pAllEntities, pAbility.TileOccupancy, pAbility.OriginRowPosition, pAbility.TargetRowPosition, pAbility.Target, pAbility.TargetArea, pAbility.Actions);
}
```

The full method (`VenueHelper.cs:302-459`), field-by-field:

1. **Group resolution.** `friendlyGroup = pActiveEntity.Get<CharacterComponent>().GroupIndex`;
   `targetGroup = CombatHelper.GetTargetGroup(friendlyGroup, pTarget)` — for `SELF`/`SELF_PICK`/
   `ALLY*` this returns `friendlyGroup` itself; for `ENEMY` it flips group 0↔1
   (`CombatHelper.cs:271-297`).

2. **Base candidate list** — every tile entity in `targetGroup` that passes
   `_filterSelectableTileCondition` (`VenueHelper.cs:327`):
   ```csharp
   List<Entity> list = pAllEntities.FindAll((Entity e) => e.Has<VenueTileComponent>() && e.Get<VenueTileComponent>().GroupIndex == targetGroup && _filterSelectableTileCondition(e, activeTileEntity, pAllEntities, pOriginRow, pTargetRow, pTileOccupancy, pTarget));
   ```
   `_filterSelectableTileCondition` (`VenueHelper.cs:462-494`) is the row/occupancy gate:
   ```csharp
   private static bool _filterSelectableTileCondition(Entity pFilterTile, Entity pActiveCharacterTile, List<Entity> pAllEntities, eTileRowPositions pOriginRowPosition, eTileRowPositions pTargetRowPosition, eTileOccupancies eTileOccupancy, eTargets pTargetType)
   {
       if (pTargetType == eTargets.ENEMY && CoreHelper.HasStatusType(pFilterTile, eStatusEffectTypes.GUARD))
       {
           return false;
       }
       bool result = true;
       if (pOriginRowPosition != eTileRowPositions.ANY && pOriginRowPosition != pActiveCharacterTile.Get<VenueTileComponent>().RowPositionsType)
       {
           result = false;
       }
       else if (pTargetRowPosition != eTileRowPositions.ANY && pTargetRowPosition != pFilterTile.Get<VenueTileComponent>().RowPositionsType)
       {
           result = false;
       }
       else
       {
           switch (eTileOccupancy)
           {
           case eTileOccupancies.FULL:
               result = tileOccupied();
               break;
           case eTileOccupancies.EMPTY:
               result = !tileOccupied();
               break;
           }
       }
       return result;
       bool tileOccupied()
       {
           return pAllEntities.Any((Entity e) => e.Has<CombatComponent>() && e.Has<CharacterComponent>() && !CharacterHelper.IsDead(e) && e.Has<VenueComponent>() && e.Get<VenueComponent>().OccupiedTiles.Contains(pFilterTile.Get<VenueComponent>().TilePosition));
       }
   }
   ```
   A tile whose target-group entity is GUARD-statused is unselectable outright when targeting
   `ENEMY`. `OriginRowPosition` gates on the **caster's own tile** row; `TargetRowPosition` gates
   on the **candidate tile's** row; `TileOccupancy` (`ANY`/`FULL`/`EMPTY`) is only checked when both
   row filters pass, and only matters for abilities that care whether a living combatant currently
   occupies that tile (e.g. `BASIC_MOVE` targets `EMPTY`, most attacks are `ANY`/implicitly
   occupancy-agnostic).

3. **`pTarget` (`eTargets`) shape narrowing** — a `switch` over the enum picks the shape of the
   final candidate set from the row/occupancy-filtered `list` (`VenueHelper.cs:328-419`):
   - `SELF` → `GetAreaTiles(list, activeTileEntity, eTileTargetAreas.SELF, targetGroup)`
   - `SELF_PICK` → `GetAreaTiles(list, activeTileEntity, pTargetArea, targetGroup)` (caster picks
     the origin tile of an area shaped by `pTargetArea`)
   - `ALLY` → same-group tiles excluding the caster's own tile
   - `ALLY_ALL` → all same-group tiles (including caster's own)
   - `ALLY_NEARBY` / `ALLY_NEARBY_ALL` → the 4 orthogonally-adjacent tiles to the caster (with/
     without also requiring same-group membership already baked into `list`)
   - `ENEMY` → all tiles in the opposing group
   - default (`ANY`, and unhandled fallthrough) → `list` unchanged
   - `TargetArea` (`eTileTargetAreas`) is consulted only for `SELF`/`SELF_PICK` here — it otherwise
     shapes the AoE *around* whatever single tile ends up chosen, not which tiles are independently
     selectable, via `GetAreaTiles`.

4. **Inanimate-target guard** (`VenueHelper.cs:420-443`) — for non-`ADD_STATUS`-only,
   `SINGLE`-area abilities cast by a non-inanimate caster, tiles occupied by `eCharacterTypes.
   INANIMATE` actors (other than the caster's own tile) are stripped from the candidate list.

5. **Taunt override** (`VenueHelper.cs:444-458`) — for `ENEMY`/`ANY` targeting, if any taunting
   character (`eStatusEffectTypes.TAUNT`) exists among all entities, the candidate set is narrowed
   to just the tile(s) occupied by taunters (with `GUARD`-statused taunters excluded); if that
   narrowed set is non-empty it is returned immediately, short-circuiting everything else.

---

## ABILITY_RESOLUTION

Path from a chosen ability to applied effects:

1. **`CombatHelper.IsUsableAbility(GameRunData, Entity, string pAbility, bool pConsiderRemainingActions = true)`**
   (`bool`, `CombatHelper.cs:515-644`) gates whether an ability can even be offered/attempted. Verbatim
   default-case tail (`CombatHelper.cs:633-634`):
   ```csharp
   CharacterHelper.IgnoreEquipmentStats(pCharacterEntity);
   bool flag = VenueHelper.GetTargetableTiles(pCharacterEntity, pGameRun.CombatState.Entities, abilityConfig).Count > 0 && CharacterHelper.GetFocus(pCharacterEntity) >= abilityConfig.RequiresFocus && (!pConsiderRemainingActions || (abilityConfig.IsMajorAction && pCharacterEntity.Get<CombatComponent>().PrimaryActions > 0) || (!abilityConfig.IsMajorAction && pCharacterEntity.Get<CombatComponent>().SecondaryActions > 0));
   ```
   — matches the pre-verified fact exactly (targetable-tiles count, focus check, action-points
   check). Special-cased abilities (`SKIP_TURN`, `FLEE`, `BASIC_MOVE`, `GRAB*`, summon actions, etc.)
   have their own early gates before falling through to this shared tail.

2. **`CombatHelper.PerformAbility(...)`** (`List<(eAbilityResults, object)>`, `CombatHelper.cs:1153`)
   — the resolution entry point once an ability + target + roll result are decided (called by both
   the player-input path and AI decisions, and by status "tick" abilities). Full signature:
   ```csharp
   public static List<(eAbilityResults result, object arg)> PerformAbility(Entity pOrigin, Entity pTarget,
       List<Entity> pParty, Thing pThing, CombatDecisionData pCombatDecision, RollResultData pRollData,
       Func<Entity, string, eGetStatEquippedFilters, int> pGetStat, Func<Entity, string, int> pGetTileStat,
       SkillContext pOriginSkillContext, bool pConsumeAction, Env pEnv, GameRandom pGameRandom)
   ```
   Inside: resolves the `CombatAbilityConfig` for `pCombatDecision.Ability.AbilityName`
   (`CombatHelper.cs:1173`), builds `actionsToApply` from `abilityConfig.Actions`
   (`CombatHelper.cs:1190`), computes the target tile/area set, then for each target entity calls
   the private `_applyActions(...)` helper (`CombatHelper.cs:1592-...`, called at
   `CombatHelper.cs:1389` inside `PerformAbility`), which in turn calls `ApplyAction` once per
   `(action, target)` pair.

3. **`CombatHelper.ApplyAction(...)`** (`void`, `CombatHelper.cs:1871`) — dispatches on
   `eCombatActions`:
   - `ADD_STATUS` (`CombatHelper.cs:1883-2009`): gated at the very top —
     ```csharp
     if (pRollData.Status != eRollStatus.PERFECT)
     {
         break;
     }
     ```
     (`CombatHelper.cs:1885-1888`) — confirms the pre-verified "PERFECT roll only" rule. Past the
     gate it resolves shield/immunity interactions, then for the ordinary case calls
     `InteractableHelper.ApplyStatus(...)` (`CombatHelper.cs:1993`) — **this is where a status
     actually lands on the target's `StatusEffectComponent`**.
   - `REMOVE_STATUS` (`CombatHelper.cs:2010-2029`) is **also** gated `pRollData.Status !=
     eRollStatus.PERFECT` (`CombatHelper.cs:2012-2015`) before calling
     `InteractableHelper.RemoveStatus(...)`.
   - `CHANGE_STAT` (`CombatHelper.cs:2030-2036`) deserializes a `ChangeStatAction` and delegates to
     `InteractableHelper.ApplyStatChange(...)` — **this is where damage/healing is computed**.

4. **`InteractableHelper.ApplyStatChange(...)`** (`void`, `InteractableHelper.cs:627`) — for
   `pStatAction.Stat == "HP"` with a positive (damage-signed) delta (`InteractableHelper.cs:686-694`):
   ```csharp
   case "HP":
   {
       if (statDeltaValue > 0)
       {
           if (pIsCrit)
           {
               statDeltaValue += (int)Math.Max(1m, Math.Round((decimal)statDeltaValue * pCritRatio));
           }
           statDeltaValue = CalculateFinalDamage(pTargetEntity, statDeltaValue, pAbilityPowerRatio, pStatAction.Type, flag2, flag);
       }
       ...
   ```
   `CalculateFinalDamage` (`InteractableHelper.cs:1711-1742`) is the DEF/RES-subtraction math:
   ```csharp
   public static int CalculateFinalDamage(Entity pCharacterEntity, int pDamage, decimal pPowerRatio, eDamageType pDamageType, bool pBlockable, bool pInanimateTarget)
   {
       if (pInanimateTarget) { ... return 1; /* or 0 */ }
       int num = (int)Math.Round((decimal)pDamage * pPowerRatio);
       switch (pDamageType)
       {
       case eDamageType.PHYSICAL: num -= _getDamageReduction(eCharacterStats.DEF); break;
       case eDamageType.MAGICAL: num -= _getDamageReduction(eCharacterStats.RES); break;
       case eDamageType.NONE: throw new Exception($"{pDamageType} Damage type not supported");
       }
       return Math.Max(0, num);
   }
   ```
   Then the **HP-sign inversion** (`InteractableHelper.cs:722-727`) — this is the concrete site
   backing the pre-verified "HP sign is inverted" fact:
   ```csharp
   if (flag3)   // target == origin, self-target path
   {
       int pValue2 = -statDeltaValue;
       CharacterHelper.AddHealth(pTargetEntity, ref pValue2, pNonLethal: false, pResults, statChangedResultsData, pAddLinkAbilityResult: true);
       statDeltaValue = -pValue2;
       CharacterHelper.TryKillCharacter(pTargetEntity, statDeltaValue, statChangedResultsData, pEnv, pGameRandom, pResults);
       break;
   }
   ```
   (the non-self-target damage path at `InteractableHelper.cs:701-719` does the same
   `pValue = -statDeltaValue` negation before calling `CharacterHelper.AddHealth`).
   `CharacterHelper.AddHealth` takes a positive-means-heal value; a positive `ChangeStatAction`
   delta on `"HP"` (i.e. damage, per `eDamageType`-classified damage abilities) is negated to a
   negative heal amount before being applied — so **positive `statDeltaValue` = damage**, exactly
   as pre-verified.

**Summary of where each piece happens:**
- Ability legality → `CombatHelper.IsUsableAbility`
- Ability → action list + per-target dispatch → `CombatHelper.PerformAbility` → `_applyActions`
  (private) → `CombatHelper.ApplyAction`
- Damage math (DEF/RES subtraction) → `InteractableHelper.CalculateFinalDamage`, called from
  `InteractableHelper.ApplyStatChange`
- Status application (writes `StatusEffectComponent.Statuses`) → `InteractableHelper.ApplyStatus`,
  called from `CombatHelper.ApplyAction`'s `ADD_STATUS` case, gated on a PERFECT roll

---

## STATUSES

### Storage

```csharp
public class StatusEffectComponent
{
    public Dictionary<string, StatusEffectInfo> Statuses;
}
```
(`StatusEffectComponent.cs:3-6`, entire type — a status carrier is nothing but this one dictionary).

```csharp
public struct StatusEffectInfo
{
    public int Duration;
    public int InitialDuration;
    public int TickDuration;
    public string OriginEntityId;
    ...
}
```
(`StatusEffectInfo.cs:4-12`). The actual write into the dictionary happens in
`InteractableHelper.ApplyStatus` (`InteractableHelper.cs:1424`):
```csharp
statusEffectComponent.Statuses[pStatusConfigName] = StatusEffectInfo.Create(num3, pTickDuration, (pOriginEntity == null) ? string.Empty : pOriginEntity.Guid);
```
No stack-count field exists anywhere on `StatusEffectInfo` — matches
`crucible-combat-field-map.md`'s prior finding that stacking (where it exists) is represented as
distinct dictionary keys (e.g. tiered `STATUS_ATTACKUP_00`-style ids), not a numeric count on one
entry.

### Decay

`CoreHelper.DecrementStatus(Entity, string pStatusType, Env, GameRandom, List<...> pResults, bool pDecrementTick = false)`
(`CoreHelper.cs:757-808`):
```csharp
public static int DecrementStatus(Entity pEntity, string pStatusType, Env pEnv, GameRandom pGameRandom, List<(eAbilityResults, object)> pResults, bool pDecrementTick = false)
{
    if (pEntity.TryGet<StatusEffectComponent>(out var pComponent))
    {
        if (pComponent.Statuses.TryGetValue(pStatusType, out var status))
        {
            int duration = status.Duration;
            duration--;
            int num = ((status.TickDuration <= 0) ? Env.Configs.StatusEffects[pStatusType].TickFrequency : status.TickDuration);
            num--;
            _decrement(pEntity, pComponent, duration, num);
            ...
            return duration;
        }
    }
    return 0;
    void _decrement(Entity pCharacter, StatusEffectComponent pStatusEffectComponent, int pDuration, int pTickDuration)
    {
        if (pDuration == 0 && !pDecrementTick)
        {
            SkillContext pOriginSkillContext = new SkillContext(eSkills.NONE, -1);
            CombatHelper.ApplyAction(pCharacter, pCharacter, null, null, null, string.Empty, eCombatActions.REMOVE_STATUS, ref pOriginSkillContext, ref pOriginSkillContext, pStatusType, pResults, 1m, CombatHelper.SLOT_ROLL_DEFAULT, null, null, pIsCrit: false, 0m, pIsCenterTarget: true, pEnv, pGameRandom);
        }
        else
        {
            StatusEffectInfo value = pStatusEffectComponent.Statuses[pStatusType];
            if (!pDecrementTick) { value.Duration = pDuration; } else { value.TickDuration = pTickDuration; }
            pStatusEffectComponent.Statuses[pStatusType] = value;
        }
    }
}
```
`CoreHelper.DecrementStatus` is called from `CombatHelper.TickActiveEntityCharacterStatus(Env,
GameRandom, bool pIsStartTurn)` (`CombatHelper.cs:954`), which is itself invoked from
`CombatHelper.NextTurn` at start-of-turn (`pIsStartTurn: true`, `CombatHelper.cs:826`) and from
`CombatHelper.EndTurn` at end-of-turn (`pIsStartTurn: false`, `CombatHelper.cs:888`). The gate on
the config's `TickCombat` flag happens in `TickActiveEntityCharacterStatus` before any decrement is
attempted (`CombatHelper.cs:1017-1019`):
```csharp
foreach (string item in from s in CoreHelper.GetAllEntityStatuses(activeEntity)
    where Env.Configs.StatusEffects[s].TickCombat
    select s)
```
— only statuses whose `StatusEffectConfig.TickCombat` is true are iterated here at all; the loop
target is always `pEnv.GameRun.CombatState.RoundEntities[0]` (`CombatHelper.cs:957`) — i.e. this
runs for **the currently-active entity only**, confirming "at the owner's turn start" (and, via
`EndTurn`, also at that same entity's turn end).

### Removal at zero

`_decrement`'s `pDuration == 0 && !pDecrementTick` branch (`CoreHelper.cs:789-793`, quoted above)
calls `CombatHelper.ApplyAction(..., eCombatActions.REMOVE_STATUS, ...)` on the entity itself —
i.e. removal at zero duration is not a direct dictionary delete, it re-enters the same action
pipeline as an explicit "remove status" ability effect. That in turn calls
`InteractableHelper.RemoveStatus` (`ApplyAction`'s `REMOVE_STATUS` case, `CombatHelper.cs:2010-2029`,
quoted in ABILITY_RESOLUTION above) — **also gated on `pRollData.Status == eRollStatus.PERFECT`**,
but the call here passes `CombatHelper.SLOT_ROLL_DEFAULT` as `pRollData`, which is presumably a
constant PERFECT roll (not independently verified in this pass — see UNVERIFIED).

---

## SUMMONING

### `CombatHelper.TryCreateSummon(...)` — full contract

Signature (`CombatHelper.cs:112`):
```csharp
public static bool TryCreateSummon((int x, int y) pPos, int pGroupIndex, eSummonTypes pSummonType,
    string pSummonTarget, int pLevel, Env pEnv, GameRandom pRandom, out Entity pSummonEntity,
    string pGuid = null, Entity pOriginEntity = null, eTileRowPositions pTileRowPosition = eTileRowPositions.ANY)
```

**What it sets** (`CombatHelper.cs:225-250`):
```csharp
CharacterComponent characterComponent = pSummonEntity.Get<CharacterComponent>();
pSummonEntity.Add<VenueComponent>(new VenueComponent
{
    TilePosition = pPos,
    TileSize = (x: 1, y: 1),
    OccupiedTiles = new List<(int, int)>()
}).Add<CombatComponent>(new CombatComponent
{
    CanSummon = false
});
if (CharacterHelper.ActorHasTag(pSummonEntity, eConfigTags.TWO_BY_TWO)) { pSummonEntity.Get<VenueComponent>().TileSize = (x: 2, y: 2); }
else if (CharacterHelper.ActorHasTag(pSummonEntity, eConfigTags.ONE_BY_TWO)) { pSummonEntity.Get<VenueComponent>().TileSize = (x: 1, y: 2); }
pSummonEntity.Get<VenueComponent>().OccupiedTiles.Add(pPos);
VenueHelper.SetTilePosition(pSummonEntity, pPos);
characterComponent.GroupIndex = pGroupIndex;
if (characterComponent.CharacterType != eCharacterTypes.COMPANION)
{
    CharacterHelper.ActorAddProperty(eActorProperties.SUMMON, pSummonEntity);
}
return true;
```
It creates the `Entity` (via `CharacterHelper.CreateCharacterEntity` / `CreateFollowerCharacter` /
`VenueViewHelper.CreateReflectionEntity` depending on `pSummonType`), gives it `VenueComponent`
(tile position + size + occupied tiles) and a bare `CombatComponent{ CanSummon = false }`, and sets
`characterComponent.GroupIndex = pGroupIndex` (the **parameter**, not read from any tile here).

**What it does NOT do:**
- **Does not add the entity to `CombatState.Entities` or `CombatState.RoundEntities`.** No such
  call appears anywhere in the method body.
- **Does not create initiative** — no call to `SetInitiative`.
- **Does not reset actions** — no call to `ResetCharacterActions`.
- **Does not create a visual actor** (no `_gameObjectMaps`/`GameObjectMaps` registration, no
  `AIComponent` add for enemy-side summons).
- **Does not place the summon into any UI/phase roster** (`_playerEntities`/`_enemyEntities` on
  `CombatPhase`, or any wave list).

**Where `pGroupIndex` (allegiance) comes from at the call sites** — the task's "allegiance comes
from the tile's `GroupIndex`" claim resolves to the caller passing the **target tile's**
`VenueTileComponent.GroupIndex` in, not the summon function reading it itself. The `ADD_CHARACTER`/
`ADD_CHARACTER_SMOKE`/`ADD_CHARACTER_INSTANT` handler in `CombatHelper.ApplyAction`'s `_applyActions`
path calls it as (`CombatHelper.cs:2233`):
```csharp
if (!TryCreateSummon(targetPosition, pTarget.Get<VenueTileComponent>().GroupIndex, eSummonTypes2, pSummonTarget, num3, pEnv, pEnv.GameRun.CombatState.Random, out var pSummonEntity, null, pOrigin, pTarget.Get<VenueTileComponent>().RowPositionsType))
```
— `pTarget` here is the target *tile entity*, so the summon inherits whichever side's tile it was
placed on. (Two other call sites, `CombatPhase.cs:7218` and `CombatPhase.cs:7314`/`7543`, are
scripted encounter-event summons that pass a literal `1` or `0` for `pGroupIndex` instead of reading
a tile — those are not the ability-driven summon path.)

**What the caller must additionally do** — shown by the code immediately surrounding the
`CombatHelper.cs:2233` call site (`CombatHelper.cs:2237-2312`), i.e. this is the full obligation
list a mod must replicate if it wants to spawn a functioning combatant outside this exact call path:
```csharp
CharacterComponent characterComponent2 = pSummonEntity.Get<CharacterComponent>();
if (characterComponent2.GroupIndex == 1 && !pSummonEntity.Has<AIComponent>())
{
    pSummonEntity.Add<AIComponent>(new AIComponent());
}
...
pEnv.GameRun.CombatState.RoundEntities.Add(pSummonEntity);   // (or .Insert(1, ...) for an ambush-revenge case)
pEnv.GameRun.CombatState.Entities.Add(pSummonEntity);
SetInitiative(pSummonEntity, pParty, pEnv.GameRun.CombatState, pEnv.GameRun, pResults);
ResetCharacterActions(pSummonEntity);
if (pOrigin.Has<PlayerComponent>())
{
    StatsHelper.CollectCombatSummonsStats(pOrigin.Guid, characterComponent2.ConfigName);
}
eCombatActions eCombatActions2 = pAction;
pResults.Insert(0, (eCombatActions2 switch
{
    eCombatActions.ADD_CHARACTER_SMOKE => eAbilityResults.CHARACTER_ADDED_SMOKE,
    eCombatActions.ADD_CHARACTER_INSTANT => eAbilityResults.CHARACTER_ADDED_INSTANT,
    _ => eAbilityResults.CHARACTER_ADDED,
}, pSummonEntity.Guid));
```
So, concretely, a caller must: (a) add an `AIComponent` if the summon lands on the enemy group
(`GroupIndex == 1`), (b) push it into `CombatState.RoundEntities` (this is what makes it get a turn
this round/future rounds — note it does **not** retroactively get inserted at a computed initiative
slot, it's appended or inserted at index 1 for a special revenge case), (c) push it into
`CombatState.Entities` (the master combat roster — everything from `GetTargetableTiles` to
`_endCombatAsync`'s alive/dead counting reads this list), (d) call `SetInitiative(...)` (populates
`CombatState.EntityInitiative`), (e) call `ResetCharacterActions(...)` (grants it `PrimaryActions`/
`SecondaryActions` for the current round), and (f) emit an `eAbilityResults.CHARACTER_ADDED*` result
so the visual layer (`CombatViewHelper`/`CharacterVisualHelper`, consumed back in `CombatPhase`) can
spawn the actual on-screen actor — `TryCreateSummon` itself creates zero visual state.

### `CombatHelper.TryCheckForSummonAvailability(CombatState)`

(`CombatHelper.cs:646-659`):
```csharp
public static bool TryCheckForSummonAvailability(CombatState pCombatState)
{
    List<Entity> allyTiles = pCombatState.Entities.FindAll((Entity e) => e.Has<VenueTileComponent>() && e.Get<VenueTileComponent>().GroupIndex == 0);
    if (pCombatState.Entities.FindAll((Entity e) => e.TryGet<VenueComponent>(out var entityVenueComponent) && e.Has<CharacterComponent>() && !CharacterHelper.IsDead(e) && allyTiles.Any((Entity e) => e.TryGet<VenueComponent>(out var venueComponent) && entityVenueComponent.OccupiedTiles.Any(...))).Count() + pCombatState.GrabbedEntities.Count() >= allyTiles.Count())
    {
        return false;
    }
    return true;
}
```
This is **hardcoded to group 0** ("ally" tiles, i.e. it only ever checks whether the *player* side
has an empty tile to summon a companion/reflection into) — living occupants of group-0 tiles plus
currently-grabbed entities are compared against the total group-0 tile count; if there's no free
slot, it returns `false`. It does not take a group parameter, so it cannot be reused as-is to check
enemy-side (`GroupIndex == 1`) summon room. `IsUsableAbility`'s default case calls this before
allowing an `ADD_CHARACTER*` ability to be considered usable (`CombatHelper.cs:606-609`, quoted in
the pre-verified facts and confirmed in ABILITY_RESOLUTION context above).

---

## MOD_SEAMS

| Method | Signature shape | Return / async note | Good for |
|---|---|---|---|
| `CombatPhase.Initialize` | `Task Initialize(object, int, VenueState, VisualElement, GameObject, VenueCameraController, Diorama, Scene, InputController, int)` — `CombatPhase.cs:177` | `Task`, awaited by caller | Combat-start hook (prefix: inspect/modify incoming wave/seed before setup; postfix: react after `_nextTurn(pIsFirstTurn:true)` has fully run since it's awaited inside) |
| `CombatHelper.NextTurn(Env, GameRandom, out bool)` | `List<(eAbilityResults,object)> NextTurn(Env, GameRandom, out bool)` — `CombatHelper.cs:795` | synchronous | Turn/round-advance hook; postfix sees the freshly-updated `RoundEntities[0]`/`TotalRounds`; reliable place to increment a custom turn counter (per the field-map's UNRESOLVED note that no such counter exists natively) |
| `CombatPhase._nextTurn` | `Task _nextTurn(bool pIsFirstTurn)` — `CombatPhase.cs:1957` | `Task`, always awaited at its 5 call sites | Coarser turn-boundary hook than `NextTurn`; also the single choke point that decides "end game vs. keep going" via the `TryEndGame` check at its top |
| `CombatPhase._tryProceedAsync` | `Task _tryProceedAsync(bool, bool, bool, bool, float)` — `CombatPhase.cs:4770` | `Task`, awaited at all its own call sites (it is itself called after every player action) | Best single seam for "an action just resolved, what happens next" — branches to end-game, end-turn, or same-entity-continues; a prefix can override the branch decision by mutating `_combatState` before the `TryEndGame`/`IsTurnOver` checks run |
| `CombatHelper.IsUsableAbility` | `bool IsUsableAbility(GameRunData, Entity, string, bool)` — `CombatHelper.cs:515` | synchronous | Ability-legality override — postfix can force an ability usable/unusable per mod rules (e.g. new custom abilities, disabling vanilla ones) without touching `VenueHelper.GetTargetableTiles` itself |
| `VenueHelper.GetTargetableTiles` (both overloads) | `List<Entity> GetTargetableTiles(...)` — `VenueHelper.cs:297` / `:302` | synchronous | Targeting-rule override — postfix can add/remove tiles from the returned list (e.g. custom `eTargets`/`eTileTargetAreas` combos, ignoring taunt/GUARD short-circuit) |
| `CombatHelper.PerformAbility` | `List<(eAbilityResults,object)> PerformAbility(Entity, Entity, List<Entity>, Thing, CombatDecisionData, RollResultData, Func<...>, Func<...>, SkillContext, bool, Env, GameRandom)` — `CombatHelper.cs:1153` | synchronous | Ability-effect interception — prefix can swap `pCombatDecision.Ability.AbilityName` (redirect to a different ability) or short-circuit entirely; postfix can inspect/append to the returned results list before visuals consume it |
| `CombatHelper.ApplyAction` | `void ApplyAction(Entity, Entity, Entity, List<Entity>, Thing, string, eCombatActions, ref SkillContext, ref SkillContext, object, List<(eAbilityResults,object)>, decimal, RollResultData, Func<...>, Func<...>, bool, decimal, bool, Env, GameRandom)` — `CombatHelper.cs:1871` | synchronous | Finest-grained per-action hook — patch on `pAction == eCombatActions.CHANGE_STAT` (damage/heal) or `ADD_STATUS`/`REMOVE_STATUS` individually via a prefix that inspects `pActionArgs`; note there is no `eCombatActions` value for despawn/remove-entity (per the pre-verified enum list) so a "kill/remove a combatant" mod feature cannot hook this switch — it needs `CharacterHelper.KillCharacter` or direct `CombatState.Entities`/`RoundEntities` list surgery instead |
| `InteractableHelper.ApplyStatChange` | `void ApplyStatChange(string, ChangeStatAction, Entity, Entity, List<Entity>, Thing, decimal, RollResultData, Func<Entity,string,int>, bool, decimal, bool, Env, GameRandom, ref SkillContext, ref SkillContext, List<(eAbilityResults,object)>)` — `InteractableHelper.cs:627` | synchronous | Damage/heal number tuning — prefix can rewrite `pStatAction.FlatValue`/`FlatPercent`/`Type` before the HP branch runs; postfix can read final `statDeltaValue` for logging/telemetry |
| `InteractableHelper.CalculateFinalDamage` | `int CalculateFinalDamage(Entity, int, decimal, eDamageType, bool, bool)` — `InteractableHelper.cs:1711` | synchronous | Mitigation-formula override (DEF/RES subtraction, pierce, inanimate 0/1 special-case) without touching crit or the HP-sign inversion around it |
| `InteractableHelper.ApplyStatus` | `void ApplyStatus(Entity, Entity, Thing, string, string, GameRandom, List<(eAbilityResults,object)>, bool, bool, int?)` — `InteractableHelper.cs:1222` (plus a `List<Entity>`-party overload at `:1130`) | synchronous | Status-grant hook — this is the actual write site for `StatusEffectComponent.Statuses[...]`; prefix can redirect/cancel a status grant (many vanilla `CURSE`/`CHAOS`/meta status names are already resolved recursively here, so patching deep enough matters) |
| `CoreHelper.DecrementStatus` | `int DecrementStatus(Entity, string, Env, GameRandom, List<(eAbilityResults,object)>, bool pDecrementTick = false)` — `CoreHelper.cs:757` | synchronous | Status-decay-rate hook — prefix can change how much duration/tick is subtracted per call, or force early removal by faking `pDuration == 0` |
| `CombatHelper.TickActiveEntityCharacterStatus` | `List<(eAbilityResults,object)> TickActiveEntityCharacterStatus(Env, GameRandom, bool pIsStartTurn)` — `CombatHelper.cs:954` | synchronous | Coarser status-tick hook — fires once per active-entity turn-start and turn-end; postfix can inject extra statuses/effects tied to whoever's turn it currently is |
| `CombatHelper.TryCreateSummon` | `bool TryCreateSummon((int,int), int, eSummonTypes, string, int, Env, GameRandom, out Entity, string, Entity, eTileRowPositions)` — `CombatHelper.cs:112` | synchronous, `out` param | Summon-species/placement override — prefix can force `pSummonTarget`/`pGroupIndex`/`pPos`; remember the caller-obligation list above (initiative/actions/roster/visuals) still has to run for the summon to actually function, so patch the surrounding `ApplyAction`/`_applyActions` call site (`CombatHelper.cs:2233` block) if the goal is "make summoning behave differently end-to-end" rather than just "change which creature spawns" |
| `CombatHelper.TryCheckForSummonAvailability` | `bool TryCheckForSummonAvailability(CombatState)` — `CombatHelper.cs:646` | synchronous | Summon-slot-availability override — postfix can force `true`/`false`; note it is hardcoded to group 0 (ally side) so it is only meaningful for player/companion summon gating, not enemy summon gating |
| `CombatPhase._engageActiveEntity` | `async void _engageActiveEntity()` — `CombatPhase.cs:1619` | **`async void`, not `Task`** — fire-and-forget at its call site (`CombatPhase.cs:4911`) | Turn-engagement hook (status-gate resolution, AI decision dispatch) — prefix-only is safe; a postfix cannot reliably signal "engagement is done," see TURN_FLOW async caveats |
| `CombatPhase._endCombatAsync` | `Task _endCombatAsync(bool pIsImmediate = false)` — `CombatPhase.cs:2352` | `Task`; awaited at most call sites, but fire-and-forget at `CombatPhase.cs:8294` (`_endCombatAsync();` with no `await`) | Combat-end hook — prefix can flip `pIsImmediate` or short-circuit; postfix ordering is unreliable specifically from the one un-awaited call site at `CombatPhase.cs:8294` |
| `CombatHelper.TryEndGame` | `bool TryEndGame(CombatState)` — `CombatHelper.cs:679` | synchronous | Win/loss-condition override — postfix can force early combat end or prevent it (checked repeatedly from both `_nextTurn` and `_tryProceedAsync`) |

---

## UNVERIFIED

- **`CombatHelper.IsTurnOver`** — referenced in `_tryProceedAsync` (`CombatPhase.cs:4863`) as the
  turn-boundary predicate, but its own body was not decompiled/read in this pass; only its call
  site and effect (gates whether `EndTurn`/`_nextTurn` fire) were confirmed.
  UNVERIFIED signature/body.
- **`CombatHelper.SLOT_ROLL_DEFAULT`** — used repeatedly as the `RollResultData` passed into
  `ApplyAction` for non-slot-rolled effects (status ticks, `DecrementStatus`'s `REMOVE_STATUS` call
  at `CoreHelper.cs:792`). Assumed to represent a constant PERFECT roll (which would explain why
  tick-driven `ADD_STATUS`/`REMOVE_STATUS` calls succeed despite the PERFECT-only gate), but its
  actual field values were **not** decompiled/read in this pass. UNVERIFIED.
- **`CombatHelper.SetInitiative`** and **`CombatHelper.ResetCharacterActions`** — named and called
  from the `TryCreateSummon` caller-obligations block and from `NextTurn`/`EndTurn`, but their own
  bodies were not opened in this pass; their existence and call sites are confirmed, their internal
  behavior (how initiative is actually computed, what "resetting actions" sets `PrimaryActions`/
  `SecondaryActions` to) is UNVERIFIED here.
- **`CombatHelper.GetCombatOrder`** — called from `NextTurn` to build `RoundEntities` for a new
  round (`CombatHelper.cs:809`); its ordering algorithm (how initiative rolls translate to turn
  order) was not opened in this pass. UNVERIFIED.
- **Exact stack trace of what happens inside `_engageActiveEntity`'s `await`s** — only the top of
  the method (status gates, AI dispatch entry) was read; the full method body (~300+ lines per the
  battle-ai-deep-dive doc's earlier pass) was not re-walked line-by-line here. The `async void`
  fact and its mod-seam implication are confirmed; deeper internal sequencing beyond what
  `battle-ai-deep-dive.md` already documents is not re-verified in this pass.
- **`CombatHelper.EndTurn`'s `pIsEndRound` parameter derivation** — `_tryProceedAsync` passes
  `_combatState.RoundEntities.Count == 1` as `pIsEndRound` (`CombatPhase.cs:4868`), i.e. "this is
  the last entity left in the round" — confirmed at the call site, but whether that's exactly
  equivalent to "round is ending" in every edge case (e.g. mid-round entity death shrinking
  `RoundEntities`) was not independently traced.

---

FILE_WRITTEN: C:\Users\ben\repos\ftk2-mods-crucible\docs\research\coverage\combat.md
