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
            _toggleShow = view == null ? null : AccessTools.Method(view, "ToggleShow");
            _isShowing = view == null ? null : AccessTools.Method(view, "IsShowing");

            Report("CommandLineHelper.ExecuteCommand", _execute);
            Report("CommandLineHelper.GetCommands", _getCommands);
            Report("CommandLineHelper.TryGetCommand", _tryGet);
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
