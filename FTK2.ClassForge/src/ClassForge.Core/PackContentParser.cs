using System;
using System.Collections.Generic;
using System.IO;
using ClassForge.Core.IO;
using ClassForge.Core.Json;

namespace ClassForge.Core
{
    /// <summary>
    /// Parses one pack's content files — classes.json/traits.json/abilities.json/items.json (SPEC.md
    /// §4.2-§4.5), localization/en.json (§4.7), and icons/*.png + portraits/*.png (§4.8) — into plain
    /// id -&gt; raw-JSON-value dictionaries. Deliberately does not model CharacterConfig/ThingConfig/etc.:
    /// Core hands opaque parsed objects to the game layer (Plugin), which is the only place with the real
    /// game types available.
    /// </summary>
    public static class PackContentParser
    {
        /// <summary>
        /// <c>eStatusEffectTypes</c> member set (Encounter Modifiers spec §3.1), transcribed verbatim from
        /// the current-build decompile (<c>tools/out/decompile/FTK2/eStatusEffectTypes.cs</c>). Core has zero
        /// game references by design, so this is the one place the enum's shape lives on this side of the
        /// boundary — statuses.json's <c>Type</c> field is validated against it.
        /// </summary>
        private static readonly HashSet<string> KnownStatusEffectTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "NONE", "AURA", "ACID", "BLEED", "CONFUSE", "CURSE", "STUN", "DAZE", "ENTANGLE", "INFINITE_FIRE",
            "FIRE", "ICE", "SHOCK", "WATER", "POISON", "PETRIFY", "BUFF", "DEBUFF", "IMMUNITY", "PURIFY",
            "REGEN", "TAUNT", "PROTECT", "INFINITE_PROTECT", "DEATHSAVE", "SCARE", "REFLECT", "DRAIN", "GRAB",
            "MOVE", "STEAL", "ROWDY", "GUARD", "INK", "DEATHMARK", "SANCTUM", "BARRIER", "DECOY",
            "IMBUE_FIRE", "IMBUE_ICE", "IMBUE_SHOCK", "IMBUE_WATER", "STATSUP", "DISTRACT", "STAR_SHIELD",
            "LINK", "CONCENTRATION", "PROWESS", "RATTLED", "VIGOR", "HASTE", "CHANNEL_MAGIC", "CHANNEL_PHYSIC",
            "MARKED", "CHARGE", "BADWEATHER", "POLLINATE", "LONEWOLF"
        };

        public static ParsedPack Parse(IFileSource fs, DiscoveredPack pack, List<Finding> findings)
        {
            var result = new ParsedPack { PackId = pack.Manifest.Id };

            result.Classes = ParseObjectFile(fs, pack, "classes.json", findings);
            result.Traits = ParseObjectFile(fs, pack, "traits.json", findings);
            result.Abilities = ParseObjectFile(fs, pack, "abilities.json", findings);
            result.Items = ParseObjectFile(fs, pack, "items.json", findings);
            result.Localization = ParseLocalization(fs, pack, findings);
            result.Icons = ParseAssetDir(fs, pack, "icons", findings);
            result.Portraits = ParseAssetDir(fs, pack, "portraits", findings);

            var recipeIdsInPack = ReadSkillRecipeIdsInPack(fs, pack);
            result.Statuses = ParseStatusesFile(fs, pack, findings, recipeIdsInPack);
            result.ModifierTable = ParseModifiersFile(fs, pack, findings, result.Statuses);

            // SPEC-DELTA-v1.1 §1 OQ#1: the native trait substrate keys purely on Thing.ConfigName starting with
            // "TRAIT_" (CharacterHelper.GiveTrait / InventoryHelper.GetTraits) — there is no eTraits bridge.
            // M1 still merges every traits.json entry into Configs.Things as inert data regardless of prefix
            // (SPEC.md §10 M1 milestone: "Traits merge into Configs.Things as inert data... not yet grantable").
            // A non-TRAIT_-prefixed id is flagged here (loudly, per CONVENTIONS.md) so a future M2 trait-bridge
            // pass — or a pack author — can fix it before that id needs to be actually granted in-game.
            foreach (var kv in result.Traits)
            {
                if (!kv.Key.StartsWith("TRAIT_", StringComparison.Ordinal))
                {
                    findings.Add(Finding.Warning("CF_TRAIT_PREFIX",
                        $"Trait id '{kv.Key}' in pack '{pack.Manifest.Id}' does not start with 'TRAIT_'. The native trait " +
                        "substrate (CharacterHelper.GiveTrait/RemoveTrait, InventoryHelper.GetTraits) keys on that literal " +
                        "ConfigName prefix (SPEC-DELTA-v1.1 §1). This entry still merges into Configs.Things as inert data " +
                        "for M1, but will not be grantable through the native trait API until renamed.",
                        pack.Manifest.Id));
                }
            }

            return result;
        }

