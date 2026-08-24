using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// <c>DAMAGE_DEALT_PCT</c> — the value source that makes proportional lifesteal and proportional
    /// reflect expressible.
    ///
    /// Every negative control here guards a way the token could ship as a silent no-op, which is the
    /// failure mode this repo keeps rediscovering: the two shipped "CURSE" recipes parsed cleanly and
    /// did nothing, and a flat drain that rounds to zero on a small hit reads to a player as a broken
    /// trait rather than as a small number.
    /// </summary>
    public static class DamageShareTests
    {
        public static void Run(TestRunner t)
        {
            t.Section("DAMAGE_DEALT_PCT: share arithmetic");

            t.Case("100 damage at 25% -> 25", () =>
            {
                Check.Eq(25, ResolveShare(100, 25), "exact division");
            });

            t.Case("7 damage at 25% -> 2 (1.75 rounds to nearest)", () =>
            {
                Check.Eq(2, ResolveShare(7, 25), "rounds to nearest, not truncated");
            });

            t.Case("negative Percent preserves sign (reflect back at the attacker)", () =>
            {
                Check.Eq(-25, ResolveShare(100, -25), "sign follows Percent");
            });

            t.Case("floor at 1: 1 damage at 25% -> 1, never 0", () =>
            {
                // 1 * 25 / 100 = 0.25 -> rounds to 0 -> floored to 1. A drain that silently does
                // nothing on chip damage is indistinguishable from a broken trait.
                Check.Eq(1, ResolveShare(1, 25), "small hits still drain something");
            });

            t.Case("damage magnitude is used, so a negative amount still drains positively", () =>
            {
                Check.Eq(25, ResolveShare(-100, 25), "sign comes from Percent, not from the amount");
            });

            t.Case("zero damage resolves to 0", () =>
            {
                Check.Eq(0, ResolveShare(0, 25), "no damage, no drain");
            });

            t.Case("no Percent authored resolves to 0", () =>
            {
                var t2 = new TriggerContext { Amount = 100 };
                var effect = new RecipeEffect();
                Check.Eq(0, ValueSources.Resolve(Vocabulary.SourceDamageDealtPct, effect, t2),
                    "undefined share is 0, and the validator rejects it outright");
            });

            // ================================================================================
            t.Section("DAMAGE_DEALT_PCT: validator fails closed");

            t.Case("rejected under a trigger that carries no damage", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Effects\":[{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"HP\"," +
                    "\"StatChangeType\":\"REGEN\",\"FlatValueFrom\":\"DAMAGE_DEALT_PCT\",\"Percent\":25}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_VALUE_SOURCE_SCOPE",
                    "no damage in scope under ON_TURN_START");
            });

            t.Case("rejected when no Percent is authored", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGE_DEALT\"," +
                    "\"Effects\":[{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"HP\"," +
                    "\"StatChangeType\":\"REGEN\",\"FlatValueFrom\":\"DAMAGE_DEALT_PCT\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_VALUE_SOURCE_NO_PERCENT",
                    "a share with no percentage is a no-op");
            });

            t.Case("accepted under ON_DAMAGE_TAKEN (reflect direction)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGE_TAKEN\"," +
                    "\"Effects\":[{\"Type\":\"STAT_CHANGE\",\"Target\":\"TRIGGER_SOURCE\",\"Stat\":\"HP\"," +
                    "\"StatChangeType\":\"MAGICAL\",\"FlatValueFrom\":\"DAMAGE_DEALT_PCT\",\"Percent\":-50}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "ON_DAMAGE_TAKEN carries damage");
            });

            // ================================================================================
            t.Section("DAMAGE_DEALT_PCT: end-to-end lifesteal");

            t.Case("ON_DAMAGE_DEALT drains a share of the hit onto SELF", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGE_DEALT\"," +
                    "\"Effects\":[{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"HP\"," +
                    "\"StatChangeType\":\"REGEN\",\"FlatValueFrom\":\"DAMAGE_DEALT_PCT\"," +
                    "\"Percent\":25,\"Min\":1}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "validates cleanly");

                var rig = Rig.FromSet(set, 1);
                var plan = rig.Fire(TriggerKind.ON_DAMAGE_DEALT);
                Check.PlanCount(plan, 1, "one STAT_CHANGE emitted");

                var change = plan[0] as StatChangeAction;
                Check.True(change != null, "is a StatChangeAction");
                Check.Eq("HP", change.Stat, "heals HP");
                // The rig fires ON_DAMAGE_DEALT with Amount = 7; 7 * 25% = 1.75 -> 2.
                Check.Eq(2, change.FlatValue.HasValue ? change.FlatValue.Value : -9999,
                    "drained a quarter of the 7 damage the trigger carried");
                Check.True(!change.FlatPercent.HasValue,
                    "emitted as a plain FlatValue, never FlatPercent");
            });

            // ================================================================================
            t.Section("SUMMON: placement and allegiance");

            t.Case("TRIGGER_TARGET_POSITION under a target-less trigger is rejected", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Effects\":[{\"Type\":\"SUMMON\",\"Target\":\"TRIGGER_TARGET_POSITION\"," +
                    "\"SummonType\":\"SPECIFIC\",\"CharacterConfig\":\"BAT_CAVE_01\",\"Count\":1}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_SUMMON_TARGET",
                    "ON_COMBAT_START carries no target, so no summon action is emitted");
            });

            t.Case("SELF under a target-less trigger validates", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Effects\":[{\"Type\":\"SUMMON\",\"Target\":\"SELF\"," +
                    "\"SummonType\":\"SPECIFIC\",\"CharacterConfig\":\"BAT_CAVE_01\",\"Count\":1}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "SELF always resolves; the executor picks the friendly tile");
            });

            t.Case("TRIGGER_TARGET_POSITION under ON_KILL validates", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"Effects\":[{\"Type\":\"SUMMON\",\"Target\":\"TRIGGER_TARGET_POSITION\"," +
                    "\"SummonType\":\"SPECIFIC\",\"CharacterConfig\":\"BAT_CAVE_01\",\"Count\":1}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "ON_KILL carries the slain target");
            });
        }

        private static int ResolveShare(int damage, int percent)
        {
            var t = new TriggerContext { Amount = damage };
            var effect = new RecipeEffect { Percent = percent };
            return ValueSources.Resolve(Vocabulary.SourceDamageDealtPct, effect, t);
        }
    }
}
