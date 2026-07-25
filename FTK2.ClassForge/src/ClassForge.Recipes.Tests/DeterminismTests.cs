using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    public static class DeterminismTests
    {
        /// <summary>A book that exercises multi-owner dispatch, chance rolls, element picks, budgets,
        /// counters and multi-target effects at once — i.e. every draw source in v1.1.</summary>
        private static string Book()
        {
            return
            "{\"SKILL_D_ELEMENT\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_CRIT\",\"Priority\":0," +
            "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\"," +
            "\"StatusOneOf\":[\"STATUS_FIRE_00\",\"STATUS_ICE_00\",\"STATUS_SHOCK_00\"],\"Duration\":1}]," +
            "\"ProcChance\":40,\"AiProcChance\":40}," +

            "\"SKILL_A_REACT\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ENEMY_ABILITY_RESOLVED\",\"Priority\":0," +
            "\"Conditions\":[{\"Type\":\"ROLL_TIER\",\"Comparator\":\"GTE\",\"Value\":\"SUCCESS\"}]," +
            "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_ALL\",\"Status\":\"STATUS_EVADEUP_00\",\"Duration\":1}]," +
            "\"ProcChance\":50,\"AiProcChance\":50,\"Budget\":{\"Scope\":\"ONCE_PER_ROUND\",\"ConsumeOn\":\"PROC\"}}," +

            "\"SKILL_B_STACK\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGE_TAKEN\",\"Priority\":1," +
            "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"RAGE\",\"Delta\":1,\"Max\":3}]," +
            "\"ProcChance\":100,\"AiProcChance\":100}," +

            "\"SKILL_C_SPEND\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ABILITY_DECLARED\",\"Priority\":2," +
            "\"Conditions\":[{\"Type\":\"COUNTER\",\"Name\":\"RAGE\",\"Comparator\":\"GTE\",\"Value\":1}]," +
            "\"Effects\":[{\"Type\":\"ROLL_STAT_BONUS\",\"Stat\":\"ATK\",\"PercentFrom\":\"COUNTER:RAGE\",\"PerUnit\":10,\"Max\":30}," +
            "{\"Type\":\"COUNTER_SET\",\"Name\":\"RAGE\",\"Value\":0}]," +
            "\"ProcChance\":100,\"AiProcChance\":100}," +

            "\"SKILL_E_KILL\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0," +
            "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"STATUS_ATTACKUP_00\"}]," +
            "\"ProcChance\":70,\"AiProcChance\":70,\"Cooldown\":1}}";
        }

        /// <summary>A fixed, non-trivial event script replayed identically by both peers.</summary>
        private static string RunStream(Rig rig)
        {
            var all = new List<EngineAction>();
            for (int round = 1; round <= 3; round++)
            {
                rig.Ctx.RoundValue = round;
                all.AddRange(rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT }));

                all.AddRange(rig.Dispatcher.OnDamageTaken(rig.Ctx, new DamageTakenEvent
                { Attacker = rig.Foe, Victim = rig.Hero, AbilityId = "AB_SLASH", Amount = 6 }));

                all.AddRange(rig.Dispatcher.OnAbilityDeclared(rig.Ctx, new AbilityDeclaredEvent
                { Origin = rig.Hero, Target = rig.Foe, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT, FocusUsed = 1 }));

                all.AddRange(rig.Dispatcher.OnCrit(rig.Ctx, new CritEvent
                { Origin = rig.Hero, Target = rig.Foe, AbilityId = "AB_SLASH", Stat = "HP" }));

                all.AddRange(rig.Dispatcher.OnKill(rig.Ctx, new KillEvent
                { Origin = rig.Hero, Target = rig.Foe2, AbilityId = "AB_SLASH", HpBefore = 4, HpAfter = 0 }));

                rig.Dispatcher.ObserveMove(rig.Ctx, rig.Hero);

                all.AddRange(rig.Dispatcher.OnAbilityUsed(rig.Ctx, new AbilityUsedEvent
                { Origin = rig.Hero, Target = rig.Foe, AbilityId = "AB_SLASH", RollTier = RollTier.SUCCESS, FocusUsed = 1 }));
            }
            return ActionLog.Render(all);
        }

        public static void Run(TestRunner t)
        {
            t.Section("determinism");

            t.Case("DETERMINISM PAIR TEST: two dispatchers, identical event stream + same-seeded RNG -> identical action logs", () =>
            {
                var peerA = Rig.Build(Book(), 20260725);
                peerA.GrantAlso(peerA.Ally);
                var peerB = Rig.Build(Book(), 20260725);
                peerB.GrantAlso(peerB.Ally);

                string logA = RunStream(peerA);
                string logB = RunStream(peerB);

                Check.True(logA.Length > 0, "the stream actually produced actions");
                Check.Eq(logA, logB, "peer A and peer B produced identical plans");
                Check.Eq(peerA.Rng.Draws, peerB.Rng.Draws, "identical shared-stream draw counts");
                Check.True(peerA.Rng.Draws > 0, "the stream actually consumed the shared RNG");
            });

            t.Case("determinism: shuffled entity enumeration order produces the identical plan", () =>
            {
                // each rig is single-use: RunStream mutates the per-battle runtime
                var normal = Rig.Build(Book(), 4242);
                normal.GrantAlso(normal.Ally);
                string baseline = RunStream(normal);

                var shuffled = Rig.Build(Book(), 4242);
                shuffled.GrantAlso(shuffled.Ally);
                shuffled.Ctx.EntityList.Reverse();
                Check.Eq(baseline, RunStream(shuffled), "entity order must not matter (§5.2 invariant 4)");

                // and a second, differently-permuted ordering
                var rotated = Rig.Build(Book(), 4242);
                rotated.GrantAlso(rotated.Ally);
                var first = rotated.Ctx.EntityList[0];
                rotated.Ctx.EntityList.RemoveAt(0);
                rotated.Ctx.EntityList.Add(first);
                Check.Eq(baseline, RunStream(rotated), "rotation must not matter either");
            });

            t.Case("determinism: source order of recipe ids in the JSON does not matter", () =>
            {
                string forward =
                    "{\"SKILL_A\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_A\"}],\"ProcChance\":100}," +
                    "\"SKILL_B\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_B\"}],\"ProcChance\":100}}";
                string reversed =
                    "{\"SKILL_B\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_B\"}],\"ProcChance\":100}," +
                    "\"SKILL_A\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_A\"}],\"ProcChance\":100}}";
                var f = Rig.Build(forward, 7);
                var r = Rig.Build(reversed, 7);
                Check.Eq(ActionLog.Render(f.Fire(TriggerKind.ON_KILL)), ActionLog.Render(r.Fire(TriggerKind.ON_KILL)),
                    "recipe file order is irrelevant");
            });

            t.Case("determinism: two dispatchers in one process share no state", () =>
            {
                var a = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Effects\":[" + J.SelfStatusEffect + "]," +
                    "\"ProcChance\":100,\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\"}}}", 5);
                var b = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Effects\":[" + J.SelfStatusEffect + "]," +
                    "\"ProcChance\":100,\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\"}}}", 5);
                Check.PlanCount(a.Fire(TriggerKind.ON_KILL), 1, "A fires");
                Check.PlanCount(b.Fire(TriggerKind.ON_KILL), 1, "B fires — A's budget did not leak");
                Check.PlanCount(a.Fire(TriggerKind.ON_KILL), 0, "A is spent");
                Check.PlanCount(b.Fire(TriggerKind.ON_KILL), 0, "B is spent");
            });

            t.Case("determinism: draw count is a pure function of eligibility, not of outcome", () =>
            {
                // Same recipe, same events, two RNGs whose scripted outcomes differ: the draw COUNT
                // must still match, because the roll is unconditional once an evaluation is eligible.
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"ProcChance\":50," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}";
                var yes = Rig.Build(json, 1);
                yes.Rng.ScriptChance(true, true, true, true);
                var no = Rig.Build(json, 1);
                no.Rng.ScriptChance(false, false, false, false);
                for (int i = 0; i < 4; i++) { yes.Fire(TriggerKind.ON_KILL); no.Fire(TriggerKind.ON_KILL); }
                Check.Eq(yes.Rng.Draws, no.Rng.Draws, "same number of draws regardless of outcome");
                Check.Eq(4, yes.Rng.Draws, "one per eligible evaluation");
            });

            t.Case("determinism: multi-owner T7 draw order follows ascending ordinal Guid", () =>
            {
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ENEMY_ABILITY_RESOLVED\",\"ProcChance\":50," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S\"}]}}";
                var rig = Rig.Build(json, 1);
                rig.GrantAlso(rig.Ally);
                rig.Ctx.EntityList.Reverse();
                rig.Rng.ScriptChance(true, false);   // first draw -> A_HERO, second -> B_ALLY
                var p = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(p, 1, "only the first owner's roll succeeded");
                Check.Eq("A_HERO", p[0].OwnerGuid, "the first draw went to the lowest ordinal Guid");
            });

            t.Case("determinism: a new CombatKey drops budgets, cooldowns, counters and turn state together", () =>
            {
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"ProcChance\":100,\"Cooldown\":5," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}]," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\"}}}";
                var rig = Rig.Build(json, 5);
                Check.Eq(1, ((CounterAddAction)rig.Fire(TriggerKind.ON_KILL)[0]).NewValue, "counter starts at 1");
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "budget + cooldown block the second");
                rig.Ctx.Identity = "COMBAT_NEXT";
                var p = rig.Fire(TriggerKind.ON_KILL);
                Check.PlanCount(p, 1, "new combat, fresh runtime");
                Check.Eq(1, ((CounterAddAction)p[0]).NewValue, "counter reset to 0 before the add");
            });
        }
    }
}
