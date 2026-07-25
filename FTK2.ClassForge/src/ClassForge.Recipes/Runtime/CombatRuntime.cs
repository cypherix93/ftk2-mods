using System;
using System.Collections.Generic;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Runtime
{
    /// <summary>Per-entity turn bookkeeping — SPEC-DELTA-v1.1 §6 <c>TurnState</c>.</summary>
    public sealed class TurnState
    {
        public int ActedRound = int.MinValue;
        public int MovedRound = int.MinValue;
        public string LastAbilityId = "";
    }

    /// <summary>
    /// The whole per-battle state table — SPEC-DELTA-v1.1 §6. Nothing here is transmitted; every key is a
    /// replicated identity (<c>Entity.Guid</c>) or a parity-hashed authored id (recipe id, <c>Budget.Key</c>,
    /// counter name).
    /// <para><b>Rule (charter rule 4):</b> no gameplay-affecting state survives a combat. That is enforced
    /// structurally by <see cref="RecipeStateStore"/>'s single-slot cache, not by an end-of-combat hook.</para>
    /// </summary>
    public sealed class CombatRuntime
    {
        /// <summary>The <c>CombatKey</c> this runtime belongs to.</summary>
        public readonly string CombatKey;

        /// <summary>Round counter. Mirrors <c>ICombatContext.Round</c>, which the Plugin drives from the
        /// <c>CombatHelper.NextTurn</c> postfix (<c>pIsNewRound == true</c>) — §6 names this the source of truth,
        /// not an unverified <c>TotalRounds</c> field.</summary>
        public int Round;

        /// <summary><c>(budgetKey, ownerGuid, targetGuid?)</c> → round the budget was used at.
        /// <c>ONCE_PER_COMBAT*</c> entries live for the runtime; <c>ONCE_PER_ROUND*</c> compare against
        /// <see cref="Round"/>.</summary>
        private readonly Dictionary<string, int> _budgets = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary><c>(recipeId, ownerGuid)</c> → expiry round.</summary>
        private readonly Dictionary<string, int> _cooldowns = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary><c>(ownerGuid, counterName)</c> → value.</summary>
        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly Dictionary<string, TurnState> _turnState = new Dictionary<string, TurnState>(StringComparer.Ordinal);

        public CombatRuntime(string combatKey)
        {
            CombatKey = combatKey ?? "";
        }

        /// <summary>US (U+001F) separator: cannot occur inside a guid, recipe id or counter name.</summary>
        private const string Sep = "\u001F";

        private static string Key3(string a, string b, string c)
        {
            return (a ?? "") + Sep + (b ?? "") + Sep + (c ?? "");
        }

        // ----- budgets -----------------------------------------------------------------

        /// <summary>True when the budget slot is still open for this owner/target at the current round.</summary>
        public bool IsBudgetAvailable(BudgetScope scope, string budgetKey, string ownerGuid, string targetGuid)
        {
            if (scope == BudgetScope.NONE) return true;
            string k = Key3(budgetKey, ownerGuid, ScopeTargetPart(scope, targetGuid));
            int usedAt;
            if (!_budgets.TryGetValue(k, out usedAt)) return true;
            if (scope == BudgetScope.ONCE_PER_ROUND || scope == BudgetScope.ONCE_PER_TARGET_PER_ROUND)
                return usedAt != Round;
            return false; // ONCE_PER_COMBAT*: consumed for the life of the runtime
        }

        public void ConsumeBudget(BudgetScope scope, string budgetKey, string ownerGuid, string targetGuid)
        {
            if (scope == BudgetScope.NONE) return;
            _budgets[Key3(budgetKey, ownerGuid, ScopeTargetPart(scope, targetGuid))] = Round;
        }

        private static string ScopeTargetPart(BudgetScope scope, string targetGuid)
        {
            if (scope == BudgetScope.ONCE_PER_TARGET_PER_ROUND || scope == BudgetScope.ONCE_PER_TARGET_PER_COMBAT)
                return targetGuid ?? "";
            return "";
        }

        // ----- cooldowns ---------------------------------------------------------------

        public bool IsCooldownReady(string recipeId, string ownerGuid)
        {
            int expiry;
            if (!_cooldowns.TryGetValue(Key3(recipeId, ownerGuid, ""), out expiry)) return true;
            return Round >= expiry;
        }

        public void StartCooldown(string recipeId, string ownerGuid, int rounds)
        {
            if (rounds <= 0) return;
            _cooldowns[Key3(recipeId, ownerGuid, "")] = Round + rounds;
        }

        // ----- counters ----------------------------------------------------------------

        public int GetCounter(string ownerGuid, string name)
        {
            int v;
            return _counters.TryGetValue(Key3(ownerGuid, name, ""), out v) ? v : 0;
        }

        public int AddCounter(string ownerGuid, string name, int delta, int? max)
        {
            int v = GetCounter(ownerGuid, name) + delta;
            if (max.HasValue && v > max.Value) v = max.Value;
            _counters[Key3(ownerGuid, name, "")] = v;
            return v;
        }

        public void SetCounter(string ownerGuid, string name, int value)
        {
            _counters[Key3(ownerGuid, name, "")] = value;
        }

        // ----- turn state --------------------------------------------------------------

        public TurnState GetTurnState(string ownerGuid)
        {
            TurnState s;
            if (!_turnState.TryGetValue(ownerGuid ?? "", out s))
            {
                s = new TurnState();
                _turnState[ownerGuid ?? ""] = s;
            }
            return s;
        }

        public bool HasTurnState(string ownerGuid)
        {
            return _turnState.ContainsKey(ownerGuid ?? "");
        }
    }

    /// <summary>
    /// Single-slot per-battle state cache — SPEC-DELTA-v1.1 §6.
    /// <para>The engine holds exactly ONE <c>(CombatKey, CombatRuntime)</c> pair. Any hook observing a
    /// different <c>CombatKey</c> drops the entire runtime and allocates a fresh one <b>before doing anything
    /// else</b>. This makes leakage structurally impossible even if an end-of-combat hook is missed — the
    /// direct fix for EOR's process-global <c>SteadyAimUsedThisCombat</c> HashSet whose only cleanup was a
    /// per-entity <c>.Remove()</c> (TM §6 hazard 3).</para>
    /// <para>This type has <b>no static fields</b> at all. Two dispatchers in the same process are fully
    /// independent — which is exactly what the determinism pair test exercises.</para>
    /// </summary>
    public sealed class RecipeStateStore
    {
        private string _combatKey;
        private CombatRuntime _runtime;

        /// <summary>Current runtime, or null before the first <see cref="Sync"/>.</summary>
        public CombatRuntime Current { get { return _runtime; } }

        /// <summary>
        /// Returns the runtime for <paramref name="ctx"/>, dropping and re-allocating it when the combat
        /// identity changed. Always call this first in any dispatch path.
        /// </summary>
        public CombatRuntime Sync(ICombatContext ctx)
        {
            string key = ctx == null ? "" : (ctx.CombatIdentity ?? "");
            if (_runtime == null || !string.Equals(_combatKey, key, StringComparison.Ordinal))
            {
                _combatKey = key;
                _runtime = new CombatRuntime(key);
            }
            if (ctx != null) _runtime.Round = ctx.Round;
            return _runtime;
        }

        /// <summary>Explicit end-of-combat reset. Redundant with <see cref="Sync"/>'s key check by design —
        /// §6 requires that correctness never depend on this being called.</summary>
        public void ResetCombat()
        {
            _combatKey = null;
            _runtime = null;
        }

        /// <summary>Explicit end-of-run reset. v1.1 introduces no per-run state (§6 "Per-run state: none"),
        /// so this is exactly <see cref="ResetCombat"/> plus the guarantee that it stays that way.</summary>
        public void ResetRun()
        {
            ResetCombat();
        }
    }
}
