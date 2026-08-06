using System;
using System.Collections.Generic;

namespace ClassForge.Recipes.Model
{
    /// <summary>Diagnostic severity. <see cref="Error"/> always disables the offending recipe.</summary>
    public enum FindingSeverity
    {
        Warning,
        Error
    }

    /// <summary>
    /// A parse/validate diagnostic. The engine never throws on bad data: an unknown trigger/condition/
    /// effect/target token produces a Finding and the recipe is disabled (fail-safe posture, charter rule 1).
    /// </summary>
    public sealed class Finding
    {
        public FindingSeverity Severity;
        public string RecipeId;
        public string Path;
        public string Code;
        public string Message;

        public Finding(FindingSeverity severity, string recipeId, string path, string code, string message)
        {
            Severity = severity;
            RecipeId = recipeId ?? "";
            Path = path ?? "";
            Code = code ?? "";
            Message = message ?? "";
        }

        public override string ToString()
        {
            return Severity.ToString().ToUpperInvariant() + " [" + Code + "] " + RecipeId +
                   (Path.Length > 0 ? "." + Path : "") + ": " + Message;
        }
    }

    /// <summary>A condition entry. Conditions are ANDed; an empty array is always true (SPEC-DELTA-v1.1 §3).</summary>
    public sealed class RecipeCondition
    {
        public ConditionKind Type;
        public string RawType;

        /// <summary>Universal <c>Negate</c> (§3). Subsumes v1's <c>TARGET_BASE_TYPE_NOT</c>.</summary>
        public bool Negate;

        /// <summary>Universal <c>Of</c> selector (§3). Default <see cref="OfSelector.SELF"/>.</summary>
        public OfSelector Of = OfSelector.SELF;

        public string Value;
        public bool? ValueBool;
        public int? ValueInt;

        public Comparator Comparator = Comparator.GTE;
        public bool HasComparator;

        // HP_THRESHOLD
        public int? Percent;
        public int? Flat;

        // STATUS_COUNT
        public StatusCategory Category = StatusCategory.ANY;
        public List<string> Types;

        // COUNTER
        public string Name;

        // ROLL_TIER
        public RollTier Tier;
    }

    /// <summary><c>ALLY_BY_RANK.Rank</c> sub-schema — SPEC-DELTA-v1.1 §4.3.</summary>
    public sealed class RankSpec
    {
        /// <summary>An <c>eCharacterStats</c> member, or the synthetic <c>"HP_PCT"</c>.</summary>
        public string Stat = "HP_PCT";
        public RankOrder Order = RankOrder.LOWEST;
        public List<RecipeCondition> Where = new List<RecipeCondition>();
        public bool ExcludeSelf = true;
    }

    /// <summary>An effect entry. Per SPEC-DELTA-v1.1 §4 every effect may carry its own <c>Conditions</c>.</summary>
    public sealed class RecipeEffect
    {
        public EffectKind Type;
        public string RawType;

        /// <summary>Per-effect conditions (§4, "the single highest-leverage generalization in v1.1").</summary>
        public List<RecipeCondition> Conditions = new List<RecipeCondition>();

        public TargetKind Target = TargetKind.SELF;
        public RankSpec Rank;

        // ADD_STATUS / REMOVE_STATUS
        public string Status;
        public string FallbackStatus;
        public List<string> StatusOneOf;
        public int? Duration;

        // STAT_CHANGE
        public string Stat;
        public string StatChangeType;
        public int? FlatValue;
        public int? FlatPercent;
        public bool Blockable;
        public bool IsSilent;

        // SUMMON
        public SummonType SummonType = SummonType.SPECIFIC;
        public string CharacterConfig;
        public int Count = 1;

        // ROLL_STAT_BONUS / HEAL_MODIFIER
        public int? Percent;
        public int? Flat;
        public int? MinDelta;
        public HealScope Scope = HealScope.RECEIVED;

        // COUNTER_ADD / COUNTER_SET
        public string Name;
        public int Delta;
        public int Value;

        // Dynamic value sources (§4.1)
        public string FlatValueFrom;
        public string PercentFrom;
        public int? PerUnit;
        public int? Min;
        public int? Max;

        // --- Loot-grant effects (ON_COMBAT_LOOT only, SchemaVersion 1.2) — verb spec §6.2 ---

        // GOLD_GRANT
        public int? MinGold;
        public int? MaxGold;

