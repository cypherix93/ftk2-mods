using System;
using System.Collections;
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
        private static readonly MethodInfo AdvanceHandler = typeof(ChaosCommands).GetMethod("CrucibleChaosAdvance", BindingFlags.Public | BindingFlags.Static);

        private static bool _freezeRegistered;
        private static bool _stateRegistered;
        private static bool _advanceRegistered;

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
            if (_freezeRegistered && _stateRegistered && _advanceRegistered) return;
            if (!_freezeRegistered) _freezeRegistered = GameBridge.RegisterCommand("crucible_chaos_freeze", FreezeHandler, new List<string> { "on|off" });
            if (!_stateRegistered) _stateRegistered = GameBridge.RegisterCommand("crucible_chaos_state", StateHandler, new List<string>());
            if (!_advanceRegistered) _advanceRegistered = GameBridge.RegisterCommand("crucible_chaos_advance", AdvanceHandler, new List<string> { "chaosConfigName" });
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

                // AdventureState.MapState.ChaosState, NOT GameRunData.ChaosState.
                //
                // GameRunData still carries a ChaosState field, but it is declared
                //     [Obsolete("Use AdventureState.MapState.ChaosState")]
                // and nothing writes it any more -- AdventureHelper.ModifyChaosLevel only ever
                // mutates the MapState copy. Reading the alias reported a chaos level that could
                // never move, whatever the game did. GameRunData.RoundCount is obsolete the same way.
                object mapState = RunAccess.GetMember(RunAccess.GetAdventureState(), "MapState");
                object chaosState = RunAccess.GetMember(mapState, "ChaosState");
                if (chaosState == null)
                {
                    LastResult = "frozen=" + _frozen + " patched=" + _patched
                        + " note=no active chaos (AdventureState.MapState.ChaosState is null)";
                    return;
                }

                object history = RunAccess.GetMember(chaosState, "ChaosHistory");
                int historyCount = RunAccess.CountOf(history);
                object maxChaos = RunAccess.GetMember(chaosState, "MaxChaos");
                object lastRound = RunAccess.GetMember(chaosState, "LastChaosRoundAdded");
                object startedAt = RunAccess.GetMember(chaosState, "StartedAtRound");
                object roundCount = RunAccess.GetMember(mapState, "RoundCount");

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

        // ============================================================== crucible_chaos_advance

        /// <summary>
        /// crucible_chaos_advance &lt;chaosConfigName&gt; -- advances the chaos STAGE (a whole
        /// ChaosConfig), by invoking AdventureDirector._processWorldTrigger(eWorldTriggers.CHAOS_ACTIVE,
        /// pArg) via reflection, matching the game's own CHAOS_ACTIVE world-trigger case:
        ///     _env.GameRun.AdventureState.MapState.ChaosState = ChaosState.Create(pArg, ...RoundCount);
        /// after reading Env.Configs.ChaosConfigs[pArg] (decompiled AdventureDirector.cs, CHAOS_ACTIVE
        /// case).
        ///
        /// An empty/whitespace arg is REFUSED before it ever reaches the director: the same case sets
        /// ChaosState to null on an empty pArg, and AdventureHelper.ModifyChaosLevel dereferences
        /// ChaosState.ChaosHistory with no null guard on the next chaos tick -- so the empty-arg path is
        /// a live NullReferenceException, not a harmless no-op.
        ///
        /// _processWorldTrigger is a protected async instance method with no public accessible
        /// signature; it is reachable only by reflection on the live AdventureDirector instance, the
        /// same posture RunCommands.TryPump uses for _tryCompleteQuests. The stage swap itself is the
        /// FIRST statement in the CHAOS_ACTIVE case, executed synchronously before any await point in
        /// the state machine, so it has already happened by the time Invoke returns -- but the Task it
        /// returns is deliberately not awaited (matches _tryCompleteQuests), so the caller should
        /// re-read crucible_chaos_state to confirm nothing downstream is still resolving.
        /// </summary>
        public static void CrucibleChaosAdvance(string pConfigName)
        {
            LastResult = null;
            try
            {
                string configName = (pConfigName ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(configName))
                {
                    LastResult = "error: usage: crucible_chaos_advance <chaosConfigName> -- REFUSED an "
                        + "empty/whitespace config name. AdventureDirector._processWorldTrigger(CHAOS_ACTIVE, \"\") "
                        + "sets AdventureState.MapState.ChaosState to null, and AdventureHelper.ModifyChaosLevel "
                        + "then dereferences ChaosState.ChaosHistory with no null guard on the next chaos tick -- "
                        + "that is a live NullReferenceException, not a harmless no-op. " + AvailableConfigsNote();
                    return;
                }

                object gameRun = RunAccess.GetGameRun();
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                object mapState = RunAccess.GetMember(RunAccess.GetAdventureState(), "MapState");
                if (mapState == null) { LastResult = "error: AdventureState.MapState is null -- no run loaded"; return; }

                object beforeState = RunAccess.GetMember(mapState, "ChaosState");
                string configNameBefore = Describe(RunAccess.GetMember(beforeState, "ConfigName"));
                int historyCountBefore = RunAccess.CountOf(RunAccess.GetMember(beforeState, "ChaosHistory"));

                object director = PartyAccess.Director();
                if (director == null) { LastResult = "error: AdventureDirector unavailable"; return; }

                MethodInfo trigger = FindProcessWorldTrigger(director.GetType());
                if (trigger == null)
                {
                    LastResult = "error: AdventureDirector._processWorldTrigger(eWorldTriggers, string, ...) not "
                        + "found (game update?); crucible_chaos_advance unavailable";
                    return;
                }

                ParameterInfo[] parameters = trigger.GetParameters();
                object triggerValue;
                try
                {
                    triggerValue = Enum.Parse(parameters[0].ParameterType, "CHAOS_ACTIVE");
                }
                catch (Exception ex)
                {
                    LastResult = "error: eWorldTriggers.CHAOS_ACTIVE could not be resolved: " + ex.Message;
                    return;
                }

                object[] args = new object[parameters.Length];
                args[0] = triggerValue;
                args[1] = configName;
                for (int i = 2; i < args.Length; i++) args[i] = null;

                object task;
                try
                {
                    task = trigger.Invoke(director, args);
                }
                catch (TargetInvocationException ex)
                {
                    Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                    LastResult = "error: _processWorldTrigger threw: " + root.GetType().Name + ": " + root.Message
                        + " (is '" + configName + "' a real key in Env.Configs.ChaosConfigs?) " + AvailableConfigsNote();
                    return;
                }

                object afterState = RunAccess.GetMember(mapState, "ChaosState");
                string configNameAfter = Describe(RunAccess.GetMember(afterState, "ConfigName"));
                int historyCountAfter = RunAccess.CountOf(RunAccess.GetMember(afterState, "ChaosHistory"));

                bool changed = !string.Equals(configNameBefore, configNameAfter, StringComparison.Ordinal)
                    || historyCountBefore != historyCountAfter;

                LastResult = "configArg=" + configName
                    + " configNameBefore=" + configNameBefore + " configNameAfter=" + configNameAfter
                    + " chaosHistoryCountBefore=" + historyCountBefore + " chaosHistoryCountAfter=" + historyCountAfter
                    + " changed=" + changed
                    + " taskState=" + (task == null ? "(null)" : "returned, not awaited")
                    + "\nNOTE: _processWorldTrigger's Task is deliberately not awaited (same posture as "
                    + "RunCommands._tryCompleteQuests). The ChaosState swap above runs synchronously before any "
                    + "await in that method, so configNameAfter/chaosHistoryCountAfter should already reflect it, "
                    + "but re-read crucible_chaos_state afterward if anything downstream (GenerateChaosTimelineEvents "
                    + "etc.) needs to be confirmed settled."
                    + "\n" + AvailableConfigsNote();
                if (_log != null) _log.LogInfo("crucible_chaos_advance: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_chaos_advance threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Locates AdventureDirector._processWorldTrigger(eWorldTriggers, string, ...) by name and
        /// leading-parameter shape rather than an exact overload match, since the trailing optional
        /// parameters (List, QuestState, Entity) are game-internal types Crucible has no compile-time
        /// reference to.
        /// </summary>
        private static MethodInfo FindProcessWorldTrigger(Type directorType)
        {
            foreach (MethodInfo m in directorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(m.Name, "_processWorldTrigger", StringComparison.Ordinal)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 2) continue;
                if (!string.Equals(ps[0].ParameterType.Name, "eWorldTriggers", StringComparison.Ordinal)) continue;
                if (ps[1].ParameterType != typeof(string)) continue;
                return m;
            }
            return null;
        }

        /// <summary>
        /// Lists the keys of Env.Configs.ChaosConfigs (SerializedSortedDictionary&lt;string, ChaosConfig&gt;
        /// in the decompiled Configs.cs), so a tester can discover valid crucible_chaos_advance arguments
        /// instead of guessing. Degrades to a note rather than guessing when the map cannot be reached.
        /// </summary>
        private static string AvailableConfigsNote()
        {
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) return "availableChaosConfigs=(Env unavailable)";

                object configs = RunAccess.GetMember(env, "Configs");
                if (configs == null) return "availableChaosConfigs=(Env.Configs is null)";

                object chaosConfigs = RunAccess.GetMember(configs, "ChaosConfigs");
                IDictionary dict = chaosConfigs as IDictionary;
                if (dict == null) return "availableChaosConfigs=(Env.Configs.ChaosConfigs not reachable as a dictionary)";

                List<string> keys = new List<string>();
                foreach (object k in dict.Keys) keys.Add(k == null ? "(null)" : k.ToString());
                keys.Sort(StringComparer.Ordinal);
                return "availableChaosConfigs(" + keys.Count + ")=[" + string.Join(", ", keys.ToArray()) + "]";
            }
            catch (Exception ex)
            {
                return "availableChaosConfigs=(error reading Env.Configs.ChaosConfigs: " + ex.Message + ")";
            }
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
