# Combat rendering layer coverage map

Decompiled with:
```
DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/tools/ilspycmd.exe --disable-updatecheck -t <Type> \
  -r "C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed" \
  "<ManagedDir>/FTK2.dll"
```
against `FTK2.dll` in the Managed dir above. Line numbers below (`CombatPhase.cs:NNN` etc.) are lines in
that per-type decompile output, same convention as `docs/research/crucible-combat-field-map.md`.

Goal: this is the map to read *before* touching tile drawing, actor creation, ability visuals, or the
camera rig, because this layer has a long track record of throwing on unchecked dictionary lookups the
moment state (tiles, actors) gets rebuilt or mutated out of the sequence the game itself uses.

Established facts this doc builds on (do not re-derive):
- `Env.VenueGameObjectMaps` is the single source of truth for `FromTile` (`Dictionary<Entity, GameObject>`)
  and `FromCharacter` (`Dictionary<Entity, ActorGameObjectBase>`). `CombatPhase` exposes it as the
  **property** `_gameObjectMaps` (`_gameObjectMaps => _env.VenueGameObjectMaps`) — reflective *field*
  lookups on `CombatPhase` will return null.
- `_canvas3D` is a `GameObject`, not a `Component`.
- `VenueViewHelper.CreateVenueTileGameObjects` lays tiles out procedurally at `1.8f * logical coord` under
  the diorama's `VenueGridRoot` anchor.

---

## 1. DRAW_PIPELINE — the ordered steps of `CombatPhase.Initialize`

`CombatPhase.Initialize(...)` (signature at `CombatPhase.cs:177`) does a *lot*, but the rendering-relevant
steps happen in this order:

1. **Tile entities already exist by the time `Initialize` runs.** `Initialize` never calls
   `VenueViewHelper.CreateVenueTileGameObjects` itself for the opening combat — `base._gameObjectMaps.FromTile`
   is already populated when `Initialize` starts, and line 314 just folds its keys into combat state:
   ```csharp
   _combatState.Entities.AddRange(base._gameObjectMaps.FromTile.Keys);
   ```
   (`CombatPhase.cs:314`). `CreateVenueTileGameObjects` itself is only called later at runtime, e.g. for the
   boss `CHANGE_VENUE_GRID` event (`CombatPhase.cs:7961`, see §6) — the diorama/venue setup that runs before
   `CombatPhase.Initialize` is responsible for the first call.

2. **Ally actors are preloaded and placed onto the grid.**
   ```csharp
   await MemoryManagementHelper.PreloadCharacterAssetsForCombat(pEntities, _env, base._gameObjectMaps, pIgnoreGameObjectMap: true);
   VenueViewHelper.LoadCharacterEntitiesToVenueGrid(_canvas3D.transform, _allyEntities, base._gameObjectMaps,
       allyTiles, pDio, _combatState.Random, eLoadVenueGridRules.PREFERRED, eAnimationTypes.TAUNT,
       eAnimationIdentities.INTRO, bossSchedule);
   ```
   (`CombatPhase.cs:369-370`). This is the call that actually instantiates `ActorGameObjectBase` instances
   and writes them into `FromCharacter` for the player party (internally it calls
   `CharacterVisualHelper.CreateActorGameObject`, see §3).

3. **Character animation assets are loaded as a collection** keyed off everything currently in
   `FromCharacter` (`CombatPhase.cs:373-376`), gated on `base._gameObjectMaps.FromCharacter.ContainsKey(e)` —
   i.e. only entities that already have an actor get their anims preloaded here.

4. **Every tile is force-set to `Hidden`** — the "hidden reveal" pass:
   ```csharp
   foreach (Entity item in _combatState.Entities.FindAll((Entity e) => e.Has<VenueTileComponent>()))
   {
       VenueViewHelper.RenderVenueTile(base._gameObjectMaps.FromTile[item], null, TileRender.Hidden);
   }
   ```
   (`CombatPhase.cs:377-380`). Note this also indexes `FromTile[item]` with **no membership check** — if
   `item` isn't in the dictionary this throws (see §4/§6 for why that matters after a tile rebuild).

5. **Dead allies are ragdolled, live allies get initiative rolled** (`CombatPhase.cs:383-414`) — this is
   character-state bookkeeping, not rendering, but it runs in the same pass and depends on step 2 having
   already populated `FromCharacter` (`base._gameObjectMaps.FromCharacter[allyEntity]` is indexed unchecked
   at `CombatPhase.cs:389` and `:400`).

6. **Enemy tiles/actors load the same way later in `Initialize`** (not shown above — the enemy-side mirror
   of step 1-2 happens further down using the same `RenderVenueTile(..., TileRender.Hidden)` +
   `LoadCharacterEntitiesToVenueGrid` pair at `CombatPhase.cs:690-692`).

7. **The camera rig is set up in parallel with tile/actor loading**, not after it:
   ```csharp
   _venueCamera.SetRigType(pVenueState.Venue.DioramaName);         // CombatPhase.cs:196, early in Initialize
   ...
   _venueCamera.SetRig(VenueViewHelper.FindCameraRig(pScene, pVenueState.Venue.DioramaName), _env); // CombatPhase.cs:356
   ```

