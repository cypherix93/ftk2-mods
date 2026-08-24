using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// CONDITIONAL_STAT_MODIFIER spec (docs/superpowers/plans/2026-08-08-conditional-stat-modifier-spec.md)
    /// M-CS1: statmodifiers.json parse/validate + the pure composition engine. Rounding vectors are
    /// EOR 0.7.0.62's verbatim arithmetic (Plugin.cs L24571 positive / L24567 negative).
    /// </summary>
    public static class StatModifierTests
    {
        private static Func<StatModifier, bool> All { get { return m => true; } }
        private static Func<string, int> NoCounters { get { return name => 0; } }

        public static void Run(TestRunner t)
        {
            t.Section("stat modifiers (M-CS1)");

            t.Case("statmod parser: well-formed modifier round-trips every field", () =>
            {
                var set = StatModifierParser.Parse(
                    "{\"STAT_CF_X\":{\"Stat\":\"EVD\",\"Percent\":20,\"Floor\":1," +
                    "\"RequiresPositiveBase\":true,\"Conditions\":[]}}");
                Check.True(!set.HasErrors, "clean parse");
                var m = set.Find("STAT_CF_X");
                Check.True(m != null && m.IsLive, "modifier present and live");
                Check.Eq("EVD", m.Stat, "Stat");
                Check.Eq(20, m.Percent.Value, "Percent");
                Check.Eq(1, m.Floor, "Floor");
                Check.True(m.RequiresPositiveBase, "RequiresPositiveBase");
            });

            t.Case("statmod parser: PercentFrom COUNTER form round-trips", () =>
            {
                var set = StatModifierParser.Parse(
                    "{\"STAT_CF_Y\":{\"Stat\":\"MAG\",\"PercentFrom\":\"COUNTER:arcane_focus\"," +
                    "\"PerUnit\":5,\"Max\":15,\"RequiresPositiveBase\":false}}");
                Check.True(!set.HasErrors, "clean parse");
                var m = set.Find("STAT_CF_Y");
                Check.Eq("COUNTER:arcane_focus", m.PercentFrom, "PercentFrom");
                Check.Eq(5, m.PerUnit.Value, "PerUnit");
                Check.Eq(15, m.Max.Value, "Max");
                Check.False(m.RequiresPositiveBase, "RequiresPositiveBase authored false");
            });

            t.Case("statmod validator: unknown Stat key is an Error and disables the modifier", () =>
            {
                var set = StatModifierParser.Parse("{\"STAT_CF_BAD\":{\"Stat\":\"NOSUCH\",\"Percent\":10}}");
                Check.True(set.HasErrors, "expected an Error finding");
                Check.True(set.Find("STAT_CF_BAD").DisabledByValidator, "modifier disabled");
            });

            t.Case("statmod validator: Percent and PercentFrom together is an Error; neither is an Error", () =>
            {
                var both = StatModifierParser.Parse(
                    "{\"STAT_CF_B\":{\"Stat\":\"EVD\",\"Percent\":10,\"PercentFrom\":\"COUNTER:x\",\"PerUnit\":1}}");
                Check.True(both.HasErrors, "both forms authored must be an Error");
                var neither = StatModifierParser.Parse("{\"STAT_CF_N\":{\"Stat\":\"EVD\"}}");
                Check.True(neither.HasErrors, "neither form authored must be an Error");
            });

            t.Case("statmod validator: PercentFrom must be a COUNTER: token and carry PerUnit", () =>
            {
                var bad = StatModifierParser.Parse(
                    "{\"STAT_CF_S\":{\"Stat\":\"MAG\",\"PercentFrom\":\"FOCUS_SPENT\",\"PerUnit\":5}}");
                Check.True(bad.HasErrors, "non-COUNTER source rejected");
                var noPer = StatModifierParser.Parse(
                    "{\"STAT_CF_P\":{\"Stat\":\"MAG\",\"PercentFrom\":\"COUNTER:x\"}}");
                Check.True(noPer.HasErrors, "PercentFrom without PerUnit rejected");
            });

            t.Case("statmod validator: stat-reading conditions are rejected; COUNTER and PARTY_HAS_FOLLOWER are legal", () =>
            {
                var bad = StatModifierParser.Parse(
                    "{\"STAT_CF_C\":{\"Stat\":\"EVD\",\"Percent\":10," +
                    "\"Conditions\":[{\"Type\":\"HP_THRESHOLD\",\"Percent\":50}]}}");
                Check.True(bad.HasErrors, "HP_THRESHOLD resolves via GetStat and must be rejected (spec 3.1/3.3)");

                var ok = StatModifierParser.Parse(
                    "{\"STAT_CF_D\":{\"Stat\":\"PHY\",\"Percent\":10," +
                    "\"Conditions\":[{\"Type\":\"PARTY_HAS_FOLLOWER\"},{\"Type\":\"COUNTER\",\"Name\":\"n\",\"Comparator\":\"GTE\",\"Value\":1}]}}");
                Check.True(!ok.HasErrors, "PARTY_HAS_FOLLOWER + COUNTER are the legal read-time set");
                Check.Eq(2, ok.Find("STAT_CF_D").Conditions.Count, "both conditions kept");
            });

            t.Case("statmod validator: Percent outside -99..500 is an Error", () =>
            {
                Check.True(StatModifierParser.Parse("{\"A\":{\"Stat\":\"EVD\",\"Percent\":-100}}").HasErrors, "-100 rejected");
                Check.True(StatModifierParser.Parse("{\"A\":{\"Stat\":\"EVD\",\"Percent\":501}}").HasErrors, "501 rejected");
            });

            t.Case("recipe validator: PARTY_HAS_FOLLOWER in a skill recipe is an Error (statmodifiers-only condition)", () =>
            {
                var set = RecipeParser.Parse(
                    "{\"SKILL_X\":{\"SchemaVersion\":\"1.1\",\"Trigger\":\"ON_ABILITY_USED\"," +
                    "\"Conditions\":[{\"Type\":\"PARTY_HAS_FOLLOWER\"}]," +
                    "\"Effects\":[{\"Type\":\"COUNTER_ADD\",\"Name\":\"n\",\"Delta\":1}]}}");
                RecipeValidator.Validate(set);
                Check.HasFinding(set, "E_COND_CONTEXT", "PARTY_HAS_FOLLOWER must be rejected outside statmodifiers.json");
                Check.True(set.Find("SKILL_X").DisabledByValidator, "recipe disabled");
            });

            t.Section("stat modifier composition (EOR 0.7.0.62 arithmetic)");

            t.Case("compose: +20% on 10 yields 12 (ceil of 2.0, min bump 1)", () =>
            {
                var mods = Mods(Fixed("STAT_CF_A", "EVD", 20));
                Check.Eq(12, StatModifierEngine.Compose(10, "EVD", mods, All, NoCounters), "+20% of 10");
            });

            t.Case("compose: +12% on base 1 yields 2 — a positive percentage always moves at least 1", () =>
            {
                var mods = Mods(Fixed("STAT_CF_A", "INT", 12));
                Check.Eq(2, StatModifierEngine.Compose(1, "INT", mods, All, NoCounters), "+12% of 1");
            });

            t.Case("compose: RequiresPositiveBase skips a base of 0; false applies the min bump (PACK_TACTICS on 0 -> 1)", () =>
            {
                var guarded = Mods(Fixed("STAT_CF_A", "EVD", 20));
                Check.Eq(0, StatModifierEngine.Compose(0, "EVD", guarded, All, NoCounters), "guarded skip on 0");

                var unguarded = Fixed("STAT_CF_B", "PHY", 10);
                unguarded.RequiresPositiveBase = false;
                Check.Eq(1, StatModifierEngine.Compose(0, "PHY", Mods(unguarded), All, NoCounters), "EOR: result += Max(1, ceil(0)) = 1");
            });

            t.Case("compose: KNIFE_EDGE -5% on 30 yields 28; on 1 the floor holds at 1", () =>
            {
                var mods = Mods(Fixed("STAT_CF_A", "HP", -5));
                Check.Eq(28, StatModifierEngine.Compose(30, "HP", mods, All, NoCounters), "30 - ceil(1.5)=2");
                Check.Eq(1, StatModifierEngine.Compose(1, "HP", mods, All, NoCounters), "Max(1, 1-1)");
            });

            t.Case("compose: PercentFrom COUNTER scales per stack, zero stacks is a true no-op, Max caps", () =>
            {
                var m = Counter("STAT_CF_A", "MAG", "arcane_focus", 5, 15);
                m.RequiresPositiveBase = false;

                Check.Eq(84, StatModifierEngine.Compose(84, "MAG", Mods(m), All, n => 0), "0 stacks: no +1 floor bump");
                Check.Eq(93, StatModifierEngine.Compose(84, "MAG", Mods(m), All, n => 2), "2 stacks = +10%: 84 + ceil(8.4)=9");
                Check.Eq(StatModifierEngine.Compose(84, "MAG", Mods(m), All, n => 3),
                         StatModifierEngine.Compose(84, "MAG", Mods(m), All, n => 4), "Max 15 caps 4 stacks to 3-stack value");
            });

            t.Case("compose: multiple modifiers on one stat apply in ascending ordinal Id order", () =>
            {
                var a = Fixed("STAT_CF_A", "EVD", 20);
                var b = Fixed("STAT_CF_B", "EVD", 15);
                // Ascending: A(+20) then B(+15): 10 -> 12 -> 12+ceil(1.8)=2 -> 14.
                // Reversed would give 15 — the test pins the order.
                Check.Eq(14, StatModifierEngine.Compose(10, "EVD", Mods(b, a), All, NoCounters), "A then B regardless of input order");
            });

            t.Case("compose: a failing condition gate skips the modifier; other stats never match", () =>
            {
                var m = Fixed("STAT_CF_A", "PHY", 10);
                Check.Eq(50, StatModifierEngine.Compose(50, "PHY", Mods(m), mod => false, NoCounters), "applies=false skips");
                Check.Eq(50, StatModifierEngine.Compose(50, "LCK", Mods(m), All, NoCounters), "PHY modifier never touches LCK");
            });

            t.Case("compose: disabled and validator-disabled modifiers never apply", () =>
            {
                var off = Fixed("STAT_CF_A", "EVD", 20);
                off.Enabled = false;
                var broken = Fixed("STAT_CF_B", "EVD", 20);
                broken.DisabledByValidator = true;
                Check.Eq(10, StatModifierEngine.Compose(10, "EVD", Mods(off, broken), All, NoCounters), "neither applies");
            });
        }

        private static StatModifier Fixed(string id, string stat, int percent)
        {
            return new StatModifier { Id = id, Stat = stat, Percent = percent };
        }

        private static StatModifier Counter(string id, string stat, string counter, int perUnit, int max)
        {
            return new StatModifier { Id = id, Stat = stat, PercentFrom = "COUNTER:" + counter, PerUnit = perUnit, Max = max };
        }

        private static List<StatModifier> Mods(params StatModifier[] mods)
        {
            return new List<StatModifier>(mods);
        }
    }
}
