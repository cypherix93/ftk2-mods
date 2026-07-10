# FTK2.Venue — SPEC

Plugin GUID: `ftk2mods.venue` · Id prefix: `VNU_` · Priority: **P3** (deliberately small scope)

## 1. Purpose & scope

FTK2.Venue makes the game's pre-combat **battle-grid choice** data-driven. Every fight picks one of three
hardcoded grid layouts (`eVenueGrids`: `Standard`, `BossKraken`, `Extended`) via `CombatState.GridType`.
Vanilla logic for *when* `Extended` gets picked (if any exists beyond kraken-boss special-casing) is not
visible in our reference docs. This mod inserts an ordered, JSON-configured rule list that looks at the
upcoming fight's context (enemy count, 2x2 enemies, boss presence, encounter type, party size + summon
capability, specific character ids/tags) and assigns `Standard` or `Extended` accordingly — before the
game builds tiles from that grid type.

It also exposes a couple of already-parameterized `VenueHelper` placement behaviors as data knobs
(enemy-count scaling with grid size, room-making for support characters), so the bigger-room decisions this
mod makes don't produce cramped/overflowing placements.

**Deliberately out of scope for M1/M2:** `BossKraken` selection is never touched by the rule engine — kraken
fights keep whatever grid the base game assigns them. Out of scope entirely for this mod (not just deferred):
new ability/status/character content, AI behavior, anything not about tile-grid choice or placement capacity.

**M3 is explicitly exploratory, not a deliverable**: a custom grid *definition* format (tile count, rows,
groups, row-position types) is sketched in §4 and one example file ships, but it is marked
**NOT FUNCTIONAL YET**. `VenueHelper`'s grid layouts (`VenueMap1`, `ExtendedVenueMap1`, `KrakenMap1`) are
static C# fields, not data-loaded, and the diorama's visual anchors (`Diorama.LoadVenueGrid`) are a separate,
harder problem. §7's M3 milestone documents the investigation plan honestly; it does not promise a working
custom-grid loader.

## 2. Player-facing behavior

- Nothing changes visually for the common case: fights that would already read as "big" (many enemies, a
  2x2 boss with backup, a swollen summon-heavy party) get the roomier Extended grid instead of feeling
  cramped on Standard. Small/normal fights are unaffected.
- No new UI. The only player-visible surface is the grid itself (more/fewer tiles, row layout) and, in
  debug builds, whatever the force-grid knob produces (e.g. forcing every fight to Extended to stress-test
  crowding).
- No new content, no new abilities, no new UI panels.

## 3. Architecture

**Engine (C#) responsibilities**: read `data/GridRules.json` once at startup (with DevKit-style hot-reload
if `FTK2.DevKit` is present — not a hard dependency), evaluate the ordered rule list against a per-combat
context object built from whatever fight data is available at the patch point, and — if `Enabled` and a rule
matches — set `CombatState.GridType` to that rule's grid before `VenueHelper.CreateVenueTileEntities` (or
whatever earlier call actually assigns it — see §6 open question) consumes it. Also two small parameter
patches around `VenueHelper.AdjustMaxEnemiesToPlayerCount` / `MakeRoomForSupportCharacters` to scale
placement capacity with the chosen grid.

**Data surface**: `data/GridRules.json` (rule list + defaults), knobs in BepInEx config (see §5). No
tuning numbers or content hardcoded in the engine — rule thresholds, grid choices, and placement bonuses are
all data.

**Runtime flow (per combat)**:
1. Fight is about to start; the game determines its enemy set / encounter data somewhere in
   `CombatPhase`/`EncounterPhase` setup (exact call sequence unverified — see §11).
2. Our patch fires at (or just before) whichever point sets `CombatState.GridType`. It builds a context
   object: `EnemyCount`, `HasTwoByTwoEnemy`, `HasBossEnemy` (id-prefix `BOSS_*` match — see §11 on tag vs.
   prefix), `EncounterType` (best-effort, may be empty — see §11), `PartySize`, `PartyPlusSummonCapable`
   (party members plus any ally whose `CombatComponent.CanSummon` is true), `CharacterIds`/`CharacterTags`
   present on either side.
