using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LiveDataHarness
{
    /// <summary>
    /// Machine-readable run summary, so the harness can gate a future CI step or be diffed between game
    /// updates.
    ///
    /// Deliberately absent (AC5): elapsed times, the resolved game path, and the repo path. All three vary
    /// between two runs of an unchanged setup, and a report that cannot be byte-compared cannot prove
    /// cross-process determinism. Everything present is sorted ordinally for the same reason.
    /// </summary>
    public static class Report
    {
        public static void Write(string path, int passed, int failed,
                                 IReadOnlyList<CheckFailure> failures, IReadOnlyList<string> warnings)
        {
            List<Dictionary<string, string>> failureRows = failures
                .OrderBy(delegate (CheckFailure f) { return f.Name; }, StringComparer.Ordinal)
                .Select(delegate (CheckFailure f)
                {
                    Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.Ordinal);
                    row["name"] = f.Name;
                    row["message"] = f.Message;
                    return row;
                })
                .ToList();

            List<string> warningRows = warnings.OrderBy(delegate (string w) { return w; }, StringComparer.Ordinal).ToList();

            Dictionary<string, object> payload = new Dictionary<string, object>(StringComparer.Ordinal);
            payload["schema"] = "livedataharness.report.v1";
            payload["passed"] = passed;
            payload["failed"] = failed;
            payload["failures"] = failureRows;
            payload["warnings"] = warningRows;

            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            string json = JsonSerializer.Serialize(payload, options);

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }
}
