using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.Json;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// Cross-references every id a shipped pack points at, against the LIVE game data plus the packs' own
    /// merged and authored ids. Two corrections are baked in and must not be undone:
    ///
    ///   (1) CharacterConfig.Passives resolve against Configs.SkillConfigs (68 entries), NOT Configs.Abilities.
    ///       Checking Abilities produced 100 false "dangling" findings (2026-08-08).
    ///   (2) Passives must ALSO resolve against the packs' own skillrecipes.json ids, which are absent from
    ///       ClassForge's MergePlan entirely. Omitting them produced 56 false findings (2026-08-23).
    ///   (3) Passives must ALSO resolve against the LIVE Configs.StatusEffects keys, not just the packs'
    ///       merged ones. A CharacterConfig.Passives entry is a plain string matched by raw equality
    ///       (CoreHelper.HasPassive -> local hasNestedPassive -> Configs.Characters[id].Passives.Contains),
    ///       and the shipped game uses live status ids there as immunity markers:
    ///       CharacterHelper.HasImmunity calls HasPassive(entity, CoreHelper.GetImmunityName(type)),
    ///       i.e. "STATUS_IMMUNITY_" + type. 766 vanilla characters carry STATUS_IMMUNITY_STUN this way,
    ///       593 STATUS_IMMUNITY_POISON, 296 STATUS_IMMUNITY_WATER — and none of those ids is in
    ///       SkillConfigs. Resolving only against merged statuses produced 30 false findings (2026-08-25)
    ///       against a load-bearing, vanilla-idiomatic reference.
    /// </summary>
    public static class ReferenceChecks
    {
        /// <summary>Tags authored by this repo's packs (or owned by the engine) that are absent from every
        /// learned live tag vocabulary. TRAIT and LOADOUT_0 are very likely FTK2.dll enum members and
        /// therefore already covered — the "redundant allowlist entries" warning below reports any entry
        /// that turns out to be, so this list shrinks on evidence rather than guesswork.</summary>
        /// ("TRAIT" was in this list in the plan; measured 2026-08-25 it IS an FTK2.dll enum member, so the
        /// enum vocabulary already covers it and the redundancy warning below told us to drop it.)
        /// (CF_TRAINER_PARTNER and the three CF_LINE_* markers are deliberate pack-internal labels on the
        /// Trainer's partner classes. CharacterConfig.Tags is a List<string> matched by string, so a tag the
        /// engine does not know is inert rather than broken — they group the roster for authoring and are
        /// read by nothing at runtime, which is intended.)
        public static readonly string[] AuthoredTags =
        {
            "CF_SUMMON", "LOADOUT_0",
            "CF_TRAINER_PARTNER", "CF_LINE_GRASS", "CF_LINE_WATER", "CF_LINE_FIRE"
        };

        public static void Register(CheckRunner runner, GameData data, GameVocabulary vocab, AuthoredContent content)
        {
            runner.Section("ClassForge — reference integrity vs live Configs");

            runner.Case("every pack reference resolves", delegate
            {
                Check.AtLeast(1, content.Packs.MergePlan.Characters.Count,
                    "merged Characters inspected (a zero-subject scan is a vacuous pass)");
                Check.Empty(Scan(data, vocab, content), "dangling references in shipped pack content");
            });

            runner.Case("the status-group vocabulary is real (guards the warning below)", delegate
            {
                Check.AtLeast(1, vocab.StatusGroups.Count,
                    "eStatusEffectsGroups members read from FTK2.dll (zero would make the warning below vacuous)");
                Check.AtLeast(1, content.Packs.MergePlan.StatusEffects.Count, "merged statuses inspected");
            });

            runner.Warn("merged status ids not prefixed by a vanilla eStatusEffectsGroups member",
                StatusGroupOffenders(vocab, content));

            runner.Case("NEGATIVE fixture: a dangling Passive IS caught", delegate
            {
                string fixtureRoot = Path.Combine(PackRoots.FixturesDir(), "dangling");
                Check.True(Directory.Exists(fixtureRoot), "fixture root must exist at " + fixtureRoot);

                AuthoredContent bad = AuthoredContent.LoadFrom(data, new string[] { fixtureRoot });
                List<string> offenders = Scan(data, vocab, bad).ToList();
                string got = offenders.Count == 0 ? "<nothing>" : string.Join(" | ", offenders);

                Check.True(offenders.Any(delegate (string o) { return o.Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST"); }),
                    "fixture CF_PACK_HARNESS_DANGLING must be reported as dangling; got: " + got);

                // Correction 3 unions in the live status table. Prove it did NOT degrade into "anything that
                // looks like a status id passes": an invented id wearing the STATUS_IMMUNITY_ prefix is still
                // caught, because membership is by key, not by prefix.
                Check.True(offenders.Any(delegate (string o) { return o.Contains("STATUS_IMMUNITY_HARNESS_DOES_NOT_EXIST"); }),
                    "an invented STATUS_IMMUNITY_* passive must still be reported — the live-status union " +
                    "must match by key, never by prefix; got: " + got);

                // The AuthoredTags allowlist is a fixed set, not a CF_ prefix pass.
                Check.True(offenders.Any(delegate (string o) { return o.Contains("CF_HARNESS_TAG_DOES_NOT_EXIST"); }),
                    "an invented CF_-prefixed tag must still be reported; got: " + got);
            });

            runner.Case("the vanilla STATUS_IMMUNITY_* passive idiom resolves (correction 3)", delegate
            {
                // CharacterHelper.HasImmunity -> CoreHelper.HasPassive(entity, "STATUS_IMMUNITY_" + type),
                // matched by raw string equality against CharacterConfig.Passives. These are live
                // Configs.StatusEffects keys and none of them is in SkillConfigs, so before correction 3
                // the scan called the shipped game's own idiom a dangling reference.
                List<string> immunityIds = data.Ids("StatusEffects")
                    .Where(delegate (string id) { return id.StartsWith("STATUS_IMMUNITY_", StringComparison.Ordinal); })
                    .ToList();
                Check.AtLeast(1, immunityIds.Count,
                    "live STATUS_IMMUNITY_* status ids (zero would make this check vacuous)");

                HashSet<string> resolvable = ResolvableSkills(data, content);
                Check.Empty(immunityIds.Where(delegate (string id) { return !resolvable.Contains(id); })
                                       .Select(delegate (string id) {
                                           return id + " is a live Configs.StatusEffects key used verbatim in " +
                                                  "vanilla CharacterConfig.Passives arrays, but the resolver rejects it";
                                       }),
                    "vanilla immunity passives the resolver still rejects");
            });

            runner.Case("NEGATIVE control: the resolvable sets reject an invented id", delegate
            {
                Check.True(!data.Ids("SkillConfigs").Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST"),
                    "live SkillConfigs must not contain the fixture's invented id");
                Check.True(!content.AllRecipeIds.Contains("SKILL_CF_HARNESS_DOES_NOT_EXIST"),
                    "the authored recipe universe must not contain the fixture's invented id");
                Check.AtLeast(50, content.AllRecipeIds.Count,
                    "authored + generated recipe ids — a near-empty set would make correction 2 silently " +
                    "ineffective and flood the run with false findings");
            });

            runner.Warn("redundant AuthoredTags entries (already covered by the enum vocabulary)",
                AuthoredTags.Where(delegate (string t) { return vocab.EnumMembers.Contains(t); })
                            .Select(delegate (string t) { return t + " is an FTK2.dll enum member — remove it from ReferenceChecks.AuthoredTags"; }));
        }

        /// <summary>
        /// The "status id must begin with a vanilla eStatusEffectsGroups member or it renders nowhere" rule,
        /// reported as a WARNING rather than an Error — because the rule as usually stated is measurably
        /// false against vanilla itself.
        ///
        /// Measured 2026-08-25 on the live install: eStatusEffectsGroups declares 95 members, and 35 of the
        /// 189 vanilla Configs.StatusEffects ids do NOT start with any of them (AURA_ATTACK_00,
        /// STATUS_CURSE_00, STATUS_GUARD_00, STATUS_SANCTUM_*, STATUS_GRAND_SANCTUM_*, ...). StatusEffectConfig
        /// carries no Group field either. So a non-conforming id cannot be treated as proof of invisibility;
        /// escalating this to an Error would fire on 18% of the base game.
        ///
        /// It stays reported because prefix-grouped ids are the majority convention and a miss is worth a
        /// look. Escalate it only on evidence from the game's own status-group resolution path.
        /// </summary>
        public static IEnumerable<string> StatusGroupOffenders(GameVocabulary vocab, AuthoredContent content)
        {
            List<string> offenders = new List<string>();
            foreach (MergeOp op in content.Packs.MergePlan.StatusEffects)
            {
                bool ok = false;
                foreach (string group in vocab.StatusGroups)
                {
                    if (string.IsNullOrEmpty(group)) continue;
                    if (op.Id.StartsWith(group, StringComparison.Ordinal)) { ok = true; break; }
                }
                if (!ok)
                    offenders.Add("status " + op.Id + " (pack " + op.SourcePackId + ") does not start with any " +
                                  "vanilla eStatusEffectsGroups member — worth checking it renders; note 35 of " +
                                  "the 189 vanilla status ids do not either, so this alone is not proof of a bug");
            }
            offenders.Sort(StringComparer.Ordinal);
            return offenders;
        }

        /// <summary>One human-readable offender line per unresolved reference. Pure over its inputs.</summary>
        public static IEnumerable<string> Scan(GameData data, GameVocabulary vocab, AuthoredContent content)
        {
            MergePlan plan = content.Packs.MergePlan;

            HashSet<string> mergedThings = IdSet(plan.Things);
            HashSet<string> mergedAbilities = IdSet(plan.Abilities);

            ISet<string> liveThings = data.Ids("Things");
            ISet<string> liveAbilities = data.Ids("Abilities");
            ISet<string> liveCharacters = data.Ids("Characters");

            // The one set all three corrections live in.
            HashSet<string> resolvableSkills = ResolvableSkills(data, content);

            HashSet<string> resolvableThings = new HashSet<string>(liveThings, StringComparer.Ordinal);
            resolvableThings.UnionWith(mergedThings);

            HashSet<string> resolvableAbilities = new HashSet<string>(liveAbilities, StringComparer.Ordinal);
            resolvableAbilities.UnionWith(mergedAbilities);

            HashSet<string> authoredTags = new HashSet<string>(AuthoredTags, StringComparer.Ordinal);

            List<string> offenders = new List<string>();

            foreach (MergeOp op in plan.Characters)
            {
                foreach (string passive in op.Value.GetStringArray("Passives"))
                    if (!resolvableSkills.Contains(passive))
                        offenders.Add(op.Id + ".Passives -> " + passive +
                                      " (not in Configs.SkillConfigs, the packs' authored/generated recipes, " +
                                      "or merged Abilities/StatusEffects)");

                foreach (KeyValuePair<string, JsonValue> member in op.Value.Get("Things").AsObjectMembers)
                    if (!resolvableThings.Contains(member.Key))
                        offenders.Add(op.Id + ".Things -> " + member.Key + " (not in Configs.Things nor merged Things)");

                CheckVocab(offenders, op.Id, "BaseType", op.Value.GetString("BaseType"), vocab.BaseTypes, null);
                CheckVocab(offenders, op.Id, "DefaultBodyType", op.Value.GetString("DefaultBodyType"), vocab.BodyTypes, null);
                CheckVocab(offenders, op.Id, "Rarity", op.Value.GetString("Rarity"), vocab.Rarities, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "Expansion", op.Value.GetString("Expansion"), vocab.Expansions, vocab.EnumMembers);
                CheckTags(offenders, op.Id, op.Value.GetStringArray("Tags"), vocab.CharacterTags, vocab.EnumMembers, content.PackIds, authoredTags);
            }

            foreach (MergeOp op in plan.Things)
            {
                JsonValue equippable = op.Value.Get("Equippable");
                if (!equippable.IsNull)
                    foreach (string passive in equippable.GetStringArray("Passives"))
                        if (!resolvableSkills.Contains(passive))
                            offenders.Add(op.Id + ".Equippable.Passives -> " + passive +
                                          " (not in Configs.SkillConfigs, the packs' authored/generated recipes, " +
                                          "or merged Abilities/StatusEffects)");

                JsonValue interactable = op.Value.Get("Interactable");
                if (!interactable.IsNull)
                    foreach (KeyValuePair<string, JsonValue> ability in interactable.Get("Abilities").AsObjectMembers)
                        if (!resolvableAbilities.Contains(ability.Key))
                            offenders.Add(op.Id + ".Interactable.Abilities -> " + ability.Key +
                                          " (not in Configs.Abilities nor merged Abilities)");

                CheckVocab(offenders, op.Id, "Class", op.Value.GetString("Class"), vocab.ThingClasses, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "Rarity", op.Value.GetString("Rarity"), vocab.Rarities, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "Material", op.Value.GetString("Material"), vocab.Materials, vocab.EnumMembers);
                CheckVocab(offenders, op.Id, "ConsumableType", op.Value.GetString("ConsumableType"), vocab.ConsumableTypes, vocab.EnumMembers);
                CheckTags(offenders, op.Id, op.Value.GetStringArray("Tags"), vocab.ThingTags, vocab.EnumMembers, content.PackIds, authoredTags);
            }

            foreach (MergeOp op in plan.StatusEffects)
            {
                foreach (string passive in op.Value.GetStringArray("Passives"))
                    if (!resolvableSkills.Contains(passive))
                        offenders.Add(op.Id + ".Passives -> " + passive +
                                      " (not in Configs.SkillConfigs, the packs' authored/generated recipes, " +
                                      "or merged Abilities/StatusEffects)");

                CheckVocab(offenders, op.Id, "Type", op.Value.GetString("Type"), vocab.StatusTypes, vocab.EnumMembers);
            }

            // A CharacterConfig id a recipe summons/spawns must exist, or the entity is alive, targetable and
            // rendered as nothing. Recipes are walked here (not in RecipeChecks) because this is the file that
            // owns "does this id resolve".
            HashSet<string> resolvableCharacters = new HashSet<string>(liveCharacters, StringComparer.Ordinal);
            foreach (MergeOp op in plan.Characters) resolvableCharacters.Add(op.Id);

            foreach (KeyValuePair<string, ClassForge.Recipes.Model.RecipeSet> entry in content.RecipeSetsByPackId)
                foreach (ClassForge.Recipes.Model.SkillRecipe recipe in entry.Value.Ordered)
                    foreach (ClassForge.Recipes.Model.RecipeEffect effect in recipe.Effects)
                    {
                        if (string.IsNullOrEmpty(effect.CharacterConfig)) continue;
                        if (resolvableCharacters.Contains(effect.CharacterConfig)) continue;
                        offenders.Add(entry.Key + " / " + recipe.Id + ".CharacterConfig -> " + effect.CharacterConfig +
                                      " (not in Configs.Characters nor merged Characters — the spawned entity " +
                                      "would be alive and targetable but render as nothing)");
                    }

            offenders.Sort(StringComparer.Ordinal);
            return offenders;
        }

        /// <summary>Everything a Passives entry is allowed to name: live SkillConfigs, the packs' authored and
        /// engine-generated recipe ids, the packs' merged Abilities, and BOTH the packs' merged StatusEffects
        /// and the live Configs.StatusEffects keys (corrections 1-3 in the type doc above). Deliberately does
        /// NOT include Configs.Abilities — correction 1.</summary>
        private static HashSet<string> ResolvableSkills(GameData data, AuthoredContent content)
        {
            MergePlan plan = content.Packs.MergePlan;
            HashSet<string> set = new HashSet<string>(data.Ids("SkillConfigs"), StringComparer.Ordinal);
            set.UnionWith(content.AllRecipeIds);
            set.UnionWith(IdSet(plan.Abilities));
            set.UnionWith(IdSet(plan.StatusEffects));
            set.UnionWith(data.Ids("StatusEffects"));
            return set;
        }

        private static HashSet<string> IdSet(List<MergeOp> ops)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ops.Count; i++) set.Add(ops[i].Id);
            return set;
        }

        /// <summary>An absent or empty value is always fine — these fields are optional in the schema and
        /// vanilla itself leaves several of them blank.</summary>
        private static void CheckVocab(List<string> offenders, string id, string field, string value,
                                       ISet<string> primary, ISet<string> fallback)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (primary != null && primary.Contains(value)) return;
            if (fallback != null && fallback.Contains(value)) return;
            offenders.Add(id + "." + field + " -> " + value + " (not a value the live game uses for this field)");
        }

        private static void CheckTags(List<string> offenders, string id, List<string> tags,
                                      ISet<string> liveTags, ISet<string> enums, ISet<string> packIds, ISet<string> authoredTags)
        {
            if (tags == null) return;
            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                if (string.IsNullOrEmpty(tag)) continue;
                if (liveTags.Contains(tag) || enums.Contains(tag) || packIds.Contains(tag) || authoredTags.Contains(tag)) continue;
                offenders.Add(id + ".Tags -> " + tag +
                              " (not a live tag, an FTK2 enum member, a loaded pack id, or a ReferenceChecks.AuthoredTags entry)");
            }
        }
    }
}
