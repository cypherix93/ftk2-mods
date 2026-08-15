# SPEC — `CONDITIONAL_STAT_MODIFIER` and the EOR 0.7.0.62 percentage-trait rebalance

**Date:** 2026-08-08 · **Status:** design draft for review · **Vocabulary target:** recipe vocabulary **v1.3**
· **Owner engine:** FTK2.ClassForge ·
**Applies to:** `FTK2.ClassForge/SPEC-DELTA-v1.1.md` §4.2/§5.2/§7.10,
`FTK2.ClassForge/src/ClassForge.Recipes/Model/Vocabulary.cs` (`EffectKind`),
`FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/traits.json`,
`docs/research/eor-rehost-coverage-matrix.md` §2 rows 1–5/7/11/17, §3 stat-only rows.

**Decision this implements:** OQ-A from `docs/research/eor-0762-delta-audit.md` §6 — **adopt upstream's
rebalance** rather than stay flat and diverge.

**Grounding sources.** `docs/research/eor-0762-delta-audit.md` §1 (**DA**);
`tools/out/decompile/EOR-0.7.0.62/FTK2.BanditKingPlayable/Plugin.cs` (**EOR62**);
`tools/out/decompile/FTK2/CharacterHelper.cs` (**CH**, the live game decompile);
`docs/research/game-patch-surface-notes.md` §3 (**PSN**); `docs/research/enum-ground-truth.md` (**EGT**);
`docs/MULTIPLAYER.md` (**R1–R5**); `docs/research/eor-rehost-coverage-matrix.md` (**CM**).

**Binding constraints inherited:** MULTIPLAYER.md R1–R5; engine-charter rules 1–7; SPEC-DELTA §5.2.

---

## 0. What changed upstream, and why the park reopens

SPEC-DELTA §7.10 parked this primitive with a cost/benefit argument:

> `GetStat` has **8 overloads** (PSN §3) called on an extremely hot path, in and out of combat, with no
> authority or RNG context; EOR had to patch a private core overload to make it work. **Cost/benefit fails
> for two flat `+2`s.**

The cost side of that sentence is still true. The **benefit side is obsolete.** EOR 0.7.0.62 moved its entire
selectable-trait stat layer out of `Equippable.Stats` data and into this exact postfix (DA §1). The primitive
is no longer buying two flat `+2`s — it is buying **9 traits, one replaced mechanic, and a new stat
channel**, and it is what standing still now costs us.

Two things this primitive is emphatically **not**:

- It is **not an MP risk.** `GetStat` is a pure read. The postfix mutates a return value, never entity state,
  takes no RNG draw, and derives entirely from replicated inputs. Every peer computes the same number.
  SPEC-DELTA §5.2's invariants are untouched — no amendment is required by this document (contrast
  `STATE_HASH_CHANCE`, which needs three).
- It is **not free.** §3 is about the cost, and it is the part of this spec most likely to be wrong.

---

## 1. The patch target — where SPEC-DELTA's "8 overloads" objection actually resolves

PSN §3 lists the overloads; CH L386–L422 shows they form a **funnel**, which the park's framing did not use:

| CH line | Overload | Delegates to |
|---|---|---|
| L386 | `(Entity, eCharacterStats, bool)` | L406 |
| L391 | `(Entity, eCharacterStats, bool, bool)` | L416 |
| L396 | `(Entity, string, bool)` | L406 |
| L401 | `(Entity, eCharacterStats, eGetStatEquippedFilters)` | L406 |
| L406 | `(Entity, string, eGetStatEquippedFilters)` | L416 |
| L411 | `(Entity, string, bool, bool, bool)` | L416 |
| L416 | `(Entity, string, eGetStatEquippedFilters, bool, bool)` | **L422** |
| **L422** | `(Entity, string, eGetStatEquippedFilters, out List<(string,int)> pBonuses, bool, bool)` | **terminal** |

**All seven public overloads funnel into L422.** One postfix covers the whole surface. The "8 overloads"
objection dissolves into "pick the right one."

### 1.1 Patch L422, not L416 — and what that buys over EOR

