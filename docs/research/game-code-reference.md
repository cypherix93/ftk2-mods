# FTK2.dll code reference (verified)

Extracted 2026-07-09/10 from `E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed\FTK2.dll`
(5,375 types) via System.Reflection.Metadata dumps + IL field-usage scans. Names are **verbatim**.
"USED_IN" facts are IL-verified. Treat anything not listed here as unverified — check with dnSpyEx before patching.

There is **no official modding API** (no Workshop/ModIO/AddOns types or strings). All modding is
BepInEx/Harmony + StreamingAssets JSON.

## 1. Enemy battle AI

Entire decision pipeline is static class **`AIHelper`**:

- Entry points, called from `CombatPhase._engageActiveEntity` (async):
  **`AIHelper.BehaviourAiDecision`** → falls back to **`AIHelper.StandardAiDecision`**.
  Executed by `CombatPhase._performAiDecision`.
- Decision result type: **`CombatDecisionData`** — fields: `Ability`, `Position`, `FocusUsed`. That is the
  entire contract. A Harmony prefix on the two decision methods that writes its own `CombatDecisionData`
  and returns `false` replaces enemy decision-making entirely.
- Full `AIHelper` method list: `TryGetAiParameter`, `CategorizeCombatOptions`, `GetAbilityCategory`,
  `StandardAiDecision`, `TryConsiderSecondaryAction`, `BehaviourAiDecision`, `ConfusedAiDecision`,
  `ForceAiDecision`, `GetPreferredTarget`, `_orderTargetsByTendency`, `_canUseAbilityByTendency`,
  `PlayerPartyHasAI`, `_initializeAbilityBag`, `_intializeBehaviourShuffle` (sic), `_getDecisionData`.
- `AIHelper` fields: `AI_SOURCE_ID`, `AI_DESTINATION_ID`, `AI_START_POSITION`, `AI_RANGE`,
  `AI_MOVEMENT_RULE`, `_strictTendencies`, `_emptyOccupancyTendencies`.
- Per-entity AI state: **`AIComponent`** — `Parameters`, `PriorityTargets`, `Properties`, `BehaviourShuffle`.
- **`eAiBehaviours`**: `DEFAULT, DPS, SUPPORT, TANK, CURSE`. Weights come from `Behaviours.json` →
  `Configs.Behaviours` as `AiBehaviourConfig { Category, Weight }`.
- **`eAbilityCategories`** (14): `ATTACK, DEBUFF, SUPPORT_SELF, SUPPORT_ALLY, SUPPORT_GROUP, TAUNT,
  USE_ITEM, MOVE, MOVE_ENEMY, SUMMON, FLEE, SKIP_TURN, REVIVE_ALLY, REPAIR_VEHICLE`.
- **`eAiTendencies`** (25 values incl. `MOSTARMOR, LEASTHEALTH, FASTEST, FRONTROW, BACKROW, COLUMNSTACK,
  MUSTHAVEPOISON, ISBUFFED, …`): consumed ONLY by `_orderTargetsByTendency` / `_canUseAbilityByTendency`;
  per-ability value from `CombatAbilityConfig.Tendency` (Abilities.json).
- Ability-bag state: `CombatState.AiAbilities`, `AIComponent.BehaviourShuffle`.

## 2. Combat core

- **`CombatHelper`** (static; key methods): `PerformAbility` (consumes actions; ability execution),
  `ApplyAction` (applies each `eCombatActions` entry; grants/resets too), `SetInitiative`,
  `TickActiveEntityCharacterStatus`, `TickActiveEntityTileStatus`, `IsUsableAbility`, `IsTurnOver`,
  `ResetCharacterActions` (2 overloads), `TryClearEntityActions`, `NextTurn`, `TryCreateSummon`,
  `TryCheckForSummonAvailability`, `TryEndTurnSummons`, `RecalculateLoneWolf`, `_onCombatSkillProc`.
