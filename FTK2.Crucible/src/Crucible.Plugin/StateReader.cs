using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Best-effort reflection snapshot of the live run.
    ///
    /// Explicitly versioned and deliberately forgiving: a renamed or missing game field yields a
    /// null entry plus a warning, never an exception. A snapshot that throws would take down the
    /// desync oracle exactly when a game update makes it most needed.
    /// </summary>
    internal static class StateReader
    {
        internal const string Schema = "crucible.state.v1";

        internal static Dictionary<string, object> Snapshot()
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            List<object> warnings = new List<object>();

            root["schema"] = Schema;
            root["instance"] = CruciblePlugin.Instance != null ? CruciblePlugin.Instance.InstanceName : null;

            try
            {
                object env = GameBridge.GetEnv();
                if (env == null)
                {
                    warnings.Add("RouterHelper.Env unavailable (game may still be loading)");
                    root["warnings"] = warnings;
                    return root;
                }

                root["route"] = SafeValue(InvokeStatic("RouterHelper", "GetCurrentRoute"));

                object gameRun = GetMember(env, "GameRun", warnings);
                Dictionary<string, object> run = new Dictionary<string, object>();
                run["present"] = gameRun != null;
                if (gameRun != null)
                {
                    run["seed"] = SafeValue(GetMember(gameRun, "Seed", null));
                    run["day"] = SafeValue(GetMember(gameRun, "Day", null));
                    run["gold"] = SafeValue(GetMember(gameRun, "Gold", null));
                    run["chapter"] = SafeValue(GetMember(gameRun, "Chapter", null));
                }
                root["run"] = run;

                object networkData = GameBridge.GetNetworkData();
                Dictionary<string, object> net = new Dictionary<string, object>();
                net["online"] = GameBridge.IsOnlineSession();
                if (networkData != null)
                {
                    net["isHost"] = SafeValue(GetMember(networkData, "IsHost", warnings));
                    net["playerCount"] = SafeValue(CountOf(GetMember(networkData, "PlayerList", warnings)));
                }
                else
                {
                    warnings.Add("NetworkData unavailable");
                }
                root["network"] = net;

                root["console"] = GameBridge.IsConsoleShowing();
            }
            catch (Exception ex)
            {
                warnings.Add("snapshot failed: " + ex.Message);
            }

            root["warnings"] = warnings;
            return root;
        }

        /// <summary>
        /// NetworkData exposes no player count. The roster is the <c>PlayerList</c> field, so the
        /// count is derived from it. Verified against the retail assembly 2026-08-23: NetworkData
        /// has IsHost, UserName, PlayerList and PlayingOnlineMultiplayer, but no PlayerCount.
        /// </summary>
        private static object CountOf(object collection)
        {
            ICollection list = collection as ICollection;
            return list != null ? (object)list.Count : null;
        }

        private static object GetMember(object instance, string name, List<object> warnings)
        {
            if (instance == null) return null;
            try
            {
                FieldInfo field = AccessTools.Field(instance.GetType(), name);
                if (field != null) return field.GetValue(instance);

                PropertyInfo prop = AccessTools.Property(instance.GetType(), name);
                if (prop != null) return prop.GetValue(instance, null);

                if (warnings != null) warnings.Add("member not found: " + name);
                return null;
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.Add("member threw: " + name + ": " + ex.Message);
                return null;
            }
        }

        private static object InvokeStatic(string typeName, string methodName)
        {
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                if (type == null) return null;
                MethodInfo method = AccessTools.Method(type, methodName);
                if (method == null || method.GetParameters().Length != 0) return null;
                return method.Invoke(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Keeps primitives as primitives (so the digest can quantize floats) and stringifies
        /// everything else (enums, ids) rather than walking arbitrary object graphs.
        /// </summary>
        private static object SafeValue(object value)
        {
            if (value == null) return null;
            if (value is bool || value is int || value is long || value is double || value is float) return value;
            return value.ToString();
        }
    }
}
