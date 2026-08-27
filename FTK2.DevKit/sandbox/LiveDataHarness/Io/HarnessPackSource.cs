using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Summoner.Core.Packs;

namespace LiveDataHarness.Io
{
    /// <summary>IPackFileSource over the real filesystem, mirroring
    /// Summoner.Plugin/Adapters/FileSystemPackSource without the BepInEx coupling. Read-only.</summary>
    public sealed class HarnessPackSource : IPackFileSource
    {
        public IEnumerable<string> ListPackDirectories(string root)
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0];
        }

        public bool Exists(string path) { return File.Exists(path) || Directory.Exists(path); }

        public byte[] ReadAllBytes(string path) { return File.ReadAllBytes(path); }

        public IEnumerable<string> ListFilesRecursive(string packDir)
        {
            return Directory.Exists(packDir)
                ? Directory.GetFiles(packDir, "*", SearchOption.AllDirectories)
                : new string[0];
        }
    }

    /// <summary>
    /// IJsonCodec over System.Text.Json (BCL, no NuGet). PropertyNameCaseInsensitive is MANDATORY:
    /// Summoner.Core.Packs.PackManifest declares PascalCase properties (Id, LoadOrder, Dependencies) while
    /// every shipped pack.json uses lowercase-camel keys. Without it every manifest deserializes with a
    /// null Id and PackLoader skips the pack as "manifest_invalid" — 0 packs loaded, all checks vacuous.
    /// </summary>
    public sealed class HarnessJsonCodec : IJsonCodec
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public T Deserialize<T>(string json) { return JsonSerializer.Deserialize<T>(json, Options); }
    }
}
