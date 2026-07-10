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
- `[Multiplayer] OnParityMismatch` (enum: `Inherit`, `WarnAndSafeMode`, `WarnOnly`, `Block`; default
  `Block`) — overrides the repo-wide ParityService default (`docs/MULTIPLAYER.md` R1) because Questsmith's
  in-flight quest state can reference config ids that don't exist on a diverging peer (§9.5). `Inherit`
  is available for operators who want the repo-wide default instead.

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
8. **MP smoke test.** Host+client session, both with `QS_PACK_SHOWCASE` installed and identical data;
   play a showcase quest through a branch choice (`QS_RIVAL_COMPANY_CHOICE`) and dump-compare quest-instance
   state — must be identical on both peers. Then verify a mid-session client join against an in-progress
   quest, and a parity-mismatch join (`Block` must engage). Full procedure: §9.6.

## 9. Save & multiplayer considerations

Structure and rules (R1–R5) per `docs/MULTIPLAYER.md`, the repo-wide MP architecture — co-op multiplayer
is a **hard requirement**, not an opt-in posture. This section is Questsmith's binding of that architecture.

### 9.1 Parity class

**`ALL_PEERS`.** Unlike a host-only-computation mod, this isn't contingent on any unresolved decompile
question: pack content (quests/templates/spawners/TowerDefenseUnits/RewardEncounters/board-injections/
verbs-config) merges straight into `Configs` at load (§3 step 1), and R1 states the rule plainly — "the
game simulates from `Configs`, and our mods merge into `Configs`" is exactly Questsmith's core mechanism.
Every peer needs the same pack set, same versions, same `verbs-config.json` data, or a board could offer a
quest that only exists on one peer, or a verb (`SURVIVE_ROUNDS`, etc.) could evaluate against parameters
the other peer doesn't have. `Localization/*.json` and `dialogues/*.json` are excluded from the parity
`dataHash` per R1's text-file carve-out.

### 9.2 Feature table

| Feature | `[SYNCED]`/`[LOCAL]` | Authority |
|---|---|---|
| Pack merge (quests/templates/spawners/TDU/RewardEncounters) into `Configs` | `[SYNCED]` | `ALL_PEERS` data via R1 parity — not runtime transmission |
| `board-injections.json` weighted table | `[SYNCED]` | `ALL_PEERS` data; the roll itself is host-decided (§9.4) |
| Board-roll weighted injection pick | `[SYNCED]` | Host-decided; effect carried by vanilla board-state replication (recommended) or `QS_SYNC_BOARD_V1` (fallback) — §9.4 |
| Quest instance state (active/complete/objective progress) for vanilla verbs | `[SYNCED]` | Vanilla quest-completion pipeline — the backbone (§9.4) |
| `QS_*` verb evaluation (`SURVIVE_ROUNDS`, `PROTECT_ENTITY_HP`, `STEALTH_REACH`, `KILL_WITH_TAG`, `TIMED_CHAIN`) | outcome `[SYNCED]` / computation `[LOCAL]` | Host-only computation (R3); outcome flows through the vanilla quest-completion pipeline |
| Verb progress display text (e.g. "3/5 rounds") | `[SYNCED]` | Host, via vanilla objective-progress field (recommended) or `QS_SYNC_VERBPROGRESS_V1` (fallback) — §9.4 |
| Trigger actions (`GIVE_STATUS_PARTY`, `START_MAP_SPAWNER`, `SET_WORLD_MODIFIER`) | `[SYNCED]` | Host-only invocation; effect is vanilla status/spawner/world-modifier state, already replicated |
| Origin-pack provenance tag on a quest instance | `[LOCAL]` | Debug/log provenance only; parity-exempt, R4 |
| `VerboseLogging` / `VerbVerboseLogging` | `[LOCAL]` | Any peer; presentation-only, R4 |
| `qs_*` debug console commands | mutating | `DebugCommandsEnabled`; a dev mutation per R5 — hard-gated in MP unless `ForceAllowInMP`, host-only even then |

