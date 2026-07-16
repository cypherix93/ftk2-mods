using WarBrain.Core;

namespace WarBrainSim;

/// <summary>
/// Party compositions and enemy squads. Player numbers are late-game (level 9,
/// tier-3 gear) estimates grounded in the config analysis
/// (docs/research/battle-ai-deep-dive.md §2.4): HP = 30-40 base + round(9×VIT×0.13)
/// + gear; DEF from up to 5 armor pieces at 4-13 DEF each; EVD capped 95.
/// Enemy numbers are the measured medians for config levels 5/7 plus real weapon
/// templates (MaxValue/Rolls/ACC from Things/Weapons.json).
/// </summary>
public static class Scenarios
{
    // ---------- player archetypes (late-game) ----------
    private static SimActor Tank(string id) => new()
    {
        Id = id, Name = "Tank", IsEnemy = false, Row = Row.FRONT,
        MaxHp = 130, Hp = 130, Atk = 45, RollStat = 88, Def = 34, Res = 18, Evd = 8,
        Crt = 10, Crtd = 10, MaxFocus = 4, Focus = 4, Spd = 60, Threat = 4,
        Abilities = [
            new("TANK_STRIKE", AbilityCategory.ATTACK, DmgType.PHYSICAL, 0.5m, 1.0m, 4, 0),
            new("TANK_HEAVY",  AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 1.5m, 5, -20, IsBlockable: false)
        ]
    };

    private static SimActor Healer(string id) => new()
    {
        Id = id, Name = "Healer", IsEnemy = false, Row = Row.BACK, IsHealer = true,
        MaxHp = 105, Hp = 105, Atk = 38, RollStat = 86, Def = 12, Res = 25, Evd = 12,
        Crt = 10, Crtd = 5, MaxFocus = 6, Focus = 6, Spd = 62, Threat = 7,
        Abilities = [
            new("HEAL",        AbilityCategory.SUPPORT_ALLY, DmgType.NONE, 0m, 0m, 0, 0, HealFlat: 38, TargetsAlly: true),
            new("HOLY_BOLT",   AbilityCategory.ATTACK, DmgType.MAGICAL, 0.5m, 0.9m, 3, 0, IsRanged: true)
        ]
    };

    private static SimActor MeleeDps(string id) => new()
    {
        Id = id, Name = "MeleeDPS", IsEnemy = false, Row = Row.FRONT,
        MaxHp = 115, Hp = 115, Atk = 60, RollStat = 92, Def = 18, Res = 10, Evd = 15,
        Crt = 20, Crtd = 25, MaxFocus = 5, Focus = 5, Spd = 75, Threat = 8,
        Abilities = [
            new("DPS_STRIKE", AbilityCategory.ATTACK, DmgType.PHYSICAL, 0.5m, 1.0m, 4, 0),
            new("DPS_HEAVY",  AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 1.5m, 5, -20, IsBlockable: false)
        ]
    };

    private static SimActor RangedDps(string id) => new()
    {
        Id = id, Name = "RangedDPS", IsEnemy = false, Row = Row.BACK,
        MaxHp = 95, Hp = 95, Atk = 55, RollStat = 92, Def = 8, Res = 8, Evd = 42,
        Crt = 25, Crtd = 25, MaxFocus = 5, Focus = 5, Spd = 80, Threat = 9,
        Abilities = [
            new("BOW_SHOT",  AbilityCategory.ATTACK, DmgType.PHYSICAL, 0.5m, 1.0m, 4, 0, IsRanged: true),
            new("BOW_AIMED", AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 1.4m, 5, -15, IsBlockable: false, IsRanged: true)
        ]
    };

    // ---------- enemy archetypes (config-median stats + real weapon shapes) ----------
    private static SimActor EnemyMelee(string id, int lvl) => Scale(new SimActor
    {
        Id = id, Name = $"Brute{lvl}", IsEnemy = true, Row = Row.FRONT,
        MaxHp = 97, Hp = 97, Atk = 43, RollStat = 84, Def = 9, Res = 12, Evd = 19,
        Crt = 5, Crtd = 0, MaxFocus = 2, Focus = 2, Spd = 74, Prw = 50, Threat = 5,
        BrainProfileId = "WB_BRAIN_BRUTE",
        Abilities = [
            new("BLADE_BASIC", AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 1.0m, 4, 0),
            new("BLADE_HEAVY", AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 1.5m, 5, -20, IsBlockable: false),
            new("BLADE_SPIN",  AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 0.75m, 4, 0, AoeFraction: 0.5m)
        ]
    }, lvl);

