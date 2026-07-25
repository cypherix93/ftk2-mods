using System;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Merges pack localization/en.json strings into the game's active translation dictionary
    /// (SPEC.md §3, §4.7). Ground truth (Lang.cs L251-262): <c>Lang.SetLanguage(pISO)</c> reassigns
    /// <c>Lang.__translations</c> to reference <c>Env.Configs.Langs[pISO]</c> each call, so the merge must run
    /// as a Postfix on SetLanguage (after that reassignment) rather than once at boot — the EOR precedent
    /// SPEC.md §3 calls out.
    /// </summary>
    public static class LocalizationPatches
    {
        public static void SetLanguage_Postfix(string pISO)
        {
            if (!ClassForgePlugin.Enabled.Value) return;

            var plan = ClassForgePlugin.CurrentMergePlan;
            if (plan == null || plan.Localization.Count == 0)
                return; // no packs merged yet (e.g. SetLanguage ran before the first LoadConfigs postfix) — nothing to do.

            try
            {
                foreach (var kv in plan.Localization)
                    Lang.__translations[kv.Key] = kv.Value;

                if (ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug($"[ClassForge] Merged {plan.Localization.Count} localization keys into Lang.__translations after SetLanguage('{pISO}').");
            }
            catch (Exception ex)
            {
                // Fail-safe: vanilla strings stay whatever they already were; pack ids fall back to raw id display.
                ClassForgePlugin.Log.LogError($"[ClassForge] Localization merge failed after SetLanguage('{pISO}'): {ex}");
            }
        }
    }
}
