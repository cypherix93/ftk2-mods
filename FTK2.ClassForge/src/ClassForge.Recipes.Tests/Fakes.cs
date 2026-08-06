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
        public int Team;
        public bool Alive = true;
        public bool Ai;
        public readonly Dictionary<string, int> Stats = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> StatusList = new List<string>();
        public EntityRow RowValue = EntityRow.FRONT;
        public string Weapon = "BLADE";
        public string CharType = "PLAYER";
        public string Base = "HUMAN";
        public readonly List<string> PassiveList = new List<string>();

        public FakeEntity(string guid, int team)
        {
            Guid = guid;
            Team = team;
            Stats["HP"] = 100;
            Stats["MXHP"] = 100;
            Stats["FOC"] = 0;
            Stats["MXFOC"] = 3;
        }

        public bool IsAlive { get { return Alive; } }
        public bool IsAiControlled { get { return Ai; } }

        public int GetStat(string statKey)
        {
            int v;
            return Stats.TryGetValue(statKey ?? "", out v) ? v : 0;
        }

        public IReadOnlyList<string> Statuses { get { return StatusList; } }
        public EntityRow Row { get { return RowValue; } }
        public string WeaponClass { get { return Weapon; } }
        public string CharacterType { get { return CharType; } }
        public string BaseType { get { return Base; } }
        public IReadOnlyList<string> Passives { get { return PassiveList; } }

        public FakeEntity With(string stat, int value) { Stats[stat] = value; return this; }
        public FakeEntity WithStatus(string id) { StatusList.Add(id); return this; }
        public FakeEntity WithPassive(string id) { PassiveList.Add(id); return this; }
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

        public string CombatIdentity { get { return Identity; } }
        public int Round { get { return RoundValue; } }
        public IReadOnlyList<ICombatEntity> Entities { get { return EntityList; } }

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
