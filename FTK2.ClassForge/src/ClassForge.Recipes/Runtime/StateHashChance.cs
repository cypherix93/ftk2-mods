using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Runtime
{
    /// <summary>
    /// STATE_HASH_CHANCE (state-hash-chance spec §2.2): a deterministic, draw-free chance verdict —
    /// FNV-1a 32-bit over the UTF-16 code units of <c>salt|v0|v1|…</c>, verdict <c>h % 100 &lt; Percent</c>.
    /// Deliberately matches EOR 0.7.0.62's <c>ShouldShieldbearerMitigate</c> arithmetic verbatim
    /// (Plugin.cs L26754) so behavior comparison against upstream is a direct read.
    /// <para>Pure static, no game dependency, no state. NOT a roll: it takes zero RNG draws, and it is
    /// <b>correlated across re-evaluation with identical inputs</b> — the same tuple always yields the
    /// same verdict (spec §6; authors must include a per-evaluation-varying input such as
    /// <c>TRIGGER_DAMAGE</c>).</para>
    /// </summary>
    public static class StateHashChance
    {
        public static uint Fnv1a(string text)
        {
            uint h = 2166136261u;
            if (text == null) return h;
            for (int i = 0; i < text.Length; i++)
            {
                h ^= text[i];
                unchecked { h *= 16777619u; }
            }
            return h;
        }

        /// <summary>The canonical string (spec §2.2): salt, then each value in authored order, '|'-joined.
        /// Null values render as the empty string.</summary>
        public static string Canonical(string salt, IReadOnlyList<string> values)
        {
            var sb = new System.Text.StringBuilder(salt ?? "");
            if (values != null)
                for (int i = 0; i < values.Count; i++)
                    sb.Append('|').Append(values[i] ?? "");
            return sb.ToString();
        }

        public static bool Verdict(string salt, IReadOnlyList<string> values, int percent)
        {
            return Fnv1a(Canonical(salt, values)) % 100u < (uint)percent;
        }

        /// <summary>
        /// Resolves one §2.1 input token against a trigger context. Every resolver is wrapped per spec
        /// §3.3: a throw yields the agreed fallback ("0" for numeric tokens, "" for string tokens) and a
        /// once-per-(Salt, token) Warn through <paramref name="t"/>'s log — agreement matters more than
        /// correctness, but a systematically-throwing resolver must be visible.
        /// </summary>
        public static string ResolveInput(string token, string salt, TriggerContext t)
        {
            try
            {
                switch (token)
                {
                    case "SELF_GUID": return t.Owner != null ? (t.Owner.Guid ?? "") : "";
                    case "TRIGGER_SOURCE_GUID": return t.TriggerSource != null ? (t.TriggerSource.Guid ?? "") : "";
                    case "TRIGGER_TARGET_GUID": return t.TriggerTarget != null ? (t.TriggerTarget.Guid ?? "") : "";
                    case "COMBAT_ROUND": return Int(t.Runtime != null ? t.Runtime.Round : 0);
                    case "COMBAT_SEED": return Int(t.Ctx != null ? t.Ctx.CombatSeed : 0);
                    case "SELF_HP": return Int(t.Owner != null ? t.Owner.GetStat("HP") : 0);
                    case "SELF_FOCUS": return Int(t.Owner != null ? t.Owner.GetStat("FOC") : 0);
                    case "TRIGGER_DAMAGE": return Int(t.Amount);
                    case "TRIGGER_ITEM_ID": return t.ItemConfigName ?? "";
                    case "ABILITY_ID": return t.AbilityId ?? "";
                    case "RUN_SEED": return Int(t.Ctx != null ? t.Ctx.RunSeed : 0);
                    case "ENCOUNTER_GUID": return t.Ctx != null ? (t.Ctx.EncounterGuid ?? "") : "";
                    default: return "";   // unreachable past the validator's closed-set check
                }
            }
            catch (Exception ex)
            {
                if (t.Runtime != null && t.Runtime.MarkHashInputWarnedOnce(salt, token) && t.Log != null)
                    t.Log.Warn("[STATE_HASH_CHANCE] input resolver '" + token + "' threw for salt '" + (salt ?? "") +
                               "' — degraded to the agreed fallback value (spec 3.3). Logged once per (salt, token). " + ex.Message);
                return IsStringToken(token) ? "" : "0";
            }
        }

        /// <summary>Full condition evaluation: resolve the authored tuple, hash, verdict.</summary>
        public static bool Evaluate(RecipeCondition c, TriggerContext t)
        {
            if (c == null || c.Inputs == null || !c.Percent.HasValue) return false;
            var values = new string[c.Inputs.Count];
            for (int i = 0; i < c.Inputs.Count; i++)
                values[i] = ResolveInput(c.Inputs[i], c.Salt, t);
            return Verdict(c.Salt, values, c.Percent.Value);
        }

        private static string Int(int v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsStringToken(string token)
        {
            switch (token)
            {
                case "SELF_GUID":
                case "TRIGGER_SOURCE_GUID":
                case "TRIGGER_TARGET_GUID":
                case "TRIGGER_ITEM_ID":
                case "ABILITY_ID":
                case "ENCOUNTER_GUID":
                    return true;
                default:
                    return false;
            }
        }
    }
}
