using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Summoner.Core.Diagnostics;
using Summoner.Core.Packs;

namespace Summoner.Core.Validation
{
    /// <summary>
    /// Pure content validator (design §A4.1). Distinct from MergePlanner's Add/Skip/Reject gate --
    /// this is the broader QA pass that produces Findings for diagnostics/CI regardless of whether
    /// an entry will ultimately merge.
    /// </summary>
    public static class PackValidator
    {
        public static readonly Regex IdPattern = new Regex("^SMN_[A-Z0-9_]+$", RegexOptions.Compiled);

        /// <summary>
        /// Validates one pack. <paramref name="externalCharacterIds"/> is the live Configs.Characters
        /// key set (or an empty set when validating offline); it is unioned with the pack's own
        /// characters.json ids before ConfigName resolution, per design §A4.1's "the pack's own
        /// characters.json ids ∪ the live Configs.Characters keys".
        /// </summary>
        public static List<Finding> ValidatePack(FollowerPack pack, ISet<string> externalCharacterIds)
        {
            var findings = new List<Finding>();
            var packId = pack.Manifest?.Id ?? pack.SourcePath;

            var knownCharacterIds = new HashSet<string>(externalCharacterIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (var id in pack.Characters.Keys) knownCharacterIds.Add(id);

            ValidateCharacterIds(pack, packId, findings);
            ValidateFollowers(pack, packId, knownCharacterIds, findings);

            return findings;
        }

        /// <summary>Per-pack validation plus a cross-pack duplicate-id check across the whole enabled set.</summary>
        public static List<Finding> ValidateAll(IReadOnlyList<FollowerPack> packs, ISet<string> externalCharacterIds)
        {
            var findings = new List<Finding>();
            foreach (var pack in packs)
                findings.AddRange(ValidatePack(pack, externalCharacterIds));

            var seenFollowerIds = new Dictionary<string, string>(StringComparer.Ordinal); // id -> first pack id
            foreach (var pack in packs)
            {
                var packId = pack.Manifest?.Id ?? pack.SourcePath;
                foreach (var id in pack.Followers.Keys)
                {
                    if (seenFollowerIds.TryGetValue(id, out var firstPackId))
                        findings.Add(Finding.Error(packId, id, "duplicate_id_cross_pack",
                            $"id '{id}' also declared by pack '{firstPackId}'."));
                    else
                        seenFollowerIds[id] = packId;
                }
            }

            return findings;
        }

        private static void ValidateCharacterIds(FollowerPack pack, string packId, List<Finding> findings)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in pack.Characters.Keys)
            {
                if (!IdPattern.IsMatch(id))
                    findings.Add(Finding.Error(packId, id, "id_shape", $"characters.json id '{id}' does not match ^SMN_[A-Z0-9_]+$."));
                if (!seen.Add(id))
                    findings.Add(Finding.Error(packId, id, "duplicate_id", $"duplicate id '{id}' within characters.json."));
            }
        }

        private static void ValidateFollowers(FollowerPack pack, string packId, ISet<string> knownCharacterIds, List<Finding> findings)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in pack.Followers)
            {
                var id = kv.Key;
                var entry = kv.Value;

                if (!IdPattern.IsMatch(id))
                    findings.Add(Finding.Error(packId, id, "id_shape", $"id '{id}' does not match ^SMN_[A-Z0-9_]+$."));

                if (!seen.Add(id))
                    findings.Add(Finding.Error(packId, id, "duplicate_id", $"duplicate id '{id}' within followers.json."));

                if (entry == null)
                {
                    findings.Add(Finding.Error(packId, id, "entry_null", "follower entry is null."));
                    continue;
                }

                if (entry.Type == null || !Vocabulary.FollowerTypes.Contains(entry.Type))
                    findings.Add(Finding.Error(packId, id, "unknown_type",
                        $"Type '{entry.Type}' is not legal for an M0 follower pack (allowed: COMPANION, MERCENARY)."));

                if (entry.Rarity == null || !Vocabulary.ItemRarities.Contains(entry.Rarity))
                    findings.Add(Finding.Error(packId, id, "unknown_rarity", $"Rarity '{entry.Rarity}' is not a legal eItemRarities value."));

                if (entry.ContractPrice == null || !Vocabulary.LootScales.Contains(entry.ContractPrice))
                    findings.Add(Finding.Error(packId, id, "unknown_contract_price", $"ContractPrice '{entry.ContractPrice}' is not a legal eLootScales value."));

                if (entry.Behaviour == null || !Vocabulary.AiBehaviours.Contains(entry.Behaviour))
                    findings.Add(Finding.Error(packId, id, "unknown_behaviour", $"Behaviour '{entry.Behaviour}' is not a legal eAiBehaviours value."));

                if (entry.AppendableStats != null)
                {
                    foreach (var statKey in entry.AppendableStats.Keys)
                    {
                        if (!Vocabulary.CharacterStats.Contains(statKey))
                            findings.Add(Finding.Error(packId, id, "unknown_appendable_stat",
                                $"AppendableStats key '{statKey}' is not a legal eCharacterStats value."));
                    }
                }

                if (string.IsNullOrEmpty(entry.ConfigName))
                    findings.Add(Finding.Error(packId, id, "config_name_missing", "ConfigName is empty -- follower's visuals/stats/abilities cannot resolve."));
                else if (!knownCharacterIds.Contains(entry.ConfigName))
                    findings.Add(Finding.Error(packId, id, "config_name_unresolved",
                        $"ConfigName '{entry.ConfigName}' is not present in the supplied character-id set."));

                if (entry.MinTier > entry.MaxTier)
                    findings.Add(Finding.Error(packId, id, "tier_range_invalid", $"MinTier ({entry.MinTier}) > MaxTier ({entry.MaxTier})."));

                if (pack.Localization == null || !pack.Localization.ContainsKey(id))
                    findings.Add(Finding.Warn(packId, id, "missing_loc", $"no localization/en.json name entry for '{id}'."));
            }
        }
    }
}
