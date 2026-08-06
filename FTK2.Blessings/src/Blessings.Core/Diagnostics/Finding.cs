namespace Blessings.Core.Diagnostics
{
    public enum FindingSeverity
    {
        Info,
        Warn,
        Error
    }

    /// <summary>Uniform diagnostic record, same shape as Summoner.Core.Diagnostics.Finding /
    /// ClassForge.Core's Finding — emitted by <c>BlessingsRegistryParser</c>.</summary>
    public sealed class Finding
    {
        public FindingSeverity Severity { get; private set; }
        public string EntityId { get; private set; }
        public string Check { get; private set; }
        public string Message { get; private set; }

        private Finding(FindingSeverity severity, string entityId, string check, string message)
        {
            Severity = severity;
            EntityId = entityId;
            Check = check;
            Message = message;
        }

        public static Finding Error(string entityId, string check, string message)
            => new Finding(FindingSeverity.Error, entityId, check, message);

        public static Finding Warn(string entityId, string check, string message)
            => new Finding(FindingSeverity.Warn, entityId, check, message);

        public static Finding Info(string entityId, string check, string message)
            => new Finding(FindingSeverity.Info, entityId, check, message);

        public override string ToString()
            => "[" + Severity + "] " + (EntityId ?? "(blessings.json)") + " [" + Check + "] " + Message;
    }
}
