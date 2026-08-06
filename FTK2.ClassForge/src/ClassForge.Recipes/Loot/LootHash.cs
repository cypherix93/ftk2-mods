using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClassForge.Recipes.Loot
{
    /// <summary>
    /// SHA-256 plumbing shared by every deterministic identity in the loot-grant verb
    /// (docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §3.3/§3.4, §4.2 — GATE A resolution).
    /// <para>Mirrors <c>FTK2.DevKit.DataHasher</c>'s conventions (UTF-8 bytes, lowercase hex, invariant
    /// culture, ordinal sorting) without taking a reference to DevKit — this assembly stays
    /// zero-cross-project-reference by design (SPEC.md "pure C#, no game refs").</para>
    /// </summary>
    internal static class LootHash
    {
        public const string HashPrefix = "sha256:";

        public static byte[] Sha256Bytes(string s)
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
        }

        /// <summary>Lowercase hex of the first <paramref name="byteCount"/> bytes (or all of them when
        /// <paramref name="byteCount"/> is negative or exceeds the array length).</summary>
        public static string ToHex(byte[] bytes, int byteCount)
        {
            int n = (byteCount < 0 || byteCount > bytes.Length) ? bytes.Length : byteCount;
            StringBuilder sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary><c>"sha256:"</c> + all 64 hex digits of the digest of <paramref name="s"/>.</summary>
        public static string Sha256HexPrefixed(string s)
        {
            return HashPrefix + ToHex(Sha256Bytes(s), -1);
        }

        /// <summary>Ordinal-sorted copy — the universal tiebreak/iteration-order rule
        /// (SPEC-DELTA-v1.1 §5.2 invariant 4, restated for the grant stream at verb spec §4.3 item 1).</summary>
        public static List<string> SortedOrdinal(IEnumerable<string> items)
        {
            List<string> list = new List<string>();
            if (items != null)
                foreach (string s in items) list.Add(s ?? string.Empty);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        public static string JoinComma(IEnumerable<string> items)
        {
            return string.Join(",", new List<string>(items ?? new string[0]).ToArray());
        }

        public static string IntInvariant(int v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }
    }
}
