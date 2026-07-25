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

            // MP review M2 — honest Block semantics, documented here rather than pretended in code:
            // ClassForge's Block policy (SPEC.md §9.5 / SPEC-DELTA-v1.1 §5.3) turns off RUNTIME features
            // (recipe engine, trait-loadout injection, class-select injection — everything gated behind
            // ClassForgePlugin.FeaturesActive), not the content merge. There used to be an
            // `if (ParityBridge.Blocked) return;` guard right here; it was dead code in practice (this method
            // only runs from ConfigsHelper.LoadConfigs at boot, before any multiplayer handshake could
            // possibly have latched Blocked, and ConfigsHelper.ReloadConfigs has no vanilla caller at all — see
            // the MP review), and worse, it actively misrepresented the real posture as "merge stops too".
            // The honest posture is: merged Configs entries (classes/traits/items/abilities/localization/
            // icons/portraits) are inert DATA. With FeaturesActive false, nothing exercises that data — no
            // class-select/trait-loadout injection offers it, and the recipe engine never fires — so
            // re-running (or having already run) the merge under Block cannot itself cause an asymmetric
            // simulation. Re-running the merge is also always safe: ApplyPlan is idempotent, and this is the
            // same data-only content ConfigsHelper.LoadConfigs/ReloadConfigs already rebuilds from scratch.
            try
            {
                ClassForgePlugin.EnsurePackKnobsBound();

                var fs = new FileSystemFileSource();
                var roots = ClassForgePlugin.GetRoots();
                var loader = new PackLoader();

                // MP review M0 — adds-only enforcement against LIVE ids. Snapshot the ids already present in
                // `configs` (vanilla content the native LoadConfigs/ReloadConfigs body just built, BEFORE this
                // postfix applies anything) so MergePlanner can refuse a pack entry that would silently
                // overwrite pre-existing content (e.g. a pack shipping an id named "KNIGHT").
                var liveIds = new LiveIdSets(configs.Characters?.Keys, configs.Things?.Keys, configs.Abilities?.Keys);
                var result = loader.Load(fs, roots, ClassForgePlugin.IsPackEnabled, liveIds);

                foreach (var finding in result.Findings)
                    LogFinding(finding);

                ApplyPlan(configs, result.MergePlan);

                ClassForgePlugin.CurrentMergePlan = result.MergePlan;
                ClassForgePlugin.CurrentDataHash = result.DataHash;

                // Icon/portrait paths may have changed (or their PNGs been edited) — drop the texture cache
                // so a hot-reload actually shows the new art.
                AssetPatches.InvalidateCache();

                // Rebuild the skill-recipe book from every enabled pack's skillrecipes.json. Full rebuild,
                // never incremental, so a hot-reload cannot leave a stale recipe behind; this also drops the
                // per-battle recipe runtime (budgets/cooldowns keyed to the old book).
                RecipeEngineHost.LoadBook(result);

                ClassForgePlugin.Log.LogInfo(
                    $"[ClassForge] {caller}: merged {result.EnabledOrderedPacks.Count} pack(s) " +
                    $"[{string.Join(", ", result.EnabledOrderedPacks.Select(p => p.Id))}] -> " +
                    $"{result.MergePlan.Characters.Count} classes, {result.MergePlan.Things.Count} things " +
                    $"({result.MergePlan.TraitIds.Count} traits), {result.MergePlan.Abilities.Count} abilities, " +
                    $"{result.MergePlan.Localization.Count} loc keys, {result.MergePlan.Icons.Count} icons, " +
                    $"{result.MergePlan.Portraits.Count} portraits. dataHash={result.DataHash}.");

                // MP review B4: hand the real, current knob values to the registration builder so a knob
                // difference between peers becomes a genuine parity divergence instead of silent "Match".
                var payload = ParityRegistrationBuilder.Build(
                    result, ClassForgePlugin.Guid, ClassForgePlugin.Version,
                    ClassForgePlugin.EnableRecipeEngine.Value, ClassForgePlugin.EnableTraitLoadoutInjection.Value);
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
