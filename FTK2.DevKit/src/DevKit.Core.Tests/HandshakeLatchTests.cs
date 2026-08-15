using System;

namespace FTK2Mods.DevKit.Tests
{
    /// <summary>
    /// <c>HandshakeRetryLatch</c> — task #12. The party-phase kickoff can race the game's
    /// <c>PlayingOnlineMultiplayer</c> flag (host log 2026-08-15: flag still false at
    /// <c>PartyManagementDirector.Initialize</c>, so the handshake silently skipped and no verdict
    /// ever arrived). The latch is the pure state machine behind the fix: the kickoff runs when the
    /// flag is up, and otherwise stays owed so the first received lobby traffic retries it — at most
    /// one SUCCESSFUL handshake per session, failed sends retryable.
    /// </summary>
    internal static class HandshakeLatchTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("handshake retry latch (task #12)");

            TestHarness.Run("online at session start: attempt allowed, success completes the latch", delegate
            {
                HandshakeRetryLatch latch = new HandshakeRetryLatch();
                latch.OnSessionStart();
                TestHarness.True(latch.ShouldAttempt(true), "fresh latch + online flag => attempt");
                latch.OnAttemptResult(true);
                TestHarness.True(latch.Completed, "successful send completes");
                TestHarness.True(!latch.ShouldAttempt(true), "completed latch never re-attempts this session");
            });

            TestHarness.Run("offline at session start: attempt withheld until the flag flips (the race fix)", delegate
            {
                HandshakeRetryLatch latch = new HandshakeRetryLatch();
                latch.OnSessionStart();
                TestHarness.True(!latch.ShouldAttempt(false), "flag down => no attempt, nothing consumed");
                TestHarness.True(!latch.Completed, "a withheld attempt is not completion");
                TestHarness.True(latch.ShouldAttempt(true), "flag up later (first lobby traffic) => attempt");
            });

            TestHarness.Run("failed send leaves the handshake owed; success then stops retries", delegate
            {
                HandshakeRetryLatch latch = new HandshakeRetryLatch();
                latch.OnSessionStart();
                TestHarness.True(latch.ShouldAttempt(true), "first attempt");
                latch.OnAttemptResult(false);
                TestHarness.True(!latch.Completed, "failed send is not completion");
                TestHarness.True(latch.ShouldAttempt(true), "failed send => retry allowed");
                latch.OnAttemptResult(true);
                TestHarness.True(!latch.ShouldAttempt(true), "success ends the retries");
            });

            TestHarness.Run("a new session re-arms a completed latch (once per SESSION, not per process)", delegate
            {
                HandshakeRetryLatch latch = new HandshakeRetryLatch();
                latch.OnSessionStart();
                latch.OnAttemptResult(true);
                TestHarness.True(latch.Completed, "session 1 complete");
                latch.OnSessionStart();
                TestHarness.True(!latch.Completed, "session 2 owes a fresh handshake");
                TestHarness.True(latch.ShouldAttempt(true), "and may attempt it");
            });
        }
    }
}
