using System;

namespace LiveDataHarness
{
    /// <summary>
    /// Runs the repo's .Core logic against the real game's Configs, with no game running.
    ///
    ///   dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release -- --json out.json
    ///
    /// Exit codes: 0 = every check passed · 1 = at least one check failed · 2 = no loadable game
    /// install (skipped, not failed).
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string gameDir = ArgValue(args, "--game-dir");

            GameInstall install = GameInstall.Resolve(gameDir);
            if (install == null)
            {
                Console.WriteLine("LiveDataHarness: SKIPPED — no For The King II install found.");
                Console.WriteLine("  Pass --game-dir \"X:\\...\\steamapps\\common\\For The King II\".");
                return 2;
            }

            Console.WriteLine("LiveDataHarness");
            Console.WriteLine("  game: " + install.Root);
            Console.WriteLine();
            Console.WriteLine("  Why this exists: it catches, offline and in seconds, the failure class that");
            Console.WriteLine("  repeatedly ships INVISIBLE content in this repo —");
            Console.WriteLine("    * a CharacterConfig id the game does not define (an entity that is alive and");
            Console.WriteLine("      targetable but renders as nothing or a generic humanoid);");
            Console.WriteLine("    * a status id that is not a real Configs.StatusEffects key (silently does");
            Console.WriteLine("      nothing) — e.g. \"CURSE\", which is an eStatusEffectTypes member, not a key;");
            Console.WriteLine("    * a custom status id not prefixed by a vanilla eStatusEffectsGroups member");
            Console.WriteLine("      (invisible in the UI);");
            Console.WriteLine("    * an id that collides with live content and is refused at merge");
            Console.WriteLine("      (CF_LIVE_ID_COLLISION), so an edited file is never actually loaded.");
            Console.WriteLine("  Each of those is an ERROR here, named with its pack, key and id.");

            GameData data;
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                data = GameData.Load(install);
                sw.Stop();
                Console.WriteLine("  ConfigsHelper.LoadConfigs OK in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine("LiveDataHarness: SKIPPED — could not load Configs: " + ex.Message);
                return 2;
            }

            CheckRunner runner = new CheckRunner();

            runner.Section("Live config sanity");
            runner.Case("live Configs are populated", delegate
            {
                Check.AtLeast(1800, data.Count("Things"), "Configs.Things");
                Check.AtLeast(2000, data.Count("Characters"), "Configs.Characters");
                Check.AtLeast(900, data.Count("Abilities"), "Configs.Abilities");
                Check.AtLeast(50, data.Count("SkillConfigs"), "Configs.SkillConfigs");
                Check.AtLeast(150, data.Count("StatusEffects"), "Configs.StatusEffects");
                Check.AtLeast(40, data.Count("Followers"), "Configs.Followers");
                Check.True(data.Lang("en") != null, "Configs.Langs contains 'en'");
                Check.AtLeast(9000, data.Lang("en").Count, "Configs.Langs['en'] key count");
            });

            runner.Case("negative control: an invented dictionary key is absent", delegate
            {
                // If Ids() ever returned a set that answered true to everything, every adds-only and
                // reference check downstream would pass vacuously. Prove it does not.
                Check.True(!data.Ids("Characters").Contains("LDH_NEVER_SHIPPED_CHARACTER"),
                    "Configs.Characters must not contain an invented id");
                Check.True(!data.Ids("Things").Contains("LDH_NEVER_SHIPPED_THING"),
                    "Configs.Things must not contain an invented id");
            });

            GameVocabulary vocab = GameVocabulary.Build(data);

            runner.Section("Game vocabulary");
            runner.Case("enum + learned vocabularies are populated", delegate
            {
                Check.AtLeast(2000, vocab.EnumMembers.Count, "distinct enum member names in FTK2.dll");
                Check.True(vocab.EnumMembers.Contains("COMMON"), "enum vocabulary contains COMMON");
                Check.True(vocab.EnumMembers.Contains("MELEE"), "enum vocabulary contains MELEE");
                Check.AtLeast(25, vocab.BaseTypes.Count, "learned CharacterConfig.BaseType values (32 measured)");
                Check.True(vocab.BaseTypes.Contains("HUMAN"), "BaseType vocabulary contains HUMAN");
                Check.True(vocab.BaseTypes.Contains("SKELETON"), "BaseType vocabulary contains SKELETON");
                Check.True(vocab.BodyTypes.Contains("M") && vocab.BodyTypes.Contains("F"), "BodyType vocabulary is {F,M}");
                Check.AtLeast(400, vocab.CharacterTags.Count, "learned character tags (483 measured)");
                Check.True(vocab.CharacterTags.Contains("PLAYER"), "character tag vocabulary contains PLAYER");
                Check.AtLeast(400, vocab.ThingTags.Count, "learned Thing tags (496 measured)");
                Check.AtLeast(1, vocab.StatusTypes.Count, "learned StatusEffect types");
                Check.AtLeast(1, vocab.Expansions.Count, "learned CharacterConfig.Expansion values");
                Check.True(vocab.Rarities.Contains("COMMON"), "Rarity vocabulary contains COMMON");
            });

            runner.Case("negative control: vocabularies reject an invented value", delegate
            {
                // A vocabulary built from an empty or always-true source would wave every value through,
                // making the whole reference checker vacuous.
                Check.True(!vocab.BaseTypes.Contains("LDH_NOT_A_BASETYPE"), "BaseTypes must reject an invented value");
                Check.True(!vocab.BodyTypes.Contains("Q"), "BodyTypes must reject an invented value");
                Check.True(!vocab.EnumMembers.Contains("LDH_NOT_AN_ENUM_MEMBER"), "EnumMembers must reject an invented value");
                Check.True(!vocab.CharacterTags.Contains("LDH_NOT_A_TAG"), "CharacterTags must reject an invented value");
            });

            InstallProvenance.Register(runner, data);

            AuthoredContent authored = AuthoredContent.Load(data);
            Checks.AddsOnlyChecks.Register(runner, data, authored);
            Checks.ReferenceChecks.Register(runner, data, vocab, authored);
            Checks.LocalizationChecks.Register(runner, data, authored);
            Checks.BlessingsChecks.Register(runner, data, authored);
            Checks.SummonerChecks.Register(runner, data);
            Checks.InventoryChecks.Register(runner, authored);
            Checks.RecipeChecks.Register(runner, data, authored);
            Checks.DeterminismChecks.Register(runner, data, authored);

            // Source scan, not a data check: no rendering path in ClassForge.Plugin may delete a
            // combatant from the replicated combat roster. See RosterMutationChecks for why that is a
            // desync and not a cosmetic bug.
            Checks.RosterMutationChecks.Register(runner);

            int exit = runner.Report();

            string jsonOut = ArgValue(args, "--json");
            if (!string.IsNullOrEmpty(jsonOut))
            {
                Report.Write(jsonOut, runner.Passed, runner.Failed, runner.Failures, runner.Warnings);
                Console.WriteLine("report: " + System.IO.Path.GetFullPath(jsonOut));
            }

            return exit;
        }

        /// <summary>Returns the value following <paramref name="name"/>, or null.</summary>
        public static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }
    }
}
