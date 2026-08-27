using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
        /// The debug menu dictionary is keyed by DebugHelper._addDebugButton as
        /// <c>"{" + pContainer + "}" + pTitle</c> (DebugHelper.cs:364), and the title carries rich
        /// text markup (e.g. "&lt;color=#d05151&gt;Spawn All Market&lt;/color&gt;"). A flat substring
        /// search over the raw key string therefore has to get lucky: it is comparing against
        /// "{F6.ENCOUNTERS.MARKET}&lt;color=#d05151&gt;Spawn All Market&lt;/color&gt;", not against
        /// "Spawn All Market". Parsing container and title apart makes matching (and listing)
        /// container-aware instead of a coincidence.
        /// </summary>
        private sealed class MenuEntry
        {
            internal object RawKey;
            internal string Container;
            internal string TitleClean;
            internal object Value;
        }

        private static readonly Regex KeyPattern = new Regex(@"^\{(.*?)\}(.*)$", RegexOptions.Singleline);
        private static readonly Regex TagPattern = new Regex("<[^>]+>");

        private static string StripMarkup(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string noTags = TagPattern.Replace(s, "");
            return noTags.Replace("\n", " ").Trim();
        }

        /// <summary>
        /// crucible_debug_spawn &lt;enemies|encounters&gt; &lt;selector|-&gt;
        ///
        /// With "-" it lists every SPAWNABLE entry (container -&gt; clean title) and does nothing,
        /// so the catalogue can be read before anything is spawned. With a selector it invokes the
        /// first spawnable entry whose clean title contains it, preferring an exact match.
        ///
        /// "Spawnable" excludes two kinds of entries that a flat search can't tell apart from a real
        /// spawn action: separator rows (DebugHelper adds these with a null callback, e.g.
        /// "---- Categories ----") and navigation-only headers that live directly in the root
        /// container (e.g. "{F6.ENCOUNTERS}Market", DebugHelper.cs:743-746) whose callback only opens
        /// a submenu via DebugHelper.TryShowDebugMenu -- itself a no-op in this build
        /// (DebugHelper.cs:281-284, "return false"). Invoking either "succeeds" (no exception, no
        /// error) while spawning nothing, which is exactly the silent no-op this command used to
        /// produce. The real spawn actions -- both "Spawn All &lt;Category&gt;" and each individual
        /// encounter/enemy -- live one level deeper, in a per-category container such as
        /// "F6.ENCOUNTERS.MARKET" (DebugHelper.cs:743-798).
        /// </summary>
        public static void CrucibleDebugSpawn(string kind, string selector)
        {
            LastResult = null;
            try
            {
                kind = string.IsNullOrEmpty(kind) ? "enemies" : kind.Trim().ToLowerInvariant();

                string methodName;
                string rootContainer;
                if (kind == "enemies") { methodName = "GenerateSpawnEnemiesDebugMenuButtons"; rootContainer = "F6.ENEMIES"; }
                else if (kind == "encounters") { methodName = "GenerateSpawnEncountersDebugMenuButtons"; rootContainer = "F6.ENCOUNTERS"; }
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

                List<MenuEntry> entries = new List<MenuEntry>();
                foreach (object key in map.Keys)
                {
                    string raw = key == null ? "" : key.ToString();
                    Match parsed = KeyPattern.Match(raw);
                    string container = parsed.Success ? parsed.Groups[1].Value : "";
                    string titleRaw = parsed.Success ? parsed.Groups[2].Value : raw;
                    entries.Add(new MenuEntry
                    {
                        RawKey = key,
                        Container = container,
                        TitleClean = StripMarkup(titleRaw),
                        Value = map[key]
                    });
                }

                List<MenuEntry> spawnable = entries.FindAll(e =>
                    e.Value != null &&
                    e.Container.Length > rootContainer.Length &&
                    e.Container.StartsWith(rootContainer + ".", StringComparison.Ordinal));

                StringBuilder sb = new StringBuilder();
                sb.Append("kind=").Append(kind)
                  .Append(" buttonCount=").Append(entries.Count)
                  .Append(" spawnableCount=").Append(spawnable.Count);

                bool listOnly = string.IsNullOrEmpty(selector) || selector.Trim() == "-";
                if (listOnly)
                {
                    sb.Append("\nspawnable entries (container -> title):");
                    for (int i = 0; i < spawnable.Count; i++)
                    {
                        sb.Append("\n  [").Append(i).Append("] ").Append(spawnable[i].Container)
                          .Append(" -> ").Append(spawnable[i].TitleClean);
                    }
                    sb.Append("\n(list only; nothing was spawned)");
                    LastResult = sb.ToString();
                    return;
                }

                string wanted = selector.Trim();
                MenuEntry chosen = null;
                foreach (MenuEntry e in spawnable)
                {
                    if (string.Equals(e.TitleClean, wanted, StringComparison.OrdinalIgnoreCase)) { chosen = e; break; }
                }
                if (chosen == null)
                {
                    foreach (MenuEntry e in spawnable)
                    {
                        if (e.TitleClean.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) { chosen = e; break; }
                    }
                }
                if (chosen == null)
                {
                    sb.Append("\nno spawnable entry matches '").Append(wanted).Append("'. Candidates:");
                    for (int i = 0; i < spawnable.Count && i < 60; i++)
                    {
                        sb.Append("\n  ").Append(spawnable[i].Container).Append(" -> ").Append(spawnable[i].TitleClean);
                    }
                    if (spawnable.Count > 60) sb.Append("\n  ... (" + (spawnable.Count - 60) + " more; pass '-' to list all)");
                    LastResult = sb.ToString();
                    return;
                }

                sb.Append("\nchosen container=").Append(chosen.Container)
                  .Append(" entry=").Append(chosen.TitleClean)
                  .Append(" valueType=").Append(chosen.Value.GetType().Name);

                string invokeError;
                bool invoked = TryInvokeButton(chosen.Value, out invokeError);
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
