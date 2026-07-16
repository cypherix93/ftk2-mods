# The Armory catalog — design doc

56 hand-crafted items: 9 themed sets (41 items) + 13 standalone build-arounds, plus the 60-item
curated forge pack. Every synergy cites a **verified** claim id from
`docs/research/game-mechanics.md` (the DesignNote field on each item); nothing is anchored on
folklore. Rarity ladder uses the game's real tiers (COMMON→UNCOMMON→RARE→ARTIFACT); "legendary"
= ARTIFACT. All packs: 0 validator errors.

Sets synergize through *complementary mechanics*, not hidden bonuses (set-bonus engine = Armory
M2; items already carry set-identifiable id prefixes for it).

## The nine sets

### 1. Bramblelord — retaliation tank (`ARM_BRAMBLE_*`, 5 items, COMMON→ARTIFACT)
Loop: stack THRN, force melee enemies to hit you, win by attrition. THRN fires flat, unreduced,
even on **blocked** hits, and only melee triggers it [C-STAT-11, C-DEF-04]; SKILL_STEADFAST turns
hits into blocks that *still* proc thorns; SKILL_GUARD makes your backline untargetable so
enemies must come to you [C-STATUS-11]. Keystone: **Bramblelord Aegis** (ARTIFACT shield,
THRN 5 + STEADFAST).

### 2. Tidebinder — focus economy (`ARM_TIDE_*`, 5, COMMON→ARTIFACT)
Loop: focus points buy guaranteed roll slots and +5% crit each [C-STAT-08, C-FOC-04]; FOC-gain
abilities only pay out on PERFECT [C-STAT-07] — so the set stacks FOC caps, SKILL_REFOCUS
(overworld regen), SKILL_HARDWORK (free swings at 0 focus) and SKILL_DISCIPLINE (party focus on
kills) into a self-refueling engine. Keystone: **Tidebinder Orb** (1-roll FOC-gain ability — one
pinned point = guaranteed PERFECT = guaranteed focus back).

### 3. Gravedigger — luck gambler (`ARM_GRAVE_*`, 5, COMMON→ARTIFACT)
Loop: the skill-proc luck bonus is `(LCK−50) × mult` — **negative below 50** [C-STAT-15] — and
LCK is the most common overworld TestStat [O-ENC-11]. The set exists to cross that 50 threshold
hard, then spend it: SKILL_FORTUNE (the game's only full re-roll), SKILL_NICEDAY,
SKILL_FINDTREASURE. Keystone: **Gravedigger's Crown** (PLCK 10 — party-wide luck aura
[C-STAT-04]).

### 4. Red Season — bleed/crit hunter (`ARM_REDSEASON_*`, 5, UNCOMMON→ARTIFACT)
Loop: on-hit statuses require PERFECT rolls [C-STATUS-10]; SKILL_CALLEDSHOT *forces* PERFECT on
ranged attacks [C-PROC-12] — making bleed application reliable; BLEED ticks pierce DEF/RES
entirely [C-STATUS-07]; CRT/CRTD/MARKED finish the wounded [C-CRIT-01/02]. DAM_BEAST rounds out
the "hunter" identity [C-ATK-07].

### 5. Waywatcher — overworld explorer (`ARM_WAYWATCH_*`, 5, UNCOMMON→RARE)
The set that plays the other half of the game: AWR avoids ambushes [O-STAT-03], MOV adds
movement dice and SPD lands them [O-STAT-01], XPM compounds XP [O-LOOT-16], and
SKILL_ELITEAMBUSH/ELITESNEAK simply delete the ambush/sneak minigames [O-STAT-04]. Deliberately
capped at RARE — it trades combat power for map tempo.

### 6. Ashen Covenant — cursed bargains (`ARM_ASHEN_*`, 5, RARE→ARTIFACT)
Every piece is an over-curve spike with a real, claim-backed drawback: negative LCK poisons all
proc rolls [C-STAT-15], negative EVD/SPD/VIT costs dodge/initiative/max-HP [C-STAT-20, C-TGT-04,
C-STAT-05]. Keystone: **Covenant Chalice** (+1 primary action per turn [C-STAT-18] — priced in
VIT). The forge's `cursed_bargains` profile is this set's mass-produced cousin.

