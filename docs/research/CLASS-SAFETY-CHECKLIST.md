---
title: Class Safety Checklist - the gate a class must pass before it ships
type: reference
tags: [ftk2, classforge, safety, checklist, ship-gate]
repo: C:\Users\ben\repos\ftk2-mods-crucible
---

# Class Safety Checklist

The gate every class must pass before it goes into a co-op session. Balance is a separate
question and is **not** in scope here — this list is only about *does it break the game, and can
the player see what it does*.

Every item is checkable. Several are checkable offline. Each one exists because it actually
happened in this project, and the entry says which.

Companion docs: `AGENT-BRIEF.md` (global rules), `VERIFICATION-METHOD.md` (what counts as proof),
`MULTIPLAYER-RULES.md` (the 21 co-op rules).

---

## A. Crash safety — will it end a session

- [ ] **A1. Every custom status has an icon donor, or is explicitly recorded as iconless.**
      *Why:* a status with no `dStatusEffect` record makes the combat timeline deref null and the
      **round never advances** — a hard freeze, 60 NREs/sec. Cost us a session-ending bug.
      *Check:* the donor-completeness test in `ClassForge.Core.Tests` fails the build if a status in
      any `statuses.json` has neither a donor nor an `IntentionallyIconless` record. Run the suite.
      *Also:* the donor must resolve in the **Unity asset index**, not merely exist in
      `StatusEffects.json` — a JSON-only donor reproduces the identical freeze.

- [ ] **A2. No pack-authored ability id can resolve onto a body.**
      *Why:* `GetCharacterAbilityRecord` is dereferenced **unguarded** at `CharacterVisualHelper.cs:2567`
      and ~18 further sites. A pack id that reaches a rendered actor is an instant NRE in combat.
      *Check:* every ability on a class weapon, partner or summon is a **shipped vanilla id**.
      Grep the pack's `items.json` ability bags against the game's `Abilities.json`.

- [ ] **A3. Every equippable has a `visualfallbacks.json` donor.**
      *Why:* an item with no 3D model throws in `UpdateCosmeticTint`. `CF_PACK_BALDURS` shipped with
      **no fallbacks file at all** and four modelless items.
      *Check:* count equippables vs fallback entries per pack; they must match.

- [ ] **A4. Recipe effects fail soft.**
      *Why:* `[ClassForge] Recipe effect failed (skipped, rest of plan continues)` is a PASS for
      crash-safety and a FAIL for correctness. Both must be reported, separately.
      *Check:* the fail-safety sweep (§D2) reports them as two different numbers.

- [ ] **A5. No unguarded deref of a record our content may not have.**
      *Known remaining:* `ToolTipHelper.cs:763` does the same `GetRecordByName(...).IconTexture` as the
      status freeze. Tooltip-reached so it cannot wedge a round, but it can throw.

## B. Visibility — can the player see it work

- [ ] **B1. Every ability, creature and item id resolves to a NON-EMPTY display name.**
      *Why:* `Lang.__t` returns the **raw key** on a miss, and several vanilla names are the empty
      string. This is the bug Ben reported first-hand (`ONLY_ATTACKUP_ALL_ATTACK` on screen).
      *Check:* script it — pack loc first, then the game's `Langs/en.json`. Must print `BLANKS: []`.

- [ ] **B2. Custom status ids start with a real `eStatusEffectsGroups` member.**
      *Why:* the icon strip iterates the **compiled enum** and asks whether a live status starts with
      a member name. A status that fails this is **invisible** even with a donor.
      *Check:* verify each prefix against the enum in the shipped DLL — not against the naming
      convention, which 35 of 189 vanilla ids violate.

- [ ] **B3. Every skill has both a name key and a description key.**
      Convention: `<ID>` = memey name, `UI_ENCYCLOPEDIA_<ID>` = plain accurate description.
      Descriptions must be **true to the resolved record** — resolve the `Inherits` chain first.

- [ ] **B4. Deleting a pack loc key does not restore an EMPTY vanilla name.**
      *Why:* a pack name overriding a vanilla id is global. Remove it and every vanilla user of that
      id goes blank. **When in doubt, keep the key.** Precedent: `PLANT_AOE_POISON_ATTACK` kept
      after being orphaned.

- [ ] **B5. Paired evidence for every player-facing feature.**
      A `proc` line in the log **and** a screenshot showing the effect. Log-only is the
      *invisible mechanic* false positive; screenshot-only can't say why. See VERIFICATION-METHOD §1.

