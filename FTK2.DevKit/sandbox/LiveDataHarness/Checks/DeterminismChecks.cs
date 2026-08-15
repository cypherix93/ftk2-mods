using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// A second full pack load in the same process must produce a byte-identical dataHash and an identical
    /// merge-op ordering. Peers whose dataHash disagrees fail the multiplayer parity handshake, so this is
    /// the cheapest available proxy for "would two players' installs agree with each other".
    /// </summary>
    public static class DeterminismChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult first)
        {
            runner.Section("Determinism");

            // One reload shared by all three cases: a third and fourth full load would triple the run time
            // to re-prove the same thing.
            var second = ClassForgeChecks.LoadAll(data);

            runner.Case("dataHash is well-formed and stable across two loads", () =>
            {
                Check.True(!string.IsNullOrEmpty(first.DataHash), "first load produced a dataHash");
                Check.True(FTK2Mods.DevKit.DataHasher.IsWellFormedHash(first.DataHash),
                    "dataHash is well-formed: " + first.DataHash);
                Check.Eq(first.DataHash, second.DataHash, "dataHash across two loads");
            });

            runner.Case("merge-op ordering is stable across two loads", () =>
            {
                var offenders = new List<string>();
                Compare(offenders, "Characters", first.MergePlan.Characters, second.MergePlan.Characters);
                Compare(offenders, "Things", first.MergePlan.Things, second.MergePlan.Things);
                Compare(offenders, "Abilities", first.MergePlan.Abilities, second.MergePlan.Abilities);
                Compare(offenders, "StatusEffects", first.MergePlan.StatusEffects, second.MergePlan.StatusEffects);
                Check.Empty(offenders, "merge-op ordering differences between two loads");
            });

            runner.Case("pack load order is stable across two loads", () =>
                Check.Eq(string.Join(",", first.EnabledOrderedPacks.Select(p => p.Id)),
                         string.Join(",", second.EnabledOrderedPacks.Select(p => p.Id)),
                         "resolved pack load order"));
        }

        private static void Compare(List<string> offenders, string label, List<MergeOp> a, List<MergeOp> b)
        {
            if (a.Count != b.Count) { offenders.Add(label + ": count " + a.Count + " vs " + b.Count); return; }
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal))
                    offenders.Add(label + "[" + i + "]: '" + a[i].Id + "' vs '" + b[i].Id + "'");
        }
    }
}
