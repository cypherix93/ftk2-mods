# FTK2.Armory — item content packs (hand-crafted catalog + curated forge output)

Id prefix: `ARM_` · Priority: **P1** · **Zero-code M1** (no plugin GUID yet — M1 ships no plugin).

## 1. Purpose & scope

FTK2.Armory is the repo's item *content* mod: a hand-crafted catalog of themed, synergistic items
(9 sets + standalone artifacts) plus a curated selection from the offline item forge
(`tools/itemforge/`), shipped as static, validator-gated JSON packs. It is the consumer proof of
the repo's content-generation toolchain (`tools/` — vocab extractor, iconforge, itemforge,
validator, compiler) and of the mechanics ground truth in `docs/research/game-mechanics.md`.

**In scope**: item packs (Things + VisualFallbacks + Localization + icons), the manifest sources
they compile from (`packs/*.pack.json`), design documentation per item (DesignNote claim
citations), review artifacts (contact sheets, REVIEW.html).

**Deliberately out of scope**:
- Any C# plugin in M1 (see §10 for the M2/M3 plugin candidates).
- Set *bonuses* (equipping N pieces grants an extra effect) — that needs runtime state and is the
  flagship M2 feature; M1 sets synergize through complementary mechanics only.
- New abilities, statuses, skills — items compose only what the game already executes
  (owner decision 2026-07-16; also what makes M1 zero-code and MP-trivial).
- Loot-table weight edits — items enter loot/markets purely via tags + tiers
  (game-mechanics.md O-LOOT-04/05/06, O-MKT-03).
- Custom 3D models; visuals ride `VisualFallbacks` donors + PNG icons (eor-loader-notes.md §2).

## 2. Player-facing behavior

- ~120 new items (≈60 hand-crafted + ≈60 forge-curated) drop from enemies, appear in town/
  dungeon/night markets, and show up in quest reward pools, gated by the same tier/rarity/tag
  rules as base-game items.
- Hand-crafted sets are discoverable identities (Bramblelord thorns tank, Tidebinder focus
  caster, Gravedigger luck gambler, Red Season bleed hunter, Waywatcher explorer, Ashen Covenant
  cursed bargains, Hollow Court summoner, Stormcaller shock, Vanguard's Oath skirmish support)
  plus standalone build-around artifacts (UNIQUE-tagged: once per run).
- Items render with base-game models/icons via visual fallbacks; trinket-class items additionally
  show custom remixed icons under EOR (see §6).
- Everything reads normally in tooltips/markets — they are ordinary `ThingConfig`s.

## 3. Architecture

Pure data pipeline, no runtime engine:

| Layer | What it owns |
|---|---|
| `packs/*.pack.json` (manifests, committed) | Source of truth per item: Thing + VisualFallback + Loc + Icon recipe + DesignNote + Provenance (`tools/manifest_schema.md`). |
| `tools/validate_pack.py` | The gate: live-vocab id closure, EOR loader rules, power budgets vs measured curves. Nothing compiles with ERRORs. |
| `tools/compile_pack.py` + `tools/iconforge/` | Manifests → `data/` ship shape (Things/VisualFallbacks/Localization/icons). |
| `data/` (compiled, committed) | What a player actually installs (see INSTALL.md). |

Regeneration is deterministic: same manifests → identical `data/`; forge packs additionally
carry (Profile, Seed) provenance and reproduce byte-identically.

## 4. Data file formats

Manifest contract: `tools/manifest_schema.md`. Ship shape: game-native `ThingConfig` dicts
(`docs/research/data-schemas.md` + corrections in `docs/research/vocab-summary.md`),
EOR-convention `VisualFallbacks.json`, `Localization/en.json`, `icons/<ITEM_ID>.png`.

## 5. Knobs

None at runtime (no plugin). Authoring-time knobs live in forge profiles
(`tools/itemforge/profiles/*.json`) and `tools/validator-config.json`.

## 6. Patch targets & integration points

None. Load path is EOR's verified-generic CustomItems framework
(`docs/research/eor-loader-notes.md`): `CustomItems\Things\*.json` (drop-in),
`CustomItems\VisualFallbacks.json` (merge), `Localization\en.json` (merge),
`ItemIcons\*.png` (copy). Fallback path without EOR: merge into the game's own
`StreamingAssets\...\Things\` tree (documented in INSTALL.md; requires manual backups).
Known EOR behaviors we design around: ability references must exist or the item is skipped;
items without a safe visual fallback are quarantined out of loot/markets; custom PNG icons
render for trinket-class items only (equipment shows donor art) until an M3 icon micro-plugin.

## 7. Example starting dataset

`packs/`: `catalog_sets_1_3.pack.json`, `catalog_sets_4_6.pack.json`,
`catalog_sets_7_9.pack.json`, `catalog_standalones.pack.json`, `forge_curated.pack.json`
(+ per-pack contact sheets). `data/`: the compiled output of all five. `CATALOG.md`: the set
design doc. Together they demonstrate: mechanics-grounded synergy design, forge output curation,
icon remixing, and the full manifest→validate→compile flow.

## 8. Testing plan

Automated (already run, re-runnable): `python -m pytest tools/tests -v` (39 tests);
`validate_pack --strict` per pack; forge determinism test; icon render completeness.
Human in-game (<15 min, needs EOR debug toolkit): install per INSTALL.md; give-item an
`ARM_BRAMBLE_*` piece + confirm tooltip/stats/fallback model; give a weapon and confirm it
attacks (ability template wiring); check a town market at tier 0–1 for any COMMON ARM_ item;
kill a few enemies for drop sanity; confirm a trinket shows its custom PNG icon under EOR;
confirm a cursed Ashen piece shows its negative stat. Full checklist in REVIEW.html.

## 9. Save & multiplayer

Parity class `ALL_PEERS`, enforced by file identity — identical pack files on all peers, exactly
like base-game data edits (EOR's own config-hash handshake covers the CustomItems folder when
installed via EOR). Zero runtime randomness (forge randomness is authoring-time; every shipped
file is static). Zero custom sync surface, zero per-instance state: an owned ARM_ item is an
ordinary item id in inventory, replicated by vanilla systems. SafeMode semantics: none needed —
a peer missing the packs simply fails the EOR hash handshake before play. This is the
"config-shaped content" ideal case in `docs/MULTIPLAYER.md`.

## 10. Milestones

- **M1 (this drop, zero-code)**: packs + compiled data + docs + review artifacts.
- **M2 — set bonus engine (plugin)**: reads `ARM_SET_*` tags, grants threshold passives;
  ParityService registration; the reason set tags are already stamped on M1 items.
- **M3 — icon micro-plugin**: extend EOR's `AssetLoader.GetImage` atlas-0 pattern to serve
  `ARM_*` PNGs for all item classes (presentation-only, `[LOCAL]` per MULTIPLAYER.md R4).
- **M4 — Armory-specific drop tables**: optional LootDrops/quest-reward integration beyond tags.

## 11. Open questions

1. Whether EOR's config-hash handshake actually covers `CustomItems\Things\*.json` content
   byte-for-byte (its README says data edits ⇒ same files on all peers; verify the hash scope in
   a live MP session before recommending MP use).
2. Icon-only-card behavior for `ARMOR_TRINKET` — verified in decompile; confirm visually in-game.
3. Whether `Rarity: ARTIFACT` items respect the 5% loot bucket without further tags (O-LOOT-06
   says yes; confirm empirically).
4. Balance pass after real play: validator budgets keep items inside sane envelopes, but "fun"
   tuning (especially Ashen Covenant rebates) needs human playtesting.
