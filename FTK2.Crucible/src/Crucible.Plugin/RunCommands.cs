using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;
using UnityEngine;

namespace Crucible.Plugin
{
    /// <summary>
    /// Run-level commands: suppress tutorials, complete quest objectives, and read run status.
    ///
    /// These three exist because a full adventure cannot be driven without them, and each is
    /// grounded in a decompilation of the retail assembly rather than a guess:
    ///
    /// 1. <b>Tutorials are the highest-frequency wedge.</b> Every Royal Tutor popup is gated on
    ///    <c>!Env.User.SeenTutorialIds.Contains(id)</c>, and several fire from <c>_tryProceed</c>
    ///    itself — low health, end turn, receiving a portal scroll. An unattended run walks into one
    ///    within a few turns.
    /// 2. <b>Completing an objective by flag actually works.</b>
    ///    <c>QuestHelper.CheckObjectiveCompletion</c> opens with
    ///    <c>if (pQuest.CompletedObjectives[pObjectiveIndex / 2]) return true;</c> — it short-circuits
    ///    BEFORE evaluating any world condition, and <c>GetCompletedQuests</c> re-derives completion
    ///    every pass. So setting the flag and then pumping runs the game's own closure path: rewards,
    ///    dialogue, next quests, stage triggers. This collapses dungeons, escorts and fetch quests
    ///    into one field write each.
    /// 3. <b>Victory is a data flag, not a code path.</b> <c>_tryCompleteQuests</c> ends the adventure
    ///    when a completed quest carries <c>AdventureEndTrigger == WIN</c>. Route cannot be used to
    ///    detect it: victory and defeat both land on ADVENTURE_SELECTION.
    ///
    /// Verified members (TypeProbe --signatures, 2026-08-24):
    ///   UserData.SeenTutorialIds : List(String) · .TutorialEnabled : Boolean · .ShouldAutoEndTurn : Boolean
    ///   QuestState.CompletedObjectives : Boolean[] · .RewardsDistributed : Boolean[] · .RoundsLeft : Int32
    ///   QuestState.Data : QuestData · .MapID : String
    ///   GameRunData.ActiveQuests / CompletedQuests / FailedQuests / FutureQuests
    ///   AdventureState.MapState (the live copy; GameRunData.RoundCount and
    ///     AdventureState.CurrentTimeOfDayIndex are both [Obsolete] and never written)
    /// </summary>
    internal static class RunCommands
    {
        internal static string LastResult;

        /// <summary>
        /// The Task returned by the last pump. Held ONLY so its outcome can be reported.
        ///
        /// _tryCompleteQuests is async: an exception inside it faults the returned Task instead of
        /// propagating, and nothing observes that Task, so a pump that dies half-way looks exactly
        /// like a pump that ran and found nothing to complete. Reporting the Task state in
        /// crucible_run_status turns that silence into a message.
        /// </summary>
        private static object _lastPumpTask;
        private static ManualLogSource _log;
        private static readonly Dictionary<string, bool> Registered = new Dictionary<string, bool>();

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            Register("crucible_tutorials_suppress", "CrucibleTutorialsSuppress", new List<string>());
            Register("crucible_quest_complete_objective", "CrucibleQuestCompleteObjective",
                new List<string> { "questId", "objectiveIndex or all" });
            Register("crucible_run_status", "CrucibleRunStatus", new List<string>());
            Register("crucible_quest_activate", "CrucibleQuestActivate", new List<string> { "questId" });
            Register("crucible_quest_remove", "CrucibleQuestRemove", new List<string> { "questId or all" });
            Register("crucible_encounters_clear", "CrucibleEncountersClear", new List<string> { "keyword or all" });
        }

        private static void Register(string command, string method, List<string> hints)
        {
            bool done;
            if (Registered.TryGetValue(command, out done) && done) return;
            MethodInfo handler = typeof(RunCommands).GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            Registered[command] = GameBridge.RegisterCommand(command, handler, hints);
        }

        // ============================================================== crucible_tutorials_suppress

        /// <summary>
        /// crucible_tutorials_suppress — mark every tutorial as already seen and turn the tutorial
        /// system off.
        ///
        /// Ids are read from the game's own Tutorials.json rather than hard-coded, so a game update
        /// that adds a tutorial does not silently reintroduce a wedge. The file is a JSON array of
        /// objects each carrying an "Id"; a regex is enough to harvest them and avoids taking a JSON
        /// dependency for one read. TutorialEnabled is also cleared as a belt-and-braces measure,
        /// since it is a separate gate from the seen-list.
        /// </summary>
        public static void CrucibleTutorialsSuppress()
        {
            LastResult = null;
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) { LastResult = "error: Env unavailable"; return; }

