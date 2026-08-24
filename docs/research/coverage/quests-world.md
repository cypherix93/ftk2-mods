# Coverage Map: ADVENTURE, QUEST, WORLD, CHAOS

Source of truth: `DOTNET_ROLL_FORWARD=LatestMajor ~/.dotnet/tools/ilspycmd.exe --disable-updatecheck
-t <Type> -r "<ManagedDir>" "<ManagedDir>/FTK2.dll"` against
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\Managed`.
All `.cs:LINE` citations below point at this repo's canonical per-type decompile dump,
`.decompile-scratch/proj/<Type>.cs` — diffed byte-identical against a fresh decompile run for this
task (`AdventureDirector.cs`, `QuestHelper.cs`, `AdventureHelper.cs` all matched with zero diff).
Shipped JSON is read from
`C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II_Data\StreamingAssets\Assets\Configs\JSON~`.

Live, already-verified engineering context (do not re-derive, cited inline where relevant):
`C:\Users\ben\repos\ftk2-mods-crucible\FTK2.Crucible\src\Crucible.Plugin\RunCommands.cs`,
`...\ChaosCommands.cs`, `...\DebugVerbCommands.cs` — a working Harmony/BepInEx harness that already
reflects into most of the methods this document names, with hard-won notes on what does and doesn't
work, measured live 2026-08-23/24. Their doc-comments are treated as verified fact, not inference,
and are quoted throughout.

---

## 1. RUN_STRUCTURE

### 1.1 Ownership

- **`GameRunData`** (`.decompile-scratch/proj/GameRunData.cs:7`) — the top-level run object. Owns
  quest lists (`FutureQuests`, `ActiveQuests`, `CompletedQuests`, `FailedQuests`), the party
  (`Entities`/`SetEntities`), `AdventureState`, `VenueState`, `DungeonState`, `CombatState`, and a
  pile of `[Obsolete]` aliases (below) that used to be the live home for chaos/round/stage/scourge
  state before they moved down into `MapState`.
- **`AdventureState`** (`.decompile-scratch/proj/AdventureState.cs:5`) — the overworld-adventure
  layer: map gen pools, encounter grab bags, the `Markers`/`AdventureThings`/`SummaryEvents` used by
  UI, `TotalRoundCount` (adventure-scoped, distinct from the per-map `MapState.RoundCount`), and
  critically `MapStates : Dictionary<string, MapState>` plus `ActiveMapID : string`. It also carries
  its own `[Obsolete]` aliases for time-of-day/weather/timeline fields that used to live here before
  moving to `MapState`.
- **`MapState`** (`.decompile-scratch/proj/MapState.cs:3`) — the **per-map, currently-live** state:
  `RoundCount`, `GameStageIndex`, `GameStageRoundStart`, `TimeOfDay`, `TimeOfDayLength`,
  `CurrentTimeOfDayIndex`, `CurrentWeather`, `CustomTimelineEvents`, `ActiveScourges`,
  `WorldModifiers`, and **`ChaosState`**. Reached only via
  `AdventureState.MapState => MapStates[ActiveMapID]`
  (`.decompile-scratch/proj/AdventureState.cs:117`) — a computed property, not a field, keyed by
  whichever map is currently active. Multi-map adventures (e.g. entering a side dungeon that swaps
  `ActiveMapID`) get a **separate** `MapState`, with its own independent round count, chaos state,
  and time of day.

### 1.2 Live field vs. obsolete alias — the "if you want X, read Y" table

| You want... | Obsolete/legacy field (do NOT read) | Live field (READ THIS) | Cite |
|---|---|---|---|
| Current round count | `GameRunData.RoundCount` | `GameRunData.AdventureState.MapState.RoundCount` | `GameRunData.cs:92-93` (`[Obsolete("Use AdventureState.MapState.RoundCount")]`) |
| Chaos state / history | `GameRunData.ChaosState` | `GameRunData.AdventureState.MapState.ChaosState` | `GameRunData.cs:89-90` (`[Obsolete("Use AdventureState.MapState.ChaosState")]`) |
| Game-stage index | `GameRunData.GameStageIndex` | `GameRunData.AdventureState.MapState.GameStageIndex` | `GameRunData.cs:80-81` |
| Game-stage round start | `GameRunData.GameStageRoundStart` | `GameRunData.AdventureState.MapState.GameStageRoundStart` | `GameRunData.cs:83-84` |
| Active scourges | `GameRunData.ActiveScourges` | `GameRunData.AdventureState.MapState.ActiveScourges` | `GameRunData.cs:86-87` |
| World modifiers | `GameRunData.WorldModifiers` | `GameRunData.AdventureState.MapState.WorldModifiers` | `GameRunData.cs:95-96` |
| Time of day | `AdventureState.TimeOfDay` | `AdventureState.MapState.TimeOfDay` | `AdventureState.cs:89` (`[Obsolete("Use MapState.TimeOfDay")]`) |
| Time-of-day slot index (drives HUD) | `AdventureState.CurrentTimeOfDayIndex` | `AdventureState.MapState.CurrentTimeOfDayIndex` | `AdventureState.cs:101` (`[Obsolete("Use MapState.CurrentTimeOfDayIndex")]`) — confirmed by the harness: `DebugVerbCommands.cs` notes *"AdventureState.CurrentTimeOfDayIndex ... already proved does NOT update the HUD"* |
| Time-of-day length/timeline | `AdventureState.TimeOfDayLength`/`.TimeOfDayTimeline` | `AdventureState.MapState.TimeOfDayLength`/`.TimeOfDayTimeline` | `AdventureState.cs:92,95` |
| Current weather | `AdventureState.CurrentWeather` | `AdventureState.MapState.CurrentWeather` | `AdventureState.cs:98` |
| Custom/expired timeline events | `AdventureState.CustomTimelineEvents`/`.ExpiredCustomTimelineEvents` | `AdventureState.MapState.*` | `AdventureState.cs:104,107` |
| Encounter spawn cooldowns | `AdventureState.EncounterSpawnHexCooldown` | `AdventureState.MapState.EncounterSpawnHexCooldown` | `AdventureState.cs:110` |
| Disabled zones | `AdventureState.DisabledZones` | `AdventureState.MapState.DisabledZones` | `AdventureState.cs:113` |
| Active quests | (no obsolete alias) | `GameRunData.ActiveQuests : List<QuestState>` | `GameRunData.cs:7` field list |
| Completed / failed / future quests | — | `GameRunData.CompletedQuests` / `.FailedQuests` / `.FutureQuests` | same |
| Adventure-scoped round count | — | `AdventureState.TotalRoundCount` | `AdventureState.cs:5` field list |

**Confirmed never written**: grepping `.decompile-scratch/proj/AdventureDirector.cs`,
`QuestHelper.cs`, and `AdventureHelper.cs` for any assignment to `GameRun.RoundCount` or
`GameRun.ChaosState` (the obsolete top-level aliases) returns **zero matches** — every mutation in
this codebase targets `AdventureState.MapState.RoundCount` / `.ChaosState` instead. This matches the
pre-established fact in the task brief and is independently reconfirmed here.

### 1.3 Verified by the live harness

`RunCommands.cs` header comment (verbatim, `FTK2.Crucible/src/Crucible.Plugin/RunCommands.cs:36-39`):
```
///   AdventureState.MapState (the live copy; GameRunData.RoundCount and
///     AdventureState.CurrentTimeOfDayIndex are both [Obsolete] and never written)
```

---

## 2. QUESTS

### 2.1 Shape

`QuestData` — the static config (`.decompile-scratch/proj/QuestData.cs:1`, a `struct`):
```csharp
public struct QuestData
{
	public string ID;
	public eQuests QuestType;
	public eSideQuestTypes SideQuestType;
	public eCompleteObjectives CompleteObjectives;
	public string[] Objectives;
	public string[] Requirements;
	public string[] Conflicts;
	public string[] CombatQuests;
	public string[] QuestStartWorldTriggers;
	public string[] QuestEndWorldTriggers;
	public string PreDialogueID;
	public string PostDialogueID;
	public string PostRewardDialogueID;
	public string[] ObjectiveEngageWorldTriggers;
	public string[] ObjectiveCompleteWorldTriggers;
	public string[] ObjectiveCameraActions;
	public string[] ObjectiveCustomTexts;
	public string[] NextQuests;
	public string[] Rewards;
	public bool ChoiceRewards;
	public bool ChoiceRewardsTake;
	public bool Hidden;
	public string[] Tags;
	public string[] ObjectiveTags;
	public int RoundsToExpire;
	public string ExpireDialogueID;
	public int DisplayOrder;
	public bool ShuffleObjectives;
	public bool DontRemoveObjectivesOnConflict;
	public bool DisplayQuestFail;
	public bool DisplayQuestComplete;
	public eAdventureEndTriggers AdventureEndTrigger;
	public bool MysteryReward;
	public bool PartyRewards;
	public string GroupID;
	public string BannerDisplayName;
	public bool QuestMarkers;
}
```

`QuestState` — the live per-run instance (`.decompile-scratch/proj/QuestState.cs:5`, a `class`):
```csharp
public class QuestState
{
	public QuestData Data;
	public bool Initialized;
	public bool Hidden;
	public int[] ObjectiveDisplayOrder;
	public int RoundsLeft = -1;
	public bool[] CompletedObjectives;
	public bool[] RewardsDistributed;
	public List<ObjectiveProgress> ObjectivesProgress;
	public string PreviousQuestID;
	public string MapID;
	[JsonIgnore]
	public List<List<Entity>> ObjectiveEntityCache;
	[Obsolete]
	public QuestState PreviousQuest;

