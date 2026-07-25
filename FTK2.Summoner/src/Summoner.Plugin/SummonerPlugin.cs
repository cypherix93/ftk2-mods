using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Summoner.Core.Diagnostics;
using Summoner.Core.Merge;
using Summoner.Core.Packs;
using Summoner.Core.Parity;
using Summoner.Core.Validation;
using Summoner.Plugin.Adapters;
using Summoner.Plugin.Patches;
using PluginParity = Summoner.Plugin.Parity.ParityRegistration;

namespace Summoner.Plugin
{
    [BepInPlugin(Guid, Name, Version)]
    public class SummonerPlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.summoner";
        public const string Name = "FTK2.Summoner";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static SummonerPlugin Instance;

        // ---- knobs (design §A5) ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<string> AdditionalRoots;
        internal static ConfigEntry<string> OnParityMismatch;

        // ---- immutable in-memory pack model (design §A3.2: read once in Awake(), never mutated) ----
        internal static IReadOnlyList<FollowerPack> Packs = Array.Empty<FollowerPack>();
        internal static string DataHash = "(none)";
        private static bool _parityRegistered;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch; false = no scan, no patch, vanilla untouched.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false,
                "Per-pack/per-entry merge decisions at LogLevel.Debug.");
            AdditionalRoots = Config.Bind("Packs", "AdditionalRoots", "",
                "Comma-separated absolute dirs scanned in addition to <plugin>/data/FollowerPacks.");
            OnParityMismatch = Config.Bind("Multiplayer", "OnParityMismatch", "Block",
                "WarnAndSafeMode | WarnOnly | Block. Summoner's default is Block, overriding the " +
                "repo-wide WarnAndSafeMode default, for every milestone (design §A5) -- the M0 " +
                "merge is load-time, so SafeMode has no runtime feature to switch off. WarnAndSafeMode is NOT recommended.");

            LoadPacks();
            ApplyPatches();

            Log.LogInfo($"{Name} {Version} loaded. Enabled={Enabled.Value}, packs={Packs.Count}, " +
                        $"followers={Packs.Sum(p => p.Followers.Count)}, dataHash={DataHash}");

            if (!string.Equals(OnParityMismatch.Value, "Block", StringComparison.OrdinalIgnoreCase))
                Log.LogWarning("[Summoner] OnParityMismatch is not Block. Design §A5: SafeMode is a no-op for " +
                                "this load-time loader -- WarnAndSafeMode degenerates to 'nothing changes', leaving " +
                                "divergent peers with divergent Configs.Followers. Not recommended.");
        }

        /// <summary>Reads FollowerPacks/ from disk exactly once. Never called again for a hot-reload --
        /// ReloadConfigs re-plans/re-applies from this same immutable list (design §A3.2).</summary>
        internal static void LoadPacks()
        {
            if (!Enabled.Value)
            {
                Packs = Array.Empty<FollowerPack>();
                DataHash = "(disabled)";
                return;
            }

            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var defaultRoot = Path.Combine(pluginDir ?? ".", "data", "FollowerPacks");
                var source = new FileSystemPackSource();
                var codec = new GameJsonCodec();

                var allPacks = new List<FollowerPack>();
                var findings = new List<Finding>();

                foreach (var root in Roots(defaultRoot))
                {
                    if (!Directory.Exists(root))
                    {
                        Log.LogDebug($"[Summoner] pack root not found, skipped: {root}");
                        continue;
                    }

                    var result = PackLoader.Load(source, codec, root);
                    allPacks.AddRange(result.Packs);
                    findings.AddRange(result.Findings);
                }

                var externalCharacterIds = new HashSet<string>(StringComparer.Ordinal);
                var validationFindings = PackValidator.ValidateAll(allPacks, externalCharacterIds);
                findings.AddRange(validationFindings);

                foreach (var f in findings)
                    LogFinding(f);

                Packs = allPacks;

                var hashInput = Packs
                    .Select(p => (p.Manifest.Id, (IReadOnlyList<(string RelPath, byte[] Bytes)>)p.Files))
                    .ToList();
                DataHash = DataHasher.ComputeHash(hashInput);
            }
            catch (Exception e)
            {
                Log.LogError($"[Summoner] pack load failed: {e} -- Summoner defers to vanilla for this pass (fail-safe).");
                Packs = Array.Empty<FollowerPack>();
                DataHash = "(error)";
            }
        }

        /// <summary>Re-plans from the immutable Packs list and applies into the freshly-built Configs
        /// instance handed to us by a LoadConfigs/ReloadConfigs postfix (design §A3).</summary>
        internal static void MergeInto(Configs configs)
        {
            if (configs == null) return;

            var existingFollowerIds = new HashSet<string>(configs.Followers?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var existingCharacterIds = new HashSet<string>(configs.Characters?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            var plan = MergePlanner.Plan(Packs, existingFollowerIds, existingCharacterIds);
            ConfigsSink.Apply(plan, configs, Log);

            if (VerboseLogging.Value)
                Log.LogDebug($"[Summoner] merge complete: +{plan.CharacterAdds.Count} characters, " +
                             $"+{plan.FollowerAdds.Count} followers, {plan.Skips.Count} skipped, {plan.Rejects.Count} rejected.");

            if (!_parityRegistered)
            {
                var enabledPackIds = Packs.Where(p => p.Manifest.Enabled).Select(p => p.Manifest.Id)
                    .OrderBy(id => id, StringComparer.Ordinal).ToArray();
                PluginParity.Register(Guid, Version, DataHash, enabledPackIds, Log);
                _parityRegistered = true;
            }
        }

        private static IEnumerable<string> Roots(string defaultRoot)
        {
            yield return defaultRoot;
            var extra = AdditionalRoots.Value;
            if (string.IsNullOrWhiteSpace(extra)) yield break;
            foreach (var raw in extra.Split(','))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }

        private static void LogFinding(Finding f)
        {
            switch (f.Severity)
            {
                case FindingSeverity.Error:
                    Log.LogError(f.ToString());
                    break;
                case FindingSeverity.Warn:
                    Log.LogWarning(f.ToString());
                    break;
                default:
                    if (VerboseLogging.Value) Log.LogDebug(f.ToString());
                    break;
            }
        }

        private void ApplyPatches()
        {
            var harmony = new Harmony(Guid);

            Patch(harmony, typeof(ConfigsHelper), "LoadConfigs",
                postfix: new HarmonyMethod(typeof(ConfigsMergePatches), nameof(ConfigsMergePatches.LoadConfigs_Postfix)));
            Patch(harmony, typeof(ConfigsHelper), "ReloadConfigs",
                postfix: new HarmonyMethod(typeof(ConfigsMergePatches), nameof(ConfigsMergePatches.ReloadConfigs_Postfix)));
        }

        private static void Patch(Harmony harmony, Type type, string method, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                Log.LogError($"Target NOT found: {type.Name}.{method} -- this feature is disabled (fail-safe).");
                return;
            }
            harmony.Patch(target, prefix: prefix, postfix: postfix);
            Log.LogInfo($"Target found: {type.Name}.{method}");
        }
    }
}
