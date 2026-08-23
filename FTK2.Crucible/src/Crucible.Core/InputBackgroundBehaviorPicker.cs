using System;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Picks a target member out of a live-reflected enum's member-name list, by heuristic rather
    /// than a hardcoded guess — used against
    /// <c>UnityEngine.InputSystem.InputSettings.backgroundBehavior</c>'s enum type
    /// (<c>InputSettings.BackgroundBehavior</c>), whose exact member names are only knowable by
    /// reflecting the live <c>Unity.InputSystem.dll</c> shipped with the game. Pure string logic —
    /// no Reflection, no Unity — so it unit-tests with a fake member-name array standing in for
    /// whatever the real enum turns out to contain, and keeps working (or reports plainly that it
    /// can't) if a future Input System version renames the member.
    /// </summary>
    public static class InputBackgroundBehaviorPicker
    {
        /// <summary>
        /// Picks the member meaning "process injected device input regardless of window focus" —
        /// documented (per SPEC.md / Unity's own docs, as of the version this was written against)
        /// as <c>IgnoreFocus</c>, but matched here by heuristic (name contains both "Ignore" and
        /// "Focus", case-insensitive) rather than an exact hardcoded string, so a differently-cased
        /// or differently-ordered future name is still found. If nothing matches, reports every
        /// actual member name rather than failing silently.
        /// </summary>
        public static bool TryPickIgnoreFocusMember(string[] memberNames, out string picked, out string error)
        {
            return TryPickByHeuristic(memberNames, new[] { "Ignore", "Focus" }, "meaning 'ignore focus'", out picked, out error);
        }

        /// <summary>
        /// Picks the member to fall back to when turning background input back OFF and no earlier
        /// captured original value exists to restore. Prefers a name meaning "reset and disable
        /// non-background devices" (Unity's own documented default), matched by heuristic (contains
        /// "Reset" and "NonBackground"); if that specific shade isn't found, falls back to any
        /// member whose name just contains "Reset". Reports the actual members rather than guessing
        /// if neither heuristic matches.
        /// </summary>
        public static bool TryPickResetDefaultMember(string[] memberNames, out string picked, out string error)
        {
            if (TryPickByHeuristic(memberNames, new[] { "Reset", "NonBackground" }, "meaning 'reset, non-background devices disabled'", out picked, out error))
                return true;

            return TryPickByHeuristic(memberNames, new[] { "Reset" }, "meaning 'reset'", out picked, out error);
        }

        private static bool TryPickByHeuristic(string[] memberNames, string[] mustContainAll, string description, out string picked, out string error)
        {
            picked = null;
            error = null;

            if (memberNames == null || memberNames.Length == 0)
            {
                error = "no enum members supplied";
                return false;
            }

            for (int i = 0; i < memberNames.Length; i++)
            {
                string name = memberNames[i];
                if (string.IsNullOrEmpty(name)) continue;

                bool allPresent = true;
                for (int j = 0; j < mustContainAll.Length; j++)
                {
                    if (name.IndexOf(mustContainAll[j], StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        allPresent = false;
                        break;
                    }
                }
                if (allPresent) { picked = name; return true; }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("no member ").Append(description).Append(" found among: [");
            for (int i = 0; i < memberNames.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(memberNames[i]);
            }
            sb.Append("]");
            error = sb.ToString();
            return false;
        }
    }
}
