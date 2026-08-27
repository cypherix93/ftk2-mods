using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
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
                new List<string> { "group (default 1 = enemies)", "keepAlive (default 0 = kill them all)" });
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
            Register("crucible_kill_target", "CrucibleKillTarget",
                new List<string> { "targetGuidOrIndex", "killerGuid (optional, auto-picks a living opposing-group combatant)" });
            Register("crucible_use_item", "CrucibleUseItem",
                new List<string> { "itemConfigNameOrThingId", "x", "y", "abilityName (optional, default the item's first ability)" });
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
                        // 30 was too low to see a whole board: a standard venue already has 30
                        // tiles counting the neutral ones, so the list truncated exactly at the
                        // cap and made an ENLARGED grid look identical to a standard one.
                        if (tiles <= 120)
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

                // InteractableHelper.GetAbilityConfig(string pAbilityName, bool pAllowNull = false) --
                // TWO parameters. MethodInfo.Invoke does not backfill defaulted parameters, so
                // calling it with a 1-element args array throws
                // TargetParameterCountException("Number of parameters specified does not match the
                // expected number.") -- exactly the error crucible_list_targets was throwing on
                // every call, ability-agnostic, because this line runs before any ability-specific
                // work.
                Type interactableHelper = AccessTools.TypeByName("InteractableHelper");
                MethodInfo getAbilityConfig = interactableHelper == null ? null : AccessTools.Method(interactableHelper, "GetAbilityConfig");
                if (getAbilityConfig == null) { LastResult = "error: InteractableHelper.GetAbilityConfig not found"; return; }
                object abilityConfig = getAbilityConfig.Invoke(null, new object[] { abilityName, false });
                if (abilityConfig == null) { LastResult = "error: no ability config for '" + abilityName + "'"; return; }

                // VenueHelper.GetTargetableTiles is overloaded:
                //   (Entity, List<Entity>, CombatAbilityConfig, string pThingName = null)              -- 4 params
                //   (Entity, List<Entity>, eTileOccupancies, eTileRowPositions, eTileRowPositions,
                //    eTargets, eTileTargetAreas, List<(eCombatActions, object)>,
                //    bool pAllowInanimateTarget = true)                                                -- 8/9 params
                // Selected by parameter COUNT (4), matching the args actually supplied, per the
                // codebase's standing rule against name-only lookups on overloaded helpers.
                Type venueHelper = AccessTools.TypeByName("VenueHelper");
                MethodInfo getTargetable = null;
                foreach (MethodInfo m in venueHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "GetTargetableTiles", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 4 && ps[2].ParameterType.Name == "CombatAbilityConfig") { getTargetable = m; break; }
                }
                if (getTargetable == null) { LastResult = "error: VenueHelper.GetTargetableTiles(4 args, CombatAbilityConfig overload) not found"; return; }

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
                        object tileComp = PartyAccess.FindComponent(tileEntity, "VenueTileComponent");
                        if (venue == null) continue;
                        count++;
                        object pos = PartyAccess.ReadMember(venue, "TilePosition");
                        sb.Append("\n  tile=").Append(Str(pos))
                          .Append(" group=").Append(tileComp == null ? "(unknown)" : Str(PartyAccess.ReadMember(tileComp, "GroupIndex")))
                          .Append(" occupant=").Append(OccupantAt(combatState, pos));
                    }
                }
                sb.Append("\ntargetableTiles=").Append(count)
                  .Append("\nNOTE: pass a tile= coordinate straight to crucible_use_ability <abilityName> <x> <y>.");
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
        /// crucible_use_ability_auto — pick a real ability and target, then fire.
        ///
        /// The zero-argument smoke test: if this cannot fire, nothing else will, and it separates
        /// "my targeting is wrong" from "combat is not driveable at all".
        ///
        /// <c>CombatHelper.GetFirstEntityDecision</c> itself resolves the candidate ability with a
        /// bare LINQ <c>.First(a =&gt; IsUsableAbility(...))</c> over whatever order GetAbilities
        /// returned -- FLEE is usable whenever combat allows fleeing at all, so calling it unfiltered
        /// mostly just flees, which is useless for a verb every caller expects to DO something. This
        /// verb runs the SAME <c>IsUsableAbility</c> check itself, over every candidate, excludes
        /// FLEE/SKIP_TURN and the three built-in reposition/reconfigure verbs BASIC_MOVE/EQUIP_WEAPON/
        /// BASIC_RELOAD (each spends MOV, not PA/SA, and carries no damage payload -- see
        /// docs/research/coverage/ability-catalog.md:461-481), ranks what remains by
        /// <c>CharacterHelper.GetMinAndMaxDamageOfAbilityForCharacter</c> (same helper crucible_kill_target
        /// uses), and hands GetFirstEntityDecision a single-element list containing only the winner so
        /// its own targeting logic still picks the position. Falls back to the first usable *_ATTACK-named
        /// ability (vanilla offensive-ability naming convention) when none provably deals damage, then to
        /// any other usable non-excluded ability, and only then reports nothing chosen -- naming every
        /// ability considered with its usable flag and damage range, instead of a one-line LINQ "Sequence
        /// contains no matching element" exception.
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
                if (abilities.Count == 0) { LastResult = "error: CombatHelper.GetAbilities returned zero abilities for this entity -- nothing to auto-fire"; return; }

                object gameRun = GameRun();
                Type combatHelperType = AccessTools.TypeByName("CombatHelper");
                Type characterHelperType = AccessTools.TypeByName("CharacterHelper");
                MethodInfo isUsable = combatHelperType == null ? null : AccessTools.Method(combatHelperType, "IsUsableAbility");

                // Measured 2026-08-24/25: CombatHelper.GetFirstEntityDecision itself just takes the
                // FIRST usable ability in whatever order GetAbilities returned them -- FLEE is
                // usable whenever combat allows fleeing at all, so an unfiltered "auto" verb mostly
                // just flees, which is useless for testing (every caller wants this to DO something).
                // Rank every usable, non-FLEE/SKIP_TURN candidate by
                // CharacterHelper.GetMinAndMaxDamageOfAbilityForCharacter's maxDamage -- same helper
                // crucible_kill_target already uses for exactly this judgment -- and prefer the
                // highest; fall back to any other usable non-FLEE/SKIP_TURN ability; only then to
                // nothing. Considered-but-rejected list is built regardless of outcome so a "nothing
                // usable" error names every ability this entity actually has, with its damage range,
                // matching crucible_list_abilities' own usable= column rather than inventing a fresh
                // judgment.
                MethodInfo getCharacterAbilityThing = null;
                if (combatHelperType != null)
                {
                    foreach (MethodInfo m in combatHelperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (!string.Equals(m.Name, "GetCharacterAbilityThing", StringComparison.Ordinal)) continue;
                        if (m.GetParameters().Length == 2) { getCharacterAbilityThing = m; break; }
                    }
                }
                MethodInfo getMinMaxDamage = null;
                if (characterHelperType != null)
                {
                    foreach (MethodInfo m in characterHelperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (!string.Equals(m.Name, "GetMinAndMaxDamageOfAbilityForCharacter", StringComparison.Ordinal)) continue;
                        if (m.GetParameters().Length == 5) { getMinMaxDamage = m; break; }
                    }
                }
                bool canVerifyDamage = getCharacterAbilityThing != null && getMinMaxDamage != null;

                StringBuilder considered = new StringBuilder();
                object chosenAbility = null;
                object fallbackAbility = null;
                object fallbackAttackAbility = null; // preferred fallback: name ends in _ATTACK (vanilla offensive-ability convention)
                string chosenReason = null;
                int bestDamage = -1;
                foreach (object candidate in abilities)
                {
                    string candidateName = Str(PartyAccess.ReadMember(candidate, "AbilityName"));
                    object usableResult = null;
                    if (isUsable != null && gameRun != null)
                    {
                        try { usableResult = isUsable.Invoke(null, new object[] { gameRun, entity, candidateName, true }); }
                        catch (TargetInvocationException ex)
                        {
                            Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                            usableResult = "threw " + root.GetType().Name + ": " + root.Message;
                        }
                    }
                    bool usableBool = usableResult is bool && (bool)usableResult;
                    if (!usableBool)
                    {
                        considered.Append("\n  ").Append(candidateName).Append(" usable=").Append(usableResult == null ? "(unknown)" : usableResult.ToString());
                        continue;
                    }

                    // Measured 2026-08-25: excluding only FLEE/SKIP_TURN still let a run auto-choose
                    // BASIC_MOVE -- it spends MOV, not PA/SA, so "the verb fired" looked like nothing
                    // happened (pa/sa before==after). Per docs/research/coverage/ability-catalog.md:461-481,
                    // BASIC_MOVE/EQUIP_WEAPON/BASIC_RELOAD are the game's three built-in reposition/
                    // reconfigure verbs -- each is a single-action `{"Item1":"MOVE"}` with no damage
                    // payload (Target: SELF or SELF_PICK), so none of them can ever be "the ability
                    // that spends an action" this verb is supposed to pick. GATHER_FOCUS does not
                    // appear anywhere in this repo's ability data (checked via grep) so it is not a
                    // real built-in here and is not excluded on a guess.
                    bool isNonAction = string.Equals(candidateName, "FLEE", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidateName, "SKIP_TURN", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidateName, "BASIC_MOVE", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidateName, "EQUIP_WEAPON", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidateName, "BASIC_RELOAD", StringComparison.OrdinalIgnoreCase);
                    if (isNonAction)
                    {
                        considered.Append("\n  ").Append(candidateName).Append(" usable=true damage=(n/a, non-action, excluded)");
                        continue;
                    }
                    if (fallbackAbility == null) fallbackAbility = candidate;
                    // Vanilla offensive abilities are named *_ATTACK (see the ONLY_*_ATTACK family
                    // documented in docs/research/coverage/ability-catalog.md) -- when the damage
                    // lookup below can't crown a winner, prefer one of these over an arbitrary
                    // non-excluded ability before giving up.
                    if (fallbackAttackAbility == null && candidateName.EndsWith("_ATTACK", StringComparison.OrdinalIgnoreCase))
                    {
                        fallbackAttackAbility = candidate;
                    }

                    int maxDamage = -1; // -1 = "could not verify", not zero
                    string damageNote;
                    if (!canVerifyDamage)
                    {
                        damageNote = "damage=(unverifiable -- CharacterHelper.GetMinAndMaxDamageOfAbilityForCharacter/CombatHelper.GetCharacterAbilityThing not found)";
                    }
                    else
                    {
                        object thing = null;
                        try { thing = getCharacterAbilityThing.Invoke(null, new object[] { entity, candidate }); }
                        catch (TargetInvocationException) { thing = null; }
                        if (thing == null)
                        {
                            damageNote = "damage=(unverifiable -- GetCharacterAbilityThing returned null)";
                        }
                        else
                        {
                            string thingConfigName = Str(PartyAccess.ReadMember(thing, "ConfigName"));
                            try
                            {
                                object dmg = getMinMaxDamage.Invoke(null, new object[] { entity, thingConfigName, candidateName, 1m, 0 });
                                FieldInfo maxField = dmg.GetType().GetField("Item2");
                                maxDamage = maxField == null ? -1 : Convert.ToInt32(maxField.GetValue(dmg));
                                damageNote = "damage=" + maxDamage;
                            }
                            catch (TargetInvocationException)
                            {
                                damageNote = "damage=(unverifiable -- no roll/damage data for this ability on " + thingConfigName + ")";
                            }
                        }
                    }

                    considered.Append("\n  ").Append(candidateName).Append(" usable=true ").Append(damageNote);

                    if (maxDamage > bestDamage)
                    {
                        bestDamage = maxDamage;
                        chosenAbility = candidate;
                        chosenReason = maxDamage > 0
                            ? ("deals damage (maxDamage=" + maxDamage + ", highest of the usable non-FLEE/SKIP_TURN candidates)")
                            : null;
                    }
                }

                if (bestDamage <= 0) chosenAbility = null; // only trust the damage ranking when something actually deals damage
                if (chosenAbility == null && fallbackAttackAbility != null)
                {
                    chosenAbility = fallbackAttackAbility;
                    chosenReason = "no usable ability provably deals damage -- fell back to the first usable *_ATTACK-named ability (vanilla offensive-ability naming convention)";
                }
                if (chosenAbility == null && fallbackAbility != null)
                {
                    chosenAbility = fallbackAbility;
                    chosenReason = "no usable ability provably deals damage and none is *_ATTACK-named -- fell back to the first usable non-excluded ability";
                }

                if (chosenAbility == null)
                {
                    LastResult = "error: no usable ability other than FLEE/SKIP_TURN/BASIC_MOVE/EQUIP_WEAPON/BASIC_RELOAD for this entity right now. Considered:" + considered;
                    return;
                }

                MethodInfo getFirst = combatHelperType == null ? null : AccessTools.Method(combatHelperType, "GetFirstEntityDecision");
                if (getFirst == null) { LastResult = "error: CombatHelper.GetFirstEntityDecision not found"; return; }

                // Pass a single-element list so GetFirstEntityDecision's own targeting logic
                // (VenueHelper.GetTargetableTiles + AIHelper.GetPreferredTarget) still resolves the
                // position, but ability selection is OURS above, not its bare "first usable ability"
                // LINQ (CombatHelper.cs:313).
                List<object> chosenOnly = new List<object> { chosenAbility };
                object typedAbilities = BuildTypedList(getFirst.GetParameters()[2].ParameterType, chosenOnly);
                object decision = getFirst.Invoke(null, new object[] { entity, gameRun, typedAbilities });
                if (decision == null) { LastResult = "error: GetFirstEntityDecision returned null for chosen ability. Considered:" + considered; return; }

                object ability = PartyAccess.ReadMember(decision, "Item1");
                object position = PartyAccess.ReadMember(decision, "Item2");
                if (ability == null) { LastResult = "error: no ability chosen (decision=" + decision + "). Considered:" + considered; return; }

                string name = Str(PartyAccess.ReadMember(ability, "AbilityName"));
                object px = PartyAccess.ReadMember(position, "Item1");
                object py = PartyAccess.ReadMember(position, "Item2");

                // Read the actor's OWN actions right now, synchronously, so the result carries a real
                // "before" figure -- _performAiDecision's actual spend happens on an un-awaited Task
                // (see FireAbility's NOTE), so an "after" figure from THIS call would be a lie; the
                // caller has to re-read crucible_combat_snapshot for that, same as crucible_use_ability.
                object combat = PartyAccess.FindComponent(entity, "CombatComponent");
                string actionsBefore = combat == null ? "(unknown)"
                    : ("pa=" + Str(PartyAccess.ReadMember(combat, "PrimaryActions")) + " sa=" + Str(PartyAccess.ReadMember(combat, "SecondaryActions")));

                FireAbility(name, Convert.ToInt32(px), Convert.ToInt32(py), ability);
                LastResult = "auto-chose ability=" + name + " because " + chosenReason
                    + " target=(" + px + ", " + py + ")"
                    + " actor=" + Str(PartyAccess.ReadMember(entity, "Guid")) + " actionsBefore: " + actionsBefore
                    + "\nconsidered:" + considered
                    + "\n" + LastResult;
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

            object fireTask;
            try
            {
                fireTask = perform.Invoke(combatPhase, new object[] { entity, decision, results });
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: _performAiDecision threw: " + root.GetType().Name + ": " + root.Message;
                return;
            }

            // _performAiDecision's Task is deliberately not awaited here (awaiting on the game thread
            // would deadlock the pump -- see the NOTE below), but an UNOBSERVED faulted Task is
            // silently swallowed: nothing before this fix ever looked at it again, so an exception
            // thrown after the method's first `await` (e.g. a null tile/thing deep in
            // CombatHelper.PerformAbility) vanished with no trace at all -- the exact "reported
            // success while doing nothing" shape this file's header warns about. This attaches a
            // continuation on the default (non-Unity) scheduler purely to OBSERVE the fault and log
            // it, so a fire that dies mid-flight leaves a line in the BepInEx log instead of nothing.
            Task asTask = fireTask as Task;
            if (asTask != null)
            {
                asTask.ContinueWith(delegate(Task t)
                {
                    if (!t.IsFaulted) return;
                    Exception root = t.Exception; while (root.InnerException != null) root = root.InnerException;
                    if (_log != null) _log.LogWarning("crucible_use_ability/_auto: _performAiDecision faulted asynchronously (ability=" + abilityName + "): " + root);
                }, TaskScheduler.Default);
            }

            ICollection resultList = results as ICollection;
            LastResult = "ability=" + abilityName + " target=(" + tileX + ", " + tileY + ")"
                + " origin=" + Str(PartyAccess.ReadMember(entity, "Guid"))
                + "\nresultCount=" + (resultList == null ? -1 : resultList.Count)
                + "\nhealthBefore: " + before
                + "\nasyncFireMonitored=" + (asTask != null)
                + "\nNOTE: _performAiDecision returns a Task that is NOT awaited (blocking the game"
                + "\n      thread would deadlock the pump). Re-read crucible_combat_snapshot after a"
                + "\n      moment to observe the effect; if pa/sa never drop, check the BepInEx log for"
                + "\n      a 'faulted asynchronously' warning before assuming this verb did nothing.";
        }

        // ============================================================== crucible_use_item

        /// <summary>
        /// crucible_use_item &lt;itemConfigNameOrThingId&gt; &lt;x&gt; &lt;y&gt; [abilityName] — make the
        /// active combatant THROW/USE a toolbelt item at a tile, with the ITEM as the acting Thing.
        ///
        /// <para><b>Why crucible_use_ability could not do this.</b> Every fire path ends at
        /// <c>CombatPhase._performAiDecision</c> (CombatPhase.cs:1449), which does NOT take a Thing —
        /// it derives one at CombatPhase.cs:1479 with
        /// <c>CombatHelper.GetCharacterAbilityThing(pCharacter, pDecision.Ability)</c>, and that
        /// helper (CombatHelper.cs:1833) resolves purely off <c>AbilityAction.ThingId</c>. So the
        /// acting Thing is a property of the ABILITY ACTION, not of the call. crucible_use_ability
        /// selects its AbilityAction from <c>CombatHelper.GetAbilities</c> by AbilityName and takes
        /// the FIRST match, and abilities ids are shared across items — Gary's rod and the capture
        /// ball both carry <c>ONLY_RESISTDOWN_ATTACK</c> — so the equipped weapon always won and the
        /// ball could never be the acting item.
        /// </para>
        ///
        /// <para><b>The real toolbelt route, followed here.</b> The combat HUD's toolbelt handler is
        /// <c>CombatPhase._showCombatHudBar</c>'s local <c>onUseItem</c> (CombatPhase.cs:5444), which
        /// calls <c>_characterSelectConsumableThing</c> (CombatPhase.cs:3225) →
        /// <c>_onSelectConsumableThing</c> (CombatPhase.cs:3290). That method's FIRST act is
        /// <c>CharacterHelper.GetCharacterUseItemAbilities(pCharacter, pItem)</c>
        /// (CombatPhase.cs:3293 → CharacterHelper.cs:288), whose final branch stamps
        /// <c>ThingConfigName</c> and <c>ThingId</c> from the item itself (CharacterHelper.cs:320-327).
        /// That is the only place in the game that mints an item-owned AbilityAction. From there the
        /// SELF-target branch fires it directly (CombatPhase.cs:3310) and the non-SELF branch parks
        /// it as <c>_uiSelectedThing</c> for the player to aim, ending at the same
        /// <c>_performAbility</c>. This verb mints the AbilityAction the same way and then hands it to
        /// the existing <c>_performAiDecision</c> path with the caller's tile, which covers both
        /// branches without needing <c>pMenuContext.Layout</c> or any other real-UI-only state.
        /// </para>
        ///
        /// <para><b>The ThingId guard is not optional.</b> Two of
        /// <c>GetCharacterUseItemAbilities</c>' three branches return an AbilityAction with NO
        /// ThingId — the logic-ability branch (CharacterHelper.cs:291-303, only reachable with
        /// pPrioritizeLogicAbility, which this verb never passes) and the
        /// <c>_itemToSkillAbility</c> branch (CharacterHelper.cs:305-319, reachable whenever the
        /// item's config name parses as an <c>eItemNames</c> and the character has the matching
        /// skill). An AbilityAction with a null ThingId makes GetCharacterAbilityThing return null,
        /// which is the silent form of exactly the bug this verb exists to fix. So the chosen action
        /// is run back through GetCharacterAbilityThing before firing and the call is REFUSED, loudly,
        /// unless the Thing that comes back is the very Thing the caller named.
        /// </para>
        /// </summary>
        public static void CrucibleUseItem(string item, string x, string y, string abilityName)
        {
            LastResult = null;
            try
            {
                int tileX, tileY;
                if (string.IsNullOrEmpty(item)
                    || !int.TryParse((x ?? "").Trim(), out tileX)
                    || !int.TryParse((y ?? "").Trim(), out tileY))
                {
                    LastResult = "error: usage: crucible_use_item <itemConfigNameOrThingId> <x> <y> [abilityName]";
                    return;
                }
                string wantedItem = item.Trim();
                string wantedAbility = (abilityName ?? "").Trim();
                if (wantedAbility == "-") wantedAbility = "";

                object entity;
                string error;
                if (!ResolveEntity(null, out entity, out error)) { LastResult = "error: " + error; return; }

                object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                if (character == null) { LastResult = "error: active combatant has no CharacterComponent"; return; }

                // CharacterComponent.Things is the character's whole carried inventory; the toolbelt
                // is a FILTERED VIEW of it (InventoryHelper.GetCharacterBeltItems, InventoryHelper.cs:973).
                // Matching over Things rather than the belt view on purpose: the belt filter drops
                // HIDE_TOOLBAR items and is render-mode dependent, and "the item is carried but the
                // toolbelt chose not to draw it" must not read as "you do not have that item".
                IEnumerable things = PartyAccess.ReadMember(character, "Things") as IEnumerable;
                if (things == null) { LastResult = "error: CharacterComponent.Things is not enumerable"; return; }

                object thing = null;
                StringBuilder carried = new StringBuilder();
                foreach (object candidate in things)
                {
                    if (candidate == null) continue;
                    string cfg = Str(PartyAccess.ReadMember(candidate, "ConfigName"));
                    string id = Str(PartyAccess.ReadMember(candidate, "Id"));
                    carried.Append("\n  ").Append(cfg).Append(" id=").Append(id);
                    if (thing != null) continue;
                    if (string.Equals(cfg, wantedItem, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(id, wantedItem, StringComparison.Ordinal))
                    {
                        thing = candidate;
                    }
                }
                if (thing == null)
                {
                    LastResult = "error: the active combatant is not carrying '" + wantedItem
                        + "' (matched against Thing.ConfigName and Thing.Id). Carried:" + carried;
                    return;
                }

                string thingConfig = Str(PartyAccess.ReadMember(thing, "ConfigName"));
                string thingId = Str(PartyAccess.ReadMember(thing, "Id"));

                // CharacterHelper.GetCharacterUseItemAbilities(Entity, Thing, bool) -- bound by
                // parameter COUNT because the third argument has a compiler default that reflection
                // does not supply. pPrioritizeLogicAbility is passed FALSE deliberately: true returns
                // a bare AbilityAction with no ThingId (CharacterHelper.cs:291-303), which is the
                // overworld/feeding shape, not the combat-throw shape.
                Type characterHelper = AccessTools.TypeByName("CharacterHelper");
                if (characterHelper == null) { LastResult = "error: CharacterHelper not found"; return; }
                MethodInfo getUseItem = null;
                foreach (MethodInfo m in characterHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "GetCharacterUseItemAbilities", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 3) { getUseItem = m; break; }
                }
                if (getUseItem == null) { LastResult = "error: CharacterHelper.GetCharacterUseItemAbilities(Entity, Thing, bool) not found"; return; }

                List<object> itemAbilities = new List<object>();
                try
                {
                    IEnumerable raw = getUseItem.Invoke(null, new object[] { entity, thing, false }) as IEnumerable;
                    if (raw == null) { LastResult = "error: GetCharacterUseItemAbilities returned a non-enumerable for " + thingConfig; return; }
                    foreach (object a in raw) if (a != null) itemAbilities.Add(a);
                }
                catch (TargetInvocationException ex)
                {
                    Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                    LastResult = "error: GetCharacterUseItemAbilities threw for " + thingConfig + ": " + root.GetType().Name + ": " + root.Message;
                    return;
                }
                if (itemAbilities.Count == 0)
                {
                    LastResult = "error: " + thingConfig + " has no use-item abilities (InventoryHelper.GetInteractable(\""
                        + thingConfig + "\").Abilities is empty) -- it is not a usable item.";
                    return;
                }

                object chosen = null;
                StringBuilder offered = new StringBuilder();
                foreach (object a in itemAbilities)
                {
                    string an = Str(PartyAccess.ReadMember(a, "AbilityName"));
                    offered.Append("\n  ").Append(an)
                           .Append(" thingConfigName=").Append(Str(PartyAccess.ReadMember(a, "ThingConfigName")))
                           .Append(" thingId=").Append(Str(PartyAccess.ReadMember(a, "ThingId")));
                    if (chosen != null) continue;
                    if (wantedAbility.Length == 0 || string.Equals(an, wantedAbility, StringComparison.OrdinalIgnoreCase)) chosen = a;
                }
                if (chosen == null)
                {
                    LastResult = "error: '" + wantedAbility + "' is not an ability of " + thingConfig + ". Offered:" + offered;
                    return;
                }
                string chosenName = Str(PartyAccess.ReadMember(chosen, "AbilityName"));

                // The acceptance guard. This is the exact call _performAiDecision will make at
                // CombatPhase.cs:1479 to decide what the acting Thing is, so running it HERE is the
                // difference between proving the item acts and hoping it does.
                Type combatHelper = AccessTools.TypeByName("CombatHelper");
                MethodInfo getAbilityThing = combatHelper == null ? null : AccessTools.Method(combatHelper, "GetCharacterAbilityThing");
                if (getAbilityThing == null) { LastResult = "error: CombatHelper.GetCharacterAbilityThing not found -- cannot verify the acting Thing, refusing to fire blind"; return; }

                object actingThing;
                try { actingThing = getAbilityThing.Invoke(null, new object[] { entity, chosen }); }
                catch (TargetInvocationException ex)
                {
                    Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                    LastResult = "error: GetCharacterAbilityThing threw while verifying the acting Thing: " + root.GetType().Name + ": " + root.Message;
                    return;
                }
                string actingId = actingThing == null ? null : Str(PartyAccess.ReadMember(actingThing, "Id"));
                if (actingThing == null || !string.Equals(actingId, thingId, StringComparison.Ordinal))
                {
                    LastResult = "error: REFUSED -- ability '" + chosenName + "' would act with "
                        + (actingThing == null ? "NO Thing (its AbilityAction carries no ThingId)"
                                               : ("Thing " + Str(PartyAccess.ReadMember(actingThing, "ConfigName")) + " id=" + actingId))
                        + ", not " + thingConfig + " id=" + thingId + "."
                        + "\nThis is the branch of CharacterHelper.GetCharacterUseItemAbilities that does not stamp"
                        + "\nThingId (CharacterHelper.cs:291-319). Firing anyway would reproduce the silent wrong-item"
                        + "\nbug this verb exists to fix. Offered abilities:" + offered;
                    return;
                }

                object combat = PartyAccess.FindComponent(entity, "CombatComponent");
                string actionsBefore = combat == null ? "(unknown)"
                    : ("pa=" + Str(PartyAccess.ReadMember(combat, "PrimaryActions")) + " sa=" + Str(PartyAccess.ReadMember(combat, "SecondaryActions")));

                FireAbility(chosenName, tileX, tileY, chosen);
                LastResult = "item=" + thingConfig + " thingId=" + thingId
                    + " ability=" + chosenName
                    + " actingThingVerified=" + thingConfig + " (GetCharacterAbilityThing agrees)"
                    + " actionsBefore: " + actionsBefore
                    + "\nofferedAbilities:" + offered
                    + "\n" + LastResult;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: crucible_use_item threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_use_item threw: " + ex.Message;
            }
        }

        // ============================================================== end turn / win

        /// <summary>
        /// crucible_combat_end_turn — end the ACTIVE entity's turn the way the game itself ends it,
        /// so ON_TURN_END skill procs actually fire.
        ///
        /// Verified in CombatPhase.cs (decompile): _nextTurn (line ~1957) is what runs AFTER a turn
        /// has ended — it starts the next one, and the only eSkillEventProcs it raises is START_TURN
        /// (line ~2105, gated on the entity not being charged). END_TURN is raised twice, only inside
        /// _tryProceedAsync (line ~4770): once at line ~4855 in the wave-advance branch, and once at
        /// line ~4861 in the RoundEntities/IsTurnOver branch, which is the normal "this character is
        /// done" path. Both branches raise END_TURN and THEN call _nextTurn themselves (line ~4906).
        /// So _nextTurn is the consequence of a turn ending, not the cause — the procs live in the
        /// cause. Calling _nextTurn directly (the old implementation) jumps straight to the
        /// consequence and skips the cause, which is why every ON_TURN_END recipe in the pack has
        /// been silent all session: the proc call was never reached.
        ///
        /// CombatHelper.IsTurnOver (CombatHelper.cs:762) is exactly `PrimaryActions == 0` (or the
        /// entity is dead) — verified by reading the method body. So to make the game take the
        /// IsTurnOver branch of _tryProceedAsync itself, this zeroes the active entity's
        /// CombatComponent.PrimaryActions and SecondaryActions first, then invokes _tryProceedAsync()
        /// with its own defaults (pIsScripted=false, pIsFirstTurn=false, pIsStartTurn=false,
        /// pIsNextRound=false, pWaitEngageTime=0f — read off the declaration at CombatPhase.cs:4770).
        /// _tryProceedAsync then runs the real end-of-turn results, raises END_TURN for the active
        /// entity, and calls _nextTurn on its own — so this verb no longer touches _nextTurn at all.
        ///
        /// Do NOT call crucible_combat_restore_actions immediately before this verb: restore_actions
        /// refills PrimaryActions/SecondaryActions to the character's PA/SA stat, which is exactly
        /// what this verb must zero to make IsTurnOver report true. Calling them back to back is
        /// self-defeating and will make the turn look like it never ended.
        /// </summary>
        public static void CrucibleCombatEndTurn()
        {
            LastResult = null;
            try
            {
                object combatPhase = FindCombatPhase();
                if (combatPhase == null) { LastResult = "error: CombatPhase unavailable (is a fight in progress?)"; return; }

                object activeEntity = PartyAccess.ReadMember(combatPhase, "_activeCharacterEntity");
                if (activeEntity == null) { LastResult = "error: CombatPhase._activeCharacterEntity unavailable"; return; }

                object combat = PartyAccess.FindComponent(activeEntity, "CombatComponent");
                if (combat == null) { LastResult = "error: active entity has no CombatComponent"; return; }

                FieldInfo primary = AccessTools.Field(combat.GetType(), "PrimaryActions");
                FieldInfo secondary = AccessTools.Field(combat.GetType(), "SecondaryActions");
                if (primary == null || secondary == null) { LastResult = "error: CombatComponent.PrimaryActions/SecondaryActions not found"; return; }

                MethodInfo tryProceed = null;
                foreach (MethodInfo m in combatPhase.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_tryProceedAsync", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 5) { tryProceed = m; break; }
                }
                if (tryProceed == null) { LastResult = "error: CombatPhase._tryProceedAsync(5 args) not found"; return; }

                string activeGuid = Str(PartyAccess.ReadMember(activeEntity, "Guid"));
                string activeName = Str(PartyAccess.ReadMember(PartyAccess.FindComponent(activeEntity, "CharacterComponent"), "ConfigName"));
                object beforeP = primary.GetValue(combat);
                object beforeS = secondary.GetValue(combat);

                primary.SetValue(combat, 0);
                secondary.SetValue(combat, 0);

                // Defaults read off the CombatPhase.cs:4770 declaration:
                // _tryProceedAsync(pIsScripted=false, pIsFirstTurn=false, pIsStartTurn=false, pIsNextRound=false, pWaitEngageTime=0f)
                tryProceed.Invoke(combatPhase, new object[] { false, false, false, false, 0f });

                LastResult = "endedTurnFor=" + activeName + " guid=" + activeGuid
                    + " primaryActions " + Str(beforeP) + "->0 secondaryActions " + Str(beforeS) + "->0"
                    + "\nactiveAfter=" + ActiveEntityGuid(null)
                    + "\nNOTE: _tryProceedAsync returns a Task that is not awaited; re-read the"
                    + "\n      snapshot after a beat to observe END_TURN procs and the next turn.";
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: _tryProceedAsync threw: " + root.GetType().Name + ": " + root.Message;
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

        // ============================================================== crucible_kill_target

        /// <summary>
        /// crucible_kill_target &lt;targetGuidOrIndex&gt; [killerGuid] — land a REAL killing blow,
        /// through the game's own damage pipeline, so kill-gated traits get a fair shot to fire.
        ///
        /// crucible_combat_wipe_enemies and crucible_kill_all both end a life by writing
        /// CurrentHealth directly and/or calling CharacterHelper.KillCharacter. Read cold in the
        /// decompile, neither of those touches the only place a kill trait is checked. The chain,
        /// confirmed by opening every file:line below, not inferred:
        ///   CombatPhase._performAiDecision(Entity, CombatDecisionData, results)   CombatPhase.cs:1449
        ///     -> _performAbility(pCharacter, ...)                                CombatPhase.cs:1523
        ///       -> CombatHelper.PerformAbility(...)                              CombatPhase.cs:4025
        ///         -> _applyActions(...)                                         CombatHelper.cs:1153,1391
        ///           -> CombatHelper.ApplyAction(...), case CHANGE_STAT           CombatHelper.cs:1592,2034
        ///             -> InteractableHelper.ApplyStatChange(...)                 InteractableHelper.cs:627
        ///               -> CharacterHelper.TryKillCharacter(...)                 InteractableHelper.cs:857
        ///               -> if killed: CombatHelper.TryCombatOnKillSkillCondition(SKILL_PLAYTHING, ...)
        ///                                                                        InteractableHelper.cs:859
        ///               -> unconditionally: TryProcDiscipline(...)               InteractableHelper.cs:849,862
        /// CharacterHelper.TryKillCharacter and .KillCharacter themselves (CharacterHelper.cs:2130,
        /// 2166) do neither call -- both bodies were read in full and contain no reference to
        /// TryCombatOnKillSkillCondition or any *_performSkillAbilityProcs-style dispatch. The proc
        /// check lives only in ApplyStatChange, the CALLER, which is why any verb that ends a life
        /// by writing state instead of dealing damage through this pipeline can never reach it.
        ///
        /// So this verb drops the target to 1 HP by direct write -- same technique
        /// crucible_combat_wipe_enemies already uses, but to 1, never 0 -- and then fires a real
        /// ability from the killer at the target's tile through CombatPhase._performAiDecision, the
        /// same entry point crucible_use_ability already drives. _performAiDecision takes the acting
        /// Entity as an explicit parameter rather than always reading _activeCharacterEntity
        /// (CombatPhase.cs:1449), so the killer does not have to be whoever's turn it currently is.
        /// The actual HP 1-&gt;0 transition, and everything gated on it, happens inside the verified
        /// pipeline above -- not in this verb's direct write.
        ///
        /// "killTriggerPathInvoked" in the result is a claim about which CODE PATH ran, not about
        /// whether a trait actually procced: a proc is only externally observable if the killer
        /// happens to carry SKILL_PLAYTHING or SKILL_DISCIPLINE. What this verb CAN verify is
        /// whether the target flipped dead across the one call that reaches ApplyStatChange, which
        /// is the only place either check runs -- if the target is still alive afterward, the check
        /// never ran, and the result says so plainly instead of guessing why the swing missed.
        /// </summary>
        public static void CrucibleKillTarget(string targetGuidOrIndex, string killerGuid)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(targetGuidOrIndex))
                {
                    LastResult = "error: usage: crucible_kill_target <targetGuidOrIndex> [killerGuid]";
                    return;
                }

                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) { LastResult = "error: " + error; return; }

                object entities = PartyAccess.ReadMember(combatState, "Entities");
                IEnumerable entitiesEnum = entities as IEnumerable;
                if (entitiesEnum == null) { LastResult = "error: CombatState.Entities is not enumerable"; return; }

                // Index is 0-based position in this same enumeration order, restricted to actual
                // combatants -- the same order crucible_combat_snapshot lists them in -- so a guid
                // copied from that snapshot always works, and so does its position in the list.
                List<object> combatants = new List<object>();
                foreach (object e in entitiesEnum)
                {
                    if (e == null) continue;
                    if (PartyAccess.FindComponent(e, "CharacterComponent") != null && PartyAccess.FindComponent(e, "CombatComponent") != null)
                        combatants.Add(e);
                }

                object target = ResolveCombatant(combatants, targetGuidOrIndex.Trim());
                if (target == null)
                {
                    LastResult = "error: no combatant matches target '" + targetGuidOrIndex
                        + "' (guid, or 0-based index into the combatant list crucible_combat_snapshot shows)";
                    return;
                }

                object targetCharacter = PartyAccess.FindComponent(target, "CharacterComponent");
                string targetLabel = Str(PartyAccess.ReadMember(target, "Guid")) + "/" + Str(PartyAccess.ReadMember(targetCharacter, "ConfigName"));

                if (IsDead(target))
                {
                    LastResult = "target=" + targetLabel + " is already dead -- nothing to kill, no trigger to observe.";
                    return;
                }

                object killer;
                string killerError;
                if (!ResolveKiller(combatants, target, targetCharacter, killerGuid, out killer, out killerError))
                {
                    LastResult = "error: " + killerError;
                    return;
                }

                object killerCharacter = PartyAccess.FindComponent(killer, "CharacterComponent");
                string killerLabel = Str(PartyAccess.ReadMember(killer, "Guid")) + "/" + Str(PartyAccess.ReadMember(killerCharacter, "ConfigName"));

                // Find one of the killer's abilities whose legal target tiles include the target's
                // tile, using the SAME enumeration crucible_list_targets uses (VenueHelper's 4-arg
                // CombatAbilityConfig overload), so the choice is legal by construction, not assumed.
                List<object> abilities;
                if (!TryGetAbilities(killer, out abilities, out error)) { LastResult = "error: " + error; return; }

                Type interactableHelper = AccessTools.TypeByName("InteractableHelper");
                MethodInfo getAbilityConfig = interactableHelper == null ? null : AccessTools.Method(interactableHelper, "GetAbilityConfig");
                Type venueHelper = AccessTools.TypeByName("VenueHelper");
                MethodInfo getTargetable = null;
                if (venueHelper != null)
                {
                    foreach (MethodInfo m in venueHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (!string.Equals(m.Name, "GetTargetableTiles", StringComparison.Ordinal)) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 4 && ps[2].ParameterType.Name == "CombatAbilityConfig") { getTargetable = m; break; }
                    }
                }
                if (getAbilityConfig == null || getTargetable == null)
                {
                    LastResult = "error: InteractableHelper.GetAbilityConfig or VenueHelper.GetTargetableTiles(4-arg) not found";
                    return;
                }

                object targetVenue = PartyAccess.FindComponent(target, "VenueComponent");
                object targetTile = targetVenue == null ? null : PartyAccess.ReadMember(targetVenue, "TilePosition");
                if (targetTile == null) { LastResult = "error: target has no VenueComponent.TilePosition"; return; }

                // Measured 2026-08-24/25: firing the first tile-legal ability is not enough -- a
                // weapon can carry multiple abilities that all legally reach the same enemy tile,
                // and some of them are zero-damage by design (e.g. the Trainer's starter weapon
                // ARM_ORIG_STARTER_TRAINER_BEAST_WHISTLE ships STAFF_BASIC_ATTACK, MinValue=0
                // MaxValue=1, alongside ONLY_SCARE_ATTACK and ONLY_ATTACKUP_OTHER_ATTACK, both
                // MinValue=0 MaxValue=0 -- see items.json). Those two are still legitimate hostile
                // actions (they proc ON_ABILITY_USED / HOSTILE_ACTION recipes and can roll PERFECT),
                // so "the ability fired" is not evidence it can deal damage. Rank every tile-legal
                // candidate by CharacterHelper.GetMinAndMaxDamageOfAbilityForCharacter's maxDamage
                // and only fire one whose max is provably > 0.
                Type combatHelperType = AccessTools.TypeByName("CombatHelper");
                Type characterHelperType = AccessTools.TypeByName("CharacterHelper");
                MethodInfo getCharacterAbilityThing = null;
                if (combatHelperType != null)
                {
                    foreach (MethodInfo m in combatHelperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (!string.Equals(m.Name, "GetCharacterAbilityThing", StringComparison.Ordinal)) continue;
                        if (m.GetParameters().Length == 2) { getCharacterAbilityThing = m; break; }
                    }
                }
                MethodInfo getMinMaxDamage = null;
                if (characterHelperType != null)
                {
                    foreach (MethodInfo m in characterHelperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (!string.Equals(m.Name, "GetMinAndMaxDamageOfAbilityForCharacter", StringComparison.Ordinal)) continue;
                        if (m.GetParameters().Length == 5) { getMinMaxDamage = m; break; }
                    }
                }

                // Only trust the damage filter below when BOTH helper methods actually resolved --
                // otherwise fall back to "first tile-legal ability" (the prior behavior) rather than
                // rejecting every candidate because reflection, not the ability, came up empty.
                bool canVerifyDamage = getCharacterAbilityThing != null && getMinMaxDamage != null;

                object chosenAbility = null;
                string chosenAbilityName = null;
                int chosenMaxDamage = -1;
                StringBuilder consideredAbilities = new StringBuilder();
                foreach (object ability in abilities)
                {
                    if (chosenAbility != null && !canVerifyDamage) break;
                    string name = Str(PartyAccess.ReadMember(ability, "AbilityName"));
                    object abilityConfig;
                    try { abilityConfig = getAbilityConfig.Invoke(null, new object[] { name, false }); }
                    catch (TargetInvocationException) { continue; }
                    if (abilityConfig == null) continue;

                    object tilesResult;
                    try { tilesResult = getTargetable.Invoke(null, new object[] { killer, entities, abilityConfig, null }); }
                    catch (TargetInvocationException) { continue; }
                    IEnumerable tileEntities = tilesResult as IEnumerable;
                    if (tileEntities == null) continue;

                    bool reachesTarget = false;
                    foreach (object tileEntity in tileEntities)
                    {
                        object venue = PartyAccess.FindComponent(tileEntity, "VenueComponent");
                        if (venue == null) continue;
                        object pos = PartyAccess.ReadMember(venue, "TilePosition");
                        if (pos != null && pos.ToString() == targetTile.ToString()) { reachesTarget = true; break; }
                    }
                    if (!reachesTarget) continue;

                    if (!canVerifyDamage)
                    {
                        // Reflection couldn't find one of the helper methods -- can't judge damage,
                        // so fall back to the original "first tile-legal ability" behavior rather
                        // than reject every candidate on a lookup failure that isn't the ability's fault.
                        chosenAbility = ability;
                        chosenAbilityName = name;
                        break;
                    }

                    // Legal-by-tile. Now check it can actually hurt: resolve the weapon/Thing behind
                    // it exactly the way _performAiDecision will, then ask for its damage range.
                    int maxDamage = -1; // -1 = "could not verify" (missing thing/roll data), not zero
                    string skipReason;
                    object thing;
                    try { thing = getCharacterAbilityThing.Invoke(null, new object[] { killer, ability }); }
                    catch (TargetInvocationException) { thing = null; }

                    if (thing == null)
                    {
                        skipReason = "GetCharacterAbilityThing returned null (would silently fail if fired)";
                    }
                    else
                    {
                        string thingConfigName = Str(PartyAccess.ReadMember(thing, "ConfigName"));
                        try
                        {
                            object dmg = getMinMaxDamage.Invoke(null, new object[] { killer, thingConfigName, name, 1m, 0 });
                            FieldInfo maxField = dmg.GetType().GetField("Item2");
                            maxDamage = maxField == null ? -1 : Convert.ToInt32(maxField.GetValue(dmg));
                            skipReason = maxDamage <= 0 ? "maxDamage=" + maxDamage + " (zero-damage utility ability)" : null;
                        }
                        catch (TargetInvocationException)
                        {
                            skipReason = "no roll/damage data for this ability on " + thingConfigName;
                        }
                    }

                    consideredAbilities.Append("\n  ").Append(name)
                        .Append(" maxDamage=").Append(maxDamage)
                        .Append(skipReason == null ? " (candidate)" : " -- skipped: " + skipReason);

                    if (skipReason == null && maxDamage > chosenMaxDamage)
                    {
                        chosenAbility = ability;
                        chosenAbilityName = name;
                        chosenMaxDamage = maxDamage;
                    }
                }

                if (chosenAbility == null)
                {
                    LastResult = "target=" + targetLabel + " killer=" + killerLabel
                        + " deadAfter=(unattempted) killTriggerPathInvoked=false"
                        + (consideredAbilities.Length == 0
                            ? "\nerror: none of " + killerLabel + "'s " + abilities.Count
                              + " abilities can legally target " + targetLabel + "'s tile " + Str(targetTile) + " -- no ability was fired."
                            : "\nerror: " + killerLabel + "'s abilities that legally reach " + targetLabel + "'s tile " + Str(targetTile)
                              + " all deal zero verifiable damage -- no ability was fired. Considered:" + consideredAbilities);
                    return;
                }

                // Drop the target to 1 HP by direct write -- exactly what crucible_combat_wipe_enemies
                // already does, but to 1, never 0. Any nonzero damage from the chosen ability then
                // lands the kill; the HP 1->0 transition and the on-kill check gated on it still run
                // inside the real pipeline documented above, not here.
                FieldInfo hpField = AccessTools.Field(targetCharacter.GetType(), "CurrentHealth");
                object hpBefore = hpField == null ? null : hpField.GetValue(targetCharacter);
                if (hpField != null)
                {
                    int currentHp = Convert.ToInt32(hpField.GetValue(targetCharacter));
                    if (currentHp > 1) hpField.SetValue(targetCharacter, 1);
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

                abilityField.SetValue(decision, chosenAbility);
                positionField.SetValue(decision, targetTile);
                if (focusField != null) focusField.SetValue(decision, 0);

                MethodInfo perform = null;
                foreach (MethodInfo m in combatPhase.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_performAiDecision", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 3) { perform = m; break; }
                }
                if (perform == null) { LastResult = "error: CombatPhase._performAiDecision(3 args) not found"; return; }

                object results = Activator.CreateInstance(perform.GetParameters()[2].ParameterType);
                bool deadBefore = IsDead(target);

                try
                {
                    // Killer is passed explicitly, not "whoever's active" -- _performAiDecision takes
                    // pCharacter as a parameter (CombatPhase.cs:1449) instead of always reading
                    // _activeCharacterEntity, so this works outside the killer's own turn. Whether the
                    // game enforces turn order anywhere else this call doesn't touch is unverified;
                    // report what happened, not what should have happened.
                    perform.Invoke(combatPhase, new object[] { killer, decision, results });
                }
                catch (TargetInvocationException ex)
                {
                    Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                    LastResult = "target=" + targetLabel + " killer=" + killerLabel + " ability=" + chosenAbilityName
                        + " deadBefore=" + deadBefore + " deadAfter=(unattempted) killTriggerPathInvoked=false"
                        + "\nerror: _performAiDecision threw: " + root.GetType().Name + ": " + root.Message;
                    return;
                }

                bool deadAfter = IsDead(target);
                ICollection resultList = results as ICollection;

                // Only claim the trigger's CODE PATH ran when the target actually flipped dead across
                // this single call -- see the class doc comment for why that is the honest boundary of
                // what this verb can observe.
                bool killTriggerPathInvoked = !deadBefore && deadAfter;

                LastResult = "target=" + targetLabel + " killer=" + killerLabel + " ability=" + chosenAbilityName
                    + " tile=" + Str(targetTile)
                    + " hpBefore=" + Str(hpBefore) + "->1(forced)"
                    + " deadBefore=" + deadBefore + " deadAfter=" + deadAfter
                    + " killTriggerPathInvoked=" + killTriggerPathInvoked
                    + " resultCount=" + (resultList == null ? -1 : resultList.Count)
                    + (deadAfter ? "" : "\nNOTE: target is still alive -- the ability did not land a killing blow; no on-kill trigger check ran.")
                    + "\nNOTE: _performAiDecision returns a Task that is NOT awaited; re-read crucible_combat_snapshot"
                    + "\n      after a moment to confirm the death and any secondary effects (bond counters, etc).";
                if (_log != null) _log.LogInfo("crucible_kill_target: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_kill_target threw: " + ex.Message;
            }
        }

        /// <summary>Guid match (case-insensitive) or 0-based index into <paramref name="combatants"/>.</summary>
        private static object ResolveCombatant(List<object> combatants, string guidOrIndex)
        {
            int index;
            if (int.TryParse(guidOrIndex, out index))
            {
                return (index >= 0 && index < combatants.Count) ? combatants[index] : null;
            }
            foreach (object candidate in combatants)
            {
                object guid = PartyAccess.ReadMember(candidate, "Guid");
                if (guid != null && string.Equals(guid.ToString(), guidOrIndex, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Explicit killerGuid if given; otherwise the active combatant if it qualifies, otherwise
        /// the first living combatant outside the target's group. Never the target itself.
        /// </summary>
        private static bool ResolveKiller(List<object> combatants, object target, object targetCharacter, string killerGuid, out object killer, out string error)
        {
            killer = null;
            error = null;

            if (!string.IsNullOrEmpty(killerGuid) && killerGuid.Trim() != "-")
            {
                object explicitKiller = ResolveCombatant(combatants, killerGuid.Trim());
                if (explicitKiller == null) { error = "no combatant matches killer '" + killerGuid + "'"; return false; }
                if (ReferenceEquals(explicitKiller, target)) { error = "killer and target are the same entity"; return false; }
                killer = explicitKiller;
                return true;
            }

            object targetGroup = PartyAccess.ReadMember(targetCharacter, "GroupIndex");
            int targetGroupIdx = targetGroup == null ? -1 : Convert.ToInt32(targetGroup);

            object combatPhase = FindCombatPhase();
            object active = combatPhase == null ? null : PartyAccess.ReadMember(combatPhase, "_activeCharacterEntity");
            if (active != null && !ReferenceEquals(active, target) && !IsDead(active))
            {
                object activeCharacter = PartyAccess.FindComponent(active, "CharacterComponent");
                object activeGroup = activeCharacter == null ? null : PartyAccess.ReadMember(activeCharacter, "GroupIndex");
                if (activeCharacter != null && (activeGroup == null || Convert.ToInt32(activeGroup) != targetGroupIdx))
                {
                    killer = active;
                    return true;
                }
            }

            foreach (object candidate in combatants)
            {
                if (ReferenceEquals(candidate, target) || IsDead(candidate)) continue;
                object candidateCharacter = PartyAccess.FindComponent(candidate, "CharacterComponent");
                object candidateGroup = candidateCharacter == null ? null : PartyAccess.ReadMember(candidateCharacter, "GroupIndex");
                if (candidateCharacter != null && (candidateGroup == null || Convert.ToInt32(candidateGroup) != targetGroupIdx))
                {
                    killer = candidate;
                    return true;
                }
            }

            error = "no living combatant outside target's group to act as killer -- pass [killerGuid] explicitly";
            return false;
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
        public static void CrucibleCombatWipeEnemies(string group, string keepAlive)
        {
            LastResult = null;
            try
            {
                int wanted;
                if (!int.TryParse((group ?? "1").Trim(), out wanted)) wanted = 1;

                // keepAlive: leave the FIRST N living members of the group standing. An isolation
                // scenario needs a REAL encounter monster to test against -- a body made by
                // crucible_combat_spawn has no StatusEffectComponent and so cannot carry or report
                // a status at all (measured 2026-08-26: the identical FOCUS FIRE proc stamped
                // ARMORDOWN on an encounter rat and left a spawned jelly at statuses=[]) -- but the
                // encounter the party walks into brings four of them, which killed the HP-16
                // Trainer partner before it could take its turn in two runs out of three. Thinning
                // to one real monster is the only way to get an isolation fight that is both
                // status-capable and survivable. Empty/absent means keep none: the old behaviour.
                int keep;
                if (!int.TryParse((keepAlive ?? "").Trim(), out keep) || keep < 0) keep = 0;

                object combatState;
                string error;
                if (!TryGetCombatState(out combatState, out error)) { LastResult = "error: " + error; return; }

                IEnumerable entitiesLive = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
                if (entitiesLive == null) { LastResult = "error: CombatState.Entities is not enumerable"; return; }

                // Snapshot BEFORE killing anything -- same fix as crucible_kill_all's own snapshot
                // (see CrucibleKillAll above). Killing a GROUP 0 (party) combatant can trigger the
                // game's own defeat/teardown handling synchronously, which mutates CombatState.Entities
                // out from under a live enumerator. Measured: wiping group 0 killed only the first
                // combatant in enumeration order and then threw InvalidOperationException ("Collection
                // was modified") on the next MoveNext(), aborting the loop silently into LastResult's
                // error branch -- three of four party members were never touched. Enumerating a
                // List<object> snapshot instead means the mutation of the live collection can't affect
                // the iteration.
                List<object> entities = new List<object>();
                foreach (object e in entitiesLive) entities.Add(e);

                Type helper = AccessTools.TypeByName("CharacterHelper");
                MethodInfo isDead = helper == null ? null : AccessTools.Method(helper, "IsDead");

                // KillCharacter, NOT TryKillCharacter. TryKillCharacter's real signature is
                //     TryKillCharacter(Entity, int, StatChangedResultsData, Env, GameRandom,
                //                      List<(eAbilityResults, object)>)
                // -- six parameters (CharacterHelper.cs:2130). Invoking it with one argument threw
                // TargetParameterCountException, which derives from TargetException and NOT from
                // TargetInvocationException, so it sailed past the narrow inner catch, was swallowed
                // by the per-entity catch BEFORE `killed++`, and the verb reported
                // `killed=0 changed=False` on every call -- while the CurrentHealth=0 write just
                // above had already landed. A verb that half-worked and reported total failure.
                // KillCharacter(Entity, List<(eAbilityResults,object)>, bool) is the method
                // TryKillCharacter itself calls to do the actual killing; everything else in
                // TryKillCharacter is deathsave/necro/revive logic a test wipe does not want.
                // The results list must be a REAL list, not null: KillCharacter only appends the
                // DIED result (which is what downstream turn-order/summary bookkeeping reads) when
                // pResults is non-null.
                Type abilityResults = AccessTools.TypeByName("eAbilityResults");
                MethodInfo killChar = null;
                Type resultListType = null;
                if (helper != null && abilityResults != null)
                {
                    killChar = AccessTools.Method(helper, "KillCharacter");
                    resultListType = typeof(List<>).MakeGenericType(
                        typeof(ValueTuple<,>).MakeGenericType(abilityResults, typeof(object)));
                }
                if (killChar == null)
                {
                    LastResult = "error: CharacterHelper.KillCharacter could not be resolved; refusing "
                        + "to half-kill anything by writing CurrentHealth without death bookkeeping";
                    return;
                }

                int seen = 0, killed = 0, alreadyDead = 0, skippedGroup = 0, kept = 0;
                StringBuilder detail = new StringBuilder();
                string firstFailure = null;

                foreach (object entity in entities)
                {
                    // Per-entity try/catch: killing an earlier GROUP 0 combatant can trigger the
                    // game's own defeat handling mid-loop (see the snapshot note above), which can
                    // leave a LATER entity in this same snapshot stale (component lookups throwing)
                    // even though the collection itself is now safe to enumerate. One bad entity must
                    // not stop the rest of the party from being killed.
                    try
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
                        if (kept < keep) { kept++; continue; }

                        FieldInfo hp = AccessTools.Field(character.GetType(), "CurrentHealth");
                        if (hp != null) hp.SetValue(character, 0);

                        object results = Activator.CreateInstance(resultListType);
                        killChar.Invoke(null, new object[] { entity, results, false });
                        killed++;
                        if (detail.Length < 400)
                            detail.Append("\n  ").Append(Str(PartyAccess.ReadMember(character, "ConfigName")));
                    }
                    catch (Exception ex)
                    {
                        Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                        firstFailure = firstFailure ?? (root.GetType().Name + ": " + root.Message);
                    }
                }

                LastResult = "group=" + wanted + " inGroup=" + seen + " killed=" + killed
                    + " keptAlive=" + kept
                    + " alreadyDead=" + alreadyDead + " otherGroups=" + skippedGroup
                    + " changed=" + (killed > 0) + detail
                    + (firstFailure == null ? "" : ("\nfirst per-entity failure (loop continued past it): " + firstFailure))
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

                    // CharacterHelper.GetMaxHealth(Entity), NOT a "MaxHealth" member.
                    //
                    // CharacterComponent has no such field or property. The old lookup failed on
                    // EVERY character on EVERY tick, and HarmonyX logs a warning for each miss:
                    // measured 45,432 of 49,219 lines in Player.log -- 92% of the file -- from this
                    // one line, immediately before the game died on a d3d11 out-of-memory. A
                    // reflection miss in a per-tick loop is not a silent no-op; it is a log flood.
                    int max = MaxHealthOf(entity);
                    FieldInfo hp = AccessTools.Field(character.GetType(), "CurrentHealth");
                    if (hp == null || max <= 0) continue;
                    int current = Convert.ToInt32(hp.GetValue(character));
                    if (current >= max || max <= 0) continue;
                    hp.SetValue(character, max);
                    _godmodeTopUps++;
                }
            }
            catch (Exception) { /* a tick must never throw */ }
        }


        /// <summary>Cached <c>CharacterHelper.GetMaxHealth(Entity)</c>; 0 when unavailable.</summary>
        private static MethodInfo _getMaxHealth;
        private static bool _getMaxHealthResolved;

        private static int MaxHealthOf(object entity)
        {
            try
            {
                // Resolve ONCE. The point of the cache is not speed, it is that a failed lookup
                // inside a per-tick loop floods the log and can take the process down with it.
                if (!_getMaxHealthResolved)
                {
                    _getMaxHealthResolved = true;
                    Type helper = AccessTools.TypeByName("CharacterHelper");
                    if (helper != null)
                        foreach (MethodInfo m in helper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (!string.Equals(m.Name, "GetMaxHealth", StringComparison.Ordinal)) continue;
                            if (m.GetParameters().Length != 1) continue;
                            _getMaxHealth = m; break;
                        }
                    if (_getMaxHealth == null && _log != null)
                        _log.LogWarning("crucible_godmode: CharacterHelper.GetMaxHealth(Entity) not found; "
                            + "godmode cannot top anyone up. Reported once, not per tick.");
                }
                if (_getMaxHealth == null) return 0;
                object value = _getMaxHealth.Invoke(null, new[] { entity });
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch (Exception) { return 0; }
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
