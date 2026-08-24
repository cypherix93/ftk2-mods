using System;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using FTK2Mods.Crucible;

namespace Crucible.Plugin
{
    [BepInPlugin(PluginGuid, "FTK2 Crucible", "0.1.0")]
    public class CruciblePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ftk2mods.crucible";

        /// <summary>
        /// Env var overrides. Two game instances on one machine share a single BepInEx config file,
        /// so the RPC port cannot come from config alone — the second instance would fail to bind.
        /// A launcher sets these per process to give each peer its own port and identity, which is
        /// what makes multiplayer debugging across instances possible (SPEC §9).
        /// </summary>
        public const string EnvPort = "CRUCIBLE_RPC_PORT";
        public const string EnvInstance = "CRUCIBLE_INSTANCE";

        public static CruciblePlugin Instance;
        public static TraceWriter Trace;

        /// <summary>
        /// Plain-static mirror of [Safety] ForceSinglePlayer, captured in Awake.
        ///
        /// Per-tick code MUST read this rather than <c>Instance.CfgForceSinglePlayer.Value</c>.
        /// <see cref="Instance"/> is a MonoBehaviour and this plugin's host GameObject is explicitly
        /// destroyed at the end of chainloader startup (see OnDestroy). Unity overloads
        /// <c>operator==</c> so a destroyed component compares EQUAL TO NULL while the managed
        /// reference is still perfectly alive — so `Instance == null` silently became true a second
        /// after boot, and every `AutoApplyTick` guarded by it returned early forever. Measured
        /// 2026-08-23: the focus gate and the Input System background behaviour were BOTH never
        /// auto-applied for this reason, which is what made unattended runs look frozen.
        /// A plain static has no Unity lifetime semantics and cannot fake-null.
        /// </summary>
        internal static bool ForceSinglePlayerEnabled;

        public ConfigEntry<bool> CfgEnabled;
        public ConfigEntry<bool> CfgVerbose;
        public ConfigEntry<bool> CfgConsoleEnabled;
        public ConfigEntry<KeyboardShortcut> CfgConsoleKey;
        public ConfigEntry<KeyboardShortcut> CfgScreenshotKey;
        public ConfigEntry<string> CfgArtifactFolder;
        public ConfigEntry<bool> CfgHideUiForShots;
        public ConfigEntry<int> CfgSuperSize;
        public ConfigEntry<bool> CfgRpcEnabled;
        public ConfigEntry<int> CfgRpcPort;
        public ConfigEntry<string> CfgRpcToken;
        public ConfigEntry<int> CfgMaxWorkPerFrame;
        public ConfigEntry<bool> CfgTraceEnabled;
        public ConfigEntry<bool> CfgAllowMutationsInMP;
        public ConfigEntry<string> CfgAllowedCommands;
        public ConfigEntry<bool> CfgForceSinglePlayer;

        private ManualLogSource _log;
        private Harmony _harmony;
        private RpcServer _rpc;
        private bool _consoleHeld;
        private bool _screenshotHeld;

        /// <summary>Identity of this peer, used in trace/screenshot names. Defaults to "p1".</summary>
        public string InstanceName { get; private set; }

