using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FTK2Mods.DevKit;
using HarmonyLib;

namespace DevKit.Plugin
{
    /// <summary>
    /// FTK2.DevKit BepInEx plugin — M1 scope is the ParityService (R1) wiring only:
    /// config knobs, the observe-only <c>AdventureDirector._handleNetworkAction</c> hook, the
    /// host/join <c>FTK2MODS_PARITY_V1</c> exchange, and <c>ParityFailed</c> callback dispatch.
    /// Hot-reload, dumps, the console and the health check are M1/M2/M3 work in the same plugin and
    /// are NOT implemented here.
    ///
    /// Conventions (docs/CONVENTIONS.md): GUID <c>ftk2mods.devkit</c>, master <c>[General] Enabled</c>
    /// knob, one "Target found: X" line per Harmony lookup, and every patch fails safe — a missing
    /// target logs and turns that feature off, it never throws and never changes vanilla behavior.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class DevKitPlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.devkit";
        public const string Name = "FTK2.DevKit";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static DevKitPlugin Instance;

        internal static ConfigEntry<bool> EnabledKnob;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<string> OnParityMismatch;
        internal static ConfigEntry<bool> RehandshakeOnHotReload;
        internal static ConfigEntry<int> ParityRequestTimeoutMs;

        /// <summary>True only if the parity network hook actually resolved its target.</summary>
        internal static bool ParityHookInstalled;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            EnabledKnob = Config.Bind("General", "Enabled", true,
                "Master switch. When false DevKit installs no patches and registers nothing.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false,
                "DevKit's own internal chatter at LogLevel.Debug.");
            OnParityMismatch = Config.Bind("Multiplayer", "OnParityMismatch", "WarnAndSafeMode",
                "Policy applied when the FTK2MODS_PARITY_V1 handshake finds a divergent mod: "
                + "WarnOnly (banner only) | WarnAndSafeMode (default: banner + the diverged mod's SafeMode) | "
                + "Block (refuse to start/continue the session).");
            RehandshakeOnHotReload = Config.Bind("Multiplayer", "RehandshakeOnHotReload", true,
                "After any successful hot-reload in a detected MP session, re-run the parity handshake. "
                + "Turning this off is not recommended (SPEC §5).");
            ParityRequestTimeoutMs = Config.Bind("Multiplayer", "ParityRequestTimeoutMs", 5000,
                "How long a late-joining client waits for the host's FTK2MODS_PARITY_V1 reply to its "
                + "FTK2MODS_PARITY_REQUEST_V1 before logging a timeout warning and retrying once.");

            if (!EnabledKnob.Value)
            {
                Log.LogInfo(Name + " " + Version + " disabled by [General] Enabled=false. No patches installed.");
                return;
            }

            try
            {
                ParityCoordinator.Initialize();
            }
            catch (Exception ex)
            {
                // Fail-safe: DevKit must never take the game down. Without the coordinator the
                // parity handshake is simply absent (sibling mods' Register calls still no-op safely).
                Log.LogError("ParityCoordinator.Initialize failed, parity handshake disabled: " + ex);
            }

            ApplyPatches();

            Log.LogInfo(string.Format(
                "{0} {1} loaded. Enabled={2}, OnParityMismatch={3}, parityHook={4}, dataHash={5}",
                Name, Version, EnabledKnob.Value, ParityService.GetPolicy(),
                ParityHookInstalled ? "installed" : "NOT INSTALLED",
                ParityCoordinator.LocalDataHash));
        }

        private void ApplyPatches()
        {
            Harmony harmony;
            try
            {
                harmony = new Harmony(Guid);
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony instance could not be created; DevKit runs with no patches: " + ex);
                return;
            }

            // Observe-only PREFIX. It returns void, so it is structurally incapable of skipping the
            // original method — deliberate, per docs/research/eor-0760-content-audit.md §3.4, where
            // EOR's `_handleNetworkAction` prefix returns false and swallows messages for peers that
            // don't share its mod set. DevKit must never do that to anyone else's traffic.
            ParityHookInstalled = Patch(harmony, "AdventureDirector", "_handleNetworkAction",
                nameof(ParityPatches.HandleNetworkActionPrefix), null);

            // Candidate "session started/joined" trigger (SPEC §6). If it doesn't resolve, the
            // handshake simply never auto-fires; nothing else breaks.
            Patch(harmony, "AdventureDirector", "Initialize",
                null, nameof(ParityPatches.AdventureDirectorInitializePostfix));
        }

        /// <summary>
        /// Resolves a target and patches it, EOR-style: one "Target found"/"Target NOT found" line
        /// per lookup so a game update's breakage is diagnosable from the BepInEx console alone.
        /// Returns false (feature off) instead of throwing on any failure.
        /// </summary>
        private bool Patch(Harmony harmony, string typeName, string methodName, string prefixName, string postfixName)
        {
            string label = typeName + "." + methodName;
            try
            {
                Type targetType = AccessTools.TypeByName(typeName);
                if (targetType == null)
                {
                    Log.LogError("Target NOT found: " + typeName + " (type unresolved) - feature disabled.");
                    return false;
                }
                MethodBase target = AccessTools.Method(targetType, methodName);
                if (target == null)
                {
                    Log.LogError("Target NOT found: " + label + " - feature disabled.");
                    return false;
                }
                HarmonyMethod prefix = prefixName == null ? null : PatchMethod(prefixName);
                HarmonyMethod postfix = postfixName == null ? null : PatchMethod(postfixName);
                if ((prefixName != null && prefix == null) || (postfixName != null && postfix == null))
                {
                    Log.LogError("Patch method missing for " + label + " - feature disabled.");
                    return false;
                }
                harmony.Patch(target, prefix, postfix);
                Log.LogInfo("Target found: " + label + " (" + DescribeSignature(target) + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError("Patch install failed for " + label + " - feature disabled: " + ex);
                return false;
            }
        }

        private static HarmonyMethod PatchMethod(string name)
        {
            MethodInfo method = typeof(ParityPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return method == null ? null : new HarmonyMethod(method);
        }

        internal static string DescribeSignature(MethodBase method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            string[] parts = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parts[i] = parameters[i].ParameterType.Name + " " + parameters[i].Name;
            }
            return (method.IsStatic ? "static " : "instance ") + method.Name + "(" + string.Join(", ", parts) + ")";
        }

        internal static void Verbose(string message)
        {
            if (VerboseLogging != null && VerboseLogging.Value && Log != null) Log.LogDebug(message);
        }

        /// <summary>Folder this plugin's DLL lives in — the root for DevKit's own <c>data/</c>.</summary>
        internal static string PluginFolder()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(location)) return string.Empty;
                return Path.GetDirectoryName(location);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
