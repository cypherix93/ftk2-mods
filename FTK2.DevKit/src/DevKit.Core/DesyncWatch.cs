using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>Which of the vendor's two independent desync channels tripped.</summary>
    /// <remarks>
    /// The game runs two separate checks, and they mean different things:
    /// <list type="bullet">
    /// <item><c>Hash</c> — <c>NetworkData.LatestDesyncHashIndex</c>/<c>DebugDesyncHashData</c>. An MD5 over
    /// near-full <c>GameRunData</c> (entities/things included) plus an explicit <c>Stats</c> copy, taken at
    /// save/init/end-turn checkpoints. Divergence here means <b>game state</b> differs — a stat, trait or item
    /// landed on one peer and not the other.</item>
    /// <item><c>GameRandom</c> — <c>LatestGameRandomDesyncIndex</c>/<c>DebugDesyncGameRandomData</c>. Compares
    /// shared-stream draw results per action. Divergence here means somebody <b>took an RNG draw</b> the other
    /// peer didn't, which is a much sharper accusation: some code ran asymmetrically.</item>
    /// </list>
    /// Reporting which one fired is most of the diagnostic value — it separates "we applied different state"
    /// from "we executed different code".
    /// </remarks>
    public enum DesyncChannel
    {
        /// <summary>Detector latched but neither index/dictionary pair carries evidence.</summary>
        Unknown = 0,
        Hash = 1,
        GameRandom = 2,
        Both = 3
    }

    /// <summary>What <see cref="DesyncWatch.Observe"/> concluded about one poll.</summary>
    public enum DesyncVerdict
    {
        /// <summary>Not an online session, or monitoring is off — nothing to watch.</summary>
        Inactive = 0,
        /// <summary>Online and monitoring; no desync latched.</summary>
        Clean = 1,
        /// <summary>Desync observed for the first time this session. Report exactly once.</summary>
        TrippedNow = 2,
        /// <summary>Still desynced, already reported. Stay quiet.</summary>
        AlreadyReported = 3
    }

    /// <summary>
    /// One poll's worth of <c>NetworkData</c>, copied out of the live game object so the decision logic
    /// stays free of Unity and reflection (and therefore testable offline).
    /// </summary>
    public sealed class DesyncSnapshot
    {
        public bool PlayingOnlineMultiplayer;
        public bool DoMonitorForDesyncs;
        public bool HasADesyncBeenDetected;
        public bool IsHost;

        /// <summary>Vendor index of the last state-hash divergence, or negative when unset.</summary>
        public int LatestDesyncHashIndex = -1;

        /// <summary>Vendor index of the last GameRandom divergence, or negative when unset.</summary>
        public int LatestGameRandomDesyncIndex = -1;

        /// <summary>Keys present in <c>NetworkData.DebugDesyncHashData</c>.</summary>
        public IList<int> HashDataIndices = new List<int>();

        /// <summary>Keys present in <c>NetworkData.DebugDesyncGameRandomData</c>.</summary>
        public IList<int> GameRandomDataIndices = new List<int>();
    }

    /// <summary>
    /// Watches the vendor's own desync detector and, when it trips, says <b>why</b> — which channel fired,
    /// and which mod is the most likely cause based on the parity state DevKit already tracks.
    ///
    /// <para>The vendor detector answers "did the two simulations diverge". It cannot answer "which mod pushed
    /// them apart", because it has no idea mods exist. This class joins the two halves: the vendor's verdict on
    /// one side, <see cref="ParityService"/>'s per-mod hashes and SafeMode state on the other.</para>
    ///
    /// <para>Latches on first trip. The vendor flag stays true for the rest of the session once set, so without
    /// a latch every poll would re-report.</para>
    /// </summary>
    public static class DesyncWatch
    {
        private static bool _reported;
        private static DesyncChannel _lastChannel = DesyncChannel.Unknown;

        /// <summary>True once a desync has been observed and reported this session.</summary>
        public static bool HasReported { get { return _reported; } }

        /// <summary>Channel of the latched desync; meaningless before one trips.</summary>
        public static DesyncChannel LastChannel { get { return _lastChannel; } }

        /// <summary>Clears the latch. Call on session end so a later session can report again.</summary>
        public static void Reset()
        {
            _reported = false;
            _lastChannel = DesyncChannel.Unknown;
        }

        /// <summary>
        /// Classifies one poll. Returns <see cref="DesyncVerdict.TrippedNow"/> exactly once per session,
        /// which is the caller's cue to log loudly, banner, and write a report.
        /// </summary>
        public static DesyncVerdict Observe(DesyncSnapshot snapshot)
        {
            if (snapshot == null) return DesyncVerdict.Inactive;
            if (!snapshot.PlayingOnlineMultiplayer || !snapshot.DoMonitorForDesyncs) return DesyncVerdict.Inactive;
            if (!snapshot.HasADesyncBeenDetected) return DesyncVerdict.Clean;
            if (_reported) return DesyncVerdict.AlreadyReported;

            _reported = true;
            _lastChannel = Classify(snapshot);
            return DesyncVerdict.TrippedNow;
        }

        /// <summary>
        /// Decides which channel carries evidence. A channel counts only when its index is non-negative
        /// <b>and</b> its debug dictionary actually holds that index — an index alone can be a leftover
        /// default, and a populated dictionary without a matching index isn't this trip's evidence.
        /// </summary>
        public static DesyncChannel Classify(DesyncSnapshot snapshot)
        {
            if (snapshot == null) return DesyncChannel.Unknown;

            bool hash = snapshot.LatestDesyncHashIndex >= 0
                        && Contains(snapshot.HashDataIndices, snapshot.LatestDesyncHashIndex);
            bool random = snapshot.LatestGameRandomDesyncIndex >= 0
                          && Contains(snapshot.GameRandomDataIndices, snapshot.LatestGameRandomDesyncIndex);

            if (hash && random) return DesyncChannel.Both;
            if (hash) return DesyncChannel.Hash;
            if (random) return DesyncChannel.GameRandom;
            return DesyncChannel.Unknown;
        }

        /// <summary>
        /// The one-line explanation shown on screen and logged first. Deliberately blames a specific
        /// mod when DevKit's parity state supports it, and says so plainly when it doesn't.
        /// </summary>
        public static string BuildCause(DesyncChannel channel, string[] safeModeGuids, bool sessionBlocked)
        {
            bool haveParitySignal = sessionBlocked || (safeModeGuids != null && safeModeGuids.Length > 0);

            if (haveParitySignal)
            {
                string who = (safeModeGuids != null && safeModeGuids.Length > 0)
                    ? string.Join(", ", safeModeGuids)
                    : "a registered mod";
                return "mod data diverged before this desync — " + who
                       + (sessionBlocked ? " (session blocked)" : " (SafeMode)")
                       + ". Compare that mod's files across peers first.";
            }

            switch (channel)
            {
                case DesyncChannel.GameRandom:
                    return "an RNG draw happened on one peer but not the other — code ran asymmetrically. "
                           + "Parity hashes matched, so suspect a shared-stream draw taken behind a condition "
                           + "that differs per peer, not a config difference.";
                case DesyncChannel.Hash:
                    return "game state diverged without any RNG disagreement — a stat, trait or item applied "
                           + "on one peer only. Parity hashes matched, so suspect a grant or status applied "
                           + "under a peer-local condition.";
                case DesyncChannel.Both:
                    return "both state and RNG diverged. The RNG split is the earlier cause; treat the state "
                           + "difference as its consequence.";
                default:
                    return "the vendor detector latched but published no channel evidence. Capture the "
                           + "NetworkDesyncReport folder — this one needs the vendor's own dump to read.";
            }
        }

        /// <summary>
        /// The full report body written to disk and echoed to the log. Plain text on purpose: this gets
        /// pasted into a group chat at midnight, not parsed.
        /// </summary>
        public static string BuildReport(
            DesyncSnapshot snapshot,
            DesyncChannel channel,
            string localPeerId,
            string[] registeredGuids,
            string registrationDump,
            string[] safeModeGuids,
            bool sessionBlocked,
            string lastMismatchSummary,
            string timestampUtc)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== FTK2 DevKit desync report ===");
            sb.AppendLine("time (UTC)  : " + (timestampUtc ?? "(unknown)"));
            sb.AppendLine("peer        : " + (string.IsNullOrEmpty(localPeerId) ? "(unidentified)" : localPeerId)
                          + (snapshot != null && snapshot.IsHost ? "  [HOST]" : "  [CLIENT]"));
            sb.AppendLine("channel     : " + channel);
            sb.AppendLine();

            sb.AppendLine("WHY: " + BuildCause(channel, safeModeGuids, sessionBlocked));
            sb.AppendLine();

            sb.AppendLine("-- vendor detector --");
            if (snapshot != null)
            {
                sb.AppendLine("  HasADesyncBeenDetected     : " + snapshot.HasADesyncBeenDetected);
                sb.AppendLine("  LatestDesyncHashIndex      : " + Num(snapshot.LatestDesyncHashIndex)
                              + "   (entries: " + Count(snapshot.HashDataIndices) + ")");
                sb.AppendLine("  LatestGameRandomDesyncIndex: " + Num(snapshot.LatestGameRandomDesyncIndex)
                              + "   (entries: " + Count(snapshot.GameRandomDataIndices) + ")");
            }
            else
            {
                sb.AppendLine("  (snapshot unavailable)");
            }
            sb.AppendLine();

            sb.AppendLine("-- DevKit parity state --");
            sb.AppendLine("  session blocked : " + sessionBlocked);
            sb.AppendLine("  in SafeMode     : "
                          + (safeModeGuids == null || safeModeGuids.Length == 0 ? "(none)" : string.Join(", ", safeModeGuids)));
            sb.AppendLine("  registered mods : "
                          + (registeredGuids == null || registeredGuids.Length == 0 ? "(none)" : string.Join(", ", registeredGuids)));
            if (!string.IsNullOrEmpty(lastMismatchSummary))
            {
                sb.AppendLine("  last mismatch   : " + lastMismatchSummary);
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(registrationDump))
            {
                sb.AppendLine("-- registrations (compare these lines peer-to-peer) --");
                sb.AppendLine(registrationDump);
                sb.AppendLine();
            }

            sb.AppendLine("-- next step --");
            sb.AppendLine("  Send this file plus the vendor's NetworkDesyncReport folder to whoever is");
            sb.AppendLine("  triaging. Every peer writes its own copy of this report; the interesting part");
            sb.AppendLine("  is where two peers' registration lines differ.");

            return sb.ToString();
        }

        private static bool Contains(IList<int> list, int value)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value) return true;
            }
            return false;
        }

        private static string Num(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Count(IList<int> list)
        {
            return (list == null ? 0 : list.Count).ToString(CultureInfo.InvariantCulture);
        }
    }
}
