using System;
using System.Collections.Generic;
using System.Linq;
using WarBrain.Core;

namespace WarBrain.Plugin
{
    /// <summary>
    /// Builds WarBrain candidates from live game state and converts the engine's pick
    /// into a CombatDecisionData. All game reads are the same public helpers vanilla
    /// AIHelper/ForceAiDecision use, so candidate legality mirrors vanilla exactly.
    /// All randomness flows through CombatState.Random (MP lockstep).
    /// </summary>
    public static class GameStateAdapter
    {
        private static readonly DecisionEngine Engine = new DecisionEngine();

        // per-battle caches (reset by CombatState.Create postfix)
        private static readonly Dictionary<string, (string profile, string doctrine)> _resolvedCache
            = new Dictionary<string, (string, string)>();

        public static void OnBattleStart() => _resolvedCache.Clear();

        public static CombatDecisionData Decide(Entity entity, GameRunData gameRun)
        {
            var combatState = gameRun.CombatState;
            if (combatState == null) return null;

            // vanilla secondary-action pass (reload / move off hazardous tile) — keep verbatim
            if (AIHelper.TryConsiderSecondaryAction(entity, gameRun, (eSkills.NONE, 0), out var secondary))
                return secondary;

            var (profile, doctrine) = ResolveBrain(entity);
            if (profile == null) return null;

            var abilities = new HashSet<AbilityAction>(
                CombatHelper.GetAbilities(entity, pMainHandOnly: true, pIncludeDefaultAbilities: true,
                    pIncludeConsumables: true, pIgnoreConfuseAbilites: true))
                .Where(a => CombatHelper.IsUsableAbility(gameRun, entity, a.AbilityName))
                .ToList();
            if (abilities.Count == 0) return null;

            var candidates = new List<Candidate>();
            var lookup = new List<(AbilityAction action, CombatAbilityConfig config, Entity tile, int focus)>();
            bool sawUnmodelable = false;

            var allies = combatState.Entities.Where(e =>
                e != entity && e.Has<CombatComponent>() && CharacterHelper.IsEnemy(e) && !CharacterHelper.IsDead(e)).ToList();
            decimal lowestAllyHp = 1m;
            foreach (var ally in allies)
            {
                decimal frac = HpFraction(ally);
                if (frac < lowestAllyHp) lowestAllyHp = frac;
            }
            decimal selfHp = HpFraction(entity);

            foreach (var action in abilities)
            {
                var name = action.AbilityName;
                if (name == "SKIP_TURN" || name == "BASIC_MOVE" || name == "FLEE" || name == "SMOKE_FLEE") continue;

                var config = InteractableHelper.GetAbilityConfig(name, pAllowNull: true);
                if (config == null) continue;

                // M1 limitation: empty-tile-occupancy abilities (summons, teleports) are not
                // modeled; if this enemy relies on them, defer the whole turn to vanilla so
                // summoners keep summoning.
                if (config.TileOccupancy == eTileOccupancies.EMPTY) { sawUnmodelable = true; continue; }

                var category = (AbilityCategory)(int)AIHelper.GetAbilityCategory(name);
                var tiles = GetLegalTiles(entity, combatState, config);

                foreach (var tile in tiles)
                {
                    var occupant = OccupantOf(tile, combatState);
                    if (occupant == null || CharacterHelper.IsDead(occupant)) continue;

                    bool targetIsOpponent = occupant.Get<CharacterComponent>().GroupIndex
                                            != entity.Get<CharacterComponent>().GroupIndex;

                    if (targetIsOpponent)
                        BuildAttackCandidates(entity, action, config, category, tile, occupant,
                            lowestAllyHp, selfHp, profile, candidates, lookup);
                    else
                        BuildSupportCandidate(entity, action, config, category, tile, occupant,
                            selfHp, candidates, lookup);
                }
            }

            if (candidates.Count == 0) return null;              // nothing modelable → vanilla
            if (sawUnmodelable && candidates.Count < abilities.Count) { /* partial coverage is fine — summd abilities were skipped only for this actor if it had others */ }

            var picked = Engine.Decide(candidates, profile, doctrine, WarBrainPlugin.Knobs(),
                reactionWeightDeltas: null, targetPriorityDeltas: null,
                rng: () => combatState.Random.NextNormalizedDecimal(),
                out var allScored);

            if (picked == null) return null;
            int idx = candidates.IndexOf(picked.Candidate);
            var (chosenAction, chosenConfig, chosenTile, focus) = lookup[idx];

            LogDecision(entity, profile, doctrine, picked, allScored);

            // mirror ForceAiDecision's focus bookkeeping
            var cc = entity.Get<CharacterComponent>();
            cc.CachedFocus = chosenConfig.RequiresSkillRoll && chosenConfig.IsFocusable ? focus : 0;

            return new CombatDecisionData
            {
                Ability = chosenAction,
                Position = chosenTile.Get<VenueComponent>().TilePosition,
                FocusUsed = cc.CachedFocus
            };
        }

        // ------------------------------------------------------------------

