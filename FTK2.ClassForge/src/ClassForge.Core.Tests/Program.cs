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
    // Scan the real data/ClassPacks dir (so this is still a true end-to-end read of the shipped
    // BALDURS fixture), but restrict "enabled" to just CF_PACK_BALDURS via the isPackEnabled knob
    // (same mechanism ClassForge.PackCheck uses). This keeps the assertion independent of how many
    // other packs happen to be enabled in the repo (e.g. CF_PACK_EOR_CLASSES) -- we assert on
    // CF_PACK_BALDURS's own presence/shape, not on a repo-wide enabled-pack count.
    baldursResult = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_BALDURS", StringComparison.Ordinal));

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

Test("CF_PACK_BALDURS: TRAIT_-prefixed trait ids produce zero CF_TRAIT_PREFIX warnings", () =>
{
    // The fixture's 6 trait ids were renamed CF_TRAIT_* -> TRAIT_CF_BALDURS_* so the native trait
    // substrate (CharacterHelper.GiveTrait/InventoryHelper.GetTraits, which keys on a literal
    // "TRAIT_" ConfigName prefix) can actually inject them. That should make the loader's
    // CF_TRAIT_PREFIX warning disappear for this pack entirely.
    var result = baldursResult!;
    var warnings = result.Findings.Where(f => f.Code == "CF_TRAIT_PREFIX").ToList();
    Assert(warnings.Count == 0, $"Expected 0 CF_TRAIT_PREFIX warnings (fixture trait ids are TRAIT_-prefixed), got {warnings.Count}: " + string.Join(" | ", warnings.Select(w => w.ToString())));
});

Test("CF_PACK_BALDURS: dataHash is sha256:-prefixed, stable 64-char hex (MP review B0)", () =>
{
    var result = baldursResult!;
    Assert(!string.IsNullOrEmpty(result.DataHash), "DataHash was empty.");
    Assert(result.DataHash.StartsWith("sha256:", StringComparison.Ordinal),
        "DataHash must be sha256:-prefixed so DevKit's ParityComparer treats it as well-formed (MP review B0), was: " + result.DataHash);
    var hex = result.DataHash.Substring("sha256:".Length);
    Assert(hex.Length == 64, $"DataHash hex portion should be 64 chars, was {hex.Length}.");
    Assert(hex.All(c => "0123456789abcdef".Contains(c)), "DataHash hex portion contained non-hex characters.");
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

Test("DataHasher: provenance.json is excluded (metadata, not gameplay data -- MP review M5)", () =>
{
    var fsWithout = new InMemoryFileSource();
    fsWithout.AddFile("root/CF_PACK_PROV/pack.json", MakePackJson("CF_PACK_PROV"));
    fsWithout.AddFile("root/CF_PACK_PROV/classes.json", "{\"A\":1}");

    var fsWith = new InMemoryFileSource();
    fsWith.AddFile("root/CF_PACK_PROV/pack.json", MakePackJson("CF_PACK_PROV"));
    fsWith.AddFile("root/CF_PACK_PROV/classes.json", "{\"A\":1}");
    fsWith.AddFile("root/CF_PACK_PROV/provenance.json", "{\"PackageVersion\":\"anything, doesn't matter\"}");

    var hashWithout = DataHasher.ComputeHash(fsWithout, new[] { new PackForHash("CF_PACK_PROV", "root/CF_PACK_PROV") });
    var hashWith = DataHasher.ComputeHash(fsWith, new[] { new PackForHash("CF_PACK_PROV", "root/CF_PACK_PROV") });

    AssertEqual(hashWithout, hashWith, "provenance.json is import/regeneration bookkeeping, not gameplay data, and must not affect the dataHash");
});

Test("DataHasher: PNG (binary, non-UTF8) bytes are hashed directly and stably (MP review M5)", () =>
{
    // Real PNG signature bytes, deliberately including 0x0D 0x0A (which a naive text-based CRLF
    // normalizer would corrupt) and 0xFF/0x89 (invalid as a UTF-8 lead byte on its own -- a
    // File.ReadAllText(..., Encoding.UTF8) decode would replace these with U+FFFD).
    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE, 0x01, 0x02, 0x03 };

    var fs1 = new InMemoryFileSource();
    fs1.AddFile("root/CF_PACK_PNG/pack.json", MakePackJson("CF_PACK_PNG"));
    fs1.AddBinaryFile("root/CF_PACK_PNG/icons/CF_X.png", pngBytes);

    var fs2 = new InMemoryFileSource();
    fs2.AddFile("root/CF_PACK_PNG/pack.json", MakePackJson("CF_PACK_PNG"));
    fs2.AddBinaryFile("root/CF_PACK_PNG/icons/CF_X.png", (byte[])pngBytes.Clone());

    var hash1 = DataHasher.ComputeHash(fs1, new[] { new PackForHash("CF_PACK_PNG", "root/CF_PACK_PNG") });
    var hash2 = DataHasher.ComputeHash(fs2, new[] { new PackForHash("CF_PACK_PNG", "root/CF_PACK_PNG") });
    AssertEqual(hash1, hash2, "Identical PNG bytes must hash identically across independent runs (byte-stable, no lossy text decode).");

    var changedBytes = (byte[])pngBytes.Clone();
    changedBytes[changedBytes.Length - 1] = 0x99;
    var fsChanged = new InMemoryFileSource();
    fsChanged.AddFile("root/CF_PACK_PNG/pack.json", MakePackJson("CF_PACK_PNG"));
    fsChanged.AddBinaryFile("root/CF_PACK_PNG/icons/CF_X.png", changedBytes);
    var hashChanged = DataHasher.ComputeHash(fsChanged, new[] { new PackForHash("CF_PACK_PNG", "root/CF_PACK_PNG") });
    Assert(hash1 != hashChanged, "Changing a single PNG byte must change the dataHash.");
});

Test("DataHasher: line-ending normalization is NOT applied to non-text (.png) extensions", () =>
{
    // If normalization incorrectly applied to .png, these two fixtures (one with a real CRLF pair, one
    // with it pre-collapsed to a single LF) would hash identically. They must NOT: a PNG's bytes are
    // opaque and 0x0D/0x0A are ordinary content bytes, not line endings.
    var withCrlf = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    var withLfOnly = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0A, 0x1A, 0x0A };

    var fsCrlf = new InMemoryFileSource();
    fsCrlf.AddFile("root/CF_PACK_BIN/pack.json", MakePackJson("CF_PACK_BIN"));
    fsCrlf.AddBinaryFile("root/CF_PACK_BIN/icons/x.png", withCrlf);

    var fsLf = new InMemoryFileSource();
    fsLf.AddFile("root/CF_PACK_BIN/pack.json", MakePackJson("CF_PACK_BIN"));
    fsLf.AddBinaryFile("root/CF_PACK_BIN/icons/x.png", withLfOnly);

    var hashCrlf = DataHasher.ComputeHash(fsCrlf, new[] { new PackForHash("CF_PACK_BIN", "root/CF_PACK_BIN") });
    var hashLf = DataHasher.ComputeHash(fsLf, new[] { new PackForHash("CF_PACK_BIN", "root/CF_PACK_BIN") });

    Assert(hashCrlf != hashLf,
        "A .png file's bytes must be hashed verbatim -- CRLF normalization must be scoped to the known-text extension set (.json/.md/.txt) only.");
});

