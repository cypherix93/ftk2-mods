# EOR-rehost operator handoff (2026-07-25)

**Branch:** `engine/eor-rehost` · **Audience:** whoever installs this build against a real game copy and
runs the first in-game smoke test. Nothing here has been verified in-game yet — every claim below is
offline-verified (unit tests, sim harnesses, schema validators, a reference-assembly build) unless marked
otherwise. If a step in this doc contradicts what you actually see in-game, trust the game and file it as a
new finding; this doc is a starting map, not a guarantee.

**Owning docs, if you need more than this handoff gives you:** `docs/MULTIPLAYER.md` (repo-wide MP rules),
`docs/research/eor-rehost-mp-review.md` (the adversarial review whose fixes are already in this build),
`docs/research/eor-rehost-coverage-matrix.md` (every EOR mechanic's disposition), `FTK2.ClassForge/SPEC.md`,
`FTK2.Summoner/SPEC.md`, `FTK2.DevKit/SPEC.md` (updated 2026-07-25 to describe this build).

---

## (a) What was built

| Component | Status | Test count |
|---|---|---|
| `FTK2.DevKit` — `ParityService` (R1 parity enforcement: registration, ProtoBuf-channel transport, hash/compare, mismatch policy) | Implemented, offline-verified | `DevKit.Core`: **91** |
| `FTK2.ClassForge` — M1 pack loader + M2 trait injection (no-bridge, `TRAIT_`-prefix + loadout-pool injection) + M3 skill-recipe engine (v1.1 vocabulary: 15 triggers / 23 conditions / 8 effects) | Implemented, offline-verified | `ClassForge.Core`: **21** · `ClassForge.Recipes`: **149** |
| `FTK2.Summoner` — M0 `Characters.json`/`Followers.json` pack loader (adds-only, parity-registered) | Implemented, offline-verified | `Summoner.Core`: **16** |
| `tools/eor_import.py` — EOR → Armory/ClassForge/Summoner converter (items, classes, followers) | Implemented, deterministic (byte-identical across two processes at different `PYTHONHASHSEED` values) | `python -m pytest tools/tests`: **114** |
| Converted content packs | `CF_PACK_EOR_CLASSES` (31 classes, 20 traits, 48 recipes), `SMN_PACK_EOR_MERCS` + `SMN_PACK_EOR_PETS` (200 followers combined), `ARM_EOR_ITEMS`/`ARM_EOR_STARTERS` (417 items) | Schema-validated; vocab regenerated clean (2095 chars / 1847 things) |
| Coverage matrix (all 74 EOR class-skill/trait/affix mechanics dispositioned) | Complete | PORT 47 / PORT-MODIFIED 20 / PARK 7 |
| Reference build | All 10 changed/new C# projects build clean against the snapshotted `tools/bin/refs/` reference assemblies | 10/10 pass |

**What "offline-verified" means and doesn't mean:** every number above comes from a unit test, a sim/CLI
harness, a schema validator, or a `dotnet build`/`pytest` run — none of it has touched a running copy of the
game. The Wave-4 adversarial MP-correctness review (`docs/research/eor-rehost-mp-review.md`) additionally
read every new line of code against the decompile and found (and this build already fixes) several
transport/parity bugs that no unit test alone would have caught — treat that review as complementary
evidence, not a substitute for the smoke test below.

---

## (b) Install for smoke test

### Step 0 — BepInEx itself

**A fresh For The King II install has no BepInEx at all.** You need BepInEx 5.4.23 in the game folder before
any of our plugin DLLs will load. Two ways to get it:

- **From an existing mod package** — if you have (or can get) the "Enhanced Overhaul Revamped" release
  package used elsewhere in this repo's research (`Release 29 0.7.0.60`), its `BepInEx/` folder already
  contains a working BepInEx 5.4.23 core. Copy the whole `BepInEx/` folder (at minimum `BepInEx/core/` and
  the `winhttp.dll`/`doorstop_config.ini` bootstrap files at the game root) into your target game install.
- **Fresh download** — get BepInEx 5.4.23 (the exact version, not a newer 5.x or BepInEx 6) for the game's
  platform (Windows, Unity Mono) from the official BepInEx releases and extract it into the game root per its
  own install instructions. Launch the game once with no other plugins installed to confirm BepInEx itself
  loads (a `BepInEx/LogOutput.log` file appears) before adding anything else.

### Step 1 — the three engine plugins

Each plugin is two layers: a `.Core`/host-agnostic assembly (compiled logic, no BepInEx/Unity/game
references) and a `.Plugin` assembly (the actual BepInEx plugin — Harmony patches + game adapter). **Both
must ship together in the same folder**, because the `.Plugin` assembly has a project reference to its
`.Core` assembly and BepInEx loads by scanning `BepInEx/plugins/**` for plugin DLLs, resolving their
dependencies from whatever else is sitting next to them.

Build them first if you haven't (`-c Release`, referencing `tools/bin/refs/` per
`docs/research/build-template-notes.md` — the exact commands are recorded there for each plugin):

