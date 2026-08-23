using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Enumerates dialogue OPTIONS (as opposed to a plain continue prompt) for
    /// <c>crucible_dialogue_choose</c>. Pure — no game reference — so it unit-tests with hand-built
    /// element lists.
    ///
    /// ASSUMED, needs live verification: no dialogue-choice element name/kind was ever observed live
    /// tonight (only the continue prompt was), so this deliberately does NOT hardcode option element
    /// names. Instead it defines "an option" structurally: any on-screen element belonging to a
    /// dialogue document (<see cref="UiForbiddenElements.DialogueDocuments"/>) that is NOT one of the
    /// known continue-prompt elements (<see cref="DialoguePromptDetector"/>'s targets). Selection
    /// among the resulting list is then selector-driven via <see cref="UiPressMatcher"/>, not
    /// hardcoded to any specific option name — this is the part that most needs a live check.
    /// </summary>
    public static class DialogueOptions
    {
        private static readonly string[] ContinuePromptNames = { "continue-label", "continue-btn", DialoguePromptDetector.InConversationTargetName };

        public static List<UiElementInfo> ListOptions(IEnumerable<UiElementInfo> candidates)
        {
            List<UiElementInfo> options = new List<UiElementInfo>();
            if (candidates == null) return options;

            foreach (UiElementInfo e in candidates)
            {
                if (e == null || !e.OnScreen) continue;
                if (!UiForbiddenElements.IsDialogueDocument(e.DocumentName)) continue;
                if (IsContinuePromptName(e.Name)) continue;
                options.Add(e);
            }

            return options;
        }

        private static bool IsContinuePromptName(string name)
        {
            if (name == null) return false;
            for (int i = 0; i < ContinuePromptNames.Length; i++)
            {
                if (string.Equals(name, ContinuePromptNames[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
