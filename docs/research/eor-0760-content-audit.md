# EOR v0.7.0.60 (Release 29) — content audit & re-hosting assessment

**Date:** 2026-07-25 · **Analyzed package:** `D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN`
**Decompile:** `tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs` (29,415 lines, gitignored;
regenerate with `ilspycmd -p --nested-directories -r "<game>\For The King II_Data\Managed" EnhancedOverhaulRemix.dll`).
All `L####` references below are into that file. Supersedes/extends `eor-loader-notes.md` (which covered v0.7.0.51).

**Question answered here:** which parts of Enhanced Overhaul Revamped can be extracted as content and re-hosted
on this repo's engines (existing or to-be-built) so that they run without EOR's multiplayer bugs — and what,
verified against the decompiled source, makes EOR buggy in the first place.

---

## 1. Package anatomy

One DLL + data. `BepInEx/plugins/EnhancedOverhaulRemix.dll` (743 KB, GUID `lastg.ftk2.banditkingplayable`)
contains **all** scripting: ~83 Harmony patch targets (discovery L8471–L8942, patching L9287–L9624), four nested
managers (RiskyBlessingManager L646, NemesisManager L889, LocalizationManager L1757, DiagnosticReporter L2492).
Everything else is data:

| Path | Contents |
|---|---|
| `For The King II_Data/.../JSON~/Characters.json` | **Game-file override**: vanilla copy + 31 appended `EOR_*` player classes. 0 modified, 0 removed vanilla entries. |
| `.../JSON~/ServerNames.json` | **Game-file override**: all 97 lobby names replaced with "Enhanced … (Modded)". |
| `EnhancedOverhaulRevamped/CustomItems/` | 421 items (`EORR_CustomItems.json` 386, `StarterWeapons.json` 31, `Examples.json` 4) + `VisualFallbacks.json` (364 entries). |
| `EnhancedOverhaulRevamped/Localization/` | en 1440 keys / zh-Hans 1440 / pl 842 (double-BOM, parse-broken) / ru 630. |
| `EnhancedOverhaulRevamped/{Class,Trait,Service,Item}Icons`, `ClassPortraits` | 32/20/4/1/31 PNGs. |
| `FTK2_EnhancedPets/` | `Followers.json` (106 new pets), plus **inert** `Abilities.json`/`Characters.json` stale dumps (loader is `allowUpdates:false`, L25581–25615 discards all but one new key) and a mis-filed `ServerNames.json` full of item ids. |
| `FTK2_EnhancedMercenaries/Followers.json` | 100 new `MERC_PLUS_*` mercs + the same 106 pets duplicated + 22 modified vanilla followers. |
| `BepInEx/EnhancedOverhaulReports/github_issue_token.txt` | A shipped GitHub credential (see §5). |

⚠ **The local game install is contaminated.** `E:\Games\Steam\...\JSON~\Characters.json` and `ServerNames.json`
are byte-identical to the mod's overrides (mtimes differ from all sibling configs) — the mod was installed over
the game directory. There is no pristine baseline on disk; **Steam-verify before re-running `extract_vocab.py`**,
or the vocab index will treat EOR's 31 classes as vanilla.

## 2. Content inventory and code dependency

Headline: **the data layer is conservative — every ability/skill/status referenced by classes, items, pets and
mercs resolves to a vanilla id.** The single exception is one new pet ability (`HONEYBEE`). The mod adds **zero
monsters**. Everything genuinely custom is implemented in the DLL and surfaced to data only via loc keys + icons.

### 2.1 Classes (31, `EOR_ARCANIST` … `EOR_WIZARD`)

- Pure data: stats, `Things` starting gear (29 ids, all vanilla), 2 `Passives` each (37 distinct `SKILL_*`, all
  in vanilla `SkillConfigs.json`; 27 used by vanilla classes, 10 repurposed follower/NPC skills).
- Stat envelope is a deliberate power creep vs the 23 vanilla classes: LCK 50–95 (vanilla flat 50), CRT 5–9
  (vanilla flat 5), FOC up to 5 (vanilla max 4). A rebalance pass is warranted during porting.
