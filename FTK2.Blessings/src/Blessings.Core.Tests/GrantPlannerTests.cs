using System.Collections.Generic;
using Blessings.Core.Resolution;

namespace Blessings.Core.Tests
{
    public static class GrantPlannerTests
    {
        public static void Plan_OrdersByAscendingOrdinalGuid_RegardlessOfInputOrder()
        {
            var guids = new List<string> { "guid-c", "guid-a", "guid-b" };
            var plan = GrantPlanner.Plan(guids, new Dictionary<string, bool>());
            Check.SequenceEqual(new[] { "guid-a", "guid-b", "guid-c" }, plan.ConvertAll(p => p.EntityGuid), "ascending ordinal order");
        }

        public static void Plan_MembersWithoutTrait_ShouldGrant()
        {
            var guids = new List<string> { "guid-1", "guid-2" };
            var plan = GrantPlanner.Plan(guids, new Dictionary<string, bool>());
            Check.True(plan[0].ShouldGrant, "guid-1 should grant (no prior state)");
            Check.True(plan[1].ShouldGrant, "guid-2 should grant (no prior state)");
        }

        public static void Plan_MembersAlreadyHoldingTrait_AreSkipped()
        {
            var guids = new List<string> { "guid-1", "guid-2" };
            var already = new Dictionary<string, bool> { { "guid-1", true } };
            var plan = GrantPlanner.Plan(guids, already);
            Check.False(plan[0].ShouldGrant, "guid-1 already has the trait -- skip");
            Check.True(plan[1].ShouldGrant, "guid-2 does not -- grant");
        }

        /// <summary>§8.2 item 3: double-invoke at the anchor grants once. Simulates the plugin's real
        /// flow: plan, "apply" (flip the flag for every ShouldGrant entry), plan again -- the second
        /// plan must show nothing left to grant.</summary>
        public static void Plan_DoubleInvoke_SecondPlanGrantsNothing()
        {
            var guids = new List<string> { "guid-1", "guid-2", "guid-3" };
            var state = new Dictionary<string, bool>();

            var first = GrantPlanner.Plan(guids, state);
            int firstGrantCount = 0;
            foreach (var entry in first)
            {
                if (entry.ShouldGrant) { state[entry.EntityGuid] = true; firstGrantCount++; }
            }
            Check.Equal(3, firstGrantCount, "first plan grants every party member");

            var second = GrantPlanner.Plan(guids, state);
            foreach (var entry in second)
                Check.False(entry.ShouldGrant, "second plan must grant nothing -- idempotent (" + entry.EntityGuid + ")");
        }

        /// <summary>Save-load simulation (§8.2 item 3): trait already present (from save data), latch
        /// state irrelevant to GrantPlanner (source of truth is the trait presence flag the caller
        /// supplies) -- plan must grant nothing.</summary>
        public static void Plan_TraitAlreadyPresentFromSave_GrantsNothing()
        {
            var guids = new List<string> { "guid-1" };
            var already = new Dictionary<string, bool> { { "guid-1", true } };
            var plan = GrantPlanner.Plan(guids, already);
            Check.False(plan[0].ShouldGrant, "a save-restored trait must not be re-granted");
        }

        public static void Plan_IgnoresNullOrEmptyGuids()
        {
            var guids = new List<string> { "guid-1", null, "", "guid-2" };
            var plan = GrantPlanner.Plan(guids, new Dictionary<string, bool>());
            Check.Equal(2, plan.Count, "null/empty guids are dropped, not planned");
        }

        public static void LatchStatKey_MatchesEorPrefixConvention()
        {
            Check.Equal("BLSS_ACTIVE_BLSS_BLOOD_PRICE", GrantPlanner.LatchStatKey("BLSS_BLOOD_PRICE"), "latch key shape");
        }
    }
}
