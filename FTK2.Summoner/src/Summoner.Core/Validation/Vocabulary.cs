using System.Collections.Generic;

namespace Summoner.Core.Validation
{
    /// <summary>
    /// Legal token sets for the follower/character schema's free-string-mirrored enum fields.
    /// Copied verbatim from docs/research/enum-ground-truth.md, itself decompile-verified against
    /// tools/out/decompile/FTK2/&lt;File&gt;.cs (design §A4.1). Every set below names its source file.
    /// </summary>
    public static class Vocabulary
    {
        /// <summary>eCharacterTypes.cs, 8 members incl. NONE (enum-ground-truth.md §11).</summary>
        public static readonly HashSet<string> CharacterTypes = new HashSet<string>
        {
            "NONE", "STANDARD", "PROP", "FORCED_FIGHT", "MERCENARY", "CURSE", "COMPANION", "BOSS", "INANIMATE"
        };

        /// <summary>
        /// M0 restriction (design §B4.2, decision restated at §A4.1): a FollowerCharacterConfig's
        /// Type must be COMPANION or MERCENARY for M0 packs even though eCharacterTypes has more
        /// legal members -- CURSE/INANIMATE/etc. are Characters.json-only types or out of M0 scope.
        /// </summary>
        public static readonly HashSet<string> FollowerTypes = new HashSet<string> { "COMPANION", "MERCENARY" };

        /// <summary>eLootScales.cs, 9 members incl. NONE (enum-ground-truth.md §11).</summary>
        public static readonly HashSet<string> LootScales = new HashSet<string>
        {
            "NONE", "TINY", "VERY_LOW", "LOW", "AVERAGE", "HIGH", "VERY_HIGH", "HUGE", "MASSIVE"
        };

        /// <summary>eAiBehaviours.cs, 5 members (enum-ground-truth.md §11).</summary>
        public static readonly HashSet<string> AiBehaviours = new HashSet<string>
        {
            "DEFAULT", "DPS", "SUPPORT", "TANK", "CURSE"
        };

        /// <summary>eItemRarities.cs, 13 non-NONE members + NONE = 14 tokens (enum-ground-truth.md §2).</summary>
        public static readonly HashSet<string> ItemRarities = new HashSet<string>
        {
            "NONE", "COMMON", "UNCOMMON", "RARE", "ARTIFACT", "QUEST", "LORE", "SKIN",
            "LOCKED", "MERCENARY", "CHARACTER", "LOCATION", "MYSTERY", "SEASONAL"
        };

        /// <summary>
        /// eCharacterStats.cs (enum-ground-truth.md §6). The source doc's prose says "53 members"
        /// but its own verbatim listing enumerates 51 tokens -- this set reproduces that verbatim
        /// listing exactly (51 entries); it is the authoritative decompile-sourced list, not the
        /// prose count.
        /// </summary>
        public static readonly HashSet<string> CharacterStats = new HashSet<string>
        {
            "RND", "NONE", "STR", "VIT", "INT", "AWR", "TAL", "SPD", "LCK", "DEF", "RES", "EVD",
            "PA", "SA", "MXFOC", "FOC", "MXHP", "HP", "XP", "ACC", "ATK", "MAG", "PHY", "PRW",
            "CRT", "CRTD", "GLD", "XPM", "SKL", "HRG", "LOP", "THRN", "MOV", "LBM", "WBM", "FIND",
            "PSTR", "PVIT", "PINT", "PAWR", "PTAL", "PSPD", "PLCK", "PDEF", "PRES", "PEVD", "PFOC",
            "PGLD", "DAM", "PARTY_XP", "CURRENT_FOCUS"
        };
    }
}
