using System.Collections.Generic;

namespace Summoner.Core.Packs
{
    /// <summary>
    /// Filesystem abstraction (design §A4.1) so Summoner.Core has no System.IO-shaped host
    /// dependency baked into its API surface — a fake in-memory implementation drives the test
    /// console runner, Summoner.Plugin's Adapters/FileSystemPackSource.cs drives the real game.
    ///
    /// Contract: <see cref="ListPackDirectories"/> returns opaque pack-directory identifiers
    /// (implementation-defined shape — the real adapter returns absolute directory paths, the
    /// test fake returns short in-memory keys). Every other method takes one of those identifiers,
    /// or a further path built by joining one of those identifiers with a sub-path, and treats it
    /// as an opaque handle — Summoner.Core never inspects or parses it.
    /// </summary>
    public interface IPackFileSource
    {
        /// <summary>Immediate child pack directories under <paramref name="root"/>. Order is not guaranteed —
        /// callers (PackLoader) apply the deterministic StringComparer.Ordinal sort themselves (design §A3.3).</summary>
        IEnumerable<string> ListPackDirectories(string root);

        bool Exists(string path);

        byte[] ReadAllBytes(string path);

        /// <summary>Every file (not directory) under <paramref name="packDir"/>, recursively, as identifiers
        /// usable with <see cref="Exists"/>/<see cref="ReadAllBytes"/>. Order is not guaranteed.</summary>
        IEnumerable<string> ListFilesRecursive(string packDir);
    }
}
