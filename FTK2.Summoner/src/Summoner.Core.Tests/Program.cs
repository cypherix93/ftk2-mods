using System;
using System.Collections.Generic;

namespace Summoner.Core.Tests
{
    /// <summary>
    /// Plain console-runner test harness for Summoner.Core (no xunit/NuGet -- build rule: BCL
    /// only). Covers the 14 tests planned in the design doc's §C.2 test list. Run with:
    /// dotnet run --project FTK2.Summoner/src/Summoner.Core.Tests -c Release
    /// Exit code 0 iff every test passes.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            var tests = new List<(string Name, Action Run)>
            {
                ("PackLoader_SortsPackIdsOrdinally_NotFilesystemOrder", PackLoaderTests.SortsPackIdsOrdinally_NotFilesystemOrder),
                ("PackLoader_TopoSortsByLoadOrderThenIdOrdinal", PackLoaderTests.TopoSortsByLoadOrderThenIdOrdinal),
                ("PackLoader_MissingDependency_SkipsPackAndReportsFinding", PackLoaderTests.MissingDependency_SkipsPackAndReportsFinding),
                ("PackLoader_DependencyCycle_SkipsPacksAndReportsFinding", PackLoaderTests.DependencyCycle_SkipsPacksAndReportsFinding),
                ("PackLoader_MalformedJsonInOnePack_DisablesOnlyThatPack", PackLoaderTests.MalformedJsonInOnePack_DisablesOnlyThatPack),

                ("Validator_RejectsIdWithoutSmnPrefix", PackValidatorTests.RejectsIdWithoutSmnPrefix),
                ("Validator_RejectsUnknownFollowerType", PackValidatorTests.RejectsUnknownFollowerType),
                ("Validator_RejectsUnknownContractPriceAndBehaviour", PackValidatorTests.RejectsUnknownContractPriceAndBehaviour),
                ("Validator_RejectsAppendableStatKeyNotInCharacterStats", PackValidatorTests.RejectsAppendableStatKeyNotInCharacterStats),
                ("Validator_RejectsUnresolvableConfigName_AgainstSuppliedIdSet", PackValidatorTests.RejectsUnresolvableConfigName_AgainstSuppliedIdSet),
                ("Validator_AcceptsConfigNameSatisfiedByPacksOwnCharactersJson", PackValidatorTests.AcceptsConfigNameSatisfiedByPacksOwnCharactersJson),

                ("MergePlanner_SkipsIdAlreadyPresentInLiveConfigs", MergePlannerTests.SkipsIdAlreadyPresentInLiveConfigs),
                ("MergePlanner_IsIdempotent_SamePlanTwiceIsNoOp", MergePlannerTests.IsIdempotent_SamePlanTwiceIsNoOp),

                ("DataHasher_IsStableAcrossFileOrder_AndExcludesLocalization", DataHasherTests.IsStableAcrossFileOrder_AndExcludesLocalization),
                ("DataHasher_EmitsSha256PrefixedHash_AndIsWellFormed", DataHasherTests.EmitsSha256PrefixedHash_AndIsWellFormed),
                ("DataHasher_NonTextExtension_HashedAsRawBytes_NotNewlineNormalized", DataHasherTests.NonTextExtension_HashedAsRawBytes_NotNewlineNormalized),
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
            Console.WriteLine($"{passed + failed} tests, {passed} passed, {failed} failed. (design §C.2 plans 14 for Summoner.Core.Tests.)");

            return failed == 0 ? 0 : 1;
        }
    }
}
