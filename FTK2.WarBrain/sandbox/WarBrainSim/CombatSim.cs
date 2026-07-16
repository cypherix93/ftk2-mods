using WarBrain.Core;

namespace WarBrainSim;

/// <summary>
/// Faithful reimplementation of the decompiled combat resolution
/// (docs/research/battle-ai-deep-dive.md §2). The AI under test only supplies
/// (ability, target, focus) — resolution below is identical for all AIs.
/// </summary>
public class CombatSim
{
    public required Random Rng;
    public List<SimActor> Players = new();
    public List<SimActor> Enemies = new();
    public int MaxRounds = 60;

    public IAiPolicy EnemyAi = null!;
    public IAiPolicy PlayerAi = null!;

    public BattleResult Run()
    {
        var result = new BattleResult();
        var all = Players.Concat(Enemies).ToList();

        for (int round = 0; round < MaxRounds; round++)
        {
            // initiative: SPD desc (approximation of SetInitiative), stable order
            foreach (var actor in all.Where(a => !a.IsDead).OrderByDescending(a => a.Spd).ThenBy(a => a.Id))
            {
                if (actor.IsDead) continue;
                if (Players.All(p => p.IsDead) || Enemies.All(e => e.IsDead)) break;

                var policy = actor.IsEnemy ? EnemyAi : PlayerAi;
                var decision = policy.Decide(actor, this);
                if (actor.IsEnemy) result.EnemyTurns++;

                if (decision == null) { if (actor.IsEnemy) result.EnemyWastedTurns++; continue; }

                ExecuteAbility(actor, decision.Value.ability, decision.Value.target, decision.Value.focus, result);
            }
            result.Turns = round + 1;

            if (Players.All(p => p.IsDead)) { result.PartyWiped = true; break; }
            if (Enemies.All(e => e.IsDead)) { result.EnemiesWiped = true; break; }

            // light focus regen for players (potions/rest abstraction): +1 every 2 rounds
            if (round % 2 == 1)
                foreach (var p in Players.Where(p => !p.IsDead && p.Focus < p.MaxFocus)) p.Focus++;
        }
        return result;
    }

    public void ExecuteAbility(SimActor actor, SimAbility ability, SimActor target, int focus, BattleResult result)
    {
        focus = Math.Min(focus, Math.Min(actor.Focus, ability.Rolls));
        if (!ability.IsFocusable) focus = 0;
        actor.Focus -= focus;
        if (actor.IsEnemy) result.EnemyFocusSpent += focus;

        if (ability.HealFlat > 0)
        {
            int healed = Math.Min(ability.HealFlat, target.MaxHp - target.Hp);
            target.Hp += healed;
            if (actor.IsEnemy && healed == 0) result.EnemyWastedTurns++;
            return;
        }

        // ---- slot roll (SlotRollHelper._getRollResultData) ----
        int rollStat = FocusedStatValue(actor.RollStat + ability.AccAdjust, focus);
        decimal p = rollStat / 100m;
        int successes = 0;
        for (int i = 0; i < ability.Rolls; i++)
            if (i < focus || Roll(p)) successes++;

        bool perfect = successes == ability.Rolls;
        bool critFail = successes == 0;
        decimal successRatio = ability.Rolls == 0 ? 1m : Math.Round((decimal)successes / ability.Rolls, 2);

        // RollData.Value = max(minScale, successRatio); minScale is 0 for enemies (verified)
        decimal minScale = actor.IsEnemy ? 0m : ability.MinValue * actor.RollStat * 0.01m;
        decimal value = Math.Max(minScale, successRatio);

        int minDmg = Math.Max(0, (int)Math.Round(minScale * actor.Atk));
        int maxDmg = Math.Max(0, (int)Math.Round(ability.MaxValue * actor.Atk));

        // whiff: CRIT_FAIL on a damage ability with minDmg 0 → nothing happens
        if (critFail && minDmg == 0)
        {
            if (actor.IsEnemy) result.EnemyWastedTurns++;
            return;
        }

        // ---- dodge (CombatHelper._applyActions, verified asymmetry) ----
        bool canDodge = target.IsEnemy
            ? !perfect && !target.DodgeCooldown   // enemies: no dodge vs PERFECT, cooldown after dodge
            : true;                               // players: always eligible
        if (canDodge && Roll(target.Evd / 100m))
        {
            if (target.IsEnemy) target.DodgeCooldown = true;
            if (actor.IsEnemy) result.EnemyWastedTurns++;
            return;
        }
        if (target.IsEnemy) target.DodgeCooldown = false;

        // ---- damage (lerp + crit + CalculateFinalDamage) ----
        int dmg = (int)Math.Round(minDmg + (maxDmg - minDmg) * value);

        // crit: (CRT + focus*5)%, bonus max(1, dmg*(0.15+CRTD%)) — verified
        bool isCrit = Roll((actor.Crt + focus * 5) / 100m);
        if (isCrit)
        {
            dmg += (int)Math.Max(1m, Math.Round(dmg * (0.15m + actor.Crtd * 0.01m)));
            if (actor.IsEnemy) result.EnemyCrits++;
        }

        int dealt = ApplyMitigatedDamage(actor, target, ability, dmg, perfect, result);

        // simplified AoE: fraction of the damage to every other actor on target's side
        if (ability.AoeFraction > 0m)
        {
            var side = target.IsEnemy ? Enemies : Players;
            foreach (var splash in side.Where(s => s != target && !s.IsDead).ToList())
                dealt += ApplyMitigatedDamage(actor, splash, ability, (int)Math.Round(dmg * ability.AoeFraction), perfect, result);
        }

        if (actor.IsEnemy && dealt == 0) result.EnemyWastedTurns++;

        if (ability.AppliesStatus == "STUN" && !target.IsDead && Roll(0.5m))
        {
            // abstract stun: skip is modeled as losing SPD next round — omitted (kept out of scope for damage study)
        }
    }

