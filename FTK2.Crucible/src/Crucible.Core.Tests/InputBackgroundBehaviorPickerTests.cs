using System;

namespace FTK2Mods.Crucible.Tests
{
    internal static class InputBackgroundBehaviorPickerTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("InputBackgroundBehaviorPicker");

            TestHarness.Run("picks the documented IgnoreFocus member", delegate
            {
                string[] members = { "ResetAndDisableNonBackgroundDevices", "ResetAndDisableAllDevices", "IgnoreFocus" };
                string picked; string error;
                bool ok = InputBackgroundBehaviorPicker.TryPickIgnoreFocusMember(members, out picked, out error);
                TestHarness.True(ok, "found");
                TestHarness.Equal("IgnoreFocus", picked, "picked member");
            });

            TestHarness.Run("finds a renamed member by heuristic, not exact string", delegate
            {
                // A hypothetical future Input System renames the member — heuristic (contains both
                // "Ignore" and "Focus", case-insensitive) must still find it without a hardcoded guess.
                string[] members = { "ResetOnFocusLoss", "AlwaysProcess_IgnoreFocus" };
                string picked; string error;
                bool ok = InputBackgroundBehaviorPicker.TryPickIgnoreFocusMember(members, out picked, out error);
                TestHarness.True(ok, "found");
                TestHarness.Equal("AlwaysProcess_IgnoreFocus", picked, "picked the renamed member");
            });

            // Negative control: no member means "ignore focus" — must report actual members, not
            // fail silently or fabricate a name.
            TestHarness.Run("reports actual members when nothing matches, instead of failing silently", delegate
            {
                string[] members = { "ResetAndDisableAllDevices", "SomethingElseEntirely" };
                string picked; string error;
                bool ok = InputBackgroundBehaviorPicker.TryPickIgnoreFocusMember(members, out picked, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(picked == null, "no guess");
                TestHarness.True(error.IndexOf("ResetAndDisableAllDevices", StringComparison.Ordinal) >= 0, "names actual member 1");
                TestHarness.True(error.IndexOf("SomethingElseEntirely", StringComparison.Ordinal) >= 0, "names actual member 2");
            });

            TestHarness.Run("empty member list is a clean error, not a crash", delegate
            {
                string picked; string error;
                bool ok = InputBackgroundBehaviorPicker.TryPickIgnoreFocusMember(new string[0], out picked, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(error != null, "has a message");
            });

            TestHarness.Section("InputBackgroundBehaviorPicker — reset/default fallback");

            TestHarness.Run("prefers the non-background-specific reset member", delegate
            {
                string[] members = { "IgnoreFocus", "ResetAndDisableAllDevices", "ResetAndDisableNonBackgroundDevices" };
                string picked; string error;
                bool ok = InputBackgroundBehaviorPicker.TryPickResetDefaultMember(members, out picked, out error);
                TestHarness.True(ok, "found");
                TestHarness.Equal("ResetAndDisableNonBackgroundDevices", picked, "picked the specific default");
            });

            TestHarness.Run("falls back to any Reset* member when the specific one is absent", delegate
            {
                string[] members = { "IgnoreFocus", "ResetSomethingElse" };
                string picked; string error;
                bool ok = InputBackgroundBehaviorPicker.TryPickResetDefaultMember(members, out picked, out error);
                TestHarness.True(ok, "found");
                TestHarness.Equal("ResetSomethingElse", picked, "fallback pick");
            });

            TestHarness.Run("reports actual members when no reset-like member exists", delegate
            {
                string[] members = { "IgnoreFocus", "AlwaysProcess" };
                string picked; string error;
                bool ok = InputBackgroundBehaviorPicker.TryPickResetDefaultMember(members, out picked, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(error.IndexOf("IgnoreFocus", StringComparison.Ordinal) >= 0, "names actual member 1");
                TestHarness.True(error.IndexOf("AlwaysProcess", StringComparison.Ordinal) >= 0, "names actual member 2");
            });
        }
    }
}
