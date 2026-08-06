using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blessings.Core.Parsing;
using Blessings.Core.Resolution;

namespace Blessings.Core.Tests
{
    /// <summary>
    /// §8.2 item 1's roster&lt;-&gt;pack cross-check, deferred from M1 to M2 per the M2 task brief: reads
    /// the ACTUAL repo pack files (not a fixture) and asserts blessings.json's roster agrees with
    /// traits.json. Path is repo-relative from this project's directory -- ".." (src) ".." (FTK2.Blessings)
    /// then "data/ClassPacks/BLSS_PACK_EOR_BLESSINGS/", which is where `dotnet run --project
    /// FTK2.Blessings/src/Blessings.Core.Tests` sets the working directory (the .csproj's own directory).
    /// </summary>
    public static class RosterPackCrossCheckTests
    {
        private static readonly Regex TraitIdPattern = new Regex("^TRAIT_[A-Z0-9_]+$", RegexOptions.Compiled);

        /// <summary>
        /// Locates the repo's real <c>FTK2.Blessings/data/ClassPacks/BLSS_PACK_EOR_BLESSINGS/</c> folder.
        /// The spec's own path shape is "../../data/ClassPacks/BLSS_PACK_EOR_BLESSINGS/" relative to this
        /// project's directory (which is where a plain `dotnet run` from inside the project would put the
        /// working directory) -- but `dotnet run --project &lt;path&gt;` invoked from a DIFFERENT cwd (e.g.
        /// the repo root, which is how the CI/acceptance command in the M2 brief actually runs it) leaves
        /// the working directory at the CALLER's cwd, not the project's. Rather than assume one or the
        /// other, this walks up from both the process working directory and the compiled output directory
        /// looking for the "FTK2.Blessings" folder and resolves the pack path from there -- correct no
        /// matter where `dotnet run`/the built exe is invoked from.
        /// </summary>
        private static string PackDir()
        {
            var found = FindUnderFtk2Blessings(Directory.GetCurrentDirectory())
                ?? FindUnderFtk2Blessings(AppContext.BaseDirectory);
            if (found != null) return found;

            // Last resort: the spec's literal relative path (kept so the failure message below is
            // still informative about what was tried).
            return Path.Combine("..", "..", "data", "ClassPacks", "BLSS_PACK_EOR_BLESSINGS");
        }

        private static string FindUnderFtk2Blessings(string startDir)
        {
            var dir = string.IsNullOrEmpty(startDir) ? null : new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "FTK2.Blessings", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(dir.FullName, "data", "ClassPacks", "BLSS_PACK_EOR_BLESSINGS");
                    if (Directory.Exists(candidate)) return candidate;
                }
                var sibling = Path.Combine(dir.FullName, "FTK2.Blessings", "data", "ClassPacks", "BLSS_PACK_EOR_BLESSINGS");
                if (Directory.Exists(sibling)) return sibling;
                dir = dir.Parent;
            }
            return null;
        }

        private static Blessings.Core.Model.BlessingsRegistry LoadRoster()
        {
            var path = Path.Combine(PackDir(), "blessings.json");
            Check.True(File.Exists(path), "blessings.json must exist at " + Path.GetFullPath(path));
            var json = File.ReadAllText(path);
            var result = BlessingsRegistryParser.Parse(json);
            Check.True(result.Success, "the real blessings.json must parse cleanly: " +
                (result.Findings.Count > 0 ? result.Findings[0].ToString() : "(no findings)"));
            return result.Registry;
        }

        private static JsonDocument LoadTraits()
        {
            var path = Path.Combine(PackDir(), "traits.json");
            Check.True(File.Exists(path), "traits.json must exist at " + Path.GetFullPath(path));
            var json = File.ReadAllText(path);
            return JsonDocument.Parse(json);
        }

        public static void EveryRosterTraitId_ExistsInTraitsJson()
        {
            var registry = LoadRoster();
            using var traits = LoadTraits();

            foreach (var entry in registry.Blessings)
            {
                bool found = traits.RootElement.TryGetProperty(entry.TraitId, out _);
                Check.True(found, "roster entry '" + entry.Id + "' references TraitId '" + entry.TraitId +
                    "' which is not present in traits.json");
            }
        }

        public static void EveryTraitId_MatchesTraitIdRegex()
        {
            var registry = LoadRoster();
            foreach (var entry in registry.Blessings)
                Check.True(TraitIdPattern.IsMatch(entry.TraitId), "TraitId '" + entry.TraitId + "' does not match ^TRAIT_[A-Z0-9_]+$");
        }

        public static void TraitsJson_EveryKey_MatchesTraitIdRegex()
        {
            using var traits = LoadTraits();
            foreach (var prop in traits.RootElement.EnumerateObject())
                Check.True(TraitIdPattern.IsMatch(prop.Name), "traits.json key '" + prop.Name + "' does not match ^TRAIT_[A-Z0-9_]+$");
        }

        public static void RosterWeights_SumTo114()
        {
            var registry = LoadRoster();
            long sum = 0;
            foreach (var entry in registry.Blessings) sum += entry.Weight; // raw authored sum, not Max(1,w) -- the spec asserts the AUTHORED total
            Check.Equal(114L, sum, "blessings.json roster weights must sum to 114 (SPEC §7)");
        }

        public static void ExactlyOneTrait_CarriesPassives()
        {
            using var traits = LoadTraits();
            int passiveCarriers = 0;
            string carrierName = null;
            foreach (var prop in traits.RootElement.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("Equippable", out var equippable) &&
                    equippable.TryGetProperty("Passives", out var passives) &&
                    passives.ValueKind == JsonValueKind.Array &&
                    passives.GetArrayLength() > 0)
                {
                    passiveCarriers++;
                    carrierName = prop.Name;
                }
            }
            Check.Equal(1, passiveCarriers, "exactly one trait must carry a non-empty Passives array (found: " + (carrierName ?? "none") + ")");
            Check.Equal("TRAIT_BLSS_ARCANE_HUNGER", carrierName, "the passive carrier must be ARCANE_HUNGER's trait");
        }

        public static void RosterCount_Is15_AllEnabled()
        {
            var registry = LoadRoster();
            Check.Equal(15, registry.Blessings.Count, "M1 shipped roster has 15 entries");
            foreach (var entry in registry.Blessings)
                Check.True(entry.Enabled, "M1 ships every entry Enabled=true (blessing '" + entry.Id + "')");
        }
    }
}
