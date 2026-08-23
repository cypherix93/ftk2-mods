namespace FTK2Mods.Crucible.Tests
{
    internal static class WarningTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("WarningSink");

            TestHarness.Run("records a member miss with type and member name", delegate
            {
                WarningSink sink = new WarningSink();
                sink.MemberMissing("NetworkData", "PlayerCount");
                TestHarness.Equal(1, sink.Count, "one warning");
                TestHarness.Equal("member_missing: NetworkData.PlayerCount", sink.ToArray()[0], "text");
            });

            TestHarness.Run("deduplicates identical messages", delegate
            {
                WarningSink sink = new WarningSink();
                for (int i = 0; i < 12; i++) sink.MemberMissing("CharacterComponent", "DisplayName");
                TestHarness.Equal(1, sink.Count, "twelve combatants, one warning");
            });

            // Negative control: dedupe must not collapse genuinely different problems.
            TestHarness.Run("keeps distinct messages", delegate
            {
                WarningSink sink = new WarningSink();
                sink.MemberMissing("CharacterComponent", "DisplayName");
                sink.MemberMissing("CharacterComponent", "ConfigName");
                sink.MemberThrew("Entity", "Guid", "boom");
                sink.Ambiguous("CharacterHelper", "GetStat", 3, "Entity");
                sink.TypeMissing("CombatPhase");
                sink.Note("something else");
                TestHarness.Equal(6, sink.Count, "six distinct warnings");
            });

            TestHarness.Run("ignores null and empty messages", delegate
            {
                WarningSink sink = new WarningSink();
                sink.Add(null);
                sink.Add("");
                sink.Note(null);
                TestHarness.Equal(0, sink.Count, "nothing recorded");
            });
        }
    }
}
