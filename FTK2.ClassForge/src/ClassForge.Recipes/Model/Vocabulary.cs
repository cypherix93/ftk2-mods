using System;
using System.Collections.Generic;

namespace ClassForge.Recipes.Model
{
    /// <summary>Trigger tokens — SPEC-DELTA-v1.1 §2 (7 v1 + 8 added = 15 tokens).</summary>
    public enum TriggerKind
    {
        // --- v1, re-anchored (§2.1) ---
        ON_ABILITY_USED,
        ON_CRIT,
        ON_KILL,
        ON_HEAL,
        ON_DAMAGED,           // deprecated alias of ON_DAMAGE_TAKEN
        ON_TURN_START,
        ON_TURN_END,
        // --- added in v1.1 (§2.2) ---
        ON_COMBAT_START,           // T1
        ON_ABILITY_DECLARED,       // T2
        ON_DAMAGE_DEALT,           // T3
        ON_DAMAGE_TAKEN,           // T4
        ON_STATUS_APPLIED,         // T5
        ON_CONSUMABLE_USED,        // T6
        ON_ENEMY_ABILITY_RESOLVED, // T7
        ON_HEAL_PENDING,           // T8
        // --- added in v1.2, loot-grant verb spec (docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §6.1) ---
        ON_COMBAT_LOOT,
        // --- added in v1.3, state-hash-chance spec M-SH3 ---
        /// <summary><c>InteractableHelper.CalculateFinalDamage</c> <b>Postfix</b> (PSN §2 L1708) — the
        /// pre-application damage value, mutable via <c>DAMAGE_TAKEN_MULT</c>. The hook has no
        /// <c>GameRandom</c> parameter, which is exactly why this trigger is validator-enforced RNG-free
        /// and why chance gates on it must be <c>STATE_HASH_CHANCE</c>. v1.3 limitation (recorded): the
        /// Plugin fires it for PHYSICAL damage only — its sole consumer (SHIELDBEARER) is physical-only
        /// in EOR 0.7.0.62 (Plugin.cs L26733 gates <c>eDamageType == 0</c>).</summary>
        ON_DAMAGE_PENDING
    }

