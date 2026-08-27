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
    /// skipped, never silently overwritten. Measured 2026-08-23: Configs.Followers = 48 with zero SMN_ ids,
    /// Configs.Characters = 2126; the two shipped packs contribute 100 + 100 = 200 followers and zero
    /// characters (neither pack ships a characters.json), so CharacterAdds is legitimately 0.
    /// </summary>
    public static class SummonerChecks
    {
        public static void Register(CheckRunner runner, GameData data)
        {
            runner.Section("Summoner — follower packs vs live Configs");

            string root = PackRoots.FollowerPackRoot();

            runner.Case("both follower packs load", delegate
            {
                Check.True(root != null, "FTK2.Summoner/data/FollowerPacks exists");
                LoadResult loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                Check.Exactly(2, loaded.Packs.Count,
                    "follower packs loaded (SMN_PACK_EOR_MERCS + SMN_PACK_EOR_PETS). Zero here means the " +
                    "IJsonCodec dropped the camelCase manifests — see HarnessJsonCodec's remarks.");
                Check.Empty(loaded.Findings
                        .Where(delegate (Summoner.Core.Diagnostics.Finding f) { return f.Severity == Summoner.Core.Diagnostics.FindingSeverity.Error; })
                        .Select(delegate (Summoner.Core.Diagnostics.Finding f) { return f.ToString(); }),
                    "Summoner PackLoader Error findings");
            });

            runner.Case("no follower or character id collides with live Configs", delegate
            {
                LoadResult loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                MergePlan plan = MergePlanner.Plan(loaded.Packs, data.Ids("Followers"), data.Ids("Characters"));

                Check.AtLeast(1, plan.FollowerAdds.Count,
                    "planned follower adds — zero adds would make the collision assertion below vacuous");

                // MergePlan.Skips is exactly "entries not added because the id already exists in live
                // Configs" (Summoner design §A2.3), so a non-empty Skips list is a pack shipping content
                // the game already has.
                IEnumerable<string> offenders = plan.Skips
                    .Select(delegate (MergeSkip s) { return "skip " + s.PackId + "/" + s.Id + ": " + s.Reason; })
                    .Concat(plan.Rejects.Select(delegate (Summoner.Core.Diagnostics.Finding f) { return "reject: " + f.ToString(); }));
                Check.Empty(offenders, "Summoner entries refused against live Configs");
            });

            runner.Case("NEGATIVE control: the live Followers set is real and finite", delegate
            {
                Check.AtLeast(40, data.Ids("Followers").Count, "Configs.Followers (48 measured)");
                Check.True(!data.Ids("Followers").Contains("SMN_HARNESS_NEVER_SHIPPED"),
                    "live Followers must not contain an invented id — otherwise the collision check is vacuous");
            });

            runner.Case("NEGATIVE control: a synthetic pre-existing id IS skipped", delegate
            {
                // Prove MergePlanner's skip path actually runs, by telling it one of the packs' own ids is
                // already live. Without this, "0 skips" could mean "adds-only enforcement never fired".
                LoadResult loaded = PackLoader.Load(new HarnessPackSource(), new HarnessJsonCodec(), root);
                Check.AtLeast(1, loaded.Packs.Count, "at least one pack to draw an id from");

                MergePlan baseline = MergePlanner.Plan(loaded.Packs, data.Ids("Followers"), data.Ids("Characters"));

                string victim = loaded.Packs[0].Followers.Keys.OrderBy(delegate (string k) { return k; }, StringComparer.Ordinal).First();
                HashSet<string> pretendLive = new HashSet<string>(data.Ids("Followers"), StringComparer.Ordinal);
                pretendLive.Add(victim);

                MergePlan plan = MergePlanner.Plan(loaded.Packs, pretendLive, data.Ids("Characters"));
                Check.True(plan.Skips.Any(delegate (MergeSkip s) { return string.Equals(s.Id, victim, StringComparison.Ordinal); }),
                    "MergePlanner must skip '" + victim + "' once it is present in the live follower ids");
                Check.Exactly(baseline.FollowerAdds.Count - 1, plan.FollowerAdds.Count, "adds after one id is skipped");
            });
        }
    }
}
