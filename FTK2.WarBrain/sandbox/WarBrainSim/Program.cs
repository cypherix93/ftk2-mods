using System.Text;
using System.Text.Json;
using WarBrain.Core;
using WarBrainSim;

// ------------------------------------------------------------------
// WarBrain sandbox experiment runner.
// Usage: dotnet run -c Release [battles-per-cell] [outfile]
// ------------------------------------------------------------------

int battles = args.Length > 0 ? int.Parse(args[0]) : 2000;
string outFile = args.Length > 1 ? args[1] : "results.md";

// ---- load the shipped data-driven profiles ----
string dataDir = FindDataDir();
var jsonOpts = new JsonSerializerOptions
{
    IncludeFields = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
};

var profiles = new Dictionary<string, BrainProfile>();
foreach (var f in Directory.GetFiles(Path.Combine(dataDir, "Profiles"), "*.brain.json"))
{
    var p = JsonSerializer.Deserialize<BrainProfile>(File.ReadAllText(f), jsonOpts)!;
    profiles[p.Id] = p;
}
var doctrines = new Dictionary<string, DoctrineConfig>();
foreach (var f in Directory.GetFiles(Path.Combine(dataDir, "Doctrines"), "*.doctrine.json"))
{
    var d = JsonSerializer.Deserialize<DoctrineConfig>(File.ReadAllText(f), jsonOpts)!;
    doctrines[d.Id] = d;
}
Console.WriteLine($"Loaded {profiles.Count} profiles, {doctrines.Count} doctrines from {dataDir}");

var report = new StringBuilder();
report.AppendLine("# WarBrain sandbox results");
report.AppendLine();
report.AppendLine($"Battles per cell: {battles}. Player side: scripted experienced party (focus-fire, kill-secure, proactive heals).");
report.AppendLine("Metrics are means per battle. `EDmg` = enemy damage dealt to players (post-mitigation HP), `Downs` = players reduced to 0,");
report.AppendLine("`Wipe%` = full party kill, `Waste%` = enemy turns that changed nothing.");
report.AppendLine();

string[] comps = ["BALANCED", "TURTLE", "GLASS_CANNON", "DODGE"];
string[] squads = ["BRUTES", "MIXED", "WARBAND"];

// ---- experiment 1: vanilla vs WarBrain (shipped profiles), lvl 7 enemies ----
report.AppendLine("## 1. Vanilla AI vs WarBrain (decision changes only, identical stats, enemy lvl 7)");
report.AppendLine();
foreach (var squad in squads)
{
    report.AppendLine($"### Squad: {squad}");
    report.AppendLine();
    report.AppendLine("| Party | AI | EDmg | Downs | Wipe% | Waste% | Kills/battle | Turns | Focus/turn | Pierce | Crits |");
    report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (var comp in comps)
    {
        foreach (var (label, factory) in AiVariants(profiles, doctrines))
        {
            var m = RunCell(comp, squad, 7, battles, factory, null);
            report.AppendLine($"| {comp} | {label} | {m.EDmg:F1} | {m.Downs:F2} | {m.Wipe:P1} | {m.Waste:P1} | {m.Kills:F2} | {m.Turns:F1} | {m.FocusPerTurn:F2} | {m.Pierce:F2} | {m.Crits:F2} |");
        }
    }
    report.AppendLine();
}

// ---- experiment 2: knob sweep (intelligence / temperature / mistake) ----
report.AppendLine("## 2. Difficulty knob sweep (WarBrain, BALANCED party, MIXED squad, lvl 7)");
report.AppendLine();
report.AppendLine("| GlobalIntelligence | GlobalTempMult | MistakeAdd | EDmg | Downs | Wipe% | Waste% |");
report.AppendLine("|---|---|---|---|---|---|---|");
foreach (var (intel, temp, mistake) in new (decimal, decimal, decimal)[]
         { (0.5m, 3.0m, 0.15m), (0.75m, 2.0m, 0.10m), (1.0m, 1.0m, 0.0m), (1.25m, 0.5m, 0.0m), (1.5m, 0.0m, 0.0m) })
{
    var knobs = new GlobalDifficultyKnobs
    {
        GlobalIntelligenceScalar = intel,
        GlobalTemperatureMultiplier = temp,
        GlobalMistakeChanceAdd = mistake
    };
    var m = RunCell("BALANCED", "MIXED", 7, battles,
        (rng, hook) => MakeWarBrain(profiles, doctrines, knobs, hook), null);
    report.AppendLine($"| {intel} | {temp} | {mistake} | {m.EDmg:F1} | {m.Downs:F2} | {m.Wipe:P1} | {m.Waste:P1} |");
}
report.AppendLine();

// ---- experiment 3: stat scaling layer (vanilla AI vs WarBrain, scaled enemies) ----
report.AppendLine("## 3. Enemy stat-scaling layer (BALANCED party, MIXED squad)");
report.AppendLine();
report.AppendLine("Scaling: percentage multipliers on ATK/HP plus flat ACC add — the shape vanilla `EnemyStatMods`/`GetExtraLevelStats` already uses.");
report.AppendLine();
report.AppendLine("| Scaling | AI | EDmg | Downs | Wipe% | Waste% |");
report.AppendLine("|---|---|---|---|---|---|");
foreach (var (label, atkMult, hpMult, accAdd) in new (string, decimal, decimal, int)[]
         { ("none", 1.0m, 1.0m, 0), ("ATK+25%", 1.25m, 1.0m, 0), ("ATK+25% HP+25%", 1.25m, 1.25m, 0),
           ("ATK+25% HP+25% ACC+8", 1.25m, 1.25m, 8), ("ATK+50% HP+50% ACC+8", 1.5m, 1.5m, 8) })
{
    Action<SimActor> scale = e =>
    {
        e.Atk = (int)Math.Round(e.Atk * atkMult);
        e.MaxHp = (int)Math.Round(e.MaxHp * hpMult);
        e.Hp = e.MaxHp;
        e.RollStat += accAdd;
    };
    foreach (var (ai, factory) in new (string, Func<Random, ExperiencedPlayerPolicy, IAiPolicy>)[]
             { ("vanilla", (rng, hook) => new VanillaAi()),
               ("WarBrain", (rng, hook) => MakeWarBrain(profiles, doctrines, new GlobalDifficultyKnobs(), hook)) })
    {
        var m = RunCell("BALANCED", "MIXED", 7, battles, factory, scale);
        report.AppendLine($"| {label} | {ai} | {m.EDmg:F1} | {m.Downs:F2} | {m.Wipe:P1} | {m.Waste:P1} |");
    }
}
report.AppendLine();

