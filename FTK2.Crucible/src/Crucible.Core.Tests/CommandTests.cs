namespace FTK2Mods.Crucible.Tests
{
    internal static class CommandTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("CommandRequest.TryParse");

            TestHarness.Run("parses bare command", delegate
            {
                CommandRequest r; string e;
                TestHarness.True(CommandRequest.TryParse("EndPhase", out r, out e), "parsed: " + e);
                TestHarness.Equal("EndPhase", r.Name, "name");
                TestHarness.Equal(0, r.Args.Length, "no args");
            });

            TestHarness.Run("parses args", delegate
            {
                CommandRequest r; string e;
                TestHarness.True(CommandRequest.TryParse("SetPlayerHealth 0 25", out r, out e), "parsed: " + e);
                TestHarness.Equal("SetPlayerHealth", r.Name, "name");
                TestHarness.Equal(2, r.Args.Length, "arg count");
                TestHarness.Equal("25", r.Args[1], "second arg");
            });

            TestHarness.Run("honours quoted args with spaces", delegate
            {
                CommandRequest r; string e;
                TestHarness.True(CommandRequest.TryParse("GetSpecificThing \"Iron Sword\" 2", out r, out e), "parsed: " + e);
                TestHarness.Equal("Iron Sword", r.Args[0], "quoted arg");
                TestHarness.Equal("2", r.Args[1], "trailing arg");
            });

            TestHarness.Run("collapses repeated whitespace", delegate
            {
                CommandRequest r; string e;
                TestHarness.True(CommandRequest.TryParse("SetStat   HP    10", out r, out e), "parsed: " + e);
                TestHarness.Equal(2, r.Args.Length, "arg count");
                TestHarness.Equal("HP", r.Args[0], "first arg");
            });

            TestHarness.Run("rejects empty input", delegate
            {
                CommandRequest r; string e;
                TestHarness.False(CommandRequest.TryParse("   ", out r, out e), "should reject");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects unterminated quote", delegate
            {
                CommandRequest r; string e;
                TestHarness.False(CommandRequest.TryParse("Cmd \"open", out r, out e), "should reject");
            });

            TestHarness.Section("CommandGate");

            TestHarness.Run("allows anything offline with empty allowlist", delegate
            {
                TestHarness.True(CommandGate.Evaluate("SetStat", false, false, new string[0]) == GateVerdict.Allow, "offline allow");
            });

            TestHarness.Run("blocks mutation in MP by default", delegate
            {
                TestHarness.True(CommandGate.Evaluate("EndPhase", true, false, new string[0]) == GateVerdict.DeniedMultiplayer, "EndPhase is a mutation");
            });

            TestHarness.Run("allows read-only in MP", delegate
            {
                TestHarness.True(CommandGate.Evaluate("ToggleUI", true, false, new string[0]) == GateVerdict.Allow, "read-only ok");
            });

            TestHarness.Run("read-only check is case-insensitive", delegate
            {
                TestHarness.True(CommandGate.Evaluate("toggleUI", true, false, new string[0]) == GateVerdict.Allow, "case-insensitive");
            });

            TestHarness.Run("unknown commands are treated as mutations", delegate
            {
                TestHarness.True(CommandGate.Evaluate("SomeFutureGameCommand", true, false, new string[0]) == GateVerdict.DeniedMultiplayer, "fail safe");
            });

            TestHarness.Run("AllowMutationsInMP unlocks", delegate
            {
                TestHarness.True(CommandGate.Evaluate("EndPhase", true, true, new string[0]) == GateVerdict.Allow, "override");
            });

            TestHarness.Run("allowlist excludes others, case-insensitively", delegate
            {
                string[] allow = new string[] { "endphase" };
                TestHarness.True(CommandGate.Evaluate("EndPhase", false, false, allow) == GateVerdict.Allow, "listed");
                TestHarness.True(CommandGate.Evaluate("SetStat", false, false, allow) == GateVerdict.DeniedNotAllowlisted, "unlisted");
            });

            TestHarness.Run("allowlist entries are trimmed", delegate
            {
                string[] allow = new string[] { " EndPhase ", "  " };
                TestHarness.True(CommandGate.Evaluate("EndPhase", false, false, allow) == GateVerdict.Allow, "trimmed entry matches");
            });

            TestHarness.Run("allowlist is applied before the MP gate", delegate
            {
                string[] allow = new string[] { "toggleui" };
                TestHarness.True(CommandGate.Evaluate("EndPhase", true, true, allow) == GateVerdict.DeniedNotAllowlisted, "allowlist wins");
            });
        }
    }
}
