using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;
using ClassForge.Core.Json;
using ClassForge.Core.Tests;

int passed = 0, failed = 0;

void Test(string name, Action body)
{
    try
    {
        body();
        Console.WriteLine($"PASS: {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {name} -- {ex.Message}");
        failed++;
    }
}

void Assert(bool cond, string message)
{
    if (!cond) throw new Exception(message);
}

void AssertEqual<T>(T expected, T actual, string message)
{
    if (!Equals(expected, actual))
        throw new Exception($"{message} (expected '{expected}', actual '{actual}')");
}

string MakePackJson(string id, int loadOrder = 0, string[]? dependencies = null, bool enabled = true) =>
    "{" +
    $"\"id\":\"{id}\"," +
    $"\"name\":\"{id}\"," +
    "\"version\":\"1.0.0\"," +
    $"\"loadOrder\":{loadOrder}," +
    $"\"dependencies\":[{string.Join(",", (dependencies ?? Array.Empty<string>()).Select(d => $"\"{d}\""))}]," +
    $"\"enabled\":{(enabled ? "true" : "false")}" +
    "}";

// ---------------------------------------------------------------------
// 1. Discovery determinism: shuffled directory-listing order -> identical resolved order.
// ---------------------------------------------------------------------
Test("Discovery determinism: shuffled directory order yields identical sorted result", () =>
{
    var ids = new[] { "CF_PACK_ALPHA", "CF_PACK_BETA", "CF_PACK_GAMMA" };

    var fsInsertionOrder = new InMemoryFileSource();
    foreach (var id in ids)
        fsInsertionOrder.AddFile($"root/{id}/pack.json", MakePackJson(id));

    var fsShuffled = new InMemoryFileSource();
    foreach (var id in ids)
        fsShuffled.AddFile($"root/{id}/pack.json", MakePackJson(id));
    fsShuffled.ShuffleDirectoryOrder(new[] { "root/CF_PACK_GAMMA", "root/CF_PACK_ALPHA", "root/CF_PACK_BETA" });

    var findingsA = new List<Finding>();
    var findingsB = new List<Finding>();
    var a = PackDiscovery.Discover(fsInsertionOrder, new[] { "root" }, findingsA).Select(p => p.Manifest.Id).ToList();
    var b = PackDiscovery.Discover(fsShuffled, new[] { "root" }, findingsB).Select(p => p.Manifest.Id).ToList();

    Assert(a.SequenceEqual(b), $"Discovery order differed between insertion-order and shuffled file sources: [{string.Join(",", a)}] vs [{string.Join(",", b)}]");
    Assert(a.SequenceEqual(ids.OrderBy(x => x, StringComparer.Ordinal)), "Discovery result was not alphabetically sorted by pack id.");
});

// ---------------------------------------------------------------------
// 2. Topo sort / cycle / missing-dep cases.
// ---------------------------------------------------------------------
Test("Ordering: dependencies force order regardless of discovery order", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_A", loadOrder: 0, deps: new[] { "CF_PACK_B" }),
        MakeDiscovered("CF_PACK_B", loadOrder: 0, deps: new[] { "CF_PACK_C" }),
        MakeDiscovered("CF_PACK_C", loadOrder: 0),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_C", "CF_PACK_B", "CF_PACK_A" }), $"Expected C,B,A got {string.Join(",", ordered)}");
});

Test("Ordering: loadOrder ties break alphabetically by id", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_E", loadOrder: 5),
        MakeDiscovered("CF_PACK_D", loadOrder: 5),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_D", "CF_PACK_E" }), $"Expected D,E got {string.Join(",", ordered)}");
});

Test("Ordering: missing dependency skips the dependent pack and logs a Finding", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_F", loadOrder: 0, deps: new[] { "CF_PACK_MISSING" }),
        MakeDiscovered("CF_PACK_OK", loadOrder: 0),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_OK" }), $"Expected only CF_PACK_OK got {string.Join(",", ordered)}");
    Assert(findings.Any(f => f.Code == "CF_MISSING_DEP" && f.PackId == "CF_PACK_F"), "Expected a CF_MISSING_DEP Finding for CF_PACK_F.");
});

