# Coverage Map: Character, Class, Stat, and Progression Systems

Scope: what a "character" entity IS at the component level; how `eCharacterStats` are computed;
how a class config is structured and how class-swap rebuilds an entity; XP/level/tier mechanics;
followers vs. combat summons; death and revive. Written so the team stops rediscovering these
systems feature by feature.

**Method.** Decompiled with:
```
DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/tools/ilspycmd.exe --disable-updatecheck -t <Type> \
  -r "C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed" \
  "<ManagedDir>/FTK2.dll"
```
Each type was decompiled alone with `-t <Type>`, so `File.cs:N` line numbers below are relative to
that single-type decompile output, not the original source layout. JSON facts are read directly
from `Characters.json` under
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\StreamingAssets\Assets\Configs\JSON~\`.

---

## 1. COMPONENTS — what a character IS

A "character" is an ECS `Entity` with a bag of components. None of these types have methods beyond
trivial helpers (`AvatarComponent.GetScale()`, `AvatarComponent.IsDirty`) — they are pure data,
serialized with `System.Text.Json`. Persistence is inferred from where a component is added/removed
in code: `PlayerComponent`/`CharacterComponent`/`AvatarComponent`/`AIComponent`/`StatusEffectComponent`
are added once at creation and never stripped by ordinary flow; `CombatComponent` and `VenueComponent`
are explicitly added when an entity enters a fight/venue tile and explicitly removed when it leaves
one (see `FollowerHelper.RemoveFollower`, `FollowerHelper.cs:217-224`, which strips both on follower
removal).

| Component | Persistent / Combat-scoped | Notable fields | Source |
|---|---|---|---|
| `CharacterComponent` | **Persistent** — the core identity record; always present on any character entity | `ConfigName`, `Variant`, `DisplayName`, `CurrentHealth`, `CurrentFocus`, `ExtraLives`, `NecroLives`, `ExtraLevel`, `CharacterType` (`eCharacterTypes`), `TypeArgs`, `GroupIndex` (0 = friendly, 1 = enemy — see §6), `Things` (inventory/passives-as-things), `Equipped`, `SkinEquipped`, `BaseStatModifiers`, `OverrideStats`, `Properties` (`eActorProperties`), `State` (`eCharacterStates`), `CustomData` | `CharacterComponent.cs:1-59` |
| `AvatarComponent` | **Persistent** — visual/customization state, travels with the entity | `PrimaryColor`/`SecondaryColor`/`SkinColor`/`HairColor`, `BodyType`, `ScaleMultiplier`, `Position`, dirty flags (`SkinDirty`, `EquipmentDirty`, `StatusDirty`, `PortraitDirty`) rolled into `IsDirty` | `AvatarComponent.cs:1-62` |
| `PlayerComponent` | **Persistent**, but player-only (party members) — not present on plain NPCs/enemies/most followers | `ActionPoints`, `ExtraMovementRolls`, `TurnsLeftToSkip`, `RoundsLeftToAutoRevive`, `RoundsWithoutMoving`, `Sneaked`, `HasMoved`, `TurnsPlayed`, `RespawnHexPosition`, `ThingsUserState` | `PlayerComponent.cs:1-28` |
| `AIComponent` | **Persistent** on any AI-controlled entity (enemies, some summons) | `Parameters` (string dict), `PriorityTargets` (queue of entity guids), `Properties` (`eAiProperties`, e.g. `AMBUSH`), `BehaviourShuffle` | `AIComponent.cs:1-12` |
| `StatusEffectComponent` | **Persistent** container, contents are transient | `Statuses: Dictionary<string, StatusEffectInfo>` | `StatusEffectComponent.cs:1-6` |
| `CombatComponent` | **Combat-scoped** — added when an entity enters combat venue tiles, removed on leaving (`FollowerHelper.RemoveFollower`, `FollowerHelper.cs:217-220`: `if (pFollower.Has<CombatComponent>()) pFollower.Remove<CombatComponent>();`) | `SecondaryActions`, `PrimaryActions`, `CanSummon`, `DeathSaves`, `DodgeCooldown`, `JuggleState` | `CombatComponent.cs:1-14` |
| `VenueComponent` | **Combat/venue-scoped** — tile placement data, added/removed alongside `CombatComponent` (`FollowerHelper.cs:221-224`) | `TilePosition (x,y)`, `TileSize (x,y)`, `OccupiedTiles` | `VenueComponent.cs:1-10` |

---

## 2. STATS

### 2.1 `eCharacterStats` enum (full member list)

```csharp
public enum eCharacterStats
{
    RND = -2, NONE, STR, VIT, INT, AWR, TAL, SPD, LCK, DEF, RES, EVD, PA, SA,
    MXFOC, FOC, MXHP, HP, XP, ACC, ATK, MAG, PHY, PRW, CRT, CRTD, GLD, XPM,
    SKL, HRG, LOP, THRN, MOV, LBM, WBM, FIND, PSTR, PVIT, PINT, PAWR, PTAL,
    PSPD, PLCK, PDEF, PRES, PEVD, PFOC, PGLD, DAM, PARTY_XP, CURRENT_FOCUS
}
```
Source: `eCharacterStats.cs:1-53`.

`eGetStatEquippedFilters`:
```csharp
public enum eGetStatEquippedFilters { NONE, ALL, HANDS }
```
Source: `eGetStatEquippedFilters.cs:1-5`. Passed into `CharacterHelper.GetStat` to control which
equipped `Thing`s contribute: `NONE` includes everything found via
`EquipmentHelper.GetEquippedThingsNonAlloc`; `ALL` **excludes** all `eThingTypes.EQUIPMENT` items
(keeps only non-equipment passives/traits); `HANDS` excludes only equipment in `MAIN_HAND`/`OFF_HAND`
slots (used to compute "what would this stat be without my weapon").

### 2.2 `CharacterHelper.GetStat` computation order

Full signature actually used for computed lookups (`CharacterHelper.cs:424`):
```csharp
public static int GetStat(Entity pCharacterEntity, string pStat, eGetStatEquippedFilters pEquipFilter,
    out List<(string, int)> pBonuses, bool pCappedStat = true, bool pIgnoreBaseStatModifiers = false)
