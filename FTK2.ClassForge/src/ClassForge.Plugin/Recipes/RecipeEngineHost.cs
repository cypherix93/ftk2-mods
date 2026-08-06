using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClassForge.Core;
using ClassForge.Recipes.Generation;
using ClassForge.Recipes.Loot;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Parsing;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Owns the recipe book and the single live <see cref="RecipeDispatcher"/>, and is the one place every
    /// combat hook goes through to reach the engine.
    ///
    /// <para><b>Per-battle state model (SPEC-DELTA-v1.1 §6).</b> A <b>single-slot</b> cache holds exactly one
    /// <c>(CombatKey, RecipeDispatcher)</c> pair, where
    /// <c>CombatKey := (ReferenceEquals-identity of Env.GameRun.CombatState, CombatState.Random.Seed)</c>.
    /// Any hook observing a different key <b>drops the whole dispatcher and allocates a fresh one before doing
    /// anything else</b>. That makes state leakage structurally impossible — there is no code path on which
    /// stale per-battle state can be read, even if an end-of-combat hook is missed. (This is the direct fix
    /// for EOR's process-global <c>SteadyAimUsedThisCombat</c> HashSet, whose only cleanup was a per-entity
    /// <c>.Remove()</c> — TM §6 hazard 3.)</para>
    ///
    /// <para><b>Null stream ⇒ no fire, ever (§5.2 invariant 2).</b> When there is no active combat, or
    /// <c>CombatState.Random</c> is null, <see cref="TryBegin"/> returns false and the hook does nothing. A
    /// fresh/ad-hoc seeded <c>GameRandom</c> is <b>never</b> constructed as a fallback — that is precisely
    /// EOR's non-lockstep anti-pattern (<c>TryCreateSharedSeededGameplayRandom</c>).</para>
    ///
    /// <para><b>Re-entrancy.</b> While a plan is executing, <see cref="TryBegin"/> refuses to start a nested
    /// dispatch. Executing an effect calls native verbs which themselves fire ClassForge's hooks
    /// (<c>ApplyStatus</c> → <c>ON_STATUS_APPLIED</c>, <c>ApplyStatChange</c> → <c>ON_DAMAGE_*</c>), so
    /// without this a WARDBOUND-style "observe a status, remove it" recipe would recurse. Suppressing the
    /// nested dispatch is deterministic — it depends only on the plan being executed, which is itself a pure
    /// function of parity-identical data — whereas allowing unbounded chaining would make draw counts
    /// depend on recursion depth.</para>
    /// </summary>
    internal static class RecipeEngineHost
    {
        /// <summary>The merged, validated recipe book. Replaced wholesale on every pack merge.</summary>
        internal static RecipeSet Book = new RecipeSet();

        private static readonly RecipeLogAdapter LogAdapter = new RecipeLogAdapter();

        // ---- single-slot per-combat cache (§6) ----
        private static CombatState _cachedState;
        private static int _cachedSeed;
        private static RecipeDispatcher _cachedDispatcher;

        private static int _executionDepth;
        private static bool _loggedNoCombat;

        /// <summary>True while a plan is being executed against native verbs.</summary>
        internal static bool IsExecuting { get { return _executionDepth > 0; } }

        internal static IDisposable EnterExecution() { return new ExecutionScope(); }

        private sealed class ExecutionScope : IDisposable
        {
            internal ExecutionScope() { _executionDepth++; }
            public void Dispose() { if (_executionDepth > 0) _executionDepth--; }
        }

        /// <summary>
        /// Resolves the live combat and returns a fresh context plus the dispatcher for the current
        /// <c>CombatKey</c>. False means "do nothing" — no combat, no RNG, engine disabled, parity blocked,
        /// an empty book, or a nested dispatch.
        /// </summary>
        internal static bool TryBegin(out CombatContextAdapter ctx, out RecipeDispatcher dispatcher)
        {
            ctx = null;
            dispatcher = null;

            if (!ClassForgePlugin.FeaturesActive) return false;
            if (ClassForgePlugin.EnableRecipeEngine == null || !ClassForgePlugin.EnableRecipeEngine.Value) return false;
            if (_executionDepth > 0) return false;
            if (Book == null || Book.Ordered.Count == 0) return false;

            try
            {
                var env = RouterHelper.Env;
                var run = env != null ? env.GameRun : null;
                var state = run != null ? run.CombatState : null;

                // §5.2 invariant 2 — no active combat / no shared stream ⇒ the recipe does not fire.
                if (state == null || state.Random == null)
                {
                    if (!_loggedNoCombat)
                    {
                        _loggedNoCombat = true;
                        ClassForgePlugin.Log.LogInfo(
                            "[ClassForge] No active CombatState.Random — recipe evaluation skipped " +
                            "(SPEC-DELTA-v1.1 §5.2 invariant 2: null stream means the recipe never fires; " +
                            "an ad-hoc seeded GameRandom is never substituted). Logged once.");
                    }
                    return false;
                }

                bool freshlyAllocated = SyncCombat(state);

                ctx = new CombatContextAdapter(env, state);
                dispatcher = _cachedDispatcher;

                // Encounter Modifiers spec §4.5 — derived-state reconstruction. Runs exactly once, right
                // after a fresh per-battle runtime is allocated for THIS combat (new CombatKey), using the
                // just-built ctx so the scan sees the combat's current entities/statuses.
                if (freshlyAllocated && dispatcher != null)
                    RunModifierReconstruction(ctx, dispatcher);

                return dispatcher != null;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Recipe engine failed to resolve combat state (fail-safe, no recipes fire): " + ex);
                ctx = null;
                dispatcher = null;
                return false;
            }
        }

        /// <summary>
        /// The §6 single-slot drop-and-reallocate. Compares by <b>reference identity</b> of the CombatState
        /// (not a hash — a hash could collide) plus the <c>GameRandom.Seed</c>, which is a
        /// <c>public readonly int</c> identical on every peer and different for every combat. Returns true
        /// exactly when a fresh dispatcher/runtime was just allocated for a CombatKey the host had not seen
        /// before — the §4.5 reconstruction trigger.
        /// </summary>
        private static bool SyncCombat(CombatState state)
        {
            int seed = state.Random.Seed;
            if (_cachedDispatcher != null && ReferenceEquals(_cachedState, state) && _cachedSeed == seed)
                return false;

            _cachedState = state;
            _cachedSeed = seed;
            int? debugChance = null;
            if (ClassForgePlugin.DebugEncounterModifierChance != null && ClassForgePlugin.DebugEncounterModifierChance.Value >= 0)
                debugChance = ClassForgePlugin.DebugEncounterModifierChance.Value;
            _cachedDispatcher = new RecipeDispatcher(Book, new GameRandomSource(state.Random), LogAdapter, debugChance);

            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
            {
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] New CombatKey (seed=" + seed.ToString(CultureInfo.InvariantCulture) +
                    ") — per-battle recipe runtime reallocated from scratch (SPEC-DELTA-v1.1 §6).");
            }
            return true;
        }

        // =====================================================================================
        // §4.5 derived-state reconstruction (Encounter Modifiers spec)
        // =====================================================================================

        /// <summary>
        /// Content-agnostic wiring for <see cref="ModifierReconstruction.Reconstruct"/>: for every pack that
        /// shipped a <c>modifiers.json</c> (<see cref="MergePlan.ModifierTables"/>, M-EM1), auto-discovers
        /// the selection recipe's <c>SELECTION_SET.Name</c> and the matching "apply" recipe (any live recipe
        /// whose <c>ADD_STATUS.StatusFromSelection</c> reads that same Name) purely by scanning the loaded
        /// <see cref="Book"/> — no hardcoded pack recipe ids. The registry (modifier id → status id) comes
        /// from the pack's own <c>ModifierTable</c>. A table with no matching recipes in the current book
        /// (e.g. M-EM1/M-EM2 packs shipped before the generated recipes exist, M-EM3) is silently skipped.
        /// </summary>
        private static void RunModifierReconstruction(CombatContextAdapter ctx, RecipeDispatcher dispatcher)
        {
            try
            {
                var plan = ClassForgePlugin.CurrentMergePlan;
                var tables = plan != null ? plan.ModifierTables : null;
                if (tables == null || tables.Count == 0) return;

                var runtime = dispatcher.State.Sync(ctx);
                var book = Book.Ordered;

                for (int ti = 0; ti < tables.Count; ti++)
                {
                    var table = tables[ti];
                    if (table == null || string.IsNullOrEmpty(table.SelectionRecipe)) continue;

                    SkillRecipe selRecipe = FindRecipe(book, table.SelectionRecipe);
                    if (selRecipe == null || !selRecipe.IsLive) continue;

                    string selectionName = DiscoverSelectionName(selRecipe);
                    if (string.IsNullOrEmpty(selectionName)) continue;

                    SkillRecipe applyRecipe = null;
                    for (int i = 0; i < book.Count && applyRecipe == null; i++)
                    {
                        var r = book[i];
                        if (!r.IsLive) continue;
                        for (int e = 0; e < r.Effects.Count; e++)
                        {
                            if (r.Effects[e].Type == EffectKind.ADD_STATUS &&
                                string.Equals(r.Effects[e].StatusFromSelection, selectionName, StringComparison.Ordinal))
                            { applyRecipe = r; break; }
                        }
                    }
                    if (applyRecipe == null) continue;

                    var registry = new List<ModifierReconstruction.ModifierRegistryEntry>();
                    for (int i = 0; i < table.Modifiers.Count; i++)
                    {
                        var m = table.Modifiers[i];
                        if (m == null || string.IsNullOrEmpty(m.Status) || string.IsNullOrEmpty(m.Id)) continue;
                        registry.Add(new ModifierReconstruction.ModifierRegistryEntry { StatusId = m.Status, ModifierId = m.Id });
                    }

                    ModifierReconstruction.Reconstruct(ctx, runtime, registry, selectionName,
                        selRecipe.Budget.Scope, selRecipe.BudgetKey,
                        applyRecipe.Budget.Scope, applyRecipe.BudgetKey);
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Modifier reconstruction failed (fail-safe, no reconstruction applied): " + ex);
            }
        }

        /// <summary>Drops the per-battle runtime. Belt-and-braces only: <see cref="SyncCombat"/> already
        /// reallocates on a new CombatKey, so a missed end-of-combat hook cannot leak state. Also clears
        /// <c>CombatHookPatches._healOrigin</c> (MP review M4) — that static is exception-safe on its own
        /// (Harmony finalizer), but this is the combat-end reset boundary and it must not survive it either.</summary>
        internal static void ResetCombat()
        {
            _cachedState = null;
            _cachedSeed = 0;
            _cachedDispatcher = null;
            CombatHookPatches.ClearHealOrigin();
        }

        // =====================================================================================
        // Recipe book loading
        // =====================================================================================

        /// <summary>
        /// Rebuilds the recipe book from every enabled pack's <c>skillrecipes.json</c>, in resolved pack load
        /// order (later pack wins on a duplicate recipe id, matching the content merge's last-pack-wins rule).
        /// Fully rebuild-from-scratch, so a hot-reload can never leave a stale recipe behind.
        /// </summary>
        internal static void LoadBook(PackLoadResult result)
        {
            var book = new RecipeSet();
            int files = 0, recipes = 0, errors = 0, warnings = 0;

            try
            {
                // Last-pack-wins: walk packs in load order, overwriting earlier definitions of the same id.
                var byId = new Dictionary<string, SkillRecipe>(StringComparer.Ordinal);
                var order = new List<string>();

                for (int i = 0; i < result.EnabledOrderedPacks.Count; i++)
                {
                    var pack = result.EnabledOrderedPacks[i];
                    if (pack == null || string.IsNullOrEmpty(pack.RootDir)) continue;

                    string path = Path.Combine(pack.RootDir, "skillrecipes.json");
                    if (!File.Exists(path)) continue;

                    string json;
                    try { json = File.ReadAllText(path); }
                    catch (Exception ex)
                    {
                        errors++;
                        ClassForgePlugin.Log.LogError(
                            "[ClassForge] Could not read '" + path + "' (pack '" + pack.Id + "') — its recipes are skipped: " + ex.Message);
                        continue;
                    }

                    files++;
                    var parsed = RecipeParser.Parse(json);

                    for (int f = 0; f < parsed.Findings.Count; f++)
                    {
                        var finding = parsed.Findings[f];
                        if (finding.Severity == ClassForge.Recipes.Model.FindingSeverity.Error)
                        {
                            errors++;
                            ClassForgePlugin.Log.LogError("[ClassForge] skillrecipes.json (pack '" + pack.Id + "'): " + finding);
                        }
                        else
                        {
                            warnings++;
                            ClassForgePlugin.Log.LogWarning("[ClassForge] skillrecipes.json (pack '" + pack.Id + "'): " + finding);
                        }
                    }

                    var ordered = parsed.Ordered;
                    for (int r = 0; r < ordered.Count; r++)
                    {
                        var recipe = ordered[r];
                        if (recipe == null || string.IsNullOrEmpty(recipe.Id)) continue;
                        if (byId.ContainsKey(recipe.Id))
                        {
                            ClassForgePlugin.Log.LogWarning(
                                "[ClassForge] Recipe '" + recipe.Id + "' redefined by pack '" + pack.Id +
                                "' — later pack wins (same rule as the content merge).");
                        }
                        else
                        {
                            order.Add(recipe.Id);
                        }
                        byId[recipe.Id] = recipe;
                    }
                }

                // M-EM3 — engine-generated encounter-modifier recipes (Encounter Modifiers spec §6.1).
                // Single source = the pack's modifiers.json; the two combat-scoped recipes are synthesized
                // here, never hand-authored (upserted into the SAME byId/order staging the file pass just
                // built, so a same-id hand-authored recipe would collide through the identical warned path
                // a two-pack skillrecipes.json collision already uses).
                GenerateModifierRecipes(result.MergePlan, byId, order, ref errors, ref warnings);

                // RecipeSet.Add re-sorts to (Priority, ordinal id) itself; `order` only keeps the dedupe
                // deterministic, it is not the evaluation order (§5.2 invariant 4 owns that).
                for (int i = 0; i < order.Count; i++)
                {
                    var recipe = byId[order[i]];
                    book.Add(recipe);
                    if (recipe.IsLive) recipes++;
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Recipe book load failed (fail-safe — engine runs with whatever parsed): " + ex);
            }

            Book = book;
            // A changed book means every cached per-battle budget/cooldown is keyed against recipes that may
            // no longer exist. Drop the runtime rather than reason about it.
            ResetCombat();

            if (files > 0 || book.Ordered.Count > 0)
            {
                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] Recipe book: " + book.Ordered.Count.ToString(CultureInfo.InvariantCulture) +
                    " recipe(s) from " + files.ToString(CultureInfo.InvariantCulture) + " skillrecipes.json file(s); " +
                    recipes.ToString(CultureInfo.InvariantCulture) + " live, " +
                    errors.ToString(CultureInfo.InvariantCulture) + " error(s), " +
                    warnings.ToString(CultureInfo.InvariantCulture) + " warning(s). " +
                    "Recipes with validation errors are disabled and never evaluated (fail-safe).");
            }
        }

        // =====================================================================================
        // M-EM3 — engine-owned recipe generation (Encounter Modifiers spec §6.1)
        // =====================================================================================

        /// <summary>
        /// For every pack that shipped a <c>modifiers.json</c> (<see cref="MergePlan.ModifierTables"/>,
        /// M-EM1), synthesizes the SELECT + APPLY recipe pair via
        /// <see cref="ModifierRecipeGenerator.Generate"/> and upserts both into the SAME
        /// <paramref name="byId"/>/<paramref name="order"/> staging <see cref="LoadBook"/> just populated
        /// from every pack's <c>skillrecipes.json</c> — so a hand-authored recipe sharing a generated
        /// recipe's id collides through the identical warned last-writer-wins path a two-pack
        /// skillrecipes.json id collision already uses (spec §6.1: "hand-authored copies must not exist";
        /// this makes a violation loud rather than silently ignored).
        /// </summary>
        private static void GenerateModifierRecipes(
            MergePlan plan, Dictionary<string, SkillRecipe> byId, List<string> order, ref int errors, ref int warnings)
        {
            var tables = plan != null ? plan.ModifierTables : null;
            if (tables == null) return;

            for (int ti = 0; ti < tables.Count; ti++)
            {
                var table = tables[ti];
                if (table == null) continue;

                GeneratedModifierRecipes generated;
                try { generated = ModifierRecipeGenerator.Generate(ToGeneratorInput(table)); }
                catch (Exception ex)
                {
                    errors++;
                    ClassForgePlugin.Log.LogError(
                        "[ClassForge] Encounter-modifier recipe generation failed for pack '" + table.PackId +
                        "' (fail-safe — this pack's modifiers.json ships inert this load): " + ex);
                    continue;
                }
                // Generate() itself returns null for a malformed/empty table (no Selection.Recipe id, or
                // zero Modifiers) -- ships inert rather than emitting broken recipes (mirrors M-EM1 posture).
                if (generated == null) continue;

                UpsertGenerated(generated.Select, table.PackId, byId, order, ref errors, ref warnings);
                UpsertGenerated(generated.Apply, table.PackId, byId, order, ref errors, ref warnings);
            }
        }

        private static ModifierTableInput ToGeneratorInput(ModifierTable table)
        {
            var input = new ModifierTableInput { SelectionRecipeId = table.SelectionRecipe };
            if (table.Modifiers != null)
            {
                for (int i = 0; i < table.Modifiers.Count; i++)
                {
                    var m = table.Modifiers[i];
                    if (m == null) continue;
                    input.Modifiers.Add(new ModifierRow
                    {
                        Id = m.Id,
                        Weight = m.Weight,
                        Status = m.Status,
                        MaxHpPercent = m.MaxHpPercent
                    });
                }
            }
            return input;
        }

        /// <summary>Validates ONE generated recipe in isolation (a throwaway <see cref="RecipeSet"/> is the
        /// validator's only entry point) so a malformed table (e.g. a dangling/duplicate id upstream)
        /// disables cleanly rather than corrupting the rest of the book, then upserts it into the shared
        /// staging with the same collision-warning posture <see cref="LoadBook"/> uses for hand-authored
        /// duplicates.</summary>
        private static void UpsertGenerated(SkillRecipe recipe, string packId,
            Dictionary<string, SkillRecipe> byId, List<string> order, ref int errors, ref int warnings)
        {
            if (recipe == null || string.IsNullOrEmpty(recipe.Id)) return;

            var temp = new RecipeSet();
            temp.Add(recipe);
            RecipeValidator.Validate(temp);
            for (int f = 0; f < temp.Findings.Count; f++)
            {
                var finding = temp.Findings[f];
                if (finding.Severity == ClassForge.Recipes.Model.FindingSeverity.Error)
                {
                    errors++;
                    ClassForgePlugin.Log.LogError("[ClassForge] generated recipe (pack '" + packId + "'): " + finding);
                }
                else
                {
                    warnings++;
                    ClassForgePlugin.Log.LogWarning("[ClassForge] generated recipe (pack '" + packId + "'): " + finding);
                }
            }

            if (byId.ContainsKey(recipe.Id))
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] Generated encounter-modifier recipe '" + recipe.Id + "' (pack '" + packId +
                    "') collides with a same-id recipe already in the book — the generated recipe wins " +
                    "(Encounter Modifiers spec §6.1: hand-authored copies of these two recipes must not exist).");
            }
            else
            {
                order.Add(recipe.Id);
            }
            byId[recipe.Id] = recipe;
        }

        // =====================================================================================
        // M-EM4 — reward-half interface (Encounter Modifiers spec §11)
        // =====================================================================================

        /// <summary>The read-only <c>(activeModifierId, Rewards{...})</c> interface spec §11 promises the
        /// loot-grant verb engine, resolved for the CURRENT <c>CombatKey</c>'s runtime.</summary>
        internal sealed class ActiveModifierRewards
        {
            internal string ModifierId;
            internal int XpBonusPercent;
            internal int GoldBonusPercent;
            internal int ExtraLootChancePercent;
        }

        /// <summary>
        /// Spec §11's interface: "the modifier engine exposes read-only (activeModifierId,
        /// Rewards{XpBonusPercent, GoldBonusPercent, ExtraLootChancePercent}) for the current CombatKey".
        /// Implemented as a small accessor over the SAME two things §4.5 reconstruction already reads —
        /// <c>CombatRuntime.Selections</c> (via <paramref name="runtime"/>) and the pack's own
        /// <c>ModifierTable</c> registry (<c>ClassForgePlugin.CurrentMergePlan.ModifierTables</c>) — reusing
        /// the identical SELECT-recipe → SELECTION_SET-Name auto-discovery <see cref="RunModifierReconstruction"/>
        /// uses, so there is exactly one place that knows how to find a table's selection-slot name.
        /// <para>Returns null when no table has an active (non-empty) selection this combat, or when the
        /// active modifier's <c>Rewards</c> block is entirely absent/zero — the loot-grant postfix then
        /// emits zero reward ops, which is the correct "no encounter modifier this combat" behavior.</para>
        /// </summary>
        internal static ActiveModifierRewards ResolveActiveModifierRewards(CombatRuntime runtime)
        {
            try
            {
                if (runtime == null) return null;
                var plan = ClassForgePlugin.CurrentMergePlan;
                var tables = plan != null ? plan.ModifierTables : null;
                if (tables == null || tables.Count == 0) return null;

                var book = Book.Ordered;
                for (int ti = 0; ti < tables.Count; ti++)
                {
                    var table = tables[ti];
                    if (table == null || string.IsNullOrEmpty(table.SelectionRecipe)) continue;

                    var selRecipe = FindRecipe(book, table.SelectionRecipe);
                    if (selRecipe == null || !selRecipe.IsLive) continue;

                    string selectionName = DiscoverSelectionName(selRecipe);
                    if (string.IsNullOrEmpty(selectionName)) continue;

                    string activeId = runtime.GetSelection(selectionName);
                    if (string.IsNullOrEmpty(activeId)) continue;

                    for (int mi = 0; mi < table.Modifiers.Count; mi++)
                    {
                        var m = table.Modifiers[mi];
                        if (m == null || !string.Equals(m.Id, activeId, StringComparison.Ordinal)) continue;

                        var rewards = m.Rewards;
                        return new ActiveModifierRewards
                        {
                            ModifierId = activeId,
                            XpBonusPercent = rewards != null && rewards.XpBonusPercent.HasValue ? rewards.XpBonusPercent.Value : 0,
                            GoldBonusPercent = rewards != null && rewards.GoldBonusPercent.HasValue ? rewards.GoldBonusPercent.Value : 0,
                            ExtraLootChancePercent = rewards != null && rewards.ExtraLootChancePercent.HasValue ? rewards.ExtraLootChancePercent.Value : 0
                        };
                    }
                    // This table has an active selection but no matching row (should not happen for a
                    // consistent table) -- fall through and keep scanning any other shipped table.
                }
                return null;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] Active-modifier reward resolution failed (fail-safe, zero reward ops emitted): " + ex);
                return null;
            }
        }

        private static SkillRecipe FindRecipe(IReadOnlyList<SkillRecipe> book, string id)
        {
            for (int i = 0; i < book.Count; i++)
                if (string.Equals(book[i].Id, id, StringComparison.Ordinal)) return book[i];
            return null;
        }

        private static string DiscoverSelectionName(SkillRecipe selRecipe)
        {
            for (int i = 0; i < selRecipe.Effects.Count; i++)
                if (selRecipe.Effects[i].Type == EffectKind.SELECTION_SET) return selRecipe.Effects[i].Name;
            return null;
        }

        // =====================================================================================
        // Shared helpers for the hook layer
        // =====================================================================================

        /// <summary>
        /// <c>eRollStatus</c> → engine <c>RollTier</c>.
        /// <para><b>Ground-truth note:</b> the game's <c>eRollStatus</c> has only <b>three</b> members —
        /// <c>CRIT_FAIL, SUCCESS, PERFECT</c>. The engine's <c>RollTier</c> additionally declares
        /// <c>FAIL</c> (worst→best ordering <c>CRIT_FAIL &lt; FAIL &lt; SUCCESS &lt; PERFECT</c>), which the
        /// game can never produce. A recipe authoring <c>ROLL_TIER {Value: "FAIL"}</c> with
        /// <c>Comparator: EQ</c> therefore never matches. <c>GTE</c>/<c>LTE</c> comparisons against
        /// <c>FAIL</c> still behave sensibly because the ordering is preserved.</para>
        /// </summary>
        internal static RollTier MapRollTier(eRollStatus status)
        {
            switch (status)
            {
                case eRollStatus.CRIT_FAIL: return RollTier.CRIT_FAIL;
                case eRollStatus.PERFECT: return RollTier.PERFECT;
                default: return RollTier.SUCCESS;
            }
        }

        /// <summary>The party list for <c>ApplyAction</c>'s <c>pParty</c> when a hook did not supply one.
        /// <c>CoreHelper.GetParty(List&lt;Entity&gt;)</c> filters <c>GroupIndex == 0</c> (CoreHelper.cs L875).</summary>
        internal static List<Entity> ResolveParty(List<Entity> supplied, CombatContextAdapter ctx)
        {
            if (supplied != null) return supplied;
            try
            {
                if (ctx != null && ctx.State != null && ctx.State.Entities != null)
                    return CoreHelper.GetParty(ctx.State.Entities);
            }
            catch { }
            return new List<Entity>();
        }
    }
}
