using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Localization coverage against the live Lang table plus the packs' merged localization. Convention
    /// verified 2026-08-23 against vanilla: the display name is keyed by the config id itself
    /// (en["ALCHEMIST"] == "Alchemist") and the tooltip by "UI_TOOLTIP_&lt;id&gt;_DESCRIPTION".
    /// CharacterConfig.LocKey is empty for every vanilla PLAYER class, so the id — not LocKey — is what
    /// must resolve. Live en.json carries zero mod keys (mod localization is injected at runtime), so a
    /// missing key here means the string will render as its raw id in game.
    /// </summary>
    public static class LocalizationChecks
    {
        public static void Register(CheckRunner runner, GameData data, AuthoredContent content)
        {
            IDictionary<string, string> en = data.Lang("en");
            Dictionary<string, string> merged = content.Packs.MergePlan.Localization;

            runner.Section("Localization coverage");

            runner.Case("the live 'en' table and the merged pack table are both present", delegate
            {
                Check.True(en != null, "Configs.Langs['en'] is available");
                Check.AtLeast(9000, en.Count, "live en key count (9707 measured)");
                Check.AtLeast(100, merged.Count, "merged pack localization keys — a near-empty " +
                                                 "table would make every coverage check below fail loudly, and " +
                                                 "a suspiciously full one would make them pass vacuously");
            });

            runner.Case("every merged class has a display-name and a tooltip key", delegate
            {
                Check.AtLeast(1, content.Packs.MergePlan.Characters.Count, "classes inspected");
                List<string> offenders = new List<string>();
                foreach (MergeOp op in content.Packs.MergePlan.Characters)
                {
                    if (!Has(merged, en, op.Id))
                        offenders.Add("class " + op.Id + ": missing display-name key '" + op.Id + "'");
                    string tip = "UI_TOOLTIP_" + op.Id + "_DESCRIPTION";
                    if (!Has(merged, en, tip))
                        offenders.Add("class " + op.Id + ": missing tooltip key '" + tip + "'");
                }
                Check.Empty(offenders, "classes with missing localization");
            });

            runner.Case("every merged trait and item has a display-name key", delegate
            {
                Check.AtLeast(1, content.Packs.MergePlan.Things.Count, "traits + items inspected");
                List<string> offenders = new List<string>();
                foreach (MergeOp op in content.Packs.MergePlan.Things)
                    if (!Has(merged, en, op.Id))
                        offenders.Add("thing " + op.Id + ": missing display-name key '" + op.Id + "'");
                Check.Empty(offenders, "traits/items with missing localization");
            });

            runner.Case("no pack localization key overwrites a vanilla key", delegate
            {
                List<string> offenders = new List<string>();
                foreach (KeyValuePair<string, string> kv in merged)
                    if (en.ContainsKey(kv.Key))
                        offenders.Add("pack key '" + kv.Key + "' shadows vanilla en['" + kv.Key + "'] = \"" + en[kv.Key] + "\"");
                Check.Empty(offenders, "pack localization keys shadowing vanilla");
            });

            runner.Case("NEGATIVE control: an unshipped key is reported missing", delegate
            {
                // If Has() were ever always-true, all three coverage checks above would pass forever.
                Check.True(!Has(merged, en, "LDH_NEVER_SHIPPED_LOCALIZATION_KEY"),
                    "the localization lookup must return false for a key nobody ships");
                Check.True(Has(merged, en, "ALCHEMIST"),
                    "...and true for a key that does exist (ALCHEMIST is a verified vanilla en key) — " +
                    "an always-false lookup would be equally vacuous in the other direction");
            });
        }

        private static bool Has(IDictionary<string, string> merged, IDictionary<string, string> en, string key)
        {
            if (merged != null && merged.ContainsKey(key)) return true;
            return en != null && en.ContainsKey(key);
        }
    }
}
