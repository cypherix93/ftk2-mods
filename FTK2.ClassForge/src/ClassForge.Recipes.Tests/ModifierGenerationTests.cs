using System;
using System.Collections.Generic;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Json;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// M-EM3 (Encounter Modifiers spec §6.1/§12): golden tests that
    /// <see cref="ModifierRecipeGenerator.Generate"/>, fed the SHIPPED
    /// <c>CF_PACK_ENCOUNTER_MODIFIERS/modifiers.json</c> (mirrored here as
    /// <c>fixtures/encmod_modifiers.json</c> — a byte-for-byte copy, not a re-authored approximation),
    /// produces recipes semantically equal to spec §6.1's worked example; plus an end-to-end
    /// selection-&gt;application flow test through the dispatcher.
    /// </summary>
    public static class ModifierGenerationTests
    {
        public static void Run(TestRunner t)
        {
            RunGoldenGeneration(t);
            RunSelectionApplyFlow(t);
        }

        // =====================================================================================
        // Fixture -> ModifierTableInput (test-only mapping; production code never round-trips JSON
        // for this -- the Plugin maps ClassForge.Core.ModifierTable directly, see RecipeEngineHost).
        // =====================================================================================

        private static ModifierTableInput LoadShippedTable()
        {
            // Deliberately under fixtures/encmod/, NOT fixtures/ itself: Fixtures.All() (non-recursive
            // Directory.GetFiles) feeds every top-level fixtures/*.json into the generic
            // "every fixture parses as a skillrecipes.json" tests (ParserTests/MechanicTests) -- this file
            // is a modifiers.json shape, not a skillrecipes.json shape, and must not be swept into those.
            string json = Fixtures.Read("encmod/encmod_modifiers.json");
            JsonValue root;
            string error;
            if (!JsonParser.TryParse(json, out root, out error))
                throw new InvalidOperationException("fixtures/encmod/encmod_modifiers.json failed to parse: " + error);

            var table = new ModifierTableInput();
            var selection = root.Get("Selection");
            var recipeNode = selection != null ? selection.Get("Recipe") : null;
            table.SelectionRecipeId = recipeNode != null ? recipeNode.StringValue : null;

            var modifiers = root.Get("Modifiers");
            if (modifiers != null)
            {
                for (int i = 0; i < modifiers.Items.Count; i++)
                {
                    var m = modifiers.Items[i];
                    var row = new ModifierRow
                    {
                        Id = Str(m, "Id"),
                        Weight = (int)m.Get("Weight").NumberValue,
                        Status = Str(m, "Status")
                    };
                    var maxHp = m.Get("MaxHpPercent");
                    if (maxHp != null) row.MaxHpPercent = (int)maxHp.NumberValue;
                    table.Modifiers.Add(row);
                }
            }
            return table;
        }

        private static string Str(JsonValue node, string key)
        {
            var v = node.Get(key);
            return v != null ? v.StringValue : null;
        }

        // =====================================================================================
        // Golden generation — structure, not raw JSON strings (§12.1 "semantically equal")
        // =====================================================================================

        private static void RunGoldenGeneration(TestRunner t)
        {
            t.Section("M-EM3: generation from the shipped modifiers.json (§6.1, §12)");

            var table = LoadShippedTable();

            t.Case("shipped table transcribes correctly (sanity on the fixture/mapper itself)", () =>
            {
                Check.Eq("SKILL_CF_ENCMOD_SELECT", table.SelectionRecipeId, "Selection.Recipe");
                Check.Eq(10, table.Modifiers.Count, "10 modifiers");
                int sum = 0;
                for (int i = 0; i < table.Modifiers.Count; i++) sum += table.Modifiers[i].Weight;
                Check.Eq(85, sum, "weights sum to 85");
            });

            GeneratedModifierRecipes generated = null;
            t.Case("Generate() produces a non-null pair for the shipped table", () =>
            {
                generated = ModifierRecipeGenerator.Generate(table);
                Check.True(generated != null, "generation succeeded");
                Check.True(generated.Select != null, "SELECT recipe produced");
                Check.True(generated.Apply != null, "APPLY recipe produced");
                Check.Eq("CF_ENCMOD", generated.SelectionName, "selection name derived per spec §6.1's worked example");
            });

            t.Case("both generated recipes validate with zero errors", () =>
            {
                var set = new RecipeSet();
                set.Add(generated.Select);
                set.Add(generated.Apply);
                RecipeValidator.Validate(set);
                Check.NoErrors(set, "generated SELECT+APPLY");
                Check.True(generated.Select.IsLive, "SELECT is live");
                Check.True(generated.Apply.IsLive, "APPLY is live");
            });

            // ---- SELECT recipe shape ----

            t.Case("SELECT: id/schema/scope/trigger/priority", () =>
            {
                var r = generated.Select;
                Check.Eq("SKILL_CF_ENCMOD_SELECT", r.Id, "id");
                Check.Eq("1.2", r.SchemaVersion, "SchemaVersion");
                Check.True(r.Scope == RecipeScope.COMBAT, "Scope COMBAT");
                Check.True(r.Trigger == TriggerKind.ON_COMBAT_START, "Trigger ON_COMBAT_START");
                Check.Eq(10, r.Priority, "Priority 10 (before APPLY's 20)");
            });

            t.Case("SELECT: the 9 §6.1 conditions verbatim, in order", () =>
            {
                var c = generated.Select.Conditions;
                Check.Eq(9, c.Count, "9 conditions");

                Check.True(c[0].Type == ConditionKind.COMBAT_START_REAL, "c0 COMBAT_START_REAL");
                Check.True(c[0].ValueBool == true, "c0 Value true");

                Check.True(c[1].Type == ConditionKind.IS_ENEMY, "c1 IS_ENEMY (Gate D)");
                Check.True(c[1].Of == OfSelector.TRIGGER_TARGET, "c1 Of TRIGGER_TARGET");
                Check.True(c[1].ValueBool == true, "c1 Value true");

                Check.True(c[2].Type == ConditionKind.PARTY_AVG_LEVEL, "c2 PARTY_AVG_LEVEL");
                Check.True(c[2].Comparator == Comparator.GTE, "c2 GTE");
                Check.Eq(1, c[2].ValueInt.Value, "c2 Value 1");

                Check.True(c[3].Type == ConditionKind.BOSS_FIGHT, "c3 BOSS_FIGHT");
                Check.True(c[3].ValueBool == false, "c3 Value false");

                Check.True(c[4].Type == ConditionKind.ENTITY_TAG, "c4 ENTITY_TAG");
                Check.True(c[4].Of == OfSelector.TRIGGER_TARGET, "c4 Of TRIGGER_TARGET");
                Check.Eq("BOSS", c[4].Value, "c4 Value BOSS");
                Check.True(c[4].Negate, "c4 Negate");

                Check.True(c[5].Type == ConditionKind.CONFIG_NAME_CONTAINS, "c5 CONFIG_NAME_CONTAINS");
                Check.True(c[5].Of == OfSelector.TRIGGER_TARGET, "c5 Of TRIGGER_TARGET");
                Check.Eq("SCOURGE", c[5].Value, "c5 Value SCOURGE");
                Check.True(c[5].Negate, "c5 Negate");

                Check.True(c[6].Type == ConditionKind.ENCOUNTER_PROPERTY, "c6 ENCOUNTER_PROPERTY");
                Check.Eq("BOSS", c[6].Value, "c6 Value BOSS");
                Check.True(c[6].Negate, "c6 Negate");

                Check.True(c[7].Type == ConditionKind.ENCOUNTER_PROPERTY, "c7 ENCOUNTER_PROPERTY");
                Check.Eq("SPECIAL", c[7].Value, "c7 Value SPECIAL");
                Check.True(c[7].Negate, "c7 Negate");

                Check.True(c[8].Type == ConditionKind.ENCOUNTER_PROPERTY, "c8 ENCOUNTER_PROPERTY");
                Check.Eq("SIEGE", c[8].Value, "c8 Value SIEGE");
                Check.True(c[8].Negate, "c8 Negate");
            });

            t.Case("SELECT: ProcChanceFormula matches spec §5's example exactly", () =>
            {
                var f = generated.Select.ProcChanceFormula;
                Check.True(f != null, "formula present");
                Check.Eq(0, f.Min.Value, "Min 0");
                Check.Eq(35, f.Max.Value, "Max 35");

                Check.Eq(3, f.Base.Count, "3 base rows");
                Check.Eq(10, f.Base[0].Value, "row0 value 10");
                Check.Eq(1, f.Base[0].Conditions.Count, "row0 has 1 condition");
                Check.True(f.Base[0].Conditions[0].Type == ConditionKind.PARTY_AVG_LEVEL, "row0 PARTY_AVG_LEVEL");
                Check.True(f.Base[0].Conditions[0].Comparator == Comparator.LTE, "row0 LTE");
                Check.Eq(2, f.Base[0].Conditions[0].ValueInt.Value, "row0 <= 2");

                Check.Eq(20, f.Base[1].Value, "row1 value 20");
                Check.True(f.Base[1].Conditions[0].Comparator == Comparator.LTE, "row1 LTE");
                Check.Eq(5, f.Base[1].Conditions[0].ValueInt.Value, "row1 <= 5");

                Check.Eq(30, f.Base[2].Value, "row2 (default) value 30");
                Check.Eq(0, f.Base[2].Conditions.Count, "row2 has no conditions -- unconditional default");

                Check.Eq(3, f.Adjustments.Count, "3 adjustment rows");
                Check.Eq(5, f.Adjustments[0].Value, "dungeon +5");
                Check.True(f.Adjustments[0].Conditions[0].Type == ConditionKind.IS_DUNGEON, "adj0 IS_DUNGEON");
                Check.Eq(5, f.Adjustments[1].Value, "ambush +5");
                Check.True(f.Adjustments[1].Conditions[0].Type == ConditionKind.ENCOUNTER_PROPERTY, "adj1 ENCOUNTER_PROPERTY");
                Check.Eq("AMBUSH", f.Adjustments[1].Conditions[0].Value, "adj1 AMBUSH");
                Check.Eq(-5, f.Adjustments[2].Value, "quest-target -5");
                Check.Eq("QUEST_TARGET", f.Adjustments[2].Conditions[0].Value, "adj2 QUEST_TARGET");
            });

            t.Case("SELECT: Budget ONCE_PER_COMBAT/EVALUATION", () =>
            {
                Check.True(generated.Select.Budget.Scope == BudgetScope.ONCE_PER_COMBAT, "scope");
                Check.True(generated.Select.Budget.ConsumeOn == ConsumeOn.EVALUATION, "consume-on");
            });

            t.Case("SELECT: SELECTION_SET carries the full weighted table, authored order, then EVENT_BANNER", () =>
            {
                var effects = generated.Select.Effects;
                Check.Eq(2, effects.Count, "2 effects");

                Check.True(effects[0].Type == EffectKind.SELECTION_SET, "effect0 SELECTION_SET");
                Check.Eq("CF_ENCMOD", effects[0].Name, "effect0 Name");
                Check.Eq(10, effects[0].OneOfWeighted.Count, "10 weighted rows");
                var expectedIds = new[] { "ARMORED", "RESISTANT", "FRENZIED", "SWIFT", "VETERAN", "WEALTHY", "CURSED", "REGENERATING", "GLASSCANNON", "TREASUREGUARDED" };
                var expectedWeights = new[] { 12, 12, 10, 10, 10, 8, 6, 6, 6, 5 };
                for (int i = 0; i < expectedIds.Length; i++)
                {
                    Check.Eq(expectedIds[i], effects[0].OneOfWeighted[i].Value, "row " + i + " id");
                    Check.Eq(expectedWeights[i], effects[0].OneOfWeighted[i].Weight, "row " + i + " weight");
                }

                Check.True(effects[1].Type == EffectKind.EVENT_BANNER, "effect1 EVENT_BANNER");
                Check.Eq("CF_ENCMOD_BANNER", effects[1].LocKey, "LocKey");
                Check.Eq(4000, effects[1].DurationMs.Value, "DurationMs 4000");
                Check.Eq("CF_ENCMOD", effects[1].TextFromSelection, "TextFromSelection");
            });

            // ---- APPLY recipe shape ----

            t.Case("APPLY: id/schema/scope/trigger/priority", () =>
            {
                var r = generated.Apply;
                Check.Eq("SKILL_CF_ENCMOD_APPLY", r.Id, "id");
                Check.Eq("1.2", r.SchemaVersion, "SchemaVersion");
                Check.True(r.Scope == RecipeScope.COMBAT, "Scope COMBAT");
                Check.True(r.Trigger == TriggerKind.ON_COMBAT_START, "Trigger ON_COMBAT_START");
                Check.Eq(20, r.Priority, "Priority 20 (after SELECT's 10)");
            });

            t.Case("APPLY: COMBAT_START_REAL + IS_ENEMY + SELECTION_PRESENT", () =>
            {
                var c = generated.Apply.Conditions;
                Check.Eq(3, c.Count, "3 conditions");
                Check.True(c[0].Type == ConditionKind.COMBAT_START_REAL && c[0].ValueBool == true, "c0");
                Check.True(c[1].Type == ConditionKind.IS_ENEMY && c[1].Of == OfSelector.TRIGGER_TARGET && c[1].ValueBool == true, "c1 (Gate D)");
                Check.True(c[2].Type == ConditionKind.SELECTION_PRESENT, "c2 SELECTION_PRESENT");
                Check.Eq("CF_ENCMOD", c[2].Name, "c2 Name");
                Check.True(c[2].ValueBool == true, "c2 Value true");
            });

            t.Case("APPLY: Budget ONCE_PER_TARGET_PER_COMBAT/EFFECT_APPLIED", () =>
            {
                Check.True(generated.Apply.Budget.Scope == BudgetScope.ONCE_PER_TARGET_PER_COMBAT, "scope");
                Check.True(generated.Apply.Budget.ConsumeOn == ConsumeOn.EFFECT_APPLIED, "consume-on");
            });

            t.Case("APPLY: ADD_STATUS StatusFromSelection + STAT_CHANGE MXHP table-driven per selection", () =>
            {
                var effects = generated.Apply.Effects;
                Check.Eq(2, effects.Count, "2 effects");

                Check.True(effects[0].Type == EffectKind.ADD_STATUS, "effect0 ADD_STATUS");
                Check.True(effects[0].Target == TargetKind.TRIGGER_TARGET, "effect0 Target TRIGGER_TARGET");
                Check.Eq("CF_ENCMOD", effects[0].StatusFromSelection, "effect0 StatusFromSelection");
                Check.Eq(10, effects[0].StatusFromSelectionTable.Count, "10 modifier-id -> status-id rows");
                Check.Eq("STATUS_CF_ENCMOD_VETERAN", FindStatusRow(effects[0].StatusFromSelectionTable, "VETERAN"), "VETERAN row");
                Check.Eq("STATUS_CF_ENCMOD_ARMORED", FindStatusRow(effects[0].StatusFromSelectionTable, "ARMORED"), "ARMORED row");

                var mxhp = effects[1];
                Check.True(mxhp.Type == EffectKind.STAT_CHANGE, "effect1 STAT_CHANGE");
                Check.True(mxhp.Target == TargetKind.TRIGGER_TARGET, "effect1 Target TRIGGER_TARGET");
                Check.Eq("MXHP", mxhp.Stat, "effect1 Stat MXHP");
                Check.Eq(ClassForge.Recipes.Model.Vocabulary.SourceTargetMxhpPct, mxhp.FlatValueFrom, "effect1 FlatValueFrom TARGET_MXHP_PCT (Gate C)");
                Check.Eq("CF_ENCMOD", mxhp.PercentFromSelection, "effect1 PercentFromSelection");
                Check.True(!mxhp.Percent.HasValue, "effect1 no static Percent (per-selection instead)");

                // Exactly the 5 modifiers with a MaxHpPercent (spec §3.2 table): SWIFT -10, VETERAN +10,
                // WEALTHY +10, GLASSCANNON -20, TREASUREGUARDED +15. Absent modifiers (ARMORED, RESISTANT,
                // FRENZIED, CURSED, REGENERATING) never get a row -- the "0/absent -> omitted" rule.
                var table2 = mxhp.PercentFromSelectionTable;
                Check.Eq(5, table2.Count, "5 rows (only modifiers with a non-zero MaxHpPercent)");
                var expected = new Dictionary<string, int>
                {
                    { "SWIFT", -10 }, { "VETERAN", 10 }, { "WEALTHY", 10 }, { "GLASSCANNON", -20 }, { "TREASUREGUARDED", 15 }
                };
                foreach (var kv in expected)
                {
                    bool found = false;
                    for (int i = 0; i < table2.Count; i++)
                        if (string.Equals(table2[i].Value, kv.Key, StringComparison.Ordinal)) { Check.Eq(kv.Value, table2[i].Percent, kv.Key + " percent"); found = true; }
                    Check.True(found, kv.Key + " present in the table");
                }
                var noRow = new[] { "ARMORED", "RESISTANT", "FRENZIED", "CURSED", "REGENERATING" };
                for (int i = 0; i < noRow.Length; i++)
                {
                    for (int j = 0; j < table2.Count; j++)
                        Check.True(!string.Equals(table2[j].Value, noRow[i], StringComparison.Ordinal), noRow[i] + " must not have a row");
                }
            });

            t.Case("Generate() derives ids/selection-name generically, not just for the shipped ids", () =>
            {
                Check.Eq("CF_FOO", ModifierRecipeGenerator.DeriveSelectionName("SKILL_CF_FOO_SELECT"), "SKILL_ prefix + _SELECT suffix stripped");
                Check.Eq("SKILL_CF_FOO_APPLY", ModifierRecipeGenerator.DeriveApplyId("SKILL_CF_FOO_SELECT"), "_SELECT -> _APPLY");
                Check.Eq("BARE_APPLY", ModifierRecipeGenerator.DeriveApplyId("BARE"), "no _SELECT suffix -> _APPLY appended");
            });

            t.Case("Generate() returns null for an empty/malformed table (ships inert, per M-EM1 posture)", () =>
            {
                Check.True(ModifierRecipeGenerator.Generate(null) == null, "null table");
                Check.True(ModifierRecipeGenerator.Generate(new ModifierTableInput()) == null, "no SelectionRecipeId, no Modifiers");
                Check.True(ModifierRecipeGenerator.Generate(new ModifierTableInput { SelectionRecipeId = "SKILL_X_SELECT" }) == null, "no Modifiers");
            });
        }

        // =====================================================================================
        // Selection -> application flow through the dispatcher (scripted combat)
        // =====================================================================================

        private static void RunSelectionApplyFlow(TestRunner t)
        {
            t.Section("M-EM3: selection -> application flow through the dispatcher");

            t.Case("eligible fight, forced chance: status + MXHP applied to each enemy event; banner planned", () =>
            {
                var table = LoadShippedTable();
                var generated = ModifierRecipeGenerator.Generate(table);
                var set = new RecipeSet();
                set.Add(generated.Select);
                set.Add(generated.Apply);
                RecipeValidator.Validate(set);
                Check.NoErrors(set, "generated recipes");

                var ctx = Scenario.Standard();
                var foe1 = new FakeEntity("C_FOE1", 1); foe1.EnemyFlag = true;
                var foe2 = new FakeEntity("D_FOE2", 1); foe2.EnemyFlag = true;
                ctx.AddEntity(foe1).AddEntity(foe2);

                var rng = new FakeRandom(1);
                rng.ScriptChance(true);   // gate succeeds
                rng.ScriptInt(50);        // walk: ARMORED<=12, RESISTANT<=24, FRENZIED<=34, SWIFT<=44, VETERAN<=54 -> VETERAN

                var log = new FakeLog();
                var dispatcher = new RecipeDispatcher(set, rng, log);

                var plan1 = dispatcher.OnCombatStart(ctx, new CombatStartEvent { Entity = foe1, TrySkillProc = true });
                Check.Eq(2, rng.Draws, "first enemy's event: exactly 2 draws (SELECT's gate+pick); APPLY takes zero");

                var add1 = FindAddStatus(plan1, "C_FOE1");
                Check.True(add1 != null, "foe1 gets ADD_STATUS");
                Check.Eq("VETERAN", dispatcher.State.Sync(ctx).GetSelection("CF_ENCMOD"), "Selections canonically holds the MODIFIER id");
                Check.Eq("STATUS_CF_ENCMOD_VETERAN", add1.StatusId, "StatusFromSelectionTable maps VETERAN -> its Status");

                var change1 = FindStatChange(plan1, "C_FOE1");
                Check.True(change1 != null, "foe1 gets the MXHP STAT_CHANGE (VETERAN carries +10%)");
                Check.Eq("MXHP", change1.Stat, "stat MXHP");
                Check.Eq(10, change1.FlatValue.Value, "flat = round(100 * 10 / 100) = 10 (foe1's default MXHP 100)");

                var banner = FindBanner(plan1);
                Check.True(banner != null, "banner planned on the same event");
                Check.Eq("CF_ENCMOD_BANNER", banner.LocKey, "banner LocKey");
                Check.Eq(4000, banner.DurationMs, "banner duration");
                Check.Eq("VETERAN", banner.SelectionValue, "banner carries the selected modifier id");

                // Second enemy's own SetInitiative event: SELECT's ONCE_PER_COMBAT budget is already
                // consumed (no re-fire, no re-draw) but APPLY re-evaluates for foe2 -- EOR's per-enemy
                // application shape (spec §6.2), zero additional engine draws.
                var plan2 = dispatcher.OnCombatStart(ctx, new CombatStartEvent { Entity = foe2, TrySkillProc = true });
                Check.Eq(2, rng.Draws, "second enemy's event takes zero further draws");
                var add2 = FindAddStatus(plan2, "D_FOE2");
                Check.True(add2 != null, "foe2 also gets ADD_STATUS (late-wave application)");
                Check.Eq("VETERAN", dispatcher.State.Sync(ctx).GetSelection("CF_ENCMOD"), "same modifier, still VETERAN");
                var change2 = FindStatChange(plan2, "D_FOE2");
                Check.True(change2 != null, "foe2 also gets the MXHP STAT_CHANGE");
            });

            t.Case("modifier with no MaxHpPercent (ARMORED): ADD_STATUS fires, STAT_CHANGE is omitted entirely", () =>
            {
                var table = LoadShippedTable();
                var generated = ModifierRecipeGenerator.Generate(table);
                var set = new RecipeSet();
                set.Add(generated.Select);
                set.Add(generated.Apply);
                RecipeValidator.Validate(set);

                var ctx = Scenario.Standard();
                var foe = new FakeEntity("C_FOE", 1); foe.EnemyFlag = true;
                ctx.AddEntity(foe);

                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                rng.ScriptInt(1); // roll 1 -> ARMORED (first row, weight 12)

                var dispatcher = new RecipeDispatcher(set, rng, new FakeLog());
                var plan = dispatcher.OnCombatStart(ctx, new CombatStartEvent { Entity = foe, TrySkillProc = true });

                Check.Eq("ARMORED", dispatcher.State.Sync(ctx).GetSelection("CF_ENCMOD"), "ARMORED selected");
                Check.True(FindAddStatus(plan, "C_FOE") != null, "ADD_STATUS still fires");
                Check.True(FindStatChange(plan, "C_FOE") == null, "no STAT_CHANGE action -- ARMORED has no MaxHpPercent");
            });

            t.Case("excluded fight (non-enemy trigger target): zero draws, zero plan", () =>
            {
                var table = LoadShippedTable();
                var generated = ModifierRecipeGenerator.Generate(table);
                var set = new RecipeSet();
                set.Add(generated.Select);
                set.Add(generated.Apply);
                RecipeValidator.Validate(set);

                var ctx = Scenario.Standard();
                var hero = new FakeEntity("A_HERO", 0); hero.EnemyFlag = false;
                ctx.AddEntity(hero);

                var rng = new FakeRandom(1);
                var dispatcher = new RecipeDispatcher(set, rng, new FakeLog());
                var plan = dispatcher.OnCombatStart(ctx, new CombatStartEvent { Entity = hero, TrySkillProc = true });

                Check.PlanCount(plan, 0, "IS_ENEMY false -> excluded, no plan");
                Check.Eq(0, rng.Draws, "zero draws on an excluded fight");
            });

            t.Case("debugProcChanceFormulaOverride forces a fixed chance regardless of the real formula (§12.6 SP smoke knob)", () =>
            {
                var table = LoadShippedTable();
                var generated = ModifierRecipeGenerator.Generate(table);
                var set = new RecipeSet();
                set.Add(generated.Select);
                set.Add(generated.Apply);
                RecipeValidator.Validate(set);

                var ctx = Scenario.Standard();
                var foe = new FakeEntity("C_FOE", 1); foe.EnemyFlag = true;
                ctx.AddEntity(foe);

                // Level-1 party (Scenario.Standard's ctx default) -> the REAL formula would compute a 10%
                // base; the override replaces the whole result with 50% instead. 50 (not 100) is chosen
                // deliberately so the gate still takes its normal NextChance draw (invariant 3: only a
                // chance >= 100 skips it) and LastChance is observable.
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                rng.ScriptInt(1); // the pick draw, forced deterministic too (roll 1 -> ARMORED)
                var dispatcher = new RecipeDispatcher(set, rng, new FakeLog(), debugProcChanceFormulaOverride: 50);
                var plan = dispatcher.OnCombatStart(ctx, new CombatStartEvent { Entity = foe, TrySkillProc = true });

                Check.True(rng.LastChance.HasValue && rng.LastChance.Value == 0.50m,
                    "override forced chance to 50% (would be 10% from the real formula for a level-1 party)");
                Check.True(FindAddStatus(plan, "C_FOE") != null, "modifier applied once the (overridden) gate succeeds");
            });

            t.Case("debugProcChanceFormulaOverride of 100 skips the gate draw entirely (invariant 3), same as authored ProcChance 100", () =>
            {
                var table = LoadShippedTable();
                var generated = ModifierRecipeGenerator.Generate(table);
                var set = new RecipeSet();
                set.Add(generated.Select);
                set.Add(generated.Apply);
                RecipeValidator.Validate(set);

                var ctx = Scenario.Standard();
                var foe = new FakeEntity("C_FOE", 1); foe.EnemyFlag = true;
                ctx.AddEntity(foe);

                var rng = new FakeRandom(1);
                rng.ScriptInt(1); // only the pick draw is taken
                var dispatcher = new RecipeDispatcher(set, rng, new FakeLog(), debugProcChanceFormulaOverride: 100);
                var plan = dispatcher.OnCombatStart(ctx, new CombatStartEvent { Entity = foe, TrySkillProc = true });

                Check.Eq(1, rng.Draws, "chance >= 100 takes zero gate draws -- only the pick draw remains");
                Check.True(!rng.LastChance.HasValue, "NextChance was never called");
                Check.True(FindAddStatus(plan, "C_FOE") != null, "modifier still applied");
            });
        }

        private static AddStatusAction FindAddStatus(IReadOnlyList<EngineAction> plan, string targetGuid)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var a = plan[i] as AddStatusAction;
                if (a != null && string.Equals(a.TargetGuid, targetGuid, StringComparison.Ordinal)) return a;
            }
            return null;
        }

        private static string FindStatusRow(List<SelectionStatusEntry> table, string modifierId)
        {
            for (int i = 0; i < table.Count; i++)
                if (string.Equals(table[i].Value, modifierId, StringComparison.Ordinal)) return table[i].StatusId;
            return null;
        }

        private static StatChangeAction FindStatChange(IReadOnlyList<EngineAction> plan, string targetGuid)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var a = plan[i] as StatChangeAction;
                if (a != null && string.Equals(a.TargetGuid, targetGuid, StringComparison.Ordinal)) return a;
            }
            return null;
        }

        private static EventBannerAction FindBanner(IReadOnlyList<EngineAction> plan)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var a = plan[i] as EventBannerAction;
                if (a != null) return a;
            }
            return null;
        }
    }
}