- **DLL-only per class:** a signature "class skill" (31 hardcoded switches in `ClassSkillEngineBeforeAbility`
  L6091, `AfterAbility` L6190, `HandleClassSkillResponses` L6403); starting-kit application (code table L5762);
  starter-weapon configs (code table L4985, exported to JSON L26910); the entire **mastery** meta-progression
  system (L6838–6957, profile-persisted via `StatsHelper`, polled per-frame offline-only L19741).

### 2.2 Items (421)

- Pure data merged into `Env.Configs.Things` (loader `EnsureCustomItemFrameworkRuntimeConfigs` L26753; per-file
  merge L27169; validation gate L27233 — ability refs must exist, equippables need slots; quarantine of items
  without safe visuals L27405 strips loot/market tags).
- Ability surface: 112 distinct `Interactable.Abilities` keys + 123 bag ids (EORR set) — **all vanilla**. Skills
  referenced by items (`SKILL_SUPPORTRANGE` ×97, `SKILL_PARRY` ×35, …) and 17 `STATUS_IMMUNITY_*` — all vanilla.
- **Shipped defects** (our validator catches all of these):
  1. 26 `EORR_*_KATANA` items have **no localization and no VisualFallback** → quarantined by EOR's own loader;
     dead content as shipped.
  2. 262 items (all of `EORR_CustomItems.json`) declare `Interactable.Abilities` but ship an **empty
     `AbilityFillBag`** (starters/examples populate theirs correctly).
  3. 144 items list `AbilityBag` ids not present in that item's own `Abilities` dict.
  4. `StarterWeapons.json` uses `Class` strings (`SWORD`, `LANCE`, `ORB`) not seen elsewhere — verify against
     the live Class vocabulary.

### 2.3 Pets (106) and mercenaries (100 + 22 modified vanilla followers)

- `Followers.json` entries are pure data; **combat behavior is inherited entirely via `ConfigName` → vanilla
  `Characters.json`** (no ability fields exist in the follower schema). 100/106 pet ConfigNames resolve; the 6
  `SHEPHERD_SHEEP_*` entries dangle (`SHEEP_<X>_00` configs exist nowhere).
- Localization coverage is 3/106 (pets) and 18/206 (mercs).
- **DLL-only mechanics on top:** permanent contracts (`EnsureActiveMercenaryContractsUnlimited` L25838),
  per-level stat bonuses **baked into saved followers** (`ApplyMercenaryLevelBonuses` L25926 writes
  `BaseStatModifiers` + `EOR_MERC_LEVEL_BONUS_LEVEL` custom data — offline-only per-frame poll L19721, so saves
  drift between SP and MP), kibble feeding, training-manual items, shop dedup.

### 2.4 Traits (20 selectable loadout traits)

Configs are minted by code (`EnsureSelectableLoadoutTraitConfigs` L18554 → `Env.Configs.Things`), stat tables
at L18701–18732. Effect delivery splits:

- **Stat-only (9), pure data once a trait config exists:** LIGHT_FOOTED, TOUGHENED, STREETWISE, GOLD_INSTINCT,
  KNIFE_EDGE + the stat halves of TREASURE_SENSE, STEADY_AIM, WARDBOUND, SCHOLARS_HABIT.
- **DLL-only (11) — silent no-ops without the DLL:** PREPARED (start-combat focus, `SetInitiative` postfix
  L22849), PACK_TACTICS (`GetStat` conditional L22618), SCAVENGER (loot postfix L16357), BATTLE_RHYTHM
  (`ApplyStatChange` postfix L24556), ARCANE_MEMORY (`InventoryHelper.Consume` prefix L24462 — suppresses item
  consumption), SHIELDBEARER (`CalculateFinalDamage` postfix L24608), FIELDMEDIC + MENDERS_TOUCH
  (`AddHealth` prefix L24495/L24502), ARCANE_FOCUS (`GetStat` L22622), MOMENTUM (`ApplyStatChange` L24565),
  DRUNKEN_COURAGE (`PerformConsumableAbility` postfix L24632).
