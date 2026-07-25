using System.Collections.Generic;

namespace Summoner.Core.Model
{
    /// <summary>
    /// POCO mirror of the game's <c>CharacterConfig</c>
    /// (decompile: tools/out/decompile/FTK2/CharacterConfig.cs, 14 fields verbatim).
    /// Only used for the <c>characters.json</c> slot declared in the pack format (design §A2.1):
    /// EOR's 249 followers all reuse vanilla ConfigNames, so no shipped M0 pack populates this,
    /// but a future original-creature pack (or an override pack) needs somewhere to put the
    /// CharacterConfig so ConfigName resolution doesn't dangle.
    /// </summary>
    public sealed class CharacterEntry
    {
        /// <summary>Decompile type is SerializedSortedDictionary&lt;string,int&gt; game-side; Core mirrors as Dictionary.</summary>
        public Dictionary<string, int> Stats { get; set; } = new Dictionary<string, int>();

        /// <summary>Decompile type is SerializedSortedDictionary&lt;string,int&gt; game-side (item id -> quantity).</summary>
        public Dictionary<string, int> Things { get; set; } = new Dictionary<string, int>();

        public List<string> Passives { get; set; } = new List<string>();
        public string CampQuery { get; set; }
        public string SwarmQuery { get; set; }
        public string LootID { get; set; }
        public string LocKey { get; set; }

        /// <summary>eItemRarities as a string. Legal set: Vocabulary.ItemRarities.</summary>
        public string Rarity { get; set; }

        public int Level { get; set; }
        public int Threat { get; set; }
        public string BaseType { get; set; }
        public string DefaultBodyType { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string OnDeathAbility { get; set; }

        /// <summary>eExpansions as a string. Not gated by the M0 PackValidator.</summary>
        public string Expansion { get; set; }
    }
}
