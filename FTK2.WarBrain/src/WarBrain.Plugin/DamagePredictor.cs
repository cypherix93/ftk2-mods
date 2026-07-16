using System;
using WarBrain.Core;

namespace WarBrain.Plugin
{
    /// <summary>
    /// Closed-form E[final damage] / P(kill) for one (attacker, ability, target),
    /// mirroring the verified resolution pipeline (deep-dive §2):
    /// binomial slot rolls at the entity's real combat roll stat, focus auto-successes
    /// + GetFocusedStatValue chance bonus, crit (CRT + 5%/focus, pre-mitigation),
    /// flat DEF/RES subtraction, pierce on PERFECT for IsBlockable=false, player dodge.
    /// Same math as the sandbox oracle — validated there against the sim engine.
    /// </summary>
    public sealed class DamagePredictor
    {
        public int Rolls { get; private set; }

        private int _baseRollStat;      // combat roll stat incl. per-ability ACC, before focus bonus
        private int _minDmg, _maxDmg;
        private decimal _minScale;
        private int _mitigation;        // target DEF or RES
        private bool _blockable;
        private decimal _dodge;
        private int _targetHp;
        private int _crt, _crtd;

        public static DamagePredictor For(Entity attacker, AbilityAction action, CombatAbilityConfig config, Entity target)
        {
            try
            {
                if (!config.RequiresSkillRoll) return null;
                var change = InteractableHelper.GetChangeStatActionOfAbility(config);
                if (change == null || change.Stat != "HP" || change.FlatValue.HasValue) return null;

                var cc = attacker.Get<CharacterComponent>();
                var thing = string.IsNullOrEmpty(action.ThingId)
                    ? EquipmentHelper.GetUnarmedWeapon(cc)
                    : InventoryHelper.GetCharacterThingByID(attacker, action.ThingId);
                if (thing == null) return null;

                var roll = InventoryHelper.GetSkillRollData(thing, action.AbilityName);
                if (roll == null || roll.Rolls <= 0) return null;

                var filter = CharacterHelper.IgnoreEquipmentStats(attacker)
                    ? eGetStatEquippedFilters.ALL : eGetStatEquippedFilters.NONE;
                var tile = VenueHelper.GetTileEntityOfCharacter(attacker, RouterHelper.Env.GameRun.CombatState.Entities);
                int stat = CharacterHelper.GetCombatStat(attacker, attacker, tile, roll.Stat, filter, pForAbility: true);

                decimal dmgMult = InteractableHelper.GetTargetCharacterDamageMultiplier(attacker, target);
                var (min, max) = CharacterHelper.GetMinAndMaxDamageOfAbilityForCharacter(
                    attacker, thing.ConfigName, action.AbilityName, dmgMult, 0);

                bool magical = change.Type == eDamageType.MAGICAL;
                return new DamagePredictor
                {
                    Rolls = roll.Rolls,
                    _baseRollStat = stat + roll.ACC,
                    _minDmg = min,
                    _maxDmg = max,
                    _minScale = attacker.Has<PlayerComponent>() ? roll.MinValue * stat * 0.01m : 0m,
                    _mitigation = CharacterHelper.GetStat(target,
                        magical ? eCharacterStats.RES : eCharacterStats.DEF,
                        CharacterHelper.IgnoreEquipmentStats(target)),
                    _blockable = change.IsBlockable,
                    _dodge = Curve.Clamp01(CharacterHelper.GetStat(target, eCharacterStats.EVD) / 100m),
                    _targetHp = CharacterHelper.GetHealth(target),
                    _crt = CharacterHelper.GetStat(attacker, eCharacterStats.CRT),
                    _crtd = CharacterHelper.GetStat(attacker, eCharacterStats.CRTD)
                };
            }
            catch
            {
                return null; // unpredictable ability → facts stay 0, candidate still legal
            }
        }

        public Prediction Predict(int focus)
        {
            focus = Math.Max(0, Math.Min(focus, Rolls));
            int rollStat = CharacterHelper.GetFocusedStatValue(_baseRollStat, focus);
            decimal p = rollStat / 100m;
            int freeRolls = Rolls - focus;

            decimal critChance = Math.Min(1m, (_crt + focus * 5) / 100m);
            decimal critRatio = 0.15m + _crtd * 0.01m;

            decimal expected = 0m, killProb = 0m, perfectProb = 0m;

            for (int s = 0; s <= freeRolls; s++)
            {
                decimal ps = Binomial(freeRolls, s, p);
                int successes = focus + s;
                bool perfect = successes == Rolls;
                bool critFail = successes == 0;
                if (perfect) perfectProb += ps;
                if (critFail && _minDmg == 0) continue;

                decimal ratio = Math.Round((decimal)successes / Rolls, 2);
                decimal value = Math.Max(_minScale, ratio);
                int dmg = (int)Math.Round(_minDmg + (_maxDmg - _minDmg) * value);
                int critDmg = dmg + (int)Math.Max(1m, Math.Round(dmg * critRatio));

                bool pierces = !_blockable && perfect;
                int mit = pierces ? 0 : _mitigation;
                int netNormal = Math.Max(0, dmg - mit);
                int netCrit = Math.Max(0, critDmg - mit);

                decimal hit = 1m - _dodge; // player targets always dodge-eligible (verified)

                expected += ps * hit * ((1m - critChance) * netNormal + critChance * netCrit);
                if (netNormal >= _targetHp) killProb += ps * hit * (1m - critChance);
                if (netCrit >= _targetHp) killProb += ps * hit * critChance;
            }

            return new Prediction(expected, Curve.Clamp01(killProb), perfectProb);
        }

        public sealed class Prediction
        {
            public readonly decimal ExpectedDamage;
            public readonly decimal KillProbability;
            public readonly decimal PerfectProbability;
            public Prediction(decimal e, decimal k, decimal p) { ExpectedDamage = e; KillProbability = k; PerfectProbability = p; }
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
}
