using System;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// Console test runner for the ClassForge skill-recipe engine (SPEC-DELTA-v1.1).
    /// Exit code 0 = all green. No xunit, no NuGet, no game references.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("ClassForge.Recipes — vocabulary v1.1 test suite");
            Console.WriteLine("fixtures: " + Fixtures.Dir);

            var runner = new TestRunner();
            ParserTests.Run(runner);
            TriggerTests.Run(runner);
            ConditionTests.Run(runner);
            EffectTests.Run(runner);
            BudgetTests.Run(runner);
            DeterminismTests.Run(runner);
            MechanicTests.Run(runner);
            LootTests.Run(runner);
            return runner.Report();
        }
    }
}
