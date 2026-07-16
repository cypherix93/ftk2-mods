namespace WarBrainSim;

/// <summary>
/// Scripted "experienced co-op party": focus-fires the weakest enemy, secures kills,
/// heals proactively, spends focus intelligently. This is the benchmark the enemy AI
/// must threaten — deliberately strong play, not average play.
/// </summary>
public class ExperiencedPlayerPolicy : IAiPolicy
{
    /// <summary>Hook so the WarBrain memory emulation can observe heals.</summary>
    public Action<string>? OnHeal;

    public (SimAbility ability, SimActor target, int focus)? Decide(SimActor actor, CombatSim sim)
    {
        var enemies = sim.LivingOpponentsOf(actor);
        if (enemies.Count == 0) return null;

        // 1. healer: heal the lowest ally under 45%
        if (actor.IsHealer)
        {
            var heal = actor.Abilities.FirstOrDefault(a => a.HealFlat > 0);
            var hurt = sim.LivingAlliesOf(actor).Where(a => a.HpFraction < 0.45m).OrderBy(a => a.HpFraction).FirstOrDefault();
            if (heal != null && hurt != null)
            {
                OnHeal?.Invoke(actor.Id);
                return (heal, hurt, 0);
            }
        }

        // 2. attack: maximize expected damage; prefer guaranteed kills; focus-fire weakest
        (SimAbility ability, SimActor target, int focus)? best = null;
        decimal bestScore = decimal.MinValue;

        foreach (var ability in actor.Abilities.Where(a => a.HealFlat == 0 && !a.TargetsAlly))
        {
            foreach (var target in sim.ReachableTargets(actor, ability))
            {
                int maxFocus = Math.Min(actor.Focus, ability.Rolls);
                foreach (int f in new[] { 0, maxFocus }.Distinct())
                {
                    var pred = DamageOracle.Predict(actor, ability, target, f);
                    // score: E[dmg] + kill bonus + focus-fire bonus − focus cost
                    decimal score = pred.ExpectedDamage
                                    + pred.KillProbability * 60m
                                    + (1m - target.HpFraction) * 12m
                                    - f * 2.5m;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (ability, target, f);
                    }
                }
            }
        }
        return best;
    }
}
