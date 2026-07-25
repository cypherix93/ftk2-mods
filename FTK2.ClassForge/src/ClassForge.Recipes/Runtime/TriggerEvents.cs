using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Model;

namespace ClassForge.Recipes.Runtime
{
    // One event struct per trigger, each carrying EXACTLY the payload SPEC-DELTA-v1.1 §2 says the
    // corresponding hook makes available. If a datum is not on this struct, the delta does not have a
    // verified parameter carrying it, and no condition may read it.

    /// <summary>
    /// <c>ON_COMBAT_START</c> (T1) — <c>CombatHelper.SetInitiative</c> <b>Postfix</b> (PSN §1 L43).
    /// Owner = <c>pEntity</c>. Needed by PREPARED, OF_FOCUS.
    /// </summary>
    public struct CombatStartEvent
    {
        /// <summary><c>pEntity</c> — the recipe owner.</summary>
        public ICombatEntity Entity;
    }

    /// <summary>
    /// <c>ON_ABILITY_DECLARED</c> (T2) — <c>CombatHelper.PerformAbility</c> <b>Prefix</b> (PSN §1 L1155).
    /// Owner = <c>pOrigin</c>. The <c>ROLL_STAT_BONUS</c> insertion point; <c>pRollData</c> is already resolved
    /// and passed in. Needed by ASSASSIN, CORSAIR, PEASANT, RANGER, SCOUT, TEMPLAR, WARRIOR, WIZARD, STEADY_AIM.
    /// </summary>
    public struct AbilityDeclaredEvent
    {
        /// <summary><c>pOrigin</c>.</summary>
        public ICombatEntity Origin;
        /// <summary><c>pTarget</c>.</summary>
        public ICombatEntity Target;
        /// <summary>Ability id resolved from <c>pThing</c> + <c>pCombatDecision</c>.</summary>
        public string AbilityId;
        /// <summary><c>pRollData.Status</c> (<c>eRollStatus</c>) — already resolved at prefix time.</summary>
        public RollTier RollTier;
        /// <summary><c>pCombatDecision.FocusUsed</c> (PSN §6 L1357).</summary>
        public int FocusUsed;
    }

    /// <summary>
    /// <c>ON_ABILITY_USED</c> (v1) — <c>CombatHelper.PerformAbility</c> <b>Postfix</b> (PSN §1 L1155).
    /// Owner = <c>pOrigin</c>.
    /// </summary>
    public struct AbilityUsedEvent
    {
        public ICombatEntity Origin;
        public ICombatEntity Target;
        public string AbilityId;
        public RollTier RollTier;
        public int FocusUsed;
    }

    /// <summary>
    /// <c>ON_ENEMY_ABILITY_RESOLVED</c> (T7) — <c>CombatHelper.PerformAbility</c> <b>Postfix</b>.
    /// Owner = <b>each living recipe-holder opposed to <c>pOrigin</c></b>, iterated in ascending ordinal
    /// <c>Entity.Guid</c> order (§2 T7 determinism note). Acting enemy = <c>pOrigin</c> → <c>TRIGGER_SOURCE</c>.
    /// Needed by ORACLE, JESTER.
    /// </summary>
    public struct EnemyAbilityResolvedEvent
    {
        /// <summary><c>pOrigin</c> — the acting enemy, bound to <c>TRIGGER_SOURCE</c>.</summary>
        public ICombatEntity Origin;
        public ICombatEntity Target;
        public string AbilityId;
        /// <summary><c>pRollData.Status</c> → <c>ROLL_TIER</c>.</summary>
        public RollTier RollTier;
    }

    /// <summary>
    /// <c>ON_CRIT</c> (v1, OQ#2 resolved) — <c>InteractableHelper.ApplyStatChange</c> <b>Postfix</b>
    /// (PSN §2 L626). Fires when <c>pIsCrit == true &amp;&amp; pStatAction.Stat == "HP"</c> and the delta was
    /// damage. Owner = <c>pOriginEntity</c>. No knob gate — the <c>bool pIsCrit</c> parameter is explicit.
    /// </summary>
    public struct CritEvent
    {
        /// <summary><c>pOriginEntity</c>.</summary>
        public ICombatEntity Origin;
        /// <summary><c>pTargetEntity</c>.</summary>
        public ICombatEntity Target;
        /// <summary><c>pAbilityName</c>.</summary>
        public string AbilityId;
        /// <summary><c>pStatAction.Stat</c>.</summary>
        public string Stat;
    }

    /// <summary>
    /// <c>ON_KILL</c> (v1, re-anchored) — <c>InteractableHelper.ApplyStatChange</c>
    /// <b>Prefix (capture) + Postfix</b>. Prefix stores <c>pTargetEntity</c> HP into <c>__state</c>; the
    /// postfix fires when <c>HpBefore &gt; 0 &amp;&amp; HpAfter &lt;= 0</c>. Owner = <c>pOriginEntity</c> —
    /// the kill is origin-attributed (unlike EOR's unfiltered <c>HasKilledTarget</c>).
    /// </summary>
    public struct KillEvent
    {
        public ICombatEntity Origin;
        public ICombatEntity Target;
        public string AbilityId;
        public int HpBefore;
        public int HpAfter;
    }

