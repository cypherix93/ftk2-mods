# SPEC — `STATE_HASH_CHANCE`: a deterministic, draw-free chance condition

**Date:** 2026-08-08 · **Status:** design draft for review · **Vocabulary target:** recipe vocabulary **v1.3**
· **Owner engine:** FTK2.ClassForge (recipe engine) ·
**Applies to:** `FTK2.ClassForge/SPEC-DELTA-v1.1.md` §3.2/§5.2/§7.2/§7.3/§7.4,
`FTK2.ClassForge/src/ClassForge.Recipes/Model/Vocabulary.cs` (`ConditionKind`),
`docs/research/eor-rehost-coverage-matrix.md` §2 rows 8/14/15, §3 rows 7/13.

**Grounding sources.** `docs/research/eor-0762-delta-audit.md` §2.3 (**DA**) and the decompile it cites,
`tools/out/decompile/EOR-0.7.0.62/FTK2.BanditKingPlayable/Plugin.cs` (**EOR62**);
`docs/research/game-patch-surface-notes.md` (**PSN**); `docs/MULTIPLAYER.md` (**R1–R5**);
`FTK2.ClassForge/SPEC-DELTA-v1.1.md` (**SPEC-DELTA**); `docs/research/eor-rehost-coverage-matrix.md` (**CM**).

**Binding constraints inherited:** MULTIPLAYER.md R1–R5; engine-charter rules 1–7; SPEC-DELTA §5.2
determinism invariants (this document proposes **amendments** to invariants 1, 2 and 6 — see §4).

House rule restated: anything not verifiable in a cited decompile line is marked **[UNVERIFIED]** and carried
as an open question, not silently assumed.

---

## 0. The one-sentence version

A recipe condition that yields a stable pseudo-random pass/fail verdict by **hashing a declared tuple of
replicated state** plus an explicit salt — taking **zero RNG draws** — so that a chance gate becomes
expressible at hook points that have no `GameRandom` and at which no peer may unilaterally advance the
shared stream.

---

## 1. Why this exists: the park it dissolves

SPEC-DELTA §7.4 parks `DAMAGE_TAKEN_MULT` on a specific, correct observation:

> `InteractableHelper.CalculateFinalDamage(Entity, int, decimal, eDamageType, bool, bool)` (PSN §2 L1708)
> has **no `GameRandom` parameter**. A chance-gated reduction there must reach into
> `Env.GameRun.CombatState.Random` statically and take a draw at a point that … only the host executes — an
> asymmetric advance of the shared stream, i.e. charter rule 2's named failure.

Every word of that stays true. What it did **not** consider is the third option: *take no draw at all.*

EOR 0.7.0.62 ships exactly that (**DA §2.3**, EOR62 L26754, called from the `CalculateFinalDamage` postfix at
L26733):

```csharp
private static bool ShouldShieldbearerMitigate(Entity target, int finalDamage)
{
    int round = (RouterHelper.Env?.GameRun?.CombatState?.TotalRounds).GetValueOrDefault();
    int hp    = (target != null && target.Has<CharacterComponent>()) ? CharacterHelper.GetHealth(target) : 0;
    string text = $"{target?.Guid}|{round}|{hp}|{finalDamage}|SHIELDBEARER";
    uint h = 2166136261u;                                  // FNV-1a offset basis
    for (int i = 0; i < text.Length; i++) { h ^= text[i]; h *= 16777619; }
    return h % 100 < 20;                                   // 20%, zero draws
}
```

The gate is a **pure function of state every peer already has**. Every peer computes the same verdict, the
shared stream is untouched, and the vendor desync detector's draw-count comparison sees nothing because there
is nothing to see.

This is worth stating precisely, because it is the whole argument: *chance* and *randomness* are separable.
A recipe author wants "this fires about 20% of the time." They do not require that the 20% be drawn from the
lockstep stream. Decoupling the two buys hook points the stream cannot reach.

---

## 2. Schema

