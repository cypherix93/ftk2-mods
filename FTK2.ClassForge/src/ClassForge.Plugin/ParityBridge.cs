using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using ClassForge.Core;

namespace ClassForge.Plugin
{
    /// <summary>docs/MULTIPLAYER.md R1's three <c>OnParityMismatch</c> policies.</summary>
    internal enum MismatchPolicy
    {
        /// <summary>Log/notify only; every feature keeps running. Never the default for any kind.</summary>
        WarnOnly = 0,

        /// <summary>State-mutating features off for the session, presentation features keep running
        /// (<see cref="ClassForgePlugin.PresentationActive"/>).</summary>
        WarnAndSafeMode = 1,

        /// <summary>Every ClassForge runtime feature off for the session.</summary>
        Block = 2,
    }

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
    /// <para><b>ClassForge's policy is per-kind (P0.5 §4; see <see cref="ResolvePolicy"/>).</b>
    /// <c>Block</c> — <see cref="Blocked"/> latches and <em>every</em> ClassForge feature switches off — is
    /// the default for a <c>FeaturesMismatch</c>, because post-P0.5 that kind means a gameplay KNOB
    /// differs, and a knob like <c>[Combat] VenueGridPreset</c> changes the arena tile count and therefore
    /// the shared <c>GameRandom</c> draw count on the first AI turn: not "desync likely", a desync.
    /// <c>VersionMismatch</c>/<c>DataMismatch</c> keep docs/MULTIPLAYER.md R1's <c>WarnAndSafeMode</c>,
    /// because a pack-content difference may be benign and SafeMode already stops everything that would act
    /// on it. Under <c>Block</c> there is still no presentation-only subset (SPEC-DELTA-v1.1 §5.3); SafeMode
    /// is the weaker latch that keeps <see cref="ClassForgePlugin.PresentationActive"/> true.</para>
    ///
    /// <para><b>Fail CLOSED, not fail silent (P0.5 §5).</b> A missing FTK2.DevKit used to be one
    /// <c>LogInfo</c> line and a full-speed-ahead return, which made every decision above optional in
    /// practice. It is now a warning, an in-game banner, and — when the session is online multiplayer and
    /// <c>[Multiplayer] RequireParityService</c> is true — SafeMode. Same for the two shapes of DevKit that
    /// register successfully but cannot enforce (see <see cref="FailClosedUnenforceable"/>). Nothing here
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
        private static bool _safeMode;
        private static bool _verdictCallbackSubscribed;
        private static bool _loggedNoVerdictCallback;