EOR patches **L416** (EOR62 L8952 registers `AccessTools.Method(typeof(CharacterHelper), "GetStat", [Entity,
string, eGetStatEquippedFilters, bool, bool])`, postfixed by `CharacterHelper_GetStat_Core_Postfix` at
L24415). L416 is *not* terminal — it delegates to L422 — so EOR's postfix misses every **direct** L422 call.
That is why EOR needs a **second, separate** patch on `UIHelper.GetBreakdownStats` (EOR62 L8961): the
character-sheet breakdown calls L422 directly for its `out pBonuses` list, and their core postfix never sees
it.

Patching L422 instead gives us:

1. **One patch instead of two**, covering both gameplay reads and the UI breakdown.
2. **Itemized tooltips for free.** L422's `out pBonuses` is the list the character sheet renders. Appending
   `("TRAIT_LIGHT_FOOTED", +4)` makes the bonus *show up in the breakdown as its own line*, rather than
   silently inflating a total. EOR cannot do this from L416 and papers over it with a bespoke UI patch.
3. **No double-application risk** — patch exactly one overload, and every logical call is modified exactly
   once. **Patching both L416 and L422 would double-apply every bonus.** This is the single most likely
   implementation bug in this spec; M-CS1's test matrix exists for it.

**Decision: postfix `CharacterHelper.GetStat` at CH L422 only.** Appending to `pBonuses` is deferred to
M-CS4 (it is a UI nicety, and `pBonuses` is `null` on most call paths — see OQ-CS3).

---

## 2. Schema

New `EffectKind` member — but note it is an **unusual effect**: it does not fire from a trigger, it is a
standing modifier attached to a trait/class via `Passives[]` and consulted on every stat read. It is closer
to a declaration than an action. It therefore lives in its own pack file section rather than in
`skillrecipes.json` (see §2.2).

```json
{
  "Id": "STAT_CF_TRAIT_LIGHT_FOOTED_EVD",
  "Kind": "CONDITIONAL_STAT_MODIFIER",
  "Stat": "EVD",
  "Percent": 20,
  "Floor": 1,
  "RequiresPositiveBase": true,
  "Conditions": []
}
```

| Field | Type | Req | Meaning |
|---|---|---|---|
| `Stat` | string, must be a legal `eCharacterStats` member (EGT-validated) | yes | Target stat key |
| `Percent` | int, `-99..500` | one of | Fixed percentage delta |
| `PercentFrom` | value-source token, e.g. `"COUNTER:arcane_focus"` | one of | Scaling source, mirroring the existing `ROLL_STAT_BONUS{PercentFrom, PerUnit, Max}` shape (CM §1 row 30, WARRIOR) |
| `PerUnit` | int | with `PercentFrom` | Percent per unit of the source |
| `Max` | int | no | Cap on the resolved percentage |
| `Flat` | int | no | Flat delta, applied after percentage (for the `THRN` case, §4.3) |
| `Floor` | int | no, default `1` | Result is never reduced below this. Matches EOR's `Math.Max(1, …)` |
| `RequiresPositiveBase` | bool, default `true` | no | Skip when the incoming value is `<= 0`. Matches EOR's `result > 0` guards |
| `Conditions` | condition array | no | Standard vocabulary conditions, evaluated in the stat-read context (§3.3 constrains which are legal) |

Rounding matches EOR exactly (EOR62 L24571): `result += Math.Max(1, Mathf.CeilToInt(result * pct))` for
positive deltas — note this means **a positive percentage always moves the value by at least 1**, so a `+12%`
on a base of `1` yields `2`, not `1`. For negative deltas, EOR's KNIFE_EDGE form is
`result = Math.Max(1, result - Mathf.CeilToInt(result * pct))` (L24567). Both are reproduced verbatim; the
asymmetry is upstream's, not ours.

### 2.2 Where these live

A new optional `statmodifiers.json` per pack, keyed by id, referenced from a class's or trait's `Passives[]`
exactly as skill recipes are. Rationale: `skillrecipes.json` entries are trigger→condition→effect records and
the parser/validator is built around that shape; a standing modifier has no trigger and would need a
synthetic one. Keeping it a separate file avoids weakening the recipe schema for a different kind of object.

---

## 3. The cost problem — this is the part to get right

### 3.1 Reentrancy: the sharp edge

