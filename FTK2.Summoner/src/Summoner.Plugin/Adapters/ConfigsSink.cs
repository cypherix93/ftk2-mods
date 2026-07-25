using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Summoner.Core.Merge;
using Summoner.Core.Model;

namespace Summoner.Plugin.Adapters
{
    /// <summary>
    /// Applies a MergePlan to Env.Configs.Followers / Env.Configs.Characters (design §A4.2). The
    /// only file in Summoner.Plugin that knows SerializedSortedDictionary, FollowerCharacterConfig
    /// and CharacterConfig exist. Maps FollowerEntry/CharacterEntry (Core, string-typed enum
    /// mirrors) -> the real game types via Enum.TryParse. A parse failure here should be
    /// unreachable -- PackValidator already gated every enum-backed field against
    /// Validation/Vocabulary before this pack ever reached MergePlanner -- but is logged and the
    /// entry is skipped rather than thrown, per design §A4.2.
    /// </summary>
    public static class ConfigsSink
    {
        public static void Apply(MergePlan plan, Configs configs, ManualLogSource log)
        {
            foreach (var add in plan.CharacterAdds)
            {
                var entry = add.Entry as CharacterEntry;
                var converted = entry != null ? ToGameCharacter(entry, add.PackId, add.Id, log) : null;
                if (converted != null) configs.Characters[add.Id] = converted;
            }

            foreach (var add in plan.FollowerAdds)
            {
                var entry = add.Entry as FollowerEntry;
                var converted = entry != null ? ToGameFollower(entry, add.PackId, add.Id, log) : null;
                if (converted != null) configs.Followers[add.Id] = converted;
            }

            foreach (var skip in plan.Skips)
                log.LogDebug($"[Summoner] skip {skip.PackId}:{skip.Id} -- {skip.Reason}");

            foreach (var reject in plan.Rejects)
                log.LogError($"[Summoner] reject {reject.PackId}:{reject.EntityId} [{reject.Check}] {reject.Message}");
        }

        private static FollowerCharacterConfig ToGameFollower(FollowerEntry e, string packId, string id, ManualLogSource log)
        {
            if (!Enum.TryParse(e.Type, out eCharacterTypes type) ||
                !Enum.TryParse(e.Rarity, out eItemRarities rarity) ||
                !Enum.TryParse(e.ContractPrice, out eLootScales price) ||
                !Enum.TryParse(e.Behaviour, out eAiBehaviours behaviour) ||
                !Enum.TryParse(e.Expansion, out eExpansions expansion))
            {
                log.LogError($"[Summoner] {packId}:{id} -- enum parse failed after PackValidator's gate (should be unreachable); entry skipped.");
                return null;
            }

            var appendable = new Dictionary<eCharacterStats, int>();
            if (e.AppendableStats != null)
            {
                foreach (var kv in e.AppendableStats)
                {
                    if (Enum.TryParse(kv.Key, out eCharacterStats stat))
                        appendable[stat] = kv.Value;
                    else
                        log.LogWarning($"[Summoner] {packId}:{id} -- AppendableStats key '{kv.Key}' is not a known eCharacterStats value; dropped.");
                }
            }

            return new FollowerCharacterConfig
            {
                Type = type,
                ConfigName = e.ConfigName,
                ClassName = e.ClassName,
                ContractRounds = e.ContractRounds,
                MinTier = e.MinTier,
                MaxTier = e.MaxTier,
                Rarity = rarity,
                ContractPrice = price,
                Behaviour = behaviour,
                Subtitle = e.Subtitle,
                JoinParty = e.JoinParty,
                LeaveParty = e.LeaveParty,
                Rescued = e.Rescued,
                GiveDeed = e.GiveDeed,
                Tags = e.Tags != null ? new List<string>(e.Tags) : new List<string>(),
                CaravanStatID = e.CaravanStatID,
                CaravanStatValue = e.CaravanStatValue,
                AppendableStats = appendable,
                Expansion = expansion
            };
        }

        private static CharacterConfig ToGameCharacter(CharacterEntry e, string packId, string id, ManualLogSource log)
        {
            if (!Enum.TryParse(e.Rarity, out eItemRarities rarity) ||
                !Enum.TryParse(e.Expansion, out eExpansions expansion))
            {
                log.LogError($"[Summoner] {packId}:{id} -- enum parse failed after PackValidator's gate (should be unreachable); entry skipped.");
                return null;
            }

            var stats = new SerializedSortedDictionary<string, int>();
            if (e.Stats != null) foreach (var kv in e.Stats) stats[kv.Key] = kv.Value;

            var things = new SerializedSortedDictionary<string, int>();
            if (e.Things != null) foreach (var kv in e.Things) things[kv.Key] = kv.Value;

            return new CharacterConfig
            {
                Stats = stats,
                Things = things,
                Passives = e.Passives != null ? new List<string>(e.Passives) : new List<string>(),
                CampQuery = e.CampQuery,
                SwarmQuery = e.SwarmQuery,
                LootID = e.LootID,
                LocKey = e.LocKey,
                Rarity = rarity,
                Level = e.Level,
                Threat = e.Threat,
                BaseType = e.BaseType,
                DefaultBodyType = e.DefaultBodyType,
                Tags = e.Tags != null ? new List<string>(e.Tags) : new List<string>(),
                OnDeathAbility = e.OnDeathAbility,
                Expansion = expansion
            };
        }
    }
}
