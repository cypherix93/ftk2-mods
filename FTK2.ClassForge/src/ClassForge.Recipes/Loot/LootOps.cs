using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClassForge.Recipes.Loot
{
    /// <summary>Closed delta-op vocabulary, v1 — verb spec §3.2.</summary>
    public enum LootOpKind
    {
        ADD_GOLD,
        ADD_ITEM,
        SCALE_STACK,
        /// <summary>Schema-reserved: parses, but <see cref="LootOpValidator"/> always rejects it in v1
        /// (ships with CM §3 row 22 at M-LG4).</summary>
        REPLACE_ITEM
    }

    /// <summary>
    /// One entry of the wire payload's <c>Ops[]</c> — verb spec §3.1/§3.2. <see cref="Source"/> is
    /// diagnostic only (recipe id + owner guid); it is never read by <see cref="LootOpApplier"/>.
    /// </summary>
    public sealed class LootOp
    {
        public LootOpKind Kind;

        // ADD_GOLD
        public int Amount;

        // ADD_ITEM / SCALE_STACK / REPLACE_ITEM(existing target)
        public string ConfigName;

        // ADD_ITEM
        public int Stack = 1;
        /// <summary>ADD_ITEM: the minted id (§3.3). REPLACE_ITEM: the id of the existing Thing to swap.</summary>
        public string ThingId;

        // SCALE_STACK
        public int Percent;

        // REPLACE_ITEM
        public string NewConfigName;

        /// <summary>Diagnostic only — recipe id + owner guid (e.g. <c>"SKILL_CF_TRAIT_SCAVENGER|&lt;guid&gt;"</c>).
        /// Never consulted by application logic; exists for mismatch-banner/offline-harness attribution.</summary>
        public string Source;

        /// <summary>
        /// Canonical <c>"Op|field=value|..."</c> row for <see cref="LootGrantCodec.ComputeOpsHash"/>
        /// (verb spec §3.1 <c>OpsHash</c>) — field order matches the wire-schema examples exactly, so a
        /// hand-computed cross-check hashes identically.
        /// </summary>
        public string ToCanonicalRow()
        {
            switch (Kind)
            {
                case LootOpKind.ADD_GOLD:
                    return "ADD_GOLD|Amount=" + I(Amount) + "|Source=" + S(Source);
                case LootOpKind.ADD_ITEM:
                    return "ADD_ITEM|ConfigName=" + S(ConfigName) + "|Stack=" + I(Stack) +
                           "|ThingId=" + S(ThingId) + "|Source=" + S(Source);
                case LootOpKind.SCALE_STACK:
                    return "SCALE_STACK|ConfigName=" + S(ConfigName) + "|Percent=" + I(Percent) + "|Source=" + S(Source);
                case LootOpKind.REPLACE_ITEM:
                    return "REPLACE_ITEM|ThingId=" + S(ThingId) + "|NewConfigName=" + S(NewConfigName) + "|Source=" + S(Source);
                default:
                    return "UNKNOWN";
            }
        }

        private static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }
        private static string S(string v) { return v ?? string.Empty; }
    }

    /// <summary>
    /// The abstract pending-list model (verb spec §3.2/§3.3) that <see cref="LootOpApplier"/> mutates.
    /// Deliberately narrow — no game refs — so a M-LG2 Plugin adapts a real <c>List&lt;Thing&gt;</c>
    /// onto/from this shape around one <see cref="LootOpApplier.Apply"/> call.
    /// </summary>
    public sealed class PendingThing : ILootThingSnapshot
    {
        public string Id;
        public string ConfigName;
        public int Stack;

        string ILootThingSnapshot.ConfigName { get { return ConfigName; } }
        int ILootThingSnapshot.Stack { get { return Stack; } }
    }

    /// <summary>
    /// Encounter Modifiers spec §11 reward-half interface, as consumed by the loot-grant verb (spec §6.3
    /// item 6, M-EM4). Read-only input to <see cref="LootDeltaComputer.Compute"/>: the CURRENT combat's
    /// active modifier's <c>Rewards</c> block, already resolved by the Plugin (which is the one layer that
    /// can see both <c>CombatRuntime.Selections</c> and the pack's <c>ModifierTable</c> registry — this
    /// pure-C# core has neither). All three fields are 0 when absent — a pure "no bonus of this kind"
    /// value, so a caller can build this unconditionally from a nullable-int source without translating
    /// null to a sentinel.
    /// <para>
    /// <b>Recorded delta vs. the loot-grant verb spec's own sketch (§6.3 consumer-6 row).</b> That spec
    /// anticipated Mode H (host-authoritative push, zero client computation) for encounter-modifier
    /// rewards specifically because it assumed the modifier assignment would be host-private, like Nemesis
    /// state — "Nemesis/encounter modifier assignments... where deterministic mirroring is impossible."
    /// The Encounter Modifiers spec's actual design (§4.2/§4.5/§6.3) is different: the selection is a
    /// <c>[SYNCED]</c> draw off <c>CombatState.Random</c> that every peer executing the path computes
    /// identically, and a JIP/mid-combat-load peer re-derives the SAME selection from replicated statuses
    /// (§4.5 reconstruction) rather than needing it pushed. The active modifier — and therefore its
    /// Rewards — is symmetric and mirrored on every peer, exactly like the four trait-loot consumers
    /// (1-4) this same computer already serves in Mode M. No host-private state is involved, so Mode M
    /// suffices; this is a recorded correction of the loot-grant verb spec's anticipatory sketch, not a
    /// new sync surface, a Mode-H implementation, or a deviation from this verb's shipped MP posture.
    /// </para>
    /// </summary>
    public sealed class ModifierRewardsInput
    {
        public int XpBonusPercent;
        public int GoldBonusPercent;
        public int ExtraLootChancePercent;
    }

    /// <summary>Shared vocabulary/constants for op validation and application — kept in ONE place
    /// (SPEC-DELTA-v1.1 §9 risk-5 discipline), consulted by both <see cref="Parsing.RecipeValidator"/>
    /// (authoring-time) and <see cref="LootOpApplier"/> (application-time, defense in depth).</summary>
    public static class LootVocabulary
    {
        /// <summary><c>SCALE_STACK.ConfigName</c> whitelist — verb spec §3.2. Exactly the currency
        /// <c>Thing</c>s the vanilla loot aggregator creates (LootDropHelper.cs:1267-1278).</summary>
        public static readonly IReadOnlyList<string> ScaleStackConfigNames = new[]
        {
            "PARTY_XP", "XP", "CURRENCY_ADVENTURE", "CURRENCY_LORE"
        };

        public static bool IsScaleStackConfigName(string configName)
        {
            for (int i = 0; i < ScaleStackConfigNames.Count; i++)
                if (string.Equals(ScaleStackConfigNames[i], configName, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// Applies a computed/received <c>Ops[]</c> delta to the abstract pending-list model — verb spec
    /// §3.2. Pure, no RNG, no game refs. Never throws (fail-safe posture, docs/CONVENTIONS.md):
    /// malformed/out-of-whitelist ops are silently no-ops, matching the spec's own "no-op if absent"
    /// language for <c>SCALE_STACK</c>.
    /// </summary>
    public static class LootOpApplier
    {
        /// <param name="pending">The mutable pending list — mutated in place, append-only for additive ops.</param>
        /// <param name="ops">Authored order == computation order == application order (verb spec §3.1).</param>
        /// <param name="grantKey">Needed only for <c>ADD_GOLD</c>'s conditional new-Thing case, whose id is
        /// never carried on the wire and so is re-derived here with the SAME formula (§3.3) at the op's own
        /// index within <paramref name="ops"/>.</param>
        public static void Apply(List<PendingThing> pending, IReadOnlyList<LootOp> ops, string grantKey)
        {
            if (pending == null || ops == null) return;
            for (int i = 0; i < ops.Count; i++)
            {
                LootOp op = ops[i];
                if (op == null) continue;
                switch (op.Kind)
                {
                    case LootOpKind.ADD_GOLD: ApplyAddGold(pending, op, grantKey, i); break;
                    case LootOpKind.ADD_ITEM: ApplyAddItem(pending, op, grantKey, i); break;
                    case LootOpKind.SCALE_STACK: ApplyScaleStack(pending, op); break;
                    case LootOpKind.REPLACE_ITEM: ApplyReplaceItem(pending, op); break;
                }
            }
        }

        private static void ApplyAddGold(List<PendingThing> pending, LootOp op, string grantKey, int opIndex)
        {
            if (op.Amount <= 0) return;
            PendingThing existing = FindByConfig(pending, "CURRENCY_ADVENTURE");
            if (existing != null) { existing.Stack += op.Amount; return; }
            pending.Add(new PendingThing
            {
                Id = LootThingId.Mint(grantKey, opIndex),
                ConfigName = "CURRENCY_ADVENTURE",
                Stack = op.Amount
            });
        }

        private static void ApplyAddItem(List<PendingThing> pending, LootOp op, string grantKey, int opIndex)
        {
            if (string.IsNullOrEmpty(op.ConfigName)) return;
            string id = !string.IsNullOrEmpty(op.ThingId) ? op.ThingId : LootThingId.Mint(grantKey, opIndex);
            pending.Add(new PendingThing
            {
                Id = id,
                ConfigName = op.ConfigName,
                Stack = op.Stack > 0 ? op.Stack : 1
            });
        }

        /// <summary>
        /// Round-AwayFromZero, floor-at-1 (M-LG1). Consumers 1-4's <c>LOOT_SCALE</c> effect and M-EM4's
        /// encounter-modifier <c>XpBonusPercent</c>/<c>GoldBonusPercent</c> reward ops both ride this SAME
        /// application — there is no separate reward-specific rounding path.
        /// <para>
        /// <b>Recorded delta vs. EOR</b> (Encounter Modifiers spec §11 / loot-grant verb spec §6.3 item 6):
        /// EOR's own XP/gold percent-bump used ceiling, floor-at-1 (<c>max(1, ceil(stack × pct/100))</c>,
        /// EOR L16674-16688). This engine's SCALE_STACK already shipped round-AwayFromZero, floor-at-1
        /// (M-LG1, this method, unchanged by M-EM4) for its four existing consumers. Per the task's own
        /// framing, the simpler option is taken deliberately: reuse the shipped semantics and record the
        /// ceil→round-AwayFromZero delta here, rather than adding a per-op rounding-mode field for one
        /// digit of EOR-parity that a stat-panel round-trip already renders moot (both floor at 1, and
        /// round-AwayFromZero only differs from ceiling on exact <c>.5</c> boundaries rounding down instead
        /// of up — a one-unit difference, at most, on values already estimating a percent bonus).
        /// </para>
        /// </summary>
        private static void ApplyScaleStack(List<PendingThing> pending, LootOp op)
        {
            if (string.IsNullOrEmpty(op.ConfigName)) return;
            if (!LootVocabulary.IsScaleStackConfigName(op.ConfigName)) return; // whitelist — no-op outside it
            PendingThing existing = FindByConfig(pending, op.ConfigName);
            if (existing == null) return; // no-op if absent (verb spec §3.2)
            double factor = (100 + op.Percent) / 100.0;
            int scaled = (int)Math.Round(existing.Stack * factor, MidpointRounding.AwayFromZero);
            existing.Stack = scaled < 1 ? 1 : scaled;
        }

        private static void ApplyReplaceItem(List<PendingThing> pending, LootOp op)
        {
            // Reserved (M-LG4). Implemented for completeness of the abstract application model (§3.2's
            // REPLACE_ITEM row); LootOpValidator.ValidateV1 is what actually keeps it out of v1 traffic.
            if (string.IsNullOrEmpty(op.ThingId)) return;
            PendingThing existing = FindById(pending, op.ThingId);
            if (existing == null) return;
            existing.ConfigName = op.NewConfigName; // id and stack preserved, per §3.2
        }

        private static PendingThing FindByConfig(List<PendingThing> pending, string configName)
        {
            for (int i = 0; i < pending.Count; i++)
                if (string.Equals(pending[i].ConfigName, configName, StringComparison.Ordinal)) return pending[i];
            return null;
        }

        private static PendingThing FindById(List<PendingThing> pending, string id)
        {
            for (int i = 0; i < pending.Count; i++)
                if (string.Equals(pending[i].Id, id, StringComparison.Ordinal)) return pending[i];
            return null;
        }
    }

    /// <summary>v1 op-vocabulary gate — verb spec §8.1 item 6. Separate from
    /// <see cref="Parsing.RecipeValidator"/> (which gates AUTHORED recipe effects): this gates the
    /// Ops[] themselves, defense in depth against a malformed/future payload carrying a reserved op
    /// even though no v1 recipe effect can emit one.</summary>
    public static class LootOpValidator
    {
        public static bool IsAllowedInV1(LootOpKind kind)
        {
            return kind != LootOpKind.REPLACE_ITEM;
        }

        /// <summary>True iff every op is v1-allowed; <paramref name="errors"/> is always non-null.</summary>
        public static bool ValidateV1(IReadOnlyList<LootOp> ops, out List<string> errors)
        {
            errors = new List<string>();
            if (ops == null) return true;
            for (int i = 0; i < ops.Count; i++)
            {
                if (ops[i] != null && !IsAllowedInV1(ops[i].Kind))
                    errors.Add("Ops[" + i.ToString(CultureInfo.InvariantCulture) + "]: " + ops[i].Kind +
                               " is reserved and rejected by the v1 validator (M-LG4)");
            }
            return errors.Count == 0;
        }
    }
}