    private int ApplyMitigatedDamage(SimActor actor, SimActor target, SimAbility ability, int dmg, bool perfect, BattleResult result)
    {
        if (dmg <= 0 || target.IsDead) return 0;

        // CalculateFinalDamage: flat DEF/RES subtract unless (IsBlockable==false && PERFECT)
        bool pierces = !ability.IsBlockable && perfect;
        if (!pierces)
        {
            int mitigation = ability.DamageType switch
            {
                DmgType.PHYSICAL => target.Def,
                DmgType.MAGICAL => target.Res,
                _ => 0
            };
            dmg = Math.Max(0, dmg - mitigation);
        }
        else if (actor.IsEnemy) result.EnemyPierceHits++;

        if (dmg <= 0) return 0;

        int before = target.Hp;
        target.Hp = Math.Max(0, target.Hp - dmg);
        int dealt = before - target.Hp;

        if (actor.IsEnemy)
        {
            result.EnemyDamageDealt += dealt;
            if (target.Hp == 0) { result.PlayerDowns++; result.EnemyKillsSecured++; }
        }
        else result.PlayerDamageDealt += dealt;

        return dealt;
    }

    /// <summary>CharacterHelper.GetFocusedStatValue verbatim: +10,+5,+2,+1... clamp [10,100].</summary>
    public static int FocusedStatValue(int raw, int focused)
    {
        for (int i = 0; i < focused; i++)
            raw += (int)(10m * (decimal)Math.Pow(0.5, i));
        return Math.Clamp(raw, 10, 100);
    }

    public bool Roll(decimal chance) => (decimal)Rng.NextDouble() < chance;

    public List<SimActor> LivingOpponentsOf(SimActor a) => (a.IsEnemy ? Players : Enemies).Where(x => !x.IsDead).ToList();
    public List<SimActor> LivingAlliesOf(SimActor a) => (a.IsEnemy ? Enemies : Players).Where(x => !x.IsDead).ToList();

    /// <summary>Row reachability: melee can hit FRONT; BACK reachable only if front row empty or ability ranged.</summary>
    public List<SimActor> ReachableTargets(SimActor actor, SimAbility ability)
    {
        var opponents = LivingOpponentsOf(actor);
        if (ability.IsRanged) return opponents;
        var front = opponents.Where(o => o.Row == Row.FRONT).ToList();
        return front.Count > 0 ? front : opponents;
    }
}

/// <summary>An AI under test: returns (ability, target, focus) or null for skip.</summary>
public interface IAiPolicy
{
    (SimAbility ability, SimActor target, int focus)? Decide(SimActor actor, CombatSim sim);
}
