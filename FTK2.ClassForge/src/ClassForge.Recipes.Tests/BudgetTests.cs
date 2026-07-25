using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    public static class BudgetTests
    {
        private static string Budgeted(string scope, string consumeOn, int chance)
        {
            return "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGE_DEALT\"," +
                   "\"Effects\":[" + J.SelfStatusEffect + "]," +
                   "\"ProcChance\":" + chance + ",\"AiProcChance\":" + chance + "," +
                   "\"Budget\":{\"Scope\":\"" + scope + "\",\"ConsumeOn\":\"" + consumeOn + "\"}}}";
        }

        private static IReadOnlyList<EngineAction> Hit(Rig rig, FakeEntity target)
        {
            return rig.Dispatcher.OnDamageDealt(rig.Ctx, new DamageDealtEvent
            { Origin = rig.Hero, Target = target, AbilityId = "AB_SLASH", Amount = 5, HpBefore = 20, HpAfter = 15 });
        }

        public static void Run(TestRunner t)
        {
            t.Section("budgets (5 scopes x 3 ConsumeOn modes), cooldowns, priority, proc rolls");

            t.Case("budget NONE: every qualifying event fires", () =>
            {
                var rig = Rig.Build(Budgeted("NONE", "PROC", 100), 5);
                Check.PlanCount(Hit(rig, rig.Foe), 1, "first");
                Check.PlanCount(Hit(rig, rig.Foe), 1, "second");
                Check.PlanCount(Hit(rig, rig.Foe2), 1, "third, other target");
            });

            t.Case("budget ONCE_PER_ROUND: once per round, refreshed by the round counter", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_ROUND", "PROC", 100), 5);
                Check.PlanCount(Hit(rig, rig.Foe), 1, "round 1 first");
                Check.PlanCount(Hit(rig, rig.Foe), 0, "round 1 second is blocked");
                Check.PlanCount(Hit(rig, rig.Foe2), 0, "round 1, different target, still blocked");
                rig.Ctx.RoundValue = 2;
                Check.PlanCount(Hit(rig, rig.Foe), 1, "round 2 refreshes");
            });

            t.Case("budget ONCE_PER_COMBAT: once for the whole combat, across rounds", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_COMBAT", "PROC", 100), 5);
                Check.PlanCount(Hit(rig, rig.Foe), 1, "first");
                rig.Ctx.RoundValue = 5;
                Check.PlanCount(Hit(rig, rig.Foe), 0, "later round still blocked");
            });

            t.Case("budget ONCE_PER_COMBAT: a new CombatKey drops the runtime (the EOR SteadyAim bug class)", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_COMBAT", "PROC", 100), 5);
                Check.PlanCount(Hit(rig, rig.Foe), 1, "combat 1 first");
                Check.PlanCount(Hit(rig, rig.Foe), 0, "combat 1 second blocked");
                rig.Ctx.Identity = "COMBAT_2";
                rig.Ctx.RoundValue = 1;
                Check.PlanCount(Hit(rig, rig.Foe), 1, "combat 2 gets a fresh budget with no explicit reset call");
            });

            t.Case("budget ONCE_PER_TARGET_PER_ROUND: independent per target, refreshed each round", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_TARGET_PER_ROUND", "PROC", 100), 5);
                Check.PlanCount(Hit(rig, rig.Foe), 1, "foe A first");
                Check.PlanCount(Hit(rig, rig.Foe), 0, "foe A second blocked");
                Check.PlanCount(Hit(rig, rig.Foe2), 1, "foe B has its own slot");
                rig.Ctx.RoundValue = 2;
                Check.PlanCount(Hit(rig, rig.Foe), 1, "new round refreshes per-target slots");
            });

            t.Case("budget ONCE_PER_TARGET_PER_COMBAT: independent per target, never refreshed", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_TARGET_PER_COMBAT", "PROC", 100), 5);
                Check.PlanCount(Hit(rig, rig.Foe), 1, "foe A first");
                Check.PlanCount(Hit(rig, rig.Foe2), 1, "foe B first");
                rig.Ctx.RoundValue = 4;
                Check.PlanCount(Hit(rig, rig.Foe), 0, "foe A still blocked in a later round");
                Check.PlanCount(Hit(rig, rig.Foe2), 0, "foe B still blocked");
            });

            t.Case("ConsumeOn PROC: a failed roll leaves the budget open (real SENTINEL behavior)", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_ROUND", "PROC", 25), 5);
                rig.Rng.ScriptChance(false, true, false);
                Check.PlanCount(Hit(rig, rig.Foe), 0, "roll 1 fails");
                Check.PlanCount(Hit(rig, rig.Foe), 1, "roll 2 succeeds -> budget consumed");
                Check.PlanCount(Hit(rig, rig.Foe), 0, "roll 3 never happens, budget is spent");
                Check.Eq(2, rig.Rng.Draws, "exactly two draws: no draw once the budget is spent");
            });

            t.Case("ConsumeOn EVALUATION: consumed as soon as conditions pass, roll or no roll", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_ROUND", "EVALUATION", 25), 5);
                rig.Rng.ScriptChance(false, true);
                Check.PlanCount(Hit(rig, rig.Foe), 0, "roll fails but budget is already spent");
                Check.PlanCount(Hit(rig, rig.Foe), 0, "no second chance this round");
                Check.Eq(1, rig.Rng.Draws, "only one draw");
            });

            t.Case("ConsumeOn EFFECT_APPLIED: unspent when no effect resolved (KNIGHT)", () =>
            {
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ABILITY_USED\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_BY_RANK\",\"Status\":\"STATUS_PROTECT_00\"," +
                    "\"Rank\":{\"Stat\":\"HP_PCT\",\"Order\":\"LOWEST\",\"ExcludeSelf\":true," +
                    "\"Where\":[{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"LTE\",\"Percent\":25}]}}]," +
                    "\"ProcChance\":100,\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\",\"ConsumeOn\":\"EFFECT_APPLIED\"}}}";
                var rig = Rig.Build(json, 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 0, "no wounded ally: nothing applied");
                rig.Ally.With("HP", 10);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 1, "budget was NOT spent by the empty pass");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 0, "now it is spent");
            });

            t.Case("Budget.Key: a recipe pair shares one budget namespace", () =>
            {
                string json =
                    "{\"SKILL_A_HALF\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_CRIT\",\"Priority\":0," +
                    "\"Effects\":[" + J.SelfStatusEffect + "],\"ProcChance\":100," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_ROUND\",\"Key\":\"CF_SHOWMANSHIP\"}}," +
                    "\"SKILL_B_HALF\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0," +
                    "\"Effects\":[" + J.SelfStatusEffect + "],\"ProcChance\":100," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_ROUND\",\"Key\":\"CF_SHOWMANSHIP\"}}}";
                var rig = Rig.Build(json, 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_CRIT), 1, "crit half fires");
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "kill half shares the spent budget");
                rig.Ctx.RoundValue = 2;
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "new round, kill half fires");
                Check.PlanCount(rig.Fire(TriggerKind.ON_CRIT), 0, "crit half now blocked");
            });

            t.Case("Budget: without a Key, two recipes have independent budgets", () =>
            {
                string json =
                    "{\"SKILL_A_HALF\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_CRIT\"," +
                    "\"Effects\":[" + J.SelfStatusEffect + "],\"ProcChance\":100,\"Budget\":{\"Scope\":\"ONCE_PER_ROUND\"}}," +
                    "\"SKILL_B_HALF\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"Effects\":[" + J.SelfStatusEffect + "],\"ProcChance\":100,\"Budget\":{\"Scope\":\"ONCE_PER_ROUND\"}}}";
                var rig = Rig.Build(json, 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_CRIT), 1, "crit half");
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "kill half unaffected");
            });

            t.Case("budgets are per owner, not global", () =>
            {
                var rig = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ENEMY_ABILITY_RESOLVED\"," +
                    "\"Effects\":[" + J.SelfStatusEffect + "],\"ProcChance\":100," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\"}}}", 5);
                rig.GrantAlso(rig.Ally);
                var p = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(p, 2, "both owners spend their own budget");
                var q = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(q, 0, "both are now spent");
            });

            t.Section("cooldowns / priority / proc rolls");

            t.Case("cooldown: blocks for N rounds after a proc, then clears", () =>
            {
                var rig = Rig.Build(J.One("ON_KILL", "", J.SelfStatusEffect, ",\"Cooldown\":2"), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "round 1 procs, cooldown expires at round 3");
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "still round 1");
                rig.Ctx.RoundValue = 2;
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "round 2 still on cooldown");
                rig.Ctx.RoundValue = 3;
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "round 3 is ready");
            });

            t.Case("cooldown: 0 means no cooldown", () =>
            {
                var rig = Rig.Build(J.One("ON_KILL", "", J.SelfStatusEffect, ",\"Cooldown\":0"), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "1");
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "2");
            });

            t.Case("cooldown: is per owner", () =>
            {
                var rig = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ENEMY_ABILITY_RESOLVED\"," +
                    "\"Effects\":[" + J.SelfStatusEffect + "],\"ProcChance\":100,\"Cooldown\":3}}", 5);
                rig.GrantAlso(rig.Ally);
                var p = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(p, 2, "both owners proc independently");
            });

            t.Case("priority: recipes proc in ascending Priority, then ordinal id", () =>
            {
                string json =
                    "{\"SKILL_Z\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":-1," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_Z\"}],\"ProcChance\":100}," +
                    "\"SKILL_A\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_A\"}],\"ProcChance\":100}," +
                    "\"SKILL_B\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_B\"}],\"ProcChance\":100}}";
                var rig = Rig.Build(json, 5);
                var p = rig.Fire(TriggerKind.ON_KILL);
                Check.PlanCount(p, 3, "all three proc");
                Check.Eq("S_Z", ((AddStatusAction)p[0]).StatusId, "priority -1 first");
                Check.Eq("S_A", ((AddStatusAction)p[1]).StatusId, "then id A");
                Check.Eq("S_B", ((AddStatusAction)p[2]).StatusId, "then id B");
            });

            t.Case("ProcChance 100 takes zero draws; anything else takes exactly one per eligible evaluation", () =>
            {
                var full = Rig.Build(J.One("ON_KILL", "", J.SelfStatusEffect, ""), 5);
                full.Fire(TriggerKind.ON_KILL);
                full.Fire(TriggerKind.ON_KILL);
                Check.Eq(0, full.Rng.Draws, "100 -> zero draws");

                var partial = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"ProcChance\":50,\"AiProcChance\":50," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}", 5);
                partial.Fire(TriggerKind.ON_KILL);
                partial.Fire(TriggerKind.ON_KILL);
                partial.Fire(TriggerKind.ON_KILL);
                Check.Eq(3, partial.Rng.Draws, "one draw per eligible evaluation");
            });

            t.Case("ProcChance 0 never fires", () =>
            {
                var rig = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"ProcChance\":0,\"AiProcChance\":0," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}", 5);
                for (int i = 0; i < 20; i++) Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "never fires");
            });

            t.Case("failed conditions take no draw at all (the roll is last)", () =>
            {
                var rig = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"ProcChance\":50," +
                    "\"Conditions\":[{\"Type\":\"HAS_STATUS\",\"Value\":\"STATUS_BLEED_00\"}]," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}", 5);
                rig.Fire(TriggerKind.ON_KILL);
                Check.Eq(0, rig.Rng.Draws, "conditions failed before the roll");
            });

            t.Case("AiProcChance is used for AI-controlled owners", () =>
            {
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"ProcChance\":100,\"AiProcChance\":0," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}";
                var human = Rig.Build(json, 5);
                Check.PlanCount(human.Fire(TriggerKind.ON_KILL), 1, "human uses ProcChance 100");
                Check.Eq(0, human.Rng.Draws, "and takes no draw");

                var ai = Rig.Build(json, 5);
                ai.Hero.Ai = true;
                Check.PlanCount(ai.Fire(TriggerKind.ON_KILL), 0, "AI uses AiProcChance 0");
                Check.Eq(1, ai.Rng.Draws, "AI path takes its draw");
            });

            t.Case("ResetCombat / ResetRun clear the per-battle table explicitly", () =>
            {
                var rig = Rig.Build(Budgeted("ONCE_PER_COMBAT", "PROC", 100), 5);
                Check.PlanCount(Hit(rig, rig.Foe), 1, "first");
                Check.PlanCount(Hit(rig, rig.Foe), 0, "blocked");
                rig.Dispatcher.ResetCombat();
                Check.PlanCount(Hit(rig, rig.Foe), 1, "ResetCombat re-opened it");
                rig.Dispatcher.ResetRun();
                Check.PlanCount(Hit(rig, rig.Foe), 1, "ResetRun re-opened it");
            });
        }
    }
}
