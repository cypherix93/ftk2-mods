---
date: 2026-08-06
slug: eor-rehost-wave2-implementation
stage: implementation complete offline; in-game smoke pending
status: in-progress
---

# Pass the torch: EOR-rehost Wave 2 implementation — offline-complete, closeout docs in flight, smoke pending

## Next action (start here)

1. **If closeout docs aren't finished yet:** Wave 8 units 15 (SPEC.md/coverage-matrix/MULTIPLAYER.md
   deltas) and 16 (opus disposition ledger annotating the three spec files) were dispatched but showed
   `pending` in the flight log at the time this handoff was written, and other agents may still be working
   `FTK2.ClassForge/SPEC.md`, `FTK2.DevKit/SPEC.md`, `docs/research/eor-rehost-coverage-matrix.md`,
   `docs/MULTIPLAYER.md`, and the three `docs/superpowers/plans/2026-08-05-*-spec.md` files (git status
   showed all three specs modified-but-uncommitted at the time this handoff was written) — do not touch
   those paths until whatever session owns them finishes and commits.
2. **Run Wave 8 unit 18's final gate** once 15/16 land: all 5 offline suites (`DevKit.Core`,
   `ClassForge.Core`, `ClassForge.Recipes`, `Summoner.Core`, `Blessings.Core.Tests` — see the note under
   "State of the world" about the suite actually being 5 now, not 4, since Blessings shipped), all plugin
   builds vs `tools/bin/refs`, `ClassForge.PackCheck` across all packs, and
   `tools/deploy.ps1 -StageOnly -SkipBuild` staging clean.
3. **Hand off to the operator.** The smoke script is ready:
   `docs/superpowers/plans/2026-07-25-eor-rehost-operator-handoff.md`, new "Wave-2 smoke checks
   (2026-08-06)" subsection of §(c) (checks 9–35, systems A–F). Nothing in this wave has touched a running
   game yet — every claim below is offline-verified only, same posture as the Wave-1 handoff.
4. **After the operator's V-1 measurement comes back green** (loot-grant `GrantKey` agreement across a
   real 2-peer session, smoke check 12): flip `[Skills] EnableLootGrants`'s **default** from `false` to
   `true` in `ClassForgePlugin.cs` — a small, deliberate follow-up commit, not a config change (the
   default itself is what's gated).
5. **After the operator's V-2 measurement (if ever pursued):** re-open `M-LG4` (affix drop-rolling +
   Mode-H design) — parked at Wave 3's engage and never picked back up this session; it needs its own
   spec pass, not just implementation, since Mode H's channel choice (§9's OQ-5) was never finalized.

## Objective (what this wave was)

Implement three EOR-porting specs — loot-grant sync verb, Risky Blessings, encounter modifiers — on
`engine/eor-rehost`, offline-verified to the Wave-1 standard (unit tests + schema validation + reference
builds), each spec's verification milestone run before its implementation milestones, every open question
explicitly dispositioned (resolved or carried with a note), ending in an updated operator smoke script and
this handoff. Full brief: `docs/superpowers/handoffs/2026-08-05-eor-rehost-wave2-handoff.md` (the
pass-the-torch doc that kicked this run off) and the autopilot flight log this handoff summarizes
(`autopilot-flight-log.md`, session-scratchpad only — not checked into the repo).

## What shipped

Nine milestone commits, in order, all on `engine/eor-rehost`, all authored this autopilot run:

| Commit | What |
|---|---|
| `e57b02b` | Blessings M1 — `BLSS_PACK_EOR_BLESSINGS` content pack (15 traits, 1 recipe, all `Enabled: true`) |
| `fc17c53` | Loot M-LG1 — offline core (delta computer, grant stream, codec, pending-grant store) |
| `f7b14b9` | Loot M-LG2 — `GetLootDropsFromEnemies` postfix, SP path, 4 consumer recipes (dark) |
| `1b98408` | Blessings M2 — `Blessings.Core`/`Blessings.Plugin` orchestrator, deploy.ps1 staging entry |
| `22131ce` | DevKit `TransportService` + Loot M-LG3 — MP wire-up (host send, receive/verify, failure matrix) |
| `f8c4eaf` | EM M-EM1 — `statuses.json`/`modifiers.json` loader, `CF_PACK_ENCOUNTER_MODIFIERS` (inert) |
| `1f25aec` | PSN merge — Wave-2 verification surfaces folded into `game-patch-surface-notes.md` |
| `a92a0e3` | EM M-EM2 — `Scope: COMBAT` engine capability, vocabulary v1.2, adapter, merge write |
| `d293536` | EM M-EM3+M-EM4 — generated recipes, reward halves wired through the loot verb |

