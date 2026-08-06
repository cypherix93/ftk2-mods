using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Blessings.Core.Model;
using Blessings.Core.Resolution;

namespace Blessings.Plugin
{
    /// <summary>
    /// The grant anchor (SPEC §3.4, §3.5, §6; binding resolution GATE E / V7): a postfix on
    /// <c>AdventureDirector.Initialize(UIDocument, UIDocument, int, GameObject,
    /// AdventureCameraController, GameObject, InputController)</c>, the same anchor FTK2.DevKit's own
    /// parity handshake uses, ordered after it in the postfix chain by BepInEx's default load order
    /// (Harmony postfixes on the SAME target method from different Harmony instances run in an
    /// unspecified but stable-per-session order; correctness here does not depend on ordering relative to
    /// DevKit's own postfix -- it depends on <see cref="DevKitParityBridge.HasVerifiedMatch"/> reflecting
    /// whatever DevKit's handshake has produced by the time THIS anchor call is observed, which may
    /// legitimately be "not yet" on the very first call of an online session; see GATE E below).
    ///
    /// Orchestration, in order: master switch -&gt; data-load gate -&gt; resolve Mode via
    /// Blessings.Core -&gt; verify every roster TraitId resolves in <c>Env.Configs.Things</c> -&gt;
    /// GATE E (online parity gate) -&gt; grant plan -&gt; <c>CharacterHelper.GiveTrait</c> per plan
    /// entry -&gt; latch. Idempotent throughout (trait-presence check first, §3.5): a missed window
    /// self-heals at the next <c>Initialize</c> call (e.g. save-load re-entry).
    /// </summary>
    internal static class GrantAnchorPatches
    {
        // ---- once-per-session state -------------------------------------------------------------
        // AdventureDirector.Initialize can fire more than once per process (new run, save-load
        // re-entry, party rebuild). Every one-shot decision below is cached so repeat calls neither
        // spam the log nor redo expensive work, while the actual grant/latch logic still re-runs every
        // call (that's what makes the self-heal in the class doc comment true).
        private static bool _rosterVerified;
        private static bool _rosterVerificationFailed;
        private static bool _loggedRosterMissing;
        private static bool _loggedOnlineGateDecision;
        private static bool _loggedModeDisabledThisSession;
        private static string _lastLoggedResolvedId;

        internal static void Initialize_Postfix()
        {
            try
            {
                DevKitParityBridge.ResetForNewSession();

                if (BlessingsPlugin.Enabled == null || !BlessingsPlugin.Enabled.Value) return; // master switch off.
                if (BlessingsPlugin.LoadFailed || BlessingsPlugin.Registry == null) return; // §3.3 fail-closed data gate; already logged once at Awake.
                if (DevKitParityBridge.SafeMode) return; // §9.5: nothing else runs in SafeMode.

                if (!EnsureRosterResolvesAgainstConfigs(BlessingsPlugin.Registry)) return; // §3.3 fail-closed; logs once.

                var resolution = BlessingResolver.Resolve(
                    BlessingsPlugin.Registry, BlessingsPlugin.Mode.Value, MapGenSeed(), ConfigName());

                if (resolution.Kind == ResolutionKind.Disabled)
                {
                    if (!_loggedModeDisabledThisSession)
                    {
                        _loggedModeDisabledThisSession = true;
                        if (!string.IsNullOrEmpty(resolution.Message))
                            BlessingsPlugin.Log.LogWarning("[Blessings] " + resolution.Message);
                        else
                            BlessingsPlugin.Log.LogInfo("[Blessings] Mode=Disabled -- no blessing this run.");
                    }
                    return;
                }

                var resolved = resolution.Resolved;
                if (resolved == null) return; // defensive; Resolve never returns Kind != Disabled with a null Resolved.

                if (!GrantIsAllowedThisCall()) return; // GATE E.

                GrantToParty(resolved);
            }
            catch (Exception ex)
            {
                // Fail-safe (repo convention): never let a Harmony patch body take the game down.
                BlessingsPlugin.Log.LogError("[Blessings] Grant-anchor postfix failed (fail-safe, no grants this call): " + ex);
            }
        }

