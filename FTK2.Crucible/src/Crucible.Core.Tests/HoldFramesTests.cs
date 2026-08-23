namespace FTK2Mods.Crucible.Tests
{
    internal static class HoldFramesTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("HoldFrames.ForMilliseconds");

            TestHarness.Run("converts a duration into whole frames at the assumed frame budget", delegate
            {
                TestHarness.Equal(6, HoldFrames.ForMilliseconds(100), "100ms -> 6 frames");
                TestHarness.Equal(12, HoldFrames.ForMilliseconds(200), "200ms -> 12 frames");
            });

            TestHarness.Run("never returns fewer than the minimum, so a frame always sees the press", delegate
            {
                // The whole point of the type: a sub-frame hold that rounds to 0 or 1 would put the
                // press and release close enough to collapse, which is the bug this replaced.
                TestHarness.Equal(HoldFrames.Minimum, HoldFrames.ForMilliseconds(1), "1ms floors to minimum");
                TestHarness.Equal(HoldFrames.Minimum, HoldFrames.ForMilliseconds(16), "16ms floors to minimum");
                TestHarness.Equal(HoldFrames.Minimum, HoldFrames.ForMilliseconds(33), "33ms floors to minimum");
            });

            TestHarness.Run("negative control: zero and negative durations floor to the minimum, never 0 or negative", delegate
            {
                TestHarness.Equal(HoldFrames.Minimum, HoldFrames.ForMilliseconds(0), "0ms");
                TestHarness.Equal(HoldFrames.Minimum, HoldFrames.ForMilliseconds(-1), "-1ms");
                TestHarness.Equal(HoldFrames.Minimum, HoldFrames.ForMilliseconds(-1000), "-1000ms");
            });
        }
    }
}