        private static Dictionary<string, JsonValue> ParseObjectFile(IFileSource fs, DiscoveredPack pack, string fileName, List<Finding> findings)
        {
            var dict = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var path = fs.CombinePath(pack.RootDir, fileName);
            if (!fs.FileExists(path))
                return dict;

            try
            {
                var text = fs.ReadAllText(path);
                var json = JsonParser.Parse(text);
                if (json.Kind != JsonKind.Object)
                    throw new FormatException($"{fileName} root must be a JSON object.");
                foreach (var kv in json.AsObjectMembers)
                    dict[kv.Key] = kv.Value; // last-wins on duplicate keys within one file, matches JSON semantics
            }
            catch (Exception ex)
            {
                findings.Add(Finding.Error("CF_FILE_PARSE", $"Failed to parse {fileName} in pack '{pack.Manifest.Id}': {ex.Message}", pack.Manifest.Id));
            }

            return dict;
        }

        private static Dictionary<string, string> ParseLocalization(IFileSource fs, DiscoveredPack pack, List<Finding> findings)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = fs.CombinePath(pack.RootDir, "localization", "en.json");
            if (!fs.FileExists(path))
                return dict;

            try
            {
                var text = fs.ReadAllText(path);
                var json = JsonParser.Parse(text);
                if (json.Kind != JsonKind.Object)
                    throw new FormatException("localization/en.json root must be a JSON object.");
                foreach (var kv in json.AsObjectMembers)
                {
                    if (kv.Value.Kind != JsonKind.String)
                    {
                        findings.Add(Finding.Warning("CF_LOC_NON_STRING", $"localization/en.json key '{kv.Key}' in pack '{pack.Manifest.Id}' is not a string — skipped.", pack.Manifest.Id));
                        continue;
                    }
                    dict[kv.Key] = kv.Value.AsString;
                }
            }
            catch (Exception ex)
            {
                findings.Add(Finding.Error("CF_FILE_PARSE", $"Failed to parse localization/en.json in pack '{pack.Manifest.Id}': {ex.Message}", pack.Manifest.Id));
            }

