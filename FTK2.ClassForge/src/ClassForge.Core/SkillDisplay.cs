using System;
using System.Collections.Generic;

namespace ClassForge.Core
{
    /// <summary>
    /// Decides which of a class's <c>Passives</c> deserve a ClassForge-rendered skill row in the
    /// character UI. The game's own panels only render passives that parse into the compiled
    /// <c>eSkills</c> enum (CharacterCustomizationViewHelper.RenderStatsContainer, decompile L1069),
    /// so pack-granted skills — real, functioning recipe passives — are silently invisible without
    /// this. Host-agnostic: the caller supplies "is vanilla-renderable" and "has a display name"
    /// as predicates (the Plugin binds them to <c>Enum.TryParse(typeof(eSkills), …)</c> and the
    /// merged pack localization respectively).
    /// </summary>
    public static class SkillDisplay
    {
        public static List<string> SelectCustomSkillRows(
            IEnumerable<string> passives,
            Func<string, bool> isVanillaSkill,
            Func<string, bool> hasDisplayName)
        {
            var rows = new List<string>();
            if (passives == null) return rows;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var passive in passives)
            {
                if (string.IsNullOrEmpty(passive)) continue;
                if (isVanillaSkill(passive)) continue;
                if (!hasDisplayName(passive)) continue;
                if (seen.Add(passive)) rows.Add(passive);
            }
            return rows;
        }
    }
}
