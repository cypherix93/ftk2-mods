using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

            // RollStatBonusAction / HealModifierAction: handled by their owning prefixes, not here.
            // CounterAddAction / CounterSetAction: engine already applied the state write; log only.
            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug("[ClassForge] (no native call) " + action.Describe());
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
                InteractableHelper.ApplyStatus(origin, target, env.Thing, AbilityName(recipeId), statusId,
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
                ApplyAction(eCombatActions.CHANGE_STAT, doc.RootElement, origin, target, env, party, results, a.RecipeId);
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

            // OQ#4: AddCharacterAction is { eSummonTypes Type; string Value; } with NO count field, and
            // TryCreateSummon returns a single `out Entity`. The engine has already expanded the authored
            // Count into N sequential SummonActions (Index 0..Count-1 ascending), each of which becomes its
            // own ApplyAction call with its own freshly-deserialized payload and its own placement draws.
            string json = "{\"Type\":" + JsonString(a.SummonType.ToString()) +
                          ",\"Value\":" + JsonString(a.CharacterConfig) + "}";
            using (var doc = JsonDocument.Parse(json))
            {
                ApplyAction(eCombatActions.ADD_CHARACTER, doc.RootElement, origin, target, env, party, results, a.RecipeId);
            }
        }

        // ------------------------------------------------------------------ the one native seam

        private static void ApplyAction(eCombatActions verb, object args, Entity origin, Entity target,
            RecipeExecEnvironment env, List<Entity> party, List<(eAbilityResults, object)> results, string recipeId)
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
                AbilityName(recipeId),  // pAbilityName
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

        private static string AbilityName(string recipeId)
        {
            return AbilityNamePrefix + (string.IsNullOrEmpty(recipeId) ? "UNKNOWN" : recipeId);
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
