using System.Collections.Generic;
using Blessings.Core.Model;
using Blessings.Core.Resolution;

namespace Blessings.Core.Tests
{
    public static class ResolverTests
    {
        /// <summary>Small synthetic roster (not the real 15-entry pack -- that's covered separately by
        /// RosterPackCrossCheckTests): A(w=1) B(w=2) C(w=3), all enabled. Sum(Max(1,w)) = 6.</summary>
        private static BlessingsRegistry SmallRoster()
        {
            var entries = new List<BlessingEntry>
            {
                new BlessingEntry("BLSS_A", "TRAIT_BLSS_A", 1, true, new List<string>()),
                new BlessingEntry("BLSS_B", "TRAIT_BLSS_B", 2, true, new List<string>()),
                new BlessingEntry("BLSS_C", "TRAIT_BLSS_C", 3, true, new List<string>())
            };
            return new BlessingsRegistry("1.0", entries);
        }

        // ---- §8.2 item 2: golden resolution tests ------------------------------------------------
        // Expected ids computed once by running BlessingResolver.ComputeOfferHash/WalkWeighted against
        // this exact roster (see the PowerShell/bash SHA-256 cross-check performed during
        // implementation) and frozen here, per the spec's own instruction ("compute expected values by
        // running your implementation once and freezing them, then assert stability").

        public static void Golden_Seed1_GoldenCfg_PicksA()
        {
            var result = BlessingResolver.Resolve(SmallRoster(), "Random", 1, "GOLDEN_CFG");
            Check.Equal(ResolutionKind.Random, result.Kind, "kind");
            Check.Equal("BLSS_A", result.Resolved.Id, "golden pick for (seed=1, GOLDEN_CFG)");
        }

        public static void Golden_Seed42_AdvTest_PicksC()
        {
            var result = BlessingResolver.Resolve(SmallRoster(), "Random", 42, "ADV_TEST");
            Check.Equal(ResolutionKind.Random, result.Kind, "kind");
            Check.Equal("BLSS_C", result.Resolved.Id, "golden pick for (seed=42, ADV_TEST)");
        }

        public static void Golden_SeedNegative5_EmptyCfg_PicksB()
        {
            var result = BlessingResolver.Resolve(SmallRoster(), "Random", -5, "EMPTY_CFG");
            Check.Equal(ResolutionKind.Random, result.Kind, "kind");
            Check.Equal("BLSS_B", result.Resolved.Id, "golden pick for (seed=-5, EMPTY_CFG)");
        }

        public static void Random_SameSeedAndConfig_IsDeterministicAcrossCalls()
        {
            var r1 = BlessingResolver.Resolve(SmallRoster(), "Random", 42, "ADV_TEST");
            var r2 = BlessingResolver.Resolve(SmallRoster(), "Random", 42, "ADV_TEST");
            Check.Equal(r1.Resolved.Id, r2.Resolved.Id, "same (seed, config) must resolve to the same blessing every time");
        }

        // ---- weight-walk boundary cases -----------------------------------------------------------

        public static void WalkWeighted_RollAtExactBucketEdge_PicksThatEntry()
        {
            var entries = SmallRoster().Blessings; // A=1,B=2,C=3 -> buckets [1,1] [2,3] [4,6]
            Check.Equal("BLSS_A", BlessingResolver.WalkWeighted(entries, 1).Id, "roll=1 is A's only slot");
            Check.Equal("BLSS_B", BlessingResolver.WalkWeighted(entries, 2).Id, "roll=2 is B's first slot");
            Check.Equal("BLSS_B", BlessingResolver.WalkWeighted(entries, 3).Id, "roll=3 is B's last slot");
            Check.Equal("BLSS_C", BlessingResolver.WalkWeighted(entries, 4).Id, "roll=4 is C's first slot");
            Check.Equal("BLSS_C", BlessingResolver.WalkWeighted(entries, 6).Id, "roll=6 is C's last slot (== SumWeights)");
        }

        public static void WalkWeighted_WeightLEZero_ClampsToOneSlot()
        {
            var entries = new List<BlessingEntry>
            {
                new BlessingEntry("BLSS_ZERO", "TRAIT_BLSS_ZERO", 0, true, new List<string>()),
                new BlessingEntry("BLSS_NEG", "TRAIT_BLSS_NEG", -7, true, new List<string>()),
                new BlessingEntry("BLSS_TWO", "TRAIT_BLSS_TWO", 2, true, new List<string>())
            };
            // Max(1,0)=1, Max(1,-7)=1, Max(1,2)=2 -> sum=4, buckets [1,1] [2,2] [3,4]
            Check.Equal(4, BlessingResolver.SumWeights(entries), "SumWeights applies Max(1,Weight) per entry");
            Check.Equal("BLSS_ZERO", BlessingResolver.WalkWeighted(entries, 1).Id, "roll=1 -> the zero-weight entry, clamped to a 1-slot bucket");
            Check.Equal("BLSS_NEG", BlessingResolver.WalkWeighted(entries, 2).Id, "roll=2 -> the negative-weight entry, clamped to a 1-slot bucket");
            Check.Equal("BLSS_TWO", BlessingResolver.WalkWeighted(entries, 4).Id, "roll=4 -> the positive-weight entry's last slot");
        }