3. If `ForceGrid` (debug knob) is set to anything but `None`, that value wins outright and the rule engine
   is skipped (logged).
4. Otherwise, if the vanilla-computed grid for this fight is `BossKraken`, the mod does nothing —
   kraken grids are never overridden.
5. Otherwise, rules in `GridRules.json` are evaluated top-to-bottom; the first rule whose conditions all
   match wins and its `Grid` value is written to `CombatState.GridType`. If no rule matches, vanilla's
   choice (effectively `Standard` for non-kraken fights) stands.
6. If `VerboseLogging` is on, every rule considered and the final decision are logged at `Debug` level,
   once per combat.
7. Placement-tuning knobs (§5 `[Placement]`) are applied unconditionally inside the
   `AdjustMaxEnemiesToPlayerCount` / `MakeRoomForSupportCharacters` patches, independent of whether this
   mod's rule engine changed the grid — they just make sure a bigger grid gets used well.

**State lifecycle**: no persistent state. Rules are loaded once (or hot-reloaded); the per-combat context
object is built fresh each fight and discarded at combat end. Nothing touches `GameRunData` or save files.

## 4. Data file formats

### 4.1 `data/GridRules.json` — grid-selection rule list

```jsonc
{
  // Master toggle for the rule engine specifically (independent of the plugin-wide [General] Enabled
  // knob, so designers can disable just the rules while keeping placement-tuning knobs active).
  "Enabled": true,

  // Evaluated top-to-bottom. First rule whose Conditions all match (AND) wins. A rule with an empty
  // Conditions object always matches — use one at the end as an explicit default/fallback.
  // Kraken fights are never touched: this file cannot select "BossKraken" as an override target for a
  // non-kraken fight, and the engine skips evaluation entirely when vanilla already picked BossKraken.
  "Rules": [
    {
      "Id": "VNU_RULE_BIG_ENCOUNTER",
      "Comment": "5+ enemies: give the room.",
      "Grid": "Extended",
      "Conditions": {
        "MinEnemyCount": 5
      }
    },
    {
      "Id": "VNU_RULE_TWOBYTWO_CROWD",
      "Comment": "A 2x2 enemy (boss-sized footprint) plus at least 3 others needs Extended so the 2x2 doesn't eat half the row.",
      "Grid": "Extended",
      "Conditions": {
        "HasTwoByTwoEnemy": true,
        "MinEnemyCount": 4
      }
    },
    {
      "Id": "VNU_RULE_SUMMON_SYNERGY",
      "Comment": "Summoner/Necromancer-type parties: party size + summon-capable allies >= 5 crowds Standard fast.",
      "Grid": "Extended",
      "Conditions": {
        "MinPartyPlusSummons": 5
      }
    },
    {
      "Id": "VNU_RULE_DEFAULT",
      "Comment": "Fallback: everything else stays Standard (vanilla behavior).",
      "Grid": "Standard",
      "Conditions": {}
    }
  ]
}
```

**Condition fields** (all optional inside a rule; a rule matches only if every field it specifies matches):

| Field | Type | Matches when |
|---|---|---|
| `MinEnemyCount` | int | `EnemyCount >= value` |
| `MaxEnemyCount` | int | `EnemyCount <= value` |
| `HasTwoByTwoEnemy` | bool | any enemy tagged/sized as a 2x2 footprint is present (`==` value) |
| `HasBossEnemy` | bool | any enemy id matches the `BOSS_*` prefix (`==` value) — see §11, tag-vs-prefix caveat |
| `EncounterType` | string[] | fight's encounter-type context is one of the listed values (best-effort; see §11) |
| `MinPartySize` | int | living player-party member count `>= value` |
| `MinPartyPlusSummons` | int | `PartySize + PartyPlusSummonCapable >= value` |
| `CharacterIds` | string[] | any combatant (either side) has one of these config ids |
| `CharacterTags` | string[] | any combatant (either side) has one of these tags |

`Grid` must be `"Standard"` or `"Extended"` (rules cannot target `"BossKraken"` — enforced at load time;
an offending rule is rejected and logged, file otherwise still loads).

### 4.2 `data/Grids/*.json` — custom grid definition (M3, **NOT FUNCTIONAL YET**)

