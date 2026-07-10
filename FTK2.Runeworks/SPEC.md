# FTK2.Runeworks — SPEC

Plugin GUID: `ftk2mods.runeworks` · Id prefix: `RW_` · Priority: **P2**

## 1. Purpose & scope

Runeworks is a socket/rune/gem engine: dropped gear can carry sockets, and gems (a new lootable,
stackable item family) can be slotted into those sockets to add stat deltas, passives, granted
abilities, and on-hit status procs, with optional cross-item "set bonuses" for gems of the same
family. It is the FTK2 analogue of an affix/enchanting system, built the way EOR's crafting-on-items
affix system was built: a layer on top of the vanilla item pipeline, not a replacement for it.

**In scope:** gem items as a data-driven registry (`data/Gems/*.json`); a socket-count rules file
(by rarity/class/tag); the socketing/unsocketing player interaction (context menu); stat/passive/
ability/proc application; optional set bonuses; knobs for all of the above.

**Deliberately out of scope:** a crafting-station UI for socketing (this is a context-menu action on
the item itself, not a CraftConfigs recipe); gem upgrading/combining; visual VFX for socketed gems;
any new `eCombatActions`/`eStatusEffectTypes`/skill-proc *engine* work beyond what a gem needs to
prove out (M2 ships exactly one proc-carrying gem and one skill-passive-carrying gem, not a general
proc authoring tool). Runeworks does not modify base `Things.json`; it ships/merges its own gem items
the way EOR's `CustomItems\Things\*.json` packs are merged (see §9 open question on the exact
runtime hook).

## 2. Player-facing behavior

- Rare/uncommon/epic/legendary gear now drops (or is found in markets) with sockets — visible on the
  item's tooltip as empty socket icons/text, count driven by rarity (see `SocketRules.json`).
- Gems drop as their own lootable item (market-tagged, stackable) with a tooltip describing their
  effect.
- Right-clicking (or opening the context menu on) a socketed item shows a new **"Socket Gem…"**
  entry when it has an empty socket and the player holds at least one gem whose socket-type affinity
  matches the item (`WEAPON`/`ARMOR`/`TRINKET`/`ANY`). Picking a gem consumes it and immediately
  updates the item's stats/passives/tooltip.
- A socketed item's tooltip lists each socketed gem and its effect, plus set-bonus progress
  ("2/2 RUBY gems socketed in party — bonus active") when applicable.
- A socketed item shows a **"Remove Gem…"** entry. Depending on the `UnsocketPolicy` knob, removing
  a gem either destroys it, refunds it, or costs gold (default: destroy).
- Gems and socketed items behave like normal items for every other purpose the player cares about:
  they can be dropped, sold, traded between party members, and used in multiplayer exactly as in
  single-player whenever every peer's Runeworks install and data match (parity-gated; see §9).

## 3. Architecture

### 3.1 Engine vs data split

The engine (C# plugin) knows nothing about any specific gem, stat number, or socket count. It:

- loads `data/Gems/*.json` into an in-memory `GemDefinition` registry (hot-reloadable, one file per
  gem, new files picked up automatically — matches the "AI profile / evolution chain" registry
  pattern from CONVENTIONS.md);
- loads `data/SocketRules.json` once to know sockets-per-item and socket-type groupings;
- implements the *mechanism* of turning "base item + N gem ids" into a playable socketed item, the
  *mechanism* of rolling on-hit procs, and the *mechanism* of counting set-bonus membership across a
  party.

All tuning numbers (stat deltas, proc chances, socket counts, set-bonus thresholds) live in JSON.

### 3.2 Candidate architectures for socket state

Two candidates were asked for; both are specified here, with a recommendation.

**(a) EOR-style item-variant wrapper.** Socketing a gem into an item generates a new `ThingConfig`
id deterministically from `(BaseItemId, sorted GemIds[])`, following the id shape
`docs/MULTIPLAYER.md` R2 mandates for any mod-generated content id — `RW_VAR_<BaseItemId>_<gemhash>`
(e.g. `RW_VAR_WEAPON_LONGSWORD_4f2a91c7`, where `gemhash` is a stable hash over the sorted
`GemIds[]`, never insertion order or a runtime GUID) — whose `Equippable.Stats`/`Passives` are the base
item's plus every socketed gem's `Effects.Stats`/`Effects.Passives` merged in, and whose
`Interactable.AbilityBag` gets any `GrantedAbilities`. That generated config is registered into the
game's live `Configs.Things` registry (mirrors how EOR's `CustomItems` packs and the pets/mercs JSON
packs are merged in at runtime) and the specific inventory item instance's config-id reference is
swapped to point at it. This is exactly the technique credited to EOR's
`AffixDefinition`/`AffixedItemVariant` (decompile target, see §11).

**(b) Sidecar instance-state on `GameRunData`.** The item keeps its original config id. A dictionary
keyed by item-instance identity → `List<GemId>` is persisted on `GameRunData` (the repo's proven
per-run-state piggyback). All stat/passive/ability contributions are computed live via postfixes on
`CharacterHelper.GetStat` (numeric stats) and `UIHelper.GetBreakdownStats` (tooltip breakdown), with
`ItemCardViewHelper.ShowItemCard*` patched to append socket info.

**Tradeoff analysis:**

