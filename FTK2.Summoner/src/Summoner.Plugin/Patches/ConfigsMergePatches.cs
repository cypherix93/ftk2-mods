using System;

namespace Summoner.Plugin.Patches
{
    /// <summary>
    /// The two Harmony postfixes from design §A3.1. Both wrapped in try/catch that logs and
    /// defers to vanilla (fail-safe, CONVENTIONS.md). ConfigsHelper.LoadConfigs/ReloadConfigs both
    /// rebuild Configs from scratch every call (game-patch-surface-notes.md §7), so re-planning
    /// from the immutable in-memory pack model on every postfix invocation is idempotent by
    /// construction (design §A3.2) -- packs themselves are read from disk once in Awake() and
    /// never re-scanned here.
    ///
    /// MP review B7 fix: both postfixes now refuse to merge once <see cref="SummonerPlugin.Blocked"/>
    /// latches (see SummonerPlugin.OnParityMismatchRow for the full WarnOnly/WarnAndSafeMode/Block
    /// semantics and the "already-merged content stays merged" honesty note). ReloadConfigs is the
    /// realistic target -- the initial LoadConfigs merge at boot necessarily runs before any
    /// multiplayer handshake could have reported a mismatch -- but the check is added to LoadConfigs
    /// too, defensively, in case it is ever invoked again later in the same process.
    /// </summary>
    public static class ConfigsMergePatches
    {
        // Target: public static Configs LoadConfigs(string basePath)
        public static void LoadConfigs_Postfix(string basePath, ref Configs __result)
        {
            if (!SummonerPlugin.Enabled.Value) return;
            if (SummonerPlugin.Blocked)
            {
                SummonerPlugin.Log.LogWarning("[Summoner] LoadConfigs merge skipped -- parity Blocked latched earlier this session.");
                return;
            }
            try
            {
                SummonerPlugin.MergeInto(__result);
            }
            catch (Exception e)
            {
                SummonerPlugin.Log.LogWarning($"[Summoner] LoadConfigs merge failed: {e} -- vanilla Configs left untouched for this pass.");
            }
        }

        // Target: public static void ReloadConfigs(ref Configs configs, string basePath)
        public static void ReloadConfigs_Postfix(ref Configs configs, string basePath)
        {
            if (!SummonerPlugin.Enabled.Value) return;
            if (SummonerPlugin.Blocked)
            {
                SummonerPlugin.Log.LogWarning("[Summoner] ReloadConfigs merge skipped -- parity Blocked latched earlier this session " +
                                               "(no further FollowerPacks content merges; content already merged before the mismatch stays merged).");
                return;
            }
            try
            {
                SummonerPlugin.MergeInto(configs);
            }
            catch (Exception e)
            {
                SummonerPlugin.Log.LogWarning($"[Summoner] ReloadConfigs merge failed: {e} -- vanilla Configs left untouched for this pass.");
            }
        }
    }
}
