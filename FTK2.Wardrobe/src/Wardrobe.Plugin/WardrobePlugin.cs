using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Wardrobe.Plugin
{
    /// <summary>
    /// Character-creation cosmetic unlocks (see SPEC.md). Pure client-cosmetic — peers accept
    /// cosmetic ids without purchase validation, every id served exists in every peer's game
    /// data, and no profile stat is ever written. Not parity-registered by design.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class WardrobePlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.wardrobe";
        public const string Name = "FTK2.Wardrobe";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> HelmetSkins;
        internal static ConfigEntry<bool> ArmorSkins;
        internal static ConfigEntry<bool> BackpackSkins;
        internal static ConfigEntry<bool> CharacterSkins;
        internal static ConfigEntry<bool> ClassModelsAsSkins;
        internal static ConfigEntry<bool> ApplyToPresets;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch. Off = vanilla lore-store gating everywhere.");
            HelmetSkins = Config.Bind("Unlock", "HelmetSkins", true,
                "Offer all non-DLC helmet skins at character creation without purchase.");
            ArmorSkins = Config.Bind("Unlock", "ArmorSkins", true,
                "Offer all non-DLC armor skins at character creation without purchase.");
            BackpackSkins = Config.Bind("Unlock", "BackpackSkins", true,
                "Offer all non-DLC backpack skins at character creation without purchase.");
            CharacterSkins = Config.Bind("Unlock", "CharacterSkins", true,
                "Offer all non-DLC character skins at character creation without purchase.");
            ClassModelsAsSkins = Config.Bind("Unlock", "ClassModelsAsSkins", true,
                "EOR-style species trick: offer every vanilla playable class's 3D model as a selectable character skin.");
            ApplyToPresets = Config.Bind("Unlock", "ApplyToPresets", true,
                "Treat unlocked skins as purchased when loading saved presets (side effect: the Lore Store " +
                "UI shows them as owned while this is on). Off = presets degrade unpurchased skins to default.");

            var harmony = new Harmony(Guid);
            Patch(harmony, typeof(LoreStoreHelper), "GetAllPurchasedSkinCosmetics",
                postfix: new HarmonyMethod(typeof(UnlockPatches), nameof(UnlockPatches.GetAllPurchasedSkinCosmetics_Postfix)));
            Patch(harmony, typeof(LoreStoreHelper), "IsItemPurchased",
                prefix: new HarmonyMethod(typeof(UnlockPatches), nameof(UnlockPatches.IsItemPurchased_Prefix)),
                argumentTypes: new[] { typeof(string), typeof(StatsHelper.eStatType) });

            Log.LogInfo($"{Name} {Version} loaded. Enabled={Enabled.Value}, presets={ApplyToPresets.Value}, species={ClassModelsAsSkins.Value}");
        }

        /// <summary>docs/CONVENTIONS.md logging convention: one found/NOT-found line per target;
        /// a missing target disables only the feature riding it (fail-safe).</summary>
        private static void Patch(Harmony harmony, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, Type[] argumentTypes = null)
        {
            var target = argumentTypes == null
                ? AccessTools.Method(type, method)
                : AccessTools.Method(type, method, argumentTypes);
            if (target == null)
            {
                Log.LogError($"Target NOT found: {type.Name}.{method} — this feature is disabled (fail-safe).");
                return;
            }
            harmony.Patch(target, prefix: prefix, postfix: postfix);
            Log.LogInfo($"Target found: {type.Name}.{method}");
        }
    }
}