8. **The actual reveal (Hidden → visible) is not part of `Initialize` itself** — `Initialize` leaves tiles in
   `TileRender.Hidden`. The transition to a real `TileRender` state (Default/Selectable/etc.) happens the
   first time `_clearTileRenderState()` runs (`CombatPhase.cs:4736`, called from many places, e.g.
   `CombatPhase.cs:4820`) which is driven by turn-engagement logic downstream of `Initialize`, not by
   `Initialize` directly. **Do not assume `Initialize` alone makes tiles visible** — a caller that only
   invokes `Initialize` and never reaches a `_clearTileRenderState()` call will see a Hidden grid.

Ordering summary: **tile entities (pre-existing) → tile GameObjects (pre-existing, or created via
`CreateVenueTileGameObjects` upstream) → ally actor creation/placement (`LoadCharacterEntitiesToVenueGrid`)
→ force-Hidden render pass over every `VenueTileComponent` entity → enemy actor creation/placement → first
`_clearTileRenderState()` call, which is what actually reveals the grid.**

---

## 2. TILE_RENDER_STATE — what controls tile visibility

### `VenueViewHelper.CreateVenueTileGameObjects` (`VenueViewHelper.cs:94-165`)

This is where a tile GameObject's **render layer** is decided, and it is the single most important line
for "why did my modded larger grid draw only faintly":

```csharp
GameUtil.SetLayerRecursive(gameObject2.transform,
    (pEntity.Get<VenueTileComponent>().GroupIndex > -1) ? "UI" : "DoNotRender");
```
(`VenueViewHelper.cs:104`)

Any tile Entity whose `VenueTileComponent.GroupIndex` is **not greater than -1** (i.e. `-1` or unset) gets
put on the `"DoNotRender"` layer — it is invisible outright, independent of its `TileRender` state. This
runs once at instantiation and is never revisited elsewhere for that GameObject.

Immediately after, every new tile is initialized to `Hidden`:
```csharp
Vector3 vector = new Vector3(1.8f * (float)venueComponent.TilePosition.x, 0f, 1.8f * (float)venueComponent.TilePosition.y);
gameObject2.transform.position = vector;
RenderVenueTile(gameObject2, null, TileRender.Hidden);
```
(`VenueViewHelper.cs:105-108`)

### `VenueViewHelper.SetVenueTileRenderState` (`VenueViewHelper.cs:836-867`)

This is the per-frame/per-selection decision function. Full body:

```csharp
public static void SetVenueTileRenderState(Entity pTileEntity, bool pFriendly, GameObject pTileGameObject,
    ActorGameObjectBase pActorGameObject, List<Entity> pSelectableTiles, Entity pActiveCharacterTile,
    Entity pSelectedTile, List<Entity> pHighlightedAreaOfEffect, bool isCharging, bool invertDefaultTiles = false)
{
    TileRender pTileRender = TileRender.Hidden;
    if (pTileEntity == pActiveCharacterTile)
    {
        pTileRender = ((pFriendly && pHighlightedAreaOfEffect != null && pHighlightedAreaOfEffect.Contains(pTileEntity))
            ? TileRender.ActiveCharacterSplash : TileRender.ActiveCharacter);
    }
    else if (pTileEntity == pSelectedTile)
    {
        pTileRender = (pFriendly ? TileRender.TargetFriendly : TileRender.TargetOpposing);
    }
    else if (pHighlightedAreaOfEffect.Contains(pTileEntity) && isCharging)
    {
        pTileRender = TileRender.ChargingSplash;
    }
    else if (pHighlightedAreaOfEffect.Contains(pTileEntity))
    {
        pTileRender = ((!pFriendly) ? TileRender.TargetOpposingSplash : TileRender.TargetFriendlySplash);
    }
    else if (isCharging)
    {
        pTileRender = TileRender.Charging;
    }
    else if (pSelectableTiles != null && pSelectableTiles.Contains(pTileEntity))
    {
        pTileRender = (invertDefaultTiles ? TileRender.SelectableInverted : TileRender.Selectable);
    }
    else if (pTileEntity.Get<VenueTileComponent>().GroupIndex >= 0)
    {
        pTileRender = (invertDefaultTiles ? TileRender.DefaultInverted : TileRender.Default);
    }
    RenderVenueTile(pTileGameObject, pActorGameObject, pTileRender);
}
```
(`VenueViewHelper.cs:836-867`)

Key facts:
- `pTileRender` **starts as `Hidden`** and only leaves `Hidden` if one of the `if/else if` branches matches.
- The last branch — the one that produces the "everything else" state — only fires if
  `GroupIndex >= 0`. **A tile whose `GroupIndex` is `-1` falls through every branch and stays `Hidden`,
  even outside of the active-character/selected/highlighted/charging/selectable special cases.** This is
  the second half of the "modded larger grid drew only faintly" bug: new grid tiles added by a mod that
  don't get a valid non-negative `GroupIndex` are both put on `DoNotRender` layer at creation (see above)
  *and* perpetually resolve to `Hidden` in state updates, so they never draw under any circumstance.
