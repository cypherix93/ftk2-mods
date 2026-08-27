using System;
using System.Collections.Generic;
using System.Globalization;
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
        private readonly int? _debugProcChanceFormulaOverride;
        private bool _loggedNullRandom;

        /// <param name="recipes">Parsed + validated recipe book. Recipes with validation errors are skipped.</param>
        /// <param name="random">
        /// The shared combat RNG. Adapter must pass <c>Env.GameRun.CombatState.Random</c>.
        /// Passing <c>null</c> means "no active combat": per §5.2 invariant 2 no recipe fires and a one-time
        /// skip is logged. It NEVER falls back to an ad-hoc seeded stream (EOR's non-lockstep anti-pattern).
        /// </param>
        /// <param name="log">Optional diagnostic sink.</param>
        /// <param name="debugProcChanceFormulaOverride">
        /// DIAGNOSTIC ONLY — Encounter Modifiers spec §12.6 smoke knob (plugin-bound <c>[Skills]
        /// DebugEncounterModifierChance</c>). When set, REPLACES every <see cref="ProcChanceFormula"/>
        /// result with this fixed 0..100 value instead of evaluating the formula's conditions — e.g. 100
        /// forces every eligible encounter-modifier roll to succeed for an SP smoke test. Plain
        /// <c>ProcChance</c> recipes (the vast majority of the book) are entirely unaffected; this only
        /// ever substitutes for a <see cref="ProcChanceFormula"/> result. <c>null</c> (the default, and
        /// what every non-diagnostic construction — including every test in this suite — passes) means
        /// "no override": the formula's own conditions decide, exactly as shipped.
        /// </param>
        public RecipeDispatcher(RecipeSet recipes, IRandomSource random, IRecipeLog log, int? debugProcChanceFormulaOverride = null)
        {
            _recipes = recipes ?? new RecipeSet();
            _rng = random;
            _log = log;
            _debugProcChanceFormulaOverride = debugProcChanceFormulaOverride;
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
            return Fire(ctx, TriggerKind.ON_COMBAT_START, e.Entity, t => { t.CombatStartReal = e.TrySkillProc; });
        }

        /// <summary>T2 <c>ON_ABILITY_DECLARED</c> — <c>CombatHelper.PerformAbility</c> Prefix.</summary>
        public IReadOnlyList<EngineAction> OnAbilityDeclared(ICombatContext ctx, AbilityDeclaredEvent e)
        {
            return Fire(ctx, TriggerKind.ON_ABILITY_DECLARED, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Roll = e.RollTier;
                t.HasRoll = true;
                t.FocusUsed = e.FocusUsed;
            });
        }

        /// <summary><c>ON_ABILITY_USED</c> — <c>CombatHelper.PerformAbility</c> Postfix.
        /// Also the <c>ActedRound</c>/<c>LastAbilityId</c> observer for §6's TurnState (written AFTER dispatch
        /// so <c>ABILITY_REPEATED</c> compares against the previous ability, not this one).</summary>
        public IReadOnlyList<EngineAction> OnAbilityUsed(ICombatContext ctx, AbilityUsedEvent e)
        {
            var plan = Fire(ctx, TriggerKind.ON_ABILITY_USED, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Roll = e.RollTier;
                t.HasRoll = true;
                t.FocusUsed = e.FocusUsed;
            });
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
            var combatTemplate = new TriggerContext
            {
                Ctx = ctx, Runtime = runtime, Owner = null,
                TriggerSource = e.Origin, TriggerTarget = e.Target, AbilityId = e.AbilityId,
                Roll = e.RollTier, HasRoll = true
            };
            return Run(ctx, TriggerKind.ON_ENEMY_ABILITY_RESOLVED, contexts, combatTemplate);
        }

        /// <summary><c>ON_CRIT</c> — <c>InteractableHelper.ApplyStatChange</c> Postfix, <c>pIsCrit == true</c>.</summary>
        public IReadOnlyList<EngineAction> OnCrit(ICombatContext ctx, CritEvent e)
        {
            return Fire(ctx, TriggerKind.ON_CRIT, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
            });
        }

        /// <summary><c>ON_KILL</c> — <c>ApplyStatChange</c> Prefix(capture)+Postfix, origin-attributed.</summary>
        public IReadOnlyList<EngineAction> OnKill(ICombatContext ctx, KillEvent e)
        {
            return Fire(ctx, TriggerKind.ON_KILL, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Amount = e.HpBefore - e.HpAfter;
            });
        }

        /// <summary>T3 <c>ON_DAMAGE_DEALT</c> — owner = <c>pOriginEntity</c>.</summary>
        public IReadOnlyList<EngineAction> OnDamageDealt(ICombatContext ctx, DamageDealtEvent e)
        {
            return Fire(ctx, TriggerKind.ON_DAMAGE_DEALT, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.AbilityId = e.AbilityId;
                t.Amount = e.Amount;
            });
        }

        /// <summary>T4 <c>ON_DAMAGE_TAKEN</c> — owner = <c>pTargetEntity</c>, attacker → <c>TRIGGER_SOURCE</c>.
        /// This is also where the deprecated <c>ON_DAMAGED</c> alias lands (rewritten at parse time).</summary>
        public IReadOnlyList<EngineAction> OnDamageTaken(ICombatContext ctx, DamageTakenEvent e)
        {
            return Fire(ctx, TriggerKind.ON_DAMAGE_TAKEN, e.Victim, t =>
            {
                t.TriggerTarget = e.Victim;
                t.TriggerSource = e.Attacker;
                t.AbilityId = e.AbilityId;
                t.Amount = e.Amount;
            });
        }

        /// <summary><c>ON_HEAL</c> — owner = healer, healed entity → <c>TRIGGER_TARGET</c>.</summary>
        public IReadOnlyList<EngineAction> OnHeal(ICombatContext ctx, HealEvent e)
        {
            return Fire(ctx, TriggerKind.ON_HEAL, e.Healer, t =>
            {
                t.TriggerTarget = e.Healed;
                t.AbilityId = e.AbilityId;
                t.Amount = e.Amount;
            });
        }

        /// <summary>T8 <c>ON_HEAL_PENDING</c> — <c>CharacterHelper.AddHealth</c> Prefix. No RNG on this path
        /// (the validator rejects a chance-gated <c>HEAL_MODIFIER</c>).</summary>
        public IReadOnlyList<EngineAction> OnHealPending(ICombatContext ctx, HealPendingEvent e)
        {
            return Fire(ctx, TriggerKind.ON_HEAL_PENDING, e.Recipient, t =>
            {
                t.TriggerTarget = e.Recipient;
                t.TriggerSource = e.Healer;
                t.ItemConfigName = e.ItemConfigName;
                t.Amount = e.PendingAmount;
            });
        }

        /// <summary>v1.3 <c>ON_DAMAGE_PENDING</c> — <c>InteractableHelper.CalculateFinalDamage</c> Postfix
        /// (state-hash-chance spec M-SH3). Owner = the damage recipient; <c>Amount</c> = the computed final
        /// damage. <b>No RNG on this path</b> (the validator rejects a chance-gated recipe here — the hook
        /// has no <c>GameRandom</c> and no peer may advance the shared stream at it, SPEC-DELTA §7.4).</summary>
        public IReadOnlyList<EngineAction> OnDamagePending(ICombatContext ctx, DamagePendingEvent e)
        {
            return Fire(ctx, TriggerKind.ON_DAMAGE_PENDING, e.Victim, t =>
            {
                t.TriggerTarget = e.Victim;
                t.Amount = e.Amount;
            });
        }

        /// <summary>T5 <c>ON_STATUS_APPLIED</c> — <c>InteractableHelper.ApplyStatus</c> single-target Postfix.
        /// The status IS authoritatively applied; the recipe observes and may remove it (§7.3).</summary>
        public IReadOnlyList<EngineAction> OnStatusApplied(ICombatContext ctx, StatusAppliedEvent e)
        {
            return Fire(ctx, TriggerKind.ON_STATUS_APPLIED, e.Target, t =>
            {
                t.TriggerTarget = e.Target;
                t.TriggerSource = e.Applier;
                t.StatusId = e.StatusId;
            });
        }

        /// <summary>T6 <c>ON_CONSUMABLE_USED</c> — <c>InteractableHelper.PerformConsumableAbility</c> Postfix.</summary>
        public IReadOnlyList<EngineAction> OnConsumableUsed(ICombatContext ctx, ConsumableUsedEvent e)
        {
            return Fire(ctx, TriggerKind.ON_CONSUMABLE_USED, e.Origin, t =>
            {
                t.TriggerTarget = e.Target;
                t.ItemConfigName = e.ItemConfigName;
                t.AbilityId = e.AbilityId;
            });
        }

        /// <summary><c>ON_TURN_START</c> — <c>CombatHelper._onCombatSkillProc</c> Postfix, <c>START_TURN</c>.</summary>
        public IReadOnlyList<EngineAction> OnTurnStart(ICombatContext ctx, TurnEvent e)
        {
            return Fire(ctx, TriggerKind.ON_TURN_START, e.Entity, t => { });
        }

        /// <summary><c>ON_TURN_END</c> — <c>CombatHelper._onCombatSkillProc</c> Postfix, <c>END_TURN</c>.</summary>
        public IReadOnlyList<EngineAction> OnTurnEnd(ICombatContext ctx, TurnEvent e)
        {
            return Fire(ctx, TriggerKind.ON_TURN_END, e.Entity, t => { });
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

        /// <summary>
        /// Builds both the owned-recipe context (via <see cref="Single"/>) and the combat-scope template for
        /// one trigger firing, from the SAME <paramref name="fill"/> delegate — Encounter Modifiers spec
        /// §4.2/§4.3: "owner binding — for owned recipes the event's entity is the owner; for combat recipes
        /// it is bound to TRIGGER_TARGET instead". The combat template has <c>Owner = null</c>; if
        /// <paramref name="fill"/> did not itself set <c>TriggerTarget</c> (most triggers carry an explicit
        /// one already, e.g. <c>ON_ABILITY_DECLARED</c>'s <c>e.Target</c>), <paramref name="impliedTarget"/>
        /// (the same entity that is Owner for the owned pass) is bound there instead — this is exactly
        /// <c>ON_COMBAT_START</c>'s "the entity being initialized, owned by no one" case.
        /// </summary>
        private List<EngineAction> Fire(ICombatContext ctx, TriggerKind trigger, ICombatEntity impliedTarget, Action<TriggerContext> fill)
        {
            var owners = Single(ctx, impliedTarget, fill);
            TriggerContext template = null;
            if (ctx != null)
            {
                template = new TriggerContext { Ctx = ctx, Runtime = _state.Sync(ctx), Owner = null };
                fill(template);
                if (template.TriggerTarget == null) template.TriggerTarget = impliedTarget;
            }
            return Run(ctx, trigger, owners, template);
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
        /// <para>Encounter Modifiers spec §4.2: owned recipes for this event are evaluated first (unchanged
        /// v1.1 behavior, one pass per owner in <paramref name="owners"/>), then <c>Scope: COMBAT</c> recipes
        /// are evaluated exactly ONCE against <paramref name="combatTemplate"/> (extends invariant 4 — owned
        /// first, then combat recipes ascending Priority/ordinal id, which <c>_recipes.Ordered</c> already
        /// guarantees since it is one globally-sorted list filtered by <c>Scope</c> here).</para>
        /// </summary>
        private List<EngineAction> Run(ICombatContext ctx, TriggerKind trigger, List<TriggerContext> owners, TriggerContext combatTemplate)
        {
            var plan = new List<EngineAction>();
            if (ctx == null) return plan;
            if (owners.Count == 0 && combatTemplate == null) return plan;

            // Single fill point for the diagnostic sink (STATE_HASH_CHANCE §3.3 warn path) — every
            // context flows through Run, so the four construction sites stay log-agnostic.
            for (int i = 0; i < owners.Count; i++) owners[i].Log = _log;
            if (combatTemplate != null) combatTemplate.Log = _log;

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

            // Pass 1 — owned recipes, one owner at a time (§5.2 inv. 4: owners ascending ordinal Guid).
            for (int oi = 0; oi < owners.Count; oi++)
            {
                var t = owners[oi];
                t.Runtime = runtime;
                t.Trigger = trigger;
                for (int ri = 0; ri < ordered.Count; ri++)   // recipes: ascending Priority, then ordinal id
                {
                    var r = ordered[ri];
                    if (r.Trigger != trigger) continue;
                    if (r.Scope != RecipeScope.OWNED) continue;
                    if (!r.IsLive) continue;                      // Enabled + validator gate
                    if (!Holds(t.Owner, r.Id)) continue;
                    EvaluateRecipe(r, t, runtime, plan);
                }
            }

            // Pass 2 — combat-scoped recipes, once per trigger event, after every owned recipe (§4.2).
            if (combatTemplate != null)
            {
                combatTemplate.Runtime = runtime;
                combatTemplate.Trigger = trigger;
                for (int ri = 0; ri < ordered.Count; ri++)
                {
                    var r = ordered[ri];
                    if (r.Trigger != trigger) continue;
                    if (r.Scope != RecipeScope.COMBAT) continue;
                    if (!r.IsLive) continue;
                    EvaluateRecipe(r, combatTemplate, runtime, plan);
                }
            }

            for (int i = 0; i < plan.Count; i++) ctx.EmitAction(plan[i]);
            return plan;
        }

        private void EvaluateRecipe(SkillRecipe r, TriggerContext t, CombatRuntime runtime, List<EngineAction> plan)
        {
            // 1. Conditions (ANDed; empty array is always true)
            if (!ConditionEvaluator.EvaluateAll(r.Conditions, t, null)) return;

            // Encounter Modifiers spec §4.2 state keying: COMBAT-scoped recipes (Owner == null) use the
            // fixed sentinel owner guid "" — ordinal-sorts before every real guid, so iteration order stays
            // deterministic. ONCE_PER_TARGET_* budgets still key on the trigger entity's guid exactly as today.
            string ownerGuid = t.Owner != null ? t.Owner.Guid : "";

            // 2. Cooldown
            if (!runtime.IsCooldownReady(r.Id, ownerGuid)) return;

            // 3. Budget availability
            string targetGuid = t.TriggerTarget != null ? t.TriggerTarget.Guid : "";
            if (!runtime.IsBudgetAvailable(r.Budget.Scope, r.BudgetKey, ownerGuid, targetGuid)) return;

            if (r.Budget.ConsumeOn == ConsumeOn.EVALUATION)
                runtime.ConsumeBudget(r.Budget.Scope, r.BudgetKey, ownerGuid, targetGuid);

            // 4. THE roll(s). This is the ONE place (or, for a hoisted SELECTION_SET recipe, the two places —
            //    §6.3) the engine draws for the proc gate, reached exactly once per eligible evaluation, on
            //    every peer, in the same order (§5.2 inv. 1+3). ProcChance == 100 takes ZERO draws (§5.1).
            //    ProcChanceFormula (Encounter Modifiers spec §5) is a pure function of replicated state
            //    replacing the whole chance computation; a result <= 0 takes ZERO draws (symmetric skip).
            bool usesFormula = r.ProcChanceFormula != null;
            int chance;
            if (usesFormula)
            {
                // Encounter Modifiers spec §12.6 diagnostic override — see the constructor doc comment.
                // Still clamped to a legal chance range even when the operator fat-fingers the knob.
                chance = _debugProcChanceFormulaOverride.HasValue
                    ? Clamp0To100(_debugProcChanceFormulaOverride.Value)
                    : EvaluateProcChanceFormula(r.ProcChanceFormula, t);
            }
            else
            {
                chance = t.Owner != null && t.Owner.IsAiControlled ? r.AiProcChance : r.ProcChance;
            }

            if (usesFormula && chance <= 0) return;   // excluded/clamped fight — zero draws, full stop.

            bool hoistPick = usesFormula && HasSelectionSet(r);

            bool proc;
            if (chance >= 100) proc = true;
            else proc = _rng.NextChance(chance / 100m);

            if (!proc)
            {
                if (hoistPick)
                {
                    // §6.3 constant-2 hoisting: the pick draw is taken unconditionally so every eligible
                    // fight (gate succeeds or fails) takes exactly 2 draws; the result is discarded here.
                    TakeAndDiscardSelectionDraw(r);
                }
                // ConsumeOn.PROC leaves the budget open so a later qualifying event in the same round
                // re-rolls — EOR's real SENTINEL behavior (§5.1).
                return;
            }

            if (r.Budget.ConsumeOn == ConsumeOn.PROC)
                runtime.ConsumeBudget(r.Budget.Scope, r.BudgetKey, ownerGuid, targetGuid);

            runtime.StartCooldown(r.Id, ownerGuid, r.Cooldown);

            // 5. Effects, in authored array order (§5.2 inv. 4). The recipe's SELECTION_SET effect (if any)
            //    takes its own draw here as normal — for a hoisted recipe this IS the pick draw #2.
            int before = plan.Count;
            for (int i = 0; i < r.Effects.Count; i++)
                PlanEffect(r, r.Effects[i], i, t, runtime, plan);

            if (r.Budget.ConsumeOn == ConsumeOn.EFFECT_APPLIED && plan.Count > before)
                runtime.ConsumeBudget(r.Budget.Scope, r.BudgetKey, ownerGuid, targetGuid);

            if (_log != null && !string.IsNullOrEmpty(r.VerboseLogTag))
                _log.Info("[ClassForge] proc " + r.Id + " (" + r.VerboseLogTag + ") owner=" + ownerGuid +
                          " actions=" + (plan.Count - before));
        }

        /// <summary>Encounter Modifiers spec §5: pure function of replicated state, no RNG. First
        /// <c>Base</c> row whose Conditions all pass wins (authoring convention: last row has no Conditions
        /// and so always passes, acting as the default); every passing <c>Adjustments</c> row adds its
        /// Value; the total is clamped to <c>[Min, Max]</c> when present.</summary>
        private static int EvaluateProcChanceFormula(ProcChanceFormula f, TriggerContext t)
        {
            int total = 0;
            for (int i = 0; i < f.Base.Count; i++)
            {
                if (ConditionEvaluator.EvaluateAll(f.Base[i].Conditions, t, null))
                {
                    total = f.Base[i].Value;
                    break;
                }
            }
            for (int i = 0; i < f.Adjustments.Count; i++)
                if (ConditionEvaluator.EvaluateAll(f.Adjustments[i].Conditions, t, null))
                    total += f.Adjustments[i].Value;

            if (f.Min.HasValue && total < f.Min.Value) total = f.Min.Value;
            if (f.Max.HasValue && total > f.Max.Value) total = f.Max.Value;
            return total;
        }

        private static int Clamp0To100(int v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        private static bool HasSelectionSet(SkillRecipe r)
        {
            for (int i = 0; i < r.Effects.Count; i++)
                if (r.Effects[i].Type == EffectKind.SELECTION_SET) return true;
            return false;
        }

        /// <summary>§6.3 constant-2 hoisting: takes the SELECTION_SET pick draw and discards the result,
        /// keeping the per-combat draw count constant (2) whether the gate succeeds or fails.</summary>
        private void TakeAndDiscardSelectionDraw(SkillRecipe r)
        {
            for (int i = 0; i < r.Effects.Count; i++)
            {
                if (r.Effects[i].Type == EffectKind.SELECTION_SET)
                {
                    ResolveSelectionPick(r.Effects[i]);
                    return;
                }
            }
        }

        /// <summary>
        /// <c>SELECTION_SET</c>'s weighted pick — Encounter Modifiers spec §5/§6.3, EOR's algorithm
        /// verbatim (L22814-28): exactly one draw, <c>NextInt(1, Σweights, pMaxInclusive: true)</c>, then walk
        /// the authored array in order subtracting weights until the running total drops to <c>&lt;= 0</c>.
        /// </summary>
        private string ResolveSelectionPick(RecipeEffect e)
        {
            if (e.OneOfWeighted == null || e.OneOfWeighted.Count == 0) return null;
            int total = 0;
            for (int i = 0; i < e.OneOfWeighted.Count; i++)
            {
                int w = e.OneOfWeighted[i].Weight;
                if (w > 0) total += w;
            }
            if (total <= 0) return null;

            int roll = _rng.NextIntInclusive(1, total);
            int remaining = roll;
            for (int i = 0; i < e.OneOfWeighted.Count; i++)
            {
                int w = e.OneOfWeighted[i].Weight;
                if (w <= 0) continue;
                remaining -= w;
                if (remaining <= 0) return e.OneOfWeighted[i].Value;
            }
            return e.OneOfWeighted[e.OneOfWeighted.Count - 1].Value;   // unreachable in practice; safe fallback
        }

        private void PlanEffect(SkillRecipe r, RecipeEffect e, int index, TriggerContext t, CombatRuntime runtime, List<EngineAction> plan)
        {
            // Per-effect Conditions (§4) — same evaluator, same trigger context.
            if (!ConditionEvaluator.EvaluateAll(e.Conditions, t, null)) return;

            // Encounter Modifiers spec §4.2: sentinel owner guid "" for COMBAT-scoped recipes (Owner == null).
            string ownerGuid = t.Owner != null ? t.Owner.Guid : "";

            switch (e.Type)
            {
                case EffectKind.COUNTER_ADD:
                {
                    int v = runtime.AddCounter(ownerGuid, e.Name, e.Delta, e.Max);
                    plan.Add(new CounterAddAction
                    {
                        RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                        CounterName = e.Name, Delta = e.Delta, NewValue = v, Persistent = e.Persistent
                    });
                    return;
                }
                case EffectKind.COUNTER_SET:
                {
                    runtime.SetCounter(ownerGuid, e.Name, e.Value);
                    plan.Add(new CounterSetAction
                    {
                        RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                        CounterName = e.Name, NewValue = e.Value, Persistent = e.Persistent
                    });
                    return;
                }
                case EffectKind.SELECTION_SET:
                {
                    // Encounter Modifiers spec §5/§6.1: exactly one draw (or zero if the table sums to 0),
                    // stores the winner (or nothing) in CombatRuntime.Selections[Name].
                    string picked = ResolveSelectionPick(e);
                    if (!string.IsNullOrEmpty(picked))
                        runtime.SetSelection(e.Name, picked);
                    plan.Add(new SelectionSetAction
                    {
                        RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                        Name = e.Name, Value = picked
                    });
                    return;
                }
                case EffectKind.EVENT_BANNER:
                {
                    // [LOCAL] presentation only — the engine resolves only the raw selection value (a pure
                    // per-battle state read, no RNG); localization/formatting is a Plugin concern (§9).
                    string selectionValue = !string.IsNullOrEmpty(e.TextFromSelection)
                        ? runtime.GetSelection(e.TextFromSelection)
                        : null;
                    plan.Add(new EventBannerAction
                    {
                        RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                        LocKey = e.LocKey, FallbackText = e.FallbackText,
                        DurationMs = e.DurationMs.HasValue ? e.DurationMs.Value : 3000,
                        SelectionValue = selectionValue
                    });
                    return;
                }
            }

            // --- v1.4 RANDOM_TILE (SchemaVersionCover). A tile is not an ICombatEntity, so it never goes
            //     through TargetResolver; it gets its own branch here, BEFORE any target resolution. ---
            if (e.Target == TargetKind.RANDOM_TILE)
            {
                PlanRandomTileEffect(r, e, index, t, ownerGuid, plan);
                return;
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
                            RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
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
                            RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
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

                    // Encounter Modifiers spec §6.1 "PercentFromSelection" sugar (M-EM3): a resolved flat
                    // value of 0 off a PercentFromSelection lookup means "the selected modifier carries no
                    // MaxHpPercent" (or no selection is stored at all) — the WHOLE effect is omitted, never
                    // a zero-value STAT_CHANGE action. This does not affect plain FlatValue/FlatValueFrom
                    // authoring (e.g. an authored Percent of 0), which is unchanged from today.
                    if (!string.IsNullOrEmpty(e.PercentFromSelection) && (!flat.HasValue || flat.Value == 0))
                        return;

                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new StatChangeAction
                        {
                            RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
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
                                RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                                TargetGuid = targets[i].Guid, UseTargetPosition = usePos,
                                SummonType = e.SummonType, CharacterConfig = e.CharacterConfig,
                                CharacterConfigFrom = e.CharacterConfigFrom, Index = k
                            });
                    break;
                }
                case EffectKind.CAPTURE:
                {
                    // One action per resolved target. The engine plans only: it cannot read an item's
                    // CustomData (no game Entity in scope) and it must not decide eligibility, which is a
                    // read of live Configs. Both live in the Plugin's capture-rules unit.
                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new CaptureAction
                        {
                            RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                            TargetGuid = targets[i].Guid, IntoItem = e.IntoItem, IntoKey = e.IntoKey
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
                            RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
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
                            RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                            TargetGuid = targets[i].Guid, Scope = e.Scope,
                            FlatDelta = flat, PercentDelta = pct, MinDelta = e.MinDelta
                        });
                    break;
                }
                case EffectKind.DAMAGE_TAKEN_MULT:
                {
                    // v1.3, state-hash-chance spec M-SH3. The Plugin applies EOR's arithmetic to
                    // CalculateFinalDamage's ref result; the engine only plans (zero draws on this path).
                    for (int i = 0; i < targets.Count; i++)
                        plan.Add(new DamageTakenMultAction
                        {
                            RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                            TargetGuid = targets[i].Guid,
                            Percent = e.Percent.HasValue ? e.Percent.Value : 0,
                            MinDelta = e.MinDelta
                        });
                    break;
                }
            }
        }

        /// <summary>
        /// v1.4 <c>ADD_STATUS</c> aimed at <c>RANDOM_TILE</c> - the random-TARGET half of "drop a random
        /// status on a random tile" (<c>StatusOneOf</c> was always the random-STATUS half).
        ///
        /// <para><b>RNG source and replication.</b> The tile is drawn with a single
        /// <c>IRandomSource.NextInt(0, tiles.Count)</c> - the SAME call <c>StatusOneOf</c> makes, backed by
        /// the same <c>GameRandom</c>, which the Plugin binds to <c>CombatState.Random</c> and nothing else
        /// (SPEC-DELTA-v1.1 5.2 invariant 1). <c>GameRandom</c> is a seeded <c>System.Random</c> whose
        /// <c>Seed</c> is forced to <c>NetworkDebuggingHelper.MultiplayerSeed</c> in an online session
        /// (GameRandom.cs ctors), so every peer walks one identical stream. This is the same stream the
        /// game's own random-tile weather ticks draw from (<c>CombatPhase.cs:2009-2031</c>:
        /// <c>shuffleBag.Pull(_combatState.Random)</c>).</para>
        ///
        /// <para><b>Why an INDEX and not a guid.</b> Drawing the same NUMBER on every peer is only useful if
        /// that number names the same board square everywhere. <c>ICombatContext.Tiles</c> is contracted to
        /// be ordered ascending by <c>(Y, X)</c>, which is a pure function of the static venue map string -
        /// NOT of <c>VenueGameObjectMaps.FromTile</c>'s Dictionary enumeration order, and NOT of any entity
        /// Guid. The draw is therefore peer-identical in both the number drawn and what it means.</para>
        ///
        /// <para><b>Draw discipline (pinned by test).</b> Zero draws when no tile resolves - the empty-board
        /// no-op must never advance the shared stream, or the peer that CAN see the board and the peer that
        /// cannot immediately disagree about every subsequent roll in the combat. Otherwise exactly one draw
        /// for the tile, then <c>ResolveStatus</c>'s usual zero-or-one for <c>StatusOneOf</c> - tile first,
        /// status second, always in that order.</para>
        ///
        /// <para><b>Tile-illegal statuses.</b> After the status resolves, its real
        /// <c>StatusEffectConfig.Type</c> is checked against <see cref="Vocabulary.TileIllegalStatusTypes"/>
        /// (the game's own <c>InteractableHelper.CHARACTER_ONLY_STATUS</c>, whose first member is
        /// <c>STUN</c>). A tile-illegal draw is a logged no-op: the draws are already spent identically on
        /// every peer, so skipping the ACTION keeps every peer in step. The validator rejects the statically
        /// visible cases at load time, so this path is the residual belt, not the primary defence.</para>
        ///
        /// <para><b>Fail-safe contract</b> (mirrors SELF_LEVEL / HAS_ITEM): every unresolvable case is a
        /// no-op that logs at Debug with the <c>[ClassForge]</c> prefix - never an exception, never a
        /// silent success. <c>IRecipeLog.Info</c> IS the Debug channel (the Plugin's adapter maps it to
        /// <c>LogDebug</c>).</para>
        /// </summary>
        private void PlanRandomTileEffect(SkillRecipe r, RecipeEffect e, int index, TriggerContext t,
            string ownerGuid, List<EngineAction> plan)
        {
            var tiles = t.Ctx != null ? t.Ctx.Tiles : null;
            if (tiles == null || tiles.Count == 0)
            {
                // NO DRAW. See "Draw discipline" above.
                if (_log != null)
                    _log.Info("[ClassForge] RANDOM_TILE resolved no tile for " + r.Id + " effect[" +
                              index.ToString(CultureInfo.InvariantCulture) + "] - no-op (0 draws). The venue " +
                              "exposed no playable tiles (no combat, or every tile is a border cell).");
                return;
            }

            int idx = _rng.NextInt(0, tiles.Count);
            if (idx < 0) idx = 0;
            if (idx >= tiles.Count) idx = tiles.Count - 1;
            var tile = tiles[idx];
            if (tile == null)
            {
                if (_log != null)
                    _log.Info("[ClassForge] RANDOM_TILE drew a null tile at index " +
                              idx.ToString(CultureInfo.InvariantCulture) + " for " + r.Id + " - no-op. " +
                              "The draw is spent, so peers stay in step.");
                return;
            }

            string status = ResolveStatus(e, t);
            if (string.IsNullOrEmpty(status))
            {
                if (_log != null)
                    _log.Info("[ClassForge] RANDOM_TILE for " + r.Id + " resolved no status - no-op.");
                return;
            }

            if (IsTileIllegalStatus(t.Ctx, status))
            {
                if (_log != null)
                    _log.Info("[ClassForge] RANDOM_TILE for " + r.Id + " resolved '" + status +
                              "', whose type is character-only (InteractableHelper.CHARACTER_ONLY_STATUS) " +
                              "and can never sit on a tile - no-op. STUN is a member of that set.");
                return;
            }

            plan.Add(new AddStatusAction
            {
                RecipeId = r.Id, OwnerGuid = ownerGuid, EffectIndex = index,
                TargetGuid = tile.LocalGuid, TargetIsTile = true,
                TargetTileX = tile.X, TargetTileY = tile.Y,
                StatusId = status, FallbackStatusId = e.FallbackStatus, Duration = e.Duration
            });
        }

        /// <summary>
        /// Authoritative tile-legality check: reads the status's REAL <c>StatusEffectConfig.Type</c> through
        /// <c>ICombatContext.GetStatus</c> and tests it against the game's own
        /// <c>CHARACTER_ONLY_STATUS</c> list. Independent of the id-naming convention the validator leans on.
        /// <para>An UNKNOWN status id (<c>GetStatus</c> returns null) is treated as LEGAL here and left to
        /// the native call - refusing it would silently swallow every status a caller has not registered a
        /// config for, and <c>ApplyStatus</c> already fails safe on an id absent from
        /// <c>Env.Configs.StatusEffects</c>.</para>
        /// </summary>
        private static bool IsTileIllegalStatus(ICombatContext ctx, string statusId)
        {
            if (ctx == null || string.IsNullOrEmpty(statusId)) return false;
            IStatusInfo info;
            try { info = ctx.GetStatus(statusId); }
            catch { return false; }
            if (info == null || string.IsNullOrEmpty(info.Type)) return false;
            var banned = Vocabulary.TileIllegalStatusTypes;
            for (int i = 0; i < banned.Count; i++)
                if (string.Equals(banned[i], info.Type, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// <c>Status</c> / <c>StatusOneOf</c> / <c>StatusFromSelection</c> / <c>TRIGGER_STATUS</c> resolution.
        /// <para><c>StatusOneOf</c> is <b>exactly one draw</b>, taken only when the effect actually executes —
        /// i.e. after conditions, budget and <c>ProcChance</c> have all passed and at least one target
        /// resolved (SPEC-DELTA-v1.1 §4.1 RANDOM_ELEMENT_CHOICE). The authored list is parity-hashed and
        /// identical on every peer, and <c>NextInt(0, n)</c> is exactly what
        /// <c>GameRandom.GetRandomElementFromList</c> does internally.</para>
        /// <para><c>StatusFromSelection</c> (Encounter Modifiers spec §5) is a pure per-battle state read —
        /// zero RNG. An empty/absent selection resolves to null, which the ADD_STATUS/REMOVE_STATUS caller
        /// treats as a no-op — and, with <c>Budget.ConsumeOn: EFFECT_APPLIED</c>, that no-op does not burn
        /// the budget (same "no target resolved" pattern ALLY_BY_RANK already uses, §4.3).</para>
        /// <para><b>Table-driven variant (M-EM3):</b> when <see cref="RecipeEffect.StatusFromSelectionTable"/>
        /// is also populated, the stored selection value is looked up in that table (a plain
        /// <c>modifier id → status id</c> map, generator-authored) rather than being used AS the status id
        /// directly — this is what lets <c>CombatRuntime.Selections</c> canonically hold a MODIFIER id
        /// (matching <c>ModifierReconstruction</c>'s already-shipped choice and spec §11's
        /// "activeModifierId") while an <c>ADD_STATUS</c> effect still resolves to the right STATUS id. No
        /// table authored ⇒ the original raw-echo behavior, unchanged — every pre-M-EM3 use of
        /// <c>StatusFromSelection</c> (including this suite's own generic mechanism test) keeps working
        /// exactly as before. A selection value with no row in an AUTHORED table is a fail-safe no-op
        /// (never a mis-applied status), mirroring <c>PercentFromSelectionTable</c>'s "0/absent ⇒ omitted".</para>
        /// </summary>
        private string ResolveStatus(RecipeEffect e, TriggerContext t)
        {
            if (!string.IsNullOrEmpty(e.StatusFromSelection))
            {
                string selectionValue = t.Runtime != null ? t.Runtime.GetSelection(e.StatusFromSelection) : null;
                if (string.IsNullOrEmpty(selectionValue)) return null;
                if (e.StatusFromSelectionTable != null && e.StatusFromSelectionTable.Count > 0)
                {
                    var table = e.StatusFromSelectionTable;
                    for (int i = 0; i < table.Count; i++)
                        if (string.Equals(table[i].Value, selectionValue, StringComparison.Ordinal)) return table[i].StatusId;
                    return null; // selection value has no mapped status in an AUTHORED table -- fail-safe no-op
                }
                return selectionValue; // original raw-echo semantics, unchanged, when no table is authored
            }
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
