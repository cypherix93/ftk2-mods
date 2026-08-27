using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;
using ClassForge.Recipes.Parsing;

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
            RunSummonDraws(t);
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

            t.Case("determinism: guid VALUES are not an ordering input; only ROSTER POSITION is", () =>
            {
                // This replaces an older "shuffled entity enumeration order produces the identical plan"
                // case, whose premise was that ordering came from ascending ordinal Entity.Guid and was
                // therefore permutation-proof. That premise was a co-op desync: Entity.Create() mints the
                // guid with System.Guid.NewGuid(), so two peers simulating the same fight held DIFFERENT
                // guids and any tie-broken pick chose a different combatant on each of them. Ordering is
                // now roster position, which every peer computes identically -- so the property worth
                // testing flipped: renaming the guids must change nothing, and permuting the roster is a
                // genuinely different roster.

                // each rig is single-use: RunStream mutates the per-battle runtime
                var normal = Rig.Build(Book(), 4242);
                normal.GrantAlso(normal.Ally);
                string baseline = RunStream(normal);
                Check.True(baseline.Length > 0, "the stream actually produced actions");

                // ---- 1. Rename every guid so ordinal-GUID order is the exact REVERSE of roster order.
                // If any guid value still leaked into ordering, this plan would differ.
                var renamed = Rig.Build(Book(), 4242);
                renamed.GrantAlso(renamed.Ally);
                renamed.Hero.Guid = "Z_HERO";       // roster slot 0, guid sorts last
                renamed.Ally.Guid = "Y_ALLY";       // roster slot 1
                renamed.Foe.Guid = "X_FOE";         // roster slot 2
                renamed.Foe2.Guid = "W_FOE2";       // roster slot 3, guid sorts first
                string renamedLog = RunStream(renamed)
                    .Replace("Z_HERO", "A_HERO").Replace("Y_ALLY", "B_ALLY")
                    .Replace("X_FOE", "C_FOE").Replace("W_FOE2", "D_FOE2");
                Check.Eq(baseline, renamedLog, "guid VALUES are not an input to ordering or to any roll");

                // ---- 2. Permuting the roster IS a different roster, so the plan may legitimately differ --
                // but the DRAW COUNT must not, because draw count is what desyncs lockstep permanently.
                var reference = Rig.Build(Book(), 4242);
                reference.GrantAlso(reference.Ally);
                RunStream(reference);

                var reversed = Rig.Build(Book(), 4242);
                reversed.GrantAlso(reversed.Ally);
                reversed.Ctx.EntityList.Reverse();
                RunStream(reversed);
                Check.Eq(reference.Rng.Draws, reversed.Rng.Draws,
                    "roster permutation must not change the shared-stream DRAW COUNT");

                var rotated = Rig.Build(Book(), 4242);
                rotated.GrantAlso(rotated.Ally);
                var first = rotated.Ctx.EntityList[0];
                rotated.Ctx.EntityList.RemoveAt(0);
                rotated.Ctx.EntityList.Add(first);
                RunStream(rotated);
                Check.Eq(reference.Rng.Draws, rotated.Rng.Draws, "rotation must not change the draw count either");
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

            t.Case("draw count: the proc gate costs the same whether the owner is AI- or player-controlled", () =>
            {
                // ICombatEntity.IsAiControlled selects AiProcChance over ProcChance
                // (RecipeDispatcher.EvaluateRecipe). With the pair on the SAME side of 100 the selection
                // cannot move the draw count, which is the property that matters: the adapter derives
                // IsAiControlled from CharacterComponent.GroupIndex, and a companion or follower can change
                // sides mid-run (CombatHelper.cs:2219, AdventureHelper.cs:748).
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"ProcChance\":60,\"AiProcChance\":25," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}";

                var player = Rig.Build(json, 11);
                player.Hero.Ai = false;
                var ai = Rig.Build(json, 11);
                ai.Hero.Ai = true;

                for (int i = 0; i < 5; i++) { player.Fire(TriggerKind.ON_KILL); ai.Fire(TriggerKind.ON_KILL); }
                Check.Eq(5, player.Rng.Draws, "player-controlled owner: one draw per eligible evaluation");
                Check.Eq(player.Rng.Draws, ai.Rng.Draws,
                    "AI-controlled owner took a different NUMBER of draws for the same events");
            });

            t.Case("draw count: the proc gate costs the same at every ProcChance value below 100", () =>
            {
                // GameRandom.NextChance(decimal) always costs exactly one draw whatever the chance
                // (GameRandom.cs:191), so tuning a percentage must never be a draw-count change.
                int reference = -1;
                foreach (int pct in new[] { 0, 1, 25, 50, 99 })
                {
                    string json =
                        "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                        "\"ProcChance\":" + pct.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ",\"AiProcChance\":" + pct.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ",\"Effects\":[" + J.SelfStatusEffect + "]}}";
                    var rig = Rig.Build(json, 3);
                    for (int i = 0; i < 4; i++) rig.Fire(TriggerKind.ON_KILL);
                    if (reference < 0) reference = rig.Rng.Draws;
                    Check.Eq(reference, rig.Rng.Draws, "ProcChance " + pct.ToString() + " moved the draw count");
                }
                Check.Eq(4, reference, "one draw per eligible evaluation, four evaluations");
            });

            t.Case("draw count: a ProcChance/AiProcChance pair straddling 100 forks the count, and the validator says so", () =>
            {
                // This documents the hazard rather than hiding it. `chance >= 100` short-circuits to
                // proc:true WITHOUT a draw, so 100/50 costs 0 draws for a player owner and 1 for an AI
                // owner. It is SAFE today (the discriminator is replicated GroupIndex) but it makes draw
                // cost a function of a per-unit property, so the validator warns and shipped packs keep the
                // pair on one side of 100.
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
                    "\"ProcChance\":100,\"AiProcChance\":50," +
                    "\"Effects\":[" + J.SelfStatusEffect + "]}}";

                var player = Rig.Build(json, 11); player.Hero.Ai = false;
                var ai = Rig.Build(json, 11); ai.Hero.Ai = true;
                player.Fire(TriggerKind.ON_KILL);
                ai.Fire(TriggerKind.ON_KILL);
                Check.Eq(0, player.Rng.Draws, "ProcChance 100 short-circuits without drawing");
                Check.Eq(1, ai.Rng.Draws, "AiProcChance 50 must roll -- this IS the fork being warned about");

                var set = RecipeParser.Parse(json);
                RecipeValidator.Validate(set);
                Check.NoErrors(set, "a straddling pair is a warning, never an error");
                Check.HasFinding(set, "W_PROC_CHANCE_DRAW_FORK", "the straddling pair is reported");
            });

            t.Case("determinism: multi-owner T7 draw order follows ascending ROSTER ORDINAL, not guid", () =>
            {
                string json =
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ENEMY_ABILITY_RESOLVED\",\"ProcChance\":50," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S\"}]}}";
                var rig = Rig.Build(json, 1);
                rig.GrantAlso(rig.Ally);
                // Reversing the roster makes the two orderings DISAGREE, which is what makes this test
                // discriminating: roster order is now [D_FOE2, C_FOE, B_ALLY, A_HERO], so the lowest roster
                // ordinal among the two owners is B_ALLY -- while the lowest ordinal GUID is still A_HERO.
                rig.Ctx.EntityList.Reverse();
                rig.Rng.ScriptChance(true, false);   // first draw -> lowest-ordinal owner
                var p = rig.Dispatcher.OnEnemyAbilityResolved(rig.Ctx, new EnemyAbilityResolvedEvent
                { Origin = rig.Foe, Target = rig.Hero, AbilityId = "AB_SLASH", RollTier = RollTier.PERFECT });
                Check.PlanCount(p, 1, "only the first owner's roll succeeded");
                Check.Eq("B_ALLY", p[0].OwnerGuid,
                    "the first draw went to the lowest ROSTER ORDINAL (A_HERO would mean guid order leaked back in)");
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

        // =====================================================================================
        // W1-J — the operator's actual goal, stated as executable draw counts: DON'T DESYNC ON SUMMONS.
        //
        // A summon is the highest-consequence thing ClassForge can do to combat, because it changes
        // CombatState.Entities.Count on every peer -- and Entities.Count is what AIHelper's targetable-tile
        // list is built from, which is exactly how many draws GameRandom.ShuffleList takes from the SHARED
        // stream (AIHelper.cs:507-511; ShuffleList takes exactly _list.Count draws). If one peer summons
        // and another does not, or if the two disagree about HOW MANY draws the summon itself cost, the
        // streams are offset from that point on and nothing reports it: CombatState is [JsonIgnore] on
        // GameRunData, so combat-side divergence is not in the vendor's desync MD5.
        //
        // The invariant these tests pin: firing a summon costs the same number of shared-stream draws
        // regardless of proc chance value, of AI-vs-player control, and of any AI targeting state -- and
        // the SUMMON effect itself costs ZERO, so the entire draw cost of a summoning recipe is its proc
        // gate and nothing else.
        //
        // Already audited clean elsewhere and deliberately NOT re-tested here: FindFreeTileForGroup
        // (RecipeActionExecutor) walks CombatState.Entities by index and resolves through
        // TileOrder.SelectPlacement, a single MIN scan provably independent of collection order; and the
        // DOWNED gate derives from replicated CF_POKE_HP, which is what keeps Entities.Count -- and
        // therefore ShuffleList's draw count -- equal across peers. See TileOrderTests.
        // =====================================================================================

        private const string SummonJson =
            "{\"SKILL_SUMMON\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\"," +
            "\"ProcChance\":{P},\"AiProcChance\":{A}," +
            "\"Effects\":[{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"SPECIFIC\"," +
            "\"CharacterConfig\":\"CF_SKELETON_WARRIOR\",\"Count\":{C}}]}}";

        private static string Summon(int procChance, int aiProcChance, int count)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return SummonJson
                .Replace("{P}", procChance.ToString(inv))
                .Replace("{A}", aiProcChance.ToString(inv))
                .Replace("{C}", count.ToString(inv));
        }

        private static void RunSummonDraws(TestRunner t)
        {
            t.Section("W1-J summons: the draw cost of a summon is a constant of the recipe");

            t.Case("summon draws: the SUMMON effect itself costs ZERO draws, at every Count", () =>
            {
                // If the effect drew, Count would make the summon's draw cost a function of an authored
                // number -- and a peer that clamped the roster differently would then fork the stream.
                foreach (int count in new[] { 1, 2, 3, Vocabulary.SummonCountCap })
                {
                    var rig = Rig.Build(Summon(100, 100, count), 7);
                    var plan = rig.Fire(TriggerKind.ON_KILL);
                    Check.PlanCount(plan, count, "Count expands to N SummonActions");
                    Check.Eq(0, rig.Rng.Draws,
                        "Count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ": SUMMON planning must take zero shared-stream draws (ProcChance 100 takes none " +
                        "either), so the whole recipe is draw-free");
                }
            });

            t.Case("summon draws: the cost is identical at every ProcChance below 100", () =>
            {
                // NextChance(decimal) always costs exactly one draw whatever the chance (GameRandom.cs:191),
                // so a balance tweak to a summon's proc rate must never be a draw-count change.
                int reference = -1;
                foreach (int pct in new[] { 1, 10, 25, 50, 75, 99 })
                {
                    var rig = Rig.Build(Summon(pct, pct, 2), 3);
                    for (int i = 0; i < 4; i++) rig.Fire(TriggerKind.ON_KILL);
                    if (reference < 0) reference = rig.Rng.Draws;
                    Check.Eq(reference, rig.Rng.Draws,
                        "ProcChance " + pct.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " moved the summon's draw count");
                }
                Check.Eq(4, reference, "one proc draw per eligible evaluation, four evaluations");
            });

            t.Case("summon draws: an AI-controlled summoner costs exactly what a player-controlled one does", () =>
            {
                // The reachable per-unit fork: RecipeDispatcher picks AiProcChance vs ProcChance on
                // ICombatEntity.IsAiControlled. Keeping the pair on one side of 100 (which every shipped
                // pack does, and RecipeValidator's W_PROC_CHANCE_DRAW_FORK warns when it does not) makes
                // the count a constant. A companion changing sides mid-combat (CombatHelper.cs:2219) then
                // cannot move it.
                foreach (int pct in new[] { 20, 100 })
                {
                    var player = Rig.Build(Summon(pct, pct, 2), 11); player.Hero.Ai = false;
                    var ai = Rig.Build(Summon(pct, pct, 2), 11); ai.Hero.Ai = true;

                    for (int i = 0; i < 5; i++) { player.Fire(TriggerKind.ON_KILL); ai.Fire(TriggerKind.ON_KILL); }

                    Check.Eq(player.Rng.Draws, ai.Rng.Draws,
                        "ProcChance/AiProcChance " + pct.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ": an AI-controlled summoner took a different NUMBER of draws than a " +
                        "player-controlled one for the same events");
                }
            });

            t.Case("summon draws: the whole evaluation is draw-identical whether or not the summon procs", () =>
            {
                // Both peers must reach the same stream position even when the roll disagrees with what a
                // hypothetical other peer rolled -- i.e. the FAILING branch must not be cheaper than the
                // succeeding one. (It is the same gate either way; this pins that no summon-side effect
                // sneaks a draw into the success path.)
                var proc = Rig.Build(Summon(50, 50, 3), 4);
                proc.Rng.ScriptChance(true);
                var noProc = Rig.Build(Summon(50, 50, 3), 4);
                noProc.Rng.ScriptChance(false);

                Check.PlanCount(proc.Fire(TriggerKind.ON_KILL), 3, "the summon fired");
                Check.PlanCount(noProc.Fire(TriggerKind.ON_KILL), 0, "the summon did not fire");
                Check.Eq(proc.Rng.Draws, noProc.Rng.Draws,
                    "a summon that PROCS must cost the same number of draws as one that does not");
                Check.Eq(1, proc.Rng.Draws, "exactly the one proc-gate draw");
            });

            t.Case("summon draws: two independent dispatchers replay the identical draw sequence", () =>
            {
                // The lockstep property itself: same book, same seed, same events -> same stream position
                // and same plan, in two fully independent engine instances (no static state anywhere).
                var a = Rig.Build(Summon(35, 35, 2), 99);
                var b = Rig.Build(Summon(35, 35, 2), 99);
                var planA = new List<EngineAction>();
                var planB = new List<EngineAction>();
                for (int round = 1; round <= 6; round++)
                {
                    a.Ctx.RoundValue = round; b.Ctx.RoundValue = round;
                    planA.AddRange(a.Fire(TriggerKind.ON_KILL));
                    planB.AddRange(b.Fire(TriggerKind.ON_KILL));
                }
                Check.Eq(a.Rng.Draws, b.Rng.Draws, "two peers took a different number of draws");
                Check.Eq(ActionLog.Render(planA), ActionLog.Render(planB), "two peers summoned differently");
            });

            t.Case("summon draws: AI targeting neutrality holds the OTHER half constant, at every tendency", () =>
            {
                // The half that lives outside the recipe engine. A summon lands a new body on the board,
                // and the next AI turn targets over it; AiTargetingDraws is the model AiDrawNeutrality
                // implements, and it must report a constant regardless of tendency, strictness, or whether
                // a Focus Fire order supplied the position before GetPreferredTarget was consulted.
                int reference = -1;
                foreach (bool ordered in new[] { true, false })
                    foreach (bool isNone in new[] { true, false })
                        foreach (bool isStrict in new[] { true, false })
                        {
                            int draws = ClassForge.Core.Rng.AiTargetingDraws.NeutralisedTargetingDraws(ordered, isNone, isStrict);
                            if (reference < 0) reference = draws;
                            Check.Eq(reference, draws,
                                "ordered=" + ordered + " none=" + isNone + " strict=" + isStrict +
                                ": AI targeting over a board containing a summon cost a different number " +
                                "of draws");
                        }
                Check.Eq(1, reference, "exactly one draw for target preference, always");
            });
        }
    }
}
