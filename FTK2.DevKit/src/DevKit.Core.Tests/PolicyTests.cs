using System;

namespace FTK2Mods.DevKit.Tests
{
    internal static class PolicyTests
    {
        private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        internal static void RunAll()
        {
            TestHarness.Section("OnParityMismatch policy decisions");

            TestHarness.Run("no mismatch produces no banner, no SafeMode, no block", delegate
            {
                ParityDecision d = ParityPolicyEngine.Decide(ParityMismatchPolicy.WarnAndSafeMode, Verdicts(false));
                TestHarness.False(d.HasMismatch, "HasMismatch");
                TestHarness.False(d.ShowWarning, "ShowWarning - parity is silent when healthy");
                TestHarness.False(d.EngageSafeMode, "EngageSafeMode");
                TestHarness.False(d.BlockSession, "BlockSession");
                TestHarness.Equal("", d.BannerText, "BannerText");
            });

            TestHarness.Run("WarnOnly warns but does not engage SafeMode or block", delegate
            {
                ParityDecision d = ParityPolicyEngine.Decide(ParityMismatchPolicy.WarnOnly, Verdicts(true));
                TestHarness.True(d.ShowWarning, "ShowWarning");
                TestHarness.False(d.EngageSafeMode, "EngageSafeMode");
                TestHarness.False(d.BlockSession, "BlockSession");
                TestHarness.True(d.BannerText.Contains("Policy WarnOnly"), "banner states the policy");
                TestHarness.True(d.BannerText.Contains("ftk2mods.forge"), "banner names the mod");
            });

            TestHarness.Run("WarnAndSafeMode (default) warns and engages SafeMode for the diverged mod", delegate
            {
                TestHarness.True(ParityPolicyEngine.DefaultPolicy == ParityMismatchPolicy.WarnAndSafeMode,
                    "WarnAndSafeMode must be the default policy");
                ParityDecision d = ParityPolicyEngine.Decide(ParityMismatchPolicy.WarnAndSafeMode, Verdicts(true));
                TestHarness.True(d.ShowWarning, "ShowWarning");
                TestHarness.True(d.EngageSafeMode, "EngageSafeMode");
                TestHarness.False(d.BlockSession, "BlockSession");
                TestHarness.Equal(1, d.AffectedGuids.Length, "affected guid count");
                TestHarness.Equal("ftk2mods.forge", d.AffectedGuids[0], "affected guid");
                TestHarness.True(d.BannerText.Contains("SafeMode engaged"), "banner states SafeMode");
            });

            TestHarness.Run("Block warns and blocks the session", delegate
            {
                ParityDecision d = ParityPolicyEngine.Decide(ParityMismatchPolicy.Block, Verdicts(true));
                TestHarness.True(d.ShowWarning, "ShowWarning");
                TestHarness.False(d.EngageSafeMode, "EngageSafeMode");
                TestHarness.True(d.BlockSession, "BlockSession");
                TestHarness.True(d.BannerText.Contains("will not start/continue"), "banner states the block");
            });

            TestHarness.Run("only the diverged mods are affected, matching mods are untouched", delegate
            {
                ParityVerdict[] verdicts = ParityComparer.Compare(
                    new ParityRegistration[]
                    {
                        new ParityRegistration("ftk2mods.forge", "1.0.0", HashA, null),
                        new ParityRegistration("ftk2mods.warbrain", "1.0.0", HashA, null),
                    },
                    new ParityRegistration[]
                    {
                        new ParityRegistration("ftk2mods.forge", "1.0.0", HashB, null),
                        new ParityRegistration("ftk2mods.warbrain", "1.0.0", HashA, null),
                    }, "peer-2");
                ParityDecision d = ParityPolicyEngine.Decide(ParityMismatchPolicy.WarnAndSafeMode, verdicts);
                TestHarness.Equal(1, d.AffectedGuids.Length, "only the diverged mod is affected");
                TestHarness.Equal("ftk2mods.forge", d.AffectedGuids[0], "affected guid");
            });

            TestHarness.Run("policy names parse case-insensitively and reject junk", delegate
            {
                ParityMismatchPolicy p;
                TestHarness.True(ParityPolicyEngine.TryParsePolicy("WarnOnly", out p) && p == ParityMismatchPolicy.WarnOnly, "WarnOnly");
                TestHarness.True(ParityPolicyEngine.TryParsePolicy("  block  ", out p) && p == ParityMismatchPolicy.Block, "Block trimmed/lowercased");
                TestHarness.True(ParityPolicyEngine.TryParsePolicy("WARNANDSAFEMODE", out p) && p == ParityMismatchPolicy.WarnAndSafeMode, "WarnAndSafeMode uppercased");
                TestHarness.False(ParityPolicyEngine.TryParsePolicy("Nonsense", out p), "junk must be rejected");
                TestHarness.True(p == ParityPolicyEngine.DefaultPolicy, "rejection must leave the default policy");
                TestHarness.Equal(3, ParityPolicyEngine.PolicyNames().Length, "policy name count");
            });

            TestHarness.RunInCulture("policy parsing is culture-invariant", "tr-TR", delegate
            {
                ParityMismatchPolicy p;
                TestHarness.True(ParityPolicyEngine.TryParsePolicy("warnonly", out p) && p == ParityMismatchPolicy.WarnOnly,
                    "lowercase parse under tr-TR");
                TestHarness.True(ParityPolicyEngine.TryParsePolicy("WARNANDSAFEMODE", out p) && p == ParityMismatchPolicy.WarnAndSafeMode,
                    "uppercase parse under tr-TR");
                TestHarness.Equal("WarnAndSafeMode", ParityPolicyEngine.PolicyName(ParityMismatchPolicy.WarnAndSafeMode),
                    "policy name rendering under tr-TR");
            });
        }

        private static ParityVerdict[] Verdicts(bool mismatch)
        {
            return ParityComparer.Compare(
                new ParityRegistration[] { new ParityRegistration("ftk2mods.forge", "1.0.0", HashA, null) },
                new ParityRegistration[] { new ParityRegistration("ftk2mods.forge", "1.0.0", mismatch ? HashB : HashA, null) },
                "peer-2");
        }
    }
}