**A resolver that calls `GetStat` re-enters the postfix and recurses until the stack dies.** EOR sidesteps it
by only reading non-`GetStat` state (`ClassSkillRuntimeState.ArcaneFocusStacks`, and
`PartyHasPetOrMercenary()` which scans `GameRunData.Entities`).

Two enforcements, both required:

1. **A `[ThreadStatic] bool _inGetStatPostfix` reentrancy guard.** On re-entry the postfix returns
   immediately, yielding the unmodified base value. Cheap, and it converts a stack overflow into a
   well-defined value.
2. **Validator rejection of stat-reading conditions in `Conditions`.** Conditions that resolve via `GetStat`
   (`HP_THRESHOLD`, `STATUS_COUNT`, anything added later that reads a stat) are **errors** in a
   `CONDITIONAL_STAT_MODIFIER` context — a static guarantee, so the runtime guard is a backstop and not the
   mechanism. The legal condition set is enumerated in §3.3.

### 3.2 Hot-path budget

`GetStat` L422 is called from combat resolution, the overworld, and every UI redraw. The postfix must be
near-free when nothing applies — the common case for most entities.

Design:

- **A per-entity applicable-modifier index**, built once per `(entity, generation)` and stored in the
  `CombatKey`-invalidated runtime already used for budgets (SPEC-DELTA §6). Lookup is a dictionary hit on
  `Entity.Guid`; entities with no modifiers get a cached empty sentinel and return after one lookup.
- **Keyed by stat within that index** — `Dictionary<string, List<Modifier>>` — so an `EVD` read never
  iterates `HP` modifiers. EOR runs a linear `if`-chain of string comparisons over every trait on every call
  (EOR62 L24516–L24570); we should not copy that.
- **Invalidation** on the existing combat-generation counter, plus on equipment/trait change. **[UNVERIFIED]:
  which event signals a loadout change outside combat** — needs a PSN pass (OQ-CS2). Until it is known, the
  index must be rebuilt on a cheap validity stamp rather than cached indefinitely.

**No performance claim is made in this spec.** M-CS2 requires a measured before/after on a real frame
budget, and if it fails, §7's fallback (data-only approximation) is the honest exit.

### 3.3 Legal conditions

Only conditions resolvable without a stat read and without combat context:
`COUNTER`, `HAS_STATUS`, `LACKS_STATUS`, `STATUS_TYPE`, `CHARACTER_TYPE`, `ENTITY_TAG`,
`CONFIG_NAME_CONTAINS`, `IS_ENEMY`, and a new `PARTY_HAS_FOLLOWER` (§4.2). Everything else is a validator
error in this context — including all trigger-scoped conditions, which have no meaning here.

### 3.4 Composition order

With multiple modifiers on one stat the result is order-dependent (`+20%` then `+15%` ≠ `+15%` then `+20%`
once `Math.Max(1, ceil())` is in play). **Order: ascending ordinal modifier `Id`**, matching SPEC-DELTA
§5.2#4's fixed-iteration-order rule. Each modifier composes on the running value, reproducing EOR's
sequential `if`-chain semantics rather than summing percentages first. Documented, tested, and stable across
peers.

---

## 4. The content: EOR 0.7.0.62's table

### 4.1 The seven straightforward rows

From DA §1.2 (EOR62 L24516–L24570). Each becomes one `CONDITIONAL_STAT_MODIFIER` with no `Conditions`, and
the corresponding flat entry is **removed** from `traits.json`'s `Equippable.Stats` — mirroring
`RemoveLegacyFlatSelectableTraitStats` (EOR62 L20178), except we do it at authoring time rather than by
mutating a config at load.

| Trait | Stat | Percent | CM row (was) |
|---|---|---|---|
| `TRAIT_LIGHT_FOOTED` | `EVD` | `+20` | §2 row 1 (PORT, flat `EVD +10`) |
| `TRAIT_TREASURE_SENSE` | `LCK` | `+15` | §2 row 6 (stat half) |
| `TRAIT_TOUGHENED` | `HP` / `DEF` | `+20` / `+15` | §2 row 2 |
| `TRAIT_STEADY_AIM` | `AWR` | `+12` | §2 row 7 (stat half) |
| `TRAIT_STREETWISE` | `TAL` | `+15` | §2 row 3 |
| `TRAIT_WARDBOUND` | `RES` | `+20` | §2 row 8 (stat half) |
| `TRAIT_SCHOLARS_HABIT` | `INT` | `+12` | §2 row 9 (stat half) |
| `TRAIT_KNIFE_EDGE` | `HP` | `−5` (`Floor:1`) | §2 row 5 |

