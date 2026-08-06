# Encounter Modifiers — implementation SPEC (EOR re-host on ClassForge)

**Date:** 2026-08-05 · **Status:** design proposal (no code) · **Host engine:** `FTK2.ClassForge`
(`ftk2mods.classforge`), extending the shipped recipe engine v1.1 (`FTK2.ClassForge/SPEC.md` §4.6, merged
from `SPEC-DELTA-v1.1.md`).

**Sources of truth used, all re-verified this session:**

- EOR decompile: `tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs` (all `EOR L####`
  cites below are into this file).
- Current game build: types decompiled fresh from
  `E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed\FTK2.dll` via `ilspycmd`
  into `<scratchpad>/decomp/` (`CombatState.cs`, `EncounterComponent.cs`, `eEncounterProperties.cs`,
  `eConfigTags.cs`, `AdventureState.cs`, `ProgressionHelper.cs`, `GameplayDialogViewHelper.cs`,
  `StatusEffectConfig.cs`, `StatusEffectComponent.cs`, `CharacterComponent.cs`) — plus the repo's full
  native decompile `tools/out/decompile/FTK2/` for `CharacterHelper.cs` / `CombatHelper.cs` /
  `LootDropHelper.cs` line cites. Signature-level findings are restated inline so this spec stands alone.
- Our engine as shipped: `FTK2.ClassForge/src/ClassForge.Core/*` (pack loader),
  `ClassForge.Recipes/*` + `ClassForge.Plugin/Recipes/*` (recipe engine), verified by direct read.
- Binding constraints: `docs/MULTIPLAYER.md` R1–R5; recipe-engine determinism invariants SPEC §5.2
  (SPEC-DELTA numbering, = SPEC.md §4.6 "Determinism invariants"); SafeMode §5.3; CombatKey runtime §6.

**Hard dependency (referenced, NOT specced here):** the loot-grant sync verb spec being written in
parallel (the `CF_SYNC_LOOT_GRANT_V1` / `GOLD_GRANT` unlock tracked in SPEC.md §4.6 "parked" and
SPEC-DELTA §7.1). The reward halves of Veteran/Wealthy/Cursed/Treasure-Guarded depend on it — §11.

---

## 0. What this is

EOR v0.7.0.60 ships 10 weighted **encounter modifiers** — combat-variety mutators (Frenzied, Regenerating,
Cursed, Veteran, …) rolled once per eligible combat and applied to every enemy as a stat-delta status, with
two per-turn behaviors and three reward bonuses. The system is entirely DLL-side and is one of the named
homes of EOR's worst MP behavior (`docs/research/eor-0760-content-audit.md` §2.6, §3, §6 row 9 "Dropped —
re-spec later"). This spec is that re-spec: the same player-facing behavior, re-hosted as **pack-shipped
data + engine-evaluated combat-scoped recipes**, MP-first per `docs/MULTIPLAYER.md`.

What EOR does wrong, mechanically (each verified below): runtime-minted status configs
(config-divergence anti-pattern), a stubbed MP kill-switch, shared-stream RNG draws on an asymmetric
path, per-process static state that survives combats, and a `GetStat` overlay for the Max-HP halves.
Each gets a named fix in §2.

---

## 1. EOR's implementation — decompile-verified record

### 1.1 The 10 modifiers (EOR L5086–5098, definition class L405–433, enum L179–191)

`EncounterModifierDefinition(type, displayName, effectText, weight, xpBonusPercent = 0,
goldBonusPercent = 0, extraLootChancePercent = 0)`; `StatusKey => "STATUS_EOR_ENCOUNTER_" +
Type.ToUpperInvariant()` (L421). Status stat deltas are minted at L18645–18654 via
`CreateEncounterModifierStatus` (L18664–18690); the Max-HP percent halves are **not** in the statuses —
they live in a `CharacterHelper.GetStat` postfix overlay, `ApplyEncounterModifierDynamicStats`
(L22647–22667, hooked at L22529), keyed on `statKey == "HP"`, `component.GroupIndex == 1`, and
membership in `EncounterModifierAppliedEnemies`.

| # | Modifier | Weight | Status stat deltas (minted) | MaxHP % (GetStat overlay L22652–22659) | Reward meta | Per-turn behavior |
|---|---|---|---|---|---|---|
| 1 | Armored | 12 | `DEF +2, SPD -5` | — | — | — |
| 2 | Resistant | 12 | `RES +2, SPD -5` | — | — | — |
| 3 | Frenzied | 10 | `PHY +2, DEF -1` | — | — | — |
| 4 | Swift | 10 | `SPD +10` | **-10%** | — | — |
| 5 | Veteran | 10 | `ATK +1` | **+10%** | XP +10% | — |
| 6 | Wealthy | 8 | *(none)* | **+10%** | Gold +25% | — |
| 7 | Cursed | 6 | `RES +1` | — | XP +10% | 10% curse-on-hit (§1.4) |
| 8 | Regenerating | 6 | *(none)* | — | — | turn-start heal 2/3/4 (§1.4) |
| 9 | Glass Cannon | 6 | `ATK +3` | **-20%** | — | — |
| 10 | Treasure-Guarded | 5 | *(none)* | **+15%** | 20% extra-loot chance | — |

Total weight **85**. Minted status shape (L18675–18689): `Type: BUFF, Duration: -1, TickFrequency: 1,
TickOverworld: false, TickCombat: false, TickExpire: false, TileSync: false, GroupSync: false`, empty
`Passives`/`AddProperties`/`CustomStats`, `Stats` = the flat deltas above. The MaxHP overlay applies
`max(1, round(|result × pct| / 100))` and floors the reduced result at 1 (L22663–22664).

### 1.2 Selection (EOR L22688–22740, chance L22752–22795, pick L22814–22828)

- **Anchor:** `CombatHelper.SetInitiative` **Postfix** (L22830–22869), only when
  `ShouldDisableVolatileCombatMutationsForMultiplayer()` is false (it always is — §1.5), `__5`
  (= `pTrySkillProc`) is true, the entity is an enemy (`CharacterHelper.IsEnemy`), and the Nemesis system
  didn't claim the enemy first (L22840–22843).
