using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Pure depth-first tree walk driving <c>crucible_ui_dump</c>'s traversal (SPEC S3 UI
    /// true-visibility perf). Generic over the node type via delegates so pruning, the hard
    /// element budget, and subtree counting are unit-testable with a hand-built tree — no live
    /// Unity UIToolkit tree, no reflection. UiCommands.cs (Crucible.Plugin) binds
    /// <typeparamref name="TNode"/> to the real VisualElement type and supplies reflective
    /// accessors as delegates.
    ///
    /// <b>Pruning</b> (the big optimisation): once a node's OWN frame fails
    /// <see cref="OnScreenTest.TryGetSubtreePruneReason"/>, nothing beneath it can ever be
    /// on-screen (see that method's doc for why this is provably equivalent to the full walk) —
    /// this stops recursing into the subtree instead of reflecting into every descendant, counts
    /// it cheaply via <c>countSubtree</c>, and folds the whole count into
    /// <see cref="Stats.PrunedCounts"/> under that one reason.
    ///
    /// <b>Budget</b>: stops walking (and reports <see cref="Stats.BudgetTruncated"/>) once
    /// <see cref="Stats.ElementsVisited"/> would exceed <c>budget</c> — a dump must never silently
    /// blow the main-thread timeout; a truncated-but-honest partial result beats no result at all.
    /// </summary>
    public static class UiTreeWalker
    {
        /// <summary>Reads an element's own worldBound width/height in one shot (mirrors the live reflective read, which fetches both from a single Rect) — a delegate rather than two separate Funcs so the walker never pays for that read twice.</summary>
        public delegate void SizeReader<TNode>(TNode node, out double? width, out double? height);

        public sealed class Stats
        {
            public int ElementsVisited;
            public int SubtreesPruned;
            public bool BudgetTruncated;
            public readonly OnScreenTest.SkipReasonCounts PrunedCounts = new OnScreenTest.SkipReasonCounts();
        }

        public static Stats Walk<TNode>(
            TNode root,
            bool? documentActive,
            int budget,
            Func<TNode, OnScreenTest.AncestorFrame> getFrame,
            SizeReader<TNode> getSize,
            Func<TNode, IEnumerable<TNode>> getChildren,
            Func<TNode, int> countSubtree,
            Action<TNode, bool, OnScreenTest.SkipReason> visit)
        {
            Stats stats = new Stats();
            if (budget < 0) budget = 0;
            List<OnScreenTest.AncestorFrame> ancestorStack = new List<OnScreenTest.AncestorFrame>();
            WalkNode(root, documentActive, budget, getFrame, getSize, getChildren, countSubtree, visit, ancestorStack, stats);
            return stats;
        }

        private static void WalkNode<TNode>(
            TNode node, bool? documentActive, int budget,
            Func<TNode, OnScreenTest.AncestorFrame> getFrame,
            SizeReader<TNode> getSize,
            Func<TNode, IEnumerable<TNode>> getChildren,
            Func<TNode, int> countSubtree,
            Action<TNode, bool, OnScreenTest.SkipReason> visit,
            List<OnScreenTest.AncestorFrame> ancestorStack,
            Stats stats)
        {
            if (node == null) return;
            if (stats.BudgetTruncated) return;
            if (stats.ElementsVisited >= budget) { stats.BudgetTruncated = true; return; }

            stats.ElementsVisited++;

            OnScreenTest.AncestorFrame frame = getFrame(node);
            ancestorStack.Add(frame);

            OnScreenTest.SkipReason pruneReason;
            bool prune = OnScreenTest.TryGetSubtreePruneReason(frame, out pruneReason);

            bool onScreen;
            OnScreenTest.SkipReason reason;
            if (prune)
            {
                // Own frame alone already guarantees exclusion — no need to pay for a worldBound
                // read (the most expensive part of the per-element check) that IsOnScreen would
                // never even reach.
                onScreen = false;
                reason = pruneReason;
            }
            else
            {
                double? width, height;
                getSize(node, out width, out height);
                onScreen = OnScreenTest.IsOnScreen(documentActive, ancestorStack, width, height, out reason);
            }

            visit(node, onScreen, reason);

            if (prune)
            {
                stats.SubtreesPruned++;
                int descendantCount = countSubtree(node) - 1; // countSubtree includes node itself
                if (descendantCount > 0) stats.PrunedCounts.Add(pruneReason, descendantCount);
            }
            else
            {
                IEnumerable<TNode> children = getChildren(node);
                if (children != null)
                {
                    foreach (TNode child in children)
                    {
                        WalkNode(child, documentActive, budget, getFrame, getSize, getChildren, countSubtree, visit, ancestorStack, stats);
                        if (stats.BudgetTruncated) break;
                    }
                }
            }

            ancestorStack.RemoveAt(ancestorStack.Count - 1);
        }
    }
}