```
Order of accumulation (`CharacterHelper.cs:424-583`):
1. `CURRENT_FOCUS` short-circuits to `GetFocus(pCharacterEntity)` — not a computed stat (`CharacterHelper.cs:428-431`).
2. **Base**: `int baseStatsTotal = GetCharacterBaseStat(pCharacterEntity, pStat);` — the config's base
   `Stats` value (`CharacterHelper.cs:437`).
3. **Extra-level scaling**: if `characterComponent.ExtraLevel > 0` and the entity's max level (player
   or enemy) exceeds 7, `baseStatsTotal = GetExtraLevelStats(...)` rescales it (`CharacterHelper.cs:438-441`).
4. **Equipment/passive Things**: iterates non-`PASSIVE` and equipped `Thing`s (filtered by
   `pEquipFilter` as above) and adds `InventoryHelper.GetThingStat(item, pStat)` for each
   (`CharacterHelper.cs:443-475`).
5. **`BaseStatModifiers`**: `if (!pIgnoreBaseStatModifiers && characterComponent.BaseStatModifiers.TryGetValue(pStat, out var value)) baseStatsTotal += value;` (`CharacterHelper.cs:476-479`) — this dictionary lives directly on `CharacterComponent.BaseStatModifiers` (`CharacterComponent.cs:44`).
6. **Party-wide stat**: `+= GetPartyStat(pStat)` if the entity `Has<PlayerComponent>()` (`CharacterHelper.cs:480`).
7. **Skill-specific bonus** (e.g. `SKILL_HEAVYHANDED` bumping `ATK`) is added and recorded in `pBonuses` (`CharacterHelper.cs:481-489`).
8. **Dungeon modifiers** (`ALL_HAS_STAT`, `PLAYER_HAS_STAT`, `ENEMY_HAS_STAT`, `BOSS_HAS_STAT`) applied via `DungeonModifierHelper.GetDungeonModifierResult` when a `DungeonState` is active (`CharacterHelper.cs:491-514`).
9. **Status effects**: sums `InventoryHelper.GetStatusStat` over every active status on the entity, recorded per-status in `pBonuses`; non-player entities additionally get `GetStatusEffectsModifierStatForNonPlayerCharacters` (`CharacterHelper.cs:516-536`).
10. **Follower-owner bonus**: `GetFollowerStatModifierFromOwner` — if the entity is a `COMPANION` and its owning player has `SKILL_NURTURE`, adds +3 `HRG` or +20 `XPM` (`CharacterHelper.cs:537-556`).
11. **Clamping**: parses `pStat` back to `eCharacterStats` and clamps to `TryGetMaxStatValue`/`TryGetMinStatValue` if defined (`CharacterHelper.cs:558-566`).

Quoted excerpt (steps 2, 5, 6, 11):
```csharp
int baseStatsTotal = GetCharacterBaseStat(pCharacterEntity, pStat);
...
if (!pIgnoreBaseStatModifiers && characterComponent.BaseStatModifiers.TryGetValue(pStat, out var value))
{
    baseStatsTotal += value;
}
baseStatsTotal += (pCharacterEntity.Has<PlayerComponent>() ? GetPartyStat(pStat) : 0);
...
int num2 = baseStatsTotal + num;
num2 += GetFollowerStatModifierFromOwner(pCharacterEntity, pStat);
if (Enum.TryParse<eCharacterStats>(pStat, out var result))
{
    if (TryGetMaxStatValue(result, out var pMaxStat)) { num2 = Math.Min(num2, pMaxStat); }
    if (pCappedStat && TryGetMinStatValue(result, out var pMinStat)) { num2 = Math.Max(num2, pMinStat); }
}
```
(`CharacterHelper.cs:437, 476-480, 558-566`)

`GLOBAL_CONSTANT`-style bounds live in `ProgressionHelper`: `MAX_CHARACTER_STAT = 95`,
`MIN_CHARACTER_STAT = 1` (`ProgressionHelper.cs:24-26`).

---

## 3. CLASSES

### 3.1 Structure in `Characters.json`

Player classes are ordinary entries in the flat `Characters.json` dict (2126 entries total,
confirmed by `python -c "len(json.load(...))"` = `2126`). A player class entry is tagged
`"PLAYER"` and carries `Level: -1` (not a level-tiered `FAMILY_VARIANT_NN` monster config). Example,
the base-game `EOR_WARRIOR` (a ClassForge-authored base class, same shape as vanilla classes):

```json
{
  "Stats": {
    "STR": 82, "LCK": 50, "SA": 1, "DEF": 2, "INT": 50, "SPD": 64, "PA": 1,
    "AWR": 66, "FOC": 3, "CRT": 5, "EVD": 0, "HP": 38, "VIT": 78, "RES": 0, "TAL": 58
  },
  "Things": {
    "AXE_WOODCUTTER_BASIC_00": 1,
    "SHIELD_BLACKSMITH_BASIC_00": 1,
    "HERB_GODSBEARD_01": 1
  },
  "Passives": ["SKILL_JUSTICE", "SKILL_GUARD"],
  "Rarity": "COMMON",
  "Level": -1,
  "Threat": 0,
  "BaseType": "HUMAN",
  "DefaultBodyType": "F",
  "Tags": ["PLAYER", "MELEE"],
  "Expansion": "BASE",
  "LocKey": "EOR_WARRIOR"
}
```
Source: `Characters.json` key `EOR_WARRIOR`, read directly via Python `json.load`.

Fields used: `Stats` (base `eCharacterStats` values), `Things` (starting inventory: config-name →
stack count), `Passives` (skill ids granted for free), `Rarity` (`eItemRarities`), `Level` (`-1` for
player classes; numeric for level-tiered NPC/monster configs — see §4), `BaseType` (creature family,
e.g. `HUMAN`), `DefaultBodyType` (`F`/`M`-style body mesh key), `Tags` (includes `PLAYER` to mark it
selectable; `MELEE`/etc. for gameplay classification). `Env.Configs.Characters` is the live runtime
map keyed by these same config-name strings — confirmed by the crucible tool
`ConfigCommands.CrucibleClassConfig` (`FTK2.Crucible/src/Crucible.Plugin/ConfigCommands.cs:91-109`),
which reads exactly `Passives`, `Things`, `Stats`, `BaseType`, `Rarity`, `LocKey` off the live config
object at runtime.

### 3.2 Rebuild path when a character's class changes

`PartyManagementDirector._rebuildCharactertAsNewConfigType(Entity pCharacter, string pClassConfigName, ...)`
(`PartyManagementDirector.cs:1846-1878`):

```csharp
string configName = pCharacter.Get<CharacterComponent>().ConfigName;
pCharacter.Get<CharacterComponent>().ConfigName = pClassConfigName;
pCharacter.Get<AvatarComponent>().BodyType = Env.Configs.Characters[pClassConfigName].DefaultBodyType;
pCharacter.Get<AvatarComponent>().SkinDirty = true;
_rebuildCharacterEntity(pCharacter, configName);
```

It sets `ConfigName` to the new class id, then calls `_rebuildCharacterEntity`
(`PartyManagementDirector.cs:2152-2199`), which:
1. Snapshots cosmetic-only state off the current components (skin/hair/primary/secondary color,
   body type, skin cosmetics, dirty flags, nomenclator, display name).
2. `Entity entity = CharacterHelper.CreatePlayableCharacterEntity(configName, displayName, _gameRandom, null, pAddPlayerComponent: false);` — builds a **brand-new** `CharacterComponent`
   from the new class config (fresh `Stats`, `Things`, `Passives` — see §3.1), discarding the old one.
3. `pCharacter.Remove<CharacterComponent>(); pCharacter.Remove<AvatarComponent>();` then
   `pCharacter.Add<CharacterComponent>(entity.Get<CharacterComponent>());` and adds a freshly built
   `AvatarComponent` (`CharacterVisualHelper.CreateAvatarComponent`) — full component replacement, not
   an in-place field edit.
4. Re-applies the snapshotted cosmetic fields onto the new `AvatarComponent`.
5. Grants `CoreHelper.GetDifficultyPlayerStartThings()` and auto-equips any equippable starting
   `Thing` via `EquipmentHelper.Equip`.

So a class change is a **full identity rebuild**: `SkinEquipped` and `Nomenclator` are the only
`CharacterComponent` fields explicitly carried over (`entity.Get<CharacterComponent>().SkinEquipped = skinEquipped; entity.Get<CharacterComponent>().Nomenclator = nomenclator;`,
`PartyManagementDirector.cs:2169-2170`); everything else (stats, things, passives, equipped items,
current HP/focus, XP) resets to the new class's config defaults. Display name is preserved via the
cosmetic snapshot at the top of `_rebuildCharacterEntity`, separate from the `ConfigName`-based
`Lang.__t` fallback name swap in the caller (`PartyManagementDirector.cs:1863-1866`).

---

## 4. LEVELLING and TIERS

### 4.1 XP tables

```csharp
public static int[] PLAYER_XP_LEVELS = new int[12]
{ 50, 140, 255, 455, 720, 1175, 1770, 2800, 4500, 7200, 11400, 18200 };