- **`CombatPhase`** (turn flow; key methods): `_engageActiveEntity`, `_performAiDecision`,
  `_performAbility` (async body `<_performAbility>d__106`), `_onTryFocus`, `_characterCanUseItem`,
  `_addEntityToCombat`, `_processCombatResults`, `_nextTurn`, `_initializeNextWave`, `_startPhaseEvent`,
  `_tryStartBossPhase`, `OnTakeTarotCard`, `_endCombatAsync` (contains local `g__cleanUpSummon`).
- **`CombatComponent`** fields: `PrimaryActions`, `SecondaryActions`, `CanSummon`, `DeathSaves`,
  `DodgeCooldown`, `JuggleState`. Serialized component (survives save; netcode-synced — inference).
- Damage/status application: **`InteractableHelper`** — `ApplyStatus` (target/party overloads),
  `ApplyStatChange`, `CalculateFinalDamage`, `PerformConsumableAbility`.
- **`eCombatActions`** includes: `CHANGE_STAT, ADD_STATUS, REMOVE_STATUS, ADD_CHARACTER,
  ADD_CHARACTER_SMOKE, ADD_CHARACTER_INSTANT, REVIVE_ALLY, MOVE, FLEE, VOIDWALK, VEHICLE_DAMAGE,
  VEHICLE_REPAIR`. `ADD_CHARACTER*` handled via `CombatHelper.ApplyAction` → `TryCreateSummon` (IL-verified).

### Action economy choke points (IL-verified users of PrimaryActions/SecondaryActions)
- Consume: `CombatHelper.PerformAbility`, `CombatPhase._performAbility`.
- Gate: `CombatHelper.IsUsableAbility`, `CombatHelper.IsTurnOver`, `CombatAbilitiesTemplateHelper.Show` (UI),
  `CombatPhase._characterCanUseItem`, `InventoryController._isValidInventoryOption`.
- Grant/reset: `CombatHelper.ResetCharacterActions` ← callers: `CombatHelper.NextTurn`,
  `CombatHelper.ApplyAction`, `CombatPhase._addEntityToCombat`, `_processCombatResults`, `_nextTurn`,
  `_initializeNextWave`, `OnTakeTarotCard`.
- Misc touchers: `CombatHelper.TickActiveEntityCharacterStatus`, `InteractableHelper.ApplyStatChange`,
  `CombatPhase._startPhaseEvent`, `_tryStartBossPhase`, `CombatHelper.TryClearEntityActions`.
- Per-ability cost flag: `CombatAbilityConfig.IsMajorAction` (data). UI icons: `eSpriteIcons.PA` / `.SA`;
  end-turn UI: `EndTurnButtonViewHelper`.

## 3. Battle grid ("Venue")

- **`VenueHelper`** (static): properties `VenueMap1`, `ExtendedVenueMap1`, `KrakenMap1`; constants
  `DEFAULT_VENUE_TILE_AMOUNT`, `EXTENDED_VENUE_TILE_AMOUNT`, `KRAKEN_VENUE_TILE_AMOUNT`,
  `DEFAULT_TWO_BY_TWO_POSITION`; methods `getSlotRow`, `CreateVenueTileEntity/Entities`,
  `PlaceCharacterEntitiesAtRandom`, `DoesVenueRowHaveOpenSpot`, `GetVacantTileCount`,
  `MakeRoomForSupportCharacters`, `GetTargetableTiles`, `GetAreaTiles`, `GetEntityAtTileOffset`,
  `GetDamageAgainstRowModifierValue`, `AdjustMaxEnemiesToPlayerCount`, `SetTilePosition`,
  `TrySetStaticCombatPosition`.
- **`eVenueGrids`**: `Standard`, `BossKraken`, `Extended`. Selected per fight via `CombatState.GridType`.
- **`VenueComponent`**: `TilePosition`, `TileSize`, `OccupiedTiles` (2x2 bosses supported).
  **`VenueTileComponent`**: `GroupIndex`, `RowPositionsType`, `AuraStatuses`.
  **`eLoadVenueGridRules`**: `PREFERRED, EXISTING_POSITIONS, FRONT_ROW, BACK_ROW, ORDERED, ANY`.
