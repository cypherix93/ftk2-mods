# Morning review guide

## 2026-07-16 overnight drop — content generation system + FTK2.Armory M1

**Start here: open `FTK2.Armory/REVIEW.html` in a browser** (local file; item cards, icons,
in-game verification checklist, open questions). Then:

1. **`FTK2.Armory/CATALOG.md`** — the 9 sets + 13 standalones and the verified mechanics each
   synergy is built on. The claim ids link into `docs/research/game-mechanics.md`.
2. **`docs/research/game-mechanics.md`** — the ground-truth doc you asked for (combat +
   overworld, every claim decompile-cited). Biggest design-relevant finds: crits only on PERFECT
   rolls; ADD_STATUS only on PERFECT; focus buys guaranteed slots; THRN pierces blocks; `Threat`
   does nothing in combat; DoT ticks ignore DEF/RES; LCK procs go **negative** below 50; tiers
   are 0–3; ARTIFACT is the real "legendary".
3. **`docs/research/forge-tuning-notes.md`** — what each itemforge knob did across 900 rolls;
   curated 60 shipped in `FTK2.Armory/packs/forge_curated.pack.json`.
4. **`FTK2.Armory/INSTALL.md`** — manual install (your game dir was never written to). The EOR
   CustomItems drop-in path is decompile-verified generic (`docs/research/eor-loader-notes.md`,
   which also resolves Forge SPEC OQ#6 and #2).
5. Tooling if you want to crank it yourself: `tools/README.md` —
   `itemforge/forge.py --profile … --seed …` (deterministic), `validate_pack.py` (the gate),
   `iconforge/render.py`, `compile_pack.py`. 40 pytest tests: `python -m pytest tools/tests`.

In-game verification checklist (nothing was launch-tested): the numbered list at the top of
REVIEW.html — ~15 minutes with EOR's give-item debug.

---

## 2026-07-10 spec drop (previous)

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

## Cross-cutting decisions

- **MP posture — DECIDED (2026-07-10): co-op is a hard requirement.** All specs are now MP-first per
  `docs/MULTIPLAYER.md` (repo-wide architecture: parity handshake hosted by DevKit, determinism rules
  for generated content, host authority for decisions, dev-command lockout). Every spec's §9 declares
  its parity class: WarBrain is likely HOST_ONLY (host-only install works); Venue is HOST_ONLY or
  ALL_PEERS pending a probe test; everything that merges content into game Configs (ClassForge, Forge,
  Runeworks, Summoner, Questsmith, ActionPoints) is ALL_PEERS with Block-on-mismatch defaults.
  MULTIPLAYER.md tracks the 5 netcode open questions the decompile pass must resolve first.
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