public static int[] COMPANION_XP_LEVELS = new int[12]
{ 45, 125, 230, 410, 650, 1060, 1595, 2520, 3645, 5832, 9234, 14742 };
```
Source: `ProgressionHelper.cs:8-17`. Levels are 1-indexed against these arrays; index selection is
by `entityXP < array[i]`.

`_getXPLevels(Entity)` picks the table by whether the entity is a `COMPANION`:
```csharp
private static int[] _getXPLevels(Entity pEntity)
{
    if (pEntity.Get<CharacterComponent>().CharacterType != eCharacterTypes.COMPANION)
        return PLAYER_XP_LEVELS;
    return COMPANION_XP_LEVELS;
}
```
(`ProgressionHelper.cs:148-154`) — note this means **mercenaries and curse followers use the
PLAYER table**, not the companion table; only `COMPANION` uses `COMPANION_XP_LEVELS`.

`PLAYER_LEVEL_CAP` / `COMPANION_LEVEL_CAP` are both `Math.Min(CharacterHelper.GetPlayerMaxLevel(), <table>.Length)` (`ProgressionHelper.cs:144-146`) — level cap is dynamically bounded by a game-difficulty-driven max, not just table length.

### 4.2 Level derivation

```csharp
public static int GetEntityLevel(Entity pEntity)
{
    int entityXP = GetEntityXP(pEntity);
    int[] array = _getXPLevels(pEntity);
    for (int i = 0; i < array.Length; i++)
    {
        if (entityXP < array[i]) { return i; }
    }
    return array.Length;
}
```
(`ProgressionHelper.cs:565-576`). `GetEntityXP` reads the stack count of the character's `XP` `Thing`:
```csharp
public static int GetEntityXP(Entity pPlayer)
{
    return pPlayer.Get<CharacterComponent>().Things.Find((Thing t) => t.ConfigName == "XP")?.StackCount ?? 0;
}
```
(`ProgressionHelper.cs:555-558`). So level is **derived**, not stored — it is recomputed on demand
from the `XP` Thing's stack against the XP table. `EntityGainXP` grants it:
```csharp
Thing thing = pEntity.Get<CharacterComponent>().Things.Find((Thing t) => t.ConfigName == "XP");
if (thing == null) { thing = InventoryHelper.CreateThing("XP"); pEntity.Get<CharacterComponent>().Things.Add(thing); }
...
thing.TryIncreaseStack(num);
```
(`ProgressionHelper.cs:664-687`, abridged).

### 4.3 Config-tier rewriting

```csharp
public static string GetCharacterConfigAtLevel(string pConfigName, int pLevel)
{
    if (InteractableHelper.GetValueFromConfigName(pConfigName) == -1) { return pConfigName; }
    string text = pConfigName.Substring(0, pConfigName.Length - 2) + pLevel.ToString("00");
    if (Env.Configs.Characters.ContainsKey(text)) { return text; }
    for (int num = pLevel; num >= 0; num--)
    {
        text = pConfigName.Substring(0, pConfigName.Length - 2) + num.ToString("00");
        if (Env.Configs.Characters.ContainsKey(text)) { return text; }
    }
    return pConfigName;
}
```
(`CharacterHelper.cs:2342-2361`) — confirms the known fact exactly: it rewrites the trailing two
digits and, if the exact-level tier config doesn't exist, walks **down** to the nearest lower tier
that does. `InteractableHelper.GetValueFromConfigName(pConfigName) == -1` short-circuits configs
that aren't level-tiered at all (e.g. player classes with `Level: -1`) and returns them unchanged —
this is why player classes are never rewritten by this function (see §4.4).

Actual rebuild of an entity onto a new tier — `CharacterHelper.TryProgressCharacterEntityToLevel`
(`CharacterHelper.cs:2042-2077`):
```csharp
string characterConfigAtLevel = GetCharacterConfigAtLevel(characterComponent.ConfigName, pLevel);
bool num = !characterConfigAtLevel.Equals(characterComponent.ConfigName);
...
if (num)
{
    CharacterComponent characterComponent2 = CreateCharacterEntity(characterConfigAtLevel, pIsNpc: true, "temp", pGameRandom, null, null, characterComponent.BaseStatModifiers, pAddPlayerComponent: false, eSummonTypes.NONE, pLevel).Get<CharacterComponent>();
    characterComponent2.CharacterType = characterComponent.CharacterType;
    characterComponent2.TypeArgs = characterComponent.TypeArgs;
    characterComponent2.GroupIndex = characterComponent.GroupIndex;
    AvatarComponent avatarComponent2 = CharacterVisualHelper.CreateAvatarComponent(characterConfigAtLevel, pGameRandom != null, pGameRandom);
    ... // copy colors from old avatar
    pCharacterEntity.Remove<CharacterComponent>();
    pCharacterEntity.Add<CharacterComponent>(characterComponent2);
    pCharacterEntity.Remove<AvatarComponent>();
    pCharacterEntity.Add<AvatarComponent>(avatarComponent2);
    ProgressionHelper.EntityGainXP(pCharacterEntity, entityXP, pConsiderMultiplier: false, null);
}
```
This rebuilds **both** `CharacterComponent` and `AvatarComponent` wholesale (matching the known
fact), carrying forward `CharacterType`, `TypeArgs`, `GroupIndex`, avatar colors, and re-granting the
prior XP total on the freshly-created entity.

`TryProgressCompanionEntityToLevel` (`CharacterHelper.cs:2090-2119`) wraps the above and then
restores more state that the raw rebuild would otherwise drop:
```csharp
bool num = TryProgressCharacterEntityToLevel(pCharacterEntity, pLevel, pGameRandom);
CharacterComponent characterComponent2 = pCharacterEntity.Get<CharacterComponent>();
AvatarComponent avatarComponent2 = pCharacterEntity.Get<AvatarComponent>();
characterComponent2.State = characterComponent.State;
characterComponent2.BaseStatModifiers = characterComponent.BaseStatModifiers;
characterComponent2.CustomData = characterComponent.CustomData;
characterComponent2.DisplayName = characterComponent.DisplayName;
avatarComponent2.PrimaryColor = avatarComponent.PrimaryColor;
avatarComponent2.SecondaryColor = avatarComponent.SecondaryColor;
avatarComponent2.HairColor = avatarComponent.HairColor;
avatarComponent2.SkinColor = avatarComponent.SkinColor;
if (pXPThing != null) { ... copies XP stack count onto the new entity's XP Thing ... }
if (!num && int.TryParse(characterComponent2.ConfigName.Split('_').Last(), out var result))
{
    characterComponent2.ExtraLevel = Math.Max(0, pLevel - result);
}
```
This confirms the known fact: companion progression preserves `State`, colours, `BaseStatModifiers`,
`CustomData`, `DisplayName`, and XP stack across the config-id swap, where a raw
`TryProgressCharacterEntityToLevel` call would not.

### 4.4 What visibly changes on level up; PLAYER vs NPC/companion config-id behaviour

In `ProgressionHelper.EntityGainXP` (`ProgressionHelper.cs:648-722`), once `entityLevel2 > entityLevel`:
```csharp
if (pConsiderProgressCompanion && !pEntity.Has<PlayerComponent>() && pEntity.Get<CharacterComponent>().CharacterType == eCharacterTypes.COMPANION)
{
    CharacterHelper.TryProgressCompanionEntityToLevel(pEntity, entityLevel2, thing, null);
}
CharacterHelper.SetToMaxHealth(pEntity);
if (CoreHelper.HasPassive(pEntity, "SKILL_EUREKA") && CharacterHelper.GetFocusMissing(pEntity) > 0)
{
    CharacterHelper.SetToMaxFocus(pEntity);
    ...
}
else
{
    CharacterHelper.AddFocus(pEntity, 2);
}
pResults?.Add((eAbilityResults.LEVELED_UP, pEntity.Guid));
```
**Confirmed explicitly: only `COMPANION`-type, non-player entities get their config id rewritten on
level-up inside `EntityGainXP`.** A `PlayerComponent`-carrying entity (`pEntity.Has<PlayerComponent>()`)
never enters that branch — `TryProgressCompanionEntityToLevel` is guarded by
`!pEntity.Has<PlayerComponent>()`, so **a player's `CharacterComponent.ConfigName` does not change on
level up.** Player classes are `Level: -1` in `Characters.json` (§3.1) and are not `FAMILY_VARIANT_NN`
tiered configs at all, so `GetCharacterConfigAtLevel` would be a no-op for them regardless (§4.3).
NPC/enemy configs get tier-rewritten separately, on-demand, via `GetEncounterProgressionEnemies` →
`GetCharacterConfigAtLevel` (`CharacterHelper.cs:2412-2423`) when enemies are spawned/scaled for the
current dungeon/progression level — not through the `EntityGainXP` level-up path at all (enemies
don't gain XP the way player-party characters do).

On every level-up (player or companion, generic): full heal (`SetToMaxHealth`), +2 focus (or full
focus + `SKILL_EUREKA` proc if that skill is owned and focus is missing), and a
`(eAbilityResults.LEVELED_UP, pEntity.Guid)` result is queued.

---

## 5. FOLLOWERS and COMPANIONS

### 5.1 Storage

```csharp
public Dictionary<string, FollowerState> PlayerFollowers;
```
(`GameRunData.cs:58`, confirmed also at instantiation `GameRunData.cs:120`:
`PlayerFollowers = new Dictionary<string, FollowerState>()`). Keyed by the **owning player's**
entity guid, value is:
```csharp
public class FollowerState { public string FollowerID; public int RoundsToExpire; }
```
(decompiled `FollowerState` type, 5 lines). One follower per player entity — the dictionary shape
itself enforces "one follower at a time" generically (a second `JoinFollower` call for the same
player overwrites the dictionary entry — see `JoinFollower` below), independent of the honeybee
special-case.

### 5.2 Core API (`FollowerHelper`, all `FollowerHelper.cs`)

- **`HasAFollower(Entity pPlayer, Env pEnv)`** (`:245-248`): `return pEnv.GameRun.PlayerFollowers.ContainsKey(pPlayer.Guid);` — pure dictionary check, fully generic.
- **`IsAFollower(Entity pCharacter, Env pEnv)`** (`:250-253`): reverse lookup — is this entity anyone's `FollowerID`.
- **`JoinFollower(Entity pPlayer, Entity pFollower, Env pEnv, GameRandom pGameRandom, int pRoundsToExpire = -1, bool pDisplayMessage = true)`** (`:105-156`): sets `pFollower`'s `GroupIndex` to match the player's; if the player has an `AdventureComponent` and the follower doesn't, gives the follower one at the player's hex and adds it to `pEnv.HexMap`; if the player already `HasAFollower`, removes the *old* follower's venue-player-name entry (but does **not** call `RemoveFollower` — the old follower entity is simply displaced from `VenuePlayerNames`, its `PlayerFollowers` entry gets overwritten below); adds the follower to `pEnv.GameRun.Entities` if not already present; adds to `VenueState.VenuePlayerNames` if in a venue; finally `pEnv.GameRun.PlayerFollowers[pPlayer.Guid] = new FollowerState { FollowerID = pFollower.Guid, RoundsToExpire = pRoundsToExpire };`. One special case inside this otherwise-generic function: if `pFollower.Get<CharacterComponent>().TypeArgs == "COMPANION_WATCHER_01"`, it additionally clears/reapplies a `STATUS_LINK_INFINITE_00` link status (`:107-119`) — unrelated to the honeybee.
- **`RemoveFollower(Env pEnv, Entity pPlayer, Entity pFollower, bool pDisplayMessage = true, bool pRemoveFromEntity = true, bool pRemoveVenueComponent = true)`** (`:187-234`): removes the `PlayerFollowers` dictionary entry; **conditionally** removes the follower entity from `pEnv.GameRun.Entities` and the hex map — `if (pRemoveFromEntity && !IsSpecialFollower(pFollower))` (`:205-216`) — so a special follower (the honeybee) is *never* deleted from the world entity list on removal, only unlinked from the player. Strips `CombatComponent`/`VenueComponent` if present (`:217-224`). Calls `SpecialFollowerRemove(pPlayer, pFollower)` unconditionally at the end (`:233`).
- **`RemoveFollowerFromPlayer(Env pEnv, VenueGameObjectMaps pGameObjectMaps, Entity pPlayer)`** (`:158-185`): looks up the player's current follower via `PlayerFollowers[pPlayer.Guid].FollowerID`, calls `RemoveFollower`, and — generically for any dead `COMPANION` or `MERCENARY` — appends a `PET_DEATH`/`MERC_DEATH` summary event.
- **`IsSpecialFollower(Entity pFollower)`** (`:347-354`):
  ```csharp
  public static bool IsSpecialFollower(Entity pFollower)
  {
      if (pFollower.TryGet<CharacterComponent>(out var pComponent) && pComponent.TypeArgs == "COMPANION_BUMBLEBEE_01")
      {
          return true;
      }
      return false;
  }
  ```
  **Hard-coded to the literal string `"COMPANION_BUMBLEBEE_01"`.** This is the only "special
  follower" check in the file — there is no generic flag/tag mechanism for "keep this follower
  resident between fights"; it's a single `if` on one config id.
- **`SpecialFollowerRemove(Entity pCharacter, Entity pFollower)`** (`:329-345`): also hard-coded to
  `characterComponent.TypeArgs == "COMPANION_BUMBLEBEE_01"`; on removal, sets a `"COOLDOWN"` custom
  data value (`"3"` if the bee is alive, `"5"` if dead) on the `TOOL_HONEYBEE_01` item `Thing` that
  summoned it (looked up via `CoreHelper.GetCustomData(characterComponent, "GUID")` →
  `InventoryHelper.GetCharacterThing`). This is entirely honeybee-specific bookkeeping, not a general
  follower mechanic.

### 5.3 Honeybee lifecycle (`SUMMON_HONEYBEE` ability handling, `CombatHelper.cs:2169-2225`)

Confirms the honeybee is **not** a generic `TryCreateSummon` combat summon — it goes through the same
`FollowerHelper.JoinFollower` call any generic follower uses:
```csharp
if (pAbilityName == "SUMMON_HONEYBEE")
{
    string guid = CoreHelper.GetCustomData(pThing, "GUID");
    Entity entity3 = pEnv.GameRun.Entities.FirstOrDefault((Entity x) => x.Guid == guid);
    ...
    if (pOrigin != null)
    {
        FollowerHelper.JoinFollower(pOrigin, entity3, pEnv, pGameRandom, -1, pDisplayMessage: false);
    }
    ... // re-grant XP, add VenueComponent/CombatComponent, set tile, group index, scale
    pEnv.GameRun.CombatState.RoundEntities.Add(entity3);
    pEnv.GameRun.CombatState.Entities.Add(entity3);
    SetInitiative(entity3, pParty, pEnv.GameRun.CombatState, pEnv.GameRun, pResults);
}
```
The bee entity is looked up by a `"GUID"` custom-data value stored on the `TOOL_HONEYBEE_01` tool
`Thing` (so the *same* bee entity persists across summons — it is created once and then
found/reused, not recreated each time), rejoined as a follower, and given fresh `VenueComponent`
+ `CombatComponent` for this fight.

### 5.4 Follower vs. combat summon comparison

| Aspect | Follower (generic, `PlayerFollowers` dict) | Combat summon (generic, `eActorProperties.SUMMON`) |
|---|---|---|
| Registration | `pEnv.GameRun.PlayerFollowers[playerGuid] = FollowerState{...}` (`FollowerHelper.JoinFollower`, `FollowerHelper.cs:147-151`) | Marked via `CharacterHelper.ActorAddProperty(eActorProperties.SUMMON, pSummonEntity)` (`CombatHelper.cs:248`); no `PlayerFollowers` entry unless separately joined as a follower (`eSummonTypes.AS_FOLLOWER` path) |
| Entity list membership | Added to `pEnv.GameRun.Entities` and stays there across combat (not removed unless `RemoveFollower` runs) | Added to `pEnv.GameRun.CombatState.Entities` / `RoundEntities`; at combat end, `CombatHelper.TryEndGame` explicitly strips it: `list2.RemoveAll((Entity e) => !CharacterHelper.IsEnemy(e) && (CharacterHelper.IsReflection(e) \|\| CharacterHelper.IsSummon(e)));` (`CombatHelper.cs:692`) |
| Lifecycle across fights | Persists between encounters until explicitly `RemoveFollower`'d, dismissed, or expires (`FollowerState.RoundsToExpire`) | Ephemeral — created for one fight via `TryCreateSummon`/`SUMMON_HONEYBEE` special-case, gone once combat ends (unless it was also joined as a follower via `eSummonTypes.AS_FOLLOWER`, see below) |
| Death | Generic `KillCharacter` (§6); a dead `COMPANION`/`MERCENARY` follower fires `PET_DEATH`/`MERC_DEATH` summary events in `RemoveFollowerFromPlayer` (`FollowerHelper.cs:162-179`) | Same `KillCharacter` path if it has a `CharacterComponent`; typically just vanishes with the rest of `CombatState.Entities` at combat end |
| Healing/persistence between fights | Retains `CurrentHealth`/`CurrentFocus`/XP as a normal party-adjacent entity | N/A — doesn't outlive the fight unless also a follower |
| One-at-a-time rule | Enforced generically by the `Dictionary<string,FollowerState>` shape (one entry per player guid) — a new `JoinFollower` call overwrites the old entry | N/A |
| Special-cased entity ("honeybee model") | `COMPANION_BUMBLEBEE_01` is hard-coded in `IsSpecialFollower` to survive `RemoveFollower`'s entity-list deletion (kept resident in `pEnv.GameRun.Entities` between fights) and to get honeybee-specific cooldown bookkeeping in `SpecialFollowerRemove` | Generic `eSummonTypes.AS_FOLLOWER` summons (not honeybee-specific) go through `FollowerHelper.JoinFollower` too when summoned in combat (`CombatHelper.cs:2224-2244`), inheriting XP/level from the owner via `TryProgressCompanionEntityToLevel` |

**What's generic vs. what's hard-coded, precisely, for a future pet class:** the entire
join/remove/has-a-follower/one-at-a-time machinery (`JoinFollower`, `RemoveFollower`,
`HasAFollower`, `PlayerFollowers` dict, `eCharacterTypes.COMPANION`, `eSummonTypes.AS_FOLLOWER`
combat-summon-becomes-follower path, XP/level inheritance via `TryProgressCompanionEntityToLevel`,
`GetCompanionScaleForLevel`) is fully generic and reusable for any companion config. Only two things
are hard-coded to the literal id `"COMPANION_BUMBLEBEE_01"`: (1) `IsSpecialFollower` — "don't delete
this entity from the world when its follower slot is vacated", and (2) `SpecialFollowerRemove` — the
tool-cooldown bookkeeping tied to `TOOL_HONEYBEE_01`. Everything else the honeybee does
(`SUMMON_HONEYBEE` ability branch in `CombatHelper.cs:2169-2225`) is also somewhat special-cased in
that it looks up the bee by a stored `"GUID"` custom-data value instead of using the generic
`TryCreateSummon`+`AS_FOLLOWER` path that other summon-as-follower abilities use — a pet class
copying this model should prefer the generic `AS_FOLLOWER` summon path (`CombatHelper.cs:2224-2251`)
over replicating the honeybee's bespoke GUID-lookup branch, unless "resident even when its follower
slot is empty" behavior is specifically wanted (in which case `IsSpecialFollower` needs a new
id/tag check, since it is currently a single hard-coded string, not a data-driven flag).

---

## 6. DEATH and REVIVE

### 6.1 `IsDead`
```csharp
public static bool IsDead(Entity pEntity)
{
    return pEntity.Get<CharacterComponent>().CurrentHealth < 1;
}
```
(`CharacterHelper.cs:1472-1475`) — generic, HP-threshold check, no type distinction.

### 6.2 `KillCharacter`
```csharp
public static void KillCharacter(Entity pTargetEntity, List<(eAbilityResults, object)> pResults, bool pPlaySoundFX = true)
{
    if (pPlaySoundFX) { AudioHelper.PlayPlayerDeadSFX(); }
    CustomEvents.SetCharacterData(pTargetEntity.Guid, delegate(CustomEvents.CharacterData cd) { cd.Deaths++; return cd; });
    CoreHelper.ClearEntityStatuses(pTargetEntity, pResults, pKeepOverworldStatuses: false);
    pTargetEntity.Get<CharacterComponent>().CurrentHealth = 0;
    pTargetEntity.Get<CharacterComponent>().CurrentFocus = 0;
    if (pTargetEntity.TryGet<PlayerComponent>(out var pComponent))
    {
        pComponent.ActionPoints = 0;
        pComponent.RoundsLeftToAutoRevive = 2;
    }
    if (pTargetEntity.TryGet<AvatarComponent>(out var pComponent2)) { pComponent2.PortraitDirty = true; }
    if (pResults != null && !pResults.Any(...DIED... == pTargetEntity.Guid))
    {
        if (pComponent != null)
        {
            RouterMono.GetEnv().GameRun.AdventureState.SummaryEvents?.Add(new AdventureSummaryViewHelper.SummaryEvent
            {
                Type = AdventureSummaryViewHelper.eSummaryEventType.PLAYER_DEATH,
                EventDetail = null,
                PlayerIndex = RouterMono.GetEnv().User.PartyCharacters.IndexOf(pTargetEntity)
            });
        }
        pResults.Add((eAbilityResults.DIED, pTargetEntity.Guid));
    }
}
```
(`CharacterHelper.cs:2166-2199`, lightly reformatted). This is the generic kill path for **any**
character entity: zeroes HP/focus, clears statuses, flags portrait dirty, emits `eAbilityResults.DIED`.

**Player-specific branch**: only if the entity `TryGet<PlayerComponent>` does it zero
`ActionPoints` and set `RoundsLeftToAutoRevive = 2` (auto-revive countdown), and only for a
`PlayerComponent`-carrying entity does it log a `PLAYER_DEATH` summary event keyed by
`User.PartyCharacters.IndexOf(pTargetEntity)`. A non-player creature (enemy, NPC, non-player
follower) skips both — it has no `RoundsLeftToAutoRevive` field at all (`PlayerComponent` isn't
present), so it simply stays dead until externally revived/removed; no auto-revive countdown applies
to it.

### 6.3 `TryKillCharacter` — revive-avoidance branches before death is finalized (`CharacterHelper.cs:2130-2164`)
Before calling `KillCharacter`, the caller checks, in order:
1. `CoreHelper.HasStatusType(pTarget, eStatusEffectTypes.DEATHSAVE)` → `DeathSavePlayer` instead (sets `CurrentHealth = 1`, emits `DEATH_SAVED`), no death occurs.
2. Otherwise `KillCharacter` runs, then: `if (characterComponent.ExtraLives > 0)` → `ReviveType = STANDARD`, `result = false` (death is "soft" — an extra life absorbs it, no permanent death recorded).
3. Otherwise, for **non-player** `STANDARD`/`FORCED_FIGHT` enemies only (`!pTarget.Has<PlayerComponent>()`), an RNG-gated `ScourgeHelper.TryScourgeAbilityProc(eScourgeAbilities.NECRO_REVIVE, ...)` can increment `NecroLives` and cancel the kill (`ReviveType = NECRO`) — this is an enemy-only necromancy revive mechanic with no player equivalent.

### 6.4 `PlayerComponent.RoundsLeftToAutoRevive`
Field lives on `PlayerComponent` (`PlayerComponent.cs:11`), set to `2` on death
(`CharacterHelper.cs:2183`, quoted above). Could not verify the exact countdown/consume site (the
code that decrements it each round and performs the actual auto-revive) within the scope of this
pass — see UNVERIFIED.

### 6.5 `ReviveCharacter` (generic, any entity)
```csharp
public static void ReviveCharacter(Entity pTargetEntity, List<(eAbilityResults, object)> pResults)
{
    CharacterComponent characterComponent = pTargetEntity.Get<CharacterComponent>();
    string sanctumID = characterComponent.SanctumID;
    characterComponent.CurrentHealth = GetMaxHealth(pTargetEntity) / 2;
    characterComponent.CurrentFocus = (pTargetEntity.Has<PlayerComponent>() ? 1 : 0);
    ...
}
```
(`CharacterHelper.cs:2200-2206+`) — revives to half max HP; focus resets to `1` for players, `0` for
non-players. If the character had a `SanctumID` set (was resurrected at a Sanctum), extra bookkeeping
clears the sanctum link and logs a `LOSE_SANCTUM` summary event.

---

## HARD_CODED_IDS — every literal config id found special-cased in code

| Literal id | Where | What it does |
|---|---|---|
| `"COMPANION_BUMBLEBEE_01"` | `FollowerHelper.IsSpecialFollower` (`FollowerHelper.cs:347-354`) | Marks this exact `TypeArgs` as exempt from entity-list deletion on `RemoveFollower` — kept resident between fights. |
| `"COMPANION_BUMBLEBEE_01"` | `FollowerHelper.SpecialFollowerRemove` (`FollowerHelper.cs:329-345`) | On removal, sets a `"COOLDOWN"` custom-data value (`"3"` alive / `"5"` dead) on the summoning `Thing`. |
| `"COMPANION_BUMBLEBEE_01"` (`FollowerHelper.COMPANION_BUMBLEBEE` const) | `FollowerHelper.cs:12` | Named constant holding the same literal (declared but the two call sites above compare against the raw string, not this const — worth flagging as a minor inconsistency, not a second special-case). |
| `"CURSE_GHOST_01"` (`FollowerHelper.CURSE_GHOST_FOLLOWER` const) | `FollowerHelper.cs:10` | Declared constant; not observed being read anywhere in the decompiled `FollowerHelper.cs` beyond the declaration — likely used elsewhere (curse-follower dialogue/behaviour code) outside this pass's scope. |
| `"COMPANION_WATCHER_01"` | `FollowerHelper.JoinFollower` (`FollowerHelper.cs:107-119`) | On join, swaps a `STATUS_LINK_INFINITE_00` link status from whatever the player was linked to onto this follower — a link-companion-specific mechanic, unrelated to the honeybee's residency special-case. |
| `"SUMMON_HONEYBEE"` (ability name) | `CombatHelper.cs:2174-2225` | Bespoke combat-ability branch: looks up the persistent bee entity by a `"GUID"` custom-data value rather than using the generic `TryCreateSummon` + `eSummonTypes.AS_FOLLOWER` path used by other summon-as-follower abilities. |
| `"DOLL_SUMMON_01"` (ability name) | `CombatHelper.cs:2169-2173` | Separate special-case forcing `eSummonTypes.PLAYTHING` and deriving the summon target from the item's config-name suffix. |
| `"XP"` | `ProgressionHelper` (multiple sites, e.g. `ProgressionHelper.cs:555-558, 665-666`) and `CombatHelper.cs` (honeybee XP re-grant) | The XP-tracking `Thing`'s `ConfigName` is the literal string `"XP"` — level is derived from this Thing's stack count, not a dedicated field. |
| `"SKILL_HEAVYHANDED"` | `CharacterHelper.GetStat` (`CharacterHelper.cs:481-489`) | Adds a special `ATK` bonus computed by `SkillHelper.GetHeavyHandedBonusAttack` inside the generic stat pipeline. |
| `"SKILL_EUREKA"` | `ProgressionHelper.EntityGainXP` (`ProgressionHelper.cs:712-719`) | On level-up, grants full focus instead of the generic +2 if this passive is owned. |
| `"SKILL_NURTURE"` | `CharacterHelper.GetFollowerStatModifierFromOwner` (`CharacterHelper.cs:537-556`) | Grants +3 `HRG` / +20 `XPM` to a `COMPANION` follower if its owning player has this skill. |
| `"SKILL_SUMMONREVENGE"` | `CombatHelper.cs:2258-2280` (approx.) | Special-cased AI targeting/ambush setup applied to summoned entities with this skill. |

---

## UNVERIFIED

- **Where `RoundsLeftToAutoRevive` is decremented and where the actual auto-revive fires.** Confirmed
  the field exists on `PlayerComponent` (`PlayerComponent.cs:11`) and is set to `2` in
  `KillCharacter` (`CharacterHelper.cs:2183`), but the per-round decrement/consume logic was not
  located within the files decompiled for this pass (likely in a turn/round-processing helper not
  covered here, e.g. an adventure/overworld turn director).
- **`GetCharacterBaseStat`, `GetExtraLevelStats`, `GetPartyStat`, `TryGetMaxStatValue`/`TryGetMinStatValue`, `GetCompanionScaleForLevel`, `TryCreateSummon`** — referenced and quoted at their call sites above but their own bodies were not decompiled/read in this pass; behavior described is only what's inferable from the call sites shown.
- **`CURSE_GHOST_FOLLOWER` (`"CURSE_GHOST_01"`) usage** — the constant is declared (`FollowerHelper.cs:10`) but no read site was found inside `FollowerHelper.cs` itself; not confirmed where/how it's actually used.
- **Exact set of vanilla (non-ClassForge) player class config ids.** `Characters.json` was confirmed to hold player classes as flat entries tagged `"PLAYER"` (verified via `EOR_WARRIOR`, a ClassForge base-pack class using the same schema), but a full enumeration of the base game's own class ids (as opposed to `EOR_*`/`CF_*` ClassForge-authored ones already inventoried in `docs/research/class-test-matrix.md`) was not performed in this pass.
- **`OverrideStats` field on `CharacterComponent`** (`CharacterComponent.cs:46`) — present in the data shape but no read/write site was located in `CharacterHelper.GetStat` or elsewhere in this pass; its role relative to `BaseStatModifiers` is not confirmed.

---

FILE_WRITTEN: `C:\Users\ben\repos\ftk2-mods-crucible\docs\research\coverage\characters-progression.md`