        private void Awake()
        {
            Instance = this;
            _log = Logger;

            CfgEnabled = Config.Bind("General", "Enabled", false,
                "Master switch. Off by default: Crucible is dev tooling, not a gameplay mod.");
            CfgVerbose = Config.Bind("General", "VerboseLogging", false, "Log every command dispatch.");

            CfgConsoleEnabled = Config.Bind("Console", "Enabled", true,
                "Revive the game's own built-in developer console (already present in the retail build).");
            CfgConsoleKey = Config.Bind("Console", "ToggleKey", new KeyboardShortcut(KeyCode.F1),
                "Toggles the console overlay.");

            CfgScreenshotKey = Config.Bind("Capture", "ScreenshotKey", new KeyboardShortcut(KeyCode.F2),
                "Writes a screenshot to the artifact folder.");
            CfgArtifactFolder = Config.Bind("Capture", "ArtifactFolder", "BepInEx/crucible-artifacts",
                "Artifact root, relative to the game folder.");
            CfgHideUiForShots = Config.Bind("Capture", "HideUiForShots", false,
                "Run the game's ToggleUI command around captures for clean screenshots.");
            CfgSuperSize = Config.Bind("Capture", "SuperSize", 1, "ScreenCapture supersize factor (1 = native).");

            CfgRpcEnabled = Config.Bind("Rpc", "Enabled", false,
                "Loopback HTTP control surface for the MCP server.");
            CfgRpcPort = Config.Bind("Rpc", "Port", 8787,
                "Port on 127.0.0.1. Overridden per-process by the " + EnvPort + " environment variable.");
            CfgRpcToken = Config.Bind("Rpc", "Token", "",
                "If set, requests must carry it as the X-Crucible-Token header.");
            CfgMaxWorkPerFrame = Config.Bind("Rpc", "MaxWorkPerFrame", 4, "Queued work items drained per frame.");

            CfgTraceEnabled = Config.Bind("Trace", "Enabled", true, "Write a JSONL session trace.");

            CfgAllowMutationsInMP = Config.Bind("Safety", "AllowMutationsInMP", false,
                "DANGER: allows state-mutating commands during an online session. Can desync peers.");
            CfgAllowedCommands = Config.Bind("Safety", "AllowedCommands", "",
                "Comma-separated command allowlist. Empty means all commands are permitted.");
            CfgForceSinglePlayer = Config.Bind("Safety", "ForceSinglePlayer", true,
                "Suppresses the game's own boot-time auto-rejoin (MainMenuDirector.TryAutoJoinRoom) so "
                + "automated test runs always start in a clean single-player state instead of landing in "
                + "the multiplayer lobby. On by default: this is a test harness.");

            ForceSinglePlayerEnabled = CfgForceSinglePlayer.Value;

            InstanceName = ReadEnv(EnvInstance);
            if (string.IsNullOrEmpty(InstanceName)) InstanceName = "p1";

            if (!CfgEnabled.Value)
            {
                _log.LogInfo("Crucible disabled ([General] Enabled = false). Doing nothing.");
                return;
            }

            _log.LogInfo("Crucible starting as instance '" + InstanceName + "'.");

            // Survive scene loads. Without this the host object is destroyed on the first scene
            // transition, OnDestroy runs, and the RPC listener is torn down seconds after startup —
            // which presents as "RPC logged listening but nothing is bound".
            DontDestroyOnLoad(gameObject);

            if (CfgTraceEnabled.Value)
            {
                try
                {
                    string sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                        + "_" + InstanceName;
                    Trace = new TraceWriter(Path.Combine(ArtifactRoot, "traces"), sessionId, 500);
                    _log.LogInfo("Trace: " + Trace.FilePath);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Trace disabled (" + ex.Message + ")");
                }
            }

            _harmony = new Harmony(PluginGuid);
            GameBridge.Initialize(_log);
            ReflectionCommands.Initialize(_log);
            UiCommands.Initialize(_log);
            GamepadCommands.Initialize(_log);
            InputBackgroundCommands.Initialize(_log);
            MouseCommands.Initialize(_log);
            KeyboardCommands.Initialize(_log);
            FixtureCommands.Initialize(_log);
            AbilityCommands.Initialize(_log);
            ConfigCommands.Initialize(_log);
            DebugVerbCommands.Initialize(_log);
            ChaosCommands.Initialize(_harmony, _log);
            MainThreadPump.Initialize(_harmony, _log);
            SinglePlayerGuard.Initialize(_harmony, _log);
            TurnHooks.Initialize(_harmony, _log);
            InputFocusGateCommands.Initialize(_harmony, _log);

            // Registration is retried on the tick, not done in Awake: the game's command registry
            // does not exist until RouterMono has started, so registering here throws from inside
            // CommandLineHelper. See ReflectionCommands.TryRegister.
            MainThreadPump.OnTick = delegate
            {
                ReflectionCommands.TryRegister();
                SinglePlayerGuard.Tick();
                InputBackgroundCommands.AutoApplyTick();
                InputFocusGateCommands.AutoApplyTick();
                KeyboardCommands.Tick();
                GamepadCommands.Tick();
                PollHotkeys();
            };

            if (CfgRpcEnabled.Value)
            {
                _rpc = new RpcServer(ResolvePort(), CfgRpcToken.Value);
                _rpc.Start();
            }

            _log.LogInfo("Crucible ready.");
        }

