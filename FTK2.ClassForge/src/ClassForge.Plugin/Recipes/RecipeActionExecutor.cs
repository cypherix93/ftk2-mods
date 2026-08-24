using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine;
using HarmonyLib;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Everything the executor needs from the hook that produced the plan. Whatever the hook could not
    /// supply is resolved from live combat state.
    /// </summary>
    internal sealed class RecipeExecEnvironment
    {
        internal CombatContextAdapter Ctx;
        internal List<Entity> Party;
        internal List<(eAbilityResults, object)> Results;
        internal Thing Thing;

        /// <summary>The hook's own <c>pGetStat</c>, when it had one. Reusing the real delegate keeps a
        /// recipe-emitted <c>CHANGE_STAT</c> arithmetically identical to a natively-emitted one.</summary>
        internal Func<Entity, string, eGetStatEquippedFilters, int> GetStat;

        /// <summary>The hook's own <c>pGetTileStat</c>, when it had one.</summary>
        internal Func<Entity, string, int> GetTileStat;

        /// <summary>A copy carrying a different Thing. Used where the native path needs one and the
        /// recipe, not being item-sourced, has none.</summary>
        internal RecipeExecEnvironment WithThing(Thing thing)
        {
            return new RecipeExecEnvironment
            {
                Ctx = Ctx, Party = Party, Results = Results, Thing = thing,
                GetStat = GetStat, GetTileStat = GetTileStat,
            };
        }
    }

    /// <summary>
    /// Translates the dispatcher's ordered <see cref="EngineAction"/> plan into native calls.
    ///
    /// <para><b>Effect emission rule (SPEC-DELTA-v1.1 §4, §5.2 invariant 5).</b> Every state-changing effect
    /// is emitted by constructing the equivalent <c>(eCombatActions, object)</c> pair and routing it through
    /// <c>CombatHelper.ApplyAction</c> (CombatHelper.cs L1871). Nothing bypasses the native action pipeline
    /// and no bespoke <c>_SYNC_</c> action is defined.</para>
    ///
    /// <para><b>Payload shapes, read verbatim from <c>ApplyAction</c>'s switch</b> — these differ per verb
    /// and getting them wrong is a hard crash, not a degradation:</para>
    /// <list type="bullet">
    /// <item><c>ADD_STATUS</c> (L1883-1889) and <c>REMOVE_STATUS</c> (L2010-2017) take a <b>bare string</b>
    /// (<c>pActionArgs is JsonElement ? ((JsonElement)pActionArgs).GetString() : (string)pActionArgs</c>) —
    /// there is no <c>AddStatusAction</c>/<c>RemoveStatusAction</c> type in the game at all.</item>
    /// <item><c>CHANGE_STAT</c> (L2030-2036) casts <c>((JsonElement)pActionArgs).GetRawText()</c>
    /// <b>unconditionally</b> — handing it a raw <c>ChangeStatAction</c> instance throws
    /// <c>InvalidCastException</c>. It must be a <c>JsonElement</c>.</item>
    /// <item><c>ADD_CHARACTER</c> (L2143) likewise requires a <c>JsonElement</c>.</item>
    /// </list>
    ///
    /// <para><b>The <c>PERFECT</c> gate.</b> <c>ApplyAction</c>'s <c>ADD_STATUS</c> and <c>REMOVE_STATUS</c>
    /// cases both open with <c>if (pRollData.Status != eRollStatus.PERFECT) break;</c>. A recipe proc is a
    /// decision that has already been made — conditions, cooldown, budget and the <c>ProcChance</c> roll all
    /// passed — so the synthetic <c>RollResultData</c> below carries <c>PERFECT</c>. Anything else would make
    /// status effects silently no-op.</para>
    ///
    /// <para><b>Not executed here:</b> <c>ROLL_STAT_BONUS</c> (consumed by the <c>PerformAbility</c> prefix's
    /// delegate swap) and <c>HEAL_MODIFIER</c> (consumed by the <c>AddHealth</c> prefix's <c>ref int pValue</c>
    /// mutation) — neither is an action, both are in-flight value adjustments. <c>COUNTER_ADD</c>/
    /// <c>COUNTER_SET</c> are pure per-battle state writes the engine has <b>already</b> performed; the
    /// actions exist only for the parity/verbose log.</para>
    /// </summary>
    internal static class RecipeActionExecutor
    {
        /// <summary>Synthetic ability name on emitted actions, so BepInEx logs and result tuples attribute
        /// the effect to ClassForge rather than to whatever ability happened to trigger it.</summary>
        private const string AbilityNamePrefix = "CF_RECIPE_";

        internal static void Execute(IReadOnlyList<EngineAction> plan, RecipeExecEnvironment env)
        {
            if (plan == null || plan.Count == 0 || env == null || env.Ctx == null) return;

            using (RecipeEngineHost.EnterExecution())
            {
                var results = env.Results ?? new List<(eAbilityResults, object)>();
                var party = RecipeEngineHost.ResolveParty(env.Party, env.Ctx);

                for (int i = 0; i < plan.Count; i++)
                {
                    var action = plan[i];
                    if (action == null) continue;
                    try
                    {
                        Dispatch(action, env, party, results);
                    }
                    catch (Exception ex)
                    {
                        // Fail-safe per action: one bad effect never takes down the rest of the plan,
                        // and never escapes the Harmony patch body.
                        ClassForgePlugin.Log.LogError(
                            "[ClassForge] Recipe effect failed (skipped, rest of plan continues): " +
                            action.Describe() + " -> " + ex);
                    }
                }
            }
        }

        private static void Dispatch(EngineAction action, RecipeExecEnvironment env,
            List<Entity> party, List<(eAbilityResults, object)> results)
        {
            var ctx = env.Ctx;
            var origin = ctx.NativeByGuid(action.OwnerGuid);

            var addStatus = action as AddStatusAction;
            if (addStatus != null) { ExecAddStatus(addStatus, origin, env, party, results); return; }

            var removeStatus = action as RemoveStatusAction;
            if (removeStatus != null) { ExecRemoveStatus(removeStatus, origin, env, party, results); return; }

            var statChange = action as StatChangeAction;
            if (statChange != null) { ExecStatChange(statChange, origin, env, party, results); return; }

            var summon = action as SummonAction;
            if (summon != null) { ExecSummon(summon, origin, env, party, results); return; }

            var banner = action as EventBannerAction;
            if (banner != null) { ExecEventBanner(banner); return; }

            // RollStatBonusAction / HealModifierAction: handled by their owning prefixes, not here.
            // CounterAddAction / CounterSetAction / SelectionSetAction: engine already applied the state
            // write; log only.
            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug("[ClassForge] (no native call) " + action.Describe());
        }

        // ------------------------------------------------------------------ EVENT_BANNER

        /// <summary>
        /// <c>EVENT_BANNER</c> — Encounter Modifiers spec §5/§9. <c>[LOCAL]</c> presentation only: builds the
        /// display text from <see cref="EventBannerAction.LocKey"/> (looked up via the game's localization,
        /// falling back to <see cref="EventBannerAction.FallbackText"/> when the key has none) with
        /// <see cref="EventBannerAction.SelectionValue"/> substituted for a <c>{0}</c> placeholder, then
        /// calls <c>GameplayDialogViewHelper.ShowEventTitle(text, DurationMs)</c>. Exceptions are swallowed —
        /// this never gates gameplay and is excluded from SafeMode considerations like all presentation (R4).
        /// </summary>
        private static void ExecEventBanner(EventBannerAction a)
        {
            try
            {
                string text = ResolveBannerText(a);
                if (string.IsNullOrEmpty(text)) return;
                GameplayDialogViewHelper.ShowEventTitle(text, a.DurationMs > 0 ? a.DurationMs : 3000);
            }
            catch (Exception ex)
            {
                // [LOCAL] presentation — never let a banner failure touch gameplay state or propagate.
                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug("[ClassForge] EVENT_BANNER failed (swallowed, presentation-only): " + ex);
            }
        }

        /// <summary>Prefers the localized <c>Lang.__t(LocKey, SelectionValue)</c> string (native lookup +
        /// <c>{0}</c>-style arg substitution in one call); falls back to <see cref="EventBannerAction.FallbackText"/>
        /// (itself <c>{0}</c>-substituted) when the key is missing or unresolvable.</summary>
        private static string ResolveBannerText(EventBannerAction a)
        {
            if (!string.IsNullOrEmpty(a.LocKey))
            {
                try
                {
                    bool success;
                    string localized = Lang.__t(a.LocKey, out success, a.SelectionValue ?? "");
                    if (success && !string.IsNullOrEmpty(localized)) return localized;
                }
                catch { /* fall through to FallbackText */ }
            }

            if (string.IsNullOrEmpty(a.FallbackText)) return null;
            if (a.FallbackText.IndexOf("{0}", StringComparison.Ordinal) >= 0)
                return string.Format(CultureInfo.InvariantCulture, a.FallbackText, a.SelectionValue ?? "");
            return a.FallbackText;
        }

        // ------------------------------------------------------------------ ADD_STATUS

        private static void ExecAddStatus(AddStatusAction a, Entity origin, RecipeExecEnvironment env,
            List<Entity> party, List<(eAbilityResults, object)> results)
        {
            var target = env.Ctx.NativeByGuid(a.TargetGuid);
            if (target == null || string.IsNullOrEmpty(a.StatusId)) return;

            int before = results.Count;
            ApplyStatusOnce(a.StatusId, a.Duration, origin, target, env, party, results, a.RecipeId);

            if (string.IsNullOrEmpty(a.FallbackStatusId)) return;

            // IMMUNITY_FALLBACK (§4.1): result-driven, not immunity-table-driven. Inspect the results the
            // native call appended; if no STATUS_ADDED landed, the primary did not take (immunity, cap,
            // already-present, whatever) and the fallback is emitted. Deterministic, no RNG. This degrades
            // correctly for ANY reason the primary failed, with zero introspection of STATUS_IMMUNITY_* config.
            if (WasStatusAdded(results, before)) return;

            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] '" + a.StatusId + "' did not apply (no STATUS_ADDED result) — emitting FallbackStatus '" +
                    a.FallbackStatusId + "' for recipe " + a.RecipeId + ".");

            ApplyStatusOnce(a.FallbackStatusId, a.Duration, origin, target, env, party, results, a.RecipeId);
        }

        private static void ApplyStatusOnce(string statusId, int? duration, Entity origin, Entity target,
            RecipeExecEnvironment env, List<Entity> party, List<(eAbilityResults, object)> results, string recipeId)
        {
            if (duration.HasValue)
            {
                // ApplyAction's ADD_STATUS path calls ApplyStatus WITHOUT pDurationOverride, so a Duration
                // override can only be expressed through the single-target overload directly
                // (InteractableHelper.cs L1219, `int? pDurationOverride = null`). This is still a native
                // verb — it is the exact call ApplyAction itself makes one line deeper (CombatHelper.cs L1993).
                InteractableHelper.ApplyStatus(origin, target, env.Thing, ResolvableAbilityName(), statusId,
                    env.Ctx.Random, results, true, true, duration);
                return;
            }

            ApplyAction(eCombatActions.ADD_STATUS, statusId, origin, target, env, party, results, recipeId);
        }

        /// <summary>Scans only the range the native call just appended, so an earlier unrelated
        /// <c>STATUS_ADDED</c> in the shared results list cannot be mistaken for success.</summary>
        private static bool WasStatusAdded(List<(eAbilityResults, object)> results, int fromIndex)
        {
            for (int i = fromIndex; i < results.Count; i++)
                if (results[i].Item1 == eAbilityResults.STATUS_ADDED) return true;
            return false;
        }

        // ------------------------------------------------------------------ REMOVE_STATUS

        private static void ExecRemoveStatus(RemoveStatusAction a, Entity origin, RecipeExecEnvironment env,
            List<Entity> party, List<(eAbilityResults, object)> results)
        {
            var target = env.Ctx.NativeByGuid(a.TargetGuid);
            if (target == null || string.IsNullOrEmpty(a.StatusId)) return;
            ApplyAction(eCombatActions.REMOVE_STATUS, a.StatusId, origin, target, env, party, results, a.RecipeId);
        }

        // ------------------------------------------------------------------ CHANGE_STAT

        private static void ExecStatChange(StatChangeAction a, Entity origin, RecipeExecEnvironment env,
            List<Entity> party, List<(eAbilityResults, object)> results)
        {
            var target = env.Ctx.NativeByGuid(a.TargetGuid);
            if (target == null || string.IsNullOrEmpty(a.Stat)) return;

            // ChangeStatAction's real field names (ChangeStatAction.cs): Stat, Type (eDamageType),
            // IsBlockable, IsSilent, FlatValue (int?), FlatPercent (int?). Note it is `IsBlockable`, not
            // `Blockable`, and `Type` is an eDamageType — a JSON member name mismatch would silently
            // deserialize to the enum default (PHYSICAL = 0).
            string json = BuildChangeStatJson(a);
            using (var doc = JsonDocument.Parse(json))
            {
                // HP needs a Thing; FOC does not.
                //
                // InteractableHelper.ApplyStatChange's "HP" case runs the full damage/heal pipeline
                // -- CalculateFinalDamage, the thorn/protect/steadfast reactions, dungeon modifiers
                // -- and several of those dereference pThing without a null check. Recipe effects
                // are not item-sourced, so env.Thing is null and the whole effect died with a
                // NullReferenceException that the per-action catch then swallowed as "skipped".
                //
                // Nothing caught this earlier because EVERY shipped STAT_CHANGE in
                // CF_PACK_EOR_CLASSES targets FOC, whose case is a simple stat write that never
                // touches pThing. Measured live 2026-08-24 on SKILL_CF_VAMPIRIC_BLOOD_PRICE.
                //
                // The origin's equipped weapon is the honest stand-in: it is the Thing the game
                // would have passed had this damage come from an ordinary attack by the same
                // character.
                var env2 = env;
                if (env.Thing == null && IsHealthStat(a.Stat))
                {
                    var weapon = TryGetEquippedWeapon(origin);
                    if (weapon != null)
                        env2 = env.WithThing(weapon);
                    else if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                        ClassForgePlugin.Log.LogDebug(
                            "[ClassForge] STAT_CHANGE on " + a.Stat + " has no Thing and the origin has no " +
                            "equipped weapon; the native damage path may throw.");
                }

                ApplyStatChangeAction(doc.RootElement, origin, target, env2, party, results);
            }
        }

        /// <summary>HP and its aliases route through the native damage pipeline, which needs a Thing.</summary>
        private static bool IsHealthStat(string stat)
        {
            return string.Equals(stat, "HP", StringComparison.Ordinal)
                || string.Equals(stat, "MXHP", StringComparison.Ordinal);
        }

        /// <summary>
        /// The origin's equipped main-hand weapon, or null. Never throws: a missing weapon is a
        /// normal state (an unarmed summon, an inanimate) and must not take the effect down.
        /// </summary>
        private static Thing TryGetEquippedWeapon(Entity origin)
        {
            try
            {
                if (origin == null) return null;
                CharacterComponent character;
                if (!origin.TryGet<CharacterComponent>(out character)) return null;
                return EquipmentHelper.GetEquippedWeaponThing(character);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string BuildChangeStatJson(StatChangeAction a)
        {
            var sb = new StringBuilder();
            sb.Append("{\"Stat\":").Append(JsonString(a.Stat));
            sb.Append(",\"Type\":").Append(JsonString(NormalizeDamageType(a.StatChangeType)));
            sb.Append(",\"IsBlockable\":").Append(a.Blockable ? "true" : "false");
            sb.Append(",\"IsSilent\":").Append(a.IsSilent ? "true" : "false");
            if (a.FlatValue.HasValue)
                sb.Append(",\"FlatValue\":").Append(a.FlatValue.Value.ToString(CultureInfo.InvariantCulture));
            if (a.FlatPercent.HasValue)
                sb.Append(",\"FlatPercent\":").Append(a.FlatPercent.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// <c>ChangeStatAction.Type</c> is an <c>eDamageType</c>
        /// (<c>NONE, PHYSICAL, MAGICAL, FIRE, BLEED, POISON, RATTLED, STRENGTH, INFINITE_FIRE, CHAOS, REGEN</c>
        /// — note there is <b>no</b> <c>WATER</c>). An unrecognised token would deserialize to
        /// <c>PHYSICAL</c> silently, so it is normalised to <c>MAGICAL</c> (the unblockable-ish default the
        /// SPEC's own <c>FOC</c> example uses) and logged instead.
        /// </summary>
        private static string NormalizeDamageType(string token)
        {
            if (string.IsNullOrEmpty(token)) return "MAGICAL";
            foreach (var name in Enum.GetNames(typeof(eDamageType)))
                if (string.Equals(name, token, StringComparison.Ordinal)) return name;

            ClassForgePlugin.Log.LogWarning(
                "[ClassForge] STAT_CHANGE StatChangeType '" + token + "' is not an eDamageType member " +
                "(NONE, PHYSICAL, MAGICAL, FIRE, BLEED, POISON, RATTLED, STRENGTH, INFINITE_FIRE, CHAOS, REGEN) " +
                "— using MAGICAL. Note eDamageType has no WATER.");
            return "MAGICAL";
        }

        // ------------------------------------------------------------------ ADD_CHARACTER (SUMMON)

        private static void ExecSummon(SummonAction a, Entity origin, RecipeExecEnvironment env,
            List<Entity> party, List<(eAbilityResults, object)> results)
        {
            var target = env.Ctx.NativeByGuid(a.TargetGuid);
            if (target == null || string.IsNullOrEmpty(a.CharacterConfig)) return;

            // ADD_CHARACTER needs a TILE entity, not a character.
            //
            // CombatHelper.ApplyAction's ADD_CHARACTER case reads
            // `pTarget.Get<VenueTileComponent>().GroupIndex` and `.RowPositionsType` — both
            // unconditionally. A character carries a VenueComponent, NOT a VenueTileComponent, so
            // handing it the killed enemy throws NullReferenceException before TryCreateSummon is
            // ever reached. UseTargetPosition (set by the dispatcher for TRIGGER_TARGET_POSITION)
            // was being computed and then ignored here, which is the whole bug: the recipe procced,
            // the effect threw, and no creature appeared.
            //
            // So resolve the tile the target is standing on and summon against that.
            // ALWAYS resolve a friendly tile, whatever the authored Target was. A summon has to land
            // on a tile entity regardless (ADD_CHARACTER reads VenueTileComponent off the target,
            // which a character does not have), and the tile decides allegiance, so there is no
            // authored Target for which passing the raw entity is correct.
            {
                // The summoned creature's ALLEGIANCE comes from the tile it lands on:
                // TryCreateSummon is handed `pTarget.Get<VenueTileComponent>().GroupIndex` and
                // assigns it straight onto the new character. Placing on a slain enemy's tile
                // therefore spawns a HOSTILE creature -- measured 2026-08-24, the Beast Trainer's
                // "partner" appeared in red on the enemy side and fought the party.
                //
                // So a summon is placed on a free tile belonging to the SUMMONER's own group. That
                // also removes the dependency on something having just died: any empty friendly
                // tile will do.
                var tile = FindFreeTileForGroup(origin, env);
                if (tile == null)
                {
                    if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                        ClassForgePlugin.Log.LogDebug(
                            "[ClassForge] SUMMON for " + a.RecipeId + ": the summoner's side has no free " +
                            "tile, so " + a.CharacterConfig + " could not be placed.");
                    return;
                }
                target = tile;
            }

            // OQ#4: AddCharacterAction is { eSummonTypes Type; string Value; } with NO count field, and
            // TryCreateSummon returns a single `out Entity`. The engine has already expanded the authored
            // Count into N sequential SummonActions (Index 0..Count-1 ascending), each of which becomes its
            // own ApplyAction call with its own freshly-deserialized payload and its own placement draws.
            string json = "{\"Type\":" + JsonString(a.SummonType.ToString()) +
                          ",\"Value\":" + JsonString(a.CharacterConfig) + "}";
            // Note which combatants exist before, so the new one can be found and DRAWN afterwards.
            var before = SnapshotCombatRoster();

            using (var doc = JsonDocument.Parse(json))
            {
                ApplyAction(eCombatActions.ADD_CHARACTER, doc.RootElement, origin, target, env, party, results, a.RecipeId);
            }

            DrawNewSummon(before, a.RecipeId, a.CharacterConfig);
        }

        /// <summary>Identity set of the current combat roster, for diffing after a summon.</summary>
        private static HashSet<Entity> SnapshotCombatRoster()
        {
            var set = new HashSet<Entity>();
            try
            {
                var entities = RouterHelper.Env?.GameRun?.CombatState?.Entities;
                if (entities != null) foreach (var e in entities) if (e != null) set.Add(e);
            }
            catch (Exception) { }
            return set;
        }

        /// <summary>
        /// Builds the actor GameObject for a creature ADD_CHARACTER just created, and places it.
        ///
        /// <para>ADD_CHARACTER does NOT draw anything. CharacterVisualHelper's
        /// <c>CHARACTER_ADDED_SMOKE</c> case opens with <c>pActorGameObjects[entity]</c> -- it LOOKS
        /// UP an actor that must already exist and merely activates it. Creation is the caller's
        /// job, which is why CombatPhase's own summon branches do
        /// <c>_gameObjectMaps.FromCharacter[e] = CreateActorGameObject(...)</c> themselves.</para>
        ///
        /// <para>Without this a recipe summon is a fully functional combatant that is never drawn:
        /// the Beast Trainer's partner wolf was correctly created as an ALLY on the party's side of
        /// the grid, took turns, and was invisible -- <c>FromCharacter</c> simply had no entry for
        /// it. Measured 2026-08-24.</para>
        ///
        /// Presentation only: every failure is logged and swallowed so a drawing problem can never
        /// take down a summon that is otherwise correct.
        /// </summary>
        /// <summary>The diorama's PlayerOffset, or zero when it cannot be read.</summary>
        private static UnityEngine.Vector3 DioramaOffset(object phase)
        {
            try
            {
                var diorama = AccessTools.Field(phase.GetType(), "_diorama")?.GetValue(phase);
                if (diorama == null) return UnityEngine.Vector3.zero;
                var offset = AccessTools.Field(diorama.GetType(), "PlayerOffset")?.GetValue(diorama);
                return offset is UnityEngine.Vector3 ? (UnityEngine.Vector3)offset : UnityEngine.Vector3.zero;
            }
            catch (Exception) { return UnityEngine.Vector3.zero; }
        }

        private static void DrawNewSummon(HashSet<Entity> before, string recipeId, string characterConfig)
        {
            try
            {
                var entities = RouterHelper.Env?.GameRun?.CombatState?.Entities;
                if (entities == null) return;

                Entity spawned = null;
                foreach (var e in entities) if (e != null && !before.Contains(e)) spawned = e;
                if (spawned == null) return;

                // The phase and its private view state, reached the same way Crucible does: these
                // are internals of CombatPhase with no public accessor.
                var router = AccessTools.Field(AccessTools.TypeByName("RouterHelper"), "_router")?.GetValue(null);
                var phase = router == null ? null : AccessTools.Field(router.GetType(), "_combatPhase")?.GetValue(router);
                if (phase == null) return;

                // Straight off the env. VenueDirectorBase declares this as
                //     protected VenueGameObjectMaps _gameObjectMaps => _env.VenueGameObjectMaps;
                // so it is a PROPERTY, and a reflective lookup by FIELD name returns null -- which
                // silently skipped every draw and is why recipe summons were invisible.
                var maps = RouterHelper.Env?.VenueGameObjectMaps;
                if (maps == null || maps.FromCharacter == null) return;
                if (maps.FromCharacter.ContainsKey(spawned)) return;

                var canvas3D = AccessTools.Field(phase.GetType(), "_canvas3D")?.GetValue(phase) as Component;
                if (canvas3D == null) return;

                var actor = CharacterVisualHelper.CreateActorGameObject(
                    spawned, canvas3D.transform, new GameRandom(), pUseOverworldOverrides: false);
                if (actor == null) return;
                maps.FromCharacter[spawned] = actor;

                // Position it on its tile. OccupiedTiles is filled by TryCreateSummon; an empty list
                // averages ZERO tiles and would drop the model at the diorama origin.
                var venue = spawned.Get<VenueComponent>();
                if (venue != null && venue.OccupiedTiles != null && venue.OccupiedTiles.Count > 0)
                {
                    var centre = VenueViewHelper.GetAveragePositionOfTiles(venue.OccupiedTiles, maps.FromTile)
                                 + DioramaOffset(phase);
                    actor.transform.position = CharacterVisualHelper.GetCharacterRootPosition(spawned, centre);
                }

                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] drew summon " + characterConfig + " for " + recipeId + ".");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] summon for " + recipeId + " was created but could not be drawn: " + ex.Message);
            }
        }

        /// <summary>
        /// A free combat tile belonging to <paramref name="summoner"/>'s group, or null.
        ///
        /// Tiles are entities in their own right: they carry a <c>VenueTileComponent</c> holding the
        /// GroupIndex and RowPositionsType that <c>TryCreateSummon</c> consumes, while characters
        /// carry a <c>VenueComponent</c> holding a TilePosition. A tile counts as free when no LIVING
        /// character stands on it -- the same test <c>CombatHelper.ApplyAction</c> applies before it
        /// will place anything.
        ///
        /// Front tiles are preferred so a summoned ally lands where it can act rather than stranded
        /// in the back row.
        /// </summary>
        private static Entity FindFreeTileForGroup(Entity summoner, RecipeExecEnvironment env)
        {
            try
            {
                CharacterComponent summonerCharacter;
                if (summoner == null || !summoner.TryGet<CharacterComponent>(out summonerCharacter)) return null;
                int wantedGroup = summonerCharacter.GroupIndex;

                var routerEnv = RouterHelper.Env;
                var entities = routerEnv == null || routerEnv.GameRun == null || routerEnv.GameRun.CombatState == null
                    ? null : routerEnv.GameRun.CombatState.Entities;
                if (entities == null) return null;

                // Positions held by anything still alive.
                var occupied = new HashSet<(int, int)>();
                for (int i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    if (e == null || !e.Has<CharacterComponent>()) continue;
                    if (CharacterHelper.IsDead(e)) continue;
                    VenueComponent venue;
                    if (!e.TryGet<VenueComponent>(out venue)) continue;
                    occupied.Add(venue.TilePosition);
                }

                Entity fallback = null;
                for (int i = 0; i < entities.Count; i++)
                {
                    var candidate = entities[i];
                    if (candidate == null) continue;

                    VenueTileComponent tile;
                    if (!candidate.TryGet<VenueTileComponent>(out tile)) continue;
                    if (tile.GroupIndex != wantedGroup) continue;

                    VenueComponent venue;
                    if (!candidate.TryGet<VenueComponent>(out venue)) continue;
                    if (occupied.Contains(venue.TilePosition)) continue;

                    if (tile.RowPositionsType == eTileRowPositions.FRONT) return candidate;
                    if (fallback == null) fallback = candidate;
                }
                return fallback;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ the one native seam

        /// <summary>CHANGE_STAT goes through the resolvable ability name; see <see cref="ResolvableAbilityName"/>.</summary>
        private static void ApplyStatChangeAction(object args, Entity origin, Entity target,
            RecipeExecEnvironment env, List<Entity> party, List<(eAbilityResults, object)> results)
        {
            ApplyActionNamed(eCombatActions.CHANGE_STAT, args, origin, target, env, party, results,
                ResolvableAbilityName());
        }

        private static void ApplyAction(eCombatActions verb, object args, Entity origin, Entity target,
            RecipeExecEnvironment env, List<Entity> party, List<(eAbilityResults, object)> results, string recipeId)
        {
            // EVERY verb goes through the resolvable name, not just CHANGE_STAT. Any native path may
            // look the ability config up, and an unresolvable name is a NullReferenceException that
            // the per-action catch turns into a silent "skipped". SUMMON hit exactly this after
            // CHANGE_STAT was fixed: the recipe procced, then ADD_CHARACTER threw and no creature
            // appeared. Attribution is preserved by the verbose proc log, which names the recipe.
            ApplyActionNamed(verb, args, origin, target, env, party, results, ResolvableAbilityName());
        }

        private static void ApplyActionNamed(eCombatActions verb, object args, Entity origin, Entity target,
            RecipeExecEnvironment env, List<Entity> party, List<(eAbilityResults, object)> results, string abilityName)
        {
            // SkillContext is a primary-constructor struct (eSkills Skill, int Level, string GlobalAnimModifier);
            // ApplyAction takes both by ref, so they need real locals.
            var originSkill = new SkillContext(eSkills.NONE, 0);
            var targetSkill = new SkillContext(eSkills.NONE, 0);

            // ADD_STATUS / REMOVE_STATUS are gated on PERFECT inside ApplyAction. A recipe proc has already
            // passed every gate the engine has, so the synthetic roll is PERFECT (see class remarks).
            var roll = new RollResultData
            {
                Status = eRollStatus.PERFECT,
                Value = 1m,
                SuccessRatio = 1m,
                IsWhiff = false,
                IsDamageReaction = false
            };

            var getStat = env.GetStat ?? DefaultGetStat;
            var getTileStat = env.GetTileStat ?? DefaultGetTileStat;

            CombatHelper.ApplyAction(
                origin,                 // pOrigin
                target,                 // pTarget
                target,                 // pPrimaryTarget
                party,                  // pParty
                env.Thing,              // pThing (may be null — recipe effects are not item-sourced)
                abilityName,            // pAbilityName
                verb,                   // pAction
                ref originSkill,        // pOriginSkillContext
                ref targetSkill,        // pTargetSkillContext
                args,                   // pActionArgs
                results,                // pResults
                1m,                     // pAbilityPowerRatio — recipe effects author absolute values
                roll,                   // pRollData
                getStat,                // pGetStat
                getTileStat,            // pGetTileStat
                false,                  // pIsCrit — a recipe effect is never itself a crit
                0m,                     // pCritRatio
                true,                   // pIsCenterTarget
                env.Ctx.Env,            // pEnv
                env.Ctx.Random);        // pGameRandom — the shared stream, never an ad-hoc one
        }

        /// <summary><c>CharacterHelper.GetStat(Entity, string, eGetStatEquippedFilters)</c> — L406.</summary>
        private static readonly Func<Entity, string, eGetStatEquippedFilters, int> DefaultGetStat =
            (e, s, f) => { try { return CharacterHelper.GetStat(e, s, f); } catch { return 0; } };

        /// <summary>
        /// Tile-stat fallback. Tile stats are aura bonuses the engine supplies from <c>CombatPhase</c>; there
        /// is no static accessor for them. Returning 0 means a recipe-emitted effect inherits no tile aura —
        /// deterministic and identical on every peer, which matters more here than exactness. Hooks that have
        /// the real <c>pGetTileStat</c> parameter pass it through instead, so this only applies to effects
        /// emitted from hooks that never had one.
        /// </summary>
        private static readonly Func<Entity, string, int> DefaultGetTileStat = (e, s) => 0;

        /// <summary>
        /// The ability name handed to the native pipeline.
        ///
        /// This is NOT free-form. <c>InteractableHelper.ApplyStatChange</c> dereferences
        /// <c>GetAbilityConfig(pAbilityName).Actions</c> unconditionally, so a name that resolves to
        /// nothing makes every STAT_CHANGE effect throw. It therefore returns the registered
        /// <see cref="ConfigMergePatches.RecipeEffectAbilityId"/> for effects that reach that path,
        /// and keeps the descriptive per-recipe name only where nothing looks the config up.
        /// </summary>
        private static string AbilityName(string recipeId)
        {
            return AbilityNamePrefix + (string.IsNullOrEmpty(recipeId) ? "UNKNOWN" : recipeId);
        }

        /// <summary>The name to use where the native path will look up an ability config.</summary>
        private static string ResolvableAbilityName()
        {
            return ConfigMergePatches.RecipeEffectAbilityId;
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
