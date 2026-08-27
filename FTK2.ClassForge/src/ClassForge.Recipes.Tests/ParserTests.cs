using System;
using System.Collections.Generic;
using System.IO;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;

namespace ClassForge.Recipes.Tests
{
    public static class ParserTests
    {
        public static void Run(TestRunner t)
        {
            t.Section("parser / validator");

            t.Case("parser: well-formed v1.1 recipe round-trips every field", () =>
            {
                var set = RecipeParser.Parse(
                    "{\"SKILL_X\":{\"SchemaVersion\":\"1.1\",\"DisplayName\":\"X\",\"Enabled\":true," +
                    "\"Trigger\":\"ON_ABILITY_USED\",\"Conditions\":[{\"Type\":\"ABILITY_TAG\",\"Value\":\"T\"}]," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S\",\"Duration\":2}]," +
                    "\"ProcChance\":20,\"AiProcChance\":30," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_ROUND\",\"ConsumeOn\":\"EVALUATION\",\"Key\":\"K\"}," +
                    "\"Cooldown\":2,\"Priority\":5,\"VerboseLogTag\":\"tag\"}}");
                Check.NoErrors(set, "clean parse");
                var r = set.Find("SKILL_X");
                Check.True(r != null, "recipe present");
                Check.Eq("1.1", r.SchemaVersion, "SchemaVersion");
                Check.True(r.Enabled && r.IsLive, "enabled");
                Check.Eq((int)TriggerKind.ON_ABILITY_USED, (int)r.Trigger, "Trigger");
                Check.Eq(1, r.Conditions.Count, "condition count");
                Check.Eq(1, r.Effects.Count, "effect count");
                Check.Eq(2, r.Effects[0].Duration.Value, "Duration");
                Check.Eq(20, r.ProcChance, "ProcChance");
                Check.Eq(30, r.AiProcChance, "AiProcChance");
                Check.Eq((int)BudgetScope.ONCE_PER_ROUND, (int)r.Budget.Scope, "Budget.Scope");
                Check.Eq((int)ConsumeOn.EVALUATION, (int)r.Budget.ConsumeOn, "Budget.ConsumeOn");
                Check.Eq("K", r.BudgetKey, "Budget.Key");
                Check.Eq(2, r.Cooldown, "Cooldown");
                Check.Eq(5, r.Priority, "Priority");
                Check.Eq("tag", r.VerboseLogTag, "VerboseLogTag");
            });

            t.Case("parser: JSONC comments and trailing commas are accepted", () =>
            {
                var set = RecipeParser.Parse(
                    "// leading comment\n{\n  /* block */\n  \"SKILL_C\": {\n" +
                    "    \"SchemaVersion\": \"1.1\", \"Trigger\": \"ON_KILL\",\n" +
                    "    \"Effects\": [ { \"Type\": \"ADD_STATUS\", \"Target\": \"SELF\", \"Status\": \"S\" }, ],\n" +
                    "  },\n}\n");
                Check.NoErrors(set, "jsonc");
                Check.True(set.Find("SKILL_C") != null, "recipe parsed");
            });

            t.Case("malformed: broken JSON yields E_JSON and never throws", () =>
            {
                var set = RecipeParser.Parse("{\"SKILL_A\": {\"Trigger\": ");
                Check.HasFinding(set, "E_JSON", "broken json");
                Check.Eq(0, set.Ordered.Count, "no recipes loaded");
            });

            t.Case("malformed: non-object root yields E_ROOT", () =>
            {
                Check.HasFinding(RecipeParser.Parse("[1,2,3]"), "E_ROOT", "array root");
            });

            t.Case("malformed: non-object recipe body yields E_SHAPE", () =>
            {
                Check.HasFinding(RecipeParser.Parse("{\"SKILL_A\": 7}"), "E_SHAPE", "scalar body");
            });

            t.Case("malformed: unknown trigger token disables the recipe", () =>
            {
                var set = RecipeParser.Parse(J.Trigger("ON_SNEEZE"));
                Check.HasFinding(set, "E_TRIGGER_UNKNOWN", "unknown trigger");
                Check.Disabled(set, "SKILL_T", "unknown trigger disables");
            });

            t.Case("malformed: missing trigger disables the recipe", () =>
            {
                var set = RecipeParser.Parse("{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Effects\":[" + J.CounterEffect + "]}}");
                Check.HasFinding(set, "E_TRIGGER_MISSING", "missing trigger");
                Check.Disabled(set, "SKILL_T", "disabled");
            });

            t.Case("malformed: unknown condition token disables the recipe", () =>
            {
                var set = RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"MOON_PHASE\",\"Value\":\"FULL\"}"));
                Check.HasFinding(set, "E_COND_UNKNOWN", "unknown condition");
                Check.Disabled(set, "SKILL_T", "disabled");
            });

