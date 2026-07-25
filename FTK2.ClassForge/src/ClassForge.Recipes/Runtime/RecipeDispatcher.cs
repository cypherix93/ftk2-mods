using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Runtime
{
    /// <summary>
    /// The v1.1 recipe engine. Routes trigger events to recipes, evaluates conditions/cooldowns/budgets,
    /// takes the <c>ProcChance</c> roll at one documented point, and returns an ordered
    /// <see cref="EngineAction"/> plan. <b>It never mutates game state</b> — the Plugin unit translates
    /// the plan into <c>CombatHelper.ApplyAction</c> calls.
    /// <para><b>No static mutable state anywhere.</b> Two dispatchers fed the same event stream with the
    /// same-seeded RNG produce identical plans (SPEC-DELTA-v1.1 §6, §5.2).</para>
    /// </summary>
    public sealed class RecipeDispatcher
    {
        private readonly RecipeSet _recipes;
        private readonly IRandomSource _rng;
        private readonly IRecipeLog _log;
        private readonly RecipeStateStore _state = new RecipeStateStore();
        private bool _loggedNullRandom;

        /// <param name="recipes">Parsed + validated recipe book. Recipes with validation errors are skipped.</param>
        /// <param name="random">
        /// The shared combat RNG. Adapter must pass <c>Env.GameRun.CombatState.Random</c>.
        /// Passing <c>null</c> means "no active combat": per §5.2 invariant 2 no recipe fires and a one-time
        /// skip is logged. It NEVER falls back to an ad-hoc seeded stream (EOR's non-lockstep anti-pattern).
        /// </param>
        public RecipeDispatcher(RecipeSet recipes, IRandomSource random, IRecipeLog log)
        {
            _recipes = recipes ?? new RecipeSet();
            _rng = random;
            _log = log;
        }

        /// <summary>Per-battle state table (§6). Exposed for tests and for the Plugin's reset hooks.</summary>
        public RecipeStateStore State { get { return _state; } }

        /// <summary>Drops the per-battle runtime. Redundant by design — <see cref="RecipeStateStore.Sync"/>
        /// already re-allocates on a new <c>CombatKey</c> (§6).</summary>
        public void ResetCombat() { _state.ResetCombat(); }

        /// <summary>Drops all engine state at end of run. v1.1 has no per-run state, so this is
        /// <see cref="ResetCombat"/> plus the guarantee it stays that way (§6 "Per-run state: none").</summary>
        public void ResetRun() { _state.ResetRun(); }

        // =====================================================================================
        // Trigger entry points — one per SPEC-DELTA-v1.1 §2 token.
        // =====================================================================================

        /// <summary>T1 <c>ON_COMBAT_START</c> — <c>CombatHelper.SetInitiative</c> Postfix.</summary>
        public IReadOnlyList<EngineAction> OnCombatStart(ICombatContext ctx, CombatStartEvent e)
        {
            return Run(ctx, TriggerKind.ON_COMBAT_START, Single(ctx, e.Entity, t => { }));
        }

        /// <summary>T2 <c>ON_ABILITY_DECLARED</c> — <c>CombatHelper.PerformAbility</c> Prefix.</summary>
        public IReadOnlyList<EngineAction> OnAbilityDeclared(ICombatContext ctx, AbilityDeclaredEvent e)
        {
            return Run(ctx, TriggerKind.ON_ABILITY_DECLARED, Single(ctx, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Roll = e.RollTier;
                t.HasRoll = true;
                t.FocusUsed = e.FocusUsed;
            }));
        }

        /// <summary><c>ON_ABILITY_USED</c> — <c>CombatHelper.PerformAbility</c> Postfix.
        /// Also the <c>ActedRound</c>/<c>LastAbilityId</c> observer for §6's TurnState (written AFTER dispatch
        /// so <c>ABILITY_REPEATED</c> compares against the previous ability, not this one).</summary>
        public IReadOnlyList<EngineAction> OnAbilityUsed(ICombatContext ctx, AbilityUsedEvent e)
        {
            var plan = Run(ctx, TriggerKind.ON_ABILITY_USED, Single(ctx, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Roll = e.RollTier;
                t.HasRoll = true;
                t.FocusUsed = e.FocusUsed;
            }));
            if (e.Origin != null)
            {
                var ts = _state.Sync(ctx).GetTurnState(e.Origin.Guid);
                ts.ActedRound = ctx.Round;
                ts.LastAbilityId = e.AbilityId ?? "";
            }
            return plan;
        }

        /// <summary>
        /// T7 <c>ON_ENEMY_ABILITY_RESOLVED</c> — <c>CombatHelper.PerformAbility</c> Postfix.
        /// <para>The owner list is a deterministic function of replicated state: living entities opposed to
        /// <c>pOrigin</c>, filtered to holders of an <c>ON_ENEMY_ABILITY_RESOLVED</c> recipe, sorted by
        /// <b>ascending ordinal <c>Entity.Guid</c></b> (§2 T7 determinism note). Owners are the outer loop,
        /// recipes the inner loop, so the draw sequence is a pure function of (entity set, recipe book).</para>
        /// </summary>
        public IReadOnlyList<EngineAction> OnEnemyAbilityResolved(ICombatContext ctx, EnemyAbilityResolvedEvent e)
        {
            var runtime = _state.Sync(ctx);
            var owners = EntitySets.Opponents(ctx, e.Origin);
            var contexts = new List<TriggerContext>();
            for (int i = 0; i < owners.Count; i++)
            {
                if (!HoldsAnyRecipeFor(owners[i], TriggerKind.ON_ENEMY_ABILITY_RESOLVED)) continue;
                contexts.Add(new TriggerContext
                {
                    Ctx = ctx,
                    Runtime = runtime,
                    Trigger = TriggerKind.ON_ENEMY_ABILITY_RESOLVED,
                    Owner = owners[i],
                    TriggerSource = e.Origin,
                    TriggerTarget = e.Target,
                    AbilityId = e.AbilityId,
                    Roll = e.RollTier,
                    HasRoll = true
                });
            }
            return Run(ctx, TriggerKind.ON_ENEMY_ABILITY_RESOLVED, contexts);
        }

        /// <summary><c>ON_CRIT</c> — <c>InteractableHelper.ApplyStatChange</c> Postfix, <c>pIsCrit == true</c>.</summary>
        public IReadOnlyList<EngineAction> OnCrit(ICombatContext ctx, CritEvent e)
        {
            return Run(ctx, TriggerKind.ON_CRIT, Single(ctx, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
            }));
        }

        /// <summary><c>ON_KILL</c> — <c>ApplyStatChange</c> Prefix(capture)+Postfix, origin-attributed.</summary>
        public IReadOnlyList<EngineAction> OnKill(ICombatContext ctx, KillEvent e)
        {
            return Run(ctx, TriggerKind.ON_KILL, Single(ctx, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Amount = e.HpBefore - e.HpAfter;
            }));
        }

        /// <summary>T3 <c>ON_DAMAGE_DEALT</c> — owner = <c>pOriginEntity</c>.</summary>
        public IReadOnlyList<EngineAction> OnDamageDealt(ICombatContext ctx, DamageDealtEvent e)
        {
            return Run(ctx, TriggerKind.ON_DAMAGE_DEALT, Single(ctx, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Amount = e.Amount;
            }));
        }

        /// <summary>T4 <c>ON_DAMAGE_TAKEN</c> — owner = <c>pTargetEntity</c>, attacker → <c>TRIGGER_SOURCE</c>.
        /// This is also where the deprecated <c>ON_DAMAGED</c> alias lands (rewritten at parse time).</summary>
        public IReadOnlyList<EngineAction> OnDamageTaken(ICombatContext ctx, DamageTakenEvent e)
        {
            return Run(ctx, TriggerKind.ON_DAMAGE_TAKEN, Single(ctx, e.Victim, t =>
            {
                t.TriggerTarget = e.Victim;
                t.TriggerSource = e.Attacker;
                t.AbilityId = e.AbilityId;
                t.Amount = e.Amount;
            }));
        }

        /// <summary><c>ON_HEAL</c> — owner = healer, healed entity → <c>TRIGGER_TARGET</c>.</summary>
        public IReadOnlyList<EngineAction> OnHeal(ICombatContext ctx, HealEvent e)
        {
            return Run(ctx, TriggerKind.ON_HEAL, Single(ctx, e.Healer, t =>
            {
                t.TriggerTarget = e.Healed;
                t.AbilityId = e.AbilityId;
                t.Amount = e.Amount;
            }));
        }

        /// <summary>T8 <c>ON_HEAL_PENDING</c> — <c>CharacterHelper.AddHealth</c> Prefix. No RNG on this path
        /// (the validator rejects a chance-gated <c>HEAL_MODIFIER</c>).</summary>
        public IReadOnlyList<EngineAction> OnHealPending(ICombatContext ctx, HealPendingEvent e)
        {
            return Run(ctx, TriggerKind.ON_HEAL_PENDING, Single(ctx, e.Recipient, t =>
            {
                t.TriggerTarget = e.Recipient;
                t.TriggerSource = e.Healer;
                t.ItemConfigName = e.ItemConfigName;
                t.Amount = e.PendingAmount;
            }));
        }

        /// <summary>T5 <c>ON_STATUS_APPLIED</c> — <c>InteractableHelper.ApplyStatus</c> single-target Postfix.
        /// The status IS authoritatively applied; the recipe observes and may remove it (§7.3).</summary>
        public IReadOnlyList<EngineAction> OnStatusApplied(ICombatContext ctx, StatusAppliedEvent e)
        {
            return Run(ctx, TriggerKind.ON_STATUS_APPLIED, Single(ctx, e.Target, t =>
            {
                t.TriggerTarget = e.Target;
                t.TriggerSource = e.Applier;
                t.StatusId = e.StatusId;
            }));
        }

        /// <summary>T6 <c>ON_CONSUMABLE_USED</c> — <c>InteractableHelper.PerformConsumableAbility</c> Postfix.</summary>
        public IReadOnlyList<EngineAction> OnConsumableUsed(ICombatContext ctx, ConsumableUsedEvent e)
        {
            return Run(ctx, TriggerKind.ON_CONSUMABLE_USED, Single(ctx, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.ItemConfigName = e.ItemConfigName;
                t.AbilityId = e.AbilityId;
            }));
        }

        /// <summary><c>ON_TURN_START</c> — <c>CombatHelper._onCombatSkillProc</c> Postfix, <c>START_TURN</c>.</summary>
        public IReadOnlyList<EngineAction> OnTurnStart(ICombatContext ctx, TurnEvent e)
        {
            return Run(ctx, TriggerKind.ON_TURN_START, Single(ctx, e.Entity, t => { }));
        }

        /// <summary><c>ON_TURN_END</c> — <c>CombatHelper._onCombatSkillProc</c> Postfix, <c>END_TURN</c>.</summary>
        public IReadOnlyList<EngineAction> OnTurnEnd(ICombatContext ctx, TurnEvent e)
        {
            return Run(ctx, TriggerKind.ON_TURN_END, Single(ctx, e.Entity, t => { }));
        }

        /// <summary>
        /// Internal <c>MOVED_THIS_ROUND</c> observer — the Plugin calls this from
        /// <c>CombatHelper.ApplyAction</c> Postfix where <c>pAction == eCombatActions.MOVE</c>
        /// (§3.2 C12). Emits no actions.
        /// </summary>
        public void ObserveMove(ICombatContext ctx, ICombatEntity entity)
        {
            if (entity == null) return;
            var runtime = _state.Sync(ctx);
            runtime.GetTurnState(entity.Guid).MovedRound = runtime.Round;
        }

        // =====================================================================================
        // Core evaluation
        // =====================================================================================

        private List<TriggerContext> Single(ICombatContext ctx, ICombatEntity owner, Action<TriggerContext> fill)
        {
            var list = new List<TriggerContext>();
            if (owner == null) return list;
            var t = new TriggerContext { Ctx = ctx, Runtime = _state.Sync(ctx), Owner = owner };
            fill(t);
            list.Add(t);
            return list;
        }

        private bool HoldsAnyRecipeFor(ICombatEntity e, TriggerKind trigger)
        {
            var ordered = _recipes.Ordered;
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                if (r.Trigger != trigger || !r.IsLive) continue;
                if (Holds(e, r.Id)) return true;
            }
            return false;
        }

        private static bool Holds(ICombatEntity e, string recipeId)
        {
            if (e == null) return false;
            var p = e.Passives;
            if (p == null) return false;
            for (int i = 0; i < p.Count; i++)
                if (string.Equals(p[i], recipeId, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Evaluation order is fixed by SPEC-DELTA-v1.1 §5.2 invariant 3:
        /// <c>Enabled → Trigger match → Conditions → Cooldown → Budget → roll → Effects</c>.
        /// Because the roll is last, no draw is taken on a path a peer could skip for a state-dependent reason.
        /// </summary>
        private List<EngineAction> Run(ICombatContext ctx, TriggerKind trigger, List<TriggerContext> owners)
        {
            var plan = new List<EngineAction>();
            if (ctx == null || owners.Count == 0) return plan;

            var runtime = _state.Sync(ctx);

            // §5.2 invariant 2 — null stream ⇒ no fire, ever. Never falls back to an ad-hoc GameRandom.
            if (_rng == null)
            {
                if (!_loggedNullRandom)
                {
                    _loggedNullRandom = true;
                    if (_log != null)
                        _log.Warn("[ClassForge] no CombatState.Random available; recipe evaluation skipped (SPEC-DELTA-v1.1 §5.2 invariant 2)");
                }
                return plan;
            }

            var ordered = _recipes.Ordered;
            for (int oi = 0; oi < owners.Count; oi++)   // owners: ascending ordinal Guid (§5.2 inv. 4)
            {
                var t = owners[oi];
                t.Runtime = runtime;
                t.Trigger = trigger;
                for (int ri = 0; ri < ordered.Count; ri++)   // recipes: ascending Priority, then ordinal id
                {
                    var r = ordered[ri];
                    if (r.Trigger != trigger) continue;
                    if (!r.IsLive) continue;                      // Enabled + validator gate
                    if (!Holds(t.Owner, r.Id)) continue;
                    EvaluateRecipe(r, t, runtime, plan);
                }
            }

            for (int i = 0; i < plan.Count; i++) ctx.EmitAction(plan[i]);
            return plan;
        }

        private void EvaluateRecipe(SkillRecipe r, TriggerContext t, CombatRuntime runtime, List<EngineAction> plan)
        {
            // 1. Conditions (ANDed; empty array is always true)
            if (!ConditionEvaluator.EvaluateAll(r.Conditions, t, null)) return;

            // 2. Cooldown
            if (!runtime.IsCooldownReady(r.Id, t.Owner.Guid)) return;

            // 3. Budget availability
            string targetGuid = t.TriggerTarget != null ? t.TriggerTarget.Guid : "";
            if (!runtime.IsBudgetAvailable(r.Budget.Scope, r.BudgetKey, t.Owner.Guid, targetGuid)) return;

            if (r.Budget.ConsumeOn == ConsumeOn.EVALUATION)
                runtime.ConsumeBudget(r.Budget.Scope, r.BudgetKey, t.Owner.Guid, targetGuid);

            // 4. THE roll. This is the ONE place the engine draws for ProcChance, and it is reached
            //    exactly once per eligible evaluation, on every peer, in the same order (§5.2 inv. 1+3).
            //    ProcChance == 100 takes ZERO draws — explicitly required by §5.1.
            int chance = t.Owner.IsAiControlled ? r.AiProcChance : r.ProcChance;
            bool proc;
            if (chance >= 100) proc = true;
            else proc = _rng.NextChance(chance / 100m);

            if (!proc)
            {
                // ConsumeOn.PROC leaves the budget open so a later qualifying event in the same round
                // re-rolls — EOR's real SENTINEL behavior (§5.1).
                return;
            }

            if (r.Budget.ConsumeOn == ConsumeOn.PROC)
                runtime.ConsumeBudget(r.Budget.Scope, r.BudgetKey, t.Owner.Guid, targetGuid);

            runtime.StartCooldown(r.Id, t.Owner.Guid, r.Cooldown);

            // 5. Effects, in authored array order (§5.2 inv. 4)
            int before = plan.Count;
            for (int i = 0; i < r.Effects.Count; i++)
                PlanEffect(r, r.Effects[i], i, t, runtime, plan);

            if (r.Budget.ConsumeOn == ConsumeOn.EFFECT_APPLIED && plan.Count > before)
                runtime.ConsumeBudget(r.Budget.Scope, r.BudgetKey, t.Owner.Guid, targetGuid);

            if (_log != null && !string.IsNullOrEmpty(r.VerboseLogTag))
                _log.Info("[ClassForge] proc " + r.Id + " (" + r.VerboseLogTag + ") owner=" + t.Owner.Guid +
                          " actions=" + (plan.Count - before));
        }

        private void PlanEffect(SkillRecipe r, RecipeEffect e, int index, TriggerContext t, CombatRuntime runtime, List<EngineAction> plan)
        {
            // Per-effect Conditions (§4) — same evaluator, same trigger context.
            if (!ConditionEvaluator.EvaluateAll(e.Conditions, t, null)) return;

            switch (e.Type)
            {
                case EffectKind.COUNTER_ADD:
                {
                    int v = runtime.AddCounter(t.Owner.Guid, e.Name, e.Delta, e.Max);
                    plan.Add(new CounterAddAction
                    {
                        RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                        CounterName = e.Name, Delta = e.Delta, NewValue = v
                    });
                    return;
                }
                case EffectKind.COUNTER_SET:
                {
                    runtime.SetCounter(t.Owner.Guid, e.Name, e.Value);
                    plan.Add(new CounterSetAction
                    {
                        RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                        CounterName = e.Name, NewValue = e.Value
                    });
                    return;
                }
            }

            var targets = TargetResolver.Resolve(e, t);
            // ALLY_BY_RANK with no matching candidate is a no-op and, with ConsumeOn EFFECT_APPLIED,
            // does not consume the budget (§4.3). No RNG is drawn on this path.
            if (targets.Count == 0) return;

            switch (e.Type)
            {
                case EffectKind.ADD_STATUS:
                {
                    string status = ResolveStatus(e, t);
                    if (string.IsNullOrEmpty(status)) return;
                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new AddStatusAction
                        {
                            RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                            TargetGuid = targets[i].Guid, StatusId = status,
                            FallbackStatusId = e.FallbackStatus, Duration = e.Duration
                        });
                    break;
                }
                case EffectKind.REMOVE_STATUS:
                {
                    string status = ResolveStatus(e, t);
                    if (string.IsNullOrEmpty(status)) return;
                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new RemoveStatusAction
                        {
                            RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                            TargetGuid = targets[i].Guid, StatusId = status
                        });
                    break;
                }
                case EffectKind.STAT_CHANGE:
                {
                    int? flat = e.FlatValue;
                    if (!flat.HasValue && !string.IsNullOrEmpty(e.FlatValueFrom))
                        flat = ValueSources.Resolve(e.FlatValueFrom, e, t);
                    int? pct = e.FlatPercent;
                    if (!pct.HasValue && !string.IsNullOrEmpty(e.PercentFrom))
                        pct = ValueSources.Resolve(e.PercentFrom, e, t);
                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new StatChangeAction
                        {
                            RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                            TargetGuid = targets[i].Guid, Stat = e.Stat, StatChangeType = e.StatChangeType,
                            FlatValue = flat, FlatPercent = pct, Blockable = e.Blockable, IsSilent = e.IsSilent
                        });
                    break;
                }
                case EffectKind.SUMMON:
                {
                    bool usePos = e.Target == TargetKind.TRIGGER_TARGET_POSITION;
                    for (int i = 0; i < targets.Count; i++)
                        for (int k = 0; k < e.Count; k++)   // Count expands to N sequential actions, ascending (OQ#4)
                            plan.Add(new SummonAction
                            {
                                RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                                TargetGuid = targets[i].Guid, UseTargetPosition = usePos,
                                SummonType = e.SummonType, CharacterConfig = e.CharacterConfig, Index = k
                            });
                    break;
                }
                case EffectKind.ROLL_STAT_BONUS:
                {
                    int flat = e.Flat.HasValue ? e.Flat.Value : 0;
                    if (!e.Flat.HasValue && !string.IsNullOrEmpty(e.FlatValueFrom))
                        flat = ValueSources.Resolve(e.FlatValueFrom, e, t);
                    int pct = e.Percent.HasValue ? e.Percent.Value : 0;
                    if (!e.Percent.HasValue && !string.IsNullOrEmpty(e.PercentFrom))
                        pct = ValueSources.Resolve(e.PercentFrom, e, t);
                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new RollStatBonusAction
                        {
                            RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                            TargetGuid = targets[i].Guid, Stat = e.Stat,
                            FlatDelta = flat, PercentDelta = pct, MinDelta = e.MinDelta
                        });
                    break;
                }
                case EffectKind.HEAL_MODIFIER:
                {
                    int flat = e.Flat.HasValue ? e.Flat.Value : 0;
                    int pct = e.Percent.HasValue ? e.Percent.Value : 0;
                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new HealModifierAction
                        {
                            RecipeId = r.Id, OwnerGuid = t.Owner.Guid, EffectIndex = index,
                            TargetGuid = targets[i].Guid, Scope = e.Scope,
                            FlatDelta = flat, PercentDelta = pct, MinDelta = e.MinDelta
                        });
                    break;
                }
            }
        }

        /// <summary>
        /// <c>Status</c> / <c>StatusOneOf</c> / <c>TRIGGER_STATUS</c> resolution.
        /// <para><c>StatusOneOf</c> is <b>exactly one draw</b>, taken only when the effect actually executes —
        /// i.e. after conditions, budget and <c>ProcChance</c> have all passed and at least one target
        /// resolved (SPEC-DELTA-v1.1 §4.1 RANDOM_ELEMENT_CHOICE). The authored list is parity-hashed and
        /// identical on every peer, and <c>NextInt(0, n)</c> is exactly what
        /// <c>GameRandom.GetRandomElementFromList</c> does internally.</para>
        /// </summary>
        private string ResolveStatus(RecipeEffect e, TriggerContext t)
        {
            if (e.StatusOneOf != null && e.StatusOneOf.Count > 0)
            {
                int idx = _rng.NextInt(0, e.StatusOneOf.Count);
                if (idx < 0) idx = 0;
                if (idx >= e.StatusOneOf.Count) idx = e.StatusOneOf.Count - 1;
                return e.StatusOneOf[idx];
            }
            if (string.Equals(e.Status, Vocabulary.TriggerStatusToken, StringComparison.Ordinal))
                return t.StatusId;
            return e.Status;
        }
    }
}
