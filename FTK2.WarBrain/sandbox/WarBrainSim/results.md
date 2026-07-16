# WarBrain sandbox results

Battles per cell: 2000. Player side: scripted experienced party (focus-fire, kill-secure, proactive heals).
Metrics are means per battle. `EDmg` = enemy damage dealt to players (post-mitigation HP), `Downs` = players reduced to 0,
`Wipe%` = full party kill, `Waste%` = enemy turns that changed nothing.

## 1. Vanilla AI vs WarBrain (decision changes only, identical stats, enemy lvl 7)

### Squad: BRUTES

| Party | AI | EDmg | Downs | Wipe% | Waste% | Kills/battle | Turns | Focus/turn | Pierce | Crits |
|---|---|---|---|---|---|---|---|---|---|---|
| BALANCED | vanilla | 77.9 | 0.02 | 0.0% | 19.1% | 0.02 | 2.8 | 0.82 | 0.35 | 0.35 |
| BALANCED | WarBrain | 125.9 | 0.45 | 0.0% | 19.0% | 0.45 | 3.1 | 1.05 | 1.06 | 0.44 |
| TURTLE | vanilla | 75.1 | 0.02 | 0.0% | 22.3% | 0.02 | 3.0 | 0.78 | 0.48 | 0.47 |
| TURTLE | WarBrain | 135.8 | 0.40 | 0.1% | 21.1% | 0.40 | 3.4 | 0.98 | 1.50 | 0.54 |
| GLASS_CANNON | vanilla | 40.2 | 0.00 | 0.0% | 15.7% | 0.00 | 2.1 | 0.91 | 0.14 | 0.14 |
| GLASS_CANNON | WarBrain | 54.1 | 0.08 | 0.0% | 15.1% | 0.08 | 2.1 | 1.36 | 0.44 | 0.17 |
| DODGE | vanilla | 68.3 | 0.02 | 0.0% | 35.8% | 0.02 | 2.6 | 0.88 | 0.17 | 0.17 |
| DODGE | WarBrain | 92.4 | 0.26 | 0.0% | 22.6% | 0.26 | 2.7 | 1.20 | 0.63 | 0.25 |

### Squad: MIXED

| Party | AI | EDmg | Downs | Wipe% | Waste% | Kills/battle | Turns | Focus/turn | Pierce | Crits |
|---|---|---|---|---|---|---|---|---|---|---|
| BALANCED | vanilla | 48.1 | 0.00 | 0.0% | 26.2% | 0.00 | 2.1 | 1.26 | 0.15 | 0.29 |
| BALANCED | WarBrain | 70.9 | 0.12 | 0.0% | 24.4% | 0.12 | 2.2 | 1.40 | 0.48 | 0.35 |
| TURTLE | vanilla | 51.9 | 0.00 | 0.0% | 28.3% | 0.00 | 2.6 | 1.09 | 0.23 | 0.40 |
| TURTLE | WarBrain | 82.2 | 0.12 | 0.0% | 33.2% | 0.12 | 2.7 | 1.22 | 0.80 | 0.43 |
| GLASS_CANNON | vanilla | 19.9 | 0.00 | 0.0% | 13.9% | 0.00 | 1.8 | 0.79 | 0.08 | 0.06 |
| GLASS_CANNON | WarBrain | 26.6 | 0.00 | 0.0% | 13.0% | 0.00 | 1.8 | 1.18 | 0.21 | 0.08 |
| DODGE | vanilla | 38.1 | 0.00 | 0.0% | 35.0% | 0.00 | 2.0 | 0.98 | 0.10 | 0.09 |
| DODGE | WarBrain | 54.1 | 0.08 | 0.0% | 23.2% | 0.08 | 2.1 | 1.40 | 0.39 | 0.16 |

### Squad: WARBAND

