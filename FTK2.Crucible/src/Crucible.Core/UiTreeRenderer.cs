using System;
using System.Collections.Generic;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Renders a flat list of <see cref="UiElementInfo"/> (already walked from the live UIToolkit
    /// tree by the plugin) into <c>crucible_ui_dump</c>'s text output: filtered, capped, with
    /// truncation reported rather than silently dropped. Pure — no game reference — so it
    /// unit-tests with hand-built element lists.
    /// </summary>
    public static class UiTreeRenderer
    {
        public const int DefaultCap = 200;

        /// <summary>
        /// Elements that are not <see cref="UiElementInfo.OnScreen"/> are always skipped from the
        /// rendered output (but counted). Of the remaining on-screen+matching elements, only the
        /// first <paramref name="cap"/> are rendered; <paramref name="truncated"/> reports whether
        /// more existed than fit.
        /// </summary>
        public static string Render(
            IEnumerable<UiElementInfo> elements,
            string filter,
            int cap,
            out int matchedVisibleCount,
            out int skippedInvisibleCount,
            out bool truncated)
        {
            return Render(elements, filter, "-", cap, out matchedVisibleCount, out skippedInvisibleCount, out truncated);
        }

        /// <summary>Overload with the "kinds" type filter (see <see cref="UiKindsFilter"/>) layered on top of the name/text filter.</summary>
        public static string Render(
            IEnumerable<UiElementInfo> elements,
            string filter,
            string kinds,
            int cap,
            out int matchedVisibleCount,
            out int skippedInvisibleCount,
            out bool truncated)
        {
            OnScreenTest.SkipReasonCounts ignoredCounts;
            return Render(elements, filter, kinds, cap, out matchedVisibleCount, out skippedInvisibleCount, out truncated, out ignoredCounts);
        }

        /// <summary>
        /// Overload that also breaks down WHY each off-screen element was excluded (SPEC S3 UI
        /// true-visibility diagnostics) — turns "why is my button missing" into one call instead of
        /// a follow-up round trip.
        /// </summary>
        public static string Render(
            IEnumerable<UiElementInfo> elements,
            string filter,
            string kinds,
            int cap,
            out int matchedVisibleCount,
            out int skippedInvisibleCount,
            out bool truncated,
            out OnScreenTest.SkipReasonCounts skipReasons)
        {
            matchedVisibleCount = 0;
            skippedInvisibleCount = 0;
            truncated = false;
            skipReasons = new OnScreenTest.SkipReasonCounts();
            if (cap < 1) cap = 1;

            bool matchAll = string.IsNullOrEmpty(filter) || filter == "-";

            List<string> lines = new List<string>();
            if (elements != null)
            {
                foreach (UiElementInfo e in elements)
                {
                    if (e == null) continue;

                    if (!e.OnScreen)
                    {
                        skippedInvisibleCount++;
                        skipReasons.Add(e.SkipReason);
                        continue;
                    }

                    if (!UiKindsFilter.Matches(e, kinds)) continue;

                    if (!matchAll && !Matches(e, filter)) continue;

                    matchedVisibleCount++;
                    if (lines.Count < cap) lines.Add(FormatLine(e));
                }
            }

            truncated = matchedVisibleCount > lines.Count;

            StringBuilder sb = new StringBuilder();
            if (lines.Count == 0)
            {
                sb.Append("(no matching visible elements)");
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(lines[i]);
                }
            }

            if (truncated)
            {
                sb.Append("\n... truncated: showing ").Append(lines.Count)
                    .Append(" of ").Append(matchedVisibleCount).Append(" matching visible elements");
            }

            return sb.ToString();
        }

        /// <summary>Case-insensitive substring match against name or text. Filter of "-" is handled by the caller (matches everything).</summary>
        public static bool Matches(UiElementInfo e, string filter)
        {
            if (e == null || string.IsNullOrEmpty(filter)) return false;
            if (e.Name != null && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (e.Text != null && e.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static string FormatLine(UiElementInfo e)
        {
            if (e == null) return "(null)";
            return e.Type + " name=" + Quote(e.Name) + " text=" + Quote(e.Text)
                + " visible=" + e.Visible + " enabled=" + e.Enabled
                + " doc=" + Quote(e.DocumentName)
                + (e.Focused ? " [FOCUSED]" : "");
        }

        private static string Quote(string s)
        {
            return s == null ? "(null)" : "'" + s + "'";
        }
    }
}
