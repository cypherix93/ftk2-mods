using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassForge.Core
{
    /// <summary>
    /// Builds the final <see cref="MergePlan"/> from a resolved pack order + their parsed content (SPEC.md §3
    /// runtime flow). Id collisions across packs resolve last-pack-wins (the later pack in resolved load order),
    /// logging both pack ids (SPEC.md §3, §8 edge cases). Iteration is always over keys sorted ordinally —
    /// Dictionary enumeration order is never relied on for anything observable (MULTIPLAYER.md R2).
    /// </summary>
    public static class MergePlanner
    {
        public static MergePlan Build(List<(DiscoveredPack Pack, ParsedPack Content)> orderedContents, List<Finding> findings)
        {
            var plan = new MergePlan();
            var characters = new Dictionary<string, MergeOp>(StringComparer.Ordinal);
            var things = new Dictionary<string, MergeOp>(StringComparer.Ordinal);
            var abilities = new Dictionary<string, MergeOp>(StringComparer.Ordinal);

            foreach (var entry in orderedContents)
            {
                var packId = entry.Pack.Manifest.Id;
                var content = entry.Content;

                MergeCategory(characters, packId, content.Classes, findings, "Character");
                MergeCategory(things, packId, content.Traits, findings, "Trait/Thing");
                MergeCategory(things, packId, content.Items, findings, "Item/Thing");
                MergeCategory(abilities, packId, content.Abilities, findings, "Ability");

                MergeStringDict(plan.Localization, packId, content.Localization, findings, "CF_LOC_OVERRIDE", "Localization key");
                MergeStringDict(plan.Icons, packId, content.Icons, findings, "CF_ICON_OVERRIDE", "Icon id");
                MergeStringDict(plan.Portraits, packId, content.Portraits, findings, "CF_PORTRAIT_OVERRIDE", "Portrait id");
            }

            plan.Characters = characters.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            plan.Things = things.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            plan.Abilities = abilities.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

            // Class:"TRAIT" entries register with the trait registry (SPEC.md §3), regardless of which source
            // file (traits.json vs. items.json) they came from.
            plan.TraitIds = plan.Things
                .Where(m => string.Equals(m.Value.GetString("Class"), "TRAIT", StringComparison.Ordinal))
                .Select(m => m.Id)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            plan.Findings = findings;
            return plan;
        }

        private static void MergeCategory(
            Dictionary<string, MergeOp> target,
            string packId,
            Dictionary<string, ClassForge.Core.Json.JsonValue> entries,
            List<Finding> findings,
            string label)
        {
            foreach (var kv in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (target.TryGetValue(kv.Key, out var existing) && !string.Equals(existing.SourcePackId, packId, StringComparison.Ordinal))
                {
                    findings.Add(Finding.Warning("CF_ID_COLLISION",
                        $"{label} id '{kv.Key}' is defined by both pack '{existing.SourcePackId}' and pack '{packId}' — " +
                        $"'{packId}' wins (later in resolved load order).", packId));
                }
                target[kv.Key] = new MergeOp(kv.Key, kv.Value, packId);
            }
        }

        private static void MergeStringDict(
            Dictionary<string, string> target,
            string packId,
            Dictionary<string, string> entries,
            List<Finding> findings,
            string findingCode,
            string label)
        {
            foreach (var kv in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (target.ContainsKey(kv.Key) && !string.Equals(target[kv.Key], kv.Value, StringComparison.Ordinal))
                {
                    findings.Add(Finding.Info(findingCode, $"{label} '{kv.Key}' overridden by pack '{packId}'.", packId));
                }
                target[kv.Key] = kv.Value;
            }
        }
    }
}