- **State reset:** keyed on `combatState.Random` **reference identity** — a new combat's `GameRandom`
  resets `_activeEncounterModifier`/`_encounterModifierSelectionResolved`/`EncounterModifierAppliedEnemies`
  (`ResetEncounterModifierState`, L22679–22686). (EOR independently invented a weaker version of our
  CombatKey; its `EncounterModifierAppliedEnemies` is still a process-global `HashSet` — L4883 — listed in
  the audit's "partially cleared static state", audit §3.6.)
- **One roll on the first enemy processed** (`_encounterModifierSelectionResolved` latch, L22698–22728):
  compute chance (§1.3); `combatState.Random.NextChance(chance/100m)` (L22711); on success
  `PickEncounterModifier(combatState.Random)` — `NextInt(1, Σweights, pMaxInclusive: true)` then walk the
  array in authored order subtracting weights (L22814–22828). **Two shared-stream draws on success, one on
  failure, zero on exclusion.**
- **Application to every enemy** (L22729–22739): each enemy whose `SetInitiative` postfix runs while a
  modifier is active gets the status via native
  `InteractableHelper.ApplyStatus(enemy, enemy, null, "", StatusKey, combatState.Random, pResults,
  pTierStatus: false, EncounterModifierAppliedEnemies.Count == 1)` (L22733), deduped by
  `EncounterModifierAppliedEnemies.Add(enemy.Guid)`. Late-wave enemies therefore also receive it.
- **Banner** on selection: `GameplayDialogViewHelper.ShowEventTitle(bannerText, 4000)` (L22722; text
  format L10395–10405).

### 1.3 Chance formula + exclusions (EOR L22752–22795)

Excluded outright (no draw taken): party level ≤ 0 (L22756); `combatState.BossFightState != null` **or**
`CharacterHelper.ActorHasTag(firstEnemy, eConfigTags.BOSS)` **or** first enemy `ConfigName` contains
`"SCOURGE"` (case-insensitive) (L22761); encounter entity (resolved via
`gameRun.AdventureState.EncounterGUID` → entity with `EncounterComponent`, L22766–22771) has any of
`eEncounterProperties.BOSS / SPECIAL / SIEGE` (L22777); the encounter is an EOR legendary-contract quest
target (L22784, quest ids `EOR_LEGENDARY_CONTRACT_*` — EOR-specific, see §7).

Otherwise: `base = partyLevel ≤ 2 ? 10 : partyLevel ≤ 5 ? 20 : 30` (party level =
`ProgressionHelper.GetAveragePartyLevel` over `PlayerComponent` entities, L22742–22750);
`chance = clamp(base + (IsDungeon ? 5 : 0) + (AMBUSH ? 5 : 0) − (QUEST_TARGET ? 5 : 0), 0, 35)`
(L22790–22792).

### 1.4 Per-turn behaviors

- **Regenerating** (L22936–22977): postfix on `CombatHelper.TickActiveEntityCharacterStatus`; when
  `pIsStartTurn` and the round's active entity (`combatState.RoundEntities[0]`) is a living, damaged,
  modifier-tagged enemy, heal `partyLevel ≤ 3 ? 2 : partyLevel ≤ 6 ? 3 : 4` via
  `CharacterHelper.AddHealth(entity, num, __result)` (L22958–22961).
- **Cursed** (L24590–24602, called from the `InteractableHelper.ApplyStatChange` postfix at L24549):
  when origin is a modifier-tagged enemy and target is a hero, `random.NextChance(0.10m)` →
  `InteractableHelper.ApplyStatus(origin, target, null, "", "CURSE", random, results)` — the **vanilla**
  `CURSE` status.

### 1.5 Rewards + MP breakage

Rewards: postfix on `LootDropHelper.GetLootDropsFromEnemies` (L16339–16350) →
`ApplyEncounterModifierRewards` (L16636–16672): +`GoldBonusPercent` to the `CURRENCY_ADVENTURE` stack and
+`XpBonusPercent` to the `XP` stack (ceil, min 1 — L16674–16688); Treasure-Guarded takes **one more
shared-stream draw** (`random.NextChance(ExtraLootChancePercent/100)`, L16646) and on success duplicates
the first non-currency loot entry (or falls back to +15 gold).

MP breakage, all decompile-verified: the kill-switch `ShouldDisableVolatileCombatMutationsForMultiplayer`
is a stub `return false` (L14918–14921) — the system runs online with no sync design; the status configs
are minted into `Env.Configs.StatusEffects` **at runtime** (L18641–18662) so a peer that hasn't reached
the mint yet resolves the status id to nothing (the config-divergence anti-pattern; audit §3);
selection/pick/extra-loot draws perturb the shared `GameRandom` stream on paths not all peers execute
symmetrically (audit §3.2); `EncounterModifierAppliedEnemies` is process-global static state not cleared
by the `GameRunData.Create` postfix (L4883; audit §3.6); the MaxHP halves live in a per-peer `GetStat`
overlay rather than any replicated state.

---

## 2. Design overview — the three re-host moves

1. **Status payloads become pack-shipped, pre-minted status configs** (`statuses.json`, §3). The 10
   statuses exist in `Configs.StatusEffects` on every peer from config-load time, parity-hashed like all
   pack data (R1). Kills the runtime-minting anti-pattern outright. *Loader gap verified:* the pack
   loader today parses exactly `classes.json / traits.json / abilities.json / items.json`
   (`ClassForge.Core/PackContentParser.cs` L22–25) plus `skillrecipes.json`/localization/icons, and
   `MergePlanner` targets only `Characters/Things/Abilities` (`MergePlanner.cs` L37–49) — `statuses.json`
   and a `Configs.StatusEffects` merge target are **new** (§3.4). Both shipped packs
   (`data/ClassPacks/CF_PACK_BALDURS`, `CF_PACK_EOR_CLASSES`) confirm no pack ships statuses today.
2. **Selection + application become a combat-scoped (ownerless) recipe class** (§4) — a new engine
   capability: every shipped recipe today is owned by a character (`RecipeDispatcher.Holds()` checks the
   owner's `Passives`, `RecipeDispatcher.cs` L266–274). The CombatKey single-slot runtime (SPEC-DELTA §6)
   is the state container; EOR's `_encounterModifierCombatRandom` reference-identity reset (§1.2) is
   independent evidence the same identity notion works in practice — ours adds the seed component and the
   drop-on-any-change rule, making the `EncounterModifierAppliedEnemies` leak structurally impossible.
3. **Per-turn behaviors are ordinary owned recipes attached to the modifier status via its `Passives`**
   (§4.4) — with one verified engine fix required: the recipe engine's owner-passive resolution does
   **not** currently see status-attached passives, but the native game does (precedent + gap both cited
   in §4.4). Reward halves are data-declared here but delivered by the parallel loot-grant verb spec
   (§11).

Everything else is composition of shipped v1.1 machinery: effects ride `CombatHelper.ApplyAction`
(§5.2 inv. 5), all rolls draw from `CombatState.Random` (inv. 1–3), SafeMode is whole-engine-off (§5.3),
parity is ClassForge's existing `Block` posture (SPEC.md §9.5).

---

## 3. Pack schema

A modifier pack is an ordinary ClassForge pack (manifest, load order, per-pack enable knob, dataHash —
SPEC.md §4.1/§3) adding two new file types. Reference pack: `CF_PACK_ENCOUNTER_MODIFIERS`.

### 3.1 `statuses.json` — StatusEffectConfig-shaped (new pack file)

Shape = the native `StatusEffectConfig` verbatim (verified current build, `decomp/StatusEffectConfig.cs`:
`Type (eStatusEffectTypes), Duration, TickFrequency, TickOverworld, TickCombat, TickExpire, TileSync,
GroupSync, Passives (List<string>), AddProperties (List<eActorProperties>), Stats, CustomStats`). Id
convention `STATUS_CF_*` (loader-warned, not hard-failed — mirrors the `CF_PACK_` convention). Merged
adds-only into `Configs.StatusEffects` under M0 live-id enforcement (SPEC.md §9.1). `Type` validated
against `eStatusEffectTypes` (EGT §9); every `Passives` entry must resolve to a recipe id in the same
pack (or an `eSkills` member) or the entry is dropped with an Error finding.

```jsonc
{
  "STATUS_CF_ENCMOD_ARMORED": {
    "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
    "TickOverworld": false, "TickCombat": false, "TickExpire": false,
    "TileSync": false, "GroupSync": false,
    "Passives": [], "AddProperties": [],
    "Stats": { "DEF": 2, "SPD": -5 }, "CustomStats": {}
  }
}
```

The 10 shipped statuses transcribe §1.1's minted table exactly (same `BUFF/-1/no-tick` envelope EOR used,
L18675–18689), with two deltas: `STATUS_CF_ENCMOD_CURSED` carries `Passives:
["SKILL_CF_ENCMOD_CURSE_ON_HIT"]` and `STATUS_CF_ENCMOD_REGENERATING` carries `Passives:
["SKILL_CF_ENCMOD_REGEN_TICK"]` (§4.4, §8) — EOR shipped empty `Passives` and hardcoded both behaviors.

### 3.2 `modifiers.json` — the modifier table (new pack file)

```jsonc
{
  "SchemaVersion": "1.0",
  "Selection": {
    "Recipe": "SKILL_CF_ENCMOD_SELECT"        // the combat-scoped selection recipe (§6) this table feeds
  },
  "Modifiers": [                               // AUTHORED ARRAY ORDER = pick walk order (§6.3)
    { "Id": "ARMORED",     "Weight": 12, "Status": "STATUS_CF_ENCMOD_ARMORED" },
    { "Id": "RESISTANT",   "Weight": 12, "Status": "STATUS_CF_ENCMOD_RESISTANT" },
    { "Id": "FRENZIED",    "Weight": 10, "Status": "STATUS_CF_ENCMOD_FRENZIED" },
    { "Id": "SWIFT",       "Weight": 10, "Status": "STATUS_CF_ENCMOD_SWIFT",     "MaxHpPercent": -10 },
    { "Id": "VETERAN",     "Weight": 10, "Status": "STATUS_CF_ENCMOD_VETERAN",   "MaxHpPercent": 10,
      "Rewards": { "XpBonusPercent": 10 } },
    { "Id": "WEALTHY",     "Weight": 8,  "Status": "STATUS_CF_ENCMOD_WEALTHY",   "MaxHpPercent": 10,
      "Rewards": { "GoldBonusPercent": 25 } },
    { "Id": "CURSED",      "Weight": 6,  "Status": "STATUS_CF_ENCMOD_CURSED",
      "Rewards": { "XpBonusPercent": 10 } },
    { "Id": "REGENERATING","Weight": 6,  "Status": "STATUS_CF_ENCMOD_REGENERATING" },
    { "Id": "GLASSCANNON", "Weight": 6,  "Status": "STATUS_CF_ENCMOD_GLASSCANNON", "MaxHpPercent": -20 },
    { "Id": "TREASUREGUARDED", "Weight": 5, "Status": "STATUS_CF_ENCMOD_TREASUREGUARDED",
      "MaxHpPercent": 15, "Rewards": { "ExtraLootChancePercent": 20 } }
  ]
}
```

- `Weight` ≥ 1 int; Σ = 85 for the shipped table, but the engine sums whatever is authored.
- `MaxHpPercent` — delivered as a one-time `STAT_CHANGE {Stat:"MXHP", FlatPercent}` on application (§8.1),
  **not** a `GetStat` overlay (that primitive is parked, SPEC-DELTA §7.10, and EOR's version is per-peer
  display-state — §1.5). `MXHP` is a legal `CHANGE_STAT` stat (SPEC.md §4.4 vocabulary); runtime
  semantics of `FlatPercent` on `MXHP` (percent-of-what; current-HP clamp on reduction) are **unverified**
  — open question §13.1, gated test §12.
- `Rewards` — declarative metadata only; consumed by the loot-grant verb engine when it lands (§11).
  Ignored (logged once, not an error) until then.
- Localization: `CF_ENCMOD_<Id>` / `CF_ENCMOD_<Id>_DESCRIPTION` keys in the pack's
  `localization/en.json`, used by the banner (§9). EOR's English effect text (§1.1) ships as the default
  strings.

### 3.3 What is deliberately *not* in the schema

No per-modifier chance formula (the selection recipe owns it, §6.2); no free-form C# hooks; no
encounter-type whitelist beyond the exclusion conditions in §7 (want a different exclusion set → author a
different selection recipe).

### 3.4 Loader changes (engine work, M-EM1)

`PackContentParser` gains `statuses.json` + `modifiers.json`; `MergePlanner`/`PackLoader` gain a
`StatusEffects` merge category (same adds-only, last-pack-wins-among-packs, live-id-refused semantics as
the existing three — SPEC.md §3); the plugin merge postfix writes it into `Env.Configs.StatusEffects`
(same `ConfigsHelper.LoadConfigs`/`ReloadConfigs` postfix pass, idempotent by the same from-scratch
rebuild argument, SPEC.md §11.3/8). `modifiers.json` parses into ClassForge's own registry (like
`skillrecipes.json` — not a `Configs.*` field). Both files are new inputs to the existing `dataHash`
automatically (it hashes every non-localization file in the pack tree, SPEC.md §3), so R1 parity covers
them with zero new mechanism. Validation: every `Status` must resolve to a status id merged by the same
pack (or a live vanilla id); duplicate `Id`s are an Error; `Weight < 1` is an Error.

