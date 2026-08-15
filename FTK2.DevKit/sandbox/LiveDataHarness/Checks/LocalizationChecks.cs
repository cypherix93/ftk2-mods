using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Localization coverage against the live Lang table.
    ///
    /// The convention is keyed off the config id, not off LocKey: a class with id ALCHEMIST needs
    /// en["ALCHEMIST"] for its display name and en["UI_TOOLTIP_ALCHEMIST_DESCRIPTION"] for its tooltip.
    /// Vanilla leaves CharacterConfig.LocKey empty on every player class, so keying the check off LocKey
    /// would check nothing at all.
    /// </summary>
    public static class LocalizationChecks
    {
        public static void Register(CheckRunner runner, GameData data, PackLoadResult result)
        {
            var en = data.Lang("en");
            var merged = result.MergePlan.Localization;

            Func<string, bool> has = key => merged.ContainsKey(key) || (en != null && en.ContainsKey(key));

            runner.Section("Localization coverage");

            runner.Case("every merged class has a name and a tooltip key", () =>
            {
                var offenders = new List<string>();
                foreach (var op in result.MergePlan.Characters)
                {
                    if (!has(op.Id)) offenders.Add("class " + op.Id + ": missing display-name key '" + op.Id + "'");
                    var tip = "UI_TOOLTIP_" + op.Id + "_DESCRIPTION";
                    if (!has(tip)) offenders.Add("class " + op.Id + ": missing tooltip key '" + tip + "'");
                }
                Check.Empty(offenders, "classes with missing localization");
            });

            runner.Case("every merged trait has a name key", () =>
            {
                var offenders = result.MergePlan.TraitIds
                    .Where(id => !has(id))
                    .Select(id => "trait " + id + ": missing display-name key '" + id + "'");
                Check.Empty(offenders, "traits with missing localization");
            });

            runner.Case("no pack localization key overwrites a vanilla key", () =>
            {
                if (en == null) { Check.True(false, "Configs.Langs['en'] is unavailable"); return; }
                var offenders = merged.Keys
                    .Where(k => en.ContainsKey(k))
                    .Select(k => "pack key '" + k + "' shadows vanilla en['" + k + "'] = \"" + en[k] + "\"");
                Check.Empty(offenders, "pack localization keys shadowing vanilla");
            });

            runner.Case("negative control: a key we never ship is reported missing", () =>
                Check.True(!has("CF_HARNESS_NEVER_SHIPPED_KEY"),
                    "the localization lookup must return false for an unshipped key — otherwise 'has' is always true and the checks above are vacuous"));
        }
    }
}
