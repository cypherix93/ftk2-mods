using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveDataHarness
{
    /// <summary>One live Configs dictionary and the count vanilla alone contributes to it.</summary>
    public sealed class DictBaseline
    {
        public string Dictionary;
        public int VanillaCount;
    }

    /// <summary>One content pack this install is EXPECTED to carry on disk, identified by the id prefix it
    /// writes into a named Configs dictionary.</summary>
    public sealed class ExpectedPack
    {
        public string Label;
        public string Prefix;
        public string Dictionary;
        public int RecordedCount;
    }

    /// <summary>
    /// The provenance gate, inverted for this install (spec §8).
    ///
    /// The live install is deliberately contaminated and that is CORRECT: Enhanced Overhaul Revamped
    /// (sirpepperpot.enhanced-overhaul-revamped 0.7.0.62) writes 31 EOR_* classes into
    /// StreamingAssets\...\Configs\JSON~\Characters.json, and this repo's own FTK2.Armory writes 533 ARM_*
    /// Things across seven files under Configs\JSON~\Things\. The owner's co-op saves depend on that
    /// content. Steam's "Verify integrity of game files" DELETES it, so this class never recommends it.
    ///
    /// What is asserted instead, measured 2026-08-23:
    ///   1. every expected pack is PRESENT      (Error when absent  — the Steam-verify-wiped-it failure)
    ///   2. every live id is ACCOUNTED FOR      (Error on a surplus — an unrecorded third mod)
    ///   3. no repo-authored id is ON DISK      (Error when found   — a repo pack baked into StreamingAssets
    ///                                           would make every adds-only check measure itself)
    ///   4. expected-pack counts match record   (Warning on drift   — a legitimate dependency update)
    ///
    /// Rule 2 is arithmetic (live == vanilla baseline + recorded pack contributions), so it catches an
    /// unknown modder without needing to know their id prefix. It stays green under rule-4 drift because it
    /// counts the prefix hits actually present, not the recorded number.
    ///
    /// Every method below is PURE over a (dictionary name -> id set) snapshot. That is what lets the three
    /// Error paths be proven with synthetic snapshots instead of by damaging the real install.
    /// </summary>
    public static class InstallProvenance
    {
        /// <summary>Vanilla-only counts. Measured by subtracting each dictionary's recorded pack
        /// contribution from the live count on 2026-08-23: Characters 2126-31=2095, Things 2380-533=1847;
        /// the rest carry no recorded pack content and were read directly.</summary>
        public static readonly DictBaseline[] Baselines =
        {
            new DictBaseline { Dictionary = "Characters",    VanillaCount = 2095 },
            new DictBaseline { Dictionary = "Things",        VanillaCount = 1847 },
            new DictBaseline { Dictionary = "Abilities",     VanillaCount = 992  },
            new DictBaseline { Dictionary = "SkillConfigs",  VanillaCount = 68   },
            new DictBaseline { Dictionary = "StatusEffects", VanillaCount = 189  },
            new DictBaseline { Dictionary = "Followers",     VanillaCount = 48   },
        };

        /// <summary>Content this install is expected to carry on disk. Absence is an Error, not a pass.</summary>
        public static readonly ExpectedPack[] Expected =
        {
            new ExpectedPack {
                Label = "Enhanced Overhaul Revamped (third-party, sirpepperpot.enhanced-overhaul-revamped; the co-op saves depend on it)",
                Prefix = "EOR_", Dictionary = "Characters", RecordedCount = 31 },
            new ExpectedPack {
                Label = "FTK2.Armory (this repo, deployed as seven ARM_*.json files under Configs\\JSON~\\Things)",
                Prefix = "ARM_", Dictionary = "Things", RecordedCount = 533 },
        };

        /// <summary>Prefixes owned by this repo's runtime-applied packs. These are merged into Configs by
        /// their plugins at load time and must NEVER be found written into StreamingAssets — if one is, the
        /// adds-only checks are comparing pack content against itself.</summary>
        public static readonly string[] RepoAuthoredPrefixes = { "CF_", "BLSS_", "SMN_" };

        /// <summary>Adapter from the live game to the pure functions below.</summary>
        public static IDictionary<string, ISet<string>> Snapshot(GameData data)
        {
            Dictionary<string, ISet<string>> snapshot =
                new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < Baselines.Length; i++)
                snapshot[Baselines[i].Dictionary] = data.Ids(Baselines[i].Dictionary);
            return snapshot;
        }

        /// <summary>Rule 1 — an expected pack contributing zero ids has been wiped off the disk.</summary>
        public static List<string> MissingExpected(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> offenders = new List<string>();
            for (int i = 0; i < Expected.Length; i++)
            {
                ExpectedPack pack = Expected[i];
                int found = CountWithPrefix(snapshot, pack.Dictionary, pack.Prefix);
                if (found == 0)
                    offenders.Add("EXPECTED CONTENT MISSING: no '" + pack.Prefix + "' ids in Configs." +
                                  pack.Dictionary + " — " + pack.Label +
                                  ". Recorded contribution was " + pack.RecordedCount.ToString(CultureInfo.InvariantCulture) +
                                  ". Restore the mod's files; do NOT run Steam's 'Verify integrity of game files'.");
            }
            return offenders;
        }

        /// <summary>Rule 2 — live count must equal vanilla baseline plus the prefix hits actually present.
        /// Any residual is content nobody recorded.</summary>
        public static List<string> Unaccounted(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> offenders = new List<string>();
            for (int i = 0; i < Baselines.Length; i++)
            {
                DictBaseline baseline = Baselines[i];
                ISet<string> ids;
                if (!snapshot.TryGetValue(baseline.Dictionary, out ids) || ids == null) continue;

                int accounted = baseline.VanillaCount;
                for (int p = 0; p < Expected.Length; p++)
                    if (string.Equals(Expected[p].Dictionary, baseline.Dictionary, StringComparison.Ordinal))
                        accounted += CountWithPrefix(snapshot, baseline.Dictionary, Expected[p].Prefix);

                int delta = ids.Count - accounted;
                if (delta > 0)
                    offenders.Add("UNACCOUNTED CONTENT: Configs." + baseline.Dictionary + " holds " +
                                  ids.Count.ToString(CultureInfo.InvariantCulture) + " ids but only " +
                                  accounted.ToString(CultureInfo.InvariantCulture) +
                                  " are accounted for (vanilla " + baseline.VanillaCount.ToString(CultureInfo.InvariantCulture) +
                                  " + recorded packs) — " + delta.ToString(CultureInfo.InvariantCulture) +
                                  " surplus id(s) from an unrecorded source. Identify it and add it to " +
                                  "InstallProvenance.Expected, or remove it.");
                else if (delta < 0)
                    offenders.Add("CONTENT SHORTFALL: Configs." + baseline.Dictionary + " holds " +
                                  ids.Count.ToString(CultureInfo.InvariantCulture) + " ids, " +
                                  (-delta).ToString(CultureInfo.InvariantCulture) +
                                  " fewer than the recorded baseline of " + accounted.ToString(CultureInfo.InvariantCulture) +
                                  ". Vanilla content is missing — the install has been damaged.");
            }
            return offenders;
        }

        /// <summary>Rule 3 — a repo-authored id found in the live Configs means a pack was baked onto disk
        /// instead of applied at runtime.</summary>
        public static List<string> RepoIdsOnDisk(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> offenders = new List<string>();
            foreach (KeyValuePair<string, ISet<string>> entry in snapshot)
            {
                if (entry.Value == null) continue;
                foreach (string id in entry.Value)
                {
                    for (int p = 0; p < RepoAuthoredPrefixes.Length; p++)
                    {
                        if (!id.StartsWith(RepoAuthoredPrefixes[p], StringComparison.Ordinal)) continue;
                        offenders.Add("REPO CONTENT ON DISK: Configs." + entry.Key + "." + id +
                                      " carries repo prefix '" + RepoAuthoredPrefixes[p] +
                                      "'. Repo packs are applied at runtime by their plugins and must never " +
                                      "be written into StreamingAssets — while one is, every adds-only check " +
                                      "is comparing the pack against itself.");
                        break;
                    }
                }
            }
            return offenders;
        }

        /// <summary>Rule 4 — a recorded pack whose id count moved. A dependency update is legitimate, so
        /// this is a Warning; it exists so the recorded numbers get refreshed on evidence.</summary>
        public static List<string> VersionDrift(IDictionary<string, ISet<string>> snapshot)
        {
            List<string> notes = new List<string>();
            for (int i = 0; i < Expected.Length; i++)
            {
                ExpectedPack pack = Expected[i];
                int found = CountWithPrefix(snapshot, pack.Dictionary, pack.Prefix);
                if (found != 0 && found != pack.RecordedCount)
                    notes.Add(pack.Prefix + " in Configs." + pack.Dictionary + ": " +
                              found.ToString(CultureInfo.InvariantCulture) + " ids, recorded " +
                              pack.RecordedCount.ToString(CultureInfo.InvariantCulture) +
                              " — likely a pack update (" + pack.Label +
                              "). Update ExpectedPack.RecordedCount and the README baseline table.");
            }
            return notes;
        }

        private static int CountWithPrefix(IDictionary<string, ISet<string>> snapshot, string dictName, string prefix)
        {
            ISet<string> ids;
            if (!snapshot.TryGetValue(dictName, out ids) || ids == null) return 0;
            int n = 0;
            foreach (string id in ids)
                if (id.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
        }

        public static void Register(CheckRunner runner, GameData data)
        {
            IDictionary<string, ISet<string>> snapshot = Snapshot(data);

            runner.Section("Install provenance (expected-content gate)");

            runner.Case("every expected content pack is present on disk", delegate
            {
                Check.Empty(MissingExpected(snapshot), "expected content packs missing from the install");
            });

            runner.Case("every live id is accounted for by vanilla or a recorded pack", delegate
            {
                Check.Empty(Unaccounted(snapshot), "unaccounted-for content in the live Configs");
            });

            runner.Case("no repo-authored id is written into the game's StreamingAssets", delegate
            {
                Check.Empty(RepoIdsOnDisk(snapshot), "repo pack content found baked into the game folder");
            });

            runner.Warn("expected-pack version drift", VersionDrift(snapshot));

            // ---- negative controls -------------------------------------------------------------
            // The real install must not be mutated to prove these fire, so each runs the same pure
            // function over a synthetic snapshot. Without them all three checks above could be
            // permanently, silently green.

            runner.Case("NEGATIVE: a wiped expected pack IS reported", delegate
            {
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                RemovePrefix(fake, "Characters", "EOR_");
                List<string> hits = MissingExpected(fake);
                Check.AtLeast(1, hits.Count, "MissingExpected must fire when EOR_ content is gone");
                Check.True(hits[0].Contains("EOR_"), "the finding must name the missing prefix; got: " + hits[0]);
            });

            runner.Case("NEGATIVE: an unrecorded third mod IS reported", delegate
            {
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                fake["Characters"].Add("XMOD_INTRUDER_00");
                List<string> hits = Unaccounted(fake);
                Check.AtLeast(1, hits.Count, "Unaccounted must fire on a surplus id");
                Check.True(hits[0].Contains("Characters"), "the finding must name the dictionary; got: " + hits[0]);
            });

            runner.Case("NEGATIVE: a repo pack baked onto disk IS reported", delegate
            {
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                fake["Characters"].Add("CF_EOR_BARD");
                List<string> hits = RepoIdsOnDisk(fake);
                Check.AtLeast(1, hits.Count, "RepoIdsOnDisk must fire on a CF_ id in the live Configs");
                Check.True(hits[0].Contains("CF_EOR_BARD"), "the finding must name the id; got: " + hits[0]);
            });

            runner.Case("NEGATIVE: a clean synthetic baseline produces no findings", delegate
            {
                // Without this, all three controls above would still pass if the functions returned a
                // finding for literally every input.
                IDictionary<string, ISet<string>> fake = SyntheticBaseline();
                Check.Empty(MissingExpected(fake), "MissingExpected on a clean synthetic baseline");
                Check.Empty(Unaccounted(fake), "Unaccounted on a clean synthetic baseline");
                Check.Empty(RepoIdsOnDisk(fake), "RepoIdsOnDisk on a clean synthetic baseline");
                Check.Empty(VersionDrift(fake), "VersionDrift on a clean synthetic baseline");
            });
        }

        /// <summary>A synthetic snapshot matching the recorded baseline exactly: for each dictionary,
        /// VanillaCount placeholder ids plus RecordedCount prefixed ids for each expected pack. Used only by
        /// the negative controls — it never touches the game.</summary>
        private static IDictionary<string, ISet<string>> SyntheticBaseline()
        {
            Dictionary<string, ISet<string>> fake = new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < Baselines.Length; i++)
            {
                DictBaseline b = Baselines[i];
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                for (int n = 0; n < b.VanillaCount; n++)
                    ids.Add("VANILLA_" + b.Dictionary + "_" + n.ToString(CultureInfo.InvariantCulture));
                fake[b.Dictionary] = ids;
            }
            for (int p = 0; p < Expected.Length; p++)
            {
                ExpectedPack pack = Expected[p];
                ISet<string> ids = fake[pack.Dictionary];
                for (int n = 0; n < pack.RecordedCount; n++)
                    ids.Add(pack.Prefix + "SYNTH_" + n.ToString(CultureInfo.InvariantCulture));
            }
            return fake;
        }

        private static void RemovePrefix(IDictionary<string, ISet<string>> snapshot, string dictName, string prefix)
        {
            ISet<string> ids;
            if (!snapshot.TryGetValue(dictName, out ids) || ids == null) return;
            List<string> doomed = new List<string>();
            foreach (string id in ids)
                if (id.StartsWith(prefix, StringComparison.Ordinal)) doomed.Add(id);
            for (int i = 0; i < doomed.Count; i++) ids.Remove(doomed[i]);
        }
    }
}
