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
    /// Drives the overworld: read state, preview a path, move, inspect a hex, interact, end a turn.
    ///
    /// Grounded in a decompilation of the retail assembly, which corrected four beliefs that had
    /// each cost a lot of time:
    ///
    /// 1. <b>The turn/time fields we were watching are <c>[Obsolete]</c>.</b>
    ///    <c>GameRunData.RoundCount</c> is marked "Use AdventureState.MapState.RoundCount" and
    ///    <c>AdventureState.CurrentTimeOfDayIndex</c> "Use MapState.CurrentTimeOfDayIndex". The game
    ///    only ever writes the MapState copies. Twenty-five end-turn calls reported changed=False
    ///    against dead fields, so "turn advance does not work" was never established.
    /// 2. <b><c>_performVenueAction</c> is combat-only.</b> Its twelfth line dereferences
    ///    <c>pEncounterEntity.Get&lt;CombatEncounterComponent&gt;().Combats</c>, so a town, market or
    ///    quest board throws — and because it is <c>async void</c>, the exception unwinds silently.
    ///    That is exactly what happened when it was called on the town the party was standing in.
    /// 3. <b>The active character is <c>_roundPlayersEntities[0]</c></b>, an override; the inherited
    ///    <c>_activePlayerIndex</c> is unused on the overworld.
    /// 4. <b>One end-turn is not one round.</b> A round advances only once every party member's turn
    ///    has ended, so with four heroes RoundCount moves on the fourth call.
    ///
    /// Every driver here invokes on the game thread and never blocks. These methods await
    /// <c>Task.Delay</c>, tween sequences and the visual stack; calling <c>.Wait()</c> or
    /// <c>.Result</c> on them deadlocks the pump permanently.
    /// </summary>
    internal static class OverworldCommands
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
            Register("crucible_overworld_state", "CrucibleOverworldState", new List<string>());
            Register("crucible_path_preview", "CruciblePathPreview", new List<string> { "x", "y" });
            Register("crucible_move", "CrucibleMove", new List<string> {
                "x", "y", "consumeActionPoints(true|false)",
                "canBeAmbushed(true|false|auto)", "showEncounterMenu(true|false)" });
            Register("crucible_hex_info", "CrucibleHexInfo", new List<string> { "x", "y" });
            Register("crucible_overworld_end_turn", "CrucibleOverworldEndTurn", new List<string>());
            Register("crucible_interact", "CrucibleInteract", new List<string> { "action (e.g. VENUE)" });
        }

        private static void Register(string command, string method, List<string> hints)
        {
            bool done;
            if (Registered.TryGetValue(command, out done) && done) return;
            MethodInfo handler = typeof(OverworldCommands).GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            Registered[command] = GameBridge.RegisterCommand(command, handler, hints);
        }

        // ============================================================== crucible_overworld_state

        /// <summary>
        /// crucible_overworld_state — the CORRECT observables for turn and time, plus per-character
        /// action points and position.
        ///
        /// Reads <c>AdventureState.MapState</c>, never the obsolete <c>GameRunData.RoundCount</c> or
        /// <c>AdventureState.CurrentTimeOfDayIndex</c>. Both obsolete values are also printed, marked
        /// as such, so a stale reading is recognisable rather than misleading.
        /// </summary>
        public static void CrucibleOverworldState()
        {
            LastResult = null;
            try
            {
                object adventureState;
                string error;
                if (!TryGetAdventureState(out adventureState, out error)) { LastResult = "error: " + error; return; }

                object mapState = PartyAccess.ReadMember(adventureState, "MapState");
                StringBuilder sb = new StringBuilder();

                if (mapState == null)
                {
                    sb.Append("MapState=(null) -- MapStates[ActiveMapID] did not resolve");
                }
                else
                {
                    sb.Append("roundCount=").Append(Str(PartyAccess.ReadMember(mapState, "RoundCount")))
                      .Append(" timeOfDayIndex=").Append(Str(PartyAccess.ReadMember(mapState, "CurrentTimeOfDayIndex")))
                      .Append(" timeOfDay=").Append(Str(PartyAccess.ReadMember(mapState, "TimeOfDay")))
                      .Append(" weather=").Append(Str(PartyAccess.ReadMember(mapState, "CurrentWeather")))
                      .Append(" gameStage=").Append(Str(PartyAccess.ReadMember(mapState, "GameStageIndex")));
                }

                sb.Append("\ntotalRoundCount=").Append(Str(PartyAccess.ReadMember(adventureState, "TotalRoundCount")))
                  .Append(" activeMapId=").Append(Str(PartyAccess.ReadMember(adventureState, "ActiveMapID")));

                sb.Append("\n[obsolete, do not assert on these] GameRunData.RoundCount=")
                  .Append(Str(PartyAccess.ReadMember(GameRun(), "RoundCount")))
                  .Append(" AdventureState.CurrentTimeOfDayIndex=")
                  .Append(Str(PartyAccess.ReadMember(adventureState, "CurrentTimeOfDayIndex")));

                object director = FindDirector();
                sb.Append("\ninteractionEnabled=").Append(Str(PartyAccess.ReadMember(director, "_interactionEnabled")))
                  .Append(" userPickHexPending=").Append(PartyAccess.ReadMember(director, "_userPickHex") != null);

                IEnumerable roundPlayers = PartyAccess.ReadMember(director, "_roundPlayersEntities") as IEnumerable;
                sb.Append("\nturnOrder (index 0 is ACTIVE):");
                int n = 0;
                if (roundPlayers != null)
                {
                    foreach (object entity in roundPlayers)
                    {
                        object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                        sb.Append("\n  [").Append(n++).Append("] ")
                          .Append(Str(PartyAccess.ReadMember(character, "DisplayName")))
                          .Append(" guid=").Append(Str(PartyAccess.ReadMember(entity, "Guid")));
                    }
                }
                if (n == 0) sb.Append(" (empty -- no character can act; _doEndTurn would throw)");

                List<object> party;
                if (PartyAccess.TryGetParty(out party, out error))
                {
                    sb.Append("\nparty:");
                    for (int i = 0; i < party.Count; i++)
                    {
                        object player = PartyAccess.FindComponent(party[i], "PlayerComponent");
                        object character = PartyAccess.FindComponent(party[i], "CharacterComponent");
                        object adventure = AdventureComponentOf(party[i]);
                        sb.Append("\n  [").Append(i).Append("] ")
                          .Append(Str(PartyAccess.ReadMember(character, "DisplayName")))
                          .Append(" hex=").Append(adventure == null ? "(none)" : Str(PartyAccess.ReadMember(adventure, "HexPosition")))
                          .Append(" ap=").Append(Str(PartyAccess.ReadMember(player, "ActionPoints")))
                          .Append(" hasMoved=").Append(Str(PartyAccess.ReadMember(player, "HasMoved")))
                          .Append(" turnsPlayed=").Append(Str(PartyAccess.ReadMember(player, "TurnsPlayed")));
                    }
                }

                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_overworld_state threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_path_preview

        /// <summary>
        /// crucible_path_preview &lt;x&gt; &lt;y&gt; — can the active character reach that hex, and at
        /// what cost? Read-only: it computes the move data without executing it.
        /// </summary>
        public static void CruciblePathPreview(string x, string y)
        {
            LastResult = null;
            try
            {
                int goalX, goalY;
                if (!int.TryParse((x ?? "").Trim(), out goalX) || !int.TryParse((y ?? "").Trim(), out goalY))
                {
                    LastResult = "error: usage: crucible_path_preview <x> <y>";
                    return;
                }

                object moveData;
                string error;
                if (!TryBuildMoveData(goalX, goalY, true, out moveData, out error)) { LastResult = "error: " + error; return; }
                LastResult = DescribeMoveData(moveData, goalX, goalY);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_path_preview threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_move

        /// <summary>
        /// crucible_move &lt;x&gt; &lt;y&gt; [consumeActionPoints] — move the active character.
        ///
        /// Refuses when the computed path reports <c>IsValidMove=false</c> rather than firing and
        /// hoping: an invalid move is silently ignored by the game, which is indistinguishable from
        /// a harness fault. Passing consumeActionPoints=false gives the pathfinder unlimited range,
        /// which is the game's own free-move path.
        ///
        /// <c>canBeAmbushed</c> and <c>showEncounterMenu</c> default to FALSE for traversal. They are
        /// the two ways a scripted move stops being a move: an ambush drops the party into a combat
        /// nobody asked for, and the encounter menu opens a UI that several branches (market, town
        /// services, quest board) never close by themselves. Pass <c>canBeAmbushed=auto</c> to restore
        /// the game's own behaviour, which follows <c>PlayerComponent.Sneaked</c>.
        /// </summary>
        public static void CrucibleMove(string x, string y, string consumeActionPoints,
            string canBeAmbushedArg, string showEncounterMenuArg)
        {
            LastResult = null;
            try
            {
                int goalX, goalY;
                if (!int.TryParse((x ?? "").Trim(), out goalX) || !int.TryParse((y ?? "").Trim(), out goalY))
                {
                    LastResult = "error: usage: crucible_move <x> <y> [consumeAP] [canBeAmbushed|auto] [showEncounterMenu]";
                    return;
                }
                bool consume = !string.Equals((consumeActionPoints ?? "true").Trim(), "false", StringComparison.OrdinalIgnoreCase);

                object moveData;
                string error;
                if (!TryBuildMoveData(goalX, goalY, consume, out moveData, out error)) { LastResult = "error: " + error; return; }

                string describe = DescribeMoveData(moveData, goalX, goalY);
                object isValid = PartyAccess.ReadMember(moveData, "IsValidMove");
                if (!(isValid is bool) || !(bool)isValid)
                {
                    LastResult = describe + "\nREFUSED: IsValidMove is false, so the move was not attempted.";
                    return;
                }

                object director = FindDirector();
                MethodInfo move = null;
                foreach (MethodInfo m in director.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_move", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 4 && string.Equals(ps[0].ParameterType.Name, "AdventureMoveData", StringComparison.Ordinal))
                    {
                        move = m;
                        break;
                    }
                }
                if (move == null) { LastResult = describe + "\nerror: AdventureDirector._move(AdventureMoveData, ...) not found"; return; }

                // Defaults are not supplied by reflection, so all four arguments are passed
                // explicitly. "auto" reproduces the game's own call, where canBeAmbushed follows
                // PlayerComponent.Sneaked; the harness default is a flat false.
                string ambushArg = (canBeAmbushedArg ?? "false").Trim();
                bool canBeAmbushed;
                if (string.Equals(ambushArg, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    object active = ActiveCharacter();
                    object player = PartyAccess.FindComponent(active, "PlayerComponent");
                    object sneaked = PartyAccess.ReadMember(player, "Sneaked");
                    canBeAmbushed = !(sneaked is bool && (bool)sneaked);
                }
                else
                {
                    canBeAmbushed = string.Equals(ambushArg, "true", StringComparison.OrdinalIgnoreCase);
                }

                bool showEncounterMenu = string.Equals((showEncounterMenuArg ?? "false").Trim(), "true",
                    StringComparison.OrdinalIgnoreCase);

                move.Invoke(director, new object[] { moveData, consume, canBeAmbushed, showEncounterMenu });

                LastResult = describe
                    + "\nmove invoked (consumeAP=" + consume + " canBeAmbushed=" + canBeAmbushed
                    + " showEncounterMenu=" + showEncounterMenu + ")"
                    + "\nNOTE: _move returns a Task that is NOT awaited -- it plays a visual stack and"
                    + "\n      then chains into the encounter menu or end-of-turn. Re-read"
                    + "\n      crucible_overworld_state after a few seconds.";
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: _move threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_move threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_hex_info

        /// <summary>crucible_hex_info &lt;x&gt; &lt;y&gt; — terrain and encounter on a hex, with its legal actions.</summary>
        public static void CrucibleHexInfo(string x, string y)
        {
            LastResult = null;
            try
            {
                int hexX, hexY;
                if (!int.TryParse((x ?? "").Trim(), out hexX) || !int.TryParse((y ?? "").Trim(), out hexY))
                {
                    LastResult = "error: usage: crucible_hex_info <x> <y>";
                    return;
                }

                Array grid;
                string error;
                if (!TryGetHexGrid(out grid, out error)) { LastResult = "error: " + error; return; }
                if (hexX < 0 || hexY < 0 || hexX >= grid.GetLength(0) || hexY >= grid.GetLength(1))
                {
                    LastResult = "error: (" + hexX + ", " + hexY + ") is outside the "
                        + grid.GetLength(0) + "x" + grid.GetLength(1) + " grid";
                    return;
                }

                IEnumerable cell = grid.GetValue(hexX, hexY) as IEnumerable;
                StringBuilder sb = new StringBuilder();
                sb.Append("hex=(").Append(hexX).Append(", ").Append(hexY).Append(")");

                if (cell == null) { LastResult = sb + " (empty)"; return; }

                foreach (object entity in cell)
                {
                    if (entity == null) continue;
                    object hex = PartyAccess.FindComponent(entity, "HexComponent");
                    if (hex != null)
                    {
                        sb.Append("\n  terrain biome=").Append(Str(PartyAccess.ReadMember(hex, "BiomeName")))
                          .Append(" zone=").Append(Str(PartyAccess.ReadMember(hex, "ZoneName")))
                          .Append(" visibility=").Append(Str(PartyAccess.ReadMember(hex, "VisibilityState")));
                    }

                    object encounter = PartyAccess.FindComponent(entity, "EncounterComponent");
                    if (encounter != null)
                    {
                        sb.Append("\n  encounter guid=").Append(Str(PartyAccess.ReadMember(entity, "Guid")))
                          .Append(" type=").Append(Str(PartyAccess.ReadMember(encounter, "Type")))
                          .Append("\n    actions: ").Append(RenderList(PartyAccess.ReadMember(encounter, "ActionList")))
                          .Append("\n    properties: ").Append(RenderList(PartyAccess.ReadMember(encounter, "Properties")))
                          .Append("\n    isCombat=").Append(PartyAccess.FindComponent(entity, "CombatEncounterComponent") != null);
                    }

                    object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                    if (character != null)
                    {
                        sb.Append("\n  character ").Append(Str(PartyAccess.ReadMember(character, "DisplayName")))
                          .Append(" class=").Append(Str(PartyAccess.ReadMember(character, "ConfigName")));
                    }
                }
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_hex_info threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_overworld_end_turn

        /// <summary>
        /// crucible_overworld_end_turn — end the active character's turn via the game's own
        /// <c>_tryProceed(true)</c>.
        ///
        /// Confirm success by watching <c>PlayerComponent.TurnsPlayed</c>, NOT RoundCount: a round
        /// only advances once every party member has ended a turn.
        /// </summary>
        public static void CrucibleOverworldEndTurn()
        {
            LastResult = null;
            try
            {
                object director = FindDirector();
                if (director == null) { LastResult = "error: AdventureDirector unavailable"; return; }

                object active = ActiveCharacter();
                if (active == null)
                {
                    LastResult = "error: no active character (_roundPlayersEntities is empty) -- ending a turn now would throw";
                    return;
                }

                object player = PartyAccess.FindComponent(active, "PlayerComponent");
                object turnsBefore = PartyAccess.ReadMember(player, "TurnsPlayed");
                object roundBefore = RoundCount();

                MethodInfo tryProceed = null;
                foreach (MethodInfo m in director.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_tryProceed", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 1) { tryProceed = m; break; }
                }
                if (tryProceed == null) { LastResult = "error: AdventureDirector._tryProceed(Boolean) not found"; return; }

                tryProceed.Invoke(director, new object[] { true });

                LastResult = "active=" + Str(PartyAccess.ReadMember(active, "Guid"))
                    + " turnsPlayedBefore=" + Str(turnsBefore)
                    + " roundCountBefore=" + Str(roundBefore)
                    + "\nNOTE: _tryProceed is async void -- it returns immediately and any failure"
                    + "\n      appears in the BepInEx log. Confirm with crucible_overworld_state:"
                    + "\n      TurnsPlayed increments per turn; RoundCount only after every member.";
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: _tryProceed threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_overworld_end_turn threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_interact

        /// <summary>
        /// crucible_interact &lt;action&gt; — perform an encounter action on the hex the active
        /// character is standing on. Use VENUE to start a fight.
        ///
        /// This is the game's own path and the reason a direct _performVenueAction call does
        /// nothing useful. The sequence is:
        ///   1. _tryShowEncounterMenu(activeChar, encounterEntity, false) — this is what assigns
        ///      _encounterEntity and _proxyEncounterEntity and resolves PROXY encounters to their
        ///      real target. Setting those fields by hand skips that resolution.
        ///   2. _performEncounterAction(context, pAllowBroadcast: false) — every branch of that
        ///      method begins by checking PlayingOnlineMultiplayer &amp;&amp; pAllowBroadcast and returning
        ///      after broadcasting, so passing true would turn this into a no-op in multiplayer.
        ///      The VENUE branch closes the menu and then calls _performVenueAction itself.
        ///
        /// The action is validated against EncounterComponent.ActionList first: an action the
        /// encounter does not offer is refused here rather than silently doing nothing.
        ///
        /// The context is the exact seven-field shape the game constructs internally
        /// (Layout, EncounterEntity, ViewingCharacterEntity, ActiveCharacterEntity, Action,
        /// ProxyEntity, ViewOnly) — the type has fifteen fields, but the game itself fills only
        /// these, which is the evidence that the rest are optional.
        /// </summary>
        public static void CrucibleInteract(string action)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(action))
                {
                    LastResult = "error: usage: crucible_interact <action>  (e.g. VENUE to start a fight)";
                    return;
                }
                action = action.Trim().ToUpperInvariant();

                object director = FindDirector();
                if (director == null) { LastResult = "error: AdventureDirector unavailable"; return; }

                object active = ActiveCharacter();
                if (active == null) { LastResult = "error: no active character"; return; }

                object adventure = AdventureComponentOf(active);
                object position = adventure == null ? null : PartyAccess.ReadMember(adventure, "HexPosition");
                if (position == null) { LastResult = "error: active character has no hex position"; return; }

                object encounterEntity;
                string findError;
                if (!TryFindEncounterAt(position, out encounterEntity, out findError))
                {
                    LastResult = "error: " + findError;
                    return;
                }

                object encounterComponent = PartyAccess.FindComponent(encounterEntity, "EncounterComponent");
                string actions = RenderList(PartyAccess.ReadMember(encounterComponent, "ActionList"));
                if (actions.IndexOf(action, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    LastResult = "REFUSED: this encounter does not offer " + action
                        + "\nhex=" + position + " encounter=" + Str(PartyAccess.ReadMember(encounterEntity, "Guid"))
                        + "\nactions: " + actions;
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("hex=").Append(position)
                  .Append(" encounter=").Append(Str(PartyAccess.ReadMember(encounterEntity, "Guid")))
                  .Append(" action=").Append(action);

                // Step 1: let the game set up its own encounter state.
                MethodInfo showMenu = null;
                foreach (MethodInfo m in director.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_tryShowEncounterMenu", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 3) { showMenu = m; break; }
                }
                if (showMenu == null) { LastResult = sb + "\nerror: _tryShowEncounterMenu(3 args) not found"; return; }
                showMenu.Invoke(director, new object[] { active, encounterEntity, false });

                object resolved = PartyAccess.ReadMember(director, "_encounterEntity") ?? encounterEntity;
                object proxy = PartyAccess.ReadMember(director, "_proxyEncounterEntity");
                sb.Append("\nafterShowMenu: _encounterEntity=").Append(Str(PartyAccess.ReadMember(resolved, "Guid")))
                  .Append(" proxy=").Append(proxy == null ? "(none)" : Str(PartyAccess.ReadMember(proxy, "Guid")));

                // Step 2: build the context and perform the action.
                Type helperType = AccessTools.TypeByName("EncounterMenuViewHelper2");
                if (helperType == null) { LastResult = sb + "\nerror: EncounterMenuViewHelper2 not found"; return; }
                Type contextType = helperType.GetNestedType("EncounterActionContext",
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (contextType == null) { LastResult = sb + "\nerror: EncounterActionContext nested type not found"; return; }

                object context = Activator.CreateInstance(contextType);
                SetField(contextType, context, "EncounterEntity", resolved);
                SetField(contextType, context, "ViewingCharacterEntity", active);
                SetField(contextType, context, "ActiveCharacterEntity", active);
                SetField(contextType, context, "ProxyEntity", proxy);
                SetField(contextType, context, "ViewOnly", false);

                FieldInfo actionField = AccessTools.Field(contextType, "Action");
                if (actionField == null) { LastResult = sb + "\nerror: EncounterActionContext.Action not found"; return; }
                object actionValue;
                try { actionValue = Enum.Parse(actionField.FieldType, action, true); }
                catch (Exception)
                {
                    LastResult = sb + "\nerror: '" + action + "' is not an eEncounterActions member; valid: "
                        + string.Join(", ", Enum.GetNames(actionField.FieldType));
                    return;
                }
                actionField.SetValue(context, actionValue);

                MethodInfo perform = null;
                foreach (MethodInfo m in director.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_performEncounterAction", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 2) { perform = m; break; }
                }
                if (perform == null) { LastResult = sb + "\nerror: _performEncounterAction(2 args) not found"; return; }

                string routeBefore = ReadRoute();
                perform.Invoke(director, new object[] { context, false });

                sb.Append("\nrouteBefore=").Append(routeBefore).Append(" routeAfter=").Append(ReadRoute());
                sb.Append("\nNOTE: _performEncounterAction is async void and the venue transition");
                sb.Append("\n      waits ~0.75s before raising the route change. Re-read state.");
                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_interact: " + LastResult);
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: interact threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_interact threw: " + ex.Message;
            }
        }

        private static void SetField(Type type, object instance, string name, object value)
        {
            FieldInfo f = AccessTools.Field(type, name);
            if (f != null) f.SetValue(instance, value);
        }

        /// <summary>
        /// Finds the interactable encounter on a hex, using the same ordering the game uses when a
        /// character arrives: vehicles first, and skipping PROP / NO_MENU encounters, which exist
        /// on the map but offer no interaction.
        /// </summary>
        private static bool TryFindEncounterAt(object position, out object encounterEntity, out string error)
        {
            encounterEntity = null;
            error = null;

            Array grid;
            if (!TryGetHexGrid(out grid, out error)) return false;

            int x = Convert.ToInt32(PartyAccess.ReadMember(position, "Item1"));
            int y = Convert.ToInt32(PartyAccess.ReadMember(position, "Item2"));
            if (x < 0 || y < 0 || x >= grid.GetLength(0) || y >= grid.GetLength(1))
            {
                error = "hex " + position + " is outside the grid";
                return false;
            }

            IEnumerable cell = grid.GetValue(x, y) as IEnumerable;
            if (cell == null) { error = "hex " + position + " is empty"; return false; }

            foreach (object entity in cell)
            {
                object component = PartyAccess.FindComponent(entity, "EncounterComponent");
                if (component == null) continue;

                string properties = RenderList(PartyAccess.ReadMember(component, "Properties"));
                if (properties.IndexOf("PROP", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (properties.IndexOf("NO_MENU", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                encounterEntity = entity;
                return true;
            }

            error = "no interactable encounter on hex " + position;
            return false;
        }

        // ============================================================== helpers

        private static bool TryBuildMoveData(int goalX, int goalY, bool consume, out object moveData, out string error)
        {
            moveData = null;
            error = null;

            object director = FindDirector();
            if (director == null) { error = "AdventureDirector unavailable (is the overworld loaded?)"; return false; }

            object active = ActiveCharacter();
            if (active == null) { error = "no active character (_roundPlayersEntities is empty)"; return false; }

            MethodInfo build = null;
            foreach (MethodInfo m in director.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(m.Name, "_getAdventureMoveData", StringComparison.Ordinal)) continue;
                if (m.GetParameters().Length == 3) { build = m; break; }
            }
            if (build == null) { error = "AdventureDirector._getAdventureMoveData(3 args) not found"; return false; }

            Type tupleType = build.GetParameters()[1].ParameterType;
            object goal = Activator.CreateInstance(tupleType, new object[] { goalX, goalY });

            try
            {
                moveData = build.Invoke(director, new object[] { active, goal, consume });
                if (moveData == null) { error = "_getAdventureMoveData returned null"; return false; }
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = "_getAdventureMoveData threw: " + root.Message;
                return false;
            }
        }

        private static string DescribeMoveData(object moveData, int goalX, int goalY)
        {
            object path = PartyAccess.ReadMember(moveData, "Path");
            ICollection steps = path as ICollection;
            int cost = -1;
            if (path is IEnumerable)
            {
                object last = null;
                foreach (object node in (IEnumerable)path) last = node;
                if (last != null)
                {
                    object pathCost = PartyAccess.ReadMember(last, "PathCost");
                    if (pathCost != null) int.TryParse(pathCost.ToString(), out cost);
                }
            }

            return "goal=(" + goalX + ", " + goalY + ")"
                + " start=" + Str(PartyAccess.ReadMember(moveData, "Start"))
                + " isValidMove=" + Str(PartyAccess.ReadMember(moveData, "IsValidMove"))
                + " actionPoints=" + Str(PartyAccess.ReadMember(moveData, "ActionPoints"))
                + " pathSteps=" + (steps == null ? -1 : steps.Count)
                + " pathCost=" + cost;
        }

        private static bool TryGetHexGrid(out Array grid, out string error)
        {
            grid = null;
            error = null;

            object director = FindDirector();
            object hexMap = director == null ? null : PartyAccess.ReadMember(director, "_hexMap");
            if (hexMap == null)
            {
                object env = GameBridge.GetEnv();
                hexMap = env == null ? null : PartyAccess.ReadMember(env, "HexMap");
            }
            grid = hexMap as Array;
            if (grid == null || grid.Rank != 2) { error = "no 2D hex map available"; return false; }
            return true;
        }

        private static bool TryGetAdventureState(out object adventureState, out string error)
        {
            adventureState = null;
            error = null;
            object gameRun = GameRun();
            if (gameRun == null) { error = "no run loaded"; return false; }
            adventureState = PartyAccess.ReadMember(gameRun, "AdventureState");
            if (adventureState == null) { error = "GameRunData.AdventureState is null"; return false; }
            return true;
        }

        private static object GameRun()
        {
            object env = GameBridge.GetEnv();
            return env == null ? null : PartyAccess.ReadMember(env, "GameRun");
        }

        private static object RoundCount()
        {
            object adventureState;
            string error;
            if (!TryGetAdventureState(out adventureState, out error)) return null;
            object mapState = PartyAccess.ReadMember(adventureState, "MapState");
            return mapState == null ? null : PartyAccess.ReadMember(mapState, "RoundCount");
        }

        private static object FindDirector()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                FieldInfo routerField = routerHelper == null ? null : AccessTools.Field(routerHelper, "_router");
                object router = routerField == null ? null : routerField.GetValue(null);
                return router == null ? null : PartyAccess.ReadMember(router, "_adventureDirector");
            }
            catch (Exception) { return null; }
        }

        /// <summary>The active overworld character is _roundPlayersEntities[0]; there is no setter.</summary>
        private static object ActiveCharacter()
        {
            object director = FindDirector();
            if (director == null) return null;

            object active = PartyAccess.ReadMember(director, "_activeCharacterEntity");
            if (active != null) return active;

            IEnumerable roundPlayers = PartyAccess.ReadMember(director, "_roundPlayersEntities") as IEnumerable;
            if (roundPlayers == null) return null;
            foreach (object entity in roundPlayers) return entity;
            return null;
        }

        private static object AdventureComponentOf(object entity)
        {
            try
            {
                object gameRun = GameRun();
                MethodInfo tryGet = AccessTools.Method(entity.GetType(), "TryGetAdventureComponent");
                if (tryGet == null || gameRun == null) return null;
                object[] args = new object[] { gameRun, null };
                object ok = tryGet.Invoke(entity, args);
                return (ok is bool && (bool)ok) ? args[1] : null;
            }
            catch (Exception) { return null; }
        }

        private static string RenderList(object value)
        {
            if (value == null) return "(null)";
            IEnumerable list = value as IEnumerable;
            if (list == null) return value.ToString();
            List<string> parts = new List<string>();
            foreach (object item in list) parts.Add(item == null ? "(null)" : item.ToString());
            parts.Sort(StringComparer.Ordinal);
            return "count=" + parts.Count + " [" + string.Join(", ", parts.ToArray()) + "]";
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
