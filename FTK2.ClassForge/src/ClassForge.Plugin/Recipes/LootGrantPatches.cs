using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Loot;

namespace ClassForge.Plugin
{
    /// <summary>
    /// M-LG2 — the game-side wiring for the loot-grant sync verb
    /// (docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md, as resolved by the verification wave's
    /// GATE A/GATE B: <c>DrawMark</c> -&gt; <c>ListDigest</c>, candidate-source seam). One hook:
    /// <c>LootDropHelper.GetLootDropsFromEnemies</c> postfix (§1.1/§5) — computes and applies the delta
    /// identically whether single-player or online (transport/host-push is M-LG3; nothing in this unit
    /// sends or receives a network payload). Ships DARK behind <c>[Skills] EnableLootGrants = false</c>
    /// (charter rule 3's disabled-by-default carrier while unproven).
    /// </summary>
    public static class LootGrantPatches
    {
        /// <summary>
        /// Single-slot per-combat pending-grant record (verb spec §3.4) — verification bookkeeping consumed
        /// by M-LG3 (host send / mismatch detection). M-LG2 only arms it; nothing here ever reads it back.
        /// </summary>
        internal static readonly PendingGrantStore Store = new PendingGrantStore();

        private static bool _warnedOpValidation;

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
                IReadOnlyList<LootOp> ops = LootDeltaComputer.Compute(RecipeEngineHost.Book, owners, grantKey, grantRandom, candidateSource);

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

                // ---- Step 6: record for M-LG3 (host send / mismatch verification) ----
                Store.Arm(grantKey, ops, opsHash);

                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                {
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] Loot grant: GrantKey=" + grantKey + " ops=" + ops.Count + " opsHash=" + opsHash);
                }
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