`TRAIT_KNIFE_EDGE`'s `CRT` half stays flat data at `+10` — EOR did not move it into the postfix, and neither
should we.

### 4.2 `TRAIT_PACK_TACTICS` — conditional, restored

CM §2 row 11 currently ships an **unconditional `PHY +1`**, halved from EOR's `+2` precisely because the
condition could not be expressed. It becomes what it always should have been:

```json
{ "Id": "STAT_CF_TRAIT_PACK_TACTICS_PHY", "Kind": "CONDITIONAL_STAT_MODIFIER",
  "Stat": "PHY", "Percent": 10,
  "Conditions": [ { "Kind": "PARTY_HAS_FOLLOWER", "Of": "SELF", "Kinds": ["PET", "MERCENARY"] } ] }
```

New condition `PARTY_HAS_FOLLOWER`, mirroring EOR's `PartyHasPetOrMercenary()` (EOR62 L24576): scan
`GameRunData.PlayerFollowers` for a `FollowerState` whose `FollowerID` resolves to an entity with a
`CharacterComponent` that is `CharacterHelper.IsPet` or `IsMercenary`. All replicated state; no stat read, so
it is §3.3-legal. **This retires CM's "halved to +1 because it is now always on" balance note.**

### 4.3 `TRAIT_ARCANE_FOCUS` — a mechanic replacement, not a number change

The only row where upstream changed *what the trait does*. 0.60: conditional `MAG +2` while Focus ≥ 2. 0.62
(EOR62 L24522): a stacking in-combat buff — `ArcaneFocusStacks × 5%` MAG, stacks earned on successful
magical attacks, decayed at turn start.

This maps onto vocabulary we already have, which is the pleasant part:

- **Stacks are a counter.** `COUNTER_ADD{arcane_focus, 1, Max:N}` on a magical-attack trigger, exactly the
  WARRIOR rage pattern (CM §1 row 30).
- **The stat read scales off it**:
  `{"Stat":"MAG","PercentFrom":"COUNTER:arcane_focus","PerUnit":5,"Max":<cap>}` — the same `PercentFrom` /
  `PerUnit` / `Max` shape `ROLL_STAT_BONUS` already uses.
- **Earning the stack needs `DAMAGE_TYPE{MAGICAL}`**, the new condition DA §3 flagged from EOR62's
  `WasMagicalDamageAppliedTo` (L7006). That is a v1.3 candidate in its own right and is a **dependency** of
  this row.

**Two gaps, stated plainly:**

1. **The stack cap is [UNVERIFIED].** EOR62 L24522 applies `stacks × 0.05` with no visible ceiling at that
   site. A cap must be read off the stack-increment site before authoring, not guessed — an uncapped
   percentage on `MAG` is a balance hole.
2. **Counters do not decay.** Our `COUNTER_*` effects set and add; EOR decays `TemporaryThorns` by ×0.25 at
   turn start (EOR62 L6768) and expires `ArcaneFocusStacks` on a generation check. **We have no decay
   primitive.** Options: (a) `COUNTER_SET{arcane_focus, 0}` on `ON_TURN_END` — simpler, harsher, and a
   recorded deviation; (b) a new `COUNTER_DECAY{Factor}` effect. **Recommend (a) for v1**, with (b) noted as
   the follow-up if playtesting says the trait feels bad. Either way it is a **recorded deviation from EOR**,
   not a silent one.

### 4.4 `THRN` — deferred

EOR's new thorns channel (`TemporaryThorns`, EOR62 L24528, decaying ×0.25/turn) is legal — `THRN` is a real
`eCharacterStats` member (game decompile `eCharacterStats.cs` L34; EGT §6 stat list). But nothing in the
selectable-trait or affix corpus feeds it; it exists for EOR's class-skill runtime. **Out of scope for this
spec.** The `Flat` field (§2) is specified so a future consumer needs no schema change.

---

## 5. Balance consequences — flag, do not bury

