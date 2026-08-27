using System.Collections.Generic;
using System.IO;

namespace LiveDataHarness
{
    /// <summary>Resolves the repo's shipped pack directories by walking up from the executable — the same
    /// trick ClassForge.PackCheck's FindDefaultPack uses. Read-only; nothing here writes.</summary>
    public static class PackRoots
    {
        /// <summary>Repo root, or null when the harness has been copied out of the repo.</summary>
        public static string RepoRoot()
        {
            string dir = System.AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "FTK2.ClassForge", "data", "ClassPacks")))
                    return dir;
                DirectoryInfo parent = Directory.GetParent(dir);
                dir = parent != null ? parent.FullName : null;
            }
            return null;
        }

        /// <summary>Every root a ClassForge PackLoader pass should scan. Ordinal-sorted for AC5.</summary>
        public static string[] ClassPackRoots()
        {
            string repo = RepoRoot();
            if (repo == null) return new string[0];

            List<string> roots = new List<string>();
            string cf = Path.Combine(repo, "FTK2.ClassForge", "data", "ClassPacks");
            string bl = Path.Combine(repo, "FTK2.Blessings", "data", "ClassPacks");
            if (Directory.Exists(cf)) roots.Add(cf);
            if (Directory.Exists(bl)) roots.Add(bl);
            roots.Sort(System.StringComparer.Ordinal);
            return roots.ToArray();
        }

        public static string FollowerPackRoot()
        {
            string repo = RepoRoot();
            if (repo == null) return null;
            string p = Path.Combine(repo, "FTK2.Summoner", "data", "FollowerPacks");
            return Directory.Exists(p) ? p : null;
        }

        /// <summary>Deliberately-broken packs, copied next to the executable by the csproj.</summary>
        public static string FixturesDir()
        {
            return Path.Combine(System.AppContext.BaseDirectory, "fixtures");
        }
    }
}
