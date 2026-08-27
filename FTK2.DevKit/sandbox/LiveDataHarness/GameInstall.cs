using System.IO;

namespace LiveDataHarness
{
    /// <summary>Locates a For The King II install. Read-only: every member is a path accessor or an
    /// Exists probe. Nothing in this class or anything downstream ever writes to the game folder.</summary>
    public sealed class GameInstall
    {
        public string Root { get; private set; }

        public string ManagedDir
        {
            get { return Path.Combine(Root, "For The King II_Data", "Managed"); }
        }

        public string StreamingAssetsDir
        {
            get { return Path.Combine(Root, "For The King II_Data", "StreamingAssets", "Assets"); }
        }

        private GameInstall(string root) { Root = root; }

        private static readonly string[] Candidates =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\For The King II",
            @"C:\Program Files\Steam\steamapps\common\For The King II",
            @"E:\Games\Steam\steamapps\common\For The King II",
        };

        /// <summary>Returns null when no install is found; the caller exits 2 (skipped, not failed).</summary>
        public static GameInstall Resolve(string requested)
        {
            if (!string.IsNullOrEmpty(requested)) return Probe(requested);
            for (int i = 0; i < Candidates.Length; i++)
            {
                GameInstall hit = Probe(Candidates[i]);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GameInstall Probe(string root)
        {
            string marker = Path.Combine(root, "For The King II_Data", "Managed", "FTK2.dll");
            return File.Exists(marker) ? new GameInstall(root) : null;
        }
    }
}
