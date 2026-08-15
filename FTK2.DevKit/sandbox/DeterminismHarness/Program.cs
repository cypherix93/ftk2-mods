using System.Globalization;
using System.Text;
using ClassForge.Core;
using ClassForge.Core.IO;

namespace FTK2Mods.DeterminismHarness;

/// <summary>
/// Proves the pack pipeline is deterministic, without a running game and without a second peer.
///
/// <para><b>Why this exists.</b> Multiplayer parity rests on every peer computing the same
/// <c>dataHash</c> from the same pack files. If loading the same folder twice can produce two different
/// hashes — because a dictionary enumerated in hash order, or discovery followed the filesystem's
/// ordering, or a root list was walked in a different sequence — then two peers with byte-identical
/// installs will still report divergent parity and the session degrades for no real reason. That class
/// of bug is invisible in single-player and expensive to find in a live co-op session, but it is cheap
/// to catch here.</para>
///
/// <para>What this <b>cannot</b> catch: anything requiring the running game (Harmony targets, UI, the
/// vendor desync detector) and anything requiring a real second peer (GrantKey agreement, the parity
/// handshake, join-in-progress). Those still need the in-game drills.</para>
///
/// <para>Exit codes: <c>0</c> all checks passed, <c>1</c> a determinism failure, <c>2</c> skipped
/// (no packs found to test).</para>
/// </summary>
internal static class Program
{
    private const int ExitPass = 0;
    private const int ExitFail = 1;
    private const int ExitSkip = 2;

    /// <summary>How many times each load is repeated. More than two so an intermittent ordering bug
    /// has more than one chance to show itself.</summary>
    private const int Repeats = 5;

