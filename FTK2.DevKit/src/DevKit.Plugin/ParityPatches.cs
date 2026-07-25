using System;

namespace DevKit.Plugin
{
    /// <summary>
    /// Harmony patch bodies for the ParityService transport. Both are strictly observational.
    ///
    /// <b>Why the receive hook is a void Prefix.</b> A void prefix cannot return false, so it cannot
    /// skip the original method — the compiler enforces the property we care about. EOR's own
    /// <c>_handleNetworkAction</c> prefix returns <c>false</c> for its custom payloads and therefore
    /// swallows messages, which is exactly the hazard called out in
    /// docs/research/eor-0760-content-audit.md §3.4 (an unmodded peer's forged action then routes
    /// into the vanilla handler — undefined behavior). DevKit reads the payload and always lets the
    /// game's own handler run.
    ///
    /// <b>Why <c>object[] __args</c>.</b> <c>AdventureDirector._handleNetworkAction</c>'s exact
    /// signature is an open question (SPEC §11.8) and the game refs weren't available at
    /// implementation time. Taking the raw argument array means the hook binds to ANY signature and
    /// simply scans for a string that looks like one of our payloads — a game update that reorders
    /// or renames parameters cannot break it.
    /// </summary>
    internal static class ParityPatches
    {
        internal static void HandleNetworkActionPrefix(object __instance, object[] __args)
        {
            try
            {
                if (__instance != null) ParityTransport.RememberDirector(__instance);
                if (__args == null) return;
                for (int i = 0; i < __args.Length; i++)
                {
                    string candidate = __args[i] as string;
                    if (string.IsNullOrEmpty(candidate)) continue;
                    ParityCoordinator.OnNetworkPayloadObserved(candidate);
                }
            }
            catch (Exception ex)
            {
                // Never rethrow from a patch body: an exception here would propagate into the game's
                // network handler. Log and let vanilla continue untouched (fail-safe rule).
                SafeLog("DevKit parity receive hook error (ignored): " + ex);
            }
        }

        internal static void AdventureDirectorInitializePostfix(object __instance)
        {
            try
            {
                if (__instance != null) ParityTransport.RememberDirector(__instance);
                ParityCoordinator.OnSessionStarted();
            }
            catch (Exception ex)
            {
                SafeLog("DevKit parity session-start hook error (ignored): " + ex);
            }
        }

        private static void SafeLog(string message)
        {
            try
            {
                if (DevKitPlugin.Log != null) DevKitPlugin.Log.LogWarning(message);
            }
            catch (Exception)
            {
            }
        }
    }
}
