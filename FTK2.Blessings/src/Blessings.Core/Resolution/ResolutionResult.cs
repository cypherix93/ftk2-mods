using Blessings.Core.Model;

namespace Blessings.Core.Resolution
{
    /// <summary>Result of <see cref="BlessingResolver.Resolve"/>. Pure data -- no logging, no side effects;
    /// callers (Blessings.Plugin) decide how to log <see cref="Message"/>.</summary>
    public sealed class ResolutionResult
    {
        public string ModeRaw { get; internal set; }
        public ResolutionKind Kind { get; internal set; }

        /// <summary>The resolved blessing, or null when <see cref="Kind"/> is <see cref="ResolutionKind.Disabled"/>.</summary>
        public BlessingEntry Resolved { get; internal set; }

        /// <summary>Human-readable explanation, set whenever something noteworthy happened (unknown id,
        /// disabled roster entry named literally, empty candidate set, etc.). Null on the ordinary
        /// "Mode=Disabled" / successful-pick paths.</summary>
        public string Message { get; internal set; }
    }
}
