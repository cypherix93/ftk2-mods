# FTK2.Questsmith — SPEC

Plugin GUID: `ftk2mods.questsmith` · Id prefix: `QS_` · Priority: P2

## 1. Purpose & scope

For The King II's quest system is already a strong JSON scripting surface: scripted `Quests\*.json` entries
compose objectives, world triggers, dialogue, camera actions, chaining (`NextQuests`/`Conflicts`/
`Requirements`), and branching-choice rewards (`ChoiceRewards`) out of a fixed, code-enumerated verb
vocabulary (see `docs/research/data-schemas.md` §Quest system). Questsmith does two things:

1. **Makes quest content drop-in.** A quest pack is a folder under `data/QuestPacks/<Pack>/`; the engine
   merges its quests/templates/spawners/etc. into the game's `Configs` at load and can inject its
   `QuestTemplates` into existing `QuestBoards` by board id — no code, no touching base-game JSON.
2. **Extends the verb vocabulary.** A small C# registry lets quest authors reference new objective verbs and
   new trigger actions from JSON, each verb's *behavior* is code but its *parameters* are data (a "verbs-config"
   file per pack, the same philosophy as `Behaviours.json` weights or `Abilities.json` action params).

Questsmith deliberately does **not**: replace or rewrite the vanilla quest board/rarity-roll system, add a
quest editor UI, or implement a general-purpose scripting language for objectives — new verbs are a
short, curated, hand-coded launch set (§4), not an arbitrary expression evaluator. It also does not touch
`Adventures\*.json` campaign structure (map zones, game stages) beyond the pack-declared spawners/reward
encounters a quest needs.

## 2. Player-facing behavior

- Installing a quest pack (dropping a folder in `data/QuestPacks/`) makes its quests available through the
  normal quest-board flow — the player sees new bounty/side-quest entries on boards the pack targets,
  with no other change to how boards work.
- Quests written against the new verbs play like any other quest: objective text, camera pans to the
  relevant hex/encounter, dialogue on start/complete — the player cannot tell "SURVIVE_ROUNDS" apart from
  "REACH_HEX" except by what the objective asks them to do.
- The showcase pack (§7) adds three new quests: a race against a rival adventuring party with a
  confront-or-ally choice, an escort-under-siege with a survive-the-finale beat, and a stealth heist with a
  moral-choice ending (steal for gold + Chaos, or refuse for a Life Pool boost).
- No permanent player-visible UI from Questsmith itself; a debug console command (`qs_*`, §8) exists for
  testing/force-injecting quests, off by default in normal play (gated by a knob, §5).

## 3. Architecture

**Engine vs data split.** The C# plugin hardcodes zero quest content and zero verb *parameters*. It
provides: (a) a pack loader/merger that is agnostic to any specific pack's content, (b) a verb/trigger
registry keyed by string id with each launch-set handler reading its tunables from a `verbs-config.json`
block, not from constants. Adding a fourth objective verb later means writing one handler class and
registering it — no changes to the loader, board-injection, or existing verbs.

**Runtime flow:**
1. On `ConfigsHelper.LoadConfigs` (and again on `ConfigsHelper.ReloadConfigs`, both verified — see §6),
   Questsmith scans `data/QuestPacks/*/manifest.json`, resolves `LoadOrder` + `Dependencies` into a
   deterministic load sequence, and for each **enabled** pack (master `Enabled` knob AND per-pack toggle)
   merges its `quests/*.json` into the Quests config, `templates.json` into QuestTemplates, `spawners.json`
   into MapSpawners, and (additional supported kinds beyond the four named in scope) `towerdefenseunits.json`
   into TowerDefenseUnits and `rewardencounters.json` into RewardEncounters — all as straight dict-union
   merges keyed by id, logging a collision loudly (last-loaded-pack-wins by `LoadOrder`) rather than
   silently overwriting. `board-injections.json` is **not** merged into a vanilla config; it is kept in an
   internal `Dictionary<BoardID, List<(TemplateID, Weight)>>` consumed by the board-injection patch (§6).
   `Localization/en.json` merges into the string table (`Lang`, verified patch target). `dialogues/*.json`
   schema is unverified (§11) — merged best-effort, not required for M1.
2. Fail-safe per CONVENTIONS: a pack whose JSON fails to parse is skipped with a loud log line; already-merged
   packs and vanilla content are untouched.
