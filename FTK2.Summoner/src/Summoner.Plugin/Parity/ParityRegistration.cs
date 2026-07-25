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
    /// MP review B7 fix: Summoner now HAS a ParityFailed pathway -- SummonerPlugin's callback latches a
    /// session-scoped Blocked flag on any non-Match verdict row, which the Configs merge postfixes
    /// (ConfigsMergePatches) consult to stop future merges (see SummonerPlugin.OnParityMismatchRow and
    /// its doc comment for the exact WarnOnly/WarnAndSafeMode/Block semantics). So registration now
    /// prefers RegisterWithCallback and only falls back to the callback-less Register(...) when this
    /// DevKit build doesn't expose it -- in which case the policy is UNENFORCEABLE and we say so loudly.
    /// </summary>
    public static class ParityRegistration
    {
        private const string TypeName = "FTK2Mods.DevKit.ParityService";
        private const string AssemblyQualifiedTypeName = "FTK2Mods.DevKit.ParityService, ftk2mods.devkit";
        private static bool _loggedAbsence;

        /// <summary>
        /// Registers Summoner's parity tuple plus its mismatch callback. Prefers
        /// <c>RegisterWithCallback(string,string,string,string[],Action&lt;string[]&gt;)</c> so the
        /// <c>[Multiplayer] OnParityMismatch</c> policy can actually fire; falls back to the
        /// callback-less <c>Register(string,string,string,string[])</c> only if an older DevKit build
        /// lacks it. Never throws.
        /// </summary>
        public static void RegisterWithCallback(string guid, string version, string dataHash, string[] enabledFeatures,
            Action<string[]> onParityMismatch, ManualLogSource log)
        {
            try
            {
                var type = FindType();
                if (type == null)
                {
                    LogAbsenceOnce(log, $"[Summoner] {TypeName} not found -- parity registration is a no-op (DevKit not installed). " +
                                        "Multiplayer parity is UNENFORCED without DevKit; install FTK2.DevKit for the R1 handshake.");
                    return;
                }

                var withCallback = type.GetMethod("RegisterWithCallback", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string[]), typeof(Action<string[]>) }, null);

                if (withCallback != null)
                {
                    var result = withCallback.Invoke(null, new object[] { guid, version, dataHash, enabledFeatures, onParityMismatch });
                    LogRegistered(log, guid, dataHash, enabledFeatures, result, "RegisterWithCallback");
                    return;
                }

                var plain = type.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string[]) }, null);
                if (plain == null)
                {
                    LogAbsenceOnce(log, $"[Summoner] {TypeName} found but exposes neither RegisterWithCallback(string,string,string,string[],Action<string[]>) " +
                                        "nor Register(string,string,string,string[]) -- parity registration is a no-op.");
                    return;
                }

                var plainResult = plain.Invoke(null, new object[] { guid, version, dataHash, enabledFeatures });
                LogRegistered(log, guid, dataHash, enabledFeatures, plainResult, "Register");
                log.LogWarning("[Summoner] This DevKit build has no RegisterWithCallback -- Summoner's [Multiplayer] " +
                                "OnParityMismatch policy CANNOT be enforced (no callback to latch Blocked on). " +
                                "Verify data parity manually before playing online.");
            }
            catch (Exception e)
            {
                log.LogWarning($"[Summoner] ParityService registration reflection call failed: {e.Message} -- continuing without parity registration.");
            }
        }

        private static void LogRegistered(ManualLogSource log, string guid, string dataHash, string[] enabledFeatures, object result, string via)
        {
            bool ok = !(result is bool) || (bool)result;
            var features = enabledFeatures == null || enabledFeatures.Length == 0 ? "(none)" : string.Join(",", enabledFeatures);
            if (ok)
            {
                log.LogInfo($"[Summoner] Registered with ParityService via {via}: guid={guid}, dataHash={dataHash}, enabledFeatures=[{features}].");
            }
            else
            {
                log.LogWarning($"[Summoner] ParityService.{via} returned false (rejected registration) -- parity is UNENFORCED for Summoner this session. dataHash={dataHash}.");
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
