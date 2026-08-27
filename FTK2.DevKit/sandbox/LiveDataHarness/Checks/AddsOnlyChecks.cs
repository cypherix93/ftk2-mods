using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Packs are adds-only: no pack id may collide with an id the live game already defines. MergePlanner
    /// enforces this itself when handed a LiveIdSets (emitting CF_LIVE_ID_COLLISION and dropping the id);
    /// this check both asserts that no such finding fired AND re-derives the collision set directly, so a
    /// regression in MergePlanner cannot silently make the check pass.
    ///
    /// A collision is not cosmetic: the colliding id is DROPPED, so an author who edits that file sees no
    /// effect in game whatsoever — the content is never loaded.
    /// </summary>
    public static class AddsOnlyChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            runner.Section("ClassForge — pack load against live Configs");

            runner.Case("every shipped pack loads with zero Error findings", delegate
            {
                // isPackEnabled is null here, so each manifest's own "enabled" flag is authoritative.
                // Parking a pack is a legitimate change — update InventoryChecks' expected pack id list and
                // the README when it happens, do not soften this to a floor.
                Check.AtLeast(1, content.Packs.EnabledOrderedPacks.Count, "packs merged (zero would be vacuous)");
                Check.Empty(AuthoredContent.ErrorFindings(content), "ClassForge PackLoader Error findings");
            });

            runner.Case("the merge plan is non-empty (the checks below are not vacuous)", delegate
            {
                Check.AtLeast(1, content.Packs.MergePlan.Characters.Count, "merged Characters");
                Check.AtLeast(1, content.Packs.MergePlan.Things.Count, "merged Things");
                Check.AtLeast(1, content.Packs.MergePlan.StatusEffects.Count, "merged StatusEffects");
                Check.AtLeast(1, content.Packs.MergePlan.Abilities.Count, "merged Abilities");
            });

            runner.Case("no pack id collides with a live vanilla id", delegate
            {
                List<string> offenders = new List<string>();
                AddCollisions(offenders, data, "Characters", content.Packs.MergePlan.Characters);
                AddCollisions(offenders, data, "Things", content.Packs.MergePlan.Things);
                AddCollisions(offenders, data, "Abilities", content.Packs.MergePlan.Abilities);
                AddCollisions(offenders, data, "StatusEffects", content.Packs.MergePlan.StatusEffects);
                Check.Empty(offenders, "pack ids colliding with live Configs ids");
            });

            runner.Case("NEGATIVE fixture: a vanilla-colliding pack IS refused", delegate
            {
                string fixtureRoot = Path.Combine(PackRoots.FixturesDir(), "collide");
                Check.True(Directory.Exists(fixtureRoot),
                    "fixture root must exist at " + fixtureRoot + " — check the csproj's fixtures\\**\\* copy rule");

                AuthoredContent bad = AuthoredContent.LoadFrom(data, new string[] { fixtureRoot });
                List<string> findings = AuthoredContent.ErrorFindings(bad).ToList();

                Check.True(findings.Any(delegate (string f) { return f.Contains("CF_LIVE_ID_COLLISION"); }),
                    "fixture CF_PACK_HARNESS_COLLIDE (ships live id ALCHEMIST) must produce a " +
                    "CF_LIVE_ID_COLLISION Error — if this passes silently, adds-only enforcement is not " +
                    "actually running. Got: " + (findings.Count == 0 ? "<no errors>" : string.Join(" | ", findings)));

                Check.Exactly(0, bad.Packs.MergePlan.Characters.Count,
                    "the colliding id must be DROPPED from the merge plan, not merely reported");
            });

            runner.Case("NEGATIVE control: the fixture loader is not simply always-failing", delegate
            {
                // Without this, the case above would still pass if LoadFrom errored on every input and
                // merged nothing. Note it deliberately does NOT assert "zero Errors over the real roots" —
                // the real roots currently DO produce collision Errors (see the case above), and a control
                // that duplicates a real finding just doubles the noise without adding signal.
                AuthoredContent good = AuthoredContent.LoadFrom(data, PackRoots.ClassPackRoots());
                Check.AtLeast(1, good.Packs.MergePlan.Characters.Count,
                    "the same LoadFrom call over the real pack roots must still merge content");
                Check.True(!AuthoredContent.ErrorFindings(good)
                        .Any(delegate (string f) { return f.Contains("CF_PACK_HARNESS_COLLIDE"); }),
                    "the fixture's finding must not appear in a real-roots load — the two loads are distinct");
            });
        }

        private static void AddCollisions(List<string> offenders, GameData data, string dictName, List<MergeOp> ops)
        {
            ISet<string> live = data.Ids(dictName);
            for (int i = 0; i < ops.Count; i++)
                if (live.Contains(ops[i].Id))
                    offenders.Add(dictName + "." + ops[i].Id + " (from pack " + ops[i].SourcePackId +
                                  ") already exists in the live Configs");
        }
    }
}
