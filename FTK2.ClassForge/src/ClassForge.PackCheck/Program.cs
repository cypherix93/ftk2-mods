using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;

namespace ClassForge.PackCheck
{
    /// <summary>
    /// Console verification tool for a single ClassForge pack directory (or several, one per arg).
    /// <para>For each pack directory given: loads it through <see cref="ClassForge.Core.PackLoader"/> (real
    /// classes/traits/items/abilities/localization/icons/portraits parsing — SPEC.md §3/§4), then reads
    /// <c>skillrecipes.json</c> directly (Core does not own that file) and runs it through
    /// <see cref="ClassForge.Recipes.Parsing.RecipeParser"/> + validator (SPEC-DELTA-v1.1). Prints per-file
    /// counts and every <c>Finding</c> from both layers. Exit code 0 only when zero Errors were produced
    /// anywhere (Warnings are always allowed — e.g. CF_PACK_BALDURS's known CF_TRAIT_ prefix warning).</para>
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var packDirs = new List<string>();
            if (args != null && args.Length > 0)
            {
                packDirs.AddRange(args);
            }
            else
            {
                // Convenience default: the packs this tool is chartered to check, resolved relative to the
                // repo layout (FTK2.ClassForge/data/ClassPacks/<id>), searched upward from the executable.
                var eor = FindDefaultPack("CF_PACK_EOR_CLASSES");
                var baldurs = FindDefaultPack("CF_PACK_BALDURS");
                var encounterModifiers = FindDefaultPack("CF_PACK_ENCOUNTER_MODIFIERS");
                if (eor != null) packDirs.Add(eor);
                if (baldurs != null) packDirs.Add(baldurs);
                if (encounterModifiers != null) packDirs.Add(encounterModifiers);
            }

            if (packDirs.Count == 0)
            {
                Console.WriteLine("ClassForge.PackCheck");
                Console.WriteLine("Usage: ClassForge.PackCheck <packDir> [<packDir> ...]");
                Console.WriteLine("  <packDir> = absolute path to a folder containing pack.json");
                Console.WriteLine("  (no args and no default packs found under the repo layout)");
                return 1;
            }

            var fs = new FileSystemFileSource();
            bool anyErrors = false;

            foreach (var packDir in packDirs)
            {
                bool ok = CheckOnePack(fs, packDir);
                if (!ok) anyErrors = true;
            }

