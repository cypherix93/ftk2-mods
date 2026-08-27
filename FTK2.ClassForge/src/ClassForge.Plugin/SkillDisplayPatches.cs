using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// <para><b>Row capacity — UNVERIFIED, so the code must not assume one.</b> Both templates ship a
    /// FIXED number of <c>skill-container</c> / <c>class-skill-container</c> rows, and that number lives
    /// in the game's UXML inside <c>dUIDocument</c> — a binary asset with no <c>.uxml</c> in this repo.
    /// It was previously assumed to be "4, sized for vanilla's maximum of 4 passives (HUNTER)"; that
    /// assumption is what silently ate most of the Pokemon Trainer's passives (<c>CF_ORIG_TRAINER</c>
    /// declares 20, of which every one that carries a title earns a row). Nothing below reads or guesses
    /// a row count: rows are consumed until they run out, and whatever is left over is handled
    /// explicitly.</para>
    ///
    /// <para><b>Overflow policy.</b> On the character-customization surface the leftovers are appended to
    /// the <c>class-description-text</c> Label — the exact idiom <see cref="TrainerPartnerPanel"/> already
    /// uses successfully on that same Label (<c>TrainerPartnerPanel.cs:110-126</c>), which the game itself
    /// uses for its own dynamic lines (<c>ItemCardViewHelper.cs:1438-1465</c>). So that surface drops
    /// nothing, whatever the row count turns out to be. The in-game party summary has no equivalent
    /// text element — <c>CharacterSummaryViewHelper.Initialize</c> resolves only titles and fixed row
    /// lists (<c>CharacterSummaryViewHelper.cs:143-184</c>) — and building a new panel is out of scope, so
    /// there the overflow is reported rather than shown: one Warning per dropped skill id, plus a
    /// read-back count (<see cref="DroppedSkillsFor"/>) a harness can assert on.</para>
    ///
    /// <para><b>Known limitation of the append (preserved from <see cref="TrainerPartnerPanel"/>).</b>
    /// <c>UIToolkitHelper.ProcessAsianMultilineText</c> is <c>async void</c> and assigns the Label at
    /// <c>UIToolkitHelper.cs:1265</c> AFTER awaiting a layout pass — i.e. after this postfix has run — so
    /// in Chinese/Japanese the appended block is overwritten and does not show. Every other language takes
    /// the synchronous branch at <c>UIToolkitHelper.cs:1214-1217</c> and the append stands.</para>
    /// </summary>
    internal static class SkillDisplayPatches
    {
        /// <summary>The character-creation / party-management class info tab.</summary>
        public const string SurfaceCustomization = "customization";

        /// <summary>The in-game party summary's "Class Skills" block.</summary>
        public const string SurfaceSummary = "summary";

        /// <summary>One warning per (surface, class id, skill id) so a rebind loop cannot spam the log.</summary>
        private static readonly HashSet<string> WarnedNoRow = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The shape every appended block in this Label starts with — ours and
        /// <see cref="TrainerPartnerPanel"/>'s alike. Truncation stops at the next occurrence of it so we
        /// replace only our own block.
        /// </summary>
        private const string BlockMarkerPrefix = "\n\n— ";

        /// <summary>The bullet each overflowed skill line starts with.</summary>
        private const string Bullet = "• ";

        /// <summary>
        /// Marks the start of our overflow block inside the description Label so a re-render truncates
        /// the previous one instead of stacking duplicates. Starts with <see cref="BlockMarkerPrefix"/>
        /// so a sibling patch's block (the partner roster) can be recognised and preserved.
        /// </summary>
        private const string OverflowMarker = BlockMarkerPrefix + "MORE CLASS SKILLS —";

        /// <summary>
        /// Per-surface record of skills that found neither a row nor an overflow home on the last render.
        /// A test/harness asserts <c>DroppedSkillsFor(SurfaceCustomization).Count == 0</c> — that surface
        /// has the description Label, so it must never drop anything regardless of the template's row count.
        /// </summary>
        private static readonly Dictionary<string, List<string>> DroppedBySurface =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>The skill ids the given surface could not show on its last render (never null).</summary>
        public static IReadOnlyList<string> DroppedSkillsFor(string surface)
        {
            List<string> dropped;
            return DroppedBySurface.TryGetValue(surface ?? "", out dropped)
                ? (IReadOnlyList<string>)dropped
                : new List<string>();
        }

        /// <summary>How many skill ids the given surface could not show on its last render.</summary>
        public static int DroppedSkillCountFor(string surface)
        {
            return DroppedSkillsFor(surface).Count;
        }

        /// <summary>
        /// The exact overflow text last pushed into the class info tab's description Label, or "" when
        /// nothing overflowed. Read back OFF the live element after the write, not off the string we built,
        /// so a test asserts what the panel actually holds (same posture as
        /// <c>TrainerPartnerPanel.LastRenderedText</c>).
        /// </summary>
        public static string LastOverflowText { get; private set; } = "";

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
                var overflow = FillSpareRows(SurfaceCustomization, pConfigName, skillRows, rows,
                    ToolTipHelper.ePosition.ABOVE, TextAnchor.UpperCenter);

                // The tab's rows are a fixed-count template; its description Label is not. Anything the
                // rows could not hold goes there, so this surface drops nothing at any row count.
                RenderOverflowIntoDescription(statsContainer, pConfigName, overflow);
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
                var overflow = FillSpareRows(SurfaceSummary, component.ConfigName, skillRows, rows,
                    ToolTipHelper.ePosition.BELOW, TextAnchor.UpperLeft);

                // No description Label exists on this template (CharacterSummaryViewHelper.cs:143-184), and
                // building a new panel is out of scope — so the overflow is recorded and warned, not shown.
                RecordDropped(SurfaceSummary, component.ConfigName, overflow);
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
        ///
        /// <para>Returns the skills that found no row, in authored order — never null, never truncated.
        /// The caller decides what to do with them; this method makes no assumption about how many rows
        /// the template ships.</para>
        /// </summary>
        private static List<string> FillSpareRows(string surface, string configName, List<VisualElement> skillRows,
            List<string> customSkills, ToolTipHelper.ePosition tooltipPosition, TextAnchor secondaryAnchor)
        {
            var overflow = new List<string>();
            if (customSkills == null) return overflow;
            if (skillRows == null) { overflow.AddRange(customSkills); return overflow; }

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
                    // Keep walking the list: every remaining skill is overflow and the caller needs all of
                    // them BY ID. (The old code returned here, which is what made them vanish silently.)
                    overflow.Add(skillId);
                    continue;
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

            return overflow;
        }

        /// <summary>
        /// Appends the row-starved skills to the class info tab's <c>class-description-text</c> Label.
        ///
        /// <para>Reuses <see cref="TrainerPartnerPanel"/>'s idiom on the very same Label
        /// (<c>TrainerPartnerPanel.cs:110-126</c>): resolve the Label off <c>stats-container</c>, truncate
        /// any block we appended previously, append, then read the result back off the live element.
        /// Vanilla rewrites this Label from scratch on every render
        /// (<c>CharacterCustomizationViewHelper.cs:1008</c>), so the truncation is belt-and-braces; it stops
        /// at the next block marker rather than cutting to the end, so it can never eat a sibling patch's
        /// block (e.g. the partner roster) if the two postfixes ever run in the other order.</para>
        /// </summary>
        private static void RenderOverflowIntoDescription(VisualElement statsContainer, string configName,
            List<string> overflow)
        {
            if (overflow == null || overflow.Count == 0)
            {
                RecordDropped(SurfaceCustomization, configName, null);
                LastOverflowText = "";
                return;
            }

            var label = statsContainer != null ? statsContainer.Q<Label>("class-description-text") : null;
            if (label == null)
            {
                // Nowhere to put them -> they really are dropped, and that is worth shouting about.
                RecordDropped(SurfaceCustomization, configName, overflow);
                LastOverflowText = "";
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] The class info tab has no 'class-description-text' label, so " +
                    overflow.Count + " row-starved skill(s) for " + configName + " cannot be shown.");
                return;
            }

            var plan = ClassForgePlugin.CurrentMergePlan;
            var block = new StringBuilder(OverflowMarker);
            foreach (var skillId in overflow)
            {
                var descriptionKey = "UI_ENCYCLOPEDIA_" + skillId;
                var description = plan != null && plan.Localization.ContainsKey(descriptionKey)
                    ? Lang.__t(descriptionKey) : "";
                block.Append("\n").Append(Bullet).Append(Lang.__t(skillId));
                if (!string.IsNullOrEmpty(description)) block.Append(" — ").Append(description);
            }

            var baseText = label.text ?? "";
            int prior = baseText.IndexOf(OverflowMarker, StringComparison.Ordinal);
            if (prior >= 0)
            {
                int after = baseText.IndexOf(BlockMarkerPrefix, prior + OverflowMarker.Length, StringComparison.Ordinal);
                baseText = after >= 0
                    ? baseText.Substring(0, prior) + baseText.Substring(after)
                    : baseText.Substring(0, prior);
            }

            label.text = baseText + block;

            // Read back OFF the live element -- not off the string we built -- so a test asserts what the
            // panel actually holds (same posture as TrainerPartnerPanel.LastRenderedText).
            LastOverflowText = label.text ?? "";

            // Shown, not dropped.
            RecordDropped(SurfaceCustomization, configName, null);

            ClassForgePlugin.Log.LogInfo(
                "[ClassForge] " + configName + ": " + overflow.Count + " class skill(s) exceeded the info tab's " +
                "spare skill rows and were appended to the description text instead: " +
                string.Join(", ", overflow.ToArray()) + ".");
        }

        /// <summary>
        /// Records the per-surface dropped set for read-back and logs one Warning per dropped skill id
        /// (deduped by surface+class+skill), so the log names exactly what is missing instead of one
        /// generic line that hides both the count and the ids.
        /// </summary>
        private static void RecordDropped(string surface, string configName, List<string> dropped)
        {
            var list = dropped != null ? new List<string>(dropped) : new List<string>();
            DroppedBySurface[surface] = list;

            foreach (var skillId in list)
            {
                if (!WarnedNoRow.Add(surface + "|" + configName + "|" + skillId)) continue;
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] No spare " + surface + " skill row left for " + configName +
                    " — '" + skillId + "' is NOT shown on that surface. The template's row count is fixed in " +
                    "dUIDocument's UXML; see the SkillDisplayPatches header. Logged once per skill.");
            }
        }
    }
}
