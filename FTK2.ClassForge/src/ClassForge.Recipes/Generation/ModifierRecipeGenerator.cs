using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Generation
{
    /// <summary>
    /// Plain, Core-agnostic transcription of one pack's <c>modifiers.json</c> row (Encounter Modifiers
    /// spec §3.2 <c>ModifierEntry</c>) — deliberately narrower and with no dependency on
    /// <c>ClassForge.Core.ModifierEntry</c> (this assembly takes no dependency on ClassForge.Core, so the
    /// Plugin caller maps the real parsed table onto this shape).
    /// </summary>
    public sealed class ModifierRow
    {
        public string Id;
        public int Weight;
        public string Status;

        /// <summary>Null/0 ⇒ this modifier carries no MaxHP delta (spec §3.2; §6.1's
        /// "PercentFromSelection ... 0/absent ⇒ effect omitted").</summary>
        public int? MaxHpPercent;
    }

    /// <summary>
    /// Plain transcription of one pack's <c>modifiers.json</c> (Encounter Modifiers spec §3.2), the input
    /// to <see cref="ModifierRecipeGenerator.Generate"/>. <see cref="Modifiers"/> MUST preserve authored
    /// array order — it is the weighted-pick walk order (spec §6.3, EOR L22814-28 verbatim).
    /// </summary>
    public sealed class ModifierTableInput
    {
        /// <summary>The combat-scoped selection recipe id this table feeds (spec §3.2
        /// <c>Selection.Recipe</c>) — e.g. <c>"SKILL_CF_ENCMOD_SELECT"</c>. Both the selection-slot name
        /// and the application recipe's id are DERIVED from this one id (see
        /// <see cref="ModifierRecipeGenerator.DeriveSelectionName"/> /
        /// <see cref="ModifierRecipeGenerator.DeriveApplyId"/>) — the table has no separate field for
        /// either, by design: one authored id, zero chance of the two drifting apart.</summary>
        public string SelectionRecipeId;

        public List<ModifierRow> Modifiers = new List<ModifierRow>();
    }

    /// <summary>The two recipes <see cref="ModifierRecipeGenerator.Generate"/> produced for one table,
    /// plus the derived selection-slot name (so a caller need not re-derive it).</summary>
    public sealed class GeneratedModifierRecipes
    {
        public SkillRecipe Select;
        public SkillRecipe Apply;
        public string SelectionName;
    }

    /// <summary>
    /// Encounter Modifiers spec §6.1 — the engine-owned generator: from one pack's <c>modifiers.json</c>
    /// (transcribed as <see cref="ModifierTableInput"/>), synthesizes the two combat-scoped recipes
    /// (<c>SKILL_CF_ENCMOD_SELECT</c> / <c>SKILL_CF_ENCMOD_APPLY</c> for the shipped table; ids derived
    /// generically for any other table) verbatim to the spec §6.1 shape, with the enemy gate resolved per
    /// Gate D (<c>IS_ENEMY {Of: TRIGGER_TARGET}</c>) and the MaxHP effect resolved per Gate C
    /// (<c>STAT_CHANGE FlatValueFrom: "TARGET_MXHP_PCT"</c>, percent sourced per-selection via the
    /// <c>PercentFromSelection</c>/<c>PercentFromSelectionTable</c> sugar, §6.1 remarks).
    /// <para>
    /// <b>Single source of truth (spec §6.1 closing note):</b> the table is authored, the recipes are
    /// derived — nothing here is itself authored content, so a hand-authored copy of either recipe can
    /// never drift from the table. Pure, no RNG, no game references — safe to call from an offline test or
    /// from the Plugin's pack-load path alike.
    /// </para>
    /// <para>
    /// <b>Naming convention (recorded, since the JSON schema has no separate field for either):</b> the
    /// selection-slot name (<c>CombatRuntime.Selections</c> key, and the EVENT_BANNER's
    /// <c>TextFromSelection</c>) is <paramref name="table"/>.<c>SelectionRecipeId</c> with a leading
    /// <c>"SKILL_"</c> stripped and a trailing <c>"_SELECT"</c> stripped —
    /// <c>"SKILL_CF_ENCMOD_SELECT"</c> → <c>"CF_ENCMOD"</c>, matching spec §6.1's worked example exactly.
    /// The application recipe's id is the same id with <c>"_SELECT"</c> replaced by <c>"_APPLY"</c> (or,
    /// absent that suffix, <c>"_APPLY"</c> appended). Two packs shipping distinct
    /// <c>Selection.Recipe</c> ids therefore never collide on either the recipe id or the selection-slot
    /// key — the one thing every packs's uniqueness already had to hold for their own recipe ids not to
    /// collide with each other in the merged book.
    /// </para>
    /// </summary>
    public static class ModifierRecipeGenerator
    {
        public const string SelectSuffix = "_SELECT";
        public const string ApplySuffix = "_APPLY";
        private const string SkillPrefix = "SKILL_";

        /// <summary>Selection-slot name shared by both generated recipes and used by
        /// <see cref="Runtime.ModifierReconstruction"/>'s auto-discovery (which independently re-derives
        /// it by reading the SELECTION_SET effect's own <c>Name</c> off the loaded book — this method's
        /// output is what ends up there).</summary>
        public static string DeriveSelectionName(string selectionRecipeId)
        {
            string s = selectionRecipeId ?? string.Empty;
            if (s.StartsWith(SkillPrefix, StringComparison.Ordinal)) s = s.Substring(SkillPrefix.Length);
            if (s.EndsWith(SelectSuffix, StringComparison.Ordinal)) s = s.Substring(0, s.Length - SelectSuffix.Length);
            return s;
        }

        public static string DeriveApplyId(string selectionRecipeId)
        {
            string s = selectionRecipeId ?? string.Empty;
            if (s.EndsWith(SelectSuffix, StringComparison.Ordinal))
                return s.Substring(0, s.Length - SelectSuffix.Length) + ApplySuffix;
            return s + ApplySuffix;
        }

        /// <summary>
        /// Generates the SELECT + APPLY recipe pair for one table. Returns null when the table has no
        /// selection recipe id or no modifiers (nothing to generate — the pack ships inert, matching
        /// M-EM1's "ships inert, no engine consumer yet" posture for a malformed/empty table rather than
        /// throwing or emitting broken recipes).
        /// </summary>
        public static GeneratedModifierRecipes Generate(ModifierTableInput table)
        {
            if (table == null || string.IsNullOrEmpty(table.SelectionRecipeId)) return null;
            if (table.Modifiers == null || table.Modifiers.Count == 0) return null;

            string selectionName = DeriveSelectionName(table.SelectionRecipeId);
            string applyId = DeriveApplyId(table.SelectionRecipeId);

            return new GeneratedModifierRecipes
            {
                SelectionName = selectionName,
                Select = BuildSelectRecipe(table, selectionName),
                Apply = BuildApplyRecipe(table, selectionName, applyId)
            };
        }

        // =====================================================================================
        // SKILL_CF_ENCMOD_SELECT — spec §6.1
        // =====================================================================================

        private static SkillRecipe BuildSelectRecipe(ModifierTableInput table, string selectionName)
        {
            var r = new SkillRecipe
            {
                Id = table.SelectionRecipeId,
                DisplayName = table.SelectionRecipeId,
                SchemaVersion = Vocabulary.SchemaVersionLoot,
                Scope = RecipeScope.COMBAT,
                Trigger = TriggerKind.ON_COMBAT_START,
                Priority = 10,
                VerboseLogTag = "Encounter modifier selection"
            };

            // §6.1 conditions verbatim, in authored order.
            r.Conditions.Add(BoolCond(ConditionKind.COMBAT_START_REAL, true));
            r.Conditions.Add(EnemyGateCond());                                   // Gate D
            r.Conditions.Add(IntCond(ConditionKind.PARTY_AVG_LEVEL, Comparator.GTE, 1));
            r.Conditions.Add(BoolCond(ConditionKind.BOSS_FIGHT, false));
            r.Conditions.Add(EntityTagCond("BOSS", negate: true));
            r.Conditions.Add(ConfigNameContainsCond("SCOURGE", negate: true));
            r.Conditions.Add(EncounterPropertyCond("BOSS", negate: true));
            r.Conditions.Add(EncounterPropertyCond("SPECIAL", negate: true));
            r.Conditions.Add(EncounterPropertyCond("SIEGE", negate: true));

            r.ProcChanceFormula = BuildProcChanceFormula(); // spec §5 example verbatim

            r.Budget = new RecipeBudget { Scope = BudgetScope.ONCE_PER_COMBAT, ConsumeOn = ConsumeOn.EVALUATION };

            var selectionSet = new RecipeEffect
            {
                Type = EffectKind.SELECTION_SET,
                Name = selectionName,
                OneOfWeighted = new List<WeightedValue>()
            };
            for (int i = 0; i < table.Modifiers.Count; i++)
            {
                var m = table.Modifiers[i];
                if (m == null || string.IsNullOrEmpty(m.Id)) continue; // defensive; Core already errors on this
                selectionSet.OneOfWeighted.Add(new WeightedValue { Value = m.Id, Weight = m.Weight });
            }
            r.Effects.Add(selectionSet);

            r.Effects.Add(new RecipeEffect
            {
                Type = EffectKind.EVENT_BANNER,
                LocKey = "CF_ENCMOD_BANNER",
                DurationMs = 4000,
                TextFromSelection = selectionName
            });

            return r;
        }

        /// <summary>Spec §5's worked example, verbatim: base 10/20/30 by party-level band (LTE 2 / LTE 5 /
        /// default), adjustments dungeon +5 / ambush +5 / quest-target -5, clamp 0..35.</summary>
        private static ProcChanceFormula BuildProcChanceFormula()
        {
            var f = new ProcChanceFormula { Min = 0, Max = 35 };

            f.Base.Add(new ProcChanceFormulaRow { Value = 10 });
            f.Base[0].Conditions.Add(IntCond(ConditionKind.PARTY_AVG_LEVEL, Comparator.LTE, 2));

            f.Base.Add(new ProcChanceFormulaRow { Value = 20 });
            f.Base[1].Conditions.Add(IntCond(ConditionKind.PARTY_AVG_LEVEL, Comparator.LTE, 5));

            f.Base.Add(new ProcChanceFormulaRow { Value = 30 }); // no Conditions -> always passes (default)

            var dungeon = new ProcChanceFormulaRow { Value = 5 };
            dungeon.Conditions.Add(BoolCond(ConditionKind.IS_DUNGEON, true));
            f.Adjustments.Add(dungeon);

            var ambush = new ProcChanceFormulaRow { Value = 5 };
            ambush.Conditions.Add(EncounterPropertyCond("AMBUSH", negate: false));
            f.Adjustments.Add(ambush);

            var questTarget = new ProcChanceFormulaRow { Value = -5 };
            questTarget.Conditions.Add(EncounterPropertyCond("QUEST_TARGET", negate: false));
            f.Adjustments.Add(questTarget);

            return f;
        }

        // =====================================================================================
        // SKILL_CF_ENCMOD_APPLY — spec §6.1
        // =====================================================================================

        private static SkillRecipe BuildApplyRecipe(ModifierTableInput table, string selectionName, string applyId)
        {
            var r = new SkillRecipe
            {
                Id = applyId,
                DisplayName = applyId,
                SchemaVersion = Vocabulary.SchemaVersionLoot,
                Scope = RecipeScope.COMBAT,
                Trigger = TriggerKind.ON_COMBAT_START,
                Priority = 20,
                VerboseLogTag = "Encounter modifier application"
            };

            r.Conditions.Add(BoolCond(ConditionKind.COMBAT_START_REAL, true));
            r.Conditions.Add(EnemyGateCond());                                   // Gate D
            r.Conditions.Add(new RecipeCondition
            {
                Type = ConditionKind.SELECTION_PRESENT,
                Name = selectionName,
                ValueBool = true,
                Value = "true"
            });

            r.Budget = new RecipeBudget { Scope = BudgetScope.ONCE_PER_TARGET_PER_COMBAT, ConsumeOn = ConsumeOn.EFFECT_APPLIED };

            // CombatRuntime.Selections canonically holds the MODIFIER id (matching ModifierReconstruction's
            // already-shipped choice and spec §11's "activeModifierId") -- so ADD_STATUS needs the
            // table-driven StatusFromSelection variant (modifier id -> status id) rather than the plain
            // raw-echo form.
            var statusTable = new List<SelectionStatusEntry>();
            for (int i = 0; i < table.Modifiers.Count; i++)
            {
                var m = table.Modifiers[i];
                if (m == null || string.IsNullOrEmpty(m.Id) || string.IsNullOrEmpty(m.Status)) continue;
                statusTable.Add(new SelectionStatusEntry { Value = m.Id, StatusId = m.Status });
            }
            r.Effects.Add(new RecipeEffect
            {
                Type = EffectKind.ADD_STATUS,
                Target = TargetKind.TRIGGER_TARGET,
                StatusFromSelection = selectionName,
                StatusFromSelectionTable = statusTable
            });

            // Gate C + §6.1 "PercentFromSelection" sugar: the MaxHP delta, table-driven per selected
            // modifier. Modifiers with no MaxHpPercent simply never get a row -> 0/absent -> the whole
            // effect is omitted for them at plan time (RecipeDispatcher.PlanEffect), never a zero-value
            // STAT_CHANGE.
            var mxhpTable = new List<SelectionPercentEntry>();
            for (int i = 0; i < table.Modifiers.Count; i++)
            {
                var m = table.Modifiers[i];
                if (m == null || string.IsNullOrEmpty(m.Id)) continue;
                if (!m.MaxHpPercent.HasValue || m.MaxHpPercent.Value == 0) continue;
                mxhpTable.Add(new SelectionPercentEntry { Value = m.Id, Percent = m.MaxHpPercent.Value });
            }
            r.Effects.Add(new RecipeEffect
            {
                Type = EffectKind.STAT_CHANGE,
                Target = TargetKind.TRIGGER_TARGET,
                Stat = "MXHP",
                StatChangeType = "MAGICAL", // non-HP stat convention (matches every shipped FOC STAT_CHANGE)
                FlatValueFrom = Vocabulary.SourceTargetMxhpPct,
                PercentFromSelection = selectionName,
                PercentFromSelectionTable = mxhpTable
            });

            return r;
        }

        // =====================================================================================
        // Condition builders
        // =====================================================================================

        private static RecipeCondition BoolCond(ConditionKind type, bool value)
        {
            return new RecipeCondition { Type = type, ValueBool = value, Value = value ? "true" : "false" };
        }

        private static RecipeCondition IntCond(ConditionKind type, Comparator cmp, int value)
        {
            return new RecipeCondition
            {
                Type = type,
                Comparator = cmp,
                HasComparator = true,
                ValueInt = value,
                Value = value.ToString(CultureInfo.InvariantCulture)
            };
        }

        /// <summary>Gate D: <c>IS_ENEMY {Of: TRIGGER_TARGET, Value: true}</c> — the enemy-side gate both
        /// generated recipes share (spec §6.2's "..." placeholder, resolved).</summary>
        private static RecipeCondition EnemyGateCond()
        {
            return new RecipeCondition
            {
                Type = ConditionKind.IS_ENEMY,
                Of = OfSelector.TRIGGER_TARGET,
                ValueBool = true,
                Value = "true"
            };
        }

        private static RecipeCondition EntityTagCond(string tagName, bool negate)
        {
            return new RecipeCondition
            {
                Type = ConditionKind.ENTITY_TAG,
                Of = OfSelector.TRIGGER_TARGET,
                Value = tagName,
                Negate = negate
            };
        }

        private static RecipeCondition ConfigNameContainsCond(string substring, bool negate)
        {
            return new RecipeCondition
            {
                Type = ConditionKind.CONFIG_NAME_CONTAINS,
                Of = OfSelector.TRIGGER_TARGET,
                Value = substring,
                Negate = negate
            };
        }

        private static RecipeCondition EncounterPropertyCond(string propertyName, bool negate)
        {
            return new RecipeCondition
            {
                Type = ConditionKind.ENCOUNTER_PROPERTY,
                Value = propertyName,
                Negate = negate
            };
        }
    }
}
