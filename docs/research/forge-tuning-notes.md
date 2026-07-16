# Item forge tuning notes (sweep of 2026-07-16)

Sweep: 3 profiles × seeds {7, 42, 1337} × 100 items = 900 candidates
(`tools/out/forge_runs/*.pack.json` + per-run audit reports, gitignored — reproduce with the
commands in the plan's Task 11; identical seeds give byte-identical packs). Every run validated
with **0 errors**. This file records what each knob visibly did — the "test the randomness"
deliverable.

## Knob observations

- **`BudgetMultiplier` + measured curves** hold the center: baseline runs put ATK/DEF/CRT means
  within the live p50 bands across all seeds. The budget model (typical-statline × multiplier ×
  chaos-jitter) is a trustworthy power governor.
- **`Chaos` 0.15 → 0.35** widens the min–max spread noticeably (chaotic_epics ATK 3–51 vs
  baseline's tighter band) without producing validator errors — the per-stat `max×1.3` allocation
  cap does its job as the outer fence.
- **`SynergyBias` 0.85** makes theme identity legible in the output: bulwark items reliably pair
  DEF+THRN+STEADFAST/GUARD, huntmaster pairs AWR+DAM_BEAST+CALLEDSHOT. At 0.5–0.6 (baseline)
  maybe a third of items read as "themed", the rest as honest random rolls. This knob is the
  single biggest "interestingness" lever.
- **`CurseChance` 1.0 + `CurseRebate` 0.5** (cursed_bargains) produces genuinely playable
  devil's bargains: every item carries a negative stat and an over-curve positive spike (hence
  that profile's 45 budget warnings — they are the design, not a bug).
- **`PassiveBudgetShare`/`SkillPrices`**: pricing skills at 0.15–0.45 of budget yields ~50–70%
  of items carrying a passive; passives correctly eat stat budget (passive-bearing items are
  visibly leaner on raw stats).
- **Hot spots to keep an eye on** (warnings, not errors): chaotic_epics rolls AWR (mean 8.9 vs
  live p50 0–5), THRN (up to 25 vs live 2–4 on armor) and DAM_* (up to 81 vs live fixed 20–25)
  above live norms — intentional for a late-game "epics" profile, but a real balance pass in-game
  should start with those three, or simply lower `BudgetMultiplier`/archetype weights for them.
- **Names/grammar**: the pattern+noun-per-class grammar produces zero unreadable names in 900
  rolls; duplicates across a run are possible (~4% observed) — curation dedupes by name.

## Curated pack

`FTK2.Armory/packs/forge_curated.pack.json` — 60/900, selected by a deterministic
interestingness score (passives, tradeoff curses, DAM_* niches, stat variety, icon availability,
ARTIFACT bonus) with caps (≤6 per class, ≤24 per rarity, unique names). Result: 60/60 with
passives, 32/60 cursed tradeoffs, 17 classes covered, ARTIFACT 24 / RARE 24 / UNCOMMON 8 /
COMMON 4. Validator: 0 errors, 11 budget warnings (all cursed-rebate spikes).
Contact sheet: `FTK2.Armory/packs/forge_curated-contact-sheet.html` (54/60 icons; 6 items had no
PRERENDER base for their donor and fall back to donor art in-game).
