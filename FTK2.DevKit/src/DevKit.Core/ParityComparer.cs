using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>Per-mod parity outcome against one remote peer.</summary>
    public enum ParityVerdictKind
    {
        /// <summary>Guid, version, dataHash and the feature set all agree.</summary>
        Match = 0,

        /// <summary>Same mod, different <c>version</c> string.</summary>
        VersionMismatch = 1,

        /// <summary>Same mod+version, different <c>dataHash</c> (or a missing/malformed hash on either side).</summary>
        DataMismatch = 2,

        /// <summary>Same mod+version+data, different set of <c>enabledFeatures</c>.</summary>
        FeaturesMismatch = 3,

        /// <summary>The remote peer has the mod; this peer does not.</summary>
        MissingLocal = 4,

        /// <summary>This peer has the mod; the remote peer does not.</summary>
        MissingRemote = 5,
    }

    /// <summary>
    /// One mod's verdict against one peer, carrying a human-readable message that names the exact
    /// mod and exactly which part diverged (docs/MULTIPLAYER.md R1: "naming the exact mod + which
    /// part diverged (version vs data vs features)").
    /// </summary>
    public sealed class ParityVerdict
    {
        public ParityVerdict(string pluginGuid, ParityVerdictKind kind, string localValue, string remoteValue,
            string remotePeerId, string message)
        {
            Guid = pluginGuid ?? string.Empty;
            Kind = kind;
            LocalValue = localValue ?? string.Empty;
            RemoteValue = remoteValue ?? string.Empty;
            RemotePeerId = remotePeerId ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public string Guid { get; private set; }
        public ParityVerdictKind Kind { get; private set; }

        /// <summary>Local side of the divergence (version / dataHash / feature list), or "" .</summary>
        public string LocalValue { get; private set; }

        /// <summary>Remote side of the divergence, or "".</summary>
        public string RemoteValue { get; private set; }

        public string RemotePeerId { get; private set; }

        /// <summary>Banner/log text naming the mod and the divergence.</summary>
        public string Message { get; private set; }

        public bool IsMismatch { get { return Kind != ParityVerdictKind.Match; } }

        /// <summary>
        /// Flat, BCL-only representation handed to a registered mod's <c>ParityFailed</c> callback
        /// (<c>Action&lt;string[]&gt;</c>), so a sibling mod never needs a compile-time reference to
        /// DevKit to react. Layout is fixed and versioned by position:
        /// <c>[0]=guid [1]=kind [2]=localValue [3]=remoteValue [4]=remotePeerId [5]=message</c>.
        /// </summary>
        public string[] ToCallbackArgs()
        {
            return new string[]
            {
                Guid,
                Kind.ToString(),
                LocalValue,
                RemoteValue,
                RemotePeerId,
                Message,
            };
        }

        public override string ToString()
        {
            return Message;
        }
    }

    /// <summary>
    /// Diffs this peer's registrations against one remote peer's, producing one
    /// <see cref="ParityVerdict"/> per plugin guid seen on either side.
    ///
    /// Divergence precedence when several parts differ at once: Version &gt; Data &gt; Features. The
    /// reported <see cref="ParityVerdict.Kind"/> is the highest-precedence divergence; the message
    /// still enumerates every part that diverged so triage isn't misled.
    /// </summary>
    public static class ParityComparer
    {
        private static readonly ParityVerdict[] EmptyVerdicts = new ParityVerdict[0];

        public static ParityVerdict[] Compare(ParityRegistration[] local, ParityRegistration[] remote, string remotePeerId)
        {
            string peer = string.IsNullOrEmpty(remotePeerId) ? "(unknown peer)" : remotePeerId;
            Dictionary<string, ParityRegistration> localByGuid = Index(local);
            Dictionary<string, ParityRegistration> remoteByGuid = Index(remote);
            if (localByGuid.Count == 0 && remoteByGuid.Count == 0) return EmptyVerdicts;

            List<string> guids = new List<string>();
            foreach (KeyValuePair<string, ParityRegistration> kv in localByGuid) guids.Add(kv.Key);
            foreach (KeyValuePair<string, ParityRegistration> kv in remoteByGuid)
            {
                if (!localByGuid.ContainsKey(kv.Key)) guids.Add(kv.Key);
            }
            guids.Sort(StringComparer.Ordinal);

            List<ParityVerdict> verdicts = new List<ParityVerdict>(guids.Count);
            for (int i = 0; i < guids.Count; i++)
            {
                string guid = guids[i];
                ParityRegistration l, r;
                bool hasLocal = localByGuid.TryGetValue(guid, out l);
                bool hasRemote = remoteByGuid.TryGetValue(guid, out r);
                if (hasLocal && !hasRemote)
                {
                    verdicts.Add(new ParityVerdict(guid, ParityVerdictKind.MissingRemote, Describe(l), "(not installed)", peer,
                        string.Format(CultureInfo.InvariantCulture,
                            "Parity mismatch: mod '{0}' is installed locally ({1}) but is NOT installed on peer '{2}'.",
                            guid, Describe(l), peer)));
                    continue;
                }
                if (!hasLocal && hasRemote)
                {
                    verdicts.Add(new ParityVerdict(guid, ParityVerdictKind.MissingLocal, "(not installed)", Describe(r), peer,
                        string.Format(CultureInfo.InvariantCulture,
                            "Parity mismatch: peer '{0}' has mod '{1}' installed ({2}) but it is NOT installed locally.",
                            peer, guid, Describe(r))));
                    continue;
                }
                verdicts.Add(CompareOne(guid, l, r, peer));
            }
            return verdicts.ToArray();
        }

        public static bool HasMismatch(ParityVerdict[] verdicts)
        {
            if (verdicts == null) return false;
            for (int i = 0; i < verdicts.Length; i++)
            {
                if (verdicts[i] != null && verdicts[i].IsMismatch) return true;
            }
            return false;
        }

        /// <summary>Guids with a mismatching verdict, ordinal-sorted and de-duplicated.</summary>
        public static string[] MismatchedGuids(ParityVerdict[] verdicts)
        {
            List<string> guids = new List<string>();
            if (verdicts != null)
            {
                for (int i = 0; i < verdicts.Length; i++)
                {
                    ParityVerdict v = verdicts[i];
                    if (v == null || !v.IsMismatch) continue;
                    if (!guids.Contains(v.Guid)) guids.Add(v.Guid);
                }
            }
            guids.Sort(StringComparer.Ordinal);
            return guids.ToArray();
        }

        /// <summary>Multi-line summary of every mismatching verdict (empty string if all match).</summary>
        public static string Summarize(ParityVerdict[] verdicts)
        {
            if (verdicts == null || verdicts.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < verdicts.Length; i++)
            {
                ParityVerdict v = verdicts[i];
                if (v == null || !v.IsMismatch) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(v.Message);
            }
            return sb.ToString();
        }

        private static ParityVerdict CompareOne(string guid, ParityRegistration l, ParityRegistration r, string peer)
        {
            bool versionDiffers = !string.Equals(l.Version, r.Version, StringComparison.Ordinal);
            // SPEC §8 edge case: a null/empty/malformed dataHash is a GUARANTEED mismatch, never a
            // silent pass — otherwise a mod that failed to hash its data would look healthy.
            bool hashUnusable = !l.HasWellFormedDataHash || !r.HasWellFormedDataHash;
            bool dataDiffers = hashUnusable || !string.Equals(l.DataHash, r.DataHash, StringComparison.Ordinal);
            bool featuresDiffer = !l.FeaturesEqual(r);

            if (!versionDiffers && !dataDiffers && !featuresDiffer)
            {
                return new ParityVerdict(guid, ParityVerdictKind.Match, Describe(l), Describe(r), peer,
                    string.Format(CultureInfo.InvariantCulture,
                        "Parity OK: mod '{0}' matches peer '{1}' ({2}).", guid, peer, Describe(l)));
            }

            List<string> alsoDiverged = new List<string>();
            if (versionDiffers) alsoDiverged.Add("version");
            if (dataDiffers) alsoDiverged.Add("data");
            if (featuresDiffer) alsoDiverged.Add("features");

            ParityVerdictKind kind;
            string localValue, remoteValue, detail;
            if (versionDiffers)
            {
                kind = ParityVerdictKind.VersionMismatch;
                localValue = l.Version;
                remoteValue = r.Version;
                detail = string.Format(CultureInfo.InvariantCulture,
                    "version diverged - local '{0}' vs peer '{1}' '{2}'",
                    Blank(l.Version), peer, Blank(r.Version));
            }
            else if (dataDiffers)
            {
                kind = ParityVerdictKind.DataMismatch;
                localValue = l.DataHash;
                remoteValue = r.DataHash;
                detail = string.Format(CultureInfo.InvariantCulture,
                    "data diverged - local dataHash '{0}' vs peer '{1}' '{2}'{3}",
                    Blank(l.DataHash), peer, Blank(r.DataHash),
                    hashUnusable ? " (a dataHash is missing or malformed - treated as a guaranteed mismatch)" : string.Empty);
            }
            else
            {
                kind = ParityVerdictKind.FeaturesMismatch;
                localValue = l.FeaturesToString();
                remoteValue = r.FeaturesToString();
                detail = string.Format(CultureInfo.InvariantCulture,
                    "enabled features diverged - local {0} vs peer '{1}' {2}",
                    l.FeaturesToString(), peer, r.FeaturesToString());
            }

            string also = alsoDiverged.Count > 1
                ? " [all diverging parts: " + string.Join(", ", alsoDiverged.ToArray()) + "]"
                : string.Empty;

            string message = string.Format(CultureInfo.InvariantCulture,
                "Parity mismatch: mod '{0}' {1}.{2}", guid, detail, also);
            return new ParityVerdict(guid, kind, localValue, remoteValue, peer, message);
        }

        private static string Blank(string value)
        {
            return string.IsNullOrEmpty(value) ? "(none)" : value;
        }

        private static string Describe(ParityRegistration r)
        {
            return string.Format(CultureInfo.InvariantCulture, "v{0}, data {1}, features {2}",
                Blank(r.Version), Blank(r.DataHash), r.FeaturesToString());
        }

        private static Dictionary<string, ParityRegistration> Index(ParityRegistration[] registrations)
        {
            Dictionary<string, ParityRegistration> map = new Dictionary<string, ParityRegistration>(StringComparer.Ordinal);
            if (registrations == null) return map;
            for (int i = 0; i < registrations.Length; i++)
            {
                ParityRegistration r = registrations[i];
                if (r == null || r.Guid.Length == 0) continue;
                map[r.Guid] = r;
            }
            return map;
        }
    }
}
