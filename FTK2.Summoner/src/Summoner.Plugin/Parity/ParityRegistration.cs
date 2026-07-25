using System;
using System.Reflection;
using BepInEx.Logging;

namespace Summoner.Plugin.Parity
{
    /// <summary>
    /// Reflection-based DevKit ParityService registration (design §A3.4) -- no hard build
    /// dependency on FTK2.DevKit. Surface: static
    /// FTK2Mods.DevKit.ParityService.Register(string,string,string,string[]) /
    /// RegisterWithCallback(string,string,string,string[],Action&lt;string[]&gt;), assembly
    /// ftk2mods.devkit (FTK2.DevKit/src/DevKit.Core/ParityService.cs). Resolved by name every call
    /// (type/method lookup, no cached failure fast-path beyond the one-time log) so DevKit installed
    /// later in the same session without a restart still gets registered. No-op + log once if the
    /// type or method is absent.
    ///
    /// Summoner has no ParityFailed pathway to wire a callback into: design §A5 notes SafeMode is a
    /// no-op for this load-time loader (the M0 merge happens once at Awake(), nothing to switch off
    /// at runtime), and OnParityMismatch=Block likewise has no runtime hook here -- Summoner just
    /// warns if the knob isn't Block (see SummonerPlugin.Awake). So this calls the callback-less
    /// Register(...) rather than RegisterWithCallback(...) with a callback that would have nothing to
    /// do, and logs that the callback is omitted.
    /// </summary>
    public static class ParityRegistration
    {
        private const string TypeName = "FTK2Mods.DevKit.ParityService";
        private const string AssemblyQualifiedTypeName = "FTK2Mods.DevKit.ParityService, ftk2mods.devkit";
        private static bool _loggedAbsence;

        public static void Register(string guid, string version, string dataHash, string[] enabledFeatures, ManualLogSource log)
        {
            try
            {
                var type = FindType();
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
                log.LogInfo($"[Summoner] Registered with ParityService: guid={guid}, dataHash={dataHash}, " +
                            $"enabledFeatures=[{string.Join(",", enabledFeatures)}] (no ParityFailed callback wired -- " +
                            "Summoner has no SafeMode/Block runtime pathway to call it from; see design §A5).");
            }
            catch (Exception e)
            {
                log.LogWarning($"[Summoner] ParityService.Register reflection call failed: {e.Message} -- continuing without parity registration.");
            }
        }

        private static void LogAbsenceOnce(ManualLogSource log, string message)
        {
            if (_loggedAbsence) return;
            log.LogInfo(message);
            _loggedAbsence = true;
        }

        private static Type FindType()
        {
            var direct = Type.GetType(AssemblyQualifiedTypeName, throwOnError: false);
            if (direct != null) return direct;

            // Fallback: scan already-loaded assemblies by short type name, in case DevKit's assembly
            // isn't resolvable by Type.GetType's normal probing (e.g. dev/test loading setups) but is
            // already loaded into the AppDomain.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(TypeName, throwOnError: false); }
                catch { continue; }
                if (t != null) return t;
            }
            return null;
        }
    }
}