        // ITEM_TAG_GRANT
        public string Tag;
        /// <summary>Optional; null means "any rarity" (matches EOR's SCHOLARS_HABIT). Not enum-validated
        /// by this pure-C# core — <c>eItemRarities</c> membership is a game-ref concern deferred to the
        /// M-LG2 Plugin unit (no decompile evidence for its members is available inside this assembly).</summary>
        public string Rarity;
        public int Stack = 1;

        // LOOT_SCALE (reuses Percent above); ConfigName is the currency Thing to scale
        public string ConfigName;

        // AFFIX_ROLL — reserved, always validator-rejected in v1 (M-LG4)
        public int? ChancePct;
        public string Table;
    }

    /// <summary><c>Budget</c> block — SPEC-DELTA-v1.1 §5.1.</summary>
    public sealed class RecipeBudget
    {
        public BudgetScope Scope = BudgetScope.NONE;
        public ConsumeOn ConsumeOn = ConsumeOn.PROC;

        /// <summary>Optional shared budget namespace so a recipe pair shares one budget (§5.1 "Recipe pairs").
        /// Defaults to the recipe id.</summary>
        public string Key;
    }

    /// <summary>
    /// One authored skill recipe — the v1.1 shape of SPEC §4.6's <c>skillrecipes.json</c> entry
    /// (SPEC-DELTA-v1.1 §5.1: 8 v1 fields + 7 new = 15).
    /// </summary>
    public sealed class SkillRecipe
    {
        public string Id;
        public string SchemaVersion = Vocabulary.SchemaVersionLegacy;
        public string DisplayName;

        /// <summary>Per-recipe kill switch (§5.1). Also the "disabled-by-default" carrier for charter rule 3.</summary>
        public bool Enabled = true;

        /// <summary>Set by the validator when a Finding of severity Error was produced. Fail-safe: such a
        /// recipe is never evaluated.</summary>
        public bool DisabledByValidator;

        public TriggerKind Trigger;
        public string RawTrigger;

        public List<RecipeCondition> Conditions = new List<RecipeCondition>();
        public List<RecipeEffect> Effects = new List<RecipeEffect>();

        /// <summary>0–100, = the <c>CHANCE_PCT</c> primitive. 100 takes ZERO draws (§5.2 invariant 3).</summary>
        public int ProcChance = 100;
        public int AiProcChance = 100;

        public RecipeBudget Budget = new RecipeBudget();

        /// <summary>Rounds; 0 = none. Tracked per battle only (§6).</summary>
        public int Cooldown;

        /// <summary>
        /// <c>ON_COMBAT_LOOT</c> only (loot-grant verb spec §6.1): after the proc roll passes, exactly
        /// one entry of <see cref="Effects"/> is selected uniformly (one grant-stream draw), mirroring
        /// EOR's nested 50/50 shape. Requires <see cref="Effects"/>.Count &gt;= 2. The validator rejects
        /// this field set true on any other trigger.
        /// </summary>
        public bool PickOneEffect;

        /// <summary>Deterministic evaluation order; ties broken by ordinal recipe id (§5.1, §5.2 invariant 4).</summary>
        public int Priority;

        public string VerboseLogTag;

        /// <summary>True when the recipe may actually be evaluated.</summary>
        public bool IsLive { get { return Enabled && !DisabledByValidator; } }

        /// <summary>The budget namespace: <c>Budget.Key</c> when authored, else the recipe id (§5.1).</summary>
        public string BudgetKey
        {
            get { return string.IsNullOrEmpty(Budget.Key) ? Id : Budget.Key; }
        }
    }

    /// <summary>
    /// The loaded recipe book. <see cref="Ordered"/> is the ONLY iteration order the dispatcher uses:
    /// ascending <c>Priority</c>, then ascending ordinal recipe id (SPEC-DELTA-v1.1 §5.2 invariant 4 —
    /// "Never Dictionary/HashSet enumeration order, never filesystem order").
    /// </summary>
    public sealed class RecipeSet
    {
        private readonly List<SkillRecipe> _ordered = new List<SkillRecipe>();
        private readonly List<Finding> _findings = new List<Finding>();

        public IReadOnlyList<SkillRecipe> Ordered { get { return _ordered; } }
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

        public void Add(SkillRecipe recipe)
        {
            _ordered.Add(recipe);
            _ordered.Sort(CompareRecipes);
        }

        public SkillRecipe Find(string id)
        {
            for (int i = 0; i < _ordered.Count; i++)
                if (string.Equals(_ordered[i].Id, id, StringComparison.Ordinal)) return _ordered[i];
            return null;
        }

        private static int CompareRecipes(SkillRecipe a, SkillRecipe b)
        {
            int c = a.Priority.CompareTo(b.Priority);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Id, b.Id);
        }
    }
}