    /// <summary>
    /// <c>ON_DAMAGE_DEALT</c> (T3) — <c>InteractableHelper.ApplyStatChange</c> Prefix(capture)+Postfix.
    /// Owner = <c>pOriginEntity</c>, target ∉ <c>pParty</c>. Needed by BATTLE_RHYTHM.
    /// </summary>
    public struct DamageDealtEvent
    {
        public ICombatEntity Origin;
        public ICombatEntity Target;
        public string AbilityId;
        public int Amount;
        public int HpBefore;
        public int HpAfter;
    }

    /// <summary>
    /// <c>ON_DAMAGE_TAKEN</c> (T4, and the target of the deprecated <c>ON_DAMAGED</c> alias) — same hook.
    /// Owner = <b><c>pTargetEntity</c></b>; the attacker (<c>pOriginEntity</c>) binds to <c>TRIGGER_SOURCE</c>.
    /// <c>pAbilityName</c> resolves <c>Configs.Abilities[...]</c> for <c>ABILITY_RANGED</c>.
    /// Needed by DRUID, SENTINEL, WARRIOR.
    /// </summary>
    public struct DamageTakenEvent
    {
        /// <summary><c>pOriginEntity</c> — the attacker, bound to <c>TRIGGER_SOURCE</c>.</summary>
        public ICombatEntity Attacker;
        /// <summary><c>pTargetEntity</c> — the recipe owner.</summary>
        public ICombatEntity Victim;
        public string AbilityId;
        public int Amount;
    }

    /// <summary>
    /// <c>ON_HEAL</c> (v1, re-anchored) — <c>InteractableHelper.ApplyStatChange</c> <b>Postfix</b> where
    /// <c>pStatAction.Stat == "HP"</c> and the delta is a heal. Owner = <c>pOriginEntity</c> (the healer);
    /// the healed entity binds to <c>TRIGGER_TARGET</c>.
    /// </summary>
    public struct HealEvent
    {
        public ICombatEntity Healer;
        public ICombatEntity Healed;
        public string AbilityId;
        public int Amount;
    }

    /// <summary>
    /// <c>ON_HEAL_PENDING</c> (T8) — <c>CharacterHelper.AddHealth(Entity, ref int pValue, ...)</c>
    /// <b>Prefix</b> (PSN §3 L1342/L1357). Owner = <c>pEntity</c>, the heal recipient. <c>ref int pValue</c>
    /// is the mutable heal amount and the ONLY <c>HEAL_MODIFIER</c> insertion point; healer identity comes
    /// from the paired <c>ApplyStatChange</c> prefix. <b>No RNG on this path.</b>
    /// Needed by FIELDMEDIC, MENDERS_TOUCH.
    /// </summary>
    public struct HealPendingEvent
    {
        /// <summary><c>pEntity</c> — the recipient, and the owner for <c>Scope: RECEIVED</c>.</summary>
        public ICombatEntity Recipient;
        /// <summary>Healer captured by the paired <c>ApplyStatChange</c> prefix; owner for <c>Scope: GIVEN</c>.</summary>
        public ICombatEntity Healer;
        /// <summary>The current value of <c>ref int pValue</c>.</summary>
        public int PendingAmount;
        /// <summary><c>_activeHealingThing</c> config name, when the heal came from an item.</summary>
        public string ItemConfigName;
    }

    /// <summary>
    /// <c>ON_STATUS_APPLIED</c> (T5) — <c>InteractableHelper.ApplyStatus</c> single-target overload
    /// <b>Postfix</b> (PSN §2 L1219). Owner = <c>pTargetEntity</c>. <c>pStatusConfigName</c> binds to the
    /// <c>TRIGGER_STATUS</c> token. Needed by WARDBOUND, OF_STABILITY.
    /// </summary>
    public struct StatusAppliedEvent
    {
        /// <summary><c>pTargetEntity</c> — the recipe owner.</summary>
        public ICombatEntity Target;
        /// <summary><c>pOriginEntity</c> — bound to <c>TRIGGER_SOURCE</c>.</summary>
        public ICombatEntity Applier;
        /// <summary><c>pStatusConfigName</c> — the <c>TRIGGER_STATUS</c> token's value.</summary>
        public string StatusId;
    }

    /// <summary>
    /// <c>ON_CONSUMABLE_USED</c> (T6) — <c>InteractableHelper.PerformConsumableAbility</c> <b>Postfix</b>
    /// (PSN §2 L538). Owner = <c>pOriginEntity</c>. <c>pThing</c> feeds <c>ITEM_CLASS</c>/<c>ITEM_CONSUMABLE</c>
    /// via <c>InventoryHelper.GetThingConfig</c> (PSN §4 L806). Needed by DRUNKEN_COURAGE.
    /// </summary>
    public struct ConsumableUsedEvent
    {
        public ICombatEntity Origin;
        public ICombatEntity Target;
        /// <summary><c>pThing.ConfigName</c>.</summary>
        public string ItemConfigName;
        /// <summary><c>pAbilityName</c>.</summary>
        public string AbilityId;
    }

    /// <summary>
    /// <c>ON_TURN_START</c> / <c>ON_TURN_END</c> (v1) — <c>CombatHelper._onCombatSkillProc</c> <b>Postfix</b>
    /// (PSN §1 L1818, <b>private</b> — SPEC-DELTA-v1.1 §9 risk 3). Owner = <c>pCharacter</c>.
    /// Maps the native <c>EVENT_PROC</c> <c>START_TURN</c>/<c>END_TURN</c> vocabulary.
    /// </summary>
    public struct TurnEvent
    {
        public ICombatEntity Entity;
    }
}
