# FTK2.ClassForge — SPEC amendment: skill-recipe vocabulary **v1.1**

**Status:** design-locked (2026-07-25) · **Applies to:** `FTK2.ClassForge/SPEC.md` §4.6, §6, §9, §11 ·
**Merge policy:** this is an *amendment*. The base SPEC is merged from it later; until then, where the two
disagree, **this document wins**.

**Grounding rule for this amendment (binding).** Every adopted primitive below names (a) the exact game method
it hooks, (b) prefix/postfix, and (c) the *parameter* carrying its data — all verifiable in
`docs/research/game-patch-surface-notes.md` (hereafter **PSN**) or `docs/research/enum-ground-truth.md`
(hereafter **EGT**). Anything not verifiable there is **parked**, not shipped. Sources for the mechanics being
hosted: `docs/research/eor-behavior-matrix.md` (**BM**), `docs/research/eor-trait-mechanism.md` (**TM**),
`docs/research/eor-0760-content-audit.md` (**AUD**).

**Binding constraints inherited:** `docs/MULTIPLAYER.md` R1–R5 and
`docs/superpowers/plans/2026-07-25-eor-rehost-engine-charter.md` "Engineering rules" 1–7. Rule 3 in particular:
*primitives that suppress or mutate authoritative state per-client are forbidden — host-decided + vanilla-
replicated/synced, or disabled-by-default, or parked.* No adopted primitive in v1.1 suppresses authoritative
state on any client.

---

## 0. Summary of the change

| | v1 | v1.1 |
|---|---|---|
| Triggers | 7 | 7 (4 re-anchored to verified hooks, 1 deprecated-aliased) **+ 8 added = 15** |
| Conditions | 8 | 8 (4 gain an `Of` selector; all gain `Negate`) **+ 15 added = 23** |
| Effects | 4 | 4 (all extended) **+ 4 added = 8** |
| Recipe-level fields | 8 | **+ 7 added = 15** |
| Parked primitives | — | 9, each with an unlock condition (§7) |

Coverage produced by this vocabulary: **47 PORT / 20 PORT-MODIFIED / 7 PARK** across all 74 EOR mechanics —
see `docs/research/eor-rehost-coverage-matrix.md`.

---

## 1. Resolved open questions (SPEC §11)

### OQ#1 — "`eTraits` enum bridge" → **RESOLVED: there is no bridge, because there is no enum gate.**

