using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClassForge.Recipes.Abstractions;

namespace ClassForge.Recipes.Tests
{
    /// <summary>
    /// The tile-permutation property suite for <see cref="TileOrder"/>.
    ///
    /// <para><b>What was missing.</b> The suite already had the ordering CONTRACT written down —
    /// <c>Fakes.AddTile</c> says "callers add them in (Y,X) ascending order, which is the ordering the real
    /// adapter guarantees", and <c>RandomTileTests.WithBoard</c> dutifully does exactly that. Every existing
    /// tile test therefore ASSUMES the contract it is meant to be protecting. Nothing anywhere fed the
    /// ordering code a board in the WRONG order and demanded the same answer back.</para>
    ///
    /// <para><b>Why that gap mattered.</b> Two real call sites built their tile list from a
    /// <c>Dictionary&lt;Entity, …&gt;</c> whose enumeration order is reference-hash order — process-dependent,
    /// because <c>Entity</c> does not override <c>GetHashCode</c>. Two peers running the same seed and the
    /// same shuffle permutation over two different input orders land the summon on two different squares;
    /// from there the roster differs, so <c>GetTargetableTiles</c>'s Count differs, so
    /// <c>GameRandom.ShuffleList</c> takes a different number of draws off the SHARED combat stream on the
    /// next AI turn. A single misplaced tile is a desync.</para>
    ///
    /// <para>So these cases attack the ordering from the input side: every permutation of a small board
    /// (exhaustive for N &lt;= 6) and a thousand seeded shuffles of a larger one, each asserted to collapse
    /// to exactly ONE result. Modelled on <c>DeterminismTests</c>' "shuffled entity enumeration order
    /// produces the identical plan", which does the same thing one level up.</para>
    /// </summary>
    public static class TileOrderTests
    {
        /// <summary>Stand-in for <c>(int)eTileRowPositions.FRONT</c>. The game enum does not exist in this
        /// assembly by design (ClassForge.Recipes has no game refs), so the front-row value is a parameter
        /// of <see cref="TileOrder.SelectPlacement"/> and the plugin supplies the real one.</summary>
        private const int FrontRow = 2;
        private const int BackRow = 3;

        // ---------------------------------------------------------------- boards

        /// <summary>A board shaped like the real ally side of <c>VenueHelper.VenueMap1</c>: rows 1..h,
        /// a FRONT column and a BACK column, in canonical order.</summary>
        private static List<TileKey> Board(int rows)
        {
            var tiles = new List<TileKey>();
            for (int y = 1; y <= rows; y++)
            {
                tiles.Add(new TileKey(y, 3, BackRow, null));    // 'A' column
                tiles.Add(new TileKey(y, 4, FrontRow, null));   // 'a' column
            }
            return tiles;
        }

        /// <summary>A degenerate board that ties on (Y,X) so the Row and Name tiebreaks are actually
        /// exercised — on a real venue grid (Y,X) is unique and they never fire.</summary>
        private static List<TileKey> TiedBoard()
        {
            return new List<TileKey>
            {
                new TileKey(1, 1, BackRow,  "b"),
                new TileKey(1, 1, BackRow,  "a"),
                new TileKey(1, 1, FrontRow, "z"),
                new TileKey(1, 1, FrontRow, "a"),
                new TileKey(1, 2, BackRow,  null),
                new TileKey(0, 9, BackRow,  null),
            };
        }

        // ---------------------------------------------------------------- rendering

        private static string Render(List<TileKey> tiles)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < tiles.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append('(').Append(tiles[i].Y.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(tiles[i].X.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(tiles[i].Row.ToString(CultureInfo.InvariantCulture))
                  .Append(',').Append(tiles[i].Name ?? "-").Append(')');
            }
            return sb.ToString();
        }

        private static string RenderOne(TileKey t)
        {
            return Render(new List<TileKey> { t });
        }

        // ---------------------------------------------------------------- permutation drivers

        /// <summary>Calls <paramref name="body"/> once for EVERY permutation of <paramref name="tiles"/>.
        /// Heap's algorithm; N! grows fast, so callers keep N at 6 or below (720 permutations).</summary>
        private static void ForEachPermutation(List<TileKey> tiles, Action<List<TileKey>> body)
        {
            var work = new List<TileKey>(tiles);
            Permute(work, work.Count, body);
        }

