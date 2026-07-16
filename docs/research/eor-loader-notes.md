# EOR loader mechanics (verified by decompile, 2026-07-16)

Source: `EnhancedOverhaulRemix.dll` (699 KB, `BepInEx\plugins\`), decompiled with ilspycmd 10.1.1
to a single `Plugin.cs` (namespace `FTK2.BanditKingPlayable`). Line references below are into that
decompile output (`tools/out/decompile/EOR/`, gitignored — regenerate with the command in
`docs/superpowers/plans/2026-07-16-content-generation-system.md` Task 2).

## Findings

### 1. `CustomItems` Things loader — GENERIC drop-in. [verified: decompile]

`EnsureCustomItemFrameworkRuntimeConfigs` (~L26674) runs from several runtime preflight hooks
(e.g. the `InitializePartyStats` preflight ~L9607) behind a `_customItemFrameworkLoaded`
once-per-process latch. It:

- Enumerates `plugins\EnhancedOverhaulRevamped\CustomItems\Things.json` **plus every
  `CustomItems\Things\*.json`**, ordinal-sorted (`GetCustomThingFiles`, ~L27018). Any file name
  works — the loader is fully generic. **Forge SPEC Open Question #6: resolved (Things half).**
  There is no CraftConfigs equivalent anywhere in EOR (0 grep hits for CraftConfig/CraftingHelper),
  so the recipe half of that question stays on Path A.
- Deserializes each file as `Dictionary<string, ThingConfig>` and merges into
  `Env.Configs.Things[id]` (`MergeCustomThingFile`, ~L27036). **Existing ids are overwritten**
  ("ThingsUpdated" counter) — id uniqueness is on us.
- **Validation gate** (`IsCustomThingConfigValid`, ~L27104): every key in
  `Interactable.Abilities` and every entry of `AbilityBag`/`AbilityFillBag` must exist in
  `Env.Configs.Abilities`, else the item is skipped with a warning; an `Equippable` with an empty
  `Slots` array is rejected. Our validator must enforce the same rules.
- Auto-adds an `EOR_CUSTOM` tag to every merged item (~L27073) — this tag also gates EOR's
  icon-as-card-render path (see §2), so it is a feature, not noise.
- Ensures runtime localization defaults: missing loc keys get a humanized name derived from the id
  (`HumanizeCustomItemId`) and a generic description (~L27074).
- EOR also self-heals the folder by writing template/example files if missing
  (`ExportCustomItemFrameworkTemplates`) and ships a README documenting the intended contract:
  normal tags (`DROPPABLE`, `TOWN_MARKET`, …) + normal `MinTier`/`MaxTier`/`Value`/`Rarity` are
  what make custom items reachable by existing loot and market queries.

### 2. `ItemIcons` loose PNGs — id-keyed, but only rendered for icon-only-card items. [verified: decompile]

`TryGetCustomItemIconTexture` (~L28762) probes
`plugins\EnhancedOverhaulRevamped\ItemIcons\<key><ext>` with keys tried in order: **exact item id**,
lowercased id, humanized display name with underscores (`Arcane_Focus`), lowercased humanized name;
extensions `.png/.jpg/.jpeg`; loaded via `ImageConversion.LoadImage`, cached, misses negative-cached.
**Emit `<ITEM_ID>.png` — the exact-id key is first-class.**

Where those textures are actually *used* is narrow (this matters):

- `AssetLoader.GetRender` prefix (~L23070): item **card render** uses the custom PNG only when
  `ShouldUseItemIconAsCardRender` (~L16665) passes — item must carry `EOR_CUSTOM`/`EORR_CUSTOM`
  tag AND be TRINKET-class/slot (or a non-weapon/armor item with a fallback). 
- `AssetLoader.GetImage` prefix, atlas 0 = item icon atlas (~L22898): custom items' grid icons are
  redirected to the **VisualFallback donor's icon**, not the PNG (PNG is consulted only for one
  special mercenary item).

**Consequence: under EOR's pipeline, remixed PNG icons display in-game for trinket-type items
only; weapons/armor show their VisualFallback donor's icon + model.** A future ~50-line Armory
micro-plugin (M2 candidate) can extend the same `AssetLoader.GetImage` atlas-0 prefix to serve
`ARM_*` PNGs for all classes — presentation-only, so `[LOCAL]` under `docs/MULTIPLAYER.md` R4.
Until then the remixed icons are: the morning review artifact, live for trinkets, and
plugin-ready payload for everything else.

### 3. `VisualFallbacks.json` — flat map, validated, load-bearing for loot eligibility. [verified: decompile]

`LoadCustomItemVisualFallbacks` (~L27170) reads a flat `{customId: donorId}` map; entries are
dropped (warning) unless **both** ids exist in `Env.Configs.Things`. Applied via the
`AssetLoader.GetImage`/`GetRender` prefixes (icon + card render redirect to donor) and an
equipment-visual "shape" application (`ApplyCustomItemVisualFallbackShapes`).

**Sharp edge:** `QuarantineCustomItemsWithoutSafeVisuals` (~L27277) strips
`DROPPABLE/LOOTABLE/TOWN_MARKET/DUNGEON_MARKET/BLACK_MARKET/RELIC_BROKER` tags from any custom
item that ends up without a safe visual (`RemoveNaturalRewardAndMarketTags`, ~L27342) — i.e. **a
missing/invalid VisualFallback silently removes the item from loot and markets.** There is an
inference pass (`InferMissingCustomItemVisualFallbacks`) but we must not rely on it: every ARM_
item ships an explicit, validated VisualFallback. Our `validate_pack.py` hard-fails otherwise.

### 4. Localization — EOR-side `Lang.__t` injection with humanized defaults. [verified: decompile]

EOR's `LocalizationManager` (~L1721) loads `plugins\EnhancedOverhaulRevamped\Localization\
<lang>.json` (`Language=auto` follows the game language) and injects via a patched `Lang.__t`.
Custom items without entries get humanized-id defaults at merge time (§1). Manual install merges
our `<ID>` / `<ID>_DESCRIPTION` keys into EOR's `Localization\en.json`; if the user skips this
step, items still show readable humanized names — degraded, not broken.

## Consequences for FTK2.Armory

1. **Recommended manual install path (all confirmed generic):**
   - Things packs → drop files into `CustomItems\Things\` (e.g. `ARM_Catalog.json`) — no
     game-file edits at all.
   - VisualFallbacks → merge our entries into `CustomItems\VisualFallbacks.json` (single file; EOR
     pre-seeds it — merge, don't replace).
   - Localization → merge our keys into `Localization\en.json` (optional but recommended).
   - Icons → copy `ARM_*.png` into `ItemIcons\`.
2. **Icon filename convention for iconforge: `<ITEM_ID>.png`** (exact id).
3. **Validator obligations imported from EOR's gate**: ability references must exist; equippable
   items need ≥1 slot; every item needs a valid VisualFallback (quarantine risk); unique ids
   (merge overwrites silently).
4. Real-world field values differ from `docs/research/data-schemas.md`'s Class list: EOR's own
   examples use `Class: "CHESTARMOR"`/`"HELMET"` and `Slots: ["MAIN_HAND"|"ARMOR"|"HELMET"]`
   (`eEquipmentSlots` enum). The vocab extractor treats **observed live data** as ground truth for
   enums; data-schemas.md's Class list needs a correction pass (tracked in vocab-summary).
5. EOR itself writes template files into its own plugin folder at runtime — that's EOR's behavior,
   not ours; our tooling still never writes to the game directory.