        public static void DisablingEntries_ChangesSumWeights_Deterministically()
        {
            var full = SmallRoster();
            Check.Equal(6, BlessingResolver.SumWeights(BlessingResolver.EnabledInAuthoredOrder(full)), "full roster sum");

            var partial = new BlessingsRegistry("1.0", new List<BlessingEntry>
            {
                new BlessingEntry("BLSS_A", "TRAIT_BLSS_A", 1, true, new List<string>()),
                new BlessingEntry("BLSS_B", "TRAIT_BLSS_B", 2, false, new List<string>()), // disabled
                new BlessingEntry("BLSS_C", "TRAIT_BLSS_C", 3, true, new List<string>())
            });
            var enabled = BlessingResolver.EnabledInAuthoredOrder(partial);
            Check.Equal(2, enabled.Count, "disabled entry drops out of the candidate list");
            Check.Equal(4, BlessingResolver.SumWeights(enabled), "sum shrinks by the disabled entry's Max(1,Weight)");
            Check.SequenceEqual(new[] { "BLSS_A", "BLSS_C" }, enabled.ConvertAll(e => e.Id), "authored order preserved with the gap closed");
        }

        // ---- Mode resolution: Disabled / unknown-id / disabled-literal handling -------------------

        public static void Mode_Disabled_ResolvesToNoBlessing()
        {
            var result = BlessingResolver.Resolve(SmallRoster(), "Disabled", 1, "X");
            Check.Equal(ResolutionKind.Disabled, result.Kind, "kind");
            Check.True(result.Resolved == null, "no blessing resolved");
        }

        public static void Mode_Empty_TreatedAsDisabled()
        {
            var result = BlessingResolver.Resolve(SmallRoster(), "", 1, "X");
            Check.Equal(ResolutionKind.Disabled, result.Kind, "empty Mode treated as Disabled");
        }

        public static void Mode_LiteralKnownEnabledId_Resolves()
        {
            var result = BlessingResolver.Resolve(SmallRoster(), "BLSS_B", 1, "X");
            Check.Equal(ResolutionKind.Literal, result.Kind, "kind");
            Check.Equal("BLSS_B", result.Resolved.Id, "resolves to the named blessing");
        }

        public static void Mode_LiteralUnknownId_TreatedAsDisabled_NeverGuesses()
        {
            var result = BlessingResolver.Resolve(SmallRoster(), "BLSS_DOES_NOT_EXIST", 1, "X");
            Check.Equal(ResolutionKind.Disabled, result.Kind, "unknown id falls back to Disabled");
            Check.True(result.Resolved == null, "no blessing resolved");
            Check.True(!string.IsNullOrEmpty(result.Message), "explains why");
        }

        public static void Mode_LiteralDisabledRosterEntry_TreatedAsDisabled()
        {
            var registry = new BlessingsRegistry("1.0", new List<BlessingEntry>
            {
                new BlessingEntry("BLSS_PARKED", "TRAIT_BLSS_PARKED", 10, false, new List<string>())
            });
            var result = BlessingResolver.Resolve(registry, "BLSS_PARKED", 1, "X");
            Check.Equal(ResolutionKind.Disabled, result.Kind, "a literal reference to a disabled roster entry never grants it");
        }

        public static void Mode_Random_NoEnabledCandidates_TreatedAsDisabled()
        {
            var registry = new BlessingsRegistry("1.0", new List<BlessingEntry>
            {
                new BlessingEntry("BLSS_OFF", "TRAIT_BLSS_OFF", 10, false, new List<string>())
            });
            var result = BlessingResolver.Resolve(registry, "Random", 1, "X");
            Check.Equal(ResolutionKind.Disabled, result.Kind, "Random with an empty candidate set never throws, degrades to Disabled");
        }

        public static void Mode_NullRegistry_TreatedAsDisabled()
        {
            var result = BlessingResolver.Resolve(null, "Random", 1, "X");
            Check.Equal(ResolutionKind.Disabled, result.Kind, "a null registry fails closed to Disabled, never throws");
        }
    }
}
