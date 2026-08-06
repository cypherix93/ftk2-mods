using System.Collections.Generic;

namespace Blessings.Core.Model
{
    /// <summary>One <c>blessings.json</c> roster entry (SPEC §4.1). Immutable — parsed once, never mutated.</summary>
    public sealed class BlessingEntry
    {
        /// <summary>Roster key, e.g. "BLSS_BLOOD_PRICE" (the <c>[Blessings] Mode</c> literal-id vocabulary).</summary>
        public string Id { get; private set; }

        /// <summary>The hidden <c>TRAIT_BLSS_*</c> ThingConfig id this entry grants, e.g. "TRAIT_BLSS_BLOOD_PRICE".</summary>
        public string TraitId { get; private set; }

        /// <summary>Authored weight, VERBATIM (may be &lt;= 0 -- callers apply <c>Max(1, Weight)</c> themselves,
        /// mirroring EOR L832/L837; never pre-clamped here so the raw authored value stays inspectable).</summary>
        public int Weight { get; private set; }

        /// <summary>Roster gate (§3.4): disabled entries never enter the weighted table and cannot be
        /// selected by a literal <c>Mode</c> reference either.</summary>
        public bool Enabled { get; private set; }

        /// <summary>EOR tags, informational only in v0 (§4.1).</summary>
        public IReadOnlyList<string> Tags { get; private set; }

        public BlessingEntry(string id, string traitId, int weight, bool enabled, IReadOnlyList<string> tags)
        {
            Id = id;
            TraitId = traitId;
            Weight = weight;
            Enabled = enabled;
            Tags = tags ?? new List<string>();
        }

        public override string ToString() => Id + " (" + TraitId + ", w=" + Weight + ", enabled=" + Enabled + ")";
    }
}
