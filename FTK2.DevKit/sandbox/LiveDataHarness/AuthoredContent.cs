using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Parsing;

namespace LiveDataHarness
{
    /// <summary>
    /// One pass over every shipped pack, producing the id universe the rest of the harness shares.
    ///
    /// Why the separate recipe pass: ClassForge.Core's ParsedPack/MergePlan carry Classes, Traits, Items,
    /// Abilities, Statuses, Localization, Icons, Portraits and ModifierTables — and NO recipe collection.
    /// The authored SKILL_CF_*/SKILL_BLSS_* recipes exist only in each pack's skillrecipes.json.
    /// Measured 2026-08-23: resolving class/trait Passives against SkillConfigs + MergePlan alone produces
    /// 56 false "dangling" findings (40 class, 16 trait). Building AuthoredRecipeIds here is what removes them.
    ///
    /// GeneratedRecipeIds covers the pair ModifierRecipeGenerator synthesizes per modifiers.json table
    /// (SKILL_CF_ENCMOD_SELECT and its derived SKILL_CF_ENCMOD_APPLY sibling for the shipped table). They are
    /// authored nowhere, so a checker that did not know about them would report the selection id as dangling.
    /// They are DERIVED via the generator's own public helper, never hard-coded.
    /// </summary>
    public sealed class AuthoredContent
    {
        public PackLoadResult Packs { get; private set; }
        public IDictionary<string, ClassForge.Recipes.Model.RecipeSet> RecipeSetsByPackId { get; private set; }
        public ISet<string> AuthoredRecipeIds { get; private set; }
        public ISet<string> GeneratedRecipeIds { get; private set; }
        public ISet<string> AllRecipeIds { get; private set; }
        public ISet<string> PackIds { get; private set; }

        /// <summary>Every shipped ClassPack root, with the LIVE id sets handed to PackLoader — the argument
        /// ClassForge.PackCheck is documented as unable to supply.</summary>
        public static AuthoredContent Load(GameData data)
        {
            return LoadFrom(data, PackRoots.ClassPackRoots());
        }

        /// <summary>Same call shape pointed at an arbitrary root list — used by the fixtures.</summary>
        public static AuthoredContent LoadFrom(GameData data, string[] roots)
        {
            LiveIdSets liveIds = new LiveIdSets(
                data.Ids("Characters"),
                data.Ids("Things"),
                data.Ids("Abilities"),
                data.Ids("StatusEffects"));

            PackLoadResult result = new PackLoader().Load(new FileSystemFileSource(), roots, null, liveIds);

            Dictionary<string, ClassForge.Recipes.Model.RecipeSet> sets =
                new Dictionary<string, ClassForge.Recipes.Model.RecipeSet>(StringComparer.Ordinal);
            HashSet<string> authored = new HashSet<string>(StringComparer.Ordinal);

            foreach (PackManifest manifest in result.EnabledOrderedPacks)
            {
                string path = Path.Combine(manifest.RootDir, "skillrecipes.json");
                if (!File.Exists(path)) continue;

                ClassForge.Recipes.Model.RecipeSet set = RecipeParser.Parse(File.ReadAllText(path));
                RecipeValidator.Validate(set);
                sets[manifest.Id] = set;

                // Every parsed id counts as authored, including one the validator disabled: a Passives entry
                // pointing at a disabled recipe is a validator finding, not a dangling reference, and
                // reporting it twice under two different names helps nobody.
                foreach (ClassForge.Recipes.Model.SkillRecipe recipe in set.Ordered)
                    authored.Add(recipe.Id);
            }

            HashSet<string> generated = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModifierTable table in result.MergePlan.ModifierTables)
            {
                if (string.IsNullOrEmpty(table.SelectionRecipe)) continue;
                generated.Add(table.SelectionRecipe);
                string applyId = ModifierRecipeGenerator.DeriveApplyId(table.SelectionRecipe);
                if (!string.IsNullOrEmpty(applyId)) generated.Add(applyId);
            }

            HashSet<string> all = new HashSet<string>(authored, StringComparer.Ordinal);
            all.UnionWith(generated);

            AuthoredContent content = new AuthoredContent();
            content.Packs = result;
            content.RecipeSetsByPackId = sets;
            content.AuthoredRecipeIds = authored;
            content.GeneratedRecipeIds = generated;
            content.AllRecipeIds = all;
            content.PackIds = new HashSet<string>(
                result.EnabledOrderedPacks.Select(delegate (PackManifest m) { return m.Id; }),
                StringComparer.Ordinal);
            return content;
        }

        /// <summary>Error-severity findings from the ClassForge load, as one line each.</summary>
        public static IEnumerable<string> ErrorFindings(AuthoredContent content)
        {
            return content.Packs.Findings
                .Where(delegate (Finding f) { return f.Severity == FindingSeverity.Error; })
                .Select(delegate (Finding f) { return f.Code + " [" + (f.PackId ?? "-") + "]: " + f.Message; });
        }
    }
}