- Parameters and what they mean:
  - `pFriendly` — whether the tile belongs to the friendly (ally) group; only matters for choosing between
    `...Friendly` vs `...Opposing` variants of Target/Splash tiles.
  - `pTileGameObject` / `pActorGameObject` — the tile's GameObject and (if occupied) the occupant's actor,
    passed straight through to `RenderVenueTile`.
  - `pSelectableTiles` — tiles the player can currently click/target; drives `Selectable`/`SelectableInverted`.
  - `pActiveCharacterTile` — the tile under the currently-acting character; drives `ActiveCharacter*`.
  - `pSelectedTile` — the tile currently hovered/targeted; drives `TargetFriendly`/`TargetOpposing`.
  - `pHighlightedAreaOfEffect` — tiles inside an ability's AoE preview; drives the `*Splash`/`Charging*`
    variants. **Note this is dereferenced with `.Contains(...)` at lines 846 and 850 without a null check**
    even though the very next branch (line 842) explicitly null-guards it — if a caller ever passes
    `pHighlightedAreaOfEffect == null` this throws `NullReferenceException` unless the tile also happens to
    equal `pActiveCharacterTile`.
  - `invertDefaultTiles` — swaps `Default`/`Selectable` for their `Inverted` counterparts (diorama-driven,
    see `_diorama.InvertDefaultTiles` used by the caller `_clearTileRenderState`, `CombatPhase.cs:4741`).

### `VenueViewHelper.RenderVenueTile` (`VenueViewHelper.cs:1518-1543`)

This is what actually flips visuals: it sets the `VenueTileMono` state and toggles the occupant's outline.

```csharp
public static void RenderVenueTile(GameObject pTileGameObject, ActorGameObjectBase pActorGameObject, TileRender pTileRender)
{
    if (pTileGameObject == null)
    {
        return;
    }
    VenueTileMono component = pTileGameObject.transform.GetComponent<VenueTileMono>();
    if (pTileRender == TileRender.Hidden)
    {
        component.SetState(TileRender.Hidden);
        if (pActorGameObject != null)
        {
            pActorGameObject.SetOutlineDisabled();
        }
        return;
    }
    dBattleGridTile dBattleGridTile2 = dObjectHelper.Index.dBattleGridTile.GetAllRecords()
        .FirstOrDefault((dBattleGridTile slot) => slot.TileType == pTileRender);
    component.SetState(pTileRender);
    if (pActorGameObject != null)
    {
        if (pTileRender == TileRender.Default || pTileRender == TileRender.DefaultInverted ||
            pTileRender == TileRender.Selectable || pTileRender == TileRender.SelectableInverted ||
            pTileRender == TileRender.Hidden)
        {
            pActorGameObject.SetOutlineDisabled();
        }
        else
        {
            pActorGameObject.SetOutlineEnabled(1f, dBattleGridTile2.CharacterOutline);
        }
    }
}
```
(`VenueViewHelper.cs:1518-1543`)

What makes a tile **outlined/glowing**: any `TileRender` value that is *not* `Default`, `DefaultInverted`,
`Selectable`, `SelectableInverted`, or `Hidden` (i.e. `ActiveCharacter*`, `Target*`, `Charging*`) causes
`SetOutlineEnabled` on its occupant's actor, using `dBattleGridTile2.CharacterOutline` looked up from the
`dBattleGridTile` config table by matching `TileType == pTileRender`. **If a mod introduces a `TileRender`
value with no matching `dBattleGridTile` record, `dBattleGridTile2` is null and `.CharacterOutline` throws
`NullReferenceException`** — this is unchecked (no null guard before the property access at line 1541,
implied by the `FirstOrDefault` above it).

What makes a tile **invisible** — two independent mechanisms, both must be right for a tile to draw:
1. Its GameObject's render layer must not be `"DoNotRender"` — decided once, at `CreateVenueTileGameObjects`
   time, by `GroupIndex > -1`.
2. Its resolved `TileRender` must not be `Hidden` — decided per-update by `SetVenueTileRenderState`, which
   requires `GroupIndex >= 0` to reach any non-Hidden default state at all.

**Practical modder rule derived from this**: every venue tile entity's `VenueTileComponent.GroupIndex` must
be `>= 0` (ally group is typically `0`, enemy group `1`, per `CombatPhase.cs:362-363`) or it is guaranteed
invisible under both the layer check and the render-state fallthrough — this is very likely the exact cause
of the "modded larger grid drew only faintly" symptom if new tiles were spawned with a default/uninitialized
`GroupIndex`.

---

## 3. ACTOR_SEQUENCE — the correct sequence to add a visible combatant

The game's own boss-fight `SUMMON_ENTITY_ENEMIES` branch (`CombatPhase.cs:7177-7306`) is the canonical
reference for "add one new combatant, on the grid, fully rendered, mid-combat." The rendering-relevant
subset, in order:

