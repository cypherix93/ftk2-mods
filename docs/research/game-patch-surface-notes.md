# Game patch-surface notes — ClassForge / Summoner / DevKit ground truth

**Date:** 2026-07-25 · **Source:** `tools/out/decompile/FTK2/*.cs` (per-class ilspycmd decompile of `FTK2.dll`,
already present in this repo — one `.cs` file per type, `public class X { ... }` at top). All line numbers below
are into the specific `.cs` file named in each heading, as it exists at the time of this audit.

**Purpose:** resolve `FTK2.ClassForge/SPEC.md` §11 Open Questions #2 (`ON_CRIT` signal), #3
(`ConfigsHelper` load path), #4 (`ADD_CHARACTER` `{Type,Value}` mapping), and extract verbatim signatures for
every method named in SPEC §6 (patch targets) and `docs/research/eor-0760-content-audit.md` §7 (primitive
candidates). Extends `docs/research/game-code-reference.md` — nothing here contradicts it except where called out
explicitly under "Corrections to game-code-reference.md" at the end.

---

## 1. `CombatHelper` — ability execution, initiative, summons, status ticking

File: `tools/out/decompile/FTK2/CombatHelper.cs` (2,783 lines).

### `PerformAbility` (one overload only — no overloads found)

```csharp
// L1155
public static List<(eAbilityResults result, object arg)> PerformAbility(Entity pOrigin, Entity pTarget, List<Entity> pParty, Thing pThing, CombatDecisionData pCombatDecision, RollResultData pRollData, Func<Entity, string, eGetStatEquippedFilters, int> pGetStat, Func<Entity, string, int> pGetTileStat, SkillContext pOriginSkillContext, bool pConsumeAction, Env pEnv, GameRandom pGameRandom)
```

Semantics: entry point for one ability cast. Resolves `abilityConfig.Actions` (from `Abilities.json`), computes
`isCrit` internally (see §6 below), consumes `PrimaryActions`/`SecondaryActions` via `pComponent3.PrimaryActions--`
/`SecondaryActions--` when `pConsumeAction` is true, then calls the private helper `_applyActions(...)` once per
target entity in `targetEntities`, which in turn calls `ApplyAction` once per `(eCombatActions, object)` entry in
the ability's `Actions[]`. Returns the full `combatResults` list (`(eAbilityResults, object)` tuples) — this is
the same result-tuple vocabulary `ApplyAction`/`ApplyStatChange`/`AddHealth` all append to.

### `ApplyAction`

```csharp
// L1871
public static void ApplyAction(Entity pOrigin, Entity pTarget, Entity pPrimaryTarget, List<Entity> pParty, Thing pThing, string pAbilityName, eCombatActions pAction, ref SkillContext pOriginSkillContext, ref SkillContext pTargetSkillContext, object pActionArgs, List<(eAbilityResults, object)> pResults, decimal pAbilityPowerRatio, RollResultData pRollData, Func<Entity, string, eGetStatEquippedFilters, int> pGetStat, Func<Entity, string, int> pGetTileStat, bool pIsCrit, decimal pCritRatio, bool pIsCenterTarget, Env pEnv, GameRandom pGameRandom)
```

Semantics: applies ONE resolved `eCombatActions` entry (`pAction`) from an ability's `Actions[]` against `pTarget`.
`pActionArgs` is the raw `Item2` of that `(eCombatActions, object)` tuple — for `CHANGE_STAT` it's deserialized
elsewhere into `ChangeStatAction`; for `ADD_CHARACTER`/`ADD_CHARACTER_SMOKE`/`ADD_CHARACTER_INSTANT` it's
deserialized here via `JsonHelper.Deserialize<AddCharacterAction>(((JsonElement)pActionArgs).GetRawText())`
(L2143) — see §5. **`pIsCrit` (bool) and `pTarget` (Entity) are both direct parameters** — this is the clean
Harmony hook for `ON_CRIT`/`ON_DAMAGED`/`ON_HEAL` (see §6).

### `SetInitiative`

```csharp
// L43
public static void SetInitiative(Entity pEntity, List<Entity> pAllies, CombatState pCombatState, GameRunData pGameRun, List<(eAbilityResults, object)> pResults, bool pTrySkillProc = false)
```

### `TryCreateSummon`

```csharp
// L112
public static bool TryCreateSummon((int x, int y) pPos, int pGroupIndex, eSummonTypes pSummonType, string pSummonTarget, int pLevel, Env pEnv, GameRandom pRandom, out Entity pSummonEntity, string pGuid = null, Entity pOriginEntity = null, eTileRowPositions pTileRowPosition = eTileRowPositions.ANY)
```

Semantics: single source of truth for spawning a summoned/companion entity into `CombatState`. Branches on
`pSummonType` (`eSummonTypes`): `SPECIFIC` → `pSummonTarget` used verbatim as the character-config id;
`RANDOM`/`AS_FOLLOWER` → `pSummonTarget` used as a **tag** fed to `CharacterHelper.GetActorsByTags` (a weighted
pool pick, not a direct id) or, for `AS_FOLLOWER`, matched against `Env.Configs.Followers[*].ConfigName`;
`PLAYTHING` → `pSummonTarget` fed to `SkillHelper.GetDollTypeEnemies(pSummonTarget, pLevel)` (also a weighted
pool). Only ever creates **one** entity per call — there is no count/repeat parameter anywhere in this method.

### `TickActiveEntityCharacterStatus` / `TickActiveEntityTileStatus`

```csharp
// L954
public static List<(eAbilityResults result, object args)> TickActiveEntityCharacterStatus(Env pEnv, GameRandom pGameRandom, bool pIsStartTurn)
// L893
public static List<(eAbilityResults result, object args)> TickActiveEntityTileStatus(Env pEnv, GameRandom pGameRandom)
```

### Other confirmed signatures in this file (context, not separately requested but load-bearing)