    /// <summary>Condition tokens — SPEC-DELTA-v1.1 §3 (8 v1 + 15 v1.1 + 9 v1.2 encounter-modifiers = 32 tokens).</summary>
    public enum ConditionKind
    {
        // --- v1 (§3.1) ---
        HP_THRESHOLD,
        HAS_STATUS,
        LACKS_STATUS,
        ROW,
        WEAPON_CLASS,
        ABILITY_TAG,
        TARGET_BASE_TYPE,
        TARGET_BASE_TYPE_NOT,  // deprecated alias of TARGET_BASE_TYPE + Negate
        // --- added in v1.1 (§3.2) ---
        ROLL_TIER,        // C1
        HOSTILE_ACTION,   // C2
        ABILITY_STAT,     // C3
        ABILITY_RANGED,   // C4
        ABILITY_REPEATED, // C5
        CHARACTER_TYPE,   // C6
        FOCUS_SPENT,      // C7
        FOCUS_CURRENT,    // C8
        STATUS_COUNT,     // C9
        STATUS_TYPE,      // C10
        COUNTER,          // C11
        MOVED_THIS_ROUND, // C12
        ALL_ALLIES_ACTED, // C13
        ITEM_CLASS,       // C14
        ITEM_CONSUMABLE,  // C15
        // --- added in v1.2, Encounter Modifiers spec §5 (M-EM2) ---
        PARTY_AVG_LEVEL,
        IS_DUNGEON,
        BOSS_FIGHT,
        ENCOUNTER_PROPERTY,
        ENTITY_TAG,
        CONFIG_NAME_CONTAINS,
        COMBAT_START_REAL,
        SELECTION_PRESENT,
        IS_ENEMY,          // GATE D: CharacterHelper.IsEnemy semantics (GroupIndex == 1)
        // --- added in v1.3, conditional-stat-modifier spec §4.2 ---
        /// <summary>EOR62 <c>PartyHasPetOrMercenary()</c> (L24576): any player follower resolves to a
        /// pet or mercenary entity. Legal ONLY in a CONDITIONAL_STAT_MODIFIER context — the combat
        /// dispatcher has no evaluator for it, so <c>RecipeValidator</c> rejects it in skillrecipes.json.</summary>
        PARTY_HAS_FOLLOWER,
        /// <summary>state-hash-chance spec §2: a deterministic, draw-free chance gate — FNV-1a over
        /// <c>Salt|inputs…</c>, verdict <c>h % 100 &lt; Percent</c>. NOT a roll (SPEC-DELTA §5.2
        /// invariant-1 amendment); correlated across re-evaluation with identical inputs (spec §6).</summary>
        STATE_HASH_CHANCE,
        // --- added in v1.4, cover spec: board-adjacency reads ---
        /// <summary>The tile DIRECTLY IN FRONT of the owner — same board row-line (<c>TileY</c>),
        /// adjacent column (<c>|dx| == 1</c>), <c>FRONT</c> row, same group — holds a LIVING ALLY.
        /// Mirrors the engine's own "adjacent tile in the other row" predicate (CombatHelper.cs:2414:
        /// same <c>y</c>, <c>Math.Abs(dx) == 1</c>, target tile's <c>RowPositionsType</c> equals the
        /// wanted row) rather than assuming a direction: <c>VenueHelper.getSlotRow</c>
        /// (VenueHelper.cs:39) makes FRONT/BACK a per-tile property, and the stock board
        /// <c>"|..Aa.bB..|"</c> puts group 0's FRONT at x+1 but group 1's FRONT at x-1. Pure read of
        /// replicated state — ZERO random draws, so it composes with <c>ON_DAMAGE_PENDING</c>.</summary>
        ALLY_IN_FRONT,
        /// <summary>THIS character's own progression level (<c>ICombatEntity.Level</c>), compared with
        /// the condition's <c>Comparator</c> against an integer <c>Value</c>. Distinct from
        /// <c>PARTY_AVG_LEVEL</c>, which averages the whole party and therefore misfires in a mixed
        /// party — the reason this token did not exist before. Reads SELF only (the <c>Of</c> selector
        /// is not defined for it). Pure read of replicated state — zero random draws.
        /// <para>An UNKNOWN level (<c>EntityReads.UnknownLevel</c>) makes the condition false
        /// <b>before</b> <c>Negate</c> is applied: a level gate must never silently open.</para></summary>
        SELF_LEVEL,
        /// <summary>THIS character CARRIES the Thing named by the string <c>Value</c>
        /// (<c>ICombatEntity.HasItem</c> — an ordinal scan of <c>CharacterComponent.Things</c>, the read
        /// <c>InventoryHelper.HasItemByName</c> performs at InventoryHelper.cs:424). Inventory POSSESSION,
        /// not equipment: toolbelt Things have no <c>eEquipmentSlots</c> slot and can never be equipped,
        /// and an equipped Thing is still in <c>Things</c> anyway. Reads SELF only (the <c>Of</c> selector
        /// is not defined for it). Pure read of replicated state — zero random draws.
        /// <para>Distinct from <c>ITEM_CLASS</c>/<c>ITEM_CONSUMABLE</c>, which read the TRIGGERING
        /// ability's Thing and are false under any trigger that carries no <c>pThing</c>; this one reads
        /// the inventory and is answerable under every trigger, <c>ON_COMBAT_START</c> included.</para>
        /// <para>An UNREADABLE inventory (<c>HasItem</c> returns <c>null</c>) makes the condition false
        /// <b>before</b> <c>Negate</c> is applied: a possession gate must never silently open. A readable
        /// inventory that simply lacks the item is an ordinary false, which <c>Negate</c> may invert.</para></summary>
        HAS_ITEM
    }

