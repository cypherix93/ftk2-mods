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

        public UiElementInfo(string type, string name, string text, bool visible, bool enabled, bool focused)
        {
            Type = type;
            Name = name;
            Text = text;
            Visible = visible;
            Enabled = enabled;
            Focused = focused;
        }
    }
}
