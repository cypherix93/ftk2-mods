using System;

namespace FTK2Mods.DevKit.Tests
{
    /// <summary>
    /// DevKit.Core test runner.
    ///
    ///   dotnet run --project FTK2.DevKit/src/DevKit.Core.Tests -c Release
    ///
    /// Exit code 0 = every test passed (WarBrain console-harness pattern; the repo build rules bar
    /// NuGet test frameworks). Every test name is printed as it runs.
    /// </summary>
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            Console.WriteLine("FTK2.DevKit — ParityService (R1) core tests");
            Console.WriteLine("=============================================");

            CodecTests.RunAll();
            HasherTests.RunAll();
            ComparerTests.RunAll();
            PolicyTests.RunAll();
            RegistryTests.RunAll();
            HandshakeTests.RunAll();
            VerdictCallbackTests.RunAll();
            HandshakeLatchTests.RunAll();

            return TestHarness.Report();
        }
    }
}