3. When a board is rolled (`QuestHelper.GenerateSideQuestsFromQuestBoardConfig`, verified), a postfix appends
   weighted picks from the internal board-injection table for that `BoardID` on top of whatever vanilla
   produced, scaled by the global `BoardInjectionWeightScale` knob and each entry's own `Weight`.
   `QuestHelper.AddQuestToGameRun` (verified) is postfixed to tag the resulting quest instance with its
   origin pack (for debug/log provenance and for §9's per-instance state keying) — it does not change what
   gets added.
4. Verb dispatch (M2): when the (unverified, §11) objective-evaluation and trigger-execution code paths in
   `QuestHelper` hit a verb/action string it registers a handler for, the handler runs against that
   objective/trigger's params block (looked up by a `paramsId` string from `verbs-config.json`, §4) instead
   of the vanilla switch. Unregistered/vanilla verbs are untouched — Questsmith only ever adds cases, never
   removes them.

**State lifecycle:**
- **Persistent (per-run):** loaded-pack registry, board-injection instance provenance (which BoardID got
  which pack's TemplateID and when), and all live verb runtime state (round counters for `SURVIVE_ROUNDS`/
  `TIMED_CHAIN`, HP snapshots for `PROTECT_ENTITY_HP`, stealth-broken flags for `STEALTH_REACH`, per-tag kill
  counters for `KILL_WITH_TAG`) — piggybacked on `GameRunData` (verified EOR pattern: `GameRunData.Create`
  is a proven patch target for attaching custom per-run containers), keyed by quest instance id + verb id so
  it survives save/load across the whole quest's lifetime, not just the current combat.
- **Per-battle:** the *current combat's* round counter and "combat engaged" flag (for `STEALTH_REACH`) live
  on the plugin and reset at combat end; they get folded into the persistent state above only at the
  moments a verb cares about (round tick, combat end, kill event).
- **Config-load-time (not saved):** the merged pack content itself is `Configs` data, reloaded from disk
  every load/reload — never serialized into the save.

## 4. Data file formats

### 4.1 `manifest.json` (one per pack, at pack root)

```jsonc
{
  "PackId": "QS_PACK_SHOWCASE",       // must match the folder name; becomes the QS debug-command target
  "DisplayName": "Questsmith Showcase Pack",
  "Version": "1.0.0",
  "LoadOrder": 100,                    // lower loads first; ties broken by PackId string sort
  "Dependencies": [],                  // other PackIds that must be present & loaded first; missing dep = pack skipped, logged loudly
  "Enabled": true,                     // pack's own default; AND'd with the BepInEx per-pack toggle (§5)
  "Author": "FTK2.Questsmith",
  "Description": "Reference pack: vanilla-vocabulary depth + the QS launch-set verbs."
}
```

### 4.2 `quests/*.json` — Quests-shaped

One file may hold multiple quest ids (grouped by story arc, matching the vanilla `STORY_1_6_*` grouping
convention). Every field below is the vanilla scripted-quest vocabulary (`docs/research/data-schemas.md`
§Quest system) — Questsmith adds nothing to this shape except that objective/trigger *verb strings* may be
one of the `QS_*` ids from §4.5 in place of a vanilla verb:

```jsonc
{
  "QS_EXAMPLE_QUEST": {
    "Objectives": [ ["REACH_HEX", "SOME_HEX_ID"] ],      // flat [verb, arg] pairs; vanilla verbs: ASSASSINATE_ENTITY, DELIVER, REMOVE_ENCOUNTER, ACTIVATE_ENTITY, REACH_HEX, REACH_RADIUS_LARGE, DUMMY
    "CompleteObjectives": "ALL",                          // ALL | ANY
    "PreDialogueID": "QS_EXAMPLE_QUEST_PRE",
    "PostDialogueID": "QS_EXAMPLE_QUEST_POST",
    "QuestStartWorldTriggers": [ ["DIALOGUE", "QS_EXAMPLE_INTRO"] ],
    "QuestEndWorldTriggers": [],
    "ObjectiveEngageWorldTriggers": [],
    "ObjectiveCompleteWorldTriggers": [],
    "ObjectiveCameraActions": [],                          // ENCOUNTER | ZONE
    "NextQuests": [], "CombatQuests": [], "Requirements": [], "Conflicts": [],
    "DontRemoveObjectivesOnConflict": false,
    "RoundsToExpire": -1,
    "Hidden": false,
    "DisplayOrder": 0,
    "AdventureEndTrigger": false,
    "ChoiceRewards": false,
    "ChoiceRewardsTake": false
  }
}
```

Full worked examples: `data/QuestPacks/QS_PACK_SHOWCASE/quests/*.json` (§7).

### 4.3 `templates.json` — QuestTemplates-shaped

```jsonc
{
  "QS_TPL_EXAMPLE": {
    "QuestID": "QS_EXAMPLE_QUEST",           // -> a quest id from quests/*.json in the same or a dependency pack
    "Type": "BOUNTY",                          // BOUNTY | BOUNTY_WATER | DELIVERY | FETCH | EXPLORATION | PURGE | MYSTERY
    "Args": [],
    "Description": "QS_TPL_EXAMPLE_DESC",     // localization key
    "Rarity": "COMMON",
    "IsCamp": false,
    "ThreatLevel": 3,
    "ObjectiveArgs": [],                       // comma = OR, "+" = AND, e.g. "TWO_BY_TWO+PIRATE"
    "RewardType": "GOLD",                       // GOLD | ITEM | PARTY_XP | REDUCE_CHAOS | LIFE_POOL_UP | NONE
    "RewardsTagQuery": "",
    "RewardItemsCount": 0,
    "LootScale": 1.0,
    "AdventureQuest": false,
    "MysteryReward": false,
    "Tags": []
  }
}
```

### 4.4 `board-injections.json` — new, engine-owned (not a vanilla shape)

Maps existing `QuestBoards.json` board ids to weighted `TemplateID`s this pack wants considered whenever
that board rolls its quest list.

```jsonc
{
  "<BoardID>": [
    { "TemplateID": "QS_TPL_EXAMPLE", "Weight": 5 }
  ]
}
```

`Weight` is an arbitrary positive number scaled at runtime by the `BoardInjectionWeightScale` knob (§5);
it is only meaningful relative to other injected entries for the same board — vanilla's own roll weighting
is untouched.

### 4.5 `verbs-config.json` — engine-owned, parameterizes the M2 verb registry

Two top-level maps, `ObjectiveParams` and `TriggerParams`, each keyed by an arbitrary `paramsId` string
that a quest's `Objectives`/`*WorldTriggers` array references as its second element in place of a plain
vanilla arg (e.g. `["QS_SURVIVE_ROUNDS", "QS_MY_PARAMS_ID"]`). Launch-set shapes:

```jsonc
{
  "ObjectiveParams": {
    "<paramsId>": { "Verb": "SURVIVE_ROUNDS",     "CombatTag": "<tag matched against the combat's quest/encounter tag>", "RoundsRequired": 3 },
    "<paramsId>": { "Verb": "PROTECT_ENTITY_HP",  "EntityTag": "<tagged ally/NPC id>", "MinHpPercent": 50 },
    "<paramsId>": { "Verb": "STEALTH_REACH",      "HexId": "<destination hex>", "AllowFlee": true },
    "<paramsId>": { "Verb": "KILL_WITH_TAG",      "EnemyTag": "<enemy tag>", "Count": 2, "WeaponClass": "BLADE", "DamageType": null },
    "<paramsId>": { "Verb": "TIMED_CHAIN",        "RoundStart": 2, "RoundEnd": 5, "WrappedVerb": "REACH_HEX", "WrappedArg": "SOME_HEX_ID", "ExpireOutsideWindow": true }
  },
  "TriggerParams": {
    "<paramsId>": { "Verb": "GIVE_STATUS_PARTY",  "StatusId": "<StatusEffects.json id>", "Duration": 3 },
    "<paramsId>": { "Verb": "START_MAP_SPAWNER",  "SpawnerId": "<MapSpawners.json id>" },
    "<paramsId>": { "Verb": "SET_WORLD_MODIFIER", "ModifierId": "LIFE_POOL_MAX", "Value": 5 }
  }
}
```

Verb id strings carry the `QS_` prefix in actual quest JSON (`QS_SURVIVE_ROUNDS`, not `SURVIVE_ROUNDS`) to
avoid any collision with vanilla or other mods' verbs — the `"Verb"` field above is the short internal
registry key; the table stays terse by omitting the prefix in this schema doc only.

**Verb semantics (launch set):**
- `SURVIVE_ROUNDS` — complete when the party is still alive `RoundsRequired` rounds after the tagged combat
  (`CombatTag`, matched against whatever tag the encounter/`CombatQuests` chain carries — exact tag source
  is an implementation detail resolved at M2) begins. If `CombatTag` is omitted, it applies to the *next*
  combat engaged after the objective becomes active.
- `PROTECT_ENTITY_HP` — complete when that combat is won AND the tagged entity's HP (sampled at
  combat-end) is `>= MinHpPercent` of its max HP. If the entity died, this cannot complete.
