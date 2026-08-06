using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Json;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Parsing
{
    /// <summary>
    /// <c>skillrecipes.json</c> → <see cref="RecipeSet"/>.
    /// <para><b>Never throws.</b> Malformed JSON, wrong node kinds and unknown tokens all become
    /// <see cref="Finding"/>s; a recipe that produced any Error finding is marked
    /// <see cref="SkillRecipe.DisabledByValidator"/> and is never evaluated (fail-safe, charter rule 1).</para>
    /// </summary>
    public static class RecipeParser
    {
        private static readonly string[] RecipeFields =
        {
            "SchemaVersion", "DisplayName", "Enabled", "Trigger", "Conditions", "Effects",
            "ProcChance", "AiProcChance", "Budget", "Cooldown", "Priority", "VerboseLogTag",
            "PickOneEffect"
        };

        private static readonly string[] BudgetFields = { "Scope", "ConsumeOn", "Key" };

        private static readonly string[] ConditionFields =
        {
            "Type", "Negate", "Of", "Value", "Comparator", "Percent", "Flat",
            "Category", "Types", "Name"
        };

        private static readonly string[] EffectFields =
        {
            "Type", "Conditions", "Target", "Rank", "Status", "FallbackStatus", "StatusOneOf", "Duration",
            "Stat", "StatChangeType", "FlatValue", "FlatPercent", "Blockable", "IsSilent",
            "SummonType", "CharacterConfig", "Count",
            "Percent", "Flat", "MinDelta", "Scope", "Name", "Delta", "Value",
            "FlatValueFrom", "PercentFrom", "PerUnit", "Min", "Max",
            // loot-grant effects (ON_COMBAT_LOOT only, SchemaVersion 1.2, verb spec §6.2)
            "MinGold", "MaxGold", "Tag", "Rarity", "Stack", "ConfigName", "ChancePct", "Table"
        };

        private static readonly string[] RankFields = { "Stat", "Order", "Where", "ExcludeSelf" };

        /// <summary>Parses a recipe file. The returned set is always usable, possibly empty.</summary>
        public static RecipeSet Parse(string json)
        {
            var set = new RecipeSet();
            JsonValue root;
            string error;
            if (!JsonParser.TryParse(json, out root, out error))
            {
                set.AddFinding(new Finding(FindingSeverity.Error, "", "", "E_JSON",
                    "skillrecipes.json is not valid JSON: " + error));
                return set;
            }
            if (root.Kind != JsonKind.Object)
            {
                set.AddFinding(new Finding(FindingSeverity.Error, "", "", "E_ROOT",
                    "top-level value must be an object of { recipeId: recipe }, saw " + root.Kind));
                return set;
            }

            // Recipe ids are read in source order; RecipeSet re-sorts to (Priority, ordinal id).
            var ids = new List<string>(root.Keys);
            for (int i = 0; i < ids.Count; i++)
            {
                var recipe = ParseRecipe(set, ids[i], root.Get(ids[i]));
                if (recipe != null) set.Add(recipe);
            }
            RecipeValidator.Validate(set);
            return set;
        }

        private static SkillRecipe ParseRecipe(RecipeSet set, string id, JsonValue node)
        {
            var r = new SkillRecipe { Id = id };
            if (node == null || node.Kind != JsonKind.Object)
            {
                Err(set, r, "", "E_SHAPE", "recipe body must be an object");
                return r;
            }

            WarnUnknownFields(set, r, node, RecipeFields, "");

            r.SchemaVersion = Str(node, "SchemaVersion", null);
            if (string.IsNullOrEmpty(r.SchemaVersion))
            {
                r.SchemaVersion = Vocabulary.SchemaVersionLegacy;
                Warn(set, r, "SchemaVersion", "W_SCHEMA_DEFAULT",
                    "no SchemaVersion; assuming " + Vocabulary.SchemaVersionLegacy + " (v1 vocabulary only)");
            }

            r.DisplayName = Str(node, "DisplayName", id);
            r.Enabled = Bool(set, r, node, "Enabled", true);
            r.VerboseLogTag = Str(node, "VerboseLogTag", r.DisplayName);

            r.RawTrigger = Str(node, "Trigger", null);
            TriggerKind trig;
            if (string.IsNullOrEmpty(r.RawTrigger))
            {
                Err(set, r, "Trigger", "E_TRIGGER_MISSING", "Trigger is required");
            }
            else if (!Vocabulary.TryParseTrigger(r.RawTrigger, out trig))
            {
                Err(set, r, "Trigger", "E_TRIGGER_UNKNOWN", "unknown trigger token '" + r.RawTrigger + "'");
            }
            else
            {
                r.Trigger = trig;
                if (trig == TriggerKind.ON_DAMAGED)
                {
                    // SPEC-DELTA-v1.1 §2.1: loader accepts it, logs a one-time rename warning.
                    r.Trigger = TriggerKind.ON_DAMAGE_TAKEN;
                    Warn(set, r, "Trigger", "W_TRIGGER_DEPRECATED",
                        "ON_DAMAGED is a deprecated alias of ON_DAMAGE_TAKEN; rename it");
                }
            }

            r.ProcChance = Int(set, r, node, "ProcChance", 100, "ProcChance");
            r.AiProcChance = Int(set, r, node, "AiProcChance", r.ProcChance, "AiProcChance");
            r.Cooldown = Int(set, r, node, "Cooldown", 0, "Cooldown");
            r.Priority = Int(set, r, node, "Priority", 0, "Priority");
            r.PickOneEffect = Bool(set, r, node, "PickOneEffect", false);

            var budgetNode = node.Get("Budget");
            if (budgetNode != null)
            {
                if (budgetNode.Kind != JsonKind.Object)
                {
                    Err(set, r, "Budget", "E_SHAPE", "Budget must be an object");
                }
                else
                {
                    WarnUnknownFields(set, r, budgetNode, BudgetFields, "Budget");
                    string scopeTok = Str(budgetNode, "Scope", "NONE");
                    BudgetScope scope;
                    if (!TryEnum(scopeTok, out scope))
                        Err(set, r, "Budget.Scope", "E_BUDGET_SCOPE", "unknown Budget.Scope token '" + scopeTok + "'");
                    else r.Budget.Scope = scope;

                    string consumeTok = Str(budgetNode, "ConsumeOn", "PROC");
                    ConsumeOn consume;
                    if (!TryEnum(consumeTok, out consume))
                        Err(set, r, "Budget.ConsumeOn", "E_BUDGET_CONSUME", "unknown Budget.ConsumeOn token '" + consumeTok + "'");
                    else r.Budget.ConsumeOn = consume;

                    r.Budget.Key = Str(budgetNode, "Key", null);
                }
            }

            var condNode = node.Get("Conditions");
            r.Conditions = ParseConditions(set, r, condNode, "Conditions");

            var effNode = node.Get("Effects");
            if (effNode == null)
            {
                Err(set, r, "Effects", "E_NO_EFFECTS", "Effects is required and must contain at least one entry");
            }
            else if (effNode.Kind != JsonKind.Array)
            {
                Err(set, r, "Effects", "E_SHAPE", "Effects must be an array");
            }
            else
            {
                for (int i = 0; i < effNode.Items.Count; i++)
                {
                    var e = ParseEffect(set, r, effNode.Items[i], "Effects[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    if (e != null) r.Effects.Add(e);
                }
                if (r.Effects.Count == 0)
                    Err(set, r, "Effects", "E_NO_EFFECTS", "Effects is empty; a recipe that plans nothing is an authoring error");
            }

            return r;
        }

        private static List<RecipeCondition> ParseConditions(RecipeSet set, SkillRecipe r, JsonValue node, string path)
        {
            var list = new List<RecipeCondition>();
            if (node == null) return list;
            if (node.Kind != JsonKind.Array)
            {
                Err(set, r, path, "E_SHAPE", path + " must be an array");
                return list;
            }
            for (int i = 0; i < node.Items.Count; i++)
            {
                var c = ParseCondition(set, r, node.Items[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                if (c != null) list.Add(c);
            }
            return list;
        }

        private static RecipeCondition ParseCondition(RecipeSet set, SkillRecipe r, JsonValue node, string path)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                Err(set, r, path, "E_SHAPE", "condition must be an object");
                return null;
            }
            WarnUnknownFields(set, r, node, ConditionFields, path);

            var c = new RecipeCondition();
            c.RawType = Str(node, "Type", null);
            ConditionKind kind;
            if (string.IsNullOrEmpty(c.RawType))
            {
                Err(set, r, path + ".Type", "E_COND_MISSING", "condition Type is required");
                return null;
            }
            if (!Vocabulary.TryParseCondition(c.RawType, out kind))
            {
                Err(set, r, path + ".Type", "E_COND_UNKNOWN", "unknown condition token '" + c.RawType + "'");
                return null;
            }
            c.Type = kind;
            c.Negate = Bool(set, r, node, "Negate", false);

            // SPEC-DELTA-v1.1 §3: TARGET_BASE_TYPE_NOT is a deprecated alias of
            // {TARGET_BASE_TYPE, Negate:true}. Rewrite at parse time so the evaluator has one code path.
            if (kind == ConditionKind.TARGET_BASE_TYPE_NOT)
            {
                c.Type = ConditionKind.TARGET_BASE_TYPE;
                c.Negate = !c.Negate;
                Warn(set, r, path + ".Type", "W_COND_DEPRECATED",
                    "TARGET_BASE_TYPE_NOT is a deprecated alias of {TARGET_BASE_TYPE, Negate:true}");
            }

            string ofTok = Str(node, "Of", null);
            if (!string.IsNullOrEmpty(ofTok))
            {
                OfSelector of;
                if (!TryEnum(ofTok, out of))
                    Err(set, r, path + ".Of", "E_OF_UNKNOWN", "unknown Of token '" + ofTok + "'");
                else c.Of = of;
            }

            var valueNode = node.Get("Value");
            if (valueNode != null)
            {
                switch (valueNode.Kind)
                {
                    case JsonKind.String: c.Value = valueNode.StringValue; break;
                    case JsonKind.Bool: c.ValueBool = valueNode.BoolValue; c.Value = valueNode.BoolValue ? "true" : "false"; break;
                    case JsonKind.Number: c.ValueInt = (int)valueNode.NumberValue; c.Value = c.ValueInt.Value.ToString(CultureInfo.InvariantCulture); break;
                    default:
                        Err(set, r, path + ".Value", "E_SHAPE", "Value must be a string, bool or number");
                        break;
                }
            }

            string cmpTok = Str(node, "Comparator", null);
            if (!string.IsNullOrEmpty(cmpTok))
            {
                Comparator cmp;
                if (!TryEnum(cmpTok, out cmp))
                    Err(set, r, path + ".Comparator", "E_CMP_UNKNOWN", "unknown Comparator token '" + cmpTok + "'");
                else { c.Comparator = cmp; c.HasComparator = true; }
            }

            c.Percent = OptInt(set, r, node, "Percent", path);
            c.Flat = OptInt(set, r, node, "Flat", path);
            c.Name = Str(node, "Name", null);

            string catTok = Str(node, "Category", null);
            if (!string.IsNullOrEmpty(catTok))
            {
                StatusCategory cat;
                if (!TryEnum(catTok, out cat))
                    Err(set, r, path + ".Category", "E_CAT_UNKNOWN", "unknown Category token '" + catTok + "'");
                else c.Category = cat;
            }

            var typesNode = node.Get("Types");
            if (typesNode != null)
            {
                if (typesNode.Kind != JsonKind.Array)
                    Err(set, r, path + ".Types", "E_SHAPE", "Types must be an array of eStatusEffectTypes members");
                else
                {
                    c.Types = new List<string>();
                    for (int i = 0; i < typesNode.Items.Count; i++)
                    {
                        if (typesNode.Items[i].Kind != JsonKind.String)
                            Err(set, r, path + ".Types", "E_SHAPE", "Types entries must be strings");
                        else c.Types.Add(typesNode.Items[i].StringValue);
                    }
                }
            }

            if (c.Type == ConditionKind.ROLL_TIER)
            {
                RollTier tier;
                if (string.IsNullOrEmpty(c.Value) || !TryEnum(c.Value, out tier))
                    Err(set, r, path + ".Value", "E_TIER_UNKNOWN", "ROLL_TIER Value must be PERFECT|SUCCESS|FAIL|CRIT_FAIL");
                else c.Tier = tier;
            }

            return c;
        }

        private static RecipeEffect ParseEffect(RecipeSet set, SkillRecipe r, JsonValue node, string path)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                Err(set, r, path, "E_SHAPE", "effect must be an object");
                return null;
            }
            WarnUnknownFields(set, r, node, EffectFields, path);

            var e = new RecipeEffect();
            e.RawType = Str(node, "Type", null);
            EffectKind kind;
            if (string.IsNullOrEmpty(e.RawType))
            {
                Err(set, r, path + ".Type", "E_EFFECT_MISSING", "effect Type is required");
                return null;
            }
            if (!Vocabulary.TryParseEffect(e.RawType, out kind))
            {
                Err(set, r, path + ".Type", "E_EFFECT_UNKNOWN", "unknown effect token '" + e.RawType + "'");
                return null;
            }
            e.Type = kind;

            e.Conditions = ParseConditions(set, r, node.Get("Conditions"), path + ".Conditions");

            string targetTok = Str(node, "Target", null);
            if (!string.IsNullOrEmpty(targetTok))
            {
                TargetKind tk;
                if (!Vocabulary.TryParseTarget(targetTok, out tk))
                    Err(set, r, path + ".Target", "E_TARGET_UNKNOWN", "unknown Target token '" + targetTok + "'");
                else e.Target = tk;
            }

            var rankNode = node.Get("Rank");
            if (rankNode != null)
            {
                if (rankNode.Kind != JsonKind.Object)
                {
                    Err(set, r, path + ".Rank", "E_SHAPE", "Rank must be an object");
                }
                else
                {
                    WarnUnknownFields(set, r, rankNode, RankFields, path + ".Rank");
                    e.Rank = new RankSpec();
                    e.Rank.Stat = Str(rankNode, "Stat", "HP_PCT");
                    string orderTok = Str(rankNode, "Order", "LOWEST");
                    RankOrder order;
                    if (!TryEnum(orderTok, out order))
                        Err(set, r, path + ".Rank.Order", "E_RANK_ORDER", "unknown Rank.Order token '" + orderTok + "'");
                    else e.Rank.Order = order;
                    e.Rank.ExcludeSelf = Bool(set, r, rankNode, "ExcludeSelf", true);
                    e.Rank.Where = ParseConditions(set, r, rankNode.Get("Where"), path + ".Rank.Where");
                }
            }

            e.Status = Str(node, "Status", null);
            e.FallbackStatus = Str(node, "FallbackStatus", null);
            var oneOf = node.Get("StatusOneOf");
            if (oneOf != null)
            {
                if (oneOf.Kind != JsonKind.Array)
                    Err(set, r, path + ".StatusOneOf", "E_SHAPE", "StatusOneOf must be an array of status ids");
                else
                {
                    e.StatusOneOf = new List<string>();
                    for (int i = 0; i < oneOf.Items.Count; i++)
                    {
                        if (oneOf.Items[i].Kind != JsonKind.String)
                            Err(set, r, path + ".StatusOneOf", "E_SHAPE", "StatusOneOf entries must be strings");
                        else e.StatusOneOf.Add(oneOf.Items[i].StringValue);
                    }
                    if (e.StatusOneOf.Count == 0)
                        Err(set, r, path + ".StatusOneOf", "E_SHAPE", "StatusOneOf must not be empty");
                }
            }
            e.Duration = OptInt(set, r, node, "Duration", path);

            e.Stat = Str(node, "Stat", null);
            e.StatChangeType = Str(node, "StatChangeType", null);
            e.FlatValue = OptInt(set, r, node, "FlatValue", path);
            e.FlatPercent = OptInt(set, r, node, "FlatPercent", path);
            e.Blockable = Bool(set, r, node, "Blockable", false);
            e.IsSilent = Bool(set, r, node, "IsSilent", false);

            string summonTok = Str(node, "SummonType", null);
            if (!string.IsNullOrEmpty(summonTok))
            {
                SummonType st;
                if (!TryEnum(summonTok, out st))
                    Err(set, r, path + ".SummonType", "E_SUMMON_TYPE",
                        "unknown SummonType token '" + summonTok + "' (eSummonTypes: SPECIFIC|RANDOM|PLAYTHING|AS_FOLLOWER; NONE is never authored)");
                else e.SummonType = st;
            }
            e.CharacterConfig = Str(node, "CharacterConfig", null);
            e.Count = Int(set, r, node, "Count", 1, path + ".Count");

            e.Percent = OptInt(set, r, node, "Percent", path);
            e.Flat = OptInt(set, r, node, "Flat", path);
            e.MinDelta = OptInt(set, r, node, "MinDelta", path);

            string scopeTok = Str(node, "Scope", null);
            if (!string.IsNullOrEmpty(scopeTok))
            {
                HealScope hs;
                if (!TryEnum(scopeTok, out hs))
                    Err(set, r, path + ".Scope", "E_HEAL_SCOPE", "unknown HEAL_MODIFIER Scope token '" + scopeTok + "'");
                else e.Scope = hs;
            }

            e.Name = Str(node, "Name", null);
            e.Delta = Int(set, r, node, "Delta", 0, path + ".Delta");
            e.Value = Int(set, r, node, "Value", 0, path + ".Value");

            e.FlatValueFrom = Str(node, "FlatValueFrom", null);
            e.PercentFrom = Str(node, "PercentFrom", null);
            e.PerUnit = OptInt(set, r, node, "PerUnit", path);
            e.Min = OptInt(set, r, node, "Min", path);
            e.Max = OptInt(set, r, node, "Max", path);

            // loot-grant effects (ON_COMBAT_LOOT only, SchemaVersion 1.2, verb spec §6.2)
            e.MinGold = OptInt(set, r, node, "MinGold", path);
            e.MaxGold = OptInt(set, r, node, "MaxGold", path);
            e.Tag = Str(node, "Tag", null);
            e.Rarity = Str(node, "Rarity", null);
            e.Stack = Int(set, r, node, "Stack", 1, path + ".Stack");
            e.ConfigName = Str(node, "ConfigName", null);
            e.ChancePct = OptInt(set, r, node, "ChancePct", path);
            e.Table = Str(node, "Table", null);

            return e;
        }

        // ----- primitive readers -------------------------------------------------------

        private static string Str(JsonValue node, string key, string fallback)
        {
            var v = node.Get(key);
            if (v == null || v.Kind != JsonKind.String) return fallback;
            return v.StringValue;
        }

        private static bool Bool(RecipeSet set, SkillRecipe r, JsonValue node, string key, bool fallback)
        {
            var v = node.Get(key);
            if (v == null) return fallback;
            if (v.Kind != JsonKind.Bool)
            {
                Err(set, r, key, "E_SHAPE", key + " must be a boolean");
                return fallback;
            }
            return v.BoolValue;
        }

        private static int Int(RecipeSet set, SkillRecipe r, JsonValue node, string key, int fallback, string path)
        {
            var v = node.Get(key);
            if (v == null) return fallback;
            if (v.Kind != JsonKind.Number)
            {
                Err(set, r, path, "E_SHAPE", key + " must be a number");
                return fallback;
            }
            return (int)v.NumberValue;
        }

        private static int? OptInt(RecipeSet set, SkillRecipe r, JsonValue node, string key, string path)
        {
            var v = node.Get(key);
            if (v == null) return null;
            if (v.Kind != JsonKind.Number)
            {
                Err(set, r, path + "." + key, "E_SHAPE", key + " must be a number");
                return null;
            }
            return (int)v.NumberValue;
        }

        private static bool TryEnum<T>(string token, out T value) where T : struct
        {
            value = default(T);
            if (string.IsNullOrEmpty(token)) return false;
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, token, StringComparison.Ordinal))
                {
                    value = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }
            return false;
        }

        private static void WarnUnknownFields(RecipeSet set, SkillRecipe r, JsonValue node, string[] known, string path)
        {
            for (int i = 0; i < node.Keys.Count; i++)
            {
                string k = node.Keys[i];
                bool found = false;
                for (int j = 0; j < known.Length; j++)
                    if (string.Equals(known[j], k, StringComparison.Ordinal)) { found = true; break; }
                if (!found)
                    Warn(set, r, (path.Length > 0 ? path + "." : "") + k, "W_UNKNOWN_FIELD",
                        "unknown field '" + k + "' is ignored");
            }
        }

        internal static void Err(RecipeSet set, SkillRecipe r, string path, string code, string message)
        {
            set.AddFinding(new Finding(FindingSeverity.Error, r.Id, path, code, message));
            r.DisabledByValidator = true;
        }

        internal static void Warn(RecipeSet set, SkillRecipe r, string path, string code, string message)
        {
            set.AddFinding(new Finding(FindingSeverity.Warning, r.Id, path, code, message));
        }
    }
}
