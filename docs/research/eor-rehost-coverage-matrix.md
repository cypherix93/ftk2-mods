# EOR re-host coverage matrix — every mechanic dispositioned

**Date:** 2026-07-25 · **Charter deliverable D8** ·
**Vocabulary of record:** `FTK2.ClassForge/SPEC-DELTA-v1.1.md` (recipe vocabulary **v1.1**).

**Purpose:** disposition **every** Enhanced Overhaul Revamped v0.7.0.60 mechanic slated for re-hosting as
**PORT** / **PORT-MODIFIED** / **PARK**, against the adopted v1.1 primitive set, with a citation into
`docs/research/eor-behavior-matrix.md` (**BM**) for each. Supporting citations:
`docs/research/eor-trait-mechanism.md` (**TM**), `docs/research/eor-0760-content-audit.md` (**AUD**),
`docs/research/game-patch-surface-notes.md` (**PSN**), `docs/research/enum-ground-truth.md` (**EGT**).

**Verdict definitions**
- **PORT** — expressible with vanilla data and/or adopted v1.1 primitives with **no behavioral change**.
- **PORT-MODIFIED** — ships, but with a stated behavioral difference from EOR. Every row names the change.
- **PARK** — does not ship in this wave. Every row names the reason and the primitive that would unlock it.

---

## 0. Summary counts

| Family | Rows | PORT | PORT-MODIFIED | PARK |
|---|---:|---:|---:|---:|
| §1 Class signature skills | 31 | 19 | 9 | 3 |
| §2 Selectable loadout traits | 20 | 14 | 5 | 1 |
| §3 Affixes (20 ids + 2 system rows) | 22 | 18 | 3 | 1 |
| §4 Mastery meta-progression | 1 | 0 | 0 | 1 |
| **Total** | **74** | **51** | **17** | **6** |

**Recount note (2026-08-06):** the loot-grant sync verb's adoption (commits `fc17c53`/`f7b14b9`/`22131ce` —
see "Implementation updates (2026-08-06)" at the end of this document) flips §2 rows 6/9/12
(`TRAIT_TREASURE_SENSE`/`TRAIT_SCHOLARS_HABIT`/`TRAIT_SCAVENGER`, PORT-MODIFIED/PORT-MODIFIED/PARK →
PORT/PORT/PORT) and §3 row 16 (`OF_SCAVENGING`, PORT-MODIFIED → PORT); §3 row 22 (affix drop-rolling) stays
**PARK**, re-gated on M-LG4 instead of the now-resolved "loot hook absent from PSN" reason. Net: PORT
47→51 (+4), PORT-MODIFIED 20→17 (−3), PARK 7→6 (−1); row total unchanged at 74.

**Row-count reconciliation (read this before auditing the totals).** The charter and AUD §2.5 both say
"22 affixes". The decompile says **20**: `AffixDefinitions = new AffixDefinition[20]` (EOR `Plugin.cs`
L5114–5135), and an exhaustive grep of the assembly for `"OF_*"` string literals returns exactly those 20 ids
and no others. The "22" figure appears to be a transcription slip in AUD §2.5's heading. This document
disposes of **all 20 real affix ids plus the 2 affix *system* mechanics** (runtime variant minting; drop-time
affix rolling) that AUD §2.5 describes and that must also be dispositioned — giving 22 affix-section rows with
zero silent omissions in either direction.

**Parked-primitive index** (from SPEC-DELTA-v1.1 §7) — what each PARK row is waiting on:

| Parked primitive | Blocks |
|---|---|
| ~~`GOLD_GRANT` / `ITEM_TAG_GRANT` (§7.1)~~ — **ADOPTED v1.2, 2026-08-06** | ~~SCAVENGER; loot halves of TREASURE_SENSE, SCHOLARS_HABIT, OF_SCAVENGING~~ — all shipped, see §2 rows 6/9/12. Affix drop-rolling now blocks on `REPLACE_ITEM` + the pre-mint registry instead — **M-LG4** (loot-grant-verb-spec.md §3.2/§10), see §3 row 22 |
| `SUPPRESS_CONSUME` (§7.2) | ARCANE_MEMORY; code half of OF_SPELLKEEPING |
| `DAMAGE_TAKEN_MULT` (§7.4) | code half of SHIELDBEARER |
| `STEAL_STATUS` (§7.5) | THIEF |
| `CLEANSE_RANDOM_STATUS` (§7.6) | PALADIN |
| `CROSS_ENTITY_COORDINATION` (§7.7) | BEASTMASTER's literal form (mechanic still ports, modified) |
| `ON_DODGE` (§7.9) | DUELIST |
| `CONDITIONAL_STAT_MODIFIER` (§7.10) | code halves of PACK_TACTICS, ARCANE_FOCUS |

---

## 1. Class signature skills (31)

All rows cite **BM Table 1**. "Recipe sketch" names the adopted v1.1 tokens; `T#`/`C#`/`E#` refer to
SPEC-DELTA-v1.1 §2.2 / §3.2 / §4.2.

