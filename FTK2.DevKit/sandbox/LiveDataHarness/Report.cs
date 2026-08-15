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
    /// inputs must produce byte-identical files, which is what makes comparing two processes' reports a
    /// meaningful cross-process determinism test rather than a clock comparison.
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

        public static void Write(string path, string gameRoot, int passed, int failed, IReadOnlyList<CheckFailure> failures)
        {
            var payload = new Dictionary<string, object>
            {
                { "gameRoot", gameRoot },
                { "passed", passed },
                { "failed", failed },
                { "failures", failures },
            };
            var json = JsonSerializer.Serialize(payload, Options);
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }
}