```csharp
// 1. Resolve/create the summoned Entity (character or inanimate) via CombatHelper.TryCreateSummon
if (!CombatHelper.TryCreateSummon((x: 0, y: 0), 1, pSummonType, text, num2, _env, _combatState.Random,
        out var pSummonEntity2, pPhaseEvent.Guid))
{
    break;
}
...
// 2. Pick a vacant tile from the CURRENT FromTile map
Entity entity14 = VenueViewHelper.FindVacantVenueTile(pSummonEntity2, base._gameObjectMaps.FromTile.Keys.ToList(),
    _combatState.Entities.FindAll((Entity e) => e.Has<CombatComponent>()),
    _combatState.BossFightState.BossEntity.Get<CharacterComponent>().GroupIndex, _combatState.Random);
...
// 3. Set the entity's logical tile position and register it in combat state
VenueHelper.SetTilePosition(pSummonEntity2, entity14.Get<VenueComponent>().TilePosition);
_addEntityToCombat(pSummonEntity2, entity14.Get<VenueComponent>().TilePosition);
// 4. Create the actor GameObject and REGISTER it in FromCharacter — this is the only place an actor is created
base._gameObjectMaps.FromCharacter[pSummonEntity2] =
    CharacterVisualHelper.CreateActorGameObject(pSummonEntity2, _canvas3D.transform, new GameRandom(), pUseOverworldOverrides: false);
VenueHelper.TrySetStaticCombatPosition(pSummonEntity2, base._gameObjectMaps.FromTile.Keys.ToList(), _gameRandom);
// 5. Position the actor in world space from its occupied tiles
positionActorGameObject(pSummonEntity2, base._gameObjectMaps.FromCharacter[pSummonEntity2]);
// 6. Face it at its tile
VenueViewHelper.CharacterLookAtTarget(pSummonEntity2, entity14, pTryRotateForward: true, base._gameObjectMaps);
...
CombatHelper.SetInitiative(pSummonEntity2, _enemyEntities, _combatState, _env.GameRun, null);
...
_doRefreshUIAll();
// 7. Optional entrance FX, then the actual reveal animation
if (pPhaseEvent.EventType == eBossPhaseEventTypes.SUMMON_ENTITY_ENEMIES_SMOKE)
{
    AdventureViewHelper.PlayFxOnGameObject(base._gameObjectMaps.FromCharacter[pSummonEntity2].transform.position, "SPELL_FLEE_SMOKEBOMB_01");
}
base._gameObjectMaps.FromCharacter[pSummonEntity2].PlayAnimation(eAnimationTypes.TAUNT, eAnimationIdentities.SUMMON, eAnimationTypes.IDLE_COMBAT);
await Task.Delay(100);
```
(`CombatPhase.cs:7218-7304`)

Step 5's helper, `positionActorGameObject`, is a local function defined in a sibling method
(`CombatPhase.cs:8247-8250`):
```csharp
void positionActorGameObject(Entity pCharacterEntity, ActorGameObjectBase pActor)
{
    pActor.transform.position = CharacterVisualHelper.GetCharacterRootPosition(pCharacterEntity,
        VenueViewHelper.GetAveragePositionOfTiles(pCharacterEntity.Get<VenueComponent>().OccupiedTiles,
            base._gameObjectMaps.FromTile) + _diorama.PlayerOffset);
}
```
This itself indexes `FromTile` (inside `GetAveragePositionOfTiles`) keyed by the entity's `OccupiedTiles` —
another place a stale/mismatched tile position silently breaks positioning.

**Confirmed distinction (per the established context)**: `CHARACTER_ADDED_SMOKE` in
`CharacterVisualHelper.RenderAbilityResults` only *looks up* an actor, it never creates one:
```csharp
case eAbilityResults.CHARACTER_ADDED_SMOKE:
{
    Entity entity = pEntities.Find((Entity e) => e.Guid == (string)visualResult.Item2);
    ActorGameObjectBase originActor = pActorGameObjects[entity];
    ...
    originActor.gameObject.SetActive(value: true);
    originActor.PlayAnimation(eAnimationTypes.TAUNT, eAnimationIdentities.INTRO, eAnimationTypes.IDLE_COMBAT);
    continue;
}
```
(`CharacterVisualHelper.cs:2417-2433`, inside `RenderAbilityResults` at `CharacterVisualHelper.cs:2121`)
`pActorGameObjects[entity]` is an unchecked dictionary index — if `pEntities.Find` returns `null` (no
matching Guid), the subsequent `entity.TryGet<CharacterComponent>()` call at line 2422 throws
`NullReferenceException` on the null `entity`; if `entity` is found but was never registered via
`CreateActorGameObject`, `pActorGameObjects[entity]` throws `KeyNotFoundException`. **A mod that fires
`CHARACTER_ADDED_SMOKE` for an entity that hasn't already gone through the summon sequence above (steps
3-4) will crash here.**