	public override string ToString() { return Data.ID + " - " + MapID; }
}
```

**`Objectives` is a FLAT PAIR ARRAY**: `[verb, arg, verb, arg, ...]`. So the objective *count* is
`Data.Objectives.Length / 2`, and `CompletedObjectives` is indexed by `i / 2` where `i` is the even
verb-slot index. `QuestHelper.CheckObjectiveCompletion` opens exactly this way
(`.decompile-scratch/proj/QuestHelper.cs:371-378`):
```csharp
public static bool CheckObjectiveCompletion(eQuestObjective pObjectiveType, QuestState pQuest, int pObjectiveIndex, GameRunData pGameRun, List<Entity>[,] pHexMap, AdventureConfig pAdventureConfig, List<(QuestState quest, int objectiveIndex, Entity objectiveEntity, Entity player)> pCompletedObjectives, List<Entity> pPlayerEntities, List<(eAbilityResults, object)> pAbilityResults, Entity pActivePlayer)
{
	int completedObjectiveIndex = pObjectiveIndex / 2;
	if (pQuest.CompletedObjectives[completedObjectiveIndex])
	{
		return true;
	}
```
Live-harness restatement of the same gotcha, `RunCommands.cs:188-190`:
```
/// Objectives are a FLAT PAIR ARRAY -- Objectives is [verb, arg, verb, arg, …] so the
/// objective count is Length / 2 and CompletedObjectives is indexed by i / 2. Indexing the
/// pair array directly is the obvious mistake here.
```

A shipped example, `Quests/SIDE_QUEST_BOUNTY_00.json`:
```json
{
	"QuestType": "SIDE_MISSION",
	"Objectives": [
		"ASSASSINATE_ENTITY",
		"{0}"
	],
	"Requirements": [],
	"Conflicts": [],
	"PreDialogueID": "SIDE_QUEST_PRE_DIALOG",
	"PostDialogueID": "",
	"NextQuests": [],
	"Rewards": [],
	"ObjectiveTags": [],
	"Tags": [ "GENERIC" ]
}
```

A story quest that ends the adventure, `Quests/STORY_1_1_CLEAR_BANDIT_KING.json` (quotes the
`AdventureEndTrigger: "WIN"` field named in the task brief):
```json
{
	"ID": "STORY_1_1_CLEAR_BANDIT_KING",
	"QuestType": "STORY",
	"CompleteObjectives": "ALL",
	"Objectives": [ "REMOVE_ENCOUNTER", "GAMBLINGDEN" ],
	"CombatQuests": [],
	"PreDialogueID": "STORY_1_1_CLEAR_BANDIT_KING_PRE_DIALOG",
	"PostDialogueID": "STORY_1_1_CLEAR_BANDIT_KING_POST_DIALOG",
	"ObjectiveCustomTexts": [
		"STORY_1_1_CLEAR_BANDIT_KING,NPC_BANDIT_KING,QUEST_OBJECTIVE_0_ENTITY_NAME,QUEST_OBJECTIVE_0_ENTITY_BIOME"
	],
	"RoundsToExpire": 0,
	"DisplayOrder": 0,
	"AdventureEndTrigger": "WIN"
}
```

### 2.2 `eQuestObjective` — every objective verb (`.decompile-scratch/proj/eQuestObjective.cs` /
same enum, confirmed via `-t eQuestObjective`):

```
DUMMY, AUTO_COMPLETE, GAME_MODIFIER, SCOURGE_MODIFIER, ASSASSINATE_ENTITY, SURVIVE_ENTITY,
REACH_HEX, REACH_HEX_IGNORE_NULL, REACH_ZONE, REACH_PARENT_ZONE,
REACH_RADIUS_VERY_SMALL, REACH_RADIUS_SMALL, REACH_RADIUS_AVERAGE, REACH_RADIUS_LARGE,
REACH_RADIUS_VERY_LARGE, REACH_RADIUS_HUGE, HAS_THING, REMOVE_ENCOUNTER,
REMOVE_ENCOUNTER_IGNORE_NULL, REMOVE_ENTITY, REVEAL_ENTITY, ROUNDS_COMPLETED,
ENTITY_REACH_HEX, ENTITY_REACH_ZONE, ENTITY_REACH_RADIUS_VERY_SMALL, ENTITY_REACH_RADIUS_SMALL,
ENTITY_REACH_RADIUS_AVERAGE, ENTITY_REACH_RADIUS_LARGE, ENTITY_REACH_RADIUS_VERY_LARGE,
ENTITY_REACH_RADIUS_HUGE, DELIVER, DELIVER_TO_TYPE, NOT_HAS_PROPERTY, HAS_PROPERTY,
DESTROY_ENCOUNTER, ENCOUNTER_HAS_HP, DISABLE_MAP_SPAWNER, MAP_SPAWNER_KILLS, MAP_SPAWNER_TURNS,
COMBAT_START, COMBAT_WAVE_START, COMBAT_HAS_ENEMY, CHAOS_HISTORY_CONTAINS, ACTIVATE_ENTITY,
QUEST_COMPLETE, VISIT_DUNGEON_ENCOUNTER
```

What a sample of these check, inside `QuestHelper.CheckObjectiveCompletion`
(`.decompile-scratch/proj/QuestHelper.cs:371-800`), all gated by the same
`if (pQuest.CompletedObjectives[completedObjectiveIndex]) return true;` short-circuit at the top:

- **`AUTO_COMPLETE`** (`QuestHelper.cs:392-394`): `flag = true;` unconditionally — an objective
  authored this way completes the instant it is evaluated once. Not an invokable "complete now" API,
  just a data value that always passes the check.
- **`ASSASSINATE_ENTITY`** (`QuestHelper.cs:395-411`): finds the target entity by GUID; completes if
  it's a dead `CharacterComponent` or a fully-decayed `MapPropComponent` (`TurnsToDecay == 0`); also
  auto-completes if the entity can't be found at all *and* the quest type isn't `COMBAT`/`SCOURGE`.
- **`SURVIVE_ENTITY`** (`QuestHelper.cs:414-421`): completes if the target exists and is **not**
  dead.
- **`HAS_THING`** (`QuestHelper.cs:423-432`): any player entity holding a `Thing` whose
  `ConfigName` matches (case-insensitive).
- **`REACH_HEX`/`REACH_HEX_IGNORE_NULL`** (`QuestHelper.cs:433-466`): a player entity's hex position
  matches the target entity's; on success, a `LOCATION`-type non-trigger encounter is completed and
  decayed via `AdventureHelper.CompleteEncounter`.
- **`DELIVER`/`DELIVER_TO_TYPE`** (`QuestHelper.cs:467-543`): party member carrying a specific
  `Thing` must be co-located with the delivery target (`DELIVER`) or with any encounter of a given
  `eEncounterTypes` (`DELIVER_TO_TYPE`).
- **`ENTITY_REACH_HEX`** (`QuestHelper.cs:544-560`): two GUID-referenced entities share a hex.
- **`REACH_ZONE`/`REACH_PARENT_ZONE`** (`QuestHelper.cs:561-573`): any living party member is
  standing in a named zone (or that zone's parent zone).
- **`CHAOS_HISTORY_CONTAINS`** (`QuestHelper.cs:758-764`):
  ```csharp
  case eQuestObjective.CHAOS_HISTORY_CONTAINS:
      if (pGameRun.AdventureState.MapState.ChaosState != null && pGameRun.AdventureState.MapState.ChaosState.ChaosHistory.Contains(objectiveData))
      {
          flag = true;
      }
      break;
  ```
  — reads `AdventureState.MapState.ChaosState`, the **live** copy, not the obsolete top-level alias.
- **`ACTIVATE_ENTITY`** (`QuestHelper.cs:764-780`): target entity's `EncounterComponent.Properties`
  must contain `eEncounterProperties.ACTIVATED`.
- **`QUEST_COMPLETE`** (`QuestHelper.cs:782-787`):
  ```csharp
  case eQuestObjective.QUEST_COMPLETE:
      if (pGameRun.CompletedQuests.Any((QuestState x) => x.Data.ID == objectiveData))
      {
          flag = true;
      }
      break;
  ```
  — one quest's completion can gate another's objective, purely by ID lookup in
  `GameRunData.CompletedQuests`.

### 2.3 `eCompleteObjectives`, `eQuests`, `eAdventureEndTriggers`

- `eCompleteObjectives`: `ALL, ALL_EXCEPT_DUMMY, ANY, FIRST, REPEAT` — combination rule across a
  quest's multiple objectives, applied in `QuestHelper.GetCompletedQuests`
  (`.decompile-scratch/proj/QuestHelper.cs:863-889`, quoted in full below).
- `eQuests`: `COMBAT, GENERIC_ADVENTURE, MODIFIER, PERMA_MODIFIER, SCOURGE, SIDE_MISSION, STORY,
  STORY_OPTIONAL, SYSTEM`.
- `eAdventureEndTriggers`: `NONE, WIN, LOSE` — see §2.5.

### 2.4 How an objective actually completes → how a quest closes

`GetCompletedQuests` is the driver, called every resolution pass:
```csharp
public static List<QuestState> GetCompletedQuests(GameRunData pGameRun, List<Entity>[,] pHexMap, AdventureConfig pAdventureConfig, List<eQuests> pFilters, List<(QuestState quest, int objectiveIndex, Entity objectiveEntity, Entity player)> pCompletedObjectives, List<Entity> pPlayerEntities, List<(eAbilityResults, object)> pAbilityResults, Entity pActivePlayer)
{
	List<QuestState> list = new List<QuestState>();
	foreach (QuestState item in pGameRun.ActiveQuests.Where((QuestState x) => x.MapID == pGameRun.AdventureState.ActiveMapID && pFilters.Contains(x.Data.QuestType)))
	{
		int num = item.Data.Objectives.Length / 2;
		int num2 = 0;
		for (int i = 0; i < item.Data.Objectives.Length; i += 2)
		{
			if (CheckObjectiveCompletion((eQuestObjective)Enum.Parse(typeof(eQuestObjective), item.Data.Objectives[i]), item, i, pGameRun, pHexMap, pAdventureConfig, pCompletedObjectives, pPlayerEntities, pAbilityResults, pActivePlayer))
			{
				num2++;
			}
		}
		if (num2 == num || (item.Data.CompleteObjectives == eCompleteObjectives.ANY && num2 > 0) || (item.Data.CompleteObjectives == eCompleteObjectives.FIRST && item.CompletedObjectives[0]))
		{
			list.Add(item);
		}
		else if (item.Data.CompleteObjectives == eCompleteObjectives.ALL_EXCEPT_DUMMY)
		{
			int num3 = item.Data.Objectives.Count((string x) => x == "DUMMY");
			if (num - num3 <= num2) { list.Add(item); }
		}
	}
	return list;
}
```
(`.decompile-scratch/proj/QuestHelper.cs:863-889`) — **note the `x.MapID == pGameRun.AdventureState.ActiveMapID` filter**: a `QuestState` stamped with the wrong `MapID` (e.g. left over from a
different map) can never complete, regardless of its objective flags.

`QuestHelper.CheckObjectiveCompletion`, when it finds `flag == true`, records the completion and
(unless the quest is `REPEAT`) permanently flips the bit (`QuestHelper.cs:788-800`):
```csharp
if (flag)
{
	pCompletedObjectives?.Add((pQuest, completedObjectiveIndex, objectiveEntity, item));
	if (pQuest.Data.CompleteObjectives != eCompleteObjectives.REPEAT)
	{
		pQuest.CompletedObjectives[completedObjectiveIndex] = true;
		...
	}
}
```

Closing a quest (success *or* failure, `pAddToFailed` selects which) is
`QuestHelper.CloseQuest(QuestState, List<Entity> pPlayers, GameRunData, List<Entity>
pEntitiesToRefresh = null, bool pAddToFailed = true, string pMapID = null)` (`.decompile-scratch/proj/QuestHelper.cs:321`) — public, static, and idempotent-ish: it removes
the quest from `ActiveQuests`, adds it to `FailedQuests` if `pAddToFailed`, decays any objective
target entities, and strips quest-only reward `Thing`s from the party's inventory.

**`AdventureDirector` never calls `CloseQuest` directly for a normal win** — instead
`_tryCompleteQuests()` (`.decompile-scratch/proj/AdventureDirector.cs:4492`, `async Task<bool>`,
private) calls `QuestHelper.GetCompletedQuests`, then `_resolveQuests(completedQuests,
completedObjectives)` (`AdventureDirector.cs:4547`, `async Task`, private) to run rewards/dialogue/
follow-on-quest logic. `RoundsToExpire` failures instead go through `CloseQuest` directly from the
per-round loop (§4).

### 2.5 How a quest starts, and how the adventure ends

`AdventureDirector._startQuest(QuestData pQuestData, QuestState pParentQuest)` (`AdventureDirector.cs:4682`, `async Task`, private):
```csharp
private async Task _startQuest(QuestData pQuestData, QuestState pParentQuest)
{
	QuestState nextQuestState = QuestHelper.CreateQuestState(pQuestData, pParentQuest?.Data.ID, pInitialized: false, _gameRandom);
	...
	_env.GameRun.ActiveQuests.Add(nextQuestState);
	string[] questStartWorldTriggers = pQuestData.QuestStartWorldTriggers;
	if (questStartWorldTriggers != null && questStartWorldTriggers.Length != 0)
	{
		await _doQuestWorldTriggers(pQuestData.QuestStartWorldTriggers, nextQuestState);
	}
	...
	nextQuestState.Initialized = true;
	if (pQuestData.Objectives.Length <= 2 && pQuestData.Objectives[0] == "AUTO_COMPLETE")
	{
		List<QuestState> autoResolvedQuests = new List<QuestState> { nextQuestState };
		await _resolveQuests(autoResolvedQuests);
	}
}
```
`QuestHelper.CreateQuestState(string pQuestID, string pPreviousQuestID = null, bool pInitialized =
true, GameRandom pGameRandom = null)` and its `QuestData`-taking overload live at
`.decompile-scratch/proj/QuestHelper.cs:227` and `:232`.

**Ending the adventure** — `_tryCompleteQuests` checks the *completed set*'s trigger flag directly
(`AdventureDirector.cs:4517-4525`):
```csharp
if (completedQuests.Any((QuestState x) => x.Data.AdventureEndTrigger == eAdventureEndTriggers.WIN))
{
	await _endAdventure(pIsVictory: true);
	return false;
}
if (completedQuests.Any((QuestState x) => x.Data.AdventureEndTrigger == eAdventureEndTriggers.LOSE))
{
	await _endAdventure(pIsVictory: false);
	return false;
}
```
This matches the pre-established fact and the harness's own framing
(`RunCommands.cs:32-33`): *"Victory is a data flag, not a code path. `_tryCompleteQuests` ends the
adventure when a completed quest carries `AdventureEndTrigger == WIN`."*

A **second**, independent adventure-loss path exists that has nothing to do with quests directly:
`_checkAdventureLoss()` (`AdventureDirector.cs:4453-4461`) OR's together three conditions — party
wipe, a `STORY` quest hitting `RoundsLeft == 0`, and chaos hitting its ceiling (see §3.4). Quest
`RoundsToExpire` failure and quest-driven `LOSE` are two *different* mechanisms.

### 2.6 World triggers

`eWorldTriggers` (partial, confirmed by `-t eWorldTriggers`; the enum continues past what's quoted
here — this is the subset relevant to quests/chaos/time):
```
TOWN_REFRESH, MARKET_REFRESH, RESPAWN_ENEMIES, SWAP_ENCOUNTER, SET_TIME, CHAOS_REDUCE,
CHAOS_INCREASE, CHAOS_HISTORY_ADD, CHAOS_MAX_REDUCE, NEXT_GAME_STAGE, SET_GAME_STAGE,
DECAY_ENCOUNTER, SET_TURNS_TO_DECAY, SET_NPC_HOST, MOVE_PARTY_TO_ENTITY, MOVE_PARTY_TO_ZONE,
MOVE_PARTY_TO_ZONE_AVERAGE, MOVE_PARTY_TO_ZONE_FLAG, CHAOS_ACTIVE, ENEMY_ENCOUNTERS_ACTIVE,
SKILL_ENCOUNTERS_ACTIVE, LIFE_POOL_UP, ADD_MAP_PROP_VISUAL, REMOVE_MAP_PROP_VISUAL, GIVE_THING,
GIVE_LOOT, REMOVE_THING, REMOVE_THING_SILENT, TRIGGER_PLAYER_ABILITY, TRIGGER_WORLD_ABILITY,
REVEAL_ENCOUNTER, REVEAL_ENCOUNTER_TYPE, FORCED_FIGHT, CREATE_FORCED_FIGHT, CANCEL_NEXT_TURN,
KILL_SCOURGE, TOGGLE_ENCOUNTER_TYPE, DISABLE_ZONE, DISABLE_ENCOUNTER_ZONE, ENABLE_MAP_SPAWNER,
DISABLE_MAP_SPAWNER, DECREMENT_ENCOUNTER_HEALTH, INCREMENT_SPAWNER_KILLS, INCREMENT_STAT,
TRY_SHOW_ENCOUNTER_MENU, CREATE_ENCOUNTER, DIALOGUE, ADD_ENCOUNTER_PROPERTY,
REMOVE_ENCOUNTER_PROPERTY, TRIGGER_MAP_SPAWNER, MOVE_TOWER_DEFENSE, PLAY_VISUAL_FX, SET_ACTIVE,
REVEAL_ALL_ROADS, REVEAL_ZONE_ROADS, TOGGLE_QUEST_VISIBILITY, SPAWN_ENCOUNTER,
TIMELINE_CHAOS_REMOVE, BANNER_EVENT, CLOSE_QUEST, RESTART_QUEST, CLOSE_SPECIFIC_MAP_QUESTS,
INCREMENT_STAT_REWARD_ENCOUNTER, ADD_ANONYMOUS_EVENT, ENTER_DUNGEON, SET_ENCOUNTER_SIEGE,
SPAWN_TREASURE, GATHER_PARTY, ZONE_ENCOUNTER_COOLDOWN, DECAY_ZONE_ENEMIES, REVEAL_ZONE_AVERAGE,
DISABLE_TELEPORTING, CHANGE_MAP_PROP, SCOURGE_REDUCE, WAIT, LOOP_INDEX_TRIGGER,
LOOP_INDEX_TRIGGER_CHECK, DECREMENT_DUNGEON_MODIFIERS, ... (enum continues further; only this
prefix was captured by `--methods`/`-t` output before truncation in this pass — UNVERIFIED beyond
this point, see §UNVERIFIED)
```

**`_doQuestWorldTriggers`** — how `QuestData.QuestStartWorldTriggers` /
`.QuestEndWorldTriggers` / `.ObjectiveEngageWorldTriggers` / `.ObjectiveCompleteWorldTriggers`
string-pair arrays get dispatched (`.decompile-scratch/proj/AdventureDirector.cs:4720`, `async Task`,
private):
```csharp
private async Task _doQuestWorldTriggers(string[] pTriggers, QuestState pQuest, int pIndex = -1, Entity pPlayerCompleted = null)
{
	for (int i = 0; i < pTriggers.Length; i += 2)
	{
		if (pIndex > -1 && i != pIndex) { continue; }
		Match match = Regex.Match(pTriggers[i], "(?:<(.*?)>)?(.+)");
		string value = match.Groups[1].Value;
		string value2 = match.Groups[2].Value;
		if (!string.IsNullOrEmpty(value))
		{
			string[] conditionArgs = value.Split(':');
			if (conditionArgs[0] == "REQUIRE") { if (!_env.GameRun.CompletedQuests.Any((QuestState x) => x.Data.ID == conditionArgs[1])) { continue; } }
			else if (conditionArgs[0] == "CONFLICT") { if (_env.GameRun.CompletedQuests.Any((QuestState x) => x.Data.ID == conditionArgs[1])) { continue; } }
			else if (conditionArgs[0] == "NOT_LAST_COMPLETED_OBJECTIVE") { /* only fires if not every non-DUMMY objective is complete */ }
		}
		string[] triggers = value2.Split('|');
		string[] triggerArgs = pTriggers[i + 1].Split('|');
		for (int j = 0; j < triggers.Length; j++)
		{
			if (Enum.TryParse(typeof(eWorldTriggers), triggers[j], out var result))
			{
				await _processWorldTrigger((eWorldTriggers)result, triggerArgs[j], null, pQuest, pPlayerCompleted);
			}
		}
	}
}
```
So each entry pairs a (possibly `<CONDITION>`-prefixed, `|`-delimited multi-trigger) trigger name
with its arg string, and `_processWorldTrigger` (`AdventureDirector.cs:6650`, `protected override
async Task`) is the giant switch that actually executes each `eWorldTriggers` case — this is the
single dispatch point for `CHAOS_ACTIVE`/`CHAOS_REDUCE`/`CHAOS_INCREASE` covered in §3.

Also confirmed: the harness's own quest-chain lesson (`RunCommands.cs:274-284`), showing world
triggers gating quest *availability*, not just quest *effects*:
```
/// Measured 2026-08-24: completing STORY_1_1's opening quests promoted five follow-ons and then
/// STOPPED. The chain is not driven by NextQuests alone -- STORY_1_1_COMPLETE_TASKS, the
/// quest that leads to the WIN quest, carries
/// QuestStartWorldTriggers: ["CHAOS_ACTIVE", "STORY_1_1_CHAOS_3"], so it only appears
/// once the world reaches chaos stage 3.
```

---

## 3. CHAOS

### 3.1 Lifecycle overview

1. A **chaos STAGE** (a whole `ChaosConfig`) is activated by
   `_processWorldTrigger(eWorldTriggers.CHAOS_ACTIVE, <configName>)`
   (`AdventureDirector.cs:6658-6699`):
   ```csharp
   case eWorldTriggers.CHAOS_ACTIVE:
       if (string.IsNullOrEmpty(pArg))
       {
           _env.GameRun.AdventureState.MapState.ChaosState = null;
       }
       else
       {
           ChaosConfig chaosConfig2 = Env.Configs.ChaosConfigs[pArg];
           ChaosState chaosState = ChaosState.Create(pArg, _env.GameRun.AdventureState.MapState.RoundCount);
           _env.GameRun.AdventureState.MapState.ChaosState = chaosState;
           ...
       }
   ```
   **Passing an empty `pArg` NULLS `ChaosState`** — confirmed exactly as the task brief states.
   `ChaosCommands.cs` in the live harness independently confirms and *guards against* this:
   ```
   /// An empty/whitespace arg is REFUSED before it ever reaches the director: the same case sets
   /// ChaosState to null on an empty pArg, and AdventureHelper.ModifyChaosLevel dereferences
   /// ChaosState.ChaosHistory with no null guard on the next chaos tick -- so the empty-arg path is
   /// a live NullReferenceException, not a harmless no-op.
   ```
2. `ChaosState.Create(string pChaosConfigName, int pRoundCount)` (`.decompile-scratch/proj/ChaosState.cs:22-35`):
   ```csharp
   public static ChaosState Create(string pChaosConfigName, int pRoundCount)
   {
       ChaosConfig chaosConfig = Env.Configs.ChaosConfigs[pChaosConfigName];
       ChaosState chaosState = new ChaosState
       {
           MaxChaos = chaosConfig.ChaosLevelMax,
           ConfigName = pChaosConfigName,
           StartedAtRound = Math.Max(0, pRoundCount),
           LastChaosRoundAdded = 0,
           ChaosHistory = new List<string>()
       };
       if (chaosConfig.ChaosLevelStart > 0)
       {
           for (int i = 0; i < chaosConfig.ChaosLevelStart; i++)
           {
               chaosState.ChaosHistory.Add(chaosConfig.MeterIcon);
           }
       }
       return chaosState;
   }
   ```
   **`ChaosState` has no scalar level field — `ChaosHistory.Count` IS the level**, seeded from
   `ChaosConfig.ChaosLevelStart`, exactly as pre-established.
3. Chaos **rises** via `AdventureDirector._triggerChaos(bool pCheckChaosType, int
   pChaosIncreaseValue, string pChaosEvent = null, bool pIsRemoveNextEvent = false)`
   (`AdventureDirector.cs:7703`, `async Task<ChaosPostProcessData?>`, private), invoked from the
   `CHAOS_INCREASE` world-trigger case (`AdventureDirector.cs:7352-7358`):
   ```csharp
   case eWorldTriggers.CHAOS_INCREASE:
   {
       string[] array11 = pArg.Split(',');
       int pChaosIncreaseValue = int.Parse(array11[0]);
       bool pCheckChaosType = array11.Length <= 1 || bool.Parse(array11[1]);
       await _triggerChaos(pCheckChaosType, pChaosIncreaseValue);
       break;
   }
   ```
   `_triggerChaos` resolves the actual chaos-history token to append (from a scheduled
   `CustomTimelineEvents` entry for `POOL`/`SPAWNER` types, or `chaosConfig.TimelineIcon` for
   `FIGHT`), then calls the actual mutator:
   ```csharp
   if (flag) { AdventureHelper.ModifyChaosLevel(chaosConfig, _env.GameRun, pChaosEvent2, pChaosIncreaseValue); }
   ```
4. **`AdventureHelper.ModifyChaosLevel`** — two overloads
   (`.decompile-scratch/proj/AdventureHelper.cs:2550,2558`):
   ```csharp
   public static void ModifyChaosLevel(ChaosConfig pChaosConfig, GameRunData pGameRunData, string pChaosEvent, int pValue)
   {
       for (int i = 0; i < Math.Abs(pValue); i++)
       {
           ModifyChaosLevel(pChaosConfig, pGameRunData, pChaosEvent);
       }
   }

   private static void ModifyChaosLevel(ChaosConfig pChaosConfig, GameRunData pGameRunData, string pChaosEvent)
   {
       bool flag = pChaosConfig.ChaosType == eChaosTypes.CHAOS;
       ...
       if (!string.IsNullOrEmpty(pChaosEvent))
       {
           pGameRunData.AdventureState.MapState.ChaosState.ChaosHistory.Add(pChaosEvent);
           if (pGameRunData.AdventureState.MapState.ChaosState.ChaosHistory.Count > pGameRunData.AdventureState.MapState.ChaosState.MaxChaos)
           {
               pGameRunData.AdventureState.MapState.ChaosState.ChaosHistory.RemoveAt(0);
           }
           ...
           return;
       }
       if (flag && pGameRunData.AdventureState.MapState.ChaosState.ChaosHistory.Count > 0)
       {
           pGameRunData.AdventureState.MapState.ChaosState.ChaosHistory.RemoveAt(pGameRunData.AdventureState.MapState.ChaosState.ChaosHistory.Count - 1);
       }
       ...
   }
   ```
   The public overload with a non-empty `pChaosEvent` **appends** `pValue` times (trimming from the
   front if `ChaosHistory.Count` exceeds `MaxChaos`, so the history is a bounded FIFO window, not an
   unbounded log). With an empty `pChaosEvent` and `ChaosType == CHAOS`, it instead **pops from the
   end** — this is how `CHAOS_REDUCE` reduces the level (see next). This is the exact method the
   task brief pre-established as "mutates `MapState.ChaosState.ChaosHistory`" — confirmed verbatim.
5. Chaos **falls** via the `CHAOS_REDUCE` world-trigger case (`AdventureDirector.cs:7326-7346`),
   which calls `ModifyChaosLevel` with a **negative** value:
   ```csharp
   case eWorldTriggers.CHAOS_REDUCE:
       if (_env.GameRun.AdventureState.MapState.ChaosState != null)
       {
           ChaosConfig chaosConfig = Env.Configs.ChaosConfigs[_env.GameRun.AdventureState.MapState.ChaosState.ConfigName];
           AdventureHelper.ModifyChaosLevel(pValue: -Mathf.Abs(int.Parse(pArg)), pChaosConfig: chaosConfig, pGameRunData: _env.GameRun, pChaosEvent: null);
           ...
       }
       break;
   ```
6. After a raise, `_postProcessModifyChaos(string pChaosEvent = null)`
   (`AdventureDirector.cs:7754`, `async Task<ChaosPostProcessData>`, private) reacts per
   `eChaosTypes`: `FIGHT` starts a forced combat (`FORCED_FIGHT` world trigger, sourced from
   `{Args[0]}_{tier}` enemy set names) and sets `ContinueTurn = false`; `CHAOS`/`POOL`/`TIMER` show
   dialogue and then call `_tryCompleteQuests()` (so a chaos tick can complete/gate quests via
   `CHAOS_HISTORY_CONTAINS`, exactly the objective type named in the task brief); `SPAWNER` pulls a
   spawner/destination pair from two `ShuffleBag<string>`s and triggers a map spawner.

### 3.2 `ChaosConfig` shape and a real example

```csharp
public class ChaosConfig
{
	public eChaosTypes ChaosType;
	public string[] Args;
	public int ChaosFrequency;
	public int ChaosLevelStart;
	public int ChaosLevelMax;
	public string MeterIcon;
	public string TimelineIcon;
	public bool UseSpecifiedMeterIcon;
	public bool Degradation;
	public bool GameOverOnLevelMax;
	public int StartReduction;
	public string ChaosWarningDialogue;
	public string[] ChaosIncreaseDialogue;
	public string[] ChaosDecreaseDialogue;
	public string[][] ChaosTierModifiers;
}
```
(`.decompile-scratch/proj/ChaosConfig.cs:1`). `eChaosTypes`: `FIGHT, CHAOS, POOL, TIMER, SPAWNER`.

Shipped example with `GameOverOnLevelMax: true`, `ChaosConfigs.json["STORY_1_1_CHAOS_1"]`:
```json
{
  "ChaosType": "TIMER",
  "ChaosFrequency": 6,
  "ChaosLevelMax": 4,
  "MeterIcon": "HOURGLASS",
  "TimelineIcon": "HOURGLASS",
  "GameOverOnLevelMax": true,
  "UseSpecifiedMeterIcon": true,
  "ChaosIncreaseDialogue": [
    "STORY_1_1_AUTUMN_FOREST_CHAOS_INCREASE_1",
    "STORY_1_1_AUTUMN_FOREST_CHAOS_INCREASE_2",
    "STORY_1_1_AUTUMN_FOREST_CHAOS_INCREASE_3",
    "STORY_1_1_AUTUMN_FOREST_CHAOS_INCREASE_4"
  ]
}
```
And one with `ChaosTierModifiers` (tiered escalation payloads keyed by history length),
`ChaosConfigs.json["STORY_1_6_CHAOS"]`:
```json
{
  "ChaosType": "CHAOS",
  "ChaosFrequency": 5,
  "ChaosLevelStart": 0,
  "ChaosLevelMax": 3,
  "MeterIcon": "CHAOS",
  "TimelineIcon": "CHAOS",
  "UseSpecifiedMeterIcon": true,
  "StartReduction": 1,
  "ChaosIncreaseDialogue": [ "STORY_1_6_HILDEBRANT_CHAOS_INCREASE_1", "STORY_1_6_HILDEBRANT_CHAOS_INCREASE_2", "STORY_1_6_HILDEBRANT_CHAOS_INCREASE_3" ],
  "ChaosTierModifiers": [
    [ "ENEMY_HEALTH,120", "CHAOS_HEXES,5" ],
    [ "ENEMY_HEALTH,140", "CHAOS_HEXES,12" ],
    [ "ENEMY_HEALTH,160", "CHAOS_HEXES,18", "CHAOS_BEAST", "ENCOUNTER_CHAOS_STATUE_1_6", "END_TURN_CHAOS_DAMAGE,-5", "CHAOS_WEATHER" ]
  ]
}
```
12 `ChaosConfig` entries ship in `ChaosConfigs.json` total (`STORY_1_1_CHAOS_1` through
`SIDE_ADVENTURE_DUNGEON_CRAWL_CHAOS`).

### 3.3 Stage swaps

A chaos **stage** transition is a full config swap, not an incremental change to the existing
`ChaosState` — `_processWorldTrigger(CHAOS_ACTIVE, <newConfigName>)` **replaces the whole
`ChaosState` object** (`ChaosState.Create(pArg, ...)`), discarding the prior `ChaosHistory`,
`MaxChaos`, shuffle bags, etc., and reseeding from the new config's `ChaosLevelStart`. This is how a
level authors progressive chaos stages (`STORY_1_1_CHAOS_1` → `_2` → `_3`), each a different
`ChaosConfig` with its own type/frequency/max — confirmed above by both the `CHAOS_ACTIVE` case body
and the harness's quest-chain example (§2.6) where `STORY_1_1_CHAOS_3` gates a quest's availability.

### 3.4 `GameOverOnLevelMax`

Consumed by `AdventureDirector._checkAdventureLoss()`
(`.decompile-scratch/proj/AdventureDirector.cs:4456-4463`):
```csharp
private bool _checkAdventureLoss()
{
	bool flag = false;
	if (_env.GameRun.AdventureState.MapState.ChaosState != null)
	{
		ChaosConfig chaosConfig = Env.Configs.ChaosConfigs[_env.GameRun.AdventureState.MapState.ChaosState.ConfigName];
		flag = _env.GameRun.AdventureState.MapState.ChaosState != null && chaosConfig != null && _env.GameRun.AdventureState.MapState.ChaosState.MaxChaos > 0 && _env.GameRun.AdventureState.MapState.ChaosState.ChaosHistory.Count >= _env.GameRun.AdventureState.MapState.ChaosState.MaxChaos && chaosConfig.GameOverOnLevelMax;
	}
	bool num = _env.GameRun.Entities.Where((Entity e) => e.Has<PlayerComponent>() && !CharacterHelper.IsDead(e)).Count() == 0;
	bool flag2 = _env.GameRun.ActiveQuests.Any((QuestState x) => x.Data.QuestType == eQuests.STORY && x.RoundsLeft == 0);
	return num || flag2 || flag;
}
```
So `GameOverOnLevelMax` is checked as `ChaosHistory.Count >= MaxChaos` — reaching the chaos ceiling
on a config with the flag set is **OR'd with party wipe and story-quest round-expiry** as one of
three independent adventure-loss conditions, consumed by `_tryVictoryLoss()`
(`AdventureDirector.cs:4468-4482`, `async Task<bool>`, private), which routes to
`_endAdventure(pIsVictory: false)` on any true.

### 3.5 Chaos gating quest availability

Two mechanisms, both already surfaced above:
1. `eQuestObjective.CHAOS_HISTORY_CONTAINS` — an objective completes only once a specific token has
   been pushed into `ChaosHistory` (§2.2, §3.1 step 6).
2. `QuestData.QuestStartWorldTriggers` containing `CHAOS_ACTIVE` with a specific config name as its
   `<CONDITION>` — a quest can be authored to only *appear* once the world reaches a given chaos
   stage (§2.6, the `STORY_1_1_COMPLETE_TASKS` / `STORY_1_1_CHAOS_3` example, independently measured
   live by the harness).

---

## 4. TIME_TURNS

### 4.1 Turn advance

`AdventureDirector._doEndTurn()` (`.decompile-scratch/proj/AdventureDirector.cs:6437`, `async Task`,
private) — the per-player "end my turn" action. Increments `PlayerComponent.TurnsPlayed`, runs
`AdventureHelper.EndTurnActions(...)` (status ticks, fishing, treasure-find skill checks, deaths),
renders results, calls `_tryVictoryLoss()`, `_tryBreakSanctums()`, `_doEndTurnReveals(...)`, and
**finally calls `await _nextTurn(_gameRandom)`** at the very end
(`AdventureDirector.cs:6558` region, last line of the method body).

### 4.2 `_nextTurn` → new round

`_nextTurn(GameRandom pGameRandom, bool pForceNextRound = false, bool pIsFirstTurn = false)`
(`AdventureDirector.cs:2658`, `async Task`, private) decides whether the round advances:
```csharp
if (_roundPlayersEntities.Count <= 1 || pForceNextRound)
{
	await nextRound();
}
else
{
	_roundPlayersEntities.Remove(_activeCharacterEntity);
	if (_roundPlayersEntities.Count == 0) { await nextRound(); }
	else { _reconsiderVehicleGroups(); }
}
```
i.e. **a new round starts only once every player's turn has been consumed this round**
(`_roundPlayersEntities` drains to empty), or `pForceNextRound` is passed explicitly.

Local function `nextRound()` (`AdventureDirector.cs:3137-3190`) is where round-scoped mutation
happens:
```csharp
async Task nextRound()
{
	_env.GameRun.AdventureState.MapState.RoundCount++;
	_env.GameRun.AdventureState.TotalRoundCount++;
	...
	StatsHelper.SetStat("GAME_ROUNDS_PASSED", _env.GameRun.AdventureState.TotalRoundCount, StatsHelper.eStatType.GAMERUN);
	_reconsiderVehicleGroups();
	_roundPlayersEntities = _playerEntities.ToList();
	foreach (Entity roundPlayersEntity in _roundPlayersEntities) { roundPlayersEntity.Get<PlayerComponent>().ActionPoints = 0; }
	// skill cooldowns tick down and expire
	foreach (QuestState item8 in _env.GameRun.ActiveQuests.FindAll((QuestState x) => x.RoundsLeft > 0))
	{
		item8.RoundsLeft--;
		if (item8.RoundsLeft <= 0)
		{
			List<Entity> list4 = new List<Entity>();
			QuestHelper.CloseQuest(item8, _playerEntities, _env.GameRun, list4);
			...
		}
	}
	// progression-level check -> town refresh
	isNewRound = true;
}
```
**Both `RoundCount` (the live, per-map one) and `TotalRoundCount` (adventure-scoped) increment
together, exactly once per new round** — `MapState.RoundCount` is what resets per-map (§1),
`AdventureState.TotalRoundCount` never does. This is also where a `RoundsLeft`-bearing quest expires
by round countdown, closed via `QuestHelper.CloseQuest` directly (not through `_resolveQuests`).

### 4.3 Time of day

Still inside `_nextTurn`, gated on `isNewRound` (i.e. only advances once per **round**, not once per
individual player turn):
```csharp
if (isNewRound)
{
	if (_activeGaffer.IsDynamicTimeOfDay)
	{
		_env.GameRun.AdventureState.MapState.CurrentTimeOfDayIndex = AdventureHelper.GetNextTimeOfDayIndex(_env.GameRun.AdventureState.MapState.CurrentTimeOfDayIndex, _activeGaffer.GetGafferData().TimeOfDayProfiles);
		eTimesOfDay timeOfDay = AdventureHelper.GetTimeOfDay(_env.GameRun.AdventureState.MapState.CurrentTimeOfDayIndex, _activeGaffer.GetGafferData().TimeOfDayProfiles);
		hasChangedToOrFromNight = (_env.GameRun.AdventureState.MapState.TimeOfDay != eTimesOfDay.NIGHT && timeOfDay == eTimesOfDay.NIGHT) || (_env.GameRun.AdventureState.MapState.TimeOfDay == eTimesOfDay.NIGHT && timeOfDay != eTimesOfDay.NIGHT);
		_env.GameRun.AdventureState.MapState.TimeOfDay = timeOfDay;
		_activeGaffer.AdvanceTimeOfDay();
		await _onWeatherOrTimeOfDayChange(hasChangedToOrFromNight, pIsFirstTurn);
	}
	else
	{
		_env.GameRun.AdventureState.MapState.CurrentTimeOfDayIndex = (_env.GameRun.AdventureState.MapState.CurrentTimeOfDayIndex + 1) % GlobalHeaderViewHelper.TOD_DISPLAY_LENGTH;
		await _onWeatherOrTimeOfDayChange(pHasChangedToOrFromNight: false, pIsFirstTurn);
	}
}
```
(`AdventureDirector.cs:2730-2743`). **All writes target `AdventureState.MapState.*`**, never the
obsolete `AdventureState.TimeOfDay`/`.CurrentTimeOfDayIndex` aliases — reconfirmed by the harness's
own measurement (`DebugVerbCommands.cs:405-408`):
```
/// MapState, NOT AdventureState/GameRunData directly. Both
/// AdventureState.CurrentTimeOfDayIndex and GameRunData.RoundCount are declared
/// [Obsolete("Use MapState....")] and are never written; every assignment in
/// AdventureDirector targets AdventureState.MapState.*. Asserting on the aliases
/// meant this verb's changed= result was comparing dead state against itself.
```
`_onWeatherOrTimeOfDayChange(bool pHasChangedToOrFromNight = false, bool pIsFirstTurn = false)`
(`AdventureDirector.cs:8256`, `async Task`, private) is the downstream hook that reacts to a
weather/time change (lighting, music, enemy activity, etc.) — not itself further probed in this
pass.

### 4.4 Summary table

| Event | Fires when | What advances | Cite |
|---|---|---|---|
| Player turn end | `_doEndTurn()` called | `PlayerComponent.TurnsPlayed`, then recurses into `_nextTurn` | `AdventureDirector.cs:6437` |
| Round advance | `_roundPlayersEntities` drains to 0 or `pForceNextRound` | `MapState.RoundCount++`, `AdventureState.TotalRoundCount++`, action points reset, quest `RoundsLeft` countdown/expiry, skill cooldowns tick | `AdventureDirector.cs:2708-2716`, `3137-3190` |
| Time-of-day advance | once per round (`isNewRound`), not per player turn | `MapState.CurrentTimeOfDayIndex`, `MapState.TimeOfDay` | `AdventureDirector.cs:2730-2743` |
| Chaos tick | driven by `CHAOS_INCREASE`/`CHAOS_REDUCE` world triggers, themselves fired from timeline events scheduled per `ChaosConfig.ChaosFrequency` | `MapState.ChaosState.ChaosHistory` | §3.1 |

---

## 5. ENCOUNTERS

### 5.1 Representation and action taxonomy

`eEncounterActions` — the full enum (confirmed via `-t eEncounterActions`):
```
NONE, VENUE, AMBUSH, SNEAK, SKILL_TEST, DISARM, DUNGEON, ALLURING_POOL, PORTAL, TRIBUTE, DEVOTE,
LOOT, COINS, CHOICE_REWARD, FATE, REST, MEDITATE, RELAX, ADD_TO_CAMP, MARKET, TOWN_SERVICES,
QUEST_BOARD, MERC_GUILD, PET_SHOP, OPEN, TAKE_QUEST, USE_ITEM, BANK_BOX, PROXY, EMBARK, DISEMBARK,
PICKUP_PARTY, LAND, LAUNCH, ACTIVATE, PASS, RETREAT, LEAVE, CLOSE, VIEW_ONLY_CLOSE,
MP_REFUND_FOCUS, MP_ADD_FOCUS, MP_SELECT_ACTION, DIALOG_CONTINUE
```
An encounter's current menu context (`pMenuContext.Action`) is dispatched through a giant
`switch (pMenuContext.Action)` in `AdventureDirector`'s encounter-menu handler, cases starting at
`AdventureDirector.cs:9944` and running through ~`11000` (`VENUE` at `:9944` through `FATE` at
`:10986`), plus a second, smaller switch at `:15188-15198` handling broadcast/network dedup for
`QUEST_BOARD`/`MARKET`/`TOWN_SERVICES`/`MERC_GUILD`/`PET_SHOP` specifically.

### 5.2 Which branches never call `_closeEncounterMenuAsync`

**Confirmed and quoted** — `MARKET`, `QUEST_BOARD`, `TOWN_SERVICES`, `MERC_GUILD`, `PET_SHOP` all end
their case block with `break;` after showing a sub-menu, and **none of them contain a call to
`_closeEncounterMenuAsync`** anywhere in their bodies (verified by direct read of
`AdventureDirector.cs:10203-10360`, no match for `_closeEncounterMenuAsync` in that span):

```csharp
case eEncounterActions.MARKET:
	...
	EncounterMenuViewHelper2.Hide(_canvas2D.rootVisualElement.CachedQ("encounter-menu-container2"));
	_onSelectUICharacter(_uiSelectedCharacter);
	_showAdventureMarketInventory(pMenuContext.EncounterEntity, pMenuContext.ViewingCharacterEntity, pIsRefresh: false, pMenuContext.ViewOnly);
	_showUiSelectedCharacterInventory(pMenuContext.EncounterEntity);
	_doRefreshUIAll();
	break;
case eEncounterActions.QUEST_BOARD:
{
	...
	EncounterMenuViewHelper2.Hide(encounterMenuContainer2);
	QuestBoardMenuViewHelper.Show(questMenu, pMenuContext.EncounterEntity, pMenuContext.ViewingCharacterEntity, _env, _adventureConfig, _gameObjectMaps.Characters, pMenuContext.ViewOnly);
	...
	UIToolkitHelper.RegisterSingleSubmit(QuestBoardMenuViewHelper.BackButton, delegate
	{
		QuestBoardMenuViewHelper.Hide(questMenu);
		_showEncounterMenu(pMenuContext.ViewingCharacterEntity, pMenuContext.EncounterEntity, pMenuContext.ProxyEntity, pMenuContext.ViewOnly, pIsComingFromSecondaryMenu: true);
	});
	break;
}
case eEncounterActions.TOWN_SERVICES:
{
	...
	ServiceMenuViewHelper.Show(visualElement, pMenuContext.EncounterEntity, pMenuContext.ViewingCharacterEntity, pMenuContext.ViewOnly, _env, _gameObjectMaps.Characters);
	...
	UIToolkitHelper.RegisterSingleSubmit(ServiceMenuViewHelper.BackButton, delegate
	{
		ServiceMenuViewHelper.Hide();
		_showEncounterMenu(pMenuContext.ViewingCharacterEntity, pMenuContext.EncounterEntity, pMenuContext.ProxyEntity, pMenuContext.ViewOnly, pIsComingFromSecondaryMenu: true);
	});
	break;
}
case eEncounterActions.MERC_GUILD:
case eEncounterActions.PET_SHOP:
{
	...
	if (encounterComponent.ActionList.Contains(eEncounterActions.MERC_GUILD) || encounterComponent.ActionList.Contains(eEncounterActions.PET_SHOP))
	{
		_renderMercBoard(pMenuContext.ViewingCharacterEntity, pMenuContext.EncounterEntity, pMenuContext.ProxyEntity, pMenuContext.ViewOnly, petShop);
	}
	break;
}
```
(`AdventureDirector.cs:10203-10221` MARKET; `:10224-10265` QUEST_BOARD; `:10267-10336`
TOWN_SERVICES; `:10338-10360` MERC_GUILD/PET_SHOP.)

**By contrast**, `LEAVE`/`CLOSE` and `VIEW_ONLY_CLOSE` are exactly the branches that DO call it
(`AdventureDirector.cs:9977-10011`):
```csharp
case eEncounterActions.LEAVE:
case eEncounterActions.CLOSE:
{
	...
	await _closeEncounterMenuAsync(pMenuContext.EncounterEntity, pMenuContext.ViewOnly, pUnloadRender: true, closeOffturnPlayersPreview);
	...
	await _stopEncounterAsync(pMenuContext.EncounterEntity, pMenuContext.ViewingCharacterEntity, pMenuContext.ViewOnly, pReturnCameraFocus: true, closeOffturnPlayersPreview);
	break;
}
case eEncounterActions.VIEW_ONLY_CLOSE:
	await _closeEncounterMenuAsync(pMenuContext.EncounterEntity, pMenuContext.ViewOnly);
	await _stopEncounterAsync(pMenuContext.EncounterEntity, pMenuContext.ViewingCharacterEntity, pMenuContext.ViewOnly);
	break;
```
`VENUE`, `RETREAT`, and `DUNGEON` also call `_closeEncounterMenuAsync` up front before doing their
own thing (`:9944-9955` VENUE, `:9957-9976` RETREAT, `:10012-10021` DUNGEON).

### 5.3 What a caller must do to recover

`MARKET`/`QUEST_BOARD`/`TOWN_SERVICES`/`MERC_GUILD`/`PET_SHOP` are all designed as **sub-menus that
route back through `_showEncounterMenu(...)` on their own Back button**, not terminal states — e.g.
`QuestBoardMenuViewHelper.BackButton` and `ServiceMenuViewHelper.BackButton` both wire a submit
handler that re-invokes `_showEncounterMenu` with `pIsComingFromSecondaryMenu: true`
(`AdventureDirector.cs:10256-10260`, `:10329-10334`). So for an automated harness, being "stuck" in
one of these sub-menus is only recoverable by:
1. Dispatching a **second** encounter-menu action explicitly of type `LEAVE` or `CLOSE` (or
   `VIEW_ONLY_CLOSE` for a view-only session) against the same `pMenuContext.EncounterEntity` — this
   is the only path that reaches `_closeEncounterMenuAsync`/`_stopEncounterAsync`.
2. Or, if driving actual UI rather than reflection, clicking the sub-menu's own Back button, which
   routes back to `_showEncounterMenu` (still inside the encounter, not closed) — from there a
   `LEAVE`/`CLOSE` action still has to be issued to actually exit.

There is **no reflection-only "force close" shortcut** distinct from calling
`_closeEncounterMenuAsync`/`_stopEncounterAsync` directly (both `private async Task`, instance,
`AdventureDirector.cs:9804` and `:9862` respectively) — a harness that needs to guarantee it isn't
stuck inside a MARKET/QUEST_BOARD/TOWN_SERVICES/MERC_GUILD/PET_SHOP sub-menu should reflect into
`_closeEncounterMenuAsync(Entity pEncounterEntity, bool pViewOnly, bool pUnloadRender = true, bool
pForceCloseOffturnPlayersPreview = true)` directly rather than trying to synthesize a `CLOSE`
menu-context action end-to-end.

---

## 6. MOD_DRIVEABLE

All of the following are already implemented and (per their own doc-comments) **measured live**
against the retail build by the harness at
`FTK2.Crucible/src/Crucible.Plugin/{RunCommands,ChaosCommands,DebugVerbCommands}.cs`. Signatures are
quoted from the decompile; ⚠️ flags every `Task`-returning method that is invoked and **not
awaited**.

| Verb | Method reflected into | Signature | async? | Awaited by caller? | Cite |
|---|---|---|---|---|---|
| Complete a quest objective | `QuestState.CompletedObjectives[i] = true` (field write) then pump | `public bool[] CompletedObjectives` | n/a (field) | n/a | `QuestState.cs:5` |
| Pump quest resolution | `AdventureDirector._tryCompleteQuests()` | `private async Task<bool> _tryCompleteQuests()` | **yes** | ⚠️ **NO** — `RunCommands.TryPump` stores the returned `Task` in `_lastPumpTask` "held ONLY so its outcome can be reported"; never `.Wait()`/awaited | `AdventureDirector.cs:4492`; `RunCommands.cs:276-306` |
| Activate a quest immediately | `QuestHelper.CreateQuestState(string, string, bool, GameRandom)` then `GameRunData.ActiveQuests.Add(...)` | `public static QuestState CreateQuestState(string pQuestID, string pPreviousQuestID = null, bool pInitialized = true, GameRandom pGameRandom = null)` | no (sync) | n/a | `QuestHelper.cs:227` |
| Close/remove a quest | `QuestHelper.CloseQuest(...)` | `public static void CloseQuest(QuestState pQuest, List<Entity> pPlayers, GameRunData pGameRun, List<Entity> pEntitiesToRefresh = null, bool pAddToFailed = true, string pMapID = null)` | no (sync) | n/a | `QuestHelper.cs:321` |
| Advance chaos to a new stage | `AdventureDirector._processWorldTrigger(eWorldTriggers.CHAOS_ACTIVE, <configName>, ...)` | `protected override async Task _processWorldTrigger(eWorldTriggers pTrigger, string pArg, List<(eAbilityResults, object)> pResults = null, QuestState pQuest = null, Entity pPlayerCompleted = null)` | **yes** | ⚠️ **NO** — `ChaosCommands.CrucibleChaosAdvance` invokes and stores the `Task` unwaited, noting "the Task it returns is deliberately not awaited...the caller should re-read `crucible_chaos_state` to confirm nothing downstream is still resolving" | `AdventureDirector.cs:6650`; `ChaosCommands.cs:217-280` |
| Freeze chaos gain (test-soak mode) | Harmony **prefix** on `AdventureHelper.ModifyChaosLevel` (both overloads) that returns `false` to skip the original | `public static void ModifyChaosLevel(ChaosConfig, GameRunData, string, int)` / `private static void ModifyChaosLevel(ChaosConfig, GameRunData, string)` | no (sync) | n/a | `AdventureHelper.cs:2550,2558`; `ChaosCommands.cs:35-83` |
| Advance a turn / end-turn N times | `AdventureDirector._doEndTurn()`, called in a loop | `private async Task _doEndTurn()` | **yes** | ⚠️ **NO** — `DebugVerbCommands.CrucibleTimeAdvance` calls `ReflectionCommands.TryInvoke(...)` per step and does not await; doc-comment: *"`_doEndTurn` returns `Task` and is not awaited...an immediate read can race the async continuation"* | `AdventureDirector.cs:6437`; `DebugVerbCommands.cs:405-458` |
| Complete an objective by index (alt entry point) | field write + `_tryCompleteQuests()` pump | same as row 1/2, addressed by `(questIndex, objectiveIndex)` into `GameRunData.ActiveQuests` | **yes** (the pump) | ⚠️ **NO**, same as row 2 | `DebugVerbCommands.cs:583-635` |
| Read live chaos state | `AdventureState.MapState.ChaosState.{ChaosHistory,MaxChaos,LastChaosRoundAdded,StartedAtRound}`, `MapState.RoundCount` | field reads only | n/a | n/a | `ChaosState.cs:4-20`; `MapState.cs:3` |
| Read run/quest status | `GameRunData.{ActiveQuests,CompletedQuests,FailedQuests,FutureQuests}` | field reads only | n/a | n/a | `GameRunData.cs:7` |

### 6.1 Why the un-awaited `Task`s are a real trap, not a nitpick

Every one of these methods (`_tryCompleteQuests`, `_processWorldTrigger`, `_doEndTurn`) is an
**async instance method whose `Task` is discarded by the reflecting caller**. `RunCommands.cs:47-53`
states the failure mode plainly:
```
/// _tryCompleteQuests is async: an exception inside it faults the returned Task instead of
/// propagating, and nothing observes that Task, so a pump that dies half-way looks exactly
/// like a pump that ran and found nothing to complete. Reporting the Task state in
/// crucible_run_status turns that silence into a message.
```
Practically: a harness that calls one of these methods via `MethodInfo.Invoke` gets a `Task`/`Task<T>`
back immediately (the method has already run synchronously up to its first `await`, per normal C#
async semantics — `ChaosCommands.cs:210-214` notes the `CHAOS_ACTIVE` stage swap specifically
"is the FIRST statement...executed synchronously before any await point...so it has already
happened by the time Invoke returns"), but anything *after* that first `await` — dialogue, reward
distribution, follow-on quest starts, the actual `RoundCount`/`CurrentTimeOfDayIndex` write inside
`_nextTurn` reached via `_doEndTurn`'s tail call — runs asynchronously on the game's own thread with
no signal back to the caller. Every verb in the table above documents this by re-reading state a
"moment later" rather than trusting the immediate return value.

---

## UNVERIFIED

- **`eWorldTriggers` full member list**: the `-t eWorldTriggers` decompile output was captured to
  ~85 members (`TOWN_REFRESH` through `DECREMENT_DUNGEON_MODIFIERS`) but the enum continues beyond
  that in the raw tool output truncation window used for this pass; not all trailing members were
  individually re-verified against a case in `_processWorldTrigger`'s switch. The members actually
  exercised in this document (`CHAOS_ACTIVE`, `CHAOS_REDUCE`, `CHAOS_INCREASE`, `BANNER_EVENT`,
  `SPAWN_TREASURE`, `SCOURGE_REDUCE`, `GATHER_PARTY`, `ZONE_ENCOUNTER_COOLDOWN`) were directly located
  in the switch body and are solid.
- **`_onWeatherOrTimeOfDayChange` internals**: signature and call sites confirmed
  (`AdventureDirector.cs:8256`), body not read in this pass — what specifically changes (lighting
  rig, enemy activity tables, music) is unverified here.
- **Second, network-dedup encounter-action switch** at `AdventureDirector.cs:15188-15198`: confirmed
  to exist and to list `QUEST_BOARD`/`MARKET`/`TOWN_SERVICES`/`MERC_GUILD`/`PET_SHOP` again, but its
  full surrounding logic (multiplayer action broadcast dedup) was not read in depth — flagged here in
  case its handling of "in multiplayer, who actually closes the menu" differs from the singleplayer
  path documented in §5.
- **`_prepareQuestEntitiesAsObjectives`** (`AdventureDirector.cs:4782`, public, non-async) — named and
  signature confirmed, called from `_startQuest`, but its body (how quest objective entities get
  placed on the map) was not read in this pass.
- **`ChaosPostProcessData`** struct/class shape — referenced by `_triggerChaos`/
  `_postProcessModifyChaos` return types, not itself decompiled in this pass.
- Whether `AdventureDirector._closeEncounterMenuAsync`/`_stopEncounterAsync` are reachable via a
  *public* reflection-friendly wrapper anywhere else in the codebase (vs. only as `private` instance
  methods reflected into directly, as documented in §5.3) was not exhaustively searched — a repo-wide
  grep for other callers beyond the encounter-menu switch itself was not performed.

---

FILE_WRITTEN: `C:\Users\ben\repos\ftk2-mods-crucible\docs\research\coverage\quests-world.md`
