using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Runtime
{
    /// <summary>
    /// The pure composition engine for CONDITIONAL_STAT_MODIFIER (spec §2/§3.4). Reproduces EOR
    /// 0.7.0.62's arithmetic verbatim (Plugin.cs L24571 positive / L24567 negative), composes in
    /// ascending ordinal Id order, and holds no state.
    /// </summary>
    public static class StatModifierEngine
    {
        public static int Compose(int baseValue, string stat, IReadOnlyList<StatModifier> modifiers,
            Func<StatModifier, bool> applies, Func<string, int> counterValue)
        {
            if (modifiers == null || modifiers.Count == 0 || string.IsNullOrEmpty(stat)) return baseValue;

            // Ascending ordinal Id (spec §3.4) — enforced here rather than trusted from the caller, so
            // composition order can never depend on registry build order.
            var ordered = new List<StatModifier>(modifiers);
            ordered.Sort(delegate (StatModifier a, StatModifier b) { return string.CompareOrdinal(a.Id, b.Id); });

            int result = baseValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                if (!m.IsLive) continue;
                if (!string.Equals(m.Stat, stat, StringComparison.Ordinal)) continue;
                if (applies != null && !applies(m)) continue;

                int pct = ResolvePercent(m, counterValue);
                if (pct == 0) continue;   // EOR gates on stacks > 0 — a resolved 0% is a true no-op, no floor bump
                if (m.RequiresPositiveBase && result <= 0) continue;

                if (pct > 0)
                {
                    // EOR62 L24571: result += Math.Max(1, Mathf.CeilToInt(result * multiplier)) —
                    // a positive percentage always moves the value by at least 1.
                    result += Math.Max(1, CeilToInt(result * (pct / 100f)));
                }
                else
                {
                    // EOR62 L24567 (KNIFE_EDGE): result = Math.Max(1, result - Mathf.CeilToInt(result * pct)).
                    result = Math.Max(m.Floor, result - CeilToInt(result * (-pct / 100f)));
                }
            }
            return result;
        }

        private static int ResolvePercent(StatModifier m, Func<string, int> counterValue)
        {
            int pct;
            if (m.Percent.HasValue)
            {
                pct = m.Percent.Value;
            }
            else if (!string.IsNullOrEmpty(m.PercentFrom) &&
                     m.PercentFrom.StartsWith(Vocabulary.SourceCounterPrefix, StringComparison.Ordinal))
            {
                var name = m.PercentFrom.Substring(Vocabulary.SourceCounterPrefix.Length);
                int units = counterValue != null ? counterValue(name) : 0;
                pct = units * (m.PerUnit ?? 0);
            }
            else
            {
                return 0;
            }

            if (m.Max.HasValue && pct > m.Max.Value) pct = m.Max.Value;
            return pct;
        }

        /// <summary>Unity's <c>Mathf.CeilToInt(float)</c> — float in, ceil via double, matching the
        /// game's arithmetic exactly (net472 has no MathF).</summary>
        private static int CeilToInt(float value)
        {
            return (int)Math.Ceiling((double)value);
        }
    }
}
