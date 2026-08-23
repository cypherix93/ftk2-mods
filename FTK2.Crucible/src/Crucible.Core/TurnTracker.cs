using System.Collections.Generic;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>One synthesized combat transition, destined for the JSONL trace.</summary>
    public sealed class TurnEvent
    {
        public string Kind;       // combat_start | combat_end | turn | phase
        public int Turn;
        public string Phase;
        public string EntityId;
        public string Episode;
    }

    /// <summary>
    /// Synthesizes <c>combat.turn</c> and <c>combat.phase</c>, neither of which exists as readable
    /// data anywhere in the assembly (field map: "turn and intra-combat phase are genuinely absent…
    /// it's driven procedurally through CombatPhase._engageActiveEntity / _nextTurn").
    ///
    /// The plugin feeds it from two Harmony postfixes and from each snapshot read; this class holds
    /// no game reference at all, so the whole state machine — including every reset path — is
    /// unit-testable with no game running.
    ///
    /// Contract:
    ///  - <c>turn</c> is an ordinal of observed advances within one episode, 0-based, -1 when idle.
    ///    It is the only monotonic axis in the combat block: <c>CombatState.TotalRounds</c> resets to
    ///    -1 per wave, so <c>round</c> is not.
    ///  - <c>phase</c> is PLAYER/ENEMY derived from the engaged entity, NONE when idle.
    /// </summary>
    public sealed class TurnTracker
    {
        public const string PhaseNone = "NONE";
        public const string PhasePlayer = "PLAYER";
        public const string PhaseEnemy = "ENEMY";
        public const string PhaseUnknown = "UNKNOWN";

        private readonly object _lock = new object();
        private readonly Queue<TurnEvent> _events = new Queue<TurnEvent>();
        private readonly int _maxEvents;

        private bool _active;
        private int _episodeKey;
        private int _turn = -1;
        private string _phase = PhaseNone;
        private string _entityId;
        private int _dropped;

        public TurnTracker(int maxEvents)
        {
            _maxEvents = maxEvents < 1 ? 1 : maxEvents;
        }

        /// <summary>
        /// False when the Harmony hooks never installed, so the reader can report
        /// <c>turn: null</c> / <c>phase: "UNKNOWN"</c> plus a warning instead of serving a
        /// permanently-zero counter as if it were a measurement.
        /// </summary>
        public bool HooksInstalled;

        public int DroppedEvents
        {
            get { lock (_lock) { return _dropped; } }
        }

        /// <summary>Called once per snapshot with what the reader can see right now.</summary>
        public void OnObserved(bool combatActive, int episodeKey)
        {
            lock (_lock)
            {
                if (!combatActive)
                {
                    if (_active) EndLocked();
                    return;
                }
                if (!_active || episodeKey != _episodeKey) StartLocked(episodeKey);
            }
        }

        /// <summary>Postfix on <c>CombatPhase._nextTurn</c>.</summary>
        public void OnTurnAdvanced()
        {
            lock (_lock)
            {
                if (!_active) StartLocked(_episodeKey);   // hook fired before any snapshot: turn 0
                else _turn++;
                EmitLocked("turn");
            }
        }

        /// <summary>Postfix on <c>CombatPhase._engageActiveEntity</c>.</summary>
        public void OnEntityEngaged(string entityId, bool isPlayer)
        {
            lock (_lock)
            {
                if (!_active) StartLocked(_episodeKey);
                string phase = isPlayer ? PhasePlayer : PhaseEnemy;
                bool changed = !string.Equals(phase, _phase) || !string.Equals(entityId, _entityId);
                _phase = phase;
                _entityId = entityId;
                if (changed) EmitLocked("phase");
            }
        }

        public void Read(out bool active, out int turn, out string phase, out string entityId, out string episode)
        {
            lock (_lock)
            {
                active = _active;
                turn = _turn;
                phase = _phase;
                entityId = _entityId;
                episode = EpisodeLocked();
            }
        }

        public TurnEvent[] DrainEvents()
        {
            lock (_lock)
            {
                TurnEvent[] all = _events.ToArray();
                _events.Clear();
                return all;
            }
        }

        private void StartLocked(int episodeKey)
        {
            _active = true;
            _episodeKey = episodeKey;
            _turn = 0;
            _phase = PhaseNone;
            _entityId = null;
            EmitLocked("combat_start");
        }

        private void EndLocked()
        {
            _active = false;
            _turn = -1;
            _phase = PhaseNone;
            _entityId = null;
            EmitLocked("combat_end");
        }

        private string EpisodeLocked()
        {
            return _episodeKey.ToString("x8", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Bounded queue: an undrained tracker must not grow without limit. Drops are counted rather
        /// than swallowed — silent truncation is forbidden (SPEC §9).
        /// </summary>
        private void EmitLocked(string kind)
        {
            TurnEvent e = new TurnEvent();
            e.Kind = kind;
            e.Turn = _turn;
            e.Phase = _phase;
            e.EntityId = _entityId;
            e.Episode = EpisodeLocked();
            _events.Enqueue(e);
            while (_events.Count > _maxEvents) { _events.Dequeue(); _dropped++; }
        }
    }
}