- Three trait statuses are declared "unsafe" by the mod itself and stripped at combat entry (L4399/L23890).

### 2.5 Affixes (22 defined, 20 in the roll table L5114)

Runtime item-variant minting: `TryEnsureAffixVariantConfig` L16941 **mutates `Env.Configs.Things` at runtime**,
tracked in per-process `AffixedItemVariants` L5048 (not persisted — hence a whole restore/repair layer
L17013–L17384 for reloads). 15 affixes are pure stat tables; 5 have bespoke effects (OF_FOCUS L22849,
OF_MOMENTUM L24565, OF_SPELLKEEPING L24463, OF_STABILITY L24780, OF_SCAVENGING L16397). This per-instance-state
design is exactly what `FTK2.Forge/SPEC.md` refuses; the config-shaped alternative is deterministic pre-minting
of affix variants as ordinary config ids (Forge `_PLUS{N}` pattern).

### 2.6 DLL-only gameplay systems (no home in current specs)

Risky Blessings (15, L646–887, `GameRunData.Stats` keys `EOR_RISKY_BLESSING_*`), Nemesis/Revenge foes
(L889–1751, `EOR_REVENGE_*` stats, 10 trait tables L5169), encounter modifiers (10, L5086, status configs
minted L18641), campaign mutators (8, L5074), world events (10, L5100 — reuse vanilla `ENCOUNTER_*` skill
encounters), quest archetypes (17, code tables L5718/L5732), town specialists (4, fully code L12234–L13920),
Sanctum status system (16+15 custom statuses, L5256/L5292), difficulty/adaptive-world-threat tuning (L17521).

## 3. Why EOR is buggy in multiplayer (decompile-verified)

1. **Stubbed MP guards.** `ShouldDisableCampaignMutatorsForMultiplayer` L14908,
   `ShouldDisablePreparedFocusForMultiplayer` L14913, `ShouldDisableVolatileCombatMutationsForMultiplayer`
   L14918 (checked at 11 call sites), `ShouldDisableRuntimeAffixesForMultiplayer` L20475 — **all `return
   false`**. Nemesis, encounter modifiers, trait combat effects, affix rolls all run online with no sync design.
   Only quest boards (L14885), market boosts (L20480) and town specialists (L12156/L12241/L13926) are actually
   gated.
