using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using IOG.dObjects;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Gives ordinary combats the larger EXTENDED tile grid, by replaying the game's own
    /// grid-resize routine.
    ///
    /// <para><b>Why a replay rather than a config nudge.</b> The obvious approach — write
    /// <c>Diorama.VenueGrid</c> before <c>CombatPhase.Initialize</c> reads it — was tried and did
    /// nothing: the log reported the switch while the board still had its four ally tiles. The game
    /// does not merely read that field once; it builds the tile entities, their GameObjects, the
    /// actor placement and the render pass as one sequence, and every part has to agree.</para>
    ///
    /// <para>So this ports <c>CombatPhase</c>'s <c>CHANGE_VENUE_GRID</c> boss-phase event, which is
    /// the game resizing a live arena on the currently loaded diorama. Its steps, in order, all of
    /// which matter:</para>
    /// <list type="number">
    /// <item>Build new tile entities from <c>ExtendedVenueMap1</c>.</item>
    /// <item>Measure the OLD grid depth, destroy its tile GameObjects, drop the old tile entities.</item>
    /// <item>Add the new tiles and rebuild <c>FromTile</c> against the SAME diorama anchor.</item>
    /// <item>Measure the new depth and shift every character by <c>(new - old) / 2</c> — without this
    /// the party keeps its old coordinates and ends up off-centre or off the board.</item>
    /// <item>Re-cache actor tiles, redraw the characters, activate the tiles, clear render state.</item>
    /// </list>
    ///
    /// <para><b>Not BossKraken:</b> <c>CombatViewHelper</c> hard-codes a travel-animation clamp for
    /// that grid, shaped around the one arena. <c>Extended</c> is a shipping non-boss value with no
    /// such special case.</para>
    ///
    /// <para><b>Known limitation:</b> the camera is not re-framed. The game pairs its own grid change
    /// with a separate CHANGE_CAMERA_RIG event. Off by default for that reason.</para>
    /// </summary>
    public static class VenueGridPatches
    {
        internal static ConfigEntry<string> Preset;

        /// <summary>
        /// The tile maps this mod can install, decoded from the game's own format.
        ///
        /// <para>A map is a rectangle of characters, one tile per cell, and every row must be the
        /// same length. <c>CharacterHelper.GetGroupIndex(c)</c> is <c>c - ('a' or 'A')</c>, so an
        /// A/a cell belongs to group 0 and a B/b cell to group 1; <c>VenueHelper.getSlotRow</c>
        /// makes UPPERCASE the BACK row and lowercase the FRONT row. Anything that is not a letter
        /// becomes a neutral tile with group -1. That is the whole format.</para>
        ///
        /// <para>Which means the shipped maps are not a constraint. The standard row,
        /// <c>|..Aa.bB..|</c>, gives each side exactly ONE back column and ONE front column — so a
        /// TWO_BY_TWO creature has no 2x2 block of same-group tiles to stand on anywhere on the
        /// board. Widening the letter runs fixes that, and is the reason these presets exist rather
        /// than just using Extended.</para>
        /// </summary>
        private static readonly Dictionary<string, string[]> Presets = new Dictionary<string, string[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            // The game's own deeper board: 6 rows, still one column per side per row.
            { "extended", null },

            // The Kraken boss arena, VERBATIM as the game ships it. Included because a known-good
            // shipped map is the honest baseline: if this draws and targets correctly then the
            // substitution mechanism is sound and any remaining problem is in a custom map, not in
            // the approach. Note its shape is boss-shaped -- ally 8, enemy 32 -- so it gives the
            // PLAYER no more room than the standard board.
            { "kraken", null },

            // Every cell is a LETTER, deliberately.
            //
            // A '.' cell becomes GroupIndex -1, and the renderer refuses to draw those:
            // CreateVenueTileGameObjects puts any tile with GroupIndex <= -1 on Unity's
            // "DoNotRender" layer, and SetVenueTileRenderState only escapes Hidden when
            // GroupIndex >= 0. The shipped rows are padded with dots -- "|..Aa.bB..|" is 4 letters
            // to 6 dots -- so most of a standard board is deliberately invisible and the two sides
            // are separated by an unrendered gap. Padding a wider map the same way is what made an
            // enlarged arena read as "the tiles do not exist" while every tile was real, occupied
            // and movable-to.
            //
            // Column order follows the game's own: ally BACK, ally FRONT, enemy FRONT, enemy BACK.
            // Uppercase is the back row, lowercase the front.

            // 6 rows x 2 columns a side -- 12 tiles each, same count as the shipped Extended
            // board but fully drawn rather than padded into invisibility.
            //
            // The single '.' between the sides is KEPT. Only the OUTER padding causes the
            // invisibility problem; this interior cell is the no-man's-land the shipped maps put
            // between the two front rows, and dropping it made the sides physically adjacent
            // (measured: ally cols [1,2] against enemy cols [3,4], touching).
            { "large", new[]
                {
                    "+-----+",
                    "|Aa.bB|",
                    "|Aa.bB|",
                    "|Aa.bB|",
                    "|Aa.bB|",
                    "|Aa.bB|",
                    "|Aa.bB|",
                    "+-----+",
                } },

            // 6 rows x 4 columns a side -- 24 each. Room for a summoner to field a team, and the
            // only shape here that gives a TWO_BY_TWO creature a 2x2 block of same-group tiles.
            { "huge", new[]
                {
                    "+---------+",
                    "|AAaa.bbBB|",
                    "|AAaa.bbBB|",
                    "|AAaa.bbBB|",
                    "|AAaa.bbBB|",
                    "|AAaa.bbBB|",
                    "|AAaa.bbBB|",
                    "+---------+",
                } },
        };

        internal static ConfigEntry<string> CameraRig;
        internal static ConfigEntry<float> ZoomOut;
        internal static ConfigEntry<float> TileBorderOpacity;
        internal static ConfigEntry<float> ClearFoliage;
        internal static ConfigEntry<string> RotateDioramas;
        internal static ConfigEntry<bool> RotateInDungeons;

        internal static void Bind(ConfigFile config)
        {
            CameraRig = CFConfig.Bind(config, "Combat", "VenueCameraRig", "",
                "Camera rig to switch to when a grid preset is active. Empty leaves the camera alone. "
                + "The game pairs its own grid change with a rig change, and without one a bigger "
                + "arena is framed for the small board -- zoomed in, with the outer tiles off screen. "
                + "Valid: OutdoorCameraRig, IndoorCameraRig, KrakenCameraRig, SpiderQueenCameraRig, "
                + "QueenCameraRig, HarazuelRoofRig, HarazuelFlyingRoofRig, OmusCameraRig, "
                + "OutdoorCondensedCameraRig. The boss rigs are the ones framing the big arenas.", ClassForge.Core.ParityClass.Presentation);

            TileBorderOpacity = CFConfig.Bind(config, "Combat", "VenueTileBorderOpacity", 0f,
                "Opacity of the resting tile BORDERS, 0 to leave the game's own value. A resting "
                + "tile draws only its border -- TileRender.Default disables the fill renderer and "
                + "enables the shadow one -- so this is the knob that makes the grid readable "
                + "without filling every square in. Raising the tiles' emissive instead brightens "
                + "the highlight FILLS, which is the wrong look entirely. Try 0.3-0.6.", ClassForge.Core.ParityClass.Presentation);

            RotateDioramas = CFConfig.Bind(config, "Combat", "RotateDioramas", DefaultRotation,
                "Comma-separated diorama names to choose from, one per fight. Which one a fight gets is "
                + "a pure function of REPLICATED encounter identity (map seed, overworld round, map/biome/"
                + "dungeon, the venue's own diorama name), never a process-local counter -- so every peer "
                + "loads the same battlefield, and therefore the same tile count, whether it booted with "
                + "the session or joined an hour in. Set this to empty "
                + "to give every fight its own venue back. "
                + "The three shipped defaults are the game's boss arenas: CASTLETHRONE (Powerstone "
                + "Castle), HARAZUEL_ROOF and OMUS_CASTLE_BOSS. All three are interiors with hard "
                + "floors, which is the point -- an outdoor venue stands tall grass on the board "
                + "between the camera and the tiles, and culling it was not enough. Each also "
                + "carries a camera framed for a deep arena. "
                + "The trade is that every fight happens in one of three places regardless of "
                + "where you are on the map. The overworld biome name still shows in the header.", ClassForge.Core.ParityClass.Gameplay);

            RotateInDungeons = CFConfig.Bind(config, "Combat", "RotateDioramasInDungeons", false,
                "Also swap the battlefield for fights INSIDE a dungeon. Off by default, and the "
                + "reason is not caution for its own sake. "
                + "A dungeon room is not a standalone arena the way an overworld venue is. The "
                + "rooms on a floor are chained together by corridors that VenueViewHelper "
                + "generates from each diorama's OWN attach points, and opening a door indexes "
                + "that diorama's door parts directly -- a room swapped for one the corridor "
                + "generator cannot connect breaks the floor rather than merely looking wrong. "
                + "It is also written to the SAVE. DungeonState.OngoingVenues is the whole "
                + "remaining floor, generated ahead of the player and serialised, so a swap here "
                + "lands in the save file for rooms not yet reached -- unlike the overworld, where "
                + "the venue is rebuilt per encounter and nothing persists. "
                + "Turn it on only if you are willing to lose a dungeon run. Dungeon fights are "
                + "already indoors on hard floors, so this buys far less than the overworld swap "
                + "does.", ClassForge.Core.ParityClass.Gameplay);

            ClearFoliage = CFConfig.Bind(config, "Combat", "ClearFoliageOverGrid", 0f,
                "Hide venue scenery standing ON the battle grid, so tall grass and props stop "
                + "occluding the tiles. The value is a margin in world units around the grid's "
                + "footprint; 0 disables it, 1-3 is a sensible range. Only scenery inside that "
                + "footprint is touched -- the surrounding venue is left alone, so the fight still "
                + "looks like it is happening somewhere.", ClassForge.Core.ParityClass.Presentation);

            ZoomOut = CFConfig.Bind(config, "Combat", "VenueCameraZoomOut", 0f,
                "Degrees of extra camera field of view during combat, so a larger arena fits on "
                + "screen. 0 leaves the camera as the game sets it; 10-25 is a reasonable range for "
                + "the wider grid presets. This is applied on top of whatever zoom the game chose, "
                + "and re-applied whenever the game resets it.", ClassForge.Core.ParityClass.Presentation);

            Preset = CFConfig.Bind(config, "Combat", "VenueGridPreset", "large",
                "Combat arena size. 'off' leaves every fight as the game ships it. 'kraken' uses the "
                + "shipped Kraken boss map verbatim (ally 8, enemy 32). 'extended' uses "
                + "the game's own deeper board. 'large' and 'huge' are custom maps that also widen "
                + "'large' is 6 rows x 2 columns a side (12 tiles each) and 'huge' is 6 x 4 (24 each). "
                + "Both are unpadded, so every tile draws -- the shipped maps pad with '.' cells "
                + "that are never rendered. The camera is not re-framed for the bigger arenas, so "
                + "they sit loosely in view on some venues.", ClassForge.Core.ParityClass.Gameplay);
        }




        /// <summary>
        /// The battlefields every fight is played on, shipped ON by default.
        ///
        /// <para>These are the game's own boss arenas. They are used rather than the venue the
        /// encounter would otherwise pick because all three are interiors: nothing grows on the
        /// board, so the grid is readable, and each carries a camera framed for a deep arena.
        /// Widening an outdoor venue instead leaves its grass standing between the camera and the
        /// tiles, and culling that scenery was tried and was not enough.</para>
        ///
        /// <para>Two of the three do NOT lay out the bigger grid on their own, whatever an asset
        /// inventory reports -- measured live, HARAZUEL_ROOF and OMUS_CASTLE_BOSS both came up
        /// 8 tiles a side. That is why <c>VenueGridPreset</c> also defaults to <c>large</c>: the
        /// map is substituted at build time so all three lay out identically.</para>
        /// </summary>
        private const string DefaultRotation = "CASTLETHRONE,HARAZUEL_ROOF,OMUS_CASTLE_BOSS";

        /// <summary>
        /// How many battlefields this process has chosen. DIAGNOSTIC ONLY — read by the log line and by
        /// nothing else.
        ///
        /// <para><b>It used to pick the battlefield</b> (<c>names[_dioramaTurn++ % names.Count]</c>) and
        /// that was a live, default-ON co-op desync. The counter is process-lifetime: it is not reset at
        /// run start, session start or join, and the overworld and dungeon paths shared it. Two peers
        /// whose process-local fight counts differed — anyone who played solo first, rejoined after a
        /// crash, or joined mid-session — loaded a DIFFERENT diorama, hence a different
        /// <c>VenueGrid</c>, hence a different tile count, hence a different
        /// <c>GetTargetableTiles().Count</c>, hence a different <c>ShuffleList</c> draw count off the
        /// SHARED stream on the very first AI turn (<c>AIHelper.cs:507-511</c>,
        /// <c>GameRandom.ShuffleList</c> takes exactly <c>list.Count</c> draws). Resetting it would not
        /// have been enough: a peer joining mid-session starts at 0 while the host is at 7.
        /// The choice now comes from <see cref="ReplicatedEncounterKey"/> instead — see there for why
        /// those inputs are peer-identical and this one never could be.</para>
        /// </summary>
        private static int _dioramaChoices;

        /// <summary>
        /// The battlefield for one fight: a pure function of REPLICATED encounter identity plus the
        /// venue's own diorama name (and, in a dungeon, the room's index in <c>OngoingVenues</c>) — so
        /// every peer picks the same one, first fight or hundredth, fresh boot or mid-session join.
        /// </summary>
        private static string ChooseBattlefield(List<string> names, string wasDioramaName, int roomIndex)
        {
            int i = ReplicatedEncounterKey.IndexOf(names.Count, "CF_BATTLEFIELD_ROTATION_V1", wasDioramaName, roomIndex);
            _dioramaChoices++;
            return names[i];
        }

        /// <summary>
        /// Prefix on <c>VenueDirector.Initialize</c> — picks the battlefield the combat is fought on.
        ///
        /// <para><b>Why here and nowhere earlier.</b> <c>VenueDirector.Initialize</c> is the single
        /// place that turns <c>VenueState.Venue.DioramaName</c> into an actual arena, and it does the
        /// whole job in one call: it loads the diorama's assets
        /// (<c>MemoryManagementHelper.GetVenueDioramaAssets</c>), instantiates it, builds the tile
        /// entities from <c>_diorama.VenueGrid</c>, places the party, and then frames the shot with
        /// <c>VenueViewHelper.FindCameraRig(pScene, DioramaName)</c>. Writing the name in a prefix
        /// means every one of those steps — assets, grid AND camera — agrees on the new venue.</para>
        ///
        /// <para>The previous attempt hooked <c>RouterMono.Route</c> and looked for
        /// <c>eRoutes.COMBAT</c>. It never fired for an ordinary fight: an overworld encounter routes
        /// to <c>eRoutes.VENUE</c>, and the VenueDirector then runs the combat phase inside it. The
        /// log line reported the swap because the venue had ALREADY been built by the time the COMBAT
        /// route came round, so the write landed on a VenueState nobody read again.</para>
        ///
        /// <para><b>Why swap the venue rather than widen the grid.</b> The shipped
        /// <c>ExtendedVenueMap1</c> is exactly six rows with a back and a front column a side — the
        /// 6x2 board — and the three dioramas that declare <c>eVenueGrids.Extended</c> ship a camera
        /// rig framed for it. Borrowing one of those gets the bigger board and its framing for free,
        /// with no map substitution at all. Widening an outdoor venue instead leaves its tall grass
        /// standing on the board between the camera and the tiles, which culling did not fix.</para>
        ///
        /// <para>Boss arenas are left alone: they are authored as grid, diorama and camera together,
        /// and the ocean/Kraken ones are shaped around a vehicle.</para>
        /// </summary>
        public static void VenueDirector_Initialize_Prefix(object __instance)
        {
            try
            {
                if (RotateDioramas == null) return;
                string raw = (RotateDioramas.Value ?? "").Trim();
                if (raw.Length == 0) return;

                var names = ParseNames(raw);
                if (names.Count == 0) return;

                // Initialize runs once per PHASE. The venue is only built on the pass where
                // _diorama is still null; on later passes it returns early and the name is never
                // read again, so rotating there would burn a turn of the cycle for nothing.
                // The venue is only BUILT on the pass where _diorama is empty; on later passes
                // Initialize returns early and the name is never read again.
                //
                // The comparison has to be Unity's, not C#'s. Deinitialize calls
                // Object.Destroy(_diorama.gameObject) but never assigns null, so on every venue
                // after the first the field holds a DESTROYED Diorama -- which `!= null` on
                // `object` reports as present, while Unity's own overload reports it as gone.
                // Reflection hands back `object`, so the plain check silently skipped every fight
                // but the first: rotation fired once, then quietly never again, with no log line
                // to say why.
                var priorDiorama = Field(__instance, "_diorama") as UnityEngine.Object;
                if (priorDiorama != null) return;

                var venueState = RouterHelper.Env?.GameRun?.VenueState;
                var venue = venueState == null ? null : venueState.Venue;
                if (venue == null || venue.PhasesData == null)
                {
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] battlefield: no venue to choose for (venue="
                        + (venue == null ? "null" : "phasesData null") + ").");
                    return;
                }

                // Only fights. Rest, treasure, trap, fortune and plain encounters keep their venue --
                // teleporting the party into a throne room to open a chest would be nonsense.
                bool isCombat = false;
                var phaseNames = new List<string>();
                foreach (var phase in venue.PhasesData)
                {
                    phaseNames.Add(phase.Type.ToString());
                    if (phase.Type == ePhases.COMBAT || phase.Type == ePhases.BOSS) isCombat = true;
                }

                string was = venue.DioramaName ?? "";

                // Report every outcome, including the ones that change nothing. Returning quietly
                // on a skip branch is indistinguishable from the patch never running, and that
                // exact ambiguity already cost this feature one wrong diagnosis.
                if (!isCombat || SkipVenue(was, names))
                {
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] battlefield: keeping " + was + " (phases="
                        + string.Join("+", phaseNames.ToArray())
                        + (isCombat ? ", venue is exempt" : ", not a fight") + ").");
                    return;
                }

                string chosen = ChooseBattlefield(names, was, -1);
                venue.DioramaName = chosen;

                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] battlefield " + was + " -> " + chosen
                    + " (1 of " + names.Count + ", chosen from replicated encounter identity; this "
                    + "process has now chosen " + _dioramaChoices + ").");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not choose a battlefield; the venue keeps its own: " + ex.Message);
            }
        }

        /// <summary>
        /// Prefix on <c>DungeonDirector._loadNextDioramas</c> — the dungeon counterpart of
        /// <see cref="VenueDirector_Initialize_Prefix"/>. OFF unless
        /// <c>RotateDioramasInDungeons</c> is set, and that default is deliberate.
        ///
        /// <para>This is the right HOOK: <c>_loadNextDioramas</c> is the single call that reads
        /// <c>DungeonState.OngoingVenues</c>, loads their assets through
        /// <c>GetVenueDioramaAssets</c>, and instantiates them through <c>CreateVenueDioramas</c>,
        /// which is where <c>Diorama.VenueGrid</c> is taken from the named record. The camera rig
        /// is looked up from <c>_venueState.Venue.DioramaName</c> after this returns. So a write
        /// here reaches assets, grid and camera, exactly as on the overworld.</para>
        ///
        /// <para><b>Two things make it riskier than the overworld swap, and both are why it ships
        /// off.</b></para>
        /// <list type="number">
        /// <item><b>It is written to the save.</b> <c>OngoingVenues</c> is a plain field on
        /// <c>DungeonState</c> holding the WHOLE remaining floor, generated ahead of the player and
        /// serialised. The overworld's <c>VenueState.Venue</c> is one object rebuilt per encounter
        /// and persists nothing; this list persists, so a swap lands in the save for rooms the
        /// player has not reached.</item>
        /// <item><b>Rooms are chained, not standalone.</b> The floor's corridors are generated from
        /// each diorama's own attach points, and opening a door indexes that diorama's door parts
        /// by junction. A room swapped for one the generator cannot connect does not merely look
        /// wrong — it breaks the floor.</item>
        /// </list>
        ///
        /// <para>The swap is idempotent by way of <see cref="SkipVenue"/>'s "already one of ours"
        /// test, which matters here in a way it does not on the overworld: reloading mid-floor
        /// re-enters this method over rooms that were already rewritten, and without that test they
        /// would be re-rolled every time.</para>
        /// </summary>
        public static void DungeonDirector_LoadNextDioramas_Prefix()
        {
            try
            {
                if (RotateInDungeons == null || !RotateInDungeons.Value) return;
                if (RotateDioramas == null) return;

                var names = ParseNames(RotateDioramas.Value);
                if (names.Count == 0) return;

                var dungeon = RouterHelper.Env?.GameRun?.DungeonState;
                if (dungeon == null || dungeon.OngoingVenues == null) return;

                // roomIndex is the room's position in OngoingVenues -- the whole remaining floor,
                // generated ahead of the player and SERIALISED into DungeonState, so the list and the
                // index into it are replicated state. It is what keeps two rooms on one floor from
                // getting the same battlefield now that the choice is identity-derived rather than a
                // counter.
                int roomIndex = -1;
                foreach (var venueData in dungeon.OngoingVenues)
                {
                    roomIndex++;
                    if (venueData == null || venueData.PhasesData == null) continue;

                    // Fights only. A BLACKOUT room -- the end-of-floor stairs sentinel -- carries
                    // only an EXIT phase and falls out here without needing its own special case.
                    bool isCombat = false;
                    foreach (var phase in venueData.PhasesData)
                        if (phase.Type == ePhases.COMBAT || phase.Type == ePhases.BOSS)
                        { isCombat = true; break; }

                    string was = venueData.DioramaName ?? "";
                    if (!isCombat || SkipVenue(was, names)) continue;

                    string chosen = ChooseBattlefield(names, was, roomIndex);
                    venueData.DioramaName = chosen;

                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] dungeon battlefield " + was + " -> " + chosen + ".");
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not choose a dungeon battlefield; the rooms keep their "
                    + "own: " + ex.Message);
            }
        }

        /// <summary>Splits the configured rotation into names, dropping blanks.</summary>
        private static List<string> ParseNames(string raw)
        {
            var names = new List<string>();
            foreach (var part in (raw ?? "").Split(','))
            {
                string n = part.Trim();
                if (n.Length > 0) names.Add(n);
            }
            return names;
        }

        /// <summary>Venues that must keep their own diorama.</summary>
        private static bool SkipVenue(string name, List<string> rotation)
        {
            // Already one of ours -- nothing to do, and rotating would double-count the turn.
            foreach (var n in rotation)
                if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;

            // Ocean fights are staged on the party's boat: the arena, the grid and the camera are
            // all built around that vehicle, and KrakenMap1 additionally has a hard-coded
            // travel-animation clamp in CombatViewHelper.
            string upper = name.ToUpperInvariant();
            return upper.Contains("KRAKEN") || upper.StartsWith("OCEAN");
        }

        /// <summary>
        /// Hides venue scenery that stands inside the battle grid's footprint.
        ///
        /// <para>Outdoor dioramas scatter tall grass and props across the ground, and a good deal of
        /// it lands on the play area, where it sits between the camera and the tiles. On night grass
        /// that is enough to make a perfectly-drawn grid unreadable.</para>
        ///
        /// <para>Only the footprint is cleared, not the venue: the bounds come from the tile
        /// GameObjects themselves, so anything outside the board keeps its scenery and the fight
        /// still looks like it is happening somewhere. Characters are safe because they are parented
        /// to the combat canvas, not to the diorama, and the tiles are safe because they live under
        /// their own <c>Venue_Grid</c> object rather than in the diorama hierarchy.</para>
        ///
        /// <para>Renderers are disabled rather than the GameObjects deactivated, which leaves
        /// colliders, spawn anchors and any script state on those objects untouched.</para>
        /// </summary>
        private static string ClearFoliageOverGrid(Diorama diorama, Dictionary<Entity, GameObject> tiles)
        {
            try
            {
                if (ClearFoliage == null) return null;
                float margin = ClearFoliage.Value;
                if (margin <= 0f) return null;

                if (diorama == null || tiles == null || tiles.Count == 0) return ",foliage:noDiorama";

                // Footprint of the board in world space, taken from the tiles that were just built.
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue, groundY = 0f;
                foreach (var go in tiles.Values)
                {
                    if (go == null) continue;
                    Vector3 p = go.transform.position;
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                    groundY = p.y;
                }
                if (minX > maxX) return ",foliage:noTiles";
                minX -= margin; maxX += margin; minZ -= margin; maxZ += margin;

                int hidden = 0;
                foreach (var r in diorama.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    Vector3 p = r.transform.position;
                    if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;

                    // Leave the ground itself: a floor is wide, so anything much larger than a
                    // couple of tiles is terrain rather than a prop standing on it.
                    Vector3 size = r.bounds.size;
                    if (size.x > 12f || size.z > 12f) continue;

                    // Leave anything sunk into the floor -- that is surface detail, not an occluder.
                    if (r.bounds.max.y < groundY + 0.15f) continue;

                    r.enabled = false;
                    hidden++;
                }

                return hidden == 0 ? ",foliage:none" : (",foliage:hid" + hidden);
            }
            catch (Exception ex)
            {
                return ",foliage:" + ex.GetType().Name;
            }
        }

        /// <summary>
        /// Postfix on <c>VenueTileMono.SetState</c> — raises the opacity of a RESTING tile's border.
        ///
        /// <para>A tile at rest draws only its border, not a fill. <c>SetState</c>'s
        /// <c>TileRender.Default</c> branch does exactly this:</para>
        /// <code>
        /// _overlayRenderer.enabled = false;        // the fill is OFF
        /// _overlayShadowRenderer.enabled = true;   // only the border renderer draws
        /// </code>
        /// <para>Which is why brightening the tiles the obvious way does not work.
        /// <c>_setOpacity</c> writes <c>Emissive_Intensity</c> onto <c>_overlayRenderer</c> only —
        /// the renderer that is DISABLED at rest — so raising it lights up the highlight, target and
        /// active-character FILLS and leaves the resting grid exactly as faint as before. The grid
        /// then reads as solid plates under whatever is highlighted rather than as an outlined
        /// board.</para>
        ///
        /// <para>The border's strength is <c>_Opacity_Boost</c> on the shadow renderer, which
        /// <c>_fade</c> tweens toward the value held on the Default tile's shadow MATERIAL. Setting
        /// it on the material is therefore the durable place: a property block would be overwritten
        /// by the tween a frame later.</para>
        ///
        /// <para><b>This mutates a shared material asset</b>, so it affects every tile using it for
        /// the rest of the session. That is the intent — one grid look — but it is why this is
        /// opt-in and defaults to off.</para>
        /// </summary>
        private static bool _loggedBorder;

        /// <summary>Postfix on <c>VenueViewHelper.CreateVenueTileGameObjects</c> — the moment the
        /// board's footprint is first known, which is when scenery standing on it can be culled.</summary>
        public static void CreateVenueTileGameObjects_Postfix(
            Dictionary<Entity, GameObject> __result, Diorama pDiorama)
        {
            try
            {
                // The diorama arrives as an ARGUMENT. An earlier version dug for it through
                // RouterHelper._router -> _combatPhase -> _diorama and got null every time
                // ("grid foliage: noDiorama"), because this runs while the venue is still being
                // built and the phase does not hold it yet. Reflection was never needed.
                string note = ClearFoliageOverGrid(pDiorama, __result);
                if (note != null)
                    ClassForgePlugin.Log.LogInfo("[ClassForge] grid foliage" + note.Replace(",foliage:", ": "));
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not clear foliage over the grid: " + ex.Message);
            }
        }

        /// <summary>The live CombatPhase, reached through the router.</summary>
        private static object CombatPhaseInstance()
        {
            try
            {
                var router = AccessTools.Field(AccessTools.TypeByName("RouterHelper"), "_router")?.GetValue(null);
                return router == null ? null
                    : AccessTools.Field(router.GetType(), "_combatPhase")?.GetValue(router);
            }
            catch (Exception) { return null; }
        }

        public static void SetState_Postfix(TileRender pTileRenderType)
        {
            try
            {
                if (TileBorderOpacity == null) return;
                float wanted = TileBorderOpacity.Value;
                if (wanted <= 0f) return;

                // Resting states only. The highlight/target/active states are MEANT to be filled --
                // that fill is how a player reads what is selected -- so they are left alone.
                if (pTileRenderType != TileRender.Default
                    && pTileRenderType != TileRender.DefaultInverted
                    && pTileRenderType != TileRender.Selectable
                    && pTileRenderType != TileRender.SelectableInverted) return;

                var record = dObjectHelper.Index.dBattleGridTile.GetAllRecords()
                    .FirstOrDefault(t => t.TileType == pTileRenderType);
                var shadowMat = record == null ? null : record.ShadowMaterialAsset;

                // Report ONCE what was actually found, whatever the outcome. An earlier version
                // returned quietly on every one of these branches, which is indistinguishable from
                // "the patch never ran" -- the exact failure shape that hid a per-tick reflection
                // miss until it had written 45,000 log lines.
                if (!_loggedBorder)
                {
                    _loggedBorder = true;
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] tile border: state=" + pTileRenderType
                        + " record=" + (record == null ? "MISSING" : "ok")
                        + " shadowMaterial=" + (shadowMat == null ? "MISSING" : shadowMat.name)
                        + " hasOpacityBoost=" + (shadowMat != null && shadowMat.HasProperty("_Opacity_Boost"))
                        + " current=" + (shadowMat != null && shadowMat.HasProperty("_Opacity_Boost")
                                            ? shadowMat.GetFloat("_Opacity_Boost").ToString() : "n/a")
                        + " wanted=" + wanted);
                }

                if (shadowMat == null || !shadowMat.HasProperty("_Opacity_Boost")) return;
                shadowMat.SetFloat("_Opacity_Boost", wanted);
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not adjust the tile border opacity: " + ex.Message);
            }
        }

        /// <summary>
        /// Prefix on <c>VenueHelper.CreateVenueTileEntities(string[] pMap)</c> — swaps the map the
        /// game is about to build, so the ENGINE constructs the larger grid natively.
        ///
        /// <para><b>Why this rather than resizing afterwards.</b> The first approach replayed the
        /// game's own CHANGE_VENUE_GRID routine from a postfix on <c>CombatPhase.Initialize</c>:
        /// destroy the tile GameObjects, build new ones, re-place the characters. It half-worked —
        /// the tiles were real and characters could be moved onto them — but the board did not draw
        /// and enemy tiles could not be targeted, because <c>Initialize</c> had already wired the
        /// ORIGINAL tiles into the rest of combat. Anything still holding one of those references was
        /// left pointing at a destroyed object, which surfaced as:</para>
        /// <code>
        /// NullReferenceException
        ///   at CombatViewHelper.JoinAbilityHitEffectNode (… pTargetTileEntity, pGameObjectMaps …)
        ///   at CombatViewHelper._damageAppliedToCharacter → CreateVisualSequence
        ///   at CombatPhase._performAbility → _engageActiveEntity
        /// </code>
        /// <para>Substituting the argument instead means there is never a second set of tiles and
        /// never a stale reference: every consumer — rendering, targeting, placement, the visual
        /// sequence — is built once, by the game, against the map we handed it. The game's own
        /// CHANGE_VENUE_GRID gets away with the destructive path only because it runs mid-fight,
        /// after everything is initialised and where it also re-centres and re-caches.</para>
        ///
        /// <para>The map format is the game's: <c>CharacterHelper.GetGroupIndex(c)</c> is
        /// <c>c - ('a' or 'A')</c>, so A/a is group 0 and B/b group 1; uppercase is the BACK row and
        /// lowercase the FRONT row; any non-letter is a neutral tile with group -1. Rows must all be
        /// the same length.</para>
        /// </summary>
        public static void CreateVenueTileEntities_Prefix(ref string[] pMap)
        {
            try
            {
                if (Preset == null || pMap == null) return;
                string preset = (Preset.Value ?? "off").Trim();
                if (preset.Length == 0 || preset.Equals("off", StringComparison.OrdinalIgnoreCase)) return;

                // Only ever widen the ordinary board. A boss arena is authored as a whole -- grid,
                // diorama and camera rig together -- and KrakenMap1 in particular has a hard-coded
                // travel-animation clamp in CombatViewHelper keyed to its grid type.
                if (!SameMap(pMap, VenueHelper.VenueMap1)
                    && !SameMap(pMap, VenueHelper.ExtendedVenueMap1)) return;

                string[] wanted;
                if (!Presets.TryGetValue(preset, out wanted))
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] unknown VenueGridPreset '" + preset + "'; the fight keeps its "
                        + "normal size.");
                    return;
                }

                if (wanted == null)
                    wanted = preset.Equals("kraken", StringComparison.OrdinalIgnoreCase)
                        ? VenueHelper.KrakenMap1
                        : VenueHelper.ExtendedVenueMap1;
                if (SameMap(pMap, wanted)) return;

                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] combat grid preset '" + preset + "': "
                    + pMap.Length + "x" + pMap[0].Length + " -> "
                    + wanted.Length + "x" + wanted[0].Length + " (built by the game itself).");

                pMap = wanted;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not substitute the combat grid; the fight keeps its normal "
                    + "size: " + ex.Message);
            }
        }

        private static bool SameMap(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>How many tiles share the largest x — the game's own measure of grid depth.</summary>
        private static int DepthAtMaxX(IEnumerable<Entity> tiles)
        {
            int maxX = 0, count = 0;
            foreach (var e in tiles)
            {
                int x = e.Get<VenueComponent>().TilePosition.x;
                if (x > maxX) maxX = x;
            }
            foreach (var e in tiles)
                if (e.Get<VenueComponent>().TilePosition.x == maxX) count++;
            return count;
        }

        /// <summary>Clears and refills the phase's <c>_cachedActorTiles</c>. Stale entries there point
        /// at tile objects that were just destroyed.</summary>
        private static void RecacheActorTiles(object phase, List<Entity> characters)
        {
            try
            {
                (Field(phase, "_cachedActorTiles") as IDictionary)?.Clear();
                var refresh = AccessTools.Method(phase.GetType(), "_getUpdateCachedActorTile");
                if (refresh == null) return;
                foreach (var e in characters) refresh.Invoke(phase, new object[] { e });
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the actor-tile cache could not be refreshed after resizing the grid: "
                    + ex.Message);
            }
        }

        /// <summary>Reads a field from anywhere in the type's hierarchy, private base fields included —
        /// <c>Type.GetField</c> alone does not surface those.</summary>
        private static object Field(object instance, string name)
        {
            for (var t = instance.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(instance);
            }
            return null;
        }
    }
}
