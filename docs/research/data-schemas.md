# FTK2 game data schemas (verified)

Source: `E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\StreamingAssets\Assets\Configs\JSON~\`
All files are flat `id → config` dictionaries unless noted. UTF-8 (Characters.json has a BOM), Newtonsoft-style
pretty-print. Ids are UPPERCASE_UNDERSCORE. The game also merges the EOR mod folders at runtime
(pets/mercs Followers/Characters/Abilities, CustomItems Things packs).

## Characters.json (~2,126 entries; 4.4MB)

Everything with a stat block: player classes (Tags contains `PLAYER`), bosses (`BOSS_*`, 393), mercs
(`MERC_*`, 160), companions (`COMPANION_*`, 178), enemies, NPCs, environment entities.

Fields (15): `Stats{}` (subset of 20 keys: `ACC ATK AWR CRT DEF EVD FOC HP HRG INT LCK PA SA PRW RES SPD
STR TAL THRN VIT` — `PA`/`SA` = primary/secondary actions per turn), `Things{}` (itemId→qty loadout —
**abilities come from equipped Things**), `Passives[]` (`SKILL_*` / `STATUS_IMMUNITY_*`), `LootID`,
`LocKey`, `Rarity`, `Level`, `Threat` (int 0–9 — aggro/target-priority weight; the only per-character AI
knob), `BaseType` (32 species: HUMAN, SKELETON, DEMON, BIRD, VOID…), `Tags[]`, `Expansion`,
optional: `OnDeathAbility` (ability id fired on death), `CampQuery`/`SwarmQuery` (comma-separated
character-id/tag lists — encounter composition & reinforcement waves), `DefaultBodyType` (M/F).

Player class example (`HUNTER`): Stats incl `PA:1,SA:1`; Things = starting gear; Passives =
`SKILL_CALLEDSHOT, SKILL_ENERGYBOOST, SKILL_ELITESNEAK, SKILL_ELITEAMBUSH`; `Level:-1, Threat:0,
Tags:["PLAYER","RANGED"]`.

## Abilities.json (972 entries)

Inheritance via `"Inherits": "DEFAULT_ABILITY"`. Fields: `TargetArea` (SINGLE/AOE/ROW/COLUMN/CHECKERED/
SPLASH/SPLASH_WAVE/SWIPE/ALL_GROUP/SELF/NONE), `Target` (ENEMY/ALLY/ALLY_ALL/ALLY_NEARBY/ANY/SELF/
SELF_PICK), `TileOccupancy` (FULL/ANY), `OriginRowPosition`/`TargetRowPosition` (FRONT/BACK/ANY/NONE),
`DamageAgainstRow`, `IsMajorAction` (bool), `RequiresSkillRoll`, `RequiresFocus` (int), `IsFocusable`,
`IsRanged`, `Kamikaze`, `Chargeable`, `Ammo`, `AnimationIdentity`, **`Tendency`** (AI targeting hint,
25-value enum: FRONTROW BACKROW FASTEST SLOWEST LEASTHEALTH MOSTHEALTH LEASTARMOR MOSTARMOR LEASTEVASION
MOSTEVASION LEASTRESISTANCE MOSTRESISTANCE MOSTDAMAGE MOSTFOCUS MOSTGOLD ISBUFFED MUSTHAVEPOISON
NOTHASPOISON MUSTNOTHAVEHIVE COLUMNSTACK NONE …), `Actions[]`, `Tags[]` (AMBUSH/JUMP seen).

`Actions` = list of `{Item1: eCombatActions, Item2: params}`:
- `CHANGE_STAT` → `{Stat, Type, IsBlockable, FlatValue?, FlatPercent?}`; Stat ∈ {HP FOC MXHP MXFOC STR VIT
  MAG INT AWR TAL LCK SPD MOV PA SA DEF RES EVD PHY XP}; Type ∈ {PHYSICAL MAGICAL BLEED FIRE INFINITE_FIRE
  POISON RATTLED REGEN STRENGTH}. Negative FlatValue = damage; no value = weapon-scaled.
- `ADD_STATUS`/`REMOVE_STATUS` → status id string (or category `"DEBUFF"`).
- `ADD_CHARACTER` → `{Type, Value}` (summon by character config).
- `FLEE` → roll object `{MinValue,MaxValue,ACC,Stat,Rolls,Ammo}`; `MOVE`/`VOIDWALK` → null;
  `REVIVE_ALLY` → `""`; `VEHICLE_DAMAGE/REPAIR` → int.
- No cooldown field — gating is IsMajorAction/RequiresFocus/Ammo.

Item-side ability params (Things files, `Interactable.Abilities`): per-ability
`{MinValue, MaxValue, ACC, Stat (scaling stat), Rolls, Ammo}` + weighted `AbilityBag[]` (repeats = weight),
`AbilityFillBag[]`, `ShuffleAbilityBag`.

## Behaviours.json — AI category-weight profiles

`ProfileName → [{Category, Weight}]`. Shipped: `DEFAULT`, `DPS`, `SUPPORT`, `CURSE` over categories
`ATTACK, SUPPORT_ALLY, SUPPORT_SELF, DEBUFF`. Engine enum supports 14 categories (see code reference).
Referenced by `Followers.json` `Behaviour` field and `eAiBehaviours`. New profiles = pure JSON.

## StatusEffects.json (~40 Types)

`StatusID → {Type, Duration (-1 perm, 0 instant), TickFrequency, TickOverworld, TickCombat, TickExpire,
TileSync, GroupSync, AddProperties[], Stats{}, Passives[]?}`. Types (code-recognized): AURA BUFF DEBUFF
BLEED POISON FIRE INFINITE_FIRE ICE WATER SHOCK ACID ENTANGLE GRAB STUN DAZE SCARE CONFUSE TAUNT GUARD
PROTECT INFINITE_PROTECT STAR_SHIELD REGEN PURIFY REFLECT DECOY DEATHMARK DEATHSAVE CURSE IMMUNITY MARKED
IMBUE_FIRE/ICE/SHOCK/WATER CHANNEL_MAGIC CONCENTRATION PROWESS VIGOR HASTE RATTLED LINK POLLINATE LONEWOLF
SANCTUM CHARGE INK ROWDY. New effects reusing an existing Type = pure JSON; new Type = code.

## SkillConfigs.json — SKILL_* tuning (logic is C#)

`SkillID → {Properties:[{…}]}`. Property vocabulary: `PROC_CHANCE, AI_PROC_CHANCE, PROC_STAT_1..3 +
PROC_STAT_MULTIPLIER_n, PROC_STAT_LUCK_BONUS, EVENT_PROC (START_TURN/END_TURN), PROC_RADIUS,
PROC_COOLDOWN/SKILL_COOLDOWN, PROC_EQUIPMENT, TARGET, STATUS_EFFECT, MAX_SKILL_CACHE, IS_MANUAL_ABILITY,
PROC_BAG_ATTEMPT/PROC_BAG_SUCCESS, ENCOUNTER_TYPES, NEGATE_ADD_STATUS, AURA_AREA, MULTIPLIER`. Many skills
have empty Properties (logic entirely code-side). Adding a new SKILL_ id does nothing without a C# handler.

## Things\ (Weapons 1,020 / Attires 595 / Items / Traits 18)

ThingConfig: `ConsumableType (NONE/COMBAT), Value, MinTier/MaxTier, Class (23: BLADE KATANA DAGGER RAPIER
AXE BLUNT SPEAR WHIP WAND LONGSTAFF BOW RIFLE GUN SHOTGUN HANDBOW CANNON BOOMERANG BOMB SHIELD ARMOR_BODY
ARMOR_HEAD ARMOR_HANDS ARMOR_FEET ARMOR_TRINKET TRAIT), Rarity, Material, Hidden, Stacks, Ammo,
Equippable{Slots[], Stats{}, Passives[], MaxCharges}, Interactable{Abilities{}, AbilityBag[],
AbilityFillBag[], ShuffleAbilityBag}, Tags[], Expansion`.
Tag namespaces: loot/market (`DROPPABLE TOWN_MARKET DUNGEON_MARKET NIGHT_MARKET`), rarity, category,
weapon subtype, `MELEE/RANGED`, weight (`TINY LIGHT MEDIUM HEAVY`), damage type (`PHYSICAL MAGICAL
ELEMENTAL FIRE SHOCK`), procs (`BLEED DAZE STUN CONFUSE BUFF DEBUFF`), faction (`MILITIA GOBLIN CULTIST…`).
**Traits** are ThingConfigs with `Class:"TRAIT"`, `Hidden:true`, empty Slots; power = Equippable.Stats +
Passives (e.g. TRAIT_TACTICIAN → SKILL_TACTICS).

## Crafting / materials / charge

- **CraftConfigs.json**: `OutputItemID → {Recipes:[{Catalyst, CatalystAmount, Ingredients{itemId:count}}]}`.
  Base game: candy only. Fully extensible in JSON.
- **Materials.json**: `MaterialID → {Tier, Type (METAL/LEATHER/CLOTH/WOOD/GLASS), Rarity, Stats{}}`;
  `ATT_*`/`WEP_*` split.
- **ItemMaterials.json**: material family → ordered tier ladder `{Tier, Rarity, Attributes.Explicit.Stats}`.
- **ChargeConfigs.json**: `ChargeID → {OriginChargingStatus, OriginChargingStatusNoLimit,
  TargetInflictStatus, Turns, Area (DEFAULT/AOE/EXPAND), Modifiers:["DAMAGE_PERCENT_200",…]}`.

## Followers.json (base 26KB; EOR extends: pets 149, mercs 249)

`FollowerID → {Type (COMPANION/MERCENARY/CURSE/INANIMATE), ConfigName (→Characters.json), ClassName,
ContractRounds (0 = permanent), ContractPrice (TINY…HIGH), MinTier, MaxTier, Rarity, Behaviour
(→Behaviours.json), Subtitle, JoinParty, LeaveParty (dialogue keys), Tags[] (COMPANION/PETSHOP/MERCENARY),
CaravanStatID, CaravanStatValue, AppendableStats{}?, GiveDeed, Rescued, Expansion}`.

## Quest system

- **QuestTemplates.json** (procedural fillers): `TemplateID → {QuestID (→Quests\), Type (BOUNTY/
  BOUNTY_WATER/DELIVERY/FETCH/EXPLORATION/PURGE/MYSTERY), Args[], Description, Rarity, IsCamp, ThreatLevel,
  ObjectiveArgs[] (comma=OR, "+"=AND e.g. "TWO_BY_TWO+PIRATE"), RewardType (GOLD/ITEM/PARTY_XP/
  REDUCE_CHAOS/LIFE_POOL_UP/NONE), RewardsTagQuery, RewardItemsCount, LootScale, AdventureQuest,
  MysteryReward, Tags[]}`.
- **QuestBoards.json**: `BoardID → {QuestCount, FillerType, ObjectiveTerrains[], RequiredRewardTypes{},
  RequiredQuestTypes{}, RequiredQuestTemplates[], UniqueQuestTemplates[], Inherit}`.
- **Quests\*.json** (scripted): objectives = flat `[verb, arg, …]`; verbs: `ASSASSINATE_ENTITY, DELIVER,
  REMOVE_ENCOUNTER, ACTIVATE_ENTITY, REACH_HEX, REACH_RADIUS_LARGE, DUMMY`; `CompleteObjectives: ALL|ANY`.
  Trigger hooks (`QuestStartWorldTriggers`, `QuestEndWorldTriggers`, `ObjectiveEngageWorldTriggers`,
  `ObjectiveCompleteWorldTriggers`) with actions `DIALOGUE, GIVE_THING, REMOVE_THING_SILENT, CHAOS_ACTIVE,
  NEXT_GAME_STAGE, TOWN_REFRESH, SET_NPC_HOST, REMOVE_ENCOUNTER_PROPERTY`. Dialogue: `PreDialogueID`/
  `PostDialogueID`. Camera: `ObjectiveCameraActions` (ENCOUNTER/ZONE); texts with `[HIDE]`,
  `[SHOW_UNTIL:n]`, `[HIDE_UNTIL:n]`. Chaining: `NextQuests[], CombatQuests[], Requirements[], Conflicts[],
  DontRemoveObjectivesOnConflict, RoundsToExpire, Hidden, DisplayOrder, AdventureEndTrigger`.
  Branching rewards: `ChoiceRewards:true, ChoiceRewardsTake:true`, tokens `[CHOICE_CANCEL]`,
  `[ON_VALID_CHOICE_TRIGGER]` → `RESTART_QUEST`/`CLOSE_QUEST` (reference: STORY_1_6_OFFERING_CHOICE).
- **Adventures\*.json** (campaign scripts): `PlayerStartEncounter, MaxPlayerLevel, MaxEnemyLevel,
  StartingTimeOfDay, TimeOfDayLength, DefaultWeatherState, DefaultNPCHostID, StartQuests[],
  GenericEncounters[], MapZones[] (BiomeConfigName, MegaHexTemplates, Encounters[{UniqueID, SpawnRule,
  SpawnMethod}]), MegaHexes[{ZoneName,x,y}], ZoneSwaps, DailyTownRefresh, StartWorldModifiers[]
  (LIFE_POOL_MAX, EXTRA_LOOT_CHANCE, XP_MULTIPLIER, GOLD_MULTIPLIER…), GameStages[], ZoneSchedules,
  DungeonSchedules`.

## World-encounter engines (roll-table → outcome-verb pattern)

- **MapSpawners.json** (wave spawners): `SpawnerID → {Loop, Prewarm, Health, Waves[{SPAWN_TRIGGER
  (TURN_START/ROUND_START/CHAOS/NONE), SPAWN_FREQUENCY, SPAWN_PER_TRIGGER, SPAWN_MAX_TRIGGERS,
  SPAWN_MAX_UNITS, SPAWN_METHOD, SPAWN_MOVE_RULE (ROAD/LAND_AVOID/AIR), SPAWN_DISTANCE_MIN/MAX, SPAWN_UNIT
  (→TowerDefenseUnits), SPAWN_ENEMY_SET, SPAWN_DESTINATION, SPAWN_DIALOGUE, REACH_DIALOGUE, QUEST_PARENT,
  REWARDS_DESTROY, DAMAGE_ON_REACH}]}`.
- **TowerDefenseUnits.json**: `UnitID → {PropName, SpawnType (TOWER_DEFENSE/HORDE_DEFENSE/HUNT_NEAREST),
  MaxHealth, Movement, CameraFollow, ReachTargetTriggers[], CollideTriggers[] (DAMAGE/DEATH/DISPLACE/
  COMBAT), LootDropArgs, Properties[] (BLOCK_PATH/UNNAVIGABLE/HUNT_PLAYER_NEAREST), DialogueOnCollide/
  Damage/Death}`.
- **Traps.json**: `TrapID → {DisplayName, TrapPropName, SuccessXP, RunTestStat, DisarmTestStat, Tags[],
  RunResults[]/DisarmResults[] ({Item1: SUCCESS/DAMAGE/PARTY_DAMAGE, Item2:[magnitude]}),
  Run/DisarmMysteryOutcomes}`.
- **SkillEncounters.json** (overworld stat-checks, 162KB): `ID → {MapPropName, ButtonName, CanFocus,
  TestStat, Rarity, MinTier/MaxTier, SuccessThreshold, SuccessXP, Tags[], ChoiceRewards, RollModifier,
  Results[] ({Item1: GAIN_PARTY_XP/LOSE_PARTY_XP/GAIN_ITEM/FIGHT/PARTY_CURSE/NOTHING, Item2:[args]}),
  MysteryOutcomes[], Rewards[]}`.
- **RewardEncounters.json**: `ID → {EncounterType (ENEMY/DUNGEON/LOCATION), TypeArgs, DisplayName,
  MapPropName, Guid, TurnsToDecay, ProgressionType, ChanceToSpawn, SpawnConditions[] (MAX_INSTANCE:n,
  HAS_USER_STAT:…), DecayConditions[], RequiresItemKey, StartQuest, Rewards[], EngageDialogue,
  TimeOfDayAvailability[]}`.
- **Wheels.json / WheelWedges.json** (random-effect tables): Wheels = level-bracketed weighted wedge pools
  (`Pools[{PoolCategory, MinLevel, MaxLevel, WedgePool{id:weight}, RequiredAmounts{}}]`, `*` glob);
  Wedges = `WedgeID → {Trigger (verb), Arg, Time?, Type (POSITIVE/NEGATIVE/NEUTRAL/SCOURGE/SPECIAL)}`.
  Verb library: GIVE_THING, GIVE_LOOT, PARTY_PROTECT, LIFE_POOL_UP, APPEND_DUNGEON, ALL_HAS_STAT,
  PLAYER_HAS_PASSIVE, LOSE_ITEM, PARTY_POISON, ACTIVATE_SCOURGE, WHEEL_REPLACE_PIECES….
- **NPCs.json**: `NPC_ID → {SUBTITLE, CHARACTER_CONFIG, TYPE (FRIENDLY/BOSS), PORTRAIT_TEXTURE,
  FOUND_OBJECTIVE, FOUND_RANDOM, ADVENTURE_FAILED, QUEST_FAILED, PARTY_WIPE}` (last five = dialogue ids).
- Also: `DungeonChoices.json`, `FortuneGames.json`, `Scourges.json`, `GameDifficulties.json`,
  `TarotConfigs.json`, `Weathers.json`, `Vehicles.json`, `LoadOuts.json` (per-adventure item/trait
  blacklist — NOT class loadouts), `HallOfHeroes.json` (cosmetic dead-hero memorials — NOT class registry).

## EOR mod data surfaces (for interop)

- `BepInEx\plugins\EnhancedOverhaulRevamped\CustomItems\Things\*.json` — custom item packs (merged into
  Things); `VisualFallbacks.json` (custom id → base-game id for model/icon); `Localization\*.json`.
- `BepInEx\plugins\FTK2_EnhancedPets\{Abilities,Characters,Followers,ServerNames}.json`,
  `FTK2_EnhancedMercenaries\{Followers,ServerNames}.json` — runtime-merged when the EOR master toggles
  (`EnableEnhancedPetsRuntimeMerge` etc.) are on.
- MP: EOR hashes config+data files for a sync handshake; data edits ⇒ same files on all peers or SP only.
