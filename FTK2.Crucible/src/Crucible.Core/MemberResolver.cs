using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Reflection primitives with a warning contract: every lookup that fails reports into a
    /// <see cref="WarningSink"/> rather than returning a quiet null (SPEC §2 corollary).
    ///
    /// Host-agnostic on purpose — <c>System.Reflection</c> is part of netstandard2.0, so the two
    /// genuinely subtle behaviours (walking base types for non-public members, and choosing among
    /// overloads) are unit-testable against fake types with no game running.
    /// </summary>
    public static class MemberResolver
    {
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Field first, then property. Both halves matter to real game types: <c>Entity.Guid</c> is a
        /// property whose backing field is named <c>&lt;Guid&gt;k__BackingField</c>, so a field-only
        /// lookup finds nothing; <c>CombatPhase._activeCharacterEntity</c> is a property despite the
        /// leading underscore.
        /// </summary>
        public static object GetMember(object instance, string memberName, WarningSink warnings)
        {
            if (instance == null || string.IsNullOrEmpty(memberName)) return null;
            Type type = instance.GetType();
            try
            {
                FieldInfo field = FindField(type, memberName);
                if (field != null) return field.GetValue(instance);

                PropertyInfo prop = FindProperty(type, memberName);
                if (prop != null && prop.CanRead) return prop.GetValue(instance, null);

                if (warnings != null) warnings.MemberMissing(type.Name, memberName);
                return null;
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.MemberThrew(type.Name, memberName, Unwrap(ex).Message);
                return null;
            }
        }

        /// <summary>DeclaredOnly, one level at a time: BindingFlags.FlattenHierarchy does not surface
        /// non-public members of base types, and game state classes inherit plenty of them.</summary>
        public static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, AnyInstance);
                if (f != null) return f;
            }
            return null;
        }

        public static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, AnyInstance);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>
        /// Finds the one static method named <paramref name="methodName"/> taking a single parameter
        /// that accepts <paramref name="arg"/>. Selection is by runtime assignability, never by a
        /// written signature, so the caller only needs to have grounded the member *name*.
        ///
        /// <c>CharacterHelper.GetStat</c> has 8 overloads (field map), so refusing is a real outcome:
        /// two unrelated candidates yield null plus a <c>member_ambiguous</c> warning rather than a
        /// coin flip.
        /// </summary>
        public static MethodInfo FindUnaryStatic(Type declaring, string methodName, object arg, WarningSink warnings)
        {
            if (declaring == null || string.IsNullOrEmpty(methodName) || arg == null) return null;

            List<MethodInfo> matches = new List<MethodInfo>();
            for (Type t = declaring; t != null; t = t.BaseType)
            {
                MethodInfo[] all = t.GetMethods(AnyStatic);
                for (int i = 0; i < all.Length; i++)
                {
                    MethodInfo m = all[i];
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                    if (m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(arg)) continue;
                    matches.Add(m);
                }
            }

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
            {
                if (warnings != null)
                    warnings.MemberMissing(declaring.Name,
                        methodName + "(<one parameter accepting " + arg.GetType().Name + ">)");
                return null;
            }

            MethodInfo best = MostDerived(matches);
            if (best != null) return best;

            if (warnings != null)
                warnings.Ambiguous(declaring.Name, methodName, matches.Count, arg.GetType().Name);
            return null;
        }

        /// <summary>
        /// The candidate whose parameter type is assignable to every other candidate's — the most
        /// derived one. Null when no single candidate dominates (two unrelated interfaces), which is
        /// an ambiguity the caller must refuse rather than guess at.
        /// </summary>
        private static MethodInfo MostDerived(List<MethodInfo> candidates)
        {
            MethodInfo best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                Type ci = candidates[i].GetParameters()[0].ParameterType;
                bool dominatesAll = true;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (i == j) continue;
                    Type cj = candidates[j].GetParameters()[0].ParameterType;
                    if (!cj.IsAssignableFrom(ci)) { dominatesAll = false; break; }
                }
                if (!dominatesAll) continue;
                if (best != null) return null;
                best = candidates[i];
            }
            return best;
        }

        public static object InvokeStatic(MethodInfo method, object arg, string label, WarningSink warnings)
        {
            if (method == null) return null;
            try
            {
                return method.Invoke(null, new object[] { arg });
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.Add("invoke_failed: " + label + ": " + Unwrap(ex).Message);
                return null;
            }
        }

        /// <summary>
        /// Finds a component by type name inside <c>Entity.Components</c>.
        ///
        /// Scans values, not keys: the field map records <c>Components</c> as a <c>Dictionary`2</c>
        /// without telling us what the key is, and the values carry their own types. Base-type names
        /// match too, so a subclassed component still resolves.
        /// </summary>
        public static object FindComponentByTypeName(object componentMap, string typeName, WarningSink warnings)
        {
            IDictionary map = componentMap as IDictionary;
            if (map == null)
            {
                if (warnings != null)
                    warnings.Note("component map unavailable (expected IDictionary, got "
                        + (componentMap == null ? "null" : componentMap.GetType().Name) + ")");
                return null;
            }

            foreach (object value in map.Values)
            {
                if (value == null) continue;
                if (TypeNameMatches(value.GetType(), typeName)) return value;
            }

            if (warnings != null) warnings.Note("component not present: " + typeName);
            return null;
        }

        public static bool TypeNameMatches(Type type, string typeName)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (string.Equals(t.Name, typeName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static int? AsInt(object value)
        {
            if (value == null) return null;
            if (value is int) return (int)value;
            if (value is long || value is short || value is byte || value is uint || value is Enum)
            {
                try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
                catch (Exception) { return null; }
            }
            return null;
        }

        public static string AsString(object value)
        {
            if (value == null) return null;
            string s = value as string;
            return s != null ? s : value.ToString();
        }

        public static bool? AsBool(object value)
        {
            if (value is bool) return (bool)value;
            return null;
        }

        private static Exception Unwrap(Exception ex)
        {
            TargetInvocationException tie = ex as TargetInvocationException;
            return tie != null && tie.InnerException != null ? tie.InnerException : ex;
        }
    }
}
