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

    /// <summary>Shared sentinels for entity reads that cannot be resolved.</summary>
    public static class EntityReads
    {
        /// <summary>"level unknown" — see <see cref="ICombatEntity.Level"/>. Deliberately
        /// <see cref="int.MinValue"/> so it can never be confused with a real level (0..10) and so a
        /// naive comparison against any authored Value is false for GT/GTE/EQ.</summary>
        public const int UnknownLevel = int.MinValue;

        // The inventory read's "unknown" is the third value of a bool? — see ICombatEntity.HasItem.
        // It needs no constant here (a null bool? IS the sentinel), but it obeys the same contract as
        // UnknownLevel: callers fail safe on it, BEFORE Negate.
    }

    /// <summary>
    /// Everything a recipe may read about a combatant. Deliberately narrow: the Plugin unit adapts
    /// a game <c>Entity</c> onto this; the engine never sees a game type.
    /// <para>Every member must be a read of replicated state (SPEC-DELTA-v1.1 §3: "All conditions are
    /// pure reads of replicated state or of hook parameters").</para>
    /// </summary>
    public interface ICombatEntity
    {
        /// <summary>
        /// <b>PEER-LOCAL</b> handle. Adapter: <c>Entity.Guid</c>, which <c>Entity.Create()</c> mints with
        /// <c>System.Guid.NewGuid()</c> — so the SAME logical combatant carries a DIFFERENT value on every
        /// peer in a co-op session.
        /// <para><b>Legal uses (all same-peer round trips):</b> a key into a dictionary this peer both
        /// writes and reads (turn state, cooldown/budget ledgers), and the <c>TargetGuid</c> on a planned
        /// <c>EngineAction</c>, which the executor resolves back through
        /// <c>ICombatContext</c> on the same peer that planned it.</para>
        /// <para><b>Illegal uses — every one of these was a live desync and has been migrated to
        /// <see cref="RosterOrdinal"/>:</b> sorting or tie-breaking (a different order on each peer picks a
        /// different target), seeding any stream, feeding any hash whose value is compared across peers or
        /// persisted, and writing into <c>Thing.CustomData</c> (which is inside the vendor's desync MD5).
        /// SPEC-DELTA-v1.1 §5.2 invariant 4 used to name ordinal Guid sort as "the universal tiebreak";
        /// that clause is WITHDRAWN and replaced by <see cref="RosterOrdinal"/>.</para>
        /// </summary>
        string Guid { get; }

        /// <summary>
        /// The cross-peer stable identity: this combatant's index in the replicated combat roster
        /// (<c>CombatState.Entities</c>). Every peer builds and appends to that list in the same sequence
        /// off the same shared-seed stream, so the index agrees everywhere at a given lockstep point —
        /// which is exactly the property <see cref="Guid"/> does not have. See
        /// <c>ClassForge.Core.Rng.EntityKey</c> for the full argument, including why grid position,
        /// <c>ConfigName</c> and <c>GroupIndex</c> were all rejected as key material.
        /// <para><b>Unknown sentinel:</b> <see cref="int.MaxValue"/> when the roster index cannot be
        /// resolved (no combat state, entity not on the roster, a throwing read). It sorts LAST rather than
        /// aliasing slot 0, and callers must not do arithmetic on it. It is deliberately NOT -1: an
        /// unfound <c>List.IndexOf</c> result must not collide with a legitimately negative ordinal in any
        /// future caller.</para>
        /// <para><b>Peer-stable, not time-stable.</b> One entity's ordinal shifts when a different entity
        /// dies, is revived or is summoned. That is fine and is the contract: what must hold is that both
        /// peers compute the SAME ordinal at the SAME lockstep point.</para>
        /// </summary>
        int RosterOrdinal { get; }

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

        /// <summary>Board column. Adapter: <c>VenueComponent.TilePosition.x</c>. Returns
        /// <see cref="int.MinValue"/> when the tile cannot be resolved (no VenueComponent, no combat
        /// state, or a throwing read) — callers MUST treat that sentinel as "unknown position" and
        /// fail safe rather than doing arithmetic on it.</summary>
        int TileX { get; }

        /// <summary>Board row-line. Adapter: <c>VenueComponent.TilePosition.y</c>. Returns
        /// <see cref="int.MinValue"/> when the tile cannot be resolved, exactly as
        /// <see cref="TileX"/> does.</summary>
        int TileY { get; }

        /// <summary>
        /// THIS character's own progression level — the per-entity value <c>eCharacterStats</c> does not
        /// carry and <c>ICombatContext.PartyAverageLevel</c> only averages away.
        /// <para>Adapter: <c>ProgressionHelper.GetEntityLevel(entity)</c> (ProgressionHelper.cs:565), which
        /// derives the level from the entity's "XP" inventory Thing
        /// (<c>GetEntityXP</c>, ProgressionHelper.cs:555 — <c>CharacterComponent.Things.Find(t =&gt;
        /// t.ConfigName == "XP")?.StackCount ?? 0</c>) against <c>PLAYER_XP_LEVELS</c> /
        /// <c>COMPANION_XP_LEVELS</c>.</para>
        /// <para><b>Unknown sentinel.</b> Returns <see cref="EntityReads.UnknownLevel"/> (<see cref="int.MinValue"/>,
        /// the same "unresolvable" convention <see cref="TileX"/>/<see cref="TileY"/> use) whenever a real
        /// per-entity level cannot be read: no <c>CharacterComponent</c>, a throwing read, or an entity
        /// with no XP progression at all (enemies carry no "XP" Thing — their level lives in the static
        /// <c>CharacterConfig.Level</c>, which is NOT this value). Callers MUST fail safe on the sentinel;
        /// <c>SELF_LEVEL</c> evaluates to false on it, before Negate.</para>
        /// </summary>
        int Level { get; }

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

        /// <summary>
        /// v1.4 <c>HAS_ITEM</c>: does this character CARRY the Thing whose <c>ThingConfig</c> id is
        /// <paramref name="thingConfigName"/>? Adapter: an ORDINAL scan of
        /// <c>CharacterComponent.Things</c> for <c>t.ConfigName == thingConfigName</c> — the same read
        /// <c>InventoryHelper.HasItemByName</c> performs (InventoryHelper.cs:424-427:
        /// <c>pInventory.Any(t =&gt; t.ConfigName == pThingConfigName &amp;&amp; t.ParentId == pParentId)</c>)
        /// and the same one <c>ProgressionHelper.GetEntityXP</c> uses for "XP" (ProgressionHelper.cs:557).
        /// <para><b>Possession, not equipment.</b> This is inventory possession and deliberately NOT an
        /// equipped check. <c>CharacterComponent.Equipped</c> is a
        /// <c>Dictionary&lt;eEquipmentSlots, string&gt;</c> of Thing <b>Ids</b> that
        /// <c>EquipmentHelper.GetEquippedThingBySlot</c> (EquipmentHelper.cs:165-177) resolves back out of
        /// <c>Things</c> — so an equipped Thing is still in <c>Things</c> and possession is the superset.
        /// Toolbelt items (the <c>TOOLBELT</c> config tag, InventoryHelper.cs:975) have no slot in
        /// <c>eEquipmentSlots</c> at all and therefore can NEVER be equipped; an equipped-only variant of
        /// this read would be permanently false for them, which is why no such option is exposed.</para>
        /// <para><b>Nesting.</b> The adapter must NOT filter on <c>Thing.ParentId</c> the way
        /// <c>HasItemByName</c>'s default argument does (<c>ParentId == null</c>, i.e. top level only):
        /// possession is possession wherever the Thing sits in the container tree.</para>
        /// <para><b>Distinct from <c>ITEM_CLASS</c>/<c>ITEM_CONSUMABLE</c>.</b> Those read the item the
        /// TRIGGER carries (<c>TriggerContext.ItemConfigName</c> → <see cref="IItemInfo"/>, i.e. the Thing
        /// whose ability just fired) and are false under any trigger with no <c>pThing</c>. This reads the
        /// entity's inventory and is answerable under every trigger, including <c>ON_COMBAT_START</c>.</para>
        /// <para><b>Unknown tri-state.</b> Returns <c>null</c> — the <c>bool?</c> counterpart of
        /// <see cref="EntityReads.UnknownLevel"/> — whenever the inventory cannot be read AT ALL: no
        /// <c>CharacterComponent</c>, a null/empty <c>Things</c> list reference, or a throwing read.
        /// <c>false</c> means the inventory WAS read and genuinely lacks the item. The two must stay
        /// distinguishable: <c>HAS_ITEM</c> evaluates to false on <c>null</c> <b>before</b> Negate (an
        /// unreadable inventory must never open a gate), while a genuine <c>false</c> is negatable.</para>
        /// <para><b>Case.</b> ORDINAL, case-SENSITIVE — Thing config ids are exact keys into
        /// <c>Env.Configs.Things</c> (Thing.cs:40). "arm_orig_trainer_ball_water" is not the charm.</para>
        /// </summary>
        bool? HasItem(string thingConfigName);
    }

    /// <summary>
    /// A BOARD TILE — the second kind of thing a combat effect can be aimed at, and the one the engine
    /// had no vocabulary for before v1.4.
    /// <para>Tiles are real ECS entities, produced by <c>VenueHelper.CreateVenueTileEntities(string[] pMap)</c>
    /// (VenueHelper.cs:69) — one per CHARACTER of the static venue map string — and registered into the
    /// combat by <c>CombatPhase.cs:314</c> (<c>_combatState.Entities.AddRange(_gameObjectMaps.FromTile.Keys)</c>).
    /// A tile entity carries <c>VenueTileComponent</c> (<c>GroupIndex</c>, <c>RowPositionsType</c>,
    /// <c>AuraStatuses</c> — no coordinates) AND <c>VenueComponent</c>, which is where its
    /// <c>(int x, int y) TilePosition</c> actually lives, exactly as on a character.</para>
    /// <para>The game itself puts statuses on tiles: <c>CombatPhase.cs:2013</c> (rain) calls
    /// <c>InteractableHelper.ApplyStatus(null, tileEntity, null, "", "STATUS_WATER_00", _combatState.Random, null)</c>
    /// and <c>CombatPhase.cs:2081</c> (poison hexes) the same with <c>STATUS_ACID_00</c>. That overload
    /// (InteractableHelper.cs:1222) branches on <c>pTargetEntity.TryGet&lt;VenueTileComponent&gt;(...)</c> and
    /// otherwise attaches the status to the tile's own <c>StatusEffectComponent</c> generically — a null
    /// <c>pOriginEntity</c> is legal and is what the game passes for environmental tile statuses.</para>
    /// <para><b>Why coordinates and not a guid.</b> <see cref="X"/>/<see cref="Y"/> are the tile's index into
    /// the venue map string and are therefore IDENTICAL on every peer by construction — the map is a static
    /// <c>string[]</c> compiled into the game (<c>VenueHelper.VenueMap1</c> and friends). An entity Guid is a
    /// LOCAL object identity, and the enumeration order of <c>VenueGameObjectMaps.FromTile</c> (a
    /// <c>Dictionary</c>) is not a cross-peer contract either. The engine therefore draws an INDEX into a
    /// (Y,X)-ordered tile list; each peer maps that same index onto the same board square and resolves it to
    /// its own local tile entity. See <see cref="ICombatContext.Tiles"/>.</para>
    /// </summary>
    public interface ICombatTile
    {
        /// <summary>Board column — <c>VenueComponent.TilePosition.x</c> on the tile entity, i.e. the
        /// character index within a venue-map row. Peer-identical.</summary>
        int X { get; }

        /// <summary>Board row-line — <c>VenueComponent.TilePosition.y</c>, i.e. the venue-map row index.
        /// Peer-identical.</summary>
        int Y { get; }

        /// <summary>
        /// LOCAL entity identity for this tile, used only so the Plugin executor can hand the planned action
        /// back to <c>InteractableHelper.ApplyStatus</c>. NEVER hashed, compared across peers, or rendered
        /// into <c>EngineAction.Describe()</c> — <see cref="X"/>/<see cref="Y"/> are the replication-safe
        /// identity and are what the determinism log prints.
        /// </summary>
        string LocalGuid { get; }
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

        /// <summary>
        /// Every entity in the combat, in <b>replicated combat-roster order</b> — i.e. index <c>i</c> here
        /// is the entity whose <see cref="ICombatEntity.RosterOrdinal"/> is <c>i</c>.
        /// <para>This ordering IS the cross-peer contract and callers may rely on it. It was previously
        /// documented as "not trusted, the engine re-sorts by Guid ordinal"; that re-sort was a desync (a
        /// per-peer <c>Guid.NewGuid()</c> gave each peer a different order, so any tie-broken pick chose a
        /// different combatant on each peer). Both the adapter's materialisation order and every engine-side
        /// re-sort are now keyed on <see cref="ICombatEntity.RosterOrdinal"/>.</para>
        /// </summary>
        IReadOnlyList<ICombatEntity> Entities { get; }

        /// <summary>
        /// v1.4 <c>RANDOM_TILE</c>: every board tile of the current venue grid, ordered ascending by
        /// <c>(Y, X)</c> — the map-string reading order, which is a pure function of the static venue map
        /// and therefore byte-identical on every peer. That ordering IS the replication contract: the
        /// engine picks an INDEX with one <c>IRandomSource.NextInt(0, Tiles.Count)</c> draw off the shared
        /// combat stream, and every peer maps that index onto the same board square.
        /// <para>Adapter: <c>CombatState.Entities.FindAll(e =&gt; e.Has&lt;VenueTileComponent&gt;() &amp;&amp;
        /// e.Get&lt;VenueTileComponent&gt;().GroupIndex &gt; -1)</c> — the game's OWN playable-tile pool,
        /// copied verbatim from the rain/chaos weather ticks at <c>CombatPhase.cs:2009</c> and
        /// <c>CombatPhase.cs:2021</c>. Border/floor cells (the map's <c>+ - | .</c> characters) are given
        /// <c>GroupIndex = -1</c> by <c>CreateVenueTileEntities</c> and are excluded by that same predicate:
        /// they are not part of the board.</para>
        /// <para><b>Fail-safe:</b> EMPTY (never null, never a throw) whenever the tile set cannot be
        /// resolved — no combat, no venue director, or a throwing read. An empty list makes every
        /// <c>RANDOM_TILE</c> effect a logged no-op that takes ZERO draws, so a peer that cannot see the
        /// board can never desync the shared stream by drawing anyway.</para>
        /// </summary>
        IReadOnlyList<ICombatTile> Tiles { get; }

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

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>COMBAT_SEED</c>: adapter — <c>CombatState.Random.Seed</c>
        /// (<c>public readonly int</c>, PSN §10, identical on every peer). Deliberately NOT
        /// <see cref="CombatIdentity"/>, whose ReferenceEquals-identity half is process-local and must never
        /// feed a cross-peer hash. 0 outside combat.</summary>
        int CombatSeed { get; }

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>RUN_SEED</c>: adapter — <c>GameRunData.MapGenSeed</c>.
        /// 0 when no run.</summary>
        int RunSeed { get; }

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>ENCOUNTER_GUID</c>: adapter —
        /// <c>GameRun.AdventureState.EncounterGUID</c>. Empty string when absent.</summary>
        string EncounterGuid { get; }

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
