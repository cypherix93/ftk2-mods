using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Cross-references every id a shipped pack's classes.json points at against the live game data plus the
    /// pack's own merged ids.
    ///
    /// The field map below is measured, not assumed, and three entries are traps worth stating outright:
    ///
    /// CharacterConfig.Passives resolve against Configs.SkillConfigs, not Configs.Abilities — checking
    /// Abilities produces 100 false "dangling" findings. BaseType and DefaultBodyType are plain strings with
    /// a closed de-facto vocabulary rather than dictionary keys, so they are checked against learned value
    /// sets; checking them against Configs.Characters produces 31 more.
    ///
    /// Passives also resolve against skill ids a pack *defines in its own skillrecipes.json*, which is not a
    /// merge category and therefore appears nowhere in the merge plan. Omitting that source reported all 41
    /// of this repo's SKILL_CF_* signature skills as dangling when they are simply recipe-defined rather
    /// than config-defined. Whether such a recipe is well-formed enough to produce a skill at runtime is a
    /// separate concern checked against the recipe validator, so it is deliberately not re-judged here.
    /// </summary>
    public static class ReferenceChecks
    {
        public static void Register(CheckRunner runner, GameData data, GameVocabulary vocab, PackLoadResult result)
        {
            runner.Section("ClassForge — reference integrity vs live Configs");

            runner.Case("every class reference resolves", () =>
                Check.Empty(Scan(data, vocab, result), "dangling references in shipped pack classes.json"));

            runner.Case("negative fixture: a dangling Passive IS caught", () =>
            {
                var fixtureRoot = System.IO.Path.Combine(PackRoots.FixturesDir(), "dangling");
                var bad = ClassForgeChecks.LoadFixture(data, fixtureRoot);
                var offenders = Scan(data, vocab, bad).ToList();
                Check.True(offenders.Any(o => o.Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST")),
                    "fixture CF_HARNESS_DANGLING must be reported; got: " + string.Join(" | ", offenders));
            });
        }

        /// <summary>Returns one human-readable offender line per unresolved reference.</summary>
        public static IEnumerable<string> Scan(GameData data, GameVocabulary vocab, PackLoadResult result)
        {
            var mergedThings = new HashSet<string>(result.MergePlan.Things.Select(o => o.Id), StringComparer.Ordinal);
            var mergedAbilities = new HashSet<string>(result.MergePlan.Abilities.Select(o => o.Id), StringComparer.Ordinal);
            var mergedStatuses = new HashSet<string>(result.MergePlan.StatusEffects.Select(o => o.Id), StringComparer.Ordinal);
            var packIds = new HashSet<string>(result.EnabledOrderedPacks.Select(p => p.Id), StringComparer.Ordinal);

            var liveSkills = data.Ids("SkillConfigs");
            var liveThings = data.Ids("Things");
            var recipeSkills = ShippedRecipeIds();

            var offenders = new List<string>();

            foreach (var op in result.MergePlan.Characters)
            {
                foreach (var passive in op.Value.GetStringArray("Passives"))
                    if (!liveSkills.Contains(passive) && !mergedAbilities.Contains(passive)
                        && !mergedStatuses.Contains(passive) && !recipeSkills.Contains(passive))
                        offenders.Add(op.Id + ".Passives -> " + passive + " (not in Configs.SkillConfigs, merged Abilities/StatusEffects, nor any pack's skillrecipes.json)");

                foreach (var member in op.Value.Get("Things").AsObjectMembers)
                {
                    var thing = member.Key;
                    if (!liveThings.Contains(thing) && !mergedThings.Contains(thing))
                        offenders.Add(op.Id + ".Things -> " + thing + " (not in Configs.Things nor merged Things)");
                }

                var baseType = op.Value.GetString("BaseType");
                if (!string.IsNullOrEmpty(baseType) && !vocab.BaseTypes.Contains(baseType))
                    offenders.Add(op.Id + ".BaseType -> " + baseType + " (not one of the " + vocab.BaseTypes.Count + " BaseType values vanilla uses)");

                var bodyType = op.Value.GetString("DefaultBodyType");
                if (!string.IsNullOrEmpty(bodyType) && !vocab.BodyTypes.Contains(bodyType))
                    offenders.Add(op.Id + ".DefaultBodyType -> " + bodyType + " (expected one of " + string.Join("/", vocab.BodyTypes) + ")");

                foreach (var fieldName in new[] { "Rarity", "Expansion" })
                {
                    var v = op.Value.GetString(fieldName);
                    if (!string.IsNullOrEmpty(v) && !vocab.EnumMembers.Contains(v))
                        offenders.Add(op.Id + "." + fieldName + " -> " + v + " (not an FTK2 enum member)");
                }

                // Packs tag their own content with their pack id, so the loaded pack ids are a legal tag
                // source alongside the vanilla tag vocabulary and the enum members.
                foreach (var tag in op.Value.GetStringArray("Tags"))
                    if (!vocab.CharacterTags.Contains(tag) && !vocab.EnumMembers.Contains(tag) && !packIds.Contains(tag))
                        offenders.Add(op.Id + ".Tags -> " + tag + " (not a vanilla tag, enum member, or loaded pack id)");
            }

            return offenders;
        }

        /// <summary>
        /// Skill ids every shipped pack defines in its own skillrecipes.json. Parsed through the real recipe
        /// parser rather than read as raw JSON keys, so this set cannot drift from what the runtime actually
        /// registers.
        /// </summary>
        private static ISet<string> ShippedRecipeIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in PackRoots.ShippedRecipeFiles())
            {
                var set = ClassForge.Recipes.Parsing.RecipeParser.Parse(System.IO.File.ReadAllText(path));
                foreach (var recipe in set.Ordered) ids.Add(recipe.Id);
            }
            return ids;
        }
    }
}
