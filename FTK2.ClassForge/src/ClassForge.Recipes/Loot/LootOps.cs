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
