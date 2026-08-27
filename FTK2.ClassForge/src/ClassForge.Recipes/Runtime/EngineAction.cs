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
        /// <summary>The entity this status lands on. For a <c>RANDOM_TILE</c> action this is the LOCAL guid
        /// of the drawn tile entity (<see cref="TargetIsTile"/>), which the executor feeds straight to
        /// <c>NativeByGuid</c> exactly as it does for a character — tile entities live in
        /// <c>CombatState.Entities</c> alongside characters (CombatPhase.cs:314), so no new resolution path
        /// is needed. It is a local identity and is deliberately NOT rendered by <see cref="Describe"/>.</summary>
        public string TargetGuid;
        public string StatusId;
        public string FallbackStatusId;
        public int? Duration;

        /// <summary>True when this action targets a BOARD TILE rather than a combatant (v1.4
        /// <c>RANDOM_TILE</c>). Switches <see cref="Describe"/> onto the peer-identical
        /// <c>(TargetTileX, TargetTileY)</c> board coordinate.</summary>
        public bool TargetIsTile;

        /// <summary>Board column of the targeted tile — meaningful only when <see cref="TargetIsTile"/>.</summary>
        public int TargetTileX;

        /// <summary>Board row-line of the targeted tile — meaningful only when <see cref="TargetIsTile"/>.</summary>
        public int TargetTileY;

        public override string Kind { get { return "AddStatus"; } }

        /// <summary>
        /// Tile targets render as <c>tile(x,y)</c>, NEVER as the tile entity's guid. The determinism suite's
        /// contract is that two peers replaying the same event stream produce byte-identical
        /// <c>Describe</c> sequences; a tile entity's Guid is local object identity and would break that
        /// even when both peers correctly chose the SAME board square. The coordinate is the peer-identical
        /// name for the square, so it is the one that goes in the log.
        /// </summary>
        public override string Describe()
        {
            return "AddStatus{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + DescribeTarget() +
                   ",status=" + S(StatusId) + ",fallback=" + S(FallbackStatusId) + ",duration=" + N(Duration) + "}";
        }

        private string DescribeTarget()
        {
            if (!TargetIsTile) return S(TargetGuid);
            return "tile(" + TargetTileX.ToString(CultureInfo.InvariantCulture) + "," +
                   TargetTileY.ToString(CultureInfo.InvariantCulture) + ")";
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

        /// <summary>v1.5 — the authored <c>ITEM_CUSTOM_DATA:&lt;ThingConfigId&gt;:&lt;Key&gt;</c> token when the
        /// config is resolved at execution time instead of being authored. Null for every static summon,
        /// which is every summon that existed before v1.5.
        /// <para>The engine deliberately does NOT resolve it: reading an item's <c>Thing.CustomData</c>
        /// requires the game <c>Entity</c>, which the pure-C# core never sees. The Plugin resolves it, and a
        /// failure to resolve is a logged no-op there.</para></summary>
        public string CharacterConfigFrom;

        public override string Kind { get { return "Summon"; } }

        public override string Describe()
        {
            return "Summon{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",pos=" + (UseTargetPosition ? "1" : "0") + ",type=" + SummonType + ",config=" +
                   S(CharacterConfig) +
                   // Appended ONLY when a dynamic source is authored, so every plan string that existed
                   // before v1.5 stays byte-identical -- Describe() is the parity/determinism rendering.
                   (string.IsNullOrEmpty(CharacterConfigFrom) ? "" : ",from=" + S(CharacterConfigFrom)) +
                   ",i=" + Index.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    /// <summary>
    /// <c>CAPTURE</c> (v1.5) — store the target's character config on an inventory item, then take the
    /// target off the board.
    /// <para><b>Plugin call:</b> (1) resolve the OWNER's <see cref="IntoItem"/> Thing out of
    /// <c>CharacterComponent.Things</c>; (2) run the eligibility gate; (3)
    /// <c>CoreHelper.SetCustomData(thing, IntoKey, targetConfigName)</c>; (4) push
    /// <c>(eAbilityResults.PLAYTHINGED, targetEntity)</c> into the ability's results list, the only route to
    /// <c>CombatPhase</c>'s local <c>removeFromCombat</c> + <c>_checkChargeRetargets()</c>
    /// (CombatPhase.cs:4329-4338).</para>
    /// <para><b>Zero random draws.</b> Nothing on this path rolls: the PERFECT gate is an authored
    /// <c>ROLL_TIER</c> condition read off the hook's own <c>pRollData</c>, and every eligibility test is a
    /// pure read of replicated config data.</para>
    /// </summary>
    public sealed class CaptureAction : EngineAction
    {
        /// <summary>The combatant being captured.</summary>
        public string TargetGuid;
        /// <summary>ThingConfig id of the owner's carried item that receives the record.</summary>
        public string IntoItem;
        /// <summary><c>Thing.CustomData</c> key written on that item.</summary>
        public string IntoKey;

        public override string Kind { get { return "Capture"; } }

        public override string Describe()
        {
            return "Capture{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",item=" + S(IntoItem) + ",key=" + S(IntoKey) + "}";
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
    /// <c>DAMAGE_TAKEN_MULT</c> (v1.3, state-hash-chance spec M-SH3) — the retired SPEC-DELTA §7.4 park.
    /// <para><b>Plugin call:</b> mutate <c>CalculateFinalDamage</c>'s <c>ref int __result</c> by
    /// <c>delta = sign(Percent) * max(MinDelta, ceil(result * |Percent| / 100))</c>, flooring the result
    /// at 0 — EOR 0.7.0.62's SHIELDBEARER arithmetic verbatim (Plugin.cs L26733). A postfix mutating a
    /// return value is NOT suppression; SPEC-DELTA §5.2 invariant 6 is not engaged (spec §5.1).</para>
    /// <para><b>No RNG permitted on this path</b> — the validator rejects a chance-gated
    /// <c>ON_DAMAGE_PENDING</c> recipe outright; the only legal gate is <c>STATE_HASH_CHANCE</c>.</para>
    /// </summary>
    public sealed class DamageTakenMultAction : EngineAction
    {
        public string TargetGuid;
        public int Percent;
        public int? MinDelta;

        public override string Kind { get { return "DamageTakenMult"; } }

        public override string Describe()
        {
            return "DamageTakenMult{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",target=" + S(TargetGuid) +
                   ",pct=" + Percent.ToString(CultureInfo.InvariantCulture) + ",min=" + N(MinDelta) + "}";
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

        /// <summary>True when the authored effect carried <c>Persistent: true</c> — the executor writes
        /// <see cref="NewValue"/> through to the owner's per-character store in addition to the (already
        /// applied) in-memory write (§6 run-persistence escape hatch).</summary>
        public bool Persistent;

        public override string Kind { get { return "CounterAdd"; } }

        public override string Describe()
        {
            return "CounterAdd{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",name=" + S(CounterName) +
                   ",delta=" + Delta.ToString(CultureInfo.InvariantCulture) +
                   ",value=" + NewValue.ToString(CultureInfo.InvariantCulture) +
                   ",persistent=" + Persistent.ToString(CultureInfo.InvariantCulture) + "}";
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

        /// <summary>Same posture as <see cref="CounterAddAction.Persistent"/>.</summary>
        public bool Persistent;

        public override string Kind { get { return "CounterSet"; } }

        public override string Describe()
        {
            return "CounterSet{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",name=" + S(CounterName) +
                   ",value=" + NewValue.ToString(CultureInfo.InvariantCulture) +
                   ",persistent=" + Persistent.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    /// <summary>
    /// <c>SELECTION_SET</c> — Encounter Modifiers spec §5/§6.1.
    /// <para><b>Plugin call: none.</b> Pure per-battle state write the engine has ALREADY performed
    /// (<c>CombatRuntime.Selections[Name] = Value</c>) — same posture as <see cref="CounterAddAction"/>.
    /// Emitted only for logging/parity-audit.</para>
    /// </summary>
    public sealed class SelectionSetAction : EngineAction
    {
        public string Name;
        /// <summary>The winning value, or null when the weighted draw resolved to nothing (empty table).</summary>
        public string Value;

        public override string Kind { get { return "SelectionSet"; } }

        public override string Describe()
        {
            return "SelectionSet{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",name=" + S(Name) +
                   ",value=" + S(Value) + "}";
        }
    }

    /// <summary>
    /// <c>EVENT_BANNER</c> — Encounter Modifiers spec §5/§9. <c>[LOCAL]</c> presentation only, never gates
    /// gameplay, never touches replicated state or RNG.
    /// <para><b>Plugin call:</b> <c>GameplayDialogViewHelper.ShowEventTitle(text, DurationMs)</c>, with
    /// exceptions swallowed (R4 posture) and the formatted/localized text built from
    /// <see cref="LocKey"/>/<see cref="FallbackText"/>/<see cref="SelectionValue"/> — the engine resolves
    /// only the raw selection value (a pure per-battle state read), never localization.</para>
    /// </summary>
    public sealed class EventBannerAction : EngineAction
    {
        public string LocKey;
        public string FallbackText;
        public int DurationMs;
        /// <summary>Resolved from <c>CombatRuntime.Selections[TextFromSelection]</c> at plan time when the
        /// effect authored <c>TextFromSelection</c>; null otherwise.</summary>
        public string SelectionValue;

        public override string Kind { get { return "EventBanner"; } }

        public override string Describe()
        {
            return "EventBanner{recipe=" + S(RecipeId) + ",owner=" + S(OwnerGuid) + ",locKey=" + S(LocKey) +
                   ",fallback=" + S(FallbackText) + ",durationMs=" + DurationMs.ToString(CultureInfo.InvariantCulture) +
                   ",selection=" + S(SelectionValue) + "}";
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
