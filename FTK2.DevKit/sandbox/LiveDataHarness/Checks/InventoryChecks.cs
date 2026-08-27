using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>One pack's expected contribution, so a drop-out names the pack rather than a global total.</summary>
    public sealed class PackInventory
    {
        public string PackId;
        public int Characters;
        public int Things;
        public int StatusEffects;
        public int Abilities;
        public int Recipes;
    }

    /// <summary>
    /// The authored inventory, enforced per pack. These are exact equalities on purpose: a floor would let a
    /// pack vanish from the load without a sound, which is exactly how CF_PACK_BALDURS stayed undeployed and
    /// unnoticed. When content is legitimately added or removed, update this table, docs/research/
    /// class-test-matrix.md and the plan's ground-truth table together — in that order.
    ///
    /// The per-pack split (rather than the plan's four global totals) is deliberate: the plan was written
    /// when four packs shipped, and CF_PACK_ARMORY_VISUALS and CF_PACK_ORIGINALS have since been added.
    /// A global total cannot say WHICH pack moved; this table can.
    /// </summary>
    public static class InventoryChecks
    {
        /// <summary>
        /// Measured against the repo's shipped packs on 2026-08-25, NOT copied from the plan: the plan's
        /// ground-truth table (35 classes / 41 traits / 60 recipes over four packs) predates
        /// CF_PACK_ARMORY_VISUALS and CF_PACK_ORIGINALS and no longer matches the repo. It also recorded
        /// CF_PACK_EOR_CLASSES at 20 Things / 52 recipes where the pack now ships 21 / 56.
        ///
        /// CF_PACK_ORIGINALS is under active authoring — its row is the one most likely to need refreshing,
        /// and a mismatch there is a content change to confirm, not necessarily a bug. The run prints the
        /// measured numbers above the check so refreshing this table never needs a guess.
        /// </summary>
        public static readonly PackInventory[] Expected =
        {
            new PackInventory { PackId = "BLSS_PACK_EOR_BLESSINGS",     Characters = 0,  Things = 15, StatusEffects = 0,  Abilities = 0,  Recipes = 1 },
            new PackInventory { PackId = "CF_PACK_ARMORY_VISUALS",      Characters = 0,  Things = 0,  StatusEffects = 0,  Abilities = 0,  Recipes = 0 },
            new PackInventory { PackId = "CF_PACK_BALDURS",             Characters = 4,  Things = 10, StatusEffects = 0,  Abilities = 0,  Recipes = 5 },
            new PackInventory { PackId = "CF_PACK_ENCOUNTER_MODIFIERS", Characters = 0,  Things = 0,  StatusEffects = 10, Abilities = 0,  Recipes = 2 },
            new PackInventory { PackId = "CF_PACK_EOR_CLASSES",         Characters = 31, Things = 21, StatusEffects = 0,  Abilities = 0,  Recipes = 56 },
            new PackInventory { PackId = "CF_PACK_ORIGINALS",           Characters = 17, Things = 24, StatusEffects = 5,  Abilities = 12, Recipes = 41 },
        };

        public static void Register(CheckRunner runner, AuthoredContent content)
        {
            MergePlan plan = content.Packs.MergePlan;

            runner.Section("Authored inventory (per pack)");

            PrintMeasured(content);

            runner.Case("every expected pack is loaded, by id", delegate
            {
                string[] expected = Expected.Select(delegate (PackInventory p) { return p.PackId; })
                                            .OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal).ToArray();
                List<string> loaded = content.PackIds.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal).ToList();
                Check.Eq(string.Join(",", expected), string.Join(",", loaded), "loaded pack ids");
            });

            runner.Case("each pack contributes exactly the content it is recorded as contributing", delegate
            {
                Check.AtLeast(1, plan.Characters.Count, "merged Characters (zero would be vacuous)");

                Dictionary<string, PackInventory> actual = Measure(content);
                List<string> offenders = new List<string>();
                for (int i = 0; i < Expected.Length; i++)
                {
                    PackInventory want = Expected[i];
                    PackInventory got;
                    if (!actual.TryGetValue(want.PackId, out got))
                    {
                        offenders.Add(want.PackId + ": contributed nothing at all — the pack failed to load " +
                                      "or was parked (check its pack.json 'enabled' flag and that it still sits " +
                                      "under a PackRoots.ClassPackRoots() root)");
                        continue;
                    }
                    Diff(offenders, want.PackId, "Characters", want.Characters, got.Characters);
                    Diff(offenders, want.PackId, "Things", want.Things, got.Things);
                    Diff(offenders, want.PackId, "StatusEffects", want.StatusEffects, got.StatusEffects);
                    Diff(offenders, want.PackId, "Abilities", want.Abilities, got.Abilities);
                    Diff(offenders, want.PackId, "Recipes", want.Recipes, got.Recipes);
                }
                Check.Empty(offenders, "packs whose contribution differs from the recorded inventory");
            });

            runner.Case("the PLAYER tag actually partitions the authored classes", delegate
            {
                // The plan asserted exactly one non-PLAYER class (CF_SKELETON_WARRIOR, a summon:
                // Tags ["CF_PACK_BALDURS","CF_SUMMON"]). CF_PACK_ORIGINALS has since added more non-PLAYER
                // classes, so an exact list is a moving target owned by another pack. What stays checkable:
                // the split is non-degenerate in both directions, and the known summon is on the right side.
                List<string> nonPlayers = NonPlayerClasses(plan);
                Check.AtLeast(1, nonPlayers.Count, "non-PLAYER authored classes");
                Check.AtLeast(1, plan.Characters.Count - nonPlayers.Count, "PLAYER-tagged authored classes");
                Check.True(nonPlayers.Contains("CF_SKELETON_WARRIOR"),
                    "CF_SKELETON_WARRIOR is a CF_SUMMON and must not carry the PLAYER tag");
            });

            runner.Warn("authored classes without the PLAYER tag (not selectable at class-select)",
                NonPlayerClasses(plan).Select(delegate (string id) { return id; }));

            runner.Case("modifier tables generate the recipe pair they are recorded as generating", delegate
            {
                Check.Exactly(1, plan.ModifierTables.Count, "modifier tables");
                Check.Exactly(10, plan.ModifierTables[0].Modifiers.Count, "encounter modifiers");
                Check.Exactly(2, content.GeneratedRecipeIds.Count,
                    "engine-synthesized recipes (SKILL_CF_ENCMOD_SELECT + its derived APPLY sibling)");
            });

            runner.Warn("authored recipes defined but consumed by nothing", Unconsumed(content));

            runner.Case("NEGATIVE control: the inventory is derived, not hard-coded", delegate
            {
                // Every count above would also pass if the check compared a constant against itself. Prove
                // the numbers come from the loaded data by asserting relationships no constant satisfies.
                Dictionary<string, PackInventory> actual = Measure(content);
                Check.Exactly(plan.Characters.Count,
                    actual.Values.Sum(delegate (PackInventory p) { return p.Characters; }),
                    "per-pack Characters must sum to the merge plan's total");
                Check.Exactly(plan.Things.Count,
                    actual.Values.Sum(delegate (PackInventory p) { return p.Things; }),
                    "per-pack Things must sum to the merge plan's total");
                Check.True(content.AllRecipeIds.Count == content.AuthoredRecipeIds.Count + content.GeneratedRecipeIds.Count,
                    "the combined recipe universe must be the union of the authored and generated sets " +
                    "(a non-empty intersection would mean a pack hand-authored an engine-owned id)");
                Check.True(!content.PackIds.Contains("CF_PACK_HARNESS_COLLIDE"),
                    "the fixtures must never be discovered by the real pack roots");
            });
        }

        private static List<string> NonPlayerClasses(MergePlan plan)
        {
            List<string> nonPlayers = new List<string>();
            foreach (MergeOp op in plan.Characters)
                if (!op.Value.GetStringArray("Tags").Contains("PLAYER")) nonPlayers.Add(op.Id);
            nonPlayers.Sort(StringComparer.Ordinal);
            return nonPlayers;
        }

        /// <summary>Prints what was actually measured, so refreshing the table above never needs a guess.</summary>
        private static void PrintMeasured(AuthoredContent content)
        {
            Dictionary<string, PackInventory> actual = Measure(content);
            Console.WriteLine("  measured contribution per pack (characters/things/statuses/abilities/recipes):");
            foreach (string id in actual.Keys.OrderBy(delegate (string s) { return s; }, StringComparer.Ordinal))
            {
                PackInventory p = actual[id];
                Console.WriteLine("    " + id + ": " +
                    p.Characters.ToString(CultureInfo.InvariantCulture) + "/" +
                    p.Things.ToString(CultureInfo.InvariantCulture) + "/" +
                    p.StatusEffects.ToString(CultureInfo.InvariantCulture) + "/" +
                    p.Abilities.ToString(CultureInfo.InvariantCulture) + "/" +
                    p.Recipes.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void Diff(List<string> offenders, string packId, string what, int want, int got)
        {
            if (want == got) return;
            offenders.Add(packId + "." + what + ": recorded " + want.ToString(CultureInfo.InvariantCulture) +
                          ", loaded " + got.ToString(CultureInfo.InvariantCulture));
        }

        private static Dictionary<string, PackInventory> Measure(AuthoredContent content)
        {
            Dictionary<string, PackInventory> byPack = new Dictionary<string, PackInventory>(StringComparer.Ordinal);
            foreach (string id in content.PackIds) byPack[id] = new PackInventory { PackId = id };

            MergePlan plan = content.Packs.MergePlan;
            foreach (MergeOp op in plan.Characters) Row(byPack, op.SourcePackId).Characters++;
            foreach (MergeOp op in plan.Things) Row(byPack, op.SourcePackId).Things++;
            foreach (MergeOp op in plan.StatusEffects) Row(byPack, op.SourcePackId).StatusEffects++;
            foreach (MergeOp op in plan.Abilities) Row(byPack, op.SourcePackId).Abilities++;

            foreach (KeyValuePair<string, ClassForge.Recipes.Model.RecipeSet> entry in content.RecipeSetsByPackId)
                Row(byPack, entry.Key).Recipes = entry.Value.Ordered.Count;

            return byPack;
        }

        private static PackInventory Row(Dictionary<string, PackInventory> byPack, string packId)
        {
            string key = packId ?? "<unknown>";
            PackInventory row;
            if (!byPack.TryGetValue(key, out row))
            {
                row = new PackInventory { PackId = key };
                byPack[key] = row;
            }
            return row;
        }

        /// <summary>Authored recipes no class, trait, item or status Passives array points at. Reported as a
        /// Warning, never a failure — an unwired recipe is dead weight, not a break.</summary>
        private static IEnumerable<string> Unconsumed(AuthoredContent content)
        {
            MergePlan plan = content.Packs.MergePlan;
            HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);

            foreach (MergeOp op in plan.Characters)
                consumed.UnionWith(op.Value.GetStringArray("Passives"));

            foreach (MergeOp op in plan.Things)
            {
                consumed.UnionWith(op.Value.GetStringArray("Passives"));
                ClassForge.Core.Json.JsonValue eq = op.Value.Get("Equippable");
                if (!eq.IsNull) consumed.UnionWith(eq.GetStringArray("Passives"));
            }

            foreach (MergeOp op in plan.StatusEffects)
                consumed.UnionWith(op.Value.GetStringArray("Passives"));

            return content.AuthoredRecipeIds
                .Where(delegate (string id) { return !consumed.Contains(id); })
                .Select(delegate (string id) { return id + " is authored but no Passives array references it"; });
        }
    }
}
