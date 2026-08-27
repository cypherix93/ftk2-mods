using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    /// <summary>
    /// Tests for <see cref="UiIndexedMatcher"/> — the pure logic behind
    /// <c>crucible_ui_click_nth</c>/<c>crucible_ui_matches</c>: index selection among same-named
    /// text-less elements, document scoping, a LOUD out-of-range refusal (never a silent fallback to
    /// index 0), and — the safety property — the <see cref="UiForbiddenElements"/> check firing
    /// through this new path exactly as it does through <c>crucible_ui_click</c>.
    /// </summary>
    internal static class UiIndexedMatcherTests
    {
        private static UiElementInfo El(string name, string text, string docName)
        {
            return new UiElementInfo("Button", name, text, true, true, false, true, OnScreenTest.SkipReason.None, docName);
        }

        private static UiElementInfo OffScreen(string name, string docName)
        {
            return new UiElementInfo("Button", name, "", true, true, false, false, OnScreenTest.SkipReason.HiddenAncestor, docName);
        }

        /// <summary>The live case this verb exists for: nine identically-named, text-less carousel entries.</summary>
        private static List<UiElementInfo> Carousel()
        {
            List<UiElementInfo> list = new List<UiElementInfo>();
            for (int i = 0; i < 9; i++) list.Add(El("adventure-art-holder", "", "AdventureSelectionUIDocument"));
            return list;
        }

        internal static void RunAll()
        {
            TestHarness.Section("UiIndexedMatcher — index selection");

            TestHarness.Run("index selects the Nth match among nine same-named, text-less elements", delegate
            {
                List<UiElementInfo> carousel = Carousel();
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(carousel, "adventure-art-holder", "-", 4, out error);
                TestHarness.True(error == null, "no error expected");
                TestHarness.True(r.Found, "index 4 of 9 must be found");
                TestHarness.Equal(9, r.MatchCount, "all nine must be reported as matches");
                TestHarness.Equal(4, r.Index, "the requested index must be echoed back");
                TestHarness.True(ReferenceEquals(r.Match, carousel[4]), "the element at index 4 must be the one chosen, not the first");
            });

            TestHarness.Run("index 0 selects the first match (parity with crucible_ui_click)", delegate
            {
                List<UiElementInfo> carousel = Carousel();
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(carousel, "adventure-art-holder", "-", 0, out error);
                TestHarness.True(r.Found, "index 0 must be found");
                TestHarness.True(ReferenceEquals(r.Match, carousel[0]), "index 0 is the first match");
            });

            TestHarness.Run("last valid index (count-1) is selectable", delegate
            {
                List<UiElementInfo> carousel = Carousel();
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(carousel, "adventure-art-holder", "-", 8, out error);
                TestHarness.True(r.Found, "index 8 of 9 must be found");
                TestHarness.True(ReferenceEquals(r.Match, carousel[8]), "index 8 is the ninth element");
            });

            TestHarness.Run("off-screen elements are excluded from the INDEX SPACE, not just from selection", delegate
            {
                List<UiElementInfo> list = new List<UiElementInfo>();
                list.Add(OffScreen("adventure-art-holder", "AdventureSelectionUIDocument"));
                UiElementInfo first = El("adventure-art-holder", "", "AdventureSelectionUIDocument");
                UiElementInfo second = El("adventure-art-holder", "", "AdventureSelectionUIDocument");
                list.Add(first);
                list.Add(second);

                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(list, "adventure-art-holder", "-", 0, out error);
                TestHarness.Equal(2, r.MatchCount, "the off-screen element must not occupy an index");
                TestHarness.True(ReferenceEquals(r.Match, first), "index 0 must be the first ON-SCREEN match");
            });

            TestHarness.Section("UiIndexedMatcher — out-of-range is LOUD, never a fallback to 0");

            TestHarness.Run("index past the last match reports OutOfRange and selects NOTHING", delegate
            {
                List<UiElementInfo> carousel = Carousel();
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(carousel, "adventure-art-holder", "-", 9, out error);
                TestHarness.True(error == null, "out-of-range is a result state, not a thrown/arg error");
                TestHarness.False(r.Found, "an out-of-range index must NOT be found");
                TestHarness.True(r.OutOfRange, "it must be flagged as out of range so the caller can say so loudly");
                TestHarness.True(r.Match == null, "SILENT-FALLBACK GUARD: no element may be selected on an out-of-range index");
                TestHarness.Equal(9, r.MatchCount, "the real match count must still be reported so the caller can correct the index");
            });

            TestHarness.Run("a wildly out-of-range index still refuses rather than clamping", delegate
            {
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(Carousel(), "adventure-art-holder", "-", 999, out error);
                TestHarness.False(r.Found, "999 must not be found");
                TestHarness.True(r.OutOfRange, "999 must be flagged out of range");
                TestHarness.True(r.Match == null, "no clamping to the last element either");
            });

            TestHarness.Run("no match at all is reported as not-found and NOT as out-of-range", delegate
            {
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(Carousel(), "no-such-element", "-", 0, out error);
                TestHarness.False(r.Found, "nothing matches");
                TestHarness.False(r.OutOfRange, "a zero-match selector is a different failure from a bad index");
                TestHarness.Equal(0, r.MatchCount, "zero matches");
                TestHarness.Equal(9, r.AllVisible.Count, "what IS on screen must still be reported for diagnosis");
            });

            TestHarness.Run("a negative index is an error, never index 0", delegate
            {
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(Carousel(), "adventure-art-holder", "-", -1, out error);
                TestHarness.True(error != null, "a negative index must be an explicit error");
                TestHarness.False(r.Found, "nothing may be selected");
            });

            TestHarness.Section("UiIndexedMatcher.TryParseIndex — never silently defaults");

            TestHarness.Run("a plain integer parses", delegate
            {
                int index; string error;
                TestHarness.True(UiIndexedMatcher.TryParseIndex("3", out index, out error), "'3' must parse");
                TestHarness.Equal(3, index, "'3' is 3");
            });

            TestHarness.Run("a non-numeric index is refused, not coerced to 0", delegate
            {
                int index; string error;
                TestHarness.False(UiIndexedMatcher.TryParseIndex("second", out index, out error), "'second' must be refused");
                TestHarness.True(error != null, "the refusal must explain itself");
            });

            TestHarness.Run("an empty/missing index is refused, not coerced to 0", delegate
            {
                int index; string error;
                TestHarness.False(UiIndexedMatcher.TryParseIndex("", out index, out error), "an empty index must be refused");
                TestHarness.False(UiIndexedMatcher.TryParseIndex(null, out index, out error), "a null index must be refused");
            });

            TestHarness.Run("a negative index string is refused", delegate
            {
                int index; string error;
                TestHarness.False(UiIndexedMatcher.TryParseIndex("-2", out index, out error), "'-2' must be refused");
            });

            TestHarness.Section("UiIndexedMatcher — document scoping");

            TestHarness.Run("a document filter narrows the match set and renumbers the indices", delegate
            {
                List<UiElementInfo> list = new List<UiElementInfo>();
                list.Add(El("next-btn", "", "AdventureSelectionUIDocument"));
                UiElementInfo other = El("next-btn", "Select Adventure", "AdventureDetailUIDocument");
                list.Add(other);

                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(list, "next-btn", "AdventureDetailUIDocument", 0, out error);
                TestHarness.Equal(1, r.MatchCount, "only the detail document's next-btn is in scope");
                TestHarness.True(ReferenceEquals(r.Match, other), "the document-scoped match must be the one chosen");
            });

            TestHarness.Run("document matching is exact and case-insensitive, never a substring", delegate
            {
                TestHarness.True(UiIndexedMatcher.DocumentMatches("MainMenuUIDocument", "mainmenuuidocument"), "case-insensitive exact must match");
                TestHarness.False(UiIndexedMatcher.DocumentMatches("MainMenuUIDocument", "MainMenu"), "a prefix must NOT match — document scoping is identity, not a filter");
                TestHarness.True(UiIndexedMatcher.DocumentMatches("MainMenuUIDocument", "-"), "'-' means any document");
                TestHarness.True(UiIndexedMatcher.DocumentMatches(null, "-"), "'-' matches even an unknown document");
                TestHarness.False(UiIndexedMatcher.DocumentMatches(null, "MainMenuUIDocument"), "an unknown document cannot satisfy a named scope");
            });

            TestHarness.Section("UiIndexedMatcher — FORBIDDEN-ELEMENT REFUSAL through the indexed path (the safety property)");

            TestHarness.Run("SAFETY: continue-btn reached by index on the adventure-selection screen is flagged forbidden", delegate
            {
                List<UiElementInfo> list = new List<UiElementInfo>();
                list.Add(El("campaign-btn", "", "AdventureSelectionUIDocument"));
                list.Add(El("continue-btn", "Continue", "AdventureSelectionUIDocument"));

                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(list, "continue-btn", "-", 0, out error);
                TestHarness.True(r.Found, "the element is found...");
                TestHarness.True(r.Forbidden, "...and MUST be flagged forbidden — pressing it resumes the owner's live co-op campaign");
                TestHarness.True(r.ForbiddenReason != null, "the refusal must explain why");
            });

            TestHarness.Run("SAFETY: load-btn reached by index is flagged forbidden even as the sole match", delegate
            {
                List<UiElementInfo> list = new List<UiElementInfo>();
                list.Add(El("load-btn", "", "AdventureSelectionUIDocument"));

                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(list, "load-btn", "-", 0, out error);
                TestHarness.True(r.Found, "sole match is found...");
                TestHarness.True(r.Forbidden, "...and still refused — load-btn is forbidden unconditionally");
            });

            TestHarness.Run("SAFETY: load-game-btn is forbidden too -- the guard covers all THREE named controls", delegate
            {
                // The operator's standing constraint names continue-btn / load-btn / load-game-btn.
                // The guard originally covered only the first two, so a screen spelling it
                // 'load-game-btn' would have sailed straight through. A gap in a safety list is the
                // whole failure: a caller should never have to know which spelling a screen uses.
                List<UiElementInfo> list = new List<UiElementInfo>();
                list.Add(El("load-game-btn", "Load Game", "AdventureSelectionUIDocument"));

                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(list, "load-game-btn", "-", 0, out error);
                TestHarness.True(r.Found, "load-game-btn is matched...");
                TestHarness.True(r.Forbidden, "...and MUST be refused, exactly like load-btn");
                TestHarness.True(r.ForbiddenReason != null && r.ForbiddenReason.Contains("load-game-btn"),
                    "the refusal must name the element that was actually refused");
            });

            TestHarness.Run("SAFETY: an index that lands on a forbidden element mid-list is flagged, not skipped past", delegate
            {
                List<UiElementInfo> list = new List<UiElementInfo>();
                list.Add(El("adventure-art-holder", "", "AdventureSelectionUIDocument"));
                list.Add(El("continue-btn", "Continue", "AdventureSelectionUIDocument"));
                list.Add(El("adventure-art-holder", "", "AdventureSelectionUIDocument"));

                string error;
                // A substring selector that sweeps the whole screen, the shape that caused the
                // original incident: index 1 lands squarely on continue-btn.
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(list, "-btn", "-", 0, out error);
                TestHarness.Equal(1, r.MatchCount, "'-btn' matches only continue-btn here");
                TestHarness.True(r.Forbidden, "the indexed path must refuse it exactly as crucible_ui_click does");
            });

            TestHarness.Run("SAFETY NEGATIVE CONTROL: continue-btn inside a dialogue document is NOT flagged", delegate
            {
                List<UiElementInfo> list = new List<UiElementInfo>();
                list.Add(El("continue-btn", "Continue", "DialogueUIDocument"));

                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(list, "continue-btn", "-", 0, out error);
                TestHarness.True(r.Found, "found inside a dialogue");
                TestHarness.False(r.Forbidden, "inside a dialogue document continue-btn is the dialogue's own advance control");
            });

            TestHarness.Run("NEGATIVE CONTROL: an ordinary indexed match is not flagged forbidden", delegate
            {
                string error;
                UiIndexedMatcher.Result r = UiIndexedMatcher.Find(Carousel(), "adventure-art-holder", "-", 3, out error);
                TestHarness.True(r.Found, "found");
                TestHarness.False(r.Forbidden, "adventure-art-holder is an ordinary, safe element");
                TestHarness.True(r.ForbiddenReason == null, "no reason when not forbidden");
            });
        }
    }
}
