using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ClassForge.Core;
using ClassForge.Core.IO;
using HarmonyLib;

namespace ClassForge.Plugin
{
    /// <summary>
    /// ClassForge M1: the BepInEx entry point. Owns knobs, Harmony patch installation, per-pack knob binding,
    /// and the reflection-based FTK2.DevKit ParityService registration (SPEC.md §3, §5, §6, §9.6).
    /// The actual pack-merge logic lives in ClassForge.Core (host-agnostic, testable); this class is a thin
    /// adapter that wires Core into the game's ConfigsHelper/Lang hooks and BepInEx config.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class ClassForgePlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.classforge";
        public const string Name = "FTK2.ClassForge";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ClassForgePlugin Instance;

        // ---- knobs (SPEC.md §5) ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<string> AdditionalRoots;

        /// <summary>Bind-only in M1 — the CharacterCustomizationViewHelper.RenderClassList patch itself is a
        /// later unit (see ClassSelectPatches). Bound now so the config key/section is stable across versions.</summary>
        internal static ConfigEntry<bool> EnableClassSelectInjection;

        /// <summary>Bind-only in M1 — the AssetLoader.GetImage/GetRender patch itself is a later unit (see AssetPatches).</summary>
        internal static ConfigEntry<bool> EnableIconFallback;

