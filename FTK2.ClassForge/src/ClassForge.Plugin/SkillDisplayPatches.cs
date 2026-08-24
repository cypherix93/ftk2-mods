using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Renders pack-granted class skills (e.g. <c>SKILL_CF_*</c>) in the two skill-list UIs that
    /// otherwise silently drop them (SPEC.md §6 presentation layer).
    ///
    /// <para><b>Why this exists.</b> Both vanilla renderers filter a class's <c>Passives</c> through
    /// <c>Enum.TryParse&lt;eSkills&gt;</c> before display — <c>CharacterCustomizationViewHelper.
    /// RenderStatsContainer</c> (decompile L1069) and <c>CharacterSummaryViewHelper._showClassSkills</c>
    /// (L280). A pack skill is not a compiled enum member, so it renders nowhere despite being a real,
    /// functioning recipe passive. The reference implementation hits the same wall and solves it the same
    /// way (its <c>RenderClassSkillCustomizationRow</c>): un-hide one of the template's spare
    /// <c>skill-container</c> rows and fill it by hand. Unlike the reference implementation, we also
    /// patch the in-game party summary, not just character creation.</para>
    ///
    /// <para><b>Presentation-only.</b> Both postfixes read merged Configs/Lang state and mutate UI
    /// elements; no game state is touched, so this is outside the parity payload (same posture as
    /// <see cref="AssetPatches"/>). Every body is wrapped in a fail-safe catch per docs/CONVENTIONS.md —
    /// worst case is the vanilla panel, i.e. the skill just stays invisible.</para>
    ///
    /// <para><b>Row capacity.</b> The templates ship a fixed number of rows sized for vanilla's maximum
    /// of 4 passives (HUNTER). Our worst case is 2 vanilla + 2 custom halves = 4. If a future pack
    /// exceeds the free rows we log one warning per class id and drop the overflow — same degradation
    /// the reference implementation chose.</para>
    /// </summary>
    internal static class SkillDisplayPatches
    {
        /// <summary>One warning per (surface, class id) so a rebind loop cannot spam the log.</summary>
        private static readonly HashSet<string> WarnedNoRow = new HashSet<string>(StringComparer.Ordinal);

        // ---------------------------------------------------------------- character creation

        /// <summary>
        /// Postfix for <c>CharacterCustomizationViewHelper.RenderStatsContainer(Entity, string,
        /// VisualElement)</c>. The vanilla body has just finished assigning every <c>skill-container</c>
        /// row's <c>style.display</c> (Flex = used, None = spare), so spare-row detection is reliable.
        /// </summary>
        public static void RenderStatsContainer_Postfix(string pConfigName)
        {
            try
            {
                var rows = CustomRowsFor(pConfigName);
                if (rows == null || rows.Count == 0) return;

                var root = AccessTools.StaticFieldRefAccess<VisualElement>(
                    typeof(CharacterCustomizationViewHelper), "_root");
                var statsContainer = root?.Q("stats-container");
                if (statsContainer == null) return;

                var skillRows = statsContainer.Query("skill-container").ToList();
                FillSpareRows("customization", pConfigName, skillRows, rows,
                    ToolTipHelper.ePosition.ABOVE, TextAnchor.UpperCenter);
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Skill display (customization) failed (fail-safe, vanilla panel intact): " + ex);
            }
        }

        // ---------------------------------------------------------------- in-game party summary

        /// <summary>
        /// Postfix for <c>CharacterSummaryViewHelper._showClassSkills(Entity)</c> — the in-game party
        /// screen's "Class Skills" block. Instance method, rows cached in the private
        /// <c>_classSkillsList</c> field.
        /// </summary>
        public static void ShowClassSkills_Postfix(CharacterSummaryViewHelper __instance, Entity pCharacter)
        {
            try
            {
                var component = pCharacter?.Get<CharacterComponent>();
                if (component == null) return;

                var rows = CustomRowsFor(component.ConfigName);
                if (rows == null || rows.Count == 0) return;

                var skillRows = AccessTools.FieldRefAccess<CharacterSummaryViewHelper, List<VisualElement>>(
                    __instance, "_classSkillsList");
                FillSpareRows("summary", component.ConfigName, skillRows, rows,
                    ToolTipHelper.ePosition.BELOW, TextAnchor.UpperLeft);
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Skill display (party summary) failed (fail-safe, vanilla panel intact): " + ex);
            }
        }

        // ---------------------------------------------------------------- shared

        /// <summary>
        /// The custom skill ids to render for a class, or null when the feature is off / the class is
        /// not merged pack content. Selection logic lives in Core (<see cref="SkillDisplay"/>, tested
        /// offline); this binds its predicates to the live game: "vanilla-renderable" is exactly the
        /// <c>Enum.TryParse&lt;eSkills&gt;</c> filter both vanilla renderers apply, and display names come
        /// from the merged pack localization (never vanilla Lang — a vanilla key on a non-enum passive
        /// would still render as a raw id elsewhere, so it earns no row).
        /// </summary>
        private static List<string> CustomRowsFor(string configName)
        {
            if (!ClassForgePlugin.FeaturesActive) return null;
            if (!ClassForgePlugin.EnableSkillDisplay.Value) return null;
            var plan = ClassForgePlugin.CurrentMergePlan;
            if (plan == null || plan.Localization.Count == 0) return null;
            if (string.IsNullOrEmpty(configName)) return null;

            CharacterConfig config;
            if (!Env.Configs.Characters.TryGetValue(configName, out config) || config.Passives == null)
                return null;

            return SkillDisplay.SelectCustomSkillRows(
                config.Passives,
                p => { eSkills _; return Enum.TryParse<eSkills>(p, out _); },
                plan.Localization.ContainsKey);
        }

        /// <summary>
        /// Un-hides one spare row per custom skill and fills icon/name/tooltip, mirroring each vanilla
        /// renderer's own binding shape. Icon is the class's pack icon (served by
        /// <see cref="AssetPatches"/> through <c>AssetLoader.GetImage</c>) — the signature skill carries
        /// its class's mark; there is no per-skill art in the pack format.
        /// </summary>
        private static void FillSpareRows(string surface, string configName, List<VisualElement> skillRows,
            List<string> customSkills, ToolTipHelper.ePosition tooltipPosition, TextAnchor secondaryAnchor)
        {
            if (skillRows == null) return;

            var plan = ClassForgePlugin.CurrentMergePlan;
            Color iconColor;
            var icon = AssetLoader.GetImage(configName, eTextureAtlas.Class, out iconColor);

            int next = 0;
            foreach (var skillId in customSkills)
            {
                VisualElement row = null;
                for (; next < skillRows.Count; next++)
                {
                    if (skillRows[next].style.display.value == DisplayStyle.None)
                    {
                        row = skillRows[next];
                        next++;
                        break;
                    }
                }
                if (row == null)
                {
                    var warnKey = surface + "|" + configName;
                    if (WarnedNoRow.Add(warnKey))
                        ClassForgePlugin.Log.LogWarning(
                            "[ClassForge] No spare " + surface + " skill row left for " + configName +
                            " — '" + skillId + "' (and any further custom skills) will not be shown. Logged once per class.");
                    return;
                }

                var name = Lang.__t(skillId);
                var descriptionKey = "UI_ENCYCLOPEDIA_" + skillId;
                var description = plan.Localization.ContainsKey(descriptionKey) ? Lang.__t(descriptionKey) : "";

                row.style.display = DisplayStyle.Flex;
                var iconElement = row.Q("skill-icon");
                if (iconElement != null && icon != null)
                    iconElement.style.backgroundImage = icon;
                var label = row.Q<Label>("skill-text");
                if (label != null)
                    label.text = name;

                var boundRow = row;
                ToolTipHelper.Bind(boundRow, delegate
                {
                    ToolTipHelper.ShowListHint(boundRow, tooltipPosition, icon, name, description, "", secondaryAnchor);
                }, ToolTipHelper.HideListHint);
            }
        }
    }
}
