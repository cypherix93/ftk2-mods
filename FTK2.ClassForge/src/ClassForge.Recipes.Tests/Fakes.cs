using System;
using System.Collections.Generic;
using System.Globalization;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Loot;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Recipes.Tests
{
    /// <summary>Test double for a combatant. The Plugin unit will adapt a real game <c>Entity</c> instead.</summary>
    public sealed class FakeEntity : ICombatEntity
    {
        public string Guid { get; set; }

        /// <summary>
        /// Cross-peer identity - see <see cref="ICombatEntity.RosterOrdinal"/>. Left at the unresolved
        /// sentinel by the constructor and stamped with this entity's position by
        /// <see cref="FakeContext.Entities"/>, which is exactly what the real adapter does with
        /// <c>CombatState.Entities</c>. An entity that is never put on a context roster therefore keeps
        /// the sentinel, which is the honest answer for it.
        /// </summary>
        public int RosterOrdinalValue = PeerOrder.Unknown;
        public int RosterOrdinal { get { return RosterOrdinalValue; } }

        public int Team;
        public bool Alive = true;
        public bool Ai;
        public readonly Dictionary<string, int> Stats = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> StatusList = new List<string>();
        public EntityRow RowValue = EntityRow.FRONT;
        /// <summary>Board position (VenueComponent.TilePosition). Defaults to the unresolvable
        /// sentinel so every pre-existing test keeps a position-free entity.</summary>
        public int TileXValue = int.MinValue;
        public int TileYValue = int.MinValue;
        /// <summary>Per-entity progression level. Defaults to the UNKNOWN sentinel so every pre-existing
        /// test keeps a level-free entity and SELF_LEVEL fails safe unless a test opts in.</summary>
        public int LevelValue = EntityReads.UnknownLevel;
        public string Weapon = "BLADE";
        public string CharType = "PLAYER";
        public string Base = "HUMAN";
        public readonly List<string> PassiveList = new List<string>();
        public readonly List<string> TagList = new List<string>();
        public string ConfigNameValue = "";
        /// <summary>GATE D: defaults to the team-1 convention every test Rig already uses for "foe".</summary>
        public bool EnemyFlag;

        /// <summary>Carried Thing ConfigNames (CharacterComponent.Things). <c>null</c> — the default —
        /// means the inventory is UNREADABLE, so <c>HasItem</c> returns its unknown (<c>null</c>) and
        /// HAS_ITEM fails safe unless a test opts in. An EMPTY list is a readable inventory that simply
        /// holds nothing, which is an ordinary negatable false.</summary>
        public List<string> ItemList;

        /// <summary>STATE_HASH_CHANCE spec §3.3 negative control: a systematically-throwing stat resolver.</summary>
        public bool ThrowOnGetStat;

        public FakeEntity(string guid, int team)
        {
            Guid = guid;
            Team = team;
            EnemyFlag = team == 1;
            Stats["HP"] = 100;
            Stats["MXHP"] = 100;
            Stats["FOC"] = 0;
            Stats["MXFOC"] = 3;
        }

        public bool IsAlive { get { return Alive; } }
        public bool IsAiControlled { get { return Ai; } }

        public int GetStat(string statKey)
        {
            if (ThrowOnGetStat) throw new InvalidOperationException("test resolver throw");
            int v;
            return Stats.TryGetValue(statKey ?? "", out v) ? v : 0;
        }

        public IReadOnlyList<string> Statuses { get { return StatusList; } }
        public EntityRow Row { get { return RowValue; } }
        public int TileX { get { return TileXValue; } }
        public int TileY { get { return TileYValue; } }
        public int Level { get { if (ThrowOnGetStat) throw new InvalidOperationException("test resolver throw"); return LevelValue; } }
        public string WeaponClass { get { return Weapon; } }
        public string CharacterType { get { return CharType; } }
        public string BaseType { get { return Base; } }
        public IReadOnlyList<string> Passives { get { return PassiveList; } }
        public bool IsEnemy { get { return EnemyFlag; } }
        public bool HasTag(string tagName) { return tagName != null && TagList.Contains(tagName); }

        /// <summary>Ordinal, case-sensitive possession check over <see cref="ItemList"/>. Shares the
        /// <see cref="ThrowOnGetStat"/> switch with <see cref="Level"/>: one knob for "this entity's
        /// reads throw".</summary>
        public bool? HasItem(string thingConfigName)
        {
            if (ThrowOnGetStat) throw new InvalidOperationException("test inventory read throw");
            if (ItemList == null) return null;
            return ItemList.Contains(thingConfigName ?? "");
        }
        public string ConfigName { get { return ConfigNameValue; } }

        public FakeEntity With(string stat, int value) { Stats[stat] = value; return this; }
        public FakeEntity WithStatus(string id) { StatusList.Add(id); return this; }
        public FakeEntity WithPassive(string id) { PassiveList.Add(id); return this; }
        public FakeEntity WithTag(string tag) { TagList.Add(tag); return this; }
        public FakeEntity At(int x, int y) { TileXValue = x; TileYValue = y; return this; }
        public FakeEntity AtLevel(int level) { LevelValue = level; return this; }
        /// <summary>Makes the inventory readable and puts one Thing in it.</summary>
        public FakeEntity Carrying(string thingConfigName) { WithEmptyInventory(); ItemList.Add(thingConfigName); return this; }
        /// <summary>Makes the inventory readable but empty — a genuine "does not have it", not "unknown".</summary>
        public FakeEntity WithEmptyInventory() { if (ItemList == null) ItemList = new List<string>(); return this; }
    }

    /// <summary>
    /// Test double for a board tile. <see cref="LocalGuid"/> is deliberately a made-up local string: the
    /// engine must never render it into a plan (a tile is logged as <c>tile(x,y)</c>), so a test that sees
    /// a guid leak into <c>Describe()</c> has caught a real replication bug.
    /// </summary>
    public sealed class FakeTile : ICombatTile
    {
        public int XValue;
        public int YValue;
        public string LocalGuidValue;

        public FakeTile(int x, int y)
        {
            XValue = x;
            YValue = y;
            LocalGuidValue = "TILE_LOCAL_" + x.ToString(CultureInfo.InvariantCulture) + "_" +
                             y.ToString(CultureInfo.InvariantCulture);
        }

        public int X { get { return XValue; } }
        public int Y { get { return YValue; } }
        public string LocalGuid { get { return LocalGuidValue; } }
    }

    public sealed class FakeAbility : IAbilityInfo
    {
        public string IdValue;
        public bool Ranged;
        public bool Enemy = true;
        public string StatValue = "PHY";
        public readonly List<string> TagList = new List<string>();

        public FakeAbility(string id) { IdValue = id; }
        public string Id { get { return IdValue; } }
        public bool IsRanged { get { return Ranged; } }
        public bool TargetsEnemy { get { return Enemy; } }
        public string Stat { get { return StatValue; } }
        public IReadOnlyList<string> Tags { get { return TagList; } }
    }

    public sealed class FakeStatus : IStatusInfo
    {
        public string IdValue;
        public string TypeValue;
        public FakeStatus(string id, string type) { IdValue = id; TypeValue = type; }
        public string Id { get { return IdValue; } }
        public string Type { get { return TypeValue; } }
    }

    public sealed class FakeItem : IItemInfo
    {
        public string NameValue;
        public string ClassValue;
        public bool Consumable;
        public FakeItem(string name, string cls, bool consumable) { NameValue = name; ClassValue = cls; Consumable = consumable; }
        public string ConfigName { get { return NameValue; } }
        public string Class { get { return ClassValue; } }
        public bool IsConsumable { get { return Consumable; } }
    }

    /// <summary>Test double for the combat. Entity order is deliberately shufflable to prove the engine
    /// re-sorts by ordinal Guid (SPEC-DELTA-v1.1 §5.2 invariant 4).</summary>
    public sealed class FakeContext : ICombatContext
    {
        public string Identity = "COMBAT_1";
        public int RoundValue = 1;
        public readonly List<ICombatEntity> EntityList = new List<ICombatEntity>();
        public readonly Dictionary<string, IAbilityInfo> Abilities = new Dictionary<string, IAbilityInfo>(StringComparer.Ordinal);
        public readonly Dictionary<string, IStatusInfo> StatusConfigs = new Dictionary<string, IStatusInfo>(StringComparer.Ordinal);
        public readonly Dictionary<string, IItemInfo> Items = new Dictionary<string, IItemInfo>(StringComparer.Ordinal);
        public readonly List<EngineAction> Emitted = new List<EngineAction>();

        /// <summary>v1.4 RANDOM_TILE. Empty by default, so every pre-existing test keeps a board-less
        /// combat and RANDOM_TILE fails safe unless a test opts in.</summary>
        public readonly List<ICombatTile> TileList = new List<ICombatTile>();

        /// <summary>Encounter Modifiers spec §5 test knobs — plain settable fields, no game refs.</summary>
        public int PartyAverageLevelValue = 1;
        public bool IsDungeonValue;
        public bool IsBossFightValue;
        public readonly HashSet<string> EncounterPropertiesSet = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>STATE_HASH_CHANCE spec §2.1 input-token knobs (COMBAT_SEED / RUN_SEED / ENCOUNTER_GUID).</summary>
        public int CombatSeedValue = 12345;
        public int RunSeedValue;
        public string EncounterGuidValue = "";

        public string CombatIdentity { get { return Identity; } }
        public int Round { get { return RoundValue; } }
        /// <summary>
        /// The roster, in roster order - and, like the real adapter, the place each entity learns its own
        /// ordinal. Stamping here (rather than making tests set it by hand) keeps the fake's contract
        /// identical to <c>CombatContextAdapter</c>'s: ordinal i IS position i in this list.
        /// </summary>
        public IReadOnlyList<ICombatEntity> Entities
        {
            get
            {
                for (int i = 0; i < EntityList.Count; i++)
                {
                    var fe = EntityList[i] as FakeEntity;
                    if (fe != null) fe.RosterOrdinalValue = i;
                }
                return EntityList;
            }
        }
        public IReadOnlyList<ICombatTile> Tiles { get { return TileList; } }
        public int PartyAverageLevel { get { return PartyAverageLevelValue; } }
        public bool IsDungeon { get { return IsDungeonValue; } }
        public bool IsBossFight { get { return IsBossFightValue; } }
        public int CombatSeed { get { return CombatSeedValue; } }
        public int RunSeed { get { return RunSeedValue; } }
        public string EncounterGuid { get { return EncounterGuidValue; } }
        public bool HasEncounterProperty(string propertyName) { return propertyName != null && EncounterPropertiesSet.Contains(propertyName); }

        public bool AreOpponents(ICombatEntity a, ICombatEntity b)
        {
            var fa = a as FakeEntity;
            var fb = b as FakeEntity;
            if (fa == null || fb == null) return false;
            return fa.Team != fb.Team;
        }

        public IAbilityInfo GetAbility(string abilityId)
        {
            IAbilityInfo v;
            return abilityId != null && Abilities.TryGetValue(abilityId, out v) ? v : null;
        }

        public IStatusInfo GetStatus(string statusId)
        {
            IStatusInfo v;
            return statusId != null && StatusConfigs.TryGetValue(statusId, out v) ? v : null;
        }

        public IItemInfo GetItem(string thingConfigName)
        {
            IItemInfo v;
            return thingConfigName != null && Items.TryGetValue(thingConfigName, out v) ? v : null;
        }

        public void EmitAction(EngineAction action) { Emitted.Add(action); }

        public FakeContext AddEntity(FakeEntity e) { EntityList.Add(e); return this; }
        /// <summary>Appends one tile. Callers add them in (Y,X) ascending order, which is the ordering the
        /// real adapter guarantees and the ordering the draw index is defined against.</summary>
        public FakeContext AddTile(int x, int y) { TileList.Add(new FakeTile(x, y)); return this; }
        public FakeContext AddAbility(FakeAbility a) { Abilities[a.IdValue] = a; return this; }
        public FakeContext AddStatus(string id, string type) { StatusConfigs[id] = new FakeStatus(id, type); return this; }
        public FakeContext AddItem(string name, string cls, bool consumable) { Items[name] = new FakeItem(name, cls, consumable); return this; }
    }

    /// <summary>
    /// Test double for <c>GameRandom</c>. Deterministic xorshift so two same-seeded instances produce an
    /// identical stream in any runtime, plus a draw counter so tests can assert the roll-count discipline
    /// of SPEC-DELTA-v1.1 §5.2 invariant 3 (a <c>ProcChance</c> of 100 must take ZERO draws).
    /// </summary>
    public sealed class FakeRandom : IRandomSource
    {
        private uint _state;
        private readonly Queue<bool> _scriptedChance = new Queue<bool>();
        private readonly Queue<int> _scriptedInt = new Queue<int>();

        public int Draws;

        /// <summary>The <c>chance</c> argument of the most recent <see cref="NextChance"/> call (0..1), or
        /// null if never called. Lets a test assert the exact numeric output of a <c>ProcChanceFormula</c>
        /// (Encounter Modifiers spec §5) without exposing the dispatcher's private evaluator.</summary>
        public decimal? LastChance;

        public FakeRandom(int seed) { _state = seed == 0 ? 0x9E3779B9u : (uint)seed; }

        public FakeRandom ScriptChance(params bool[] values)
        {
            for (int i = 0; i < values.Length; i++) _scriptedChance.Enqueue(values[i]);
            return this;
        }

        public FakeRandom ScriptInt(params int[] values)
        {
            for (int i = 0; i < values.Length; i++) _scriptedInt.Enqueue(values[i]);
            return this;
        }

        private uint NextState()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        public bool NextChance(decimal chance)
        {
            Draws++;
            LastChance = chance;
            if (_scriptedChance.Count > 0) return _scriptedChance.Dequeue();
            return (decimal)(NextState() % 1000u) / 1000m < chance;
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            Draws++;
            if (_scriptedInt.Count > 0) return _scriptedInt.Dequeue();
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(NextState() % (uint)(maxExclusive - minInclusive));
        }

        /// <summary>Mirrors <c>GameRandom.NextInt(min, max, pMaxInclusive: true)</c> — used by
        /// <c>SELECTION_SET</c> (Encounter Modifiers spec §5/§6.3). Shares the same scripted-int queue as
        /// <see cref="NextInt"/> so a test can script the exact draw regardless of which method consumes it.</summary>
        public int NextIntInclusive(int minInclusive, int maxInclusive)
        {
            Draws++;
            if (_scriptedInt.Count > 0) return _scriptedInt.Dequeue();
            if (maxInclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(NextState() % (uint)(maxInclusive - minInclusive + 1));
        }
    }

    /// <summary>Test double for the loot-grant item-candidate seam (GATE B). Keys are
    /// <c>Tag</c> or <c>Tag|Rarity</c>; a rarity-specific entry is preferred when both are registered.</summary>
    public sealed class FakeCandidateSource : IItemCandidateSource
    {
        private readonly Dictionary<string, List<string>> _byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public FakeCandidateSource Add(string tag, string rarity, params string[] configNames)
        {
            _byKey[Key(tag, rarity)] = new List<string>(configNames);
            return this;
        }

        public IReadOnlyList<string> GetCandidates(string tag, string rarity)
        {
            List<string> exact;
            if (!string.IsNullOrEmpty(rarity) && _byKey.TryGetValue(Key(tag, rarity), out exact)) return exact;
            List<string> anyRarity;
            if (_byKey.TryGetValue(Key(tag, null), out anyRarity)) return anyRarity;
            return new List<string>();
        }

        private static string Key(string tag, string rarity)
        {
            return (tag ?? "") + "|" + (rarity ?? "");
        }
    }

    public sealed class FakeLog : IRecipeLog
    {
        public readonly List<string> Lines = new List<string>();
        public void Info(string message) { Lines.Add("INFO " + message); }
        public void Warn(string message) { Lines.Add("WARN " + message); }
    }

    public static class Scenario
    {
        /// <summary>A 3v2 combat with the usual status/ability configs the fixtures reference.</summary>
        public static FakeContext Standard()
        {
            var ctx = new FakeContext();
            ctx.AddStatus("STATUS_ATTACKUP_00", "BUFF");
            ctx.AddStatus("STATUS_ATTACKUP_01", "BUFF");
            ctx.AddStatus("STATUS_ATTACKUP_02", "BUFF");
            ctx.AddStatus("STATUS_EVADEUP_00", "BUFF");
            ctx.AddStatus("STATUS_ARMORUP_00", "BUFF");
            ctx.AddStatus("STATUS_PROTECT_00", "BUFF");
            ctx.AddStatus("STATUS_DAZE_00", "DAZE");
            ctx.AddStatus("STATUS_ATTACKDOWN_00", "DEBUFF");
            ctx.AddStatus("STATUS_BLEED_00", "BLEED");
            ctx.AddStatus("STATUS_CURSE_00", "CURSE");
            ctx.AddStatus("STATUS_FIRE_00", "FIRE");
            ctx.AddStatus("STATUS_ICE_00", "ICE");
            ctx.AddStatus("STATUS_SHOCK_00", "SHOCK");
            ctx.AddStatus("STATUS_MARKED_00", "DEBUFF");
            ctx.AddItem("DRINK_ALE", "DRINK", true);
            ctx.AddItem("SCROLL_FIRE", "SCROLL", true);
            return ctx;
        }

        public static string Fmt(int v) { return v.ToString(CultureInfo.InvariantCulture); }
    }
}
