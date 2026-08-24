using System;
using System.Collections.Generic;

namespace ClassForge.Recipes.Model
{
    /// <summary>
    /// One CONDITIONAL_STAT_MODIFIER — a standing percentage modifier attached to a trait/class via
    /// <c>Passives[]</c> and consulted on every stat read (conditional-stat-modifier spec §2). Unlike a
    /// <see cref="SkillRecipe"/> it has no trigger: it is a declaration, not an action, which is why it
    /// lives in its own pack file (<c>statmodifiers.json</c>, spec §2.2) and its own model.
    /// </summary>
    public sealed class StatModifier
    {
        public string Id;

        /// <summary>Per-modifier kill switch, mirroring <see cref="SkillRecipe.Enabled"/>.</summary>
        public bool Enabled = true;

        /// <summary>Set when validation produced an Error — such a modifier is never applied (fail-safe).</summary>
        public bool DisabledByValidator;

        /// <summary>Target stat key — must be a real <c>eCharacterStats</c> member (EGT §6).</summary>
        public string Stat;

        /// <summary>Fixed percentage delta, -99..500. Mutually exclusive with <see cref="PercentFrom"/>.</summary>
        public int? Percent;

        /// <summary><c>"COUNTER:&lt;name&gt;"</c> scaling source (spec §2, mirrors ROLL_STAT_BONUS.PercentFrom).</summary>
        public string PercentFrom;

        /// <summary>Percent per unit of the <see cref="PercentFrom"/> source.</summary>
        public int? PerUnit;

        /// <summary>Cap on the resolved percentage.</summary>
        public int? Max;

        /// <summary>The composed result is never reduced below this (EOR's <c>Math.Max(1, …)</c>).</summary>
        public int Floor = 1;

        /// <summary>Skip when the incoming value is &lt;= 0 (EOR's <c>result &gt; 0</c> guards). Authored
        /// false only where EOR itself is unguarded (PACK_TACTICS, ARCANE_FOCUS).</summary>
        public bool RequiresPositiveBase = true;

        /// <summary>Read-time conditions — the validator only admits the spec §3.3 subset that resolves
        /// without a stat read (v1: COUNTER, PARTY_HAS_FOLLOWER).</summary>
        public List<RecipeCondition> Conditions = new List<RecipeCondition>();

        public bool IsLive { get { return Enabled && !DisabledByValidator; } }
    }

    /// <summary>The loaded statmodifiers registry — findings + ascending-ordinal-Id iteration order
    /// (spec §3.4: composition order is ascending Id, SPEC-DELTA §5.2 invariant 4).</summary>
    public sealed class StatModifierSet
    {
        private readonly List<StatModifier> _ordered = new List<StatModifier>();
        private readonly List<Finding> _findings = new List<Finding>();

        public IReadOnlyList<StatModifier> Ordered { get { return _ordered; } }
        public IReadOnlyList<Finding> Findings { get { return _findings; } }

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < _findings.Count; i++)
                    if (_findings[i].Severity == FindingSeverity.Error) return true;
                return false;
            }
        }

        public void AddFinding(Finding f) { _findings.Add(f); }

        public void Add(StatModifier modifier)
        {
            _ordered.Add(modifier);
            _ordered.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        }

        public StatModifier Find(string id)
        {
            for (int i = 0; i < _ordered.Count; i++)
                if (string.Equals(_ordered[i].Id, id, StringComparison.Ordinal)) return _ordered[i];
            return null;
        }
    }
}
