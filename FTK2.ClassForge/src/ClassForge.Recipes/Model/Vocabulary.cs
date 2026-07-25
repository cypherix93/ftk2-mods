using System;
using System.Collections.Generic;

namespace ClassForge.Recipes.Model
{
    /// <summary>Trigger tokens — SPEC-DELTA-v1.1 §2 (7 v1 + 8 added = 15 tokens).</summary>
    public enum TriggerKind
    {
        // --- v1, re-anchored (§2.1) ---
        ON_ABILITY_USED,
        ON_CRIT,
        ON_KILL,
        ON_HEAL,
        ON_DAMAGED,           // deprecated alias of ON_DAMAGE_TAKEN
        ON_TURN_START,
        ON_TURN_END,
        // --- added in v1.1 (§2.2) ---
        ON_COMBAT_START,           // T1
        ON_ABILITY_DECLARED,       // T2
        ON_DAMAGE_DEALT,           // T3
        ON_DAMAGE_TAKEN,           // T4
        ON_STATUS_APPLIED,         // T5
        ON_CONSUMABLE_USED,        // T6
        ON_ENEMY_ABILITY_RESOLVED, // T7
        ON_HEAL_PENDING            // T8
    }

    /// <summary>Condition tokens — SPEC-DELTA-v1.1 §3 (8 v1 + 15 added = 23 tokens).</summary>
    public enum ConditionKind
    {
        // --- v1 (§3.1) ---
        HP_THRESHOLD,
        HAS_STATUS,
        LACKS_STATUS,
        ROW,
        WEAPON_CLASS,
        ABILITY_TAG,
        TARGET_BASE_TYPE,
        TARGET_BASE_TYPE_NOT,  // deprecated alias of TARGET_BASE_TYPE + Negate
        // --- added in v1.1 (§3.2) ---
        ROLL_TIER,        // C1
        HOSTILE_ACTION,   // C2
        ABILITY_STAT,     // C3
        ABILITY_RANGED,   // C4
        ABILITY_REPEATED, // C5
        CHARACTER_TYPE,   // C6
        FOCUS_SPENT,      // C7
        FOCUS_CURRENT,    // C8
        STATUS_COUNT,     // C9
        STATUS_TYPE,      // C10
        COUNTER,          // C11
        MOVED_THIS_ROUND, // C12
        ALL_ALLIES_ACTED, // C13
        ITEM_CLASS,       // C14
        ITEM_CONSUMABLE   // C15
    }

    /// <summary>Effect tokens — SPEC-DELTA-v1.1 §4 (4 v1 + 4 added = 8 tokens).</summary>
    public enum EffectKind
    {
        ADD_STATUS,
        REMOVE_STATUS,
        STAT_CHANGE,
        SUMMON,
        ROLL_STAT_BONUS, // E1
        HEAL_MODIFIER,   // E2
        COUNTER_ADD,     // E3
        COUNTER_SET      // E4
    }

    /// <summary>Target tokens — SPEC-DELTA-v1.1 §4.3 (5 v1 + 4 added = 9 tokens).</summary>
    public enum TargetKind
    {
        SELF,
        CASTER,
        TRIGGER_TARGET,
        TRIGGER_TARGET_POSITION,
        ALLY_ALL,
        TRIGGER_SOURCE,    // v1.1
        ALLY_ALL_OTHERS,   // v1.1
        ENEMY_ALL,         // v1.1
        ALLY_BY_RANK       // v1.1
    }

    /// <summary>Universal <c>Of</c> selector — SPEC-DELTA-v1.1 §3.</summary>
    public enum OfSelector
    {
        SELF,
        TRIGGER_TARGET,
        TRIGGER_SOURCE
    }

    /// <summary>Budget scopes — SPEC-DELTA-v1.1 §5.1 (5 values).</summary>
    public enum BudgetScope
    {
        NONE,
        ONCE_PER_ROUND,
        ONCE_PER_COMBAT,
        ONCE_PER_TARGET_PER_ROUND,
        ONCE_PER_TARGET_PER_COMBAT
    }

    /// <summary>Budget consumption modes — SPEC-DELTA-v1.1 §5.1.</summary>
    public enum ConsumeOn
    {
        PROC,
        EVALUATION,
        EFFECT_APPLIED
    }

    public enum Comparator
    {
        EQ,
        NE,
        LT,
        LTE,
        GT,
        GTE
    }

    /// <summary><c>eRollStatus</c>, ordered worst→best so GTE/LTE mean "at least/at most this good".</summary>
    public enum RollTier
    {
        CRIT_FAIL = 0,
        FAIL = 1,
        SUCCESS = 2,
        PERFECT = 3
    }

    public enum StatusCategory
    {
        ANY,
        HARMFUL,
        BENEFICIAL
    }

    public enum RankOrder
    {
        LOWEST,
        HIGHEST
    }

    /// <summary><c>eSummonTypes</c> — SPEC-DELTA-v1.1 OQ#4.</summary>
    public enum SummonType
    {
        SPECIFIC,
        RANDOM,
        PLAYTHING,
        AS_FOLLOWER
    }

    /// <summary><c>HEAL_MODIFIER.Scope</c> — SPEC-DELTA-v1.1 §4.2 E2.</summary>
    public enum HealScope
    {
        RECEIVED,
        GIVEN
    }

    /// <summary>
    /// Token tables. Everything is <c>const</c> or <c>static readonly</c> — the engine holds
    /// <b>no static mutable state</b> (SPEC-DELTA-v1.1 §6, the direct fix for EOR's
    /// process-global <c>SteadyAimUsedThisCombat</c> leak).
    /// </summary>
    public static class Vocabulary
    {
        public const string SchemaVersionCurrent = "1.1";
        public const string SchemaVersionLegacy = "1.0";

