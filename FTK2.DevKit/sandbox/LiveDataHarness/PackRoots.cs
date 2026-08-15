using System.Collections.Generic;
using System.IO;

namespace LiveDataHarness
{
    /// <summary>
    /// Resolves the repo's shipped pack directories by walking up from the executable, so the harness reads
    /// the packs as they exist in source rather than as they were last deployed. That is deliberate: a stale
    /// deployment would otherwise let the harness pass on content the repo no longer ships.
    /// </summary>
    public static class PackRoots
    {
        public static string RepoRoot()
        {
            var dir = System.AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "FTK2.ClassForge", "data", "ClassPacks")))
                    return dir;
                var parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            return null;
        }

        public static string[] ClassPackRoots()
        {
            var repo = RepoRoot();
            if (repo == null) return new string[0];
            var roots = new List<string>();
            var cf = Path.Combine(repo, "FTK2.ClassForge", "data", "ClassPacks");
            var bl = Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks");
            if (Directory.Exists(cf)) roots.Add(cf);
            if (Directory.Exists(bl)) roots.Add(bl);
            return roots.ToArray();
        }

        public static string FollowerPackRoot()
        {
            var repo = RepoRoot();
            if (repo == null) return null;
            var p = Path.Combine(repo, "FTK2.Summoner", "data", "FollowerPacks");
            return Directory.Exists(p) ? p : null;
        }

        public static string FixturesDir()
        {
            return Path.Combine(System.AppContext.BaseDirectory, "fixtures");
        }
    }
}
