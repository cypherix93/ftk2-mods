namespace FTK2Mods.Crucible.Tests
{
    internal static class PadCommandTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("GamepadButtonName.TryParse");

            TestHarness.Run("parses face buttons by letter and by GamepadButton name", delegate
            {
                string canonical, e;
                TestHarness.True(GamepadButtonName.TryParse("a", out canonical, out e), "a: " + e);
                TestHarness.Equal("South", canonical, "a -> South");
                TestHarness.True(GamepadButtonName.TryParse("SOUTH", out canonical, out e), "SOUTH: " + e);
                TestHarness.Equal("South", canonical, "SOUTH -> South");

                TestHarness.True(GamepadButtonName.TryParse("b", out canonical, out e), "b: " + e);
                TestHarness.Equal("East", canonical, "b -> East");

                TestHarness.True(GamepadButtonName.TryParse("x", out canonical, out e), "x: " + e);
                TestHarness.Equal("West", canonical, "x -> West");

                TestHarness.True(GamepadButtonName.TryParse("y", out canonical, out e), "y: " + e);
                TestHarness.Equal("North", canonical, "y -> North");
            });

            TestHarness.Run("parses dpad directions", delegate
            {
                string canonical, e;
                TestHarness.True(GamepadButtonName.TryParse("up", out canonical, out e), "up: " + e);
                TestHarness.Equal("DpadUp", canonical, "up -> DpadUp");
                TestHarness.True(GamepadButtonName.TryParse("down", out canonical, out e), "down: " + e);
                TestHarness.Equal("DpadDown", canonical, "down -> DpadDown");
                TestHarness.True(GamepadButtonName.TryParse("left", out canonical, out e), "left: " + e);
                TestHarness.Equal("DpadLeft", canonical, "left -> DpadLeft");
                TestHarness.True(GamepadButtonName.TryParse("RIGHT", out canonical, out e), "right: " + e);
                TestHarness.Equal("DpadRight", canonical, "right -> DpadRight");
            });

            TestHarness.Run("parses start/select/lb/rb, case-insensitive", delegate
            {
                string canonical, e;
                TestHarness.True(GamepadButtonName.TryParse("Start", out canonical, out e), "start: " + e);
                TestHarness.Equal("Start", canonical, "start -> Start");
                TestHarness.True(GamepadButtonName.TryParse("SELECT", out canonical, out e), "select: " + e);
                TestHarness.Equal("Select", canonical, "select -> Select");
                TestHarness.True(GamepadButtonName.TryParse("lb", out canonical, out e), "lb: " + e);
                TestHarness.Equal("LeftShoulder", canonical, "lb -> LeftShoulder");
                TestHarness.True(GamepadButtonName.TryParse("Rb", out canonical, out e), "rb: " + e);
                TestHarness.Equal("RightShoulder", canonical, "rb -> RightShoulder");
            });

            TestHarness.Run("NEGATIVE CONTROL: rejects an unknown button rather than defaulting", delegate
            {
                string canonical, e;
                TestHarness.False(GamepadButtonName.TryParse("z", out canonical, out e), "should reject");
                TestHarness.True(canonical == null, "canonical stays null on failure");
                TestHarness.True(e != null && e.Length > 0, "error message present");
                TestHarness.True(e.IndexOf("south", System.StringComparison.OrdinalIgnoreCase) >= 0,
                    "error lists valid names: " + e);
            });

            TestHarness.Run("rejects empty/null/whitespace button names", delegate
            {
                string canonical, e;
                TestHarness.False(GamepadButtonName.TryParse("", out canonical, out e), "empty");
                TestHarness.False(GamepadButtonName.TryParse(null, out canonical, out e), "null");
                TestHarness.False(GamepadButtonName.TryParse("   ", out canonical, out e), "whitespace");
            });

            TestHarness.Section("GamepadStick.TryParseSide");

            TestHarness.Run("parses left/right, case-insensitive", delegate
            {
                string canonical, e;
                TestHarness.True(GamepadStick.TryParseSide("left", out canonical, out e), "left: " + e);
                TestHarness.Equal("Left", canonical, "left -> Left");
                TestHarness.True(GamepadStick.TryParseSide("RIGHT", out canonical, out e), "right: " + e);
                TestHarness.Equal("Right", canonical, "right -> Right");
            });

            TestHarness.Run("NEGATIVE CONTROL: rejects an unknown stick name", delegate
            {
                string canonical, e;
                TestHarness.False(GamepadStick.TryParseSide("middle", out canonical, out e), "should reject");
                TestHarness.True(canonical == null, "canonical stays null on failure");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects empty/null stick name", delegate
            {
                string canonical, e;
                TestHarness.False(GamepadStick.TryParseSide("", out canonical, out e), "empty");
                TestHarness.False(GamepadStick.TryParseSide(null, out canonical, out e), "null");
            });

            TestHarness.Section("GamepadStick.TryParseDirection");

            TestHarness.Run("center maps to (0,0)", delegate
            {
                float x, y; string e;
                TestHarness.True(GamepadStick.TryParseDirection("center", out x, out y, out e), "center: " + e);
                TestHarness.True(x == 0f && y == 0f, "center is the zero vector");
            });

            TestHarness.Run("each direction maps to a distinct non-zero vector", delegate
            {
                float upX, upY, downX, downY, leftX, leftY, rightX, rightY; string e;
                TestHarness.True(GamepadStick.TryParseDirection("up", out upX, out upY, out e), "up: " + e);
                TestHarness.True(GamepadStick.TryParseDirection("down", out downX, out downY, out e), "down: " + e);
                TestHarness.True(GamepadStick.TryParseDirection("left", out leftX, out leftY, out e), "left: " + e);
                TestHarness.True(GamepadStick.TryParseDirection("right", out rightX, out rightY, out e), "right: " + e);

                TestHarness.True(!(upX == 0f && upY == 0f), "up is non-zero");
                TestHarness.True(!(downX == 0f && downY == 0f), "down is non-zero");
                TestHarness.True(!(leftX == 0f && leftY == 0f), "left is non-zero");
                TestHarness.True(!(rightX == 0f && rightY == 0f), "right is non-zero");

                TestHarness.True(!(upX == downX && upY == downY), "up != down");
                TestHarness.True(!(leftX == rightX && leftY == rightY), "left != right");
                TestHarness.True(!(upX == leftX && upY == leftY), "up != left");
                TestHarness.True(!(upX == rightX && upY == rightY), "up != right");
                TestHarness.True(!(downX == leftX && downY == leftY), "down != left");
                TestHarness.True(!(downX == rightX && downY == rightY), "down != right");
            });

            TestHarness.Run("NEGATIVE CONTROL: rejects an unknown direction", delegate
            {
                float x, y; string e;
                TestHarness.False(GamepadStick.TryParseDirection("diagonal", out x, out y, out e), "should reject");
                TestHarness.True(x == 0f && y == 0f, "out params stay zero on failure");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects empty/null direction", delegate
            {
                float x, y; string e;
                TestHarness.False(GamepadStick.TryParseDirection("", out x, out y, out e), "empty");
                TestHarness.False(GamepadStick.TryParseDirection(null, out x, out y, out e), "null");
            });

            TestHarness.Section("PadHoldDuration.TryParse");

            TestHarness.Run("parses a normal positive duration unchanged", delegate
            {
                int ms; bool clamped; string e;
                TestHarness.True(PadHoldDuration.TryParse("250", out ms, out clamped, out e), "250: " + e);
                TestHarness.Equal(250, ms, "ms");
                TestHarness.False(clamped, "not clamped");
            });

            TestHarness.Run("clamps a duration above the max instead of rejecting it", delegate
            {
                int ms; bool clamped; string e;
                TestHarness.True(PadHoldDuration.TryParse("999999", out ms, out clamped, out e), "999999: " + e);
                TestHarness.Equal(PadHoldDuration.MaxMs, ms, "clamped to max");
                TestHarness.True(clamped, "clamped flag set");
            });

            TestHarness.Run("NEGATIVE CONTROL: rejects a negative duration rather than defaulting", delegate
            {
                int ms; bool clamped; string e;
                TestHarness.False(PadHoldDuration.TryParse("-100", out ms, out clamped, out e), "should reject");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("NEGATIVE CONTROL: rejects zero", delegate
            {
                int ms; bool clamped; string e;
                TestHarness.False(PadHoldDuration.TryParse("0", out ms, out clamped, out e), "should reject");
            });

            TestHarness.Run("NEGATIVE CONTROL: rejects a non-numeric duration", delegate
            {
                int ms; bool clamped; string e;
                TestHarness.False(PadHoldDuration.TryParse("abc", out ms, out clamped, out e), "should reject");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects empty/null duration", delegate
            {
                int ms; bool clamped; string e;
                TestHarness.False(PadHoldDuration.TryParse("", out ms, out clamped, out e), "empty");
                TestHarness.False(PadHoldDuration.TryParse(null, out ms, out clamped, out e), "null");
            });
        }
    }
}