Research-milestone-only sketch of what a data-driven grid layout might look like if `VenueHelper`'s
static maps (`VenueMap1`, `ExtendedVenueMap1`, `KrakenMap1`) could be replaced/extended by JSON. **No
engine code reads this file in M1/M2.** See `data/Grids/example.NOT_FUNCTIONAL.json` for the fully
commented version and §7/§11 for why this is unproven.

```jsonc
{
  "NOT_FUNCTIONAL_YET": true,
  "Id": "VNU_GRID_EXAMPLE",
  "TileCount": 12,
  "Rows": [
    { "RowIndex": 0, "RowPositionsType": "FRONT", "TileCount": 4 },
    { "RowIndex": 1, "RowPositionsType": "BACK",  "TileCount": 4 },
    { "RowIndex": 2, "RowPositionsType": "BACK",  "TileCount": 4 }
  ],
  "Tiles": [
    { "Index": 0, "GroupIndex": 0, "RowPositionsType": "FRONT" }
    // ...one entry per tile, mirroring VenueTileComponent{ GroupIndex, RowPositionsType }
  ]
}
```

## 5. Knobs

```
[General]
Enabled (bool, true) — master toggle; if false, GridType and placement patches are no-ops (vanilla behavior).
VerboseLogging (bool, false) — log rule evaluation + final grid decision per combat at LogLevel.Debug.

[Rules]
RulesEnabled (bool, true) — mirrors GridRules.json's own "Enabled"; both must be true for the rule engine to run. Split out so a bad data file can be disabled from config without editing JSON.

[Debug]
ForceGrid (string, "None") — one of None/Standard/Extended/BossKraken. Non-None bypasses the rule engine entirely and forces every combat's grid to this value (including kraken fights) — for stress-testing placement at each grid size. In MP, treat as host-only regardless of parity class (§9.2): a client-forced grid that isn't itself replicated to peers is a guaranteed desync. Doubles as the MP sync probe — see §9.6/§10.

[Placement]
TunePlacementCapacity (bool, true) — master toggle for the two knobs below; if false, AdjustMaxEnemiesToPlayerCount / MakeRoomForSupportCharacters patches are no-ops.
ExtendedGridBonusEnemySlots (int, 2) — additional max-enemy allowance applied when CombatState.GridType == Extended, on top of whatever AdjustMaxEnemiesToPlayerCount computes.
SupportCharacterExtraRoomSlots (int, 0) — additional reserved-empty-tile slack applied wherever MakeRoomForSupportCharacters carves out space, so summon overflow doesn't fail to place. Default 0 (vanilla behavior) pending confirmation of that method's exact parameters (§11).

[Multiplayer]
OnParityMismatch (enum, WarnAndSafeMode) — repo-wide default from docs/MULTIPLAYER.md R1 (alternatives: WarnOnly, Block). Governs this mod's ParityService registration: under the recommended HOST_ONLY parity class (§9.1) a mismatch is warn-only informational and never blocks or SafeModes the session; under ALL_PEERS (fallback, if GridType turns out to be computed per-peer) it is enforced as specified. See §9.5 for exactly what SafeMode disables for this mod.
```

## 6. Patch targets & integration points

All names verbatim from `docs/research/game-code-reference.md` §3 (Battle grid) unless noted.

| Target | Kind | Why |
|---|---|---|
| **`CombatState.GridType`** (field) | write, from wherever it's set pre-combat — **exact assignment site not yet located**, see §11 | This is the actual selector the rest of the venue pipeline consumes; our rule engine's output has to land here before tiles are built. |
| **`VenueHelper.CreateVenueTileEntity` / `CreateVenueTileEntities`** | Prefix (fallback patch point) | Known, verified surface. If the true `GridType` write site can't be pinned down safely, a Prefix here that (a) reads the fight context, (b) evaluates rules, (c) overwrites `CombatState.GridType` just before tile creation reads it, achieves the same effect as long as `GridType` hasn't already been consumed by something else upstream (e.g. diorama pre-sizing) — needs runtime verification, see §11. |
| **`VenueHelper.AdjustMaxEnemiesToPlayerCount`** | Postfix | Add `ExtendedGridBonusEnemySlots` to the vanilla result when grid is Extended. Exact signature/return type unverified — see §11; patch shape (adjust return value vs. adjust an out-param) depends on it. |
| **`VenueHelper.MakeRoomForSupportCharacters`** | Postfix or Prefix (TBD) | Apply `SupportCharacterExtraRoomSlots`. Exact signature unverified — see §11. |
| **`eLoadVenueGridRules`** (enum: `PREFERRED, EXISTING_POSITIONS, FRONT_ROW, BACK_ROW, ORDERED, ANY`) | read-only reference | Not patched; used only to understand how tile occupancy rules interact with grid size when reasoning about placement-tuning knobs. |