        private static void BuildAttackCandidates(Entity entity, AbilityAction action, CombatAbilityConfig config,
            AbilityCategory category, Entity tile, Entity target, decimal lowestAllyHp, decimal selfHp,
            BrainProfile profile, List<Candidate> candidates,
            List<(AbilityAction, CombatAbilityConfig, Entity, int)> lookup)
        {
            var predictor = DamagePredictor.For(entity, action, config, target);
            var baseline = predictor?.Predict(0);

            foreach (int focus in FocusOptions(profile, entity, config, predictor))
            {
                var pred = focus == 0 ? baseline : predictor?.Predict(focus);
                var c = new Candidate
                {
                    AbilityId = action.AbilityName,
                    Category = category,
                    TargetId = target.Guid,
                    FocusSpend = focus,
                    ExpectedDamageFractionOfTargetHp = pred == null ? 0m
                        : Curve.Clamp01(SafeDiv(pred.ExpectedDamage, CharacterHelper.GetHealth(target))),
                    KillProbability = pred?.KillProbability ?? 0m,
                    TargetMissingHpFraction = 1m - HpFraction(target),
                    TargetThreatNormalized = ThreatOf(target),
                    TargetSquishiness = SquishinessOf(config, target),
                    StatusValue = HasAddStatus(config) ? 0.5m : 0m,
                    LowestReachableAllyHpFraction = lowestAllyHp,
                    SelfHpFraction = selfHp,
                    RangeSafety = config.IsRanged ? 1m : 0.3m,
                    FocusEfficiency = pred == null || baseline == null || focus == 0 ? 0m
                        : Curve.Clamp01((pred.ExpectedDamage - baseline.ExpectedDamage) / Math.Max(1m, baseline.ExpectedDamage)),
                    TendencyMatchRank = 0m, // M2: reuse eAiTendencies ordering
                    TargetTags = TagsOf(target),
                    TargetBaseType = BaseTypeOf(target),
                    TargetHpFraction = HpFraction(target),
                    TargetRow = RowOf(tile)
                };
                candidates.Add(c);
                lookup.Add((action, config, tile, focus));
            }
        }

        private static void BuildSupportCandidate(Entity entity, AbilityAction action, CombatAbilityConfig config,
            AbilityCategory category, Entity tile, Entity ally, decimal selfHp,
            List<Candidate> candidates, List<(AbilityAction, CombatAbilityConfig, Entity, int)> lookup)
        {
            candidates.Add(new Candidate
            {
                AbilityId = action.AbilityName,
                Category = category,
                TargetId = ally.Guid,
                FocusSpend = 0,
                StatusValue = HasAddStatus(config) ? 0.5m : 0m,
                LowestReachableAllyHpFraction = HpFraction(ally),
                SelfHpFraction = selfHp,
                RangeSafety = config.IsRanged ? 1m : 0.3m,
                TargetTags = TagsOf(ally),
                TargetBaseType = BaseTypeOf(ally),
                TargetHpFraction = HpFraction(ally),
                TargetRow = RowOf(tile)
            });
            lookup.Add((action, config, tile, 0));
        }

        private static IEnumerable<int> FocusOptions(BrainProfile profile, Entity entity,
            CombatAbilityConfig config, DamagePredictor predictor)
        {
            if (!config.RequiresSkillRoll || !config.IsFocusable || predictor == null) return new[] { 0 };
            int max = Math.Min(CharacterHelper.GetFocus(entity), predictor.Rolls);
            if (max <= 0) return new[] { 0 };

            string policy = string.IsNullOrEmpty(WarBrainPlugin.DefaultFocusPolicy.Value)
                ? profile.FocusPolicy : WarBrainPlugin.DefaultFocusPolicy.Value;
            switch (policy)
            {
                case "NONE": return new[] { 0 };
                case "MAX": return new[] { max };
                case "VANILLA_RANDOM": return new[] { 0 }; // vanilla parity mode: engine still scores, focus stays conservative
                case "SMART":
                default:
                    var opts = new List<int>(max + 1);
                    for (int f = 0; f <= max; f++) opts.Add(f);
                    return opts;
            }
        }

        private static (BrainProfile, DoctrineConfig) ResolveBrain(Entity entity)
        {
            var cc = entity.Get<CharacterComponent>();
            if (!_resolvedCache.TryGetValue(entity.Guid, out var resolved))
            {
                var subject = new AssignmentSubject
                {
                    CharacterId = cc.ConfigName,
                    Tags = TagsOf(entity),
                    BaseType = BaseTypeOf(entity),
                    AiBehaviour = BehaviourOf(cc)
                };
                resolved = WarBrainPlugin.Assignments.Resolve(subject, WarBrainPlugin.DefaultProfileId.Value);
                _resolvedCache[entity.Guid] = resolved;
                if (WarBrainPlugin.VerboseLogging.Value)
                    WarBrainPlugin.Log.LogDebug($"[WarBrain] {cc.ConfigName} → profile={resolved.Item1} doctrine={resolved.Item2 ?? "none"}");
            }

            WarBrainPlugin.Profiles.TryGetValue(resolved.Item1 ?? string.Empty, out var profile);
            DoctrineConfig doctrine = null;
            if (resolved.Item2 != null) WarBrainPlugin.Doctrines.TryGetValue(resolved.Item2, out doctrine);
            return (profile, doctrine);
        }