```csharp
// L515
public static bool IsUsableAbility(GameRunData pGameRun, Entity pCharacterEntity, string pAbility, bool pConsiderRemainingActions = true)
// L760
public static bool IsTurnOver(Entity pEntity)
// L787
public static List<(eAbilityResults result, object args)> NextTurn(Env pEnv, GameRandom pGameRandom)
// L793
public static List<(eAbilityResults result, object args)> NextTurn(Env pEnv, GameRandom pGameRandom, out bool pIsNewRound)
// L2586
public static void ResetCharacterActions(List<Entity> pEntities)
// L2594
public static void ResetCharacterActions(Entity pEntity)
// L2616
public static bool TryClearEntityActions(Env pEnv, GameRandom pGameRandom, List<Entity> pEntities, List<(eAbilityResults, object)> pResults)
// L1818 (private — native EVENT_PROC dispatch pipeline for SKILL_* passives)
private static void _onCombatSkillProc(CombatState pCombatState, Entity pCharacter, eSkills pSkill, int pSkillLevel, bool pProcAnim, List<(eAbilityResults, object)> pResults, bool pAddVisuals = true)
```

---

## 2. `InteractableHelper` — damage/status resolution

File: `tools/out/decompile/FTK2/InteractableHelper.cs` (1,808 lines).

### `ApplyStatus` (two overloads)

```csharp
// L1128 — party/self-broadcast overload
public static void ApplyStatus(Entity pOriginEntity, List<Entity> pParty, Thing pThing, string pAbilityName, string pStatusConfigName, GameRandom pGameRandom, List<(eAbilityResults result, object arg)> pResults, bool pTierStatus = true, bool pRenderStatusPopcorn = true)
// L1219 — single-target overload
public static void ApplyStatus(Entity pOriginEntity, Entity pTargetEntity, Thing pThing, string pAbilityName, string pStatusConfigName, GameRandom pGameRandom, List<(eAbilityResults result, object arg)> pResults, bool pTierStatus = true, bool pRenderStatusPopcorn = true, int? pDurationOverride = null)
```

### `ApplyStatChange`

```csharp
// L626
public static void ApplyStatChange(string pAbilityName, ChangeStatAction pStatAction, Entity pOriginEntity, Entity pTargetEntity, List<Entity> pParty, Thing pThing, decimal pAbilityPowerRatio, RollResultData pRollData, Func<Entity, string, int> pGetTileStat, bool pIsCrit, decimal pCritRatio, bool pIsCenterTarget, Env pEnv, GameRandom pGameRandom, ref SkillContext pOriginSkillContext, ref SkillContext pTargetSkillContext, List<(eAbilityResults, object)> pResults)
```

Semantics (L626–L753 read in full): resolves a single `CHANGE_STAT` action's numeric delta. For `Stat == "HP"`
with a positive `statDeltaValue` (damage path): **if `pIsCrit`, the crit bonus is added first** —
`statDeltaValue += (int)Math.Max(1m, Math.Round((decimal)statDeltaValue * pCritRatio))` (L689–692) — *then*
`CalculateFinalDamage` is called to apply defense reduction. So crit inflation happens **inside `ApplyStatChange`,
before `CalculateFinalDamage` is invoked** — `CalculateFinalDamage`'s return value does not itself encode
"was this a crit," it just reflects a pre-inflated `pDamage` if `pIsCrit` was true. A `StatChangedResultsData`
object is built at L661 with `IsCrit = pIsCrit` (verbatim field assignment) and is what ends up in
`pResults` as `(eAbilityResults.STAT_CHANGED, StatChangedResultsData)` — this is the second usable crit signal
(see §6).

### `CalculateFinalDamage`

```csharp
// L1708
public static int CalculateFinalDamage(Entity pCharacterEntity, int pDamage, decimal pPowerRatio, eDamageType pDamageType, bool pBlockable, bool pInanimateTarget)
```