## C. Multiplayer — will it desync

- [ ] **C1. No `Entity.Guid` as a seed input, ordering key, hash input, or persisted key.**
      GUIDs are minted per-peer by `Guid.NewGuid()`. The cross-peer identity is the **roster ordinal**.
      A GUID is legal only as a same-peer round trip.

- [ ] **C2. No ad-hoc `GameRandom` on a gameplay path.**
      Two sanctioned patterns only: roll from the shared `CombatState.Random`, or the zero-shared-draw
      derived stream (`LootGrantPatches`, with `pIgnoreMultiplayerStaticSeed: true`).
      `System.Random` / `UnityEngine.Random` are banned outright.

- [ ] **C3. `ProcChance` and `AiProcChance` never straddle 100.**
      A chance `>= 100` costs **zero** draws, `< 100` costs one — so a straddling pair makes draw
      count a function of a per-unit property. `W_PROC_CHANCE_DRAW_FORK` enforces it; keep it at zero hits.

- [ ] **C4. Any AI-targeting change is draw-count neutral.**
      `AIComponent.PriorityTargets` is drained **before** `GetPreferredTarget`, so issuing an order
      *skips* that draw. `AiDrawNeutrality` holds the count constant — anything new that steers
      targeting must go through it or prove neutrality with a draw-count test.

- [ ] **C5. Anything written to `CustomData` derives from replicated state.**
      `Thing.CustomData` **is** inside the vendor desync hash. A value only one peer can compute is a
      desync over cosmetics.

- [ ] **C6. No gameplay knob defaults differ per player, and every knob is parity-classified.**
      Gameplay knobs ride the parity payload; Presentation ones don't. There is a test asserting every
      knob's classification — keep it green.

- [ ] **C7. The pack determinism sweep passes.**
      `tools/run-determinism.ps1` — two **separate processes**, byte-identical reports. Catches the
      cross-peer variance (string hashing, dictionary order) that a single-process test cannot see.

- [ ] **C8. No pack id collides with a live vanilla id.**
      The merge is **adds-only**; a colliding record is refused, and if it weren't it would rewrite
      vanilla content. Use a free suffix.

## D. Integration — does it survive a real run

- [ ] **D1. The class loads and fights** without exception, on a party built through **character
      creation** (never `set_class`, which leaves vanilla names and voids screenshot identity).

- [ ] **D2. Fail-safety sweep: every ability, skill and item fired once**, asserting zero unhandled
      exceptions and that combat still advances. **Baseline the log first** — known-benign boot noise
      will otherwise drown real errors.

- [ ] **D3. Survives a wave boundary** — summons, partners and once-per-round budgets intact.

- [ ] **D4. Survives a defeat** — party wipe resolves cleanly, no wedge. *(Verified 2026-08-26.)*

- [ ] **D5. Recipe id naming contract intact.** `TrainerPartnerPersistence.ResolveSlot` parses
      `SKILL_CF_TRAINER_<LINE>_<STAGE>` and binds it to `ARM_ORIG_TRAINER_BALL_<LINE>`.
      **Deleting a stage is safe; renaming or renumbering silently breaks partner HP persistence.**

- [ ] **D6. Persistent state actually persists.** For Gary: the captured monster keeps its HP in the
      ball between fights (`CF_POKE_HP/MAXHP/DOWNED/CONFIG`, written by `ExecPersistCounter`, read
      back by `SeedPersistentCounters` before `ON_COMBAT_START`). **This is a feature Ben wants
      preserved — verify it after any Trainer/Gary change.**

## E. Suites — the cheap gate, run every time

- [ ] `ClassForge.PackCheck` → zero Errors
- [ ] `ClassForge.Core.Tests` → 0 failed
- [ ] `ClassForge.Recipes.Tests` → 0 failed
- [ ] `ClassForge.Plugin` compiles (`-p:ManagedDir=tools/bin/refs -p:BepInExDir=tools/bin/refs`)
- [ ] `Crucible.Core.Tests` → 0 failed
- [ ] `tools/run-determinism.ps1` → DETERMINISTIC

---

## Per-class sign-off

| | Vampiric | Pacifist | Trainer (Ash) | Gary | Chaos Mage |
|---|---|---|---|---|---|
| A crash safety | | | | | |
| B visibility | | | | | |
| C multiplayer | | | | | |
| D integration | | | | | |

A class ships when its column is complete. **An unchecked box is not a failure — it is an
unknown**, and an unknown is what this list exists to convert into a yes or a no.
