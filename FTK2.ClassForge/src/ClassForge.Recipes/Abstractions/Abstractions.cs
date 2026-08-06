using System.Collections.Generic;

namespace ClassForge.Recipes.Abstractions
{
    /// <summary>Row bucket, per SPEC §4.6 <c>ROW</c> condition (VenueTileComponent.RowPositionsType).</summary>
    public enum EntityRow
    {
        UNKNOWN = 0,
        FRONT = 1,
        BACK = 2
    }

    /// <summary>
    /// Everything a recipe may read about a combatant. Deliberately narrow: the Plugin unit adapts
    /// a game <c>Entity</c> onto this; the engine never sees a game type.
    /// <para>Every member must be a read of replicated state (SPEC-DELTA-v1.1 §3: "All conditions are
    /// pure reads of replicated state or of hook parameters").</para>
    /// </summary>
    public interface ICombatEntity
    {
        /// <summary>Stable replicated identity. Adapter: <c>Entity.Guid</c>. Ordinal sort of this value
        /// is the universal tiebreak in SPEC-DELTA-v1.1 §5.2 invariant 4.</summary>
        string Guid { get; }

        /// <summary>Adapter: <c>!CharacterHelper.IsDead(entity)</c>.</summary>
        bool IsAlive { get; }

        /// <summary>Selects <c>AiProcChance</c> over <c>ProcChance</c> (SPEC §4.6).</summary>
        bool IsAiControlled { get; }

        /// <summary>
        /// Adapter: <c>CharacterHelper.GetStat(entity, statKey, true)</c> — PSN §3 L396, the single
        /// overload the engine standardizes on (PSN "Notable surprises" #6 flags the 8-overload trap).
        /// Keys are <c>eCharacterStats</c> members: HP, MXHP, FOC, MXFOC, ATK, CRT, SPD, ...
        /// </summary>
        int GetStat(string statKey);

        /// <summary>Adapter: status config names on <c>StatusEffectComponent.Statuses</c>.</summary>
        IReadOnlyList<string> Statuses { get; }

        /// <summary>Adapter: <c>VenueComponent.TilePosition</c> → <c>RowPositionsType</c>.</summary>
        EntityRow Row { get; }

        /// <summary>Adapter: equipped weapon's <c>ThingConfig.Class</c> (EGT §5 free-string space).</summary>
        string WeaponClass { get; }

        /// <summary>Adapter: <c>CharacterComponent.CharacterType</c> (<c>eCharacterTypes</c>, EGT §11).</summary>
        string CharacterType { get; }

        /// <summary>Adapter: the character config's <c>BaseType</c> (v1 <c>TARGET_BASE_TYPE</c> condition).</summary>
        string BaseType { get; }

        /// <summary>
        /// Skill ids this entity carries (adapter: <c>Equippable.Passives</c> across equipped Things,
        /// including <c>TRAIT_*</c> Things per SPEC-DELTA-v1.1 OQ#1, plus status-attached <c>SKILL_</c>
        /// passives per Encounter Modifiers spec §4.4). A recipe only evaluates for owners that hold its
        /// <c>SKILL_*</c> id.
        /// </summary>
        IReadOnlyList<string> Passives { get; }

        /// <summary>GATE D (Encounter Modifiers spec, binding gate resolution): adapter —
        /// <c>CharacterHelper.IsEnemy(entity)</c>, i.e. <c>CharacterComponent.GroupIndex == 1</c>. The
        /// enemy-side gate for <c>IS_ENEMY</c> — proven unable to be expressed via <c>CHARACTER_TYPE</c>.</summary>
        bool IsEnemy { get; }

        /// <summary>Encounter Modifiers spec §5 <c>ENTITY_TAG</c>: adapter —
        /// <c>CharacterHelper.ActorHasTag(entity, eConfigTags.&lt;tagName&gt;)</c>, parsing the enum member
        /// name from <paramref name="tagName"/>. An unparseable tag name is a fail-safe false, never a throw.</summary>
        bool HasTag(string tagName);

        /// <summary>Encounter Modifiers spec §5 <c>CONFIG_NAME_CONTAINS</c>: adapter —
        /// <c>CharacterComponent.ConfigName</c> (the character's config id, e.g. "SCOURGE_TOTEM").</summary>
        string ConfigName { get; }
    }

    /// <summary>Ability facts a condition may read. Adapter: <c>Configs.Abilities[id]</c> + the acting Thing's config.</summary>
    public interface IAbilityInfo
    {
        string Id { get; }
        /// <summary><c>CombatAbilityConfig.IsRanged</c> (condition C4).</summary>
        bool IsRanged { get; }
        /// <summary><c>Configs.Abilities[id].Target == eTargets.ENEMY</c> (condition C2).</summary>
        bool TargetsEnemy { get; }
        /// <summary><c>Interactable.Abilities[id].Stat</c> — an <c>eCharacterStats</c> member (condition C3).</summary>
        string Stat { get; }
        /// <summary>Authored <c>Tags[]</c> (v1 <c>ABILITY_TAG</c> condition).</summary>
        IReadOnlyList<string> Tags { get; }
    }

    /// <summary>Status config facts. Adapter: <c>Configs.StatusEffects[id]</c>.</summary>
    public interface IStatusInfo
    {
        string Id { get; }
        /// <summary><c>StatusEffectConfig.Type</c> — an <c>eStatusEffectTypes</c> member (EGT §9).</summary>
        string Type { get; }
    }

