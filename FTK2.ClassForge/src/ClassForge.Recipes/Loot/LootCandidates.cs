using System.Collections.Generic;

namespace ClassForge.Recipes.Loot
{
    /// <summary>
    /// Per-<c>ITEM_TAG_GRANT</c>-evaluation candidate config-name source — verb spec OQ-2, resolved by
    /// the verification wave's GATE B. This pure-C# core has no game references and cannot filter
    /// <c>Env.Configs.Things</c> itself; the caller (the M-LG2 Plugin unit) supplies an
    /// ALREADY-FILTERED candidate list for the given <c>(Tag, Rarity)</c> pair.
    /// <para>
    /// <b>M-LG2 plugin-side filter contract</b> (recorded here so it ships with the seam, not enforced
    /// by this interface — the core cannot verify it): a candidate config name is eligible iff
    /// <c>Tags</c> contains <c>tag</c> (OrdinalIgnoreCase) AND (<c>rarity</c> is null OR an exact match)
    /// AND NOT <c>Hidden</c> AND <c>Value != 0</c> AND rarity weight != 0 AND the owning expansion is
    /// enabled. This is a DELIBERATE, RECORDED DEVIATION from EOR, which filtered tag+rarity only
    /// (<c>TryAddTraitTaggedReward</c>) — the extra predicates keep loot-grant items limited to the
    /// same droppable/valuable/enabled pool the rest of the game's loot pipeline already respects.
    /// </para>
    /// <para>
    /// The core's own responsibility, applied uniformly to whatever this seam returns
    /// (<see cref="LootDeltaComputer"/>): sort candidates ordinal-IGNORE-CASE by <c>ConfigName</c>, then
    /// take exactly one draw semantically equal to <c>NextInt(0, count-1, maxInclusive)</c> from the
    /// grant stream (verb spec §4.3 item 3).
    /// </para>
    /// </summary>
    public interface IItemCandidateSource
    {
        /// <summary>Already-filtered candidate <c>ConfigName</c>s for <paramref name="tag"/> (and,
        /// when non-null, exact <paramref name="rarity"/>). Order is irrelevant — the computer re-sorts.
        /// Empty/null means "no eligible candidates"; the effect then emits no op and takes no draw.</summary>
        IReadOnlyList<string> GetCandidates(string tag, string rarity);
    }
}
