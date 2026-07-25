using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Summoner.Core.Diagnostics;
using Summoner.Core.Packs;

namespace Summoner.Core.Merge
{
    /// <summary>
    /// Adds-only, hard-enforced merge planner (design §A2.3, §A3.2). Pure function of its inputs:
    /// same packs + same existing-id sets always produce the same plan, and applying a plan's Adds
    /// to the existing-id sets before calling Plan again yields an empty second-pass Adds list
    /// (idempotency by construction, not by luck -- see design §A3.2).
    /// </summary>
    public static class MergePlanner
    {
        private static readonly Regex IdPattern = new Regex("^SMN_[A-Z0-9_]+$", RegexOptions.Compiled);

        /// <summary>
        /// Characters are planned before followers so a pack's own characters.json satisfies its
        /// own ConfigName references within the same pass (design §A4.1). Only packs whose
        /// manifest reports Enabled=true participate.
        /// </summary>
        public static MergePlan Plan(IReadOnlyList<FollowerPack> packs, ISet<string> existingFollowerIds, ISet<string> existingCharacterIds)
        {
            var plan = new MergePlan();
            var enabledPacks = (packs ?? Array.Empty<FollowerPack>()).Where(p => p.Manifest != null && p.Manifest.Enabled).ToList();

            var knownCharacterIds = new HashSet<string>(existingCharacterIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (var pack in enabledPacks)
            {
                foreach (var kv in pack.Characters.OrderBy(k => k.Key, StringComparer.Ordinal))
                    PlanEntry(plan.CharacterAdds, plan.Skips, plan.Rejects, knownCharacterIds, pack.Manifest.Id, kv.Key, kv.Value);
            }

            var knownFollowerIds = new HashSet<string>(existingFollowerIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (var pack in enabledPacks)
            {
                foreach (var kv in pack.Followers.OrderBy(k => k.Key, StringComparer.Ordinal))
                    PlanEntry(plan.FollowerAdds, plan.Skips, plan.Rejects, knownFollowerIds, pack.Manifest.Id, kv.Key, kv.Value);
            }

            return plan;
        }

        private static void PlanEntry(List<MergeAdd> adds, List<MergeSkip> skips, List<Finding> rejects,
            HashSet<string> known, string packId, string id, object entry)
        {
            if (!IdPattern.IsMatch(id))
            {
                rejects.Add(Finding.Error(packId, id, "id_shape", $"'{id}' does not match ^SMN_[A-Z0-9_]+$ -- rejected, not merged."));
                return;
            }

            if (known.Contains(id))
            {
                skips.Add(new MergeSkip { PackId = packId, Id = id, Reason = "id already present in live Configs (adds-only)." });
                return;
            }

            known.Add(id);
            adds.Add(new MergeAdd { PackId = packId, Id = id, Entry = entry });
        }
    }
}
