namespace FTK2Mods.DevKit
{
    /// <summary>
    /// Per-session handshake state machine (task #12). The parity kickoff anchors on director
    /// initialization, but the game's <c>NetworkData.PlayingOnlineMultiplayer</c> flag can flip true
    /// only later in the lobby flow — so a kickoff that fires too early must stay OWED, and the first
    /// received lobby traffic retries it. Semantics:
    ///
    ///  - <see cref="OnSessionStart"/> re-arms the latch (a new session owes a fresh handshake);
    ///  - <see cref="ShouldAttempt"/> is true only while the handshake is owed AND the caller has
    ///    observed the online flag up — a false flag consumes nothing;
    ///  - <see cref="OnAttemptResult"/> completes the latch only on a SUCCESSFUL snapshot send;
    ///    a failed send stays owed so later traffic may retry (the transport's own per-session
    ///    failure cap bounds how often a broken sender is actually exercised).
    ///
    /// Instance class with no statics, mirroring the rest of DevKit.Core (SPEC-DELTA §6 posture);
    /// the Plugin owns the single coordinator-scoped instance. Not thread-safe by design: every
    /// caller is a Harmony patch body on Unity's main thread.
    /// </summary>
    public sealed class HandshakeRetryLatch
    {
        private bool _completed;

        public void OnSessionStart()
        {
            _completed = false;
        }

        public bool ShouldAttempt(bool isOnlineMultiplayer)
        {
            return !_completed && isOnlineMultiplayer;
        }

        public void OnAttemptResult(bool snapshotSent)
        {
            if (snapshotSent) _completed = true;
        }

        public bool Completed
        {
            get { return _completed; }
        }
    }
}
