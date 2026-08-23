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
        }
    }
}
