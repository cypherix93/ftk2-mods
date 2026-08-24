# Coverage map: Items, Equipment, Abilities

Purpose: a single reference for how FTK2 things, equipment slots, and combat abilities fit
together, so the team stops re-deriving this feature by feature. Every claim below is backed
by a decompiled C# quote or a shipped JSON quote, with file:line.

Decompile command used throughout:
```
DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/tools/ilspycmd.exe --disable-updatecheck -t <Type> \
  -r "C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed" \
  "<ManagedDir>/FTK2.dll"
```
JSON root: `C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\StreamingAssets\Assets\Configs\JSON~\`
(`Abilities.json` at the root; per-category thing files under `Things\`, e.g. `Weapons.json`,
`Items.json`, `Attires.json`, `Traits.json`, plus curated/EOR/forge catalogs).

File:line citations below refer to the decompiled `.cs` file named after the type (e.g.
`CombatHelper.cs:2854`), matching this repo's existing decompile-citation convention.

---

## THING_ANATOMY

### `ThingConfig` — every field, verbatim

```csharp
public class ThingConfig
{
    public eConsumableTypes ConsumableType;
    public int Value;
    public float MinTier;
    public float MaxTier;
    public string Class;
    public eItemRarities Rarity;
    public eItemMaterialFamilies Material;
    public bool Hidden;
    public bool Stacks;
    public int Ammo;
    public Equippable Equippable;
    public Interactable Interactable;
    public List<string> Tags;
    public eExpansions Expansion;
}
```
`ThingConfig.cs:1-32`

Field notes:
- `ConsumableType` (`eConsumableTypes`: `NONE, COMBAT, OVERWORLD, ANY, EITHER, TRIGGER`,
  `eConsumableTypes.cs`) — gates *when* the thing can be "used" as a consumable. See
  `InventoryHelper.IsConsumableItem` below.
- `MinTier`/`MaxTier` are `float`, not `int` — the loot-tier roll (`LootDropHelper`) clamps
  into this range.
- `Class` is a free string (e.g. `"AXE_2H"`, `"ALCOHOL"`), not an enum — no compile-time
  validation, a mod can put any string here.
- `Rarity` — `eItemRarities`: `NONE=-1, COMMON, UNCOMMON, RARE, ARTIFACT, QUEST, LORE, SKIN,
  LOCKED, MERCENARY, CHARACTER, LOCATION, MYSTERY, SEASONAL, PREMIUM` (`eItemRarities.cs`).
- `Material` — `eItemMaterialFamilies`: `NONE, WOOD, METAL, GLASS, CLOTH, LEATHER`
  (`eItemMaterialFamilies.cs`).
- `Stacks` / `Ammo` are plain fields read by inventory/combat code directly (e.g. ammo
  depletion checks `CustomData["AMMO"] < Env.Configs.Things[configName].Ammo`, see
  `CombatHelper.cs:466-467`).
- `Tags` is a free `List<string>` — many behaviors key off literal tag strings (e.g.
  `"CANNOT_UNEQUIP"`, `"FIXED"`, `"HIDE_TOOLBAR"`, `"TWO_BY_TWO"`) rather than dedicated
  bool fields; see MOD_CAPABILITIES.
- `Expansion` — `eExpansions` enum, used for DLC gating, not otherwise relevant here.

### `Equippable` block

```csharp
public class Equippable
{
    public eEquipmentSlots[] Slots;
    public SerializedSortedDictionary<string, int> Stats;
    public List<string> Passives;
    public List<AbilityTrigger> AbilityTriggers;
    public int MaxCharges;
}
```
`Equippable.cs:1-14`

- `Slots` — which `eEquipmentSlots` this thing occupies when equipped (can be multiple, e.g.
  two-handed weapons occupy `MAIN_HAND` + `OFF_HAND`).
- `Stats` — flat stat deltas (`ATK`, etc.) applied on equip via
  `EquipmentHelper._addEquippablePartyStats` (party-only, gated on `PlayerComponent`; see
  EQUIPMENT section).
- `Passives` — string ids resolved through `CharacterHelper.ValidPartyPassives` on equip
  (`EquipmentHelper.cs:88-97`).
- `MaxCharges` — used by chargeable weapons; `-1` means "not chargeable" (every weapon
  example below has `-1` unless it's a torch/charge item, e.g. `BLUNT_TORCH_LIGHT_00` has
  `"MaxCharges": 1`, confirmed by direct JSON read).

### `Interactable` block

```csharp
public class Interactable
{
    public Dictionary<string, SkillRollData> Abilities;
    public List<string> AbilityBag;
    public bool ShuffleAbilityBag;
    public List<string> AbilityFillBag;
}
```
`Interactable.cs:1-12`

- `Abilities` — dictionary of ability-name → `SkillRollData` (a per-thing *roll modifier*
  override, not the ability's full definition — the full definition lives in
  `Env.Configs.Abilities[abilityName]`, i.e. `Abilities.json`, parsed into
  `CombatAbilityConfig`). `SkillRollData`:
  ```csharp
  public class SkillRollData
  {
      public decimal MinValue;
      public decimal MaxValue;
      public int ACC;
      public string Stat;
      public int Rolls;
      public int Ammo;
  }
  ```
  `SkillRollData.cs:1-10`. A dictionary value of `null` (as seen on simple consumables below)
  means "use the ability's own defaults, no per-thing roll override."
- `AbilityBag` / `ShuffleAbilityBag` / `AbilityFillBag` — enemy-AI-only mechanism
  (`AbilityBag`) and juggle-weapon-only mechanism (`AbilityFillBag`); see EQUIPMENT and
  ABILITY_MENU sections.

### Real weapon example — `AXE_MILITIA_HEAVY_00` (`Things/Weapons.json`)

```json
{
  "ConsumableType": "NONE",
  "Value": 24,
  "MinTier": 0,
  "MaxTier": 0,
  "Class": "AXE_2H",
  "Rarity": "COMMON",
  "Material": "METAL",
  "Hidden": false,
  "Stacks": false,
  "Ammo": 0,
  "Equippable": {
    "Slots": ["MAIN_HAND", "OFF_HAND"],
    "Stats": { "ATK": 14 },
    "MaxCharges": -1
  },
  "Interactable": {
    "Abilities": {
      "AXE_BASIC_ATTACK": { "MinValue": 0.5, "MaxValue": 1, "ACC": 0, "Stat": "STR", "Rolls": 5, "Ammo": 0 },
      "AXE_HEAVY_BLEED_ATTACK": { "MinValue": 0, "MaxValue": 1.25, "ACC": -15, "Stat": "STR", "Rolls": 5, "Ammo": 0 }
    },
    "AbilityBag": [],
    "ShuffleAbilityBag": true
  },
  "Tags": ["PHYSICAL", "BLEED", "AXE_MILITIA_HEAVY", "COMMON", "WEAPON", "HAND_EQUIP",
           "WEAPON_TWO_HAND", "AXE", "MELEE", "MILITIA", "DROPPABLE", "TOWN_MARKET",
           "DUNGEON_MARKET", "HEAVY"],
  "Expansion": "BASE"
}
```
Source: `Things/Weapons.json`, key `AXE_MILITIA_HEAVY_00`.

A weapon with a `Passives` entry — `BOOK_MILITIA_BASIC_00`:
`"Equippable": {"Slots": ["MAIN_HAND","OFF_HAND"], "Stats": {"ATK": 10}, "Passives": ["SKILL_SUPPORTRANGE"], "MaxCharges": -1}`
(`Things/Weapons.json`, key `BOOK_MILITIA_BASIC_00`).

### Real consumable example — `DRINK_HILDEBRANT_01` (`Things/Items.json`)

```json
{
  "ConsumableType": "COMBAT",
  "Value": 10,
  "MinTier": 0,
  "MaxTier": 1,
  "Class": "ALCOHOL",
  "Rarity": "UNCOMMON",
  "Material": "NONE",
  "Hidden": false,
  "Stacks": true,
  "Ammo": 0,
  "Interactable": {
    "Abilities": { "DRINK_HILDEBRANT_01": null },
    "ShuffleAbilityBag": false
  },
  "Tags": ["UNCOMMON", "ALCOHOL", "DROPPABLE", "TOWN_MARKET", "DUNGEON_MARKET", "USEABLE", "LOOTABLE"],
  "Expansion": "BASE"
}
```
Source: `Things/Items.json`, key `DRINK_HILDEBRANT_01`. Note it has no `Equippable` block at
all (not equippable), a single `Interactable.Abilities` entry sharing its own config name, and
`null` for the `SkillRollData` value (no per-thing roll override — the ability's own
`Abilities.json` entry supplies everything).

---

## EQUIPMENT

### Slots — `eEquipmentSlots` (verbatim, 9 values + NONE)

```csharp
public enum eEquipmentSlots
{
    NONE = -1,
    MAIN_HAND, OFF_HAND, HELMET, ARMOR, GLOVES, BOOTS, TRINKET, BACKPACK, PIPE
}
```
`eEquipmentSlots.cs:1-13`

### Equip / Unequip — `EquipmentHelper`

```csharp
public static void Equip(Thing pThing, Entity pCharacterEntity)
{
    CharacterComponent characterComponent = pCharacterEntity.Get<CharacterComponent>();
    if (!characterComponent.Things.Any((Thing thing) => thing.Id == pThing.Id))
        throw new Exception("Cannot equip thing that is not in your inventory");
    if (!InventoryHelper.HasEquippable(pThing.ConfigName))
        throw new Exception("Cannot equip thing without an Equippable config");
    Equippable equippable = InventoryHelper.GetEquippable(pThing.ConfigName);
    if (equippable.Slots != null && equippable.Slots.Length != 0)
    {
        eEquipmentSlots[] slots = equippable.Slots;
        foreach (eEquipmentSlots slot in slots)
        {
            if (!string.IsNullOrEmpty(characterComponent.Equipped[slot]) && characterComponent.Equipped[slot] != pThing.Id)
                Unequip(characterComponent.Things.Find((Thing t) => t.Id == characterComponent.Equipped[slot]), pCharacterEntity, pReconsiderVitals: false);
            characterComponent.Equipped[slot] = pThing.Id;
        }
    }
    if (pCharacterEntity.Has<PlayerComponent>())
        _addEquippablePartyStats(equippable, pAdd: true);
    CharacterHelper.ReconsiderVitals(pCharacterEntity);
}
```
`EquipmentHelper.cs:41-66`

Key facts:
- Equipping a thing requires it to already be in `CharacterComponent.Things` (your
  inventory) — `Equip` never gives you the item, it just moves it into equipped slots.
  Whichever thing currently occupies each of the new item's `Slots` is auto-unequipped first
  (a two-handed weapon evicts both hands; a one-handed weapon evicts only `MAIN_HAND`, etc.).
- `characterComponent.Equipped` is a `Dictionary<eEquipmentSlots,string>` of slot → equipped
  thing `Id` (not `ConfigName`) — each slot holds exactly one equipped instance.
- Party-wide stat/passive bonuses (`_addEquippablePartyStats`) are applied **only** if the
  entity `Has<PlayerComponent>()` — companions/enemies equip things without touching party
  stats.
- `Unequip` mirrors this: clears `Equipped[slot]` for every slot in `equippable.Slots`, and
  if `pReconsiderVitals` reruns `CharacterHelper.ReconsiderVitals`.
  ```csharp
  public static void Unequip(Thing pThing, Entity pCharacterEntity, bool pReconsiderVitals = true)
  {
      CharacterComponent characterComponent = pCharacterEntity.Get<CharacterComponent>();
      Equippable equippable = InventoryHelper.GetEquippable(pThing.ConfigName);
      eEquipmentSlots[] slots = equippable.Slots;
      foreach (eEquipmentSlots key in slots)
      {
          if (!string.IsNullOrWhiteSpace(characterComponent.Equipped[key]) && characterComponent.Equipped[key] != pThing.Id)
              Debug.LogError("Trying to unequip something that is not equipped for guid: " + pCharacterEntity.Guid);
          characterComponent.Equipped[key] = string.Empty;
      }
      if (pCharacterEntity.Has<PlayerComponent>())
          _addEquippablePartyStats(equippable, pAdd: false);
      if (pReconsiderVitals)
          CharacterHelper.ReconsiderVitals(pCharacterEntity);
  }
  ```
  `EquipmentHelper.cs:130-149`
- `CanEquip` blocks equipping over a slot whose current occupant is tagged
  `"CANNOT_UNEQUIP"` (a literal tag-string check, `EquipmentHelper.cs:20-27`) — a mod-added
  weapon can be permanently stuck if it (or something in its slot) carries that tag.

### What happens to the ability list when you swap weapons

Abilities are **not** cached per-character; they are recomputed fresh every time
`CombatHelper.GetAbilities` runs (called from `CombatPhase._getActiveEntityAbilities`,
`CombatPhase.cs:2808-2836`), by walking the character's *currently equipped* things:

```csharp
public static List<AbilityAction> GetAbilities(Entity pCharacter, bool pMainHandOnly, ...)
{
    ...
    equipments.UnionWith(EquipmentHelper.GetEquippedThings(pCharacter, pVisualOnly: false, pIncludeTraits: true));
    ...
    foreach (Thing item2 in equipments)
        addThingAbilities(item2);
    ...
}
```
`CombatHelper.cs:330-380` (equip-collection), `addThingAbilities` at `CombatHelper.cs:361-364`
delegates to `GetThingAbilities`:
```csharp
public static List<AbilityAction> GetThingAbilities(Entity pCharacter, Thing pThing)
{
    if (InventoryHelper.HasInteractable(pThing.ConfigName))
        return CharacterHelper.GetCharacterUseItemAbilities(pCharacter, pThing);
    return new List<AbilityAction>();
}
```
`CombatHelper.cs:321-327`

So: **swap the weapon, and the ability menu changes the very next time it's rebuilt** — there
is no stale-ability state to invalidate, because the list is derived from
`Equipped[MAIN_HAND]`/`Equipped[OFF_HAND]` (etc.) at read time, not stored anywhere. The old
weapon's abilities disappear immediately; the new weapon's `Interactable.Abilities` keys
appear immediately (subject to the UI-menu bound in ABILITY_MENU below). Ammo/reload state is
recalculated the same way via `checkAmmo()`, which reads `CustomData["AMMO"]` off whichever
thing is now in `MAIN_HAND` (`CombatHelper.cs:466-483`).

---

## ABILITY_CONFIG

### `CombatAbilityConfig` — every field, verbatim

```csharp
public class CombatAbilityConfig
{
    public string Inherits;
    public eTileTargetAreas TargetArea;
    public eTargets Target;
    public eTileOccupancies TileOccupancy;
    public eTileRowPositions OriginRowPosition;
    public eTileRowPositions TargetRowPosition;
    public eDamageAgainstRow DamageAgainstRow;
    public bool IsMajorAction;
    public bool RequiresSkillRoll;
    public int RequiresFocus;
    public bool IsFocusable;
    public bool IsRanged;
    public bool Kamikaze;
    public bool Chargeable;
    public int Ammo;
    public eAnimationIdentities AnimationIdentity;
    public List<(eCombatActions, object)> Actions;
    public eAiTendencies Tendency;
    public List<string> Tags;