    /// <summary>Effect tokens — SPEC-DELTA-v1.1 §4 (4 v1 + 4 v1.1 + 4 loot v1.2 + 2 encounter-modifiers v1.2 = 14 tokens).</summary>
    public enum EffectKind
    {
        ADD_STATUS,
        REMOVE_STATUS,
        STAT_CHANGE,
        SUMMON,
        ROLL_STAT_BONUS, // E1
        HEAL_MODIFIER,   // E2
        COUNTER_ADD,     // E3
        COUNTER_SET,     // E4
        // --- added in v1.2, loot-grant verb spec §6.2 (ON_COMBAT_LOOT-only; emit LootOp deltas) ---
        GOLD_GRANT,
        ITEM_TAG_GRANT,
        LOOT_SCALE,
        AFFIX_ROLL,      // reserved: parses, but the v1 validator always rejects it (M-LG4)
        // --- added in v1.2, Encounter Modifiers spec §5 (M-EM2) ---
        SELECTION_SET,
        EVENT_BANNER,
        // --- added in v1.3, state-hash-chance spec M-SH3 ---
        /// <summary>Mutates the pending damage on <c>ON_DAMAGE_PENDING</c> (the SPEC-DELTA §7.4 park,
        /// retired): <c>delta = sign(Percent) * max(MinDelta, ceil(damage * |Percent| / 100))</c>,
        /// result floored at 0 — EOR 0.7.0.62's SHIELDBEARER arithmetic verbatim (L26733:
        /// <c>Max(2, CeilToInt(result * 0.25f))</c>). ON_DAMAGE_PENDING-only, validator-enforced.</summary>
        DAMAGE_TAKEN_MULT,
        // --- added in v1.4.1 -> v1.5, capture spec: the monster-capture verb ---
        /// <summary>Binds the resolved TARGET's <c>CharacterComponent.ConfigName</c> into an INVENTORY
        /// ITEM's <c>Thing.CustomData</c> (the owner's <c>IntoItem</c> Thing, key <c>IntoKey</c>) and then
        /// removes that target from the fight.
        /// <para>The store is the game's own durable per-item mod-data idiom — <c>Thing.CustomData</c> is
        /// <c>public Dictionary&lt;string,string&gt;</c> (Thing.cs:18) written through
        /// <c>CoreHelper.SetCustomData</c> (CoreHelper.cs:1646), which is how the shipped honeybee stores its
        /// cooldown (FollowerHelper.cs:338). It rides the existing save schema: no new save key, no new
        /// ThingConfig, and the record lives exactly as long as the item does.</para>
        /// <para><b>Removal is not a kill.</b> <c>CharacterHelper.KillCharacter</c> leaves a corpse on the
        /// board; the engine's real removal (<c>CombatPhase._processCombatResults</c>' local
        /// <c>removeFromCombat</c>, CombatPhase.cs:4362-4398) is a local function no mod code can call. The
        /// ONE reachable route is pushing <c>(eAbilityResults.PLAYTHINGED, targetEntity)</c> into the
        /// ability's results list, which <c>_processCombatResults</c> (CombatPhase.cs:4329-4338) turns into
        /// <c>KillCharacter</c> + <c>removeFromCombat</c> + <c>_checkChargeRetargets()</c>. That last call is
        /// why hand-rolling removal is forbidden: skipping it leaves charged abilities aimed at an entity
        /// that is no longer on the board.</para>
        /// <para><b>Eligibility is a Plugin-side gate, not an authoring convenience.</b> The same
        /// <c>_processCombatResults</c> branch also does
        /// <c>_additionalDrops.Add(InventoryHelper.CreateThing(SkillHelper.GetEnemyDoll(entity), 1))</c>, and
        /// <c>GetEnemyDoll</c> returns <c>null</c> for any config without a <c>PLAYTHING_*</c> tag
        /// (SkillHelper.cs:1513-1531) — <c>CreateThing(null)</c> then throws inside the game's own frame,
        /// outside every mod try/catch. See the Plugin's capture-rules unit.</para>
        CAPTURE
    }

    /// <summary>Recipe-level scope — Encounter Modifiers spec §4.2. <c>OWNED</c> is exactly today's v1.1
    /// semantics (default, field omitted everywhere pre-v1.2); <c>COMBAT</c> is the new ownerless
    /// registration capability: live for every combat while the pack is enabled, evaluated once per
    /// trigger event after all owned recipes for that event.</summary>
    public enum RecipeScope
    {
        OWNED,
        COMBAT
    }