- `STEALTH_REACH` — complete on reaching `HexId` only if no combat was *fought* since the objective became
  active; fleeing a combat (win/loss irrelevant, `AllowFlee`) does not break stealth, engaging and finishing
  one does.
- `KILL_WITH_TAG` — complete after `Count` kills of enemies tagged `EnemyTag`, each kill constrained by
  `WeaponClass` (a `Things.json` `Class` value, e.g. `BLADE`) and/or `DamageType` (an `Abilities.json`
  action `Type` value, e.g. `FIRE`) when those fields are non-null; a kill only counts if every non-null
  constraint matches the killing blow.
- `TIMED_CHAIN` — wraps another verb (`WrappedVerb`/`WrappedArg`, itself a vanilla or `QS_` verb); that
  inner verb is only checkable while the current round is within `[RoundStart, RoundEnd]`. Outside the
  window the objective is either hidden (if `ExpireOutsideWindow:false`) or force-failed
  (`ExpireOutsideWindow:true`).
- `GIVE_STATUS_PARTY` — calls the party-overload of `InteractableHelper.ApplyStatus` (verified) with
  `StatusId`/`Duration`.
- `START_MAP_SPAWNER` — activates a merged `MapSpawners.json` entry by id outside of its normal
  zone-encounter placement. Exact runtime activation entry point is unverified (§11) — M3 milestone.
