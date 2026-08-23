using System;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_dialogue_advance</c>'s maxPresses argument. Same posture as
    /// <see cref="PadHoldDuration"/>: rejects non-numeric and non-positive values outright, and
    /// clamps anything above <see cref="MaxPresses"/> rather than letting a stray large value burn
    /// an unbounded number of presses against the live game.
    /// </summary>
    public static class DialogueAdvanceBudget
    {
        public const int MaxPresses = 50;

        public static bool TryParse(string raw, out int maxPresses, out bool clamped, out string error)
        {
            maxPresses = 0;
            clamped = false;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty maxPresses (expected a positive integer)";
                return false;
            }

            int parsed;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                error = "invalid maxPresses: '" + raw + "' is not an integer";
                return false;
            }

            if (parsed <= 0)
            {
                error = "invalid maxPresses: '" + raw + "' must be a positive integer";
                return false;
            }

            if (parsed > MaxPresses)
            {
                maxPresses = MaxPresses;
                clamped = true;
                return true;
            }

            maxPresses = parsed;
            return true;
        }
    }
}
