using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Runs the real PackLoader over every shipped ClassPack root while supplying the LIVE id sets.
    /// PackLoader already enforces adds-only against those sets, but the in-game plugin is the only caller
    /// that ever had them — the offline pack checker has no Configs to snapshot and must pass null, so this
    /// enforcement path went unexercised outside a running game until now.
    /// </summary>
    public static class ClassForgeChecks
    {
        public static PackLoadResult LoadAll(GameData data)
        {
            return new PackLoader().Load(new FileSystemFileSource(), PackRoots.ClassPackRoots(), null, LiveIds(data));
        }

        /// <summary>Same call shape pointed at one fixture root, so a fixture proves a check bites using the
        /// identical code path the real packs travel.</summary>
        public static PackLoadResult LoadFixture(GameData data, string fixtureRoot)
        {
            return new PackLoader().Load(new FileSystemFileSource(), new[] { fixtureRoot }, null, LiveIds(data));
        }

        private static LiveIdSets LiveIds(GameData data)
        {
            return new LiveIdSets(
                data.Ids("Characters"),
                data.Ids("Things"),
                data.Ids("Abilities"),
                data.Ids("StatusEffects"));
        }

        public static IEnumerable<string> ErrorFindings(PackLoadResult result)
        {
            return result.Findings
                .Where(f => f.Severity == FindingSeverity.Error)
                .Select(f => f.Code + ": " + f.Message);
        }

        public static void RegisterAddsOnly(CheckRunner runner, GameData data, PackLoadResult result)
        {
            runner.Section("ClassForge — pack load against live Configs");

            runner.Case("shipped packs load with zero Error findings", () =>
            {
                // The floor is below the number of packs actually shipped because each manifest's own
                // enabled flag is authoritative here: parking a pack legitimately lowers the count, and
                // that should not turn this check red.
                Check.AtLeast(3, result.EnabledOrderedPacks.Count, "packs merged");
                Check.Empty(ErrorFindings(result), "ClassForge PackLoader Error findings");
            });

            runner.Case("no pack id collides with a live vanilla id", () =>
            {
                var categories = new[]
                {
                    Tuple.Create("Characters", (IEnumerable<string>)result.MergePlan.Characters.Select(o => o.Id)),
                    Tuple.Create("Things",     (IEnumerable<string>)result.MergePlan.Things.Select(o => o.Id)),
                    Tuple.Create("Abilities",  (IEnumerable<string>)result.MergePlan.Abilities.Select(o => o.Id)),
                    Tuple.Create("StatusEffects", (IEnumerable<string>)result.MergePlan.StatusEffects.Select(o => o.Id)),
                };

                var offenders = new List<string>();
                foreach (var pair in categories)
                {
                    var liveSet = data.Ids(pair.Item1);
                    foreach (var id in pair.Item2)
                        if (liveSet.Contains(id))
                            offenders.Add(pair.Item1 + "." + id + " already exists in vanilla Configs");
                }
                Check.Empty(offenders, "pack ids colliding with live Configs ids");
            });

            runner.Case("negative fixture: a vanilla-colliding pack IS refused", () =>
            {
                var fixtureRoot = System.IO.Path.Combine(PackRoots.FixturesDir(), "collide");
                var bad = LoadFixture(data, fixtureRoot);
                Check.True(ErrorFindings(bad).Any(),
                    "fixture pack KNIGHT_COLLIDER ships the vanilla id ALCHEMIST and must produce an Error " +
                    "finding — if this passes silently the adds-only live-id enforcement is not running at all");
            });
        }
    }
}
