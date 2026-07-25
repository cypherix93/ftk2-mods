using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassForge.Core.IO;

namespace ClassForge.Core
{
    /// <summary>
    /// Computes the ParityService <c>dataHash</c> (SPEC.md §3, §9.6): SHA-256 over every file under each
    /// *enabled* pack's tree, excluding <c>localization/**</c>, computed over sorted pack ids then sorted
    /// file paths within each pack, with normalized line endings, so the result is identical byte-for-byte
    /// on every peer regardless of git checkout line-ending settings (MULTIPLAYER.md R1/R2).
    /// </summary>
    public static class DataHasher
    {
        public static string ComputeHash(IFileSource fs, IEnumerable<PackForHash> enabledPacks)
        {
            var sb = new StringBuilder();

            foreach (var pack in enabledPacks.OrderBy(p => p.PackId, StringComparer.Ordinal))
            {
                var rootDir = NormalizeSeparators(pack.RootDir);
                var files = fs.GetFiles(pack.RootDir, "*", true)
                    .Select(f => new { Full = f, Rel = RelativePath(rootDir, NormalizeSeparators(f)) })
                    .Where(f => !IsUnderLocalization(f.Rel))
                    .OrderBy(f => f.Rel, StringComparer.Ordinal);

                foreach (var file in files)
                {
                    var content = fs.ReadAllText(file.Full);
                    var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
                    sb.Append(pack.PackId).Append('|').Append(file.Rel).Append('|').Append(normalized).Append('\n');
                }
            }

            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                var hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private static bool IsUnderLocalization(string relativePath)
            => relativePath.StartsWith("localization/", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeSeparators(string path) => (path ?? string.Empty).Replace('\\', '/');

        private static string RelativePath(string rootDir, string fullPath)
        {
            var root = rootDir.TrimEnd('/');
            var rel = fullPath;
            if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(root.Length);
            return rel.TrimStart('/');
        }
    }
}