// ---------------------------------------------------------------------
// 6. Parity-registration payload shape.
// ---------------------------------------------------------------------
Test("ParityRegistrationBuilder: payload includes pack ids + gameplay feature knobs (MP review B4)", () =>
{
    var result = baldursResult!;
    var payload = ParityRegistrationBuilder.Build(result, "ftk2mods.classforge", "0.1.0",
        enableRecipeEngine: true, enableTraitLoadoutInjection: false, enableStatModifiers: true);

    AssertEqual("ftk2mods.classforge", payload.Guid, "guid");
    AssertEqual("0.1.0", payload.Version, "version");
    AssertEqual(result.DataHash, payload.DataHash, "dataHash");
    Assert(payload.EnabledFeatures.SequenceEqual(new[]
    {
        "CF_PACK_BALDURS",
        "feature:EnableRecipeEngine=true",
        "feature:EnableStatModifiers=true",
        "feature:EnableTraitLoadoutInjection=false"
    }), $"enabledFeatures should include the pack id and all three gameplay feature knobs, got [{string.Join(",", payload.EnabledFeatures)}]");
    Assert(payload.EnabledFeatures.SequenceEqual(payload.EnabledFeatures.OrderBy(x => x, StringComparer.Ordinal)), "enabledFeatures must be sorted ordinally");
});

Test("ParityRegistrationBuilder: feature knob values flip the encoded string (MP review B4)", () =>
{
    var result = baldursResult!;
    var payload = ParityRegistrationBuilder.Build(result, "ftk2mods.classforge", "0.1.0",
        enableRecipeEngine: false, enableTraitLoadoutInjection: true);

    Assert(payload.EnabledFeatures.Contains("feature:EnableRecipeEngine=false"), "Expected feature:EnableRecipeEngine=false in " + string.Join(",", payload.EnabledFeatures));
    Assert(payload.EnabledFeatures.Contains("feature:EnableTraitLoadoutInjection=true"), "Expected feature:EnableTraitLoadoutInjection=true in " + string.Join(",", payload.EnabledFeatures));
    Assert(!payload.EnabledFeatures.Any(f => f.Contains("EnableClassSelectInjection")), "EnableClassSelectInjection is presentation-only and must NOT appear in enabledFeatures.");
    Assert(!payload.EnabledFeatures.Any(f => f.Contains("EnableIconFallback")), "EnableIconFallback is presentation-only and must NOT appear in enabledFeatures.");
});

