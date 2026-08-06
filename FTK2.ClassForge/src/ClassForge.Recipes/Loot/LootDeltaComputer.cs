using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Loot
{
    /// <summary>
    /// The grant-stream computation — verb spec §4.2-§4.4, §6.1. Evaluates every live
    /// <c>ON_COMBAT_LOOT</c> recipe held by each owner, in the fixed iteration order the spec mandates,
    /// and returns the ordered <c>Ops[]</c> delta. Pure; never mutates a pending list itself (that is
    /// <see cref="LootOpApplier"/>'s job) and never throws.
    /// </summary>
    public static class LootDeltaComputer
    {
        /// <summary>
        /// Computes one combat's loot-grant delta.
        /// <para>
        /// <b>GATE A structural invariant — read before calling.</b> <paramref name="grantRandom"/> is
        /// the ONLY random-shaped parameter this method accepts, and it MUST be the private per-combat
        /// grant stream derived per <see cref="LootGrantKey.ComputeGrantSeed"/> (a freshly constructed
        /// <c>GameRandom(grantSeed, pIgnoreMultiplayerStaticSeed: true)</c> on the game side) —
        /// <b>NEVER</b> <c>Env.GameRun.CombatState.Random</c> or any other shared stream. There is no
        /// second RNG-shaped parameter on this signature for a caller to reach for by mistake, this
        /// method touches no ambient/static state, and it is otherwise pure-C# with no game references —
        /// the zero-shared-draw invariant is therefore enforced by this signature's SHAPE, not by a
        /// runtime assertion. (The spec's §4.1 draft proposed a runtime "assert NextCount unchanged"
        /// mechanism; that is DROPPED per the verification wave's GATE A — <c>GameRandom.NextCount</c>
        /// never increments in the shipped game, so the assertion could not have worked. The offline
        /// harness instead enforces draw-count discipline via golden draw-sequence tests against this
        /// exact API.)
        /// </para>
        /// </summary>
        /// <param name="recipes">The loaded, validated recipe book. Only live <c>ON_COMBAT_LOOT</c>
        /// recipes held by an owner are evaluated; everything else is ignored.</param>
        /// <param name="owners">Alive players in the postfix's <c>pParty</c> — re-sorted ascending
        /// ordinal <c>Guid</c> internally regardless of input order (verb spec §4.3 item 1).</param>
        /// <param name="grantKey">This combat's <see cref="LootGrantKey.ComputeGrantKey"/> output —
        /// needed only to mint deterministic <c>ADD_ITEM</c> Thing ids (§3.3).</param>
        /// <param name="grantRandom">The private grant stream. See the GATE A note above.</param>
        /// <param name="candidateSource">The <c>ITEM_TAG_GRANT</c> candidate seam (GATE B). May be
        /// null — any <c>ITEM_TAG_GRANT</c> effect then draws nothing and emits no op.</param>
        public static IReadOnlyList<LootOp> Compute(
            RecipeSet recipes,
            IReadOnlyList<ICombatEntity> owners,
            string grantKey,
            IRandomSource grantRandom,
            IItemCandidateSource candidateSource)
        {
            List<LootOp> ops = new List<LootOp>();
            if (recipes == null || owners == null || owners.Count == 0 || grantRandom == null) return ops;

            List<ICombatEntity> sortedOwners = new List<ICombatEntity>(owners);
            sortedOwners.Sort((a, b) => string.CompareOrdinal(a == null ? "" : a.Guid, b == null ? "" : b.Guid));

            IReadOnlyList<SkillRecipe> orderedRecipes = recipes.Ordered; // already (Priority, ordinal id)

            for (int oi = 0; oi < sortedOwners.Count; oi++) // owners: ascending ordinal Guid (§4.3 item 1)
            {
                ICombatEntity owner = sortedOwners[oi];
                if (owner == null) continue;
                for (int ri = 0; ri < orderedRecipes.Count; ri++) // recipes: ascending (Priority, ordinal id)
                {
                    SkillRecipe r = orderedRecipes[ri];
                    if (r.Trigger != TriggerKind.ON_COMBAT_LOOT) continue;
                    if (!r.IsLive) continue; // Enabled + validator gate
                    if (!Holds(owner, r.Id)) continue;
                    EvaluateRecipe(r, owner, grantKey, grantRandom, candidateSource, ops);
                }
            }
            return ops;
        }

        private static bool Holds(ICombatEntity e, string recipeId)
        {
            IReadOnlyList<string> p = e.Passives;
            if (p == null) return false;
            for (int i = 0; i < p.Count; i++)
                if (string.Equals(p[i], recipeId, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Evaluation order per recipe — §4.3 item 2: <c>Enabled → trigger match → Conditions →
        /// proc roll → (PickOneEffect roll) → per-effect draws</c>. <c>ProcChance == 100</c> takes ZERO
        /// draws. A failed proc takes no further draws for this recipe.</summary>
        private static void EvaluateRecipe(SkillRecipe r, ICombatEntity owner, string grantKey,
            IRandomSource grantRandom, IItemCandidateSource candidateSource, List<LootOp> ops)
        {
            // Conditions: restricted to HP_THRESHOLD / CHARACTER_TYPE (Of:SELF) / Negate by the
            // validator (§6.1) — neither reads TriggerContext.Ctx or .Runtime, so a minimal context
            // with only Owner bound is sufficient and reuses the shared ConditionEvaluator verbatim.
            TriggerContext t = new TriggerContext { Owner = owner, Trigger = TriggerKind.ON_COMBAT_LOOT };
            if (!ConditionEvaluator.EvaluateAll(r.Conditions, t, null)) return;

            // AiProcChance is ignored — owners are players (§6.1).
            int chance = r.ProcChance;
            bool proc = chance >= 100 || grantRandom.NextChance(chance / 100m);
            if (!proc) return;

            if (r.PickOneEffect && r.Effects.Count > 0)
            {
                int idx = grantRandom.NextInt(0, r.Effects.Count);
                if (idx < 0) idx = 0;
                if (idx >= r.Effects.Count) idx = r.Effects.Count - 1;
                EmitEffect(r, r.Effects[idx], owner, grantKey, grantRandom, candidateSource, ops);
            }
            else
            {
                for (int i = 0; i < r.Effects.Count; i++)
                    EmitEffect(r, r.Effects[i], owner, grantKey, grantRandom, candidateSource, ops);
            }
        }

        private static void EmitEffect(SkillRecipe r, RecipeEffect e, ICombatEntity owner, string grantKey,
            IRandomSource grantRandom, IItemCandidateSource candidateSource, List<LootOp> ops)
        {
            string source = (r.Id ?? "") + "|" + (owner.Guid ?? "");
            switch (e.Type)
            {
                case EffectKind.GOLD_GRANT:
                {
                    int min = e.MinGold.HasValue ? e.MinGold.Value : 0;
                    int max = e.MaxGold.HasValue ? e.MaxGold.Value : min;
                    int amount = min == max ? min : grantRandom.NextInt(min, max + 1); // inclusive; == -> zero draws
                    if (amount <= 0) return;
                    ops.Add(new LootOp { Kind = LootOpKind.ADD_GOLD, Amount = amount, Source = source });
                    return;
                }
                case EffectKind.ITEM_TAG_GRANT:
                {
                    IReadOnlyList<string> raw = candidateSource != null ? candidateSource.GetCandidates(e.Tag, e.Rarity) : null;
                    if (raw == null || raw.Count == 0) return; // no candidates: no draw, no op
                    List<string> sorted = new List<string>(raw);
                    sorted.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
                    int idx = grantRandom.NextInt(0, sorted.Count);
                    if (idx < 0) idx = 0;
                    if (idx >= sorted.Count) idx = sorted.Count - 1;
                    int opIndex = ops.Count; // this op's own eventual position in Ops[] (§3.3)
                    ops.Add(new LootOp
                    {
                        Kind = LootOpKind.ADD_ITEM,
                        ConfigName = sorted[idx],
                        Stack = e.Stack > 0 ? e.Stack : 1,
                        ThingId = LootThingId.Mint(grantKey, opIndex),
                        Source = source
                    });
                    return;
                }
                case EffectKind.LOOT_SCALE:
                {
                    // Zero draws (§3.2).
                    ops.Add(new LootOp
                    {
                        Kind = LootOpKind.SCALE_STACK,
                        ConfigName = e.ConfigName,
                        Percent = e.Percent.HasValue ? e.Percent.Value : 0,
                        Source = source
                    });
                    return;
                }
                case EffectKind.AFFIX_ROLL:
                    // Reserved (M-LG4). RecipeValidator disables any recipe carrying this effect
                    // (E_LOOT_RESERVED), so this branch is unreachable via r.IsLive filtering in Compute.
                    return;
                default:
                    return; // not a grant effect; RecipeValidator already rejects this authoring shape
            }
        }
    }
}
