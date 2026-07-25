# Project charter — EOR re-host engine build (2026-07-25)

**Objective:** build the engine pieces this repo needs to re-host the salvageable content of Enhanced Overhaul
Revamped v0.7.0.60 as validated, MP-first, data-driven packs — per the audit in
`docs/research/eor-0760-content-audit.md` — so content porting can begin the moment the game install is
restored to vanilla (Steam verify in progress).

**Owner directions locked (2026-07-25):**
- Content-driven as far as practical; DLL behaviors are ported **only** as engine *primitives* (generic,
  data-triggered, MP-first), each validated against the decompiled EOR source — never as per-feature patches.
- Engine first, content second: the build must not depend on the game install being available (it is being
  re-verified); reference assemblies are snapshotted before any C# work.
- Dropped outright (audit §6 rows 9, 11): Risky Blessings, Nemesis, encounter modifiers, campaign mutators,
  world events, town specialists, sanctums, quest archetypes (deferred to Questsmith), telemetry, version
  check, debug toolkit, camera tweaks.

## Scope

| Deliverable | Repo location | Basis |
|---|---|---|
| D1. ParityService core (R1 enforcement) | `FTK2.DevKit/src/` | `docs/MULTIPLAYER.md` R1; DevKit SPEC §ParityService |
| D2. ClassForge M1 — pack loader + class-select injection | `FTK2.ClassForge/src/` | ClassForge SPEC §3–4, OQ#3 resolved from decompile |
| D3. ClassForge M2 — trait bridge | `FTK2.ClassForge/src/` | OQ#1 resolved from EOR decompile (audit §4) |
| D4. ClassForge M3 — skill-recipe engine, vocabulary v1.1 | `FTK2.ClassForge/src/` | SPEC §4.6 + audit §7 primitives |
| D5. Summoner M0 — followers/characters pack loader | `FTK2.Summoner/src/` | Summoner SPEC §3 (loader subset only) |
| D6. EOR import converter + tests | `tools/eor_import.py`, `tools/tests/` | Armory manifest contract |
| D7. Converted content packs (classes, traits, recipes, items, followers) | `FTK2.ClassForge/data/`, `FTK2.Armory/packs/`, `FTK2.Summoner/data/` | audit §2, §6 |
| D8. Coverage matrix — every EOR mechanic dispositioned | `docs/research/eor-rehost-coverage-matrix.md` | audit §7 |
| D9. Spec/docs updates (SPEC deltas, README status table) | respective SPEC.md files | — |

**Out of scope for this run:** in-game testing (game files unavailable; operator smoke-tests after landing),
final vocab-index validation of converted packs (needs the restored install; schema-level validation only for
now), Questsmith/Forge/Runeworks/Venue/ActionPoints work, WarBrain changes, pushing/merging.

## Engineering rules (bind all implementation units)

1. Every Harmony patch logs "Target found: X" and fails safe (missing target → feature off, vanilla untouched).
2. All gameplay rolls draw from the game's shared `GameRandom`/`CombatState.Random` — never `System.Random`;
   no extra draws from shared streams on code paths that can execute asymmetrically across peers.
3. Primitives that suppress or mutate authoritative state (audit §7 MP-hazard rows) ship **host-decided +
   vanilla-replicated or synced**, or ship disabled-by-default with the hazard documented — never per-client.
4. Per-battle state lives on the plugin and resets on combat end; per-run state piggybacks `GameRunData`
   (CONVENTIONS.md); no gameplay-affecting static state that survives a run uncleaned.
5. Every engine registers `(guid, version, dataHash, enabledFeatures)` with ParityService; ClassForge policy
   `OnParityMismatch = Block` per its SPEC.
6. C# layout follows the WarBrain template (netstandard2.0 core + net472 plugin + offline sim/test harness);
   reference DLLs from the snapshot dir, never from the live game dir.
7. Game directory and the downloaded mod package are read-only. Commits on branch `engine/eor-rehost` only;
   no push, no merge, no writes outside the repo + scratchpad.

## Execution plan (autopilot waves)

Wave boundaries are dependency edges; units within a wave are independent.

**Wave 0 — grounding (retrieval).** Snapshot reference assemblies to `tools/bin/refs/` (gitignored) + record
the WarBrain build template · mine EOR decompile for the trait-system mechanism (OQ#1) · mine the game decompile
(`tools/out/decompile/FTK2/`) for `ADD_CHARACTER {Type,Value}`, patch-target signatures, `eTraits`,
`ConfigsHelper` load path (OQ#3/#4) and live enum ground truth (OQ#5) · extract the precise behavior of all 31
class skills + 11 code traits + 5 code affixes into a machine-usable behavior matrix.

**Wave 1 — design (judgment).** ClassForge SPEC delta: resolved OQs, trait-bridge design, recipe vocabulary
v1.1 with per-primitive MP posture · coverage matrix v1 (PORT / PORT-MODIFIED / PARK per mechanic, cited) ·
Summoner M0 loader design + `eor_import.py` converter design.

**Wave 2 — independent implementation.** ParityService · ClassForge M1 loader · class-select UI injection ·
trait bridge · recipe-engine core · Summoner M0 loader · `eor_import.py` + pytest.

**Wave 3 — dependent implementation + content.** Recipe primitive handlers (needs core) · classes/traits/
recipes pack authored from the behavior matrix + coverage verdicts · items → Armory manifests + followers packs
via converter.

**Wave 4 — verification + hardening.** Full build + all test suites · adversarial MP-posture review of all new
engine code against R1–R5 · schema-level validation of converted packs · SPEC/README/docs updates.

**Landing.** Acceptance check with evidence, per-wave commits squash-reviewed on the branch, parked list,
operator hand-off notes (in-game smoke-test script + post-verify validation steps).

## Acceptance criteria

1. Audit doc, this charter, and the coverage matrix exist and are committed on `engine/eor-rehost`.
2. `dotnet build` succeeds for every new/changed C# project (DevKit, ClassForge, Summoner) against snapshotted
   reference assemblies.
3. All C# test/sim suites pass; `python -m pytest tools/tests` fully green including new converter tests.
4. Coverage matrix dispositions **all** of: 31 class skills, 20 traits, 22 affixes — each PORT/PORT-MODIFIED/
   PARK with a decompile citation; no silent omissions.
5. Every new primitive's SPEC entry states MP posture (SYNCED/LOCAL, RNG source, authority, SafeMode); the
   Wave-4 review finds zero unaddressed R1–R5 violations.
6. Converted packs exist for: classes (31), traits (portable subset), skill recipes (PORT subset), items
   (~390 as Armory manifests), followers (206, dangling refs fixed or excluded with note) — schema-valid;
   full vocab validation explicitly parked pending the restored game install.
7. No game-directory writes, no pushes; all work on the branch; flight log complete.

## Risks / parked-by-design

- Vocab-index is stale/contaminated until Steam verify completes → final validation parked (criterion 6).
- `ON_CRIT` signal and combat-gold verb remain unverified game surfaces → affected mechanics land as PARK in
  the matrix unless Wave 0 finds the signal.
- Consume/status-suppression primitives are MP-hazardous → rule 3 applies; may land disabled-by-default.
- Trait bridge may reveal that native trait-slot UI can't host >17 traits without deeper UI patches → bridge
  scope may narrow to grant/query mechanics + loadout-style selection (EOR's own model), documented in SPEC.