// ---------------------------------------------------------------------
// 7. Merge-time adds-only enforcement against live (pre-existing) ids (MP review M0).
// ---------------------------------------------------------------------
Test("Merge: a pack entry colliding with a LIVE id is refused, not merged (MP review M0)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_LIVE/pack.json", MakePackJson("CF_PACK_LIVE"));
    fs.AddFile("root/CF_PACK_LIVE/classes.json",
        "{\"KNIGHT\":{\"Stats\":{},\"Value\":1},\"CF_NEW_CLASS\":{\"Stats\":{},\"Value\":2}}");

    var loader = new PackLoader();
    var liveIds = new LiveIdSets(new[] { "KNIGHT" }, null, null);
    var result = loader.Load(fs, new[] { "root" }, null, liveIds);

    AssertEqual(1, result.MergePlan.Characters.Count, "Only the non-colliding class should merge");
    AssertEqual("CF_NEW_CLASS", result.MergePlan.Characters.Single().Id, "KNIGHT must not appear in the merge plan");

    var refusals = result.Findings.Where(f => f.Code == "CF_LIVE_ID_COLLISION").ToList();
    Assert(refusals.Count == 1, $"Expected 1 CF_LIVE_ID_COLLISION finding, got {refusals.Count}");
    Assert(refusals[0].Severity == FindingSeverity.Error, "A live-id collision must be an Error (loud), not a Warning");
    Assert(refusals[0].Message.Contains("KNIGHT"), "Finding should name the refused id: " + refusals[0].Message);
});

Test("Merge: pack-vs-pack collisions still resolve last-pack-wins alongside an unrelated live-id set (MP review M0)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_ONE/pack.json", MakePackJson("CF_PACK_ONE", loadOrder: 0));
    fs.AddFile("root/CF_PACK_ONE/classes.json", "{\"CF_Y\":{\"Stats\":{},\"Value\":1}}");
    fs.AddFile("root/CF_PACK_TWO/pack.json", MakePackJson("CF_PACK_TWO", loadOrder: 10));
    fs.AddFile("root/CF_PACK_TWO/classes.json", "{\"CF_Y\":{\"Stats\":{},\"Value\":2}}");

    var loader = new PackLoader();
    var liveIds = new LiveIdSets(new[] { "SOME_OTHER_LIVE_ID" }, null, null);
    var result = loader.Load(fs, new[] { "root" }, null, liveIds);

    AssertEqual(1, result.MergePlan.Characters.Count, "Expected exactly one merged CF_Y entry");
    AssertEqual("CF_PACK_TWO", result.MergePlan.Characters.Single().SourcePackId, "Last-pack-wins must be unaffected by an unrelated live-id set");
    Assert(result.Findings.Any(f => f.Code == "CF_ID_COLLISION"), "The existing pack-vs-pack collision Finding must still fire");
    Assert(!result.Findings.Any(f => f.Code == "CF_LIVE_ID_COLLISION"), "No live-id collision should fire when neither id is live");
});

Test("Merge: a null live-id set (e.g. ClassForge.PackCheck) refuses nothing (MP review M0)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_NOLIVE/pack.json", MakePackJson("CF_PACK_NOLIVE"));
    fs.AddFile("root/CF_PACK_NOLIVE/classes.json", "{\"KNIGHT\":{\"Stats\":{},\"Value\":1}}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }); // liveIds omitted entirely

    AssertEqual(1, result.MergePlan.Characters.Count, "With no live-id set supplied, nothing is refused");
    Assert(!result.Findings.Any(f => f.Code == "CF_LIVE_ID_COLLISION"), "No live-id collision Finding should appear when liveIds is null");
});

// ---------------------------------------------------------------------
// 8. Encounter Modifiers (M-EM1): statuses.json + modifiers.json loader/validation/merge-plan (spec §12.5).
// ---------------------------------------------------------------------