```
dotnet build FTK2.DevKit/src/DevKit.Plugin -c Release -p:ManagedDir="<repo>/tools/bin/refs" -p:BepInExDir="<repo>/tools/bin/refs"
dotnet build FTK2.ClassForge/src/ClassForge.Plugin -c Release -p:ManagedDir="<repo>/tools/bin/refs" -p:BepInExDir="<repo>/tools/bin/refs"
dotnet build FTK2.Summoner/src/Summoner.Plugin -c Release
```

Then create one BepInEx plugin folder per mod under `<game>/BepInEx/plugins/` and copy in:

**`BepInEx/plugins/ftk2mods.devkit/`**
- `FTK2.DevKit/src/DevKit.Plugin/bin/Release/net472/FTK2.DevKit.dll` — the BepInEx plugin itself
- `FTK2.DevKit/src/DevKit.Core/bin/Release/netstandard2.0/ftk2mods.devkit.dll` — **required alongside it.**
  This is not optional: every sibling mod's `ParityService`/`DevKitLog`/`PatchRegistry` reflection lookup
  resolves the assembly-qualified name `"FTK2Mods.DevKit.ParityService, ftk2mods.devkit"` — if this DLL is
  missing, every other mod's parity registration silently no-ops and R1 enforcement is invisible for the
  whole session, not just degraded.
- `FTK2.DevKit/data/` (i.e. `Macros.json`, `LogConfig.json`) copied into this same folder, so the plugin's
  own `data/Macros.json`/`data/LogConfig.json` paths resolve next to its DLL.

**`BepInEx/plugins/ftk2mods.classforge/`**
- `FTK2.ClassForge.dll`, `ClassForge.Core.dll`, `ClassForge.Recipes.dll` — all three, from
  `FTK2.ClassForge/src/ClassForge.Plugin/bin/Release/net472/` (a single `dotnet build` on the Plugin project
  already places all three side by side in that output folder — copy the whole folder's DLLs, don't hand-pick).