Semantics: applies `pPowerRatio`, then subtracts `DEF` (PHYSICAL) or `RES` (MAGICAL) if `pBlockable`, floors at 0.
**No crit parameter, no crit-related field on the return value** — confirmed NOT a crit signal (see OQ#2 verdict).

### `PerformConsumableAbility`

```csharp
// L538
public static List<(eAbilityResults, object)> PerformConsumableAbility(Entity pOriginEntity, Entity pTargetEntity, List<Entity> pParty, Thing pThing, string pAbilityName, Env pEnv, GameRandom pGameRandom, eConsumableTypes pContext, bool pPlayConsumeAnim = true, bool pDoAbilityAction = true)
```

---

## 3. `CharacterHelper`

File: `tools/out/decompile/FTK2/CharacterHelper.cs` (2,936 lines).

### `AddHealth` (four overloads)

```csharp
// L1330
public static void AddHealth(Entity pEntity, int pValue, List<(eAbilityResults, object)> pResults, bool pConsiderLinkStatus = false)
// L1336
public static int AddHealth(Entity pEntity, int pValue, bool pNonLethal = false)
// L1342
public static void AddHealth(Entity pEntity, ref int pValue, List<(eAbilityResults, object)> pResults, bool pConsiderLinkStatus = false)
// L1357 — the "real" implementation the other three funnel into
public static int AddHealth(Entity pEntity, ref int pValue, bool pNonLethal = false, List<(eAbilityResults, object)> pResults = null, StatChangedResultsData pLinkStatusChangeData = null, bool pAddLinkAbilityResult = false)
```

The L1330 overload appends `(eAbilityResults.STAT_CHANGED, StatChangedResultsData)` to `pResults` itself
(L1353) — same result vocabulary as `ApplyStatChange`.

### `GetStat` (seven overloads — game-code-reference.md undercounted this)

```csharp
// L386
public static int GetStat(Entity pCharacterEntity, eCharacterStats pStat, bool pIgnoreEquipped = false)
// L391
public static int GetStat(Entity pCharacterEntity, eCharacterStats pStat, bool pCappedStat, bool pIgnoreEquipped = false)
// L396
public static int GetStat(Entity pCharacterEntity, string pStat, bool pIgnoreEquipped = false)
// L401
public static int GetStat(Entity pCharacterEntity, eCharacterStats pStat, eGetStatEquippedFilters pEquipFilter)
// L406
public static int GetStat(Entity pCharacterEntity, string pStat, eGetStatEquippedFilters pEquipFilter)
// L411
public static int GetStat(Entity pCharacterEntity, string pStat, bool pCappedStat, bool pIgnoreEquipped = false, bool pIgnoreBaseStatModifiers = false)
// L416
public static int GetStat(Entity pCharacterEntity, string pStat, eGetStatEquippedFilters pEquipFilter, bool pCappedStat = true, bool pIgnoreBaseStatModifiers = false)
// L422 — breakdown overload (returns bonus list too)
public static int GetStat(Entity pCharacterEntity, string pStat, eGetStatEquippedFilters pEquipFilter, out List<(string, int)> pBonuses, bool pCappedStat = true, bool pIgnoreBaseStatModifiers = false)
```

### `InitializePartyStats`

```csharp
// L654
public static void InitializePartyStats(List<Entity> pParty)
```

### Trait bridge surface — `GiveTrait`/`RemoveTrait`/`RemoveAllTraits`/`GetFirstTrait`

```csharp
// L1974
public static string GetFirstTrait(CharacterComponent pCharacterComponent)
// L1984
public static void GiveTrait(Entity pCharacter, string pTraitName)
// L1989
public static void GiveTrait(Entity pCharacter, Thing pTrait)
// L1994
public static void RemoveAllTraits(Entity pCharacter)
// L2002
public static void RemoveTrait(Entity pCharacter, Thing pTrait)
```

Bodies (verbatim logic, L1974–2005):
- `GetFirstTrait` → `InventoryHelper.GetTraits(pCharacterComponent.Things)[0].ConfigName` (or `""`).
- `GiveTrait(Entity, string)` → `GiveTrait(pCharacter, InventoryHelper.CreateThing(pTraitName))`.
- `GiveTrait(Entity, Thing)` → `pCharacter.Get<CharacterComponent>().Things.Add(pTrait)` — literally just an
  inventory add, no enum validation.
- `RemoveAllTraits` → `Things.FindAll(t => t.ConfigName.StartsWith("TRAIT_")).ForEach(t => RemoveTrait(...))`.
- `RemoveTrait` → `InventoryHelper.Take(pTrait, pCharacter)`.
- `InventoryHelper.GetTraits(List<Thing> pThings)` (`InventoryHelper.cs` L1263) →
  `pThings.FindAll((Thing t) => t.ConfigName.StartsWith("TRAIT_"))`.

**None of these four methods reference the `eTraits` enum at all.** A trait is *defined* purely by its
`ConfigName` starting with `"TRAIT_"`; the enum plays no role in grant/remove/query. (Not one of this task's
assigned OQs, but directly answers ClassForge §11.1 — see "Notable surprises" below.)

### `GetEnemiesForCombat` (two overloads)

```csharp
// L2473
public static List<(string name, string tag)> GetEnemiesForCombat(EnemySet pEnemySet, GameRandom pGameRandom, int pLevel, int pThreat, GameRunData pGameRunData, eGetEnemyRules pGetEnemyRule, BiomeDefaultsConfig pBiomeConfig = null)
// L2506
public static List<(string name, string tag)> GetEnemiesForCombat(List<string> pTagQuery, GameRandom pGameRandom, int pLevel, int pThreat, GameRunData pGameRunData, eGetEnemyRules pGetEnemyRule, BiomeDefaultsConfig pBiomeConfig = null, bool pAdditiveToDefault = false)
```

---

## 4. `InventoryHelper`

File: `tools/out/decompile/FTK2/InventoryHelper.cs` (1,627 lines).

```csharp
// L392
public static void Consume(Thing pConsumable, Entity pEntity)
// L806
public static ThingConfig GetThingConfig(string pThingName)
```

`GetThingConfig` body: `return Env.Configs.Things[pThingName];` — direct dictionary index (throws `KeyNotFoundException`
if absent, no null-check) — pack-merged `Things` entries must exist in `Env.Configs.Things` before any code path
that calls `GetThingConfig` runs.

---

## 5. `ADD_CHARACTER` — OQ#4, resolved definitively

### The action-arg payload type

```csharp
// AddCharacterAction.cs (whole file)
public class AddCharacterAction
{
	public eSummonTypes Type;

	public string Value;
}
```

```csharp
// eSummonTypes.cs (whole file)
public enum eSummonTypes
{
	NONE = -1,
	SPECIFIC,
	RANDOM,
	PLAYTHING,
	AS_FOLLOWER
}
```

### Where it's deserialized and consumed

`CombatHelper.cs` L2135–2168 (`ApplyAction`'s `switch (pAction)` for `ADD_CHARACTER`/`ADD_CHARACTER_SMOKE`/
`ADD_CHARACTER_INSTANT`, all three share this case block):

```csharp
// L2143
AddCharacterAction addCharacterAction = JsonHelper.Deserialize<AddCharacterAction>(((JsonElement)pActionArgs).GetRawText());
...
// L2167-2168
string pSummonTarget = addCharacterAction.Value;
eSummonTypes eSummonTypes2 = addCharacterAction.Type;
```

`pSummonTarget`/`eSummonTypes2` then flow straight into `TryCreateSummon(..., eSummonTypes2, pSummonTarget, ...)`
(L2233, `CombatHelper.cs`). Inside `TryCreateSummon` (`CombatHelper.cs` L112–255), the `Value` string's meaning is
**entirely determined by `Type`**:

| `Type` (`eSummonTypes`) | What `Value` (the `pSummonTarget` string) means |
|---|---|
| `SPECIFIC` | The exact `Characters.json` config-name id, used verbatim (`text = pSummonTarget`, L161). |
| `RANDOM` | A **tag** fed to `CharacterHelper.GetActorsByTags(new List<string>{ pSummonTarget }, pLevel)` (L125) — a weighted random pick from all characters carrying that tag, filtered to exclude `TWO_BY_TWO`/`ONE_BY_TWO`/zero-rarity-weight entries when possible. |
| `AS_FOLLOWER` | Same tag-based weighted pick as `RANDOM` (same `case` block, L117-118), but the resulting name is then matched against `Env.Configs.Followers[*].ConfigName` and the entity is built via `CharacterHelper.CreateFollowerCharacter` + `FollowerHelper.JoinFollower` instead of `CharacterHelper.CreateCharacterEntity`. |
| `PLAYTHING` | Fed to `SkillHelper.GetDollTypeEnemies(pSummonTarget, pLevel)` (L165) — a weighted "doll type" pool, not a direct id. |
| `NONE` (-1) | Not handled by the `switch` (no case) — `text` stays `string.Empty`; downstream `CreateCharacterEntity("")` would presumably fail/no-op. Not a valid authoring value. |

**There is no count/quantity field anywhere in `AddCharacterAction` or in `TryCreateSummon`'s signature.**
`TryCreateSummon` creates exactly one entity per call, `out Entity pSummonEntity` (singular). To summon N
creatures, an ability's `Actions[]` must contain N separate `ADD_CHARACTER`-family entries (each independently
rolled/placed) — there is no "spawn 3 skeletons" single-entry primitive.

