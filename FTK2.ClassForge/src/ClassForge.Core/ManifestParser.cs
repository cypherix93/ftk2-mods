using System;
using System.Collections.Generic;
using ClassForge.Core.Json;

namespace ClassForge.Core
{
    /// <summary>Parses and validates <c>pack.json</c> (SPEC.md §4.1).</summary>
    public static class ManifestParser
    {
        /// <returns>The parsed manifest, or null if pack.json was unparseable/invalid (a Finding is always added in that case — fail-safe, per CONVENTIONS.md: log loudly, skip).</returns>
        public static PackManifest Parse(string json, string rootDir, List<Finding> findings)
        {
            JsonValue root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                findings.Add(Finding.Error("CF_MANIFEST_PARSE", $"pack.json at '{rootDir}' failed to parse: {ex.Message}", null));
                return null;
            }

            if (root.Kind != JsonKind.Object)
            {
                findings.Add(Finding.Error("CF_MANIFEST_SHAPE", $"pack.json at '{rootDir}' must have a JSON object as its root.", null));
                return null;
            }

            var id = root.GetString("id");
            if (string.IsNullOrEmpty(id))
            {
                findings.Add(Finding.Error("CF_MANIFEST_ID", $"pack.json at '{rootDir}' is missing a non-empty 'id'.", null));
                return null;
            }

            if (!id.StartsWith("CF_PACK_", StringComparison.Ordinal))
            {
                findings.Add(Finding.Warning("CF_PACK_ID_CONVENTION",
                    $"Pack id '{id}' does not follow the 'CF_PACK_<NAME>' convention (SPEC.md §4.1) — loading anyway.", id));
            }

            return new PackManifest
            {
                Id = id,
                Name = root.GetString("name", id),
                Version = root.GetString("version", "0.0.0"),
                Author = root.GetString("author", ""),
                Description = root.GetString("description", ""),
                LoadOrder = (int)root.GetNumber("loadOrder", 0),
                Dependencies = root.GetStringArray("dependencies"),
                Enabled = root.GetBool("enabled", true),
                RootDir = rootDir
            };
        }
    }
}
