using System;
using System.Collections.Generic;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>
    /// Canonical codec for the two parity wire payloads carried over
    /// <c>AdventureDirector._handleNetworkAction</c> (docs/MULTIPLAYER.md R1, SPEC §4).
    ///
    /// Wire format is the exact JSON shape documented in SPEC §4, emitted compactly:
    /// <code>
    /// {"Action":"FTK2MODS_PARITY_V1","SenderPeerId":"host","Registrations":[
    ///   {"Guid":"ftk2mods.warbrain","Version":"1.0.0","DataHash":"sha256:...","EnabledFeatures":["A","B"]}]}
    /// {"Action":"FTK2MODS_PARITY_REQUEST_V1","RequestingPeerId":"client-2"}
    /// </code>
    ///
    /// Determinism (docs/MULTIPLAYER.md R2 applied to our own transport):
    ///  - registrations are sorted by guid with <see cref="StringComparer.Ordinal"/> before writing;
    ///  - each registration's features are already sorted/de-duplicated by
    ///    <see cref="ParityRegistration"/>;
    ///  - key order is fixed, no whitespace, no culture-sensitive formatting anywhere, and every
    ///    non-ASCII character is <c>\uXXXX</c>-escaped. Two peers holding the same registrations
    ///    therefore produce byte-identical payloads under any OS locale.
    ///
    /// Versioning: the action key carries the <c>_V1</c> suffix. A breaking payload change ships as
    /// <c>_V2</c>; <see cref="PeekAction"/> lets a receiver route by key without guessing.
    /// </summary>
    public static class ParityPayloadCodec
    {
        /// <summary>Full-snapshot action key (host&lt;-&gt;client).</summary>
        public const string ParityActionKey = "FTK2MODS_PARITY_V1";

        /// <summary>Late-join re-query action key (client-&gt;host).</summary>
        public const string ParityRequestActionKey = "FTK2MODS_PARITY_REQUEST_V1";

        private const string KeyAction = "Action";
        private const string KeySenderPeerId = "SenderPeerId";
        private const string KeyRequestingPeerId = "RequestingPeerId";
        private const string KeyRegistrations = "Registrations";
        private const string KeyGuid = "Guid";
        private const string KeyVersion = "Version";
        private const string KeyDataHash = "DataHash";
        private const string KeyEnabledFeatures = "EnabledFeatures";
        private const string KeyTruncated = "Truncated";

        private static readonly ParityRegistration[] EmptyRegistrations = new ParityRegistration[0];

        /// <summary>Encodes a full <c>FTK2MODS_PARITY_V1</c> snapshot. Never returns null.</summary>
        public static string EncodeSnapshot(string senderPeerId, IEnumerable<ParityRegistration> registrations)
        {
            List<ParityRegistration> ordered = new List<ParityRegistration>();
            if (registrations != null)
            {
                foreach (ParityRegistration r in registrations)
                {
                    if (r == null) continue;
                    ordered.Add(r);
                }
            }
            ordered.Sort(CompareByGuid);

            StringBuilder sb = new StringBuilder(128 + ordered.Count * 160);
            sb.Append('{');
            WriteMember(sb, KeyAction, ParityActionKey);
            sb.Append(',');
            WriteMember(sb, KeySenderPeerId, senderPeerId ?? string.Empty);
            sb.Append(',');
            MiniJson.WriteString(sb, KeyRegistrations);
            sb.Append(":[");
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteRegistration(sb, ordered[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Encodes the <b>degraded</b> form of a <c>FTK2MODS_PARITY_V1</c> snapshot: the registration
        /// list is dropped and only its count is carried, in the <c>Truncated</c> member
        /// (MP review M4b — the payload has no size limit, and a peer with many mods/packs could
        /// exceed whatever the game's transport accepts, latching the sender off for the process and
        /// leaving the session looking healthy because nothing ever arrives to compare).
        ///
        /// A truncated snapshot deliberately does NOT compare as anything: a receiver must treat it
        /// as "this peer could not be verified" and say so loudly, because comparing an empty
        /// registration list would report a spurious MissingRemote for every local mod. The member is
        /// additive, so a <c>_V1</c> decoder that predates it simply ignores it and sees zero
        /// registrations — hence <see cref="ParitySnapshot.IsTruncated"/> is checked before compare.
        /// </summary>
        public static string EncodeTruncatedSnapshot(string senderPeerId, int registrationCount)
        {
            if (registrationCount < 0) registrationCount = 0;
            StringBuilder sb = new StringBuilder(128);
            sb.Append('{');
            WriteMember(sb, KeyAction, ParityActionKey);
            sb.Append(',');
            WriteMember(sb, KeySenderPeerId, senderPeerId ?? string.Empty);
            sb.Append(',');
            MiniJson.WriteString(sb, KeyRegistrations);
            sb.Append(":[],");
            WriteMember(sb, KeyTruncated,
                registrationCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Encodes a <c>FTK2MODS_PARITY_REQUEST_V1</c> late-join re-query.</summary>
        public static string EncodeRequest(string requestingPeerId)
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append('{');
            WriteMember(sb, KeyAction, ParityRequestActionKey);
            sb.Append(',');
            WriteMember(sb, KeyRequestingPeerId, requestingPeerId ?? string.Empty);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Cheap ordinal pre-filter for the observe-only <c>_handleNetworkAction</c> hook: true if
        /// the string could plausibly be one of ours. Avoids parsing every network action the game
        /// (or another mod) sends.
        /// </summary>
        public static bool LooksLikeParityPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return false;
            return payload.IndexOf(ParityActionKey, StringComparison.Ordinal) >= 0
                || payload.IndexOf(ParityRequestActionKey, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Returns the payload's <c>Action</c> value, or null if it is not parseable JSON or has no
        /// Action member. Never throws.
        /// </summary>
        public static string PeekAction(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return null;
            object parsed;
            string error;
            if (!MiniJson.TryParse(payload, out parsed, out error)) return null;
            Dictionary<string, object> map = MiniJson.AsObject(parsed);
            if (map == null) return null;
            object action;
            if (!map.TryGetValue(KeyAction, out action)) return null;
            return MiniJson.AsString(action);
        }

        /// <summary>Decodes a <c>FTK2MODS_PARITY_V1</c> snapshot. Never throws.</summary>
        public static bool TryDecodeSnapshot(string payload, out ParitySnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;
            object parsed;
            if (!MiniJson.TryParse(payload, out parsed, out error))
            {
                error = "malformed parity payload: " + (error ?? "unknown error");
                return false;
            }
            Dictionary<string, object> map = MiniJson.AsObject(parsed);
            if (map == null)
            {
                error = "parity payload root is not a JSON object";
                return false;
            }
            object actionObj;
            string action = map.TryGetValue(KeyAction, out actionObj) ? MiniJson.AsString(actionObj) : null;
            if (!string.Equals(action, ParityActionKey, StringComparison.Ordinal))
            {
                error = "payload Action is '" + (action ?? "(missing)") + "', expected " + ParityActionKey;
                return false;
            }

            object senderObj;
            string sender = map.TryGetValue(KeySenderPeerId, out senderObj) ? MiniJson.AsString(senderObj) : null;

            // Degraded-mode marker (M4b). Absent on a normal snapshot; when present the sender's
            // payload exceeded its size cap and only the registration COUNT survived.
            int truncatedCount = -1;
            object truncatedObj;
            if (map.TryGetValue(KeyTruncated, out truncatedObj))
            {
                string raw = MiniJson.AsString(truncatedObj);
                int parsedCount;
                if (raw != null && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out parsedCount) && parsedCount >= 0)
                {
                    truncatedCount = parsedCount;
                }
                else
                {
                    // Marker present but unreadable: still treat the snapshot as unverifiable rather
                    // than comparing an empty list and reporting every local mod as MissingRemote.
                    truncatedCount = 0;
                }
            }

            object registrationsObj;
            if (!map.TryGetValue(KeyRegistrations, out registrationsObj))
            {
                snapshot = new ParitySnapshot(sender, EmptyRegistrations, truncatedCount);
                return true;
            }
            List<object> rawList = MiniJson.AsArray(registrationsObj);
            if (rawList == null)
            {
                error = "Registrations is not a JSON array";
                return false;
            }

            List<ParityRegistration> result = new List<ParityRegistration>(rawList.Count);
            for (int i = 0; i < rawList.Count; i++)
            {
                Dictionary<string, object> entry = MiniJson.AsObject(rawList[i]);
                if (entry == null)
                {
                    error = "Registrations[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "] is not a JSON object";
                    return false;
                }
                string guid = ReadString(entry, KeyGuid);
                string version = ReadString(entry, KeyVersion);
                string dataHash = ReadString(entry, KeyDataHash);
                string[] features = ReadStringArray(entry, KeyEnabledFeatures);
                result.Add(new ParityRegistration(guid, version, dataHash, features));
            }
            result.Sort(CompareByGuid);
            snapshot = new ParitySnapshot(sender, result.ToArray(), truncatedCount);
            return true;
        }

        /// <summary>Decodes a <c>FTK2MODS_PARITY_REQUEST_V1</c> re-query. Never throws.</summary>
        public static bool TryDecodeRequest(string payload, out string requestingPeerId, out string error)
        {
            requestingPeerId = null;
            error = null;
            object parsed;
            if (!MiniJson.TryParse(payload, out parsed, out error))
            {
                error = "malformed parity request payload: " + (error ?? "unknown error");
                return false;
            }
            Dictionary<string, object> map = MiniJson.AsObject(parsed);
            if (map == null)
            {
                error = "parity request payload root is not a JSON object";
                return false;
            }
            object actionObj;
            string action = map.TryGetValue(KeyAction, out actionObj) ? MiniJson.AsString(actionObj) : null;
            if (!string.Equals(action, ParityRequestActionKey, StringComparison.Ordinal))
            {
                error = "payload Action is '" + (action ?? "(missing)") + "', expected " + ParityRequestActionKey;
                return false;
            }
            requestingPeerId = ReadString(map, KeyRequestingPeerId);
            return true;
        }

        private static void WriteRegistration(StringBuilder sb, ParityRegistration r)
        {
            sb.Append('{');
            WriteMember(sb, KeyGuid, r.Guid);
            sb.Append(',');
            WriteMember(sb, KeyVersion, r.Version);
            sb.Append(',');
            WriteMember(sb, KeyDataHash, r.DataHash);
            sb.Append(',');
            MiniJson.WriteString(sb, KeyEnabledFeatures);
            sb.Append(":[");
            string[] features = r.FeaturesNoCopy;
            for (int i = 0; i < features.Length; i++)
            {
                if (i > 0) sb.Append(',');
                MiniJson.WriteString(sb, features[i]);
            }
            sb.Append("]}");
        }

        private static void WriteMember(StringBuilder sb, string key, string value)
        {
            MiniJson.WriteString(sb, key);
            sb.Append(':');
            MiniJson.WriteString(sb, value);
        }

        private static string ReadString(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return string.Empty;
            string s = MiniJson.AsString(value);
            return s ?? string.Empty;
        }

        private static string[] ReadStringArray(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value)) return null;
            List<object> list = MiniJson.AsArray(value);
            if (list == null) return null;
            List<string> result = new List<string>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                string s = MiniJson.AsString(list[i]);
                if (s != null) result.Add(s);
            }
            return result.ToArray();
        }

        private static int CompareByGuid(ParityRegistration a, ParityRegistration b)
        {
            int byGuid = string.CompareOrdinal(a.Guid, b.Guid);
            if (byGuid != 0) return byGuid;
            int byVersion = string.CompareOrdinal(a.Version, b.Version);
            if (byVersion != 0) return byVersion;
            return string.CompareOrdinal(a.DataHash, b.DataHash);
        }
    }
}
