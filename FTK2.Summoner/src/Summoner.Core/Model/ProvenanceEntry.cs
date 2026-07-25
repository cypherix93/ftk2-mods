namespace Summoner.Core.Model
{
    /// <summary>
    /// Sidecar provenance record (design §A2.2). Never merged into game Configs — the game's
    /// FollowerCharacterConfig/CharacterConfig types have no Provenance field. Loaded into
    /// FollowerPack.Provenance for logging/diagnostics and included verbatim in the dataHash
    /// (it is authored data, not localization).
    /// </summary>
    public sealed class ProvenanceEntry
    {
        public string Source { get; set; }
        public string EorId { get; set; }
        public string PackageVersion { get; set; }
    }
}
