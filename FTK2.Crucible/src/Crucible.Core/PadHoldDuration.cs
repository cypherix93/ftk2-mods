using System;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_pad_hold</c>'s milliseconds argument. Rejects non-numeric and
    /// non-positive values outright (a negative, zero, or garbage hold duration must never
    /// silently become a no-op or a default), and clamps anything above <see cref="MaxMs"/> rather
    /// than letting a stray large value hang the game for an unbounded time.
    /// </summary>
    public static class PadHoldDuration
    {
        public const int MaxMs = 5000;

        public static bool TryParse(string raw, out int ms, out bool clamped, out string error)
        {
            ms = 0;
            clamped = false;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty duration (expected a positive integer number of milliseconds)";
                return false;
            }

            int parsed;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                error = "invalid duration: '" + raw + "' is not an integer";
                return false;
            }

            if (parsed <= 0)
            {
                error = "invalid duration: '" + raw + "' must be a positive number of milliseconds";
                return false;
            }

            if (parsed > MaxMs)
            {
                ms = MaxMs;
                clamped = true;
                return true;
            }

            ms = parsed;
            return true;
        }
    }
}
