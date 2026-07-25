using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Parsing
{
    /// <summary>
    /// Semantic validation on top of <see cref="RecipeParser"/>'s structural pass.
    /// <para>Enforces the per-primitive restrictions SPEC-DELTA-v1.1 states in prose:
    /// the SchemaVersion gate (§5.1), <c>ROLL_STAT_BONUS</c> being <c>ON_ABILITY_DECLARED</c>-only (§4.2 E1),
    /// <c>HEAL_MODIFIER</c> being <c>ON_HEAL_PENDING</c>-only <b>and RNG-free</b> (§4.2 E2, §2 T8),
    /// <c>STATUS_TYPE</c>/<c>TRIGGER_STATUS</c> being <c>ON_STATUS_APPLIED</c>-only (§3.2 C10, §4.1),
    /// and the <c>SUMMON.Count</c> cap of 4 (OQ#4).</para>
    /// <para>Every Error disables the recipe. Nothing throws.</para>
    /// </summary>
    public static class RecipeValidator
    {
        private static readonly TriggerKind[] TriggersWithRoll =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED, TriggerKind.ON_ENEMY_ABILITY_RESOLVED
        };

        private static readonly TriggerKind[] TriggersWithTarget =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED, TriggerKind.ON_ENEMY_ABILITY_RESOLVED,
            TriggerKind.ON_CRIT, TriggerKind.ON_KILL, TriggerKind.ON_HEAL, TriggerKind.ON_DAMAGE_DEALT,
            TriggerKind.ON_DAMAGE_TAKEN, TriggerKind.ON_CONSUMABLE_USED, TriggerKind.ON_STATUS_APPLIED,
            TriggerKind.ON_HEAL_PENDING
        };

        private static readonly TriggerKind[] TriggersWithSource =
        {
            TriggerKind.ON_DAMAGE_TAKEN, TriggerKind.ON_STATUS_APPLIED,
            TriggerKind.ON_ENEMY_ABILITY_RESOLVED, TriggerKind.ON_HEAL_PENDING
        };

        private static readonly TriggerKind[] TriggersWithAbility =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED, TriggerKind.ON_ENEMY_ABILITY_RESOLVED,
            TriggerKind.ON_CRIT, TriggerKind.ON_KILL, TriggerKind.ON_DAMAGE_DEALT, TriggerKind.ON_DAMAGE_TAKEN,
            TriggerKind.ON_HEAL, TriggerKind.ON_CONSUMABLE_USED
        };

        private static readonly TriggerKind[] TriggersWithItem =
        {
            TriggerKind.ON_CONSUMABLE_USED, TriggerKind.ON_HEAL_PENDING
        };

        private static readonly TriggerKind[] TriggersWithFocus =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED
        };

        private static readonly string[] StatChangeTypesForNonHp = { "MAGICAL", "PHYSICAL", "REGEN" };

        public static void Validate(RecipeSet set)
        {
            for (int i = 0; i < set.Ordered.Count; i++) ValidateRecipe(set, set.Ordered[i]);
        }

        private static void ValidateRecipe(RecipeSet set, SkillRecipe r)
        {
            bool isV11 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCurrent, StringComparison.Ordinal);
            bool isV10 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionLegacy, StringComparison.Ordinal);
            if (!isV11 && !isV10)
            {
                RecipeParser.Err(set, r, "SchemaVersion", "E_SCHEMA_UNSUPPORTED",
                    "SchemaVersion '" + r.SchemaVersion + "' cannot be run by this engine (supported: " +
                    Vocabulary.SchemaVersionLegacy + ", " + Vocabulary.SchemaVersionCurrent + ")");
                return; // nothing else is meaningful once the vocabulary version is unknown
            }

            if (r.ProcChance < 0 || r.ProcChance > 100)
                RecipeParser.Err(set, r, "ProcChance", "E_RANGE", "ProcChance must be 0..100, saw " + r.ProcChance.ToString(CultureInfo.InvariantCulture));
            if (r.AiProcChance < 0 || r.AiProcChance > 100)
                RecipeParser.Err(set, r, "AiProcChance", "E_RANGE", "AiProcChance must be 0..100, saw " + r.AiProcChance.ToString(CultureInfo.InvariantCulture));
            if (r.Cooldown < 0)
                RecipeParser.Err(set, r, "Cooldown", "E_RANGE", "Cooldown must be >= 0");

            // --- SchemaVersion gate on v1.1-only tokens (§5.1) ---
            if (isV10)
            {
                if (Contains(Vocabulary.V11OnlyTriggers, r.Trigger))
                    RecipeParser.Err(set, r, "Trigger", "E_SCHEMA_GATE",
                        "trigger " + r.Trigger + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                if (r.Budget.Scope != BudgetScope.NONE)
                    RecipeParser.Err(set, r, "Budget.Scope", "E_SCHEMA_GATE",
                        "Budget requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
            }

            ValidateConditionList(set, r, r.Conditions, "Conditions", isV10);

            for (int i = 0; i < r.Effects.Count; i++)
            {
                string path = "Effects[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                var e = r.Effects[i];
                if (isV10)
                {
                    if (Contains(Vocabulary.V11OnlyEffects, e.Type))
                        RecipeParser.Err(set, r, path + ".Type", "E_SCHEMA_GATE",
                            "effect " + e.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                    if (Contains(Vocabulary.V11OnlyTargets, e.Target))
                        RecipeParser.Err(set, r, path + ".Target", "E_SCHEMA_GATE",
                            "target " + e.Target + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                    if (e.Conditions.Count > 0)
                        RecipeParser.Err(set, r, path + ".Conditions", "E_SCHEMA_GATE",
                            "per-effect Conditions require SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                }
                ValidateConditionList(set, r, e.Conditions, path + ".Conditions", isV10);
                if (e.Rank != null) ValidateConditionList(set, r, e.Rank.Where, path + ".Rank.Where", isV10);
                ValidateEffect(set, r, e, path);
            }
        }

        private static void ValidateEffect(RecipeSet set, SkillRecipe r, RecipeEffect e, string path)
        {
            // --- target/trigger compatibility ---
            if (e.Target == TargetKind.TRIGGER_TARGET || e.Target == TargetKind.TRIGGER_TARGET_POSITION)
            {
                if (!Contains(TriggersWithTarget, r.Trigger))
                    RecipeParser.Warn(set, r, path + ".Target", "W_NO_TRIGGER_TARGET",
                        r.Trigger + " carries no trigger target; this effect will be a no-op");
            }
            if (e.Target == TargetKind.TRIGGER_SOURCE && !Contains(TriggersWithSource, r.Trigger))
                RecipeParser.Warn(set, r, path + ".Target", "W_NO_TRIGGER_SOURCE",
                    r.Trigger + " carries no trigger source; this effect will be a no-op");
            if (e.Target == TargetKind.ALLY_BY_RANK && e.Rank == null)
                RecipeParser.Err(set, r, path + ".Rank", "E_RANK_MISSING", "ALLY_BY_RANK requires a Rank block");

            switch (e.Type)
            {
                case EffectKind.ADD_STATUS:
                case EffectKind.REMOVE_STATUS:
                {
                    bool hasStatus = !string.IsNullOrEmpty(e.Status);
                    bool hasOneOf = e.StatusOneOf != null && e.StatusOneOf.Count > 0;
                    if (!hasStatus && !hasOneOf)
                        RecipeParser.Err(set, r, path, "E_STATUS_MISSING", e.Type + " requires Status or StatusOneOf");
                    if (hasStatus && hasOneOf)
                        RecipeParser.Err(set, r, path, "E_STATUS_AMBIGUOUS", "Status and StatusOneOf are mutually exclusive");
                    if (hasStatus && string.Equals(e.Status, Vocabulary.TriggerStatusToken, StringComparison.Ordinal)
                        && r.Trigger != TriggerKind.ON_STATUS_APPLIED)
                        RecipeParser.Err(set, r, path + ".Status", "E_TRIGGER_STATUS_SCOPE",
                            "the TRIGGER_STATUS token is only bound under ON_STATUS_APPLIED");
                    if (e.Type == EffectKind.REMOVE_STATUS && !string.IsNullOrEmpty(e.FallbackStatus))
                        RecipeParser.Warn(set, r, path + ".FallbackStatus", "W_FALLBACK_IGNORED",
                            "FallbackStatus is only meaningful on ADD_STATUS (§4.1 IMMUNITY_FALLBACK)");
                    if (e.Duration.HasValue && e.Duration.Value < 0)
                        RecipeParser.Err(set, r, path + ".Duration", "E_RANGE", "Duration must be >= 0");
                    break;
                }
                case EffectKind.STAT_CHANGE:
                {
                    if (string.IsNullOrEmpty(e.Stat))
                        RecipeParser.Err(set, r, path + ".Stat", "E_STAT_MISSING", "STAT_CHANGE requires Stat");
                    if (string.IsNullOrEmpty(e.StatChangeType))
                        RecipeParser.Err(set, r, path + ".StatChangeType", "E_STAT_MISSING", "STAT_CHANGE requires StatChangeType");
                    bool hasValue = e.FlatValue.HasValue || e.FlatPercent.HasValue ||
                                    !string.IsNullOrEmpty(e.FlatValueFrom) || !string.IsNullOrEmpty(e.PercentFrom);
                    if (!hasValue)
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING",
                            "STAT_CHANGE requires FlatValue, FlatPercent, FlatValueFrom or PercentFrom");
                    if (!string.IsNullOrEmpty(e.Stat) && !string.Equals(e.Stat, "HP", StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(e.StatChangeType) && !Contains(StatChangeTypesForNonHp, e.StatChangeType))
                        RecipeParser.Warn(set, r, path + ".StatChangeType", "W_DAMAGE_TYPE",
                            "eDamageType '" + e.StatChangeType + "' on the non-HP stat '" + e.Stat +
                            "' is semantically unverified at runtime (OQ#5); prefer MAGICAL/PHYSICAL/REGEN");
                    break;
                }
                case EffectKind.SUMMON:
                {
                    if (e.SummonType == SummonType.SPECIFIC && string.IsNullOrEmpty(e.CharacterConfig))
                        RecipeParser.Err(set, r, path + ".CharacterConfig", "E_SUMMON_CONFIG",
                            "SUMMON with SummonType SPECIFIC requires CharacterConfig");
                    if (e.Count < 1)
                        RecipeParser.Err(set, r, path + ".Count", "E_RANGE", "SUMMON Count must be >= 1");
                    if (e.Count > Vocabulary.SummonCountCap)
                        RecipeParser.Err(set, r, path + ".Count", "E_SUMMON_CAP",
                            "SUMMON Count is capped at " + Vocabulary.SummonCountCap.ToString(CultureInfo.InvariantCulture) +
                            " (OQ#4: each iteration takes its own placement/pool draws from CombatState.Random)");
                    break;
                }
                case EffectKind.ROLL_STAT_BONUS:
                {
                    if (r.Trigger != TriggerKind.ON_ABILITY_DECLARED)
                        RecipeParser.Err(set, r, path, "E_EFFECT_TRIGGER_SCOPE",
                            "ROLL_STAT_BONUS is ON_ABILITY_DECLARED only (§4.2 E1: it replaces the PerformAbility prefix's stat delegates)");
                    if (string.IsNullOrEmpty(e.Stat))
                        RecipeParser.Err(set, r, path + ".Stat", "E_STAT_MISSING", "ROLL_STAT_BONUS requires Stat");
                    if (!e.Percent.HasValue && !e.Flat.HasValue && string.IsNullOrEmpty(e.PercentFrom) &&
                        string.IsNullOrEmpty(e.FlatValueFrom))
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING",
                            "ROLL_STAT_BONUS requires Percent, Flat, PercentFrom or FlatValueFrom");
                    break;
                }
                case EffectKind.HEAL_MODIFIER:
                {
                    if (r.Trigger != TriggerKind.ON_HEAL_PENDING)
                        RecipeParser.Err(set, r, path, "E_EFFECT_TRIGGER_SCOPE",
                            "HEAL_MODIFIER is ON_HEAL_PENDING only (§4.2 E2: it mutates AddHealth's ref int pValue)");
                    if (r.ProcChance != 100 || r.AiProcChance != 100)
                        RecipeParser.Err(set, r, path, "E_HEAL_RNG",
                            "a chance-gated HEAL_MODIFIER is rejected (§2 T8 / §4.2 E2: no RNG is permitted on the heal path)");
                    if (!e.Percent.HasValue && !e.Flat.HasValue)
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING", "HEAL_MODIFIER requires Percent or Flat");
                    break;
                }
                case EffectKind.COUNTER_ADD:
                case EffectKind.COUNTER_SET:
                {
                    if (string.IsNullOrEmpty(e.Name))
                        RecipeParser.Err(set, r, path + ".Name", "E_COUNTER_NAME", e.Type + " requires Name");
                    break;
                }
            }
        }

        private static void ValidateConditionList(RecipeSet set, SkillRecipe r, List<RecipeCondition> list, string path, bool isV10)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                string p = path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                var c = list[i];
                if (isV10 && Contains(Vocabulary.V11OnlyConditions, c.Type))
                    RecipeParser.Err(set, r, p + ".Type", "E_SCHEMA_GATE",
                        "condition " + c.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                if (c.Of != OfSelector.SELF && !Contains(Vocabulary.OfCapableConditions, c.Type))
                    RecipeParser.Warn(set, r, p + ".Of", "W_OF_IGNORED",
                        "the Of selector is not defined for " + c.Type + " (§3) and is ignored");
                if (c.Of == OfSelector.TRIGGER_SOURCE && !Contains(TriggersWithSource, r.Trigger))
                    RecipeParser.Warn(set, r, p + ".Of", "W_NO_TRIGGER_SOURCE",
                        r.Trigger + " carries no trigger source; this condition will read nothing and evaluate false");
                ValidateCondition(set, r, c, p);
            }
        }

        private static void ValidateCondition(RecipeSet set, SkillRecipe r, RecipeCondition c, string p)
        {
            switch (c.Type)
            {
                case ConditionKind.HP_THRESHOLD:
                    if (!c.Percent.HasValue && !c.Flat.HasValue)
                        RecipeParser.Err(set, r, p, "E_VALUE_MISSING", "HP_THRESHOLD requires Percent or Flat");
                    if (c.Percent.HasValue && c.Flat.HasValue)
                        RecipeParser.Err(set, r, p, "E_VALUE_AMBIGUOUS", "HP_THRESHOLD takes Percent or Flat, not both");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "HP_THRESHOLD requires a Comparator");
                    break;

                case ConditionKind.HAS_STATUS:
                case ConditionKind.LACKS_STATUS:
                case ConditionKind.WEAPON_CLASS:
                case ConditionKind.ABILITY_TAG:
                case ConditionKind.TARGET_BASE_TYPE:
                case ConditionKind.CHARACTER_TYPE:
                case ConditionKind.ABILITY_STAT:
                case ConditionKind.ITEM_CLASS:
                case ConditionKind.STATUS_TYPE:
                    if (string.IsNullOrEmpty(c.Value))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a string Value");
                    break;

                case ConditionKind.ROW:
                    if (string.IsNullOrEmpty(c.Value) ||
                        (!string.Equals(c.Value, "FRONT", StringComparison.Ordinal) &&
                         !string.Equals(c.Value, "BACK", StringComparison.Ordinal)))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "ROW Value must be FRONT or BACK");
                    break;

                case ConditionKind.HOSTILE_ACTION:
                case ConditionKind.ABILITY_RANGED:
                case ConditionKind.ABILITY_REPEATED:
                case ConditionKind.MOVED_THIS_ROUND:
                case ConditionKind.ALL_ALLIES_ACTED:
                case ConditionKind.ITEM_CONSUMABLE:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a boolean Value");
                    break;

                case ConditionKind.FOCUS_SPENT:
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "FOCUS_SPENT requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "FOCUS_SPENT requires a Comparator");
                    if (!Contains(TriggersWithFocus, r.Trigger))
                        RecipeParser.Warn(set, r, p, "W_NO_FOCUS",
                            r.Trigger + " carries no pCombatDecision.FocusUsed; FOCUS_SPENT reads 0");
                    break;

                case ConditionKind.FOCUS_CURRENT:
                    if (!c.ValueInt.HasValue &&
                        !string.Equals(c.Value, Vocabulary.FocusMaxToken, StringComparison.Ordinal))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING",
                            "FOCUS_CURRENT Value must be an integer or \"" + Vocabulary.FocusMaxToken + "\"");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "FOCUS_CURRENT requires a Comparator");
                    break;

                case ConditionKind.STATUS_COUNT:
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "STATUS_COUNT requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "STATUS_COUNT requires a Comparator");
                    break;

                case ConditionKind.COUNTER:
                    if (string.IsNullOrEmpty(c.Name))
                        RecipeParser.Err(set, r, p + ".Name", "E_COUNTER_NAME", "COUNTER requires Name");
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "COUNTER requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "COUNTER requires a Comparator");
                    break;

                case ConditionKind.ROLL_TIER:
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "ROLL_TIER requires a Comparator");
                    if (!Contains(TriggersWithRoll, r.Trigger))
                        RecipeParser.Warn(set, r, p, "W_NO_ROLL_DATA",
                            r.Trigger + " carries no pRollData; ROLL_TIER reads the default tier");
                    break;
            }

            if (c.Type == ConditionKind.STATUS_TYPE && r.Trigger != TriggerKind.ON_STATUS_APPLIED)
                RecipeParser.Err(set, r, p, "E_COND_TRIGGER_SCOPE",
                    "STATUS_TYPE is ON_STATUS_APPLIED only (§3.2 C10)");

            if ((c.Type == ConditionKind.ITEM_CLASS || c.Type == ConditionKind.ITEM_CONSUMABLE) &&
                !Contains(TriggersWithItem, r.Trigger))
                RecipeParser.Warn(set, r, p, "W_NO_ITEM",
                    r.Trigger + " carries no pThing; " + c.Type + " will evaluate false");

            if ((c.Type == ConditionKind.ABILITY_TAG || c.Type == ConditionKind.ABILITY_RANGED ||
                 c.Type == ConditionKind.ABILITY_STAT || c.Type == ConditionKind.HOSTILE_ACTION ||
                 c.Type == ConditionKind.ABILITY_REPEATED) &&
                !Contains(TriggersWithAbility, r.Trigger))
                RecipeParser.Warn(set, r, p, "W_NO_ABILITY",
                    r.Trigger + " carries no ability id; " + c.Type + " will evaluate false");
        }

        private static bool Contains<T>(IReadOnlyList<T> list, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < list.Count; i++) if (cmp.Equals(list[i], value)) return true;
            return false;
        }

        private static bool Contains(string[] list, string value)
        {
            for (int i = 0; i < list.Length; i++)
                if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