| # | Class | BM row | Verdict | Recipe sketch (v1.1) | Modification / park reason |
|---|---|---|---|---|---|
| 1 | **ARCANIST** | BM T1 ARCANIST (L6192–6197, `PickArcaneOverflowStatus` L6760) | **PORT-MODIFIED** | `ON_ABILITY_USED` + `ROLL_TIER(EQ PERFECT)`(C1) + `HOSTILE_ACTION`(C2) + `ABILITY_STAT(INT)`(C3) + `ProcChance 20` → `ADD_STATUS{TRIGGER_TARGET, StatusOneOf:[FIRE_00,ICE_00,SHOCK_00], Duration:1}` | EOR first tries a **substring match** on `thing.ConfigName+"\|"+abilityName` for "FIRE"/"ICE"/"SHOCK\|LIGHTNING" and only falls back to a random pick. v1.1 always picks randomly. Authors wanting elemental affinity write 3 recipes gated on `ABILITY_TAG`. Draw count becomes a constant 2 instead of EOR's 1-or-2 — **a determinism improvement** |
| 2 | **ASSASSIN** | BM T1 ASSASSIN (L6093–6098) | **PORT** | `ON_ABILITY_DECLARED`(T2) + `ROLL_TIER(PERFECT)` + `HOSTILE_ACTION` + `HP_THRESHOLD{Of:TRIGGER_TARGET, LTE 15%}` → `ROLL_STAT_BONUS{ATK, Percent:100}`(E1) [Conditions: `CHARACTER_TYPE{Of:TRIGGER_TARGET, BOSS, Negate}`] + `ROLL_STAT_BONUS{ATK, Percent:25}` [Conditions: `CHARACTER_TYPE{BOSS}`] | — (boss split via per-effect `Conditions`; `IsBoss` → `CHARACTER_TYPE`, PSN §6 L1229 + EGT §11) |
| 3 | **BARD** | BM T1 BARD (`HandleBardCrescendo` L6492–6515) | **PORT** | Recipe A: `ON_ABILITY_USED` + `ABILITY_STAT(TAL)` + `ROLL_TIER(EQ CRIT_FAIL)` → `COUNTER_SET{crescendo, 0}`(E4). Recipe B: same trigger + `ROLL_TIER(GTE SUCCESS)` → `COUNTER_ADD{crescendo, 1, Max:3}`(E3) + 3 × `ADD_STATUS{ALLY_ALL, STATUS_ATTACKUP_0n, Duration:2}` each gated on `COUNTER{crescendo, EQ, n+1}` | — |
| 4 | **BEASTMASTER** | BM T1 BEASTMASTER (L6201–6207 + `HandlePackTactics` L6468–6490) | **PORT-MODIFIED** | Master recipe: `ON_ABILITY_USED` + `ROLL_TIER(GTE SUCCESS)` + `HOSTILE_ACTION` → `ADD_STATUS{TRIGGER_TARGET, STATUS_MARKED_00, Duration:1}`. Pet recipe (authored onto pack pet configs): `ON_ABILITY_USED` + `HOSTILE_ACTION` + `HAS_STATUS{Of:TRIGGER_TARGET, STATUS_MARKED_00}` + `Budget ONCE_PER_ROUND` → `ADD_STATUS{TRIGGER_TARGET, STATUS_BLEED_00, FallbackStatus:STATUS_ATTACKDOWN_00, Duration:1}` | The quarry link becomes a **real, visible, dispellable status** instead of EOR's cross-entity `TargetGuid`/`TargetRound` read (`CROSS_ENTITY_COORDINATION` parked, SPEC-DELTA §7.7). Budget moves from the Beastmaster to the pet. **Fixes EOR's undocumented turn-order bug** (BM MP-hazard column: a pet acting before its master never procs). Applies only to pack-authored pets, and inherits `STATUS_MARKED_00`'s vanilla +30 crit chance (PSN §6 L1355) |
| 5 | **BLADEDANCER** | BM T1 BLADEDANCER (L6208–6215) | **PORT** | `ON_ABILITY_USED` + `ROLL_TIER(PERFECT)` + `HOSTILE_ACTION` + `Budget{ONCE_PER_ROUND, PROC}` → `ADD_STATUS{SELF, EVADEUP_00, 1}` + `ADD_STATUS{SELF, HASTE_00, 1}` | — |
| 6 | **CHRONOMANCER** | BM T1 CHRONOMANCER (L6216–6225) | **PORT** | `ON_ABILITY_USED` + `ROLL_TIER(PERFECT)` + `ABILITY_STAT(INT)` + `Budget{ONCE_PER_ROUND}` + `ProcChance 15` → `ADD_STATUS{Target:ALLY_BY_RANK{Stat:SPD, Order:LOWEST}, HASTE_00, 1}` | — (`ALLY_BY_RANK`'s Guid tiebreak reproduces EOR's `.OrderBy(SPD).ThenBy(Guid)` exactly) |
| 7 | **CORSAIR** | BM T1 CORSAIR (L6099–6104 + L6226–6232) | **PORT-MODIFIED** | Pair sharing `Budget.Key`: `ON_ABILITY_DECLARED` + `HOSTILE_ACTION` + `MOVED_THIS_ROUND(true)`(C12) → `ROLL_STAT_BONUS{ATK, 20%}`; `ON_ABILITY_USED` + same conditions + `Budget{ONCE_PER_ROUND}` → `ADD_STATUS{TRIGGER_TARGET, DAZE_00, Fallback:ATTACKDOWN_00, 1}` | EOR *consumes* the buff by resetting `MovedRound = int.MinValue`; v1.1 uses a `ONCE_PER_ROUND` budget instead (there is no "clear the moved flag" effect). Identical in practice unless the Corsair moves twice in one round |
| 8 | **DRUID** | BM T1 DRUID (`HandleClassSkillResponses` L6416–6422) | **PORT** | `ON_DAMAGE_TAKEN`(T4) + `HOSTILE_ACTION` + `Budget{ONCE_PER_ROUND, ConsumeOn:PROC}` + `ProcChance 20` → `ADD_STATUS{TRIGGER_SOURCE, ENTANGLE_ROOTS_00, Fallback:ATTACKDOWN_00, 1}` | — |
| 9 | **DUELIST** | BM T1 DUELIST (L6423–6431 + L6105–6111) | **PARK** | — | Requires `ON_DODGE`. No dodge signal exists in the verified patch surface — PSN's `eAbilityResults` pass enumerated that file for crit tokens and recorded no dodge member. **Unlock:** verbatim `eAbilityResults` list in PSN with a confirmed dodge value (SPEC-DELTA §7.9). Everything else it needs (`COUNTER_*`, `ROLL_STAT_BONUS`, `Budget`) is already adopted |
| 10 | **FORTUNEBORN** | BM T1 FORTUNEBORN (L6233–6243) | **PORT** | `ON_ABILITY_USED` + `ROLL_TIER(EQ CRIT_FAIL)` + `Budget{ONCE_PER_COMBAT, EVALUATION}` → `STAT_CHANGE{SELF, FOC, +1}` [Conditions: `FOCUS_SPENT{GTE 1}`(C7)] + `ADD_STATUS{SELF, EVADEUP_00, 1}` | — (EOR's "refund exactly 1, not the amount spent" and the *unconditional* evade are both reproduced via per-effect `Conditions`) |
| 11 | **GAMBLER** | BM T1 GAMBLER (L6244–6256) | **PORT-MODIFIED** | Recipe A: `ON_ABILITY_USED` + `FOCUS_SPENT{GTE 2}` + `ProcChance 25` → `STAT_CHANGE{SELF, FOC, FlatValueFrom:"FOCUS_SPENT"}`. Recipe B: same gate + `ProcChance 10` → `STAT_CHANGE{SELF, FOC, -1}` [Conditions: `FOCUS_CURRENT{GTE 1}`(C8)] | EOR chains the rolls (`else if`), so its loss chance is 0.75 × 0.10 = **7.5%**; v1.1 rolls both independently, so it is **10%**, and a win can co-occur with a loss (net +FocusUsed−1). Deliberate: two fixed independent draws keep the shared-stream draw count constant regardless of branch, which EOR's 1-or-2-draw chain did not |
| 12 | **GLADIATOR** | BM T1 GLADIATOR (L6257–6265) | **PORT-MODIFIED** | Pair sharing `Budget.Key: CF_GLADIATOR_SHOWMANSHIP`, `Budget{ONCE_PER_ROUND}`: `ON_CRIT` → 3 self statuses; `ON_KILL` → same 3 | EOR's kill branch fires on **any** `DIED` result in the results list, not only kills the Gladiator caused. v1.1's `ON_KILL` is **origin-attributed** (`ApplyStatChange` prefix/postfix HP-before/after on `pOriginEntity`'s own change, PSN §2 L626). Strictly a fidelity fix; slightly fewer procs in AoE-death rounds |
| 13 | **HEXBLADE** | BM T1 HEXBLADE (L6266–6272) | **PORT** | `ON_ABILITY_USED` + `ROLL_TIER(PERFECT)` + `HOSTILE_ACTION` + `ProcChance 25` → `ADD_STATUS{TRIGGER_TARGET, StatusOneOf:[SHOCK_00, BLEED_00, ATTACKDOWN_00, RESISTANCEDOWN_00], 1}` | — (2 draws, same as EOR) |
| 14 | **JESTER** | BM T1 JESTER (L6432–6438) | **PORT** | `ON_ENEMY_ABILITY_RESOLVED`(T7) + `ROLL_TIER(EQ CRIT_FAIL)` + `Budget{ONCE_PER_ROUND, PROC}` + `ProcChance 20` → `ADD_STATUS{TRIGGER_SOURCE, ATTACKDOWN_00, 1}` [Conditions: `CHARACTER_TYPE{Of:TRIGGER_SOURCE, BOSS}`] + `ADD_STATUS{TRIGGER_SOURCE, CONFUSE_00, 1}` [same, `Negate`] | — (`ON_CRIT_FAIL_ENEMY` is *subsumed* by `ON_ENEMY_ABILITY_RESOLVED` + `ROLL_TIER`, not a separate primitive) |
| 15 | **KNIGHT** | BM T1 KNIGHT (L6273–6286) | **PORT** | `ON_ABILITY_USED` + `Budget{ONCE_PER_COMBAT, ConsumeOn:EFFECT_APPLIED}` → `ADD_STATUS{ALLY_BY_RANK{Stat:HP_PCT, Order:LOWEST, Where:[HP_THRESHOLD LTE 25%], ExcludeSelf:true}, PROTECT_00 + ARMORUP_00, 1}` | — (`ConsumeOn: EFFECT_APPLIED` exists precisely to reproduce "`OnceCombat` is only set if an ally was found") |
| 16 | **MARSHAL** | BM T1 MARSHAL (L6287–6298) | **PORT-MODIFIED** | Same pair shape as GLADIATOR, effects `ADD_STATUS{ALLY_ALL, HASTE_00 + ATTACKUP_00, 1}` | Same origin-attributed `ON_KILL` note as GLADIATOR |
| 17 | **ORACLE** | BM T1 ORACLE (L6439–6450) | **PORT** | `ON_ENEMY_ABILITY_RESOLVED` + `ROLL_TIER(EQ PERFECT)` + `Budget{ONCE_PER_COMBAT, EVALUATION}` → `ADD_STATUS{TRIGGER_SOURCE, ATTACKDOWN_00, 1}` + `ADD_STATUS{ALLY_ALL, EVADEUP_00, 1}` | — (EOR's "no target/hostility filter" is reproduced by simply not adding `HOSTILE_ACTION`) |
| 18 | **PALADIN** | BM T1 PALADIN (L6299–6304, `TryCleanseOnePartyStatus`) | **PARK** | — | Requires `CLEANSE_RANDOM_STATUS` — a **cross-entity status selector** ("the alphabetically-first harmful status on any ally"), distinct from `ALLY_BY_RANK` which ranks entities, not statuses. `REMOVE_STATUS{ALLY_ALL, "DEBUFF"}` is available but strips every debuff from every ally, far stronger than the original, so it is not offered as a modification. **Unlock:** a `StatusSelector` sub-schema (SPEC-DELTA §7.6) |
| 19 | **PEASANT** | BM T1 PEASANT (L6112–6117 + L6305–6310) | **PORT** | Pair: `ON_ABILITY_DECLARED` + `HOSTILE_ACTION` + `ALL_ALLIES_ACTED(true)`(C13) → `ROLL_STAT_BONUS{ATK, 10%}`; `ON_ABILITY_USED` + same → `ADD_STATUS{SELF, ATTACKUP_00, 1}` | — (`ActedRound` is tracked for every entity in the per-battle turn-state table, SPEC-DELTA §6, mirroring EOR's unconditional `MarkMovementAndAction`) |
| 20 | **PRIEST** | BM T1 PRIEST (L6311–6318) | **PORT** | `ON_HEAL` (owner = healer, `TRIGGER_TARGET` = healed ally) → `ADD_STATUS{TRIGGER_TARGET, ARMORUP_01 + RESISTANCEUP_GROUP_01, 1}` [Conditions: `ROLL_TIER(EQ PERFECT)`] + the `_00` pair [same, `Negate`] | — (the "tiered status choice by roll grade" gap BM flagged is closed by per-effect `Conditions`) |
| 21 | **RANCHER** | BM T1 RANCHER (L6319–6324) | **PORT** | `ON_ABILITY_USED` + `ROLL_TIER(PERFECT)` + `HOSTILE_ACTION` → `ADD_STATUS{TRIGGER_TARGET, ENTANGLE_ROOTS_00, Fallback:ATTACKDOWN_00, 1}` | — (code is authoritative: single ATTACKDOWN fallback, no Daze branch, per BM's text/code mismatch note) |
| 22 | **RANGER** | BM T1 RANGER (L6118–6123 + L6325–6335) | **PORT-MODIFIED** | Mark: `ON_ABILITY_USED` + `HOSTILE_ACTION` + `ROLL_TIER(GTE SUCCESS)` + `LACKS_STATUS{Of:TRIGGER_TARGET, STATUS_MARKED_00}` → `ADD_STATUS{TRIGGER_TARGET, STATUS_MARKED_00, Duration:2}`. Bonus: `ON_ABILITY_DECLARED` + `HAS_STATUS{Of:TRIGGER_TARGET, STATUS_MARKED_00}` → `ROLL_STAT_BONUS{ATK, 10%}` | The quarry is the **status EOR already applied in parallel**, not a private `TargetGuid` (`TARGET_TRACKING` subsumed, SPEC-DELTA §7.8). Consequences: the mark **expires after 2 rounds** and is dispellable, where EOR's GUID lock persisted until the target died; and it can re-target, where EOR's stuck to the first-ever damaged enemy |
| 23 | **RUNEMAGE** | BM T1 RUNEMAGE (L6336–6355) | **PORT-MODIFIED** | Detonate: `ON_ABILITY_USED` + `ROLL_TIER(PERFECT)` + `HOSTILE_ACTION` + `ABILITY_STAT(INT)` + `HAS_STATUS{Of:TRIGGER_TARGET, MARKED_00}` → `ADD_STATUS{ENEMY_ALL, RESISTANCEDOWN_00 + ATTACKDOWN_00, 1}` + `REMOVE_STATUS{TRIGGER_TARGET, MARKED_00}`. Mark: same gate + `LACKS_STATUS{...MARKED_00}` → `ADD_STATUS{TRIGGER_TARGET, MARKED_00, 2}` | Same status-as-mark substitution as RANGER (mark now expires in 2 rounds). The 2-hit combo, the AoE detonation and the mark-clear are otherwise exact |
| 24 | **SCOUT** | BM T1 SCOUT (L6124–6129 + L6356–6361) | **PORT** | First-hit bonus: `ON_ABILITY_DECLARED` + `HOSTILE_ACTION` + `Budget{ONCE_PER_TARGET_PER_COMBAT, EVALUATION}` → `ROLL_STAT_BONUS{ATK, 10%}` (deliberately **no** roll-tier gate, matching code). Evade-down: `ON_ABILITY_USED` + `ROLL_TIER(PERFECT)` + `HOSTILE_ACTION` (**no** first-hit gate, matching code) → `ADD_STATUS{TRIGGER_TARGET, EVADEDOWN_00, 1}` | — (BM flags a flavor-text mismatch here; code is authoritative and is what is ported) |
| 25 | **SENTINEL** | BM T1 SENTINEL (L6451–6457) | **PORT** | `ON_DAMAGE_TAKEN` + `HOSTILE_ACTION` + `ABILITY_RANGED(false)`(C4) + `Budget{ONCE_PER_ROUND, ConsumeOn:PROC}` + `ProcChance 25` → `ADD_STATUS{TRIGGER_SOURCE, DAZE_00, Fallback:ATTACKDOWN_00, 1}` | — (`ConsumeOn: PROC` reproduces EOR's real "each melee hit re-rolls until one procs" behavior, which its flavor text described incorrectly) |
| 26 | **TEMPLAR** | BM T1 TEMPLAR (L6130–6132, `CountHarmfulStatuses` L6699–6725) | **PORT** | `ON_ABILITY_DECLARED` + `HOSTILE_ACTION` → `ROLL_STAT_BONUS{ATK, PercentFrom:"STATUS_COUNT:HARMFUL", PerUnit:5, Max:20}` | — (BM's `HAS_STATUS_COUNT` gap is closed by `STATUS_COUNT`(C9) + the `PercentFrom` value source; the harmful-type set is an authored constant validated against EGT §9) |
| 27 | **THIEF** | BM T1 THIEF (L6362–6367, `TryStealAllowlistedBuff`) | **PARK** | — | Requires `STEAL_STATUS`: an ordered "first status present on the target from an allowlist" selector, a **bound reference** so the self-application uses the same id, and a not-found `else` branch (`FOCUS_CHANGE +1`). Three new schema concepts for one mechanic — fails the long-tail bar. **Unlock:** the `StatusSelector` sub-schema shared with PALADIN (SPEC-DELTA §7.5) |
| 28 | **TRICKSHOT** | BM T1 TRICKSHOT (L6368–6377) | **PORT** | `ON_ABILITY_USED` + `ROLL_TIER(PERFECT)` + `HOSTILE_ACTION` + `ABILITY_RANGED(true)` + `Budget{ONCE_PER_TARGET_PER_ROUND, EVALUATION}` → `ADD_STATUS{TRIGGER_TARGET, ENTANGLE_ROOTS_00, Fallback:ATTACKDOWN_00, 1}` | — (the round-stamped `SeenTargets` key is exactly `ONCE_PER_TARGET_PER_ROUND`; code-authoritative fallback is ATTACKDOWN, not the flavor text's Evasion Down) |
| 29 | **WARDEN** | BM T1 WARDEN (L6378–6390) | **PORT-MODIFIED** | `ON_TURN_END` + `MOVED_THIS_ROUND(false)` → `ADD_STATUS{ALLY_ALL_OTHERS, ARMORUP_00 + RESISTANCEUP_GROUP_00, 1}` | EOR gates on `IsEndTurnAbility(decision)` — an ability whose **name string** contains "SKIP"/"END_TURN"/"ENDTURN". v1.1 uses the engine's `ON_TURN_END` signal (native `EVENT_PROC END_TURN` via `_onCombatSkillProc`, PSN §1 L1818), which also fires when the Warden ends a turn after acting — **broader than EOR**, gated only by `MOVED_THIS_ROUND(false)`. Self-exclusion (`ALLY_ALL_OTHERS`) matches code, not flavor text |
| 30 | **WARRIOR** | BM T1 WARRIOR (L6458–6463 + L6133–6139) | **PORT** | Build: `ON_DAMAGE_TAKEN` + `HOSTILE_ACTION` → `COUNTER_ADD{rage, 1, Max:3}`. Consume: `ON_ABILITY_DECLARED` + `HOSTILE_ACTION` + `COUNTER{rage, GTE, 1}` → `ROLL_STAT_BONUS{ATK, PercentFrom:"COUNTER:rage", PerUnit:10, Max:30}` + `COUNTER_SET{rage, 0}` | — |
| 31 | **WIZARD** | BM T1 WIZARD (L6391–6396 + L6140–6148) | **PORT** | `ON_ABILITY_DECLARED` + `ABILITY_STAT(INT)` + `ABILITY_REPEATED(false)`(C5) → `ROLL_STAT_BONUS{ATK, 15%}` | — (`LastAbilityId` lives in the per-battle turn-state table, SPEC-DELTA §6, and is written by the `ON_ABILITY_USED` observer — no recording effect is needed in the recipe) |

**§1 counts: PORT 19 · PORT-MODIFIED 9 · PARK 3 = 31** ✔

---

## 2. Selectable loadout traits (20)

All 20 are `SelectableLoadoutTraitKeys` (TM §0, EOR `Plugin.cs` L5183–5187). **All 20 port their storage and
selection for free** under the OQ#1 resolution (`TRAIT_`-prefixed `Thing` in `CharacterComponent.Things`,
picked on the vanilla loadout screen; TM §1/§2/§4) — the verdicts below concern only their *effects*.
Rows cite **BM Table 2** for coded traits and **TM §3a / AUD §2.4** for the stat-only ones.

| # | Trait | Effect class | Citation | Verdict | Delivery / modification |
|---|---|---|---|---|---|
| 1 | `TRAIT_LIGHT_FOOTED` | stat-only (`EVD +10`) | TM §3a L18703–18705 | **PORT** | Pure `Equippable.Stats` data. Zero primitives, zero patches (native `GetStat` + `GetEquippedThingsNonAlloc(..., pIncludeTraits:true)`, TM §3a) |
| 2 | `TRAIT_TOUGHENED` | stat-only (`HP +10, DEF +2`) | TM §3a L18709–18712 | **PORT** | as above |
| 3 | `TRAIT_STREETWISE` | stat-only | TM §3a / AUD §2.4 | **PORT** | as above |
| 4 | `TRAIT_GOLD_INSTINCT` | stat-only (`GLD`) | TM §3a / AUD §2.4 | **PORT** | as above; `GLD` is a legal `eCharacterStats` member (EGT §6) and this is a passive stat, **not** the parked combat-time gold verb |
| 5 | `TRAIT_KNIFE_EDGE` | stat-only (`CRT +10, HP -3`) | TM §3a L18728–18731 | **PORT** | as above |
| 6 | `TRAIT_TREASURE_SENSE` | hybrid: stat + 20% post-combat gold | BM T2 TREASURE_SENSE (L16379–16388) | **PORT** | Stat half ports as data. **Loot half ADOPTED (2026-08-06, commits `fc17c53`/`f7b14b9`):** `SKILL_CF_TRAIT_TREASURE_SENSE_LOOT` — `ON_COMBAT_LOOT`, `ProcChance 20` → `GOLD_GRANT{Min:8, Max:20}`, byte-identical to EOR (L16379–16388), delivered via `CF_SYNC_LOOT_GRANT_V1` Mode-M mirror+audit (loot-grant-verb-spec.md §6.3). Ships dark behind `[Skills] EnableLootGrants=false` pending operator V-1 smoke |
| 7 | `TRAIT_STEADY_AIM` | hybrid: stat + first ranged attack/combat gets `+10 CRT` | BM T2 STEADY_AIM (L22876–22905) | **PORT** | Stat half as data; code half = `ON_ABILITY_DECLARED` + `HOSTILE_ACTION` + `ABILITY_RANGED(true)` + `Budget{ONCE_PER_COMBAT}` → `ROLL_STAT_BONUS{Stat:"CRT", Flat:10}`. **Also fixes EOR's bug**: `SteadyAimUsedThisCombat` is a process-global `HashSet` never cleared on a new run (TM §6 hazard 3); v1.1's budget lives in the `CombatKey`-invalidated single-slot runtime (SPEC-DELTA §6) and cannot leak |
| 8 | `TRAIT_WARDBOUND` | hybrid: stat + 20% resist CURSE/DEBUFF | BM T2 WARDBOUND (L24780–24788) | **PORT-MODIFIED** | Stat half as data. Resist half **redesigned**: EOR prefixes `ApplyStatus` and `return false`s (per-client suppression of authoritative state — forbidden by charter rule 3). v1.1 uses `ON_STATUS_APPLIED`(T5, **Post**fix) + `STATUS_TYPE{CURSE\|DEBUFF}`(C10) + `ProcChance 20` → `REMOVE_STATUS{SELF, "TRIGGER_STATUS"}`. **Delta:** the status is applied and then removed rather than never applied — any on-apply side effect fires once and the application is briefly visible |
| 9 | `TRAIT_SCHOLARS_HABIT` | hybrid: stat + 20% post-combat scroll | BM T2 SCHOLARS_HABIT (L16389–16396) | **PORT** | Stat half ports. **Loot half ADOPTED (2026-08-06, commits `fc17c53`/`f7b14b9`):** `SKILL_CF_TRAIT_SCHOLARS_HABIT_LOOT` — `ON_COMBAT_LOOT`, `ProcChance 20` → `ITEM_TAG_GRANT{Tag:SCROLL}`. **Recorded deviation from EOR (Gate B, deliberate):** candidate pool is native-stricter (tag OrdinalIgnoreCase + exact rarity + NOT Hidden + Value≠0 + rarity-weight≠0 + expansion-enabled, ordinal-sorted) rather than EOR's tag+rarity-only pool with hardcoded fallbacks (loot-grant-verb-spec.md §4.3/§9). Ships dark behind `[Skills] EnableLootGrants=false` pending operator V-1 smoke |
| 10 | `TRAIT_PREPARED` | `+1` Focus at combat start | BM T2 PREPARED (L22830/L22849) | **PORT** | `ON_COMBAT_START`(T1) + `FOCUS_CURRENT{LT, "MAX"}`(C8) → `STAT_CHANGE{SELF, FOC, +1}`. No RNG. EOR's `ShouldDisablePreparedFocusForMultiplayer` stub (always `false`, TM §6 hazard 2) is **not** copied — the primitive is genuinely deterministic, so no gate is needed or faked |
| 11 | `TRAIT_PACK_TACTICS` | conditional `PHY +2` when a pet is active | BM T2 PACK_TACTICS (L22616–22621) | **PORT-MODIFIED** | Becomes an **unconditional `PHY +1`** stat trait. `CONDITIONAL_STAT_MODIFIER` is parked (SPEC-DELTA §7.10): a `CharacterHelper.GetStat` postfix runs on an extremely hot path with 8 overloads (PSN §3), in and out of combat, with no authority/RNG context — cost/benefit fails for a flat `+2`. Halved to `+1` because it is now always on |
| 12 | `TRAIT_SCAVENGER` | 25% post-combat gold **or** herb | BM T2 SCAVENGER (L16357–16378) | **PORT** | **ADOPTED (2026-08-06, commits `fc17c53`/`f7b14b9`).** `SKILL_CF_TRAIT_SCAVENGER_LOOT` — `ON_COMBAT_LOOT`, `ProcChance 25`, `PickOneEffect: true` → `GOLD_GRANT{Min:8,Max:20}` **or** `ITEM_TAG_GRANT{Tag:HERB, Rarity:COMMON}`, mirroring EOR's nested 50/50 (L16357–16377) via the loot-grant verb's private grant-stream draw instead of the shared stream. Herb branch inherits the same Gate-B pool deviation as `TRAIT_SCHOLARS_HABIT` above. Ships dark behind `[Skills] EnableLootGrants=false` pending operator V-1 smoke |
| 13 | `TRAIT_BATTLE_RHYTHM` | on damage dealt → self `EVADEUP` | BM T2 BATTLE_RHYTHM (L24541/L24556) | **PORT** | `ON_DAMAGE_DEALT`(T3) + `HOSTILE_ACTION` → `ADD_STATUS{SELF, STATUS_EVADEUP_00}`. Uses the vanilla status EOR itself substitutes; the retired `STATUS_EOR_BATTLE_RHYTHM` custom id is **not** re-created (TM §6 hazard 4) |
| 14 | `TRAIT_ARCANE_MEMORY` | 30% chance not to consume a scroll | BM T2 ARCANE_MEMORY (L24454/L24462) | **PARK** | `SUPPRESS_CONSUME` parked (SPEC-DELTA §7.2). Both the original (prefix `return false` = per-client suppression) and the host-decided refund-after-consume redesign fail: scroll use is **normally out of combat**, where `CombatState.Random` is null, so invariant 5.2#2 forbids the roll — and the only escape is a fresh seeded `GameRandom`, which is exactly the non-lockstep path EOR shipped (BM Legend `RollSelectableTraitChance(random: null, …)`). **Unlock:** an out-of-combat shared deterministic RNG + a verified replicated inventory-grant verb |
| 15 | `TRAIT_SHIELDBEARER` | 25% chance, incoming physical damage `−2` | BM T2 SHIELDBEARER (L24604/L24608) | **PORT-MODIFIED** | Becomes a flat **`DEF +1`** stat trait, zero primitives. `DAMAGE_TAKEN_MULT` is parked (SPEC-DELTA §7.4): `CalculateFinalDamage` (PSN §2 L1708) has **no `GameRandom` parameter**, so a chance gate there must take a static draw at a point only the host may execute — charter rule 2's named failure. Balance note: `DEF +1` reduces every blockable physical hit by 1 vs EOR's expected 0.5/hit — roughly 2× stronger, flag for the balance pass |
| 16 | `TRAIT_FIELDMEDIC` | `+25%` healing given or received | BM T2 FIELDMEDIC (L24486/L24495) | **PORT** | `ON_HEAL_PENDING`(T8) → `HEAL_MODIFIER{Percent:25, MinDelta:1, Scope:RECEIVED\|GIVEN}`(E2) via `ref int pValue` on `CharacterHelper.AddHealth` (PSN §3 L1342/L1357). No RNG. EOR's single combined `if` (no double-count when both healer and target have it) is reproduced by one recipe with both scopes |
| 17 | `TRAIT_ARCANE_FOCUS` | conditional `MAG +2` when Focus ≥ 2 | BM T2 ARCANE_FOCUS (L22616/L22622) | **PORT-MODIFIED** | Unconditional **`MAG +1`** stat trait — same `CONDITIONAL_STAT_MODIFIER` park as PACK_TACTICS. (Note: `FOCUS_CURRENT`(C8) exists and could gate a *recipe*, but not a continuous stat read, which is what this trait is) |
| 18 | `TRAIT_MOMENTUM` | on kill → self `ATTACKUP` | BM T2 MOMENTUM (L24541/L24565) | **PORT** | `ON_KILL` (re-anchored to the `ApplyStatChange` prefix/postfix HP-before/after shape, which is exactly EOR's) → `ADD_STATUS{SELF, STATUS_ATTACKUP_00}`. Retired `STATUS_EOR_MOMENTUM` id not re-created |
| 19 | `TRAIT_DRUNKEN_COURAGE` | after a `DRINK_*` consumable → self `ATTACKUP` | BM T2 DRUNKEN_COURAGE (L24628/L24632) | **PORT-MODIFIED** | `ON_CONSUMABLE_USED`(T6) + `ITEM_CLASS{"ALCOHOL"}`(C14) → `ADD_STATUS{SELF, STATUS_ATTACKUP_00}`. **Delta:** the gate is the vanilla `ThingConfig.Class == "ALCOHOL"` (a real, verified class value, EGT §5) rather than EOR's `ConfigName.StartsWith("DRINK_")` / ability-id string prefix match — so the set of qualifying items is whatever the game classes as alcohol, not whatever happens to be named `DRINK_*`. Retired `STATUS_EOR_DRUNKEN_COURAGE` id not re-created |
| 20 | `TRAIT_MENDERS_TOUCH` | consumable heals get flat `+10` | BM T2 MENDERS_TOUCH (L24486/L24502) | **PORT** | `ON_HEAL_PENDING` + `ITEM_CONSUMABLE(true)`(C15) → `HEAL_MODIFIER{Flat:10, Scope:GIVEN}`. Additive stacking with FIELDMEDIC on the same healer is preserved (two recipes, both fire) |

**§2 counts: PORT 14 · PORT-MODIFIED 5 · PARK 1 = 20** ✔ (updated 2026-08-06 — loot-grant verb adoption,
commits `fc17c53`/`f7b14b9`; was PORT 11 · PORT-MODIFIED 7 · PARK 2)

---

## 3. Affixes (20 ids + 2 system rows = 22)

Affix ids verbatim from EOR `Plugin.cs` L5114–5135 (`AffixDefinition[20]`). **All 20 stat tables port as
deterministically pre-minted item-variant configs** in the Forge `_PLUS{N}` style (AUD §2.5, §6 row 6) — not
as runtime `Env.Configs.Things` mutation. All five bespoke affixes **share their code path with a trait**
(BM Table 3 verifies identical line ranges), so their verdicts mirror the trait rows above; the affix is
delivered as an **alternate grantor condition** on the same recipe, not a duplicate implementation.

| # | Affix | Stats | Coded effect | Citation | Verdict | Notes |
|---|---|---|---|---|---|---|
| 1 | `OF_FURY` | `PHY +1` | — | L5116 | **PORT** | stat-only pre-mint |
| 2 | `OF_PRECISION` | `CRT +5` | — | L5117 | **PORT** | stat-only pre-mint |
| 3 | `OF_MOMENTUM` | `SPD +2` | on kill → self `ATTACKUP` | BM T3 OF_MOMENTUM (L24565, identical path to `TRAIT_MOMENTUM`) | **PORT** | Same recipe as `TRAIT_MOMENTUM`; the affix is an alternate grantor |
| 4 | `OF_EXECUTION` | `CRT +5` | — | L5119 | **PORT** | stat-only pre-mint |
| 5 | `OF_THE_ARCANE` | `MAG +1` | — | L5120 | **PORT** | stat-only pre-mint |
| 6 | `OF_THE_SCHOLAR` | `INT +5` | — | L5121 | **PORT** | stat-only pre-mint |
| 7 | `OF_SPELLKEEPING` | `INT +1` | 10% not to consume a scroll | BM T3 OF_SPELLKEEPING (L24463, same method as `TRAIT_ARCANE_MEMORY`, independent 10% roll) | **PORT-MODIFIED** | Stat half ports; **code half parked** with `SUPPRESS_CONSUME` (SPEC-DELTA §7.2). BM flags this as the highest-hazard row in the corpus (`random: null` seeded-fallback, out-of-combat, per-client suppression) |
| 8 | `OF_FOCUS` | `MXFOC +1` | `+1` Focus at combat start | BM T3 OF_FOCUS (L22849, identical path to `TRAIT_PREPARED`) | **PORT** | Same recipe as `TRAIT_PREPARED`; alternate grantor |
| 9 | `OF_THE_BULWARK` | `DEF +1` | — | L5124 | **PORT** | stat-only pre-mint |
| 10 | `OF_AEGIS` | `RES +1` | — | L5125 | **PORT** | stat-only pre-mint |
| 11 | `OF_FORTITUDE` | `HP +5` | — | L5126 | **PORT** | stat-only pre-mint |
| 12 | `OF_RENEWAL` | `HRG +1` | — | L5127 | **PORT** | stat-only pre-mint |
| 13 | `OF_STABILITY` | `VIT +1` | 20% resist CURSE/DEBUFF (**one roll shared with `TRAIT_WARDBOUND`**, not independent) | BM T3 OF_STABILITY (L24780) | **PORT-MODIFIED** | Stat half ports; resist half uses `TRAIT_WARDBOUND`'s **redesigned** apply-then-cleanse recipe (`ON_STATUS_APPLIED` + `REMOVE_STATUS{TRIGGER_STATUS}`). Same semantic delta as row §2.8. Shared-roll behavior preserved: one recipe, two grantor conditions — a character with both trait and affix gets one 20% roll, not two |
| 14 | `OF_FORTUNE` | `LCK +5` | — | L5129 | **PORT** | stat-only pre-mint |
| 15 | `OF_PROSPERITY` | `GLD +10` | — | L5130 | **PORT** | stat-only pre-mint (passive `GLD` stat, not the parked combat-gold verb) |
| 16 | `OF_SCAVENGING` | `LCK +2` | 10% post-combat gold **or** herb | BM T3 OF_SCAVENGING (L16397–16418) | **PORT** | Stat half ports. **Loot half recipe ADOPTED (2026-08-06, commits `fc17c53`/`f7b14b9`):** `SKILL_CF_AFFIX_OF_SCAVENGING_LOOT` — `ON_COMBAT_LOOT`, `ProcChance 10`, `PickOneEffect: true` → `GOLD_GRANT{Min:5,Max:10}` **or** `ITEM_TAG_GRANT{Tag:HERB, Rarity:COMMON}` — written and validated, but **carrier-less**: no pre-minted `_OF_SCAVENGING` item variant exists yet to carry it as an `Equippable.Passives` entry (row 21's deterministic pre-mint registry), so the recipe cannot fire in practice until **M-LG4** wires the affix-variant carrier. BM's stacked-draw hazard (up to 8 extra shared-stream draws/player/combat, AUD §3.2) is moot regardless — the loot-grant verb draws from a private grant stream, never the shared one |
| 17 | `OF_THE_PATHFINDER` | `MOV +1` | — | L5132 | **PORT** | stat-only pre-mint |
| 18 | `OF_THE_WILDS` | `AWR +5` | — | L5133 | **PORT** | stat-only pre-mint |
| 19 | `OF_THE_DUELIST` | `SPD +5, EVD +3` | — | L5134 | **PORT** | stat-only pre-mint |
| 20 | `OF_THE_MERCHANT` | `TAL +5` | — | L5135 | **PORT** | stat-only pre-mint |
| 21 | **[system]** Affix variant **minting** | — | `TryEnsureAffixVariantConfig` mutates `Env.Configs.Things` at runtime; variants tracked in a per-process `AffixedItemVariants` dict that is **not persisted**, requiring a whole restore/repair layer (L17013–L17384) | AUD §2.5 (L16941/L5048) | **PORT-MODIFIED** | Replaced by **deterministic pre-minting**: every `<affix × eligible base item>` pair becomes an ordinary parity-hashed `ThingConfig` id at load, Forge `_PLUS{N}`-style. Removes the runtime `Configs` mutation, the restore/repair layer, and the R1 hash instability. Explicitly refused by `FTK2.Forge/SPEC.md`'s design and by `docs/MULTIPLAYER.md`'s "prefer config-shaped content over runtime state" |
| 22 | **[system]** Affix **drop-time rolling** | — | `PickWeightedAffix` + `TryCreateAffixedThing` roll an affix onto a dropped item from the shared `GameRandom` inside the loot path | AUD §2.5, §3.2; BM T3 loot-postfix hazard class | **PARK — M-LG4** | **Reason superseded (2026-08-06):** the loot hook is no longer absent from PSN — `LootDropHelper.GetLootDropsFromEnemies` is verified (PSN §11) and `CF_SYNC_LOOT_GRANT_V1` ships (M-LG1–3, commits `fc17c53`/`f7b14b9`/`22131ce`). What still blocks this row specifically: the `REPLACE_ITEM` delta op is defined in the wire schema (loot-grant-verb-spec.md §3.1/§3.2) but **validator-rejected in v1** pending row 21's deterministic pre-mint registry (the fixed `<affix × base item>` id set `REPLACE_ITEM` would swap onto). **Unlock:** M-LG4 — pre-mint registry + `REPLACE_ITEM`/`AFFIX_ROLL` validator activation, gated on in-game verification item V-2 (delivery-window measurement for the reserved Mode-H host-push variant) |

**§3 counts: PORT 18 · PORT-MODIFIED 3 · PARK 1 = 22** ✔ (updated 2026-08-06 — loot-grant verb adoption,
commits `fc17c53`/`f7b14b9`; was PORT 17 · PORT-MODIFIED 4 · PARK 1; row 22 stays PARK, re-gated on M-LG4)

---

## 4. Mastery meta-progression (1)

| Mechanic | Citation | Verdict | Reason |
|---|---|---|---|
| **Class mastery system** — per-class XP/rank meta-progression, persisted to the **player profile** via `StatsHelper`, driven by a **per-frame poll gated to offline-only** | AUD §2.1 (EOR `Plugin.cs` L6838–6957; poll L19741) | **PARK** | **Out of scope, as expected by the charter.** Three independent disqualifiers: (1) it is *meta*-progression persisted outside the run, which no current SPEC in this repo owns — ClassForge's state lifecycle is per-session `Configs` content + per-battle recipe state, with no profile-persistence surface; (2) it is **explicitly offline-only in EOR itself** (the per-frame poll is gated), so there is no MP design to port and building one is a new feature, not a re-host; (3) a per-frame `Update` poll is not a data-driven primitive and could not be expressed as a recipe under any plausible vocabulary. **Unlock:** a dedicated profile-progression SPEC (not ClassForge) with its own MP posture; re-spec later if missed, per charter's "Dropped outright" precedent for AUD §6 rows 9/11 |

---

## 5. Cross-cutting notes

1. **No mechanic is UNPORTABLE.** BM's "Cross-cutting notes" already established that all 47 primary + 4 hybrid
   mechanics resolve to primitive combinations. Every PARK here is a *deliberate deferral* on MP-safety or
   grounding-verification grounds, never "this cannot be modeled."
2. **The two largest primitive wins.** `OUTGOING_ATK_BUFF_WINDOW` → `ROLL_STAT_BONUS` (E1) alone unlocks 10
   mechanics (ASSASSIN, CORSAIR, PEASANT, RANGER, SCOUT, TEMPLAR, WARRIOR, WIZARD, STEADY_AIM, + DUELIST once
   `ON_DODGE` lands). Per-effect `Conditions[]` — which costs **zero** new hooks and zero MP surface — unlocks
   ASSASSIN, BARD, FORTUNEBORN, JESTER, PRIEST.
3. **Three EOR bugs are fixed by construction, not by patching.** (a) `STEADY_AIM`'s process-global budget leak
   (TM §6 hazard 3) → `CombatKey`-invalidated single-slot runtime. (b) `BEASTMASTER`'s silent turn-order
   dependency (BM MP-hazard column) → status-based quarry link. (c) `GLADIATOR`/`MARSHAL` firing on kills they
   did not cause → origin-attributed `ON_KILL`.
4. **Four text/code mismatches inherited deliberately.** RANCHER, TRICKSHOT, SCOUT and WARDEN all ship the
   **code** behavior, not the flavor text, per BM's authority mandate. Localization for these four must be
   rewritten to match the ported behavior rather than copied from EOR.
5. **Three retired custom status ids are never re-created.** `STATUS_EOR_BATTLE_RHYTHM`, `STATUS_EOR_MOMENTUM`,
   `STATUS_EOR_DRUNKEN_COURAGE` (TM §6 hazard 4) have no `StatusEffectConfig` in the shipped build; ClassForge
   uses the vanilla statuses EOR's own later code substitutes.
6. **Balance passes owed** (all flagged, none blocking): PACK_TACTICS `+2`→`+1`, ARCANE_FOCUS `+2`→`+1`,
   SHIELDBEARER →`DEF +1` (≈2× stronger), ~~TREASURE_SENSE / SCHOLARS_HABIT / OF_SCAVENGING losing their loot
   halves~~ (**resolved 2026-08-06** — loot halves shipped via the loot-grant verb, see §2 rows 6/9/12 and §3
   row 16), BEASTMASTER newly inheriting `STATUS_MARKED_00`'s vanilla +30 crit chance, and AUD §2.1's noted
   class stat-envelope power creep (LCK 50–95 vs vanilla flat 50).
7. **Not covered by this matrix** (dispositioned elsewhere, by design): classes as data (AUD §6 row 3 → the
   ClassForge M1 loader), items (row 7 → Armory), pets/mercs (rows in AUD §2.3 → Summoner), quest archetypes
   (row 8 → Questsmith, deferred), and the systems the charter dropped outright (row 9: ~~Risky Blessings~~ —
   **ADOPTED v0, 2026-08-06**, see note 8 below; ~~encounter modifiers~~ — **ADOPTED, 2026-08-06**, see note 8
   below; Nemesis, campaign mutators, world events, town specialists, sanctums remain dropped/re-spec-later;
   row 11: telemetry, version check, debug toolkit, camera tweaks).
8. **Two AUD §6 row 9 systems re-specced and shipped (2026-08-06), superseding note 7 above for these two
   only:**
   - **Risky Blessings — PORT (v0).** Re-hosted as `FTK2.Blessings` (a thin orchestrator plugin + the
     `BLSS_PACK_EOR_BLESSINGS` ClassForge content pack) — commit `e57b02b` (M1 content: 15 blessings,
     Σweight 114, EOR table order), commit `1b98408` (M2 orchestrator: hash-derived deterministic offer pick,
     zero RNG draws; symmetric grant anchor gated on `ParityBridge.HasVerifiedMatch()`, Gate E). All 15 EOR
     blessings ship `Enabled: true`. Fixes EOR's three identified desync mechanisms (non-shared offer RNG,
     client-local acceptance, stubbed MP guards). v1 (in-run accept/decline dialog + `BLSS_SYNC_BLESSING_V1`)
     remains parked. Full design: `docs/superpowers/plans/2026-08-05-risky-blessings-spec.md`.
   - **Encounter modifiers — PORT.** Re-hosted on ClassForge's engine as `CF_PACK_ENCOUNTER_MODIFIERS`
     (`statuses.json`+`modifiers.json`, all 10 EOR modifiers, `Scope: COMBAT` selection/application recipes)
     — commits `f8c4eaf` (M-EM1 pack surface), `a92a0e3` (M-EM2 engine capability: `Scope:COMBAT`,
     `ProcChanceFormula`, `IS_ENEMY`), `d293536` (M-EM3/4 generated recipes + reward halves). **Reward halves
     included** (Veteran/Cursed XP, Wealthy gold, Treasure-Guarded extra loot) — they ride the loot-grant
     verb's `LOOT_SCALE`/`ADD_ITEM` ops in Mode M, drawing from a private per-combat stream (a recorded
     improvement over EOR's shared-stream extra-loot draw). Full design:
     `docs/superpowers/plans/2026-08-05-encounter-modifiers-spec.md`.

   Nemesis, campaign mutators, world events, town specialists and sanctums remain dropped per note 7.

---

## Implementation updates (2026-07-25)

Findings from Wave 3 (implementation of `CF_PACK_EOR_CLASSES` against this matrix) and the Wave-4 adversarial
MP-correctness review (`docs/research/eor-rehost-mp-review.md`) that refine or correct dispositions above.
None reopen a PORT/PORT-MODIFIED/PARK verdict; all are either newly-discovered evidence or authoring notes
found while actually writing the recipes this matrix specified.

1. **`ON_DODGE` is unparkable as of this update — new evidence, not previously found.** §7's parked-primitive
   index (and DUELIST's row, §1 row 9) cite PSN's `eAbilityResults` investigation as finding no dodge member.
   A direct decompile check of `tools/out/decompile/FTK2/eAbilityResults.cs` shows **`DODGED` is in fact a
   member of the enum** (line 14). This does not retroactively unpark DUELIST in this wave — the recipe
   engine's dispatch surface and DUELIST's specific counter/`ROLL_STAT_BONUS` shape were not built against it
   — but it removes the stated blocker. **Candidate for v1.2**: add an `ON_DODGE` trigger anchored wherever
   `eAbilityResults.DODGED` is appended to a results list (needs its own hook-point verification, same
   discipline as every other trigger in SPEC-DELTA-v1.1 §2), then revisit DUELIST (§1 row 9) and any other
   mechanic that was parked solely on this evidence gap.
2. **`ROLL_TIER {Value: "FAIL"}` is a schema token the game can never produce — validator-warning candidate.**
   The game's real `eRollStatus` has exactly **three** members: `CRIT_FAIL, SUCCESS, PERFECT` — there is no
   `FAIL`. The shipped recipe engine's `RollTier` vocabulary additionally declares `FAIL` for a complete
   worst→best ordering (`CRIT_FAIL < FAIL < SUCCESS < PERFECT`), so `GTE`/`LTE` comparisons against `FAIL`
   still behave sensibly (they degrade to "at least/at most `CRIT_FAIL`-or-better", which is meaningful), but
   `{"Comparator":"EQ","Value":"FAIL"}` can **never** match — the game never produces that roll grade.
   Verified: `ClassForge.Plugin/Recipes/RecipeEngineHost.cs` `MapRollTier` maps only `CRIT_FAIL`/`PERFECT`
   explicitly and defaults everything else (including the game's actual `SUCCESS`) to `RollTier.SUCCESS`.
   **Recommended fix, not yet built:** the pack loader's recipe validator should warn (not error, since the
   condition is inert rather than broken) on any `ROLL_TIER{Comparator:"EQ", Value:"FAIL"}` authored against a
   trigger whose roll tier comes from `pRollData.Status` (i.e. every trigger except the few that don't carry
   one), so an author doesn't ship a silently-dead condition.
3. **PRIEST ships simplified: `ON_HEAL` carries no roll tier, so the tiered status choice is honestly
   flattened to one tier.** §1 row 20's recipe sketch gates the `_01`-tier status pair (`ARMORUP_01`/
   `RESISTANCEUP_GROUP_01`) on `ROLL_TIER(EQ PERFECT)`, mirroring BM's read of the EOR source. As authored,
   `ClassForge.Recipes`' `HealEvent` (`Runtime/TriggerEvents.cs`) carries **no roll-tier field** under
   `ON_HEAL` — that condition would always read the engine's `RollTier.SUCCESS` default and never evaluate
   true, silently downgrading the recipe to always-`_00` while *appearing* to gate on `PERFECT` in the JSON.
   The shipped `SKILL_CF_PRIEST_BENEDICTION` recipe (`CF_PACK_EOR_CLASSES/skillrecipes.json`) ships the
   honestly-expressible **always-`_00`** behavior instead of an improvised roll-tier gate that would silently
   never fire its intended branch — documented inline in the recipe's own `_source`/provenance
   `expressiveness_gaps` note. This is a real behavioral simplification versus BM's read of EOR, not a bug in
   the shipped recipe; closing the gap needs `ON_HEAL` to carry an explicit roll-tier datum, which is not
   currently among `InteractableHelper.ApplyStatChange`'s verified parameters for a heal path.
4. **BEASTMASTER's pet-half recipe is authored but unattached — no pet Character config to carry it.**
   §1 row 4's recipe sketch calls for a master recipe (quarry-marking, `SKILL_CF_BEASTMASTER_QUARRY`, shipped
   and attached) plus a pet recipe (`SKILL_CF_BEASTMASTER_PACK_TACTICS`, authored onto pack pet configs).
   `CF_PACK_EOR_CLASSES` owns no pet `Characters.json` entry — pack pets/mercs are Summoner's domain per this
   matrix's own §5 cross-cutting note 7 — so `SKILL_CF_BEASTMASTER_PACK_TACTICS` is written and validated but
   **currently unattached to any `Passives[]`** (see the recipe's own `_source` note and
   `provenance.json`'s `expressiveness_gaps`). No functional loss for the shipped pack (Beastmaster's own
   quarry-marking half fully ports), but the pet half needs a home the moment a Beastmaster-compatible pet
   config exists (Summoner M1+ or a future ClassForge pack).
5. **M6 (Armory zero-registration) posture note.** The Wave-4 MP review's M6 finding — Armory's ~417 items
   reach `Configs` via manual copy/merge (`FTK2.Armory/INSTALL.md`) with **zero parity registration** — is a
   real gap, but its disposition is a posture note, not a coverage-matrix reclassification: Armory ships no
   C# at all by design (§6 row 1's "zero-code M1" status), so there is nothing in Armory itself to register
   with. When installed under the recommended EOR path, EOR's own hash covers `CustomItems` (including our
   packs) as part of its existing (if diagnostic-only) sync-guard hashing — better than nothing, but not R1
   enforcement. **Our own DevKit-side registration for manually-installed data roots is a future Armory
   micro-plugin item**, not a Wave-3/4 deliverable: a minimal plugin (or a DevKit hook a player points at an
   arbitrary data folder) that computes `DevKit.DataHasher.ComputeFolderHash` over the installed Armory data
   and registers `(guid, version, dataHash)` the same way every other `ftk2mods.*` mod does. Tracked here so
   it isn't lost between the MP review (which found it) and Armory's own SPEC (which doesn't yet plan for it).

---

## Implementation updates (2026-08-06)

Wave-2 findings (loot-grant verb, Risky Blessings, encounter modifiers — implementation on `engine/eor-rehost`,
offline-verified) that flip dispositions above. None invents a new verdict category; every flip is recorded
inline at its row plus recapped here for scannability.

1. **Loot-grant sync verb (`CF_SYNC_LOOT_GRANT_V1`) adopted — unlocks 4 of the 8 `GOLD_GRANT`/`ITEM_TAG_GRANT`
   consumers named in the parked-primitive index (§0).** Commits `fc17c53` (M-LG1 offline core), `f7b14b9`
   (M-LG2 hooks + SP + consumer recipes), `22131ce` (M-LG3 MP wire-up). Flips: §2 row 6 `TRAIT_TREASURE_SENSE`
   (PORT-MODIFIED → PORT), §2 row 9 `TRAIT_SCHOLARS_HABIT` (PORT-MODIFIED → PORT), §2 row 12
   `TRAIT_SCAVENGER` (PARK → PORT), §3 row 16 `OF_SCAVENGING` (PORT-MODIFIED → PORT, carrier-less pending
   M-LG4). §3 row 22 (affix drop-rolling) **stays PARK**, re-gated on M-LG4's pre-mint registry +
   `REPLACE_ITEM` validator activation rather than the now-resolved "loot hook absent from PSN" reason. One
   deliberate deviation recorded against EOR (Gate B): `ITEM_TAG_GRANT`'s candidate pool is native-stricter
   than EOR's tag+rarity-only pool. Ships **dark** behind `[Skills] EnableLootGrants = false` pending operator
   smoke item V-1 — every flipped row is a *shipped, verified-offline* capability, not yet an in-game-confirmed
   one. Full design: `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md`.
2. **Risky Blessings re-specced and shipped (v0) — was AUD §6 row 9 "dropped".** Commits `e57b02b` (content
   pack) + `1b98408` (orchestrator). See note 8 in "Cross-cutting notes" above. **PORT.**
3. **Encounter modifiers re-specced and shipped — was AUD §6 row 9 "dropped".** Commits `f8c4eaf`/`a92a0e3`/
   `d293536`. Reward halves ride the loot-grant verb (item 1 above) rather than being separately parked. See
   note 8 in "Cross-cutting notes" above. **PORT.**
4. **Totals recount (§0 Summary counts, updated in place above):** §2 PORT 11→14, PORT-MODIFIED 7→5, PARK
   2→1 (20 unchanged). §3 PORT 17→18, PORT-MODIFIED 4→3, PARK 1→1 unchanged in count but row 22's reason
   changed (22 unchanged). §1/§4 untouched. Grand total: PORT 47→51, PORT-MODIFIED 20→17, PARK 7→6 (74 rows
   unchanged). Risky Blessings and encounter modifiers are AUD §6 row-9 *systems*, not rows in this
   mechanic-by-mechanic matrix, so their adoption doesn't change the 74-row count — it is recorded in
   "Cross-cutting notes" note 8 instead.
