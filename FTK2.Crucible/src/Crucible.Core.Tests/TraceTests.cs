using System.Collections.Generic;
using System.IO;

namespace FTK2Mods.Crucible.Tests
{
    internal static class TraceTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("TraceWriter");

            string dir = Path.Combine(Path.GetTempPath(), "crucible-trace-tests");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);

            TestHarness.Run("writes one JSON object per line", delegate
            {
                TraceWriter w = new TraceWriter(dir, "sess1", 100);
                w.TimestampProvider = delegate { return "2026-08-15T00:00:00Z"; };
                Dictionary<string, object> f = new Dictionary<string, object>();
                f["command"] = "EndPhase";
                w.Write("exec", "corr-1", f);
                w.Write("exec", "corr-2", f);
                w.Flush();

                string[] lines = File.ReadAllLines(w.FilePath);
                TestHarness.Equal(2, lines.Length, "line count");

                Dictionary<string, object> obj = MiniJson.AsObject(Parse(lines[0]));
                TestHarness.Equal("exec", MiniJson.AsString(obj["kind"]), "kind");
                TestHarness.Equal("corr-1", MiniJson.AsString(obj["correlationId"]), "correlation id");
                TestHarness.Equal("EndPhase", MiniJson.AsString(obj["command"]), "merged field");
                TestHarness.Equal("2026-08-15T00:00:00Z", MiniJson.AsString(obj["ts"]), "timestamp");
            });

            TestHarness.Run("Tail returns the most recent entries", delegate
            {
                TraceWriter w = new TraceWriter(dir, "sess2", 100);
                for (int i = 0; i < 5; i++)
                {
                    Dictionary<string, object> f = new Dictionary<string, object>();
                    f["i"] = i;
                    w.Write("exec", "c" + i, f);
                }
                string[] tail = w.Tail(2);
                TestHarness.Equal(2, tail.Length, "tail length");
                TestHarness.True(tail[1].Contains("\"c4\""), "last entry is newest");
                TestHarness.True(tail[0].Contains("\"c3\""), "first tail entry");
            });

            TestHarness.Run("ring buffer caps memory but the file keeps everything", delegate
            {
                TraceWriter w = new TraceWriter(dir, "sess3", 3);
                for (int i = 0; i < 10; i++)
                    w.Write("exec", "c" + i, new Dictionary<string, object>());
                w.Flush();
                TestHarness.Equal(3, w.Tail(100).Length, "buffer capped");
                TestHarness.Equal(10, File.ReadAllLines(w.FilePath).Length, "file complete");
            });

            TestHarness.Run("reserved keys are not overwritten by caller fields", delegate
            {
                TraceWriter w = new TraceWriter(dir, "sess4", 10);
                w.TimestampProvider = delegate { return "TS"; };
                Dictionary<string, object> f = new Dictionary<string, object>();
                f["kind"] = "hacked";
                f["correlationId"] = "hacked";
                w.Write("exec", "real", f);
                w.Flush();

                Dictionary<string, object> obj = MiniJson.AsObject(Parse(File.ReadAllLines(w.FilePath)[0]));
                TestHarness.Equal("exec", MiniJson.AsString(obj["kind"]), "kind preserved");
                TestHarness.Equal("real", MiniJson.AsString(obj["correlationId"]), "correlation id preserved");
            });

            TestHarness.Run("every buffered line is valid JSON", delegate
            {
                TraceWriter w = new TraceWriter(dir, "sess5", 10);
                Dictionary<string, object> f = new Dictionary<string, object>();
                f["weird"] = "quotes \" and \n newlines";
                w.Write("exec", "c", f);
                w.Flush();

                object parsed; string error;
                TestHarness.True(MiniJson.TryParse(File.ReadAllLines(w.FilePath)[0], out parsed, out error),
                    "line parses: " + error);
            });
        }

        private static object Parse(string s)
        {
            object v; string e;
            MiniJson.TryParse(s, out v, out e);
            return v;
        }
    }
}
