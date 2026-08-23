using System;

namespace FTK2Mods.Crucible.Tests
{
    /// <summary>
    /// Crucible.Core test runner.
    ///
    ///   dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
    ///
    /// Exit code 0 = every test passed (DevKit.Core.Tests pattern; the repo build rules bar NuGet
    /// test frameworks). Every test name is printed as it runs.
    /// </summary>
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            Console.WriteLine("FTK2.Crucible — core tests");
            Console.WriteLine("==========================");

            JsonTests.RunAll();
            CommandTests.RunAll();
            TraceTests.RunAll();
            DigestTests.RunAll();
            ReflectionProbeTests.RunAll();
            MemberAccessTests.RunAll();
            UiCommandTests.RunAll();
            WarningTests.RunAll();
            ResolverTests.RunAll();
            TurnTrackerTests.RunAll();
            SnapshotShapeTests.RunAll();

            return TestHarness.Report();
        }
    }
}