### 9.3 Determinism inventory (R2)

- **Pack merge order** (`LoadOrder` + dependency resolution, §3 step 1) is already a pure function of
  manifest data — stable sort, no runtime randomness. Satisfies R2 for free.
- **Board-injection weighted pick** is the one true "roll" Questsmith performs at runtime (§3 step 3).
  This spec did not previously pin down its RNG source — closing that gap is this revision's R2
  requirement: the pick **must** use the game's deterministic `GameRandom` (EOR `EOR_SHARED_RNG`
  pattern), never a local `System.Random`, regardless of which sync design (§9.4) is chosen for the
  *result*.
- **Verb runtime state** (round counters, HP snapshots, stealth flags, kill counts) is not a roll — it's
  a deterministic accumulation of host-observed combat events (§3, State lifecycle). No RNG is involved;
  R2 is satisfied by host-only computation (R3), not by shared-RNG reproduction.
- **Generated ids:** none. Questsmith mints no runtime ids — pack content ids are author-supplied and
  fixed at config-load time.

### 9.4 Sync surface

**The backbone.** Vanilla quest state is config-referenced by id: which quest is active, which
objectives are complete, chain transitions (`NextQuests`/`Conflicts`), and `ChoiceRewards` resolution are
all part of the game's own quest-run state, which vanilla MP must already replicate for co-op questing to
function at all. This is Questsmith's single biggest lever, and it costs zero new code: with identical
`Configs` on every peer (R1), a `QS_`-prefixed quest is exactly as replicable as a `STORY_1_6_`-prefixed
one — the replication layer can't tell the difference.

**Contrast with EOR's precedent — stated explicitly as a design goal.** EOR's fallback for its own custom
quest boards/legendary contracts in MP is to disable them, syncing only quest *archetypes* host→client via
`EOR_MP_QUEST_ARCHETYPE` — proof that FTK2's netcode can carry custom quest-content identifiers, but a
choice not to build full custom-quest MP support on top of that proof. **Questsmith's improvement:
parity-gated enable, not MP-disable.** Once R1 parity is confirmed, QS content runs exactly as it does in
single-player — no MP-only feature subset, no archetype-only degraded mode. `EOR_MP_QUEST_ARCHETYPE`'s
existence is direct evidence this is achievable; Questsmith actually needs *less* machinery than EOR
built, because config-parity-hashing (R1) does the job EOR's archetype-sync was working around.

**Board injection — deterministic `GameRandom` vs. host-decides + `QS_SYNC_BOARD_V1` (analysis and
recommendation).** Two designs satisfy R2 for the weighted pick:
  - **(a) Shared deterministic `GameRandom`:** every peer independently computes the same board roll and
    arrives at the identical weighted pick from identical pack data. Needs no network message, but only
    works if every peer that needs the result actually executes the roll (i.e. board generation runs
    per-peer, not host-only).
  - **(b) Host-decides + syncs (`QS_SYNC_BOARD_V1`):** a host→clients snapshot, `{BoardID,
    [TemplateID...]}`, idempotent, fired once per board roll, explicitly carrying the composed board
    list. Needed only if the host-only roll's result does *not* already ride vanilla's own board-state
    replication.

  **Recommendation:** neither in isolation. Default to **host-side execution using `GameRandom`** for the
  pick (closes R2 regardless of the answer below) **and rely on the backbone** — vanilla board-state
  replication of the postfix's modified return value — **rather than adding a custom sync action**, per
  `docs/MULTIPLAYER.md`'s stated preference ("host-decides + vanilla-replicates over custom sync; custom
  `_SYNC_` actions are a last resort"). This is contingent on two unverified facts, both tracked in §11:
  (i) whether `QuestHelper.GenerateSideQuestsFromQuestBoardConfig` runs host-only in vanilla MP (strong
  prior, mirroring the analogous AI-decision open question other specs face); and (ii) whether a Harmony
  postfix's modified return value is what the caller reads when it performs board-state replication, or
  whether replication has already happened before the postfix runs. If either resolves unfavorably,
  `QS_SYNC_BOARD_V1` becomes the required fallback — its shape is specified above precisely so
  implementation isn't blocked on the decompile pass.

