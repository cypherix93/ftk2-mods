using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Loot;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// M-EM4 — reward-half emission through the loot-grant verb engine (Encounter Modifiers spec §11;
    /// loot-grant verb spec §6.3 item 6). <see cref="LootDeltaComputer.Compute"/>'s optional
    /// <c>modifierRewards</c>/<c>preGrantList</c> parameters append the active modifier's reward ops, in
    /// fixed order, AFTER every owner-recipe op — the Mode-M mirror this milestone rides unchanged.
    /// </summary>
    public static class ModifierRewardsLootTests
    {
        private static IReadOnlyList<ICombatEntity> Owner(string guid, params string[] recipeIds)
        {
            var e = new FakeEntity(guid, 0);
            for (int i = 0; i < recipeIds.Length; i++) e.PassiveList.Add(recipeIds[i]);
            return new List<ICombatEntity> { e };
        }

        private static List<PendingThing> PreGrantList(params (string ConfigName, int Stack)[] rows)
        {
            var list = new List<PendingThing>();
            for (int i = 0; i < rows.Length; i++)
                list.Add(new PendingThing { Id = "pre-" + i, ConfigName = rows[i].ConfigName, Stack = rows[i].Stack });
            return list;
        }

        public static void Run(TestRunner t)
        {
            t.Section("M-EM4: reward-half emission (spec §11 / loot-grant verb §6.3 item 6)");

            t.Case("modifierRewards == null -> zero reward ops, zero extra draws (regression: identical to pre-M-EM4)", () =>
            {
                var recipes = new RecipeSet(); // no owner recipes -- isolate the rewards-only path
                var rng = new FakeRandom(1);
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null);
                Check.Eq(0, ops.Count, "no ops at all");
                Check.Eq(0, rng.Draws, "zero draws");
            });

            t.Case("XpBonusPercent only -> one SCALE_STACK{PARTY_XP}, zero draws", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { XpBonusPercent = 10 };
                var rng = new FakeRandom(1);
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, null);
                Check.Eq(1, ops.Count, "one op");
                Check.True(ops[0].Kind == LootOpKind.SCALE_STACK, "SCALE_STACK");
                Check.Eq("PARTY_XP", ops[0].ConfigName, "PARTY_XP");
                Check.Eq(10, ops[0].Percent, "+10%");
                Check.Eq(0, rng.Draws, "zero draws -- SCALE_STACK never rolls");
            });

            t.Case("GoldBonusPercent only -> one SCALE_STACK{CURRENCY_ADVENTURE}, zero draws", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { GoldBonusPercent = 25 };
                var rng = new FakeRandom(1);
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, null);
                Check.Eq(1, ops.Count, "one op");
                Check.True(ops[0].Kind == LootOpKind.SCALE_STACK, "SCALE_STACK");
                Check.Eq("CURRENCY_ADVENTURE", ops[0].ConfigName, "CURRENCY_ADVENTURE");
                Check.Eq(25, ops[0].Percent, "+25%");
                Check.Eq(0, rng.Draws, "zero draws");
            });

            t.Case("Xp + Gold together -> fixed order (Xp then Gold), zero draws", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { XpBonusPercent = 10, GoldBonusPercent = 25 };
                var rng = new FakeRandom(1);
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, null);
                Check.Eq(2, ops.Count, "two ops");
                Check.Eq("PARTY_XP", ops[0].ConfigName, "op0 is XP (fixed order)");
                Check.Eq("CURRENCY_ADVENTURE", ops[1].ConfigName, "op1 is Gold (fixed order)");
                Check.Eq(0, rng.Draws, "still zero draws -- neither SCALE_STACK rolls");
            });

            t.Case("ExtraLootChancePercent <= 0 -> zero draws, zero ops (pure function of the replicated selection)", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { ExtraLootChancePercent = 0 };
                var rng = new FakeRandom(1);
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, PreGrantList(("HERB_LUCKY", 1)));
                Check.Eq(0, ops.Count, "no ops");
                Check.Eq(0, rng.Draws, "zero draws -- the gate is never reached");
            });

            t.Case("ExtraLootChancePercent > 0, gate fails -> exactly ONE draw, zero ops (Treasure-Guarded miss)", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { ExtraLootChancePercent = 20 };
                var rng = new FakeRandom(1);
                rng.ScriptChance(false);
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, PreGrantList(("HERB_LUCKY", 1)));
                Check.Eq(0, ops.Count, "gate failed -> no op");
                Check.Eq(1, rng.Draws, "exactly one draw (the gate) -- Treasure-Guarded's whole draw budget");
                Check.True(rng.LastChance.HasValue && rng.LastChance.Value == 0.20m, "NextChance(pct/100) = 0.20");
            });

            t.Case("ExtraLootChancePercent success, non-currency entry present -> ADD_ITEM duplicate, deterministic id, Stack 1", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { ExtraLootChancePercent = 20 };
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                var pre = PreGrantList(("CURRENCY_ADVENTURE", 40), ("XP", 12), ("SWORD_IRON", 1), ("HERB_COMMON", 2));
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, pre);

                Check.Eq(1, ops.Count, "one op");
                Check.Eq(1, rng.Draws, "exactly one draw (the gate) -- the duplicate itself is zero-draw");
                Check.True(ops[0].Kind == LootOpKind.ADD_ITEM, "ADD_ITEM");
                Check.Eq("SWORD_IRON", ops[0].ConfigName, "first NON-CURRENCY entry (skips CURRENCY_ADVENTURE and XP)");
                Check.Eq(1, ops[0].Stack, "Stack 1");
                Check.Eq(LootThingId.Mint("GK", 0), ops[0].ThingId, "deterministic id at this op's own index (0)");
            });

            t.Case("ExtraLootChancePercent success, no non-currency entry -> ADD_GOLD 15 fallback", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { ExtraLootChancePercent = 20 };
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                var pre = PreGrantList(("CURRENCY_ADVENTURE", 40), ("XP", 12)); // currency-only pre-grant list
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, pre);

                Check.Eq(1, ops.Count, "one op");
                Check.True(ops[0].Kind == LootOpKind.ADD_GOLD, "ADD_GOLD fallback");
                Check.Eq(15, ops[0].Amount, "+15 gold (EOR L16646-16662 semantics)");
            });

            t.Case("ExtraLootChancePercent success, null/empty pre-grant list -> ADD_GOLD 15 fallback", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { ExtraLootChancePercent = 20 };
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, null);
                Check.Eq(1, ops.Count, "one op");
                Check.True(ops[0].Kind == LootOpKind.ADD_GOLD, "ADD_GOLD fallback when there is no pre-grant snapshot at all");
                Check.Eq(15, ops[0].Amount, "+15 gold");
            });

            t.Case("all three rewards together -> fixed order Xp, Gold, ExtraLoot; exactly one draw total", () =>
            {
                var recipes = new RecipeSet();
                var rewards = new ModifierRewardsInput { XpBonusPercent = 10, GoldBonusPercent = 25, ExtraLootChancePercent = 20 };
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                var pre = PreGrantList(("CURRENCY_ADVENTURE", 40), ("HERB_LUCKY", 1));
                var ops = LootDeltaComputer.Compute(recipes, Owner("A_HERO"), "GK", rng, null, rewards, pre);

                Check.Eq(3, ops.Count, "three ops");
                Check.Eq("PARTY_XP", ops[0].ConfigName, "op0 XP");
                Check.Eq("CURRENCY_ADVENTURE", ops[1].ConfigName, "op1 Gold");
                Check.True(ops[2].Kind == LootOpKind.ADD_ITEM, "op2 the extra-loot draw's result");
                Check.Eq("HERB_LUCKY", ops[2].ConfigName, "duplicates the first non-currency entry");
                Check.Eq(1, rng.Draws, "exactly one draw total (VETERAN-style Xp/Gold recipes take none; only the extra-loot gate rolls)");
            });

            // -------------------------------------------------------------------------------
            t.Section("M-EM4: integration -- owner-recipe ops (consumers 1-4) then modifier-reward ops, fixed order");

            const string TreasureSenseJson =
                "{\"SKILL_CF_TRAIT_TREASURE_SENSE_LOOT\":{\"SchemaVersion\":\"1.2\"," +
                "\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100,\"Effects\":[" +
                "{\"Type\":\"GOLD_GRANT\",\"MinGold\":8,\"MaxGold\":8}]}}"; // Min==Max -> zero-draw, deterministic

            t.Case("owner-recipe ops come first, unmodified; modifier-reward ops are appended after, in fixed order", () =>
            {
                var set = RecipeParser.Parse(TreasureSenseJson);
                Check.NoErrors(set, "TREASURE_SENSE parses clean");

                var rewards = new ModifierRewardsInput { XpBonusPercent = 10, GoldBonusPercent = 25, ExtraLootChancePercent = 20 };
                var rng = new FakeRandom(1);
                rng.ScriptChance(true); // the extra-loot gate; TREASURE_SENSE itself is ProcChance 100 (zero draws)
                var pre = PreGrantList(("CURRENCY_ADVENTURE", 40), ("SCROLL_FIRE", 1));

                var ops = LootDeltaComputer.Compute(
                    set, Owner("A_HERO", "SKILL_CF_TRAIT_TREASURE_SENSE_LOOT"), "GK", rng, null, rewards, pre);

                Check.Eq(4, ops.Count, "1 owner op + 3 reward ops");
                Check.True(ops[0].Kind == LootOpKind.ADD_GOLD && ops[0].Amount == 8, "op0: TREASURE_SENSE's flat 8 gold (owner recipe, unaffected by rewards)");
                Check.Eq("PARTY_XP", ops[1].ConfigName, "op1: Xp reward");
                Check.Eq("CURRENCY_ADVENTURE", ops[2].ConfigName, "op2: Gold reward");
                Check.True(ops[3].Kind == LootOpKind.ADD_ITEM && ops[3].ConfigName == "SCROLL_FIRE", "op3: extra-loot duplicate");
            });

            t.Case("determinism: identical inputs (including modifierRewards/preGrantList) -> identical OpsHash", () =>
            {
                var set = RecipeParser.Parse(TreasureSenseJson);
                var rewards = new ModifierRewardsInput { XpBonusPercent = 10, ExtraLootChancePercent = 20 };
                var pre = PreGrantList(("CURRENCY_ADVENTURE", 40), ("SCROLL_FIRE", 1));

                var rngA = new FakeRandom(7); rngA.ScriptChance(true);
                var opsA = LootDeltaComputer.Compute(set, Owner("A_HERO", "SKILL_CF_TRAIT_TREASURE_SENSE_LOOT"), "GK", rngA, null, rewards, pre);

                var rngB = new FakeRandom(7); rngB.ScriptChance(true);
                var opsB = LootDeltaComputer.Compute(set, Owner("A_HERO", "SKILL_CF_TRAIT_TREASURE_SENSE_LOOT"), "GK", rngB, null, rewards, pre);

                Check.Eq(LootGrantCodec.ComputeOpsHash(opsA), LootGrantCodec.ComputeOpsHash(opsB), "identical inputs -> identical Ops");
            });
        }
    }
}
