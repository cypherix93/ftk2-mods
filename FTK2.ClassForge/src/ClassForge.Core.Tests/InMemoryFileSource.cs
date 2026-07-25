using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassForge.Core.IO;

namespace ClassForge.Core.Tests;

/// <summary>
/// In-memory <see cref="IFileSource"/> fixture used to test pack discovery/parsing/hashing without touching
/// disk. Directory listing order follows insertion order (a List, not a Dictionary/HashSet) specifically so
/// tests can deliberately shuffle it and prove PackDiscovery's alphabetical re-sort makes the result
/// order-independent (SPEC.md §3: "never trust filesystem/OS directory-listing order").
/// </summary>
public sealed class InMemoryFileSource : IFileSource
{
    private readonly Dictionary<string, string> _textFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _binaryFiles = new(StringComparer.Ordinal);
    private readonly List<string> _dirs = new();

    public void AddFile(string path, string content)
    {
        path = Normalize(path);
        _textFiles[path] = content;
        RegisterDirs(path);
    }

    /// <summary>Registers a file by raw bytes rather than text — used by DataHasher's PNG-bytes tests, where
    /// the fixture content must not be valid UTF-8 (or must contain byte sequences, like a PNG header's own
    /// 0x0D 0x0A, that must survive hashing completely untouched — see MP review M5).</summary>
    public void AddBinaryFile(string path, byte[] content)
    {
        path = Normalize(path);
        _binaryFiles[path] = content;
        RegisterDirs(path);
    }

    private void RegisterDirs(string path)
    {
        var parts = path.Split('/');
        var cur = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            cur = cur.Length == 0 ? parts[i] : cur + "/" + parts[i];
            if (!_dirs.Contains(cur)) _dirs.Add(cur);
        }
    }

    /// <summary>Re-orders the internal directory list (test hook for the determinism test — simulates a different OS enumeration order).</summary>
    public void ShuffleDirectoryOrder(IEnumerable<string> newOrder)
    {
        var reordered = newOrder.Select(Normalize).ToList();
        // Keep any dirs not explicitly mentioned, appended at the end, so this stays safe to call partially.
        foreach (var d in _dirs) if (!reordered.Contains(d)) reordered.Add(d);
        _dirs.Clear();
        _dirs.AddRange(reordered);
    }

    private static string Normalize(string p) => p.Replace('\\', '/').TrimEnd('/');

    public bool DirectoryExists(string path)
    {
        var norm = Normalize(path);
        return _dirs.Contains(norm) || AllPaths().Any(f => f.StartsWith(norm + "/", StringComparison.Ordinal));
    }

    public bool FileExists(string path)
    {
        var norm = Normalize(path);
        return _textFiles.ContainsKey(norm) || _binaryFiles.ContainsKey(norm);
    }

    public string ReadAllText(string path)
    {
        var norm = Normalize(path);
        if (_textFiles.TryGetValue(norm, out var text)) return text;
        if (_binaryFiles.TryGetValue(norm, out var bytes)) return Encoding.UTF8.GetString(bytes);
        throw new System.IO.FileNotFoundException(norm);
    }

    public byte[] ReadAllBytes(string path)
    {
        var norm = Normalize(path);
        if (_binaryFiles.TryGetValue(norm, out var bytes)) return bytes;
        if (_textFiles.TryGetValue(norm, out var text)) return Encoding.UTF8.GetBytes(text);
        throw new System.IO.FileNotFoundException(norm);
    }

    public IEnumerable<string> GetDirectories(string path)
    {
        var norm = Normalize(path);
        foreach (var d in _dirs)
        {
            var idx = d.LastIndexOf('/');
            var parent = idx < 0 ? "" : d.Substring(0, idx);
            if (parent == norm) yield return d;
        }
    }

    public IEnumerable<string> GetFiles(string path, string searchPattern, bool recursive)
    {
        var norm = Normalize(path) + "/";
        var candidates = AllPaths().Where(f => f.StartsWith(norm, StringComparison.Ordinal));
        if (!recursive)
            candidates = candidates.Where(f => !f.Substring(norm.Length).Contains('/'));
        return candidates.Where(f => MatchesPattern(f.Substring(f.LastIndexOf('/') + 1), searchPattern)).OrderBy(f => f, StringComparer.Ordinal);
    }

    private IEnumerable<string> AllPaths() => _textFiles.Keys.Concat(_binaryFiles.Keys);

    private static bool MatchesPattern(string fileName, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith("*.")) return fileName.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
        return string.Equals(fileName, pattern, StringComparison.Ordinal);
    }

    public string CombinePath(params string[] parts) => Normalize(string.Join("/", parts));
}
