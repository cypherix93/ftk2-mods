using System.Collections.Generic;

namespace ClassForge.Recipes.Abstractions
{
    /// <summary>
    /// The four ordering keys of one board tile, in the order they are compared. Game-agnostic on
    /// purpose: the Plugin fills these from <c>VenueComponent.TilePosition</c> /
    /// <c>VenueTileComponent.RowPositionsType</c>, and the test suite fills them by hand.
    /// </summary>
    public struct TileKey
    {
        /// <summary>Venue-map ROW index — <c>VenueComponent.TilePosition.y</c>. Primary key.</summary>
        public int Y;
        /// <summary>Venue-map COLUMN index — <c>VenueComponent.TilePosition.x</c>. Secondary key.</summary>
        public int X;
        /// <summary><c>(int)VenueTileComponent.RowPositionsType</c>. First tiebreak, and the key
        /// <see cref="TileOrder.SelectPlacement"/> uses to prefer a front-row square.</summary>
        public int Row;
        /// <summary>A peer-identical NAME for the tile, ordinal-compared as the last tiebreak. May be
        /// null — real venue tile entities carry no name (see <see cref="TileOrder"/>).</summary>
        public string Name;

        public TileKey(int y, int x, int row, string name)
        {
            Y = y; X = x; Row = row; Name = name;
        }
    }

    /// <summary>
    /// The ONE total order over board tiles, shared by every site that has to turn a set of tiles into a
    /// sequence.
    ///
    /// <para><b>Why a total order is the whole point.</b> <c>AIHelper.ForceAiDecision</c> — the funnel every
    /// AI path goes through — does <c>list = VenueHelper.GetTargetableTiles(...)</c> and then
    /// <c>pGameRun.CombatState.Random.ShuffleList(list)</c> (<c>AIHelper.cs:507-511</c>), and
    /// <c>GameRandom.ShuffleList</c> takes exactly <c>_list.Count</c> draws off the SHARED combat stream
    /// (<c>GameRandom.cs:227-241</c>). One extra or differently-placed combatant therefore shifts the RNG
    /// stream for both peers from the very next AI turn onward. Roster stability and tile stability ARE
    /// draw-count stability.</para>
    ///
    /// <para><b>What was actually unordered.</b> <c>VenueViewHelper.LoadCharacterEntitiesToVenueGrid</c>
    /// copies the tile list it is handed and shuffles the copy (<c>VenueViewHelper.cs:460-461</c>). The
    /// shuffle is seeded and therefore applies the SAME PERMUTATION on both peers — but a permutation
    /// applied to two different input orders yields two different outputs. Callers that built that input
    /// from <c>Dictionary&lt;Entity, …&gt;.Keys</c> were handing it a reference-hash enumeration order,
    /// which is process-dependent because <c>Entity</c> does not override <c>GetHashCode</c>. The RNG was
    /// innocent; the input order was the bug.</para>
    ///
    /// <para><b>Key order.</b> Ascending <c>(Y, X)</c> — the venue map's own reading order, a pure function
    /// of a compiled-in <c>string[]</c> (<c>VenueHelper.CreateVenueTileEntities</c> emits
    /// <c>CreateVenueTileEntity((column, row), …)</c> at <c>VenueHelper.cs:75/79</c>) and therefore
    /// byte-identical on every peer. <c>Row</c> then <c>Name</c> follow so the order is TOTAL for any input,
    /// including the synthetic boards the property test builds. On a real venue grid <c>(Y, X)</c> is unique
    /// per tile, so the two tiebreaks are unreachable there — and <c>Name</c> is always null, because
    /// <c>VenueTileComponent</c> carries only <c>GroupIndex</c>, <c>RowPositionsType</c> and
    /// <c>AuraStatuses</c>: a tile entity has no <c>ConfigName</c>.</para>
    ///
    /// <para><b>Never the guid.</b> <c>ICombatTile.LocalGuid</c> is LOCAL identity and must never enter a
    /// cross-peer comparison, so the <see cref="ICombatTile"/> overload compares <c>(Y, X)</c> only.</para>
    /// </summary>
    public static class TileOrder
    {
        /// <summary>Ascending <c>(Y, X)</c>, then <c>Row</c>, then <c>Name</c> ordinal. A strict total order:
        /// it returns 0 only for two keys that are equal in all four fields.</summary>
        public static int Compare(TileKey a, TileKey b)
        {
            if (a.Y != b.Y) return a.Y < b.Y ? -1 : 1;
            if (a.X != b.X) return a.X < b.X ? -1 : 1;
            if (a.Row != b.Row) return a.Row < b.Row ? -1 : 1;
            return string.CompareOrdinal(a.Name ?? "", b.Name ?? "");
        }

        /// <summary>Ascending <c>(Y, X)</c> for the engine's tile abstraction. <c>LocalGuid</c> is deliberately
        /// NOT a key — see the type docs.</summary>
        public static int Compare(ICombatTile a, ICombatTile b)
        {
            return Compare(new TileKey(a.Y, a.X, 0, null), new TileKey(b.Y, b.X, 0, null));
        }

        /// <summary>Sorts in place into the canonical order. <c>List.Sort</c> is unstable, which is exactly
        /// why <see cref="Compare"/> has to be total: with a total order, instability cannot show.</summary>
        public static void Sort(List<TileKey> tiles)
        {
            if (tiles != null && tiles.Count > 1) tiles.Sort(Compare);
        }

        /// <summary>
        /// Index into <paramref name="candidates"/> of the tile a summon should be placed on: the
        /// canonically-first tile whose <c>Row</c> equals <paramref name="frontRow"/>, or — when no candidate
        /// is a front-row tile — the canonically-first tile overall. <c>-1</c> for an empty or null list.
        ///
        /// <para>Written as a single linear MIN scan rather than sort-then-index so the result is provably
        /// independent of the order the candidates were collected in: the caller's collection order is itself
        /// an invariant other fixes exist to protect, and it must not ALSO be load-bearing here.</para>
        /// </summary>
        public static int SelectPlacement(IList<TileKey> candidates, int frontRow)
        {
            if (candidates == null || candidates.Count == 0) return -1;
            int best = 0;
            bool bestIsFront = candidates[0].Row == frontRow;
            for (int i = 1; i < candidates.Count; i++)
            {
                bool isFront = candidates[i].Row == frontRow;
                if (isFront != bestIsFront)
                {
                    if (isFront) { best = i; bestIsFront = true; }
                    continue;
                }
                if (Compare(candidates[i], candidates[best]) < 0) best = i;
            }
            return best;
        }
    }
}
