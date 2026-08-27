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

        /// <summary>Encounter Modifiers spec §5/§6.1: <c>SELECTION_SET</c>'s state slot. <c>[LOCAL]</c> state,
        /// <c>[SYNCED]</c> cause — never transmitted, reset with the runtime like <see cref="_counters"/>.
        /// Reconstructable from replicated statuses at allocation time (§4.5, <see cref="ModifierReconstruction"/>).</summary>
        private readonly Dictionary<string, string> _selections = new Dictionary<string, string>(StringComparer.Ordinal);

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

        /// <summary>STATE_HASH_CHANCE spec §3.3 once-per-(salt, token) warn latch. Per-battle state like
        /// everything else here — resets with the runtime, keeping the engine free of static mutable
        /// state (§6). Returns true exactly once per pair.</summary>
        public bool MarkHashInputWarnedOnce(string salt, string token)
        {
            return _warnedHashInputs.Add((salt ?? "") + "|" + (token ?? ""));
        }

        private readonly HashSet<string> _warnedHashInputs = new HashSet<string>(StringComparer.Ordinal);

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

        // ----- selections (Encounter Modifiers spec §5/§6.1) ---------------------------

        /// <summary>Null when <paramref name="name"/> has no stored selection (never fired, or empty).</summary>
        public string GetSelection(string name)
        {
            string v;
            return !string.IsNullOrEmpty(name) && _selections.TryGetValue(name, out v) ? v : null;
        }

        public void SetSelection(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return;
            _selections[name] = value ?? "";
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

    /// <summary>
    /// Derived-state reconstruction — Encounter Modifiers spec §4.5. When a fresh <see cref="CombatRuntime"/>
    /// is allocated for a combat already in progress (JIP, mid-combat save/load, or a missed first hook), the
    /// only cross-event state the encounter-modifiers system needs — "which modifier is active" and "which
    /// enemies already got it" — is fully derivable from replicated state (the applied statuses themselves).
    /// <para>Pure function of (<see cref="ICombatContext.Entities"/>, registry); no RNG, no game references.
    /// The Plugin supplies the registry (built from the pack's <c>ModifierTable</c>, M-EM1) and the
    /// selection/apply recipe budget identity (auto-discovered from the loaded recipe book, so this stays a
    /// content-agnostic engine capability rather than hardcoding a pack's recipe ids).</para>
    /// </summary>
    public static class ModifierReconstruction
    {
        /// <summary>One row of the mapping the reconstruction scan needs: the status id a modifier's
        /// application grants, and the id stored into <c>CombatRuntime.Selections[selectionName]</c> when
        /// that modifier is the active one.</summary>
        public sealed class ModifierRegistryEntry
        {
            public string StatusId;
            public string ModifierId;
        }

        /// <summary>
        /// Scans <paramref name="ctx"/>'s entities in ascending ordinal <c>Guid</c> order for any status id
        /// present in <paramref name="registry"/>: first hit ⇒ that modifier is recorded as selected
        /// (selection latch set via <paramref name="selectionName"/>, no draws taken) and the apply recipe's
        /// per-target budget is marked consumed for every entity already bearing that modifier's status. A
        /// no-op when nothing in <paramref name="registry"/> is found on any entity (fresh/pre-selection combat).
        /// </summary>
        public static void Reconstruct(
            ICombatContext ctx, CombatRuntime runtime, IReadOnlyList<ModifierRegistryEntry> registry,
            string selectionName,
            BudgetScope selectionBudgetScope, string selectionBudgetKey,
            BudgetScope applyBudgetScope, string applyBudgetKey)
        {
            if (ctx == null || runtime == null || registry == null || registry.Count == 0) return;
            if (string.IsNullOrEmpty(selectionName)) return;

            var entities = EntitySets.SortedByRosterOrdinal(ctx.Entities);

            string chosenModifierId = null;
            string chosenStatusId = null;
            for (int i = 0; i < entities.Count && chosenModifierId == null; i++)
            {
                var statuses = entities[i].Statuses;
                if (statuses == null) continue;
                for (int s = 0; s < statuses.Count; s++)
                {
                    var hit = FindByStatus(registry, statuses[s]);
                    if (hit != null) { chosenModifierId = hit.ModifierId; chosenStatusId = hit.StatusId; break; }
                }
            }
            if (chosenModifierId == null) return;

            // Selection latch — no draws taken (§4.5).
            runtime.SetSelection(selectionName, chosenModifierId);
            runtime.ConsumeBudget(selectionBudgetScope, selectionBudgetKey, "", "");

            // Per-enemy application budgets — marked consumed for every entity already bearing the chosen
            // modifier's status, so a JIP peer never re-applies it to an already-modified late-wave enemy.
            for (int i = 0; i < entities.Count; i++)
            {
                var statuses = entities[i].Statuses;
                if (statuses == null) continue;
                for (int s = 0; s < statuses.Count; s++)
                {
                    if (string.Equals(statuses[s], chosenStatusId, StringComparison.Ordinal))
                    {
                        runtime.ConsumeBudget(applyBudgetScope, applyBudgetKey, "", entities[i].Guid);
                        break;
                    }
                }
            }
        }

        private static ModifierRegistryEntry FindByStatus(IReadOnlyList<ModifierRegistryEntry> registry, string statusId)
        {
            if (string.IsNullOrEmpty(statusId)) return null;
            for (int i = 0; i < registry.Count; i++)
                if (string.Equals(registry[i].StatusId, statusId, StringComparison.Ordinal)) return registry[i];
            return null;
        }
    }
}