Test("Encounter Modifiers: statuses.json + modifiers.json parse and merge cleanly (authored order preserved)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TESTMOD/pack.json", MakePackJson("CF_PACK_TESTMOD"));
    fs.AddFile("root/CF_PACK_TESTMOD/statuses.json", """
    {
      "STATUS_CF_TM_ONE": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": { "DEF": 1 }, "CustomStats": {} },
      "STATUS_CF_TM_TWO": { "Type": "DEBUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": { "DEF": -1 }, "CustomStats": {} }
    }
    """);
    fs.AddFile("root/CF_PACK_TESTMOD/modifiers.json", """
    {
      "SchemaVersion": "1.0",
      "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [
        { "Id": "ZETA", "Weight": 3, "Status": "STATUS_CF_TM_TWO" },
        { "Id": "ALPHA", "Weight": 7, "Status": "STATUS_CF_TM_ONE" }
      ]
    }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Severity == FindingSeverity.Error).ToList();
    Assert(errors.Count == 0, "Unexpected Error findings: " + string.Join(" | ", errors.Select(e => e.ToString())));

    AssertEqual(2, result.MergePlan.StatusEffects.Count, "StatusEffects count");
    Assert(result.MergePlan.StatusEffects.Any(m => m.Id == "STATUS_CF_TM_ONE"), "STATUS_CF_TM_ONE should be merged");
    Assert(result.MergePlan.StatusEffects.Any(m => m.Id == "STATUS_CF_TM_TWO"), "STATUS_CF_TM_TWO should be merged");

    AssertEqual(1, result.MergePlan.ModifierTables.Count, "One modifiers.json table expected");
    var table = result.MergePlan.ModifierTables[0];
    AssertEqual("SKILL_CF_TM_SELECT", table.SelectionRecipe, "Selection.Recipe");
    AssertEqual("1.0", table.SchemaVersion, "SchemaVersion");
    AssertEqual(2, table.Modifiers.Count, "Modifiers count");
    // Authored array order is load-bearing (spec §6.3 weighted-pick walk order) -- ZETA before ALPHA,
    // NOT re-sorted alphabetically or by weight.
    Assert(table.Modifiers.Select(m => m.Id).SequenceEqual(new[] { "ZETA", "ALPHA" }),
        "Modifiers authored order must be preserved, got: " + string.Join(",", table.Modifiers.Select(m => m.Id)));
});

Test("Encounter Modifiers: a non-STATUS_CF_-prefixed status id produces a CF_STATUS_ID_PREFIX warning but still merges", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_PREFIX/pack.json", MakePackJson("CF_PACK_TM_PREFIX"));
    fs.AddFile("root/CF_PACK_TM_PREFIX/statuses.json", """
    { "MYSTATUS_BAD": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var warnings = result.Findings.Where(f => f.Code == "CF_STATUS_ID_PREFIX").ToList();
    Assert(warnings.Count == 1, $"Expected 1 CF_STATUS_ID_PREFIX warning, got {warnings.Count}");
    Assert(warnings[0].Severity == FindingSeverity.Warning, "Id-convention mismatch must be a Warning, not an Error (mirrors CF_TRAIT_PREFIX)");
    Assert(result.MergePlan.StatusEffects.Any(m => m.Id == "MYSTATUS_BAD"), "The status must still merge despite the naming warning");
});

Test("Encounter Modifiers: an unknown Type is an Error and the whole status entry is dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_BADTYPE/pack.json", MakePackJson("CF_PACK_TM_BADTYPE"));
    fs.AddFile("root/CF_PACK_TM_BADTYPE/statuses.json", """
    { "STATUS_CF_BAD_TYPE": { "Type": "NOT_A_REAL_TYPE", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_STATUS_TYPE_UNKNOWN").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_STATUS_TYPE_UNKNOWN error, got {errors.Count}");
    Assert(errors[0].Severity == FindingSeverity.Error, "Unresolvable Type must be an Error");
    Assert(!result.MergePlan.StatusEffects.Any(m => m.Id == "STATUS_CF_BAD_TYPE"), "A status with an unresolvable Type must not merge");
});

Test("Encounter Modifiers: a Passives entry that resolves to neither a same-pack recipe nor SKILL_ is dropped, the status entry survives", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_PASSIVE/pack.json", MakePackJson("CF_PACK_TM_PASSIVE"));
    fs.AddFile("root/CF_PACK_TM_PASSIVE/statuses.json", """
    { "STATUS_CF_TM_PASSIVE": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": ["BOGUS_NOT_A_SKILL"], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_STATUS_PASSIVE_UNRESOLVED").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_STATUS_PASSIVE_UNRESOLVED error, got {errors.Count}");
    var op = result.MergePlan.StatusEffects.SingleOrDefault(m => m.Id == "STATUS_CF_TM_PASSIVE");
    Assert(op != null, "The status entry itself must survive (only the bad Passives entry is dropped)");
    AssertEqual(0, op!.Value.GetStringArray("Passives").Count, "The unresolved Passives entry must be dropped from the array");
});

