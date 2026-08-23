using System;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Pure argument parsers for the crucible_* debug/cheat verbs (DebugVerbCommands, ChaosCommands
    /// in Crucible.Plugin). No game reference -- unit-tests with no game running, same posture as
    /// ArgCoercion/PathParser. Every parser refuses rather than defaults: an unknown phase name, a
    /// non-numeric level/seed/qty, or an out-of-range slot must be a rejected command, never a
    /// silently-substituted value.
    /// </summary>
    public static class PhaseName
    {
        /// <summary>
        /// Maps a phase-name argument to the live Type name that owns its <c>_debugEndPhase</c> (see
        /// docs/research/crucible-traversal-inventory.md §2). RestPhase is flagged separately: it is
        /// the one phase whose <c>_debugEndPhase</c> takes an <c>Int32 pOption</c> instead of no
        /// arguments -- a dispatcher assuming a uniform zero-arg overload breaks on it.
        /// </summary>
        public static bool TryParse(string raw, out string typeName, out bool needsOption, out string error)
        {
            typeName = null;
            needsOption = false;
            error = null;

            if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
            {
                error = "missing phase name";
                return false;
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "combat": typeName = "CombatPhase"; return true;
                case "encounter": typeName = "EncounterPhase"; return true;
                case "fortune": typeName = "FortunePhase"; return true;
                case "treasure": typeName = "TreasurePhase"; return true;
                case "trap": typeName = "TrapPhase"; return true;
                case "wheel": typeName = "WheelPhase"; return true;
                case "rest": typeName = "RestPhase"; needsOption = true; return true;
                default:
                    error = "unknown phase '" + raw + "'. Expected one of: combat, encounter, fortune, "
                        + "treasure, trap, wheel, rest";
                    return false;
            }
        }
    }

    public static class IntArg
    {
        public static bool TryParse(string raw, string argName, out int value, out string error)
        {
            value = 0;
            error = null;
            if (string.IsNullOrEmpty(raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = argName + " must be an integer, got '" + raw + "'";
                return false;
            }
            return true;
        }

        public static bool TryParsePositive(string raw, string argName, out int value, out string error)
        {
            if (!TryParse(raw, argName, out value, out error)) return false;
            if (value < 1)
            {
                error = argName + " must be >= 1, got " + value.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }

        public static bool TryParseNonNegative(string raw, string argName, out int value, out string error)
        {
            if (!TryParse(raw, argName, out value, out error)) return false;
            if (value < 0)
            {
                error = argName + " must be >= 0, got " + value.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }
    }

    public static class PartySlotArg
    {
        /// <summary>FTK2 parties top out well below this; kept generous but bounded so a typo
        /// ("350" for a level meant elsewhere) is refused rather than silently accepted as a slot.</summary>
        public const int MaxSlot = 5;

        public static bool TryParse(string raw, out int slot, out string error)
        {
            if (!IntArg.TryParseNonNegative(raw, "slot", out slot, out error)) return false;
            if (slot > MaxSlot)
            {
                error = "slot must be between 0 and " + MaxSlot.ToString(CultureInfo.InvariantCulture)
                    + ", got " + slot.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }
    }

    public static class BoolToggleArg
    {
        public static bool TryParse(string raw, out bool value, out string error)
        {
            value = false;
            error = null;
            if (string.IsNullOrEmpty(raw)) { error = "missing on/off argument"; return false; }
            string t = raw.Trim().ToLowerInvariant();
            if (t == "on" || t == "true" || t == "1") { value = true; return true; }
            if (t == "off" || t == "false" || t == "0") { value = false; return true; }
            error = "expected on/off (or true/false/1/0), got '" + raw + "'";
            return false;
        }
    }

    /// <summary>
    /// Pure decision behind ChaosCommands' Harmony gate: whether the live chaos-mutating call
    /// (<c>AdventureHelper.ModifyChaosLevel</c>) should be skipped. Extracted from the Harmony patch
    /// itself so the gating LOGIC has an offline negative control: see
    /// DebugVerbArgsTests "ChaosGate" -- a simulated counter held while frozen, and moving again once
    /// unfrozen, proves the gate actually suppresses an increment rather than merely existing.
    /// </summary>
    public static class ChaosGate
    {
        public static bool ShouldSkipOriginal(bool frozen)
        {
            return frozen;
        }
    }
}
