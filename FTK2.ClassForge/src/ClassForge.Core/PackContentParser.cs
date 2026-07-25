using System;
using System.Collections.Generic;
using System.IO;
using ClassForge.Core.IO;
using ClassForge.Core.Json;

namespace ClassForge.Core
{
    /// <summary>
    /// Parses one pack's content files — classes.json/traits.json/abilities.json/items.json (SPEC.md
    /// §4.2-§4.5), localization/en.json (§4.7), and icons/*.png + portraits/*.png (§4.8) — into plain
    /// id -&gt; raw-JSON-value dictionaries. Deliberately does not model CharacterConfig/ThingConfig/etc.:
    /// Core hands opaque parsed objects to the game layer (Plugin), which is the only place with the real
    /// game types available.
    /// </summary>
    public static class PackContentParser
    {
        public static ParsedPack Parse(IFileSource fs, DiscoveredPack pack, List<Finding> findings)
        {
            var result = new ParsedPack { PackId = pack.Manifest.Id };

            result.Classes = ParseObjectFile(fs, pack, "classes.json", findings);
            result.Traits = ParseObjectFile(fs, pack, "traits.json", findings);
            result.Abilities = ParseObjectFile(fs, pack, "abilities.json", findings);
            result.Items = ParseObjectFile(fs, pack, "items.json", findings);
            result.Localization = ParseLocalization(fs, pack, findings);
            result.Icons = ParseAssetDir(fs, pack, "icons", findings);
            result.Portraits = ParseAssetDir(fs, pack, "portraits", findings);

            // SPEC-DELTA-v1.1 §1 OQ#1: the native trait substrate keys purely on Thing.ConfigName starting with
            // "TRAIT_" (CharacterHelper.GiveTrait / InventoryHelper.GetTraits) — there is no eTraits bridge.
            // M1 still merges every traits.json entry into Configs.Things as inert data regardless of prefix
            // (SPEC.md §10 M1 milestone: "Traits merge into Configs.Things as inert data... not yet grantable").
            // A non-TRAIT_-prefixed id is flagged here (loudly, per CONVENTIONS.md) so a future M2 trait-bridge
            // pass — or a pack author — can fix it before that id needs to be actually granted in-game.
            foreach (var kv in result.Traits)
            {
                if (!kv.Key.StartsWith("TRAIT_", StringComparison.Ordinal))
                {
                    findings.Add(Finding.Warning("CF_TRAIT_PREFIX",
                        $"Trait id '{kv.Key}' in pack '{pack.Manifest.Id}' does not start with 'TRAIT_'. The native trait " +
                        "substrate (CharacterHelper.GiveTrait/RemoveTrait, InventoryHelper.GetTraits) keys on that literal " +
                        "ConfigName prefix (SPEC-DELTA-v1.1 §1). This entry still merges into Configs.Things as inert data " +
                        "for M1, but will not be grantable through the native trait API until renamed.",
                        pack.Manifest.Id));
                }
            }

            return result;
        }

        private static Dictionary<string, JsonValue> ParseObjectFile(IFileSource fs, DiscoveredPack pack, string fileName, List<Finding> findings)
        {
            var dict = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var path = fs.CombinePath(pack.RootDir, fileName);
            if (!fs.FileExists(path))
                return dict;

            try
            {
                var text = fs.ReadAllText(path);
                var json = JsonParser.Parse(text);
                if (json.Kind != JsonKind.Object)
                    throw new FormatException($"{fileName} root must be a JSON object.");
                foreach (var kv in json.AsObjectMembers)
                    dict[kv.Key] = kv.Value; // last-wins on duplicate keys within one file, matches JSON semantics
            }
            catch (Exception ex)
            {
                findings.Add(Finding.Error("CF_FILE_PARSE", $"Failed to parse {fileName} in pack '{pack.Manifest.Id}': {ex.Message}", pack.Manifest.Id));
            }

            return dict;
        }

        private static Dictionary<string, string> ParseLocalization(IFileSource fs, DiscoveredPack pack, List<Finding> findings)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = fs.CombinePath(pack.RootDir, "localization", "en.json");
            if (!fs.FileExists(path))
                return dict;

            try
            {
                var text = fs.ReadAllText(path);
                var json = JsonParser.Parse(text);
                if (json.Kind != JsonKind.Object)
                    throw new FormatException("localization/en.json root must be a JSON object.");
                foreach (var kv in json.AsObjectMembers)
                {
                    if (kv.Value.Kind != JsonKind.String)
                    {
                        findings.Add(Finding.Warning("CF_LOC_NON_STRING", $"localization/en.json key '{kv.Key}' in pack '{pack.Manifest.Id}' is not a string — skipped.", pack.Manifest.Id));
                        continue;
                    }
                    dict[kv.Key] = kv.Value.AsString;
                }
            }
            catch (Exception ex)
            {
                findings.Add(Finding.Error("CF_FILE_PARSE", $"Failed to parse localization/en.json in pack '{pack.Manifest.Id}': {ex.Message}", pack.Manifest.Id));
            }

            return dict;
        }

        private static Dictionary<string, string> ParseAssetDir(IFileSource fs, DiscoveredPack pack, string subDir, List<Finding> findings)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var dir = fs.CombinePath(pack.RootDir, subDir);
            if (!fs.DirectoryExists(dir))
                return map;

            foreach (var file in fs.GetFiles(dir, "*.png", false))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (map.ContainsKey(id))
                {
                    findings.Add(Finding.Warning("CF_ASSET_DUP", $"Duplicate {subDir} id '{id}' within pack '{pack.Manifest.Id}'.", pack.Manifest.Id));
                }
                map[id] = file;
            }

            return map;
        }
    }
}