Test("Encounter Modifiers: M0 -- a statuses.json entry colliding with a live StatusEffects id is refused", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_LIVE/pack.json", MakePackJson("CF_PACK_TM_LIVE"));
    fs.AddFile("root/CF_PACK_TM_LIVE/statuses.json", """
    { "STATUS_CF_LIVE_TEST": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var liveIds = new LiveIdSets(null, null, null, new[] { "STATUS_CF_LIVE_TEST" });
    var result = new PackLoader().Load(fs, new[] { "root" }, null, liveIds);

    AssertEqual(0, result.MergePlan.StatusEffects.Count, "The live-colliding status must be refused, not merged");
    var refusals = result.Findings.Where(f => f.Code == "CF_LIVE_ID_COLLISION").ToList();
    Assert(refusals.Count == 1, $"Expected 1 CF_LIVE_ID_COLLISION finding, got {refusals.Count}");
    Assert(refusals[0].Severity == FindingSeverity.Error, "A live-id collision must be an Error");
    Assert(refusals[0].Message.Contains("StatusEffect"), "Finding should label the category: " + refusals[0].Message);
});

Test("Encounter Modifiers: a modifiers.json Status ref that resolves nowhere is a dangling-ref Error and the entry is dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_DANGLE/pack.json", MakePackJson("CF_PACK_TM_DANGLE"));
    fs.AddFile("root/CF_PACK_TM_DANGLE/statuses.json", "{}");
    fs.AddFile("root/CF_PACK_TM_DANGLE/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [ { "Id": "GHOST", "Weight": 1, "Status": "STATUS_CF_DOES_NOT_EXIST_ANYWHERE" } ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_MODIFIER_STATUS_DANGLING").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_MODIFIER_STATUS_DANGLING error, got {errors.Count}");
    Assert(errors[0].Severity == FindingSeverity.Error, "A dangling Status ref must be an Error");
    var table = result.MergePlan.ModifierTables.Single();
    Assert(!table.Modifiers.Any(m => m.Id == "GHOST"), "A modifier with a dangling Status ref must be dropped");
});

Test("Encounter Modifiers: a modifiers.json Status ref matching a plausible vanilla status name is deferred (Info), not an Error", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_VANILLA/pack.json", MakePackJson("CF_PACK_TM_VANILLA"));
    fs.AddFile("root/CF_PACK_TM_VANILLA/statuses.json", "{}");
    fs.AddFile("root/CF_PACK_TM_VANILLA/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [ { "Id": "VANILLA_CURSE", "Weight": 1, "Status": "CURSE" } ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    Assert(!result.Findings.Any(f => f.Code == "CF_MODIFIER_STATUS_DANGLING"), "A vanilla-plausible ref must not be flagged dangling");
    var deferred = result.Findings.Where(f => f.Code == "CF_MODIFIER_STATUS_VANILLA_DEFERRED").ToList();
    Assert(deferred.Count == 1, $"Expected 1 CF_MODIFIER_STATUS_VANILLA_DEFERRED note, got {deferred.Count}");
    Assert(deferred[0].Severity == FindingSeverity.Info, "The deferred vanilla-id check is Info-level, not blocking");
    var table = result.MergePlan.ModifierTables.Single();
    Assert(table.Modifiers.Any(m => m.Id == "VANILLA_CURSE"), "The modifier still parses through (resolution just deferred to the Plugin, M-EM2)");
});

Test("Encounter Modifiers: duplicate Modifier Id is an Error and only the first occurrence is kept", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_DUPE/pack.json", MakePackJson("CF_PACK_TM_DUPE"));
    fs.AddFile("root/CF_PACK_TM_DUPE/statuses.json", """
    { "STATUS_CF_TM_DUPE": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);
    fs.AddFile("root/CF_PACK_TM_DUPE/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [
        { "Id": "DUPE", "Weight": 1, "Status": "STATUS_CF_TM_DUPE" },
        { "Id": "DUPE", "Weight": 2, "Status": "STATUS_CF_TM_DUPE" }
      ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_MODIFIER_DUP_ID").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_MODIFIER_DUP_ID error, got {errors.Count}");
    Assert(errors[0].Severity == FindingSeverity.Error, "Duplicate Modifier Id must be an Error");
    var table = result.MergePlan.ModifierTables.Single();
    AssertEqual(1, table.Modifiers.Count(m => m.Id == "DUPE"), "Only the first occurrence of a duplicate Id should be kept");
    AssertEqual(1, table.Modifiers.Single(m => m.Id == "DUPE").Weight, "The kept occurrence should be the first one authored (Weight 1)");
});

