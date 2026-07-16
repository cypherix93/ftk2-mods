# FTK2 battle-AI deep dive (IL-verified)

Extracted 2026-07-16 from `FTK2.dll` via ilspycmd full decompilation of `AIHelper`, `CombatHelper`,
`CombatPhase`, `InteractableHelper`, `SlotRollHelper`, `CharacterHelper`, `CoreHelper`, plus analysis
of the shipped `Configs/JSON~` data. Everything below is verbatim decompiled behavior, not inference.
This doc resolves the open questions in `FTK2.WarBrain/SPEC.md` §11 and provides the quantitative
root-cause analysis for "late-game enemies aren't threatening."

## 1. The vanilla decision pipeline, exactly

### 1.1 Call flow (per enemy turn)

```
CombatPhase._engageActiveEntity()                  [async void; runs on EVERY peer]
 ├─ status gates: STUN/GRAB → SkipTurn; CONFUSE → ConfusedAiDecision;
 │   SCARE/COWARD-tag/thief → flee-or-move-to-backrow; charged ability → resume charge
 └─ else if entity.Has<AIComponent>():
      CombatPhase._performAiDecision(entity,
          AIHelper.BehaviourAiDecision(entity, gameRun, skillContext), results)
```

Verified signatures (SPEC §11 "exact method signatures" — resolved):

```csharp
public static CombatDecisionData BehaviourAiDecision(Entity pActiveEntity, GameRunData pGameRun, (eSkills, int) pSkillContext)
public static CombatDecisionData StandardAiDecision (Entity pActiveEntity, GameRunData pGameRun, (eSkills, int) pSkillContext, bool pForcedShuffle = false)
public static CombatDecisionData ConfusedAiDecision (Entity pActiveEntity, GameRunData pGameRun, (eSkills, int) pSkillContext, Func<Entity, Thing, bool> pCanUseItem)
public static CombatDecisionData ForceAiDecision    (Entity pActiveEntity, GameRunData pGameRun, AbilityAction pForcedAbility, (int x, int y)? pForcedPosition = null)
public class CombatDecisionData { public AbilityAction Ability; public (int x, int y) Position; public int FocusUsed; }
public struct AbilityAction { public string AbilityName; public string ThingConfigName; public string ThingId; public SkillContext SkillContext; }
```

Note: `CombatDecisionData.FocusUsed` is an **int** (number of focus points committed), not a bool —
corrects SPEC §6's "FocusUsed bool" assumption. Focus committed is later added to
`abilityConfig.RequiresFocus` at execution (`CombatPhase._performAbility` line ~3973:
`focusUsed = pCombatDecision.FocusUsed + abilityConfig.RequiresFocus`).

### 1.2 `BehaviourAiDecision` is dead code in the shipped game

`BehaviourAiDecision` only does behaviour-weighted category selection when the entity's
`CharacterComponent.TypeArgs` names a `Followers.json` entry whose `Behaviour != DEFAULT`.
**All 48 Followers.json entries ship with `Behaviour: DEFAULT`** (verified against the live config),
and regular enemies don't have follower TypeArgs at all — so every enemy turn in the shipped game
falls through to `StandardAiDecision`. `Behaviours.json`'s DPS/SUPPORT/CURSE weight tables are
loaded and never used. The vanilla "AI profiles" system effectively does not run.

### 1.3 `StandardAiDecision` — uniformly random ability choice

1. `TryConsiderSecondaryAction`: reload if ammo empty; move via `BASIC_MOVE` only if the current
   tile is unusable/hazardous and a better tile exists (ordered by `useable && desirable && !hazardous`).
   This is the *entire* vanilla positioning logic — no tactical value, only "can I act / is the tile cursed."
2. Otherwise: `abilityAction = Random.GetRandomElementFromList(usableAbilities)` — **a uniformly
   random pick from the ability bag** (per-weapon ability list, `CharacterHelper.GetAbilityBag`).
   No damage estimation, no category preference, no kill awareness. Up to 5 retries if the pick is
   "BAD" (status already present on all targets, or taunt with <2 allies); then SKIP_TURN.
3. `ForceAiDecision(entity, gameRun, ability)` picks the target (below) and the focus spend.

### 1.4 Targeting — random unless a tendency fires, and tendencies rarely fire

`ForceAiDecision` → `GetPreferredTarget(origin, tendency, occupancy, targetArea, targetGroup, targetableTiles, combatState)`:

