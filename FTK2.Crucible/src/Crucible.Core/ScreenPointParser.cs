using System;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_mouse_click</c>'s two screen-coordinate arguments. Rejects non-numeric or
    /// negative values outright — a garbage coordinate must never silently become 0,0 (which would
    /// click the corner of the screen instead of failing loudly).
    /// </summary>
    public static class ScreenPointParser
    {
        public static bool TryParse(string rawX, string rawY, out float x, out float y, out string error)
        {
            x = 0f;
            y = 0f;
            error = null;

            float parsedX;
            if (!TryParseCoordinate(rawX, "x", out parsedX, out error)) return false;

            float parsedY;
            if (!TryParseCoordinate(rawY, "y", out parsedY, out error)) return false;

            x = parsedX;
            y = parsedY;
            return true;
        }

        private static bool TryParseCoordinate(string raw, string axisName, out float value, out string error)
        {
            value = 0f;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty " + axisName + " coordinate (expected a non-negative number)";
                return false;
            }

            float parsed;
            if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                error = "invalid " + axisName + " coordinate: '" + raw + "' is not a number";
                return false;
            }

            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
            {
                error = "invalid " + axisName + " coordinate: '" + raw + "' is not finite";
                return false;
            }

            if (parsed < 0f)
            {
                error = "invalid " + axisName + " coordinate: '" + raw + "' must be non-negative";
                return false;
            }

            value = parsed;
            return true;
        }
    }
}