| Concern | (a) Item-variant wrapper | (b) Sidecar instance-state |
|---|---|---|
| Item identity | Socketed item *is* a different config id — matches player intuition ("a different sword now") | Config id unchanged; "identity" only exists in the sidecar map |
| Stacking | Naturally correct: two items with the same base + same gems generate the same variant id and legitimately stack; different socket loadouts never incorrectly merge | **Risk**: if base-game stacking is keyed by config id (per `ThingConfig.Stacks`, `data-schemas.md`), two differently-socketed copies of the same base item could stack and silently lose one item's socket data unless we can also prevent that at the inventory layer — unverified whether that hook exists |
| Trade / drop | Works for free — it's a normal item as far as inventory/trade/drop code is concerned | Works for free on the item side, but the *sidecar record* must travel with the item (to another party member's slot, to the ground, to a trade) or the gems are silently orphaned — requires patching every inventory-move path, not just stat display |
| Stat/tooltip patches needed | **None** for stats/passives/abilities — they're baked into the generated config the game already understands; only the context-menu and tooltip-annotation patches are needed | Required on `CharacterHelper.GetStat` *and* `UIHelper.GetBreakdownStats` *and* every other stat consumer we haven't enumerated — larger patch surface, more places to miss |
| Save persistence | Generated configs are in-memory only — must be regenerated (replayed) from a small persisted "socket formula" list on load, *before* anything reads the item | Sidecar map persists directly on `GameRunData`; no replay step, but still needs the item-instance-move problem above solved |
| MP sync (co-op is a **hard requirement**, `docs/MULTIPLAYER.md`) | **Free, by construction.** Once registered, the variant *is* a real `ThingConfig` — R1's parity hash covers it the same way it covers every other config, so the only thing that needs to cross the wire is the small formula record `{BaseItemId, GemIds[]}` (§9.4); every peer derives the byte-identical `RW_VAR_<baseId>_<gemhash>` id per R2 and registers it locally. Divergence is self-evident (different item stats on different screens) and is exactly what ParityService is built to catch. | Would need a **bespoke** sync channel for the sidecar map itself, *and* a bespoke parity check for it — R1's Configs-hash coverage never reaches a plugin-private dictionary — on top of the instance-identity question below. This is strictly more custom-sync surface than (a) needs, for a feature class (`docs/MULTIPLAYER.md`'s "practical guidance") that says to prefer config-shaped content precisely so it doesn't have to be built. |

**Recommendation: (a), the item-variant wrapper — and multiplayer is now the deciding factor, not
just a tiebreaker.** Before co-op was a hard requirement this recommendation rested on patch-surface
and stacking-correctness arguments alone; both still hold (no `GetStat`/`GetBreakdownStats` postfixes
needed, stacking is correct by construction). But `docs/MULTIPLAYER.md` changes the calculus directly:
a real, parity-hashed `ThingConfig` on every peer is the textbook case R1 and R2 were written for
("config-shaped content over runtime state... syncs for free"), whereas (b)'s sidecar dictionary is
exactly the kind of plugin-private state that R1's Configs-hash doesn't see and that would need its
own custom sync + its own parity story from scratch — effectively rebuilding, by hand, what (a) gets
from the game's existing config pipeline. Given MP is no longer optional, that difference alone would
be enough to pick (a) even if the patch-surface/stacking arguments were a wash. Its costs are: (1)
needing a runtime config-registration hook (open question, §11) and (2) needing to replay the "socket
formula" list on load before any UI touches the item (§9). Both are one-time plumbing costs, not
recurring patch-surface costs like (b)'s, and (2) is unchanged by the MP requirement since it's a
per-peer, save-load-time concern, not a cross-peer one.

**Cross-cutting open question that affects both architectures:** neither `game-code-reference.md`
nor `data-schemas.md` documents how a specific *inventory item instance* (as opposed to its
`ThingConfig` id) is identified at runtime. Both candidates need this — (a) to know which slot's
config-id reference to swap, (b) to key the sidecar dictionary. Flagged in §11; must be resolved by
decompile before implementation starts.

### 3.3 Runtime flow (architecture (a), as recommended)

1. Player picks "Socket Gem…" on an eligible item → picks a gem from inventory.
2. Engine validates: item has ≥1 empty socket (per `SocketRules.json`), gem's `SocketType` matches
   the item's class group (`WEAPON`/`ARMOR`/`TRINKET`/`ANY`).
3. Engine computes `VariantId = RW_VAR_<BaseItemId>_<gemhash(sorted currently-socketed GemIds + new
   GemId)>` — a pure function per MULTIPLAYER.md R2 (sorted iteration, invariant-culture, stable hash,
   no wall-clock/GUID/insertion-order inputs) — merges `Effects.Stats`/`Effects.Passives`/
   `GrantedAbilities` from every socketed `GemDefinition` on top of the base `ThingConfig`, and
   registers the resulting config (if not already registered this session).
4. Engine swaps the item instance's config-id reference to `VariantId`, consumes the gem, and appends
   a `SocketRecord {VariantId, BaseItemId, GemIds[]}` to the `RW_SocketRecords` list piggybacked on
   `GameRunData`.
5. Engine invalidates/recomputes party set-bonus state (party-wide scan of all equipped gems'
   families) — cheap enough to redo on every socket/unsocket and at battle start.
6. On game load, before any inventory/UI code can read an item, the engine replays every
   `SocketRecord` in `RW_SocketRecords` to regenerate and re-register the same variant configs (they
   don't survive a process restart since `Configs.Things` is in-memory).

### 3.4 State lifecycle

- **Per-battle:** nothing persistent; only a cached "which set bonuses are currently active for this
  party" snapshot, recomputed at battle start (`CharacterHelper.InitializePartyStats`) and safe to
  discard/recompute anytime.
- **Per-run (persistent):** `RW_SocketRecords` list on `GameRunData` (the socketing "formula" history
  needed to replay variant generation after a save load) — this is the only thing that must survive
  a save.
- **Session-only (never saved):** the generated `Configs.Things` variant entries themselves — derived
  data, always reconstructable from `RW_SocketRecords`.

## 4. Data file formats

### 4.1 `data/Gems/*.json` — one gem per file

```jsonc
{
  // Mod content id, RW_ prefix, uppercase-underscore (Naming convention).
  "Id": "RW_GEM_RUBY_2",

  // Gem's own item tier (loot-table power level), independent of the item it's socketed into.
  "Tier": 2,

  // Which item class-group this gem may be socketed into. One of WEAPON | ARMOR | TRINKET | ANY.
  // Class-group membership (which ThingConfig.Class values count as WEAPON/ARMOR/TRINKET) is
  // defined once in SocketRules.json, not repeated per gem.
  "SocketType": "WEAPON",

  // --- Fields that become the gem's own droppable ThingConfig (see open question on Class, §11) ---
  "DisplayName": "RW_GEM_RUBY_2",          // localization key, see Localization/en.json
  "Rarity": "UNCOMMON",
  "Value": 75,
  "Stacks": true,
  "Tags": ["DROPPABLE", "TOWN_MARKET", "DUNGEON_MARKET"],

  // --- Gameplay payload applied to whatever item this gem is socketed into ---
  "Effects": {
    // Flat additive deltas merged into the target item's Equippable.Stats. Keys must be real
    // Characters.json/Equippable stat keys: ACC ATK AWR CRT DEF EVD FOC HP HRG INT LCK PA SA PRW
    // RES SPD STR TAL THRN VIT.
    "Stats": { "ATK": 4 },

    // SKILL_* / STATUS_IMMUNITY_* ids merged into the target item's Equippable.Passives.
    "Passives": [],

    // Ability ids (Abilities.json) added to the target item's Interactable.AbilityBag.
    // Left empty across all six example gems — see open question §11.10 (we don't have a verified
    // Abilities.json id to reference without inventing one).
    "GrantedAbilities": [],

    // Optional on-hit status proc. Chance is rolled once per successful hit landed by this item
    // (see §6, CombatHelper.ApplyAction postfix). StatusId must be a real StatusEffects.json key.
    "OnHitProc": { "Chance": 0.15, "StatusId": "FIRE" },

    // Optional set bonus. Family is an arbitrary grouping key the engine sums across every
    // socketed gem in the whole party (all characters, all equipped items). "RW_ANY" is a reserved
    // wildcard family meaning "any Runeworks gem, regardless of its own Family". When the party-wide
    // count for Family reaches MinCount, every party member gets Stats/Passives applied for the
    // duration the threshold holds (recomputed on socket/unsocket and at battle start).
    "SetBonus": null
  }
}
```

### 4.2 `data/SocketRules.json` — socket counts + class groupings (single file)

```jsonc
{
  // Master toggle for this file; if false, engine falls back to Default for every item.
  "Enabled": true,

  // Sockets granted when no more specific rule matches.
  "Default": 0,

  // Socket count by ThingConfig.Rarity. Checked after ByClass/ByTag; first match in this priority
  // order wins: ByTag > ByClass > ByRarity > Default.
  "ByRarity": {
    "COMMON": 1,
    "UNCOMMON": 1,
    "RARE": 2,
    "EPIC": 2,
    "LEGENDARY": 3
  },

  // Socket count override by ThingConfig.Class (e.g. trinkets always get exactly 1 regardless of
  // rarity).
  "ByClass": {
    "ARMOR_TRINKET": 1
  },

  // Socket count override by ThingConfig.Tags entry (checked in array order, first match wins).
  "ByTag": {},

  // Item classes eligible for sockets at all (everything else gets 0 regardless of the rules
  // above). Verbatim ThingConfig.Class values from data-schemas.md.
  "EligibleClasses": [
    "BLADE", "KATANA", "DAGGER", "RAPIER", "AXE", "BLUNT", "SPEAR", "WHIP", "WAND", "LONGSTAFF",
    "BOW", "RIFLE", "GUN", "SHOTGUN", "HANDBOW", "CANNON", "BOOMERANG", "BOMB",
    "SHIELD", "ARMOR_BODY", "ARMOR_HEAD", "ARMOR_HANDS", "ARMOR_FEET", "ARMOR_TRINKET"
  ],

  // Class-group membership used to validate a gem's SocketType against a target item.
  "SocketTypeGroups": {
    "WEAPON": [
      "BLADE", "KATANA", "DAGGER", "RAPIER", "AXE", "BLUNT", "SPEAR", "WHIP", "WAND", "LONGSTAFF",
      "BOW", "RIFLE", "GUN", "SHOTGUN", "HANDBOW", "CANNON", "BOOMERANG", "BOMB"
    ],
    "ARMOR": ["SHIELD", "ARMOR_BODY", "ARMOR_HEAD", "ARMOR_HANDS", "ARMOR_FEET"],
    "TRINKET": ["ARMOR_TRINKET"]
  },

  // What happens on "Remove Gem…". DESTROY_GEM | REFUND_GEM | GOLD_COST. Mirrored by the
  // UnsocketPolicy knob (knob wins if set to something other than "UseDataFile").
  "UnsocketPolicy": "DESTROY_GEM",

  // Only read when UnsocketPolicy == GOLD_COST.
  "UnsocketGoldCost": 100
}
```

### 4.3 `data/Localization/en.json` — EOR-style `ID`/`ID_DESCRIPTION` pairs

One entry per gem (`DisplayName` key + `_DESCRIPTION`) plus the two UI strings the context-menu
patch adds. See shipped file for the full list.

## 5. Knobs

`[General]`
- `Enabled` (bool, `true`) — master switch; if data fails to parse, log loudly and behave as if
  `false` (fails safe, per CONVENTIONS.md).
- `VerboseLogging` (bool, `false`) — log every socket/unsocket/proc-roll/set-bonus-recompute decision
  at `LogLevel.Debug`.

`[Sockets]`
- `SocketCountOverride` (int, `-1`) — if `>= 0`, forces every eligible item to exactly this many
  sockets, ignoring `SocketRules.json` entirely. Testing/balance knob.
- `UnsocketPolicy` (string enum `DESTROY_GEM`/`REFUND_GEM`/`GOLD_COST`/`UseDataFile`, default
  `UseDataFile`) — overrides `SocketRules.json`'s `UnsocketPolicy` when set to anything else.
- `UnsocketGoldCost` (int, `100`) — overrides `SocketRules.json`'s `UnsocketGoldCost` when
  `UnsocketPolicy` resolves to `GOLD_COST`.

`[Gems]`
- `GemDropRateMultiplier` (float, `1.0`) — multiplies whatever weight gems are given in loot tables
  (exact loot-table integration point is an open question, §11).

`[SetBonus]`
- `EnableSetBonuses` (bool, `true`) — if `false`, `SetBonus` blocks in gem definitions are parsed but
  never applied (stats/passives from individually socketed gems still work).

`[Multiplayer]`
- Socketing has no separate on/off switch for MP: it is **enabled by default** whenever
  ParityService (`docs/MULTIPLAYER.md` R1) confirms every peer runs the same Runeworks version and
  data. This replaces the previous `AllowInMultiplayer` opt-in-and-default-off knob — co-op is the
  target, not an escape hatch (see §9).
- `OnParityMismatch` (string enum `Block`/`WarnAndSafeMode`/`WarnOnly`, default `Block`) — overrides
  the repo-wide default (`WarnAndSafeMode`) for this mod specifically. Recommended and defaulted to
  `Block` because socketed items are shared, tradeable inventory state (§9.5): a peer that can't
  reconstruct a variant config isn't just missing a feature, it's holding an item the game can't
  resolve for it. `WarnAndSafeMode`/`WarnOnly` remain available for anyone who wants the repo-wide
  default instead.

## 6. Patch targets & integration points

All targets are verbatim from `docs/research/game-code-reference.md`; "why" ties each to a specific
piece of §3's flow.

- **`InventoryViewHelper.ShowContextMenu`** (proven EOR patch target) — Postfix. Adds "Socket Gem…"
  to an eligible item's context menu when it has an empty socket and the player holds a matching
  gem; adds "Remove Gem…" when it has ≥1 socketed gem. M1.
- **`ItemCardViewHelper.ShowItemCard*`** (proven EOR patch target) — Postfix. Appends the socketed
  gem list and, if active, set-bonus progress text to the tooltip. M1 (gem list), M3 (set-bonus
  progress).
- **`CombatHelper.ApplyAction`** (proven EOR patch target, Postfix per EOR precedent) — Postfix on
  the `CHANGE_STAT`/damage branch. When the acting item's generated variant carries an `OnHitProc`,
  roll its `Chance` and, on success, call `InteractableHelper.ApplyStatus` with its `StatusId`. This
  is the concrete, verified-target mechanism chosen over trying to make a brand-new `SKILL_*` id
  proc generically — `data-schemas.md` explicitly warns new skill ids "do nothing without a C#
  handler," so we don't rely on that path (see open question §11.5). M2.
- **`CharacterHelper.InitializePartyStats`** (listed EOR-adjacent `CharacterHelper` method) —
  Postfix. Recomputes each `SetBonus.Family` count across the whole party's equipped/socketed gems
  and applies/removes the aggregate Stats/Passives. Also called directly (not just via patch) by our
  own socket/unsocket handler so the UI updates immediately, not just at next battle start. M3.
- **`ConfigsHelper` / `Configs.Things`** — not a Harmony patch but the runtime config-registration
  entry point for generated variant `ThingConfig`s (architecture (a), §3.3 step 3) and for merging
  the gem items themselves in at startup (EOR `CustomItems`-pack style). Exact insertion API
  (public setter vs. reflection into a private dictionary) is unverified — open question §11.2. M1.
- **`GameRunData`** (piggyback field, proven per-run-state pattern) — carries `RW_SocketRecords`
  (the persisted socket-formula list, §3.3 step 6). M1.
- **`AdventureDirector._handleNetworkAction`** (proven custom-network-action piggyback) — custom
  `RW_SOCKET_V1` action: a non-host peer's "Socket Gem…" sends `{ItemInstanceId, BaseItemId,
  GemIds[]}` to the host as a request; the host validates and performs the socket op (§9.4), then
  re-broadcasts the confirmed record to every peer (including itself) so each independently derives
  and registers the identical `RW_VAR_...` variant id/config before/at the same time the vanilla
  item-reference swap lands. `RW_UNSOCKET_V1` mirrors it for gem removal. Always wired up (no
  MP on/off knob — see §5/§9); the socket/unsocket op always executes host-side, never locally by a
  non-host peer, so two peers can't race to fill the same socket. M1.
- **ParityService** (FTK2.DevKit, reflection-based registration, `docs/MULTIPLAYER.md` R1) —
  Runeworks registers `(guid=ftk2mods.runeworks, version, dataHash-over-data/Gems/*.json+
  SocketRules.json, enabledFeatures=[Sockets, SetBonus])` at boot; drives the `OnParityMismatch`
  knob (§5) and the SafeMode/Block behavior in §9.5. M1.

## 7. Example starting dataset

Six gems across two "real" families plus two standalone gems, chosen to exercise every payload
type the schema supports except `GrantedAbilities` (see §11.10):

| File | SocketType | Effect | Demonstrates |
|---|---|---|---|
| `RW_GEM_RUBY_1.json` | WEAPON | `+2 ATK` | baseline stat-only gem, `RUBY` family (no bonus yet) |
| `RW_GEM_RUBY_2.json` | WEAPON | `+4 ATK`, 15% on-hit `FIRE` proc | `OnHitProc`, `RUBY` family |
| `RW_GEM_SAPPHIRE_1.json` | ARMOR | `+2 DEF` | baseline stat-only gem |
| `RW_GEM_SAPPHIRE_2.json` | ARMOR | `+3 DEF / +2 RES`, `STATUS_IMMUNITY_WATER` passive | `Passives` (immunity) |
| `RW_GEM_ONYX.json` | ANY | `+3 CRT`, `SKILL_ELITEAMBUSH` passive | `Passives` (real skill id), `SocketType: ANY` |
| `RW_GEM_STARSTONE.json` | TRINKET | `+3 LCK`, set bonus at 2 gems of family `RW_ANY` (+2 LCK party-wide) | `SetBonus` wildcard family |

`RUBY` family also carries a matching `SetBonus` (`MinCount: 2`, `+2 ATK` party-wide) declared
identically on both `RW_GEM_RUBY_1` and `RW_GEM_RUBY_2`, so the dataset exercises both a
family-specific set bonus and `STARSTONE`'s cross-family wildcard simultaneously.

`data/SocketRules.json` ships with the standard rarity ladder (`COMMON`→1, `RARE`/`EPIC`→2,
`LEGENDARY`→3) and trinket-always-1 override, so the six example gems can be tested against gear of
every socket count from 1 to 3.

## 8. Testing plan

Executable in <15 minutes with the shipped example data, per CONVENTIONS.md. Run with
`VerboseLogging=true`.

1. **Boot check.** Enable the mod, start/load a run. Log should show the six gems and
   `SocketRules.json` parsed with no errors, and the six `RW_GEM_*` items registered as droppable.
2. **Basic socketing (M1).** Obtain a RARE weapon (2 sockets) and an `RW_GEM_RUBY_1`. Context-menu
   "Socket Gem…" → pick the gem. Confirm: gem consumed, item tooltip shows `+2 ATK` and one filled /
   one empty socket, log shows the generated variant id.
3. **Multi-socket + proc (M2).** Socket `RW_GEM_RUBY_2` into the same weapon's second socket. Confirm
   tooltip now shows `+6 ATK` total and the `FIRE` on-hit proc description. Fight a trash encounter
   and confirm the proc fires roughly at the stated rate over ~20 hits (log line per roll) and that
   the weapon's normal attack is otherwise unaffected (regression check).
4. **Passive-granting gem (M2).** Socket `RW_GEM_SAPPHIRE_2` into a RARE armor piece. Confirm
   `STATUS_IMMUNITY_WATER` appears among the character's passives and a scripted `WATER` status
   application on that character is prevented.
5. **`ANY` socket type + skill passive (M2).** Socket `RW_GEM_ONYX` into any eligible weapon *or*
   armor piece (confirm both accept it, since `SocketType: ANY`). Confirm `+3 CRT` and that
   `SKILL_ELITEAMBUSH` shows up on the character sheet's passive list.
6. **Set bonus, family-specific (M3).** Socket a second `RUBY`-family gem anywhere in the party (e.g.
   `RW_GEM_RUBY_1` on a second weapon). Confirm the `+2 ATK` party-wide set bonus activates (tooltip
   set-bonus text, and `CharacterHelper.GetStat`-level check on an unrelated party member). Unsocket
   one Ruby gem; confirm the bonus deactivates.
7. **Set bonus, wildcard family (M3).** With any 2 Runeworks gems socketed anywhere in the party,
   socket `RW_GEM_STARSTONE`. Confirm the `RW_ANY` set bonus activates at the 2-gem threshold
   regardless of family mix.
8. **Unsocketing policy.** Default `DESTROY_GEM`: remove a gem, confirm it's gone. Set
   `UnsocketPolicy=GOLD_COST`, `UnsocketGoldCost=50`: remove another gem, confirm 50 gold deducted
   and the gem returned to inventory.
9. **Save/reload.** Save mid-run with at least 2 sockets filled across 2 items, reload. Confirm both
   items still resolve to their correct variant stats/passives/procs (validates `RW_SocketRecords`
   replay-on-load).
10. **Edge cases.** (a) Try socketing a `WEAPON`-type gem into armor → rejected, logged. (b) Try
    socketing a `COMMON` item with no rarity override → "Socket Gem…" absent/disabled (0 sockets).
    (c) Try socketing a 3rd gem into a 2-socket item → rejected. (d) Set
    `SocketCountOverride=3` → previously-2-socket items now show a 3rd empty socket without touching
    already-socketed data.
11. **MP smoke test** (manual/opt-in, host + 1 client, both on matching Runeworks version+data — not
    required to pass the <15 minute solo loop; full detail in §9.6).
    a. Boot both peers, confirm the `FTK2MODS_PARITY_V1` handshake logs a match for `ftk2mods.runeworks`.
    b. **Client sockets a gem:** on the non-host peer, socket `RW_GEM_RUBY_1` into an eligible weapon.
       Confirm the client's own view updates (item shows `+2 ATK`, one socket filled). On the **host**,
       open the same item (inventory view or a DevKit dump-compare of `Configs.Things[VariantId]`) and
       confirm it shows the identical `RW_VAR_...` id and `+2 ATK` — not just "some socketed item."
    c. **Unsocket:** from either peer, remove the gem. Confirm both peers agree the item reverted to
       base stats and the gem was destroyed/refunded/gold-charged identically per `UnsocketPolicy`.
    d. **Mismatch test:** restart the client with a locally-edited `RW_GEM_RUBY_1.json` (different
       `ATK` value) so its `dataHash` diverges, then attempt to join. Confirm ParityService reports the
       mismatch naming Runeworks + "data," and — per the `OnParityMismatch=Block` default (§5) — the
       join is refused rather than silently entering SafeMode.

## 9. Save & multiplayer considerations

Co-op is a **hard requirement** for this repo (`docs/MULTIPLAYER.md`). This section follows that
doc's mandated §9 structure; the "why" for each answer is that Runeworks mutates a shared, tradeable
inventory item — the exact class of feature `docs/MULTIPLAYER.md` calls out as needing all peers on
the same mod+data.

**Persistence** (unaffected by the MP requirement — this is per-peer save state, not cross-peer sync).
Only `RW_SocketRecords` (`{VariantId, BaseItemId, GemIds[]}` per socketed item) is persisted,
piggybacked on `GameRunData` per the repo's proven per-run-state pattern. Everything else (generated
`Configs.Things` variant entries, cached set-bonus state) is derived and rebuilt: on load, before any
UI/inventory code can read a socketed item, the engine replays every `SocketRecord` to regenerate its
variant config deterministically, on each peer, from that peer's own save.

### 9.1 Parity class: `ALL_PEERS`

Both gem definitions and generated variant configs merge into `Configs.Things` — the game simulates
stats/passives/abilities straight out of that registry. A peer missing Runeworks, or running
different `data/Gems/*.json`/`SocketRules.json`, cannot resolve a socketed item another peer created,
so every peer must run the same mod at the same version with the same data. There is no `HOST_ONLY`
or `LOCAL` fallback mode for this mod's core loop.

### 9.2 Feature table

| Feature | Sync class | Authority |
|---|---|---|
| Gem registry / `SocketRules.json` load | `[SYNCED]` (data, via ParityService dataHash) | all peers, identical data required |
| Socket / Unsocket player action | `[SYNCED]` | **host** — always executes host-side (§9.4), regardless of which peer initiated it |
| Variant `ThingConfig` generation + registration | `[SYNCED]` (effect) | all peers — each independently runs the same deterministic function on receipt of the confirmed socket record |
| Inventory item config-id swap + gem consumption | `[SYNCED]` via vanilla inventory/equipment pipeline (assumption — unverified by decompile, see §11.4) | host performs it; effect replicates |
| Set-bonus party-wide aggregation (`CharacterHelper.InitializePartyStats`) | `[SYNCED]` (effect, derived) | all peers — recomputed identically from each peer's own already-synced party/inventory state, no separate wire format needed |
| `OnHitProc` chance roll (`CombatHelper.ApplyAction` postfix) | `[SYNCED]` | must use the game's deterministic `GameRandom` (R2) or be decided host-side and synced — **not** a local `System.Random` roll (flagged fix, §11) |
| Tooltip / context-menu presentation | `[LOCAL]` | each peer's own UI (R4, parity-exempt) |
| `VerboseLogging` | `[LOCAL]` | each peer (R4) |

### 9.3 Determinism inventory

- **Variant id + effect merge** (the mod's one piece of generated content, R2): pure function of
  `(BaseItemId, sorted GemIds[])` → `RW_VAR_<BaseItemId>_<gemhash>`, sorted iteration, invariant
  culture, stable hash — never insertion order, wall-clock, or a runtime GUID. This was already
  flagged as "the single riskiest correctness property of architecture (a)"; it now also gets an M1
  test (§8.11.b/c) rather than being a documentation-only warning.
- **Set-bonus aggregation**: pure function of current party equip/socket state; no RNG, no ordering
  sensitivity — recomputed identically on every peer by construction.
- **`OnHitProc` roll**: currently unspecified in §6 as anything other than "roll `Chance`" — that must
  be nailed down to the shared deterministic `GameRandom` (EOR `EOR_SHARED_RNG` pattern) before M2
  ships `RW_GEM_RUBY_2`, or the fire-proc will visibly desync between peers watching the same hit.
- **`RW_SocketRecords` replay-on-load**: deterministic replay of already-persisted formulas; no
  cross-peer concern since it's per-peer, from that peer's own save file.

### 9.4 Sync surface — the socketing operation itself

The open question the MP requirement surfaces that didn't exist under a single-player-only reading of
this spec: when a client sockets a gem, where does the inventory mutation (consume gem, swap the
item's config-id reference to the variant) actually execute, and how does it replicate?

**Option (a) — host-routed custom action (recommended).** The acting peer's "Socket Gem…"/"Remove
Gem…" sends a `RW_SOCKET_V1` / `RW_UNSOCKET_V1` request (`{ItemInstanceId, BaseItemId, GemIds[]}`) to
the host via `AdventureDirector._handleNetworkAction`. The host validates (socket empty, gem held,
`SocketType` match) and performs the exact same engine flow as single-player (§3.3) — this is the one
and only place the mutation executes. The host then re-broadcasts the confirmed record to *every*
peer, including itself, so each independently derives and registers the identical `RW_VAR_...` config
before/at the same moment the vanilla item-reference swap and gem consumption replicate. No peer is
authoritative over *content* (the deterministic function guarantees that); the host is authoritative
over the *sequence* of socket/unsocket events, which rules out two peers racing to fill the same
socket.

**Option (b) — each peer executes locally in lockstep.** Every peer runs the socket op independently
when it observes the player's input, relying on all peers reaching the same result because the
generation function is deterministic. **Risky and rejected:** it requires the *input* to socketing
(which item, which gem, in what order) to already be perfectly synced before either peer acts, which
is precisely the ordering problem R3 exists to avoid ("host authority for decisions; vanilla pipeline
for effects"). A network hiccup or a race between two players trying to use the last gem in a shared
stash produces divergent inventory state with no single authority to arbitrate it. Rejected in favor
of (a).

**Recommendation: (a).** It matches R3 directly (host decides, vanilla-shaped effects replicate) and
keeps the deterministic-generation guarantee (R2) as the only thing peers have to agree on, rather
than also needing to agree on event ordering.

**Corollary: the variant config must already exist on all peers before/when the replicated item
reference lands.** Two ways to guarantee that:

- **Pre-generate all combinations at load.** Rejected — combinatorial explosion. Combos-with-repetition
  of `g` gem types across `k` sockets is `C(g+k-1, k)`; with the six shipped example gems (`g=6`) that's
  6 (`k=1`) + 21 (`k=2`) + 56 (`k=3`) = **83** reachable variant states per fully-3-socketed base item.
  Weapons + Attires total ~1,615 `ThingConfig`s (data-schemas.md §Things: 1,020 + 595); even a
  conservative illustrative split — roughly 1,000 items eligible for 1 socket only, ~450 for 2
  (RARE/EPIC), ~160 for 3 (LEGENDARY) — puts pre-generation at roughly `1,000×6 + 450×27 + 160×83 ≈
  6,000 + 12,150 + 13,280 ≈ 31,000` variant configs, **from the six example gems alone**, before the
  mod ever ships a bigger gem roster. Growth is combinatorial in gem count (`C(g+k-1,k)`), so a modest
  30-gem roster pushes `k=2`/`k=3` combo counts into the thousands per item and total pre-generated
  configs into the high hundreds of thousands to millions. Pre-generation at load is a non-starter past
  toy datasets.
- **Generate-on-demand + generate-on-receipt (recommended).** Each peer keeps a small
  `VariantId → registered?` cache. On receiving a confirmed `RW_SOCKET_V1`/`RW_UNSOCKET_V1` record
  (including the host, for its own action), a peer checks the cache; if absent, it runs the same
  one-item deterministic merge (§3.3 step 3) and registers it — a single cheap computation per socket
  event, not a batch job. This is **still fully deterministic** (R2 doesn't require content to exist
  ahead of time, only that its derivation be a pure function of shared data), so it carries none of
  option (a)-pre-generate's blow-up while giving every peer the config it needs at the moment it needs
  it.

### 9.5 SafeMode definition

On parity mismatch, SafeMode means: the "Socket Gem…"/"Remove Gem…" context entries are hidden (no new
socket/unsocket events fire this session); any item already socketed **continues to work** *only if*
that peer's own local data reproduces the same variant config on replay — which is exactly the
condition parity mismatch calls into question. Mismatch scenarios, worst-to-best:

1. **Peer missing Runeworks entirely.** When a replicated item reference arrives pointing at
   `RW_VAR_...`, that peer has no code path to decode it — likely a null-config lookup (crash or
   vanilla "unknown item" fallback), not a graceful degradation. This is the scenario `ALL_PEERS` (§9.1)
   exists to rule out before it happens, not to recover from gracefully after.
2. **Peer running different `Gems.json`/`SocketRules.json` data (dataHash mismatch).** Even for items
   already socketed before the mismatch was detected, that peer's own `RW_SocketRecords` replay-on-load
   uses *its own* (divergent) gem data — producing different stats on that peer's own screen than on
   everyone else's, for what both peers believe is "the same item." SafeMode's premise ("existing
   variants keep working") only holds if the peer's data still matches; under a dataHash mismatch it
   explicitly doesn't, which is the scenario that motivates Block over WarnAndSafeMode below.
3. **Matching data, matching version, session-only mismatch** (e.g. one peer force-reloaded data
   mid-session without the R5-mandated re-handshake). Transient; resolved once the re-handshake
   confirms parity again.

**Default policy: `Block`, not the repo-wide `WarnAndSafeMode` default — recommended, like
ClassForge's "treat mismatched packs as unsupported."** ClassForge's reasoning is that a pack-mismatch
peer sees pack-class characters "desync/resolve to nothing"; Runeworks' variant items have the same
failure shape — they live in shared, tradeable inventories, visible to and actionable by every peer,
so a SafeMode that merely disables *future* socketing does nothing about *already-existing* variant
items a mismatched peer can't correctly reconstruct (scenario 2 above). Refusing the join outright is
safer than letting a player enter a session where their own screen silently disagrees with everyone
else's about what an item does.

### 9.6 MP test plan

Host + 1 client, both running the same Runeworks version+data unless the test says otherwise (full
step-by-step script lives in §8.11; summarized here per the mandated structure):

1. Confirm the `FTK2MODS_PARITY_V1` handshake passes for `ftk2mods.runeworks` at session start.
2. **Client sockets a gem** on a non-host peer; **host verifies** the same item (inventory view or a
   DevKit dump-compare of `Configs.Things[VariantId]`) shows the identical `RW_VAR_...` id and stats —
   not just "some socketed item."
3. **Unsocket** from either peer; confirm both peers agree on the reverted base stats and the
   `UnsocketPolicy` outcome (gem destroyed/refunded/gold-charged) identically.
4. **Mismatch test**: diverge one peer's `RW_GEM_RUBY_1.json` data, attempt to join, and confirm
   ParityService names Runeworks + "data" as the mismatch and the join is refused per
   `OnParityMismatch=Block` — not silently downgraded to SafeMode.

## 10. Milestones

- **M1 — stats-only gems, variant architecture, MP foundation.** `RW_GEM_RUBY_1` and
  `RW_GEM_SAPPHIRE_1` only (pure `Stats` deltas, no passives/procs/set bonuses). Ships: gem registry
  loader, `SocketRules.json` loader, context-menu Socket/Unsocket actions, deterministic
  `RW_VAR_<baseId>_<gemhash>` variant generation + registration (R2), `GameRunData` persistence +
  replay-on-load, tooltip patch showing socketed gems, **ParityService registration (R1)**, and the
  **`RW_SOCKET_V1`/`RW_UNSOCKET_V1` host-routed sync path (§9.4)**. Co-op works for stats-only gems
  from M1 — multiplayer is not deferred to a later milestone.
- **M2 — passives, granted abilities, on-hit procs.** Adds `RW_GEM_RUBY_2` (`FIRE` proc),
  `RW_GEM_SAPPHIRE_2` (`STATUS_IMMUNITY_WATER` passive), `RW_GEM_ONYX` (`SKILL_ELITEAMBUSH` passive +
  `CRT`). Ships: `CombatHelper.ApplyAction` proc-roll patch — using the shared deterministic
  `GameRandom` per R2/§9.3, not a local `System.Random` — `GrantedAbilities` merge support (even
  though no example gem populates it — see §11.10), passive merge into generated variant configs. No
  new sync surface: passives/procs ride the same M1 `RW_SOCKET_V1` variant-generation path.
- **M3 — set bonuses, UI polish, MP hardening.** Adds `RW_GEM_STARSTONE` + the `RUBY` family set
  bonus. Ships: `CharacterHelper.InitializePartyStats` set-bonus aggregation patch, tooltip set-bonus
  progress text, and the finalized `OnParityMismatch=Block` SafeMode/Block behavior (§9.5) plus the
  full MP smoke test (§9.6/§8.11) as a repeatable regression check.

## 11. Open questions

MP-specific unknowns tracked repo-wide (not per-spec) live in `docs/MULTIPLAYER.md`'s numbered list;
items 2 (does `GameRunData` custom state replicate natively?) and 5 (payload shape/size limits of
`_handleNetworkAction`) are the two most relevant to §9.4's `RW_SOCKET_V1` design and are referenced
inline below rather than duplicated.

1. **EOR's exact variant/persistence technique** (`AffixDefinition`/`AffixedItemVariant`) is
   referenced by name in `game-code-reference.md` but not documented beyond that name — decompiling
   `EnhancedOverhaulRemix.dll` is required to confirm architecture (a) matches a proven pattern rather
   than reinventing one with unknown pitfalls.
2. **Runtime config-registration API.** `ConfigsHelper` only documents load-time parsing
   (`ParseThingsConfig`, `ProcessDirectory`, etc.); whether there's a supported way to add an entry to
   `Configs.Things` (or whatever backing field holds it) after boot, or whether this requires
   reflecting into a private dictionary, is unverified.
3. **`ThingConfig.Class` extensibility.** Is `Class` a strict C# enum (`eItemClasses`?) requiring
   recompiled game code for a new value, or a free string validated only by convention? This decides
   whether gems ship as a dedicated `GEM` class or must reuse an existing one. This spec provisionally
   proposes reusing `TRAIT`'s shape (hidden-slot-capable, stat+passive-carrying, non-equippable) for
   the gem item itself, but `TRAIT` items are `Hidden:true` by game convention and gems need to be
   visible/lootable — needs verification that `Class:"TRAIT", Hidden:false` is actually a supported
   combination, or a different placeholder chosen.
4. **Item-instance identity.** Neither reference doc describes how a specific inventory item
   *instance* (as opposed to its `ThingConfig` id) is addressed at runtime. Both candidate
   architectures need this (§3.2) — resolving which config-id reference to swap, or what to key a
   sidecar map by. Blocks implementation of either architecture, not just (b). **MP angle (§9.2/§9.4):**
   this also decides whether an item instance's config-id swap replicates via whatever vanilla
   mechanism already syncs inventory/equipment state, or whether Runeworks must carry the
   `ItemInstanceId` itself in the `RW_SOCKET_V1` payload for the host to resolve unambiguously — needs
   the same decompile pass as `docs/MULTIPLAYER.md` open question 2 (`GameRunData` replication).
5. **Whether `SkillConfigs.json` Properties are read generically.** `data-schemas.md` states new
   `SKILL_*` ids "do nothing without a C# handler," which this spec takes at face value (hence
   patching `CombatHelper.ApplyAction` directly for on-hit procs rather than authoring a new skill via
   `PROC_CHANCE`/`PROC_EQUIPMENT`/`STATUS_EFFECT` data alone). Worth a decompile check of
   `CombatHelper._onCombatSkillProc`'s dispatch — if it turns out to read those properties generically
   for any skill id, procs could become pure data with no combat patch at all.
6. **Exact `StatusEffects.json` id(s) for a basic on-hit fire burn.** `FIRE` is confirmed as a `Type`
   in the ~40-Type enum, but the concrete `StatusID` key(s) that use it (a single `"FIRE"` entry vs.
   tiered ids) aren't enumerated in our docs. `RW_GEM_RUBY_2`'s `OnHitProc.StatusId: "FIRE"` is a
   best guess pending a direct look at `StatusEffects.json`.
7. **Stacking semantics.** Whether `ThingConfig.Stacks` keys purely off config id (favorable to
   architecture (a)) or has some other per-instance uniqueness check is unverified; this spec's
   stacking-safety claim for architecture (a) assumes the former.
8. **`Configs.Things` read freshness.** Whether item lookups re-read `Configs.Things` live on every
   access (supporting post-boot registration working everywhere immediately) or whether some UI/data
   paths cache a snapshot at scene load (which would require re-registering variants on every scene
   transition) is unverified.
9. **MP mod-sync handshake — resolved at the repo level, one API question remains.** Runeworks does
   not roll its own config+data hash handshake; it registers with the shared **ParityService**
   (`docs/MULTIPLAYER.md` R1, FTK2.DevKit) at boot (§6, §9.1), which is an M1 requirement, not deferred
   past M3. The remaining open question is purely mechanical: whether ParityService's registration API
   exists yet in FTK2.DevKit at the time Runeworks M1 starts, or whether Runeworks needs a temporary
   local stub (its own minimal version/dataHash check) until DevKit ships it, later swapped for the
   real registration call.
10. **`GrantedAbilities` example.** The schema supports adding `Abilities.json` ids to a socketed
    item's `AbilityBag`, but `Abilities.json`'s 972 entries aren't enumerated in our reference docs, so
    no example gem populates this field (inventing a plausible-looking ability id would violate the
    no-invented-ids rule). Needs a follow-up lookup against the live `Abilities.json` before an M2/M3
    example gem can exercise it.
11. **Loot-table integration point for `GemDropRateMultiplier`.** `LootDropHelper` methods
    (`GetAdventureLoadOut`, `GetLootDropsFromEnemies`, `GetMarketCategoryQuantity`) are listed as EOR
    patch precedents in `game-code-reference.md` but which one actually gates "does this gem drop and
    how often" for a new lootable item family is unverified; the knob is specified but its wiring is
    deferred to implementation-time investigation of those methods.
