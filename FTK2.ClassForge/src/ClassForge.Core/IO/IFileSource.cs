using System.Collections.Generic;

namespace ClassForge.Core.IO
{
    /// <summary>
    /// Filesystem abstraction so pack discovery/parsing/hashing can be driven by a real directory tree
    /// (<see cref="FileSystemFileSource"/>) in the Plugin, or by an in-memory fixture in tests — including
    /// tests that deliberately shuffle directory-listing order, since SPEC.md §3 requires discovery to never
    /// trust filesystem/OS enumeration order.
    /// </summary>
    public interface IFileSource
    {
        bool DirectoryExists(string path);
        bool FileExists(string path);
        string ReadAllText(string path);

        /// <summary>Immediate subdirectories of <paramref name="path"/>, in whatever order the underlying source returns them (callers must sort before treating order as meaningful).</summary>
        IEnumerable<string> GetDirectories(string path);

        /// <summary>Files under <paramref name="path"/> matching a simple "*" / "*.ext" pattern.</summary>
        IEnumerable<string> GetFiles(string path, string searchPattern, bool recursive);

        string CombinePath(params string[] parts);
    }
}
