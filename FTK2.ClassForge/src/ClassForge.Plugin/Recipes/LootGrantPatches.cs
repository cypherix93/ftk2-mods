using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Loot;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Plugin
{
    /// <summary>
    /// M-LG2/M-LG3 — the game-side wiring for the loot-grant sync verb
    /// (docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md, as resolved by the verification wave's
    /// GATE A/GATE B: <c>DrawMark</c> -&gt; <c>ListDigest</c>, candidate-source seam). One hook:
    /// <c>LootDropHelper.GetLootDropsFromEnemies</c> postfix (§1.1/§5) — computes and applies the delta
    /// identically whether single-player or online. M-LG3 adds the MP wire-up on top of that unchanged
    /// compute/apply path: host send via <see cref="LootGrantTransportBridge"/>, receive/verify via
    /// <see cref="OnLootGrantPayloadReceived"/>, the §7 failure-mode matrix, and the loot-grant SafeMode
    /// latch. Ships DARK behind <c>[Skills] EnableLootGrants = false</c> (charter rule 3's
    /// disabled-by-default carrier; the default flip is gated on an in-game V-1 measurement that is
    /// operator scope, not part of this milestone — see the knob's own comment in ClassForgePlugin.cs).
    /// </summary>
    public static class LootGrantPatches
    {
        /// <summary>
        /// Single-slot per-combat pending-grant record (verb spec §3.4) — verification bookkeeping
        /// consumed here by the M-LG3 host send / receive-verify path.
        /// </summary>
        internal static readonly PendingGrantStore Store = new PendingGrantStore();

        private const string LootLogPrefix = "[ClassForge][CLASSFORGE_LOOT] ";

        private static bool _warnedOpValidation;

        /// <summary>
        /// Loot-grant SafeMode (verb spec §7.5b) — a SESSION-SCOPED latch local to this engine, distinct
        /// from <see cref="ParityBridge.Blocked"/> (ClassForge's whole-engine parity SafeMode). Engaged
        /// only by an OpsHash mismatch on receive (<see cref="ReportMismatch"/>); once set, the postfix
        /// disables grant COMPUTATION for the remainder of the session on this peer. Already-applied
        /// deltas are never retro-mutated (verb spec §7.5b — the lists are already on screen). Cleared at
        /// the next session boundary by <see cref="AdventureDirectorInitialize_Postfix"/>, the same
        /// anchor <see cref="ParityBridge"/> uses for its own latch.
        /// </summary>
        private static volatile bool _safeModeEngaged;

        internal static bool SafeModeEngaged { get { return _safeModeEngaged; } }

        /// <summary>
        /// Postfix for <c>LootDropHelper.GetLootDropsFromEnemies</c>. Harmony binds postfix parameters by
        /// name, so omitting <c>pMaxMaterialTier</c> (unused here) is safe. <c>ref List&lt;Thing&gt; __result</c>
        /// is mutated in place; on any failure it is left byte-for-byte as vanilla produced it (fail-safe,
        /// docs/CONVENTIONS.md).
        /// </summary>
        public static void GetLootDropsFromEnemies_Postfix(List<Entity> pParty, List<Entity> pEnemies,
            Env pEnv, GameRandom pGameRandom, ref List<Thing> __result)
        {
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return;
                if (ClassForgePlugin.EnableRecipeEngine == null || !ClassForgePlugin.EnableRecipeEngine.Value) return;
                if (ClassForgePlugin.EnableLootGrants == null || !ClassForgePlugin.EnableLootGrants.Value) return;
                // Loot-grant SafeMode (verb spec §7.5b): an OpsHash mismatch this session disables grant
                // COMPUTATION on this peer for the rest of the session. Vanilla loot is untouched either way.
                if (_safeModeEngaged) return;
                if (RecipeEngineHost.Book == null || RecipeEngineHost.Book.Ordered.Count == 0) return;
                if (__result == null || pParty == null || pParty.Count == 0) return;

                // Dev-only diagnostic (verb spec §4.1/§8): logs every draw taken from the SHARED combat
                // stream, so a diagnostic session can visually confirm this postfix took none. LogCalls
                // itself does not advance _nextCount -- it only toggles a debug log sink -- so this single,
                // explicitly opt-in call is not a violation of the zero-shared-draw rule below.
                if (ClassForgePlugin.DebugLogCombatRandomDraws != null && ClassForgePlugin.DebugLogCombatRandomDraws.Value
                    && pGameRandom != null)
                {
                    try { pGameRandom.LogCalls(true); } catch { /* diagnostic only */ }
                }

                // ===== Zero-shared-draw discipline (verb spec §4.1): from here on, pGameRandom is read
                // exactly ONCE more (its public readonly Seed field, a pure read -- not a draw) and then
                // never touched again. It is never passed to LootDeltaComputer or anything downstream. =====

                // ---- Step 1: snapshot the PRE-GRANT vanilla list -- ListDigest input (GATE A) ----
                List<PendingThing> pending = new List<PendingThing>(__result.Count);
                for (int i = 0; i < __result.Count; i++)
                {
                    Thing th = __result[i];
                    if (th == null) continue;
                    pending.Add(new PendingThing { Id = th.Id, ConfigName = th.ConfigName, Stack = th.StackCount });
                }
                string listDigest = LootGrantKey.ComputeListDigest(pending);

                // ---- Step 2: derive the private grant stream (verb spec §4.2) ----
                List<string> ownerGuids = GuidsOf(pParty);
                List<string> enemyGuids = GuidsOf(pEnemies);
                int combatSeed = pGameRandom != null ? pGameRandom.Seed : 0; // field read -- zero draws
                string grantKey = LootGrantKey.ComputeGrantKey(combatSeed, enemyGuids, listDigest, ownerGuids);
                int grantSeed = LootGrantKey.ComputeGrantSeed(combatSeed, listDigest, enemyGuids);
                IRandomSource grantRandom = new GameRandomSource(new GameRandom(grantSeed, pIgnoreMultiplayerStaticSeed: true));

                // ---- Step 3: compute the delta (pure; owns its own fixed iteration order, §4.3) ----
                List<ICombatEntity> owners = BuildOwners(pParty, pEnv);
                IItemCandidateSource candidateSource = new ThingCandidateSource();

                // Encounter Modifiers spec §11/§6.3 item 6 (M-EM4) — the read-only reward-half interface,
                // resolved for THIS combat's already-cached CombatRuntime (the SAME single-slot dispatcher
                // every ON_COMBAT_START hook synced; TryBegin here reuses it, never reallocates, since the
                // CombatState/seed identity has not changed between combat end and this postfix).
                ModifierRewardsInput modifierRewards = ResolveModifierRewardsInput();

                IReadOnlyList<LootOp> ops = LootDeltaComputer.Compute(
                    RecipeEngineHost.Book, owners, grantKey, grantRandom, candidateSource, modifierRewards, pending);

                // Defense in depth (§8.1 item 6): Compute() can never emit a reserved op today (no v1 recipe
                // effect maps to REPLACE_ITEM), but a future consumer effect must not be able to smuggle one
                // onto the pending list unnoticed.
                List<string> opErrors;
                if (!LootOpValidator.ValidateV1(ops, out opErrors))
                {
                    ops = FilterAllowed(ops);
                    if (!_warnedOpValidation)
                    {
                        _warnedOpValidation = true;
                        ClassForgePlugin.Log.LogWarning(
                            "[ClassForge] Loot-grant delta contained reserved op(s); dropped: " + string.Join("; ", opErrors));
                    }
                }

                string opsHash = LootGrantCodec.ComputeOpsHash(ops);

                // ---- Step 4/5: apply + reconcile onto the real list (append-only, §3.2) ----
                if (ops.Count > 0)
                {
                    LootOpApplier.Apply(pending, ops, grantKey);
                    ReconcileToReal(pending, __result);
                }

                // ---- Step 6: record for verification (mirrors this peer's own future receive) ----
                Store.Arm(grantKey, ops, opsHash);

                // ---- Step 7 (M-LG3): host send, over the MP posture in verb spec §7 ----
                // SP fast path (§8.0): no transport resolution is attempted at all when offline.
                bool onlineMultiplayer = NetworkSessionState.IsOnlineMultiplayer();
                bool isHost = false;
                bool transportAvailable = false;
                bool sent = false;
                if (onlineMultiplayer)
                {
                    isHost = NetworkSessionState.IsHost();
                    transportAvailable = LootGrantTransportBridge.CanSend();
                    // Non-host peers never send (verb spec §7 feature table: host only).
                    if (isHost && transportAvailable)
                    {
                        sent = SendGrant(grantKey, combatSeed, listDigest, ops, opsHash);
                    }
                }

                LogGrantState(grantKey, ops.Count, opsHash, onlineMultiplayer, isHost, transportAvailable, sent);
            }
            catch (Exception ex)
            {
                // Fail-safe (CONVENTIONS.md): __result is left exactly as vanilla produced it -- nothing
                // computed above is ever written back to it once an exception has been thrown.
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] Loot-grant postfix failed (fail-safe, vanilla loot list unchanged): " + ex);
            }
        }

        // =====================================================================================
        // M-EM4: reward-half interface (Encounter Modifiers spec §11) -> ModifierRewardsInput
        // =====================================================================================

        /// <summary>
        /// Resolves this combat's active-modifier rewards, if any, via the read-only §11 interface
        /// (<see cref="RecipeEngineHost.ResolveActiveModifierRewards"/>). <c>TryBegin</c> here reuses the
        /// SAME single-slot <c>(CombatKey, RecipeDispatcher)</c> cache every <c>ON_COMBAT_START</c> hook
        /// already synced this combat — the CombatState/seed identity has not changed between combat end
        /// and this postfix, so this call reallocates nothing and re-runs no §4.5 reconstruction; it is a
        /// pure read of state already computed. Returns null (⇒ zero reward ops appended) whenever the
        /// recipe engine cannot resolve the current combat at all, no pack shipped a
        /// <c>modifiers.json</c>, or no modifier was selected — the ordinary "no encounter modifier this
        /// combat" case, identical on every peer since the selection itself is (§4.5/§6.3).
        /// </summary>
        private static ModifierRewardsInput ResolveModifierRewardsInput()
        {
            try
            {
                CombatContextAdapter ctx; RecipeDispatcher dispatcher;
                if (!RecipeEngineHost.TryBegin(out ctx, out dispatcher)) return null;

                var active = RecipeEngineHost.ResolveActiveModifierRewards(dispatcher.State.Current);
                if (active == null) return null;
                if (active.XpBonusPercent == 0 && active.GoldBonusPercent == 0 && active.ExtraLootChancePercent == 0)
                    return null; // an active modifier with an empty/zero Rewards block -- nothing to emit

                return new ModifierRewardsInput
                {
                    XpBonusPercent = active.XpBonusPercent,
                    GoldBonusPercent = active.GoldBonusPercent,
                    ExtraLootChancePercent = active.ExtraLootChancePercent
                };
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    LootLogPrefix + "modifier-reward resolution failed (fail-safe, zero reward ops emitted): " + ex);
                return null;
            }
        }

        // =====================================================================================
        // M-LG3: transport init, host send, receive/verify, log-visible grant state
        // =====================================================================================

        /// <summary>
        /// Registers the <c>CF_SYNC_LOOT_GRANT_V1</c> receiver with FTK2.DevKit's TransportService, and
        /// the SafeMode-reset session hook. Called once from <c>ClassForgePlugin.Awake</c>, AFTER the
        /// <c>[Skills]</c> knobs are bound. Deliberately gated the same way the postfix itself is gated
        /// (recipe engine + loot grants both enabled) — "at plugin init, only when the loot engine is
        /// enabled" (M-LG3 task scope): with the shipped dark default this registers nothing, so an
        /// operator who never flips the knob sees zero new receive-side behavior.
        /// </summary>
        internal static void InitializeTransport()
        {
            if (ClassForgePlugin.EnableRecipeEngine == null || !ClassForgePlugin.EnableRecipeEngine.Value) return;
            if (ClassForgePlugin.EnableLootGrants == null || !ClassForgePlugin.EnableLootGrants.Value) return;
            LootGrantTransportBridge.RegisterReceiver(LootGrantCodec.ActionKey, OnLootGrantPayloadReceived);
        }

        /// <summary>
        /// Postfix for <c>AdventureDirector.Initialize</c> — the same session-start anchor
        /// <see cref="ParityBridge.AdventureDirectorInitialize_Postfix"/> uses to clear ITS latch, for
        /// the same reason: a SafeMode engaged in a prior session must not silently disable grants in a
        /// fresh session that never diverged. Re-derived, not merely cleared — a fresh mismatch this
        /// session re-latches exactly as before.
        /// </summary>
        public static void AdventureDirectorInitialize_Postfix()
        {
            try
            {
                if (!_safeModeEngaged) return;
                _safeModeEngaged = false;
                ClassForgePlugin.Log.LogInfo(LootLogPrefix + "new session started -- clearing the previous session's loot-grant SafeMode latch.");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(LootLogPrefix + "session-start SafeMode reset failed (non-fatal): " + ex);
            }
        }

        /// <summary>
        /// Encodes and sends this combat's grant over <see cref="LootGrantTransportBridge"/> (host only,
        /// verb spec §3.1/§7). Digest-only degrade over DevKit's payload cap is handled inside
        /// <see cref="LootGrantCodec.EncodeCapped"/> itself.
        /// </summary>
        private static bool SendGrant(string grantKey, int combatSeed, string listDigest,
            IReadOnlyList<LootOp> ops, string opsHash)
        {
            try
            {
                LootGrantPayload payload = new LootGrantPayload
                {
                    SchemaVersion = LootGrantCodec.CurrentSchemaVersion,
                    Mode = LootGrantMode.MIRROR,
                    GrantKey = grantKey,
                    CombatSeed = combatSeed,
                    ListDigest = listDigest,
                    Ops = new List<LootOp>(ops),
                    OpsHash = opsHash
                };
                string json = LootGrantCodec.EncodeCapped(payload, LootGrantCodec.DefaultMaxPayloadBytes);
                bool ok = LootGrantTransportBridge.Send(json);
                if (!ok)
                {
                    ClassForgePlugin.Log.LogWarning(LootLogPrefix + "host send failed for GrantKey=" + grantKey +
                        " -- this combat is logged unverified on peers (verb spec §7: transport failure has zero " +
                        "gameplay effect, Mode M grants are identical on every peer regardless).");
                }
                return ok;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(LootLogPrefix + "host send threw (fail-safe, ignored): " + ex);
                return false;
            }
        }

        /// <summary>
        /// The CLASSFORGE_LOOT log-visible grant state (M-LG3 task item 4): GrantKey, op count, OpsHash
        /// prefix, and a one-word status enough for an operator smoke script to assert against. Gated on
        /// <c>[General] VerboseLogging</c> like every other per-combat debug line this engine emits.
        /// </summary>
        private static void LogGrantState(string grantKey, int opCount, string opsHash,
            bool onlineMultiplayer, bool isHost, bool transportAvailable, bool sent)
        {
            if (ClassForgePlugin.VerboseLogging == null || !ClassForgePlugin.VerboseLogging.Value) return;

            string status;
            if (!onlineMultiplayer) status = "unverified (single-player, no host push expected)";
            else if (sent) status = "unverified (sent, awaiting local verification)";
            else if (isHost) status = "unverified (host send failed)";
            else if (!transportAvailable) status = "unverified (transport unavailable)";
            else status = "unverified (peer, awaiting host push)";

            ClassForgePlugin.Log.LogDebug(LootLogPrefix + "GrantKey=" + grantKey +
                " ops=" + opCount.ToString(CultureInfo.InvariantCulture) +
                " opsHash=" + ShortHash(opsHash) + " status=" + status);
        }

        /// <summary>
        /// The DevKit TransportService receiver for <c>CF_SYNC_LOOT_GRANT_V1</c> (M-LG3 task item 3),
        /// registered by <see cref="InitializeTransport"/>. Decodes and dispatches per §3.4/§7: every
        /// branch below is one row of the verb spec §7 failure-mode matrix. Never throws — this runs
        /// straight off DevKit's network-hook prefix, and an exception here must never propagate into
        /// the game's network pump (fail-safe rule, docs/CONVENTIONS.md; DevKit's own dispatcher also
        /// isolates it, this is belt-and-braces).
        /// </summary>
        private static void OnLootGrantPayloadReceived(string json)
        {
            try
            {
                LootGrantPayload payload;
                string decodeError;
                if (!LootGrantCodec.TryDecode(json, out payload, out decodeError))
                {
                    // §7 row: "Payload malformed / unknown SchemaVersion" -> discard + one warning.
                    ClassForgePlugin.Log.LogWarning(LootLogPrefix + "malformed CF_SYNC_LOOT_GRANT_V1 payload (discarded): " + decodeError);
                    return;
                }

                LootReceiveResult result = Store.Receive(payload);
                switch (result)
                {
                    case LootReceiveResult.Verified:
                        LogReceive(payload, "verified");
                        break;

                    case LootReceiveResult.Mismatch:
                        ReportMismatch(payload);
                        break;

                    case LootReceiveResult.Duplicate:
                        // §7 row: "Payload duplicated" -> GrantKey match, no-op.
                        LogReceive(payload, "duplicate (no-op)");
                        break;

                    case LootReceiveResult.Stale:
                        // §7 row: "Payload reordered across combats" -> stale GrantKey, discard + log.
                        LogReceive(payload, "stale GrantKey (discarded)");
                        break;

                    case LootReceiveResult.HeldInInbox:
                        // §7 row: "Payload late (after loot screen)" / host-faster-than-client — held in
                        // the single-slot inbox (§3.4), compared once the local postfix arms this key.
                        LogReceive(payload, "held in single-slot inbox (host faster than this peer's local compute)");
                        break;

                    case LootReceiveResult.Malformed:
                        ClassForgePlugin.Log.LogWarning(LootLogPrefix + "rejected CF_SYNC_LOOT_GRANT_V1 payload (v1 op-vocabulary gate), GrantKey=" + payload.GrantKey);
                        break;
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError(LootLogPrefix + "receive handler failed (fail-safe, ignored): " + ex);
            }
        }

        private static void LogReceive(LootGrantPayload payload, string status)
        {
            if (ClassForgePlugin.VerboseLogging == null || !ClassForgePlugin.VerboseLogging.Value) return;
            ClassForgePlugin.Log.LogDebug(LootLogPrefix + "GrantKey=" + payload.GrantKey +
                " ops=" + (payload.Ops != null ? payload.Ops.Count.ToString(CultureInfo.InvariantCulture) : "(digest-only)") +
                " opsHash=" + ShortHash(payload.OpsHash) + " status=" + status);
        }

        /// <summary>
        /// §7 row: "OpsHash mismatch (real divergence)" -> loud banner naming the mod + offending op
        /// Source, engage loot-grant SafeMode. The already-applied local delta is never retro-mutated
        /// (§7.5b) — this only reports and latches; nothing here touches the loot list.
        /// </summary>
        private static void ReportMismatch(LootGrantPayload payload)
        {
            PendingGrant current = Store.Current;
            string localHash = ShortHash(current != null ? current.LocalOpsHash : string.Empty);
            string remoteHash = ShortHash(payload.OpsHash);
            string offending = FindOffendingSource(current, payload);

            ClassForgePlugin.Log.LogError(
                "==================================================================\n" +
                "  ClassForge LOOT-GRANT MISMATCH -- GrantKey=" + payload.GrantKey + "\n" +
                "==================================================================\n" +
                "  local  OpsHash : " + localHash + "\n" +
                "  remote OpsHash : " + remoteHash + "\n" +
                "  offending op   : " + (offending ?? "(could not be isolated -- digest-only payload or op-count differs)") + "\n" +
                "------------------------------------------------------------------\n" +
                "  This peer's locally computed loot-grant delta disagrees with the host's for this\n" +
                "  combat (verb spec §7.5b). The delta ALREADY APPLIED to this peer's loot screen is\n" +
                "  NOT retro-mutated. Loot-grant SafeMode is now ENGAGED for the REST OF THIS SESSION:\n" +
                "  grant computation on this peer is disabled going forward (see [Skills]\n" +
                "  EnableLootGrants). This is a LOCAL, verb-scoped latch -- it does not block the rest\n" +
                "  of ClassForge (contrast ParityBridge's whole-engine Block policy). FIX: verify every\n" +
                "  peer's ClassPacks are byte-identical, then start a new session.\n" +
                "==================================================================");

            _safeModeEngaged = true;
        }

        /// <summary>
        /// Best-effort per-op diff between the local record and the received payload, for the mismatch
        /// banner's "offending op" line (verb spec §7.5b: "names the recipe via Source"). Returns null
        /// (banner falls back to a generic note) when either side has no <c>Ops</c> to diff — the
        /// digest-only degrade path (§3.1) carries no <c>Ops</c> at all.
        /// </summary>
        private static string FindOffendingSource(PendingGrant current, LootGrantPayload payload)
        {
            if (current == null || current.LocalOps == null || payload.Ops == null) return null;
            IReadOnlyList<LootOp> local = current.LocalOps;
            List<LootOp> remote = payload.Ops;
            int max = local.Count > remote.Count ? local.Count : remote.Count;
            for (int i = 0; i < max; i++)
            {
                LootOp l = i < local.Count ? local[i] : null;
                LootOp r = i < remote.Count ? remote[i] : null;
                string lRow = l != null ? l.ToCanonicalRow() : "(missing)";
                string rRow = r != null ? r.ToCanonicalRow() : "(missing)";
                if (string.Equals(lRow, rRow, StringComparison.Ordinal)) continue;

                string source = l != null && !string.IsNullOrEmpty(l.Source) ? l.Source
                    : (r != null ? r.Source : null);
                return "index " + i.ToString(CultureInfo.InvariantCulture) + " Source='" + (source ?? "(unknown)") +
                       "' local=[" + lRow + "] remote=[" + rRow + "]";
            }
            return null;
        }

        private static string ShortHash(string opsHash)
        {
            if (string.IsNullOrEmpty(opsHash)) return "(none)";
            const string prefix = "sha256:";
            if (opsHash.StartsWith(prefix, StringComparison.Ordinal) && opsHash.Length >= prefix.Length + 12)
                return prefix + opsHash.Substring(prefix.Length, 12);
            return opsHash.Length > 12 ? opsHash.Substring(0, 12) : opsHash;
        }

        // =====================================================================================
        // Owner adaptation
        // =====================================================================================

        /// <summary>
        /// Wraps each <paramref name="party"/> entity onto <see cref="ICombatEntity"/> via the same
        /// <see cref="CombatContextAdapter"/>/<see cref="EntityAdapter"/> pair every other recipe hook uses,
        /// so <c>Passives</c> resolution (class config + equipped Things' <c>Equippable.Passives</c>,
        /// including <c>TRAIT_*</c> carriers) is identical to every other trigger's owner-holds-recipe check.
        /// </summary>
        private static List<ICombatEntity> BuildOwners(List<Entity> party, Env env)
        {
            List<ICombatEntity> owners = new List<ICombatEntity>(party.Count);
            CombatState state = null;
            try { state = env != null && env.GameRun != null ? env.GameRun.CombatState : null; }
            catch { state = null; }

            CombatContextAdapter ctx = new CombatContextAdapter(env, state);
            for (int i = 0; i < party.Count; i++)
            {
                Entity e = party[i];
                if (e == null) continue;
                ICombatEntity adapter = ctx.Wrap(e);
                if (adapter != null) owners.Add(adapter);
            }
            return owners;
        }

        private static List<string> GuidsOf(List<Entity> entities)
        {
            List<string> guids = new List<string>();
            if (entities == null) return guids;
            for (int i = 0; i < entities.Count; i++)
            {
                try { guids.Add(entities[i] != null ? (entities[i].Guid ?? string.Empty) : string.Empty); }
                catch { guids.Add(string.Empty); }
            }
            return guids;
        }

        private static List<LootOp> FilterAllowed(IReadOnlyList<LootOp> ops)
        {
            List<LootOp> result = new List<LootOp>();
            if (ops == null) return result;
            for (int i = 0; i < ops.Count; i++)
                if (ops[i] != null && LootOpValidator.IsAllowedInV1(ops[i].Kind)) result.Add(ops[i]);
            return result;
        }

        // =====================================================================================
        // Pending-model <-> real Thing list reconciliation
        // =====================================================================================

        /// <summary>
        /// Syncs <see cref="LootOpApplier.Apply"/>'s mutations back onto the real <c>List&lt;Thing&gt;</c>:
        /// existing <c>Thing</c>s whose stack changed (ADD_GOLD merge, SCALE_STACK) are updated in place;
        /// new pending entries (ADD_GOLD's no-existing-stack branch, every ADD_ITEM) are minted via
        /// <c>InventoryHelper.CreateThing</c> with the deterministic id already computed at compute-time
        /// (verb spec §3.3) and appended at the end, preserving <c>pending</c>'s order.
        /// </summary>
        private static void ReconcileToReal(List<PendingThing> pending, List<Thing> real)
        {
            for (int i = 0; i < real.Count; i++)
            {
                Thing th = real[i];
                if (th == null || string.IsNullOrEmpty(th.Id)) continue;
                PendingThing match = FindById(pending, th.Id);
                if (match != null) th._stackCount = match.Stack;
            }

            for (int i = 0; i < pending.Count; i++)
            {
                PendingThing pt = pending[i];
                if (pt == null || string.IsNullOrEmpty(pt.ConfigName)) continue;
                if (ExistsById(real, pt.Id)) continue;

                Thing minted = InventoryHelper.CreateThing(pt.ConfigName);
                minted.Id = pt.Id;
                minted._stackCount = pt.Stack > 0 ? pt.Stack : 1;
                real.Add(minted);
            }
        }

        private static PendingThing FindById(List<PendingThing> pending, string id)
        {
            for (int i = 0; i < pending.Count; i++)
                if (pending[i] != null && string.Equals(pending[i].Id, id, StringComparison.Ordinal)) return pending[i];
            return null;
        }

        private static bool ExistsById(List<Thing> real, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < real.Count; i++)
                if (real[i] != null && string.Equals(real[i].Id, id, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// <see cref="IItemCandidateSource"/> against <c>Env.Configs.Things</c> — verb spec OQ-2, GATE B. The
    /// filter contract is recorded on the seam itself (<c>LootCandidates.cs</c>); mirrored here from
    /// <c>LootDropHelper.FilterLootNames</c> (tools/out/decompile/FTK2/LootDropHelper.cs L47-73), with the
    /// same helper names verified against that method body: <c>StatsHelper.GetEnabledExpansions()</c>,
    /// <c>CoreHelper.GetRarityWeightValue(eItemRarities)</c>. <c>Rarity</c> enum validation is the deferred
    /// M-LG1 deviation, performed here because <c>eItemRarities</c> is a game type the pure-C# recipe core
    /// cannot reference.
    /// </summary>
    internal sealed class ThingCandidateSource : IItemCandidateSource
    {
        private static bool _warnedBadRarity;

        public IReadOnlyList<string> GetCandidates(string tag, string rarity)
        {
            List<string> result = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(tag)) return result;
                Configs configs = global::Env.Configs;
                if (configs == null || configs.Things == null) return result;

                eItemRarities? wantRarity = null;
                if (!string.IsNullOrEmpty(rarity))
                {
                    eItemRarities parsed;
                    if (Enum.TryParse(rarity, true, out parsed) && Enum.IsDefined(typeof(eItemRarities), parsed))
                    {
                        wantRarity = parsed;
                    }
                    else
                    {
                        if (!_warnedBadRarity)
                        {
                            _warnedBadRarity = true;
                            ClassForgePlugin.Log.LogWarning(
                                "[ClassForge] ITEM_TAG_GRANT Rarity '" + rarity + "' is not a member of " +
                                "eItemRarities -- treating as zero eligible candidates (fail-safe: this " +
                                "recipe's grant simply does not fire, rather than crashing or picking the " +
                                "wrong pool). Valid values: " + string.Join(", ", Enum.GetNames(typeof(eItemRarities))));
                        }
                        return result;
                    }
                }

                List<eExpansions> enabledExpansions = StatsHelper.GetEnabledExpansions();

                foreach (KeyValuePair<string, ThingConfig> kv in configs.Things)
                {
                    ThingConfig cfg = kv.Value;
                    if (cfg == null) continue;
                    if (cfg.Tags == null || !ContainsOrdinalIgnoreCase(cfg.Tags, tag)) continue;
                    if (wantRarity.HasValue && cfg.Rarity != wantRarity.Value) continue;
                    if (cfg.Hidden) continue;
                    if (cfg.Value == 0) continue;
                    if (CoreHelper.GetRarityWeightValue(cfg.Rarity) == 0m) continue;
                    if (enabledExpansions == null || !enabledExpansions.Contains(cfg.Expansion)) continue;
                    result.Add(kv.Key);
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] ITEM_TAG_GRANT candidate scan failed (fail-safe, no candidates): " + ex);
                return new List<string>();
            }
            return result;
        }

        private static bool ContainsOrdinalIgnoreCase(List<string> tags, string tag)
        {
            for (int i = 0; i < tags.Count; i++)
                if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
