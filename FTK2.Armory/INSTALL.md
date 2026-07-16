# Installing FTK2.Armory packs (manual steps — nothing here is automated)

Per owner decision, **no script in this repo writes to the game directory**. Installation is a
manual copy/merge you perform yourself. Back up first; uninstall = restore backups.

Game root below = `E:\Games\Steam\steamapps\common\For The King II`.

## Recommended path — EOR CustomItems drop-in (verified generic; no game files touched)

Requires the Enhanced Overhaul Revamped install you already have (it created these folders; if a
folder is missing, launch the game once with EOR enabled — it self-creates templates).

1. **Back up** (copy somewhere safe):
   - `<game>\BepInEx\plugins\EnhancedOverhaulRevamped\CustomItems\VisualFallbacks.json`
   - `<game>\BepInEx\plugins\EnhancedOverhaulRevamped\Localization\en.json`
2. **Things packs (plain copy — the loader scans every `*.json` in the folder):**
   copy every file from `FTK2.Armory\data\Things\` into
   `<game>\BepInEx\plugins\EnhancedOverhaulRevamped\CustomItems\Things\`
3. **Visual fallbacks (merge, don't replace):** open
   `CustomItems\VisualFallbacks.json` and add every key from
   `FTK2.Armory\data\VisualFallbacks.json` into the existing JSON object (both are flat
   `"id": "donorId"` maps; keep EOR's existing entries).
4. **Localization (merge, optional but recommended):** add every key from
   `FTK2.Armory\data\Localization\en.json` into
   `<game>\...\EnhancedOverhaulRevamped\Localization\en.json`. Skipping this step is safe —
   items then show auto-humanized names ("Arm Bramble Cuirass" style) instead of the real ones.
5. **Icons (plain copy):** copy `FTK2.Armory\data\icons\*.png` into
   `<game>\BepInEx\plugins\EnhancedOverhaulRevamped\ItemIcons\`
   (note: under current EOR these render for trinket-class items; other classes show their
   fallback donor's art — see SPEC §6).
6. Launch. Sanity check: the BepInEx console/log should show EOR's
   `Custom item framework ready. ThingsAdded=<n>…` line counting our items, with no
   `Custom item skipped` warnings mentioning `ARM_` ids.

**Uninstall**: delete the copied `ARM_*.json` files from `CustomItems\Things\`, delete the
`ARM_*.png` icons, restore your two backed-up merged files.

## Fallback path — no EOR (edits game-owned files; bigger blast radius)

Only if you run without EOR. Back up `<game>\For The King II_Data\StreamingAssets\Assets\
Configs\JSON~\Things\` first. Copy `FTK2.Armory\data\Things\*.json` into that folder (the native
config reader scans the directory). Limitations: no visual-fallback layer exists without EOR —
items will render without models/icons unless you also swap `VisualFallback` donors into the
items' own ids; localization must merge into the game's own language files; game updates will
overwrite everything. This path is documented for completeness; the EOR path is strictly better.

## Multiplayer note

Packs are `ALL_PEERS` by file identity: every peer needs byte-identical pack files installed the
same way (SPEC §9). Diff the files before a session; EOR's own sync handshake provides the
enforcement when installed via the EOR path.
