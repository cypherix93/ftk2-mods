using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using WarBrain.Core;

namespace WarBrain.Plugin
{
    [BepInPlugin(Guid, Name, Version)]
    public class WarBrainPlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.warbrain";
        public const string Name = "FTK2.WarBrain";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static WarBrainPlugin Instance;

        // ---- knobs (SPEC §5) ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<bool> LogDecisionBreakdown;
        internal static ConfigEntry<bool> EnableScoringEngine;
        internal static ConfigEntry<float> GlobalIntelligenceScalar;
        internal static ConfigEntry<float> GlobalTemperatureMultiplier;
        internal static ConfigEntry<float> GlobalMistakeChanceAdd;
        internal static ConfigEntry<string> DefaultProfileId;
        internal static ConfigEntry<string> DefaultFocusPolicy;
        internal static ConfigEntry<bool> FailSafeOnError;
        // [Scaling] — the tunable enemy stat layer (owner-approved scope extension)
        internal static ConfigEntry<bool> EnableEnemyScaling;
        internal static ConfigEntry<float> EnemyAtkMultiplier;
        internal static ConfigEntry<float> EnemyHpMultiplier;
        internal static ConfigEntry<int> EnemyAccuracyAdd;
        internal static ConfigEntry<int> EnemyFocusAdd;

