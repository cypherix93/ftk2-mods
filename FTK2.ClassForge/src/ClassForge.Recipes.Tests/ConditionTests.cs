using System;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
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

        /// <summary>Like <see cref="J.Cond"/> but SchemaVersion 1.4 — required for the v1.4-only
        /// ALLY_IN_FRONT condition (<see cref="J.Cond"/> hardcodes 1.1 for the pre-existing suite).</summary>
        private static string Cond14(string trigger, string condition)
        {
            return "{\"SKILL_T\":{\"SchemaVersion\":\"1.4\",\"DisplayName\":\"T\",\"Trigger\":\"" + trigger +
                   "\",\"Conditions\":[" + condition + "],\"Effects\":[" + J.CounterEffect + "]," +
                   "\"ProcChance\":100,\"AiProcChance\":100}}";
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

            t.Case("condition HP_THRESHOLD (Percent): negative control — a stat source with no live MXHP "
                + "(GameAdapters.GetStat's base-table fallback for a non-combat entity, whose static table "
                + "carries no MXHP key) resolves the percentage safely to exactly 0 instead of throwing on "
                + "the division, rather than crashing or silently computing garbage", () =>
            {
                var rig = Rig.Build(J.Cond("ON_KILL",
                    "{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"EQ\",\"Percent\":0}"), 5);
                rig.Hero.With("HP", 40).With("MXHP", 0); // no live combat state -> MXHP unresolved
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1,
                    "max<=0 degrades HP_PCT to exactly 0, matching Comparator EQ 0 -- no throw, no garbage value");
            });

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

            // ================================================================================
            t.Section("ALLY_IN_FRONT (v1.4, cover spec)");

            const string AllyInFront = "{\"Type\":\"ALLY_IN_FRONT\",\"Value\":true}";
            const string NoAllyInFront = "{\"Type\":\"ALLY_IN_FRONT\",\"Value\":false}";

            t.Case("ALLY_IN_FRONT: a living ally on the adjacent FRONT tile of the same row-line fires", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(3, 2); rig.Hero.RowValue = EntityRow.BACK;
                rig.Ally.At(4, 2); rig.Ally.RowValue = EntityRow.FRONT;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "ally directly in front");
            });

            t.Case("ALLY_IN_FRONT: the direction is group-dependent — dx = -1 counts too", () =>
            {
                // Stock board "|..Aa.bB..|": group 0's FRONT sits at x+1, group 1's at x-1. The predicate
                // mirrors CombatHelper.cs:2414 (|dx| == 1 + the other tile's row), never a hardcoded x+1.
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(7, 2); rig.Hero.RowValue = EntityRow.BACK;
                rig.Ally.At(6, 2); rig.Ally.RowValue = EntityRow.FRONT;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "group-1 side: FRONT is at x-1");
            });

            t.Case("ALLY_IN_FRONT: a DEAD ally in front does not count", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(3, 2); rig.Hero.RowValue = EntityRow.BACK;
                rig.Ally.At(4, 2); rig.Ally.RowValue = EntityRow.FRONT;
                rig.Ally.Alive = false;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "corpses give no cover");
            });

            t.Case("ALLY_IN_FRONT: a FRONT ally two columns away does not count", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(3, 2); rig.Hero.RowValue = EntityRow.BACK;
                rig.Ally.At(5, 2); rig.Ally.RowValue = EntityRow.FRONT;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "|dx| == 2 is not adjacent");
            });

            t.Case("ALLY_IN_FRONT: an adjacent ally standing in the BACK row does not count", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(4, 2); rig.Hero.RowValue = EntityRow.FRONT;
                rig.Ally.At(3, 2); rig.Ally.RowValue = EntityRow.BACK;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "the tile in front must be a FRONT tile");
            });

            t.Case("ALLY_IN_FRONT: an adjacent FRONT ally on a different row-line does not count", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(3, 2); rig.Hero.RowValue = EntityRow.BACK;
                rig.Ally.At(4, 3); rig.Ally.RowValue = EntityRow.FRONT;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "different y = a different row-line");
            });

            t.Case("ALLY_IN_FRONT: an ENEMY on the adjacent FRONT tile does not count", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(3, 2); rig.Hero.RowValue = EntityRow.BACK;
                rig.Foe.At(4, 2); rig.Foe.RowValue = EntityRow.FRONT;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "opponents are not allies");
            });

            t.Case("ALLY_IN_FRONT: an unresolvable tile (int.MinValue) fails safe to false", () =>
            {
                var own = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                own.Ally.At(4, 2); own.Ally.RowValue = EntityRow.FRONT;   // owner keeps the sentinel
                Check.PlanCount(own.Fire(TriggerKind.ON_TURN_START), 0, "owner tile unresolvable");

                var other = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                other.Hero.At(3, 2); other.Hero.RowValue = EntityRow.BACK;
                other.Ally.RowValue = EntityRow.FRONT;                     // ally keeps the sentinel
                Check.PlanCount(other.Fire(TriggerKind.ON_TURN_START), 0, "ally tile unresolvable, no MinValue arithmetic");
            });

            t.Case("ALLY_IN_FRONT: Value:false inverts, and Negate inverts again", () =>
            {
                var empty = Rig.Build(Cond14("ON_TURN_START", NoAllyInFront), 5);
                empty.Hero.At(3, 2); empty.Hero.RowValue = EntityRow.BACK;
                empty.Ally.At(9, 9); empty.Ally.RowValue = EntityRow.FRONT;
                Check.PlanCount(empty.Fire(TriggerKind.ON_TURN_START), 1, "Value:false fires with nobody in front");

                var covered = Rig.Build(Cond14("ON_TURN_START", NoAllyInFront), 5);
                covered.Hero.At(3, 2); covered.Hero.RowValue = EntityRow.BACK;
                covered.Ally.At(4, 2); covered.Ally.RowValue = EntityRow.FRONT;
                Check.PlanCount(covered.Fire(TriggerKind.ON_TURN_START), 0, "Value:false does not fire when covered");

                var neg = Rig.Build(Cond14("ON_TURN_START", Negated(AllyInFront)), 5);
                neg.Hero.At(3, 2); neg.Hero.RowValue = EntityRow.BACK;
                neg.Ally.At(4, 2); neg.Ally.RowValue = EntityRow.FRONT;
                Check.PlanCount(neg.Fire(TriggerKind.ON_TURN_START), 0, "Negate inverts the true case");
            });

            t.Case("ALLY_IN_FRONT: draws no RNG (it must be legal under the RNG-free ON_DAMAGE_PENDING)", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", AllyInFront), 5);
                rig.Hero.At(3, 2); rig.Hero.RowValue = EntityRow.BACK;
                rig.Ally.At(4, 2); rig.Ally.RowValue = EntityRow.FRONT;
                rig.Fire(TriggerKind.ON_TURN_START);
                Check.Eq(0, rig.Rng.Draws, "zero draws");
            });

            // ================================================================================
            t.Section("SELF_LEVEL (v1.4) - THIS character's own level, not the party average");

            const string LvlGte3 = "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"GTE\",\"Value\":3}";
            const string LvlLte3 = "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"LTE\",\"Value\":3}";
            const string LvlEq3  = "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"EQ\",\"Value\":3}";
            const string LvlGte1 = "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"GTE\",\"Value\":1}";

            t.Case("SELF_LEVEL GTE: fails below the boundary, passes at and above it", () =>
            {
                var below = Rig.Build(Cond14("ON_TURN_START", LvlGte3), 5);
                below.Hero.AtLevel(2);
                Check.PlanCount(below.Fire(TriggerKind.ON_TURN_START), 0, "level 2 is below GTE 3");

                var at = Rig.Build(Cond14("ON_TURN_START", LvlGte3), 5);
                at.Hero.AtLevel(3);
                Check.PlanCount(at.Fire(TriggerKind.ON_TURN_START), 1, "level 3 IS the GTE boundary");

                var above = Rig.Build(Cond14("ON_TURN_START", LvlGte3), 5);
                above.Hero.AtLevel(4);
                Check.PlanCount(above.Fire(TriggerKind.ON_TURN_START), 1, "level 4 is above GTE 3");
            });

            t.Case("SELF_LEVEL LTE: passes at and below the boundary, fails above it", () =>
            {
                var below = Rig.Build(Cond14("ON_TURN_START", LvlLte3), 5);
                below.Hero.AtLevel(2);
                Check.PlanCount(below.Fire(TriggerKind.ON_TURN_START), 1, "level 2 is below LTE 3");

                var at = Rig.Build(Cond14("ON_TURN_START", LvlLte3), 5);
                at.Hero.AtLevel(3);
                Check.PlanCount(at.Fire(TriggerKind.ON_TURN_START), 1, "level 3 IS the LTE boundary");

                var above = Rig.Build(Cond14("ON_TURN_START", LvlLte3), 5);
                above.Hero.AtLevel(4);
                Check.PlanCount(above.Fire(TriggerKind.ON_TURN_START), 0, "level 4 is above LTE 3");
            });

            t.Case("SELF_LEVEL EQ: passes only on the exact level", () =>
            {
                var lo = Rig.Build(Cond14("ON_TURN_START", LvlEq3), 5);
                lo.Hero.AtLevel(2);
                Check.PlanCount(lo.Fire(TriggerKind.ON_TURN_START), 0, "2 != 3");

                var eq = Rig.Build(Cond14("ON_TURN_START", LvlEq3), 5);
                eq.Hero.AtLevel(3);
                Check.PlanCount(eq.Fire(TriggerKind.ON_TURN_START), 1, "3 == 3");

                var hi = Rig.Build(Cond14("ON_TURN_START", LvlEq3), 5);
                hi.Hero.AtLevel(4);
                Check.PlanCount(hi.Fire(TriggerKind.ON_TURN_START), 0, "4 != 3");
            });

            t.Case("SELF_LEVEL: level 0 is a real level, never 'unknown'", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"EQ\",\"Value\":0}"), 5);
                rig.Hero.AtLevel(0);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1,
                    "ProgressionHelper.GetEntityLevel returns 0 for a character with no XP yet");
            });

            t.Case("SELF_LEVEL: reads THIS character, not the party average", () =>
            {
                // The whole reason the token exists: in a mixed party PARTY_AVG_LEVEL reads 5 here and
                // would open the gate for a level-1 owner.
                var rig = Rig.Build(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"GTE\",\"Value\":5}"), 5);
                rig.Hero.AtLevel(1);
                rig.Ally.AtLevel(9);
                rig.Ctx.PartyAverageLevelValue = 5;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "the owner is level 1; the average is irrelevant");
            });

            t.Case("SELF_LEVEL: an UNKNOWN level is false", () =>
            {
                // Hero keeps FakeEntity's default EntityReads.UnknownLevel sentinel.
                var rig = Rig.Build(Cond14("ON_TURN_START", LvlGte1), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "no level readable = the gate stays shut");
            });

            t.Case("SELF_LEVEL: an UNKNOWN level is false for LT/NE too, which naive arithmetic would pass", () =>
            {
                var lt = Rig.Build(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"LT\",\"Value\":5}"), 5);
                Check.PlanCount(lt.Fire(TriggerKind.ON_TURN_START), 0, "int.MinValue < 5 must NOT open the gate");

                var ne = Rig.Build(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"NE\",\"Value\":5}"), 5);
                Check.PlanCount(ne.Fire(TriggerKind.ON_TURN_START), 0, "int.MinValue != 5 must NOT open the gate");
            });

            t.Case("SELF_LEVEL: Negate cannot turn an UNKNOWN level into a pass", () =>
            {
                var unknown = Rig.Build(Cond14("ON_TURN_START", Negated(LvlGte3)), 5);
                Check.PlanCount(unknown.Fire(TriggerKind.ON_TURN_START), 0, "unknown short-circuits BEFORE Negate");

                // Negate still behaves normally once the level IS readable.
                var known = Rig.Build(Cond14("ON_TURN_START", Negated(LvlGte3)), 5);
                known.Hero.AtLevel(1);
                Check.PlanCount(known.Fire(TriggerKind.ON_TURN_START), 1, "Negate inverts a genuine false");
            });

            t.Case("SELF_LEVEL: a throwing level read fails safe to false", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", LvlGte1), 5);
                rig.Hero.AtLevel(9);
                rig.Hero.ThrowOnGetStat = true;   // FakeEntity.Level throws on the same switch
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "a throwing read never opens the gate");
            });

            t.Case("SELF_LEVEL: draws no RNG", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", LvlGte1), 5);
                rig.Hero.AtLevel(4);
                rig.Fire(TriggerKind.ON_TURN_START);
                Check.Eq(0, rig.Rng.Draws, "pure read of replicated state");
            });

            t.Case("SELF_LEVEL: a well-formed condition validates clean", () =>
            {
                Check.NoErrors(RecipeParser.Parse(Cond14("ON_TURN_START", LvlGte3)), "canonical SELF_LEVEL");
            });

            t.Case("SELF_LEVEL: a missing Value is E_VALUE_MISSING", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"GTE\"}")), "E_VALUE_MISSING", "no Value");
            });

            t.Case("SELF_LEVEL: a non-numeric Value is E_VALUE_MISSING", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"GTE\",\"Value\":\"three\"}")),
                    "E_VALUE_MISSING", "string Value");
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"GTE\",\"Value\":true}")),
                    "E_VALUE_MISSING", "boolean Value");
            });

            t.Case("SELF_LEVEL: a missing Comparator is E_CMP_MISSING", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Value\":3}")), "E_CMP_MISSING", "no Comparator");
            });

            t.Case("SELF_LEVEL: a malformed Comparator is E_CMP_UNKNOWN", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"MORE_THAN\",\"Value\":3}")),
                    "E_CMP_UNKNOWN", "bogus comparator token");
            });

            t.Case("SELF_LEVEL: requires SchemaVersion 1.4", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_TURN_START", LvlGte3)),
                    "E_SCHEMA_GATE", "SELF_LEVEL under SchemaVersion 1.1");
            });

            // ================================================================================
            t.Section("HAS_ITEM (v1.4) - inventory possession, not the triggering ability's item");

            const string Ball = "ARM_ORIG_TRAINER_BALL_WATER";
            const string HasBall = "{\"Type\":\"HAS_ITEM\",\"Value\":\"ARM_ORIG_TRAINER_BALL_WATER\"}";

            t.Case("HAS_ITEM: present in the inventory passes", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                rig.Hero.Carrying(Ball);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "the owner carries the charm");
            });

            t.Case("HAS_ITEM: a readable inventory without the item is a normal false", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                rig.Hero.Carrying("ARM_ORIG_TRAINER_BALL_FIRE");
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "a different charm is not this charm");

                var empty = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                empty.Hero.WithEmptyInventory();
                Check.PlanCount(empty.Fire(TriggerKind.ON_TURN_START), 0, "an empty inventory holds nothing");
            });

            t.Case("HAS_ITEM: Negate inverts a genuine absence", () =>
            {
                var absent = Rig.Build(Cond14("ON_TURN_START", Negated(HasBall)), 5);
                absent.Hero.WithEmptyInventory();
                Check.PlanCount(absent.Fire(TriggerKind.ON_TURN_START), 1, "readable-and-absent IS negatable");

                var present = Rig.Build(Cond14("ON_TURN_START", Negated(HasBall)), 5);
                present.Hero.Carrying(Ball);
                Check.PlanCount(present.Fire(TriggerKind.ON_TURN_START), 0, "Negate inverts the true case");
            });

            t.Case("HAS_ITEM: an UNREADABLE inventory is false", () =>
            {
                // Hero keeps FakeEntity's default null ItemList: HasItem returns its unknown.
                var rig = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "no inventory readable = the gate stays shut");
            });

            t.Case("HAS_ITEM: Negate cannot turn an UNREADABLE inventory into a pass", () =>
            {
                // THE crux: unknown and readable-absent are both "false", but only one is negatable.
                var unknown = Rig.Build(Cond14("ON_TURN_START", Negated(HasBall)), 5);
                Check.PlanCount(unknown.Fire(TriggerKind.ON_TURN_START), 0, "unknown short-circuits BEFORE Negate");
            });

            t.Case("HAS_ITEM: a throwing inventory read fails safe to false, Negate included", () =>
            {
                var plain = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                plain.Hero.Carrying(Ball);
                plain.Hero.ThrowOnGetStat = true;   // FakeEntity.HasItem throws on the same switch
                Check.PlanCount(plain.Fire(TriggerKind.ON_TURN_START), 0, "a throwing read never opens the gate");

                var neg = Rig.Build(Cond14("ON_TURN_START", Negated(HasBall)), 5);
                neg.Hero.Carrying(Ball);
                neg.Hero.ThrowOnGetStat = true;
                Check.PlanCount(neg.Fire(TriggerKind.ON_TURN_START), 0, "a throw is unknown, and unknown is not negatable");
            });

            t.Case("HAS_ITEM: the Thing name is matched case-SENSITIVELY", () =>
            {
                // Thing ids are exact keys into Env.Configs.Things (Thing.cs:40) — deliberately NOT the
                // OrdinalIgnoreCase substring match CONFIG_NAME_CONTAINS uses.
                var lower = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                lower.Hero.Carrying("arm_orig_trainer_ball_water");
                Check.PlanCount(lower.Fire(TriggerKind.ON_TURN_START), 0, "lower-cased id is not the Thing id");

                var exact = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                exact.Hero.Carrying(Ball);
                Check.PlanCount(exact.Fire(TriggerKind.ON_TURN_START), 1, "the exact id matches");
            });

            t.Case("HAS_ITEM: a substring of a carried Thing id does NOT match", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START",
                    "{\"Type\":\"HAS_ITEM\",\"Value\":\"TRAINER_BALL\"}"), 5);
                rig.Hero.Carrying(Ball);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "whole-id equality, never Contains");
            });

            t.Case("HAS_ITEM: reads the OWNER's inventory, not an ally's", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                rig.Hero.WithEmptyInventory();
                rig.Ally.Carrying(Ball);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "the ally's charm is not the owner's");
            });

            t.Case("HAS_ITEM: is answerable under ON_COMBAT_START (unlike ITEM_CLASS, which needs a pThing)", () =>
            {
                var rig = Rig.Build(Cond14("ON_COMBAT_START", HasBall), 5);
                rig.Hero.Carrying(Ball);
                Check.PlanCount(rig.Fire(TriggerKind.ON_COMBAT_START), 1, "no triggering item is needed");
            });

            t.Case("HAS_ITEM: draws no RNG", () =>
            {
                var rig = Rig.Build(Cond14("ON_TURN_START", HasBall), 5);
                rig.Hero.Carrying(Ball);
                rig.Fire(TriggerKind.ON_TURN_START);
                Check.Eq(0, rig.Rng.Draws, "pure read of replicated state");
            });

            t.Case("HAS_ITEM: a well-formed condition validates clean", () =>
            {
                Check.NoErrors(RecipeParser.Parse(Cond14("ON_TURN_START", HasBall)), "canonical HAS_ITEM");
            });

            t.Case("HAS_ITEM: a missing Value is E_VALUE_MISSING", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"HAS_ITEM\"}")), "E_VALUE_MISSING", "no Value");
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"HAS_ITEM\",\"Value\":\"\"}")), "E_VALUE_MISSING", "empty-string Value");
            });

            t.Case("HAS_ITEM: a non-string Value is E_VALUE_MISSING", () =>
            {
                // The parser stringifies numbers and bools into Value, so these must be rejected on the
                // ValueInt/ValueBool witnesses, not on Value being empty.
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"HAS_ITEM\",\"Value\":7}")), "E_VALUE_MISSING", "numeric Value");
                Check.HasFinding(RecipeParser.Parse(Cond14("ON_TURN_START",
                    "{\"Type\":\"HAS_ITEM\",\"Value\":true}")), "E_VALUE_MISSING", "boolean Value");
            });

            t.Case("HAS_ITEM: requires SchemaVersion 1.4", () =>
            {
                Check.HasFinding(RecipeParser.Parse(J.Cond("ON_TURN_START", HasBall)),
                    "E_SCHEMA_GATE", "HAS_ITEM under SchemaVersion 1.1");
            });
        }
    }
}
