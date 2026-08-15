using System;
using System.Collections.Generic;
using System.Linq;
using LiveDataHarness.Io;
using Summoner.Core.Merge;
using Summoner.Core.Packs;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Summoner is adds-only: a follower pack entry whose id already exists in the live Configs must be
    /// skipped rather than silently overwriting shipped content. The planner expresses that as a Skip, so a
    /// non-empty Skips list means a pack is shipping something the game already has.
    /// </summary>
    public static class SummonerChecks
    {
        public static void Register(CheckRunner runner, GameData data)
        {
            runner.Section("Summoner — follower packs vs live Configs");

            var root = PackRoots.FollowerPackRoot();

            runner.Case("follower packs load", () =>
            {
                Check.True(root != null, "the repo's FollowerPacks directory was found");
                var loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                Check.AtLeast(2, loaded.Packs.Count, "follower packs loaded");
            });

            runner.Case("no follower or character id collides with live Configs", () =>
            {
                var loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                var plan = MergePlanner.Plan(loaded.Packs, data.Ids("Followers"), data.Ids("Characters"));

                var offenders = plan.Skips
                    .Select(s => s.PackId + "/" + s.Id + ": " + s.Reason)
                    .Concat(plan.Rejects.Select(f => "reject: " + f));
                Check.Empty(offenders, "Summoner entries refused against live Configs");

                // Zero planned adds would make the assertion above trivially true, which is the failure mode
                // this floor exists to catch — a path-semantics mistake that loads no packs at all.
                Check.AtLeast(1, plan.FollowerAdds.Count, "follower adds planned");
            });

            runner.Case("negative control: live Followers set is real", () =>
            {
                Check.AtLeast(10, data.Ids("Followers").Count, "Configs.Followers");
                Check.True(!data.Ids("Followers").Contains("SMN_HARNESS_NEVER_SHIPPED"),
                    "live Followers must not contain an invented id — otherwise the collision check is vacuous");
            });
        }
    }
}
