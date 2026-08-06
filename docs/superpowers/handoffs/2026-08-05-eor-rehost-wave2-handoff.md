---
date: 2026-08-05
slug: eor-rehost-wave2
stage: specs complete (agent-authored + committed); implementation not started
status: in-progress
---

# Pass the torch: implement EOR-rehost Wave 2 — Risky Blessings, loot-grant sync verb, encounter modifiers

## Next action (start here)

Invoke `/engage-autopilot` with the objective and acceptance criteria in §Autopilot brief below.
The three specs to execute, in order, are:

1. `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md` (keystone — encounter-modifier
   rewards depend on it; milestones M-LG1..M-LG4)
2. `docs/superpowers/plans/2026-08-05-risky-blessings-spec.md` (independent of #1; M0..M4)
3. `docs/superpowers/plans/2026-08-05-encounter-modifiers-spec.md` (reward halves consume #1;
   M-EM1..M-EM4)

Every spec's FIRST milestone is a verification wave (PSN-style signature write-ups against
`tools/bin/refs/` — refreshed 2026-08-05 to the 7/31 game build). Run all three verification
milestones before any implementation; several later milestones are explicitly gated on their
results (blessings Tier B/C stat keys; loot verb V-1/V-2/V-3; modifiers' 7 open questions).

## Objective

Implement the second EOR-porting wave — three specced systems that together unlock ~10 parked
mechanics — on the existing engine stack (ClassForge/DevKit), offline-verified to the same
standard as Wave 1 (unit tests + schema validation + reference builds), ending with an updated
operator smoke script for the in-game checks no offline test can reach.

## Pipeline stage

Design stage complete: three implementation specs were written by verification agents on
2026-08-05 (each verified against BOTH the EOR 0.7.0.60 decompile and fresh live-DLL decompiles)
and committed in `7217b99`. No implementation code exists for any of the three. The specs are
autopilot-ready: milestone-broken, with acceptance criteria and open-questions sections.

## State of the world

- **Branch:** `engine/eor-rehost`   **Worktree:** `D:\src\mods\ftk2-mods`
- **Committed, not pushed:** 5 commits ahead of `origin/engine/eor-rehost`
  (`git status -sb` run 2026-08-05, working tree clean):
  - `7217b99` Wave-2 specs: Risky Blessings, loot-grant sync verb, encounter modifiers
  - `a4a69c5` FTK2.Wardrobe: character-creation cosmetic unlocks + EOR species trick
  - `c4d2fcd` Add UI_TOOLTIP_<class>_DESCRIPTION loc family; repair mangled apostrophes
  - `c469f09` Localization for all 48 SKILL_CF_* recipe skills; park Baldurs pack
  - `ee6759f` Day-one in-game smoke fixes: boot crash, hollow merges, visual remap, deploy tooling
- **PR:** no PR (branch-only workflow so far)
- **Working tree:** clean
- **CI / checks:** no CI. All four offline suites (DevKit.Core 91, ClassForge.Core 21,
  ClassForge.Recipes 149, Summoner.Core 16) ran green earlier this session (2026-08-05, before
  the final plugin-only changes: VisualRemapPatches, Wardrobe — neither is test-covered).
  All 5 plugin projects build clean vs `tools/bin/refs` (last full build this session).
  **Re-run `dotnet test` on all four suites at resume start** as a baseline.

## Autopilot brief (objective + acceptance criteria for /engage-autopilot)

**Objective:** execute the three Wave-2 specs in the order above, each spec's milestones in
order, verification milestones first.

**Acceptance criteria (per spec, and overall):**
1. Every PSN verification item in the spec's verification section is written up (verbatim live
   signatures) BEFORE code that depends on it; a verification that fails its expectation gates
   its dependent milestone into the spec's stated fallback (do not improvise around it).
2. All existing test suites stay green; every new Core-level capability (recipe vocabulary,
   loaders, selectors) ships with unit tests in the matching `*.Tests` project.
3. All plugin projects build clean vs `tools/bin/refs` (`-p:ManagedDir/-p:BepInExDir` for
   DevKit/ClassForge/WarBrain-style csprojs; Summoner/Wardrobe pattern needs no flags).
4. New pack content schema-validates; `tools/deploy.ps1 -StageOnly -SkipBuild` stages without
   error (extend the script if a new mod/pack needs staging).
5. Spec open questions are each explicitly dispositioned (resolved with evidence, or carried
   forward in the spec with a note) — never silently dropped.
6. Ends with: an updated operator smoke script section (extending
   `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md` §c style) covering the
   new systems' in-game checks, and a session handoff for anything unfinished.
7. In-game verification is explicitly OUT of autopilot scope (needs the running game/operator);
   everything must be offline-verified and honestly labeled as awaiting smoke test.

## Decisions made this session (verbatim, with rejected alternatives)

- **Loot verb ships Mode M, not host-push:** every peer computes the identical delta from a
  private `GameRandom` derived from combat seed (`pIgnoreMultiplayerStaticSeed: true` is
  mandatory — the seeded ctor silently overrides to MultiplayerSeed in MP otherwise); host
  pushes `CF_SYNC_LOOT_GRANT_V1` as an idempotent AUDIT record only. The original "host rolls,
  clients apply verbatim" brief was REFUTED by decompile evidence (loot gen is lockstep on all
  peers; the vendor desync detector compares draw counts). Mode H remains schema-reserved,
  gated on verification V-2.
