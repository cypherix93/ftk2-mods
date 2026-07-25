using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassForge.Core.IO;

namespace ClassForge.Core
{
    /// <summary>
    /// Computes the ParityService <c>dataHash</c> (SPEC.md §3, §9.6): SHA-256 over every file under each
    /// *enabled* pack's tree, excluding <c>localization/**</c> and <c>provenance.json</c>, computed over
    /// sorted pack ids then sorted file paths within each pack, so the result is identical byte-for-byte on
    /// every peer regardless of git checkout line-ending settings (MULTIPLAYER.md R1/R2).
    ///
    /// <para><b>MP review B0/M5 fixes applied here:</b></para>
    /// <list type="bullet">
    /// <item><b>B0 — <c>sha256:</c> prefix.</b> DevKit's <c>ParityComparer</c> treats a hash with no prefix (or
    /// the wrong length) as <i>unusable</i> and forces a hard mismatch verdict even between two byte-identical
    /// packs (<c>ParityRegistration.HasWellFormedDataHash</c>). The digest is now emitted as
    /// <c><see cref="HashPrefix"/> + 64 hex chars</c> to match DevKit's own canonical shape.</item>
    /// <item><b>M5 — hash real bytes, not a lossy text decode.</b> The previous implementation ran every file
    /// (including every PNG icon/portrait) through <c>File.ReadAllText(path, Encoding.UTF8)</c>, which is a
    /// lossy U+FFFD decode of binary data and is not even guaranteed peer-stable (a different CLR/Mono build
    /// can decode invalid UTF-8 differently). Every file is now read as raw bytes
    /// (<see cref="IFileSource.ReadAllBytes"/>). CRLF/LF line-ending normalization — needed so a git checkout
    /// with <c>core.autocrlf</c> enabled cannot flip the hash on Windows vs. Linux — is applied ONLY to a
    /// known-text extension set (<see cref="NormalizedTextExtensions"/>: <c>.json</c>, <c>.md</c>, <c>.txt</c>),
    /// operating directly on the byte stream (CR/LF are single ASCII bytes and never appear inside a UTF-8
    /// multi-byte continuation sequence, so this is safe for UTF-8 text without decoding it). Binary assets
    /// (PNGs etc.) are hashed byte-for-byte, verbatim — a PNG's own header legitimately contains the byte
    /// sequence 0x0D 0x0A and must never be "normalized".</item>
    /// <item><b>provenance.json is excluded.</b> It is import/regeneration bookkeeping (source package
    /// version, timestamps) — metadata ABOUT the pack, not gameplay data the pack contributes to <c>Configs</c>
    /// or the recipe book. Two peers can have byte-identical gameplay content with different provenance
    /// records (e.g. one re-ran the converter against a newer EOR build with no content change), and forcing
    /// a parity mismatch over that would make ClassForge's Block policy fire for a non-event.</item>
    /// </list>
    /// </summary>
    public static class DataHasher
    {
        /// <summary>DevKit's canonical hash-string prefix (<c>DevKit.Core.DataHasher.HashPrefix</c>) —
        /// duplicated here rather than referenced, since Core has zero dependency on any sibling mod's
        /// assembly by design; the string value is the actual parity contract (SPEC.md §9.6), not the type.</summary>
        public const string HashPrefix = "sha256:";

        /// <summary>Extensions normalized for line endings before hashing. Anything else (icons, portraits,
        /// any future binary asset type) is hashed as opaque bytes with zero transformation.</summary>
        private static readonly HashSet<string> NormalizedTextExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json", ".md", ".txt" };

        private const string ProvenanceFileName = "provenance.json";

        public static string ComputeHash(IFileSource fs, IEnumerable<PackForHash> enabledPacks)
        {
            using (var buffer = new MemoryStream())
            {
                foreach (var pack in enabledPacks.OrderBy(p => p.PackId, StringComparer.Ordinal))
                {
                    var rootDir = NormalizeSeparators(pack.RootDir);
                    var files = fs.GetFiles(pack.RootDir, "*", true)
                        .Select(f => new { Full = f, Rel = RelativePath(rootDir, NormalizeSeparators(f)) })
                        .Where(f => !IsExcluded(f.Rel))
                        .OrderBy(f => f.Rel, StringComparer.Ordinal);

                    foreach (var file in files)
                    {
                        WriteUtf8(buffer, pack.PackId);
                        buffer.WriteByte((byte)'|');
                        WriteUtf8(buffer, file.Rel);
                        buffer.WriteByte((byte)'|');

                        var raw = fs.ReadAllBytes(file.Full);
                        var content = IsNormalizedTextExtension(file.Rel) ? NormalizeLineEndings(raw) : raw;
                        buffer.Write(content, 0, content.Length);

                        buffer.WriteByte((byte)'\n');
                    }
                }

                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(buffer.ToArray());
                    var hex = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return HashPrefix + hex;
                }
            }
        }

        private static bool IsExcluded(string relativePath)
            => IsUnderLocalization(relativePath) || IsProvenanceFile(relativePath);

        private static bool IsUnderLocalization(string relativePath)
            => relativePath.StartsWith("localization/", StringComparison.OrdinalIgnoreCase);

        /// <summary>Root-level <c>provenance.json</c> only — that is the only place a pack ever has one
        /// (SPEC.md §4), so this deliberately does not match a same-named file nested under pack content.</summary>
        private static bool IsProvenanceFile(string relativePath)
            => string.Equals(relativePath, ProvenanceFileName, StringComparison.OrdinalIgnoreCase);

        private static bool IsNormalizedTextExtension(string relativePath)
            => NormalizedTextExtensions.Contains(Path.GetExtension(relativePath));

        /// <summary>Byte-level CRLF/lone-CR -&gt; LF normalization, equivalent to the old
        /// <c>text.Replace("\r\n","\n").Replace("\r","\n")</c> but operating on raw bytes so it never has to
        /// decode the file as text.</summary>
        private static byte[] NormalizeLineEndings(byte[] input)
        {
            bool hasCr = false;
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == (byte)'\r') { hasCr = true; break; }
            }
            if (!hasCr) return input;

            var output = new byte[input.Length];
            int o = 0;
            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i];
                if (b == (byte)'\r')
                {
                    output[o++] = (byte)'\n';
                    if (i + 1 < input.Length && input[i + 1] == (byte)'\n') i++; // CRLF -> single LF
                }
                else
                {
                    output[o++] = b;
                }
            }

            if (o == output.Length) return output;
            var trimmed = new byte[o];
            Array.Copy(output, trimmed, o);
            return trimmed;
        }

        private static void WriteUtf8(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            stream.Write(bytes, 0, bytes.Length);
        }

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
