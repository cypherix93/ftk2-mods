using System.Collections.Generic;

namespace FTK2Mods.DevKit.Tests
{
    /// <summary>
    /// Tests for <see cref="DesyncWatch"/> — the vendor-detector watcher.
    ///
    /// The interesting behaviour is channel attribution (which of the game's two independent desync
    /// checks fired) and the latch (the vendor flag stays true once set, so a naive poller would
    /// re-report forever).
    /// </summary>
    internal static class DesyncWatchTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("Desync watch — activity gating");

            TestHarness.Run("offline session is Inactive, even with the flag set", delegate
            {
                DesyncWatch.Reset();
                DesyncSnapshot s = Online();
                s.PlayingOnlineMultiplayer = false;
                s.HasADesyncBeenDetected = true;
                TestHarness.True(DesyncWatch.Observe(s) == DesyncVerdict.Inactive, "offline -> Inactive");
            });

            TestHarness.Run("monitoring disabled is Inactive", delegate
            {
                DesyncWatch.Reset();
                DesyncSnapshot s = Online();
                s.DoMonitorForDesyncs = false;
                s.HasADesyncBeenDetected = true;
                TestHarness.True(DesyncWatch.Observe(s) == DesyncVerdict.Inactive, "not monitoring -> Inactive");
            });

            TestHarness.Run("online and monitoring with no desync is Clean", delegate
            {
                DesyncWatch.Reset();
                TestHarness.True(DesyncWatch.Observe(Online()) == DesyncVerdict.Clean, "clean poll");
            });

            TestHarness.Section("Desync watch — the latch");

            TestHarness.Run("first trip reports, every later poll stays quiet", delegate
            {
                DesyncWatch.Reset();
                DesyncSnapshot s = Tripped(hashIndex: 7, randomIndex: -1);

                TestHarness.True(DesyncWatch.Observe(s) == DesyncVerdict.TrippedNow, "first poll -> TrippedNow");
                TestHarness.True(DesyncWatch.Observe(s) == DesyncVerdict.AlreadyReported, "second poll");
                TestHarness.True(DesyncWatch.Observe(s) == DesyncVerdict.AlreadyReported, "third poll");
                TestHarness.True(DesyncWatch.HasReported, "HasReported latched");
            });

            TestHarness.Run("Reset re-arms for a new session", delegate
            {
                DesyncWatch.Reset();
                DesyncSnapshot s = Tripped(hashIndex: 3, randomIndex: -1);
                DesyncWatch.Observe(s);
                DesyncWatch.Reset();
                TestHarness.False(DesyncWatch.HasReported, "latch cleared");
                TestHarness.True(DesyncWatch.Observe(s) == DesyncVerdict.TrippedNow, "reports again after Reset");
            });

            TestHarness.Section("Desync watch — channel attribution");

            TestHarness.Run("state-hash evidence only -> Hash", delegate
            {
                TestHarness.True(DesyncWatch.Classify(Tripped(hashIndex: 4, randomIndex: -1)) == DesyncChannel.Hash, "Hash");
            });

            TestHarness.Run("GameRandom evidence only -> GameRandom", delegate
            {
                TestHarness.True(DesyncWatch.Classify(Tripped(hashIndex: -1, randomIndex: 9)) == DesyncChannel.GameRandom, "GameRandom");
            });

            TestHarness.Run("both channels carry evidence -> Both", delegate
            {
                TestHarness.True(DesyncWatch.Classify(Tripped(hashIndex: 2, randomIndex: 5)) == DesyncChannel.Both, "Both");
            });

            TestHarness.Run("an index with no matching dictionary entry is not evidence", delegate
            {
                DesyncSnapshot s = Online();
                s.HasADesyncBeenDetected = true;
                s.LatestDesyncHashIndex = 11;          // index set...
                s.HashDataIndices = new List<int>();   // ...but nothing recorded under it
                TestHarness.True(DesyncWatch.Classify(s) == DesyncChannel.Unknown, "bare index is not evidence");
            });

            TestHarness.Run("a populated dictionary without a matching index is not this trip's evidence", delegate
            {
                DesyncSnapshot s = Online();
                s.HasADesyncBeenDetected = true;
                s.LatestGameRandomDesyncIndex = -1;
                s.GameRandomDataIndices = new List<int> { 1, 2, 3 };
                TestHarness.True(DesyncWatch.Classify(s) == DesyncChannel.Unknown, "stale dictionary ignored");
            });

