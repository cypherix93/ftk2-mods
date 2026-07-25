using System;

namespace Summoner.Core.Packs
{
    /// <summary>
    /// pack.json shape, verbatim from ClassForge SPEC §4.1 (design §A2.1) so one manifest concept
    /// serves both engines' load-order algorithm and hashing rule.
    /// </summary>
    public sealed class PackManifest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public int LoadOrder { get; set; }
        public string[] Dependencies { get; set; } = Array.Empty<string>();
        public bool Enabled { get; set; } = true;
    }
}
