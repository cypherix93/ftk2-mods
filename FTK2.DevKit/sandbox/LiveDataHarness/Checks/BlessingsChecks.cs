using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blessings.Core.Parsing;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Offline replica of the Blessings fail-closed data gate: every blessing's TraitId must resolve in
    /// Configs.Things, or the plugin disables itself for the entire session and says so in one log line.
    /// In-game that costs a full launch to discover, and only if someone reads the log; here it costs the
    /// couple of seconds the config load already took.
    /// </summary>
    public static class BlessingsChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult result)
        {
            runner.Section("Blessings — fail-closed roster gate");

            var repo = PackRoots.RepoRoot();
            var blessingsJson = repo == null ? null
                : Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks", "BLSS_PACK_EOR_BLESSINGS", "blessings.json");

            runner.Case("blessings.json parses without errors", () =>
            {
                Check.True(blessingsJson != null && File.Exists(blessingsJson), "blessings.json exists at " + blessingsJson);
                var parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "BlessingsRegistryParser succeeded (Registry != null)");
                Check.True(!parsed.HasErrors,
                    "blessings.json has no Error findings; got: " + string.Join(" | ", parsed.Findings.Select(f => f.ToString())));
                Check.AtLeast(1, parsed.Registry.Blessings.Count, "blessings in the roster");
            });

            runner.Case("every roster TraitId resolves (plugin will NOT self-disable)", () =>
            {
                var parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "registry parsed");
                var registry = parsed.Registry;

                var resolvable = ResolvableThings(data, result);

                var missing = registry.Blessings
                    .Select(b => b.TraitId)
                    .Where(t => !resolvable.Contains(t))
                    .Select(t => "TraitId '" + t + "' unresolved — the plugin would disable itself for the session");

                Check.Empty(missing, "unresolvable Blessings roster TraitIds");
            });

            runner.Case("negative control: an invented TraitId would be caught", () =>
                Check.True(!ResolvableThings(data, result).Contains("TRAIT_CF_HARNESS_NEVER_SHIPPED"),
                    "the resolvable-Things set must not contain an invented id — otherwise the gate check is vacuous"));
        }

        /// <summary>
        /// What Configs.Things will contain once the packs merge: the live dictionary plus everything the
        /// merge plan adds. The gate runs after the merge in-game, so checking against live ids alone would
        /// report every pack-supplied trait as missing.
        /// </summary>
        private static ISet<string> ResolvableThings(GameData data, PackLoadResult result)
        {
            var resolvable = new HashSet<string>(data.Ids("Things"), StringComparer.Ordinal);
            foreach (var op in result.MergePlan.Things) resolvable.Add(op.Id);
            return resolvable;
        }
    }
}
