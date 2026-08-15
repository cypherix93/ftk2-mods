using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// STATE_HASH_CHANCE spec (docs/superpowers/plans/2026-08-08-state-hash-chance-spec.md) M-SH1/M-SH2
    /// + the ON_DAMAGE_PENDING / DAMAGE_TAKEN_MULT surface (M-SH3's engine half).
    /// Hash vectors were computed with an independent Python FNV-1a implementation, not this code.
    /// </summary>
    public static class StateHashTests
    {
        public static void Run(TestRunner t)
        {
            t.Section("STATE_HASH_CHANCE (M-SH1: hash + schema)");

            t.Case("hash: FNV-1a matches EOR62 L26754 exactly (externally computed vectors)", () =>
            {
                // EOR-shaped canonical string (their SHIELDBEARER format).
                Check.Eq(unchecked((int)1536509494u), unchecked((int)StateHashChance.Fnv1a("E1|3|25|7|SHIELDBEARER")), "EOR-shape vector");
                // Our canonical shape: salt|inputs...
                Check.Eq(unchecked((int)3491965674u), unchecked((int)StateHashChance.Fnv1a("CF_TEST_SALT|A_HERO|2|40|7|777")), "canonical vector");
                // Offset basis on empty input.
                Check.Eq(unchecked((int)2166136261u), unchecked((int)StateHashChance.Fnv1a("")), "offset basis");
                Check.Eq(unchecked((int)1918752861u), unchecked((int)StateHashChance.Fnv1a("CF_X|")), "salt-only vector");
            });

            t.Case("hash: verdict is h % 100 < Percent; adjacent damage values decorrelate", () =>
            {
                // 3491965674 % 100 = 74 -> passes only at Percent > 74.
                var vals7 = new[] { "A_HERO", "2", "40", "7", "777" };
                Check.False(StateHashChance.Verdict("CF_TEST_SALT", vals7, 74), "74 is not < 74");
                Check.True(StateHashChance.Verdict("CF_TEST_SALT", vals7, 75), "74 < 75");
                // 1016629279 % 100 = 79 — the damage-8 tuple lands elsewhere (spec §6.1: EOR includes
                // finalDamage precisely so repeated evaluation varies with it).
                var vals8 = new[] { "A_HERO", "2", "40", "8", "777" };
                Check.False(StateHashChance.Verdict("CF_TEST_SALT", vals8, 79), "79 not < 79");
                Check.True(StateHashChance.Verdict("CF_TEST_SALT", vals8, 80), "79 < 80");
            });

            t.Case("hash: identical tuple is perfectly correlated (same verdict, always)", () =>
            {
                var vals = new[] { "G1", "5", "12", "9", "42" };
                bool first = StateHashChance.Verdict("CF_CORR", vals, 20);
                for (int i = 0; i < 50; i++)
                    Check.Eq(first ? 1 : 0, StateHashChance.Verdict("CF_CORR", vals, 20) ? 1 : 0,
                        "re-evaluation " + i.ToString());
            });

            t.Case("hash: observed pass rate over 100k varied tuples tracks Percent (spec M-SH1 distribution)", () =>
            {
                int hits = 0;
                for (int i = 0; i < 100000; i++)
                    if (StateHashChance.Verdict("CF_DIST", new[] { "G", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "SEED" }, 20))
                        hits++;
                // Python reference gives 20038. Assert the same engine-side arithmetic (exact, since the
                // algorithm is fully deterministic) — a drifting constant means the hash changed.
                Check.Eq(20038, hits, "distribution count at Percent 20");
            });

            t.Section("STATE_HASH_CHANCE (validator)");

            t.Case("validator: well-formed condition parses and validates clean on SchemaVersion 1.3", () =>
            {
                var set = ParseValid(HashRecipe("{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"CF_OK_SALT\"," +
                                                "\"Inputs\":[\"SELF_GUID\",\"COMBAT_ROUND\",\"SELF_HP\",\"TRIGGER_DAMAGE\",\"COMBAT_SEED\"]}"));
                Check.NoErrors(set, "clean 1.3 recipe");
                var c = set.Find("SKILL_H").Conditions[0];
                Check.Eq("CF_OK_SALT", c.Salt, "Salt");
                Check.Eq(5, c.Inputs.Count, "Inputs count");
            });

            t.Case("validator: Percent 0 and 100 are errors (a constant gate written as a hash is a bug)", () =>
            {
                Check.HasFinding(ParseValid(HashRecipe("{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":0,\"Salt\":\"CF_ZERO_SALT\",\"Inputs\":[\"SELF_GUID\",\"COMBAT_SEED\"]}")),
                    "E_HASH_PERCENT", "0 rejected");
                Check.HasFinding(ParseValid(HashRecipe("{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":100,\"Salt\":\"CF_FULL_SALT\",\"Inputs\":[\"SELF_GUID\",\"COMBAT_SEED\"]}")),
                    "E_HASH_PERCENT", "100 rejected");
            });

            t.Case("validator: Salt shape and Inputs bounds are enforced", () =>
            {
                Check.HasFinding(ParseValid(HashRecipe("{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"bad salt\",\"Inputs\":[\"SELF_GUID\",\"COMBAT_SEED\"]}")),
                    "E_HASH_SALT", "lowercase/space salt rejected");
                Check.HasFinding(ParseValid(HashRecipe("{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"CF_ONE_INPUT\",\"Inputs\":[\"SELF_GUID\"]}")),
                    "E_HASH_INPUTS", "1 input rejected (min 2)");
                Check.HasFinding(ParseValid(HashRecipe("{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"CF_BAD_TOKEN\",\"Inputs\":[\"SELF_GUID\",\"NOT_A_TOKEN\"]}")),
                    "E_HASH_TOKEN", "unknown input token rejected (closed set, spec §3.2)");
            });

            t.Case("validator: trigger-scoped tokens are rejected under triggers that cannot supply them", () =>
            {
                // TRIGGER_DAMAGE under ON_ABILITY_USED — no damage in scope.
                var set = RecipeParser.Parse(
                    "{\"SKILL_H\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_ABILITY_USED\"," +
                    "\"Conditions\":[{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"CF_SCOPE_SALT\",\"Inputs\":[\"SELF_GUID\",\"TRIGGER_DAMAGE\"]}]," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"n\",\"Delta\":1}]}}");
                RecipeValidator.Validate(set);
                Check.HasFinding(set, "E_HASH_TOKEN_SCOPE", "TRIGGER_DAMAGE needs a damage-carrying trigger (spec §2.1)");
            });

            t.Case("validator: duplicate (Salt, Inputs) pair across the set is an error; COMBAT_SEED absence warns", () =>
            {
                var dup = RecipeParser.Parse(
                    "{\"SKILL_H1\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_DAMAGE_TAKEN\"," +
                    "\"Conditions\":[{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"CF_DUP_SALT\",\"Inputs\":[\"SELF_GUID\",\"COMBAT_SEED\"]}]," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"n\",\"Delta\":1}]}," +
                    "\"SKILL_H2\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_DAMAGE_TAKEN\"," +
                    "\"Conditions\":[{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":30,\"Salt\":\"CF_DUP_SALT\",\"Inputs\":[\"SELF_GUID\",\"COMBAT_SEED\"]}]," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"n\",\"Delta\":1}]}}");
                RecipeValidator.Validate(dup);
                Check.HasFinding(dup, "E_HASH_SALT_DUP", "identical (Salt, Inputs) on two conditions rejected (spec §2)");

                var noSeed = ParseValid(HashRecipe("{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"CF_NOSEED_SALT\",\"Inputs\":[\"SELF_GUID\",\"COMBAT_ROUND\"]}"));
                Check.NoErrors(noSeed, "missing COMBAT_SEED is not an error");
                Check.HasFinding(noSeed, "W_HASH_NO_COMBAT_SEED", "…but it warns (OQ-SH3)");
            });

            t.Case("validator: STATE_HASH_CHANCE requires SchemaVersion 1.3", () =>
            {
                var set = RecipeParser.Parse(
                    "{\"SKILL_H\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_DAMAGE_TAKEN\"," +
                    "\"Conditions\":[{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"CF_GATE_SALT\",\"Inputs\":[\"SELF_GUID\",\"COMBAT_SEED\"]}]," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"n\",\"Delta\":1}]}}");
                RecipeValidator.Validate(set);
                Check.HasFinding(set, "E_SCHEMA_GATE", "1.3-only condition on a 1.1 recipe rejected");
            });

            t.Section("ON_DAMAGE_PENDING / DAMAGE_TAKEN_MULT (M-SH3 engine half)");

            t.Case("dispatch: SHIELDBEARER-shaped recipe emits DamageTakenMultAction with authored fields", () =>
            {
                var rig = Rig.Build(ShieldbearerJson(salt: "CF_SB_EMIT"), seed: 1);
                rig.Ctx.CombatSeedValue = 777;
                // Find a damage amount whose hash verdict passes at 20% for this rig's hero guid/round/HP.
                int amount = FindPassingAmount(rig, "CF_SB_EMIT", 20);
                var plan = rig.Dispatcher.OnDamagePending(rig.Ctx, new DamagePendingEvent { Victim = rig.Hero, Amount = amount });
                Check.Eq(1, plan.Count, "one action for a passing verdict");
                var act = plan[0] as DamageTakenMultAction;
                Check.True(act != null, "action is DamageTakenMultAction");
                Check.Eq(-25, act.Percent, "Percent");
                Check.Eq(2, act.MinDelta ?? 0, "MinDelta");
                Check.Eq(rig.Hero.Guid, act.TargetGuid, "target = victim/owner");
            });

            t.Case("dispatch: failing verdict emits nothing; identical event is perfectly correlated", () =>
            {
                var rig = Rig.Build(ShieldbearerJson(salt: "CF_SB_CORR"), seed: 1);
                rig.Ctx.CombatSeedValue = 777;
                int failing = FindFailingAmount(rig, "CF_SB_CORR", 20);
                for (int i = 0; i < 5; i++)
                {
                    var plan = rig.Dispatcher.OnDamagePending(rig.Ctx, new DamagePendingEvent { Victim = rig.Hero, Amount = failing });
                    Check.Eq(0, plan.Count, "failing verdict emits nothing, evaluation " + i.ToString());
                }
            });

            t.Case("dispatch: two independent dispatchers agree action-for-action (determinism)", () =>
            {
                var a = Rig.Build(ShieldbearerJson(salt: "CF_SB_TWIN"), seed: 9);
                var b = Rig.Build(ShieldbearerJson(salt: "CF_SB_TWIN"), seed: 9);
                a.Ctx.CombatSeedValue = 555; b.Ctx.CombatSeedValue = 555;
                for (int dmg = 1; dmg <= 40; dmg++)
                {
                    var pa = a.Dispatcher.OnDamagePending(a.Ctx, new DamagePendingEvent { Victim = a.Hero, Amount = dmg });
                    var pb = b.Dispatcher.OnDamagePending(b.Ctx, new DamagePendingEvent { Victim = b.Hero, Amount = dmg });
                    Check.Eq(pa.Count, pb.Count, "plan count at dmg " + dmg.ToString());
                }
            });

            t.Case("dispatch: zero draws — the shared stream is untouched by hash-gated evaluation", () =>
            {
                var rig = Rig.Build(ShieldbearerJson(salt: "CF_SB_DRAWS"), seed: 3);
                rig.Ctx.CombatSeedValue = 777;
                for (int dmg = 1; dmg <= 20; dmg++)
                    rig.Dispatcher.OnDamagePending(rig.Ctx, new DamagePendingEvent { Victim = rig.Hero, Amount = dmg });
                Check.Eq(0, rig.Rng.Draws, "no RNG draw on the ON_DAMAGE_PENDING path");
            });

            t.Case("resolver safety: a throwing stat read degrades to '0' and warns once per (salt, token)", () =>
            {
                var rig = Rig.Build(ShieldbearerJson(salt: "CF_SB_THROW"), seed: 4);
                rig.Ctx.CombatSeedValue = 777;
                rig.Hero.ThrowOnGetStat = true;
                rig.Dispatcher.OnDamagePending(rig.Ctx, new DamagePendingEvent { Victim = rig.Hero, Amount = 7 });
                rig.Dispatcher.OnDamagePending(rig.Ctx, new DamagePendingEvent { Victim = rig.Hero, Amount = 7 });
                int warns = 0;
                for (int i = 0; i < rig.Log.Lines.Count; i++)
                    if (rig.Log.Lines[i].StartsWith("WARN", StringComparison.Ordinal) &&
                        rig.Log.Lines[i].Contains("SELF_HP")) warns++;
                Check.Eq(1, warns, "exactly one warn for the throwing SELF_HP resolver (spec §3.3)");
            });

            t.Case("validator: DAMAGE_TAKEN_MULT is ON_DAMAGE_PENDING-only and the trigger is RNG-free", () =>
            {
                var wrongTrigger = RecipeParser.Parse(
                    "{\"SKILL_D\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_DAMAGE_TAKEN\"," +
                    "\"Effects\":[{\"Type\":\"DAMAGE_TAKEN_MULT\",\"Percent\":-25}]}}");
                RecipeValidator.Validate(wrongTrigger);
                Check.HasFinding(wrongTrigger, "E_EFFECT_TRIGGER_SCOPE", "DAMAGE_TAKEN_MULT off its trigger rejected");

                var chanced = RecipeParser.Parse(
                    "{\"SKILL_D\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_DAMAGE_PENDING\",\"ProcChance\":50," +
                    "\"Effects\":[{\"Type\":\"DAMAGE_TAKEN_MULT\",\"Percent\":-25}]}}");
                RecipeValidator.Validate(chanced);
                Check.HasFinding(chanced, "E_DMGPEND_RNG", "ProcChance on ON_DAMAGE_PENDING rejected (the park's original hazard)");

                var wrongEffect = RecipeParser.Parse(
                    "{\"SKILL_D\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_DAMAGE_PENDING\"," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Status\":\"S\",\"Duration\":1}]}}");
                RecipeValidator.Validate(wrongEffect);
                Check.HasFinding(wrongEffect, "E_DMGPEND_EFFECT", "only DAMAGE_TAKEN_MULT/COUNTER_* may ride ON_DAMAGE_PENDING");
            });
        }

        // ---------------------------------------------------------------- helpers

        private static RecipeSet ParseValid(string json)
        {
            var set = RecipeParser.Parse(json);
            RecipeValidator.Validate(set);
            return set;
        }

        /// <summary>A 1.3 recipe on ON_DAMAGE_TAKEN carrying the supplied condition JSON.</summary>
        private static string HashRecipe(string conditionJson)
        {
            return "{\"SKILL_H\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_DAMAGE_TAKEN\"," +
                   "\"Conditions\":[" + conditionJson + "]," +
                   "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"n\",\"Delta\":1}]}}";
        }

        private static string ShieldbearerJson(string salt)
        {
            return "{\"SKILL_CF_TRAIT_SB\":{\"SchemaVersion\":\"1.3\",\"Trigger\":\"ON_DAMAGE_PENDING\"," +
                   "\"Conditions\":[{\"Type\":\"STATE_HASH_CHANCE\",\"Percent\":20,\"Salt\":\"" + salt + "\"," +
                   "\"Inputs\":[\"SELF_GUID\",\"COMBAT_ROUND\",\"SELF_HP\",\"TRIGGER_DAMAGE\",\"COMBAT_SEED\"]}]," +
                   "\"Effects\":[{\"Type\":\"DAMAGE_TAKEN_MULT\",\"Percent\":-25,\"MinDelta\":2}]}}";
        }

        private static int FindPassingAmount(Rig rig, string salt, int percent)
        {
            for (int dmg = 1; dmg < 500; dmg++)
                if (VerdictFor(rig, salt, dmg, percent)) return dmg;
            throw new AssertFailed("no passing damage amount below 500 — hash badly skewed");
        }

        private static int FindFailingAmount(Rig rig, string salt, int percent)
        {
            for (int dmg = 1; dmg < 500; dmg++)
                if (!VerdictFor(rig, salt, dmg, percent)) return dmg;
            throw new AssertFailed("no failing damage amount below 500 — hash badly skewed");
        }

        private static bool VerdictFor(Rig rig, string salt, int damage, int percent)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return StateHashChance.Verdict(salt, new[]
            {
                rig.Hero.Guid,
                rig.Ctx.Round.ToString(ci),
                rig.Hero.GetStat("HP").ToString(ci),
                damage.ToString(ci),
                rig.Ctx.CombatSeedValue.ToString(ci)
            }, percent);
        }
    }
}
