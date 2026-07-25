using System.Collections.Generic;
using Summoner.Core.Model;

namespace Summoner.Core.Packs
{
    /// <summary>
    /// One loaded FollowerPacks/&lt;PackId&gt;/ directory (design §A2.1, §A4.1).
    /// </summary>
    public sealed class FollowerPack
    {
        public PackManifest Manifest { get; set; }
        public Dictionary<string, FollowerEntry> Followers { get; set; } = new Dictionary<string, FollowerEntry>();
        public Dictionary<string, CharacterEntry> Characters { get; set; } = new Dictionary<string, CharacterEntry>();

        /// <summary>Flat {"<ID>": name, "<ID>_DESCRIPTION": desc} from localization/en.json.</summary>
        public Dictionary<string, string> Localization { get; set; } = new Dictionary<string, string>();

        /// <summary>Keyed by the emitted SMN_ id. Never merged into game Configs (design §A2.2).</summary>
        public Dictionary<string, ProvenanceEntry> Provenance { get; set; } = new Dictionary<string, ProvenanceEntry>();

        /// <summary>Identifier of this pack's directory as returned by IPackFileSource (host-defined shape).</summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// Every file under this pack's directory, path relative to the pack root (forward-slash
        /// normalized), for DataHasher (design §A3.4). Includes provenance.json; excludes nothing
        /// here — the localization/** exclusion is DataHasher's job, not the loader's.
        /// </summary>
        public List<(string RelPath, byte[] Bytes)> Files { get; set; } = new List<(string RelPath, byte[] Bytes)>();
    }
}
