using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveDataHarness
{
    public sealed class CheckFailed : Exception
    {
        public CheckFailed(string message) : base(message) { }
    }

    public sealed class CheckFailure
    {
        public string Name;
        public string Message;
    }

    /// <summary>Assertions. Static methods and no static fields, so parallel or repeated runs cannot interfere.</summary>
    public static class Check
    {
        public static void True(bool value, string what)
        {
            if (!value) throw new CheckFailed("expected true: " + what);
        }

        public static void AtLeast(int floor, int actual, string what)
        {
            if (actual < floor)
                throw new CheckFailed(what + ": expected at least " + floor.ToString(CultureInfo.InvariantCulture) +
                                      ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }

        public static void Eq(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new CheckFailed(what + ":\n  expected: " + (expected ?? "<null>") + "\n  actual:   " + (actual ?? "<null>"));
        }

        /// <summary>
        /// The workhorse: a check collects offender strings and asserts the list is empty. Truncates to 15
        /// offenders plus a count so that a wholesale break (every id dangling) stays readable in a console
        /// instead of flooding thousands of lines.
        /// </summary>
        public static void Empty(IEnumerable<string> offenders, string what)
        {
            var list = offenders.ToList();
            if (list.Count == 0) return;
            var shown = string.Join("\n    ", list.Take(15));
            var more = list.Count > 15 ? "\n    ... and " + (list.Count - 15).ToString(CultureInfo.InvariantCulture) + " more" : "";
            throw new CheckFailed(what + ": " + list.Count.ToString(CultureInfo.InvariantCulture) + " offender(s)\n    " + shown + more);
        }
    }

    /// <summary>Console check runner. Instance state only.</summary>
    public sealed class CheckRunner
    {
        private int _passed;
        private int _failed;
        private readonly List<CheckFailure> _failures = new List<CheckFailure>();

        public IReadOnlyList<CheckFailure> Failures { get { return _failures; } }
        public int Passed { get { return _passed; } }
        public int Failed { get { return _failed; } }

        public void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        public void Case(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (CheckFailed ex)
            {
                _failed++;
                _failures.Add(new CheckFailure { Name = name, Message = ex.Message });
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.Message.Replace("\n", "\n        "));
            }
            catch (Exception ex)
            {
                // An unexpected exception is a harness defect or a game-shape change, not a content
                // finding, so it is recorded distinctly from a FAIL and the full trace is kept.
                _failed++;
                _failures.Add(new CheckFailure { Name = name, Message = "unexpected " + ex.GetType().Name + ": " + ex.Message });
                Console.WriteLine("  ERROR " + name);
                Console.WriteLine("        " + ex);
            }
        }

        public int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("checks: " + (_passed + _failed).ToString(CultureInfo.InvariantCulture) +
                              "  passed: " + _passed.ToString(CultureInfo.InvariantCulture) +
                              "  failed: " + _failed.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _failures.Count; i++)
                Console.WriteLine("FAILED: " + _failures[i].Name);
            Console.WriteLine("---------------------------------------------");
            return _failed == 0 ? 0 : 1;
        }
    }
}
