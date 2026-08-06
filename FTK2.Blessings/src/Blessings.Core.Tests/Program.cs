using System;
using System.Collections.Generic;

namespace Blessings.Core.Tests
{
    /// <summary>
    /// Plain console-runner test harness for Blessings.Core (no xunit/NuGet -- build rule: BCL only),
    /// mirrors Summoner.Core.Tests/Program.cs and DevKit.Core.Tests/Program.cs. Covers SPEC §8.2 items
    /// 1-3 (parser/validator, deterministic resolution + weight-walk boundaries, grant idempotency) plus
    /// the M1-deferred roster&lt;-&gt;pack cross-check. Run with:
    /// dotnet run --project FTK2.Blessings/src/Blessings.Core.Tests -c Release
    /// Exit code 0 iff every test passes.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            var tests = new List<(string Name, Action Run)>
            {
                // ---- Parser / validator (§8.2 item 1, parser half) ----
                ("Parser_ParsesMinimalRegistry_PreservesAuthoredOrder", ParserTests.ParsesMinimalRegistry_PreservesAuthoredOrder),
                ("Parser_UnknownTopLevelField_ProducesWarning_NotError", ParserTests.UnknownTopLevelField_ProducesWarning_NotError),
                ("Parser_UnknownEntryField_ProducesWarning_NotError", ParserTests.UnknownEntryField_ProducesWarning_NotError),
                ("Parser_EorSourceField_IsKnown_NoWarning", ParserTests.EorSourceField_IsKnown_NoWarning),
                ("Parser_NonPositiveWeight_WarnsButIsAcceptedVerbatim", ParserTests.NonPositiveWeight_WarnsButIsAcceptedVerbatim),
                ("Parser_MalformedJson_ReturnsNoRegistry_WithErrorFinding", ParserTests.MalformedJson_ReturnsNoRegistry_WithErrorFinding),
                ("Parser_MissingRequiredField_DropsEntry_ButKeepsRestOfRegistry", ParserTests.MissingRequiredField_DropsEntry_ButKeepsRestOfRegistry),

                // ---- Deterministic resolution: golden tests (§8.2 item 2) ----
                ("Resolver_Golden_Seed1_GoldenCfg_PicksA", ResolverTests.Golden_Seed1_GoldenCfg_PicksA),
                ("Resolver_Golden_Seed42_AdvTest_PicksC", ResolverTests.Golden_Seed42_AdvTest_PicksC),
                ("Resolver_Golden_SeedNegative5_EmptyCfg_PicksB", ResolverTests.Golden_SeedNegative5_EmptyCfg_PicksB),
                ("Resolver_Random_SameSeedAndConfig_IsDeterministicAcrossCalls", ResolverTests.Random_SameSeedAndConfig_IsDeterministicAcrossCalls),

                // ---- Weight-walk boundary cases (§8.2 item 2) ----
                ("Resolver_WalkWeighted_RollAtExactBucketEdge_PicksThatEntry", ResolverTests.WalkWeighted_RollAtExactBucketEdge_PicksThatEntry),
                ("Resolver_WalkWeighted_WeightLEZero_ClampsToOneSlot", ResolverTests.WalkWeighted_WeightLEZero_ClampsToOneSlot),
                ("Resolver_DisablingEntries_ChangesSumWeights_Deterministically", ResolverTests.DisablingEntries_ChangesSumWeights_Deterministically),

                // ---- Mode resolution: Disabled / unknown-id / disabled-literal (§8.2 item 2) ----
                ("Resolver_Mode_Disabled_ResolvesToNoBlessing", ResolverTests.Mode_Disabled_ResolvesToNoBlessing),
                ("Resolver_Mode_Empty_TreatedAsDisabled", ResolverTests.Mode_Empty_TreatedAsDisabled),
                ("Resolver_Mode_LiteralKnownEnabledId_Resolves", ResolverTests.Mode_LiteralKnownEnabledId_Resolves),
                ("Resolver_Mode_LiteralUnknownId_TreatedAsDisabled_NeverGuesses", ResolverTests.Mode_LiteralUnknownId_TreatedAsDisabled_NeverGuesses),
                ("Resolver_Mode_LiteralDisabledRosterEntry_TreatedAsDisabled", ResolverTests.Mode_LiteralDisabledRosterEntry_TreatedAsDisabled),
                ("Resolver_Mode_Random_NoEnabledCandidates_TreatedAsDisabled", ResolverTests.Mode_Random_NoEnabledCandidates_TreatedAsDisabled),
                ("Resolver_Mode_NullRegistry_TreatedAsDisabled", ResolverTests.Mode_NullRegistry_TreatedAsDisabled),

                // ---- Grant planner idempotency (§8.2 item 3) ----
                ("GrantPlanner_Plan_OrdersByAscendingOrdinalGuid_RegardlessOfInputOrder", GrantPlannerTests.Plan_OrdersByAscendingOrdinalGuid_RegardlessOfInputOrder),
                ("GrantPlanner_Plan_MembersWithoutTrait_ShouldGrant", GrantPlannerTests.Plan_MembersWithoutTrait_ShouldGrant),
                ("GrantPlanner_Plan_MembersAlreadyHoldingTrait_AreSkipped", GrantPlannerTests.Plan_MembersAlreadyHoldingTrait_AreSkipped),
                ("GrantPlanner_Plan_DoubleInvoke_SecondPlanGrantsNothing", GrantPlannerTests.Plan_DoubleInvoke_SecondPlanGrantsNothing),
                ("GrantPlanner_Plan_TraitAlreadyPresentFromSave_GrantsNothing", GrantPlannerTests.Plan_TraitAlreadyPresentFromSave_GrantsNothing),
                ("GrantPlanner_Plan_IgnoresNullOrEmptyGuids", GrantPlannerTests.Plan_IgnoresNullOrEmptyGuids),
                ("GrantPlanner_LatchStatKey_MatchesEorPrefixConvention", GrantPlannerTests.LatchStatKey_MatchesEorPrefixConvention),

                // ---- Roster<->pack cross-check, deferred from M1 (§8.2 item 1) ----
                ("RosterPack_EveryRosterTraitId_ExistsInTraitsJson", RosterPackCrossCheckTests.EveryRosterTraitId_ExistsInTraitsJson),
                ("RosterPack_EveryTraitId_MatchesTraitIdRegex", RosterPackCrossCheckTests.EveryTraitId_MatchesTraitIdRegex),
                ("RosterPack_TraitsJson_EveryKey_MatchesTraitIdRegex", RosterPackCrossCheckTests.TraitsJson_EveryKey_MatchesTraitIdRegex),
                ("RosterPack_RosterWeights_SumTo114", RosterPackCrossCheckTests.RosterWeights_SumTo114),
                ("RosterPack_ExactlyOneTrait_CarriesPassives", RosterPackCrossCheckTests.ExactlyOneTrait_CarriesPassives),
                ("RosterPack_RosterCount_Is15_AllEnabled", RosterPackCrossCheckTests.RosterCount_Is15_AllEnabled),
            };

            int passed = 0, failed = 0;
            foreach (var (name, run) in tests)
            {
                try
                {
                    run();
                    Console.WriteLine($"PASS  {name}");
                    passed++;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"FAIL  {name}");
                    Console.WriteLine($"      {e.Message}");
                    failed++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{passed + failed} tests, {passed} passed, {failed} failed.");

            return failed == 0 ? 0 : 1;
        }
    }
}
