using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Summoner.Core.Packs;

namespace LiveDataHarness.Io
{
    /// <summary>
    /// IPackFileSource over System.IO, semantically identical to the plugin's adapter but without the
    /// BepInEx-coupled assembly around it. Identifiers handed back are plain absolute paths, and
    /// Exists/ReadAllBytes accept those same paths.
    ///
    /// Exists is deliberately File.Exists alone rather than also accepting directories: the loader uses it
    /// to decide whether an optional pack file is present, and answering "yes" for a directory would make it
    /// try to read a directory as JSON.
    /// </summary>
    public sealed class HarnessPackSource : IPackFileSource
    {
        public IEnumerable<string> ListPackDirectories(string root)
        {
            if (!Directory.Exists(root)) yield break;
            foreach (var dir in Directory.GetDirectories(root))
                yield return dir;
        }

        public bool Exists(string path) { return File.Exists(path); }

        public byte[] ReadAllBytes(string path) { return File.ReadAllBytes(path); }

        public IEnumerable<string> ListFilesRecursive(string packDir)
        {
            if (!Directory.Exists(packDir)) yield break;
            foreach (var file in Directory.GetFiles(packDir, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }

    /// <summary>IJsonCodec over System.Text.Json, which keeps the harness on the BCL with no package feed.</summary>
    public sealed class HarnessJsonCodec : IJsonCodec
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public T Deserialize<T>(string json) { return JsonSerializer.Deserialize<T>(json, Options); }
    }
}
