using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// v1.5 — the capture vocabulary: the <c>CAPTURE</c> effect and <c>SUMMON.CharacterConfigFrom</c>.
    ///
    /// <para>The suite's load-bearing themes are (a) <b>the schema gate is an ALLOW-list</b> — 1.5 tokens
    /// must be rejected by 1.0-1.4 and 1.4 tokens must still be accepted by 1.5, because an additive
    /// version that silently stops accepting its predecessor's vocabulary disables live recipes; (b) <b>a
    /// dynamic summon takes ZERO draws in the engine</b> and its resolution is entirely the Plugin's, so
    /// nothing here may touch the shared RNG stream; and (c) <b>every authoring mistake is caught at LOAD
    /// time</b>, because a mis-shaped CharacterConfigFrom fails at runtime by summoning nothing at all,
    /// which is indistinguishable from the recipe simply not firing.</para>
    /// </summary>
    public static class CaptureTests
    {
        private static string At(string schema, string trigger, string effect)
        {
            return "{\"SKILL_T\":{\"SchemaVersion\":\"" + schema + "\",\"DisplayName\":\"T\",\"Trigger\":\"" +
                   trigger + "\",\"Conditions\":[],\"Effects\":[" + effect + "]," +
                   "\"ProcChance\":100,\"AiProcChance\":100}}";
        }

        private const string CaptureEffect =
            "{\"Type\":\"CAPTURE\",\"Target\":\"TRIGGER_TARGET\"," +
            "\"IntoItem\":\"ARM_ORIG_TRAINER_BALL_CAPTURE\",\"IntoKey\":\"CF_POKE_CONFIG\"}";

        private const string DynamicSummon =
            "{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"SPECIFIC\"," +
            "\"CharacterConfigFrom\":\"ITEM_CUSTOM_DATA:ARM_ORIG_TRAINER_BALL_CAPTURE:CF_POKE_CONFIG\"," +
            "\"Count\":1}";

        public static void Run(TestRunner t)
        {
            t.Section("CAPTURE + SUMMON.CharacterConfigFrom (v1.5) - the monster-capture vocabulary");

            // ------------------------------------------------------------------ the schema gate

            t.Case("1.5 is a supported engine version", () =>
            {
                Check.NoErrors(RecipeParser.Parse(At("1.5", "ON_TURN_START", J.CounterEffect)), "1.5 accepted");
            });

            t.Case("1.5 is ADDITIVE: it still accepts the 1.3 and 1.4 vocabularies", () =>
            {
                // The gates for RANDOM_TILE / SELF_LEVEL / HAS_ITEM / STATE_HASH_CHANCE were written as
                // exact-version equalities against "1.4"/"1.3". Bumping the top version without widening
                // them would silently disable every shipped 1.4 recipe -- including the Trainer's whole
                // partner line -- so this is the regression this case exists to catch.
                string tile = "{\"SKILL_T\":{\"SchemaVersion\":\"1.5\",\"Trigger\":\"ON_TURN_START\"," +
                    "\"Conditions\":[{\"Type\":\"HAS_ITEM\",\"Value\":\"ARM_X\"}," +
                    "{\"Type\":\"SELF_LEVEL\",\"Comparator\":\"GTE\",\"Value\":3}," +
                    "{\"Type\":\"ALLY_IN_FRONT\",\"Value\":true}]," +
                    "\"Effects\":[{\"Type\":\"ADD_STATUS\",\"Target\":\"RANDOM_TILE\"," +
                    "\"Status\":\"STATUS_FIRE_00\",\"Duration\":2}]}}";
                Check.NoErrors(RecipeParser.Parse(tile), "1.4 tokens still legal under 1.5");

                string hash = "{\"SKILL_T\":{\"SchemaVersion\":\"1.5\",\"Trigger\":\"ON_DAMAGE_PENDING\"," +
                    "\"Conditions\":[{\"Type\":\"STATE_HASH_CHANCE\",\"Salt\":\"CAPT_TEST\",\"Percent\":50," +
                    "\"Inputs\":[\"SELF_GUID\",\"COMBAT_ROUND\"]}]," +
                    "\"Effects\":[{\"Type\":\"DAMAGE_TAKEN_MULT\",\"Target\":\"SELF\",\"Percent\":-25}]}}";
                Check.NoErrors(RecipeParser.Parse(hash), "1.3 tokens still legal under 1.5");
            });

            t.Case("CAPTURE is rejected below SchemaVersion 1.5", () =>
            {
                string[] older = { "1.0", "1.1", "1.2", "1.3", "1.4" };
                for (int i = 0; i < older.Length; i++)
                    Check.HasFinding(RecipeParser.Parse(At(older[i], "ON_ABILITY_USED", CaptureEffect)),
                        "E_SCHEMA_GATE", "CAPTURE gated off " + older[i]);
            });

            t.Case("CharacterConfigFrom is rejected below SchemaVersion 1.5", () =>
            {
                Check.HasFinding(RecipeParser.Parse(At("1.4", "ON_COMBAT_START", DynamicSummon)),
                    "E_SCHEMA_GATE", "CharacterConfigFrom gated off 1.4");
            });

            // ------------------------------------------------------------------ CAPTURE authoring rules

            t.Case("CAPTURE: the canonical authoring parses clean and plans one action per target", () =>
            {
                var rig = Rig.Build(At("1.5", "ON_ABILITY_USED", CaptureEffect), 5);
                Check.NoErrors(rig.Set, "canonical CAPTURE");
                Check.PlanIs(rig.Fire(TriggerKind.ON_ABILITY_USED),
                    "0: Capture{recipe=SKILL_T,owner=A_HERO,target=C_FOE," +
                    "item=ARM_ORIG_TRAINER_BALL_CAPTURE,key=CF_POKE_CONFIG}",
                    "CAPTURE plan");
            });

            t.Case("CAPTURE takes ZERO random draws", () =>
            {
                // The PERFECT gate is a read of the hook's own pRollData, and every eligibility test is a
                // read of replicated config data. A draw here would desync peers for the rest of the fight.
                var rig = Rig.Build(At("1.5", "ON_ABILITY_USED", CaptureEffect), 5);
                rig.Fire(TriggerKind.ON_ABILITY_USED);
                Check.Eq(0, rig.Rng.Draws, "draws taken by CAPTURE");
            });

            t.Case("CAPTURE requires IntoItem and IntoKey", () =>
            {
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_ABILITY_USED",
                    "{\"Type\":\"CAPTURE\",\"Target\":\"TRIGGER_TARGET\",\"IntoKey\":\"K\"}")),
                    "E_CAPTURE_ITEM", "no IntoItem");
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_ABILITY_USED",
                    "{\"Type\":\"CAPTURE\",\"Target\":\"TRIGGER_TARGET\",\"IntoItem\":\"ARM_X\"}")),
                    "E_CAPTURE_KEY", "no IntoKey");
            });

            t.Case("CAPTURE under a trigger with no target is rejected at LOAD", () =>
            {
                // Not a style rule. ON_COMBAT_START carries no trigger target, so the effect would resolve
                // nothing and the recipe would look enabled while being a permanent no-op -- the
                // ROLL_TIER{EQ FAIL} mistake class this repo has already shipped once.
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_TURN_START", CaptureEffect)),
                    "E_EFFECT_TRIGGER_SCOPE", "no target under ON_TURN_START");
            });

            t.Case("CAPTURE aimed at SELF or CASTER is rejected", () =>
            {
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_ABILITY_USED",
                    "{\"Type\":\"CAPTURE\",\"Target\":\"SELF\",\"IntoItem\":\"ARM_X\",\"IntoKey\":\"K\"}")),
                    "E_CAPTURE_TARGET", "capturing yourself");
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_ABILITY_USED",
                    "{\"Type\":\"CAPTURE\",\"Target\":\"CASTER\",\"IntoItem\":\"ARM_X\",\"IntoKey\":\"K\"}")),
                    "E_CAPTURE_TARGET", "capturing the caster");
            });

            t.Case("CAPTURE aimed at a TILE is rejected", () =>
            {
                // A tile has no CharacterComponent.ConfigName to store and cannot be removed from combat.
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_ABILITY_USED",
                    "{\"Type\":\"CAPTURE\",\"Target\":\"RANDOM_TILE\",\"IntoItem\":\"ARM_X\",\"IntoKey\":\"K\"}")),
                    "E_CAPTURE_TARGET", "RANDOM_TILE");
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_ABILITY_USED",
                    "{\"Type\":\"CAPTURE\",\"Target\":\"TRIGGER_TARGET_POSITION\",\"IntoItem\":\"ARM_X\",\"IntoKey\":\"K\"}")),
                    "E_CAPTURE_TARGET", "TRIGGER_TARGET_POSITION");
            });

            // ------------------------------------------------------------------ CharacterConfigFrom

            t.Case("CharacterConfigFrom: the token is carried into the plan, unresolved", () =>
            {
                // The ENGINE must not resolve it: reading an item's Thing.CustomData needs a game Entity,
                // which the pure-C# core never sees. It travels verbatim to the Plugin.
                var rig = Rig.Build(At("1.5", "ON_COMBAT_START", DynamicSummon), 5);
                Check.NoErrors(rig.Set, "dynamic summon parses");
                var plan = rig.Fire(TriggerKind.ON_COMBAT_START);
                Check.PlanCount(plan, 1, "one summon action");
                var summon = (SummonAction)plan[0];
                Check.Eq("ITEM_CUSTOM_DATA:ARM_ORIG_TRAINER_BALL_CAPTURE:CF_POKE_CONFIG",
                    summon.CharacterConfigFrom, "token carried verbatim");
                Check.True(string.IsNullOrEmpty(summon.CharacterConfig), "no static config invented");
                Check.Eq(0, rig.Rng.Draws, "a dynamic summon takes no engine draws");
            });

            t.Case("CharacterConfigFrom widens the SPECIFIC requirement instead of breaking it", () =>
            {
                // Before v1.5, SummonType SPECIFIC with no CharacterConfig was E_SUMMON_CONFIG. That must
                // still hold when NEITHER source is authored, and must NOT fire when the dynamic one is.
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_COMBAT_START",
                    "{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"SPECIFIC\"}")),
                    "E_SUMMON_CONFIG", "neither source authored");
                Check.NoErrors(RecipeParser.Parse(At("1.5", "ON_COMBAT_START", DynamicSummon)),
                    "dynamic source satisfies it");
            });

            t.Case("CharacterConfig and CharacterConfigFrom are mutually exclusive", () =>
            {
                Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_COMBAT_START",
                    "{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"SPECIFIC\"," +
                    "\"CharacterConfig\":\"JELLY_ACID_01\"," +
                    "\"CharacterConfigFrom\":\"ITEM_CUSTOM_DATA:ARM_X:K\"}")),
                    "E_SUMMON_CONFIG_MUTEX", "two sources for one value");
            });

            t.Case("CharacterConfigFrom is SPECIFIC-only", () =>
            {
                // RANDOM and PLAYTHING pick their own config from a weighted pool (CombatHelper.cs:120-197)
                // and would ignore the token entirely, which is a silent no-op, not an error, in game.
                string[] wrong = { "RANDOM", "PLAYTHING", "AS_FOLLOWER" };
                for (int i = 0; i < wrong.Length; i++)
                    Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_COMBAT_START",
                        "{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"" + wrong[i] + "\"," +
                        "\"CharacterConfigFrom\":\"ITEM_CUSTOM_DATA:ARM_X:K\"}")),
                        "E_SUMMON_CONFIG_FROM_TYPE", wrong[i] + " rejects the token");
            });

            t.Case("CharacterConfigFrom: a mis-shaped token is caught at LOAD", () =>
            {
                string[] bad =
                {
                    "COUNTER:cf_bond",                              // a different token family
                    "ITEM_CUSTOM_DATA:ARM_X",                       // missing the key
                    "ITEM_CUSTOM_DATA:ARM_X:K:EXTRA",               // too many parts
                    "ITEM_CUSTOM_DATA::K",                          // no Thing id
                    "ITEM_CUSTOM_DATA:ARM_X:"                       // no key
                };
                for (int i = 0; i < bad.Length; i++)
                    Check.HasFinding(RecipeParser.Parse(At("1.5", "ON_COMBAT_START",
                        "{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"SPECIFIC\"," +
                        "\"CharacterConfigFrom\":\"" + bad[i] + "\"}")),
                        "E_CONFIG_FROM_TOKEN", "rejects [" + bad[i] + "]");
            });

            t.Case("a STATIC summon's plan string is unchanged by v1.5", () =>
            {
                // Describe() is the parity/determinism rendering. Appending a field unconditionally would
                // change the recorded string of every summon that already ships.
                var rig = Rig.Build(At("1.4", "ON_KILL",
                    "{\"Type\":\"SUMMON\",\"Target\":\"SELF\",\"SummonType\":\"SPECIFIC\"," +
                    "\"CharacterConfig\":\"BAT_VAMPIRE_01\",\"Count\":1}"), 5);
                Check.PlanIs(rig.Fire(TriggerKind.ON_KILL),
                    "0: Summon{recipe=SKILL_T,owner=A_HERO,target=A_HERO,pos=0,type=SPECIFIC," +
                    "config=BAT_VAMPIRE_01,i=0}",
                    "static summon plan string");
            });

            // ------------------------------------------------------------------ the shipped pair

            t.Case("the shipped Gary pair is exactly the authoring documented in the pack", () =>
            {
                // Guards the two ids TrainerPartnerPersistence.ResolveSlot parses. SKILL_CF_TRAINER_CAPTURE_1
                // MUST keep the SKILL_CF_TRAINER_<LINE>_<STAGE> shape -- renaming it to anything that does
                // not end in a stage number silently drops HP persistence, max-HP capping and town revival
                // for the captured monster, with no error anywhere.
                string json =
                    "{\"SKILL_CF_TRAINER_CAPTURE_CATCH\":{\"SchemaVersion\":\"1.5\"," +
                    "\"DisplayName\":\"Catch 'Em All\",\"Trigger\":\"ON_ABILITY_USED\"," +
                    "\"Conditions\":[{\"Type\":\"HAS_ITEM\",\"Value\":\"ARM_ORIG_TRAINER_BALL_CAPTURE\"}," +
                    "{\"Type\":\"ROLL_TIER\",\"Comparator\":\"EQ\",\"Value\":\"PERFECT\"}," +
                    "{\"Type\":\"IS_ENEMY\",\"Of\":\"TRIGGER_TARGET\",\"Value\":true}," +
                    "{\"Type\":\"ENTITY_TAG\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"BOSS\",\"Negate\":true}," +
                    "{\"Type\":\"ENTITY_TAG\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"SCOURGE\",\"Negate\":true}," +
                    "{\"Type\":\"CHARACTER_TYPE\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"BOSS\",\"Negate\":true}]," +
                    "\"Effects\":[" + CaptureEffect + "]," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\"}}," +
                    "\"SKILL_CF_TRAINER_CAPTURE_1\":{\"SchemaVersion\":\"1.5\"," +
                    "\"DisplayName\":\"Go, Partner!\",\"Trigger\":\"ON_COMBAT_START\"," +
                    "\"Conditions\":[{\"Type\":\"HAS_ITEM\",\"Value\":\"ARM_ORIG_TRAINER_BALL_CAPTURE\"}]," +
                    "\"Effects\":[" + DynamicSummon + "]," +
                    "\"Budget\":{\"Scope\":\"ONCE_PER_COMBAT\"}}}";
                var set = RecipeParser.Parse(json);
                Check.NoErrors(set, "shipped pair validates");
                Check.True(set.Find("SKILL_CF_TRAINER_CAPTURE_CATCH").IsLive, "catch recipe is live");
                Check.True(set.Find("SKILL_CF_TRAINER_CAPTURE_1").IsLive, "send-out recipe is live");
            });

            t.Case("the boss guard is a TAG test first, with IsBoss only as a second layer", () =>
            {
                // Both conditions must be present and negated. CharacterComponent.CharacterType is BOSS only
                // inside a scripted boss fight, so a BOSS_* config met as a wandering enemy reports STANDARD
                // -- dropping the ENTITY_TAG rows would make every such creature capturable.
                var rig = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.5\",\"Trigger\":\"ON_ABILITY_USED\"," +
                    "\"Conditions\":[{\"Type\":\"ENTITY_TAG\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"BOSS\",\"Negate\":true}]," +
                    "\"Effects\":[" + CaptureEffect + "],\"ProcChance\":100,\"AiProcChance\":100}}", 5);
                Check.NoErrors(rig.Set, "tag guard parses");

                rig.Foe.TagList.Add("BOSS");
                Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED), 0, "a BOSS-tagged target is not captured");

                var clean = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.5\",\"Trigger\":\"ON_ABILITY_USED\"," +
                    "\"Conditions\":[{\"Type\":\"ENTITY_TAG\",\"Of\":\"TRIGGER_TARGET\",\"Value\":\"BOSS\",\"Negate\":true}]," +
                    "\"Effects\":[" + CaptureEffect + "],\"ProcChance\":100,\"AiProcChance\":100}}", 5);
                Check.PlanCount(clean.Fire(TriggerKind.ON_ABILITY_USED), 1, "an untagged target is captured");
            });

            t.Case("ROLL_TIER{EQ PERFECT} is the capture gate and nothing less clears it", () =>
            {
                RollTier[] tiers = { RollTier.CRIT_FAIL, RollTier.FAIL, RollTier.SUCCESS };
                for (int i = 0; i < tiers.Length; i++)
                {
                    var rig = Rig.Build(
                        "{\"SKILL_T\":{\"SchemaVersion\":\"1.5\",\"Trigger\":\"ON_ABILITY_USED\"," +
                        "\"Conditions\":[{\"Type\":\"ROLL_TIER\",\"Comparator\":\"EQ\",\"Value\":\"PERFECT\"}]," +
                        "\"Effects\":[" + CaptureEffect + "],\"ProcChance\":100,\"AiProcChance\":100}}", 5);
                    Check.PlanCount(rig.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", tiers[i]), 0,
                        tiers[i] + " does not capture");
                }

                var perfect = Rig.Build(
                    "{\"SKILL_T\":{\"SchemaVersion\":\"1.5\",\"Trigger\":\"ON_ABILITY_USED\"," +
                    "\"Conditions\":[{\"Type\":\"ROLL_TIER\",\"Comparator\":\"EQ\",\"Value\":\"PERFECT\"}]," +
                    "\"Effects\":[" + CaptureEffect + "],\"ProcChance\":100,\"AiProcChance\":100}}", 5);
                Check.PlanCount(perfect.Fire(TriggerKind.ON_ABILITY_USED, "AB_SLASH", RollTier.PERFECT), 1,
                    "PERFECT captures");
            });
        }
    }
}
