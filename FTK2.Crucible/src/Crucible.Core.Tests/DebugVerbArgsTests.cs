namespace FTK2Mods.Crucible.Tests
{
    internal static class DebugVerbArgsTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("PhaseName.TryParse");

            TestHarness.Run("maps combat", delegate
            {
                string type; bool needsOption; string error;
                TestHarness.True(PhaseName.TryParse("combat", out type, out needsOption, out error), "parsed: " + error);
                TestHarness.Equal("CombatPhase", type, "type");
                TestHarness.False(needsOption, "no option");
            });

            TestHarness.Run("maps rest and flags needsOption", delegate
            {
                string type; bool needsOption; string error;
                TestHarness.True(PhaseName.TryParse("rest", out type, out needsOption, out error), "parsed: " + error);
                TestHarness.Equal("RestPhase", type, "type");
                TestHarness.True(needsOption, "rest needs an option arg");
            });

            TestHarness.Run("maps every remaining phase", delegate
            {
                string type; bool needsOption; string error;
                TestHarness.True(PhaseName.TryParse("encounter", out type, out needsOption, out error), "encounter");
                TestHarness.Equal("EncounterPhase", type, "type");
                TestHarness.True(PhaseName.TryParse("fortune", out type, out needsOption, out error), "fortune");
                TestHarness.Equal("FortunePhase", type, "type");
                TestHarness.True(PhaseName.TryParse("treasure", out type, out needsOption, out error), "treasure");
                TestHarness.Equal("TreasurePhase", type, "type");
                TestHarness.True(PhaseName.TryParse("trap", out type, out needsOption, out error), "trap");
                TestHarness.Equal("TrapPhase", type, "type");
                TestHarness.True(PhaseName.TryParse("wheel", out type, out needsOption, out error), "wheel");
                TestHarness.Equal("WheelPhase", type, "type");
            });

            TestHarness.Run("is case-insensitive", delegate
            {
                string type; bool needsOption; string error;
                TestHarness.True(PhaseName.TryParse("CoMbAt", out type, out needsOption, out error), "parsed: " + error);
                TestHarness.Equal("CombatPhase", type, "type");
            });

            TestHarness.Run("refuses unknown phase rather than defaulting", delegate
            {
                string type; bool needsOption; string error;
                TestHarness.False(PhaseName.TryParse("cutscene", out type, out needsOption, out error), "should refuse");
                TestHarness.True(type == null, "no type on refusal");
                TestHarness.True(error != null && error.Length > 0, "error present");
            });

            TestHarness.Run("refuses empty phase", delegate
            {
                string type; bool needsOption; string error;
                TestHarness.False(PhaseName.TryParse("", out type, out needsOption, out error), "should refuse");
            });

            TestHarness.Run("refuses whitespace-only phase", delegate
            {
                string type; bool needsOption; string error;
                TestHarness.False(PhaseName.TryParse("   ", out type, out needsOption, out error), "should refuse");
            });

            TestHarness.Section("IntArg");

            TestHarness.Run("parses a valid int", delegate
            {
                int value; string error;
                TestHarness.True(IntArg.TryParse("42", "level", out value, out error), "parsed: " + error);
                TestHarness.Equal(42, value, "value");
            });

            TestHarness.Run("parses a negative int", delegate
            {
                int value; string error;
                TestHarness.True(IntArg.TryParse("-7", "level", out value, out error), "parsed: " + error);
                TestHarness.Equal(-7, value, "value");
            });

            TestHarness.Run("refuses non-numeric rather than defaulting to 0", delegate
            {
                int value; string error;
                TestHarness.False(IntArg.TryParse("abc", "level", out value, out error), "should refuse");
                TestHarness.True(error.IndexOf("level") >= 0, "error names the argument");
            });

            TestHarness.Run("refuses empty", delegate
            {
                int value; string error;
                TestHarness.False(IntArg.TryParse("", "seed", out value, out error), "should refuse");
            });

            TestHarness.Run("refuses a float-looking string", delegate
            {
                int value; string error;
                TestHarness.False(IntArg.TryParse("3.5", "qty", out value, out error), "should refuse");
            });

            TestHarness.Run("TryParsePositive refuses zero", delegate
            {
                int value; string error;
                TestHarness.False(IntArg.TryParsePositive("0", "steps", out value, out error), "should refuse");
            });

            TestHarness.Run("TryParsePositive refuses negative", delegate
            {
                int value; string error;
                TestHarness.False(IntArg.TryParsePositive("-3", "steps", out value, out error), "should refuse");
            });

            TestHarness.Run("TryParsePositive allows 1", delegate
            {
                int value; string error;
                TestHarness.True(IntArg.TryParsePositive("1", "steps", out value, out error), "should allow: " + error);
            });

            TestHarness.Run("TryParseNonNegative refuses negative", delegate
            {
                int value; string error;
                TestHarness.False(IntArg.TryParseNonNegative("-1", "qty", out value, out error), "should refuse");
            });

            TestHarness.Run("TryParseNonNegative allows zero", delegate
            {
                int value; string error;
                TestHarness.True(IntArg.TryParseNonNegative("0", "qty", out value, out error), "should allow: " + error);
            });

            TestHarness.Section("PartySlotArg");

            TestHarness.Run("allows in-range slot", delegate
            {
                int slot; string error;
                TestHarness.True(PartySlotArg.TryParse("3", out slot, out error), "should allow: " + error);
                TestHarness.Equal(3, slot, "slot");
            });

            TestHarness.Run("allows the max boundary slot", delegate
            {
                int slot; string error;
                TestHarness.True(PartySlotArg.TryParse("5", out slot, out error), "should allow: " + error);
            });

            TestHarness.Run("refuses out-of-range slot", delegate
            {
                int slot; string error;
                TestHarness.False(PartySlotArg.TryParse("99", out slot, out error), "should refuse");
            });

            TestHarness.Run("refuses negative slot", delegate
            {
                int slot; string error;
                TestHarness.False(PartySlotArg.TryParse("-1", out slot, out error), "should refuse");
            });

            TestHarness.Run("refuses non-numeric slot", delegate
            {
                int slot; string error;
                TestHarness.False(PartySlotArg.TryParse("two", out slot, out error), "should refuse");
            });

            TestHarness.Section("BoolToggleArg");

            TestHarness.Run("parses on/off", delegate
            {
                bool value; string error;
                TestHarness.True(BoolToggleArg.TryParse("on", out value, out error), "on parsed: " + error);
                TestHarness.True(value, "on -> true");
                TestHarness.True(BoolToggleArg.TryParse("off", out value, out error), "off parsed: " + error);
                TestHarness.False(value, "off -> false");
            });

            TestHarness.Run("parses true/false and 1/0", delegate
            {
                bool value; string error;
                TestHarness.True(BoolToggleArg.TryParse("true", out value, out error), "true parsed");
                TestHarness.True(value, "true -> true");
                TestHarness.True(BoolToggleArg.TryParse("0", out value, out error), "0 parsed");
                TestHarness.False(value, "0 -> false");
            });

            TestHarness.Run("refuses garbage rather than defaulting", delegate
            {
                bool value; string error;
                TestHarness.False(BoolToggleArg.TryParse("maybe", out value, out error), "should refuse");
            });

            TestHarness.Run("refuses empty", delegate
            {
                bool value; string error;
                TestHarness.False(BoolToggleArg.TryParse("", out value, out error), "should refuse");
            });

            TestHarness.Section("ChaosGate (negative control)");

            TestHarness.Run("frozen holds a value that would otherwise move", delegate
            {
                int value = 5;
                for (int i = 0; i < 3; i++)
                {
                    if (!ChaosGate.ShouldSkipOriginal(true)) value++;
                }
                TestHarness.Equal(5, value, "value must be held while frozen");
            });

            TestHarness.Run("unfrozen lets the same loop move the value (negative control)", delegate
            {
                int value = 5;
                for (int i = 0; i < 3; i++)
                {
                    if (!ChaosGate.ShouldSkipOriginal(false)) value++;
                }
                TestHarness.Equal(8, value, "value must move when not frozen -- proves the frozen case above is a real hold, not a coincidence");
            });
        }
    }
}
