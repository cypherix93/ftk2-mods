using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    /// <summary>
    /// Tests for the SPEC S3 <c>crucible_ui_press</c>/<c>crucible_dialogue_*</c> pure logic:
    /// <see cref="UiForbiddenElements"/>, <see cref="UiPressMatcher"/>, <see cref="DialogueAdvanceBudget"/>,
    /// <see cref="DialoguePromptDetector"/>, <see cref="DialogueOptions"/>.
    /// </summary>
    internal static class UiPressTests
    {
        private static UiElementInfo ElOnScreen(string type, string name, string text, string docName = null)
        {
            return new UiElementInfo(type, name, text, true, true, false, true, OnScreenTest.SkipReason.None, docName);
        }

        internal static void RunAll()
        {
            TestHarness.Section("UiForbiddenElements.IsForbidden");

            TestHarness.Run("load-btn is forbidden unconditionally, even with no document", delegate
            {
                string reason;
                bool forbidden = UiForbiddenElements.IsForbidden("load-btn", null, out reason);
                TestHarness.True(forbidden, "load-btn must always be forbidden");
                TestHarness.True(reason != null, "a forbidden result must explain why");
            });

            TestHarness.Run("continue-btn is forbidden outside a dialogue document (e.g. AdventureSelectionUIDocument)", delegate
            {
                string reason;
                bool forbidden = UiForbiddenElements.IsForbidden("continue-btn", "AdventureSelectionUIDocument", out reason);
                TestHarness.True(forbidden, "continue-btn on the adventure-selection screen resumes the owner's save and must be refused");
                TestHarness.True(reason != null, "a forbidden result must explain why");
            });

            TestHarness.Run("NEGATIVE CONTROL: continue-btn is allowed inside DialogueUIDocument", delegate
            {
                string reason;
                bool forbidden = UiForbiddenElements.IsForbidden("continue-btn", "DialogueUIDocument", out reason);
                TestHarness.False(forbidden, "continue-btn inside a dialogue document is the dialogue's own advance control, not the co-op resume");
            });

            TestHarness.Run("NEGATIVE CONTROL: continue-btn is allowed inside GameplayDialogUIDocument", delegate
            {
                string reason;
                bool forbidden = UiForbiddenElements.IsForbidden("continue-btn", "GameplayDialogUIDocument", out reason);
                TestHarness.False(forbidden, "GameplayDialogUIDocument is also a dialogue document");
            });

            TestHarness.Run("sys-dialog-ok-btn is never forbidden (different element entirely, must not be blocked)", delegate
            {
                string reason;
                bool forbidden = UiForbiddenElements.IsForbidden("sys-dialog-ok-btn", "SystemDialogUIDocument", out reason);
                TestHarness.False(forbidden, "sys-dialog-ok-btn is a distinct, always-safe control");
            });

            TestHarness.Run("an ordinary button name is never forbidden", delegate
            {
                string reason;
                bool forbidden = UiForbiddenElements.IsForbidden("back-btn", "LobbyPanel", out reason);
                TestHarness.False(forbidden, "back-btn is not on the forbidden list");
            });

            TestHarness.Section("UiPressMatcher.Find");

            TestHarness.Run("NEGATIVE CONTROL: a forbidden name is refused even when it is the ONLY match", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "load-btn", "Load Game", "AdventureSelectionUIDocument"),
                };
                string error;
                UiPressMatcher.Result r = UiPressMatcher.Find(candidates, "load", out error);
                TestHarness.True(r.Found, "the selector does match the element");
                TestHarness.True(r.Forbidden, "the sole match must still be refused, not silently activated");
                TestHarness.True(r.ForbiddenReason != null, "a refusal must explain why");
            });

            TestHarness.Run("NEGATIVE CONTROL: exact match wins over an earlier-encountered substring match (literal back-btn vs backpack-button)", delegate
            {
                // backpack-button is listed FIRST, matching a first-match-wins strategy's wrong pick
                // observed live: selector 'Back' matched backpack-button before back-btn.
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "backpack-button", "Backpack", "LobbyPanel"),
                    ElOnScreen("Button", "back-btn", "Back", "LobbyPanel"),
                };
                string error;
                UiPressMatcher.Result r = UiPressMatcher.Find(candidates, "Back", out error);
                TestHarness.True(r.Found, "should find a match");
                TestHarness.Equal("back-btn", r.Match.Name, "the EXACT text match ('Back') must win over the substring-only match (backpack-button), even though backpack-button was encountered first");
                TestHarness.Equal(1, r.OtherMatches.Count, "backpack-button should still be reported as another (substring) match");
            });

            TestHarness.Run("NEGATIVE CONTROL: a selector matching nothing reports no-match and lists what IS on screen", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "back-btn", "Back", "LobbyPanel"),
                    ElOnScreen("Button", "settings-btn", "Settings", "LobbyPanel"),
                };
                string error;
                UiPressMatcher.Result r = UiPressMatcher.Find(candidates, "nonexistent-selector", out error);
                TestHarness.False(r.Found, "no element should match");
                TestHarness.Equal(2, r.AllVisible.Count, "the on-screen elements that DO exist must still be listed for diagnosability");
            });

            TestHarness.Run("an off-screen element is never selected even on an exact text match", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    new UiElementInfo("Button", "back-btn", "Back", true, true, false, false, OnScreenTest.SkipReason.DisplayNone, "HiddenPanel"),
                };
                string error;
                UiPressMatcher.Result r = UiPressMatcher.Find(candidates, "Back", out error);
                TestHarness.False(r.Found, "off-screen match must not count as found");
                TestHarness.Equal(0, r.AllVisible.Count, "off-screen element is not even listed as a candidate");
            });

            TestHarness.Run("a non-forbidden, unambiguous match reports Found with no forbidden flag", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "settings-btn", "Settings", "LobbyPanel"),
                };
                string error;
                UiPressMatcher.Result r = UiPressMatcher.Find(candidates, "settings", out error);
                TestHarness.True(r.Found, "should match");
                TestHarness.False(r.Forbidden, "an ordinary button must not be refused");
            });

            TestHarness.Section("DialogueAdvanceBudget.TryParse");

            TestHarness.Run("rejects a non-integer", delegate
            {
                int maxPresses; bool clamped; string error;
                bool ok = DialogueAdvanceBudget.TryParse("abc", out maxPresses, out clamped, out error);
                TestHarness.False(ok, "non-integer must be rejected");
            });

            TestHarness.Run("rejects zero and negative", delegate
            {
                int maxPresses; bool clamped; string error;
                TestHarness.False(DialogueAdvanceBudget.TryParse("0", out maxPresses, out clamped, out error), "zero must be rejected");
                TestHarness.False(DialogueAdvanceBudget.TryParse("-5", out maxPresses, out clamped, out error), "negative must be rejected");
            });

            TestHarness.Run("clamps above MaxPresses rather than accepting an unbounded value", delegate
            {
                int maxPresses; bool clamped; string error;
                bool ok = DialogueAdvanceBudget.TryParse("999", out maxPresses, out clamped, out error);
                TestHarness.True(ok, "should parse");
                TestHarness.True(clamped, "should report clamped");
                TestHarness.Equal(DialogueAdvanceBudget.MaxPresses, maxPresses, "clamped to the cap");
            });

            TestHarness.Run("accepts an in-range positive value unchanged", delegate
            {
                int maxPresses; bool clamped; string error;
                bool ok = DialogueAdvanceBudget.TryParse("10", out maxPresses, out clamped, out error);
                TestHarness.True(ok, "should parse");
                TestHarness.False(clamped, "should not be clamped");
                TestHarness.Equal(10, maxPresses, "value preserved");
            });

            TestHarness.Section("DialoguePromptDetector.TryFind (PROVEN targets: dialogue-container Button, continue-label Button in LoadingUIDocument)");

            TestHarness.Run("finds an in-conversation dialogue-container Button", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "dialogue-container", "Hildegard: Welcome, traveler.", "DialogueUIDocument"),
                };
                UiElementInfo prompt;
                bool found = DialoguePromptDetector.TryFind(candidates, out prompt);
                TestHarness.True(found, "should find the dialogue-container prompt");
                TestHarness.Equal("dialogue-container", prompt.Name, "matched the right element");
            });

            TestHarness.Run("finds the post-load continue-label ONLY as a Button inside LoadingUIDocument", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "continue-label", "Click to Continue", "LoadingUIDocument"),
                };
                UiElementInfo prompt;
                bool found = DialoguePromptDetector.TryFind(candidates, out prompt);
                TestHarness.True(found, "should find the loading-gate prompt");
                TestHarness.Equal("LoadingUIDocument", prompt.DocumentName, "matched the loading gate, not a dialogue-document lookalike");
            });

            TestHarness.Run("NEGATIVE CONTROL: continue-label as a Label (not a Button) inside DialogueUIDocument is never treated as a pressable prompt", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Label", "continue-label", "Click to Continue", "DialogueUIDocument"),
                };
                UiElementInfo prompt;
                bool found = DialoguePromptDetector.TryFind(candidates, out prompt);
                TestHarness.False(found, "a non-focusable Label variant of continue-label must not be selected as a prompt (proven live: Focus() reports focusMoved=false on it)");
            });

            TestHarness.Run("prefers dialogue-container over the loading-gate continue-label when both are somehow present", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "continue-label", "Click to Continue", "LoadingUIDocument"),
                    ElOnScreen("Button", "dialogue-container", "...", "DialogueUIDocument"),
                };
                UiElementInfo prompt;
                bool found = DialoguePromptDetector.TryFind(candidates, out prompt);
                TestHarness.True(found, "should find a prompt");
                TestHarness.Equal("dialogue-container", prompt.Name, "in-conversation target takes priority");
            });

            TestHarness.Run("no prompt on screen reports not-found", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "back-btn", "Back", "LobbyPanel"),
                };
                UiElementInfo prompt;
                bool found = DialoguePromptDetector.TryFind(candidates, out prompt);
                TestHarness.False(found, "an unrelated button must not be mistaken for a dialogue prompt");
            });

            TestHarness.Section("DialogueOptions.ListOptions");

            TestHarness.Run("lists an on-screen dialogue-document button that isn't a known continue prompt as an option", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "dialogue-choice-1", "Ask about the tavern", "DialogueUIDocument"),
                    ElOnScreen("Button", "dialogue-container", "...", "DialogueUIDocument"),
                };
                List<UiElementInfo> options = DialogueOptions.ListOptions(candidates);
                TestHarness.Equal(1, options.Count, "only the non-continue-prompt element counts as an option");
                TestHarness.Equal("dialogue-choice-1", options[0].Name, "the right option was listed");
            });

            TestHarness.Run("excludes buttons outside a dialogue document entirely", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "back-btn", "Back", "LobbyPanel"),
                };
                List<UiElementInfo> options = DialogueOptions.ListOptions(candidates);
                TestHarness.Equal(0, options.Count, "a lobby button is never a dialogue option");
            });

            TestHarness.Run("NEGATIVE CONTROL: choosing with a selector that matches no option must not pick something arbitrary", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "dialogue-choice-1", "Ask about the tavern", "DialogueUIDocument"),
                    ElOnScreen("Button", "dialogue-choice-2", "Leave", "DialogueUIDocument"),
                };
                List<UiElementInfo> options = DialogueOptions.ListOptions(candidates);
                string error;
                UiPressMatcher.Result r = UiPressMatcher.Find(options, "nonexistent-choice", out error);
                TestHarness.False(r.Found, "a selector matching no option must report not-found, not pick one arbitrarily");
                TestHarness.Equal(2, r.AllVisible.Count, "both real options must still be listed so the caller can pick");
            });

            TestHarness.Run("choosing an exact option among substring collisions picks the exact one", delegate
            {
                List<UiElementInfo> candidates = new List<UiElementInfo>
                {
                    ElOnScreen("Button", "dialogue-choice-1", "Ask about the tavern's history", "DialogueUIDocument"),
                    ElOnScreen("Button", "dialogue-choice-2", "Ask", "DialogueUIDocument"),
                };
                List<UiElementInfo> options = DialogueOptions.ListOptions(candidates);
                string error;
                UiPressMatcher.Result r = UiPressMatcher.Find(options, "Ask", out error);
                TestHarness.True(r.Found, "should find a match");
                TestHarness.Equal("dialogue-choice-2", r.Match.Name, "the exact text match ('Ask') must win over the substring match");
            });
        }
    }
}
