using System.Collections.Generic;

namespace Summoner.Core.Model
{
    /// <summary>
    /// POCO mirror of the game's <c>FollowerCharacterConfig</c>
    /// (decompile: tools/out/decompile/FTK2/FollowerCharacterConfig.cs, 19 fields verbatim).
    /// Enum-backed game fields (Type/Rarity/ContractPrice/Behaviour/Expansion) are held as
    /// <see cref="string"/> here — Core has no game-DLL dependency, so the enum types themselves
    /// stay game-side; <c>Validation/Vocabulary.cs</c> holds the legal string sets and
    /// Summoner.Plugin's ConfigsSink is the only place that calls Enum.TryParse against the real
    /// game enums (design §A4.2).
    /// </summary>
    public sealed class FollowerEntry
    {
        /// <summary>eCharacterTypes as a string. Legal set for M0 follower packs: Vocabulary.FollowerTypes.</summary>
        public string Type { get; set; }

        /// <summary>Points at a vanilla (or the pack's own) Characters.json entry. Never rewritten.</summary>
        public string ConfigName { get; set; }

        /// <summary>EOR's own invariant: ClassName == the entry's own key. Never diverges in M0 output.</summary>
        public string ClassName { get; set; }

        public int ContractRounds { get; set; }
        public int MinTier { get; set; }
        public int MaxTier { get; set; }

        /// <summary>eItemRarities as a string. Legal set: Vocabulary.ItemRarities.</summary>
        public string Rarity { get; set; }

        /// <summary>eLootScales as a string. Legal set: Vocabulary.LootScales.</summary>
        public string ContractPrice { get; set; }

        /// <summary>eAiBehaviours as a string. Legal set: Vocabulary.AiBehaviours.</summary>
        public string Behaviour { get; set; }

        /// <summary>Vanilla loc key, resolved by the game's own Langs. Never synthesized/rewritten by the loader.</summary>
        public string Subtitle { get; set; }

        /// <summary>Vanilla loc key. Never synthesized/rewritten by the loader.</summary>
        public string JoinParty { get; set; }

        /// <summary>Vanilla loc key. Never synthesized/rewritten by the loader.</summary>
        public string LeaveParty { get; set; }

        /// <summary>Decompile type is <c>string</c>, not bool — preserved verbatim.</summary>
        public string Rescued { get; set; }

        /// <summary>Decompile type is <c>string</c>, not bool — preserved verbatim.</summary>
        public string GiveDeed { get; set; }

        public List<string> Tags { get; set; } = new List<string>();

        public string CaravanStatID { get; set; }
        public int CaravanStatValue { get; set; }

        /// <summary>eCharacterStats keys as strings. Legal key set: Vocabulary.CharacterStats.</summary>
        public Dictionary<string, int> AppendableStats { get; set; } = new Dictionary<string, int>();

        /// <summary>eExpansions as a string. Not gated by the M0 PackValidator (no ground-truth list required by design).</summary>
        public string Expansion { get; set; }
    }
}
