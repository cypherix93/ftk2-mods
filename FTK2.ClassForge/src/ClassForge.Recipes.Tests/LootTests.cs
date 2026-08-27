using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Loot;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// Offline core tests for the loot-grant verb (M-LG1) —
    /// docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §8.1 items 1-7, adapted per the
    /// verification wave's GATE A/GATE B resolutions (DrawMark -> ListDigest; candidate-source seam).
    /// </summary>
    public static class LootTests
    {
        // ----------------------------------------------------------------------------- fixtures

        private const string ScavengerJson =
            "{\"SKILL_CF_TRAIT_SCAVENGER_LOOT\":{\"SchemaVersion\":\"1.2\",\"Enabled\":true," +
            "\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":25,\"PickOneEffect\":true,\"Effects\":[" +
            "{\"Type\":\"GOLD_GRANT\",\"MinGold\":8,\"MaxGold\":20}," +
            "{\"Type\":\"ITEM_TAG_GRANT\",\"Tag\":\"HERB\",\"Rarity\":\"COMMON\"}]}}";

        private const string TreasureSenseJson =
            "{\"SKILL_CF_TRAIT_TREASURE_SENSE_LOOT\":{\"SchemaVersion\":\"1.2\"," +
            "\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":20,\"Effects\":[" +
            "{\"Type\":\"GOLD_GRANT\",\"MinGold\":8,\"MaxGold\":20}]}}";

        private const string ScholarsHabitJson =
            "{\"SKILL_CF_TRAIT_SCHOLARS_HABIT_LOOT\":{\"SchemaVersion\":\"1.2\"," +
            "\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":20,\"Effects\":[" +
            "{\"Type\":\"ITEM_TAG_GRANT\",\"Tag\":\"SCROLL\"}]}}";

        private const string OfScavengingJson =
            "{\"SKILL_CF_AFFIX_OF_SCAVENGING_LOOT\":{\"SchemaVersion\":\"1.2\"," +
            "\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":10,\"PickOneEffect\":true,\"Effects\":[" +
            "{\"Type\":\"GOLD_GRANT\",\"MinGold\":5,\"MaxGold\":10}," +
            "{\"Type\":\"ITEM_TAG_GRANT\",\"Tag\":\"HERB\",\"Rarity\":\"COMMON\"}]}}";

        private static FakeEntity Owner(string guid, params string[] recipeIds)
        {
            var e = new FakeEntity(guid, 0);
            for (int i = 0; i < recipeIds.Length; i++) e.PassiveList.Add(recipeIds[i]);
            return e;
        }

        private static IReadOnlyList<ICombatEntity> One(ICombatEntity e)
        {
            return new List<ICombatEntity> { e };
        }

        public static void Run(TestRunner t)
        {
            RunDeterminism(t);
            RunGoldenDrawSequence(t);
            RunIdempotency(t);
            RunCodec(t);
            RunValidator(t);
            RunOpApplication(t);
        }

        // ----------------------------------------------------------------------------- 1. determinism

        private static void RunDeterminism(TestRunner t)
        {
            t.Section("loot: determinism (harness item 1)");

            t.Case("GrantKey: identical inputs -> identical key; key-list order does not matter", () =>
            {
                var enemies = new[] { "E2", "E1" };
                var owners = new[] { "O2", "O1" };
                string digest = "sha256:" + "ab".PadRight(64, 'c');

                string k1 = LootGrantKey.ComputeGrantKey(100, enemies, digest, owners);
                string k2 = LootGrantKey.ComputeGrantKey(100, enemies, digest, owners);
                Check.Eq(k1, k2, "same inputs -> same key");
                Check.Eq(16, k1.Length, "GrantKey is 16 hex chars (first 8 bytes)");

                string reordered = LootGrantKey.ComputeGrantKey(100, new[] { "E1", "E2" }, digest, new[] { "O1", "O2" });
                Check.Eq(k1, reordered, "key order does not matter (sorted internally)");
            });

            t.Case("GrantKey: every input dimension perturbing produces a different key", () =>
            {
                var enemies = new[] { "E1", "E2" };
                var owners = new[] { "O1", "O2" };
                const string digest = "sha256:base";
                string baseline = LootGrantKey.ComputeGrantKey(100, enemies, digest, owners);

                Check.True(baseline != LootGrantKey.ComputeGrantKey(101, enemies, digest, owners), "CombatSeed perturbation");
                Check.True(baseline != LootGrantKey.ComputeGrantKey(100, new[] { "E1", "E3" }, digest, owners), "enemy-set perturbation");
                Check.True(baseline != LootGrantKey.ComputeGrantKey(100, enemies, "sha256:other", owners), "ListDigest perturbation");
                Check.True(baseline != LootGrantKey.ComputeGrantKey(100, enemies, digest, new[] { "O1", "O3" }), "owner-set perturbation");
            });

            t.Case("grantSeed: identical inputs -> identical seed; perturbation changes it", () =>
            {
                var enemies = new[] { "E1", "E2" };
                int s1 = LootGrantKey.ComputeGrantSeed(42, "sha256:x", enemies);
                int s2 = LootGrantKey.ComputeGrantSeed(42, "sha256:x", enemies);
                Check.Eq(s1, s2, "same inputs -> same seed");
                int s3 = LootGrantKey.ComputeGrantSeed(43, "sha256:x", enemies);
                Check.True(s1 != s3, "CombatSeed perturbation changes grantSeed");
            });

            t.Case("ListDigest: preserves list order (not re-sorted)", () =>
            {
                var forward = new List<PendingThing>
                {
                    new PendingThing { ConfigName = "ITEM_A", Stack = 1 },
                    new PendingThing { ConfigName = "ITEM_B", Stack = 2 }
                };
                var reversed = new List<PendingThing>
                {
                    new PendingThing { ConfigName = "ITEM_B", Stack = 2 },
                    new PendingThing { ConfigName = "ITEM_A", Stack = 1 }
                };
                string da = LootGrantKey.ComputeListDigest(forward);
                string db = LootGrantKey.ComputeListDigest(reversed);
                Check.True(da != db, "list order changes the digest");
                Check.Eq(da, LootGrantKey.ComputeListDigest(forward), "same order -> same digest");
            });

            t.Case("Ops/OpsHash: identical (recipes, owners, grantKey) + a fresh same-seeded stream -> identical output, over many scenarios", () =>
            {
                var candidates = new FakeCandidateSource().Add("HERB", "COMMON", "HERB_B", "HERB_A").Add("SCROLL", null, "SCROLL_A");
                var set = RecipeParser.Parse(ScavengerJson);
                Check.NoErrors(set, "scavenger parses clean");

                for (int seed = 1; seed <= 50; seed++)
                {
                    var ownerA = Owner("A_HERO", "SKILL_CF_TRAIT_SCAVENGER_LOOT");
                    var ownerB = Owner("A_HERO", "SKILL_CF_TRAIT_SCAVENGER_LOOT");
                    var opsA = LootDeltaComputer.Compute(set, One(ownerA), "GK", new FakeRandom(seed), candidates);
                    var opsB = LootDeltaComputer.Compute(set, One(ownerB), "GK", new FakeRandom(seed), candidates);
                    Check.Eq(LootGrantCodec.ComputeOpsHash(opsA), LootGrantCodec.ComputeOpsHash(opsB),
                        "seed " + seed + ": identical inputs must produce identical Ops");
                }
            });

            t.Case("Ops: owner iteration order does not matter (re-sorted ascending ROSTER ORDINAL internally)", () =>
            {
                var candidates = new FakeCandidateSource().Add("HERB", "COMMON", "HERB_A");
                var set = RecipeParser.Parse(TreasureSenseJson);
                var a = Owner("A_FIRST", "SKILL_CF_TRAIT_TREASURE_SENSE_LOOT");
                var b = Owner("B_SECOND", "SKILL_CF_TRAIT_TREASURE_SENSE_LOOT");
                // Roster ordinals, NOT guids, are what the computer sorts on -- and they are deliberately
                // assigned against ordinal-guid order here so the two disagree.
                a.RosterOrdinalValue = 1;
                b.RosterOrdinalValue = 0;

                var forward = LootDeltaComputer.Compute(set, new List<ICombatEntity> { a, b }, "GK", new FakeRandom(7), candidates);
                var backward = LootDeltaComputer.Compute(set, new List<ICombatEntity> { b, a }, "GK", new FakeRandom(7), candidates);
                Check.Eq(LootGrantCodec.ComputeOpsHash(forward), LootGrantCodec.ComputeOpsHash(backward), "input order-independent");
            });
        }

        // ----------------------------------------------------------------------------- 2. golden draw sequence

        private static void RunGoldenDrawSequence(TestRunner t)
        {
            t.Section("loot: golden draw sequence (harness item 2, verb spec §4.4)");

            t.Case("SCAVENGER: proc fail takes exactly one draw and emits nothing", () =>
            {
                var set = RecipeParser.Parse(ScavengerJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_SCAVENGER_LOOT");
                var rng = new FakeRandom(1);
                rng.ScriptChance(false);
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, null);
                Check.Eq(0, ops.Count, "no ops on failed proc");
                Check.Eq(1, rng.Draws, "exactly one draw: the failed proc roll");
            });

            t.Case("SCAVENGER: proc -> pick(gold) -> gold amount = 3 draws, one ADD_GOLD op", () =>
            {
                var set = RecipeParser.Parse(ScavengerJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_SCAVENGER_LOOT");
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                rng.ScriptInt(0, 14); // pick index 0 (GOLD_GRANT), amount 14
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, null);
                Check.Eq(1, ops.Count, "one op");
                Check.Eq(3, rng.Draws, "proc + pick + gold = 3 draws");
                Check.True(ops[0].Kind == LootOpKind.ADD_GOLD, "ADD_GOLD emitted");
                Check.Eq(14, ops[0].Amount, "amount from the scripted draw");
                // "E0" is the owner's PEER-STABLE key (position in the sorted owner list), not its guid.
                // LootOp.Source is inside ComputeOpsHash, which the CF_SYNC_LOOT_GRANT_V1 audit compares
                // across peers -- embedding Entity.Guid here made that audit mismatch by construction.
                Check.Eq("SKILL_CF_TRAIT_SCAVENGER_LOOT|E0", ops[0].Source, "Source is recipe|ownerKey");
            });

            t.Case("SCAVENGER: proc -> pick(herb) -> item pick = 3 draws, one ADD_ITEM op with a deterministic id", () =>
            {
                var set = RecipeParser.Parse(ScavengerJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_SCAVENGER_LOOT");
                var candidates = new FakeCandidateSource().Add("HERB", "COMMON", "HERB_B", "HERB_A"); // sorted -> HERB_A, HERB_B
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                rng.ScriptInt(1, 0); // pick index 1 (ITEM_TAG_GRANT), item index 0 -> HERB_A
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK1", rng, candidates);
                Check.Eq(1, ops.Count, "one op");
                Check.Eq(3, rng.Draws, "proc + pick + item = 3 draws");
                Check.True(ops[0].Kind == LootOpKind.ADD_ITEM, "ADD_ITEM emitted");
                Check.Eq("HERB_A", ops[0].ConfigName, "ordinal-ignore-case sort puts HERB_A first");
                Check.Eq(LootThingId.Mint("GK1", 0), ops[0].ThingId, "deterministic Thing id, opIndex 0");
            });

            t.Case("TREASURE_SENSE: proc -> gold amount = 2 draws, one ADD_GOLD op", () =>
            {
                var set = RecipeParser.Parse(TreasureSenseJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_TREASURE_SENSE_LOOT");
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                rng.ScriptInt(9);
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, null);
                Check.Eq(1, ops.Count, "one op");
                Check.Eq(2, rng.Draws, "proc + gold = 2 draws (no PickOneEffect)");
                Check.Eq(9, ops[0].Amount, "amount from the scripted draw");
            });

            t.Case("SCHOLARS_HABIT: proc -> item pick = 2 draws, one ADD_ITEM op, Rarity null = any", () =>
            {
                var set = RecipeParser.Parse(ScholarsHabitJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_SCHOLARS_HABIT_LOOT");
                var candidates = new FakeCandidateSource().Add("SCROLL", null, "SCROLL_B", "SCROLL_A");
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                rng.ScriptInt(1); // -> SCROLL_B (sorted: SCROLL_A, SCROLL_B)
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK2", rng, candidates);
                Check.Eq(1, ops.Count, "one op");
                Check.Eq(2, rng.Draws, "proc + item = 2 draws");
                Check.Eq("SCROLL_B", ops[0].ConfigName, "index 1 of the sorted candidate list");
            });

            t.Case("OF_SCAVENGING: proc -> pick(gold) -> gold amount = 3 draws", () =>
            {
                var set = RecipeParser.Parse(OfScavengingJson);
                var owner = Owner("A_HERO", "SKILL_CF_AFFIX_OF_SCAVENGING_LOOT");
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                rng.ScriptInt(0, 7);
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, null);
                Check.Eq(1, ops.Count, "one op");
                Check.Eq(3, rng.Draws, "proc + pick + gold = 3 draws");
                Check.Eq(7, ops[0].Amount, "amount from the scripted draw");
            });

            t.Case("ProcChance == 100 takes zero draws for the proc roll", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"CURRENCY_ADVENTURE\",\"Percent\":20}]}}";
                var set = RecipeParser.Parse(json);
                var owner = Owner("A_HERO", "SKILL_T");
                var rng = new FakeRandom(1);
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, null);
                Check.Eq(1, ops.Count, "LOOT_SCALE emitted");
                Check.Eq(0, rng.Draws, "ProcChance 100 + zero-draw LOOT_SCALE = zero total draws");
            });

            t.Case("GOLD_GRANT with MinGold == MaxGold is a zero-draw flat grant", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"GOLD_GRANT\",\"MinGold\":10,\"MaxGold\":10}]}}";
                var set = RecipeParser.Parse(json);
                var owner = Owner("A_HERO", "SKILL_T");
                var rng = new FakeRandom(1);
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, null);
                Check.Eq(0, rng.Draws, "flat grant takes zero draws");
                Check.Eq(10, ops[0].Amount, "flat amount");
            });


            // ------------------------------------------------------------------ W1-E: total candidate order
            //
            // The bug: candidates were sorted with `string.Compare(a, b, OrdinalIgnoreCase)` through
            // List<T>.Sort, and the very next line indexes the result with a grant-stream draw.
            // OrdinalIgnoreCase is NOT a total order over config names (two names differing only in case
            // compare EQUAL) and List<T>.Sort is an UNSTABLE introsort, so which of two case-variant names
            // landed at the lower index was implementation-defined AND dependent on the order
            // Env.Configs.Things happened to enumerate in on that peer. Same seed, same draw COUNT,
            // different ITEM -- a divergence no draw-count probe can see. PeerOrder.SortInPlace is a
            // deliberate stable insertion sort for exactly this reason; this is the same rule applied here.

            t.Case("W1-E: case-variant candidates pick the same item whatever order the seam returns them in", () =>
            {
                var set = RecipeParser.Parse(ScholarsHabitJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_SCHOLARS_HABIT_LOOT");

                // Every permutation of a pool whose members are pairwise EQUAL under OrdinalIgnoreCase.
                string[][] enumerations =
                {
                    new[] { "scroll_a", "SCROLL_A", "Scroll_A" },
                    new[] { "SCROLL_A", "Scroll_A", "scroll_a" },
                    new[] { "Scroll_A", "scroll_a", "SCROLL_A" },
                    new[] { "SCROLL_A", "scroll_a", "Scroll_A" },
                    new[] { "scroll_a", "Scroll_A", "SCROLL_A" },
                    new[] { "Scroll_A", "SCROLL_A", "scroll_a" },
                };

                for (int index = 0; index < 3; index++)
                {
                    string reference = null;
                    foreach (var enumeration in enumerations)
                    {
                        var candidates = new FakeCandidateSource().Add("SCROLL", null, enumeration);
                        var rng = new FakeRandom(1);
                        rng.ScriptChance(true);
                        rng.ScriptInt(index);
                        var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, candidates);
                        Check.Eq(1, ops.Count, "one ADD_ITEM op");
                        Check.Eq(2, rng.Draws, "proc + item = 2 draws, whatever the enumeration order");
                        if (reference == null) reference = ops[0].ConfigName;
                        Check.Eq(reference, ops[0].ConfigName,
                            "draw index " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ": the candidate seam's enumeration order changed which item was granted. The " +
                            "sort must be a TOTAL order (Ordinal tiebreak under the OrdinalIgnoreCase " +
                            "primary key), or two peers pick different loot from the same seed.");
                    }
                }
            });

            t.Case("W1-E: the tiebreak is Ordinal, and the case-insensitive primary key still governs", () =>
            {
                var set = RecipeParser.Parse(ScholarsHabitJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_SCHOLARS_HABIT_LOOT");

                // Primary key is still OrdinalIgnoreCase: "scroll_a" sorts BEFORE "SCROLL_B", which a
                // plain Ordinal sort would not do (upper-case letters sort before lower-case ones).
                // Among the two spellings of the same name, Ordinal decides: 'S' (0x53) < 's' (0x73).
                var candidates = new FakeCandidateSource().Add("SCROLL", null, "scroll_a", "SCROLL_B", "SCROLL_A");

                var expected = new[] { "SCROLL_A", "scroll_a", "SCROLL_B" };
                for (int index = 0; index < expected.Length; index++)
                {
                    var rng = new FakeRandom(1);
                    rng.ScriptChance(true);
                    rng.ScriptInt(index);
                    var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, candidates);
                    Check.Eq(expected[index], ops[0].ConfigName,
                        "sorted position " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            });

            t.Case("ITEM_TAG_GRANT with no candidates emits nothing and draws nothing", () =>
            {
                var set = RecipeParser.Parse(ScholarsHabitJson);
                var owner = Owner("A_HERO", "SKILL_CF_TRAIT_SCHOLARS_HABIT_LOOT");
                var rng = new FakeRandom(1);
                rng.ScriptChance(true);
                var ops = LootDeltaComputer.Compute(set, One(owner), "GK", rng, null); // no candidate source
                Check.Eq(0, ops.Count, "no candidates -> no op");
                Check.Eq(1, rng.Draws, "proc draw only; the item draw is skipped");
            });
        }

        // ----------------------------------------------------------------------------- 4. idempotency / failure matrix

        private static void RunIdempotency(TestRunner t)
        {
            t.Section("loot: idempotency / failure matrix (harness item 4, verb spec §3.4/§7)");

            t.Case("duplicate payload after verification is a no-op", () =>
            {
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 5, Source = "S" } };
                string hash = LootGrantCodec.ComputeOpsHash(ops);
                var store = new PendingGrantStore();
                store.Arm("GK1", ops, hash);

                var payload = new LootGrantPayload { GrantKey = "GK1", Ops = ops, OpsHash = hash };
                Check.True(store.Receive(payload) == LootReceiveResult.Verified, "first receipt verifies");
                Check.True(store.Receive(payload) == LootReceiveResult.Duplicate, "second receipt is a duplicate no-op");
            });

            t.Case("stale GrantKey after a newer combat armed is discarded", () =>
            {
                var store = new PendingGrantStore();
                store.Arm("GK_OLD", new List<LootOp>(), "sha256:old");
                store.Arm("GK_NEW", new List<LootOp>(), "sha256:new");

                var stalePayload = new LootGrantPayload { GrantKey = "GK_OLD", OpsHash = "sha256:old" };
                Check.True(store.Receive(stalePayload) == LootReceiveResult.Stale, "payload for the dropped key is stale");
            });

            t.Case("payload arriving before the local record forms is held in a single-slot inbox, then verified on Arm", () =>
            {
                var store = new PendingGrantStore();
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 3, Source = "S" } };
                string hash = LootGrantCodec.ComputeOpsHash(ops);
                var payload = new LootGrantPayload { GrantKey = "GK_LATE", Ops = ops, OpsHash = hash };

                Check.True(store.Receive(payload) == LootReceiveResult.HeldInInbox, "no local record yet -> held");
                var record = store.Arm("GK_LATE", ops, hash);
                Check.True(record.Verified, "the held inbox payload verified once the local record formed");
            });

            t.Case("single-slot inbox is overwritten by a newer payload, not accumulated", () =>
            {
                var store = new PendingGrantStore();
                var opsFirst = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 1, Source = "S" } };
                var opsSecond = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 2, Source = "S" } };
                store.Receive(new LootGrantPayload { GrantKey = "GK_X", Ops = opsFirst, OpsHash = LootGrantCodec.ComputeOpsHash(opsFirst) });
                store.Receive(new LootGrantPayload { GrantKey = "GK_X", Ops = opsSecond, OpsHash = LootGrantCodec.ComputeOpsHash(opsSecond) });

                var record = store.Arm("GK_X", opsSecond, LootGrantCodec.ComputeOpsHash(opsSecond));
                Check.True(record.Verified, "the LATEST inbox payload (matching local) verified");
            });

            t.Case("malformed JSON payload is rejected without throwing", () =>
            {
                var store = new PendingGrantStore();
                Check.True(store.ReceiveRaw("{not json") == LootReceiveResult.Malformed, "malformed JSON");
            });

            t.Case("unsupported SchemaVersion is rejected", () =>
            {
                var store = new PendingGrantStore();
                string json = "{\"Action\":\"CF_SYNC_LOOT_GRANT_V1\",\"SchemaVersion\":99,\"GrantKey\":\"GK\",\"OpsHash\":\"sha256:x\"}";
                Check.True(store.ReceiveRaw(json) == LootReceiveResult.Malformed, "unsupported SchemaVersion rejected");
            });

            t.Case("a payload carrying a reserved REPLACE_ITEM op is rejected (v1 op-vocabulary gate)", () =>
            {
                var store = new PendingGrantStore();
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.REPLACE_ITEM, ThingId = "cf-x", NewConfigName = "Y", Source = "S" } };
                var payload = new LootGrantPayload { GrantKey = "GK_R", Ops = ops, OpsHash = LootGrantCodec.ComputeOpsHash(ops) };
                Check.True(store.Receive(payload) == LootReceiveResult.Malformed, "REPLACE_ITEM ops are v1-rejected");
            });

            t.Case("digest-only (over-cap degrade) payload verifies purely by hash comparison", () =>
            {
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 5, Source = "S" } };
                string hash = LootGrantCodec.ComputeOpsHash(ops);
                var store = new PendingGrantStore();
                store.Arm("GK_D", ops, hash);

                var matching = new LootGrantPayload { GrantKey = "GK_D", Ops = null, OpsHash = hash };
                Check.True(store.Receive(matching) == LootReceiveResult.Verified, "digest-only match verifies");

                var store2 = new PendingGrantStore();
                store2.Arm("GK_D2", ops, hash);
                var mismatching = new LootGrantPayload { GrantKey = "GK_D2", Ops = null, OpsHash = "sha256:" + new string('0', 64) };
                Check.True(store2.Receive(mismatching) == LootReceiveResult.Mismatch, "digest-only mismatch is loud, not silent");
            });

            t.Case("Mode HOST_PUSH decodes but is never apply-supported in v1; MIRROR is", () =>
            {
                Check.True(LootGrantCodec.IsApplyModeSupported(LootGrantMode.MIRROR), "MIRROR applies");
                Check.False(LootGrantCodec.IsApplyModeSupported(LootGrantMode.HOST_PUSH), "HOST_PUSH reserved for M-LG4");

                string json = "{\"Action\":\"CF_SYNC_LOOT_GRANT_V1\",\"SchemaVersion\":1,\"Mode\":\"HOST_PUSH\"," +
                    "\"GrantKey\":\"GK\",\"CombatSeed\":1,\"ListDigest\":\"sha256:x\",\"OpsHash\":\"sha256:y\"}";
                LootGrantPayload payload;
                string error;
                Check.True(LootGrantCodec.TryDecode(json, out payload, out error), "HOST_PUSH still decodes: " + error);
                Check.True(payload.Mode == LootGrantMode.HOST_PUSH, "mode round-trips");
            });

            t.Case("re-arming the SAME GrantKey does not reset an already-verified record", () =>
            {
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 5, Source = "S" } };
                string hash = LootGrantCodec.ComputeOpsHash(ops);
                var store = new PendingGrantStore();
                store.Arm("GK1", ops, hash);
                store.Receive(new LootGrantPayload { GrantKey = "GK1", Ops = ops, OpsHash = hash });
                Check.True(store.Current.Verified, "verified once");
                store.Arm("GK1", ops, hash); // same combat re-armed defensively
                Check.True(store.Current.Verified, "re-arming the same key keeps Verified true");
            });
        }

        // ----------------------------------------------------------------------------- 5. codec round-trip

        private static void RunCodec(TestRunner t)
        {
            t.Section("loot: codec round-trip (harness item 5)");

            t.Case("encode -> decode -> OpsHash re-verify round-trips exactly", () =>
            {
                var ops = new List<LootOp>
                {
                    new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 14, Source = "R|G" },
                    new LootOp { Kind = LootOpKind.ADD_ITEM, ConfigName = "HERB_LUCKY_00", Stack = 1, ThingId = "cf-abc123abc123", Source = "R|G" },
                    new LootOp { Kind = LootOpKind.SCALE_STACK, ConfigName = "PARTY_XP", Percent = 20, Source = "R|G" }
                };
                var payload = new LootGrantPayload
                {
                    Mode = LootGrantMode.MIRROR,
                    GrantKey = "a3f09c2e51b7d804",
                    CombatSeed = 1846397,
                    ListDigest = "sha256:" + new string('a', 64),
                    Ops = ops,
                    OpsHash = LootGrantCodec.ComputeOpsHash(ops)
                };

                string wire = LootGrantCodec.Encode(payload);
                LootGrantPayload decoded;
                string error;
                Check.True(LootGrantCodec.TryDecode(wire, out decoded, out error), "decode succeeds: " + error);
                Check.Eq(payload.GrantKey, decoded.GrantKey, "GrantKey round-trips");
                Check.Eq(payload.CombatSeed, decoded.CombatSeed, "CombatSeed round-trips");
                Check.Eq(payload.ListDigest, decoded.ListDigest, "ListDigest round-trips");
                Check.Eq(payload.OpsHash, decoded.OpsHash, "OpsHash round-trips");
                Check.Eq(3, decoded.Ops.Count, "all ops round-trip");
                Check.Eq(payload.OpsHash, LootGrantCodec.ComputeOpsHash(decoded.Ops), "re-computed hash of the DECODED ops matches");
            });

            t.Case("EncodeCapped degrades to digest-only when the full payload exceeds the cap", () =>
            {
                var ops = new List<LootOp>();
                for (int i = 0; i < 50; i++)
                    ops.Add(new LootOp { Kind = LootOpKind.ADD_ITEM, ConfigName = "ITEM_" + i, Stack = 1, ThingId = "cf-" + i, Source = "R|G" });
                var payload = new LootGrantPayload { GrantKey = "GK_BIG", CombatSeed = 1, ListDigest = "sha256:x", Ops = ops, OpsHash = LootGrantCodec.ComputeOpsHash(ops) };

                string full = LootGrantCodec.Encode(payload);
                string capped = LootGrantCodec.EncodeCapped(payload, 200);
                Check.True(capped.Length < full.Length, "degraded payload is smaller");

                LootGrantPayload decoded;
                string error;
                Check.True(LootGrantCodec.TryDecode(capped, out decoded, out error), "digest-only still decodes: " + error);
                Check.True(decoded.IsDigestOnly, "Ops omitted");
                Check.Eq(payload.GrantKey, decoded.GrantKey, "GrantKey kept");
                Check.Eq(payload.OpsHash, decoded.OpsHash, "OpsHash kept");
            });

            t.Case("EncodeCapped is a no-op below the cap", () =>
            {
                var payload = new LootGrantPayload { GrantKey = "GK_SMALL", OpsHash = "sha256:x", Ops = new List<LootOp>() };
                Check.Eq(LootGrantCodec.Encode(payload), LootGrantCodec.EncodeCapped(payload, LootGrantCodec.DefaultMaxPayloadBytes), "unchanged under the cap");
            });
        }

        // ----------------------------------------------------------------------------- 6. validator

        private static void RunValidator(TestRunner t)
        {
            t.Section("loot: validator rejections (harness item 6)");

            t.Case("AFFIX_ROLL is always rejected in v1", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":10," +
                    "\"Effects\":[{\"Type\":\"AFFIX_ROLL\",\"ChancePct\":15,\"Table\":\"CF_AFFIX_TABLE_STANDARD\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_LOOT_RESERVED", "AFFIX_ROLL reserved");
                Check.Disabled(set, "SKILL_T", "recipe disabled");
            });

            t.Case("LootOpValidator rejects REPLACE_ITEM ops directly", () =>
            {
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.REPLACE_ITEM, ThingId = "x", NewConfigName = "y" } };
                List<string> errors;
                Check.False(LootOpValidator.ValidateV1(ops, out errors), "REPLACE_ITEM rejected");
                Check.True(errors.Count == 1, "one error reported");
            });

            t.Case("Budget is rejected on ON_COMBAT_LOOT", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\"}," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"CURRENCY_ADVENTURE\",\"Percent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_LOOT_BUDGET", "Budget rejected");
                Check.Disabled(set, "SKILL_T", "recipe disabled");
            });

            t.Case("Cooldown is rejected on ON_COMBAT_LOOT", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100,\"Cooldown\":1," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"CURRENCY_ADVENTURE\",\"Percent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_LOOT_COOLDOWN", "Cooldown rejected");
            });

            t.Case("grant effects are rejected on any trigger other than ON_COMBAT_LOOT", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_KILL\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"GOLD_GRANT\",\"MinGold\":1,\"MaxGold\":2}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_LOOT_EFFECT_SCOPE", "GOLD_GRANT off-trigger rejected");
                Check.Disabled(set, "SKILL_T", "recipe disabled");
            });

            t.Case("non-grant effects are rejected on ON_COMBAT_LOOT (restricted vocabulary)", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"S\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_LOOT_EFFECT_SCOPE", "ADD_STATUS on ON_COMBAT_LOOT rejected");
            });

            t.Case("combat-turn conditions (e.g. ROLL_TIER) are rejected on ON_COMBAT_LOOT", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Conditions\":[{\"Type\":\"ROLL_TIER\",\"Comparator\":\"GTE\",\"Value\":\"SUCCESS\"}]," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"CURRENCY_ADVENTURE\",\"Percent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_LOOT_COND_SCOPE", "ROLL_TIER rejected on ON_COMBAT_LOOT");
            });

            t.Case("HP_THRESHOLD and CHARACTER_TYPE are permitted on ON_COMBAT_LOOT", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Conditions\":[{\"Type\":\"HP_THRESHOLD\",\"Comparator\":\"GTE\",\"Percent\":1}," +
                    "{\"Type\":\"CHARACTER_TYPE\",\"Value\":\"PLAYER\"}]," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"CURRENCY_ADVENTURE\",\"Percent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "allowed condition subset parses clean");
            });

            t.Case("PickOneEffect requires >= 2 Effects", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100,\"PickOneEffect\":true," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"CURRENCY_ADVENTURE\",\"Percent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_PICKONE_COUNT", "PickOneEffect with 1 effect rejected");
            });

            t.Case("PickOneEffect is only valid on ON_COMBAT_LOOT", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_KILL\",\"PickOneEffect\":true," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"A\"}," +
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"SELF\",\"Status\":\"B\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_PICKONE_TRIGGER_SCOPE", "PickOneEffect off ON_COMBAT_LOOT rejected");
            });

            t.Case("GOLD_GRANT requires MinGold and MaxGold", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"GOLD_GRANT\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_GOLD_RANGE_MISSING", "missing gold range rejected");
            });

            t.Case("ITEM_TAG_GRANT requires Tag", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"ITEM_TAG_GRANT\"}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_ITEM_TAG_MISSING", "missing Tag rejected");
            });

            t.Case("LOOT_SCALE ConfigName must be in the whitelist", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.2\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"GEM_RARE\",\"Percent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_LOOT_SCALE_CONFIG", "non-whitelisted ConfigName rejected");
            });

            t.Case("ON_COMBAT_LOOT requires SchemaVersion 1.2", () =>
            {
                string json = "{\"SKILL_T\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_COMBAT_LOOT\",\"ProcChance\":100," +
                    "\"Effects\":[{\"Type\":\"LOOT_SCALE\",\"ConfigName\":\"CURRENCY_ADVENTURE\",\"Percent\":10}]}}";
                var set = RecipeParser.Parse(json);
                Check.HasFinding(set, "E_SCHEMA_GATE", "ON_COMBAT_LOOT under 1.1 rejected");
            });
        }

        // ----------------------------------------------------------------------------- 7. op application

        private static void RunOpApplication(TestRunner t)
        {
            t.Section("loot: op application (harness item 7)");

            t.Case("ADD_GOLD merges into an existing CURRENCY_ADVENTURE stack", () =>
            {
                var pending = new List<PendingThing> { new PendingThing { Id = "cf-existing", ConfigName = "CURRENCY_ADVENTURE", Stack = 5 } };
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 10 } };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq(1, pending.Count, "no new Thing created");
                Check.Eq(15, pending[0].Stack, "stacks merged");
            });

            t.Case("ADD_GOLD appends a new deterministic Thing when none exists", () =>
            {
                var pending = new List<PendingThing>();
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = 10 } };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq(1, pending.Count, "new Thing appended");
                Check.Eq("CURRENCY_ADVENTURE", pending[0].ConfigName, "config name");
                Check.Eq(LootThingId.Mint("GK", 0), pending[0].Id, "deterministic id, opIndex 0");
            });

            t.Case("ADD_ITEM ops append in authored order at the end of the pending list", () =>
            {
                var pending = new List<PendingThing> { new PendingThing { Id = "cf-pre", ConfigName = "PRE_EXISTING", Stack = 1 } };
                var ops = new List<LootOp>
                {
                    new LootOp { Kind = LootOpKind.ADD_ITEM, ConfigName = "ITEM_A", Stack = 1, ThingId = "cf-a" },
                    new LootOp { Kind = LootOpKind.ADD_ITEM, ConfigName = "ITEM_B", Stack = 2, ThingId = "cf-b" }
                };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq(3, pending.Count, "pre-existing + two appended");
                Check.Eq("PRE_EXISTING", pending[0].ConfigName, "pre-existing item untouched at index 0");
                Check.Eq("ITEM_A", pending[1].ConfigName, "first appended item");
                Check.Eq("ITEM_B", pending[2].ConfigName, "second appended item, in authored order");
            });

            t.Case("SCALE_STACK: whitelist rejects non-currency ConfigNames even when a matching Thing exists", () =>
            {
                var pending = new List<PendingThing> { new PendingThing { Id = "cf-g", ConfigName = "GEM_RARE", Stack = 3 } };
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.SCALE_STACK, ConfigName = "GEM_RARE", Percent = 50 } };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq(3, pending[0].Stack, "unscaled: GEM_RARE is not in the whitelist");
            });

            t.Case("SCALE_STACK: no-op when the target Thing is absent", () =>
            {
                var pending = new List<PendingThing>();
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.SCALE_STACK, ConfigName = "CURRENCY_LORE", Percent = 50 } };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq(0, pending.Count, "still empty");
            });

            t.Case("SCALE_STACK: rounds to int and clamps to a minimum of 1", () =>
            {
                var pending = new List<PendingThing> { new PendingThing { Id = "cf-l", ConfigName = "CURRENCY_LORE", Stack = 3 } };
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.SCALE_STACK, ConfigName = "CURRENCY_LORE", Percent = -90 } };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq(1, pending[0].Stack, "0.3 rounds down to 0, then clamped to the min of 1");
            });

            t.Case("SCALE_STACK: rounds away from zero on a positive fractional result", () =>
            {
                var pending = new List<PendingThing> { new PendingThing { Id = "cf-x", ConfigName = "PARTY_XP", Stack = 5 } };
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.SCALE_STACK, ConfigName = "PARTY_XP", Percent = 10 } };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq(6, pending[0].Stack, "5 * 1.10 = 5.5 -> 6");
            });

            t.Case("REPLACE_ITEM swaps ConfigName, preserving Id and Stack (application-model completeness)", () =>
            {
                var pending = new List<PendingThing> { new PendingThing { Id = "cf-existing", ConfigName = "SWORD_IRON", Stack = 1 } };
                var ops = new List<LootOp> { new LootOp { Kind = LootOpKind.REPLACE_ITEM, ThingId = "cf-existing", NewConfigName = "SWORD_IRON_OF_FURY" } };
                LootOpApplier.Apply(pending, ops, "GK");
                Check.Eq("SWORD_IRON_OF_FURY", pending[0].ConfigName, "config swapped");
                Check.Eq("cf-existing", pending[0].Id, "id preserved");
                Check.Eq(1, pending[0].Stack, "stack preserved");
            });

            t.Case("LootThingId.Mint: deterministic and index-sensitive", () =>
            {
                string a = LootThingId.Mint("GK", 0);
                string b = LootThingId.Mint("GK", 0);
                string c = LootThingId.Mint("GK", 1);
                Check.Eq(a, b, "same key+index -> same id");
                Check.True(a != c, "different index -> different id");
                Check.True(a.StartsWith("cf-", StringComparison.Ordinal), "cf- prefix");
                Check.Eq(3 + 12, a.Length, "\"cf-\" + 12 hex chars");
            });
        }
    }
}