    /// <summary>Target tokens — SPEC-DELTA-v1.1 §4.3 (5 v1 + 4 added = 9 tokens).</summary>
    public enum TargetKind
    {
        SELF,
        CASTER,
        TRIGGER_TARGET,
        TRIGGER_TARGET_POSITION,
        ALLY_ALL,
        TRIGGER_SOURCE,    // v1.1
        ALLY_ALL_OTHERS,   // v1.1
        ENEMY_ALL,         // v1.1
        ALLY_BY_RANK,      // v1.1
        /// <summary>
        /// v1.4 — a UNIFORMLY DRAWN PLAYABLE BOARD TILE. The first target token that resolves to something
        /// other than a combatant, and the primitive that makes "drop a random status on a random tile"
        /// expressible at all (previously written off as impossible: <c>StatusOneOf</c> gave a random
        /// ELEMENT, but nothing gave a random TARGET).
        /// <para>One <c>IRandomSource.NextInt(0, Tiles.Count)</c> draw off the shared combat stream — the
        /// exact mechanism <c>StatusOneOf</c> already uses — indexing the (Y,X)-ordered
        /// <c>ICombatContext.Tiles</c>. The ordering is a pure function of the static venue map, so the same
        /// index is the same board square on every peer.</para>
        /// <para>Legal on <c>ADD_STATUS</c> only (a tile is not a combatant: it has no stats to change, no
        /// heal to modify and no rank), and never under the RNG-free <c>ON_DAMAGE_PENDING</c>, which admits
        /// no <c>ADD_STATUS</c> at all.</para>
        /// <para><b>Fail-safe:</b> an empty <c>Tiles</c> list is a logged no-op that takes ZERO draws.
        /// A peer that cannot see the board must never advance the shared stream.</para>
        /// </summary>
        RANDOM_TILE
    }

    /// <summary>Universal <c>Of</c> selector — SPEC-DELTA-v1.1 §3.</summary>
    public enum OfSelector
    {
        SELF,
        TRIGGER_TARGET,
        TRIGGER_SOURCE
    }

    /// <summary>Budget scopes — SPEC-DELTA-v1.1 §5.1 (5 values).</summary>
    public enum BudgetScope
    {
        NONE,
        ONCE_PER_ROUND,
        ONCE_PER_COMBAT,
        ONCE_PER_TARGET_PER_ROUND,
        ONCE_PER_TARGET_PER_COMBAT
    }

    /// <summary>Budget consumption modes — SPEC-DELTA-v1.1 §5.1.</summary>
    public enum ConsumeOn
    {
        PROC,
        EVALUATION,
        EFFECT_APPLIED
    }

    public enum Comparator
    {
        EQ,
        NE,
        LT,
        LTE,
        GT,
        GTE
    }

    /// <summary><c>eRollStatus</c>, ordered worst→best so GTE/LTE mean "at least/at most this good".</summary>
    public enum RollTier
    {
        CRIT_FAIL = 0,
        FAIL = 1,
        SUCCESS = 2,
        PERFECT = 3
    }

    public enum StatusCategory
    {
        ANY,
        HARMFUL,
        BENEFICIAL
    }

    public enum RankOrder
    {
        LOWEST,
        HIGHEST
    }

    /// <summary><c>eSummonTypes</c> — SPEC-DELTA-v1.1 OQ#4.</summary>
    public enum SummonType
    {
        SPECIFIC,
        RANDOM,
        PLAYTHING,
        AS_FOLLOWER
    }

    /// <summary><c>HEAL_MODIFIER.Scope</c> — SPEC-DELTA-v1.1 §4.2 E2.</summary>
    public enum HealScope
    {
        RECEIVED,
        GIVEN
    }

    /// <summary>
    /// Token tables. Everything is <c>const</c> or <c>static readonly</c> — the engine holds
    /// <b>no static mutable state</b> (SPEC-DELTA-v1.1 §6, the direct fix for EOR's
    /// process-global <c>SteadyAimUsedThisCombat</c> leak).
    /// </summary>
    public static class Vocabulary
    {
        public const string SchemaVersionCurrent = "1.1";
        public const string SchemaVersionLegacy = "1.0";

        /// <summary>Schema version gating <c>ON_COMBAT_LOOT</c> + the loot-grant effect vocabulary
        /// (loot-grant verb spec §6, M-LG1). Additive over 1.1 — nothing 1.1-authored breaks.</summary>
        public const string SchemaVersionLoot = "1.2";

        /// <summary>Schema version gating <c>STATE_HASH_CHANCE</c> / <c>ON_DAMAGE_PENDING</c> /
        /// <c>DAMAGE_TAKEN_MULT</c> (state-hash-chance spec). Additive over 1.2.</summary>
        public const string SchemaVersionStateHash = "1.3";