**Reference sequence for modders**: create the Entity → set its `VenueComponent.TilePosition`/`OccupiedTiles`
→ register it with `_addEntityToCombat` (or equivalent combat-state registration) → call
`CharacterVisualHelper.CreateActorGameObject` and immediately assign the result into
`base._gameObjectMaps.FromCharacter[entity]` → position via `GetCharacterRootPosition`/`GetAveragePositionOfTiles`
→ face via `VenueViewHelper.CharacterLookAtTarget` → play an entrance animation. Skipping the `FromCharacter`
registration step, or reordering it after any code path that reads `FromCharacter[entity]`, is what produces
`KeyNotFoundException` crashes elsewhere in the visual pipeline.

---

## 4. VISUAL_SEQUENCE — `CombatViewHelper.CreateVisualSequence` and unchecked indexing crash sites

`CombatViewHelper.CreateVisualSequence` (two overloads, `CombatViewHelper.cs:119` and `:124`) walks a list
of `(eAbilityResults, object)` tuples produced by ability resolution and turns them into a `VisualSequence`
of `VisualNode`s (animations, FX, popcorn text, camera moves). It is fed by `CombatPhase._performAbility`
(`CombatPhase.cs:3582`) and its `_damageApplied*` family of private helpers further down in
`CombatViewHelper.cs` (`_damageApplied` at `:2749`, `_damageAppliedToCharacter` at `:2821`,
`_damageAppliedToInanimate` at `:2758`, `_damageAppliedByWeapon` at `:2708`).

### The confirmed live bug: `CombatViewHelper.JoinAbilityHitEffectNode`

```csharp
public static void JoinAbilityHitEffectNode(VisualNode pVisualNode, dAbility pAbility, Entity pTargetEntity,
    Entity pTargetTileEntity, VenueGameObjectMaps pGameObjectMaps, RollResultData pRollData, float pEngageTime)
{
    if (pAbility.HitEffect != null)
    {
        JoinFxNode(pVisualNode, pAbility.HitEffect, pTargetEntity, pTargetTileEntity, pGameObjectMaps, pEngageTime, pIsPersistant: false, pParent: false);
    }
    if (pRollData.Status == eRollStatus.PERFECT && pAbility.AddEffect != null)
    {
        JoinFxNode(pVisualNode, pAbility.AddEffect, pTargetEntity, pTargetTileEntity, pGameObjectMaps, pEngageTime, pIsPersistant: false, pParent: false);
    }
}
```
(`CombatViewHelper.cs:993-1003`)

It hands off to `JoinFxNode` → `GetSpawnPositionPoint`, whose `GROUND` case is where the actual dereference
lives:

```csharp
public static Transform GetSpawnPositionPoint(eSpawnPoints pSpawnPoint, Entity pTargetTileEntity, Entity pTargetEntity,
    Dictionary<Entity, GameObject> pFromTile, Dictionary<Entity, ActorGameObjectBase> pFromActor, out Quaternion pRotation)
{
    Transform pMatch2 = null;
    switch (pSpawnPoint)
    {
    case eSpawnPoints.GROUND:
        if (pTargetTileEntity == null)
        {
            pMatch2 = pFromActor[pTargetEntity].transform;
            pRotation = pMatch2.rotation;
        }
        else if (pFromActor.ContainsKey(pTargetTileEntity))
        {
            Entity tileEntityOfCharacter = VenueHelper.GetTileEntityOfCharacter(pTargetTileEntity, pFromTile.Keys.ToList());
            pMatch2 = pFromTile[tileEntityOfCharacter].transform;
            pRotation = Quaternion.Euler(pMatch2.transform.rotation.eulerAngles + VenueViewHelper.GetTileForward(tileEntityOfCharacter, pFromTile));
        }
        else
        {
            pMatch2 = pFromTile[pTargetTileEntity].transform;
            pRotation = Quaternion.Euler(pMatch2.transform.rotation.eulerAngles + VenueViewHelper.GetTileForward(pTargetTileEntity, pFromTile));
        }
        break;
    ...
```
(`CombatViewHelper.cs:1044-1066`)

This is the exact site behind the observed live crash (`JoinAbilityHitEffectNode` throwing via
`_damageAppliedToCharacter` → `CreateVisualSequence` → `_performAbility`), and the mechanism is now
traceable in decompiled code:

- `pFromActor[pTargetEntity]` (line 1052) and `pFromTile[pTargetTileEntity]` (line 1063) are both **unchecked
  dictionary indexers** — no `TryGetValue`/`ContainsKey` guard before them.
- `VenueHelper.GetTileEntityOfCharacter(pTargetTileEntity, pFromTile.Keys.ToList())` (`VenueHelper.cs:103-123`)
  matches purely by **logical `(x, y)` position**, not by Entity reference/Guid — it walks the *current*
  `pFromTile.Keys` looking for a positional match, and **returns `null` if none is found**:
  ```csharp
  public static Entity GetTileEntityOfCharacter(Entity pCharacterEntity, List<Entity> pEntities)
  {
      if (pCharacterEntity == null || !pCharacterEntity.Has<VenueComponent>())
      {
          return null;
      }
      ...
      return null; // no positional match found
  }
  ```
  (`VenueHelper.cs:103-123`)
