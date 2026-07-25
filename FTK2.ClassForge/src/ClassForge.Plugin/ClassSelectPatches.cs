using BepInEx.Logging;

namespace ClassForge.Plugin
{
    /// <summary>
    /// LATER-UNIT WIRING POINT — not installed in M1 (explicit scope note for this unit: "the UI patch itself
    /// is a LATER unit; leave a clearly-marked wiring point").
    ///
    /// Target (game-patch-surface-notes.md §8): <c>CharacterCustomizationViewHelper.RenderClassList(Entity
    /// pEntity, List&lt;string&gt; pPlayableCharacters, Func&lt;Entity,string,bool,bool,Task&gt; pOnChangeClass,
    /// VisualElement pItemCard)</c> — confirmed hook shape is a <b>Prefix that mutates <c>pPlayableCharacters</c>
    /// in place</b> (it's a List&lt;string&gt; reference and RenderClassList doesn't rebuild it), appending pack
    /// PLAYER-tagged class ids before the vanilla render walks the list. M1 only binds the
    /// <c>[UI] EnableClassSelectInjection</c> knob (see ClassForgePlugin) so its config key is stable when the
    /// real patch lands; until then, pack classes exist in Configs.Characters (from ConfigMergePatches) and are
    /// already usable via console/dev tools or LoadOuts.json, per SPEC.md §5.
    /// </summary>
    public static class ClassSelectPatches
    {
        public static void LogWiringPointOnly(ManualLogSource log)
        {
            log.LogInfo("[ClassForge] CharacterCustomizationViewHelper.RenderClassList pack-class injection: " +
                        "wiring point only in M1 (knob [UI] EnableClassSelectInjection bound; the Harmony patch " +
                        "itself ships in a later unit).");
        }

        // Later unit, sketch:
        //
        // public static void RenderClassList_Prefix(Entity pEntity, List<string> pPlayableCharacters, ...)
        // {
        //     if (!ClassForgePlugin.Enabled.Value || !ClassForgePlugin.EnableClassSelectInjection.Value) return;
        //     var plan = ClassForgePlugin.CurrentMergePlan;
        //     if (plan == null) return;
        //     foreach (var op in plan.Characters)
        //         if (string.Equals(op.Value.GetString("Tags"), ..., ...) /* Tags contains "PLAYER" */
        //             && !pPlayableCharacters.Contains(op.Id))
        //             pPlayableCharacters.Add(op.Id);
        // }
    }
}
