using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// v1.4 <c>RANDOM_TILE</c> — the random-TARGET primitive. <c>StatusOneOf</c> already gave a random
    /// STATUS; this is the half that was previously written off as impossible, and together they express
    /// "drop a random status on a random tile".
    /// <para>The suite's two load-bearing themes are (a) the DRAW DISCIPLINE — how many draws are taken, in
    /// what order, and that the no-tile path takes none — because a draw-count divergence between peers is
    /// a desync; and (b) the STUN ban, which is enforced statically by the validator and again at plan time
    /// against the real status type.</para>
    /// </summary>
    public static class RandomTileTests
    {
        /// <summary>One SchemaVersion 1.4 recipe carrying a single effect.</summary>
        private static string Eff14(string trigger, string effect)
        {
            return "{\"SKILL_T\":{\"SchemaVersion\":\"1.4\",\"DisplayName\":\"T\",\"Trigger\":\"" + trigger +
                   "\",\"Conditions\":[],\"Effects\":[" + effect + "]," +
                   "\"ProcChance\":100,\"AiProcChance\":100}}";
        }

        private static string EffAt(string schema, string trigger, string effect)
        {
            return "{\"SKILL_T\":{\"SchemaVersion\":\"" + schema + "\",\"DisplayName\":\"T\",\"Trigger\":\"" +
                   trigger + "\",\"Conditions\":[],\"Effects\":[" + effect + "]," +
                   "\"ProcChance\":100,\"AiProcChance\":100}}";
        }

        private const string FireOnTile =
            "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"STATUS_FIRE_00\"}";

        private const string RandomStatusOnRandomTile =
            "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\"," +
            "\"StatusOneOf\":[\"STATUS_FIRE_00\",\"STATUS_ICE_00\",\"STATUS_SHOCK_00\"],\"Duration\":3}";

        /// <summary>A rig whose venue exposes a small board, tiles appended in (Y,X) ascending order —
        /// the ordering the real adapter guarantees and the one the draw index is defined against.</summary>
        private static Rig WithBoard(string json, int seed)
        {
            var rig = Rig.Build(json, seed);
            rig.Ctx.AddTile(1, 0).AddTile(2, 0).AddTile(1, 1).AddTile(2, 1);
            return rig;
        }

        private static bool LoggedNoOp(Rig rig)
        {
            for (int i = 0; i < rig.Log.Lines.Count; i++)
            {
                var line = rig.Log.Lines[i];
                if (line.StartsWith("INFO [ClassForge] RANDOM_TILE", System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static void Run(TestRunner t)
        {
            t.Section("RANDOM_TILE (v1.4) - random-tile targeting, the random-TARGET primitive");

            // ---------------------------------------------------------------- the happy path

            t.Case("RANDOM_TILE: a tile is chosen out of the available set and the status lands on it", () =>
            {
                var rig = WithBoard(Eff14("ON_TURN_START", FireOnTile), 5);
                rig.Rng.ScriptInt(2);   // index 2 of the (Y,X)-ordered board == (1,1)
                Check.PlanIs(rig.Fire(TriggerKind.ON_TURN_START),
                    "0: AddStatus{recipe=SKILL_T,owner=A_HERO,target=tile(1,1),status=STATUS_FIRE_00,fallback=-,duration=-}",
                    "RANDOM_TILE plan");
            });

            t.Case("RANDOM_TILE: the draw is an INDEX into the (Y,X)-ordered tile list", () =>
            {
                // Every index must name its own square: this is the property that makes the shared draw
                // mean the same board square on every peer.
                var expected = new[] { "tile(1,0)", "tile(2,0)", "tile(1,1)", "tile(2,1)" };
                for (int i = 0; i < expected.Length; i++)
                {
                    var rig = WithBoard(Eff14("ON_TURN_START", FireOnTile), 5);
                    rig.Rng.ScriptInt(i);
                    var plan = rig.Fire(TriggerKind.ON_TURN_START);
                    Check.PlanCount(plan, 1, "index " + Scenario.Fmt(i) + " fires");
                    Check.Contains(plan[0].Describe(), "target=" + expected[i], "index " + Scenario.Fmt(i));
                }
            });

            t.Case("RANDOM_TILE: the plan names the tile by BOARD COORDINATE, never by its local guid", () =>
            {
                // A tile entity's Guid is local object identity. Rendering it would break the determinism
                // contract (byte-identical Describe() across peers) even when both peers picked the SAME
                // square, so it must never reach the log.
                var rig = WithBoard(Eff14("ON_TURN_START", FireOnTile), 5);
                rig.Rng.ScriptInt(0);
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan, 1, "one action");
                Check.False(plan[0].Describe().Contains("TILE_LOCAL_"), "no local guid in the plan log");
                Check.Eq("TILE_LOCAL_1_0", ((AddStatusAction)plan[0]).TargetGuid,
                    "the local guid IS still carried, for the executor to resolve");
                Check.True(((AddStatusAction)plan[0]).TargetIsTile, "flagged as a tile target");
            });

            t.Case("RANDOM_TILE: Duration and FallbackStatus are carried through verbatim", () =>
            {
                var rig = WithBoard(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"STATUS_FIRE_00\"," +
                    "\"FallbackStatus\":\"STATUS_ICE_00\",\"Duration\":4}"), 5);
                rig.Rng.ScriptInt(1);
                Check.PlanIs(rig.Fire(TriggerKind.ON_TURN_START),
                    "0: AddStatus{recipe=SKILL_T,owner=A_HERO,target=tile(2,0),status=STATUS_FIRE_00,fallback=STATUS_ICE_00,duration=4}",
                    "RANDOM_TILE carries duration and fallback");
            });

            t.Case("RANDOM_TILE: BEN'S ASK end-to-end - a random status on a random tile", () =>
            {
                var rig = WithBoard(Eff14("ON_TURN_START", RandomStatusOnRandomTile), 5);
                rig.Rng.ScriptInt(3, 1);   // tile index 3 first, THEN status index 1
                Check.PlanIs(rig.Fire(TriggerKind.ON_TURN_START),
                    "0: AddStatus{recipe=SKILL_T,owner=A_HERO,target=tile(2,1),status=STATUS_ICE_00,fallback=-,duration=3}",
                    "random status on random tile");
            });

            // ---------------------------------------------------------------- draw discipline

            t.Case("RANDOM_TILE: a fixed Status takes exactly ONE draw (the tile)", () =>
            {
                var rig = WithBoard(Eff14("ON_TURN_START", FireOnTile), 5);
                rig.Fire(TriggerKind.ON_TURN_START);
                Check.Eq(1, rig.Rng.Draws, "one draw for the tile, none for a fixed status");
            });

            t.Case("RANDOM_TILE + StatusOneOf: exactly TWO draws, tile FIRST then status", () =>
            {
                // The ORDER is the contract, not just the count: two peers must consume the shared stream
                // in the same sequence or every later roll in the combat diverges.
                var rig = WithBoard(Eff14("ON_TURN_START", RandomStatusOnRandomTile), 5);
                rig.Rng.ScriptInt(0, 2);
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.Eq(2, rig.Rng.Draws, "exactly two draws");
                Check.Contains(plan[0].Describe(), "target=tile(1,0)", "first scripted int went to the TILE");
                Check.Contains(plan[0].Describe(), "status=STATUS_SHOCK_00", "second scripted int went to the STATUS");
            });

            t.Case("RANDOM_TILE: NO tiles available is a no-op that takes ZERO draws", () =>
            {
                // Rig.Build leaves the board empty. A peer that cannot see the board must never advance the
                // shared stream, or it desyncs every subsequent roll against the peer that can.
                var rig = Rig.Build(Eff14("ON_TURN_START", RandomStatusOnRandomTile), 5);
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "no tile, no action");
                Check.Eq(0, rig.Rng.Draws, "no wasted draw - not even for the status");
            });

            t.Case("RANDOM_TILE: 'no tile available' is DISTINGUISHABLE from 'tile chosen'", () =>
            {
                // The fail-safe contract: a no-op must be observable, never a silent success and never a
                // throw. It logs at Debug (IRecipeLog.Info, which the Plugin maps to LogDebug) with the
                // [ClassForge] prefix.
                var none = Rig.Build(Eff14("ON_TURN_START", FireOnTile), 5);
                Check.PlanCount(none.Fire(TriggerKind.ON_TURN_START), 0, "no-op");
                Check.True(LoggedNoOp(none), "the no-tile no-op logged at Debug with the [ClassForge] prefix");

                var some = WithBoard(Eff14("ON_TURN_START", FireOnTile), 5);
                Check.PlanCount(some.Fire(TriggerKind.ON_TURN_START), 1, "tile chosen");
                Check.False(LoggedNoOp(some), "the success path logs no no-op line");
            });

            t.Case("RANDOM_TILE: an out-of-range draw is clamped, never an exception", () =>
            {
                var rig = WithBoard(Eff14("ON_TURN_START", FireOnTile), 5);
                rig.Rng.ScriptInt(99);
                var plan = rig.Fire(TriggerKind.ON_TURN_START);
                Check.PlanCount(plan, 1, "still one action");
                Check.Contains(plan[0].Describe(), "target=tile(2,1)", "clamped to the last tile");
            });

            t.Case("RANDOM_TILE: identically seeded runs produce byte-identical plans", () =>
            {
                var a = WithBoard(Eff14("ON_TURN_START", RandomStatusOnRandomTile), 4242);
                var b = WithBoard(Eff14("ON_TURN_START", RandomStatusOnRandomTile), 4242);
                Check.Eq(ActionLog.Render(a.Fire(TriggerKind.ON_TURN_START)),
                         ActionLog.Render(b.Fire(TriggerKind.ON_TURN_START)),
                         "same seed, same plan");
            });

            // ---------------------------------------------------------------- STUN is banned

            t.Case("RANDOM_TILE: a STUN status is rejected outright (E_TILE_STATUS_ILLEGAL)", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"STATUS_STUN_00\"}")),
                    "E_TILE_STATUS_ILLEGAL", "STUN on a tile");
            });

            t.Case("RANDOM_TILE: STUN hiding inside a StatusOneOf pool is rejected too", () =>
            {
                // This is the shape that matters: the game's own random pool
                // (InteractableHelper.CHAOS_STATUS_NAMES) CONTAINS STATUS_STUN_00, so an author copying it
                // wholesale into StatusOneOf is the realistic way stun would sneak back in.
                var set = RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"StatusOneOf\":" +
                    "[\"STATUS_POISON_00\",\"STATUS_FIRE_00\",\"STATUS_SHOCK_00\",\"STATUS_STUN_00\"," +
                    "\"STATUS_WATER_00\",\"STATUS_BLEED_00\",\"STATUS_ACID_00\",\"STATUS_ICE_00\"," +
                    "\"STATUS_CONFUSE_00\"]}"));
                Check.HasFinding(set, "E_TILE_STATUS_ILLEGAL", "STUN inside the pool");
                Check.Disabled(set, "SKILL_T", "a pool carrying STUN disables the whole recipe");
            });

            t.Case("RANDOM_TILE: the whole CHARACTER_ONLY_STATUS family is refused, not just STUN", () =>
            {
                var banned = new[]
                {
                    "STATUS_STUN_00", "STATUS_DAZE_00", "STATUS_GRAB_00",
                    "STATUS_BLEED_00", "STATUS_DEATHMARK_00", "STATUS_DEATHSAVE_00"
                };
                for (int i = 0; i < banned.Length; i++)
                    Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                        "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"" + banned[i] + "\"}")),
                        "E_TILE_STATUS_ILLEGAL", banned[i] + " on a tile");
            });

            t.Case("RANDOM_TILE: a character-only status that DODGES the id convention is caught at plan time", () =>
            {
                // The validator's prefix rule is a naming heuristic. The authoritative belt is the
                // dispatcher's read of the REAL StatusEffectConfig.Type, which catches an id that does not
                // follow STATUS_<TYPE>_NN. The draws are still spent, so peers stay in step.
                var rig = WithBoard(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"STATUS_HAMMERBLOW_00\"}"), 5);
                rig.Ctx.AddStatus("STATUS_HAMMERBLOW_00", "STUN");
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "no action for a STUN-typed status");
                Check.Eq(1, rig.Rng.Draws, "the tile draw is still spent, so the streams stay aligned");
                Check.True(LoggedNoOp(rig), "logged at Debug with the [ClassForge] prefix");
            });

            t.Case("RANDOM_TILE: a legal elemental status with the same shape still fires", () =>
            {
                // Negative control for the test above: prove the refusal is about the TYPE, not the id.
                var rig = WithBoard(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"STATUS_HAMMERBLOW_00\"}"), 5);
                rig.Ctx.AddStatus("STATUS_HAMMERBLOW_00", "FIRE");
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 1, "a FIRE-typed status is tile-legal");
            });

            // ---------------------------------------------------------------- validator surface

            t.Case("RANDOM_TILE: the canonical authoring shape validates clean", () =>
            {
                Check.NoErrors(RecipeParser.Parse(Eff14("ON_TURN_START", RandomStatusOnRandomTile)),
                    "canonical RANDOM_TILE recipe");
            });

            t.Case("RANDOM_TILE: only ADD_STATUS may aim at a tile (E_TILE_EFFECT_SCOPE)", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"STAT_CHANGE\",\"Target\":\"RANDOM_TILE\",\"Stat\":\"ATK\",\"FlatValue\":1}")),
                    "E_TILE_EFFECT_SCOPE", "STAT_CHANGE on a tile");
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"REMOVE_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"STATUS_FIRE_00\"}")),
                    "E_TILE_EFFECT_SCOPE", "REMOVE_STATUS on a tile");
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"SUMMON\",\"Target\":\"RANDOM_TILE\",\"CharacterConfig\":\"WOLF\"}")),
                    "E_TILE_EFFECT_SCOPE", "SUMMON on a tile");
            });

            t.Case("RANDOM_TILE: a malformed status list is still caught by the shared ADD_STATUS rules", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\"}")),
                    "E_STATUS_MISSING", "no Status and no StatusOneOf");
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"StatusOneOf\":[]}")),
                    "E_SHAPE", "an empty StatusOneOf");
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\",\"Status\":\"STATUS_FIRE_00\"," +
                    "\"StatusOneOf\":[\"STATUS_ICE_00\"]}")),
                    "E_STATUS_AMBIGUOUS", "both Status and StatusOneOf");
            });

            t.Case("RANDOM_TILE: an unknown target token is rejected, never silently defaulted", () =>
            {
                Check.HasFinding(RecipeParser.Parse(Eff14("ON_TURN_START",
                    "{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_HEX\",\"Status\":\"STATUS_FIRE_00\"}")),
                    "E_TARGET_UNKNOWN", "RANDOM_HEX is not a target token");
            });

            t.Case("RANDOM_TILE: requires SchemaVersion 1.4 - 1.3 and below reject it", () =>
            {
                var schemas = new[] { "1.0", "1.1", "1.2", "1.3" };
                for (int i = 0; i < schemas.Length; i++)
                {
                    var set = RecipeParser.Parse(EffAt(schemas[i], "ON_TURN_START", FireOnTile));
                    Check.HasFinding(set, "E_SCHEMA_GATE", "RANDOM_TILE under SchemaVersion " + schemas[i]);
                    Check.Disabled(set, "SKILL_T", "SchemaVersion " + schemas[i] + " disables the recipe");
                }
            });

            t.Case("RANDOM_TILE: cannot ride the RNG-free ON_DAMAGE_PENDING trigger", () =>
            {
                // ON_DAMAGE_PENDING's hook carries no GameRandom, so no peer may advance the shared stream
                // there. RANDOM_TILE takes a draw by definition, so it must be unreachable under it - and it
                // is, because that trigger admits only the draw-free effect set (never ADD_STATUS).
                var set = RecipeParser.Parse(EffAt("1.4", "ON_DAMAGE_PENDING", FireOnTile));
                Check.HasFinding(set, "E_DMGPEND_EFFECT", "ADD_STATUS under ON_DAMAGE_PENDING");
                Check.Disabled(set, "SKILL_T", "the RNG-free trigger disables an RNG-bearing recipe");
            });

            t.Case("RANDOM_TILE: a disabled recipe takes no tile draw at all", () =>
            {
                var rig = WithBoard(Eff14("ON_TURN_START", FireOnTile), 5);
                rig.Set.Find("SKILL_T").Enabled = false;
                Check.PlanCount(rig.Fire(TriggerKind.ON_TURN_START), 0, "disabled, no action");
                Check.Eq(0, rig.Rng.Draws, "no draw from a recipe that never evaluates");
            });
        }
    }
}
