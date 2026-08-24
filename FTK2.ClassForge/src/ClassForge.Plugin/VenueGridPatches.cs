using System;
using System.Collections;
using System.Collections.Generic;
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
        internal static ConfigEntry<bool> Enabled;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Combat", "ExtendedVenueGrid", false,
                "Use the larger EXTENDED combat grid (6 rows per side) for ordinary fights instead of "
                + "the standard 4. More room for summons and bigger encounters. The camera is not "
                + "re-framed, so on some venues the arena may sit loosely in view.");
        }

        /// <summary>Postfix on <c>CombatPhase.Initialize</c> — the grid is rebuilt after the phase has
        /// finished its own setup, exactly as the boss event does mid-fight.</summary>
        public static void Initialize_Postfix(object __instance)
        {
            try
            {
                if (Enabled == null || !Enabled.Value || __instance == null) return;

                var combatState = RouterHelper.Env?.GameRun?.CombatState;
                if (combatState == null) return;

                // Leave a boss arena as its designer authored it.
                if (combatState.GridType != eVenueGrids.Standard) return;

                Resize(__instance, combatState);
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not enlarge the combat grid; the fight keeps its normal size: "
                    + ex.Message);
            }
        }

        private static void Resize(object phase, CombatState combatState)
        {
            var maps = RouterHelper.Env?.VenueGameObjectMaps;
            if (maps?.FromTile == null) return;

            var diorama = Field(phase, "_diorama") as Diorama;
            var canvas3D = Field(phase, "_canvas3D") as GameObject;
            if (diorama == null || canvas3D == null) return;

            var newTiles = VenueHelper.CreateVenueTileEntities(VenueHelper.ExtendedVenueMap1);
            if (newTiles == null || newTiles.Count == 0) return;

            // Depth of the old grid, measured the way the game measures it: how many tiles share the
            // largest x. This is what the recentre offset is computed from.
            int oldDepth = DepthAtMaxX(maps.FromTile.Keys);

            foreach (var go in maps.FromTile.Values)
                if (go != null) UnityEngine.Object.Destroy(go.gameObject);
            maps.FromTile = null;
            combatState.Entities.RemoveAll(e => e.Has<VenueTileComponent>());

            combatState.Entities.AddRange(newTiles);
            var root = diorama.GetVenueGridRoot().transform;
            maps.FromTile = VenueViewHelper.CreateVenueTileGameObjects(
                newTiles, diorama, root.position, root.rotation, diorama.IsOutdoor);

            int newDepth = DepthAtMaxX(maps.FromTile.Keys);
            int shift = (newDepth - oldDepth) / 2;

            // Recentre everyone already on the board. Skip this and the party keeps the coordinates
            // the SMALL grid gave it, which on a deeper grid is off-centre or off the board.
            var characters = combatState.Entities.FindAll(
                e => e.Has<CharacterComponent>() && e.Has<VenueComponent>());
            if (shift != 0)
                foreach (var e in characters)
                {
                    var venue = e.Get<VenueComponent>();
                    if (venue == null) continue;
                    venue.TilePosition = (venue.TilePosition.x, venue.TilePosition.y + shift);
                    var moved = new List<(int, int)>();
                    foreach (var t in venue.OccupiedTiles) moved.Add((t.Item1, t.Item2 + shift));
                    venue.OccupiedTiles = moved;
                }

            RecacheActorTiles(phase, characters);

            VenueViewHelper.LoadCharacterEntitiesToVenueGrid(
                canvas3D.transform, characters, maps, newTiles, diorama,
                Field(phase, "_gameRandom") as GameRandom);

            foreach (var go in maps.FromTile.Values)
                if (go != null) go.gameObject.SetActive(true);

            combatState.GridType = eVenueGrids.Extended;

            // Give every NEW tile the render pass Initialize already ran on the OLD ones.
            //
            // This is the step whose absence made the enlarged grid invisible. Initialize walks
            // every tile calling RenderVenueTile(..., TileRender.Hidden) and then reveals them
            // through the placement call. Tiles created afterwards -- which is exactly what this
            // does -- never receive that pass, so they exist, are active, and are neither drawn nor
            // interactive. The data said 88 tiles while the screen showed the small board.
            foreach (var pair in maps.FromTile)
                if (pair.Value != null)
                    VenueViewHelper.RenderVenueTile(pair.Value, null, TileRender.Hidden);

            AccessTools.Method(phase.GetType(), "_clearTileRenderState")?.Invoke(phase, null);

            ClassForgePlugin.Log.LogInfo(
                "[ClassForge] combat grid enlarged to Extended: " + newTiles.Count
                + " tiles, depth " + oldDepth + " -> " + newDepth + ", characters shifted " + shift + ".");
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
