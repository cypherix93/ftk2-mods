# FTK2 game mechanics — ground truth (decompile-verified)

Compiled 2026-07-16 from the ilspycmd decompile of `FTK2.dll` (tools/out/decompile/FTK2/,
gitignored — regenerate per the plan's Task 2) cross-checked against the live game JSON.
**Every claim is tagged** `[verified: decompile]`, `[verified: data]`, or `[inferred]`.
Item/synergy designs (FTK2.Armory CATALOG, itemforge affix pools) may only anchor on
verified claims, cited by claim id.

This doc covers *meaning*; schemas live in `data-schemas.md`; loaders in `eor-loader-notes.md`;
observed enum/value inventories in `vocab-summary.md`.

---

## Part I — Combat

### Stat pipeline

- **[C-STAT-01]** `CharacterHelper.GetStat` aggregates: config base stat (+level scaling) +
  equipped Things/Traits/Passives + `BaseStatModifiers` + party stats (players) + status-effect
  stat mods + dungeon modifiers, then clamps to min/max. (CharacterHelper.cs:422–562)
  [verified: decompile]
- **[C-STAT-02]** Rollable "base stats" are exactly `AWR TAL LCK INT SPD VIT STR`.
  (CharacterHelper.cs:100) [verified: decompile]
- **[C-STAT-03]** Caps: FOC max 9; AWR/TAL/LCK/INT/SPD/VIT/STR/**EVD max 95**; ATK/DEF/RES
  uncapped upward. Mins: HP 1, FOC/RES/DEF/EVD 0, base stats 1.
  (CharacterHelper.TryGetMaxStatValue/TryGetMinStatValue) [verified: decompile]
- **[C-STAT-04]** Party-wide stats = P-prefixed equipment keys
  (`PDEF PRES PEVD PFOC PSTR PVIT PINT PAWR PTAL PSPD PLCK PGLD`), applied to **every player**
  via `GetPartyStat`. An equipped `PGLD` trinket buffs the whole party's gold find.
  (CharacterHelper.cs:102–116, 654–717) [verified: decompile]
- **[C-STAT-05]** Max HP = HP stat; players add `round(level × VIT × 0.13)`.
  (GetMaxHealth) [verified: decompile]
- **[C-STAT-06]** VIT: max-HP contributor + rollable stat + unarmed bonus (`+VIT/10` ATK, players,
  item name contains "UNARMED"). (GetUnarmedAttackStat) [verified: decompile]
- **[C-STAT-07]** FOC stat = max focus (cap 9). `CHANGE_STAT FOC` grants focus **only on a
  PERFECT roll**. (InteractableHelper.cs:881–892) [verified: decompile]
- **[C-STAT-08]** Focus is NOT a re-roll: each pinned focus point makes one roll slot
  **auto-SUCCEED** (`slots with index < focusUsed` forced SUCCESS).
  (CombatPhase._onTryFocus; SlotRollHelper.cs:109) [verified: decompile]
- **[C-STAT-09]** Pinned focus also buffs the *remaining* slots: `+10, +5, +2, +1…` (10·0.5^i) to
  the roll stat; effective roll stat always clamped [10, 100] — nothing is ever a guaranteed
  slot-success or slot-failure. (GetFocusedStatValue) [verified: decompile]
- **[C-STAT-10]** HRG = flat **out-of-combat** end-of-turn regen (dungeon: min(missing, HRG);
  overworld: unused move points + HRG; leftover CachedFocus converts to healing). No combat use.
  (AdventureHelper.cs:820–857) [verified: decompile]
- **[C-STAT-11]** **THRN (thorns)**: when an HP-damage action from a **non-ranged** ability
  resolves against a target, the attacker takes flat damage = target's THRN — including on
  BLOCKED/STEADFAST/PARRY outcomes, NOT reduced by DEF/RES, can kill the attacker. Ranged attacks
  are immune. (InteractableHelper.cs:1069–1097) [verified: decompile]
- **[C-STAT-12]** PRW is AI-only: chance the enemy applies its `Tendency` (`PRW × 0.01`) and a
  multiplier on enemy skill procs. Useless on player items. (AIHelper.cs:588–615;
  SkillHelper.cs:327–335) [verified: decompile]
- **[C-STAT-13]** AWR: avoid overworld AMBUSH property with chance `AWR × 0.01`; TestStat of the
  camp-ambush minigame; ranged-weapon roll stat (501 live abilities). (AdventureHelper.cs:2850)
  [verified: decompile]
- **[C-STAT-14]** TAL: pure roll stat (457 live abilities, incl. LUTE_2H) + encounter TestStat;
  no hidden combat formula. [verified: decompile]
- **[C-STAT-15]** **LCK**: roll stat (45 abilities; NOTE: ability rolls on LCK do **not** get the
  ACC bonus) and **player skill-proc luck bonus** = `ceil((LCK − 50) × multiplier)%` — negative
  below 50 LCK. (CharacterHelper.cs:787–801; SkillHelper.cs:274–288) [verified: decompile]
- **[C-STAT-17]** SPD: initiative = `min(100, SPD + rand 0..10)`; sneak-minigame TestStat; roll
  stat. (CombatHelper.SetInitiative) [verified: decompile]
- **[C-STAT-19]** ACC: (a) character ACC adds to any base-stat ability roll except LCK; (b) the
  per-ability JSON `ACC` (e.g. −20 on heavy attacks) adjusts that ability's roll only; (c) POISON
  gives non-players −10 ACC. (CharacterHelper.cs:787–801; SlotRollHelper.cs:67–82)
  [verified: decompile]
- **[C-STAT-20]** EVD = dodge chance `EVD × 0.01` (cap 95). No dodge while STUN/DAZE/PETRIFY/
  GRAB/ENTANGLE; **non-players cannot dodge PERFECT rolls** and get a dodge cooldown (every other
  hit). Dodge = total negation. (GetChanceToDodge; CombatHelper.cs:1523–1554) [verified: decompile]
- **[C-STAT-18]** `CHANGE_STAT PA/SA` adds primary/secondary actions directly — action-economy
  buffs are data-expressible. (InteractableHelper.cs:901–916) [verified: decompile]

### Attack resolution

- **[C-ATK-01]** Item ability params `{MinValue MaxValue ACC Stat Rolls Ammo}` = `SkillRollData`.
  Live roll-stat usage: STR 570, INT 519, AWR 501, TAL 457, VIT 430, SPD 160, LCK 45 abilities.
  [verified: decompile + data]
- **[C-ATK-02]** Slot roll: `Rolls` independent slots; per-slot success chance =
  `(rollStat + abilityACC + charACC + focus bonus) × 0.01`; statuses mutate slots (SHOCK forces
  one failure unless focused; DISTRACT flips a success; ENCOURAGE flips a failure).
  (SlotRollHelper.cs:80–133) [verified: decompile]
- **[C-ATK-03]** Grading: 0 successes = CRIT_FAIL, all = PERFECT, else SUCCESS.
  `Value = successes/Rolls`. [verified: decompile]
- **[C-ATK-04]** Attack stat = ATK + player level (players) + PHY (physical) or MAG (magical)
  + unarmed VIT/10 + channeling bonus. PHY/MAG are flat ATK additives per damage school.
  (GetAttackStat) [verified: decompile]
- **[C-ATK-05/06]** Damage range: max = `MaxValue × atk × mult`; min = `MinValue × rollStat ×
  0.01 × atk × mult` (**players only**; enemy min = 0). Rolled damage = `Lerp(min, max, Value)`.
  (GetMinAndMaxDamageOfAbilityForCharacter; InteractableHelper.cs:641–654) [verified: decompile]
- **[C-ATK-07]** Damage multiplier per target = 1.0 + 0.25 (target has ICE) + charge modifier +
  Σ(attacker's `DAM_<targetTag>` × 0.01). **`DAM_*` stats = +1% damage per point vs enemies
  carrying that tag** (UNDEAD, BEAST, BOSS, SCOURGE, FIRE, …). (GetTargetCharacterDamageMultiplier)
  [verified: decompile]
- **[C-ATK-09]** A CRIT_FAIL player attack with a real weapon **breaks it** (MaxCharges = number
  of crit-fails it survives; SKILL_MEND can restore). (CombatHelper.cs:1252–1279)
  [verified: decompile]
- **[C-ATK-10]** PERFECT-roll exclusive effects: crits (C-CRIT-01), armor-pierce on
  IsBlockable:false (C-DEF-03), FOC gain, charge inflict statuses, **and ADD_STATUS of any kind
  (C-STATUS-10)**. [verified: decompile]
- **[C-ATK-11]** Off-center SPLASH/SWIPE targets take 0.5×; SPLASH_WAVE other-row 0.5×.
  [verified: decompile]
- **[C-ATK-14]** `IsMajorAction:true` consumes a Primary action; else Secondary.
  [verified: decompile]

### Crit

- **[C-CRIT-01]** Crits roll **only on PERFECT** vs ENEMY HP actions. Chance% =
  `CRT + 5 × focusUsed + 30 if target MARKED`. **No base crit chance** — CRT 0, no focus, no
  mark = never crits. (CombatHelper.cs:1351–1359) [verified: decompile]
- **[C-CRIT-02]** Crit bonus damage = `max(1, round(damage × (0.15 + CRTD × 0.01)))` — CRTD is
  crit-damage%, CRT is crit-chance%. (CombatHelper.cs:1227–1232) [verified: decompile]

### Defense

- **[C-DEF-01]** Reduction is **flat subtraction, floor 0, uncapped**: DEF vs PHYSICAL, RES vs
  MAGICAL, **nothing** vs all other damage types (FIRE/BLEED/POISON/… are unmitigatable).
  (CalculateFinalDamage) [verified: decompile]
- **[C-DEF-03]** `IsBlockable:false` = pierce **only on PERFECT roll** (else still reduced). 99
  live abilities. [verified: decompile]
- **[C-DEF-04]** Post-reduction 0 damage = "BLOCKED", no HP change — but **thorns still fire**
  (C-STAT-11). [verified: decompile]
- **[C-DEF-06]** REFLECT status: reflects `max(1, 50%)` of one hit, then consumed. Distinct from
  THRN (persistent stat, blocked-hit-proof, melee-only). [verified: decompile]
- **[C-DEF-05]** Pipeline order: whiff → dodge(EVD) → MARKED consume → roll dmg → crit → ×area
  ratio → −DEF/RES → PROTECT/STEADFAST/PARRY/REDIRECT reactions → apply → REFLECT/THRN.
  [verified: decompile]

### Status effects

- **[C-STATUS-01]** Statuses keyed by config name; re-apply = refresh duration, never stack.
  Same-family higher tier replaces lower. Two same-name statuses cannot coexist.
  (InteractableHelper.ApplyStatus) [verified: decompile]
- **[C-STATUS-03]** Conflicts: FIRE↔ICE↔WATER, IMBUE_* mutual, IMBUE_X↔X;
  INFINITE_FIRE > FIRE, INFINITE_PROTECT > PROTECT. [verified: decompile]
- **[C-STATUS-04/05]** In-combat ticks (TickCombat) run on the victim's turn: `TICK_<name>`
  ability at start of turn every `TickFrequency` turns; Duration−1/turn; −1 = permanent.
  BLEED_00: 5 dmg every 2nd turn for 4; FIRE_00: 3/turn; REGEN_00: heal 5/turn.
  [verified: decompile + data]
- **[C-STATUS-07]** **Tick damage is flat, tiered by status name, `IsBlockable:false` with a
  PERFECT default roll ⇒ never reduced by DEF/RES.** DoTs are true damage. [verified: decompile]
- **[C-STATUS-08]** Applied tier scales with source: enemies by level (bosses tier 7), items by
  `itemLevel×2`; MARKED/CHARGE/REGEN/IMBUE_*/VIGOR/HASTE are tierless. [verified: decompile]
- **[C-STATUS-09]** POISON damage ticks are **overworld-only**; in combat POISON = −10 to six
  stats (and −10 ACC for non-players, C-STAT-19), plus proc-block (C-PROC-05).
  [verified: decompile + data]
- **[C-STATUS-10]** **ADD_STATUS lands only on PERFECT rolls.** Status-proc items are therefore
  focus/accuracy-hungry: fewer Rolls or focus auto-successes make procs reliable.
  (CombatHelper.ApplyAction) [verified: decompile]
- **[C-STATUS-11]** GUARD = tile aura behind the skill-holder (SKILL_GUARD AURA_AREA BEHIND);
  guarded tiles are **untargetable by enemies**. [verified: decompile]
- **[C-STATUS-12]** TAUNT collapses enemy targetable tiles to taunters' tiles.
  [verified: decompile]
- **[C-STATUS-13]** PROTECT zeroes one hit then clears; INFINITE_PROTECT persists; STAR_SHIELD =
  party-synced counter. All three also eat debuff/curse applications. [verified: decompile]
- **[C-STATUS-16]** DEATHMARK: after 4 turns deals **100% of victim's max HP**, unblockable;
  bosses immune. [verified: decompile]
- **[C-STATUS-17]** DEATHSAVE: survive lethal at 1 HP; generic "DEATHSAVE" action also inflicts a
  random CURSE (drawback built in). [verified: decompile]
- **[C-STATUS-18]** "CURSE" = one of 7 permanent −25-stat curses; REMOVE_STATUS "CURSE" strips
  all. [verified: decompile]
- **[C-STATUS-19]** IMBUE_(FIRE/ICE/SHOCK/WATER) on the **attacker**: every damaging hit also
  applies the element status to the target; mutually exclusive; grants self-immunity to own
  element. [verified: decompile]
- **[C-STATUS-20]** MARKED: +30pp crit chance to attackers, consumed per hit. SHOCK: one roll
  slot auto-fails unless focus pinned. [verified: decompile]
- **[C-STATUS-21]** Tile statuses (TileSync) infect occupants and decrement per **round**.
  [verified: decompile]
- **[C-STATUS-23]** DAZE zeroes secondary actions; ENTANGLE blocks melee/move/flee;
  STUN/DAZE/PETRIFY/GRAB/ENTANGLE disable dodge. [verified: decompile]

### Passive skills (SKILL_*) & procs

- **[C-PROC-01]** All proc rolls ride the seed-synced `GameRandom` (MP-deterministic).
  [verified: decompile]
- **[C-PROC-02]** **`SKL` stat adds +1%/point to EVERY skill proc roll** (hidden global
  proc-chance stat, item-equippable). (SkillHelper.RollSkillChance) [verified: decompile]
- **[C-PROC-03]** Player proc chance = `ceil(stat × multiplier)%` (+LCK bonus per C-STAT-15).
  [verified: decompile]
- **[C-PROC-05]** Proc blockers: DAZE/STUN/POISON/GRAB, splash-only hits (CENTER_TARGET_ONLY),
  full cache, cooldown, and **party proc caps per turn/combat** (default 1/turn!).
  [verified: decompile]
- **[C-PROC-07]** `IS_MANUAL_ABILITY` procs accumulate into a clickable cached ability
  (MAX_SKILL_CACHE), auto-fire otherwise at EVENT_PROC timing. [verified: decompile]
- **[C-PROC-08]** `PROC_EQUIPMENT` gates procs by equipped-weapon tags (CALLEDSHOT: BOW/HANDBOW/
  GUN; PARRY: BLADE; JUSTICE: 2H AXE/BLADE/BLUNT/POLEARM/LANCE). [verified: decompile]
- **[C-PROC-11]** **Skill behavior is pure C# keyed on the 78-member `eSkills` enum** — JSON only
  tunes numbers. Items may grant any existing `SKILL_*` via `Equippable.Passives`; inventing new
  SKILL_ ids does nothing. [verified: decompile]
- **[C-PROC-12]** Verified item-passive-ready skills (effects at SkillHelper.TryProc*):
  CALLEDSHOT (ranged auto-PERFECT+crit, AWR-scaled), JUSTICE (2H melee auto-crit + wave, STR),
  HARDWORK (0-focus attack refunds primary action), STEADFAST (block to 0, shield, VIT/DEF/RES,
  +5% while taunting), PARRY (halve+reflect, blade), REDIRECT (split ranged damage, 2H),
  EVASIVE (dodge all non-PERFECT), GUARD, DECOY (adjacent-tile redirect), DISTRACT (flip enemy
  slot, TAL), ENCOURAGE (flip ally slot, TAL, radius 2), MEND (restore broken weapon, VIT),
  FORTUNE (full slot re-roll, AWR), HEAVYHANDED (+20% rolls, crit-fail always breaks), EAGER
  (act first), VOIDWALK (manual teleport cache, SPD), IRONBELLY (consumable-debuff immunity),
  DISCIPLINE (party FOC on kill/crit). [verified: decompile]

### Ability bags, charges, ammo

- **[C-BAG-03]** `AbilityBag` (repeats = weight) is the **enemy-AI draw pool**, drawn without
  replacement, refilled when empty. **Players ignore AbilityBag — they see all `Abilities`
  keys.** Player-facing on-use items = `Interactable.Abilities` entries. [verified: decompile]
- **[C-BAG-05]** `AbilityFillBag` is juggle-weapon-only. [verified: decompile]
- **[C-BAG-07]** `MaxCharges` = crit-fails survived before the weapon breaks (not use count).
  [verified: decompile]
- **[C-BAG-08]** Ammo: ability-side `Ammo` cost vs weapon `CustomData[AMMO]`, reload via
  BASIC_RELOAD up to `ThingConfig.Ammo`. [verified: decompile]

### Focus economy

- **[C-FOC-01]** Pool 0..FOC(≤9); spend commits CachedFocus + RequiresFocus; CONCENTRATION
  refunds half the pinned focus. [verified: decompile]
- **[C-FOC-04]** Each focus point: +1 auto-success slot, +remaining-slot chance, **+5% crit
  chance**. [verified: decompile]
- **[C-FOC-05]** Gain sources: CHANGE_STAT FOC (PERFECT only), DRAIN_FOCUS tag (50% of damage
  dealt as FOC), SKILL_DISCIPLINE, SKILL_REFOCUS, level-up +2, RESTORE consumables; RATTLED
  drains 1/turn. [verified: decompile]

### Targeting & turn order

- **[C-TGT-01]** **`CharacterConfig.Threat` is not read by combat targeting at all** (encounter
  budgeting only). Aggro levers that actually work: TAUNT, GUARD, DECOY, EVD.
  [verified: decompile]
- **[C-TGT-02]** Enemy targeting: `Tendency` applied with probability PRW×0.01, else "hit the
  most targets" tile. Strict tendencies (MUSTHAVEPOISON etc.) always apply or veto.
  [verified: decompile]
- **[C-TGT-04]** Initiative `min(100, SPD + rand0..10)` once per combat; RUSH/INTERRUPT statuses
  move entities in the queue. [verified: decompile]

### Summons & charge attacks

- **[C-SMN-01]** ADD_CHARACTER: PERFECT roll + free tile required. Types SPECIFIC / RANDOM
  (tag-query + rarity-weighted) / PLAYTHING / AS_FOLLOWER. Summon level = summoner's.
  [verified: decompile]
- **[C-SMN-02/03]** Summons act same round, are removed at combat end (AS_FOLLOWER/COMPANION
  persist as followers); enemy summoners once per combat; players uncapped while tiles free.
  [verified: decompile]
- **[C-CHG-01..03]** Charge attacks need SKILL_CHARGEATTACK + `Chargeable` ability + ChargeConfig;
  charging turns lose all actions and self-apply the charging status; payoff = damage multiplier
  (+50%..+175%), possible area upgrade (SINGLE→SPLASH→AOE), and on-PERFECT inflict status.
  [verified: decompile]

### Dead/negative findings (do not design against these)

- `TickExpire` is parsed but never read. [verified: decompile]
- No percent armor, no armor cap, no base crit chance, no in-combat HRG, no combat AWR/TAL
  formula, no per-round focus regen. [verified: decompile]
- `Threat` does nothing for combat aggro (see C-TGT-01). [verified: decompile]

---

## Part II — Overworld

### The universal slot-roll engine

- **[O-ENC-01]** Every overworld check (encounters, traps, movement, combat rolls) is N
  independent Bernoulli slots; per-slot success chance = `(stat + adjust) × 0.01`. The stat IS
  the percent chance. (SlotRollHelper._getRollResultData) [verified: decompile]
- **[O-ENC-02/03]** For encounters/traps the chance is clamped **[10%, 100%]**; each committed
  focus point = 1 auto-success slot + diminishing bonus (+10/+5/+2/+1) to remaining slots.
  [verified: decompile]
- **[O-ENC-05]** The only re-roll is SKILL_FORTUNE (whole-roll re-roll, once).
  [verified: decompile]

### Skill encounters & traps

- **[O-ENC-07/08]** Encounter rolls = `Results.Length − 1` slots; outcome =
  `Results[rolls − successes]` (index 0 = perfect). `SuccessThreshold` only gates XP/completion
  flags, not which outcome fires. (EncounterPhase.cs:1181–1212) [verified: decompile]
- **[O-ENC-09]** `RollModifier` × attempts adjusts the stat per retry (pity/anti-pity).
  [verified: decompile]
- **[O-ENC-10/11]** `TestStat` live distribution (172 encounters): **LCK 30**, NONE 27, TAL 22,
  STR 21, AWR 20, VIT 17, INT 15, RND 12 (uniform among STR/VIT/INT/AWR/TAL/SPD), SPD 8.
  Overworld checks are where LCK/TAL/AWR items pay off. [verified: data]
- **[O-ENC-13]** Encounter/trap `SuccessXP` goes to the **acting character only**.
  [verified: decompile]
- **[O-TRAP-04]** Trap stats: disarm = STR ×27 / TAL ×8; run = VIT/INT/SPD ×2, AWR/LCK ×1.
  [verified: data]

### Loot resolution (the #1 fact set for making items drop)

- **[O-LOOT-01]** Runtime item tier = `clamp(difficultyLevel / 2, 0, 3)` — **tiers are 0..3
  only**. Catalog `MinTier`/`MaxTier` must live in 0..3 (−1 disables the gate).
  (ProgressionHelper.GetTierFromLevel) [verified: decompile]
- **[O-LOOT-02]** `MinTier`/`MaxTier` hard-gate every tag-query pool. [verified: decompile]
- **[O-LOOT-03]** Tag query syntax: list = OR; `+` = AND within an expression (e.g.
  `"BANDIT+BLADE"`). [verified: decompile]
- **[O-LOOT-04]** Filtered out of ALL generic pools: `Value == 0`, `Hidden`, rarity outside
  {COMMON, UNCOMMON, RARE, ARTIFACT} (weight 0), blacklisted, equippables with no prefab.
  **A shipped item needs a nonzero Value and a real rarity to ever drop.** [verified: decompile]
- **[O-LOOT-05]** `DROPPABLE` tag required for loot drops; **markets ignore DROPPABLE** (they
  filter by market tag instead). Explicit-id queries bypass it. [verified: decompile]
- **[O-LOOT-06]** Drop pick = rarity bucket first (**COMMON 50, UNCOMMON 30, RARE 15,
  ARTIFACT 5**), then class group (ARMOR 37.5% / WEAPON 32.5% / ITEM 30%), then uniform class,
  then uniform item. Adding many items to one class dilutes each one's odds but not other
  classes'. (LootDropHelper._getDropItem) [verified: decompile]
- **[O-LOOT-07]** Smart loot: 25% of weapon drops favor living party members' class preferences
  (SmartLoots.json). [verified: decompile]
- **[O-LOOT-08]** 30-item loot-cooldown ring buffer suppresses repeats; `UNIQUE`-tagged items
  drop once per run. [verified: decompile]
- **[O-LOOT-11]** Enemy loot = `CharacterConfig.LootID` → LootDrops table; enemy level parsed
  from config-name suffix; tier from level. [verified: decompile]
- **[O-LOOT-12]** Gold/XP scale = `base(scale) × 1.5^level`. [verified: decompile]
- **[O-LOOT-13]** Combat XP is party-split; encounter XP individual; quest PARTY_XP full to each.
  [verified: decompile]
- **[O-LOOT-16]** **`XPM` stat = per-character ±% XP on every gain** (min ±1).
  (ProgressionHelper.EntityGainXP) [verified: decompile]
- **[O-LOOT-17]** Material families roll a better material with chance 40% (tiers 0–2) / 80%
  (tier 3), rarity-weighted 60/25/10/5. [verified: decompile]
- **[O-LOOT-18]** Quest `RewardsTagQuery` → tag pool at current tier → rarity-weighted pick
  (60/25/10/5). [verified: decompile]

### Markets

- **[O-MKT-01]** Market types in code/data: TOWN, LANDPORT, SEAPORT, DUNGEON, NIGHT (configs
  TOWN_TYPICAL, …, NIGHT_MARKET_00..06). **No BLACK_MARKET / RELIC_BROKER exists** (EOR's tag
  strip list mentions them defensively; base game has none). [verified: decompile + data]
- **[O-MKT-03]** Stock = per-category quantity ranges from Markets.json, query =
  `BASEQUERY+CATEGORY` at current tier, rarity-weighted, no DROPPABLE requirement,
  UNIQUE excluded unless AllowUnique. [verified: decompile]
- **[O-MKT-05]** Buy price: consumables inflate `Value × (1+inflation)^tier`; **equipment sells
  at flat config Value**. [verified: decompile]
- **[O-MKT-07]** Sell price = 20% of value (fish 35% at seaports). [verified: decompile]
- **[O-MKT-08]** Markets refresh at current tier as the run progresses — MaxTier keeps early
  items out of late shops. [verified: decompile]

### Overworld stats

- **[O-STAT-01]** Movement: golden slots (biome/road/HASTE) + `max(1, biomeSlots + MOV)` rolled
  slots at SPD% each (no clamp). **MOV adds movement dice; SPD makes them land.**
  [verified: decompile]
- **[O-STAT-02]** Rain converts successes to failures (SKILL_STEADYSTRIDE immune); SKILL_NICEDAY
  (LCK-scaled) adds golden slots. [verified: decompile]
- **[O-STAT-03]** AWR × 0.01 = chance to avoid roaming-enemy AMBUSH. [verified: decompile]
- **[O-STAT-04]** Ambush-a-camp minigame: AWR, 3–4 slots, ALL must succeed; Sneak: SPD, 2–3
  slots, all. [verified: decompile]
- **[O-STAT-06]** LCK feeds encounter checks (top TestStat) + player skill-proc luck bonus
  (`(LCK−50) × mult`%, negative below 50) incl. SKILL_NICEDAY and SKILL_FIND*; LCK does **not**
  enter loot-table or rarity rolls. [verified: decompile]

### XP / gold / chaos

- **[O-XP-02]** XP and gold are literal inventory Things (`XP`, `CURRENCY_ADVENTURE`).
  [verified: decompile]
- **[O-CHAOS-02]** Chaos modifiers (5 total): CHAOS_HEXES, ENEMY_HEALTH (× enemy max HP),
  CHAOS_BEAST, CHAOS_WEATHER, END_TURN_CHAOS_DAMAGE. [verified: decompile]

### Unverified-semantics stats (schema-safe, but do NOT anchor synergies on them)

- `GLD` / `PGLD` (gold-find?) and `FIND` (item-find?) appear on live items and in the stats enum,
  and render as percent stats in UI, but their exact runtime formulas were not traced this pass.
  [inferred] — safe to include as flavor stats at live-data magnitudes; not as a set's core loop.
- `LOP`, `LBM`, `WBM`, `SKL`-adjacent UI stats: untraced. `SKL` itself IS verified (C-PROC-02).
- `LootData.TierModifier` appears dead in the main drop path (decompiler caveat noted).

### Cross-checks performed (plan Task 3 step 3 / Task 4)

Status tick numbers (TICK_BLEED_00/FIRE_00/REGEN_00) verified against Abilities.json; POISON
tick flags against StatusEffects.json; TestStat distribution enumerated from SkillEncounters.json;
trap stats from Traps.json; market categories from Markets.json; LootDrops "EXTRA" table from
LootDrops.json. Decompile and data agree in all six cases.

