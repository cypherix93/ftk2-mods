using System.Collections.Generic;

namespace WarBrain.Core
{
    /// <summary>
    /// One legal (ability × target × focus) option, with host-computed facts.
    /// The host adapter (sandbox sim, or the BepInEx plugin reading live game state)
    /// is responsible for game-accurate predictions; the engine only combines them.
    /// All facts are raw values the consideration evaluators normalize to [0,1].
    /// </summary>
    public class Candidate
    {
        public string AbilityId;
        public AbilityCategory Category;
        public string TargetId;                 // entity id/guid of the chosen target tile occupant
        public int FocusSpend;                  // focus points this candidate commits

        // --- facts about the target (attack/debuff candidates) ---
        public decimal ExpectedDamageFractionOfTargetHp;  // E[final dmg] / target current HP (host: binomial over slots, DEF/RES, pierce, dodge)
        public decimal KillProbability;                   // P(final dmg >= target current HP)
        public decimal TargetMissingHpFraction;           // 1 - hp/maxHp of target
        public decimal TargetThreatNormalized;            // CharacterConfig.Threat / 9
        public decimal TargetSquishiness;                 // 1 - min(1, mitigation/expected raw dmg) vs this ability's dmg type incl. EVD
        public decimal TendencyMatchRank;                 // 1.0 = best target per ability's vanilla tendency, partial credit by rank

        // --- facts about allies / self / board ---
        public decimal StatusValue;             // sign-aware value of status applied/removed (0..1)
        public decimal LowestReachableAllyHpFraction = 1.0m; // for ALLY_IN_DANGER: min ally hp fraction this ability can help
        public decimal SelfHpFraction = 1.0m;
        public decimal RowPositionValue;        // tactical value of resulting/target row (host-computed)
        public decimal RangeSafety;             // 1 = ranged & unthreatened
        public decimal ActionEconomyValue;      // reward for cheap-action plays when actions scarce
        public decimal FocusEfficiency;         // marginal value of the focus committed (host: ΔE[dmg or pierce] per point)

        // filter inputs
        public IReadOnlyList<string> TargetTags;
        public string TargetBaseType;
        public decimal TargetHpFraction = 1.0m;
        public string TargetRow = "ANY";
    }

    /// <summary>Result of a decision with full per-consideration breakdown for logging.</summary>
    public class ScoredCandidate
    {
        public Candidate Candidate;
        public decimal Score;
        public decimal Probability;             // softmax probability it was picked with
        public List<(string id, decimal weight, decimal raw, decimal curved, decimal contribution)> Breakdown
            = new List<(string, decimal, decimal, decimal, decimal)>();
        public decimal CategoryBias = 1.0m;
        public decimal ReactionDelta = 0.0m;
    }
}
