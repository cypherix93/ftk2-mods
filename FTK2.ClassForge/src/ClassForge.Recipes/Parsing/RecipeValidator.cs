using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Loot;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Parsing
{
    /// <summary>
    /// Semantic validation on top of <see cref="RecipeParser"/>'s structural pass.
    /// <para>Enforces the per-primitive restrictions SPEC-DELTA-v1.1 states in prose:
    /// the SchemaVersion gate (§5.1), <c>ROLL_STAT_BONUS</c> being <c>ON_ABILITY_DECLARED</c>-only (§4.2 E1),
    /// <c>HEAL_MODIFIER</c> being <c>ON_HEAL_PENDING</c>-only <b>and RNG-free</b> (§4.2 E2, §2 T8),
    /// <c>STATUS_TYPE</c>/<c>TRIGGER_STATUS</c> being <c>ON_STATUS_APPLIED</c>-only (§3.2 C10, §4.1),
    /// and the <c>SUMMON.Count</c> cap of 4 (OQ#4).</para>
    /// <para>Every Error disables the recipe. Nothing throws.</para>
    /// </summary>
    public static class RecipeValidator
    {
        private static readonly TriggerKind[] TriggersWithRoll =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED, TriggerKind.ON_ENEMY_ABILITY_RESOLVED
        };

        private static readonly TriggerKind[] TriggersWithTarget =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED, TriggerKind.ON_ENEMY_ABILITY_RESOLVED,
            TriggerKind.ON_CRIT, TriggerKind.ON_KILL, TriggerKind.ON_HEAL, TriggerKind.ON_DAMAGE_DEALT,
            TriggerKind.ON_DAMAGE_TAKEN, TriggerKind.ON_CONSUMABLE_USED, TriggerKind.ON_STATUS_APPLIED,
            TriggerKind.ON_HEAL_PENDING
        };

        private static readonly TriggerKind[] TriggersWithSource =
        {
            TriggerKind.ON_DAMAGE_TAKEN, TriggerKind.ON_STATUS_APPLIED,
            TriggerKind.ON_ENEMY_ABILITY_RESOLVED, TriggerKind.ON_HEAL_PENDING
        };

        private static readonly TriggerKind[] TriggersWithAbility =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED, TriggerKind.ON_ENEMY_ABILITY_RESOLVED,
            TriggerKind.ON_CRIT, TriggerKind.ON_KILL, TriggerKind.ON_DAMAGE_DEALT, TriggerKind.ON_DAMAGE_TAKEN,
            TriggerKind.ON_HEAL, TriggerKind.ON_CONSUMABLE_USED
        };

        private static readonly TriggerKind[] TriggersWithItem =
        {
            TriggerKind.ON_CONSUMABLE_USED, TriggerKind.ON_HEAL_PENDING
        };

        /// <summary>Triggers whose context carries a DAMAGE magnitude — the legal scope of the
        /// STATE_HASH_CHANCE <c>TRIGGER_DAMAGE</c> input token (spec §2.1). <c>ON_HEAL_PENDING</c>'s
        /// Amount is a heal and is deliberately absent.</summary>
        private static readonly TriggerKind[] TriggersWithDamage =
        {
            TriggerKind.ON_DAMAGE_DEALT, TriggerKind.ON_DAMAGE_TAKEN, TriggerKind.ON_DAMAGE_PENDING
        };

        private static readonly TriggerKind[] TriggersWithFocus =
        {
            TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_ABILITY_USED
        };

        private static readonly string[] StatChangeTypesForNonHp = { "MAGICAL", "PHYSICAL", "REGEN" };

        public static void Validate(RecipeSet set)
        {
            for (int i = 0; i < set.Ordered.Count; i++) ValidateRecipe(set, set.Ordered[i]);
            ValidateHashSaltUniqueness(set);
        }

        /// <summary>state-hash-chance spec §2: a duplicate (Salt, Inputs) pair across the set means two
        /// gates share one verdict stream — the decorrelation the salt exists for is silently lost, so it
        /// is an Error, not a warning. Walks every condition surface a recipe has.</summary>
        private static void ValidateHashSaltUniqueness(RecipeSet set)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < set.Ordered.Count; i++)
            {
                var r = set.Ordered[i];
                CheckSaltList(set, r, r.Conditions, "Conditions", seen);
                for (int j = 0; j < r.Effects.Count; j++)
                {
                    var e = r.Effects[j];
                    string p = "Effects[" + j.ToString(CultureInfo.InvariantCulture) + "]";
                    CheckSaltList(set, r, e.Conditions, p + ".Conditions", seen);
                    if (e.Rank != null) CheckSaltList(set, r, e.Rank.Where, p + ".Rank.Where", seen);
                }
            }
        }

        private static void CheckSaltList(RecipeSet set, SkillRecipe r, List<RecipeCondition> list, string path, Dictionary<string, string> seen)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null || c.Type != ConditionKind.STATE_HASH_CHANCE || string.IsNullOrEmpty(c.Salt)) continue;
                string key = c.Salt + "|" + (c.Inputs != null ? string.Join(",", c.Inputs) : "");
                string firstOwner;
                if (seen.TryGetValue(key, out firstOwner))
                    RecipeParser.Err(set, r, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "].Salt", "E_HASH_SALT_DUP",
                        "duplicate (Salt, Inputs) pair '" + c.Salt + "' — first authored on '" + firstOwner +
                        "'; two conditions sharing a tuple share one verdict stream (spec §2)");
                else
                    seen[key] = r.Id;
            }
        }

        private static void ValidateRecipe(RecipeSet set, SkillRecipe r)
        {
            bool isV11 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCurrent, StringComparison.Ordinal);
            bool isV10 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionLegacy, StringComparison.Ordinal);
            bool isV12 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionLoot, StringComparison.Ordinal);
            bool isV13 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionStateHash, StringComparison.Ordinal);
            bool isV14 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCover, StringComparison.Ordinal);
            bool isV15 = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCapture, StringComparison.Ordinal);
            if (!isV11 && !isV10 && !isV12 && !isV13 && !isV14 && !isV15)
            {
                RecipeParser.Err(set, r, "SchemaVersion", "E_SCHEMA_UNSUPPORTED",
                    "SchemaVersion '" + r.SchemaVersion + "' cannot be run by this engine (supported: " +
                    Vocabulary.SchemaVersionLegacy + ", " + Vocabulary.SchemaVersionCurrent + ", " +
                    Vocabulary.SchemaVersionLoot + ", " + Vocabulary.SchemaVersionStateHash + ", " +
                    Vocabulary.SchemaVersionCover + ", " + Vocabulary.SchemaVersionCapture + ")");
                return; // nothing else is meaningful once the vocabulary version is unknown
            }

            if (r.ProcChance < 0 || r.ProcChance > 100)
                RecipeParser.Err(set, r, "ProcChance", "E_RANGE", "ProcChance must be 0..100, saw " + r.ProcChance.ToString(CultureInfo.InvariantCulture));
            if (r.AiProcChance < 0 || r.AiProcChance > 100)
                RecipeParser.Err(set, r, "AiProcChance", "E_RANGE", "AiProcChance must be 0..100, saw " + r.AiProcChance.ToString(CultureInfo.InvariantCulture));
            if (r.Cooldown < 0)
                RecipeParser.Err(set, r, "Cooldown", "E_RANGE", "Cooldown must be >= 0");

            // --- draw-count fork on the proc gate (docs/MULTIPLAYER.md R2) ---
            // RecipeDispatcher.EvaluateRecipe short-circuits `chance >= 100` to proc:true WITHOUT drawing,
            // so a pair that straddles 100 makes the number of draws this recipe takes depend on
            // ICombatEntity.IsAiControlled -- a per-unit property. That property is CharacterComponent
            // .GroupIndex (adapter: !CharacterHelper.IsFriendly), which IS replicated combat state, so the
            // fork resolves the same way on every peer and this is a WARNING, not an error. It is worth
            // warning about anyway because it is exactly the shape the brief flags: a balance tweak that
            // moves ONE of the two numbers across 100 turns a tuning change into a draw-count change, and
            // the property it then keys on is one nudge (a companion changing sides at
            // CombatHelper.cs:2219, a follower at AdventureHelper.cs:748) away from being mid-combat
            // mutable. Keep the pair on the same side of 100 and the recipe's draw cost is a constant.
            if ((r.ProcChance >= 100) != (r.AiProcChance >= 100))
                RecipeParser.Warn(set, r, "AiProcChance", "W_PROC_CHANCE_DRAW_FORK",
                    "ProcChance " + r.ProcChance.ToString(CultureInfo.InvariantCulture) + " and AiProcChance " +
                    r.AiProcChance.ToString(CultureInfo.InvariantCulture) + " straddle 100, so this recipe " +
                    "costs a different NUMBER of shared-stream draws for an AI-controlled owner than for a " +
                    "player-controlled one (chance >= 100 skips the roll entirely). Safe today because the " +
                    "AI/player discriminator is replicated GroupIndex, but keep the pair on the same side " +
                    "of 100 so draw cost stays a constant of the recipe.");

            // --- SchemaVersion gate on v1.1-only tokens (§5.1) ---
            if (isV10)
            {
                if (Contains(Vocabulary.V11OnlyTriggers, r.Trigger))
                    RecipeParser.Err(set, r, "Trigger", "E_SCHEMA_GATE",
                        "trigger " + r.Trigger + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                if (r.Budget.Scope != BudgetScope.NONE)
                    RecipeParser.Err(set, r, "Budget.Scope", "E_SCHEMA_GATE",
                        "Budget requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
            }

            // --- SchemaVersion gate on v1.2-only tokens (loot-grant verb spec §6, M-LG1) ---
            if (isV10 || isV11)
            {
                if (Contains(Vocabulary.V12OnlyTriggers, r.Trigger))
                    RecipeParser.Err(set, r, "Trigger", "E_SCHEMA_GATE",
                        "trigger " + r.Trigger + " requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
            }

            // --- SchemaVersion gate on v1.3-only tokens (state-hash-chance spec). 1.4 is additive over
            //     1.3, so it clears this gate too — a cover recipe still rides ON_DAMAGE_PENDING. ---
            if (!isV13 && !isV14 && !isV15)
            {
                if (Contains(Vocabulary.V13OnlyTriggers, r.Trigger))
                    RecipeParser.Err(set, r, "Trigger", "E_SCHEMA_GATE",
                        "trigger " + r.Trigger + " requires SchemaVersion " + Vocabulary.SchemaVersionStateHash);
            }

            // --- ON_DAMAGE_PENDING restricted vocabulary (state-hash-chance spec M-SH3): the hook has no
            //     GameRandom and no peer may advance the shared stream there (SPEC-DELTA §7.4's hazard),
            //     so the trigger is RNG-free by construction: no proc chances, and only draw-free effects. ---
            if (r.Trigger == TriggerKind.ON_DAMAGE_PENDING)
            {
                if (r.ProcChance != 100 || r.AiProcChance != 100)
                    RecipeParser.Err(set, r, "ProcChance", "E_DMGPEND_RNG",
                        "a chance-gated ON_DAMAGE_PENDING recipe is rejected — the hook has no legal RNG " +
                        "(SPEC-DELTA §7.4); gate with STATE_HASH_CHANCE instead");
                for (int i = 0; i < r.Effects.Count; i++)
                {
                    var kind = r.Effects[i].Type;
                    if (kind != EffectKind.DAMAGE_TAKEN_MULT && kind != EffectKind.COUNTER_ADD && kind != EffectKind.COUNTER_SET)
                        RecipeParser.Err(set, r, "Effects[" + i.ToString(CultureInfo.InvariantCulture) + "].Type", "E_DMGPEND_EFFECT",
                            "only DAMAGE_TAKEN_MULT and COUNTER_ADD/COUNTER_SET may ride ON_DAMAGE_PENDING (draw-free set)");
                }
            }

            // --- ON_COMBAT_LOOT restricted vocabulary (verb spec §6.1): the trigger is intrinsically
            //     once-per-combat, so Budget/Cooldown are rejected outright. ---
            if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
            {
                if (r.Budget.Scope != BudgetScope.NONE)
                    RecipeParser.Err(set, r, "Budget.Scope", "E_LOOT_BUDGET",
                        "Budget is rejected on ON_COMBAT_LOOT (the trigger is intrinsically once-per-combat)");
                if (r.Cooldown > 0)
                    RecipeParser.Err(set, r, "Cooldown", "E_LOOT_COOLDOWN",
                        "Cooldown is rejected on ON_COMBAT_LOOT (the trigger is intrinsically once-per-combat)");
            }

            // --- PickOneEffect (verb spec §6.1): ON_COMBAT_LOOT only, requires >= 2 Effects. ---
            if (r.PickOneEffect)
            {
                if (r.Trigger != TriggerKind.ON_COMBAT_LOOT)
                    RecipeParser.Err(set, r, "PickOneEffect", "E_PICKONE_TRIGGER_SCOPE",
                        "PickOneEffect is only valid on ON_COMBAT_LOOT");
                if (r.Effects.Count < 2)
                    RecipeParser.Err(set, r, "PickOneEffect", "E_PICKONE_COUNT",
                        "PickOneEffect requires at least 2 Effects");
            }

            ValidateConditionList(set, r, r.Conditions, "Conditions", isV10);
            if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
                ValidateLootConditionScope(set, r, r.Conditions, "Conditions");

            for (int i = 0; i < r.Effects.Count; i++)
            {
                string path = "Effects[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                var e = r.Effects[i];
                if (isV10)
                {
                    if (Contains(Vocabulary.V11OnlyEffects, e.Type))
                        RecipeParser.Err(set, r, path + ".Type", "E_SCHEMA_GATE",
                            "effect " + e.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                    if (Contains(Vocabulary.V11OnlyTargets, e.Target))
                        RecipeParser.Err(set, r, path + ".Target", "E_SCHEMA_GATE",
                            "target " + e.Target + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                    if (e.Conditions.Count > 0)
                        RecipeParser.Err(set, r, path + ".Conditions", "E_SCHEMA_GATE",
                            "per-effect Conditions require SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                }
                if (isV10 || isV11)
                {
                    if (Contains(Vocabulary.V12OnlyEffects, e.Type))
                        RecipeParser.Err(set, r, path + ".Type", "E_SCHEMA_GATE",
                            "effect " + e.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
                }
                // --- SchemaVersion gate on v1.4-only targets (RANDOM_TILE). Unlike the v1.1 target gate
                //     above, which only has to fence off 1.0, this one is an ALLOW-list: every schema
                //     BELOW 1.4 must reject it, 1.3 included. ---
                if (!isV14 && !isV15 && Contains(Vocabulary.V14OnlyTargets, e.Target))
                    RecipeParser.Err(set, r, path + ".Target", "E_SCHEMA_GATE",
                        "target " + e.Target + " requires SchemaVersion " + Vocabulary.SchemaVersionCover);
                // --- SchemaVersion gate on v1.5-only effects (CAPTURE). ALLOW-list, like the 1.4 target
                //     gate above: every schema BELOW 1.5 must reject it. ---
                if (!isV15 && Contains(Vocabulary.V15OnlyEffects, e.Type))
                    RecipeParser.Err(set, r, path + ".Type", "E_SCHEMA_GATE",
                        "effect " + e.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionCapture);
                if (!isV15 && !string.IsNullOrEmpty(e.CharacterConfigFrom))
                    RecipeParser.Err(set, r, path + ".CharacterConfigFrom", "E_SCHEMA_GATE",
                        "SUMMON.CharacterConfigFrom requires SchemaVersion " + Vocabulary.SchemaVersionCapture);
                ValidateConditionList(set, r, e.Conditions, path + ".Conditions", isV10);
                if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
                    ValidateLootConditionScope(set, r, e.Conditions, path + ".Conditions");
                if (e.Rank != null) ValidateConditionList(set, r, e.Rank.Where, path + ".Rank.Where", isV10);
                ValidateEffect(set, r, e, path);
            }

            // --- ProcChanceFormula (Encounter Modifiers spec §5, v1.2, M-EM2) ---
            if (r.ProcChanceFormula != null)
            {
                if (isV10 || isV11)
                    RecipeParser.Err(set, r, "ProcChanceFormula", "E_SCHEMA_GATE",
                        "ProcChanceFormula requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
                if (r.ProcChanceAuthored)
                    RecipeParser.Err(set, r, "ProcChanceFormula", "E_PCF_MUTEX",
                        "ProcChance and ProcChanceFormula are mutually exclusive");
                if (r.ProcChanceFormula.Min.HasValue && r.ProcChanceFormula.Max.HasValue &&
                    r.ProcChanceFormula.Min.Value > r.ProcChanceFormula.Max.Value)
                    RecipeParser.Err(set, r, "ProcChanceFormula", "E_RANGE", "ProcChanceFormula.Min must be <= Max");
                ValidateFormulaRows(set, r, r.ProcChanceFormula.Base, "ProcChanceFormula.Base", isV10);
                ValidateFormulaRows(set, r, r.ProcChanceFormula.Adjustments, "ProcChanceFormula.Adjustments", isV10);
            }

            // --- Scope: COMBAT (Encounter Modifiers spec §4.2, v1.2, M-EM2) ---
            if (r.Scope == RecipeScope.COMBAT)
            {
                if (!isV12)
                    RecipeParser.Err(set, r, "Scope", "E_COMBAT_SCHEMA_GATE",
                        "Scope COMBAT requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
                if (r.AiProcChanceAuthored)
                    RecipeParser.Err(set, r, "AiProcChance", "E_COMBAT_AIPROCCHANCE",
                        "AiProcChance is meaningless on a COMBAT-scoped recipe (no owner to select Ai vs. " +
                        "player chance) and is rejected");
                ValidateCombatScopeConditions(set, r, r.Conditions, "Conditions");
                for (int i = 0; i < r.Effects.Count; i++)
                {
                    string path = "Effects[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var e = r.Effects[i];
                    ValidateCombatScopeConditions(set, r, e.Conditions, path + ".Conditions");
                    if (!Contains(Vocabulary.TargetlessEffects, e.Type) && IsSelfLikeTarget(e.Target))
                        RecipeParser.Err(set, r, path + ".Target", "E_COMBAT_TARGET",
                            "COMBAT-scoped recipes have no owner — Target " + e.Target +
                            " (SELF/CASTER/ALLY_*/ENEMY_ALL) is undefined; use TRIGGER_TARGET or TRIGGER_SOURCE");
                }
            }
        }

        private static void ValidateFormulaRows(RecipeSet set, SkillRecipe r, List<ProcChanceFormulaRow> rows, string path, bool isV10)
        {
            for (int i = 0; i < rows.Count; i++)
                ValidateConditionList(set, r, rows[i].Conditions, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "].Conditions", isV10);
        }

        /// <summary>Encounter Modifiers spec §4.2 load-time rejection: any condition whose <c>Of</c>
        /// resolves to SELF (explicit or default) on a COMBAT-scoped recipe, since Owner is null and SELF
        /// would silently resolve to nothing. Only applies to conditions the <c>Of</c> selector is actually
        /// defined for — a condition that never reads <c>Of</c> at all (e.g. <c>IS_DUNGEON</c>) is unaffected.</summary>
        private static void ValidateCombatScopeConditions(RecipeSet set, SkillRecipe r, List<RecipeCondition> list, string path)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (Contains(Vocabulary.OfCapableConditions, c.Type) && c.Of == OfSelector.SELF)
                    RecipeParser.Err(set, r, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "].Of", "E_COMBAT_SELF_COND",
                        c.Type + " defaults/resolves to Of: SELF, which is undefined on a COMBAT-scoped " +
                        "recipe (no owner) — use TRIGGER_TARGET or TRIGGER_SOURCE, or a combat-level condition");
            }
        }

        private static bool IsSelfLikeTarget(TargetKind k)
        {
            return k == TargetKind.SELF || k == TargetKind.CASTER || k == TargetKind.ALLY_ALL ||
                   k == TargetKind.ALLY_ALL_OTHERS || k == TargetKind.ALLY_BY_RANK || k == TargetKind.ENEMY_ALL;
        }

        /// <summary>Loot-grant effect vocabulary — verb spec §6.1/§6.2. AFFIX_ROLL is further always
        /// rejected (reserved for M-LG4).</summary>
        private static readonly EffectKind[] LootGrantEffects =
        {
            EffectKind.GOLD_GRANT, EffectKind.ITEM_TAG_GRANT, EffectKind.LOOT_SCALE, EffectKind.AFFIX_ROLL
        };

        /// <summary>Conditions permitted on ON_COMBAT_LOOT — verb spec §6.1: "the pure-replicated-read
        /// subset"; combat-turn conditions (ROLL_TIER, FOCUS_SPENT, ...) have no context at combat end.</summary>
        private static readonly ConditionKind[] LootAllowedConditions =
        {
            ConditionKind.HP_THRESHOLD, ConditionKind.CHARACTER_TYPE
        };

        private static void ValidateLootConditionScope(RecipeSet set, SkillRecipe r, List<RecipeCondition> list, string path)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                string p = path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (!Contains(LootAllowedConditions, list[i].Type))
                    RecipeParser.Err(set, r, p + ".Type", "E_LOOT_COND_SCOPE",
                        list[i].Type + " is not permitted on ON_COMBAT_LOOT (only HP_THRESHOLD, CHARACTER_TYPE, Negate)");
            }
        }

        /// <summary>
        /// v1.4 <c>RANDOM_TILE</c> target rules.
        ///
        /// <para><b>ADD_STATUS only.</b> A board tile has no stats to change, no heal to modify, no rank to
        /// sort by and no allegiance to summon under. The one thing the game itself does to a tile is put a
        /// status on it (<c>CombatPhase.cs:2013</c> / <c>:2081</c>), so that is the one thing an author may
        /// aim at one. Every other effect kind resolves nothing and would be a silent no-op - exactly the
        /// mistake class this validator exists to make loud.</para>
        ///
        /// <para><b>STUN is banned, and so is the rest of CHARACTER_ONLY_STATUS.</b> The game's own
        /// <c>InteractableHelper.CHARACTER_ONLY_STATUS</c> (InteractableHelper.cs:261-269) is
        /// <c>{STUN, DAZE, GRAB, BLEED, DEATHMARK, DEATHSAVE}</c>, and <c>ApplyStatus</c>'s <c>"CHAOS"</c>
        /// branch strips exactly that set out of <c>CHAOS_STATUS_NAMES</c> whenever the target carries a
        /// <c>VenueTileComponent</c>. Nothing in the game stops a DIRECT call from attaching STUN to a
        /// tile's <c>StatusEffectComponent</c> anyway, so the refusal lives here. Matching is by authored-id
        /// PREFIX (<see cref="Vocabulary.TileIllegalStatusPrefixes"/>) because this assembly is pure C# and
        /// cannot read <c>StatusEffectConfig.Type</c>; the dispatcher re-checks the REAL type at plan time,
        /// which is what catches an id that does not follow the <c>STATUS_&lt;TYPE&gt;_NN</c> convention.</para>
        ///
        /// <para>ClassForge never draws from <c>CHAOS_STATUS_NAMES</c> at all - an author enumerates the
        /// pool explicitly in <c>StatusOneOf</c> - so there is no path by which the stun-bearing native pool
        /// can leak in.</para>
        /// </summary>
        private static void ValidateRandomTileTarget(RecipeSet set, SkillRecipe r, RecipeEffect e, string path)
        {
            if (e.Type != EffectKind.ADD_STATUS)
            {
                RecipeParser.Err(set, r, path + ".Target", "E_TILE_EFFECT_SCOPE",
                    "RANDOM_TILE is only valid on ADD_STATUS - a board tile is not a combatant, so " +
                    e.Type + " would resolve nothing against one");
                return;
            }

            CheckTileStatusLegal(set, r, e.Status, path + ".Status");
            if (e.StatusOneOf != null)
                for (int i = 0; i < e.StatusOneOf.Count; i++)
                    CheckTileStatusLegal(set, r, e.StatusOneOf[i],
                        path + ".StatusOneOf[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        }

        private static void CheckTileStatusLegal(RecipeSet set, SkillRecipe r, string statusId, string path)
        {
            if (string.IsNullOrEmpty(statusId)) return;
            var prefixes = Vocabulary.TileIllegalStatusPrefixes;
            for (int i = 0; i < prefixes.Count; i++)
            {
                if (!statusId.StartsWith(prefixes[i], StringComparison.Ordinal)) continue;
                RecipeParser.Err(set, r, path, "E_TILE_STATUS_ILLEGAL",
                    "'" + statusId + "' is a character-only status type (the game's own " +
                    "InteractableHelper.CHARACTER_ONLY_STATUS: STUN, DAZE, GRAB, BLEED, DEATHMARK, " +
                    "DEATHSAVE) and can never sit on a board tile");
                return;
            }
        }

        private static void ValidateEffect(RecipeSet set, SkillRecipe r, RecipeEffect e, string path)
        {
            // --- target/trigger compatibility ---
            if (e.Target == TargetKind.TRIGGER_TARGET || e.Target == TargetKind.TRIGGER_TARGET_POSITION)
            {
                // Encounter Modifiers spec §4.3: a COMBAT-scoped ON_COMBAT_START recipe IS bound a
                // TRIGGER_TARGET (the entity being initialized, owned by no one) — unlike an OWNED
                // ON_COMBAT_START recipe, which carries none (RecipeDispatcher.Fire's owned pass never
                // sets TriggerTarget for this trigger). TriggersWithTarget therefore cannot simply list
                // ON_COMBAT_START unconditionally; the COMBAT-scope carve-out is checked here instead.
                bool combatStartWithTarget = r.Trigger == TriggerKind.ON_COMBAT_START && r.Scope == RecipeScope.COMBAT;
                if (!combatStartWithTarget && !Contains(TriggersWithTarget, r.Trigger))
                    RecipeParser.Warn(set, r, path + ".Target", "W_NO_TRIGGER_TARGET",
                        r.Trigger + " carries no trigger target; this effect will be a no-op");
            }
            if (e.Target == TargetKind.TRIGGER_SOURCE && !Contains(TriggersWithSource, r.Trigger))
                RecipeParser.Warn(set, r, path + ".Target", "W_NO_TRIGGER_SOURCE",
                    r.Trigger + " carries no trigger source; this effect will be a no-op");
            if (e.Target == TargetKind.ALLY_BY_RANK && e.Rank == null)
                RecipeParser.Err(set, r, path + ".Rank", "E_RANK_MISSING", "ALLY_BY_RANK requires a Rank block");

            // --- v1.4 RANDOM_TILE: a board tile is not a combatant. ---
            if (e.Target == TargetKind.RANDOM_TILE) ValidateRandomTileTarget(set, r, e, path);

            // --- loot-grant effect scope (verb spec §6.1/§6.2): grant effects only fire ON_COMBAT_LOOT,
            //     and ON_COMBAT_LOOT accepts only grant effects. AFFIX_ROLL is further always reserved. ---
            bool isGrantEffect = Contains(LootGrantEffects, e.Type);
            if (r.Trigger == TriggerKind.ON_COMBAT_LOOT)
            {
                if (!isGrantEffect)
                    RecipeParser.Err(set, r, path + ".Type", "E_LOOT_EFFECT_SCOPE",
                        e.Type + " is not part of the ON_COMBAT_LOOT restricted vocabulary (only GOLD_GRANT, " +
                        "ITEM_TAG_GRANT, LOOT_SCALE; AFFIX_ROLL is reserved)");
            }
            else if (isGrantEffect)
            {
                RecipeParser.Err(set, r, path + ".Type", "E_LOOT_EFFECT_SCOPE",
                    e.Type + " is only valid on ON_COMBAT_LOOT");
            }
            if (e.Type == EffectKind.AFFIX_ROLL)
                RecipeParser.Err(set, r, path, "E_LOOT_RESERVED",
                    "AFFIX_ROLL is reserved; the v1 validator rejects it until M-LG4");

            // Persistent (§6 run-persistence escape hatch) is only meaningful on COUNTER_ADD/COUNTER_SET.
            if (e.Persistent && e.Type != EffectKind.COUNTER_ADD && e.Type != EffectKind.COUNTER_SET)
                RecipeParser.Err(set, r, path + ".Persistent", "E_PERSISTENT_SCOPE",
                    "Persistent is only valid on COUNTER_ADD/COUNTER_SET");

            switch (e.Type)
            {
                case EffectKind.ADD_STATUS:
                case EffectKind.REMOVE_STATUS:
                {
                    bool hasStatus = !string.IsNullOrEmpty(e.Status);
                    bool hasOneOf = e.StatusOneOf != null && e.StatusOneOf.Count > 0;
                    bool hasFromSelection = !string.IsNullOrEmpty(e.StatusFromSelection);
                    int howMany = (hasStatus ? 1 : 0) + (hasOneOf ? 1 : 0) + (hasFromSelection ? 1 : 0);
                    if (howMany == 0)
                        RecipeParser.Err(set, r, path, "E_STATUS_MISSING",
                            e.Type + " requires Status, StatusOneOf, or StatusFromSelection");
                    if (howMany > 1)
                        RecipeParser.Err(set, r, path, "E_STATUS_AMBIGUOUS",
                            "Status, StatusOneOf and StatusFromSelection are mutually exclusive");
                    if (hasStatus && string.Equals(e.Status, Vocabulary.TriggerStatusToken, StringComparison.Ordinal)
                        && r.Trigger != TriggerKind.ON_STATUS_APPLIED)
                        RecipeParser.Err(set, r, path + ".Status", "E_TRIGGER_STATUS_SCOPE",
                            "the TRIGGER_STATUS token is only bound under ON_STATUS_APPLIED");
                    if (e.Type == EffectKind.REMOVE_STATUS && !string.IsNullOrEmpty(e.FallbackStatus))
                        RecipeParser.Warn(set, r, path + ".FallbackStatus", "W_FALLBACK_IGNORED",
                            "FallbackStatus is only meaningful on ADD_STATUS (§4.1 IMMUNITY_FALLBACK)");
                    if (e.Duration.HasValue && e.Duration.Value < 0)
                        RecipeParser.Err(set, r, path + ".Duration", "E_RANGE", "Duration must be >= 0");
                    break;
                }
                case EffectKind.SELECTION_SET:
                {
                    if (string.IsNullOrEmpty(e.Name))
                        RecipeParser.Err(set, r, path + ".Name", "E_SELECTION_NAME_MISSING", "SELECTION_SET requires Name");
                    if (e.OneOfWeighted == null || e.OneOfWeighted.Count == 0)
                        RecipeParser.Err(set, r, path + ".OneOfWeighted", "E_VALUE_MISSING",
                            "SELECTION_SET requires a non-empty OneOfWeighted");
                    break;
                }
                case EffectKind.EVENT_BANNER:
                {
                    if (string.IsNullOrEmpty(e.LocKey) && string.IsNullOrEmpty(e.FallbackText))
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING", "EVENT_BANNER requires LocKey and/or FallbackText");
                    if (e.DurationMs.HasValue && e.DurationMs.Value < 0)
                        RecipeParser.Err(set, r, path + ".DurationMs", "E_RANGE", "DurationMs must be >= 0");
                    break;
                }
                case EffectKind.STAT_CHANGE:
                {
                    if (string.IsNullOrEmpty(e.Stat))
                        RecipeParser.Err(set, r, path + ".Stat", "E_STAT_MISSING", "STAT_CHANGE requires Stat");
                    if (string.IsNullOrEmpty(e.StatChangeType))
                        RecipeParser.Err(set, r, path + ".StatChangeType", "E_STAT_MISSING", "STAT_CHANGE requires StatChangeType");
                    bool hasValue = e.FlatValue.HasValue || e.FlatPercent.HasValue ||
                                    !string.IsNullOrEmpty(e.FlatValueFrom) || !string.IsNullOrEmpty(e.PercentFrom);
                    if (!hasValue)
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING",
                            "STAT_CHANGE requires FlatValue, FlatPercent, FlatValueFrom or PercentFrom");
                    if (!string.IsNullOrEmpty(e.Stat) && !string.Equals(e.Stat, "HP", StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(e.StatChangeType) && !Contains(StatChangeTypesForNonHp, e.StatChangeType))
                        RecipeParser.Warn(set, r, path + ".StatChangeType", "W_DAMAGE_TYPE",
                            "eDamageType '" + e.StatChangeType + "' on the non-HP stat '" + e.Stat +
                            "' is semantically unverified at runtime (OQ#5); prefer MAGICAL/PHYSICAL/REGEN");
                    // GATE C (Encounter Modifiers spec §5/§8.1): FlatValueFrom "TARGET_MXHP_PCT" reads the
                    // effect's own Percent field (sign + magnitude) — a plain FlatPercent STAT_CHANGE on
                    // Stat "MXHP" is a DIFFERENT, unsupported native path (InteractableHelper.
                    // GetStatChangePercentValue only handles "HP"/"XP" and throws for MXHP). A
                    // PercentFromSelection table (§6.1 "PercentFromSelection" sugar, M-EM3) is an
                    // equally-valid alternative source of Percent, authored only by the generator — it
                    // must not trip this "no Percent at all" warning.
                    // DAMAGE_DEALT_PCT: fail closed on both ways it can silently do nothing —
                    // no damage in scope, or no share authored.
                    if (string.Equals(e.FlatValueFrom, Vocabulary.SourceDamageDealtPct, StringComparison.Ordinal)
                        || string.Equals(e.PercentFrom, Vocabulary.SourceDamageDealtPct, StringComparison.Ordinal))
                    {
                        if (!Contains(TriggersWithDamage, r.Trigger))
                            RecipeParser.Err(set, r, path + ".FlatValueFrom", "E_VALUE_SOURCE_SCOPE",
                                "DAMAGE_DEALT_PCT is only legal under a damage-carrying trigger " +
                                "(ON_DAMAGE_DEALT, ON_DAMAGE_TAKEN, ON_DAMAGE_PENDING) — under '" +
                                r.Trigger + "' there is no damage in scope and it resolves to 0");
                        if (!e.Percent.HasValue)
                            RecipeParser.Err(set, r, path + ".Percent", "E_VALUE_SOURCE_NO_PERCENT",
                                "DAMAGE_DEALT_PCT with no Percent authored always resolves to 0 (no-op) — " +
                                "author the share, e.g. Percent: 25 for a quarter of the damage");
                    }

                    if (string.Equals(e.FlatValueFrom, Vocabulary.SourceTargetMxhpPct, StringComparison.Ordinal)
                        && !e.Percent.HasValue && string.IsNullOrEmpty(e.PercentFromSelection))
                        RecipeParser.Warn(set, r, path + ".Percent", "W_MXHP_PCT_NO_PERCENT",
                            "FlatValueFrom TARGET_MXHP_PCT with no Percent/PercentFromSelection authored always resolves to 0 (no-op)");
                    if (string.Equals(e.Stat, "MXHP", StringComparison.Ordinal) && e.FlatPercent.HasValue)
                        RecipeParser.Err(set, r, path + ".FlatPercent", "E_MXHP_FLATPERCENT_UNSUPPORTED",
                            "STAT_CHANGE Stat \"MXHP\" + FlatPercent throws natively (InteractableHelper." +
                            "GetStatChangePercentValue only handles HP/XP, GATE C) — use FlatValueFrom " +
                            "\"TARGET_MXHP_PCT\" + Percent instead");
                    break;
                }
                case EffectKind.SUMMON:
                {
                    // A summon is placed on a FREE tile belonging to the summoner's own group,
                    // resolved at runtime by the executor -- that is what decides allegiance, since
                    // TryCreateSummon copies the tile's GroupIndex onto the new character. Placing
                    // on an enemy tile spawns a HOSTILE creature, which is how the Beast Trainer's
                    // "partner" first arrived in red on the wrong side of the field.
                    //
                    // Because the tile is chosen at runtime there is no static trigger requirement:
                    // any trigger works so long as the summoner's side has an empty tile when it
                    // fires. What IS static is that the effect must resolve at least one target for
                    // an action to be emitted at all, so a target-less trigger is still a no-op.
                    if (e.Target == TargetKind.TRIGGER_TARGET_POSITION
                        && !Contains(TriggersWithTarget, r.Trigger))
                        RecipeParser.Err(set, r, path + ".Target", "E_SUMMON_TARGET",
                            "SUMMON targeting TRIGGER_TARGET_POSITION under '" + r.Trigger + "' resolves " +
                            "no target, so no summon action is emitted at all. Use SELF, or a trigger " +
                            "that carries a target.");

                    // --- v1.5 dynamic config source. Mutually exclusive with the static field: two
                    //     sources for one value is an authoring bug, and silently preferring one would
                    //     make the other invisible. ---
                    bool hasStatic = !string.IsNullOrEmpty(e.CharacterConfig);
                    bool hasDynamic = !string.IsNullOrEmpty(e.CharacterConfigFrom);
                    if (hasStatic && hasDynamic)
                        RecipeParser.Err(set, r, path + ".CharacterConfigFrom", "E_SUMMON_CONFIG_MUTEX",
                            "SUMMON takes CharacterConfig OR CharacterConfigFrom, never both");
                    if (hasDynamic)
                    {
                        if (e.SummonType != SummonType.SPECIFIC)
                            RecipeParser.Err(set, r, path + ".CharacterConfigFrom", "E_SUMMON_CONFIG_FROM_TYPE",
                                "CharacterConfigFrom names ONE creature and is therefore SummonType SPECIFIC " +
                                "only (RANDOM/PLAYTHING pick their own config from a weighted pool, " +
                                "CombatHelper.cs:120-197, and would ignore it)");
                        ValidateItemCustomDataToken(set, r, e.CharacterConfigFrom, path + ".CharacterConfigFrom");
                    }
                    if (e.SummonType == SummonType.SPECIFIC && !hasStatic && !hasDynamic)
                        RecipeParser.Err(set, r, path + ".CharacterConfig", "E_SUMMON_CONFIG",
                            "SUMMON with SummonType SPECIFIC requires CharacterConfig or CharacterConfigFrom");
                    if (e.Count < 1)
                        RecipeParser.Err(set, r, path + ".Count", "E_RANGE", "SUMMON Count must be >= 1");
                    if (e.Count > Vocabulary.SummonCountCap)
                        RecipeParser.Err(set, r, path + ".Count", "E_SUMMON_CAP",
                            "SUMMON Count is capped at " + Vocabulary.SummonCountCap.ToString(CultureInfo.InvariantCulture) +
                            " (OQ#4: each iteration takes its own placement/pool draws from CombatState.Random)");
                    break;
                }
                case EffectKind.CAPTURE:
                {
                    // A capture needs something to capture. Every CAPTURE target token resolves off the
                    // trigger, so a trigger that carries no target makes the whole effect a guaranteed
                    // no-op -- the ROLL_TIER{EQ FAIL} mistake class this repo has shipped before.
                    if (!Contains(TriggersWithTarget, r.Trigger))
                        RecipeParser.Err(set, r, path, "E_EFFECT_TRIGGER_SCOPE",
                            "CAPTURE under '" + r.Trigger + "' resolves no target to capture. Use a trigger " +
                            "that carries one (ON_ABILITY_USED, ON_DAMAGE_DEALT, ...).");
                    if (e.Target == TargetKind.SELF || e.Target == TargetKind.CASTER)
                        RecipeParser.Err(set, r, path + ".Target", "E_CAPTURE_TARGET",
                            "CAPTURE aimed at " + e.Target + " would capture the caster. Aim it at " +
                            "TRIGGER_TARGET.");
                    if (e.Target == TargetKind.RANDOM_TILE || e.Target == TargetKind.TRIGGER_TARGET_POSITION)
                        RecipeParser.Err(set, r, path + ".Target", "E_CAPTURE_TARGET",
                            "CAPTURE needs a COMBATANT; " + e.Target + " resolves a board tile, which has no " +
                            "CharacterComponent.ConfigName to store and cannot be removed from combat");
                    if (string.IsNullOrEmpty(e.IntoItem))
                        RecipeParser.Err(set, r, path + ".IntoItem", "E_CAPTURE_ITEM",
                            "CAPTURE requires IntoItem (the ThingConfig id of the carried item whose " +
                            "Thing.CustomData stores the captured config)");
                    if (string.IsNullOrEmpty(e.IntoKey))
                        RecipeParser.Err(set, r, path + ".IntoKey", "E_CAPTURE_KEY",
                            "CAPTURE requires IntoKey (the Thing.CustomData key to write)");
                    break;
                }
                case EffectKind.ROLL_STAT_BONUS:
                {
                    if (r.Trigger != TriggerKind.ON_ABILITY_DECLARED)
                        RecipeParser.Err(set, r, path, "E_EFFECT_TRIGGER_SCOPE",
                            "ROLL_STAT_BONUS is ON_ABILITY_DECLARED only (§4.2 E1: it replaces the PerformAbility prefix's stat delegates)");
                    if (string.IsNullOrEmpty(e.Stat))
                        RecipeParser.Err(set, r, path + ".Stat", "E_STAT_MISSING", "ROLL_STAT_BONUS requires Stat");
                    if (!e.Percent.HasValue && !e.Flat.HasValue && string.IsNullOrEmpty(e.PercentFrom) &&
                        string.IsNullOrEmpty(e.FlatValueFrom))
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING",
                            "ROLL_STAT_BONUS requires Percent, Flat, PercentFrom or FlatValueFrom");
                    break;
                }
                case EffectKind.DAMAGE_TAKEN_MULT:
                {
                    // v1.3, state-hash-chance spec M-SH3 — the retired SPEC-DELTA §7.4 park.
                    if (r.Trigger != TriggerKind.ON_DAMAGE_PENDING)
                        RecipeParser.Err(set, r, path, "E_EFFECT_TRIGGER_SCOPE",
                            "DAMAGE_TAKEN_MULT is ON_DAMAGE_PENDING only (it mutates CalculateFinalDamage's return value)");
                    if (!e.Percent.HasValue || e.Percent.Value == 0 || e.Percent.Value < -99 || e.Percent.Value > 99)
                        RecipeParser.Err(set, r, path + ".Percent", "E_VALUE_MISSING",
                            "DAMAGE_TAKEN_MULT requires a non-zero Percent in -99..99 (negative reduces damage)");
                    if (e.MinDelta.HasValue && e.MinDelta.Value < 1)
                        RecipeParser.Err(set, r, path + ".MinDelta", "E_RANGE", "MinDelta must be >= 1 when authored");
                    break;
                }
                case EffectKind.HEAL_MODIFIER:
                {
                    if (r.Trigger != TriggerKind.ON_HEAL_PENDING)
                        RecipeParser.Err(set, r, path, "E_EFFECT_TRIGGER_SCOPE",
                            "HEAL_MODIFIER is ON_HEAL_PENDING only (§4.2 E2: it mutates AddHealth's ref int pValue)");
                    if (r.ProcChance != 100 || r.AiProcChance != 100)
                        RecipeParser.Err(set, r, path, "E_HEAL_RNG",
                            "a chance-gated HEAL_MODIFIER is rejected (§2 T8 / §4.2 E2: no RNG is permitted on the heal path)");
                    if (!e.Percent.HasValue && !e.Flat.HasValue)
                        RecipeParser.Err(set, r, path, "E_VALUE_MISSING", "HEAL_MODIFIER requires Percent or Flat");
                    break;
                }
                case EffectKind.COUNTER_ADD:
                case EffectKind.COUNTER_SET:
                {
                    if (string.IsNullOrEmpty(e.Name))
                        RecipeParser.Err(set, r, path + ".Name", "E_COUNTER_NAME", e.Type + " requires Name");
                    if (e.Persistent && r.Scope != RecipeScope.OWNED)
                        RecipeParser.Err(set, r, path + ".Persistent", "E_PERSISTENT_SCOPE",
                            e.Type + " Persistent requires an OWNED-scope recipe (a per-character store needs an owner)");
                    break;
                }
                case EffectKind.GOLD_GRANT:
                {
                    if (!e.MinGold.HasValue || !e.MaxGold.HasValue)
                        RecipeParser.Err(set, r, path, "E_GOLD_RANGE_MISSING", "GOLD_GRANT requires MinGold and MaxGold");
                    else if (e.MinGold.Value > e.MaxGold.Value)
                        RecipeParser.Err(set, r, path, "E_GOLD_RANGE_INVALID", "GOLD_GRANT MinGold must be <= MaxGold");
                    else if (e.MinGold.Value < 0)
                        RecipeParser.Err(set, r, path + ".MinGold", "E_RANGE", "GOLD_GRANT MinGold must be >= 0");
                    break;
                }
                case EffectKind.ITEM_TAG_GRANT:
                {
                    if (string.IsNullOrEmpty(e.Tag))
                        RecipeParser.Err(set, r, path + ".Tag", "E_ITEM_TAG_MISSING", "ITEM_TAG_GRANT requires Tag");
                    if (e.Stack < 1)
                        RecipeParser.Err(set, r, path + ".Stack", "E_RANGE", "ITEM_TAG_GRANT Stack must be >= 1");
                    // Rarity, when present, is NOT enum-validated here: eItemRarities membership is a
                    // game-ref concern with no decompile evidence available inside this pure-C# core
                    // (deferred to the M-LG2 Plugin unit, which has EGT §2 in scope).
                    break;
                }
                case EffectKind.LOOT_SCALE:
                {
                    if (string.IsNullOrEmpty(e.ConfigName))
                        RecipeParser.Err(set, r, path + ".ConfigName", "E_LOOT_SCALE_CONFIG_MISSING", "LOOT_SCALE requires ConfigName");
                    else if (!Contains(LootVocabulary.ScaleStackConfigNames, e.ConfigName))
                        RecipeParser.Err(set, r, path + ".ConfigName", "E_LOOT_SCALE_CONFIG",
                            "LOOT_SCALE ConfigName must be one of PARTY_XP, XP, CURRENCY_ADVENTURE, CURRENCY_LORE");
                    if (!e.Percent.HasValue)
                        RecipeParser.Err(set, r, path + ".Percent", "E_VALUE_MISSING", "LOOT_SCALE requires Percent");
                    break;
                }
            }
        }

        /// <summary>
        /// v1.5 <c>SUMMON.CharacterConfigFrom</c> token shape:
        /// <c>ITEM_CUSTOM_DATA:&lt;ThingConfigId&gt;:&lt;Key&gt;</c>, exactly three colon-separated parts with
        /// no empty part. Caught at LOAD time because the runtime failure is silent — an unparseable token
        /// resolves nothing and the summon simply never appears, which is the invisible-content failure this
        /// repo keeps re-shipping (docs/research/reuse-before-authoring.md).
        /// <para>Only the SHAPE is checkable here: this is the pure-C# core and it has no access to
        /// <c>Env.Configs.Things</c>. Whether the named Thing exists is answered by the LiveDataHarness's
        /// reference-integrity pass and, at runtime, by the executor's guarded lookup.</para>
        /// </summary>
        private static void ValidateItemCustomDataToken(RecipeSet set, SkillRecipe r, string token, string path)
        {
            if (!token.StartsWith(Vocabulary.SourceItemCustomDataPrefix, StringComparison.Ordinal))
            {
                RecipeParser.Err(set, r, path, "E_CONFIG_FROM_TOKEN",
                    "unknown CharacterConfigFrom token '" + token + "' (the only supported form is " +
                    Vocabulary.SourceItemCustomDataPrefix + "<ThingConfigId>:<Key>)");
                return;
            }
            var parts = token.Split(':');
            if (parts.Length != 3)
            {
                RecipeParser.Err(set, r, path, "E_CONFIG_FROM_TOKEN",
                    "CharacterConfigFrom must be exactly " + Vocabulary.SourceItemCustomDataPrefix +
                    "<ThingConfigId>:<Key> (3 colon-separated parts, got " +
                    parts.Length.ToString(CultureInfo.InvariantCulture) + ")");
                return;
            }
            if (parts[1].Length == 0)
                RecipeParser.Err(set, r, path, "E_CONFIG_FROM_TOKEN",
                    "CharacterConfigFrom names no ThingConfig id");
            if (parts[2].Length == 0)
                RecipeParser.Err(set, r, path, "E_CONFIG_FROM_TOKEN",
                    "CharacterConfigFrom names no CustomData key");
        }

        private static void ValidateConditionList(RecipeSet set, SkillRecipe r, List<RecipeCondition> list, string path, bool isV10)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                string p = path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                var c = list[i];
                if (c.Type == ConditionKind.PARTY_HAS_FOLLOWER)
                    RecipeParser.Err(set, r, p + ".Type", "E_COND_CONTEXT",
                        "PARTY_HAS_FOLLOWER is legal only in statmodifiers.json (the combat dispatcher has no evaluator for it)");
                if (Contains(Vocabulary.V13OnlyConditions, c.Type) &&
                    !string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionStateHash, StringComparison.Ordinal) &&
                    !string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCover, StringComparison.Ordinal) &&
                    !string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCapture, StringComparison.Ordinal))
                    RecipeParser.Err(set, r, p + ".Type", "E_SCHEMA_GATE",
                        "condition " + c.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionStateHash);
                if (Contains(Vocabulary.V14OnlyConditions, c.Type) &&
                    !string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCover, StringComparison.Ordinal) &&
                    !string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCapture, StringComparison.Ordinal))
                    RecipeParser.Err(set, r, p + ".Type", "E_SCHEMA_GATE",
                        "condition " + c.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionCover);
                if (isV10 && Contains(Vocabulary.V11OnlyConditions, c.Type))
                    RecipeParser.Err(set, r, p + ".Type", "E_SCHEMA_GATE",
                        "condition " + c.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionCurrent);
                bool isV11Here = string.Equals(r.SchemaVersion, Vocabulary.SchemaVersionCurrent, StringComparison.Ordinal);
                if ((isV10 || isV11Here) && Contains(Vocabulary.V12OnlyConditions, c.Type))
                    RecipeParser.Err(set, r, p + ".Type", "E_SCHEMA_GATE",
                        "condition " + c.Type + " requires SchemaVersion " + Vocabulary.SchemaVersionLoot);
                if (c.Of != OfSelector.SELF && !Contains(Vocabulary.OfCapableConditions, c.Type))
                    RecipeParser.Warn(set, r, p + ".Of", "W_OF_IGNORED",
                        "the Of selector is not defined for " + c.Type + " (§3) and is ignored");
                if (c.Of == OfSelector.TRIGGER_SOURCE && !Contains(TriggersWithSource, r.Trigger))
                    RecipeParser.Warn(set, r, p + ".Of", "W_NO_TRIGGER_SOURCE",
                        r.Trigger + " carries no trigger source; this condition will read nothing and evaluate false");
                ValidateCondition(set, r, c, p);
            }
        }

        private static void ValidateCondition(RecipeSet set, SkillRecipe r, RecipeCondition c, string p)
        {
            switch (c.Type)
            {
                case ConditionKind.HP_THRESHOLD:
                    if (!c.Percent.HasValue && !c.Flat.HasValue)
                        RecipeParser.Err(set, r, p, "E_VALUE_MISSING", "HP_THRESHOLD requires Percent or Flat");
                    if (c.Percent.HasValue && c.Flat.HasValue)
                        RecipeParser.Err(set, r, p, "E_VALUE_AMBIGUOUS", "HP_THRESHOLD takes Percent or Flat, not both");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "HP_THRESHOLD requires a Comparator");
                    break;

                case ConditionKind.HAS_STATUS:
                case ConditionKind.LACKS_STATUS:
                case ConditionKind.WEAPON_CLASS:
                case ConditionKind.ABILITY_TAG:
                case ConditionKind.TARGET_BASE_TYPE:
                case ConditionKind.CHARACTER_TYPE:
                case ConditionKind.ABILITY_STAT:
                case ConditionKind.ITEM_CLASS:
                case ConditionKind.STATUS_TYPE:
                    if (string.IsNullOrEmpty(c.Value))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a string Value");
                    break;

                case ConditionKind.ROW:
                    if (string.IsNullOrEmpty(c.Value) ||
                        (!string.Equals(c.Value, "FRONT", StringComparison.Ordinal) &&
                         !string.Equals(c.Value, "BACK", StringComparison.Ordinal)))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "ROW Value must be FRONT or BACK");
                    break;

                case ConditionKind.HOSTILE_ACTION:
                case ConditionKind.ABILITY_RANGED:
                case ConditionKind.ABILITY_REPEATED:
                case ConditionKind.MOVED_THIS_ROUND:
                case ConditionKind.ALL_ALLIES_ACTED:
                case ConditionKind.ITEM_CONSUMABLE:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a boolean Value");
                    break;

                case ConditionKind.FOCUS_SPENT:
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "FOCUS_SPENT requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "FOCUS_SPENT requires a Comparator");
                    if (!Contains(TriggersWithFocus, r.Trigger))
                        RecipeParser.Warn(set, r, p, "W_NO_FOCUS",
                            r.Trigger + " carries no pCombatDecision.FocusUsed; FOCUS_SPENT reads 0");
                    break;

                case ConditionKind.FOCUS_CURRENT:
                    if (!c.ValueInt.HasValue &&
                        !string.Equals(c.Value, Vocabulary.FocusMaxToken, StringComparison.Ordinal))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING",
                            "FOCUS_CURRENT Value must be an integer or \"" + Vocabulary.FocusMaxToken + "\"");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "FOCUS_CURRENT requires a Comparator");
                    break;

                case ConditionKind.STATUS_COUNT:
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "STATUS_COUNT requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "STATUS_COUNT requires a Comparator");
                    break;

                case ConditionKind.COUNTER:
                    if (string.IsNullOrEmpty(c.Name))
                        RecipeParser.Err(set, r, p + ".Name", "E_COUNTER_NAME", "COUNTER requires Name");
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "COUNTER requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "COUNTER requires a Comparator");
                    break;

                case ConditionKind.ROLL_TIER:
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "ROLL_TIER requires a Comparator");
                    if (!Contains(TriggersWithRoll, r.Trigger))
                        RecipeParser.Warn(set, r, p, "W_NO_ROLL_DATA",
                            r.Trigger + " carries no pRollData; ROLL_TIER reads the default tier");
                    break;

                // --- Encounter Modifiers spec §5 (v1.2, M-EM2) ---

                case ConditionKind.PARTY_AVG_LEVEL:
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "PARTY_AVG_LEVEL requires an integer Value");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING", "PARTY_AVG_LEVEL requires a Comparator");
                    break;

                case ConditionKind.IS_DUNGEON:
                case ConditionKind.BOSS_FIGHT:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a boolean Value");
                    break;

                case ConditionKind.ENCOUNTER_PROPERTY:
                case ConditionKind.ENTITY_TAG:
                    if (string.IsNullOrEmpty(c.Value))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", c.Type + " requires a string Value (enum member name)");
                    break;

                case ConditionKind.CONFIG_NAME_CONTAINS:
                    if (string.IsNullOrEmpty(c.Value))
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "CONFIG_NAME_CONTAINS requires a string Value");
                    break;

                case ConditionKind.COMBAT_START_REAL:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "COMBAT_START_REAL requires a boolean Value");
                    if (r.Trigger != TriggerKind.ON_COMBAT_START)
                        RecipeParser.Err(set, r, p, "E_COND_TRIGGER_SCOPE", "COMBAT_START_REAL is ON_COMBAT_START only (§4.3)");
                    break;

                case ConditionKind.SELECTION_PRESENT:
                    if (string.IsNullOrEmpty(c.Name))
                        RecipeParser.Err(set, r, p + ".Name", "E_SELECTION_NAME_MISSING", "SELECTION_PRESENT requires Name");
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "SELECTION_PRESENT requires a boolean Value");
                    break;

                case ConditionKind.IS_ENEMY:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "IS_ENEMY requires a boolean Value");
                    break;

                // --- v1.4 ---
                case ConditionKind.SELF_LEVEL:
                    // Value must be present AND an integer: the parser only fills ValueInt for a JSON
                    // number, so "3" / "three" / true / a missing Value all land here as E_VALUE_MISSING
                    // rather than silently comparing against 0 (the ROLL_TIER{EQ FAIL} mistake class).
                    if (!c.ValueInt.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING",
                            "SELF_LEVEL requires an integer Value (the level to compare against)");
                    if (!c.HasComparator)
                        RecipeParser.Err(set, r, p + ".Comparator", "E_CMP_MISSING",
                            "SELF_LEVEL requires a Comparator (EQ, NE, LT, LTE, GT, GTE)");
                    break;

                case ConditionKind.HAS_ITEM:
                    // Value must be present AND a JSON string: the parser fills ValueInt for a number and
                    // ValueBool for a bool while ALSO stringifying both into Value, so a bare
                    // string.IsNullOrEmpty(c.Value) check would silently accept 7 / true as the Thing
                    // config name "7" / "true". A Thing id is a string key into Env.Configs.Things.
                    if (string.IsNullOrEmpty(c.Value) || c.ValueInt.HasValue || c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING",
                            "HAS_ITEM requires a string Value (the Thing ConfigName to look for, " +
                            "matched case-sensitively)");
                    break;

                // --- v1.4, cover spec ---
                case ConditionKind.ALLY_IN_FRONT:
                    if (!c.ValueBool.HasValue)
                        RecipeParser.Err(set, r, p + ".Value", "E_VALUE_MISSING", "ALLY_IN_FRONT requires a boolean Value");
                    break;

                // --- v1.3, state-hash-chance spec §2 ---
                case ConditionKind.STATE_HASH_CHANCE:
                {
                    if (!c.Percent.HasValue || c.Percent.Value < 1 || c.Percent.Value > 99)
                        RecipeParser.Err(set, r, p + ".Percent", "E_HASH_PERCENT",
                            "STATE_HASH_CHANCE Percent must be 1..99 — 0 and 100 are constant gates, " +
                            "which a hash must never express (use Enabled:false or omit the condition)");
                    if (!IsValidHashSalt(c.Salt))
                        RecipeParser.Err(set, r, p + ".Salt", "E_HASH_SALT",
                            "STATE_HASH_CHANCE Salt is required and must match ^[A-Z0-9_]{4,64}$");
                    if (c.Inputs == null || c.Inputs.Count < 2 || c.Inputs.Count > 8)
                        RecipeParser.Err(set, r, p + ".Inputs", "E_HASH_INPUTS",
                            "STATE_HASH_CHANCE requires 2..8 Inputs (a 1-token tuple is a near-constant gate, spec §6)");
                    else
                    {
                        bool hasCombatSeed = false;
                        for (int i = 0; i < c.Inputs.Count; i++)
                        {
                            var tok = c.Inputs[i];
                            if (!Contains(Vocabulary.StateHashInputTokens, tok))
                            {
                                RecipeParser.Err(set, r, p + ".Inputs", "E_HASH_TOKEN",
                                    "'" + tok + "' is not a STATE_HASH_CHANCE input token — the set is closed " +
                                    "(spec §3.2: every token must argue its replication)");
                                continue;
                            }
                            if (string.Equals(tok, "COMBAT_SEED", StringComparison.Ordinal)) hasCombatSeed = true;
                            if (string.Equals(tok, "TRIGGER_DAMAGE", StringComparison.Ordinal) &&
                                !Contains(TriggersWithDamage, r.Trigger))
                                RecipeParser.Err(set, r, p + ".Inputs", "E_HASH_TOKEN_SCOPE",
                                    "TRIGGER_DAMAGE is only legal under a damage-carrying trigger (spec §2.1 — " +
                                    "a silently-empty input is the ROLL_TIER{EQ FAIL} mistake class)");
                            if (string.Equals(tok, "TRIGGER_ITEM_ID", StringComparison.Ordinal) &&
                                !Contains(TriggersWithItem, r.Trigger))
                                RecipeParser.Err(set, r, p + ".Inputs", "E_HASH_TOKEN_SCOPE",
                                    "TRIGGER_ITEM_ID is only legal under an item-carrying trigger (spec §2.1)");
                            if (string.Equals(tok, "ABILITY_ID", StringComparison.Ordinal) &&
                                !Contains(TriggersWithAbility, r.Trigger))
                                RecipeParser.Err(set, r, p + ".Inputs", "E_HASH_TOKEN_SCOPE",
                                    "ABILITY_ID is only legal under an ability-carrying trigger (spec §2.1)");
                        }
                        if (!hasCombatSeed)
                            RecipeParser.Warn(set, r, p + ".Inputs", "W_HASH_NO_COMBAT_SEED",
                                "tuple lacks COMBAT_SEED — verdicts will repeat across combats in identical " +
                                "states (OQ-SH3 strongly recommends including it)");
                    }
                    break;
                }
            }

            if (c.Type == ConditionKind.STATUS_TYPE && r.Trigger != TriggerKind.ON_STATUS_APPLIED)
                RecipeParser.Err(set, r, p, "E_COND_TRIGGER_SCOPE",
                    "STATUS_TYPE is ON_STATUS_APPLIED only (§3.2 C10)");

            if ((c.Type == ConditionKind.ITEM_CLASS || c.Type == ConditionKind.ITEM_CONSUMABLE) &&
                !Contains(TriggersWithItem, r.Trigger))
                RecipeParser.Warn(set, r, p, "W_NO_ITEM",
                    r.Trigger + " carries no pThing; " + c.Type + " will evaluate false");

            if ((c.Type == ConditionKind.ABILITY_TAG || c.Type == ConditionKind.ABILITY_RANGED ||
                 c.Type == ConditionKind.ABILITY_STAT || c.Type == ConditionKind.HOSTILE_ACTION ||
                 c.Type == ConditionKind.ABILITY_REPEATED) &&
                !Contains(TriggersWithAbility, r.Trigger))
                RecipeParser.Warn(set, r, p, "W_NO_ABILITY",
                    r.Trigger + " carries no ability id; " + c.Type + " will evaluate false");
        }

        /// <summary>state-hash-chance spec §2: <c>^[A-Z0-9_]{4,64}$</c>, checked without a Regex dependency.</summary>
        private static bool IsValidHashSalt(string salt)
        {
            if (string.IsNullOrEmpty(salt) || salt.Length < 4 || salt.Length > 64) return false;
            for (int i = 0; i < salt.Length; i++)
            {
                char ch = salt[i];
                if ((ch < 'A' || ch > 'Z') && (ch < '0' || ch > '9') && ch != '_') return false;
            }
            return true;
        }

        private static bool Contains<T>(IReadOnlyList<T> list, T value)
        {
            var cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < list.Count; i++) if (cmp.Equals(list[i], value)) return true;
            return false;
        }

        private static bool Contains(string[] list, string value)
        {
            for (int i = 0; i < list.Length; i++)
                if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
