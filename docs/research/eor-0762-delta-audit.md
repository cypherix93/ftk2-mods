# EOR v0.7.0.60 → v0.7.0.62 delta audit

**Date:** 2026-08-08 · **Upstream release:** `Release 29 0.7.0.62 2026-08-04T03-16Z kgQJdw2QN`
(package `sirpepperpot.enhanced-overhaul-revamped`, `package_format_version: 1`, `package_kind: full`)

**Purpose:** establish exactly what changed in Enhanced Overhaul Revamped between the version this repo
ported (**0.7.0.60**) and the newly published **0.7.0.62**, and disposition each change against our re-host
engine. Companion to `eor-0760-content-audit.md` (**AUD**) and `eor-rehost-coverage-matrix.md` (**CM**).

**Method.** Package-tree `diff -rq` between the two extracted releases in `D:\temp\mods\`; JSON key-level
diffs of localization and the shipped game configs; `ilspycmd -p` decompile of the new
`EnhancedOverhaulRemix.dll` into `tools/out/decompile/EOR-0.7.0.62/` (gitignored, same convention as the
`EOR-0.7.0.60/` snapshot), then a name-level member diff plus targeted reads. **All line citations below are
into `tools/out/decompile/EOR-0.7.0.62/FTK2.BanditKingPlayable/Plugin.cs` unless marked 0.60.**

**Nothing in this document has been run against a live game.** It is a static-analysis pass only.

---

## 0. Executive summary

The package delta is remarkably narrow — **8 changed paths, of which one is the DLL**:

| Path | Change |
|---|---|
| `BepInEx/plugins/EnhancedOverhaulRemix.dll` | 743,424 → 759,296 bytes (+2.1%); decompiled source 29,415 → 31,919 lines (+8.5%) |
| `…/Localization/en.json` | 1440 → 1592 keys (+152 added, 0 removed, 18 changed) |
| `…/Localization/zh-Hans.json` | identical key delta, 0 value changes (English placeholders) |
| `…/JSON~/Characters.json` | 2126 entries unchanged; **30 of 31 `EOR_*` classes changed** — starting `Things` only |
| `…/JSON~/ServerNames.json` | **removed** from the shipped game-config overlay |
| `EOR_PACKAGE.json` | **new** — machine-readable package manifest |
| `README_INSTALL.txt` | **new** (replaces `changelog.txt`, removed) |

**`CustomItems/`, `ItemIcons/`, `ClassIcons/`, `ClassPortraits/`, `TraitIcons/`, `ServiceIcons/`,
`FTK2_EnhancedPets/*`, and `FTK2_EnhancedMercenaries/*` are byte-identical.** No new items, pets, mercs, or
art shipped. Our `ARM_EOR_ITEMS`, `ARM_EOR_STARTERS`, `SMN_PACK_EOR_MERCS` and `SMN_PACK_EOR_PETS` packs are
**unaffected**.

The DLL change is **purely additive**: 22 new methods, **zero removed**, zero renamed. Three themes:

1. **A selectable-trait rebalance from flat stats to percentage-of-computed-stat** — implemented in a
   `CharacterHelper.GetStat` postfix. This is precisely the `CONDITIONAL_STAT_MODIFIER` primitive our CM
   parked (SPEC-DELTA §7.10). **Our biggest divergence.**
2. **A determinism overhaul** — EOR replaced shared-`GameRandom` draws for class skills and encounter
   modifiers with hash-derived private streams, and made SHIELDBEARER's chance gate a pure state hash with
   no RNG at all. **This independently validates our Gate-A design and unparks two of our PARK rows.**
3. **Crash-recovery, save-repair, and new result-inspection helpers** — smaller, mostly not our problem.

---

## 1. Theme A — selectable traits move from flat data to percentage code

### 1.1 What changed

In 0.7.0.60 the selectable loadout traits carried their bonuses as `Equippable.Stats` on the `TRAIT_*`
`ThingConfig` — pure data, read by the vanilla `GetStat` + `GetEquippedThingsNonAlloc(…, pIncludeTraits:true)`
path (TM §3a). That is exactly why CM §2 rows 1–5 disposition them **PORT** with "zero primitives, zero
patches".

0.7.0.62 **strips those flat stats at load** and re-adds them as percentages at read time:

- `RemoveLegacyFlatSelectableTraitStats(string traitKey, ThingConfig traitConfig)` (L20178, called from
  L19953) deletes the named stat keys from the trait config's `Equippable.Stats` — e.g. `LCK` for
  `TRAIT_TREASURE_SENSE`, `INT` for `TRAIT_SCHOLARS_HABIT`.
- `ApplySelectableTraitConditionalStats(Entity, CharacterComponent, string statKey, ref int result)`
  (L24516) then adds a percentage in a `GetStat` postfix, via
  `AddPercentageStatBonus(ref int result, float multiplier)` (L24571):
  `result += Math.Max(1, Mathf.CeilToInt(result * multiplier))`.

### 1.2 The new table (verbatim from L24516–L24570)

| Trait | Stat | 0.7.0.62 behavior | 0.7.0.60 (our shipped port) |
|---|---|---|---|
| `TRAIT_LIGHT_FOOTED` | `EVD` | `+20%` | `EVD +10` flat |
| `TRAIT_TREASURE_SENSE` | `LCK` | `+15%` | `LCK` flat |
| `TRAIT_TOUGHENED` | `HP` / `DEF` | `+20%` / `+15%` | `HP +10, DEF +2` flat |
| `TRAIT_STEADY_AIM` | `AWR` | `+12%` | flat |
| `TRAIT_STREETWISE` | `TAL` | `+15%` | flat |
| `TRAIT_WARDBOUND` | `RES` | `+20%` | flat |
| `TRAIT_SCHOLARS_HABIT` | `INT` | `+12%` | flat |
| `TRAIT_KNIFE_EDGE` | `HP` | **`−5%`** (`Math.Max(1, result − ceil(result×0.05))`) | `CRT +10, HP −3` flat |
| `TRAIT_PACK_TACTICS` | `PHY` | `+10%`, still gated on `PartyHasPetOrMercenary()` (L24576) | **we ship unconditional `PHY +1`** |
| `TRAIT_ARCANE_FOCUS` | `MAG` | **mechanic replaced** — `ArcaneFocusStacks × 5%`, stacks earned in combat | **we ship unconditional `MAG +1`** |
| *(new)* | `THRN` | `value.TemporaryThorns` added flat; decays ×0.25/turn (L6768) | — no equivalent |

`TRAIT_ARCANE_FOCUS` is the only outright **mechanic replacement**: 0.60's "conditional `MAG +2` while Focus
≥ 2" became a stacking in-combat buff keyed on `ClassSkillRuntimeState.ArcaneFocusStacks`, decaying through
the new turn-start hook. The loc string confirms it: *"Successful magical attacks grant Arcane Focus: +5%
Magic Damage…"*.

### 1.3 Impact on us

CM §2 rows 1–5, 11, 17 and CM §3's stat-only affix pre-mints all assume **flat** values. They are now
behaviorally divergent from upstream. Closing the gap requires the parked `CONDITIONAL_STAT_MODIFIER`
primitive — a `CharacterHelper.GetStat` postfix, which SPEC-DELTA §7.10 explicitly refused on cost/benefit
grounds ("extremely hot path with 8 overloads, in and out of combat, no authority/RNG context… fails for a
flat `+2`").

**That refusal's premise has weakened.** The primitive is no longer buying a flat `+2` — it now buys the
correct behavior of **9 traits plus a new stat channel**. EOR is running this postfix in production, which is
evidence (not proof) that the hot-path cost is tolerable. This is a genuine design fork and is called out as
**OQ-A** in §6 below.

Note the MP posture is *safe* either way: percentage stat reads are deterministic and derive from replicated
state, so this is a balance/fidelity question, not a desync question.

---

## 2. Theme B — EOR adopted hash-derived determinism (validates our design, unparks two rows)

This is the most consequential finding for the re-host engine, and it runs **in our favor**.

### 2.1 `CreateDeterministicClassSkillRandom` (L6541, called L6300)

EOR now builds a 17-field canonical context string and hashes it into a **private** `GameRandom` instead of
drawing from the shared combat stream:

```
"EOR_CLASS_SKILL_ACTION" | MapGenSeed | GameRun.ConfigName | ActiveMapID | EncounterGUID
| CombatState.TotalRounds | origin.Guid | target.Guid | thing.ConfigName | ability.AbilityName
| decision.FocusUsed | (int)roll.Status | results.Count | joined result-enum ids
| origin HP | origin Focus | target HP
→ new GameRandom(Math.Max(1, ComputeDeterministicSeed(value)), false)
```

`SafeCombatSeedValue(Func<int>)` (L6587) wraps each stat read in try/catch → 0.

**This is our Gate-A design.** Our loot-grant verb derives `GrantKey`/`grantSeed` from
`CombatSeed + sorted enemy Guids + ListDigest + sorted owner guids` for exactly the same reason, and our
encounter modifiers hoist to a constant-2-draw pattern to stay countable by the vendor desync detector.
Independent convergence by upstream is strong corroboration that the approach is correct.

### 2.2 `CreateEncounterModifierRandom(GameRunData, string context)` (L24713)

Same pattern, context-keyed: `"selection"` for the modifier roll (L24680), and
`"effect|" + enemy.Guid + "|" + _activeEncounterModifier.StatusKey` for per-enemy application (L24703).

### 2.3 `ShouldShieldbearerMitigate(Entity target, int finalDamage)` (L26754) — the unlock

```csharp
string text = $"{target?.Guid}|{TotalRounds}|{GetHealth(target)}|{finalDamage}|SHIELDBEARER";
uint num2 = 2166136261u;                       // FNV-1a
for (…) { num2 ^= text[i]; num2 *= 16777619; }
return num2 % 100 < 20;                        // 20% chance, zero RNG draws
```

Called from the `CalculateFinalDamage` postfix at L26733.

**This directly refutes our stated PARK reason for SHIELDBEARER.** CM §2 row 15 parks
`DAMAGE_TAKEN_MULT` because "`CalculateFinalDamage` has **no `GameRandom` parameter**, so a chance gate there
must take a static draw at a point only the host may execute — charter rule 2's named failure."

EOR's answer is to **not draw at all**: hash the replicated state (`guid|round|hp|damage|salt`) and compare
mod 100. Every peer computes the same value from the same replicated inputs, so it is MP-safe by
construction, takes zero shared-stream draws, and is invisible to the vendor desync detector's draw-count
comparison. The same trick generalizes to any chance gate on an RNG-less hot path.

**Rows this unparks or cheapens:**

| CM row | Was parked on | Status after this finding |
|---|---|---|
| §2 row 15 `TRAIT_SHIELDBEARER` | `DAMAGE_TAKEN_MULT` needs a draw in an RNG-less method | **Unparked** — a `STATE_HASH_CHANCE` gate needs no draw. Also retires our "≈2× stronger `DEF +1`" balance debt |
| §2 row 14 `TRAIT_ARCANE_MEMORY` | `SUPPRESS_CONSUME`: scroll use is out-of-combat, `CombatState.Random` is null | **Likely unparked** — the objection was "no legal RNG out of combat"; a state hash needs none. Still needs the replicated-inventory-grant half verified |
| §3 row 7 `OF_SPELLKEEPING` | same as ARCANE_MEMORY | Follows ARCANE_MEMORY |
| §2 row 8 / §3 row 13 `WARDBOUND` / `OF_STABILITY` | *not* parked, but we redesigned to apply-then-cleanse to avoid per-client suppression | A state-hash gate may allow **true** prevention, retiring our recorded "briefly visible" delta |

This deserves its own primitive proposal — **`STATE_HASH_CHANCE`**, a deterministic chance condition with an
explicit salt and a declared input tuple — rather than ad-hoc reuse. Called out as **OQ-B** in §6.

**Caveat worth stating plainly:** a state hash is deterministic but *not* uniformly random, and it is
**correlated across re-evaluations with the same inputs** — the same `(guid, round, hp, damage)` always
yields the same verdict. EOR's own inclusion of `finalDamage` in the tuple mitigates this. Any primitive we
build must document that property rather than present it as an RNG substitute.

### 2.4 Did 0.62 fix EOR's multiplayer desync? **No — partially.**

Each of AUD §3's eight decompile-verified desync mechanisms, re-checked against 0.62:

| AUD §3 | Mechanism | 0.62 status |
|---|---|---|
| 2 (partial) | Shared-stream draws for **class-skill procs** | **FIXED** — `CreateDeterministicClassSkillRandom` (L6541) |
| 2 (partial) | Shared-stream draws for **encounter-modifier picks** | **FIXED** — `CreateEncounterModifierRandom` (L24713) |
| 7 (partial) | SHIELDBEARER's per-client RNG gate | **FIXED** — `ShouldShieldbearerMitigate` (L26754), zero draws, all peers agree |
| **1** | **Four stubbed MP guards** | **UNCHANGED** — `ShouldDisableCampaignMutatorsForMultiplayer` (L15891), `ShouldDisablePreparedFocusForMultiplayer` (L15896), `ShouldDisableVolatileCombatMutationsForMultiplayer` (L15901), `ShouldDisableRuntimeAffixesForMultiplayer` (L22283) — **all still `return false`**, verbatim |
| **2** (rest) | Shared-stream draws in loot, nemesis, map-gen, market | **UNCHANGED** — `.Next(` call sites 19 → 18 |
| **3** | Client-local mutation of authoritative state | **UNCHANGED** |
| **4** | `_handleNetworkAction` prefix + armed `EOR_SYNC_*` receive path | **UNCHANGED** (L9014) |
| **5** | Reflection-based MP detection, exceptions swallowed → "not multiplayer" | **UNCHANGED** — `IsOnlineMultiplayerSession` (L22245) still try/catch |
| **6** | Per-process static state not cleared on run create | **UNCHANGED** |
| **7** (rest) | **Per-client suppression of authoritative state** | **UNCHANGED** — `InventoryHelper_Consume_Prefix` (L26577) still `return false`s a host-applied inventory change on `RollSelectableTraitChance(null, 0.30m)`, the `random: null` seeded-fallback path BM flagged as the highest-hazard row in the corpus. Only the percentage moved (20% → 30%) |
| **8** | Asymmetric host-only sanitizers | **UNCHANGED**, and **extended** — `SanitizeEquippedThingReferencesForPartyStats` is a new repair for damage that runtime affix minting causes |

**Two structural notes.**

1. `RollSelectableTraitChance` (L26555) opens by returning `false` if
   `ShouldDisableVolatileCombatMutationsForMultiplayer()` — which is hardcoded `return false`. **The MP guard
   on the single most hazardous path in the mod is dead code.** It is written as though a real gate were
   intended and never wired.
2. The fix that landed is the *tractable* class — swapping a shared-stream draw for a hash-derived private
   one is a local, mechanical change. What remains untouched is the class that needs an authority model:
   deciding *who* may mutate state and *how* that decision reaches other peers. EOR has no host-authoritative
   design to extend, which is precisely the gap this re-host exists to fill.

**Consequence for us: none of our charter rules relax.** Charter rule 2 (no static draw at a point only the
host may execute) and rule 3 (no per-client suppression of authoritative state) are still violated by
upstream 0.62. The re-host's value proposition is unchanged; what changed is that EOR independently
validated our RNG-derivation *technique* (§2.1–2.3) while leaving the authority model unbuilt.

---

## 3. Theme C — new triggers, recovery, and repair (smaller impact)

| Member | Line | What it is | Our disposition |
|---|---|---|---|
| `ClassSkillEngineOnTurnStart` | L6745 | Turn-start expiry pass: clears Showmanship's taunt/armor/resist trio, Pack attack-up, decays `TemporaryThorns` ×0.25 | Our `Budget{ONCE_PER_ROUND}` + status `Duration` already model most of this declaratively. **Thorns decay has no equivalent.** |
| `HandleSelectableTraitAbilityResults` | — | Central dispatch for trait procs off an ability's result list | Our `ON_*` trigger set already covers this shape |
| `WasStatusTypeAddedTo(Entity, eStatusEffectTypes, results)` | L6955 | Result-list inspection by status type | Close to our `STATUS_TYPE`(C10); no new work |
| `WasBlockedBy(Entity defender, results)` | L6983 | Detects a block in the result list | **New trigger candidate** — we have no `ON_BLOCK`. Same class of evidence as the `ON_DODGE` finding (CM impl-update 1) |
| `WasMagicalDamageAppliedTo(Entity, results)` | L7006 | Magic-damage detection; feeds `ArcaneFocusStacks` | **New condition candidate** — `DAMAGE_TYPE{MAGICAL}` |
| `RemoveStatusSafe` | — | Exception-swallowing status removal | Housekeeping |
| `SavePendingPromptSnapshot` / `TryRecoverInterruptedPromptSnapshot` / `CompletePendingPromptSnapshot` / `CancelPendingPromptSnapshot` / `DeletePendingPromptSnapshot` / `GetPendingPromptSnapshotPath` | — | On-disk crash recovery for an interrupted prompt (blessing/mutator choice) | **Not our problem** — our Blessings v0 uses a hash-derived deterministic offer with no in-run prompt (spec §; Gate E). Becomes relevant only if Blessings v1's accept/decline dialog ships |
| `SanitizeEquippedThingReferencesForPartyStats` | — | Repairs dangling equipped-`Thing` refs before a party-stat read | Symptom of EOR's runtime affix minting (AUD §2.5) — **our deterministic pre-mint design avoids the failure class entirely.** No action |
| `IsUsableMainHandWeaponConfig` | L11627 | Main-hand weapon filter | Watch; likely tied to the unshipped katana line (§4) |
| `ShouldRefreshCanonicalEnglishKey` | — | Localization key refresh | No action |

---

## 4. Content that shipped as localization only

**152 new loc keys landed with no backing content in the package:**

- **~26 `EORR_*_KATANA` ids** (`EORR_SAKURA_EDGE_KATANA`, `EORR_ONI_BANE_KATANA`, …), each with a generic
  `_DESCRIPTION` of *"Custom gear introduced by Enhanced Overhaul Revamped."* — `CustomItems/Things.json`
  is byte-identical and the DLL contains **zero** `KATANA` string literals (grep, both versions). These are
  **pre-staged strings for unshipped content.**
- **2 new campaign mutators** — `EOR_CAMPAIGN_MUTATOR_DUNGEONFREEROAM`, `EOR_CAMPAIGN_MUTATOR_EXPLORERSPACE`.
  Campaign mutators are AUD §6 row 9 **dropped** for us and stay dropped.
- **An `EOR_CLASS_SKILL_*` loc family** — per-class skill names and descriptions
  (`EOR_CLASS_SKILL_ARCANIST` = "Arcane Overflow", etc.). Useful as a **reference for our own
  `UI_TOOLTIP_*` strings**, but note CM §5 note 4: for RANCHER, TRICKSHOT, SCOUT and WARDEN we deliberately
  ship *code* behavior, so their copy must not be lifted verbatim.
- `BOOMERANG_ATTACKDOWN_ATTACK` = "Weakening Toss" — one new ability string.

**18 changed descriptions**, all trait/class text catching up to §1's rebalance, plus `EOR_SENTINEL` and
`EOR_SENTINEL_MASTERY` ("…turns Armor and successful Taunts into…"). Treat loc as *suggestive only* — CM §5
note 4 already records that EOR's 0.60 strings disagreed with its own code.

---

## 5. Shipped game-config overlay (`JSON~/Characters.json`)

30 of 31 `EOR_*` classes changed (all but `EOR_BARD`). **Every diff is confined to the `Things` map** —
starting inventory. **No `Stats`, `Abilities`, `Passives`, or `Equippable` field changed on any class.**

Representative:

| Class | Removed from starting kit | Added |
|---|---|---|
| `EOR_SENTINEL` | `BLUNT_BLACKSMITH_BASIC_00`, `TOOL_VEHICLE_REPAIR_01` | `BLADE_MILITIA_LIGHT_00` |
| `EOR_ARCANIST` | `SCROLL_TELEPORT_01` | `SCROLL_IDENTIFY_01`, `HERB_GOLDENROOT_01` |

Most-churned entries across all 30: `HERB_GOLDENROOT_01` (9), `HERB_GODSBEARD_01` (7),
`BLUNT_BLACKSMITH_BASIC_00` (6), `TOOL_VEHICLE_REPAIR_01` (6).

**Impact:** `CF_PACK_EOR_CLASSES`'s starting loadouts are stale. This is a **mechanical converter re-run**
(`tools/eor_import.py` against the 0.62 tree), not a design change — the lowest-risk item in this document.

Separately, `ServerNames.json` was **dropped** from EOR's game-config overlay. EOR still ships its own
`FTK2_EnhancedMercenaries/ServerNames.json` and `FTK2_EnhancedPets/ServerNames.json` (both unchanged), so
this reads as removing a redundant vanilla-config override. No action for us.

---

## 6. Open questions for the owner

> **Resolved 2026-08-08 (owner decision, same day).** **OQ-A → adopt upstream's rebalance**; spec written:
> `docs/superpowers/plans/2026-08-08-conditional-stat-modifier-spec.md`. **OQ-B → adopt**; spec written:
> `docs/superpowers/plans/2026-08-08-state-hash-chance-spec.md`. Both are design drafts pending review —
> **no code written**. §7's item 1 (starting loadouts) shipped. OQ-C and OQ-D remain open as stated below.

- **OQ-A — do we follow the flat → percentage trait rebalance (§1)?** Adopting it means building the parked
  `CONDITIONAL_STAT_MODIFIER` primitive (a `CharacterHelper.GetStat` postfix across 8 overloads, SPEC-DELTA
  §7.10) and re-authoring 9 trait rows plus a `THRN` channel. Declining means our traits stay flat and
  permanently diverge from upstream — defensible, but it must be **recorded** in CM as a deliberate
  deviation rather than left as an unnoticed staleness. The refusal's original premise ("not worth it for a
  flat `+2`") no longer describes what the primitive buys.
- **OQ-B — do we adopt a `STATE_HASH_CHANCE` primitive (§2.3)?** Cheap, MP-safe by construction, needs no
  hook we don't already have, and unparks SHIELDBEARER outright plus likely ARCANE_MEMORY / `OF_SPELLKEEPING`.
  Needs an explicit design note on its non-independence across re-evaluation with identical inputs.
- **OQ-C — reference-assembly refresh.** `tools/bin/refs/` was snapshotted against the game build of
  2026-07-31 (build-template-notes §2). This audit did **not** verify whether the game itself updated since;
  the standing landmine from both Wave handoffs is *refresh refs before trusting any Harmony signature.*
  **`[UNVERIFIED]`** — check before any code lands from this audit.
- **OQ-D — `ON_BLOCK` / `DAMAGE_TYPE{MAGICAL}` (§3).** Two new trigger/condition candidates with upstream
  proof-of-existence. Same disposition path as the `ON_DODGE` finding (CM impl-update 1): candidates for a
  vocabulary v1.3, each needing its own hook-point verification.

---

## 7. Recommended sequencing

Ordered by risk-adjusted value; none of it is started.

1. **Converter re-run for starting loadouts (§5).** Point `tools/eor_import.py` at the 0.62 tree, regenerate
   `CF_PACK_EOR_CLASSES`, diff, re-run `ClassForge.PackCheck`. Mechanical, self-verifying, zero design risk.
2. **Record the divergence in CM regardless of OQ-A's answer (§1).** A known deviation is fine; an
   undocumented one is the thing our own charter forbids.
3. **Spec `STATE_HASH_CHANCE` (OQ-B).** Highest capability-per-effort item in this document — it converts a
   PARK to a PORT with no new hook.
4. **Decide OQ-A.** The largest piece of work here; deserves its own spec pass, not an inline change.
5. **Fold `ON_BLOCK` / `DAMAGE_TYPE{MAGICAL}` into the v1.3 candidate list** alongside `ON_DODGE`.

**Explicitly not recommended:** porting the prompt-snapshot recovery layer or
`SanitizeEquippedThingReferencesForPartyStats` — both are remedies for failure classes our architecture
does not have.