        private static void Permute(List<TileKey> a, int n, Action<List<TileKey>> body)
        {
            if (n <= 1) { body(new List<TileKey>(a)); return; }
            for (int i = 0; i < n; i++)
            {
                Permute(a, n - 1, body);
                int j = (n % 2 == 0) ? i : 0;
                var t = a[j]; a[j] = a[n - 1]; a[n - 1] = t;
            }
        }

        /// <summary>Deterministic xorshift shuffle, so a failure is reproducible from the seed alone. The
        /// same generator shape <c>FakeRandom</c> uses.</summary>
        private static List<TileKey> Shuffled(List<TileKey> tiles, uint seed)
        {
            uint state = seed == 0 ? 0x9E3779B9u : seed;
            var a = new List<TileKey>(tiles);
            for (int i = a.Count - 1; i > 0; i--)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                int j = (int)(state % (uint)(i + 1));
                var t = a[i]; a[i] = a[j]; a[j] = t;
            }
            return a;
        }

        // ---------------------------------------------------------------- cases

        public static void Run(TestRunner t)
        {
            t.Section("tile order — the permutation property (Fix 4)");

            t.Case("TileOrder.Sort: every permutation of a 6-tile board yields ONE ordering", () =>
            {
                var board = Board(3);                 // 6 tiles => 720 permutations, exhaustive
                Check.Eq(6, board.Count, "the board is the size the exhaustive sweep expects");

                string canonical = null;
                int seen = 0;
                ForEachPermutation(board, perm =>
                {
                    TileOrder.Sort(perm);
                    string rendered = Render(perm);
                    if (canonical == null) canonical = rendered;
                    else Check.Eq(canonical, rendered, "every input permutation must sort to one board");
                    seen++;
                });

                Check.Eq(720, seen, "all 6! permutations were actually visited");
                Check.Eq("(1,3,3,-) (1,4,2,-) (2,3,3,-) (2,4,2,-) (3,3,3,-) (3,4,2,-)", canonical,
                    "and that one board is ascending (Y,X) — the venue map's reading order");
            });

            t.Case("TileOrder.Sort: 1..6 tiles, exhaustive over every permutation", () =>
            {
                for (int rows = 1; rows <= 3; rows++)
                {
                    var board = Board(rows);
                    string canonical = null;
                    ForEachPermutation(board, perm =>
                    {
                        TileOrder.Sort(perm);
                        string rendered = Render(perm);
                        if (canonical == null) canonical = rendered;
                        else Check.Eq(canonical, rendered,
                            "board of " + board.Count.ToString(CultureInfo.InvariantCulture) + " tiles");
                    });
                    Check.True(canonical != null, "a board of "
                        + board.Count.ToString(CultureInfo.InvariantCulture) + " tiles produced a result");
                }
            });

            t.Case("TileOrder.Sort: 1000 seeded shuffles of a 16-tile board yield ONE ordering", () =>
            {
                var board = Board(8);                 // 16 tiles — far past exhaustive reach
                Check.Eq(16, board.Count, "the large board is 16 tiles");

                string canonical = Render(board);     // already canonical by construction
                for (uint seed = 1; seed <= 1000; seed++)
                {
                    var perm = Shuffled(board, seed);
                    TileOrder.Sort(perm);
                    Check.Eq(canonical, Render(perm),
                        "shuffle seed " + seed.ToString(CultureInfo.InvariantCulture) + " must sort the same");
                }
            });

            t.Case("SelectPlacement: every permutation of a 6-tile board picks the SAME square", () =>
            {
                var board = Board(3);
                string picked = null;
                int seen = 0;
                ForEachPermutation(board, perm =>
                {
                    int i = TileOrder.SelectPlacement(perm, FrontRow);
                    Check.True(i >= 0, "a non-empty candidate list always yields a pick");
                    string rendered = RenderOne(perm[i]);
                    if (picked == null) picked = rendered;
                    else Check.Eq(picked, rendered, "the placement must not depend on collection order");
                    seen++;
                });

                Check.Eq(720, seen, "all 6! permutations were actually visited");
                Check.Eq("(1,4,2,-)", picked,
                    "and the pick is the canonically-first FRONT tile, not the first FRONT tile scanned");
            });

            t.Case("SelectPlacement: 1000 seeded shuffles of a 16-tile board pick the SAME square", () =>
            {
                var board = Board(8);
                string picked = null;
                for (uint seed = 1; seed <= 1000; seed++)
                {
                    var perm = Shuffled(board, seed);
                    int i = TileOrder.SelectPlacement(perm, FrontRow);
                    Check.True(i >= 0, "a non-empty candidate list always yields a pick");
                    string rendered = RenderOne(perm[i]);
                    if (picked == null) picked = rendered;
                    else Check.Eq(picked, rendered,
                        "shuffle seed " + seed.ToString(CultureInfo.InvariantCulture) + " must pick the same");
                }
                Check.Eq("(1,4,2,-)", picked, "the canonically-first FRONT tile");
            });

            t.Case("SelectPlacement: with NO front-row candidate, falls back to the canonical first", () =>
            {
                var backOnly = new List<TileKey>
                {
                    new TileKey(4, 3, BackRow, null),
                    new TileKey(2, 3, BackRow, null),
                    new TileKey(2, 1, BackRow, null),
                    new TileKey(3, 9, BackRow, null),
                };
                string picked = null;
                ForEachPermutation(backOnly, perm =>
                {
                    int i = TileOrder.SelectPlacement(perm, FrontRow);
                    string rendered = RenderOne(perm[i]);
                    if (picked == null) picked = rendered;
                    else Check.Eq(picked, rendered, "back-row-only boards are order-independent too");
                });
                Check.Eq("(2,1,3,-)", picked, "lowest (Y,X) wins when nothing is FRONT");
            });

            t.Case("SelectPlacement: FRONT beats a lower (Y,X) back tile", () =>
            {
                var mixed = new List<TileKey>
                {
                    new TileKey(1, 1, BackRow,  null),   // sorts first overall
                    new TileKey(9, 9, FrontRow, null),   // but this one is FRONT
                };
                Check.Eq(1, TileOrder.SelectPlacement(mixed, FrontRow), "front-row preference is the first key");
                mixed.Reverse();
                Check.Eq(0, TileOrder.SelectPlacement(mixed, FrontRow), "…in either input order");
            });

            t.Case("SelectPlacement: empty and null candidate lists yield -1, never a throw", () =>
            {
                Check.Eq(-1, TileOrder.SelectPlacement(new List<TileKey>(), FrontRow), "empty list");
                Check.Eq(-1, TileOrder.SelectPlacement(null, FrontRow), "null list");
            });

            t.Case("Compare: the Row and Name tiebreaks make the order total on a tied board", () =>
            {
                // Real venue grids never tie on (Y,X) — CreateVenueTileEntities emits one entity per map
                // cell — so this board is synthetic on purpose: it is the only way to reach the tail keys
                // and prove List.Sort's instability cannot show through them.
                var tied = TiedBoard();
                string canonical = null;
                ForEachPermutation(tied, perm =>
                {
                    TileOrder.Sort(perm);
                    string rendered = Render(perm);
                    if (canonical == null) canonical = rendered;
                    else Check.Eq(canonical, rendered, "ties must still resolve to one ordering");
                });
                Check.Eq("(0,9,3,-) (1,1,2,a) (1,1,2,z) (1,1,3,a) (1,1,3,b) (1,2,3,-)", canonical,
                    "(Y,X) then Row then Name ordinal");
            });

            t.Case("Compare: antisymmetric, and zero only for fully-equal keys", () =>
            {
                var all = new List<TileKey>(TiedBoard());
                all.AddRange(Board(2));
                for (int i = 0; i < all.Count; i++)
                {
                    Check.Eq(0, TileOrder.Compare(all[i], all[i]), "a key compares equal to itself");
                    for (int j = 0; j < all.Count; j++)
                    {
                        int ab = TileOrder.Compare(all[i], all[j]);
                        int ba = TileOrder.Compare(all[j], all[i]);
                        Check.True(Math.Sign(ab) == -Math.Sign(ba),
                            "Compare is antisymmetric for " + RenderOne(all[i]) + " vs " + RenderOne(all[j]));
                        if (i != j) Check.True(ab != 0, "distinct keys never compare equal");
                    }
                }
            });

            t.Case("Compare: transitive across every ordered triple of a mixed board", () =>
            {
                var all = new List<TileKey>(TiedBoard());
                all.AddRange(Board(2));
                for (int i = 0; i < all.Count; i++)
                    for (int j = 0; j < all.Count; j++)
                        for (int k = 0; k < all.Count; k++)
                        {
                            if (TileOrder.Compare(all[i], all[j]) >= 0) continue;
                            if (TileOrder.Compare(all[j], all[k]) >= 0) continue;
                            Check.True(TileOrder.Compare(all[i], all[k]) < 0,
                                "a<b and b<c must imply a<c");
                        }
            });
        }
    }
}
