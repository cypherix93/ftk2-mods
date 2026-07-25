using System;
using System.IO;
using BepInEx.Logging;
using FTK2Mods.DevKit;

namespace DevKit.Plugin
{
    /// <summary>
    /// Glue between the host-agnostic <see cref="ParityService"/> (DevKit.Core) and the game:
    /// wires the logger and the policy knob, registers DevKit's own tuple, drives the host/join
    /// exchange, and pumps replies back out through <see cref="ParityTransport"/>.
    ///
    /// <b>Session/host detection is an open question</b> (SPEC §11.8: no networking/session-state
    /// flag has been identified yet). This coordinator therefore <i>fails closed</i> — it always
    /// runs the handshake on adventure init and treats itself as a non-host unless proven otherwise.
    /// Both halves of that are safe:
    ///  - running the exchange in single-player is a no-op (nothing receives it, nothing replies);
    ///  - a peer that wrongly believes it is not the host simply doesn't answer a late-join
    ///    <c>FTK2MODS_PARITY_REQUEST_V1</c>, while every peer still broadcasts its own snapshot on
    ///    session start — so the mismatch is still detected on both sides.
    /// </summary>
    internal static class ParityCoordinator
    {
        private static bool _initialized;
        private static string _localDataHash = string.Empty;

        /// <summary>DevKit's own <c>dataHash</c> over its <c>data/</c> folder.</summary>
        internal static string LocalDataHash
        {
            get { return _localDataHash.Length == 0 ? "(none)" : _localDataHash; }
        }

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            ParityService.SetLogger(RouteLog);

            if (!ParityService.SetPolicy(DevKitPlugin.OnParityMismatch.Value))
            {
                DevKitPlugin.Log.LogWarning("[Multiplayer] OnParityMismatch='" + DevKitPlugin.OnParityMismatch.Value
                    + "' is not one of WarnOnly|WarnAndSafeMode|Block; falling back to " + ParityService.GetPolicy() + ".");
            }
            DevKitPlugin.OnParityMismatch.SettingChanged += delegate
            {
                ParityService.SetPolicy(DevKitPlugin.OnParityMismatch.Value);
                DevKitPlugin.Log.LogInfo("ParityService: OnParityMismatch is now " + ParityService.GetPolicy() + ".");
            };

            // Peer identity: no peer-id API has been identified yet (SPEC §11.8). A per-process id is
            // enough for what ParityService uses it for - labelling divergences and suppressing our
            // own echoed broadcast - and never feeds a hash, so it cannot affect R1/R2 determinism.
            ParityService.SetLocalPeerId("peer-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ParityService.SetIsHost(false);

            _localDataHash = ComputeOwnDataHash();
            ParityService.Register(DevKitPlugin.Guid, DevKitPlugin.Version, _localDataHash, new string[0]);
        }

        /// <summary>
        /// Session started/joined: broadcast our snapshot, then (as a possible late joiner) ask the
        /// host for a fresh one rather than trusting stale state (SPEC §3 late-join re-query).
        /// </summary>
        internal static void OnSessionStarted()
        {
            if (!_initialized) return;
            ParityService.ResetSession();

            string snapshot = ParityService.BuildSnapshotPayload();
            bool sent = ParityTransport.Send(snapshot);
            if (!sent)
            {
                DevKitPlugin.Log.LogWarning("ParityService: could not broadcast FTK2MODS_PARITY_V1 - "
                    + "this peer is invisible to the parity handshake this session. " + ParityTransport.DescribeState());
                return;
            }
            if (!ParityService.GetIsHost())
            {
                ParityTransport.Send(ParityService.BuildRequestPayload());
            }
        }

        /// <summary>
        /// Every string argument the game's network handler receives is offered here. Non-parity
        /// traffic is rejected by an ordinal pre-filter and never parsed.
        /// </summary>
        internal static void OnNetworkPayloadObserved(string payload)
        {
            if (!_initialized) return;
            if (!ParityPayloadCodec.LooksLikeParityPayload(payload)) return;

            string reply = ParityService.HandleIncomingPayload(payload, null);
            if (!string.IsNullOrEmpty(reply)) ParityTransport.Send(reply);

            string banner = ParityService.TakePendingBanner();
            if (banner.Length > 0) ShowBanner(banner);
        }

        /// <summary>
        /// Called after any successful hot-reload of DevKit's own data (R5 / SPEC §5
        /// <c>RehandshakeOnHotReload</c>): the hash changed, so the handshake must run again.
        /// Wired here for M1 completeness; the hot-reload feature itself lands with the rest of M1.
        /// </summary>
        internal static void OnHotReloadCompleted()
        {
            if (!_initialized) return;
            _localDataHash = ComputeOwnDataHash();
            ParityService.UpdateDataHash(DevKitPlugin.Guid, _localDataHash);
            if (!DevKitPlugin.RehandshakeOnHotReload.Value)
            {
                DevKitPlugin.Log.LogWarning("ParityService: RehandshakeOnHotReload=false - peers may now disagree "
                    + "about this mod's data without anyone being told.");
                return;
            }
            ParityTransport.Send(ParityService.BuildSnapshotPayload());
        }

        private static string ComputeOwnDataHash()
        {
            try
            {
                string folder = DevKitPlugin.PluginFolder();
                if (string.IsNullOrEmpty(folder)) return string.Empty;
                string dataFolder = Path.Combine(folder, "data");
                string hash = ParityService.ComputeDataHashForFolder(dataFolder, null);
                if (hash.Length == 0)
                {
                    DevKitPlugin.Log.LogWarning("ParityService: DevKit's own data folder was not found at '"
                        + dataFolder + "'; registering with an empty dataHash (a guaranteed mismatch).");
                }
                return hash;
            }
            catch (Exception ex)
            {
                DevKitPlugin.Log.LogError("ParityService: failed to hash DevKit's own data folder: " + ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Prominent mismatch warning. M1 ships it as a loud, session-visible log banner; the
        /// on-screen in-game banner is deferred to M2 because it needs a verified UI surface
        /// (no UI-toolkit reference is confirmed present in the game's Managed folder yet -
        /// docs/research/build-template-notes.md §2). The text is identical either way, and
        /// <c>dk_dump_parity</c> reproduces it on demand from
        /// <see cref="ParityService.GetLastMismatchSummary"/>.
        /// </summary>
        private static void ShowBanner(string banner)
        {
            ManualLogSource log = DevKitPlugin.Log;
            if (log == null) return;
            log.LogWarning("================ FTK2MODS PARITY ================");
            string[] lines = banner.Split('\n');
            for (int i = 0; i < lines.Length; i++) log.LogWarning(lines[i]);
            log.LogWarning("=================================================");
        }

        private static void RouteLog(string level, string message)
        {
            ManualLogSource log = DevKitPlugin.Log;
            if (log == null) return;
            switch (level)
            {
                case "Debug":
                    DevKitPlugin.Verbose(message);
                    break;
                case "Warning":
                    log.LogWarning(message);
                    break;
                case "Error":
                    log.LogError(message);
                    break;
                default:
                    log.LogInfo(message);
                    break;
            }
        }
    }
}
