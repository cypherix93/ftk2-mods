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

### Wave-2 smoke checks (2026-08-06)

Three new systems shipped since (a)/(b)/(c) above were written — loot grants, Risky Blessings, encounter
modifiers — all offline-verified only (`docs/superpowers/handoffs/2026-08-06-eor-rehost-wave2-implementation-handoff.md`
has the full ledger). This section is additive: it assumes the install from (b) plus two new mods/packs.

**Additional install for this section:**
- **`blessings`** mod (`tools/deploy.ps1 -Mods devkit,classforge,summoner,blessings` or add it to an
  existing `-Mods` list) — stages `BepInEx/plugins/ftk2mods.blessings/` (plugin DLL +
  `ClassPacks/BLSS_PACK_EOR_BLESSINGS/`). **Blessings does nothing until you also point ClassForge at its
  pack**: edit `BepInEx/config/ftk2mods.classforge.cfg`, section `[Packs]`, key `AdditionalRoots`, to the
  absolute path of `BepInEx/plugins/ftk2mods.blessings/ClassPacks` (comma-separate if you already have a
  value there). ClassForge's `[Packs] AdditionalRoots` scans `<root>/<PackName>/pack.json`, same shape as
  its own `ClassPacks/` folder (`FTK2.ClassForge/src/ClassForge.Core/PackLoader.cs`); without this the
  roster-verification gate in `GrantAnchorPatches.EnsureRosterResolvesAgainstConfigs` fails closed and logs
  one `[Blessings] N roster TraitId(s) do not resolve in Env.Configs.Things -- this plugin is DISABLED for
  the session` error every launch.
- **Loot grants and encounter modifiers ship inside the `classforge` mod you already have** — no new
  plugin folder, just new BepInEx config keys under the `[Skills]` section of
  `BepInEx/config/ftk2mods.classforge.cfg` (below).
- Config keys below were grep-verified against the shipped code (`ClassForgePlugin.cs` `Config.Bind` calls,
  `BlessingsPlugin.cs` `Config.Bind` calls) as of the commits listed in the session handoff — spelling and
  casing are exact.

Order below: cheap single-player checks first (A, C, E), then their MP drills (B, D, F) last, since the
2-peer setup is the expensive part of any of these to arrange. Each check cites the spec open question
(OQ) it resolves or exercises, where one exists.

#### A. Loot grants — single-player

9. **Enable the feature.** Both `[Skills] EnableRecipeEngine = true` (default) and
   `[Skills] EnableLootGrants = false → true` in `ftk2mods.classforge.cfg` (the verb ships **dark by
   default** — this is the whole reason this check exists). Also set `[General] VerboseLogging = true` so
   the `CLASSFORGE_LOOT` log lines below actually print (`LootGrantPatches.LogGrantState`/`LogReceive` are
   both gated on `[General] VerboseLogging`, not a separate knob).
10. **Pick a Scavenger-trait character** (or any EOR class whose loadout can carry `TRAIT_SCAVENGER`) and
    win combats. Expect, per combat: a `[ClassForge][CLASSFORGE_LOOT]` debug line reading
    `GrantKey=<hash> ops=<N> opsHash=<hash8> status=unverified (single-player, no host push expected)` —
    **that exact "unverified (single-player...)" status is expected and correct in SP, not an error**; SP
    never sends a payload, it just computes+applies the same deterministic delta a host would. Roughly 1 in
    4 wins (`SCAVENGER` is `ProcChance: 25`, `FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/skillrecipes.json`
    `SKILL_CF_TRAIT_SCAVENGER_LOOT`) the loot screen shows an extra 8–20 gold or a COMMON-rarity HERB item
    on top of vanilla drops, and it's takeable like any other loot-screen item. **If it fails:** report
    whether the log line appears at all (feature not wired) vs. appears but the loot screen shows nothing
    extra (apply-side bug) vs. the extra item can't be taken (vanilla `_onTakeLootItem` issue, unrelated to
    this verb per spec §1.3).
