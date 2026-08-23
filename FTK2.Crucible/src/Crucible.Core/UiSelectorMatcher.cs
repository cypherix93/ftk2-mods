using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// First-match selector logic for <c>crucible_ui_click</c>: case-insensitive substring over
    /// name/text, first-match semantics, and a no-match/diagnosability path. Pure — no game
    /// reference — so it unit-tests with hand-built element lists, including negative controls (a
    /// selector matching nothing must report no-match, not silently pick something; an invisible
    /// element must never be selected even if its name/text would otherwise match).
    /// </summary>
    public static class UiSelectorMatcher
    {
        public sealed class Result
        {
            /// <summary>The first visible match, or null if <see cref="Found"/> is false.</summary>
            public UiElementInfo Match;

            /// <summary>Additional visible matches beyond the first (empty if only one or zero matched).</summary>
            public readonly List<UiElementInfo> OtherMatches = new List<UiElementInfo>();

            /// <summary>
            /// Every visible candidate considered, populated regardless of match outcome — used to
            /// list "the visible buttons that DO exist" when nothing matched, so a failed click is
            /// diagnosable without another round trip.
            /// </summary>
            public readonly List<UiElementInfo> AllVisible = new List<UiElementInfo>();

            public bool Found;
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

            List<UiElementInfo> matches = new List<UiElementInfo>();
            if (candidates != null)
            {
                foreach (UiElementInfo candidate in candidates)
                {
                    if (candidate == null) continue;
                    // Off-screen elements are never selectable, even on a substring match — a click
                    // on one would be a silent no-op that looks like success. Gates on the full
                    // OnScreenTest decision (SPEC S3), not just the element's own local .visible.
                    if (!candidate.OnScreen) continue;

                    result.AllVisible.Add(candidate);
                    if (UiTreeRenderer.Matches(candidate, selector)) matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                result.Found = false;
                return result;
            }

            result.Found = true;
            result.Match = matches[0];
            for (int i = 1; i < matches.Count; i++) result.OtherMatches.Add(matches[i]);
            return result;
        }
    }
}
