namespace FTK2Mods.Crucible.Tests
{
    /// <summary>
    /// Pure-logic coverage for crucible_load_run's safety gate and verification, ported to a
    /// dedicated file rather than folded into ReflectionProbeTests since these two classes have no
    /// relation to reflection -- they are plain string/bool decisions.
    /// </summary>
    internal static class LoadRunTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("LoadRunGuard.TryValidateRunId");

            TestHarness.Run("accepts a non-empty run id", delegate
            {
                string error;
                TestHarness.True(LoadRunGuard.TryValidateRunId("0778a3d1-5bf7-449a-acae-263102cf2235", out error), "should accept: " + error);
                TestHarness.True(error == null, "no error on success");
            });

            // NEGATIVE CONTROL: null must be refused, not defaulted to LastGameRunIdPlayed or "the
            // newest" save. This is the load-bearing safety property of the whole command.
            TestHarness.Run("NEGATIVE: refuses a null run id", delegate
            {
                string error;
                bool ok = LoadRunGuard.TryValidateRunId(null, out error);
                TestHarness.True(!ok, "a null run id must be refused");
                TestHarness.True(error != null && error.Length > 0, "refusal must explain itself");
            });

            // NEGATIVE CONTROL: empty string, same reasoning.
            TestHarness.Run("NEGATIVE: refuses an empty run id", delegate
            {
                string error;
                bool ok = LoadRunGuard.TryValidateRunId(string.Empty, out error);
                TestHarness.True(!ok, "an empty run id must be refused");
            });

            // NEGATIVE CONTROL: whitespace-only is not a real id either.
            TestHarness.Run("NEGATIVE: refuses a whitespace-only run id", delegate
            {
                string error;
                bool ok = LoadRunGuard.TryValidateRunId("   ", out error);
                TestHarness.True(!ok, "a whitespace-only run id must be refused");
            });

            TestHarness.Section("LoadRunGuard.IsKnownRunId");

            TestHarness.Run("finds a run id present in the known list", delegate
            {
                string[] known = { "aaa", "bbb", "ccc" };
                TestHarness.True(LoadRunGuard.IsKnownRunId("bbb", known), "bbb should be found");
            });

            TestHarness.Run("NEGATIVE: a run id absent from the known list is not known", delegate
            {
                string[] known = { "aaa", "bbb", "ccc" };
                TestHarness.True(!LoadRunGuard.IsKnownRunId("zzz", known), "zzz should not be found");
            });

            TestHarness.Run("NEGATIVE: a null known list never matches", delegate
            {
                TestHarness.True(!LoadRunGuard.IsKnownRunId("aaa", null), "null list should never match");
            });

            TestHarness.Run("match is ordinal (case-sensitive)", delegate
            {
                string[] known = { "AAA" };
                TestHarness.True(!LoadRunGuard.IsKnownRunId("aaa", known), "run ids are GUIDs; case must match exactly");
            });

            TestHarness.Section("LoadRunVerification.Confirm");

            TestHarness.Run("confirms when run is present and selected id matches", delegate
            {
                string evidence;
                bool ok = LoadRunVerification.Confirm("run-1", true, "run-1", out evidence);
                TestHarness.True(ok, "should confirm: " + evidence);
                TestHarness.True(evidence.IndexOf("run-1", System.StringComparison.Ordinal) >= 0, "evidence names the requested id");
            });

            TestHarness.Run("NEGATIVE: refuses when run is present but selected id differs", delegate
            {
                string evidence;
                bool ok = LoadRunVerification.Confirm("run-1", true, "run-2", out evidence);
                TestHarness.True(!ok, "a different selected id must not confirm the load");
            });

            TestHarness.Run("NEGATIVE: refuses when run.present is false even if the id matches", delegate
            {
                string evidence;
                bool ok = LoadRunVerification.Confirm("run-1", false, "run-1", out evidence);
                TestHarness.True(!ok, "run.present=false must never confirm a load");
            });

            TestHarness.Run("NEGATIVE: refuses when the selected id is null", delegate
            {
                string evidence;
                bool ok = LoadRunVerification.Confirm("run-1", true, null, out evidence);
                TestHarness.True(!ok, "a null selected id must not confirm the load");
            });
        }
    }
}
