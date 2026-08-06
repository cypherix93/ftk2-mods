using System;
using System.Reflection;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Reflection bridge to FTK2.DevKit's <c>TransportService</c> (loot-grant verb spec §1.5/§9 OQ-1,
    /// M-LG3). Same soft-dependency convention <see cref="ParityBridge"/> already uses for
    /// <c>ParityService</c>: zero compile-time reference to DevKit, resolved by assembly-qualified name
    /// with an AppDomain scan fallback (BepInEx load order means DevKit's assembly may not yet be
    /// resolvable by name at the moment this first runs), every call fail-safe.
    ///
    /// <b>Assembly note</b> (verified against the actual csproj, not assumed): DevKit.Plugin's own
    /// <c>AssemblyName</c> is <c>FTK2.DevKit</c> (FTK2.DevKit/src/DevKit.Plugin/DevKit.Plugin.csproj) —
    /// NOT <c>ftk2mods.devkit</c>, which is DevKit.Core's assembly name and only carries
    /// <c>ParityService</c> (see <see cref="ParityBridge.ServiceTypeName"/> for that one). DevKit's new
    /// <c>TransportService</c> lives alongside <c>ParityTransport</c> in DevKit.Plugin because it needs
    /// the live game surface DevKit.Core deliberately does not reference.
    ///
    /// <b>DevKit absent or too old</b> is a logged-once no-op everywhere: <see cref="CanSend"/>/
    /// <see cref="Send"/> report false, <see cref="RegisterReceiver"/> silently never wires a receiver.
    /// The loot-grant engine still computes and applies the identical delta on every peer either way
    /// (Mode M mirror, verb spec §2) — only host-push audit is unavailable, and that combat is logged
    /// "unverified" (verb spec §7 failure matrix, row 1).
    /// </summary>
    internal static class LootGrantTransportBridge
    {
        private const string ServiceTypeName = "DevKit.Plugin.TransportService, FTK2.DevKit";
        private const string ServiceTypeShortName = "DevKit.Plugin.TransportService";

        private static bool _resolveAttempted;
        private static Type _serviceType;
        private static PropertyInfo _canSendProperty;
        private static MethodInfo _sendMethod;
        private static MethodInfo _registerReceiverMethod;
        private static bool _loggedAbsent;

        /// <summary>True once DevKit's TransportService resolved with the expected member shape.</summary>
        internal static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                return _sendMethod != null && _canSendProperty != null && _registerReceiverMethod != null;
            }
        }

        /// <summary>Mirrors <c>TransportService.CanSend</c>. False (never throws) if DevKit is absent,
        /// too old, or the read itself fails.</summary>
        internal static bool CanSend()
        {
            try
            {
                EnsureResolved();
                if (_canSendProperty == null) return false;
                object v = _canSendProperty.GetValue(null, null);
                return v is bool && (bool)v;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning("[ClassForge][CLASSFORGE_LOOT] transport CanSend probe failed (fail-safe, treated as false): " + ex);
                return false;
            }
        }

        /// <summary>Mirrors <c>TransportService.Send(string)</c>. Never throws; false means the payload
        /// was not sent (DevKit absent, unresolved, or the invoke itself failed).</summary>
        internal static bool Send(string payloadJson)
        {
            try
            {
                EnsureResolved();
                if (_sendMethod == null) return false;
                object result = _sendMethod.Invoke(null, new object[] { payloadJson });
                return result is bool && (bool)result;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning("[ClassForge][CLASSFORGE_LOOT] transport send failed (fail-safe, non-fatal): " + ex);
                return false;
            }
        }

        /// <summary>Mirrors <c>TransportService.RegisterReceiver(string, Action&lt;string&gt;)</c>. A
        /// no-op (logged once) when DevKit is absent or too old. Never throws.</summary>
        internal static void RegisterReceiver(string actionKey, Action<string> handler)
        {
            try
            {
                EnsureResolved();
                if (_registerReceiverMethod == null)
                {
                    LogAbsentOnce();
                    return;
                }
                _registerReceiverMethod.Invoke(null, new object[] { actionKey, handler });
                ClassForgePlugin.Log.LogInfo("[ClassForge][CLASSFORGE_LOOT] registered receiver for '" + actionKey + "' with FTK2.DevKit TransportService.");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning("[ClassForge][CLASSFORGE_LOOT] transport receiver registration failed (fail-safe, non-fatal): " + ex);
            }
        }

        private static void EnsureResolved()
        {
            if (_resolveAttempted) return;
            _resolveAttempted = true;
            try
            {
                _serviceType = ResolveServiceType();
                if (_serviceType == null)
                {
                    LogAbsentOnce();
                    return;
                }

                _canSendProperty = _serviceType.GetProperty("CanSend", BindingFlags.Public | BindingFlags.Static);
                _sendMethod = _serviceType.GetMethod("Send", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string) }, null);
                _registerReceiverMethod = _serviceType.GetMethod("RegisterReceiver", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(Action<string>) }, null);

                if (_canSendProperty == null || _sendMethod == null || _registerReceiverMethod == null)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge][CLASSFORGE_LOOT] " + ServiceTypeShortName + " found but does not expose the " +
                        "expected CanSend/Send(string)/RegisterReceiver(string,Action<string>) surface -- loot-grant " +
                        "transport disabled (fail-safe). DevKit version mismatch?");
                }
                else
                {
                    ClassForgePlugin.Log.LogInfo("[ClassForge][CLASSFORGE_LOOT] bridged to FTK2.DevKit TransportService.");
                }
            }
            catch (Exception ex)
            {
                _serviceType = null;
                _canSendProperty = null;
                _sendMethod = null;
                _registerReceiverMethod = null;
                ClassForgePlugin.Log.LogWarning("[ClassForge][CLASSFORGE_LOOT] transport bridge resolution failed (fail-safe, non-fatal): " + ex);
            }
        }

        private static Type ResolveServiceType()
        {
            try
            {
                Type direct = Type.GetType(ServiceTypeName, false);
                if (direct != null) return direct;
            }
            catch
            {
                // fall through to the scan
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type candidate;
                try { candidate = assemblies[i].GetType(ServiceTypeShortName, false); }
                catch { continue; }
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static void LogAbsentOnce()
        {
            if (_loggedAbsent) return;
            _loggedAbsent = true;
            ClassForgePlugin.Log.LogInfo(
                "[ClassForge][CLASSFORGE_LOOT] FTK2.DevKit TransportService not present -- loot-grant host " +
                "send/receive is unavailable (no-op, fail-safe). Grants still compute and apply identically on " +
                "every peer (Mode M mirror, verb spec §2); combats are logged \"unverified\" without DevKit installed.");
        }
    }
}
