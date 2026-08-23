using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Synthesizes <c>combat.turn</c> and <c>combat.phase</c>, which the engine holds as control
    /// flow rather than data (field map: no turn counter and no intra-combat phase enum exist
    /// anywhere in the assembly — combat runs procedurally through
    /// <c>CombatPhase._engageActiveEntity</c> / <c>_nextTurn</c>).
    ///
    /// Also the source of the trace's combat transitions. Before this, the trace only ever recorded
    /// <c>exec</c>: what an agent asked for, never what the fight did.
    ///
    /// Degrades loudly: if either target cannot be patched, <c>HooksInstalled</c> stays false and
    /// the snapshot reports <c>turn: null</c> / <c>phase: "UNKNOWN"</c> plus a warning, rather than
    /// serving a counter stuck at zero as if it were a measurement.
    /// </summary>
    internal static class TurnHooks
    {
        internal static readonly TurnTracker Tracker = new TurnTracker(256);
        private static ManualLogSource _log;

        internal static void Initialize(Harmony harmony, ManualLogSource log)
        {
            _log = log;

            Type combatPhase = AccessTools.TypeByName("CombatPhase");
            if (combatPhase == null)
            {
                log.LogWarning("Target NOT found: CombatPhase — combat.turn/phase disabled.");
                DevKitBridge.ReportTarget("CombatPhase", null);
                return;
            }

            MethodInfo nextTurn = AccessTools.Method(combatPhase, "_nextTurn");
            MethodInfo engage = AccessTools.Method(combatPhase, "_engageActiveEntity");
            Report("CombatPhase._nextTurn", nextTurn);
            Report("CombatPhase._engageActiveEntity", engage);

            try
            {
                if (nextTurn != null)
                {
                    harmony.Patch(nextTurn, null,
                        new HarmonyMethod(AccessTools.Method(typeof(TurnHooks), "NextTurnPostfix")));
                }
                if (engage != null)
                {
                    harmony.Patch(engage, null,
                        new HarmonyMethod(AccessTools.Method(typeof(TurnHooks), "EngagePostfix")));
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("TurnHooks patching failed: " + ex.Message + " — combat.turn/phase disabled.");
                return;
            }

            Tracker.HooksInstalled = nextTurn != null && engage != null;
            log.LogInfo("TurnHooks installed: " + (Tracker.HooksInstalled ? "yes" : "partial — turn/phase disabled"));
        }

        private static void Report(string description, MethodBase resolved)
        {
            if (resolved != null) _log.LogInfo("Target found: " + description);
            else _log.LogWarning("Target NOT found: " + description + " (feature disabled)");
            DevKitBridge.ReportTarget(description, resolved);
        }

        /// <summary>
        /// One turn advance. <c>_nextTurn</c> returns <c>Task</c> (Task 1 addendum), so this fires
        /// when the state machine is created rather than when the turn completes — still exactly
        /// once per advance, in call order, which is all an ordinal needs. Task 9 Step 4 measures
        /// that rather than assuming it.
        /// </summary>
        internal static void NextTurnPostfix()
        {
            try
            {
                Tracker.OnTurnAdvanced();
                FlushEvents();
            }
            catch (Exception ex)
            {
                Warn("NextTurnPostfix", ex);
            }
        }

        /// <summary>
        /// A combatant takes the floor. <c>object __instance</c> rather than the real type: this
        /// plugin holds no compile-time game types (SPEC §3), so the CombatPhase instance arrives
        /// boxed and its members are read reflectively.
        /// </summary>
        internal static void EngagePostfix(object __instance)
        {
            try
            {
                object entity = MemberResolver.GetMember(__instance, "_activeCharacterEntity", null);
                if (entity == null) entity = MemberResolver.GetMember(__instance, "_lastEngagedEntity", null);

                string id = MemberResolver.AsString(MemberResolver.GetMember(entity, "Guid", null));
                object components = MemberResolver.GetMember(entity, "Components", null);
                bool isPlayer = MemberResolver.FindComponentByTypeName(components, "PlayerComponent", null) != null;

                Tracker.OnEntityEngaged(id, isPlayer);
                FlushEvents();
            }
            catch (Exception ex)
            {
                Warn("EngagePostfix", ex);
            }
        }

        /// <summary>
        /// Writes queued transitions to the JSONL trace. Drains unconditionally — if tracing is off
        /// the events are discarded here rather than accumulating in a queue nobody reads.
        /// </summary>
        internal static void FlushEvents()
        {
            TurnEvent[] events = Tracker.DrainEvents();
            if (events.Length == 0) return;
            if (CruciblePlugin.Trace == null) return;

            string instance = CruciblePlugin.Instance != null ? CruciblePlugin.Instance.InstanceName : null;
            int dropped = Tracker.DroppedEvents;

            for (int i = 0; i < events.Length; i++)
            {
                TurnEvent e = events[i];
                Dictionary<string, object> fields = new Dictionary<string, object>();
                fields["turn"] = e.Turn;
                fields["phase"] = e.Phase;
                fields["entityId"] = e.EntityId;
                fields["episode"] = e.Episode;
                fields["instance"] = instance;
                // Reported, never swallowed: silent truncation is forbidden (SPEC §9).
                if (dropped > 0) fields["droppedEvents"] = dropped;
                CruciblePlugin.Trace.Write(e.Kind, "combat-" + e.Episode, fields);
            }
        }

        private static void Warn(string where, Exception ex)
        {
            if (_log != null) _log.LogWarning("TurnHooks." + where + " threw: " + ex.Message);
        }
    }
}
