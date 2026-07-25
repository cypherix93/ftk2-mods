using System.Collections.Generic;
using System.Linq;
using Summoner.Core.Merge;
using Summoner.Core.Model;
using Summoner.Core.Packs;

namespace Summoner.Core.Tests
{
    public static class MergePlannerTests
    {
        private static FollowerEntry SimpleFollower() => new FollowerEntry
        {
            Type = "COMPANION",
            ConfigName = "LIVE_VANILLA_ID_00",
            ClassName = "x",
            Rarity = "COMMON",
            ContractPrice = "VERY_LOW",
            Behaviour = "DEFAULT",
            MinTier = 0,
            MaxTier = 3,
            Expansion = "BASE"
        };

        private static FollowerPack OnePack(string packId, params string[] followerIds)
        {
            var followers = new Dictionary<string, FollowerEntry>();
            foreach (var id in followerIds) followers[id] = SimpleFollower();
            return new FollowerPack
            {
                Manifest = new PackManifest { Id = packId, Name = packId, Enabled = true, LoadOrder = 100 },
                Followers = followers,
                Characters = new Dictionary<string, CharacterEntry>()
            };
        }

        public static void SkipsIdAlreadyPresentInLiveConfigs()
        {
            var packs = new List<FollowerPack> { OnePack("SMN_PACK_ONE", "SMN_FOL_EXISTS", "SMN_FOL_NEW") };
            var existingFollowerIds = new HashSet<string> { "SMN_FOL_EXISTS" };

            var plan = MergePlanner.Plan(packs, existingFollowerIds, new HashSet<string>());

            Check.DoesNotContain(plan.FollowerAdds, a => a.Id == "SMN_FOL_EXISTS", "an id already present in live Configs must not be re-added (adds-only)");
            Check.Contains(plan.Skips, s => s.Id == "SMN_FOL_EXISTS", "the already-present id must show up as a Skip");
            Check.Contains(plan.FollowerAdds, a => a.Id == "SMN_FOL_NEW", "a genuinely new id must still be added");
        }

        public static void IsIdempotent_SamePlanTwiceIsNoOp()
        {
            var packs = new List<FollowerPack> { OnePack("SMN_PACK_ONE", "SMN_FOL_A", "SMN_FOL_B") };
            var existingFollowerIds = new HashSet<string>();
            var existingCharacterIds = new HashSet<string>();

            var planA1 = MergePlanner.Plan(packs, existingFollowerIds, existingCharacterIds);
            var planA2 = MergePlanner.Plan(packs, existingFollowerIds, existingCharacterIds);

            Check.SequenceEqual(planA1.FollowerAdds.Select(a => a.Id).OrderBy(x => x), planA2.FollowerAdds.Select(a => a.Id).OrderBy(x => x),
                "Plan(packs, ids) called twice with identical inputs must produce the same Adds set");
            Check.Equal(2, planA1.FollowerAdds.Count, "first pass should add both followers");

            // "Apply" plan A1: fold its adds into the existing-id set, exactly what ConfigsSink does to Configs.Followers.
            var appliedIds = new HashSet<string>(existingFollowerIds);
            foreach (var add in planA1.FollowerAdds) appliedIds.Add(add.Id);

            var planB = MergePlanner.Plan(packs, appliedIds, existingCharacterIds);

            Check.Equal(0, planB.FollowerAdds.Count, "re-planning after applying the first plan must add nothing new (idempotent)");
            Check.Equal(2, planB.Skips.Count, "both previously-added ids must now show up as Skips");
        }
    }
}
