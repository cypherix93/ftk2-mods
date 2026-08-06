using System;
using System.Collections.Generic;

namespace ClassForge.Recipes.Loot
{
    /// <summary>Outcome of feeding one received payload to <see cref="PendingGrantStore"/> — verb spec
    /// §7 failure-mode matrix.</summary>
    public enum LootReceiveResult
    {
        /// <summary>Payload could not be decoded, or failed the v1 op-vocabulary gate.</summary>
        Malformed,
        /// <summary>No local record yet for this <c>GrantKey</c> (host faster than client) — held in the
        /// single-slot inbox for comparison when the local record forms.</summary>
        HeldInInbox,
        /// <summary>A local record exists for a DIFFERENT (older) <c>GrantKey</c> — discarded.</summary>
        Stale,
        /// <summary>This <c>GrantKey</c> was already verified — no-op.</summary>
        Duplicate,
        /// <summary><c>OpsHash</c> matched the local record.</summary>
        Verified,
        /// <summary><c>OpsHash</c> did NOT match — the silent-drift-to-loud-failure case (§7.5b, loot-grant
        /// SafeMode territory; this store only detects, the Plugin unit is what engages SafeMode).</summary>
        Mismatch
    }

    /// <summary>One armed combat's local record — verb spec §3.4:
    /// <c>{GrantKey, localOps, localOpsHash, applied, verified}</c>.</summary>
    public sealed class PendingGrant
    {
        public string GrantKey;
        public IReadOnlyList<LootOp> LocalOps;
        public string LocalOpsHash;
        public bool Applied;
        public bool Verified;
    }

    /// <summary>
    /// The single-slot per-battle pending-grant cache — verb spec §3.4, mirroring
    /// <c>RecipeStateStore</c>'s single-slot discipline (SPEC-DELTA-v1.1 §6). The engine holds exactly
    /// ONE <c>(GrantKey, PendingGrant)</c> record; a new postfix invocation with a different
    /// <c>GrantKey</c> drops the old record wholesale before doing anything else. No game refs, no
    /// static state — two stores in one process are fully independent (mirrors
    /// <c>RecipeStateStore</c>'s own determinism-pair guarantee).
    /// </summary>
    public sealed class PendingGrantStore
    {
        private PendingGrant _current;
        private LootGrantPayload _inbox; // capacity-1, overwritten by any newer payload

        public PendingGrant Current { get { return _current; } }

        /// <summary>
        /// Arms the record for one combat's locally computed delta — called once, in the postfix, right
        /// after <see cref="LootDeltaComputer.Compute"/>. Apply-at-most-once: a repeat call with the SAME
        /// <c>GrantKey</c> is a no-op on the record (it does not reset <see cref="PendingGrant.Verified"/>).
        /// A DIFFERENT <c>GrantKey</c> drops the old record and allocates a fresh one, then immediately
        /// consults the single-slot inbox in case a payload for this key arrived early.
        /// </summary>
        public PendingGrant Arm(string grantKey, IReadOnlyList<LootOp> localOps, string localOpsHash)
        {
            grantKey = grantKey ?? string.Empty;
            if (_current == null || !string.Equals(_current.GrantKey, grantKey, StringComparison.Ordinal))
            {
                _current = new PendingGrant
                {
                    GrantKey = grantKey,
                    LocalOps = localOps,
                    LocalOpsHash = localOpsHash,
                    Applied = false,
                    Verified = false
                };
                if (_inbox != null && string.Equals(_inbox.GrantKey, grantKey, StringComparison.Ordinal))
                {
                    VerifyAgainst(_inbox);
                    _inbox = null;
                }
            }
            _current.Applied = true; // ops are applied synchronously in the postfix, never again (§3.4)
            return _current;
        }

        /// <summary>Decodes and dispatches one received network payload. Never throws.</summary>
        public LootReceiveResult ReceiveRaw(string json)
        {
            LootGrantPayload payload;
            string error;
            if (!LootGrantCodec.TryDecode(json, out payload, out error)) return LootReceiveResult.Malformed;
            return Receive(payload);
        }

        /// <summary>Dispatches one already-decoded payload per the §3.4/§7 rules.</summary>
        public LootReceiveResult Receive(LootGrantPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.GrantKey)) return LootReceiveResult.Malformed;

            List<string> opErrors;
            if (payload.Ops != null && !LootOpValidator.ValidateV1(payload.Ops, out opErrors))
                return LootReceiveResult.Malformed;

            if (_current == null)
            {
                _inbox = payload; // capacity 1 — overwritten by any newer payload (§3.4)
                return LootReceiveResult.HeldInInbox;
            }

            if (!string.Equals(_current.GrantKey, payload.GrantKey, StringComparison.Ordinal))
            {
                // Could be a late-arriving payload for a combat this store never armed either — but if
                // there IS a current record and the keys differ, this is a payload for some OTHER
                // (older, per §3.4 "stale") combat than the one currently armed: discard.
                return LootReceiveResult.Stale;
            }

            if (_current.Verified) return LootReceiveResult.Duplicate;

            VerifyAgainst(payload);
            return _current.Verified ? LootReceiveResult.Verified : LootReceiveResult.Mismatch;
        }

        private void VerifyAgainst(LootGrantPayload payload)
        {
            if (_current == null) return;
            bool match;
            if (payload.IsDigestOnly)
            {
                // Digest-only degrade (§3.1): no Ops to recompute from, compare hashes only.
                match = string.Equals(_current.LocalOpsHash, payload.OpsHash, StringComparison.Ordinal);
            }
            else
            {
                string remoteRecomputed = LootGrantCodec.ComputeOpsHash(payload.Ops);
                match = string.Equals(_current.LocalOpsHash, payload.OpsHash, StringComparison.Ordinal)
                     && string.Equals(_current.LocalOpsHash, remoteRecomputed, StringComparison.Ordinal);
            }
            _current.Verified = match;
        }

        /// <summary>Explicit reset (new combat/session boundary). Redundant with <see cref="Arm"/>'s own
        /// key check by design, matching <c>RecipeStateStore.ResetCombat</c>'s posture.</summary>
        public void Reset()
        {
            _current = null;
            _inbox = null;
        }
    }
}
