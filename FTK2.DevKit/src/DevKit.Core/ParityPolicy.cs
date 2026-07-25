using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>
    /// <c>[Multiplayer] OnParityMismatch</c> policy (docs/MULTIPLAYER.md R1, SPEC §5).
    /// </summary>
    public enum ParityMismatchPolicy
    {
        /// <summary>Banner only; the session continues unmodified.</summary>
        WarnOnly = 0,

        /// <summary>DEFAULT: banner + the diverged mod's SafeMode engages (state-mutating features off, presentation kept - R4).</summary>
        WarnAndSafeMode = 1,

        /// <summary>The session refuses to start/continue at all.</summary>
        Block = 2,
    }

    /// <summary>What ParityService should actually do about a set of verdicts.</summary>
    public sealed class ParityDecision
    {
        internal ParityDecision(ParityMismatchPolicy policy, bool hasMismatch, bool showWarning,
            bool engageSafeMode, bool blockSession, string[] affectedGuids, string bannerText)
        {
            Policy = policy;
            HasMismatch = hasMismatch;
            ShowWarning = showWarning;
            EngageSafeMode = engageSafeMode;
            BlockSession = blockSession;
            AffectedGuids = affectedGuids ?? new string[0];
            BannerText = bannerText ?? string.Empty;
        }

        public ParityMismatchPolicy Policy { get; private set; }
        public bool HasMismatch { get; private set; }

        /// <summary>Show the prominent on-screen banner (true under every policy when there is a mismatch).</summary>
        public bool ShowWarning { get; private set; }

        /// <summary>Engage SafeMode for <see cref="AffectedGuids"/> (WarnAndSafeMode only).</summary>
        public bool EngageSafeMode { get; private set; }

        /// <summary>Refuse to start/continue the session (Block only).</summary>
        public bool BlockSession { get; private set; }

        /// <summary>Guids of the diverged mods, ordinal-sorted.</summary>
        public string[] AffectedGuids { get; private set; }

        /// <summary>Full banner text: one line per divergence plus the policy footer.</summary>
        public string BannerText { get; private set; }
    }

    /// <summary>
    /// Pure decision routine mapping (policy, verdicts) -&gt; <see cref="ParityDecision"/>.
    /// Callback dispatch always happens on a mismatch, under every policy — a mod must be able to
    /// react even under <c>WarnOnly</c> (docs/MULTIPLAYER.md R1: "each mod receives a ParityFailed
    /// callback", stated independently of the policy knob).
    /// </summary>
    public static class ParityPolicyEngine
    {
        public const ParityMismatchPolicy DefaultPolicy = ParityMismatchPolicy.WarnAndSafeMode;

        public static ParityDecision Decide(ParityMismatchPolicy policy, ParityVerdict[] verdicts)
        {
            bool hasMismatch = ParityComparer.HasMismatch(verdicts);
            if (!hasMismatch)
            {
                return new ParityDecision(policy, false, false, false, false, new string[0], string.Empty);
            }

            string[] affected = ParityComparer.MismatchedGuids(verdicts);
            StringBuilder banner = new StringBuilder();
            banner.Append("MULTIPLAYER PARITY MISMATCH - desync likely.");
            string details = ParityComparer.Summarize(verdicts);
            if (details.Length > 0)
            {
                banner.Append('\n');
                banner.Append(details);
            }
            banner.Append('\n');
            banner.Append(PolicyFooter(policy, affected));

            bool engageSafeMode = policy == ParityMismatchPolicy.WarnAndSafeMode;
            bool block = policy == ParityMismatchPolicy.Block;
            return new ParityDecision(policy, true, true, engageSafeMode, block, affected, banner.ToString());
        }

        /// <summary>Culture-invariant, case-insensitive policy-name parsing (BepInEx config knob).</summary>
        public static bool TryParsePolicy(string name, out ParityMismatchPolicy policy)
        {
            policy = DefaultPolicy;
            if (string.IsNullOrEmpty(name)) return false;
            string trimmed = name.Trim();
            // Ordinal comparisons only: never ToLower()/ToUpper() a knob value, or a Turkish-locale
            // peer parses the config differently from everyone else in the session.
            if (string.Equals(trimmed, "WarnOnly", StringComparison.OrdinalIgnoreCase))
            {
                policy = ParityMismatchPolicy.WarnOnly;
                return true;
            }
            if (string.Equals(trimmed, "WarnAndSafeMode", StringComparison.OrdinalIgnoreCase))
            {
                policy = ParityMismatchPolicy.WarnAndSafeMode;
                return true;
            }
            if (string.Equals(trimmed, "Block", StringComparison.OrdinalIgnoreCase))
            {
                policy = ParityMismatchPolicy.Block;
                return true;
            }
            return false;
        }

        public static string PolicyName(ParityMismatchPolicy policy)
        {
            switch (policy)
            {
                case ParityMismatchPolicy.WarnOnly: return "WarnOnly";
                case ParityMismatchPolicy.Block: return "Block";
                default: return "WarnAndSafeMode";
            }
        }

        public static string[] PolicyNames()
        {
            return new string[] { "WarnOnly", "WarnAndSafeMode", "Block" };
        }

        private static string PolicyFooter(ParityMismatchPolicy policy, string[] affected)
        {
            string list = affected.Length == 0 ? "(none)" : string.Join(", ", affected);
            switch (policy)
            {
                case ParityMismatchPolicy.WarnOnly:
                    return string.Format(CultureInfo.InvariantCulture,
                        "Policy WarnOnly: session continues unmodified. Diverged: {0}.", list);
                case ParityMismatchPolicy.Block:
                    return string.Format(CultureInfo.InvariantCulture,
                        "Policy Block: the session will not start/continue. Diverged: {0}.", list);
                default:
                    return string.Format(CultureInfo.InvariantCulture,
                        "Policy WarnAndSafeMode: SafeMode engaged for {0} - state-mutating features are disabled for this session.",
                        list);
            }
        }
    }
}