Percentage-of-computed-stat is applied **after** equipment, so these traits now **scale with gear** where
they previously did not. A `+20%` EVD is worth far more on a late-game evasion build than `EVD +10` ever was,
and far less at level 1. This is upstream's balance decision and we are adopting it deliberately, but it
interacts with two things CM already tracks:

- AUD §2.1's noted class stat-envelope power creep (LCK 50–95 vs vanilla flat 50) now **compounds
  multiplicatively** with `TRAIT_TREASURE_SENSE`'s `+15% LCK`.
- CM §5 note 6's balance-pass ledger gains these rows and loses two (`PACK_TACTICS` and `ARCANE_FOCUS`'s
  "halved because unconditional" entries, both retired by §4.2/§4.3).

`Math.Max(1, ceil(...))` also means every listed trait is **strictly positive even on a base of 0–1**, so
these never round away to nothing on low stats.

---

## 6. Milestones

| ID | Deliverable | Verification |
|---|---|---|
| **M-CS1** | `EffectKind.CONDITIONAL_STAT_MODIFIER`, `statmodifiers.json` parse/validate (stat key vs EGT, `Percent`/`PercentFrom` exclusivity, §3.3 condition allowlist), pure composition engine with EOR's rounding | `ClassForge.Recipes` suite: rounding vectors vs EOR62 L24571/L24567; §3.4 ordering determinism; validator-rejection per rule; **explicit double-application test** (§1.1) |
| **M-CS2** | `CharacterHelper.GetStat` **L422** postfix + per-entity keyed index + reentrancy guard | Plugin build vs `tools/bin/refs`; **measured** hot-path cost before/after; reentrancy test asserting a stat-reading resolver terminates |
| **M-CS3** | `PARTY_HAS_FOLLOWER` condition; the 8 rows of §4.1 + `PACK_TACTICS` authored; flat entries removed from `traits.json` | PackCheck; CM rows updated |
| **M-CS4** | `ARCANE_FOCUS` (**depends on** `DAMAGE_TYPE{MAGICAL}` and OQ-CS1); optional `pBonuses` itemization | PackCheck; deferred if the dependency slips |

M-CS1–M-CS3 are independently shippable. M-CS4 is not, and should not block them.

---

## 7. Fallback if M-CS2's measurement fails

If the postfix proves too expensive on a real frame budget, the honest exit is **not** to ship it slowly.
It is to approximate in data: keep flat `Equippable.Stats` but re-tune the constants to the percentage
values evaluated against a mid-game reference stat line, and record the approximation in CM as a deliberate
deviation. That loses gear-scaling and the `PACK_TACTICS` condition, and it is strictly worse — but it is
worse *and honest*, which beats a hitch on every UI redraw.

---

## 8. Open questions

- **OQ-CS1 — [UNVERIFIED]** `ArcaneFocusStacks`' cap and its exact earn condition (§4.3). Read the increment
  site in EOR62 before authoring. Gates M-CS4.
- **OQ-CS2 — [UNVERIFIED]** What signals a loadout/equipment change outside combat, for index invalidation
  (§3.2)? Needs a PSN pass.
- **OQ-CS3** — Is `pBonuses` non-null often enough for the itemized-tooltip win (§1.1) to be worth wiring?
  CH L422 sets `pBonuses = null` on entry and populates it conditionally; most gameplay callers discard it.
  Cheap to check, deferred to M-CS4.
- **OQ-CS4** — Do we mirror `RemoveLegacyFlatSelectableTraitStats` at **runtime** for saves created before
  this change? Our flat stats live in our own authored `traits.json`, not a mutated game config, so removing
  them at authoring time is sufficient **for our packs** — but a player carrying a save made under the flat
  build will see stat totals change on update. Believed acceptable (this is a mod-version change, and CM
  already records balance passes as non-blocking), but it should be an explicit call, not a discovery.
- **OQ-CS5** — Interaction with any *other* `GetStat` postfix. Ours would be the only one in this repo today
  (encounter modifiers ride `Configs.StatusEffects`, not a stat postfix), but EOR installs one at L416 and
  **both mods can be installed together**. Two postfixes on different overloads in the same funnel compose
  multiplicatively in an order Harmony does not guarantee. **This needs a decision:** detect EOR's presence
  and stand down, or document the combination as unsupported. Leaning toward documenting it as unsupported,
  consistent with how the re-host treats EOR generally.
