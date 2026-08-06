using System;
using System.Collections.Generic;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Plugin
{
    /// <summary>
    /// State the <c>ApplyStatChange</c> prefix hands its postfix (Harmony <c>__state</c>).
    /// Public because it appears in the signature of a public patch method.
    /// </summary>
    public sealed class StatChangeCapture
    {
        internal bool Valid;
        internal int HpBefore;
        internal Entity Target;
        internal Entity Origin;

        /// <summary>The healer slot's value on entry, restored by the postfix. Exact stack discipline, so a
        /// nested <c>ApplyStatChange</c> (link statuses re-enter it) cannot clobber the outer call's healer.</summary>
        internal Entity PrevHealOrigin;
    }

    /// <summary>
    /// Every combat trigger hook in SPEC-DELTA-v1.1 §2. One patch method per game method; each dispatches
    /// the matching engine trigger and executes the returned plan.
    ///
    /// <para><b>No per-client suppression anywhere (§5.2 invariant 6).</b> Four prefixes exist and
    /// <b>none</b> of them returns <c>false</c> — all are declared <c>void</c>, so it is structurally
    /// impossible for them to skip their original method:</para>
    /// <list type="number">
    /// <item><see cref="PerformAbility_Prefix"/> — replaces the <c>pGetStat</c> delegate argument for the
    /// duration of one call (T2 / E1 <c>ROLL_STAT_BONUS</c>).</item>
    /// <item><see cref="ApplyStatChange_Prefix"/> — writes <c>__state</c> only (ON_KILL / ON_DAMAGE_* capture).</item>
    /// <item><see cref="AddHealth_Prefix"/> — mutates <c>ref int pValue</c> (T8 / E2 <c>HEAL_MODIFIER</c>).</item>
    /// <item><see cref="PerformSkillAbilityProcs_Prefix"/> — <b>observe-only</b>, reads its two parameters and
    /// writes nothing (ON_TURN_START / ON_TURN_END). §5.2 invariant 6 says "exactly three prefix hooks";
    /// this is a fourth, and it is a pure observer, because the delta's named turn hook
    /// (<c>CombatHelper._onCombatSkillProc</c>) turned out not to carry the turn phase at all — see the
    /// remarks on that method.</item>
    /// </list>
    /// </summary>
    public static class CombatHookPatches
    {
        // The healer identity for HEAL_MODIFIER Scope: GIVEN. ApplyStatChange's HP path funnels into
        // CharacterHelper.AddHealth, so the AddHealth prefix reads the origin the ApplyStatChange prefix
        // captured one frame up the stack. Saved/restored through __state rather than depth-counted, so
        // nesting is exact and a prefix that threw before setting it cannot corrupt an outer call.
        //
        // MP review M4: the restore used to live at the top of ApplyStatChange_Postfix, which Harmony never
        // runs when the ORIGINAL method throws -- so a throwing InteractableHelper.ApplyStatChange would pin
        // this to a stale Entity for the rest of the process (across combats, across the whole run). The
        // restore now lives exclusively in ApplyStatChange_Finalizer, a Harmony finalizer, which Harmony
        // guarantees runs after Prefix -> Original -> Postfix regardless of what threw. It is ALSO cleared on
        // every combat-end reset (RecipeEngineHost.ResetCombat -> ClearHealOrigin) as a belt-and-braces
        // second line of defense, so even a wholly missed ApplyStatChange call cannot leak this across a
        // combat boundary.
        private static Entity _healOrigin;

        private static bool _warnedTurnHookMissing;

        // =====================================================================================
        // T1 ON_COMBAT_START — CombatHelper.SetInitiative Postfix (CombatHelper.cs L43)
        // =====================================================================================

        /// <summary>
        /// <c>public static void SetInitiative(Entity pEntity, List&lt;Entity&gt; pAllies,
        /// CombatState pCombatState, GameRunData pGameRun, List&lt;(eAbilityResults, object)&gt; pResults,
        /// bool pTrySkillProc = false)</c>. Owner = <c>pEntity</c>. Needed by PREPARED, OF_FOCUS.
        /// <para><b>Encounter Modifiers spec §4.3 engine fix:</b> the 6th parameter, <c>pTrySkillProc</c>, is
        /// now captured and carried on <see cref="CombatStartEvent.TrySkillProc"/> through to the
        /// <c>COMBAT_START_REAL</c> condition and the dispatcher. EOR gates its own encounter-modifier
        /// anchor on this being <c>true</c> (L22840) — summon/revive/boss-phase re-initializations pass
        /// <c>false</c>, so a recipe conditioned on <c>COMBAT_START_REAL</c> correctly never re-fires for them.</para>
        /// </summary>
        public static void SetInitiative_Postfix(Entity pEntity, List<Entity> pAllies,
            List<(eAbilityResults, object)> pResults, bool pTrySkillProc = false)
        {
            try
            {
                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;
                var owner = ctx.Wrap(pEntity);
                if (owner == null) return;

                var plan = d.OnCombatStart(ctx, new CombatStartEvent { Entity = owner, TrySkillProc = pTrySkillProc });
                RecipeActionExecutor.Execute(plan, new RecipeExecEnvironment
                {
                    Ctx = ctx, Party = pAllies, Results = pResults
                });
            }
            catch (Exception ex) { Fail("SetInitiative", ex); }
        }

        // =====================================================================================
        // T2 ON_ABILITY_DECLARED + E1 ROLL_STAT_BONUS — CombatHelper.PerformAbility Prefix (L1155)
        // =====================================================================================

        /// <summary>
        /// The <c>ROLL_STAT_BONUS</c> insertion point. <c>PerformAbility</c> receives
        /// <c>Func&lt;Entity, string, eGetStatEquippedFilters, int&gt; pGetStat</c> as a plain <b>parameter</b>,
        /// so replacing it with a wrapper via <c>ref</c> changes only the stat values read during this one
        /// invocation — no status, no persistent state, no component write, nothing to revert (the wrapper's
        /// lifetime IS the call). <c>pRollData</c> is already resolved at prefix time, which is what makes
        /// <c>ROLL_TIER</c> readable here.
        /// <para>Always <c>void</c> — the original always runs.</para>
        /// <para>Ability id is <c>pCombatDecision.Ability.AbilityName</c> (<c>CombatDecisionData.Ability</c> is
        /// an <c>AbilityAction</c> struct; there is no <c>pCombatDecision.AbilityName</c>). Note the engine may
        /// rewrite that name later in the method for <c>RANDOM_ABILITY:</c> configs, so the prefix sees the
        /// declared name and the postfix sees the resolved one — which is the correct split for
        /// declare-time vs resolve-time recipe halves.</para>
        /// </summary>
        public static void PerformAbility_Prefix(Entity pOrigin, Entity pTarget, List<Entity> pParty, Thing pThing,
            CombatDecisionData pCombatDecision, RollResultData pRollData,
            ref Func<Entity, string, eGetStatEquippedFilters, int> pGetStat,
            ref Func<Entity, string, int> pGetTileStat)
        {
            try
            {
                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var origin = ctx.Wrap(pOrigin);
                if (origin == null) return;
                ctx.ActingThing = pThing;

                var plan = d.OnAbilityDeclared(ctx, new AbilityDeclaredEvent
                {
                    Origin = origin,
                    Target = ctx.Wrap(pTarget),
                    AbilityId = AbilityIdOf(pCombatDecision),
                    RollTier = RecipeEngineHost.MapRollTier(pRollData.Status),
                    FocusUsed = pCombatDecision != null ? pCombatDecision.FocusUsed : 0
                });
                if (plan == null || plan.Count == 0) return;

                // Split: RollStatBonus is consumed by the delegate wrapper, everything else rides ApplyAction.
                List<RollStatBonusAction> bonuses = null;
                List<EngineAction> rest = null;
                for (int i = 0; i < plan.Count; i++)
                {
                    var bonus = plan[i] as RollStatBonusAction;
                    if (bonus != null)
                    {
                        if (bonuses == null) bonuses = new List<RollStatBonusAction>();
                        bonuses.Add(bonus);
                    }
                    else
                    {
                        if (rest == null) rest = new List<EngineAction>();
                        rest.Add(plan[i]);
                    }
                }

                if (bonuses != null)
                    pGetStat = BuildStatWrapper(pGetStat, bonuses, ctx);

                if (rest != null)
                {
                    RecipeActionExecutor.Execute(rest, new RecipeExecEnvironment
                    {
                        Ctx = ctx, Party = pParty, Thing = pThing,
                        GetStat = pGetStat, GetTileStat = pGetTileStat
                    });
                }
            }
            catch (Exception ex) { Fail("PerformAbility(prefix)", ex); }
        }

        /// <summary>
        /// Wraps <c>pGetStat</c> so <c>(entity == bonus target &amp;&amp; statKey == bonus Stat)</c> reads a
        /// boosted value for this one <c>PerformAbility</c> call. All arithmetic is <c>int</c>/<c>decimal</c>
        /// (never <c>float</c>/<c>double</c>) so two peers compute a byte-identical result
        /// (docs/MULTIPLAYER.md R2). No RNG on this path.
        /// </summary>
        private static Func<Entity, string, eGetStatEquippedFilters, int> BuildStatWrapper(
            Func<Entity, string, eGetStatEquippedFilters, int> inner,
            List<RollStatBonusAction> bonuses, CombatContextAdapter ctx)
        {
            return (entity, statKey, filter) =>
            {
                int baseValue;
                try { baseValue = inner != null ? inner(entity, statKey, filter) : CharacterHelper.GetStat(entity, statKey, filter); }
                catch { return 0; }

                if (entity == null || string.IsNullOrEmpty(statKey)) return baseValue;

                string guid;
                try { guid = entity.Guid ?? ""; } catch { return baseValue; }

                int total = 0;
                for (int i = 0; i < bonuses.Count; i++)
                {
                    var b = bonuses[i];
                    if (!string.Equals(b.TargetGuid, guid, StringComparison.Ordinal)) continue;
                    if (!string.Equals(b.Stat, statKey, StringComparison.Ordinal)) continue;

                    int delta = b.FlatDelta;
                    if (b.PercentDelta != 0)
                        delta += (int)Math.Floor((decimal)baseValue * b.PercentDelta / 100m);
                    if (b.MinDelta.HasValue && delta < b.MinDelta.Value)
                        delta = b.MinDelta.Value;
                    total += delta;
                }
                return baseValue + total;
            };
        }

        // =====================================================================================
        // ON_ABILITY_USED + T7 ON_ENEMY_ABILITY_RESOLVED — CombatHelper.PerformAbility Postfix
        // =====================================================================================

        /// <summary>
        /// Both resolve-time ability triggers ride the same postfix.
        /// <c>ON_ENEMY_ABILITY_RESOLVED</c>'s owner set (every living recipe-holder opposed to
        /// <c>pOrigin</c>, ascending ordinal <c>Entity.Guid</c>) is computed inside the dispatcher, so the
        /// draw sequence stays a pure function of (entity set, recipe book) — §2 T7 determinism note.
        /// </summary>
        public static void PerformAbility_Postfix(Entity pOrigin, Entity pTarget, List<Entity> pParty, Thing pThing,
            CombatDecisionData pCombatDecision, RollResultData pRollData,
            Func<Entity, string, eGetStatEquippedFilters, int> pGetStat,
            Func<Entity, string, int> pGetTileStat,
            List<(eAbilityResults, object)> __result)
        {
            try
            {
                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var origin = ctx.Wrap(pOrigin);
                if (origin == null) return;
                ctx.ActingThing = pThing;

                string abilityId = AbilityIdOf(pCombatDecision);
                var tier = RecipeEngineHost.MapRollTier(pRollData.Status);
                var target = ctx.Wrap(pTarget);
                var exec = new RecipeExecEnvironment
                {
                    Ctx = ctx, Party = pParty, Thing = pThing, Results = __result,
                    GetStat = pGetStat, GetTileStat = pGetTileStat
                };

                var used = d.OnAbilityUsed(ctx, new AbilityUsedEvent
                {
                    Origin = origin, Target = target, AbilityId = abilityId, RollTier = tier,
                    FocusUsed = pCombatDecision != null ? pCombatDecision.FocusUsed : 0
                });
                RecipeActionExecutor.Execute(used, exec);

                var resolved = d.OnEnemyAbilityResolved(ctx, new EnemyAbilityResolvedEvent
                {
                    Origin = origin, Target = target, AbilityId = abilityId, RollTier = tier
                });
                RecipeActionExecutor.Execute(resolved, exec);
            }
            catch (Exception ex) { Fail("PerformAbility(postfix)", ex); }
        }

        // =====================================================================================
        // ON_CRIT / ON_KILL / T3 ON_DAMAGE_DEALT / T4 ON_DAMAGE_TAKEN / ON_HEAL
        // InteractableHelper.ApplyStatChange Prefix(capture) + Postfix (InteractableHelper.cs L626)
        // =====================================================================================

        /// <summary>
        /// Capture-only prefix: records the target's HP so the postfix can classify the delta as damage or
        /// heal and detect a kill (<c>HpBefore &gt; 0 &amp;&amp; HpAfter &lt;= 0</c>). Writes <c>__state</c>
        /// and one static healer slot; <b>never</b> returns false, never touches the arguments.
        /// </summary>
        public static void ApplyStatChange_Prefix(ChangeStatAction pStatAction, Entity pOriginEntity,
            Entity pTargetEntity, out StatChangeCapture __state)
        {
            __state = new StatChangeCapture();
            try
            {
                __state.Origin = pOriginEntity;
                __state.Target = pTargetEntity;
                __state.HpBefore = CurrentHp(pTargetEntity);
                __state.Valid = pTargetEntity != null && pStatAction != null;

                // HEAL_MODIFIER Scope: GIVEN needs the healer identity, and CharacterHelper.AddHealth
                // (which this method funnels HP changes into) does not carry one.
                __state.PrevHealOrigin = _healOrigin;
                _healOrigin = pOriginEntity;
            }
            catch { /* capture is best-effort; the postfix degrades to "no event" */ }
        }

        public static void ApplyStatChange_Postfix(string pAbilityName, ChangeStatAction pStatAction,
            Entity pOriginEntity, Entity pTargetEntity, List<Entity> pParty, Thing pThing,
            Func<Entity, string, int> pGetTileStat, bool pIsCrit,
            List<(eAbilityResults, object)> pResults, StatChangeCapture __state)
        {
            try
            {
                // NOTE (MP review M4): the healer-slot restore does NOT happen here anymore -- a postfix never
                // runs when the original throws, so the restore now lives exclusively in
                // ApplyStatChange_Finalizer, which Harmony guarantees runs regardless of exceptions.
                if (__state == null || !__state.Valid) return;
                if (pStatAction == null || !string.Equals(pStatAction.Stat, "HP", StringComparison.Ordinal)) return;

                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var origin = ctx.Wrap(pOriginEntity);
                var target = ctx.Wrap(pTargetEntity);
                if (target == null) return;

                int hpAfter = CurrentHp(pTargetEntity);
                int delta = __state.HpBefore - hpAfter;   // >0 damage, <0 heal
                var exec = new RecipeExecEnvironment
                {
                    Ctx = ctx, Party = pParty, Thing = pThing, Results = pResults, GetTileStat = pGetTileStat
                };

                // ON_CRIT — pIsCrit is an explicit bool parameter (OQ#2). Damage only: crit inflation only
                // happens on the HP-damage path, and CalculateFinalDamage carries no crit information at all.
                if (pIsCrit && delta > 0 && origin != null)
                {
                    RecipeActionExecutor.Execute(d.OnCrit(ctx, new CritEvent
                    {
                        Origin = origin, Target = target, AbilityId = pAbilityName, Stat = pStatAction.Stat
                    }), exec);
                }

                if (delta > 0)
                {
                    // ON_DAMAGE_DEALT (owner = origin) and ON_DAMAGE_TAKEN (owner = victim) are two distinct
                    // triggers off the same event, with opposite owner binding.
                    if (origin != null)
                    {
                        RecipeActionExecutor.Execute(d.OnDamageDealt(ctx, new DamageDealtEvent
                        {
                            Origin = origin, Target = target, AbilityId = pAbilityName,
                            Amount = delta, HpBefore = __state.HpBefore, HpAfter = hpAfter
                        }), exec);
                    }

                    RecipeActionExecutor.Execute(d.OnDamageTaken(ctx, new DamageTakenEvent
                    {
                        Attacker = origin, Victim = target, AbilityId = pAbilityName, Amount = delta
                    }), exec);

                    // ON_KILL — origin-attributed (unlike EOR's unfiltered HasKilledTarget).
                    if (__state.HpBefore > 0 && hpAfter <= 0 && origin != null)
                    {
                        RecipeActionExecutor.Execute(d.OnKill(ctx, new KillEvent
                        {
                            Origin = origin, Target = target, AbilityId = pAbilityName,
                            HpBefore = __state.HpBefore, HpAfter = hpAfter
                        }), exec);
                    }
                }
                else if (delta < 0 && origin != null)
                {
                    // ON_HEAL — owner = healer, healed entity binds to TRIGGER_TARGET.
                    RecipeActionExecutor.Execute(d.OnHeal(ctx, new HealEvent
                    {
                        Healer = origin, Healed = target, AbilityId = pAbilityName, Amount = -delta
                    }), exec);
                }
            }
            catch (Exception ex) { Fail("ApplyStatChange(postfix)", ex); }
        }

        /// <summary>
        /// Harmony FINALIZER for <c>InteractableHelper.ApplyStatChange</c> (MP review M4). Finalizers run
        /// after Prefix -&gt; Original -&gt; Postfix no matter what threw, so this is the one place the healer
        /// slot's restore can live and be genuinely exception-safe: a <c>try/finally</c> inside the prefix
        /// cannot see the original throwing (the prefix has already returned by then), which is why the
        /// restore was moved out of the postfix (never runs on a throwing original) entirely into here.
        /// Never swallows or replaces the exception (void return) — it only cleans up state and lets whatever
        /// happened propagate exactly as it would without this patch.
        /// </summary>
        public static void ApplyStatChange_Finalizer(StatChangeCapture __state)
        {
            try
            {
                if (__state != null) _healOrigin = __state.PrevHealOrigin;
            }
            catch
            {
                // If even reading __state throws, do not leave the slot pinned to a stale entity: clearing it
                // is strictly safer than leaving a stale reference that could survive across combats.
                _healOrigin = null;
            }
        }

        /// <summary>
        /// Belt-and-braces second line of defense for <see cref="_healOrigin"/> (MP review M4): called from
        /// <see cref="RecipeEngineHost.ResetCombat"/> on every combat-key change, so even an
        /// <c>ApplyStatChange</c> call this hook never observed at all (not just one that threw) cannot leave
        /// a stale healer identity readable in the next combat.
        /// </summary>
        internal static void ClearHealOrigin()
        {
            _healOrigin = null;
        }

        // =====================================================================================
        // T8 ON_HEAL_PENDING + E2 HEAL_MODIFIER — CharacterHelper.AddHealth Prefix (L1357)
        // =====================================================================================

        /// <summary>
        /// <c>public static int AddHealth(Entity pEntity, ref int pValue, bool pNonLethal = false,
        /// List&lt;(eAbilityResults, object)&gt; pResults = null, StatChangedResultsData pLinkStatusChangeData = null,
        /// bool pAddLinkAbilityResult = false)</c> — the terminal implementation the other three overloads all
        /// funnel into, so patching it alone catches every heal.
        /// <para><c>ref int pValue</c> is the mutable heal amount and the <b>only</b> <c>HEAL_MODIFIER</c>
        /// insertion point. This mutates a value inside the vanilla pipeline; it does not suppress anything,
        /// and the prefix is <c>void</c> so the original always runs. <b>No RNG is permitted on this path</b>
        /// (the recipe validator rejects a chance-gated heal modifier).</para>
        /// <para>Only positive <c>pValue</c> is treated as a heal — <c>AddHealth</c> is also the damage
        /// application path.</para>
        /// </summary>
        public static void AddHealth_Prefix(Entity pEntity, ref int pValue, List<(eAbilityResults, object)> pResults)
        {
            try
            {
                if (pValue <= 0 || pEntity == null) return;

                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var recipient = ctx.Wrap(pEntity);
                if (recipient == null) return;

                var plan = d.OnHealPending(ctx, new HealPendingEvent
                {
                    Recipient = recipient,
                    Healer = ctx.Wrap(_healOrigin),
                    PendingAmount = pValue
                });
                if (plan == null || plan.Count == 0) return;

                string recipientGuid = recipient.Guid;
                string healerGuid = _healOrigin != null ? SafeGuid(_healOrigin) : null;

                int original = pValue;
                int total = 0;
                var rest = new List<EngineAction>();

                for (int i = 0; i < plan.Count; i++)
                {
                    var mod = plan[i] as HealModifierAction;
                    if (mod == null) { rest.Add(plan[i]); continue; }

                    // Scope: RECEIVED matches on pEntity; GIVEN matches on the healer identity captured by
                    // the paired ApplyStatChange prefix (§4.2 E2).
                    string want = mod.Scope == ClassForge.Recipes.Model.HealScope.GIVEN ? healerGuid : recipientGuid;
                    if (want == null || !string.Equals(mod.TargetGuid, want, StringComparison.Ordinal)) continue;

                    int delta = mod.FlatDelta;
                    if (mod.PercentDelta != 0)
                        delta += (int)Math.Floor((decimal)original * mod.PercentDelta / 100m);
                    if (mod.MinDelta.HasValue && delta < mod.MinDelta.Value)
                        delta = mod.MinDelta.Value;
                    total += delta;
                }

                if (total != 0)
                {
                    int adjusted = original + total;
                    if (adjusted < 0) adjusted = 0;   // never turn a heal into damage
                    pValue = adjusted;
                }

                if (rest.Count > 0)
                    RecipeActionExecutor.Execute(rest, new RecipeExecEnvironment { Ctx = ctx, Results = pResults });
            }
            catch (Exception ex) { Fail("AddHealth(prefix)", ex); }
        }

        // =====================================================================================
        // T5 ON_STATUS_APPLIED — InteractableHelper.ApplyStatus single-target Postfix (L1219)
        // =====================================================================================

        /// <summary>
        /// The single-target overload only (<c>Entity pTargetEntity</c>, with <c>int? pDurationOverride</c>);
        /// the party-broadcast overload at L1128 is deliberately not patched — its target is a
        /// <c>List&lt;Entity&gt;</c> and the delta binds this trigger's owner to a single
        /// <c>pTargetEntity</c>.
        /// <para>This is a <b>Postfix</b> on purpose (§7.3): the status IS authoritatively applied on every
        /// peer, and a recipe may then remove it. EOR's form — a prefix returning <c>false</c> — is a textbook
        /// per-client suppression of authoritative state and is refused. Semantic delta to record: the status
        /// is briefly applied and then removed rather than never applied, so any on-apply side effect fires
        /// once.</para>
        /// </summary>
        public static void ApplyStatus_Postfix(Entity pOriginEntity, Entity pTargetEntity, Thing pThing,
            string pStatusConfigName, List<(eAbilityResults, object)> pResults)
        {
            try
            {
                if (pTargetEntity == null || string.IsNullOrEmpty(pStatusConfigName)) return;

                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var target = ctx.Wrap(pTargetEntity);
                if (target == null) return;

                var plan = d.OnStatusApplied(ctx, new StatusAppliedEvent
                {
                    Target = target, Applier = ctx.Wrap(pOriginEntity), StatusId = pStatusConfigName
                });
                RecipeActionExecutor.Execute(plan, new RecipeExecEnvironment
                {
                    Ctx = ctx, Thing = pThing, Results = pResults
                });
            }
            catch (Exception ex) { Fail("ApplyStatus(postfix)", ex); }
        }

        // =====================================================================================
        // T6 ON_CONSUMABLE_USED — InteractableHelper.PerformConsumableAbility Postfix (L538)
        // =====================================================================================

        public static void PerformConsumableAbility_Postfix(Entity pOriginEntity, Entity pTargetEntity,
            List<Entity> pParty, Thing pThing, string pAbilityName, List<(eAbilityResults, object)> __result)
        {
            try
            {
                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var origin = ctx.Wrap(pOriginEntity);
                if (origin == null) return;
                ctx.ActingThing = pThing;

                var plan = d.OnConsumableUsed(ctx, new ConsumableUsedEvent
                {
                    Origin = origin,
                    Target = ctx.Wrap(pTargetEntity),
                    ItemConfigName = pThing != null ? pThing.ConfigName : null,
                    AbilityId = pAbilityName
                });
                RecipeActionExecutor.Execute(plan, new RecipeExecEnvironment
                {
                    Ctx = ctx, Party = pParty, Thing = pThing, Results = __result
                });
            }
            catch (Exception ex) { Fail("PerformConsumableAbility(postfix)", ex); }
        }

        // =====================================================================================
        // ON_TURN_START / ON_TURN_END — CombatPhase._performSkillAbilityProcs Prefix
        // =====================================================================================

        /// <summary>
        /// <b>Re-anchored from SPEC-DELTA-v1.1 §2.1, which named the wrong method.</b> The delta says
        /// <c>ON_TURN_START</c>/<c>ON_TURN_END</c> hook <c>CombatHelper._onCombatSkillProc</c> Postfix and
        /// read a "native <c>EVENT_PROC</c> <c>START_TURN</c>/<c>END_TURN</c>" vocabulary from it. That is not
        /// possible: <c>_onCombatSkillProc(CombatState, Entity, eSkills, int, bool, List&lt;...&gt;, bool)</c>
        /// carries <b>no phase parameter at all</b>, and <c>eSkills</c> is a flat list of concrete skill names
        /// with no <c>START_TURN</c>/<c>END_TURN</c>/<c>EVENT_PROC</c> members — it is a shared bookkeeping
        /// sink called from every skill-proc path.
        ///
        /// <para>The turn phase lives in a <b>separate</b> enum, <c>eSkillEventProcs</c>
        /// (<c>NONE, START_TURN, END_TURN, ON_KILL, ON_DAMAGE_GIVEN</c>), and the only place it appears as a
        /// parameter is <c>CombatPhase._performSkillAbilityProcs(Entity pEntity, eSkillEventProcs pProcEvent)</c>
        /// — called with <c>START_TURN</c> at CombatPhase.cs L2096 and with <c>END_TURN</c> at L4854/L4880.
        /// That is therefore the hook, and it carries the owner entity as its first parameter exactly as the
        /// delta requires.</para>
        ///
        /// <para>Two caveats, both accepted: the method is <b>private</b> (resolved by
        /// <c>AccessTools</c>, with a "Target NOT found" fail-safe that disables only these two triggers), and
        /// it is <c>async Task</c> — a prefix runs on the kickoff stub, before the first <c>await</c>, which is
        /// the correct observation point. The prefix is <c>void</c> and reads its parameters only.</para>
        /// </summary>
        public static void PerformSkillAbilityProcs_Prefix(Entity pEntity, eSkillEventProcs pProcEvent)
        {
            try
            {
                if (pProcEvent != eSkillEventProcs.START_TURN && pProcEvent != eSkillEventProcs.END_TURN) return;

                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var owner = ctx.Wrap(pEntity);
                if (owner == null) return;

                var e = new TurnEvent { Entity = owner };
                var plan = pProcEvent == eSkillEventProcs.START_TURN ? d.OnTurnStart(ctx, e) : d.OnTurnEnd(ctx, e);
                RecipeActionExecutor.Execute(plan, new RecipeExecEnvironment { Ctx = ctx });
            }
            catch (Exception ex) { Fail("_performSkillAbilityProcs(prefix)", ex); }
        }

        internal static void WarnTurnHookMissing()
        {
            if (_warnedTurnHookMissing) return;
            _warnedTurnHookMissing = true;
            ClassForgePlugin.Log.LogWarning(
                "[ClassForge] CombatPhase._performSkillAbilityProcs was not found — the ON_TURN_START and " +
                "ON_TURN_END triggers are DISABLED for this session (fail-safe). Every other trigger is " +
                "unaffected. This is the private-method risk SPEC-DELTA-v1.1 §9 risk 3 flagged.");
        }

        // =====================================================================================
        // C12 MOVED_THIS_ROUND observer — CombatHelper.ApplyAction Postfix, pAction == MOVE
        // =====================================================================================

        /// <summary>
        /// Emits no recipe actions — it only stamps <c>TurnState.MovedRound</c> so the
        /// <c>MOVED_THIS_ROUND</c> condition can read it. <c>eCombatActions pAction</c> is a plain parameter
        /// and <c>MOVE</c> is member 0.
        /// <para>Skipped while a plan is executing, so ClassForge's own emitted actions can never be
        /// mistaken for the entity having moved.</para>
        /// </summary>
        public static void ApplyAction_Postfix(Entity pTarget, eCombatActions pAction)
        {
            try
            {
                if (pAction != eCombatActions.MOVE || pTarget == null) return;
                if (RecipeEngineHost.IsExecuting) return;

                CombatContextAdapter ctx; RecipeDispatcher d;
                if (!RecipeEngineHost.TryBegin(out ctx, out d)) return;

                var mover = ctx.Wrap(pTarget);
                if (mover != null) d.ObserveMove(ctx, mover);
            }
            catch (Exception ex) { Fail("ApplyAction(move observer)", ex); }
        }

        // =====================================================================================

        private static string AbilityIdOf(CombatDecisionData decision)
        {
            try { return decision != null ? (decision.Ability.AbilityName ?? "") : ""; }
            catch { return ""; }
        }

        private static int CurrentHp(Entity entity)
        {
            try
            {
                CharacterComponent cc;
                if (entity == null || !entity.TryGet<CharacterComponent>(out cc) || cc == null) return 0;
                return cc.CurrentHealth;
            }
            catch { return 0; }
        }

        private static string SafeGuid(Entity entity)
        {
            try { return entity != null ? entity.Guid : null; }
            catch { return null; }
        }

        /// <summary>Fail-safe (CONVENTIONS.md): every patch body swallows, logs, and defers to vanilla.</summary>
        private static void Fail(string where, Exception ex)
        {
            ClassForgePlugin.Log.LogError("[ClassForge] Recipe hook '" + where + "' failed (fail-safe, vanilla behavior unchanged): " + ex);
        }
    }
}
