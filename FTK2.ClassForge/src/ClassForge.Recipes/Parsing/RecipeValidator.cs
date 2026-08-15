using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Loot;
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
            bool isV12 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionLoot, StringComparison.Ordinal);
            if (!isV11 && !isV10 && !isV12)
            {
                RecipeParser.Err(set, r, "SchemaVersion", "E_SCHEMA_UNSUPPORTED",
                    "SchemaVersion '" + r.SchemaVersion + "' cannot be run by this engine (supported: " +
                    Vocabulary.SchemaVersionLegacy + ", " + Vocabulary.SchemaVersionCurrent + ", " +
                    Vocabulary.SchemaVersionLoot + ")");
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

            // --- SchemaVersion gate on v1.2-only tokens (loot-grant verb spec §6, M-LG1) ---
            if (isV10 || isV11)
            {
                if (Contains(Vocabulary.V12OnlyTriggers, r.Trigger))
                    RecipeParser.Err(set, r, "Trigger", "E_SCHEMA_GATE",
                        "trigger " + r.Trigger + " requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
            }

            // --- ON_COMBAT_LOOT restricted vocabulary (verb spec §6.1): the trigger is intrinsically
            //     once-per-combat, so Budget/Cooldown are rejected outright. ---
            if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
            {
                if (r.Budget.Scope != BudgetScope.NONE)
                    RecipeParser.Err(set, r, "Budget.Scope", "E_LOOT_BUDGET",
                        "Budget is rejected on ON_COMBAT_LOOT (the trigger is intrinsically once-per-combat)");
                if (r.Cooldown > 0)
                    RecipeParser.Err(set, r, "Cooldown", "E_LOOT_COOLDOWN",
                        "Cooldown is rejected on ON_COMBAT_LOOT (the trigger is intrinsically once-per-combat)");
            }

            // --- PickOneEffect (verb spec §6.1): ON_COMBAT_LOOT only, requires >= 2 Effects. ---
            if (r.PickOneEffect)
            {
                if (r.Trigger != TriggerKind.ON_COMBAT_LOOT)
                    RecipeParser.Err(set, r, "PickOneEffect", "E_PICKONE_TRIGGER_SCOPE",
                        "PickOneEffect is only valid on ON_COMBAT_LOOT");
                if (r.Effects.Count < 2)
                    RecipeParser.Err(set, r, "PickOneEffect", "E_PICKONE_COUNT",
                        "PickOneEffect requires at least 2 Effects");
            }

            ValidateConditionList(set, r, r.Conditions, "Conditions", isV10);
            if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
                ValidateLootConditionScope(set, r, r.Conditions, "Conditions");

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
                if (isV10 || isV11)
                {
                    if (Contains(Vocabulary.V12OnlyEffects, e.Type))
                        RecipeParser.Err(set, r, path + ".Type", "E_SCHEMA_GATE",
                            "effect " + e.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
                }
                ValidateConditionList(set, r, e.Conditions, path + ".Conditions", isV10);
                if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
                    ValidateLootConditionScope(set, r, e.Conditions, path + ".Conditions");
                if (e.Rank != null) ValidateConditionList(set, r, e.Rank.Where, path + ".Rank.Where", isV10);
                ValidateEffect(set, r, e, path);
            }

            // --- ProcChanceFormula (Encounter Modifiers spec §5, v1.2, M-EM2) ---
            if (r.ProcChanceFormula != null)
            {
                if (isV10 || isV11)
                    RecipeParser.Err(set, r, "ProcChanceFormula", "E_SCHEMA_GATE",
                        "ProcChanceFormula requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
                if (r.ProcChanceAuthored)
                    RecipeParser.Err(set, r, "ProcChanceFormula", "E_PCF_MUTEX",
                        "ProcChance and ProcChanceFormula are mutually exclusive");
                if (r.ProcChanceFormula.Min.HasValue && r.ProcChanceFormula.Max.HasValue &&
                    r.ProcChanceFormula.Min.Value > r.ProcChanceFormula.Max.Value)
                    RecipeParser.Err(set, r, "ProcChanceFormula", "E_RANGE", "ProcChanceFormula.Min must be <= Max");
                ValidateFormulaRows(set, r, r.ProcChanceFormula.Base, "ProcChanceFormula.Base", isV10);
                ValidateFormulaRows(set, r, r.ProcChanceFormula.Adjustments, "ProcChanceFormula.Adjustments", isV10);
            }

            // --- Scope: COMBAT (Encounter Modifiers spec §4.2, v1.2, M-EM2) ---
            if (r.Scope == RecipeScope.COMBAT)
            {
                if (!isV12)
                    RecipeParser.Err(set, r, "Scope", "E_COMBAT_SCHEMA_GATE",
                        "Scope COMBAT requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
                if (r.AiProcChanceAuthored)
                    RecipeParser.Err(set, r, "AiProcChance", "E_COMBAT_AIPROCCHANCE",
                        "AiProcChance is meaningless on a COMBAT-scoped recipe (no owner to select Ai vs. " +
                        "player chance) and is rejected");
                ValidateCombatScopeConditions(set, r, r.Conditions, "Conditions");
                for (int i = 0; i < r.Effects.Count; i++)
                {
                    string path = "Effects[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var e = r.Effects[i];
                    ValidateCombatScopeConditions(set, r, e.Conditions, path + ".Conditions");
                    if (!Contains(Vocabulary.TargetlessEffects, e.Type) && IsSelfLikeTarget(e.Target))
                        RecipeParser.Err(set, r, path + ".Target", "E_COMBAT_TARGET",
                            "COMBAT-scoped recipes have no owner — Target " + e.Target +
                            " (SELF/CASTER/ALLY_*/ENEMY_ALL) is undefined; use TRIGGER_TARGET or TRIGGER_SOURCE");
                }
            }
        }

        private static void ValidateFormulaRows(RecipeSet set, SkillRecipe r, List<ProcChanceFormulaRow> rows, string path, bool isV10)
        {
            for (int i = 0; i < rows.Count; i++)
                ValidateConditionList(set, r, rows[i].Conditions, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "].Conditions", isV10);
        }

        /// <summary>Encounter Modifiers spec §4.2 load-time rejection: any condition whose <c>Of</c>
        /// resolves to SELF (explicit or default) on a COMBAT-scoped recipe, since Owner is null and SELF
        /// would silently resolve to nothing. Only applies to conditions the <c>Of</c> selector is actually
        /// defined for — a condition that never reads <c>Of</c> at all (e.g. <c>IS_DUNGEON</c>) is unaffected.</summary>
        private static void ValidateCombatScopeConditions(RecipeSet set, SkillRecipe r, List<RecipeCondition> list, string path)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (Contains(Vocabulary.OfCapableConditions, c.Type) && c.Of == OfSelector.SELF)
                    RecipeParser.Err(set, r, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "].Of", "E_COMBAT_SELF_COND",
                        c.Type + " defaults/resolves to Of: SELF, which is undefined on a COMBAT-scoped " +
                        "recipe (no owner) — use TRIGGER_TARGET or TRIGGER_SOURCE, or a combat-level condition");
            }
        }

        private static bool IsSelfLikeTarget(TargetKind k)
        {
            return k == TargetKind.SELF || k == TargetKind.CASTER || k == TargetKind.ALLY_ALL ||
                   k == TargetKind.ALLY_ALL_OTHERS || k == TargetKind.ALLY_BY_RANK || k == TargetKind.ENEMY_ALL;
        }

        /// <summary>Loot-grant effect vocabulary — verb spec §6.1/§6.2. AFFIX_ROLL is further always
        /// rejected (reserved for M-LG4).</summary>
        private static readonly EffectKind[] LootGrantEffects =
        {
            EffectKind.GOLD_GRANT, EffectKind.ITEM_TAG_GRANT, EffectKind.LOOT_SCALE, EffectKind.AFFIX_ROLL
        };

        /// <summary>Conditions permitted on ON_COMBAT_LOOT — verb spec §6.1: "the pure-replicated-read
        /// subset"; combat-turn conditions (ROLL_TIER, FOCUS_SPENT, ...) have no context at combat end.</summary>
        private static readonly ConditionKind[] LootAllowedConditions =
        {
            ConditionKind.HP_THRESHOLD, ConditionKind.CHARACTER_TYPE
        };

        private static void ValidateLootConditionScope(RecipeSet set, SkillRecipe r, List<RecipeCondition> list, string path)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                string p = path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (!Contains(LootAllowedConditions, list[i].Type))
                    RecipeParser.Err(set, r, p + ".Type", "E_LOOT_COND_SCOPE",
                        list[i].Type + " is not permitted on ON_COMBAT_LOOT (only HP_THRESHOLD, CHARACTER_TYPE, Negate)");
            }
        }

        private static void ValidateEffect(RecipeSet set, SkillRecipe r, RecipeEffect e, string path)
        {
            // --- target/trigger compatibility ---
            if (e.Target == TargetKind.TRIGGER_TARGET || e.Target == TargetKind.TRIGGER_TARGET_POSITION)
            {
                // Encounter Modifiers spec §4.3: a COMBAT-scoped ON_COMBAT_START recipe IS bound a
                // TRIGGER_TARGET (the entity being initialized, owned by no one) — unlike an OWNED
                // ON_COMBAT_START recipe, which carries none (RecipeDispatcher.Fire's owned pass never
                // sets TriggerTarget for this trigger). TriggersWithTarget therefore cannot simply list
                // ON_COMBAT_START unconditionally; the COMBAT-scope carve-out is checked here instead.
                bool combatStartWithTarget = r.Trigger == TriggerKind.ON_COMBAT_START && r.Scope == RecipeScope.COMBAT;
                if (!combatStartWithTarget && !Contains(TriggersWithTarget, r.Trigger))
                    RecipeParser.Warn(set, r, path + ".Target", "W_NO_TRIGGER_TARGET",
                        r.Trigger + " carries no trigger target; this effect will be a no-op");
            }
            if (e.Target == TargetKind.TRIGGER_SOURCE && !Contains(TriggersWithSource, r.Trigger))
                RecipeParser.Warn(set, r, path + ".Target", "W_NO_TRIGGER_SOURCE",
                    r.Trigger + " carries no trigger source; this effect will be a no-op");
            if (e.Target == TargetKind.ALLY_BY_RANK && e.Rank == null)
                RecipeParser.Err(set, r, path + ".Rank", "E_RANK_MISSING", "ALLY_BY_RANK requires a Rank block");

            // --- loot-grant effect scope (verb spec §6.1/§6.2): grant effects only fire ON_COMBAT_LOOT,
            //     and ON_COMBAT_LOOT accepts only grant effects. AFFIX_ROLL is further always reserved. ---
            bool isGrantEffect = Contains(LootGrantEffects, e.Type);
            if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
            {
                if (!isGrantEffect)
                    RecipeParser.Err(set, r, path + ".Type", "E_LOOT_EFFECT_SCOPE",
                        e.Type + " is not part of the ON_COMBAT_LOOT restricted vocabulary (only GOLD_GRANT, " +
                        "ITEM_TAG_GRANT, LOOT_SCALE; AFFIX_ROLL is reserved)");
            }
            else if (isGrantEffect)
            {
                RecipeParser.Err(set, r, path + ".Type", "E_LOOT_EFFECT_SCOPE",
                    e.Type + " is only valid on ON_COMBAT_LOOT");
            }
            if (e.Type == EffectKind.AFFIX_ROLL)
                RecipeParser.Err(set, r, path, "E_LOOT_RESERVED",
                    "AFFIX_ROLL is reserved; the v1 validator rejects it until M-LG4");

            switch (e.Type)
            {
                case EffectKind.ADD_STATUS:
                case EffectKind.REMOVE_STATUS:
                {
                    bool hasStatus = !string.IsNullOrEmpty(e.Status);
                    bool hasOneOf = e.StatusOneOf != null && e.StatusOneOf.Count > 0;
                    bool hasFromSelection = !string.IsNullOrEmpty(e.StatusFromSelection);
                    int howMany = (hasStatus ? 1 : 0) + (hasOneOf ? 1 : 0) + (hasFromSelection ? 1 : 0);
                    if (howMany == 0)
                        RecipeParser.Err(set, r, path, "E_STATUS_MISSING",
                            e.Type + " requires Status, StatusOneOf, or StatusFromSelection");
                    if (howMany > 1)
                        RecipeParser.Err(set, r, path, "E_STATUS_AMBIGUOUS",
                            "Status, StatusOneOf and StatusFromSelection are mutually exclusive");
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
                case EffectKind.SELECTION_SET:
                {
                    if (string.IsNullOrEmpty(e.Name))
                        RecipeParser.Err(set, r, path + ".Name", "E_SELECTION_NAME_MISSING", "SELECTION_SET requires Name");
                    if (e.OneOfWeighted == null || e.OneOfWeighted.Count == 0)
                        RecipeParser.Err(set, r, path + ".OneOfWeighted", "E_VALUE_MISSING",
                            "SELECTION_SET requires a non-empty OneOfWeighted");
                    break;
                }
                case EffectKind.EVENT_BANNER:
                {
                    if (string.IsNullOrEmpty(e.LocKey) && string.IsNullOrEmpty(e.FallbackText))
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING", "EVENT_BANNER requires LocKey and/or FallbackText");
                    if (e.DurationMs.HasValue && e.DurationMs.Value < 0)
                        RecipeParser.Err(set, r, path + ".DurationMs", "E_RANGE", "DurationMs must be >= 0");
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
                    // GATE C (Encounter Modifiers spec §5/§8.1): FlatValueFrom "TARGET_MXHP_PCT" reads the
                    // effect's own Percent field (sign + magnitude) — a plain FlatPercent STAT_CHANGE on
                    // Stat "MXHP" is a DIFFERENT, unsupported native path (InteractableHelper.
                    // GetStatChangePercentValue only handles "HP"/"XP" and throws for MXHP). A
                    // PercentFromSelection table (§6.1 "PercentFromSelection" sugar, M-EM3) is an
                    // equally-valid alternative source of Percent, authored only by the generator — it
                    // must not trip this "no Percent at all" warning.
                    if (string.Equals(e.FlatValueFrom, Vocabulary.SourceTargetMxhpPct, StringComparison.Ordinal)
                        && !e.Percent.HasValue && string.IsNullOrEmpty(e.PercentFromSelection))
                        RecipeParser.Warn(set, r, path + ".Percent", "W_MXHP_PCT_NO_PERCENT",
                            "FlatValueFrom TARGET_MXHP_PCT with no Percent/PercentFromSelection authored always resolves to 0 (no-op)");
                    if (string.Equals(e.Stat, "MXHP", StringComparison.Ordinal) && e.FlatPercent.HasValue)
                        RecipeParser.Err(set, r, path + ".FlatPercent", "E_MXHP_FLATPERCENT_UNSUPPORTED",
                            "STAT_CHANGE Stat \"MXHP\" + FlatPercent throws natively (InteractableHelper." +
                            "GetStatChangePercentValue only handles HP/XP, GATE C) — use FlatValueFrom " +
                            "\"TARGET_MXHP_PCT\" + Percent instead");
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
                case EffectKind.GOLD_GRANT:
                {
                    if (!e.MinGold.HasValue || !e.MaxGold.HasValue)
                        RecipeParser.Err(set, r, path, "E_GOLD_RANGE_MISSING", "GOLD_GRANT requires MinGold and MaxGold");
                    else if (e.MinGold.Value > e.MaxGold.Value)
                        RecipeParser.Err(set, r, path, "E_GOLD_RANGE_INVALID", "GOLD_GRANT MinGold must be <= MaxGold");
                    else if (e.MinGold.Value < 0)
                        RecipeParser.Err(set, r, path + ".MinGold", "E_RANGE", "GOLD_GRANT MinGold must be >= 0");
                    break;
                }
                case EffectKind.ITEM_TAG_GRANT:
                {
                    if (string.IsNullOrEmpty(e.Tag))
                        RecipeParser.Err(set, r, path + ".Tag", "E_ITEM_TAG_MISSING", "ITEM_TAG_GRANT requires Tag");
                    if (e.Stack < 1)
                        RecipeParser.Err(set, r, path + ".Stack", "E_RANGE", "ITEM_TAG_GRANT Stack must be >= 1");
                    // Rarity, when present, is NOT enum-validated here: eItemRarities membership is a
                    // game-ref concern with no decompile evidence available inside this pure-C# core
                    // (deferred to the M-LG2 Plugin unit, which has EGT §2 in scope).
                    break;
                }
                case EffectKind.LOOT_SCALE:
                {
                    if (string.IsNullOrEmpty(e.ConfigName))
                        RecipeParser.Err(set, r, path + ".ConfigName", "E_LOOT_SCALE_CONFIG_MISSING", "LOOT_SCALE requires ConfigName");
                    else if (!Contains(LootVocabulary.ScaleStackConfigNames, e.ConfigName))
                        RecipeParser.Err(set, r, path + ".ConfigName", "E_LOOT_SCALE_CONFIG",
                            "LOOT_SCALE ConfigName must be one of PARTY_XP, XP, CURRENCY_ADVENTURE, CURRENCY_LORE");
                    if (!e.Percent.HasValue)
                        RecipeParser.Err(set, r, path + ".Percent", "E_VALUE_MISSING", "LOOT_SCALE requires Percent");
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
                if (c.Type == ConditionKind.PARTY_HAS_FOLLOWER)
                    RecipeParser.Err(set, r, p + ".Type", "E_COND_CONTEXT",
                        "PARTY_HAS_FOLLOWER is legal only in statmodifiers.json (the combat dispatcher has no evaluator for it)");
                if (isV10 && Contains(Vocabulary.V11OnlyConditions, c.Type))
                    RecipeParser.Err(set, r, p + ".Type", "E_SCHEMA_GATE",
                        "condition " + c.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                bool isV11Here = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCurrent, StringComparison.Ordinal);
                if ((isV10 || isV11Here) && Contains(Vocabulary.V12OnlyConditions, c.Type))
                    RecipeParser.Err(set, r, p + ".Type", "E_SCHEMA_GATE",
                        "condition " + c.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
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

                // --- Encounter Modifiers spec §5 (v1.2, M-EM2) ---

                case ConditionKind.PARTY_AVG_LEVEL:
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "PARTY_AVG_LEVEL requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "PARTY_AVG_LEVEL requires a Comparator");
                    break;

                case ConditionKind.IS_DUNGEON:
                case ConditionKind.BOSS_FIGHT:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a boolean Value");
                    break;

                case ConditionKind.ENCOUNTER_PROPERTY:
                case ConditionKind.ENTITY_TAG:
                    if (string.IsNullOrEmpty(c.Value))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a string Value (enum member name)");
                    break;

                case ConditionKind.CONFIG_NAME_CONTAINS:
                    if (string.IsNullOrEmpty(c.Value))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "CONFIG_NAME_CONTAINS requires a string Value");
                    break;

                case ConditionKind.COMBAT_START_REAL:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "COMBAT_START_REAL requires a boolean Value");
                    if (r.Trigger != TriggerKind.ON_COMBAT_START)
                        RecipeParser.Err(set, r, p, "E_COND_TRIGGER_SCOPE", "COMBAT_START_REAL is ON_COMBAT_START only (§4.3)");
                    break;

                case ConditionKind.SELECTION_PRESENT:
                    if (string.IsNullOrEmpty(c.Name))
                        RecipeParser.Err(set, r, p + ".Name", "E_SELECTION_NAME_MISSING", "SELECTION_PRESENT requires Name");
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "SELECTION_PRESENT requires a boolean Value");
                    break;

                case ConditionKind.IS_ENEMY:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "IS_ENEMY requires a boolean Value");
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