            Console.WriteLine();
            Console.WriteLine(anyErrors ? "PackCheck: FAILED (one or more packs have Errors)" : "PackCheck: ALL PACKS OK (zero Errors)");
            return anyErrors ? 1 : 0;
        }

        private static bool CheckOnePack(FileSystemFileSource fs, string packDir)
        {
            Console.WriteLine();
            Console.WriteLine("==================================================================");
            Console.WriteLine("Pack directory: " + packDir);
            Console.WriteLine("==================================================================");

            string fullPackDir;
            try
            {
                fullPackDir = Path.GetFullPath(packDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: invalid path: " + ex.Message);
                return false;
            }

            if (!Directory.Exists(fullPackDir))
            {
                Console.WriteLine("ERROR: directory does not exist.");
                return false;
            }

            var trimmed = fullPackDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetDirectoryName(trimmed);
            var expectedId = Path.GetFileName(trimmed);

            if (string.IsNullOrEmpty(root))
            {
                Console.WriteLine("ERROR: could not determine the ClassPacks root (parent directory) for '" + packDir + "'.");
                return false;
            }

            bool packHasErrors = false;

            var result = new PackLoader().Load(fs, new[] { root }, id => string.Equals(id, expectedId, StringComparison.Ordinal));

            Console.WriteLine("Discovered pack.json files under root: " + result.DiscoveredPacks.Count);
            Console.WriteLine("Loaded (merged) packs: " + Join(result.EnabledOrderedPacks.Select(p => p.Id)));
            if (result.SkippedPacks.Count > 0)
                Console.WriteLine("Skipped packs: " + Join(result.SkippedPacks.Select(p => p.Id)));

            if (!result.EnabledOrderedPacks.Any(p => string.Equals(p.Id, expectedId, StringComparison.Ordinal)))
            {
                Console.WriteLine("ERROR: expected pack id '" + expectedId + "' (from directory name) was not loaded.");
                packHasErrors = true;
            }

            Console.WriteLine();
            Console.WriteLine("Classes:            " + result.MergePlan.Characters.Count);
            Console.WriteLine("Traits (Class=TRAIT): " + result.MergePlan.TraitIds.Count);
            Console.WriteLine("Things total (traits+items): " + result.MergePlan.Things.Count);
            Console.WriteLine("Abilities:          " + result.MergePlan.Abilities.Count);
            Console.WriteLine("StatusEffects:      " + result.MergePlan.StatusEffects.Count);
            Console.WriteLine("Localization keys:  " + result.MergePlan.Localization.Count);
            Console.WriteLine("Icons:              " + result.MergePlan.Icons.Count);
            Console.WriteLine("Portraits:          " + result.MergePlan.Portraits.Count);
            var modifierRowCount = result.MergePlan.ModifierTables.Sum(t => t.Modifiers.Count);
            Console.WriteLine("Modifier entries:   " + modifierRowCount + " (across " + result.MergePlan.ModifierTables.Count + " modifiers.json table(s))");

            Console.WriteLine();
            Console.WriteLine("-- Core Findings (" + result.Findings.Count + ") --");
            foreach (var f in result.Findings)
            {
                Console.WriteLine(FormatCoreFinding(f));
                if (f.Severity == ClassForge.Core.FindingSeverity.Error) packHasErrors = true;
            }
            if (result.Findings.Count == 0) Console.WriteLine("(none)");

            // skillrecipes.json is not owned by ClassForge.Core — parse/validate it directly here.
            var recipesPath = Path.Combine(fullPackDir, "skillrecipes.json");
            Console.WriteLine();
            if (File.Exists(recipesPath))
            {
                var json = File.ReadAllText(recipesPath);
                var set = RecipeParser.Parse(json);

                Console.WriteLine("Recipes:            " + set.Ordered.Count);
                Console.WriteLine();
                Console.WriteLine("-- Recipe Findings (" + set.Findings.Count + ") --");
                foreach (var f in set.Findings)
                    Console.WriteLine(FormatRecipeFinding(f));
                if (set.Findings.Count == 0) Console.WriteLine("(none)");

                if (set.HasErrors) packHasErrors = true;
            }
            else
            {
                Console.WriteLine("Recipes:            0 (no skillrecipes.json present)");
            }

            // M-EM3 — engine-generated encounter-modifier recipes (Encounter Modifiers spec §6.1). Exercises
            // the SAME generator RecipeEngineHost.LoadBook calls, against this pack's REAL modifiers.json
            // (not a test fixture copy) end to end through ClassForge.Core's own parse/merge.
            if (result.MergePlan.ModifierTables.Count > 0)
            {
                Console.WriteLine();
                for (int ti = 0; ti < result.MergePlan.ModifierTables.Count; ti++)
                {
                    var table = result.MergePlan.ModifierTables[ti];
                    var input = new ModifierTableInput { SelectionRecipeId = table.SelectionRecipe };
                    for (int i = 0; i < table.Modifiers.Count; i++)
                    {
                        var m = table.Modifiers[i];
                        input.Modifiers.Add(new ModifierRow { Id = m.Id, Weight = m.Weight, Status = m.Status, MaxHpPercent = m.MaxHpPercent });
                    }

                    var generated = ModifierRecipeGenerator.Generate(input);
                    if (generated == null)
                    {
                        Console.WriteLine("-- Generated recipes (table '" + table.SelectionRecipe + "') --");
                        Console.WriteLine("ERROR: generation returned null (empty/malformed table) -- ships inert.");
                        packHasErrors = true;
                        continue;
                    }

                    var genSet = new RecipeSet();
                    genSet.Add(generated.Select);
                    genSet.Add(generated.Apply);
                    RecipeValidator.Validate(genSet);

                    Console.WriteLine("-- Generated recipes (from '" + table.SelectionRecipe + "', selection name '" + generated.SelectionName + "') --");
                    Console.WriteLine("Generated recipe ids: " + generated.Select.Id + ", " + generated.Apply.Id);
                    Console.WriteLine("Generated Findings (" + genSet.Findings.Count + "):");
                    foreach (var f in genSet.Findings)
                        Console.WriteLine(FormatRecipeFinding(f));
                    if (genSet.Findings.Count == 0) Console.WriteLine("(none)");
                    if (genSet.HasErrors) packHasErrors = true;
                }
            }

            Console.WriteLine();
            Console.WriteLine(packHasErrors
                ? "RESULT: " + expectedId + " -- FAILED (Errors present)"
                : "RESULT: " + expectedId + " -- OK (zero Errors)");

            return !packHasErrors;
        }

        private static string FormatCoreFinding(ClassForge.Core.Finding f)
        {
            return "[" + f.Severity + "] " + f.Code + ": " + f.Message +
                   (f.PackId != null ? " (pack=" + f.PackId + ")" : "");
        }

        private static string FormatRecipeFinding(ClassForge.Recipes.Model.Finding f)
        {
            return f.ToString();
        }

        private static string Join(IEnumerable<string> items)
        {
            var list = items.ToList();
            return list.Count == 0 ? "(none)" : string.Join(", ", list);
        }

        private static string FindDefaultPack(string packId)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "FTK2.ClassForge", "data", "ClassPacks", packId);
                if (Directory.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            return null;
        }
    }
}
