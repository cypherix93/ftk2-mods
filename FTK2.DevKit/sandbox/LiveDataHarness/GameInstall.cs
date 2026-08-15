using System.IO;

namespace LiveDataHarness
{
    /// <summary>
    /// Locates a For The King II install. The candidate list is kept in sync with tools/deploy.ps1's
    /// Resolve-GameDir so the harness and the deployer can never disagree about which install is "the"
    /// install — a harness that validated against a different copy of the game than the one being
    /// deployed to would be worse than no harness at all.
    /// </summary>
    public sealed class GameInstall
    {
        public string Root { get; private set; }
        public string ManagedDir { get { return Path.Combine(Root, "For The King II_Data", "Managed"); } }
        public string StreamingAssetsDir { get { return Path.Combine(Root, "For The King II_Data", "StreamingAssets", "Assets"); } }

        private GameInstall(string root) { Root = root; }

        private static readonly string[] Candidates =
        {
            @"E:\Games\Steam\steamapps\common\For The King II",
            @"C:\Program Files (x86)\Steam\steamapps\common\For The King II",
            @"C:\Program Files\Steam\steamapps\common\For The King II",
        };

        /// <summary>Returns null when no install is found, which callers treat as "skipped", not "failed".</summary>
        public static GameInstall Resolve(string requested)
        {
            if (!string.IsNullOrEmpty(requested))
                return Probe(requested);
            for (int i = 0; i < Candidates.Length; i++)
            {
                var hit = Probe(Candidates[i]);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GameInstall Probe(string root)
        {
            var marker = Path.Combine(root, "For The King II_Data", "Managed", "FTK2.dll");
            return File.Exists(marker) ? new GameInstall(root) : null;
        }
    }
}