---

## 4. Combat-scoped (ownerless) recipes — new engine capability

### 4.1 The gap

Every v1.1 recipe evaluates only for owners that *hold* its id: `RecipeDispatcher.Holds(e, recipeId)`
scans `e.Passives` (`ClassForge.Recipes/Runtime/RecipeDispatcher.cs` L266–274), and
`EntityAdapter.Passives` unions the character config's `Passives` + equipped Things'
`Equippable.Passives`/`PassiveModifiers` (`ClassForge.Plugin/Recipes/GameAdapters.cs` L185–225). An
encounter modifier belongs to the *combat*, not to any character — no entity can own the selection roll.

### 4.2 `Scope: "COMBAT"` (new recipe-level field)

```jsonc
{ "SKILL_CF_ENCMOD_SELECT": {
    "SchemaVersion": "1.2", "Scope": "COMBAT", "Trigger": "ON_COMBAT_START", ... } }
```

- `Scope` ∈ `OWNED` (default — exactly today's semantics, field omitted everywhere in existing packs) |
  `COMBAT`. `SchemaVersion` bumps to `1.2`; a `1.1` loader rejects a `COMBAT` recipe cleanly via the
  existing version gate (SPEC.md §4.6 recipe-level fields).
- **Registration, not possession:** a `COMBAT` recipe is live for every combat while its pack is enabled.
  `Holds()` is bypassed; there is no owner iteration. It is evaluated **once per trigger event**, after
  all owned recipes for that event (fixed order: owned recipes first, then combat recipes ascending
  `Priority` / ordinal id — an extension of §5.2 invariant 4, keeping relative order deterministic).
- **Owner binding:** `Owner = null`. The validator (load-time) rejects, in a `COMBAT` recipe: any
  condition whose `Of` resolves to `SELF` (explicitly or by default — conditions must name
  `TRIGGER_TARGET`/`TRIGGER_SOURCE` or be combat-level, §5); any effect targeting
  `SELF`/`CASTER`/`ALLY_*`/`ENEMY_ALL` (ally-of-whom is undefined without an owner — effects must target
  `TRIGGER_TARGET`/`TRIGGER_SOURCE`); `AiProcChance` (meaningless without an owner). This is load-time
  rejection, not runtime skipping — a peer never decides locally to skip (invariant 6).
- **State keying:** `CombatRuntime.Budgets/Cooldowns/Counters` entries for combat recipes use the fixed
  sentinel owner guid `""` (empty string — ordinal-sorts before every real guid, so iteration order stays
  deterministic). `ONCE_PER_TARGET_*` budgets key on the trigger entity's guid exactly as today.
  Everything lives in the existing single-slot `(CombatKey, CombatRuntime)` cache (SPEC-DELTA §6) and
  dies with it — no new state container, no new lifetime rules.
- **Lifecycle vs CombatKey:** allocation on first hook observing a new `(CombatState identity, seed)`;
  drop-and-reallocate on any change; nothing survives the combat. §4.5 adds one derived-state
  reconstruction step at allocation.

### 4.3 ON_COMBAT_START anchoring without an owner

`ON_COMBAT_START` is already hooked: `CombatHelper.SetInitiative` Postfix, verified current signature
`SetInitiative(Entity pEntity, List<Entity> pAllies, CombatState pCombatState, GameRunData pGameRun,
List<(eAbilityResults, object)> pResults, bool pTrySkillProc = false)` (`tools/out/decompile/FTK2/
CombatHelper.cs` L43; hook at `ClassForge.Plugin/Recipes/CombatHookPatches.cs` L62–87). For owned
recipes the event's entity is the owner; for combat recipes it is bound to **`TRIGGER_TARGET`** instead
— the recipe fires *about* the entity being initialized, owned by no one.

**Engine fix required (verified):** the shipped `SetInitiative_Postfix` captures only
`(pEntity, pAllies, pResults)` (`CombatHookPatches.cs` L70–71) — it must additionally capture
`pTrySkillProc` and expose it on `CombatStartEvent`, because EOR gates on it (`__5` at L22840) and so do
we: `pTrySkillProc == false` calls are re-initializations that must not re-fire combat-start recipes.
New condition `COMBAT_START_REAL {Value: bool}` reads it (§5).

### 4.4 Per-turn behaviors: status-attached passives — verified gap + chosen route

Design direction (d) asks whether recipes attached to a status's `Passives` actually route through the
recipe engine's trigger hooks. **Verified answer: not today, but the native game sets the precedent and
the fix is a small adapter extension:**

- **Native precedent:** `CharacterHelper.GetPassiveSkills(Entity)` explicitly unions
  `Env.Configs.StatusEffects[status.Key].Passives.Where(x => x.StartsWith("SKILL_"))` over
  `StatusEffectComponent.Statuses` (`tools/out/decompile/FTK2/CharacterHelper.cs` L936–968 — re-verified
  in the current build via `decomp/StatusEffectConfig.cs` `Passives` field + `decomp/
  StatusEffectComponent.cs` `Dictionary<string, StatusEffectInfo> Statuses`). A status-granted `SKILL_*`
  is a first-class native concept.
- **Our gap:** `EntityAdapter.Passives` (`GameAdapters.cs` L185–225) reads config + equipment only —
  status-attached recipe ids are invisible to `Holds()`.
- **Fix (M-EM2):** extend `EntityAdapter.Passives` to additionally union, for each entry of the entity's
  `StatusEffectComponent.Statuses`, `Configs.StatusEffects[key].Passives` filtered to `SKILL_` — a
  mirror of the native loop. Determinism: the result feeds the existing ordinal sort + de-dup
  (`GameAdapters.cs` L220–221); statuses are replicated entity state identical on every peer; adapter
  instances are per-hook-invocation (`GameAdapters.cs` L228–230 comment), so `_passives` caching cannot
  go stale across status changes.

With that fix, `SKILL_CF_ENCMOD_REGEN_TICK` / `SKILL_CF_ENCMOD_CURSE_ON_HIT` are **plain owned recipes**
(the modifier-status-bearing enemy is the owner) using only shipped v1.1 vocabulary — no combat-scoped
machinery, no bespoke per-turn code (§8.2–8.3). The alternative (modifier-recipe-owns-the-per-turn-
behavior, i.e. more `COMBAT`-scoped recipes conditioned on `HAS_STATUS` of the round entity) is
**rejected**: it would need new "current round entity" trigger plumbing for ownerless `ON_TURN_START`,
while the adapter extension reuses every existing owned-trigger path and matches native semantics.

### 4.5 Derived-state reconstruction (JIP / mid-combat load)

The only cross-event state the system needs — "which modifier is active" and "which enemies already got
it" — is **fully derivable from replicated state**: the applied statuses themselves (`docs/
MULTIPLAYER.md` "prefer config-shaped content over runtime state", applied literally). Rule: when the
engine allocates a fresh `CombatRuntime` for a combat already in progress (join-in-progress, save/load
mid-combat, or a missed first hook), it reconstructs by scanning `CombatState.Entities` in ascending
ordinal `Entity.Guid` for any status id present in the modifier registry: first hit ⇒ that modifier is
recorded as selected (selection latch set, no draws taken), and per-enemy application budgets are marked
consumed for every enemy already bearing it. A JIP peer therefore applies the *same* modifier to
late-wave enemies the host does. Whether FTK2 supports mid-combat JIP at all is unknown (§13.3) — the
reconstruction is cheap and also covers mid-combat save/load in SP, so it ships regardless.

---

## 5. Vocabulary additions (v1.2)

All additions obey the standing rules: conditions are pure reads of replicated state or hook parameters,
no condition consumes RNG; effects ride `CombatHelper.ApplyAction` or are `[LOCAL]` presentation;
rolls come last (§5.2). Every read below was signature-verified against the **current** game build this
session (`decomp/` files).

**Conditions (6):**

| Token · shape | Reads (verified source) |
|---|---|
| `PARTY_AVG_LEVEL {Comparator, Value}` | `ProgressionHelper.GetAveragePartyLevel(List<Entity>)` over `GameRun.Entities` filtered `Has<PlayerComponent>() && Has<CharacterComponent>()` (`decomp/ProgressionHelper.cs` L777; filter mirrors EOR L22744) |
| `IS_DUNGEON {Value: bool}` | `CombatState.IsDungeon` public field (`decomp/CombatState.cs`) |
| `BOSS_FIGHT {Value: bool}` | `CombatState.BossFightState != null` (`decomp/CombatState.cs`) |
| `ENCOUNTER_PROPERTY {Value: <eEncounterProperties>, Negate}` | encounter entity via `GameRun.AdventureState.EncounterGUID` (`decomp/AdventureState.cs` L15) → `EncounterComponent.HasProperty` / `HasAnyProperty` (`decomp/EncounterComponent.cs` L53/L62); enum members incl. `BOSS, SPECIAL, SIEGE, AMBUSH, QUEST_TARGET` (`decomp/eEncounterProperties.cs`). No encounter entity resolved ⇒ condition is false (matches EOR's `flag`/`flag2` defaults, L22772–22773) |
| `ENTITY_TAG {Of, Value: <eConfigTags>, Negate}` | `CharacterHelper.ActorHasTag(entity, eConfigTags.X)` — enum overload verified in use (`tools/out/decompile/FTK2/CharacterHelper.cs` L1228); `BOSS` is a member (`decomp/eConfigTags.cs` L140) |
| `CONFIG_NAME_CONTAINS {Of, Value, Negate}` | `CharacterComponent.ConfigName` (`decomp/CharacterComponent.cs` L8), ordinal-ignore-case contains — EOR's SCOURGE check verbatim (L22761) |
| `COMBAT_START_REAL {Value: bool}` | `SetInitiative`'s `pTrySkillProc` parameter (§4.3). `ON_COMBAT_START` only |
| `SELECTION_PRESENT {Name, Value: bool}` | `CombatRuntime.Selections[Name]` non-empty (§5 effects below). Engine state, no hook |

**Recipe-level field: `ProcChanceFormula`** (mutually exclusive with `ProcChance`; `COMBAT` and `OWNED`
recipes alike):

```jsonc
"ProcChanceFormula": {
  "Base": [                                  // first row whose Conditions all pass wins; last row = default
    { "Conditions": [ { "Type": "PARTY_AVG_LEVEL", "Comparator": "LTE", "Value": 2 } ], "Value": 10 },
    { "Conditions": [ { "Type": "PARTY_AVG_LEVEL", "Comparator": "LTE", "Value": 5 } ], "Value": 20 },
    { "Value": 30 } ],
  "Adjustments": [                           // every row whose Conditions pass adds its Value
    { "Conditions": [ { "Type": "IS_DUNGEON", "Value": true } ], "Value": 5 },
    { "Conditions": [ { "Type": "ENCOUNTER_PROPERTY", "Value": "AMBUSH" } ], "Value": 5 },
    { "Conditions": [ { "Type": "ENCOUNTER_PROPERTY", "Value": "QUEST_TARGET" } ], "Value": -5 } ],
  "Min": 0, "Max": 35
}
```

A pure function of replicated state → the same integer on every peer → exactly one `NextChance` draw, or
zero when it clamps to ≤ 0 (a replicated-state-determined, therefore symmetric, skip — same argument as
`ProcChance: 100` taking zero draws, §5.2 inv. 3). Conditions inside the formula use the same evaluator;
no RNG.

**Effects (3):**

- `SELECTION_SET {Name, OneOfWeighted: [{Value, Weight}]}` — the **weighted pick** (design direction c;
  generalizes `StatusOneOf`, which stays uniform and untouched). Takes **exactly one draw**:
  `NextInt(1, Σweights, pMaxInclusive: true)`, then walks the authored array subtracting weights — EOR's
  algorithm verbatim (L22814–22828) so the pick distribution is identical. Stores the winning `Value`
  (a string) in `CombatRuntime.Selections[Name]` (new `Dictionary<string,string>` on `CombatRuntime`,
  reset with the runtime like `Counters`). `[LOCAL]` state, `[SYNCED]` cause, exactly like counters.
  Draw-count discipline for the modifier use is stated in §6.3.
- `ADD_STATUS` gains `StatusFromSelection: <Name>` (mutually exclusive with `Status`/`StatusOneOf`) —
  resolves the status id from `CombatRuntime.Selections[Name]`; no selection present ⇒ no-op (and with
  `Budget.ConsumeOn: EFFECT_APPLIED`, no budget burn). Zero RNG. Emission is the existing native
  `ADD_STATUS` path through `ApplyAction`.
- `EVENT_BANNER {LocKey, FallbackText, DurationMs, TextFromSelection?}` — `[LOCAL]` presentation (R4):
  `GameplayDialogViewHelper.ShowEventTitle(string pRichText, int pDuration = 3000)`, verified current
  build (`decomp/GameplayDialogViewHelper.cs` L153; EOR precedent L22722). Renders on every peer running
  the engine — which under `ALL_PEERS` parity is every peer. Never gates or feeds gameplay state;
  excluded from SafeMode considerations like all presentation.

---

## 6. Selection algorithm + RNG discipline

### 6.1 The two shipped combat-scoped recipes

```jsonc
{
  "SKILL_CF_ENCMOD_SELECT": {
    "SchemaVersion": "1.2", "Scope": "COMBAT", "Trigger": "ON_COMBAT_START", "Priority": 10,
    "Conditions": [
      { "Type": "COMBAT_START_REAL", "Value": true },
      { "Type": "CHARACTER_TYPE", "Of": "TRIGGER_TARGET", "Value": "..." },   // enemy-side gate, see §6.2
      { "Type": "PARTY_AVG_LEVEL", "Comparator": "GTE", "Value": 1 },
      { "Type": "BOSS_FIGHT", "Value": false },
      { "Type": "ENTITY_TAG", "Of": "TRIGGER_TARGET", "Value": "BOSS", "Negate": true },
      { "Type": "CONFIG_NAME_CONTAINS", "Of": "TRIGGER_TARGET", "Value": "SCOURGE", "Negate": true },
      { "Type": "ENCOUNTER_PROPERTY", "Value": "BOSS",    "Negate": true },
      { "Type": "ENCOUNTER_PROPERTY", "Value": "SPECIAL", "Negate": true },
      { "Type": "ENCOUNTER_PROPERTY", "Value": "SIEGE",   "Negate": true }
    ],
    "ProcChanceFormula": { /* §5 example verbatim — 10/20/30 + dungeon/ambush/quest, clamp 0..35 */ },
    "Budget": { "Scope": "ONCE_PER_COMBAT", "ConsumeOn": "EVALUATION" },
    "Effects": [
      { "Type": "SELECTION_SET", "Name": "CF_ENCMOD",
        "OneOfWeighted": [ /* generated from modifiers.json §3.2, authored order, weights 12,12,10,10,10,8,6,6,6,5 */ ] },
      { "Type": "EVENT_BANNER", "LocKey": "CF_ENCMOD_BANNER", "DurationMs": 4000,
        "TextFromSelection": "CF_ENCMOD" }
    ]
  },
  "SKILL_CF_ENCMOD_APPLY": {
    "SchemaVersion": "1.2", "Scope": "COMBAT", "Trigger": "ON_COMBAT_START", "Priority": 20,
    "Conditions": [
      { "Type": "COMBAT_START_REAL", "Value": true },
      { "Type": "CHARACTER_TYPE", "Of": "TRIGGER_TARGET", "Value": "..." },   // same enemy gate
      { "Type": "SELECTION_PRESENT", "Name": "CF_ENCMOD", "Value": true }
    ],
    "Budget": { "Scope": "ONCE_PER_TARGET_PER_COMBAT", "ConsumeOn": "EFFECT_APPLIED" },
    "Effects": [
      { "Type": "ADD_STATUS", "Target": "TRIGGER_TARGET", "StatusFromSelection": "CF_ENCMOD" },
      { "Type": "STAT_CHANGE", "Target": "TRIGGER_TARGET", "Stat": "MXHP",
        "PercentFromSelection": "CF_ENCMOD" }        // sugar: MaxHpPercent of the selected modifier; 0 ⇒ omitted
    ]
  }
}
```

(These two recipes are *generated* by the engine from `modifiers.json` — authors write the table, not the
recipes; the expansion is shown so the semantics are reviewable. `modifiers.json` is the single source;
generating keeps the weighted list, status ids and `MaxHpPercent` values from ever drifting apart across
hand-authored copies.)

### 6.2 Fixed, always-reached draw point

The draw point is: **the first `ON_COMBAT_START` event in a fresh CombatKey whose trigger entity is an
enemy and `pTrySkillProc` is true** — EOR's anchor (§1.2), expressed as recipe machinery. "Is an enemy"
must be evaluated identically everywhere: the implementation uses `CharacterHelper.IsEnemy` (the exact
native predicate EOR uses at L22690/L22840); whether that surfaces as `CHARACTER_TYPE` values or a
dedicated `IS_ENEMY {Of}` condition is an M-EM2 detail — the spec requirement is *one* predicate, the
native one, used by both recipes (placeholder marked `"..."` above deliberately, not silently guessed).
The `ONCE_PER_COMBAT / EVALUATION` budget makes later enemies' events skip the selection recipe without
reaching its roll — a budget check, not a state-dependent roll skip, and identical on every peer.

Ordering guarantee: `SetInitiative` is invoked by native combat setup from replicated state, so its call
sequence is identical on every peer executing the path (the same assumption every shipped v1.1 trigger
already makes, SPEC-DELTA §2 "Universal MP posture"; EOR ran three releases on this anchor). Under
combat-authority Design A only the host executes and results replicate; under Design B all peers execute
identically — both safe, per SPEC.md §9.3b.

### 6.3 Draw-count discipline (invariant 5.2 #3, strengthened per the brief)

Per combat, when the recipe engine is enabled and the selection recipe's conditions + formula produce an
eligible fight, the engine takes **exactly two draws, always**:

1. `NextChance(chance/100m)` — the gate.
2. `NextInt(1, Σweights, pMaxInclusive: true)` — the pick, taken **whether or not the gate succeeded**;
   the result is discarded when the gate failed.

Draw 2 unconditionally is a deliberate strengthening over EOR (which skipped the pick on gate failure —
symmetric but harder to audit): with it, the per-combat draw count is a constant (2) for every eligible
fight and 0 for every excluded/clamped-to-zero fight, both pure functions of replicated state — no
reasoning about "the second draw's reachability depends on the first draw's outcome" is ever needed.
Implementation note: this pairing is what `SELECTION_SET` + `ProcChanceFormula` compile to when they
appear on the same recipe — the engine hoists the pick draw to unconditional; a `SELECTION_SET` on a
plain-`ProcChance` recipe keeps the standard roll-last, draw-only-on-fire semantics.

The application recipe takes **zero** engine draws (`ADD_STATUS`/`STAT_CHANGE`, no `ProcChance`).
Whatever the native `ApplyStatus`/`ApplyAction` internals draw is taken at a native call point reached
identically on every executing peer — the same posture as every existing recipe effect. Per-enemy
application order needs no engine iteration at all: each enemy's own `SetInitiative` postfix applies it
(EOR's shape, L22729), deduped by the per-target budget; late-wave enemies get the status when their own
event fires, exactly like EOR (§1.2).

### 6.4 Per-turn recipe draws

`SKILL_CF_ENCMOD_CURSE_ON_HIT` (§8.2) rolls `ProcChance: 10` at its standard roll-last position on each
qualifying `ON_DAMAGE_DEALT` — the trigger set is a pure function of replicated combat events, same as
every shipped `ProcChance` recipe. `SKILL_CF_ENCMOD_REGEN_TICK` takes zero draws (no `ProcChance`,
deterministic effects). Iteration order everywhere: the standard §5.2 inv. 4 rules (recipes by
`Priority`/id, entities by ordinal `Entity.Guid`); the two shipped combat recipes order by `Priority`
10 → 20 so selection always precedes application within one event.

---

## 7. Exclusion rules

| EOR check (cite) | Our equivalent | Current-build verification |
|---|---|---|
| party level ≤ 0 (L22756) | `PARTY_AVG_LEVEL GTE 1` | `ProgressionHelper.GetAveragePartyLevel` L777 (`decomp/ProgressionHelper.cs`) |
| `combatState.BossFightState != null` (L22761) | `BOSS_FIGHT false` | `CombatState.BossFightState` public field (`decomp/CombatState.cs`) |
| `ActorHasTag(firstEnemy, eConfigTags.BOSS)` (L22761) | `ENTITY_TAG {Of: TRIGGER_TARGET, Value: BOSS, Negate}` | enum overload in current build (`CharacterHelper.cs` L1228); `eConfigTags.BOSS` (`decomp/eConfigTags.cs` L140) |
| ConfigName contains "SCOURGE" (L22761) | `CONFIG_NAME_CONTAINS {Negate}` | `CharacterComponent.ConfigName` (`decomp/CharacterComponent.cs` L8) |
| encounter has BOSS/SPECIAL/SIEGE (L22777) | `ENCOUNTER_PROPERTY ×3 {Negate}` | `EncounterComponent.HasProperty/HasAnyProperty` + all five enum members (`decomp/EncounterComponent.cs` L53/62, `decomp/eEncounterProperties.cs`); encounter resolved via `AdventureState.EncounterGUID` (`decomp/AdventureState.cs` L15) — same lookup EOR does at L22766–22771 |
| AMBUSH +5% / QUEST_TARGET −5% (L22782–22791) | `ProcChanceFormula.Adjustments` | same `ENCOUNTER_PROPERTY` reads |
| legendary-contract quest target (L22784–22811) | **dropped** | EOR-only content (`EOR_LEGENDARY_CONTRACT_*` quest ids, L22810); nothing on our stack mints those quests. If a future Questsmith port revives them, its pack overrides the generated selection recipe — the exclusion belongs to content, not engine. Recorded, not silently lost. |

All reads are of replicated run/combat/entity state (components and public fields on the replicated
`GameRunData`/`CombatState`/entity graph), so every peer computes identical exclusion results — the
precondition for the "zero draws on excluded fights" symmetry claim in §6.3. One honest caveat:
"replicated" here means "part of the state the vanilla game keeps consistent across peers" — the same
assumption v1.1 conditions already lean on; `MULTIPLAYER.md` open question #2/#3 (does *every* GameRun
field replicate) remains open repo-wide and is not re-litigated here.

---

## 8. The 10 modifiers as authored content

### 8.1 The eight status-only modifiers

Armored, Resistant, Frenzied, Swift, Veteran, Wealthy, Glass Cannon, Treasure-Guarded = one
`statuses.json` entry each with §1.1's exact stat deltas, applied by the generated application recipe.
The five `MaxHpPercent` halves (Swift −10, Veteran +10, Wealthy +10, GlassCannon −20, TreasureGuarded
+15) ride the one-time `STAT_CHANGE MXHP FlatPercent` at application (§6.1). **Recorded semantic
deltas vs EOR:** (a) EOR's MaxHP was a per-peer `GetStat` display overlay (§1.1) — ours is a real,
replicated stat change through the native verb; enemies do not outlive combat, so persistence is moot,
but the interaction of `FlatPercent` with current-HP (does a −20% MXHP also cut current HP; is it
percent-of-current-max or base) is unverified — §13.1 gates M-EM3 sign-off on an in-game check, with
EOR's floor-at-1 behavior (L22663–4) as the acceptance bar. (b) EOR floors each percent delta at 1 HP
minimum (`Math.Max(1, ...)`) — whether the native verb rounds identically is part of the same check.

### 8.2 Cursed (status + per-turn recipe)

`STATUS_CF_ENCMOD_CURSED` carries `Stats: {RES: 1}` + `Passives: ["SKILL_CF_ENCMOD_CURSE_ON_HIT"]`:

```jsonc
{ "SKILL_CF_ENCMOD_CURSE_ON_HIT": {
    "SchemaVersion": "1.1", "Trigger": "ON_DAMAGE_DEALT",
    "Conditions": [], "ProcChance": 10, "AiProcChance": 10,
    "Effects": [ { "Type": "ADD_STATUS", "Target": "TRIGGER_TARGET", "Status": "CURSE" } ],
    "Budget": { "Scope": "NONE" }, "VerboseLogTag": "Cursed encounter" } }
```

Owner = the status-bearing enemy (via the §4.4 adapter extension). `ON_DAMAGE_DEALT` already restricts
the target to opponents of the owner (SPEC-DELTA §2.2 T3: `pStatAction.Stat == "HP"`, target ∉ owner's
party) — which for an enemy owner *is* "hit a hero", EOR's `IsEnemy(origin) && !IsEnemy(target)` check
(L24593) for free. `"CURSE"` is the same vanilla status EOR applies (L24595). `AiProcChance` mirrors
`ProcChance` because the owner is always AI-controlled. Delta vs EOR: EOR procs on any
damaging `ApplyStatChange` from the enemy; T3 has the same hook and stat filter — behavior matches.

### 8.3 Regenerating (status + per-turn recipe)

`STATUS_CF_ENCMOD_REGENERATING` carries empty `Stats` + `Passives: ["SKILL_CF_ENCMOD_REGEN_TICK"]`:

```jsonc
{ "SKILL_CF_ENCMOD_REGEN_TICK": {
    "SchemaVersion": "1.1", "Trigger": "ON_TURN_START",
    "Conditions": [ { "Type": "HP_THRESHOLD", "Percent": 99, "Comparator": "LTE" } ],  // damaged only — EOR L22958
    "Effects": [
      { "Type": "STAT_CHANGE", "Target": "SELF", "Stat": "HP", "StatChangeType": "REGEN", "FlatValue": 2,
        "Conditions": [ { "Type": "PARTY_AVG_LEVEL", "Comparator": "LTE", "Value": 3 } ] },
      { "Type": "STAT_CHANGE", "Target": "SELF", "Stat": "HP", "StatChangeType": "REGEN", "FlatValue": 3,
        "Conditions": [ { "Type": "PARTY_AVG_LEVEL", "Comparator": "GTE", "Value": 4 },
                         { "Type": "PARTY_AVG_LEVEL", "Comparator": "LTE", "Value": 6 } ] },
      { "Type": "STAT_CHANGE", "Target": "SELF", "Stat": "HP", "StatChangeType": "REGEN", "FlatValue": 4,
        "Conditions": [ { "Type": "PARTY_AVG_LEVEL", "Comparator": "GTE", "Value": 7 } ] } ],
    "Budget": { "Scope": "ONCE_PER_ROUND" }, "VerboseLogTag": "Regenerating encounter" } }
```

EOR's 2/3/4-by-party-level table (L22960) expressed via per-effect `Conditions` (the v1.1 universal
extension) + the new `PARTY_AVG_LEVEL` condition — mutually exclusive bands, zero RNG, zero new effect
machinery. Two recorded deltas vs EOR: (a) EOR reads the party level cached at selection time
(`_encounterModifierPartyLevel`, L22701) — ours reads live; they diverge only if the party levels
mid-combat (accepted; live is arguably more correct, and both are identical on all peers). (b) EOR
hooks `TickActiveEntityCharacterStatus` (`pIsStartTurn`, L22936–22953) while our `ON_TURN_START` rides
`CombatPhase._performSkillAbilityProcs` via the native `START_TURN` `EVENT_PROC` (SPEC.md §6) — whether
that proc path fires for **enemy** entities is unverified (§13.2). If it doesn't, the fallback is a
`CombatHelper.TickActiveEntityCharacterStatus` postfix (current-build signature verified:
`TickActiveEntityCharacterStatus(Env pEnv, GameRandom pGameRandom, bool pIsStartTurn)` →
`List<(eAbilityResults, object)>`, `tools/out/decompile/FTK2/CombatHelper.cs` L954) as an additional
`ON_TURN_START` anchor for AI entities — decided by M-EM2's first in-game check, not silently at
implementation time.

`HP_THRESHOLD {Percent: 99, LTE}` note: v1.1 defines Percent as current/max ×100 (SPEC-DELTA §3.1);
"damaged" = strictly below 100%. If the comparator semantics on the shipped evaluator make 99 awkward
for a 1-HP-missing large-pool enemy (rounding), M-EM3 may substitute an exact `Percent < 100` form —
an evaluator detail flagged for test §12, not a design question. Healing a full-HP enemy is also
harmless (EOR's check is an optimization), so the condition may be dropped entirely if simpler.

---

## 9. UI / banner (R4 — presentation, local-only)

- **Selection banner:** `EVENT_BANNER` (§5) via `GameplayDialogViewHelper.ShowEventTitle(text, 4000)` —
  EOR's exact surface and duration (L22722, L10395–10405). Text = localized
  `CF_ENCMOD_BANNER` format (`"{0} Encounter! {1}"` default, matching EOR's
  `EOR_ENCOUNTER_MODIFIER_BANNER_FORMAT` fallback) filled from the selected modifier's
  `CF_ENCMOD_<Id>` / `_DESCRIPTION` strings. Localization lives in the pack's `localization/en.json` —
  parity-exempt (R1 carve-out), like all localization.
- **Persistent visibility:** the modifier is a real status on every affected enemy, so the native status
  UI (status icons/tooltips on the enemy panel) shows it for the rest of combat with the pack's
  localized name/description — strictly better than EOR's transient banner + invisible `GetStat`
  overlay. Status icons ship in the pack's `icons/` folder keyed by status id, riding the existing
  `AssetLoader.GetImage`/`GetRender` fallback patch (SPEC.md §4.8/§6) — whether that patch's lookup path
  covers *status* icon requests is unverified (§13.4); if not, statuses render with the game's default
  icon and the name/tooltip still carry the information (acceptable degraded state, logged).
- **No new UI surface** is built (no combat-header widget, no modifier list panel). `[LOCAL]` class,
  zero gameplay reads, zero RNG, may fail without touching parity.

---

## 10. Multiplayer posture (per `docs/MULTIPLAYER.md` §"Standard spec language")

1. **Parity class: `ALL_PEERS`.** The pack's statuses merge into `Configs.StatusEffects` on every peer
   at load; recipes evaluate on whichever peer executes the native hooks. A peer without the pack cannot
   resolve `STATUS_CF_ENCMOD_*` the moment it replicates onto an enemy — same unresolvable-config
   failure class as pack classes (SPEC.md §9.1). Covered by ClassForge's existing registration: the two
   new files fold into `dataHash` automatically (§3.4); the pack id appears in `enabledFeatures`; the
   engine knob is already covered by `feature:EnableRecipeEngine`.
2. **Feature table.**

   | Feature | Class | Authority |
   |---|---|---|
   | `statuses.json`/`modifiers.json` merge | `[SYNCED]` | all peers identically at load (R1 property, nothing transmitted) |
   | Selection gate + weighted pick (2 draws) | `[SYNCED]` | executing peer(s), shared `CombatState.Random`; safe under Design A/B (§6.2) |
   | Status + `MXHP` application | `[SYNCED]` | native `ADD_STATUS`/`CHANGE_STAT` via `ApplyAction` — vanilla replication |
   | Per-turn recipes (curse-on-hit, regen tick) | `[SYNCED]` | standard owned-recipe posture (SPEC.md §9.2) |
   | `CombatRuntime.Selections` + budgets | `[LOCAL]` | per-battle derived state, never transmitted, reconstructable from replicated statuses (§4.5) |
   | Banner + status icons/loc | `[LOCAL]` | presentation only (R4) |
   | Reward halves | *deferred* | owned by the loot-grant verb spec (§11) |
3. **Determinism inventory.** Rolls: the two selection-point draws (§6.3) + the Cursed 10% proc (§6.4)
   — all via `CombatState.Random`, all at points reached identically on executing peers, draw counts
   pure functions of replicated state (constant-2 hoisting makes this checkable by counting). Generated
   content: the two combat recipes generated from `modifiers.json` are a pure function of the
   parity-hashed table (R2); no runtime-minted ids anywhere; the vendor's own desync detector
   (`GameAction.DesyncDetectionData`, MULTIPLAYER.md) is the tripwire for any violation.
4. **Sync surface.** No custom `_SYNC_` action. Content rides config merge; effects ride the vanilla
   action pipeline; the only cross-event state is derivable from replicated statuses (§4.5). The reward
   halves are exactly the place a synced verb *is* needed — which is why they are delegated (§11).
5. **SafeMode.** Unchanged from ClassForge: default `OnParityMismatch = Block` (SPEC.md §5/§9.5 —
   content stays merged inert, runtime features off). If an operator overrides to `WarnAndSafeMode`,
   SafeMode = **whole recipe engine off** (§5.3, all-or-nothing) — no selection draws are ever taken,
   so a SafeMode peer takes zero modifier draws while a healthy peer takes two → this is precisely why
   partial SafeMode is forbidden and why `Block` stays the default. There is no modifier-specific
   SafeMode subset.
6. **JIP:** §4.5 reconstruction; statuses already on enemies replicate natively to the joiner (they are
   ordinary config-backed statuses), the joiner's pack resolves the configs, per-turn passives work via
   the §4.4 adapter, late-wave application converges via reconstruction. **MP test plan:** §12 steps
   6–8.

---

## 11. Reward halves — dependency boundary (loot-grant verb spec, in parallel)

Veteran/Cursed XP +10%, Wealthy gold +25%, Treasure-Guarded 20% extra loot are **declared here, delivered
there**:

- **This spec owns:** the `Rewards` metadata block in `modifiers.json` (§3.2); the fact of an active
  modifier at combat end (readable as `CombatRuntime.Selections["CF_ENCMOD"]`, or re-derived from any
  surviving... — no: enemies are dead at loot time, so the **selection slot is the source**, which is
  precisely why §4.5's reconstruction must run before loot on a JIP peer); the EOR ground truth the verb
  spec must reproduce: hook `LootDropHelper.GetLootDropsFromEnemies` (current-build signature verified:
  `(List<Entity> pParty, List<Entity> pEnemies, int pMaxMaterialTier, Env pEnv, GameRandom pGameRandom)
  → List<Thing>`, `tools/out/decompile/FTK2/LootDropHelper.cs` L1204), stack-bump semantics
  `max(1, ceil(stack × pct/100))` on `CURRENCY_ADVENTURE`/`XP` (EOR L16674–16688), Treasure-Guarded's
  one extra draw + duplicate-first-non-currency-else-+15-gold behavior (L16646–16662), and EOR's
  deterministic extra-thing id discipline (`AssignDeterministicThingId`, L16652 — the one thing EOR got
  right there).
- **The loot-grant verb spec owns:** the MP-safe delivery mechanism (host-decided mutation + versioned
  `CF_SYNC_LOOT_GRANT_V1`-style replication, or whatever it lands on), its RNG discipline, and its JIP
  story — per the unlock conditions already recorded in SPEC.md §4.6 / SPEC-DELTA §7.1. This spec
  neither designs nor constrains that mechanism beyond the interface below.
- **Interface:** the modifier engine exposes, read-only, `(activeModifierId, Rewards{XpBonusPercent,
  GoldBonusPercent, ExtraLootChancePercent})` for the current CombatKey. The verb engine pulls it at its
  own hook point. Until the verb ships, `Rewards` blocks parse, validate, and no-op with a one-time log
  line — the stat/status halves of all four modifiers work day one (matching how the audit ports other
  "loot-half parked, stat-half ships" mechanics, e.g. TREASURE_SENSE).
- **Milestone gate:** M-EM4 (§14) activates on that spec shipping; nothing in M-EM1..3 blocks on it.

---

## 12. Verification / test plan

**Offline (unit, extends the 149-test `ClassForge.Recipes` suite):**
1. Vocabulary: `Scope: COMBAT` parse + validator rejections (SELF conditions, owner-relative targets,
   `AiProcChance`); `ProcChanceFormula` evaluation table (band edges 2/3, 5/6; adjustment stacking;
   clamp 0/35); `SELECTION_SET` weighted walk — exhaustive over all 85 outcomes of the EOR table
   (each `NextInt` value 1..85 maps to the same modifier EOR's L22814–28 walk yields); `StatusFromSelection`
   resolution + empty-selection no-op + `EFFECT_APPLIED` budget non-consumption.
2. Draw-count constancy: scripted combats (eligible-hit, eligible-miss, excluded, SafeMode) assert
   exactly 2 / 2 / 0 / 0 draws through the `GameRandomSource` seam.
3. CombatKey: selection latch + applied-set die on key change; §4.5 reconstruction from a synthetic
   mid-combat state (statused enemies present, empty runtime) re-derives selection + budgets.
4. Adapter: `EntityAdapter.Passives` unions status-attached `SKILL_` ids (mock
   `StatusEffectComponent`); ordinal sort/dedup preserved; non-`SKILL_` status passives excluded.
5. Loader: `statuses.json`/`modifiers.json` merge, M0 live-id refusal, dangling `Status` ref, weight
   validation, dataHash covers both files (hash changes when a weight changes).

**In-game SP smoke (operator, per the handoff-doc pattern):**
6. Force chance to 100 via a debug knob; fight a normal encounter: banner shows; every enemy (incl. a
   second wave) carries the status with icon/name; Frenzied/Armored numbers visible on enemy stat
   inspection; MXHP modifiers verified against §13.1 (this test *resolves* that open question); Cursed
   procs visibly ~10%; Regenerating heals 2/3/4 at the right party levels **and confirms §13.2** (enemy
   `ON_TURN_START` delivery — if silent, switch to the fallback anchor and re-run). Boss fight, scourge
   fight, siege/special encounters: log shows the exclusion context line, zero draws.
**MP (host + client, per MULTIPLAYER.md §6 / SPEC.md §8 step 8):**
7. Both peers with the pack: several eligible fights; same modifier selected on both (compare logs),
   identical enemy HP/status panels, vendor desync monitor quiet across full combats incl. multi-wave.
8. Mismatch: client without the pack → ClassForge `Block` engages (existing §9.5 machinery, named pack
   in the message). If the game supports mid-combat JIP (§13.3): join during an active modifier combat,
   verify the joiner shows the statuses and applies the same modifier to a subsequently spawned wave.

---

## 13. Open questions

> **→ see Disposition ledger (2026-08-06)** at the end of this document — every OQ below is dispositioned
> there (resolved with evidence, superseded by a gate, carried forward with an owner, or awaiting the
> operator smoke). The wording below is the design-time record and is deliberately left unedited.

1. **`CHANGE_STAT MXHP FlatPercent` runtime semantics** — percent of current-max or base; whether a
   negative delta clamps current HP; rounding vs EOR's `max(1, round(...))`/floor-at-1 (§8.1). Resolved
   by test §12.6 before M-EM3 signs off. Fallback if the verb misbehaves: pre-minted per-modifier flat
   MXHP tiers are *not* viable (enemy HP pools vary continuously), so the fallback is a small
   `STAT_CHANGE` extension computing the flat value from the target's MXHP at emission time
   (`FlatValueFrom: "TARGET_MXHP_PCT"` — same pattern as the shipped `TARGET_HP_PCT` source), still
   through the native verb.
2. **Does `ON_TURN_START` (via `CombatPhase._performSkillAbilityProcs`) fire for enemy/AI entities?**
   EOR used `TickActiveEntityCharacterStatus` instead (§8.3), which may hint the proc path is
   player-only. Fallback anchor named and signature-verified (§8.3); decided empirically at M-EM2.
3. **Does FTK2 support mid-combat join-in-progress?** Unknown repo-wide. §4.5's reconstruction ships
   regardless (also covers SP save/load mid-combat).
4. **Do status icons route through the `AssetLoader.GetImage`/`GetRender` patch** keyed by status config
   id? If not: default icon + correct tooltip text (accepted degraded state, §9).
5. **`SELECTION_SET` generality** — shipped for one consumer. Whether `Selections` should be readable by
   *owned* recipes (`SELECTION_PRESENT` outside `COMBAT` scope) is deliberately allowed by the schema
   but has no shipped consumer; revisit when a second system (e.g. a future Nemesis port) wants it.
6. **`pTrySkillProc == false` re-initialization paths** — which native flows call `SetInitiative` with
   the flag false (mid-combat rebuilds? summons?) and whether summons should ever receive the modifier
   (EOR's gate implies no; we match EOR). Verify while wiring §4.3.
7. **Enemy-side predicate surface** (§6.2) — `CHARACTER_TYPE` value set vs a dedicated `IS_ENEMY`
   condition wrapping `CharacterHelper.IsEnemy`; pick whichever matches the native predicate exactly,
   at M-EM2, and update the generated recipes accordingly.

---

## 14. Milestones

- **M-EM1 — Pack surface.** `statuses.json` + `modifiers.json` parsing, validation,
  `Configs.StatusEffects` merge target (M0-enforced), dataHash coverage, `CF_PACK_ENCOUNTER_MODIFIERS`
  skeleton with all 10 statuses + table + localization. Ships inert (no engine consumer yet) —
  verifiable via loader logs + unit tests §12.5. No game-code risk beyond one new merge write.
- **M-EM2 — Engine capability.** `Scope: COMBAT` runtime (ownerless evaluation, sentinel state keys,
  `Selections`, §4.5 reconstruction), `ProcChanceFormula`, `SELECTION_SET`/`StatusFromSelection`/
  `EVENT_BANNER`, the 7 new conditions, `pTrySkillProc` capture, the §4.4 status-passives adapter
  extension. Resolves §13.2/6/7 empirically. Unit tests §12.1–4 green; SchemaVersion 1.2 gate live.
- **M-EM3 — Content live.** Generated selection/application recipes wired to the pack; per-turn recipes
  shipped; SP smoke §12.6 (resolves §13.1); MP smoke §12.7–8. Player-facing: modifiers appear in
  eligible fights at EOR's rates with EOR's numbers, minus reward halves.
- **M-EM4 — Reward halves.** Activates when the parallel loot-grant verb spec ships; wires the §11
  interface; re-runs the MP smoke with reward assertions. Not scheduled here.

---

*Spec ends. No implementation code was written; every named game symbol above was verified either in the
current-build decompile output under `<scratchpad>/decomp/` or in the repo's checked decompiles, at the
cited lines, during this session.*

---

## Disposition ledger (2026-08-06, post-implementation)

Written after M-EM1–M-EM4 shipped (`f8c4eaf` loader + inert pack, `a92a0e3` engine capability,
`d293536` generated recipes + reward halves). Statuses: **RESOLVED** (evidence + where) ·
**RESOLVED-BY-DESIGN-CHANGE** (a verification gate superseded the question) · **CARRIED-FORWARD** (still
open, with an owner and a stated safe interim behavior) · **AWAITING-SMOKE** (in-game only; named
operator step). Nothing from §13 is dropped.

| Item | Status | Evidence / pointer | Owner if carried |
|---|---|---|---|
| **§13.1** `CHANGE_STAT MXHP FlatPercent` runtime semantics | **RESOLVED-BY-DESIGN-CHANGE** (**Gate C**) | The question was answered in the worst possible way and then routed around: `STAT_CHANGE` on `MXHP` with `FlatPercent` **throws natively** — `GetStatChangePercentValue` handles only `HP` and `XP` (`InteractableHelper.cs:1741-53`). So §13.1's *fallback* became the **mandatory primary design**: the five MaxHP halves ride `FlatValueFrom: "TARGET_MXHP_PCT"`, with the engine computing the flat delta from the target's max HP at emission using EOR's rounding, `max(1, round(\|maxhp × pct\| / 100))`. `MXHP` + `FlatPercent` is now a **hard validator Error**, so the throw is unreachable from authored content (`a92a0e3`). Also established: `AppendStat` on `MXHP` heals on a positive delta, clamps current HP only when it exceeds the new max on a negative delta, and has **no native floor-at-1** — the floor is ours. PSN §14 (`1f25aec`). **Residual smoke rider:** the rounding *tie-break* ships as `MidpointRounding.AwayFromZero`; EOR's tie-break was never confirmed, so §12.6's in-game check still owns that one decimal of behavior (recorded delta, not a design gap). | — (rounding tie-break rides §12.6) |
| **§13.2** Does `ON_TURN_START` fire for enemy/AI entities? | **RESOLVED** | **Yes.** `CombatPhase._nextTurn` (L2108) invokes the turn-start proc path **unconditionally** — there is no player-only gate, so the `START_TURN` `EVENT_PROC` route reaches AI entities. The §8.3 fallback anchor (`CombatHelper.TickActiveEntityCharacterStatus` postfix) is therefore **not needed and not shipped**; `SKILL_CF_ENCMOD_REGEN_TICK` rides plain `ON_TURN_START` with its 3-band party-level table restored (`a92a0e3`). PSN §14 (`1f25aec`). | — |
| **§13.3** Does FTK2 support mid-combat join-in-progress? | **CARRIED-FORWARD** | Still unknown repo-wide — the Wave-1 replication pass established *what* rides `JIP_SYNC_DATA` (PSN §12) but not *when* a join is permitted. As §4.5 promised, **the reconstruction ships regardless** and is content-agnostic: it auto-discovers the selection/apply recipe pair and re-derives both the selection latch and the per-enemy applied budgets from the statuses already on `CombatState.Entities`, taking zero draws (`a92a0e3`, unit-tested per §12.3). **Safe interim behavior:** identical to the SP mid-combat save/load path, which is exercised offline; a JIP peer that never happens costs nothing. Same underlying unknown as the loot-grant spec's OQ-6. | Repo-wide MP recon (DevKit); §12.8 attempts a mid-combat join opportunistically if the game allows it |
| **§13.4** Do status icons route through the `AssetLoader` patch? | **RESOLVED** | **Yes.** The asset patch carries **no `pAtlas` filter**, and native status icons are fetched through the same patched overload — so `icons/` entries keyed by status id are picked up for the Status atlas exactly as for other atlases. §9's "accepted degraded state" branch is unreachable. PSN §14 (`1f25aec`). | — |
| **§13.5** `SELECTION_SET` generality (should `Selections` be readable by *owned* recipes?) | **CARRIED-FORWARD** | Deliberately unresolved: the schema still allows `SELECTION_PRESENT` outside `COMBAT` scope, and no second consumer appeared — the only shipped user is the generated encounter-modifier pair, plus the reward read (`ResolveActiveModifierRewards`) which goes through the engine's read-only interface rather than the condition vocabulary (`d293536`). Deciding generality with one consumer would be designing against a hypothetical. **Safe interim behavior:** the permissive schema costs nothing today; the semantics are pinned by tests for the one shipped shape. | Revisit when a second system wants it (a future Nemesis port is the named candidate) |
| **§13.6** Which native flows call `SetInitiative` with `pTrySkillProc == false` | **RESOLVED** | Callsite enumeration done: the summon and revive paths **all** pass `pTrySkillProc: false`, which makes the `COMBAT_START_REAL {Value: true}` gate exact rather than approximate — summons and mid-combat rebuilds never receive the modifier, matching EOR's intent (§1.2's `__5` gate) without inheriting its incidental behavior. `pTrySkillProc` is now captured on `CombatStartEvent` (`a92a0e3`). PSN §14 (`1f25aec`). | — |
| **§13.7** Enemy-side predicate surface (`CHARACTER_TYPE` vs a dedicated `IS_ENEMY`) | **RESOLVED** (**Gate D**) | `CHARACTER_TYPE` **cannot** express "enemy": `eCharacterTypes` and `GroupIndex` are disjoint notions, so no value of the existing condition matches the native predicate. A dedicated **`IS_ENEMY {Of}`** condition wrapping `CharacterHelper.IsEnemy` (`GroupIndex == 1`) shipped in M-EM2 and is what the generated recipes emit; `CHARACTER_TYPE` is forbidden for the enemy gate. The `"..."` placeholders in §6.1 resolve to `{ "Type": "IS_ENEMY", "Of": "TRIGGER_TARGET" }` (`a92a0e3`). | — |

**Recorded deltas touching this spec's text without being open questions.** (a) `StatusFromSelection`
had a real, found-and-fixed bug: §4.5 reconstruction canonically stores the **modifier id** in
`Selections`, while `StatusFromSelection` echoed that id as if it were a **status id** — resolved by
adding `StatusFromSelectionTable` / `PercentFromSelectionTable`, which map modifier id → status/percent
through the parsed `modifiers.json` table (`d293536`). (b) The §6.3 constant-2 draw hoisting shipped as
specced and is asserted by the draw-count tests (2/2/0/0). (c) A `DebugEncounterModifierChance` knob was
added constructor-threaded (no static state) so §12.6's "force chance to 100" step needs no code edit.
(d) `REGEN_TICK` shipped single-band in M-EM1's inert pack (a recorded TODO in the pack `_source`) and
was restored to the specced 3 bands in M-EM2 once `PARTY_AVG_LEVEL` existed. (e) The reward halves
(§11 / M-EM4) shipped under the loot verb's **Mode M**, not the Mode H that spec assumed for consumer 6 —
the modifier selection is itself mirrored on every peer, so no host-private input exists; the
Treasure-Guarded extra-loot draw comes from the loot verb's **private grant stream** (an improvement over
EOR's shared-stream draw), and the stack-bump uses the loot verb's `SCALE_STACK` **round** rather than
EOR's **ceil** (recorded delta) (`d293536`).