- `FTK2.ClassForge/data/ClassPacks/` copied to `.../ftk2mods.classforge/ClassPacks/` (note: **not** under a
  `data/` subfolder — ClassForge's loader looks for `<plugin folder>/ClassPacks/<PackName>/` directly). Bring
  both `CF_PACK_BALDURS` (hand-authored example, 3 classes) and `CF_PACK_EOR_CLASSES` (31 EOR-derived
  classes) if you want the full smoke test below.

**`BepInEx/plugins/ftk2mods.summoner/`**
- `FTK2.Summoner.dll`, `Summoner.Core.dll` from `FTK2.Summoner/src/Summoner.Plugin/bin/Release/net472/`.
- `FTK2.Summoner/data/` copied to `.../ftk2mods.summoner/data/` (Summoner's loader looks for
  `<plugin folder>/data/FollowerPacks/<PackName>/`). Bring `SMN_PACK_EOR_MERCS` and `SMN_PACK_EOR_PETS` for
  the shop-appearance check below.

### Step 2 — Armory pack (data-only, no plugin)

Armory ships zero C# by design — follow `FTK2.Armory/INSTALL.md`'s pattern exactly (it's a short, manual
copy/merge doc, not automated by anything in this repo): copy `FTK2.Armory/data/Things/*.json` (including the
new `ARM_EOR_ITEMS.json`/`ARM_EOR_STARTERS.json`, 417 items total) into
`<game>/BepInEx/plugins/EnhancedOverhaulRemixed/CustomItems/Things/` (the EOR-drop-in path INSTALL.md
recommends), merge `FTK2.Armory/data/VisualFallbacks.json`'s keys into that install's existing
`VisualFallbacks.json`, and merge `FTK2.Armory/data/Localization/en.json` into its `Localization/en.json`.
Icons copy the same way. **This step needs an existing EOR install already on the machine** (INSTALL.md's
recommended path) — the fallback path (edit the game's own `Configs/JSON~/Things/` directly) works but has no
visual-fallback layer; only use it if you have no EOR install to drop into.

---

## (c) 15-minute smoke script

Run this in order; each step should take well under a minute except where noted. Have the BepInEx console
window (or `BepInEx/LogOutput.log`) visible throughout.

1. **Launch, single-player.** Start the game. In the BepInEx console, confirm one **"Target found: X"**
   line per Harmony patch target for each of the three plugins (DevKit, ClassForge, Summoner) — this is the
   `docs/CONVENTIONS.md` logging convention every patch install follows. Any **"Target NOT found"** line means
   a patch target's method signature has drifted since this build; note which mod and which target, but a
   single missing target should not crash the plugin (fail-safe by design) — keep going and note it as a
   finding.
2. **Class-select shows 31 `CF_EOR_*` classes.** Start a new adventure, reach character customization.
   Confirm the class list includes the 31 EOR-derived classes (`CF_EOR_ARCANIST` … `CF_EOR_WIZARD`,
   displaying by their localized names, not raw ids) alongside vanilla classes and (if `CF_PACK_BALDURS` is
   also installed) the 3 Baldur's Gate classes.
3. **Start a run with one EOR class.** Pick any one (e.g. the Hexblade-flavored or Ranger-flavored class —
   whichever reads as recognizable). Confirm it starts with the stats/gear its `classes.json` entry
   specifies and its starting weapon's abilities are usable.
4. **Trait appears in loadout.** At the trait-pick step, confirm at least one `TRAIT_CF_*` pack trait appears
   in the pick list (icon/name/description populated, not raw ids) and can be assigned. This exercises the
   no-bridge/loadout-pool-injection mechanism (`FTK2.ClassForge/SPEC.md` §3 point 2) for the first time
   in-game — **this is the single highest-value check in this whole script**, since it's the one mechanism
   that could only ever be exercised in a real party-setup screen, not in a unit test.
5. **Combat: verbose recipe log procs.** Turn on `[Skills] SkillRecipeVerboseLogging` (BepInEx config), fight
   one combat. Confirm the log shows recipe trigger/condition/effect lines (e.g. an `ON_ABILITY_USED` or
   `ON_CRIT` recipe firing with its evaluated conditions) at least once. If your class doesn't have an
   `Enabled` recipe that's easy to trigger, cast a few different abilities and land a crit if you can — 48
   recipes ship in `CF_PACK_EOR_CLASSES`, most classes have at least one recipe reachable within a couple of
   turns.
6. **Pets/mercs appear in shops.** Visit a town (pet shop / mercenary guild, whichever the game's UI calls
   it). Confirm `SMN_PACK_EOR_MERCS`/`SMN_PACK_EOR_PETS` entries appear as recruitable options alongside
   vanilla followers — this is the Summoner M0 loader's whole job, merging `Followers.json` entries into the
   normal recruitment flow with zero new UI.

### MP handshake check (2 peers)

7. **Baseline — matching install.** Both peers install the identical plugin set + pack set from steps (b)
   above, at the same versions. Host a session, have the client join. Confirm **no** parity mismatch
   banner/log appears on either peer, and (if DevKit's console is enabled) `dk_dump_parity` on each peer
   shows matching `dataHash`/`enabledFeatures` for every registered mod.
8. **Mismatch test — disable one pack on one peer.** With the session still running (or a fresh one),
   disable one pack on the client only (e.g. flip `[Packs] CF_PACK_EOR_CLASSES.Enabled = false` in
   ClassForge's config, or remove the pack folder) and trigger a re-handshake (rejoin, or a hot-reload if
   wired). Confirm: a mismatch banner names the correct mod and divergence kind (likely `Data` or `Features`,
   since the pack-id list itself changes); ClassForge's `[Multiplayer] OnParityMismatch = Block` engages —
   remembering the **honest semantics** documented in `FTK2.ClassForge/SPEC.md` §9.5: content already merged
   before the mismatch was detected stays merged as inert data, but the recipe engine / trait-loadout
   injection / class-select injection all switch off on the diverged peer. You should observe: no new pack
   traits offered in future loadout screens on the blocked peer, and (if you can trigger one) no further
   recipe procs from that peer's characters.

---

## (d) Known limitations & follow-ups

- **In-game verification is pending, full stop.** Every number and mechanism above is offline-verified only.
  Treat every step in (c) as genuinely unverified until you've run it — this handoff's job is to make that
  first run efficient, not to claim it already happened.
- **Online trait gating (fail-closed) leaves a real window.** Day-one MP pack-trait injection may legitimately
  be absent from the very first loadout-pool build of an online session, because the parity handshake
  currently resolves *after* the point where the trait-loadout pool gets built and serialized
  (`AdventureDirector.Initialize` vs. `PartyManagementDirector`). This is by design (fail-closed, not a bug),
  but it means step 4 of the smoke script may behave differently in SP (works immediately) vs. the very start
  of an MP session (may need a rejoin/rebuild before pack traits appear). The real fix — moving DevKit's
  handshake anchor to `AdventureSelectionDirector`, which runs earlier — is a follow-up, not done in this
  build. See `FTK2.ClassForge/SPEC.md` §9.1.
- **The EOR-identical parity channel (`[Multiplayer] ParityChannel = EorTownServices`) exists as an escape
  hatch but is not the default and is not proven inert.** DevKit defaults to `DebugThing`
  (`eAdventureActions.DEBUG_GET_SPECIFIC_THING`), which is provably a no-op channel (no case in
  `_handleNetworkAction`'s switch, falls to a `default:` arm that logs one cosmetic
  `Debug.LogError` per peer and nothing else). Only switch to `EorTownServices` if a future game build starts
  filtering unhandled action types and `DebugThing` stops working — and understand it rides the same channel
  EOR itself uses for real gameplay actions (`TOWN_SERVICES`), which is not inert if left running.
- **A rebalance pass is pending**, using `tools/out/eor-import/eor-class-stats-report.md` (a generated,
  gitignored sidecar comparing the 31 EOR-derived classes' stat envelope against vanilla — several stats
  read "ABOVE VANILLA MAX", e.g. `LCK` 50–95 vs. vanilla flat 50). The converter makes zero balance changes by
  design; whether/how much to rebalance is a human design decision for later, not something this build
  attempted.
- **Re-import procedure, if you need to regenerate a pack:** `python tools/eor_import.py` regenerates Armory
  and Summoner packs byte-identically from a fresh EOR package, but **ClassForge's `CF_PACK_EOR_CLASSES` pack
  carries hand-authored content** (`traits.json`, `skillrecipes.json`, and hand-edited `Passives`/localization
  entries in `classes.json`) that the converter does not own and will not reproduce on a bare re-run — it
  refuses to overwrite by default. Pass `--force-classes` only if you've confirmed the hand-authored content
  is backed up or re-mergeable; the flag exists precisely because a careless re-run would silently delete 38
  classes' signature passives and 40 trait localization keys.
- **`ON_DODGE` / DUELIST is a v1.2 candidate, not shipped.** A direct decompile check found
  `eAbilityResults.DODGED` does exist (contradicting the coverage matrix's original "no dodge signal" finding)
  — see `docs/research/eor-rehost-coverage-matrix.md`'s "Implementation updates" section. This does not
  retroactively add an `ON_DODGE` trigger to this build; it just removes the stated blocker for a future wave.
- **The `DEBUG_GET_SPECIFIC_THING` parity channel produces one cosmetic log line per parity handshake, on
  every peer** (`"... is not accounted for"` from the game's own `_handleNetworkAction` default arm). This is
  expected and harmless — it is the price of using a provably-inert channel — but don't mistake it for an
  error; it fires on every successful handshake, not just failures.

## (e) What was deliberately dropped

Per the project charter (`docs/superpowers/plans/2026-07-25-eor-rehost-engine-charter.md`) and the content
audit (`docs/research/eor-0760-content-audit.md` §6 row 9, §7), the following EOR systems were **not**
re-hosted and are not planned for a near-term wave — they're complete systems in their own right (some of
them, per the audit, home to the worst of EOR's original MP bugs) with no current spec owning them:

- **Risky Blessings** (15 blessing effects, `GameRunData.Stats` keys)
- **Nemesis/Revenge foes** (the enemy-memory/grudge system)
- **Encounter modifiers** (10 modifiers)
- **Campaign mutators** (8 run-level toggles)
- **World events** (10, reusing vanilla skill-encounter machinery)
- **Town specialists** (4, fully code-driven)
- **Sanctum status system** (16+15 custom statuses)
- **Telemetry / version-check / debug toolkit / camera tweaks** (EOR's `DiagnosticReporter` — hooks Unity
  logs and files a GitHub issue with per-player inventory dumps, display names, and connection ids by
  default; explicitly not ported, and `FTK2.DevKit` never talks to the network at all, by design)

Also parked within the systems that *were* re-hosted (full list and unlock conditions:
`docs/research/eor-rehost-coverage-matrix.md` §7, `FTK2.ClassForge/SPEC-DELTA-v1.1.md` §7): combat-time gold
grants, `SUPPRESS_CONSUME` (ARCANE_MEMORY/OF_SPELLKEEPING's code half), `DAMAGE_TAKEN_MULT` (SHIELDBEARER's
code half), `STEAL_STATUS` (THIEF), `CLEANSE_RANDOM_STATUS` (PALADIN), `CONDITIONAL_STAT_MODIFIER`
(PACK_TACTICS/ARCANE_FOCUS's code halves — both ship as unconditional flat stats instead), and the mastery
meta-progression system (per-class XP/rank, persisted to the player profile — out of scope for any current
engine's state model, and explicitly offline-only in EOR itself).

If any of the dropped-outright systems turn out to matter to players, re-specifying them is the intended path
— they were dropped as a scoping decision for this wave, not ruled out permanently.
