using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClassForge.Core;
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

                SyncCombat(state);

                ctx = new CombatContextAdapter(env, state);
                dispatcher = _cachedDispatcher;
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
        /// <c>public readonly int</c> identical on every peer and different for every combat.
        /// </summary>
        private static void SyncCombat(CombatState state)
        {
            int seed = state.Random.Seed;
            if (_cachedDispatcher != null && ReferenceEquals(_cachedState, state) && _cachedSeed == seed)
                return;

            _cachedState = state;
            _cachedSeed = seed;
            _cachedDispatcher = new RecipeDispatcher(Book, new GameRandomSource(state.Random), LogAdapter);

            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
            {
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] New CombatKey (seed=" + seed.ToString(CultureInfo.InvariantCulture) +
                    ") — per-battle recipe runtime reallocated from scratch (SPEC-DELTA-v1.1 §6).");
            }
        }

        /// <summary>Drops the per-battle runtime. Belt-and-braces only: <see cref="SyncCombat"/> already
        /// reallocates on a new CombatKey, so a missed end-of-combat hook cannot leak state.</summary>
        internal static void ResetCombat()
        {
            _cachedState = null;
            _cachedSeed = 0;
            _cachedDispatcher = null;
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
