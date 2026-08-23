using System;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Picks the "lost focus" member out of a live-reflected enum's member-name list, by heuristic
    /// rather than a hardcoded guess — used against <c>InputController.eDisableRequest</c>, the
    /// game's own private enum passed to <c>InputController.RequestDisable</c>/<c>ReleaseDisable</c>
    /// (verified live 2026-08-23 via reflection over the retail FTK2.dll: member names include
    /// <c>ROUTE_CHANGE</c>, <c>LOST_FOCUS</c>, <c>SYSTEM_DIALOG_TRANSITION</c>, and 27 others). Pure
    /// string logic — no Reflection, no game/Unity reference — so it unit-tests with a fake
    /// member-name array standing in for whatever the real enum turns out to contain, and keeps
    /// working (or reports plainly that it can't) if a future game build renames the member.
    ///
    /// Mirrors <see cref="InputBackgroundBehaviorPicker"/>'s heuristic-plus-report-actual-members
    /// shape. The one member matched here must be defeated in isolation — every other
    /// eDisableRequest reason (ROUTE_CHANGE, SYSTEM_DIALOG_TRANSITION, etc.) has to keep disabling
    /// input normally, so a false-positive match here would be a real regression, not just noise.
    /// </summary>
    public static class InputDisableReasonPicker
    {
        /// <summary>
        /// Picks the member meaning "input disabled because the OS window lost focus" — matched by
        /// heuristic (name contains both "Lost" and "Focus", case-insensitive) rather than an exact
        /// hardcoded string, so a differently-cased or differently-ordered future name is still
        /// found. If nothing matches, reports every actual member name rather than failing silently.
        /// </summary>
        public static bool TryPickLostFocusMember(string[] memberNames, out string picked, out string error)
        {
            return TryPickByHeuristic(memberNames, new[] { "Lost", "Focus" }, "meaning 'lost focus'", out picked, out error);
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