- `SET_WORLD_MODIFIER` — applies a `StartWorldModifiers`-shaped single entry (`ModifierId`/`Value`, using
  the vocabulary documented for `Adventures\*.json` `StartWorldModifiers`, e.g. `LIFE_POOL_MAX`,
  `EXTRA_LOOT_CHANCE`, `XP_MULTIPLIER`, `GOLD_MULTIPLIER`) mid-run rather than only at adventure start.
  Exact single-entry runtime-apply mechanism is unverified (§11).

### 4.6 `spawners.json` / `towerdefenseunits.json` / `rewardencounters.json` — vanilla-shaped passthroughs

Straight dict-union merges into `MapSpawners`, `TowerDefenseUnits`, and `RewardEncounters` respectively,
using exactly the field vocabulary documented in `docs/research/data-schemas.md` (MapSpawners `Waves[]`
with `SPAWN_TRIGGER`/`SPAWN_UNIT`/`QUEST_PARENT`/etc.; TowerDefenseUnits `SpawnType`/`ReachTargetTriggers`/
`CollideTriggers`/etc.; RewardEncounters `EncounterType`/`SpawnConditions`/`DecayConditions`/`StartQuest`/
etc.). These two extra kinds are not in the scope's headline list of four pack files but are needed by the
showcase content (§7) and are supported by the same generic id-keyed merge — adding a new mergeable kind
later is a one-line registry entry in the loader, not a new code path.

### 4.7 `dialogues/*.json` and `Localization/en.json`

`Localization/en.json` follows the repo-wide EOR-style `ID`/`ID_DESCRIPTION` key convention (per
CONVENTIONS.md Naming) and merges into the game's string table (`Lang`, verified patch target). The
`Dialogues\*.json` schema is **not documented** in `docs/research/data-schemas.md` (mentioned only by
filename in the feasibility survey) — Questsmith ships a best-effort placeholder shape for the showcase
pack and treats real dialogue-file support as unverified pending decompile (§11); dialogue ids referenced
by `PreDialogueID`/`PostDialogueID`/`EngageDialogue`/etc. in the showcase quests still work today by falling
back to plain localization-key text if no dialogue tree is found, which is why M1 does not depend on this.

## 5. Knobs

- `[Questsmith] Enabled` (bool, `true`) — master switch; off = no pack scanning, no patches active beyond
  the no-op check itself.
- `[Questsmith] VerboseLogging` (bool, `false`) — pack load/merge/board-injection decisions at `LogLevel.Debug`.
- `[Questsmith] VerbVerboseLogging` (bool, `false`) — separate switch for M2 verb-dispatch decisions
  (per-tick objective checks are hot-path-adjacent; kept off the general log by default).
- `[Questsmith] BoardInjectionWeightScale` (float, `1.0`) — global multiplier applied to every
  `board-injections.json` `Weight` across all packs; a quick dial for "how often do modded quests show up."
- `[Questsmith] DebugCommandsEnabled` (bool, `true` in dev builds / `false` recommended for release) — gates
  the `qs_*` console commands (§8).
- `[Questsmith.Packs] <PackId>_Enabled` (bool, `true`) — one dynamically-registered entry per pack discovered
  at startup (BepInEx supports adding config entries at runtime); AND'd with the pack's own manifest
  `Enabled` default and the master switch.

## 6. Patch targets & integration points

All class/method names below are verbatim from `docs/research/game-code-reference.md`. Items marked
**(unverified)** are the open questions in §11 — the spec proceeds with a stated best-effort design and
flags the exact interception point as pending decompile, per the task's explicit instruction.