Test("Encounter Modifiers: Weight < 1 is an Error and the entry is dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_WEIGHT/pack.json", MakePackJson("CF_PACK_TM_WEIGHT"));
    fs.AddFile("root/CF_PACK_TM_WEIGHT/statuses.json", """
    { "STATUS_CF_TM_WEIGHT": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);
    fs.AddFile("root/CF_PACK_TM_WEIGHT/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [
        { "Id": "ZEROWEIGHT", "Weight": 0, "Status": "STATUS_CF_TM_WEIGHT" },
        { "Id": "NEGWEIGHT", "Weight": -3, "Status": "STATUS_CF_TM_WEIGHT" },
        { "Id": "OKWEIGHT", "Weight": 1, "Status": "STATUS_CF_TM_WEIGHT" }
      ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_MODIFIER_WEIGHT_RANGE").ToList();
    Assert(errors.Count == 2, $"Expected 2 CF_MODIFIER_WEIGHT_RANGE errors (0 and -3), got {errors.Count}");
    Assert(errors.All(e => e.Severity == FindingSeverity.Error), "Weight < 1 must be an Error");
    var table = result.MergePlan.ModifierTables.Single();
    AssertEqual(1, table.Modifiers.Count, "Only the Weight>=1 entry should survive");
    AssertEqual("OKWEIGHT", table.Modifiers.Single().Id, "The surviving entry should be OKWEIGHT");
});

Test("Encounter Modifiers: dataHash changes when a modifiers.json Weight changes (and when statuses.json changes)", () =>
{
    var fsBase = new InMemoryFileSource();
    fsBase.AddFile("root/CF_PACK_TM_HASH/pack.json", MakePackJson("CF_PACK_TM_HASH"));
    fsBase.AddFile("root/CF_PACK_TM_HASH/statuses.json",
        "{\"STATUS_CF_TM_HASH\":{\"Type\":\"BUFF\",\"Stats\":{}}}");
    fsBase.AddFile("root/CF_PACK_TM_HASH/modifiers.json",
        "{\"SchemaVersion\":\"1.0\",\"Selection\":{\"Recipe\":\"SKILL_CF_TM_SELECT\"}," +
        "\"Modifiers\":[{\"Id\":\"ONE\",\"Weight\":5,\"Status\":\"STATUS_CF_TM_HASH\"}]}");

    var fsWeightChanged = new InMemoryFileSource();
    fsWeightChanged.AddFile("root/CF_PACK_TM_HASH/pack.json", MakePackJson("CF_PACK_TM_HASH"));
    fsWeightChanged.AddFile("root/CF_PACK_TM_HASH/statuses.json",
        "{\"STATUS_CF_TM_HASH\":{\"Type\":\"BUFF\",\"Stats\":{}}}");
    fsWeightChanged.AddFile("root/CF_PACK_TM_HASH/modifiers.json",
        "{\"SchemaVersion\":\"1.0\",\"Selection\":{\"Recipe\":\"SKILL_CF_TM_SELECT\"}," +
        "\"Modifiers\":[{\"Id\":\"ONE\",\"Weight\":6,\"Status\":\"STATUS_CF_TM_HASH\"}]}"); // only the Weight differs

    var fsStatusChanged = new InMemoryFileSource();
    fsStatusChanged.AddFile("root/CF_PACK_TM_HASH/pack.json", MakePackJson("CF_PACK_TM_HASH"));
    fsStatusChanged.AddFile("root/CF_PACK_TM_HASH/statuses.json",
        "{\"STATUS_CF_TM_HASH\":{\"Type\":\"BUFF\",\"Stats\":{\"DEF\":1}}}"); // statuses.json content differs
    fsStatusChanged.AddFile("root/CF_PACK_TM_HASH/modifiers.json",
        "{\"SchemaVersion\":\"1.0\",\"Selection\":{\"Recipe\":\"SKILL_CF_TM_SELECT\"}," +
        "\"Modifiers\":[{\"Id\":\"ONE\",\"Weight\":5,\"Status\":\"STATUS_CF_TM_HASH\"}]}");

    var baseResult = new PackLoader().Load(fsBase, new[] { "root" });
    var weightChangedResult = new PackLoader().Load(fsWeightChanged, new[] { "root" });
    var statusChangedResult = new PackLoader().Load(fsStatusChanged, new[] { "root" });

    Assert(!string.IsNullOrEmpty(baseResult.DataHash), "Base dataHash must not be empty");
    Assert(baseResult.DataHash != weightChangedResult.DataHash,
        "Changing a modifiers.json Weight must change the pack dataHash (both files fold into the existing hash-every-file rule automatically)");
    Assert(baseResult.DataHash != statusChangedResult.DataHash,
        "Changing statuses.json content must change the pack dataHash");
});

// ---------------------------------------------------------------------
// 9. CF_PACK_ENCOUNTER_MODIFIERS end-to-end fixture (real pack on disk), mirrors the CF_PACK_BALDURS test.
// ---------------------------------------------------------------------
var encModDir = Path.Combine(classPacksDir, "CF_PACK_ENCOUNTER_MODIFIERS");

Test("CF_PACK_ENCOUNTER_MODIFIERS: loads cleanly with zero Error findings", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_ENCOUNTER_MODIFIERS", StringComparison.Ordinal));

    var errors = result.Findings.Where(f => f.Severity == FindingSeverity.Error).ToList();
    Assert(errors.Count == 0, "Unexpected Error findings: " + string.Join(" | ", errors.Select(e => e.ToString())));
    AssertEqual(1, result.EnabledOrderedPacks.Count, $"Expected 1 enabled pack, got {result.EnabledOrderedPacks.Count}");
    AssertEqual("CF_PACK_ENCOUNTER_MODIFIERS", result.EnabledOrderedPacks[0].Id, "Enabled pack id mismatch");

    AssertEqual(10, result.MergePlan.StatusEffects.Count, "Expected all 10 shipped statuses to merge");
    AssertEqual(1, result.MergePlan.ModifierTables.Count, "Expected exactly one modifiers.json table");
    var table = result.MergePlan.ModifierTables[0];
    AssertEqual(10, table.Modifiers.Count, "Expected all 10 shipped modifier rows");
    AssertEqual(85, table.Modifiers.Sum(m => m.Weight), "Shipped weights must sum to 85 (spec §1.1)");

    // Authored order (spec §3.2, load-bearing for the weighted-pick walk, §6.3).
    var expectedOrder = new[]
    {
        "ARMORED", "RESISTANT", "FRENZIED", "SWIFT", "VETERAN", "WEALTHY", "CURSED",
        "REGENERATING", "GLASSCANNON", "TREASUREGUARDED"
    };
    Assert(table.Modifiers.Select(m => m.Id).SequenceEqual(expectedOrder),
        "Modifiers authored order must match spec §3.2 exactly, got: " + string.Join(",", table.Modifiers.Select(m => m.Id)));

    Assert(!result.Findings.Any(f => f.Code == "CF_STATUS_ID_PREFIX"), "All shipped status ids should be STATUS_CF_-prefixed");
    Assert(!result.Findings.Any(f => f.Code == "CF_MODIFIER_STATUS_DANGLING"), "Every shipped Status ref should resolve within the pack");
    Assert(!result.Findings.Any(f => f.Code == "CF_STATUS_PASSIVE_UNRESOLVED"), "CURSED/REGENERATING Passives must resolve against the pack's own skillrecipes.json");
});

// ---------------------------------------------------------------------
// 10. SkillDisplay.SelectCustomSkillRows — which class Passives get a custom UI row in the
//     character-customization panel (the game itself only renders eSkills-parseable passives).
// ---------------------------------------------------------------------
Test("SkillDisplay: selects non-vanilla passives with display names, preserving Passives order", () =>
{
    var passives = new[] { "SKILL_BLACKHOLE", "SKILL_CF_WARRIOR_RAGE_BUILD", "SKILL_REFOCUS", "SKILL_CF_WARRIOR_RAGE_CONSUME" };
    var vanilla = new HashSet<string>(new[] { "SKILL_BLACKHOLE", "SKILL_REFOCUS" }, StringComparer.Ordinal);

    var rows = SkillDisplay.SelectCustomSkillRows(passives, vanilla.Contains, _ => true);

    Assert(rows.SequenceEqual(new[] { "SKILL_CF_WARRIOR_RAGE_BUILD", "SKILL_CF_WARRIOR_RAGE_CONSUME" }),
        "Expected the two custom passives in authored order, got: " + string.Join(",", rows));
});

Test("SkillDisplay: excludes vanilla passives even when they have display names", () =>
{
    var rows = SkillDisplay.SelectCustomSkillRows(
        new[] { "SKILL_BLACKHOLE" }, _ => true, _ => true);
    AssertEqual(0, rows.Count, "A vanilla-renderable passive must never get a duplicate custom row");
});

Test("SkillDisplay: excludes custom passives that have no display name (nothing legible to render)", () =>
{
    var rows = SkillDisplay.SelectCustomSkillRows(
        new[] { "SKILL_CF_NO_LOC" }, _ => false, _ => false);
    AssertEqual(0, rows.Count, "A custom passive without a name key would render as a raw id — must be skipped");
});

Test("SkillDisplay: null or empty passives yield an empty list", () =>
{
    AssertEqual(0, SkillDisplay.SelectCustomSkillRows(null, _ => false, _ => true).Count, "null passives");
    AssertEqual(0, SkillDisplay.SelectCustomSkillRows(Array.Empty<string>(), _ => false, _ => true).Count, "empty passives");
});

Test("SkillDisplay: duplicate passive ids produce a single row", () =>
{
    var rows = SkillDisplay.SelectCustomSkillRows(
        new[] { "SKILL_CF_X", "SKILL_CF_X" }, _ => false, _ => true);
    AssertEqual(1, rows.Count, "Duplicate passive entries must not produce duplicate UI rows");
});

Test("SkillDisplay: real CF_PACK_EOR_CLASSES data — every class's custom passives are selectable", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_EOR_CLASSES", StringComparison.Ordinal));

    var loc = result.MergePlan.Localization;
    int classesWithRows = 0;
    foreach (var op in result.MergePlan.Characters)
    {
        var passives = op.Value.Get("Passives").AsArray.Select(n => n.AsString).ToList();
        var rows = SkillDisplay.SelectCustomSkillRows(passives, p => !p.StartsWith("SKILL_CF_", StringComparison.Ordinal), loc.ContainsKey);
        if (rows.Count > 0) classesWithRows++;
        foreach (var row in rows)
            Assert(loc.ContainsKey("UI_ENCYCLOPEDIA_" + row), $"{op.Id}: row {row} has no UI_ENCYCLOPEDIA_ description key");
    }
    AssertEqual(28, classesWithRows, "28 of 31 EOR classes ship a custom signature skill (DUELIST/PALADIN/THIEF are parked)");
});

// ---------------------------------------------------------------------
// 11. visualfallbacks.json — pack item id -> donor Thing id, the equipment-visual remap surface
//     (task #8: pack items without dEquipmentPrefab records NRE CharacterVisualHelper.VisualReEquip).
// ---------------------------------------------------------------------
Test("VisualFallbacks: parse + merge round-trip; non-string values are an Error and dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_VF/pack.json", MakePackJson("CF_PACK_VF"));
    fs.AddFile("root/CF_PACK_VF/items.json", "{\"CF_ITEM_A\":{\"Class\":\"WHIP\"}}");
    fs.AddFile("root/CF_PACK_VF/visualfallbacks.json",
        "{\"CF_ITEM_A\":\"WHIP_MILITIA_BASIC_00\",\"CF_ITEM_BAD\":42}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }, id => true);

    AssertEqual("WHIP_MILITIA_BASIC_00", result.MergePlan.VisualFallbacks["CF_ITEM_A"], "fallback merged");
    Assert(!result.MergePlan.VisualFallbacks.ContainsKey("CF_ITEM_BAD"), "non-string value dropped");
    Assert(result.Findings.Any(f => f.Code == "CF_VISUALFALLBACK_SHAPE" && f.Severity == FindingSeverity.Error),
        "non-string value produced an Error finding");
});

Test("VisualFallbacks: last pack wins across packs", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_VF1/pack.json", MakePackJson("CF_PACK_VF1", loadOrder: 0));
    fs.AddFile("root/CF_PACK_VF1/visualfallbacks.json", "{\"CF_ITEM_A\":\"DONOR_ONE\"}");
    fs.AddFile("root/CF_PACK_VF2/pack.json", MakePackJson("CF_PACK_VF2", loadOrder: 1));
    fs.AddFile("root/CF_PACK_VF2/visualfallbacks.json", "{\"CF_ITEM_A\":\"DONOR_TWO\"}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }, id => true);
    AssertEqual("DONOR_TWO", result.MergePlan.VisualFallbacks["CF_ITEM_A"], "later pack wins");
});

Test("VisualFallbacks: donor resolvable via live ids or merged pack Things — else Warning", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_VF/pack.json", MakePackJson("CF_PACK_VF"));
    fs.AddFile("root/CF_PACK_VF/items.json", "{\"CF_ITEM_A\":{\"Class\":\"WHIP\"},\"CF_ITEM_B\":{\"Class\":\"AXE\"},\"CF_ITEM_C\":{\"Class\":\"BOW\"}}");
    fs.AddFile("root/CF_PACK_VF/visualfallbacks.json",
        "{\"CF_ITEM_A\":\"LIVE_DONOR\",\"CF_ITEM_B\":\"CF_ITEM_A\",\"CF_ITEM_C\":\"NO_SUCH_DONOR\"}");

    var live = new LiveIdSets(null, new HashSet<string>(StringComparer.Ordinal) { "LIVE_DONOR" }, null, null);
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }, id => true, live);

    var dangling = result.Findings.Where(f => f.Code == "CF_VISUALFALLBACK_DANGLING").ToList();
    AssertEqual(1, dangling.Count, "exactly the unresolvable donor warns");
    Assert(dangling[0].Message.Contains("NO_SUCH_DONOR"), "warning names the missing donor");
    Assert(dangling.All(f => f.Severity == FindingSeverity.Warning), "dangling donor is a Warning, not an Error");
});

Test("CF_PACK_EOR_CLASSES: visualfallbacks cover exactly the 31 starter items", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_EOR_CLASSES", StringComparison.Ordinal));

    var itemsPath = Path.Combine(classPacksDir, "CF_PACK_EOR_CLASSES", "items.json");
    var itemIds = JsonParser.Parse(File.ReadAllText(itemsPath)).AsObjectMembers.Select(m => m.Key)
        .OrderBy(x => x, StringComparer.Ordinal).ToList();

    var fallbackKeys = result.MergePlan.VisualFallbacks.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
    AssertEqual(31, fallbackKeys.Count, "31 starter fallbacks");
    Assert(fallbackKeys.SequenceEqual(itemIds), "fallback keys must be exactly the pack's items.json ids");
    Assert(result.MergePlan.VisualFallbacks.Values.All(v => !string.IsNullOrEmpty(v)), "every donor non-empty");
    Assert(!result.Findings.Any(f => f.Code == "CF_VISUALFALLBACK_DANGLING"),
        "in-pack run: donors are live-game ids, but with no live set supplied they must not warn either");
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