        /// <summary>Env var wins over config so per-process launchers can assign distinct ports.</summary>
        private int ResolvePort()
        {
            string raw = ReadEnv(EnvPort);
            int parsed;
            if (!string.IsNullOrEmpty(raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                && parsed > 0 && parsed < 65536)
            {
                _log.LogInfo("RPC port " + parsed + " (from " + EnvPort + ")");
                return parsed;
            }
            return CfgRpcPort.Value;
        }

        private static string ReadEnv(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch (Exception) { return null; }
        }

        private void PollHotkeys()
        {
            // Deferred registration: the game's command registry doesn't exist yet during Awake,
            // so this retries each tick until UiCommands.TryRegister reports every command bound.
            UiCommands.TryRegister();
            GamepadCommands.TryRegister();
            InputBackgroundCommands.TryRegister();
            MouseCommands.TryRegister();
            KeyboardCommands.TryRegister();
            FixtureCommands.TryRegister();
            AbilityCommands.TryRegister();
            ConfigCommands.TryRegister();
            DebugVerbCommands.TryRegister();
            ChaosCommands.TryRegister();
            InputFocusGateCommands.TryRegister();

            if (CfgConsoleEnabled.Value)
            {
                bool down = CfgConsoleKey.Value.IsDown();
                if (down && !_consoleHeld) GameBridge.ToggleConsole();
                _consoleHeld = down;
            }

            bool shot = CfgScreenshotKey.Value.IsDown();
            if (shot && !_screenshotHeld)
            {
                try { CaptureService.RequestScreenshot("hotkey"); }
                catch (Exception ex) { _log.LogWarning("Screenshot failed: " + ex.Message); }
            }
            _screenshotHeld = shot;
        }

        public string ArtifactRoot
        {
            get { return Path.Combine(Paths.GameRootPath, CfgArtifactFolder.Value); }
        }

        public string[] Allowlist
        {
            get
            {
                string raw = CfgAllowedCommands.Value;
                if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0) return new string[0];
                return raw.Split(',');
            }
        }

        public static void Log(string message)
        {
            if (Instance != null && Instance._log != null) Instance._log.LogInfo(message);
            DevKitBridge.Log("crucible", message);
        }

        public static void LogVerbose(string message)
        {
            if (Instance != null && Instance.CfgVerbose != null && Instance.CfgVerbose.Value) Log(message);
        }

        /// <summary>
        /// Set only by a real application shutdown. The plugin's host GameObject is destroyed at the end
        /// of BepInEx chainloader startup on this build — an explicit Destroy(), which DontDestroyOnLoad
        /// does not prevent — so tying the RPC listener's lifetime to OnDestroy killed it seconds after it
        /// bound. The listener outlives this component on purpose; only a genuine quit tears it down.
        /// </summary>
        private static bool _quitting;

        private void OnApplicationQuit()
        {
            _quitting = true;
        }

        private void OnDestroy()
        {
            if (!_quitting)
            {
                if (_log != null)
                {
                    _log.LogInfo("Crucible host object destroyed, but the application is still running — "
                        + "leaving the RPC listener up.");
                }
                if (Trace != null) Trace.Flush();
                return;
            }

            if (_log != null) _log.LogInfo("Crucible OnDestroy (application quitting) — shutting down RPC.");
            if (_rpc != null) _rpc.Stop();
            if (Trace != null) Trace.Flush();
        }
    }
}
