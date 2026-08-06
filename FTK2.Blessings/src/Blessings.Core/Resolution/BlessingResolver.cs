using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blessings.Core.Model;

namespace Blessings.Core.Resolution
{
    /// <summary>
    /// Deterministic <c>[Blessings] Mode</c> resolution (SPEC §3.4). No <c>System.Random</c>,
    /// no <c>UnityEngine.Random</c>, no constructed <c>GameRandom</c>, no wall clock -- a pure function
    /// of (registry, mode string, MapGenSeed, ConfigName), invariant-culture throughout, so every peer
    /// that ran the same pack + config computes byte-identical results (§9.3).
    /// </summary>
    public static class BlessingResolver
    {
        /// <summary>The exact salt string from §3.4: <c>h = SHA256("BLSS_OFFER_V1|" + MapGenSeed + "|" + ConfigName)</c>.</summary>
        public const string HashSaltPrefix = "BLSS_OFFER_V1|";

        /// <summary>
        /// Resolves <paramref name="modeRaw"/> against <paramref name="registry"/>.
        /// <list type="bullet">
        /// <item><c>"Disabled"</c> (case-insensitive) or empty/null -&gt; <see cref="ResolutionKind.Disabled"/>.</item>
        /// <item><c>"Random"</c> (case-insensitive) -&gt; deterministic weighted pick over Enabled entries,
        /// in authored roster order (§3.4). No enabled entries -&gt; Disabled (never throws).</item>
        /// <item>anything else -&gt; treated as a literal roster <see cref="BlessingEntry.Id"/> (ordinal match).
        /// No match, or a match with <c>Enabled=false</c> -&gt; Disabled ("never guess", §3.4)."</item>
        /// </list>
        /// </summary>
        public static ResolutionResult Resolve(BlessingsRegistry registry, string modeRaw, int mapGenSeed, string configName)
        {
            var result = new ResolutionResult { ModeRaw = modeRaw };

            if (registry == null)
            {
                result.Kind = ResolutionKind.Disabled;
                result.Message = "blessings registry is unavailable -- treated as Disabled.";
                return result;
            }

            string mode = modeRaw == null ? "" : modeRaw.Trim();

            if (mode.Length == 0 || string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = ResolutionKind.Disabled;
                return result;
            }

            if (string.Equals(mode, "Random", StringComparison.OrdinalIgnoreCase))
            {
                var enabled = EnabledInAuthoredOrder(registry);
                if (enabled.Count == 0)
                {
                    result.Kind = ResolutionKind.Disabled;
                    result.Message = "Mode=Random but no roster entries are Enabled -- treated as Disabled.";
                    return result;
                }

                ulong hash = ComputeOfferHash(mapGenSeed, configName);
                long sum = SumWeights(enabled);
                long roll = (long)(hash % (ulong)sum) + 1;

                result.Kind = ResolutionKind.Random;
                result.Resolved = WalkWeighted(enabled, roll);
                return result;
            }

            // Literal id.
            var match = registry.FindById(mode);
            if (match == null)
            {
                result.Kind = ResolutionKind.Disabled;
                result.Message = "Mode='" + mode + "' does not match any roster blessing id -- treated as Disabled (never guess).";
                return result;
            }
            if (!match.Enabled)
            {
                result.Kind = ResolutionKind.Disabled;
                result.Message = "Mode='" + mode + "' matches a roster entry with Enabled=false -- treated as Disabled.";
                return result;
            }

            result.Kind = ResolutionKind.Literal;
            result.Resolved = match;
            return result;
        }

        /// <summary>Entries with <see cref="BlessingEntry.Enabled"/>=true, in the registry's authored order.</summary>
        public static List<BlessingEntry> EnabledInAuthoredOrder(BlessingsRegistry registry)
        {
            var list = new List<BlessingEntry>();
            if (registry == null) return list;
            for (int i = 0; i < registry.Blessings.Count; i++)
                if (registry.Blessings[i].Enabled) list.Add(registry.Blessings[i]);
            return list;
        }

        /// <summary>Sum of <c>Max(1, Weight)</c> over <paramref name="entries"/> (EOR L832 clamp semantics).</summary>
        public static long SumWeights(IReadOnlyList<BlessingEntry> entries)
        {
            long sum = 0;
            if (entries == null) return sum;
            for (int i = 0; i < entries.Count; i++)
                sum += Math.Max(1, entries[i].Weight);
            return sum;
        }

        /// <summary>
        /// EOR's <c>PickRandomBlessing</c> walk (L830-844), minus the <c>GameRandom</c> draw: subtracts
        /// <c>Max(1, Weight)</c> from <paramref name="roll"/> for each entry, in the given order, until it
        /// drops to <c>&lt;= 0</c>. <paramref name="roll"/> is expected to be a 1-based value in
        /// <c>[1, SumWeights(entries)]</c>; a caller-supplied out-of-range roll degrades to returning the
        /// last entry (defensive, never throws/returns null for a non-empty list) so a boundary-test typo
        /// fails loudly in its assertion rather than via a NullReferenceException three frames away.
        /// </summary>
        public static BlessingEntry WalkWeighted(IReadOnlyList<BlessingEntry> entries, long roll)
        {
            if (entries == null || entries.Count == 0) return null;
            long remaining = roll;
            for (int i = 0; i < entries.Count; i++)
            {
                remaining -= Math.Max(1, entries[i].Weight);
                if (remaining <= 0) return entries[i];
            }
            return entries[entries.Count - 1];
        }

        /// <summary>
        /// <c>h = SHA256("BLSS_OFFER_V1|" + MapGenSeed.ToString(InvariantCulture) + "|" + ConfigName)</c>;
        /// the first 8 bytes of the digest, interpreted big-endian, as a <see cref="ulong"/> (§3.4).
        /// </summary>
        public static ulong ComputeOfferHash(int mapGenSeed, string configName)
        {
            string input = HashSaltPrefix + mapGenSeed.ToString(CultureInfo.InvariantCulture) + "|" + (configName ?? "");
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(input));

            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | digest[i];
            return value;
        }
    }
}
