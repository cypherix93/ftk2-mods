# Morning review guide

Nine mod specs were drafted overnight (2026-07-10), each with an example starting dataset.
**All 60 JSON data files parse; all 9 specs follow the 11-section CONVENTIONS template.**
Nothing is implemented yet — these are design docs for you to approve/redline.

## Suggested reading order (by decision value)

1. **FTK2.WarBrain** (578 lines) — the flagship. Review the consideration catalogue (§4), the
   brain-profile schema, and the memory/reaction design. Key approvals: softmax-temperature +
   MistakeChance as the "personality vs misplay" split; memory scope default (per-battle vs per-run).
2. **FTK2.DevKit** (410) — build first alongside WarBrain; everything else's test plans lean on it.
   Key approval: the shared logging/patch-registry APIs other mods depend on.
3. **FTK2.Forge** (516) — **M1 is zero-code** (orb + recipe-chain item levels in pure JSON). Cheapest
   win in the repo; could be play-testable the same day implementation starts.
4. **FTK2.ClassForge** (543) — pack loader + 3 fully-authored BG3 classes (Hexblade Warlock,
   Battle Master, Necromancer) + the skill-recipe framework (§the extensibility bet: data-composed
   trigger→condition→effect recipes covering ~80% of class-design needs).
5. **FTK2.Runeworks** (465) — key architecture decision to approve: **variant-wrapper** (recommended)
   vs instance-state sidecar for socket state; tradeoff table in §3.
6. **FTK2.Summoner** (622) — 3 starter evolution chains; the central mechanism is a
   `TryCreateSummon` prefix substituting the current evolution stage. Depends on ClassForge for the
   Trainer class injection.
7. **FTK2.Questsmith** (447) — pack loader + 3 showcase quests (rival-race / siege-escort / stealth
   heist) + 5 new objective verbs. M1 runs vanilla-verbs-only versions of the quests.
8. **FTK2.ActionPoints** (441) — the highest-blast-radius mod; deliberately last. Review the
   systemic-interaction audit and the per-save opt-in model.
9. **FTK2.Venue** (315) — smallest scope: rules-driven grid selection; custom grid sizes honestly
   parked as an M3 research milestone.

## Cross-cutting decisions to make

- **MP posture**: every spec defaults single-player-safe, but confirm: is multiplayer a requirement
  for any of these? (Changes AP economy and Runeworks architecture priorities.)
- **Standalone vs EOR-coupled**: all specs assume standalone plugins coexisting with EOR. Several
  open questions (eTraits injection, runtime Things merge, affix variant persistence) are answered
  fastest by decompiling `EnhancedOverhaulRemix.dll` — that decompile session is the single highest-value
  next research task and unblocks ClassForge/Runeworks/Forge simultaneously.
- **Build order**: proposed DevKit + WarBrain first (P0), then Forge M1 (zero-code), then ClassForge.
- **Open questions**: each spec's §11 lists what needs dnSpyEx verification before implementation.
  The recurring ones: exact patch-site signatures, `Configs` runtime-merge mechanics, `GameRunData`
  persistence keys, and MP sync behavior.

## What was verified vs inferred

Specs cite only class/method/field names verified from FTK2.dll metadata/IL dumps
(docs/research/game-code-reference.md) and game JSON schemas (docs/research/data-schemas.md).
Anything not verifiable is explicitly flagged in each spec's §11 rather than asserted —
expect a short decompile-verification pass at the start of each mod's implementation.

## Housekeeping

- Repo has no remote yet (you said you'd provision GitHub in the morning): `git remote add origin <url> && git push -u origin main`.
- ClassForge's icons/portraits are 1×1 placeholder PNGs (see `icons/PLACEHOLDERS.md`).
- The original feasibility study is preserved at `docs/feasibility.md`.
