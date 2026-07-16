namespace WarBrainSim;

/// <summary>
/// Closed-form expected-damage / kill-probability predictions matching CombatSim's
/// resolution exactly (binomial over slot rolls, crit pre-mitigation, flat DEF/RES,
/// pierce-on-perfect, dodge). This is the same math the plugin computes from live
/// game data (deep-dive §4 "damage prediction").
/// </summary>
public static class DamageOracle
{
    public record Prediction(decimal ExpectedDamage, decimal KillProbability, decimal PerfectProbability);

    public static Prediction Predict(SimActor actor, SimAbility ability, SimActor target, int focus)
    {
        focus = Math.Min(focus, Math.Min(actor.Focus, ability.Rolls));
        if (!ability.IsFocusable) focus = 0;

        int rollStat = CombatSim.FocusedStatValue(actor.RollStat + ability.AccAdjust, focus);
        decimal p = rollStat / 100m;
        int freeRolls = ability.Rolls - focus;

        decimal minScale = actor.IsEnemy ? 0m : ability.MinValue * actor.RollStat * 0.01m;
        int minDmg = Math.Max(0, (int)Math.Round(minScale * actor.Atk));
        int maxDmg = Math.Max(0, (int)Math.Round(ability.MaxValue * actor.Atk));

        decimal critChance = Math.Min(1m, (actor.Crt + focus * 5) / 100m);
        decimal critRatio = 0.15m + actor.Crtd * 0.01m;

        decimal dodge = target.IsEnemy
            ? target.Evd / 100m * (target.DodgeCooldown ? 0m : 1m)  // approx; perfect-exception folded below
            : target.Evd / 100m;

        decimal expected = 0m, killProb = 0m, perfectProb = 0m;

        for (int s = 0; s <= freeRolls; s++)
        {
            decimal ps = Binomial(freeRolls, s, p);
            int successes = focus + s;
            bool perfect = successes == ability.Rolls;
            bool critFail = successes == 0;
            if (perfect) perfectProb += ps;
            if (critFail && minDmg == 0) continue;

            decimal ratio = ability.Rolls == 0 ? 1m : Math.Round((decimal)successes / ability.Rolls, 2);
            decimal value = Math.Max(minScale, ratio);
            int dmg = (int)Math.Round(minDmg + (maxDmg - minDmg) * value);

            int critDmg = dmg + (int)Math.Max(1m, Math.Round(dmg * critRatio));

            bool pierces = !ability.IsBlockable && perfect;
            int mitigation = pierces ? 0 : ability.DamageType switch
            {
                DmgType.PHYSICAL => target.Def,
                DmgType.MAGICAL => target.Res,
                _ => 0
            };
            int netNormal = Math.Max(0, dmg - mitigation);
            int netCrit = Math.Max(0, critDmg - mitigation);

            decimal dodgeHere = (target.IsEnemy && perfect) ? 0m : dodge;
            decimal hit = 1m - dodgeHere;

            expected += ps * hit * ((1m - critChance) * netNormal + critChance * netCrit);

            if (netNormal >= target.Hp) killProb += ps * hit * (1m - critChance);
            if (netCrit >= target.Hp) killProb += ps * hit * critChance;
        }

        return new Prediction(expected, killProb, perfectProb);
    }

    private static decimal Binomial(int n, int k, decimal p)
    {
        decimal c = 1m;
        for (int i = 0; i < k; i++) c = c * (n - i) / (i + 1);
        decimal result = c;
        for (int i = 0; i < k; i++) result *= p;
        for (int i = 0; i < n - k; i++) result *= (1m - p);
        return result;
    }
}