    public void Merge(CombatAbilityConfig pConfig, JsonElement pJson) { ... }
}
```
`CombatAbilityConfig.cs:1-119`

Field meaning:
- `Inherits` — name of a parent `CombatAbilityConfig` entry to merge over (see below).
- `TargetArea` — shape of the hit area: `eTileTargetAreas` (below).
- `Target` — who can be targeted at all: `eTargets` (below).
- `TileOccupancy` — whether the target tile must be `FULL` (occupied), `EMPTY`, or `ANY`:
  `eTileOccupancies` (below).
- `OriginRowPosition`/`TargetRowPosition` — row constraint on the caster/target:
  `eTileRowPositions` (below).
- `DamageAgainstRow` — bonus-damage row filter: `NONE, FRONT, BACK`.
- `IsMajorAction` — consumes a Primary Action (vs. minor/free actions).
- `RequiresSkillRoll` — whether a skill-check roll gates success (vs. always-succeeds
  utility abilities like most `SELF`-target buffs).
- `RequiresFocus` — Focus-point cost.
- `IsFocusable` — whether spending extra Focus improves this ability's roll.
- `IsRanged` — affects animation/AI targeting logic, not literally "no melee retaliation".
- `Kamikaze` — self-sacrifice flag (used by suicide abilities).
- `Chargeable` — whether this ability can be charged up (`Ammo`/charge tracking).
- `Ammo` — ammo cost override at the ability level (separate from the thing-level `Ammo`).
- `AnimationIdentity` — which animation set to play: `eAnimationIdentities` (large enum,
  `STANDARD, BASH, BASIC, BITE, CLAW, COLUMN, DIRECT, DOWN, GRAPPLE, HEAVY, ITEMUSE, KICK,
  LICK, LOB, MAGIC, POUND, PUNCH, ROW, SCREAM, SHIELD, SLASH, SLICE, SLICELEFT, SPECIAL,
  SPRAY, STAB, SUMMON, UP, UPPERCUT, INTRO, LEVELUP, VARIANT, SKILL_JUSTICE,
  SKILL_CALLEDSHOT, SKILL_STEADFAST, DOH_SLASH, PARRY, REDIRECT, EAT`,
  `eAnimationIdentities.cs`).
- `Actions` — `List<(eCombatActions, object)>`, the ordered list of effects this ability
  performs; each tuple's `Item2` payload shape depends on `Item1` (see ACTION_VERBS).
- `Tendency` — AI-targeting preference hint: `eAiTendencies` (large enum — `NONE,
  HASSANCTUM, NOSANCTUM, MOSTARMOR, LEASTARMOR, MOSTRESISTANCE, LEASTRESISTANCE,
  MOSTEVASION, LEASTEVASION, MOSTDAMAGE, LEASTDAMAGE, FASTEST, SLOWEST, MOSTHEALTH,
  LEASTHEALTH, MOSTGOLD, MOSTFOCUS, ISBUFFED, FRONTROW, BACKROW, COLUMNSTACK,
  NOTHASPOISON, MUSTHAVEPOISON, MUSTNOTHAVEHIVE, MUSTHAVELOWHEALTH`, `eAiTendencies.cs`).
- `Tags` — free string tags on the ability itself, separate from `ThingConfig.Tags`.

### Enums, verbatim and complete

```csharp
public enum eTargets { ANY, SELF, SELF_PICK, ALLY, ALLY_ALL, ALLY_NEARBY, ALLY_NEARBY_ALL, ENEMY }
```
`eTargets.cs:1-11`

```csharp
public enum eTileOccupancies { ANY, FULL, EMPTY }
```
`eTileOccupancies.cs:1-6`

```csharp
public enum eTileRowPositions { NONE, ANY, FRONT, BACK }
```
`eTileRowPositions.cs:1-7`

```csharp
public enum eTileTargetAreas
{
    NONE, SELF, SINGLE, AOE, SPLASH, ROW, COLUMN, SWIPE, ALL_GROUP, ALL_TOTAL,
    SPLASH_WAVE, BEHIND, CHECKERED, DEFAULT, EXPAND
}
```
`eTileTargetAreas.cs:1-18`

### Real ability JSON examples

`DEFAULT_ABILITY` — the base every `Inherits: "DEFAULT_ABILITY"` entry merges over:
```json
{
  "TargetArea": "SINGLE",
  "Target": "ENEMY",
  "TileOccupancy": "FULL",
  "OriginRowPosition": "ANY",
  "TargetRowPosition": "ANY",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Chargeable": false,
  "Ammo": 0,
  "AnimationIdentity": "STANDARD",
  "Actions": [],
  "Tendency": "NONE"
}
```
Source: `Abilities.json`, key `DEFAULT_ABILITY`.

`DRINK_HILDEBRANT_01` (the poison-drink ability the item above uses) — a self-damage +
confuse consumable, showing `CHANGE_STAT` then `ADD_STATUS`:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "TargetArea": "SELF",
  "Target": "SELF",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": false,
  "RequiresSkillRoll": false,
  "RequiresFocus": 0,
  "IsFocusable": false,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": false, "IsSilent": false, "FlatPercent": -100 } },
    { "Item1": "ADD_STATUS", "Item2": "STATUS_CONFUSE_00" }
  ]
}
```
Source: `Abilities.json`, key `DRINK_HILDEBRANT_01`.