| Target | Kind | Why |
|---|---|---|
| `ConfigsHelper.LoadConfigs` | Postfix | Scan `data/QuestPacks/*`, resolve load order/deps, merge all pack content into `Configs` (§3 step 1). |
| `ConfigsHelper.ReloadConfigs` | Postfix | Same merge on hot-reload (DevKit loop, CONVENTIONS testing philosophy). |
| `QuestHelper.GenerateSideQuestsFromQuestBoardConfig` | Postfix | Append weighted board-injection picks for the rolled `BoardID` (§3 step 3, §4.4). |
| `QuestHelper.AddQuestToGameRun` | Postfix | Tag the added quest instance with its origin pack for debug provenance / verb-state keying; does not change selection. |
| *(unverified)* the `QuestHelper` objective-evaluation method that matches an objective's verb string | Prefix | **M2 core.** Try the `QS_*` verb registry first; if a handler is registered for the verb, run it and short-circuit; otherwise fall through to vanilla. Exact method name/signature not present in `game-code-reference.md` — needs a decompile pass before implementation. |
| *(unverified)* the `QuestHelper` (or sibling) world-trigger-action execution method | Prefix | **M2 core.** Same pattern for `GIVE_STATUS_PARTY`/`START_MAP_SPAWNER`/`SET_WORLD_MODIFIER`. Same caveat. |
| `CombatPhase._addEntityToCombat` | Postfix | Mark "combat engaged" (breaks `STEALTH_REACH`) and start the per-battle round counter feeding `SURVIVE_ROUNDS`/`TIMED_CHAIN`. |
| `CombatHelper.NextTurn` | Postfix | Round-tick source for `SURVIVE_ROUNDS`/`TIMED_CHAIN` window checks. |
| `CombatHelper.ApplyAction` | Postfix | Observe `eCombatActions.CHANGE_STAT` entries (has `Type`/damage sign) plus entity-death to attribute kills for `KILL_WITH_TAG`, and to snapshot HP for `PROTECT_ENTITY_HP` at combat end. |
| `InteractableHelper.CalculateFinalDamage` | Postfix | Cross-check weapon-class/damage-type attribution on the killing blow for `KILL_WITH_TAG` (already an EOR patch target for the same "what hit what with what" question). |
| `InteractableHelper.ApplyStatus` (party overload) | Direct call (not a patch — engine calls it) | Implementation of `GIVE_STATUS_PARTY`. |
| `AdventureHelper.InitializeWorldModifiers` | Reference only, not patched at M2 | Confirms the `StartWorldModifiers` data shape `SET_WORLD_MODIFIER` reuses; the *mid-run single-modifier apply* entry point is unverified (§11) — M3. |
| `CommandLineHelper.ExecuteCommand` | Postfix | `qs_list_packs`, `qs_reload_packs`, `qs_force_quest <QuestID>`, `qs_inject_pack <PackId>` debug commands (§8). |
| `Lang.__t` / `Lang.SetLanguage` | Postfix / merge point | Localization merge for pack `Localization/*.json`. |
| `GameRunData.Create` | Postfix | Attach the Questsmith per-run state container (loaded-pack registry, board-injection provenance, verb runtime state) to new runs; load existing container on resume. |
| `AdventureDirector._handleNetworkAction` | Reference (M3) | Piggyback point for `QS_SYNC_*` custom network actions propagating verb-state changes to MP clients (EOR precedent: `EOR_SYNC_*`). |

## 7. Example starting dataset

`data/QuestPacks/QS_PACK_SHOWCASE/` — one pack, three quest chains, exercising every launch verb and both
vanilla-only and QS-verb paths:

