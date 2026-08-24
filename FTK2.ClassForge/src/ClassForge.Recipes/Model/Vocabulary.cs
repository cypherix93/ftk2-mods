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
        ON_HEAL_PENDING,           // T8
        // --- added in v1.2, loot-grant verb spec (docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §6.1) ---
        ON_COMBAT_LOOT,
        // --- added in v1.3, state-hash-chance spec M-SH3 ---
        /// <summary><c>InteractableHelper.CalculateFinalDamage</c> <b>Postfix</b> (PSN §2 L1708) — the
        /// pre-application damage value, mutable via <c>DAMAGE_TAKEN_MULT</c>. The hook has no
        /// <c>GameRandom</c> parameter, which is exactly why this trigger is validator-enforced RNG-free
        /// and why chance gates on it must be <c>STATE_HASH_CHANCE</c>. v1.3 limitation (recorded): the
        /// Plugin fires it for PHYSICAL damage only — its sole consumer (SHIELDBEARER) is physical-only
        /// in EOR 0.7.0.62 (Plugin.cs L26733 gates <c>eDamageType == 0</c>).</summary>
        ON_DAMAGE_PENDING
    }

    /// <summary>Condition tokens — SPEC-DELTA-v1.1 §3 (8 v1 + 15 v1.1 + 9 v1.2 encounter-modifiers = 32 tokens).</summary>
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
        ITEM_CONSUMABLE,  // C15
        // --- added in v1.2, Encounter Modifiers spec §5 (M-EM2) ---
        PARTY_AVG_LEVEL,
        IS_DUNGEON,
        BOSS_FIGHT,
        ENCOUNTER_PROPERTY,
        ENTITY_TAG,
        CONFIG_NAME_CONTAINS,
        COMBAT_START_REAL,
        SELECTION_PRESENT,
        IS_ENEMY,          // GATE D: CharacterHelper.IsEnemy semantics (GroupIndex == 1)
        // --- added in v1.3, conditional-stat-modifier spec §4.2 ---
        /// <summary>EOR62 <c>PartyHasPetOrMercenary()</c> (L24576): any player follower resolves to a
        /// pet or mercenary entity. Legal ONLY in a CONDITIONAL_STAT_MODIFIER context — the combat
        /// dispatcher has no evaluator for it, so <c>RecipeValidator</c> rejects it in skillrecipes.json.</summary>
        PARTY_HAS_FOLLOWER,
        /// <summary>state-hash-chance spec §2: a deterministic, draw-free chance gate — FNV-1a over
        /// <c>Salt|inputs…</c>, verdict <c>h % 100 &lt; Percent</c>. NOT a roll (SPEC-DELTA §5.2
        /// invariant-1 amendment); correlated across re-evaluation with identical inputs (spec §6).</summary>
        STATE_HASH_CHANCE
    }

    /// <summary>Effect tokens — SPEC-DELTA-v1.1 §4 (4 v1 + 4 v1.1 + 4 loot v1.2 + 2 encounter-modifiers v1.2 = 14 tokens).</summary>
    public enum EffectKind
    {
        ADD_STATUS,
        REMOVE_STATUS,
        STAT_CHANGE,
        SUMMON,
        ROLL_STAT_BONUS, // E1
        HEAL_MODIFIER,   // E2
        COUNTER_ADD,     // E3
        COUNTER_SET,     // E4
        // --- added in v1.2, loot-grant verb spec §6.2 (ON_COMBAT_LOOT-only; emit LootOp deltas) ---
        GOLD_GRANT,
        ITEM_TAG_GRANT,
        LOOT_SCALE,
        AFFIX_ROLL,      // reserved: parses, but the v1 validator always rejects it (M-LG4)
        // --- added in v1.2, Encounter Modifiers spec §5 (M-EM2) ---
        SELECTION_SET,
        EVENT_BANNER,
        // --- added in v1.3, state-hash-chance spec M-SH3 ---
        /// <summary>Mutates the pending damage on <c>ON_DAMAGE_PENDING</c> (the SPEC-DELTA §7.4 park,
        /// retired): <c>delta = sign(Percent) * max(MinDelta, ceil(damage * |Percent| / 100))</c>,
        /// result floored at 0 — EOR 0.7.0.62's SHIELDBEARER arithmetic verbatim (L26733:
        /// <c>Max(2, CeilToInt(result * 0.25f))</c>). ON_DAMAGE_PENDING-only, validator-enforced.</summary>
        DAMAGE_TAKEN_MULT
    }

    /// <summary>Recipe-level scope — Encounter Modifiers spec §4.2. <c>OWNED</c> is exactly today's v1.1
    /// semantics (default, field omitted everywhere pre-v1.2); <c>COMBAT</c> is the new ownerless
    /// registration capability: live for every combat while the pack is enabled, evaluated once per
    /// trigger event after all owned recipes for that event.</summary>
    public enum RecipeScope
    {
        OWNED,
        COMBAT
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

        /// <summary>Schema version gating <c>ON_COMBAT_LOOT</c> + the loot-grant effect vocabulary
        /// (loot-grant verb spec §6, M-LG1). Additive over 1.1 — nothing 1.1-authored breaks.</summary>
        public const string SchemaVersionLoot = "1.2";

        /// <summary>Schema version gating <c>STATE_HASH_CHANCE</c> / <c>ON_DAMAGE_PENDING</c> /
        /// <c>DAMAGE_TAKEN_MULT</c> (state-hash-chance spec). Additive over 1.2.</summary>
        public const string SchemaVersionStateHash = "1.3";

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

        /// <summary>Triggers added in v1.2 (loot-grant verb spec §6.1). A recipe declaring SchemaVersion
        /// 1.0 or 1.1 may not use these.</summary>
        public static readonly IReadOnlyList<TriggerKind> V12OnlyTriggers = new[]
        {
            TriggerKind.ON_COMBAT_LOOT
        };

        /// <summary>Effects added in v1.2 (loot-grant verb spec §6.2 + Encounter Modifiers spec §5).</summary>
        public static readonly IReadOnlyList<EffectKind> V12OnlyEffects = new[]
        {
            EffectKind.GOLD_GRANT, EffectKind.ITEM_TAG_GRANT, EffectKind.LOOT_SCALE, EffectKind.AFFIX_ROLL,
            EffectKind.SELECTION_SET, EffectKind.EVENT_BANNER
        };

        /// <summary>Conditions added in v1.2 (Encounter Modifiers spec §5, M-EM2).</summary>
        public static readonly IReadOnlyList<ConditionKind> V12OnlyConditions = new[]
        {
            ConditionKind.PARTY_AVG_LEVEL, ConditionKind.IS_DUNGEON, ConditionKind.BOSS_FIGHT,
            ConditionKind.ENCOUNTER_PROPERTY, ConditionKind.ENTITY_TAG, ConditionKind.CONFIG_NAME_CONTAINS,
            ConditionKind.COMBAT_START_REAL, ConditionKind.SELECTION_PRESENT, ConditionKind.IS_ENEMY
        };

        /// <summary>Triggers added in v1.3 (state-hash-chance spec M-SH3).</summary>
        public static readonly IReadOnlyList<TriggerKind> V13OnlyTriggers = new[]
        {
            TriggerKind.ON_DAMAGE_PENDING
        };

        /// <summary>Conditions added in v1.3.</summary>
        public static readonly IReadOnlyList<ConditionKind> V13OnlyConditions = new[]
        {
            ConditionKind.STATE_HASH_CHANCE
        };

        /// <summary>Effects added in v1.3.</summary>
        public static readonly IReadOnlyList<EffectKind> V13OnlyEffects = new[]
        {
            EffectKind.DAMAGE_TAKEN_MULT
        };

        /// <summary>STATE_HASH_CHANCE's closed input-token set (spec §2.1). Adding a token is a spec
        /// change that must argue its replication — the closed set IS the §3.2 correctness contract.</summary>
        public static readonly IReadOnlyList<string> StateHashInputTokens = new[]
        {
            "SELF_GUID", "TRIGGER_SOURCE_GUID", "TRIGGER_TARGET_GUID", "COMBAT_ROUND", "COMBAT_SEED",
            "SELF_HP", "SELF_FOCUS", "TRIGGER_DAMAGE", "TRIGGER_ITEM_ID", "ABILITY_ID",
            "RUN_SEED", "ENCOUNTER_GUID"
        };

        /// <summary>Conditions the <c>Of</c> selector is defined for — SPEC-DELTA-v1.1 §3, extended by
        /// Encounter Modifiers spec §5.</summary>
        public static readonly IReadOnlyList<ConditionKind> OfCapableConditions = new[]
        {
            ConditionKind.HP_THRESHOLD, ConditionKind.HAS_STATUS, ConditionKind.LACKS_STATUS,
            ConditionKind.ROW, ConditionKind.CHARACTER_TYPE, ConditionKind.STATUS_COUNT,
            ConditionKind.FOCUS_CURRENT, ConditionKind.MOVED_THIS_ROUND,
            ConditionKind.ENTITY_TAG, ConditionKind.CONFIG_NAME_CONTAINS, ConditionKind.IS_ENEMY
        };

        /// <summary>Effect kinds that resolve no <c>Target</c> (pure per-battle state writes or [LOCAL]
        /// presentation) — SPEC-DELTA-v1.1 §4.2 E3/E4, Encounter Modifiers spec §5. Used both by the
        /// dispatcher's early-effect switch and by the COMBAT-scope validator's target-rejection rule
        /// (Encounter Modifiers spec §4.2), which must not flag these for the default <c>Target: SELF</c>
        /// they never actually resolve.</summary>
        public static readonly IReadOnlyList<EffectKind> TargetlessEffects = new[]
        {
            EffectKind.COUNTER_ADD, EffectKind.COUNTER_SET, EffectKind.SELECTION_SET, EffectKind.EVENT_BANNER
        };

        /// <summary>Dynamic value-source tokens for <c>FlatValueFrom</c>/<c>PercentFrom</c> — SPEC-DELTA-v1.1 §4.1.</summary>
        public const string SourceFocusSpent = "FOCUS_SPENT";
        public const string SourceCounterPrefix = "COUNTER:";
        public const string SourceStatusCountPrefix = "STATUS_COUNT:";
        public const string SourceTargetHpPct = "TARGET_HP_PCT";

        /// <summary>
        /// <c>FlatValueFrom</c>/<c>PercentFrom</c> token yielding the damage the trigger is carrying.
        ///
        /// This is what makes proportional lifesteal and proportional reflect expressible. Before it,
        /// the damage magnitude was reachable ONLY as a STATE_HASH_CHANCE input, so a "drain 25% of
        /// the damage you dealt" trait had to be written as a flat number that is far too weak early
        /// and far too strong late.
        ///
        /// Raw value is the damage amount; combine with <c>PerUnit</c> to take a share of it, e.g.
        /// <c>{FlatValueFrom: "DAMAGE_DEALT_PCT", Percent: 25, Min: 1}</c>. RNG-free, so it stays
        /// multiplayer-safe, and scoped to damage-carrying triggers by the validator — under any
        /// other trigger there is no damage in scope and it would silently resolve to 0, which is the
        /// ROLL_TIER{EQ FAIL} mistake class this repo has been bitten by before.
        /// </summary>
        public const string SourceDamageDealtPct = "DAMAGE_DEALT_PCT";

        /// <summary>GATE C (Encounter Modifiers spec §5/§8.1): <c>FlatValueFrom</c> token computing a flat
        /// <c>STAT_CHANGE</c> value from <c>TRIGGER_TARGET.MXHP</c> and the effect's authored <c>Percent</c> —
        /// <c>flat = sign(Percent) * max(1, round(|targetMaxHp * Percent| / 100))</c> (EOR's rounding).</summary>
        public const string SourceTargetMxhpPct = "TARGET_MXHP_PCT";

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