Everything in this table other than the two `AdjustMaxEnemiesToPlayerCount`/`MakeRoomForSupportCharacters`
postfixes is contingent on the M1 decompile pass confirming the actual `GridType` write site (§11). The mod
must fail safe: if the target method can't be found via `AccessTools` at startup, log loudly ("Target NOT
found: X") and leave the grid-selection feature disabled while still applying the (independent) placement
knobs if their targets resolve.

## 7. Example starting dataset

- `data/GridRules.json` — the four-rule default set from §4.1: Extended on `MinEnemyCount >= 5`, Extended
  on a 2x2 enemy plus 3+ others, Extended on party+summons `>= 5`, Standard fallback. Exercises every
  condition field except `EncounterType`/`CharacterIds`/`CharacterTags`/`HasBossEnemy` (left as documented
  but unused defaults, since we don't yet have verified enum/tag values to seed sensible examples for
  those — see §11).
- `data/Grids/example.NOT_FUNCTIONAL.json` — the M3 custom-grid sketch from §4.2, heavily commented,
  clearly marked non-functional, demonstrating the intended tile/row/group shape for future investigation.

## 8. Testing plan

Uses the debug/encounter-forcing toolkit (per `FTK2.DevKit` / existing debug console, if available) to
force encounters of varying sizes rather than grinding for them naturally.

1. **Rule correctness**: force a 2-enemy fight → Standard. Force a 5+-enemy fight → Extended. Force a
   4-enemy fight including one 2x2 tagged enemy → Extended. Force a fight with a summon-capable
   character in a small party such that `PartySize + PartyPlusSummonCapable >= 5` → Extended. Confirm each
   via `VerboseLogging` output showing which rule id matched.
2. **Kraken untouched**: force/enter a kraken boss fight; confirm `GridType` stays `BossKraken` regardless
   of rule contents, and that the rule engine's log line explicitly notes it skipped evaluation.
3. **Force-grid debug knob**: set `ForceGrid=Extended` and enter several different fights (including a
   would-be-kraken fight); confirm the grid is always Extended and the rule engine is bypassed (logged).
4. **Placement crash edge cases** (the actual risk surface of this mod):
   - `MakeRoomForSupportCharacters` on Extended with a full-size enemy wave — confirm no exception, no
     silently-unplaced character.
   - A 2x2 boss on Extended and on Standard — confirm `VenueComponent.OccupiedTiles` reflects the
     footprint correctly and no other entity is placed on an occupied tile.
   - Summon overflow: a fight that hits `AdjustMaxEnemiesToPlayerCount`'s cap while summons are actively
     being created mid-combat (`CombatHelper.TryCreateSummon` / `TryCheckForSummonAvailability`) on both
     Standard and Extended — confirm summons either place correctly or fail gracefully (no crash), and
     that the `ExtendedGridBonusEnemySlots` knob visibly raises the cap when toggled.
   - Toggle `Enabled=false` mid-testing and confirm full vanilla behavior returns (grid selection AND
     placement capacity), proving the fail-safe path.
5. **MP smoke test** (host + client, full detail in §9.6): trigger a 5-enemy fight → both host and
   client see `Extended`; trigger a 3-enemy fight → both see `Standard`; verify placement sanity
   (no occupied-tile collisions, no silent fail-to-place) on the client's own view, not just the host's.
   This also doubles as the M1 MP probe (§10) once `[Debug] ForceGrid` is wired up — host forces
   `Extended`, check whether the client renders it too.
6. Each of the above should be reachable and verifiable by a human in under 15 minutes with the shipped
   `GridRules.json` and the debug encounter-forcing toolkit; log everything relevant at `Debug` under
   `VerboseLogging`.