- **Blessings v0 has no in-game offer UI:** config knob `[Blessings] Mode` (Disabled/Random/id),
  Random = deterministic SHA256(MapGenSeed|ConfigName) weighted walk, zero RNG draws. The
  in-run offer dialog + `BLSS_SYNC_BLESSING_V1` are specced but parked for v1. Blessing = hidden
  `TRAIT_BLSS_*` Thing granted via native `CharacterHelper.GiveTrait`; NO GetStat patches.
- **Encounter-modifier statuses are pack-shipped** (`statuses.json`, a NEW ClassForge merge
  target — loader currently parses classes/traits/abilities/items only), killing EOR's
  runtime-minting anti-pattern. Selection = combat-scoped ownerless recipes, constant 2 draws
  per eligible combat.
- **CF_PACK_BALDURS is parked** (deploy.ps1 excludes it by default; `-IncludeBaldurs` restores)
  until its skill localization is written — its classes/items are otherwise fixed and working.
- **FTK2.Wardrobe is NOT parity-registered** (pure client-cosmetic; peers accept cosmetic ids
  unvalidated). Playable-class unlocking deliberately not shipped (Randomize divergence risk).
- **Game-type JSON deserialization MUST use `JsonHelper.importOptions`** (+ ClassForge's lenient
  enum converter): game config classes are field-based; default STJ silently produces hollow
  objects. This was the day-one black-screen root cause — the single most important lesson for
  any new merge code.
- **Pack classes need visual donor remap** (`VisualRemapPatches`): no JSON can create dCharacter
  model records; any new playable-entity content must either remap to a donor or be
  existence-probed.

## Artifacts

- Specs (all exist, committed in `7217b99`):
  - `docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md`
  - `docs/superpowers/plans/2026-08-05-risky-blessings-spec.md`
  - `docs/superpowers/plans/2026-08-05-encounter-modifiers-spec.md`
- Wave-1 operator handoff (install/smoke-script style to extend):
  `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md`
- MP rules: `docs/MULTIPLAYER.md` · coverage matrix: `docs/research/eor-rehost-coverage-matrix.md`
  · EOR bug patterns: `docs/research/eor-0760-content-audit.md`
- EOR source of truth (read-only): package `D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN`
  (single plugin DLL `EnhancedOverhaulRemix.dll`; decompile at
  `tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs`, gitignored, current).
- Live-game decompiles from this session (scratchpad, REGENERATE if missing — gitignored):
  `ilspycmd -t <Type> -r <game Managed dir> <game FTK2.dll>`; refs snapshot `tools/bin/refs/`
  matches the live 7/31 build.
- Deploy tooling: `tools/deploy.ps1` (build/stage/install/uninstall/zip; manifest at
  `<game>/ftk2mods-deploy-manifest.json`). Current friends zip:
  `tools/out/deploy/ftk2mods-20260805-c4d2fcd.zip`.

## Open findings (undispositioned)

none — Wave-1 findings were all fixed and committed; Wave-2 specs carry their own open-questions
sections (dispositioning them is acceptance criterion #5, not a pre-existing finding list).

## Landmines / blockers

- **The game updated 2026-07-31 and can update again.** Every Harmony target must be verified
  against `tools/bin/refs` (current) or resolved by name with params bound by name (see
  `ClassSelectPatches.cs` comments). If the game updates mid-wave, refresh refs per
  `docs/research/build-template-notes.md` §2 first.
- **`ConfigMergePatches.DeserializeGameConfig<T>`** is the ONLY sanctioned way to deserialize
  game config types (JsonHelper options + lenient enums + null-collection normalization).
  Bypassing it reintroduces the black-screen class of bug.
- **The vendor desync detector hashes all of `GameRunData`** (entities, Stats, followers) and
  compares RNG draw counts per action — any asymmetric write or extra shared-stream draw is
  flagged by the game itself. Design FOR this tripwire (the specs do); never suppress it.
- **`Env.Configs.LoreStore`/`Characters` etc. are `SerializedSortedDictionary<,>`** — plugins
  touching them need the `SerializedSortedDictionary` reference (see Wardrobe.Plugin.csproj).
- **`System.Memory`:** any plugin using `JsonSerializer` string overloads needs the compile-time
  `System.Memory` PackageReference with `ExcludeAssets="runtime"` (pattern in
  ClassForge.Plugin.csproj / Summoner.Plugin.csproj) — the game supplies it at runtime.
- **Deploying while the game runs:** `tools/deploy.ps1` skips byte-identical files, so data-only
  updates work live, but changed DLLs need the game closed. Packs are read at boot only.
- **The user's game install** at `E:\Games\Steam\steamapps\common\For The King II` has the
  current mod set deployed; ClassForge `VerboseLogging=true` is set in its BepInEx config (left
  on deliberately to confirm a Drunken Courage proc — see operator notes).
- **In-game smoke status:** single-player is verified working through class select, adventure
  start, loadout, combat. NOT yet exercised: 2-peer MP handshake, online class changes, and the
  Wardrobe plugin's first in-game run (deployed, offline-verified only).
