using WarBrain.Core;

namespace WarBrainSim;

/// <summary>
/// Faithful reimplementation of AIHelper.StandardAiDecision + ForceAiDecision +
/// GetPreferredTarget (the path 100% of shipped enemies take — deep-dive §1.2/§1.3/§1.4):
///  - uniformly random usable ability from the bag
///  - target: tendency ordering fires only PRW% of the time; else random tile
///    (max-targets ordering only matters for AoE; single-target = random)
///  - focus: uniform random 0..currentFocus (§1.5)
/// </summary>
public class VanillaAi : IAiPolicy
{
    public (SimAbility ability, SimActor target, int focus)? Decide(SimActor actor, CombatSim sim)
    {
        var usable = actor.Abilities.ToList();
        if (usable.Count == 0) return null;

        // StandardAiDecision: Random.GetRandomElementFromList(list)
        var ability = usable[sim.Rng.Next(usable.Count)];

        SimActor? target;
        if (ability.HealFlat > 0 || ability.TargetsAlly)
        {
            var allies = sim.LivingAlliesOf(actor);
            // vanilla has no smart heal targeting on the Standard path either: random ally
            target = allies.Count > 0 ? allies[sim.Rng.Next(allies.Count)] : null;
        }
        else
        {
            var targets = sim.ReachableTargets(actor, ability);
            if (targets.Count == 0) return null;

            // GetPreferredTarget: shuffled tiles; tendency only with P = PRW * 0.01
            // (sim abilities carry no tendency ~75% of the time in the real data; we model
            //  the common case MOSTHEALTH/LEASTARMOR-free: plain random pick)
            target = targets[sim.Rng.Next(targets.Count)];
        }
        if (target == null) return null;

        // ForceAiDecision: CachedFocus = Random.NextInt(0, GetFocus(entity)) inclusive
        int focus = ability.IsFocusable && actor.Focus > 0 ? sim.Rng.Next(actor.Focus + 1) : 0;

        return (ability, target, focus);
    }
}
