using System;
using HarmonyLib;
using UnityEngine;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Builds the 3D actor for a combatant that has none.
    ///
    /// <para><b>Why this is shared.</b> Adding a combatant and DRAWING it are separate jobs in this
    /// engine, and nothing enforces the pairing. <c>ApplyAction(ADD_CHARACTER)</c> creates a real,
    /// turn-taking combatant and draws nothing; <c>CharacterVisualHelper</c>'s
    /// <c>CHARACTER_ADDED_SMOKE</c> case opens with <c>pActorGameObjects[entity]</c>, i.e. it LOOKS UP
    /// an actor that must already exist. Creation is the caller's job, which is why the game's own
    /// summon branches do <c>_gameObjectMaps.FromCharacter[e] = CreateActorGameObject(...)</c>
    /// themselves.</para>
    ///
    /// <para><b>Why it is called twice.</b> A summon raised on ON_COMBAT_START — the Beast Trainer's
    /// partner wolf — is created before the combat canvas it would be parented to exists, so the draw
    /// at creation time cannot succeed no matter how correct it is. <see cref="SummonLeakPatches"/>
    /// retries from <c>_clearTileRenderState</c>, by which point the canvas is up. Without that retry
    /// the wolf is a live combatant with no model, and the modelless sweep deletes it.</para>
    /// </summary>
    internal static class SummonVisuals
    {
        /// <summary>
        /// Ensures <paramref name="entity"/> has an actor in the combat actor map, creating and
        /// positioning one if it does not. True when the entity ends up with a model.
        ///
        /// <para>Returns false rather than throwing for every failure, because the callers use it to
        /// decide between drawing a creature and dropping it — an exception here would take out the
        /// combat UI, which is the exact failure this whole path exists to prevent.</para>
        /// </summary>
        internal static bool TryBuildActor(Entity entity, object phase = null)
        {
            try
            {
                if (entity == null) return false;

                // Straight off the env. VenueDirectorBase declares this as
                //     protected VenueGameObjectMaps _gameObjectMaps => _env.VenueGameObjectMaps;
                // so it is a PROPERTY; a reflective lookup by FIELD name returns null.
                var maps = RouterHelper.Env?.VenueGameObjectMaps;
                if (maps?.FromCharacter == null) return false;
                if (maps.FromCharacter.ContainsKey(entity)) return true;

                // Prefer the phase the caller was HANDED. CombatPhaseInstance() digs it out of
                // RouterHelper._router reflectively, and a Harmony prefix on a CombatPhase method
                // already has the real instance as __instance -- no guessing, and it works even when
                // the router field lookup does not.
                phase = phase ?? CombatPhaseInstance();
                if (phase == null)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] no CombatPhase instance available, so a combatant cannot be drawn.");
                    return false;
                }

                // _canvas3D is a GameObject -- CombatPhase.Initialize takes `GameObject pCanvas3D`
                // and assigns it straight across. Casting it `as Component` yields null in Unity
                // (GameObject does not derive from Component), which made every draw look like
                // "the canvas is not up yet" while the canvas was in fact fine.
                var canvas3D = FindField(phase.GetType(), "_canvas3D")?.GetValue(phase) as GameObject;
                if (canvas3D == null) return false;   // genuinely not up yet; the retry will catch it

                var actor = CharacterVisualHelper.CreateActorGameObject(
                    entity, canvas3D.transform, new GameRandom(), pUseOverworldOverrides: false);
                if (actor == null) return false;
                maps.FromCharacter[entity] = actor;

                // Position it on its tile. OccupiedTiles is filled by TryCreateSummon; an empty list
                // averages ZERO tiles and drops the model at the diorama origin.
                var venue = entity.Get<VenueComponent>();
                if (venue?.OccupiedTiles != null && venue.OccupiedTiles.Count > 0)
                {
                    var centre = VenueViewHelper.GetAveragePositionOfTiles(venue.OccupiedTiles, maps.FromTile)
                                 + DioramaOffset(phase);
                    actor.transform.position = CharacterVisualHelper.GetCharacterRootPosition(entity, centre);
                }

                return true;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not build a 3D model for a combatant: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// A field by name anywhere in <paramref name="type"/>'s hierarchy, PRIVATE base-class fields
        /// included. <c>AccessTools.Field</c> forwards to <c>Type.GetField</c>, which deliberately
        /// does not surface those, so a lookup against the concrete phase type can return null for a
        /// field that plainly exists on its base.
        /// </summary>
        private static System.Reflection.FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>The live CombatPhase, reached through the router the same way Crucible does —
        /// these are internals with no public accessor.</summary>
        internal static object CombatPhaseInstance()
        {
            try
            {
                var router = AccessTools.Field(AccessTools.TypeByName("RouterHelper"), "_router")?.GetValue(null);
                return router == null
                    ? null
                    : AccessTools.Field(router.GetType(), "_combatPhase")?.GetValue(router);
            }
            catch (Exception) { return null; }
        }

        /// <summary>The diorama's PlayerOffset, or zero when it cannot be read.</summary>
        internal static Vector3 DioramaOffset(object phase)
        {
            try
            {
                var diorama = AccessTools.Field(phase.GetType(), "_diorama")?.GetValue(phase);
                if (diorama == null) return Vector3.zero;
                var offset = AccessTools.Field(diorama.GetType(), "PlayerOffset")?.GetValue(diorama);
                return offset is Vector3 ? (Vector3)offset : Vector3.zero;
            }
            catch (Exception) { return Vector3.zero; }
        }
    }
}
