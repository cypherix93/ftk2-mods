using System;
using System.Collections.Generic;
using IOG.dObjects;

namespace Wardrobe.Plugin
{
    /// <summary>
    /// The two unlock patches (SPEC.md). Both fail-open: any exception leaves vanilla behavior
    /// untouched. Neither ever writes a stat — unlocks are read-time list/check augmentation only.
    /// </summary>
    public static class UnlockPatches
    {
        private static readonly HashSet<string> LoggedCategories = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Species-candidate cache; rebuilt when null (configs reload rarely enough that a
        /// per-session cache is fine — the list is a pure function of static config + record data).</summary>
        private static List<string> _speciesCache;

        // ------------------------------------------------------------- list augmentation

        public static void GetAllPurchasedSkinCosmetics_Postfix(eSkinCosmetics pSkinCosmeticType, ref List<string> __result)
        {
            try
            {
                if (!WardrobePlugin.Enabled.Value || __result == null) return;

                var have = new HashSet<string>(__result, StringComparer.OrdinalIgnoreCase);
                int added = 0;

                if (CategoryKnob(pSkinCosmeticType))
                {
                    // Same key shape the vanilla implementation filters on: SKIN_{type}_*, Category SKIN.
                    // DLC entitlement is respected unconditionally (SPEC constraint 3).
                    var ownedExpansions = StatsHelper.GetEnabledExpansions();
                    string prefix = "SKIN_" + pSkinCosmeticType + "_";
                    foreach (var kv in Env.Configs.LoreStore)
                    {
                        if (kv.Value == null || kv.Value.Category != eLoreStoreCategories.SKIN) continue;
                        if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        if (!ownedExpansions.Contains(kv.Value.Expansion)) continue;
                        if (have.Add(kv.Key)) { __result.Add(kv.Key); added++; }
                    }
                }

                if (pSkinCosmeticType == eSkinCosmetics.CHARACTER && WardrobePlugin.ClassModelsAsSkins.Value)
                {
                    foreach (var key in SpeciesCandidates())
                        if (have.Add(key)) { __result.Add(key); added++; }
                }

                // Deterministic order: randomize indexes into this list, and a stable list keeps the
                // picker order identical between openings (and between peers with equal configs).
                __result.Sort(StringComparer.Ordinal);

                if (added > 0 && LoggedCategories.Add(pSkinCosmeticType.ToString()))
                    WardrobePlugin.Log.LogInfo($"[Wardrobe] {pSkinCosmeticType}: +{added} unlocked entries (list now {__result.Count}). Logged once per category.");
            }
            catch (Exception ex)
            {
                WardrobePlugin.Log.LogWarning("[Wardrobe] cosmetic list augmentation failed (vanilla list kept): " + ex.Message);
            }
        }

        // ------------------------------------------------------------- preset acceptance

        /// <summary>Narrow by design: only lore-store SKIN_* entries with an owned expansion and an
        /// enabled category knob short-circuit to "purchased". Everything else (classes, skill
        /// unlock stats, loadout rewards — all simulation-relevant) falls through to vanilla.</summary>
        public static bool IsItemPurchased_Prefix(string pItemName, ref bool __result)
        {
            try
            {
                if (!WardrobePlugin.Enabled.Value || !WardrobePlugin.ApplyToPresets.Value) return true;
                if (string.IsNullOrEmpty(pItemName) || !pItemName.StartsWith("SKIN_", StringComparison.Ordinal)) return true;
                if (!Env.Configs.LoreStore.TryGetValue(pItemName, out var cfg) || cfg == null) return true;
                if (cfg.Category != eLoreStoreCategories.SKIN) return true;
                if (!CategoryKnobForKey(pItemName)) return true;
                if (!StatsHelper.GetEnabledExpansions().Contains(cfg.Expansion)) return true;
                __result = true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        // ------------------------------------------------------------- knobs

        private static bool CategoryKnob(eSkinCosmetics type)
        {
            switch (type)
            {
                case eSkinCosmetics.HELMET: return WardrobePlugin.HelmetSkins.Value;
                case eSkinCosmetics.ARMOR: return WardrobePlugin.ArmorSkins.Value;
                case eSkinCosmetics.BACKPACK: return WardrobePlugin.BackpackSkins.Value;
                case eSkinCosmetics.CHARACTER: return WardrobePlugin.CharacterSkins.Value;
                default: return false;
            }
        }

        /// <summary>Key shape is SKIN_&lt;eSkinCosmetics&gt;_&lt;rest&gt; (vanilla builds it with the enum's
        /// ToString), so the category is the second underscore-delimited segment.</summary>
        private static bool CategoryKnobForKey(string key)
        {
            int start = "SKIN_".Length;
            int end = key.IndexOf('_', start);
            if (end <= start) return false;
            return Enum.TryParse<eSkinCosmetics>(key.Substring(start, end - start), out var type) && CategoryKnob(type);
        }

        // ------------------------------------------------------------- species trick

        /// <summary>EOR's recipe verbatim (Plugin.cs L22390-22443): PLAYER-tagged Characters minus
        /// enemy/boss/npc/mercenary/pet/companion tags and utility ids, then only the dCharacter
        /// record keys that actually exist (the crash guard). Our own pack classes are excluded —
        /// their visuals are donor-remapped, so offering them would just duplicate donor models.</summary>
        private static List<string> SpeciesCandidates()
        {
            if (_speciesCache != null) return _speciesCache;
            var result = new List<string>();
            try
            {
                foreach (var kv in Env.Configs.Characters)
                {
                    var id = kv.Key;
                    var cfg = kv.Value;
                    if (cfg?.Tags == null || !cfg.Tags.Contains("PLAYER")) continue;
                    if (id.StartsWith("CF_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (id.StartsWith("EOR_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (id.StartsWith("SKIN_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (id.EndsWith("_F", StringComparison.Ordinal) || id.EndsWith("_M", StringComparison.Ordinal)) continue;
                    if (id.Contains("MASTERY") || id.Contains("TUTORIAL") || id.Contains("TEST")) continue;
                    bool excluded = false;
                    foreach (var tag in cfg.Tags)
                    {
                        if (tag == "ENEMY" || tag == "BOSS" || tag == "NPC" || tag == "MERCENARY" || tag == "PET" || tag == "COMPANION")
                        {
                            excluded = true;
                            break;
                        }
                    }
                    if (excluded) continue;

                    foreach (var probe in new[] { id, id + "_F", id + "_M", "PC_" + id, "PC_" + id + "_F", "PC_" + id + "_M" })
                        if (dObjectHelper.Index.dCharacter.TryGetRecordByName(probe, out var rec) && rec != null)
                            result.Add(probe);
                }
            }
            catch (Exception ex)
            {
                WardrobePlugin.Log.LogWarning("[Wardrobe] species candidate scan failed: " + ex.Message);
            }
            _speciesCache = result;
            return result;
        }
    }
}
