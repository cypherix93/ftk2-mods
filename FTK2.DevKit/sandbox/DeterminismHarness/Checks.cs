namespace FTK2Mods.DeterminismHarness;

/// <summary>One load's observable outputs — everything a peer would compare against another peer.</summary>
public sealed record LoadSample(string DataHash, string MergeOrder, IReadOnlyList<string> DiscoveredIds);

public sealed record CheckResult(string Name, bool Passed, string Detail)
{
    public static CheckResult Pass(string name) => new(name, true, "");
    public static CheckResult Fail(string name, string detail) => new(name, false, detail);
}

/// <summary>
/// The determinism rules, as pure functions over observed load results.
///
/// <para>Kept free of file I/O deliberately. A check that can only be exercised by pointing it at real
/// packs is a check nobody can prove <em>fails</em> when it should, and a checker that cannot fail is
/// worse than no checker — it reports green forever and everyone trusts it. Every rule here has a
/// negative control in the test suite.</para>
/// </summary>
public static class Checks
{
    /// <summary>
    /// Loading the same roots repeatedly must produce one hash and one merge order. Divergence here
    /// means state leaked between loads, and two peers would disagree despite identical files.
    /// </summary>
    public static CheckResult RepeatedLoadStable(IReadOnlyList<LoadSample> samples)
    {
        const string name = "dataHash and merge order stable across repeated loads";

        if (samples == null || samples.Count == 0)
        {
            return CheckResult.Fail(name, "no loads were sampled — the check never ran");
        }

        LoadSample first = samples[0];
        for (int i = 1; i < samples.Count; i++)
        {
            if (!string.Equals(first.DataHash, samples[i].DataHash, StringComparison.Ordinal))
            {
                return CheckResult.Fail(name,
                    $"load 1 produced {first.DataHash}, load {i + 1} produced {samples[i].DataHash}");
            }

            if (!string.Equals(first.MergeOrder, samples[i].MergeOrder, StringComparison.Ordinal))
            {
                return CheckResult.Fail(name,
                    $"load 1 merged [{first.MergeOrder}], load {i + 1} merged [{samples[i].MergeOrder}]");
            }
        }

        return CheckResult.Pass($"{name} ({samples.Count} loads)");
    }

    /// <summary>
    /// Peers can hand the loader their roots in different sequences — a different <c>AdditionalRoots</c>
    /// string, or a different install layout. The hash must not depend on that, or identical content
    /// reports as divergent and the session degrades for no real reason.
    /// </summary>
    public static CheckResult RootOrderStable(string forwardHash, string backwardHash, bool singleRoot)
    {
        const string name = "root ordering does not affect dataHash";

        if (singleRoot) return CheckResult.Pass(name + " (one root; trivially true)");

        return string.Equals(forwardHash, backwardHash, StringComparison.Ordinal)
            ? CheckResult.Pass(name)
            : CheckResult.Fail(name,
                $"forward order gave {forwardHash}, reversed gave {backwardHash} — peers with identical "
                + "content but different root ordering would report a false parity mismatch");
    }

    /// <summary>
    /// Discovery must be stable run-to-run and ordinal-sorted. Following the filesystem's own ordering
    /// is the classic way two machines disagree about identical directories.
    /// </summary>
    public static CheckResult DiscoveryStable(IReadOnlyList<IReadOnlyList<string>> discoveries)
    {
        const string name = "pack discovery order is stable and alphabetical";

        if (discoveries == null || discoveries.Count == 0)
        {
            return CheckResult.Fail(name, "no discoveries were sampled — the check never ran");
        }

        IReadOnlyList<string> first = discoveries[0];
        for (int i = 1; i < discoveries.Count; i++)
        {
            if (!first.SequenceEqual(discoveries[i], StringComparer.Ordinal))
            {
                return CheckResult.Fail(name,
                    $"load 1 discovered [{string.Join(",", first)}], "
                    + $"load {i + 1} discovered [{string.Join(",", discoveries[i])}]");
            }
        }

        List<string> sorted = new(first);
        sorted.Sort(StringComparer.Ordinal);

        return first.SequenceEqual(sorted, StringComparer.Ordinal)
            ? CheckResult.Pass(name)
            : CheckResult.Fail(name,
                $"discovered [{string.Join(",", first)}] which is not ordinal-sorted — discovery is "
                + "following the filesystem, which differs between machines");
    }
}
