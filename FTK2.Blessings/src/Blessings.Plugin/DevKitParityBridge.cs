using System;
using System.Reflection;

namespace Blessings.Plugin
{
    /// <summary>
    /// The FTK2.DevKit <c>ParityService</c> handshake adapter (SPEC §3, §6, §9.5, §9.6) -- reflection
    /// only, NO compile-time dependency on DevKit (§3.3: "FTK2.Blessings has no compile-time reference
    /// to ClassForge"; the same posture applies to DevKit). Mirrors
    /// FTK2.ClassForge/src/ClassForge.Plugin/ParityBridge.cs member-for-member (the M2 task brief's
    /// binding resolution requires mirroring <see cref="HasVerifiedMatch"/> specifically for GATE E),
    /// folding in Summoner.Plugin's WarnOnly/WarnAndSafeMode/Block policy handling
    /// (Summoner.Plugin/SummonerPlugin.cs) since Blessings exposes the same three-value knob (§5).
    /// </summary>
    internal static class DevKitParityBridge
    {
        private const string ServiceTypeName = "FTK2Mods.DevKit.ParityService, ftk2mods.devkit";
        private const string ServiceTypeShortName = "FTK2Mods.DevKit.ParityService";
        private const string KindMatch = "Match";

        private static bool _loggedDevKitAbsent;

        /// <summary>
        /// True once a parity mismatch has been reported for Blessings THIS SESSION (§9.5's SafeMode
        /// latch: "Off in SafeMode... fail-closed on every peer that observes the mismatch"). Cleared at
        /// the same <c>AdventureDirector.Initialize</c> session-start boundary DevKit's own
        /// <c>ParityService.ResetSession()</c> uses (see <see cref="ResetForNewSession"/>), so a mismatch
        /// from a previous session in the same process cannot silently suppress a fresh, verified-Match
        /// session (mirrors ClassForge's ParityBridge.Blocked / AdventureDirectorInitialize_Postfix).
        /// </summary>
        internal static bool SafeMode { get { return _safeMode; } }
        private static bool _safeMode;

        /// <summary>WarnOnly never latches SafeMode (footgun, §5) -- it only logs. Consulted so the
        /// mismatch banner can say plainly whether anything actually changed.</summary>
        private static bool _policyDisablesOnMismatch = true;

        internal static void SetPolicyDisablesOnMismatch(bool disables)
        {
            _policyDisablesOnMismatch = disables;
        }

        internal static void ResetForNewSession()
        {
            if (!_safeMode) return;
            _safeMode = false;
            BlessingsPlugin.Log.LogInfo(
                "[Blessings] New session started (AdventureDirector.Initialize) -- clearing the previous " +
                "session's parity SafeMode latch. Re-armed: a fresh mismatch this session will SafeMode again.");
        }

        internal static void Register(string guid, string version, string dataHash, string[] enabledFeatures)
        {
            try
            {
                var service = ResolveServiceType();
                if (service == null)
                {
                    if (!_loggedDevKitAbsent)
                    {
                        _loggedDevKitAbsent = true;
                        BlessingsPlugin.Log.LogInfo(
                            "[Blessings] FTK2.DevKit not present -- skipping ParityService registration (no-op, fail-safe). " +
                            "Multiplayer parity is UNENFORCED without DevKit; install FTK2.DevKit for the R1 handshake.");
                    }
                    return;
                }

                var withCallback = service.GetMethod("RegisterWithCallback",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string[]), typeof(Action<string[]>) },
                    null);

                if (withCallback != null)
                {
                    var result = withCallback.Invoke(null, new object[] { guid, version, dataHash, enabledFeatures, new Action<string[]>(OnParityFailed) });
                    LogRegistered(dataHash, enabledFeatures, result, "RegisterWithCallback");
                    return;
                }

