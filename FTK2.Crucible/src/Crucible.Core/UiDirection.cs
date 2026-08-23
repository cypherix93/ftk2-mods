using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_ui_nav</c>'s direction argument. Pure string logic — no game reference,
    /// no reflection — so it unit-tests with no game running.
    ///
    /// Never silently defaults: an unrecognized direction (e.g. "sideways") is rejected rather than
    /// falling back to Up. A silent default would make a broken/typo'd scenario look like it worked.
    /// </summary>
    public static class UiDirection
    {
        /// <summary>Canonical output is "Up"/"Down"/"Left"/"Right", matching the game's <c>NavigationMoveEvent.Direction</c> enum member names.</summary>
        public static bool TryParse(string raw, out string canonical, out string error)
        {
            canonical = null;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty direction (expected up/down/left/right)";
                return false;
            }

            string trimmed = raw.Trim();
            if (string.Equals(trimmed, "up", StringComparison.OrdinalIgnoreCase)) { canonical = "Up"; return true; }
            if (string.Equals(trimmed, "down", StringComparison.OrdinalIgnoreCase)) { canonical = "Down"; return true; }
            if (string.Equals(trimmed, "left", StringComparison.OrdinalIgnoreCase)) { canonical = "Left"; return true; }
            if (string.Equals(trimmed, "right", StringComparison.OrdinalIgnoreCase)) { canonical = "Right"; return true; }

            error = "invalid direction: '" + raw + "' (expected up/down/left/right)";
            return false;
        }
    }
}