Two hardcoded special cases exist *before* this generic path is used, both keyed off `pAbilityName` string match
(not relevant to modded content unless a pack reuses those exact ability names): `"DOLL_SUMMON_01"` forces
`eSummonTypes.PLAYTHING` and derives the target from the summoning item's own `ConfigName`; `"SUMMON_HONEYBEE"`
bypasses `TryCreateSummon` entirely and reactivates a specific pre-existing entity by a `"GUID"` custom-data tag.

### VERDICT OQ#4

`AddCharacterAction { eSummonTypes Type; string Value; }`. **`Type` is not "character-config-id vs count" — it
is the `eSummonTypes` enum (`SPECIFIC | RANDOM | PLAYTHING | AS_FOLLOWER`) selecting *how* `Value` is resolved
into a config id, and `Value` is always a single string** (a config id when `Type=SPECIFIC`, a tag/pool key
otherwise). **There is no count field at all** — each `ADD_CHARACTER` action entry spawns exactly one entity.
This **contradicts** `FTK2.ClassForge/SPEC.md` §4.4's placeholder assumption ("`Type` = character config id
string, `Value` = count int") — the spec's example JSON (`{"Type": "CF_SKELETON_WARRIOR", "Value": 1}`) does not
match the real shape. The correct authoring shape for "summon `CF_SKELETON_WARRIOR`" is
`{"Type": "SPECIFIC", "Value": "CF_SKELETON_WARRIOR"}`; "summon N of them" requires N action entries, or the
`SUMMON` recipe effect (§4.6) must itself loop `Count` times and construct one `AddCharacterAction` per iteration
when it calls `CombatHelper.ApplyAction`/`TryCreateSummon`.

---

## 6. Crit determination — OQ#2, resolved definitively

### Where `isCrit` is computed (inside `PerformAbility`, `CombatHelper.cs`)

```csharp
// L1227-1228
bool isCrit = DebugHelper.DebugGuaranteeCrit;
decimal critRatio = 0.15m;
// L1229-1232 — CRTD stat (crit damage bonus) raises critRatio
if (pOrigin.TryGet<CharacterComponent>(out var pComponent2) && pComponent2.CharacterType != eCharacterTypes.NONE && pGetStat != null)
{
    critRatio += (decimal)pGetStat(pOrigin, "CRTD", ...) * 0.01m;
}
```

Two skill-forced crits (`SKILL_JUSTICE` L1301, `SKILL_CALLEDSHOT` L1308 set `isCrit = true` unconditionally), then
the general roll for a normal `HP`-damaging `ENEMY`-targeted `PERFECT`-roll ability:

```csharp
// L1351-1358
else if (pRollData.Status == eRollStatus.PERFECT && abilityConfig.Target == eTargets.ENEMY && abilityConfig.Actions.Any(x => x.Item1 == eCombatActions.CHANGE_STAT))
{
    if (JsonHelper.Deserialize<ChangeStatAction>(...).Stat == "HP")
    {
        int num2 = (targetEntities.Any(x => CoreHelper.HasStatusType(x, eStatusEffectTypes.MARKED)) ? 30 : 0);
        num2 += pGetStat(pOrigin, "CRT", ...);                       // CRT stat, flat percent
        num2 += Math.Max(0, pCombatDecision.FocusUsed) * 5;          // +5% per focus spent
        isCrit = pEnv.GameRun.CombatState.Random.NextChance((decimal)num2 * 0.01m) || DebugHelper.DebugGuaranteeCrit;
    }
    ...
}
```

Crit chance = `CRT` stat (%) + 30 if target has `MARKED` status + 5×`FocusUsed`, rolled via
`CombatState.Random.NextChance(...)` — i.e. the shared deterministic `GameRandom` (MP-safe by construction). Only
computed for `HP`-damaging, `ENEMY`-targeted, `PERFECT`-roll abilities — non-damage or self/ally-targeted
abilities never crit by this path (only the two hardcoded skill-forced cases can force `isCrit=true` for other
ability shapes).

### How `isCrit` reaches a Harmony-patchable call with the target known

`isCrit` (renamed `pIsCrit` at each call boundary) is threaded as an explicit parameter through every downstream
call, target entity always co-present as another parameter:

1. `_applyActions(actionsToApply, pOrigin, item7 /* target */, ..., isCrit || DebugHelper.DebugGuaranteeCrit, critRatio, combatResults, ...)` — `CombatHelper.cs` L1391 (private helper, one call per target in `targetEntities`).
2. → `CombatHelper.ApplyAction(pOrigin, pTarget, ..., pIsCrit, pCritRatio, ..., pEnv, pGameRandom)` — `CombatHelper.cs` L1871 signature (see §1); confirmed call site with `pIsCrit` at L1596 and L2362.
3. → `InteractableHelper.ApplyStatChange(pAbilityName, pStatAction, pOriginEntity, pTargetEntity, ..., pIsCrit, pCritRatio, ..., pEnv, pGameRandom, ...)` — `InteractableHelper.cs` L626 signature; call site `CombatHelper.cs` L2034.
4. Inside `ApplyStatChange`, a `StatChangedResultsData` is constructed with `IsCrit = pIsCrit` (`InteractableHelper.cs` L661-665) and appended to `pResults` as `(eAbilityResults.STAT_CHANGED, StatChangedResultsData)`.

### VERDICT OQ#2

**A confirmed, unambiguous crit signal exists at two independent Harmony-patchable points, both carrying the
target entity in the same call:**

1. **`CombatHelper.ApplyAction` Postfix** — read the `pIsCrit` parameter (6th-from-last positional arg) directly;
   `pTarget` is already a parameter. This is the coarser, ability-Actions-entry-level hook (fires once per
   `eCombatActions` entry, so once per `CHANGE_STAT` on a given target).
