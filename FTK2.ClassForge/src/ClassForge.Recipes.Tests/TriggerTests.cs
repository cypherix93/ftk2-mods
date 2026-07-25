using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    public static class TriggerTests
    {
        private static readonly string[] AllTriggerTokens =
        {
            "ON_ABILITY_USED", "ON_CRIT", "ON_KILL", "ON_HEAL", "ON_DAMAGED", "ON_TURN_START", "ON_TURN_END",
            "ON_COMBAT_START", "ON_ABILITY_DECLARED", "ON_DAMAGE_DEALT", "ON_DAMAGE_TAKEN",
            "ON_STATUS_APPLIED", "ON_CONSUMABLE_USED", "ON_ENEMY_ABILITY_RESOLVED", "ON_HEAL_PENDING"
        };

        public static void Run(TestRunner t)
        {
            t.Section("triggers (15 tokens)");

            for (int i = 0; i < AllTriggerTokens.Length; i++)
            {
                string token = AllTriggerTokens[i];
                t.Case("trigger " + token + " routes to its own event and nothing else", () =>
                {
                    // ON_HEAL_PENDING only accepts HEAL_MODIFIER-shaped work, but COUNTER_ADD is legal on
                    // every trigger and proves routing without dragging effect semantics in.
                    var rig = Rig.Build(J.Trigger(token), 11);
                    var recipe = rig.Set.Find("SKILL_T");
                    Check.True(recipe != null && recipe.IsLive, token + " recipe is live");

                    var plan = rig.Fire(recipe.Trigger);
                    Check.PlanCount(plan, 1, token + " fires on its own event");

                    // every other trigger event must leave it alone
                    for (int k = 0; k < AllTriggerTokens.Length; k++)
                    {
                        TriggerKind other;
                        ClassForge.Recipes.Model.Vocabulary.TryParseTrigger(AllTriggerTokens[k], out other);
                        if (other == TriggerKind.ON_DAMAGED) other = TriggerKind.ON_DAMAGE_TAKEN;
                        if (other == recipe.Trigger) continue;
                        var none = rig.Fire(other);
                        Check.PlanCount(none, 0, token + " must not fire on " + AllTriggerTokens[k]);
                    }
                });
            }

            t.Case("trigger: ON_DAMAGED is dispatched as ON_DAMAGE_TAKEN", () =>
            {
                var rig = Rig.Build(J.Trigger("ON_DAMAGED"), 3);
                Check.PlanCount(rig.Fire(TriggerKind.ON_DAMAGE_TAKEN), 1, "alias fires");
            });

            t.Case("trigger: recipes only evaluate for owners that hold the skill", () =>
            {
                var rig = Rig.Build(J.Trigger("ON_KILL"), 3);
                rig.Hero.PassiveList.Clear();
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "no passive, no fire");
                rig.Hero.PassiveList.Add("SKILL_T");
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "passive granted, fires");
            });

            t.Case("trigger: ON_DAMAGE_TAKEN binds owner=victim and TRIGGER_SOURCE=attacker", () =>
            {
                var rig = Rig.Build(J.Effect("ON_DAMAGE_TAKEN",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_SOURCE\",\"Status\":\"STATUS_DAZE_00\"}"), 3);
                var plan = rig.Fire(TriggerKind.ON_DAMAGE_TAKEN);
                Check.PlanCount(plan, 1, "one action");
                var a = (AddStatusAction)plan[0];
                Check.Eq("A_HERO", a.OwnerGuid, "owner is the victim");
                Check.Eq("C_FOE", a.TargetGuid, "TRIGGER_SOURCE is the attacker");
            });

            t.Case("trigger: ON_STATUS_APPLIED binds owner=target and TRIGGER_STATUS", () =>
            {
                var rig = Rig.Build(J.Effect("ON_STATUS_APPLIED",
                    "{\"Type\":\"REMOVE_STATUS\",\"Target\":\"SELF\",\"Status\":\"TRIGGER_STATUS\"}"), 3);
                var plan = rig.Fire(TriggerKind.ON_STATUS_APPLIED);
                var a = (RemoveStatusAction)plan[0];
                Check.Eq("A_HERO", a.TargetGuid, "self");
                Check.Eq("STATUS_CURSE_00", a.StatusId, "TRIGGER_STATUS resolves to pStatusConfigName");
            });

            t.Case("trigger: ON_ENEMY_ABILITY_RESOLVED evaluates every opposed holder, Guid-ascending", () =>
            {
                var rig = Rig.Build(J.Effect("ON_ENEMY_ABILITY_RESOLVED", J.SelfStatusEffect), 3);
                rig.GrantAlso(rig.Ally);
                var plan = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(plan, 2, "both opposed holders evaluate");
                Check.Eq("A_HERO", plan[0].OwnerGuid, "A_HERO first (ordinal Guid)");
                Check.Eq("B_ALLY", plan[1].OwnerGuid, "B_ALLY second");
            });

            t.Case("trigger: ON_ENEMY_ABILITY_RESOLVED skips dead and same-side entities", () =>
            {
                var rig = Rig.Build(J.Effect("ON_ENEMY_ABILITY_RESOLVED", J.SelfStatusEffect), 3);
                rig.GrantAlso(rig.Ally);
                rig.GrantAlso(rig.Foe2);
                rig.Ally.Alive = false;
                var plan = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(plan, 1, "only the living opposed holder");
                Check.Eq("A_HERO", plan[0].OwnerGuid, "hero only");
            });

            t.Case("trigger: ON_ABILITY_USED records ActedRound/LastAbilityId AFTER dispatch", () =>
            {
                var rig = Rig.Build(J.Cond("ON_ABILITY_USED", "{\"Type\":\"ABILITY_REPEATED\",\"Value\":true}"), 3);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.SUCCESS), 0,
                    "first use is not a repeat");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.SUCCESS), 1,
                    "second identical use is a repeat");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SHOOT", RollTier.SUCCESS), 0,
                    "different ability is not a repeat");
            });

            t.Case("trigger: ObserveMove feeds MOVED_THIS_ROUND", () =>
            {
                var rig = Rig.Build(J.Cond("ON_ABILITY_USED", "{\"Type\":\"MOVED_THIS_ROUND\",\"Value\":true}"), 3);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 0, "has not moved");
                rig.Dispatcher.ObserveMove(rig.Ctx, rig.Hero);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 1, "moved this round");
                rig.Ctx.RoundValue = 2;
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 0, "stale in the next round");
            });

            t.Case("trigger: Enabled=false is a hard kill switch", () =>
            {
                var rig = Rig.Build(J.One("ON_KILL", "", J.SelfStatusEffect, ",\"Enabled\":false"), 3);
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "disabled recipe never fires");
            });

            t.Case("trigger: a validator-disabled recipe never fires", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"NOWHERE\",\"Status\":\"S\"}"), 3);
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 0, "disabled by finding");
            });

            t.Case("trigger: a null RNG source blocks every recipe (no ad-hoc fallback stream)", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL", J.SelfStatusEffect), 3);
                var d = new RecipeDispatcher(rig.Set, null, rig.Log);
                var plan = d.OnKill(rig.Ctx, new KillEvent { Origin = rig.Hero, Target = rig.Foe, HpBefore = 3, HpAfter = 0 });
                Check.PlanCount(plan, 0, "no fire without CombatState.Random");
                bool logged = false;
                for (int i = 0; i < rig.Log.Lines.Count; i++)
                    if (rig.Log.Lines[i].IndexOf("CombatState.Random", StringComparison.Ordinal) >= 0) logged = true;
                Check.True(logged, "one-time skip is logged");
            });

            t.Case("trigger: planned actions are also pushed to the context emitter", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL", J.SelfStatusEffect), 3);
                var plan = rig.Fire(TriggerKind.ON_KILL);
                Check.Eq(plan.Count, rig.Ctx.Emitted.Count, "emitter received the plan");
                Check.Eq(plan[0].Describe(), rig.Ctx.Emitted[0].Describe(), "same action");
            });
        }
    }
}