        /// <summary>Schema version gating <c>ALLY_IN_FRONT</c> / <c>SELF_LEVEL</c> / <c>HAS_ITEM</c>
        /// (cover spec). Additive over 1.3 —
        /// everything 1.3 could author remains legal here, including <c>ON_DAMAGE_PENDING</c>.</summary>
        public const string SchemaVersionCover = "1.4";

        /// <summary>Schema version gating <c>CAPTURE</c> and <c>SUMMON.CharacterConfigFrom</c> (capture
        /// spec). Additive over 1.4 — everything 1.4 could author remains legal here.</summary>
        public const string SchemaVersionCapture = "1.5";

        /// <summary>Status token meaning "the status carried by the trigger" — SPEC-DELTA-v1.1 §4.1.</summary>
        public const string TriggerStatusToken = "TRIGGER_STATUS";

        /// <summary>Cap on <c>SUMMON.Count</c> — SPEC-DELTA-v1.1 OQ#4.</summary>
        public const int SummonCountCap = 4;

        /// <summary><c>FOCUS_CURRENT</c>'s symbolic value (compares against MXFOC) — condition C8.</summary>
        public const string FocusMaxToken = "MAX";

        /// <summary>
        /// The authored HARMFUL classification for <c>STATUS_COUNT</c> (condition C9). All 18 members are
        /// <c>eStatusEffectTypes</c> values per EGT §9. SPEC-DELTA-v1.1 §9 risk 5: keep it in ONE place.
        /// </summary>
        public static readonly IReadOnlyList<string> HarmfulStatusTypes = new[]
        {
            "ACID", "BLEED", "CONFUSE", "CURSE", "DAZE", "DEATHMARK", "DEBUFF", "ENTANGLE",
            "FIRE", "ICE", "INFINITE_FIRE", "PETRIFY", "POISON", "RATTLED", "SCARE", "SHOCK",
            "STUN", "WATER"
        };

        /// <summary>Triggers added in v1.1; a recipe declaring SchemaVersion 1.0 may not use these.</summary>
        public static readonly IReadOnlyList<TriggerKind> V11OnlyTriggers = new[]
        {
            TriggerKind.ON_COMBAT_START, TriggerKind.ON_ABILITY_DECLARED, TriggerKind.ON_DAMAGE_DEALT,
            TriggerKind.ON_DAMAGE_TAKEN, TriggerKind.ON_STATUS_APPLIED, TriggerKind.ON_CONSUMABLE_USED,
            TriggerKind.ON_ENEMY_ABILITY_RESOLVED, TriggerKind.ON_HEAL_PENDING
        };

        /// <summary>Conditions added in v1.1.</summary>
        public static readonly IReadOnlyList<ConditionKind> V11OnlyConditions = new[]
        {
            ConditionKind.ROLL_TIER, ConditionKind.HOSTILE_ACTION, ConditionKind.ABILITY_STAT,
            ConditionKind.ABILITY_RANGED, ConditionKind.ABILITY_REPEATED, ConditionKind.CHARACTER_TYPE,
            ConditionKind.FOCUS_SPENT, ConditionKind.FOCUS_CURRENT, ConditionKind.STATUS_COUNT,
            ConditionKind.STATUS_TYPE, ConditionKind.COUNTER, ConditionKind.MOVED_THIS_ROUND,
            ConditionKind.ALL_ALLIES_ACTED, ConditionKind.ITEM_CLASS, ConditionKind.ITEM_CONSUMABLE
        };

        /// <summary>Effects added in v1.1.</summary>
        public static readonly IReadOnlyList<EffectKind> V11OnlyEffects = new[]
        {
            EffectKind.ROLL_STAT_BONUS, EffectKind.HEAL_MODIFIER, EffectKind.COUNTER_ADD, EffectKind.COUNTER_SET
        };

        /// <summary>Targets added in v1.1.</summary>
        public static readonly IReadOnlyList<TargetKind> V11OnlyTargets = new[]
        {
            TargetKind.TRIGGER_SOURCE, TargetKind.ALLY_ALL_OTHERS, TargetKind.ENEMY_ALL, TargetKind.ALLY_BY_RANK
        };

        /// <summary>Triggers added in v1.2 (loot-grant verb spec §6.1). A recipe declaring SchemaVersion
        /// 1.0 or 1.1 may not use these.</summary>
        public static readonly IReadOnlyList<TriggerKind> V12OnlyTriggers = new[]
        {
            TriggerKind.ON_COMBAT_LOOT
        };

