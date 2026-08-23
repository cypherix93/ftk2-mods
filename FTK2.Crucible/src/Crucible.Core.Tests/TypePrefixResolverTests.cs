using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    internal static class TypePrefixResolverTests
    {
        /// <summary>
        /// Builds a fake lookup standing in for a real type table: <paramref name="found"/> maps a
        /// candidate dotted name straight to Found, <paramref name="ambiguous"/> maps a candidate to
        /// the set of full names it collides with. Anything else is NotFound. No System.Type
        /// involved anywhere — this is what makes the resolver testable with no game running.
        /// </summary>
        private static Func<string, TypeLookupResult> FakeLookup(
            Dictionary<string, string> found, Dictionary<string, string[]> ambiguous)
        {
            return delegate (string candidate)
            {
                if (ambiguous != null && ambiguous.ContainsKey(candidate))
                    return TypeLookupResult.AmbiguousResult(ambiguous[candidate]);
                if (found != null && found.ContainsKey(candidate))
                    return TypeLookupResult.FoundResult(found[candidate]);
                return TypeLookupResult.NotFoundResult();
            };
        }

        internal static void RunAll()
        {
            TestHarness.Section("TypePrefixResolver");

            TestHarness.Run("longest prefix wins over a shorter one that also resolves", delegate
            {
                // Both "UnityEngine.InputSystem.InputSystem" (3 segs) and "UnityEngine" (1 seg,
                // contrived) would resolve; the 5-segment path must bind the 3-segment type.
                Dictionary<string, string> found = new Dictionary<string, string>
                {
                    { "UnityEngine.InputSystem.InputSystem", "UnityEngine.InputSystem.InputSystem" },
                    { "UnityEngine", "UnityEngine.SomeUnrelatedType" },
                };
                var lookup = FakeLookup(found, null);

                string[] segments = { "UnityEngine", "InputSystem", "InputSystem", "settings", "backgroundBehavior" };
                int typeSegCount; string resolvedName; string error;
                bool ok = TypePrefixResolver.TryResolve(segments, lookup, out typeSegCount, out resolvedName, out error);

                TestHarness.True(ok, "resolves");
                TestHarness.Equal(3, typeSegCount, "type consumes the 3-segment prefix, not 1");
                TestHarness.Equal("UnityEngine.InputSystem.InputSystem", resolvedName, "resolved type name");
            });

            TestHarness.Run("a bare unambiguous type name still resolves (single segment)", delegate
            {
                Dictionary<string, string> found = new Dictionary<string, string> { { "GameBridge", "FTK2Mods.Crucible.GameBridge" } };
                var lookup = FakeLookup(found, null);

                string[] segments = { "GameBridge" };
                int typeSegCount; string resolvedName; string error;
                bool ok = TypePrefixResolver.TryResolve(segments, lookup, out typeSegCount, out resolvedName, out error);

                TestHarness.True(ok, "resolves");
                TestHarness.Equal(1, typeSegCount, "whole single segment is the type");
                TestHarness.Equal("FTK2Mods.Crucible.GameBridge", resolvedName, "resolved (fully-qualified) name");
            });

            // Negative control: the live MediaTypeNames+Application collision. A bare "Application"
            // must be reported as ambiguous, never silently bound to the wrong Application type.
            TestHarness.Run("an ambiguous bare name is reported, not silently resolved", delegate
            {
                Dictionary<string, string[]> ambiguous = new Dictionary<string, string[]>
                {
                    { "Application", new[] { "UnityEngine.Application", "System.Net.Mime.MediaTypeNames+Application" } },
                };
                var lookup = FakeLookup(null, ambiguous);

                string[] segments = { "Application", "runInBackground" };
                int typeSegCount; string resolvedName; string error;
                bool ok = TypePrefixResolver.TryResolve(segments, lookup, out typeSegCount, out resolvedName, out error);

                TestHarness.True(!ok, "refused rather than guessing");
                TestHarness.True(error.IndexOf("ambiguous", StringComparison.Ordinal) >= 0, "says ambiguous");
                TestHarness.True(error.IndexOf("UnityEngine.Application", StringComparison.Ordinal) >= 0, "names candidate 1");
                TestHarness.True(error.IndexOf("System.Net.Mime.MediaTypeNames+Application", StringComparison.Ordinal) >= 0, "names candidate 2");
            });

            // Negative control: a prefix that matches no type at any length must fail clearly,
            // naming the longest prefix (and every prefix) it tried.
            TestHarness.Run("no matching prefix fails with a message naming every prefix tried", delegate
            {
                var lookup = FakeLookup(null, null);

                string[] segments = { "Totally", "Bogus", "Path" };
                int typeSegCount; string resolvedName; string error;
                bool ok = TypePrefixResolver.TryResolve(segments, lookup, out typeSegCount, out resolvedName, out error);

                TestHarness.True(!ok, "refused");
                TestHarness.True(error.IndexOf("Totally.Bogus.Path", StringComparison.Ordinal) >= 0, "names the longest prefix tried");
                TestHarness.True(error.IndexOf("Totally.Bogus", StringComparison.Ordinal) >= 0, "names the middle prefix tried");
                TestHarness.True(error.IndexOf("Totally", StringComparison.Ordinal) >= 0, "names the shortest prefix tried");
            });

            TestHarness.Run("an ambiguous long prefix stops the search rather than falling back shorter", delegate
            {
                // Even though the 1-segment prefix would resolve unambiguously, the 2-segment
                // prefix being ambiguous must be reported as-is, not silently skipped.
                Dictionary<string, string> found = new Dictionary<string, string> { { "A", "Some.A" } };
                Dictionary<string, string[]> ambiguous = new Dictionary<string, string[]> { { "A.B", new[] { "X.A.B", "Y.A.B" } } };
                var lookup = FakeLookup(found, ambiguous);

                string[] segments = { "A", "B" };
                int typeSegCount; string resolvedName; string error;
                bool ok = TypePrefixResolver.TryResolve(segments, lookup, out typeSegCount, out resolvedName, out error);

                TestHarness.True(!ok, "refused");
                TestHarness.True(error.IndexOf("A.B", StringComparison.Ordinal) >= 0, "reports the ambiguous prefix itself");
            });

            TestHarness.Run("empty segments is a clean error, not a crash", delegate
            {
                var lookup = FakeLookup(null, null);
                int typeSegCount; string resolvedName; string error;
                bool ok = TypePrefixResolver.TryResolve(new string[0], lookup, out typeSegCount, out resolvedName, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(error != null, "has an error message");
            });
        }
    }
}
