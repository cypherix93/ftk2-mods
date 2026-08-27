using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    public static class EffectTests
    {
        public static void Run(TestRunner t)
        {
            t.Section("effects (8 tokens) + targets + value sources");

            t.Case("effect ADD_STATUS: target, status, fallback and duration are all planned", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\",\"Status\":\"STATUS_DAZE_00\"," +
                    "\"FallbackStatus\":\"STATUS_ATTACKDOWN_00\",\"Duration\":2}"), 5);
                var plan = rig.Fire(TriggerKind.ON_KILL);
                Check.PlanIs(plan,
                    "0: AddStatus{recipe=SKILL_T,owner=A_HERO,target=C_FOE,status=STATUS_DAZE_00,fallback=STATUS_ATTACKDOWN_00,duration=2}",
                    "ADD_STATUS plan");
            });

            t.Case("effect ADD_STATUS: StatusOneOf takes exactly one draw, after everything else passed", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_TARGET\"," +
                    "\"StatusOneOf\":[\"STATUS_FIRE_00\",\"STATUS_ICE_00\",\"STATUS_SHOCK_00\"]}"), 5);
                rig.Rng.ScriptInt(1);
                var plan = rig.Fire(TriggerKind.ON_KILL);
                Check.PlanCount(plan, 1, "one action");
                Check.Eq("STATUS_ICE_00", ((AddStatusAction)plan[0]).StatusId, "index 1 chosen");
                Check.Eq(1, rig.Rng.Draws, "exactly one draw for the element pick");
            });

            t.Case("effect ADD_STATUS: StatusOneOf draws nothing when no target resolves", () =>
            {
                var rig = Rig.Build(J.Effect("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"TRIGGER_SOURCE\"," +
                    "\"StatusOneOf\":[\"STATUS_FIRE_00\",\"STATUS_ICE_00\"]}"), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "no target, no action");
                Check.Eq(0, rig.Rng.Draws, "no wasted draw");
            });

            t.Case("effect REMOVE_STATUS: resolves the TRIGGER_STATUS token", () =>
            {
                var rig = Rig.Build(J.Effect("ON_STATUS_APPLIED",
                    "{\"Type\":\"REMOVE_STATUS\",\"Target\":\"SELF\",\"Status\":\"TRIGGER_STATUS\"}"), 5);
                Check.PlanIs(rig.Fire(TriggerKind.ON_STATUS_APPLIED),
                    "0: RemoveStatus{recipe=SKILL_T,owner=A_HERO,target=A_HERO,status=STATUS_CURSE_00}",
                    "REMOVE_STATUS plan");
            });

            t.Case("effect STAT_CHANGE: flat, percent, Blockable and IsSilent are carried verbatim", () =>
            {
                var rig = Rig.Build(J.Effect("ON_ABILITY_USED",
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"CASTER\",\"Stat\":\"FOC\",\"StatChangeType\":\"MAGICAL\"," +
                    "\"FlatValue\":1,\"Blockable\":false,\"IsSilent\":true}"), 5);
                Check.PlanIs(rig.Fire(TriggerKind.ON_ABILITY_USED),
                    "0: StatChange{recipe=SKILL_T,owner=A_HERO,target=A_HERO,stat=FOC,type=MAGICAL,flat=1,pct=-,blockable=0,silent=1}",
                    "STAT_CHANGE plan (FOCUS_CHANGE is not a separate effect)");
            });

            t.Case("effect STAT_CHANGE: FlatValueFrom FOCUS_SPENT with PerUnit/Min/Max", () =>
            {
                var rig = Rig.Build(J.Effect("ON_ABILITY_USED",
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"HP\",\"StatChangeType\":\"REGEN\"," +
                    "\"FlatValueFrom\":\"FOCUS_SPENT\",\"PerUnit\":3,\"Max\":5}"), 5);
                var plan = rig.Fire(TriggerKind.ON_ABILITY_USED);
                Check.Eq(5, ((StatChangeAction)plan[0]).FlatValue.Value, "FocusUsed 2 * 3 = 6, clamped to Max 5");
            });

            t.Case("effect STAT_CHANGE: PercentFrom TARGET_HP_PCT and COUNTER:<name>", () =>
            {
                var hp = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"HP\",\"StatChangeType\":\"REGEN\"," +
                    "\"PercentFrom\":\"TARGET_HP_PCT\"}"), 5);
                hp.Foe.With("HP", 30).With("MXHP", 60);
                Check.Eq(50, ((StatChangeAction)hp.Fire(TriggerKind.ON_KILL)[0]).FlatPercent.Value, "30/60 = 50%");

                string json =
                    "{\"SKILL_A_BUMP\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":0," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"STK\",\"Delta\":1}],\"ProcChance\":100}," +
                    "\"SKILL_B_USE\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"Priority\":1," +
                    "\"Effects\":[{\"Type\":\"STAT_CHANGE\",\"Target\":\"SELF\",\"Stat\":\"ATK\",\"StatChangeType\":\"PHYSICAL\"," +
                    "\"FlatValueFrom\":\"COUNTER:STK\",\"PerUnit\":10}],\"ProcChance\":100}}";
                var ct = Rig.Build(json, 5);
                ct.Fire(TriggerKind.ON_KILL);
                var plan = ct.Fire(TriggerKind.ON_KILL);
                Check.Eq(20, ((StatChangeAction)plan[1]).FlatValue.Value, "COUNTER:STK = 2, PerUnit 10");
            });

            t.Case("effect SUMMON: Count expands to N sequential ascending actions", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"SUMMON\",\"Target\":\"TRIGGER_TARGET_POSITION\",\"SummonType\":\"SPECIFIC\"," +
                    "\"CharacterConfig\":\"CF_SKELETON_WARRIOR\",\"Count\":3}"), 5);
                Check.PlanIs(rig.Fire(TriggerKind.ON_KILL),
                    "0: Summon{recipe=SKILL_T,owner=A_HERO,target=C_FOE,pos=1,type=SPECIFIC,config=CF_SKELETON_WARRIOR,i=0}\n" +
                    "1: Summon{recipe=SKILL_T,owner=A_HERO,target=C_FOE,pos=1,type=SPECIFIC,config=CF_SKELETON_WARRIOR,i=1}\n" +
                    "2: Summon{recipe=SKILL_T,owner=A_HERO,target=C_FOE,pos=1,type=SPECIFIC,config=CF_SKELETON_WARRIOR,i=2}",
                    "SUMMON expansion (OQ#4: AddCharacterAction has no count field)");
            });

            t.Case("effect ROLL_STAT_BONUS: PercentFrom STATUS_COUNT:HARMFUL, PerUnit 5, Max 20", () =>
            {
                var rig = Rig.Build(J.Effect("ON_ABILITY_DECLARED",
                    "{\"Type\":\"ROLL_STAT_BONUS\",\"Stat\":\"ATK\",\"PercentFrom\":\"STATUS_COUNT:HARMFUL\"," +
                    "\"PerUnit\":5,\"Max\":20}"), 5);
                rig.Hero.WithStatus("STATUS_BLEED_00").WithStatus("STATUS_CURSE_00").WithStatus("STATUS_ATTACKUP_00");
                var plan = rig.Fire(TriggerKind.ON_ABILITY_DECLARED);
                var a = (RollStatBonusAction)plan[0];
                Check.Eq(10, a.PercentDelta, "2 harmful * 5");
                Check.Eq("ATK", a.Stat, "stat");
                Check.Eq("ABILITY_ROLL", a.Window, "single-invocation window");

                var many = Rig.Build(J.Effect("ON_ABILITY_DECLARED",
                    "{\"Type\":\"ROLL_STAT_BONUS\",\"Stat\":\"ATK\",\"PercentFrom\":\"STATUS_COUNT:HARMFUL\"," +
                    "\"PerUnit\":5,\"Max\":20}"), 5);
                many.Hero.WithStatus("STATUS_BLEED_00").WithStatus("STATUS_CURSE_00")
                         .WithStatus("STATUS_FIRE_00").WithStatus("STATUS_ICE_00").WithStatus("STATUS_SHOCK_00");
                Check.Eq(20, ((RollStatBonusAction)many.Fire(TriggerKind.ON_ABILITY_DECLARED)[0]).PercentDelta,
                    "5 harmful * 5 = 25, clamped to Max 20");
            });

            t.Case("effect HEAL_MODIFIER: RECEIVED and GIVEN scopes plan a ref-pValue mutation", () =>
            {
                var rig = Rig.Build(J.Effect("ON_HEAL_PENDING",
                    "{\"Type\":\"HEAL_MODIFIER\",\"Scope\":\"RECEIVED\",\"Percent\":25,\"MinDelta\":1}"), 5);
                Check.PlanIs(rig.Fire(TriggerKind.ON_HEAL_PENDING),
                    "0: HealModifier{recipe=SKILL_T,owner=A_HERO,target=A_HERO,scope=RECEIVED,flat=0,pct=25,min=1}",
                    "HEAL_MODIFIER plan");

                var given = Rig.Build(J.Effect("ON_HEAL_PENDING",
                    "{\"Type\":\"HEAL_MODIFIER\",\"Scope\":\"GIVEN\",\"Flat\":10}"), 5);
                var a = (HealModifierAction)given.Fire(TriggerKind.ON_HEAL_PENDING)[0];
                Check.Eq(10, a.FlatDelta, "flat +10");
                Check.Eq((int)HealScope.GIVEN, (int)a.Scope, "GIVEN scope");
            });

            t.Case("effect HEAL_MODIFIER: takes zero draws (no RNG on the heal path)", () =>
            {
                var rig = Rig.Build(J.Effect("ON_HEAL_PENDING",
                    "{\"Type\":\"HEAL_MODIFIER\",\"Scope\":\"RECEIVED\",\"Percent\":25}"), 5);
                rig.Fire(TriggerKind.ON_HEAL_PENDING);
                Check.Eq(0, rig.Rng.Draws, "zero draws");
            });

            t.Case("effect COUNTER_ADD / COUNTER_SET: write per-battle state and report the new value", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"COUNTER_ADD\",\"Name\":\"STACK\",\"Delta\":1,\"Max\":2}"), 5);
                Check.Eq(1, ((CounterAddAction)rig.Fire(TriggerKind.ON_KILL)[0]).NewValue, "1");
                Check.Eq(2, ((CounterAddAction)rig.Fire(TriggerKind.ON_KILL)[0]).NewValue, "2");
                Check.Eq(2, ((CounterAddAction)rig.Fire(TriggerKind.ON_KILL)[0]).NewValue, "capped at Max 2");

                var setRig = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"COUNTER_SET\",\"Name\":\"STACK\",\"Value\":7}"), 5);
                Check.PlanIs(setRig.Fire(TriggerKind.ON_KILL),
                    "0: CounterSet{recipe=SKILL_T,owner=A_HERO,name=STACK,value=7,persistent=False}", "COUNTER_SET plan");
            });

            t.Case("effect COUNTER_ADD / COUNTER_SET: Persistent flag flows through onto the emitted action", () =>
            {
                var addRig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"COUNTER_ADD\",\"Name\":\"cf_bond\",\"Delta\":1,\"Max\":10,\"Persistent\":true}"), 5);
                var addAction = (CounterAddAction)addRig.Fire(TriggerKind.ON_KILL)[0];
                Check.Eq(1, addAction.NewValue, "add still applies in-memory as usual");
                Check.True(addAction.Persistent, "Persistent carried onto the CounterAddAction");

                var setRig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"COUNTER_SET\",\"Name\":\"cf_bond\",\"Value\":3,\"Persistent\":true}"), 5);
                var setAction = (CounterSetAction)setRig.Fire(TriggerKind.ON_KILL)[0];
                Check.True(setAction.Persistent, "Persistent carried onto the CounterSetAction");

                var plainRig = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"COUNTER_ADD\",\"Name\":\"n\",\"Delta\":1}"), 5);
                var plainAction = (CounterAddAction)plainRig.Fire(TriggerKind.ON_KILL)[0];
                Check.True(!plainAction.Persistent, "Persistent defaults to false when not authored");
            });

            t.Case("effect COUNTER_ADD Persistent=true still resets to 0 in the bare engine on a new CombatKey " +
                   "(negative control: the pure engine never seeds from a persistent store on its own -- only " +
                   "the Plugin's RecipeEngineHost.SeedPersistentCounters does, using the owner's CustomData, " +
                   "before the first hook of a fresh battle runs)", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL",
                    "{\"Type\":\"COUNTER_ADD\",\"Name\":\"cf_bond\",\"Delta\":1,\"Max\":10,\"Persistent\":true}"), 5);
                Check.Eq(1, ((CounterAddAction)rig.Fire(TriggerKind.ON_KILL)[0]).NewValue, "1 after first kill");
                Check.Eq(2, ((CounterAddAction)rig.Fire(TriggerKind.ON_KILL)[0]).NewValue, "2 after second kill");
                rig.Ctx.Identity = "COMBAT_NEXT";
                Check.Eq(1, ((CounterAddAction)rig.Fire(TriggerKind.ON_KILL)[0]).NewValue,
                    "new CombatKey -> fresh CombatRuntime -> counter starts at 0 again in the bare engine, " +
                    "Persistent flag notwithstanding");
            });

            t.Section("target tokens (9)");

            t.Case("target SELF / CASTER resolve to the recipe owner", () =>
            {
                var a = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S\"}"), 5);
                Check.Eq("A_HERO", ((AddStatusAction)a.Fire(TriggerKind.ON_KILL)[0]).TargetGuid, "SELF");
                var b = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"ADD_STATUS\",\"Target\":\"CASTER\",\"Status\":\"S\"}"), 5);
                Check.Eq("A_HERO", ((AddStatusAction)b.Fire(TriggerKind.ON_KILL)[0]).TargetGuid, "CASTER");
            });

            t.Case("target ALLY_ALL / ALLY_ALL_OTHERS / ENEMY_ALL iterate Guid-ascending", () =>
            {
                var all = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_ALL\",\"Status\":\"S\"}"), 5);
                var p = all.Fire(TriggerKind.ON_KILL);
                Check.PlanCount(p, 2, "hero + ally");
                Check.Eq("A_HERO", ((AddStatusAction)p[0]).TargetGuid, "first");
                Check.Eq("B_ALLY", ((AddStatusAction)p[1]).TargetGuid, "second");

                var others = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_ALL_OTHERS\",\"Status\":\"S\"}"), 5);
                var q = others.Fire(TriggerKind.ON_KILL);
                Check.PlanCount(q, 1, "ally only (WARDEN excludes self)");
                Check.Eq("B_ALLY", ((AddStatusAction)q[0]).TargetGuid, "ally");

                var foes = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"ADD_STATUS\",\"Target\":\"ENEMY_ALL\",\"Status\":\"S\"}"), 5);
                var s = foes.Fire(TriggerKind.ON_KILL);
                Check.PlanCount(s, 2, "both foes (RUNEMAGE detonation)");
                Check.Eq("C_FOE", ((AddStatusAction)s[0]).TargetGuid, "C first");
                Check.Eq("D_FOE2", ((AddStatusAction)s[1]).TargetGuid, "D second");
            });

            t.Case("target ALLY_ALL / ENEMY_ALL skip the dead", () =>
            {
                var rig = Rig.Build(J.Effect("ON_KILL", "{\"Type\":\"ADD_STATUS\",\"Target\":\"ENEMY_ALL\",\"Status\":\"S\"}"), 5);
                rig.Foe2.Alive = false;
                Check.PlanCount(rig.Fire(TriggerKind.ON_KILL), 1, "only the living foe");
            });

            t.Case("target ALLY_BY_RANK: Where filter, LOWEST/HIGHEST, Guid tiebreak, no-match no-op", () =>
            {
                string rank =
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_BY_RANK\",\"Status\":\"STATUS_PROTECT_00\"," +
                    "\"Rank\":{\"Stat\":\"HP_PCT\",\"Order\":\"LOWEST\",\"ExcludeSelf\":true," +
                    "\"Where\":[{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"LTE\",\"Percent\":25}]}}";

                var none = Rig.Build(J.Effect("ON_ABILITY_USED", rank), 5);
                Check.PlanCount(none.Fire(TriggerKind.ON_ABILITY_USED), 0, "no wounded ally -> no-op");

                var one = Rig.Build(J.Effect("ON_ABILITY_USED", rank), 5);
                one.Ally.With("HP", 10);
                var p = one.Fire(TriggerKind.ON_ABILITY_USED);
                Check.PlanCount(p, 1, "wounded ally found");
                Check.Eq("B_ALLY", ((AddStatusAction)p[0]).TargetGuid, "ally chosen");

                // three candidates, two tied at the lowest HP% -> ordinal Guid breaks the tie
                var tie = Rig.Build(J.Effect("ON_ABILITY_USED", rank), 5);
                var m = new FakeEntity("M_MID", 0).With("HP", 10);
                var z = new FakeEntity("Z_LAST", 0).With("HP", 10);
                tie.Ctx.AddEntity(m).AddEntity(z);
                tie.Ally.With("HP", 20);
                Check.Eq("M_MID", ((AddStatusAction)tie.Fire(TriggerKind.ON_ABILITY_USED)[0]).TargetGuid,
                    "lowest HP%, ties broken by ordinal Guid");

                var highest = Rig.Build(J.Effect("ON_ABILITY_USED",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_BY_RANK\",\"Status\":\"S\"," +
                    "\"Rank\":{\"Stat\":\"SPD\",\"Order\":\"HIGHEST\",\"ExcludeSelf\":false,\"Where\":[]}}"), 5);
                highest.Hero.With("SPD", 3);
                highest.Ally.With("SPD", 9);
                Check.Eq("B_ALLY", ((AddStatusAction)highest.Fire(TriggerKind.ON_ABILITY_USED)[0]).TargetGuid,
                    "HIGHEST SPD, ExcludeSelf false");
            });

            t.Case("target ALLY_BY_RANK: HP_PCT with no Where filter picks the wounded ally, not the ordinal-first one (SKILL_CF_PACIFIST_FIELD_MEDIC regression: GameAdapters.GetStat used to route HP/MXHP through the static base-stat table, which has no MXHP key, so HP_PCT was always 0 for every candidate and ResolveByRank kept whichever ally sorted first by Guid)", () =>
            {
                string rank =
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_BY_RANK\",\"Status\":\"S\"," +
                    "\"Rank\":{\"Stat\":\"HP_PCT\",\"Order\":\"LOWEST\",\"ExcludeSelf\":false,\"Where\":[]}}";

                // A_HERO sorts before B_ALLY by ordinal Guid, so the pre-fix bug (every HP_PCT tied at 0)
                // always kept A_HERO regardless of who was actually wounded.
                var rig = Rig.Build(J.Effect("ON_ABILITY_USED", rank), 5);
                rig.Hero.With("HP", 100).With("MXHP", 100);
                rig.Ally.With("HP", 14).With("MXHP", 100);
                Check.Eq("B_ALLY", ((AddStatusAction)rig.Fire(TriggerKind.ON_ABILITY_USED)[0]).TargetGuid,
                    "the vampiric at 14 HP is picked over the ordinal-first, full-HP hero");
            });

            t.Case("target ALLY_BY_RANK: zero draws (fully deterministic selector)", () =>
            {
                var rig = Rig.Build(J.Effect("ON_ABILITY_USED",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"ALLY_BY_RANK\",\"Status\":\"S\"," +
                    "\"Rank\":{\"Stat\":\"SPD\",\"Order\":\"LOWEST\",\"ExcludeSelf\":true,\"Where\":[]}}"), 5);
                rig.Fire(TriggerKind.ON_ABILITY_USED);
                Check.Eq(0, rig.Rng.Draws, "no RNG in ALLY_BY_RANK");
            });

            t.Section("per-effect conditions and ordering");

            t.Case("effects: applied in authored array order", () =>
            {
                var rig = Rig.Build(J.One("ON_ABILITY_USED", "",
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"CASTER\",\"Stat\":\"HP\",\"StatChangeType\":\"MAGICAL\",\"FlatValue\":-4}," +
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"CASTER\",\"Stat\":\"FOC\",\"StatChangeType\":\"MAGICAL\",\"FlatValue\":3}", ""), 5);
                var p = rig.Fire(TriggerKind.ON_ABILITY_USED);
                Check.PlanCount(p, 2, "two actions");
                Check.Eq("HP", ((StatChangeAction)p[0]).Stat, "first authored effect first");
                Check.Eq("FOC", ((StatChangeAction)p[1]).Stat, "second authored effect second");
            });

            t.Case("effects: a per-effect Conditions array gates only that entry", () =>
            {
                string effects =
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_ALWAYS\"}," +
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S_GATED\"," +
                    "\"Conditions\":[{\"Type\":\"HAS_STATUS\",\"Value\":\"STATUS_BLEED_00\"}]}";

                var off = Rig.Build(J.One("ON_KILL", "", effects, ""), 5);
                var p = off.Fire(TriggerKind.ON_KILL);
                Check.PlanCount(p, 1, "gated effect skipped");
                Check.Eq("S_ALWAYS", ((AddStatusAction)p[0]).StatusId, "ungated one survives");

                var on = Rig.Build(J.One("ON_KILL", "", effects, ""), 5);
                on.Hero.WithStatus("STATUS_BLEED_00");
                Check.PlanCount(on.Fire(TriggerKind.ON_KILL), 2, "both effects");
            });
        }
    }
}
