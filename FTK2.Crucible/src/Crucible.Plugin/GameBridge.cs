using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Reflection wrapper over the game's own shipped dev-command system.
    ///
    /// Verified against retail FTK2.dll (SPEC §0): <c>CommandLineHelper</c> and
    /// <c>CommandLineViewHelper</c> are public static, command registration is not gated behind a
    /// debug build, and <c>RouterMono</c> already calls <c>CommandLineViewHelper.Initialize</c> at
    /// startup — only the keybind that opens the console was stripped for shipping. So Crucible does
    /// not build a command system; it drives the one that is already there.
    /// </summary>
    internal static class GameBridge
    {
        private static MethodInfo _execute;      // CommandLineHelper.ExecuteCommand(string, string[], bool, bool)
        private static MethodInfo _getCommands;  // CommandLineHelper.GetCommands() -> List<string>
        private static MethodInfo _tryGet;       // CommandLineHelper.TryGetCommand(string, out tuple)
        private static MethodInfo _registerCommand; // CommandLineHelper.RegisterCommand(string, MethodInfo, List<string>, object)
        private static MethodInfo _toggleShow;   // CommandLineViewHelper.ToggleShow() -> bool
        private static MethodInfo _isShowing;    // CommandLineViewHelper.IsShowing() -> bool
        private static ManualLogSource _log;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;

            Type helper = AccessTools.TypeByName("CommandLineHelper");
            Type view = AccessTools.TypeByName("CommandLineViewHelper");

            _execute = helper == null ? null : AccessTools.Method(helper, "ExecuteCommand",
                new Type[] { typeof(string), typeof(string[]), typeof(bool), typeof(bool) });
            _getCommands = helper == null ? null : AccessTools.Method(helper, "GetCommands");
            _tryGet = helper == null ? null : AccessTools.Method(helper, "TryGetCommand");
            _registerCommand = helper == null ? null : AccessTools.Method(helper, "RegisterCommand",
                new Type[] { typeof(string), typeof(MethodInfo), typeof(List<string>), typeof(object) });
            _toggleShow = view == null ? null : AccessTools.Method(view, "ToggleShow");
            _isShowing = view == null ? null : AccessTools.Method(view, "IsShowing");

            Report("CommandLineHelper.ExecuteCommand", _execute);
            Report("CommandLineHelper.GetCommands", _getCommands);
            Report("CommandLineHelper.TryGetCommand", _tryGet);
            Report("CommandLineHelper.RegisterCommand", _registerCommand);
            Report("CommandLineViewHelper.ToggleShow", _toggleShow);
            Report("CommandLineViewHelper.IsShowing", _isShowing);
        }

        private static void Report(string description, MethodBase resolved)
        {
            if (resolved != null) _log.LogInfo("Target found: " + description);
            else _log.LogWarning("Target NOT found: " + description + " (feature disabled)");
            DevKitBridge.ReportTarget(description, resolved);
        }

        internal static bool IsAvailable { get { return _execute != null; } }

        internal static bool CommandExists(string name)
        {
            if (_tryGet == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                object[] parameters = new object[] { name, null };
                object result = _tryGet.Invoke(null, parameters);
                return result is bool && (bool)result;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string[] ListCommands()
        {
            if (_getCommands == null) return new string[0];
            try
            {
                IEnumerable list = _getCommands.Invoke(null, null) as IEnumerable;
                if (list == null) return new string[0];
                List<string> result = new List<string>();
                foreach (object item in list) if (item != null) result.Add(item.ToString());
                result.Sort(StringComparer.Ordinal);
                return result.ToArray();
            }
            catch (Exception ex)
            {
                _log.LogWarning("ListCommands failed: " + ex.Message);
                return new string[0];
            }
        }

        /// <summary>
        /// Executes a game command.
        ///
        /// <c>ExecuteCommand</c> returns silently when the name is not in the registry, so we check
        /// the registry first — without that check the RPC would report success for a command that
        /// never ran, which is the single most misleading failure this harness could have.
        /// </summary>
        internal static bool Exec(string name, string[] args, out string error)
        {
            error = null;
            if (_execute == null) { error = "bridge_unavailable"; return false; }
            if (!CommandExists(name)) { error = "unknown_command"; return false; }
            try
            {
                _execute.Invoke(null, new object[] { name, args ?? new string[0], false, true });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "command_threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "invoke_failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reproduces exactly the branch in <c>CommandLineHelper.ExecuteCommand</c> that decides
        /// whether the handler is invoked AT ALL, so an under-supplied call is refused loudly here
        /// instead of succeeding silently.
        ///
        /// <para>THE BUG THIS EXISTS FOR. ExecuteCommand marshals args positionally
        /// (decompiled, lines 103-289). For a parameter index past the end of <c>pArgs</c> it does:</para>
        /// <code>
        /// else if (parameters2[i].DefaultValue != null) { flag3 = false; }
        /// else { array[i] = parameters2[i].DefaultValue; }
        /// if (!flag3) break;
        /// ...
        /// if (flag2) { ...Invoke... }
        /// Debug.LogError("Command not found '" + pName + "'");
        /// </code>
        /// <para><c>ParameterInfo.DefaultValue</c> for a plain <c>string</c> parameter with no
        /// default is <c>DBNull.Value</c>, NOT null — so a shortfall sets flag3 false, breaks, and
        /// SKIPS the invoke entirely. ExecuteCommand returns void either way, so <see cref="Exec"/>
        /// reported success, RpcServer answered 200 ok=true, and the handler's LastResult was still
        /// the null RpcServer cleared before dispatch. The caller got an empty string for a command
        /// that never ran. Confirmed live: Player.log carried
        /// <c>Command not found 'crucible_map_encounters'</c> for a REGISTERED command, and the
        /// matching trace line was <c>{"args":[],"durationMs":16}</c> with no result, against
        /// <c>{"args":["-"],"durationMs":89,"result":"mapId=..."}</c> for the same command.</para>
        ///
        /// <para>Note the branch is not simply "fewer args than parameters": a parameter whose
        /// DefaultValue is genuinely null is filled in and execution continues. So this walks the
        /// parameters the same way rather than comparing counts, and only reports a shortfall the
        /// game would actually refuse.</para>
        /// </summary>
        internal static bool WouldSkipInvoke(string name, int suppliedArgs, out string detail)
        {
            detail = null;
            if (_tryGet == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                object[] parameters = new object[] { name, null };
                object found = _tryGet.Invoke(null, parameters);
                if (!(found is bool) || !(bool)found) return false;

                object entry = parameters[1];
                if (entry == null) return false;

                // The registry value is a ValueTuple whose Item2 is the handler MethodInfo and
                // whose Item3 is the arg-hint list. Read them by field name rather than by
                // casting, so a shape change degrades to "no guard" and never to an exception.
                FieldInfo methodField = entry.GetType().GetField("Item2");
                if (methodField == null) return false;
                MethodInfo handler = methodField.GetValue(entry) as MethodInfo;
                if (handler == null) return false;

                ParameterInfo[] ps = handler.GetParameters();
                for (int i = suppliedArgs; i < ps.Length; i++)
                {
                    if (ps[i].DefaultValue == null) continue;   // the game fills this one in
                    List<string> hints = null;
                    FieldInfo hintField = entry.GetType().GetField("Item3");
                    if (hintField != null) hints = hintField.GetValue(entry) as List<string>;
                    detail = "arity_shortfall: '" + name + "' takes " + ps.Length
                        + " arg(s) and " + suppliedArgs + " were supplied"
                        + (hints != null && hints.Count > 0 ? " (" + string.Join(", ", hints.ToArray()) + ")" : "")
                        + ". CommandLineHelper.ExecuteCommand SKIPS the invoke entirely on a shortfall"
                        + " and logs \"Command not found\", so this would have reported success while"
                        + " doing nothing. Pass '-' for the arguments you want left at their default.";
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;   // A guard that cannot read the registry must not block the command.
            }
        }

        /// <summary>Renders a handler's signature so a rejected registration says which shape was refused.</summary>
        private static string DescribeHandler(MethodInfo handler)
        {
            if (handler == null) return "(null)";
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append(handler.ReturnType.Name).Append(' ').Append(handler.Name).Append('(');
                ParameterInfo[] ps = handler.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                }
                sb.Append(")  static=").Append(handler.IsStatic);
                return sb.ToString();
            }
            catch (Exception) { return "(undescribable)"; }
        }

        /// <summary>
        /// Registers a Crucible-owned console command via CommandLineHelper.RegisterCommand, so it
        /// works from the in-game console and over RPC through the same registry as every shipped
        /// command. Degrades to a logged warning, never an exception, if the target could not be
        /// resolved at Initialize (a game update, or a wrong overload guess).
        /// </summary>
        internal static bool RegisterCommand(string name, MethodInfo handler, List<string> argHints)
        {
            if (_registerCommand == null)
            {
                if (_log != null) _log.LogWarning("RegisterCommand unavailable; '" + name + "' will not be reachable.");
                return false;
            }
            if (handler == null)
            {
                if (_log != null) _log.LogWarning("RegisterCommand: handler for '" + name + "' is null; skipping.");
                return false;
            }
            try
            {
                _registerCommand.Invoke(null, new object[] { name, handler, argHints, null });
                if (_log != null) _log.LogInfo("Registered command: " + name);
                return true;
            }
            catch (Exception ex)
            {
                // Unwrap: reflective Invoke wraps every callee failure in a
                // TargetInvocationException whose own Message is always the same generic
                // sentence, which says nothing about what actually went wrong.
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                if (_log != null)
                {
                    _log.LogWarning("RegisterCommand('" + name + "') failed: "
                                    + root.GetType().Name + ": " + root.Message);
                    _log.LogWarning("  handler: " + DescribeHandler(handler));
                    _log.LogWarning("  stack: " + root.StackTrace);
                }
                return false;
            }
        }

        internal static bool ToggleConsole()
        {
            if (_toggleShow == null) { _log.LogWarning("Console unavailable (ToggleShow not found)."); return false; }
            try
            {
                object result = _toggleShow.Invoke(null, null);
                return result is bool && (bool)result;
            }
            catch (Exception ex)
            {
                _log.LogWarning("ToggleConsole failed: " + ex.Message);
                return false;
            }
        }

        internal static bool IsConsoleShowing()
        {
            if (_isShowing == null) return false;
            try
            {
                object result = _isShowing.Invoke(null, null);
                return result is bool && (bool)result;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Returns the live <c>NetworkData</c> instance, or null.</summary>
        internal static object GetNetworkData()
        {
            try
            {
                object env = GetEnv();
                if (env == null) return null;
                FieldInfo field = AccessTools.Field(env.GetType(), "NetworkData");
                if (field != null) return field.GetValue(env);
                PropertyInfo prop = AccessTools.Property(env.GetType(), "NetworkData");
                return prop == null ? null : prop.GetValue(env, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static object GetEnv()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                if (routerHelper == null) return null;
                PropertyInfo prop = AccessTools.Property(routerHelper, "Env");
                if (prop != null) return prop.GetValue(null, null);
                FieldInfo field = AccessTools.Field(routerHelper, "Env");
                return field == null ? null : field.GetValue(null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True when an online session is active. <c>NetworkData.PlayingOnlineMultiplayer</c> is a
        /// verified public field of the retail assembly.
        ///
        /// **Fails closed:** if the shape ever changes and we cannot tell, this returns TRUE. An
        /// unknown session state must not unlock state mutation — a false "offline" reading would
        /// let a dev command desync a live co-op game.
        /// </summary>
        internal static bool IsOnlineSession()
        {
            try
            {
                object networkData = GetNetworkData();
                if (networkData == null) return false; // No session object at all: main menu.

                FieldInfo online = AccessTools.Field(networkData.GetType(), "PlayingOnlineMultiplayer");
                if (online != null)
                {
                    object v = online.GetValue(networkData);
                    return v is bool && (bool)v;
                }

                PropertyInfo onlineProp = AccessTools.Property(networkData.GetType(), "PlayingOnlineMultiplayer");
                if (onlineProp != null)
                {
                    object v = onlineProp.GetValue(networkData, null);
                    return v is bool && (bool)v;
                }

                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
