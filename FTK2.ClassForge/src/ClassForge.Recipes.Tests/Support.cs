using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>A ready-made 2v2 combat plus the dispatcher under test.</summary>
    public sealed class Rig
    {
        public FakeContext Ctx;
        public FakeEntity Hero;
        public FakeEntity Ally;
        public FakeEntity Foe;
        public FakeEntity Foe2;
        public FakeRandom Rng;
        public RecipeSet Set;
        public RecipeDispatcher Dispatcher;
        public FakeLog Log = new FakeLog();

        public static Rig Build(string json, int seed)
        {
            return FromSet(RecipeParser.Parse(json), seed);
        }

        public static Rig FromSet(RecipeSet set, int seed)
        {
            var rig = new Rig();
            rig.Ctx = Scenario.Standard();
            rig.Hero = new FakeEntity("A_HERO", 0);
            rig.Ally = new FakeEntity("B_ALLY", 0);
            rig.Foe = new FakeEntity("C_FOE", 1);
            rig.Foe2 = new FakeEntity("D_FOE2", 1);
            rig.Ctx.AddEntity(rig.Hero).AddEntity(rig.Ally).AddEntity(rig.Foe).AddEntity(rig.Foe2);

            var melee = new FakeAbility("AB_SLASH"); melee.Ranged = false; melee.Enemy = true; melee.StatValue = "PHY";
            melee.TagList.Add("CF_BARGAIN");
            var ranged = new FakeAbility("AB_SHOOT"); ranged.Ranged = true; ranged.Enemy = true; ranged.StatValue = "DEX";
            var song = new FakeAbility("AB_SONG"); song.Ranged = false; song.Enemy = false; song.StatValue = "TAL";
            var heal = new FakeAbility("AB_HEAL"); heal.Ranged = false; heal.Enemy = false; heal.StatValue = "INT";
            rig.Ctx.AddAbility(melee).AddAbility(ranged).AddAbility(song).AddAbility(heal);

            rig.Set = set;
            rig.Rng = new FakeRandom(seed);
            rig.Dispatcher = new RecipeDispatcher(rig.Set, rig.Rng, rig.Log);

            // every recipe in the book is granted to the hero unless a test says otherwise
            for (int i = 0; i < rig.Set.Ordered.Count; i++) rig.Hero.PassiveList.Add(rig.Set.Ordered[i].Id);
            return rig;
        }

        public void GrantAlso(FakeEntity e)
        {
            for (int i = 0; i < Set.Ordered.Count; i++) e.PassiveList.Add(Set.Ordered[i].Id);
        }

        /// <summary>Fires the named trigger with a canonical payload, owner = <see cref="Hero"/>.</summary>
        public IReadOnlyList<EngineAction> Fire(TriggerKind trigger, string abilityId, RollTier tier)
        {
            var d = Dispatcher;
            switch (trigger)
            {
                case TriggerKind.ON_COMBAT_START:
                    return d.OnCombatStart(Ctx, new CombatStartEvent { Entity = Hero });
                case TriggerKind.ON_ABILITY_DECLARED:
                    return d.OnAbilityDeclared(Ctx, new AbilityDeclaredEvent
                    { Origin = Hero, Target = Foe, AbilityId = abilityId, RollTier = tier, FocusUsed = 2 });
                case TriggerKind.ON_ABILITY_USED:
                    return d.OnAbilityUsed(Ctx, new AbilityUsedEvent
                    { Origin = Hero, Target = Foe, AbilityId = abilityId, RollTier = tier, FocusUsed = 2 });
                case TriggerKind.ON_ENEMY_ABILITY_RESOLVED:
                    return d.OnEnemyAbilityResolved(Ctx, new EnemyAbilityResolvedEvent
                    { Origin = Foe, Target = Hero, AbilityId = abilityId, RollTier = tier });
                case TriggerKind.ON_CRIT:
                    return d.OnCrit(Ctx, new CritEvent { Origin = Hero, Target = Foe, AbilityId = abilityId, Stat = "HP" });
                case TriggerKind.ON_KILL:
                    return d.OnKill(Ctx, new KillEvent { Origin = Hero, Target = Foe, AbilityId = abilityId, HpBefore = 5, HpAfter = 0 });
                case TriggerKind.ON_DAMAGE_DEALT:
                    return d.OnDamageDealt(Ctx, new DamageDealtEvent { Origin = Hero, Target = Foe, AbilityId = abilityId, Amount = 7, HpBefore = 20, HpAfter = 13 });
                case TriggerKind.ON_DAMAGE_TAKEN:
                    return d.OnDamageTaken(Ctx, new DamageTakenEvent { Attacker = Foe, Victim = Hero, AbilityId = abilityId, Amount = 7 });
                case TriggerKind.ON_HEAL:
                    return d.OnHeal(Ctx, new HealEvent { Healer = Hero, Healed = Ally, AbilityId = abilityId, Amount = 6 });
                case TriggerKind.ON_HEAL_PENDING:
                    return d.OnHealPending(Ctx, new HealPendingEvent { Recipient = Hero, Healer = Ally, PendingAmount = 8, ItemConfigName = "DRINK_ALE" });
                case TriggerKind.ON_STATUS_APPLIED:
                    return d.OnStatusApplied(Ctx, new StatusAppliedEvent { Target = Hero, Applier = Foe, StatusId = "STATUS_CURSE_00" });
                case TriggerKind.ON_CONSUMABLE_USED:
                    return d.OnConsumableUsed(Ctx, new ConsumableUsedEvent { Origin = Hero, Target = Hero, ItemConfigName = "DRINK_ALE", AbilityId = abilityId });
                case TriggerKind.ON_TURN_START:
                    return d.OnTurnStart(Ctx, new TurnEvent { Entity = Hero });
                case TriggerKind.ON_TURN_END:
                    return d.OnTurnEnd(Ctx, new TurnEvent { Entity = Hero });
                default:
                    throw new AssertFailed("Rig.Fire has no case for " + trigger);
            }
        }

        public IReadOnlyList<EngineAction> Fire(TriggerKind trigger)
        {
            return Fire(trigger, "AB_SLASH", RollTier.PERFECT);
        }
    }

    /// <summary>Inline recipe JSON builders, so each test states only what it exercises.</summary>
    public static class J
    {
        public const string CounterEffect = "{\"Type\":\"COUNTER_ADD\",\"Name\":\"N\",\"Delta\":1}";
        public const string SelfStatusEffect = "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"STATUS_ATTACKUP_00\"}";

        /// <summary>One recipe, id SKILL_T.</summary>
        public static string One(string trigger, string conditions, string effects, string tail)
        {
            return "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"DisplayName\":\"T\",\"Trigger\":\"" + trigger +
                   "\",\"Conditions\":[" + conditions + "],\"Effects\":[" + effects + "]," +
                   "\"ProcChance\":100,\"AiProcChance\":100" + (tail ?? "") + "}}";
        }

        public static string Trigger(string trigger)
        {
            return One(trigger, "", CounterEffect, "");
        }

        public static string Cond(string trigger, string condition)
        {
            return One(trigger, condition, CounterEffect, "");
        }

        public static string Effect(string trigger, string effect)
        {
            return One(trigger, "", effect, "");
        }
    }
}