- When tiles are destroyed and rebuilt (see §6, `CHANGE_VENUE_GRID`) *after* `CombatPhase.Initialize`, the
  new `FromTile` dictionary has entirely new `Entity` keys. Any code still holding a stale tile `Entity`
  reference from before the rebuild — or a character whose `VenueComponent.TilePosition` was not correctly
  re-derived after the grid shift — causes `GetTileEntityOfCharacter` to return `null`, and the very next
  line (`pFromTile[tileEntityOfCharacter]`, line 1058) indexes the dictionary **with a null key**, which
  throws. If instead the direct-index `else` branch is taken (line 1063) with a stale `pTargetTileEntity`
  that simply isn't a key in the rebuilt `FromTile` at all, that throws `KeyNotFoundException` for the same
  underlying reason — a mismatch between cached Entity references and the live `VenueGameObjectMaps` after a
  tile rebuild. **This is the load-bearing takeaway: anything that destroys/recreates `FromTile` (or
  `FromCharacter`) mid-combat invalidates every previously-cached `Entity` reference to a tile, and every one
  of the unchecked indexers below is a potential crash site the instant that happens.**

### Full inventory of unchecked `FromTile[...]` / `FromCharacter[...]` indexing (crash sites a mod must not create conditions for)

In `CombatViewHelper.cs` (`CreateVisualSequence` and its helpers, `pGameObjectMaps.FromCharacter[...]` /
`pGameObjectMaps.FromTile[...]`, all unchecked):
`:166`, `:283`, `:299`, `:318`, `:401`, `:483`, `:518`, `:559-565` (multiple, including a *write* that
creates the entry — see §3 pattern), `:584`, `:589`, `:594`, `:597`, `:612`, `:619`, `:647`, `:680`, `:682`,
`:730`, `:731`, `:776`, `:794`, `:797`, `:804`, `:838`, `:885`, `:975`, `:1052`, `:1055` (`ContainsKey` check
present, safe), `:1058`, `:1063`, `:1135`, `:1180`, `:1243`, `:1264`, `:1274`, `:1289`, `:1298`, `:1304`,
`:1317`, `:1324`, `:1339`, `:1372`, `:1374`, `:1391`, `:1414`, `:1416`, `:1440`, `:1457`, `:1464`, `:1467`,
`:1497`, `:2826` (`_damageAppliedToCharacter`), `:2848`, `:2853`, `:2863`, `:2889-2890`, `:2901`, `:2918`,
`:2922`, `:2927`, `:2930`, `:2944`.

In `CharacterVisualHelper.cs` (`RenderAbilityResults`, `pActorGameObjects[...]`, all unchecked):
`:2112`, `:2131`, `:2167`, `:2175`, `:2179`, `:2246`, `:2255`, `:2270`, `:2276`, `:2286`, `:2312`, `:2324`,
`:2342`, `:2346`, `:2368`, `:2373`, `:2376`, `:2379`, `:2383`, `:2387`, `:2392`, `:2402`, `:2409`, `:2415`,
`:2420` (the `CHARACTER_ADDED_SMOKE` case documented in §3), `:2464`, `:2473`, `:2483`, `:2489`, `:2495`,
`:2511`, `:2526`, `:2575`.

That is **~90 unguarded dictionary indexer call sites** across the two files that build ability-result
visuals. Every single one assumes both maps are fully populated and internally consistent (every `Entity`
referenced by an `eAbilityResults` payload has a live tile and/or actor in the *current* `VenueGameObjectMaps`)
for the entire duration between when an ability is decided and when its `VisualSequence` finishes playing.
The one checked exception found in this sweep is `CombatViewHelper.cs:1055`
(`pFromActor.ContainsKey(pTargetTileEntity)`), which is a branch condition, not a guard — the two branches it
selects between (`:1058` and `:1063`) are themselves unchecked.

`CombatPhase._clearTileRenderState` (`CombatPhase.cs:4736-4752`) has the same pattern already established:
```csharp
GameObject pTileGameObject = base._gameObjectMaps.FromTile[entity];
ActorGameObjectBase pActorGameObject = ((entity2 != null) ? base._gameObjectMaps.FromCharacter[entity2] : null);
```
(`CombatPhase.cs:4745-4746`) — `FromTile[entity]` unchecked; `FromCharacter[entity2]` unchecked whenever an
occupant is found. `entity2` is found by matching `VenueComponent.OccupiedTiles` against combat-state
entities that `Has<CharacterComponent>()`, `Has<CombatComponent>()`, `Has<VenueComponent>()` and are not
dead — **any combatant satisfying that filter with no corresponding `FromCharacter` entry throws
`KeyNotFoundException` and aborts the caller**, exactly as already established.

**Bottom line for modders**: never create, resurrect, or otherwise reference an `Entity` in combat state
(`_combatState.Entities`, `FromTile.Keys`, `FromCharacter.Keys`) that doesn't have matching entries in
*both* `FromTile` (if it's a tile) and `FromCharacter` (if it's a character with a model), and never destroy
tiles/actors out from under `VenueGameObjectMaps` while any `VisualSequence` built against the old map is
still queued or playing.

