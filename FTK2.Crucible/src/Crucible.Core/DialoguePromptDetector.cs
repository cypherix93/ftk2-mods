using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Detects a "advance the dialogue" prompt for <c>crucible_dialogue_advance</c>. Pure — no game
    /// reference — so it unit-tests with hand-built element lists.
    ///
    /// PROVEN live 2026-08-23 against an actual in-game conversation (Hildegard the Tavern Keeper,
    /// overworld):
    ///   1. <b>In-conversation:</b> a <c>Button</c> named <c>dialogue-container</c> — Focus()+
    ///      NavigationSubmitEvent advances the line. Owning document observed as
    ///      <c>DialogueUIDocument</c>, but this detector does not require a specific document for it
    ///      (only the Button type + name were confirmed load-bearing).
    ///   2. <b>Post-load gate:</b> a <c>Button</c> named <c>continue-label</c> ("Click to Continue")
    ///      SPECIFICALLY inside <c>LoadingUIDocument</c> — a virtual gamepad A press clears it.
    ///      <c>continue-label</c> ALSO appears inside <c>DialogueUIDocument</c>, but there it renders
    ///      as a <c>Label</c>, not a <c>Button</c> (Focus() on it reports focusMoved=false — it is
    ///      not focusable) — so type + document both gate this branch, not the name alone.
    ///
    /// Checked in that priority order: dialogue-container first (the far more common in-conversation
    /// case), then the loading-gate continue-label.
    ///
    /// ASSUMED, NOT live-verified: a document-scoped <c>continue-btn</c> fallback is also checked,
    /// lowest priority, for the case (from an earlier observation session, not reproduced in the
    /// dialogue-container verification pass) where <c>continue-btn</c> was seen focused during
    /// dialogue. Kept behind <see cref="UiForbiddenElements.IsForbidden"/>'s existing document-scoped
    /// rule so it is never selected outside a dialogue document.
    /// </summary>
    public static class DialoguePromptDetector
    {
        /// <summary>In-conversation advance control — PROVEN live.</summary>
        public const string InConversationTargetName = "dialogue-container";

        /// <summary>Post-load "Click to Continue" gate — PROVEN live, only as a Button inside <see cref="LoadingGateDocument"/>.</summary>
        public const string LoadingGateTargetName = "continue-label";
        public const string LoadingGateDocument = "LoadingUIDocument";

        private const string ButtonType = "Button";

        public static bool TryFind(IEnumerable<UiElementInfo> candidates, out UiElementInfo prompt)
        {
            prompt = null;
            if (candidates == null) return false;

            // 1. PROVEN: in-conversation dialogue-container Button.
            foreach (UiElementInfo e in candidates)
            {
                if (e == null || !e.OnScreen) continue;
                if (string.Equals(e.Type, ButtonType, StringComparison.Ordinal)
                    && string.Equals(e.Name, InConversationTargetName, StringComparison.Ordinal))
                {
                    prompt = e;
                    return true;
                }
            }

            // 2. PROVEN: post-load continue-label Button, scoped to LoadingUIDocument only — the
            // same name renders as a non-focusable Label inside DialogueUIDocument and must be
            // ignored there.
            foreach (UiElementInfo e in candidates)
            {
                if (e == null || !e.OnScreen) continue;
                if (string.Equals(e.Type, ButtonType, StringComparison.Ordinal)
                    && string.Equals(e.Name, LoadingGateTargetName, StringComparison.Ordinal)
                    && string.Equals(e.DocumentName, LoadingGateDocument, StringComparison.Ordinal))
                {
                    prompt = e;
                    return true;
                }
            }

            // 3. ASSUMED (not reproduced in the dialogue-container verification pass): document-scoped
            // continue-btn fallback.
            foreach (UiElementInfo e in candidates)
            {
                if (e == null || !e.OnScreen) continue;
                if (!string.Equals(e.Name, "continue-btn", StringComparison.Ordinal)) continue;

                string reason;
                if (!UiForbiddenElements.IsForbidden(e.Name, e.DocumentName, out reason))
                {
                    prompt = e;
                    return true;
                }
            }

            return false;
        }
    }
}