                var plain = service.GetMethod("Register",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string[]) }, null);

                if (plain == null)
                {
                    BlessingsPlugin.Log.LogWarning(
                        "[Blessings] " + ServiceTypeShortName + " found but exposes neither RegisterWithCallback(...) nor " +
                        "Register(...) -- skipping registration (fail-safe). DevKit version mismatch?");
                    return;
                }

                var plainResult = plain.Invoke(null, new object[] { guid, version, dataHash, enabledFeatures });
                LogRegistered(dataHash, enabledFeatures, plainResult, "Register");
                BlessingsPlugin.Log.LogWarning(
                    "[Blessings] This DevKit build has no RegisterWithCallback -- Blessings' [Multiplayer] " +
                    "OnParityMismatch policy CANNOT be enforced (no callback to latch SafeMode on). " +
                    "Verify data parity manually before playing online.");
            }
            catch (Exception ex)
            {
                BlessingsPlugin.Log.LogWarning("[Blessings] ParityService registration failed (non-fatal, fail-safe): " + ex);
            }
        }

        /// <summary>
        /// True only once DevKit's handshake has produced a <c>Match</c> verdict for Blessings' own guid
        /// in the CURRENT session (GATE E, §3.5/§6/binding resolution V7). Deliberately NOT "SafeMode is
        /// false" -- before the handshake round-trip completes there is no verdict at all yet, and the
        /// online grant gate must fail CLOSED in that gap (no verdict yet != a verified match), never
        /// fail open just because nothing has failed YET. Mirrors ClassForge's ParityBridge.HasVerifiedMatch
        /// exactly (member for member).
        /// </summary>
        internal static bool HasVerifiedMatch()
        {
            if (_safeMode) return false;

            try
            {
                var service = ResolveServiceType();
                if (service == null) return false; // no DevKit -> no handshake -> nothing verified.

                var method = service.GetMethod("GetLastVerdictRows",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method == null) return false;

                var rows = method.Invoke(null, null) as string[][];
                if (rows == null) return false;

                for (int i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    if (row == null || row.Length < 2) continue;
                    if (string.Equals(row[0], BlessingsPlugin.Guid, StringComparison.Ordinal) &&
                        string.Equals(row[1], KindMatch, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            catch
            {
                return false; // fail closed: an unreadable verdict set is not a verified match.
            }
        }

        /// <summary>DevKit's <c>ParityFailed</c> callback. Row layout fixed by position
        /// (<c>ParityVerdict.ToCallbackArgs</c>): <c>[guid, kind, local, remote, peer, message]</c>.</summary>
        private static void OnParityFailed(string[] row)
        {
            try
            {
                string kind = row != null && row.Length > 1 ? (row[1] ?? "") : "";
                string peer = row != null && row.Length > 4 ? (row[4] ?? "(unknown peer)") : "(unknown peer)";
                string local = row != null && row.Length > 2 ? (row[2] ?? "") : "";
                string remote = row != null && row.Length > 3 ? (row[3] ?? "") : "";
                string message = row != null && row.Length > 5 ? (row[5] ?? "") : "";

                if (string.Equals(kind, KindMatch, StringComparison.Ordinal)) return; // not a divergence.

                if (!_policyDisablesOnMismatch)
                {
                    BlessingsPlugin.Log.LogWarning(
                        "[Blessings] parity mismatch (kind=" + kind + ", peer=" + peer + ", local=" + local +
                        ", remote=" + remote + ", detail=" + message + ") -- OnParityMismatch=WarnOnly: logged only, " +
                        "resolution/grants continue unchanged. This is a footgun (§5): a one-sided grant is a " +
                        "guaranteed stat divergence on every subsequent GetStat-dependent decision.");
                    return;
                }

                bool first = !_safeMode;
                _safeMode = true;
                if (!first) return; // one banner per session.

                BlessingsPlugin.Log.LogError(
                    "==================================================================\n" +
                    "  Blessings PARITY MISMATCH -- SAFEMODE FOR THIS SESSION\n" +
                    "==================================================================\n" +
                    "  kind   : " + kind + "\n" +
                    "  peer   : " + peer + "\n" +
                    "  local  : " + local + "\n" +
                    "  remote : " + remote + "\n" +
                    "  detail : " + message + "\n" +
                    "------------------------------------------------------------------\n" +
                    "  [Multiplayer] OnParityMismatch = WarnAndSafeMode/Block (SPEC §9.5).\n" +
                    "  Blessing resolution and ALL GiveTrait grants are OFF for the rest of\n" +
                    "  this session, on every peer that observes the mismatch (fail-closed --\n" +
                    "  no peer grants, so no asymmetric Things). Traits already granted before\n" +
                    "  this mismatch keep working -- they are ClassForge-merged configs + native\n" +
                    "  Things, outside this plugin's runtime. FIX: make every peer's blessings.json\n" +
                    "  + [Blessings] Mode byte-identical, then start a new session.\n" +
                    "==================================================================");
            }
            catch (Exception ex)
            {
                _safeMode = true;
                BlessingsPlugin.Log.LogError("[Blessings] ParityFailed callback threw; failing CLOSED (SafeMode): " + ex);
            }
        }

        private static Type ResolveServiceType()
        {
            try
            {
                var direct = Type.GetType(ServiceTypeName, false);
                if (direct != null) return direct;
            }
            catch
            {
                // fall through to the scan
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type candidate;
                try { candidate = assemblies[i].GetType(ServiceTypeShortName, false); }
                catch { continue; }
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static void LogRegistered(string dataHash, string[] enabledFeatures, object result, string via)
        {
            bool ok = !(result is bool) || (bool)result;
            string features = enabledFeatures == null || enabledFeatures.Length == 0 ? "(none)" : string.Join(",", enabledFeatures);
            if (ok)
            {
                BlessingsPlugin.Log.LogInfo("[Blessings] Registered with FTK2.DevKit ParityService via " + via +
                    ": dataHash=" + dataHash + ", enabledFeatures=[" + features + "].");
            }
            else
            {
                BlessingsPlugin.Log.LogWarning("[Blessings] FTK2.DevKit ParityService." + via +
                    " returned false (rejected registration) -- parity is UNENFORCED for Blessings this session. dataHash=" + dataHash + ".");
            }
        }
    }
}