2. **Shared-RNG stream perturbation.** Combat runs deterministic-lockstep on a shared `GameRandom`
   (confirmed by WarBrain's IL pass, `FTK2.WarBrain/SPEC.md:715-720`). EOR patches take *extra draws* from that
   stream: class-skill procs (L6193–6452), up to 8 extra loot rolls per player per combat (L16357–16412),
   nemesis rolls (L1076/L1227/L1362), encounter-modifier picks (L22814), map-gen shuffles (L23397–23426),
   market draws (L17401). Any peer entering a path another peer doesn't → permanent stream divergence.
3. **Client-local mutation of authoritative state.** New entities injected into `gameRun.Entities` during loot
   resolution (Forbidden Spoils L16596) and map gen (world events L23456); market list swapped in place from a
   local UI click (RelicBroker L12488); enemy set to max HP per-peer inside a `SetInitiative` postfix (Nemesis
   L1389–1390); every party member's `ConfigName` temporarily rewritten inside an `ApplyStatus` prefix
   (L24679, restore in finalizer L24731 — corruption on any exception in between).
4. **`_handleNetworkAction` prefix returns `false`** for `EOR_SYNC_*` payloads (L10601): an unmodded/other-
   version peer routes the forged `TOWN_SERVICES` action through the vanilla handler — undefined behavior. The
   author disabled their own transport (`EnableOrderedTownSnapshotTransport = false` L4251, log: "custom
   ordered town payloads can lock native market flow") but left the receive path armed.
5. **Silent reflection collapse.** All MP detection is untyped reflection with swallowed exceptions falling back
   to "not multiplayer" (`IsOnlineMultiplayerSession` catch → false, L20448). One renamed field after a game
   update silently disables *every* MP gate at once.
6. **Per-process static state affecting gameplay, partially cleared.** `GameRunData.Create` postfix L10508
   clears some caches but **not** `AffixedItemVariants`, `AdaptiveWorldThreatRewardBonusByEncounterGuid`,
   `WorldEventInjectedMapKeys`, `SelectedCampaignMutators`, `SteadyAimUsedThisCombat`,
   `EncounterModifierAppliedEnemies`, `ClassSkillRuntime`.
7. **Prefixes that skip vanilla and suppress authoritative changes per-client:** `InventoryHelper.Consume`
   L24473 (RNG-gated "don't consume the scroll") and `InteractableHelper.ApplyStatus` L24780 (RNG-gated
   "ignore the curse") — a client can decide not to apply a state change the host applied.
8. **Asymmetric sanitizers:** host repairs quest boards/run data that clients don't (L10656–10657, L11184,
   L11406); several subsystems run offline-only per-frame (L19721–19741) so saves drift between modes.

Notable positive: EOR's RNG *sourcing* discipline is good — zero `System.Random`/`UnityEngine.Random` in
gameplay paths; everything draws from `GameRandom`. The bugs are stream perturbation, asymmetric execution, and
unsynced local mutation — precisely the classes of failure `docs/MULTIPLAYER.md` R1–R5 exist to prevent.

## 4. Useful loader intelligence (beyond eor-loader-notes.md)

- CustomItems loader unchanged from .51 findings and still fully generic (§1 of eor-loader-notes holds for .60;
  new line anchors: enumerate L27151, merge L27210, validate L27233, quarantine L27405).
- EOR ships **13-arg reflection sender** to `AdventureDirector._trySendNetworkAction` (L12579–12616) and a
  handshake-lite "sync guard" that only *logs* mismatches (L7506–L8013) — parity is diagnostic, not enforced.
  Our ParityService (R1) must enforce.
- The trait-config minting path (L18554–18732) + `GetStat` postfix application is EOR's answer to the
  "custom traits without `eTraits`" problem — ClassForge Open Question #1's raw material.
- Follower merge paths differ: pets use `allowUpdates:false` (adds only), mercs use a dedicated path that
  **does** apply updates to vanilla followers (L25707).

## 5. Telemetry / privacy (drop entirely)

`DiagnosticReporter` (L2492–3760): hooks Unity logs; F8 (Ctrl/Shift+F8 = force-send, no prompt) files a GitHub
issue to `SirPepperPot/EnhancedOverhaulRemixVersion` (default) with **UploadMode default `AutoAlways`** —
report includes full plugin list, mod config, per-player inventory dumps (40 items/player), **host + all
players' display names and connection ids**, room id, and a follow-up comment containing the **entire BepInEx
LogOutput.log** (every installed plugin's output). Redaction covers only home-dir paths and IPv4s. Token
sources: env `EOR_GITHUB_ISSUE_TOKEN` or the shipped `github_issue_token.txt`. Startup also GETs a version URL
(L7353). None of this is ported.

## 6. Re-hosting verdict by category

| # | Category | Count | Host engine | Status | Blockers / notes |
|---|---|---|---|---|---|
| 1 | Items | ~390 usable | **Armory** (shipped) | **Portable now** | Converter tool + fresh vocab index post-verify; validator catches §2.2 defects |
| 2 | Pets/mercs | 206 | Followers loader (Summoner M1 subset) | Portable with small loader | Fix 6 dangling ConfigNames; add loc; drop DLL-side level-bonus mechanics |
| 3 | Classes (stats+gear+vanilla passives) | 31 | **ClassForge M1** | Portable once loader built | Data-only; rebalance pass recommended |
| 4 | Class signature skills | 31 | ClassForge skill recipes | Majority portable | Needs recipe engine + primitive extensions (§7); minority parked |
| 5 | Traits | 20 | ClassForge trait bridge + recipes | 9 now / 11 via recipes | Bridge mechanism now answerable from decompile |
| 6 | Affixes | 22 | Forge-pattern pre-mint | 15 as static variants | Per-instance runtime minting refused by design; 5 need recipes |
| 7 | Starter weapons | 31 | Armory | Portable now | Treat as items; already exported JSON shape |
| 8 | Quest archetypes | 17 | Questsmith (future) | Deferred | Board mechanics = Questsmith M2 territory |
| 9 | Blessings / Nemesis / enc. modifiers / mutators / world events / specialists / sanctums | — | none specced | **Dropped** (re-spec later if missed) | Complete systems, and where the worst MP code lives |
| 10 | Monsters | 0 | — | Nothing to port | — |
| 11 | Telemetry / version check / debug toolkit / camera tweaks | — | — | **Dropped** | DevKit owns dev tooling MP-safely |

## 7. Primitive gaps (DLL mechanics → engine vocabulary candidates)

ClassForge's specced recipe vocabulary (7 triggers × 8 conditions × 4 effects, `FTK2.ClassForge/SPEC.md` §4.6)
covers the majority of EOR's class skills and code traits. Verified-against-source gaps, each anchored to the
EOR patch point that proves the game surface exists:

| Candidate primitive | Kind | EOR evidence | Needed by |
|---|---|---|---|
| `ON_COMBAT_START` | trigger | `CombatHelper.SetInitiative` postfix L22830/L22849 | PREPARED, OF_FOCUS, several class skills |
| `ON_STAT_CHANGED` / on-damage-dealt | trigger | `InteractableHelper.ApplyStatChange` postfix L24541 | BATTLE_RHYTHM, MOMENTUM, OF_MOMENTUM |
| `ON_CONSUMABLE_USED` | trigger | `PerformConsumableAbility` postfix L24632 | DRUNKEN_COURAGE |
| `ONCE_PER_COMBAT` | condition | `SteadyAimUsedThisCombat` budget L4823/L22876 | STEADY_AIM |
| `DAMAGE_TAKEN_MULT` (incoming) | effect | `CalculateFinalDamage` postfix L24608 | SHIELDBEARER |
| `HEAL_RECEIVED/GIVEN_MULT` | effect | `CharacterHelper.AddHealth` prefix L24495/L24502 | FIELDMEDIC, MENDERS_TOUCH |
| `SUPPRESS_CONSUME` (chance) | effect | `InventoryHelper.Consume` prefix L24462 | ARCANE_MEMORY, OF_SPELLKEEPING — **MP-hazard: per-client suppression of authoritative change; must be host-decided + synced or redesigned** |
| `STATUS_APPLY_RESIST` | effect | `ApplyStatus` prefix L24780 | WARDBOUND, OF_STABILITY — same MP hazard class |
| Outgoing damage/crit buff window | effect | delegate-wrap in `PerformAbility` prefix L22892/L6155 | STEADY_AIM, several class skills — prefer status-based modeling over delegate wrapping |
| Gold grant | effect | loot postfix L16379–16418 only (overworld) | TREASURE_SENSE, SCAVENGER, OF_SCAVENGING — no combat-time gold verb exists (ClassForge OQ#6); model as post-combat loot hook or park |

Design rule for adoption (owner direction 2026-07-25): **content-driven to an extent** — port DLL behaviors by
defining engine primitives (data-triggerable, MP-first, drawn from shared RNG, host-authoritative where they
mutate state), never by porting EOR's per-feature patch style. Every adopted primitive must cite its
decompile-verified patch target; anything not verifiable is parked.

## 8. Cross-references

- Engine contracts consumed by this audit: `FTK2.ClassForge/SPEC.md`, `FTK2.Summoner/SPEC.md`,
  `FTK2.Armory/SPEC.md` + `tools/manifest_schema.md`, `FTK2.Forge/SPEC.md`, `docs/MULTIPLAYER.md`.
- Execution plan built from this audit: `docs/superpowers/plans/2026-07-25-eor-rehost-engine-charter.md`.
