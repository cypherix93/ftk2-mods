using System.Collections.Generic;
using System.IO;
using Summoner.Core.Packs;

namespace Summoner.Core.Tests
{
    /// <summary>
    /// Real-filesystem IPackFileSource used to drive the fixtures/ directory tree. Same shape as
    /// Summoner.Plugin/Adapters/FileSystemPackSource.cs (deliberately -- this is the same adapter
    /// contract, just living in the test project so Summoner.Core.Tests never needs to reference
    /// Summoner.Plugin, which pulls in game DLL references).
    /// </summary>
    public sealed class TestFileSystemPackSource : IPackFileSource
    {
        public IEnumerable<string> ListPackDirectories(string root)
        {
            if (!Directory.Exists(root)) yield break;
            foreach (var dir in Directory.GetDirectories(root))
                yield return dir;
        }

        public bool Exists(string path) => File.Exists(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public IEnumerable<string> ListFilesRecursive(string packDir)
        {
            if (!Directory.Exists(packDir)) yield break;
            foreach (var file in Directory.GetFiles(packDir, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }
}
