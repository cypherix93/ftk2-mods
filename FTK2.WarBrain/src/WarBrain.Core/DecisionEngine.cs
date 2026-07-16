using System;
using System.Collections.Generic;
using System.Linq;

namespace WarBrain.Core
{
    /// <summary>
    /// The data-driven utility scorer (SPEC §3 steps c-e).
    /// Deterministic given (candidates, profile, doctrine, knobs, rng sequence):
    /// all stochastic draws come through the injected rng delegate, which the host
    /// must back with the game's shared CombatState.Random in MP (lockstep).
    /// </summary>
    public class DecisionEngine
    {
        /// <summary>rng() must return a uniform decimal in [0,1).</summary>
        public ScoredCandidate Decide(
            IReadOnlyList<Candidate> candidates,
            BrainProfile profile,
            DoctrineConfig doctrine,
            GlobalDifficultyKnobs knobs,
            IReadOnlyDictionary<string, decimal> reactionWeightDeltas,   // consideration id -> weight delta (memory reactions)
            IReadOnlyDictionary<string, decimal> targetPriorityDeltas,   // target entity id -> flat score delta
            Func<decimal> rng,
            out List<ScoredCandidate> allScored)
        {
            if (candidates == null || candidates.Count == 0) { allScored = new List<ScoredCandidate>(); return null; }
            knobs = knobs ?? new GlobalDifficultyKnobs();

            var considerations = ResolveConsiderations(profile, doctrine, reactionWeightDeltas);
            var categoryBias = ResolveCategoryBias(profile, doctrine);
            decimal intelligence = profile.DifficultyScaling.IntelligenceMultiplier * knobs.GlobalIntelligenceScalar;
            decimal temperature = profile.Temperature * profile.DifficultyScaling.TemperatureMultiplier * knobs.GlobalTemperatureMultiplier;
            decimal mistakeChance = Clamp01(profile.DifficultyScaling.MistakeChance + knobs.GlobalMistakeChanceAdd);

            var filtered = candidates.Where(c => PassesTargetFilters(c, profile.TargetFilters)).ToList();
            if (filtered.Count == 0) filtered = candidates.ToList(); // filters must never leave the AI with nothing

            allScored = new List<ScoredCandidate>(filtered.Count);
            foreach (var c in filtered)
            {
                var sc = new ScoredCandidate { Candidate = c };
                decimal sum = 0m;
                foreach (var cons in considerations)
                {
                    decimal raw = ExtractRawInput(cons.Id, c);
                    decimal curved = Curve.Evaluate(cons.Curve, raw);
                    decimal contribution = cons.Weight * intelligence * curved;
                    sum += contribution;
                    sc.Breakdown.Add((cons.Id, cons.Weight, raw, curved, contribution));
                }
                sc.CategoryBias = categoryBias.TryGetValue(c.Category.ToString(), out var bias) ? bias : 1.0m;
                sum *= sc.CategoryBias;
                if (targetPriorityDeltas != null && c.TargetId != null &&
                    targetPriorityDeltas.TryGetValue(c.TargetId, out var delta))
                {
                    sc.ReactionDelta = delta;
                    sum += delta;
                }
                sc.Score = sum;
                allScored.Add(sc);
            }

            // MistakeChance: substitute a uniformly-random legal candidate (SPEC §3 step e).
            if (mistakeChance > 0m && rng() < mistakeChance)
            {
                int idx = (int)(rng() * allScored.Count);
                if (idx >= allScored.Count) idx = allScored.Count - 1;
                var mistake = allScored[idx];
                mistake.Probability = 1m / allScored.Count;
                return mistake;
            }

            return SoftmaxPick(allScored, temperature, rng);
        }

        private static ScoredCandidate SoftmaxPick(List<ScoredCandidate> scored, decimal temperature, Func<decimal> rng)
        {
            if (scored.Count == 1) { scored[0].Probability = 1m; return scored[0]; }

            decimal max = scored.Max(s => s.Score);
            if (temperature <= 0.0001m)
            {
                // argmax; deterministic tie-break by (score, candidate order)
                var best = scored.First(s => s.Score == max);
                best.Probability = 1m;
                return best;
            }

            decimal total = 0m;
            var weights = new decimal[scored.Count];
            for (int i = 0; i < scored.Count; i++)
            {
                // exp((s - max)/T): stable, all args <= 0
                weights[i] = Curve.Exp((scored[i].Score - max) / temperature);
                total += weights[i];
            }
            for (int i = 0; i < scored.Count; i++)
                scored[i].Probability = weights[i] / total;

            decimal roll = rng() * total, acc = 0m;
            for (int i = 0; i < scored.Count; i++)
            {
                acc += weights[i];
                if (roll < acc) return scored[i];
            }
            return scored[scored.Count - 1];
        }

