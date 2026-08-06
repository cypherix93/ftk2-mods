using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Blessings.Core.Diagnostics;
using Blessings.Core.Model;
using Blessings.Core.Parsing;
using HarmonyLib;

namespace Blessings.Plugin
{
    /// <summary>
    /// FTK2.Blessings M2: the BepInEx entry point (SPEC §5, §6). Owns knobs, loads
    /// <c>blessings.json</c> once at boot via Blessings.Core's pure parser, installs the grant-anchor
    /// Harmony patch, and registers with FTK2.DevKit's ParityService by reflection (§9.6). All
    /// game-facing orchestration (resolve -&gt; verify -&gt; gate -&gt; grant -&gt; latch) lives in
    /// <see cref="GrantAnchorPatches"/>; this class is wiring only.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class BlessingsPlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.blessings";
        public const string Name = "FTK2.Blessings";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static BlessingsPlugin Instance;

        // ---- knobs (§5) ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<string> Mode;
        internal static ConfigEntry<string> OnParityMismatch;

        /// <summary>The parsed roster, or null if load/parse failed (session-disabled, §3.3).</summary>
        internal static BlessingsRegistry Registry;

        /// <summary>SHA-256 (with the repo's "sha256:" + 64-hex convention, matching
        /// DevKit.Core.DataHasher/Summoner.Core.Parity.DataHasher) over the pack's raw blessings.json
        /// bytes -- the parity <c>dataHash</c> (§9.6; double coverage of blessings.json alongside
        /// ClassForge's own pack hash is accepted, §11 OQ7).</summary>
        internal static string DataHash = "(none)";

