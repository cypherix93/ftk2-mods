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
    /// Drives the game's OWN debug spawn menus.
    ///
    /// Every other route into combat failed for the same reason: the fight needs scene context that
    /// only a legal transition builds. Forcing <c>RouterMono.Route(COMBAT, …)</c> flips the route
    /// enum but leaves CombatPhase uninitialized, and <c>TryNextAmbushEncounter</c> declines
    /// (returns NONE) unless an ambush enemy has already been assigned. Rather than keep
    /// reconstructing the game's setup by hand, this reuses the code IronOak already wrote for
    /// exactly this purpose.
    ///
    /// The <c>DebugHelper.GenerateSpawn*DebugMenuButtons</c> family takes a dictionary and fills it
    /// with the debug menu's buttons — a name plus the action that performs the spawn. Populating
    /// one and invoking an entry runs the real spawn path with the real arguments, which is why this
    /// works where hand-built payloads did not.
    ///
    /// Verified API surface (TypeProbe --signatures, 2026-08-23):
    ///   DebugHelper.GenerateSpawnEnemiesDebugMenuButtons(Dictionary pDebugButtons, List[,] pHexMap,
    ///       Env pEnv, List pPlayerCharacterEntities, Action`2 pVisualCallback) static
    ///   DebugHelper.GenerateSpawnEncountersDebugMenuButtons(Dictionary pDebugButtons, Env pEnv,
    ///       List[,] pHexMap, List pPlayerCharacterEntities, Action`2 pVisualCallback) static
    ///
    /// Note the two differ in ARGUMENT ORDER (hexMap and env are swapped). Binding by name and
    /// filling positionally from a shared list would pass a hex map where an Env is expected, so
    /// arguments are matched to each parameter by TYPE rather than by position.
    /// </summary>
    internal static class DebugSpawnCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _registered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (_registered) return;
            _registered = GameBridge.RegisterCommand("crucible_debug_spawn",
                typeof(DebugSpawnCommands).GetMethod("CrucibleDebugSpawn", BindingFlags.Public | BindingFlags.Static),
                new List<string> { "kind(enemies|encounters)", "selector or - to list" });
            if (_registered && _log != null) _log.LogInfo("DebugSpawnCommands registered (crucible_debug_spawn).");
        }

        /// <summary>
        /// crucible_debug_spawn &lt;enemies|encounters&gt; &lt;selector|-&gt;
        ///
        /// With "-" it lists the available buttons and does nothing, so the catalogue can be read
        /// before anything is spawned. With a selector it invokes the first button whose name
        /// contains it, preferring an exact match.
        /// </summary>
        public static void CrucibleDebugSpawn(string kind, string selector)
        {
            LastResult = null;
            try
            {
                kind = string.IsNullOrEmpty(kind) ? "enemies" : kind.Trim().ToLowerInvariant();

                string methodName;
                if (kind == "enemies") methodName = "GenerateSpawnEnemiesDebugMenuButtons";
                else if (kind == "encounters") methodName = "GenerateSpawnEncountersDebugMenuButtons";
                else { LastResult = "error: unknown kind '" + kind + "' (expected enemies or encounters)"; return; }

                Type debugHelper = AccessTools.TypeByName("DebugHelper");
                if (debugHelper == null) { LastResult = "error: DebugHelper type not found"; return; }

                MethodInfo generate = null;
                foreach (MethodInfo m in debugHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (string.Equals(m.Name, methodName, StringComparison.Ordinal)) { generate = m; break; }
                }
                if (generate == null) { LastResult = "error: DebugHelper." + methodName + " not found"; return; }

                object buttons;
                string buildError;
                if (!TryInvokeGenerator(generate, out buttons, out buildError))
                {
                    LastResult = "error: " + buildError;
                    return;
                }

                IDictionary map = buttons as IDictionary;
                if (map == null) { LastResult = "error: debug button container is not an IDictionary"; return; }

                List<string> names = new List<string>();
                foreach (object key in map.Keys) names.Add(key == null ? "(null)" : key.ToString());
                names.Sort(StringComparer.Ordinal);

                StringBuilder sb = new StringBuilder();
                sb.Append("kind=").Append(kind).Append(" buttonCount=").Append(names.Count);

                if (names.Count > 0)
                {
                    object sampleValue = null;
                    foreach (object value in map.Values) { sampleValue = value; break; }
                    sb.Append(" valueType=").Append(sampleValue == null ? "(null)" : sampleValue.GetType().Name);
                }

                bool listOnly = string.IsNullOrEmpty(selector) || selector.Trim() == "-";
                if (listOnly)
                {
                    sb.Append("\nbuttons:");
                    for (int i = 0; i < names.Count; i++) sb.Append("\n  [").Append(i).Append("] ").Append(names[i]);
                    sb.Append("\n(list only; nothing was spawned)");
                    LastResult = sb.ToString();
                    return;
                }

                string wanted = selector.Trim();
                object chosenKey = null;
                foreach (object key in map.Keys)
                {
                    if (key != null && string.Equals(key.ToString(), wanted, StringComparison.OrdinalIgnoreCase)) { chosenKey = key; break; }
                }
                if (chosenKey == null)
                {
                    foreach (object key in map.Keys)
                    {
                        if (key != null && key.ToString().IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) { chosenKey = key; break; }
                    }
                }
                if (chosenKey == null)
                {
                    sb.Append("\nno button matches '").Append(wanted).Append("'. Available:");
                    for (int i = 0; i < names.Count && i < 60; i++) sb.Append("\n  ").Append(names[i]);
                    LastResult = sb.ToString();
                    return;
                }

                sb.Append("\nchosen=").Append(chosenKey.ToString());
                string invokeError;
                bool invoked = TryInvokeButton(map[chosenKey], out invokeError);
                sb.Append(" invoked=").Append(invoked);
                if (!invoked) sb.Append(" invokeError=").Append(invokeError);
                sb.Append("\nrouteAfter=").Append(ReadRoute());
                sb.Append("\nNOTE: spawning is asynchronous; re-read /state?schema=v2 after a few seconds.");

                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_debug_spawn: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_debug_spawn threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Fills each parameter by TYPE, not by position: the enemies and encounters generators
        /// declare the same arguments in a different order, so positional filling would hand a hex
        /// map to an Env parameter.
        /// </summary>
        private static bool TryInvokeGenerator(MethodInfo generate, out object buttons, out string error)
        {
            buttons = null;
            error = null;

            object env = GameBridge.GetEnv();
            if (env == null) { error = "Env unavailable"; return false; }

            object gameRun = PartyAccess.ReadMember(env, "GameRun");
            if (gameRun == null) { error = "no run loaded"; return false; }

            List<object> party;
            string partyError;
            if (!PartyAccess.TryGetParty(out party, out partyError)) { error = partyError; return false; }

            object hexMap;
            string mapError;
            if (!TryGetHexMap(env, gameRun, party[0], out hexMap, out mapError)) { error = mapError; return false; }

            ParameterInfo[] ps = generate.GetParameters();
            object[] args = new object[ps.Length];

            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;

                if (typeof(IDictionary).IsAssignableFrom(t))
                {
                    buttons = Activator.CreateInstance(t);
                    args[i] = buttons;
                }
                else if (t.IsArray && t.GetArrayRank() == 2)
                {
                    args[i] = hexMap;
                }
                else if (string.Equals(t.Name, "Env", StringComparison.Ordinal))
                {
                    args[i] = env;
                }
                else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    args[i] = BuildTypedList(t, party);
                }
                else if (typeof(Delegate).IsAssignableFrom(t))
                {
                    // A real no-op delegate, NOT null. The generated button closes over these
                    // callbacks and invokes them when pressed, so a null here does not mean
                    // "no visual feedback" -- it means a NullReferenceException the moment the
                    // spawn runs. Measured: passing null made every button throw on invoke.
                    args[i] = MakeNoOpDelegate(t);
                }
                else
                {
                    args[i] = null;
                }
            }

            if (buttons == null) { error = "generator has no dictionary parameter to fill"; return false; }

            try
            {
                generate.Invoke(null, args);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = generate.Name + " threw: " + root.GetType().Name + ": " + root.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }


        /// <summary>
        /// Builds a do-nothing delegate of an arbitrary Action type. The generic no-op methods are
        /// closed over the delegate's own generic arguments so the signature matches exactly;
        /// CreateDelegate refuses anything else.
        /// </summary>
        private static Delegate MakeNoOpDelegate(Type delegateType)
        {
            try
            {
                MethodInfo invoke = delegateType.GetMethod("Invoke");
                if (invoke == null) return null;

                ParameterInfo[] ps = invoke.GetParameters();
                if (invoke.ReturnType != typeof(void)) return null;

                MethodInfo noOp;
                if (ps.Length == 0)
                {
                    noOp = typeof(DebugSpawnCommands).GetMethod("NoOp0", BindingFlags.NonPublic | BindingFlags.Static);
                }
                else if (ps.Length == 1)
                {
                    noOp = typeof(DebugSpawnCommands).GetMethod("NoOp1", BindingFlags.NonPublic | BindingFlags.Static)
                        .MakeGenericMethod(ps[0].ParameterType);
                }
                else if (ps.Length == 2)
                {
                    noOp = typeof(DebugSpawnCommands).GetMethod("NoOp2", BindingFlags.NonPublic | BindingFlags.Static)
                        .MakeGenericMethod(ps[0].ParameterType, ps[1].ParameterType);
                }
                else if (ps.Length == 3)
                {
                    noOp = typeof(DebugSpawnCommands).GetMethod("NoOp3", BindingFlags.NonPublic | BindingFlags.Static)
                        .MakeGenericMethod(ps[0].ParameterType, ps[1].ParameterType, ps[2].ParameterType);
                }
                else
                {
                    return null;
                }

                return Delegate.CreateDelegate(delegateType, noOp);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void NoOp0() { }
        private static void NoOp1<T>(T a) { }
        private static void NoOp2<T1, T2>(T1 a, T2 b) { }
        private static void NoOp3<T1, T2, T3>(T1 a, T2 b, T3 c) { }

        private static object BuildTypedList(Type listType, List<object> items)
        {
            try
            {
                object typed = Activator.CreateInstance(listType);
                MethodInfo add = listType.GetMethod("Add");
                foreach (object item in items) add.Invoke(typed, new object[] { item });
                return typed;
            }
            catch (Exception) { return null; }
        }

        private static bool TryGetHexMap(object env, object gameRun, object anyPartyEntity, out object hexMap, out string error)
        {
            hexMap = null;
            error = null;
            try
            {
                MethodInfo tryGet = AccessTools.Method(anyPartyEntity.GetType(), "TryGetAdventureComponent");
                if (tryGet == null) { error = "Entity.TryGetAdventureComponent not found"; return false; }

                object[] callArgs = new object[] { gameRun, null };
                object ok = tryGet.Invoke(anyPartyEntity, callArgs);
                if (!(ok is bool) || !(bool)ok || callArgs[1] == null)
                {
                    error = "party entity has no adventure component (is the overworld loaded?)";
                    return false;
                }

                object mapId = PartyAccess.ReadMember(callArgs[1], "MapID");
                IDictionary maps = PartyAccess.ReadMember(env, "HexMaps") as IDictionary;
                if (maps == null || mapId == null || !maps.Contains(mapId))
                {
                    error = "no hex map for MapID " + (mapId == null ? "(null)" : mapId.ToString());
                    return false;
                }
                hexMap = maps[mapId];
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = root.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// Invokes a debug button's value. It may be a delegate directly, or a struct/tuple holding
        /// one, so both are handled rather than assuming the shape a dump happened to show.
        /// </summary>
        private static bool TryInvokeButton(object value, out string error)
        {
            error = null;
            if (value == null) { error = "button value is null"; return false; }

            try
            {
                Delegate direct = value as Delegate;
                if (direct != null) { direct.DynamicInvoke(ArgsFor(direct)); return true; }

                foreach (FieldInfo f in value.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Delegate nested = f.GetValue(value) as Delegate;
                    if (nested != null) { nested.DynamicInvoke(ArgsFor(nested)); return true; }
                }

                error = "no delegate found on button value of type " + value.GetType().Name;
                return false;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = root.GetType().Name + ": " + root.Message;
                return false;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>Default arguments for a button delegate; most take none, some take a flag.</summary>
        private static object[] ArgsFor(Delegate target)
        {
            ParameterInfo[] ps = target.Method.GetParameters();
            if (ps.Length == 0) return null;
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                args[i] = t.IsValueType ? Activator.CreateInstance(t) : null;
            }
            return args;
        }

        private static string ReadRoute()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                MethodInfo get = routerHelper == null ? null : AccessTools.Method(routerHelper, "GetCurrentRoute");
                object value = get == null ? null : get.Invoke(null, null);
                return value == null ? "(unknown)" : value.ToString();
            }
            catch (Exception) { return "(unreadable)"; }
        }
    }
}
