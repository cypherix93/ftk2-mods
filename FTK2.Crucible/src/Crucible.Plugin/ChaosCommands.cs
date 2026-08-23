using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// crucible_chaos_freeze / crucible_chaos_state -- a no-chaos mode for long automated soaks.
    ///
    /// The problem: the cheapest way to advance a run for testing is to spam end-turn, but every
    /// turn raises Chaos, which escalates enemy difficulty and eventually ends the run. A soak that
    /// fast-forwards through turns therefore poisons its own test conditions -- the run gets harder
    /// purely because the harness is fast, and results stop being comparable between runs.
    ///
    /// Live probe 2026-08-23 (TypeProbe against GameRunData / AdventureState / ChaosState /
    /// AdventureHelper / AdventureDirector on the retail assembly):
    /// - GameRunData.ChaosState (field, type ChaosState) is the live home.
    /// - ChaosState carries NO scalar "current chaos level" -- only ChaosHistory (List`1),
    ///   MaxChaos (Int32), LastChaosRoundAdded (Int32), StartedAtRound (Int32), ConfigName (String),
    ///   MeterShuffleBag/ShuffleBag1/ShuffleBag2. No ChaosHelper type exists.
    /// - AdventureDirector._triggerChaos(Boolean, Int32, String, Boolean) is the CALLER (already
    ///   named in crucible-traversal-inventory.md), but AdventureHelper.ModifyChaosLevel (two
    ///   overloads, Void, confirmed present by this probe) is the actual MUTATOR -- the leaf that
    ///   changes state, not the trigger that decides to call it.
    ///
    /// Chosen implementation: (2) suppress the increment, via a Harmony Prefix on every
    /// ModifyChaosLevel overload that returns false (skip original) while frozen. Preferred over
    /// (1) re-writing a baseline every tick, per the request, because the incrementing method was
    /// identified with confidence -- by name ("ModifyChaosLevel") and by a live method-table probe,
    /// not guessed -- and patching the leaf mutator is more surgical than fighting the game every
    /// frame. If AdventureHelper.ModifyChaosLevel is ever renamed/removed by a game update, TryPatch
    /// degrades to "chaos freeze unavailable" (reported by crucible_chaos_freeze), never a silent
    /// no-op masquerading as a working freeze.
    ///
    /// Because no scalar level exists, crucible_chaos_state reports ChaosHistory.Count as the best
    /// available observable (each applied chaos increase is presumed to append an entry) alongside
    /// MaxChaos/LastChaosRoundAdded/StartedAtRound for context. Live verification protocol for the
    /// caller: call crucible_chaos_state, turn freeze on, drive several turns (crucible_time_advance
    /// or repeated EndPhase), call crucible_chaos_state again -- chaosHistoryCount must be unchanged
    /// while frozen, and observed to move in an unfrozen control run. The offline negative control
    /// for the GATING LOGIC itself (not the live game, which this bridge cannot exercise from this
    /// dev environment) is FTK2Mods.Crucible.Tests.DebugVerbArgsTests "ChaosGate": a simulated
    /// counter held at a fixed value across three iterations while frozen, and proven to move across
    /// the same three iterations when not frozen -- so the "false" return value that skips the
    /// original really does prevent a would-otherwise-happen mutation, not merely exist unused.
    /// </summary>
    internal static class ChaosCommands
    {
        private static ManualLogSource _log;
        private static bool _frozen;
        private static bool _patched;

        internal static string LastResult;

        private static readonly MethodInfo FreezeHandler = typeof(ChaosCommands).GetMethod("CrucibleChaosFreeze", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo StateHandler = typeof(ChaosCommands).GetMethod("CrucibleChaosState", BindingFlags.Public | BindingFlags.Static);

        private static bool _freezeRegistered;
        private static bool _stateRegistered;

        internal static void Initialize(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            TryPatch(harmony);
        }

        private static void TryPatch(Harmony harmony)
        {
            try
            {
                Type helper = AccessTools.TypeByName("AdventureHelper");
                if (helper == null)
                {
                    if (_log != null) _log.LogWarning("ChaosCommands: AdventureHelper not found; chaos freeze unavailable.");
                    return;
                }

                MethodInfo prefix = typeof(ChaosCommands).GetMethod("ModifyChaosLevelPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo[] all = helper.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                int patched = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    if (!string.Equals(all[i].Name, "ModifyChaosLevel", StringComparison.Ordinal)) continue;
                    harmony.Patch(all[i], new HarmonyMethod(prefix));
                    patched++;
                }

                _patched = patched > 0;
                if (_log != null)
                {
                    _log.LogInfo(_patched
                        ? "ChaosCommands: patched " + patched + " AdventureHelper.ModifyChaosLevel overload(s)."
                        : "ChaosCommands: AdventureHelper.ModifyChaosLevel not found; chaos freeze unavailable.");
                }
            }
            catch (Exception ex)
            {
                if (_log != null) _log.LogWarning("ChaosCommands: patch failed: " + ex.Message);
            }
        }

        /// <summary>Shared across every ModifyChaosLevel overload -- Harmony matches a prefix by
        /// name only for the target parameters it actually declares, so a parameterless prefix
        /// applies uniformly regardless of which overload it is patched onto.</summary>
        private static bool ModifyChaosLevelPrefix()
        {
            return !ChaosGate.ShouldSkipOriginal(_frozen);
        }

        internal static void TryRegister()
        {
            if (_freezeRegistered && _stateRegistered) return;
            if (!_freezeRegistered) _freezeRegistered = GameBridge.RegisterCommand("crucible_chaos_freeze", FreezeHandler, new List<string> { "on|off" });
            if (!_stateRegistered) _stateRegistered = GameBridge.RegisterCommand("crucible_chaos_state", StateHandler, new List<string>());
        }

        public static void CrucibleChaosFreeze(string pOnOff)
        {
            LastResult = null;
            try
            {
                if (!_patched)
                {
                    LastResult = "error: AdventureHelper.ModifyChaosLevel could not be patched (game update?); chaos freeze unavailable";
                    return;
                }

                bool on; string error;
                if (!BoolToggleArg.TryParse(pOnOff, out on, out error)) { LastResult = "error: " + error; return; }

                bool before = _frozen;
                _frozen = on;

                int historyCount = RunAccess.CountOf(ReadChaosHistory());

                LastResult = "api=Harmony Prefix on AdventureHelper.ModifyChaosLevel (skips the original call while frozen) "
                    + "frozenBefore=" + before + " frozenAfter=" + _frozen + " changed=" + (before != _frozen)
                    + " chaosHistoryCountNow=" + historyCount
                    + " -- to verify live: call crucible_chaos_state, drive several turns while frozen, "
                    + "call crucible_chaos_state again -- chaosHistoryCount must not move.";
            }
            catch (Exception ex) { LastResult = "error: crucible_chaos_freeze threw: " + ex.Message; }
        }

        public static void CrucibleChaosState()
        {
            LastResult = null;
            try
            {
                object gameRun = RunAccess.GetGameRun();
                object chaosState = RunAccess.GetMember(gameRun, "ChaosState");
                if (chaosState == null)
                {
                    LastResult = "frozen=" + _frozen + " patched=" + _patched + " note=no active run (GameRunData.ChaosState is null)";
                    return;
                }

                object history = RunAccess.GetMember(chaosState, "ChaosHistory");
                int historyCount = RunAccess.CountOf(history);
                object maxChaos = RunAccess.GetMember(chaosState, "MaxChaos");
                object lastRound = RunAccess.GetMember(chaosState, "LastChaosRoundAdded");
                object startedAt = RunAccess.GetMember(chaosState, "StartedAtRound");
                object roundCount = RunAccess.GetMember(gameRun, "RoundCount");

                LastResult = "frozen=" + _frozen + " patched=" + _patched
                    + " chaosHistoryCount=" + historyCount
                    + " maxChaos=" + Describe(maxChaos)
                    + " lastChaosRoundAdded=" + Describe(lastRound)
                    + " startedAtRound=" + Describe(startedAt)
                    + " roundCount=" + Describe(roundCount)
                    + " -- no scalar 'current chaos level' field exists on ChaosState; chaosHistoryCount "
                    + "is the best available proxy (each applied increase is presumed to append an entry).";
            }
            catch (Exception ex) { LastResult = "error: crucible_chaos_state threw: " + ex.Message; }
        }

        private static object ReadChaosHistory()
        {
            object gameRun = RunAccess.GetGameRun();
            object chaosState = RunAccess.GetMember(gameRun, "ChaosState");
            return RunAccess.GetMember(chaosState, "ChaosHistory");
        }

        private static string Describe(object value)
        {
            return value == null ? "?" : value.ToString();
        }
    }
}
