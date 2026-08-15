using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace FTK2Mods.Crucible.Tests
{
    /// <summary>
    /// Minimal console test harness (no NuGet test framework — the repo build rules bar package
    /// references). Every test name is printed; exit code 0 means all passed.
    /// Ported from FTK2.DevKit/src/DevKit.Core.Tests/TestHarness.cs.
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

        /// <summary>Runs <paramref name="body"/> with the ambient culture forced to <paramref name="cultureName"/>.</summary>
        internal static void RunInCulture(string name, string cultureName, Action body)
        {
            Run(name + " [culture=" + cultureName + "]", delegate
            {
                CultureInfo previous = Thread.CurrentThread.CurrentCulture;
                CultureInfo previousUi = Thread.CurrentThread.CurrentUICulture;
                try
                {
                    CultureInfo culture = new CultureInfo(cultureName);
                    Thread.CurrentThread.CurrentCulture = culture;
                    Thread.CurrentThread.CurrentUICulture = culture;
                    body();
                }
                finally
                {
                    Thread.CurrentThread.CurrentCulture = previous;
                    Thread.CurrentThread.CurrentUICulture = previousUi;
                }
            });
        }

        internal static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title);
        }

        internal static void True(bool condition, string message)
        {
            if (!condition) throw new Exception("expected true: " + message);
        }

        internal static void False(bool condition, string message)
        {
            if (condition) throw new Exception("expected false: " + message);
        }

        internal static void Equal(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception(message + "\n          expected: " + Show(expected) + "\n          actual:   " + Show(actual));
        }

        internal static void NotEqual(string unexpected, string actual, string message)
        {
            if (string.Equals(unexpected, actual, StringComparison.Ordinal))
                throw new Exception(message + "\n          both were: " + Show(actual));
        }

        internal static void Equal(int expected, int actual, string message)
        {
            if (expected != actual)
                throw new Exception(message + " (expected " + expected.ToString(CultureInfo.InvariantCulture)
                    + ", actual " + actual.ToString(CultureInfo.InvariantCulture) + ")");
        }

        internal static void Equal(bool expected, bool actual, string message)
        {
            if (expected != actual)
                throw new Exception(message + " (expected " + expected + ", actual " + actual + ")");
        }

        internal static int Passed { get { return _passed; } }
        internal static int Failed { get { return Failures.Count; } }

        internal static int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("Tests run: " + (_passed + Failures.Count).ToString(CultureInfo.InvariantCulture)
                + "   passed: " + _passed.ToString(CultureInfo.InvariantCulture)
                + "   failed: " + Failures.Count.ToString(CultureInfo.InvariantCulture));
            if (Failures.Count > 0)
            {
                Console.WriteLine("FAILURES:");
                for (int i = 0; i < Failures.Count; i++) Console.WriteLine("  - " + Failures[i]);
                return 1;
            }
            Console.WriteLine("ALL TESTS PASSED");
            return 0;
        }

        private static string Show(string value)
        {
            if (value == null) return "(null)";
            return "'" + value.Replace("\r", "\\r").Replace("\n", "\\n") + "'";
        }
    }
}
