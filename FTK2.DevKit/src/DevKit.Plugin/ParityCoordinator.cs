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
    /// <b>Session/host detection is wired, not guessed</b> (MP review M3; SPEC §11.8's open question
    /// is answered by the repo's own decompile). Both flags come from <see cref="GameSurface"/>:
    /// <c>NetworkData.PlayingOnlineMultiplayer</c> (<c>NetworkData.cs:61</c>) gates the whole
    /// handshake so single-player takes no network code path at all, and <c>NetworkData.IsHost</c>
    /// (<c>NetworkData.cs:24</c>) decides whether this peer answers a late joiner's
    /// <c>FTK2MODS_PARITY_REQUEST_V1</c>. Both are re-read on every session start, because a process
    /// can host one session and join the next.
    ///
    /// If the flags cannot be resolved, both read <c>false</c>: DevKit then does nothing at all
    /// rather than broadcasting into a session it cannot reason about.
    /// <c>ParityService.SetIsHost</c> remains a public override so tests can drive the host paths
    /// without a live game.
    /// </summary>
    internal static class ParityCoordinator
    {
        private static bool _initialized;
        private static string _localDataHash = string.Empty;

        /// <summary>Task #12: the kickoff can race <c>PlayingOnlineMultiplayer</c> (host log
        /// 2026-08-15: the flag was still false at <c>PartyManagementDirector.Initialize</c>, so the
        /// handshake silently skipped and no verdict ever arrived). The latch keeps the handshake
        /// OWED until a kickoff actually broadcast while the flag was up; the receive prefix retries
        /// it on the first lobby traffic (<see cref="OnNetworkTrafficObserved"/>).</summary>
        private static readonly HandshakeRetryLatch _handshakeLatch = new HandshakeRetryLatch();

        /// <summary>Re-entrancy guard for the retry path: it runs inside the network receive prefix,
        /// and the kickoff it triggers can itself pump actions synchronously. Main-thread only, so a
        /// plain bool is sufficient.</summary>
        private static bool _retryEntered;

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

            // M4b: one ceiling for every snapshot DevKit emits, including the host's replies.
            ParityService.SetMaxPayloadBytes(DevKitPlugin.MaxParityPayloadBytes.Value);
            DevKitPlugin.MaxParityPayloadBytes.SettingChanged += delegate
            {
                ParityService.SetMaxPayloadBytes(DevKitPlugin.MaxParityPayloadBytes.Value);
            };

            // Peer identity: no peer-id API has been identified yet (SPEC §11.8). A per-process id is
            // enough for what ParityService uses it for - labelling divergences and suppressing our
            // own echoed broadcast - and never feeds a hash, so it cannot affect R1/R2 determinism.
            ParityService.SetLocalPeerId("peer-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            // Host-ness is unknown until a session exists; OnSessionStarted reads the real flag.
            ParityService.SetIsHost(false);

            _localDataHash = ComputeOwnDataHash();
            ParityService.Register(DevKitPlugin.Guid, DevKitPlugin.Version, _localDataHash, new string[0]);
        }

        /// <summary>
        /// Session started/joined: read the real session flags, broadcast our snapshot, then (as a
        /// possible late joiner) ask the host for a fresh one rather than trusting stale state
        /// (SPEC §3 late-join re-query).
        /// </summary>
        internal static void OnSessionStarted(object directorInstance, string phase = "adventure")
        {
            if (!_initialized) return;

            // M4b: a transport failure blinds this peer for one session at most. Clearing here is
            // what makes the retry "re-probe on next session start" rather than process-permanent.
            // A traffic-triggered RETRY (task #12) deliberately does NOT come through here — these
            // resets would wipe verdict state mid-handshake.
            ParityTransport.ResetForNewSession();
            ParityService.ResetSession();
            _handshakeLatch.OnSessionStart();

            TryRunHandshake(directorInstance, phase, true);
        }

        /// <summary>
        /// Task #12 retry entry: called from the network receive prefix on EVERY observed action.
        /// If the session-start kickoff was withheld because <c>PlayingOnlineMultiplayer</c> had not
        /// flipped yet, the first received lobby traffic proves the session is live and runs the
        /// handshake now — the host hears it on the joiner's first stat sync, the joiner on its first
        /// received action. At most one successful handshake per session (the latch); a failed send
        /// stays owed, bounded by the transport's own per-session failure cap. Hot path: the latch
        /// check is one bool read once the handshake completed.
        /// </summary>
        internal static void OnNetworkTrafficObserved(object directorInstance)
        {
            if (!_initialized) return;
            if (_handshakeLatch.Completed) return;
            if (_retryEntered) return;
            _retryEntered = true;
            try
            {
                TryRunHandshake(directorInstance, "party phase, first lobby traffic", false);
            }
            finally
            {
                _retryEntered = false;
            }
        }

        private static void TryRunHandshake(object directorInstance, string phase, bool initialKickoff)
        {
            // The Initialize postfix always supplies __instance; fall back to the cached director so a
            // future non-instance trigger still reaches the session flags.
            if (directorInstance == null) directorInstance = ParityTransport.CurrentDirector;

            // M3: gate the entire handshake on the game's own MP flag. In single-player the game's
            // sender returns false before touching the network anyway (AdventureDirector.cs:15137),
            // but taking no code path at all is the stronger guarantee.
            bool online = GameSurface.IsOnlineMultiplayer(directorInstance);
            if (!_handshakeLatch.ShouldAttempt(online))
            {
                if (initialKickoff && !online)
                {
                    // Task #12: promoted from Verbose — this silent branch is exactly the race that
                    // left MP traits fail-closed with nothing in the log.
                    DevKitPlugin.Log.LogInfo("ParityService: not an online multiplayer session at "
                        + phase + " init (NetworkData.PlayingOnlineMultiplayer=false) - if this "
                        + "becomes an online session, the handshake retries on the first received "
                        + "lobby traffic.");
                }
                else if (!initialKickoff)
                {
                    DevKitPlugin.Verbose("ParityService: handshake retry skipped (online="
                        + (online ? "true" : "false") + ", completed="
                        + (_handshakeLatch.Completed ? "true" : "false") + ").");
                }
                return;
            }

            bool isHost = GameSurface.IsHost(directorInstance);
            ParityService.SetIsHost(isHost);
            DevKitPlugin.Log.LogInfo("ParityService: online multiplayer session detected (" + phase
                + " phase), isHost=" + (isHost ? "true" : "false")
                + "; running the FTK2MODS_PARITY_V1 handshake.");

            string snapshot = ParityService.BuildCappedSnapshotPayload();
            bool sent = ParityTransport.Send(snapshot);
            _handshakeLatch.OnAttemptResult(sent);   // task #12: only a successful broadcast retires the retry
            if (!sent)
            {
                DevKitPlugin.Log.LogWarning("ParityService: could not broadcast FTK2MODS_PARITY_V1 - "
                    + "this peer may be invisible to the parity handshake this session. "
                    + ParityTransport.DescribeState());
            }

            // MP review m5: send the REQUEST even when the broadcast failed. It is the one message a
            // send-broken peer most needs, and a non-host that stays silent is indistinguishable from
            // a healthy peer. A host does not request from itself.
            if (!isHost)
            {
                if (!ParityTransport.Send(ParityService.BuildRequestPayload()) && !sent)
                {
                    DevKitPlugin.Log.LogError("ParityService: this peer can neither broadcast nor request - "
                        + "it is INVISIBLE to the parity handshake this session and no divergence involving it "
                        + "will be detected. " + ParityTransport.DescribeState());
                }
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
            ParityTransport.Send(ParityService.BuildCappedSnapshotPayload());
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