- **`manifest.json`** — `PackId: QS_PACK_SHOWCASE`, no dependencies, `LoadOrder: 100`.
- **`quests/QS_RIVAL_COMPANY.json`** — *"The Rival Company."* A bounty race: `QS_RIVAL_COMPANY_START`
  (`ASSASSINATE_ENTITY` the bounty target, `RoundsToExpire: 6`) races a `RewardEncounters` entry
  (`QS_RIVAL_PARTY`, §below) representing the rival company; if the player is too slow, the encounter's
  `StartQuest` fires `QS_RIVAL_COMPANY_RIVAL_WINS` (`Conflicts` closes the player's quest). Either branch
  funnels into `QS_RIVAL_COMPANY_CHOICE` (`ChoiceRewards: true`) — **Confront** (M2:
  `KILL_WITH_TAG` 2 rival mercs with a `BLADE`; M1 fallback: `ASSASSINATE_ENTITY` the rival captain) vs.
  **Ally** (peaceful split, vanilla `GIVE_THING` reward). Demonstrates `RewardEncounters` spawn+expiry racing
  a scripted quest, and `Conflicts`/`ChoiceRewards` branching.
- **`quests/QS_HOLD_THE_LINE.json`** — *"Hold The Line."* Escort/siege: `QS_HOLD_THE_LINE_START` requires
  (`CompleteObjectives: ALL`) both `ACTIVATE_ENTITY` on the escorted convoy (a `TowerDefenseUnits` entry,
  `QS_CARAVAN_CONVOY`, reaching its destination) and `REMOVE_ENCOUNTER` on the raider assault spawner
  (`QS_HOLD_THE_LINE_ASSAULT`, MapSpawners waves). Chains via `CombatQuests` into
  `QS_HOLD_THE_LINE_FINALE` — M1 fallback `ASSASSINATE_ENTITY` the raider chief; **M2** swaps in
  `SURVIVE_ROUNDS` (3 rounds of the finale) `+` `PROTECT_ENTITY_HP` (convoy above 50%), with
  `GIVE_STATUS_PARTY` (battle-prep buff) and `START_MAP_SPAWNER` (reinforcement wave) firing on finale
  start. **Note the exact `TowerDefenseUnits.SpawnType` semantics for a neutral escorted convoy are unverified
  (§11) — `QS_CARAVAN_CONVOY` provisionally uses `TOWER_DEFENSE`.**
- **`quests/QS_QUIET_JOB.json`** — *"The Quiet Job."* Stealth heist: `QS_QUIET_JOB_START` — M1 fallback
  `REACH_HEX` the vault; **M2** swaps in `STEALTH_REACH` (`AllowFlee: true`). Ends in
  `QS_QUIET_JOB_CHOICE` (`ChoiceRewards`) — **Take the gold** (vanilla `GIVE_THING` + `CHAOS_ACTIVE`, both
  verified vanilla trigger actions) vs. **Refuse** (**M2**: `SET_WORLD_MODIFIER` with `ModifierId:
  LIFE_POOL_MAX` — the new-verb equivalent of the `LIFE_POOL_UP` reward flavor).
- **`templates.json`** — one `QuestTemplates`-shaped entry per chain's entry quest (`QS_TPL_RIVAL_COMPANY`,
  `QS_TPL_HOLD_THE_LINE`, `QS_TPL_QUIET_JOB`) so the board-roll system can surface them.
- **`board-injections.json`** — injects all three templates into placeholder board ids (`TOWN_BOARD_DEFAULT`,
  `TOWN_BOARD_CAMP` — **actual `QuestBoards.json` board ids must be confirmed from the shipped file before
  release, §11**) with hand-tuned weights.
- **`spawners.json`** — `QS_HOLD_THE_LINE_ASSAULT` (raider waves) and `QS_HOLD_THE_LINE_REINFORCEMENT_WAVE`
  (the M2 finale reinforcement, activated by `START_MAP_SPAWNER` rather than placement).
