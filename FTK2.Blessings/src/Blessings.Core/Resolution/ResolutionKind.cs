namespace Blessings.Core.Resolution
{
    /// <summary>How a <c>[Blessings] Mode</c> value resolved (§3.4).</summary>
    public enum ResolutionKind
    {
        /// <summary>No-op: <c>Mode="Disabled"</c>, an unresolvable literal id, a literal id pointing at a
        /// disabled roster entry, or <c>Random</c> with no enabled candidates. <see cref="ResolutionResult.Resolved"/>
        /// is null in every case -- "treat as Disabled, never guess" (§3.4).</summary>
        Disabled,

        /// <summary>Deterministic hash-derived weighted pick (§3.4). Zero RNG draws.</summary>
        Random,

        /// <summary><c>Mode</c> named a specific, enabled roster id verbatim.</summary>
        Literal
    }
}
