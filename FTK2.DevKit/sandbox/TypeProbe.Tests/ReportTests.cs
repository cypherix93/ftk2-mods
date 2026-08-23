using System;

namespace TypeProbe.Tests
{
    internal static class ReportTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("Report");

            TestHarness.Run("renders a markdown table for a found type", delegate
            {
                ProbeResult r = Probe.Describe(new Type[] { typeof(Uri) }, "Uri", false);
                string md = Report.ToMarkdown(r);
                TestHarness.Assert(md.Contains("System.Uri"), "expected the full type name in the report");
                TestHarness.Assert(md.Contains("| Host |") || md.Contains("Host"), "expected Uri.Host in the report");
                TestHarness.Assert(md.Contains("|"), "expected a markdown table");
            });

            // NEGATIVE CONTROL: a not-found result must render as an explicit failure, never as an
            // empty-but-plausible table that a reader would mistake for "this type has no members".
            TestHarness.Run("NEGATIVE: not-found renders as NOT FOUND, not an empty table", delegate
            {
                ProbeResult r = Probe.Describe(new Type[] { typeof(Uri) }, "NoSuchType", false);
                string md = Report.ToMarkdown(r);
                TestHarness.Assert(md.Contains("NOT FOUND"), "expected an explicit NOT FOUND marker, got: " + md);
            });

            TestHarness.Run("report is deterministic across repeated calls", delegate
            {
                ProbeResult r = Probe.Describe(new Type[] { typeof(Uri) }, "Uri", true);
                TestHarness.Assert(Report.ToMarkdown(r) == Report.ToMarkdown(r), "report must be deterministic");
            });

            TestHarness.Run("null result does not throw", delegate
            {
                string md = Report.ToMarkdown(null);
                TestHarness.Assert(md != null && md.Contains("NOT FOUND"), "null result must render NOT FOUND");
            });
        }
    }
}
