namespace Summoner.Core.Diagnostics
{
    public enum FindingSeverity
    {
        Info,
        Warn,
        Error
    }

    /// <summary>Uniform diagnostic record emitted by PackLoader, PackValidator and MergePlanner.</summary>
    public sealed class Finding
    {
        public FindingSeverity Severity { get; set; }
        public string PackId { get; set; }
        public string EntityId { get; set; }
        public string Check { get; set; }
        public string Message { get; set; }

        public static Finding Error(string packId, string entityId, string check, string message)
            => new Finding { Severity = FindingSeverity.Error, PackId = packId, EntityId = entityId, Check = check, Message = message };

        public static Finding Warn(string packId, string entityId, string check, string message)
            => new Finding { Severity = FindingSeverity.Warn, PackId = packId, EntityId = entityId, Check = check, Message = message };

        public static Finding Info(string packId, string entityId, string check, string message)
            => new Finding { Severity = FindingSeverity.Info, PackId = packId, EntityId = entityId, Check = check, Message = message };

        public override string ToString()
            => $"[{Severity}] {PackId ?? "(pack)"}:{EntityId ?? "(entry)"} [{Check}] {Message}";
    }
}