Plus a closeout-docs commit that will follow from Wave 8 units 15–17 (SPEC.md/coverage-matrix/
MULTIPLAYER.md deltas, disposition ledger, this handoff pair) — **not yet made as of this writing**; this
handoff's own two files (this one and the operator-handoff extension) are deliberately **left
uncommitted** per this task's instructions, for whoever lands Wave 8 to fold in.

All three specs are now implementation-complete offline through: loot M-LG1–M-LG3 (M-LG4 parked), Blessings
M0–M2 (M3/M4 are the in-game smoke waves, out of autopilot scope by design), encounter modifiers M-EM1–M-EM4.

## State of the world

- **Branch:** `engine/eor-rehost`. **Worktree:** `D:\src\mods\ftk2-mods`.
- **Commits ahead of `origin/engine/eor-rehost`:** 9 (`git status -sb` showed `ahead 9` at last check —
  the nine milestone commits above; the prior wave's `7217b99` spec commit is already on `origin` from an
  earlier push cycle... verify with `git log origin/engine/eor-rehost..HEAD` before assuming that count,
  since nobody in this session pushed).
- **Working tree at last check:** three spec files modified-but-uncommitted (Wave 8 units 15/16 in
  progress elsewhere) — `docs/superpowers/handoffs/` untracked (this handoff). Otherwise clean.
- **Suites green (last full run recorded in the flight log, Wave 7/unit 14):**

  | Suite | Count |
  |---|---|
  | `DevKit.Core` | 91 |
  | `ClassForge.Core` | 32 |
  | `ClassForge.Recipes` | 287 |
  | `Summoner.Core` | 16 |
  | `Blessings.Core.Tests` | 34 |

  Run each with `dotnet run --project <suite> -c Release` — **not** `dotnet test`; the suites are console
  runners (build-template-notes §5, a Wave-1 lesson still in force).
