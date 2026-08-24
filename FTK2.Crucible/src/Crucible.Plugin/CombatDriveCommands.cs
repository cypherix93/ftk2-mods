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
    /// Drives an in-progress fight: observe it, list what a character can do, fire an ability, end a
    /// turn, win the fight.
    ///
    /// Grounded in a decompilation of the retail assembly rather than guesswork, which settled three
    /// things that had cost a lot of time:
    ///
    /// 1. <b>Targeting is by TILE COORDINATE, not by entity.</b> The caller supplies only
    ///    <c>CombatDecisionData.Position</c>; CombatPhase resolves the target itself, preferring a
    ///    live combatant standing on that tile and falling back to the tile entity.
    /// 2. <b><c>_performAiDecision</c> is the entry point</b>, not <c>_performAbility</c>. It is what
    ///    the game's own AI calls, and it performs selection, tile highlight, look-at, slot roll and
    ///    execution in the correct order. <c>CombatHelper.PerformAbility</c> needs two delegates and
    ///    a SkillContext and skips all the visual sequencing.
    /// 3. <b><c>_debugEndPhase()</c> is a guaranteed WIN with loot.</b> It removes every living enemy
    ///    from <c>CombatState.Entities</c> and then calls <c>_endCombatAsync</c>, whose victory test
    ///    is `aliveEnemies.Count == 0 &amp;&amp; alivePlayers.Count &gt; 0`. By contrast
    ///    <c>CombatState.EndCombatEarly = true</c> ends the fight with enemies still alive, which
    ///    evaluates as a LOSS — the two are easy to confuse and behave oppositely.
    ///
    /// An <c>AbilityAction</c> is always PICKED from <c>CombatHelper.GetAbilities</c>, never
    /// constructed: a hand-built one can carry a wrong <c>ThingId</c>, which makes
    /// <c>GetCharacterAbilityThing</c> return null and the ability silently fail.
    /// </summary>
    internal static class CombatDriveCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static readonly Dictionary<string, bool> Registered = new Dictionary<string, bool>();

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            Register("crucible_combat_snapshot", "CrucibleCombatSnapshot", new List<string>());
            Register("crucible_list_abilities", "CrucibleListAbilities", new List<string> { "entityGuid or -" });
            Register("crucible_list_targets", "CrucibleListTargets", new List<string> { "abilityName", "entityGuid or -" });
            Register("crucible_use_ability", "CrucibleUseAbility", new List<string> { "abilityName", "x", "y" });
            Register("crucible_use_ability_auto", "CrucibleUseAbilityAuto", new List<string>());
            Register("crucible_win_combat", "CrucibleWinCombat", new List<string>());
            Register("crucible_combat_end_turn", "CrucibleCombatEndTurn", new List<string>());
        }

        private static void Register(string command, string method, List<string> hints)
        {
            bool done;
            if (Registered.TryGetValue(command, out done) && done) return;
            MethodInfo handler = typeof(CombatDriveCommands).GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            Registered[command] = GameBridge.RegisterCommand(command, handler, hints);
        }

        // ============================================================== crucible_combat_snapshot

        /// <summary>
        /// crucible_combat_snapshot — every combatant with its tile, health, actions and side, plus
        /// the active entity and the tile grid.
        ///
        /// Tile coordinates are the whole point: without them nothing can be targeted, because
        /// targeting takes a position rather than an entity.
        /// </summary>
        public static void CrucibleCombatSnapshot()
        {
            LastResult = null;
            try
            {
                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) { LastResult = "error: " + error; return; }

                IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
                if (entities == null) { LastResult = "error: CombatState.Entities is not enumerable"; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("totalRounds=").Append(Str(PartyAccess.ReadMember(combatState, "TotalRounds")))
                  .Append(" waveIndex=").Append(Str(PartyAccess.ReadMember(combatState, "WaveIndex")))
                  .Append(" activeGuid=").Append(ActiveEntityGuid(combatState));

                int combatants = 0;
                int tiles = 0;
                StringBuilder people = new StringBuilder();
                StringBuilder grid = new StringBuilder();

                foreach (object entity in entities)
                {
                    if (entity == null) continue;
                    object combat = PartyAccess.FindComponent(entity, "CombatComponent");
                    object venue = PartyAccess.FindComponent(entity, "VenueComponent");
                    object tile = PartyAccess.FindComponent(entity, "VenueTileComponent");
                    object character = PartyAccess.FindComponent(entity, "CharacterComponent");

                    if (combat != null && character != null)
                    {
                        combatants++;
                        people.Append("\n  ").Append(Str(PartyAccess.ReadMember(entity, "Guid")))
                              .Append(" name=").Append(Str(PartyAccess.ReadMember(character, "DisplayName")))
                              .Append(" class=").Append(Str(PartyAccess.ReadMember(character, "ConfigName")))
                              .Append(" group=").Append(Str(PartyAccess.ReadMember(character, "GroupIndex")))
                              .Append(" hp=").Append(Str(PartyAccess.ReadMember(character, "CurrentHealth")))
                              .Append(" tile=").Append(venue == null ? "(none)" : Str(PartyAccess.ReadMember(venue, "TilePosition")))
                              .Append(" pa=").Append(Str(PartyAccess.ReadMember(combat, "PrimaryActions")))
                              .Append(" sa=").Append(Str(PartyAccess.ReadMember(combat, "SecondaryActions")))
                              .Append(" dead=").Append(IsDead(entity));
                    }
                    else if (tile != null && venue != null)
                    {
                        tiles++;
                        if (tiles <= 30)
                        {
                            grid.Append("\n  ").Append(Str(PartyAccess.ReadMember(venue, "TilePosition")))
                                .Append(" group=").Append(Str(PartyAccess.ReadMember(tile, "GroupIndex")))
                                .Append(" row=").Append(Str(PartyAccess.ReadMember(tile, "RowPositionsType")));
                        }
                    }
                }

                sb.Append("\ncombatants=").Append(combatants).Append(people);
                sb.Append("\ntiles=").Append(tiles).Append(tiles > 30 ? " (showing first 30)" : "").Append(grid);
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_combat_snapshot threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_list_abilities

        /// <summary>crucible_list_abilities [entityGuid|-] — what this character can actually use.</summary>
        public static void CrucibleListAbilities(string entityGuid)
        {
            LastResult = null;
            try
            {
                object entity;
                string error;
                if (!ResolveEntity(entityGuid, out entity, out error)) { LastResult = "error: " + error; return; }

                List<object> abilities;
                if (!TryGetAbilities(entity, out abilities, out error)) { LastResult = "error: " + error; return; }

                object gameRun = GameRun();
                Type combatHelper = AccessTools.TypeByName("CombatHelper");
                MethodInfo isUsable = combatHelper == null ? null : AccessTools.Method(combatHelper, "IsUsableAbility");
                MethodInfo getThing = combatHelper == null ? null : AccessTools.Method(combatHelper, "GetCharacterAbilityThing");

                StringBuilder sb = new StringBuilder();
                sb.Append("entity=").Append(Str(PartyAccess.ReadMember(entity, "Guid")))
                  .Append(" abilities=").Append(abilities.Count);

                foreach (object ability in abilities)
                {
                    string name = Str(PartyAccess.ReadMember(ability, "AbilityName"));
                    object usable = null;
                    if (isUsable != null && gameRun != null)
                    {
                        try { usable = isUsable.Invoke(null, new object[] { gameRun, entity, name, true }); }
                        catch (Exception) { usable = null; }
                    }
                    object thing = null;
                    if (getThing != null)
                    {
                        try { thing = getThing.Invoke(null, new object[] { entity, ability }); }
                        catch (Exception) { thing = null; }
                    }

                    sb.Append("\n  ").Append(name)
                      .Append(" thingId=").Append(Str(PartyAccess.ReadMember(ability, "ThingId")))
                      .Append(" thingConfig=").Append(Str(PartyAccess.ReadMember(ability, "ThingConfigName")))
                      .Append(" usable=").Append(usable == null ? "(unknown)" : usable.ToString())
                      .Append(" thingResolves=").Append(thing != null);
                }
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_list_abilities threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_list_targets

        /// <summary>
        /// crucible_list_targets &lt;abilityName&gt; [entityGuid|-] — the legal target tiles.
        ///
        /// Uses the same enumeration the game's own decision code uses, so anything it returns is
        /// legal by construction rather than by assumption.
        /// </summary>
        public static void CrucibleListTargets(string abilityName, string entityGuid)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(abilityName))
                {
                    LastResult = "error: usage: crucible_list_targets <abilityName> [entityGuid|-]";
                    return;
                }
                abilityName = abilityName.Trim();

                object entity;
                string error;
                if (!ResolveEntity(entityGuid, out entity, out error)) { LastResult = "error: " + error; return; }

                object combatState;
                if (!TryGetCombatState(out combatState, out error)) { LastResult = "error: " + error; return; }

                Type interactableHelper = AccessTools.TypeByName("InteractableHelper");
                MethodInfo getAbilityConfig = interactableHelper == null ? null : AccessTools.Method(interactableHelper, "GetAbilityConfig");
                if (getAbilityConfig == null) { LastResult = "error: InteractableHelper.GetAbilityConfig not found"; return; }
                object abilityConfig = getAbilityConfig.Invoke(null, new object[] { abilityName });
                if (abilityConfig == null) { LastResult = "error: no ability config for '" + abilityName + "'"; return; }

                Type venueHelper = AccessTools.TypeByName("VenueHelper");
                MethodInfo getTargetable = null;
                foreach (MethodInfo m in venueHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "GetTargetableTiles", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 4) { getTargetable = m; break; }
                }
                if (getTargetable == null) { LastResult = "error: VenueHelper.GetTargetableTiles(4 args) not found"; return; }

                object entities = PartyAccess.ReadMember(combatState, "Entities");
                object result = getTargetable.Invoke(null, new object[] { entity, entities, abilityConfig, null });

                StringBuilder sb = new StringBuilder();
                sb.Append("ability=").Append(abilityName)
                  .Append(" entity=").Append(Str(PartyAccess.ReadMember(entity, "Guid")));

                IEnumerable tiles = result as IEnumerable;
                int count = 0;
                if (tiles != null)
                {
                    foreach (object tileEntity in tiles)
                    {
                        object venue = PartyAccess.FindComponent(tileEntity, "VenueComponent");
                        if (venue == null) continue;
                        count++;
                        sb.Append("\n  tile=").Append(Str(PartyAccess.ReadMember(venue, "TilePosition")))
                          .Append(" occupant=").Append(OccupantAt(combatState, PartyAccess.ReadMember(venue, "TilePosition")));
                    }
                }
                sb.Append("\ntargetableTiles=").Append(count);
                LastResult = sb.ToString();
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: GetTargetableTiles threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_list_targets threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_use_ability

        /// <summary>crucible_use_ability &lt;abilityName&gt; &lt;x&gt; &lt;y&gt; — fire an ability at a tile.</summary>
        public static void CrucibleUseAbility(string abilityName, string x, string y)
        {
            LastResult = null;
            try
            {
                int tileX, tileY;
                if (string.IsNullOrEmpty(abilityName)
                    || !int.TryParse((x ?? "").Trim(), out tileX)
                    || !int.TryParse((y ?? "").Trim(), out tileY))
                {
                    LastResult = "error: usage: crucible_use_ability <abilityName> <x> <y>";
                    return;
                }
                FireAbility(abilityName.Trim(), tileX, tileY, null);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_use_ability threw: " + ex.Message;
            }
        }

        /// <summary>
        /// crucible_use_ability_auto — let the game pick both ability and target, then fire.
        ///
        /// The zero-argument smoke test: if this cannot fire, nothing else will, and it separates
        /// "my targeting is wrong" from "combat is not driveable at all".
        /// </summary>
        public static void CrucibleUseAbilityAuto()
        {
            LastResult = null;
            try
            {
                object entity;
                string error;
                if (!ResolveEntity(null, out entity, out error)) { LastResult = "error: " + error; return; }

                List<object> abilities;
                if (!TryGetAbilities(entity, out abilities, out error)) { LastResult = "error: " + error; return; }

                Type combatHelper = AccessTools.TypeByName("CombatHelper");
                MethodInfo getFirst = combatHelper == null ? null : AccessTools.Method(combatHelper, "GetFirstEntityDecision");
                if (getFirst == null) { LastResult = "error: CombatHelper.GetFirstEntityDecision not found"; return; }

                object typedAbilities = BuildTypedList(getFirst.GetParameters()[2].ParameterType, abilities);
                object decision = getFirst.Invoke(null, new object[] { entity, GameRun(), typedAbilities });
                if (decision == null) { LastResult = "error: GetFirstEntityDecision returned null"; return; }

                object ability = PartyAccess.ReadMember(decision, "Item1");
                object position = PartyAccess.ReadMember(decision, "Item2");
                if (ability == null) { LastResult = "error: no ability chosen (decision=" + decision + ")"; return; }

                string name = Str(PartyAccess.ReadMember(ability, "AbilityName"));
                object px = PartyAccess.ReadMember(position, "Item1");
                object py = PartyAccess.ReadMember(position, "Item2");

                FireAbility(name, Convert.ToInt32(px), Convert.ToInt32(py), ability);
                LastResult = "auto-chose ability=" + name + " target=(" + px + ", " + py + ")\n" + LastResult;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: auto decision threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_use_ability_auto threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Shared firing path. Always goes through CombatPhase._performAiDecision, and always PICKS
        /// the AbilityAction from GetAbilities rather than constructing one.
        /// </summary>
        private static void FireAbility(string abilityName, int tileX, int tileY, object preChosenAbility)
        {
            object entity;
            string error;
            if (!ResolveEntity(null, out entity, out error)) { LastResult = "error: " + error; return; }

            object ability = preChosenAbility;
            if (ability == null)
            {
                List<object> abilities;
                if (!TryGetAbilities(entity, out abilities, out error)) { LastResult = "error: " + error; return; }
                foreach (object candidate in abilities)
                {
                    if (string.Equals(Str(PartyAccess.ReadMember(candidate, "AbilityName")), abilityName, StringComparison.OrdinalIgnoreCase))
                    {
                        ability = candidate;
                        break;
                    }
                }
                if (ability == null)
                {
                    StringBuilder available = new StringBuilder();
                    foreach (object candidate in abilities) available.Append("\n  ").Append(Str(PartyAccess.ReadMember(candidate, "AbilityName")));
                    LastResult = "error: '" + abilityName + "' is not an ability of this character. Available:" + available;
                    return;
                }
            }

            object combatPhase = FindCombatPhase();
            if (combatPhase == null) { LastResult = "error: CombatPhase unavailable (is a fight in progress?)"; return; }

            Type decisionType = AccessTools.TypeByName("CombatDecisionData");
            if (decisionType == null) { LastResult = "error: CombatDecisionData type not found"; return; }
            object decision = Activator.CreateInstance(decisionType);

            FieldInfo abilityField = AccessTools.Field(decisionType, "Ability");
            FieldInfo positionField = AccessTools.Field(decisionType, "Position");
            FieldInfo focusField = AccessTools.Field(decisionType, "FocusUsed");
            if (abilityField == null || positionField == null) { LastResult = "error: CombatDecisionData shape changed"; return; }

            abilityField.SetValue(decision, ability);
            positionField.SetValue(decision, Activator.CreateInstance(positionField.FieldType, new object[] { tileX, tileY }));
            if (focusField != null) focusField.SetValue(decision, 0);

            MethodInfo perform = null;
            foreach (MethodInfo m in combatPhase.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(m.Name, "_performAiDecision", StringComparison.Ordinal)) continue;
                if (m.GetParameters().Length == 3) { perform = m; break; }
            }
            if (perform == null) { LastResult = "error: CombatPhase._performAiDecision(3 args) not found"; return; }

            object results = Activator.CreateInstance(perform.GetParameters()[2].ParameterType);
            string before = SnapshotHealth();

            try
            {
                perform.Invoke(combatPhase, new object[] { entity, decision, results });
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: _performAiDecision threw: " + root.GetType().Name + ": " + root.Message;
                return;
            }

            ICollection resultList = results as ICollection;
            LastResult = "ability=" + abilityName + " target=(" + tileX + ", " + tileY + ")"
                + " origin=" + Str(PartyAccess.ReadMember(entity, "Guid"))
                + "\nresultCount=" + (resultList == null ? -1 : resultList.Count)
                + "\nhealthBefore: " + before
                + "\nNOTE: _performAiDecision returns a Task that is NOT awaited (blocking the game"
                + "\n      thread would deadlock the pump). Re-read crucible_combat_snapshot after a"
                + "\n      moment to observe the effect.";
        }

        // ============================================================== end turn / win

        /// <summary>crucible_combat_end_turn — advance to the next turn through CombatPhase._nextTurn.</summary>
        public static void CrucibleCombatEndTurn()
        {
            LastResult = null;
            try
            {
                object combatPhase = FindCombatPhase();
                if (combatPhase == null) { LastResult = "error: CombatPhase unavailable (is a fight in progress?)"; return; }

                MethodInfo nextTurn = null;
                foreach (MethodInfo m in combatPhase.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_nextTurn", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 1) { nextTurn = m; break; }
                }
                if (nextTurn == null) { LastResult = "error: CombatPhase._nextTurn(Boolean) not found"; return; }

                string activeBefore = ActiveEntityGuid(null);
                // Driven through CombatPhase, not CombatHelper.NextTurn: the helper mutates turn
                // order without the timeline refresh, engage and camera work that wraps it, which
                // leaves the UI showing a different active character than the state does.
                nextTurn.Invoke(combatPhase, new object[] { false });

                LastResult = "activeBefore=" + activeBefore + " activeAfter=" + ActiveEntityGuid(null)
                    + "\nNOTE: _nextTurn returns a Task that is not awaited; re-read the snapshot.";
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: _nextTurn threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_combat_end_turn threw: " + ex.Message;
            }
        }

        /// <summary>
        /// crucible_win_combat — win the current fight, with loot.
        ///
        /// Invokes the game's own EndPhase debug command, whose body removes every living enemy from
        /// CombatState.Entities before ending combat, so the victory test passes. This is NOT the
        /// same as CombatState.EndCombatEarly, which stops the fight with enemies alive and
        /// therefore evaluates as a loss.
        /// </summary>
        public static void CrucibleWinCombat()
        {
            LastResult = null;
            try
            {
                string before = "route=" + ReadRoute() + " combatants=" + CountCombatants();

                string execError;
                bool ran = GameBridge.Exec("EndPhase", new string[0], out execError);

                LastResult = "EndPhase invoked=" + ran
                    + (execError == null ? "" : " error=" + execError)
                    + "\nbefore: " + before
                    + "\nafter:  route=" + ReadRoute() + " combatants=" + CountCombatants()
                    + "\nNOTE: the transition is asynchronous; re-read state after a few seconds.";
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_win_combat threw: " + ex.Message;
            }
        }

        // ============================================================== helpers

        private static bool TryGetCombatState(out object combatState, out string error)
        {
            combatState = null;
            error = null;

            object gameRun = GameRun();
            if (gameRun == null) { error = "no run loaded"; return false; }

            combatState = PartyAccess.ReadMember(gameRun, "CombatState");
            if (combatState == null) { error = "GameRunData.CombatState is null -- no fight in progress"; return false; }
            return true;
        }

        private static object GameRun()
        {
            object env = GameBridge.GetEnv();
            return env == null ? null : PartyAccess.ReadMember(env, "GameRun");
        }

        private static object FindCombatPhase()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                FieldInfo routerField = routerHelper == null ? null : AccessTools.Field(routerHelper, "_router");
                object router = routerField == null ? null : routerField.GetValue(null);
                return router == null ? null : PartyAccess.ReadMember(router, "_combatPhase");
            }
            catch (Exception) { return null; }
        }

        /// <summary>Resolves an entity by guid, defaulting to whichever character's turn it is.</summary>
        private static bool ResolveEntity(string entityGuid, out object entity, out string error)
        {
            entity = null;
            error = null;

            object combatState;
            if (!TryGetCombatState(out combatState, out error)) return false;

            bool wantActive = string.IsNullOrEmpty(entityGuid) || entityGuid.Trim() == "-";
            if (wantActive)
            {
                object combatPhase = FindCombatPhase();
                entity = combatPhase == null ? null : PartyAccess.ReadMember(combatPhase, "_activeCharacterEntity");
                if (entity != null) return true;

                IEnumerable roundEntities = PartyAccess.ReadMember(combatState, "RoundEntities") as IEnumerable;
                if (roundEntities != null)
                {
                    foreach (object candidate in roundEntities) { entity = candidate; break; }
                }
                if (entity == null) { error = "no active entity (is a fight in progress?)"; return false; }
                return true;
            }

            string wanted = entityGuid.Trim();
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) { error = "CombatState.Entities is not enumerable"; return false; }
            foreach (object candidate in entities)
            {
                object guid = PartyAccess.ReadMember(candidate, "Guid");
                if (guid != null && string.Equals(guid.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    entity = candidate;
                    return true;
                }
            }
            error = "no combatant with guid '" + wanted + "'";
            return false;
        }

        private static bool TryGetAbilities(object entity, out List<object> abilities, out string error)
        {
            abilities = new List<object>();
            error = null;

            Type combatHelper = AccessTools.TypeByName("CombatHelper");
            if (combatHelper == null) { error = "CombatHelper not found"; return false; }

            MethodInfo get = null;
            foreach (MethodInfo m in combatHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!string.Equals(m.Name, "GetAbilities", StringComparison.Ordinal)) continue;
                if (m.GetParameters().Length == 8) { get = m; break; }
            }
            if (get == null) { error = "CombatHelper.GetAbilities(8 args) not found"; return false; }

            try
            {
                // Widest query: a narrow one hides abilities, and an absent ability is
                // indistinguishable from one that does not exist.
                object result = get.Invoke(null, new object[] { entity, false, true, true, false, false, true, true });
                IEnumerable list = result as IEnumerable;
                if (list == null) { error = "GetAbilities returned a non-enumerable"; return false; }
                foreach (object item in list) if (item != null) abilities.Add(item);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = "GetAbilities threw: " + root.Message;
                return false;
            }
        }

        private static object BuildTypedList(Type listType, List<object> items)
        {
            try
            {
                if (!listType.IsGenericType) return items;
                object typed = Activator.CreateInstance(listType);
                MethodInfo add = listType.GetMethod("Add");
                foreach (object item in items) add.Invoke(typed, new object[] { item });
                return typed;
            }
            catch (Exception) { return items; }
        }

        private static string ActiveEntityGuid(object combatState)
        {
            object combatPhase = FindCombatPhase();
            object active = combatPhase == null ? null : PartyAccess.ReadMember(combatPhase, "_activeCharacterEntity");
            if (active != null) return Str(PartyAccess.ReadMember(active, "Guid"));

            if (combatState == null)
            {
                string ignored;
                if (!TryGetCombatState(out combatState, out ignored)) return "(none)";
            }
            IEnumerable roundEntities = PartyAccess.ReadMember(combatState, "RoundEntities") as IEnumerable;
            if (roundEntities == null) return "(none)";
            foreach (object first in roundEntities) return Str(PartyAccess.ReadMember(first, "Guid"));
            return "(none)";
        }

        private static string OccupantAt(object combatState, object position)
        {
            if (position == null) return "(none)";
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) return "(none)";
            foreach (object entity in entities)
            {
                object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                object venue = PartyAccess.FindComponent(entity, "VenueComponent");
                if (character == null || venue == null) continue;
                object tile = PartyAccess.ReadMember(venue, "TilePosition");
                if (tile != null && tile.ToString() == position.ToString())
                    return Str(PartyAccess.ReadMember(character, "DisplayName")) + "/" + Str(PartyAccess.ReadMember(character, "ConfigName"));
            }
            return "(empty)";
        }

        private static string SnapshotHealth()
        {
            object combatState;
            string error;
            if (!TryGetCombatState(out combatState, out error)) return "(unavailable)";
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) return "(unavailable)";

            List<string> parts = new List<string>();
            foreach (object entity in entities)
            {
                object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                if (character == null) continue;
                parts.Add(Str(PartyAccess.ReadMember(character, "DisplayName")) + "="
                    + Str(PartyAccess.ReadMember(character, "CurrentHealth")));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(", ", parts.ToArray());
        }

        private static int CountCombatants()
        {
            object combatState;
            string error;
            if (!TryGetCombatState(out combatState, out error)) return -1;
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) return -1;
            int n = 0;
            foreach (object entity in entities) if (PartyAccess.FindComponent(entity, "CombatComponent") != null) n++;
            return n;
        }

        private static bool IsDead(object entity)
        {
            try
            {
                Type characterHelper = AccessTools.TypeByName("CharacterHelper");
                MethodInfo isDead = characterHelper == null ? null : AccessTools.Method(characterHelper, "IsDead");
                if (isDead == null) return false;
                object value = isDead.Invoke(null, new object[] { entity });
                return value is bool && (bool)value;
            }
            catch (Exception) { return false; }
        }

        private static string ReadRoute()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                MethodInfo get = routerHelper == null ? null : AccessTools.Method(routerHelper, "GetCurrentRoute");
                object value = get == null ? null : get.Invoke(null, null);
                return value == null ? "(unknown)" : value.ToString();
            }
            catch (Exception) { return "(unreadable)"; }
        }

        private static string Str(object value)
        {
            return value == null ? "(null)" : value.ToString();
        }
    }
}
