using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_pad</c>/<c>crucible_pad_hold</c>'s button argument into the canonical
    /// <c>UnityEngine.InputSystem.LowLevel.GamepadButton</c> member name. Verified reflectively
    /// against the retail <c>Unity.InputSystem.dll</c> 2026-08-23 — the actual enum members are:
    /// DpadUp=0, DpadDown=1, DpadLeft=2, DpadRight=3, North/Y/Triangle=4, East/B/Circle=5,
    /// South/A/Cross=6, West/X/Square=7, LeftStick=8, RightStick=9, LeftShoulder=10,
    /// RightShoulder=11, Start=12, Select=13, LeftTrigger=32, RightTrigger=33. This maps the
    /// friendly command-line names onto that canonical set.
    ///
    /// Never silently defaults: an unrecognized name is rejected and the valid names are listed —
    /// matching <see cref="UiDirection"/>'s posture (a typo must never look like it worked).
    /// </summary>
    public static class GamepadButtonName
    {
        internal const string ValidNames = "a/south, b/east, x/west, y/north, up, down, left, right, start, select, lb, rb";

        public static bool TryParse(string raw, out string canonical, out string error)
        {
            canonical = null;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty button name (expected one of: " + ValidNames + ")";
                return false;
            }

            string t = raw.Trim();
            if (string.Equals(t, "a", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "south", StringComparison.OrdinalIgnoreCase))
            {
                canonical = "South"; return true;
            }
            if (string.Equals(t, "b", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "east", StringComparison.OrdinalIgnoreCase))
            {
                canonical = "East"; return true;
            }
            if (string.Equals(t, "x", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "west", StringComparison.OrdinalIgnoreCase))
            {
                canonical = "West"; return true;
            }
            if (string.Equals(t, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "north", StringComparison.OrdinalIgnoreCase))
            {
                canonical = "North"; return true;
            }
            if (string.Equals(t, "up", StringComparison.OrdinalIgnoreCase)) { canonical = "DpadUp"; return true; }
            if (string.Equals(t, "down", StringComparison.OrdinalIgnoreCase)) { canonical = "DpadDown"; return true; }
            if (string.Equals(t, "left", StringComparison.OrdinalIgnoreCase)) { canonical = "DpadLeft"; return true; }
            if (string.Equals(t, "right", StringComparison.OrdinalIgnoreCase)) { canonical = "DpadRight"; return true; }
            if (string.Equals(t, "start", StringComparison.OrdinalIgnoreCase)) { canonical = "Start"; return true; }
            if (string.Equals(t, "select", StringComparison.OrdinalIgnoreCase)) { canonical = "Select"; return true; }
            if (string.Equals(t, "lb", StringComparison.OrdinalIgnoreCase)) { canonical = "LeftShoulder"; return true; }
            if (string.Equals(t, "rb", StringComparison.OrdinalIgnoreCase)) { canonical = "RightShoulder"; return true; }

            error = "invalid button: '" + raw + "' (expected one of: " + ValidNames + ")";
            return false;
        }
    }
}
