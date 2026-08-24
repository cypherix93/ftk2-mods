using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Json;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Parsing
{
    /// <summary>
    /// statmodifiers.json parse + validate (conditional-stat-modifier spec §2, M-CS1). Same posture as
    /// <see cref="RecipeParser"/>: never throws on bad data — an Error finding disables the offending
    /// modifier and the rest of the file loads.
    /// </summary>
    public static class StatModifierParser
    {
        private static readonly string[] ModifierFields =
        {
            "Enabled", "Stat", "Percent", "PercentFrom", "PerUnit", "Max", "Flat",
            "Floor", "RequiresPositiveBase", "Conditions"
        };

        private static readonly string[] ConditionFields = { "Type", "Negate", "Name", "Comparator", "Value" };

        /// <summary>Every real <c>eCharacterStats</c> member a modifier may target (game decompile
        /// <c>eCharacterStats.cs</c>, EGT §6). <c>RND</c>/<c>NONE</c> are meaningless targets and
        /// <c>CURRENT_FOCUS</c> short-circuits before the GetStat postfix ever runs (CH L422 first line),
        /// so all three are deliberately absent.</summary>
        private static readonly HashSet<string> LegalStats = new HashSet<string>(StringComparer.Ordinal)
        {
            "STR", "VIT", "INT", "AWR", "TAL", "SPD", "LCK", "DEF", "RES", "EVD", "PA", "SA",
            "MXFOC", "FOC", "MXHP", "HP", "XP", "ACC", "ATK", "MAG", "PHY", "PRW", "CRT", "CRTD",
            "GLD", "XPM", "SKL", "HRG", "LOP", "THRN", "MOV", "LBM", "WBM", "FIND",
            "PSTR", "PVIT", "PINT", "PAWR", "PTAL", "PSPD", "PLCK", "PDEF", "PRES", "PEVD", "PFOC",
            "PGLD", "DAM", "PARTY_XP"
        };

        /// <summary>Spec §3.3's legal read-time condition set, narrowed to what the read-time evaluator
        /// actually implements in v1 (recorded deviation: the spec lists more; admitting a condition the
        /// runtime cannot evaluate would turn a static guarantee into a silent no-op).</summary>
        private static readonly ConditionKind[] LegalConditions =
        {
            ConditionKind.COUNTER, ConditionKind.PARTY_HAS_FOLLOWER
        };

        public static StatModifierSet Parse(string json)
        {
            var set = new StatModifierSet();
            JsonValue root;
            string error;
            if (!JsonParser.TryParse(json, out root, out error))
            {
                set.AddFinding(new Finding(FindingSeverity.Error, "", "", "E_JSON", "statmodifiers.json is not valid JSON: " + error));
                return set;
            }
            if (root.Kind != JsonKind.Object)
            {
                set.AddFinding(new Finding(FindingSeverity.Error, "", "", "E_SHAPE", "statmodifiers.json root must be an object keyed by modifier id"));
                return set;
            }

            foreach (var id in root.Keys)
            {
                var m = ParseModifier(set, id, root.Get(id));
                if (m != null) set.Add(m);
            }
            return set;
        }

        private static StatModifier ParseModifier(StatModifierSet set, string id, JsonValue node)
        {
            var m = new StatModifier { Id = id };

            if (node == null || node.Kind != JsonKind.Object)
            {
                Err(set, m, "", "E_SHAPE", "modifier must be an object");
                return m;
            }

            foreach (var key in node.Keys)
            {
                bool known = false;
                for (int i = 0; i < ModifierFields.Length; i++)
                    if (string.Equals(ModifierFields[i], key, StringComparison.Ordinal)) { known = true; break; }
                if (!known)
                    set.AddFinding(new Finding(FindingSeverity.Warning, id, key, "W_UNKNOWN_FIELD",
                        "unknown field '" + key + "' is ignored"));
            }

            var enabledNode = node.Get("Enabled");
            if (enabledNode != null && enabledNode.Kind == JsonKind.Bool) m.Enabled = enabledNode.BoolValue;

            m.Stat = Str(node, "Stat");
            if (string.IsNullOrEmpty(m.Stat))
                Err(set, m, "Stat", "E_STAT_MISSING", "Stat is required");
            else if (!LegalStats.Contains(m.Stat))
                Err(set, m, "Stat", "E_STAT_UNKNOWN", "'" + m.Stat + "' is not a legal eCharacterStats member");

            m.Percent = OptInt(set, m, node, "Percent");
            m.PercentFrom = Str(node, "PercentFrom");
            m.PerUnit = OptInt(set, m, node, "PerUnit");
            m.Max = OptInt(set, m, node, "Max");
            var floor = OptInt(set, m, node, "Floor");
            if (floor.HasValue) m.Floor = floor.Value;
            var rpb = node.Get("RequiresPositiveBase");
            if (rpb != null && rpb.Kind == JsonKind.Bool) m.RequiresPositiveBase = rpb.BoolValue;

            bool hasPercent = m.Percent.HasValue;
            bool hasFrom = !string.IsNullOrEmpty(m.PercentFrom);
            if (hasPercent && hasFrom)
                Err(set, m, "Percent", "E_PCT_BOTH", "Percent and PercentFrom are mutually exclusive");
            else if (!hasPercent && !hasFrom)
                Err(set, m, "Percent", "E_PCT_NONE", "exactly one of Percent or PercentFrom is required");

            if (hasPercent && (m.Percent.Value < -99 || m.Percent.Value > 500))
                Err(set, m, "Percent", "E_PCT_RANGE", "Percent must be within -99..500");

            if (hasFrom)
            {
                if (!m.PercentFrom.StartsWith(Vocabulary.SourceCounterPrefix, StringComparison.Ordinal) ||
                    m.PercentFrom.Length <= Vocabulary.SourceCounterPrefix.Length)
                    Err(set, m, "PercentFrom", "E_PCT_SOURCE", "PercentFrom must be a 'COUNTER:<name>' token");
                if (!m.PerUnit.HasValue)
                    Err(set, m, "PerUnit", "E_PCT_PERUNIT", "PercentFrom requires PerUnit");
            }

            var condNode = node.Get("Conditions");
            if (condNode != null)
            {
                if (condNode.Kind != JsonKind.Array)
                    Err(set, m, "Conditions", "E_SHAPE", "Conditions must be an array");
                else
                {
                    for (int i = 0; i < condNode.Items.Count; i++)
                    {
                        var c = ParseCondition(set, m, condNode.Items[i], "Conditions[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                        if (c != null) m.Conditions.Add(c);
                    }
                }
            }

            return m;
        }

        private static RecipeCondition ParseCondition(StatModifierSet set, StatModifier m, JsonValue node, string path)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                Err(set, m, path, "E_SHAPE", "condition must be an object");
                return null;
            }

            foreach (var key in node.Keys)
            {
                bool known = false;
                for (int i = 0; i < ConditionFields.Length; i++)
                    if (string.Equals(ConditionFields[i], key, StringComparison.Ordinal)) { known = true; break; }
                if (!known)
                    set.AddFinding(new Finding(FindingSeverity.Warning, m.Id, path + "." + key, "W_UNKNOWN_FIELD",
                        "unknown field '" + key + "' is ignored"));
            }

            var c = new RecipeCondition();
            c.RawType = Str(node, "Type");
            ConditionKind kind;
            if (string.IsNullOrEmpty(c.RawType) || !Vocabulary.TryParseCondition(c.RawType, out kind))
            {
                Err(set, m, path + ".Type", "E_COND_UNKNOWN", "unknown condition token '" + (c.RawType ?? "(missing)") + "'");
                return null;
            }
            c.Type = kind;

            bool legal = false;
            for (int i = 0; i < LegalConditions.Length; i++)
                if (LegalConditions[i] == kind) { legal = true; break; }
            if (!legal)
            {
                // Spec §3.1 enforcement 2: conditions that resolve via GetStat (or that have no read-time
                // evaluator) are a static Error here, not a runtime surprise.
                Err(set, m, path + ".Type", "E_COND_ILLEGAL",
                    "condition '" + c.RawType + "' is not legal in a CONDITIONAL_STAT_MODIFIER context " +
                    "(read-time set: COUNTER, PARTY_HAS_FOLLOWER)");
                return null;
            }

            var negNode = node.Get("Negate");
            if (negNode != null && negNode.Kind == JsonKind.Bool) c.Negate = negNode.BoolValue;
            c.Name = Str(node, "Name");

            var valueNode = node.Get("Value");
            if (valueNode != null && valueNode.Kind == JsonKind.Number)
            {
                c.ValueInt = (int)valueNode.NumberValue;
                c.Value = c.ValueInt.Value.ToString(CultureInfo.InvariantCulture);
            }

            string cmpTok = Str(node, "Comparator");
            if (!string.IsNullOrEmpty(cmpTok))
            {
                Comparator cmp;
                if (Enum.TryParse<Comparator>(cmpTok, out cmp)) { c.Comparator = cmp; c.HasComparator = true; }
                else Err(set, m, path + ".Comparator", "E_CMP_UNKNOWN", "unknown Comparator token '" + cmpTok + "'");
            }

            if (kind == ConditionKind.COUNTER && string.IsNullOrEmpty(c.Name))
                Err(set, m, path + ".Name", "E_COND_COUNTER_NAME", "COUNTER requires Name");

            return c;
        }

        private static string Str(JsonValue node, string key)
        {
            var v = node.Get(key);
            return v != null && v.Kind == JsonKind.String ? v.StringValue : null;
        }

        private static int? OptInt(StatModifierSet set, StatModifier m, JsonValue node, string key)
        {
            var v = node.Get(key);
            if (v == null) return null;
            if (v.Kind != JsonKind.Number)
            {
                Err(set, m, key, "E_SHAPE", key + " must be a number");
                return null;
            }
            return (int)v.NumberValue;
        }

        private static void Err(StatModifierSet set, StatModifier m, string path, string code, string message)
        {
            set.AddFinding(new Finding(FindingSeverity.Error, m.Id, path, code, message));
            m.DisabledByValidator = true;
        }
    }
}