---

## 5. CAMERA — `VenueCameraController`

Full API surface (`VenueCameraController.cs`):

- **`SetRigType(string pDioramaRecord)`** (`:125-128`) — `_cameraRigType = VenueViewHelper.GetCameraRigType(pDioramaRecord)`.
  Purely metadata; does not touch any live Cinemachine camera. Called early in `CombatPhase.Initialize`
  (`CombatPhase.cs:196`), before the diorama's actual rig transform exists in scene.
- **`SetRig(Transform transformRig, Env pEnv = null)`** (`:252-278`) — the real rig activation. If
  `transformRig == _currentRig` it just re-enables the existing rig and returns early (`:254-258`).
  Otherwise: deactivates the previous `_activeVCam`, pulls `MinIntensityRigData`/`NormalIntensityRigData`/
  `MaxIntensityRigData` off the new rig's `VenueCameraRig` component, calls `SelectRigBasedOnIntensity()`
  to pick `CurrentRigData` from the user's action-camera-intensity setting, resets
  `_currentTrackingStrength = CurrentRigData.MinTrackingCenterStrength` and
  `_currentZoom = CurrentRigData.MinCameraZoom`, then activates the new rig GameObject. Called from
  `CombatPhase.Initialize` (`CombatPhase.cs:356`) and from the boss `CHANGE_CAMERA_RIG` event
  (`CombatPhase.cs:7877`).
- **`SetZoom(float pWantedZoom, float pWantedTrackingPower)`** (`:135-153`):
  ```csharp
  _currentZoom = pWantedZoom;
  _currentTrackingStrength = pWantedTrackingPower;
  ...
  _trackingCamera.m_Lens.FieldOfView = _activeVCam.m_Lens.FieldOfView - _currentZoom;
  _trackingCamera2.m_Lens.FieldOfView = _activeVCam.m_Lens.FieldOfView - _currentZoom;
  ```
  (`VenueCameraController.cs:151-152`) — confirms `FieldOfView = activeVCam.FieldOfView - zoom`, so a
  **negative** `pWantedZoom` widens the field of view (zooms out) and a positive value narrows it (zooms
  in). It also assumes `_activeVCam` is non-null — calling `SetZoom` before any rig/virtual-camera has been
  activated throws `NullReferenceException`.
- **`ResetZoom()`** (`:155-162`) — sets `_currentZoom = CurrentRigData.MinCameraZoom`. `CurrentRigData` is
  the static property backed by whichever of min/normal/max intensity rig data `SelectRigBasedOnIntensity()`
  picked during the last `SetRig` call — i.e. `MinCameraZoom` is **rig-and-intensity-specific**, not a
  universal constant. Called throughout combat whenever camera tracking is cancelled (`CombatPhase.cs:647,
  3439, 5034`) and on `CAMERA_CANCEL_TRACK` visual nodes.
- **`SetVirtualCamera(eVenueCameraRigAngles pAngle, ...)`** (`:314-344`) — looks up a child transform of
  `_currentRig` named after the angle enum (`_currentRig.Find(pAngle.ToString())`), activates its
  `CinemachineVirtualCamera` component as `_activeVCam`, and blends via Cinemachine's brain-driven timing
  (`getTimeTweenForBlend`). **If no child transform matches the angle name, it silently no-ops** (`if
  (transform != null)` guard at `:325` — no error logged) and `pOnComplete` still fires.

### The boss `CHANGE_CAMERA_RIG` re-framing sequence

```csharp
case eBossPhaseEventTypes.CHANGE_CAMERA_RIG:
{
    eVenueCameraRigAngles currentAngle = _venueCamera.GetCurrentAngle();
    eVenueCameraRigs eVenueCameraRigs2 = Enum.Parse<eVenueCameraRigs>(pPhaseEvent.Args);
    Transform currentRig = _venueCamera.GetCurrentRig();
    Transform transform3 = UnitySceneHelper.GetGameObjectFromScene(SceneManager.GetActiveScene(), "CameraDummyContainer")
        .transform.Find(eVenueCameraRigs2.ToString());
    transform3.transform.position = _diorama.GetVenueGridRoot().transform.position;
    transform3.transform.rotation = _diorama.GetVenueGridRoot().transform.rotation;
    _venueCamera.SetRig(transform3, _env);
    await _venueCamera.SetVirtualCamera(currentAngle);
    currentRig.gameObject.SetActive(value: false);
    break;
}
```
(`CombatPhase.cs:7869-7881`)