                object user = PartyAccess.ReadMember(env, "User");
                if (user == null) { LastResult = "error: Env.User is null"; return; }

                IList seen = PartyAccess.ReadMember(user, "SeenTutorialIds") as IList;
                if (seen == null) { LastResult = "error: UserData.SeenTutorialIds is not a list"; return; }

                int before = seen.Count;

                List<string> ids;
                string readError;
                TryReadTutorialIds(out ids, out readError);

                int added = 0;
                foreach (string id in ids)
                {
                    if (seen.Contains(id)) continue;
                    seen.Add(id);
                    added++;
                }

                // Separate gate from the seen-list, so clear it too.
                string flagNote = "(field not found)";
                FieldInfo enabled = AccessTools.Field(user.GetType(), "TutorialEnabled");
                if (enabled != null)
                {
                    object was = enabled.GetValue(user);
                    enabled.SetValue(user, false);
                    flagNote = was + " -> " + enabled.GetValue(user);
                }

                LastResult = "seenTutorialIds " + before + " -> " + seen.Count + " (added " + added + ")"
                    + " harvested=" + ids.Count
                    + " tutorialEnabled=" + flagNote
                    + (readError == null ? "" : "\nNOTE: " + readError);
                if (_log != null) _log.LogInfo("crucible_tutorials_suppress: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_tutorials_suppress threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Harvests tutorial ids from the shipped Tutorials.json. Returns an empty list plus a note
        /// rather than failing: clearing TutorialEnabled alone is still worth doing.
        /// </summary>
        private static void TryReadTutorialIds(out List<string> ids, out string error)
        {
            ids = new List<string>();
            error = null;
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath,
                    Path.Combine("Assets", Path.Combine("Configs", Path.Combine("JSON~", "Tutorials.json"))));
                if (!File.Exists(path))
                {
                    error = "Tutorials.json not found at " + path + "; only TutorialEnabled was cleared";
                    return;
                }

                string text = File.ReadAllText(path);
                foreach (Match m in Regex.Matches(text, "\"Id\"\\s*:\\s*\"([^\"]+)\""))
                {
                    if (m.Groups.Count > 1) ids.Add(m.Groups[1].Value);
                }
                if (ids.Count == 0) error = "Tutorials.json parsed but no \"Id\" fields matched";
            }
            catch (Exception ex)
            {
                error = "could not read Tutorials.json: " + ex.Message;
            }
        }

        // ============================================================== crucible_quest_complete_objective

        /// <summary>
        /// crucible_quest_complete_objective &lt;questId&gt; &lt;index|all&gt; — flag objectives complete,
        /// then pump so the game resolves the quest through its own path.
        ///
        /// The pump matters: the flag alone changes nothing until <c>_tryCompleteQuests</c> runs.
        ///
        /// <c>_tryCompleteQuests</c> is invoked DIRECTLY. The obvious choice, <c>_tryProceed(true)</c>,
        /// does not work and was measured failing 2026-08-24: the flag was written, the pump reported
        /// success, and the quest never left ActiveQuests. Decompiling settled why -- _tryProceed does
        /// not call _tryCompleteQuests on any branch; it ends the turn and returns. Only _initialize,
        /// _continueTurn, _move, _stopEncounterAsync and _onSelectHexPositionTeleportScroll reach it.
        /// Calling it directly runs the game's own closure path (rewards, dialogue, follow-on quests,
        /// _endAdventure on a WIN trigger) without also burning a turn.
        ///
        /// Objectives are a FLAT PAIR ARRAY — <c>Objectives</c> is [verb, arg, verb, arg, …] so the
        /// objective count is <c>Length / 2</c> and <c>CompletedObjectives</c> is indexed by
        /// <c>i / 2</c>. Indexing the pair array directly is the obvious mistake here.
        /// </summary>
        public static void CrucibleQuestCompleteObjective(string questId, string objectiveIndex)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(questId))
                {
                    LastResult = "error: usage: crucible_quest_complete_objective <questId> <index|all>";
                    return;
                }
                questId = questId.Trim();
                string which = (objectiveIndex ?? "all").Trim();

                object gameRun = GameRun();
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                List<object> quests;
                string findError;
                if (!TryFindActiveQuest(gameRun, questId, out quests, out findError))
                {
                    LastResult = "error: " + findError;
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("quest=").Append(questId).Append(" copies=").Append(quests.Count);

                int changed = 0;
                foreach (object quest in quests)
                {
                    Array flags = PartyAccess.ReadMember(quest, "CompletedObjectives") as Array;
                    if (flags == null) { sb.Append("\n  (CompletedObjectives is not an array; skipped)"); continue; }

                    sb.Append("\n  objectives=").Append(flags.Length)
                      .Append(" before=").Append(DescribeFlags(flags));
                    if (string.Equals(which, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < flags.Length; i++)
                        {
                            if (Convert.ToBoolean(flags.GetValue(i))) continue;
                            flags.SetValue(true, i);
                            changed++;
                        }
                    }
                    else
                    {
                        int index;
                        if (!int.TryParse(which, out index) || index < 0 || index >= flags.Length)
                        {
                            LastResult = sb + "\nerror: index must be 0.." + (flags.Length - 1) + " or 'all', got '" + which + "'";
                            return;
                        }
                        if (!Convert.ToBoolean(flags.GetValue(index))) { flags.SetValue(true, index); changed++; }
                    }

                    sb.Append(" after=").Append(DescribeFlags(flags));
                }

                sb.Append("\nchanged=").Append(changed);

                string pumpError;
                bool pumped = TryPump(out pumpError);
                sb.Append("\npumped=").Append(pumped);
                if (!pumped) sb.Append(" pumpError=").Append(pumpError);

                sb.Append("\nNOTE: resolution is asynchronous and runs dialogue and rewards. Re-read");
                sb.Append("\n      crucible_run_status after a few seconds to see the quest move to");
                sb.Append("\n      CompletedQuests and any follow-on quests appear.");

                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_quest_complete_objective: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_quest_complete_objective threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Drives AdventureDirector._tryCompleteQuests() directly. It returns a Task which is
        /// deliberately not awaited -- resolution plays dialogue and reward animations, so it must not
        /// block the game thread the pump is running on. Re-read crucible_run_status to observe it.
        /// </summary>
        private static bool TryPump(out string error)
        {
            error = null;
            try
            {
                object director = PartyAccess.Director();
                if (director == null) { error = "AdventureDirector unavailable"; return false; }

                MethodInfo complete = null;
                foreach (MethodInfo m in director.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_tryCompleteQuests", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 0) { complete = m; break; }
                }
                if (complete == null) { error = "_tryCompleteQuests() not found"; return false; }

                _lastPumpTask = complete.Invoke(director, null);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = root.GetType().Name + ": " + root.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ============================================================== crucible_quest_activate

        /// <summary>
        /// crucible_quest_activate &lt;questId&gt; — put any quest into ActiveQuests immediately.
        ///
        /// Needed because flag-flipping alone cannot walk the quest chain to its end. Measured
        /// 2026-08-24: completing STORY_1_1's opening quests promoted five follow-ons and then
        /// STOPPED. The chain is not driven by NextQuests alone -- STORY_1_1_COMPLETE_TASKS, the
        /// quest that leads to the WIN quest, carries
        /// <c>QuestStartWorldTriggers: ["CHAOS_ACTIVE", "STORY_1_1_CHAOS_3"]</c>, so it only appears
        /// once the world reaches chaos stage 3. A harness that has to play the game to raise chaos
        /// is not a harness.
        ///
        /// <c>QuestHelper.CreateQuestState(string, ...)</c> builds the state the same way the game
        /// does -- reading the config, normalising every null array, sizing CompletedObjectives to
        /// <c>Objectives.Length / 2</c>, and stamping MapID with the ACTIVE map id, which matters
        /// because GetCompletedQuests filters on <c>x.MapID == ActiveMapID</c> and a quest with the
        /// wrong map id can never complete.
        /// </summary>
        public static void CrucibleQuestActivate(string questId)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(questId))
                {
                    LastResult = "error: usage: crucible_quest_activate <questId>";
                    return;
                }
                questId = questId.Trim();

                object gameRun = GameRun();
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                IList active = PartyAccess.ReadMember(gameRun, "ActiveQuests") as IList;
                if (active == null) { LastResult = "error: GameRunData.ActiveQuests is not a list"; return; }

                foreach (object existing in active)
                {
                    if (!string.Equals(QuestId(existing), questId, StringComparison.OrdinalIgnoreCase)) continue;
                    LastResult = "already active: " + questId + " (no change)";
                    return;
                }

                Type questHelper = AccessTools.TypeByName("QuestHelper");
                MethodInfo create = null;
                if (questHelper != null)
                {
                    foreach (MethodInfo m in questHelper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (!string.Equals(m.Name, "CreateQuestState", StringComparison.Ordinal)) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 4 && ps[0].ParameterType == typeof(string)) { create = m; break; }
                    }
                }
                if (create == null) { LastResult = "error: QuestHelper.CreateQuestState(string, ...) not found"; return; }

                object questState = create.Invoke(null, new object[] { questId, null, true, PartyAccess.ResolveGameRandom() });
                if (questState == null) { LastResult = "error: CreateQuestState returned null for '" + questId + "'"; return; }

                active.Add(questState);

                // Drop the matching entry from FutureQuests, which holds QuestData rather than
                // QuestState. Leaving it would let the game promote a SECOND copy later.
                string futureNote = "not in FutureQuests";
                IList future = PartyAccess.ReadMember(gameRun, "FutureQuests") as IList;
                if (future != null)
                {
                    for (int i = future.Count - 1; i >= 0; i--)
                    {
                        if (!string.Equals(QuestId(future[i]), questId, StringComparison.OrdinalIgnoreCase)) continue;
                        future.RemoveAt(i);
                        futureNote = "removed from FutureQuests";
                        break;
                    }
                }

                Array flags = PartyAccess.ReadMember(questState, "CompletedObjectives") as Array;
                object data = QuestData(questState);
                LastResult = "activated " + questId
                    + " type=" + Str(data == null ? null : PartyAccess.ReadMember(data, "QuestType"))
                    + " endTrigger=" + Str(data == null ? null : PartyAccess.ReadMember(data, "AdventureEndTrigger"))
                    + " objectives=" + (flags == null ? "(null)" : DescribeFlags(flags))
                    + " mapId=" + Str(PartyAccess.ReadMember(questState, "MapID"))
                    + "\n" + futureNote
                    + "\nNOTE: activation alone changes nothing. Complete it with"
                    + "\n      crucible_quest_complete_objective " + questId + " all";
                if (_log != null) _log.LogInfo("crucible_quest_activate: " + LastResult);
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: CreateQuestState threw: " + root.GetType().Name + ": " + root.Message
                    + " (is '" + questId + "' a real quest id?)";
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_quest_activate threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_quest_remove

        /// <summary>
        /// crucible_quest_remove &lt;questId|all&gt; — drop quests out of ActiveQuests entirely.
        ///
        /// A quiet test bed needs this. A quest whose objectives are flagged complete but which never
        /// resolves re-queues its reward prompt on EVERY load, and those prompts hold interaction off
        /// while looking like nothing is wrong. STORY_1_1 ships two identical
        /// GENERIC_ADVENTURE_PRISMATIC_FISH_00 entries, the second of which throws inside
        /// QuestHelper.CheckObjectiveCompletion, so it can never complete and never stops asking.
        ///
        /// Removing beats completing here: completion runs rewards, dialogue and follow-on quests,
        /// all of which is noise when the thing under test is a class trait.
        /// </summary>
        public static void CrucibleQuestRemove(string questId)
        {
            LastResult = null;
            try
            {
                object gameRun = GameRun();
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                IList active = PartyAccess.ReadMember(gameRun, "ActiveQuests") as IList;
                if (active == null) { LastResult = "error: ActiveQuests is not a list"; return; }

                string which = (questId ?? "all").Trim();
                bool removeAll = string.Equals(which, "all", StringComparison.OrdinalIgnoreCase);

                StringBuilder sb = new StringBuilder();
                int before = active.Count;
                int removed = 0;

                for (int i = active.Count - 1; i >= 0; i--)
                {
                    string id = QuestId(active[i]);
                    if (!removeAll && !string.Equals(id, which, StringComparison.OrdinalIgnoreCase)) continue;
                    active.RemoveAt(i);
                    removed++;
                    sb.Append("\n  removed ").Append(id);
                }

                LastResult = "activeQuests " + before + " -> " + active.Count
                    + " (removed " + removed + ")" + sb
                    + "\nNOTE: this does NOT complete them -- no rewards, no follow-on quests. It only"
                    + "\n      stops them being evaluated, which is what a quiet test bed needs.";
                if (_log != null) _log.LogInfo("crucible_quest_remove: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_quest_remove threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_encounters_clear

        /// <summary>
        /// crucible_encounters_clear &lt;keyword|all&gt; — delete map encounters so they stop interrupting.
        ///
        /// Walking the party anywhere trips whatever it steps on: the Night Merchant opens a dialogue,
        /// a venue opens a shop, a quest board opens a menu that never closes itself. None of that is
        /// under test when the subject is a class trait, and each one costs a recovery cycle.
        ///
        /// Matching is a case-insensitive substring of the encounter's config name, so "MERCHANT"
        /// clears the night market and "all" clears every non-combat encounter on the map.
        /// </summary>
        public static void CrucibleEncountersClear(string keyword)
        {
            LastResult = null;
            try
            {
                object gameRun = GameRun();
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                IList entities = PartyAccess.ReadMember(gameRun, "Entities") as IList;
                if (entities == null) { LastResult = "error: GameRunData.Entities is not a list"; return; }

                string match = (keyword ?? "all").Trim();
                bool clearAll = string.Equals(match, "all", StringComparison.OrdinalIgnoreCase);

                StringBuilder sb = new StringBuilder();
                int removed = 0;
                int inspected = 0;

                for (int i = entities.Count - 1; i >= 0; i--)
                {
                    object e = entities[i];
                    if (e == null) continue;
                    object encounter = PartyAccess.FindComponent(e, "EncounterComponent");
                    if (encounter == null) continue;
                    inspected++;

                    // Leave anything with a CombatEncounterComponent alone: those ARE the fights.
                    if (PartyAccess.FindComponent(e, "CombatEncounterComponent") != null) continue;

                    object character = PartyAccess.FindComponent(e, "CharacterComponent");
                    string name = character == null
                        ? Str(PartyAccess.ReadMember(e, "Guid"))
                        : Str(PartyAccess.ReadMember(character, "ConfigName"));

                    if (!clearAll && name.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    entities.RemoveAt(i);
                    removed++;
                    if (sb.Length < 400) sb.Append("\n  removed ").Append(name);
                }

                LastResult = "encountersInspected=" + inspected + " removed=" + removed + sb
                    + "\nNOTE: combat encounters are left alone -- only interruptions are cleared.";
                if (_log != null) _log.LogInfo("crucible_encounters_clear: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_encounters_clear threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_run_status

        /// <summary>
        /// crucible_run_status — the assertion surface for a full run.
        ///
        /// Reads AdventureState.MapState for round and stage, never the [Obsolete] GameRunData.RoundCount
        /// or AdventureState.CurrentTimeOfDayIndex, which the game does not write. Also surfaces the two
        /// things that silently end a run: a STORY quest whose RoundsLeft has reached 0 (an instant
        /// defeat with no combat involved), and whether the adventure summary screen is up.
        /// </summary>
        public static void CrucibleRunStatus()
        {
            LastResult = null;
            try
            {
                object gameRun = GameRun();
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                object adventureState = PartyAccess.ReadMember(gameRun, "AdventureState");
                object mapState = PartyAccess.ReadMember(adventureState, "MapState");

                StringBuilder sb = new StringBuilder();
                sb.Append("map=").Append(Str(PartyAccess.ReadMember(adventureState, "ActiveMapID")))
                  .Append(" round=").Append(Str(PartyAccess.ReadMember(mapState, "RoundCount")))
                  .Append(" totalRounds=").Append(Str(PartyAccess.ReadMember(adventureState, "TotalRoundCount")))
                  .Append(" stage=").Append(Str(PartyAccess.ReadMember(mapState, "GameStageIndex")))
                  .Append(" timeOfDay=").Append(Str(PartyAccess.ReadMember(mapState, "TimeOfDay")));

                object dungeonState = PartyAccess.ReadMember(gameRun, "DungeonState");
                object loopState = dungeonState == null ? null : PartyAccess.ReadMember(dungeonState, "LoopState");
                sb.Append("\nlastPump=").Append(DescribePumpTask());
                sb.Append("\ninDungeon=").Append(loopState != null)
                  .Append(" summaryShowing=").Append(SummaryShowing());

                sb.Append("\nactiveQuests:").Append(DescribeQuests(gameRun, "ActiveQuests", true));
                sb.Append("\ncompletedQuests:").Append(DescribeQuestIds(gameRun, "CompletedQuests"));
                sb.Append("\nfailedQuests:").Append(DescribeQuestIds(gameRun, "FailedQuests"));
                sb.Append("\nfutureQuests:").Append(DescribeQuestIds(gameRun, "FutureQuests"));

                string victory = DescribeVictory(gameRun);
                sb.Append("\nWIN quest completed: ").Append(victory);

                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_run_status threw: " + ex.Message;
            }
        }

        /// <summary>
        /// The only trustworthy victory signal. Route is useless here: _endAdventure sends both a win
        /// and a loss to ADVENTURE_SELECTION (only OUTRO_ADVENTURES divert to CINEMATIC_CREDITS).
        /// </summary>
        /// <summary>
        /// Returns the QuestData for a list element, whatever shape the list holds.
        ///
        /// This is not defensive padding. Measured live 2026-08-24: ActiveQuests elements resolve
        /// through .Data, but CompletedQuests and FutureQuests elements do NOT -- reading .Data on
        /// them yields null, so every id printed as "(null)". That matters far beyond cosmetics:
        /// victory is detected by reading AdventureEndTrigger off a COMPLETED quest, so a
        /// .Data-only reader would have reported "no win" forever and the end-to-end assertion
        /// would never have fired.
        /// </summary>
        private static object QuestData(object quest)
        {
            if (quest == null) return null;
            object data = PartyAccess.ReadMember(quest, "Data");
            if (data != null) return data;
            // The element may already BE the QuestData. Probe a member only QuestData carries.
            if (PartyAccess.ReadMember(quest, "AdventureEndTrigger") != null
                || PartyAccess.ReadMember(quest, "QuestType") != null) return quest;
            return null;
        }

        /// <summary>Quest id, with the element type named when it cannot be resolved at all.</summary>
        private static string QuestId(object quest)
        {
            object data = QuestData(quest);
            object id = data == null ? null : PartyAccess.ReadMember(data, "ID");
            if (id == null && data != null) id = PartyAccess.ReadMember(data, "Id");
            if (id != null) return id.ToString();
            return quest == null ? "(null)" : "(unresolved:" + quest.GetType().Name + ")";
        }

        private static string DescribeVictory(object gameRun)
        {
            IEnumerable completed = PartyAccess.ReadMember(gameRun, "CompletedQuests") as IEnumerable;
            if (completed == null) return "(unreadable)";
            foreach (object quest in completed)
            {
                object data = QuestData(quest);
                object trigger = data == null ? null : PartyAccess.ReadMember(data, "AdventureEndTrigger");
                if (trigger != null && string.Equals(trigger.ToString(), "WIN", StringComparison.OrdinalIgnoreCase))
                    return "YES (" + QuestId(quest) + ")";
            }
            return "no";
        }

        private static string DescribeQuests(object gameRun, string listName, bool detailed)
        {
            IEnumerable list = PartyAccess.ReadMember(gameRun, listName) as IEnumerable;
            if (list == null) return " (unreadable)";

            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (object quest in list)
            {
                n++;
                object data = QuestData(quest);
                Array flags = PartyAccess.ReadMember(quest, "CompletedObjectives") as Array;
                object roundsLeft = PartyAccess.ReadMember(quest, "RoundsLeft");
                object questType = PartyAccess.ReadMember(data, "QuestType");
                object endTrigger = PartyAccess.ReadMember(data, "AdventureEndTrigger");

                sb.Append("\n  ").Append(Str(PartyAccess.ReadMember(data, "ID")))
                  .Append(" type=").Append(Str(questType))
                  .Append(" objectives=").Append(flags == null ? "(null)" : DescribeFlags(flags))
                  .Append(" roundsLeft=").Append(Str(roundsLeft));

                if (endTrigger != null && !string.Equals(endTrigger.ToString(), "NONE", StringComparison.OrdinalIgnoreCase))
                    sb.Append(" endTrigger=").Append(endTrigger);

                // A STORY quest at 0 rounds left ends the run in defeat via _checkAdventureLoss.
                bool isStory = questType != null && string.Equals(questType.ToString(), "STORY", StringComparison.OrdinalIgnoreCase);
                if (isStory && roundsLeft != null && Convert.ToInt32(roundsLeft) == 0)
                    sb.Append("  <-- WARNING: STORY quest at 0 rounds left ends the run in DEFEAT");
            }
            if (n == 0) sb.Append(" (none)");
            return sb.ToString();
        }

        private static string DescribeQuestIds(object gameRun, string listName)
        {
            IEnumerable list = PartyAccess.ReadMember(gameRun, listName) as IEnumerable;
            if (list == null) return " (unreadable)";
            List<string> ids = new List<string>();
            foreach (object quest in list) ids.Add(QuestId(quest));
            return " count=" + ids.Count + (ids.Count == 0 ? "" : " [" + string.Join(", ", ids.ToArray()) + "]");
        }

        /// <summary>
        /// EVERY active quest carrying this id, because ActiveQuests can hold DUPLICATES and
        /// flagging only the first one is not enough.
        ///
        /// STORY_1_1 ships two identical GENERIC_ADVENTURE_PRISMATIC_FISH_00 entries, and the second
        /// one throws NullReferenceException inside QuestHelper.CheckObjectiveCompletion. That
        /// exception aborts GetCompletedQuests for the WHOLE pass, so every quest ordered after it
        /// -- including the WIN quest -- is never evaluated and the adventure can never end. Flagging
        /// the duplicate makes CheckObjectiveCompletion short-circuit at its first line and return
        /// before it can throw, which is what unblocks the pass.
        /// </summary>
        private static bool TryFindActiveQuest(object gameRun, string questId, out List<object> matches, out string error)
        {
            matches = new List<object>();
            error = null;

            IEnumerable active = PartyAccess.ReadMember(gameRun, "ActiveQuests") as IEnumerable;
            if (active == null) { error = "GameRunData.ActiveQuests is not enumerable"; return false; }

            List<string> seen = new List<string>();
            foreach (object candidate in active)
            {
                string id = QuestId(candidate);
                seen.Add(id);
                if (string.Equals(id, questId, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
            }
            if (matches.Count > 0) return true;

            error = "no ACTIVE quest with id '" + questId + "'. Active: "
                  + (seen.Count == 0 ? "(none)" : string.Join(", ", seen.ToArray()))
                  + ". Note a quest already completed is no longer active.";
            return false;
        }

        private static string DescribeFlags(Array flags)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < flags.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Convert.ToBoolean(flags.GetValue(i)) ? '1' : '0');
            }
            return sb.Append(']').ToString();
        }

        private static string SummaryShowing()
        {
            try
            {
                Type helper = AccessTools.TypeByName("AdventureSummaryViewHelper");
                MethodInfo isShowing = helper == null ? null : AccessTools.Method(helper, "IsShowing");
                if (isShowing == null) return "(unreadable)";
                object value = isShowing.Invoke(null, null);
                return value == null ? "(null)" : value.ToString();
            }
            catch (Exception) { return "(unreadable)"; }
        }

        /// <summary>Outcome of the last pump Task, including a fault that would otherwise be silent.</summary>
        private static string DescribePumpTask()
        {
            if (_lastPumpTask == null) return "(none this session)";
            try
            {
                object completed = PartyAccess.ReadMember(_lastPumpTask, "IsCompleted");
                object faulted = PartyAccess.ReadMember(_lastPumpTask, "IsFaulted");
                string state = "completed=" + Str(completed) + " faulted=" + Str(faulted);
                if (faulted is bool && (bool)faulted)
                {
                    Exception ex = PartyAccess.ReadMember(_lastPumpTask, "Exception") as Exception;
                    Exception root = ex;
                    while (root != null && root.InnerException != null) root = root.InnerException;
                    if (root != null)
                    {
                        state += " " + root.GetType().Name + ": " + root.Message;
                        // The stack is the whole point. A bare "NullReferenceException" from inside
                        // an async game method is unactionable; the frame that threw is not.
                        if (!string.IsNullOrEmpty(root.StackTrace)) state += "\n" + root.StackTrace;
                    }
                }
                else if (completed is bool && (bool)completed)
                {
                    object result = PartyAccess.ReadMember(_lastPumpTask, "Result");
                    if (result != null) state += " result=" + result;
                }
                return state;
            }
            catch (Exception ex) { return "(unreadable: " + ex.Message + ")"; }
        }

        private static object GameRun()
        {
            object env = GameBridge.GetEnv();
            return env == null ? null : PartyAccess.ReadMember(env, "GameRun");
        }

        private static string Str(object value)
        {
            return value == null ? "(null)" : value.ToString();
        }
    }
}
