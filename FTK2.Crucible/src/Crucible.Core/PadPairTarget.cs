using System;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_pad_pair</c>'s <c>playerIndex</c> argument: either the literal
    /// <c>"-"</c> (meaning "pair with the primary/default player — index 0", the couch-co-op
    /// convention FTK2's own <c>InputController</c> uses for <c>AssignmentIndex</c>), or a
    /// non-negative integer naming a specific <c>InputPlayer.AssignmentIndex</c> to target.
    /// Rejects everything else outright — a garbage or negative index must never silently fall
    /// back to player 0, since that would silently pair the wrong player.
    /// </summary>
    public static class PadPairTarget
    {
        public const string DefaultToken = "-";

        public static bool TryParse(string raw, out int assignmentIndex, out bool isDefault, out string error)
        {
            assignmentIndex = 0;
            isDefault = false;
            error = null;

            if (raw == null)
            {
                error = "empty playerIndex (expected '" + DefaultToken + "' for the primary player, or a non-negative integer)";
                return false;
            }

            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                error = "empty playerIndex (expected '" + DefaultToken + "' for the primary player, or a non-negative integer)";
                return false;
            }

            if (trimmed == DefaultToken)
            {
                assignmentIndex = 0;
                isDefault = true;
                return true;
            }

            int parsed;
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                error = "invalid playerIndex: '" + raw + "' is neither '" + DefaultToken + "' nor an integer";
                return false;
            }

            if (parsed < 0)
            {
                error = "invalid playerIndex: '" + raw + "' must not be negative";
                return false;
            }

            assignmentIndex = parsed;
            isDefault = false;
            return true;
        }
    }
}