This is how a boss arena swaps its whole camera rig mid-fight: it (1) remembers the current angle so the
new rig can resume at the equivalent framing, (2) parses the target rig name out of the phase-event args,
(3) finds that rig prefab under the scene's `CameraDummyContainer`, (4) **re-anchors it to the diorama's
current `VenueGridRoot` position/rotation** (so the new rig lines up with wherever the grid actually is —
this matters if a mod has moved or resized the grid), (5) calls `SetRig` (which resets zoom/tracking
strength to the new rig's `MinCameraZoom`/`MinTrackingCenterStrength`), (6) re-applies the *previous* angle
on the new rig via `SetVirtualCamera`, then (7) deactivates the old rig GameObject. **A mod that swaps
camera rigs must replicate this re-anchoring step** — activating a new rig without repositioning it to
`VenueGridRoot` leaves the camera framing the old grid location.

---

## 6. MODDER_RULES

Derived directly from the above:

1. **Never spawn a venue tile Entity with `VenueTileComponent.GroupIndex < 0` (or unset/default) if you
   want it to render.** `CreateVenueTileGameObjects` puts it on the `"DoNotRender"` Unity layer
   (`VenueViewHelper.cs:104`), and `SetVenueTileRenderState` can never resolve it to anything but `Hidden`
   (`VenueViewHelper.cs:836-865`, no branch fires for `GroupIndex < 0`). This is the mechanism behind a
   larger modded grid drawing only faintly — the extra tiles most likely never got a valid `GroupIndex`.

2. **Never hold onto a tile `Entity` reference across a tile rebuild.** Anything that destroys/recreates
   `FromTile` (the game's own pattern is `CombatPhase.cs:7947-7961`, the boss `CHANGE_VENUE_GRID` event)
   invalidates every previously cached tile `Entity`. `VenueHelper.GetTileEntityOfCharacter` re-resolves by
   `(x, y)` position, not identity (`VenueHelper.cs:103-123`), but it returns `null` on no match, and the
   caller (`GetSpawnPositionPoint`, `CombatViewHelper.cs:1044-1066`) indexes `FromTile` with that possibly-null
   result unchecked. This is the exact bug class behind the live `JoinAbilityHitEffectNode` NRE seen when
   tiles were destroyed and rebuilt after `CombatPhase.Initialize`.

3. **Never register a combatant `Entity` in combat state (`_combatState.Entities`) before it has a
   `FromCharacter` entry**, and never leave a dead-but-still-tracked combatant without one either.
   `CombatPhase._clearTileRenderState` (`CombatPhase.cs:4736-4752`) and roughly 90 call sites across
   `CombatViewHelper.CreateVisualSequence` and `CharacterVisualHelper.RenderAbilityResults` index
   `FromCharacter`/`FromTile` with no `TryGetValue`/`ContainsKey` guard (§4 has the full line list) — any one
   of them throws and aborts whatever combat step was in progress the instant the map is missing an entry
   they expect.

4. **Follow the game's own summon order exactly when adding a mid-combat actor**: create Entity → set
   `VenueComponent` (`TilePosition`/`OccupiedTiles`) → register in combat state (`_addEntityToCombat` or
   equivalent) → `CharacterVisualHelper.CreateActorGameObject` → immediately assign into
   `FromCharacter[entity]` → position (`GetCharacterRootPosition`/`GetAveragePositionOfTiles`) → face
   (`VenueViewHelper.CharacterLookAtTarget`) → play an entrance animation (`CombatPhase.cs:7218-7304`).
   Firing a visual event like `CHARACTER_ADDED_SMOKE` for an entity before this sequence completes crashes
   at the lookup (`CharacterVisualHelper.cs:2417-2433`).

5. **Don't call `VenueCameraController.SetZoom` before a rig/virtual-camera is active.** It dereferences
   `_activeVCam.m_Lens.FieldOfView` unconditionally (`VenueCameraController.cs:151-152`) — no null guard.

6. **When swapping camera rigs, re-anchor the new rig transform to `_diorama.GetVenueGridRoot()`'s position
   and rotation before calling `SetRig`**, exactly as `CHANGE_CAMERA_RIG` does
   (`CombatPhase.cs:7874-7877`) — otherwise the new rig frames wherever it happened to be authored, not
   wherever the (possibly moved/resized) live grid actually is.

7. **`SetVirtualCamera` silently no-ops on a bad angle name** (`VenueCameraController.cs:325`, `if
   (transform != null)` with no else/log) — if a modded rig is missing a child transform for an angle the
   game tries to use, the camera simply stays wherever it was, with no error to signal the mistake. Verify
   rig child names match `eVenueCameraRigAngles` values exactly.

8. **`RenderVenueTile` looks up outline color from the `dBattleGridTile` config table by matching
   `TileType == pTileRender`** (`VenueViewHelper.cs:1536-1541`) with no null check on the `FirstOrDefault`
   result before dereferencing `.CharacterOutline`. Any custom `TileRender` value without a matching
   `dBattleGridTile` record crashes the moment it's used on an occupied tile.

9. **Remember `CombatPhase._gameObjectMaps` is a property, not a field** (established fact, restated here
   because it directly gates every technique above) — reflection-based access to `FromTile`/`FromCharacter`
   via a field lookup on `CombatPhase` silently returns null; go through `Env.VenueGameObjectMaps` or the
   `_gameObjectMaps` property directly.

---

FILE_WRITTEN: `C:\Users\ben\repos\ftk2-mods-crucible\docs\research\coverage\rendering.md`
