using System.Collections.Generic;
using System.Linq;
using Summoner.Core.Diagnostics;
using Summoner.Core.Model;
using Summoner.Core.Packs;
using Summoner.Core.Validation;

namespace Summoner.Core.Tests
{
    public static class PackValidatorTests
    {
        private static FollowerEntry MinimalValidFollower(string configName = "LIVE_VANILLA_ID_00") => new FollowerEntry
        {
            Type = "COMPANION",
            ConfigName = configName,
            ClassName = "SMN_FOL_WHATEVER",
            ContractRounds = 0,
            MinTier = 0,
            MaxTier = 3,
            Rarity = "COMMON",
            ContractPrice = "VERY_LOW",
            Behaviour = "DEFAULT",
            Tags = new List<string> { "COMPANION" },
            Expansion = "BASE"
        };

        private static FollowerPack PackWith(Dictionary<string, FollowerEntry> followers, Dictionary<string, CharacterEntry> characters = null)
            => new FollowerPack
            {
                Manifest = new PackManifest { Id = "SMN_PACK_TEST", Name = "test", Enabled = true },
                Followers = followers,
                Characters = characters ?? new Dictionary<string, CharacterEntry>(),
                Localization = new Dictionary<string, string>()
            };

        public static void RejectsIdWithoutSmnPrefix()
        {
            var pack = PackWith(new Dictionary<string, FollowerEntry> { { "BAD_ID_NO_PREFIX", MinimalValidFollower() } });
            var findings = PackValidator.ValidatePack(pack, new HashSet<string> { "LIVE_VANILLA_ID_00" });

            Check.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Check == "id_shape" && f.EntityId == "BAD_ID_NO_PREFIX",
                "id without the SMN_ prefix must be rejected by id_shape");
        }

        public static void RejectsUnknownFollowerType()
        {
            var followers = new Dictionary<string, FollowerEntry>
            {
                { "SMN_FOL_INANIMATE_ONE", Merge(MinimalValidFollower(), e => e.Type = "INANIMATE") },
                { "SMN_FOL_CURSE_ONE", Merge(MinimalValidFollower(), e => e.Type = "CURSE") }
            };
            var pack = PackWith(followers);
            var findings = PackValidator.ValidatePack(pack, new HashSet<string> { "LIVE_VANILLA_ID_00" });

            Check.Contains(findings, f => f.Check == "unknown_type" && f.EntityId == "SMN_FOL_INANIMATE_ONE",
                "Type=INANIMATE must be rejected for an M0 follower (legal eCharacterTypes member, illegal for followers)");
            Check.Contains(findings, f => f.Check == "unknown_type" && f.EntityId == "SMN_FOL_CURSE_ONE",
                "Type=CURSE must be rejected for an M0 follower");
        }

        public static void RejectsUnknownContractPriceAndBehaviour()
        {
            var entry = MinimalValidFollower();
            entry.ContractPrice = "NOT_A_LOOT_SCALE";
            entry.Behaviour = "NOT_A_BEHAVIOUR";
            var pack = PackWith(new Dictionary<string, FollowerEntry> { { "SMN_FOL_BAD_ENUMS", entry } });

            var findings = PackValidator.ValidatePack(pack, new HashSet<string> { "LIVE_VANILLA_ID_00" });

            Check.Contains(findings, f => f.Check == "unknown_contract_price" && f.EntityId == "SMN_FOL_BAD_ENUMS", "illegal ContractPrice must be flagged");
            Check.Contains(findings, f => f.Check == "unknown_behaviour" && f.EntityId == "SMN_FOL_BAD_ENUMS", "illegal Behaviour must be flagged");
        }

        public static void RejectsAppendableStatKeyNotInCharacterStats()
        {
            var entry = MinimalValidFollower();
            entry.AppendableStats = new Dictionary<string, int> { { "NOT_A_REAL_STAT", 5 }, { "HP", 10 } };
            var pack = PackWith(new Dictionary<string, FollowerEntry> { { "SMN_FOL_BAD_STAT", entry } });

            var findings = PackValidator.ValidatePack(pack, new HashSet<string> { "LIVE_VANILLA_ID_00" });

            Check.Contains(findings, f => f.Check == "unknown_appendable_stat" && f.EntityId == "SMN_FOL_BAD_STAT" && f.Message.Contains("NOT_A_REAL_STAT"),
                "AppendableStats key outside eCharacterStats must be flagged");
            Check.DoesNotContain(findings, f => f.Check == "unknown_appendable_stat" && f.Message.Contains("'HP'"),
                "the legal HP key must not be flagged alongside the illegal one");
        }

        public static void RejectsUnresolvableConfigName_AgainstSuppliedIdSet()
        {
            var entry = MinimalValidFollower("GHOST_CONFIG_NOT_IN_SET");
            var pack = PackWith(new Dictionary<string, FollowerEntry> { { "SMN_FOL_DANGLING", entry } });

            var findings = PackValidator.ValidatePack(pack, new HashSet<string> { "SOME_OTHER_LIVE_ID" });

            Check.Contains(findings, f => f.Check == "config_name_unresolved" && f.EntityId == "SMN_FOL_DANGLING",
                "a ConfigName absent from the supplied character-id set must be rejected");
        }

        public static void AcceptsConfigNameSatisfiedByPacksOwnCharactersJson()
        {
            var entry = MinimalValidFollower("SMN_CHAR_LOCAL");
            var characters = new Dictionary<string, CharacterEntry>
            {
                { "SMN_CHAR_LOCAL", new CharacterEntry { Rarity = "COMMON", BaseType = "STANDARD", DefaultBodyType = "HUMANOID", Expansion = "BASE" } }
            };
            var pack = PackWith(new Dictionary<string, FollowerEntry> { { "SMN_FOL_LOCAL_CONFIG", entry } }, characters);

            // externalCharacterIds deliberately does NOT contain SMN_CHAR_LOCAL -- only the pack's own characters.json does.
            var findings = PackValidator.ValidatePack(pack, new HashSet<string>());

            Check.DoesNotContain(findings, f => f.EntityId == "SMN_FOL_LOCAL_CONFIG" && (f.Check == "config_name_unresolved" || f.Check == "config_name_missing"),
                "ConfigName satisfied by the pack's own characters.json must resolve even with an empty external id set");
        }

        private static FollowerEntry Merge(FollowerEntry entry, System.Action<FollowerEntry> mutate)
        {
            mutate(entry);
            return entry;
        }
    }
}
