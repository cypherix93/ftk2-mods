# FTK2.Wardrobe — character-creation cosmetic unlocks

Makes every non-DLC lore-store SKIN cosmetic (helmet / armor / backpack / character) selectable
at character creation without purchasing it, and (EOR-style "species trick") offers every vanilla
playable class's 3D model as a selectable CHARACTER skin. Ported from EOR 0.7.0.60's
`LoreStoreHelper_GetAllPurchasedSkinCosmetics_Postfix` (Plugin.cs L22316–22363) after a full
mechanism/MP investigation (2026-08-05 agent report).

## Design constraints (from the investigation — all verified against the 7/31 game build)

1. **Single chokepoint.** Every cosmetic picker, randomizer, and preset validator builds its list
   from `LoreStoreHelper.GetAllPurchasedSkinCosmetics(eSkinCosmetics)`. One postfix covers the UI.
   Preset round-trips additionally consult `IsItemPurchased` (PartyManagementDirector `_loadPreset`);
   a narrow prefix covers that (knob).
2. **MP posture: pure client-cosmetic, safe asymmetric.** Peers apply received `EDIT_COSMETIC`
   ids with zero purchase validation (PMD `_handle*` paths) and every id this mod serves exists in
   every peer's game data (vanilla LoreStore keys / vanilla `dCharacter` records). NOT parity
   registered — same class as ClassForge's `EnableClassSelectInjection`. Known vanilla-inherited
   caveat: `RANDOMIZE_CHARACTER` replays per-peer against local lists, so randomize can pick
   different cosmetics per peer transiently (self-heals on the next explicit edit).
3. **Never bypass DLC entitlement.** Items whose `LoreStoreConfig.Expansion` is not in
   `StatsHelper.GetEnabledExpansions()` are never added (18 of the 87 skins are DLC-gated).
4. **Never write profile stats.** No `PurchaseItem`/`SetStat`/`UnlockAllLoreStore` — progression
   and `TOTAL_LORE` untouched; removing the mod degrades gracefully (equipped ids keep rendering,
   pickers fall back, presets degrade to `DEFAULT_NONE`).
5. **Species candidates are existence-probed.** Only `dCharacter` record keys that actually exist
   are offered (`X`, `X_F`, `X_M`, `PC_X`, `PC_X_F`, `PC_X_M`) — EOR's crash guard, kept verbatim.
   Our own CF_ pack classes are excluded (their visuals are donor-remapped; offering them would
   duplicate the donor models).

## Config

| Knob | Default | Effect |
|---|---|---|
| `[General] Enabled` | true | master switch |
| `[Unlock] HelmetSkins / ArmorSkins / BackpackSkins / CharacterSkins` | true | per-category unlock |
| `[Unlock] ClassModelsAsSkins` | true | the EOR species trick |
| `[Unlock] ApplyToPresets` | true | `IsItemPurchased` prefix so saved presets keep unlocked skins (side effect: lore-store UI shows those skins as owned while active) |

Deliberately NOT shipped: unlocking playable CHARACTER-category classes (simulation-relevant via
per-peer Randomize divergence; would need parity registration — future knob if ever wanted).
