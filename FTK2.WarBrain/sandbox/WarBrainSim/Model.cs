using WarBrain.Core;

namespace WarBrainSim;

/// <summary>Damage types mirroring eDamageType (only the two mitigated ones + none).</summary>
public enum DmgType { PHYSICAL, MAGICAL, NONE }

public enum Row { FRONT, BACK }

/// <summary>
/// An ability as the sim models it — mirrors CombatAbilityConfig + SkillRollData
/// (MinValue/MaxValue/Rolls/ACC verified semantics, docs/research/battle-ai-deep-dive.md §2.1).
/// </summary>
public record SimAbility(
    string Id,
    AbilityCategory Category,
    DmgType DamageType,
    decimal MinValue,          // player min scale factor (enemies effectively 0 — enforced in engine)
    decimal MaxValue,          // × ATK = max damage
    int Rolls,
    int AccAdjust,             // per-ability ACC delta (e.g. heavy attacks -20)
    bool IsBlockable = true,   // false => pierces DEF/RES on PERFECT
    bool IsRanged = false,
    bool IsFocusable = true,
    int HealFlat = 0,          // >0: heals target instead of damaging
    string? AppliesStatus = null, // simplified status tag (e.g. "ATK_DOWN", "STUN")
    bool TargetsAlly = false,
    decimal AoeFraction = 0m   // 0 = single target; >0 = fraction of full damage dealt to every other enemy-side actor
);

public class SimActor
{
    public required string Id;
    public required string Name;
    public bool IsEnemy;
    public Row Row;
    public int MaxHp, Hp;
    public int Atk;
    public int RollStat;       // enemies: ACC stat; players: weapon stat + ACC bonus (pre-clamped aggregate)
    public int Def, Res, Evd;
    public int Crt;            // crit chance %
    public int Crtd;           // bonus crit ratio %
    public int MaxFocus, Focus;
    public int Spd;
    public int Prw;            // vanilla tendency-fire chance
    public int Threat;         // 0..9
    public List<SimAbility> Abilities = new();
    public bool DodgeCooldown; // enemies only (players exempt — verified)
    public bool IsHealer;      // player-side role flag for the scripted policy
    public string? BrainProfileId;  // WarBrain assignment (enemy side)
    public string? DoctrineId;

    public bool IsDead => Hp <= 0;
    public decimal HpFraction => MaxHp == 0 ? 0m : (decimal)Hp / MaxHp;

    public SimActor Clone() => new()
    {
        Id = Id, Name = Name, IsEnemy = IsEnemy, Row = Row,
        MaxHp = MaxHp, Hp = Hp, Atk = Atk, RollStat = RollStat,
        Def = Def, Res = Res, Evd = Evd, Crt = Crt, Crtd = Crtd,
        MaxFocus = MaxFocus, Focus = Focus, Spd = Spd, Prw = Prw, Threat = Threat,
        Abilities = Abilities, IsHealer = IsHealer,
        BrainProfileId = BrainProfileId, DoctrineId = DoctrineId
    };
}

/// <summary>Per-battle metrics collected by the engine.</summary>
public class BattleResult
{
    public int Turns;
    public int EnemyDamageDealt;       // post-mitigation HP removed from players
    public int PlayerDamageDealt;
    public int PlayerDowns;            // player actors reduced to 0
    public bool PartyWiped;
    public bool EnemiesWiped;
    public int EnemyTurns;
    public int EnemyWastedTurns;       // enemy turns that changed nothing (whiff/dodged/0 dmg/no-op)
    public int EnemyKillsSecured;      // enemy turns that downed a player
    public int EnemyFocusSpent;
    public int EnemyPierceHits;
    public int EnemyCrits;
}
