using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// The commands a full adventure needs at its EDGES: how a run ends, how to get out of an
    /// encounter that will not close itself, and how to level the party without desyncing it.
    ///
    /// Each is grounded in a decompilation of the retail assembly:
    ///
    /// 1. <b>An adventure's outcome is not observable from the route.</b>
    ///    <c>GameplayDirectorBase._endAdventure(bool pIsVictory)</c> routes a LOSS to
    ///    ADVENTURE_SELECTION and a WIN to <c>RouterHelper.GetEndOfAdventureRoute</c>, which is
    ///    usually the same screen. A Harmony prefix latching <c>pIsVictory</c> is the only
    ///    trustworthy signal, and it is also the last moment at which the save still exists.
    /// 2. <b>A victorious run destroys its own save.</b> <c>_endAdventure</c> ends with
    ///    <c>if (summaryTask.Result &amp;&amp; canLoadSave &amp;&amp; pIsVictory) _removeSave();</c>
    ///    -- so on a normal-difficulty win the fixture is gone by the time anything can read it.
    ///    The prefix snapshots first. (MASTER/GAUNTLET/DARK_CARNIVAL delete it even earlier, via
    ///    the <c>!canLoadSave</c> branch at the top of the method.)
    /// 3. <b>The summary screen blocks on a Task.</b> <c>_endAdventure</c> awaits
    ///    <c>AdventureSummaryViewHelper.ShowAdventureSummary(...)</c>; until that completes the run
    ///    never finishes unwinding. Dismissing the summary is what completes it.
    /// 4. <b>Market / town-services / quest-board branches never call
    ///    <c>_closeEncounterMenuAsync</c></b>, so an encounter opened through them stays open
    ///    forever. <c>_stopEncounterAsync</c> is the universal exit, and it re-pumps quest
    ///    completion on the way out.
    /// 5. <b>Level is derived from an XP Thing.</b> Writing that stack by hand skips the heal, the
    ///    focus grant and the LEVELED_UP event, which desyncs HP exactly the way raw class swaps
    ///    did. <c>ProgressionHelper.EntitiesGainXP</c> is public static and does all three.
    /// </summary>
    internal static class RunEndCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static readonly Dictionary<string, bool> Registered = new Dictionary<string, bool>();

        /// <summary>Latched outcome of the most recent _endAdventure, or null if none has run.</summary>
        private static bool? _endedVictory;
        private static string _endedDetail;
        private static string _endedSnapshotRunId;
        private static bool _autoSnapshot = true;
        private static bool _watchInstalled;

        /// <summary>How many camera-reset exceptions have been suppressed. Reported, never silent.</summary>
        internal static int CameraFaultsSuppressed;
        internal static string LastCameraFault;

        internal static void Initialize(Harmony harmony, ManualLogSource log)
        {
            _log = log;

            // Patch the BASE declaration, not AdventureDirector's override. The override calls
            // base._endAdventure, so patching only the derived method would fire for adventures but
            // miss any other director that inherits the base implementation unchanged.
            Type baseType = AccessTools.TypeByName("GameplayDirectorBase");
            MethodInfo endAdventure = baseType == null ? null : AccessTools.Method(baseType, "_endAdventure");
            if (endAdventure == null)
            {
                log.LogWarning("Target NOT found: GameplayDirectorBase._endAdventure "
                    + "— crucible_endadventure_watch will report 'not installed'.");
                DevKitBridge.ReportTarget("GameplayDirectorBase._endAdventure", null);
                return;
            }

            DevKitBridge.ReportTarget("GameplayDirectorBase._endAdventure", endAdventure);
            try
            {
                harmony.Patch(endAdventure,
                    new HarmonyMethod(AccessTools.Method(typeof(RunEndCommands), "EndAdventurePrefix")));
                _watchInstalled = true;
                log.LogInfo("RunEndCommands: _endAdventure watch installed.");
            }
            catch (Exception ex)
            {
                log.LogWarning("RunEndCommands: could not patch _endAdventure: " + ex.Message);
            }

            InstallCameraGuard(harmony, log);
        }

        /// <summary>
        /// Stops a CAMERA failure from aborting QUEST RESOLUTION.
        ///
        /// AdventureDirector._resolveQuests calls _forceResetCameraToActivePlayer, which does
        /// `_adventureCamera.SetTrackedGroup(... _getEntityPropGameObject(pEntity).transform)`. When
        /// the active character has no prop GameObject in _gameObjectMaps -- routine in a
        /// harness-driven run, where quests are completed without the moves and encounters that
        /// normally create and refresh those props -- that dereference throws, and because
        /// _resolveQuests is awaited by _tryCompleteQuests BEFORE the AdventureEndTrigger==WIN
        /// check, the whole completion pass dies and the adventure can never end.
        ///
        /// Measured 2026-08-24: the WIN quest completed, the pump Task faulted with
        /// NullReferenceException at _forceResetCameraToActivePlayer, and _endAdventure never ran.
        ///
        /// A FINALIZER is used rather than a prefix so the game's own code still runs and only its
        /// exception is swallowed -- the camera moves whenever it can, and merely fails to move when
        /// it cannot. Every suppression is counted and the first message kept, so this can never
        /// quietly hide a real regression.
        /// </summary>
        private static void InstallCameraGuard(Harmony harmony, ManualLogSource log)
        {
            Type director = AccessTools.TypeByName("AdventureDirector");
            MethodInfo reset = director == null
                ? null
                : AccessTools.Method(director, "_forceResetCameraToActivePlayer");
            if (reset == null)
            {
                log.LogWarning("Target NOT found: AdventureDirector._forceResetCameraToActivePlayer "
                    + "— a camera fault can still abort quest resolution.");
                DevKitBridge.ReportTarget("AdventureDirector._forceResetCameraToActivePlayer", null);
                return;
            }

            DevKitBridge.ReportTarget("AdventureDirector._forceResetCameraToActivePlayer", reset);
            try
            {
                // Named argument: the positional 5-arg overload is obsolete in HarmonyX, and its
                // 5th parameter is the IL manipulator rather than the finalizer.
                harmony.Patch(reset,
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(RunEndCommands), "CameraResetFinalizer")));
                log.LogInfo("RunEndCommands: camera-reset guard installed.");
            }
            catch (Exception ex)
            {
                log.LogWarning("RunEndCommands: could not guard _forceResetCameraToActivePlayer: " + ex.Message);
            }
        }

        /// <summary>Swallows the camera exception and lets _resolveQuests continue.</summary>
        public static Exception CameraResetFinalizer(Exception __exception)
        {
            if (__exception == null) return null;
            CameraFaultsSuppressed++;
            if (LastCameraFault == null)
                LastCameraFault = __exception.GetType().Name + ": " + __exception.Message;
            return null;
        }

        internal static void TryRegister()
        {
            Register("crucible_endadventure_watch", "CrucibleEndAdventureWatch",
                new List<string> { "reset|snapshot on|snapshot off (optional)" });
            Register("crucible_summary_dismiss", "CrucibleSummaryDismiss", new List<string>());
            Register("crucible_encounter_leave", "CrucibleEncounterLeave", new List<string>());
            Register("crucible_party_gain_xp", "CruciblePartyGainXp",
                new List<string> { "xpEach", "slot or 'all' (optional)" });
        }

        private static void Register(string command, string method, List<string> hints)
        {
            bool done;
            if (Registered.TryGetValue(command, out done) && done) return;
            MethodInfo handler = typeof(RunEndCommands).GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            Registered[command] = GameBridge.RegisterCommand(command, handler, hints);
        }

        // ============================================================== _endAdventure watch

        /// <summary>
        /// Runs before the game tears the adventure down. Deliberately never throws: an exception in
        /// a prefix would abort the game's own shutdown path, turning an observation into a bug.
        /// </summary>
        public static void EndAdventurePrefix(bool pIsVictory)
        {
            try
            {
                _endedVictory = pIsVictory;
                _endedDetail = "outcome=" + (pIsVictory ? "VICTORY" : "DEFEAT");

                if (_autoSnapshot)
                {
                    // Last moment the save still exists on a win. FixtureCommands always writes a
                    // FRESH run id, so this can never overwrite the run being ended.
                    FixtureCommands.CrucibleFixtureSave("endadventure-" + (pIsVictory ? "win" : "loss"));
                    string result = FixtureCommands.LastResult ?? "";
                    foreach (string token in result.Split(' ', '\n'))
                    {
                        if (!token.StartsWith("newRunId=")) continue;
                        _endedSnapshotRunId = token.Substring("newRunId=".Length).Trim();
                        break;
                    }
                    _endedDetail += " snapshot=" + (_endedSnapshotRunId ?? "(save failed)");
                }

                if (_log != null) _log.LogInfo("crucible endadventure watch: " + _endedDetail);
            }
            catch (Exception ex)
            {
                _endedDetail = "outcome latched but snapshot threw: " + ex.Message;
            }
        }

        /// <summary>
        /// crucible_endadventure_watch [reset|snapshot on|snapshot off] — read the latched outcome.
        ///
        /// This is the ONLY sound victory assertion. Route is identical on a win and a loss, and the
        /// run's own CompletedQuests are gone with the save once the summary is dismissed.
        /// </summary>
        public static void CrucibleEndAdventureWatch(string action, string value)
        {
            LastResult = null;
            try
            {
                string verb = (action ?? "").Trim().ToLowerInvariant();
                if (verb == "reset")
                {
                    _endedVictory = null;
                    _endedDetail = null;
                    _endedSnapshotRunId = null;
                }
                else if (verb == "snapshot")
                {
                    string setting = (value ?? "").Trim().ToLowerInvariant();
                    if (setting != "on" && setting != "off")
                    {
                        LastResult = "error: usage: crucible_endadventure_watch snapshot <on|off>";
                        return;
                    }
                    _autoSnapshot = setting == "on";
                }
                else if (verb.Length > 0)
                {
                    LastResult = "error: usage: crucible_endadventure_watch [reset|snapshot <on|off>]";
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("installed=").Append(_watchInstalled)
                  .Append(" autoSnapshot=").Append(_autoSnapshot)
                  .Append(" cameraFaultsSuppressed=").Append(CameraFaultsSuppressed);
                if (LastCameraFault != null) sb.Append(" (first: ").Append(LastCameraFault).Append(")");
                sb.Append("\nended=").Append(_endedVictory.HasValue);
                if (_endedVictory.HasValue)
                {
                    sb.Append(" victory=").Append(_endedVictory.Value);
                    sb.Append("\ndetail=").Append(_endedDetail);
                    if (_endedSnapshotRunId != null)
                        sb.Append("\nsnapshotRunId=").Append(_endedSnapshotRunId)
                          .Append("  (load it with crucible_invoke AdventureSelectionDirector _loadGameRun)");
                }
                else if (!_watchInstalled)
                {
                    sb.Append("\nNOTE: the patch is NOT installed, so 'ended=False' is the absence of a");
                    sb.Append("\n      measurement, not evidence that the run is still going.");
                }
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_endadventure_watch threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_summary_dismiss

        /// <summary>
        /// crucible_summary_dismiss — page through the whole adventure summary.
        ///
        /// The summary is MULTI-PAGE: "ADVENTURE COMPLETE" is followed by "PLAYER SUMMARY", and both
        /// pages use the SAME element name, next-btn -- only its text changes ("Continue" then
        /// "Finish"). Measured 2026-08-24: a single press looked successful and left the run sitting
        /// on the second page, still not unwound. So this presses until the summary document is
        /// actually gone rather than pressing once and assuming.
        ///
        /// Pressing the button is deliberate rather than completing the TaskCompletionSource
        /// directly: the summary's own handler is what sets the Task's RESULT, and that result
        /// decides whether the save is removed. Short-circuiting it would end the run through a path
        /// the game never takes.
        ///
        /// It only ever presses next-btn. The other button on that screen, load-game-btn, LOADS A
        /// SAVE, and must never be pressed by a harness.
        /// </summary>
        public static void CrucibleSummaryDismiss()
        {
            LastResult = null;
            try
            {
                bool showing = SummaryShowing();

                StringBuilder pages = new StringBuilder();
                int pressed = 0;
                for (int page = 0; page < MaxSummaryPages; page++)
                {
                    if (!SummaryDocumentPresent()) break;

                    UiCommands.CrucibleUiClick("next-btn");
                    string pageResult = UiCommands.LastResult ?? "(no result)";
                    UiCommands.LastResult = null;

                    // A press that matched nothing means next-btn is gone; stop rather than spin.
                    if (pageResult.StartsWith("no visible button matches")) break;
                    pressed++;
                    pages.Append("\n  page ").Append(pressed).Append(": ")
                         .Append(FirstLine(pageResult));
                }

                string clickResult = "pressesMade=" + pressed
                    + " summaryStillPresent=" + SummaryDocumentPresent() + pages;
                // Clear it, or the RPC layer returns the INNER click's output instead of ours:
                // UiCommands.LastResult is read earlier than RunEndCommands.LastResult in
                // RpcServer's ?? chain, so a command that delegates to another command silently
                // loses its own result. Measured 2026-08-24.
                UiCommands.LastResult = null;

                LastResult = "summaryShowingBefore=" + showing + "\n" + clickResult
                    + "\nNOTE: the run finishes unwinding asynchronously. Re-read"
                    + "\n      crucible_endadventure_watch and crucible_state afterwards.";
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_summary_dismiss threw: " + ex.Message;
            }
        }

        /// <summary>Pages the summary can have before this gives up; two are known, this allows for more.</summary>
        private const int MaxSummaryPages = 6;

        /// <summary>
        /// Whether the summary's UIDocument is on screen. Used instead of
        /// AdventureSummaryViewHelper.IsShowing() for the loop condition, because IsShowing tracks the
        /// helper's own flag rather than which page is rendered.
        /// </summary>
        private static bool SummaryDocumentPresent()
        {
            try
            {
                UiCommands.CrucibleUiDump("-", "button");
                string dump = UiCommands.LastResult ?? "";
                UiCommands.LastResult = null;
                return dump.Contains("AdventureSummaryUIDocument");
            }
            catch (Exception) { return false; }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(no result)";
            int newline = text.IndexOf('\n');
            return newline < 0 ? text : text.Substring(0, newline);
        }

        private static bool SummaryShowing()
        {
            try
            {
                Type helper = AccessTools.TypeByName("AdventureSummaryViewHelper");
                MethodInfo isShowing = helper == null ? null : AccessTools.Method(helper, "IsShowing");
                if (isShowing == null) return false;
                object value = isShowing.Invoke(null, null);
                return value is bool && (bool)value;
            }
            catch (Exception) { return false; }
        }

        // ============================================================== crucible_encounter_leave

        /// <summary>
        /// crucible_encounter_leave — close whatever encounter UI is open.
        ///
        /// <c>_stopEncounterAsync(Entity pEncounterEntity, Entity pViewingCharacterEntity, bool
        /// pIsViewOnly, bool pReturnCameraFocus, bool pForceCloseOffturnPlayersPreview)</c>. The
        /// encounter entity is passed as null: the method tolerates it, and the alternative is
        /// guessing which of the run's entities the open UI belongs to.
        /// </summary>
        public static void CrucibleEncounterLeave()
        {
            LastResult = null;
            try
            {
                object director = PartyAccess.Director();
                if (director == null) { LastResult = "error: AdventureDirector unavailable"; return; }

                MethodInfo stop = null;
                foreach (MethodInfo m in director.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_stopEncounterAsync", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 5) { stop = m; break; }
                }
                if (stop == null) { LastResult = "error: _stopEncounterAsync(5 args) not found"; return; }

                object active = PartyAccess.ReadMember(director, "_activeCharacterEntity");
                stop.Invoke(director, new object[] { null, active, false, true, true });

                LastResult = "called _stopEncounterAsync(null, activeCharacter, false, true, true)"
                    + "\nNOTE: returns a Task that is not awaited; it also re-pumps quest completion."
                    + "\n      Re-read crucible_state to confirm the encounter UI is gone.";
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: _stopEncounterAsync threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_encounter_leave threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_party_gain_xp

        /// <summary>
        /// crucible_party_gain_xp &lt;xpEach&gt; [slot|all] — grant XP through the game's own path.
        ///
        /// <c>pConsiderMultiplier</c> is false so the amount asked for is the amount granted; a
        /// difficulty multiplier would make the command's effect depend on the save.
        /// </summary>
        public static void CruciblePartyGainXp(string xpEach, string slotOrAll)
        {
            LastResult = null;
            try
            {
                int xp;
                if (!int.TryParse((xpEach ?? "").Trim(), out xp))
                {
                    LastResult = "error: usage: crucible_party_gain_xp <xpEach> [slot|all]";
                    return;
                }

                List<object> party;
                string partyError;
                if (!PartyAccess.TryGetParty(out party, out partyError))
                {
                    LastResult = "error: " + partyError;
                    return;
                }

                string which = (slotOrAll ?? "all").Trim();
                List<object> targets = new List<object>();
                if (string.Equals(which, "all", StringComparison.OrdinalIgnoreCase))
                {
                    targets.AddRange(party);
                }
                else
                {
                    int slot;
                    if (!int.TryParse(which, out slot) || slot < 0 || slot >= party.Count)
                    {
                        LastResult = "error: slot must be 0.." + (party.Count - 1) + " or 'all', got '" + which + "'";
                        return;
                    }
                    targets.Add(party[slot]);
                }

                Type progression = AccessTools.TypeByName("ProgressionHelper");
                MethodInfo gain = progression == null ? null : AccessTools.Method(progression, "EntitiesGainXP");
                if (gain == null) { LastResult = "error: ProgressionHelper.EntitiesGainXP not found"; return; }

                // The parameter is List<Entity>; a List<object> will not bind, so build the exact
                // generic list the signature asks for.
                ParameterInfo[] parameters = gain.GetParameters();
                IList typed = (IList)Activator.CreateInstance(parameters[0].ParameterType);
                foreach (object entity in targets) typed.Add(entity);

                IList results = (IList)Activator.CreateInstance(parameters[3].ParameterType);

                string before = DescribeLevels(party);
                gain.Invoke(null, new object[] { typed, xp, false, results });
                string after = DescribeLevels(party);

                LastResult = "granted " + xp + " xp to " + targets.Count + " character(s)"
                    + "\nbefore: " + before
                    + "\nafter:  " + after
                    + "\nabilityResults=" + results.Count;
                if (_log != null) _log.LogInfo("crucible_party_gain_xp: " + LastResult);
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: EntitiesGainXP threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_party_gain_xp threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Levels and XP for every party slot. Read through CharacterHelper where possible because
        /// level is DERIVED from an inventory Thing rather than stored as a field.
        /// </summary>
        private static string DescribeLevels(List<object> party)
        {
            StringBuilder sb = new StringBuilder();
            // ProgressionHelper.GetEntityLevel, NOT CharacterHelper.GetLevel -- the latter does not
            // exist, and a null MethodInfo would have printed "(unreadable)" for every slot, which
            // reads as a broken party rather than a wrong member name.
            Type progression = AccessTools.TypeByName("ProgressionHelper");
            MethodInfo getLevel = progression == null ? null : AccessTools.Method(progression, "GetEntityLevel");

            for (int i = 0; i < party.Count; i++)
            {
                if (i > 0) sb.Append("  ");
                sb.Append('[').Append(i).Append("] ");

                object level = null;
                if (getLevel != null)
                {
                    try { level = getLevel.Invoke(null, new object[] { party[i] }); }
                    catch (Exception) { level = null; }
                }
                sb.Append("level=").Append(level == null ? "(unreadable)" : level.ToString());
            }
            return sb.ToString();
        }
    }
}
