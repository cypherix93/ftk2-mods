using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Pure "is this element actually on screen" decision (SPEC S3 UI true-visibility). Replaces
    /// gating on <c>VisualElement.visible</c> alone — a LOCAL property that ignores a hidden
    /// ancestor, a <c>display: none</c> ancestor, zero opacity, a zero-size layout rect, or the
    /// owning UIDocument's GameObject being inactive. All five of those are checked here.
    ///
    /// No game/Unity reference: the plugin resolves every input reflectively off the live tree and
    /// hands in plain structs, so this is unit-testable with hand-built data. Every "cannot
    /// resolve this property" case reported by the plugin as a null must FAIL CLOSED here (treated
    /// as not on screen, with reason <see cref="SkipReason.Unresolved"/>) rather than defaulting to
    /// "on screen" — a false negative costs a diagnostic round trip, a false positive is a
    /// silently-wrong click.
    /// </summary>
    public static class OnScreenTest
    {
        public enum SkipReason
        {
            /// <summary>The element passed every check; it is on screen.</summary>
            None,
            InactiveDocument,
            HiddenAncestor,
            DisplayNone,
            ZeroOpacity,
            ZeroSize,

            /// <summary>Fail-closed: some property along the chain could not be resolved reflectively.</summary>
            Unresolved
        }

        /// <summary>
        /// One node of the element's ancestor chain (the element itself plus every ancestor up to
        /// the document root). Each field is nullable: null means the plugin could not resolve that
        /// property reflectively, which fails the whole check closed (<see cref="SkipReason.Unresolved"/>).
        /// </summary>
        public struct AncestorFrame
        {
            public readonly bool? Visible;
            public readonly bool? DisplayNone;
            public readonly double? Opacity;

            public AncestorFrame(bool? visible, bool? displayNone, double? opacity)
            {
                Visible = visible;
                DisplayNone = displayNone;
                Opacity = opacity;
            }
        }

        /// <summary>
        /// True only if ALL of: the owning document is active+enabled; the element and every
        /// ancestor are visible; the element and every ancestor are not display:none; the element
        /// and every ancestor have opacity &gt; 0; the element's own worldBound has width &gt; 0 and
        /// height &gt; 0. <paramref name="chain"/> must contain the element itself plus every
        /// ancestor up to (and including) the root — order does not matter, every frame is checked.
        /// </summary>
        public static bool IsOnScreen(bool? documentActive, IList<AncestorFrame> chain, double? width, double? height, out SkipReason reason)
        {
            reason = SkipReason.None;

            if (documentActive != true)
            {
                reason = documentActive == null ? SkipReason.Unresolved : SkipReason.InactiveDocument;
                return false;
            }

            if (chain == null || chain.Count == 0)
            {
                reason = SkipReason.Unresolved;
                return false;
            }

            for (int i = 0; i < chain.Count; i++)
            {
                bool? v = chain[i].Visible;
                if (v == null) { reason = SkipReason.Unresolved; return false; }
                if (v == false) { reason = SkipReason.HiddenAncestor; return false; }
            }

            for (int i = 0; i < chain.Count; i++)
            {
                bool? displayNone = chain[i].DisplayNone;
                if (displayNone == null) { reason = SkipReason.Unresolved; return false; }
                if (displayNone == true) { reason = SkipReason.DisplayNone; return false; }
            }

            for (int i = 0; i < chain.Count; i++)
            {
                double? opacity = chain[i].Opacity;
                if (opacity == null) { reason = SkipReason.Unresolved; return false; }
                if (!(opacity.Value > 0)) { reason = SkipReason.ZeroOpacity; return false; }
            }

            if (width == null || height == null)
            {
                reason = SkipReason.Unresolved;
                return false;
            }
            if (!(width.Value > 0) || !(height.Value > 0))
            {
                reason = SkipReason.ZeroSize;
                return false;
            }

            return true;
        }

        /// <summary>
        /// True if <paramref name="ownFrame"/> ALONE guarantees <see cref="IsOnScreen"/> will be
        /// false for this element and for EVERY descendant, regardless of what's above it in the
        /// ancestor chain or below it in the subtree. This is what makes the perf pruning in
        /// UiCommands.cs's tree walk (SPEC S3 UI dump perf) provably equivalent to the full walk:
        /// each of the three checks above (Visible/DisplayNone/Opacity) fails the WHOLE chain the
        /// moment ANY single frame in it fails, and <paramref name="ownFrame"/> is part of every
        /// descendant's chain too — so once it fails here, a caller may skip reflecting into the
        /// subtree entirely, cheaply count it instead, and attribute the whole count to
        /// <paramref name="reason"/> (an approximation for the diagnostic tally only; the on-screen
        /// decision itself is exact).
        /// </summary>
        public static bool TryGetSubtreePruneReason(AncestorFrame ownFrame, out SkipReason reason)
        {
            if (ownFrame.Visible == null) { reason = SkipReason.Unresolved; return true; }
            if (ownFrame.Visible == false) { reason = SkipReason.HiddenAncestor; return true; }
            if (ownFrame.DisplayNone == null) { reason = SkipReason.Unresolved; return true; }
            if (ownFrame.DisplayNone == true) { reason = SkipReason.DisplayNone; return true; }
            if (ownFrame.Opacity == null) { reason = SkipReason.Unresolved; return true; }
            if (!(ownFrame.Opacity.Value > 0)) { reason = SkipReason.ZeroOpacity; return true; }
            reason = SkipReason.None;
            return false;
        }

        /// <summary>Tally of why elements were excluded, for <c>crucible_ui_dump</c>'s diagnostic summary.</summary>
        public sealed class SkipReasonCounts
        {
            public int InactiveDocument;
            public int HiddenAncestor;
            public int DisplayNone;
            public int ZeroOpacity;
            public int ZeroSize;
            public int Unresolved;

            public void Add(SkipReason reason)
            {
                Add(reason, 1);
            }

            /// <summary>Used to fold a pruned subtree's whole descendant count into one bucket at once, instead of one Add() call per descendant.</summary>
            public void Add(SkipReason reason, int count)
            {
                switch (reason)
                {
                    case SkipReason.InactiveDocument: InactiveDocument += count; break;
                    case SkipReason.HiddenAncestor: HiddenAncestor += count; break;
                    case SkipReason.DisplayNone: DisplayNone += count; break;
                    case SkipReason.ZeroOpacity: ZeroOpacity += count; break;
                    case SkipReason.ZeroSize: ZeroSize += count; break;
                    case SkipReason.Unresolved: Unresolved += count; break;
                }
            }

            /// <summary>Adds every bucket of <paramref name="other"/> into this instance — used to merge one UIDocument's walk stats into the dump-wide total.</summary>
            public void Merge(SkipReasonCounts other)
            {
                if (other == null) return;
                InactiveDocument += other.InactiveDocument;
                HiddenAncestor += other.HiddenAncestor;
                DisplayNone += other.DisplayNone;
                ZeroOpacity += other.ZeroOpacity;
                ZeroSize += other.ZeroSize;
                Unresolved += other.Unresolved;
            }

            public int Total()
            {
                return InactiveDocument + HiddenAncestor + DisplayNone + ZeroOpacity + ZeroSize + Unresolved;
            }

            public string Format()
            {
                return "inactiveDocument=" + InactiveDocument
                    + " hiddenAncestor=" + HiddenAncestor
                    + " displayNone=" + DisplayNone
                    + " zeroOpacity=" + ZeroOpacity
                    + " zeroSize=" + ZeroSize
                    + " unresolved=" + Unresolved;
            }
        }
    }
}
