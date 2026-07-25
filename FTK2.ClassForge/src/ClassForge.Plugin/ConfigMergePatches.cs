using System;
using System.Linq;
using System.Text.Json;
using ClassForge.Core;
using ClassForge.Core.IO;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The real M1 work: postfix <c>ConfigsHelper.LoadConfigs</c>/<c>ReloadConfigs</c> (SPEC-DELTA-v1.1 §1
    /// OQ#3 — the only viable merge mechanism, since neither entry point accepts extra source directories and
    /// both rebuild <c>Configs</c> from scratch, which is also why a re-run here is safe/idempotent) and merge
    /// ClassForge.Core's resolved <see cref="MergePlan"/> into the just-populated <c>Configs</c> dictionaries.
    /// </summary>
    public static class ConfigMergePatches
    {
        /// <summary>Postfix for <c>public static Configs LoadConfigs(string basePath)</c> — mutates the method's return value before the caller (which assigns it to Env.Configs) resumes.</summary>
        public static void LoadConfigs_Postfix(string basePath, ref Configs __result)
        {
            if (__result == null) return;
            RunMerge(__result, "LoadConfigs");
        }

        /// <summary>Postfix for <c>public static void ReloadConfigs(ref Configs configs, string basePath)</c>.</summary>
        public static void ReloadConfigs_Postfix(ref Configs configs, string basePath)
        {
            if (configs == null) return;
            RunMerge(configs, "ReloadConfigs");
        }

        private static void RunMerge(Configs configs, string caller)
        {
            if (!ClassForgePlugin.Enabled.Value)
            {
                ClassForgePlugin.Log.LogInfo($"[ClassForge] {caller}: Enabled=false — no packs scanned, vanilla Configs untouched.");
                return;
            }

            try
            {
                ClassForgePlugin.EnsurePackKnobsBound();

                var fs = new FileSystemFileSource();
                var roots = ClassForgePlugin.GetRoots();
                var loader = new PackLoader();
                var result = loader.Load(fs, roots, ClassForgePlugin.IsPackEnabled);

                foreach (var finding in result.Findings)
                    LogFinding(finding);

                ApplyPlan(configs, result.MergePlan);

                ClassForgePlugin.CurrentMergePlan = result.MergePlan;
                ClassForgePlugin.CurrentDataHash = result.DataHash;

                ClassForgePlugin.Log.LogInfo(
                    $"[ClassForge] {caller}: merged {result.EnabledOrderedPacks.Count} pack(s) " +
                    $"[{string.Join(", ", result.EnabledOrderedPacks.Select(p => p.Id))}] -> " +
                    $"{result.MergePlan.Characters.Count} classes, {result.MergePlan.Things.Count} things " +
                    $"({result.MergePlan.TraitIds.Count} traits), {result.MergePlan.Abilities.Count} abilities, " +
                    $"{result.MergePlan.Localization.Count} loc keys, {result.MergePlan.Icons.Count} icons, " +
                    $"{result.MergePlan.Portraits.Count} portraits. dataHash={result.DataHash}.");

                var payload = ParityRegistrationBuilder.Build(result, ClassForgePlugin.Guid, ClassForgePlugin.Version);
                ClassForgePlugin.RegisterParity(payload);
            }
            catch (Exception ex)
            {
                // Total fail-safe (CONVENTIONS.md): never let a bad pack, or any unexpected exception in the
                // pipeline, escape a Harmony postfix. Vanilla Configs are left as-is beyond whatever succeeded
                // before the failure.
                ClassForgePlugin.Log.LogError($"[ClassForge] {caller}: pack merge failed unexpectedly: {ex}");
            }
        }

        private static void LogFinding(Finding finding)
        {
            switch (finding.Severity)
            {
                case FindingSeverity.Error:
                    ClassForgePlugin.Log.LogError($"[ClassForge] {finding}");
                    break;
                case FindingSeverity.Warning:
                    ClassForgePlugin.Log.LogWarning($"[ClassForge] {finding}");
                    break;
                default:
                    if (ClassForgePlugin.VerboseLogging.Value)
                        ClassForgePlugin.Log.LogDebug($"[ClassForge] {finding}");
                    break;
            }
        }

        private static void ApplyPlan(Configs configs, MergePlan plan)
        {
            foreach (var op in plan.Characters)
            {
                try
                {
                    configs.Characters[op.Id] = JsonSerializer.Deserialize<CharacterConfig>(op.Value.ToJsonString());
                    LogApplied("class", op);
                }
                catch (Exception ex) { LogApplyFailed("class", op, ex); }
            }

            foreach (var op in plan.Things)
            {
                try
                {
                    configs.Things[op.Id] = JsonSerializer.Deserialize<ThingConfig>(op.Value.ToJsonString());
                    LogApplied("thing", op);
                }
                catch (Exception ex) { LogApplyFailed("thing", op, ex); }
            }

            foreach (var op in plan.Abilities)
            {
                try
                {
                    configs.Abilities[op.Id] = JsonSerializer.Deserialize<CombatAbilityConfig>(op.Value.ToJsonString());
                    LogApplied("ability", op);
                }
                catch (Exception ex) { LogApplyFailed("ability", op, ex); }
            }
        }

        private static void LogApplied(string kind, MergeOp op)
        {
            if (ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug($"[ClassForge] merged {kind} '{op.Id}' from pack '{op.SourcePackId}'.");
        }

        private static void LogApplyFailed(string kind, MergeOp op, Exception ex)
        {
            // Fail-safe per entry: one malformed id doesn't take down the rest of the pack's merge.
            ClassForgePlugin.Log.LogError($"[ClassForge] Failed to apply {kind} '{op.Id}' from pack '{op.SourcePackId}': {ex.Message} — entry skipped.");
        }
    }
}
