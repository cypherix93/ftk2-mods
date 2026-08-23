namespace FTK2Mods.DeterminismHarness.Tests;

/// <summary>
/// Tests for the determinism rules.
///
/// <para>Every rule gets a <b>negative control</b>: an input that must make it FAIL. Without those, a
/// rule that always returns Pass would look identical to a rule that works, and the harness would
/// report green forever while catching nothing. The positive cases alone prove nothing.</para>
///
/// <para>Console harness with an exit code, matching the other suites in this repo (the build rules
/// bar NuGet test frameworks).</para>
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    internal static int Main()
    {
        Console.WriteLine("DeterminismHarness — rule tests");
        Console.WriteLine("===============================");

        Section("RepeatedLoadStable");

        Run("identical loads pass", () =>
        {
            CheckResult r = Checks.RepeatedLoadStable(new[]
            {
                Sample("sha256:aaa", "P1,P2"),
                Sample("sha256:aaa", "P1,P2"),
                Sample("sha256:aaa", "P1,P2")
            });
            True(r.Passed, "expected pass");
        });

        Run("NEGATIVE: a differing dataHash fails", () =>
        {
            CheckResult r = Checks.RepeatedLoadStable(new[]
            {
                Sample("sha256:aaa", "P1,P2"),
                Sample("sha256:bbb", "P1,P2")
            });
            False(r.Passed, "a changed hash must fail");
            Contains(r.Detail, "sha256:bbb", "detail names the divergent hash");
        });

        Run("NEGATIVE: a differing merge order fails even when the hash matches", () =>
        {
            CheckResult r = Checks.RepeatedLoadStable(new[]
            {
                Sample("sha256:aaa", "P1,P2"),
                Sample("sha256:aaa", "P2,P1")
            });
            False(r.Passed, "reordered merge must fail");
            Contains(r.Detail, "P2,P1", "detail names the divergent order");
        });

        Run("NEGATIVE: sampling nothing fails rather than passing vacuously", () =>
        {
            False(Checks.RepeatedLoadStable(Array.Empty<LoadSample>()).Passed, "empty must fail");
            False(Checks.RepeatedLoadStable(null!).Passed, "null must fail");
        });

        Section("RootOrderStable");

        Run("matching hashes pass", () =>
            True(Checks.RootOrderStable("sha256:aaa", "sha256:aaa", false).Passed, "expected pass"));

        Run("NEGATIVE: order-dependent hash fails", () =>
        {
            CheckResult r = Checks.RootOrderStable("sha256:aaa", "sha256:zzz", false);
            False(r.Passed, "order dependence must fail");
            Contains(r.Detail, "false parity mismatch", "detail explains the consequence");
        });

        Run("a single root passes trivially without comparing", () =>
            True(Checks.RootOrderStable("sha256:aaa", "sha256:zzz", true).Passed,
                "one root cannot be reordered, so this must not fail"));

        Section("DiscoveryStable");

        Run("identical sorted discoveries pass", () =>
        {
            CheckResult r = Checks.DiscoveryStable(new IReadOnlyList<string>[]
            {
                new[] { "A_PACK", "B_PACK", "C_PACK" },
                new[] { "A_PACK", "B_PACK", "C_PACK" }
            });
            True(r.Passed, "expected pass");
        });

        Run("NEGATIVE: unstable discovery between loads fails", () =>
        {
            CheckResult r = Checks.DiscoveryStable(new IReadOnlyList<string>[]
            {
                new[] { "A_PACK", "B_PACK" },
                new[] { "B_PACK", "A_PACK" }
            });
            False(r.Passed, "unstable ordering must fail");
        });

        Run("NEGATIVE: stable but filesystem-ordered discovery fails", () =>
        {
            // Stable across loads, yet not ordinal-sorted: exactly what a machine-dependent
            // directory enumeration looks like, and the case a stability-only check would miss.
            CheckResult r = Checks.DiscoveryStable(new IReadOnlyList<string>[]
            {
                new[] { "C_PACK", "A_PACK", "B_PACK" },
                new[] { "C_PACK", "A_PACK", "B_PACK" }
            });
            False(r.Passed, "unsorted discovery must fail");
            Contains(r.Detail, "not ordinal-sorted", "detail names the cause");
        });

        Run("NEGATIVE: sampling nothing fails rather than passing vacuously", () =>
        {
            False(Checks.DiscoveryStable(Array.Empty<IReadOnlyList<string>>()).Passed, "empty must fail");
            False(Checks.DiscoveryStable(null!).Passed, "null must fail");
        });

        Run("ordinal sorting is respected, not culture sorting", () =>
        {
            // Ordinal puts underscore (0x5F) after uppercase letters; a culture-aware sort does not.
            // Two peers under different locales must still agree, so ordinal is the contract.
            CheckResult r = Checks.DiscoveryStable(new IReadOnlyList<string>[]
            {
                new[] { "CF_PACK_A", "CF_PACK_B" },
                new[] { "CF_PACK_A", "CF_PACK_B" }
            });
            True(r.Passed, "expected pass");
        });

        Console.WriteLine();
        Console.WriteLine("---------------------------------------------");
        Console.WriteLine($"Tests run: {_passed + Failures.Count}   passed: {_passed}   failed: {Failures.Count}");
        if (Failures.Count > 0)
        {
            Console.WriteLine("FAILURES:");
            foreach (string f in Failures) Console.WriteLine("  - " + f);
            return 1;
        }
        Console.WriteLine("ALL TESTS PASSED");
        return 0;
    }

    private static LoadSample Sample(string hash, string order) =>
        new(hash, order, order.Split(',', StringSplitOptions.RemoveEmptyEntries));

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title);
    }

    private static void Run(string name, Action body)
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

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception("expected true: " + message);
    }

    private static void False(bool condition, string message)
    {
        if (condition) throw new Exception("expected false: " + message);
    }

    private static void Contains(string haystack, string needle, string message)
    {
        if (haystack == null || !haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new Exception($"{message} — '{needle}' not found in '{haystack}'");
        }
    }
}