**Verb engine display (M2).** Objective *completion* is host-computed (R3: `SURVIVE_ROUNDS` round
counting, `PROTECT_ENTITY_HP` HP checks, and `STEALTH_REACH` detection all read host-simulated
combat/round state) and applied through the same vanilla quest-completion call a vanilla verb's match
would use — that call is what replicates, so clients never need a custom completion-sync action. What
clients *display* mid-objective (e.g. "3/5 rounds", "convoy HP: 72%") is a separate question:
  - If vanilla's quest-log UI already reads a numeric/text progress field off quest-instance state for
    objectives that have counts, and that field rides the same replicated instance state as completion,
    piggybacking QS verb progress onto that field is free — no new sync action, consistent with the
    backbone. **Unverified** — needs a decompile pass on the quest-log progress-display method; tracked
    in §11.
  - If no such field exists (or it doesn't replicate), the fallback is `QS_SYNC_VERBPROGRESS_V1`: a small
    host→client snapshot per active quest instance carrying a `QS_*` verb, `{QuestInstanceID, VerbID,
    Current, Target, DisplayText}`, fired on round-tick/state-change (throttled, not every tick),
    idempotent (clients just overwrite their local display cache), with a
    `QS_SYNC_VERBPROGRESS_REQUEST_V1` counterpart for late-joiners (`docs/MULTIPLAYER.md`'s mid-session
    join guidance).
  - **Recommendation:** attempt the vanilla-field piggyback first (cheapest, matches the backbone); design
    and ship `QS_SYNC_VERBPROGRESS_V1` as the guaranteed fallback so M2 isn't blocked on the decompile
    answer either way.

**Trigger actions** (`GIVE_STATUS_PARTY`, `START_MAP_SPAWNER`, `SET_WORLD_MODIFIER`) are host-invoked only
(R3) and their effects are vanilla state (status effects, spawner activation, world modifiers) that
already replicates through whatever mechanism vanilla itself uses for those systems — no QS-specific sync
needed.

**ParityService registration (R1).** Questsmith registers `(ftk2mods.questsmith, Version, dataHash,
enabledFeatures)` with the shared ParityService (FTK2.DevKit) at startup/session join. `dataHash` =
SHA-256 over every merged pack file (`manifest.json`, `quests/*.json`, `templates.json`,
`board-injections.json`, `verbs-config.json`, `spawners.json`, `towerdefenseunits.json`,
`rewardencounters.json`) across every enabled pack, sorted file order, normalized line endings, invariant
culture — `Localization/*.json` and `dialogues/*.json` excluded per R1. `enabledFeatures` = the resolved
per-pack enabled set (master switch AND per-pack knob AND manifest default) plus an M1/M2/M3 capability
flag, so a host on M2 and a client still on an M1 build fail parity cleanly instead of silently falling
back per-verb.

### 9.5 SafeMode definition

The repo default (`WarnAndSafeMode`) is **not** Questsmith's default — see the recommendation below. When
SafeMode does engage (an operator explicitly loosens the policy):
- **No new QS quest is offered.** The board-injection postfix becomes a no-op. Unconditional and safe —
  it only prevents *future* content.
- **In-flight QS quests are the hard case.** A quest instance already active before the mismatch was
  detected references QS-owned config ids (the quest itself, its template, its `verbs-config` `paramsId`)
  that may not exist — or may differ — on the diverging peer. Three options considered:
  - *Freeze* (pause objective evaluation/timers): doesn't resolve anything — `RoundsToExpire`/round-window
    verbs (`TIMED_CHAIN`) have no clean vanilla-supported pause, and the quest still renders UI that needs
    the same config data.
  - *Auto-fail* (force-close in-flight QS quests): destructive to player progress, and the quest-close
    path itself may need to resolve the same config ids it's trying to escape — not guaranteed safe on the
    very peer that's missing them.
  - *Allow-completion* (let existing instances finish under pre-mismatch rules, block only new ones):
    works when the divergence is a version/tuning difference and the content still exists on every peer —
    but a mismatch can just as easily mean a pack is *entirely missing* on one peer, where there is
    nothing left to finish.
  - **Recommended default: `Block`.** In-flight quests make the failure mode player-visible and
    potentially unrecoverable (missing config lookups, unresolvable verb `paramsId`), so Questsmith
    overrides the repo-wide `OnParityMismatch` default and ships `[Multiplayer] OnParityMismatch = Block`
    (§5) — refuse to start/continue an MP session on a Questsmith mismatch rather than ever entering a
    state with unresolvable in-flight quest data. This is stricter than the repo default specifically
    because, unlike a purely host-computed mod, Questsmith can have live per-instance state that
    *requires* its own config data on every peer just to keep functioning.
- Pack scanning/merge (§3 steps 1-2) still fails safe per-peer (a bad JSON file only degrades that
  peer's own load) — but under `Block`, that peer's ParityService registration diverges and blocks the
  session before any QS quest is ever offered, which is the point.

### 9.6 MP test plan

1. Launch host + one client, both with `QS_PACK_SHOWCASE` installed, identical versions,
   `VerboseLogging`/`VerbVerboseLogging` on.
2. On the host, `qs_force_quest QS_RIVAL_COMPANY_START`. Play to the `QS_RIVAL_COMPANY_CHOICE` branch
   with the client observing/participating; take **Confront**. Dump quest-instance state on both peers
   (active quest id, objective completion flags, chosen branch) — must be identical.
3. Repeat with `QS_HOLD_THE_LINE_START` through its M2 verbs (`SURVIVE_ROUNDS` + `PROTECT_ENTITY_HP` at
   the finale). Confirm both peers agree on the completion result (survive/fail) and, whichever display
   design shipped (§9.4), that progress text matches at a mid-fight checkpoint (e.g. "2/3 rounds") —
   either read directly off the vanilla-piggyback field or via the latest `QS_SYNC_VERBPROGRESS_V1`
   snapshot.
4. **Board-roll check.** Force a board reroll on the host containing `QS_PACK_SHOWCASE` templates;
   confirm the client's view of that board shows the identical injected entries (same `TemplateID`s, same
   order) — the regression test for §9.4's board-injection recommendation.
5. **Late-join.** Start solo (host only), advance a showcase quest partway (e.g. past
   `QS_HOLD_THE_LINE_START`'s first objective), then have a client join mid-session. Confirm the client's
   quest log shows the correct in-progress state and that progress display populates immediately (via a
   `QS_SYNC_VERBPROGRESS_REQUEST_V1` round-trip, or the vanilla-field read) rather than waiting for the
   next tick.
6. **Parity-mismatch check.** Join a client with `QS_PACK_SHOWCASE` disabled (or a stale version) while a
   QS quest is in-flight on the host. Confirm `Block` (§9.5, §5) engages — the session refuses to proceed
   / prominently warns naming Questsmith and which part diverged — rather than letting the client silently
   render broken quest state.

## 10. Milestones

- **M1 — Pack loader + vanilla-verb showcase + MP parity.** Manifest parsing, load order/dependency
  resolution, merge of `quests/templates/spawners/towerdefenseunits/rewardencounters/board-injections/
  localization`, the board-injection weighted-append patch (using `GameRandom`, §9.3), `qs_list_packs`/
  `qs_reload_packs`/`qs_force_quest`/`qs_inject_pack` debug commands. The three showcase quests play
  through both branches end-to-end using only vanilla verbs (the M1 fallback objectives called out in §7)
  — `ASSASSINATE_ENTITY`, `REACH_HEX`, `REMOVE_ENCOUNTER`, `ACTIVATE_ENTITY`, `DUMMY`, `Conflicts`,
  `ChoiceRewards`, `RewardEncounters` racing. MP parity lands here, not deferred: ParityService
  registration (`guid, version, dataHash, enabledFeatures`) wired per R1 (§9.4); the `[Multiplayer]
  OnParityMismatch = Block` default wired per §9.5; the §9.6 MP smoke test's board-roll and
  parity-mismatch steps passing. Independently shippable: a pack-only mod with zero new verbs is a
  complete, useful, MP-safe mod.
- **M2 — Verb engine + host authority.** The `QS_*` objective-verb and trigger-action registry, the
  (currently unverified, §11) dispatch interception points, and the full launch set: `SURVIVE_ROUNDS`,
  `PROTECT_ENTITY_HP`, `STEALTH_REACH`, `KILL_WITH_TAG`, `TIMED_CHAIN` objective verbs; `GIVE_STATUS_PARTY`,
  `START_MAP_SPAWNER`, `SET_WORLD_MODIFIER` trigger actions. Showcase quests swap their M1 fallback
  objectives for the M2 verbs noted in §7, wired to `verbs-config.json`. Host-authority lands with the
  engine, not deferred to M3: every verb handler gated host-only per R3 (§9.4); objective completion
  applied through the vanilla quest-completion call so it replicates for free; the verb-progress display
  design (vanilla-field piggyback, falling back to `QS_SYNC_VERBPROGRESS_V1`/`_REQUEST_V1`, §9.4) shipped;
  §9.6's branch-choice and late-join MP smoke-test steps passing.
- **M3 — Spawner/TD integration polish.** Verified, patched (not just referenced) runtime activation for
  `START_MAP_SPAWNER` and mid-run `SET_WORLD_MODIFIER`; `QS_SYNC_BOARD_V1` implementation if §9.4's
  board-injection open questions resolve unfavorably for the vanilla-replication default; board-injection
  weight tuning pass against real `QuestBoards.json` ids; TDU escort polish (e.g. wave intensity scaling
  with how long the player dawdled, echoing the Rival Company race clock).

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
10. **Does `QuestHelper.GenerateSideQuestsFromQuestBoardConfig` run host-only in vanilla MP?** §9.4's
    board-injection recommendation (host-side `GameRandom` roll, no custom sync) assumes yes, mirroring the
    analogous host-only-decision prior other specs in this repo rely on for AI/grid logic. If each peer
    independently calls this method instead, the design must switch to fully shared-`GameRandom` reproduction
    on every peer (§9.4 option (a)) or `QS_SYNC_BOARD_V1` (option (b)).
11. **Does a Harmony postfix's modified return value on `GenerateSideQuestsFromQuestBoardConfig` reach
    whatever caller performs board-state replication, or has replication already happened before the
    postfix runs?** If the latter, Questsmith's board-injection entries never leave the host and
    `QS_SYNC_BOARD_V1` (§9.4) becomes mandatory, not just a fallback.
12. **Does vanilla's quest-log UI expose a numeric/text progress field on quest-instance state for
    objectives that have counts (e.g. a generic "N of M" readout), and does that field ride the same
    replication as objective completion?** §9.4's verb-progress-display recommendation (piggyback first,
    `QS_SYNC_VERBPROGRESS_V1` fallback) depends on the answer; needs a decompile pass on the quest-log
    progress-display method.
13. **Repo-wide MP unknowns that bear directly on Questsmith.** See `docs/MULTIPLAYER.md`'s numbered open-
    questions list, especially #2 (whether custom `GameRunData` state — which is exactly how Questsmith's
    verb runtime state is stored, §3 — replicates to clients or lives host-side only) and #5 (payload
    shape/size limits for `_handleNetworkAction`, relevant if `QS_SYNC_BOARD_V1`/`QS_SYNC_VERBPROGRESS_V1`
    end up required). Track resolution there; this spec's §9 will be updated once those land.
