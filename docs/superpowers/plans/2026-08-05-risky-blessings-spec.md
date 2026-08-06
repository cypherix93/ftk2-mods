# FTK2.Blessings — "Risky Blessings" re-host SPEC

**Plugin GUID:** `ftk2mods.blessings` · **Id prefix:** `BLSS_` · **Priority:** P2 ·
**Status:** DRAFT 2026-08-05 (design; no code exists)

> **Grounding notice.** Every EOR behavior claim below is cited to
> `tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs` (line numbers re-verified for this
> spec, not inherited from earlier scouting). Every game-surface name is cited to
> `docs/research/game-patch-surface-notes.md` (PSN), `docs/research/game-code-reference.md`,
> `docs/research/enum-ground-truth.md`, or `FTK2.ClassForge/SPEC.md` (which merged `SPEC-DELTA-v1.1.md`).
> **Caution on every PSN citation:** `tools/bin/refs` was refreshed to the **7/31 game build**
> (FTK2.dll 6,597,120 bytes — memory note `eor-0760-analysis.md`), and that update already changed at least
> one hook shape (`RenderClassList` gained a 5th bool). PSN line numbers predate 7/31; §8.1 lists the
> signatures that must be re-verified against the fresh refs before implementation. Where this spec could
> not verify something, it says so and tracks it in §8.1/§11 — nothing is silently assumed.

## 1. Purpose & scope