        /// <summary>Effects added in v1.2 (loot-grant verb spec §6.2 + Encounter Modifiers spec §5).</summary>
        public static readonly IReadOnlyList<EffectKind> V12OnlyEffects = new[]
        {
            EffectKind.GOLD_GRANT, EffectKind.ITEM_TAG_GRANT, EffectKind.LOOT_SCALE, EffectKind.AFFIX_ROLL,
            EffectKind.SELECTION_SET, EffectKind.EVENT_BANNER
        };

        /// <summary>Conditions added in v1.2 (Encounter Modifiers spec §5, M-EM2).</summary>
        public static readonly IReadOnlyList<ConditionKind> V12OnlyConditions = new[]
        {
            ConditionKind.PARTY_AVG_LEVEL, ConditionKind.IS_DUNGEON, ConditionKind.BOSS_FIGHT,
            ConditionKind.ENCOUNTER_PROPERTY, ConditionKind.ENTITY_TAG, ConditionKind.CONFIG_NAME_CONTAINS,
            ConditionKind.COMBAT_START_REAL, ConditionKind.SELECTION_PRESENT, ConditionKind.IS_ENEMY
        };

        /// <summary>Triggers added in v1.3 (state-hash-chance spec M-SH3).</summary>
        public static readonly IReadOnlyList<TriggerKind> V13OnlyTriggers = new[]
        {
            TriggerKind.ON_DAMAGE_PENDING
        };

        /// <summary>Conditions added in v1.3.</summary>
        public static readonly IReadOnlyList<ConditionKind> V13OnlyConditions = new[]
        {
            ConditionKind.STATE_HASH_CHANCE
        };

        /// <summary>Effects added in v1.3.</summary>
        public static readonly IReadOnlyList<EffectKind> V13OnlyEffects = new[]
        {
            EffectKind.DAMAGE_TAKEN_MULT
        };

        /// <summary>Targets added in v1.4. A recipe declaring any earlier SchemaVersion may not use these.</summary>
        public static readonly IReadOnlyList<TargetKind> V14OnlyTargets = new[]
        {
            TargetKind.RANDOM_TILE
        };

        /// <summary>Effects added in v1.5 (capture spec). A recipe declaring any earlier SchemaVersion may
        /// not use these.</summary>
        public static readonly IReadOnlyList<EffectKind> V15OnlyEffects = new[]
        {
            EffectKind.CAPTURE
        };

        /// <summary>
        /// v1.5 <c>SUMMON.CharacterConfigFrom</c> token prefix: <c>ITEM_CUSTOM_DATA:&lt;ThingConfigId&gt;:&lt;Key&gt;</c>.
        ///
        /// <para><b>Why this token has to exist.</b> <c>SUMMON.CharacterConfig</c> is a STATIC authored
        /// string: the parser reads it as a raw string (RecipeParser.cs) and the validator only checks it is
        /// non-empty (RecipeValidator.cs), so a recipe can only ever summon a creature the AUTHOR named.
        /// A captured monster is chosen by the PLAYER at runtime, so its id cannot be in the JSON at all.
        /// This token resolves the id at EXECUTION time out of an item's <c>Thing.CustomData</c> — the same
        /// store <c>CAPTURE</c> writes and the same one the Trainer partner persistence already uses.</para>
        ///
        /// <para><b>Fail-safe contract</b> (identical to <c>SELF_LEVEL</c>/<c>HAS_ITEM</c>/<c>RANDOM_TILE</c>):
        /// every unresolvable case — no owner, no such item in the owner's <c>Things</c>, no such key, an
        /// empty value, or a value that is not a live <c>Configs.Characters</c> key — is a LOGGED NO-OP that
        /// emits no action and takes ZERO random draws. It never throws and never falls back to a default
        /// creature. A missing config id is load-bearing, not cosmetic: <c>CharacterHelper</c>'s config reads
        /// are raw indexers (<c>Env.Configs.Characters[name]</c>, CharacterHelper.cs:1913) that throw
        /// <c>KeyNotFoundException</c> on a stale id, so the guarded lookup is the whole point.</para>
        ///
        /// <para>The value is a THING CONFIG id and a CustomData KEY — both data, neither compiled in — so a
        /// second capture item needs no engine change (docs/CONVENTIONS.md: the engine hardcodes no
        /// content).</para>
        /// </summary>
        public const string SourceItemCustomDataPrefix = "ITEM_CUSTOM_DATA:";

