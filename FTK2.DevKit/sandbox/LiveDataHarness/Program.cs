using System;
using System.Collections.Generic;

namespace LiveDataHarness
{
    /// <summary>
    /// Runs the repo's .Core logic against the real game's Configs.
    /// Exit 0 = green, 1 = a check failed, 2 = no usable game install (skipped, not failed).
    /// Usage: LiveDataHarness [--game-dir &lt;path&gt;]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string gameDir = ArgValue(args, "--game-dir");

            var install = GameInstall.Resolve(gameDir);
            if (install == null)
            {
                Console.WriteLine("LiveDataHarness: SKIPPED — no For The King II install found.");
                Console.WriteLine("  Pass --game-dir \"X:\\...\\steamapps\\common\\For The King II\".");
                return 2;
            }

            Console.WriteLine("LiveDataHarness");
            Console.WriteLine("  game: " + install.Root);

            GameData data;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                data = GameData.Load(install);
                sw.Stop();
                Console.WriteLine("  ConfigsHelper.LoadConfigs OK in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                // A game that is present but unloadable is still "cannot measure", not "content is broken".
                Console.WriteLine("LiveDataHarness: SKIPPED — could not load Configs: " + ex.Message);
                return 2;
            }

            var runner = new CheckRunner();

            runner.Section("Live config sanity");
            runner.Case("live Configs are populated", () =>
            {
                // Floors, not equality: a game update legitimately adds content, and asserting exact counts
                // would turn every patch into a red harness. A dictionary collapsing far below these floors
                // means the load silently half-failed, which is what this is actually watching for.
                Check.AtLeast(1500, data.Count("Things"), "Configs.Things");
                Check.AtLeast(1800, data.Count("Characters"), "Configs.Characters");
                Check.AtLeast(900, data.Count("Abilities"), "Configs.Abilities");
                Check.AtLeast(50, data.Count("SkillConfigs"), "Configs.SkillConfigs");
                Check.AtLeast(150, data.Count("StatusEffects"), "Configs.StatusEffects");
                Check.True(data.Lang("en") != null, "Configs.Langs contains 'en'");
            });

            runner.Case("install is a pristine baseline (no third-party content on disk)", () =>
            {
                // Third-party mods can write content straight into the game's shipped JSON rather than
                // patching at runtime, which silently redefines what "a live id" means for every adds-only
                // and reference check downstream — a colliding id would be masked, and a legitimately new id
                // could be reported as a collision. That makes contamination a hard gate rather than a
                // warning. The remedy is Steam's "Verify integrity of game files".
                var foreign = new List<string>();
                foreach (var dict in new[] { "Characters", "Things", "Abilities", "SkillConfigs", "StatusEffects", "Followers" })
                    foreach (var id in data.Ids(dict))
                        foreach (var prefix in ForeignIdPrefixes)
                            if (id.StartsWith(prefix, StringComparison.Ordinal))
                                foreign.Add(dict + "." + id + " (matches third-party prefix '" + prefix + "')");

                Check.Empty(foreign,
                    "third-party content found on disk in StreamingAssets — restore with Steam's " +
                    "'Verify integrity of game files' before trusting any result below");
            });

            return runner.Report();
        }

        /// <summary>
        /// Id prefixes that must never appear in a pristine install's Configs. Verified absent from every
        /// vanilla dictionary, so none of these can produce a false positive. This repo's own packs use the
        /// CF_/SMN_/BLSS_/ARM_/WB_ prefixes and are merged at runtime from the repo's data folders — they
        /// are listed here precisely because finding one written into the game folder means something
        /// deployed content where it does not belong, which is also contamination.
        /// </summary>
        internal static readonly string[] ForeignIdPrefixes =
        {
            "EOR_", "CF_", "SMN_", "BLSS_", "ARM_", "WB_",
        };

        internal static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }
    }
}
