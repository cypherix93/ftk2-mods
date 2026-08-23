using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Selector-matching logic for <c>crucible_ui_press</c> and <c>crucible_dialogue_choose</c>.
    /// Unlike <see cref="UiSelectorMatcher"/> (first-match, used by the pre-existing
    /// <c>crucible_ui_click</c>/<c>crucible_ui_focus</c> and left unchanged so their tests keep
    /// passing), this ranks an EXACT case-insensitive match on name or text above a mere substring
    /// match — a live selector collision ("Back" matched <c>backpack-button</c> before <c>back-btn</c>
    /// and pressed the wrong control) showed first-match-wins is dangerous once a selector could be
    /// an exact label. It also folds in <see cref="UiForbiddenElements"/>: the chosen element is
    /// refused outright if forbidden, even when it is the only match.
    ///
    /// Pure — no game reference — so it unit-tests with hand-built element lists, including negative
    /// controls (forbidden refused even as sole match; exact beats an earlier-encountered substring
    /// match; no match reports what IS on screen instead of silently picking something).
    /// </summary>
    public static class UiPressMatcher
    {
        public sealed class Result
        {
            /// <summary>The chosen match, or null if <see cref="Found"/> is false.</summary>
            public UiElementInfo Match;

            /// <summary>Every other on-screen candidate that also matched the selector (substring or exact), for collision reporting.</summary>
            public readonly List<UiElementInfo> OtherMatches = new List<UiElementInfo>();

            /// <summary>Every on-screen candidate considered, regardless of match outcome — lists "what IS on screen" when nothing matched.</summary>
            public readonly List<UiElementInfo> AllVisible = new List<UiElementInfo>();

            public bool Found;

            /// <summary>True if <see cref="Match"/> is a forbidden element (<see cref="UiForbiddenElements"/>) — the caller must refuse to activate it.</summary>
            public bool Forbidden;

            /// <summary>Why <see cref="Forbidden"/> is true; null otherwise.</summary>
            public string ForbiddenReason;
        }

        public static Result Find(IEnumerable<UiElementInfo> candidates, string selector, out string error)
        {
            error = null;
            Result result = new Result();

            if (string.IsNullOrEmpty(selector))
            {
                error = "empty selector";
                return result;
            }

            List<UiElementInfo> substringMatches = new List<UiElementInfo>();
            List<UiElementInfo> exactMatches = new List<UiElementInfo>();

            if (candidates != null)
            {
                foreach (UiElementInfo candidate in candidates)
                {
                    if (candidate == null) continue;
                    // Off-screen elements are never selectable, even on a substring OR exact match —
                    // matching UiSelectorMatcher's posture (SPEC S3 true-visibility).
                    if (!candidate.OnScreen) continue;

                    result.AllVisible.Add(candidate);
                    if (!UiTreeRenderer.Matches(candidate, selector)) continue;

                    substringMatches.Add(candidate);
                    if (IsExact(candidate, selector)) exactMatches.Add(candidate);
                }
            }

            List<UiElementInfo> ranked = exactMatches.Count > 0 ? exactMatches : substringMatches;
            if (ranked.Count == 0)
            {
                result.Found = false;
                return result;
            }

            UiElementInfo chosen = ranked[0];
            result.Found = true;
            result.Match = chosen;
            foreach (UiElementInfo m in substringMatches)
            {
                if (!ReferenceEquals(m, chosen)) result.OtherMatches.Add(m);
            }

            string forbiddenReason;
            if (UiForbiddenElements.IsForbidden(chosen.Name, chosen.DocumentName, out forbiddenReason))
            {
                result.Forbidden = true;
                result.ForbiddenReason = forbiddenReason;
            }

            return result;
        }

        /// <summary>An exact case-insensitive equality on name OR text — not a substring check.</summary>
        private static bool IsExact(UiElementInfo e, string selector)
        {
            if (e.Name != null && string.Equals(e.Name, selector, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Text != null && string.Equals(e.Text, selector, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
