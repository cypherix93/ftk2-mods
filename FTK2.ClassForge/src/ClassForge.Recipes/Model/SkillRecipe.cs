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

        // STATE_HASH_CHANCE (state-hash-chance spec §2). Percent reuses the shared field above.
        /// <summary>Decorrelation salt, <c>^[A-Z0-9_]{4,64}$</c>, unique per authored condition.</summary>
        public string Salt;
        /// <summary>Ordered input-token tuple (2–8 entries from <see cref="Vocabulary.StateHashInputTokens"/>).
        /// Authored order is the hash order.</summary>
        public List<string> Inputs;
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

        /// <summary>v1.5 — SUMMON's DYNAMIC config source, mutually exclusive with
        /// <see cref="CharacterConfig"/>. The only supported form is
        /// <c>ITEM_CUSTOM_DATA:&lt;ThingConfigId&gt;:&lt;Key&gt;</c>
        /// (<see cref="Vocabulary.SourceItemCustomDataPrefix"/>): resolve the summoned creature's config id
        /// at EXECUTION time from the OWNER's carried Thing of that config id, key <paramref name="Key"/> of
        /// its <c>Thing.CustomData</c>. Unresolvable ⇒ logged no-op, zero draws, no action emitted.</summary>
        public string CharacterConfigFrom;

        // CAPTURE (v1.5)
        /// <summary>CAPTURE only — the ThingConfig id of the item in the OWNER's inventory whose
        /// <c>Thing.CustomData</c> receives the captured config id. Possession, not equipment: a toolbelt
        /// Thing can never be equipped (it has no <c>eEquipmentSlots</c> slot) yet is still in
        /// <c>CharacterComponent.Things</c>.</summary>
        public string IntoItem;

        /// <summary>CAPTURE only — the <c>Thing.CustomData</c> key written on <see cref="IntoItem"/>.</summary>
        public string IntoKey;

        // ROLL_STAT_BONUS / HEAL_MODIFIER
        public int? Percent;
        public int? Flat;
        public int? MinDelta;
        public HealScope Scope = HealScope.RECEIVED;

        // COUNTER_ADD / COUNTER_SET
        public string Name;
        public int Delta;
        public int Value;

        /// <summary>COUNTER_ADD/COUNTER_SET only. When true, the counter's value is seeded from — and
        /// written back to — the owner's persistent per-character store (<c>CharacterComponent.CustomData</c>,
        /// via <c>CoreHelper.Get/SetCustomData</c>) instead of resetting to 0 with every fresh per-battle
        /// <c>CombatRuntime</c> (SPEC-DELTA-v1.1 §6). Requires <see cref="SkillRecipe.Scope"/> OWNED — a
        /// COMBAT-scoped recipe has no owner entity to persist against.</summary>
        public bool Persistent;

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

        // --- Encounter Modifiers effects (v1.2, spec §5) ---

        // SELECTION_SET
        /// <summary>Selection-slot key written into <c>CombatRuntime.Selections</c>. Reuses the generic
        /// <see cref="Name"/> field above (same slot used by COUNTER_ADD/COUNTER_SET) — one authored key per
        /// effect kind that needs one.</summary>
        public List<WeightedValue> OneOfWeighted;

        // ADD_STATUS / REMOVE_STATUS extension
        /// <summary>Mutually exclusive with <see cref="Status"/>/<see cref="StatusOneOf"/>. Resolves the
        /// status id from <c>CombatRuntime.Selections[StatusFromSelection]</c>; empty selection ⇒ no-op.
        /// <para>Table-driven variant (M-EM3, mirrors <see cref="PercentFromSelectionTable"/>): when
        /// <see cref="StatusFromSelectionTable"/> is also populated, the stored selection value is looked
        /// up in that table instead of being used AS the status id directly — this is what lets
        /// <c>CombatRuntime.Selections</c> canonically hold a MODIFIER id (spec §11's "activeModifierId",
        /// and what <c>ModifierReconstruction</c> already stores) while still resolving to the right
        /// STATUS id here. No table authored ⇒ the original raw-echo behavior, unchanged (every
        /// hand-authored/test use of <c>StatusFromSelection</c> pre-M-EM3 keeps working exactly as
        /// before).</para></summary>
        public string StatusFromSelection;

        /// <summary>The <c>modifier id → status id</c> rows for <see cref="StatusFromSelection"/> (M-EM3).
        /// Populated only by the engine's generator; never hand-authored.</summary>
        public List<SelectionStatusEntry> StatusFromSelectionTable;

        // STAT_CHANGE extension — Encounter Modifiers spec §6.1 "PercentFromSelection" sugar (M-EM3).
        /// <summary>Selection-slot key (same <c>CombatRuntime.Selections</c> namespace as
        /// <see cref="StatusFromSelection"/>) whose CURRENTLY STORED value picks a row out of
        /// <see cref="PercentFromSelectionTable"/> to feed <c>TARGET_MXHP_PCT</c>'s (GATE C)
        /// <c>Percent</c> input, per-selection instead of a single authored <see cref="Percent"/>. Only
        /// meaningful alongside <c>FlatValueFrom: "TARGET_MXHP_PCT"</c>; mutually exclusive in practice
        /// with the plain <see cref="Percent"/> field (an authored <see cref="Percent"/>, if present,
        /// still wins — see <c>ValueSources.RawTargetMxhpPct</c>). No selection stored, or the selected
        /// value has no row (0/absent), resolves to 0 — and per the generator's "table-driven emission"
        /// design, a 0 result omits the WHOLE STAT_CHANGE effect (no zero-value action), never a no-op
        /// action with <c>FlatValue=0</c>.</summary>
        public string PercentFromSelection;

        /// <summary>The per-selection-value percent rows for <see cref="PercentFromSelection"/> — one
        /// entry per modifier that actually carries a non-zero <c>MaxHpPercent</c> (M-EM1's
        /// <c>ModifierEntry.MaxHpPercent</c>); modifiers without one are simply absent from this table,
        /// which is exactly the "0/absent ⇒ omitted" rule. Populated only by the engine's generator
        /// (<c>ClassForge.Recipes.Generation.ModifierRecipeGenerator</c>) — never hand-authored.</summary>
        public List<SelectionPercentEntry> PercentFromSelectionTable;

        // EVENT_BANNER — [LOCAL] presentation only, never gates gameplay (spec §5, §9)
        public string LocKey;
        public string FallbackText;
        public int? DurationMs;
        /// <summary>Optional selection-slot key whose stored value the Plugin substitutes into the banner
        /// text at render time (engine only resolves and carries the raw stored value — formatting/loc is
        /// a Plugin concern).</summary>
        public string TextFromSelection;
    }

    /// <summary>One <c>{Value, Weight}</c> row of <c>SELECTION_SET.OneOfWeighted</c> — Encounter Modifiers
    /// spec §5. Authored array order is load-bearing: it is the weighted-pick walk order (spec §6.3,
    /// EOR L22814-28 verbatim).</summary>
    public sealed class WeightedValue
    {
        public string Value;
        public int Weight;
    }

    /// <summary>One <c>{Value, Percent}</c> row of <c>STAT_CHANGE.PercentFromSelectionTable</c> —
    /// Encounter Modifiers spec §6.1 "PercentFromSelection" sugar (M-EM3 generation design). <c>Value</c>
    /// is a selection value (a modifier id, e.g. <c>"VETERAN"</c>); <c>Percent</c> is that modifier's
    /// <c>MaxHpPercent</c>.</summary>
    public sealed class SelectionPercentEntry
    {
        public string Value;
        public int Percent;
    }

    /// <summary>One <c>{Value, StatusId}</c> row of <c>ADD_STATUS.StatusFromSelectionTable</c> — M-EM3
    /// generation design (mirrors <see cref="SelectionPercentEntry"/>). <c>Value</c> is a selection value
    /// (a modifier id, e.g. <c>"VETERAN"</c>); <c>StatusId</c> is that modifier's <c>Status</c>.</summary>
    public sealed class SelectionStatusEntry
    {
        public string Value;
        public string StatusId;
    }

    /// <summary>One row of <see cref="ProcChanceFormula.Base"/>/<see cref="ProcChanceFormula.Adjustments"/> —
    /// Encounter Modifiers spec §5.</summary>
    public sealed class ProcChanceFormulaRow
    {
        public List<RecipeCondition> Conditions = new List<RecipeCondition>();
        public int Value;
    }

    /// <summary>
    /// <c>ProcChanceFormula</c> — Encounter Modifiers spec §5. Recipe-level, mutually exclusive with
    /// <c>ProcChance</c>. A pure function of replicated state (no RNG): the first <see cref="Base"/> row
    /// whose Conditions all pass wins (last row with no Conditions = default); every passing
    /// <see cref="Adjustments"/> row adds its <c>Value</c>; the total is clamped to <c>[Min, Max]</c> when
    /// present. A result &lt;= 0 takes zero draws (symmetric skip, spec §5).
    /// </summary>
    public sealed class ProcChanceFormula
    {
        public List<ProcChanceFormulaRow> Base = new List<ProcChanceFormulaRow>();
        public List<ProcChanceFormulaRow> Adjustments = new List<ProcChanceFormulaRow>();
        public int? Min;
        public int? Max;
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

        /// <summary>Recipe-level scope — Encounter Modifiers spec §4.2. <c>OWNED</c> (default) is exactly
        /// today's v1.1 semantics; <c>COMBAT</c> requires SchemaVersion 1.2 and is validated per §4.2's
        /// rejection rules (no SELF-defaulting conditions, no SELF/CASTER/ALLY_*/ENEMY_ALL targets, no
        /// AiProcChance).</summary>
        public RecipeScope Scope = RecipeScope.OWNED;
        public string RawScope;

        public List<RecipeCondition> Conditions = new List<RecipeCondition>();
        public List<RecipeEffect> Effects = new List<RecipeEffect>();

        /// <summary>0–100, = the <c>CHANCE_PCT</c> primitive. 100 takes ZERO draws (§5.2 invariant 3).</summary>
        public int ProcChance = 100;
        public int AiProcChance = 100;

        /// <summary>True when the JSON explicitly authored <c>ProcChance</c> (vs. the silent 100 default) —
        /// needed because <see cref="ProcChanceFormula"/> is validator-rejected as mutually exclusive only
        /// when the author actually wrote both (Encounter Modifiers spec §5).</summary>
        public bool ProcChanceAuthored;

        /// <summary>True when the JSON explicitly authored <c>AiProcChance</c> — needed because it is
        /// validator-rejected on a <c>COMBAT</c>-scoped recipe (no owner to select Ai vs. player chance,
        /// Encounter Modifiers spec §4.2) only when actually authored, not merely defaulted.</summary>
        public bool AiProcChanceAuthored;

        /// <summary>Encounter Modifiers spec §5. Mutually exclusive with <see cref="ProcChance"/>. Null when
        /// not authored (the recipe uses the plain <c>ProcChance</c>/<c>AiProcChance</c> primitive as today).</summary>
        public ProcChanceFormula ProcChanceFormula;

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
