using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Safety gate for <c>crucible_load_run</c>. The owner's live co-op saves sit in the same
    /// GameRuns folder as our test saves, sorted by date directly next to each other in the Load
    /// Game list -- there is no such thing as a safe implicit choice. This refuses any call that
    /// does not carry an explicit, non-empty run id, and never substitutes one of its own: not
    /// <c>UserData.LastGameRunIdPlayed</c>, not "the first" or "the newest" entry in
    /// <c>Env.GameRuns</c>. Pure string logic -- no game reference, unit-testable without a running
    /// game.
    /// </summary>
    public static class LoadRunGuard
    {
        /// <summary>Refuses null/empty/whitespace-only run ids. Never defaults.</summary>
        public static bool TryValidateRunId(string runId, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(runId))
            {
                error = "refused: crucible_load_run requires an explicit run id and will never fall back to "
                    + "LastGameRunIdPlayed or 'the newest' save -- pass one of the ids from Env.GameRuns.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// True when <paramref name="runId"/> is present (ordinal match -- run ids are GUIDs) in
        /// <paramref name="knownRunIds"/>. Loading an id Env.GameRuns doesn't even recognize should
        /// fail fast with a clear reason instead of being handed to the game's own load call.
        /// </summary>
        public static bool IsKnownRunId(string runId, string[] knownRunIds)
        {
            if (string.IsNullOrEmpty(runId) || knownRunIds == null) return false;
            for (int i = 0; i < knownRunIds.Length; i++)
            {
                if (string.Equals(knownRunIds[i], runId, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