| Party | AI | EDmg | Downs | Wipe% | Waste% | Kills/battle | Turns | Focus/turn | Pierce | Crits |
|---|---|---|---|---|---|---|---|---|---|---|
| BALANCED | vanilla | 36.2 | 0.00 | 0.0% | 34.4% | 0.00 | 2.2 | 0.77 | 0.14 | 0.20 |
| BALANCED | WarBrain | 63.9 | 0.06 | 0.0% | 19.9% | 0.06 | 2.4 | 1.02 | 0.49 | 0.27 |
| TURTLE | vanilla | 42.0 | 0.00 | 0.0% | 38.9% | 0.00 | 2.7 | 0.74 | 0.23 | 0.31 |
| TURTLE | WarBrain | 78.7 | 0.07 | 0.0% | 28.2% | 0.07 | 2.8 | 1.02 | 0.79 | 0.37 |
| GLASS_CANNON | vanilla | 19.9 | 0.00 | 0.0% | 13.9% | 0.00 | 1.8 | 0.79 | 0.08 | 0.06 |
| GLASS_CANNON | WarBrain | 26.6 | 0.00 | 0.0% | 13.0% | 0.00 | 1.8 | 1.18 | 0.21 | 0.08 |
| DODGE | vanilla | 38.1 | 0.00 | 0.0% | 35.0% | 0.00 | 2.0 | 0.98 | 0.10 | 0.09 |
| DODGE | WarBrain | 54.1 | 0.08 | 0.0% | 23.2% | 0.08 | 2.1 | 1.40 | 0.39 | 0.16 |

## 2. Difficulty knob sweep (WarBrain, BALANCED party, MIXED squad, lvl 7)

| GlobalIntelligence | GlobalTempMult | MistakeAdd | EDmg | Downs | Wipe% | Waste% |
|---|---|---|---|---|---|---|
| 0.5 | 3.0 | 0.15 | 50.3 | 0.01 | 0.0% | 27.1% |
| 0.75 | 2.0 | 0.10 | 56.7 | 0.03 | 0.0% | 26.5% |
| 1.0 | 1.0 | 0.0 | 70.9 | 0.12 | 0.0% | 24.4% |
| 1.25 | 0.5 | 0.0 | 72.3 | 0.19 | 0.0% | 26.7% |
| 1.5 | 0.0 | 0.0 | 69.1 | 0.24 | 0.0% | 22.7% |

## 3. Enemy stat-scaling layer (BALANCED party, MIXED squad)

Scaling: percentage multipliers on ATK/HP plus flat ACC add — the shape vanilla `EnemyStatMods`/`GetExtraLevelStats` already uses.

| Scaling | AI | EDmg | Downs | Wipe% | Waste% |
|---|---|---|---|---|---|
| none | vanilla | 48.1 | 0.00 | 0.0% | 26.2% |
| none | WarBrain | 70.9 | 0.12 | 0.0% | 24.4% |
| ATK+25% | vanilla | 77.3 | 0.03 | 0.0% | 24.3% |
| ATK+25% | WarBrain | 96.0 | 0.38 | 0.0% | 26.1% |
| ATK+25% HP+25% | vanilla | 93.1 | 0.04 | 0.0% | 24.5% |
| ATK+25% HP+25% | WarBrain | 113.0 | 0.40 | 0.0% | 26.7% |
| ATK+25% HP+25% ACC+8 | vanilla | 104.2 | 0.06 | 0.0% | 22.8% |
| ATK+25% HP+25% ACC+8 | WarBrain | 131.1 | 0.51 | 0.0% | 25.0% |
| ATK+50% HP+50% ACC+8 | vanilla | 207.6 | 0.31 | 0.2% | 22.5% |
| ATK+50% HP+50% ACC+8 | WarBrain | 247.0 | 1.22 | 2.1% | 25.1% |

## 4. Focus policy isolation (WarBrain otherwise identical, BALANCED party, MIXED squad, lvl 7)

| FocusPolicy | EDmg | Downs | Wipe% | Pierce/battle | Crits/battle |
|---|---|---|---|---|---|
| NONE | 41.2 | 0.01 | 0.0% | 0.10 | 0.12 |
| VANILLA_RANDOM | 65.0 | 0.08 | 0.0% | 0.36 | 0.34 |
| MAX | 77.6 | 0.17 | 0.0% | 0.58 | 0.43 |
| SMART | 70.9 | 0.12 | 0.0% | 0.48 | 0.35 |

