using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    internal static class UiCommandTests
    {
        private static UiElementInfo El(string type, string name, string text, bool visible = true, bool enabled = true, bool focused = false)
        {
            return new UiElementInfo(type, name, text, visible, enabled, focused);
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
        }
    }
}
