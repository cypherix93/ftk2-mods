using System;
using System.Reflection;
using ClassForge.Core;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The FTK2.DevKit <c>ParityService</c> handshake adapter (SPEC.md §3, §6, §9.5, §9.6; docs/MULTIPLAYER.md R1).
    ///
    /// <para><b>No compile-time dependency on DevKit</b>, by DevKit's own design (FTK2.DevKit/SPEC.md §3, §6):
    /// the whole surface is <c>public static</c>, BCL-typed, and deliberately overload-free so a sibling mod can
    /// resolve it by name. The canonical shape DevKit documents on <c>ParityService</c> is:</para>
    /// <code>
    /// var t = Type.GetType("FTK2Mods.DevKit.ParityService, ftk2mods.devkit");
    /// t.GetMethod("RegisterWithCallback").Invoke(null, new object[] {
    ///     guid, version, dataHash, features, new Action&lt;string[]&gt;(OnParityFailed) });
    /// </code>
    /// <para>Callback rows are the fixed positional layout DevKit pins on
    /// <c>ParityVerdict.ToCallbackArgs()</c>: <c>[0]=guid [1]=kind [2]=localValue [3]=remoteValue
    /// [4]=remotePeerId [5]=message</c>, where <c>kind</c> is a <c>ParityVerdictKind</c> name and
    /// <c>"Match"</c> is the only non-divergent value.</para>
    ///
    /// <para><b>ClassForge's policy is <c>Block</c></b> (SPEC.md §5, §9.5; SPEC-DELTA-v1.1 §5.3). ClassForge
    /// decides that for itself rather than inheriting DevKit's session policy: on any non-<c>Match</c> row,
    /// <see cref="Blocked"/> latches true for the process and <em>every</em> ClassForge feature switches off —
    /// the merge, the UI injection, the icon fallback, the trait loadout injection and the whole recipe
    /// engine. SPEC-DELTA-v1.1 §5.3 is explicit that there is no presentation-only subset to keep running:
    /// every recipe primitive either mutates combat state or feeds something that does, so a partial
    /// shutdown would produce exactly the asymmetric execution the determinism invariants exist to prevent.</para>
    ///
    /// <para>Fail-safe throughout: DevKit absent is a logged no-op, never a hard failure, and nothing here
    /// throws out of a Harmony patch body.</para>
    /// </summary>
    internal static class ParityBridge
    {
        /// <summary>DevKit's assembly-qualified service name. <c>ftk2mods.devkit</c> is DevKit.Core's
        /// <c>AssemblyName</c> (see FTK2.DevKit/src/DevKit.Core/DevKit.Core.csproj).</summary>
        private const string ServiceTypeName = "FTK2Mods.DevKit.ParityService, ftk2mods.devkit";

        private const string ServiceTypeShortName = "FTK2Mods.DevKit.ParityService";

        /// <summary>The one non-divergent <c>ParityVerdictKind</c> name.</summary>
        private const string KindMatch = "Match";

        private static bool _loggedDevKitAbsent;
        private static bool _blocked;

        /// <summary>
        /// True once a parity mismatch has been reported for ClassForge THIS SESSION. Latching within a
        /// session — a session that has diverged once is never trusted again without at least a new session
        /// — but NOT process-wide (MP review M1): see <see cref="AdventureDirectorInitialize_Postfix"/>,
        /// which clears this at the same session-start boundary FTK2.DevKit's own
        /// <c>ParityService.ResetSession()</c> uses, so a stale latch from a previous session in the same
        /// process cannot silently run one peer feature-off while a fresh, fully-verified handshake reports
        /// <c>Match</c> for everyone.
        /// Read through <see cref="ClassForgePlugin.FeaturesActive"/>, which every patch body consults.
        /// </summary>
        internal static bool Blocked { get { return _blocked; } }

        /// <summary>
        /// Registers (or re-registers, after a hot-reload re-merge) ClassForge's parity tuple.
        /// Prefers <c>RegisterWithCallback</c> so the Block policy can actually fire; falls back to the
        /// callback-less <c>Register</c> only if an older DevKit lacks it (and says so loudly, because
        /// that combination silently downgrades Block to "no enforcement").
        /// </summary>
        internal static void Register(ParityRegistration payload)
        {
            try
            {
                var service = ResolveServiceType();
                if (service == null)
                {
                    if (!_loggedDevKitAbsent)
                    {
                        _loggedDevKitAbsent = true;
                        ClassForgePlugin.Log.LogInfo(
                            "[ClassForge] FTK2.DevKit not present — skipping ParityService registration (no-op, fail-safe). " +
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
                    var result = withCallback.Invoke(null, new object[]
                    {
                        payload.Guid, payload.Version, payload.DataHash, payload.EnabledFeatures,
                        new Action<string[]>(OnParityFailed)
                    });
                    LogRegistered(payload, result, "RegisterWithCallback");
                    return;
                }

                var plain = service.GetMethod("Register",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string[]) }, null);

                if (plain == null)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] " + ServiceTypeShortName + " found but exposes neither " +
                        "RegisterWithCallback(string,string,string,string[],Action<string[]>) nor " +
                        "Register(string,string,string,string[]) — skipping registration (fail-safe). " +
                        "DevKit version mismatch?");
                    return;
                }

                var plainResult = plain.Invoke(null, new object[]
                {
                    payload.Guid, payload.Version, payload.DataHash, payload.EnabledFeatures
                });
                LogRegistered(payload, plainResult, "Register");
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] This DevKit build has no RegisterWithCallback — ClassForge's " +
                    "[Multiplayer] OnParityMismatch=Block policy cannot be enforced (no ParityFailed callback " +
                    "to latch on). Features stay ON; verify data parity manually before playing online.");
            }
            catch (Exception ex)
            {
                // Never fatal: a broken/absent DevKit must not take ClassForge (or the game) down.
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] ParityService registration failed (non-fatal, fail-safe): " + ex);
            }
        }

        /// <summary>
        /// Postfix for <c>AdventureDirector.Initialize</c> — the SAME session-start anchor FTK2.DevKit's own
        /// <c>ParityCoordinator</c> uses to call <c>ParityService.ResetSession()</c> (MP review M1;
        /// <c>ParityCoordinator.OnSessionStarted</c> resets <c>_sessionBlocked</c>, <c>SafeModeGuids</c> and
        /// <c>RemoteSnapshots</c> on every call). Without this, <see cref="_blocked"/> would be process-wide:
        /// a peer that blocked in session 1 (stale pack) and then fixed it for session 2 would still run
        /// features-off in session 2 even though DevKit now reports <c>Match</c> for everyone — a stale
        /// asymmetry blessed by a green verdict, which is worse than the original mismatch.
        /// <para>Re-derived, not merely cleared: if the new session's handshake diverges again,
        /// <see cref="OnParityFailed"/> re-latches it exactly as before.</para>
        /// </summary>
        internal static void AdventureDirectorInitialize_Postfix()
        {
            try
            {
                if (!_blocked) return;
                _blocked = false;
                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] New session started (AdventureDirector.Initialize) — clearing the previous " +
                    "session's parity Block latch. Re-armed: a fresh mismatch this session will block again.");
            }
            catch (Exception ex)
            {
                // Never let a session-boundary hook take the game down; worst case the stale latch survives
                // one more session, which is the pre-M1 behavior, not a regression.
                ClassForgePlugin.Log.LogWarning("[ClassForge] Session-start parity reset failed (non-fatal): " + ex);
            }
        }

        /// <summary>
        /// True only once DevKit's handshake has produced a <c>Match</c> verdict for ClassForge's own guid in
        /// the CURRENT session (MP review B3). Deliberately NOT the same thing as "<see cref="Blocked"/> is
        /// false" — before the handshake round-trip completes there is no verdict at all yet, and B3 requires
        /// online-multiplayer trait injection to fail CLOSED in that gap (no verdict yet is not the same as a
        /// verified match), not fail open just because nothing has failed YET.
        /// </summary>
        internal static bool HasVerifiedMatch()
        {
            if (_blocked) return false;

            try
            {
                var service = ResolveServiceType();
                if (service == null) return false; // no DevKit present -> no handshake -> nothing verified.

                var method = service.GetMethod("GetLastVerdictRows",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method == null) return false;

                var rows = method.Invoke(null, null) as string[][];
                if (rows == null) return false;

                for (int i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    if (row == null || row.Length < 2) continue;
                    if (string.Equals(row[0], ClassForgePlugin.Guid, StringComparison.Ordinal) &&
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

        /// <summary>
        /// DevKit's <c>ParityFailed</c> callback. Row layout is fixed by position
        /// (<c>ParityVerdict.ToCallbackArgs</c>): <c>[guid, kind, local, remote, peer, message]</c>.
        /// DevKit only dispatches this for verdicts where <c>IsMismatch</c> is already true, but the
        /// kind is re-checked here so the Block decision is ClassForge's own and survives any change
        /// in DevKit's dispatch filtering.
        /// </summary>
        private static void OnParityFailed(string[] row)
        {
            try
            {
                string kind = row != null && row.Length > 1 ? (row[1] ?? "") : "";
                string peer = row != null && row.Length > 4 ? (row[4] ?? "(unknown peer)") : "(unknown peer)";
                string local = row != null && row.Length > 2 ? (row[2] ?? "") : "";
                string remote = row != null && row.Length > 3 ? (row[3] ?? "") : "";
                string message = row != null && row.Length > 5 ? (row[5] ?? "") : "";

                if (string.Equals(kind, KindMatch, StringComparison.Ordinal))
                    return; // not a divergence — nothing to do.

                bool first = !_blocked;
                _blocked = true;

                if (!first) return; // one banner per session; the flag is already latched.

                ClassForgePlugin.Log.LogError(
                    "==================================================================\n" +
                    "  ClassForge PARITY MISMATCH — RUNTIME FEATURES DISABLED FOR THIS SESSION\n" +
                    "==================================================================\n" +
                    "  kind   : " + kind + "\n" +
                    "  peer   : " + peer + "\n" +
                    "  local  : " + local + "\n" +
                    "  remote : " + remote + "\n" +
                    "  detail : " + message + "\n" +
                    "------------------------------------------------------------------\n" +
                    "  [Multiplayer] OnParityMismatch = Block (SPEC.md §9.5).\n" +
                    "  Honest scope of what 'Block' does (MP review M2): the class-select\n" +
                    "  injection, icon/portrait fallback, trait loadout injection and the\n" +
                    "  ENTIRE skill-recipe engine are off for the rest of THIS SESSION\n" +
                    "  (see AdventureDirectorInitialize_Postfix -- this clears at the start\n" +
                    "  of the NEXT session). There is no partial/presentation-only runtime\n" +
                    "  mode by design (SPEC-DELTA-v1.1 §5.3) -- a half-running recipe engine\n" +
                    "  is exactly the asymmetric execution the determinism invariants exist\n" +
                    "  to prevent. This does NOT unmerge or block the content merge itself:\n" +
                    "  merged Configs entries (classes/traits/items/abilities/localization/\n" +
                    "  icons/portraits) are inert DATA and stay merged, exactly like every\n" +
                    "  other config divergence between peers -- nothing above exercises that\n" +
                    "  data while runtime features are off. FIX: make every peer's\n" +
                    "  ClassPacks/ folder byte-identical (and matching [Skills]/[Traits]\n" +
                    "  knobs), then start a new session.\n" +
                    "==================================================================");
            }
            catch (Exception ex)
            {
                // A throw here would be swallowed and isolated by DevKit anyway, but fail closed:
                // if we cannot even parse the row, assume the worst and block.
                _blocked = true;
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] ParityFailed callback threw; failing CLOSED (all features disabled): " + ex);
            }
        }

        /// <summary>
        /// Assembly-qualified lookup first (DevKit's own documented call shape), then a scan by full
        /// name — BepInEx load order means DevKit's assembly may not yet be resolvable by name at the
        /// moment ClassForge first registers, but it is always already <em>loaded</em> into the AppDomain
        /// by the time a <c>ConfigsHelper.LoadConfigs</c> postfix runs.
        /// </summary>
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

        private static void LogRegistered(ParityRegistration payload, object result, string via)
        {
            bool ok = !(result is bool) || (bool)result;
            string features = payload.EnabledFeatures == null || payload.EnabledFeatures.Length == 0
                ? "(none)"
                : string.Join(",", payload.EnabledFeatures);

            if (ok)
            {
                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] Registered with FTK2.DevKit ParityService via " + via +
                    ": dataHash=" + payload.DataHash + ", enabledFeatures=[" + features + "].");
            }
            else
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] FTK2.DevKit ParityService." + via + " returned false (rejected registration) " +
                    "— parity is UNENFORCED for ClassForge this session. dataHash=" + payload.DataHash + ".");
            }
        }
    }
}