## 9. Multiplayer

No persistent state — nothing is saved (unchanged from the old §9). The risk is entirely **live desync
during combat**, because grid choice determines tile layout that both host and clients must agree on
identically. Structure below follows the mandated 6-point format in `docs/MULTIPLAYER.md` §"Standard
spec language".

### 9.1 Parity class

Depends on `docs/MULTIPLAYER.md` open question #3 — **not yet resolved**: does `CombatState.GridType`
sync natively over the network once set (the way `CombatComponent` is inferred to be netcode-synced per
`docs/research/game-code-reference.md` §2), or does every peer compute it independently from shared
encounter data? This is the same unresolved question as this SPEC's §11 item 2; the M1 decompile pass
answers both at once.

- **(a) If `GridType` syncs natively / host-authoritative:** parity class is **`HOST_ONLY`**. Only the
  host needs the rule engine active — it evaluates `GridRules.json` once, writes `CombatState.GridType`,
  and the network layer carries that value to every client for free. Clients never need to run their own
  copy of the rule engine; a client that *does* have the mod installed must treat its own evaluation as
  informational/logging-only, never authoritative (never write `GridType` from a client). This is the
  cleanest outcome and the one we design for by default.
- **(b) If `GridType` is computed independently per peer:** parity class is **`ALL_PEERS`** — every peer
  must run the mod with byte-identical `GridRules.json` (parity-hashed per R1) and, more subtly,
  identically-synced *inputs* to the rule engine, since deterministic rule evaluation over divergent
  inputs still produces divergent outputs. See §9.3 for which inputs are at risk.

**Recommendation:** design and ship for (a); verify which branch is actually true in the M1 decompile
pass (§10/§11) before relying on it. The `[Debug] ForceGrid` knob doubles as the verification probe —
host forces `Extended`, check whether the client renders it too — see §9.6 and §10.

**ParityService registration (R1):** under (a) `HOST_ONLY`, this mod's `ParityService` registration is
**informational only** — a version/data mismatch between host and a client produces a named warning
(which mod, version vs. data) but does not block or SafeMode the session, because a client's copy of
`GridRules.json` never drives an authoritative decision. Under (b) `ALL_PEERS`, the same mismatch is
**enforced**: `[Multiplayer] OnParityMismatch` (default `WarnAndSafeMode`) applies in full, since a
divergent `GridRules.json` on even one peer can produce a divergent grid.

### 9.2 Feature table

| Feature | Sync class | Authority |
|---|---|---|
| Rule engine grid selection (`GridRules.json` → `CombatState.GridType`) | `[SYNCED]` | Host (case a, recommended) or all peers under identical data (case b) |
| `[Debug] ForceGrid` | `[SYNCED]` | Host-only effect under (a); must be set identically on every peer under (b). Recommend treating this as host-only in MP regardless of which case is confirmed — a client-forced grid that isn't itself replicated is a guaranteed desync. |
| `[Placement] ExtendedGridBonusEnemySlots` / `SupportCharacterExtraRoomSlots` | `[SYNCED]` (derived from the already-agreed `GridType`) | All peers compute locally from the agreed `GridType`; no independent decision is made, but the two config values themselves still need parity (R1) |
| `VerboseLogging` | `[LOCAL]` | Any peer — presentation-only debug output, parity-exempt (R4) |

### 9.3 Determinism inventory (R2)

Rule evaluation is already deterministic by construction: `GridRules.json`'s `Rules` array is evaluated
**top-to-bottom, first full match wins** (§4.1) — a stable, data-defined order, not iteration over an
unordered collection. **There is no randomness anywhere in this mod's engine** — no `Random`, no
`GameRandom` roll, no tiebreak-by-chance. (If a future rule design ever introduced a random tiebreak
between equally-ranked rules, R2 requires it go through the game's shared deterministic `GameRandom` or
be decided host-side and synced — never local `System.Random`. No such tiebreak exists today; this is a
constraint on future changes, not a current fix.)

What *can* diverge is not the rule evaluation itself but its **inputs** — the context object built in
step 2 of §3's runtime flow. Enumerating the fields from §4.1's condition table and flagging sync risk:

