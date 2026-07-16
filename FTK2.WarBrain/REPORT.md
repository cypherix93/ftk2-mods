# WarBrain overnight report — battle-AI discovery, build, and validation

*2026-07-16, autonomous overnight run. Everything below is committed on `master`; each section
links the artifact with the full detail.*

## TL;DR

Your instinct was right, and it's now quantified: late-game enemies are unthreatening for two
separable reasons — **they play randomly** (ability, target, and focus are all literally uniform
random picks) and **their numbers can't penetrate late-game mitigation** (flat DEF subtraction +
unrestricted player dodge + no enemy accuracy growth). I decompiled the entire vanilla AI, built
the WarBrain scoring engine + a faithful combat simulator, ran ~350k simulated battles, and
implemented the working BepInEx plugin (M1).

Headline numbers (vs. a scripted *experienced* 4-player party, identical enemy stats):

- Smarter decisions alone (WarBrain): **enemy damage ×1.5–2.0, player knockdowns ×7–25** across
  every party comp and squad type tested.
- Decisions alone still never wipe a good party (0% wipes) — the flat-DEF math caps what smart
  play can do. Stats alone are equally insufficient (vanilla AI at ATK+50%: 0.31 downs/battle).
- **Decisions + modest tunable scaling** (ATK×1.25, HP×1.25, ACC+8): 0.51 downs/battle, real
  pressure. At ×1.5/+8: 1.2 downs and party wipes start appearing (2.1%) — a knob you can turn.

One critical architecture discovery: **enemy AI runs deterministic-lockstep on ALL peers in co-op**
(not host-only, as the spec assumed). WarBrain is built for that: shared-RNG draws, decimal-only
math, identical-data requirement. Details below — this affects every future combat mod in the repo.

---

## 1. What was built (all committed)

| Artifact | Where | State |
|---|---|---|
| IL-verified AI/combat deep-dive | `docs/research/battle-ai-deep-dive.md` | complete — resolves every SPEC §11 open question |
| WarBrain.Core scoring engine | `FTK2.WarBrain/src/WarBrain.Core/` | complete, shared by sim + plugin |
| Combat simulator + experiment runner | `FTK2.WarBrain/sandbox/WarBrainSim/` | complete; `dotnet run -c Release` reproduces everything |
| Full results matrix (2000 battles/cell) | `FTK2.WarBrain/sandbox/WarBrainSim/results.md` | committed |
| BepInEx plugin M1 | `FTK2.WarBrain/src/WarBrain.Plugin/` | **builds clean against the real game DLLs; not yet launched in-game** |
| SPEC corrections | `FTK2.WarBrain/SPEC.md` §12 | committed |
| Install/build/knob docs | `FTK2.WarBrain/README.md` | committed |

## 2. Root cause: why late-game enemies don't scare you

From full decompilation of `AIHelper`/`CombatHelper`/`InteractableHelper`/`SlotRollHelper` and
analysis of all 2,072 enemy configs (evidence: deep-dive doc, §1–2):

1. **The "AI profiles" system is dead code.** All 48 `Followers.json` entries ship `DEFAULT`
   behaviour, so every enemy takes `StandardAiDecision`: a **uniformly random** usable ability
   from the weapon's bag. The nuke and the 0-damage status poke are equally likely.
2. **Targeting is random.** Ability tendencies (target-the-weakest etc.) fire only `PRW`% of the
   time (median PRW = 50), 75% of damage abilities have no tendency at all, and the targetable
   list is shuffled first. Nobody gets focus-fired; your healer is never hunted.
3. **Focus is wasted.** Enemies spend `random(0..focus)` — yet each focus point is a guaranteed
   success slot **+5% crit**, and a PERFECT roll on pierce-type abilities (102 exist) bypasses
   DEF/RES entirely. The AI's biggest damage lever, rolled away.
4. **The math wall.** Enemy damage = `maxDmg × successRatio − DEF` (flat subtract). A level-7
   enemy median-rolls ~33 pre-mitigation; a tank with DEF 30+ takes ~3, an EVD build dodges
   40–60% of everything with no cooldown (players are exempt from the dodge cooldown enemies
   have — verified). Past level 7 enemies grow only ATK +12%/HP +15% per level; their hit-rate
   stat (ACC) never grows. MASTER difficulty's entire enemy buff is **ACC +3**.

Conclusion: ~half the problem is decisions (fixable by WarBrain scoring, zero stat changes),
~half is numbers (needs the tunable scaling layer you approved).

## 3. Multiplayer architecture finding (affects the whole repo)

`CombatPhase._engageActiveEntity` computes enemy decisions **on every peer with no host gate**,
consuming the shared seeded `CombatState.Random`; peers stay identical by lockstep, checked by
the game's desync detector. So WarBrain's parity class is **`ALL_PEERS`**, not `HOST_ONLY`:

- Every peer must run identical WarBrain version + data + difficulty/scaling config.
- WarBrain draws randomness only from `CombatState.Random` and computes scores in `decimal`
  (float math could diverge across machines) — both implemented.
- M1 logs a `dataHash` at startup; hard enforcement belongs to the repo-wide ParityService
  (`docs/MULTIPLAYER.md` R1) when it lands.

`docs/MULTIPLAYER.md` open question #1 should be marked resolved (evidence in deep-dive §3).

## 4. Simulation evidence (sandbox, 2000 battles/cell)