        /// <summary>
        /// §3.3: "verify every roster TraitId in its blessings.json roster resolves in Configs.Things;
        /// any miss -&gt; the plugin disables itself for the session with one loud log line." Cached:
        /// once it has either passed or failed, later calls reuse the verdict without re-scanning
        /// Configs on every Initialize (ClassForge/the pack merge do not change mid-session).
        /// </summary>
        private static bool EnsureRosterResolvesAgainstConfigs(BlessingsRegistry registry)
        {
            if (_rosterVerified) return !_rosterVerificationFailed;

            var missing = new List<string>();
            try
            {
                var things = Env.Configs != null ? Env.Configs.Things : null;
                if (things == null)
                {
                    missing.Add("(Env.Configs.Things itself is unavailable)");
                }
                else
                {
                    foreach (var entry in registry.Blessings)
                        if (!things.ContainsKey(entry.TraitId)) missing.Add(entry.TraitId);
                }
            }
            catch (Exception ex)
            {
                missing.Add("(Env.Configs.Things read threw: " + ex.Message + ")");
            }

            _rosterVerified = true;
            _rosterVerificationFailed = missing.Count > 0;

            if (_rosterVerificationFailed && !_loggedRosterMissing)
            {
                _loggedRosterMissing = true;
                BlessingsPlugin.Log.LogError(
                    "[Blessings] " + missing.Count.ToString(CultureInfo.InvariantCulture) +
                    " roster TraitId(s) do not resolve in Env.Configs.Things -- this plugin is DISABLED for the " +
                    "session (fail-closed data dependency, SPEC §3.3). Is FTK2.ClassForge installed, is the " +
                    "BLSS_PACK_EOR_BLESSINGS pack enabled, and does ClassForge's [Packs] AdditionalRoots point at " +
                    "this plugin's ClassPacks folder? Missing: " + string.Join(", ", missing) + ".");
            }

            return !_rosterVerificationFailed;
        }

        /// <summary>
        /// GATE E (binding resolution, overrides SPEC §6's "patch ordering" framing): offline
        /// (<c>NetworkData.PlayingOnlineMultiplayer == false</c>) -&gt; grant immediately, unconditionally.
        /// Online -&gt; grant ONLY on a POSITIVE verified parity match
        /// (<see cref="DevKitParityBridge.HasVerifiedMatch"/>); no verdict yet -&gt; do nothing this
        /// anchor call (fail-closed). The grant plan is idempotent (trait-presence-first, §3.5), so a
        /// call that is denied here self-heals at the next <c>Initialize</c> (e.g. save-load re-entry) --
        /// exactly the "first-session-online grants may be legitimately deferred" posture documented in
        /// SPEC §11 OQ3 and ClassForge's own TraitLoadoutPatches.SessionInjectionGate.
        /// </summary>
        private static bool GrantIsAllowedThisCall()
        {
            bool online = IsOnlineMultiplayer();
            bool allowed = !online || DevKitParityBridge.HasVerifiedMatch();

            if (!_loggedOnlineGateDecision)
            {
                _loggedOnlineGateDecision = true;
                if (!online)
                {
                    BlessingsPlugin.Log.LogInfo("[Blessings] GATE E: offline/single-player session -- granting unconditionally.");
                }
                else
                {
                    BlessingsPlugin.Log.LogInfo("[Blessings] GATE E: online multiplayer session -- " +
                        (allowed
                            ? "DevKit parity handshake already verified Match; granting."
                            : "no verified parity Match yet (handshake may not have completed) -- grant FAILS " +
                              "CLOSED this call. Expected on the very first Initialize of an online session; " +
                              "self-heals at the next Initialize (save-load re-entry) once the handshake resolves."));
                }
            }

            return allowed;
        }

