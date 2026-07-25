using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>One file's contribution to a data hash: its mod-relative path plus its text content.</summary>
    public struct DataFileEntry
    {
        public string RelativePath;
        public string Content;

        public DataFileEntry(string relativePath, string content)
        {
            RelativePath = relativePath;
            Content = content;
        }
    }

    /// <summary>
    /// The single, shared implementation of <c>dataHash</c> (docs/MULTIPLAYER.md R1, SPEC §3).
    /// Centralized here on purpose: a per-mod-invented hashing scheme would itself be a parity risk,
    /// and SPEC §9 point 3 makes this function's determinism load-bearing for every sibling mod's R1
    /// guarantee.
    ///
    /// Rules, verbatim from the contract:
    ///  - <b>SHA-256</b> over the mod's own data files;
    ///  - files enumerated in <b>sorted order</b> — ordinal sort on the path relative to the mod
    ///    folder, so peer filesystem/enumeration order can never affect the hash;
    ///  - each file's bytes read as <b>UTF-8</b> (BOM stripped) with <b>line endings normalized</b>
    ///    (<c>\r\n</c> and lone <c>\r</c> both become <c>\n</c>), so a Windows-vs-Unix checkout of
    ///    the same content hashes identically;
    ///  - all string comparisons <b>invariant-culture</b> (ordinal sorting, invariant-culture glob
    ///    matching, invariant hex formatting);
    ///  - <b>localization files excluded</b> by default via configurable globs
    ///    (<see cref="DefaultExclusionGlobs"/>) — text-only files don't affect gameplay state.
    ///
    /// The canonical byte stream is:
    /// <code>
    /// "FTK2MODS_DATAHASH_V1\n"  then, per included file in sorted order:
    /// &lt;relative/path&gt; "\n" &lt;normalized content&gt; "\n"
    /// </code>
    /// The relative path is part of the stream so renaming a file changes the hash (a rename can
    /// absolutely change which config a mod loads).
    /// </summary>
    public static class DataHasher
    {
        /// <summary>Prefix on every hash string: <c>sha256:</c>.</summary>
        public const string HashPrefix = "sha256:";

        /// <summary>Version tag mixed into the hash stream; bump if the canonical stream changes.</summary>
        public const string StreamVersion = "FTK2MODS_DATAHASH_V1";

        private static readonly string[] DefaultExclusions = new string[]
        {
            "**/Localization/**",
            "**/localization/**",
            "**/*.lang.json",
            "**/*.png",
            "**/*.jpg",
            "**/*.jpeg",
            "**/*.ogg",
            "**/*.wav",
            "**/*.md",
        };

        /// <summary>
        /// Default exclusion globs (localization + presentation assets). Returns a fresh copy so a
        /// caller can hand a mutated variant back in without affecting anyone else. Matching is
        /// case-insensitive/invariant, so the two Localization spellings above are redundant belt
        /// and braces, kept for readability of the default set.
        /// </summary>
        public static string[] GetDefaultExclusionGlobs()
        {
            return (string[])DefaultExclusions.Clone();
        }

        /// <summary>True for <c>sha256:</c> followed by exactly 64 lowercase hex digits.</summary>
        public static bool IsWellFormedHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            if (!hash.StartsWith(HashPrefix, StringComparison.Ordinal)) return false;
            if (hash.Length != HashPrefix.Length + 64) return false;
            for (int i = HashPrefix.Length; i < hash.Length; i++)
            {
                char c = hash[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex) return false;
            }
            return true;
        }

        /// <summary>
        /// Normalizes line endings: <c>\r\n</c> -&gt; <c>\n</c> and lone <c>\r</c> -&gt; <c>\n</c>.
        /// </summary>
        public static string NormalizeContent(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\r')
                {
                    if (i + 1 < raw.Length && raw[i + 1] == '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Core hash routine over in-memory entries — the one every other overload funnels into, and
        /// the one the tests exercise (no disk needed to prove determinism).
        /// </summary>
        public static string ComputeHash(IEnumerable<DataFileEntry> entries, IEnumerable<string> exclusionGlobs)
        {
            List<string> globs = ToList(exclusionGlobs);
            List<DataFileEntry> included = new List<DataFileEntry>();
            if (entries != null)
            {
                foreach (DataFileEntry entry in entries)
                {
                    string rel = GlobMatcher.NormalizePath(entry.RelativePath);
                    if (rel.Length == 0) continue;
                    if (GlobMatcher.IsMatchAny(rel, globs)) continue;
                    included.Add(new DataFileEntry(rel, entry.Content ?? string.Empty));
                }
            }
            included.Sort(CompareEntries);

            using (SHA256 sha = SHA256.Create())
            {
                Feed(sha, StreamVersion + "\n");
                for (int i = 0; i < included.Count; i++)
                {
                    // Skip duplicate relative paths deterministically (same path listed twice).
                    if (i > 0 && string.Equals(included[i].RelativePath, included[i - 1].RelativePath, StringComparison.Ordinal))
                        continue;
                    Feed(sha, included[i].RelativePath);
                    Feed(sha, "\n");
                    Feed(sha, NormalizeContent(included[i].Content));
                    Feed(sha, "\n");
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return HashPrefix + ToHex(sha.Hash);
            }
        }

        /// <summary>
        /// Hashes every file under <paramref name="rootFolder"/> (recursive), excluding
        /// <paramref name="exclusionGlobs"/> (null =&gt; <see cref="GetDefaultExclusionGlobs"/>).
        /// Returns <see cref="string.Empty"/> if the folder does not exist — an absent data folder
        /// must surface as a missing hash (guaranteed mismatch), never as a plausible-looking one.
        /// </summary>
        public static string ComputeFolderHash(string rootFolder, IEnumerable<string> exclusionGlobs)
        {
            if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder)) return string.Empty;
            string[] files = Directory.GetFiles(rootFolder, "*", SearchOption.AllDirectories);
            return ComputeFileListHash(rootFolder, files, exclusionGlobs);
        }

        /// <summary>
        /// Hashes an explicit file list, with paths made relative to <paramref name="rootFolder"/>.
        /// Unreadable files are skipped and do not throw (fail-safe rule, docs/CONVENTIONS.md); a
        /// skipped file simply doesn't contribute, which shows up as a divergence against a peer
        /// that could read it — loud, not silent.
        /// </summary>
        public static string ComputeFileListHash(string rootFolder, IEnumerable<string> filePaths, IEnumerable<string> exclusionGlobs)
        {
            List<string> globs = exclusionGlobs == null ? new List<string>(DefaultExclusions) : ToList(exclusionGlobs);
            List<DataFileEntry> entries = new List<DataFileEntry>();
            if (filePaths != null)
            {
                foreach (string path in filePaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    string rel = MakeRelative(rootFolder, path);
                    if (rel.Length == 0) continue;
                    if (GlobMatcher.IsMatchAny(rel, globs)) continue;
                    string content;
                    if (!TryReadUtf8(path, out content)) continue;
                    entries.Add(new DataFileEntry(rel, content));
                }
            }
            // Exclusions already applied against real relative paths; pass none through again.
            return ComputeHash(entries, null);
        }

        /// <summary>
        /// SPEC §3 compatibility overload:
        /// <c>ComputeDataHash(IEnumerable&lt;string&gt; filePaths, Func&lt;string,bool&gt; excludePredicate = null)</c>.
        /// The spec signature has no explicit root, so the root is inferred as the longest common
        /// directory prefix of the supplied paths. Prefer
        /// <see cref="ComputeFileListHash(string,IEnumerable{string},IEnumerable{string})"/> when the
        /// mod folder is known — inference is exact only when the caller passes a whole data folder.
        /// </summary>
        public static string ComputeHash(IEnumerable<string> filePaths, Func<string, bool> excludePredicate)
        {
            List<string> paths = ToList(filePaths);
            string root = InferCommonRoot(paths);
            List<DataFileEntry> entries = new List<DataFileEntry>();
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path)) continue;
                if (excludePredicate != null && excludePredicate(path)) continue;
                string rel = MakeRelative(root, path);
                if (rel.Length == 0) continue;
                string content;
                if (!TryReadUtf8(path, out content)) continue;
                entries.Add(new DataFileEntry(rel, content));
            }
            return ComputeHash(entries, null);
        }

        /// <summary>Path of <paramref name="fullPath"/> relative to <paramref name="rootFolder"/>, forward-slashed.</summary>
        public static string MakeRelative(string rootFolder, string fullPath)
        {
            string full = GlobMatcher.NormalizePath(fullPath);
            if (string.IsNullOrEmpty(rootFolder)) return full;
            string root = GlobMatcher.NormalizePath(rootFolder);
            if (root.Length == 0) return full;
            if (!root.EndsWith("/", StringComparison.Ordinal)) root += "/";
            // Ordinal-ignore-case: Windows paths are case-insensitive, and IgnoreCase here is a
            // prefix strip only — it never influences the hashed bytes' ordering.
            if (full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length);
            return full;
        }

        private static bool TryReadUtf8(string path, out string content)
        {
            content = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                int offset = 0;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
                content = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string InferCommonRoot(List<string> paths)
        {
            string common = null;
            for (int i = 0; i < paths.Count; i++)
            {
                if (string.IsNullOrEmpty(paths[i])) continue;
                string dir = GlobMatcher.NormalizePath(paths[i]);
                int slash = dir.LastIndexOf('/');
                dir = slash < 0 ? string.Empty : dir.Substring(0, slash);
                if (common == null) { common = dir; continue; }
                common = CommonPrefixDirectory(common, dir);
            }
            return common ?? string.Empty;
        }

        private static string CommonPrefixDirectory(string a, string b)
        {
            string[] sa = a.Split('/');
            string[] sb = b.Split('/');
            int n = Math.Min(sa.Length, sb.Length);
            List<string> shared = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                if (!string.Equals(sa[i], sb[i], StringComparison.OrdinalIgnoreCase)) break;
                shared.Add(sa[i]);
            }
            return string.Join("/", shared.ToArray());
        }

        private static int CompareEntries(DataFileEntry a, DataFileEntry b)
        {
            return string.CompareOrdinal(a.RelativePath, b.RelativePath);
        }

        private static void Feed(SHA256 sha, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length == 0) return;
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        private static string ToHex(byte[] hash)
        {
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static List<string> ToList(IEnumerable<string> source)
        {
            List<string> list = new List<string>();
            if (source == null) return list;
            foreach (string s in source) list.Add(s);
            return list;
        }
    }
}
