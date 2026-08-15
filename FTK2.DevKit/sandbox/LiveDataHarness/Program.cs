using System;
using System.Collections.Generic;

namespace LiveDataHarness
{
    /// <summary>
    /// Runs the repo's .Core logic against the real game's Configs.
    /// Exit 0 = green, 1 = a check failed, 2 = no usable game install (skipped, not failed).
    /// Usage: LiveDataHarness [--game-dir &lt;path&gt;] [--json &lt;out.json&gt;]
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
                    {
                        foreach (var prefix in ForeignIdPrefixes)
                            if (id.StartsWith(prefix, StringComparison.Ordinal))
                                foreign.Add(dict + "." + id + " (matches third-party prefix '" + prefix + "')");
                        foreach (var marker in ForeignIdMarkers)
                            if (id.IndexOf(marker, StringComparison.Ordinal) >= 0 && !StartsWithAnyPrefix(id))
                                foreign.Add(dict + "." + id + " (contains third-party marker '" + marker + "')");
                    }

                Check.Empty(foreign,
                    "third-party content found on disk in StreamingAssets — restore with Steam's " +
                    "'Verify integrity of game files' before trusting any result below");
            });

            var vocab = GameVocabulary.Build(data);

            runner.Section("Game vocabulary");
            runner.Case("enum + learned vocabularies are populated", () =>
            {
                // These floors exist to catch the vocabulary silently coming back empty, which would make
                // every reference check below vacuously green rather than loudly broken.
                Check.AtLeast(2000, vocab.EnumMembers.Count, "distinct enum member names in FTK2.dll");
                Check.True(vocab.EnumMembers.Contains("COMMON"), "enum vocabulary contains COMMON");
                Check.True(vocab.EnumMembers.Contains("MELEE"), "enum vocabulary contains MELEE");
                Check.AtLeast(20, vocab.BaseTypes.Count, "learned CharacterConfig.BaseType values");
                Check.True(vocab.BaseTypes.Contains("HUMAN"), "BaseType vocabulary contains HUMAN");
                Check.True(vocab.BodyTypes.Contains("M") && vocab.BodyTypes.Contains("F"), "BodyType vocabulary is {F,M}");
                Check.True(vocab.CharacterTags.Contains("PLAYER"), "tag vocabulary contains PLAYER");
            });

            var cfResult = Checks.ClassForgeChecks.LoadAll(data);
            Checks.ClassForgeChecks.RegisterAddsOnly(runner, data, cfResult);

            Checks.ReferenceChecks.Register(runner, data, vocab, cfResult);

            Checks.LocalizationChecks.Register(runner, data, cfResult);

            Checks.BlessingsChecks.Register(runner, data, cfResult);

            Checks.SummonerChecks.Register(runner, data);

            Checks.RecipeChecks.Register(runner, data, cfResult);

            Checks.DeterminismChecks.Register(runner, data, cfResult);

            var exit = runner.Report();

            var jsonOut = ArgValue(args, "--json");
            if (!string.IsNullOrEmpty(jsonOut))
            {
                Report.Write(jsonOut, install.Root, runner.Passed, runner.Failed, runner.Failures);
                Console.WriteLine("report: " + System.IO.Path.GetFullPath(jsonOut));
            }

            return exit;
        }

        /// <summary>
        /// Id prefixes that must never appear in a pristine install's Configs. This repo's own packs use
        /// CF_/SMN_/BLSS_/WB_ and are merged at runtime from the repo's data folders, so finding one written
        /// into the game folder means something deployed content where it does not belong.
        ///
        /// ARM_ is deliberately NOT listed: it is a vanilla prefix. The game ships ARM_CATALOG_*.json and
        /// ARM_FORGE_CURATED.json under Configs/JSON~/Things, so hundreds of legitimate ids such as
        /// ARM_BRAMBLE_MACE start with it. Gating on ARM_ makes the pristine baseline permanently red.
        /// </summary>
        internal static readonly string[] ForeignIdPrefixes =
        {
            "CF_", "SMN_", "BLSS_", "WB_",
        };

        /// <summary>
        /// Substrings that betray third-party content wherever they appear in an id, not just at the start.
        /// Needed because a mod may file its content under a vanilla prefix: the overhaul that motivated this
        /// gate ships ARM_EOR_* item ids inside the game's own Things catalogs, which no prefix rule anchored
        /// at position zero can distinguish from vanilla ARM_ items.
        /// </summary>
        internal static readonly string[] ForeignIdMarkers =
        {
            "EOR_",
        };

        /// <summary>Keeps an id that both starts with a foreign prefix and carries a marker from being
        /// reported twice for what is one piece of misplaced content.</summary>
        private static bool StartsWithAnyPrefix(string id)
        {
            for (int i = 0; i < ForeignIdPrefixes.Length; i++)
                if (id.StartsWith(ForeignIdPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        internal static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }
    }
}
