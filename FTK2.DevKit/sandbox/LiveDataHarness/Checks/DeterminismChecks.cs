using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// A second full load in the same process must produce a byte-identical dataHash, an identical
    /// merge-op ordering, and an identical resolved pack order. Peers that disagree on dataHash fail the
    /// ParityService handshake and drop into SafeMode, so this is the cheapest possible proxy for "would
    /// two players' installs agree" — and it runs with no game and no second machine.
    ///
    /// One extra load is performed, shared by all three cases: three separate loads would triple the run
    /// time for no additional signal.
    /// </summary>
    public static class DeterminismChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent first)
        {
            runner.Section("Determinism");

            AuthoredContent second = AuthoredContent.Load(data);

            runner.Case("dataHash is well-formed and stable across two loads", delegate
            {
                Check.True(!string.IsNullOrEmpty(first.Packs.DataHash), "first load produced a dataHash");
                Check.True(FTK2Mods.DevKit.DataHasher.IsWellFormedHash(first.Packs.DataHash),
                    "dataHash is well-formed per DevKit.Core: " + first.Packs.DataHash);
                Check.True(first.Packs.DataHash.StartsWith(DataHasher.HashPrefix, StringComparison.Ordinal),
                    "dataHash carries the ClassForge.Core.DataHasher.HashPrefix: " + first.Packs.DataHash);
                Check.Eq(first.Packs.DataHash, second.Packs.DataHash, "dataHash across two loads");
            });

            runner.Case("merge-op ordering is stable across two loads", delegate
            {
                List<string> offenders = new List<string>();
                Compare(offenders, "Characters", first.Packs.MergePlan.Characters, second.Packs.MergePlan.Characters);
                Compare(offenders, "Things", first.Packs.MergePlan.Things, second.Packs.MergePlan.Things);
                Compare(offenders, "Abilities", first.Packs.MergePlan.Abilities, second.Packs.MergePlan.Abilities);
                Compare(offenders, "StatusEffects", first.Packs.MergePlan.StatusEffects, second.Packs.MergePlan.StatusEffects);
                Check.AtLeast(1, first.Packs.MergePlan.Characters.Count, "ops compared (a zero-op compare is vacuous)");
                Check.Empty(offenders, "merge-op ordering differences between two loads");
            });

            runner.Case("pack load order and recipe universe are stable across two loads", delegate
            {
                Check.Eq(Join(first.Packs.EnabledOrderedPacks), Join(second.Packs.EnabledOrderedPacks),
                    "resolved pack load order");
                Check.Eq(string.Join(",", first.AllRecipeIds.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal)),
                         string.Join(",", second.AllRecipeIds.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal)),
                         "authored + generated recipe id universe");
            });

            runner.Case("NEGATIVE control: the comparator detects a difference", delegate
            {
                // Compare() returning nothing for genuinely different inputs would make all three cases
                // above permanently green.
                List<MergeOp> a = new List<MergeOp>();
                a.Add(new MergeOp("A", ClassForge.Core.Json.JsonValue.Null, "P"));
                a.Add(new MergeOp("B", ClassForge.Core.Json.JsonValue.Null, "P"));
                List<MergeOp> b = new List<MergeOp>();
                b.Add(new MergeOp("B", ClassForge.Core.Json.JsonValue.Null, "P"));
                b.Add(new MergeOp("A", ClassForge.Core.Json.JsonValue.Null, "P"));

                List<string> reordered = new List<string>();
                Compare(reordered, "Synthetic", a, b);
                Check.AtLeast(1, reordered.Count, "Compare must report a reordering");

                List<string> shortened = new List<string>();
                Compare(shortened, "Synthetic", a, new List<MergeOp>());
                Check.AtLeast(1, shortened.Count, "Compare must report a count difference");

                List<string> identical = new List<string>();
                Compare(identical, "Synthetic", a, a);
                Check.Empty(identical, "Compare must report nothing for identical lists");
            });
        }

        private static string Join(List<PackManifest> packs)
        {
            return string.Join(",", packs.Select(delegate (PackManifest p) { return p.Id; }));
        }

        private static void Compare(List<string> offenders, string label, List<MergeOp> a, List<MergeOp> b)
        {
            if (a.Count != b.Count)
            {
                offenders.Add(label + ": count " + a.Count + " vs " + b.Count);
                return;
            }
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal))
                    offenders.Add(label + "[" + i + "]: '" + a[i].Id + "' vs '" + b[i].Id + "'");
        }
    }
}