### 7. Hollow Court — summoner regalia (`ARM_HOLLOW_*`, 4, UNCOMMON→ARTIFACT)
Loop: summons need PERFECT + a free tile [C-SMN-01]; the set makes PERFECTs cheap (low-Rolls
summon abilities, FOC batteries, SKILL_DISCIPLINE). Uses the two live summon wirings: the DOLL
consumable pattern (no roll at all) and the roll-gated `SUMMON_*_ATTACK` book pattern.

### 8. Stormcaller — shock control (`ARM_STORM_*`, 4, RARE→ARTIFACT)
Loop: SHOCK deletes an enemy roll slot [C-STATUS-20]; imbues re-apply the element on every hit
[C-STATUS-19]; the 1-roll handbow makes every success a PERFECT so the rider always lands
[C-ATK-03, C-STATUS-10]; DAM_LIGHTNING 25 keystone trinket [C-ATK-07].

### 9. Vanguard's Oath — initiative & slot-support (`ARM_VANGUARD_*`, 5, RARE→ARTIFACT)
Loop: act first (SPD initiative + SKILL_EAGER [C-TGT-04]), dodge what answers (EVD binary
negation [C-STAT-20], ENTANGLE immunity because ENTANGLE disables dodge [C-STATUS-23]), and bend
the party's dice (SKILL_ENCOURAGE/DISTRACT flip roll slots [C-PROC-12]; PSPD/PEVD/PDEF party
stats [C-STAT-04]). Keystone: **Vanguard Rapier** (PA +1 on the live RAPIER|ARTIFACT curve).

## The thirteen standalones (`ARM_UNIQ_*`, UNIQUE-tagged: once per run [O-LOOT-08])

| Item | Slot | Identity | Core claims |
|---|---|---|---|
| Kingsbane | trinket | +25% dmg vs bosses, executioner crits | C-ATK-07, C-CRIT-01 |
| Metronome | trinket | +5% to EVERY skill proc (SKL) | C-PROC-02 |
| Mentor's Journal | trinket | +15% XP carry item | O-LOOT-16 |
| Pilgrim's Soles | boots | movement engine, rain-proof | O-STAT-01/02 |
| Last Argument | 2H blunt | ATK 62, SPD −40: always speaks last | C-PROC-12, C-TGT-04 |
| Mirrorwall | shield | parry wall with thorns | C-STAT-11, C-DEF-04 |
| Pale Knuckles | unarmed | VIT-stack fist build (+VIT/10 ATK) | C-STAT-05/06 |
| Court Composer | lute | TAL support weapon, party XP | C-STAT-14, C-PROC-12 |
| Actuary's Quill | wand | damage-floor reliability (MinValue 0.85) | C-ATK-05/06 |
| Glass Appetite | katana | HEAVYHANDED/MEND break-tension crits | C-PROC-12, C-ATK-09 |
| Charger's Crest | helmet | grants charge attacks to anyone | C-CHG-01..03 |
| Dawn Tithe | body | HRG attrition-run regen | C-STAT-10 |
| Second Opinion | trinket | LCK 20 + FORTUNE: the 50-threshold pivot | C-STAT-15, O-ENC-05 |

## Coverage & review pass (all five packs, 116 items)

- No duplicate ids or names across packs (checked cross-pack; the validator only sees one pack
  at a time).
- Rarities: COMMON 7 / UNCOMMON 14 / RARE 52 / ARTIFACT 43 — deliberately top-heavy (the forge
  curation favored ARTIFACT interestingness; base game keeps ARTIFACT drops at a 5% bucket
  [O-LOOT-06], so density in the pool is fine — flag for morning review all the same).
- Tier bands all within the real 0..3 runtime range [O-LOOT-01]; 26 item classes covered.
- Known deliberate deviations: Hollow Courtier is a live-pattern DOLL consumable (not equippable);
  Pale Knuckles ships without a custom icon (no UNARMED prerender exists); Last Argument's
  SPD −40 mirrors the live Walloper's precedent.

## Authoring notes / gotchas for the next batch

- ClassAbilityTemplates for WAND/ORB_2H/STAFF_2H lean enemy-flavored; check live *player* items
  of the class and prefer their ability wiring.
- Weapon prerenders for material-family weapons are `_WEP_<MATERIAL>`-suffixed.
- Live `Passives` may be STATUS_IMMUNITY_* ids, not just SKILL_*.
- KATANA is two-handed (MAIN_HAND+OFF_HAND) despite the 1H-sounding name.
- Negative stats skip the budget ceiling by design (curses); the validator was fixed mid-run for
  this — see commit history.
