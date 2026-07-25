using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    public sealed class AssertFailed : Exception
    {
        public AssertFailed(string message) : base(message) { }
    }

    /// <summary>Assertions. Static methods, no static fields — the whole solution is free of static
    /// mutable state (SPEC-DELTA-v1.1 §6).</summary>
    public static class Check
    {
        public static void True(bool value, string what)
        {
            if (!value) throw new AssertFailed("expected true: " + what);
        }

        public static void False(bool value, string what)
        {
            if (value) throw new AssertFailed("expected false: " + what);
        }

        public static void Eq(int expected, int actual, string what)
        {
            if (expected != actual)
                throw new AssertFailed(what + ": expected " + expected.ToString(CultureInfo.InvariantCulture) +
                                       ", got " + actual.ToString(CultureInfo.InvariantCulture));
        }

        public static void Eq(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new AssertFailed(what + ":\n  expected: " + (expected ?? "<null>") + "\n  actual:   " + (actual ?? "<null>"));
        }

        public static void Contains(string haystack, string needle, string what)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new AssertFailed(what + ": '" + needle + "' not found in:\n" + haystack);
        }

        /// <summary>Asserts a Finding with the given code exists (and, for errors, that the recipe is disabled).</summary>
        public static void HasFinding(RecipeSet set, string code, string what)
        {
            for (int i = 0; i < set.Findings.Count; i++)
                if (string.Equals(set.Findings[i].Code, code, StringComparison.Ordinal)) return;
            var all = new System.Text.StringBuilder();
            for (int i = 0; i < set.Findings.Count; i++) all.Append("    ").Append(set.Findings[i]).Append('\n');
            throw new AssertFailed(what + ": no finding with code " + code + ". Findings:\n" + all);
        }

        public static void NoErrors(RecipeSet set, string what)
        {
            for (int i = 0; i < set.Findings.Count; i++)
                if (set.Findings[i].Severity == FindingSeverity.Error)
                    throw new AssertFailed(what + ": unexpected error " + set.Findings[i]);
        }

        public static void Disabled(RecipeSet set, string recipeId, string what)
        {
            var r = set.Find(recipeId);
            if (r == null) throw new AssertFailed(what + ": recipe " + recipeId + " missing from the set");
            if (r.IsLive) throw new AssertFailed(what + ": recipe " + recipeId + " should have been disabled");
        }

        public static void PlanIs(IReadOnlyList<EngineAction> plan, string expected, string what)
        {
            Eq(expected.Trim(), ActionLog.Render(plan).Trim(), what);
        }

        public static void PlanCount(IReadOnlyList<EngineAction> plan, int n, string what)
        {
            if (plan.Count != n)
                throw new AssertFailed(what + ": expected " + n.ToString(CultureInfo.InvariantCulture) +
                                       " actions, got " + plan.Count.ToString(CultureInfo.InvariantCulture) +
                                       "\n" + ActionLog.Render(plan));
        }
    }

    /// <summary>Console test runner. Instance state only.</summary>
    public sealed class TestRunner
    {
        private int _passed;
        private int _failed;
        private readonly List<string> _failures = new List<string>();

        public void Case(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (AssertFailed ex)
            {
                _failed++;
                _failures.Add(name + " :: " + ex.Message);
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.Message.Replace("\n", "\n        "));
            }
            catch (Exception ex)
            {
                _failed++;
                _failures.Add(name + " :: unexpected " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine("  ERROR " + name);
                Console.WriteLine("        " + ex);
            }
        }

        public void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        public int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("tests: " + (_passed + _failed).ToString(CultureInfo.InvariantCulture) +
                              "  passed: " + _passed.ToString(CultureInfo.InvariantCulture) +
                              "  failed: " + _failed.ToString(CultureInfo.InvariantCulture));
            if (_failed > 0)
            {
                Console.WriteLine();
                for (int i = 0; i < _failures.Count; i++) Console.WriteLine("FAILED: " + _failures[i]);
            }
            Console.WriteLine("---------------------------------------------");
            return _failed == 0 ? 0 : 1;
        }
    }

    /// <summary>Loads the real-mechanic JSON fixtures copied next to the test binary.</summary>
    public static class Fixtures
    {
        public static string Dir
        {
            get { return Path.Combine(AppContext.BaseDirectory, "fixtures"); }
        }

        public static string Read(string fileName)
        {
            return File.ReadAllText(Path.Combine(Dir, fileName));
        }

        public static RecipeSet Load(string fileName)
        {
            return RecipeParser.Parse(Read(fileName));
        }

        public static IReadOnlyList<string> All()
        {
            var files = Directory.GetFiles(Dir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
    }
}