2. **`InteractableHelper.ApplyStatChange` Postfix** — same `pIsCrit`/`pTargetEntity` parameters, one level lower
   (fires specifically for `CHANGE_STAT` resolution, guaranteed HP-stat-change granularity) — **this is the
   better hook for `ON_CRIT`** because it's the narrowest point that still has both signals as plain parameters,
   with no need to inspect `CalculateFinalDamage`'s return (confirmed to carry no crit information, §2).

`CalculateFinalDamage` (SPEC §6's "candidate source, conditional on §11.2") is **ruled out**: it has no crit
parameter and its return value is computed identically whether or not the damage was already crit-inflated
upstream — crit inflation happens in `ApplyStatChange` *before* `CalculateFinalDamage` is called (§2 above).
ClassForge's `ON_CRIT` recipe trigger should Postfix-hook `InteractableHelper.ApplyStatChange`, read `pIsCrit`
and `pTargetEntity` from the patch's own parameters (no reflection into private state needed), and fire only
when `pIsCrit == true && pStatAction.Stat == "HP"`.

---

## 7. `ConfigsHelper` — boot-time config load path (OQ#3)

File: `tools/out/decompile/FTK2/ConfigsHelper.cs` (761 lines).

### Load entry points

```csharp
// L10 (private const, NOT a field/dictionary — corrects game-code-reference.md, see bottom)
private const string CONFIGS_JSON_SOURCES = "Configs/JSON~";

// L383
public static Configs LoadConfigs(string basePath)
{
    Configs configs = CreateConfigs();
    string text = Path.Join(basePath, "Configs/JSON~");
    try
    {
        ReadJsonConfigs(text, configs);
        return configs;
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException("Failed to load configs from '" + text + "': " + ex.Message, ex);
    }
}

// L398
public static void ReloadConfigs(ref Configs configs, string basePath)
{
    configs = CreateConfigs();
    ItemMaterialHelper.ResetCache();
    ReadJsonConfigs(Path.Join(basePath, "Configs/JSON~"), configs);
    LootDropHelper.ResetClassGroups();
}
```

Both build a **brand-new `Configs` object from scratch** (`CreateConfigs()`, L406, populates ~57
`SerializedSortedDictionary<...>` fields, all empty) and then walk exactly one directory tree,
`<basePath>/Configs/JSON~`, via `ReadJsonConfigs` (L468) → `ProcessFileSystemInfo` (L489, private) →
`ProcessDirectory`/`ProcessJsonFile` (L501/L518, both private). **Neither `LoadConfigs` nor `ReloadConfigs` takes
any parameter for extra source directories** — there is no built-in multi-root config loading; `basePath` is a
single root whose `Configs/JSON~` subfolder is walked recursively.

```csharp
// L501 (private)
private static void ProcessDirectory(DirectoryInfo directoryInfo, Configs configs)
{
    try
    {
        foreach (FileInfo item in from f in GetFiles(directoryInfo.FullName, recursive: true)
            where f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            select f)
        {
            ProcessJsonFile(item.Name, item.FullName, configs, directoryInfo.Name);
        }
    }
    catch (Exception ex)
    {
        Debug.LogError("Failed to process directory " + directoryInfo.FullName + ". " + ex.Message);
    }
}
```

`ProcessDirectory`/`ProcessJsonFile` are `private static` but take an already-built `Configs configs` instance as
a parameter and mutate it in place (dictionary `TryAdd`/assignment inside the various `ConfigParsers`/
`SingletonParsers` delegates, L12–381) — they don't read from any static/global state besides their arguments, so
they're trivially reflection-callable (`AccessTools.Method` + `Invoke`) against the game's live `Env.Configs`
instance from a Harmony postfix, pointed at an arbitrary mod directory whose subfolder layout mirrors
`Configs/JSON~/<Category>/*.json` (dictionary key = the JSON parent directory name, resolved via `ConfigParsers`/
`ParseThingsConfig`/`ParseAbilitiesConfig`/etc., or the file's own name via `SingletonParsers` for singleton
config types).

`ConfigParsers` (L12–66, `Dictionary<string, Action<string, string, Configs>>`, keyed by parent-directory name)
and `SingletonParsers` (L68+, `Dictionary<string, Action<string, Configs>>`, keyed by file name) are both
`private static readonly` — not runtime-extensible without reflection, but their **key set is fixed and small**
(`Adventures`, `Langs`, `Quests`, `DialogueLangs`, `TutorialLangs`, `Dialogues`, `Dungeons`, `MegaHexTemplates`,
`Things`, `VenueGrids` for `ConfigParsers`; `MegaHexTemplateCollections` + others for `SingletonParsers` — full
enumeration not required for this task, `Things` is the one ClassForge cares about via `ParseThingsConfig`
L614). Note `Characters` and `Abilities` are **not** in the `ConfigParsers` dictionary shown at L12-66 — they
parse via a different path not captured in this excerpt (the dictionary shown only lists 10 keys); ClassForge
should not assume `Characters`/`Abilities` route through `ConfigParsers` without further inspection (flagged as
unresolved sub-detail, see NOT FOUND).

### VERDICT OQ#3

`ConfigsHelper.LoadConfigs(string basePath)` and `ConfigsHelper.ReloadConfigs(ref Configs configs, string
basePath)` are the two boot/hot-reload entry points; **neither accepts additional directories** — the native
loader is single-root (`<basePath>/Configs/JSON~`), so packs **cannot** be picked up by pointing the native
loader at extra folders. ClassForge's plan (SPEC §6: Postfix on `LoadConfigs`/`ReloadConfigs`, merge into the
returned/ref `Configs` object's dictionaries) is confirmed as the only viable approach — a Postfix runs after
`CreateConfigs()` + `ReadJsonConfigs()` have fully populated the fresh `Configs` instance, which is early enough
to merge into before any other system reads `Env.Configs` (assuming — not verified here — that the assignment of
`LoadConfigs`'s return value to `Env.Configs` happens strictly after `LoadConfigs` returns, which is the normal
Harmony Postfix guarantee since Postfixes run before the caller resumes). `ReloadConfigs`'s "idempotency"
(SPEC OQ#8) is separately unverified — each call still constructs an entirely fresh `Configs` via `CreateConfigs()`
(L400), so a merge Postfix re-running is not adding to stale state, but whether other systems hold stale
references to the pre-reload `Configs` object is out of scope for this file alone.

---

## 8. UI / localization / asset targets

File: `tools/out/decompile/FTK2/Lang.cs` (348 lines).

```csharp
// L91
public static string __t(DynamicTagCacheEntry pFormatKey, List<DynamicTagCacheEntry> pArgs)
// L105 — the "8-arg" overload named in the task (7 params + implicit this doesn't apply; counts as 7 explicit params, but matches the commonly-cited "__t(8 args incl. defaults)" description)
public static string __t(string pFormatKey, object pArg0 = null, object pArg1 = null, object pArg2 = null, object pArg3 = null, object pArg4 = null, object pArg5 = null, bool pKeyToLowerCase = false)
// L111
public static string __t(string pFormatKey, out bool pSuccess, object pArg0 = null, object pArg1 = null, object pArg2 = null, object pArg3 = null, object pArg4 = null, object pArg5 = null, bool pKeyToLowerCase = false, bool pTrim = true)
// L249
public static void SetLanguage(string pISO)
```

Note: the L105 overload has 8 formal parameters (`pFormatKey` + `pArg0..pArg5` + `pKeyToLowerCase`) — this matches
the task's "8-arg" description exactly.

File: `tools/out/decompile/FTK2/AssetLoader.cs` (539 lines).

```csharp
// L327
public static Texture2D GetImage(object pIconID, eTextureAtlas pAtlas)
// L333
public static Texture2D GetImage(object pIconID, eTextureAtlas pAtlas, out Color pColor)
// L338
public static Texture2D GetImage(string pIconName)
// L344
public static Texture2D GetImage(string pIconName, out Color pColor, bool pCheckWithContains = false)
// L379
public static Texture2D GetRender(Thing pThing)
// L394
public static Texture2D GetRender(Entity pEntity)
// L404
public static Texture2D GetRender(string pKey, bool pAllowPrerender = true, bool pIsDialogue = false)
```

`GetImage`/`GetRender` each have **more overloads than game-code-reference.md's single-signature listing implied**
(4 and 3 respectively) — a Prefix patch intending to serve pack icons/portraits needs to consider which
overload(s) are actually called from the UI code paths it cares about (`RenderClassList` calls the
`GetImage(object, eTextureAtlas, out Color)` overload specifically — see below).

File: `tools/out/decompile/FTK2/CharacterCustomizationViewHelper.cs` (1,837 lines).

```csharp
// L1543
public static async void RenderClassList(Entity pEntity, List<string> pPlayableCharacters, Func<Entity, string, bool, bool, Task> pOnChangeClass, VisualElement pItemCard)
// L291
public static void RenderCustomizationContainer(InputPlayer pInputPlayer, int pEntityIndex, Entity pEntity, Dictionary<string, int> pUserStats, List<string> pPlayableCharacters, Action<string, bool> pOnChangeName, Func<Entity, string, bool, bool, Task> pOnChangeClass, Action<Entity, string, bool, bool> pOnChangeBodyType, Action<Entity, eSkinCosmetics, string, bool, bool> pOnChangeSkinCosmetic, Action<Entity, eActorTintType, string, bool, bool, int> pOnChangeColor, Action<Entity, bool, bool> pOnRotate, Func<Entity, bool, Task> pOnRandomize, Action<bool> pOnChangeCharacter, Func<Entity, CharacterPresetSaveData, Task> pOnLoadPreset, Action<Entity, bool, bool> pOnSetHideEquipment, VisualElement pItemCard)
// L1001
public static void RenderStatsContainer(Entity pEntity, string pConfigName, VisualElement pItemCard)
// L1220
public static void RenderListSelector(List<(string value, string label, bool isEnabled)> pListSource, string pCurrentlySelected, Button pSourceButton, string pTitle, Action<string> pOnChangeCallback)
```

`RenderClassList` body (L1543–L1650+) confirms the injection point: it calls
`UIToolkitHelper.RenderListView(presetsListView, ..., pPlayableCharacters, <row-render delegate>, ...)` where
`pPlayableCharacters` is the exact `List<string>` of class config-name ids to display — a Postfix that appends
pack `PLAYER`-tagged class ids to this list **before** `RenderClassList` runs (i.e. a Prefix that mutates
`pPlayableCharacters` in place, since it's a `List<string>` reference and `RenderClassList` doesn't rebuild it) is
the correct patch shape — confirms SPEC §6's plan, refines "Postfix (or transpiler)" down to "**mutate the
`pPlayableCharacters` list via Prefix**" as the simplest viable approach, no transpiler needed since the method
receives the list as an argument rather than constructing it from a closed static source.

Each row's icon comes from `AssetLoader.GetImage(_pClassConfig, eTextureAtlas.Class, out var pColor)` (L1575) —
confirms the icon-fallback patch should target the `GetImage(object, eTextureAtlas, out Color)` overload
specifically for class icons.

---

## 9. `GameRunData` shape

File: `tools/out/decompile/FTK2/GameRunData.cs` (180 lines).

```csharp
public Dictionary<string, int> Stats;
```

`Stats` is `Dictionary<string, int>` — a flat string-keyed integer bag, exactly matching EOR's usage pattern
(`EOR_RISKY_BLESSING_*`, `EOR_REVENGE_*` custom keys). No further structure/typing — any mod can stash an
arbitrary `int` under an arbitrary string key. Persistence: `GameRunData` itself has no `[JsonIgnore]` on `Stats`
(unlike `CombatState`, `CharacterNomenclatorMap`, `DebugRoadData`, which are `[JsonIgnore]`) — `Stats` round-trips
through the game's `System.Text.Json`-based save serialization automatically. Replication across peers is not
determinable from this file alone (not investigated further — out of scope; flagged under NOT FOUND).

```csharp
// L101
public static GameRunData Create(string pGameVersion, string pAdventureConfig, List<eExpansions> pExpansions = null, eGameDifficulties pGameDifficulty = eGameDifficulties.APPRENTICE, Dictionary<eGameDifficultyHandles, int> pHouseRules = null)
```

Throws if `pAdventureConfig` isn't a valid key in `Env.Configs.Adventures`. Builds a fresh `GameRunData` with
empty `Stats = new Dictionary<string, int>()` (implicitly, via `CreateConfigs`-style field init — actually `Stats`
is set via the object initializer at L107-127, confirmed present: `Stats = new Dictionary<string, int>()`).
`ItemPools["CURRENCY_LORE"] = 0` seeded explicitly; `Stats` starts genuinely empty. `SkillCoolDown` is a separate
`Dictionary<eSkills, int>` (enum-keyed, not string-keyed) — distinct from `Stats`, don't conflate the two when
authoring skill-recipe cooldowns.

`Entities` is a computed property (`[JsonIgnore] public List<Entity> Entities => _entities;`) backed by a
`[JsonInclude][JsonPropertyName("Entities")] private List<Entity> _entities` — the actual JSON-serialized field is
the private backing field via the explicit `JsonPropertyName` attribute, not the public property (which is
`[JsonIgnore]`'d to avoid the serializer trying to use the getter-only property directly). Mutation only through
`SetEntities(List<Entity> pEntities)` (L149).

---

## 10. Combat RNG

File: `tools/out/decompile/FTK2/CombatState.cs` (82 lines) confirms:

```csharp
public GameRandom Random;
```

`CombatState.Random` is typed `GameRandom` (not `System.Random`/`UnityEngine.Random`) — confirms
`docs/research/eor-0760-content-audit.md`'s claim of deterministic-lockstep shared RNG for combat.

File: `tools/out/decompile/FTK2/GameRandom.cs` (362 lines) — full API surface (public members only):

```csharp
public readonly int Seed;
public bool RecordDebugInfo;
public int NextCount { get; }
public void LogCalls(bool pDoLog);
public GameRandom();                                                     // seeds from NetworkDebuggingHelper.MultiplayerSeed if online-MP, else GetNewRandomSeed()
public GameRandom(int _seed, bool pIgnoreMultiplayerStaticSeed = false); // same MP-seed override unless pIgnoreMultiplayerStaticSeed
public static int GetNewRandomSeed();                                    // new System.Random().Next(0, 1000000)
public int NextInt(int _max, bool pMaxInclusive = false);
public int NextInt(int _min, int _max, bool pMaxInclusive = false);
public float NextFloatVisual(float _min, float _max);
public decimal NextNormalizedDecimal();
public decimal NextDecimal();
public decimal NextDecimal(decimal _min, decimal _max);
public float NextNormalizedFloatVisual();
public float NextFloatVisual();
public bool NextBool();
public static decimal GetChanceValue(ChanceGroup pChance);               // Always=1, VeryCommon=.8, Common=.66, Average=.5, Uncommon=.33, Rare=.15, VeryRare=.05, Never=0
public bool NextChance(ChanceGroup pChanceGroup);
public bool NextChanceVisual(float _chance);
public bool NextChance(decimal _chance);                                 // <- the one ProcChance/AiProcChance rolls should use
public int NextIntDescendingWeight(int _max);
public int NextIntAscendingWeight(int _max);
public void ShuffleList<T>(List<T> _list);
public T GetRandomElementFromList<T>(List<T> _list);
public T GetRandomElementFromArray<T>(T[] pArray);
public TKey GetRandomKeyFromDictionary<TKey, TValue>(IDictionary<TKey, TValue> _dict);
public TValue GetRandomValueFromDictionary<TKey, TValue>(IDictionary<TKey, TValue> _dict);
public T GetRandomElementFromWeightedList<T>(WeightedList<T> _list);
public Vector3 NextVector3(Vector3 _min, Vector3 _max);
public float NextNormalizedFloatDistributed(AnimationCurve pDistributionCurve);
public Vector3 NextOffset3(Vector3 _max);
public Vector3 NextPointInSphere(Vector3 pExtents);
public Vector3 NextPointInUnitSphere(float pRadius);
public Vector3 NextPointInUnitSphere();
public void Log(string pText);          // [Conditional("CONSOLE_ENABLED")] — no-op in shipped build
public void WriteLogToFile();           // empty body
```

Backed by `private readonly System.Random random` — a single seeded PRNG instance per `GameRandom`, seed captured
in the public `Seed` field. Every `Next*` method funnels through this one `random` instance, so call **order**
and call **count** are exactly what must stay identical across peers for determinism (confirms
`FTK2.ClassForge/SPEC.md` §9.3b's Design A/B analysis — every recipe roll must go through
`CombatState.Random.NextChance(decimal)` or equivalent, never a fresh/local `System.Random`).

`eCombatActions` enum (`eCombatActions.cs`, whole file — **differs from game-code-reference.md**, see corrections
below):

```csharp
public enum eCombatActions
{
	MOVE,
	VOIDWALK,
	CHANGE_STAT,
	ADD_STATUS,
	REMOVE_STATUS,
	ADD_CHARACTER,
	ADD_CHARACTER_SMOKE,
	ADD_CHARACTER_INSTANT,
	FLEE,
	REVIVE_ALLY,
	REVIVE_CHARACTER,
	EQUIP_WEAPON,
	GRAB,
	VEHICLE_DAMAGE,
	VEHICLE_REPAIR
}
```

---

## Notable surprises (not directly asked for, but load-bearing for the engines)

1. **`eTraits` enum appears to be dead/vestigial code.** A repo-wide search for the exact token `eTraits` (word
   boundary, to exclude substring false-positives like `IncludeTraits`) across the entire decompile tree finds
   **only its own declaration** (`eTraits.cs`) — zero usages anywhere else in `FTK2.dll`, including inside
   `CharacterHelper.GiveTrait/RemoveTrait/RemoveAllTraits/GetFirstTrait` and `CombatHelper.RecalculateLoneWolf`
   (both of which the SPEC and game-code-reference.md describe as "consuming" `eTraits`). All four
   `CharacterHelper` trait methods operate purely on `Thing.ConfigName.StartsWith("TRAIT_")` — no enum check, no
   enum-to-string mapping, nothing. **This strongly suggests ClassForge's OQ#1 (the "eTraits enum bridge
   mechanism") may not need a bridge at all** — any `Thing` with `Class:"TRAIT"` and a `ConfigName` starting with
   `"TRAIT_"` should already be fully grantable/removable/queryable through the native API with zero patching.
   This wasn't one of this task's three assigned open questions, but it's a materially different answer than
   SPEC §11.1 assumes, and is worth a follow-up pass focused specifically on `RecalculateLoneWolf` and any
   trait-pick UI code before M2 is scoped.
2. **`AddCharacterAction` has no count field at all** (§5) — this changes the `SUMMON` recipe effect's
   implementation from "pass `Count` straight through" to "loop `Count` times, calling `ApplyAction`/
   `TryCreateSummon` once per iteration" — a real implementation-shape change for ClassForge §4.6.
3. **Crit inflation happens upstream of `CalculateFinalDamage`**, inside `ApplyStatChange` — `CalculateFinalDamage`
   was SPEC §6's crit-hook candidate and turns out to carry zero crit information. `ApplyStatChange` (not
   `ApplyAction`) is the tightest correct hook.
4. **`Lang.__t`'s "8-arg" overload is real and exact** — 8 formal parameters at `Lang.cs` L105, matching the task
   brief's description precisely.
5. **`ConfigsHelper.LoadConfigs`/`ReloadConfigs` rebuild `Configs` from scratch every call** (`CreateConfigs()`)
   — a pack-merge Postfix is therefore safe to assume it always runs against a fully-vanilla-populated `Configs`
   object with no leftover pack data from a previous load, which directly informs (but does not fully resolve —
   see NOT FOUND) SPEC's OQ#8 hot-reload-idempotency question.
6. **`GetStat` has 8 overloads, not "one method"** — game-code-reference.md's terse listing undersold the surface;
   recipe-condition authors (`HP_THRESHOLD` etc., SPEC §4.6) should standardize on one specific overload
   (`GetStat(Entity, string, bool)` at L396, or the `eGetStatEquippedFilters` one at L406) for consistency, and
   the engine should document which.
7. **`AssetLoader.GetImage`/`GetRender` have more overloads than previously catalogued** (4 and 3 respectively);
   `RenderClassList`'s class-icon row specifically calls `GetImage(object, eTextureAtlas, out Color)` — that's the
   one overload ClassForge's icon-fallback Prefix must intercept for class portraits in the select list.

## Corrections to `docs/research/game-code-reference.md`

- §4: `CONFIGS_JSON_SOURCES` is a `private const string = "Configs/JSON~"`, **not** a field alongside
  `ConfigParsers`/`SingletonParsers` in the sense of being data-driven/inspectable — it's a single hardcoded
  relative-path literal, and both `ConfigParsers` and `SingletonParsers` are `private static readonly`
  dictionaries with a fixed, small key set (10 keys visible for `ConfigParsers`: `Adventures, Langs, Quests,
  DialogueLangs, TutorialLangs, Dialogues, Dungeons, MegaHexTemplates, Things, VenueGrids`), not the "~57 fields"
  scale implied by proximity to the `Configs` class description in the same doc (that 57-field count is correct
  for `Configs`, the *registry* class, not `ConfigParsers`, the *parser dispatch table* — the original doc's
  wording conflates the two but does not misstate either number individually; flagging for clarity only).
- §2/§5: `eCombatActions` is confirmed to have exactly 15 values: `MOVE, VOIDWALK, CHANGE_STAT, ADD_STATUS,
  REMOVE_STATUS, ADD_CHARACTER, ADD_CHARACTER_SMOKE, ADD_CHARACTER_INSTANT, FLEE, REVIVE_ALLY, REVIVE_CHARACTER,
  EQUIP_WEAPON, GRAB, VEHICLE_DAMAGE, VEHICLE_REPAIR`. game-code-reference.md's partial listing
  ("`CHANGE_STAT, ADD_STATUS, REMOVE_STATUS, ADD_CHARACTER, ADD_CHARACTER_SMOKE, ADD_CHARACTER_INSTANT,
  REVIVE_ALLY, MOVE, FLEE, VOIDWALK, VEHICLE_DAMAGE, VEHICLE_REPAIR`") omitted `REVIVE_CHARACTER`, `EQUIP_WEAPON`,
  and `GRAB` — not wrong, just incomplete; adding the full list here for completeness.

## NOT FOUND

- ~~Whether `Characters`/`Abilities` route through `ConfigParsers` or a separate path~~ — **resolved during this
  pass, not actually a gap.** `ProcessJsonFile` (`ConfigsHelper.cs` L518–540) tries `ConfigParsers` first, keyed
  by **parent directory name** (`parentName`); if that misses, it falls back to `SingletonParsers`, keyed by
  **filename without extension** (`fileNameWithoutExtension`). `SingletonParsers` (L68 onward) includes
  `["Abilities"] = ParseAbilitiesConfig` (L154) and `["Characters"] = delegate { SafeParseSingletonConfig(...) }`
  (L162) — so `Characters.json`/`Abilities.json` are matched by **filename**, not folder, via the singleton path.
  This means a pack's `classes.json`/`abilities.json` files, if ever routed through the *native* loader instead
  of ClassForge's own Postfix merge, would need to literally be named `Characters.json`/`Abilities.json` (one
  file per pack, not per-pack-arbitrary-name) to hit this dispatch — another point in favor of ClassForge's
  current plan (Postfix-merge into the already-parsed `Configs` dictionaries directly, bypassing
  `ConfigParsers`/`SingletonParsers`/`ProcessJsonFile` entirely) over trying to make packs "native-loader-shaped."
- **Whether `GameRunData.Stats` values replicate across multiplayer peers**, and by what mechanism (host push,
  full-state sync, or none). Tried: reading all of `GameRunData.cs` (180 lines, whole file) — no networking code
  present in this file; would require tracing `NetworkDebuggingHelper`/`AdventureDirector._handleNetworkAction` or
  equivalent, out of scope for this pass (flagged for `docs/MULTIPLAYER.md`'s open-questions list, not
  ClassForge's).
- **The full `SingletonParsers` dictionary contents** (`ConfigsHelper.cs` L68 onward) — only the first key
  (`MegaHexTemplateCollections`) was read in this pass; the remainder (up to L380) wasn't transcribed since it
  wasn't required to answer OQ#3 (the load-path question is answered regardless of every singleton key).
- **A dedicated `eAbilityResults` value for "critical hit."** Checked `eAbilityResults.cs` for any token
  containing `CRIT` — none found. Confirms the *only* crit signal is the `bool pIsCrit` parameter thread and the
  `StatChangedResultsData.IsCrit` field (§6) — there is no separate discrete "crit occurred" result entry in the
  `pResults` tuple stream a patch could pattern-match on without also reading the `IsCrit` field/parameter
  directly.
