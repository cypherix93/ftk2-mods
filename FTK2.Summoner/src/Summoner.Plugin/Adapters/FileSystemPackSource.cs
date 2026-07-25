using System.Collections.Generic;
using System.IO;
using Summoner.Core.Packs;

namespace Summoner.Plugin.Adapters
{
    /// <summary>
    /// IPackFileSource over System.IO (design §A4.2). Identifiers handed back by
    /// ListPackDirectories/ListFilesRecursive are plain absolute paths; Exists/ReadAllBytes accept
    /// those same paths (or a Path.Combine of one of them with a sub-path, which is what
    /// Summoner.Core.Packs.PackLoader does) directly -- no hidden root state, matches the
    /// IPackFileSource contract documented on the interface itself.
    /// </summary>
    public sealed class FileSystemPackSource : IPackFileSource
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