    internal static int Main(string[] args)
    {
        string? reportPath = null;
        List<string> roots = new();

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                reportPath = args[++i];
            }
            else
            {
                roots.Add(args[i]);
            }
        }

        if (roots.Count == 0) roots = DiscoverDefaultRoots();

        roots = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine("FTK2 pack determinism harness");
        Console.WriteLine("=============================");

        if (roots.Count == 0)
        {
            Console.WriteLine("SKIP: no pack roots found. Pass one or more root directories as arguments.");
            return ExitSkip;
        }

        foreach (string r in roots) Console.WriteLine("  root: " + Rel(r));
        Console.WriteLine();

        List<CheckResult> results = new()
        {
            CheckRepeatedLoadIsStable(roots),
            CheckRootOrderDoesNotAffectHash(roots),
            CheckDiscoveryOrderIsStable(roots)
        };

        Console.WriteLine();
        int failed = results.Count(r => !r.Passed);
        foreach (CheckResult r in results)
        {
            Console.WriteLine((r.Passed ? "  PASS  " : "  FAIL  ") + r.Name);
            if (!r.Passed) Console.WriteLine("        " + r.Detail);
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "ALL DETERMINISM CHECKS PASSED"
            : failed + " determinism check(s) FAILED");

        if (reportPath != null) WriteReport(reportPath, results);

        return failed == 0 ? ExitPass : ExitFail;
    }

    /// <summary>
    /// The headline check: load the same roots repeatedly in one process and require an identical
    /// <c>dataHash</c> and identical merged-pack order every time.
    /// </summary>
    private static CheckResult CheckRepeatedLoadIsStable(List<string> roots)
    {
        string? firstHash = null;
        string? firstOrder = null;

        for (int i = 0; i < Repeats; i++)
        {
            PackLoadResult result = Load(roots);
            string hash = result.DataHash;
            string order = string.Join(",", result.EnabledOrderedPacks.Select(p => p.Id));

            if (i == 0)
            {
                firstHash = hash;
                firstOrder = order;
                Console.WriteLine("  dataHash: " + (string.IsNullOrEmpty(hash) ? "(empty)" : hash));
                Console.WriteLine("  merged:   " + (string.IsNullOrEmpty(order) ? "(none)" : order));
                continue;
            }

            if (!string.Equals(firstHash, hash, StringComparison.Ordinal))
            {
                return CheckResult.Fail("dataHash is stable across repeated loads",
                    $"load 1 produced {firstHash}, load {i + 1} produced {hash}");
            }

            if (!string.Equals(firstOrder, order, StringComparison.Ordinal))
            {
                return CheckResult.Fail("merge order is stable across repeated loads",
                    $"load 1 merged [{firstOrder}], load {i + 1} merged [{order}]");
            }
        }

        return CheckResult.Pass($"dataHash and merge order stable across {Repeats} loads");
    }

    /// <summary>
    /// Two peers can hand the loader their roots in different sequences — a different
    /// <c>AdditionalRoots</c> string, or simply a different install layout. The resulting hash must not
    /// depend on that, or identical content reports as divergent.
    /// </summary>
    private static CheckResult CheckRootOrderDoesNotAffectHash(List<string> roots)
    {
        if (roots.Count < 2) return CheckResult.Pass("root ordering does not affect dataHash (only one root; trivially true)");

        string forward = Load(roots).DataHash;

        List<string> reversed = new(roots);
        reversed.Reverse();
        string backward = Load(reversed).DataHash;

        return string.Equals(forward, backward, StringComparison.Ordinal)
            ? CheckResult.Pass("root ordering does not affect dataHash")
            : CheckResult.Fail("root ordering does not affect dataHash",
                $"forward order gave {forward}, reversed order gave {backward} — two peers with identical "
                + "content but different root ordering would report a false parity mismatch");
    }

    /// <summary>Discovery is documented as alphabetically sorted; this holds it to that.</summary>
    private static CheckResult CheckDiscoveryOrderIsStable(List<string> roots)
    {
        string first = string.Join(",", Load(roots).DiscoveredPacks.Select(p => p.Id));

        for (int i = 1; i < Repeats; i++)
        {
            string again = string.Join(",", Load(roots).DiscoveredPacks.Select(p => p.Id));
            if (!string.Equals(first, again, StringComparison.Ordinal))
            {
                return CheckResult.Fail("pack discovery order is stable",
                    $"load 1 discovered [{first}], load {i + 1} discovered [{again}]");
            }
        }

        List<string> sorted = first.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        List<string> expected = new(sorted);
        expected.Sort(StringComparer.Ordinal);

        return sorted.SequenceEqual(expected, StringComparer.Ordinal)
            ? CheckResult.Pass("pack discovery order is stable and alphabetical")
            : CheckResult.Fail("pack discovery order is alphabetical",
                $"discovered [{first}] which is not ordinal-sorted — discovery is following the filesystem, "
                + "which differs between machines");
    }

    /// <summary>A fresh loader every call: reusing one would hide state that leaks between loads.</summary>
    private static PackLoadResult Load(IEnumerable<string> roots)
    {
        return new PackLoader().Load(new FileSystemFileSource(), roots);
    }

    /// <summary>
    /// Walks up from the executable to the repo root and picks up every shipped pack folder, so the
    /// harness runs with no arguments from anywhere in the tree.
    /// </summary>
    private static List<string> DiscoverDefaultRoots()
    {
        List<string> found = new();

        // Try both the binary's location and the working directory: `dotnet run` from the repo root and a
        // published exe invoked from elsewhere start from quite different places, and only one of them is
        // reliably inside the repo.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string? repoRoot = FindRepoRoot(start);
            if (repoRoot == null) continue;

            foreach (string candidate in new[]
                     {
                         Path.Combine(repoRoot, "FTK2.ClassForge", "data", "ClassPacks"),
                         Path.Combine(repoRoot, "FTK2.Blessings", "data", "ClassPacks")
                     })
            {
                if (Directory.Exists(candidate) && !found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(candidate);
                }
            }

            if (found.Count > 0) break;
        }

        return found;
    }

    /// <summary>Walks up looking for the repo marker; null when the caller is outside a checkout.</summary>
    private static string? FindRepoRoot(string start)
    {
        DirectoryInfo? dir = new(start);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "FTK2.ClassForge", "data", "ClassPacks")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Deliberately free of timestamps, absolute paths and durations: the wrapper script compares two
    /// runs byte-for-byte, so anything varying per-run would produce a false failure.
    /// </summary>
    private static void WriteReport(string path, List<CheckResult> results)
    {
        StringBuilder sb = new();
        sb.Append("{\n  \"checks\": [\n");
        for (int i = 0; i < results.Count; i++)
        {
            sb.Append("    { \"name\": \"").Append(Escape(results[i].Name))
              .Append("\", \"passed\": ").Append(results[i].Passed ? "true" : "false")
              .Append(", \"detail\": \"").Append(Escape(results[i].Detail)).Append("\" }");
            if (i < results.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ],\n  \"failed\": ")
          .Append(results.Count(r => !r.Passed).ToString(CultureInfo.InvariantCulture))
          .Append("\n}\n");

        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine("report: " + path);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Rel(string full)
    {
        string cwd = Directory.GetCurrentDirectory();
        return full.StartsWith(cwd, StringComparison.OrdinalIgnoreCase)
            ? full[(cwd.Length + 1)..]
            : full;
    }

    private sealed record CheckResult(string Name, bool Passed, string Detail)
    {
        internal static CheckResult Pass(string name) => new(name, true, "");
        internal static CheckResult Fail(string name, string detail) => new(name, false, detail);
    }
}