New `ConditionKind` member, added to `Vocabulary.cs`'s enum and to a new `V13OnlyConditions` list alongside
the existing `V11OnlyConditions` / `V12OnlyConditions`:

```json
{
  "Kind": "STATE_HASH_CHANCE",
  "Percent": 20,
  "Salt": "CF_SHIELDBEARER_MITIGATE",
  "Inputs": ["SELF_GUID", "COMBAT_ROUND", "SELF_HP", "TRIGGER_DAMAGE"],
  "Negate": false
}
```

| Field | Type | Required | Meaning |
|---|---|---|---|
| `Percent` | int 1–99 | yes | Pass probability across the input space. 0 and 100 are **validator errors** — use `Enabled:false` or omit the condition; a constant gate written as a hash is a bug, not a style |
| `Salt` | string, `^[A-Z0-9_]{4,64}$` | yes | Decorrelates two conditions sharing an input tuple. **Must be unique per authored condition** — the validator errors on a duplicate `(Salt, Inputs)` pair across the pack |
| `Inputs` | ordered array of input tokens, 2–8 entries | yes | The state tuple. Order is significant and is the authored order |
| `Negate` | bool | no | Standard condition negation, as elsewhere in the vocabulary |

### 2.1 Input tokens (v1.3 initial set)

Every token must resolve to a value that is **replicated and identical on every peer at the moment of
evaluation**. That is the entire correctness contract; §3.2 states how it is enforced.

| Token | Source | Notes |
|---|---|---|
| `SELF_GUID` | owner `Entity.Guid` | Ordinal string |
| `TRIGGER_SOURCE_GUID` | triggering entity's `Entity.Guid` | Empty string when absent |
| `TRIGGER_TARGET_GUID` | triggered-on entity's `Entity.Guid` | Empty string when absent |
| `COMBAT_ROUND` | `CombatState.TotalRounds` | 0 outside combat |
| `COMBAT_SEED` | the combat's seed | Ties a verdict to one combat; **strongly recommended** in every tuple (see §5.2) |
| `SELF_HP` | `CharacterHelper.GetHealth(owner)` | Wrapped per §3.3 |
| `SELF_FOCUS` | `CharacterHelper.GetFocus(owner)` | Wrapped per §3.3 |
| `TRIGGER_DAMAGE` | the damage value in scope | Only legal under damage-carrying triggers; validator-enforced |
| `TRIGGER_ITEM_ID` | `Thing.ConfigName` in scope | Only legal under item-carrying triggers |
| `ABILITY_ID` | ability name in scope | Only legal under ability-carrying triggers |
| `RUN_SEED` | `GameRunData.MapGenSeed` | For out-of-combat gates |
| `ENCOUNTER_GUID` | `AdventureState.EncounterGUID` | For out-of-combat gates |

Trigger-scoped tokens (`TRIGGER_*`, `ABILITY_ID`) are **validator-rejected** under a trigger that cannot
supply them, rather than silently resolving to empty — the `ROLL_TIER{EQ FAIL}` silently-dead-condition class
recorded in CM implementation-update 2 is a mistake worth not repeating.

### 2.2 The hash

Canonical string: the salt, then each input's resolved value in authored order, joined by `|`:

```
"CF_SHIELDBEARER_MITIGATE|<v0>|<v1>|…"
```

Integers render invariant-culture; nulls render as the empty string. The digest is **FNV-1a 32-bit** over the
UTF-16 code units of that string, matching EOR62 L26754 exactly, and the verdict is `h % 100 < Percent`.

Deliberately matching EOR's function rather than picking a better one: it makes any future
behavior-comparison against upstream a direct read rather than an argument, and the security properties of
the digest are irrelevant here (§5.3). It lives in `ClassForge.Recipes` as a pure static with no game
dependency, so the offline suite tests it directly.

---

## 3. Determinism and MP posture

### 3.1 What it does *not* do

