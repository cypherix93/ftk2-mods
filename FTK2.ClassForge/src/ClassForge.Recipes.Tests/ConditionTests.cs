using System;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    public static class ConditionTests
    {
        private static string Negated(string cond)
        {
            return "{\"Negate\":true," + cond.Substring(1);
        }

        /// <summary>Runs the true case, the false case, and the Negate-inverted false case.</summary>
        private static void Both(TestRunner t, string name, TriggerKind trigger, string token, string cond,
                                 Action<Rig> setupTrue, Action<Rig> setupFalse)
        {
            t.Case("condition " + name, () =>
            {
                var a = Rig.Build(J.Cond(token, cond), 5);
                if (setupTrue != null) setupTrue(a);
                Check.PlanCount(a.Fire(trigger), 1, name + ": true case fires");

                var b = Rig.Build(J.Cond(token, cond), 5);
                if (setupFalse != null) setupFalse(b);
                Check.PlanCount(b.Fire(trigger), 0, name + ": false case does not fire");

                var c = Rig.Build(J.Cond(token, Negated(cond)), 5);
                if (setupFalse != null) setupFalse(c);
                Check.PlanCount(c.Fire(trigger), 1, name + ": Negate inverts the false case");

                var d = Rig.Build(J.Cond(token, Negated(cond)), 5);
                if (setupTrue != null) setupTrue(d);
                Check.PlanCount(d.Fire(trigger), 0, name + ": Negate inverts the true case");
            });
        }

        private static FakeAbility Melee(Rig r) { return (FakeAbility)r.Ctx.Abilities["AB_SLASH"]; }

        public static void Run(TestRunner t)
        {
            t.Section("conditions (23 tokens) + Negate + Of");

            t.Case("conditions: an empty array is always true", () =>
            {
                var rig = Rig.Build(J.Cond("ON_KILL", ""), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "no conditions");
            });

            t.Case("conditions: multiple entries are ANDed", () =>
            {
                string two = "{\"Type\":\"HAS_STATUS\",\"Value\":\"STATUS_BLEED_00\"},{\"Type\":\"HAS_STATUS\",\"Value\":\"STATUS_CURSE_00\"}";
                var a = Rig.Build(J.Cond("ON_KILL", two), 5);
                a.Hero.WithStatus("STATUS_BLEED_00");
                Check.PlanCount(a.Fire(TriggerKind.ON_KILL), 0, "only one of two");
                var b = Rig.Build(J.Cond("ON_KILL", two), 5);
                b.Hero.WithStatus("STATUS_BLEED_00").WithStatus("STATUS_CURSE_00");
                Check.PlanCount(b.Fire(TriggerKind.ON_KILL), 1, "both");
            });

            Both(t, "HP_THRESHOLD (Percent)", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"LTE\",\"Percent\":50}",
                r => r.Hero.With("HP", 40), r => r.Hero.With("HP", 90));

            Both(t, "HP_THRESHOLD (Flat)", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"LT\",\"Flat\":10}",
                r => r.Hero.With("HP", 5), r => r.Hero.With("HP", 50));

            Both(t, "HAS_STATUS", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"HAS_STATUS\",\"Value\":\"STATUS_BLEED_00\"}",
                r => r.Hero.WithStatus("STATUS_BLEED_00"), null);

            Both(t, "LACKS_STATUS", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"LACKS_STATUS\",\"Value\":\"STATUS_BLEED_00\"}",
                null, r => r.Hero.WithStatus("STATUS_BLEED_00"));

            Both(t, "ROW", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"ROW\",\"Value\":\"BACK\"}",
                r => r.Hero.RowValue = ClassForge.Recipes.Abstractions.EntityRow.BACK,
                r => r.Hero.RowValue = ClassForge.Recipes.Abstractions.EntityRow.FRONT);

            Both(t, "WEAPON_CLASS", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"WEAPON_CLASS\",\"Value\":\"BOW\"}",
                r => r.Hero.Weapon = "BOW", r => r.Hero.Weapon = "BLADE");

            Both(t, "ABILITY_TAG", TriggerKind.ON_ABILITY_USED, "ON_ABILITY_USED",
                "{\"Type\":\"ABILITY_TAG\",\"Value\":\"CF_BARGAIN\"}",
                null, r => Melee(r).TagList.Clear());

            Both(t, "TARGET_BASE_TYPE", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"TARGET_BASE_TYPE\",\"Value\":\"SKELETON\"}",
                r => r.Foe.Base = "SKELETON", r => r.Foe.Base = "HUMAN");

            Both(t, "HOSTILE_ACTION", TriggerKind.ON_ABILITY_USED, "ON_ABILITY_USED",
                "{\"Type\":\"HOSTILE_ACTION\",\"Value\":true}",
                null, r => Melee(r).Enemy = false);

            Both(t, "ABILITY_STAT", TriggerKind.ON_ABILITY_USED, "ON_ABILITY_USED",
                "{\"Type\":\"ABILITY_STAT\",\"Value\":\"PHY\"}",
                null, r => Melee(r).StatValue = "INT");

            Both(t, "ABILITY_RANGED", TriggerKind.ON_ABILITY_USED, "ON_ABILITY_USED",
                "{\"Type\":\"ABILITY_RANGED\",\"Value\":false}",
                null, r => Melee(r).Ranged = true);

            Both(t, "CHARACTER_TYPE", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"CHARACTER_TYPE\",\"Value\":\"BOSS\"}",
                r => r.Hero.CharType = "BOSS", r => r.Hero.CharType = "PLAYER");

            Both(t, "FOCUS_CURRENT (vs MAX)", TriggerKind.ON_COMBAT_START, "ON_COMBAT_START",
                "{\"Type\":\"FOCUS_CURRENT\",\"Comparator\":\"LT\",\"Value\":\"MAX\"}",
                r => r.Hero.With("FOC", 1).With("MXFOC", 3),
                r => r.Hero.With("FOC", 3).With("MXFOC", 3));

            Both(t, "STATUS_COUNT (HARMFUL category)", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"STATUS_COUNT\",\"Category\":\"HARMFUL\",\"Comparator\":\"GTE\",\"Value\":2}",
                r => r.Hero.WithStatus("STATUS_BLEED_00").WithStatus("STATUS_CURSE_00"),
                r => r.Hero.WithStatus("STATUS_BLEED_00").WithStatus("STATUS_ATTACKUP_00"));

            Both(t, "STATUS_COUNT (explicit Types list)", TriggerKind.ON_KILL, "ON_KILL",
                "{\"Type\":\"STATUS_COUNT\",\"Types\":[\"FIRE\",\"ICE\"],\"Comparator\":\"EQ\",\"Value\":1}",
                r => r.Hero.WithStatus("STATUS_FIRE_00"),
                r => r.Hero.WithStatus("STATUS_BLEED_00"));

            Both(t, "STATUS_TYPE", TriggerKind.ON_STATUS_APPLIED, "ON_STATUS_APPLIED",
                "{\"Type\":\"STATUS_TYPE\",\"Value\":\"CURSE\"}",
                null, r => r.Ctx.AddStatus("STATUS_CURSE_00", "DEBUFF"));

            Both(t, "ITEM_CLASS", TriggerKind.ON_CONSUMABLE_USED, "ON_CONSUMABLE_USED",
                "{\"Type\":\"ITEM_CLASS\",\"Value\":\"DRINK\"}",
                null, r => r.Ctx.AddItem("DRINK_ALE", "FOOD", true));

            Both(t, "ITEM_CONSUMABLE", TriggerKind.ON_CONSUMABLE_USED, "ON_CONSUMABLE_USED",
                "{\"Type\":\"ITEM_CONSUMABLE\",\"Value\":true}",
                null, r => r.Ctx.AddItem("DRINK_ALE", "DRINK", false));

            t.Case("condition ROLL_TIER: EQ / GTE / LTE over the four-tier ladder", () =>
            {
                var eqPerfect = "{\"Type\":\"ROLL_TIER\",\"Comparator\":\"EQ\",\"Value\":\"PERFECT\"}";
                var gteSuccess = "{\"Type\":\"ROLL_TIER\",\"Comparator\":\"GTE\",\"Value\":\"SUCCESS\"}";
                var lteFail = "{\"Type\":\"ROLL_TIER\",\"Comparator\":\"LTE\",\"Value\":\"FAIL\"}";

                var a = Rig.Build(J.Cond("ON_ABILITY_USED", eqPerfect), 5);
                Check.PlanCount(a.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.PERFECT), 1, "EQ PERFECT on PERFECT");
                Check.PlanCount(a.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.SUCCESS), 0, "EQ PERFECT on SUCCESS");

                var b = Rig.Build(J.Cond("ON_ABILITY_USED", gteSuccess), 5);
                Check.PlanCount(b.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.PERFECT), 1, "GTE SUCCESS on PERFECT");
                Check.PlanCount(b.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.SUCCESS), 1, "GTE SUCCESS on SUCCESS");
                Check.PlanCount(b.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.FAIL), 0, "GTE SUCCESS on FAIL");

                var c = Rig.Build(J.Cond("ON_ABILITY_USED", lteFail), 5);
                Check.PlanCount(c.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.CRIT_FAIL), 1, "LTE FAIL on CRIT_FAIL");
                Check.PlanCount(c.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.SUCCESS), 0, "LTE FAIL on SUCCESS");
            });

            t.Case("condition FOCUS_SPENT: reads pCombatDecision.FocusUsed", () =>
            {
                var a = Rig.Build(J.Cond("ON_ABILITY_USED", "{\"Type\":\"FOCUS_SPENT\",\"Comparator\":\"GTE\",\"Value\":2}"), 5);
                Check.PlanCount(a.Fire(TriggerKind.ON_ABILITY_USED), 1, "FocusUsed 2 >= 2");
                var b = Rig.Build(J.Cond("ON_ABILITY_USED", "{\"Type\":\"FOCUS_SPENT\",\"Comparator\":\"GTE\",\"Value\":3}"), 5);
                Check.PlanCount(b.Fire(TriggerKind.ON_ABILITY_USED), 0, "FocusUsed 2 < 3");
            });

            t.Case("condition ABILITY_REPEATED: compares against the owner's LastAbilityId", () =>
            {
                var rig = Rig.Build(J.Cond("ON_ABILITY_USED", "{\"Type\":\"ABILITY_REPEATED\",\"Value\":false}"), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.SUCCESS), 1, "first use is not repeated");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.SUCCESS), 0, "second use is repeated");
            });

            t.Case("condition MOVED_THIS_ROUND: fed by the ApplyAction MOVE observer", () =>
            {
                var rig = Rig.Build(J.Cond("ON_TURN_END", "{\"Type\":\"MOVED_THIS_ROUND\",\"Value\":false}"), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_END), 1, "did not move");
                rig.Dispatcher.ObserveMove(rig.Ctx, rig.Hero);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_END), 0, "moved");
            });

            t.Case("condition ALL_ALLIES_ACTED: every other living ally acted this round", () =>
            {
                var rig = Rig.Build(J.Cond("ON_ABILITY_DECLARED", "{\"Type\":\"ALL_ALLIES_ACTED\",\"Value\":true}"), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_DECLARED), 0, "ally has not acted");
                rig.Dispatcher.OnAbilityUsed(rig.Ctx, new AbilityUsedEvent
                { Origin = rig.Ally, Target = rig.Foe, AbilityId = "AB_SLASH", RollTier = RollTier.SUCCESS });
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_DECLARED), 1, "ally acted");
                rig.Ctx.RoundValue = 2;
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_DECLARED), 0, "new round resets the check");
            });

            t.Case("condition COUNTER: reads the per-battle counter table", () =>
            {
                // recipe 1 increments the counter, recipe 2 (lower priority) gates on it
                string json =
                    "{\"SKILL_A_BUMP\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}],\"ProcChance\":100}," +
                    "\"SKILL_B_GATE\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":1," +
                    "\"Conditions\":[{\"Type\":\"COUNTER\",\"Name\":\"N\",\"Comparator\":\"GTE\",\"Value\":2}]," +
                    "\"Effects\":[" + J.SelfStatusEffect + "],\"ProcChance\":100}}";
                var rig = Rig.Build(json, 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "counter=1, gate closed");
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 2, "counter=2, gate open");
            });

            t.Section("Of selector");

            t.Case("Of: SELF (default), TRIGGER_TARGET and TRIGGER_SOURCE read different entities", () =>
            {
                string self = "{\"Type\":\"HAS_STATUS\",\"Value\":\"STATUS_MARKED_00\"}";
                string target = "{\"Type\":\"HAS_STATUS\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"STATUS_MARKED_00\"}";
                string source = "{\"Type\":\"HAS_STATUS\",\"Of\":\"TRIGGER_SOURCE\",\"Value\":\"STATUS_MARKED_00\"}";

                var a = Rig.Build(J.Cond("ON_KILL", self), 5);
                a.Foe.WithStatus("STATUS_MARKED_00");
                Check.PlanCount(a.Fire(TriggerKind.ON_KILL), 0, "SELF does not see the target's status");

                var b = Rig.Build(J.Cond("ON_KILL", target), 5);
                b.Foe.WithStatus("STATUS_MARKED_00");
                Check.PlanCount(b.Fire(TriggerKind.ON_KILL), 1, "Of TRIGGER_TARGET sees it (subsumes TARGET_TRACKING)");

                var c = Rig.Build(J.Cond("ON_DAMAGE_TAKEN", source), 5);
                c.Foe.WithStatus("STATUS_MARKED_00");
                Check.PlanCount(c.Fire(TriggerKind.ON_DAMAGE_TAKEN), 1, "Of TRIGGER_SOURCE sees the attacker");
            });

            t.Case("Of: applies to HP_THRESHOLD, ROW, CHARACTER_TYPE, STATUS_COUNT, FOCUS_CURRENT", () =>
            {
                var hp = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"HP_THRESHOLD\",\"Of\":\"TRIGGER_TARGET\",\"Comparator\":\"LTE\",\"Percent\":20}"), 5);
                hp.Foe.With("HP", 10);
                Check.PlanCount(hp.Fire(TriggerKind.ON_KILL), 1, "target HP read");

                var row = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"ROW\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"BACK\"}"), 5);
                row.Foe.RowValue = ClassForge.Recipes.Abstractions.EntityRow.BACK;
                Check.PlanCount(row.Fire(TriggerKind.ON_KILL), 1, "target row read");

                var ct = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"CHARACTER_TYPE\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"BOSS\"}"), 5);
                ct.Foe.CharType = "BOSS";
                Check.PlanCount(ct.Fire(TriggerKind.ON_KILL), 1, "target CharacterType read (replaces IsBoss helpers)");

                var sc = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"STATUS_COUNT\",\"Of\":\"TRIGGER_TARGET\",\"Category\":\"HARMFUL\",\"Comparator\":\"GTE\",\"Value\":1}"), 5);
                sc.Foe.WithStatus("STATUS_BLEED_00");
                Check.PlanCount(sc.Fire(TriggerKind.ON_KILL), 1, "target status count read");

                var fc = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"FOCUS_CURRENT\",\"Of\":\"TRIGGER_TARGET\",\"Comparator\":\"GTE\",\"Value\":2}"), 5);
                fc.Foe.With("FOC", 2);
                Check.PlanCount(fc.Fire(TriggerKind.ON_KILL), 1, "target focus read");
            });

            t.Case("Of: a missing bound entity evaluates false (fail-safe, no throw)", () =>
            {
                var rig = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"HAS_STATUS\",\"Of\":\"TRIGGER_SOURCE\",\"Value\":\"STATUS_MARKED_00\"}"), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "ON_KILL has no TRIGGER_SOURCE");
            });

            t.Case("conditions: never draw from the RNG", () =>
            {
                var rig = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"LTE\",\"Percent\":50}"), 5);
                rig.Hero.With("HP", 10);
                rig.Fire(TriggerKind.ON_KILL);
                Check.Eq(0, rig.Rng.Draws, "ProcChance 100 + conditions = zero draws");
            });
        }
    }
}