- Visual side: `VenueViewHelper.LoadCharacterEntitiesToVenueGrid`; per-diorama geometry
  `Diorama.LoadVenueGrid` / `GetVenueGridRoot`, `dDiorama.VenueGrid`. Slot maps are **hardcoded static C#**
  (a `Configs.VenueGrids` field exists but no VenueGrids.json ships — likely built in code; unverified).

## 4. Config system

- **`ConfigsHelper`**: `LoadConfigs`, **`ReloadConfigs`** (hot-reload exists), `CreateConfigs`,
  `ReadJsonConfigs`, `ProcessDirectory`, `ProcessJsonFile`, `SafeParseConfig`, `ParseThingsConfig`,
  `ParseAbilitiesConfig`; fields `CONFIGS_JSON_SOURCES`, `ConfigParsers`, `SingletonParsers`.
- **`Configs`** registry: ~57 fields ≈ 1:1 with `StreamingAssets\Assets\Configs\JSON~\*.json`
  (`Abilities, StatusEffects, Characters, CharacterVariants, Things, Behaviours, CraftConfigs, LoadOuts,
  HallOfHeroes, SkillConfigs, GameDifficulties, Followers, TarotConfigs, ChargeConfigs, VenueGrids, …`).
- `JsonHelper` wraps System.Text.Json. (Newtonsoft.Json.dll is also in Managed.)

## 5. Characters / classes / traits / status / skills

- `CharacterConfig` fields: `Stats, Things, Passives, CampQuery, LootID, Rarity, Level, Threat, BaseType,
  OnDeathAbility, Expansion` (+ optional `SwarmQuery`, `DefaultBodyType`). Player classes = entries tagged
  `PLAYER` in Characters.json. No player-class enum.
- `HeroCharacterConfig` (HallOfHeroes.json — cosmetic ghost/statue heroes): `Name, LootTable, Class,
  BodyType, Color*, MainHand/OffHand/Helmet/Armor/Gloves/Boots/Trinket/Pipe/BackPack, Equipment, Tags`.
- Runtime class swap exists: `PartyManagementDirector._pOnChangeClassDelay(pCharacter, pClassConfigName)`
  and **`_rebuildCharactertAsNewConfigType`** (sic). Mimic combat-time transform:
  `EncounterPhase._mimicTransformAndStartCombat`.
- Traits: no TraitConfig — traits are Things (`Class:"TRAIT"`, hidden) granting `Equippable.Stats` +
  `Passives`. **`eTraits`** enum (17 values `TRAIT_TACTICIAN…TRAIT_LONEWOLF`) is referenced by
  `CharacterHelper.GiveTrait/RemoveTrait/RemoveAllTraits/GetFirstTrait` and special cases
  (`CombatHelper.RecalculateLoneWolf`). **New trait ids require enum handling** (EOR proves injection works).
- `StatusEffectConfig`: `Type, Duration, TickFrequency, TickOverworld, TickCombat, TickExpire, TileSync,
  GroupSync, Passives, AddProperties, Stats, CustomStats`. Runtime: `StatusEffectComponent.Statuses`,
  `StatusEffectInfo { Duration, InitialDuration, TickDuration, OriginEntityId }`; ticking in
  `CombatHelper.TickActiveEntityCharacterStatus/TickActiveEntityTileStatus`. Types/groups:
  `eStatusEffectTypes`, `eStatusEffectsGroups`.
- Skills: `SkillConfigs.json` = tuning only; logic is C# keyed on skill id — proc plumbing:
  `CombatHelper._onCombatSkillProc`, `CombatAbilitySkillCondition`. New skill behavior = patch.
- `CharacterHelper` (misc, EOR patch targets): `GetActorsByTags`, `GetStat`, `InitializePartyStats`,
  `AddHealth`, `GetEnemiesForCombat(EnemySet)`, `GetConfigNameWithBodyType`, `GetDisplayNameForUI`.