- Targetable tiles are **shuffled** (`Random.ShuffleList`) before anything else.
- `AIComponent.PriorityTargets` (a GUID queue — quest-scripted) wins if present; effectively unused in
  normal fights.
- The ability's `Tendency` (from `Abilities.json`) is consulted **only with probability
  `PRW × 1%`** (`Random.NextChance(PRW * 0.01)`), except 3 "strict" tendencies (MUSTHAVEPOISON,
  MUSTNOTHAVEHIVE, MUSTHAVELOWHEALTH) which always apply. CONFUSE forces PRW=0.
- Median enemy PRW is **50** → tendency targeting fires half the time *at best*; 825 of 2,072
  enemies have PRW < 40.
- **500 of ~670 damage abilities have `Tendency: NONE`** anyway — for those, targeting is: pick the
  tile whose area hits the most targets (`OrderByDescending(count)` over the shuffled list). For
  single-target abilities every tile ties at 1, so **the target is uniformly random**.
- `_orderTargetsByTendency` implements 24 orderings (LEASTHEALTH, MOSTARMOR, BACKROW, …) over
  `CharacterHelper.GetStat` / `GetHealth` — the building blocks for smart targeting exist and are
  reusable, the game just rarely invokes them.

### 1.5 Focus — spent randomly

In `ForceAiDecision`, for any `RequiresSkillRoll` ability:

```csharp
CachedFocus = IsFocusable ? Math.Min(slotRoll.Count, Random.NextInt(0, CharacterHelper.GetFocus(entity), pMaxInclusive: true)) : 0
```

A **uniform random 0..currentFocus** spend, regardless of target, ability, or situation. Given what
focus does mechanically (below), this throws away the AI's single biggest damage lever. (Mitigating
factor: only ~17% of enemies have any FOC stat; those have median 2, max 8.)

## 2. The combat math (what a smarter AI can exploit)

### 2.1 Slot rolls → damage

Every `RequiresSkillRoll` ability rolls `SkillRollData.Rolls` slots (enemy weapons: 2–5).
Per-slot success chance (`SlotRollHelper._getRollResultData`):

```
p = clamp(rollStat + focusBonus, 10, 100) / 100   per un-focused slot
focusBonus = +10, +5, +2, ... (10 × 0.5^i per focus point i)  [GetFocusedStatValue]
first FocusUsed slots are AUTO-SUCCESS
```