It takes no draw, so it cannot perturb the shared stream, cannot change draw counts the vendor desync
detector compares, and cannot make one peer's stream diverge from another's. On the axis that motivated the
park, it is strictly safer than the alternative — this is a **reduction** in MP surface, not an addition.

### 3.2 The one real hazard: input replication

The verdict is only agreed if every input is agreed. A tuple that includes client-local state produces
peer-specific verdicts with no draw-count anomaly to reveal it — a **silent** divergence, which is worse than
a loud one.

Three enforcements:

1. **Closed token set.** `Inputs` accepts only §2.1's tokens. Authors cannot reach arbitrary state. Adding a
   token to the set is a spec change that must argue its replication, exactly as a new trigger must argue its
   hook point.
2. **Validator trigger-scoping.** As §2.1.
3. **A parity-visible digest.** The pack hash already covers recipe JSON under R1, so two peers running
   different `Inputs`/`Salt`/`Percent` for the same recipe id is caught by the existing mismatch report
   before combat, not discovered as a desync during it. **No new transport, no new registration.**

### 3.3 Null-safety

Each input resolves through the equivalent of EOR62's `SafeCombatSeedValue` (L6587) — a try/catch yielding
`0`. Rationale: a thrown resolver at a hot hook point is a crash; a `0` is a wrong-but-agreed value, and
agreement is the property that matters. **This is a deliberate trade and must be logged once per
`(recipe id, input token)` pair at Warn**, so a systematically-throwing resolver is visible rather than
silently degrading a gate to a constant.

---

## 4. Amendments to SPEC-DELTA §5.2

`STATE_HASH_CHANCE` is compatible with invariants 3, 4 and 5 unchanged, and **strengthens** 3 (it takes zero
draws on every path). Three need amending, and one of them is subtle enough to be the crux of this document.

**Invariant 1 ("One RNG source, always")** — amend to: *every roll* draws from
`Env.GameRun.CombatState.Random`. `STATE_HASH_CHANCE` is **not a roll** and takes no draw; it is a pure
function of declared replicated state. The clause forbidding `System.Random`, `UnityEngine.Random` and
constructing a fresh `GameRandom` **stands unchanged and still binds** — this primitive introduces no RNG
object of any kind. That distinction is what separates it from EOR's
`TryCreateSharedSeededGameplayRandom`, which the invariant exists to forbid.

**Invariant 2 ("Null stream ⇒ no fire, ever")** — amend to apply to *recipes that roll*. A recipe whose only
chance gate is `STATE_HASH_CHANCE` and whose effects take no draw **may** fire with a null
`CombatState.Random`, because there is no stream to be non-lockstep with. The anti-pattern invariant 2
targets is falling back to *a seeded ad-hoc `GameRandom`*; taking no RNG at all is the opposite move. This
amendment is what makes §5.2 of this document (out-of-combat use) possible at all.

**Invariant 6 ("No per-client suppression")** — this is the one to get right, because the naive reading is
that a state hash makes suppression legal, and that reading is **wrong**.

Invariant 6 forbids a client "deciding locally that an authoritative change did not happen." A state-hash
gate removes the *locally* — every peer decides identically. It does **not** remove the second problem:
a prefix returning `false` skips the vanilla method, so a peer **not running this mod, or running a
different version of it**, still executes the change. Symmetric decision-making is necessary but not
sufficient; the guarantee also requires that every peer be running the same recipe set, which is R1 parity
enforcement.

Proposed amended wording:

> **6. No unilateral suppression.** No adopted primitive may cancel or skip a native call on the basis of
> state that is not provably identical on every peer. A primitive that suppresses on the basis of a
> `STATE_HASH_CHANCE` verdict over §2.1 tokens is permitted **only** when the recipe's pack is under R1
> parity enforcement and the mismatch policy is fail-closed, because the guarantee is "every peer decides
> the same way," and a peer without the recipe is not a peer that decides.

