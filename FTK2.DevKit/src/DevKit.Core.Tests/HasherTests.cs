using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FTK2Mods.DevKit.Tests
{
    internal static class HasherTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("DataHasher (SHA-256, sorted paths, normalized line endings)");

            TestHarness.Run("emitted hash format is sha256: + 64 lowercase hex", delegate
            {
                string hash = DataHasher.ComputeHash(Entries("a.json", "{}"), null);
                TestHarness.True(hash.StartsWith("sha256:", StringComparison.Ordinal), "prefix");
                TestHarness.Equal(7 + 64, hash.Length, "length");
                TestHarness.True(DataHasher.IsWellFormedHash(hash), "IsWellFormedHash");
                TestHarness.Equal(hash, DataHasher.NormalizeHash(hash), "an emitted hash is already canonical");
            });

            // ---- MP review B0: acceptance must tolerate BOTH spellings ----------------------
            // The sibling hashers (ClassForge, Summoner) return bare 64-hex with no prefix. When
            // IsWellFormedHash required the prefix, ParityComparer forced hashUnusable=true and every
            // pair of byte-identical peers reported DataMismatch. These tests use REAL hasher output
            // rather than a hardcoded constant, which is precisely why the old suite stayed green
            // while the integration was broken.

            TestHarness.Run("B0: a real hash is well-formed in bare AND prefixed spelling", delegate
            {
                string prefixed = DataHasher.ComputeHash(Entries("a.json", "{\"k\":1}"), null);
                string bare = StripPrefix(prefixed);
                TestHarness.Equal(64, bare.Length, "bare form is 64 hex digits");

                TestHarness.True(DataHasher.IsWellFormedHash(prefixed), "prefixed form must be accepted");
                TestHarness.True(DataHasher.IsWellFormedHash(bare), "bare sibling-style form must be accepted");
                TestHarness.Equal(prefixed, DataHasher.NormalizeHash(bare),
                    "bare and prefixed forms of the same digest must normalize identically");
            });

            TestHarness.Run("B0: normalization is case- and whitespace-insensitive", delegate
            {
                string prefixed = DataHasher.ComputeHash(Entries("a.json", "{\"k\":2}"), null);
                string bare = StripPrefix(prefixed);
                TestHarness.Equal(prefixed, DataHasher.NormalizeHash(bare.ToUpperInvariant()),
                    "uppercase bare hex must normalize to the canonical lowercase form");
                TestHarness.Equal(prefixed, DataHasher.NormalizeHash("  " + prefixed.ToUpperInvariant() + "  "),
                    "surrounding whitespace and uppercase must normalize away");
            });

            TestHarness.Run("B0: genuinely malformed hashes are still rejected in both spellings", delegate
            {
                string[] bad = new string[]
                {
                    "", null, "sha256:", "sha256:xyz", "xyz",
                    "sha256:" + new string('a', 63),        // one hex digit short
                    "sha256:" + new string('a', 65),        // one too many
                    new string('a', 63),                    // bare, one short
                    new string('g', 64),                    // bare, non-hex
                    "sha256:" + new string('g', 64),        // prefixed, non-hex
                    "md5:" + new string('a', 64),           // wrong algorithm prefix
                };
                for (int i = 0; i < bad.Length; i++)
                {
                    TestHarness.False(DataHasher.IsWellFormedHash(bad[i]),
                        "must reject '" + (bad[i] ?? "(null)") + "'");
                    TestHarness.Equal("", DataHasher.NormalizeHash(bad[i]),
                        "malformed input must normalize to empty, never to a plausible-looking hash");
                }
            });

            TestHarness.Run("B0: a registration carries the raw spelling but normalizes for compare", delegate
            {
                string prefixed = DataHasher.ComputeHash(Entries("a.json", "{\"k\":3}"), null);
                string bare = StripPrefix(prefixed);

                ParityRegistration sibling = new ParityRegistration("ftk2mods.classforge", "1.0.0", bare, null);
                TestHarness.Equal(bare, sibling.DataHash, "DataHash keeps what the mod actually reported");
                TestHarness.Equal(prefixed, sibling.NormalizedDataHash, "NormalizedDataHash is canonical");
                TestHarness.True(sibling.HasWellFormedDataHash, "a bare sibling hash must be usable");
            });

            TestHarness.Run("file enumeration order does not affect the hash", delegate
            {
                List<DataFileEntry> ordered = new List<DataFileEntry>
                {
                    new DataFileEntry("a/one.json", "1"),
                    new DataFileEntry("b/two.json", "2"),
                    new DataFileEntry("c/three.json", "3"),
                };
                List<DataFileEntry> shuffled = new List<DataFileEntry>
                {
                    new DataFileEntry("c/three.json", "3"),
                    new DataFileEntry("a/one.json", "1"),
                    new DataFileEntry("b/two.json", "2"),
                };
                TestHarness.Equal(DataHasher.ComputeHash(ordered, null), DataHasher.ComputeHash(shuffled, null),
                    "sorted-order rule violated");
            });

            TestHarness.Run("CRLF, LF and lone CR content hash identically", delegate
            {
                string lf = DataHasher.ComputeHash(Entries("x.json", "{\n  \"a\": 1\n}\n"), null);
                string crlf = DataHasher.ComputeHash(Entries("x.json", "{\r\n  \"a\": 1\r\n}\r\n"), null);
                string cr = DataHasher.ComputeHash(Entries("x.json", "{\r  \"a\": 1\r}\r"), null);
                TestHarness.Equal(lf, crlf, "CRLF must normalize to LF");
                TestHarness.Equal(lf, cr, "lone CR must normalize to LF");
            });

            TestHarness.Run("content change changes the hash", delegate
            {
                TestHarness.NotEqual(DataHasher.ComputeHash(Entries("x.json", "{\"a\":1}"), null),
                    DataHasher.ComputeHash(Entries("x.json", "{\"a\":2}"), null), "content change must change the hash");
            });

            TestHarness.Run("file rename changes the hash (path is part of the stream)", delegate
            {
                TestHarness.NotEqual(DataHasher.ComputeHash(Entries("x.json", "same"), null),
                    DataHasher.ComputeHash(Entries("y.json", "same"), null), "rename must change the hash");
            });

            TestHarness.Run("path separators and ./ prefixes normalize away", delegate
            {
                TestHarness.Equal(
                    DataHasher.ComputeHash(Entries("sub/x.json", "c"), null),
                    DataHasher.ComputeHash(Entries(".\\sub\\x.json", "c"), null),
                    "backslash + ./ prefix must normalize to the same relative path");
            });

            TestHarness.Run("default exclusions drop localization and image files", delegate
            {
                string[] globs = DataHasher.GetDefaultExclusionGlobs();
                string withoutLoc = DataHasher.ComputeHash(new List<DataFileEntry>
                {
                    new DataFileEntry("Profiles/brute.brain.json", "{}"),
                }, globs);
                string withLoc = DataHasher.ComputeHash(new List<DataFileEntry>
                {
                    new DataFileEntry("Profiles/brute.brain.json", "{}"),
                    new DataFileEntry("Localization/en.json", "{\"HELLO\":\"hi\"}"),
                    new DataFileEntry("art/icon.png", "binary-ish"),
                    new DataFileEntry("README.md", "docs"),
                }, globs);
                TestHarness.Equal(withoutLoc, withLoc, "localization/png/md files must be excluded from the hash");
            });

            TestHarness.Run("a custom exclusion glob set is honoured", delegate
            {
                string[] globs = new string[] { "**/scratch/**" };
                string baseline = DataHasher.ComputeHash(Entries("data/x.json", "1"), globs);
                string withScratch = DataHasher.ComputeHash(new List<DataFileEntry>
                {
                    new DataFileEntry("data/x.json", "1"),
                    new DataFileEntry("data/scratch/tmp.json", "junk"),
                }, globs);
                TestHarness.Equal(baseline, withScratch, "custom exclusion glob must apply");

                // ...and with no globs at all, nothing is excluded.
                TestHarness.NotEqual(DataHasher.ComputeHash(Entries("data/x.json", "1"), null),
                    DataHasher.ComputeHash(new List<DataFileEntry>
                    {
                        new DataFileEntry("data/x.json", "1"),
                        new DataFileEntry("data/scratch/tmp.json", "junk"),
                    }, null), "null globs must exclude nothing");
            });

            TestHarness.Run("glob matcher handles **, *, ? and bare filename patterns", delegate
            {
                TestHarness.True(GlobMatcher.IsMatch("a/b/Localization/en.json", "**/Localization/**"), "nested localization");
                TestHarness.True(GlobMatcher.IsMatch("Localization/en.json", "**/Localization/**"), "root-level localization");
                TestHarness.False(GlobMatcher.IsMatch("Profiles/en.json", "**/Localization/**"), "non-localization");
                TestHarness.True(GlobMatcher.IsMatch("deep/nested/icon.png", "*.png"), "bare filename pattern");
                TestHarness.True(GlobMatcher.IsMatch("a/b.json", "a/?.json"), "single-char wildcard");
                TestHarness.False(GlobMatcher.IsMatch("a/bb.json", "a/?.json"), "single-char wildcard must not span two chars");
                TestHarness.False(GlobMatcher.IsMatch("a/b/c.json", "a/*.json"), "* must not cross a path separator");
            });

            TestHarness.Run("hashing a real folder on disk matches the in-memory computation", delegate
            {
                string root = Path.Combine(Path.GetTempPath(), "ftk2devkit-hash-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(Path.Combine(root, "Profiles"));
                    Directory.CreateDirectory(Path.Combine(root, "Localization"));
                    // CRLF on disk; the in-memory expectation uses LF - normalization must bridge them.
                    File.WriteAllText(Path.Combine(root, "Profiles", "brute.brain.json"), "{\r\n  \"Id\": \"WB_BRAIN_BRUTE\"\r\n}\r\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "Macros.json"), "{}\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "Localization", "en.json"), "{\"A\":\"B\"}", new UTF8Encoding(false));

                    string diskHash = DataHasher.ComputeFolderHash(root, null);
                    string expected = DataHasher.ComputeHash(new List<DataFileEntry>
                    {
                        new DataFileEntry("Profiles/brute.brain.json", "{\n  \"Id\": \"WB_BRAIN_BRUTE\"\n}\n"),
                        new DataFileEntry("Macros.json", "{}\n"),
                    }, null);
                    TestHarness.Equal(expected, diskHash, "disk hash must equal the in-memory canonical hash");

                    // A UTF-8 BOM must not change the hash either.
                    File.WriteAllText(Path.Combine(root, "Macros.json"), "{}\n", new UTF8Encoding(true));
                    TestHarness.Equal(expected, DataHasher.ComputeFolderHash(root, null), "BOM must not change the hash");
                }
                finally
                {
                    TryDelete(root);
                }
            });

            TestHarness.Run("a missing data folder hashes to empty (guaranteed mismatch, not a plausible hash)", delegate
            {
                string missing = Path.Combine(Path.GetTempPath(), "ftk2devkit-missing-" + Guid.NewGuid().ToString("N"));
                TestHarness.Equal("", DataHasher.ComputeFolderHash(missing, null), "missing folder");
                TestHarness.False(DataHasher.IsWellFormedHash(DataHasher.ComputeFolderHash(missing, null)), "must not be well-formed");
            });

            TestHarness.Run("SPEC 3 compatibility overload (file list + exclude predicate) works", delegate
            {
                string root = Path.Combine(Path.GetTempPath(), "ftk2devkit-list-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, "a.json"), "1\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "b.json"), "2\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "skip.json"), "3\n", new UTF8Encoding(false));
                    string[] all = new string[]
                    {
                        Path.Combine(root, "a.json"),
                        Path.Combine(root, "b.json"),
                        Path.Combine(root, "skip.json"),
                    };
                    string filtered = DataHasher.ComputeHash(all, delegate (string p) { return p.EndsWith("skip.json", StringComparison.Ordinal); });
                    string expected = DataHasher.ComputeFileListHash(root, new string[] { all[0], all[1] }, new string[0]);
                    TestHarness.Equal(expected, filtered, "predicate-excluded list must match the explicit list");
                }
                finally
                {
                    TryDelete(root);
                }
            });

            // ---- culture stress -------------------------------------------------------------
            // Turkish-I: a locale-sensitive ToLower() would fold "LOCALIZATION" to "localızatıon"
            // and silently stop excluding localization files, so this peer would hash files every
            // other peer skipped - a permanent false data mismatch for the whole session.
            string invariantHash = DataHasher.ComputeHash(new List<DataFileEntry>
            {
                new DataFileEntry("Data/Item.json", "{\"Id\":\"FRG_ORB\"}"),
                new DataFileEntry("LOCALIZATION/EN.JSON", "text"),
                new DataFileEntry("ART/ICON.PNG", "bits"),
            }, DataHasher.GetDefaultExclusionGlobs());

            TestHarness.RunInCulture("exclusion globs stay case-insensitive under a Turkish-I culture", "tr-TR", delegate
            {
                string turkish = DataHasher.ComputeHash(new List<DataFileEntry>
                {
                    new DataFileEntry("Data/Item.json", "{\"Id\":\"FRG_ORB\"}"),
                    new DataFileEntry("LOCALIZATION/EN.JSON", "text"),
                    new DataFileEntry("ART/ICON.PNG", "bits"),
                }, DataHasher.GetDefaultExclusionGlobs());
                TestHarness.Equal(invariantHash, turkish, "Turkish culture changed which files were excluded");
                string onlyData = DataHasher.ComputeHash(Entries("Data/Item.json", "{\"Id\":\"FRG_ORB\"}"), DataHasher.GetDefaultExclusionGlobs());
                TestHarness.Equal(onlyData, turkish, "uppercase LOCALIZATION/PNG must still be excluded under tr-TR");
            });

            TestHarness.RunInCulture("hash bytes are identical under a comma-decimal culture", "de-DE", delegate
            {
                TestHarness.Equal(invariantHash, DataHasher.ComputeHash(new List<DataFileEntry>
                {
                    new DataFileEntry("Data/Item.json", "{\"Id\":\"FRG_ORB\"}"),
                    new DataFileEntry("LOCALIZATION/EN.JSON", "text"),
                    new DataFileEntry("ART/ICON.PNG", "bits"),
                }, DataHasher.GetDefaultExclusionGlobs()), "German culture changed the hash");
            });
        }

        private static List<DataFileEntry> Entries(string relativePath, string content)
        {
            return new List<DataFileEntry> { new DataFileEntry(relativePath, content) };
        }

        /// <summary>
        /// Turns a canonical DevKit hash into the bare 64-hex form the sibling hashers emit
        /// (ClassForge.Core/DataHasher.cs:44, Summoner.Core/Parity/DataHasher.cs:48).
        /// </summary>
        internal static string StripPrefix(string canonicalHash)
        {
            return canonicalHash.StartsWith(DataHasher.HashPrefix, StringComparison.Ordinal)
                ? canonicalHash.Substring(DataHasher.HashPrefix.Length)
                : canonicalHash;
        }

        private static void TryDelete(string root)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