    /// <summary>Thing config facts. Adapter: <c>InventoryHelper.GetThingConfig(name)</c> — PSN §4 L806.</summary>
    public interface IItemInfo
    {
        string ConfigName { get; }
        /// <summary><c>ThingConfig.Class</c> (condition C14).</summary>
        string Class { get; }
        /// <summary><c>ConsumableType != NONE</c> (condition C15).</summary>
        bool IsConsumable { get; }
    }

    /// <summary>
    /// The combat the dispatcher is reasoning about.
    /// <para><see cref="CombatIdentity"/> is the engine-side stand-in for SPEC-DELTA-v1.1 §6's
    /// <c>CombatKey := (ReferenceEquals-identity of CombatState, CombatState.Random.Seed)</c>. When it
    /// changes, the whole per-battle runtime is dropped (see <c>RecipeStateStore</c>).</para>
    /// </summary>
    public interface ICombatContext
    {
        /// <summary>Opaque per-combat token. Adapter builds it from the CombatState identity + Random.Seed.</summary>
        string CombatIdentity { get; }

        /// <summary>Round counter. Adapter: incremented by the <c>CombatHelper.NextTurn</c> postfix when
        /// <c>pIsNewRound == true</c> (PSN §1 L793) — SPEC-DELTA-v1.1 §6 names this the source of truth.</summary>
        int Round { get; }

        /// <summary>Every entity in the combat. Iteration order here is NOT trusted; the engine always
        /// re-sorts by <see cref="ICombatEntity.Guid"/> ordinal (§5.2 invariant 4).</summary>
        IReadOnlyList<ICombatEntity> Entities { get; }

        /// <summary>Adapter: <c>CharacterHelper.IsOpponent(a, b)</c> / party membership.</summary>
        bool AreOpponents(ICombatEntity a, ICombatEntity b);

        /// <summary>Null when the id is unknown — conditions that need it then evaluate false (fail-safe).</summary>
        IAbilityInfo GetAbility(string abilityId);

        /// <summary>Null when the id is unknown.</summary>
        IStatusInfo GetStatus(string statusId);

        /// <summary>Null when the config name is unknown.</summary>
        IItemInfo GetItem(string thingConfigName);

        /// <summary>Encounter Modifiers spec §5 <c>PARTY_AVG_LEVEL</c>: adapter —
        /// <c>ProgressionHelper.GetAveragePartyLevel</c> over <c>GameRun.Entities</c> filtered
        /// <c>Has&lt;PlayerComponent&gt;() &amp;&amp; Has&lt;CharacterComponent&gt;()</c> (mirrors EOR's
        /// caller-side filter, EOR L22744).</summary>
        int PartyAverageLevel { get; }

        /// <summary>Encounter Modifiers spec §5 <c>IS_DUNGEON</c>: adapter — <c>CombatState.IsDungeon</c>.</summary>
        bool IsDungeon { get; }

        /// <summary>Encounter Modifiers spec §5 <c>BOSS_FIGHT</c>: adapter — <c>CombatState.BossFightState != null</c>.</summary>
        bool IsBossFight { get; }

        /// <summary>Encounter Modifiers spec §5 <c>ENCOUNTER_PROPERTY</c>: adapter — resolves the encounter
        /// entity via <c>GameRun.AdventureState.EncounterGUID</c>, then <c>EncounterComponent.HasProperty</c>,
        /// parsing the enum member name from <paramref name="propertyName"/>. No encounter entity resolved
        /// ⇒ false (matches EOR's default, L22772-3).</summary>
        bool HasEncounterProperty(string propertyName);

        /// <summary>
        /// Action sink. The dispatcher both returns the ordered plan AND pushes each action here, so the
        /// Plugin can translate to <c>CombatHelper.ApplyAction</c> calls streaming rather than in a batch
        /// (SPEC-DELTA-v1.1 §4 "Effect emission rule").
        /// </summary>
        void EmitAction(Runtime.EngineAction action);
    }

    /// <summary>
    /// The shared combat RNG, mirrored from <c>GameRandom</c> (PSN §10).
    /// <para>SPEC §9.3b + SPEC-DELTA-v1.1 §5.2 invariant 1: every roll MUST come from
    /// <c>Env.GameRun.CombatState.Random</c>. <c>System.Random</c>, <c>UnityEngine.Random</c> and freshly
    /// constructed <c>GameRandom</c> instances are forbidden. Passing <c>null</c> here means "no active
    /// combat" and, per invariant 2, the recipe does not fire — it never falls back to an ad-hoc stream.</para>
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>Adapter: <c>GameRandom.NextChance(decimal)</c>. One draw. <paramref name="chance"/> is 0..1.</summary>
        bool NextChance(decimal chance);

        /// <summary>Adapter: <c>GameRandom.NextInt(min, max)</c> with <c>pMaxInclusive:false</c>. One draw.
        /// Used for <c>StatusOneOf</c>, which is exactly what <c>GetRandomElementFromList</c> does internally.</summary>
        int NextInt(int minInclusive, int maxExclusive);

        /// <summary>Adapter: <c>GameRandom.NextInt(min, max, pMaxInclusive: true)</c>. One draw. Used by
        /// <c>SELECTION_SET</c> (Encounter Modifiers spec §5/§6.3): <c>NextInt(1, Σweights, pMaxInclusive:
        /// true)</c>, EOR's weighted-pick draw verbatim (L22814).</summary>
        int NextIntInclusive(int minInclusive, int maxInclusive);
    }

    /// <summary>Optional diagnostic sink; the engine never requires one (all calls are null-guarded).</summary>
    public interface IRecipeLog
    {
        void Info(string message);
        void Warn(string message);
    }
}
