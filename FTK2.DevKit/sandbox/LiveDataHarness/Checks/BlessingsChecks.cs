using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blessings.Core.Model;
using Blessings.Core.Parsing;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Offline replica of Blessings' fail-closed data gate
    /// (GrantAnchorPatches.EnsureRosterResolvesAgainstConfigs): every blessing's TraitId must resolve in
    /// Configs.Things, else the plugin disables itself for the whole session and logs one error. In-game
    /// that costs a full launch to discover; here it costs about two seconds.
    ///
    /// The resolvable set is live Configs.Things PLUS the merged pack Things, because the BLSS traits are
    /// added at runtime by ClassForge from the same pack — checking live ids alone would report all 15 as
    /// unresolved, which is precisely the false-negative-shaped false positive this harness exists to avoid.
    /// </summary>
    public static class BlessingsChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            runner.Section("Blessings — fail-closed roster gate");

            string repo = PackRoots.RepoRoot();
            string blessingsJson = repo == null
                ? null
                : Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks", "BLSS_PACK_EOR_BLESSINGS", "blessings.json");

            runner.Case("blessings.json parses without Error findings", delegate
            {
                Check.True(blessingsJson != null && File.Exists(blessingsJson),
                    "blessings.json exists at " + (blessingsJson ?? "<repo root not found>"));

                ParseResult parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "BlessingsRegistryParser succeeded (Registry != null)");
                Check.True(!parsed.HasErrors,
                    "blessings.json has no Error findings; got: " +
                    string.Join(" | ", parsed.Findings.Select(delegate (Blessings.Core.Diagnostics.Finding f) { return f.ToString(); })));
                Check.AtLeast(1, parsed.Registry.Blessings.Count, "blessings in the roster");
            });

            runner.Case("every roster TraitId resolves (the plugin will NOT self-disable)", delegate
            {
                ParseResult parsed = BlessingsRegistryParser.Parse(File.ReadAllText(blessingsJson));
                Check.True(parsed.Success, "registry parsed");

                HashSet<string> resolvable = Resolvable(data, content);
                List<string> missing = new List<string>();
                foreach (BlessingEntry b in parsed.Registry.Blessings)
                    if (!resolvable.Contains(b.TraitId))
                        missing.Add("blessing '" + b.Id + "' TraitId '" + b.TraitId + "' unresolved — Blessings would log " +
                                    "'roster TraitId(s) do not resolve in Env.Configs.Things -- this plugin is DISABLED for the session'");

                Check.Empty(missing, "unresolvable Blessings roster TraitIds");
            });

            runner.Case("NEGATIVE control: an invented TraitId would be caught", delegate
            {
                HashSet<string> resolvable = Resolvable(data, content);
                Check.AtLeast(1800, resolvable.Count,
                    "resolvable Things (live + merged) — a small set here would make the " +
                    "gate check above fail loudly rather than silently, but a set containing everything " +
                    "would make it vacuous");
                Check.True(!resolvable.Contains("TRAIT_BLSS_HARNESS_NEVER_SHIPPED"),
                    "the resolvable-Things set must not contain an invented id");
            });
        }

        private static HashSet<string> Resolvable(GameData data, AuthoredContent content)
        {
            HashSet<string> resolvable = new HashSet<string>(data.Ids("Things"), StringComparer.Ordinal);
            foreach (MergeOp op in content.Packs.MergePlan.Things) resolvable.Add(op.Id);
            return resolvable;
        }
    }
}
