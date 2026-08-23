namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Pure snapshot of one UIToolkit <c>VisualElement</c> — no game/Unity reference, just the
    /// fields <c>crucible_ui_dump</c>/<c>crucible_ui_click</c> need to render and match against.
    /// The plugin reads these off the live tree via reflection and hands the resulting list to
    /// <see cref="UiTreeRenderer"/>/<see cref="UiSelectorMatcher"/>, which are unit-testable with no
    /// game running.
    /// </summary>
    public sealed class UiElementInfo
    {
        public readonly string Type;
        public readonly string Name;
        public readonly string Text;
        public readonly bool Visible;
        public readonly bool Enabled;
        public readonly bool Focused;

        /// <summary>
        /// The real on-screen decision (SPEC S3 UI true-visibility) — <see cref="OnScreenTest.IsOnScreen"/>
        /// folded in the owning document's active state plus the full ancestor chain's visibility,
        /// display, and opacity, and the element's own worldBound size. <see cref="UiTreeRenderer"/>
        /// and <see cref="UiSelectorMatcher"/> gate on THIS, not <see cref="Visible"/>.
        /// </summary>
        public readonly bool OnScreen;

        /// <summary>Why <see cref="OnScreen"/> is false; <see cref="OnScreenTest.SkipReason.None"/> when it's true.</summary>
        public readonly OnScreenTest.SkipReason SkipReason;

        /// <summary>Name of the owning UIDocument's GameObject, so callers can tell which panel a button belongs to when names collide. Null if unknown.</summary>
        public readonly string DocumentName;

        /// <summary>
        /// Legacy constructor kept for existing callers/tests: <paramref name="visible"/> alone
        /// stands in for the full on-screen decision (matches the pre-S3 behavior where
        /// <c>Visible</c> was the only gate), so tests built against this constructor keep passing
        /// unchanged.
        /// </summary>
        public UiElementInfo(string type, string name, string text, bool visible, bool enabled, bool focused)
            : this(type, name, text, visible, enabled, focused, visible, visible ? OnScreenTest.SkipReason.None : OnScreenTest.SkipReason.HiddenAncestor, null)
        {
        }

        public UiElementInfo(string type, string name, string text, bool visible, bool enabled, bool focused,
            bool onScreen, OnScreenTest.SkipReason skipReason, string documentName)
        {
            Type = type;
            Name = name;
            Text = text;
            Visible = visible;
            Enabled = enabled;
            Focused = focused;
            OnScreen = onScreen;
            SkipReason = skipReason;
            DocumentName = documentName;
        }
    }
}