        private static List<ConsiderationConfig> ResolveConsiderations(
            BrainProfile profile, DoctrineConfig doctrine, IReadOnlyDictionary<string, decimal> reactionDeltas)
        {
            var map = new Dictionary<string, ConsiderationConfig>();
            foreach (var c in profile.Considerations)
                map[c.Id] = new ConsiderationConfig { Id = c.Id, Weight = c.Weight, Curve = c.Curve };

            if (doctrine != null)
            {
                foreach (var kv in doctrine.ConsiderationWeightDeltas)
                {
                    if (map.TryGetValue(kv.Key, out var existing)) existing.Weight += kv.Value;
                    else map[kv.Key] = new ConsiderationConfig { Id = kv.Key, Weight = kv.Value };
                }
                foreach (var o in doctrine.ConsiderationOverrides)
                    map[o.Id] = new ConsiderationConfig { Id = o.Id, Weight = o.Weight, Curve = o.Curve };
            }
            if (reactionDeltas != null)
            {
                foreach (var kv in reactionDeltas)
                {
                    if (map.TryGetValue(kv.Key, out var existing)) existing.Weight += kv.Value;
                    else map[kv.Key] = new ConsiderationConfig { Id = kv.Key, Weight = kv.Value };
                }
            }
            // deterministic order: by id (never dictionary order — lockstep requirement)
            return map.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        }

        private static Dictionary<string, decimal> ResolveCategoryBias(BrainProfile profile, DoctrineConfig doctrine)
        {
            var bias = new Dictionary<string, decimal>(profile.AbilityCategoryBias);
            if (doctrine != null)
            {
                foreach (var kv in doctrine.AbilityCategoryBiasDeltas)
                    bias[kv.Key] = (bias.TryGetValue(kv.Key, out var b) ? b : 1.0m) + kv.Value;
            }
            return bias;
        }

        private static decimal ExtractRawInput(string considerationId, Candidate c)
        {
            switch (considerationId)
            {
                case ConsiderationIds.ExpectedDamage:    return c.ExpectedDamageFractionOfTargetHp;
                case ConsiderationIds.KillSecure:        return c.KillProbability;
                case ConsiderationIds.FocusFire:         return c.TargetMissingHpFraction;
                case ConsiderationIds.Threat:            return c.TargetThreatNormalized;
                case ConsiderationIds.TargetSquishiness: return c.TargetSquishiness;
                case ConsiderationIds.StatusValue:       return c.StatusValue;
                case ConsiderationIds.AllyInDanger:      return 1m - c.LowestReachableAllyHpFraction;
                case ConsiderationIds.ActionEconomy:     return c.ActionEconomyValue;
                case ConsiderationIds.RowPositionValue:  return c.RowPositionValue;
                case ConsiderationIds.SelfPreservation:  return 1m - c.SelfHpFraction;
                case ConsiderationIds.RangeSafety:       return c.RangeSafety;
                case ConsiderationIds.TendencyMatch:     return c.TendencyMatchRank;
                case ConsiderationIds.FocusEfficiency:   return c.FocusEfficiency;
                default: return 0m;
            }
        }

        private static bool PassesTargetFilters(Candidate c, List<TargetFilterConfig> filters)
        {
            if (filters == null || filters.Count == 0) return true;
            if (c.TargetId == null) return true; // non-targeted candidates are never filtered out
            foreach (var f in filters)
            {
                if (MatchesFilter(c, f)) return true; // OR across list
            }
            return false;
        }

        private static bool MatchesFilter(Candidate c, TargetFilterConfig f)
        {
            if (f.Tags != null && !MatchesTags(c.TargetTags, f.Tags)) return false;
            if (f.BaseType != null)
            {
                var bt = new[] { c.TargetBaseType ?? string.Empty };
                if (!MatchesTags(bt, f.BaseType)) return false;
            }
            if (c.TargetHpFraction > f.HpPercentBelow) return false;
            if (c.TargetHpFraction < f.HpPercentAbove) return false;
            if (f.RowPosition != "ANY" && c.TargetRow != "ANY" && c.TargetRow != f.RowPosition) return false;
            return true;
        }

        private static bool MatchesTags(IReadOnlyList<string> tags, TagMatch m)
        {
            tags = tags ?? Array.Empty<string>();
            if (m.AnyOf != null && m.AnyOf.Count > 0 && !m.AnyOf.Any(tags.Contains)) return false;
            if (m.NoneOf != null && m.NoneOf.Count > 0 && m.NoneOf.Any(tags.Contains)) return false;
            return true;
        }

        private static decimal Clamp01(decimal v) => v < 0m ? 0m : (v > 1m ? 1m : v);
    }
}
