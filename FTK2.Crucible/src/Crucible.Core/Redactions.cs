using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Field-path globs excluded from the v2 state digest.
    ///
    /// These are values that legitimately differ between healthy peers or that tick on their own;
    /// leaving them in would make every cross-peer comparison report a false desync.
    ///
    /// Paths are dotted and array elements inherit their array's path (see
    /// <c>StateDigest.RedactNode</c>), so <c>combat.combatants.id</c> covers every combatant's id.
    ///
    /// Redacting a field does NOT hide it: it is still in the snapshot body that humans and
    /// assertions read. It is only excluded from the cross-peer hash.
    /// </summary>
    public static class Redactions
    {
        public static readonly string[] V2 = new string[]
        {
            "instance",                                    // peer identity, by definition different
            "warnings",                                    // one peer noticing a miss is not a desync
            "console",                                     // local UI state
            "network.isHost",                              // exactly one peer is the host
            "combat.episode",                              // object-identity hash, local to a process
            "combat.activeId",                             // entity guid; 'phase' carries the meaning
            "combat.combatants.id",                        // entity guids
            "combat.combatants.statuses.originEntityId"    // entity guids
        };

        /// <summary>Defensive copy: callers must not be able to mutate the shared set.</summary>
        public static string[] CopyV2()
        {
            string[] copy = new string[V2.Length];
            Array.Copy(V2, copy, V2.Length);
            return copy;
        }
    }
}
