using System;
using System.Collections.Generic;

namespace Blessings.Core.Resolution
{
    /// <summary>
    /// Party-wide grant planning (§3.5). Pure -- takes the party's entity guids and a per-guid
    /// "already holds the trait" flag (both supplied by the caller, since only Blessings.Plugin can read
    /// live game state), and emits an idempotent plan: entries already holding the trait are marked
    /// "skip", so re-running the plan after granting produces an all-skip plan (§8.2 item 3).
    /// </summary>
    public static class GrantPlanner
    {
        /// <summary>
        /// Builds the grant plan, sorted by <b>ascending ordinal <c>Entity.Guid</c></b> -- SPEC §3.5's
        /// "house determinism style" invariant (ClassForge SPEC §4.6 invariant 4): never trust the
        /// caller's input order, never dictionary/enumeration order.
        /// </summary>
        public static List<GrantPlanEntry> Plan(IEnumerable<string> partyEntityGuids, IReadOnlyDictionary<string, bool> alreadyHasTraitByGuid)
        {
            var guids = new List<string>();
            if (partyEntityGuids != null)
                foreach (var g in partyEntityGuids)
                    if (!string.IsNullOrEmpty(g)) guids.Add(g);

            guids.Sort(StringComparer.Ordinal);

            var plan = new List<GrantPlanEntry>(guids.Count);
            foreach (var guid in guids)
            {
                bool already = alreadyHasTraitByGuid != null && alreadyHasTraitByGuid.TryGetValue(guid, out var has) && has;
                plan.Add(new GrantPlanEntry(guid, already));
            }
            return plan;
        }

        /// <summary>EOR key-shape precedent (L648-651), our prefix (§3.5): <c>"BLSS_ACTIVE_" + blessingId</c>.</summary>
        public static string LatchStatKey(string blessingId)
        {
            return "BLSS_ACTIVE_" + (blessingId ?? "");
        }
    }

    /// <summary>One party member's grant decision. <see cref="ShouldGrant"/> is the only field a caller
    /// needs to act on; <see cref="AlreadyHasTrait"/> is kept for logging/diagnostics.</summary>
    public sealed class GrantPlanEntry
    {
        public string EntityGuid { get; private set; }
        public bool AlreadyHasTrait { get; private set; }
        public bool ShouldGrant { get { return !AlreadyHasTrait; } }

        public GrantPlanEntry(string entityGuid, bool alreadyHasTrait)
        {
            EntityGuid = entityGuid;
            AlreadyHasTrait = alreadyHasTrait;
        }
    }
}