            TestHarness.Section("Desync watch — cause line");

            TestHarness.Run("a SafeMode mod outranks channel guesswork", delegate
            {
                string cause = DesyncWatch.BuildCause(
                    DesyncChannel.GameRandom,
                    new[] { "ftk2mods.classforge" },
                    sessionBlocked: false);
                TestHarness.True(cause.Contains("ftk2mods.classforge"), "names the mod");
                TestHarness.True(cause.Contains("SafeMode"), "says SafeMode");
            });

            TestHarness.Run("a blocked session is called out even with no SafeMode guids", delegate
            {
                string cause = DesyncWatch.BuildCause(DesyncChannel.Hash, new string[0], sessionBlocked: true);
                TestHarness.True(cause.Contains("session blocked"), "says blocked");
            });

            TestHarness.Run("with parity clean, GameRandom blames code, not config", delegate
            {
                string cause = DesyncWatch.BuildCause(DesyncChannel.GameRandom, new string[0], sessionBlocked: false);
                TestHarness.True(cause.Contains("RNG draw"), "mentions the draw");
                TestHarness.True(cause.Contains("not a config difference"), "rules out config");
            });

            TestHarness.Run("with parity clean, Hash blames applied state", delegate
            {
                string cause = DesyncWatch.BuildCause(DesyncChannel.Hash, new string[0], sessionBlocked: false);
                TestHarness.True(cause.Contains("one peer only"), "mentions one-sided application");
            });

            TestHarness.Run("Unknown asks for the vendor dump instead of guessing", delegate
            {
                string cause = DesyncWatch.BuildCause(DesyncChannel.Unknown, new string[0], sessionBlocked: false);
                TestHarness.True(cause.Contains("NetworkDesyncReport"), "points at the vendor folder");
            });

            TestHarness.Section("Desync watch — report body");

            TestHarness.Run("report carries channel, cause, host role and registrations", delegate
            {
                DesyncSnapshot s = Tripped(hashIndex: -1, randomIndex: 6);
                s.IsHost = true;

                string report = DesyncWatch.BuildReport(
                    s,
                    DesyncChannel.GameRandom,
                    "peer-abc",
                    new[] { "ftk2mods.devkit", "ftk2mods.classforge" },
                    "ftk2mods.classforge v0.1.0 data=sha256:beef",
                    new string[0],
                    sessionBlocked: false,
                    lastMismatchSummary: null,
                    timestampUtc: "2026-08-15T18:00:00Z");

                TestHarness.True(report.Contains("GameRandom"), "channel present");
                TestHarness.True(report.Contains("WHY:"), "cause present");
                TestHarness.True(report.Contains("[HOST]"), "host role present");
                TestHarness.True(report.Contains("peer-abc"), "peer id present");
                TestHarness.True(report.Contains("sha256:beef"), "registration dump present");
                TestHarness.True(report.Contains("LatestGameRandomDesyncIndex: 6"), "vendor index present");
            });

            TestHarness.Run("report survives a missing snapshot", delegate
            {
                string report = DesyncWatch.BuildReport(
                    null, DesyncChannel.Unknown, null, null, null, null,
                    sessionBlocked: false, lastMismatchSummary: null, timestampUtc: null);
                TestHarness.True(report.Contains("(snapshot unavailable)"), "degrades cleanly");
                TestHarness.True(report.Contains("(unidentified)"), "peer id degrades cleanly");
            });
        }

        /// <summary>An online, monitored, healthy session.</summary>
        private static DesyncSnapshot Online()
        {
            return new DesyncSnapshot
            {
                PlayingOnlineMultiplayer = true,
                DoMonitorForDesyncs = true,
                HasADesyncBeenDetected = false
            };
        }

        /// <summary>A tripped session carrying evidence on the requested channel(s); pass -1 to omit one.</summary>
        private static DesyncSnapshot Tripped(int hashIndex, int randomIndex)
        {
            DesyncSnapshot s = Online();
            s.HasADesyncBeenDetected = true;
            s.LatestDesyncHashIndex = hashIndex;
            s.LatestGameRandomDesyncIndex = randomIndex;
            if (hashIndex >= 0) s.HashDataIndices = new List<int> { hashIndex };
            if (randomIndex >= 0) s.GameRandomDataIndices = new List<int> { randomIndex };
            return s;
        }
    }
}
