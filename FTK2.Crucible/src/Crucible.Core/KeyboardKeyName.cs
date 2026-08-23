using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses <c>crucible_key</c>'s key argument into the canonical
    /// <c>UnityEngine.InputSystem.Key</c> member name.
    ///
    /// Keyboard driving exists because the virtual GAMEPAD cannot be made to work: FTK2 routes input
    /// through devices that are paired to its single <c>InputPlayer</c>, and a gamepad has to be
    /// CREATED (<c>InputSystem.AddDevice&lt;Gamepad&gt;</c>) because no physical pad is attached — so
    /// it is never paired, and presses are ignored. A real Keyboard and Mouse already exist and are
    /// already paired (verified live 2026-08-23: both report activated=True against
    /// InputPlayer(AssignmentIndex=-1)), which is exactly why mouse injection has always worked.
    /// Driving <c>Keyboard.current</c> therefore sidesteps the pairing problem entirely rather than
    /// trying to solve it.
    ///
    /// Never silently defaults: an unrecognized alias falls through to a pass-through of the raw
    /// name so any Key member can be reached, but an EMPTY argument is rejected — matching
    /// <see cref="GamepadButtonName"/>'s posture that a typo must never look like it worked.
    /// </summary>
    public static class KeyboardKeyName
    {
        internal const string CommonNames =
            "up, down, left, right, enter/submit, escape/cancel/back, space, tab, backspace, and any Key member name";

        public static bool TryParse(string raw, out string canonical, out string error)
        {
            canonical = null;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "empty key name (expected one of: " + CommonNames + ")";
                return false;
            }

            string t = raw.Trim();

            if (Eq(t, "up")) { canonical = "UpArrow"; return true; }
            if (Eq(t, "down")) { canonical = "DownArrow"; return true; }
            if (Eq(t, "left")) { canonical = "LeftArrow"; return true; }
            if (Eq(t, "right")) { canonical = "RightArrow"; return true; }
            if (Eq(t, "enter") || Eq(t, "submit") || Eq(t, "return") || Eq(t, "a")) { canonical = "Enter"; return true; }
            if (Eq(t, "escape") || Eq(t, "esc") || Eq(t, "cancel") || Eq(t, "back") || Eq(t, "b")) { canonical = "Escape"; return true; }
            if (Eq(t, "space")) { canonical = "Space"; return true; }
            if (Eq(t, "tab")) { canonical = "Tab"; return true; }
            if (Eq(t, "backspace")) { canonical = "Backspace"; return true; }

            // Pass through: the Key enum has ~110 members and enumerating them here would rot.
            // An invalid name is caught at Enum.Parse, which reports the real member list.
            canonical = t;
            return true;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
