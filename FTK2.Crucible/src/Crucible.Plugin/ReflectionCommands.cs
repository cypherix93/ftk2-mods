using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// The first two <c>crucible_*</c> console commands (SPEC S3 spike): a reflective state reader
    /// and a reflective method invoker, driving the running game the way GameBridge drives
    /// <c>CommandLineHelper</c> — resolved by name, degrading to a reported error rather than a
    /// crash.
    ///
    /// <b>Registration shape is unverified.</b> SPEC.md §0 confirms
    /// <c>CommandLineHelper.RegisterCommand(string, MethodInfo, List&lt;string&gt;, object)</c> exists
    /// and that the arg marshaller understands int/float/double/bool/string/Vector2/Vector3 for
    /// IronOak's own fixed-arity commands, but no first-party command is variadic, so the marshaller's
    /// behavior for a variable argument count has never been observed. Rather than guess at a fixed
    /// arity, both commands here are registered with a single <c>string[] pArgs</c> parameter — the
    /// same shape <c>CommandLineHelper.ExecuteCommand</c> itself already passes through — on the
    /// assumption that a framework built around that signature offers a raw-args escape hatch. This
    /// needs live-game confirmation (deliberately out of scope for this task) before it can be trusted.
    /// </summary>
    internal static class ReflectionCommands
    {
        private static ManualLogSource _log;

        /// <summary>
        /// The most recent crucible_get/crucible_invoke result, rendered as text. Set by the handler
        /// immediately before it returns and read back by RpcServer right after GameBridge.Exec
        /// returns — safe because MainThreadPump serializes all game-thread work, so nothing else can
        /// run between our own Invoke call returning and RpcServer reading this field.
        /// </summary>
        internal static string LastResult;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;

            MethodInfo getHandler = typeof(ReflectionCommands).GetMethod("CrucibleGet", BindingFlags.Public | BindingFlags.Static);
            MethodInfo invokeHandler = typeof(ReflectionCommands).GetMethod("CrucibleInvoke", BindingFlags.Public | BindingFlags.Static);
            MethodInfo setHandler = typeof(ReflectionCommands).GetMethod("CrucibleSet", BindingFlags.Public | BindingFlags.Static);

            GameBridge.RegisterCommand("crucible_get", getHandler, new List<string> { "path" });
            GameBridge.RegisterCommand("crucible_invoke", invokeHandler, new List<string> { "type", "method", "args..." });
            GameBridge.RegisterCommand("crucible_set", setHandler, new List<string> { "path", "value" });
        }

        /// <summary>crucible_get &lt;path&gt; — reflectively reads and renders a dot path.</summary>
        public static void CrucibleGet(string[] pArgs)
        {
            LastResult = null;
            string path = (pArgs != null && pArgs.Length > 0) ? pArgs[0] : null;

            object value; string error;
            if (!TryResolvePath(path, out value, out error))
            {
                LastResult = "error: " + error;
                if (_log != null) _log.LogWarning("crucible_get failed: " + error);
                return;
            }

            LastResult = Render(value);
        }

        /// <summary>crucible_invoke &lt;Type&gt; &lt;Method&gt; [arg1 arg2 ...]</summary>
        public static void CrucibleInvoke(string[] pArgs)
        {
            LastResult = null;
            if (pArgs == null || pArgs.Length < 2)
            {
                LastResult = "error: usage: crucible_invoke <Type> <Method> [args...]";
                return;
            }

            string typeName = pArgs[0];
            string methodName = pArgs[1];
            string[] rawArgs = new string[pArgs.Length - 2];
            Array.Copy(pArgs, 2, rawArgs, 0, rawArgs.Length);

            object result; string strategy; string error;
            if (!TryInvoke(typeName, methodName, rawArgs, out result, out strategy, out error))
            {
                LastResult = "error: " + error;
                if (_log != null) _log.LogWarning("crucible_invoke failed: " + error);
                return;
            }

            LastResult = "[instance: " + strategy + "] " + Render(result);
        }

        // ----------------------------------------------------------------- crucible_set

        /// <summary>
        /// crucible_set &lt;path&gt; &lt;value&gt; — reflectively writes a dot path. The path must
        /// resolve at least a type and a member (e.g. <c>Type.Member</c>); everything but the last
        /// segment is walked the same way <see cref="TryResolvePath"/> walks <c>crucible_get</c>,
        /// then the final segment is resolved as a settable field or property via
        /// <see cref="MemberAccess"/>, the string value coerced to that member's type via
        /// <see cref="ArgCoercion"/>, and assigned. Two discrete string parameters — the
        /// CommandLineHelper arg marshaller does not accept a <c>string[]</c> handler.
        /// </summary>
        public static void CrucibleSet(string pPath, string pValue)
        {
            LastResult = null;

            string[] segments; string error;
            if (!PathParser.TryParse(pPath, out segments, out error))
            {
                LastResult = "error: " + error;
                if (_log != null) _log.LogWarning("crucible_set failed: " + error);
                return;
            }

            if (segments.Length < 2)
            {
                LastResult = "error: path must name a type and a member, e.g. 'Type.Member' (got '" + pPath + "')";
                if (_log != null) _log.LogWarning("crucible_set failed: " + LastResult);
                return;
            }

            Type type;
            try { type = AccessTools.TypeByName(segments[0]); }
            catch (Exception ex) { LastResult = "error: segment[0] '" + segments[0] + "': type lookup threw: " + ex.Message; return; }
            if (type == null)
            {
                LastResult = "error: segment[0] '" + segments[0] + "': type not found";
                if (_log != null) _log.LogWarning("crucible_set failed: " + LastResult);
                return;
            }

            object parent; Type parentType; string walkError;
            if (!WalkSegments(type, null, segments, 1, segments.Length - 1, out parent, out parentType, out walkError))
            {
                LastResult = "error: " + walkError;
                if (_log != null) _log.LogWarning("crucible_set failed: " + walkError);
                return;
            }

            int finalIndex = segments.Length - 1;
            string finalSegment = segments[finalIndex];

            MemberInfo member; Type memberType; bool isStatic; bool isReadOnly; string memberError;
            if (!MemberAccess.TryResolveMember(parentType, finalSegment, out member, out memberType, out isStatic, out isReadOnly, out memberError))
            {
                LastResult = "error: segment[" + finalIndex + "] '" + finalSegment + "': " + memberError;
                if (_log != null) _log.LogWarning("crucible_set failed: " + LastResult);
                return;
            }

            if (!isStatic && parent == null)
            {
                LastResult = "error: segment[" + finalIndex + "] '" + finalSegment + "': null reference (parent value is null)";
                if (_log != null) _log.LogWarning("crucible_set failed: " + LastResult);
                return;
            }

            object oldValue; string getError;
            if (!MemberAccess.TryGetValue(member, parent, isStatic, out oldValue, out getError))
            {
                LastResult = "error: failed reading current value of '" + finalSegment + "': " + getError;
                if (_log != null) _log.LogWarning("crucible_set failed: " + LastResult);
                return;
            }

            object coerced; string coerceError;
            if (!ArgCoercion.TryCoerce(pValue, memberType, out coerced, out coerceError))
            {
                LastResult = "error: cannot coerce '" + pValue + "' to " + memberType.Name + ": " + coerceError;
                if (_log != null) _log.LogWarning("crucible_set failed: " + LastResult);
                return;
            }

            string setError;
            if (!MemberAccess.TrySetValue(member, parent, isStatic, isReadOnly, coerced, out setError))
            {
                LastResult = "error: " + setError;
                if (_log != null) _log.LogWarning("crucible_set failed: " + setError);
                return;
            }

            LastResult = "old=" + Render(oldValue) + " new=" + Render(coerced);
        }

        // ----------------------------------------------------------------- crucible_get

        /// <summary>
        /// Walks a dot path segment by segment: the first segment resolves as a type via
        /// AccessTools.TypeByName, each following segment as a static-or-instance field/property.
        /// Never throws — every failure reports the segment that failed.
        /// </summary>
        internal static bool TryResolvePath(string path, out object result, out string error)
        {
            result = null;
            error = null;

            string[] segments;
            if (!PathParser.TryParse(path, out segments, out error)) return false;

            Type type;
            try { type = AccessTools.TypeByName(segments[0]); }
            catch (Exception ex) { error = "segment[0] '" + segments[0] + "': type lookup threw: " + ex.Message; return false; }
            if (type == null) { error = "segment[0] '" + segments[0] + "': type not found"; return false; }

            Type resultType;
            return WalkSegments(type, null, segments, 1, segments.Length, out result, out resultType, out error);
        }

        /// <summary>
        /// Walks <paramref name="segments"/>[<paramref name="fromIndex"/>..<paramref name="toIndexExclusive"/>)
        /// as field/property member reads, starting from <paramref name="startType"/> /
        /// <paramref name="startInstance"/>. Shared by <see cref="TryResolvePath"/> (walks the whole
        /// path, for crucible_get) and <see cref="CrucibleSet"/> (walks up to but not including the
        /// final segment, which is resolved separately as an assignment target). Never throws.
        /// </summary>
        private static bool WalkSegments(Type startType, object startInstance, string[] segments, int fromIndex, int toIndexExclusive,
            out object result, out Type resultType, out string error)
        {
            result = null;
            resultType = startType;
            error = null;

            object current = startInstance;
            Type currentType = startType;

            for (int i = fromIndex; i < toIndexExclusive; i++)
            {
                string seg = segments[i];
                try
                {
                    FieldInfo field = AccessTools.Field(currentType, seg);
                    if (field != null)
                    {
                        if (!field.IsStatic && current == null)
                        {
                            error = "segment[" + i + "] '" + seg + "': null reference (parent value is null)";
                            return false;
                        }
                        current = field.GetValue(current);
                        currentType = current != null ? current.GetType() : field.FieldType;
                        continue;
                    }

                    PropertyInfo prop = AccessTools.Property(currentType, seg);
                    if (prop == null)
                    {
                        error = "segment[" + i + "] '" + seg + "': member not found on " + currentType.FullName;
                        return false;
                    }

                    MethodInfo getter = prop.GetGetMethod(true);
                    bool isStatic = getter != null && getter.IsStatic;
                    if (!isStatic && current == null)
                    {
                        error = "segment[" + i + "] '" + seg + "': null reference (parent value is null)";
                        return false;
                    }
                    current = prop.GetValue(isStatic ? null : current, null);
                    currentType = current != null ? current.GetType() : prop.PropertyType;
                }
                catch (Exception ex)
                {
                    error = "segment[" + i + "] '" + seg + "' threw: " + ex.Message;
                    return false;
                }
            }

            result = current;
            resultType = currentType;
            return true;
        }

        // ----------------------------------------------------------------- crucible_invoke

        /// <summary>
        /// Resolves Type.Method(args) reflectively, including private methods, coerces string args
        /// to the target parameter types, and resolves an instance target (for instance methods) via
        /// three strategies in order: a static Instance/Current member, FindObjectOfType, then a
        /// private field on the live RouterMono instance. Never throws.
        /// </summary>
        internal static bool TryInvoke(string typeName, string methodName, string[] rawArgs, out object returnValue, out string instanceStrategy, out string error)
        {
            returnValue = null;
            instanceStrategy = null;
            error = null;

            Type type;
            try { type = AccessTools.TypeByName(typeName); }
            catch (Exception ex) { error = "type lookup threw: " + ex.Message; return false; }
            if (type == null) { error = "type not found: " + typeName; return false; }

            MethodInfo method = FindMethod(type, methodName, rawArgs.Length);
            if (method == null)
            {
                error = "method not found: " + typeName + "." + methodName + " with " + rawArgs.Length + " arg(s)";
                return false;
            }

            object instance = null;
            if (!method.IsStatic)
            {
                if (!TryResolveInstance(type, out instance, out instanceStrategy, out error)) return false;
            }
            else
            {
                instanceStrategy = "static (no instance needed)";
            }

            ParameterInfo[] parameters = method.GetParameters();
            object[] coerced = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                object value; string coerceError;
                if (!ArgCoercion.TryCoerce(rawArgs[i], parameters[i].ParameterType, out value, out coerceError))
                {
                    error = "arg[" + i + "] ('" + rawArgs[i] + "') for parameter '" + parameters[i].Name
                        + "' (" + parameters[i].ParameterType.Name + "): " + coerceError;
                    return false;
                }
                coerced[i] = value;
            }

            object invokeResult;
            try
            {
                invokeResult = method.Invoke(instance, coerced);
            }
            catch (TargetInvocationException ex)
            {
                error = "method threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "invoke failed: " + ex.Message;
                return false;
            }

            if (IsTask(invokeResult))
            {
                string status = ReadTaskStatus(invokeResult);
                returnValue = "<Task> status=" + status + " (not awaited)";
                return true;
            }

            returnValue = invokeResult;
            return true;
        }

        private static MethodInfo FindMethod(Type type, string methodName, int argCount)
        {
            MethodInfo[] candidates = type.GetMethods(AccessTools.all);
            for (int i = 0; i < candidates.Length; i++)
            {
                MethodInfo m = candidates[i];
                if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                if (m.GetParameters().Length == argCount) return m;
            }
            return null;
        }

        /// <summary>
        /// (a) a static property/field on the type named Instance/Current, (b)
        /// UnityEngine.Object.FindObjectOfType(type), (c) a private field on the live RouterMono
        /// instance whose type matches — Directors are held this way (_adventureDirector, etc.).
        /// </summary>
        private static bool TryResolveInstance(Type type, out object instance, out string strategy, out string error)
        {
            instance = null;
            strategy = null;
            error = null;

            try
            {
                PropertyInfo instProp = AccessTools.Property(type, "Instance") ?? AccessTools.Property(type, "Current");
                if (instProp != null)
                {
                    object v = instProp.GetValue(null, null);
                    if (v != null) { instance = v; strategy = "static property " + instProp.Name; return true; }
                }

                FieldInfo instField = AccessTools.Field(type, "Instance") ?? AccessTools.Field(type, "Current");
                if (instField != null && instField.IsStatic)
                {
                    object v = instField.GetValue(null);
                    if (v != null) { instance = v; strategy = "static field " + instField.Name; return true; }
                }
            }
            catch (Exception)
            {
                // Fall through to the next strategy.
            }

            try
            {
                Type unityObject = AccessTools.TypeByName("UnityEngine.Object");
                MethodInfo findObjectOfType = unityObject == null ? null
                    : AccessTools.Method(unityObject, "FindObjectOfType", new[] { typeof(Type) });
                if (findObjectOfType != null)
                {
                    object v = findObjectOfType.Invoke(null, new object[] { type });
                    if (v != null) { instance = v; strategy = "UnityEngine.Object.FindObjectOfType"; return true; }
                }
            }
            catch (Exception)
            {
                // Fall through to the next strategy.
            }

            try
            {
                Type routerMonoType = AccessTools.TypeByName("RouterMono");
                Type unityObject = AccessTools.TypeByName("UnityEngine.Object");
                if (routerMonoType != null && unityObject != null)
                {
                    MethodInfo findObjectOfType = AccessTools.Method(unityObject, "FindObjectOfType", new[] { typeof(Type) });
                    object router = findObjectOfType == null ? null : findObjectOfType.Invoke(null, new object[] { routerMonoType });
                    if (router != null)
                    {
                        FieldInfo[] fields = routerMonoType.GetFields(AccessTools.all);
                        for (int i = 0; i < fields.Length; i++)
                        {
                            if (fields[i].IsStatic) continue;
                            if (!type.IsAssignableFrom(fields[i].FieldType)) continue;
                            object v = fields[i].GetValue(router);
                            if (v != null) { instance = v; strategy = "RouterMono field '" + fields[i].Name + "'"; return true; }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // All three strategies exhausted.
            }

            error = "no instance resolvable for " + type.FullName + " (tried Instance/Current, FindObjectOfType, RouterMono fields)";
            return false;
        }

        private static bool IsTask(object value)
        {
            if (value == null) return false;
            Type taskType = AccessTools.TypeByName("System.Threading.Tasks.Task");
            return taskType != null && taskType.IsInstanceOfType(value);
        }

        private static string ReadTaskStatus(object task)
        {
            try
            {
                PropertyInfo statusProp = AccessTools.Property(task.GetType(), "Status");
                object status = statusProp == null ? null : statusProp.GetValue(task, null);
                return status == null ? "unknown" : status.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        // ----------------------------------------------------------------- rendering

        /// <summary>
        /// Primitives as-is; collections as Count=N plus the first up-to-5 elements' ToString();
        /// objects as their type name plus a public field/prop summary. Best-effort, matching
        /// StateReader's posture: a stringification failure never throws out of this method.
        /// </summary>
        internal static string Render(object value)
        {
            if (value == null) return "null";

            Type t = value.GetType();
            if (t.IsPrimitive || value is string || value is decimal || t.IsEnum) return SafeToString(value);

            if (value is IEnumerable)
            {
                IEnumerable enumerable = (IEnumerable)value;
                int count = 0;
                List<string> sample = new List<string>();
                foreach (object item in enumerable)
                {
                    count++;
                    if (sample.Count < 5) sample.Add(item == null ? "null" : SafeToString(item));
                }
                string suffix = count > sample.Count ? ", ..." : "";
                return "Count=" + count + " [" + string.Join(", ", sample.ToArray()) + suffix + "]";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(t.Name).Append(" { ");
            bool first = true;

            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(fields[i].Name).Append('=').Append(SafeMemberValue(delegate { return fields[i].GetValue(value); }));
            }

            PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < props.Length; i++)
            {
                if (props[i].GetIndexParameters().Length > 0) continue; // indexers aren't summarizable this way
                if (!first) sb.Append(", ");
                first = false;
                PropertyInfo p = props[i];
                sb.Append(p.Name).Append('=').Append(SafeMemberValue(delegate { return p.GetValue(value, null); }));
            }

            sb.Append(" }");
            return sb.ToString();
        }

        private static string SafeMemberValue(Func<object> getter)
        {
            try
            {
                object v = getter();
                return v == null ? "null" : SafeToString(v);
            }
            catch (Exception ex)
            {
                return "<threw: " + ex.Message + ">";
            }
        }

        private static string SafeToString(object value)
        {
            try { return value.ToString(); }
            catch (Exception ex) { return "<ToString threw: " + ex.Message + ">"; }
        }
    }
}
