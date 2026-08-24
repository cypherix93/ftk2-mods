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
            Register("crucible_combat_wipe_enemies", "CrucibleCombatWipeEnemies",
                new List<string> { "group (default 1 = enemies)" });
            Register("crucible_godmode", "CrucibleGodmode", new List<string> { "on|off|status" });
            Register("crucible_combat_restore_actions", "CrucibleCombatRestoreActions",
                new List<string> { "group (default 0 = party)" });
            Register("crucible_combat_spawn", "CrucibleCombatSpawn",
                new List<string> { "characterConfig", "count (default 1)", "group (default 1 = enemies)" });
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
                              .Append(" dead=").Append(IsDead(entity))
                              // Statuses belong here: crucible_status_add could APPLY one but the
                              // snapshot never showed it, so "does the duration tick down" -- the
                              // only question that matters for a status -- was unanswerable.
                              .Append(" statuses=").Append(CharacterCommands.DescribeStatuses(entity));
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

        // ============================================================== crucible_combat_wipe_enemies

        /// <summary>
        /// crucible_combat_wipe_enemies [group] — kill every combatant in a group, instantly.
        ///
        /// The fast way to end a test fight once the thing under test has been observed. Exists
        /// because <c>crucible_kill_all</c> does NOT work in combat: it walks
        /// <c>GameRunData.Entities</c>, and the fight runs on <c>CombatState.Entities</c>, which is a
        /// different set of objects. Measured 2026-08-24 against a 74-combatant fight, it reported
        /// <c>entities=74 deadBefore=0 deadAfter=0 changed=False</c> -- a verb that looks like it
        /// works and does nothing.
        ///
        /// Health is written through <c>CharacterComponent.CurrentHealth</c> and then the game's own
        /// <c>CharacterHelper.TryKillCharacter</c> is invoked so death bookkeeping (loot, quest
        /// credit, turn-order removal) runs the way it does in a real fight, rather than leaving
        /// zero-HP entities standing.
        ///
        /// Group 0 is the player party, so the default of 1 is deliberate: the obvious typo should
        /// not wipe the party being tested.
        /// </summary>
        public static void CrucibleCombatWipeEnemies(string group)
        {
            LastResult = null;
            try
            {
                int wanted;
                if (!int.TryParse((group ?? "1").Trim(), out wanted)) wanted = 1;

                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) { LastResult = "error: " + error; return; }

                IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
                if (entities == null) { LastResult = "error: CombatState.Entities is not enumerable"; return; }

                Type helper = AccessTools.TypeByName("CharacterHelper");
                MethodInfo tryKill = helper == null ? null : AccessTools.Method(helper, "TryKillCharacter");
                MethodInfo isDead = helper == null ? null : AccessTools.Method(helper, "IsDead");

                int seen = 0, killed = 0, alreadyDead = 0, skippedGroup = 0;
                StringBuilder detail = new StringBuilder();

                foreach (object entity in entities)
                {
                    if (entity == null) continue;
                    object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                    object combat = PartyAccess.FindComponent(entity, "CombatComponent");
                    if (character == null || combat == null) continue;

                    object groupIndex = PartyAccess.ReadMember(character, "GroupIndex");
                    if (groupIndex == null || Convert.ToInt32(groupIndex) != wanted) { skippedGroup++; continue; }
                    seen++;

                    bool dead = false;
                    if (isDead != null)
                    {
                        try { object d = isDead.Invoke(null, new object[] { entity }); dead = d is bool && (bool)d; }
                        catch (Exception) { }
                    }
                    if (dead) { alreadyDead++; continue; }

                    FieldInfo hp = AccessTools.Field(character.GetType(), "CurrentHealth");
                    if (hp != null) hp.SetValue(character, 0);

                    if (tryKill != null)
                    {
                        try { tryKill.Invoke(null, new object[] { entity }); }
                        catch (TargetInvocationException) { }
                    }
                    killed++;
                    if (detail.Length < 400)
                        detail.Append("\n  ").Append(Str(PartyAccess.ReadMember(character, "ConfigName")));
                }

                LastResult = "group=" + wanted + " inGroup=" + seen + " killed=" + killed
                    + " alreadyDead=" + alreadyDead + " otherGroups=" + skippedGroup
                    + " changed=" + (killed > 0) + detail
                    + "\nNOTE: death bookkeeping is asynchronous. Re-read crucible_combat_snapshot;"
                    + "\n      the wave may advance rather than the fight ending outright.";
                if (_log != null) _log.LogInfo("crucible_combat_wipe_enemies: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_combat_wipe_enemies threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_combat_restore_actions

        /// <summary>
        /// crucible_combat_restore_actions [group] — refill primary/secondary actions so a character
        /// can act again this turn.
        ///
        /// Needed because an ability that is out of actions does not fail loudly: crucible_use_ability
        /// reports the ability name and target exactly as it does on success, and the only tell is
        /// <c>pa=0</c> in the snapshot and <c>usable=False</c> in crucible_list_abilities. A trait test
        /// that lost its action to an earlier auto-play therefore reads as "the trait never fired".
        /// Measured 2026-08-24: the Vampiric attacked with pa=0, resultCount was 0, and nothing was
        /// logged at all.
        ///
        /// Group 0 (the party) by default, for the same reason the wipe verb defaults to 1: the
        /// obvious typo should not hand the enemy free turns.
        /// </summary>
        public static void CrucibleCombatRestoreActions(string group)
        {
            LastResult = null;
            try
            {
                int wanted;
                if (!int.TryParse((group ?? "0").Trim(), out wanted)) wanted = 0;

                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) { LastResult = "error: " + error; return; }

                IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
                if (entities == null) { LastResult = "error: CombatState.Entities is not enumerable"; return; }

                StringBuilder sb = new StringBuilder();
                int restored = 0;

                foreach (object entity in entities)
                {
                    if (entity == null) continue;
                    object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                    object combat = PartyAccess.FindComponent(entity, "CombatComponent");
                    if (character == null || combat == null) continue;

                    object groupIndex = PartyAccess.ReadMember(character, "GroupIndex");
                    if (groupIndex == null || Convert.ToInt32(groupIndex) != wanted) continue;

                    // The per-turn allowance lives on the CHARACTER's stats (PA/SA); CombatComponent
                    // holds what is left of it this turn.
                    int pa = ReadStatOrDefault(character, "PA", 1);
                    int sa = ReadStatOrDefault(character, "SA", 1);

                    FieldInfo primary = AccessTools.Field(combat.GetType(), "PrimaryActions");
                    FieldInfo secondary = AccessTools.Field(combat.GetType(), "SecondaryActions");
                    if (primary == null || secondary == null) continue;

                    object beforeP = primary.GetValue(combat);
                    object beforeS = secondary.GetValue(combat);
                    primary.SetValue(combat, pa);
                    secondary.SetValue(combat, sa);
                    restored++;

                    sb.Append("\n  ").Append(Str(PartyAccess.ReadMember(character, "ConfigName")))
                      .Append(" pa ").Append(Str(beforeP)).Append("->").Append(pa)
                      .Append(" sa ").Append(Str(beforeS)).Append("->").Append(sa);
                }

                LastResult = "group=" + wanted + " restored=" + restored + sb;
                if (_log != null) _log.LogInfo("crucible_combat_restore_actions: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_combat_restore_actions threw: " + ex.Message;
            }
        }

        /// <summary>A character stat, or a default when it cannot be read. PA/SA are always 1 on every
        /// shipped class, so 1 is a safe floor rather than a guess.</summary>
        private static int ReadStatOrDefault(object character, string stat, int fallback)
        {
            try
            {
                object stats = PartyAccess.ReadMember(character, "Stats");
                IDictionary map = stats as IDictionary;
                if (map != null && map.Contains(stat)) return Convert.ToInt32(map[stat]);
            }
            catch (Exception) { }
            return fallback;
        }

        // ============================================================== crucible_combat_spawn

        /// <summary>
        /// crucible_combat_spawn &lt;characterConfig&gt; [count] [group] — drop creatures straight into
        /// the CURRENT fight.
        ///
        /// The overworld spawner (crucible_debug_spawn) starts a NEW encounter; this adds combatants
        /// to the one already running, which is what a test needs when a fight is too small to
        /// exercise something. A Bond-gated summon that unlocks at 3 and 6 kills cannot be reached in
        /// a fight that ships one spider.
        ///
        /// Placement goes through the game's own <c>CombatHelper.TryCreateSummon</c>, and the tile it
        /// lands on decides allegiance -- <c>TryCreateSummon</c> copies the tile's GroupIndex onto the
        /// new character. So group 1 spawns need a free ENEMY tile and group 0 spawns a free PLAYER
        /// one; asking for more than there are free tiles places as many as fit and says so.
        /// </summary>
        public static void CrucibleCombatSpawn(string characterConfig, string countArg, string groupArg)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(characterConfig))
                {
                    LastResult = "error: usage: crucible_combat_spawn <characterConfig> [count] [group]";
                    return;
                }
                characterConfig = characterConfig.Trim();

                int count;
                if (!int.TryParse((countArg ?? "1").Trim(), out count) || count < 1) count = 1;
                if (count > 12) count = 12;      // the grid is small; more would silently fail anyway

                int group;
                if (!int.TryParse((groupArg ?? "1").Trim(), out group)) group = 1;

                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) { LastResult = "error: " + error; return; }

                object gameRun = RunAccessGameRun();
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                Type combatHelper = AccessTools.TypeByName("CombatHelper");
                MethodInfo tryCreate = null;
                foreach (MethodInfo m in combatHelper == null
                    ? new MethodInfo[0] : combatHelper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "TryCreateSummon", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length >= 8) { tryCreate = m; break; }
                }
                if (tryCreate == null) { LastResult = "error: CombatHelper.TryCreateSummon not found"; return; }

                StringBuilder sb = new StringBuilder();
                int placed = 0;

                // Route through the game's own ADD_CHARACTER verb rather than calling
                // TryCreateSummon directly.
                //
                // CombatHelper.ApplyAction's ADD_CHARACTER case does the ENTIRE nine-step join --
                // create, place on the tile, build the actor GameObject, set the static combat
                // position, position the transform, face the target, set initiative, reset actions,
                // refresh the UI. Calling TryCreateSummon on its own returns a valid Entity and does
                // none of the rest, which is how spawned bees ended up as invisible combatants that
                // wedged the turn order.
                //
                // This is the same seam ClassForge's SUMMON effect uses, and it is why a
                // recipe-summoned partner appears correctly while a hand-rolled one does not.
                Type combatHelperType = AccessTools.TypeByName("CombatHelper");
                MethodInfo applyAction = combatHelperType == null
                    ? null : AccessTools.Method(combatHelperType, "ApplyAction");
                if (applyAction == null) { LastResult = "error: CombatHelper.ApplyAction not found"; return; }

                object origin = ActivePartyMember();
                if (origin == null) { LastResult = "error: no party member to attribute the spawn to"; return; }

                for (int i = 0; i < count; i++)
                {
                    // The TILE decides allegiance: ADD_CHARACTER reads GroupIndex off the target's
                    // VenueTileComponent and stamps it onto the new character.
                    object tile = FindFreeTileInGroup(combatState, group);
                    if (tile == null)
                    {
                        sb.Append("\n  no free tile left on group ").Append(group)
                          .Append(" after ").Append(placed);
                        break;
                    }

                    object venue = PartyAccess.FindComponent(tile, "VenueComponent");
                    object pos = PartyAccess.ReadMember(venue, "TilePosition");

                    // Snapshot the roster so the newly created entity can be identified afterwards.
                    HashSet<object> before = SnapshotRoster(combatState);

                    string spawnError;
                    if (!InvokeAddCharacter(applyAction, origin, tile, characterConfig, out spawnError))
                    {
                        sb.Append("\n  ADD_CHARACTER refused at ").Append(Str(pos))
                          .Append(" -- ").Append(spawnError);
                        break;
                    }

                    placed++;
                    sb.Append("\n  placed at ").Append(Str(pos));

                    // Build the actor GameObject for whatever just joined.
                    //
                    // ADD_CHARACTER does NOT create it. CharacterVisualHelper's
                    // CHARACTER_ADDED_SMOKE case opens with `pActorGameObjects[entity]` -- it LOOKS
                    // UP an actor that must already exist, then SetActive(true)s it and plays the
                    // appear animation. Creation is the caller's job, which is why CombatPhase's own
                    // summon branches do
                    //   _gameObjectMaps.FromCharacter[e] = CreateActorGameObject(...)
                    // immediately before rendering the result. Skip it and the combatant is real,
                    // takes turns, and is never drawn -- FromCharacter simply has no entry for it.
                    object spawned = NewestRosterEntry(combatState, before);
                    if (spawned == null) sb.Append(" [entity not identified; no visual]");
                    else sb.Append(' ').Append(EnsureActorVisual(spawned, pos));
                }

                // Redraw the turn-order banner. RoundEntities is already correct by this point --
                // the spawn adds to it -- but CombatPhase._refreshTimeline is only called on turn
                // transitions, so a spawned creature has no banner icon until something else
                // happens to trigger a redraw. (Clicking its tile does it, which is how this was
                // spotted: the banners appeared on a click, not on the spawn.)
                string timeline = RefreshTimeline();

                LastResult = "config=" + characterConfig + " group=" + group
                    + " requested=" + count + " placed=" + placed + sb + timeline
                    + "\nNOTE: routed through the game's own ADD_CHARACTER verb, so placement,"
                    + "\n      initiative, actions, the 3D model and the UI refresh are all done by"
                    + "\n      the game itself -- the same seam ClassForge's SUMMON effect uses."
                    + "\n      The 3D MODEL is created by the renderer from a CHARACTER_ADDED ability"
                    + "\n      result, which only exists inside an ability resolution -- a spawn made"
                    + "\n      outside one is a live combatant that may not be drawn until the next"
                    + "\n      redraw. Assert on crucible_combat_snapshot, not on the screenshot.";
                if (_log != null) _log.LogInfo("crucible_combat_spawn: " + LastResult);
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: TryCreateSummon threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_combat_spawn threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Fires <c>CombatHelper.ApplyAction(ADD_CHARACTER, {Type, Value}, origin, tile, ...)</c>.
        ///
        /// The payload must be a <c>JsonElement</c>: the native case does
        /// <c>((JsonElement)pActionArgs).GetRawText()</c> unconditionally and an
        /// <c>AddCharacterAction</c> instance throws InvalidCastException. The roll must be PERFECT,
        /// because ADD_CHARACTER opens with <c>if (pRollData.Status != PERFECT) break;</c> and would
        /// otherwise silently do nothing.
        /// </summary>
        private static bool InvokeAddCharacter(MethodInfo applyAction, object origin, object tile,
            string characterConfig, out string error)
        {
            error = null;
            try
            {
                string json = "{\"Type\":\"SPECIFIC\",\"Value\":\"" + characterConfig + "\"}";
                Type jsonDocument = AccessTools.TypeByName("System.Text.Json.JsonDocument");
                MethodInfo parse = jsonDocument == null ? null : AccessTools.Method(jsonDocument, "Parse",
                    new Type[] { typeof(string), AccessTools.TypeByName("System.Text.Json.JsonDocumentOptions") });
                if (parse == null)
                {
                    foreach (MethodInfo m in jsonDocument == null
                        ? new MethodInfo[0] : jsonDocument.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (!string.Equals(m.Name, "Parse", StringComparison.Ordinal)) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType == typeof(string)) { parse = m; break; }
                    }
                }
                if (parse == null) { error = "JsonDocument.Parse not found"; return false; }

                ParameterInfo[] pps = parse.GetParameters();
                object[] parseArgs = new object[pps.Length];
                parseArgs[0] = json;
                for (int i = 1; i < pps.Length; i++)
                    parseArgs[i] = pps[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(pps[i].ParameterType) : null;

                object doc = parse.Invoke(null, parseArgs);
                object root = PartyAccess.ReadMember(doc, "RootElement");
                if (root == null) { error = "JsonDocument.RootElement unreadable"; return false; }

                ParameterInfo[] ps2 = applyAction.GetParameters();
                object[] args = new object[ps2.Length];
                for (int i = 0; i < ps2.Length; i++)
                {
                    Type t = ps2[i].ParameterType;
                    string n = ps2[i].Name ?? "";

                    if (n == "pOrigin") args[i] = origin;
                    else if (n == "pTarget" || n == "pPrimaryTarget") args[i] = tile;
                    else if (n == "pActionArgs") args[i] = root;
                    else if (n == "pAbilityName") args[i] = "CF_RECIPE_EFFECT";
                    else if (n == "pAction") args[i] = Enum.Parse(t.IsByRef ? t.GetElementType() : t, "ADD_CHARACTER");
                    else if (n == "pEnv") args[i] = GameBridge.GetEnv();
                    else if (n == "pGameRandom") args[i] = PartyAccess.ResolveGameRandom();
                    else if (n == "pRollData") args[i] = BuildPerfectRoll(t);
                    else if (n == "pParty") { List<object> party; string e2;
                        PartyAccess.TryGetParty(out party, out e2);
                        IList typed = (IList)Activator.CreateInstance(t);
                        if (party != null) foreach (object a in party) typed.Add(a);
                        args[i] = typed; }
                    else if (n == "pResults") args[i] = Activator.CreateInstance(t);
                    else if (t.IsByRef) args[i] = Activator.CreateInstance(t.GetElementType());
                    else if (t == typeof(decimal)) args[i] = 1m;
                    else if (t == typeof(bool)) args[i] = n == "pIsCenterTarget";
                    else if (t.IsValueType) args[i] = Activator.CreateInstance(t);
                    else args[i] = null;
                }

                applyAction.Invoke(null, args);

                // ApplyAction only EMITS the CHARACTER_ADDED result; the phase is what draws it, via
                // CharacterVisualHelper.RenderAbilityResults(pResults, entities, gameObjectMaps, root).
                // Passing a results list nobody renders is why a correctly-spawned combatant stays
                // invisible: the simulation is complete and the presentation never ran.
                int resultsIndex = -1;
                for (int i = 0; i < ps2.Length; i++)
                    if (ps2[i].Name == "pResults") { resultsIndex = i; break; }
                if (resultsIndex >= 0) RenderResults(args[resultsIndex]);

                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root2 = ex; while (root2.InnerException != null) root2 = root2.InnerException;
                error = root2.GetType().Name + ": " + root2.Message;
                return false;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>Identity set of the current combat roster, for diffing after a spawn.</summary>
        private static HashSet<object> SnapshotRoster(object combatState)
        {
            HashSet<object> set = new HashSet<object>();
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities != null) foreach (object e in entities) if (e != null) set.Add(e);
            return set;
        }

        /// <summary>The one roster entry that was not there before.</summary>
        private static object NewestRosterEntry(object combatState, HashSet<object> before)
        {
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) return null;
            object found = null;
            foreach (object e in entities) if (e != null && !before.Contains(e)) found = e;
            return found;
        }

        /// <summary>
        /// Builds and places the actor GameObject for one newly joined combatant.
        ///
        /// <para>Deliberately NOT <c>VenueViewHelper.LoadCharacterEntitiesToVenueGrid</c>. That
        /// method does draw the actor correctly, but it is the venue's whole-grid loader: handed the
        /// full tile list it RE-LAYS-OUT the venue, and doing that mid-fight left the combat grid
        /// unrendered. A test verb must not disturb a running fight to add one creature.</para>
        ///
        /// <para>So this does only what the game's own summon branch does for a single entity:
        /// create the actor, register it in <c>FromCharacter</c>, and set its transform to the
        /// tile-average world position. The maths is copied from CombatPhase's
        /// <c>positionActorGameObject</c> LOCAL FUNCTION, which reflection cannot call:</para>
        /// <code>
        /// actor.transform.position = CharacterVisualHelper.GetCharacterRootPosition(
        ///     e, VenueViewHelper.GetAveragePositionOfTiles(e.VenueComponent.OccupiedTiles, maps.FromTile)
        ///        + diorama.PlayerOffset);
        /// </code>
        /// <para>Both helpers are OVERLOADED, so they are selected by parameter count -- a name-only
        /// lookup throws AmbiguousMatchException, which is how this failed the first time.</para>
        /// </summary>
        private static string EnsureActorVisual(object entity, object pos)
        {
            try
            {
                object phase = CombatPhaseInstance();
                if (phase == null) return "[no phase]";

                object maps = PartyAccess.ReadMember(phase, "_gameObjectMaps");
                IDictionary fromCharacter = PartyAccess.ReadMember(maps, "FromCharacter") as IDictionary;
                if (fromCharacter == null) return "[no FromCharacter]";
                if (fromCharacter.Contains(entity)) return "[drawn]";

                object canvas = PartyAccess.ReadMember(phase, "_canvas3D");
                object parent = PartyAccess.ReadMember(canvas, "transform");

                Type visualHelper = AccessTools.TypeByName("CharacterVisualHelper");
                MethodInfo create = FindStatic(visualHelper, "CreateActorGameObject", 4);
                if (create == null) create = FindStatic(visualHelper, "CreateActorGameObject", 3);
                if (create == null) return "[no CreateActorGameObject]";

                ParameterInfo[] cps = create.GetParameters();
                object[] cargs = new object[cps.Length];
                cargs[0] = entity;
                cargs[1] = parent;
                cargs[2] = Activator.CreateInstance(cps[2].ParameterType);
                for (int i = 3; i < cps.Length; i++)
                    cargs[i] = cps[i].ParameterType == typeof(bool) ? (object)false
                             : (cps[i].HasDefaultValue ? cps[i].DefaultValue : null);

                object actor = create.Invoke(null, cargs);
                if (actor == null) return "[CreateActorGameObject returned null]";
                fromCharacter[entity] = actor;

                string placement = PlaceActor(phase, maps, entity, actor, pos);

                // The phase keeps its OWN cached rosters, and targeting, hover, the HUD and the
                // AI all read those rather than CombatState.Entities. A creature missing from them
                // is on the board but cannot be clicked, hovered or targeted -- which presents as
                // "combat is broken" rather than as a missing creature.
                string rosters = RegisterWithPhase(phase, entity);

                // Face it the right way and start it idling. Without these it stands in its spawn
                // rotation with no animation, which reads as a frozen model.
                string presentation = FaceAndIdle(phase, maps, entity, actor);

                string notes = string.Concat(placement ?? "", rosters ?? "", presentation ?? "");
                return notes.Length == 0 ? "[drawn]" : "[drawn," + notes.Trim(',') + "]";
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "[visual threw " + root.GetType().Name + ": " + root.Message + "]";
            }
            catch (Exception ex)
            {
                return "[visual threw " + ex.GetType().Name + "]";
            }
        }

        /// <summary>
        /// Adds an entity to the CombatPhase's cached side rosters (<c>_allyEntities</c> /
        /// <c>_enemyEntities</c>).
        ///
        /// These are separate from <c>CombatState.Entities</c> and are what the phase actually reads
        /// for targeting, hover highlighting, the HUD and AI target selection. A combatant present
        /// in CombatState but absent here exists, takes turns and can be damaged, yet cannot be
        /// clicked or hovered -- the symptom is "combat is broken", not "a creature is missing".
        /// </summary>
        private static string RegisterWithPhase(object phase, object entity)
        {
            try
            {
                object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                object groupIndex = PartyAccess.ReadMember(character, "GroupIndex");
                if (groupIndex == null) return ",rosters:noGroup";
                bool isAlly = Convert.ToInt32(groupIndex) == 0;

                string field = isAlly ? "_allyEntities" : "_enemyEntities";
                FieldInfo listField = AccessTools.Field(phase.GetType(), field);
                if (listField == null) return ",rosters:no" + field;

                IList list = listField.GetValue(phase) as IList;
                if (list == null) return ",rosters:null" + field;
                if (!list.Contains(entity)) list.Add(entity);
                return null;
            }
            catch (Exception ex) { return ",rosters:" + ex.GetType().Name; }
        }

        /// <summary>
        /// Turns the actor to face the opposing side and starts its combat idle.
        ///
        /// <c>VenueViewHelper.CharacterLookAtTarget</c> is what the game's own summon branch uses;
        /// without it the model keeps its spawn rotation and faces the wrong way. Starting
        /// IDLE_COMBAT matters for the same reason -- a freshly built actor has no animation running
        /// and reads as frozen.
        /// </summary>
        private static string FaceAndIdle(object phase, object maps, object entity, object actor)
        {
            StringBuilder notes = new StringBuilder();
            try
            {
                // Face something on the other side.
                object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                object groupIndex = PartyAccess.ReadMember(character, "GroupIndex");
                int myGroup = groupIndex == null ? 0 : Convert.ToInt32(groupIndex);

                object opponent = null;
                object combatState;
                string error;
                if (TryGetCombatState(out combatState, out error))
                {
                    IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
                    if (entities != null)
                        foreach (object e in entities)
                        {
                            if (e == null || e == entity) continue;
                            object c = PartyAccess.FindComponent(e, "CharacterComponent");
                            if (c == null) continue;
                            object g = PartyAccess.ReadMember(c, "GroupIndex");
                            if (g == null || Convert.ToInt32(g) == myGroup) continue;
                            // The parameter is pTileTarget: CharacterLookAtTarget wants the TILE the
                            // opponent stands on, not the opponent itself. Passing the character
                            // throws, which is why facing silently failed.
                            object oppVenue = PartyAccess.FindComponent(e, "VenueComponent");
                            object oppPos = PartyAccess.ReadMember(oppVenue, "TilePosition");
                            opponent = FindTileAt(combatState, oppPos);
                            if (opponent != null) break;
                        }
                }

                Type venueView = AccessTools.TypeByName("VenueViewHelper");
                // CharacterLookAtTarget(pCharacter, pTileTarget, pTryRotateForward, pGameObjectMaps,
                // maxRotationDiff = 90f) -- five parameters, the last defaulted.
                MethodInfo lookAt = FindStatic(venueView, "CharacterLookAtTarget", 5);
                if (lookAt == null) lookAt = FindStatic(venueView, "CharacterLookAtTarget", 4);
                if (lookAt != null && opponent != null)
                {
                    try
                    {
                        ParameterInfo[] lp = lookAt.GetParameters();
                        object[] largs = new object[lp.Length];
                        largs[0] = entity; largs[1] = opponent; largs[2] = true; largs[3] = maps;
                        for (int i = 4; i < lp.Length; i++)
                            largs[i] = lp[i].HasDefaultValue ? lp[i].DefaultValue
                                     : (lp[i].ParameterType.IsValueType
                                        ? Activator.CreateInstance(lp[i].ParameterType) : null);
                        lookAt.Invoke(null, largs);
                    }
                    catch (Exception) { notes.Append(",face:failed"); }
                }
                else if (lookAt == null) notes.Append(",face:noLookAt");

                // Start the combat idle.
                // PlayAnimation(eAnimationTypes, eAnimationIdentities = STANDARD,
                //               eAnimationTypes pFollowUp = NONE, AnimSequence.Data = null)
                // -- four parameters, three defaulted. Pick the overload whose FIRST parameter is
                // the animation enum and whose second is NOT a float/out (those are the timing
                // variants).
                MethodInfo play = null;
                foreach (MethodInfo m in actor.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "PlayAnimation", StringComparison.Ordinal)) continue;
                    ParameterInfo[] aps = m.GetParameters();
                    if (aps.Length == 0 || !aps[0].ParameterType.IsEnum) continue;
                    if (aps.Length > 1 && (aps[1].ParameterType == typeof(float)
                                           || aps[1].ParameterType.IsByRef)) continue;
                    play = m; break;
                }
                if (play != null)
                {
                    try
                    {
                        ParameterInfo[] aps = play.GetParameters();
                        object[] aargs = new object[aps.Length];
                        aargs[0] = Enum.Parse(aps[0].ParameterType, "IDLE_COMBAT");
                        for (int i = 1; i < aps.Length; i++)
                            aargs[i] = aps[i].HasDefaultValue ? aps[i].DefaultValue
                                     : (aps[i].ParameterType.IsValueType
                                        ? Activator.CreateInstance(aps[i].ParameterType) : null);
                        play.Invoke(actor, aargs);
                    }
                    catch (Exception) { notes.Append(",idle:failed"); }
                }
                else notes.Append(",idle:noPlayAnimation");

                return notes.Length == 0 ? null : notes.ToString();
            }
            catch (Exception ex) { return ",present:" + ex.GetType().Name; }
        }

        /// <summary>The tile entity standing at a given position.</summary>
        private static object FindTileAt(object combatState, object pos)
        {
            if (pos == null) return null;
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) return null;
            string wanted = pos.ToString();
            foreach (object e in entities)
            {
                if (e == null || PartyAccess.FindComponent(e, "VenueTileComponent") == null) continue;
                object venue = PartyAccess.FindComponent(e, "VenueComponent");
                object p = PartyAccess.ReadMember(venue, "TilePosition");
                if (p != null && p.ToString() == wanted) return e;
            }
            return null;
        }


        /// <summary>
        /// Invokes <c>CombatPhase._refreshTimeline()</c>, which rebuilds the turn-order banner from
        /// <c>CombatState.RoundEntities</c>. Returns a note only when it could not be done.
        /// </summary>
        private static string RefreshTimeline()
        {
            try
            {
                object phase = CombatPhaseInstance();
                if (phase == null) return "\n  (timeline not refreshed: no CombatPhase)";

                MethodInfo refresh = AccessTools.Method(phase.GetType(), "_refreshTimeline");
                if (refresh == null) return "\n  (timeline not refreshed: _refreshTimeline not found)";

                refresh.Invoke(phase, null);
                return null;
            }
            catch (Exception ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "\n  (timeline refresh threw: " + root.GetType().Name + ")";
            }
        }

        /// <summary>A public static overload picked by parameter count, avoiding AmbiguousMatchException.</summary>
        private static MethodInfo FindStatic(Type type, string name, int parameterCount)
        {
            if (type == null) return null;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                if (m.GetParameters().Length == parameterCount) return m;
            }
            return null;
        }

        /// <summary>Sets the actor transform to its tile's world position. Null on success.</summary>
        private static string PlaceActor(object phase, object maps, object entity, object actor, object pos)
        {
            try
            {
                object venue = PartyAccess.FindComponent(entity, "VenueComponent");
                IList occupied = venue == null ? null : PartyAccess.ReadMember(venue, "OccupiedTiles") as IList;
                if (occupied == null) return "noOccupiedTiles";
                // TryCreateSummon already fills this; belt-and-braces for other callers. An empty
                // list averages ZERO tiles and drops the model at the diorama origin.
                if (occupied.Count == 0 && pos != null) occupied.Add(pos);

                IDictionary fromTile = PartyAccess.ReadMember(maps, "FromTile") as IDictionary;
                if (fromTile == null) return "noFromTile";

                Type venueView = AccessTools.TypeByName("VenueViewHelper");
                MethodInfo average = FindStatic(venueView, "GetAveragePositionOfTiles", 2);
                if (average == null) return "noGetAveragePositionOfTiles";
                object centre = average.Invoke(null, new object[] { occupied, fromTile });

                object diorama = PartyAccess.ReadMember(phase, "_diorama");
                object offset = diorama == null ? null : PartyAccess.ReadMember(diorama, "PlayerOffset");
                if (centre != null && offset != null)
                {
                    MethodInfo add = centre.GetType().GetMethod("op_Addition",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new Type[] { centre.GetType(), offset.GetType() }, null);
                    if (add != null) centre = add.Invoke(null, new object[] { centre, offset });
                }

                Type visualHelper = AccessTools.TypeByName("CharacterVisualHelper");
                MethodInfo rootPos = FindStatic(visualHelper, "GetCharacterRootPosition", 2);
                if (rootPos == null) return "noGetCharacterRootPosition";
                object world = rootPos.Invoke(null, new object[] { entity, centre });

                object transform = PartyAccess.ReadMember(actor, "transform");
                if (transform == null) return "noTransform";
                PropertyInfo positionProp = transform.GetType().GetProperty("position");
                if (positionProp == null) return "noPositionProperty";
                positionProp.SetValue(transform, world, null);
                return null;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "place:" + root.GetType().Name;
            }
            catch (Exception ex) { return "place:" + ex.GetType().Name; }
        }
        /// <summary>The live CombatPhase, which owns the game-object maps and the render helpers.</summary>
        private static object CombatPhaseInstance()
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

        /// <summary>
        /// Hands a results list to the combat renderer, which is what actually creates and places the
        /// actor for a CHARACTER_ADDED result. Fire-and-forget: it returns a Task and awaiting it on
        /// the game thread would deadlock the pump.
        /// </summary>
        private static void RenderResults(object results)
        {
            try
            {
                object phase = CombatPhaseInstance();
                if (phase == null || results == null) return;

                object maps = PartyAccess.ReadMember(phase, "_gameObjectMaps");
                object canvas = PartyAccess.ReadMember(phase, "_canvas2D");
                object root = canvas == null ? null : PartyAccess.ReadMember(canvas, "rootVisualElement");
                if (root == null) root = PartyAccess.ReadMember(phase, "_rootVisualElement");

                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) return;
                object entities = PartyAccess.ReadMember(combatState, "Entities");

                Type visualHelper = AccessTools.TypeByName("CharacterVisualHelper");
                MethodInfo render = null;
                foreach (MethodInfo m in visualHelper == null
                    ? new MethodInfo[0] : visualHelper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "RenderAbilityResults", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length >= 4) { render = m; break; }
                }
                if (render == null) return;

                ParameterInfo[] ps = render.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = results; args[1] = entities; args[2] = maps; args[3] = root;
                for (int i = 4; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                            : (ps[i].ParameterType.IsValueType
                               ? Activator.CreateInstance(ps[i].ParameterType) : null);

                render.Invoke(null, args);
            }
            catch (Exception) { /* presentation only -- never fail the spawn over it */ }
        }

        /// <summary>ADD_CHARACTER bails on anything but a PERFECT roll, so build one.</summary>
        private static object BuildPerfectRoll(Type rollType)
        {
            object roll = Activator.CreateInstance(rollType);
            FieldInfo status = AccessTools.Field(rollType, "Status");
            if (status != null) status.SetValue(roll, Enum.Parse(status.FieldType, "PERFECT"));
            FieldInfo value = AccessTools.Field(rollType, "Value");
            if (value != null && value.FieldType == typeof(decimal)) value.SetValue(roll, 1m);
            return roll;
        }

        /// <summary>Any living party member, used to attribute the spawn to a real origin.</summary>
        private static object ActivePartyMember()
        {
            List<object> party;
            string error;
            if (!PartyAccess.TryGetParty(out party, out error) || party == null || party.Count == 0) return null;
            return party[0];
        }

        /// <summary>A combat tile of the given group with no living character standing on it.</summary>
        private static object FindFreeTileInGroup(object combatState, int group)
        {
            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) return null;

            List<object> all = new List<object>();
            foreach (object e in entities) if (e != null) all.Add(e);

            HashSet<string> occupied = new HashSet<string>();
            foreach (object e in all)
            {
                object character = PartyAccess.FindComponent(e, "CharacterComponent");
                if (character == null) continue;
                object hp = PartyAccess.ReadMember(character, "CurrentHealth");
                if (hp != null && Convert.ToInt32(hp) <= 0) continue;
                object venue = PartyAccess.FindComponent(e, "VenueComponent");
                object pos = PartyAccess.ReadMember(venue, "TilePosition");
                if (pos != null) occupied.Add(pos.ToString());
            }

            foreach (object e in all)
            {
                object tileComp = PartyAccess.FindComponent(e, "VenueTileComponent");
                if (tileComp == null) continue;
                object gi = PartyAccess.ReadMember(tileComp, "GroupIndex");
                if (gi == null || Convert.ToInt32(gi) != group) continue;
                object venue = PartyAccess.FindComponent(e, "VenueComponent");
                object pos = PartyAccess.ReadMember(venue, "TilePosition");
                if (pos == null || occupied.Contains(pos.ToString())) continue;
                return e;
            }
            return null;
        }

        /// <summary>
        /// Joins a freshly created entity to the fight the way the GAME does, not just by adding it to
        /// a list.
        ///
        /// Adding to CombatState.Entities alone produces a combatant that exists in data and nowhere
        /// else: no model on the field, no initiative, no actions -- and because the turn order now
        /// contains entities that can never take a turn, the whole fight WEDGES. Measured 2026-08-24:
        /// six spawned bees filled the turn strip, none appeared on the grid, and every subsequent
        /// turn stalled until the game was restarted.
        ///
        /// CombatHelper's own honeybee summon path is the reference, and it does four more things:
        /// place the entity on its tile, register initiative, reset its actions, and emit a
        /// CHARACTER_ADDED result -- that last one is what makes the renderer create the model.
        /// </summary>
        private static string AddToCombat(object combatState, object entity, object tile, int group)
        {
            if (entity == null) return "null entity";
            StringBuilder notes = new StringBuilder();
            try
            {
                // 1. Position it on the tile it was created for.
                object venue = PartyAccess.FindComponent(tile, "VenueComponent");
                object pos = PartyAccess.ReadMember(venue, "TilePosition");
                Type venueHelper = AccessTools.TypeByName("VenueHelper");
                MethodInfo setTile = venueHelper == null ? null : AccessTools.Method(venueHelper, "SetTilePosition");
                if (setTile != null && pos != null)
                {
                    try { setTile.Invoke(null, new object[] { entity, pos }); }
                    catch (Exception) { notes.Append(" setTilePosition:failed"); }
                }

                // 2. Roster + round order.
                IList entities = PartyAccess.ReadMember(combatState, "Entities") as IList;
                if (entities != null && !entities.Contains(entity)) entities.Add(entity);
                IList round = PartyAccess.ReadMember(combatState, "RoundEntities") as IList;
                if (round != null && !round.Contains(entity)) round.Add(entity);

                Type combatHelper = AccessTools.TypeByName("CombatHelper");
                object gameRun = RunAccessGameRun();

                // 3. Initiative -- without this the entity never gets a turn AND the queue stalls.
                MethodInfo setInitiative = combatHelper == null ? null : AccessTools.Method(combatHelper, "SetInitiative");
                if (setInitiative != null)
                {
                    List<object> allies;
                    string partyError;
                    PartyAccess.TryGetParty(out allies, out partyError);
                    ParameterInfo[] ps = setInitiative.GetParameters();
                    IList typedAllies = (IList)Activator.CreateInstance(ps[1].ParameterType);
                    if (allies != null) foreach (object a in allies) typedAllies.Add(a);
                    IList results = (IList)Activator.CreateInstance(ps[4].ParameterType);

                    object[] args = new object[ps.Length];
                    args[0] = entity; args[1] = typedAllies; args[2] = combatState;
                    args[3] = gameRun; args[4] = results;
                    for (int i = 5; i < ps.Length; i++) args[i] = false;
                    try { setInitiative.Invoke(null, args); }
                    catch (TargetInvocationException) { notes.Append(" setInitiative:threw"); }
                }
                else notes.Append(" setInitiative:missing");

                // 4. Actions, so it can actually act on its turn.
                foreach (MethodInfo m in combatHelper == null
                    ? new MethodInfo[0] : combatHelper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "ResetCharacterActions", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType.IsGenericType) continue;
                    try { m.Invoke(null, new object[] { entity }); }
                    catch (Exception) { notes.Append(" resetActions:failed"); }
                    break;
                }

                // 5. The 3D model. Without this the combatant is real -- it takes turns and can be
                //    attacked -- but is invisible, which is indistinguishable from "the spawn did
                //    nothing" for anyone watching the screen.
                string visual = CreateActorVisual(entity);
                if (visual != null) notes.Append(' ').Append(visual);

                // 6. Position it AGAIN, now that a transform exists.
                //
                // SetTilePosition moves both the data (VenueComponent.TilePosition) and the actor's
                // transform, but step 1 ran before CreateActorGameObject, so there was no transform
                // to move and the model was built wherever the prefab defaults to. Re-applying it
                // here is what actually puts the creature on its tile. Measured 2026-08-24: spawned
                // bees were live combatants in the turn order but drawn next to the party instead of
                // on the enemy tiles they occupied.
                if (setTile != null && pos != null)
                {
                    try { setTile.Invoke(null, new object[] { entity, pos }); }
                    catch (Exception) { notes.Append(" reposition:failed"); }
                }

                return notes.Length == 0 ? "wired" : "wired," + notes;
            }
            catch (Exception ex)
            {
                return "wiring failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Builds the entity's actor GameObject and registers it, mirroring what CombatPhase does for
        /// its own summons:
        /// <code>
        /// _gameObjectMaps.FromCharacter[e] = CharacterVisualHelper.CreateActorGameObject(
        ///     e, _canvas3D.transform, new GameRandom(), pUseOverworldOverrides: false);
        /// </code>
        /// Returns null when it worked, or a short note when it did not — the combatant is still
        /// perfectly functional without a model, so a failure here must never abort the spawn.
        /// </summary>
        private static string CreateActorVisual(object entity)
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                FieldInfo routerField = routerHelper == null ? null : AccessTools.Field(routerHelper, "_router");
                object router = routerField == null ? null : routerField.GetValue(null);
                object phase = router == null ? null : PartyAccess.ReadMember(router, "_combatPhase");
                if (phase == null) return "visual:noPhase";

                object maps = PartyAccess.ReadMember(phase, "_gameObjectMaps");
                object canvas = PartyAccess.ReadMember(phase, "_canvas3D");
                if (maps == null || canvas == null) return "visual:noMaps";

                object fromCharacter = PartyAccess.ReadMember(maps, "FromCharacter");
                IDictionary map = fromCharacter as IDictionary;
                if (map == null) return "visual:noFromCharacter";
                if (map.Contains(entity)) return null;                 // already drawn

                object transform = PartyAccess.ReadMember(canvas, "transform");
                Type visualHelper = AccessTools.TypeByName("CharacterVisualHelper");
                MethodInfo create = null;
                foreach (MethodInfo m in visualHelper == null
                    ? new MethodInfo[0] : visualHelper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "CreateActorGameObject", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length >= 3) { create = m; break; }
                }
                if (create == null) return "visual:noCreateActor";

                ParameterInfo[] ps = create.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = entity;
                args[1] = transform;
                args[2] = Activator.CreateInstance(ps[2].ParameterType);   // a fresh GameRandom
                for (int i = 3; i < ps.Length; i++)
                    args[i] = ps[i].ParameterType == typeof(bool) ? (object)false
                            : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);

                object actor = create.Invoke(null, args);
                if (actor == null) return "visual:createReturnedNull";
                map[entity] = actor;
                return null;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "visual:threw(" + root.GetType().Name + ")";
            }
            catch (Exception ex)
            {
                return "visual:threw(" + ex.GetType().Name + ")";
            }
        }

        private static object RunAccessGameRun()
        {
            object env = GameBridge.GetEnv();
            return env == null ? null : PartyAccess.ReadMember(env, "GameRun");
        }

        // ============================================================== crucible_godmode

        /// <summary>
        /// crucible_godmode &lt;on|off|status&gt; — keep the party alive so a trait can be observed
        /// instead of the party dying first.
        ///
        /// Implemented as a per-tick top-up rather than an invulnerability flag, because no such flag
        /// exists on CharacterComponent: the tick rewrites CurrentHealth to the character's maximum.
        /// That means damage still LANDS and still fires ON_DAMAGE_TAKEN -- which matters, since the
        /// Pacifist's whole kit keys off being hit. A true invulnerability flag would suppress the
        /// very trigger under test.
        ///
        /// Party only (group 0). Enemies are left alone so a fight still behaves like a fight.
        /// </summary>
        public static void CrucibleGodmode(string mode)
        {
            LastResult = null;
            try
            {
                string verb = (mode ?? "status").Trim().ToLowerInvariant();
                if (verb == "on") _godmode = true;
                else if (verb == "off") _godmode = false;
                else if (verb != "status")
                {
                    LastResult = "error: usage: crucible_godmode <on|off|status>";
                    return;
                }

                LastResult = "godmode=" + (_godmode ? "ON" : "OFF")
                    + " topUps=" + _godmodeTopUps
                    + "\nParty (group 0) health is restored to maximum every tick while ON."
                    + "\nDamage still lands and still fires ON_DAMAGE_TAKEN, so reflect/thorns"
                    + "\ntraits remain observable -- this is a top-up, not invulnerability.";
                if (_log != null) _log.LogInfo("crucible_godmode: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_godmode threw: " + ex.Message;
            }
        }

        private static bool _godmode;
        private static int _godmodeTopUps;

        /// <summary>Per-tick godmode top-up. Cheap no-op when off or when no combat is running.</summary>
        internal static void Tick()
        {
            if (!_godmode) return;
            try
            {
                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) return;

                IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
                if (entities == null) return;

                foreach (object entity in entities)
                {
                    if (entity == null) continue;
                    object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                    if (character == null) continue;

                    object groupIndex = PartyAccess.ReadMember(character, "GroupIndex");
                    if (groupIndex == null || Convert.ToInt32(groupIndex) != 0) continue;

                    object maxHealth = PartyAccess.ReadMember(character, "MaxHealth");
                    FieldInfo hp = AccessTools.Field(character.GetType(), "CurrentHealth");
                    if (hp == null || maxHealth == null) continue;

                    int max = Convert.ToInt32(maxHealth);
                    int current = Convert.ToInt32(hp.GetValue(character));
                    if (current >= max || max <= 0) continue;
                    hp.SetValue(character, max);
                    _godmodeTopUps++;
                }
            }
            catch (Exception) { /* a tick must never throw */ }
        }

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
                // Same flags the game uses; forcing the hook/charge flags on hides the weapon's
                // real attacks behind the HOOK_* movement abilities.
                object result = get.Invoke(null, new object[] { entity, false, true, true, false, false, false, false });
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