- **Plugin build count has grown to 6, not 5.** `tools/deploy.ps1`'s `Stage-Payload` build list now
  includes `Blessings.Plugin` alongside DevKit/ClassForge/Summoner/Wardrobe/WarBrain. **Flag for whoever
  runs Wave 8 unit 18:** the flight log's own "Next" note says "builds ×5" — that undercounts by one now
  that Blessings shipped a plugin project this wave. Verify all 6 build clean vs `tools/bin/refs`
  (DevKit/ClassForge/WarBrain need `-p:ManagedDir/-p:BepInExDir`; Summoner/Wardrobe/Blessings don't, per
  `deploy.ps1`'s own build-arg table).
- **PackCheck:** all packs (including the new `CF_PACK_ENCOUNTER_MODIFIERS` and
  `BLSS_PACK_EOR_BLESSINGS`) were reported zero-error as of their respective milestone commits; re-run at
  final gate to confirm nothing regressed since.
- **Deploy staging:** `tools/deploy.ps1 -StageOnly -SkipBuild` was not re-run after the final `d293536`
  commit as of this writing — do that as part of Wave 8 unit 18, not assumed clean.

## The five verification gates (Wave 1 → Wave 2 gate synthesis) and their resolutions

Every spec's first milestone was a PSN-style live-signature verification pass; five gates fell out of it
and were resolved as binding decisions (override spec text where they conflict) before any implementation
code was written:

- **Gate A (loot §3.4/§4.1/§4.2 — `GameRandom.NextCount` is dead code in the shipped build).** Dropped
  `DrawMark` entirely. `GrantKey`/`grantSeed` entropy is now `CombatSeed + sorted enemy Entity.Guids +
  ListDigest(SHA256 over canonical "ConfigName|Stack" rows of the pre-grant vanilla loot list) + sorted
  owner guids`. The zero-shared-draw invariant is enforced **structurally** (the core compute API takes no
  shared-stream reference at all) rather than by a runtime assertion against dead code.
- **Gate B (loot OQ-2 — item-pool filter semantics).** Adopted the native-stricter pool (tag membership
  OrdinalIgnoreCase + exact rarity + not-Hidden + Value≠0 + rarity-weight≠0 + expansion-enabled), a
  **deliberate deviation** from EOR's looser tag+rarity-only pool — recorded, not silent.
- **Gate C (encounter-modifiers §13.1 — `CHANGE_STAT MXHP FlatPercent` throws natively).**
  `FlatValueFrom: "TARGET_MXHP_PCT"` (engine computes the flat value from the target's max HP at emission,
  mirroring EOR's `max(1, round(|maxhp×pct|/100))` floor) is the shipped **primary** design for M-EM2/3,
  not a fallback — the native verb genuinely can't do percent-of-max MXHP deltas. Runtime rounding/clamp
  behavior against EOR's floor-at-1 is still smoke-pending (§13.1, operator smoke check 28).
- **Gate D (encounter-modifiers §13.7 — no native way to express "enemy" via `CHARACTER_TYPE`).** Shipped
  a new `IS_ENEMY {Of}` condition wrapping `CharacterHelper.IsEnemy` (GroupIndex==1) instead.
- **Gate E (blessings V7 — grant-anchor-vs-parity-handshake ordering).** "Postfix ordered after DevKit's"
  turned out not to be an achievable/meaningful guarantee (the handshake is async fire-and-forget). Shipped
  design: poll `DevKitParityBridge.HasVerifiedMatch()` at the grant anchor, fail-closed if no verdict yet,
  self-healing at the next `AdventureDirector.Initialize` call. This is exactly what operator smoke check
  24 (first-online-session note) is watching for.

## Parked / carried-forward items

**Explicitly out of scope, gated on future work:**
- **M-LG4** (loot spec) — affix drop-rolling (`AFFIX_ROLL`/`REPLACE_ITEM`) on the pre-mint registry, and
  Mode-H (host-push, as opposed to shipped Mode-M lockstep-mirror) design finalization. Gated on the V-2
  in-game measurement (loot spec §8.2) which has never run. Not re-specced this wave.
- **`EnableLootGrants` default flip** (`false` → `true`) — gated on the operator's V-1 measurement
  (smoke check 12). Code and tests are ready; only the default itself is withheld.
- **Blessings debug commands** (`blss status` / `blss clear`, spec §6 "DevKit command surface") — skipped
  this wave because **DevKit has no command-registration surface at all** (searched; the spec's §6 row
  describes a concept, not something that exists in `FTK2.DevKit` today). Needs a DevKit feature, not a
  Blessings-side fix.

**Spec open questions carried forward (not silently dropped — each has an owner and a next step):**
- **Loot OQ-3** (`RewardEncounterComponent` combats granting loot-verb procs) — kept as-is (EOR's postfix
  did the same); revisit only if playtesting says reward-encounter "combats" shouldn't proc traits.
- **Loot OQ-4** (Branch-A scripted venue loot bypasses the hook entirely) — accepted v1 gap, EOR had the
  same gap.
- **Loot OQ-5** (Mode-H channel choice, `DebugThing` vs `EorTownServices`) — moot until Mode H ships
  (parked with M-LG4).
- **Loot OQ-6** (can a peer join mid-combat/mid-combat-end?) — `[UNVERIFIED]`, affects only a JIP footnote.
- **Blessings OQ4** (mid-run party-composition changes — can a player entity be added/rebuilt mid-run, and
  does it need its own grant-on-add hook?) — owner: M0 recon, never done this wave; v0 behavior if
  unhooked is "asymmetry only if the rebuild itself is asymmetric," which vanilla replication is assumed to
  prevent, but this is unverified.
- **Blessings OQ6** (double parity coverage of `blessings.json` — both ClassForge's pack hash and
  Blessings' own hash cover it, so one edit can produce two mismatch reports) — accepted redundancy,
  cosmetic; owner M4, revisit once the operator has actually seen the double-report in practice.
- **Blessings OQ5** (hidden-trait visibility) is technically "smoke-pending" rather than "carried" — it's
  answered by operator smoke check 22, not by more implementation work.
- **Encounter-modifiers §13.3** (does FTK2 support mid-combat join-in-progress at all?) — unknown
  repo-wide; the §4.5 reconstruction logic ships regardless (it also covers ordinary SP save/load
  mid-combat), so nothing is blocked on the answer, but MP JIP behavior specifically is unverified.
- **Encounter-modifiers §13.5** (`SELECTION_SET` generality — should `Selections` be readable by *owned*
  recipes outside `COMBAT` scope?) — shipped for exactly one consumer; the schema allows more but nothing
  else uses it yet. Revisit only when a second system (a hypothetical future Nemesis port is the named
  candidate) wants it.

## Next actions, in order

1. Land Wave 8 units 15/16 (doc deltas + disposition ledger) — owned by other agents as of this writing.
2. Run Wave 8 unit 18's final gate (5 suites, 6 plugin builds — see the count correction above — PackCheck,
   `deploy.ps1 -StageOnly -SkipBuild`).
3. Commit the closeout docs (units 15–17's output, including this handoff pair) as Wave 8's final commit.
4. Hand the operator smoke script (`2026-07-25-eor-rehost-operator-handoff.md` §(c), Wave-2 subsection,
   checks 9–35) to whoever has the real game install. Nothing past `dotnet build`/`dotnet run`/schema
   validation has touched a running copy of the game for any Wave-2 system yet.
5. On a green V-1 (loot smoke check 12): flip `EnableLootGrants`'s default, small standalone commit.
6. On operator bandwidth for a V-2 measurement: re-open M-LG4 with its own spec pass (not blind
   implementation — Mode H's design was never finalized, only reserved).

## Landmines / blockers carried forward from the prior handoff (still apply, unchanged by this wave)

- **The game can update again; refresh `tools/bin/refs` first if it does**, per
  `docs/research/build-template-notes.md` §2, before trusting any Harmony target signature cited in this
  wave's specs or code comments.
- **`ConfigMergePatches.DeserializeGameConfig<T>` is still the only sanctioned way to deserialize game
  config types** (`JsonHelper.importOptions` + ClassForge's lenient enum converter + null-collection
  normalization). This wave's two new merge writes (`Configs.StatusEffects` for encounter modifiers, and
  the Blessings pack's trait/recipe merge riding ClassForge's existing pack loader) both go through it.
  Bypassing it is the day-one black-screen bug class — still the single most important lesson standing.
- **The vendor desync detector hashes all of `GameRunData`** and compares RNG draw counts per action. This
  wave's two new systems were explicitly designed for that tripwire rather than around it: loot grants use
  a private derived stream (Gate A) with a structural zero-shared-draw guarantee; encounter modifiers use
  constant-2-draw hoisting so draw counts are checkable by counting. Never suppress or route around this
  detector; it is the belt to every system's suspenders.
- **`Env.Configs.*` dictionaries are `SerializedSortedDictionary<,>`** — any new plugin code touching them
  needs the `SerializedSortedDictionary` reference (Wardrobe.Plugin.csproj pattern). Encounter modifiers'
  `Configs.StatusEffects` merge write follows this.
- **Deploying while the game runs:** `deploy.ps1` skips byte-identical files, so data-only pack updates
  (e.g. re-staging just the Blessings `ClassPacks/`) work live, but any changed DLL needs the game closed.
  Packs are read at boot only — a `Mode`/`EnableLootGrants`/`DebugEncounterModifierChance` config edit
  needs a relaunch (or a rejoin, for the MP-visible ones) to take effect, not a hot-reload.
- **The user's game install** at `E:\Games\Steam\steamapps\common\For The King II` — same install noted in
  the prior handoff — is where the operator smoke checks in this handoff's companion doc should run.
