using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;

namespace Summoner.Plugin.Parity
{
    /// <summary>
    /// Reflection-based DevKit ParityService registration (design §A3.4) -- no hard build
    /// dependency on FTK2.DevKit. Surface: static
    /// FTK2Mods.DevKit.Core.ParityRegistry.Register(string,string,string,string[]). Resolved by
    /// name every call (type/method lookup, no cached failure fast-path beyond the one-time log)
    /// so DevKit installed later in the same session without a restart still gets registered.
    /// No-op + log once if the type or method is absent.
    /// </summary>
    public static class ParityRegistration
    {
        private const string TypeName = "FTK2Mods.DevKit.Core.ParityRegistry";
        private static bool _loggedAbsence;

        public static void Register(string guid, string version, string dataHash, string[] enabledFeatures, ManualLogSource log)
        {
            try
            {
                var type = FindType(TypeName);
                if (type == null)
                {
                    LogAbsenceOnce(log, $"[Summoner] {TypeName} not found -- parity registration is a no-op (DevKit not installed).");
                    return;
                }

                var method = type.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string[]) }, null);
                if (method == null)
                {
                    LogAbsenceOnce(log, $"[Summoner] {TypeName}.Register(string,string,string,string[]) not found -- parity registration is a no-op.");
                    return;
                }

                method.Invoke(null, new object[] { guid, version, dataHash, enabledFeatures });
                log.LogInfo($"[Summoner] Registered with ParityService: guid={guid}, dataHash={dataHash}, enabledFeatures=[{string.Join(",", enabledFeatures)}]");
            }
            catch (Exception e)
            {
                log.LogWarning($"[Summoner] ParityRegistry.Register reflection call failed: {e.Message} -- continuing without parity registration.");
            }
        }

        private static void LogAbsenceOnce(ManualLogSource log, string message)
        {
            if (_loggedAbsence) return;
            log.LogInfo(message);
            _loggedAbsence = true;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }
    }
}
