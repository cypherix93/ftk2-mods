using System;
using System.Collections.Generic;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Identity-by-index selector logic for <c>crucible_ui_click_nth</c> and its read-only companion
    /// <c>crucible_ui_matches</c>.
    ///
    /// <b>Why this exists.</b> <see cref="UiSelectorMatcher"/> is first-match-wins and
    /// <see cref="UiPressMatcher"/> ranks an exact name/text match first — neither can address one of
    /// several elements that share a name and carry NO text. The adventure-selection carousel is
    /// exactly that: nine <c>adventure-art-holder</c> Buttons, all with empty text, so every
    /// name-based verb hits the first one and no adventure can be chosen by identity. The
    /// nav-then-submit workaround is unsafe on that screen — it focuses and submits something that is
    /// never named, so the forbidden-element guard cannot see it, and <c>continue-btn</c>/
    /// <c>load-btn</c> are both live there.
    ///
    /// So: match by (selector [+ owning document] + zero-based index among the matches, in walk
    /// order). An index past the end is reported as OUT OF RANGE and never falls back to index 0 —
    /// a silent fallback would put the caller back on the first element while reporting success.
    ///
    /// Folds in <see cref="UiForbiddenElements"/> the same way <see cref="UiPressMatcher"/> does: the
    /// chosen element is flagged forbidden even when it is the only match, and the caller must refuse.
    ///
    /// Pure — no game reference — so it unit-tests with hand-built element lists.
    /// </summary>
    public static class UiIndexedMatcher
    {
        /// <summary>Document filter token meaning "any document".</summary>
        public const string AnyDocument = "-";

        public sealed class Result
        {
            /// <summary>The element at <see cref="Index"/>, or null unless <see cref="Found"/>.</summary>
            public UiElementInfo Match;

            /// <summary>The zero-based index that was requested (echoed back even on an out-of-range miss).</summary>
            public int Index = -1;

            /// <summary>Every on-screen candidate that matched the selector (and document filter), in walk order.</summary>
            public readonly List<UiElementInfo> Matches = new List<UiElementInfo>();

            /// <summary>Every on-screen candidate considered, regardless of match — lists "what IS on screen" when nothing matched.</summary>
            public readonly List<UiElementInfo> AllVisible = new List<UiElementInfo>();

            public bool Found;

            /// <summary>True when the selector DID match elements but <see cref="Index"/> is past the last one. Never a fallback to 0.</summary>
            public bool OutOfRange;

            /// <summary>True if <see cref="Match"/> is a forbidden element (<see cref="UiForbiddenElements"/>) — the caller must refuse to activate it.</summary>
            public bool Forbidden;

            /// <summary>Why <see cref="Forbidden"/> is true; null otherwise.</summary>
            public string ForbiddenReason;

            /// <summary>Total number of matches — what a caller checks an index against.</summary>
            public int MatchCount { get { return Matches.Count; } }
        }

        /// <summary>
        /// Parses the zero-based index argument. Never silently defaults: a non-numeric or negative
        /// index is an error, not a 0 (see <see cref="ArgCoercion"/> for the same posture).
        /// </summary>
        public static bool TryParseIndex(string raw, out int index, out string error)
        {
            index = -1;
            error = null;
            if (string.IsNullOrEmpty(raw))
            {
                error = "missing index: pass a zero-based index (use crucible_ui_matches to list them)";
                return false;
            }

            int parsed;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                error = "index must be an integer; got '" + raw + "'";
                return false;
            }

            if (parsed < 0)
            {
                error = "index must be zero or greater; got " + parsed.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            index = parsed;
            return true;
        }

        /// <summary>True if <paramref name="documentName"/> passes the <paramref name="document"/> filter ("-"/empty = any; otherwise exact, case-insensitive).</summary>
        public static bool DocumentMatches(string documentName, string document)
        {
            if (string.IsNullOrEmpty(document) || document == AnyDocument) return true;
            if (documentName == null) return false;
            return string.Equals(documentName, document, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Collects every on-screen candidate matching <paramref name="selector"/> (case-insensitive
        /// substring over name/text, same rule as <c>crucible_ui_dump</c>) inside
        /// <paramref name="document"/>, then selects the one at <paramref name="index"/>.
        /// </summary>
        public static Result Find(IEnumerable<UiElementInfo> candidates, string selector, string document, int index, out string error)
        {
            error = null;
            Result result = new Result();
            result.Index = index;

            if (string.IsNullOrEmpty(selector))
            {
                error = "empty selector";
                return result;
            }

            if (index < 0)
            {
                error = "index must be zero or greater; got " + index.ToString(CultureInfo.InvariantCulture);
                return result;
            }

            if (candidates != null)
            {
                foreach (UiElementInfo candidate in candidates)
                {
                    if (candidate == null) continue;
                    // Off-screen elements are never selectable, matching UiSelectorMatcher/
                    // UiPressMatcher (SPEC S3 true-visibility). They are also excluded from the
                    // INDEX SPACE, so an index is stable against whatever crucible_ui_dump shows.
                    if (!candidate.OnScreen) continue;
                    if (!DocumentMatches(candidate.DocumentName, document)) continue;

                    result.AllVisible.Add(candidate);
                    if (UiTreeRenderer.Matches(candidate, selector)) result.Matches.Add(candidate);
                }
            }

            if (result.Matches.Count == 0)
            {
                result.Found = false;
                return result;
            }

            if (index >= result.Matches.Count)
            {
                // Loud, never a fallback to index 0.
                result.Found = false;
                result.OutOfRange = true;
                return result;
            }

            UiElementInfo chosen = result.Matches[index];
            result.Found = true;
            result.Match = chosen;

            string forbiddenReason;
            if (UiForbiddenElements.IsForbidden(chosen.Name, chosen.DocumentName, out forbiddenReason))
            {
                result.Forbidden = true;
                result.ForbiddenReason = forbiddenReason;
            }

            return result;
        }
    }
}
