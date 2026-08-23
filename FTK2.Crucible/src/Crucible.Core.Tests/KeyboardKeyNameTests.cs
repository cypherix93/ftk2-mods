namespace FTK2Mods.Crucible.Tests
{
    internal static class KeyboardKeyNameTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("KeyboardKeyName.TryParse");

            TestHarness.Run("maps the four arrow aliases onto Unity Key members", delegate
            {
                TestHarness.Equal("UpArrow", Parse("up"), "up -> UpArrow");
                TestHarness.Equal("DownArrow", Parse("down"), "down -> DownArrow");
                TestHarness.Equal("LeftArrow", Parse("left"), "left -> LeftArrow");
                TestHarness.Equal("RightArrow", Parse("right"), "right -> RightArrow");
            });

            TestHarness.Run("maps confirm/cancel aliases, including the pad-style a/b", delegate
            {
                TestHarness.Equal("Enter", Parse("enter"), "enter -> Enter");
                TestHarness.Equal("Enter", Parse("submit"), "submit -> Enter");
                TestHarness.Equal("Enter", Parse("a"), "a -> Enter");
                TestHarness.Equal("Escape", Parse("escape"), "escape -> Escape");
                TestHarness.Equal("Escape", Parse("cancel"), "cancel -> Escape");
                TestHarness.Equal("Escape", Parse("back"), "back -> Escape");
                TestHarness.Equal("Escape", Parse("b"), "b -> Escape");
            });

            TestHarness.Run("is case-insensitive and trims surrounding whitespace", delegate
            {
                TestHarness.Equal("UpArrow", Parse("  UP  "), "'  UP  ' -> UpArrow");
                TestHarness.Equal("Enter", Parse("Submit"), "Submit -> Enter");
            });

            TestHarness.Run("passes an unaliased name through so any Key member is reachable", delegate
            {
                // Deliberate: the Key enum has ~110 members. Enumerating them in Core would rot
                // against an Input System update; Enum.Parse in the plugin reports the real list.
                TestHarness.Equal("F5", Parse("F5"), "F5 passes through");
                TestHarness.Equal("Digit1", Parse("Digit1"), "Digit1 passes through");
            });

            TestHarness.Run("negative control: an empty key is rejected, not defaulted", delegate
            {
                string canonical, e;
                TestHarness.False(KeyboardKeyName.TryParse("", out canonical, out e), "empty rejected");
                TestHarness.True(e != null, "empty reports an error");

                TestHarness.False(KeyboardKeyName.TryParse("   ", out canonical, out e), "whitespace rejected");
                TestHarness.True(e != null, "whitespace reports an error");

                TestHarness.False(KeyboardKeyName.TryParse(null, out canonical, out e), "null rejected");
                TestHarness.True(e != null, "null reports an error");
            });
        }

        private static string Parse(string raw)
        {
            string canonical, e;
            TestHarness.True(KeyboardKeyName.TryParse(raw, out canonical, out e), raw + ": " + e);
            return canonical;
        }
    }
}