            return dict;
        }

        private static Dictionary<string, string> ParseAssetDir(IFileSource fs, DiscoveredPack pack, string subDir, List<Finding> findings)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var dir = fs.CombinePath(pack.RootDir, subDir);
            if (!fs.DirectoryExists(dir))
                return map;

            foreach (var file in fs.GetFiles(dir, "*.png", false))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (map.ContainsKey(id))
                {
                    findings.Add(Finding.Warning("CF_ASSET_DUP", $"Duplicate {subDir} id '{id}' within pack '{pack.Manifest.Id}'.", pack.Manifest.Id));
                }
                map[id] = file;
            }

            return map;
        }

        /// <summary>Top-level keys of this pack's skillrecipes.json, read directly with Core's own JSON
        /// parser (Core does not depend on ClassForge.Recipes — that assembly owns real recipe parsing +
        /// validation, invoked separately by ClassForge.PackCheck). Used only to resolve statuses.json
        /// <c>Passives</c> entries against same-pack recipe ids (§3.1); a missing or malformed file yields an
        /// empty set and produces no Finding of its own here (the recipe engine's own tooling reports that).</summary>
        private static HashSet<string> ReadSkillRecipeIdsInPack(IFileSource fs, DiscoveredPack pack)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var path = fs.CombinePath(pack.RootDir, "skillrecipes.json");
            if (!fs.FileExists(path)) return ids;

            try
            {
                var text = fs.ReadAllText(path);
                var json = JsonParser.Parse(text);
                if (json.Kind != JsonKind.Object) return ids;
                foreach (var kv in json.AsObjectMembers)
                    ids.Add(kv.Key);
            }
            catch (Exception)
            {
                // Malformed skillrecipes.json is not this method's concern to report -- it just means no
                // same-pack recipe ids are known, so every Passives entry falls back to the SKILL_ prefix
                // check below.
            }
            return ids;
        }

        /// <summary>
        /// statuses.json (Encounter Modifiers spec §3.1) — StatusEffectConfig-shaped entries. Id convention
        /// <c>STATUS_CF_*</c> is loader-warned, not hard-failed (mirrors the CF_TRAIT_PREFIX pattern above).
        /// <c>Type</c> must be a member of <see cref="KnownStatusEffectTypes"/>; an entry with an unresolvable
        /// Type is dropped outright (Error) since the rest of the entry is meaningless without it. Every
        /// <c>Passives</c> array entry must resolve to a recipe id shipped by the same pack's skillrecipes.json
        /// or start with the vanilla <c>SKILL_</c> prefix pattern; an entry that resolves to neither is
        /// dropped from the array (Error), but the surrounding status entry is kept (its other fields — Type,
        /// Duration, Stats, etc. — remain perfectly usable).
        /// </summary>
        private static Dictionary<string, JsonValue> ParseStatusesFile(IFileSource fs, DiscoveredPack pack, List<Finding> findings, HashSet<string> recipeIdsInPack)
        {
            var result = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var raw = ParseObjectFile(fs, pack, "statuses.json", findings);

            foreach (var kv in raw)
            {
                var id = kv.Key;
                var value = kv.Value;

                if (!id.StartsWith("STATUS_CF_", StringComparison.Ordinal))
                {
                    findings.Add(Finding.Warning("CF_STATUS_ID_PREFIX",
                        $"Status id '{id}' in pack '{pack.Manifest.Id}' does not start with 'STATUS_CF_' (Encounter Modifiers spec §3.1 id convention).",
                        pack.Manifest.Id));
                }

                if (value.Kind != JsonKind.Object)
                {
                    findings.Add(Finding.Error("CF_STATUS_SHAPE", $"Status '{id}' in pack '{pack.Manifest.Id}' must be a JSON object.", pack.Manifest.Id));
                    continue;
                }

                var typeTok = value.GetString("Type");
                if (string.IsNullOrEmpty(typeTok) || !KnownStatusEffectTypes.Contains(typeTok))
                {
                    findings.Add(Finding.Error("CF_STATUS_TYPE_UNKNOWN",
                        $"Status '{id}' in pack '{pack.Manifest.Id}' has Type '{typeTok ?? "(missing)"}', which is not a member of eStatusEffectTypes — entry dropped.",
                        pack.Manifest.Id));
                    continue;
                }

                var passives = value.Get("Passives");
                if (passives.Kind == JsonKind.Array)
                {
                    var kept = new List<JsonValue>();
                    bool anyDropped = false;
                    foreach (var p in passives.AsArray)
                    {
                        var passiveId = p.Kind == JsonKind.String ? p.AsString : null;
                        bool resolvable = !string.IsNullOrEmpty(passiveId) &&
                            (recipeIdsInPack.Contains(passiveId) || passiveId.StartsWith("SKILL_", StringComparison.Ordinal));
                        if (resolvable)
                        {
                            kept.Add(p);
                        }
                        else
                        {
                            anyDropped = true;
                            findings.Add(Finding.Error("CF_STATUS_PASSIVE_UNRESOLVED",
                                $"Status '{id}' in pack '{pack.Manifest.Id}' has a Passives entry '{passiveId ?? p.ToString()}' that resolves to neither a same-pack recipe id nor a SKILL_-prefixed token — dropped.",
                                pack.Manifest.Id));
                        }
                    }
                    if (anyDropped)
                        value = WithReplacedMember(value, "Passives", JsonValue.NewArray(kept));
                }

                result[id] = value;
            }

            return result;
        }

        /// <summary>Rebuilds a JSON object with one member's value replaced, preserving every other member
        /// and their original order (<see cref="JsonValue"/> is immutable). Used to drop an unresolved
        /// <c>Passives</c> entry without discarding the rest of a statuses.json entry.</summary>
        private static JsonValue WithReplacedMember(JsonValue obj, string key, JsonValue newValue)
        {
            var members = new List<KeyValuePair<string, JsonValue>>();
            bool replaced = false;
            foreach (var kv in obj.AsObjectMembers)
            {
                if (string.Equals(kv.Key, key, StringComparison.Ordinal))
                {
                    members.Add(new KeyValuePair<string, JsonValue>(key, newValue));
                    replaced = true;
                }
                else
                {
                    members.Add(kv);
                }
            }
            if (!replaced)
                members.Add(new KeyValuePair<string, JsonValue>(key, newValue));
            return JsonValue.NewObject(members);
        }

        /// <summary>
        /// modifiers.json (Encounter Modifiers spec §3.2) — parses into a <see cref="ModifierTable"/>, a
        /// ClassForge-owned registry (like skillrecipes.json), not a <c>Configs.*</c> merge category. Returns
        /// null when the pack ships no modifiers.json. Authored <c>Modifiers</c> array order is preserved —
        /// it is the weighted-pick walk order (spec §6.3) and is never re-sorted.
        /// </summary>
        private static ModifierTable ParseModifiersFile(IFileSource fs, DiscoveredPack pack, List<Finding> findings, Dictionary<string, JsonValue> statusesInPack)
        {
            var path = fs.CombinePath(pack.RootDir, "modifiers.json");
            if (!fs.FileExists(path)) return null;

            JsonValue json;
            try
            {
                var text = fs.ReadAllText(path);
                json = JsonParser.Parse(text);
                if (json.Kind != JsonKind.Object)
                    throw new FormatException("modifiers.json root must be a JSON object.");
            }
            catch (Exception ex)
            {
                findings.Add(Finding.Error("CF_FILE_PARSE", $"Failed to parse modifiers.json in pack '{pack.Manifest.Id}': {ex.Message}", pack.Manifest.Id));
                return null;
            }

            var table = new ModifierTable { PackId = pack.Manifest.Id };
            table.SchemaVersion = json.GetString("SchemaVersion");
            if (string.IsNullOrEmpty(table.SchemaVersion))
            {
                findings.Add(Finding.Error("CF_MODIFIERS_SCHEMA_MISSING", $"modifiers.json in pack '{pack.Manifest.Id}' is missing SchemaVersion.", pack.Manifest.Id));
            }
            else if (!string.Equals(table.SchemaVersion, "1.0", StringComparison.Ordinal))
            {
                findings.Add(Finding.Warning("CF_MODIFIERS_SCHEMA_UNKNOWN",
                    $"modifiers.json in pack '{pack.Manifest.Id}' declares SchemaVersion '{table.SchemaVersion}' (only \"1.0\" is defined, Encounter Modifiers spec §3.2).",
                    pack.Manifest.Id));
            }

            table.SelectionRecipe = json.Get("Selection").GetString("Recipe");
            if (string.IsNullOrEmpty(table.SelectionRecipe))
                findings.Add(Finding.Error("CF_MODIFIERS_SELECTION_MISSING", $"modifiers.json in pack '{pack.Manifest.Id}' is missing Selection.Recipe.", pack.Manifest.Id));

            var modifiersNode = json.Get("Modifiers");
            if (modifiersNode.Kind != JsonKind.Array)
            {
                findings.Add(Finding.Error("CF_MODIFIERS_SHAPE", $"modifiers.json in pack '{pack.Manifest.Id}': Modifiers must be an array.", pack.Manifest.Id));
                return table;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in modifiersNode.AsArray) // authored order preserved -- load-bearing, spec §6.3
            {
                if (item.Kind != JsonKind.Object)
                {
                    findings.Add(Finding.Error("CF_MODIFIER_SHAPE", $"modifiers.json in pack '{pack.Manifest.Id}' has a Modifiers entry that is not an object.", pack.Manifest.Id));
                    continue;
                }

                var id = item.GetString("Id");
                if (string.IsNullOrEmpty(id))
                {
                    findings.Add(Finding.Error("CF_MODIFIER_ID_MISSING", $"modifiers.json in pack '{pack.Manifest.Id}' has a Modifiers entry with no Id.", pack.Manifest.Id));
                    continue;
                }
                if (!seenIds.Add(id))
                {
                    findings.Add(Finding.Error("CF_MODIFIER_DUP_ID", $"modifiers.json in pack '{pack.Manifest.Id}' has duplicate Modifier Id '{id}'.", pack.Manifest.Id));
                    continue;
                }

                var entry = new ModifierEntry { Id = id };

                var weightVal = item.Get("Weight");
                if (weightVal.Kind != JsonKind.Number)
                {
                    findings.Add(Finding.Error("CF_MODIFIER_WEIGHT_MISSING", $"Modifier '{id}' in pack '{pack.Manifest.Id}' is missing a numeric Weight.", pack.Manifest.Id));
                    continue;
                }
                entry.Weight = (int)weightVal.AsNumber;
                if (entry.Weight < 1)
                {
                    findings.Add(Finding.Error("CF_MODIFIER_WEIGHT_RANGE", $"Modifier '{id}' in pack '{pack.Manifest.Id}' has Weight {entry.Weight} (must be >= 1).", pack.Manifest.Id));
                    continue;
                }

                entry.Status = item.GetString("Status");
                if (string.IsNullOrEmpty(entry.Status))
                {
                    findings.Add(Finding.Error("CF_MODIFIER_STATUS_MISSING", $"Modifier '{id}' in pack '{pack.Manifest.Id}' is missing Status.", pack.Manifest.Id));
                    continue;
                }
                if (!statusesInPack.ContainsKey(entry.Status))
                {
                    // §3.4: "must resolve to a status id merged by the same pack OR a live vanilla id." Core
                    // has no live game data at parse time (that only exists at the Plugin's merge-time
                    // LiveIdSets snapshot, M-EM2) -- same-pack resolution is the only thing checkable here.
                    // A ref that is also not even a plausible vanilla status name (an eStatusEffectTypes
                    // member -- several of which double as real vanilla StatusEffectConfig ids, e.g. "CURSE")
                    // is flagged as dangling outright; one that IS plausible is recorded at Info so the
                    // deferred vanilla-id check stays visible without blocking the pack.
                    if (KnownStatusEffectTypes.Contains(entry.Status))
                    {
                        findings.Add(Finding.Info("CF_MODIFIER_STATUS_VANILLA_DEFERRED",
                            $"Modifier '{id}' in pack '{pack.Manifest.Id}' references Status '{entry.Status}', not defined by this pack's statuses.json. It matches a plausible vanilla status name — resolution against the live game's Configs.StatusEffects is deferred to the Plugin (M-EM2).",
                            pack.Manifest.Id));
                    }
                    else
                    {
                        findings.Add(Finding.Error("CF_MODIFIER_STATUS_DANGLING",
                            $"Modifier '{id}' in pack '{pack.Manifest.Id}' references Status '{entry.Status}', which is defined neither by this pack's statuses.json nor recognizable as a vanilla status name.",
                            pack.Manifest.Id));
                        continue;
                    }
                }

                var maxHp = item.Get("MaxHpPercent");
                if (maxHp.Kind == JsonKind.Number) entry.MaxHpPercent = (int)maxHp.AsNumber;

                var rewardsNode = item.Get("Rewards");
                if (rewardsNode.Kind == JsonKind.Object)
                {
                    entry.Rewards = new ModifierRewards();
                    var xp = rewardsNode.Get("XpBonusPercent");
                    if (xp.Kind == JsonKind.Number) entry.Rewards.XpBonusPercent = (int)xp.AsNumber;
                    var gold = rewardsNode.Get("GoldBonusPercent");
                    if (gold.Kind == JsonKind.Number) entry.Rewards.GoldBonusPercent = (int)gold.AsNumber;
                    var loot = rewardsNode.Get("ExtraLootChancePercent");
                    if (loot.Kind == JsonKind.Number) entry.Rewards.ExtraLootChancePercent = (int)loot.AsNumber;

                    findings.Add(Finding.Info("CF_MODIFIER_REWARDS_DEFERRED",
                        $"Modifier '{id}' in pack '{pack.Manifest.Id}' declares Rewards; the loot-grant verb engine that consumes them has not shipped (M-EM4, spec §11) — parsed and validated, currently inert.",
                        pack.Manifest.Id));
                }

                table.Modifiers.Add(entry);
            }

            return table;
        }
    }
}
