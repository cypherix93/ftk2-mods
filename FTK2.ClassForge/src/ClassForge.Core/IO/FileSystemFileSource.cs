using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassForge.Core.IO
{
    /// <summary>Real-disk <see cref="IFileSource"/>, used by ClassForge.Plugin at runtime.</summary>
    public sealed class FileSystemFileSource : IFileSource
    {
        public bool DirectoryExists(string path) => !string.IsNullOrEmpty(path) && Directory.Exists(path);

        public bool FileExists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

        public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public IEnumerable<string> GetDirectories(string path)
            => DirectoryExists(path) ? Directory.GetDirectories(path) : Enumerable.Empty<string>();

        public IEnumerable<string> GetFiles(string path, string searchPattern, bool recursive)
            => DirectoryExists(path)
                ? Directory.GetFiles(path, searchPattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>();

        public string CombinePath(params string[] parts)
        {
            if (parts == null || parts.Length == 0) return string.Empty;
            var result = parts[0];
            for (int i = 1; i < parts.Length; i++)
                result = Path.Combine(result, parts[i]);
            return result;
        }
    }
}