Test("Ordering: dependency cycle skips every pack in the cycle and logs Findings", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_G", loadOrder: 0, deps: new[] { "CF_PACK_H" }),
        MakeDiscovered("CF_PACK_H", loadOrder: 0, deps: new[] { "CF_PACK_G" }),
        MakeDiscovered("CF_PACK_OK2", loadOrder: 0),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_OK2" }), $"Expected only CF_PACK_OK2 got {string.Join(",", ordered)}");
    Assert(findings.Count(f => f.Code == "CF_CYCLE") == 2, $"Expected 2 CF_CYCLE findings, got {findings.Count(f => f.Code == "CF_CYCLE")}");
});

// ---------------------------------------------------------------------
// 3. CF_PACK_BALDURS end-to-end parse + merge plan against the real fixture on disk.
// ---------------------------------------------------------------------
var classPacksDir = FindClassPacksDir();
var baldursDir = Path.Combine(classPacksDir, "CF_PACK_BALDURS");
Console.WriteLine($"Fixture root: {baldursDir}");

PackLoadResult? baldursResult = null;

Test("CF_PACK_BALDURS: loads cleanly with zero Error findings", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    baldursResult = loader.Load(fs, new[] { classPacksDir });

    var errors = baldursResult.Findings.Where(f => f.Severity == FindingSeverity.Error).ToList();
    Assert(errors.Count == 0, "Unexpected Error findings: " + string.Join(" | ", errors.Select(e => e.ToString())));
    Assert(baldursResult.EnabledOrderedPacks.Count == 1, $"Expected 1 enabled pack, got {baldursResult.EnabledOrderedPacks.Count}");
    AssertEqual("CF_PACK_BALDURS", baldursResult.EnabledOrderedPacks[0].Id, "Enabled pack id mismatch");
});

Test("CF_PACK_BALDURS: merge plan counts match independently re-parsed raw JSON", () =>
{
    var result = baldursResult!;
    int RawObjectCount(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(baldursDir, fileName));
        return JsonParser.Parse(text).AsObjectMembers.Count;
    }

    var classCount = RawObjectCount("classes.json");
    var traitCount = RawObjectCount("traits.json");
    var abilityCount = RawObjectCount("abilities.json");
    var itemCount = RawObjectCount("items.json");
    var locCount = RawObjectCount(Path.Combine("localization", "en.json"));

    Console.WriteLine($"  classes={classCount} traits={traitCount} abilities={abilityCount} items={itemCount} localization={locCount}");

    AssertEqual(classCount, result.MergePlan.Characters.Count, "Characters count");
    AssertEqual(traitCount + itemCount, result.MergePlan.Things.Count, "Things (traits+items) count");
    AssertEqual(abilityCount, result.MergePlan.Abilities.Count, "Abilities count");
    AssertEqual(locCount, result.MergePlan.Localization.Count, "Localization key count");
    AssertEqual(traitCount, result.MergePlan.TraitIds.Count, "TraitIds count (every traits.json entry has Class:\"TRAIT\")");

    var iconFiles = Directory.GetFiles(Path.Combine(baldursDir, "icons"), "*.png").Length;
    var portraitFiles = Directory.GetFiles(Path.Combine(baldursDir, "portraits"), "*.png").Length;
    AssertEqual(iconFiles, result.MergePlan.Icons.Count, "Icons count");
    AssertEqual(portraitFiles, result.MergePlan.Portraits.Count, "Portraits count");

    Console.WriteLine($"  BALDURS merge plan: classes={result.MergePlan.Characters.Count} things={result.MergePlan.Things.Count} " +
                       $"abilities={result.MergePlan.Abilities.Count} traitIds={result.MergePlan.TraitIds.Count} " +
                       $"loc={result.MergePlan.Localization.Count} icons={result.MergePlan.Icons.Count} portraits={result.MergePlan.Portraits.Count}");
});

