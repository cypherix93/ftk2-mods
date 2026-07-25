using System;
using System.Collections.Generic;
using System.Linq;

namespace Summoner.Core.Tests
{
    /// <summary>Minimal assertion helpers for the console-runner test harness (no xunit -- see Summoner.Core.Tests.csproj).</summary>
    public static class Check
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new Exception("Assertion failed: " + message);
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!Equals(expected, actual))
                throw new Exception($"Assertion failed: {message} -- expected <{expected}>, got <{actual}>");
        }

        public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
        {
            var e = expected.ToList();
            var a = actual.ToList();
            if (!e.SequenceEqual(a))
                throw new Exception($"Assertion failed: {message} -- expected [{string.Join(",", e)}], got [{string.Join(",", a)}]");
        }

        public static void Contains<T>(IEnumerable<T> items, Func<T, bool> predicate, string message)
        {
            if (!items.Any(predicate))
                throw new Exception("Assertion failed: " + message);
        }

        public static void DoesNotContain<T>(IEnumerable<T> items, Func<T, bool> predicate, string message)
        {
            if (items.Any(predicate))
                throw new Exception("Assertion failed: " + message);
        }
    }
}
