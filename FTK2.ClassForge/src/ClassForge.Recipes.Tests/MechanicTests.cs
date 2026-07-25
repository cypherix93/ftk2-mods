using System;
using System.Collections.Generic;
using System.IO;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// Each case expresses one PORT / PORT-MODIFIED row from
    /// <c>docs/research/eor-rehost-coverage-matrix.md</c> as a real JSON fixture and simulates it.
    /// </summary>
    public static class MechanicTests
    {
        private static Rig Load(string fixture, int seed)
        {
            var rig = Rig.Build(Fixtures.Read(fixture), seed);
            Check.NoErrors(rig.Set, fixture + " parses cleanly");
            return rig;
        }

        public static void Run(TestRunner t)
        {
            t.Section("EOR mechanic fixtures (real JSON, simulated)");

            t.Case("mechanic PREPARED / OF_FOCUS (prepared.json): +1 Focus at combat start, zero RNG", () =>
            {
                var rig = Load("prepared.json", 9);
                rig.Hero.With("FOC", 1).With("MXFOC", 3);
                Check.PlanIs(rig.Fire(TriggerKind.ON_COMBAT_START),
                    "0: StatChange{recipe=SKILL_CF_PREPARED,owner=A_HERO,target=A_HERO,stat=FOC,type=MAGICAL,flat=1,pct=-,blockable=0,silent=0}",
                    "PREPARED plan");
                Check.Eq(0, rig.Rng.Draws, "no draw (ProcChance 100)");

                var full = Load("prepared.json", 9);
                full.Hero.With("FOC", 3).With("MXFOC", 3);
                Check.PlanCount(full.Fire(TriggerKind.ON_COMBAT_START), 0, "no focus missing, no grant");
            });

            t.Case("mechanic STEADY_AIM (steady_aim.json): +10 CRT on the first ranged hostile declare per combat", () =>
            {
                var rig = Load("steady_aim.json", 9);
                var p = rig.Fire(TriggerKind.ON_ABILITY_DECLARED, "AB_SHOOT", RollTier.SUCCESS);
                Check.PlanIs(p,
                    "0: RollStatBonus{recipe=SKILL_CF_STEADY_AIM,owner=A_HERO,target=A_HERO,stat=CRT,flat=10,pct=0,min=-,window=ABILITY_ROLL}",
                    "STEADY_AIM plan");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_DECLARED, "AB_SHOOT", RollTier.SUCCESS), 0,
                    "ONCE_PER_COMBAT budget spent");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_DECLARED, "AB_SLASH", RollTier.SUCCESS), 0,
                    "melee never qualifies anyway");

                // the EOR bug this fixes: a process-global budget set that survived into the next combat
                rig.Ctx.Identity = "COMBAT_2";
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_DECLARED, "AB_SHOOT", RollTier.SUCCESS), 1,
                    "new combat re-opens the budget with no explicit cleanup call");
            });

            t.Case("mechanic WARDBOUND / OF_STABILITY (wardbound.json): observe-then-REMOVE_STATUS{TRIGGER_STATUS}", () =>
            {
                var hit = Load("wardbound.json", 9);
                hit.Rng.ScriptChance(true);
                var p = hit.Dispatcher.OnStatusApplied(hit.Ctx, new StatusAppliedEvent
                { Target = hit.Hero, Applier = hit.Foe, StatusId = "STATUS_CURSE_00" });
                Check.PlanIs(p,
                    "0: RemoveStatus{recipe=SKILL_CF_WARDBOUND_CURSE,owner=A_HERO,target=A_HERO,status=STATUS_CURSE_00}",
                    "cleanse plan");
                Check.Eq(1, hit.Rng.Draws, "exactly one 20% draw per status application");

                var miss = Load("wardbound.json", 9);
                miss.Rng.ScriptChance(false);
                Check.PlanCount(miss.Dispatcher.OnStatusApplied(miss.Ctx, new StatusAppliedEvent
                { Target = miss.Hero, Applier = miss.Foe, StatusId = "STATUS_CURSE_00" }), 0, "failed roll");

                var debuff = Load("wardbound.json", 9);
                debuff.Rng.ScriptChance(true);
                var q = debuff.Dispatcher.OnStatusApplied(debuff.Ctx, new StatusAppliedEvent
                { Target = debuff.Hero, Applier = debuff.Foe, StatusId = "STATUS_ATTACKDOWN_00" });
                Check.PlanCount(q, 1, "the DEBUFF half covers DEBUFF-typed statuses");
                Check.Eq("SKILL_CF_WARDBOUND_DEBUFF", q[0].RecipeId, "the debuff recipe, not the curse one");
                Check.Eq(1, debuff.Rng.Draws, "still one draw — a status has exactly one Type");

                var benign = Load("wardbound.json", 9);
                Check.PlanCount(benign.Dispatcher.OnStatusApplied(benign.Ctx, new StatusAppliedEvent
                { Target = benign.Hero, Applier = benign.Foe, StatusId = "STATUS_ATTACKUP_00" }), 0, "buffs are untouched");
                Check.Eq(0, benign.Rng.Draws, "and take no draw");
            });

            t.Case("mechanic MOMENTUM / OF_MOMENTUM (momentum.json): kill -> self ATTACKUP", () =>
            {
                var rig = Load("momentum.json", 9);
                Check.PlanIs(rig.Fire(TriggerKind.ON_KILL),
                    "0: AddStatus{recipe=SKILL_CF_MOMENTUM,owner=A_HERO,target=A_HERO,status=STATUS_ATTACKUP_00,fallback=-,duration=-}",
                    "MOMENTUM plan");
                Check.PlanCount(rig.Fire(TriggerKind.ON_DAMAGE_DEALT), 0, "damage without a kill does nothing");
            });

            t.Case("mechanic BATTLE_RHYTHM (battle_rhythm.json): damage dealt to an opponent -> self EVADEUP", () =>
            {
                var rig = Load("battle_rhythm.json", 9);
                Check.PlanIs(rig.Fire(TriggerKind.ON_DAMAGE_DEALT),
                    "0: AddStatus{recipe=SKILL_CF_BATTLE_RHYTHM,owner=A_HERO,target=A_HERO,status=STATUS_EVADEUP_00,fallback=-,duration=-}",
                    "BATTLE_RHYTHM plan");

                var friendly = Load("battle_rhythm.json", 9);
                var p = friendly.Dispatcher.OnDamageDealt(friendly.Ctx, new DamageDealtEvent
                { Origin = friendly.Hero, Target = friendly.Ally, AbilityId = "AB_SLASH", Amount = 3, HpBefore = 30, HpAfter = 27 });
                Check.PlanCount(p, 0, "HOSTILE_ACTION excludes friendly fire");
            });

            t.Case("mechanic SENTINEL (sentinel.json): melee-only, re-rolls until it procs once per round", () =>
            {
                var rig = Load("sentinel.json", 9);
                rig.Rng.ScriptChance(false, true, false);
                Check.PlanCount(rig.Fire(TriggerKind.ON_DAMAGE_TAKEN, "AB_SLASH", RollTier.SUCCESS), 0, "hit 1 rolls and fails");
                var p = rig.Fire(TriggerKind.ON_DAMAGE_TAKEN, "AB_SLASH", RollTier.SUCCESS);
                Check.PlanIs(p,
                    "0: AddStatus{recipe=SKILL_CF_SENTINEL_BRACE,owner=A_HERO,target=C_FOE,status=STATUS_DAZE_00,fallback=STATUS_ATTACKDOWN_00,duration=1}",
                    "hit 2 procs onto the attacker with an immunity fallback");
                Check.PlanCount(rig.Fire(TriggerKind.ON_DAMAGE_TAKEN, "AB_SLASH", RollTier.SUCCESS), 0, "hit 3 is budget-blocked");
                Check.Eq(2, rig.Rng.Draws, "no draw once the round budget is spent");

                var ranged = Load("sentinel.json", 9);
                Check.PlanCount(ranged.Fire(TriggerKind.ON_DAMAGE_TAKEN, "AB_SHOOT", RollTier.SUCCESS), 0, "ranged attacks do not qualify");
                Check.Eq(0, ranged.Rng.Draws, "and take no draw");
            });

            t.Case("mechanic TEMPLAR Zeal (templar.json): ATK% from harmful status count, capped at 20", () =>
            {
                var rig = Load("templar.json", 9);
                rig.Hero.WithStatus("STATUS_BLEED_00").WithStatus("STATUS_CURSE_00").WithStatus("STATUS_FIRE_00")
                        .WithStatus("STATUS_ATTACKUP_00");
                var a = (RollStatBonusAction)rig.Fire(TriggerKind.ON_ABILITY_DECLARED)[0];
                Check.Eq(15, a.PercentDelta, "3 harmful * 5 (the buff does not count)");

                var capped = Load("templar.json", 9);
                capped.Hero.WithStatus("STATUS_BLEED_00").WithStatus("STATUS_CURSE_00").WithStatus("STATUS_FIRE_00")
                           .WithStatus("STATUS_ICE_00").WithStatus("STATUS_SHOCK_00");
                Check.Eq(20, ((RollStatBonusAction)capped.Fire(TriggerKind.ON_ABILITY_DECLARED)[0]).PercentDelta,
                    "min(20, count*5)");
                Check.Eq(0, capped.Rng.Draws, "Zeal has no roll-tier gate and no RNG");
            });

            t.Case("mechanic KNIGHT Chivalry (knight.json): ALLY_BY_RANK + ConsumeOn EFFECT_APPLIED", () =>
            {
                var rig = Load("knight.json", 9);
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 0, "no wounded ally yet");
                rig.Ally.With("HP", 20).With("MXHP", 100);
                var p = rig.Fire(TriggerKind.ON_ABILITY_USED);
                Check.PlanIs(p,
                    "0: AddStatus{recipe=SKILL_CF_KNIGHT_CHIVALRY,owner=A_HERO,target=B_ALLY,status=STATUS_PROTECT_00,fallback=-,duration=1}\n" +
                    "1: AddStatus{recipe=SKILL_CF_KNIGHT_CHIVALRY,owner=A_HERO,target=B_ALLY,status=STATUS_ARMORUP_00,fallback=-,duration=1}",
                    "both statuses land on the lowest-HP% wounded ally");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 0, "once per combat, now spent");
            });

            t.Case("mechanic BARD Crescendo (bard.json): counters + per-effect conditions drive the escalating tier", () =>
            {
                var rig = Load("bard.json", 9);
                var s1 = rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SONG", RollTier.SUCCESS);
                Check.PlanCount(s1, 3, "counter + one tier-00 status per ally");
                Check.Eq("STATUS_ATTACKUP_00", ((AddStatusAction)s1[1]).StatusId, "tier 00 at 1 stack");

                var s2 = rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SONG", RollTier.PERFECT);
                Check.Eq("STATUS_ATTACKUP_01", ((AddStatusAction)s2[1]).StatusId, "tier 01 at 2 stacks");

                var s3 = rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SONG", RollTier.SUCCESS);
                Check.Eq("STATUS_ATTACKUP_02", ((AddStatusAction)s3[1]).StatusId, "tier 02 at 3 stacks");

                var s4 = rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SONG", RollTier.SUCCESS);
                Check.Eq("STATUS_ATTACKUP_02", ((AddStatusAction)s4[1]).StatusId, "capped at 3 stacks");

                var reset = rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SONG", RollTier.CRIT_FAIL);
                Check.PlanCount(reset, 1, "crit fail only resets");
                Check.Eq("CounterSet", reset[0].Kind, "COUNTER_SET");

                var after = rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SONG", RollTier.SUCCESS);
                Check.Eq("STATUS_ATTACKUP_00", ((AddStatusAction)after[1]).StatusId, "back to tier 00");

                var wrongStat = rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.PERFECT);
                Check.PlanCount(wrongStat, 0, "non-TAL abilities are ignored");
            });

            t.Case("mechanic FIELDMEDIC + MENDERS_TOUCH (fieldmedic.json): stacked HEAL_MODIFIERs, no RNG", () =>
            {
                var rig = Load("fieldmedic.json", 9);
                var p = rig.Fire(TriggerKind.ON_HEAL_PENDING);
                Check.PlanIs(p,
                    "0: HealModifier{recipe=SKILL_CF_FIELDMEDIC,owner=A_HERO,target=A_HERO,scope=RECEIVED,flat=0,pct=25,min=1}\n" +
                    "1: HealModifier{recipe=SKILL_CF_MENDERS_TOUCH,owner=A_HERO,target=A_HERO,scope=GIVEN,flat=10,pct=0,min=-}",
                    "both heal modifiers plan");
                Check.Eq(0, rig.Rng.Draws, "no RNG is permitted on the heal path");

                var noItem = Load("fieldmedic.json", 9);
                var q = noItem.Dispatcher.OnHealPending(noItem.Ctx, new HealPendingEvent
                { Recipient = noItem.Hero, Healer = noItem.Ally, PendingAmount = 8, ItemConfigName = null });
                Check.PlanCount(q, 1, "MENDERS_TOUCH needs a consumable");
            });

            t.Case("mechanic DRUNKEN_COURAGE (drunken_courage.json): ITEM_CLASS off the Thing config", () =>
            {
                var rig = Load("drunken_courage.json", 9);
                Check.PlanIs(rig.Fire(TriggerKind.ON_CONSUMABLE_USED),
                    "0: AddStatus{recipe=SKILL_CF_DRUNKEN_COURAGE,owner=A_HERO,target=A_HERO,status=STATUS_ATTACKUP_00,fallback=-,duration=-}",
                    "DRUNKEN_COURAGE plan");

                var scroll = Load("drunken_courage.json", 9);
                Check.PlanCount(scroll.Dispatcher.OnConsumableUsed(scroll.Ctx, new ConsumableUsedEvent
                { Origin = scroll.Hero, Target = scroll.Hero, ItemConfigName = "SCROLL_FIRE", AbilityId = "AB_SLASH" }), 0,
                    "a scroll is not a drink");
            });

            t.Case("mechanic ORACLE Premonition (oracle.json): T7 multi-owner, once per combat", () =>
            {
                var rig = Load("oracle.json", 9);
                rig.GrantAlso(rig.Ally);
                var p = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(p, 6, "2 owners x (1 attacker debuff + 2 ally buffs)");
                Check.Eq("A_HERO", p[0].OwnerGuid, "lowest ordinal Guid owner first");
                Check.Eq("C_FOE", ((AddStatusAction)p[0]).TargetGuid, "TRIGGER_SOURCE is the acting enemy");
                Check.Eq("B_ALLY", p[3].OwnerGuid, "then the ally");
                Check.Eq(0, rig.Rng.Draws, "ProcChance 100 on a multi-owner trigger takes zero draws");

                Check.PlanCount(rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT }), 0,
                    "ONCE_PER_COMBAT for both owners");

                var notPerfect = Load("oracle.json", 9);
                Check.PlanCount(notPerfect.Dispatcher.OnEnemyAbilityResolved(notPerfect.Ctx, new EnemyAbilityResolvedEvent
                { Origin = notPerfect.Foe, Target = notPerfect.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.SUCCESS }), 0,
                    "only a PERFECT enemy roll triggers it");
            });

            t.Section("scripted combat over the whole fixture book");

            t.Case("all fixtures loaded together simulate a 3-round combat deterministically", () =>
            {
                var a = LoadAll(31337);
                var b = LoadAll(31337);
                string logA = SimulateCombat(a);
                string logB = SimulateCombat(b);
                Check.True(logA.Length > 0, "the scripted combat produced actions");
                Check.Eq(logA, logB, "two peers running the whole fixture book agree exactly");
                Check.Eq(a.Rng.Draws, b.Rng.Draws, "identical draw counts");

                Check.Contains(logA, "SKILL_CF_PREPARED", "PREPARED fired");
                Check.Contains(logA, "SKILL_CF_STEADY_AIM", "STEADY_AIM fired");
                Check.Contains(logA, "SKILL_CF_MOMENTUM", "MOMENTUM fired");
                Check.Contains(logA, "SKILL_CF_BATTLE_RHYTHM", "BATTLE_RHYTHM fired");
                Check.Contains(logA, "SKILL_CF_BARD_CRESCENDO", "BARD fired");
                Check.Contains(logA, "SKILL_CF_ORACLE_PREMONITION", "ORACLE fired");
            });
        }

        private static Rig LoadAll(int seed)
        {
            var combined = new RecipeSet();
            var files = Fixtures.All();
            for (int i = 0; i < files.Count; i++)
            {
                var set = RecipeParser.Parse(File.ReadAllText(files[i]));
                for (int k = 0; k < set.Ordered.Count; k++) combined.Add(set.Ordered[k]);
            }
            var rig = Rig.FromSet(combined, seed);
            rig.GrantAlso(rig.Ally);
            rig.Hero.With("FOC", 0).With("MXFOC", 3);
            rig.Ally.With("HP", 20).With("MXHP", 100);
            return rig;
        }

        /// <summary>A fixed script: combat start, then three rounds of declare/attack/react/heal/drink.</summary>
        private static string SimulateCombat(Rig rig)
        {
            var all = new List<EngineAction>();
            var d = rig.Dispatcher;
            var ctx = rig.Ctx;

            all.AddRange(d.OnCombatStart(ctx, new CombatStartEvent { Entity = rig.Hero }));
            all.AddRange(d.OnCombatStart(ctx, new CombatStartEvent { Entity = rig.Ally }));

            for (int round = 1; round <= 3; round++)
            {
                ctx.RoundValue = round;

                all.AddRange(d.OnAbilityDeclared(ctx, new AbilityDeclaredEvent
                { Origin = rig.Hero, Target = rig.Foe, AbilityId = "AB_SHOOT", RollTier = RollTier.PERFECT, FocusUsed = 1 }));
                all.AddRange(d.OnDamageDealt(ctx, new DamageDealtEvent
                { Origin = rig.Hero, Target = rig.Foe, AbilityId = "AB_SHOOT", Amount = 6, HpBefore = 20, HpAfter = 14 }));
                all.AddRange(d.OnAbilityUsed(ctx, new AbilityUsedEvent
                { Origin = rig.Hero, Target = rig.Foe, AbilityId = "AB_SHOOT", RollTier = RollTier.PERFECT, FocusUsed = 1 }));

                all.AddRange(d.OnAbilityDeclared(ctx, new AbilityDeclaredEvent
                { Origin = rig.Ally, Target = rig.Foe, AbilityId = "AB_SONG", RollTier = RollTier.SUCCESS, FocusUsed = 0 }));
                all.AddRange(d.OnAbilityUsed(ctx, new AbilityUsedEvent
                { Origin = rig.Ally, Target = rig.Foe, AbilityId = "AB_SONG", RollTier = RollTier.SUCCESS, FocusUsed = 0 }));

                // the enemy acts: a perfect roll, a melee hit on the hero, and a curse
                all.AddRange(d.OnEnemyAbilityResolved(ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT }));
                all.AddRange(d.OnDamageTaken(ctx, new DamageTakenEvent
                { Attacker = rig.Foe, Victim = rig.Hero, AbilityId = "AB_SLASH", Amount = 5 }));
                all.AddRange(d.OnStatusApplied(ctx, new StatusAppliedEvent
                { Target = rig.Hero, Applier = rig.Foe, StatusId = "STATUS_CURSE_00" }));

                // support actions
                all.AddRange(d.OnHealPending(ctx, new HealPendingEvent
                { Recipient = rig.Ally, Healer = rig.Hero, PendingAmount = 9, ItemConfigName = "DRINK_ALE" }));
                all.AddRange(d.OnConsumableUsed(ctx, new ConsumableUsedEvent
                { Origin = rig.Hero, Target = rig.Hero, ItemConfigName = "DRINK_ALE", AbilityId = "AB_HEAL" }));

                // a finishing blow on round 2
                if (round == 2)
                    all.AddRange(d.OnKill(ctx, new KillEvent
                    { Origin = rig.Hero, Target = rig.Foe2, AbilityId = "AB_SHOOT", HpBefore = 3, HpAfter = 0 }));
            }
            return ActionLog.Render(all);
        }
    }
}
