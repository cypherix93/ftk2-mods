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
    /// The debug/cheat verb layer (S3): crucible_kill_all, crucible_heal_party, crucible_end_phase,
    /// crucible_set_level, crucible_give, crucible_time_advance, crucible_pin_seed,
    /// crucible_quest_state, crucible_quest_complete.
    ///
    /// Hard lesson this file is built around, from a live session 2026-08-23: writing a state field
    /// directly is not enough. Setting `AdventureState.CurrentTimeOfDayIndex` from 0 to 2 succeeded
    /// as a write (old=0 new=2) and the HUD kept showing "Dawn" -- nothing propagated. So every verb
    /// here either (a) drives the game's OWN method (ReflectionCommands.TryInvoke, the same
    /// machinery behind the already-CONFIRMED-LIVE crucible_invoke) so the game updates its own
    /// state and UI, or (b) where only a field write is buildable (CharacterComponent stats,
    /// QuestState.CompletedObjectives, GameRandom.Seed), follows it with the game's own resolution
    /// call where one exists, and always verifies against an observable read before/after rather
    /// than trusting the write/dispatch alone.
    ///
    /// Same handoff contract as ReflectionCommands/UiCommands/GamepadCommands: results go through
    /// <see cref="LastResult"/> for RpcServer to pick up; handlers never throw out.
    /// </summary>
    internal static class DebugVerbCommands
    {
        private static ManualLogSource _log;
        internal static string LastResult;

        private static readonly MethodInfo KillAllHandler = typeof(DebugVerbCommands).GetMethod("CrucibleKillAll", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo HealPartyHandler = typeof(DebugVerbCommands).GetMethod("CrucibleHealParty", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo EndPhaseHandler = typeof(DebugVerbCommands).GetMethod("CrucibleEndPhaseNamed", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo SetLevelHandler = typeof(DebugVerbCommands).GetMethod("CrucibleSetLevel", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo GiveHandler = typeof(DebugVerbCommands).GetMethod("CrucibleGive", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo TimeAdvanceHandler = typeof(DebugVerbCommands).GetMethod("CrucibleTimeAdvance", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo PinSeedHandler = typeof(DebugVerbCommands).GetMethod("CruciblePinSeed", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo QuestStateHandler = typeof(DebugVerbCommands).GetMethod("CrucibleQuestState", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo QuestCompleteHandler = typeof(DebugVerbCommands).GetMethod("CrucibleQuestComplete", BindingFlags.Public | BindingFlags.Static);

        private static bool _registered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (_registered) return;

            bool a = GameBridge.RegisterCommand("crucible_kill_all", KillAllHandler, new List<string>());
            bool b = GameBridge.RegisterCommand("crucible_heal_party", HealPartyHandler, new List<string>());
            bool c = GameBridge.RegisterCommand("crucible_end_phase", EndPhaseHandler, new List<string> { "phase" });
            bool d = GameBridge.RegisterCommand("crucible_set_level", SetLevelHandler, new List<string> { "slot", "level" });
            bool e = GameBridge.RegisterCommand("crucible_give", GiveHandler, new List<string> { "configId", "qty" });
            bool f = GameBridge.RegisterCommand("crucible_time_advance", TimeAdvanceHandler, new List<string> { "steps" });
            bool g = GameBridge.RegisterCommand("crucible_pin_seed", PinSeedHandler, new List<string> { "seed" });
            bool h = GameBridge.RegisterCommand("crucible_quest_state", QuestStateHandler, new List<string>());
            bool i = GameBridge.RegisterCommand("crucible_quest_complete", QuestCompleteHandler, new List<string> { "questIndex", "objectiveIndex" });

            _registered = a && b && c && d && e && f && g && h && i;
            if (_registered && _log != null) _log.LogInfo("DebugVerbCommands registered (crucible_kill_all/heal_party/end_phase/set_level/give/time_advance/pin_seed/quest_state/quest_complete).");
        }

        // ============================================================== crucible_kill_all

        /// <summary>Loops CombatState.Entities and calls CharacterHelper.TryKillCharacter (falling
        /// back to .KillCharacter), verifying via CharacterHelper.IsDead before/after every
        /// combatant. Kills BOTH sides -- this is a raw "wipe the field" cheat, not a "make me win"
        /// verb (see the report for why force-win is not buildable at all).</summary>
        public static void CrucibleKillAll()
        {
            LastResult = null;
            try
            {
                object combatState = RunAccess.GetCombatState();
                if (combatState == null) { LastResult = "error: not in combat (GameRunData.CombatState is null)"; return; }

                IList entities = RunAccess.GetMember(combatState, "Entities") as IList;
                if (entities == null) { LastResult = "error: CombatState.Entities unavailable"; return; }

                Type helperType = AccessTools.TypeByName("CharacterHelper");
                if (helperType == null) { LastResult = "error: CharacterHelper type not found"; return; }

                List<object> snapshot = new List<object>();
                foreach (object e in entities) if (e != null) snapshot.Add(e);

                int deadBefore = 0;
                foreach (object e in snapshot) if (IsDeadEntity(helperType, e)) deadBefore++;

                foreach (object e in snapshot)
                {
                    object result;
                    if (!TryInvokeUnary(helperType, "TryKillCharacter", e, out result))
                        TryInvokeUnary(helperType, "KillCharacter", e, out result);
                }

                int deadAfter = 0;
                foreach (object e in snapshot) if (IsDeadEntity(helperType, e)) deadAfter++;

                LastResult = "api=CharacterHelper.TryKillCharacter(Entity)/.KillCharacter(Entity) "
                    + "observable=CharacterHelper.IsDead(Entity) entities=" + snapshot.Count
                    + " deadBefore=" + deadBefore + " deadAfter=" + deadAfter
                    + " changed=" + (deadAfter > deadBefore);
            }
            catch (Exception ex) { LastResult = "error: crucible_kill_all threw: " + ex.Message; }
        }

        private static bool IsDeadEntity(Type helperType, object entity)
        {
            object result;
            if (!TryInvokeUnary(helperType, "IsDead", entity, out result)) return false;
            return result is bool && (bool)result;
        }

        // ============================================================== crucible_heal_party

        /// <summary>Loops every PlayerComponent-bearing entity in the current run and calls
        /// CharacterHelper.SetToMaxHealth(Entity), verifying via CharacterComponent.CurrentHealth
        /// before/after each.</summary>
        public static void CrucibleHealParty()
        {
            LastResult = null;
            try
            {
                Type helperType = AccessTools.TypeByName("CharacterHelper");
                if (helperType == null) { LastResult = "error: CharacterHelper type not found"; return; }

                List<object> party = RunAccess.GetPartyEntities();
                if (party.Count == 0) { LastResult = "error: no party entities found (no PlayerComponent-bearing entity in GameRunData.Entities)"; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("api=CharacterHelper.SetToMaxHealth(Entity) observable=CharacterComponent.CurrentHealth party=").Append(party.Count);
                int anyChanged = 0;

                foreach (object e in party)
                {
                    object character = RunAccess.GetComponent(e, "CharacterComponent");
                    int before = ReadInt(character, "CurrentHealth");
                    object result;
                    TryInvokeUnary(helperType, "SetToMaxHealth", e, out result);
                    int after = ReadInt(character, "CurrentHealth");
                    bool changed = after != before;
                    if (changed) anyChanged++;
                    sb.Append(" | ").Append(RunAccess.DisplayName(e) ?? "?")
                      .Append(": hpBefore=").Append(before).Append(" hpAfter=").Append(after).Append(" changed=").Append(changed);
                }

                sb.Append(" -- changed=").Append(anyChanged > 0);
                LastResult = sb.ToString();
            }
            catch (Exception ex) { LastResult = "error: crucible_heal_party threw: " + ex.Message; }
        }

        // ============================================================== crucible_end_phase

        /// <summary>Dispatches to the live phase owner's own <c>_debugEndPhase</c> (RestPhase's one
        /// overload takes an Int32 pOption; every other phase is zero-arg -- crucible-traversal-
        /// inventory.md §0/§2). Resolves the live instance and invokes via
        /// ReflectionCommands.TryInvoke (the same CONFIRMED-LIVE machinery behind crucible_invoke).
        /// Verifies via RouterHelper.GetCurrentRoute() before/after: COMBAT/ENCOUNTER/REST/TREASURE/
        /// TRAP/WHEEL/FORTUNE are themselves live eRoutes values, so ending a phase should move the
        /// route.</summary>
        public static void CrucibleEndPhaseNamed(string pPhase)
        {
            LastResult = null;
            try
            {
                string typeName; bool needsOption; string parseError;
                if (!PhaseName.TryParse(pPhase, out typeName, out needsOption, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                MethodInfo getRoute = ResolveGetCurrentRoute();
                object before = SafeInvokeStatic(getRoute);

                string[] args = needsOption ? new string[] { "0" } : new string[0];
                object returnValue; string strategy; string invokeError;
                bool ok = ReflectionCommands.TryInvoke(typeName, "_debugEndPhase", args, out returnValue, out strategy, out invokeError);

                object after = SafeInvokeStatic(getRoute);

                LastResult = "api=" + typeName + "._debugEndPhase(" + (needsOption ? "0" : "") + ") instance=" + strategy
                    + " dispatched=" + ok + (ok ? "" : " error=" + invokeError)
                    + " observable=RouterHelper.GetCurrentRoute()"
                    + " routeBefore=" + Describe(before) + " routeAfter=" + Describe(after)
                    + " changed=" + !object.Equals(before, after);
            }
            catch (Exception ex) { LastResult = "error: crucible_end_phase threw: " + ex.Message; }
        }

        // ============================================================== crucible_set_level

        /// <summary>Resolves a party entity by index into GetPartyEntities() (documented as a
        /// best-available, non-persistent ordering -- see RunAccess.GetPartyEntities), finds the
        /// live CharacterHelper.TryProgressCharacterEntityToLevel overload accepting (Entity, Int32)
        /// by runtime parameter matching (never a guessed fixed signature), and verifies via
        /// CharacterComponent.ExtraLevel before/after -- the closest documented level-shaped field;
        /// no direct GetLevel accessor was found, so this is reported explicitly as a proxy.</summary>
        public static void CrucibleSetLevel(string pSlot, string pLevel)
        {
            LastResult = null;
            try
            {
                int slot; string slotError;
                if (!PartySlotArg.TryParse(pSlot, out slot, out slotError)) { LastResult = "error: " + slotError; return; }

                int level; string levelError;
                if (!IntArg.TryParseNonNegative(pLevel, "level", out level, out levelError)) { LastResult = "error: " + levelError; return; }

                List<object> party = RunAccess.GetPartyEntities();
                if (slot >= party.Count)
                {
                    LastResult = "error: slot " + slot + " out of range (party size=" + party.Count + ")";
                    return;
                }

                object entity = party[slot];
                string name = RunAccess.DisplayName(entity) ?? "?";

                Type helperType = AccessTools.TypeByName("CharacterHelper");
                if (helperType == null) { LastResult = "error: CharacterHelper type not found"; return; }

                MethodInfo method = FindBinaryStatic(helperType, "TryProgressCharacterEntityToLevel", entity, typeof(int));
                if (method == null)
                {
                    LastResult = "error: CharacterHelper.TryProgressCharacterEntityToLevel(<Entity>, Int32) overload not found on the live assembly";
                    return;
                }

                object[] args; string buildError;
                if (!BuildArgs(method, entity, level, out args, out buildError))
                {
                    LastResult = "error: " + buildError;
                    return;
                }

                object character = RunAccess.GetComponent(entity, "CharacterComponent");
                int before = ReadInt(character, "ExtraLevel");

                try { method.Invoke(null, args); }
                catch (Exception ex) { LastResult = "error: invoke failed: " + ex.Message; return; }

                int after = ReadInt(character, "ExtraLevel");

                LastResult = "api=CharacterHelper.TryProgressCharacterEntityToLevel entity=" + name + " slot=" + slot
                    + " requestedLevel=" + level
                    + " observable=CharacterComponent.ExtraLevel (proxy -- no direct GetLevel accessor found)"
                    + " before=" + before + " after=" + after + " changed=" + (before != after);
            }
            catch (Exception ex) { LastResult = "error: crucible_set_level threw: " + ex.Message; }
        }

        // ============================================================== crucible_give

        /// <summary>Thin wrapper over the shipped GetSpecificThing console command (already working
        /// per the harness's own record), verifying via the sum of CharacterComponent.Things.Count
        /// across every party entity before/after -- the shipped command's own targeting logic
        /// decides which character receives the item, so a whole-party sum is the honest observable
        /// rather than guessing which slot changed.</summary>
        public static void CrucibleGive(string pConfigId, string pQty)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(pConfigId)) { LastResult = "error: missing configId"; return; }
                int qty; string qtyError;
                if (!IntArg.TryParsePositive(pQty, "qty", out qty, out qtyError)) { LastResult = "error: " + qtyError; return; }

                List<object> party = RunAccess.GetPartyEntities();
                int before = SumThings(party);

                string execError;
                bool ok = GameBridge.Exec("GetSpecificThing", new string[] { pConfigId, pQty.ToString() }, out execError);

                int after = SumThings(party);

                LastResult = "api=shipped console command GetSpecificThing configId=" + pConfigId + " qty=" + qty
                    + " dispatched=" + ok + (ok ? "" : " error=" + execError)
                    + " observable=sum(CharacterComponent.Things.Count) across party"
                    + " before=" + before + " after=" + after + " changed=" + (after != before);
            }
            catch (Exception ex) { LastResult = "error: crucible_give threw: " + ex.Message; }
        }

        private static int SumThings(List<object> party)
        {
            int sum = 0;
            foreach (object e in party)
            {
                object character = RunAccess.GetComponent(e, "CharacterComponent");
                object things = RunAccess.GetMember(character, "Things");
                sum += RunAccess.CountOf(things);
            }
            return sum;
        }

        // ============================================================== crucible_time_advance

        /// <summary>Calls the real overworld end-turn method, AdventureDirector._doEndTurn(),
        /// `steps` times -- the game's own turn-advance path, not a direct write to
        /// AdventureState.CurrentTimeOfDayIndex (which the session already proved does NOT update
        /// the HUD). Verifies via CurrentTimeOfDayIndex and GameRunData.RoundCount before/after.
        /// _doEndTurn returns Task and is not awaited (ReflectionCommands.TryInvoke's documented
        /// behaviour for Task-returning members), so an immediate read can race the async
        /// continuation -- flagged plainly in the result rather than hidden.</summary>
        public static void CrucibleTimeAdvance(string pSteps)
        {
            LastResult = null;
            try
            {
                int steps; string stepsError;
                if (!IntArg.TryParsePositive(pSteps, "steps", out steps, out stepsError)) { LastResult = "error: " + stepsError; return; }
                if (steps > 20)
                {
                    LastResult = "error: steps must be <= 20 per call (asked for " + steps + "); call again to advance further";
                    return;
                }

                object adventureState = RunAccess.GetAdventureState();
                if (adventureState == null) { LastResult = "error: not in an adventure (GameRunData.AdventureState is null)"; return; }
                object gameRun = RunAccess.GetGameRun();

                int timeBefore = ReadInt(adventureState, "CurrentTimeOfDayIndex");
                int roundBefore = ReadInt(gameRun, "RoundCount");

                int dispatched = 0;
                string lastError = null;
                for (int i = 0; i < steps; i++)
                {
                    object returnValue; string strategy; string invokeError;
                    bool ok = ReflectionCommands.TryInvoke("AdventureDirector", "_doEndTurn", new string[0], out returnValue, out strategy, out invokeError);
                    if (!ok) { lastError = invokeError; break; }
                    dispatched++;
                }

                int timeAfter = ReadInt(adventureState, "CurrentTimeOfDayIndex");
                int roundAfter = ReadInt(gameRun, "RoundCount");

                LastResult = "api=AdventureDirector._doEndTurn() (dispatched " + dispatched + "/" + steps + " time(s))"
                    + (lastError != null ? " lastError=" + lastError : "")
                    + " caveat=_doEndTurn returns Task and is not awaited; if changed=false, re-read "
                    + "RouterHelper.Env.GameRun.AdventureState.CurrentTimeOfDayIndex via crucible_get a "
                    + "moment later before concluding it failed"
                    + " observable=AdventureState.CurrentTimeOfDayIndex,GameRunData.RoundCount"
                    + " timeOfDayIndexBefore=" + timeBefore + " timeOfDayIndexAfter=" + timeAfter
                    + " roundCountBefore=" + roundBefore + " roundCountAfter=" + roundAfter
                    + " changed=" + (timeBefore != timeAfter || roundBefore != roundAfter);
            }
            catch (Exception ex) { LastResult = "error: crucible_time_advance threw: " + ex.Message; }
        }

        // ============================================================== crucible_pin_seed

        /// <summary>Writes GameRandom.Seed on every live GameRandom instance this bridge can reach
        /// (CombatState.Random, AdventureDirector._gameRandom, CombatPhase._gameRandom). Unlike
        /// CurrentTimeOfDayIndex, GameRandom.Seed IS the field the RNG itself reads on every draw --
        /// there is no separate cached/derived UI state for it to fall out of sync with, so a direct
        /// field write is the correct implementation here, not a shortcut around one. Verified via
        /// reading .Seed back on each instance found.</summary>
        public static void CruciblePinSeed(string pSeed)
        {
            LastResult = null;
            try
            {
                int seed; string seedError;
                if (!IntArg.TryParse(pSeed, "seed", out seed, out seedError)) { LastResult = "error: " + seedError; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("api=GameRandom.Seed (field write on every reachable live instance)");
                int scopesFound = 0;
                int scopesChanged = 0;

                object combatState = RunAccess.GetCombatState();
                scopesChanged += PinScope(sb, "CombatState.Random", RunAccess.GetMember(combatState, "Random"), seed, ref scopesFound);

                object adventureDirector = ResolveDirectorInstance("AdventureDirector");
                scopesChanged += PinScope(sb, "AdventureDirector._gameRandom", RunAccess.GetMember(adventureDirector, "_gameRandom"), seed, ref scopesFound);

                object combatPhase = ResolveDirectorInstance("CombatPhase");
                scopesChanged += PinScope(sb, "CombatPhase._gameRandom", RunAccess.GetMember(combatPhase, "_gameRandom"), seed, ref scopesFound);

                sb.Append(" -- scopesFound=").Append(scopesFound).Append(" scopesChanged=").Append(scopesChanged)
                  .Append(" changed=").Append(scopesFound > 0 && scopesChanged == scopesFound);
                if (scopesFound == 0) sb.Append(" note=no live GameRandom instance reachable (not in an active run)");

                LastResult = sb.ToString();
            }
            catch (Exception ex) { LastResult = "error: crucible_pin_seed threw: " + ex.Message; }
        }

        private static int PinScope(StringBuilder sb, string label, object gameRandom, int seed, ref int scopesFound)
        {
            if (gameRandom == null) return 0;
            scopesFound++;
            int before = ReadInt(gameRandom, "Seed");
            string setError;
            bool set = TrySetField(gameRandom, "Seed", seed, out setError);
            int after = ReadInt(gameRandom, "Seed");
            bool changed = set && after == seed;
            sb.Append(" | ").Append(label).Append(": before=").Append(before).Append(" after=").Append(after).Append(" changed=").Append(changed);
            return changed ? 1 : 0;
        }

        private static object ResolveDirectorInstance(string typeName)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null) return null;
            object instance; string strategy; string error;
            if (!ReflectionCommands.TryResolveInstance(type, out instance, out strategy, out error)) return null;
            return instance;
        }

        // ============================================================== crucible_quest_state

        /// <summary>Read-only: active/completed/failed quest counts plus each active quest's id and
        /// CompletedObjectives bitmap.</summary>
        public static void CrucibleQuestState()
        {
            LastResult = null;
            try
            {
                object gameRun = RunAccess.GetGameRun();
                if (gameRun == null) { LastResult = "error: no active run (RouterHelper.Env.GameRun is null)"; return; }

                IList active = RunAccess.GetMember(gameRun, "ActiveQuests") as IList;
                IList completed = RunAccess.GetMember(gameRun, "CompletedQuests") as IList;
                IList failed = RunAccess.GetMember(gameRun, "FailedQuests") as IList;

                StringBuilder sb = new StringBuilder();
                sb.Append("active=").Append(active == null ? 0 : active.Count);
                sb.Append(" completed=").Append(completed == null ? 0 : completed.Count);
                sb.Append(" failed=").Append(failed == null ? 0 : failed.Count);

                if (active != null)
                {
                    for (int i = 0; i < active.Count; i++)
                    {
                        object quest = active[i];
                        object data = RunAccess.GetMember(quest, "Data");
                        string id = RunAccess.GetMember(data, "ID") as string;
                        bool[] objectives = RunAccess.GetMember(quest, "CompletedObjectives") as bool[];
                        sb.Append(" | [").Append(i).Append("] id=").Append(id ?? "?").Append(" objectives=");
                        AppendBoolArray(sb, objectives);
                    }
                }

                LastResult = sb.ToString();
            }
            catch (Exception ex) { LastResult = "error: crucible_quest_state threw: " + ex.Message; }
        }

        private static void AppendBoolArray(StringBuilder sb, bool[] values)
        {
            if (values == null) { sb.Append("(unavailable)"); return; }
            sb.Append('[');
            for (int j = 0; j < values.Length; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(values[j] ? "1" : "0");
            }
            sb.Append(']');
        }

        // ============================================================== crucible_quest_complete

        /// <summary>Sets QuestState.CompletedObjectives[objectiveIndex] = true (the one public
        /// mutator field docs/research/crucible-quest-progression.md found), then drives the game's
        /// OWN resolution pass, AdventureDirector._tryCompleteQuests(), so the flag actually gets
        /// picked up rather than sitting inert -- the same "field write alone is not enough" lesson
        /// applied to quests. Verifies the flag flipped AND whether the quest actually left
        /// ActiveQuests (the stronger, end-to-end check).</summary>
        public static void CrucibleQuestComplete(string pQuestIndex, string pObjectiveIndex)
        {
            LastResult = null;
            try
            {
                int questIndex; string qError;
                if (!IntArg.TryParseNonNegative(pQuestIndex, "questIndex", out questIndex, out qError)) { LastResult = "error: " + qError; return; }
                int objectiveIndex; string oError;
                if (!IntArg.TryParseNonNegative(pObjectiveIndex, "objectiveIndex", out objectiveIndex, out oError)) { LastResult = "error: " + oError; return; }

                object gameRun = RunAccess.GetGameRun();
                if (gameRun == null) { LastResult = "error: no active run"; return; }

                IList active = RunAccess.GetMember(gameRun, "ActiveQuests") as IList;
                if (active == null || questIndex >= active.Count)
                {
                    LastResult = "error: questIndex " + questIndex + " out of range (active count=" + (active == null ? 0 : active.Count) + ")";
                    return;
                }

                object quest = active[questIndex];
                object data = RunAccess.GetMember(quest, "Data");
                string id = RunAccess.GetMember(data, "ID") as string;
                bool[] objectives = RunAccess.GetMember(quest, "CompletedObjectives") as bool[];
                if (objectives == null) { LastResult = "error: QuestState.CompletedObjectives unavailable"; return; }
                if (objectiveIndex >= objectives.Length)
                {
                    LastResult = "error: objectiveIndex " + objectiveIndex + " out of range (quest has " + objectives.Length + " objective(s))";
                    return;
                }

                bool before = objectives[objectiveIndex];
                objectives[objectiveIndex] = true;

                object returnValue; string strategy; string invokeError;
                bool ok = ReflectionCommands.TryInvoke("AdventureDirector", "_tryCompleteQuests", new string[0], out returnValue, out strategy, out invokeError);

                IList activeAfter = RunAccess.GetMember(gameRun, "ActiveQuests") as IList;
                IList completedAfter = RunAccess.GetMember(gameRun, "CompletedQuests") as IList;
                bool stillActive = false;
                if (activeAfter != null) foreach (object q in activeAfter) if (ReferenceEquals(q, quest)) { stillActive = true; break; }

                LastResult = "questId=" + (id ?? "?")
                    + " api1=QuestState.CompletedObjectives[i]=true (public field write) "
                    + "api2=AdventureDirector._tryCompleteQuests() (game's own resolution pass; dispatched=" + ok
                    + (ok ? "" : " error=" + invokeError) + ") "
                    + "objectiveBefore=" + before + " objectiveAfter=" + objectives[objectiveIndex]
                    + " questStillActive=" + stillActive
                    + " activeCountAfter=" + (activeAfter == null ? 0 : activeAfter.Count)
                    + " completedCountAfter=" + (completedAfter == null ? 0 : completedAfter.Count)
                    + " changed=" + !before;
            }
            catch (Exception ex) { LastResult = "error: crucible_quest_complete threw: " + ex.Message; }
        }

        // ============================================================== shared helpers

        private static bool TryInvokeUnary(Type declaringType, string methodName, object arg, out object result)
        {
            result = null;
            MethodInfo m = MemberResolver.FindUnaryStatic(declaringType, methodName, arg, null);
            if (m == null) return false;
            try { result = m.Invoke(null, new object[] { arg }); return true; }
            catch (Exception) { return false; }
        }

        private static MethodInfo FindBinaryStatic(Type declaring, string methodName, object firstArg, Type secondParamType)
        {
            for (Type t = declaring; t != null; t = t.BaseType)
            {
                MethodInfo[] all = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < all.Length; i++)
                {
                    MethodInfo m = all[i];
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 2) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(firstArg)) continue;
                    if (ps[1].ParameterType != secondParamType) continue;
                    return m;
                }
            }
            return null;
        }

        /// <summary>Builds a full argument array for a resolved overload, filling any parameters
        /// beyond the two we actually have values for with their own declared defaults. Refuses
        /// (never guesses a value) if a further parameter is required.</summary>
        private static bool BuildArgs(MethodInfo method, object firstArg, object secondArg, out object[] args, out string error)
        {
            error = null;
            ParameterInfo[] ps = method.GetParameters();
            args = new object[ps.Length];
            args[0] = firstArg;
            args[1] = secondArg;
            for (int i = 2; i < ps.Length; i++)
            {
                if (!ps[i].IsOptional)
                {
                    error = method.Name + " has a required parameter '" + ps[i].Name + "' (" + ps[i].ParameterType.Name
                        + ") beyond the two this verb supplies -- refusing rather than guessing a value";
                    args = null;
                    return false;
                }
                args[i] = ps[i].DefaultValue;
            }
            return true;
        }

        private static bool TrySetField(object instance, string name, object value, out string error)
        {
            error = null;
            try
            {
                FieldInfo field = AccessTools.Field(instance.GetType(), name);
                if (field == null) { error = "field not found: " + name; return false; }
                field.SetValue(instance, value);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static int ReadInt(object instance, string field)
        {
            object v = RunAccess.GetMember(instance, field);
            return v is int ? (int)v : -1;
        }

        private static MethodInfo ResolveGetCurrentRoute()
        {
            Type routerHelper = AccessTools.TypeByName("RouterHelper");
            return routerHelper == null ? null : AccessTools.Method(routerHelper, "GetCurrentRoute");
        }

        private static object SafeInvokeStatic(MethodInfo m)
        {
            if (m == null) return null;
            try { return m.Invoke(null, null); }
            catch (Exception) { return null; }
        }

        private static string Describe(object value)
        {
            return value == null ? "(null)" : value.ToString();
        }
    }
}