    private static SimActor EnemyRanged(string id, int lvl) => Scale(new SimActor
    {
        Id = id, Name = $"Archer{lvl}", IsEnemy = true, Row = Row.BACK,
        MaxHp = 53, Hp = 53, Atk = 34, RollStat = 81, Def = 0, Res = 5, Evd = 45,
        Crt = 10, Crtd = 0, MaxFocus = 3, Focus = 3, Spd = 72, Prw = 50, Threat = 6,
        BrainProfileId = "WB_BRAIN_TACTICIAN",
        Abilities = [
            new("BOW_SHOT",    AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 1.0m, 3, 0, IsRanged: true),
            new("BOW_PIERCE",  AbilityCategory.ATTACK, DmgType.PHYSICAL, 0m, 1.3m, 4, -10, IsBlockable: false, IsRanged: true),
            new("BOW_DEBUFF",  AbilityCategory.DEBUFF, DmgType.PHYSICAL, 0m, 0.5m, 3, 0, IsRanged: true, AppliesStatus: "ENTANGLE")
        ]
    }, lvl);

    private static SimActor EnemyCaster(string id, int lvl) => Scale(new SimActor
    {
        Id = id, Name = $"Cultist{lvl}", IsEnemy = true, Row = Row.BACK,
        MaxHp = 60, Hp = 60, Atk = 38, RollStat = 83, Def = 2, Res = 14, Evd = 15,
        Crt = 5, Crtd = 0, MaxFocus = 4, Focus = 4, Spd = 68, Prw = 60, Threat = 7,
        BrainProfileId = "WB_BRAIN_TACTICIAN",
        Abilities = [
            new("FIRE_BOLT",  AbilityCategory.ATTACK, DmgType.MAGICAL, 0m, 1.1m, 3, 0, IsRanged: true),
            new("FIRE_NOVA",  AbilityCategory.ATTACK, DmgType.MAGICAL, 0m, 0.7m, 3, -5, IsRanged: true, AoeFraction: 0.5m),
            new("CURSE",      AbilityCategory.DEBUFF, DmgType.MAGICAL, 0m, 0.3m, 3, 0, IsRanged: true, AppliesStatus: "ATK_DOWN")
        ]
    }, lvl);

    private static SimActor EnemySupport(string id, int lvl) => Scale(new SimActor
    {
        Id = id, Name = $"Shaman{lvl}", IsEnemy = true, Row = Row.BACK,
        MaxHp = 70, Hp = 70, Atk = 30, RollStat = 82, Def = 4, Res = 16, Evd = 12,
        Crt = 5, Crtd = 0, MaxFocus = 4, Focus = 4, Spd = 65, Prw = 55, Threat = 6,
        BrainProfileId = "WB_BRAIN_SUPPORT",
        Abilities = [
            new("SHAMAN_HEAL",  AbilityCategory.SUPPORT_ALLY, DmgType.NONE, 0m, 0m, 0, 0, HealFlat: 30, TargetsAlly: true),
            new("SPIRIT_BOLT",  AbilityCategory.ATTACK, DmgType.MAGICAL, 0m, 0.9m, 3, 0, IsRanged: true),
            new("WAR_CHANT",    AbilityCategory.SUPPORT_GROUP, DmgType.NONE, 0m, 0m, 0, 0, HealFlat: 12, TargetsAlly: true)
        ]
    }, lvl);

    /// <summary>GetExtraLevelStats verbatim: past lvl 7, +12%/lvl ATK, +15%/lvl HP only.</summary>
    private static SimActor Scale(SimActor a, int lvl)
    {
        for (int i = 7; i < lvl; i++)
        {
            a.Atk += (int)Math.Round(a.Atk * 0.12);
            a.MaxHp += (int)Math.Round(a.MaxHp * 0.15);
        }
        a.Hp = a.MaxHp;
        return a;
    }

    // ---------- compositions ----------
    public static List<SimActor> Party(string comp) => comp switch
    {
        "BALANCED"     => [Tank("p1"), MeleeDps("p2"), RangedDps("p3"), Healer("p4")],
        "TURTLE"       => [Tank("p1"), Tank("p2"), Healer("p3"), RangedDps("p4")],
        "GLASS_CANNON" => [MeleeDps("p1"), MeleeDps("p2"), RangedDps("p3"), RangedDps("p4")],
        "DODGE"        => [RangedDps("p1"), RangedDps("p2"), RangedDps("p3"), Healer("p4")],
        _ => throw new ArgumentException(comp)
    };

    public static List<SimActor> Squad(string squad, int lvl) => squad switch
    {
        "BRUTES"   => [EnemyMelee("e1", lvl), EnemyMelee("e2", lvl), EnemyMelee("e3", lvl), EnemyMelee("e4", lvl)],
        "MIXED"    => [EnemyMelee("e1", lvl), EnemyMelee("e2", lvl), EnemyRanged("e3", lvl), EnemyCaster("e4", lvl)],
        "WARBAND"  => [EnemyMelee("e1", lvl), EnemyMelee("e2", lvl), EnemyCaster("e3", lvl), EnemySupport("e4", lvl)],
        _ => throw new ArgumentException(squad)
    };
}
