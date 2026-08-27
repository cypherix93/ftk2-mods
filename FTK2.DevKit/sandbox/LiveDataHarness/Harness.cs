using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveDataHarness
{
    /// <summary>Thrown by <see cref="Check"/> when an assertion fails. Distinct from a genuine
    /// exception so <see cref="CheckRunner"/> can report FAIL vs ERROR differently.</summary>
    public sealed class CheckFailed : Exception
    {
        public CheckFailed(string message) : base(message) { }
    }

    /// <summary>One failed check, carried into the JSON report.</summary>
    public sealed class CheckFailure
    {
        public string Name;
        public string Message;
    }

    /// <summary>Assertions. Static methods, no static fields.</summary>
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

        public static void Exactly(int expected, int actual, string what)
        {
            if (actual != expected)
                throw new CheckFailed(what + ": expected exactly " + expected.ToString(CultureInfo.InvariantCulture) +
                                      ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }

        public static void Eq(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new CheckFailed(what + ":\n  expected: " + (expected ?? "<null>") +
                                      "\n  actual:   " + (actual ?? "<null>"));
        }

        /// <summary>The workhorse: a check collects offender strings and asserts the list is empty.
        /// Offenders are ordinal-sorted so two runs report identically (AC5), and at most 15 are
        /// printed plus a count, so a wholesale break stays readable.</summary>
        public static void Empty(IEnumerable<string> offenders, string what)
        {
            List<string> list = offenders == null ? new List<string>() : offenders.ToList();
            if (list.Count == 0) return;
            list.Sort(StringComparer.Ordinal);
            string shown = string.Join("\n    ", list.Take(15));
            string more = list.Count > 15
                ? "\n    ... and " + (list.Count - 15).ToString(CultureInfo.InvariantCulture) + " more"
                : "";
            throw new CheckFailed(what + ": " + list.Count.ToString(CultureInfo.InvariantCulture) +
                                  " offender(s)\n    " + shown + more);
        }
    }

    /// <summary>Console check runner. Instance state only.</summary>
    public sealed class CheckRunner
    {
        private int _passed;
        private int _failed;
        private readonly List<CheckFailure> _failures = new List<CheckFailure>();
        private readonly List<string> _warnings = new List<string>();

        public IReadOnlyList<CheckFailure> Failures { get { return _failures; } }
        public IReadOnlyList<string> Warnings { get { return _warnings; } }
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
                _failed++;
                _failures.Add(new CheckFailure { Name = name, Message = "unexpected " + ex.GetType().Name + ": " + ex.Message });
                Console.WriteLine("  ERROR " + name);
                Console.WriteLine("        " + ex);
            }
        }

        /// <summary>Records notes that must be visible but must never fail the run (repo rule:
        /// Warning findings never fail). Sorted for report determinism (AC5).</summary>
        public void Warn(string name, IEnumerable<string> notes)
        {
            List<string> list = notes == null ? new List<string>() : notes.ToList();
            if (list.Count == 0) return;
            list.Sort(StringComparer.Ordinal);
            Console.WriteLine("  WARN  " + name + " (" + list.Count.ToString(CultureInfo.InvariantCulture) + ")");
            for (int i = 0; i < list.Count; i++)
            {
                _warnings.Add(name + ": " + list[i]);
                Console.WriteLine("        " + list[i]);
            }
        }

        public int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("checks: " + (_passed + _failed).ToString(CultureInfo.InvariantCulture) +
                              "  passed: " + _passed.ToString(CultureInfo.InvariantCulture) +
                              "  failed: " + _failed.ToString(CultureInfo.InvariantCulture) +
                              "  warnings: " + _warnings.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _failures.Count; i++)
                Console.WriteLine("FAILED: " + _failures[i].Name);
            Console.WriteLine("---------------------------------------------");
            return _failed == 0 ? 0 : 1;
        }
    }
}
