using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core.IO;

namespace ClassForge.Core
{
    /// <summary>
    /// End-to-end orchestrator for SPEC.md §3's runtime flow: discover -&gt; filter by enabled -&gt; resolve
    /// order (topo sort + cycle/missing-dep skip) -&gt; parse each pack's content -&gt; build the merge plan
    /// -&gt; compute the ParityService dataHash. This is the one entry point ClassForge.Plugin calls from its
    /// ConfigsHelper.LoadConfigs/ReloadConfigs postfixes.
    /// </summary>
    public sealed class PackLoader
    {
        /// <param name="fs">Filesystem abstraction (real disk in the Plugin, in-memory in tests).</param>
        /// <param name="roots">Root directories to scan for `&lt;root&gt;/&lt;PackName&gt;/pack.json` (the plugin's own ClassPacks folder plus any [Packs] AdditionalRoots).</param>
        /// <param name="isPackEnabled">Optional per-pack override (the BepInEx-generated `[Packs] &lt;PackId&gt;.Enabled` knob). Null = every pack's own manifest `enabled` field is authoritative.</param>
        /// <param name="liveIds">MP review M0: ids already present in the live game <c>Configs</c>, snapshotted by the
        /// Plugin immediately before this call. Null (the default — e.g. <c>ClassForge.PackCheck</c>, which has no live
        /// Configs to snapshot) means adds-only enforcement against live ids is skipped; pack-vs-pack collision handling
        /// is unaffected either way.</param>
        public PackLoadResult Load(IFileSource fs, IEnumerable<string> roots, Func<string, bool> isPackEnabled = null, LiveIdSets liveIds = null)
        {
            var findings = new List<Finding>();

            var discovered = PackDiscovery.Discover(fs, roots, findings);

            var candidateEnabled = discovered
                .Where(p => p.Manifest.Enabled && (isPackEnabled == null || isPackEnabled(p.Manifest.Id)))
                .ToList();

            foreach (var p in discovered)
            {
                if (!p.Manifest.Enabled)
                    findings.Add(Finding.Info("CF_PACK_DISABLED", $"Pack '{p.Manifest.Id}' has enabled=false in its manifest — skipped.", p.Manifest.Id));
                else if (isPackEnabled != null && !isPackEnabled(p.Manifest.Id))
                    findings.Add(Finding.Info("CF_PACK_DISABLED_BY_KNOB", $"Pack '{p.Manifest.Id}' disabled via its [Packs] knob — skipped.", p.Manifest.Id));
            }

            var orderedPacks = PackOrderer.Order(candidateEnabled, findings);

            var parsedContents = new List<(DiscoveredPack Pack, ParsedPack Content)>();
            foreach (var pack in orderedPacks)
            {
                try
                {
                    var content = PackContentParser.Parse(fs, pack, findings);
                    parsedContents.Add((pack, content));
                }
                catch (Exception ex)
                {
                    findings.Add(Finding.Error("CF_PACK_PARSE_FAILED", $"Pack '{pack.Manifest.Id}' failed to parse and was skipped: {ex.Message}", pack.Manifest.Id));
                }
            }

            var mergePlan = MergePlanner.Build(parsedContents, findings, liveIds);

            var hashInputs = parsedContents.Select(t => new PackForHash(t.Pack.Manifest.Id, t.Pack.RootDir));
            var dataHash = DataHasher.ComputeHash(fs, hashInputs);

            var mergedIds = new HashSet<string>(parsedContents.Select(t => t.Pack.Manifest.Id), StringComparer.Ordinal);
            var skipped = discovered
                .Where(p => !mergedIds.Contains(p.Manifest.Id))
                .Select(p => p.Manifest)
                .ToList();

            return new PackLoadResult
            {
                DiscoveredPacks = discovered.Select(p => p.Manifest).ToList(),
                EnabledOrderedPacks = parsedContents.Select(t => t.Pack.Manifest).ToList(),
                SkippedPacks = skipped,
                MergePlan = mergePlan,
                DataHash = dataHash,
                Findings = findings
            };
        }
    }
}
