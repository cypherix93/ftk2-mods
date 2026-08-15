using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LiveDataHarness
{
    /// <summary>
    /// Machine-readable run summary, so a run can gate a future CI step or be diffed between game updates
    /// to see exactly which checks a patch changed.
    ///
    /// The payload deliberately carries no timestamp, duration, or machine identity: two runs over identical
    /// inputs must produce byte-identical files, which is what lets a caller compare two processes' reports
    /// and learn something real rather than compare clocks.
    ///
    /// It does carry the pack dataHash and a digest of the merge ordering. Those are the values two players'
    /// installs must agree on, and without them in the payload a byte comparison of two reports would pass
    /// even when the two processes disagreed about both.
    /// </summary>
    public static class Report
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // CheckFailure carries public fields rather than properties, which the serializer skips by
            // default — without this every failure would be written as an empty object.
            IncludeFields = true,
        };

        /// <summary>Returns false when the requested path resolves outside the repo, in which case nothing
        /// is written.</summary>
        public static bool Write(string path, string gameRoot, string dataHash, string mergeOrderHash,
                                 int passed, int failed, IReadOnlyList<CheckFailure> failures)
        {
            var full = Path.GetFullPath(path);
            if (!IsInsideRepo(full)) return false;

            var payload = new Dictionary<string, object>
            {
                { "gameRoot", gameRoot },
                { "dataHash", dataHash },
                { "mergeOrderHash", mergeOrderHash },
                { "passed", passed },
                { "failed", failed },
                { "failures", failures },
            };
            var json = JsonSerializer.Serialize(payload, Options);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, json);
            return true;
        }

        /// <summary>
        /// The report path is the only caller-controlled write in the program, and the harness writes nothing
        /// outside the repo — the game directory in particular must stay strictly read-only to it, so that
        /// running the harness can never be what corrupts the baseline it is measuring.
        /// </summary>
        private static bool IsInsideRepo(string fullPath)
        {
            var repo = PackRoots.RepoRoot();
            if (repo == null) return false;
            var root = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