That is a genuinely weaker invariant than v1.1's, and it should be adopted **only** alongside the audit
clause: v1.1's boast that "v1.1 contains exactly three prefix hooks, none of which can skip its original
method" is a property worth keeping countable. Any suppression hook added under the amended rule must be
enumerated in the same place, with its parity dependency named.

---

## 5. What this unlocks, honestly graded

### 5.1 `TRAIT_SHIELDBEARER` (CM §2 row 15) — **unlocked, high confidence**

`DAMAGE_TAKEN_MULT` becomes expressible: a `CalculateFinalDamage` postfix that reduces damage, gated by
`STATE_HASH_CHANCE{Percent:20, Inputs:[SELF_GUID, COMBAT_ROUND, SELF_HP, TRIGGER_DAMAGE, COMBAT_SEED]}`.
A postfix mutating a return value is **not** suppression — invariant 6 is not even engaged. This is the clean
case, and it retires CM's recorded balance debt (our `DEF +1` substitute is ≈2× stronger than EOR's
expected 0.5 damage/hit).

### 5.2 `TRAIT_ARCANE_MEMORY` (CM §2 row 14) and `OF_SPELLKEEPING` (CM §3 row 7) — **partially unlocked, two blockers remain**

SPEC-DELTA §7.2's park has two independent legs. The hash resolves the first and not the second:

- **Leg 1 — "scroll use is out of combat, `CombatState.Random` is null, so invariant 5.2#2 forbids the
  roll."** Resolved. No roll is taken; §4's invariant-2 amendment permits the fire.
- **Leg 2 — "the original is a prefix `return false` = per-client suppression."** *Not* resolved by the hash
  alone. It is admissible only under the amended invariant 6, i.e. only with fail-closed R1 parity. That is
  a real dependency on DevKit's mismatch policy, not a formality.