| Context input | Backing condition field(s) | Per-peer divergence risk |
|---|---|---|
| `EnemyCount` | `MinEnemyCount`, `MaxEnemyCount` | Low if read after wave/encounter setup is fully replicated on every peer; **flag**: if evaluated mid-`_initializeNextWave` before all enemies are added everywhere, a client mid-setup could see a smaller count than the host |
| `HasTwoByTwoEnemy` | `HasTwoByTwoEnemy` | Low — depends on enemy config data (parity-hashed) and already-placed enemies; same setup-timing caveat as above |
| `HasBossEnemy` | `HasBossEnemy` | Low — id-prefix match against enemy config data, parity-hashed |
| `PartySize` | `MinPartySize`, `MinPartyPlusSummons` | Low — party composition is core game state, already synced by vanilla |
| `PartyPlusSummonCapable` (`CombatComponent.CanSummon`) | `MinPartyPlusSummons` | **Flag — highest risk**: summon-capable-ally count can change mid-setup if summons are created before this mod's patch fires relative to when each peer processes the same event. `CombatComponent` is inferred netcode-synced (code reference §2), but the *timing* of when each peer reads it relative to combat setup is unverified. If the rule engine runs before every peer is caught up to the same summon state, `MinPartyPlusSummons` can evaluate differently host vs. client. |
| `EncounterType` | `EncounterType` | Unseeded in the default rule set (§7) precisely because the context source is unverified (§11 item 4) — no live divergence risk today since no shipped rule uses it, but any future rule that does must confirm the value comes from already-synced encounter data, not something recomputed locally |
| `CharacterIds` / `CharacterTags` | `CharacterIds`, `CharacterTags` | Low — read from character config (parity-hashed) and combatant roster (synced) |

Net: under design (a) (`HOST_ONLY`), none of this matters for correctness — only the host's context and
evaluation ever produce the authoritative `GridType`. Under (b) (`ALL_PEERS`), the `PartyPlusSummonCapable`
row is the one input this SPEC cannot currently guarantee is safe without the M1 decompile pass
confirming exactly when the rule engine's patch point fires relative to summon-state replication.

### 9.4 Sync surface

- **Case (a) — recommended:** no custom sync action needed. `CombatState.GridType`, once written by the
  host, is carried to clients by whatever native mechanism already syncs it (per open question #3 — to
  be confirmed, not assumed). This mod's write is a normal field write on host; the vanilla pipeline
  does the rest.
- **Case (b) — fallback if (a) is false:** define a versioned snapshot action `VNU_SYNC_GRIDTYPE_V1`
  (host → clients, payload `{ GridType: string }`, fired immediately after the host's rule engine
  decision and before tile creation, idempotent to apply) so the host's decision is still authoritative
  even though vanilla doesn't carry it — this collapses case (b) back into the simplicity of case (a) at
  the cost of one small custom sync action, and is preferable to trusting independent `ALL_PEERS`
  agreement given the `PartyPlusSummonCapable` risk in §9.3. If adopted, this action needs a
  `VNU_SYNC_GRIDTYPE_REQUEST_V1` companion for mid-session (re)join, per MULTIPLAYER.md's practical
  guidance.
- The two placement-tuning knobs never need their own sync action: they're pure functions of `GridType`
  (already agreed) plus config values (parity-hashed), applied identically wherever
  `AdjustMaxEnemiesToPlayerCount` / `MakeRoomForSupportCharacters` run.

### 9.5 SafeMode definition

**SafeMode = vanilla grid selection.** On a parity mismatch (R1) that would otherwise put this mod in
`WarnAndSafeMode`:
- The rule engine (`GridRules.json` evaluation, `[Rules] RulesEnabled`) is disabled for the session;
  `CombatState.GridType` is left exactly as vanilla would compute it (`Standard` for non-kraken fights,
  `BossKraken` for kraken fights — the same "no rule matched" fallback path already described in §3
  steps 4-5; SafeMode simply forces that path unconditionally).
- `[Debug] ForceGrid` is also disabled in SafeMode (forcing a grid on a mismatched session is the
  opposite of safe).
