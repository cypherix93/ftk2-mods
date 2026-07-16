using System.Collections.Generic;

namespace WarBrain.Core
{
    /// <summary>Mirror of the game's eAbilityCategories (names must match verbatim).</summary>
    public enum AbilityCategory
    {
        ATTACK, DEBUFF, SUPPORT_SELF, SUPPORT_ALLY, SUPPORT_GROUP, TAUNT,
        USE_ITEM, MOVE, MOVE_ENEMY, SUMMON, FLEE, SKIP_TURN, REVIVE_ALLY, REPAIR_VEHICLE
    }

    public enum CurveType { LINEAR, QUADRATIC, LOGISTIC, STEP }

    /// <summary>Curve schema per SPEC §4.2. Only fields relevant to Type are read.</summary>
    public class CurveConfig
    {
        public CurveType Type = CurveType.LINEAR;
        public decimal Exponent = 2.0m;      // QUADRATIC
        public decimal Midpoint = 0.5m;      // LOGISTIC
        public decimal Steepness = 8.0m;     // LOGISTIC
        public decimal Threshold = 0.5m;     // STEP
    }

    public class ConsiderationConfig
    {
        public string Id;
        public decimal Weight = 1.0m;
        public CurveConfig Curve = new CurveConfig();
    }

    public class TargetFilterConfig
    {
        public TagMatch Tags;
        public TagMatch BaseType;
        public decimal HpPercentBelow = 1.0m;
        public decimal HpPercentAbove = 0.0m;
        public string RowPosition = "ANY"; // FRONT | BACK | ANY
    }

    public class TagMatch
    {
        public List<string> AnyOf = new List<string>();
        public List<string> NoneOf = new List<string>();
    }

    public class DifficultyScalingConfig
    {
        public decimal IntelligenceMultiplier = 1.0m;
        public decimal TemperatureMultiplier = 1.0m;
        public decimal MistakeChance = 0.0m;
    }

    /// <summary>data/Profiles/*.brain.json</summary>
    public class BrainProfile
    {
        public string Id;
        public string Description;
        public List<ConsiderationConfig> Considerations = new List<ConsiderationConfig>();
        public List<TargetFilterConfig> TargetFilters = new List<TargetFilterConfig>();
        public Dictionary<string, decimal> AbilityCategoryBias = new Dictionary<string, decimal>();
        public decimal Temperature = 0.2m;
        public DifficultyScalingConfig DifficultyScaling = new DifficultyScalingConfig();
        /// <summary>Focus policy: SMART (commit to kill/pierce), MAX, VANILLA_RANDOM, NONE.</summary>
        public string FocusPolicy = "SMART";
    }

    /// <summary>data/Doctrines/*.doctrine.json — delta layer over a resolved profile.</summary>
    public class DoctrineConfig
    {
        public string Id;
        public string Description;
        public Dictionary<string, decimal> ConsiderationWeightDeltas = new Dictionary<string, decimal>();
        public List<ConsiderationConfig> ConsiderationOverrides = new List<ConsiderationConfig>();
        public Dictionary<string, decimal> AbilityCategoryBiasDeltas = new Dictionary<string, decimal>();
        public decimal TemperatureDelta = 0.0m;
        public List<string> ReactionRefs = new List<string>();
    }

    public class AssignmentRule
    {
        public int Priority;
        public AssignmentMatch Match = new AssignmentMatch();
        public string ProfileId;
        public string DoctrineId;
    }

    public class AssignmentMatch
    {
        public string CharacterId;
        public TagMatch Tags;
        public TagMatch BaseType;
        public string AiBehaviour;
        public bool Default;
    }

    public class AssignmentConfig
    {
        public List<AssignmentRule> Rules = new List<AssignmentRule>();
    }

    /// <summary>Global knobs mirrored from BepInEx config ([Difficulty] section).</summary>
    public class GlobalDifficultyKnobs
    {
        public decimal GlobalIntelligenceScalar = 1.0m;
        public decimal GlobalTemperatureMultiplier = 1.0m;
        public decimal GlobalMistakeChanceAdd = 0.0m;
    }

    /// <summary>Consideration ids — the fixed engine catalogue (SPEC §4.1).</summary>
    public static class ConsiderationIds
    {
        public const string ExpectedDamage    = "WB_CONSIDER_EXPECTED_DAMAGE";
        public const string KillSecure        = "WB_CONSIDER_KILL_SECURE";
        public const string FocusFire         = "WB_CONSIDER_FOCUS_FIRE";
        public const string Threat            = "WB_CONSIDER_THREAT";
        public const string TargetSquishiness = "WB_CONSIDER_TARGET_SQUISHINESS";
        public const string StatusValue       = "WB_CONSIDER_STATUS_VALUE";
        public const string AllyInDanger      = "WB_CONSIDER_ALLY_IN_DANGER";
        public const string ActionEconomy     = "WB_CONSIDER_ACTION_ECONOMY";
        public const string RowPositionValue  = "WB_CONSIDER_ROW_POSITION_VALUE";
        public const string SelfPreservation  = "WB_CONSIDER_SELF_PRESERVATION";
        public const string RangeSafety       = "WB_CONSIDER_RANGE_SAFETY";
        public const string TendencyMatch     = "WB_CONSIDER_TENDENCY_MATCH";
        public const string FocusEfficiency   = "WB_CONSIDER_FOCUS_EFFICIENCY";
    }
}
