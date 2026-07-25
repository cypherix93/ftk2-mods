using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Summoner.Core.Diagnostics;
using Summoner.Core.Model;

namespace Summoner.Core.Packs
{
    /// <summary>Result of a full FollowerPacks/ discovery pass (design §A4.1).</summary>
    public sealed class LoadResult
    {
        /// <summary>Successfully-loaded packs, in final apply order: topologically sorted by
        /// (LoadOrder asc, Id ordinal asc) with dependency cycles/missing deps removed (design §A3.3).</summary>
        public List<FollowerPack> Packs { get; set; } = new List<FollowerPack>();

        public List<Finding> Findings { get; set; } = new List<Finding>();
    }

    /// <summary>
    /// Discovery -> ordinal sort -> parse via IJsonCodec -> topo sort by (LoadOrder, Id) (design §A4.1).
    /// Every per-pack parse is wrapped in try/catch: one bad file disables that pack only, everything
    /// else still loads (SPEC §8 item 10, resolved in favour of per-pack granularity per design §A4.1).
    /// Pure aside from the supplied IPackFileSource; no direct System.IO side effects of its own beyond
    /// path-string composition (System.IO.Path is available in netstandard2.0 and touches no disk).
    /// </summary>
    public static class PackLoader
    {
        public static LoadResult Load(IPackFileSource source, IJsonCodec codec, string root)
        {
            var findings = new List<Finding>();

            var dirs = (source.ListPackDirectories(root) ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();

            var loaded = new List<FollowerPack>();
            foreach (var dir in dirs)
            {
                var pack = TryLoadPack(source, codec, dir, findings);
                if (pack != null) loaded.Add(pack);
            }

            var sorted = TopoSort(loaded, findings);
            return new LoadResult { Packs = sorted, Findings = findings };
        }

        private static FollowerPack TryLoadPack(IPackFileSource source, IJsonCodec codec, string packDir, List<Finding> findings)
        {
            string manifestId = null;
            try
            {
                var manifestPath = Combine(packDir, "pack.json");
                if (!source.Exists(manifestPath))
                {
                    findings.Add(Finding.Error(packDir, null, "manifest_missing", $"pack.json missing under '{packDir}' -- pack skipped."));
                    return null;
                }

                var manifest = codec.Deserialize<PackManifest>(ReadText(source, manifestPath));
                if (manifest == null || string.IsNullOrEmpty(manifest.Id))
                {
                    findings.Add(Finding.Error(packDir, null, "manifest_invalid", $"pack.json under '{packDir}' has no 'id' -- pack skipped."));
                    return null;
                }
                manifestId = manifest.Id;

                var followersPath = Combine(packDir, "followers.json");
                if (!source.Exists(followersPath))
                {
                    findings.Add(Finding.Error(manifestId, null, "followers_missing", "followers.json is required -- pack skipped."));
                    return null;
                }
                var followers = codec.Deserialize<Dictionary<string, FollowerEntry>>(ReadText(source, followersPath))
                                 ?? new Dictionary<string, FollowerEntry>();

                var characters = new Dictionary<string, CharacterEntry>();
                var charactersPath = Combine(packDir, "characters.json");
                if (source.Exists(charactersPath))
                    characters = codec.Deserialize<Dictionary<string, CharacterEntry>>(ReadText(source, charactersPath))
                                 ?? new Dictionary<string, CharacterEntry>();

                var localization = new Dictionary<string, string>();
                var locPath = Combine(packDir, Combine("localization", "en.json"));
                if (source.Exists(locPath))
                    localization = codec.Deserialize<Dictionary<string, string>>(ReadText(source, locPath))
                                   ?? new Dictionary<string, string>();

                var provenance = new Dictionary<string, ProvenanceEntry>();
                var provPath = Combine(packDir, "provenance.json");
                if (source.Exists(provPath))
                    provenance = codec.Deserialize<Dictionary<string, ProvenanceEntry>>(ReadText(source, provPath))
                                 ?? new Dictionary<string, ProvenanceEntry>();

                var files = new List<(string RelPath, byte[] Bytes)>();
                foreach (var filePath in (source.ListFilesRecursive(packDir) ?? Enumerable.Empty<string>())
                         .OrderBy(p => p, StringComparer.Ordinal))
                {
                    files.Add((MakeRelative(packDir, filePath), source.ReadAllBytes(filePath)));
                }

                return new FollowerPack
                {
                    Manifest = manifest,
                    Followers = followers,
                    Characters = characters,
                    Localization = localization,
                    Provenance = provenance,
                    SourcePath = packDir,
                    Files = files
                };
            }
            catch (Exception ex)
            {
                findings.Add(Finding.Error(manifestId ?? packDir, null, "pack_parse_error",
                    $"pack under '{packDir}' failed to load: {ex.Message} -- pack disabled, other packs unaffected."));
                return null;
            }
        }

        /// <summary>
        /// Dependency-respecting topological sort with global tiebreak (LoadOrder asc, Id ordinal asc)
        /// (design §A3.3). Packs whose dependency id doesn't exist among the discovered set are
        /// "missing dependency" (distinct diagnostic from a genuine cycle among present packs).
        /// </summary>
        private static List<FollowerPack> TopoSort(List<FollowerPack> packs, List<Finding> findings)
        {
            var byId = new Dictionary<string, FollowerPack>(StringComparer.Ordinal);
            foreach (var p in packs)
            {
                if (byId.ContainsKey(p.Manifest.Id))
                {
                    findings.Add(Finding.Error(p.Manifest.Id, null, "duplicate_pack_id",
                        $"duplicate pack id '{p.Manifest.Id}' -- later occurrence skipped."));
                    continue;
                }
                byId[p.Manifest.Id] = p;
            }

            var knownIds = new HashSet<string>(byId.Keys, StringComparer.Ordinal);

            var missingDep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in byId.Values)
            {
                foreach (var dep in p.Manifest.Dependencies ?? Array.Empty<string>())
                {
                    if (!knownIds.Contains(dep))
                    {
                        findings.Add(Finding.Error(p.Manifest.Id, null, "missing_dependency",
                            $"pack '{p.Manifest.Id}' depends on unknown pack '{dep}' -- pack skipped."));
                        missingDep.Add(p.Manifest.Id);
                        break;
                    }
                }
            }

            var candidateIds = new HashSet<string>(knownIds.Where(id => !missingDep.Contains(id)), StringComparer.Ordinal);
            var pending = byId.Values.Where(p => candidateIds.Contains(p.Manifest.Id)).ToList();

            var resolved = new List<FollowerPack>();
            var resolvedIds = new HashSet<string>(StringComparer.Ordinal);

            while (pending.Count > 0)
            {
                var ready = pending
                    .Where(p => (p.Manifest.Dependencies ?? Array.Empty<string>())
                        .Where(candidateIds.Contains)
                        .All(resolvedIds.Contains))
                    .OrderBy(p => p.Manifest.LoadOrder)
                    .ThenBy(p => p.Manifest.Id, StringComparer.Ordinal)
                    .ToList();

                if (ready.Count == 0)
                {
                    foreach (var p in pending.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal))
                        findings.Add(Finding.Error(p.Manifest.Id, null, "dependency_cycle",
                            $"pack '{p.Manifest.Id}' is part of a dependency cycle -- pack skipped."));
                    break;
                }

                var next = ready[0];
                resolved.Add(next);
                resolvedIds.Add(next.Manifest.Id);
                pending.Remove(next);
            }

            return resolved;
        }

        private static string Combine(string a, string b) => Path.Combine(a, b);

        private static string MakeRelative(string packDir, string fullPath)
        {
            var rel = fullPath;
            if (rel.StartsWith(packDir, StringComparison.Ordinal))
                rel = rel.Substring(packDir.Length).TrimStart('\\', '/');
            return rel.Replace('\\', '/');
        }

        private static string ReadText(IPackFileSource source, string path)
        {
            var bytes = source.ReadAllBytes(path);
            // Strip a UTF-8 BOM if present (matches the game's own tolerant JSON reading; see build-template-notes.md).
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
