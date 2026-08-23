using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_pad_stick</c>'s two arguments: which stick (left/right, matching
    /// <c>GamepadState.leftStick</c>/<c>rightStick</c>) and a direction name mapped to an (x,y)
    /// vector. Pure logic, no game reference — unit-tested with no game running.
    ///
    /// Never silently defaults: an unrecognized stick or direction is rejected rather than falling
    /// back to "center" or "left".
    /// </summary>
    public static class GamepadStick
    {
        public static bool TryParseSide(string raw, out string canonical, out string error)
        {
            canonical = null;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty stick name (expected left or right)";
                return false;
            }

            string t = raw.Trim();
            if (string.Equals(t, "left", StringComparison.OrdinalIgnoreCase)) { canonical = "Left"; return true; }
            if (string.Equals(t, "right", StringComparison.OrdinalIgnoreCase)) { canonical = "Right"; return true; }

            error = "invalid stick: '" + raw + "' (expected left or right)";
            return false;
        }

        /// <summary>"center" maps to (0,0); each direction maps to a distinct non-zero unit vector.</summary>
        public static bool TryParseDirection(string raw, out float x, out float y, out string error)
        {
            x = 0f;
            y = 0f;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty stick direction (expected up/down/left/right/center)";
                return false;
            }

            string t = raw.Trim();
            if (string.Equals(t, "up", StringComparison.OrdinalIgnoreCase)) { x = 0f; y = 1f; return true; }
            if (string.Equals(t, "down", StringComparison.OrdinalIgnoreCase)) { x = 0f; y = -1f; return true; }
            if (string.Equals(t, "left", StringComparison.OrdinalIgnoreCase)) { x = -1f; y = 0f; return true; }
            if (string.Equals(t, "right", StringComparison.OrdinalIgnoreCase)) { x = 1f; y = 0f; return true; }
            if (string.Equals(t, "center", StringComparison.OrdinalIgnoreCase)) { x = 0f; y = 0f; return true; }

            error = "invalid stick direction: '" + raw + "' (expected up/down/left/right/center)";
            return false;
        }
    }
}