## 6. Crafting / materials

- `CraftConfig { Recipes }`, `CraftRecipe { Catalyst, CatalystAmount, Ingredients }` ← CraftConfigs.json.
- **`CraftingHelper`**: `GetCatalystRecipes`, `IsCraftableRecipe`, `GetCraftChoiceText`, `CraftItem`,
  `GetCraftingChoices`.
- Material tiers: `ItemMaterialHelper.GetItemClassConfigs` + Materials.json / ItemMaterials.json.
- **No socket/rune/gem/affix/enchant/reforge system exists in the game assembly** (searched). The EOR mod
  implements affixes in plugin code (`AffixDefinition`, `AffixedItemVariant`, `AffixItemType/Tier/Source`)
  — decompile EnhancedOverhaulRemix.dll for the reference implementation.

## 7. Useful EOR patch precedents (proven hook points)

EOR patches 73 targets with zero `[HarmonyPatch]` attributes (manual `AccessTools` + `Harmony.Patch`,
logging "Target X found"). Highlights relevant to our mods:
- Stat calc / UI: `CharacterHelper.GetStat`, `UIHelper.GetBreakdownStats`,
  `CharacterManagementViewHelper.SetCharacterInfo`, `ItemCardViewHelper.ShowItemCard*`.
- Inventory: `InventoryViewHelper.ShowContextMenu`, `InventoryHelper.Consume/GetThingConfig/HasInteractable`,
  `InventoryController.CharacterFeed`, `EquipmentVisualHelper.GetITMEquipmentPrefab`.
- Combat: `CombatHelper.PerformAbility` (Prefix), `.ApplyAction` (Postfix), `.SetInitiative` (Postfix),
  `.TickActiveEntityCharacterStatus` (Postfix); `CombatPhase._onTryFocus` (Prefix);
  `InteractableHelper.ApplyStatus/ApplyStatChange/CalculateFinalDamage/PerformConsumableAbility`.
- Enemy composition: `CharacterHelper.GetEnemiesForCombat` (enemy-set injection — Nemesis).
- World/quests: `AdventureDirector.Initialize/._handleNetworkAction/._distributeQuestRewards`,
  `QuestHelper.GenerateSideQuestsFromQuestBoardConfig/.AddQuestToGameRun`, `MapGenHelper.
  GenerateMapEncountersFromConfigs`, `AdventureHelper.InitializeWorldModifiers/.FillMarket`.
- Loot: `LootDropHelper.GetAdventureLoadOut/GetLootDropsFromEnemies/GetMarketCategoryQuantity/CreateMercs/CreatePets`.
- Class UI: `CharacterCustomizationViewHelper.RenderClassList/RenderCustomizationContainer/RenderListSelector/
  RenderStatsContainer`, `UIToolkitHelper.RenderListView<string>`.
- Infra: `AppConfigManager.Initialize`, `MainMenuDirector.Initialize`, `RouterMono.Start/Update`,
  `CommandLineHelper.ExecuteCommand`, `AssetLoader.GetImage/GetRender`, `Lang.__t/SetLanguage`.
- Custom network actions piggyback `AdventureDirector._handleNetworkAction` (EOR: `EOR_SYNC_*` string actions);
  per-run persistence piggybacks `GameRunData` (`GameRunData.Create` is patched); EOR also runs a
  mod-sync handshake hashing config+data (`EOR_VER/EOR_CFG/EOR_DAT/EOR_SYS/EOR_DEF/EOR_SIG`).

## 8. Regenerating the dumps

Session scratchpad files may be gone. Recreate with a PowerShell 7 script using System.Reflection.Metadata:
enumerate TypeDefs (namespace+name), MethodDefs/FieldDefs per type, and the #US heap for user strings;
IL field-usage scanning needs reading method bodies and resolving member tokens. Or just use dnSpyEx/ILSpy
interactively — Mono assembly, decompiles cleanly.
