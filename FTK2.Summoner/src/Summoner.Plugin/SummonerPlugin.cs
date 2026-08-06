using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Summoner.Core.Diagnostics;
using Summoner.Core.Merge;
using Summoner.Core.Packs;
using Summoner.Core.Parity;
using Summoner.Core.Validation;
using Summoner.Plugin.Adapters;
using Summoner.Plugin.Patches;
using PluginParity = Summoner.Plugin.Parity.ParityRegistration;

namespace Summoner.Plugin
{
    [BepInPlugin(Guid, Name, Version)]
    public class SummonerPlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.summoner";
        public const string Name = "FTK2.Summoner";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static SummonerPlugin Instance;

        // ---- knobs (design §A5) ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<string> AdditionalRoots;
        internal static ConfigEntry<string> OnParityMismatch;

        // ---- immutable in-memory pack model (design §A3.2: read once in Awake(), never mutated) ----
        internal static IReadOnlyList<FollowerPack> Packs = Array.Empty<FollowerPack>();
        internal static string DataHash = "(none)";

        /// <summary>Parsed <c>[Multiplayer] OnParityMismatch</c> policy (MP review B7 fix -- the knob is
        /// now actually consumed, not just logged about). Defaults to <see cref="MismatchPolicy.Block"/>
        /// (design §A5) for an unrecognized value, same as an unset config entry.</summary>
        private static MismatchPolicy _policy = MismatchPolicy.Block;

        /// <summary>
        /// True once any peer has reported a parity mismatch for Summoner this process
        /// (MP review B7 fix). "Session-scoped" in intent -- it is meant to describe the current MP
        /// session, not the whole game process -- but there is currently no session-boundary hook in
        /// Summoner (unlike DevKit's own ParityService.ResetSession) to clear it on a fresh session
        /// starting in the same process, so in practice it behaves like ClassForge's own
        /// process-latching Blocked flag (see FTK2.ClassForge/src/ClassForge.Plugin/ParityBridge.cs and
        /// MP review finding M1) -- a mismatched process stays blocked until restarted. Consulted by
        /// <see cref="Patches.ConfigsMergePatches"/> to refuse FUTURE merges; it does NOT touch
        /// content already merged into <c>Configs</c> earlier in the session -- see the doc comment on
        /// <see cref="OnParityMismatchRow"/> for why that's the correct, honest semantics for a
        /// load-time loader rather than a partial-shutdown lie.
        /// </summary>
        internal static bool Blocked { get; private set; }