Test("CF_PACK_BALDURS: non-TRAIT_-prefixed trait ids produce a Warning, not an Error (M1 still merges them)", () =>
{
    var result = baldursResult!;
    var warnings = result.Findings.Where(f => f.Code == "CF_TRAIT_PREFIX").ToList();
    Assert(warnings.Count == 6, $"Expected 6 CF_TRAIT_PREFIX warnings (fixture uses CF_TRAIT_* ids), got {warnings.Count}");
    Assert(warnings.All(f => f.Severity == FindingSeverity.Warning), "CF_TRAIT_PREFIX findings must be Warning severity, not Error (fail-safe: still merges as inert data for M1).");
});

Test("CF_PACK_BALDURS: dataHash is a stable 64-char hex SHA-256", () =>
{
    var result = baldursResult!;
    Assert(!string.IsNullOrEmpty(result.DataHash), "DataHash was empty.");
    Assert(result.DataHash.Length == 64, $"DataHash should be 64 hex chars, was {result.DataHash.Length}.");
    Assert(result.DataHash.All(c => "0123456789abcdef".Contains(c)), "DataHash contained non-hex characters.");
});

// ---------------------------------------------------------------------
// 4. Collision: id defined by two packs -> last (later load order) pack wins, both ids logged.
// ---------------------------------------------------------------------
Test("Merge: id collision across packs resolves last-pack-wins and logs both pack ids", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_ONE/pack.json", MakePackJson("CF_PACK_ONE", loadOrder: 0));
    fs.AddFile("root/CF_PACK_ONE/classes.json", "{\"CF_X\":{\"Stats\":{},\"Value\":1}}");
    fs.AddFile("root/CF_PACK_TWO/pack.json", MakePackJson("CF_PACK_TWO", loadOrder: 10));
    fs.AddFile("root/CF_PACK_TWO/classes.json", "{\"CF_X\":{\"Stats\":{},\"Value\":2}}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" });

    AssertEqual(1, result.MergePlan.Characters.Count, "Expected exactly one merged CF_X entry");
    var op = result.MergePlan.Characters.Single();
    AssertEqual("CF_PACK_TWO", op.SourcePackId, "Later-loadOrder pack should win the collision");
    AssertEqual(2.0, op.Value.GetNumber("Value"), "Winning value should be CF_PACK_TWO's Value");

    var collisionFindings = result.Findings.Where(f => f.Code == "CF_ID_COLLISION").ToList();
    Assert(collisionFindings.Count == 1, $"Expected 1 CF_ID_COLLISION finding, got {collisionFindings.Count}");
    Assert(collisionFindings[0].Message.Contains("CF_PACK_ONE") && collisionFindings[0].Message.Contains("CF_PACK_TWO"),
        "Collision finding should name both pack ids: " + collisionFindings[0].Message);
});

// ---------------------------------------------------------------------
// 5. dataHash stability: CRLF/LF invariance, localization exclusion, sensitivity to real changes.
// ---------------------------------------------------------------------
Test("DataHasher: CRLF vs LF line endings produce the same hash", () =>
{
    var fsLf = new InMemoryFileSource();
    fsLf.AddFile("root/CF_PACK_X/pack.json", MakePackJson("CF_PACK_X"));
    fsLf.AddFile("root/CF_PACK_X/classes.json", "{\n  \"A\":1\n}");

    var fsCrlf = new InMemoryFileSource();
    fsCrlf.AddFile("root/CF_PACK_X/pack.json", MakePackJson("CF_PACK_X"));
    fsCrlf.AddFile("root/CF_PACK_X/classes.json", "{\r\n  \"A\":1\r\n}");

    var hashLf = DataHasher.ComputeHash(fsLf, new[] { new PackForHash("CF_PACK_X", "root/CF_PACK_X") });
    var hashCrlf = DataHasher.ComputeHash(fsCrlf, new[] { new PackForHash("CF_PACK_X", "root/CF_PACK_X") });

    AssertEqual(hashLf, hashCrlf, "CRLF and LF variants of the same content should hash identically");
});

