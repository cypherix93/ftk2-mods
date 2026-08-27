using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Class-select UI integration (SPEC.md §6, M1 scope: <c>RenderClassList</c>).
    ///
    /// <para><b>Verified target</b> (CharacterCustomizationViewHelper.cs L1543):</para>
    /// <code>public static async void RenderClassList(Entity pEntity, List&lt;string&gt; pPlayableCharacters,
    /// Func&lt;Entity, string, bool, bool, Task&gt; pOnChangeClass, VisualElement pItemCard)</code>
    ///
    /// <para><b>Hook shape: Prefix that mutates <c>pPlayableCharacters</c> in place.</b> The method hands the
    /// list straight to <c>UIToolkitHelper.RenderListView(..., pPlayableCharacters, &lt;row-render delegate&gt;, ...)</c>
    /// as the <c>ListView.itemsSource</c> and never rebuilds it, so appending to the existing instance is
    /// sufficient and no transpiler is needed. Reassigning the reference would <b>not</b> work (the list
    /// instance is what the ListView binds to), so this only ever calls <c>Add</c>.</para>
    ///
    /// <para><b>The crash the guard exists for.</b> <c>RenderClassList</c> itself is safe with an unknown id,
    /// but its selection-changed callback runs <c>RenderStatsContainer</c>, which opens with
    /// <c>CharacterConfig charConfig = Env.Configs.Characters[pConfigName];</c> — a raw indexer that throws
    /// <c>KeyNotFoundException</c> — and then dereferences <c>charConfig.Stats[...]</c>,
    /// <c>charConfig.Things</c> and <c>charConfig.Passives</c> unguarded. So a pack class id is injected
    /// <b>only</b> once its <c>CharacterConfig</c> is really present in <c>Env.Configs.Characters</c> with
    /// those three collections non-null. This is the same discipline EOR's <c>EnsureOverhaulPlayerClasses</c>
    /// uses (a <c>ContainsKey</c> gate before every <c>playable.Add</c>).</para>
    ///
    /// <para>Row labels come from <c>Lang.__t(classId)</c> — the config key <i>is</i> the localization key,
    /// and <c>Lang.__t</c>'s miss path returns the raw key rather than throwing. ClassForge already merges
    /// pack strings into <c>Lang.__translations</c> from the <c>SetLanguage</c> postfix
    /// (<see cref="LocalizationPatches"/>), so no <c>Lang.__t</c> patch is needed here.</para>
    /// </summary>
    public static class ClassSelectPatches
    {
        /// <summary>Playability is expressed by a <c>"PLAYER"</c> entry in <c>CharacterConfig.Tags</c> —
        /// there is no <c>IsPlayable</c> field. <c>CharacterHelper</c> itself queries
        /// <c>GetActorsByTags(new List&lt;string&gt; { "PLAYER" }, -1)</c>.</summary>
        private const string PlayerTag = "PLAYER";

        private static readonly HashSet<string> WarnedIncomplete = new HashSet<string>(StringComparer.Ordinal);
        private static bool _loggedInjection;

        // Only the two parameters the prefix actually uses — Harmony binds prefix parameters by
        // name, so omitting pOnChangeClass/pItemCard keeps this valid when their types drift
        // between game updates (they did on 7/31/2026: pOnChangeClass gained a fifth bool).
        public static void RenderClassList_Prefix(Entity pEntity, List<string> pPlayableCharacters)
        {
            if (!ClassForgePlugin.PresentationActive) return;
            if (!ClassForgePlugin.EnableClassSelectInjection.Value) return;
            if (pPlayableCharacters == null) return;

            var plan = ClassForgePlugin.CurrentMergePlan;
            if (plan == null || plan.Characters.Count == 0) return;

            try
            {
                // Deterministic order so every peer's class list reads identically (and so the list is
                // stable between openings of the screen).
                var ids = new List<string>();
                for (int i = 0; i < plan.Characters.Count; i++)
                {
                    var op = plan.Characters[i];
                    if (op != null && !string.IsNullOrEmpty(op.Id)) ids.Add(op.Id);
                }
                ids.Sort(StringComparer.Ordinal);

                var present = new HashSet<string>(pPlayableCharacters, StringComparer.Ordinal);
                int added = 0;

                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (present.Contains(id)) continue;

                    var cfg = GameLookups.CharacterConfig(id);
                    if (cfg == null) continue;                 // merge has not run / entry failed to deserialize
                    if (!HasPlayerTag(cfg)) continue;          // not a selectable class

                    if (!IsRenderable(cfg))
                    {
                        if (WarnedIncomplete.Add(id))
                        {
                            ClassForgePlugin.Log.LogWarning(
                                "[ClassForge] Class '" + id + "' is tagged " + PlayerTag + " but its CharacterConfig is " +
                                "missing Stats/Things/Passives — NOT added to the class list. " +
                                "CharacterCustomizationViewHelper.RenderStatsContainer dereferences all three without a " +
                                "null check and would throw the moment the row is selected. Add the missing sections to " +
                                "classes.json.");
                        }
                        continue;
                    }

                    pPlayableCharacters.Add(id);
                    present.Add(id);
                    added++;
                }

                if (added > 0 && !_loggedInjection)
                {
                    _loggedInjection = true;
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] RenderClassList: injected " + added.ToString(CultureInfo.InvariantCulture) +
                        " pack class(es); list now " + pPlayableCharacters.Count.ToString(CultureInfo.InvariantCulture) +
                        " entries. Logged once per session.");
                }
                else if (added > 0 && ClassForgePlugin.VerboseLogging.Value)
                {
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] RenderClassList: injected " + added.ToString(CultureInfo.InvariantCulture) + " pack class(es).");
                }
            }
            catch (Exception ex)
            {
                // Fail-safe: the vanilla class list still renders exactly as it would have.
                ClassForgePlugin.Log.LogError("[ClassForge] Class-select injection failed (fail-safe, vanilla list unchanged): " + ex);
            }
        }

        private static bool HasPlayerTag(CharacterConfig cfg)
        {
            var tags = cfg.Tags;
            if (tags == null) return false;
            for (int i = 0; i < tags.Count; i++)
                if (string.Equals(tags[i], PlayerTag, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Exactly the three collections <c>RenderStatsContainer</c> dereferences unguarded.</summary>
        private static bool IsRenderable(CharacterConfig cfg)
        {
            return cfg.Stats != null && cfg.Things != null && cfg.Passives != null;
        }
    }
}