11. **Diagnostic-only zero-draw check (optional, noisy).** `[Skills] DebugLogCombatRandomDraws = true`
    makes the same postfix call `GameRandom.LogCalls(true)` on the **shared** combat stream so every draw
    from it logs — confirms nothing in the loot-grant path is stealing a shared-stream roll (the whole
    point of Gate A's private-stream redesign). **Turn this back off after the check** — it spams a
    per-draw log line for the rest of the session once enabled (per its own config comment).

#### B. Loot grants — 2-peer MP (resolves V-1, V-3; exercises §7's failure-mode matrix)

12. **Baseline — matching install.** Both peers: identical build, `EnableLootGrants = true`,
    `VerboseLogging = true`, a Scavenger-trait character each. Host+join, win 5+ combats. Expect on
    **both** peers: identical loot lists (compare the two loot screens directly), a `CLASSFORGE_LOOT`
    receive-side line reading `status=verified` on the non-computing peer(s) once the host's push arrives
    (`LootGrantPatches.LogReceive`, the literal string is `"verified"`), and no vendor desync warning
    (`NetworkData.DoMonitorForDesyncs`) across the run. Record: **V-1** — did `GrantKey` (which folds in
    `CombatState.Random.Seed` per §4.2) agree on both peers every combat, i.e. zero `stale GrantKey` /
    `Mismatch` log lines? **V-3** — when a granted item is taken from the loot screen, does it disappear
    identically on both peers' inventories (confirms the take replicates by the same identity loot-grant's
    deterministic `Thing.Id` derivation relies on, §3.3)?
13. **Deliberate divergence drill.** With the session still running, hand-edit the **client's**
    `CF_PACK_EOR_CLASSES/skillrecipes.json` → `SKILL_CF_TRAIT_SCAVENGER_LOOT` → `ProcChance` from `25` to
    `100`, then trigger a config reload/rejoin so the edit takes. Next won combat with a Scavenger
    character: expect the loud `ClassForge LOOT-GRANT MISMATCH -- GrantKey=...` error banner
    (`LootGrantPatches.ReportMismatch`, both a local/remote `OpsHash` line and an "offending op" line) on
    whichever peer(s) detect the divergence, loot-grant SafeMode latching for the rest of that peer's
    session (`[Skills] EnableLootGrants` effectively goes inert there — no retro-mutation of the
    already-applied delta, per spec §7.5b), **and** — belt and suspenders — ClassForge's own
    pack-`dataHash` parity check should separately flag the edited pack (`ParityBridge`/`Block`, same
    mechanism as Wave-1 step 8). Report which of the two fired, or neither.
14. **Transport-kill check (if feasible).** If you have a way to block outbound `TransportService.Send`
    for one peer (dev knob / firewall rule on the DevKit transport port), do it, then win a combat on both
    peers. Expect: identical loot on both (deterministic mirror per Mode M — no host push required for
    correctness) but the `CLASSFORGE_LOOT` log shows `status=unverified (transport unavailable)` instead of
    `verified` on the peer that couldn't send/receive. This is the one place "unverified (transport
    unavailable)" is the correct string to see — contrast with check 10's SP string, which is worded
    differently on purpose.
15. **On a green V-1 pass:** the recorded follow-up is flipping `[Skills] EnableLootGrants`'s **default**
    from `false` to `true` in `ClassForgePlugin.cs` (currently ships dark specifically because this
    measurement was pending) — file that as the next code change, not a config change, since it's the
    shipped default that moves.

#### C. Blessings — single-player (resolves OQ5; exercises §8.3)