        /// <summary>
        /// <c>eStatusEffectTypes</c> members the game itself refuses to put on a TILE:
        /// <c>InteractableHelper.CHARACTER_ONLY_STATUS</c> (InteractableHelper.cs:261-269) verbatim.
        /// <c>ApplyStatus</c>'s <c>"CHAOS"</c> branch (InteractableHelper.cs:1229-1240) filters exactly this
        /// set out of <c>CHAOS_STATUS_NAMES</c> when the target has a <c>VenueTileComponent</c>.
        /// <para><b>This is why STUN can never ride RANDOM_TILE.</b> Ben's ban on STUN ("too broken") and
        /// the game's own tile rule agree: <c>STUN</c> is the first member of this list. ClassForge never
        /// draws from <c>CHAOS_STATUS_NAMES</c> — an author names every status explicitly — and both the
        /// validator (statically, via <see cref="TileIllegalStatusPrefixes"/>) and the dispatcher (at plan
        /// time, against the real <c>StatusEffectConfig.Type</c>) refuse these types on a tile.</para>
        /// <para>Note the generic tail of <c>ApplyStatus</c> would happily attach STUN to a tile's
        /// <c>StatusEffectComponent</c> if asked directly — nothing in the game stops it. The refusal has to
        /// live here.</para>
        /// </summary>
        public static readonly IReadOnlyList<string> TileIllegalStatusTypes = new[]
        {
            "STUN", "DAZE", "GRAB", "BLEED", "DEATHMARK", "DEATHSAVE"
        };

        /// <summary>
        /// The <see cref="TileIllegalStatusTypes"/> set rendered as authored-id prefixes, so the pure-C#
        /// validator can reject a tile-illegal status at LOAD time without a game config lookup. Status
        /// config ids follow <c>STATUS_&lt;TYPE&gt;_NN</c> — verified against
        /// <c>CHAOS_STATUS_NAMES</c> (InteractableHelper.cs:302), every member of which is
        /// <c>STATUS_</c> + its own <c>eStatusEffectTypes</c> name + <c>_00</c>.
        /// <para>This is a NAMING heuristic and therefore the belt, not the braces: the authoritative check
        /// is the dispatcher's plan-time read of the real <c>StatusEffectConfig.Type</c>. A status that
        /// breaks the convention is still caught there.</para>
        /// </summary>
        public static readonly IReadOnlyList<string> TileIllegalStatusPrefixes = new[]
        {
            "STATUS_STUN_", "STATUS_DAZE_", "STATUS_GRAB_", "STATUS_BLEED_",
            "STATUS_DEATHMARK_", "STATUS_DEATHSAVE_"
        };

        /// <summary>Conditions added in v1.4 (cover spec).</summary>
        public static readonly IReadOnlyList<ConditionKind> V14OnlyConditions = new[]
        {
            ConditionKind.ALLY_IN_FRONT, ConditionKind.SELF_LEVEL, ConditionKind.HAS_ITEM
        };

        /// <summary>STATE_HASH_CHANCE's closed input-token set (spec §2.1). Adding a token is a spec
        /// change that must argue its replication — the closed set IS the §3.2 correctness contract.</summary>
        public static readonly IReadOnlyList<string> StateHashInputTokens = new[]
        {
            "SELF_GUID", "TRIGGER_SOURCE_GUID", "TRIGGER_TARGET_GUID", "COMBAT_ROUND", "COMBAT_SEED",
            "SELF_HP", "SELF_FOCUS", "TRIGGER_DAMAGE", "TRIGGER_ITEM_ID", "ABILITY_ID",
            "RUN_SEED", "ENCOUNTER_GUID"
        };

        /// <summary>Conditions the <c>Of</c> selector is defined for — SPEC-DELTA-v1.1 §3, extended by
        /// Encounter Modifiers spec §5.</summary>
        public static readonly IReadOnlyList<ConditionKind> OfCapableConditions = new[]
        {
            ConditionKind.HP_THRESHOLD, ConditionKind.HAS_STATUS, ConditionKind.LACKS_STATUS,
            ConditionKind.ROW, ConditionKind.CHARACTER_TYPE, ConditionKind.STATUS_COUNT,
            ConditionKind.FOCUS_CURRENT, ConditionKind.MOVED_THIS_ROUND,
            ConditionKind.ENTITY_TAG, ConditionKind.CONFIG_NAME_CONTAINS, ConditionKind.IS_ENEMY
        };

