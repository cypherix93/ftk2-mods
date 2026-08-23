using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace TypeProbe.Tests
{
    /// <summary>
    /// Minimal console test harness (no NuGet test framework — the repo build rules bar package
    /// references). Every test name is printed; exit code 0 means all passed.
    /// Ported from FTK2.Crucible/src/Crucible.Core.Tests/TestHarness.cs.
    /// </summary>
    internal static class TestHarness
    {
        private static int _passed;
        private static readonly List<string> Failures = new List<string>();

        internal static void Run(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception ex)
            {
                Failures.Add(name + ": " + ex.Message);
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.Message);
            }
        }

        internal static void RunInCulture(string name, string cultureName, Action body)
        {
            Run(name + " [culture=" + cultureName + "]", delegate
            {
                CultureInfo previous = Thread.CurrentThread.CurrentCulture;
                try
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
                    body();
                }
                finally
                {
                    Thread.CurrentThread.CurrentCulture = previous;
                }
            });
        }

        internal static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title);
        }

        internal static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        internal static int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("Tests run: " + (_passed + Failures.Count)
                              + "   passed: " + _passed + "   failed: " + Failures.Count);
            if (Failures.Count == 0)
            {
                Console.WriteLine("ALL TESTS PASSED");
                return 0;
            }
            foreach (string f in Failures) Console.WriteLine("  " + f);
            return 1;
        }
    }
}
