using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Runtime
{
    /// <summary>
    /// A PLANNED effect. The dispatcher never mutates game state — it returns an ordered list of these and
    /// the Plugin unit translates each into the native call named in its subclass doc comment.
    /// <para>SPEC-DELTA-v1.1 §4 "Effect emission rule": every effect is emitted by constructing the
    /// equivalent <c>(eCombatActions, object)</c> pair and routing it through
    /// <c>CombatHelper.ApplyAction</c> (PSN §1 L1871) with the recipe's resolved
    /// <c>pOrigin</c>/<c>pTarget</c>/<c>pResults</c>. Nothing bypasses the native action pipeline.</para>
    /// </summary>
    public abstract class EngineAction
    {
        /// <summary>Recipe that planned this action.</summary>
        public string RecipeId;

        /// <summary>Owner entity guid — becomes <c>pOrigin</c> on the <c>ApplyAction</c> call.</summary>
        public string OwnerGuid;

        /// <summary>Effect index within the recipe's <c>Effects[]</c> array (authored order, §5.2 invariant 4).</summary>
        public int EffectIndex;

        public abstract string Kind { get; }

        /// <summary>
        /// Deterministic one-line rendering. Two peers replaying the same event stream must produce
        /// byte-identical <see cref="Describe"/> sequences — this is the assertion the determinism tests make.
        /// </summary>
        public abstract string Describe();

        public override string ToString() { return Describe(); }

        internal static string N(int? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        internal static string S(string v)
        {
            return string.IsNullOrEmpty(v) ? "-" : v;
        }
    }

    /// <summary>
    /// <c>ADD_STATUS</c> — SPEC-DELTA-v1.1 §4.1.
    /// <para><b>Plugin call:</b> <c>CombatHelper.ApplyAction(eCombatActions.ADD_STATUS, new AddStatusAction{...})</c>,
    /// or directly <c>InteractableHelper.ApplyStatus(...)</c> single-target overload (PSN §2 L1219) when a
    /// <see cref="Duration"/> override is present — that overload's <c>int? pDurationOverride</c> is the only
    /// duration insertion point.</para>
    /// <para><b>Fallback:</b> per §4.1 <c>IMMUNITY_FALLBACK</c>, the Plugin emits the primary status, then
    /// inspects the <c>List&lt;(eAbilityResults, object)&gt; pResults</c> list <c>ApplyAction</c> appends to;
    /// if no status-applied result was appended it emits <see cref="FallbackStatusId"/>. Result-driven,
    /// not immunity-table-driven; deterministic, no RNG. The engine cannot do this itself because it plans
    /// rather than executes — hence the field.</para>
    /// </summary>
    public sealed class AddStatusAction : EngineAction
    {
        public string TargetGuid;
        public string StatusId;
        public string FallbackStatusId;
        public int? Duration;

        public override string Kind { get { return "AddStatus"; } }

        public override string Describe()
        {
            return "AddStatus{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",status=" + S(StatusId) + ",fallback=" + S(FallbackStatusId) + ",duration=" + N(Duration) + "}";
        }
    }

    /// <summary>
    /// <c>REMOVE_STATUS</c> — SPEC-DELTA-v1.1 §4.1.
    /// <para><b>Plugin call:</b> <c>CombatHelper.ApplyAction(eCombatActions.REMOVE_STATUS, ...)</c>
    /// (equivalently <c>InteractableHelper.RemoveStatus</c>).</para>
    /// <para>Carries the WARDBOUND / OF_STABILITY redesign (§7.3): observe with <c>ON_STATUS_APPLIED</c>
    /// (a POSTfix — the status IS authoritatively applied on every peer) and then remove it, instead of
    /// EOR's forbidden prefix <c>return false</c> per-client suppression.</para>
    /// </summary>
    public sealed class RemoveStatusAction : EngineAction
    {
        public string TargetGuid;
        public string StatusId;

        public override string Kind { get { return "RemoveStatus"; } }

        public override string Describe()
        {
            return "RemoveStatus{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",status=" + S(StatusId) + "}";
        }
    }

    /// <summary>
    /// <c>STAT_CHANGE</c> — SPEC §4.6 / SPEC-DELTA-v1.1 §4.1.
    /// <para><b>Plugin call:</b> <c>CombatHelper.ApplyAction(eCombatActions.CHANGE_STAT,
    /// new ChangeStatAction{ Stat, Type, FlatValue|FlatPercent, Blockable, IsSilent })</c>.</para>
    /// <para>Also carries "gain focus" (<c>Stat:"FOC"</c>) — §4.1 explicitly declines to add a
    /// <c>FOCUS_CHANGE</c> effect because <c>FOC</c> is a legal <c>eCharacterStats</c> member (EGT §6).</para>
    /// </summary>
    public sealed class StatChangeAction : EngineAction
    {
        public string TargetGuid;
        public string Stat;
        public string StatChangeType;
        public int? FlatValue;
        public int? FlatPercent;
        public bool Blockable;
        public bool IsSilent;

        public override string Kind { get { return "StatChange"; } }

        public override string Describe()
        {
            return "StatChange{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",stat=" + S(Stat) + ",type=" + S(StatChangeType) + ",flat=" + N(FlatValue) +
                   ",pct=" + N(FlatPercent) + ",blockable=" + (Blockable ? "1" : "0") +
                   ",silent=" + (IsSilent ? "1" : "0") + "}";
        }
    }

    /// <summary>
    /// <c>SUMMON</c> — SPEC-DELTA-v1.1 OQ#4.
    /// <para><b>Plugin call:</b> one <c>CombatHelper.ApplyAction(eCombatActions.ADD_CHARACTER,
    /// new AddCharacterAction{ Type = eSummonTypes.&lt;SummonType&gt;, Value = CharacterConfig })</c> per action.
    /// <c>AddCharacterAction</c> has <b>no count field</b> and <c>CombatHelper.TryCreateSummon</c> returns a
    /// single <c>out Entity</c> — so authored <c>Count</c> is expanded here into N sequential actions,
    /// <see cref="Index"/> = 0..Count-1 ascending, each needing its own freshly deserialized payload.</para>
    /// </summary>
    public sealed class SummonAction : EngineAction
    {
        public string TargetGuid;
        /// <summary>True when authored as <c>TRIGGER_TARGET_POSITION</c> — the Plugin passes the target's tile
        /// rather than the target entity.</summary>
        public bool UseTargetPosition;
        public SummonType SummonType;
        public string CharacterConfig;
        public int Index;

        public override string Kind { get { return "Summon"; } }

        public override string Describe()
        {
            return "Summon{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",pos=" + (UseTargetPosition ? "1" : "0") + ",type=" + SummonType + ",config=" +
                   S(CharacterConfig) + ",i=" + Index.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    /// <summary>
    /// <c>ROLL_STAT_BONUS</c> (E1) — SPEC-DELTA-v1.1 §4.2.
    /// <para><b>Plugin call:</b> inside the <c>CombatHelper.PerformAbility</c> <b>Prefix</b> (PSN §1 L1155),
    /// replace the <c>Func&lt;Entity,string,eGetStatEquippedFilters,int&gt; pGetStat</c> and/or
    /// <c>Func&lt;Entity,string,int&gt; pGetTileStat</c> delegate parameters with a wrapper adding
    /// <see cref="FlatDelta"/> / <see cref="PercentDelta"/> when
    /// <c>(entity == pOrigin &amp;&amp; statKey == Stat)</c>, for this call only. Reverted implicitly — the
    /// wrapper's lifetime is the single <c>PerformAbility</c> invocation. No status, no persistent state,
    /// no RNG. <c>ON_ABILITY_DECLARED</c> only.</para>
    /// </summary>
    public sealed class RollStatBonusAction : EngineAction
    {
        public string TargetGuid;
        public string Stat;
        public int FlatDelta;
        public int PercentDelta;
        /// <summary>Floor on the delta the wrapper applies. Travels to the Plugin because the percent is
        /// resolved against the base stat inside the delegate wrapper, where the base value is known.</summary>
        public int? MinDelta;
        /// <summary>Always <c>"ABILITY_ROLL"</c> in v1.1 — the wrapper lives for exactly one PerformAbility call.</summary>
        public string Window = "ABILITY_ROLL";

        public override string Kind { get { return "RollStatBonus"; } }

        public override string Describe()
        {
            return "RollStatBonus{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",stat=" + S(Stat) + ",flat=" + FlatDelta.ToString(CultureInfo.InvariantCulture) +
                   ",pct=" + PercentDelta.ToString(CultureInfo.InvariantCulture) + ",min=" + N(MinDelta) +
                   ",window=" + S(Window) + "}";
        }
    }

    /// <summary>
    /// <c>HEAL_MODIFIER</c> (E2) — SPEC-DELTA-v1.1 §4.2.
    /// <para><b>Plugin call:</b> inside the <c>CharacterHelper.AddHealth(Entity, ref int pValue, ...)</c>
    /// <b>Prefix</b> (PSN §3 L1342/L1357), mutate <c>ref int pValue</c> by
    /// <c>max(MinDelta, Flat + ceil(pValue * Percent/100))</c>. <c>Scope: GIVEN</c> matches on the healer
    /// identity captured by the paired <c>ApplyStatChange</c> prefix; <c>RECEIVED</c> matches on
    /// <c>pEntity</c>. <c>ON_HEAL_PENDING</c> only.</para>
    /// <para><b>No RNG permitted on this path</b> — the validator rejects a chance-gated heal modifier
    /// (§2 T8 authority note, §4.2 E2).</para>
    /// </summary>
    public sealed class HealModifierAction : EngineAction
    {
        public string TargetGuid;
        public HealScope Scope;
        public int FlatDelta;
        public int PercentDelta;
        public int? MinDelta;

        public override string Kind { get { return "HealModifier"; } }

        public override string Describe()
        {
            return "HealModifier{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",scope=" + Scope + ",flat=" + FlatDelta.ToString(CultureInfo.InvariantCulture) +
                   ",pct=" + PercentDelta.ToString(CultureInfo.InvariantCulture) + ",min=" + N(MinDelta) + "}";
        }
    }

    /// <summary>
    /// <c>COUNTER_ADD</c> (E3) — SPEC-DELTA-v1.1 §4.2.
    /// <para><b>Plugin call: none.</b> This is a pure per-battle state write (§6) that the engine has
    /// ALREADY performed; the action is emitted for logging/parity-audit only. <c>[LOCAL]</c> state,
    /// <c>[SYNCED]</c> cause — never transmitted, never read by any peer but its own. Replaces EOR's
    /// <c>ClassSkillRuntimeState.Stacks</c> for BARD/WARRIOR.</para>
    /// </summary>
    public sealed class CounterAddAction : EngineAction
    {
        public string CounterName;
        public int Delta;
        public int NewValue;

        public override string Kind { get { return "CounterAdd"; } }

        public override string Describe()
        {
            return "CounterAdd{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",name=" + S(CounterName) +
                   ",delta=" + Delta.ToString(CultureInfo.InvariantCulture) +
                   ",value=" + NewValue.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    /// <summary>
    /// <c>COUNTER_SET</c> (E4) — SPEC-DELTA-v1.1 §4.2. Same posture as <see cref="CounterAddAction"/>.
    /// <para><b>Plugin call: none</b> — per-battle state write, already applied by the engine.</para>
    /// </summary>
    public sealed class CounterSetAction : EngineAction
    {
        public string CounterName;
        public int NewValue;

        public override string Kind { get { return "CounterSet"; } }

        public override string Describe()
        {
            return "CounterSet{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",name=" + S(CounterName) +
                   ",value=" + NewValue.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    /// <summary>Helpers for turning an action plan into a stable comparable log.</summary>
    public static class ActionLog
    {
        public static string Render(IReadOnlyList<EngineAction> actions)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < actions.Count; i++)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(actions[i].Describe());
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
