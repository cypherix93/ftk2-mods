using System;

namespace FTK2Mods.Crucible.Tests
{
    internal static class ScreenPointParserTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("ScreenPointParser");

            TestHarness.Run("parses a valid integer pair", delegate
            {
                float x, y; string error;
                bool ok = ScreenPointParser.TryParse("640", "360", out x, out y, out error);
                TestHarness.True(ok, "parsed");
                TestHarness.True(x == 640f, "x");
                TestHarness.True(y == 360f, "y");
            });

            TestHarness.Run("parses a valid fractional pair", delegate
            {
                float x, y; string error;
                bool ok = ScreenPointParser.TryParse("12.5", "0", out x, out y, out error);
                TestHarness.True(ok, "parsed");
                TestHarness.True(x == 12.5f, "x");
                TestHarness.True(y == 0f, "y");
            });

            TestHarness.Run("rejects non-numeric x", delegate
            {
                float x, y; string error;
                bool ok = ScreenPointParser.TryParse("nope", "10", out x, out y, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(error.IndexOf("x", StringComparison.Ordinal) >= 0, "mentions x");
            });

            TestHarness.Run("rejects negative y", delegate
            {
                float x, y; string error;
                bool ok = ScreenPointParser.TryParse("10", "-1", out x, out y, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(error.IndexOf("non-negative", StringComparison.Ordinal) >= 0, "explains why");
            });

            TestHarness.Run("rejects empty arguments", delegate
            {
                float x, y; string error;
                bool ok = ScreenPointParser.TryParse("", "10", out x, out y, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(error != null, "has a message");
            });
        }
    }
}
