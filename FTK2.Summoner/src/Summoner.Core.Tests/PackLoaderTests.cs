using System;
using System.IO;
using System.Linq;
using Summoner.Core.Diagnostics;
using Summoner.Core.Packs;

namespace Summoner.Core.Tests
{
    public static class PackLoaderTests
    {
        public static void SortsPackIdsOrdinally_NotFilesystemOrder()
        {
            var source = new InMemoryPackFileSource();
            const string root = "root://ordinal";

            // Same LoadOrder (tie) on all three, so the only thing that can produce a deterministic
            // final order is Id ordinal comparison -- and the *discovery* dir list is inserted into
            // the source in an order that matches neither the dir names' nor the ids' ordinal order,
            // so a bug that trusted "whatever ListPackDirectories/insertion order happened to be"
            // would show up as a different final order across runs/insertion orders.
            source.AddSimplePack(root, "dirZ", "SMN_PACK_A", 100, Array.Empty<string>(), "{}");
            source.AddSimplePack(root, "dirY", "SMN_PACK_C", 100, Array.Empty<string>(), "{}");
            source.AddSimplePack(root, "dirX", "SMN_PACK_B", 100, Array.Empty<string>(), "{}");

            var result = PackLoader.Load(source, new TestJsonCodec(), root);

            Check.Equal(0, result.Findings.Count(f => f.Severity == FindingSeverity.Error), "no findings expected for three well-formed packs");
            Check.SequenceEqual(
                new[] { "SMN_PACK_A", "SMN_PACK_B", "SMN_PACK_C" },
                result.Packs.Select(p => p.Manifest.Id),
                "packs with tied LoadOrder must resolve in Id ordinal order, independent of discovery/insertion order");
        }

        public static void TopoSortsByLoadOrderThenIdOrdinal()
        {
            var source = new InMemoryPackFileSource();
            const string root = "root://loadorder";

            source.AddSimplePack(root, "d1", "SMN_PACK_TWO_B", 20, Array.Empty<string>(), "{}");
            source.AddSimplePack(root, "d2", "SMN_PACK_TEN", 10, Array.Empty<string>(), "{}");
            source.AddSimplePack(root, "d3", "SMN_PACK_TWO_A", 20, Array.Empty<string>(), "{}");

            var result = PackLoader.Load(source, new TestJsonCodec(), root);

            Check.SequenceEqual(
                new[] { "SMN_PACK_TEN", "SMN_PACK_TWO_A", "SMN_PACK_TWO_B" },
                result.Packs.Select(p => p.Manifest.Id),
                "expected (LoadOrder asc, Id ordinal asc) ordering");
        }

        public static void MissingDependency_SkipsPackAndReportsFinding()
        {
            var source = new InMemoryPackFileSource();
            const string root = "root://missingdep";

            source.AddSimplePack(root, "d1", "SMN_PACK_DEPENDENT", 100, new[] { "SMN_PACK_GHOST" }, "{}");

            var result = PackLoader.Load(source, new TestJsonCodec(), root);

            Check.DoesNotContain(result.Packs, p => p.Manifest.Id == "SMN_PACK_DEPENDENT",
                "pack with an unresolvable dependency must not be included in the loaded set");
            Check.Contains(result.Findings,
                f => f.Severity == FindingSeverity.Error && f.Check == "missing_dependency" && f.PackId == "SMN_PACK_DEPENDENT",
                "expected a missing_dependency error finding for SMN_PACK_DEPENDENT");
        }

        public static void DependencyCycle_SkipsPacksAndReportsFinding()
        {
            var source = new InMemoryPackFileSource();
            const string root = "root://cycle";

            source.AddSimplePack(root, "d1", "SMN_PACK_CYCLE_A", 100, new[] { "SMN_PACK_CYCLE_B" }, "{}");
            source.AddSimplePack(root, "d2", "SMN_PACK_CYCLE_B", 100, new[] { "SMN_PACK_CYCLE_A" }, "{}");

            var result = PackLoader.Load(source, new TestJsonCodec(), root);

            Check.Equal(0, result.Packs.Count, "both packs in a 2-cycle must be excluded from the loaded set");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Error && f.Check == "dependency_cycle" && f.PackId == "SMN_PACK_CYCLE_A",
                "expected a dependency_cycle finding naming SMN_PACK_CYCLE_A");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Error && f.Check == "dependency_cycle" && f.PackId == "SMN_PACK_CYCLE_B",
                "expected a dependency_cycle finding naming SMN_PACK_CYCLE_B");
        }

        public static void MalformedJsonInOnePack_DisablesOnlyThatPack()
        {
            var root = Path.Combine(FixturesRoot(), "malformed_root");
            var result = PackLoader.Load(new TestFileSystemPackSource(), new TestJsonCodec(), root);

            Check.Equal(1, result.Packs.Count, "exactly one of the two sibling packs should load");
            Check.Equal("SMN_PACK_GOOD", result.Packs[0].Manifest.Id, "the well-formed sibling should be the one that loaded");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Error && f.PackId == "SMN_PACK_BAD",
                "expected an error finding naming the malformed pack SMN_PACK_BAD");
        }

        internal static string FixturesRoot()
            => Path.Combine(AppContext.BaseDirectory, "fixtures");
    }
}