Re-host EOR's **Risky Blessings** system — a once-per-run, party-wide bargain (a stat bonus paired with a
stat downside, weighted-rolled from a fixed 15-entry table) — on our mod stack, MP-first per
`docs/MULTIPLAYER.md`. The EOR original was dispositioned "dropped" by the engine charter
(`eor-rehost-coverage-matrix.md` L199, content-audit §2.6: "DLL-only gameplay systems, no home in current
specs"); this spec gives it a home as a **thin orchestrator plugin + a standard ClassForge content pack**.

**In scope (v0):**
- The 15-blessing table transcribed from EOR (§7), shipped as hidden `TRAIT_BLSS_*` Things in a ClassForge
  pack, granted to every party player character.
- A deterministic, run-start offer flow driven by a config knob (no in-game offer UI in v0 — §3.4).
- One skill recipe (`ARCANE_HUNGER`'s start-combat Focus rider) on ClassForge's shipped recipe engine.
- Full MP posture (§9), fixing all three of EOR's identified desync mechanisms (§3.1).

**Out of scope (deliberately):**
- The FORBIDDEN_SANCTUM world-event encounter surface (EOR L5104, L11739–11833). Mid-run map encounters are
  a separate future system; v0's offer surface is run start only.
- An in-run accept/decline dialog with host→client sync (`BLSS_SYNC_BLESSING_V1`) — v1 candidate, named in
  §9.4 so it isn't a scope surprise.
- Blessing removal/cleansing mechanics (EOR has none either — one blessing, permanent for the run).
- EOR's aspirational non-stat downsides. EOR itself shipped flat-stat fallbacks (its own `DownsideText`
  strings say so: "healing reduction **fallback**", "elite damage **fallback**" — L5144, L5149). We port
  the *shipped* behavior (pure stat modifiers + one Focus rider), not the aspirational text.

## 2. Player-facing behavior

- The party (out-of-band, e.g. over voice) agrees on a blessing mode before starting a run: off, random, or
  a specific blessing. Each player sets the same `[Blessings] Mode` config value; the parity handshake
  enforces that they actually match (§9).
- On adventure start, if a blessing resolves, every party player character silently gains one hidden trait
  carrying the bonus and the downside (e.g. Blood Price: +2 Physical Damage, −10 Max HP, party-wide). The
  BepInEx log records blessing id, resolved source (config/random), and per-character grant.
- Character-sheet stat totals reflect the blessing immediately (native equipped-trait stat path). v0 does
  **not** add a breakdown-row attribution — totals move without a labeled source line (honest gap; v1
  candidate via a `UIHelper.GetBreakdownStats` postfix, the exact surface EOR used at L22572–22604).
- `ARCANE_HUNGER` additionally grants +1 Focus to each blessed character at combat start when below max
  Focus (identical to the shipped `TRAIT_PREPARED` behavior).
- One blessing per run, maximum, ever (EOR parity: `TryApplyOffer` refuses a second, L713–722). It persists
  through save/load because it is an ordinary `Thing` in the character's `Things` list.
- With the mod absent or `Mode = Disabled`, nothing changes — no pack merge side effects beyond inert
  `Configs.Things` entries (same posture as any ClassForge pack).

## 3. Architecture

### 3.1 What EOR shipped, and exactly why it desyncs (verified)

EOR's implementation, all in `Plugin.cs`:

- **Data:** `RiskyBlessingDefinition[15]` static table (L5138–5155); definition shape
  `(Id, DisplayName, Description, BonusText, DownsideText, Weight, PartyWide, CanStack,
  GrantStartCombatFocus, Tags[], params (string,int)[] StatModifiers)` (L305–343). All 15 are
  `PartyWide: true, CanStack: false`; only `ARCANE_HUNGER` has `GrantStartCombatFocus: true`.
- **Active-state storage:** one flag key `"EOR_RISKY_BLESSING_ACTIVE_" + Id` = 1 in `GameRunData.Stats`
  (L648–651, `ApplyBlessing` L874–886). Active lookup = first table entry whose key is set (L846–853).
- **Effect application:** a postfix on `CharacterHelper.GetStat` (5-arg overload, patched at L8520–8527 /
  L9351) calls `RiskyBlessingManager.ApplyPartyStatModifiers(entity, statKey, ref result)` (L789–809,
  wired at L22532) — for every `PlayerComponent` holder, adds each `(stat, value)` modifier whose key
  string-matches the queried stat. `ARCANE_HUNGER`'s Focus rider is in the `CombatHelper.SetInitiative`
  postfix (patch target L8840, body L22830–22869): `GrantsStartCombatFocus(entity)` (L811–823) OR'd with
  `TRAIT_PREPARED`/`OF_FOCUS`, gated on `CharacterHelper.GetFocusMissing > 0`, then
  `CharacterHelper.AddFocus(entity, 1, results)` (L22849–22858).
- **Offer:** the FORBIDDEN_SANCTUM encounter panel (L11739–11833) rolls an offer **at UI render time on the
  viewing peer only** and caches it client-side per encounter GUID (`RiskyBlessingOffersByEncounterGuid`,
  L5046, L11746–11750). The roll is weighted (`PickRandomBlessing`, L830–844: `random.NextInt(1, Σweights,
  inclusive)` then walk the table in declaration order).

**The three desync mechanisms (what our design must not reproduce):**
1. **Non-shared offer RNG.** `CreateOffer` (L688–699) resolves RNG via `TryResolveGameplayRandom`
   (L10442–10472): prefers `CombatState.Random` (absent on the overworld), else **constructs a fresh
   seed-derived `GameRandom`** (L10474+) from `MapGenSeed|ConfigName|ActiveMapID|EncounterGUID|RoundCount`
   — peer-local values that are not guaranteed synchronized at roll time, and only the peer whose UI
   rendered ever rolls at all.
2. **Client-local acceptance.** Accepting writes `GameRunData.Stats` on the accepting client only
   (L11805 → `TryApplyOffer` L701 → `ApplyBlessing` L880); no network send exists anywhere in the flow.
   From that instant, the accepting peer's `GetStat` postfix inflates every party stat and the other peers'
   does not.
3. **Stubbed MP guards.** EOR's own `ShouldDisablePreparedFocusForMultiplayer` /
   `ShouldDisableVolatileCombatMutationsForMultiplayer` both `return false` unconditionally
   (L14913–14921) — the guards that were supposed to fence this off in MP are dead.

Notably, EOR's *parity* layer knew blessings mattered: its `EOR_DEF` definitions-hash covers the blessing
table (`"blessing:" + Id|Weight|PartyWide|CanStack|GrantStartCombatFocus`, L7725–7737). We keep that idea
(§9) and fix the runtime.

### 3.2 Our design in one paragraph

A blessing is a **hidden `TRAIT_`-prefixed `ThingConfig`** whose `Equippable.Stats` carries both the bonus
and the downside, granted once to every party player character through the native, public
`CharacterHelper.GiveTrait(Entity, string)` (PSN "Trait bridge surface", L197–215: `GiveTrait(Entity,
string)` → `InventoryHelper.CreateThing` → `Things.Add`). This rides ClassForge's proven no-bridge trait
mechanism (`FTK2.ClassForge/SPEC.md` §3 point 2, resolving old OQ#1: the `TRAIT_` ConfigName prefix *is*
the mechanism; `eTraits` is dead code). Stat effects then flow through the untouched native
equipped-trait `GetStat` path — a `Slots: []`, `Hidden: true` trait contributes its `Equippable.Stats`
with **zero patches** (ClassForge SPEC §4.3/§9.2, `eor-trait-mechanism.md` §1). `ARCANE_HUNGER`'s Focus
rider is a ClassForge skill recipe (`ON_COMBAT_START` + `STAT_CHANGE{FOC}`), byte-for-byte the shape of
the shipped `SKILL_CF_PREPARED` (`FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/skillrecipes.json`
L656–673). The only new *code* is a thin orchestrator: resolve which blessing (deterministically), grant
it at a safe point, latch it, register parity.

Contrast with EOR: no `GetStat` postfix (native path instead), no UI-time RNG (hash-derived resolution),
no client-local acceptance (symmetric deterministic resolution on every peer), no run-scoped static caches
(EOR's `RiskyBlessingOffersByEncounterGuid` never clears across runs).

### 3.3 Content packaging — decision: a standard ClassForge pack + a `blessings.json` sidecar owned by this plugin

**Decision.** Content ships as ClassForge pack `BLSS_PACK_EOR_BLESSINGS` (installed like any pack;
discoverable via ClassForge's `[Packs] AdditionalRoots` pointing at the Blessings plugin folder):

```
ClassPacks/BLSS_PACK_EOR_BLESSINGS/
  pack.json                 — ClassForge manifest (loadOrder, dependencies: [])
  traits.json               — 15 hidden TRAIT_BLSS_* ThingConfigs (ClassForge merges → Configs.Things)
  skillrecipes.json         — SKILL_BLSS_ARCANE_HUNGER (ClassForge recipe engine executes it)
  localization/en.json      — names/descriptions/bonus/downside strings (EOR key-shape precedent L2200–2205)
  blessings.json            — the Blessings plugin's OWN registry (offer table); ClassForge ignores it
```

This is legal today with zero ClassForge changes: `PackContentParser` parses only its five named files
(`ClassForge.Core/PackContentParser.cs` L22–24 + items/skillrecipes) and does not reject unknown files.
Two free properties fall out: (a) the trait configs are merged, id-validated (`^TRAIT_[A-Z0-9_]+$`
hard-enforced), and hot-reload-idempotent via ClassForge's proven pipeline; (b) **the entire pack —
including `blessings.json` — is covered by ClassForge's `dataHash`** (SHA-256 over every pack file except
`localization/**`, ClassForge SPEC §3) and therefore by ClassForge's `Block` parity posture. Blessing-table
divergence between peers is a blocked join before this plugin runs any logic at all.

**Rejected alternatives:**
- *Extend ClassForge with a native `blessings.json` file type + offer engine.* ClassForge's charter is a
  content-pack engine for classes (SPEC §1); run-lifecycle orchestration (one-per-run latch, offer
  resolution, party-wide grant timing) doesn't belong in its loader, and ClassForge M1–M3 are implemented
  and frozen — reopening its file contract for a P2 feature is bad economics. Its `Block`-only SafeMode
  posture is also wrong for blessings (§9.5: a not-yet-granted blessing is cleanly skippable).
- *Fully standalone plugin with its own `Configs.Things` merge.* Duplicates the proven merge/validation/
  parity machinery and creates a second writer into `Configs.Things` (collision surface, hash-ordering
  questions). No benefit.

**Dependency posture:** data-level only, fail-closed. `FTK2.Blessings` has **no compile-time reference to
ClassForge**. At its anchor point (§3.5) it verifies every `TRAIT_BLSS_*` id in its `blessings.json` roster
resolves in `Configs.Things`; any miss (ClassForge absent, pack disabled, roster/pack drift) → the plugin
disables itself for the session with one loud log line. `ARCANE_HUNGER` additionally requires the recipe
engine; if ClassForge is present the recipe rides it automatically (the recipe lives in the pack), and if
it is not, the whole plugin is already disabled by the same check.

### 3.4 Offer flow v0 — deterministic, run-start, config-driven (honest scope)

**There is no in-game offer UI in v0.** The "offer" is a config knob resolved identically on every peer:

- `[Blessings] Mode` (string, default `"Disabled"`): `Disabled` | `Random` | a literal blessing id
  (e.g. `BLSS_BLOOD_PRICE`).
- Resolution runs once per run at the grant anchor (§3.5):
  - `Disabled` → no-op.
  - `<BlessingId>` → that blessing (unknown id → log Error, treat as Disabled — never guess).
  - `Random` → deterministic weighted pick with **zero RNG draws**:
    `h = SHA256("BLSS_OFFER_V1|" + GameRunData.MapGenSeed.ToString(InvariantCulture) + "|" +
    GameRunData.ConfigName)`; take the first 8 bytes as a big-endian `uint64`;
    `roll = (h mod ΣMax(1,Weight)) + 1`; walk the roster **in `blessings.json` authored order**
    subtracting `Max(1, Weight)` until `roll <= 0` (same walk semantics as EOR's `PickRandomBlessing`
    L830–844, minus the `GameRandom`). A pure function of the shared run identity — R2 by construction, and
    it cannot perturb the game's lockstep `GameRandom` stream because it never touches it.
- Why not `CombatState.Random`? It doesn't exist on the overworld/at run start — that absence is exactly
  what pushed EOR into its broken seeded-fallback (§3.1 #1). Why not EOR's own multi-field seed recipe?
  `EncounterGUID`/`RoundCount` are not run-start-stable; `MapGenSeed + ConfigName` is the minimal pair, and
  its cross-peer identity is a named verification item (§8.1 V3), not an assumption.
- `Mode` is parity-covered as `feature:Mode=<value>` in `enabledFeatures` (§9), so "host says Random,
  client says BLOOD_PRICE" is a reported mismatch, not a silent divergence.

The decline path is trivially honest: don't want a blessing, set `Disabled`. A real in-run choice
(host-side dialog + `BLSS_SYNC_BLESSING_V1` riding a real `AdventureActionData` string field per the
MULTIPLAYER.md transport correction) is v1; the map-encounter surface is a separate future system.

### 3.5 Grant / remove mechanism

**Grant — native calls:** for each party player character (entities with `PlayerComponent`, iterated in
ascending ordinal `Entity.Guid` order — house determinism style, ClassForge SPEC §4.6 invariant 4), if the
entity does not already hold the resolved trait (`InventoryHelper.GetTraits` /
`CharacterHelper.GetFirstTrait` — native, string-prefix keyed): call
`CharacterHelper.GiveTrait(entity, "TRAIT_BLSS_<ID>")` (PSN L197–215). Then write the latch
`GameRunData.Stats["BLSS_ACTIVE_<ID>"] = 1` (EOR key-shape precedent L648–651, our prefix).

**Source of truth is the trait, not the latch.** "Does this run have a blessing" =
"does any party player character hold a `TRAIT_BLSS_*` Thing". Traits are ordinary `Things` — they
save-persist natively (ClassForge SPEC §3 State lifecycle) and are expected to reach a join-in-progress
client with the rest of party state (verification item V4). The `GameRunData.Stats` latch is a fast-path
guard and a debugging surface, mirroring EOR's key shape; whether `GameRunData.Stats` itself replicates is
**explicitly unresolved** (PSN "NOT FOUND" L755–759; `docs/MULTIPLAYER.md` open question #2), which is
precisely why nothing gameplay-authoritative reads only the latch.

**Replicated point:** the grant is a **symmetric local write at a shared point in the run timeline**, not a
transmitted message — the same correctness model as ClassForge's config merge ("config-shaped content over
runtime state", MULTIPLAYER.md practical guidance). Every peer computes the same blessing (§3.4) and
performs the same `GiveTrait` calls on the same entities in the same order. Anchor: a postfix ordered
**after DevKit's parity handshake anchor** (`AdventureDirector.Initialize` today; DevKit owns a follow-up
to move it earlier — ClassForge SPEC §9.1), gated fail-closed exactly like ClassForge's trait-loadout
injection: offline → grant immediately; online → grant only on a *positive* verified parity match. The
grant is idempotent (trait-presence check first), so a missed window self-heals at the next anchor
opportunity (e.g. save-load re-entry). Confirming the exact anchor method and its ordering vs. first
gameplay divergence on the 7/31 build is V7.

**Remove:** none in v0 (EOR parity — a blessing is permanent for the run). Debug-only removal via native
`CharacterHelper.RemoveTrait` + latch delete, exposed only through DevKit's R5-gated command surface
(hard-off in MP sessions).

**Mid-run new party members:** FTK2 party composition is fixed at run start to the best of current
knowledge, but followers/mercs and any revival/rebuild path (`PartyManagementDirector.
_rebuildCharactertAsNewConfigType`, MULTIPLAYER.md OQ#4) need a recon pass — tracked as §11 OQ4. Blessings
apply to `PlayerComponent` holders only, matching EOR's check (L791, L813).

## 4. Data file formats

### 4.1 `blessings.json` (this plugin's registry; ClassForge ignores it)

Strict JSON, UTF-8, no comments (annotations below are illustrative):

```jsonc
{
  "SchemaVersion": "1.0",
  "Blessings": {
    "BLSS_BLOOD_PRICE": {
      "TraitId": "TRAIT_BLSS_BLOOD_PRICE",   // must resolve in Configs.Things post-merge (fail-closed)
      "Weight": 6,                            // EOR weight, verbatim; Max(1, Weight) in the walk
      "Enabled": true,                        // roster gate — disabled entries leave the weighted table
      "Tags": ["Physical", "RiskReward"],    // EOR tags, informational in v0
      "_eor_source": "Plugin.cs L5140"
    }
    // ... one entry per shipped blessing, authored order = EOR table order (walk order is load-bearing)
  }
}
```

Display strings live in `localization/en.json` under EOR's proven key shape (L2200–2205):
`BLSS_<ID>`, `BLSS_<ID>_DESCRIPTION`, `BLSS_<ID>_BONUS`, `BLSS_<ID>_DOWNSIDE`.

### 4.2 `traits.json` entries — hidden, slotless, stats-bearing

Exactly ClassForge SPEC §4.3 shape; the id prefix `TRAIT_` is mandatory and load-bearing:

```jsonc
{
  "TRAIT_BLSS_BLOOD_PRICE": {
    "ConsumableType": "NONE", "Value": 0, "MinTier": 1, "MaxTier": 1,
    "Class": "TRAIT", "Rarity": "COMMON", "Hidden": true,
    "Equippable": { "Slots": [], "Stats": { "PHY": 2, "HP": -10 }, "Passives": [], "MaxCharges": 0 },
    "Tags": ["TRAIT", "BLSS_PACK_EOR_BLESSINGS"], "Expansion": ""
  },
  "TRAIT_BLSS_ARCANE_HUNGER": {
    "ConsumableType": "NONE", "Value": 0, "MinTier": 1, "MaxTier": 1,
    "Class": "TRAIT", "Rarity": "COMMON", "Hidden": true,
    "Equippable": { "Slots": [], "Stats": { "TAL": -5 },
                     "Passives": ["SKILL_BLSS_ARCANE_HUNGER"], "MaxCharges": 0 },
    "Tags": ["TRAIT", "BLSS_PACK_EOR_BLESSINGS"], "Expansion": ""
  }
}
```

### 4.3 `skillrecipes.json` — one recipe

`SKILL_BLSS_ARCANE_HUNGER`, cloned from the shipped `SKILL_CF_PREPARED`
(`CF_PACK_EOR_CLASSES/skillrecipes.json` L656–673; EOR original behavior at L22849): trigger
`ON_COMBAT_START` (rides `CombatHelper.SetInitiative` postfix, PSN §1 L43), condition
`{FOCUS_CURRENT LT MAX}` (reproduces EOR's `GetFocusMissing > 0` overcap guard), effect
`STAT_CHANGE{Target: SELF, Stat: "FOC", StatChangeType: "MAGICAL", FlatValue: 1, Blockable: false}`,
`ProcChance: 100` (no RNG draw at all — ClassForge SPEC §4.6: 100 = no roll taken). `[SYNCED]` per the
recipe engine's universal posture.

## 5. Knobs

- `[General] Enabled` (bool, `true`) — master switch; false = no resolution, no grant, pack data inert.
- `[General] VerboseLogging` (bool, `false`) — per-entity grant decisions at Debug.
- `[Blessings] Mode` (string, `"Disabled"`) — `Disabled` / `Random` / literal blessing id (§3.4).
  Parity-covered (`feature:Mode=<value>`).
- `[Multiplayer] OnParityMismatch` (enum, default **`WarnAndSafeMode`** — the repo default, deliberately
  *not* ClassForge's `Block` override; rationale §9.5).

## 6. Patch targets & integration points

Deliberately tiny. **No `CharacterHelper.GetStat` patch** — the native equipped-trait path replaces EOR's
entire L22518 postfix, which is the point of the design.

| Target | Kind | Why |
|---|---|---|
| Grant anchor (candidate: `AdventureDirector.Initialize`, same anchor DevKit's handshake uses; final choice = V7) | Postfix, ordered after the handshake | Resolve `Mode` → grant traits → write latch. Fail-closed online (positive parity match required), idempotent (trait-presence check). |
| `FTK2Mods.DevKit.ParityService.RegisterWithCallback` | Reflection call (no compile-time dep — `Type.GetType("FTK2Mods.DevKit.ParityService, ftk2mods.devkit")`, ClassForge precedent SPEC §3) | `(guid, version, dataHash, enabledFeatures, onParityFailed)`; §9.6. |
| `CharacterHelper.GiveTrait(Entity, string)` / `GetFirstTrait` / `RemoveTrait` | **Called, not patched** (native public API, PSN L197–215) | Grant / presence check / debug removal. |
| `GameRunData.Stats` | Direct dictionary write (symmetric on all peers) | The `BLSS_ACTIVE_<ID>` latch (§3.5). |
| DevKit command surface | Registration only, R5-gated | `blss status` / `blss clear` debug commands (host-only, MP-off). |

Everything else (config merge, trait id validation, recipe execution, icon/loc plumbing) is ClassForge
executing the pack — no Blessings code involved.

## 7. The blessing table — full EOR transcription (Plugin.cs L5138–5155, verbatim)

Constructor field order (L329): `(Id, DisplayName, Description, BonusText, DownsideText, Weight,
PartyWide, CanStack, GrantStartCombatFocus, Tags[], StatModifiers...)`. All 15 rows are
`PartyWide: true`, `CanStack: false`; `GrantStartCombatFocus` is true **only** for `ARCANE_HUNGER`.
Σ`Weight` = 114.

| # | EOR Id | Display name | BonusText (EOR verbatim) | DownsideText (EOR verbatim) | W | Tags | StatModifiers | v0 tier |
|---|---|---|---|---|---|---|---|---|
| 1 | BLOOD_PRICE | Blood Price | +2 Physical Damage | -10 Max HP | 6 | Physical, RiskReward | PHY +2, HP −10 | B |
| 2 | GLASS_MIND | Glass Mind | +10 Intelligence and +1 Magic Damage | -1 Armor | 6 | Magic, RiskReward | INT +10, MAG +1, DEF −1 | B |
| 3 | FLEET_CURSE | Fleet Curse | +2 Movement and +5 Speed | -5 Vitality | 6 | Movement, Curse, RiskReward | MOV +2, SPD +5, VIT −5 | B |
| 4 | GOLDEN_BURDEN | Golden Burden | +25% Gold gained | -5 Speed | 10 | Economy, RiskReward | GLD +25, SPD −5 | C |
| 5 | FATED_WOUND | Fated Wound | +10% Critical Chance | -5 Max HP (healing reduction fallback) | 6 | Physical, RiskReward | CRT +10, HP −5 | A |
| 6 | IRON_SHACKLES | Iron Shackles | +2 Armor and +2 Resistance | -1 Movement | 6 | Defense, Movement, RiskReward | DEF +2, RES +2, MOV −1 | B |
| 7 | LUCKY_DOOM | Lucky Doom | +15 Luck | -1 Resistance (curse vulnerability fallback) | 10 | Curse, RiskReward | LCK +15, RES −1 | A |
| 8 | ARCANE_HUNGER | Arcane Hunger | Start combat with +1 Focus | -5 Talent (shop price fallback) | 2 | Magic, Economy, RiskReward | TAL −5 (+ Focus rider flag) | A |
| 9 | WILD_PACT | Wild Pact | +5 Awareness and +5 Evasion | -5 Talent (service cost fallback) | 10 | Movement, Economy, RiskReward | AWR +5, EVD +5, TAL −5 | A |
| 10 | VENGEFUL_MARK | Vengeful Mark | +1 Physical Damage (elite damage fallback) | -10% Gold gained | 6 | Physical, Economy, RiskReward | PHY +1, GLD −10 | C |
| 11 | CURSED_INSIGHT | Cursed Insight | +15% XP gained | -5 Luck | 10 | Curse, RiskReward | XPM +15, LCK −5 | C |
| 12 | HOLLOW_VIGOR | Hollow Vigor | +15 Max HP | -1 Health Regen | 10 | Defense, RiskReward | HP +15, HRG −1 | A |
| 13 | BURNING_SOUL | Burning Soul | +1 Physical Damage and +1 Magic Damage | -5 Max HP (start damage fallback) | 6 | Physical, Magic, RiskReward | PHY +1, MAG +1, HP −5 | B |
| 14 | SHADOW_BARGAIN | Shadow Bargain | +10 Evasion | -1 Armor | 10 | Defense, RiskReward | EVD +10, DEF −1 | A |
| 15 | MERCHANTS_CURSE | Merchant's Curse | +10 Talent (shop discount fallback) | -10% Gold gained | 10 | Economy, Curse, RiskReward | TAL +10, GLD −10 | C |

**Tiering (roster gate, resolved by V1):**
- **Tier A (6 blessings — every stat key is in the verified character-sheet vocabulary, ClassForge SPEC
  §4.2: AWR CRT DEF EVD HP HRG INT LCK RES SPD TAL VIT):** FATED_WOUND, LUCKY_DOOM, ARCANE_HUNGER,
  WILD_PACT, HOLLOW_VIGOR, SHADOW_BARGAIN. Ship in v0 unconditionally.
- **Tier B (5 — additionally use `PHY`/`MAG`/`MOV`):** those keys are verified members of the *ability*
  `CHANGE_STAT` vocabulary (ClassForge SPEC §4.4) but **unverified as effective `Equippable.Stats` keys**
  on the trait/equipped path. Ship iff V1 confirms.
- **Tier C (4 — use `GLD`/`XPM`):** pure pseudo-stats. In EOR these only did anything because EOR's
  `GetStat` postfix answered *whatever* stat key the game queried — whether the vanilla game ever queries
  `"GLD"`/`"XPM"` (i.e. whether EOR's own gold/XP blessings actually functioned) is unverified. Ship iff
  V1 confirms; otherwise park with the entries authored but `Enabled: false`.
- **Correction to prior scouting:** the earlier pass said "13 clean stat blessings" with only GLD%/XPM
  suspect. The actual counts against verified vocabularies are **6 / 5 / 4** as above (11 GLD/XPM-free,
  of which only 6 are fully sheet-vocabulary-clean). The "13" figure double-counted two GLD carriers
  (VENGEFUL_MARK, MERCHANTS_CURSE).

Localized display strips the "(… fallback)" annotations (they are EOR dev notes, not player text) but the
`_eor_source` fields preserve the verbatim originals.

## 8. Verification & testing plan

### 8.1 Pre-implementation verification (PSN write-ups required, against the 7/31 refs)

> **→ see Disposition ledger (2026-08-06)** at the end of this document — V1–V7 each carry an explicit
> post-implementation status there. The list below is preserved as the design-time record.

`tools/bin/refs` matches the 7/31 game build; **all PSN line-number citations predate it** and the 7/31
update demonstrably changed at least one hook shape (memory note: `RenderClassList` grew a 5th bool;
vanilla now ships `TRAIT_FIELDMEDIC`). Each item below needs a PSN section (or addendum) before its
dependent milestone starts:

- **V1 — stat-key effectiveness (blocks the roster freeze, M0→M1 gate).** (a) Extract vanilla Things JSON
  from the 7/31 build (`tools/extract_assets.py`) and enumerate every distinct `Equippable.Stats` key in
  live data; (b) for each blessing key absent from vanilla equippable usage (`PHY MAG MOV GLD XPM`
  expected suspects), grep the decompiled 7/31 `FTK2.dll` for `GetStat`-family callsites passing that
  literal. A key nothing queries = a dead stat = its blessings stay `Enabled: false`. Also confirm
  vanilla `TRAIT_*` Things carry negative stat values somewhere (or that negatives behave; EOR relied on
  postfix arithmetic, we rely on the native sum).
- **V2 — `GameRunData.Stats` MP replication + join snapshot** (PSN "NOT FOUND" L755–759;
  `docs/MULTIPLAYER.md` open question #2). Decides whether the `BLSS_ACTIVE_*` latch is peer-consistent
  or advisory-local. The design survives either answer (trait-presence is authoritative, §3.5), but the
  write-up must say which.
- **V3 — `MapGenSeed`/`ConfigName` cross-peer identity at run start** (the offer-determinism basis, §3.4).
  EOR treats `MapGenSeed` as shared (its shared-seed fallback, L10482); confirm in vanilla netcode, and
  confirm both fields are populated *before* the grant anchor fires.
- **V4 — party `Things` replication on join-in-progress** — does a late joiner receive already-granted
  trait Things with party state? (Expected yes — save-shaped state — but cite the path.)
- **V5 — `GameAction.DesyncDetectionData.Hash` coverage** — what state does the vendor's per-action hash
  actually cover (party stats? `GameRunData`?). Backs the §9.6 self-audit claim with specifics instead of
  the current "the game polices `GameRandom` draws" (MULTIPLAYER.md) generality.
- **V6 — 7/31 signatures for every surface this spec names:** `CharacterHelper.GiveTrait(Entity, string)`
  / `GetFirstTrait` / `RemoveTrait`, `CharacterHelper.GetFocusMissing`, `CombatHelper.SetInitiative`
  (EOR bound it as a 6-arg method, L8840 — confirm arity), `AdventureDirector.Initialize`,
  `InventoryHelper.GetTraits`, `PlayerComponent` presence semantics.
- **V7 — grant-anchor ordering:** confirm the chosen anchor runs after DevKit's parity handshake resolves
  and before any gameplay divergence is possible; inherits/depends on the known DevKit follow-up to move
  the handshake earlier (ClassForge SPEC §9.1 last paragraph).

### 8.2 Offline tests (pre-game)

1. Pack validates under `ClassForge.PackCheck`; all 15 trait ids pass the `^TRAIT_[A-Z0-9_]+$` gate;
   recipe passes the recipe validator; `blessings.json` roster ↔ `traits.json` ids cross-check (a roster
   entry whose `TraitId` isn't in the pack = build-time test failure).
2. Deterministic resolution: golden tests for `Random` mode — fixed `(MapGenSeed, ConfigName)` pairs →
   expected blessing id; weight-walk boundary cases (roll exactly at a bucket edge, `Weight ≤ 0` clamps
   to 1 mirroring EOR's `Max(1, Weight)` L832/L837); roster with Tier B/C disabled changes Σweights and
   outcomes deterministically.
3. Grant idempotency: double-invoke at the anchor grants once; save-load simulation (trait already
   present, latch absent) grants nothing and restores the latch.

### 8.3 In-game single-player smoke

1. `Mode = BLSS_HOLLOW_VIGOR`, start a run: log shows resolution + one grant per party character;
   character sheet Max HP +15; HRG reduced by 1 (observe over rests/regen ticks).
2. `Mode = BLSS_ARCANE_HUNGER`: enter combat at full Focus → no grant (condition), enter below max →
   +1 Focus at combat start, once, every combat; TAL −5 visible at shops.
3. Save, quit, reload: blessing still active (trait persisted), no double-grant, latch restored.
4. `Mode = Random`: restart the *same* run seed twice → same blessing both times.
5. `Mode = Disabled` / plugin removed: zero behavior delta; a save carrying `TRAIT_BLSS_*` with the pack
   still installed keeps its stats (ClassForge data), with orchestration inert.

### 8.4 MP smoke — see §9.6.

## 9. Multiplayer

Per `docs/MULTIPLAYER.md`'s mandated §9 structure.

### 9.1 Parity class: `ALL_PEERS`

The trait configs merge into every peer's `Configs.Things` (ClassForge pack), the recipe executes inside
every peer's combat simulation, and the granted `Thing` must resolve on every peer that renders or
simulates a blessed character. A peer missing the pack is ClassForge's unresolvable-config failure mode —
and is already refused by **ClassForge's `Block`** on the pack's `dataHash` (which covers
`blessings.json` too, §3.3) before Blessings logic runs. The Blessings plugin registers separately so its
*orchestration* config (`Mode`) is also parity-visible.

### 9.2 Feature table

| Feature | Class | Authority |
|---|---|---|
| Pack content (15 traits + recipe) in `Configs.Things` / recipe registry | `[SYNCED]` | All peers via R1 parity (ClassForge merge); nothing transmitted |
| Blessing resolution (`Mode` + hash-derived pick) | `[SYNCED]` | All peers, symmetric deterministic function of shared run identity (§3.4); zero RNG draws |
| Trait grant (`GiveTrait` per party player) | `[SYNCED]` | All peers, symmetric local writes at the shared anchor; fail-closed online until positive parity match |
| `BLSS_ACTIVE_*` latch in `GameRunData.Stats` | `[SYNCED]`-by-construction (symmetric write); replication status itself = V2 | All peers; never solely authoritative (§3.5) |
| `SKILL_BLSS_ARCANE_HUNGER` proc | `[SYNCED]` | ClassForge recipe engine's universal posture (SPEC §4.6): no RNG (`ProcChance: 100`), effect rides native `CHANGE_STAT` |
| Logging, localization, (future) breakdown-row attribution | `[LOCAL]` | Local peer, R4-exempt |
| `Enabled` / `Mode` knobs | parity-covered | `feature:Mode=<value>` in `enabledFeatures` |

### 9.3 Determinism inventory

- **The offer pick** (`Random` mode): SHA-256 over `MapGenSeed|ConfigName` → weighted walk in authored
  order (§3.4). No `System.Random`, no `UnityEngine.Random`, no constructed `GameRandom`, no wall clock,
  invariant culture — R2 satisfied without touching the shared stream at all (stronger than the EOR
  `EOR_SHARED_RNG` pattern for this use case, and immune to EOR's fallback-seed divergence, §3.1 #1).
- **Grant iteration**: ascending ordinal `Entity.Guid`; never dictionary enumeration order.
- **Roster/table order**: authored `blessings.json` order (file is parity-hashed), never filesystem or
  dictionary order.
- **`ARCANE_HUNGER` proc**: `ProcChance: 100` — by the recipe engine's evaluation contract, no roll is
  drawn (ClassForge SPEC §4.6), so no draw-count skew is possible.

### 9.4 Sync surface

**v0 transmits nothing.** Effects flow through: (a) parity-enforced config data (traits in
`Configs.Things`), (b) the native equipped-trait `GetStat` summation on each peer, (c) the recipe engine's
native `CHANGE_STAT` emission via `CombatHelper.ApplyAction`. Reserved for v1 (named now per house rule):
`BLSS_SYNC_BLESSING_V1` — host-decided in-run offer acceptance, host → clients, idempotent, riding a real
`_trySendNetworkAction` overload's `AdventureActionData` string field (per the MULTIPLAYER.md transport
correction — there is no string-keyed bus), plus `BLSS_SYNC_BLESSING_REQUEST_V1` for rejoin.

### 9.5 SafeMode definition (default `WarnAndSafeMode` — repo default retained, unlike ClassForge)

SafeMode is genuinely meaningful here, which is why this plugin does **not** copy ClassForge's `Block`
override: a blessing **not yet granted** is cleanly skippable, and a blessing **already granted** is pack
data whose parity ClassForge's own `Block` already enforces upstream. On a `ftk2mods.blessings` mismatch
(version, `dataHash` over `blessings.json`+config schema, or `feature:Mode` divergence):

- **Off in SafeMode:** blessing resolution and all `GiveTrait` grants for the session (fail-closed on
  every peer that observes the mismatch — no peer grants, so no asymmetric `Things`).
- **Stays on:** nothing state-mutating remains; logging only. Previously-granted traits keep working
  because they are ClassForge-merged configs + native Things, outside this plugin's runtime.
- `WarnOnly` exists for completeness but is footgun-labeled in the config comment (a one-sided grant is a
  guaranteed stat divergence on every subsequent `GetStat`-dependent decision).

### 9.6 MP test plan + self-audit via the vendor's desync detector

Registration: `Load()` → reflection → `ParityService.RegisterWithCallback(guid="ftk2mods.blessings",
version=assembly, dataHash=sha256 over blessings.json (the pack's traits/recipes are hashed by
ClassForge's registration; double coverage of blessings.json is accepted, see §11 OQ7),
enabledFeatures=["feature:Mode=<value>"], onParityFailed→SafeMode latch)`.

Host+client smoke (extends §8.3):
1. **Match:** both peers same pack + `Mode = Random`. Start a run; both logs resolve the **same blessing
   id** from the same seed; both show N grants; both character sheets show identical modified totals.
   Fight one combat with `ARCANE_HUNGER` active: +1 Focus applies identically on both screens.
2. **Self-audit:** play 10+ actions with the game's desync monitor available
   (`NetworkData.DoMonitorForDesyncs` / `GameAction.DesyncDetectionData` — MULTIPLAYER.md "the game
   self-polices desyncs"): `HasADesyncBeenDetected` stays false. Cross-check with the DevKit dump-compare
   / EOR "Print Sync-Relevant Data Hash" precedent (EOR debug button L3999). The precise state the vendor
   hash covers is V5; until V5 lands, the dump-compare is the primary audit and the vendor detector is
   the backstop.
3. **Mismatch — mode:** host `Random`, client `Disabled` → parity reports `feature:Mode` divergence; both
   peers' Blessings enter SafeMode; **neither** grants; run proceeds blessing-less; no desync.
4. **Mismatch — data:** client edits a weight in `blessings.json` → ClassForge `dataHash` mismatch blocks
   at ClassForge's layer (join refused / features off per its §9.5) before any Blessings behavior;
   Blessings' own hash also reports.
5. **Join-in-progress** (if the game supports rejoin): grant on host+client, drop client, rejoin →
   client's blessed characters still carry the trait and identical totals (exercises V4).

## 10. Milestones (single-session implementation waves)

- **M0 — Verification wave (no shipping code).** Execute V1–V7 (§8.1); land PSN addenda; freeze the v0
  roster (which of Tiers B/C ship `Enabled: true`); confirm the grant anchor. Exit: every §6 surface
  cited against 7/31 refs.
- **M1 — Content wave.** Author `BLSS_PACK_EOR_BLESSINGS` (traits.json ×15, skillrecipes.json ×1,
  localization, blessings.json, pack.json); PackCheck green; recipe fixture test green; roster
  cross-check test green (§8.2 #1). No plugin code yet — the pack loads under ClassForge and its traits
  are inert-but-valid configs.
- **M2 — Orchestrator wave.** `Blessings.Core` (resolution, weighted walk, grant planner, latch,
  fail-closed gates) with the §8.2 #2–3 unit/golden tests; `Blessings.Plugin` (anchor postfix, GiveTrait
  execution, ParityService registration, SafeMode latch, knobs, R5 debug commands). Offline-verified
  against the refs snapshot, ClassForge-style.
- **M3 — SP smoke wave (operator, live game).** §8.3 script; fix fallout (expect JsonHelper.importOptions
  -class issues per the 7/31 lessons in `eor-0760-analysis.md` if any game types are deserialized).
- **M4 — MP smoke wave (operator, two peers).** §9.6 script incl. both mismatch drills and the
  dump-compare audit. Exit = v0 done.
- **v1 (parked, not scheduled):** host-choice run-start dialog + `BLSS_SYNC_BLESSING_V1`;
  `UIHelper.GetBreakdownStats` attribution row (EOR precedent L22572–22604); FORBIDDEN_SANCTUM-style
  world-event offer surface (separate future world-events system); Tier C blessings if V1 rules them out
  for v0 but a `GetStat`-independent gold/XP verb is later verified.

## 11. Open questions

> **→ see Disposition ledger (2026-08-06)** at the end of this document — every OQ below is dispositioned
> there (resolved with evidence, superseded by a gate, carried forward with an owner, or awaiting the
> operator smoke). The wording below is the design-time record and is deliberately left unedited.

1. **`PHY`/`MAG`/`MOV`/`GLD`/`XPM` effectiveness as `Equippable.Stats` keys** — the roster gate.
   Owner: M0 (V1). Ship-blocking for Tiers B/C only; Tier A ships regardless.
2. **`GameRunData.Stats` MP replication** (PSN NOT FOUND L755; MULTIPLAYER.md OQ#2). Owner: repo-wide
   decompile pass (DevKit). Blessings is designed to survive either answer (trait-presence authority,
   §3.5); resolves whether the latch is peer-consistent.
3. **Grant anchor vs. parity-handshake ordering** — inherits ClassForge §9.1's known limitation (handshake
   anchor currently `AdventureDirector.Initialize`; DevKit owns moving it earlier). Owner: DevKit
   follow-up + M0 (V7). Consequence if unresolved: first-session online grants may be legitimately
   deferred to the next anchor opportunity (fail-closed, self-healing) — safe but visible.
4. **Mid-run party-composition changes** — can a *player* entity be added/rebuilt mid-run
   (`_rebuildCharactertAsNewConfigType`, revival paths), and does it need a grant-on-add hook? Owner: M0
   recon. v0 behavior if unhooked: a rebuilt character could lack the trait → asymmetry only if the
   rebuild itself is asymmetric (vanilla replicates it — MULTIPLAYER.md OQ#4).
5. **Hidden-trait visibility** — with `Hidden: true`, confirm the trait truly renders nowhere it
   shouldn't (inventory/loadout screens) and decide the v1 attribution surface. Owner: M3 smoke + design.
6. **Double parity coverage of `blessings.json`** (ClassForge's pack hash *and* Blessings' own hash) —
   accepted redundancy; may produce two mismatch reports for one edit. Cosmetic; decide whether Blessings
   should exclude it from its own hash once DevKit's mismatch UI is observed. Owner: M4.
7. **`Weight ≤ 0` semantics** — EOR clamps to 1 in both the sum and the walk (`Max(1, Weight)`,
   L832/L837); we mirror it, but `blessings.json` authoring guidance should simply forbid non-positive
   weights (validator warning). Owner: M1 validator.

---

## Disposition ledger (2026-08-06, post-implementation)

Written after M0–M2 shipped (`e57b02b` content pack, `1b98408` orchestrator + 34 tests; M3/M4 are
operator smoke waves and remain outstanding by design). Statuses: **RESOLVED** (evidence + where) ·
**RESOLVED-BY-DESIGN-CHANGE** (a verification gate superseded the question) · **CARRIED-FORWARD** (still
open, with an owner and a stated safe interim behavior) · **AWAITING-SMOKE** (in-game only; named
operator step). Nothing from §8.1/§11 is dropped.

| Item | Status | Evidence / pointer | Owner if carried |
|---|---|---|---|
| **V1** stat-key effectiveness (roster gate) | **RESOLVED** | `PHY MAG MOV GLD XPM` are all canonical `eCharacterStats` members and all live in vanilla `Equippable.Stats` data (55/50/22/111/36 uses in the 7/31 build), summed by the *generic unfiltered* `GetStat` path (`CharacterHelper.cs` L411-562, `InventoryHelper.cs` L861-868). `GLD`/`XPM` are real percent-modifier stats (`CharacterSummaryViewHelper` L136, `ToolTipHelper` L721-722). Negatives are unclamped for all five (none appear in the `TryGetMin/MaxStatValue` allowlists) and negative equippable stats are pervasive in vanilla. Written up as **PSN §15** (stat-key census, `1f25aec`). **Consequence: the Tier A/B/C split in §7 dissolves — all 15 blessings ship `Enabled: true`** (`e57b02b`). | — |
| **V2** `GameRunData.Stats` MP replication + join snapshot | **RESOLVED** | It **does** replicate: the full `GameRunData` (JSON + LZ4 blob) rides `JIP_SYNC_DATA` — `NetworkHelper` L1237/L1264-73, `SaveGameHelper` L507-14. This **contradicts and supersedes** the stale PSN "NOT FOUND" entry, which was corrected in the same pass. Written up as **PSN §12** (`1f25aec`). The `BLSS_ACTIVE_<ID>` latch is therefore peer-consistent — but §3.5's "trait is the source of truth" rule was kept anyway (it costs nothing and survives a future regression). | — |
| **V3** `MapGenSeed`/`ConfigName` cross-peer identity at run start | **RESOLVED** | Both are set in `PartyManagementDirector._initAdventureAndRoute` during the **replicated** `NewRouteAction`, i.e. before `AdventureDirector.Initialize` runs — so they are populated *and* identical on every peer at the grant anchor. PSN §12 (`1f25aec`). The §3.4 offer hash (`SHA256("BLSS_OFFER_V1\|" + MapGenSeed + "\|" + ConfigName)`) stands as specced and is golden-tested with independently computed hashes (`1b98408`). | — |
| **V4** party `Things` replication on join-in-progress | **RESOLVED** | Party `Thing`s ride the same `JIP_SYNC_DATA` blob as the rest of `GameRunData` — a late joiner receives already-granted `TRAIT_BLSS_*` Things with party state, as §3.5 expected. PSN §12 (`1f25aec`). | — |
| **V5** `GameAction.DesyncDetectionData.Hash` coverage | **RESOLVED** | The hash is an MD5 over near-full `GameRunData` (including `Entities`/`Things`) **plus an explicit `Stats` copy**, taken at save/init/end-turn checkpoints; `GameRandomNextInt` is a separate, narrower check. So the vendor detector genuinely covers both a divergent trait grant and a divergent latch — §9.6's self-audit claim is now specific rather than general. PSN §12 (`1f25aec`). | — |
| **V6** 7/31 signatures for every §6 surface | **RESOLVED** (with two API corrections) | `CharacterHelper.GiveTrait(Entity, string)` and `AdventureDirector.Initialize` confirmed as specced. **Corrections:** `GetFirstTrait` takes a `CharacterComponent` and returns only the first trait — presence checks use `InventoryHelper.GetTraits(e).Any(ConfigName == id)` instead; `RemoveTrait` takes a `Thing`, not a string. Also confirmed: `PlayerComponent` is an explicit opt-in flag on the ≤4 party characters (mercs are created with `pAddPlayerComponent: false`), so §3.5's "`PlayerComponent` holders only" scoping is exact. Written up as **PSN §13** (`1f25aec`); the corrected calls ship in `Blessings.Plugin` (`1b98408`). | — |
| **V7** grant-anchor ordering vs the parity handshake | **RESOLVED-BY-DESIGN-CHANGE** (**Gate E**) | The premise was wrong: "postfix ordered *after* DevKit's handshake" is neither achievable nor meaningful, because the handshake is **async fire-and-forget** — no Harmony ordering can make a postfix wait for it. Superseded by the ClassForge `ParityBridge.HasVerifiedMatch()` pattern (`ParityBridge.cs:167-197`): the grant anchor **polls** `ParityService.GetLastVerdictRows()` for a `Match` verdict, fail-closed, and re-tries idempotently at later anchor opportunities. Shipped in `Blessings.Plugin` via `DevKitParityBridge` (`1b98408`). Visible consequence, logged as expected rather than as an error: **in a first online session the grant lands at the second anchor opportunity**, not the first. | — |
| **OQ1** `PHY`/`MAG`/`MOV`/`GLD`/`XPM` effectiveness | **RESOLVED** | Same evidence as V1 (PSN §15). Roster frozen at **all 15 `Enabled: true`**; §7's Tier A/B/C gate is now historical. | — |
| **OQ2** `GameRunData.Stats` MP replication | **RESOLVED** | Same evidence as V2 (PSN §12). The latch is peer-consistent; trait-presence remains the authority per §3.5. | — |
| **OQ3** grant anchor vs parity-handshake ordering | **RESOLVED-BY-DESIGN-CHANGE** (**Gate E**) | Same as V7 — verdict polling replaces patch ordering entirely, so the "inherits ClassForge §9.1's known limitation" framing no longer applies. The predicted consequence ("first-session online grants may be legitimately deferred to the next anchor opportunity") is exactly what ships, and is now a designed, logged behavior rather than a limitation. | — |
| **OQ4** mid-run party-composition changes | **CARRIED-FORWARD** | No recon pass was run — the Wave-1 verification budget went to V1–V7, and this question needs a `_rebuildCharactertAsNewConfigType`/revival-path decompile sweep that nothing in M1/M2 depended on. **Risk is bounded by design, not by luck:** the grant is idempotent (trait-presence check first) and self-healing (re-checked at every later anchor opportunity), so a rebuilt character that lost its trait re-acquires it at the next anchor, symmetrically on every peer. **Safe interim behavior:** no grant-on-add hook; worst case is a temporarily blessing-less rebuilt character. | Repo-wide MP recon (DevKit), or M4 if the smoke surfaces it |
| **OQ5** hidden-trait visibility (`Hidden: true` renders nowhere it shouldn't) | **AWAITING-SMOKE** | Purely a rendering question; no offline surface answers it. The pack ships all 15 traits `Hidden: true, Slots: []` per §4.2 and PackCheck is clean (`e57b02b`). **Safe interim behavior:** if a hidden trait does render somewhere unwanted, it is cosmetic — stats are already correct via the native equipped-trait path. | **Operator step:** M3 SP smoke (§8.3) — after granting, inspect inventory/loadout/character-sheet screens for a stray `TRAIT_BLSS_*` row; also decides the v1 attribution surface |
| **OQ6** double parity coverage of `blessings.json` | **CARRIED-FORWARD** | Shipped as designed, i.e. the accepted redundancy stands: ClassForge hashes the whole pack tree (including `blessings.json`) and `Blessings` registers its own hash over the same file. Whether one edit produces two mismatch reports is only observable once DevKit's mismatch UI is watched live. **Safe interim behavior:** double-reporting is cosmetic; both hashes fail closed in the same direction. | M4 MP smoke (§9.6 step 4) — observe the report count, then decide whether Blessings drops the file from its own hash |
| **OQ7** `Weight ≤ 0` semantics | **RESOLVED** | Both halves shipped in `Blessings.Core` (`1b98408`): `BlessingsRegistryParser` emits a **warning** finding for any `Weight <= 0` ("authoring guidance is to avoid non-positive weights"), and `BlessingResolver.SumWeights`/`WalkWeighted` both apply `Math.Max(1, Weight)`, mirroring EOR L832/L837 exactly. Boundary cases are covered by the golden weight-walk tests. | — |

**Recorded delta touching §3.5/§6 without being an open question:** the `blss status` / `blss clear`
R5-gated debug commands are **not shipped**. DevKit has no command-registration surface at all — the
`FTK2.DevKit/SPEC.md` text describes the concept, not an implemented API (searched during M2). The §6
row "DevKit command surface" is therefore aspirational until DevKit builds one; debug removal via
`CharacterHelper.RemoveTrait(Thing)` remains available to a future command surface with no design change.