- The `[Placement]` tuning knobs (`ExtendedGridBonusEnemySlots`, `SupportCharacterExtraRoomSlots`) are
  **not** disabled by SafeMode — they only adjust capacity math local to whichever grid vanilla already
  picked, and peers running slightly different placement slack produces a "character fails to place"
  bug, not a tile-layout desync (no shared state depends on them once `GridType` itself is vanilla).
  Still recommend keeping them parity-checked (R1) as informational-only under `HOST_ONLY`, same as the
  rule engine.
- Under `HOST_ONLY` (case a), SafeMode is largely moot for `GridType` itself — since only the host's
  decision matters, a mismatched client's disabled rule engine changes nothing observable. SafeMode is
  primarily relevant under `ALL_PEERS` (case b), or if case (b)'s `VNU_SYNC_GRIDTYPE_V1` fallback (§9.4)
  isn't yet implemented and peers are still independently evaluating rules.

### 9.6 MP test plan

Host + client smoke test (two real or simulated peers), additive to the single-player plan in §8:

1. Both peers on identical `GridRules.json` and config. Host triggers a 5-enemy fight (`MinEnemyCount`
   rule) → confirm **both host and client** render `Extended`. Host triggers a 3-enemy fight → confirm
   **both** render `Standard`.
2. Placement sanity check on the client: for the 5-enemy/`Extended` case, verify on the client's own view
   that no entity is placed on an occupied tile and no character silently fails to place
   (`VenueComponent.OccupiedTiles` sane) — the same checks as §8 item 4, observed client-side.
3. **The MP probe (also folded into M1, §10):** host sets `[Debug] ForceGrid=Extended`; enter any fight
   (including one that would vanilla-resolve to `Standard` or `BossKraken`) and confirm the **client**
   renders `Extended` too — without the client's own rule engine or force-grid knob doing anything. This
   single test empirically answers open question #3 / this SPEC's §11 item 2: client shows `Extended` →
   `GridType` syncs natively (case a confirmed); client still shows its own vanilla-computed grid → it's
   computed per-peer (case b confirmed), and this SPEC falls back to the `VNU_SYNC_GRIDTYPE_V1` sync
   action in §9.4 before shipping M2 for MP use.
4. Parity mismatch drill: deliberately run a stale/edited `GridRules.json` on the client only; confirm
   the ParityService warning fires naming this mod and the diverging part (version vs. data), and confirm
   session behavior matches §9.5 (informational-only under `HOST_ONLY`, SafeMode-enforced under
   `ALL_PEERS`).

## 10. Milestones

- **M1 — force-Extended knob**: `[Debug] ForceGrid` knob works end-to-end (patches whichever site actually
  controls `GridType`, confirmed via decompile), `[General] Enabled` fail-safe works, verbose logging
  scaffold in place. No rule engine yet — this milestone exists to nail down and verify the real patch
  point (§11) before building the rules engine on top of an assumption. **M1 also carries the MP probe
  (§9.6 item 3):** in a host+client session, host sets `ForceGrid=Extended` and the client is checked for
  whether its grid renders as `Extended` too. The result resolves open question #3 (does `GridType` sync
  natively, or is it computed per-peer) and decides this SPEC's actual parity class (§9.1) before the
  rule engine (M2) is built on top of an unverified MP assumption — same rationale as verifying the patch
  point itself, one decompile/runtime pass settles both.
- **M2 — rules engine**: `data/GridRules.json` loader, condition evaluation, default dataset from §7,
  kraken-skip logic, placement-tuning knobs (`ExtendedGridBonusEnemySlots`,
  `SupportCharacterExtraRoomSlots`) wired to their `VenueHelper` targets once signatures are confirmed.
  Full testing plan (§8) passes.
- **M3 — custom-grid research (exploratory only)**: investigate what it would take to make
  `VenueHelper.VenueMap1/ExtendedVenueMap1/KrakenMap1` data-driven and whether `Diorama.LoadVenueGrid` /
  `GetVenueGridRoot` / `dDiorama.VenueGrid` visual anchors can be repointed at custom layouts. Deliverable
  is a written investigation report (decompile findings + feasibility verdict), not working code. The
  `data/Grids/example.NOT_FUNCTIONAL.json` format may change entirely or be abandoned based on findings.