16. **Setup.** `blessings` mod installed + `[Packs] AdditionalRoots` pointed at its `ClassPacks` folder
    (see this section's preamble). In `BepInEx/config/ftk2mods.blessings.cfg`: `[General] Enabled = true`
    (default), `[General] VerboseLogging = true`, `[Blessings] Mode = BLSS_HOLLOW_VIGOR`.
17. **Start a new run.** Expect an info-level log line `[Blessings] GATE E: offline/single-player session
    -- granting unconditionally.` followed by `[Blessings] Blessing resolved: HOLLOW_VIGOR
    (TRAIT_BLSS_HOLLOW_VIGOR). Granted <N> party members, 0 already held it.` Every starting party
    character's sheet should show **Max HP +15** and **Health Regen −1** (observe over a rest/regen tick,
    since HRG isn't always a headline stat on the sheet). **If it fails:** check whether the roster-missing
    error logged instead (pack not discovered — re-check `AdditionalRoots`) or the grant log fired with 0
    granted (GATE E denied it — expected only online, see check 19).
18. **Repeat with `Mode = BLSS_ARCANE_HUNGER`.** Enter combat at full Focus: no grant (the recipe's
    `FOCUS_CURRENT LT MAX` condition blocks it). Enter combat below max Focus: **+1 Focus at combat start**,
    once per combat (`SKILL_BLSS_ARCANE_HUNGER`, `ProcChance: 100`, `ON_COMBAT_START` trigger). Also
    confirm **Talent −5** is visible wherever the game surfaces shop/service pricing.
19. **Save/reload.** Save mid-run, quit, reload. Expect: no second `Blessing resolved` grant line with a
    nonzero granted count (the trait-presence check makes re-grants a no-op — `GetTraits().Any(ConfigName
    == blessing.TraitId)`), stats unchanged (no double-application), latch (`GameRunData.Stats["
    BLSS_ACTIVE_HOLLOW_VIGOR"]`, not player-visible) still present if you can inspect a save.
20. **`Mode = Random`, same run seed twice.** Start, note the resolved blessing id from the log, restart
    the exact same seed (or same `MapGenSeed`/`ConfigName` pair): expect the **same** blessing id resolved
    both times (SHA-256 over `MapGenSeed|ConfigName`, zero RNG draws, §9.3).
21. **`Mode = Disabled`.** Zero delta: no grant log beyond `[Blessings] Mode=Disabled -- no blessing this
    run.`, no stat changes, run plays exactly like vanilla+ClassForge-without-Blessings.
22. **Hidden-trait visibility (OQ5).** With any blessing active, check every place traits normally render
    — loadout/party screen, character inspect, inventory/equipment panels, any "traits" tooltip list — and
    confirm `TRAIT_BLSS_*` (authored `Hidden: true` in `traits.json`) appears in **none** of them. Report
    exactly which screen(s), if any, leak it; that's the OQ5 answer this check exists to produce.

#### D. Blessings — 2-peer MP (resolves OQ3 in practice; exercises §9.6)

23. **Match.** Both peers same build+pack, `Mode = Random`. Start a run: both logs resolve the **same**
    blessing id from the same seed, both show `N` grants, both character sheets show identical modified
    totals. Fight one combat with `ARCANE_HUNGER` active: `+1 Focus` applies identically on both screens.
24. **First-online-session note (expected, not a bug).** On the very first `AdventureDirector.Initialize`
    of a fresh online session, you may see `[Blessings] GATE E: online multiplayer session -- no verified
    parity Match yet (handshake may not have completed) -- grant FAILS CLOSED this call.` with zero grants
    that call. This is fail-closed-by-design (`GrantAnchorPatches.GrantIsAllowedThisCall`, GATE E) — it
    self-heals at the **next** `Initialize` call (a save-load re-entry, or simply continuing play once
    DevKit's parity handshake resolves `HasVerifiedMatch()`), at which point the grant fires normally. Only
    report this as a real bug if the grant **never** lands after a full handshake settles.
25. **Mismatch drill — mode.** Host `Mode = Random`, client `Mode = Disabled`. Expect a `feature:Mode`
    parity divergence reported by both peers' Blessings registration, **both** peers' Blessings entering
    SafeMode (`[Multiplayer] OnParityMismatch = WarnAndSafeMode`, the Blessings-local default — distinct
    from ClassForge's own `Block`), **neither** peer granting a blessing, run proceeding blessing-less, and
    no vendor desync warning (both peers are now symmetric — nobody granted).

#### E. Encounter modifiers — single-player (resolves §13.1, contributes to §13.2/13.4; exercises §12.6)

26. **Force the roll.** `[Skills] DebugEncounterModifierChance = 100` in `ftk2mods.classforge.cfg`
    (default `-1` = off; valid range `0..100`; requires `EnableRecipeEngine = true`). This overrides only
    the generated selection recipe's `ProcChanceFormula` result — it does not change which modifier gets
    picked, only whether one is rolled at all.
27. **Fight a normal (non-boss/non-scourge/non-siege) encounter.** Expect a banner reading
    `"<Name> Encounter! <description>"` (`GameplayDialogViewHelper.ShowEventTitle`, `CF_ENCMOD_BANNER`
    format string, 4-second display) and every enemy in the fight — **including enemies that spawn in a
    later wave of the same combat** — carrying a `STATUS_CF_ENCMOD_<NAME>` status with a working
    icon/tooltip on the enemy panel (this persists for the rest of combat, unlike EOR's transient-banner
    original). With `[General] VerboseLogging = true` you should also see a
    `[ClassForge] proc SKILL_CF_ENCMOD_SELECT (Encounter modifier selection) owner=<guid> actions=<N>` line.
28. **MXHP modifiers (Swift −10%, Veteran +10%, Wealthy +10%, GlassCannon −20%, TreasureGuarded +15%).**
    Get one of these selected (re-roll fights until you see it, or narrow via save-scumming) and inspect
    the affected enemies' max HP against an un-modified enemy of the same type. Record: does the delta
    match EOR's floor-at-1 rounding (`Math.Max(1, round(|maxhp × pct| / 100))`, the §13.1 acceptance bar)
    and does a negative delta (Swift/GlassCannon) also cut **current** HP, or only the max? This single
    observation is what resolves spec open question §13.1 (the fallback design, `FlatValueFrom:
    "TARGET_MXHP_PCT"`, is already what's shipped — this check is confirming its runtime behavior matches
    EOR's floor/rounding, not choosing between designs).
29. **Cursed.** Get it selected; deal several hits to a Cursed enemy and confirm the `CURSE` status lands
    on the attacker roughly 1 time in 10 (`SKILL_CF_ENCMOD_CURSE_ON_HIT`, `ProcChance: 10`).
30. **Regenerating.** Get it selected; confirm the enemy heals **2 / 3 / 4** HP at the start of its turn
    depending on your party's average level (≤3 / 4–6 / ≥7 — `PARTY_AVG_LEVEL` bands, `SKILL_CF_ENCMOD_REGEN_TICK`),
    and **only while damaged** (no heal tick at full HP). This also empirically answers §13.2 — does the
    native `ON_TURN_START` proc path fire for AI/enemy entities at all? If the enemy never heals despite
    the status being visibly present, that's a "no" and the fallback anchor
    (`CombatHelper.TickActiveEntityCharacterStatus` postfix, already signature-verified per the spec) needs
    to be wired in as a follow-up — report a clean pass/fail here, don't guess.
31. **Icon check (§13.4).** While any modifier status is active, note whether its enemy-panel icon is the
    pack's authored icon or the game's generic missing-icon placeholder. Either is an acceptable v1 state
    (tooltip text carries the info either way) but the answer resolves §13.4.
32. **Exclusions.** Fight a boss fight, a "SCOURGE"-named encounter, and a siege/special encounter (any
    encounter carrying the `BOSS`/`SPECIAL`/`SIEGE` `EncounterComponent` properties). Expect: **no** banner,
    **no** `STATUS_CF_ENCMOD_*` on any enemy. Note for the record: there is currently **no dedicated
    "excluded" log line** — the engine's exclusion behavior is a structural zero-draw early-return
    (`RecipeDispatcher.cs`, "excluded/clamped fight — zero draws, full stop") with nothing printed even at
    Verbose. The observable is the *absence* of the banner and the *absence* of the
    `Encounter modifier selection` proc line from check 27 — don't wait for text that isn't there.
33. **Knob back to default.** Set `DebugEncounterModifierChance = -1` and fight several more normal
    encounters. Expect the real formula's rates: base 10% (party avg level ≤2) / 20% (3–5) / 30% (6+),
    ±5 for AMBUSH/QUEST_TARGET encounter properties where applicable, clamped to a max of **35%** overall
    (`ProcChanceFormula { Min = 0, Max = 35 }`, `ModifierRecipeGenerator.BuildProcChanceFormula`) — you
    won't see this converge in a handful of fights, but confirm the banner does *not* fire on every single
    normal encounter once the knob is off (a sanity check that the debug override actually stopped
    overriding).

#### F. Encounter modifiers — 2-peer MP (exercises §12.7-8)

34. **Match.** Both peers with the pack, `DebugEncounterModifierChance = 100` on both for a fast baseline
    (then repeat once at default rates for a longer soak, if time allows). Fight several eligible combats:
    confirm the **same** modifier is selected on both peers each fight (compare logs), identical enemy
    HP/status panels, and the vendor desync monitor stays quiet across full combats including multi-wave
    fights.
35. **Reward halves.** For Veteran (+10% XP), Wealthy (+25% gold), Cursed (+10% XP), and Treasure-Guarded
    (+20% extra-loot chance) fights: confirm the post-combat loot/reward screen reflects the bonus
    identically on both peers (same gold total, same XP total, same extra-item-or-not on Treasure-Guarded).
    This exercises the M-EM4 interface into the loot-grant verb (§11) — if loot grants (section B above)
    aren't enabled (`EnableLootGrants = false`), the reward-halves paths that ride the loot verb's
    reward-ops will no-op with a one-time log line instead of applying; enable loot grants first if you
    want this check to actually exercise the bonus math.

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