The sim reimplements the decompiled resolution math exactly (slot rolls, focus bonuses, crit,
flat mitigation, pierce-on-perfect, the player/enemy dodge asymmetry) and pits AIs against a
deliberately strong scripted party (focus-fire, kill-secure, max-focus nukes, proactive heals).
Full tables: `sandbox/WarBrainSim/results.md`.

**Decisions only** (identical stats, level-7 enemies, BALANCED party):

| Squad | AI | Enemy dmg/battle | Player downs | Wasted enemy turns |
|---|---|---|---|---|
| BRUTES | vanilla | 78 | 0.02 | 19% |
| BRUTES | **WarBrain** | **126** | **0.45** | 19% |
| MIXED | vanilla | 48 | 0.00 | 26% |
| MIXED | **WarBrain** | **71** | **0.12** | 24% |
| WARBAND | vanilla | 36 | 0.00 | 34% |
| WARBAND | **WarBrain** | **64** | **0.06** | 20% |

Same shape holds for TURTLE / GLASS_CANNON / DODGE comps (see results.md). Against the DODGE
party, WarBrain cuts wasted enemy turns from 35% → 23% by targeting low-EVD members.

**Focus policy isolated** (everything else fixed): NONE 41 dmg → VANILLA_RANDOM 65 → SMART 71 →
**MAX 78**. Confirms focus as the single biggest decision lever; MAX beats SMART because battles
are short enough that hoarding focus has no value — recommendation below.

**Difficulty knobs** scale smoothly: intelligence 0.5→1.5 with temperature 3.0→0 moves damage
50→70+ and downs 0.01→0.24 — a usable easy↔brutal dial with no stat changes.

**Scaling layer** (BALANCED party, MIXED squad):

| Scaling | vanilla-AI downs | WarBrain downs | WarBrain wipes |
|---|---|---|---|
| none | 0.00 | 0.12 | 0% |
| ATK×1.25 | 0.03 | 0.38 | 0% |
| ATK+HP×1.25, ACC+8 | 0.06 | **0.51** | 0% |
| ATK+HP×1.5, ACC+8 | 0.31 | **1.22** | **2.1%** |

Neither lever alone threatens an experienced party; together they do, tunably.

*Sim caveats:* single-action turns, rows-only grid, no death saves/life pool, no stun/entangle
effects, archetype stats (config medians + estimated late-game player gear). Also note: against
fast parties, back-row enemies often die before acting, so enemy-side metrics for GLASS_CANNON/
DODGE cells are driven by the front-line pair (verified artifact, not a bug). Trends and ratios
are what to trust, not absolute values.

## 5. Recommended defaults for playtesting

- `[Difficulty] GlobalIntelligenceScalar=1.0, GlobalTemperatureMultiplier=1.0, MistakeChanceAdd=0`
  (shipped profiles already read as "opinionated but not perfect").
- `[Assignments] DefaultFocusPolicy=MAX` for maximum threat, or leave per-profile `SMART` if you
  want brutes reckless and tacticians deliberate. My call: ship `SMART` but raise
  `WB_CONSIDER_FOCUS_EFFICIENCY` weights so SMART converges toward MAX when damage is on the table.
- `[Scaling] EnableEnemyScaling=true, ATK×1.25, HP×1.25, ACC+8, FOC+1` as the "late-game feels
  dangerous" baseline; ×1.5 tier as an opt-in brutal mode. Off by default in the shipped config
  so vanilla-parity is the out-of-box state.

## 6. Plugin M1 status & how to try it

Builds clean (`dotnet build FTK2.WarBrain/src/WarBrain.Plugin -c Release`) against your game
install. Implements: takeover prefixes with fail-safe + re-entrancy latch, vanilla-mirroring
candidate enumeration, closed-form damage prediction, assignment resolution
(id > tag > basetype > behaviour > default), softmax with lockstep RNG, decision-breakdown
logging, JSON hot-reload on `ConfigsHelper.ReloadConfigs`, and the `[Scaling]` layer at the
exact code site vanilla difficulty uses. Install steps in `FTK2.WarBrain/README.md`.

**I did not launch the game or copy anything into your BepInEx folder** — didn't want to touch
your live install unattended. First in-game step: install per README, set `VerboseLogging=true`,
start any fight, and check the `[WarBrain]` decision lines in the BepInEx console (SPEC §8 test 1).

## 7. Open items for your review

1. **Focus policy default** — MAX vs tuned-SMART (see §5). Cheap decision, big feel impact.
2. **M1 limitation**: `TileOccupancy: EMPTY` abilities (summons/teleports) aren't scored; enemies
   whose only options are those defer to vanilla that turn. Fine for M1, needs modeling in M2.
3. **Profile tuning pass**: the four shipped brains were spec-era guesses; the sim can now
   A/B any weight change in minutes (`dotnet run -c Release -- 2000 out.md`). Worth a dedicated
   tuning session against the comps you actually play.
4. **Memory subsystem (M2)** is stubbed in the sim (healer-identified reaction works there) but
   not in the plugin — per SPEC milestones.
5. **ParityService**: ALL_PEERS makes it load-bearing for MP safety, raising its repo priority.
6. **In-game verification** of the SPEC §8 checklist — needs a human at the keyboard.

## 8. Commit trail (this session)

- `18eac1f` discovery deep-dive
- `ccba327` WarBrain.Core + sandbox simulator
- `a7c1f35` 2000-battle experiment matrix
- `8d800e5` plugin M1 + SPEC §12 + README

*Note: unrelated commits from your parallel session are interleaved on master; nothing collided.*