        /// <summary>Status token meaning "the status carried by the trigger" — SPEC-DELTA-v1.1 §4.1.</summary>
        public const string TriggerStatusToken = "TRIGGER_STATUS";

        /// <summary>Cap on <c>SUMMON.Count</c> — SPEC-DELTA-v1.1 OQ#4.</summary>
        public const int SummonCountCap = 4;

        /// <summary><c>FOCUS_CURRENT</c>'s symbolic value (compares against MXFOC) — condition C8.</summary>
        public const string FocusMaxToken = "MAX";

        /// <summary>
        /// The authored HARMFUL classification for <c>STATUS_COUNT</c> (condition C9). All 18 members are
        /// <c>eStatusEffectTypes</c> values per EGT §9. SPEC-DELTA-v1.1 §9 risk 5: keep it in ONE place.
        /// </summary>
        public static readonly IReadOnlyList<string> HarmfulStatusTypes = new[]
        {
            "ACID", "BLEED", "CONFUSE", "CURSE", "DAZE", "DEATHMARK", "DEBUFF", "ENTANGLE",
            "FIRE", "ICE", "INFINITE_FIRE", "PETRIFY", "POISON", "RATTLED", "SCARE", "SHOCK",
            "STUN", "WATER"
        };

        /// <summary>Triggers added in v1.1; a recipe declaring SchemaVersion 1.0 may not use these.</summary>
        public static readonly IReadOnlyList<TriggerKind> V11OnlyTriggers = new[]
        {
            TriggerKind.ON_COMBAT_START, TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_DAMAGE_DEALT,
            TriggerKind.ON_DAMAGE_TAKEN, TriggerKind.ON_STATUS_APPLIED, TriggerKind.ON_CONSUMABLE_USED,
            TriggerKind.ON_ENEMY_ABILITY_RESOLVED, TriggerKind.ON_HEAL_PENDING
        };

        /// <summary>Conditions added in v1.1.</summary>
        public static readonly IReadOnlyList<ConditionKind> V11OnlyConditions = new[]
        {
            ConditionKind.ROLL_TIER, ConditionKind.HOSTILE_ACTION, ConditionKind.ABILITY_STAT,
            ConditionKind.ABILITY_RANGED, ConditionKind.ABILITY_REPEATED, ConditionKind.CHARACTER_TYPE,
            ConditionKind.FOCUS_SPENT, ConditionKind.FOCUS_CURRENT, ConditionKind.STATUS_COUNT,
            ConditionKind.STATUS_TYPE, ConditionKind.COUNTER, ConditionKind.MOVED_THIS_ROUND,
            ConditionKind.ALL_ALLIES_ACTED, ConditionKind.ITEM_CLASS, ConditionKind.ITEM_CONSUMABLE
        };

        /// <summary>Effects added in v1.1.</summary>
        public static readonly IReadOnlyList<EffectKind> V11OnlyEffects = new[]
        {
            EffectKind.ROLL_STAT_BONUS, EffectKind.HEAL_MODIFIER, EffectKind.COUNTER_ADD, EffectKind.COUNTER_SET
        };

        /// <summary>Targets added in v1.1.</summary>
        public static readonly IReadOnlyList<TargetKind> V11OnlyTargets = new[]
        {
            TargetKind.TRIGGER_SOURCE, TargetKind.ALLY_ALL_OTHERS, TargetKind.ENEMY_ALL, TargetKind.ALLY_BY_RANK
        };

        /// <summary>Conditions the <c>Of</c> selector is defined for — SPEC-DELTA-v1.1 §3.</summary>
        public static readonly IReadOnlyList<ConditionKind> OfCapableConditions = new[]
        {
            ConditionKind.HP_THRESHOLD, ConditionKind.HAS_STATUS, ConditionKind.LACKS_STATUS,
            ConditionKind.ROW, ConditionKind.CHARACTER_TYPE, ConditionKind.STATUS_COUNT,
            ConditionKind.FOCUS_CURRENT, ConditionKind.MOVED_THIS_ROUND
        };

        /// <summary>Dynamic value-source tokens for <c>FlatValueFrom</c>/<c>PercentFrom</c> — SPEC-DELTA-v1.1 §4.1.</summary>
        public const string SourceFocusSpent = "FOCUS_SPENT";
        public const string SourceCounterPrefix = "COUNTER:";
        public const string SourceStatusCountPrefix = "STATUS_COUNT:";
        public const string SourceTargetHpPct = "TARGET_HP_PCT";

        public static bool TryParseTrigger(string token, out TriggerKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        public static bool TryParseCondition(string token, out ConditionKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        public static bool TryParseEffect(string token, out EffectKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        public static bool TryParseTarget(string token, out TargetKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        /// <summary>
        /// Strict token→enum parse. Deliberately NOT <c>Enum.Parse(ignoreCase:true)</c> with numeric
        /// fallback: <c>Enum.TryParse</c> accepts "3" for any enum, which would silently admit garbage
        /// tokens. Unknown tokens must produce a Finding and disable the recipe (fail-safe).
        /// </summary>
        private static bool TryParseEnum<T>(string token, out T value) where T : struct
        {
            value = default(T);
            if (string.IsNullOrEmpty(token)) return false;
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, token, StringComparison.Ordinal))
                {
                    value = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }
            return false;
        }

        public static bool Compare(Comparator cmp, int actual, int expected)
        {
            switch (cmp)
            {
                case Comparator.EQ: return actual == expected;
                case Comparator.NE: return actual != expected;
                case Comparator.LT: return actual < expected;
                case Comparator.LTE: return actual <= expected;
                case Comparator.GT: return actual > expected;
                default: return actual >= expected;
            }
        }
    }
}
