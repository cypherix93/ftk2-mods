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

            TestHarness.Run("hash format is sha256: + 64 lowercase hex", delegate
            {
                string hash = DataHasher.ComputeHash(Entries("a.json", "{}"), null);
                TestHarness.True(hash.StartsWith("sha256:", StringComparison.Ordinal), "prefix");
                TestHarness.Equal(7 + 64, hash.Length, "length");
                TestHarness.True(DataHasher.IsWellFormedHash(hash), "IsWellFormedHash");
                TestHarness.False(DataHasher.IsWellFormedHash(hash.ToUpperInvariant()), "uppercase hex must be rejected");
                TestHarness.False(DataHasher.IsWellFormedHash("sha256:xyz"), "short hash must be rejected");
                TestHarness.False(DataHasher.IsWellFormedHash(""), "empty must be rejected");
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
