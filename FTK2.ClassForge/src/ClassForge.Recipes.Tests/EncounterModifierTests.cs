using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// Encounter Modifiers spec §12.1-4 (M-EM2): Scope:COMBAT parse/validator, ProcChanceFormula,
    /// SELECTION_SET weighted walk, StatusFromSelection, draw-count constancy, §4.5 reconstruction, and the
    /// TARGET_MXHP_PCT golden tests (GATE C). The EntityAdapter status-passives union (§4.4) is
    /// Plugin-side, game-referencing code with no equivalent in this pure-C# assembly — it is verified by
    /// direct code review and by ClassForge.Plugin compiling clean against the real game types, not here.
    /// </summary>
    public static class EncounterModifierTests
    {
        // The 10 EOR encounter modifiers, authored order and weights verbatim (spec §1.1/§3.2). Sum = 85.
        private static readonly (string Id, int Weight)[] ModifierTable =
        {
            ("ARMORED", 12), ("RESISTANT", 12), ("FRENZIED", 10), ("SWIFT", 10), ("VETERAN", 10),
            ("WEALTHY", 8), ("CURSED", 6), ("REGENERATING", 6), ("GLASSCANNON", 6), ("TREASUREGUARDED", 5)
        };

        /// <summary>Like <see cref="J.Cond"/> but SchemaVersion 1.2 — required for the v1.2-only conditions
        /// this file exercises (<see cref="J.Cond"/> itself hardcodes 1.1 for the pre-existing v1.1 suite).</summary>
        private static string Cond12(string trigger, string condition)
        {
            return "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"DisplayName\":\"T\",\"Trigger\":\"" + trigger +
                   "\",\"Conditions\":[" + condition + "],\"Effects\":[" + J.CounterEffect + "]," +
                   "\"ProcChance\":100,\"AiProcChance\":100}}";
        }

        private static string OneOfWeightedJson()
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < ModifierTable.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"Value\":\"").Append(ModifierTable[i].Id).Append("\",\"Weight\":").Append(ModifierTable[i].Weight).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static void Run(TestRunner t)
        {
            t.Section("encounter modifiers: Scope COMBAT parse + validator rejections (§4.2, §12.1)");

            t.Case("Scope COMBAT parses and is live with a well-formed recipe", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"COMBAT_START_REAL\",\"Value\":true},{\"Type\":\"IS_ENEMY\",\"Of\":\"TRIGGER_TARGET\",\"Value\":true}]," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_X\"}]," +
                    "\"ProcChance\":100}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "well-formed COMBAT recipe");
                var r = set.Find("SKILL_T");
                Check.True(r != null && r.IsLive, "recipe is live");
                Check.True(r.Scope == RecipeScope.COMBAT, "Scope parsed as COMBAT");
            });

            t.Case("Scope COMBAT on SchemaVersion 1.1 is rejected (schema gate)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_COMBAT_SCHEMA_GATE", "1.1 COMBAT recipe rejected");
                Check.Disabled(set, "SKILL_T", "disabled by schema gate");
            });

            t.Case("Scope COMBAT rejects a condition defaulting Of to SELF", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"HAS_STATUS\",\"Value\":\"STATUS_X\"}]," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_COMBAT_SELF_COND", "default-SELF condition rejected");
                Check.Disabled(set, "SKILL_T", "disabled by SELF-condition rejection");
            });

            t.Case("Scope COMBAT rejects a condition explicitly authoring Of: SELF", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"HAS_STATUS\",\"Of\":\"SELF\",\"Value\":\"STATUS_X\"}]," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_COMBAT_SELF_COND", "explicit Of:SELF condition rejected");
            });

            t.Case("Scope COMBAT does not reject a condition with no Of capability (e.g. IS_DUNGEON)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"IS_DUNGEON\",\"Value\":true}]," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "IS_DUNGEON has no Of, never flagged");
            });

            string[] rejectedTargets = { "SELF", "CASTER", "ALLY_ALL", "ALLY_ALL_OTHERS", "ENEMY_ALL" };
            for (int i = 0; i < rejectedTargets.Length; i++)
            {
                string target = rejectedTargets[i];
                t.Case("Scope COMBAT rejects effect Target " + target, () =>
                {
                    string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                        "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"" + target + "\",\"Status\":\"STATUS_X\"}]}}";
                    var set = RecipeParser.Parse(json);
                    Check.HasFinding(set, "E_COMBAT_TARGET", target + " rejected");
                });
            }

            t.Case("Scope COMBAT permits TRIGGER_TARGET / TRIGGER_SOURCE effect targets", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_DAMAGE_TAKEN\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_SOURCE\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "TRIGGER_SOURCE permitted");
            });

            t.Case("Scope COMBAT does not flag Target on targetless effects (SELECTION_SET/EVENT_BANNER/COUNTER_*)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Effects\":[" +
                    "{\"Type\":\"SELECTION_SET\",\"Name\":\"SEL\",\"OneOfWeighted\":[{\"Value\":\"A\",\"Weight\":1}]}," +
                    "{\"Type\":\"EVENT_BANNER\",\"FallbackText\":\"hi\"}," +
                    "{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}" +
                    "]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "targetless effects never flagged E_COMBAT_TARGET");
            });

            t.Case("Scope COMBAT rejects an authored AiProcChance", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\",\"AiProcChance\":50," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_COMBAT_AIPROCCHANCE", "authored AiProcChance rejected");
            });

            t.Case("Scope COMBAT does NOT reject a defaulted (unauthored) AiProcChance", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "defaulted AiProcChance never flagged");
            });

            t.Case("Scope OWNED (default) is unaffected by the COMBAT validator rules", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"STATUS_X\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "default OWNED scope, SELF target legal");
                Check.True(set.Find("SKILL_T").Scope == RecipeScope.OWNED, "Scope defaults OWNED");
            });

            // ================================================================================
            t.Section("encounter modifiers: new v1.2 conditions require SchemaVersion 1.2 (§5)");

            string[] newConditions =
            {
                "PARTY_AVG_LEVEL", "IS_DUNGEON", "BOSS_FIGHT", "ENCOUNTER_PROPERTY",
                "ENTITY_TAG", "CONFIG_NAME_CONTAINS", "COMBAT_START_REAL", "SELECTION_PRESENT", "IS_ENEMY"
            };
            for (int i = 0; i < newConditions.Length; i++)
            {
                string cond = newConditions[i];
                t.Case(cond + " on SchemaVersion 1.1 is rejected (v1.2-only condition)", () =>
                {
                    string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_COMBAT_START\"," +
                        "\"Conditions\":[{\"Type\":\"" + cond + "\",\"Value\":true}]," +
                        "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                    var set = RecipeParser.Parse(json);
                    Check.HasFinding(set, "E_SCHEMA_GATE", cond + " requires 1.2");
                });
            }

            // ================================================================================
            t.Section("cover spec: ALLY_IN_FRONT requires SchemaVersion 1.4 (v1.4-only condition)");

            string[] belowV14 = { "1.0", "1.1", "1.2", "1.3" };
            for (int i = 0; i < belowV14.Length; i++)
            {
                string ver = belowV14[i];
                t.Case("ALLY_IN_FRONT on SchemaVersion " + ver + " is rejected", () =>
                {
                    string json = "{\"SKILL_T\":{\"SchemaVersion\":\"" + ver + "\",\"Trigger\":\"ON_TURN_START\"," +
                        "\"Conditions\":[{\"Type\":\"ALLY_IN_FRONT\",\"Value\":true}]," +
                        "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                    var set = RecipeParser.Parse(json);
                    Check.HasFinding(set, "E_SCHEMA_GATE", "ALLY_IN_FRONT requires 1.4");
                });
            }

            t.Case("ALLY_IN_FRONT on SchemaVersion 1.4 is accepted", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.4\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Conditions\":[{\"Type\":\"ALLY_IN_FRONT\",\"Value\":true}]," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "1.4 clears the gate");
                Check.True(set.Find("SKILL_T").IsLive, "recipe is live");
            });

            t.Case("SchemaVersion 1.4 is a supported engine version (not E_SCHEMA_UNSUPPORTED)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.4\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                Check.NoErrors(RecipeParser.Parse(json), "1.4 accepted");

                // 1.5 landed with the capture spec, so the "one past the top" probe moved up to 1.6.
                string bad = "{\"SKILL_T\":{\"SchemaVersion\":\"1.6\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                Check.HasFinding(RecipeParser.Parse(bad), "E_SCHEMA_UNSUPPORTED", "1.6 still unknown");
            });

            t.Case("ALLY_IN_FRONT requires a boolean Value", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.4\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Conditions\":[{\"Type\":\"ALLY_IN_FRONT\"}]," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                Check.HasFinding(RecipeParser.Parse(json), "E_VALUE_MISSING", "no Value = E_VALUE_MISSING");
            });

            t.Case("the Pokemon-Trainer cover trait validates end-to-end with zero errors", () =>
            {
                // The exact authored shape: 1.4 + the RNG-free ON_DAMAGE_PENDING path + ALLY_IN_FRONT +
                // DAMAGE_TAKEN_MULT. ON_DAMAGE_PENDING is v1.3-gated (1.4 is additive over it), rejects
                // chance-gating (E_DMGPEND_RNG) and restricts its effect set (E_DMGPEND_EFFECT) — this
                // proves this shape clears all three.
                string json =
                    "{\"SKILL_CF_TRAIT_TRAINER_COVER\":{\"SchemaVersion\":\"1.4\"," +
                    "\"DisplayName\":\"Take Cover\",\"Trigger\":\"ON_DAMAGE_PENDING\"," +
                    "\"Conditions\":[{\"Type\":\"ALLY_IN_FRONT\",\"Value\":true}]," +
                    "\"Effects\":[{\"Type\":\"DAMAGE_TAKEN_MULT\",\"Target\":\"SELF\",\"Percent\":-35,\"MinDelta\":2}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "cover trait validates clean");
                var r = set.Find("SKILL_CF_TRAIT_TRAINER_COVER");
                Check.True(r != null && r.IsLive, "recipe is live");
                Check.True(r.Trigger == TriggerKind.ON_DAMAGE_PENDING, "ON_DAMAGE_PENDING legal at 1.4");
            });

            // ================================================================================
            t.Section("encounter modifiers: new condition evaluation (§5)");

            t.Case("PARTY_AVG_LEVEL compares ICombatContext.PartyAverageLevel", () =>
            {
                var rig = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"PARTY_AVG_LEVEL\",\"Comparator\":\"GTE\",\"Value\":5}"), 1);
                rig.Ctx.PartyAverageLevelValue = 4;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "level 4 < 5, no fire");
                rig.Ctx.PartyAverageLevelValue = 5;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "level 5 >= 5, fires");
            });

            t.Case("IS_DUNGEON / BOSS_FIGHT read ICombatContext flags", () =>
            {
                var rig = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"IS_DUNGEON\",\"Value\":true}"), 1);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "not a dungeon");
                rig.Ctx.IsDungeonValue = true;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "dungeon now true");

                var rig2 = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"BOSS_FIGHT\",\"Value\":false}"), 1);
                Check.PlanCount(rig2.Fire(TriggerKind.ON_TURN_START), 1, "not a boss fight, Value:false matches");
                rig2.Ctx.IsBossFightValue = true;
                Check.PlanCount(rig2.Fire(TriggerKind.ON_TURN_START), 0, "boss fight now true, Value:false no longer matches");
            });

            t.Case("ENCOUNTER_PROPERTY reads ICombatContext.HasEncounterProperty, Negate supported", () =>
            {
                var rig = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"ENCOUNTER_PROPERTY\",\"Value\":\"BOSS\",\"Negate\":true}"), 1);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "no BOSS property, Negate:true fires");
                rig.Ctx.EncounterPropertiesSet.Add("BOSS");
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "BOSS property present, Negate:true no longer fires");
            });

            t.Case("ENTITY_TAG reads ICombatEntity.HasTag", () =>
            {
                var rig = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"ENTITY_TAG\",\"Value\":\"BOSS\"}"), 1);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "hero untagged");
                rig.Hero.TagList.Add("BOSS");
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "hero tagged BOSS");
            });

            t.Case("CONFIG_NAME_CONTAINS is ordinal-ignore-case contains", () =>
            {
                var rig = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"CONFIG_NAME_CONTAINS\",\"Value\":\"scourge\"}"), 1);
                rig.Hero.ConfigNameValue = "SPAWN_SCOURGE_TOTEM";
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "case-insensitive substring match");
                rig.Hero.ConfigNameValue = "KNIGHT";
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "no match");
            });

            t.Case("COMBAT_START_REAL reads CombatStartEvent.TrySkillProc, ON_COMBAT_START only", () =>
            {
                var rig = Rig.Build(Cond12("ON_COMBAT_START", "{\"Type\":\"COMBAT_START_REAL\",\"Value\":true}"), 1);
                var real = rig.Dispatcher.OnCombatStart(rig.Ctx, new CombatStartEvent { Entity = rig.Hero, TrySkillProc = true });
                Check.PlanCount(real, 1, "TrySkillProc true fires");
                var fake = rig.Dispatcher.OnCombatStart(rig.Ctx, new CombatStartEvent { Entity = rig.Hero, TrySkillProc = false });
                Check.PlanCount(fake, 0, "TrySkillProc false (re-init) does not fire");
            });

            t.Case("IS_ENEMY (GATE D) reads ICombatEntity.IsEnemy, not CHARACTER_TYPE", () =>
            {
                var rig = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"IS_ENEMY\",\"Value\":true}"), 1);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "Hero (team 0) is not an enemy");
                rig.Hero.EnemyFlag = true;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "flagged enemy fires");
            });

            t.Case("SELECTION_PRESENT reads CombatRuntime.Selections non-empty", () =>
            {
                var rig = Rig.Build(Cond12("ON_TURN_START", "{\"Type\":\"SELECTION_PRESENT\",\"Name\":\"SEL\",\"Value\":true}"), 1);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "no selection stored yet");
                rig.Dispatcher.State.Sync(rig.Ctx).SetSelection("SEL", "ARMORED");
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "selection now present");
            });

            // ================================================================================
            t.Section("encounter modifiers: ProcChanceFormula (§5, §12.1)");

            const string formulaJson =
                "\"ProcChanceFormula\":{" +
                "\"Base\":[" +
                "{\"Conditions\":[{\"Type\":\"PARTY_AVG_LEVEL\",\"Comparator\":\"LTE\",\"Value\":2}],\"Value\":10}," +
                "{\"Conditions\":[{\"Type\":\"PARTY_AVG_LEVEL\",\"Comparator\":\"LTE\",\"Value\":5}],\"Value\":20}," +
                "{\"Value\":30}]," +
                "\"Adjustments\":[" +
                "{\"Conditions\":[{\"Type\":\"IS_DUNGEON\",\"Value\":true}],\"Value\":5}," +
                "{\"Conditions\":[{\"Type\":\"ENCOUNTER_PROPERTY\",\"Value\":\"AMBUSH\"}],\"Value\":5}," +
                "{\"Conditions\":[{\"Type\":\"ENCOUNTER_PROPERTY\",\"Value\":\"QUEST_TARGET\"}],\"Value\":-5}]," +
                "\"Min\":0,\"Max\":35}";

            t.Case("ProcChanceFormula mutually exclusive with an authored ProcChance", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\",\"ProcChance\":50," +
                    formulaJson + ",\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_PCF_MUTEX", "ProcChance + ProcChanceFormula mutually exclusive");
            });

            t.Case("ProcChanceFormula band edges 2/3 and 5/6", () =>
            {
                var actions = new (int Level, int ExpectedChance)[]
                {
                    (2, 10), (3, 20), (5, 20), (6, 30)
                };
                foreach (var a in actions)
                {
                    string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\"," +
                        formulaJson + ",\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                    var rig = Rig.Build(json, 1);
                    rig.Ctx.PartyAverageLevelValue = a.Level;
                    // Force the gate to succeed and assert both the exact computed chance (via LastChance)
                    // and the draw count (one NextChance draw; no SELECTION_SET on this recipe, so no hoisting).
                    rig.Rng.ScriptChance(true);
                    var plan = rig.Fire(TriggerKind.ON_TURN_START);
                    Check.PlanCount(plan, 1, "level " + a.Level + " fires when the gate succeeds");
                    Check.Eq(1, rig.Rng.Draws, "level " + a.Level + ": exactly one gate draw (no SELECTION_SET)");
                    Check.True(rig.Rng.LastChance.HasValue && rig.Rng.LastChance.Value == a.ExpectedChance / 100m,
                        "level " + a.Level + ": computed chance is " + a.ExpectedChance + "%, saw " + rig.Rng.LastChance);
                }
            });

            t.Case("ProcChanceFormula adjustment stacking", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\"," +
                    formulaJson + ",\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                var rig = Rig.Build(json, 1);
                rig.Ctx.PartyAverageLevelValue = 1;           // base 10
                rig.Ctx.IsDungeonValue = true;                 // +5
                rig.Ctx.EncounterPropertiesSet.Add("AMBUSH");  // +5  -> 20 total
                rig.Rng.ScriptChance(true);
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan, 1, "stacked adjustments still produce a positive chance that can fire");
                Check.True(rig.Rng.LastChance.HasValue && rig.Rng.LastChance.Value == 0.20m,
                    "10 (base) + 5 (dungeon) + 5 (ambush) = 20%, saw " + rig.Rng.LastChance);

                // Adding QUEST_TARGET (-5) brings it back down to 15%.
                var rig2 = Rig.Build(json, 1);
                rig2.Ctx.PartyAverageLevelValue = 1;
                rig2.Ctx.IsDungeonValue = true;
                rig2.Ctx.EncounterPropertiesSet.Add("AMBUSH");
                rig2.Ctx.EncounterPropertiesSet.Add("QUEST_TARGET");
                rig2.Rng.ScriptChance(true);
                rig2.Fire(TriggerKind.ON_TURN_START);
                Check.True(rig2.Rng.LastChance.HasValue && rig2.Rng.LastChance.Value == 0.15m,
                    "10 + 5 + 5 - 5 = 15%, saw " + rig2.Rng.LastChance);
            });

            t.Case("ProcChanceFormula clamps to Min/Max", () =>
            {
                string clampJson =
                    "\"ProcChanceFormula\":{\"Base\":[{\"Value\":30}]," +
                    "\"Adjustments\":[{\"Value\":50},{\"Value\":-200}],\"Min\":0,\"Max\":35}";
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\"," +
                    clampJson + ",\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                // 30 + 50 - 200 = -120, clamps to Min 0 -> zero draws, symmetric skip.
                var rig = Rig.Build(json, 1);
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan, 0, "clamped to 0 never fires");
                Check.Eq(0, rig.Rng.Draws, "clamped-to-0 formula takes ZERO draws (symmetric skip)");
            });

            t.Case("ProcChanceFormula clamps a too-high total to Max", () =>
            {
                string clampJson = "\"ProcChanceFormula\":{\"Base\":[{\"Value\":30}],\"Adjustments\":[{\"Value\":50}],\"Max\":35}";
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\"," +
                    clampJson + ",\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]}}";
                // 30 + 50 = 80, clamps to Max 35 -- still positive, still takes exactly one gate draw.
                var rig = Rig.Build(json, 1);
                rig.Rng.ScriptChance(true);
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan, 1, "clamped-to-35 (not 0) still takes the gate draw and can fire");
                Check.Eq(1, rig.Rng.Draws, "exactly one draw");
            });

            // ================================================================================
            t.Section("encounter modifiers: SELECTION_SET weighted walk — exhaustive 1..85 (§5, §6.3, §12.1)");

            t.Case("SELECTION_SET.OneOfWeighted walk matches EOR's algorithm for all 85 outcomes", () =>
            {
                // Cumulative boundaries transcribed from the authored weights 12,12,10,10,10,8,6,6,6,5.
                var boundaries = new List<(int Hi, string Id)>();
                int running = 0;
                for (int i = 0; i < ModifierTable.Length; i++)
                {
                    running += ModifierTable[i].Weight;
                    boundaries.Add((running, ModifierTable[i].Id));
                }
                Check.Eq(85, running, "table sums to 85");

                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"SELECTION_SET\",\"Name\":\"SEL\",\"OneOfWeighted\":" + OneOfWeightedJson() + "}]," +
                    "\"ProcChance\":100,\"AiProcChance\":100}}";

                for (int roll = 1; roll <= 85; roll++)
                {
                    string expected = null;
                    for (int b = 0; b < boundaries.Count; b++)
                        if (roll <= boundaries[b].Hi) { expected = boundaries[b].Id; break; }

                    var rig = Rig.Build(json, roll);
                    rig.Rng.ScriptInt(roll);
                    var plan = rig.Fire(TriggerKind.ON_TURN_START);
                    Check.PlanCount(plan, 1, "roll " + roll + " emits exactly one SelectionSetAction");
                    var action = plan[0] as SelectionSetAction;
                    Check.True(action != null, "roll " + roll + " action is a SelectionSetAction");
                    Check.Eq(expected, action.Value, "roll " + roll + " walks to the EOR-equivalent modifier");
                    Check.Eq(expected, rig.Dispatcher.State.Sync(rig.Ctx).GetSelection("SEL"), "roll " + roll + " stored in Selections");
                    Check.Eq(1, rig.Rng.Draws, "roll " + roll + " takes exactly one draw");
                }
            });

            // ================================================================================
            t.Section("encounter modifiers: StatusFromSelection + budget non-consumption (§5, §12.1)");

            t.Case("ADD_STATUS StatusFromSelection resolves the stored selection", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"StatusFromSelection\":\"SEL\"}]}}";
                var rig = Rig.Build(json, 1);
                rig.Dispatcher.State.Sync(rig.Ctx).SetSelection("SEL", "STATUS_CF_ENCMOD_ARMORED");
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan, 1, "resolves the stored selection");
                var add = plan[0] as AddStatusAction;
                Check.True(add != null, "is an AddStatusAction");
                Check.Eq("STATUS_CF_ENCMOD_ARMORED", add.StatusId, "status id resolved from Selections");
            });

            t.Case("ADD_STATUS StatusFromSelection with an empty selection is a no-op, no budget burn", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"StatusFromSelection\":\"SEL\"}]," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\",\"ConsumeOn\":\"EFFECT_APPLIED\"}}}";
                var rig = Rig.Build(json, 1);

                // No selection stored -> no-op, budget must stay open.
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "empty selection -> no-op");
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "still a no-op, budget never consumed");

                // Now store a selection -- budget is still open, so it fires and THEN consumes.
                rig.Dispatcher.State.Sync(rig.Ctx).SetSelection("SEL", "STATUS_X");
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan, 1, "selection now present, fires");

                // Budget is consumed only once the effect actually applied.
                var plan2 = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan2, 0, "ONCE_PER_COMBAT budget now consumed, no re-fire");
            });

            // ================================================================================
            t.Section("encounter modifiers: draw-count constancy (§6.3, §12.2)");

            t.Case("eligible-hit combat takes exactly 2 draws", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"COMBAT_START_REAL\",\"Value\":true},{\"Type\":\"IS_ENEMY\",\"Of\":\"TRIGGER_TARGET\",\"Value\":true}]," +
                    "\"ProcChanceFormula\":{\"Base\":[{\"Value\":30}],\"Min\":0,\"Max\":35}," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\",\"ConsumeOn\":\"EVALUATION\"}," +
                    "\"Effects\":[{\"Type\":\"SELECTION_SET\",\"Name\":\"CF_ENCMOD\",\"OneOfWeighted\":" + OneOfWeightedJson() + "}]}}";
                var rig = Rig.Build(json, 1);
                rig.Foe.EnemyFlag = true;
                rig.Rng.ScriptChance(true);
                var plan = rig.Dispatcher.OnCombatStart(rig.Ctx, new CombatStartEvent { Entity = rig.Foe, TrySkillProc = true });
                Check.Eq(2, rig.Rng.Draws, "eligible-hit: gate draw + pick draw = 2");
                Check.True(FindSelectionSet(plan) != null, "SELECTION_SET fired and stored a value");
            });

            t.Case("eligible-miss combat still takes exactly 2 draws (§6.3 constant-2 hoisting)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"COMBAT_START_REAL\",\"Value\":true},{\"Type\":\"IS_ENEMY\",\"Of\":\"TRIGGER_TARGET\",\"Value\":true}]," +
                    "\"ProcChanceFormula\":{\"Base\":[{\"Value\":30}],\"Min\":0,\"Max\":35}," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\",\"ConsumeOn\":\"EVALUATION\"}," +
                    "\"Effects\":[{\"Type\":\"SELECTION_SET\",\"Name\":\"CF_ENCMOD\",\"OneOfWeighted\":" + OneOfWeightedJson() + "}]}}";
                var rig = Rig.Build(json, 1);
                rig.Foe.EnemyFlag = true;
                rig.Rng.ScriptChance(false);
                var plan = rig.Dispatcher.OnCombatStart(rig.Ctx, new CombatStartEvent { Entity = rig.Foe, TrySkillProc = true });
                Check.Eq(2, rig.Rng.Draws, "eligible-miss: gate draw + discarded pick draw = 2");
                Check.True(FindSelectionSet(plan) == null, "gate failed -> no SELECTION_SET action emitted");
                Check.True(string.IsNullOrEmpty(rig.Dispatcher.State.Sync(rig.Ctx).GetSelection("CF_ENCMOD")), "no selection stored on gate failure");
            });

            t.Case("excluded combat (condition false) takes exactly 0 draws", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"COMBAT_START_REAL\",\"Value\":true},{\"Type\":\"IS_ENEMY\",\"Of\":\"TRIGGER_TARGET\",\"Value\":true}]," +
                    "\"ProcChanceFormula\":{\"Base\":[{\"Value\":30}],\"Min\":0,\"Max\":35}," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\",\"ConsumeOn\":\"EVALUATION\"}," +
                    "\"Effects\":[{\"Type\":\"SELECTION_SET\",\"Name\":\"CF_ENCMOD\",\"OneOfWeighted\":" + OneOfWeightedJson() + "}]}}";
                var rig = Rig.Build(json, 1);
                // Foe stays non-enemy (EnemyFlag default false for team 1 unless we set it -- force it false).
                rig.Foe.EnemyFlag = false;
                var plan = rig.Dispatcher.OnCombatStart(rig.Ctx, new CombatStartEvent { Entity = rig.Foe, TrySkillProc = true });
                Check.PlanCount(plan, 0, "excluded: no plan");
                Check.Eq(0, rig.Rng.Draws, "excluded: zero draws");
            });

            t.Case("SafeMode-equivalent (null CombatState.Random) takes exactly 0 draws", () =>
            {
                // §5.2 invariant 2 / spec §10.5: SafeMode is whole-engine-off. The concrete mechanism is the
                // dispatcher's null-random guard -- no ad-hoc GameRandom is ever substituted.
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"ProcChanceFormula\":{\"Base\":[{\"Value\":30}],\"Min\":0,\"Max\":35}," +
                    "\"Effects\":[{\"Type\":\"SELECTION_SET\",\"Name\":\"CF_ENCMOD\",\"OneOfWeighted\":" + OneOfWeightedJson() + "}]}}";
                var set = RecipeParser.Parse(json);
                var ctx = Scenario.Standard();
                var foe = new FakeEntity("C_FOE", 1); foe.EnemyFlag = true;
                ctx.AddEntity(foe);
                var log = new FakeLog();
                var dispatcher = new RecipeDispatcher(set, null, log);   // null random == SafeMode-equivalent
                var plan = dispatcher.OnCombatStart(ctx, new CombatStartEvent { Entity = foe, TrySkillProc = true });
                Check.PlanCount(plan, 0, "null-random path fires nothing, zero draws by construction");
            });

            // ================================================================================
            t.Section("encounter modifiers: §4.5 derived-state reconstruction (§12.3)");

            t.Case("reconstruction sets the selection latch and consumes per-enemy apply budgets from replicated statuses", () =>
            {
                var ctx = Scenario.Standard();
                var foeA = new FakeEntity("A_FOE", 1).WithStatus("STATUS_CF_ENCMOD_ARMORED");
                var foeB = new FakeEntity("B_FOE", 1).WithStatus("STATUS_CF_ENCMOD_ARMORED");
                var foeC = new FakeEntity("C_FOE_LATE", 1); // late-wave enemy, not yet statused
                ctx.AddEntity(foeA).AddEntity(foeB).AddEntity(foeC);

                var runtime = new CombatRuntime("K1");
                var registry = new List<ModifierReconstruction.ModifierRegistryEntry>
                {
                    new ModifierReconstruction.ModifierRegistryEntry { StatusId = "STATUS_CF_ENCMOD_ARMORED", ModifierId = "ARMORED" },
                    new ModifierReconstruction.ModifierRegistryEntry { StatusId = "STATUS_CF_ENCMOD_SWIFT", ModifierId = "SWIFT" }
                };

                ModifierReconstruction.Reconstruct(ctx, runtime, registry, "CF_ENCMOD",
                    BudgetScope.ONCE_PER_COMBAT, "SELECT_KEY",
                    BudgetScope.ONCE_PER_TARGET_PER_COMBAT, "APPLY_KEY");

                Check.Eq("ARMORED", runtime.GetSelection("CF_ENCMOD"), "selection latch set from replicated statuses, no draws");
                Check.False(runtime.IsBudgetAvailable(BudgetScope.ONCE_PER_COMBAT, "SELECT_KEY", "", ""), "selection budget consumed (sentinel guid)");
                Check.False(runtime.IsBudgetAvailable(BudgetScope.ONCE_PER_TARGET_PER_COMBAT, "APPLY_KEY", "", "A_FOE"), "foeA apply budget consumed");
                Check.False(runtime.IsBudgetAvailable(BudgetScope.ONCE_PER_TARGET_PER_COMBAT, "APPLY_KEY", "", "B_FOE"), "foeB apply budget consumed");
                Check.True(runtime.IsBudgetAvailable(BudgetScope.ONCE_PER_TARGET_PER_COMBAT, "APPLY_KEY", "", "C_FOE_LATE"), "late-wave foeC still open -- applies when its own event fires");
            });

            t.Case("reconstruction is a no-op when no entity carries a registry status", () =>
            {
                var ctx = Scenario.Standard();
                ctx.AddEntity(new FakeEntity("A_FOE", 1));
                var runtime = new CombatRuntime("K1");
                var registry = new List<ModifierReconstruction.ModifierRegistryEntry>
                {
                    new ModifierReconstruction.ModifierRegistryEntry { StatusId = "STATUS_CF_ENCMOD_ARMORED", ModifierId = "ARMORED" }
                };
                ModifierReconstruction.Reconstruct(ctx, runtime, registry, "CF_ENCMOD",
                    BudgetScope.ONCE_PER_COMBAT, "SELECT_KEY", BudgetScope.ONCE_PER_TARGET_PER_COMBAT, "APPLY_KEY");
                Check.True(string.IsNullOrEmpty(runtime.GetSelection("CF_ENCMOD")), "no selection set");
                Check.True(runtime.IsBudgetAvailable(BudgetScope.ONCE_PER_COMBAT, "SELECT_KEY", "", ""), "selection budget still open");
            });

            t.Case("reconstruction picks the first hit in ROSTER ORDER when multiple modifiers are present", () =>
            {
                // Deliberately malformed/synthetic mid-combat state (should not occur in practice -- two
                // different modifier statuses on different enemies) to prove the scan order is deterministic.
                var ctx = Scenario.Standard();
                var foeZ = new FakeEntity("Z_FOE", 1).WithStatus("STATUS_CF_ENCMOD_SWIFT");
                var foeA = new FakeEntity("A_FOE", 1).WithStatus("STATUS_CF_ENCMOD_ARMORED");
                // Roster order is [.. , foeZ, foeA] while ordinal-GUID order would be [foeA, foeZ] -- the
                // two disagree on purpose, so this test pins WHICH one the scan follows. It follows roster
                // order, because that is the ordering every peer computes identically; Entity.Guid is
                // minted locally by Guid.NewGuid() and used to give each peer a different scan order.
                ctx.AddEntity(foeZ).AddEntity(foeA);
                var runtime = new CombatRuntime("K1");
                var registry = new List<ModifierReconstruction.ModifierRegistryEntry>
                {
                    new ModifierReconstruction.ModifierRegistryEntry { StatusId = "STATUS_CF_ENCMOD_ARMORED", ModifierId = "ARMORED" },
                    new ModifierReconstruction.ModifierRegistryEntry { StatusId = "STATUS_CF_ENCMOD_SWIFT", ModifierId = "SWIFT" }
                };
                ModifierReconstruction.Reconstruct(ctx, runtime, registry, "CF_ENCMOD",
                    BudgetScope.ONCE_PER_COMBAT, "SELECT_KEY", BudgetScope.ONCE_PER_TARGET_PER_COMBAT, "APPLY_KEY");
                Check.Eq("SWIFT", runtime.GetSelection("CF_ENCMOD"),
                    "foeZ (roster-first) wins over foeA; ARMORED here would mean guid order leaked back in");
            });

            t.Case("CombatKey change drops the runtime -- reconstruction re-runs on the fresh one", () =>
            {
                var store = new RecipeStateStore();
                var ctx1 = Scenario.Standard();
                ctx1.Identity = "COMBAT_A";
                var r1 = store.Sync(ctx1);
                r1.SetSelection("CF_ENCMOD", "ARMORED");
                Check.Eq("ARMORED", store.Sync(ctx1).GetSelection("CF_ENCMOD"), "same key, same runtime, selection persists");

                var ctx2 = Scenario.Standard();
                ctx2.Identity = "COMBAT_B";
                var r2 = store.Sync(ctx2);
                Check.True(!ReferenceEquals(r1, r2), "different CombatKey allocates a fresh runtime");
                Check.True(string.IsNullOrEmpty(r2.GetSelection("CF_ENCMOD")), "fresh runtime has no stale selection");
            });

            // ================================================================================
            t.Section("encounter modifiers: FlatValueFrom TARGET_MXHP_PCT golden tests (GATE C, §12.4)");

            t.Case("TARGET_MXHP_PCT: MXHP 100, Percent +10 -> +10", () =>
            {
                Check.Eq(10, ResolveMxhpPct(100, 10), "positive percent, exact division");
            });

            t.Case("TARGET_MXHP_PCT: MXHP 100, Percent -20 -> -20 (sign preserved)", () =>
            {
                Check.Eq(-20, ResolveMxhpPct(100, -20), "negative percent preserved");
            });

            t.Case("TARGET_MXHP_PCT: MXHP 15, Percent -10 -> -2 (round-away-from-zero on .5)", () =>
            {
                // 15 * 10 / 100 = 1.5 -> round-away-from-zero -> 2
                Check.Eq(-2, ResolveMxhpPct(15, -10), "half-value rounds away from zero");
            });

            t.Case("TARGET_MXHP_PCT: floor-at-1 -- MXHP 1, Percent -10 -> -1 (never rounds to 0)", () =>
            {
                // 1 * 10 / 100 = 0.1 -> round = 0 -> max(1, 0) = 1, sign negative
                Check.Eq(-1, ResolveMxhpPct(1, -10), "EOR's max(1, round(...)) floor");
            });

            t.Case("TARGET_MXHP_PCT: Percent 0 is a no-op", () =>
            {
                Check.Eq(0, ResolveMxhpPct(100, 0), "zero percent resolves to 0");
            });

            t.Case("TARGET_MXHP_PCT: no resolvable target MXHP (<=0) resolves to 0", () =>
            {
                Check.Eq(0, ResolveMxhpPct(0, 10), "zero MXHP resolves to 0, no divide-by-zero");
            });

            t.Case("TARGET_MXHP_PCT: STAT_CHANGE on Stat MXHP + FlatPercent is a hard validator error (GATE C)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"MXHP\",\"StatChangeType\":\"MAGICAL\",\"FlatPercent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_MXHP_FLATPERCENT_UNSUPPORTED", "MXHP + FlatPercent rejected");
            });

            t.Case("TARGET_MXHP_PCT end-to-end through a STAT_CHANGE effect on TRIGGER_TARGET", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGE_DEALT\"," +
                    "\"Effects\":[{\"Type\":\"STAT_CHANGE\",\"Target\":\"TRIGGER_TARGET\",\"Stat\":\"MXHP\",\"StatChangeType\":\"MAGICAL\"," +
                    "\"FlatValueFrom\":\"TARGET_MXHP_PCT\",\"Percent\":15}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "FlatValueFrom TARGET_MXHP_PCT + Percent validates cleanly");
                var rig = Rig.FromSet(set, 1);
                rig.Foe.With("MXHP", 100);
                var plan = rig.Fire(TriggerKind.ON_DAMAGE_DEALT);
                Check.PlanCount(plan, 1, "one STAT_CHANGE emitted");
                var change = plan[0] as StatChangeAction;
                Check.True(change != null, "is a StatChangeAction");
                Check.Eq("MXHP", change.Stat, "targets MXHP");
                Check.Eq(15, change.FlatValue.HasValue ? change.FlatValue.Value : -9999, "flat value resolved from target MXHP * 15%");
                Check.True(!change.FlatPercent.HasValue, "emitted as a plain FlatValue, never FlatPercent (GATE C)");
            });
        }

        private static int ResolveMxhpPct(int targetMaxHp, int percent)
        {
            var target = new FakeEntity("T", 1).With("MXHP", targetMaxHp);
            var t = new TriggerContext { TriggerTarget = target };
            var effect = new RecipeEffect { Percent = percent };
            return ValueSources.Resolve(Vocabulary.SourceTargetMxhpPct, effect, t);
        }

        private static SelectionSetAction FindSelectionSet(IReadOnlyList<EngineAction> plan)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var a = plan[i] as SelectionSetAction;
                if (a != null) return a;
            }
            return null;
        }
    }
}
