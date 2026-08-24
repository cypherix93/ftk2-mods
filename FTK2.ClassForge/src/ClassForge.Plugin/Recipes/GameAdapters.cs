using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Adapts one game <c>Entity</c> onto <see cref="ICombatEntity"/>. Every member is a pure read of
    /// replicated state (SPEC-DELTA-v1.1 §3) and every one is individually try/caught: a recipe condition
    /// that cannot be answered degrades to a neutral value rather than throwing out of a Harmony patch.
    /// </summary>
    internal sealed class EntityAdapter : ICombatEntity
    {
        internal readonly Entity Native;
        private readonly CombatContextAdapter _ctx;

        private IReadOnlyList<string> _statuses;
        private IReadOnlyList<string> _passives;
        private string _weaponClass;
        private bool _weaponClassResolved;

        internal EntityAdapter(Entity native, CombatContextAdapter ctx)
        {
            Native = native;
            _ctx = ctx;
        }

        public string Guid
        {
            // Entity.Guid is `public string Guid { get; private set; }` (Entity.cs L16) — a string property,
            // not a System.Guid. Ordinal sort of it is the universal tiebreak (§5.2 invariant 4).
            get { try { return Native != null ? (Native.Guid ?? "") : ""; } catch { return ""; } }
        }

        /// <summary><c>CharacterHelper.IsDead</c> is <c>CurrentHealth &lt; 1</c> (CharacterHelper.cs L1469).</summary>
        public bool IsAlive
        {
            get { try { return Native != null && !CharacterHelper.IsDead(Native); } catch { return false; } }
        }

        /// <summary>
        /// Selects <c>AiProcChance</c> over <c>ProcChance</c>. There is no "is this entity player-driven"
        /// flag in the decompile; the engine's own discriminator is <c>CharacterComponent.GroupIndex</c>
        /// (<c>CharacterHelper.IsFriendly</c> = <c>GroupIndex == 0</c>, L1483), so anything outside the
        /// player group is treated as AI-controlled.
        /// </summary>
        public bool IsAiControlled
        {
            get { try { return Native == null || !CharacterHelper.IsFriendly(Native); } catch { return false; } }
        }

        /// <summary><c>CharacterHelper.GetStat(Entity, string, bool)</c> — L396, the one overload
        /// SPEC-DELTA-v1.1 §3.1 standardises on out of the eight available (PSN "Notable surprises" #6).</summary>
        public int GetStat(string statKey)
        {
            try
            {
                if (Native == null || string.IsNullOrEmpty(statKey)) return 0;
                return CharacterHelper.GetStat(Native, statKey, false);
            }
            catch { return 0; }
        }

        /// <summary>
        /// <c>StatusEffectComponent.Statuses</c> is a <c>Dictionary&lt;string, StatusEffectInfo&gt;</c>
        /// (StatusEffectComponent.cs) — <b>not</b> a list. Keys are materialised and sorted ordinal here so
        /// the engine never sees Dictionary enumeration order (§5.2 invariant 4).
        /// </summary>
        public IReadOnlyList<string> Statuses
        {
            get
            {
                if (_statuses != null) return _statuses;
                var list = new List<string>();
                try
                {
                    StatusEffectComponent comp;
                    if (Native != null && Native.TryGet<StatusEffectComponent>(out comp) && comp != null && comp.Statuses != null)
                    {
                        foreach (var kv in comp.Statuses)
                            if (!string.IsNullOrEmpty(kv.Key)) list.Add(kv.Key);
                    }
                }
                catch { }
                list.Sort(StringComparer.Ordinal);
                _statuses = list;
                return _statuses;
            }
        }

        /// <summary>
        /// Row bucket. Characters carry <c>VenueComponent</c> (position); the row bucket lives on the
        /// <b>tile</b> entity's <c>VenueTileComponent.RowPositionsType</c> (<c>eTileRowPositions</c>), reached
        /// exactly the way the engine reaches it —
        /// <c>VenueHelper.GetTileEntityOfCharacter(entity, CombatState.Entities)</c> (VenueHelper.cs L103).
        /// </summary>
        public EntityRow Row
        {
            get
            {
                try
                {
                    var state = _ctx != null ? _ctx.State : null;
                    if (Native == null || state == null || state.Entities == null) return EntityRow.UNKNOWN;
                    var tile = VenueHelper.GetTileEntityOfCharacter(Native, state.Entities);
                    VenueTileComponent tc;
                    if (tile == null || !tile.TryGet<VenueTileComponent>(out tc) || tc == null) return EntityRow.UNKNOWN;
                    switch (tc.RowPositionsType)
                    {
                        case eTileRowPositions.FRONT: return EntityRow.FRONT;
                        case eTileRowPositions.BACK: return EntityRow.BACK;
                        default: return EntityRow.UNKNOWN;
                    }
                }
                catch { return EntityRow.UNKNOWN; }
            }
        }

        /// <summary>Equipped main-hand weapon's <c>ThingConfig.Class</c> (EGT §5 free-string space).</summary>
        public string WeaponClass
        {
            get
            {
                if (_weaponClassResolved) return _weaponClass;
                _weaponClassResolved = true;
                _weaponClass = "";
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return _weaponClass;
                    var weapon = EquipmentHelper.GetEquippedWeaponThing(cc);
                    if (weapon == null || string.IsNullOrEmpty(weapon.ConfigName)) return _weaponClass;
                    var cfg = GameLookups.ThingConfig(weapon.ConfigName);
                    if (cfg != null) _weaponClass = cfg.Class ?? "";
                }
                catch { }
                return _weaponClass;
            }
        }

        /// <summary><c>CharacterComponent.CharacterType</c> (<c>eCharacterTypes</c>, EGT §11).</summary>
        public string CharacterType
        {
            get
            {
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return "";
                    return cc.CharacterType.ToString();
                }
                catch { return ""; }
            }
        }

        /// <summary>The character config's <c>BaseType</c> (CharacterConfig.cs).</summary>
        public string BaseType
        {
            get
            {
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return "";
                    var cfg = GameLookups.CharacterConfig(cc.ConfigName);
                    return cfg != null ? (cfg.BaseType ?? "") : "";
                }
                catch { return ""; }
            }
        }

        /// <summary>
        /// Skill ids this entity carries — a recipe only evaluates for owners holding its <c>SKILL_*</c> id.
        /// Sources, per SPEC-DELTA-v1.1 OQ#1: the class config's own <c>Passives</c>, plus
        /// <c>Equippable.Passives</c> of every equipped Thing. <c>EquipmentHelper.GetEquippedThings(entity,
        /// pVisualOnly: false, pIncludeTraits: true)</c> (EquipmentHelper.cs L341) is the native call that
        /// includes <c>TRAIT_</c>-prefixed Things regardless of slot — which is exactly why a ClassForge
        /// trait with <c>Equippable.Slots: []</c> still contributes its passives with zero patches.
        /// <b>Plus</b>, per Encounter Modifiers spec §4.4: for each entry of this entity's
        /// <c>StatusEffectComponent.Statuses</c>, <c>Configs.StatusEffects[key].Passives</c> filtered to
        /// <c>SKILL_</c>-prefixed entries — mirroring native <c>CharacterHelper.GetPassiveSkills</c>
        /// (L936-968). This is what lets a status-attached recipe (e.g. <c>SKILL_CF_ENCMOD_REGEN_TICK</c>
        /// on <c>STATUS_CF_ENCMOD_REGENERATING</c>) route through the ordinary owned-trigger path with no
        /// bespoke combat-scoped machinery.
        /// <para>The native calls return unordered collections, so the result is sorted ordinal and
        /// de-duplicated before the engine sees it (§5.2 invariant 4). Adapter instances are per-hook-
        /// invocation (see remarks below), so this cannot go stale across status changes.</para>
        /// </summary>
        public IReadOnlyList<string> Passives
        {
            get
            {
                if (_passives != null) return _passives;
                var set = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    CharacterComponent cc;
                    if (Native != null && Native.TryGet<CharacterComponent>(out cc) && cc != null)
                    {
                        var charCfg = GameLookups.CharacterConfig(cc.ConfigName);
                        if (charCfg != null && charCfg.Passives != null)
                            for (int i = 0; i < charCfg.Passives.Count; i++)
                                if (!string.IsNullOrEmpty(charCfg.Passives[i])) set.Add(charCfg.Passives[i]);

                        var equipped = EquipmentHelper.GetEquippedThings(Native, false, true);
                        if (equipped != null)
                        {
                            foreach (var thing in equipped)
                            {
                                if (thing == null || string.IsNullOrEmpty(thing.ConfigName)) continue;
                                if (thing.PassiveModifiers != null)
                                    for (int i = 0; i < thing.PassiveModifiers.Count; i++)
                                        if (!string.IsNullOrEmpty(thing.PassiveModifiers[i])) set.Add(thing.PassiveModifiers[i]);

                                var tc = GameLookups.ThingConfig(thing.ConfigName);
                                if (tc == null || tc.Equippable == null || tc.Equippable.Passives == null) continue;
                                for (int i = 0; i < tc.Equippable.Passives.Count; i++)
                                    if (!string.IsNullOrEmpty(tc.Equippable.Passives[i])) set.Add(tc.Equippable.Passives[i]);
                            }
                        }
                    }

                    // Encounter Modifiers spec §4.4 — status-attached SKILL_ passives.
                    StatusEffectComponent statusComp;
                    if (Native != null && Native.TryGet<StatusEffectComponent>(out statusComp) && statusComp != null && statusComp.Statuses != null)
                    {
                        foreach (var kv in statusComp.Statuses)
                        {
                            if (string.IsNullOrEmpty(kv.Key)) continue;
                            var statusCfg = GameLookups.StatusConfig(kv.Key);
                            if (statusCfg == null || statusCfg.Passives == null) continue;
                            for (int i = 0; i < statusCfg.Passives.Count; i++)
                            {
                                var p = statusCfg.Passives[i];
                                if (!string.IsNullOrEmpty(p) && p.StartsWith("SKILL_", StringComparison.Ordinal))
                                    set.Add(p);
                            }
                        }
                    }
                }
                catch { }
                var list = new List<string>(set);
                list.Sort(StringComparer.Ordinal);
                _passives = list;
                return _passives;
            }
        }

        /// <summary>GATE D: <c>CharacterHelper.IsEnemy(entity)</c> — <c>CharacterComponent.GroupIndex == 1</c>
        /// (verified CharacterHelper.cs:1492).</summary>
        public bool IsEnemy
        {
            get { try { return Native != null && CharacterHelper.IsEnemy(Native); } catch { return false; } }
        }

        /// <summary><c>CharacterHelper.ActorHasTag(entity, eConfigTags.&lt;tagName&gt;)</c> — parses the enum
        /// member name from <paramref name="tagName"/>; an unparseable name is a fail-safe false.</summary>
        public bool HasTag(string tagName)
        {
            try
            {
                if (Native == null || string.IsNullOrEmpty(tagName)) return false;
                eConfigTags tag;
                if (!Enum.TryParse(tagName, out tag)) return false;
                return CharacterHelper.ActorHasTag(Native, tag);
            }
            catch { return false; }
        }

        /// <summary><c>CharacterComponent.ConfigName</c>.</summary>
        public string ConfigName
        {
            get
            {
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return "";
                    return cc.ConfigName ?? "";
                }
                catch { return ""; }
            }
        }
    }

    /// <summary>
    /// Adapts the live combat onto <see cref="ICombatContext"/>. One instance is built per hook invocation;
    /// it is a thin view over <c>Env.GameRun.CombatState</c>, not a copy.
    /// </summary>
    internal sealed class CombatContextAdapter : ICombatContext
    {
        internal readonly CombatState State;
        internal readonly GameRandom Random;
        internal readonly Env Env;

        private readonly Dictionary<string, EntityAdapter> _byGuid = new Dictionary<string, EntityAdapter>(StringComparer.Ordinal);
        private readonly List<ICombatEntity> _entities = new List<ICombatEntity>();
        private readonly string _identity;

        /// <summary>Actions the dispatcher streamed out via <see cref="EmitAction"/>, in plan order.</summary>
        internal readonly List<EngineAction> Emitted = new List<EngineAction>();

        internal CombatContextAdapter(Env env, CombatState state)
        {
            Env = env;
            State = state;
            Random = state != null ? state.Random : null;

            // SPEC-DELTA-v1.1 §6: CombatKey := (ReferenceEquals-identity of CombatState, CombatState.Random.Seed).
            // GameRandom.Seed is `public readonly int` (PSN §10) and is identical on every peer in an online
            // session. RuntimeHelpers.GetHashCode is the identity hash (never an overridden GetHashCode).
            int identityHash = state != null ? RuntimeHelpers.GetHashCode(state) : 0;
            int seed = Random != null ? Random.Seed : 0;
            _identity = identityHash.ToString(CultureInfo.InvariantCulture) + "|" + seed.ToString(CultureInfo.InvariantCulture);

            try
            {
                if (state != null && state.Entities != null)
                {
                    // Materialise in ascending ordinal Guid order so ICombatContext.Entities is already
                    // deterministic even though the engine re-sorts defensively (§5.2 invariant 4).
                    var natives = new List<Entity>(state.Entities);
                    natives.Sort(CompareByGuid);
                    for (int i = 0; i < natives.Count; i++)
                    {
                        var n = natives[i];
                        if (n == null) continue;
                        var adapter = Wrap(n);
                        if (adapter != null) _entities.Add(adapter);
                    }
                }
            }
            catch { }
        }

        private static int CompareByGuid(Entity a, Entity b)
        {
            string ga = "", gb = "";
            try { ga = a != null ? (a.Guid ?? "") : ""; } catch { }
            try { gb = b != null ? (b.Guid ?? "") : ""; } catch { }
            return string.CompareOrdinal(ga, gb);
        }

        /// <summary>Stable adapter per Entity guid, so reference equality holds within one dispatch.</summary>
        internal EntityAdapter Wrap(Entity native)
        {
            if (native == null) return null;
            string guid;
            try { guid = native.Guid ?? ""; } catch { return null; }
            EntityAdapter existing;
            if (_byGuid.TryGetValue(guid, out existing)) return existing;
            var created = new EntityAdapter(native, this);
            _byGuid[guid] = created;
            return created;
        }

        /// <summary>Resolves a planned action's guid back to the native <c>Entity</c>. Only entities present
        /// in this combat resolve; anything else returns null and the executor skips that action.</summary>
        internal Entity NativeByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            EntityAdapter adapter;
            return _byGuid.TryGetValue(guid, out adapter) && adapter != null ? adapter.Native : null;
        }

        public string CombatIdentity { get { return _identity; } }

        /// <summary>
        /// <c>CombatState.TotalRounds</c> (CombatState.cs L8) — a real, verified field, incremented inside
        /// <c>CombatHelper.NextTurn</c>'s <c>pIsNewRound</c> branch (CombatHelper.cs L793-838) right after the
        /// <c>"## NEW ROUND"</c> log. SPEC-DELTA-v1.1 §6 named the <c>NextTurn</c> postfix as the round source
        /// of truth specifically because <c>TotalRounds</c> was "unverified" at the time; it is now verified,
        /// so ClassForge reads it directly. That is strictly safer than mirroring it into plugin state: a
        /// pure read of replicated state cannot drift if a hook is ever missed, and it needs no hook at all.
        /// </summary>
        public int Round
        {
            get { try { return State != null ? State.TotalRounds : 0; } catch { return 0; } }
        }

        public IReadOnlyList<ICombatEntity> Entities { get { return _entities; } }

        /// <summary><c>CharacterHelper.IsOpponent</c> = differing <c>CharacterComponent.GroupIndex</c> (L1497).</summary>
        public bool AreOpponents(ICombatEntity a, ICombatEntity b)
        {
            try
            {
                var ea = a as EntityAdapter;
                var eb = b as EntityAdapter;
                if (ea == null || eb == null || ea.Native == null || eb.Native == null) return false;
                return CharacterHelper.IsOpponent(ea.Native, eb.Native);
            }
            catch { return false; }
        }

        public IAbilityInfo GetAbility(string abilityId)
        {
            try
            {
                if (string.IsNullOrEmpty(abilityId)) return null;
                var cfg = GameLookups.AbilityConfig(abilityId);
                if (cfg == null) return null;
                return new AbilityInfoAdapter(abilityId, cfg, this);
            }
            catch { return null; }
        }

        public IStatusInfo GetStatus(string statusId)
        {
            try
            {
                if (string.IsNullOrEmpty(statusId)) return null;
                var cfg = GameLookups.StatusConfig(statusId);
                if (cfg == null) return null;
                return new StatusInfoAdapter(statusId, cfg.Type.ToString());
            }
            catch { return null; }
        }

        public IItemInfo GetItem(string thingConfigName)
        {
            try
            {
                if (string.IsNullOrEmpty(thingConfigName)) return null;
                var cfg = GameLookups.ThingConfig(thingConfigName);
                if (cfg == null) return null;
                return new ItemInfoAdapter(thingConfigName, cfg.Class ?? "", cfg.ConsumableType != eConsumableTypes.NONE);
            }
            catch { return null; }
        }

        /// <summary>Encounter Modifiers spec §5 <c>PARTY_AVG_LEVEL</c>: <c>ProgressionHelper.
        /// GetAveragePartyLevel</c> over <c>GameRun.Entities</c> filtered to
        /// <c>Has&lt;PlayerComponent&gt;() &amp;&amp; Has&lt;CharacterComponent&gt;()</c> — mirrors EOR's
        /// caller-side filter (L22744); the extra CharacterComponent filter is a safety net against a
        /// PlayerComponent entity that (abnormally) lacks one, which the native helper would otherwise throw on.</summary>
        public int PartyAverageLevel
        {
            get
            {
                try
                {
                    var run = Env != null ? Env.GameRun : null;
                    if (run == null || run.Entities == null) return 0;
                    var filtered = new List<Entity>();
                    foreach (var e in run.Entities)
                    {
                        if (e == null) continue;
                        PlayerComponent pc; CharacterComponent cc;
                        if (e.TryGet<PlayerComponent>(out pc) && e.TryGet<CharacterComponent>(out cc))
                            filtered.Add(e);
                    }
                    return ProgressionHelper.GetAveragePartyLevel(filtered);
                }
                catch { return 0; }
            }
        }

        /// <summary>Encounter Modifiers spec §5 <c>IS_DUNGEON</c>: <c>CombatState.IsDungeon</c>.</summary>
        public bool IsDungeon
        {
            get { try { return State != null && State.IsDungeon; } catch { return false; } }
        }

        /// <summary>Encounter Modifiers spec §5 <c>BOSS_FIGHT</c>: <c>CombatState.BossFightState != null</c>.</summary>
        public bool IsBossFight
        {
            get { try { return State != null && State.BossFightState != null; } catch { return false; } }
        }

        /// <summary>Encounter Modifiers spec §5 <c>ENCOUNTER_PROPERTY</c>: resolves the encounter entity via
        /// <c>GameRun.AdventureState.EncounterGUID</c>, then <c>EncounterComponent.HasProperty</c>. No
        /// encounter entity resolved ⇒ false (matches EOR's default, L22772-3).</summary>
        public bool HasEncounterProperty(string propertyName)
        {
            try
            {
                if (string.IsNullOrEmpty(propertyName)) return false;
                eEncounterProperties prop;
                if (!Enum.TryParse(propertyName, out prop)) return false;

                var run = Env != null ? Env.GameRun : null;
                var adv = run != null ? run.AdventureState : null;
                string guid = adv != null ? adv.EncounterGUID : null;
                if (string.IsNullOrEmpty(guid) || run.Entities == null) return false;

                foreach (var e in run.Entities)
                {
                    if (e == null || !string.Equals(e.Guid, guid, StringComparison.Ordinal)) continue;
                    EncounterComponent ec;
                    if (e.TryGet<EncounterComponent>(out ec) && ec != null)
                        return ec.HasProperty(prop);
                    return false;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>COMBAT_SEED</c>: <c>CombatState.Random.Seed</c> —
        /// <c>public readonly int</c> (PSN §10), identical on every peer. Deliberately NOT
        /// <see cref="CombatIdentity"/>: its ReferenceEquals half is process-local and must never feed a
        /// cross-peer hash.</summary>
        public int CombatSeed
        {
            get { try { return Random != null ? Random.Seed : 0; } catch { return 0; } }
        }

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>RUN_SEED</c>: <c>GameRunData.MapGenSeed</c>.</summary>
        public int RunSeed
        {
            get
            {
                try
                {
                    var run = Env != null ? Env.GameRun : null;
                    return run != null ? run.MapGenSeed : 0;
                }
                catch { return 0; }
            }
        }

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>ENCOUNTER_GUID</c>: <c>GameRun.AdventureState.EncounterGUID</c>.</summary>
        public string EncounterGuid
        {
            get
            {
                try
                {
                    var run = Env != null ? Env.GameRun : null;
                    var adv = run != null ? run.AdventureState : null;
                    return adv != null && adv.EncounterGUID != null ? adv.EncounterGUID : "";
                }
                catch { return ""; }
            }
        }

        /// <summary>Action sink. The plan is collected here and executed by <see cref="RecipeActionExecutor"/>
        /// after the dispatcher returns, so nothing mutates game state mid-evaluation.</summary>
        public void EmitAction(EngineAction action)
        {
            if (action != null) Emitted.Add(action);
        }

        /// <summary>
        /// The acting Thing carrying an ability, used for <c>ABILITY_STAT</c> (C3):
        /// <c>Interactable.Abilities[abilityId].Stat</c> (<c>SkillRollData.Stat</c>).
        /// Set by whichever hook has a <c>pThing</c> parameter.
        /// </summary>
        internal Thing ActingThing;
    }

    internal sealed class AbilityInfoAdapter : IAbilityInfo
    {
        private readonly string _id;
        private readonly CombatAbilityConfig _cfg;
        private readonly CombatContextAdapter _ctx;

        internal AbilityInfoAdapter(string id, CombatAbilityConfig cfg, CombatContextAdapter ctx)
        {
            _id = id; _cfg = cfg; _ctx = ctx;
        }

        public string Id { get { return _id; } }
        public bool IsRanged { get { try { return _cfg.IsRanged; } catch { return false; } } }

        /// <summary>C2 <c>HOSTILE_ACTION</c>: <c>Configs.Abilities[id].Target == eTargets.ENEMY</c>
        /// (PSN §6 L1351 reads exactly this comparison).</summary>
        public bool TargetsEnemy { get { try { return _cfg.Target == eTargets.ENEMY; } catch { return false; } } }

        /// <summary>C3 <c>ABILITY_STAT</c>: the acting Thing's
        /// <c>Interactable.Abilities[abilityId].Stat</c> (<c>SkillRollData.Stat</c>, a free string ∩
        /// <c>eCharacterStats</c>). Empty when the trigger carried no Thing.</summary>
        public string Stat
        {
            get
            {
                try
                {
                    var thing = _ctx != null ? _ctx.ActingThing : null;
                    if (thing == null || string.IsNullOrEmpty(thing.ConfigName)) return "";
                    var tc = GameLookups.ThingConfig(thing.ConfigName);
                    if (tc == null || tc.Interactable == null || tc.Interactable.Abilities == null) return "";
                    SkillRollData roll;
                    if (!tc.Interactable.Abilities.TryGetValue(_id, out roll) || roll == null) return "";
                    return roll.Stat ?? "";
                }
                catch { return ""; }
            }
        }

        public IReadOnlyList<string> Tags
        {
            get
            {
                try
                {
                    if (_cfg.Tags == null) return EmptyStrings;
                    var list = new List<string>(_cfg.Tags);
                    list.Sort(StringComparer.Ordinal);
                    return list;
                }
                catch { return EmptyStrings; }
            }
        }

        private static readonly string[] EmptyStrings = new string[0];
    }

    internal sealed class StatusInfoAdapter : IStatusInfo
    {
        private readonly string _id;
        private readonly string _type;
        internal StatusInfoAdapter(string id, string type) { _id = id; _type = type; }
        public string Id { get { return _id; } }
        public string Type { get { return _type; } }
    }

    internal sealed class ItemInfoAdapter : IItemInfo
    {
        private readonly string _name;
        private readonly string _class;
        private readonly bool _consumable;
        internal ItemInfoAdapter(string name, string cls, bool consumable) { _name = name; _class = cls; _consumable = consumable; }
        public string ConfigName { get { return _name; } }
        public string Class { get { return _class; } }
        public bool IsConsumable { get { return _consumable; } }
    }

    /// <summary>
    /// The shared combat RNG (SPEC-DELTA-v1.1 §5.2 invariant 1). Wraps <c>CombatState.Random</c> and
    /// <b>nothing else</b> — <c>System.Random</c>, <c>UnityEngine.Random</c> and freshly constructed
    /// <c>GameRandom</c> instances are all forbidden, because <c>GameRandom</c> is backed by a single seeded
    /// <c>System.Random</c> whose call order and call count must stay identical across peers (PSN §10).
    /// </summary>
    internal sealed class GameRandomSource : IRandomSource
    {
        private readonly GameRandom _rng;
        internal GameRandomSource(GameRandom rng) { _rng = rng; }

        /// <summary><c>GameRandom.NextChance(decimal)</c> — one draw.</summary>
        public bool NextChance(decimal chance) { return _rng.NextChance(chance); }

        /// <summary><c>GameRandom.NextInt(min, max, pMaxInclusive: false)</c> — one draw. Exactly what
        /// <c>GetRandomElementFromList&lt;T&gt;</c> does internally, which is what <c>StatusOneOf</c> needs.</summary>
        public int NextInt(int minInclusive, int maxExclusive) { return _rng.NextInt(minInclusive, maxExclusive, false); }

        /// <summary><c>GameRandom.NextInt(min, max, pMaxInclusive: true)</c> — one draw. Used by
        /// <c>SELECTION_SET</c> (Encounter Modifiers spec §5/§6.3), EOR's weighted-pick draw verbatim (L22814).</summary>
        public int NextIntInclusive(int minInclusive, int maxInclusive) { return _rng.NextInt(minInclusive, maxInclusive, true); }
    }

    /// <summary>Routes the engine's diagnostics into BepInEx, gated on <c>[General] VerboseLogging</c>.</summary>
    internal sealed class RecipeLogAdapter : IRecipeLog
    {
        public void Info(string message)
        {
            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug(message);
        }

        public void Warn(string message)
        {
            ClassForgePlugin.Log.LogWarning(message);
        }
    }

    /// <summary>
    /// Null-safe config lookups. <c>Env.Configs</c> is a <c>public static</c> field (Env.cs L6) and the
    /// dictionaries are <c>SerializedSortedDictionary&lt;string, T&gt;</c>. Every native accessor the game
    /// uses is a raw indexer — <c>InventoryHelper.GetThingConfig</c> is literally
    /// <c>return Env.Configs.Things[pThingName];</c>, which throws <c>KeyNotFoundException</c> on a miss —
    /// so ClassForge never indexes directly.
    /// </summary>
    internal static class GameLookups
    {
        internal static ThingConfig ThingConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.Things == null || string.IsNullOrEmpty(id)) return null;
                return c.Things.ContainsKey(id) ? c.Things[id] : null;
            }
            catch { return null; }
        }

        internal static CharacterConfig CharacterConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.Characters == null || string.IsNullOrEmpty(id)) return null;
                return c.Characters.ContainsKey(id) ? c.Characters[id] : null;
            }
            catch { return null; }
        }

        internal static CombatAbilityConfig AbilityConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.Abilities == null || string.IsNullOrEmpty(id)) return null;
                return c.Abilities.ContainsKey(id) ? c.Abilities[id] : null;
            }
            catch { return null; }
        }

        internal static StatusEffectConfig StatusConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.StatusEffects == null || string.IsNullOrEmpty(id)) return null;
                return c.StatusEffects.ContainsKey(id) ? c.StatusEffects[id] : null;
            }
            catch { return null; }
        }
    }
}
