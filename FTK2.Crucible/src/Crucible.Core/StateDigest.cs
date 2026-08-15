using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Deterministic hash of a state snapshot, comparable across peers.
    ///
    /// This is the desync oracle's primitive: two peers that agree on game state must produce the
    /// same digest, and two that disagree must not. Three rules make that true —
    ///  - keys are ordinal-sorted by <see cref="MiniJson.Write"/> (reflection has no member order);
    ///  - floats are quantized, so harmless FP jitter does not read as a desync;
    ///  - volatile fields (frame counters, elapsed times, object ids) are redacted by glob.
    ///
    /// Same discipline as DevKit's <c>DataHasher</c>, applied to an in-memory tree instead of files.
    /// </summary>
    public static class StateDigest
    {
        private const int QuantumDecimals = 4;

        public static double Quantize(double value)
        {
            return Math.Round(value, QuantumDecimals, MidpointRounding.AwayFromZero);
        }

        public static Dictionary<string, object> Redact(Dictionary<string, object> snapshot, string[] redactionGlobs)
        {
            if (snapshot == null) return new Dictionary<string, object>();
            return (Dictionary<string, object>)RedactNode(snapshot, string.Empty, redactionGlobs);
        }

        public static string Compute(Dictionary<string, object> snapshot, string[] redactionGlobs)
        {
            Dictionary<string, object> redacted = Redact(snapshot, redactionGlobs);
            string canonical = MiniJson.Write(NormalizeNode(redacted));
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                StringBuilder sb = new StringBuilder("sha256:");
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static object RedactNode(object node, string path, string[] globs)
        {
            Dictionary<string, object> obj = node as Dictionary<string, object>;
            if (obj != null)
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in obj)
                {
                    string childPath = path.Length == 0 ? kv.Key : path + "." + kv.Key;
                    if (IsRedacted(childPath, globs)) continue;
                    result[kv.Key] = RedactNode(kv.Value, childPath, globs);
                }
                return result;
            }

            List<object> arr = node as List<object>;
            if (arr != null)
            {
                List<object> result = new List<object>();
                for (int i = 0; i < arr.Count; i++) result.Add(RedactNode(arr[i], path, globs));
                return result;
            }

            return node;
        }

        private static bool IsRedacted(string path, string[] globs)
        {
            if (globs == null) return false;
            for (int i = 0; i < globs.Length; i++)
            {
                if (string.IsNullOrEmpty(globs[i])) continue;
                if (GlobMatcher.IsMatch(path, globs[i])) return true;
            }
            return false;
        }

        private static object NormalizeNode(object node)
        {
            Dictionary<string, object> obj = node as Dictionary<string, object>;
            if (obj != null)
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in obj) result[kv.Key] = NormalizeNode(kv.Value);
                return result;
            }

            List<object> arr = node as List<object>;
            if (arr != null)
            {
                List<object> result = new List<object>();
                for (int i = 0; i < arr.Count; i++) result.Add(NormalizeNode(arr[i]));
                return result;
            }

            if (node is float || node is double || node is decimal)
                return Quantize(Convert.ToDouble(node, CultureInfo.InvariantCulture));

            return node;
        }
    }
}
