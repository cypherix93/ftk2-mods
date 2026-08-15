using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Every shipped skillrecipes.json parses and validates clean, and every StatusEffect id a recipe applies
    /// resolves against live Configs.StatusEffects plus the packs' own merged statuses. The offline pack
    /// checker already parses and validates recipes; what it cannot do — and what this adds — is confirm the
    /// status ids those recipes reference actually exist in the game.
    /// </summary>
    public static class RecipeChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult result)
        {
            runner.Section("ClassForge — recipes vs live Configs");

            var files = PackRoots.ShippedRecipeFiles().ToList();

            runner.Case("every shipped skillrecipes.json parses and validates clean", () =>
            {
                Check.AtLeast(3, files.Count, "shipped skillrecipes.json files found");
                var offenders = new List<string>();
                foreach (var path in files)
                {
                    var set = RecipeParser.Parse(File.ReadAllText(path));
                    RecipeValidator.Validate(set);
                    if (set.HasErrors)
                        foreach (var f in set.Findings.Where(x => x.Severity == ClassForge.Recipes.Model.FindingSeverity.Error))
                            offenders.Add(PackNameOf(path) + ": " + f);
                }
                Check.Empty(offenders, "recipe validation errors");
            });

            runner.Case("every referenced StatusEffect id resolves", () =>
            {
                var resolvable = new HashSet<string>(data.Ids("StatusEffects"), StringComparer.Ordinal);
                foreach (var op in result.MergePlan.StatusEffects) resolvable.Add(op.Id);

                var offenders = new List<string>();
                foreach (var path in files)
                {
                    var set = RecipeParser.Parse(File.ReadAllText(path));
                    RecipeValidator.Validate(set);
                    foreach (var pair in ReferencedStatusIds(set))
                        if (!resolvable.Contains(pair.Value))
                            offenders.Add(PackNameOf(path) + " / " + pair.Key + " -> status '" + pair.Value + "' not in Configs.StatusEffects nor merged statuses");
                }
                Check.Empty(offenders, "recipes referencing unknown StatusEffect ids");
            });

            runner.Case("modifier tables generate valid recipes", () =>
            {
                var offenders = new List<string>();
                foreach (var table in result.MergePlan.ModifierTables)
                {
                    var input = new ModifierTableInput { SelectionRecipeId = table.SelectionRecipe };
                    foreach (var m in table.Modifiers)
                        input.Modifiers.Add(new ModifierRow { Id = m.Id, Weight = m.Weight, Status = m.Status, MaxHpPercent = m.MaxHpPercent });

                    var generated = ModifierRecipeGenerator.Generate(input);
                    if (generated == null) { offenders.Add(table.PackId + "/" + table.SelectionRecipe + ": generator returned null"); continue; }

                    var genSet = new RecipeSet();
                    genSet.Add(generated.Select);
                    genSet.Add(generated.Apply);
                    RecipeValidator.Validate(genSet);
                    if (genSet.HasErrors)
                        foreach (var f in genSet.Findings.Where(x => x.Severity == ClassForge.Recipes.Model.FindingSeverity.Error))
                            offenders.Add(table.PackId + ": " + f);
                }
                Check.Empty(offenders, "generated encounter-modifier recipe errors");
            });

            runner.Case("negative control: an invented status id would not resolve", () =>
            {
                var resolvable = new HashSet<string>(data.Ids("StatusEffects"), StringComparer.Ordinal);
                foreach (var op in result.MergePlan.StatusEffects) resolvable.Add(op.Id);
                Check.True(!resolvable.Contains("STATUS_CF_HARNESS_NEVER_SHIPPED"),
                    "the resolvable-status set must not contain an invented id — otherwise the reference check is vacuous");
            });
        }

        private static string PackNameOf(string recipePath)
        {
            return Path.GetFileName(Path.GetDirectoryName(recipePath));
        }

        /// <summary>
        /// recipeId -> status id, walked from the parsed effect model rather than from raw JSON, so this
        /// cannot drift from what the parser actually understands. Only live recipes are walked: one the
        /// validator already disabled cannot reference anything at runtime. TRIGGER_STATUS is a sentinel
        /// meaning "whatever status the trigger carried" and is not a content id.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> ReferencedStatusIds(RecipeSet set)
        {
            foreach (var recipe in set.Ordered)
            {
                if (!recipe.IsLive) continue;
                foreach (var effect in recipe.Effects)
                {
                    foreach (var s in DirectStatuses(effect))
                    {
                        if (string.IsNullOrEmpty(s)) continue;
                        if (string.Equals(s, Vocabulary.TriggerStatusToken, StringComparison.Ordinal)) continue;
                        yield return new KeyValuePair<string, string>(recipe.Id, s);
                    }
                }
            }
        }

        private static IEnumerable<string> DirectStatuses(RecipeEffect effect)
        {
            yield return effect.Status;
            yield return effect.FallbackStatus;

            if (effect.StatusOneOf != null)
                foreach (var s in effect.StatusOneOf) yield return s;

            // StatusFromSelection names a runtime selection slot rather than a status id, so it is skipped;
            // the ids it can actually resolve to are exactly the table's StatusId column.
            if (effect.StatusFromSelectionTable != null)
                foreach (var row in effect.StatusFromSelectionTable) yield return row.StatusId;
        }
    }
}
