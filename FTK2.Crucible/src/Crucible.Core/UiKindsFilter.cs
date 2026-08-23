using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Parses/applies <c>crucible_ui_dump</c>'s second "kinds" argument — a comma-separated type
    /// filter (e.g. "Button,Label") layered on top of the existing name/text filter, so a dump of
    /// a busy screen (e.g. 85 identical TemplateContainers) can be narrowed to just the element
    /// kinds worth reading. Pure string logic — no game reference — unit-testable with no game
    /// running.
    ///
    /// "-" (and, explicitly, null/empty) mean "match every kind" — the same convention the
    /// existing name/text filter already uses. An unrecognized kind name is never treated as
    /// "match everything": that would silently defeat the whole point of the filter.
    /// </summary>
    public static class UiKindsFilter
    {
        public static bool Matches(UiElementInfo element, string kinds)
        {
            if (element == null) return false;
            if (string.IsNullOrEmpty(kinds)) return true;

            string trimmed = kinds.Trim();
            if (trimmed.Length == 0 || trimmed == "-") return true;

            string[] parts = trimmed.Split(',');
            foreach (string part in parts)
            {
                string k = part.Trim();
                if (k.Length == 0) continue;
                if (string.Equals(k, element.Type, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
