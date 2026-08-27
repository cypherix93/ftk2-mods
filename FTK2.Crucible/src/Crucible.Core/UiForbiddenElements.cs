using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Names that <c>crucible_ui_press</c>/<c>crucible_dialogue_*</c> must refuse to activate, even
    /// when they are the ONLY match for a selector — pressing them silently resumes the box owner's
    /// live co-op campaign. Pure — no game reference — so it unit-tests with hand-built names,
    /// including negative controls (a forbidden name is refused even as the sole match).
    ///
    /// <c>continue-btn</c> is document-scoped rather than blanket-forbidden: it was observed live
    /// both on the adventure-selection screen (where pressing it resumes the owner's save — forbidden)
    /// and inside an in-progress dialogue (where it is the dialogue's own advance control — allowed).
    /// <c>load-btn</c> has no such legitimate in-dialogue use and is refused unconditionally.
    /// <c>sys-dialog-ok-btn</c>, which also renders "Continue", is a different element name entirely
    /// and is never touched by this list.
    /// </summary>
    public static class UiForbiddenElements
    {
        /// <summary>Owning UIDocument GameObject names inside which <c>continue-btn</c> is a safe, ordinary dialogue-advance control.</summary>
        public static readonly string[] DialogueDocuments = { "DialogueUIDocument", "GameplayDialogUIDocument" };

        /// <summary>
        /// True if <paramref name="elementName"/> must be refused given the element's owning
        /// <paramref name="documentName"/>. <paramref name="reason"/> explains why whenever this
        /// returns true.
        /// </summary>
        public static bool IsForbidden(string elementName, string documentName, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(elementName)) return false;

            // load-game-btn is listed alongside load-btn because the operator's standing constraint
            // names THREE controls (continue-btn / load-btn / load-game-btn) while this guard
            // originally covered two. A gap in a safety list is not a style issue: the whole point
            // is that a caller never has to know which spelling a given screen uses.
            if (string.Equals(elementName, "load-btn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(elementName, "load-game-btn", StringComparison.OrdinalIgnoreCase))
            {
                reason = "'" + elementName + "' resumes the owner's live co-op campaign; refused unconditionally.";
                return true;
            }

            if (string.Equals(elementName, "continue-btn", StringComparison.OrdinalIgnoreCase))
            {
                if (IsDialogueDocument(documentName)) return false;

                reason = "'continue-btn' resumes the owner's live co-op campaign outside a dialogue document (owning doc="
                    + (documentName == null ? "(null)" : "'" + documentName + "'")
                    + "); refused. It is only permitted inside DialogueUIDocument/GameplayDialogUIDocument.";
                return true;
            }

            return false;
        }

        /// <summary>True if <paramref name="documentName"/> is one of the dialogue documents <c>continue-btn</c> is safe inside.</summary>
        public static bool IsDialogueDocument(string documentName)
        {
            if (documentName == null) return false;
            for (int i = 0; i < DialogueDocuments.Length; i++)
            {
                if (string.Equals(documentName, DialogueDocuments[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
