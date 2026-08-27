using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Model;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Every shipped skillrecipes.json validates clean, every StatusEffect id a recipe applies resolves
    /// against live Configs.StatusEffects plus the packs' own merged statuses, and every modifiers.json
    /// table still round-trips through the engine-owned generator.
    ///
    /// The recipe sets are the ones AuthoredContent already parsed and validated, so this file cannot drift
    /// from the id universe the reference checker used.
    ///
    /// This is the check that catches a status id which is not a real Configs.StatusEffects KEY — the
    /// failure mode where an effect silently does nothing in game and nothing anywhere says so.
    /// </summary>
    public static class RecipeChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            runner.Section("ClassForge — recipes vs live Configs");

            runner.Case("every shipped skillrecipes.json validates clean", delegate
            {
                Check.AtLeast(1, content.RecipeSetsByPackId.Count,
                    "packs with a skillrecipes.json (zero would make this vacuous)");

                List<string> offenders = new List<string>();
                foreach (KeyValuePair<string, RecipeSet> entry in content.RecipeSetsByPackId)
                    foreach (ClassForge.Recipes.Model.Finding f in entry.Value.Findings)
                        if (f.Severity == ClassForge.Recipes.Model.FindingSeverity.Error)
                            offenders.Add(entry.Key + ": " + f.ToString());
                Check.Empty(offenders, "recipe validation errors");
            });

            runner.Case("every referenced StatusEffect id resolves", delegate
            {
                HashSet<string> resolvable = new HashSet<string>(data.Ids("StatusEffects"), StringComparer.Ordinal);
                foreach (MergeOp op in content.Packs.MergePlan.StatusEffects) resolvable.Add(op.Id);

                int inspected = 0;
                List<string> offenders = new List<string>();
                foreach (KeyValuePair<string, RecipeSet> entry in content.RecipeSetsByPackId)
                {
                    foreach (KeyValuePair<string, string> pair in ReferencedStatusIds(entry.Value))
                    {
                        inspected++;
                        if (!resolvable.Contains(pair.Value))
                            offenders.Add(entry.Key + " / " + pair.Key + " -> status '" + pair.Value +
                                          "' is neither in Configs.StatusEffects nor a merged pack status " +
                                          "(the effect will silently do nothing in game)");
                    }
                }

                Check.AtLeast(20, inspected,
                    "status references inspected (32 measured) — zero would make this check vacuous");
                Check.Empty(offenders, "recipes referencing unknown StatusEffect ids");
            });

            runner.Case("every modifier table generates a valid recipe pair", delegate
            {
                Check.AtLeast(1, content.Packs.MergePlan.ModifierTables.Count, "modifier tables");

                List<string> offenders = new List<string>();
                foreach (ModifierTable table in content.Packs.MergePlan.ModifierTables)
                {
                    ModifierTableInput input = new ModifierTableInput();
                    input.SelectionRecipeId = table.SelectionRecipe;
                    foreach (ModifierEntry m in table.Modifiers)
                    {
                        ModifierRow row = new ModifierRow();
                        row.Id = m.Id;
                        row.Weight = m.Weight;
                        row.Status = m.Status;
                        row.MaxHpPercent = m.MaxHpPercent;
                        input.Modifiers.Add(row);
                    }

                    GeneratedModifierRecipes generated = ModifierRecipeGenerator.Generate(input);
                    if (generated == null || generated.Select == null || generated.Apply == null)
                    {
                        offenders.Add(table.PackId + "/" + table.SelectionRecipe + ": generator returned nothing usable");
                        continue;
                    }

                    RecipeSet genSet = new RecipeSet();
                    genSet.Add(generated.Select);
                    genSet.Add(generated.Apply);
                    ClassForge.Recipes.Parsing.RecipeValidator.Validate(genSet);
                    foreach (ClassForge.Recipes.Model.Finding f in genSet.Findings)
                        if (f.Severity == ClassForge.Recipes.Model.FindingSeverity.Error)
                            offenders.Add(table.PackId + ": " + f.ToString());
                }
                Check.Empty(offenders, "generated encounter-modifier recipe errors");
            });

            runner.Case("NEGATIVE control: TRIGGER_STATUS is excluded, an invented id is not", delegate
            {
                // Two ways this check family could go silently wrong: treating the TRIGGER_STATUS sentinel
                // as a content id (false positives on every recipe that uses it), or resolving anything at
                // all (false negatives everywhere).
                HashSet<string> resolvable = new HashSet<string>(data.Ids("StatusEffects"), StringComparer.Ordinal);
                Check.True(!resolvable.Contains(Vocabulary.TriggerStatusToken),
                    "TRIGGER_STATUS must not be a live status id — it is a sentinel, and ReferencedStatusIds skips it");
                Check.True(!resolvable.Contains("STATUS_LDH_NEVER_SHIPPED"),
                    "the resolvable status set must reject an invented id");
                Check.True(resolvable.Contains("STATUS_CURSE_00"),
                    "...and accept a verified real one (STATUS_CURSE_00 measured present)");

                RecipeSet probe = new RecipeSet();
                SkillRecipe fake = new SkillRecipe();
                fake.Id = "SKILL_LDH_PROBE";
                RecipeEffect effect = new RecipeEffect();
                effect.Status = "STATUS_LDH_NEVER_SHIPPED";
                fake.Effects.Add(effect);
                probe.Add(fake);
                Check.True(ReferencedStatusIds(probe).Any(delegate (KeyValuePair<string, string> p)
                        { return p.Value == "STATUS_LDH_NEVER_SHIPPED"; }),
                    "ReferencedStatusIds must surface a plain Status field — if it returns nothing, the " +
                    "resolution check above inspects nothing and passes vacuously");
            });
        }

        /// <summary>
        /// recipeId -&gt; status id, walked from the parsed effect model, never from raw JSON (that would
        /// re-implement the parser and drift from it). Only live recipes are walked: one the validator
        /// already disabled cannot reference anything at runtime. TRIGGER_STATUS is a sentinel meaning
        /// "whatever status the trigger carried" and is skipped — treating it as a content id yields a false
        /// positive on every recipe that uses it. StatusFromSelection names a CombatRuntime selection slot,
        /// not a status, so it is skipped too; the status ids it can produce are exactly
        /// StatusFromSelectionTable's StatusId column, which IS walked.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> ReferencedStatusIds(RecipeSet set)
        {
            foreach (SkillRecipe recipe in set.Ordered)
            {
                if (!recipe.IsLive) continue;
                foreach (RecipeEffect effect in recipe.Effects)
                {
                    foreach (string s in DirectStatuses(effect))
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
                foreach (string s in effect.StatusOneOf) yield return s;

            if (effect.StatusFromSelectionTable != null)
                foreach (SelectionStatusEntry row in effect.StatusFromSelectionTable) yield return row.StatusId;
        }
    }
}
