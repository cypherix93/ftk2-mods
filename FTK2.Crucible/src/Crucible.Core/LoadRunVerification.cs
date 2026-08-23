using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Decides whether <c>crucible_load_run</c> actually loaded the run it was asked for. Pure
    /// string/bool logic over already-read values (<c>run.present</c> and
    /// <c>RouterHelper.Env.SelectedGameRunId</c>) -- no game reference, unit-testable without a
    /// running game. Never reports success on a bare "the call didn't throw"; a match requires both
    /// a present run AND its selected id equalling what was asked for.
    /// </summary>
    public static class LoadRunVerification
    {
        public static bool Confirm(string requestedRunId, bool runPresent, string selectedGameRunId, out string evidence)
        {
            bool matched = runPresent
                && !string.IsNullOrEmpty(selectedGameRunId)
                && string.Equals(selectedGameRunId, requestedRunId, StringComparison.Ordinal);

            evidence = "requestedRunId=" + requestedRunId
                + " run.present=" + runPresent
                + " RouterHelper.Env.SelectedGameRunId=" + (selectedGameRunId ?? "(null)");

            return matched;
        }
    }
}