        private static readonly Dictionary<string, ConfigEntry<bool>> PackEnabledKnobs = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);

        /// <summary>The most recently computed merge plan — read by LocalizationPatches (and, later, the icon/UI patches).</summary>
        internal static MergePlan CurrentMergePlan;
        internal static string CurrentDataHash = "(none)";

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch; if false, no pack is scanned and vanilla behavior is untouched.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false,
                "Pack discovery/merge decisions at Debug level.");
            AdditionalRoots = Config.Bind("Packs", "AdditionalRoots", "",
                "Comma-separated absolute paths to additional directories to scan for ClassPacks/<PackName>/ folders.");
            EnableClassSelectInjection = Config.Bind("UI", "EnableClassSelectInjection", true,
                "Class-select UI integration toggle. M1 NOTE: this only binds the knob — the RenderClassList " +
                "injection patch itself ships in a later unit; pack classes exist in Configs.Characters and are " +
                "already usable via console/dev tools regardless of this setting.");
            EnableIconFallback = Config.Bind("UI", "EnableIconFallback", true,
                "AssetLoader.GetImage/GetRender icon+portrait fallback toggle. M1 NOTE: this only binds the " +
                "knob — the fallback patch itself ships in a later unit.");

            ApplyPatches();

            Log.LogInfo($"{Name} {Version} awakened. Enabled={Enabled.Value}. " +
                        "Pack discovery/merge runs from the ConfigsHelper.LoadConfigs/ReloadConfigs postfixes.");
        }

        private void ApplyPatches()
        {
            var harmony = new HarmonyLib.Harmony(Guid);

            Patch(harmony, typeof(ConfigsHelper), "LoadConfigs",
                postfix: new HarmonyMethod(typeof(ConfigMergePatches), nameof(ConfigMergePatches.LoadConfigs_Postfix)));
            Patch(harmony, typeof(ConfigsHelper), "ReloadConfigs",
                postfix: new HarmonyMethod(typeof(ConfigMergePatches), nameof(ConfigMergePatches.ReloadConfigs_Postfix)));
            Patch(harmony, typeof(Lang), "SetLanguage",
                postfix: new HarmonyMethod(typeof(LocalizationPatches), nameof(LocalizationPatches.SetLanguage_Postfix)));

            // --- Later-unit wiring points (M2 class-select/icon polish) — deliberately NOT Harmony-patched yet.
            // Knobs above are already bound so their config keys are stable when the real patches land.
            AssetPatches.LogWiringPointOnly(Log);
            ClassSelectPatches.LogWiringPointOnly(Log);
        }

        private static void Patch(HarmonyLib.Harmony harmony, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, Type[] argumentTypes = null)
        {
            var target = argumentTypes == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, argumentTypes);
            if (target == null)
            {
                Log.LogError($"Target NOT found: {type.Name}.{method} — this feature is disabled (fail-safe).");
                return;
            }
            harmony.Patch(target, prefix: prefix, postfix: postfix);
            Log.LogInfo($"Target found: {type.Name}.{method}");
        }

        /// <summary>
        /// Cheap discovery-only pre-pass so a per-pack <c>[Packs] &lt;PackId&gt;.Enabled</c> BepInEx entry
        /// exists before the real merge runs (SPEC.md §5). Idempotent — safe to call from every
        /// LoadConfigs/ReloadConfigs postfix; newly-discovered pack ids get a knob, existing ones are left
        /// exactly as the player set them.
        /// </summary>
        internal static void EnsurePackKnobsBound()
        {
            try
            {
                var fs = new FileSystemFileSource();
                var findings = new List<Finding>();
                var discovered = PackDiscovery.Discover(fs, GetRoots(), findings);
                foreach (var pack in discovered)
                {
                    if (PackEnabledKnobs.ContainsKey(pack.Manifest.Id)) continue;
                    PackEnabledKnobs[pack.Manifest.Id] = Instance.Config.Bind(
                        "Packs", $"{pack.Manifest.Id}.Enabled", true,
                        $"Enable/disable pack '{pack.Manifest.Id}' without deleting it.");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[ClassForge] Pre-scan for per-pack knobs failed (fail-safe — packs still respect their own manifest 'enabled' field): {ex}");
            }
        }

        internal static List<string> GetRoots()
        {
            var roots = new List<string>();
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginDir != null)
                roots.Add(Path.Combine(pluginDir, "ClassPacks"));

            var extra = AdditionalRoots.Value ?? string.Empty;
            foreach (var r in extra.Split(','))
            {
                var trimmed = r.Trim();
                if (trimmed.Length > 0) roots.Add(trimmed);
            }
            return roots;
        }

        internal static bool IsPackEnabled(string packId)
            => !PackEnabledKnobs.TryGetValue(packId, out var knob) || knob.Value;

        // ---- FTK2.DevKit ParityService registration (SPEC.md §3, §6, §9.6) ----
        // Surface (no compile-time dependency, resolved by name):
        //   static class FTK2Mods.DevKit.Core.ParityRegistry
        //   static void Register(string guid, string version, string dataHash, string[] enabledFeatures)
        private static bool _loggedDevKitAbsent;

        internal static void RegisterParity(ParityRegistration payload)
        {
            try
            {
                Type registryType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type candidate;
                    try { candidate = asm.GetType("FTK2Mods.DevKit.Core.ParityRegistry", false); }
                    catch { continue; }
                    if (candidate != null) { registryType = candidate; break; }
                }

                if (registryType == null)
                {
                    if (!_loggedDevKitAbsent)
                    {
                        Log.LogInfo("[ClassForge] FTK2.DevKit not present — skipping ParityService registration (no-op, fail-safe).");
                        _loggedDevKitAbsent = true;
                    }
                    return;
                }

                var method = registryType.GetMethod("Register", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string), typeof(string), typeof(string), typeof(string[]) }, null);
                if (method == null)
                {
                    Log.LogWarning("[ClassForge] FTK2.DevKit.Core.ParityRegistry found but has no matching " +
                                   "Register(string,string,string,string[]) — skipping registration (fail-safe).");
                    return;
                }

                method.Invoke(null, new object[] { payload.Guid, payload.Version, payload.DataHash, payload.EnabledFeatures });
                Log.LogInfo($"[ClassForge] Registered with FTK2.DevKit ParityService: dataHash={payload.DataHash}, " +
                            $"enabledFeatures=[{string.Join(",", payload.EnabledFeatures)}].");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[ClassForge] ParityService registration failed (non-fatal, fail-safe): {ex}");
            }
        }
    }
}
