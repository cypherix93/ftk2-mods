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
/// (no packs found).</para>
/// </summary>
internal static class Program
{
    private const int ExitPass = 0;
    private const int ExitFail = 1;
    private const int ExitSkip = 2;

    /// <summary>Repeats per run. More than two so an intermittent ordering bug gets more than one
    /// chance to show itself.</summary>
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

        // Collect observations first, then judge them. Keeping measurement and verdict apart is what
        // lets the rules be tested with deliberately-divergent inputs.
        List<LoadSample> samples = new();
        for (int i = 0; i < Repeats; i++) samples.Add(Observe(roots));

        Console.WriteLine("  dataHash: " + (string.IsNullOrEmpty(samples[0].DataHash) ? "(empty)" : samples[0].DataHash));
        Console.WriteLine("  merged:   " + (string.IsNullOrEmpty(samples[0].MergeOrder) ? "(none)" : samples[0].MergeOrder));

        List<string> reversed = new(roots);
        reversed.Reverse();

        List<CheckResult> results = new()
        {
            Checks.RepeatedLoadStable(samples),
            Checks.RootOrderStable(samples[0].DataHash, Observe(reversed).DataHash, roots.Count < 2),
            Checks.DiscoveryStable(samples.Select(s => s.DiscoveredIds).ToList())
        };

        Console.WriteLine();
        int failed = results.Count(r => !r.Passed);
        foreach (CheckResult r in results)
        {
            Console.WriteLine((r.Passed ? "  PASS  " : "  FAIL  ") + r.Name);
            if (!r.Passed) Console.WriteLine("        " + r.Detail);
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "ALL DETERMINISM CHECKS PASSED" : failed + " determinism check(s) FAILED");

        if (reportPath != null) WriteReport(reportPath, results);

        return failed == 0 ? ExitPass : ExitFail;
    }

    /// <summary>A fresh loader every call: reusing one would hide state that leaks between loads.</summary>
    private static LoadSample Observe(IEnumerable<string> roots)
    {
        PackLoadResult result = new PackLoader().Load(new FileSystemFileSource(), roots);
        return new LoadSample(
            result.DataHash,
            string.Join(",", result.EnabledOrderedPacks.Select(p => p.Id)),
            result.DiscoveredPacks.Select(p => p.Id).ToList());
    }

    /// <summary>
    /// Convenience only. The wrapper script passes absolute roots, because <c>dotnet run</c> does not
    /// start the app in the repo root and a silent SKIP reads exactly like a pass.
    /// </summary>
    private static List<string> DiscoverDefaultRoots()
    {
        List<string> found = new();

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
    /// Deliberately free of timestamps, absolute paths and durations: the wrapper compares two runs
    /// byte-for-byte, so anything varying per-run would produce a false failure.
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
        return full.StartsWith(cwd, StringComparison.OrdinalIgnoreCase) ? full[(cwd.Length + 1)..] : full;
    }
}