- **Leg 3, newly identified — [UNVERIFIED].** Does `InventoryHelper.Consume` even execute on every peer for
  a given scroll use, or only on the acting client with the result replicated? If the latter, a symmetric
  decision is irrelevant because only one peer ever decides, and the correct design is a replicated
  inventory-grant verb (the loot-grant verb's shape), not a suppression prefix. **This must be verified
  before either row is reclassified.** CM §2 row 14's second stated blocker — "a verified replicated
  inventory-grant verb" — was always the harder half, and this document does not deliver it.

**Disposition: do not reclassify these two rows yet.** Record the park reason as *narrowed* — from "no legal
RNG exists here" to "pending the Leg-3 verification and a parity-policy decision."

### 5.3 `TRAIT_WARDBOUND` / `OF_STABILITY` (CM §2 row 8, §3 row 13) — **available, and probably declined**

We ship an apply-then-cleanse redesign with a recorded delta ("the status is applied and then removed rather
than never applied — any on-apply side effect fires once and the application is briefly visible"). A
state-hash gate on the `ApplyStatus` prefix could deliver *true* prevention and retire that delta.

**Recommend declining.** It trades a cosmetic, documented, fully-symmetric delta for a suppression hook and a
dependency on the weakened invariant 6. The current design is strictly safer and the flaw is a brief visual
artifact. Worth revisiting only if playtesting shows a real on-apply side effect that matters.

### 5.4 Not a general RNG replacement

`ProcChance` stays the default for anything with a live stream. `STATE_HASH_CHANCE` is for hook points the
stream cannot legally reach. A pack that uses it where `ProcChance` would work is worse off — see §6.

---

## 6. The caveat that must ship in the author-facing docs

**A state hash is deterministic, not random, and it is correlated across re-evaluation with identical
inputs.** The same tuple always yields the same verdict. Consequences an author must design around:

1. **No independent retries.** Two evaluations in the same state give the same answer — never two
   independent 20% chances. A tuple must include something that varies per evaluation, or the gate is a
   constant for as long as the state holds. EOR includes `finalDamage` for exactly this reason.
2. **Correlated within a batch.** An AoE dealing identical damage to two entities at the same HP in the same
   round differs only by `SELF_GUID`; include it (as EOR does) or every target in the batch shares one
   verdict.
3. **Streakiness, not fairness.** Over the input space the rate is `Percent`. Over *one player's actual
   experience* it is not: a player parked in a repeated state gets a repeated verdict. This can read as "the
   trait is broken."
4. **Predictable in principle.** The function is public and the inputs are visible. Irrelevant for co-op PvE;
   worth stating so nobody later mistakes it for a fair-randomness source.
5. **Not uniform.** FNV-1a `% 100` has a slight modulo bias and no avalanche guarantee on short, highly
   structured inputs. At `Percent` granularity of whole percents this is immaterial — but it means the
   primitive must never be described as "a 20% roll." It is "a gate that passes for about 20% of states."

`Salt` exists to address (1)–(2) only partially — it decorrelates *different conditions*, never repeated
evaluations of the same one.

---

## 7. Milestones

| ID | Deliverable | Verification |
|---|---|---|
| **M-SH1** | Pure `StateHashChance` static in `ClassForge.Recipes` (FNV-1a, canonical string builder) + `ConditionKind.STATE_HASH_CHANCE` + parser + validator rules (§2 field bounds, salt uniqueness, trigger-scoping, `Percent` 1–99) | `ClassForge.Recipes` suite: vector tests against EOR62 L26754's exact algorithm; distribution test over ≥100k synthetic tuples asserting the observed rate is within tolerance of `Percent`; validator-rejection tests per rule |
| **M-SH2** | Engine-side input resolvers behind the §3.3 safe-wrapper, with the once-per-pair Warn log | Recipes-suite tests with a faked resolver surface; no game dependency |
| **M-SH3** | `DAMAGE_TAKEN_MULT` effect + `CalculateFinalDamage` postfix; `TRAIT_SHIELDBEARER` recipe replacing the `DEF +1` substitute; CM §2 row 15 → PORT | Plugin builds vs `tools/bin/refs`; PackCheck; two-process determinism check that identical inputs yield identical verdicts across processes (guards against any hash-code/culture leakage) |
| **M-SH4** | SPEC-DELTA §5.2 amendments (§4) + §7.4 park retired + author-facing caveat doc (§6) | Doc review |

**Explicitly out of scope:** ARCANE_MEMORY / `OF_SPELLKEEPING` (blocked on §5.2 Leg 3 + a parity-policy
decision) and WARDBOUND (§5.3, recommended declined).

---

## 8. Open questions

- **OQ-SH1 — invariant 6's amendment is the real decision here.** M-SH1–M-SH3 need only the invariant-1 and
  invariant-2 amendments; the SHIELDBEARER postfix never suppresses. **Recommend shipping M-SH1–M-SH4 with
  invariant 6 left *unchanged*,** and treating its amendment as a separate decision at the point something
  actually needs suppression. Weakening a safety invariant speculatively, for a consumer that is still
  blocked on two other things, is how invariants stop being load-bearing.
- **OQ-SH2 — [UNVERIFIED]** Does `InventoryHelper.Consume` execute on every peer? (§5.2 Leg 3.) Needs a PSN
  pass. Gates any ARCANE_MEMORY work.
- **OQ-SH3** — Should `COMBAT_SEED` be *mandatory* in every in-combat tuple rather than recommended? It
  guarantees verdicts do not repeat across combats in identical states. Leaning yes; costs authors nothing.
- **OQ-SH4** — Does the vendor desync detector hash anything this touches? It hashes all of `GameRunData` and
  compares per-action draw counts (Wave-2 landmine). A postfix that changes a damage *result* changes
  replicated HP — but identically on every peer, so the hash agrees. **Believed safe; confirm in the same
  smoke pass that covers M-SH3** rather than assumed.
- **OQ-SH5** — `Percent` is currently a whole integer. Anything in the corpus needing sub-percent
  granularity? None found (EOR's gates are 10/15/20/25/30). Leaving it integral.
