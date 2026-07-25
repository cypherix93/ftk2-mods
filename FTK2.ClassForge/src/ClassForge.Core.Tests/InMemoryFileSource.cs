using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<string> _dirs = new();

    public void AddFile(string path, string content)
    {
        path = Normalize(path);
        _files[path] = content;
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
        return _dirs.Contains(norm) || _files.Keys.Any(f => f.StartsWith(norm + "/", StringComparison.Ordinal));
    }

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public string ReadAllText(string path) => _files[Normalize(path)];

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
        var candidates = _files.Keys.Where(f => f.StartsWith(norm, StringComparison.Ordinal));
        if (!recursive)
            candidates = candidates.Where(f => !f.Substring(norm.Length).Contains('/'));
        return candidates.Where(f => MatchesPattern(f.Substring(f.LastIndexOf('/') + 1), searchPattern)).OrderBy(f => f, StringComparer.Ordinal);
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith("*.")) return fileName.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
        return string.Equals(fileName, pattern, StringComparison.Ordinal);
    }

    public string CombinePath(params string[] parts) => Normalize(string.Join("/", parts));
}
