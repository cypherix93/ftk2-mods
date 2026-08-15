using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClassForge.Core;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The statmodifiers.json registry (conditional-stat-modifier spec §2.2, M-CS2). Rebuilt wholesale on
    /// every pack merge, mirroring <see cref="RecipeEngineHost.LoadBook"/>: later pack wins on a duplicate
    /// id (the same last-pack-wins rule the content merge and the recipe book use), and a modifier that
    /// validated with Errors is carried but never applied (fail-safe).
    ///
    /// <para>Ownership binding happens here, at load time, not at read time: a modifier id appearing in a
    /// class's <c>Passives</c> makes that class an owner; appearing in a Thing's <c>Equippable.Passives</c>
    /// makes that Thing (a trait) an owner. The read path then only has to compare
    /// <c>component.ConfigName</c> / scan <c>component.Things</c> — the same per-read cost class EOR ships
    /// in production (EOR62 <c>HasSelectedTrait</c>, Plugin.cs L24607).</para>
    /// </summary>
    internal static class StatModifierHost
    {
        internal sealed class Entry
        {
            public StatModifier Modifier;
            public List<string> ClassOwners = new List<string>();
            public List<string> TraitOwners = new List<string>();
        }

        internal static StatModifierSet Book = new StatModifierSet();

        /// <summary>stat key → entries targeting that stat. Replaced wholesale by <see cref="LoadBook"/>.</summary>
        internal static Dictionary<string, List<Entry>> ByStat =
            new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

        internal static void LoadBook(PackLoadResult result)
        {
            var book = new StatModifierSet();
            var byStat = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            int files = 0, live = 0, errors = 0, warnings = 0;

            try
            {
                // Last-pack-wins on a duplicate id, matching the content merge and the recipe book.
                var byId = new Dictionary<string, StatModifier>(StringComparer.Ordinal);
                var order = new List<string>();

                for (int i = 0; i < result.EnabledOrderedPacks.Count; i++)
                {
                    var pack = result.EnabledOrderedPacks[i];
                    if (pack == null || string.IsNullOrEmpty(pack.RootDir)) continue;

                    string path = Path.Combine(pack.RootDir, "statmodifiers.json");
                    if (!File.Exists(path)) continue;

                    string json;
                    try { json = File.ReadAllText(path); }
                    catch (Exception ex)
                    {
                        errors++;
                        ClassForgePlugin.Log.LogError(
                            "[ClassForge] Could not read '" + path + "' (pack '" + pack.Id + "') — its stat modifiers are skipped: " + ex.Message);
                        continue;
                    }

                    files++;
                    var parsed = StatModifierParser.Parse(json);

                    for (int f = 0; f < parsed.Findings.Count; f++)
                    {
                        var finding = parsed.Findings[f];
                        if (finding.Severity == ClassForge.Recipes.Model.FindingSeverity.Error)
                        {
                            errors++;
                            ClassForgePlugin.Log.LogError("[ClassForge] statmodifiers.json (pack '" + pack.Id + "'): " + finding);
                        }
                        else
                        {
                            warnings++;
                            ClassForgePlugin.Log.LogWarning("[ClassForge] statmodifiers.json (pack '" + pack.Id + "'): " + finding);
                        }
                    }

                    var ordered = parsed.Ordered;
                    for (int m = 0; m < ordered.Count; m++)
                    {
                        var modifier = ordered[m];
                        if (modifier == null || string.IsNullOrEmpty(modifier.Id)) continue;
                        if (byId.ContainsKey(modifier.Id))
                        {
                            ClassForgePlugin.Log.LogWarning(
                                "[ClassForge] Stat modifier '" + modifier.Id + "' redefined by pack '" + pack.Id +
                                "' — later pack wins (same rule as the content merge).");
                        }
                        else
                        {
                            order.Add(modifier.Id);
                        }
                        byId[modifier.Id] = modifier;
                    }
                }

                for (int i = 0; i < order.Count; i++)
                {
                    var modifier = byId[order[i]];
                    book.Add(modifier);
                    if (!modifier.IsLive) continue;
                    live++;

                    var entry = new Entry { Modifier = modifier };
                    BindOwners(result, modifier.Id, entry);
                    if (entry.ClassOwners.Count == 0 && entry.TraitOwners.Count == 0)
                    {
                        warnings++;
                        ClassForgePlugin.Log.LogWarning(
                            "[ClassForge] Stat modifier '" + modifier.Id + "' is referenced by no class Passives " +
                            "and no trait Equippable.Passives — it can never apply to anything.");
                        continue;
                    }

                    List<Entry> list;
                    if (!byStat.TryGetValue(modifier.Stat, out list))
                    {
                        list = new List<Entry>();
                        byStat[modifier.Stat] = list;
                    }
                    list.Add(entry);
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Stat-modifier book load failed (fail-safe — whatever parsed applies): " + ex);
            }

            Book = book;
            ByStat = byStat;

            if (files > 0 || book.Ordered.Count > 0)
            {
                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] Stat-modifier book: " + book.Ordered.Count.ToString(CultureInfo.InvariantCulture) +
                    " modifier(s) from " + files.ToString(CultureInfo.InvariantCulture) + " statmodifiers.json file(s); " +
                    live.ToString(CultureInfo.InvariantCulture) + " live across " +
                    byStat.Count.ToString(CultureInfo.InvariantCulture) + " stat(s) [" +
                    string.Join(", ", StatKeys(byStat)) + "], " +
                    errors.ToString(CultureInfo.InvariantCulture) + " error(s), " +
                    warnings.ToString(CultureInfo.InvariantCulture) + " warning(s).");
            }
        }

        /// <summary>Scans the merge plan for classes/traits whose Passives reference this modifier id.</summary>
        private static void BindOwners(PackLoadResult result, string modifierId, Entry entry)
        {
            var plan = result.MergePlan;
            if (plan == null) return;

            for (int i = 0; i < plan.Characters.Count; i++)
            {
                var op = plan.Characters[i];
                if (PassivesContain(op.Value.Get("Passives"), modifierId))
                    entry.ClassOwners.Add(op.Id);
            }
            for (int i = 0; i < plan.Things.Count; i++)
            {
                var op = plan.Things[i];
                if (PassivesContain(op.Value.Get("Equippable").Get("Passives"), modifierId))
                    entry.TraitOwners.Add(op.Id);
            }
        }

        private static bool PassivesContain(ClassForge.Core.Json.JsonValue passives, string id)
        {
            var arr = passives.AsArray;
            for (int i = 0; i < arr.Count; i++)
                if (string.Equals(arr[i].AsString, id, StringComparison.Ordinal)) return true;
            return false;
        }

        private static List<string> StatKeys(Dictionary<string, List<Entry>> byStat)
        {
            var keys = new List<string>(byStat.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }
}
