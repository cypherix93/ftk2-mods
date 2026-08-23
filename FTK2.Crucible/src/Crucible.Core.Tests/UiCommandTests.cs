using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    internal static class UiCommandTests
    {
        private static UiElementInfo El(string type, string name, string text, bool visible = true, bool enabled = true, bool focused = false)
        {
            return new UiElementInfo(type, name, text, visible, enabled, focused);
        }

        private static UiElementInfo ElOnScreen(string type, string name, string text, bool onScreen, OnScreenTest.SkipReason reason, string docName = null)
        {
            return new UiElementInfo(type, name, text, true, true, false, onScreen, reason, docName);
        }

        /// <summary>A fully on-screen ancestor chain frame: visible, not display:none, opaque.</summary>
        private static OnScreenTest.AncestorFrame Frame(bool? visible = true, bool? displayNone = false, double? opacity = 1.0)
        {
            return new OnScreenTest.AncestorFrame(visible, displayNone, opacity);
        }

        // ---------------------------------------------------------------- UiTreeWalker fixtures
        //
        // A hand-built tree standing in for the live UIToolkit VisualElement tree, so
        // UiTreeWalker's pruning/budget/counting logic (Crucible.Core, pure) can be exercised
        // without a live Unity UIToolkit tree or reflection -- see UiCommands.cs (Crucible.Plugin)
        // for how the real walk binds these same delegates to reflective accessors.

        private sealed class FakeNode
        {
            public OnScreenTest.AncestorFrame Frame;
            public double? Width;
            public double? Height;
            public string Name;
            public readonly List<FakeNode> Children = new List<FakeNode>();
        }

        private static OnScreenTest.AncestorFrame FakeGetFrame(FakeNode n) { return n.Frame; }
        private static void FakeGetSize(FakeNode n, out double? width, out double? height) { width = n.Width; height = n.Height; }
        private static IEnumerable<FakeNode> FakeGetChildren(FakeNode n) { return n.Children; }

        private static int FakeCountSubtree(FakeNode n)
        {
            int count = 1;
            foreach (FakeNode child in n.Children) count += FakeCountSubtree(child);
            return count;
        }

        internal static void RunAll()
        {
            TestHarness.Section("UiDirection.TryParse");

            TestHarness.Run("parses lowercase up", delegate
            {
                string canonical, e;
                TestHarness.True(UiDirection.TryParse("up", out canonical, out e), "parsed: " + e);
                TestHarness.Equal("Up", canonical, "canonical");
            });

            TestHarness.Run("parses uppercase UP", delegate
            {
                string canonical, e;
                TestHarness.True(UiDirection.TryParse("UP", out canonical, out e), "parsed: " + e);
                TestHarness.Equal("Up", canonical, "canonical");
            });

            TestHarness.Run("parses mixed-case Up", delegate
            {
                string canonical, e;
                TestHarness.True(UiDirection.TryParse("Up", out canonical, out e), "parsed: " + e);
                TestHarness.Equal("Up", canonical, "canonical");
            });

            TestHarness.Run("parses down/left/right", delegate
            {
                string canonical, e;
                TestHarness.True(UiDirection.TryParse("down", out canonical, out e), "down: " + e);
                TestHarness.Equal("Down", canonical, "down canonical");
                TestHarness.True(UiDirection.TryParse("LEFT", out canonical, out e), "left: " + e);
                TestHarness.Equal("Left", canonical, "left canonical");
                TestHarness.True(UiDirection.TryParse("RiGhT", out canonical, out e), "right: " + e);
                TestHarness.Equal("Right", canonical, "right canonical");
            });

            TestHarness.Run("NEGATIVE CONTROL: rejects an invalid direction rather than defaulting to Up", delegate
            {
                string canonical, e;
                TestHarness.False(UiDirection.TryParse("sideways", out canonical, out e), "should reject");
                TestHarness.True(canonical == null, "canonical stays null on failure (no silent Up default)");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects empty direction", delegate
            {
                string canonical, e;
                TestHarness.False(UiDirection.TryParse("", out canonical, out e), "should reject");
            });

            TestHarness.Run("rejects null direction", delegate
            {
                string canonical, e;
                TestHarness.False(UiDirection.TryParse(null, out canonical, out e), "should reject");
            });

            TestHarness.Run("rejects whitespace-only direction", delegate
            {
                string canonical, e;
                TestHarness.False(UiDirection.TryParse("   ", out canonical, out e), "should reject");
            });

            TestHarness.Run("trims surrounding whitespace on a valid direction", delegate
            {
                string canonical, e;
                TestHarness.True(UiDirection.TryParse("  up  ", out canonical, out e), "parsed: " + e);
                TestHarness.Equal("Up", canonical, "canonical");
            });

            TestHarness.Section("UiTreeRenderer.Matches");

            TestHarness.Run("matches by name substring, case-insensitive", delegate
            {
                UiElementInfo e = El("Button", "CloseButton", null);
                TestHarness.True(UiTreeRenderer.Matches(e, "close"), "should match name");
            });

            TestHarness.Run("matches by text substring, case-insensitive", delegate
            {
                UiElementInfo e = El("Button", "btn1", "Close");
                TestHarness.True(UiTreeRenderer.Matches(e, "CLOSE"), "should match text");
            });

            TestHarness.Run("does not match unrelated filter", delegate
            {
                UiElementInfo e = El("Button", "CloseButton", "Close");
                TestHarness.False(UiTreeRenderer.Matches(e, "Submit"), "should not match");
            });

            TestHarness.Run("null name/text never match, don't throw", delegate
            {
                UiElementInfo e = El("VisualElement", null, null);
                TestHarness.False(UiTreeRenderer.Matches(e, "anything"), "should not match");
            });

            TestHarness.Section("UiTreeRenderer.Render");

            TestHarness.Run("skips invisible elements but counts them", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>
                {
                    El("Button", "A", null, visible: true),
                    El("Button", "B", null, visible: false),
                    El("Button", "C", null, visible: false),
                };
                int matched, skipped; bool truncated;
                string rendered = UiTreeRenderer.Render(elements, "-", 200, out matched, out skipped, out truncated);
                TestHarness.Equal(1, matched, "matched visible count");
                TestHarness.Equal(2, skipped, "skipped invisible count");
                TestHarness.False(truncated, "not truncated");
                TestHarness.True(rendered.IndexOf("name='B'") < 0, "invisible B must not appear in rendered output");
            });

            TestHarness.Run("filter '-' matches everything visible", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>
                {
                    El("Button", "Alpha", null),
                    El("Label", "Beta", "some text"),
                };
                int matched, skipped; bool truncated;
                UiTreeRenderer.Render(elements, "-", 200, out matched, out skipped, out truncated);
                TestHarness.Equal(2, matched, "all visible matched with '-'");
            });

            TestHarness.Run("filter narrows to matching elements only", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>
                {
                    El("Button", "CloseButton", "Close"),
                    El("Button", "SubmitButton", "Submit"),
                };
                int matched, skipped; bool truncated;
                string rendered = UiTreeRenderer.Render(elements, "close", 200, out matched, out skipped, out truncated);
                TestHarness.Equal(1, matched, "only Close matched");
                TestHarness.True(rendered.IndexOf("SubmitButton") < 0, "Submit must not appear");
            });

            TestHarness.Run("truncation is reported, not silent", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>();
                for (int i = 0; i < 5; i++) elements.Add(El("Button", "B" + i, null));

                int matched, skipped; bool truncated;
                string rendered = UiTreeRenderer.Render(elements, "-", 3, out matched, out skipped, out truncated);
                TestHarness.Equal(5, matched, "matched counts all 5, cap only limits rendered lines");
                TestHarness.True(truncated, "truncated flag set");
                TestHarness.True(rendered.IndexOf("truncated", System.StringComparison.OrdinalIgnoreCase) >= 0,
                    "rendered text mentions truncation: " + rendered);
            });

            TestHarness.Run("no truncation when everything fits under the cap", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>
                {
                    El("Button", "A", null),
                    El("Button", "B", null),
                };
                int matched, skipped; bool truncated;
                string rendered = UiTreeRenderer.Render(elements, "-", 200, out matched, out skipped, out truncated);
                TestHarness.False(truncated, "not truncated");
                TestHarness.True(rendered.IndexOf("truncated", System.StringComparison.OrdinalIgnoreCase) < 0,
                    "rendered text must not mention truncation when nothing was cut");
            });

            TestHarness.Run("empty element list reports no matches, not an exception", delegate
            {
                int matched, skipped; bool truncated;
                string rendered = UiTreeRenderer.Render(new List<UiElementInfo>(), "-", 200, out matched, out skipped, out truncated);
                TestHarness.Equal(0, matched, "zero matches");
                TestHarness.False(truncated, "not truncated");
                TestHarness.True(rendered.Length > 0, "renders a 'no elements' message rather than an empty string");
            });

            TestHarness.Run("null element list is handled without throwing", delegate
            {
                int matched, skipped; bool truncated;
                string rendered = UiTreeRenderer.Render(null, "-", 200, out matched, out skipped, out truncated);
                TestHarness.Equal(0, matched, "zero matches");
                TestHarness.True(rendered != null, "non-null result");
            });

            TestHarness.Section("UiKindsFilter.Matches");

            TestHarness.Run("'-' matches every kind", delegate
            {
                TestHarness.True(UiKindsFilter.Matches(El("Button", "A", null), "-"), "Button matches '-'");
                TestHarness.True(UiKindsFilter.Matches(El("Label", "B", null), "-"), "Label matches '-'");
            });

            TestHarness.Run("empty/null kinds is treated as '-' (match everything) — explicit choice, not rejection", delegate
            {
                TestHarness.True(UiKindsFilter.Matches(El("Button", "A", null), ""), "empty string matches everything");
                TestHarness.True(UiKindsFilter.Matches(El("Button", "A", null), null), "null matches everything");
            });

            TestHarness.Run("single kind matches only that type, case-insensitive", delegate
            {
                TestHarness.True(UiKindsFilter.Matches(El("Button", "A", null), "button"), "lowercase 'button' matches Button (case-insensitive)");
                TestHarness.True(UiKindsFilter.Matches(El("Button", "A", null), "BUTTON"), "uppercase 'BUTTON' matches Button (case-insensitive)");
            });

            TestHarness.Run("comma-separated kinds matches any listed type", delegate
            {
                TestHarness.True(UiKindsFilter.Matches(El("Button", "A", null), "Button,Label"), "Button in list");
                TestHarness.True(UiKindsFilter.Matches(El("Label", "B", null), "Button,Label"), "Label in list");
            });

            TestHarness.Run("NEGATIVE CONTROL: a kind not in the list does not match, even alongside other kinds", delegate
            {
                TestHarness.False(UiKindsFilter.Matches(El("TemplateContainer", "C", null), "Button,Label"), "TemplateContainer not in list");
            });

            TestHarness.Run("NEGATIVE CONTROL: an unknown/misspelled kind does not silently match everything", delegate
            {
                TestHarness.False(UiKindsFilter.Matches(El("Button", "A", null), "Frobnicator"), "unrecognized kind name must not match Button");
                TestHarness.False(UiKindsFilter.Matches(El("Label", "B", null), "Frobnicator"), "unrecognized kind name must not match Label either");
            });

            TestHarness.Run("whitespace around comma-separated kinds is trimmed", delegate
            {
                TestHarness.True(UiKindsFilter.Matches(El("Button", "A", null), " Button , Label "), "trims whitespace around each kind");
            });

            TestHarness.Run("null element never matches, does not throw", delegate
            {
                TestHarness.False(UiKindsFilter.Matches(null, "-"), "null element should not match");
            });

            TestHarness.Section("UiTreeRenderer.Render with kinds filter");

            TestHarness.Run("kinds filter narrows output on top of the name/text filter", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>
                {
                    El("Button", "CloseButton", "Close"),
                    El("Label", "CloseLabel", "Close"),
                };
                int matched, skipped; bool truncated;
                string rendered = UiTreeRenderer.Render(elements, "close", "Button", 200, out matched, out skipped, out truncated);
                TestHarness.Equal(1, matched, "only the Button matched, Label filtered out by kinds");
                TestHarness.True(rendered.IndexOf("CloseLabel") < 0, "CloseLabel must not appear");
            });

            TestHarness.Run("6-arg Render overload (no kinds) still matches everything visible, unaffected by the new overload", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>
                {
                    El("Button", "A", null),
                    El("Label", "B", "text"),
                };
                int matched, skipped; bool truncated;
                UiTreeRenderer.Render(elements, "-", 200, out matched, out skipped, out truncated);
                TestHarness.Equal(2, matched, "old overload still matches all visible kinds");
            });

            TestHarness.Section("UiSelectorMatcher.Find");

            TestHarness.Run("finds a single visible match by name", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo> { El("Button", "CloseButton", "Close") };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "close", out e);
                TestHarness.True(r.Found, "should find");
                TestHarness.Equal("CloseButton", r.Match.Name, "matched element name");
                TestHarness.Equal(0, r.OtherMatches.Count, "no other matches");
            });

            TestHarness.Run("finds a single visible match by text when name doesn't match", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo> { El("Button", "btn7", "Close") };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "close", out e);
                TestHarness.True(r.Found, "should find");
                TestHarness.Equal("btn7", r.Match.Name, "matched element name");
            });

            TestHarness.Run("first-match semantics: uses the first match, names the others", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    El("Button", "CloseButtonTop", "Close"),
                    El("Button", "CloseButtonBottom", "Close"),
                };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "close", out e);
                TestHarness.True(r.Found, "should find");
                TestHarness.Equal("CloseButtonTop", r.Match.Name, "uses the first match");
                TestHarness.Equal(1, r.OtherMatches.Count, "one other match");
                TestHarness.Equal("CloseButtonBottom", r.OtherMatches[0].Name, "names the other match");
            });

            TestHarness.Run("NEGATIVE CONTROL: a selector matching nothing reports no-match, does not silently pick something", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    El("Button", "SubmitButton", "Submit"),
                    El("Button", "CancelButton", "Cancel"),
                };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "close", out e);
                TestHarness.False(r.Found, "should not find");
                TestHarness.True(r.Match == null, "no match object on failure");
            });

            TestHarness.Run("no-match result lists the visible buttons that DO exist, for diagnosability", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    El("Button", "SubmitButton", "Submit"),
                    El("Button", "CancelButton", "Cancel"),
                };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "close", out e);
                TestHarness.Equal(2, r.AllVisible.Count, "lists both visible buttons that exist");
            });

            TestHarness.Run("NEGATIVE CONTROL: an invisible element must never be selected, even on a name match", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    El("Button", "CloseButton", "Close", visible: false),
                };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "close", out e);
                TestHarness.False(r.Found, "invisible match must not count as found");
                TestHarness.Equal(0, r.AllVisible.Count, "invisible element is not even listed as a visible candidate");
            });

            TestHarness.Run("an invisible element is skipped in favor of a later visible match", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    El("Button", "CloseButtonHidden", "Close", visible: false),
                    El("Button", "CloseButtonVisible", "Close", visible: true),
                };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "close", out e);
                TestHarness.True(r.Found, "should find the visible one");
                TestHarness.Equal("CloseButtonVisible", r.Match.Name, "skips the invisible match");
            });

            TestHarness.Run("rejects an empty selector", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo> { El("Button", "A", null) };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "", out e);
                TestHarness.False(r.Found, "should not find");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects a null selector", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo> { El("Button", "A", null) };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, null, out e);
                TestHarness.False(r.Found, "should not find");
            });

            TestHarness.Run("empty candidate list reports no-match, not an exception", delegate
            {
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(new List<UiElementInfo>(), "close", out e);
                TestHarness.False(r.Found, "should not find");
                TestHarness.Equal(0, r.AllVisible.Count, "no visible candidates");
            });

            TestHarness.Run("null candidate list is handled without throwing", delegate
            {
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(null, "close", out e);
                TestHarness.False(r.Found, "should not find");
            });

            TestHarness.Section("OnScreenTest.IsOnScreen");

            TestHarness.Run("POSITIVE CONTROL: fully visible element with a fully visible chain IS on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame> { Frame(), Frame(), Frame() };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, 100.0, 40.0, out reason);
                TestHarness.True(onScreen, "should be on screen: " + reason);
                TestHarness.True(reason == OnScreenTest.SkipReason.None, "reason is None when on screen");
            });

            TestHarness.Run("NEGATIVE CONTROL: a hidden ancestor makes a visible child NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame>
                {
                    Frame(visible: true),              // the element itself
                    Frame(visible: false),              // a hidden ancestor
                    Frame(visible: true),
                };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "should not be on screen");
                TestHarness.True(reason == OnScreenTest.SkipReason.HiddenAncestor, "reason should be HiddenAncestor, was " + reason);
            });

            TestHarness.Run("NEGATIVE CONTROL: display:none on an ancestor makes a visible child NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame>
                {
                    Frame(),
                    Frame(displayNone: true),
                    Frame(),
                };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "should not be on screen");
                TestHarness.True(reason == OnScreenTest.SkipReason.DisplayNone, "reason should be DisplayNone, was " + reason);
            });

            TestHarness.Run("NEGATIVE CONTROL: zero opacity on an ancestor makes a visible child NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame>
                {
                    Frame(),
                    Frame(opacity: 0.0),
                    Frame(),
                };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "should not be on screen");
                TestHarness.True(reason == OnScreenTest.SkipReason.ZeroOpacity, "reason should be ZeroOpacity, was " + reason);
            });

            TestHarness.Run("NEGATIVE CONTROL: a zero-size rect makes an otherwise-visible element NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame> { Frame(), Frame() };
                OnScreenTest.SkipReason reason;
                bool onScreenZeroWidth = OnScreenTest.IsOnScreen(true, chain, 0.0, 40.0, out reason);
                TestHarness.False(onScreenZeroWidth, "zero width should not be on screen");
                TestHarness.True(reason == OnScreenTest.SkipReason.ZeroSize, "reason should be ZeroSize (width), was " + reason);

                bool onScreenZeroHeight = OnScreenTest.IsOnScreen(true, chain, 100.0, 0.0, out reason);
                TestHarness.False(onScreenZeroHeight, "zero height should not be on screen");
                TestHarness.True(reason == OnScreenTest.SkipReason.ZeroSize, "reason should be ZeroSize (height), was " + reason);
            });

            TestHarness.Run("NEGATIVE CONTROL: an inactive document excludes everything under it", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame> { Frame(), Frame() };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(false, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "should not be on screen");
                TestHarness.True(reason == OnScreenTest.SkipReason.InactiveDocument, "reason should be InactiveDocument, was " + reason);
            });

            TestHarness.Run("fail-closed: an unresolved document active state is NOT on screen (not defaulted to true)", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame> { Frame() };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(null, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "unresolved document active state must fail closed");
                TestHarness.True(reason == OnScreenTest.SkipReason.Unresolved, "reason should be Unresolved, was " + reason);
            });

            TestHarness.Run("fail-closed: an unresolved visible flag anywhere in the chain is NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame>
                {
                    Frame(),
                    new OnScreenTest.AncestorFrame(null, false, 1.0),
                };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "unresolved visible must fail closed");
                TestHarness.True(reason == OnScreenTest.SkipReason.Unresolved, "reason should be Unresolved, was " + reason);
            });

            TestHarness.Run("fail-closed: an unresolved display:none flag anywhere in the chain is NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame>
                {
                    Frame(),
                    new OnScreenTest.AncestorFrame(true, null, 1.0),
                };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "unresolved display:none must fail closed");
                TestHarness.True(reason == OnScreenTest.SkipReason.Unresolved, "reason should be Unresolved, was " + reason);
            });

            TestHarness.Run("fail-closed: an unresolved opacity anywhere in the chain is NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame>
                {
                    Frame(),
                    new OnScreenTest.AncestorFrame(true, false, null),
                };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "unresolved opacity must fail closed");
                TestHarness.True(reason == OnScreenTest.SkipReason.Unresolved, "reason should be Unresolved, was " + reason);
            });

            TestHarness.Run("fail-closed: an unresolved worldBound size is NOT on screen", delegate
            {
                List<OnScreenTest.AncestorFrame> chain = new List<OnScreenTest.AncestorFrame> { Frame() };
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, chain, null, 40.0, out reason);
                TestHarness.False(onScreen, "unresolved width must fail closed");
                TestHarness.True(reason == OnScreenTest.SkipReason.Unresolved, "reason should be Unresolved, was " + reason);
            });

            TestHarness.Run("fail-closed: an empty ancestor chain is NOT on screen (not silently accepted)", delegate
            {
                OnScreenTest.SkipReason reason;
                bool onScreen = OnScreenTest.IsOnScreen(true, new List<OnScreenTest.AncestorFrame>(), 100.0, 40.0, out reason);
                TestHarness.False(onScreen, "empty chain must fail closed");
                TestHarness.True(reason == OnScreenTest.SkipReason.Unresolved, "reason should be Unresolved, was " + reason);
            });

            TestHarness.Section("OnScreenTest.SkipReasonCounts");

            TestHarness.Run("tallies each reason into its own bucket", delegate
            {
                OnScreenTest.SkipReasonCounts counts = new OnScreenTest.SkipReasonCounts();
                counts.Add(OnScreenTest.SkipReason.InactiveDocument);
                counts.Add(OnScreenTest.SkipReason.InactiveDocument);
                counts.Add(OnScreenTest.SkipReason.HiddenAncestor);
                counts.Add(OnScreenTest.SkipReason.DisplayNone);
                counts.Add(OnScreenTest.SkipReason.ZeroOpacity);
                counts.Add(OnScreenTest.SkipReason.ZeroSize);
                counts.Add(OnScreenTest.SkipReason.Unresolved);
                counts.Add(OnScreenTest.SkipReason.None); // must not be tallied anywhere

                TestHarness.Equal(2, counts.InactiveDocument, "inactiveDocument count");
                TestHarness.Equal(1, counts.HiddenAncestor, "hiddenAncestor count");
                TestHarness.Equal(1, counts.DisplayNone, "displayNone count");
                TestHarness.Equal(1, counts.ZeroOpacity, "zeroOpacity count");
                TestHarness.Equal(1, counts.ZeroSize, "zeroSize count");
                TestHarness.Equal(1, counts.Unresolved, "unresolved count");
            });

            TestHarness.Run("Add(reason, count) folds a whole pruned subtree into one bucket at once", delegate
            {
                OnScreenTest.SkipReasonCounts counts = new OnScreenTest.SkipReasonCounts();
                counts.Add(OnScreenTest.SkipReason.DisplayNone, 24360);
                counts.Add(OnScreenTest.SkipReason.DisplayNone, 5);
                TestHarness.Equal(24365, counts.DisplayNone, "displayNone count");
                TestHarness.Equal(24365, counts.Total(), "total");
            });

            TestHarness.Run("Merge adds every bucket of another SkipReasonCounts", delegate
            {
                OnScreenTest.SkipReasonCounts a = new OnScreenTest.SkipReasonCounts();
                a.Add(OnScreenTest.SkipReason.DisplayNone, 10);
                a.Add(OnScreenTest.SkipReason.ZeroOpacity, 2);

                OnScreenTest.SkipReasonCounts b = new OnScreenTest.SkipReasonCounts();
                b.Add(OnScreenTest.SkipReason.DisplayNone, 5);
                b.Add(OnScreenTest.SkipReason.HiddenAncestor, 1);

                a.Merge(b);

                TestHarness.Equal(15, a.DisplayNone, "displayNone after merge");
                TestHarness.Equal(2, a.ZeroOpacity, "zeroOpacity after merge");
                TestHarness.Equal(1, a.HiddenAncestor, "hiddenAncestor after merge");
                TestHarness.Equal(18, a.Total(), "total after merge");
            });

            TestHarness.Section("OnScreenTest.TryGetSubtreePruneReason (S3 UI dump perf pruning)");

            TestHarness.Run("a hidden (visible=false) element prunes with HiddenAncestor", delegate
            {
                OnScreenTest.SkipReason reason;
                bool pruned = OnScreenTest.TryGetSubtreePruneReason(Frame(visible: false), out reason);
                TestHarness.True(pruned, "should prune");
                TestHarness.True(reason == OnScreenTest.SkipReason.HiddenAncestor, "reason should be HiddenAncestor, was " + reason);
            });

            TestHarness.Run("a display:none element prunes with DisplayNone", delegate
            {
                OnScreenTest.SkipReason reason;
                bool pruned = OnScreenTest.TryGetSubtreePruneReason(Frame(displayNone: true), out reason);
                TestHarness.True(pruned, "should prune");
                TestHarness.True(reason == OnScreenTest.SkipReason.DisplayNone, "reason should be DisplayNone, was " + reason);
            });

            TestHarness.Run("a zero-opacity element prunes with ZeroOpacity", delegate
            {
                OnScreenTest.SkipReason reason;
                bool pruned = OnScreenTest.TryGetSubtreePruneReason(Frame(opacity: 0.0), out reason);
                TestHarness.True(pruned, "should prune");
                TestHarness.True(reason == OnScreenTest.SkipReason.ZeroOpacity, "reason should be ZeroOpacity, was " + reason);
            });

            TestHarness.Run("an unresolved own visible fails closed with Unresolved (prunes rather than assumes on-screen)", delegate
            {
                OnScreenTest.SkipReason reason;
                bool pruned = OnScreenTest.TryGetSubtreePruneReason(Frame(visible: null), out reason);
                TestHarness.True(pruned, "should prune");
                TestHarness.True(reason == OnScreenTest.SkipReason.Unresolved, "reason should be Unresolved, was " + reason);
            });

            TestHarness.Run("a fully clean own frame does not prune", delegate
            {
                OnScreenTest.SkipReason reason;
                bool pruned = OnScreenTest.TryGetSubtreePruneReason(Frame(), out reason);
                TestHarness.False(pruned, "should not prune");
                TestHarness.True(reason == OnScreenTest.SkipReason.None, "reason should be None, was " + reason);
            });

            TestHarness.Run("NEGATIVE CONTROL: a display:none root's own-frame prune reason exactly matches the full ancestor-chain decision for a descendant that would otherwise pass", delegate
            {
                // rootFrame alone is display:none; descendantFrame is, on its own, fully clean --
                // visible, not display:none, opaque -- i.e. it would be reported on-screen if
                // pruning skipped it without ever checking the chain above it.
                OnScreenTest.AncestorFrame rootFrame = Frame(displayNone: true);
                OnScreenTest.AncestorFrame descendantFrame = Frame();

                List<OnScreenTest.AncestorFrame> fullChain = new List<OnScreenTest.AncestorFrame> { rootFrame, descendantFrame };
                OnScreenTest.SkipReason fullWalkReason;
                bool fullWalkOnScreen = OnScreenTest.IsOnScreen(true, fullChain, 100.0, 40.0, out fullWalkReason);
                TestHarness.False(fullWalkOnScreen, "a descendant under a display:none root must still be excluded by the full ancestor-chain walk");
                TestHarness.True(fullWalkReason == OnScreenTest.SkipReason.DisplayNone, "full-walk reason should be DisplayNone, was " + fullWalkReason);

                OnScreenTest.SkipReason pruneReason;
                bool pruned = OnScreenTest.TryGetSubtreePruneReason(rootFrame, out pruneReason);
                TestHarness.True(pruned, "the root's own frame alone must predict pruning, without ever looking at the descendant");
                TestHarness.True(pruneReason == fullWalkReason, "the cheap prune reason must match the full-walk reason exactly, so the diagnostic tally stays accurate");
            });

            TestHarness.Section("UiTreeRenderer.Render gates on OnScreen, not local Visible/Enabled");

            TestHarness.Run("an element that is locally visible but off-screen (hidden ancestor) is skipped and counted by reason", delegate
            {
                List<UiElementInfo> elements = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "OnScreenBtn", null, true, OnScreenTest.SkipReason.None),
                    ElOnScreen("Button", "HiddenAncestorBtn", null, false, OnScreenTest.SkipReason.HiddenAncestor),
                    ElOnScreen("Button", "DisplayNoneBtn", null, false, OnScreenTest.SkipReason.DisplayNone),
                    ElOnScreen("Button", "ZeroOpacityBtn", null, false, OnScreenTest.SkipReason.ZeroOpacity),
                    ElOnScreen("Button", "ZeroSizeBtn", null, false, OnScreenTest.SkipReason.ZeroSize),
                };
                int matched, skipped; bool truncated; OnScreenTest.SkipReasonCounts counts;
                string rendered = UiTreeRenderer.Render(elements, "-", "-", 200, out matched, out skipped, out truncated, out counts);

                TestHarness.Equal(1, matched, "only the on-screen button matched");
                TestHarness.Equal(4, skipped, "four off-screen buttons skipped");
                TestHarness.True(rendered.IndexOf("HiddenAncestorBtn") < 0, "off-screen HiddenAncestorBtn must not appear");
                TestHarness.True(rendered.IndexOf("DisplayNoneBtn") < 0, "off-screen DisplayNoneBtn must not appear");
                TestHarness.True(rendered.IndexOf("ZeroOpacityBtn") < 0, "off-screen ZeroOpacityBtn must not appear");
                TestHarness.True(rendered.IndexOf("ZeroSizeBtn") < 0, "off-screen ZeroSizeBtn must not appear");
                TestHarness.True(rendered.IndexOf("OnScreenBtn") >= 0, "on-screen button must appear");

                TestHarness.Equal(1, counts.HiddenAncestor, "hiddenAncestor breakdown");
                TestHarness.Equal(1, counts.DisplayNone, "displayNone breakdown");
                TestHarness.Equal(1, counts.ZeroOpacity, "zeroOpacity breakdown");
                TestHarness.Equal(1, counts.ZeroSize, "zeroSize breakdown");
            });

            TestHarness.Run("FormatLine includes the owning document's name", delegate
            {
                UiElementInfo e = ElOnScreen("Button", "back-btn", "Back", true, OnScreenTest.SkipReason.None, "LobbyPanel");
                string line = UiTreeRenderer.FormatLine(e);
                TestHarness.True(line.IndexOf("LobbyPanel") >= 0, "rendered line should mention the owning document name: " + line);
            });

            TestHarness.Section("UiSelectorMatcher.Find gates on OnScreen, not local Visible");

            TestHarness.Run("NEGATIVE CONTROL: a locally-visible element behind a display:none ancestor is never selected", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "back-btn", "Back", false, OnScreenTest.SkipReason.DisplayNone, "HiddenPanel"),
                };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "back", out e);
                TestHarness.False(r.Found, "off-screen match must not count as found");
                TestHarness.Equal(0, r.AllVisible.Count, "off-screen element is not even listed as a candidate");
            });

            TestHarness.Run("an off-screen element is skipped in favor of a later truly on-screen match", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "back-btn", "Back", false, OnScreenTest.SkipReason.InactiveDocument, "CombatHud"),
                    ElOnScreen("Button", "back-btn", "Back", true, OnScreenTest.SkipReason.None, "LobbyPanel"),
                };
                string e;
                UiSelectorMatcher.Result r = UiSelectorMatcher.Find(candidates, "back", out e);
                TestHarness.True(r.Found, "should find the on-screen one");
                TestHarness.Equal("LobbyPanel", r.Match.DocumentName, "matches the on-screen LobbyPanel back-btn, not the off-screen CombatHud one");
            });

            TestHarness.Section("UiTreeWalker.Walk (S3 UI dump perf: pruning + budget)");

            TestHarness.Run("walks and visits every element when nothing is hidden", delegate
            {
                FakeNode leaf1 = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "leaf1" };
                FakeNode leaf2 = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "leaf2" };
                FakeNode root = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "root" };
                root.Children.Add(leaf1);
                root.Children.Add(leaf2);

                List<string> visited = new List<string>();
                List<bool> onScreenFlags = new List<bool>();
                UiTreeWalker.Stats stats = UiTreeWalker.Walk<FakeNode>(
                    root, true, 100,
                    FakeGetFrame, FakeGetSize, FakeGetChildren, FakeCountSubtree,
                    delegate(FakeNode n, bool onScreen, OnScreenTest.SkipReason reason) { visited.Add(n.Name); onScreenFlags.Add(onScreen); });

                TestHarness.Equal(3, stats.ElementsVisited, "elements visited");
                TestHarness.Equal(0, stats.SubtreesPruned, "nothing should be pruned");
                TestHarness.False(stats.BudgetTruncated, "should not be truncated");
                TestHarness.Equal(3, visited.Count, "visit called for every node");
                foreach (bool onScreen in onScreenFlags) TestHarness.True(onScreen, "every node should be on screen");
            });

            TestHarness.Run("NEGATIVE CONTROL: a display:none subtree is pruned -- its would-otherwise-pass descendant is never individually visited, and the whole subtree is attributed to DisplayNone", delegate
            {
                FakeNode wouldPass = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "wouldPass" };
                FakeNode hiddenChild = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "hiddenChild" };
                hiddenChild.Children.Add(wouldPass);
                FakeNode hiddenRoot = new FakeNode { Frame = Frame(displayNone: true), Width = 10, Height = 10, Name = "hiddenRoot" };
                hiddenRoot.Children.Add(hiddenChild);
                FakeNode visibleSibling = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "visibleSibling" };
                FakeNode root = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "root" };
                root.Children.Add(hiddenRoot);
                root.Children.Add(visibleSibling);

                List<string> visited = new List<string>();
                UiTreeWalker.Stats stats = UiTreeWalker.Walk<FakeNode>(
                    root, true, 100,
                    FakeGetFrame, FakeGetSize, FakeGetChildren, FakeCountSubtree,
                    delegate(FakeNode n, bool onScreen, OnScreenTest.SkipReason reason) { visited.Add(n.Name); });

                // root, hiddenRoot, visibleSibling are individually visited; hiddenChild/wouldPass
                // are NOT -- proving the subtree's reflection was skipped, not just its result
                // discarded after the fact.
                TestHarness.Equal(3, stats.ElementsVisited, "elements individually visited (hiddenChild/wouldPass must not be)");
                TestHarness.False(visited.Contains("hiddenChild"), "hiddenChild must not be individually visited");
                TestHarness.False(visited.Contains("wouldPass"), "wouldPass must not be individually visited -- it would otherwise pass on its own, proving pruning isn't just getting lucky");
                TestHarness.Equal(1, stats.SubtreesPruned, "exactly one subtree pruned (hiddenRoot)");
                TestHarness.Equal(2, stats.PrunedCounts.DisplayNone, "hiddenChild + wouldPass folded into DisplayNone");
                TestHarness.Equal(2, stats.PrunedCounts.Total(), "total pruned count");
            });

            TestHarness.Run("NEGATIVE CONTROL: a hard budget truncates rather than silently returning a short list", delegate
            {
                FakeNode root = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "root" };
                FakeNode cursor = root;
                for (int i = 0; i < 10; i++)
                {
                    FakeNode child = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "n" + i };
                    cursor.Children.Add(child);
                    cursor = child;
                }
                // 11 total elements (root + a 10-deep chain of children), budget of 5.

                int visitCount = 0;
                UiTreeWalker.Stats stats = UiTreeWalker.Walk<FakeNode>(
                    root, true, 5,
                    FakeGetFrame, FakeGetSize, FakeGetChildren, FakeCountSubtree,
                    delegate(FakeNode n, bool onScreen, OnScreenTest.SkipReason reason) { visitCount++; });

                TestHarness.Equal(5, stats.ElementsVisited, "stops exactly at budget");
                TestHarness.Equal(5, visitCount, "visit callback called exactly budget times, not for the rest of the tree");
                TestHarness.True(stats.BudgetTruncated, "must report truncation rather than silently returning a short list");
            });

            TestHarness.Run("budget is not reported truncated when the tree is smaller than the cap", delegate
            {
                FakeNode root = new FakeNode { Frame = Frame(), Width = 10, Height = 10, Name = "root" };
                UiTreeWalker.Stats stats = UiTreeWalker.Walk<FakeNode>(
                    root, true, 100,
                    FakeGetFrame, FakeGetSize, FakeGetChildren, FakeCountSubtree,
                    delegate(FakeNode n, bool onScreen, OnScreenTest.SkipReason reason) { });

                TestHarness.False(stats.BudgetTruncated, "small tree should not be truncated");
                TestHarness.Equal(1, stats.ElementsVisited, "elements visited");
            });
        }
    }
}