        // ---- data registries ----
        internal static Dictionary<string, BrainProfile> Profiles = new Dictionary<string, BrainProfile>();
        internal static Dictionary<string, DoctrineConfig> Doctrines = new Dictionary<string, DoctrineConfig>();
        internal static AssignmentResolver Assignments = new AssignmentResolver(Array.Empty<AssignmentConfig>());
        internal static string DataHash = "(none)";

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Master switch; false fully restores vanilla AIHelper behavior.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false, "Debug decision logs.");
            LogDecisionBreakdown = Config.Bind("General", "LogDecisionBreakdown", false, "Log every candidate's per-consideration score breakdown.");
            EnableScoringEngine = Config.Bind("Subsystems", "EnableScoringEngine", true, "If false, prefixes install but always defer to vanilla.");
            GlobalIntelligenceScalar = Config.Bind("Difficulty", "GlobalIntelligenceScalar", 1.0f, "Multiplies every profile's consideration weights.");
            GlobalTemperatureMultiplier = Config.Bind("Difficulty", "GlobalTemperatureMultiplier", 1.0f, "Multiplies every profile's softmax temperature.");
            GlobalMistakeChanceAdd = Config.Bind("Difficulty", "GlobalMistakeChanceAdd", 0.0f, "Added to every profile's MistakeChance (0..1).");
            DefaultProfileId = Config.Bind("Assignments", "DefaultProfileId", "WB_BRAIN_BRUTE", "Profile when no assignment rule matches.");
            DefaultFocusPolicy = Config.Bind("Assignments", "DefaultFocusPolicy", "", "Override every profile's FocusPolicy (SMART|MAX|VANILLA_RANDOM|NONE). Empty = per-profile.");
            FailSafeOnError = Config.Bind("Safety", "FailSafeOnError", true, "On any scoring exception, defer that turn to vanilla instead of throwing.");
            EnableEnemyScaling = Config.Bind("Scaling", "EnableEnemyScaling", false, "Tunable enemy stat layer (ATK/HP/ACC/FOC). MP: identical values required on all peers.");
            EnemyAtkMultiplier = Config.Bind("Scaling", "EnemyAtkMultiplier", 1.25f, "Multiplies enemy ATK (sim-validated default 1.25).");
            EnemyHpMultiplier = Config.Bind("Scaling", "EnemyHpMultiplier", 1.25f, "Multiplies enemy max HP.");
            EnemyAccuracyAdd = Config.Bind("Scaling", "EnemyAccuracyAdd", 8, "Flat add to enemy ACC (same shape as vanilla EnemyStatMods).");
            EnemyFocusAdd = Config.Bind("Scaling", "EnemyFocusAdd", 1, "Flat add to enemy FOC so smarter focus policies have fuel.");

            LoadData();
            ApplyPatches();

            Log.LogInfo($"{Name} {Version} loaded. Enabled={Enabled.Value}, profiles={Profiles.Count}, doctrines={Doctrines.Count}, dataHash={DataHash}");
            Log.LogWarning("MP note: enemy AI is deterministic lockstep on ALL peers — every peer must run identical WarBrain version+data (see docs/research/battle-ai-deep-dive.md §3).");
        }

        internal static void LoadData()
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var dataDir = Path.Combine(pluginDir, "data");
            if (!Directory.Exists(dataDir))
            {
                Log.LogWarning($"Data directory not found: {dataDir} — WarBrain will defer to vanilla (fail-safe).");
                return;
            }

            var profiles = new Dictionary<string, BrainProfile>();
            var doctrines = new Dictionary<string, DoctrineConfig>();
            var assignments = new List<AssignmentConfig>();
            var hash = new StringBuilder();

            foreach (var f in SortedFiles(Path.Combine(dataDir, "Profiles"), "*.brain.json"))
                TryLoad(f, hash, () => { var p = JsonHelper.Deserialize<BrainProfile>(File.ReadAllText(f)); profiles[p.Id] = p; });
            foreach (var f in SortedFiles(Path.Combine(dataDir, "Doctrines"), "*.doctrine.json"))
                TryLoad(f, hash, () => { var d = JsonHelper.Deserialize<DoctrineConfig>(File.ReadAllText(f)); doctrines[d.Id] = d; });
            foreach (var f in SortedFiles(Path.Combine(dataDir, "Assignments"), "*.assignment.json"))
                TryLoad(f, hash, () => assignments.Add(JsonHelper.Deserialize<AssignmentConfig>(File.ReadAllText(f))));

            Profiles = profiles;
            Doctrines = doctrines;
            Assignments = new AssignmentResolver(assignments);

            using (var sha = SHA256.Create())
                DataHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(hash.ToString()))).Replace("-", "").Substring(0, 16);

            Log.LogInfo($"WarBrain data loaded: {profiles.Count} profiles, {doctrines.Count} doctrines, {assignments.Sum(a => a.Rules.Count)} assignment rules. dataHash={DataHash}");
        }

        private static IEnumerable<string> SortedFiles(string dir, string pattern)
            => Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        private static void TryLoad(string file, StringBuilder hash, Action load)
        {
            try
            {
                load();
                hash.Append(Path.GetFileName(file)).Append('|').Append(File.ReadAllText(file)).Append('\n');
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to parse {file}: {e.Message} — file skipped, everything else loads (fail-safe).");
            }
        }

        private void ApplyPatches()
        {
            var harmony = new Harmony(Guid);

            Patch(harmony, typeof(AIHelper), "BehaviourAiDecision",
                prefix: new HarmonyMethod(typeof(AiDecisionPatches), nameof(AiDecisionPatches.BehaviourAiDecision_Prefix)));
            Patch(harmony, typeof(AIHelper), "StandardAiDecision",
                prefix: new HarmonyMethod(typeof(AiDecisionPatches), nameof(AiDecisionPatches.StandardAiDecision_Prefix)));
            Patch(harmony, typeof(ConfigsHelper), "ReloadConfigs",
                postfix: new HarmonyMethod(typeof(AiDecisionPatches), nameof(AiDecisionPatches.ReloadConfigs_Postfix)));
            Patch(harmony, typeof(CombatState), "Create",
                postfix: new HarmonyMethod(typeof(AiDecisionPatches), nameof(AiDecisionPatches.CombatStateCreate_Postfix)));
            Patch(harmony, typeof(CharacterHelper), "GetCharacterBaseStat",
                postfix: new HarmonyMethod(typeof(ScalingPatches), nameof(ScalingPatches.GetCharacterBaseStat_Postfix)),
                argumentTypes: new[] { typeof(Entity), typeof(string) });
        }

        private static void Patch(Harmony harmony, Type type, string method,
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

        internal static GlobalDifficultyKnobs Knobs() => new GlobalDifficultyKnobs
        {
            GlobalIntelligenceScalar = (decimal)GlobalIntelligenceScalar.Value,
            GlobalTemperatureMultiplier = (decimal)GlobalTemperatureMultiplier.Value,
            GlobalMistakeChanceAdd = (decimal)GlobalMistakeChanceAdd.Value
        };
    }
}
