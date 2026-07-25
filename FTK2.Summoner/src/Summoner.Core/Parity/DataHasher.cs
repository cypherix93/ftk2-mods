using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Summoner.Core.Parity
{
    /// <summary>
    /// Pure SHA-256 parity hash (design §A3.4). No filesystem access -- callers (Summoner.Plugin)
    /// supply every enabled pack's file bytes already read from disk. Deterministic construction:
    /// packs are iterated in sorted-by-PackId (ordinal) order; within each pack, files are iterated
    /// in sorted-by-RelPath (ordinal) order after dropping any path under "localization/" (R1:
    /// localization is parity-exempt); every file's bytes are newline-normalized (CRLF -> LF)
    /// before hashing so a Windows-vs-Unix checkout of the same content hashes identically.
    /// The exact byte layout fed to SHA256 is an internal implementation detail (design left this
    /// latitude — it specifies inputs/ordering/exclusions/normalization, not a wire format); the
    /// only external contract is: stable across input ordering, changes iff enabled-pack content
    /// (excluding localization/**) changes.
    /// </summary>
    public static class DataHasher
    {
        public static string ComputeHash(IReadOnlyList<(string PackId, IReadOnlyList<(string RelPath, byte[] Bytes)> Files)> packs)
        {
            using (var sha = SHA256.Create())
            using (var stream = new System.IO.MemoryStream())
            {
                foreach (var pack in (packs ?? Array.Empty<(string, IReadOnlyList<(string, byte[])>)>())
                         .OrderBy(p => p.PackId, StringComparer.Ordinal))
                {
                    WriteToken(stream, "PACK:" + (pack.PackId ?? string.Empty));

                    var files = (pack.Files ?? Array.Empty<(string, byte[])>())
                        .Where(f => !IsLocalization(f.RelPath))
                        .OrderBy(f => f.RelPath, StringComparer.Ordinal);

                    foreach (var file in files)
                    {
                        WriteToken(stream, "FILE:" + file.RelPath);
                        var normalized = NormalizeNewlines(file.Bytes ?? Array.Empty<byte>());
                        stream.Write(normalized, 0, normalized.Length);
                        WriteToken(stream, "/FILE");
                    }
                }

                stream.Position = 0;
                var hash = sha.ComputeHash(stream);
                return ToHex(hash);
            }
        }

        private static bool IsLocalization(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            var normalized = relPath.Replace('\\', '/');
            return normalized.StartsWith("localization/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Byte-level CRLF -> LF normalization -- avoids any text-encoding assumption about the file content.</summary>
        private static byte[] NormalizeNewlines(byte[] bytes)
        {
            if (bytes.Length == 0) return bytes;
            var result = new List<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
                    continue; // drop the CR, keep the following LF
                result.Add(bytes[i]);
            }
            return result.ToArray();
        }

        private static void WriteToken(System.IO.Stream stream, string token)
        {
            var bytes = Encoding.UTF8.GetBytes(token + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
