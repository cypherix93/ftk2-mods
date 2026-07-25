using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>
    /// One mod's parity registration tuple: (guid, version, dataHash, enabledFeatures).
    /// See docs/MULTIPLAYER.md R1 and FTK2.DevKit/SPEC.md §3 "ParityService".
    ///
    /// Normalization rules applied at construction (all of them exist so two peers that hold the
    /// *same* logical registration can never produce different bytes on the wire):
    ///  - null strings become "" and every string is trimmed;
    ///  - <see cref="DataHash"/> is lowercased with the invariant culture (our hashes are ASCII hex,
    ///    so this can never depend on the ambient culture);
    ///  - <see cref="EnabledFeatures"/> is trimmed, empty entries dropped, de-duplicated and sorted
    ///    with <see cref="StringComparer.Ordinal"/> — SPEC §8 requires features to compare as a SET,
    ///    so two peers with the same features in a different order must NOT report a mismatch;
    ///  - guid and version are compared ordinally and are NOT case-folded: a guid that differs only
    ///    by case is a genuine divergence (repo convention is lowercase `ftk2mods.&lt;mod&gt;`).
    /// </summary>
    public sealed class ParityRegistration
    {
        private static readonly string[] EmptyFeatures = new string[0];

        private readonly string _guid;
        private readonly string _version;
        private readonly string _dataHash;
        private readonly string _normalizedDataHash;
        private readonly string[] _enabledFeatures;

        public ParityRegistration(string pluginGuid, string version, string dataHash, string[] enabledFeatures)
        {
            _guid = Clean(pluginGuid);
            _version = Clean(version);
            _dataHash = Clean(dataHash).ToLowerInvariant();
            _normalizedDataHash = DataHasher.NormalizeHash(_dataHash);
            _enabledFeatures = NormalizeFeatures(enabledFeatures);
        }

        /// <summary>Plugin GUID, e.g. <c>ftk2mods.warbrain</c>. Compared ordinally.</summary>
        public string Guid { get { return _guid; } }

        /// <summary>Mod version string, e.g. <c>1.0.0</c>. Compared ordinally.</summary>
        public string Version { get { return _version; } }

        /// <summary><c>sha256:&lt;64 lowercase hex&gt;</c> from <see cref="DataHasher"/>.</summary>
        public string DataHash { get { return _dataHash; } }

        /// <summary>Normalized (sorted, de-duplicated) feature name set. Returns a defensive copy.</summary>
        public string[] EnabledFeatures
        {
            get
            {
                if (_enabledFeatures.Length == 0) return EmptyFeatures;
                return (string[])_enabledFeatures.Clone();
            }
        }

        /// <summary>Number of enabled features, without allocating a copy.</summary>
        public int FeatureCount { get { return _enabledFeatures.Length; } }

        /// <summary>
        /// True for a syntactically valid SHA-256 digest in either accepted spelling — bare 64 hex,
        /// or <c>sha256:</c> + 64 hex (MP review B0: sibling hashers emit the bare form).
        /// A null/empty/malformed hash is treated as a GUARANTEED mismatch by
        /// <see cref="ParityComparer"/> (SPEC §8 edge case), never as a silent pass.
        /// </summary>
        public bool HasWellFormedDataHash
        {
            get { return _normalizedDataHash.Length != 0; }
        }

        /// <summary>
        /// The canonical <c>sha256:&lt;64 lowercase hex&gt;</c> form of <see cref="DataHash"/>, or
        /// <see cref="string.Empty"/> if the reported hash was missing/malformed.
        ///
        /// <b>This — not <see cref="DataHash"/> — is what parity comparison compares</b>, so a peer
        /// reporting a bare digest and a peer reporting a prefixed one agree when the underlying
        /// data is identical (MP review B0). <see cref="DataHash"/> keeps the raw reported spelling
        /// so <c>dk_dump_parity</c> shows what the mod actually registered.
        /// </summary>
        public string NormalizedDataHash { get { return _normalizedDataHash; } }

        internal string[] FeaturesNoCopy { get { return _enabledFeatures; } }

        /// <summary>Ordinal set equality over the normalized feature arrays.</summary>
        public bool FeaturesEqual(ParityRegistration other)
        {
            if (other == null) return false;
            string[] a = _enabledFeatures;
            string[] b = other._enabledFeatures;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary><c>[a, b, c]</c>, for human-readable mismatch messages.</summary>
        public string FeaturesToString()
        {
            if (_enabledFeatures.Length == 0) return "[]";
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < _enabledFeatures.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(_enabledFeatures[i]);
            }
            sb.Append(']');
            return sb.ToString();
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} v{1} data={2} features={3}",
                _guid.Length == 0 ? "(no-guid)" : _guid,
                _version.Length == 0 ? "(no-version)" : _version,
                _dataHash.Length == 0 ? "(no-hash)" : _dataHash,
                FeaturesToString());
        }

        private static string Clean(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }

        private static string[] NormalizeFeatures(string[] features)
        {
            if (features == null || features.Length == 0) return EmptyFeatures;
            List<string> cleaned = new List<string>(features.Length);
            for (int i = 0; i < features.Length; i++)
            {
                string f = Clean(features[i]);
                if (f.Length == 0) continue;
                cleaned.Add(f);
            }
            if (cleaned.Count == 0) return EmptyFeatures;
            cleaned.Sort(StringComparer.Ordinal);
            List<string> deduped = new List<string>(cleaned.Count);
            for (int i = 0; i < cleaned.Count; i++)
            {
                if (i > 0 && string.Equals(cleaned[i], cleaned[i - 1], StringComparison.Ordinal)) continue;
                deduped.Add(cleaned[i]);
            }
            return deduped.ToArray();
        }
    }

    /// <summary>
    /// A decoded <c>FTK2MODS_PARITY_V1</c> payload: one peer's complete registration list.
    /// Idempotent to apply — receiving it replaces that peer's last-known set (SPEC §9 point 4).
    /// </summary>
    public sealed class ParitySnapshot
    {
        private static readonly ParityRegistration[] EmptyRegistrations = new ParityRegistration[0];

        private readonly string _senderPeerId;
        private readonly ParityRegistration[] _registrations;
        private readonly int _truncatedRegistrationCount;

        public ParitySnapshot(string senderPeerId, ParityRegistration[] registrations)
            : this(senderPeerId, registrations, -1)
        {
        }

        public ParitySnapshot(string senderPeerId, ParityRegistration[] registrations, int truncatedRegistrationCount)
        {
            _senderPeerId = senderPeerId == null ? string.Empty : senderPeerId.Trim();
            _registrations = registrations == null ? EmptyRegistrations : registrations;
            _truncatedRegistrationCount = truncatedRegistrationCount;
        }

        public string SenderPeerId { get { return _senderPeerId; } }

        /// <summary>Registrations, always sorted by guid (ordinal) by the codec.</summary>
        public ParityRegistration[] Registrations { get { return _registrations; } }

        /// <summary>
        /// True when the sender's payload exceeded its size cap and it sent the degraded
        /// count-only form (MP review M4b). Such a snapshot carries NO registrations and must never
        /// be fed to <see cref="ParityComparer"/> — an empty list would report a spurious
        /// MissingRemote for every local mod. Receivers report it loudly as "unverifiable peer".
        /// </summary>
        public bool IsTruncated { get { return _truncatedRegistrationCount >= 0; } }

        /// <summary>
        /// How many registrations the sender had when it truncated, or -1 for a normal snapshot.
        /// </summary>
        public int TruncatedRegistrationCount { get { return _truncatedRegistrationCount; } }
    }
}
