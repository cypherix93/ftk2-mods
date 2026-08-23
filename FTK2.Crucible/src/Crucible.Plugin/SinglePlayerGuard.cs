using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Forces the game into single-player so automated test runs never land in the multiplayer
    /// lobby. Gated by <c>[Safety] ForceSinglePlayer</c> (default true — Crucible is dev tooling).
    ///
    /// Two layers, per docs/research/crucible-traversal-inventory.md §"Recommended order for the
    /// harness":
    ///   1. A Harmony prefix on <c>MainMenuDirector.TryAutoJoinRoom()</c> that skips the original and
    ///      returns false, stopping the auto-join at its single confirmed entry point.
    ///   2. Belt-and-braces: clear <c>Env.RoomAutoJoinId</c> to "" on the first tick <c>Env</c> is
    ///      reachable, in case something reads that field before <c>TryAutoJoinRoom</c> runs.
    /// Resolved entirely by name via AccessTools — no compile-time game reference. If the target
    /// can't be found, this logs a warning and leaves the game alone; it never throws or blocks boot.
    /// </summary>
    internal static class SinglePlayerGuard
    {
        private static ManualLogSource _log;
        private static bool _loggedSuppression;
        private static bool _clearedAutoJoinId;

        internal static void Initialize(Harmony harmony, ManualLogSource log)
        {
            _log = log;

            Type mainMenuDirector = AccessTools.TypeByName("MainMenuDirector");
            MethodInfo target = mainMenuDirector == null
                ? null
                : AccessTools.Method(mainMenuDirector, "TryAutoJoinRoom", Type.EmptyTypes);

            if (target == null)
            {
                log.LogWarning("Target NOT found: MainMenuDirector.TryAutoJoinRoom — "
                    + "ForceSinglePlayer's Harmony guard is disabled (belt-and-braces Env.RoomAutoJoinId clear still runs).");
                DevKitBridge.ReportTarget("MainMenuDirector.TryAutoJoinRoom", null);
                return;
            }

            log.LogInfo("Target found: MainMenuDirector.TryAutoJoinRoom");
            DevKitBridge.ReportTarget("MainMenuDirector.TryAutoJoinRoom", target);

            try
            {
                harmony.Patch(target, new HarmonyMethod(AccessTools.Method(typeof(SinglePlayerGuard), "Prefix")));
            }
            catch (Exception ex)
            {
                log.LogWarning("Failed to patch MainMenuDirector.TryAutoJoinRoom: " + ex.Message);
            }
        }

        /// <summary>Harmony prefix. Returning false skips the original and uses <c>__result</c> as-is.</summary>
        private static bool Prefix(ref bool __result)
        {
            if (CruciblePlugin.Instance == null || !CruciblePlugin.Instance.CfgForceSinglePlayer.Value)
                return true; // run the original.

            __result = false;

            if (!_loggedSuppression)
            {
                _loggedSuppression = true;
                if (_log != null)
                {
                    _log.LogInfo("ForceSinglePlayer: suppressed MainMenuDirector.TryAutoJoinRoom "
                        + "(would have auto-joined a multiplayer room).");
                }
            }

            return false; // skip the original.
        }

        /// <summary>
        /// Called from MainThreadPump.OnTick every frame. Once <c>Env</c> is reachable, clears
        /// <c>Env.RoomAutoJoinId</c> to "" a single time. No-ops once ForceSinglePlayer is off, and
        /// re-arms if it's turned back on so a config toggle mid-session still takes effect.
        /// </summary>
        internal static void Tick()
        {
            if (CruciblePlugin.Instance == null || !CruciblePlugin.Instance.CfgForceSinglePlayer.Value)
            {
                _clearedAutoJoinId = false; // re-arm for next time ForceSinglePlayer is enabled.
                return;
            }

            if (_clearedAutoJoinId) return;

            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) return; // not reachable yet; try again next tick.

                FieldInfo field = AccessTools.Field(env.GetType(), "RoomAutoJoinId");
                if (field == null)
                {
                    _clearedAutoJoinId = true; // give up quietly; the Harmony prefix is the primary guard.
                    return;
                }

                field.SetValue(env, string.Empty);
                _clearedAutoJoinId = true;
                if (_log != null) _log.LogInfo("ForceSinglePlayer: cleared Env.RoomAutoJoinId.");
            }
            catch (Exception ex)
            {
                _clearedAutoJoinId = true; // avoid retrying every frame on a persistent failure.
                if (_log != null) _log.LogWarning("ForceSinglePlayer: failed to clear Env.RoomAutoJoinId: " + ex.Message);
            }
        }
    }
}
