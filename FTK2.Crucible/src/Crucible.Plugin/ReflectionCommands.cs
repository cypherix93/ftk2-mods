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
            MethodInfo setHandler = typeof(ReflectionCommands).GetMethod("CrucibleSet", BindingFlags.Public | BindingFlags.Static);
            MethodInfo loadRunHandler = typeof(ReflectionCommands).GetMethod("CrucibleLoadRun", BindingFlags.Public | BindingFlags.Static);

            bool a = GameBridge.RegisterCommand("crucible_get", getHandler, new List<string> { "path" });
            bool b = GameBridge.RegisterCommand("crucible_invoke", invokeHandler, new List<string> { "type", "method", "args (space-separated, or - for none)" });
            bool c = GameBridge.RegisterCommand("crucible_set", setHandler, new List<string> { "path", "value" });
            bool d = GameBridge.RegisterCommand("crucible_load_run", loadRunHandler, new List<string> { "runId" });

            _registered = a && b && c && d;
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

        // ----------------------------------------------------------------- crucible_load_run

        /// <summary>
        /// crucible_load_run &lt;runId&gt; — loads a save BY ID, bypassing the Load Game UI (which
        /// has no observable response to focus + submit/gamepad-A over RPC, and which sits one
        /// wrong keypress away from the owner's live co-op save in the same date-ordered list).
        ///
        /// SAFETY, non-negotiable: <paramref name="pRunId"/> must be explicit and non-empty
        /// (<see cref="LoadRunGuard.TryValidateRunId"/>) and must already appear in
        /// <c>RouterHelper.Env.GameRuns</c> (<see cref="LoadRunGuard.IsKnownRunId"/>). This command
        /// NEVER falls back to <c>UserData.LastGameRunIdPlayed</c> or "the first"/"the newest" run —
        /// an implicit choice in a folder that also holds the owner's real saves is exactly the
        /// mistake this command exists to make impossible.
        ///
        /// Call path: <c>AdventureDirector._loadSave(String pGameRunId, String pFilename,
        /// CancellationToken pCancellationToken)</c>, reached through the same reflective
        /// <see cref="TryInvoke"/> machinery <c>crucible_invoke</c> uses (now that
        /// <see cref="ArgCoercion"/> understands <c>CancellationToken</c>). <paramref name="pRunId"/>
        /// itself is passed for <c>pGameRunId</c>; <c>pFilename</c> is assumed to be
        /// <c>&lt;runId&gt;.ftk2</c> (on-disk saves observed as GUID-named .ftk2 files, per
        /// docs/research/crucible-save-load-feasibility.md §6/§4 — ASSUMED, not confirmed against a
        /// live <c>GameSaveData.GetFileName()</c> call).
        ///
        /// Before invoking, this best-effort routes to <c>eRoutes.ADVENTURE_SELECTION</c> via
        /// <c>RouterMono.Route</c> so the Director that owns <c>_loadSave</c> has a chance to be
        /// initialized (docs/research/crucible-load-path.md marks this whole sequence
        /// NEEDS-LIVE-SPIKE); a failure to route is logged but not fatal, since
        /// <see cref="TryResolveInstance"/> may still find an already-live Director via its
        /// RouterMono-field fallback strategy.
        ///
        /// <b>Verification is necessarily partial in one call.</b> <c>_loadSave</c> returns a
        /// <c>Task</c> that <see cref="TryInvoke"/> deliberately does not await (see IsTask), and
        /// every command handler here runs synchronously inside a Harmony postfix on
        /// <c>RouterMono.Update</c> (see MainThreadPump) — blocking that call with a long sleep
        /// would freeze the one loop the async load needs to progress on. So this does a short,
        /// bounded check immediately (and once more after a brief settle), reports exactly what it
        /// observed via <see cref="LoadRunVerification.Confirm"/>, and says so plainly when the load
        /// is still in flight: never a bare "success" for a call that only kicked the load off.
        /// </summary>
        public static void CrucibleLoadRun(string pRunId)
        {
            LastResult = null;

            string guardError;
            if (!LoadRunGuard.TryValidateRunId(pRunId, out guardError))
            {
                LastResult = "error: " + guardError;
                if (_log != null) _log.LogWarning("crucible_load_run refused: " + guardError);
                return;
            }

            object env = GameBridge.GetEnv();
            if (env == null)
            {
                LastResult = "error: RouterHelper.Env unavailable (game may still be loading)";
                return;
            }

            string[] knownRunIds;
            string listError;
            if (!TryReadGameRuns(env, out knownRunIds, out listError))
            {
                LastResult = "error: could not read RouterHelper.Env.GameRuns: " + listError;
                return;
            }

            if (!LoadRunGuard.IsKnownRunId(pRunId, knownRunIds))
            {
                LastResult = "error: refused: '" + pRunId + "' is not in RouterHelper.Env.GameRuns ("
                    + knownRunIds.Length + " known run id(s)) -- refusing to load an id the game does not recognize.";
                return;
            }

            string routeNote = TryRouteToAdventureSelection();
            System.Threading.Thread.Sleep(200); // brief settle, not a completion wait -- see method doc.

            string pFilename = pRunId + ".ftk2"; // ASSUMED filename convention -- see method doc.
            object invokeResult; string invokeStrategy; string invokeError;
            bool invokeOk = TryInvoke("AdventureDirector", "_loadSave", new[] { pRunId, pFilename, "-" },
                out invokeResult, out invokeStrategy, out invokeError);

            if (!invokeOk)
            {
                LastResult = "error: _loadSave invoke failed: " + invokeError + " | route: " + routeNote;
                if (_log != null) _log.LogWarning("crucible_load_run failed: " + invokeError);
                return;
            }

            // One immediate read, then one more after letting a couple of frames pass -- NOT a
            // long poll (see method doc on why blocking here would stall the load itself).
            bool loaded; string evidence;
            CheckLoaded(pRunId, out loaded, out evidence);
            if (!loaded)
            {
                System.Threading.Thread.Sleep(500);
                CheckLoaded(pRunId, out loaded, out evidence);
            }

            LastResult = "loaded=" + loaded + " " + evidence
                + " | invoke=[" + invokeStrategy + "] " + Render(invokeResult)
                + " | route=" + routeNote
                + (loaded ? "" : " | NOTE: not confirmed within this call -- _loadSave is async and this "
                    + "handler cannot block the frame loop waiting for it; re-check with "
                    + "'crucible_get RouterHelper.Env.SelectedGameRunId' or crucible_state after a few seconds.");

            if (_log != null) _log.LogInfo("crucible_load_run(" + pRunId + "): " + LastResult);
        }

        /// <summary>Reads RouterHelper.Env.GameRuns (a List&lt;string&gt; of run-id GUIDs) as a string[].</summary>
        private static bool TryReadGameRuns(object env, out string[] runIds, out string error)
        {
            runIds = null;
            error = null;
            try
            {
                FieldInfo field = AccessTools.Field(env.GetType(), "GameRuns");
                object value = field != null ? field.GetValue(env) : null;
                IEnumerable enumerable = value as IEnumerable;
                if (enumerable == null)
                {
                    error = "GameRuns field not found or not enumerable";
                    return false;
                }
                List<string> list = new List<string>();
                foreach (object item in enumerable) list.Add(item == null ? null : item.ToString());
                runIds = list.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Best-effort route to eRoutes.ADVENTURE_SELECTION via the public RouterMono.Route. Returns
        /// a human-readable note of what happened; never throws, and a failure here is not treated
        /// as fatal by the caller (docs/research/crucible-load-path.md marks this hop ASSUMED, not
        /// CONFIRMED, and TryResolveInstance's RouterMono-field fallback may find a live Director
        /// regardless of the current route).
        /// </summary>
        private static string TryRouteToAdventureSelection()
        {
            try
            {
                Type routerMonoType = AccessTools.TypeByName("RouterMono");
                Type eRoutesType = AccessTools.TypeByName("eRoutes");
                Type unityObject = AccessTools.TypeByName("UnityEngine.Object");
                if (routerMonoType == null || eRoutesType == null || unityObject == null)
                    return "skipped (RouterMono/eRoutes/UnityEngine.Object type not found)";

                MethodInfo findObjectOfType = AccessTools.Method(unityObject, "FindObjectOfType", new[] { typeof(Type) });
                object router = findObjectOfType == null ? null : findObjectOfType.Invoke(null, new object[] { routerMonoType });
                if (router == null) return "skipped (no live RouterMono instance)";

                MethodInfo route = AccessTools.Method(routerMonoType, "Route",
                    new[] { eRoutesType, typeof(int), typeof(object), typeof(bool), typeof(bool) });
                if (route == null) return "skipped (RouterMono.Route signature not found)";

                object targetRoute;
                try { targetRoute = Enum.Parse(eRoutesType, "ADVENTURE_SELECTION", true); }
                catch (Exception) { return "skipped (eRoutes.ADVENTURE_SELECTION not found)"; }

                route.Invoke(router, new object[] { targetRoute, 0, null, false, false });
                return "routed to ADVENTURE_SELECTION";
            }
            catch (Exception ex)
            {
                return "failed (" + ex.Message + ")";
            }
        }

        /// <summary>One reflective read of run.present + RouterHelper.Env.SelectedGameRunId, reduced through LoadRunVerification.</summary>
        private static void CheckLoaded(string requestedRunId, out bool loaded, out string evidence)
        {
            loaded = false;
            evidence = "evidence unavailable";
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) { evidence = "requestedRunId=" + requestedRunId + " RouterHelper.Env=unavailable"; return; }

                FieldInfo gameRunField = AccessTools.Field(env.GetType(), "GameRun");
                object gameRun = gameRunField != null ? gameRunField.GetValue(env) : null;
                bool present = gameRun != null;

                FieldInfo selectedField = AccessTools.Field(env.GetType(), "SelectedGameRunId");
                object selected = selectedField != null ? selectedField.GetValue(env) : null;

                loaded = LoadRunVerification.Confirm(requestedRunId, present, selected as string, out evidence);
            }
            catch (Exception ex)
            {
                evidence = "evidence read threw: " + ex.Message;
            }
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
        /// <summary>internal, not private: reused by DebugVerbCommands (crucible_pin_seed) to reach
        /// a live Director's private fields, e.g. AdventureDirector._gameRandom, without
        /// re-implementing the (Instance/Current, FindObjectOfType, RouterMono field) strategy.</summary>
        internal static bool TryResolveInstance(Type type, out object instance, out string strategy, out string error)
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
