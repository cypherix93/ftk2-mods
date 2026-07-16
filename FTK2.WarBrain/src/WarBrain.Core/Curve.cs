using System;

namespace WarBrain.Core
{
    /// <summary>
    /// Curve math per SPEC §4.2. Input and output clamped to [0,1].
    /// decimal-only arithmetic: cross-peer deterministic (MP lockstep requirement,
    /// see docs/research/battle-ai-deep-dive.md §3).
    /// </summary>
    public static class Curve
    {
        public static decimal Evaluate(CurveConfig cfg, decimal raw)
        {
            raw = Clamp01(raw);
            if (cfg == null) return raw;
            switch (cfg.Type)
            {
                case CurveType.LINEAR:
                    return raw;
                case CurveType.QUADRATIC:
                    return Clamp01(Pow(raw, cfg.Exponent));
                case CurveType.LOGISTIC:
                    return Clamp01(1.0m / (1.0m + Exp(-cfg.Steepness * (raw - cfg.Midpoint))));
                case CurveType.STEP:
                    return raw >= cfg.Threshold ? 1.0m : 0.0m;
                default:
                    return raw;
            }
        }

        public static decimal Clamp01(decimal v) => v < 0m ? 0m : (v > 1m ? 1m : v);

        /// <summary>Deterministic decimal power for base in [0,1]: exp(e*ln(b)) via series.</summary>
        public static decimal Pow(decimal b, decimal e)
        {
            if (b <= 0m) return 0m;
            if (b >= 1m) return 1m;
            return Exp(e * Ln(b));
        }

        /// <summary>Deterministic decimal exp via Taylor series (argument range used: ~[-40, 40]).</summary>
        public static decimal Exp(decimal x)
        {
            if (x < -40m) return 0m;      // exp(-40) ~ 4e-18: below scoring resolution
            if (x > 40m) x = 40m;         // avoid overflow; softmax normalizes anyway
            // range-reduce: exp(x) = exp(k) * exp(r), k integer, |r| <= 0.5
            int k = (int)Math.Round(x);
            decimal r = x - k;
            decimal expR = 1m, term = 1m;
            for (int i = 1; i <= 12; i++)
            {
                term *= r / i;
                expR += term;
            }
            decimal expK = 1m;
            decimal e = 2.718281828459045235m;
            if (k > 0) for (int i = 0; i < k; i++) expK *= e;
            else for (int i = 0; i < -k; i++) expK /= e;
            return expK * expR;
        }

        /// <summary>Deterministic decimal natural log for x in (0,1] via atanh series.</summary>
        public static decimal Ln(decimal x)
        {
            if (x <= 0m) return -40m;
            // normalize x into [0.5, 1.5) using ln(x) = ln(x*2^n) - n*ln2
            decimal ln2 = 0.6931471805599453094m;
            int n = 0;
            while (x < 0.5m) { x *= 2m; n++; }
            while (x >= 1.5m) { x /= 2m; n--; }
            // atanh series: ln(x) = 2*atanh((x-1)/(x+1))
            decimal y = (x - 1m) / (x + 1m);
            decimal y2 = y * y, sum = 0m, t = y;
            for (int i = 1; i <= 19; i += 2)
            {
                sum += t / i;
                t *= y2;
            }
            return 2m * sum - n * ln2;
        }
    }
}
