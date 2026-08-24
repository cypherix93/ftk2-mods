using System;
using System.Collections.Generic;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Stops a summoned creature surviving the combat that created it.
    ///
    /// <para><b>The defect.</b> <c>CombatPhase._endCombatAsync</c> banishes summons in a
    /// <c>cleanUpSummon</c> pass that is wrapped in <c>if (!pIsImmediate)</c>. Every immediate exit --
    /// <c>_endCombatAsync(pIsImmediate: true)</c>, which is how a flee, a scripted end and the debug
    /// end-phase all leave -- skips that pass entirely, so the creature keeps its
    /// <c>CombatComponent</c> and <c>VenueComponent</c> and is still standing on a tile when the NEXT
    /// fight starts.</para>
    ///
    /// <para><b>How that presents.</b> Not as a stray creature. <c>_clearTileRenderState</c> resolves,
    /// for each tile, the living occupant that has all three of CharacterComponent, CombatComponent and
    /// VenueComponent, and then indexes <c>_gameObjectMaps.FromCharacter[occupant]</c> with NO
    /// membership check. The leaked creature satisfies the predicate but has no entry in the freshly
    /// rebuilt map, so the indexer throws:</para>
    /// <code>
    /// KeyNotFoundException: '(WOLF_CHAOSHOUND_01) - (3, 1) e85cf401...' was not present in the dictionary
    ///   at CombatPhase._clearTileRenderState ()
    ///   at CombatPhase.Initialize (...)
    /// </code>
    /// <para>That aborts the remainder of <c>Initialize</c>, which is why the symptom is the whole combat
    /// UI missing -- no tile grid, no action menu, nothing hoverable or targetable -- rather than
    /// anything that points at summoning. The overworld is untouched, since it never calls this.</para>
    ///
    /// <para><b>The fix</b> is the game's own cleanup, applied where it cannot be skipped.
    /// <see cref="Deinitialize_Prefix"/> runs the purge on EVERY exit from combat, immediate or not,
    /// and does so as a prefix because <c>Deinitialize</c>'s own body calls
    /// <c>_clearTileRenderState</c> at line 1244 and would otherwise throw before we could act.
    /// <see cref="Initialize_Prefix"/> repeats it on entry, which costs one list walk and repairs a
    /// save that was already written with a leaked summon in it -- without that, an affected save stays
    /// unplayable no matter how the exit path is fixed.</para>
    ///
    /// <para><b>Why the SUMMON actor property is the right discriminator:</b> it is what
    /// <c>_endCombatAsync</c> itself selects on, and <c>CombatHelper.TryCreateSummon</c> deliberately
    /// does NOT set it when <c>CharacterType == COMPANION</c>. Permanent companions and AS_FOLLOWER
    /// recruits are therefore invisible to this pass and are never banished by it.</para>
    ///
    /// <para>Every step is individually guarded. A creature that cannot be cleaned is left alone and
    /// logged; this must never be able to take down entering or leaving a fight.</para>
    /// </summary>
    public static class SummonLeakPatches
    {
        /// <summary>
        /// Removes, from the combat roster, any character <c>_clearTileRenderState</c> is about to look
        /// up and fail to find.
        ///
        /// <para>This is the fix that does not depend on knowing how the creature got there. The
        /// defective line is an unchecked dictionary indexer:</para>
        /// <code>
        /// ActorGameObjectBase pActorGameObject =
        ///     ((entity2 != null) ? base._gameObjectMaps.FromCharacter[entity2] : null);
        /// </code>
        /// <para><c>entity2</c> is the living occupant of a tile, selected on
        /// CharacterComponent + CombatComponent + VenueComponent. Nothing checks that the actor map
        /// actually has an entry for it, so ANY combatant without a model takes down the whole method
        /// -- and with it the rest of <c>Initialize</c>, which is why the visible symptom is a missing
        /// combat UI rather than a missing creature.</para>
        ///
        /// <para>Running as a prefix, this sweeps exactly those orphans out of the roster first, using
        /// the same component-stripping the game's own <c>_removeEntityFromCombat</c> does. The
        /// original method then iterates a roster it can fully resolve. It fires at every call site,
        /// covers creatures from any source -- a leaked summon, a save written mid-fight, a spawn whose
        /// actor build failed -- and needs no cooperation from whatever created them.</para>
        ///
        /// <para>Deliberately NOT a finalizer: swallowing the exception would leave the tiles undrawn,
        /// which is the same broken screen by a quieter route.</para>
        /// </summary>
        public static void ClearTileRenderState_Prefix()
        {
            try
            {
                var combatState = RouterHelper.Env?.GameRun?.CombatState;
                var entities = combatState?.Entities;
                if (entities == null || entities.Count == 0) return;

                // The actor map, straight off the env. VenueDirectorBase exposes it as
                //     protected VenueGameObjectMaps _gameObjectMaps => _env.VenueGameObjectMaps;
                // -- a PROPERTY over the env, not a field on the phase. Reflection by field name finds
                // nothing there and hands back null, which is how an earlier version of this sweep
                // installed cleanly and then did nothing.
                var maps = RouterHelper.Env?.VenueGameObjectMaps;
                if (maps?.FromCharacter == null)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] could not reach the combat actor map, so combatants with no 3D " +
                        "model cannot be swept; _clearTileRenderState may throw and leave the combat " +
                        "UI undrawn.");
                    return;
                }

                List<Entity> orphans = null;
                foreach (var e in entities)
                {
                    if (e == null) continue;
                    if (!e.Has<CharacterComponent>() || !e.Has<CombatComponent>() || !e.Has<VenueComponent>())
                        continue;
                    if (CharacterHelper.IsDead(e)) continue;
                    if (maps.FromCharacter.ContainsKey(e)) continue;

                    (orphans ?? (orphans = new List<Entity>())).Add(e);
                }
                if (orphans == null) return;

                foreach (var e in orphans)
                {
                    try
                    {
                        if (e.Has<CombatComponent>()) e.Remove<CombatComponent>();
                        if (e.Has<VenueComponent>()) e.Remove<VenueComponent>();
                        entities.Remove(e);
                        combatState.RoundEntities?.Remove(e);
                        RouterHelper.Env?.GameRun?.Entities?.Remove(e);
                    }
                    catch (Exception ex)
                    {
                        ClassForgePlugin.Log.LogWarning(
                            "[ClassForge] a modelless combatant could not be removed from the roster; " +
                            "the combat UI may fail to draw: " + ex.Message);
                    }
                }

                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] removed " + orphans.Count + " combatant(s) with no 3D model from the " +
                    "roster; without this _clearTileRenderState throws and the combat UI does not draw.");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the modelless-combatant sweep failed; combat continues unchanged: "
                    + ex.Message);
            }
        }

        /// <summary>Purge on the way in, so an already-poisoned save loads into a working fight.</summary>
        public static void Initialize_Prefix()
        {
            Purge("entering combat");
        }

        /// <summary>
        /// Purge on the way out. Prefix, not postfix: <c>Deinitialize</c> calls
        /// <c>_clearTileRenderState</c> itself, so a postfix would run after the throw it is meant to
        /// prevent.
        /// </summary>
        public static void Deinitialize_Prefix()
        {
            Purge("leaving combat");
        }

        /// <summary>
        /// Strips combat residue from every SUMMON-tagged creature, mirroring
        /// <c>CombatPhase._removeEntityFromCombat(e, pAddToWaves: false)</c>.
        ///
        /// <para>Removing EITHER component is enough to hide the creature from
        /// <c>_clearTileRenderState</c>'s predicate; both go, plus the roster entries, because a
        /// half-cleaned entity is a worse failure to diagnose than an uncleaned one. It is also dropped
        /// from <c>GameRunData.Entities</c> -- that list is persistent and survives the fight, and is
        /// how a summon reaches a later combat in the first place.</para>
        /// </summary>
        private static void Purge(string when)
        {
            try
            {
                var gameRun = RouterHelper.Env?.GameRun;
                if (gameRun == null) return;

                var doomed = new List<Entity>();
                Collect(gameRun.Entities, doomed);
                Collect(gameRun.CombatState?.Entities, doomed);
                if (doomed.Count == 0) return;

                foreach (var e in doomed)
                {
                    try
                    {
                        if (e.Has<CombatComponent>()) e.Remove<CombatComponent>();
                        if (e.Has<VenueComponent>()) e.Remove<VenueComponent>();

                        gameRun.Entities?.Remove(e);
                        gameRun.CombatState?.Entities?.Remove(e);
                        gameRun.CombatState?.RoundEntities?.Remove(e);
                    }
                    catch (Exception ex)
                    {
                        ClassForgePlugin.Log.LogWarning(
                            "[ClassForge] a summon could not be cleaned up when " + when +
                            "; it is left in place: " + ex.Message);
                    }
                }

                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] purged " + doomed.Count + " leftover summon(s) when " + when + ".");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the summon purge failed when " + when +
                    "; combat continues unchanged: " + ex.Message);
            }
        }


        /// <summary>Adds the live, SUMMON-tagged characters in <paramref name="source"/> to
        /// <paramref name="into"/>, skipping ones already collected from the other roster.</summary>
        private static void Collect(List<Entity> source, List<Entity> into)
        {
            if (source == null) return;
            foreach (var e in source)
            {
                if (e == null || !e.Has<CharacterComponent>()) continue;
                if (!CharacterHelper.ActorHasProperty(eActorProperties.SUMMON, e)) continue;
                if (into.Contains(e)) continue;
                into.Add(e);
            }
        }
    }
}
