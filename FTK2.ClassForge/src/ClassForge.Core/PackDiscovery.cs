using System;
using System.Collections.Generic;
using System.Linq;
using ClassForge.Core.IO;

namespace ClassForge.Core
{
    /// <summary>
    /// Scans a set of root directories for <c>&lt;root&gt;/&lt;PackName&gt;/pack.json</c> folders (SPEC.md §3).
    /// Discovery order out of the filesystem is never trusted: the result is always sorted alphabetically by
    /// pack id before being returned, per SPEC.md §3/§9.3a and MULTIPLAYER.md R2 (deterministic merge order
    /// across peers with no network coordination required).
    /// </summary>
    public static class PackDiscovery
    {
        public static List<DiscoveredPack> Discover(IFileSource fs, IEnumerable<string> roots, List<Finding> findings)
        {
            var found = new List<DiscoveredPack>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(root) || !fs.DirectoryExists(root))
                    continue;

                foreach (var dir in fs.GetDirectories(root))
                {
                    var manifestPath = fs.CombinePath(dir, "pack.json");
                    if (!fs.FileExists(manifestPath))
                        continue;

                    string text;
                    try
                    {
                        text = fs.ReadAllText(manifestPath);
                    }
                    catch (Exception ex)
                    {
                        findings.Add(Finding.Error("CF_MANIFEST_READ", $"Failed to read '{manifestPath}': {ex.Message}", null));
                        continue;
                    }

                    var manifest = ManifestParser.Parse(text, dir, findings);
                    if (manifest == null)
                        continue;

                    if (!seenIds.Add(manifest.Id))
                    {
                        findings.Add(Finding.Error("CF_DUPLICATE_PACK_ID",
                            $"Pack id '{manifest.Id}' was discovered more than once (folders: previously found + '{dir}') — the later one is ignored.", manifest.Id));
                        continue;
                    }

                    found.Add(new DiscoveredPack(manifest, dir));
                }
            }

            // Deterministic discovery — never trust filesystem/OS directory-listing order (SPEC.md §3).
            return found.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).ToList();
        }
    }
}
