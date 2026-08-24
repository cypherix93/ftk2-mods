using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassForge.Core
{
    /// <summary>
    /// Builds the payload for the reflection-based call to
    /// <c>FTK2Mods.DevKit.ParityService.Register(string guid, string version, string dataHash, string[] enabledFeatures)</c>
    /// (SPEC.md §3, §6, §9.6). Core has no DevKit reference (mods never reference each other's assemblies);
    /// the Plugin resolves the call by name at runtime and no-ops gracefully if DevKit is absent.
    ///
    /// <para><b>MP review B4.</b> The original implementation put only enabled pack ids into
    /// <c>EnabledFeatures</c>, so two peers with byte-identical packs but a different
    /// <c>[Skills] EnableRecipeEngine</c> (or trait-loadout) knob compared as <c>Match</c> even though their
    /// RNG draw counts / loadout-pool lengths diverge from the first affected action. Gameplay-relevant
    /// feature knobs are now encoded as extra entries in the same sorted <c>string[]</c>, canonical form
    /// <c>"feature:&lt;KnobName&gt;=&lt;true|false&gt;"</c>, so a knob mismatch becomes a real
    /// <c>ParityVerdictKind</c> divergence DevKit's comparer already knows how to report.</para>
    /// <para><b>Only <c>EnableRecipeEngine</c> and <c>EnableTraitLoadoutInjection</c> qualify.</b>
    /// Both change what actually executes against replicated combat/party state on divergence (RNG draw
    /// counts, loadout-pool length — see B4's failure scenarios). <c>EnableClassSelectInjection</c> is
    /// deliberately EXCLUDED: it only filters which classes appear in the character-creation UI list — a
    /// local, presentation-only view over <c>Configs</c> that never itself touches <c>Configs</c>, RNG, or
    /// anything replicated — so including it would just make a purely cosmetic UI preference trip the Block
    /// policy for no gameplay reason. <c>EnableIconFallback</c> is the same kind of presentation-only knob
    /// (icon/portrait art) and is excluded for the identical reason. The master <c>Enabled</c> switch is not
    /// listed either: when it is false, <c>ConfigMergePatches</c> never runs the merge or calls this builder
    /// at all, so there is no payload for it to appear in.</para>
    /// </summary>
    public static class ParityRegistrationBuilder
    {
        internal const string FeaturePrefix = "feature:";

        public static ParityRegistration Build(
            PackLoadResult result,
            string guid,
            string version,
            bool enableRecipeEngine,
            bool enableTraitLoadoutInjection,
            bool enableStatModifiers = true)
        {
            var features = new List<string>();
            features.AddRange(result.EnabledOrderedPacks.Select(p => p.Id).Distinct(StringComparer.Ordinal));

            // B4: gameplay-relevant feature knobs, canonical "feature:Name=true|false" form.
            features.Add(FeaturePrefix + "EnableRecipeEngine=" + BoolText(enableRecipeEngine));
            features.Add(FeaturePrefix + "EnableTraitLoadoutInjection=" + BoolText(enableTraitLoadoutInjection));
            features.Add(FeaturePrefix + "EnableStatModifiers=" + BoolText(enableStatModifiers));

            return new ParityRegistration
            {
                Guid = guid,
                Version = version,
                DataHash = result.DataHash,
                EnabledFeatures = features
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        private static string BoolText(bool value) => value ? "true" : "false";
    }
}
