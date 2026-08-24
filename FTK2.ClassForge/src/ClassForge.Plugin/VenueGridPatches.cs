using System;
using BepInEx.Configuration;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Gives ordinary combats the larger EXTENDED tile grid instead of the standard one.
    ///
    /// <para><b>Why this is safe.</b> Tile world-positions are computed, not baked into the venue
    /// art. <c>VenueViewHelper.CreateVenueTileGameObjects</c> lays every tile out from its logical
    /// coordinate — <c>new Vector3(1.8f * x, 0f, 1.8f * y)</c> — parents the block under the
    /// diorama's single <c>VenueGridRoot</c> anchor and centres it there. So a bigger grid extends
    /// outward from that anchor rather than needing extra per-tile anchors the art does not have.</para>
    ///
    /// <para>The game itself already does exactly this, live, mid-fight, on the currently loaded
    /// diorama: the <c>CHANGE_VENUE_GRID</c> boss-phase event rebuilds the tile set from a different
    /// map and re-uses the same <c>_diorama</c> and the same <c>GetVenueGridRoot()</c> before and
    /// after. No art is swapped. That is the proof this is a data decision, not an asset one.</para>
    ///
    /// <para><b>Why EXTENDED and not the Kraken grid.</b> <c>eVenueGrids.Extended</c> is a shipping,
    /// NON-boss value (6 rows per side against the standard 4). <c>BossKraken</c> is not safely
    /// reusable: <c>CombatViewHelper</c> hard-codes a travel-animation X-clamp for it
    /// (<c>if (GridType == BossKraken &amp;&amp; GroupIndex == 0) b = new Vector3(1f, b.y, b.z);</c>),
    /// a special case shaped around that one arena.</para>
    ///
    /// <para><b>Known limitation:</b> the camera is NOT adjusted. The game pairs its own grid change
    /// with a separate <c>CHANGE_CAMERA_RIG</c> event, so a larger arena may frame loosely on a
    /// diorama authored for the small one. Off by default for that reason.</para>
    /// </summary>
    public static class VenueGridPatches
    {
        internal static ConfigEntry<bool> Enabled;

        /// <summary>Binds the [Combat] ExtendedVenueGrid knob. Default OFF: this changes the shape of
        /// every fight, so it is opt-in rather than something a player gets by surprise.</summary>
        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Combat", "ExtendedVenueGrid", false,
                "Use the larger EXTENDED combat grid (6 rows per side) for ordinary fights instead of "
                + "the standard 4. More room for summons and bigger encounters. The camera is not "
                + "re-framed, so on some venues the arena may sit loosely in view.");
        }

        /// <summary>
        /// Postfix on <c>CombatPhase.Initialize</c>. The phase sets
        /// <c>_combatState.GridType = _diorama.VenueGrid</c> during setup; overwriting it afterwards is
        /// the smallest possible intervention, and it is the same field the game's own switch reads
        /// when it builds the tile entities.
        /// </summary>
        public static void Initialize_Postfix()
        {
            try
            {
                if (Enabled == null || !Enabled.Value) return;

                var combatState = RouterHelper.Env?.GameRun?.CombatState;
                if (combatState == null) return;

                // Leave a boss arena exactly as its designer authored it -- those pair a specific
                // grid with a specific camera rig and, for the Kraken, a hard-coded view special case.
                if (combatState.GridType != eVenueGrids.Standard) return;

                combatState.GridType = eVenueGrids.Extended;

                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] combat grid Standard -> Extended (6 rows per side).");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not enlarge the combat grid; the fight uses its normal size: "
                    + ex.Message);
            }
        }
    }
}
