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

            // Wide board, and every cell is a LETTER.
            //
            // A '.' cell becomes GroupIndex -1, and the renderer refuses to draw those:
            // CreateVenueTileGameObjects puts any tile with GroupIndex <= -1 on Unity's
            // "DoNotRender" layer, and SetVenueTileRenderState only escapes Hidden when
            // GroupIndex >= 0. The shipped rows are padded with dots -- "|..Aa.bB..|" is 4 letters
            // to 6 dots -- so most of a standard board is deliberately invisible and the two sides
            // are separated by an unrendered gap. Padding a WIDER map the same way is what made the
            // enlarged arena read as "the tiles do not exist" even though every tile was real,
            // occupied and movable-to.
            //
            // So these maps have no padding at all: back columns then front columns, meeting in the
            // middle, which is the continuous outlined field the boss arenas show.
            { "large", new[]
                {
                    "+--------+",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "+--------+",
                } },

            // The same width, eight rows deep.
            { "huge", new[]
                {
                    "+--------+",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "|AAaabbBB|",
                    "+--------+",
                } },
        };

        internal static ConfigEntry<string> CameraRig;
        internal static ConfigEntry<float> ZoomOut;

        internal static void Bind(ConfigFile config)
        {
            CameraRig = config.Bind("Combat", "VenueCameraRig", "",
                "Camera rig to switch to when a grid preset is active. Empty leaves the camera alone. "
                + "The game pairs its own grid change with a rig change, and without one a bigger "
                + "arena is framed for the small board -- zoomed in, with the outer tiles off screen. "
                + "Valid: OutdoorCameraRig, IndoorCameraRig, KrakenCameraRig, SpiderQueenCameraRig, "
                + "QueenCameraRig, HarazuelRoofRig, HarazuelFlyingRoofRig, OmusCameraRig, "
                + "OutdoorCondensedCameraRig. The boss rigs are the ones framing the big arenas.");

            ZoomOut = config.Bind("Combat", "VenueCameraZoomOut", 0f,
                "Degrees of extra camera field of view during combat, so a larger arena fits on "
                + "screen. 0 leaves the camera as the game sets it; 10-25 is a reasonable range for "
                + "the wider grid presets. This is applied on top of whatever zoom the game chose, "
                + "and re-applied whenever the game resets it.");

            Preset = config.Bind("Combat", "VenueGridPreset", "off",
                "Combat arena size. 'off' leaves every fight as the game ships it. 'kraken' uses the "
                + "shipped Kraken boss map verbatim (ally 8, enemy 32). 'extended' uses "
                + "the game's own deeper board. 'large' and 'huge' are custom maps that also widen "
                + "each side to two back and two front columns, which is what a TWO_BY_TWO creature "
                + "needs to stand anywhere at all. The camera is not re-framed for the bigger "
                + "arenas, so they sit loosely in view on some venues.");
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