            t.Case("malformed: unknown effect token disables the recipe", () =>
            {
                var set = RecipeParser.Parse(J.Effect("ON_KILL", "{\"Type\":\"GRANT_GOLD\",\"Target\":\"SELF\"}"));
                Check.HasFinding(set, "E_EFFECT_UNKNOWN", "unknown effect");
                Check.Disabled(set, "SKILL_T", "disabled");
            });

            t.Case("malformed: unknown target token disables the recipe", () =>
            {
                var set = RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"EVERYONE\",\"Status\":\"S\"}"));
                Check.HasFinding(set, "E_TARGET_UNKNOWN", "unknown target");
                Check.Disabled(set, "SKILL_T", "disabled");
            });

            t.Case("malformed: unknown Budget.Scope / ConsumeOn / Comparator / Of tokens", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.One("ON_KILL", "", J.CounterEffect,
                    ",\"Budget\":{\"Scope\":\"ONCE_PER_ETERNITY\"}")), "E_BUDGET_SCOPE", "scope");
                Check.HasFinding(RecipeParser.Parse(J.One("ON_KILL", "", J.CounterEffect,
                    ",\"Budget\":{\"Scope\":\"NONE\",\"ConsumeOn\":\"WHENEVER\"}")), "E_BUDGET_CONSUME", "consumeOn");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL",
                    "{\"Type\":\"COUNTER\",\"Name\":\"N\",\"Comparator\":\"ISH\",\"Value\":1}")), "E_CMP_UNKNOWN", "comparator");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL",
                    "{\"Type\":\"HAS_STATUS\",\"Of\":\"THE_MOON\",\"Value\":\"S\"}")), "E_OF_UNKNOWN", "of");
            });

            t.Case("malformed: wrong node kinds are rejected, not coerced", () =>
            {
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Effects\":\"nope\"}}"),
                    "E_SHAPE", "Effects string");
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Conditions\":{},\"Effects\":[" + J.CounterEffect + "]}}"),
                    "E_SHAPE", "Conditions object");
                Check.HasFinding(RecipeParser.Parse(J.One("ON_KILL", "", J.CounterEffect, ",\"Enabled\":\"yes\"")),
                    "E_SHAPE", "Enabled string");
                Check.HasFinding(RecipeParser.Parse(J.One("ON_KILL", "", J.CounterEffect, ",\"Cooldown\":\"two\"")),
                    "E_SHAPE", "Cooldown string");
            });

            t.Case("malformed: empty or missing Effects is an error", () =>
            {
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Effects\":[]}}"),
                    "E_NO_EFFECTS", "empty");
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"}}"),
                    "E_NO_EFFECTS", "missing");
            });

            t.Case("malformed: out-of-range ProcChance / negative Cooldown", () =>
            {
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"ProcChance\":140,\"Effects\":[" + J.CounterEffect + "]}}"),
                    "E_RANGE", "ProcChance 140");
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Cooldown\":-1,\"Effects\":[" + J.CounterEffect + "]}}"),
                    "E_RANGE", "negative cooldown");
            });

            t.Case("SchemaVersion gate: unsupported version disables the recipe", () =>
            {
                var set = RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"2.7\",\"Trigger\":\"ON_KILL\",\"Effects\":[" + J.CounterEffect + "]}}");
                Check.HasFinding(set, "E_SCHEMA_UNSUPPORTED", "2.7");
                Check.Disabled(set, "SKILL_T", "disabled");
            });

            t.Case("SchemaVersion gate: absent version defaults to 1.0 with a warning", () =>
            {
                var set = RecipeParser.Parse(
                    "{\"SKILL_T\":{\"Trigger\":\"ON_KILL\",\"Effects\":[" + J.SelfStatusEffect + "]}}");
                Check.HasFinding(set, "W_SCHEMA_DEFAULT", "default warning");
                Check.NoErrors(set, "still loads");
                Check.Eq("1.0", set.Find("SKILL_T").SchemaVersion, "assumed version");
            });

            t.Case("SchemaVersion gate: v1.1 tokens are refused under 1.0", () =>
            {
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.0\",\"Trigger\":\"ON_COMBAT_START\",\"Effects\":[" + J.SelfStatusEffect + "]}}"),
                    "E_SCHEMA_GATE", "v1.1 trigger under 1.0");
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.0\",\"Trigger\":\"ON_KILL\"," +
                    "\"Conditions\":[{\"Type\":\"COUNTER\",\"Name\":\"N\",\"Comparator\":\"GTE\",\"Value\":1}]," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}"),
                    "E_SCHEMA_GATE", "v1.1 condition under 1.0");
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.0\",\"Trigger\":\"ON_KILL\",\"Effects\":[" + J.CounterEffect + "]}}"),
                    "E_SCHEMA_GATE", "v1.1 effect under 1.0");
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.0\",\"Trigger\":\"ON_KILL\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"ENEMY_ALL\",\"Status\":\"S\"}]}}"),
                    "E_SCHEMA_GATE", "v1.1 target under 1.0");
            });

            t.Case("deprecated alias: ON_DAMAGED warns and maps to ON_DAMAGE_TAKEN", () =>
            {
                var set = RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGED\",\"Effects\":[" + J.SelfStatusEffect + "]}}");
                Check.HasFinding(set, "W_TRIGGER_DEPRECATED", "rename warning");
                Check.NoErrors(set, "still live");
                Check.Eq((int)TriggerKind.ON_DAMAGE_TAKEN, (int)set.Find("SKILL_T").Trigger, "remapped");
            });

            t.Case("deprecated alias: TARGET_BASE_TYPE_NOT warns and becomes {TARGET_BASE_TYPE, Negate}", () =>
            {
                var set = RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"TARGET_BASE_TYPE_NOT\",\"Value\":\"SKELETON\"}"));
                Check.HasFinding(set, "W_COND_DEPRECATED", "alias warning");
                var c = set.Find("SKILL_T").Conditions[0];
                Check.Eq((int)ConditionKind.TARGET_BASE_TYPE, (int)c.Type, "rewritten type");
                Check.True(c.Negate, "negate set");
            });

            t.Case("per-primitive scope: ROLL_STAT_BONUS is ON_ABILITY_DECLARED only", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"ROLL_STAT_BONUS\",\"Stat\":\"ATK\",\"Flat\":5}")),
                    "E_EFFECT_TRIGGER_SCOPE", "wrong trigger");
                Check.NoErrors(RecipeParser.Parse(J.Effect("ON_ABILITY_DECLARED",
                    "{\"Type\":\"ROLL_STAT_BONUS\",\"Stat\":\"ATK\",\"Flat\":5}")), "right trigger");
            });

            t.Case("per-primitive scope: HEAL_MODIFIER is ON_HEAL_PENDING only and RNG-free", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_HEAL",
                    "{\"Type\":\"HEAL_MODIFIER\",\"Percent\":25}")),
                    "E_EFFECT_TRIGGER_SCOPE", "wrong trigger");
                Check.HasFinding(RecipeParser.Parse(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_HEAL_PENDING\",\"ProcChance\":50," +
                    "\"Effects\":[{\"Type\":\"HEAL_MODIFIER\",\"Percent\":25}]}}"),
                    "E_HEAL_RNG", "chance-gated heal modifier is rejected");
            });

            t.Case("per-primitive scope: STATUS_TYPE and TRIGGER_STATUS are ON_STATUS_APPLIED only", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"STATUS_TYPE\",\"Value\":\"CURSE\"}")),
                    "E_COND_TRIGGER_SCOPE", "STATUS_TYPE");
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"REMOVE_STATUS\",\"Target\":\"SELF\",\"Status\":\"TRIGGER_STATUS\"}")),
                    "E_TRIGGER_STATUS_SCOPE", "TRIGGER_STATUS");
            });

            t.Case("SUMMON: Count is capped at 4 and CharacterConfig is required", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"SUMMON\",\"Target\":\"TRIGGER_TARGET_POSITION\",\"CharacterConfig\":\"CF_X\",\"Count\":5}")),
                    "E_SUMMON_CAP", "count 5");
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"SUMMON\",\"Target\":\"TRIGGER_TARGET_POSITION\",\"Count\":1}")),
                    "E_SUMMON_CONFIG", "missing config");
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"NONE\",\"CharacterConfig\":\"CF_X\"}")),
                    "E_SUMMON_TYPE", "eSummonTypes NONE is never authored");
            });

            t.Case("ADD_STATUS: Status and StatusOneOf are mutually exclusive and one is required", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL", "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\"}")),
                    "E_STATUS_MISSING", "neither");
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"A\",\"StatusOneOf\":[\"B\"]}")),
                    "E_STATUS_AMBIGUOUS", "both");
            });

            t.Case("ALLY_BY_RANK requires a Rank block", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_BY_RANK\",\"Status\":\"S\"}")),
                    "E_RANK_MISSING", "no rank");
            });

            t.Case("required condition fields are enforced", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"LTE\"}")),
                    "E_VALUE_MISSING", "hp threshold without percent/flat");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"LTE\",\"Percent\":10,\"Flat\":3}")),
                    "E_VALUE_AMBIGUOUS", "both percent and flat");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"HP_THRESHOLD\",\"Percent\":10}")),
                    "E_CMP_MISSING", "no comparator");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"HAS_STATUS\"}")),
                    "E_VALUE_MISSING", "has_status without value");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"ROW\",\"Value\":\"SIDEWAYS\"}")),
                    "E_VALUE_MISSING", "bad row");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_ABILITY_USED", "{\"Type\":\"ABILITY_RANGED\",\"Value\":\"yes\"}")),
                    "E_VALUE_MISSING", "non-bool bool");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_ABILITY_USED", "{\"Type\":\"ROLL_TIER\",\"Comparator\":\"GTE\",\"Value\":\"AMAZING\"}")),
                    "E_TIER_UNKNOWN", "bad tier");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_KILL", "{\"Type\":\"COUNTER\",\"Comparator\":\"GTE\",\"Value\":1}")),
                    "E_COUNTER_NAME", "counter without name");
            });

            t.Case("required effect fields are enforced", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL", "{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\"}")),
                    "E_STAT_MISSING", "stat_change without stat");
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"FOC\",\"StatChangeType\":\"MAGICAL\"}")),
                    "E_VALUE_MISSING", "stat_change without a value");
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL", "{\"Type\":\"COUNTER_SET\",\"Value\":3}")),
                    "E_COUNTER_NAME", "counter_set without name");
            });

            t.Case("Persistent (§6 run-persistence escape hatch) parses on OWNED COUNTER_ADD/COUNTER_SET " +
                   "and is rejected everywhere else", () =>
            {
                var ok = RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"COUNTER_ADD\",\"Name\":\"cf_bond\",\"Delta\":1,\"Persistent\":true}"));
                Check.NoErrors(ok, "Persistent COUNTER_ADD on a default-OWNED recipe is well-formed");
                var r = ok.Find("SKILL_T");
                Check.True(r != null && r.IsLive, "recipe is live");
                Check.True(r.Effects[0].Persistent, "Persistent parsed true on the effect");

                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S\",\"Persistent\":true}")),
                    "E_PERSISTENT_SCOPE", "Persistent on a non-counter effect is rejected");

                string combatScoped =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Scope\":\"COMBAT\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"cf_bond\",\"Delta\":1,\"Persistent\":true}]," +
                    "\"ProcChance\":100}}";
                Check.HasFinding(RecipeParser.Parse(combatScoped),
                    "E_PERSISTENT_SCOPE", "Persistent on a COMBAT-scoped recipe is rejected (no owner to persist against)");
            });

            t.Case("warnings: unknown fields, ignored Of, non-standard eDamageType", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.One("ON_KILL", "", J.SelfStatusEffect, ",\"Wibble\":3")),
                    "W_UNKNOWN_FIELD", "unknown recipe field");
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_ABILITY_USED",
                    "{\"Type\":\"ABILITY_TAG\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"T\"}")),
                    "W_OF_IGNORED", "Of on a non-Of condition");
                Check.HasFinding(RecipeParser.Parse(J.Effect("ON_KILL",
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"FOC\",\"StatChangeType\":\"FIRE\",\"FlatValue\":1}")),
                    "W_DAMAGE_TYPE", "eDamageType on a non-HP stat");
            });

            t.Case("ordering: recipes are ordered by Priority then ordinal id, not source order", () =>
            {
                var set = RecipeParser.Parse(
                    "{\"SKILL_Z\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":1,\"Effects\":[" + J.SelfStatusEffect + "]}," +
                    "\"SKILL_B\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0,\"Effects\":[" + J.SelfStatusEffect + "]}," +
                    "\"SKILL_A\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0,\"Effects\":[" + J.SelfStatusEffect + "]}}");
                Check.NoErrors(set, "clean");
                Check.Eq("SKILL_A", set.Ordered[0].Id, "priority 0, id A first");
                Check.Eq("SKILL_B", set.Ordered[1].Id, "priority 0, id B second");
                Check.Eq("SKILL_Z", set.Ordered[2].Id, "priority 1 last");
            });

            t.Case("fail-safe: the parser never throws on adversarial input", () =>
            {
                string[] garbage =
                {
                    "", "   ", "null", "\"str\"", "{", "}", "[{}]", "{\"a\":}", "{\"a\":\"\\q\"}",
                    "{\"a\":{\"Trigger\":1}}", "{\"a\":{\"Effects\":[[]]}}", "{\"a\":0.0.0}",
                    "{\"a\":{\"Effects\":[{\"Type\":null}]}}", "{\"\\u0041\":{}}", "/* unterminated"
                };
                for (int i = 0; i < garbage.Length; i++)
                {
                    var set = RecipeParser.Parse(garbage[i]);
                    Check.True(set != null, "set returned for input " + i);
                }
                Check.True(RecipeParser.Parse(null) != null, "null input");
            });

            t.Case("shipped pack: CF_PACK_BALDURS/skillrecipes.json parses as a clean v1.0 book", () =>
            {
                string path = FindRepoFile("FTK2.ClassForge/data/ClassPacks/CF_PACK_BALDURS/skillrecipes.json");
                Check.True(path != null, "shipped pack file located");
                var set = RecipeParser.Parse(File.ReadAllText(path));
                Check.NoErrors(set, "shipped pack has no errors");
                Check.Eq(5, set.Ordered.Count, "five shipped recipes");
                Check.HasFinding(set, "W_SCHEMA_DEFAULT", "v1 recipes get the SchemaVersion warning");
            });

            t.Case("fixtures: every real-mechanic fixture parses with zero errors", () =>
            {
                var files = Fixtures.All();
                Check.True(files.Count >= 4, "at least four fixtures present");
                for (int i = 0; i < files.Count; i++)
                {
                    var set = RecipeParser.Parse(File.ReadAllText(files[i]));
                    Check.NoErrors(set, Path.GetFileName(files[i]));
                    Check.True(set.Ordered.Count > 0, Path.GetFileName(files[i]) + " has recipes");
                }
            });
        }

        private static string FindRepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