        /// <summary>
        /// True when ClassForge is running with its state-mutating features off but presentation features
        /// still on (docs/MULTIPLAYER.md R1's SafeMode). Reached two ways: a
        /// <see cref="MismatchPolicy.WarnAndSafeMode"/> verdict, or P0.5 §5(b) — an online session with no
        /// FTK2.DevKit present and <c>[Multiplayer] RequireParityService = true</c>. Cleared at the same
        /// session boundary as <see cref="Blocked"/>, then immediately re-derived.
        /// </summary>
        internal static bool SafeMode { get { return _safeMode; } }

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
                    HandleDevKitAbsent();
                    return;
                }

                TrySubscribeVerdictCallback(service);

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
                    FailClosedUnenforceable(
                        ServiceTypeShortName + " was found but exposes neither " +
                        "RegisterWithCallback(string,string,string,string[],Action<string[]>) nor " +
                        "Register(string,string,string,string[]) — ClassForge never registered at all, so " +
                        "the handshake has no ClassForge entry to compare. DevKit version mismatch?");
                    return;
                }

                var plainResult = plain.Invoke(null, new object[]
                {
                    payload.Guid, payload.Version, payload.DataHash, payload.EnabledFeatures
                });
                LogRegistered(payload, plainResult, "Register");

                // P0.5 §5(c). This combination registers SUCCESSFULLY and then cannot enforce anything:
                // there is no ParityFailed callback to latch Block on, so a mismatched peer would be
                // detected by DevKit and ClassForge would keep running every feature anyway. Before P0.5
                // that was a single warning line. Now that Block is the DEFAULT for a knob divergence, a
                // registration that structurally cannot Block is a hard failure, not a note.
                FailClosedUnenforceable(
                    "FTK2.DevKit is present but this build exposes only Register(...), not " +
                    "RegisterWithCallback(...). ClassForge would be told nothing when a peer diverges, so " +
                    "[Multiplayer] OnParityMismatch could never fire — the handshake would be decorative. " +
                    "Update FTK2.DevKit.");
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
                if (_blocked || _safeMode)
                {
                    _blocked = false;
                    _safeMode = false;
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] New session started (AdventureDirector.Initialize) — clearing the previous " +
                        "session's parity Block/SafeMode latches. Re-armed: a fresh mismatch this session will " +
                        "latch again.");
                }

                // P0.5 §5(b): re-derive the DevKit-absent decision HERE, not at Awake. Registration runs from
                // the ConfigsHelper.LoadConfigs postfix, which can fire at the main menu where
                // PlayingOnlineMultiplayer is still false — so the "DevKit missing + online" combination only
                // becomes knowable at the session-start boundary. This is the one place that sees both.
                if (ResolveServiceType() == null) HandleDevKitAbsent();
            }
            catch (Exception ex)
            {
                // Never let a session-boundary hook take the game down; worst case the stale latch survives
                // one more session, which is the pre-M1 behavior, not a regression.
                ClassForgePlugin.Log.LogWarning("[ClassForge] Session-start parity reset failed (non-fatal): " + ex);
            }
        }

        /// <summary>
        /// P0.5 §5(a)+(b) — the hole that made every parity decision optional in practice. Before this,
        /// a missing FTK2.DevKit logged ONE <c>LogInfo</c> line ("parity is UNENFORCED without DevKit") and
        /// returned, leaving ClassForge fully enabled: a mismatched peer joined an online session and
        /// desynced with nothing to stop it and nothing visible to the player.
        ///
        /// <para>(a) The log line is a WARNING, and is mirrored to an in-game banner when the session is
        /// online multiplayer — a line in the BepInEx console is not a failure UX.
        /// (b) With <c>[Multiplayer] RequireParityService = true</c> (default) an ONLINE session fails
        /// CLOSED into SafeMode: presentation features only. Offline is unaffected, because with no peers
        /// there is nothing parity could protect.</para>
        /// </summary>
        private static void HandleDevKitAbsent()
        {
            FailClosedUnenforceable(
                "FTK2.DevKit is not installed, so the R1 parity handshake never runs at all: a peer with " +
                "different packs, a different ClassForge version or a different gameplay knob cannot be " +
                "detected, let alone refused. Install FTK2.DevKit.");
        }

        /// <summary>
        /// The shared "parity cannot be enforced this session" path: DevKit absent (<see cref="HandleDevKitAbsent"/>)
        /// or present-but-callback-less (§5(c)). Online + <c>RequireParityService</c> ⇒ SafeMode. Offline, or
        /// with the knob deliberately turned off, ⇒ a warning and nothing else.
        /// </summary>
        private static void FailClosedUnenforceable(string reason)
        {
            bool online = NetworkSessionState.IsOnlineMultiplayer();
            bool require = ClassForgePlugin.RequireParityService == null
                           || ClassForgePlugin.RequireParityService.Value; // null ⇒ fail closed.

            if (online && require)
            {
                EnterSafeMode("PARITY UNENFORCEABLE — " + reason);
                return;
            }

            if (_loggedDevKitAbsent) return;
            _loggedDevKitAbsent = true;

            string text = "[ClassForge] Multiplayer parity is UNENFORCED this session. " + reason;
            ClassForgePlugin.Log.LogWarning(
                text + (online
                    ? " [Multiplayer] RequireParityService is FALSE, so ClassForge is running fully enabled " +
                      "anyway — this is an explicitly unenforced online session."
                    : " This session is not online multiplayer, so nothing is at risk right now."));
            if (online) ShowBanner("ClassForge: multiplayer parity is UNENFORCED (no FTK2.DevKit)");
        }

        /// <summary>
        /// Latches SafeMode: <see cref="ClassForgePlugin.FeaturesActive"/> goes false (recipe engine, trait
        /// injection, stat modifiers, every Trainer feature) while
        /// <see cref="ClassForgePlugin.PresentationActive"/> stays true (icon fallback, class-select list).
        /// Idempotent — one banner per session.
        /// </summary>
        private static void EnterSafeMode(string reason)
        {
            if (_safeMode || _blocked) return;
            _safeMode = true;

            ClassForgePlugin.Log.LogWarning(
                "==================================================================\n" +
                "  ClassForge SAFE MODE — state-mutating features OFF this session\n" +
                "==================================================================\n" +
                "  " + reason + "\n" +
                "------------------------------------------------------------------\n" +
                "  ON  : icon/portrait fallback, the character-creation class list.\n" +
                "  OFF : the skill-recipe engine, trait loadout injection, conditional\n" +
                "        stat modifiers and every Trainer partner feature.\n" +
                "  The content merge itself is untouched — merged Configs entries are\n" +
                "  inert DATA and nothing above exercises them while features are off.\n" +
                "==================================================================");
            ShowBanner("ClassForge: SAFE MODE (multiplayer parity could not be verified)");
            AnnounceUnilateralDegrade("SAFE MODE", reason);
        }

        /// <summary>
        /// The correction to "graceful degradation", which in deterministic lockstep is not graceful.
        ///
        /// <para><b>The hazard.</b> Every ClassForge latch — <see cref="EnterSafeMode"/>,
        /// <see cref="_blocked"/>, <c>LootGrantPatches</c>'s loot SafeMode,
        /// <see cref="AiDrawNeutrality.HardDisable"/> — is decided from LOCAL evidence and takes effect
        /// on THIS peer only. There is no broadcast: ClassForge has no channel of its own to force a
        /// latch onto a peer (the DevKit handshake reports verdicts, it does not carry commands), and
        /// inventing one is out of scope here. So the reachable case is real and asymmetric: one peer
        /// alone lacks FTK2.DevKit, enters SafeMode, stops firing recipes AND stops taking
        /// <see cref="AiDrawNeutrality"/>'s compensating draw, while its partner keeps doing both. In
        /// lockstep that is not "reduced features on one machine", it is a permanent shared-stream
        /// offset from the first AI turn, and — because <c>CombatState</c> is <c>[JsonIgnore]</c> — the
        /// vendor's own desync MD5 cannot see it.</para>
        ///
        /// <para><b>So the honest thing is to say so.</b> Offline, a latch IS graceful and this says
        /// nothing. ONLINE, it is an error-level report plus a banner telling the player the session is
        /// now expected to diverge and to leave and fix it, rather than a reassuring "features off"
        /// notice that reads like the mod handled it. Refusing harder than this — force-quitting the
        /// session — is not ClassForge's call to make and would be a worse failure than the one it
        /// prevents.</para>
        /// </summary>
        internal static void AnnounceUnilateralDegrade(string what, string reason)
        {
            try
            {
                if (!NetworkSessionState.IsOnlineMultiplayer()) return;

                ClassForgePlugin.Log.LogError(
                    "==================================================================\n" +
                    "  ClassForge DEGRADED ON THIS PEER ONLY — LEAVE THIS ONLINE SESSION\n" +
                    "==================================================================\n" +
                    "  latch  : " + what + "\n" +
                    "  reason : " + reason + "\n" +
                    "------------------------------------------------------------------\n" +
                    "  This decision was made from LOCAL evidence and applies to THIS peer\n" +
                    "  only; ClassForge cannot broadcast it. Your partners are still running\n" +
                    "  the features just switched off here.\n" +
                    "  FTK2 co-op is deterministic lockstep: what breaks a session is not two\n" +
                    "  peers rolling different VALUES, it is two peers taking a different\n" +
                    "  NUMBER of draws from the shared GameRandom. A one-sided feature switch\n" +
                    "  is exactly that, from the first AI turn onward. Worse, CombatState is\n" +
                    "  [JsonIgnore] on GameRunData, so combat-side divergence is NOT in the\n" +
                    "  game's own desync MD5 -- nothing will tell you it happened.\n" +
                    "  This is NOT graceful degradation. Leave the session, fix the cause\n" +
                    "  named above on every machine, and start a new one.\n" +
                    "==================================================================");
                ShowBanner("ClassForge is degraded on YOUR machine only — leave this online session (" + what + ")");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning("[ClassForge] degrade announcement failed (non-fatal): " + ex.Message);
            }
        }

        /// <summary>
        /// Mirrors a parity decision into the game itself. The BepInEx console is not where a player finds
        /// out their session is about to desync. Best-effort and fully swallowed: the UI may not exist yet
        /// (registration can run before any view is up), and a missing banner must never change the
        /// enforcement decision that was already latched by the caller.
        /// </summary>
        private static void ShowBanner(string text)
        {
            try { GameplayDialogViewHelper.ShowEventTitle(text, 8000); }
            catch { /* presentation only — the latch above is the actual enforcement. */ }
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
            if (_blocked || _safeMode) return false;

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
        /// Subscribes to DevKit's verdict-arrival surface (task #11): <c>RegisterVerdictCallback(string,
        /// Action&lt;string[]&gt;)</c>, args <c>[remotePeerId, "Match"|"Mismatch"]</c>, fired on verdict
        /// TRANSITIONS only (DevKit m13 dedupe). Deliberately a separate method on DevKit's side — never an
        /// overload — so the documented typeless <c>GetMethod("RegisterWithCallback")</c> recipe other mods
        /// use cannot become ambiguous. An older DevKit without it degrades exactly like the
        /// <c>RegisterWithCallback</c> fallback pattern above: logged once, feature off (MP traits then
        /// require leaving and re-entering party management after the handshake), nothing throws.
        /// Idempotent per merge — DevKit stores one callback per guid, so re-registration just replaces it.
        /// </summary>
        private static void TrySubscribeVerdictCallback(Type service)
        {
            try
            {
                var method = service.GetMethod("RegisterVerdictCallback",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(Action<string[]>) }, null);

                if (method == null)
                {
                    if (!_loggedNoVerdictCallback)
                    {
                        _loggedNoVerdictCallback = true;
                        ClassForgePlugin.Log.LogInfo(
                            "[ClassForge] This DevKit build has no RegisterVerdictCallback — the MP trait-loadout " +
                            "refresh is off (traits appear after leaving and re-entering party management once the " +
                            "handshake completes). Update FTK2.DevKit for day-one MP traits.");
                    }
                    return;
                }

                method.Invoke(null, new object[] { ClassForgePlugin.Guid, new Action<string[]>(OnVerdict) });
                if (!_verdictCallbackSubscribed)
                {
                    _verdictCallbackSubscribed = true;
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] Subscribed to DevKit verdict-arrival callbacks (MP trait-loadout refresh armed).");
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] Verdict-callback subscription failed (non-fatal; MP trait refresh off): " + ex);
            }
        }

        /// <summary>
        /// DevKit's verdict-arrival callback: <c>[remotePeerId, "Match"|"Mismatch"]</c>. Runs on the Unity
        /// main thread (dispatched synchronously from the network-action receive path — see
        /// <see cref="TraitLoadoutRefresh"/>'s threading note). Mismatch needs no action here:
        /// <see cref="OnParityFailed"/> has already latched Block or SafeMode by then, and neither state
        /// injects anything (trait injection reads <see cref="ClassForgePlugin.FeaturesActive"/>, false in
        /// both).
        /// </summary>
        private static void OnVerdict(string[] args)
        {
            try
            {
                if (args == null || args.Length < 2) return;
                if (!string.Equals(args[1], KindMatch, StringComparison.Ordinal)) return;
                TraitLoadoutRefresh.OnVerifiedMatch();
            }
            catch (Exception ex)
            {
                // DevKit isolates a throwing subscriber anyway, but never rely on that from a callback.
                ClassForgePlugin.Log.LogWarning("[ClassForge] Verdict callback handling failed (non-fatal): " + ex);
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

                var policy = ResolvePolicy(kind);
                string knobReport = DescribeKnobDivergence(kind, local, remote);

                if (policy == MismatchPolicy.WarnAndSafeMode)
                {
                    EnterSafeMode(
                        "PARITY MISMATCH (" + kind + ") against peer '" + peer + "'.\n" +
                        knobReport + "\n  detail : " + message);
                    return;
                }

                if (policy == MismatchPolicy.WarnOnly)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] PARITY MISMATCH (" + kind + ") against peer '" + peer + "' — " +
                        "[Multiplayer] OnParityMismatch=WarnOnly, so NOTHING is being disabled and this " +
                        "session is expected to desync.\n" + knobReport + "\n  detail : " + message);
                    ShowBanner("ClassForge: parity mismatch (" + kind + ") — desync likely");
                    return;
                }

                bool first = !_blocked;
                _blocked = true;
                _safeMode = false; // Block supersedes SafeMode; nothing runs, so the weaker latch is moot.

                if (!first) return; // one banner per session; the flag is already latched.

                ClassForgePlugin.Log.LogError(
                    "==================================================================\n" +
                    "  ClassForge PARITY MISMATCH — JOIN REFUSED, FEATURES OFF THIS SESSION\n" +
                    "==================================================================\n" +
                    "  kind   : " + kind + "\n" +
                    "  peer   : " + peer + "\n" +
                    knobReport + "\n" +
                    "  detail : " + message + "\n" +
                    "------------------------------------------------------------------\n" +
                    "  Policy: BLOCK. This is the DEFAULT for a gameplay-knob divergence\n" +
                    "  because such a divergence is not 'desync likely', it is a desync:\n" +
                    "  e.g. [Combat] VenueGridPreset substitutes the combat arena map, so\n" +
                    "  the two peers have a different tile count, so AIHelper's ShuffleList\n" +
                    "  takes a different number of draws from the SHARED GameRandom stream\n" +
                    "  on the very first AI turn. ([Multiplayer] OnParityMismatch=WarnOnly\n" +
                    "  or WarnAndSafeMode override this, at your own risk.)\n" +
                    "  Scope (MP review M2): the class-select injection, icon/portrait\n" +
                    "  fallback, trait loadout injection and the ENTIRE skill-recipe engine\n" +
                    "  are off for the rest of THIS SESSION (see\n" +
                    "  AdventureDirectorInitialize_Postfix -- this clears at the start of\n" +
                    "  the NEXT session). There is no partial/presentation-only runtime mode\n" +
                    "  under Block by design (SPEC-DELTA-v1.1 §5.3). This does NOT unmerge\n" +
                    "  the content merge itself: merged Configs entries are inert DATA and\n" +
                    "  stay merged -- nothing above exercises that data while features are\n" +
                    "  off. FIX: change the knob(s) named above so both peers agree (or make\n" +
                    "  every peer's ClassPacks/ folder byte-identical for a data mismatch),\n" +
                    "  then start a new session.\n" +
                    "==================================================================");
                ShowBanner("ClassForge BLOCKED this session — " + FirstKnobLine(kind, local, remote));
            }
            catch (Exception ex)
            {
                // A throw here would be swallowed and isolated by DevKit anyway, but fail closed:
                // if we cannot even parse the row, assume the worst and block.
                _blocked = true;
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] ParityFailed callback threw; failing CLOSED (all features disabled): " + ex);
                // Unlike the policy Block above -- which DevKit hands to both sides of the mismatch, so
                // both latch together -- this one is a local parse failure. The other peer has no reason
                // to latch anything, so this latch is one-sided by construction.
                AnnounceUnilateralDegrade("BLOCK (ParityFailed row unreadable)", ex.Message);
            }
        }

        /// <summary>
        /// docs/MULTIPLAYER.md R1's <c>OnParityMismatch</c>, resolved per verdict kind.
        ///
        /// <para><c>"Default"</c> (the shipped value) is deliberately NOT one policy for everything:</para>
        /// <list type="bullet">
        /// <item><b>FeaturesMismatch ⇒ Block.</b> A gameplay KNOB differs. Post-P0.5 the payload carries
        /// every Gameplay-classified knob, so this kind now means something specific and always fatal —
        /// <c>[Combat] VenueGridPreset</c> alone changes the arena tile count and therefore the shared
        /// <c>GameRandom</c> draw count on the first AI turn. "Multiplayer desync likely" is EOR's failure
        /// mode; a guaranteed desync deserves a refusal.</item>
        /// <item><b>MissingLocal / MissingRemote ⇒ Block.</b> One peer has the merged content and the other
        /// has none of it. Not a difference of degree.</item>
        /// <item><b>VersionMismatch / DataMismatch ⇒ WarnAndSafeMode.</b> A pack-content or build
        /// difference MAY be benign (a localization-only pack edit, a version bump with no data change),
        /// and SafeMode already stops everything that would act on the difference.</item>
        /// </list>
        /// An explicit <c>Block</c>/<c>WarnAndSafeMode</c>/<c>WarnOnly</c> forces one policy for all kinds.
        /// An unrecognised value falls back to Default rather than to the most permissive option.
        /// </summary>
        internal static MismatchPolicy ResolvePolicy(string kind)
        {
            string configured = ClassForgePlugin.OnParityMismatch != null
                ? (ClassForgePlugin.OnParityMismatch.Value ?? "").Trim()
                : "";

            if (string.Equals(configured, "Block", StringComparison.OrdinalIgnoreCase))
                return MismatchPolicy.Block;
            if (string.Equals(configured, "WarnOnly", StringComparison.OrdinalIgnoreCase))
                return MismatchPolicy.WarnOnly;
            if (string.Equals(configured, "WarnAndSafeMode", StringComparison.OrdinalIgnoreCase))
                return MismatchPolicy.WarnAndSafeMode;

            if (string.Equals(kind, "FeaturesMismatch", StringComparison.Ordinal) ||
                string.Equals(kind, "MissingLocal", StringComparison.Ordinal) ||
                string.Equals(kind, "MissingRemote", StringComparison.Ordinal))
                return MismatchPolicy.Block;

            return MismatchPolicy.WarnAndSafeMode;
        }

        /// <summary>
        /// The failure UX P0.5 §4 asks for: name the exact <c>Section.Key</c> and BOTH values.
        ///
        /// <para>DevKit hands a <c>FeaturesMismatch</c> row the two full feature lists in
        /// <c>ParityVerdict.LocalValue</c>/<c>RemoteValue</c>, rendered by <c>FeaturesToString()</c> as
        /// <c>[a, b, c]</c>. That is everything needed to say "<c>Combat.VenueGridPreset</c>: you have
        /// <c>large</c>, they have <c>off</c>" instead of "Multiplayer desync likely" — which is precisely
        /// the EOR failure mode this work exists to improve on. Entries are diffed on the
        /// <c>feature:&lt;Section.Key&gt;=</c> prefix, so a pack id present on one side only is reported
        /// too. For non-feature kinds the two raw values are printed instead.</para>
        /// </summary>
        internal static string DescribeKnobDivergence(string kind, string local, string remote)
        {
            try
            {
                if (!string.Equals(kind, "FeaturesMismatch", StringComparison.Ordinal))
                    return "  local  : " + local + "\n  remote : " + remote;

                var lines = DiffFeatureLists(local, remote);
                if (lines.Count == 0)
                    return "  local  : " + local + "\n  remote : " + remote;

                var sb = new StringBuilder();
                sb.Append("  DIVERGED (").Append(lines.Count).Append("):");
                for (int i = 0; i < lines.Count; i++) sb.Append("\n    ").Append(lines[i]);
                return sb.ToString();
            }
            catch
            {
                return "  local  : " + local + "\n  remote : " + remote;
            }
        }

        /// <summary>The single most important diverged knob, for the one-line in-game banner.</summary>
        private static string FirstKnobLine(string kind, string local, string remote)
        {
            try
            {
                var lines = DiffFeatureLists(local, remote);
                if (lines.Count == 0) return kind;
                return lines.Count == 1 ? lines[0] : lines[0] + " (+" + (lines.Count - 1) + " more)";
            }
            catch { return kind; }
        }

        /// <summary>
        /// Diffs two <c>FeaturesToString()</c> renderings into human lines. Values can never contain a
        /// comma — <c>ParityValue.Format</c> maps <c>,</c> to <c>;</c> on both peers before the value is
        /// ever emitted — so splitting on <c>", "</c> is unambiguous.
        /// </summary>
        private static List<string> DiffFeatureLists(string local, string remote)
        {
            var l = ParseFeatures(local);
            var r = ParseFeatures(remote);

            var names = new List<string>();
            foreach (var name in l.Keys) if (!names.Contains(name)) names.Add(name);
            foreach (var name in r.Keys) if (!names.Contains(name)) names.Add(name);
            names.Sort(StringComparer.Ordinal);

            var lines = new List<string>();
            foreach (var name in names)
            {
                string lv, rv;
                bool hasL = l.TryGetValue(name, out lv);
                bool hasR = r.TryGetValue(name, out rv);
                if (hasL && hasR)
                {
                    if (string.Equals(lv, rv, StringComparison.Ordinal)) continue;
                    lines.Add(name + ": you have '" + lv + "', the peer has '" + rv + "'");
                }
                else if (hasL)
                {
                    lines.Add(name + ": you have '" + lv + "', the peer does NOT have this entry");
                }
                else
                {
                    lines.Add(name + ": the peer has '" + rv + "', you do NOT have this entry");
                }
            }
            return lines;
        }

        /// <summary>
        /// <c>"[CF_PACK_X, feature:Combat.VenueGridPreset=large]"</c> ⇒
        /// <c>{ "CF_PACK_X" -> "(present)", "Combat.VenueGridPreset" -> "large" }</c>.
        /// </summary>
        private static Dictionary<string, string> ParseFeatures(string rendered)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(rendered)) return map;

            string body = rendered.Trim();
            if (body.Length >= 2 && body[0] == '[' && body[body.Length - 1] == ']')
                body = body.Substring(1, body.Length - 2);
            if (body.Length == 0) return map;

            foreach (var raw in body.Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;

                if (entry.StartsWith(ParityRegistrationBuilder.FeaturePrefix, StringComparison.Ordinal))
                {
                    string rest = entry.Substring(ParityRegistrationBuilder.FeaturePrefix.Length);
                    int eq = rest.IndexOf('=');
                    if (eq >= 0) map[rest.Substring(0, eq)] = rest.Substring(eq + 1);
                    else map[rest] = "(present)";
                }
                else
                {
                    map["pack " + entry] = "(enabled)";
                }
            }
            return map;
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
