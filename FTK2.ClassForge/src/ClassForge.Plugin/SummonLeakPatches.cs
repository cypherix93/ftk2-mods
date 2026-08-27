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
        /// <para>Running as a prefix, this DRAWS those orphans before the original runs, so the roster
        /// it then iterates is one it can fully resolve. It fires at every call site, covers creatures
        /// from any source -- a leaked summon, a save written mid-fight, a spawn whose actor build
        /// failed -- and needs no cooperation from whatever created them.</para>
        ///
        /// <para><b>What it must never do: delete.</b> Until 2026-08-25 the fallback for "could not be
        /// drawn" was to strip CombatComponent/VenueComponent and remove the entity from
        /// <c>CombatState.Entities</c>, <c>CombatState.RoundEntities</c> and <c>GameRun.Entities</c>.
        /// That is a rendering path mutating REPLICATED state on a LOCAL trigger, and it is the single
        /// worst desync vector in the mod:</para>
        /// <list type="bullet">
        ///   <item><see cref="SummonVisuals.TryBuildActor"/> fails on purely local art/timing conditions
        ///     -- <c>_canvas3D == null</c>, a prefab that has not streamed in, an addressables hitch.
        ///     Two peers trivially disagree about whether it failed.</item>
        ///   <item><c>AIHelper.ForceAiDecision</c> (AIHelper.cs:508-511) -- which EVERY ai path funnels
        ///     through (<c>StandardAiDecision:144</c>, <c>BehaviourAiDecision:395</c>,
        ///     <c>ConfusedAiDecision:451</c>) -- does
        ///     <c>list = VenueHelper.GetTargetableTiles(...); if (list.Count &gt; 0)
        ///     pGameRun.CombatState.Random.ShuffleList(list);</c>, and
        ///     <c>GameRandom.ShuffleList</c> (GameRandom.cs:222-236) takes EXACTLY <c>list.Count</c>
        ///     draws from the SHARED stream. A different roster means a different targetable-tile
        ///     count means a different number of draws on the very next AI turn -- permanent
        ///     divergence, not a cosmetic difference. <c>AIHelper._initializeAbilityBag</c> (:691-697)
        ///     compounds it.</item>
        /// </list>
        /// <para>The old comment's reasoning ("a missing creature is a smaller loss than an unplayable
        /// fight") is correct in single-player and catastrophic in multiplayer. The rule now is
        /// <b>INVISIBLE, never ABSENT</b>: a combatant that cannot be drawn keeps every component and
        /// its place in every roster, and the crash it would have caused is guarded at the throwing
        /// line instead -- see <see cref="CombatVisualNullGuards.ClearTileRenderState_Finalizer"/>.</para>
        ///
        /// <para>The escalation ladder for one modelless combatant is therefore:
        /// (1) build its real actor; (2) failing that, force a guaranteed-resolvable PLACEHOLDER donor
        /// record via <see cref="VisualRemapPatches.ForcePlaceholderDonor"/> and build again -- wrong
        /// art on one peer is a cosmetic bug, a missing combatant is a desync; (3) failing that, LOG AN
        /// ERROR naming the entity's ORDINAL in <c>CombatState.Entities</c> (the vendor's own cross-peer
        /// identity), its config name and its tile, and leave it alone. Never a silent repair.</para>
        /// </summary>
        public static void ClearTileRenderState_Prefix(object __instance)
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

                // Ordinal = index in CombatState.Entities. It is captured HERE, while the roster is
                // still being walked, because it is the identity the escalation log reports: the
                // vendor's own cross-peer handle for a combatant, and the only one two players can
                // compare over voice chat.
                List<KeyValuePair<Entity, int>> modelless = null;
                for (int i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    if (e == null) continue;
                    if (!e.Has<CharacterComponent>() || !e.Has<CombatComponent>() || !e.Has<VenueComponent>())
                        continue;
                    if (CharacterHelper.IsDead(e)) continue;
                    if (maps.FromCharacter.ContainsKey(e)) continue;

                    (modelless ?? (modelless = new List<KeyValuePair<Entity, int>>()))
                        .Add(new KeyValuePair<Entity, int>(e, i));
                }
                if (modelless == null) return;

                int drawn = 0, placeheld = 0, unrenderable = 0;
                foreach (var pair in modelless)
                {
                    var e = pair.Key;

                    // (1) The real actor.
                    //
                    // A summon created during ON_COMBAT_START has no model yet because the combat
                    // canvas it would be parented to does not exist that early, so the draw at
                    // creation time fails. By the time this runs the canvas IS up, which makes this
                    // the natural second chance -- and the Beast Trainer's partner wolf only exists
                    // at all because of it.
                    if (SummonVisuals.TryBuildActor(e, __instance)) { drawn++; continue; }

                    // (2) A PLACEHOLDER actor. VisualRemapPatches owns donor resolution and its
                    // LastResort list makes that resolution total, so forcing a donor for this config
                    // and building again turns "no model" into "wrong model". That trade is the whole
                    // point: wrong art is local and cosmetic, a missing combatant changes the roster
                    // and desyncs the shared RNG stream on the next AI turn.
                    if (VisualRemapPatches.ForcePlaceholderDonor(e) &&
                        SummonVisuals.TryBuildActor(e, __instance))
                    {
                        placeheld++;
                        continue;
                    }

                    // (3) Genuinely unrenderable. It STAYS -- component-complete and in every roster.
                    // _clearTileRenderState's unchecked indexer will throw for it and
                    // CombatVisualNullGuards.ClearTileRenderState_Finalizer will absorb that into
                    // degraded tile render state. Escalate loudly; never repair silently.
                    unrenderable++;
                    ClassForgePlugin.Log.LogError(
                        "[ClassForge] combatant " + Describe(e, pair.Value) + " has NO 3D model and one " +
                        "could not be built even from a placeholder donor. It is DELIBERATELY left in " +
                        "the fight -- removing it would change the roster on this peer only and desync " +
                        "the shared combat RNG. Expect missing art and degraded tile highlighting for " +
                        "it. Investigate the character config.");
                }

                if (drawn > 0)
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] built a 3D model for " + drawn + " combatant(s) that had none " +
                        "(summons created before the combat canvas existed).");
                if (placeheld > 0)
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] drew " + placeheld + " combatant(s) with a PLACEHOLDER donor model " +
                        "because their own art could not be built. The art on screen is WRONG for them; " +
                        "the fight itself is correct and stays in sync. Investigate the character config.");
                if (unrenderable > 0)
                    ClassForgePlugin.Log.LogError(
                        "[ClassForge] " + unrenderable + " combatant(s) could not be drawn at all and were " +
                        "left in the roster on purpose (see the per-combatant lines above).");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the modelless-combatant sweep failed; combat continues unchanged: "
                    + ex.Message);
            }
        }

        /// <summary>
        /// "ordinal N (CONFIG_NAME) at tile (x, y)" — the identity the escalation log reports.
        /// <paramref name="ordinal"/> is the entity's index in <c>CombatState.Entities</c>, which is
        /// the roster both peers replicate; a GUID would be unusable across two machines' logs.
        /// Every field is read defensively, because this only ever runs on an already-degraded entity.
        /// </summary>
        private static string Describe(Entity e, int ordinal)
        {
            var config = "<unknown config>";
            var tile = "<unknown tile>";
            try
            {
                CharacterComponent cc;
                if (e.TryGet<CharacterComponent>(out cc) && cc != null && !string.IsNullOrEmpty(cc.ConfigName))
                    config = cc.ConfigName;
            }
            catch (Exception) { }
            try
            {
                VenueComponent vc;
                if (e.TryGet<VenueComponent>(out vc) && vc != null)
                    tile = vc.TilePosition.ToString();
            }
            catch (Exception) { }
            return "ordinal " + ordinal + " (" + config + ") at tile " + tile;
        }

        /// <summary>
        /// Guards <c>CombatViewHelper.GetTargetHighlights</c> (CombatViewHelper.cs:3202), the third
        /// site with this exact defect. Its tile loop (CombatViewHelper.cs:3248-3252) walks EVERY
        /// tile on the whole map with <c>GroupIndex &gt; -1</c> -- not just tiles related to what is
        /// being targeted -- finds whoever occupies each one, and does:
        /// <code>
        /// ActorGameObjectBase actorGameObjectBase =
        ///     ((characterOnTile != null) ? pGameObjectMaps.FromCharacter[characterOnTile] : null);
        /// </code>
        /// with no membership check, at line 3252. Because the loop covers the whole board, ANY
        /// combatant anywhere that is missing from <c>FromCharacter</c> takes down the whole method --
        /// including the ability actually being targeted, which may have nothing to do with the
        /// entity that is missing its model.
        ///
        /// <para><b>Measured live.</b> <c>CombatPhase._performAiDecision</c> (CombatPhase.cs:1449,
        /// which calls <c>GetTargetHighlights</c> at CombatPhase.cs:1497) faulted asynchronously for
        /// ability FLEE with:</para>
        /// <code>
        /// KeyNotFoundException: The given key '(HOBGOBLIN_HEAVY_02) - (4, 5) - (30, 38) cf022f22-...'
        /// was not present in the dictionary.
        /// </code>
        /// <para>The Hobgoblin stood at tile (4,5); the ability targeted tile (5,5). It faulted on an
        /// unrelated bystander, exactly as the code above predicts. That fight had gone through a wave
        /// advance, so a second-wave enemy whose actor was never built is the likely origin -- unconfirmed,
        /// since no log line was captured proving which entity or which wave produced the missing map
        /// entry, only that the fault occurred after one.</para>
        ///
        /// <para><b>The fix</b> is the same one <see cref="ClearTileRenderState_Prefix"/> uses for the
        /// identical defect: before the original runs, walk the same "living combatant on a
        /// non-negative-group tile" set the original will touch, and for anyone missing from
        /// <c>FromCharacter</c>, try to BUILD their model via <see cref="SummonVisuals.TryBuildActor"/>
        /// -- the same builder, so a wave-2 spawn gets exactly the second chance a pre-canvas summon
        /// does. A combatant that still can't be built is left in place and only logged, never removed
        /// -- the same rule <c>ClearTileRenderState_Prefix</c> now follows, and for the same reason:
        /// a rendering path must never mutate replicated roster state, because a roster that differs
        /// between peers changes <c>GetTargetableTiles(...).Count</c> and therefore the number of draws
        /// <c>ShuffleList</c> takes from the shared RNG on the next AI turn. See
        /// <see cref="ClearTileRenderState_Prefix"/>'s doc for the full chain. If the build attempt
        /// still leaves a gap, the original method's indexer throws exactly as it does today.</para>
        /// </summary>
        public static void GetTargetHighlights_Prefix(List<Entity> pEntities, VenueGameObjectMaps pGameObjectMaps)
        {
            try
            {
                if (pEntities == null || pEntities.Count == 0) return;
                if (pGameObjectMaps?.FromCharacter == null)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] GetTargetHighlights was handed no actor map, so combatants with " +
                        "no 3D model cannot be swept; the targeting lookup may throw.");
                    return;
                }

                int built = 0, stillMissing = 0;
                foreach (var e in pEntities)
                {
                    if (e == null) continue;
                    // Same membership test the original's characterOnTile lookup uses
                    // (CombatViewHelper.cs:3249), plus IsDead: a dead occupant is never selected as
                    // characterOnTile, so it is never indexed and does not need a model here.
                    if (!e.Has<CharacterComponent>() || !e.Has<VenueComponent>() || !e.Has<CombatComponent>())
                        continue;
                    if (CharacterHelper.IsDead(e)) continue;
                    if (pGameObjectMaps.FromCharacter.ContainsKey(e)) continue;

                    // DRAW FIRST, same as ClearTileRenderState_Prefix. Never remove: this method has
                    // no business deleting a combatant just to make an unrelated ability's highlight
                    // calculation succeed.
                    if (SummonVisuals.TryBuildActor(e)) built++;
                    else stillMissing++;
                }

                if (built > 0)
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] GetTargetHighlights: built a 3D model for " + built +
                        " combatant(s) that had none before target highlighting ran.");
                if (stillMissing > 0)
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] GetTargetHighlights: " + stillMissing + " combatant(s) still have " +
                        "no 3D model after the build attempt; left in place, and the targeting lookup " +
                        "may still throw for them. Investigate how they entered combat without one.");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the GetTargetHighlights actor sweep failed; targeting continues " +
                    "unchanged: " + ex.Message);
            }
        }

        /// <summary>Purge on the way in, so an already-poisoned save loads into a working fight.</summary>
        public static void Initialize_Prefix()
        {
            // Trainer partners are bound to their balls per fight; the durable HP record already lives
            // on the ball item, so dropping the entity->ball map here loses nothing and guarantees no
            // reference to a previous fight's roster survives into this one.
            TrainerPartnerPersistence.ClearLive();
            Purge("entering combat");
        }

        /// <summary>
        /// Purge on the way out. Prefix, not postfix: <c>Deinitialize</c> calls
        /// <c>_clearTileRenderState</c> itself, so a postfix would run after the throw it is meant to
        /// prevent.
        /// </summary>
        public static void Deinitialize_Prefix()
        {
            TrainerPartnerPersistence.ClearLive();
            Purge("leaving combat");
        }

        /// <summary>
        /// Strips combat residue from every SUMMON-tagged creature, mirroring
        /// <c>CombatPhase._removeEntityFromCombat(e, pAddToWaves: false)</c>.
        ///
        /// <para>This is the game's own intent applied where it cannot be skipped:
        /// <c>_endCombatAsync</c> banishes summons inside an <c>if (!pIsImmediate)</c> block, so every
        /// immediate exit leaves one standing on a tile and it is still there when the next fight
        /// starts.</para>
        ///
        /// <para>The SUMMON actor property is the right discriminator because it is what
        /// <c>_endCombatAsync</c> itself selects on, and <c>TryCreateSummon</c> deliberately withholds
        /// it when <c>CharacterType == COMPANION</c> — so permanent companions and AS_FOLLOWER
        /// recruits are invisible to this pass and are never banished by it.</para>
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

                        // CF-ROSTER-OK: banishing SUMMON-tagged creatures at combat entry/exit is the
                        // game's own _endCombatAsync cleanup, keyed on replicated state (the SUMMON
                        // actor property) and NOT on any local art or timing condition, so every peer
                        // reaches the identical decision at the identical point. It also runs outside
                        // any turn, so no AI draw is in flight. Contrast the modelless sweep, which
                        // must never remove anything.
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
