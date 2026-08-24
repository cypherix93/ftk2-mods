using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>
    /// The R1 parity-handshake foundation every <c>ftk2mods.*</c> engine registers with
    /// (docs/MULTIPLAYER.md R1, FTK2.DevKit/SPEC.md §3).
    ///
    /// <b>Reflection-friendly by design.</b> Sibling mods must never take a compile-time reference to
    /// DevKit (SPEC §3, §6), so every entry point below is <c>public static</c> and uses only BCL
    /// types — <c>string</c>, <c>string[]</c>, <c>bool</c>, <c>Action&lt;string[]&gt;</c>. Overloads
    /// are deliberately avoided: each operation has its own distinct method name so a caller can do
    /// a plain <c>GetMethod(name)</c> without disambiguating parameter types. Canonical client-side
    /// call shape (this is the whole integration contract for a sibling mod):
    ///
    /// <code>
    /// var t = Type.GetType("FTK2Mods.DevKit.ParityService, ftk2mods.devkit");
    /// if (t != null) // DevKit not installed -> no-op, never a hard failure
    /// {
    ///     var hash = (string)t.GetMethod("ComputeDataHashForFolder")
    ///                         .Invoke(null, new object[] { myDataFolder, null });
    ///     t.GetMethod("RegisterWithCallback").Invoke(null, new object[] {
    ///         "ftk2mods.warbrain", "1.0.0", hash,
    ///         new[] { "TacticalScoring" },
    ///         new Action&lt;string[]&gt;(OnParityFailed) });   // args: [guid,kind,local,remote,peer,message]
    /// }
    /// </code>
    ///
    /// <b>Everything here is fail-safe</b> (docs/CONVENTIONS.md): no public method throws. Bad input
    /// returns false/empty and logs; a sibling mod's callback that throws is caught and isolated so
    /// one bad mod can never abort the handshake for the rest.
    ///
    /// <b>Thread-safety.</b> All state is behind one lock. Registration happens on the BepInEx
    /// <c>Awake()</c> thread, handshake handling on the Unity main thread via the
    /// <c>_handleNetworkAction</c> hook; the lock keeps those honest. Callbacks are invoked OUTSIDE
    /// the lock so a sibling mod cannot deadlock the service.
    /// </summary>
    public static class ParityService
    {
        private sealed class Entry
        {
            public ParityRegistration Registration;
            public Action<string[]> OnParityFailed;
            /// <summary>Verdict-ARRIVAL subscriber (task #11): fires on Match too, not just mismatch.
            /// An entry may exist with only this set (subscription before Register is legal).</summary>
            public Action<string[]> OnVerdict;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Entry> Registry = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ParityRegistration[]> RemoteSnapshots = new Dictionary<string, ParityRegistration[]>(StringComparer.Ordinal);
        private static readonly HashSet<string> SafeModeGuids = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> RepliedPeers = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Per-peer verdict fingerprint, so a duplicate snapshot doesn't re-dispatch callbacks (m13).</summary>
        private static readonly Dictionary<string, string> LastVerdictSignatures = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly ParityVerdict[] NoVerdicts = new ParityVerdict[0];

        /// <summary>
        /// Upper bound on tracked remote peers (m13). The peer id is sender-supplied, so without a
        /// bound a peer that rotates it grows the dictionaries for the whole session. FTK2 lobbies
        /// are 4 players; 32 is far above any legitimate session.
        /// </summary>
        private const int MaxTrackedPeers = 32;
        private static readonly string[] NoStrings = new string[0];

        private static ParityMismatchPolicy _policy = ParityPolicyEngine.DefaultPolicy;
        private static string _localPeerId = "local";
        private static bool _isHost;
        private static bool _sessionBlocked;
        private static ParityVerdict[] _lastVerdicts = NoVerdicts;
        private static string _lastMismatchSummary = string.Empty;
        private static string _pendingBanner = string.Empty;
        private static int _maxPayloadBytes = DefaultMaxPayloadBytes;
        private static Action<string, string> _logger;

        // ---------------------------------------------------------------- registration

        /// <summary>
        /// Registers (or re-registers, replacing) a mod's parity tuple with no callback.
        /// Returns false for an empty guid. Never throws.
        /// </summary>
        public static bool Register(string pluginGuid, string version, string dataHash, string[] enabledFeatures)
        {
            return RegisterWithCallback(pluginGuid, version, dataHash, enabledFeatures, null);
        }

        /// <summary>
        /// Subscribes a mod to verdict ARRIVAL (task #11): unlike <see cref="RegisterWithCallback"/>,
        /// which only reports mismatches, this fires on every verdict TRANSITION for a peer — Match
        /// included — with args <c>[remotePeerId, "Match"|"Mismatch"]</c>. A mod that fails closed
        /// until a verified Match (ClassForge's MP trait-loadout gate) subscribes here instead of
        /// polling <see cref="GetLastVerdictRows"/>.
        /// <para>A separate method rather than a <c>RegisterWithCallback</c> overload on purpose: the
        /// documented reflection recipe binds <c>GetMethod("RegisterWithCallback")</c> with no
        /// <c>Type[]</c>, and an overload would turn that into <c>AmbiguousMatchException</c> in every
        /// mod that followed the doc. Order-independent with Register: subscribing first is fine.</para>
        /// Returns false for an empty guid or null callback. Never throws.
        /// </summary>
        public static bool RegisterVerdictCallback(string pluginGuid, Action<string[]> onVerdict)
        {
            try
            {
                if (string.IsNullOrEmpty(pluginGuid) || onVerdict == null)
                {
                    Log("Warning", "ParityService.RegisterVerdictCallback rejected: guid and callback are both required.");
                    return false;
                }
                lock (Sync)
                {
                    Entry entry;
                    if (!Registry.TryGetValue(pluginGuid, out entry))
                    {
                        entry = new Entry();
                        Registry[pluginGuid] = entry;
                    }
                    entry.OnVerdict = onVerdict;
                }
                Log("Info", "ParityService: verdict callback registered for '" + pluginGuid + "'.");
                return true;
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.RegisterVerdictCallback failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Registers (or re-registers, replacing) a mod's parity tuple plus its <c>ParityFailed</c>
        /// callback. The callback receives the flat arg layout documented on
        /// <see cref="ParityVerdict.ToCallbackArgs"/>:
        /// <c>[guid, kind, localValue, remoteValue, remotePeerId, message]</c>.
        /// Returns false for an empty guid. Never throws.
        /// </summary>
        public static bool RegisterWithCallback(string pluginGuid, string version, string dataHash,
            string[] enabledFeatures, Action<string[]> onParityFailed)
        {
            try
            {
                ParityRegistration registration = new ParityRegistration(pluginGuid, version, dataHash, enabledFeatures);
                if (registration.Guid.Length == 0)
                {
                    Log("Warning", "ParityService.Register rejected: pluginGuid is null/empty.");
                    return false;
                }
                lock (Sync)
                {
                    Entry entry;
                    if (!Registry.TryGetValue(registration.Guid, out entry))
                    {
                        entry = new Entry();
                        Registry[registration.Guid] = entry;
                    }
                    entry.Registration = registration;
                    if (onParityFailed != null) entry.OnParityFailed = onParityFailed;
                }
                if (!registration.HasWellFormedDataHash)
                {
                    Log("Warning", string.Format(CultureInfo.InvariantCulture,
                        "ParityService: mod '{0}' registered with a missing/malformed dataHash ('{1}') - it will be treated as a GUARANTEED mismatch against every peer.",
                        registration.Guid, registration.DataHash));
                }
                Log("Info", "ParityService: registered " + registration);
                return true;
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.Register failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Removes a registration (and any SafeMode flag for it). Never throws.</summary>
        public static bool Unregister(string pluginGuid)
        {
            if (string.IsNullOrEmpty(pluginGuid)) return false;
            string guid = pluginGuid.Trim();
            lock (Sync)
            {
                SafeModeGuids.Remove(guid);
                return Registry.Remove(guid);
            }
        }

        /// <summary>
        /// Updates just the <c>dataHash</c> after a hot-reload (SPEC §3 "MP interaction", R5).
        /// The caller re-runs the handshake afterwards. Never throws.
        /// </summary>
        public static bool UpdateDataHash(string pluginGuid, string dataHash)
        {
            if (string.IsNullOrEmpty(pluginGuid)) return false;
            string guid = pluginGuid.Trim();
            lock (Sync)
            {
                Entry entry;
                if (!Registry.TryGetValue(guid, out entry)) return false;
                entry.Registration = new ParityRegistration(guid, entry.Registration.Version, dataHash,
                    entry.Registration.EnabledFeatures);
                return true;
            }
        }

        /// <summary>Updates just the enabled-feature set (e.g. a knob toggled at runtime). Never throws.</summary>
        public static bool UpdateFeatures(string pluginGuid, string[] enabledFeatures)
        {
            if (string.IsNullOrEmpty(pluginGuid)) return false;
            string guid = pluginGuid.Trim();
            lock (Sync)
            {
                Entry entry;
                if (!Registry.TryGetValue(guid, out entry)) return false;
                entry.Registration = new ParityRegistration(guid, entry.Registration.Version,
                    entry.Registration.DataHash, enabledFeatures);
                return true;
            }
        }

        /// <summary>Every registered plugin guid, ordinal-sorted.</summary>
        public static string[] GetRegisteredGuids()
        {
            lock (Sync)
            {
                List<string> guids = new List<string>(Registry.Keys);
                guids.Sort(StringComparer.Ordinal);
                return guids.ToArray();
            }
        }

        /// <summary>
        /// One registration as a flat string array — <c>[guid, version, dataHash, feature...]</c>,
        /// or an empty array if the guid isn't registered. Simple types only, for reflection callers.
        /// </summary>
        public static string[] GetRegistrationFields(string pluginGuid)
        {
            if (string.IsNullOrEmpty(pluginGuid)) return NoStrings;
            lock (Sync)
            {
                Entry entry;
                if (!Registry.TryGetValue(pluginGuid.Trim(), out entry)) return NoStrings;
                ParityRegistration r = entry.Registration;
                List<string> fields = new List<string>(3 + r.FeatureCount);
                fields.Add(r.Guid);
                fields.Add(r.Version);
                fields.Add(r.DataHash);
                fields.AddRange(r.EnabledFeatures);
                return fields.ToArray();
            }
        }

        /// <summary>Human-readable report for <c>dk_dump_parity</c> / the <c>parity-check</c> macro (SPEC §2, §7).</summary>
        public static string DumpRegistrations()
        {
            StringBuilder sb = new StringBuilder();
            lock (Sync)
            {
                sb.Append("ParityService: localPeerId='").Append(_localPeerId)
                  .Append("', isHost=").Append(_isHost ? "true" : "false")
                  .Append(", policy=").Append(ParityPolicyEngine.PolicyName(_policy))
                  .Append(", sessionBlocked=").Append(_sessionBlocked ? "true" : "false")
                  .Append('\n');
                List<string> guids = new List<string>(Registry.Keys);
                guids.Sort(StringComparer.Ordinal);
                sb.Append("Local registrations (").Append(guids.Count.ToString(CultureInfo.InvariantCulture)).Append("):\n");
                for (int i = 0; i < guids.Count; i++)
                {
                    Entry entry = Registry[guids[i]];
                    sb.Append("  - ").Append(entry.Registration);
                    if (SafeModeGuids.Contains(guids[i])) sb.Append("  [SAFEMODE]");
                    if (entry.OnParityFailed == null) sb.Append("  (no ParityFailed callback)");
                    sb.Append('\n');
                }
                List<string> peers = new List<string>(RemoteSnapshots.Keys);
                peers.Sort(StringComparer.Ordinal);
                sb.Append("Known peers (").Append(peers.Count.ToString(CultureInfo.InvariantCulture)).Append("):\n");
                for (int i = 0; i < peers.Count; i++)
                {
                    ParityRegistration[] regs = RemoteSnapshots[peers[i]];
                    sb.Append("  - peer '").Append(peers[i]).Append("' reported ")
                      .Append(regs.Length.ToString(CultureInfo.InvariantCulture)).Append(" mod(s)\n");
                    for (int j = 0; j < regs.Length; j++)
                    {
                        sb.Append("      ").Append(regs[j]).Append('\n');
                    }
                }
                sb.Append("Last mismatch result: ");
                sb.Append(_lastMismatchSummary.Length == 0 ? "(none - all peers matched)" : "\n" + _lastMismatchSummary);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- hashing

        /// <summary>
        /// Hashes every file under <paramref name="rootFolder"/> with the shared R1 rules.
        /// Pass <c>null</c> for <paramref name="exclusionGlobs"/> to use
        /// <see cref="GetDefaultExclusionGlobs"/> (localization + presentation assets excluded).
        /// Returns "" if the folder is missing — an empty hash is a guaranteed mismatch, by design.
        /// Never throws.
        /// </summary>
        public static string ComputeDataHashForFolder(string rootFolder, string[] exclusionGlobs)
        {
            try
            {
                return DataHasher.ComputeFolderHash(rootFolder, exclusionGlobs);
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.ComputeDataHashForFolder failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Hashes an explicit file list, relative to <paramref name="rootFolder"/>, with the shared
        /// R1 rules. Never throws.
        /// </summary>
        public static string ComputeDataHashForFiles(string rootFolder, string[] filePaths, string[] exclusionGlobs)
        {
            try
            {
                return DataHasher.ComputeFileListHash(rootFolder, filePaths, exclusionGlobs);
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.ComputeDataHashForFiles failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>The default exclusion glob set (fresh copy).</summary>
        public static string[] GetDefaultExclusionGlobs()
        {
            return DataHasher.GetDefaultExclusionGlobs();
        }

        // ---------------------------------------------------------------- session / policy

        public static void SetLocalPeerId(string peerId)
        {
            lock (Sync)
            {
                _localPeerId = string.IsNullOrEmpty(peerId) ? "local" : peerId.Trim();
            }
        }

        public static string GetLocalPeerId()
        {
            lock (Sync) { return _localPeerId; }
        }

        public static void SetIsHost(bool isHost)
        {
            lock (Sync) { _isHost = isHost; }
        }

        public static bool GetIsHost()
        {
            lock (Sync) { return _isHost; }
        }

        /// <summary>Sets the <c>[Multiplayer] OnParityMismatch</c> policy by name. False if unrecognized (policy unchanged).</summary>
        public static bool SetPolicy(string policyName)
        {
            ParityMismatchPolicy parsed;
            if (!ParityPolicyEngine.TryParsePolicy(policyName, out parsed))
            {
                Log("Warning", "ParityService: unrecognized OnParityMismatch policy '" + (policyName ?? "(null)")
                    + "'; keeping " + GetPolicy() + ".");
                return false;
            }
            lock (Sync) { _policy = parsed; }
            return true;
        }

        public static string GetPolicy()
        {
            lock (Sync) { return ParityPolicyEngine.PolicyName(_policy); }
        }

        /// <summary>True if this mod is in SafeMode for the session (WarnAndSafeMode + a mismatch).</summary>
        public static bool IsInSafeMode(string pluginGuid)
        {
            if (string.IsNullOrEmpty(pluginGuid)) return false;
            lock (Sync) { return SafeModeGuids.Contains(pluginGuid.Trim()); }
        }

        /// <summary>Every guid currently in SafeMode, ordinal-sorted.</summary>
        public static string[] GetSafeModeGuids()
        {
            lock (Sync)
            {
                List<string> guids = new List<string>(SafeModeGuids);
                guids.Sort(StringComparer.Ordinal);
                return guids.ToArray();
            }
        }

        /// <summary>True once a <c>Block</c>-policy mismatch has been seen this session.</summary>
        public static bool IsSessionBlocked()
        {
            lock (Sync) { return _sessionBlocked; }
        }

        /// <summary>
        /// Clears per-session state (remote snapshots, SafeMode flags, block flag, banner) while
        /// KEEPING local registrations — mods register once at <c>Awake()</c>, sessions come and go.
        /// </summary>
        public static void ResetSession()
        {
            lock (Sync)
            {
                RemoteSnapshots.Clear();
                SafeModeGuids.Clear();
                RepliedPeers.Clear();
                LastVerdictSignatures.Clear();
                _sessionBlocked = false;
                _lastVerdicts = NoVerdicts;
                _lastMismatchSummary = string.Empty;
                _pendingBanner = string.Empty;
            }
        }

        /// <summary>
        /// Full reset including local registrations. Test-facing; the plugin never calls this.
        /// </summary>
        public static void ResetAll()
        {
            lock (Sync)
            {
                Registry.Clear();
                _policy = ParityPolicyEngine.DefaultPolicy;
                _localPeerId = "local";
                _isHost = false;
            }
            ResetSession();
        }

        /// <summary>
        /// Routes DevKit's own diagnostics to a host logger: <c>(level, message)</c> where level is
        /// one of the BepInEx LogLevel names. BCL-only signature so Core stays host-agnostic.
        /// </summary>
        public static void SetLogger(Action<string, string> logger)
        {
            lock (Sync) { _logger = logger; }
        }

        // ---------------------------------------------------------------- handshake

        /// <summary>The local peer's full <c>FTK2MODS_PARITY_V1</c> snapshot payload. Never throws.</summary>
        public static string BuildSnapshotPayload()
        {
            try
            {
                string peerId;
                List<ParityRegistration> regs = new List<ParityRegistration>();
                lock (Sync)
                {
                    peerId = _localPeerId;
                    foreach (KeyValuePair<string, Entry> kv in Registry)
            {
                // A verdict-only subscription (RegisterVerdictCallback before Register) has no tuple
                // yet; it must not surface as a null registration row.
                if (kv.Value.Registration != null) regs.Add(kv.Value.Registration);
            }
                }
                return ParityPayloadCodec.EncodeSnapshot(peerId, regs);
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.BuildSnapshotPayload failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Default ceiling for one serialized parity payload, in bytes (MP review M4b). The codec
        /// escapes every non-ASCII character to <c>\uXXXX</c>, so the encoded payload is pure ASCII
        /// and its UTF-8 byte length equals its character length — the cap can be applied to either.
        /// Deliberately conservative: the game's transport (<c>NetworkHelper.BroadcastActionMessage</c>
        /// over Photon) has no documented per-action limit, and the failure mode of guessing too high
        /// is a silently invisible peer.
        /// </summary>
        public const int DefaultMaxPayloadBytes = 8192;

        /// <summary>
        /// Sets the payload ceiling used by every snapshot DevKit emits — including the host's
        /// replies to a peer snapshot and to a late-join <c>FTK2MODS_PARITY_REQUEST_V1</c>, which are
        /// exactly as capable of exceeding the transport as the initial broadcast. Pass 0 or less to
        /// restore <see cref="DefaultMaxPayloadBytes"/>.
        /// </summary>
        public static void SetMaxPayloadBytes(int bytes)
        {
            lock (Sync) { _maxPayloadBytes = bytes <= 0 ? DefaultMaxPayloadBytes : bytes; }
        }

        /// <summary>The current payload ceiling in bytes.</summary>
        public static int GetMaxPayloadBytes()
        {
            lock (Sync) { return _maxPayloadBytes; }
        }

        /// <summary>The local snapshot payload, capped at the configured ceiling. Never throws.</summary>
        public static string BuildCappedSnapshotPayload()
        {
            return BuildSnapshotPayloadCapped(GetMaxPayloadBytes());
        }

        /// <summary>
        /// The local snapshot payload, degrading to the count-only form if the full one exceeds
        /// <paramref name="maxPayloadBytes"/> (pass 0 or less for <see cref="DefaultMaxPayloadBytes"/>).
        ///
        /// <b>Degraded mode is loud, not silent</b> (MP review M4b): exceeding the cap logs an Error
        /// and still sends something, so a peer with 30 mods and 40 packs is reported as
        /// "present but unverifiable" instead of vanishing from the handshake entirely. Never throws.
        /// </summary>
        public static string BuildSnapshotPayloadCapped(int maxPayloadBytes)
        {
            try
            {
                if (maxPayloadBytes <= 0) maxPayloadBytes = DefaultMaxPayloadBytes;
                string full = BuildSnapshotPayload();
                if (full.Length <= maxPayloadBytes) return full;

                string peerId;
                int count;
                lock (Sync)
                {
                    peerId = _localPeerId;
                    count = Registry.Count;
                }
                string degraded = ParityPayloadCodec.EncodeTruncatedSnapshot(peerId, count);
                Log("Error", string.Format(CultureInfo.InvariantCulture,
                    "ParityService: snapshot payload is {0} bytes, over the {1}-byte cap - sending the DEGRADED "
                    + "count-only form for {2} mod(s). Peers will report this peer as PRESENT BUT UNVERIFIABLE: "
                    + "no version/dataHash/feature comparison happens against it this session. Raise "
                    + "[Multiplayer] MaxParityPayloadBytes or reduce the number of registered packs/features.",
                    full.Length.ToString(CultureInfo.InvariantCulture),
                    maxPayloadBytes.ToString(CultureInfo.InvariantCulture),
                    count.ToString(CultureInfo.InvariantCulture)));
                return degraded;
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.BuildSnapshotPayloadCapped failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>A late-joiner's <c>FTK2MODS_PARITY_REQUEST_V1</c> payload. Never throws.</summary>
        public static string BuildRequestPayload()
        {
            try
            {
                return ParityPayloadCodec.EncodeRequest(GetLocalPeerId());
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.BuildRequestPayload failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Feeds one received network payload into the handshake state machine.
        ///
        /// Returns a payload the caller should send back, or <c>null</c> if nothing needs sending:
        ///  - <c>FTK2MODS_PARITY_V1</c> from another peer: store, compare against the local
        ///    registrations, apply the policy, dispatch <c>ParityFailed</c> callbacks. The host
        ///    replies once per peer with its own snapshot so the client can compare symmetrically.
        ///  - <c>FTK2MODS_PARITY_REQUEST_V1</c>: the host replies with a FRESH snapshot (never a
        ///    cached one — SPEC §4), so a late joiner never trusts stale state.
        ///  - anything else (including a payload that isn't ours): <c>null</c>, no state change.
        ///
        /// Never throws — a malformed payload is logged and ignored.
        /// </summary>
        public static string HandleIncomingPayload(string payload, string senderPeerIdFallback)
        {
            try
            {
                if (!ParityPayloadCodec.LooksLikeParityPayload(payload)) return null;
                string action = ParityPayloadCodec.PeekAction(payload);
                if (string.Equals(action, ParityPayloadCodec.ParityRequestActionKey, StringComparison.Ordinal))
                    return HandleRequest(payload, senderPeerIdFallback);
                if (string.Equals(action, ParityPayloadCodec.ParityActionKey, StringComparison.Ordinal))
                    return HandleSnapshot(payload, senderPeerIdFallback);
                return null;
            }
            catch (Exception ex)
            {
                Log("Error", "ParityService.HandleIncomingPayload failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Takes (and clears) the pending banner text for the on-screen warning. Empty when there is
        /// nothing new to show — parity is silent when healthy (SPEC §2).
        /// </summary>
        public static string TakePendingBanner()
        {
            lock (Sync)
            {
                string banner = _pendingBanner;
                _pendingBanner = string.Empty;
                return banner;
            }
        }

        /// <summary>Most recent mismatch summary (sticky, for <c>dk_dump_parity</c>). "" if all peers matched.</summary>
        public static string GetLastMismatchSummary()
        {
            lock (Sync) { return _lastMismatchSummary; }
        }

        /// <summary>
        /// Most recent verdict set as flat callback-arg rows (one <c>string[6]</c> per verdict),
        /// including matches. Simple types only.
        /// </summary>
        public static string[][] GetLastVerdictRows()
        {
            ParityVerdict[] verdicts;
            lock (Sync) { verdicts = _lastVerdicts; }
            string[][] rows = new string[verdicts.Length][];
            for (int i = 0; i < verdicts.Length; i++) rows[i] = verdicts[i].ToCallbackArgs();
            return rows;
        }

        private static string HandleRequest(string payload, string senderPeerIdFallback)
        {
            string requestingPeerId;
            string error;
            if (!ParityPayloadCodec.TryDecodeRequest(payload, out requestingPeerId, out error))
            {
                Log("Warning", "ParityService: ignoring bad parity request - " + error);
                return null;
            }
            string peer = Coalesce(requestingPeerId, senderPeerIdFallback);
            bool isHost;
            lock (Sync)
            {
                isHost = _isHost;
                if (isHost) RepliedPeers.Add(peer);
            }
            if (!isHost)
            {
                Log("Debug", "ParityService: parity request from '" + peer + "' ignored (not host).");
                return null;
            }
            Log("Info", "ParityService: late-join parity request from '" + peer + "' - replying with a fresh snapshot.");
            return BuildCappedSnapshotPayload();
        }

        private static string HandleSnapshot(string payload, string senderPeerIdFallback)
        {
            ParitySnapshot snapshot;
            string error;
            if (!ParityPayloadCodec.TryDecodeSnapshot(payload, out snapshot, out error))
            {
                Log("Warning", "ParityService: ignoring bad parity snapshot - " + error);
                return null;
            }
            string peer = Coalesce(snapshot.SenderPeerId, senderPeerIdFallback);
            ParityRegistration[] localRegs;
            bool isHost;
            bool alreadyReplied;
            lock (Sync)
            {
                if (string.Equals(peer, _localPeerId, StringComparison.Ordinal))
                {
                    // Our own broadcast echoed back to us; nothing to compare.
                    return null;
                }
                isHost = _isHost;
                alreadyReplied = RepliedPeers.Contains(peer);
                if (isHost) RepliedPeers.Add(peer);
                if (!snapshot.IsTruncated)
                {
                    // MP review m13: the peer dictionaries are keyed by the SENDER-SUPPLIED peer id,
                    // so a peer rotating that field would grow them without bound until ResetSession.
                    if (RemoteSnapshots.Count >= MaxTrackedPeers && !RemoteSnapshots.ContainsKey(peer))
                    {
                        Log("Warning", "ParityService: tracking " + MaxTrackedPeers.ToString(CultureInfo.InvariantCulture)
                            + " peers already; ignoring snapshot from '" + peer
                            + "'. A peer rotating its SenderPeerId, or a session larger than expected, can cause this.");
                        return null;
                    }
                    RemoteSnapshots[peer] = snapshot.Registrations;
                }
                localRegs = LocalRegistrationsNoLock();
            }

            if (snapshot.IsTruncated)
            {
                // M4b degraded mode: comparing an empty registration list would report a spurious
                // MissingRemote for every local mod, so we refuse to compare and say so loudly.
                string message = string.Format(CultureInfo.InvariantCulture,
                    "ParityService: peer '{0}' sent a TRUNCATED snapshot ({1} mod(s), payload over its size cap). "
                    + "Parity with that peer CANNOT be verified this session - treat a divergence as possible.",
                    peer, snapshot.TruncatedRegistrationCount.ToString(CultureInfo.InvariantCulture));
                lock (Sync) { _pendingBanner = message; }
                Log("Warning", message);
                if (isHost && !alreadyReplied) return BuildCappedSnapshotPayload();
                return null;
            }

            ParityVerdict[] verdicts = ParityComparer.Compare(localRegs, snapshot.Registrations, peer);
            ParityDecision decision = ParityPolicyEngine.Decide(GetPolicyEnum(), verdicts);

            // MP review m13: a peer re-broadcasting the same snapshot (the host reply, a late-join
            // re-query, a retry) must not re-dispatch ParityFailed to every sibling mod. Callbacks and
            // the banner fire only on a verdict TRANSITION for that peer; the stored state above is
            // still refreshed every time, so applying a snapshot stays idempotent (SPEC §9 point 4).
            string signature = BuildVerdictSignature(verdicts);
            bool verdictChanged;
            List<KeyValuePair<Action<string[]>, string[]>> pending = new List<KeyValuePair<Action<string[]>, string[]>>();
            List<Action<string[]>> verdictSubscribers = new List<Action<string[]>>();
            lock (Sync)
            {
                string previous;
                verdictChanged = !LastVerdictSignatures.TryGetValue(peer, out previous)
                    || !string.Equals(previous, signature, StringComparison.Ordinal);
                LastVerdictSignatures[peer] = signature;

                _lastVerdicts = verdicts;
                _lastMismatchSummary = ParityComparer.Summarize(verdicts);
                if (decision.HasMismatch && verdictChanged)
                {
                    _pendingBanner = decision.BannerText;
                    if (decision.BlockSession) _sessionBlocked = true;
                    if (decision.EngageSafeMode)
                    {
                        for (int i = 0; i < decision.AffectedGuids.Length; i++) SafeModeGuids.Add(decision.AffectedGuids[i]);
                    }
                    for (int i = 0; i < verdicts.Length; i++)
                    {
                        ParityVerdict v = verdicts[i];
                        if (!v.IsMismatch) continue;
                        Entry entry;
                        if (!Registry.TryGetValue(v.Guid, out entry)) continue;   // MissingLocal: nothing to call
                        if (entry.OnParityFailed == null) continue;
                        pending.Add(new KeyValuePair<Action<string[]>, string[]>(entry.OnParityFailed, v.ToCallbackArgs()));
                    }
                }
                // Verdict-arrival subscribers (task #11) hear about EVERY transition, Match included —
                // same m13 dedupe as the failure callbacks, collected in-lock, invoked outside it.
                if (verdictChanged)
                {
                    foreach (KeyValuePair<string, Entry> kv in Registry)
                        if (kv.Value.OnVerdict != null) verdictSubscribers.Add(kv.Value.OnVerdict);
                }
            }

            if (decision.HasMismatch && !verdictChanged)
            {
                Log("Debug", "ParityService: duplicate snapshot from peer '" + peer
                    + "' with an unchanged verdict; state refreshed, callbacks not re-dispatched.");
            }
            else if (decision.HasMismatch)
            {
                Log("Warning", decision.BannerText);
                // Callbacks run OUTSIDE the lock, each isolated: one sibling mod throwing must never
                // stop the others from being notified, nor abort the handshake (fail-safe rule).
                for (int i = 0; i < pending.Count; i++)
                {
                    try
                    {
                        pending[i].Key(pending[i].Value);
                    }
                    catch (Exception ex)
                    {
                        Log("Error", string.Format(CultureInfo.InvariantCulture,
                            "ParityService: mod '{0}' threw from its ParityFailed callback ({1}: {2}) - isolated, continuing.",
                            pending[i].Value[0], ex.GetType().Name, ex.Message));
                    }
                }
            }
            else
            {
                Log("Info", "ParityService: parity OK against peer '" + peer + "' ("
                    + verdicts.Length.ToString(CultureInfo.InvariantCulture) + " mod(s) compared).");
            }

            if (verdictSubscribers.Count > 0)
            {
                string[] verdictArgs = new string[] { peer, decision.HasMismatch ? "Mismatch" : "Match" };
                for (int i = 0; i < verdictSubscribers.Count; i++)
                {
                    try
                    {
                        verdictSubscribers[i](verdictArgs);
                    }
                    catch (Exception ex)
                    {
                        Log("Error", "ParityService: a verdict callback threw (" + ex.GetType().Name + ": "
                            + ex.Message + ") - isolated, continuing.");
                    }
                }
            }

            // Host answers each peer's snapshot exactly once, so the client can run the same
            // comparison locally. No ping-pong: the client never replies to a snapshot.
            if (isHost && !alreadyReplied) return BuildCappedSnapshotPayload();
            return null;
        }

        /// <summary>
        /// Order-independent fingerprint of a verdict set for one peer (m13). Two snapshots that
        /// produce the same guids-and-kinds are "the same news" and must not re-notify sibling mods.
        /// </summary>
        private static string BuildVerdictSignature(ParityVerdict[] verdicts)
        {
            if (verdicts == null || verdicts.Length == 0) return string.Empty;
            List<string> rows = new List<string>(verdicts.Length);
            for (int i = 0; i < verdicts.Length; i++)
            {
                ParityVerdict v = verdicts[i];
                if (v == null) continue;
                rows.Add(v.Guid + "=" + v.Kind + ":" + v.LocalValue + "|" + v.RemoteValue);
            }
            rows.Sort(StringComparer.Ordinal);
            return string.Join("\n", rows.ToArray());
        }

        private static ParityMismatchPolicy GetPolicyEnum()
        {
            lock (Sync) { return _policy; }
        }

        private static ParityRegistration[] LocalRegistrationsNoLock()
        {
            List<ParityRegistration> regs = new List<ParityRegistration>(Registry.Count);
            foreach (KeyValuePair<string, Entry> kv in Registry)
            {
                // A verdict-only subscription (RegisterVerdictCallback before Register) has no tuple
                // yet; it must not surface as a null registration row.
                if (kv.Value.Registration != null) regs.Add(kv.Value.Registration);
            }
            return regs.ToArray();
        }

        private static string Coalesce(string primary, string fallback)
        {
            if (!string.IsNullOrEmpty(primary)) return primary;
            if (!string.IsNullOrEmpty(fallback)) return fallback;
            return "(unknown peer)";
        }

        private static void Log(string level, string message)
        {
            Action<string, string> logger;
            lock (Sync) { logger = _logger; }
            if (logger == null) return;
            try
            {
                logger(level, message);
            }
            catch (Exception)
            {
                // A broken host logger must never take the handshake down.
            }
        }
    }
}
