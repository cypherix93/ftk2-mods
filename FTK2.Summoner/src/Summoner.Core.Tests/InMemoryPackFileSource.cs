using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Summoner.Core.Tests
{
    /// <summary>
    /// Hand-rolled in-memory IPackFileSource for the synthetic edge-case fixtures (missing
    /// dependency, dependency cycle, ordinal ordering, ...) where standing up real files on disk
    /// would be more ceremony than signal. The filesystem-backed happy-path / malformed-JSON tests
    /// use TestFileSystemPackSource against Summoner.Core.Tests/fixtures/ instead.
    /// </summary>
    public sealed class InMemoryPackFileSource : Summoner.Core.Packs.IPackFileSource
    {
        private readonly Dictionary<string, List<string>> _dirsByRoot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _filesByPackDir = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public void AddPackDir(string root, string packDir)
        {
            if (!_dirsByRoot.TryGetValue(root, out var list))
            {
                list = new List<string>();
                _dirsByRoot[root] = list;
            }
            list.Add(packDir);
        }

        public void AddFile(string packDir, string fileName, string content)
        {
            var path = Path.Combine(packDir, fileName);
            _files[path] = Encoding.UTF8.GetBytes(content);
            if (!_filesByPackDir.TryGetValue(packDir, out var list))
            {
                list = new List<string>();
                _filesByPackDir[packDir] = list;
            }
            list.Add(path);
        }

        /// <summary>Convenience: adds a minimal well-formed pack (pack.json + followers.json) under root.</summary>
        public void AddSimplePack(string root, string packDir, string id, int loadOrder, string[] dependencies, string followersJson)
        {
            AddPackDir(root, packDir);
            var deps = dependencies == null || dependencies.Length == 0
                ? "[]"
                : "[" + string.Join(",", dependencies.Select(d => "\"" + d + "\"")) + "]";
            var manifest = "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\",\"author\":\"test\"," +
                           "\"description\":\"\",\"loadOrder\":" + loadOrder + ",\"dependencies\":" + deps + ",\"enabled\":true}";
            AddFile(packDir, "pack.json", manifest);
            AddFile(packDir, "followers.json", followersJson);
        }

        public IEnumerable<string> ListPackDirectories(string root)
            => _dirsByRoot.TryGetValue(root, out var l) ? l : Enumerable.Empty<string>();

        public bool Exists(string path) => _files.ContainsKey(path);

        public byte[] ReadAllBytes(string path) => _files[path];

        public IEnumerable<string> ListFilesRecursive(string packDir)
            => _filesByPackDir.TryGetValue(packDir, out var l) ? l : Enumerable.Empty<string>();
    }
}