        /// <summary>True once the data-load (blessings.json parse) failed hard -- master fail-closed
        /// latch, distinct from <see cref="DevKitParityBridge.SafeMode"/> (§3.3: "any miss -&gt; the
        /// plugin disables itself for the session with one loud log line").</summary>
        internal static bool LoadFailed;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch; false = no resolution, no grant, pack data inert.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false,
                "Per-entity grant decisions at LogLevel.Debug.");
            Mode = Config.Bind("Blessings", "Mode", "Disabled",
                "Disabled | Random | a literal blessing id (e.g. BLSS_BLOOD_PRICE). Parity-covered as " +
                "feature:Mode=<value> -- every peer must set the same value (§3.4/§9).");
            OnParityMismatch = Config.Bind("Multiplayer", "OnParityMismatch", "WarnAndSafeMode",
                "WarnAndSafeMode (default) | WarnOnly | Block. WarnAndSafeMode/Block: on a mismatch, " +
                "resolution and ALL trait grants stop for the rest of the session on every peer that " +
                "observes it (fail-closed). WarnOnly is a FOOTGUN (§5): logs only, a one-sided grant is " +
                "a guaranteed stat divergence.");

            DevKitParityBridge.SetPolicyDisablesOnMismatch(!IsWarnOnly(OnParityMismatch.Value));

            LoadRegistry();
            ApplyPatches();
            RegisterParity();

            Log.LogInfo(Name + " " + Version + " loaded. Enabled=" + Enabled.Value + ", Mode=" + Mode.Value +
                        ", roster=" + (Registry != null ? Registry.Blessings.Count.ToString() : "(load failed)") +
                        ", dataHash=" + DataHash + ", onParityMismatch=" + OnParityMismatch.Value);
        }

        private static bool IsWarnOnly(string raw)
        {
            return string.Equals(raw, "WarnOnly", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads and parses <c>&lt;plugin&gt;/ClassPacks/BLSS_PACK_EOR_BLESSINGS/blessings.json</c> --
        /// the plugin's own copy, staged alongside the pack ClassForge merges from the same
        /// <c>ClassPacks/</c> root (tools/deploy.ps1). ClassForge never reads this file (§3.3); this is
        /// the ONLY reader. Any failure here (missing file, parse error) sets <see cref="LoadFailed"/> and
        /// leaves <see cref="Registry"/> null -- <see cref="GrantAnchorPatches"/> checks that before doing
        /// anything (fail-closed data dependency, §3.3).
        /// </summary>
        private static void LoadRegistry()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                var path = Path.Combine(pluginDir, "ClassPacks", "BLSS_PACK_EOR_BLESSINGS", "blessings.json");

                if (!File.Exists(path))
                {
                    LoadFailed = true;
                    DataHash = "(missing)";
                    Log.LogError("[Blessings] blessings.json NOT found at '" + path + "' -- this plugin is DISABLED for " +
                                 "the session (fail-safe). Is the BLSS_PACK_EOR_BLESSINGS pack staged under this plugin's folder?");
                    return;
                }

                byte[] bytes = File.ReadAllBytes(path);
                DataHash = ComputeDataHash(bytes);

                string json = Encoding.UTF8.GetString(bytes);
                var parsed = BlessingsRegistryParser.Parse(json);

                foreach (var finding in parsed.Findings) LogFinding(finding);

                if (!parsed.Success)
                {
                    LoadFailed = true;
                    Log.LogError("[Blessings] blessings.json failed to parse -- this plugin is DISABLED for the session (fail-safe). See errors above.");
                    return;
                }

                Registry = parsed.Registry;
            }
            catch (Exception ex)
            {
                LoadFailed = true;
                DataHash = "(error)";
                Log.LogError("[Blessings] blessings.json load threw -- this plugin is DISABLED for the session (fail-safe): " + ex);
            }
        }

        private static void LogFinding(Finding f)
        {
            switch (f.Severity)
            {
                case FindingSeverity.Error:
                    Log.LogError("[Blessings] " + f);
                    break;
                case FindingSeverity.Warn:
                    Log.LogWarning("[Blessings] " + f);
                    break;
                default:
                    if (VerboseLogging.Value) Log.LogDebug("[Blessings] " + f);
                    break;
            }
        }

        private static string ComputeDataHash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
                var sb = new StringBuilder(71);
                sb.Append("sha256:");
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private void ApplyPatches()
        {
            var harmony = new Harmony(Guid);

            // Grant anchor (candidate: AdventureDirector.Initialize, same anchor DevKit's own handshake
            // uses -- GATE E / binding resolution V7). Postfix only: never intercept the original call.
            Patch(harmony, typeof(AdventureDirector), "Initialize",
                postfix: new HarmonyMethod(typeof(GrantAnchorPatches), nameof(GrantAnchorPatches.Initialize_Postfix)));
        }

        /// <summary>docs/CONVENTIONS.md logging convention: one found/NOT-found line per target;
        /// a missing target disables only the feature riding it (fail-safe).</summary>
        private static void Patch(Harmony harmony, Type type, string method, HarmonyMethod postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    Log.LogError("Target NOT found: " + type.Name + "." + method + " -- this feature is disabled (fail-safe).");
                    return;
                }
                harmony.Patch(target, postfix: postfix);
                Log.LogInfo("Target found: " + type.Name + "." + method);
            }
            catch (Exception ex)
            {
                Log.LogError("Target NOT found: " + type.Name + "." + method + " -- patch installation threw, feature disabled (fail-safe): " + ex);
            }
        }

        /// <summary>
        /// §9.6: <c>RegisterWithCallback(guid="ftk2mods.blessings", version=assembly, dataHash=sha256
        /// over blessings.json, enabledFeatures=["feature:Mode=&lt;value&gt;"], onParityFailed -&gt;
        /// SafeMode latch)</c>. Runs regardless of <see cref="Enabled"/>/<see cref="LoadFailed"/> (mirrors
        /// Summoner.Plugin's MP-review-B7 fix: a disabled/broken peer must still be VISIBLE to the
        /// handshake, not silently absent, or a peer expecting Blessings content sees nothing instead of
        /// a real mismatch).
        /// </summary>
        private static void RegisterParity()
        {
            var enabledFeatures = new[] { "feature:Mode=" + (Mode != null ? Mode.Value : "Disabled") };
            DevKitParityBridge.Register(Guid, Version, DataHash, enabledFeatures);
        }
    }
}
