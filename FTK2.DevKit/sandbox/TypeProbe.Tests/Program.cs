using System;

namespace TypeProbe.Tests
{
    /// <summary>
    ///   dotnet run --project FTK2.DevKit/sandbox/TypeProbe.Tests -c Release
    /// Exit code 0 = every test passed.
    /// </summary>
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            Console.WriteLine("TypeProbe — tests");
            Console.WriteLine("=================");

            ProbeTests.RunAll();
            // ReportTests.RunAll(); // restored in Task 3

            return TestHarness.Report();
        }
    }
}