## 11. Open questions

1. **Where is `CombatState.GridType` actually assigned pre-combat?** Not found in
   `docs/research/game-code-reference.md`. Candidates to check via decompile: `CombatPhase`'s combat-setup
   methods (`_addEntityToCombat`, `_initializeNextWave`) or something in `EncounterPhase`. This blocks the
   real M1 patch and is the first thing to nail down.
2. **Is `CombatState.GridType` synced over the network, or computed independently per peer?** Determines
   this mod's parity class (§9.1: `HOST_ONLY` if synced, `ALL_PEERS` if not) and whether it needs the
   `VNU_SYNC_GRIDTYPE_V1` fallback sync action (§9.4). Not resolved by existing docs (only
   `CombatComponent` is noted as inferred netcode-synced). **This is the same question as
   `docs/MULTIPLAYER.md` open question #3** — that doc tracks it repo-wide, this item tracks it for
   Venue specifically; the M1 decompile pass (§10) resolves both at once via the MP probe (§9.6 item 3).
3. **Exact tag vocabulary for "boss" and "2x2" enemies.** `TWO_BY_TWO` is confirmed to exist as a concept
   in game data (used in a `QuestTemplates.json` `ObjectiveArgs` example, `"TWO_BY_TWO+PIRATE"`), and
   `VenueHelper.DEFAULT_TWO_BY_TWO_POSITION` confirms 2x2 footprint support in the engine — but whether
   `TWO_BY_TWO` is a `Characters.json` `Tags[]` entry (readable pre-combat from character config) versus
   something inferred at runtime from `VenueComponent.TileSize`/`OccupiedTiles` is unverified. Similarly,
   "boss" detection in §4.1 falls back to `BOSS_*` id-prefix matching because no confirmed `Tags[]` value
   for "is a boss" was found in `docs/research/data-schemas.md`.
4. **`EncounterType` context source.** No enum or field for "encounter type" was found in either reference
   doc (closest candidates: `EncounterPhase`, `CampQuery`/`SwarmQuery` composition strings, `EnemySet`
   parameter to `CharacterHelper.GetEnemiesForCombat`). The condition field ships in the schema for
   forward-compatibility but has no seeded example rule and may need to be cut if no usable context value
   exists at the patch point.
5. **Exact signatures of `VenueHelper.AdjustMaxEnemiesToPlayerCount` and `MakeRoomForSupportCharacters`.**
   Only method names are verified; parameter/return shapes are not, which determines whether the placement
   knobs are Postfixes that adjust a return value, Prefixes that adjust an argument, or something else.
6. **Whether `Configs.VenueGrids` / a hypothetical `VenueGrids.json` is real.** Code reference notes a
   `Configs.VenueGrids` field exists but no such JSON ships and it's flagged "likely built in code —
   unverified." If it turns out to be a genuine data-loadable registry, M3's custom-grid research gets
   dramatically easier and should re-target that instead of patching static map fields.
7. **`Diorama.LoadVenueGrid` / visual anchor feasibility** for M3 — completely unscoped pending decompile;
   documented here rather than estimated, per the milestone's exploratory framing.
8. **Repo-wide MP open questions** are tracked centrally in `docs/MULTIPLAYER.md` ("Open questions to
   resolve in the decompile pass"); folding in which of that numbered list bears on this mod:
   - **#3 (`CombatState.GridType` sync)** is this SPEC's item 2 above, verbatim — the one that actually
     matters for Venue, and the one the M1 milestone's MP probe (§9.6 item 3) is designed to answer.
   - **#5 (exact payload shape/size limits of `_handleNetworkAction`)** only matters to this mod if item 2
     resolves to "computed per-peer" and the `VNU_SYNC_GRIDTYPE_V1` fallback (§9.4) actually ships — its
     payload (`{ GridType: string }`) is trivially small, so this is a low-risk dependency even then.
   - **#1 (AI host-only), #2 (`GameRunData` replication), #4 (`_rebuildCharactertAsNewConfigType`
     propagation)** don't apply to this mod — Venue has no AI logic, touches no `GameRunData`, and does
     no character-class rebuilding (§3 "State lifecycle": no persistent state, nothing touches
     `GameRunData` or save files).