        private static void GrantToParty(BlessingEntry blessing)
        {
            var env = RouterHelper.Env;
            var gameRun = env != null ? env.GameRun : null;
            if (gameRun == null || gameRun.Entities == null)
            {
                BlessingsPlugin.Log.LogWarning("[Blessings] No active GameRunData.Entities -- nothing to grant this call.");
                return;
            }

            var partyEntities = gameRun.Entities.FindAll(e => e != null && e.Has<PlayerComponent>());
            var entityByGuid = new Dictionary<string, Entity>(StringComparer.Ordinal);
            var alreadyHasTraitByGuid = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var entity in partyEntities)
            {
                if (entity == null || string.IsNullOrEmpty(entity.Guid)) continue;
                entityByGuid[entity.Guid] = entity;
                bool has;
                try
                {
                    has = InventoryHelper.GetTraits(entity).Any(t => t != null && t.ConfigName == blessing.TraitId);
                }
                catch (Exception ex)
                {
                    has = false; // fail-open on the READ so a broken trait list doesn't block the grant loop entirely.
                    BlessingsPlugin.Log.LogWarning("[Blessings] GetTraits threw for entity '" + entity.Guid + "' (treated as not-present): " + ex.Message);
                }
                alreadyHasTraitByGuid[entity.Guid] = has;
            }

            var plan = GrantPlanner.Plan(entityByGuid.Keys, alreadyHasTraitByGuid);

            int granted = 0, skipped = 0;
            foreach (var planEntry in plan)
            {
                if (!planEntry.ShouldGrant) { skipped++; continue; }

                Entity entity;
                if (!entityByGuid.TryGetValue(planEntry.EntityGuid, out entity) || entity == null) continue;

                try
                {
                    CharacterHelper.GiveTrait(entity, blessing.TraitId);
                    granted++;
                    if (BlessingsPlugin.VerboseLogging.Value)
                        BlessingsPlugin.Log.LogDebug("[Blessings] Granted " + blessing.TraitId + " to entity '" + planEntry.EntityGuid + "'.");
                }
                catch (Exception ex)
                {
                    BlessingsPlugin.Log.LogError("[Blessings] GiveTrait('" + blessing.TraitId + "') failed for entity '" + planEntry.EntityGuid + "': " + ex);
                }
            }

            // Latch write happens AFTER the grant loop, symmetric on every peer that reaches this point
            // (§3.5). Idempotent: writing 1 over an existing 1 is a no-op in effect.
            try
            {
                if (gameRun.Stats != null) gameRun.Stats[GrantPlanner.LatchStatKey(blessing.Id)] = 1;
            }
            catch (Exception ex)
            {
                BlessingsPlugin.Log.LogWarning("[Blessings] Failed to write the BLSS_ACTIVE_* latch (non-fatal -- trait presence is the source of truth, §3.5): " + ex.Message);
            }

            if (granted > 0 || _lastLoggedResolvedId != blessing.Id)
            {
                _lastLoggedResolvedId = blessing.Id;
                BlessingsPlugin.Log.LogInfo("[Blessings] Blessing resolved: " + blessing.Id + " (" + blessing.TraitId + "). Granted " +
                    granted.ToString(CultureInfo.InvariantCulture) + " part" + (granted == 1 ? "y member" : "y members") +
                    ", " + skipped.ToString(CultureInfo.InvariantCulture) + " already held it.");
            }
        }

        private static int MapGenSeed()
        {
            try
            {
                var gameRun = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
                return gameRun != null ? gameRun.MapGenSeed : 0;
            }
            catch { return 0; }
        }

        private static string ConfigName()
        {
            try
            {
                var gameRun = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
                return gameRun != null ? gameRun.ConfigName : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads <c>Env.NetworkData.PlayingOnlineMultiplayer</c> directly (a compile-time field read --
        /// unlike ClassForge's NetworkSessionState/DevKit's GameSurface, which resolve this reflectively
        /// for defense-in-depth against a rename). This plugin already takes a compile-time dependency on
        /// every other FTK2 surface it touches at this exact anchor (AdventureDirector, Entity,
        /// PlayerComponent, CharacterHelper, InventoryHelper, GameRunData); a rename here would already
        /// break the build loudly at compile time rather than silently at runtime, so the extra
        /// reflection indirection buys nothing additional here. Fails CLOSED (assume online) on any
        /// exception -- GATE E must never fail open.
        /// </summary>
        private static bool IsOnlineMultiplayer()
        {
            try
            {
                var env = RouterHelper.Env;
                if (env == null || env.NetworkData == null) return true;
                return env.NetworkData.PlayingOnlineMultiplayer;
            }
            catch
            {
                return true;
            }
        }
    }
}