        /// <summary>Effect kinds that resolve no <c>Target</c> (pure per-battle state writes or [LOCAL]
        /// presentation) — SPEC-DELTA-v1.1 §4.2 E3/E4, Encounter Modifiers spec §5. Used both by the
        /// dispatcher's early-effect switch and by the COMBAT-scope validator's target-rejection rule
        /// (Encounter Modifiers spec §4.2), which must not flag these for the default <c>Target: SELF</c>
        /// they never actually resolve.</summary>
        public static readonly IReadOnlyList<EffectKind> TargetlessEffects = new[]
        {
            EffectKind.COUNTER_ADD, EffectKind.COUNTER_SET, EffectKind.SELECTION_SET, EffectKind.EVENT_BANNER
        };

        /// <summary>Dynamic value-source tokens for <c>FlatValueFrom</c>/<c>PercentFrom</c> — SPEC-DELTA-v1.1 §4.1.</summary>
        public const string SourceFocusSpent = "FOCUS_SPENT";
        public const string SourceCounterPrefix = "COUNTER:";
        public const string SourceStatusCountPrefix = "STATUS_COUNT:";
        public const string SourceTargetHpPct = "TARGET_HP_PCT";

        /// <summary>
        /// <c>FlatValueFrom</c>/<c>PercentFrom</c> token yielding the damage the trigger is carrying.
        ///
        /// This is what makes proportional lifesteal and proportional reflect expressible. Before it,
        /// the damage magnitude was reachable ONLY as a STATE_HASH_CHANCE input, so a "drain 25% of
        /// the damage you dealt" trait had to be written as a flat number that is far too weak early
        /// and far too strong late.
        ///
        /// Raw value is the damage amount; combine with <c>PerUnit</c> to take a share of it, e.g.
        /// <c>{FlatValueFrom: "DAMAGE_DEALT_PCT", Percent: 25, Min: 1}</c>. RNG-free, so it stays
        /// multiplayer-safe, and scoped to damage-carrying triggers by the validator — under any
        /// other trigger there is no damage in scope and it would silently resolve to 0, which is the
        /// ROLL_TIER{EQ FAIL} mistake class this repo has been bitten by before.
        /// </summary>
        public const string SourceDamageDealtPct = "DAMAGE_DEALT_PCT";

        /// <summary>GATE C (Encounter Modifiers spec §5/§8.1): <c>FlatValueFrom</c> token computing a flat
        /// <c>STAT_CHANGE</c> value from <c>TRIGGER_TARGET.MXHP</c> and the effect's authored <c>Percent</c> —
        /// <c>flat = sign(Percent) * max(1, round(|targetMaxHp * Percent| / 100))</c> (EOR's rounding).</summary>
        public const string SourceTargetMxhpPct = "TARGET_MXHP_PCT";

        public static bool TryParseTrigger(string token, out TriggerKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        public static bool TryParseCondition(string token, out ConditionKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        public static bool TryParseEffect(string token, out EffectKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        public static bool TryParseTarget(string token, out TargetKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        /// <summary>
        /// Strict token→enum parse. Deliberately NOT <c>Enum.Parse(ignoreCase:true)</c> with numeric
        /// fallback: <c>Enum.TryParse</c> accepts "3" for any enum, which would silently admit garbage
        /// tokens. Unknown tokens must produce a Finding and disable the recipe (fail-safe).
        /// </summary>
        private static bool TryParseEnum<T>(string token, out T value) where T : struct
        {
            value = default(T);
            if (string.IsNullOrEmpty(token)) return false;
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, token, StringComparison.Ordinal))
                {
                    value = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }
            return false;
        }

        public static bool Compare(Comparator cmp, int actual, int expected)
        {
            switch (cmp)
            {
                case Comparator.EQ: return actual == expected;
                case Comparator.NE: return actual != expected;
                case Comparator.LT: return actual < expected;
                case Comparator.LTE: return actual <= expected;
                case Comparator.GT: return actual > expected;
                default: return actual >= expected;
            }
        }
    }
}
