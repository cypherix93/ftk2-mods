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
    /// Forces a combat encounter — the gate every ability assertion sits behind.
    ///
    /// Combat had never been reached programmatically. The research docs concluded that entering it
    /// was infeasible ("traversal is a graph walk through legal transitions, not teleportation"),
    /// but that was measured against the wrong lever. Verified live 2026-08-23:
    /// <c>RouterMono.Route(eRoutes.COMBAT, 0, null, false, true)</c> DOES switch the route to COMBAT.
    /// It produces an empty fight — no CombatState, no combatants — because the enemy payload was
    /// null. So the problem was never the routing; it is the payload.
    ///
    /// Verified API surface (TypeProbe --signatures, 2026-08-23):
    ///   RouterMono.Route(eRoutes pTargetRoute, Int32 pRandomSeed, Object pCustomData,
    ///                    Boolean pReload, Boolean pForceRoute)
    ///   CombatPhase.Initialize(Object pEnemyEntityNames, Int32 pRandomSeed, VenueState, …)
    ///   MapGenHelper._createDefaultCombatArg(Boolean pIsCamp, Boolean pIsBoat, List pProperties) static
    ///   CombatEncounterArgs fields: EnemyNames, EnemyGuids, WaveAmount, BossID, LootDropID, …
    ///   CharacterHelper.DEBUG_* — 29 real enemy config names (e.g. DEBUG_WOLF = WOLF_CRAGS_00)
    ///
    /// <c>Route</c>'s <c>pCustomData</c> reaches <c>CombatPhase.Initialize</c> as
    /// <c>pEnemyEntityNames</c>, and both are typed <c>Object</c>, so the concrete shape it expects
    /// cannot be read off a signature. The <c>payloadKind</c> argument therefore selects the shape
    /// rather than guessing one: a wrong guess baked into the code would look like "combat entry is
    /// impossible" instead of "that payload was wrong".
    /// </summary>
    internal static class CombatCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _registered;
        private static bool _encounterRegistered;
        private static bool _engageRegistered;
        private static bool _mapRegistered;
        private static bool _hexRegistered;
        private static bool _revealRegistered;
        private static bool _loggedRegistration;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (_registered) return;
            _registered = GameBridge.RegisterCommand("crucible_force_combat",
                typeof(CombatCommands).GetMethod("CrucibleForceCombat", BindingFlags.Public | BindingFlags.Static),
                new List<string> { "enemyConfigName", "count", "payloadKind(names|args|entities)" });

            if (!_encounterRegistered)
                _encounterRegistered = GameBridge.RegisterCommand("crucible_force_encounter",
                    typeof(CombatCommands).GetMethod("CrucibleForceEncounter", BindingFlags.Public | BindingFlags.Static),
                    new List<string>());

            if (!_engageRegistered)
                _engageRegistered = GameBridge.RegisterCommand("crucible_engage_encounter",
                    typeof(CombatCommands).GetMethod("CrucibleEngageEncounter", BindingFlags.Public | BindingFlags.Static),
                    new List<string>());

            if (!_mapRegistered)
                _mapRegistered = GameBridge.RegisterCommand("crucible_map_encounters",
                    typeof(CombatCommands).GetMethod("CrucibleMapEncounters", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "filter" });

            if (!_hexRegistered)
                _hexRegistered = GameBridge.RegisterCommand("crucible_party_set_hex",
                    typeof(CombatCommands).GetMethod("CruciblePartySetHex", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "x", "y" });

            if (!_revealRegistered)
                _revealRegistered = GameBridge.RegisterCommand("crucible_reveal_map",
                    typeof(CombatCommands).GetMethod("CrucibleRevealMap", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "Visible|Revealed|Hidden" });

            if (_registered && _encounterRegistered && _engageRegistered && _mapRegistered && _hexRegistered && _revealRegistered && !_loggedRegistration && _log != null)
            {
                _loggedRegistration = true;
                _log.LogInfo("CombatCommands registered (crucible_force_combat, crucible_force_encounter).");
            }
        }

        /// <summary>
        /// crucible_force_combat &lt;enemyConfigName&gt; &lt;count&gt; &lt;payloadKind&gt;
        ///
        /// payloadKind: names = List&lt;string&gt; of config names · args = CombatEncounterArgs with
        /// EnemyNames populated · entities = List&lt;Entity&gt; of constructed enemies.
        /// </summary>
        public static void CrucibleForceCombat(string enemyConfigName, string count, string payloadKind)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(enemyConfigName))
                {
                    LastResult = "error: usage: crucible_force_combat <enemyConfigName> <count> <names|args|entities>";
                    return;
                }
                enemyConfigName = enemyConfigName.Trim();

                int howMany;
                if (!int.TryParse((count ?? "1").Trim(), out howMany) || howMany < 1) howMany = 1;
                if (howMany > 8) howMany = 8; // a wave is small; a huge number is a typo, not a request.

                string kind = string.IsNullOrEmpty(payloadKind) ? "names" : payloadKind.Trim().ToLowerInvariant();

                StringBuilder sb = new StringBuilder();
                sb.Append("enemy=").Append(enemyConfigName).Append(" count=").Append(howMany).Append(" payloadKind=").Append(kind);

                object payload;
                string payloadError;
                if (!TryBuildPayload(kind, enemyConfigName, howMany, out payload, out payloadError))
                {
                    LastResult = sb + "\nerror: " + payloadError;
                    return;
                }
                sb.Append("\npayloadType=").Append(payload == null ? "(null)" : payload.GetType().Name);

                string routeError;
                string before = ReadRoute();
                bool routed = TryRoute(payload, out routeError);
                sb.Append("\nrouteBefore=").Append(before)
                  .Append(" routeInvoked=").Append(routed)
                  .Append(" routeAfter=").Append(ReadRoute());
                if (!routed) sb.Append("\nrouteError=").Append(routeError);

                sb.Append("\nNOTE: the phase initializes asynchronously. Re-read /state?schema=v2 after a");
                sb.Append("\n      few seconds; combat.active only becomes true once CombatState has entities.");

                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_force_combat: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_force_combat threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_reveal_map

        /// <summary>
        /// crucible_reveal_map [Visible|Revealed|Hidden] - sets every hex visibility state,
        /// removing fog of war.
        ///
        /// Fog is not cosmetic for a test harness: an unexplored hex has no clickable target, so a
        /// party teleported into unrevealed map is surrounded by cloud and every movement click does
        /// nothing. That presents exactly like broken mouse input, and cost a long detour before a
        /// screenshot showed the clouds. Revealing the map removes the whole class of confusion and
        /// makes teleport-anywhere usable.
        ///
        /// Verified API surface (TypeProbe --signatures, 2026-08-23):
        ///   HexComponent.VisibilityState : eHexVisibility
        ///   eHexVisibility members: Hidden, Revealed, Visible
        ///   Env.HexMaps : Dictionary(String, List(Entity)[,])
        ///
        /// Every map is revealed, not just the one the party is on, so a later teleport to another
        /// map does not silently land back in fog.
        /// </summary>
        public static void CrucibleRevealMap(string state)
        {
            LastResult = null;
            try
            {
                string wanted = string.IsNullOrEmpty(state) ? "Visible" : state.Trim();

                Type visibilityEnum = AccessTools.TypeByName("eHexVisibility");
                if (visibilityEnum == null) { LastResult = "error: eHexVisibility type not found"; return; }

                object target;
                try { target = Enum.Parse(visibilityEnum, wanted, true); }
                catch (Exception)
                {
                    LastResult = "error: '" + wanted + "' is not an eHexVisibility member; valid: "
                        + string.Join(", ", Enum.GetNames(visibilityEnum));
                    return;
                }

                object env = GameBridge.GetEnv();
                if (env == null) { LastResult = "error: Env unavailable"; return; }

                IDictionary maps = PartyAccess.ReadMember(env, "HexMaps") as IDictionary;
                if (maps == null) { LastResult = "error: Env.HexMaps is not a dictionary"; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("target=").Append(wanted);

                int totalHexes = 0;
                int changed = 0;

                foreach (DictionaryEntry entry in maps)
                {
                    Array grid = entry.Value as Array;
                    if (grid == null || grid.Rank != 2) continue;

                    int mapChanged = 0;
                    int mapHexes = 0;

                    for (int x = 0; x < grid.GetLength(0); x++)
                    {
                        for (int y = 0; y < grid.GetLength(1); y++)
                        {
                            IEnumerable cell = grid.GetValue(x, y) as IEnumerable;
                            if (cell == null) continue;
                            foreach (object entity in cell)
                            {
                                object hex = PartyAccess.FindComponent(entity, "HexComponent");
                                if (hex == null) continue;

                                mapHexes++;
                                FieldInfo field = AccessTools.Field(hex.GetType(), "VisibilityState");
                                if (field == null) continue;

                                object before = field.GetValue(hex);
                                if (before != null && before.Equals(target)) continue;
                                field.SetValue(hex, target);
                                mapChanged++;
                            }
                        }
                    }

                    totalHexes += mapHexes;
                    changed += mapChanged;
                    sb.Append("\nmap ").Append(entry.Key).Append(": hexes=").Append(mapHexes)
                      .Append(" changed=").Append(mapChanged);
                }

                sb.Append("\ntotalHexes=").Append(totalHexes).Append(" changed=").Append(changed);
                sb.Append("\nNOTE: this sets state only. The view refreshes on the next redraw the");
                sb.Append("\n      game performs; it is not repainted here.");
                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_reveal_map: totalHexes=" + totalHexes + " changed=" + changed);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_reveal_map threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_map_encounters

        /// <summary>
        /// crucible_map_encounters [filter] - scans the hex map and reports every encounter entity
        /// with its hex position.
        ///
        /// Spawning an encounter places it somewhere on the map, not necessarily under the party, so
        /// engaging one first requires knowing where it landed. Env.HexMaps maps a MapID to a
        /// two-dimensional array of entity lists - one list per hex - so a scan is a walk over that
        /// grid looking for entities carrying an EncounterComponent.
        /// </summary>
        public static void CrucibleMapEncounters(string filter)
        {
            LastResult = null;
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) { LastResult = "error: Env unavailable"; return; }

                object gameRun = PartyAccess.ReadMember(env, "GameRun");
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                List<object> party;
                string partyError;
                if (!PartyAccess.TryGetParty(out party, out partyError)) { LastResult = "error: " + partyError; return; }

                object component;
                string componentError;
                if (!TryGetAdventureComponent(party[0], gameRun, out component, out componentError))
                {
                    LastResult = "error: " + componentError;
                    return;
                }
                object mapId = PartyAccess.ReadMember(component, "MapID");

                IDictionary maps = PartyAccess.ReadMember(env, "HexMaps") as IDictionary;
                if (maps == null || mapId == null || !maps.Contains(mapId))
                {
                    LastResult = "error: no hex map for MapID " + (mapId == null ? "(null)" : mapId.ToString());
                    return;
                }

                Array grid = maps[mapId] as Array;
                if (grid == null || grid.Rank != 2) { LastResult = "error: hex map is not a 2D array"; return; }

                bool hasFilter = !string.IsNullOrEmpty(filter) && filter.Trim() != "-";
                string wanted = hasFilter ? filter.Trim() : null;

                StringBuilder sb = new StringBuilder();
                sb.Append("mapId=").Append(mapId).Append(" partyHex=").Append(StringOfMember(component, "HexPosition"));

                int rows = grid.GetLength(0);
                int cols = grid.GetLength(1);
                int found = 0;

                for (int x = 0; x < rows; x++)
                {
                    for (int y = 0; y < cols; y++)
                    {
                        IEnumerable cell = grid.GetValue(x, y) as IEnumerable;
                        if (cell == null) continue;
                        foreach (object entity in cell)
                        {
                            if (entity == null) continue;
                            object encounterComponent = PartyAccess.FindComponent(entity, "EncounterComponent");
                            if (encounterComponent == null) continue;

                            string configName = StringOfMember(encounterComponent, "ConfigName");
                            if (configName == "(null)") configName = StringOfMember(entity, "Guid");
                            if (wanted != null && configName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            found++;
                            if (found <= 40)
                            {
                                sb.Append("\n[").Append(x).Append(",").Append(y).Append("] ")
                                  .Append(configName)
                                  .Append(" guid=").Append(StringOfMember(entity, "Guid"));
                            }
                        }
                    }
                }

                sb.Append("\ngridSize=").Append(rows).Append("x").Append(cols)
                  .Append(" encountersFound=").Append(found);
                if (found > 40) sb.Append(" (showing first 40)");
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_map_encounters threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_party_set_hex

        /// <summary>
        /// crucible_party_set_hex &lt;x&gt; &lt;y&gt; - teleports the whole party to a hex by writing
        /// AdventureComponent.HexPosition.
        ///
        /// This is what makes a fixture able to start anywhere. Overworld movement is mouse and hex
        /// driven and the confirm gesture is still unsolved, so the party could not be repositioned
        /// at all; and the game teleport ability is not usable here either, since
        /// _doTeleportAbility takes a Thing (a teleport scroll) rather than a destination.
        ///
        /// It writes state directly and does NOT run whatever the game does on arrival - no hex
        /// reveal, no encounter trigger, no movement cost. That is the point for a fixture, but it
        /// means arriving on a hex is not the same as walking onto it.
        ///
        /// Verified API surface: AdventureComponent.HexPosition : ValueTuple(Int32,Int32),
        /// Entity.TryGetAdventureComponent(GameRunData, out AdventureComponent).
        /// </summary>
        public static void CruciblePartySetHex(string x, string y)
        {
            LastResult = null;
            try
            {
                int hexX, hexY;
                if (!int.TryParse((x ?? "").Trim(), out hexX) || !int.TryParse((y ?? "").Trim(), out hexY))
                {
                    LastResult = "error: usage: crucible_party_set_hex <x> <y>";
                    return;
                }

                object env = GameBridge.GetEnv();
                object gameRun = env == null ? null : PartyAccess.ReadMember(env, "GameRun");
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                List<object> party;
                string partyError;
                if (!PartyAccess.TryGetParty(out party, out partyError)) { LastResult = "error: " + partyError; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("target=(").Append(hexX).Append(", ").Append(hexY).Append(")");

                int moved = 0;
                for (int i = 0; i < party.Count; i++)
                {
                    object component;
                    string componentError;
                    if (!TryGetAdventureComponent(party[i], gameRun, out component, out componentError))
                    {
                        sb.Append("\n[").Append(i).Append("] skipped: ").Append(componentError);
                        continue;
                    }

                    FieldInfo field = AccessTools.Field(component.GetType(), "HexPosition");
                    if (field == null) { sb.Append("\n[").Append(i).Append("] no HexPosition field"); continue; }

                    object before = field.GetValue(component);
                    object tuple = Activator.CreateInstance(field.FieldType, new object[] { hexX, hexY });
                    field.SetValue(component, tuple);
                    object after = field.GetValue(component);

                    bool changed = !string.Equals(before.ToString(), after.ToString(), StringComparison.Ordinal);
                    if (changed) moved++;
                    sb.Append("\n[").Append(i).Append("] ").Append(before).Append(" -> ").Append(after)
                      .Append(" changed=").Append(changed);
                }

                sb.Append("\nmovedCount=").Append(moved).Append("/").Append(party.Count);
                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_party_set_hex: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_party_set_hex threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_engage_encounter

        /// <summary>
        /// crucible_engage_encounter - finds the encounter on the party hex and performs it, which
        /// is the game own ADVENTURE -> VENUE -> COMBAT transition.
        ///
        /// This is the step that a spawned encounter still needs. Spawning places an encounter
        /// entity on the map; nothing happens until a character acts on it, which normally means
        /// walking onto the hex. Performing the venue action directly skips the movement we cannot
        /// yet drive, while still going through the real transition that builds the Scene, Diorama,
        /// camera and VenueState that CombatPhase.Initialize requires - the exact context a forced
        /// Route(COMBAT) does not produce.
        ///
        /// Verified API surface (TypeProbe --signatures, 2026-08-23):
        ///   AdventureHelper.GetPlayerEncounter(Entity pCharacter, Env pEnv) static -> Entity
        ///   AdventureDirector._performVenueAction(Entity pActiveCharacter, Entity pEncounterEntity,
        ///       String pLootTableArg)
        ///   AdventureDirector._encounterEntity / ._viewingEncounterEntity : Entity
        /// </summary>
        public static void CrucibleEngageEncounter()
        {
            LastResult = null;
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) { LastResult = "error: Env unavailable"; return; }

                List<object> party;
                string partyError;
                if (!PartyAccess.TryGetParty(out party, out partyError)) { LastResult = "error: " + partyError; return; }
                object active = party[0];

                object director = FindDirector();
                if (director == null) { LastResult = "error: AdventureDirector unavailable (is the overworld loaded?)"; return; }

                StringBuilder sb = new StringBuilder();

                // Three candidate sources, tried in order. The director fields are what the game
                // itself is currently pointing at; GetPlayerEncounter asks what is under the party.
                object encounter = PartyAccess.ReadMember(director, "_encounterEntity");
                string source = "_encounterEntity";
                if (encounter == null)
                {
                    encounter = PartyAccess.ReadMember(director, "_viewingEncounterEntity");
                    source = "_viewingEncounterEntity";
                }
                if (encounter == null)
                {
                    Type adventureHelper = AccessTools.TypeByName("AdventureHelper");
                    MethodInfo getPlayerEncounter = adventureHelper == null
                        ? null
                        : AccessTools.Method(adventureHelper, "GetPlayerEncounter");
                    if (getPlayerEncounter != null)
                    {
                        try
                        {
                            encounter = getPlayerEncounter.Invoke(null, new object[] { active, env });
                            source = "GetPlayerEncounter";
                        }
                        catch (TargetInvocationException ex)
                        {
                            Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                            sb.Append("GetPlayerEncounter threw: ").Append(root.Message).Append("\n");
                        }
                    }
                }

                if (encounter == null)
                {
                    LastResult = sb + "no encounter found on the party hex (spawn one first with "
                        + "crucible_debug_spawn encounters <id>)";
                    return;
                }

                sb.Append("encounterSource=").Append(source)
                  .Append(" encounterGuid=").Append(StringOfMember(encounter, "Guid"));

                Type directorType = director.GetType();
                MethodInfo perform = null;
                foreach (MethodInfo m in directorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "_performVenueAction", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 3) { perform = m; break; }
                }
                if (perform == null) { LastResult = sb + "\nerror: _performVenueAction(3 args) not found"; return; }

                string routeBefore = ReadRoute();
                try
                {
                    perform.Invoke(director, new object[] { active, encounter, null });
                }
                catch (TargetInvocationException ex)
                {
                    Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                    LastResult = sb + "\nerror: _performVenueAction threw: " + root.GetType().Name + ": " + root.Message;
                    return;
                }

                sb.Append("\nrouteBefore=").Append(routeBefore).Append(" routeAfter=").Append(ReadRoute());
                sb.Append("\nNOTE: the venue transition is asynchronous; re-read /state?schema=v2 after a few seconds.");
                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_engage_encounter: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_engage_encounter threw: " + ex.Message;
            }
        }

        private static string StringOfMember(object instance, string name)
        {
            object value = PartyAccess.ReadMember(instance, name);
            return value == null ? "(null)" : value.ToString();
        }

        // ============================================================== crucible_force_encounter

        /// <summary>
        /// crucible_force_encounter - spawns an ambush encounter at the party hex using the game
        /// own routine, then reports whether an encounter entity now exists.
        ///
        /// This is the LEGAL path, and it exists because the forced route is not one. Measured
        /// 2026-08-23: RouterMono.Route(COMBAT, payload, force:true) does flip the route enum to
        /// COMBAT, but CombatPhase.Initialize needs a Scene, Diorama, VenueCameraController and
        /// VenueState that only a real transition produces, so the fight never initializes - no
        /// CombatState, no combatants, no UI - and repeating it wedges the main thread. Three
        /// payload shapes (List of string, CombatEncounterArgs, List of Entity) all behaved
        /// identically, which is what identifies the scene context rather than the payload as the
        /// missing piece.
        ///
        /// Verified API surface (TypeProbe --signatures, 2026-08-23):
        ///   AdventureHelper.TryNextAmbushEncounter(Entity pActivePlayer, List pParty, String pVehicleID,
        ///       ValueTuple pPosition, List[,] pHexMap, Env pEnv, GameRandom pGameRandom) static
        ///   AdventureComponent.HexPosition : ValueTuple(Int32,Int32) and .MapID : String
        ///   Entity.TryGetAdventureComponent(GameRunData pGameRun, out AdventureComponent pComponent)
        ///   AdventureDirector._gameRandom : GameRandom and ._encounterEntity : Entity
        /// </summary>
        public static void CrucibleForceEncounter()
        {
            LastResult = null;
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) { LastResult = "error: Env unavailable"; return; }

                object gameRun = PartyAccess.ReadMember(env, "GameRun");
                if (gameRun == null) { LastResult = "error: no run loaded"; return; }

                List<object> party;
                string partyError;
                if (!PartyAccess.TryGetParty(out party, out partyError)) { LastResult = "error: " + partyError; return; }

                object active = party[0];

                object adventureComponent;
                string componentError;
                if (!TryGetAdventureComponent(active, gameRun, out adventureComponent, out componentError))
                {
                    LastResult = "error: " + componentError;
                    return;
                }

                object position = PartyAccess.ReadMember(adventureComponent, "HexPosition");
                object mapId = PartyAccess.ReadMember(adventureComponent, "MapID");

                object hexMaps = PartyAccess.ReadMember(env, "HexMaps");
                IDictionary maps = hexMaps as IDictionary;
                object hexMap = (maps != null && mapId != null && maps.Contains(mapId)) ? maps[mapId] : null;

                object director = FindDirector();
                object gameRandom = director == null ? null : PartyAccess.ReadMember(director, "_gameRandom");

                StringBuilder sb = new StringBuilder();
                sb.Append("mapId=").Append(mapId == null ? "(null)" : mapId.ToString())
                  .Append(" position=").Append(position == null ? "(null)" : position.ToString())
                  .Append(" hexMap=").Append(hexMap == null ? "(null)" : "present")
                  .Append(" gameRandom=").Append(gameRandom == null ? "(null)" : "present")
                  .Append(" partyCount=").Append(party.Count);

                if (hexMap == null || gameRandom == null)
                {
                    LastResult = sb + "\nerror: cannot call TryNextAmbushEncounter without a hex map and a GameRandom";
                    return;
                }

                Type adventureHelper = AccessTools.TypeByName("AdventureHelper");
                MethodInfo ambush = adventureHelper == null ? null : AccessTools.Method(adventureHelper, "TryNextAmbushEncounter");
                if (ambush == null) { LastResult = sb + "\nerror: AdventureHelper.TryNextAmbushEncounter not found"; return; }

                object encounterBefore = director == null ? null : PartyAccess.ReadMember(director, "_encounterEntity");

                object result;
                try
                {
                    result = ambush.Invoke(null, new object[]
                    {
                        active, BuildTypedParty(party, ambush), null, position, hexMap, env, gameRandom
                    });
                }
                catch (TargetInvocationException ex)
                {
                    Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                    LastResult = sb + "\nerror: TryNextAmbushEncounter threw: " + root.GetType().Name + ": " + root.Message;
                    return;
                }

                object encounterAfter = director == null ? null : PartyAccess.ReadMember(director, "_encounterEntity");

                sb.Append("\nresult=").Append(result == null ? "(null)" : result.ToString());
                sb.Append("\nencounterBefore=").Append(encounterBefore == null ? "(none)" : "present");
                sb.Append("\nencounterAfter=").Append(encounterAfter == null ? "(none)" : "present");
                sb.Append("\nroute=").Append(ReadRoute());
                LastResult = sb.ToString();
                if (_log != null) _log.LogInfo("crucible_force_encounter: " + LastResult);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_force_encounter threw: " + ex.Message;
            }
        }

        /// <summary>
        /// Rebuilds the party as the exact List type the target parameter declares. Reflection will
        /// not coerce a List of object into a List of Entity, and the resulting ArgumentException
        /// reads as if the method were wrong rather than the argument.
        /// </summary>
        private static object BuildTypedParty(List<object> party, MethodInfo target)
        {
            try
            {
                Type listType = target.GetParameters()[1].ParameterType;
                if (!listType.IsGenericType) return party;
                object typed = Activator.CreateInstance(listType);
                MethodInfo add = listType.GetMethod("Add");
                foreach (object entity in party) add.Invoke(typed, new object[] { entity });
                return typed;
            }
            catch (Exception) { return party; }
        }

        private static bool TryGetAdventureComponent(object entity, object gameRun, out object component, out string error)
        {
            component = null;
            error = null;
            try
            {
                MethodInfo tryGet = AccessTools.Method(entity.GetType(), "TryGetAdventureComponent");
                if (tryGet == null) { error = "Entity.TryGetAdventureComponent not found"; return false; }

                object[] args = new object[] { gameRun, null };
                object ok = tryGet.Invoke(entity, args);
                component = args[1];
                if (!(ok is bool) || !(bool)ok || component == null)
                {
                    error = "TryGetAdventureComponent returned false -- no adventure component on this map";
                    return false;
                }
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = "TryGetAdventureComponent threw: " + root.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
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

        private static bool TryBuildPayload(string kind, string enemyConfigName, int howMany, out object payload, out string error)
        {
            payload = null;
            error = null;

            if (kind == "names")
            {
                List<string> names = new List<string>();
                for (int i = 0; i < howMany; i++) names.Add(enemyConfigName);
                payload = names;
                return true;
            }

            if (kind == "entities")
            {
                List<object> built = new List<object>();
                for (int i = 0; i < howMany; i++)
                {
                    object entity;
                    if (!TryCreateEnemyEntity(enemyConfigName, i, out entity, out error)) return false;
                    built.Add(entity);
                }
                payload = built;
                return true;
            }

            if (kind == "args")
            {
                Type argsType = AccessTools.TypeByName("CombatEncounterArgs");
                if (argsType == null) { error = "CombatEncounterArgs type not found"; return false; }

                object args;
                Type mapGen = AccessTools.TypeByName("MapGenHelper");
                MethodInfo createDefault = mapGen == null ? null : AccessTools.Method(mapGen, "_createDefaultCombatArg");
                if (createDefault != null)
                {
                    try { args = createDefault.Invoke(null, new object[] { false, false, null }); }
                    catch (Exception) { args = Activator.CreateInstance(argsType); }
                }
                else
                {
                    args = Activator.CreateInstance(argsType);
                }
                if (args == null) { error = "could not construct CombatEncounterArgs"; return false; }

                FieldInfo namesField = AccessTools.Field(argsType, "EnemyNames");
                if (namesField == null) { error = "CombatEncounterArgs.EnemyNames not found"; return false; }

                List<string> names = new List<string>();
                for (int i = 0; i < howMany; i++) names.Add(enemyConfigName);
                namesField.SetValue(args, names);

                FieldInfo waveField = AccessTools.Field(argsType, "WaveAmount");
                if (waveField != null) waveField.SetValue(args, 1);

                payload = args;
                return true;
            }

            error = "unknown payloadKind '" + kind + "' (expected names, args or entities)";
            return false;
        }

        /// <summary>
        /// CharacterHelper.CreateCharacterEntity(String pCharacterConfigName, Boolean pIsNpc,
        /// GameRandom pGameRandom, String pUniqueID, Boolean pAddPlayerComponent,
        /// eSummonTypes pSummonType, Int32 pLevel) — verified signature. GameRandom and the summon
        /// type are passed as defaults, which is what a plain enemy wants.
        /// </summary>
        private static bool TryCreateEnemyEntity(string configName, int ordinal, out object entity, out string error)
        {
            entity = null;
            error = null;
            try
            {
                Type characterHelper = AccessTools.TypeByName("CharacterHelper");
                if (characterHelper == null) { error = "CharacterHelper not found"; return false; }

                MethodInfo create = null;
                foreach (MethodInfo m in characterHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "CreateCharacterEntity", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 7) { create = m; break; }
                }
                if (create == null) { error = "CreateCharacterEntity(7 args) not found"; return false; }

                ParameterInfo[] ps = create.GetParameters();
                object summonDefault = ps[5].ParameterType.IsEnum
                    ? Enum.ToObject(ps[5].ParameterType, 0)
                    : null;

                object[] callArgs = new object[]
                {
                    configName, true, null, configName + "_crucible_" + ordinal, false, summonDefault, 1
                };
                entity = create.Invoke(null, callArgs);
                if (entity == null) { error = "CreateCharacterEntity returned null for '" + configName + "'"; return false; }
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                error = "CreateCharacterEntity threw: " + root.GetType().Name + ": " + root.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static bool TryRoute(object payload, out string error)
        {
            error = null;
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                if (routerHelper == null) { error = "RouterHelper not found"; return false; }

                FieldInfo routerField = AccessTools.Field(routerHelper, "_router");
                object router = routerField == null ? null : routerField.GetValue(null);
                if (router == null) { error = "RouterHelper._router is null"; return false; }

                Type routesEnum = AccessTools.TypeByName("eRoutes");
                if (routesEnum == null) { error = "eRoutes not found"; return false; }
                object combat = Enum.Parse(routesEnum, "COMBAT", false);

                MethodInfo route = null;
                foreach (MethodInfo m in router.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(m.Name, "Route", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 5) { route = m; break; }
                }
                if (route == null) { error = "RouterMono.Route(5 args) not found"; return false; }

                // pForceRoute: true. A legal-transition check would refuse a jump straight into
                // COMBAT from the overworld, which is exactly the jump this command exists to make.
                route.Invoke(router, new object[] { combat, 0, payload, false, true });
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
    }
}
