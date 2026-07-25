using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassForge.Core
{
    /// <summary>
    /// Resolves final pack load order from a set of (already enabled-filtered) discovered packs: topological
    /// sort by <c>dependencies</c>, ties broken by <c>loadOrder</c> then alphabetically by pack id (SPEC.md §3,
    /// §4.1, §9.3a). A pack with an unresolved dependency, or one that participates in a dependency cycle, is
    /// skipped and logged loudly (CONVENTIONS.md fail-safe) — every other pack still loads.
    /// </summary>
    public static class PackOrderer
    {
        public static List<DiscoveredPack> Order(List<DiscoveredPack> enabledPacks, List<Finding> findings)
        {
            var byId = new Dictionary<string, DiscoveredPack>(StringComparer.Ordinal);
            foreach (var p in enabledPacks) byId[p.Manifest.Id] = p;

            // Fixpoint removal of packs whose dependency isn't present in the enabled set (missing/unresolved dependency).
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var id in byId.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList())
                {
                    if (!byId.TryGetValue(id, out var pack)) continue; // already removed this pass
                    foreach (var dep in pack.Manifest.Dependencies ?? Enumerable.Empty<string>())
                    {
                        if (!byId.ContainsKey(dep))
                        {
                            findings.Add(Finding.Error("CF_MISSING_DEP",
                                $"Pack '{pack.Manifest.Id}' depends on '{dep}', which is not enabled/found — skipping pack.", pack.Manifest.Id));
                            byId.Remove(pack.Manifest.Id);
                            changed = true;
                            break;
                        }
                    }
                }
            }

            // Kahn's algorithm; ready-queue tiebreak = (loadOrder, id) ascending, so output is a pure function
            // of (loadOrder, dependencies, id) — never of insertion/discovery order.
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal); // depId -> packs that depend on it
            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in byId.Keys) inDegree[id] = 0;
            foreach (var pack in byId.Values)
            {
                foreach (var dep in pack.Manifest.Dependencies ?? Enumerable.Empty<string>())
                {
                    if (!dependents.TryGetValue(dep, out var list))
                        dependents[dep] = list = new List<string>();
                    list.Add(pack.Manifest.Id);
                    inDegree[pack.Manifest.Id]++;
                }
            }

            var ready = byId.Values.Where(p => inDegree[p.Manifest.Id] == 0).ToList();
            var result = new List<DiscoveredPack>();
            var visited = new HashSet<string>(StringComparer.Ordinal);

            while (ready.Count > 0)
            {
                ready.Sort((a, b) =>
                {
                    int c = a.Manifest.LoadOrder.CompareTo(b.Manifest.LoadOrder);
                    return c != 0 ? c : string.CompareOrdinal(a.Manifest.Id, b.Manifest.Id);
                });
                var next = ready[0];
                ready.RemoveAt(0);
                result.Add(next);
                visited.Add(next.Manifest.Id);

                if (dependents.TryGetValue(next.Manifest.Id, out var dependentIds))
                {
                    foreach (var depId in dependentIds)
                    {
                        inDegree[depId]--;
                        if (inDegree[depId] == 0)
                            ready.Add(byId[depId]);
                    }
                }
            }

            if (result.Count != byId.Count)
            {
                foreach (var id in byId.Keys.Except(visited).OrderBy(x => x, StringComparer.Ordinal))
                {
                    findings.Add(Finding.Error("CF_CYCLE",
                        $"Pack '{id}' participates in a dependency cycle — skipping.", id));
                }
            }

            return result;
        }
    }
}