- **`towerdefenseunits.json`** — `QS_RAIDER` (attacking wave unit) and `QS_CARAVAN_CONVOY` (escorted target).
- **`rewardencounters.json`** — `QS_RIVAL_PARTY` (the rival company's race-clock encounter).
- **`verbs-config.json`** — every `ObjectiveParams`/`TriggerParams` entry referenced by the quests above.
- **`dialogues/QS_PACK_SHOWCASE.json`** — placeholder dialogue text for all `*DialogueID` references (best
  effort, §4.7).
- **`Localization/en.json`** — every id/description string the pack references.

This dataset alone must prove: pack discovery+merge, board injection with weights, `RewardEncounters` racing
a quest, `Conflicts`/`ChoiceRewards` branching (vanilla depth), and all five launch verbs +
all three launch trigger actions (new-verb depth), each verb used at least once.

## 8. Testing plan

Primary loop per CONVENTIONS: edit data → `FTK2.DevKit` hot-reload (or restart until DevKit exists) →
observe in-game. Concrete steps (all executable in well under 15 minutes with the shipped showcase pack):

1. Start any side adventure. Confirm the log shows `[Questsmith] Target found: ...` for every patch target
   in §6 and `Pack loaded: QS_PACK_SHOWCASE (LoadOrder=100)`.
2. Run `qs_list_packs` — confirm `QS_PACK_SHOWCASE` is listed enabled.
3. Run `qs_force_quest QS_RIVAL_COMPANY_START` (bypasses board rarity rolls). Play it twice:
   - Race and win before `RoundsToExpire` — verify `QS_RIVAL_COMPANY_PLAYER_WINS`/choice path opens.
   - Deliberately dawdle past the expiry — verify `QS_RIVAL_PARTY` resolves first, `QS_RIVAL_COMPANY_RIVAL_WINS`
     fires and closes the player's quest via `Conflicts`.
   - From the resulting `QS_RIVAL_COMPANY_CHOICE`, take **Confront** once (M2 build: verify `KILL_WITH_TAG`
     tracks only `BLADE` kills against `QS_RIVAL_MERC`-tagged enemies) and **Ally** once on a second run.
4. Run `qs_force_quest QS_HOLD_THE_LINE_START`. Let the raider waves spawn; verify `REMOVE_ENCOUNTER`
   completes when the spawner is cleared and `ACTIVATE_ENTITY` completes when the convoy reaches its
   destination (test both orders). At the finale: M1 build kills the raider chief directly; M2 build must
   survive 3 rounds with the convoy's HP staying ≥ 50% — test one run where the convoy is allowed to drop
   below 50% (objective must fail even if the party survives) and one clean run.
5. Run `qs_force_quest QS_QUIET_JOB_START`. M1: walk to the vault, combats allowed, objective still
   completes. M2: repeat with `STEALTH_REACH` — verify fleeing an ambush along the way does not break
   stealth but finishing a fight does (retry from a save). At the choice: take gold (verify `CHAOS_ACTIVE`
   fires) and refuse on a separate run (verify the `SET_WORLD_MODIFIER LIFE_POOL_MAX` bump applies).
6. Edge cases to explicitly hit: two packs declaring the same `TemplateID` (expect a loud collision log,
   last-`LoadOrder` wins); a pack with a missing `Dependencies` entry (expect the pack to be skipped, not
   crash); disabling a pack via its BepInEx knob mid-run while one of its quests is active (expect the
   active quest instance to keep working — only future board rolls/new-quest-starts are affected);
   `SURVIVE_ROUNDS` when the tagged combat ends (win or flee) before `RoundsRequired` elapses (must not
   complete); `KILL_WITH_TAG` when a kill comes from a DOT/status tick rather than a direct hit (define via
   `DamageType` matching the status's `Type`, `WeaponClass` simply never matches non-weapon kills).
7. Log everything above at `LogLevel.Debug` behind `VerboseLogging`/`VerbVerboseLogging`; a passing test run
   should be fully reconstructable from the log alone.

## 9. Save & multiplayer considerations

- Pack content (quests/templates/spawners/etc.) is `Configs` data — identical across a save, but **must be
  identical across MP peers** (same pack files, same versions) or boards/quest content will desync, exactly
  like EOR's config+data hash handshake (`EOR_CFG`/`EOR_DAT`) that this repo's CONVENTIONS calls out as the
  precedent. Questsmith ships single-player-first; an MP-safe posture requires either (a) all peers run
  identical pack sets — recommended default — or (b) host-authoritative quest/verb state with `QS_SYNC_*`
  network actions (piggybacking the verified `AdventureDirector._handleNetworkAction`) propagating verb
  round counters/HP snapshots/kill counts to clients, deferred to M3.
- Verb runtime state (§3) is per-run persistent via `GameRunData` and must be evaluated host-side only in
  MP to avoid two peers disagreeing on whether e.g. `SURVIVE_ROUNDS` completed — clients render the result,
  they don't compute it. This mirrors the CONVENTIONS default ("AI-side mods: host-authoritative").
  Single-player is unaffected by this distinction.
- Board injection is deterministic given the same pack set + `BoardInjectionWeightScale`, so it does not by
  itself introduce desync as long as the roll happens host-side and the result is transmitted like any
  other board-roll (vanilla mechanism, unmodified).

## 10. Milestones

- **M1 — Pack loader + vanilla-verb showcase.** Manifest parsing, load order/dependency resolution, merge
  of `quests/templates/spawners/towerdefenseunits/rewardencounters/board-injections/localization`, the
  board-injection weighted-append patch, `qs_list_packs`/`qs_reload_packs`/`qs_force_quest`/`qs_inject_pack`
  debug commands. The three showcase quests play through both branches end-to-end using only vanilla verbs
  (the M1 fallback objectives called out in §7) — `ASSASSINATE_ENTITY`, `REACH_HEX`, `REMOVE_ENCOUNTER`,
  `ACTIVATE_ENTITY`, `DUMMY`, `Conflicts`, `ChoiceRewards`, `RewardEncounters` racing. Independently shippable:
  a pack-only mod with zero new verbs is a complete, useful mod.
- **M2 — Verb engine.** The `QS_*` objective-verb and trigger-action registry, the (currently unverified,
  §11) dispatch interception points, and the full launch set: `SURVIVE_ROUNDS`, `PROTECT_ENTITY_HP`,
  `STEALTH_REACH`, `KILL_WITH_TAG`, `TIMED_CHAIN` objective verbs; `GIVE_STATUS_PARTY`,
  `START_MAP_SPAWNER`, `SET_WORLD_MODIFIER` trigger actions. Showcase quests swap their M1 fallback
  objectives for the M2 verbs noted in §7, wired to `verbs-config.json`.
- **M3 — Spawner/TD integration polish.** Verified, patched (not just referenced) runtime activation for
  `START_MAP_SPAWNER` and mid-run `SET_WORLD_MODIFIER`; `QS_SYNC_*` MP propagation for verb state;
  board-injection weight tuning pass against real `QuestBoards.json` ids; TDU escort polish (e.g. wave
  intensity scaling with how long the player dawdled, echoing the Rival Company race clock).

## 11. Open questions

1. **Configs field names.** `Configs.Quests`, `Configs.QuestTemplates`, `Configs.QuestBoards`,
   `Configs.MapSpawners`, `Configs.TowerDefenseUnits`, `Configs.RewardEncounters` are assumed by pattern
   from the one confirmed case (`Behaviours.json → Configs.Behaviours`) but are not individually
   decompile-verified — `game-code-reference.md`'s `Configs` field list is explicitly partial ("~57 fields
   … `…`"). Verify exact field names before implementing the merge step.
2. **Verb/trigger dispatch interception point.** The exact `QuestHelper` method(s) that evaluate an
   objective's verb string and execute a world-trigger's action string are not named in
   `game-code-reference.md` (only `GenerateSideQuestsFromQuestBoardConfig`/`AddQuestToGameRun` are
   confirmed `QuestHelper` patch precedents, and those are about board generation / run attachment, not
   verb evaluation). This is the single riskiest unknown for M2 and needs a decompile pass before any verb
   code is written.
3. **`Dialogues\*.json` schema** is not documented anywhere in `docs/research/`. §4.7's shape is a
   placeholder; confirm the real schema (or confirm dialogue ids degrade gracefully to plain text, which
   M1's design currently assumes) before relying on rich dialogue in shipped quests.
4. **Real `QuestBoards.json` board ids.** No example ids are given in the docs; `board-injections.json`'s
   `TOWN_BOARD_DEFAULT`/`TOWN_BOARD_CAMP` are placeholders pending inspection of the shipped file.
5. **`TowerDefenseUnits.SpawnType` semantics for a neutral/escorted unit.** The documented values
   (`TOWER_DEFENSE`, `HORDE_DEFENSE`, `HUNT_NEAREST`) all read as hostile/aggressive-unit flavors; whether
   the system supports a friendly convoy that simply paths to a destination (as `QS_CARAVAN_CONVOY` assumes)
   or whether "escort" must be represented some other way is unconfirmed.
6. **Mid-run single-modifier apply for `SET_WORLD_MODIFIER`.** `AdventureHelper.InitializeWorldModifiers`
   is confirmed but (by its name) looks like a whole-list, adventure-start operation; whether a single
   `StartWorldModifiers`-shaped entry can be applied mid-run through the same or a sibling method is
   unverified.
7. **`START_MAP_SPAWNER` runtime activation.** No runtime class/method for activating a `MapSpawners.json`
   entry outside of normal zone-encounter placement is documented; needs a decompile pass (likely a
   `MapSpawnerHelper`-equivalent, name unconfirmed).
8. **Exact `ChoiceRewards` reward-entry field names.** The docs confirm the *booleans* (`ChoiceRewards`,
   `ChoiceRewardsTake`) and the *text tokens* (`[CHOICE_CANCEL]`, `[ON_VALID_CHOICE_TRIGGER]` →
   `RESTART_QUEST`/`CLOSE_QUEST`, reference `STORY_1_6_OFFERING_CHOICE`) but not the JSON field that houses
   the reward option list on a scripted quest. §7's quests model this as a small set of sibling quests
   chosen via the token pattern (mirroring the documented reference) rather than guessing an undocumented
   inline rewards-array shape — decompile `STORY_1_6_OFFERING_CHOICE` to confirm/replace before M1 ships.
9. **Granting `LIFE_POOL_UP`-style rewards from a hand-scripted quest.** `LIFE_POOL_UP` is documented as a
   `QuestTemplates.RewardType` value and separately as a `Wheels.json` wedge verb, not as a `Quests\*.json`
   world-trigger action. The Quiet Job's refuse-branch works around this by using our own `SET_WORLD_MODIFIER`
   (`LIFE_POOL_MAX`) instead — confirm whether a more direct vanilla mechanism exists.
10. **Single-player vs required-MP-parity default.** Per `docs/feasibility.md` §13, the repo-wide question
    of how strictly MP must be supported is still open; §9 assumes single-player-first with an opt-in
    host-authoritative MP path deferred to M3.
