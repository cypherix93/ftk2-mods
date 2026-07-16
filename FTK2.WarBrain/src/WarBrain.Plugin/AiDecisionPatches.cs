using System;
using HarmonyLib;

namespace WarBrain.Plugin
{
    /// <summary>
    /// Harmony entry points. Contract (SPEC §6): prefix returns false with a built
    /// CombatDecisionData to skip vanilla; returns true on ANY internal failure so
    /// vanilla AIHelper decides that turn (fail-safe).
    /// </summary>
    public static class AiDecisionPatches
    {
        /// <summary>
        /// Re-entrancy latch: when WarBrain decides to defer, vanilla BehaviourAiDecision
        /// internally calls StandardAiDecision — our Standard prefix must pass through.
        /// Single-threaded game loop, so a plain static bool is safe.
        /// </summary>
        private static bool _deferring;

        public static bool BehaviourAiDecision_Prefix(
            Entity pActiveEntity, GameRunData pGameRun, (eSkills, int) pSkillContext, ref CombatDecisionData __result)
            => TryTakeOver(pActiveEntity, pGameRun, ref __result);

        public static bool StandardAiDecision_Prefix(
            Entity pActiveEntity, GameRunData pGameRun, (eSkills, int) pSkillContext, ref CombatDecisionData __result)
            => TryTakeOver(pActiveEntity, pGameRun, ref __result);

        private static bool TryTakeOver(Entity entity, GameRunData gameRun, ref CombatDecisionData result)
        {
            if (_deferring) return true;
            if (!WarBrainPlugin.Enabled.Value || !WarBrainPlugin.EnableScoringEngine.Value) return true;
            if (WarBrainPlugin.Profiles.Count == 0) return true;

            try
            {
                // Player-side AI (PlayerPartyHasAI) is out of scope — enemies only.
                if (!CharacterHelper.IsEnemy(entity)) return true;

                var decision = GameStateAdapter.Decide(entity, gameRun);
                if (decision == null)
                {
                    _deferring = true;
                    try { return true; }
                    finally { _deferring = false; }
                }
                result = decision;
                return false; // skip vanilla
            }
            catch (Exception e)
            {
                WarBrainPlugin.Log.LogWarning($"[WarBrain] decision failed for {entity}: {e}. Falling back to vanilla for this turn.");
                if (!WarBrainPlugin.FailSafeOnError.Value) throw;
                return true;
            }
        }

        public static void ReloadConfigs_Postfix()
        {
            WarBrainPlugin.Log.LogInfo("[WarBrain] ReloadConfigs detected — hot-reloading WarBrain data.");
            WarBrainPlugin.LoadData();
        }

        public static void CombatStateCreate_Postfix()
        {
            GameStateAdapter.OnBattleStart();
        }
    }

    /// <summary>
    /// [Scaling] layer: flat/multiplicative enemy base-stat adjustments, same shape as
    /// vanilla difficulty EnemyStatMods (GetCharacterBaseStat is exactly where those apply).
    /// MP-safe: pure function of config values, which must be identical on all peers.
    /// </summary>
    public static class ScalingPatches
    {
        public static void GetCharacterBaseStat_Postfix(Entity pCharacterEntity, string pStat, ref int __result)
        {
            if (!WarBrainPlugin.Enabled.Value || !WarBrainPlugin.EnableEnemyScaling.Value) return;
            try
            {
                if (pCharacterEntity.Has<PlayerComponent>() || !CharacterHelper.IsEnemy(pCharacterEntity)) return;
                if (CharacterHelper.IsInanimate(pCharacterEntity)) return;

                switch (pStat)
                {
                    case "ATK":
                        __result = (int)Math.Round(__result * (decimal)WarBrainPlugin.EnemyAtkMultiplier.Value);
                        break;
                    case "HP":
                        __result = (int)Math.Round(__result * (decimal)WarBrainPlugin.EnemyHpMultiplier.Value);
                        break;
                    case "ACC":
                        __result += WarBrainPlugin.EnemyAccuracyAdd.Value;
                        break;
                    case "FOC":
                        if (__result > 0 || WarBrainPlugin.EnemyFocusAdd.Value > 0)
                            __result += WarBrainPlugin.EnemyFocusAdd.Value;
                        break;
                }
            }
            catch
            {
                // entities without CharacterComponent etc. — never break stat reads
            }
        }
    }
}
