using System;
using System.Collections.Generic;
using System.Reflection;

namespace TypeProbe
{
    /// <summary>One described member of a game type.</summary>
    public struct MemberEntry
    {
        public string Kind;      // "field" | "prop" | "method"
        public string TypeName;  // field/property type, or method return type
        public string Name;
        /// <summary>Parameter list for methods, e.g. "(String runId, GameRunData run)". Empty for
        /// fields and properties. Without this a caller must GUESS an overload, which is the
        /// single most common way reflection against this game silently binds the wrong member.</summary>
        public string Signature;
    }

    /// <summary>The result of describing one type. Never null; check <see cref="Found"/>.</summary>
    public sealed class ProbeResult
    {
        public string RequestedName;
        public bool Found;
        public string FullName;
        public List<MemberEntry> Members = new List<MemberEntry>();
    }

    /// <summary>
    /// Pure member description over a set of types. No file I/O and no game dependency, so it can
    /// be driven with deliberately wrong inputs — which is what makes the negative controls
    /// meaningful. Output is ordinal-sorted so two runs on two machines produce identical field
    /// maps; a stability-only guarantee would wave through a machine-dependent enumeration order.
    /// </summary>
    public static class Probe
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static ProbeResult Describe(IEnumerable<Type> types, string simpleName, bool includeMethods)
        {
            return Describe(types, simpleName, includeMethods, false);
        }

        public static ProbeResult Describe(IEnumerable<Type> types, string simpleName, bool includeMethods, bool includeSignatures)
        {
            ProbeResult result = new ProbeResult();
            result.RequestedName = simpleName;
            result.Found = false;

            if (types == null || string.IsNullOrEmpty(simpleName)) return result;

            Type target = null;
            foreach (Type t in types)
            {
                if (t == null) continue;
                if (string.Equals(t.Name, simpleName, StringComparison.Ordinal)) { target = t; break; }
            }
            if (target == null) return result;

            result.Found = true;
            result.FullName = target.FullName;

            foreach (FieldInfo f in target.GetFields(Flags))
                result.Members.Add(Entry("field", SafeTypeName(f.FieldType), f.Name));

            foreach (PropertyInfo p in target.GetProperties(Flags))
                result.Members.Add(Entry("prop", SafeTypeName(p.PropertyType), p.Name));

            if (includeMethods)
            {
                foreach (MethodInfo m in target.GetMethods(Flags))
                {
                    if (m.IsSpecialName) continue; // property accessors are already reported as props
                    MemberEntry me = Entry("method", SafeTypeName(m.ReturnType), m.Name);
                    if (includeSignatures) me.Signature = DescribeParameters(m);
                    result.Members.Add(me);
                }
            }

            result.Members.Sort(delegate (MemberEntry a, MemberEntry b)
            {
                int byKind = string.CompareOrdinal(a.Kind, b.Kind);
                if (byKind != 0) return byKind;
                int byName = string.CompareOrdinal(a.Name, b.Name);
                if (byName != 0) return byName;
                return string.CompareOrdinal(a.TypeName, b.TypeName);
            });

            return result;
        }

        /// <summary>
        /// Renders a method's parameter list. Overloads share a name, so the name alone cannot
        /// identify which member to bind — this is what distinguishes them.
        /// </summary>
        private static string DescribeParameters(MethodInfo m)
        {
            try
            {
                ParameterInfo[] ps = m.GetParameters();
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append('(');
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(SafeTypeName(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
                }
                sb.Append(')');
                if (m.IsStatic) sb.Append(" static");
                return sb.ToString();
            }
            catch (Exception) { return "(unreadable)"; }
        }

        private static MemberEntry Entry(string kind, string typeName, string name)
        {
            MemberEntry e = new MemberEntry();
            e.Kind = kind;
            e.TypeName = typeName;
            e.Name = name;
            e.Signature = "";
            return e;
        }

        /// <summary>A member whose type failed to load must not take the whole dump down.</summary>
        private static string SafeTypeName(Type t)
        {
            try { return t == null ? "?" : t.Name; }
            catch (Exception) { return "?"; }
        }
    }
}
