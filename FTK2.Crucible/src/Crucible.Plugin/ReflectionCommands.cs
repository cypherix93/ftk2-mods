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

        private static bool _registered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Registers the probe commands, once, as soon as the game's command system is alive.
        ///
        /// Registration cannot happen in Awake. BepInEx runs plugin Awake during chainloader
        /// startup, but CommandLineHelper.Initialize is not called until RouterMono starts, so the
        /// registry the game keeps internally is still null. Calling RegisterCommand before that
        /// throws NullReferenceException from inside the game (verified in-game 2026-08-23:
        /// NRE at CommandLineHelper.RegisterCommand IL_0015, with a perfectly valid handler).
        ///
        /// So this is driven from the RouterMono.Update tick instead and retries until it takes.
        /// Returns true once registration has succeeded, so the caller can stop asking.
        /// </summary>
        internal static bool TryRegister()
        {
            if (_registered) return true;

            // GetCommands() returning a list is the observable proof that the game's registry exists.
            string[] existing = GameBridge.ListCommands();
            if (existing == null || existing.Length == 0) return false;

            MethodInfo getHandler = typeof(ReflectionCommands).GetMethod("CrucibleGet", BindingFlags.Public | BindingFlags.Static);
            MethodInfo invokeHandler = typeof(ReflectionCommands).GetMethod("CrucibleInvoke", BindingFlags.Public | BindingFlags.Static);

            bool a = GameBridge.RegisterCommand("crucible_get", getHandler, new List<string> { "path" });
            bool b = GameBridge.RegisterCommand("crucible_invoke", invokeHandler, new List<string> { "type", "method", "args (space-separated, or - for none)" });

            _registered = a && b;
            return _registered;
        }

        /// <summary>
        /// crucible_get &lt;path&gt; — reflectively reads and renders a dot path.
        ///
        /// Takes a discrete string rather than string[]: CommandLineHelper marshals the raw arg
        /// array into a handler's individual typed parameters, and its vocabulary is
        /// int/float/double/bool/string/Vector2/Vector3. A string[] parameter is not in that set,
        /// so RegisterCommand throws while inspecting the MethodInfo. Verified in-game 2026-08-23:
        /// registering a string[] handler fails with TargetInvocationException.
        /// </summary>
        public static void CrucibleGet(string pPath)
        {
            LastResult = null;
            string path = pPath;

            object value; string error;
            if (!TryResolvePath(path, out value, out error))
            {
                LastResult = "error: " + error;
                if (_log != null) _log.LogWarning("crucible_get failed: " + error);
                return;
            }

            LastResult = Render(value);
        }

        /// <summary>
        /// crucible_invoke &lt;Type&gt; &lt;Method&gt; &lt;args&gt; — invokes a method reflectively.
        ///
        /// Three discrete string parameters, for the marshalling reason documented on CrucibleGet.
        /// Method arguments arrive as one space-separated string in pArgs and are split here;
        /// pass "-" for a no-argument call, since the marshaller supplies every declared parameter.
        /// </summary>
        public static void CrucibleInvoke(string pType, string pMethod, string pArgs)
        {
            LastResult = null;
            if (string.IsNullOrEmpty(pType) || string.IsNullOrEmpty(pMethod))
            {
                LastResult = "error: usage: crucible_invoke <Type> <Method> <args|->";
                return;
            }

            string typeName = pType;
            string methodName = pMethod;
            string[] rawArgs = (string.IsNullOrEmpty(pArgs) || pArgs == "-")
                ? new string[0]
                : pArgs.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            object result; string strategy; string error;
            if (!TryInvoke(typeName, methodName, rawArgs, out result, out strategy, out error))
            {
                LastResult = "error: " + error;
                if (_log != null) _log.LogWarning("crucible_invoke failed: " + error);
                return;
            }

            LastResult = "[instance: " + strategy + "] " + Render(result);
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

            object current = null;
            Type currentType = type;

            for (int i = 1; i < segments.Length; i++)
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
