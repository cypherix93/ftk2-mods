using System;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Coerces a console-style string argument to a target CLR type for <c>crucible_invoke</c>.
    /// Pure — operates on BCL <see cref="Type"/> values only, no game reference — so it unit-tests
    /// against ordinary types (int, bool, enums) with no game running.
    ///
    /// Never silently defaults: a non-numeric string targeting <c>int</c> is a failure, not a 0. A
    /// silent 0 would make a mis-typed argument invoke the right method with the wrong data instead
    /// of refusing outright.
    /// </summary>
    public static class ArgCoercion
    {
        public static bool TryCoerce(string raw, Type targetType, out object value, out string error)
        {
            value = null;
            error = null;

            if (targetType == null)
            {
                error = "null target type";
                return false;
            }

            if (raw == null)
            {
                error = "missing argument for type " + targetType.Name;
                return false;
            }

            // System.Object parameters carry optional payloads in this game's API surface
            // (RouterMono.Route's pCustomData is the motivating case). There is no sensible way to
            // build an arbitrary object from a console string, so the only supported values are the
            // null tokens; anything else is refused rather than silently passed as a string, which
            // would look like it worked and then behave differently from the real call site.
            if (targetType == typeof(object))
            {
                if (raw == null || raw == "-" || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                {
                    value = null;
                    return true;
                }
                error = "object parameters accept only 'null' or '-'; got: " + raw;
                return false;
            }

            if (targetType == typeof(string))
            {
                value = raw;
                return true;
            }

            if (targetType == typeof(int))
            {
                int i;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                {
                    error = "not a valid int: " + raw;
                    return false;
                }
                value = i;
                return true;
            }

            if (targetType == typeof(float))
            {
                float f;
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                {
                    error = "not a valid float: " + raw;
                    return false;
                }
                value = f;
                return true;
            }

            if (targetType == typeof(double))
            {
                double d;
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    error = "not a valid double: " + raw;
                    return false;
                }
                value = d;
                return true;
            }

            if (targetType == typeof(bool))
            {
                bool b;
                if (!bool.TryParse(raw, out b))
                {
                    error = "not a valid bool: " + raw;
                    return false;
                }
                value = b;
                return true;
            }

            if (targetType.IsEnum)
            {
                try
                {
                    value = Enum.Parse(targetType, raw, true);
                    return true;
                }
                catch (Exception)
                {
                    error = "not a valid " + targetType.Name + " value: " + raw;
                    return false;
                }
            }

            error = "unsupported target type: " + targetType.FullName;
            return false;
        }
    }
}
