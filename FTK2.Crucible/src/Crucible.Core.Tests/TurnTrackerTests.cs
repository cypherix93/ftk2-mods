namespace FTK2Mods.Crucible.Tests
{
    internal static class TurnTrackerTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("TurnTracker — counting");

            TestHarness.Run("turn starts at 0 when combat is first observed", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                TestHarness.Equal(0, Turn(t), "first turn is 0");
                TestHarness.True(Active(t), "active");
            });

            TestHarness.Run("each advance increments the turn", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                TestHarness.Equal(3, Turn(t), "three advances");
            });

            TestHarness.Run("turn resets when the episode key changes", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                t.OnObserved(true, 222);
                TestHarness.Equal(0, Turn(t), "new fight, new count");
            });

            // Negative control: the SAME episode key must NOT reset the count.
            TestHarness.Run("re-observing the same episode does not reset the turn", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                t.OnObserved(true, 111);
                t.OnObserved(true, 111);
                TestHarness.Equal(2, Turn(t), "count survives repeated observation");
            });

            TestHarness.Run("combat end clears turn and phase", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnEntityEngaged("e-1", true);
                t.OnTurnAdvanced();
                t.OnObserved(false, 0);
                TestHarness.False(Active(t), "inactive");
                TestHarness.Equal(-1, Turn(t), "turn cleared");
                TestHarness.Equal(TurnTracker.PhaseNone, Phase(t), "phase cleared");
            });

            TestHarness.Run("a fresh episode after an end starts at 0 again", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnObserved(false, 0);
                t.OnObserved(true, 111);
                TestHarness.Equal(0, Turn(t), "restarted");
            });

            TestHarness.Run("an advance before any observation implies a combat start", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnTurnAdvanced();
                TestHarness.True(Active(t), "implicit start");
                TestHarness.Equal(0, Turn(t), "counted as turn 0");
            });

            TestHarness.Section("TurnTracker — phase");

            TestHarness.Run("phase follows the engaged entity", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnEntityEngaged("e-1", true);
                TestHarness.Equal(TurnTracker.PhasePlayer, Phase(t), "player turn");
                t.OnEntityEngaged("e-2", false);
                TestHarness.Equal(TurnTracker.PhaseEnemy, Phase(t), "enemy turn");
                TestHarness.Equal("e-2", EntityId(t), "active entity recorded");
            });

            TestHarness.Section("TurnTracker — events");

            TestHarness.Run("transitions are emitted in order and drained once", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnEntityEngaged("e-1", true);
                t.OnTurnAdvanced();
                t.OnObserved(false, 0);

                TurnEvent[] events = t.DrainEvents();
                TestHarness.Equal(4, events.Length, "four transitions");
                TestHarness.Equal("combat_start", events[0].Kind, "0");
                TestHarness.Equal("phase", events[1].Kind, "1");
                TestHarness.Equal("turn", events[2].Kind, "2");
                TestHarness.Equal("combat_end", events[3].Kind, "3");
                TestHarness.Equal(0, t.DrainEvents().Length, "drain empties the queue");
            });

            TestHarness.Run("re-engaging the same entity emits no duplicate phase event", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.DrainEvents();
                t.OnEntityEngaged("e-1", true);
                t.OnEntityEngaged("e-1", true);
                TestHarness.Equal(1, t.DrainEvents().Length, "one phase event");
            });

            // Negative control: a genuine change must still emit.
            TestHarness.Run("engaging a different entity does emit a phase event", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.DrainEvents();
                t.OnEntityEngaged("e-1", true);
                t.OnEntityEngaged("e-2", true);
                TestHarness.Equal(2, t.DrainEvents().Length, "two phase events");
            });

            TestHarness.Run("an overfull queue drops the oldest and counts the drops", delegate
            {
                TurnTracker t = new TurnTracker(2);
                t.OnObserved(true, 111);          // combat_start
                t.OnTurnAdvanced();               // turn 1
                t.OnTurnAdvanced();               // turn 2  -> drops combat_start
                TurnEvent[] events = t.DrainEvents();
                TestHarness.Equal(2, events.Length, "capped at 2");
                TestHarness.Equal(1, events[0].Turn, "oldest dropped");
                TestHarness.Equal(1, t.DroppedEvents, "drop counted, not silent");
            });

            // Negative control: under capacity nothing may be reported as dropped.
            TestHarness.Run("nothing is dropped under capacity", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                TestHarness.Equal(0, t.DroppedEvents, "no drops");
            });
        }

        private static bool Active(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return active;
        }

        private static int Turn(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return turn;
        }

        private static string Phase(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return phase;
        }

        private static string EntityId(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return id;
        }
    }
}
