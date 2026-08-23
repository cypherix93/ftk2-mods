using System;
using System.Reflection;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Resolves a single field-or-property member on a plain CLR <see cref="Type"/> by name, and
    /// reads/writes it. Pure reflection (public + non-public, instance + static) — no Harmony, no
    /// game reference — so it unit-tests against ordinary POCOs with no game running.
    ///
    /// This is the write-side counterpart to the walk <c>ReflectionCommands.TryResolvePath</c>
    /// already does for <c>crucible_get</c>: that code resolves a chain of members to a value;
    /// this resolves the *last* segment of a chain to something assignable, for <c>crucible_set</c>.
    /// </summary>
    public static class MemberAccess
    {
        private const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// Finds a field or property named <paramref name="memberName"/> declared on
        /// <paramref name="ownerType"/> (or inherited). Never throws.
        /// </summary>
        public static bool TryResolveMember(Type ownerType, string memberName, out MemberInfo member,
            out Type memberType, out bool isStatic, out bool isReadOnly, out string error)
        {
            member = null;
            memberType = null;
            isStatic = false;
            isReadOnly = false;
            error = null;

            if (ownerType == null) { error = "null owner type"; return false; }
            if (string.IsNullOrEmpty(memberName)) { error = "empty member name"; return false; }

            try
            {
                FieldInfo field = ownerType.GetField(memberName, AllMembers);
                if (field != null)
                {
                    member = field;
                    memberType = field.FieldType;
                    isStatic = field.IsStatic;
                    isReadOnly = field.IsInitOnly || field.IsLiteral;
                    return true;
                }

                PropertyInfo prop = ownerType.GetProperty(memberName, AllMembers);
                if (prop != null)
                {
                    member = prop;
                    memberType = prop.PropertyType;
                    MethodInfo getter = prop.GetGetMethod(true);
                    MethodInfo setter = prop.GetSetMethod(true);
                    isStatic = (getter != null && getter.IsStatic) || (setter != null && setter.IsStatic);
                    isReadOnly = setter == null;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "member lookup threw: " + ex.Message;
                return false;
            }

            error = "member not found on " + ownerType.FullName + ": " + memberName;
            return false;
        }

        /// <summary>Reads the current value of a resolved member. Never throws.</summary>
        public static bool TryGetValue(MemberInfo member, object instance, bool isStatic, out object value, out string error)
        {
            value = null;
            error = null;
            try
            {
                FieldInfo field = member as FieldInfo;
                if (field != null) { value = field.GetValue(isStatic ? null : instance); return true; }

                PropertyInfo prop = member as PropertyInfo;
                if (prop != null) { value = prop.GetValue(isStatic ? null : instance, null); return true; }
            }
            catch (Exception ex)
            {
                error = "get failed: " + ex.Message;
                return false;
            }

            error = "unsupported member kind: " + member.GetType().Name;
            return false;
        }

        /// <summary>
        /// Writes a value to a resolved member. Refuses read-only properties rather than throwing
        /// the framework's own exception, so callers get a consistent error shape.
        /// </summary>
        public static bool TrySetValue(MemberInfo member, object instance, bool isStatic, bool isReadOnly, object value, out string error)
        {
            error = null;

            if (isReadOnly)
            {
                error = "member is read-only: " + member.Name;
                return false;
            }

            try
            {
                FieldInfo field = member as FieldInfo;
                if (field != null) { field.SetValue(isStatic ? null : instance, value); return true; }

                PropertyInfo prop = member as PropertyInfo;
                if (prop != null) { prop.SetValue(isStatic ? null : instance, value, null); return true; }
            }
            catch (Exception ex)
            {
                error = "set failed: " + ex.Message;
                return false;
            }

            error = "unsupported member kind: " + member.GetType().Name;
            return false;
        }
    }
}