// ---- experiment 4: focus policy isolation ----
report.AppendLine("## 4. Focus policy isolation (WarBrain otherwise identical, BALANCED party, MIXED squad, lvl 7)");
report.AppendLine();
report.AppendLine("| FocusPolicy | EDmg | Downs | Wipe% | Pierce/battle | Crits/battle |");
report.AppendLine("|---|---|---|---|---|---|");
foreach (var policy in new[] { "NONE", "VANILLA_RANDOM", "MAX", "SMART" })
{
    var patched = profiles.ToDictionary(kv => kv.Key, kv =>
    {
        var clone = JsonSerializer.Deserialize<BrainProfile>(JsonSerializer.Serialize(kv.Value, jsonOpts), jsonOpts)!;
        clone.FocusPolicy = policy;
        return clone;
    });
    var m = RunCell("BALANCED", "MIXED", 7, battles,
        (rng, hook) => MakeWarBrain(patched, doctrines, new GlobalDifficultyKnobs(), hook), null);
    report.AppendLine($"| {policy} | {m.EDmg:F1} | {m.Downs:F2} | {m.Wipe:P1} | {m.Pierce:F2} | {m.Crits:F2} |");
}
report.AppendLine();

File.WriteAllText(outFile, report.ToString());
Console.WriteLine($"Wrote {outFile}");

// ------------------------------------------------------------------

static IEnumerable<(string label, Func<Random, ExperiencedPlayerPolicy, IAiPolicy> factory)> AiVariants(
    Dictionary<string, BrainProfile> profiles, Dictionary<string, DoctrineConfig> doctrines)
{
    yield return ("vanilla", (rng, hook) => new VanillaAi());
    yield return ("WarBrain", (rng, hook) => MakeWarBrain(profiles, doctrines, new GlobalDifficultyKnobs(), hook));
}

static WarBrainAi MakeWarBrain(Dictionary<string, BrainProfile> profiles, Dictionary<string, DoctrineConfig> doctrines,
    GlobalDifficultyKnobs knobs, ExperiencedPlayerPolicy playerPolicy)
{
    var ai = new WarBrainAi { Profiles = profiles, Doctrines = doctrines, Knobs = knobs };
    playerPolicy.OnHeal = ai.OnPlayerHealObserved;
    return ai;
}

static Metrics RunCell(string comp, string squad, int lvl, int battles,
    Func<Random, ExperiencedPlayerPolicy, IAiPolicy> enemyAiFactory, Action<SimActor>? enemyScale)
{
    var agg = new Metrics();
    for (int i = 0; i < battles; i++)
    {
        var rng = new Random(1000 + i); // deterministic seeds — reproducible across runs
        var players = Scenarios.Party(comp);
        var enemies = Scenarios.Squad(squad, lvl);
        if (enemyScale != null) foreach (var e in enemies) enemyScale(e);

        var playerPolicy = new ExperiencedPlayerPolicy();
        var enemyAi = enemyAiFactory(rng, playerPolicy);
        if (enemyAi is WarBrainAi wb) wb.ResetBattleMemory();

        var sim = new CombatSim { Rng = rng, Players = players, Enemies = enemies, EnemyAi = enemyAi, PlayerAi = playerPolicy };
        var r = sim.Run();

        agg.EDmg += r.EnemyDamageDealt;
        agg.Downs += r.PlayerDowns;
        agg.Wipe += r.PartyWiped ? 1 : 0;
        agg.Waste += r.EnemyTurns > 0 ? (double)r.EnemyWastedTurns / r.EnemyTurns : 0;
        agg.Kills += r.EnemyKillsSecured;
        agg.Turns += r.Turns;
        agg.FocusPerTurn += r.EnemyTurns > 0 ? (double)r.EnemyFocusSpent / r.EnemyTurns : 0;
        agg.Pierce += r.EnemyPierceHits;
        agg.Crits += r.EnemyCrits;
    }
    agg.Scale(1.0 / battles);
    return agg;
}

static string FindDataDir()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8; i++)
    {
        var candidate = Path.Combine(dir, "data");
        if (Directory.Exists(Path.Combine(candidate, "Profiles"))) return candidate;
        var parent = Path.GetDirectoryName(dir);
        if (parent == null) break;
        dir = parent;
    }
    throw new DirectoryNotFoundException("Could not locate FTK2.WarBrain/data (looked upward from " + AppContext.BaseDirectory + ")");
}

class Metrics
{
    public double EDmg, Downs, Wipe, Waste, Kills, Turns, FocusPerTurn, Pierce, Crits;
    public void Scale(double f)
    {
        EDmg *= f; Downs *= f; Wipe *= f; Waste *= f; Kills *= f; Turns *= f; FocusPerTurn *= f; Pierce *= f; Crits *= f;
    }
}