`eTraits` is **dead code**: a repo-wide search of the native decompile for qualified `eTraits.<Member>` usages
returns zero hits outside its own declaration (PSN "Notable surprises" #1; EGT §1; TM §4). The entire runtime
trait substrate is keyed on the string prefix `"TRAIT_"` on `Thing.ConfigName`:

- `CharacterHelper.GiveTrait(Entity, string)` → `GiveTrait(Entity, InventoryHelper.CreateThing(name))` →
  `pCharacter.Get<CharacterComponent>().Things.Add(pTrait)` — no validation of any kind (PSN §3).
- `InventoryHelper.GetTraits(List<Thing>)` = `FindAll(t => t.ConfigName.StartsWith("TRAIT_"))` (PSN §3).
- `EquipmentHelper.GetEquippedThingsNonAlloc(..., pIncludeTraits: true)` treats any `TRAIT_`-prefixed `Thing`
  as equipped **regardless of slot**, which is why `Equippable.Slots: []` traits still contribute
  `Equippable.Stats` through the native `CharacterHelper.GetStat` path with zero patches (TM §3a).
- `eTraits` is additionally *stale*: it lists `TRAIT_LONGLEGS` (no live data) and omits `TRAIT_LUCKY` /
  `TRAIT_SUPPORTIVE` (both live) — EGT §1. Gating against it would be actively wrong.

**Design (replaces SPEC §3.2 / §6 "Trait bridge" rows entirely):**

1. **Storage — nothing to build.** A ClassForge trait is a `ThingConfig` in `Configs.Things` with
   `ConfigName` matching `^TRAIT_[A-Z0-9_]+$`, `Class: "TRAIT"`, `Hidden: true`,
   `Equippable.Slots: []`, `Equippable.Stats: {...}`, `Equippable.Passives: [SKILL_*]`. Grant/remove/query all
   flow through the untouched native API. **The four `CharacterHelper` patch rows in SPEC §6 are deleted.**
2. **Loader enforcement.** The pack loader **hard-rejects** a `traits.json` entry whose id does not start with
   `TRAIT_` (logged, entry skipped) — the prefix is not cosmetic, it *is* the mechanism.
3. **Selection — vanilla loadout injection (primary).** Postfix `LootDropHelper.GetAdventureLoadOut(string,
   GameRandom)` and append one `InventoryHelper.CreateThing(traitId)` per pack trait not already in `__result`;
   tag each trait config `"LOADOUT_0"` so native `LootDropHelper.GetLoadOutValue` prices it at 0 (free pick).
   The vanilla `PartyManagementDirector` take/untake action then performs `Things.Add` itself — capture and
   application are the same native call, and it is **already a networked action** carried in
   `PartyManagementDirector`'s sync payload and re-broadcast on join-in-progress (TM §2, §5).
   *Grounding caveat:* `LootDropHelper.GetAdventureLoadOut` / `GetLoadOutValue` are cited from
   `tools/out/decompile/FTK2/LootDropHelper.cs:336-377,379-395` via TM, **not** from PSN. M2 must re-read those
   two methods from the native decompile before implementing. Trait selection is not a recipe primitive, so
   this does not violate the grounding rule for §2–§4 below.
4. **Selection — direct grant (fallback).** If (3) proves fragile, call
   `CharacterHelper.GiveTrait(Entity, string)` (PSN §3, L1984) from ClassForge's own UI. Public, native,
   already-replicated entry point.
5. **Deterministic `Thing.Id`.** Injected trait `Thing`s get an id derived from
   `SHA256("CF_THING_ID|" + packId + "|" + traitId)` (invariant culture, first 12 hex chars), **never** a
   runtime GUID — so the loadout pool resolves to the same index/identity on every peer (R2; TM §1 shows EOR
   needed exactly this and got it right).
6. **Known native limitations to document, not fight:** `GetFirstTrait` shows only the *first* `TRAIT_`-prefixed
   `Thing` in `Things` order on the one-line character-sheet "Trait:" display; `RemoveAllTraits` strips vanilla
   and pack traits indiscriminately (prefix match). Both are vanilla behavior (TM §4). ClassForge does not
   patch around either.

### OQ#2 — `ON_CRIT` signal → **RESOLVED: `pIsCrit` is an explicit parameter. Trigger ships ungated.**

`InteractableHelper.ApplyStatChange` (PSN §2, L626) takes **`bool pIsCrit`** and **`Entity pTargetEntity`** as
plain parameters; `CombatHelper.ApplyAction` (PSN §1, L1871) carries the same pair one level up. Crit inflation
happens *inside* `ApplyStatChange` before `CalculateFinalDamage` is called, and `CalculateFinalDamage` (L1708)
carries **no** crit parameter and no crit information on its return value — it is **ruled out** as a hook
(PSN §6 VERDICT).

**Decision:** `ON_CRIT` hooks **`InteractableHelper.ApplyStatChange` Postfix**, fires when
`pIsCrit == true && pStatAction.Stat == "HP" && <delta was damage>`. The `[Skills] EnableOnCrit` gating knob in
SPEC §4.6 ("gate the actual dispatch behind a knob until confirmed") is **removed** — the signal is confirmed.
The `InteractableHelper.CalculateFinalDamage` row in SPEC §6 is **deleted**.

### OQ#3 — pack merge mechanism → **RESOLVED: postfix re-merge is the only viable approach, and it is idempotent by construction.**

`ConfigsHelper.LoadConfigs(string basePath)` (L383) and `ReloadConfigs(ref Configs, string basePath)` (L398)
each call `CreateConfigs()` — a **brand-new, fully empty `Configs` object** — then walk exactly one directory
tree, `<basePath>/Configs/JSON~`. **Neither accepts additional source directories**; `CONFIGS_JSON_SOURCES` is a
`private const string = "Configs/JSON~"`, a hardcoded literal, not a data-driven list (PSN §7 + Corrections).

**Decision:**
- ClassForge merges via **Postfix on `LoadConfigs` and on `ReloadConfigs`**, writing directly into the
  returned/`ref` `Configs` object's dictionaries. Native multi-root loading is not available and pack files are
  **not** made "native-loader-shaped" (that would require every pack's files to be literally named
  `Characters.json`/`Abilities.json`, since those route through `SingletonParsers` keyed by *filename*, not
  folder — PSN NOT FOUND, resolved).
- **OQ#8 (hot-reload idempotency) is resolved as a corollary:** because both entry points rebuild `Configs`
  from scratch, a re-running merge postfix never sees leftover pack data and cannot double-insert. The residual
  risk is *other systems holding a stale `Configs` reference across a reload*, which is a DevKit hot-reload
  concern, not a ClassForge merge concern. ClassForge re-registers with ParityService after every merge pass.

### OQ#4 — `ADD_CHARACTER` mapping → **RESOLVED: `{eSummonTypes Type, string Value}`, no count field.**

```csharp
public class AddCharacterAction { public eSummonTypes Type; public string Value; }
public enum eSummonTypes { NONE = -1, SPECIFIC, RANDOM, PLAYTHING, AS_FOLLOWER }
```
`Type` selects **how `Value` is resolved**, not "id vs count": `SPECIFIC` → `Value` is the verbatim
`Characters.json` config id; `RANDOM`/`AS_FOLLOWER` → `Value` is a **tag** fed to a weighted pool;
`PLAYTHING` → a doll-type pool key; `NONE` → not handled by the switch, never author it.
`CombatHelper.TryCreateSummon` returns `out Entity pSummonEntity` — **singular** — and has no count parameter
anywhere (PSN §5).

**This contradicts SPEC §4.4's placeholder** (`{"Type": "CF_SKELETON_WARRIOR", "Value": 1}`), which is
**wrong and must be corrected** to `{"Type": "SPECIFIC", "Value": "CF_SKELETON_WARRIOR"}`.

**Recipe-level mapping (`SUMMON` effect):**

```jsonc
{ "Type": "SUMMON", "Target": "TRIGGER_TARGET_POSITION",
  "SummonType": "SPECIFIC",              // eSummonTypes; default SPECIFIC
  "CharacterConfig": "CF_SKELETON_WARRIOR",  // becomes AddCharacterAction.Value
  "Count": 2 }                            // AUTHORING SUGAR ONLY
```

`Count` is expanded by the emitter into **N sequential `CombatHelper.ApplyAction` calls**, each with its own
freshly-deserialized `AddCharacterAction`, `i = 0 .. Count-1` in ascending order. It is *not* passed to the
game. `Count` is capped at **4** by the schema validator (each iteration takes its own placement/pool draws from
`CombatState.Random`; an unbounded loop is an unbounded shared-stream perturbation). `Count` defaults to 1.
An ability authored in `abilities.json` that wants N summons must likewise carry N separate
`ADD_CHARACTER` entries in its `Actions[]` — there is no single-entry "spawn N" primitive.

### OQ#5 — string-enum placeholders → **RESOLVED by reference; two SPEC example values are illegal.**

All token vocabularies are now defined **by reference to `docs/research/enum-ground-truth.md`**, which is
verbatim from the decompile. Recipes and pack data are validated against these lists at load:

| Field | Backing type | Reference |
|---|---|---|
| `ThingConfig.Rarity`, `CharacterConfig.Rarity` | `eItemRarities` (13) | EGT §2 |
| `ThingConfig.Material` | `eItemMaterialFamilies` = `{NONE, WOOD, METAL, GLASS, CLOTH, LEATHER}` | EGT §4 |
| `Equippable.Slots[]` | `eEquipmentSlots` (true enum array) | EGT §3 |
| `ThingConfig.Class` | **free string**, 63 observed vanilla values | EGT §5 |
| `ChangeStatAction.Stat` | free string ∩ `eCharacterStats` (53 members) | EGT §6 |
| `ChangeStatAction.Type` | `eDamageType` (11) — **no `WATER`** | EGT §6 |
| `StatusEffectConfig.Type` | `eStatusEffectTypes` (58) | EGT §9 |
| `eCombatActions` | 15 values | EGT §8 / PSN §10 |
| `CombatAbilityConfig.Target` / `TargetArea` / `Tendency` | `eTargets` (8) / `eTileTargetAreas` (15) / `eAiTendencies` (24) | EGT §10 |
| `CharacterComponent.CharacterType` | `eCharacterTypes` (8) | EGT §11 |
| trait ids | **not** `eTraits` — free `TRAIT_*` string space | EGT §1, OQ#1 above |

**Corrections to SPEC §7's example data:**
- `Material: "IRON"` is **illegal** (`eItemMaterialFamilies` has no `IRON`). Use `"METAL"`.
- `Rarity: "COMMON"` and `Equippable.Slots: ["MAIN_HAND"]` are **confirmed legal** — placeholder flags removed.
- `CHANGE_STAT.Type` on a non-`HP` stat: still semantically unverified at runtime; authors must use
  `"MAGICAL"`/`"PHYSICAL"`/`"REGEN"` and the validator warns (not errors) on other `eDamageType` members for
  non-`HP` stats.

### OQ#6 — "gain gold" effect → **RESOLVED: PARK. See §7.1 for the full justification.**

### OQ#10 — MP combat-authority model → **still open upstream, but no longer blocking.**

v1.1 is written so that **both** SPEC §9.3b designs are safe: every roll draws from `CombatState.Random` at a
point reached identically on every peer, and every effect is emitted through a native `eCombatActions` verb.
Under Design A (host-simulated + vanilla-replicated) clients never re-decide; under Design B every peer
independently converges. §5 states the invariants that make this true.

---

## 2. Vocabulary v1.1 — **triggers**

Every trigger row states: JSON token · hook (method + prefix/postfix) · the parameter carrying each datum ·
which entity is the recipe **owner** · MP posture.

**Universal MP posture for all triggers:** `[SYNCED]` — the trigger *observation* is a read of parameters on a
native combat method; nothing is transmitted. RNG is never consumed by a trigger itself (only by
`ProcChance`/`StatusOneOf`, §4/§5). Authority: whichever peer executes the native method; correctness comes
from R1 parity plus the ordering invariants in §5.

### 2.1 v1 triggers, re-anchored (no additions, but hooks are now exact)

| Token | Hook | Owner | Datum sources | Change vs v1 |
|---|---|---|---|---|
| `ON_ABILITY_USED` | `CombatHelper.PerformAbility` **Postfix** (PSN §1 L1155) | `pOrigin` | ability id (`pThing` + decision), `pRollData`, `pCombatDecision`, `pTarget`, `pParty`, `pGameRandom` | unchanged in spirit; hook + param list now exact |
| `ON_CRIT` | `InteractableHelper.ApplyStatChange` **Postfix** (PSN §2 L626) | `pOriginEntity` | `pIsCrit`, `pTargetEntity`, `pStatAction.Stat` | **OQ#2 resolved**; knob-gating removed; `CalculateFinalDamage` candidate deleted |
| `ON_KILL` | `InteractableHelper.ApplyStatChange` **Prefix (capture) + Postfix** | `pOriginEntity` | prefix captures `pTargetEntity` HP into `__state`; postfix fires when `HpBefore > 0 && HpAfter <= 0` | **re-anchored** from `ApplyAction` (SPEC §6) to the narrower stat-change hook — the EOR-verified shape (BM MOMENTUM row); the kill is now **origin-attributed** |
| `ON_HEAL` | `InteractableHelper.ApplyStatChange` **Postfix** | `pOriginEntity` | `pStatAction.Stat == "HP"` && delta is a heal; healed entity = `pTargetEntity` (bound to `TRIGGER_TARGET`) | re-anchored |
| `ON_DAMAGED` | — | — | — | **DEPRECATED ALIAS** of `ON_DAMAGE_TAKEN` (§2.2). Loader accepts it, logs a one-time rename warning. |
| `ON_TURN_START` / `ON_TURN_END` | `CombatHelper._onCombatSkillProc` **Postfix** (PSN §1 L1818, private) | `pCharacter` | native `EVENT_PROC` `START_TURN`/`END_TURN` | unchanged |

### 2.2 Triggers **added** in v1.1 (8)

| # | Token | Hook (method · kind) | Owner binding | Parameter carrying the data | Needed by |
|---|---|---|---|---|---|
| T1 | `ON_COMBAT_START` | `CombatHelper.SetInitiative` · **Postfix** (PSN §1 L43) | `pEntity` | `pEntity`, `pAllies`, `pCombatState` (combat identity, §6), `pResults` (effect sink) | PREPARED, OF_FOCUS |
| T2 | `ON_ABILITY_DECLARED` | `CombatHelper.PerformAbility` · **Prefix** (PSN §1 L1155) | `pOrigin` | `pRollData` (roll tier is **already resolved** and passed in), `pCombatDecision.FocusUsed`, `pTarget`, `pThing`, `pGetStat`/`pGetTileStat` **delegate params** (the `ROLL_STAT_BONUS` insertion point) | ASSASSIN, CORSAIR, PEASANT, RANGER, SCOUT, TEMPLAR, WARRIOR, WIZARD, STEADY_AIM |
| T3 | `ON_DAMAGE_DEALT` | `InteractableHelper.ApplyStatChange` · **Prefix (capture) + Postfix** (PSN §2 L626) | `pOriginEntity` | prefix stores `pTargetEntity` HP in `__state`; postfix compares; `pStatAction.Stat == "HP"`, target ∉ `pParty` | BATTLE_RHYTHM |
| T4 | `ON_DAMAGE_TAKEN` | same hook | **`pTargetEntity`** | attacker = `pOriginEntity` → bound to the `TRIGGER_SOURCE` target token; `pAbilityName` resolves `Configs.Abilities[...]` for `ABILITY_RANGED` | DRUID, SENTINEL, WARRIOR |
| T5 | `ON_STATUS_APPLIED` | `InteractableHelper.ApplyStatus` (**single-target overload**, PSN §2 L1219) · **Postfix** | `pTargetEntity` | `pStatusConfigName` (bound to the `TRIGGER_STATUS` token), `pOriginEntity`, `pGameRandom`, `pResults` | WARDBOUND, OF_STABILITY |
| T6 | `ON_CONSUMABLE_USED` | `InteractableHelper.PerformConsumableAbility` · **Postfix** (PSN §2 L538) | `pOriginEntity` | `pThing` (→ `ITEM_CLASS`/`ITEM_CONSUMABLE` conditions via `InventoryHelper.GetThingConfig`, PSN §4 L806), `pAbilityName`, `pContext` | DRUNKEN_COURAGE |
| T7 | `ON_ENEMY_ABILITY_RESOLVED` | `CombatHelper.PerformAbility` · **Postfix** | **each living recipe-holder opposed to `pOrigin`**, iterated in ascending ordinal `Entity.Guid` order | acting enemy = `pOrigin` → `TRIGGER_SOURCE`; `pRollData.Status` → `ROLL_TIER`; `pParty` decides opposition | ORACLE, JESTER |
| T8 | `ON_HEAL_PENDING` | `CharacterHelper.AddHealth(Entity, ref int pValue, ...)` · **Prefix** (PSN §3 L1342 / L1357) | `pEntity` (the heal recipient) | **`ref int pValue`** is the mutable heal amount — the only `HEAL_MODIFIER` insertion point; healer identity is carried by the engine's `ON_DAMAGE_DEALT`-style `ApplyStatChange` prefix pairing | FIELDMEDIC, MENDERS_TOUCH |

**T7 determinism note.** Multi-owner dispatch is the one place where the number of shared-stream draws scales
with the entity set. It is safe because the owner list is a deterministic function of replicated state
(living entities opposed to `pOrigin`, filtered to recipe holders, sorted by `Entity.Guid` ordinal ascending)
and because a `ProcChance` of exactly `100` takes **zero** draws (§5.2).

**T8 authority note.** `HEAL_MODIFIER` mutates a value inside the vanilla heal pipeline; it does **not**
suppress anything. Under Design A the client receives a replicated HP delta and its own prefix never runs on
that path; under Design B both peers compute the identical modifier from identical parity-guaranteed recipe
data. There is no RNG on this path — a chance-gated heal modifier is **not** permitted in v1.1.

---

## 3. Vocabulary v1.1 — **conditions**

Conditions are ANDed; an empty array is always true. Two **universal** extensions apply to *every* condition,
old and new:

- **`"Negate": true`** — inverts the condition's result. This subsumes v1's `TARGET_BASE_TYPE_NOT`, which
  becomes a deprecated alias of `{"Type":"TARGET_BASE_TYPE", "Negate":true}`.
- **`"Of": "SELF" | "TRIGGER_TARGET" | "TRIGGER_SOURCE"`** — which entity the condition reads. Default
  `SELF` (the recipe owner). Applies to `HP_THRESHOLD`, `HAS_STATUS`, `LACKS_STATUS`, `ROW`, `CHARACTER_TYPE`,
  `STATUS_COUNT`, `FOCUS_CURRENT`, `MOVED_THIS_ROUND`. This single extension removes the need for a
  `TARGET_TRACKING` primitive (§7.8).

All conditions are **pure reads of replicated state or of hook parameters**. **No condition consumes RNG.**
MP posture for every condition: `[SYNCED]`, no RNG, no authority question.

### 3.1 v1 conditions retained

`HP_THRESHOLD` (+`Of`), `HAS_STATUS` (+`Of`), `LACKS_STATUS` (+`Of`), `ROW` (+`Of`), `WEAPON_CLASS`,
`ABILITY_TAG`, `TARGET_BASE_TYPE` (+`Negate`), `TARGET_BASE_TYPE_NOT` (deprecated alias).

`HP_THRESHOLD` reads via `CharacterHelper.GetStat(Entity, string, bool)` — PSN §3 L396 — the **one** overload
the engine standardizes on (PSN "Notable surprises" #6 flags the 8-overload trap). `HP_THRESHOLD` additionally
accepts `{"Percent": n}` (current/max ×100) **or** `{"Flat": n}`.

### 3.2 Conditions **added** in v1.1 (15)

| # | Token · JSON shape | Reads | Verified source |
|---|---|---|---|
| C1 | `ROLL_TIER {Comparator: EQ\|GTE\|LTE, Value: PERFECT\|SUCCESS\|FAIL\|CRIT_FAIL}` | `pRollData.Status` (`eRollStatus`), a **parameter** of both `PerformAbility` and `ApplyAction` | PSN §1 L1155/L1871; `pRollData.Status == eRollStatus.PERFECT` cited verbatim PSN §6 L1351 |
| C2 | `HOSTILE_ACTION {Value: bool}` | `Configs.Abilities[id].Target == eTargets.ENEMY` **&&** trigger target ∉ `pParty` | PSN §6 L1351 (`abilityConfig.Target == eTargets.ENEMY`); `List<Entity> pParty` param PSN §1 L1155/L1871; EGT §10 |
| C3 | `ABILITY_STAT {Value: <eCharacterStats member>}` | the acting ability's `Interactable.Abilities[<id>].Stat` on `pThing`'s config | SPEC §4.5 shape; `InventoryHelper.GetThingConfig` PSN §4 L806; EGT §6 |
| C4 | `ABILITY_RANGED {Value: bool}` | `Configs.Abilities[<id>].IsRanged` | SPEC §4.4 field; ability id available on every trigger (`pAbilityName` on `ApplyAction`/`ApplyStatChange`, PSN §1 L1871 / §2 L626) |
| C5 | `ABILITY_REPEATED {Value: bool}` | current ability id vs the owner's `LastAbilityId` in per-battle turn-state (§6) | engine-derived from C-family triggers; no new hook |
| C6 | `CHARACTER_TYPE {Of, Value: <eCharacterTypes>}` | `CharacterComponent.CharacterType` | PSN §6 L1229–1232 reads `pComponent2.CharacterType != eCharacterTypes.NONE` verbatim; EGT §11. **Replaces ad-hoc "IsBoss" helpers** (`Value: "BOSS"`) |
| C7 | `FOCUS_SPENT {Comparator, Value: int}` | `pCombatDecision.FocusUsed` | `CombatDecisionData pCombatDecision` param PSN §1 L1155; `pCombatDecision.FocusUsed` cited verbatim PSN §6 L1357 |
| C8 | `FOCUS_CURRENT {Comparator, Value: int\|"MAX"}` | `GetStat(entity, "FOC")` vs `GetStat(entity, "MXFOC")` | PSN §3 L396; `FOC`/`MXFOC` are `eCharacterStats` members, EGT §6 |
| C9 | `STATUS_COUNT {Of, Category: HARMFUL\|BENEFICIAL\|ANY, Types?: [<eStatusEffectTypes>], Comparator, Value}` | count of statuses on the entity whose `StatusEffectConfig.Type` is in the category set | `StatusEffectComponent.Statuses` (SPEC §4.6 v1 already reads it for `HAS_STATUS`); `eStatusEffectTypes` EGT §9. `HARMFUL` default set = `{BLEED, CONFUSE, CURSE, STUN, DAZE, ENTANGLE, FIRE, INFINITE_FIRE, ICE, SHOCK, WATER, POISON, DEBUFF, DEATHMARK, RATTLED, ACID, PETRIFY, SCARE}` (authored constant, all members of EGT §9) |
| C10 | `STATUS_TYPE {Value: <eStatusEffectTypes>}` | `Configs.StatusEffects[pStatusConfigName].Type`. **`ON_STATUS_APPLIED` only** | `pStatusConfigName` param PSN §2 L1219; EGT §9 |
| C11 | `COUNTER {Name, Comparator, Value}` | per-battle counter (§6) | engine state; no hook |
| C12 | `MOVED_THIS_ROUND {Of, Value: bool}` | per-battle turn-state `MovedRound == Round` (§6), fed by an internal observer on `CombatHelper.ApplyAction` Postfix where `pAction == eCombatActions.MOVE` | `eCombatActions pAction` param PSN §1 L1871; `MOVE` is enum member 0, EGT §8 |
| C13 | `ALL_ALLIES_ACTED {Value: bool}` | for every living ally in `pParty` other than the owner, per-battle `ActedRound == Round` (§6) | `List<Entity> pParty` param PSN §1 L1155; `ActedRound` written by the engine's `ON_ABILITY_USED` observer |
| C14 | `ITEM_CLASS {Value: <ThingConfig.Class>}` | `InventoryHelper.GetThingConfig(pThing.ConfigName).Class` | PSN §4 L806 (`return Env.Configs.Things[pThingName];`); free-string class vocabulary EGT §5 |
| C15 | `ITEM_CONSUMABLE {Value: bool}` | same config's `ConsumableType != NONE` | PSN §4 L806; `ConsumableType` field per SPEC §4.5 |

---

## 4. Vocabulary v1.1 — **effects**

**Universal extension: every effect entry may carry its own optional `"Conditions": [...]`**, evaluated with the
same evaluator as recipe-level conditions, against the same trigger context. This is the single highest-leverage
generalization in v1.1 (it expresses PRIEST's roll-tiered status choice, FORTUNEBORN's conditional focus refund,
ASSASSIN's boss/non-boss split, BARD's escalating tier, JESTER's boss branch) and it adds **no** hook and **no**
MP surface — it is a pure re-use of §3.

**Effect emission rule (unchanged, now mandatory):** every effect is emitted by constructing the equivalent
`(eCombatActions, object)` pair and routing it through **`CombatHelper.ApplyAction`** (PSN §1 L1871) with the
recipe's resolved `pOrigin`/`pTarget`/`pResults`. Nothing bypasses the native action pipeline. This is what
makes SPEC §9.4's "no bespoke `_SYNC_` action" claim hold for every adopted effect.

### 4.1 v1 effects retained, with extensions

| Effect | v1 shape | v1.1 additions |
|---|---|---|
| `ADD_STATUS` | `{Target, Status}` | **`FallbackStatus`** (see IMMUNITY_FALLBACK below) · **`StatusOneOf: [ids]`** (see RANDOM_ELEMENT_CHOICE below) · **`Duration: int?`** (→ `ApplyStatus`'s `int? pDurationOverride`, PSN §2 L1219) · `Status` accepts the token **`"TRIGGER_STATUS"`** under `ON_STATUS_APPLIED` |
| `REMOVE_STATUS` | `{Target, Status}` | same `StatusOneOf` / `"TRIGGER_STATUS"` tokens |
| `STAT_CHANGE` | `{Target, Stat, StatChangeType, FlatValue\|FlatPercent, Blockable}` | **`FlatValueFrom`** / **`PercentFrom`** dynamic value sources (below) · `IsSilent: bool` (`ChangeStatAction.IsSilent`, EGT §6) |
| `SUMMON` | `{Target, CharacterConfig, Count}` | `SummonType` (`eSummonTypes`, default `SPECIFIC`); `Count` is authoring sugar expanded to N `ApplyAction` calls, capped at 4 (OQ#4) |

**`FOCUS_CHANGE` is deliberately NOT a new effect.** It is
`STAT_CHANGE {Stat: "FOC", StatChangeType: "MAGICAL", FlatValue: ±n, Blockable: false}` — `FOC` is a legal
`eCharacterStats` member (EGT §6) and `ChangeStatAction` is the verified `CHANGE_STAT` payload. Five mechanics
(PREPARED, OF_FOCUS, FORTUNEBORN, GAMBLER, THIEF-fallback) are served by an existing v1 effect.

**`IMMUNITY_FALLBACK` (6 mechanics) is a field on `ADD_STATUS`, not a primitive.**
`{"Type":"ADD_STATUS", "Target":"TRIGGER_TARGET", "Status":"STATUS_DAZE_00", "FallbackStatus":"STATUS_ATTACKDOWN_00"}`.
Implementation: emit the primary `ADD_STATUS` through `ApplyAction`, then inspect the
`List<(eAbilityResults, object)> pResults` list `ApplyAction` appends to (an explicit parameter, PSN §1 L1871);
if no status-applied result was appended, emit `FallbackStatus`. **Result-driven, not immunity-table-driven** —
no introspection of `STATUS_IMMUNITY_*` config internals is required, and it degrades correctly for *any*
reason the primary failed. Deterministic, no RNG.

**`RANDOM_ELEMENT_CHOICE` (2 mechanics) is a field, not a primitive.**
`{"Type":"ADD_STATUS", "StatusOneOf": ["STATUS_FIRE_00","STATUS_ICE_00","STATUS_SHOCK_00"]}` resolves via
`CombatState.Random.GetRandomElementFromList<T>` (verified API, PSN §10). **Exactly one draw, taken only when
the effect actually executes** (i.e. after conditions, budget and `ProcChance` have all passed). The list is
authored, so it is parity-hashed and identical on every peer.

**Dynamic value sources** (`FlatValueFrom` / `PercentFrom`), with optional `PerUnit`, `Min`, `Max`:

| Source token | Value | Verified from |
|---|---|---|
| `"FOCUS_SPENT"` | `pCombatDecision.FocusUsed` | PSN §6 L1357 |
| `"COUNTER:<name>"` | per-battle counter (§6) | engine state |
| `"STATUS_COUNT:HARMFUL"` | as C9 | EGT §9 |
| `"TARGET_HP_PCT"` | `GetStat` HP/MXHP ×100 | PSN §3 L396 |

Example (TEMPLAR): `{"Type":"ROLL_STAT_BONUS","Stat":"ATK","PercentFrom":"STATUS_COUNT:HARMFUL","PerUnit":5,"Max":20}`.

### 4.2 Effects **added** in v1.1 (4)

| # | Token · JSON shape | Hook · how it applies | MP posture |
|---|---|---|---|
| E1 | `ROLL_STAT_BONUS {Stat, Percent?, Flat?, MinDelta?, Max?, PercentFrom?, PerUnit?}` | **`CombatHelper.PerformAbility` Prefix** (PSN §1 L1155). The prefix replaces the **`Func<Entity,string,eGetStatEquippedFilters,int> pGetStat`** and/or **`Func<Entity,string,int> pGetTileStat`** *delegate parameters* with a wrapper that adds the bonus when `(entity == pOrigin && statKey == Stat)`, for this call only. Reverted implicitly — the wrapper's lifetime is the single `PerformAbility` invocation. **`ON_ABILITY_DECLARED` only.** | `[SYNCED]`, **no RNG**. Touches no persistent state, no status, no component — it changes only the stat value read during this one roll. Safe under Design A (host computes, client receives the replicated result) and Design B (both peers compute the same wrapper from parity-identical recipe data). |
| E2 | `HEAL_MODIFIER {Percent?, Flat?, MinDelta?, Scope: RECEIVED\|GIVEN}` | **`CharacterHelper.AddHealth` Prefix**, mutating **`ref int pValue`** (PSN §3 L1342/L1357). `Scope: GIVEN` matches on the healer identity captured by the paired `ApplyStatChange` prefix; `RECEIVED` matches on `pEntity`. **`ON_HEAL_PENDING` only.** | `[SYNCED]`, **no RNG permitted** (a chance-gated heal modifier is rejected by the validator — see §7.4 for why the damage-side analogue is parked). Deterministic function of parity-identical data. |
| E3 | `COUNTER_ADD {Name, Delta, Max?}` | pure per-battle state write (§6) | `[LOCAL]` state, `[SYNCED]` cause. Derived from a `[SYNCED]` trigger, never transmitted, never read by any peer but its own. No RNG. |
| E4 | `COUNTER_SET {Name, Value}` | same | same |

Counters (E3/E4 + C11) replace EOR's `ClassSkillRuntimeState.Stacks` for BARD, WARRIOR (and would serve
DUELIST if `ON_DODGE` ever lands). They are **not** a new authority surface: a counter only ever gates effects
that themselves flow through `ApplyAction`.

### 4.3 `Target` and `Status` token vocabulary

`Target` ∈ `SELF` · `CASTER` · `TRIGGER_TARGET` · `TRIGGER_TARGET_POSITION` · `ALLY_ALL`
**+ v1.1:** `TRIGGER_SOURCE` (the entity that caused the trigger — the attacker under `ON_DAMAGE_TAKEN`, the
acting enemy under `ON_ENEMY_ABILITY_RESOLVED`, the status applier under `ON_STATUS_APPLIED`) ·
`ALLY_ALL_OTHERS` (party minus the owner — WARDEN) · `ENEMY_ALL` (all living opponents — RUNEMAGE) ·
`ALLY_BY_RANK`.

**`ALLY_BY_RANK`** (replaces the "selector gap" flagged for CHRONOMANCER and KNIGHT in BM):

```jsonc
{ "Target": "ALLY_BY_RANK",
  "Rank": { "Stat": "SPD",          // eCharacterStats member, or "HP_PCT"
            "Order": "LOWEST",       // LOWEST | HIGHEST
            "Where": [ { "Type": "HP_THRESHOLD", "Comparator": "LTE", "Percent": 25 } ],
            "ExcludeSelf": true } }
```

Candidates = living allies in `pParty`, filtered by `Where` (same condition evaluator, `Of` bound to the
candidate), sorted by `Rank.Stat` then **always** by `Entity.Guid` ordinal ascending as tiebreak, first taken.
**Fully deterministic, zero RNG** — this is the point: EOR itself uses `.OrderBy(SPD).ThenBy(Guid)` for exactly
this reason (BM CHRONOMANCER). If no candidate matches, the effect is a no-op and (if
`Budget.ConsumeOn == EFFECT_APPLIED`) the budget is not consumed.

---

## 5. Recipe-level fields, determinism and MP posture

### 5.1 Fields

```jsonc
{
  "SKILL_CF_EXAMPLE": {
    "SchemaVersion": "1.1",          // NEW. Loader rejects+logs a recipe whose version it cannot run.
    "DisplayName": "…",
    "Enabled": true,                 // NEW. Per-recipe kill switch; also the "disabled-by-default" carrier
                                     //      required by charter rule 3 if a hazard-class primitive ever lands.
    "Trigger": "ON_ABILITY_USED",
    "Conditions": [ … ],
    "Effects":    [ … ],             // each entry may carry its own "Conditions": [ … ]
    "ProcChance": 20,                // = CHANCE_PCT. Semantics in 5.2.
    "AiProcChance": 20,
    "Budget": {                      // NEW. Covers ONCE_PER_ROUND / ONCE_PER_COMBAT and the per-target variants.
      "Scope": "ONCE_PER_ROUND",     // NONE | ONCE_PER_ROUND | ONCE_PER_COMBAT
                                     // | ONCE_PER_TARGET_PER_ROUND | ONCE_PER_TARGET_PER_COMBAT
      "ConsumeOn": "PROC",           // PROC (default) | EVALUATION | EFFECT_APPLIED
      "Key": "CF_GLADIATOR_SHOWMANSHIP"   // NEW. Optional shared budget namespace so a recipe *pair*
                                          // (e.g. ON_CRIT + ON_KILL halves) shares one budget.
    },
    "Cooldown": 0,                   // rounds; 0 = none. Round counter defined in §6.
    "Priority": 0,                   // NEW. Deterministic evaluation order; ties broken by recipe id ordinal.
    "VerboseLogTag": "…"
  }
}
```

Seven new recipe-level fields: **`SchemaVersion`, `Enabled`, `Budget.Scope`, `Budget.ConsumeOn`,
`Budget.Key`, `Priority`**, plus the per-effect **`Conditions`** array (§4).

**`ProcChance` semantics (= the `CHANCE_PCT` primitive, 18 mechanics).** Integer 0–100.
`100` means *no roll is taken at all*. `0` means the recipe never fires (still validated/logged).
`AiProcChance` is used instead when the owner is AI-controlled. Sequential/else-if chance chains (GAMBLER)
are **not** expressible; author two recipes with independent rolls and accept the probability shift (recorded
in the coverage matrix).

**`Budget` semantics (= `ONCE_PER_ROUND` 10 + `ONCE_PER_COMBAT` 4 mechanics, plus the per-target variants that
subsume `TARGET_TRACKING`).** `ConsumeOn`:
- `PROC` (default) — consumed only when the `ProcChance` roll succeeds. A failed roll leaves the budget open,
  so a later qualifying event in the same round re-rolls. *This is EOR's real SENTINEL behavior* (BM SENTINEL:
  "`OnceRound` is only set on success"), which its own flavor text got wrong.
- `EVALUATION` — consumed as soon as conditions pass, whether or not the roll succeeds.
- `EFFECT_APPLIED` — consumed only if at least one effect actually resolved (KNIGHT: the budget is spent only
  if a wounded ally was found).

**Recipe pairs.** A mechanic with a declare-time half and a resolve-time half (CORSAIR, PEASANT, RANGER, SCOUT,
WARRIOR, WIZARD, GLADIATOR, MARSHAL) is authored as **two recipes sharing a `Budget.Key`**, not as a
multi-trigger recipe. `Trigger` stays a single token.

### 5.2 Determinism invariants (binding — these are what make §9.3b Designs A *and* B safe)

1. **One RNG source, always.** Every roll draws from `Env.GameRun.CombatState.Random` — a `GameRandom`
   (PSN §10: `public GameRandom Random;` on `CombatState`; `GameRandom` is backed by a single seeded
   `System.Random`, seeded from `NetworkDebuggingHelper.MultiplayerSeed` in online MP). `ProcChance` uses
   `NextChance(decimal)`; `StatusOneOf` uses `GetRandomElementFromList<T>`. **`System.Random`,
   `UnityEngine.Random`, and constructing a fresh `GameRandom` are all forbidden.**
2. **Null stream ⇒ no fire, ever.** If `Env.GameRun?.CombatState?.Random` is null (no active combat), the
   recipe **does not fire** and logs a one-time skip. It **never** falls back to a seeded ad-hoc `GameRandom`.
   This is the explicit anti-pattern from EOR's `TryCreateSharedSeededGameplayRandom` /
   `RollSelectableTraitChance(random: null, …)` path (BM Legend; TM §6 hazard 1), which made ARCANE_MEMORY and
   OF_SPELLKEEPING non-lockstep. It is also the reason `SUPPRESS_CONSUME` is parked (§7.2).
3. **Draw count is a pure function of replicated state.** Evaluation order is: `Enabled` → `Trigger` match →
   `Conditions` → `Cooldown` → `Budget` → **roll** → `Effects`. Because the roll is last, no draw is taken on a
   path a peer could skip for a state-dependent reason. `ProcChance == 100` takes zero draws.
4. **Fixed iteration order everywhere.** Recipes evaluated by ascending `Priority` then ascending ordinal
   recipe id. Multi-owner triggers (T7) and multi-target effects (`ALLY_ALL`, `ENEMY_ALL`, `ALLY_ALL_OTHERS`,
   `ALLY_BY_RANK` tiebreak) iterate by ascending ordinal `Entity.Guid`. Effects apply in authored array order.
   `SUMMON` `Count` iterates ascending. Never `Dictionary`/`HashSet` enumeration order, never filesystem order.
5. **All effects ride native verbs.** Every effect becomes an `(eCombatActions, object)` pair through
   `CombatHelper.ApplyAction`; nothing writes entity state directly. No custom `_SYNC_` action is defined for
   any adopted primitive. (SPEC §9.4's designed-for fallback `CF_SYNC_RECIPE_PROC_V1` remains the named last
   resort if OQ#10 resolves badly; v1.1 does not require it.)
6. **No per-client suppression.** No adopted primitive returns `false` from a prefix, cancels a native call,
   or otherwise decides locally that an authoritative change did not happen. Auditable rule: **v1.1 contains
   exactly three prefix hooks** — T2/E1 (`PerformAbility`, replaces a delegate argument, always returns true),
   T3/T4/ON_KILL capture (`ApplyStatChange`, writes `__state` only, always returns true), and T8/E2
   (`AddHealth`, mutates `ref pValue`, always returns true). None can skip its original method.

### 5.3 SafeMode

ClassForge's `[Multiplayer] OnParityMismatch` default remains **`Block`** (SPEC §5, §9.5). If an operator
overrides it to `WarnAndSafeMode`, SafeMode for the recipe engine is **all-or-nothing: every recipe stops
evaluating.** There is no presentation-only subset of the recipe vocabulary — every primitive in §2–§4 either
mutates combat state or feeds something that does. Partial SafeMode would produce exactly the asymmetric
execution invariant 3 exists to prevent.

Per-primitive posture table (all rows share the same shape, so it is stated once rather than repeated 27 times):

| Aspect | Value |
|---|---|
| Sync class | `[SYNCED]` for every trigger, condition and effect; `[LOCAL]` for the per-battle state table (§6) and for verbose logging (R4) |
| RNG source | `CombatState.Random` only, at the points named in 5.2; **no RNG** in any trigger, any condition, `ROLL_STAT_BONUS`, `HEAL_MODIFIER`, `COUNTER_*`, `ALLY_BY_RANK`, or `FallbackStatus` |
| Authority | Whichever peer executes the native method; correctness from R1 parity + 5.2 invariants. No primitive requires host election |
| SafeMode | Whole engine off (5.3) |

---

## 6. Per-battle state model

**Rule (charter rule 4, and the direct fix for EOR's `SteadyAimUsedThisCombat` leak — TM §6 hazard 3):**
*no gameplay-affecting static state survives a combat.*

**Combat identity.**
```
CombatKey := ( ReferenceEquals-identity of Env.GameRun.CombatState , CombatState.Random.Seed )
```
`CombatState.Random` is `GameRandom` and `GameRandom.Seed` is a `public readonly int` (PSN §10) — it is
identical on every peer in an online session and changes on every new combat.

**Single-slot cache, not a dictionary.** The engine holds exactly one `(CombatKey, CombatRuntime)` pair. Any
hook that observes a `CombatKey` different from the cached one **drops the entire `CombatRuntime` and
allocates a fresh one before doing anything else.** This makes leakage structurally impossible: there is no
code path on which stale state can be read, even if an end-of-combat hook is missed. (EOR's bug was a
process-global `HashSet` whose only cleanup was a per-entity `.Remove()`.)

**`CombatRuntime` contents** — all keys are replicated identities (`Entity.Guid`) or parity-hashed authored ids
(recipe id, `Budget.Key`, counter name). Nothing is ever transmitted; everything is derived from `[SYNCED]`
triggers:

| Field | Shape | Written by | Reset |
|---|---|---|---|
| `Round` | `int`, starts 0 | Postfix on `CombatHelper.NextTurn(Env, GameRandom, out bool pIsNewRound)` (PSN §1 L793) — incremented when `pIsNewRound == true`. **This, not an unverified `TotalRounds` field, is the round-counter source of truth.** | with the runtime |
| `Budgets` | `Dictionary<(budgetKey, ownerGuid, targetGuid?), int usedAtRound>` | budget consume step (5.1) | `ONCE_PER_ROUND*` entries compare against `Round`; `ONCE_PER_COMBAT*` entries live for the runtime |
| `Cooldowns` | `Dictionary<(recipeId, ownerGuid), int expiryRound>` | after a proc | with the runtime |
| `Counters` | `Dictionary<(ownerGuid, counterName), int>` | `COUNTER_ADD`/`COUNTER_SET` | with the runtime |
| `TurnState` | `Dictionary<ownerGuid, {ActedRound, MovedRound, LastAbilityId}>` | `ON_ABILITY_USED` observer (`ActedRound`, `LastAbilityId`) and the internal `ApplyAction`-with-`pAction == MOVE` observer (`MovedRound`) | with the runtime |

**Per-run state:** none. v1.1 introduces no `GameRunData.Stats` keys. **Save schema:** unchanged (SPEC §3).

---

## 7. Parked primitives — decisions and unlock conditions

Selection principle applied: a primitive used by ≤2 mechanics with a weird or unverified surface is parked
unless it is nearly free. Each row states what would unlock it.

### 7.1 `GOLD_GRANT` / `ITEM_TAG_GRANT` — **PARK.** *(the decision OQ#6 asked for)*

**Decision:** parked, **not** shipped as a post-combat loot-hook effect.

**Why, in one sentence:** the only surface is `LootDropHelper.GetLootDropsFromEnemies` — which is (a) **not
present in `game-patch-surface-notes.md`**, so it fails this amendment's grounding rule outright, and (b)
precisely the site of EOR's worst MP behavior: per-client in-place mutation of the loot `List<Thing>` plus
"up to 8 extra loot rolls per player per combat" taken from the shared stream on a path that executes
asymmetrically across peers (AUD §3.2/§3.3; BM SCAVENGER/TREASURE_SENSE/SCHOLARS_HABIT/OF_SCAVENGING rows) —
i.e. a direct violation of charter rule 2 *and* rule 3 in a single primitive.

Combat-time gold is separately impossible to verify: `GLD` **is** an `eCharacterStats` member (EGT §6), so
`CHANGE_STAT{Stat:"GLD"}` would deserialize, but there is no verified evidence that a combat-time `CHANGE_STAT`
on `GLD` credits party gold rather than a per-entity stat, and EOR — which wanted this — never found one either
(it went to the loot hook instead). Shipping an unverified currency verb is worse than shipping none.

**Unlock:** (1) `LootDropHelper.GetLootDropsFromEnemies`'s verbatim signature added to PSN, **and** (2) a
host-decided design where the host computes the loot delta and pushes it as a versioned
`CF_SYNC_LOOT_GRANT_V1` action (host → clients, idempotent), rather than each client rolling and mutating its
own list. Tracked as a v1.2 candidate. **Consequence:** SCAVENGER → PARK; the loot halves of TREASURE_SENSE,
SCHOLARS_HABIT and OF_SCAVENGING → parked (their stat halves port).

### 7.2 `SUPPRESS_CONSUME` (and its "host-decided refund-after-consume" redesign) — **PARK.**

The redesign the brief asked to consider — postfix `InventoryHelper.Consume` (PSN §4 L392) and refund
host-side instead of prefix-cancelling — was evaluated and **rejected** for a reason that kills both forms:
**scroll consumption normally happens outside combat**, where `CombatState.Random` does not exist. Invariant
5.2#2 then forbids the roll entirely, so the primitive would be a no-op in its actual use case; the only escape
is a fresh seeded `GameRandom`, which is the exact non-lockstep fallback EOR shipped and the brief forbids.
Secondarily, re-granting a `Thing` post-consume is an inventory mutation with no verified replication path.

**Unlock:** an out-of-combat shared deterministic RNG whose draw order is guaranteed identical across peers,
**plus** a verified replicated inventory-grant verb. **Consequence:** ARCANE_MEMORY → PARK; OF_SPELLKEEPING's
code half → parked (its `INT +1` stat half ports).

### 7.3 `STATUS_APPLY_RESIST` — **PARK as designed; REDESIGNED and adopted as a cleanse.**

EOR's form (prefix `ApplyStatus`, `return false`) is a textbook per-client suppression of authoritative state
(rule 3, TM §5/§6 hazard 1) and is refused. **The behavior is instead delivered by adopted primitives:**
`ON_STATUS_APPLIED` (T5, **Post**fix — the status *is* applied, authoritatively, on every peer) +
`STATUS_TYPE` (C10) + `ProcChance` + `REMOVE_STATUS{Status:"TRIGGER_STATUS"}` (E-extension). The roll happens
inside combat, so the shared stream is available. **Semantic delta to record:** the status is briefly applied
and then removed rather than never applied — any on-apply side effect fires once, and the application is
visible for an instant. WARDBOUND and OF_STABILITY are therefore **PORT-MODIFIED**, not PARK.

### 7.4 `DAMAGE_TAKEN_MULT` — **PARK.**

`InteractableHelper.CalculateFinalDamage(Entity, int, decimal, eDamageType, bool, bool)` (PSN §2 L1708) has
**no `GameRandom` parameter**. A chance-gated reduction there must reach into
`Env.GameRun.CombatState.Random` statically and take a draw at a point that, under SPEC §9.3b Design A, only
the host executes — an asymmetric advance of the shared stream, i.e. charter rule 2's named failure. There is
also no authority signal on the call to distinguish "I am deciding" from "I am replaying a decision."

Note the deterministic (no-roll) variant is trivially addable and is *not* parked on principle — nothing in the
EOR corpus needs it, so it is simply not built. **Consequence:** SHIELDBEARER → PORT-MODIFIED as a flat
`DEF` stat trait (§coverage matrix), which needs zero primitives.

### 7.5 `STEAL_STATUS` — **PARK.** 1 mechanic (THIEF). Needs a "first status present on X from an ordered
allowlist" selector plus a bound "same status id" reference between two effects plus a not-found fallback
branch — three new schema concepts for one mechanic. **Unlock:** a `StatusSelector` sub-schema shared with 7.6.

### 7.6 `CLEANSE_RANDOM_STATUS` — **PARK.** 1 mechanic (PALADIN). Needs "the alphabetically-first harmful
status on any ally" — a *cross-entity* status selector, distinct from `ALLY_BY_RANK` (which ranks entities, not
statuses). Near-miss noted: `REMOVE_STATUS` already accepts the category string `"DEBUFF"` (SPEC §4.4), but
`REMOVE_STATUS{Target:"ALLY_ALL", Status:"DEBUFF"}` strips *every* debuff from *every* ally — dramatically
stronger than the original, so it is not offered as a modification. **Unlock:** the `StatusSelector` sub-schema.

### 7.7 `CROSS_ENTITY_COORDINATION` — **PARK as a primitive.** 1 mechanic (BEASTMASTER). Reading entity A's
recipe state from entity B's evaluation is the one design that would make the per-battle state table a shared
mutable graph rather than a set of owner-keyed leaves, and EOR's own version is silently turn-order-dependent
(BM BEASTMASTER MP-hazard column: a pet acting before its master never procs). **Not needed:** the mechanic is
re-expressed with adopted primitives by making the quarry link a real `STATUS_MARKED_00` on the target — see
BEASTMASTER's PORT-MODIFIED row.

### 7.8 `TARGET_TRACKING` — **not adopted; subsumed.** RANGER/RUNEMAGE's private `TargetGuid` becomes the
vanilla `STATUS_MARKED_00` they already apply in parallel (BM RANGER/RUNEMAGE), read back with
`HAS_STATUS {Of: "TRIGGER_TARGET"}`. SCOUT's `SeenTargets` and TRICKSHOT's round-stamped set become
`Budget.Scope: ONCE_PER_TARGET_PER_COMBAT` / `ONCE_PER_TARGET_PER_ROUND`. This is the "prefer config-shaped
content over runtime state" guidance in `docs/MULTIPLAYER.md` applied literally — a status replicates for free,
a sidecar GUID dictionary does not.

### 7.9 `ON_DODGE` — **PARK.** 1 mechanic (DUELIST). EOR detects it by scanning the results list
(`HasDodged(entity, results)`), but PSN's `eAbilityResults` investigation explicitly enumerated that file
looking for crit tokens and no dodge member is recorded anywhere in the verified surface. **Unlock:** the
verbatim `eAbilityResults` member list added to PSN with a confirmed dodge value.

### 7.10 `CONDITIONAL_STAT_MODIFIER` (a `CharacterHelper.GetStat` postfix) — **PARK.** 2 mechanics
(PACK_TACTICS, ARCANE_FOCUS). `GetStat` has **8 overloads** (PSN §3) called on an extremely hot path, in and
out of combat, with no authority or RNG context; EOR had to patch a private core overload to make it work.
Cost/benefit fails for two flat `+2`s. **Consequence:** both traits → PORT-MODIFIED as unconditional flat stats.

### 7.11 Also parked, non-primitive
- **The mastery meta-progression system** (AUD §2.1: L6838–6957, profile-persisted via `StatsHelper`, polled
  per-frame offline-only at L19741) — out of ClassForge's scope entirely; no MP story; no home in any current SPEC.
- **Runtime affix-variant minting** (`TryEnsureAffixVariantConfig`, AUD §2.5) — replaced by deterministic
  pre-minting per the Forge `_PLUS{N}` pattern; see the coverage matrix's affix-system rows.

---

## 8. Amendments to existing SPEC sections (checklist for the eventual merge)

1. **§4.4** — replace the `ADD_CHARACTER` `{Type, Value}` description and example with OQ#4's resolution.
2. **§4.6** — replace the Trigger/Condition/Effect lists with §2–§4 here; replace the `ProcChance`/`Cooldown`
   paragraph with §5.1; delete the "Not included in v1 — gain gold" paragraph and point at §7.1.
3. **§6** — delete the four `CharacterHelper` trait-bridge rows (OQ#1) and the
   `InteractableHelper.CalculateFinalDamage` row (OQ#2). Add: `CombatHelper.SetInitiative` Postfix,
   `CombatHelper.PerformAbility` Prefix, `InteractableHelper.ApplyStatChange` Prefix+Postfix,
   `InteractableHelper.ApplyStatus` (single-target) Postfix, `InteractableHelper.PerformConsumableAbility`
   Postfix, `CharacterHelper.AddHealth` Prefix, `CombatHelper.NextTurn` Postfix, and
   `LootDropHelper.GetAdventureLoadOut` Postfix (trait selection, OQ#1).
4. **§7** — `Material: "IRON"` → `"METAL"`; drop the `Rarity`/`Slots`/`ADD_CHARACTER` placeholder caveats.
5. **§9.2** — the "Trait bridge" feature row becomes "Trait `Thing` grant/removal — `[SYNCED]`, native
   `Things` replication, no ClassForge mechanism"; add rows for the per-battle state table (`[LOCAL]`) and for
   `ROLL_STAT_BONUS`/`HEAL_MODIFIER` (`[SYNCED]`, no RNG).
6. **§9.3b** — keep both designs, but record that v1.1's invariants (§5.2) make the choice non-blocking.
7. **§10** — M2's trait-bridge scope collapses to "mint configs + loadout-pool injection"; M3 gains the v1.1
   primitive set and the per-battle state model.
8. **§11** — close OQ#1, #2, #3, #4, #5, #6, #8; leave #7, #9, #10 open.

---

## 9. Design risks carried forward

1. **OQ#10 (combat authority) is still formally open.** v1.1 is safe under both designs, but `ROLL_STAT_BONUS`
   and `HEAL_MODIFIER` are the two primitives whose *value* (not existence) is computed inside a hook; if
   Design A holds *and* clients also re-run these hooks on replicated results, they would be applied twice.
   Mitigation: both are gated behind a one-line "am I replaying a replicated result?" check to be added the
   moment OQ#10 resolves; until then, verify with SPEC §8's MP smoke test (step 8) before M3 ships.
2. **`LootDropHelper.GetAdventureLoadOut` is cited from the native decompile via TM, not from PSN.** M2 must
   re-verify its signature and the `"LOADOUT_"` tag-cost convention before implementing trait selection.
3. **`CombatHelper._onCombatSkillProc` is private** (PSN §1 L1818). `ON_TURN_START`/`ON_TURN_END` depend on
   patching it via `AccessTools`; a rename in a game patch silently disables those two triggers. Charter rule 1
   ("Target found: X", fail safe) covers it, but two v1 triggers ride a private method.
4. **`STATUS_MARKED_00` carries a vanilla `+30` crit-chance bonus** (PSN §6 L1355). Using it as the quarry link
   for RANGER/RUNEMAGE/BEASTMASTER inherits that bonus. EOR already incurred it for RANGER/RUNEMAGE, but
   BEASTMASTER's port newly does — flag for the balance pass.
5. **`STATUS_COUNT`'s `HARMFUL` set is an authored constant**, not a game-provided classification. If the game
   adds status types, the set silently under-counts. Keep it in one place and validate against EGT §9 at load.
6. **`ON_ENEMY_ABILITY_RESOLVED` scales shared-stream draws with party size.** Deterministic (§5.2#4) but it is
   the highest-volume new draw source; keep `ProcChance: 100` recipes off this trigger unless intended.