        private static List<Entity> GetLegalTiles(Entity entity, CombatState combatState, CombatAbilityConfig config)
        {
            if (!entity.Has<VenueComponent>()) return new List<Entity>();
            bool allowInanimate = CharacterHelper.IsInanimate(entity)
                || entity.Get<CharacterComponent>().GroupIndex != CombatHelper.GetTargetGroup(entity, config.Target);
            return VenueHelper.GetTargetableTiles(entity, combatState.Entities, config.TileOccupancy,
                config.OriginRowPosition, config.TargetRowPosition, config.Target, config.TargetArea,
                config.Actions, allowInanimate);
        }

        private static Entity OccupantOf(Entity tile, CombatState combatState)
        {
            var pos = tile.Get<VenueComponent>().TilePosition;
            return combatState.Entities.FirstOrDefault(e =>
                e.Has<CombatComponent>() && e.Has<VenueComponent>() &&
                (Equals(e.Get<VenueComponent>().TilePosition, pos) ||
                 (e.Get<VenueComponent>().OccupiedTiles != null && e.Get<VenueComponent>().OccupiedTiles.Contains(pos))));
        }

        // ---- fact helpers ----

        private static decimal HpFraction(Entity e)
        {
            int max = CharacterHelper.GetMaxHealth(e);
            return max <= 0 ? 0m : Curve.Clamp01((decimal)CharacterHelper.GetHealth(e) / max);
        }

        private static decimal ThreatOf(Entity e)
        {
            var cfgName = e.Get<CharacterComponent>().ConfigName;
            if (Env.Configs.Characters.TryGetValue(cfgName, out var cfg))
                return Curve.Clamp01(cfg.Threat / 9m);
            // player characters: approximate threat from ATK stat (no Threat config field)
            return Curve.Clamp01(CharacterHelper.GetStat(e, eCharacterStats.ATK) / 80m);
        }

        private static decimal SquishinessOf(CombatAbilityConfig config, Entity target)
        {
            var change = InteractableHelper.GetChangeStatActionOfAbility(config);
            int mit = change == null ? 0 : change.Type == eDamageType.MAGICAL
                ? CharacterHelper.GetStat(target, eCharacterStats.RES)
                : CharacterHelper.GetStat(target, eCharacterStats.DEF);
            int evd = CharacterHelper.GetStat(target, eCharacterStats.EVD);
            return Curve.Clamp01(1m - (mit * 2 + evd) / 100m);
        }

        private static bool HasAddStatus(CombatAbilityConfig config)
            => config.Actions != null && config.Actions.Any(a => a.Item1 == eCombatActions.ADD_STATUS);

        private static IReadOnlyList<string> TagsOf(Entity e)
        {
            var cfgName = e.Get<CharacterComponent>().ConfigName;
            return Env.Configs.Characters.TryGetValue(cfgName, out var cfg) && cfg.Tags != null
                ? (IReadOnlyList<string>)cfg.Tags : Array.Empty<string>();
        }

        private static string BaseTypeOf(Entity e)
        {
            var cfgName = e.Get<CharacterComponent>().ConfigName;
            return Env.Configs.Characters.TryGetValue(cfgName, out var cfg) ? cfg.BaseType : null;
        }

        private static string BehaviourOf(CharacterComponent cc)
        {
            if (!string.IsNullOrEmpty(cc.TypeArgs) && Env.Configs.Followers.TryGetValue(cc.TypeArgs, out var follower))
                return follower.Behaviour.ToString();
            return "DEFAULT";
        }

        private static string RowOf(Entity tile)
        {
            if (tile.Has<VenueTileComponent>())
                return tile.Get<VenueTileComponent>().RowPositionsType == eTileRowPositions.BACK ? "BACK" : "FRONT";
            return "ANY";
        }

        private static decimal SafeDiv(decimal a, int b) => b <= 0 ? 1m : a / b;

        private static void LogDecision(Entity entity, BrainProfile profile, DoctrineConfig doctrine,
            ScoredCandidate picked, List<ScoredCandidate> allScored)
        {
            if (!WarBrainPlugin.VerboseLogging.Value) return;
            var c = picked.Candidate;
            WarBrainPlugin.Log.LogDebug(
                $"[WarBrain] {entity.Guid} profile={profile.Id} doctrine={doctrine?.Id ?? "none"} " +
                $"candidates={allScored.Count} chosen={c.AbilityId}→{c.TargetId} focus={c.FocusSpend} " +
                $"score={picked.Score:F3} p={picked.Probability:P0}");
            if (!WarBrainPlugin.LogDecisionBreakdown.Value) return;
            foreach (var (id, weight, raw, curved, contribution) in picked.Breakdown)
                WarBrainPlugin.Log.LogDebug($"[WarBrain]   {id} w={weight:F2} raw={raw:F3} curve={curved:F3} → {contribution:F3}");
        }
    }
}
