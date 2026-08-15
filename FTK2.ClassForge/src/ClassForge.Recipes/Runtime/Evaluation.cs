using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Runtime
{
    /// <summary>
    /// The resolved facts one trigger firing makes available to conditions, targets and effects.
    /// Each field is populated ONLY from a parameter SPEC-DELTA-v1.1 §2 names for that trigger — a
    /// condition can never read something the verified hook does not carry.
    /// </summary>
    public sealed class TriggerContext
    {
        public ICombatContext Ctx;
        public CombatRuntime Runtime;
        public TriggerKind Trigger;

        /// <summary>Diagnostic sink (may be null) — needed by STATE_HASH_CHANCE's §3.3 resolver-throw
        /// warn path. Populated by the dispatcher from its own log; carries no gameplay meaning.</summary>
        public IRecipeLog Log;

        /// <summary>The recipe owner. Which hook parameter this is depends on the trigger (§2).</summary>
        public ICombatEntity Owner;

        /// <summary>Bound to the <c>TRIGGER_TARGET</c> / <c>TRIGGER_TARGET_POSITION</c> tokens.</summary>
        public ICombatEntity TriggerTarget;

        /// <summary>Bound to the <c>TRIGGER_SOURCE</c> token (§4.3): the attacker under
        /// <c>ON_DAMAGE_TAKEN</c>, the acting enemy under <c>ON_ENEMY_ABILITY_RESOLVED</c>, the status
        /// applier under <c>ON_STATUS_APPLIED</c>, the healer under <c>ON_HEAL_PENDING</c>.</summary>
        public ICombatEntity TriggerSource;

        public string AbilityId;
        public RollTier Roll = RollTier.SUCCESS;
        public bool HasRoll;
        public int FocusUsed;

        /// <summary>Bound to the <c>TRIGGER_STATUS</c> token under <c>ON_STATUS_APPLIED</c>.</summary>
        public string StatusId;

        public string ItemConfigName;

        /// <summary>Damage/heal magnitude carried by the trigger, when it has one.</summary>
        public int Amount;

        /// <summary>Bound to <c>COMBAT_START_REAL</c> — <c>CombatStartEvent.TrySkillProc</c>, valid only
        /// under <c>ON_COMBAT_START</c> (Encounter Modifiers spec §4.3/§5).</summary>
        public bool CombatStartReal;
    }

    /// <summary>
    /// Condition evaluation — SPEC-DELTA-v1.1 §3. Conditions are ANDed; an empty array is always true.
    /// <para>Universal <c>Negate</c> inverts the result. Universal <c>Of</c> chooses which entity is read.</para>
    /// <para><b>No condition consumes RNG</b> (§3, §5.3 posture table) and every read is of replicated
    /// state or a hook parameter — this is what makes conditions <c>[SYNCED]</c> with no authority question.</para>
    /// </summary>
    public static class ConditionEvaluator
    {
        /// <summary>ANDs the list. <paramref name="selfOverride"/> re-binds <c>Of: SELF</c> to a ranking
        /// candidate for <c>ALLY_BY_RANK.Rank.Where</c> (§4.3).</summary>
        public static bool EvaluateAll(IReadOnlyList<RecipeCondition> conditions, TriggerContext t, ICombatEntity selfOverride)
        {
            if (conditions == null) return true;
            for (int i = 0; i < conditions.Count; i++)
                if (!Evaluate(conditions[i], t, selfOverride)) return false;
            return true;
        }

        public static bool Evaluate(RecipeCondition c, TriggerContext t, ICombatEntity selfOverride)
        {
            bool raw = EvaluateRaw(c, t, selfOverride);
            return c.Negate ? !raw : raw;
        }

        private static ICombatEntity Resolve(OfSelector of, TriggerContext t, ICombatEntity selfOverride)
        {
            switch (of)
            {
                case OfSelector.TRIGGER_TARGET: return t.TriggerTarget;
                case OfSelector.TRIGGER_SOURCE: return t.TriggerSource;
                default: return selfOverride ?? t.Owner;
            }
        }

        private static bool EvaluateRaw(RecipeCondition c, TriggerContext t, ICombatEntity selfOverride)
        {
            var e = Resolve(c.Of, t, selfOverride);
            switch (c.Type)
            {
                case ConditionKind.HP_THRESHOLD:
                {
                    if (e == null) return false;
                    if (c.Percent.HasValue)
                    {
                        int max = e.GetStat("MXHP");
                        int pct = max > 0 ? (e.GetStat("HP") * 100) / max : 0;
                        return Vocabulary.Compare(c.Comparator, pct, c.Percent.Value);
                    }
                    return Vocabulary.Compare(c.Comparator, e.GetStat("HP"), c.Flat.HasValue ? c.Flat.Value : 0);
                }

                case ConditionKind.HAS_STATUS:
                    return e != null && HasStatus(e, c.Value);

                case ConditionKind.LACKS_STATUS:
                    return e != null && !HasStatus(e, c.Value);

                case ConditionKind.ROW:
                    return e != null && string.Equals(e.Row.ToString(), c.Value, StringComparison.Ordinal);

                case ConditionKind.WEAPON_CLASS:
                    return e != null && string.Equals(e.WeaponClass, c.Value, StringComparison.Ordinal);

                case ConditionKind.CHARACTER_TYPE:
                    return e != null && string.Equals(e.CharacterType, c.Value, StringComparison.Ordinal);

                case ConditionKind.TARGET_BASE_TYPE:
                    return t.TriggerTarget != null &&
                           string.Equals(t.TriggerTarget.BaseType, c.Value, StringComparison.Ordinal);

                case ConditionKind.ABILITY_TAG:
                {
                    var a = Ability(t);
                    if (a == null || a.Tags == null) return false;
                    for (int i = 0; i < a.Tags.Count; i++)
                        if (string.Equals(a.Tags[i], c.Value, StringComparison.Ordinal)) return true;
                    return false;
                }

                case ConditionKind.ROLL_TIER:
                    return Vocabulary.Compare(c.Comparator, (int)t.Roll, (int)c.Tier);

                case ConditionKind.HOSTILE_ACTION:
                {
                    var a = Ability(t);
                    bool hostile = a != null && a.TargetsEnemy && t.TriggerTarget != null && t.Owner != null &&
                                   t.Ctx.AreOpponents(t.Owner, t.TriggerTarget);
                    return hostile == Want(c);
                }

                case ConditionKind.ABILITY_STAT:
                {
                    var a = Ability(t);
                    return a != null && string.Equals(a.Stat, c.Value, StringComparison.Ordinal);
                }

                case ConditionKind.ABILITY_RANGED:
                {
                    var a = Ability(t);
                    return a != null && a.IsRanged == Want(c);
                }

                case ConditionKind.ABILITY_REPEATED:
                {
                    if (t.Owner == null || string.IsNullOrEmpty(t.AbilityId)) return false;
                    var ts = t.Runtime.GetTurnState(t.Owner.Guid);
                    bool repeated = !string.IsNullOrEmpty(ts.LastAbilityId) &&
                                    string.Equals(ts.LastAbilityId, t.AbilityId, StringComparison.Ordinal);
                    return repeated == Want(c);
                }

                case ConditionKind.FOCUS_SPENT:
                    return Vocabulary.Compare(c.Comparator, t.FocusUsed, c.ValueInt.HasValue ? c.ValueInt.Value : 0);

                case ConditionKind.FOCUS_CURRENT:
                {
                    if (e == null) return false;
                    int want = string.Equals(c.Value, Vocabulary.FocusMaxToken, StringComparison.Ordinal)
                        ? e.GetStat("MXFOC")
                        : (c.ValueInt.HasValue ? c.ValueInt.Value : 0);
                    return Vocabulary.Compare(c.Comparator, e.GetStat("FOC"), want);
                }

                case ConditionKind.STATUS_COUNT:
                {
                    if (e == null) return false;
                    int n = CountStatuses(t.Ctx, e, c.Category, c.Types);
                    return Vocabulary.Compare(c.Comparator, n, c.ValueInt.HasValue ? c.ValueInt.Value : 0);
                }

                case ConditionKind.STATUS_TYPE:
                {
                    if (string.IsNullOrEmpty(t.StatusId)) return false;
                    var info = t.Ctx.GetStatus(t.StatusId);
                    return info != null && string.Equals(info.Type, c.Value, StringComparison.Ordinal);
                }

                case ConditionKind.COUNTER:
                {
                    if (t.Owner == null) return false;
                    int v = t.Runtime.GetCounter(t.Owner.Guid, c.Name);
                    return Vocabulary.Compare(c.Comparator, v, c.ValueInt.HasValue ? c.ValueInt.Value : 0);
                }

                case ConditionKind.MOVED_THIS_ROUND:
                {
                    if (e == null) return false;
                    var ts = t.Runtime.GetTurnState(e.Guid);
                    return (ts.MovedRound == t.Runtime.Round) == Want(c);
                }

                case ConditionKind.ALL_ALLIES_ACTED:
                {
                    if (t.Owner == null) return false;
                    var allies = EntitySets.Allies(t.Ctx, t.Owner, true);
                    bool all = true;
                    for (int i = 0; i < allies.Count; i++)
                    {
                        if (string.Equals(allies[i].Guid, t.Owner.Guid, StringComparison.Ordinal)) continue;
                        if (t.Runtime.GetTurnState(allies[i].Guid).ActedRound != t.Runtime.Round) { all = false; break; }
                    }
                    return all == Want(c);
                }

                case ConditionKind.ITEM_CLASS:
                {
                    var item = Item(t);
                    return item != null && string.Equals(item.Class, c.Value, StringComparison.Ordinal);
                }

                case ConditionKind.ITEM_CONSUMABLE:
                {
                    var item = Item(t);
                    return item != null && item.IsConsumable == Want(c);
                }

                // --- Encounter Modifiers spec §5 (v1.2, M-EM2) — all pure reads, no RNG ---

                case ConditionKind.PARTY_AVG_LEVEL:
                    return t.Ctx != null &&
                           Vocabulary.Compare(c.Comparator, t.Ctx.PartyAverageLevel, c.ValueInt.HasValue ? c.ValueInt.Value : 0);

                case ConditionKind.IS_DUNGEON:
                    return t.Ctx != null && t.Ctx.IsDungeon == Want(c);

                case ConditionKind.BOSS_FIGHT:
                    return t.Ctx != null && t.Ctx.IsBossFight == Want(c);

                case ConditionKind.ENCOUNTER_PROPERTY:
                    return t.Ctx != null && t.Ctx.HasEncounterProperty(c.Value);

                case ConditionKind.ENTITY_TAG:
                    return e != null && e.HasTag(c.Value);

                case ConditionKind.CONFIG_NAME_CONTAINS:
                    return e != null && !string.IsNullOrEmpty(e.ConfigName) && !string.IsNullOrEmpty(c.Value) &&
                           e.ConfigName.IndexOf(c.Value, StringComparison.OrdinalIgnoreCase) >= 0;

                case ConditionKind.COMBAT_START_REAL:
                    return t.CombatStartReal == Want(c);

                case ConditionKind.SELECTION_PRESENT:
                    return t.Runtime != null && !string.IsNullOrEmpty(t.Runtime.GetSelection(c.Name)) == Want(c);

                // GATE D: enemy-side gate via CharacterHelper.IsEnemy semantics (GroupIndex == 1), never
                // CHARACTER_TYPE (proven unable to express it).
                case ConditionKind.IS_ENEMY:
                    return e != null && e.IsEnemy == Want(c);

                // v1.3 — state-hash-chance spec §2: a deterministic, draw-free verdict over declared
                // replicated state. NOT a roll; correlated across identical re-evaluations (spec §6).
                case ConditionKind.STATE_HASH_CHANCE:
                    return StateHashChance.Evaluate(c, t);

                default:
                    return false;
            }
        }

        private static bool Want(RecipeCondition c)
        {
            return c.ValueBool.HasValue ? c.ValueBool.Value : true;
        }

        private static IAbilityInfo Ability(TriggerContext t)
        {
            if (t.Ctx == null || string.IsNullOrEmpty(t.AbilityId)) return null;
            return t.Ctx.GetAbility(t.AbilityId);
        }

        private static IItemInfo Item(TriggerContext t)
        {
            if (t.Ctx == null || string.IsNullOrEmpty(t.ItemConfigName)) return null;
            return t.Ctx.GetItem(t.ItemConfigName);
        }

        private static bool HasStatus(ICombatEntity e, string statusId)
        {
            var s = e.Statuses;
            if (s == null) return false;
            for (int i = 0; i < s.Count; i++)
                if (string.Equals(s[i], statusId, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Counts statuses on <paramref name="e"/> matching a category or an explicit type list.
        /// The HARMFUL set is the authored constant in <see cref="Vocabulary.HarmfulStatusTypes"/>
        /// (SPEC-DELTA-v1.1 §3.2 C9; §9 risk 5 requires it live in exactly one place).
        /// </summary>
        public static int CountStatuses(ICombatContext ctx, ICombatEntity e, StatusCategory category, IReadOnlyList<string> types)
        {
            var list = e.Statuses;
            if (list == null) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var info = ctx.GetStatus(list[i]);
                string type = info == null ? null : info.Type;
                if (types != null && types.Count > 0)
                {
                    if (type == null) continue;
                    bool inList = false;
                    for (int j = 0; j < types.Count; j++)
                        if (string.Equals(types[j], type, StringComparison.Ordinal)) { inList = true; break; }
                    if (inList) n++;
                    continue;
                }
                switch (category)
                {
                    case StatusCategory.ANY: n++; break;
                    case StatusCategory.HARMFUL: if (type != null && IsHarmful(type)) n++; break;
                    default: if (type != null && !IsHarmful(type)) n++; break;
                }
            }
            return n;
        }

        private static bool IsHarmful(string type)
        {
            var h = Vocabulary.HarmfulStatusTypes;
            for (int i = 0; i < h.Count; i++)
                if (string.Equals(h[i], type, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// Deterministic entity-set helpers. Every list returned here is sorted by ordinal
    /// <see cref="ICombatEntity.Guid"/> — SPEC-DELTA-v1.1 §5.2 invariant 4: "Never Dictionary/HashSet
    /// enumeration order, never filesystem order."
    /// </summary>
    public static class EntitySets
    {
        private static int ByGuid(ICombatEntity a, ICombatEntity b)
        {
            return string.CompareOrdinal(a.Guid, b.Guid);
        }

        public static List<ICombatEntity> Allies(ICombatContext ctx, ICombatEntity of, bool includeSelf)
        {
            var result = new List<ICombatEntity>();
            if (ctx == null || of == null) return result;
            var all = ctx.Entities;
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (e == null || !e.IsAlive) continue;
                if (ctx.AreOpponents(of, e)) continue;
                if (!includeSelf && string.Equals(e.Guid, of.Guid, StringComparison.Ordinal)) continue;
                result.Add(e);
            }
            result.Sort(ByGuid);
            return result;
        }

        public static List<ICombatEntity> Opponents(ICombatContext ctx, ICombatEntity of)
        {
            var result = new List<ICombatEntity>();
            if (ctx == null || of == null) return result;
            var all = ctx.Entities;
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (e == null || !e.IsAlive) continue;
                if (!ctx.AreOpponents(of, e)) continue;
                result.Add(e);
            }
            result.Sort(ByGuid);
            return result;
        }

        public static List<ICombatEntity> SortedByGuid(IEnumerable<ICombatEntity> source)
        {
            var result = new List<ICombatEntity>(source);
            result.Sort(ByGuid);
            return result;
        }
    }

    /// <summary>
    /// <c>Target</c> token → ordered entity list — SPEC-DELTA-v1.1 §4.3.
    /// <para><c>ALLY_BY_RANK</c> sorts candidates by <c>Rank.Stat</c> and <b>always</b> breaks ties by
    /// ordinal <c>Entity.Guid</c>: fully deterministic, zero RNG. EOR itself uses
    /// <c>.OrderBy(SPD).ThenBy(Guid)</c> for exactly this reason (BM CHRONOMANCER).</para>
    /// </summary>
    public static class TargetResolver
    {
        public static List<ICombatEntity> Resolve(RecipeEffect effect, TriggerContext t)
        {
            var result = new List<ICombatEntity>();
            switch (effect.Target)
            {
                case TargetKind.SELF:
                case TargetKind.CASTER:
                    if (t.Owner != null) result.Add(t.Owner);
                    break;

                case TargetKind.TRIGGER_TARGET:
                case TargetKind.TRIGGER_TARGET_POSITION:
                    if (t.TriggerTarget != null) result.Add(t.TriggerTarget);
                    break;

                case TargetKind.TRIGGER_SOURCE:
                    if (t.TriggerSource != null) result.Add(t.TriggerSource);
                    break;

                case TargetKind.ALLY_ALL:
                    result.AddRange(EntitySets.Allies(t.Ctx, t.Owner, true));
                    break;

                case TargetKind.ALLY_ALL_OTHERS:
                    result.AddRange(EntitySets.Allies(t.Ctx, t.Owner, false));
                    break;

                case TargetKind.ENEMY_ALL:
                    result.AddRange(EntitySets.Opponents(t.Ctx, t.Owner));
                    break;

                case TargetKind.ALLY_BY_RANK:
                {
                    var pick = ResolveByRank(effect.Rank, t);
                    if (pick != null) result.Add(pick);
                    break;
                }
            }
            return result;
        }

        private static ICombatEntity ResolveByRank(RankSpec rank, TriggerContext t)
        {
            if (rank == null) return null;
            var candidates = EntitySets.Allies(t.Ctx, t.Owner, !rank.ExcludeSelf);
            ICombatEntity best = null;
            int bestValue = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var cand = candidates[i];
                // `Where` uses the same condition evaluator with `Of: SELF` bound to the candidate (§4.3).
                if (!ConditionEvaluator.EvaluateAll(rank.Where, t, cand)) continue;
                int v = RankValue(cand, rank.Stat);
                if (best == null ||
                    (rank.Order == RankOrder.LOWEST && v < bestValue) ||
                    (rank.Order == RankOrder.HIGHEST && v > bestValue))
                {
                    best = cand;
                    bestValue = v;
                }
                // Ties keep the earlier candidate, and `candidates` is already ordinal-Guid ascending —
                // that is the "then by Entity.Guid ordinal ascending" tiebreak.
            }
            return best;
        }

        private static int RankValue(ICombatEntity e, string stat)
        {
            if (string.Equals(stat, "HP_PCT", StringComparison.Ordinal))
            {
                int max = e.GetStat("MXHP");
                return max > 0 ? (e.GetStat("HP") * 100) / max : 0;
            }
            return e.GetStat(stat);
        }
    }

    /// <summary>
    /// Dynamic value sources for <c>FlatValueFrom</c> / <c>PercentFrom</c> — SPEC-DELTA-v1.1 §4.1.
    /// All four sources are pure reads; none consumes RNG.
    /// </summary>
    public static class ValueSources
    {
        /// <summary>Resolves the raw source magnitude, then applies <c>PerUnit</c>, <c>Min</c> and <c>Max</c>.
        /// <c>TARGET_MXHP_PCT</c> (GATE C) is the one source that already returns a fully-formed, signed,
        /// floor-at-1 flat value — <c>PerUnit</c> defaults to 1 so it passes through unchanged unless an
        /// author deliberately overrides it.</summary>
        public static int Resolve(string token, RecipeEffect effect, TriggerContext t)
        {
            int raw = Raw(token, effect, t);
            int v = raw * (effect.PerUnit.HasValue ? effect.PerUnit.Value : 1);
            if (effect.Min.HasValue && v < effect.Min.Value) v = effect.Min.Value;
            if (effect.Max.HasValue && v > effect.Max.Value) v = effect.Max.Value;
            return v;
        }

        private static int Raw(string token, RecipeEffect effect, TriggerContext t)
        {
            if (string.IsNullOrEmpty(token)) return 0;
            if (string.Equals(token, Vocabulary.SourceFocusSpent, StringComparison.Ordinal))
                return t.FocusUsed;
            if (string.Equals(token, Vocabulary.SourceTargetHpPct, StringComparison.Ordinal))
            {
                var e = t.TriggerTarget;
                if (e == null) return 0;
                int max = e.GetStat("MXHP");
                return max > 0 ? (e.GetStat("HP") * 100) / max : 0;
            }
            if (string.Equals(token, Vocabulary.SourceTargetMxhpPct, StringComparison.Ordinal))
                return RawTargetMxhpPct(effect, t);
            if (token.StartsWith(Vocabulary.SourceCounterPrefix, StringComparison.Ordinal))
            {
                if (t.Owner == null) return 0;
                return t.Runtime.GetCounter(t.Owner.Guid, token.Substring(Vocabulary.SourceCounterPrefix.Length));
            }
            if (token.StartsWith(Vocabulary.SourceStatusCountPrefix, StringComparison.Ordinal))
            {
                if (t.Owner == null) return 0;
                string cat = token.Substring(Vocabulary.SourceStatusCountPrefix.Length);
                StatusCategory category = StatusCategory.ANY;
                if (string.Equals(cat, "HARMFUL", StringComparison.Ordinal)) category = StatusCategory.HARMFUL;
                else if (string.Equals(cat, "BENEFICIAL", StringComparison.Ordinal)) category = StatusCategory.BENEFICIAL;
                return ConditionEvaluator.CountStatuses(t.Ctx, t.Owner, category, null);
            }
            return 0;
        }

        /// <summary>
        /// GATE C (binding gate resolution): STAT_CHANGE with Stat "MXHP" + FlatPercent throws natively
        /// (<c>InteractableHelper.GetStatChangePercentValue</c> only handles "HP"/"XP"). This is the
        /// engine-computed replacement: reads the effect's authored <c>Percent</c> and
        /// <c>TRIGGER_TARGET.MXHP</c>, then applies EOR's own rounding (L22663-4) —
        /// <c>flat = sign(Percent) * max(1, round(|targetMaxHp * Percent| / 100))</c> — so the emitted
        /// action is a plain <c>FlatValue</c> STAT_CHANGE on Stat "MXHP" through the native verb, never a
        /// FlatPercent one. <c>Percent == 0</c> or no resolvable target/MXHP ⇒ 0 (no-op delta).
        /// <para>Encounter Modifiers spec §6.1 "PercentFromSelection" sugar (M-EM3): when the effect
        /// carries no authored <see cref="RecipeEffect.Percent"/> but does carry
        /// <see cref="RecipeEffect.PercentFromSelection"/>, the percent is instead looked up — by the
        /// CURRENTLY STORED <c>CombatRuntime.Selections</c> value under that name — in
        /// <see cref="RecipeEffect.PercentFromSelectionTable"/>. A missing selection or a selection value
        /// with no table row resolves to percent 0, which (via the <c>pct == 0</c> early-out below) is a
        /// no-op delta exactly like an authored <c>Percent: 0</c> — i.e. the "0/absent ⇒ omitted" rule.</para>
        /// </summary>
        private static int RawTargetMxhpPct(RecipeEffect effect, TriggerContext t)
        {
            var e = t.TriggerTarget;
            if (e == null) return 0;
            int pct = effect.Percent.HasValue
                ? effect.Percent.Value
                : (!string.IsNullOrEmpty(effect.PercentFromSelection) ? ResolvePercentFromSelection(effect, t) : 0);
            if (pct == 0) return 0;
            int maxHp = e.GetStat("MXHP");
            if (maxHp <= 0) return 0;
            decimal magnitudeRaw = Math.Abs((decimal)maxHp * pct) / 100m;
            int magnitude = (int)Math.Max(1m, Math.Round(magnitudeRaw, MidpointRounding.AwayFromZero));
            return pct < 0 ? -magnitude : magnitude;
        }

        /// <summary>Table lookup for <see cref="RecipeEffect.PercentFromSelection"/> — a pure per-battle
        /// state read (<c>CombatRuntime.Selections</c>), zero RNG. Public so tests and the STAT_CHANGE
        /// "omit the whole effect on 0" check (<c>RecipeDispatcher.PlanEffect</c>) can call it without
        /// re-deriving <see cref="RawTargetMxhpPct"/>'s full rounding.</summary>
        public static int ResolvePercentFromSelection(RecipeEffect effect, TriggerContext t)
        {
            if (effect == null || t == null || t.Runtime == null) return 0;
            if (string.IsNullOrEmpty(effect.PercentFromSelection) || effect.PercentFromSelectionTable == null) return 0;
            string selectionValue = t.Runtime.GetSelection(effect.PercentFromSelection);
            if (string.IsNullOrEmpty(selectionValue)) return 0;
            var table = effect.PercentFromSelectionTable;
            for (int i = 0; i < table.Count; i++)
                if (string.Equals(table[i].Value, selectionValue, StringComparison.Ordinal)) return table[i].Percent;
            return 0;
        }
    }
}