`MAGIC_SUMMON_UNDEAD_ATTACK` — a summon ability, `ADD_CHARACTER` payload shape:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "Target": "ALLY_ALL",
  "TileOccupancy": "EMPTY",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "IsRanged": true,
  "Actions": [
    { "Item1": "ADD_CHARACTER", "Item2": { "Type": "RANDOM", "Value": "SKELETON" } }
  ]
}
```
Source: `Abilities.json`, key `MAGIC_SUMMON_UNDEAD_ATTACK`. `AddCharacterAction`:
```csharp
public class AddCharacterAction { public eSummonTypes Type; public string Value; }
```
`AddCharacterAction.cs:1-5` (`eSummonTypes`: `NONE=-1, SPECIFIC, RANDOM, PLAYTHING,
AS_FOLLOWER`, `eSummonTypes.cs`).

`ChangeStatAction` payload shape (used by `CHANGE_STAT`):
```csharp
public class ChangeStatAction
{
    public string Stat;
    public eDamageType Type;
    public bool IsBlockable;
    public bool IsSilent;
    public int? FlatValue;
    public int? FlatPercent;
}
```
`ChangeStatAction.cs:1-9`. `Stat` is a free string (e.g. `"HP"`); `FlatValue`/`FlatPercent`
are nullable — if neither is set, `InteractableHelper.ApplyStatChange` falls back to rolling
min/max weapon damage instead (`InteractableHelper.cs:648-660`).

### `Inherits` / `Merge` — precise mechanics

Parsing happens once, at ability-config load time, in
`InteractableHelper.ParseAbilityConfigFromJson`:
```csharp
public static CombatAbilityConfig ParseAbilityConfigFromJson(string pAbilityName, Dictionary<string, JsonElement> pAbilityJson, bool pAllowNull = false)
{
    ...
    CombatAbilityConfig combatAbilityConfig = JsonHelper.Deserialize<CombatAbilityConfig>(value.GetRawText());
    if (!string.IsNullOrEmpty(combatAbilityConfig.Inherits))
    {
        CombatAbilityConfig combatAbilityConfig2 = JsonHelper.Deserialize<CombatAbilityConfig>(pAbilityJson[combatAbilityConfig.Inherits].GetRawText());
        combatAbilityConfig2.Merge(combatAbilityConfig, value);
        combatAbilityConfig = combatAbilityConfig2;
    }
    return combatAbilityConfig;
}
```
`InteractableHelper.cs:419-435`

So: the **parent** (`DEFAULT_ABILITY` or whatever `Inherits` names) is deserialized first
into `combatAbilityConfig2`. Then `combatAbilityConfig2.Merge(combatAbilityConfig, value)` is
called, where `value` is the **child's own raw `JsonElement`** (not the parent's). `Merge`'s
body is a series of `if (pJson.TryGetProperty("X", out _)) { X = pConfig.X; }` checks — i.e.
*for every field, if the child's raw JSON literally contains that key, copy the child's
already-deserialized value over the parent's*:
```csharp
public void Merge(CombatAbilityConfig pConfig, JsonElement pJson)
{
    if (pJson.TryGetProperty("TargetArea", out var value)) { TargetArea = pConfig.TargetArea; }
    if (pJson.TryGetProperty("Target", out value)) { Target = pConfig.Target; }
    ... // one such block per field, ending with:
    if (pJson.TryGetProperty("Tags", out value)) { Tags = pConfig.Tags; }
}
```
`CombatAbilityConfig.cs:44-118`

Practical consequences:
- A field the child JSON omits entirely stays at the **parent's** value, not at C#'s
  zero-default — e.g. `DRINK_HILDEBRANT_01` above never mentions `TileOccupancy`,
  `RequiresFocus`, `IsFocusable`... wait, it does mention several but not `AnimationIdentity`
  or `Tendency`, so those two inherit `DEFAULT_ABILITY`'s `"STANDARD"` / `"NONE"` unchanged.
- Setting a field to its zero-equivalent explicitly (e.g. `"IsMajorAction": false`) **does**
  override the parent, because the check is presence-of-key in the raw JSON
  (`pJson.TryGetProperty`), not truthiness of the deserialized value.
- **`Inherits` is one level only** — the merge reads `pAbilityJson[combatAbilityConfig.Inherits]`
  once; if the parent itself declared an `Inherits`, that grandparent chain is not walked (the
  parent's own `Inherits` field would just sit unused on `combatAbilityConfig2`, since nothing
  re-invokes `ParseAbilityConfigFromJson` on it). A mod chaining `Inherits: "PARENT"` where
  `PARENT` itself has `Inherits: "GRANDPARENT"` will **not** pick up `GRANDPARENT`'s fields.
- `Actions` is an all-or-nothing overwrite when present in the child — there is no per-action
  merge; a child that declares `Actions` replaces the parent's entire action list.

---

## ACTION_VERBS

`eCombatActions` (verbatim, 15 members):
```csharp
public enum eCombatActions
{
    MOVE, VOIDWALK, CHANGE_STAT, ADD_STATUS, REMOVE_STATUS, ADD_CHARACTER,
    ADD_CHARACTER_SMOKE, ADD_CHARACTER_INSTANT, FLEE, REVIVE_ALLY, REVIVE_CHARACTER,
    EQUIP_WEAPON, GRAB, VEHICLE_DAMAGE, VEHICLE_REPAIR
}
```
`eCombatActions.cs:1-18`

The authoritative switch is `CombatHelper.ApplyAction` (`CombatHelper.cs:1871` onward), which
is what every combat-context ability action actually routes through:

```csharp
switch (pAction)
{
case eCombatActions.ADD_STATUS: ...        // CombatHelper.cs:1880
case eCombatActions.REMOVE_STATUS: ...     // CombatHelper.cs:2003
case eCombatActions.CHANGE_STAT: ...       // CombatHelper.cs:2020
case eCombatActions.MOVE:
case eCombatActions.VOIDWALK: ...          // CombatHelper.cs:2027
case eCombatActions.REVIVE_ALLY: ...       // CombatHelper.cs:2114
case eCombatActions.ADD_CHARACTER:
case eCombatActions.ADD_CHARACTER_SMOKE:
case eCombatActions.ADD_CHARACTER_INSTANT: ... // CombatHelper.cs:2124
case eCombatActions.FLEE: ...              // CombatHelper.cs:2325
case eCombatActions.VEHICLE_DAMAGE: ...    // CombatHelper.cs:2339
case eCombatActions.VEHICLE_REPAIR: ...    // CombatHelper.cs:2349
default:
    throw new Exception("ProcessCombatAction: " + pAction.ToString() + " not supported");
}
```
`CombatHelper.cs:1880-2361`

| Verb | Handled in `CombatHelper.ApplyAction`? | What it actually does |
|---|---|---|
| `ADD_STATUS` | Yes | Requires `pRollData.Status == PERFECT`. Resolves `pActionArgs` to a status-name string. Blocks if the roll was avoided, or the target has `PROTECT`/`INFINITE_PROTECT`/`STAR_SHIELD` and the status is a curse/cure type (then tries to redirect the protect instead, `TryProtectEntity`). Blocks on immunity, on dead targets, on `AURA`/`GUARD`/`DECOY` statuses targeting non-self, and on a skill flag `NEGATE_ADD_STATUS`. Then a literal `switch (statusName)` special-cases four string values before falling to the generic path: `"RUSH"` (reorders `RoundEntities` to move the target up in initiative), `"INTERRUPT"` (moves the target's turn to the end, or queues an interrupt if not in the round yet), `"STEAL_GOLD"` (computes a gold amount from `_stealScales` and `InventoryHelper.DecreaseCurrency`s the target), `"DESTROY_EQUIPPED"` (picks a random non-fixed equipped item via `EquipmentHelper.TryGetRandomEquippedThing` and unequips + removes it). `"DRAINLIFE"`/`"DRAIN_FOCUS"` are no-ops here (handled elsewhere). Anything else calls `InteractableHelper.ApplyStatus` (the generic status-effect application). `CombatHelper.cs:1880-2001` |
| `REMOVE_STATUS` | Yes | Requires `PERFECT` roll. Calls `InteractableHelper.RemoveStatus`. Special-cases the literal string `"CURSE"`: if the target's linked player-follower is itself cursed, that follower is force-killed. `CombatHelper.cs:2003-2019` |
| `CHANGE_STAT` | Yes | Only if target `Has<CharacterComponent>()`. Deserializes `pActionArgs` into `ChangeStatAction` and calls `InteractableHelper.ApplyStatChange`, which does the actual damage/heal math (positive `FlatValue`/`FlatPercent` heals via a `switch (pStatAction.Stat) { case "HP": ... }`, computing crit bonus and calling `CalculateFinalDamage`; negative values or omitted values roll min/max weapon damage). `CombatHelper.cs:2020-2026`, math in `InteractableHelper.cs:626-700+` |
| `MOVE` / `VOIDWALK` | Yes (shared case) | No-op if the *origin* is a tile entity. If `pActionArgs` is empty, moves the origin onto the target's tile (`VenueHelper.SetTilePosition`); otherwise (and only on a `PERFECT` roll, target not a tile, no `MOVE` immunity) calls `CombatHelper.MoveCharacter` to push the target by a direction string. Also re-syncs any tile-linked status effects (`TileSync`) between the vacated and occupied tiles. `CombatHelper.cs:2027-2113` |
| `REVIVE_ALLY` | Yes | If `pActionArgs` is empty/null, adds a `REVIVED` result for the target. Logs `"USING REVIVE ABILITY"` if `RobustLogCombat` is set. Does not itself check life-pool — that's gated earlier in `IsUsableAbility`/`_showPlayerAbilitiesMenu`. `CombatHelper.cs:2114-2123` |
| `ADD_CHARACTER` / `ADD_CHARACTER_SMOKE` / `ADD_CHARACTER_INSTANT` | Yes (shared case) | Requires `PERFECT` roll. Deserializes `AddCharacterAction`. Bails if the target tile already holds a living combatant. Has **two hard-coded ability-name branches**: `pAbilityName == "DOLL_SUMMON_01"` forces `eSummonTypes.PLAYTHING` and derives the summon target from splitting the *thing's* config name; `pAbilityName == "SUMMON_HONEYBEE"` runs an entirely bespoke path that looks up an existing bee entity by its `GUID` custom-data, joins it as a follower, and transfers XP, instead of creating a new entity via `TryCreateSummon`. All other summons go through `CombatHelper.TryCreateSummon`. Result kind (`CHARACTER_ADDED`/`_SMOKE`/`_INSTANT`) is chosen from `pAction` via a switch expression. `CombatHelper.cs:2124-2324` |
| `FLEE` | Yes | Requires `PERFECT` roll and target `Has<CharacterComponent>()`. If the target has `SKILL_SMOKEFLEE`, adds a skill-procced result instead of a plain flee result. Adds the target's GUID to `VenueResults.FleePlayerGUIDs`. `CombatHelper.cs:2325-2338` |
| `VEHICLE_DAMAGE` | Yes | Guarded by "there is a vehicle", "origin isn't friendly", and a same-group check so allies can't damage their own vehicle; skippable by a skill flag `NEGATE_VEHICLE_DMG`. Parses `pActionArgs` as an int and calls `AdventureHelper.AddShipDamageToVehicle`. `CombatHelper.cs:2339-2348` |
| `VEHICLE_REPAIR` | Yes | No-op if origin is a tile entity or `pActionArgs` is non-empty. Fully repairs the vehicle (`GetVehicleMaxHealth - CurrentHealth`) via `AdventureHelper.ReduceShipDamageToVehicle`. `CombatHelper.cs:2349-2360` |
| `REVIVE_CHARACTER` | **No** — falls to `default:` | Not referenced anywhere in `CombatHelper.ApplyAction`'s switch, nor found as a distinct handler elsewhere in the searched helpers. An `Actions` entry using this verb would hit the `default:` branch and **throw** (`"ProcessCombatAction: REVIVE_CHARACTER not supported"`). |
| `EQUIP_WEAPON` | **No** — falls to `default:` in `ApplyAction`, but exists as a **pseudo-ability name**, not an action verb executed here | `"EQUIP_WEAPON"` is added as an ability button whenever `equipments.Count > 1` (`CombatHelper.cs:445-448`), and `IsUsableAbility` special-cases the literal string `"EQUIP_WEAPON"` to check the player isn't already wielding their unarmed weapon (`CombatHelper.cs:556-565`). It's driven by a dedicated UI flow, not by any `Actions` entry going through `ApplyAction` — putting `EQUIP_WEAPON` in an ability's `Actions` array would still hit `ApplyAction`'s `default:` and throw. |
| `GRAB` | **No** — falls to `default:` in `ApplyAction` | Grabbing is tracked separately: `IsGrabAvailable` (`CombatHelper.cs:502-509`) and `CombatState.GrabbedEntities` (populated when an `ADD_STATUS` status name contains `"GRAB"`, `CombatHelper.cs:2000`: `if (text2.Contains("GRAB")) { ...GrabbedEntities.Add(...); }`). There's no `eCombatActions.GRAB` case in `ApplyAction`; grab state is a side effect of a status name substring match, not this verb. |

There is also a **second, smaller** `ApplyAction` overload on `InteractableHelper`
(`InteractableHelper.cs:601-624`) used for **out-of-combat / overworld** consumable use
(`PerformConsumableAbility`, `InteractableHelper.cs:539-587`). It only implements
`ADD_STATUS`, `REMOVE_STATUS`, and `CHANGE_STAT`; everything else falls to a
`Debug.LogError(...)` (not a throw) at its own `default:` (`InteractableHelper.cs:621-623`).
So the same ability config can behave differently depending on whether it's invoked in combat
(`CombatHelper.ApplyAction`, throws on unsupported verbs) or as an overworld consumable
(`InteractableHelper.ApplyAction`, silently logs and no-ops on unsupported verbs, and skips
`ADD_CHARACTER` entirely unless `pContext == eConsumableTypes.COMBAT`,
`InteractableHelper.cs:558-563`).

---

## ABILITY_MENU

### How the visible ability list is computed

`CombatPhase._showPlayerAbilitiesMenu` (no-arg) fetches the current abilities via
`_getActiveEntityAbilities`, which wraps `CombatHelper.GetAbilities`
(`CombatPhase.cs:2808-2836`), then calls the list-taking overload
(`CombatPhase.cs:2840` onward) to actually populate the UI.

### The bound — confirmed, and it is real

```csharp
for (int i = 0; i < CombatAbilitiesTemplateHelper.AbilityButtonTemplates.Count; i++)
{
    Button button = CombatAbilitiesTemplateHelper.AbilityButtons[i];
    ...
    if (pAbilities.Count > i)
    {
        ability = pAbilities[i];
        show = true;
        ...
    }
    ...
}
```
`CombatPhase.cs:2854-2863` (inside `_showPlayerAbilitiesMenu(List<AbilityAction>, ...)`,
`CombatPhase.cs:2840`)

The loop bound is `CombatAbilitiesTemplateHelper.AbilityButtonTemplates.Count`, **not**
`pAbilities.Count`. Any `AbilityAction` at index `i >= AbilityButtonTemplates.Count` is never
even looked at — the loop simply doesn't run that far, so it's not "computed and hidden",
it's not iterated at all.

`AbilityButtonTemplates` (and the parallel `AbilityButtons` list of the actual `Button`
elements) are populated once, at UI init, from a **UI Toolkit query over a UXML asset**:
```csharp
public static VisualElement Initialize(VisualElement pRootElement)
{
    TemplateContainer = pRootElement.CachedQ("combat-abilities-menu");
    ...
    AbilityButtons = TemplateContainer.Query<Button>("ability-button").ToList();
    ...
    AbilityButtonTemplates = TemplateContainer.Query("ability-template").ToList();
    ...
}
```
`CombatAbilitiesTemplateHelper.cs:157-192` (query calls at lines 165 and 192)

**This confirms the reported bug mechanism exactly.** The number of `"ability-template"`
elements under the `"combat-abilities-menu"` visual tree is fixed by whatever UXML/USS
document defines that UI panel — that document is a Unity UI Toolkit asset (`.uxml`), not C#,
and **is not present in the decompiled managed assembly** (it ships as part of the game's
asset bundles / Resources, outside `FTK2.dll`). This report cannot state the exact numeric
bound from code alone — say so plainly rather than guess:

> The cap is baked into a Unity UI Toolkit asset (a `.uxml` template under
> `combat-abilities-menu` in the game's asset bundles), not in any C# constant. `ilspycmd`
> against `FTK2.dll` cannot see it.

**How to actually measure it** (a live experiment, not more decompiling):
1. Equip a character with a weapon/consumable combo whose combined `Interactable.Abilities`
   keys exceed some guessed count (e.g. 8+), or use the Crucible harness to inject extra
   `AbilityAction`s directly into `GetAbilities`'s output if a hook point exists.
2. Open the combat ability menu (`mcp__crucible__ftk2_combat_spawn` /
   `ftk2_screen` / `ftk2_screenshot` against the running instance) and count how many buttons
   actually render vs. how many abilities were fed in.
3. Alternatively, since `AbilityButtonTemplates`/`AbilityButtons` are static lists populated
   once at `Initialize` time, a debugger/log injection reading
   `CombatAbilitiesTemplateHelper.AbilityButtonTemplates.Count` at runtime (e.g. via the MCP
   `ftk2_exec` console-command surface, if it can evaluate expressions, or a temporary
   `Debug.Log` patch) gives the exact number directly, no visual counting needed.

Until measured, treat "abilities beyond the template count silently vanish from the menu (but
are otherwise fully valid — `Actions`, `Merge`, `IsUsableAbility` all still work on them)" as
the mechanism: this matches the bug report ("a mod added three abilities to a weapon and they
never appeared on screen") precisely, since nothing in `GetAbilities`/`GetThingAbilities`
truncates the ability list itself — only the UI loop does, silently.

### Secondary confirmation: `AbilityButtons.ForEach` reset

Separately, `CombatAbilitiesTemplateHelper.Show` hides **every** button in `AbilityButtons`
before the fill loop runs (`CombatAbilitiesTemplateHelper.cs:311-315`,
`AbilityButtons.ForEach(delegate(Button b) { HideButton(b); ... })`), and `AbilityButtons`
(the `Button`s) and `AbilityButtonTemplates` (their container elements) are queried
independently but from the same UXML — both are asset-defined counts, and in the fill loop
they're indexed in lockstep (`AbilityButtonTemplates[i]` for visibility toggling,
`AbilityButtons[i]` for content), so both effectively share the same fixed capacity.

---

## MOD_CAPABILITIES

### Config loading — the mechanism a mod actually has to work with

Configs are loaded from plain JSON files on disk at boot, from `Configs/JSON~` under the
game's data path:
```csharp
private const string CONFIGS_JSON_SOURCES = "Configs/JSON~";
...
public static Configs LoadConfigs(string basePath)
{
    ...
    string text = Path.Join(basePath, "Configs/JSON~");
    ...
}
```
`ConfigsHelper.cs:10, 383-396`

`Things` are parsed per-file and merged additively:
```csharp
private static void ParseThingsConfig(string name, string jsonContent, Configs configs)
{
    ...
    foreach (KeyValuePair<string, ThingConfig> item in JsonHelper.Deserialize<SerializedSortedDictionary<string, ThingConfig>>(jsonContent))
    {
        configs.Things.TryAdd(item.Key, item.Value);
    }
    ...
}
```
`ConfigsHelper.cs:614-628`

**`TryAdd`, not indexer assignment.** Multiple files under `Things\` (`Weapons.json`,
`Items.json`, `Attires.json`, `ARM_CATALOG_S13.json`, `ARM_EOR_ITEMS.json`, ... — 10 files
observed) are all folded into one `configs.Things` dictionary, and **whichever file is
processed first wins on a duplicate key** — a later file cannot override an earlier one's
`ThingConfig` for the same config name. A mod that wants to *override* a vanilla thing must
either pick a config name that doesn't already exist, or control load order so its file is
processed before the vanilla one defining that key (file processing order was not traced
further here — flagged in UNVERIFIED).

Abilities are parsed as a single dictionary and go through `Inherits`/`Merge` resolution at
load time (`ConfigsHelper.cs:633-651`, calling `InteractableHelper.ParseAbilityConfigFromJson`
per key, `serializedSortedDictionary.Add` — a plain `Add`, so two ability files defining the
same key would throw rather than silently pick one, though only one `Abilities.json` ships).

### Can

- **Add a new item/weapon**: drop a new key into (or alongside) `Things/*.json` with a valid
  `ThingConfig`. Confirmed structurally correct via the real examples above; the loader is a
  generic `SerializedSortedDictionary<string, ThingConfig>` deserialize
  (`ConfigsHelper.cs:614-628`), so any well-formed entry loads.
- **Add a new ability**: add a key to `Abilities.json` with a valid `CombatAbilityConfig`,
  optionally via `Inherits: "DEFAULT_ABILITY"` (or any other existing ability) to avoid
  restating every field (see ABILITY_CONFIG's `Merge` semantics — remember it's one level of
  inheritance only, and `Actions` overwrites wholesale rather than merging per-entry).
- **Grant an item**: `InventoryHelper.Give(thing, characterComponent.Things)` is the plumbing
  `EquipmentHelper.Equip(string, Entity, int, int, GameRandom)` itself uses
  (`EquipmentHelper.cs:29-37`) to create-then-equip by config name; the same
  give/equip split is available to a mod directly (the Crucible `ftk2_give` MCP tool already
  exercises this path).
- **Make an ability usable by attaching it to a thing's `Interactable.Abilities`**: this is
  the entire mechanism — `CombatHelper.GetThingAbilities` → `CharacterHelper.
  GetCharacterUseItemAbilities` just reads `Interactable.Abilities.Keys` off whatever's
  equipped (`CharacterHelper.cs:317-323`) — no allowlist beyond "the ability name must exist
  in `Env.Configs.Abilities`" (`InteractableHelper.GetAbilityConfig`,
  `InteractableHelper.cs:406-417`, which `Debug.LogError`s and returns null for an unknown
  name unless `pAllowNull`).

### Cannot (or: works, but silently breaks something else)

- **Cannot exceed the ability-menu template count and have it show up** — see ABILITY_MENU.
  This is the single most important "cannot" for a weapon/ability mod: adding a 9th (or
  whatever the true bound is) ability to a thing's `Interactable.Abilities` produces a fully
  valid, fully functional `AbilityAction` that the player can never click, because the render
  loop never reaches it.
- **Cannot override an existing `ThingConfig` from a second file** — `TryAdd` semantics above;
  first file loaded wins silently (no error, no log, just ignored).
- **Cannot use `Inherits` to chain through more than one parent** — grandparent fields are
  dropped; `Merge` is called exactly once per `ParseAbilityConfigFromJson` invocation
  (`InteractableHelper.cs:419-435`).
- **Cannot rely on `eCombatActions.REVIVE_CHARACTER`, `GRAB`, or `EQUIP_WEAPON` as real
  `Actions` verbs in combat** — none are handled in `CombatHelper.ApplyAction`'s switch; all
  three fall to `default: throw new Exception(...)` (`CombatHelper.cs:2358-2360`). `GRAB` and
  `EQUIP_WEAPON` are implemented through entirely separate mechanisms (status-name substring
  match, and a hardcoded UI ability-name string, respectively) — putting them directly in an
  `Actions` array will crash combat resolution, not do anything useful.
- **Cannot use most of these verbs identically outside combat** — the overworld
  `InteractableHelper.ApplyAction` (used for `ConsumableType: OVERWORLD`/`EITHER` items) only
  implements `ADD_STATUS`/`REMOVE_STATUS`/`CHANGE_STAT`; everything else no-ops with a logged
  error instead of throwing (`InteractableHelper.cs:601-624`), and `ADD_CHARACTER` is silently
  skipped entirely unless the call context is `eConsumableTypes.COMBAT`
  (`InteractableHelper.cs:558-563`) — a summon-on-drink ability simply does nothing if drunk
  outside combat.

### Hard-coded literal-string / literal-name gotchas found

| Literal | Where | Effect |
|---|---|---|
| Status name `"RUSH"` | `CombatHelper.cs:1907` | Reorders initiative for a character-typed target instead of the generic status-apply path. |
| Status name `"INTERRUPT"` | `CombatHelper.cs:1923` | Moves the target to the end of the round / queues an interrupt. |
| Status name `"STEAL_GOLD"` | `CombatHelper.cs:1943` | Computes and transfers gold instead of applying a status effect. |
| Status name `"DESTROY_EQUIPPED"` | `CombatHelper.cs:1966` | Unequips + deletes a random non-fixed equipped item on the target. |
| Status name `"DRAINLIFE"` / `"DRAIN_FOCUS"` | `CombatHelper.cs:2000` | Explicit no-op case (handled by other code paths, not this switch). |
| Status name substring `"GRAB"` | `CombatHelper.cs:2004`: `if (text2.Contains("GRAB")) { GrabbedEntities.Add(...); }` | Any status name containing the substring `"GRAB"` — not an exact match — triggers grab tracking. A mod status merely named e.g. `"STATUS_GRABBED_CLAW"` would trip this. |
| Status name `"CURSE"` | `CombatHelper.cs:2018` (in `REMOVE_STATUS`) | Force-kills a linked cursed follower when curse is removed. |
| Ability name `"DOLL_SUMMON_01"` | `CombatHelper.cs:2169-2171` | Forces `eSummonTypes.PLAYTHING` and derives the summon target by splitting the *item's* config name on `_`. |
| Ability name `"SUMMON_HONEYBEE"` | `CombatHelper.cs:2174-2214` | Entirely bespoke summon path (looks up an existing entity by `GUID` custom-data instead of creating one). |
| Ability name `"EQUIP_WEAPON"` | `CombatHelper.cs:448, 556-565`; `CombatPhase.cs:2944` (checked against `SKIP_TURN`/`EQUIP_WEAPON` for tutorial gating) | Injected as a pseudo-ability whenever the character has >1 equippable thing; its usability is special-cased by exact string match in `IsUsableAbility`, not by anything in `Abilities.json`. |
| Ability name `"SKIP_TURN"` | `CombatHelper.cs:520`; `CombatAbilitiesTemplateHelper.cs:408-410` | Always usable; label special-cased. |
| Ability name `"BASIC_MOVE"` | `CombatHelper.cs:530-534`; `CombatAbilitiesTemplateHelper.cs:412-414` | Usability blocked by `ENTANGLE`/`DAZE`/large-actor/`STATIONARY` tag; label special-cased. |
| Ability name `"REVIVE_ALLY"` | `CombatHelper.cs:542-546` | Usability additionally requires `CurrentLifePool > 0` and a dead player-teammate present — logic outside the ability config entirely. |
| Ability *dictionary key* containing `"SUMMON"` | `CombatPhase.cs:5680`: `interactable.Abilities.Keys.Any((string s) => s.Contains("SUMMON"))` | Substring match again (not exact) — gates whether using this item requires an open summon slot (`CombatHelper.TryCheckForSummonAvailability`) and blocks it if the character is confused. A mod ability merely named e.g. `"CONSUME_ITEM"` wouldn't trip this even if it summons; one named `"HEAL_SUMMONER"` would trip it even if it doesn't summon. |
| Config name `"TOOL_VEHICLE_REPAIR_01"` | `CombatPhase.cs:5671-5674` | Hardcoded: this specific item is unusable unless a vehicle is present in combat. |
| Config name `"TOOL_HONEYBEE_01"` | `CombatPhase.cs:5690` | Hardcoded: blocked if the character already has a follower. |
| Tag `"CANNOT_UNEQUIP"` | `EquipmentHelper.cs:20-27` | Blocks equipping anything into a slot currently occupied by an item carrying this tag. |
| Tag `"FIXED"` | `CombatHelper.cs:415` (`InventoryHelper.ItemHasTag(consumableThing, eConfigTags.FIXED)`) | Fixed consumables are skipped from the ability list when `pIgnoreConfuseAbilites` is set. |
| Custom-data key `"BROKEN"` | `CombatHelper.cs:1877-1879` | `ApplyAction` returns immediately (no effect at all) if the acting thing has `CustomData["BROKEN"]` set, regardless of `eCombatActions`. |
| Custom-data key `"AMMO"` | `CombatHelper.cs:466-483`; `CombatAbilitiesTemplateHelper.cs:303-310` | Drives the reload-ability injection and the ammo counter UI; compared against `ThingConfig.Ammo` as the max. |

---

## UNVERIFIED

- The exact numeric cap on visible ability buttons (`AbilityButtonTemplates.Count`) — lives
  in a `.uxml` UI Toolkit asset not present in the decompiled `FTK2.dll`; needs a live
  measurement per the recipe in ABILITY_MENU, not further decompiling.
- The on-disk file-processing order for `ConfigsHelper.ReadJsonConfigs` when multiple files
  under `Things\` define overlapping keys (alphabetical? OS directory-enumeration order?
  explicit list?) — only confirmed that it's `TryAdd` (first-wins), not which file is
  "first". Relevant to whether a mod's own `Things\*.json` file can realistically win a
  key collision by naming/ordering tricks.
- Whether `AbilityBag`/`ShuffleAbilityBag`/`AbilityFillBag` have any player-usable code path
  at all outside the confirmed enemy-AI (`CharacterHelper.GetAbilityBag`, called only from
  `AIHelper`) and juggle-weapon (`AbilityFillBag`, referenced in `CharacterHelper.
  GetAbilityBag`'s `IsJuggleWeapon` branch) cases — not exhaustively traced beyond those two
  call sites.
- Whether the game's own mod-loading path (if any beyond hand-editing the shipped
  `StreamingAssets\...\Configs\JSON~` tree) supports side-loading additional JSON files
  without touching the vanilla ones — `ConfigsHelper.LoadConfigs`/`ReadJsonConfigs` only shows
  a single `Configs/JSON~` directory scan under one `basePath`; no evidence here of a second
  "mods" search path or override-priority system was found or ruled out.
- `InteractableHelper.CHARACTER_ONLY_STATUS`, `TILE_INFLICT_CHARACTER_STATUS`,
  `CURE_STATUS_TYPES`, `CURSE_STATUS_NAMES`, and `_stealScales` (referenced in
  `CombatHelper.ApplyAction`'s `ADD_STATUS`/`STEAL_GOLD` cases) were read at their call sites
  but their full contents were not individually dumped/quoted here — flagging in case a
  future status-effects coverage doc needs them.

## FILE_WRITTEN

`C:\Users\ben\repos\ftk2-mods-crucible\docs\research\coverage\items-abilities.md`