- **Enemies always roll their `ACC` stat** (`CharacterHelper.IgnoreEquipmentStats` returns true for
  every enemy → `GetCombatStat` substitutes ACC for the weapon's STR/AWR/INT roll stat), plus the
  ability's per-ability `ACC` adjust (0 to −20). Enemy ACC medians: 79 (lvl 0) → 84 (lvl 7).
- `RollResultData.Value = max(minScale, successCount / rollCount)`; **`minScale` is 0 for
  non-players** (`GetMinAttackScaleValue` requires `PlayerComponent`).
- Damage before mitigation = `lerp(minDmg, maxDmg, Value)` where
  `maxDmg = SkillRollData.MaxValue × ATK × damageMultiplier + flat` (`GetMinAndMaxDamageOfAbilityForCharacter`);
  enemy `minDmg = 0`. So **enemy damage is directly proportional to roll success ratio**.
- CRIT_FAIL (0 successes) on a damage ability with minDmg 0 = whiff, nothing happens.

### 2.2 Mitigation — flat subtraction, with a pierce exception

`InteractableHelper.CalculateFinalDamage`:

```
final = max(0, round(rolledDamage × powerRatio) − (DEF | RES))
```

- `powerRatio`: 1.0 center target, 0.5 SPLASH/SWIPE edge, ± `DamageAgainstRow` row modifier.
- The DEF/RES subtraction is **skipped entirely** when the action has `IsBlockable: false` AND the
  roll was `PERFECT` (all slots success). 102 of 584 damage actions are pierce-eligible; the other
  482 always eat DEF/RES.
- Crit: chance = `(CRT + FocusUsed×5 + 30 if target MARKED)%`, bonus = `+max(1, dmg × (0.15 + CRTD%))`.
  **Each focus point is also +5% crit.**

### 2.3 Dodge and death's door

`CombatHelper._applyActions`: target dodges with probability `EVD%`. Non-player targets can't dodge
PERFECT rolls and get a `DodgeCooldown` (can't dodge twice in a row); **players suffer neither
restriction** — a 30-EVD player dodges 30% of all incoming hits forever. Players additionally have
death saves (`CombatComponent.DeathSaves`) and party Life Pool.

### 2.4 Why late-game enemies tickle: the numbers

Enemy medians by config level (2,072 enemies in Characters.json):

| lvl | HP | ATK | ACC | PRW | DEF | E[dmg] pre-mit | vs DEF 10 | vs DEF 20 | vs DEF 30 |
|---|---|---|---|---|---|---|---|---|---|
| 0 | 22 | 9 | 79 | 50 | 2 | 7.4 | 0 | 0 | 0 |
| 3 | 51 | 23 | 80 | 50 | 5 | 18.0 | 8.0 | 0 | 0 |
| 5 | 74 | 32 | 83 | 50 | 7 | 25.9 | 15.9 | 5.9 | 0 |
| 7 | 97 | 43 | 84 | 50 | 9 | 33.4 | 23.4 | 13.4 | 3.4 |

(E[dmg] = median over enemy damage abilities of `MaxValue × ATK × p̄`, p̄ = per-slot chance.)

Root causes, ranked:

1. **Flat DEF/RES vs linear damage.** A late-game tank (tier-3 armor pieces at up to 13 DEF each +
   shield) reaches DEF 30–45; median level-7 enemy damage lands at **~3 HP per hit, ~0 through
   dodge/parry**. Player EVD builds (EVD cap 95) blank 40–60% of hits outright with no cooldown.
2. **Random ability selection** dilutes damage: a typical enemy weapon has 3 abilities of which one
   is a status poke (`MaxValue 0–0.5`); uniform choice wastes ~1/3 of turns on ~half-damage or
   zero-damage plays. Debuff/utility picks are chosen exactly as often as the nuke.
3. **Random targeting** spreads damage across the party; nobody actually dies, and player healing
   (unfocused by the AI) out-regenerates spread chip damage. The AI never focus-fires, never
   finishes a downed-adjacent target, never targets the healer.
4. **Random focus spend** wastes auto-success slots, +5%/pt crit, and pierce-enabling PERFECT rolls.
5. **No stat growth where it matters**: past config level, `GetExtraLevelStats` scales only
   HP (+15%/lvl) and ATK (+12%/lvl) and only when the adventure's `MaxEnemyLevel > 7`; ACC/PRW/CRT
   never grow. Player hit chance, DEF, EVD, and focus economy all keep growing.
6. **Difficulty barely touches enemies**: `GameDifficulties.json` MASTER = `EnemyStatMods { ACC: +3 }`;
   that is the entire enemy-side difficulty delta (APPRENTICE gives ACC −4 and buffs players).

Conclusion: the complaint decomposes ~50% decision quality (2/3/4 — WarBrain's scoring engine fixes
these with zero stat changes) and ~50% raw numbers (1/5/6 — needs the tunable scaling layer;
`EnemyStatMods` proves flat per-stat adds are a safe, native-shaped mechanism).

## 3. Multiplayer: AI is deterministic lockstep on ALL peers (not host-only)

Resolves `docs/MULTIPLAYER.md` open question #1 — **the strong prior was wrong.**

Evidence from `CombatPhase._engageActiveEntity` / `_performAiDecision`:

- The AI branch (`entity.Has<AIComponent>()`) has **no host/authority gate** — every peer that runs
  the combat phase executes `AIHelper.BehaviourAiDecision` locally for every enemy turn.
- Only *remote player* turns take the network path (`NetComponent.IsRemotePlayer` →
  `_tryPlayNextNetworkAction`); enemy decisions are **never broadcast**.
- Every stochastic step in the decision (`ShuffleList`, `GetRandomElementFromList`, `NextChance`,
  focus `NextInt`) consumes `GameRun.CombatState.Random` — the seeded, save-serialized `GameRandom`
  shared by all peers. Peers stay in sync because they all draw the same numbers in the same order;
  `NetworkHelper.CreateNewDesyncDetectionTaskFromGameState` hash-checks the result.

Consequences for WarBrain (SPEC §9 must flip to the `ALL_PEERS` branch):

1. **Every peer must run WarBrain with byte-identical data** (R1 parity hash is mandatory, not
   diagnostic). A modded host + vanilla client desyncs on the first enemy turn.
2. WarBrain must consume `CombatState.Random` for every stochastic choice (softmax sample,
   mistake roll) — *and must consume the same number of draws on every peer*, which it does
   automatically if data is identical.
3. Scoring arithmetic must be cross-machine deterministic: use `decimal` (like the game's own
   chance math) or integer math — **no `float`/`double`** in score computation.
4. A useful degenerate config exists: `Temperature = 0` + `MistakeChance = 0` consumes *zero* RNG
   draws in WarBrain's pick (argmax) — but vanilla would have consumed draws, so peers must
   uniformly run WarBrain or uniformly not. There is no partial-install topology.

## 4. Other SPEC §11 resolutions

- **Combat-start reset hook**: `CombatState.Create()` (static factory) is the cleanest signal —
  a postfix marks "new battle". `CombatPhase._initializeNextWave` fires per wave (don't reset there).
  Battle end: `CombatPhase._endCombatAsync`. All exist and are patchable.
- **`GameRunData` stable key**: `GameRunData` has no GUID field, but `OriginalVersion` +
  `ConfigName` + object identity suffice; per-run memory can key off the `GameRunData` instance
  (EOR pattern) via `GameRunData.Create` postfix. Signature verified:
  `Create(string pGameVersion, string pAdventureConfig, List<eExpansions>, eGameDifficulties, Dictionary<eGameDifficultyHandles,int>)`.
- **`ConfigsHelper.ReloadConfigs(ref Configs configs, string basePath)`** — verified, hot-reload viable.
- **Tendency reuse**: `_orderTargetsByTendency` is `private static` but trivially callable via
  reflection/AccessTools, or reimplemented — it's a pure switch of LINQ orderings over public helpers
  (`CharacterHelper.GetStat/GetHealth`, `InventoryHelper.GetCharacterGold`). Reimplementation is
  safe and avoids a fragile private call.
- **Damage prediction**: exact expected-damage is computable from public data:
  `GetMinAndMaxDamageOfAbilityForCharacter(entity, itemName, abilityName, dmgMult, flatMod)` +
  per-slot p from `GetCombatStat(..., "ACC" substitution)` + `CalculateFinalDamage` semantics
  (flat DEF/RES subtract + pierce rule). No dry-run of game code needed; WarBrain can compute
  E[damage | focus] in closed form (binomial expectation) — see the sandbox sim.
- **`CombatHelper.PerformAbility` signature** (for the memory postfix):
  `PerformAbility(Entity pOrigin, Entity pTarget, List<Entity> pParty, Thing pThing, CombatDecisionData pCombatDecision, RollResultData pRollData, Func<Entity,string,eGetStatEquippedFilters,int> pGetStat, Func<Entity,string,int> pGetTileStat, SkillContext pOriginSkillContext, bool pConsumeAction, Env pEnv, GameRandom pGameRandom)`.

## 5. Exploitable levers for WarBrain, ranked by measured impact

1. **Focus policy** (replace random `NextInt(0, focus)`): commit max focus for kill-secure or
   high-DEF targets (auto-success slots + 5%/pt crit + PERFECT-pierce on the 102 pierce actions);
   save focus on chip turns. Pure decision change, zero stat change.
2. **Ability selection by expected damage** (replace uniform pick): compute
   E[dmg] = Σ binomial(successes) × lerp − DEF per candidate; stop wasting turns on
   `MaxValue 0` status pokes when lethal is on the table.
3. **Target selection** (replace shuffle): squishiness-aware (per-target DEF/RES/EVD vs this
   ability's damage type — LEASTARMOR/LEASTEVASION orderings already exist), focus-fire the wounded,
   kill-secure below-threshold targets, healer priority via memory.
4. **Positioning**: vanilla only avoids hazard tiles; row-position value + AoE-spread reaction are
   green-field.
5. **Scaling layer** (companion to decisions): per-stat flat adds (native `EnemyStatMods` shape) +
   percentage ATK/HP/ACC multipliers by progression level, exposed as knobs. This is the only way
   to touch root causes 1/5/6 without gear rebalancing.

## 6. Decompiled sources

Full decompiled .cs files cached in the session scratchpad (`decomp/`); regenerate any time with
`ilspycmd -t <TypeName> FTK2.dll -o .`. Key types: AIHelper, CombatHelper, CombatPhase,
InteractableHelper, SlotRollHelper, CharacterHelper, CoreHelper, VenueHelper, CombatState,
AIComponent, CombatDecisionData, CombatAbilityConfig, GameDifficultyConfig, GameRunData.
