using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine;
using HarmonyLib;
using ClassForge.Recipes.Abstractions;
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

            var capture = action as CaptureAction;
            if (capture != null) { ExecCapture(capture, origin, env, results); return; }

            var banner = action as EventBannerAction;
            if (banner != null) { ExecEventBanner(banner); return; }

            var counterAdd = action as CounterAddAction;
            if (counterAdd != null && counterAdd.Persistent)
                ExecPersistCounter(counterAdd.CounterName, counterAdd.NewValue, origin);

            var counterSet = action as CounterSetAction;
            if (counterSet != null && counterSet.Persistent)
                ExecPersistCounter(counterSet.CounterName, counterSet.NewValue, origin);

            // RollStatBonusAction / HealModifierAction: handled by their owning prefixes, not here.
            // CounterAddAction / CounterSetAction / SelectionSetAction: the engine already applied the
            // in-memory (per-battle) state write; nothing further to do here except, for a Persistent
            // counter, the CustomData write-through above. Log only.
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

        // ------------------------------------------------------------------ COUNTER_ADD/COUNTER_SET "Persistent"

        /// <summary>
        /// Write-through for a <c>Persistent</c> counter (§6 amendment, <see cref="RecipeEngineHost"/> reads
        /// this back on the next combat's allocation). Reuses the native <c>CoreHelper.SetCustomData</c>
        /// idiom already used elsewhere for durable per-character mod data — no ThingConfig, no new save
        /// key, ordinary <c>CharacterComponent</c> replication. Fail-safe: an owner that vanished mid-plan
        /// (e.g. died from its own recipe's effects earlier in the same plan) just skips the write; the
        /// in-memory counter value the engine already computed is unaffected either way.
        /// </summary>
        private static void ExecPersistCounter(string counterName, int newValue, Entity origin)
        {
            try
            {
                if (origin == null || string.IsNullOrEmpty(counterName)) return;
                CharacterComponent cc;
                if (!origin.TryGet<CharacterComponent>(out cc) || cc == null) return;
                CoreHelper.SetCustomData(cc, "CF_COUNTER_" + counterName, newValue.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] Persistent counter write failed (fail-safe, not persisted this proc): " +
                    counterName + " -> " + ex);
            }
        }

        // ------------------------------------------------------------------ ADD_STATUS

        private static void ExecAddStatus(AddStatusAction a, Entity origin, RecipeExecEnvironment env,
            List<Entity> party, List<(eAbilityResults, object)> results)
        {
            var target = env.Ctx.NativeByGuid(a.TargetGuid);
            if (target == null || string.IsNullOrEmpty(a.StatusId)) return;

            ApplyAddStatusWithFallback(a, origin, target, env, party, results);

            // PRESENTATION ONLY, and strictly AFTER the status is applied (fallback included). A tile status
            // that exists in state but draws nothing is, for a class feature, a feature that does not work.
            // See RegenTileStatusVisual: no draws, no reordering, no state writes.
            if (a.TargetIsTile) RegenTileStatusVisual(a, target, env);
        }

        /// <summary>The unchanged ADD_STATUS application path, extracted verbatim so the tile visual refresh
        /// has exactly ONE place to hook and can never run before the status lands.</summary>
        private static void ApplyAddStatusWithFallback(AddStatusAction a, Entity origin, Entity target,
            RecipeExecEnvironment env, List<Entity> party, List<(eAbilityResults, object)> results)
        {
            int before = results.Count;
            ApplyStatusOnce(a.StatusId, a.Duration, origin, target, env, party, results, a.RecipeId, a.TargetIsTile);

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

            ApplyStatusOnce(a.FallbackStatusId, a.Duration, origin, target, env, party, results, a.RecipeId, a.TargetIsTile);
        }

        /// <summary>
        /// Redraws a TILE's status FX after a v1.4 <c>RANDOM_TILE</c> <c>ADD_STATUS</c> lands.
        ///
        /// <para><b>Why this exists.</b> <c>InteractableHelper.ApplyStatus</c> writes the status into the
        /// tile's <c>StatusEffectComponent</c> and stops there — it does not touch Unity at all. Every
        /// game-side tile-status site therefore refreshes the decal itself immediately afterwards, and a mod
        /// that skips it produces the exact failure this repo keeps hitting: the mechanic is fully live in
        /// state, ticks correctly, and is invisible to the player.</para>
        ///
        /// <para><b>The call shape is the game's own, verbatim.</b> The precedent is the three tile-status
        /// sites in <c>CombatPhase</c>, each of which is an <c>ApplyStatus</c> immediately followed by:
        /// <code>CharacterVisualHelper.RegenStatusVisuals(entity2, base._gameObjectMaps.FromTile[entity2]);</code>
        /// — <c>CombatPhase.cs:2014</c> (rain), <c>:2028</c> (chaos weather), <c>:2087</c> (Scourge poison
        /// hexes). Note the value is passed straight through, NOT <c>.gameObject</c>: unlike
        /// <c>FromCharacter</c> (whose values are <c>ActorGameObjectBase</c>), <c>FromTile</c> already holds
        /// <c>GameObject</c>.</para>
        ///
        /// <para><c>RegenStatusVisuals(Entity, GameObject)</c> is tile-aware by construction — it branches on
        /// <c>pEntity.Has&lt;VenueTileComponent&gt;()</c> (CharacterVisualHelper.cs:1972) to emit the
        /// <c>GroundFX</c> decal instead of a character's <c>FollowFX</c>/<c>ImbueFX</c>. There is no
        /// Entity-only overload that resolves the GameObject itself, which is why the map lookup happens
        /// here.</para>
        ///
        /// <para><b>Reaching the map.</b> The game's <c>_gameObjectMaps</c> is just
        /// <c>protected VenueGameObjectMaps _gameObjectMaps =&gt; _env.VenueGameObjectMaps;</c>
        /// (VenueDirectorBase.cs:25) — the same object as the public <c>Env.VenueGameObjectMaps</c> field we
        /// already hold on the context adapter, and <c>FromTile</c> is a public
        /// <c>Dictionary&lt;Entity, GameObject&gt;</c> field on it. No reflection, no
        /// <c>RouterHelper.Env</c> reach-through.</para>
        ///
        /// <para><b>Guarded, unlike the game.</b> All three precedent sites index <c>FromTile[...]</c> blind,
        /// as do ~90 others across <c>CombatViewHelper</c>/<c>CharacterVisualHelper</c>
        /// (docs/research/coverage/rendering.md) — which is exactly why a stale tile Entity throws
        /// <c>KeyNotFoundException</c> after a <c>CHANGE_VENUE_GRID</c> boss phase rebuilds the map with
        /// brand-new keys. ClassForge must never add one more crash site, so this uses <c>TryGetValue</c>: a
        /// missing entry is a logged no-op, never an exception. The status stays applied and keeps ticking
        /// either way — only the decal is skipped.</para>
        ///
        /// <para><b>Nothing upstream does this for us.</b> Neither <c>InteractableHelper.ApplyStatus</c> nor
        /// <c>CombatHelper.ApplyAction</c> contains any reference to <c>RegenStatusVisuals</c>.
        /// <c>ApplyStatus</c> ends by setting <c>AvatarComponent.StatusDirty = true</c>
        /// (InteractableHelper.cs:1462) — but that is the CHARACTER refresh flag, consumed by
        /// <c>RegenCharacterStatusVisuals</c>, and tiles carry no <c>AvatarComponent</c>. For a tile the
        /// dirty-flag mechanism is a no-op, so without this call the decal never appears at all.</para>
        ///
        /// <para><b>Presentation only.</b> No RNG (it never touches <c>env.Ctx.Random</c>), no state writes,
        /// no results appended, no reordering — and it runs only after the status (and any
        /// <c>FallbackStatus</c>) has been applied. It cannot affect replication.</para>
        ///
        /// <para>Logs at Debug with the <c>[ClassForge]</c> prefix, naming the tile as <c>tile(x,y)</c> to
        /// match <c>EngineAction.Describe()</c>'s convention, so the refresh is assertable from the log.</para>
        /// </summary>
        private static void RegenTileStatusVisual(AddStatusAction a, Entity tile, RecipeExecEnvironment env)
        {
            string where = "tile(" + a.TargetTileX.ToString(CultureInfo.InvariantCulture) + "," +
                           a.TargetTileY.ToString(CultureInfo.InvariantCulture) + ")";
            try
            {
                var gameEnv = env.Ctx != null ? env.Ctx.Env : null;
                var maps = gameEnv != null ? gameEnv.VenueGameObjectMaps : null;
                var fromTile = maps != null ? maps.FromTile : null;

                GameObject tileGo = null;
                if (fromTile == null || !fromTile.TryGetValue(tile, out tileGo) || tileGo == null)
                {
                    LogTileVisual("[ClassForge] " + where + " has no GameObject in VenueGameObjectMaps.FromTile" +
                                  " — status '" + a.StatusId + "' IS applied and will tick, but its decal was not " +
                                  "refreshed (recipe " + a.RecipeId + ").");
                    return;
                }

                CharacterVisualHelper.RegenStatusVisuals(tile, tileGo);
                LogTileVisual("[ClassForge] refreshed tile status visuals on " + where + " after '" +
                              a.StatusId + "' (recipe " + a.RecipeId + ").");
            }
            catch (Exception ex)
            {
                // R4 posture: a presentation failure never disturbs applied game state, and never escapes
                // into the Harmony patch body.
                LogTileVisual("[ClassForge] tile status visual refresh failed on " + where +
                              " (status '" + a.StatusId + "' is still applied): " + ex.Message);
            }
        }

        private static void LogTileVisual(string message)
        {
            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug(message);
        }

        /// <summary>
        /// Applies ONE status, choosing between the two native seams.
        ///
        /// <para><b>The direct overload is taken when a Duration override is authored</b> - <c>ApplyAction</c>'s
        /// ADD_STATUS path calls <c>ApplyStatus</c> WITHOUT <c>pDurationOverride</c> (CombatHelper.cs:1993),
        /// so a duration can only be expressed through the single-target overload (InteractableHelper.cs:1222,
        /// <c>int? pDurationOverride = null</c>). This is still a native verb - it is the exact call
        /// <c>ApplyAction</c> itself makes one line deeper.</para>
        ///
        /// <para><b>...and ALWAYS when the target is a TILE</b> (v1.4 <c>RANDOM_TILE</c>), Duration or not.
        /// To be clear about WHY, because it is not a safety fix: <c>ApplyAction</c> was traced and it IS
        /// tile-safe. Every character-specific read in its ADD_STATUS case is behind a
        /// <c>Has&lt;CharacterComponent&gt;()</c>/<c>TryGet</c> guard, and
        /// <c>CharacterHelper.TryHasImmunity</c> is itself tile-aware (CharacterHelper.cs:1204 branches on
        /// <c>Has&lt;VenueTileComponent&gt;()</c>), so a bare tile falls through to the <c>default:</c> case
        /// and applies cleanly. The reason for going direct is CONSISTENCY: routing on whether the author
        /// happened to write a <c>Duration</c> would fork tile behaviour between two seams that do not do
        /// the same thing (see TileSync below). One seam, always.</para>
        ///
        /// <para><b>And the seam chosen is the game's own tile seam.</b> All four game-side tile-status
        /// sites use THIS overload, not <c>ApplyAction</c>: <c>CombatPhase.cs:2013</c> (rain),
        /// <c>:2027</c> (chaos weather), <c>:2081</c>/<c>:2085</c> (Scourge poison hexes), each shaped
        /// <c>InteractableHelper.ApplyStatus(null, tileEntity, null, "", "STATUS_WATER_00", _combatState.Random, null)</c>
        /// - i.e. this overload with <c>pDurationOverride</c> null and
        /// <c>pTierStatus</c>/<c>pRenderStatusPopcorn</c> at their <c>true</c> defaults, which is exactly the
        /// call below. A null <c>pDurationOverride</c> is therefore not a fallback here; it is the game's own
        /// argument for a tile.</para>
        ///
        /// <para><b>Deliberately NOT inherited from ApplyAction: TileSync.</b> <c>ApplyAction</c>'s tile path
        /// has an extra step (CombatHelper.cs:1994) - when the status config carries <c>TileSync</c> and a
        /// character is standing on the tile, it applies the status to THAT CHARACTER as well. Going direct
        /// skips that, which is precisely what the game's own environmental tile statuses do. It is the
        /// narrower reading of "put a status on a tile" and the one Ben asked for; a tile hazard that also
        /// silently statuses its occupant is a bigger gameplay claim, and should be an explicit authored
        /// option if it is ever wanted rather than a side effect of which seam we picked.</para>
        ///
        /// <para>The only other difference from the game's tile call is that a non-null <c>pResults</c> is
        /// passed, which is what lets IMMUNITY_FALLBACK observe whether the status actually landed.</para>
        /// </summary>
        private static void ApplyStatusOnce(string statusId, int? duration, Entity origin, Entity target,
            RecipeExecEnvironment env, List<Entity> party, List<(eAbilityResults, object)> results,
            string recipeId, bool targetIsTile)
        {
            if (duration.HasValue || targetIsTile)
            {
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
            if (target == null) return;

            // ---- v1.5 dynamic config (CharacterConfigFrom) ----
            // Resolved HERE and nowhere earlier: the engine plans in pure C# and never sees a game Entity,
            // so an item's Thing.CustomData is unreadable to it. Every failure is a logged no-op -- the
            // same fail-safe contract SELF_LEVEL / HAS_ITEM / RANDOM_TILE carry.
            string configName = a.CharacterConfig;
            if (!string.IsNullOrEmpty(a.CharacterConfigFrom))
            {
                configName = ResolveConfigFromItem(a, origin);
                if (string.IsNullOrEmpty(configName)) return;
            }
            if (string.IsNullOrEmpty(configName)) return;

            // ---- Pokemon Trainer partner persistence (test-checklist L0) ----
            // Null for every summon that is NOT a Trainer partner -- any other class's SUMMON falls
            // straight through to the unchanged path below. See TrainerPartnerPersistence.ResolveSlot
            // for why the ball item in the summoner's own inventory is the whole discriminator.
            var partnerSlot = TrainerPartnerPersistence.ResolveSlot(origin, a.RecipeId);
            if (partnerSlot != null && partnerSlot.Downed)
            {
                // A DOWNED partner is not lost -- it is simply not sent out. Nothing is deleted, and
                // the ball keeps its record until a town revives it.
                TrainerPartnerPersistence.LogDownedSkip(partnerSlot, configName);
                return;
            }

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
                            "tile, so " + configName + " could not be placed.");
                    return;
                }
                target = tile;
            }

            // OQ#4: AddCharacterAction is { eSummonTypes Type; string Value; } with NO count field, and
            // TryCreateSummon returns a single `out Entity`. The engine has already expanded the authored
            // Count into N sequential SummonActions (Index 0..Count-1 ascending), each of which becomes its
            // own ApplyAction call with its own freshly-deserialized payload and its own placement draws.
            string json = "{\"Type\":" + JsonString(a.SummonType.ToString()) +
                          ",\"Value\":" + JsonString(configName) + "}";
            // Note which combatants exist before, so the new one can be found and DRAWN afterwards.
            var before = SnapshotCombatRoster();

            using (var doc = JsonDocument.Parse(json))
            {
                ApplyAction(eCombatActions.ADD_CHARACTER, doc.RootElement, origin, target, env, party, results, a.RecipeId);
            }

            var spawned = FindNewSummon(before);

            // Bind BEFORE drawing: the creature's max HP is re-based here, and DrawNewSummon's
            // placement/animation work has nothing to do with its health. A drawing failure must not
            // cost the partner its carried-over HP.
            if (partnerSlot != null && spawned != null)
                TrainerPartnerPersistence.RegisterSummon(spawned, partnerSlot, configName);

            DrawNewSummon(spawned, a.RecipeId, configName);
        }

        /// <summary>
        /// v1.5 <c>SUMMON.CharacterConfigFrom</c>: resolve the creature id at EXECUTION time out of an
        /// item's <c>Thing.CustomData</c>. Token shape
        /// <c>ITEM_CUSTOM_DATA:&lt;ThingConfigId&gt;:&lt;Key&gt;</c> (validated for shape at load).
        ///
        /// <para>Returns null — a LOGGED NO-OP, never a throw and never a default creature — for every
        /// unresolvable case: no owner entity, the owner is not carrying that item, the item has no such
        /// CustomData key, the stored value is empty, the id is not a live <c>Configs.Characters</c> key, or
        /// the id no longer passes the capture gate.</para>
        ///
        /// <para><b>Why the last two matter.</b> A ball's record rides the save file, and a save can outlive
        /// the content it names — a removed mod, an edited pack, a game patch. Handing a stale id onward
        /// reaches <c>CharacterHelper</c>'s raw indexer <c>Env.Configs.Characters[name].Things</c>
        /// (CharacterHelper.cs:1913), which throws <c>KeyNotFoundException</c>; the per-action catch in
        /// <see cref="Execute"/> would turn that into a LogError on EVERY combat start for the rest of the
        /// run. Re-running the eligibility gate on the way OUT is the same argument applied to art and to
        /// the no-stun rule: whatever the record says, only a creature that is still capturable today is
        /// still summonable today.</para>
        /// </summary>
        private static string ResolveConfigFromItem(SummonAction a, Entity origin)
        {
            string token = a.CharacterConfigFrom;
            var parts = token.Split(':');
            if (parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] SUMMON for " + a.RecipeId + ": CharacterConfigFrom '" + token +
                    "' is not ITEM_CUSTOM_DATA:<ThingConfigId>:<Key> — no-op.");
                return null;
            }
            string itemConfig = parts[1], key = parts[2];

            if (origin == null)
            {
                LogSummonSkip(a, "the owner entity could not be resolved");
                return null;
            }

            var thing = TrainerCaptureRules.CarriedItem(origin, itemConfig);
            if (thing == null)
            {
                LogSummonSkip(a, "the owner is not carrying " + itemConfig);
                return null;
            }

            string stored = TrainerCaptureRules.ReadCustomData(thing, key);
            if (string.IsNullOrEmpty(stored))
            {
                LogSummonSkip(a, itemConfig + " has no " + key + " record yet (nothing captured)");
                return null;
            }

            // GUARDED lookup. Never Env.Configs.Characters[stored].
            if (GameLookups.CharacterConfig(stored) == null)
            {
                LogSummonSkip(a, "'" + stored + "' is not a live CharacterConfig (a stale record on " +
                                 itemConfig + ")");
                return null;
            }

            string reason;
            if (!TrainerCaptureRules.ConfigIsCapturable(stored, null, out reason))
            {
                LogSummonSkip(a, "'" + stored + "' no longer passes the capture gate: " + reason);
                return null;
            }

            return stored;
        }

        private static void LogSummonSkip(SummonAction a, string why)
        {
            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] SUMMON for " + a.RecipeId + " resolved no creature — " + why + " (no-op).");
        }

        // ------------------------------------------------------------------ CAPTURE (v1.5)

        /// <summary>
        /// <c>CAPTURE</c>: bind the target's character config to the owner's capture item, then take the
        /// target off the board.
        ///
        /// <para><b>Order matters.</b> The record is written FIRST and the removal pushed SECOND. The
        /// removal is not performed here at all — it is a request that <c>CombatPhase</c> honours later,
        /// after this Harmony postfix has returned — so if the write failed there would be nothing to undo,
        /// and if the write succeeds the record stands even if the removal branch is never reached.</para>
        ///
        /// <para><b>Removal is a results-list push, not a kill.</b> <c>CharacterHelper.KillCharacter</c>
        /// leaves a corpse standing on its tile; it is not a removal. The engine's real removal is
        /// <c>CombatPhase._processCombatResults</c>' LOCAL function <c>removeFromCombat</c>
        /// (CombatPhase.cs:4362-4398) — unreachable from mod code by design. Pushing
        /// <c>(eAbilityResults.PLAYTHINGED, entity)</c> into the ability's results list is the only route to
        /// it (CombatPhase.cs:4329-4338), and it is the RIGHT route because that branch also calls
        /// <c>_checkChargeRetargets()</c> (CombatPhase.cs:4401): a hand-rolled removal would leave charged
        /// abilities aimed at an entity that is no longer on the board.</para>
        ///
        /// <para><b>The results list has to be the ability's own.</b> <c>CombatPhase._performAbility</c> does
        /// <c>pResults.AddRange(CombatHelper.PerformAbility(...))</c> (CombatPhase.cs:4025) and hands the
        /// result to <c>_processCombatResults</c> (CombatPhase.cs:4054). A Harmony postfix on
        /// <c>PerformAbility</c> runs BEFORE that <c>AddRange</c> copies the list, so an entry appended to
        /// <c>__result</c> is carried through. Under a trigger whose hook has no results list
        /// (<c>env.Results == null</c>) nothing can be removed, and the capture is refused rather than
        /// half-done.</para>
        ///
        /// <para><b>Accepted side effect.</b> That same branch adds a <c>DOLL_&lt;TYPE&gt;_01</c> Thing to the
        /// fight's loot (<c>_additionalDrops</c>). It is the game's own behaviour on this path, it is not
        /// suppressible from a mod, and it is harmless-to-pleasant: catching a monster also yields a doll of
        /// its family. All five <c>DOLL_*_01</c> ids exist in shipped <c>Things/Items.json</c>, verified
        /// 2026-08-25.</para>
        /// </summary>
        private static void ExecCapture(CaptureAction a, Entity origin, RecipeExecEnvironment env,
            List<(eAbilityResults, object)> results)
        {
            if (!TrainerCaptureRules.Active)
            {
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] CAPTURE for " + a.RecipeId + " is disabled by config — no-op.");
                return;
            }

            var target = env.Ctx.NativeByGuid(a.TargetGuid);
            if (target == null || origin == null) return;

            if (env.Results == null)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] CAPTURE for " + a.RecipeId + " fired under a hook with no results list, " +
                    "so the target could not be removed from combat. Refused rather than storing a monster " +
                    "that is still fighting you.");
                return;
            }

            // The ball you THREW is the ball it goes into. Without this, any ability from any item would
            // capture for a character who merely happens to be carrying a ball -- Gary's own staff shares
            // the vanilla ONLY_RESISTDOWN_ATTACK id with the ball, so this is not hypothetical. Same shape
            // as TrainerFocusFire.Issue's gate 1 (TrainerFocusFire.cs:131).
            if (env.Thing == null
                || !string.Equals(env.Thing.ConfigName, a.IntoItem, StringComparison.Ordinal))
            {
                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] CAPTURE for " + a.RecipeId + ": the acting item is " +
                        (env.Thing != null ? env.Thing.ConfigName : "none") + ", not " + a.IntoItem +
                        " -- only the capture item itself captures. No-op.");
                return;
            }

            var ball = TrainerCaptureRules.CarriedItem(origin, a.IntoItem);
            if (ball == null)
            {
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] CAPTURE for " + a.RecipeId + ": the thrower is not carrying " +
                    a.IntoItem + " — no-op.");
                return;
            }

            string reason;
            if (!TrainerCaptureRules.CanCapture(target, out reason))
            {
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] CAPTURE for " + a.RecipeId + " refused: " + reason + ".");
                return;
            }

            CharacterComponent cc;
            if (!target.TryGet<CharacterComponent>(out cc) || cc == null) return;
            string captured = cc.ConfigName;

            // ONE monster per ball. Overwriting the config id and CLEARING the health record is the whole
            // "capturing again replaces it" rule: TrainerPartnerPersistence.ResolveSlot reads HasRecord off
            // CF_POKE_HP, so a cleared record makes the next send-out a first summon at FULL health. That
            // also means a fresh capture is never born wounded or DOWNED because the previous occupant was.
            CoreHelper.SetCustomData(ball, a.IntoKey, captured);
            CoreHelper.SetCustomData(ball, TrainerPartnerPersistence.KeyHp, "");
            CoreHelper.SetCustomData(ball, TrainerPartnerPersistence.KeyMaxHp, "");
            CoreHelper.SetCustomData(ball, TrainerPartnerPersistence.KeyDowned, "0");

            results.Add((eAbilityResults.PLAYTHINGED, target));

            ClassForgePlugin.Log.LogDebug(
                "[ClassForge] CAPTURED " + captured + " into " + a.IntoItem + " (" + a.RecipeId +
                "). It leaves this fight and is sent out at the start of the next one.");
        }

        /// <summary>The combatant that appeared since <paramref name="before"/> was taken, or null.</summary>
        private static Entity FindNewSummon(HashSet<Entity> before)
        {
            try
            {
                var entities = RouterHelper.Env?.GameRun?.CombatState?.Entities;
                if (entities == null) return null;
                Entity spawned = null;
                foreach (var e in entities) if (e != null && !before.Contains(e)) spawned = e;
                return spawned;
            }
            catch (Exception) { return null; }
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

        private static void DrawNewSummon(Entity spawned, string recipeId, string characterConfig)
        {
            try
            {
                if (spawned == null) return;

                // Shared with the modelless sweep so a summon has exactly one drawing path.
                // A failure here is NOT fatal and is not even unusual: a summon raised on
                // ON_COMBAT_START predates the combat canvas, so this cannot succeed yet and
                // SummonLeakPatches retries once the canvas is up.
                bool drawn = SummonVisuals.TryBuildActor(spawned);

                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] summon " + characterConfig + " for " + recipeId
                        + (drawn ? " drawn." : " not drawn yet (combat canvas not up); will be retried."));
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

                // Collect ALL free tiles of the group, then pick by the shared total order rather than
                // "first FRONT tile the scan happens to reach".
                //
                // Correcting an earlier claim: this loop walks CombatState.Entities, which is a List
                // iterated by index -- it is list-ordered, NOT dictionary-ordered, so it was never
                // reference-hash unstable. But CombatState.Entities' order is precisely the invariant the
                // rest of this work exists to protect, and it must not ALSO be load-bearing here: if
                // ANOTHER change perturbs that list, a scan-order pick silently moves the summon, which
                // moves GetTargetableTiles' Count, which moves the shared ShuffleList draw count on the
                // next AI turn (AIHelper.cs:507-511, GameRandom.cs:227-241).
                //
                // TileOrder.SelectPlacement is a single MIN scan -- provably independent of the order the
                // candidates were collected in -- and keeps the FRONT preference as its first key so the
                // placement behaviour is unchanged for every board where the old code was already stable.
                var candidateTiles = new List<Entity>();
                var candidateKeys = new List<TileKey>();
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

                    candidateTiles.Add(candidate);
                    candidateKeys.Add(VenueTileOrder.KeyOf(candidate));
                }

                int pick = TileOrder.SelectPlacement(candidateKeys, VenueTileOrder.FrontRow);
                return pick < 0 ? null : candidateTiles[pick];
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