Test("DataHasher: localization/** is excluded from the hash", () =>
{
    var fsWithoutLoc = new InMemoryFileSource();
    fsWithoutLoc.AddFile("root/CF_PACK_Y/pack.json", MakePackJson("CF_PACK_Y"));
    fsWithoutLoc.AddFile("root/CF_PACK_Y/classes.json", "{\"A\":1}");

    var fsWithLoc = new InMemoryFileSource();
    fsWithLoc.AddFile("root/CF_PACK_Y/pack.json", MakePackJson("CF_PACK_Y"));
    fsWithLoc.AddFile("root/CF_PACK_Y/classes.json", "{\"A\":1}");
    fsWithLoc.AddFile("root/CF_PACK_Y/localization/en.json", "{\"A\":\"anything, doesn't matter\"}");

    var hashWithout = DataHasher.ComputeHash(fsWithoutLoc, new[] { new PackForHash("CF_PACK_Y", "root/CF_PACK_Y") });
    var hashWith = DataHasher.ComputeHash(fsWithLoc, new[] { new PackForHash("CF_PACK_Y", "root/CF_PACK_Y") });

    AssertEqual(hashWithout, hashWith, "Adding a localization file should not change the dataHash");
});

Test("DataHasher: a real (non-localization) content change does change the hash", () =>
{
    var fs1 = new InMemoryFileSource();
    fs1.AddFile("root/CF_PACK_Z/pack.json", MakePackJson("CF_PACK_Z"));
    fs1.AddFile("root/CF_PACK_Z/classes.json", "{\"A\":1}");

    var fs2 = new InMemoryFileSource();
    fs2.AddFile("root/CF_PACK_Z/pack.json", MakePackJson("CF_PACK_Z"));
    fs2.AddFile("root/CF_PACK_Z/classes.json", "{\"A\":2}");

    var hash1 = DataHasher.ComputeHash(fs1, new[] { new PackForHash("CF_PACK_Z", "root/CF_PACK_Z") });
    var hash2 = DataHasher.ComputeHash(fs2, new[] { new PackForHash("CF_PACK_Z", "root/CF_PACK_Z") });

    Assert(hash1 != hash2, "Changing classes.json content should change the dataHash");
});

// ---------------------------------------------------------------------
// 6. Parity-registration payload shape.
// ---------------------------------------------------------------------
Test("ParityRegistrationBuilder: payload shape matches (guid, version, dataHash, enabledFeatures)", () =>
{
    var result = baldursResult!;
    var payload = ParityRegistrationBuilder.Build(result, "ftk2mods.classforge", "0.1.0");

    AssertEqual("ftk2mods.classforge", payload.Guid, "guid");
    AssertEqual("0.1.0", payload.Version, "version");
    AssertEqual(result.DataHash, payload.DataHash, "dataHash");
    Assert(payload.EnabledFeatures.SequenceEqual(new[] { "CF_PACK_BALDURS" }), $"enabledFeatures should be [CF_PACK_BALDURS], got [{string.Join(",", payload.EnabledFeatures)}]");
    Assert(payload.EnabledFeatures.SequenceEqual(payload.EnabledFeatures.OrderBy(x => x, StringComparer.Ordinal)), "enabledFeatures must be sorted ordinally");
});

// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------
static DiscoveredPack MakeDiscovered(string id, int loadOrder = 0, string[]? deps = null)
{
    var manifest = new PackManifest
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        LoadOrder = loadOrder,
        Dependencies = (deps ?? Array.Empty<string>()).ToList(),
        Enabled = true,
        RootDir = $"root/{id}"
    };
    return new DiscoveredPack(manifest, manifest.RootDir);
}

static string FindClassPacksDir()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 12; i++)
    {
        var candidate = Path.Combine(dir, "FTK2.ClassForge", "data", "ClassPacks");
        if (Directory.Exists(candidate)) return candidate;
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
    throw new DirectoryNotFoundException($"Could not locate FTK2.ClassForge/data/ClassPacks by walking up from {AppContext.BaseDirectory}");
}
