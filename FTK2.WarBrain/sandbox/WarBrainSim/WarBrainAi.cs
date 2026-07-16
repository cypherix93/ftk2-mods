using WarBrain.Core;

namespace WarBrainSim;

/// <summary>
/// The host adapter: enumerates (ability × target × focus) candidates from sim state,
/// computes facts via DamageOracle, and lets WarBrain.Core's DecisionEngine pick.
/// This mirrors exactly what the BepInEx plugin does against live game state.
/// </summary>
public class WarBrainAi : IAiPolicy
{
    public required Dictionary<string, BrainProfile> Profiles;
    public Dictionary<string, DoctrineConfig> Doctrines = new();
    public required GlobalDifficultyKnobs Knobs;
    public string DefaultProfileId = "WB_BRAIN_BRUTE";

    /// <summary>Emulated memory reactions (M2 preview): healer focus + per-target deltas.</summary>
    public bool EnableMemory = true;
    private readonly Dictionary<string, decimal> _targetPriorityDeltas = new();

    private readonly DecisionEngine _engine = new();

    public void ResetBattleMemory() => _targetPriorityDeltas.Clear();

    /// <summary>Sim calls this when a player heals (memory fact hook emulation).</summary>
    public void OnPlayerHealObserved(string healerId)
    {
        if (EnableMemory) _targetPriorityDeltas[healerId] = 6.0m; // WB_REACT_HEALER_IDENTIFIED
    }

    public (SimAbility ability, SimActor target, int focus)? Decide(SimActor actor, CombatSim sim)
    {
        var profile = actor.BrainProfileId != null && Profiles.TryGetValue(actor.BrainProfileId, out var pr)
            ? pr : Profiles[DefaultProfileId];
        DoctrineConfig? doctrine = actor.DoctrineId != null && Doctrines.TryGetValue(actor.DoctrineId, out var d) ? d : null;

        var candidates = new List<Candidate>();
        var lookup = new List<(SimAbility ability, SimActor target, int focus)>();

        var allies = sim.LivingAlliesOf(actor);
        decimal lowestAllyHp = allies.Count > 0 ? allies.Min(a => a.HpFraction) : 1m;

        foreach (var ability in actor.Abilities)
        {
            if (ability.HealFlat > 0 || ability.TargetsAlly)
            {
                foreach (var ally in allies)
                {
                    var c = new Candidate
                    {
                        AbilityId = ability.Id,
                        Category = ability.Category,
                        TargetId = ally.Id,
                        FocusSpend = 0,
                        StatusValue = ability.AppliesStatus != null ? 0.5m : 0m,
                        LowestReachableAllyHpFraction = ally.HpFraction,
                        SelfHpFraction = actor.HpFraction,
                        TargetTags = Array.Empty<string>(),
                        TargetHpFraction = ally.HpFraction
                    };
                    // healing a full-health ally is worthless — encode via AllyInDanger raw input
                    candidates.Add(c);
                    lookup.Add((ability, ally, 0));
                }
                continue;
            }

            var targets = sim.ReachableTargets(actor, ability);
            foreach (var target in targets)
            {
                var focusOptions = FocusOptions(profile.FocusPolicy, actor, ability, sim);
                var baseline = DamageOracle.Predict(actor, ability, target, 0);
                foreach (int f in focusOptions)
                {
                    var pred = f == 0 ? baseline : DamageOracle.Predict(actor, ability, target, f);
                    decimal focusEff = f == 0 ? 0m
                        : Curve.Clamp01((pred.ExpectedDamage - baseline.ExpectedDamage) / Math.Max(1m, baseline.ExpectedDamage));

                    var c = new Candidate
                    {
                        AbilityId = ability.Id,
                        Category = ability.Category,
                        TargetId = target.Id,
                        FocusSpend = f,
                        ExpectedDamageFractionOfTargetHp = Curve.Clamp01(target.Hp <= 0 ? 0m : pred.ExpectedDamage / target.Hp),
                        KillProbability = Curve.Clamp01(pred.KillProbability),
                        TargetMissingHpFraction = 1m - target.HpFraction,
                        TargetThreatNormalized = Curve.Clamp01(target.Threat / 9m),
                        TargetSquishiness = Squishiness(ability, target),
                        StatusValue = ability.AppliesStatus != null ? 0.5m : 0m,
                        LowestReachableAllyHpFraction = lowestAllyHp,
                        SelfHpFraction = actor.HpFraction,
                        RangeSafety = ability.IsRanged ? 1m : 0.3m,
                        FocusEfficiency = focusEff,
                        TendencyMatchRank = 0m,
                        TargetTags = Array.Empty<string>(),
                        TargetHpFraction = target.HpFraction,
                        TargetRow = target.Row.ToString()
                    };
                    candidates.Add(c);
                    lookup.Add((ability, target, f));
                }
            }
        }

        if (candidates.Count == 0) return null;

        var picked = _engine.Decide(
            candidates, profile, doctrine, Knobs,
            reactionWeightDeltas: null,
            targetPriorityDeltas: EnableMemory ? _targetPriorityDeltas : null,
            rng: () => (decimal)sim.Rng.NextDouble(),
            out _);

        if (picked == null) return null;
        int idx = candidates.IndexOf(picked.Candidate);
        return lookup[idx];
    }

    /// <summary>Squishiness vs this ability: mitigation stat + dodge folded, normalized.</summary>
    private static decimal Squishiness(SimAbility ability, SimActor target)
    {
        int mit = ability.DamageType switch
        {
            DmgType.PHYSICAL => target.Def,
            DmgType.MAGICAL => target.Res,
            _ => 0
        };
        return Curve.Clamp01(1m - (mit * 2 + target.Evd) / 100m);
    }

    private static readonly int[] NoFocus = { 0 };

    private static IEnumerable<int> FocusOptions(string policy, SimActor actor, SimAbility ability, CombatSim sim)
    {
        int max = Math.Min(actor.Focus, ability.Rolls);
        if (!ability.IsFocusable || max <= 0) return NoFocus;
        switch (policy)
        {
            case "NONE": return NoFocus;
            case "MAX": return new[] { max };
            case "VANILLA_RANDOM": return new[] { sim.Rng.Next(actor.Focus + 1) is var r && r > max ? max : r };
            case "SMART":
            default:
                // enumerate every affordable focus level; the scorer decides via
                // KILL_SECURE / FOCUS_EFFICIENCY whether the spend is worth it
                return Enumerable.Range(0, max + 1);
        }
    }
}