        private enum MismatchPolicy { WarnOnly, WarnAndSafeMode, Block }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch; false = no scan, no patch, vanilla untouched.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false,
                "Per-pack/per-entry merge decisions at LogLevel.Debug.");
            AdditionalRoots = Config.Bind("Packs", "AdditionalRoots", "",
                "Comma-separated absolute dirs scanned in addition to <plugin>/data/FollowerPacks.");
            OnParityMismatch = Config.Bind("Multiplayer", "OnParityMismatch", "Block",
                "WarnAndSafeMode | WarnOnly | Block. Summoner's default is Block, overriding the " +
                "repo-wide WarnAndSafeMode default, for every milestone (design §A5) -- the M0 " +
                "merge is load-time, so WarnAndSafeMode and Block both degenerate to the same thing " +
                "here: log loudly and stop merging any FURTHER pack content into Configs. Content " +
                "already merged before the mismatch was detected stays merged (it's save data by the " +
                "time a hired follower exists) -- neither policy unmerges it. WarnOnly logs only and " +
                "never stops merging.");

            _policy = ParsePolicy(OnParityMismatch.Value);

            LoadPacks();
            ApplyPatches();
            RegisterParity();

            Log.LogInfo($"{Name} {Version} loaded. Enabled={Enabled.Value}, packs={Packs.Count}, " +
                        $"followers={Packs.Sum(p => p.Followers.Count)}, dataHash={DataHash}, " +
                        $"onParityMismatch={_policy}");
        }

        private static MismatchPolicy ParsePolicy(string raw)
        {
            if (string.Equals(raw, "WarnOnly", StringComparison.OrdinalIgnoreCase)) return MismatchPolicy.WarnOnly;
            if (string.Equals(raw, "WarnAndSafeMode", StringComparison.OrdinalIgnoreCase)) return MismatchPolicy.WarnAndSafeMode;
            if (string.Equals(raw, "Block", StringComparison.OrdinalIgnoreCase)) return MismatchPolicy.Block;

            Log.LogWarning($"[Summoner] [Multiplayer] OnParityMismatch='{raw}' is not one of WarnAndSafeMode|WarnOnly|Block -- " +
                            "defaulting to Block (design §A5's recommended default).");
            return MismatchPolicy.Block;
        }

        /// <summary>Reads FollowerPacks/ from disk exactly once. Never called again for a hot-reload --
        /// ReloadConfigs re-plans/re-applies from this same immutable list (design §A3.2).</summary>
        internal static void LoadPacks()
        {
            if (!Enabled.Value)
            {
                Packs = Array.Empty<FollowerPack>();
                DataHash = "(disabled)";
                return;
            }

            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var defaultRoot = Path.Combine(pluginDir ?? ".", "data", "FollowerPacks");
                var source = new FileSystemPackSource();
                var codec = new GameJsonCodec();

                var allPacks = new List<FollowerPack>();
                var findings = new List<Finding>();

                foreach (var root in Roots(defaultRoot))
                {
                    if (!Directory.Exists(root))
                    {
                        Log.LogDebug($"[Summoner] pack root not found, skipped: {root}");
                        continue;
                    }

                    var result = PackLoader.Load(source, codec, root);
                    allPacks.AddRange(result.Packs);
                    findings.AddRange(result.Findings);
                }

                var externalCharacterIds = new HashSet<string>(StringComparer.Ordinal);
                var validationFindings = PackValidator.ValidateAll(allPacks, externalCharacterIds);
                // At Awake the game's Configs don't exist yet, so the external character-id set
                // above is necessarily empty — every vanilla-referencing ConfigName would log as a
                // spurious [config_name_unresolved] error here (day-one finding: ~200 red lines on
                // a healthy install). Resolution against the REAL id set happens at merge time
                // (MergeInto → MergePlanner rejects, ConfigsSink logs); only intra-pack findings
                // are meaningful this early.
                findings.AddRange(validationFindings.Where(f => f.Check != "config_name_unresolved"));

                foreach (var f in findings)
                    LogFinding(f);

                Packs = allPacks;

                var hashInput = Packs
                    .Select(p => (p.Manifest.Id, (IReadOnlyList<(string RelPath, byte[] Bytes)>)p.Files))
                    .ToList();
                DataHash = DataHasher.ComputeHash(hashInput);
            }
            catch (Exception e)
            {
                Log.LogError($"[Summoner] pack load failed: {e} -- Summoner defers to vanilla for this pass (fail-safe).");
                Packs = Array.Empty<FollowerPack>();
                DataHash = "(error)";
            }
        }

        /// <summary>Re-plans from the immutable Packs list and applies into the freshly-built Configs
        /// instance handed to us by a LoadConfigs/ReloadConfigs postfix (design §A3). Callers
        /// (ConfigsMergePatches) are responsible for checking <see cref="Blocked"/> before calling this
        /// for anything past the initial boot load (MP review B7 fix) -- this method itself always
        /// merges when called, so a caller that doesn't gate would still apply.</summary>
        internal static void MergeInto(Configs configs)
        {
            if (configs == null) return;

            var existingFollowerIds = new HashSet<string>(configs.Followers?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var existingCharacterIds = new HashSet<string>(configs.Characters?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            var plan = MergePlanner.Plan(Packs, existingFollowerIds, existingCharacterIds);
            ConfigsSink.Apply(plan, configs, Log);

            // Always at Info: this line is the smoke-test signal that followers actually landed in
            // the live Configs (the Awake-time "followers=200" only counts parsed pack entries).
            Log.LogInfo($"[Summoner] merge complete: +{plan.CharacterAdds.Count} characters, " +
                        $"+{plan.FollowerAdds.Count} followers, {plan.Skips.Count} skipped, {plan.Rejects.Count} rejected.");
        }

        /// <summary>
        /// Registers Summoner's parity tuple exactly once, from Awake(), regardless of
        /// <see cref="Enabled"/> (MP review B7 fix). Previously this only ran inside
        /// <see cref="MergeInto"/>, which <c>ConfigsMergePatches</c> short-circuits when Enabled=false --
        /// so a peer with Summoner installed but disabled registered NOTHING and was invisible to the
        /// handshake rather than reported as divergent. Now a disabled peer still registers, with
        /// <c>DataHash="(disabled)"</c> and an empty feature list (both already set by
        /// <see cref="LoadPacks"/> in that case), so a peer expecting Summoner's content sees a real
        /// mismatch instead of silence.
        ///
        /// <c>enabledFeatures</c> carries the sorted set of enabled pack ids and nothing else. Checked
        /// against every gameplay-relevant knob on this plugin (design §A5): <c>VerboseLogging</c> is
        /// cosmetic/local (R4-exempt, per-peer log verbosity never affects simulation);
        /// <c>AdditionalRoots</c> only changes WHICH packs get discovered, and that fully surfaces
        /// through the resulting enabled-pack-id set and <see cref="DataHash"/> already; and
        /// <c>OnParityMismatch</c> is the local mismatch-response policy itself, not a piece of merged
        /// game state to compare -- two peers may legitimately run different values here and still
        /// merge byte-identical content. So the enabled-pack-id tuple is genuinely complete for this
        /// milestone; there are no other gameplay knobs to append (contrast ClassForge's B4 finding,
        /// where feature-toggle knobs like EnableRecipeEngine change simulated behavior independently
        /// of any pack id and therefore DO need to ride along in <c>enabledFeatures</c>).
        /// </summary>
        private static void RegisterParity()
        {
            var enabledPackIds = Packs.Where(p => p.Manifest.Enabled).Select(p => p.Manifest.Id)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            PluginParity.RegisterWithCallback(Guid, Version, DataHash, enabledPackIds, OnParityMismatchRow, Log);
        }

        /// <summary>
        /// DevKit's <c>ParityFailed</c> callback (row layout fixed by position on
        /// <c>ParityVerdict.ToCallbackArgs</c>: <c>[guid, kind, local, remote, peer, message]</c>).
        ///
        /// Policy semantics (MP review B7 fix -- the knob is finally acted on):
        /// <list type="bullet">
        /// <item><b>WarnOnly</b> -- log the mismatch and do nothing else. Merging continues exactly as
        /// before; the operator has explicitly opted into "trust me."</item>
        /// <item><b>WarnAndSafeMode</b> and <b>Block</b> -- log a loud banner AND latch
        /// <see cref="Blocked"/>, which <c>ConfigsMergePatches.ReloadConfigs_Postfix</c> (and, as a
        /// defensive belt-and-braces measure, <c>LoadConfigs_Postfix</c>) checks before calling
        /// <see cref="MergeInto"/> again, so no FURTHER pack content is merged into <c>Configs</c> for
        /// the rest of this process.
        /// </item>
        /// </list>
        /// The distinction between WarnAndSafeMode and Block is intentionally thin here: both stop
        /// future merges. A richer SafeMode (letting some presentation-only subset keep working while
        /// gameplay-affecting merges stop) doesn't apply to Summoner's M0/M1 slice -- everything
        /// FollowerPacks ships (Characters/Followers entries) is gameplay data, there is no
        /// presentation-only category to keep alive, so there's nothing between "merge" and "don't."
        ///
        /// <b>Honesty note, all three policies:</b> content already merged into <c>Configs</c> earlier
        /// in the session (before the mismatch was ever detected) stays merged -- this callback never
        /// unmerges anything. That's a deliberate, documented posture, not an oversight: by the time a
        /// divergent follower could have been hired into a run, it's save data (the character/follower
        /// already exists in the player's roster); ripping the backing <c>Configs</c> entry out from
        /// under it mid-session would orphan that save data (a null/unresolvable character reference)
        /// rather than protect anything. This mirrors ClassForge's own documented Block semantics (see
        /// FTK2.ClassForge/src/ClassForge.Plugin/ParityBridge.cs and ConfigMergePatches.cs, and MP
        /// review finding M2) -- "stop doing more of the divergent thing," not "undo what's done."
        /// </summary>
        private static void OnParityMismatchRow(string[] row)
        {
            try
            {
                var kind = row != null && row.Length > 1 ? (row[1] ?? "") : "";
                if (string.Equals(kind, "Match", StringComparison.Ordinal)) return; // not a divergence.

                var peer = row != null && row.Length > 4 ? (row[4] ?? "(unknown peer)") : "(unknown peer)";
                var local = row != null && row.Length > 2 ? (row[2] ?? "") : "";
                var remote = row != null && row.Length > 3 ? (row[3] ?? "") : "";
                var message = row != null && row.Length > 5 ? (row[5] ?? "") : "";

                if (_policy == MismatchPolicy.WarnOnly)
                {
                    Log.LogWarning($"[Summoner] parity mismatch (kind={kind}, peer={peer}, local={local}, remote={remote}, detail={message}) -- " +
                                    "OnParityMismatch=WarnOnly: logged only, merges continue unchanged.");
                    return;
                }

                var first = !Blocked;
                Blocked = true;
                if (!first) return; // one banner per process; the flag is already latched.

                Log.LogError(
                    "==================================================================\n" +
                    "  Summoner PARITY MISMATCH -- NO FURTHER MERGES THIS SESSION\n" +
                    "==================================================================\n" +
                   $"  kind   : {kind}\n" +
                   $"  peer   : {peer}\n" +
                   $"  local  : {local}\n" +
                   $"  remote : {remote}\n" +
                   $"  detail : {message}\n" +
                    "------------------------------------------------------------------\n" +
                   $"  [Multiplayer] OnParityMismatch = {_policy} (SPEC.md §5/§9.5).\n" +
                    "  No further FollowerPacks content will be merged into Configs this\n" +
                    "  session. Content already merged before this mismatch was detected\n" +
                    "  stays merged -- an already-hired follower's config is save data, not\n" +
                    "  something safe to rip out mid-session (see the doc comment on\n" +
                    "  OnParityMismatchRow).\n" +
                    "  FIX: make every peer's FollowerPacks/ folder byte-identical, then\n" +
                    "  restart the game.\n" +
                    "==================================================================");
            }
            catch (Exception ex)
            {
                // A throw here would be swallowed and isolated by DevKit anyway, but fail closed:
                // if we cannot even parse the row, assume the worst and block future merges.
                Blocked = true;
                Log.LogError($"[Summoner] ParityFailed callback threw; failing CLOSED (no further merges): {ex}");
            }
        }

        private static IEnumerable<string> Roots(string defaultRoot)
        {
            yield return defaultRoot;
            var extra = AdditionalRoots.Value;
            if (string.IsNullOrWhiteSpace(extra)) yield break;
            foreach (var raw in extra.Split(','))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }

        private static void LogFinding(Finding f)
        {
            switch (f.Severity)
            {
                case FindingSeverity.Error:
                    Log.LogError(f.ToString());
                    break;
                case FindingSeverity.Warn:
                    Log.LogWarning(f.ToString());
                    break;
                default:
                    if (VerboseLogging.Value) Log.LogDebug(f.ToString());
                    break;
            }
        }

        private void ApplyPatches()
        {
            var harmony = new Harmony(Guid);

            Patch(harmony, typeof(ConfigsHelper), "LoadConfigs",
                postfix: new HarmonyMethod(typeof(ConfigsMergePatches), nameof(ConfigsMergePatches.LoadConfigs_Postfix)));
            Patch(harmony, typeof(ConfigsHelper), "ReloadConfigs",
                postfix: new HarmonyMethod(typeof(ConfigsMergePatches), nameof(ConfigsMergePatches.ReloadConfigs_Postfix)));
        }

        private static void Patch(Harmony harmony, Type type, string method, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                Log.LogError($"Target NOT found: {type.Name}.{method} -- this feature is disabled (fail-safe).");
                return;
            }
            harmony.Patch(target, prefix: prefix, postfix: postfix);
            Log.LogInfo($"Target found: {type.Name}.{method}");
        }
    }
}
